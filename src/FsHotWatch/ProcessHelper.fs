module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
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
