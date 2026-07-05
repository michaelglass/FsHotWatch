module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks

/// Outcome of running an external process. Tagged so callers can tell a
/// nonzero exit from a timeout-induced kill without parsing the output.
type ProcessOutcome =
    /// Process exited with code 0. `output` is combined stdout+stderr (trimmed).
    | Succeeded of output: string
    /// Process exited with a nonzero code. `output` is combined stdout+stderr.
    | Failed of exitCode: int * output: string
    /// Process did not exit within the timeout and was killed (along with its
    /// child process tree). `tail` is whatever stdout+stderr we drained before
    /// the kill — best-effort, may be empty.
    | TimedOut of after: TimeSpan * tail: string

/// Outcome of an in-process unit of work bounded by a wall-clock timeout.
type WorkOutcome<'a> =
    | WorkCompleted of 'a
    | WorkTimedOut of after: TimeSpan

let isSucceeded =
    function
    | Succeeded _ -> true
    | _ -> false

let isTimedOut =
    function
    | TimedOut _ -> true
    | _ -> false

/// Combined output regardless of outcome — for callers that just want the text
/// to render in a status line. Preserves the historical message format.
let outputOf (outcome: ProcessOutcome) : string =
    match outcome with
    | Succeeded out -> out
    | Failed(_, out) -> out
    | TimedOut(after, tail) -> $"timed out after %d{int after.TotalSeconds}s\n%s{tail}"

/// F17 (audit 2026-05-02): exception classes we treat as benign on the
/// timeout-kill path. `Process.Kill` raises InvalidOperationException only
/// when the process has already exited (a race between WaitForExit's
/// timeout and the child's natural exit). Anything else (Win32Exception
/// permission failure, NullReferenceException, etc.) is a real problem
/// and propagates.
let isExpectedKillException (ex: exn) : bool =
    match ex with
    | :? InvalidOperationException -> true
    | _ -> false

/// F18 (audit 2026-05-02): exception classes we treat as benign on the
/// post-kill drain path. Task.WaitAll bundles task failures as
/// AggregateException; the underlying stream reads can raise IOException
/// (pipe broken after kill) or ObjectDisposedException (stream closed by
/// the kill). All are expected. Anything else propagates.
let isExpectedDrainException (ex: exn) : bool =
    match ex with
    | :? AggregateException -> true
    | :? IO.IOException -> true
    | :? ObjectDisposedException -> true
    | _ -> false

/// Result of a read task after the bounded post-kill drain: its value if it
/// completed successfully, otherwise "". A task that is still running, faulted,
/// or cancelled when the drain window elapses yields "" — we never block on
/// `.Result` here. Extracted so the else-arm is deterministically covered by a
/// unit test; the real call sites are reached only on the OS-scheduling-
/// sensitive timeout-kill path (covered end-to-end in IntegrationTests).
let internal drainedOrEmpty (t: Task<string>) : string =
    if t.IsCompletedSuccessfully then t.Result else ""

/// True when the command will spawn `dotnet` (matching `dotnet`, `dotnet.exe`,
/// or paths ending in either). Used to inject MSBUILDDISABLENODEREUSE.
let isDotnetCommand (command: string) =
    let basename = System.IO.Path.GetFileName(command)
    basename = "dotnet" || basename = "dotnet.exe"

