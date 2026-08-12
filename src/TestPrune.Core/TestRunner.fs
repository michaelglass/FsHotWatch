module TestPrune.TestRunner

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

/// Result of running a test process, with stdout and stderr kept separate.
type TestResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

/// Type alias for process runner functions.
type ProcessRunner = string -> string -> TestResult

/// Default target framework moniker — update when upgrading .NET SDK.
let defaultTfm = "net10.0"

/// Find the test DLL path for a given test project.
let findTestDll (projectPath: string) : string =
    let projDir = Path.GetDirectoryName(projectPath)
    let projName = Path.GetFileNameWithoutExtension(projectPath)
    Path.Combine(projDir, "bin", "Debug", defaultTfm, $"%s{projName}.dll")

/// Unwrap a completed redirected-output read. Internal (not private) so the
/// faulted-task path can be unit-tested: a faulted task must surface its
/// ORIGINAL exception (e.g. IOException), not the AggregateException wrapper
/// that `.Result` would rethrow.
let internal awaitOutput (t: Task<string>) : string = t.GetAwaiter().GetResult()

/// Exit code returned when a test run is killed for exceeding its timeout.
/// Follows the POSIX `timeout(1)` convention (124 = "the command timed out"), so a
/// wedged run surfaces as a clear, distinct non-zero exit rather than a silent hang.
[<Literal>]
let timeoutExitCode = 124

/// Default hang-detector timeout for a single test-project run, in milliseconds.
///
/// This is deliberately generous (30 minutes): a legitimate suite may run for many
/// minutes, and a too-tight bound that kills a slow-but-valid run is a worse bug than
/// the silent hang it replaces. Override per-run with the
/// `TESTPRUNE_TEST_RUN_TIMEOUT_MS` environment variable.
let defaultTestRunTimeoutMs = 30 * 60 * 1000

/// Resolve the effective test-run timeout: the `TESTPRUNE_TEST_RUN_TIMEOUT_MS`
/// environment variable when it parses to a positive integer, otherwise
/// `defaultTestRunTimeoutMs`.
let internal resolveTestRunTimeoutMs () : int =
    match Environment.GetEnvironmentVariable "TESTPRUNE_TEST_RUN_TIMEOUT_MS" with
    | null
    | "" -> defaultTestRunTimeoutMs
    | raw ->
        match Int32.TryParse raw with
        | true, ms when ms > 0 -> ms
        | _ -> defaultTestRunTimeoutMs

/// Post-exit output-drain bound, in milliseconds.
///
/// This is a WEDGE DETECTOR, not a perf knob. `WaitForExit` returns the instant the
/// DIRECT child exits, but the redirected stdout/stderr read tasks complete only once
/// EVERY process that inherited the write handle (an MSBuild worker, VBCSCompiler, or a
/// testhost grandchild) has closed it. Draining an already-exited process is normally
/// instant, so 30s behaves exactly as an unbounded read on every healthy run and only
/// fires on the grandchild-pipe wedge that this bound exists to break.
[<Literal>]
let drainOutputTimeoutMs = 30_000

/// Bound the post-exit drain of a process's redirected stdout/stderr.
///
/// Call this AFTER a successful `WaitForExit`, passing the two `ReadToEndAsync` tasks. An
/// unbounded `.Result` / `awaitOutput` here wedges: a grandchild that outlived the direct
/// child keeps the stdout/stderr pipe open, so the read never completes and the caller
/// blocks forever, silently (observed as a 16h hang). You cannot kill your way out — the
/// direct child has already exited, so there is no live process-tree root to signal;
/// give-up-with-diagnostic IS the fix.
///
/// If the drain completes within `drainTimeoutMs` the captured stdout/stderr are returned
/// exactly as an unbounded read would (each task's ORIGINAL exception surfaces via
/// `awaitOutput`, not the `AggregateException` `.Result` would wrap). If it expires, a
/// diagnostic naming the command and the bound goes to stderr and the output captured so
/// far is returned — never blocking.
///
/// On timeout the returned text is only a PARTIAL capture, so a caller that treats the
/// drained text as AUTHORITATIVE DATA (rather than as diagnostic output alongside an
/// exit-code verdict) MUST branch on `Completed` and refuse the partial read. Otherwise a
/// wedged drain masquerades as an empty-but-complete read — a truncated `jj diff` read as
/// "no changed files", i.e. silent under-selection. See `TestPrune.Program.runBoundedDiff`.
[<Struct>]
type internal DrainResult =
    { Completed: bool
      Stdout: string
      Stderr: string }

