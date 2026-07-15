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
        let stdout = awaitOutput stdoutTask
        let stderr = awaitOutput stderrTask

        eprintfn $"[%s{fileName} %s{arguments}] \u2192 exit %d{proc.ExitCode} in %.1f{sw.Elapsed.TotalSeconds}s"

        { ExitCode = proc.ExitCode
          Stdout = stdout
          Stderr = stderr }

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