/// Env keys that are only meaningful to the parent's in-process hosts (the
/// .NET host, the in-process MSBuild ProjInfo stood up) and poison spawned
/// children if inherited. We strip them unconditionally on every spawn:
/// they're a no-op for non-dotnet children, and a spawned `dotnet` re-resolves
/// the correct values from its own argv[0] / SDK. Caller-supplied overrides
/// still win — the strip runs before the env overlay.
///
/// Per-category rationale:
///
/// Arch-specific DOTNET_ROOT_* — the .NET host writes these into its parent's
/// env from argv[0]'s dir so child apphosts inherit the same runtime. On
/// Nix-wrapped SDKs (and similar shims) that dir lacks
/// shared/Microsoft.NETCore.App, so any child dotnet trusts the value and
/// fails to find the runtime. Meaningful only to the .NET host, so dropping
/// them is a no-op for non-dotnet children, and any later dotnet invocation
/// re-resolves correctly from its own argv[0].
///
/// Ionide.ProjInfo MSBuild discovery (MSBUILD_EXE_PATH / MSBuildExtensionsPath
/// / MSBuildSDKsPath) — Ionide.ProjInfo (`Init.init`) writes these into the
/// daemon's OWN process environment to make in-process design-time project
/// evaluation work. They pin MSBuild at the SDK band ProjInfo selected at
/// startup. Process.Start inherits the full parent env, so leaving them set
/// poisons every spawned `dotnet build`: the child's MSBuild is forced to that
/// band's MSBuild.dll / Sdks dir even though the muxer may resolve a different
/// (or, on a multi-SDK box, an incomplete) band — restore-graph generation
/// then fails with exit 1 and ZERO diagnostics ("Build FAILED / 0 Error(s)"),
/// while a plain-shell `dotnet build` of the same tree is clean. Meaningful
/// only to an in-process MSBuild host, so dropping them is a no-op for
/// non-dotnet children, and a spawned dotnet re-resolves MSBuild correctly
/// from its own SDK. See docs/leaked-msbuild-env-bug.md.
let private sanitizedChildEnvKeys =
    [
      // arch-specific DOTNET_ROOT_*
      "DOTNET_ROOT_ARM64"
      "DOTNET_ROOT_X64"
      "DOTNET_ROOT_X86"
      // Ionide.ProjInfo MSBuild discovery
      "MSBUILD_EXE_PATH"
      "MSBuildExtensionsPath"
      "MSBuildSDKsPath" ]

/// Merge `MSBUILDDISABLENODEREUSE=1` into the env when the command is `dotnet`
/// and the caller hasn't already set the key. See docs/msbuild-node-reuse-bug.md.
let mergeDotnetEnv (command: string) (env: (string * string) list) : (string * string) list =
    if
        isDotnetCommand command
        && not (env |> List.exists (fun (k, _) -> k = "MSBUILDDISABLENODEREUSE"))
    then
        ("MSBUILDDISABLENODEREUSE", "1") :: env
    else
        env

/// Build the `ProcessStartInfo` for a spawned child: redirected stdio, the
/// working directory, the sanitized+overlaid environment, and the realpath'd
/// `DOTNET_HOST_PATH`. Shared by every spawn path (`runProcessWithTimeout` and
/// `runProcessWithLaunchWatchdog`) so the child-env contract lives in ONE place.
let private makeChildProcessStartInfo
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    : ProcessStartInfo =
    let psi = ProcessStartInfo(command, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- workDir

    // Strip before overlay so a caller-supplied entry in `env` survives.
    for key in sanitizedChildEnvKeys do
        psi.Environment.Remove(key) |> ignore

    for (key, value) in mergeDotnetEnv command env do
        psi.Environment[key] <- value

    // Realpath DOTNET_HOST_PATH so dirname(DOTNET_HOST_PATH) lands on the
    // directory containing shared/Microsoft.NETCore.App. On normal installs
    // this is a no-op (already canonical). On Nix-wrapped SDKs, the wrapper
    // bin/ has no shared/ sibling but the unwrapped target does — without
    // this, child apphosts die with "apphost_version not found" because the
    // muxer reads DOTNET_HOST_PATH literally. See
    // memory/dotnet_tool_launcher_truncates_nix_profiles.md.
    //
    // Applied AFTER the explicit env overlay so callers passing
    // DOTNET_HOST_PATH explicitly also get the symlink resolved. This lets
    // tests exercise the contract via explicit env rather than mutating
    // process env, which would race other tests' subprocess spawns.
    match psi.Environment.TryGetValue "DOTNET_HOST_PATH" with
    | true, hostPath when not (String.IsNullOrEmpty hostPath) ->
        try
            let resolved = System.IO.File.ResolveLinkTarget(hostPath, returnFinalTarget = true)

            if not (isNull resolved) then
                psi.Environment["DOTNET_HOST_PATH"] <- resolved.FullName
        with _ ->
            () // path missing or not a symlink — leave the original value alone
    | _ -> ()

    psi

/// Run a process with a timeout. Reads stdout and stderr concurrently to avoid
/// deadlock. On timeout the process tree is killed and TimedOut is returned.
///
/// For `dotnet` commands, injects `MSBUILDDISABLENODEREUSE=1` unless the
/// caller already set it. See docs/msbuild-node-reuse-bug.md.
let runProcessWithTimeout
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (timeout: TimeSpan)
    : ProcessOutcome =
    let psi = makeChildProcessStartInfo command args workDir env

    use proc = Process.Start(psi)
    // Register so a daemon shutdown can tear down in-flight children.
    ProcessRegistry.track proc

    try
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        let timeoutMs =
            if timeout = Threading.Timeout.InfiniteTimeSpan then
                -1
            else
                int timeout.TotalMilliseconds

        let exited = proc.WaitForExit(timeoutMs)

        if not exited then
            // F17 (audit 2026-05-02): the catch is broad because a deterministic
            // narrow form would add use-site branches that no integration test
            // can reliably hit (the kill-on-exited race fires opportunistically).
            // The expected exception class is documented and tested via
            // isExpectedKillException — see that helper for the invariant. The
            // broad catch here swallows the benign InvalidOperationException
            // race and is shutdown-edge, so masking unexpected types is bounded
            // to the timeout-kill path of an already-failed run.
            try
                proc.Kill(entireProcessTree = true)
            with _ ->
                ()

            // best-effort drain so we still report partial output
            let drainMs = 500

            // F18 (audit 2026-05-02): same shape as F17. The post-kill drain
            // expects AggregateException / IOException / ObjectDisposedException
            // (see isExpectedDrainException for the documented contract) but
            // the catch stays broad for the same coverage-determinism reason
            // as F17.
            try
                Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], drainMs) |> ignore
            with _ ->
                ()

            let stdout = drainedOrEmpty stdoutTask
            let stderr = drainedOrEmpty stderrTask

            TimedOut(timeout, $"%s{stdout}\n%s{stderr}".Trim())
        else
            Task.WaitAll(stdoutTask, stderrTask)
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result
            let output = $"%s{stdout}\n%s{stderr}".Trim()

            if proc.ExitCode = 0 then
                Succeeded output
            else
                Failed(proc.ExitCode, output)
    finally
        ProcessRegistry.untrack proc