let internal drainOutputWithin
    (drainTimeoutMs: int)
    (commandLabel: string)
    (stdoutTask: Task<string>)
    (stderrTask: Task<string>)
    : DrainResult =
    let drained = Task.WhenAll(stdoutTask, stderrTask)

    // WaitAny (not `drained.Wait`) so a genuinely faulted read does not throw an
    // AggregateException here — awaitOutput below rethrows each task's ORIGINAL exception.
    if Task.WaitAny([| (drained :> Task) |], drainTimeoutMs) >= 0 then
        { Completed = true
          Stdout = awaitOutput stdoutTask
          Stderr = awaitOutput stderrTask }
    else
        eprintfn
            $"output drain exceeded %d{drainTimeoutMs / 1000}s after `%s{commandLabel}` exited \u2014 a grandchild process is still holding the stdout/stderr pipe open; abandoning the drain with the output captured so far"

        let capturedSoFar (t: Task<string>) =
            if t.IsCompletedSuccessfully then t.Result else ""

        { Completed = false
          Stdout = capturedSoFar stdoutTask
          Stderr = capturedSoFar stderrTask }

/// Run a process bounded by `timeoutMs`, capturing stdout/stderr separately.
///
/// The bound is a HANG DETECTOR, not a run-cap: a real suite may legitimately run for
/// many minutes (hence the generous default), so on a normal run this behaves exactly
/// as an unbounded wait. But a runner WEDGED on a test DLL would otherwise block the
/// CLI forever with no diagnostic; on expiry the process tree is killed, a diagnostic
/// is written to stderr, and a result carrying `timeoutExitCode` is returned instead of
/// hanging.
let internal runProcessWith (timeoutMs: int) (fileName: string) (arguments: string) : TestResult =
    let psi = ProcessStartInfo(fileName, arguments)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = Process.Start(psi)
    let sw = Stopwatch.StartNew()

    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    let completed = proc.WaitForExit(timeoutMs)
    sw.Stop()

    if not completed then
        proc.Kill(entireProcessTree = true)

        let diagnostic =
            $"test run exceeded %d{timeoutMs / 1000}s \u2014 the runner appears wedged on `%s{fileName} %s{arguments}`; killed the process tree"

        eprintfn "%s" diagnostic

        { ExitCode = timeoutExitCode
          Stdout = ""
          Stderr = diagnostic }
    else
        // Verdict here is the EXIT CODE, so a drain-timeout keeps the same exit-code path with
        // the partial output + the helper's diagnostic \u2014 it must NOT fail a genuinely-passing
        // run. (Only the diff path, where the drained TEXT is authoritative, maps timeout to a
        // hard failure; see runBoundedDiff.)
        let drain =
            drainOutputWithin drainOutputTimeoutMs $"%s{fileName} %s{arguments}" stdoutTask stderrTask

        eprintfn $"[%s{fileName} %s{arguments}] \u2192 exit %d{proc.ExitCode} in %.1f{sw.Elapsed.TotalSeconds}s"

        { ExitCode = proc.ExitCode
          Stdout = drain.Stdout
          Stderr = drain.Stderr }

/// Default process runner: bounds the wait with the resolved (env-overridable) timeout.
let private runProcess (fileName: string) (arguments: string) : TestResult =
    runProcessWith (resolveTestRunTimeoutMs ()) fileName arguments

/// Build the filter arguments string from a list of test class names.
let buildFilterArgs (testClasses: string list) : string =
    testClasses
    |> List.map (fun cls -> $"--filter-class \"%s{cls}\"")
    |> String.concat " "

/// Normalize exit codes: xUnit v3 returns 8 when zero tests match — treat as success.
let normalizeExitCode (exitCode: int) : int = if exitCode = 8 then 0 else exitCode

/// Run all tests in a project using the given process runner.
let runAllTestsWith (runner: ProcessRunner) (projectDll: string) : TestResult =
    runner "dotnet" $"exec \"%s{projectDll}\""

/// Run all tests in a project.
let runAllTests (projectDll: string) : TestResult = runAllTestsWith runProcess projectDll

/// Run only tests in the specified classes using the given process runner.
let runFilteredTestsWith (runner: ProcessRunner) (projectDll: string) (testClasses: string list) : TestResult =
    let filterArgs = buildFilterArgs testClasses
    let result = runner "dotnet" $"exec \"%s{projectDll}\" %s{filterArgs}"

    { result with
        ExitCode = normalizeExitCode result.ExitCode }

/// Run only tests in the specified classes.
/// Uses multiple --filter-class flags (ORed by xUnit v3 MTP).
let runFilteredTests (projectDll: string) (testClasses: string list) : TestResult =
    runFilteredTestsWith runProcess projectDll testClasses

/// Discover test projects by scanning for .fsproj files with xunit references.
/// Walks via SafeWalk: scoping to tests/ is NOT sufficient protection against
/// symlink cycles (tests/*/bin holds Playwright's Nix-store browser symlinks),
/// so the walk must refuse to traverse symlinked dirs. See TestPrune.SafeWalk.
let discoverTestProjects (repoRoot: string) : string list =
    let testsDir = Path.Combine(repoRoot, "tests")

    if not (Directory.Exists(testsDir)) then
        []
    else
        SafeWalk.enumerateFiles "*.fsproj" testsDir
        |> List.filter (fun path ->
            try
                let content = File.ReadAllText(path)
                content.Contains("xunit", StringComparison.OrdinalIgnoreCase)
            with
            | :? System.IO.IOException
            | :? System.UnauthorizedAccessException -> false)
        |> List.sort