/// Run a process to completion (no timeout).
let runProcess (command: string) (args: string) (workDir: string) (env: (string * string) list) : ProcessOutcome =
    runProcessWithTimeout command args workDir env Threading.Timeout.InfiniteTimeSpan

// ---------------------------------------------------------------------------
// Launch-liveness watchdog (AUTOMATION-65 QA finding: the launch gap)
//
// `runProcessWithTimeout` bounds only the TOTAL run: it blocks on a single
// `WaitForExit(timeoutMs)`. For a test config with no `TimeoutSec` that
// timeout is INFINITE, so if the spawned child never becomes a live,
// progressing process — the box is so overloaded the spawn goes nowhere, or a
// machine sleep kills the child mid-launch — the wait blocks FOREVER. Nothing
// raises, so the plugin stays `Running` and `check`'s `WaitForComplete` streams
// "Waiting for plugins" indefinitely (observed 33 min – 16 h in the field).
//
// `runProcessWithLaunchWatchdog` closes that gap: it POLLS liveness instead of
// blocking on one infinite wait, and enforces a bounded *launch* deadline — the
// window in which the child must show its FIRST sign of life (any output, or an
// exit). Once it does, the wait is unbounded again (only the overall timeout can
// end it), so a slow-but-progressing suite streaming output is NEVER launch-
// killed: the deadline governs launch, not total duration. A child that EXITS is
// classified normally by its exit code (the machine-sleep case is covered by the
// poll OBSERVING the exit + a bounded post-exit drain — NOT by guessing a "death"
// from a no-output nonzero exit, which is indistinguishable from a genuine
// failing / zero-match test).
// ---------------------------------------------------------------------------

/// Raised when a launched child never becomes a live, progressing process: it
/// showed NO sign of life (no output, no exit) within the launch deadline — the
/// box is so overloaded the spawn went nowhere. The message names the elapsed
/// launch budget; TestPrune catches it and drives the run's `Aborted` lifecycle
/// so `check` exits non-green with a legible "re-run when quiet" diagnostic
/// rather than wedging.
///
/// `Message` is overridden to return the raw diagnostic (not F#'s default
/// `LaunchStalledException "..."` repr) so the string that flows through
/// `abortedRunLifecycle ex.Message` into the plugin's Failed status — and thence
/// to `fshw check` / `fshw errors` — is clean.
exception LaunchStalledException of string with
    override this.Message = this.Data0

/// The next action for one launch-watchdog poll. `KeepWaiting` loops; the other
/// three map onto the terminal `LaunchOutcome`.
[<RequireQualifiedAccess>]
type LaunchStep =
    /// The process ended — drain its output and classify by exit code.
    | Exited
    /// The overall (per-config) timeout elapsed — kill the tree, report TimedOut.
    | TimedOut
    /// No sign of life within the launch deadline — kill the tree, report a stall.
    | Stalled
    | KeepWaiting

/// The terminal verdict of the launch-watchdog loop. Distinct from `LaunchStep`
/// so the loop's result can NEVER be `KeepWaiting` — the impossible "still
/// waiting" outcome is unrepresentable, and the production match has no dead arm.
[<RequireQualifiedAccess>]
type LaunchOutcome =
    | Exited
    | TimedOut
    | Stalled

/// Pure launch-liveness decision for one poll. Ordering is load-bearing:
///  1. a process that EXITED wins over everything (natural completion, even if
///     it raced the launch deadline);
///  2. then the overall timeout (a hard cap the caller asked for);
///  3. a process that has produced ANY output is "alive & progressing" — only
///     the overall timeout can end it, NEVER the launch deadline (this is what
///     protects a long DB suite that streams output for many minutes);
///  4. only a process that has neither exited nor produced a byte, past the
///     launch deadline, is a stall.
let decideLaunchStep
    (launchDeadlineReached: bool)
    (overallTimeoutReached: bool)
    (exited: bool)
    (sawOutput: bool)
    : LaunchStep =
    if exited then LaunchStep.Exited
    elif overallTimeoutReached then LaunchStep.TimedOut
    elif sawOutput then LaunchStep.KeepWaiting
    elif launchDeadlineReached then LaunchStep.Stalled
    else LaunchStep.KeepWaiting

/// Injectable launch-watchdog loop. Polls `observe` (returns `exited, sawOutput`)
/// against the launch deadline and the optional overall timeout, sleeping between
/// polls, until a terminal `LaunchOutcome` is reached. All effects (observe /
/// clock / sleep) are injected so the loop is deterministically testable without
/// spawning a real process — mirrors `waitForDaemonReadyWith`. `overallTimeout =
/// InfiniteTimeSpan` disables the total cap (the common TestPrune case), leaving
/// the launch deadline as the sole escape from an infinite wait.
let launchWatchdogLoopWith
    (observe: unit -> bool * bool)
    (now: unit -> DateTime)
    (sleep: int -> unit)
    (pollMs: int)
    (launchDeadline: TimeSpan)
    (overallTimeout: TimeSpan)
    : LaunchOutcome =
    let start = now ()
    let launchDeadlineAt = start.Add launchDeadline

    let overallTimeoutAt =
        if overallTimeout = Threading.Timeout.InfiniteTimeSpan then
            None
        else
            Some(start.Add overallTimeout)

    let rec loop () =
        let exited, sawOutput = observe ()

        let launchReached = now () >= launchDeadlineAt

        let overallReached =
            match overallTimeoutAt with
            | Some t -> now () >= t
            | None -> false

        match decideLaunchStep launchReached overallReached exited sawOutput with
        | LaunchStep.Exited -> LaunchOutcome.Exited
        | LaunchStep.TimedOut -> LaunchOutcome.TimedOut
        | LaunchStep.Stalled -> LaunchOutcome.Stalled
        | LaunchStep.KeepWaiting ->
            sleep pollMs
            loop ()

    loop ()

/// Default launch deadline: the window in which a spawned test child must show
/// its first sign of life. Deliberately generous — a real runner emits its
/// discovery/progress banner within seconds, so 5 min only ever trips on a
/// genuinely-wedged spawn, never on a slow-but-alive suite.
let DefaultLaunchDeadline = TimeSpan.FromMinutes 5.0

/// Resolve the launch deadline from an optional override string (the
/// `FSHW_LAUNCH_DEADLINE_SEC` env value). A positive integer count of seconds
/// wins; anything else (absent, unparseable, non-positive) falls back to
/// `DefaultLaunchDeadline`. Pure so the precedence is unit-testable without
/// touching process env.
let resolveLaunchDeadline (overrideSec: string option) : TimeSpan =
    match overrideSec with
    | Some s ->
        match Int32.TryParse(s: string) with
        | true, n when n > 0 -> TimeSpan.FromSeconds(float n)
        | _ -> DefaultLaunchDeadline
    | None -> DefaultLaunchDeadline

/// Bounded post-exit drain window. Once `HasExited` is true the exit CODE is
/// available immediately, but the redirected streams may not have reached EOF —
/// and if the child spawned a grandchild (an MSBuild/vstest node) that inherited
/// the stdout pipe and outlives it, EOF NEVER comes. An unbounded `WaitForExit()`
/// blocks forever on that pipe even though the process itself is gone — the exact
/// 16 h machine-sleep wedge. So we drain only for a bounded window, then proceed
/// with whatever output was captured; the verdict rides on the exit code, not on
/// stream EOF.
[<Literal>]
let private PostExitDrainMs = 2000

/// Run a process under a launch-liveness watchdog. Behaves like
/// `runProcessWithTimeout` for a process that becomes live and progresses, but
/// can NEVER block forever: if the child shows no sign of life within
/// `launchDeadline`, the process tree is killed and `LaunchStalledException` is
/// raised. A process that EXITS — for any reason, with any code — is classified
/// normally by its exit code (a nonzero exit with no output is a genuine test
/// failure / zero-match, INDISTINGUISHABLE from a spawn-death at the process
/// boundary, so it must NOT be force-aborted). The machine-sleep case is covered
/// not by guessing from the exit code but by POLLING `HasExited` (so the exit is
/// observed at all) plus the bounded post-exit drain (so a grandchild holding the
/// pipe can't re-wedge the read). Reads stdout/stderr incrementally (event-based)
/// so the FIRST byte is observed as liveness.
///
/// For `dotnet` commands, injects `MSBUILDDISABLENODEREUSE=1` unless the caller
/// already set it (via `makeChildProcessStartInfo`).
let runProcessWithLaunchWatchdog
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (timeout: TimeSpan)
    (launchDeadline: TimeSpan)
    : ProcessOutcome =
    let psi = makeChildProcessStartInfo command args workDir env

    use proc = Process.Start(psi)
    // Register so a daemon shutdown can tear down in-flight children.
    ProcessRegistry.track proc

    // Incremental output capture via explicit stream pumps. We can NOT use the
    // event API (`BeginOutputReadLine`) here: draining it requires the
    // parameterless `WaitForExit()` (the timed overload does NOT flush the async
    // handlers), and that overload is exactly the unbounded, grandchild-pipe-
    // wedging wait we must avoid. Direct `ReadAsync` gives us Task handles we can
    // bound-wait AND flips a latch on the FIRST byte — the "alive & progressing"
    // signal the launch deadline keys off (`ReadToEndAsync` only completes at
    // EOF, which a wedged launch never reaches).
    let output = StringBuilder()
    let outputLock = obj ()
    let mutable sawOutput = 0

    let pump (reader: IO.StreamReader) : Task =
        task {
            let buf = Array.zeroCreate<char> 4096
            let mutable go = true

            while go do
                let! n = reader.ReadAsync(buf.AsMemory())

                if n = 0 then
                    go <- false
                else
                    Volatile.Write(&sawOutput, 1)
                    lock outputLock (fun () -> output.Append(buf, 0, n) |> ignore)
        }
        :> Task

    let stdoutTask = pump proc.StandardOutput
    let stderrTask = pump proc.StandardError

    let drainedOutput () =
        lock outputLock (fun () -> output.ToString().Trim())

    // BOUNDED drain of the stream pumps. A normal process's pipes reach EOF the
    // instant it exits (returns in ms); only a grandchild holding the pipe makes
    // this block, and then only for the window — never forever.
    let drainPumps () =
        try
            Task.WaitAll([| stdoutTask; stderrTask |], PostExitDrainMs) |> ignore
        with _ ->
            () // a faulted pump (pipe torn down on kill) is expected — drain best-effort

    let pollMs = 250

    try
        // The "sleep" between polls IS a bounded `WaitForExit`: it wakes early
        // the instant the child exits (so completion is observed promptly) but
        // caps at `pollMs` so the launch/overall deadlines are still checked
        // regularly. `observe` reads the independent liveness handle
        // (`HasExited`) — the poll that closes the machine-sleep hole where a
        // single blocking wait never returned.
        let observe () =
            proc.HasExited, (Volatile.Read &sawOutput = 1)

        let sleep ms = proc.WaitForExit(ms: int) |> ignore

        let outcome =
            launchWatchdogLoopWith observe (fun () -> DateTime.UtcNow) sleep pollMs launchDeadline timeout

        // A killed tree still needs draining so partial output is reported.
        let killTree () =
            try
                proc.Kill(entireProcessTree = true)
            with _ ->
                ()

        match outcome with
        | LaunchOutcome.Exited ->
            drainPumps ()
            let out = drainedOutput ()

            if proc.ExitCode = 0 then
                Succeeded out
            else
                Failed(proc.ExitCode, out)
        | LaunchOutcome.TimedOut ->
            // Same kill+drain shape as runProcessWithTimeout's timeout arm.
            killTree ()
            drainPumps ()
            TimedOut(timeout, drainedOutput ())
        | LaunchOutcome.Stalled ->
            killTree ()
            drainPumps ()

            raise (
                LaunchStalledException(
                    $"test launch produced no live process within %d{int launchDeadline.TotalSeconds}s — box overloaded or process died at spawn; re-run when quiet"
                )
            )
    finally
        ProcessRegistry.untrack proc

/// Run a synchronous unit of work with a wall-clock timeout, threading a
/// `CancellationToken` into the work so a timed-out unit is ACTUALLY cancelled
/// rather than orphaned. The token is cancelled the instant the wait expires;
/// cooperative work (anything that polls `ct.IsCancellationRequested` or calls
/// `ct.ThrowIfCancellationRequested()`, or an `Async` driven under the token)
/// then unwinds and releases whatever lock it held — closing the "stuck unit
/// times out the WAIT but the runaway thread keeps holding a lock → daemon
/// stays wedged" hole the audit flagged.
///
/// Work that ignores the token (a tight non-cooperative CPU loop, a P/Invoke
/// that can't observe cancellation) still cannot be force-killed in-process —
/// that is a .NET limitation, not a bug here — but the common cases (FCS /
/// Fantomas / analyzer work that honour their token, or `Thread.Sleep`-style
/// waits replaced by `ct.WaitHandle.WaitOne`) now release on timeout.
///
/// Uses `TaskCreationOptions.LongRunning` so the work runs on a dedicated
/// thread rather than a pool worker. Plugin work can be CPU-heavy (FCS,
/// analyzers) and the timeout-test path injects a cooperative wait to force
/// expiry; both starve the default thread pool under parallel test load and
/// caused 5s xUnit timeouts to fire spuriously on unrelated tests.
let runWithCancellableTimeout (timeout: TimeSpan) (work: CancellationToken -> 'a) : WorkOutcome<'a> =
    if timeout = Threading.Timeout.InfiniteTimeSpan then
        WorkCompleted(work CancellationToken.None)
    else
        use cts = new CancellationTokenSource()

        let task =
            Task.Factory.StartNew((fun () -> work cts.Token), TaskCreationOptions.LongRunning)

        if task.Wait(timeout) then
            WorkCompleted task.Result
        else
            // Signal the work to unwind so it stops holding any lock. We do NOT
            // block on the orphan after cancelling — a cooperative unit observes
            // the token and exits promptly; a non-cooperative one would hang us
            // here, which is precisely what we must avoid. The CTS is disposed by
            // the enclosing `use`; cancellation has already been requested, so a
            // late-cancelling token registration on a disposed CTS cannot occur.
            cts.Cancel()
            WorkTimedOut timeout

/// Run a synchronous unit of work with a wall-clock timeout. Back-compat
/// shim over `runWithCancellableTimeout` for work that cannot observe
/// cancellation. PREFER `runWithCancellableTimeout` for new call sites so a
/// timed-out unit actually releases its locks instead of running on as an
/// orphan thread.
let runWithTimeout (timeout: TimeSpan) (work: unit -> 'a) : WorkOutcome<'a> =
    runWithCancellableTimeout timeout (fun _ct -> work ())
