module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks

/// What a spawned child ACTUALLY said — tagged with whether we managed to hear all
/// of it (AUTOMATION-126).
///
/// A child's output is read by stream pumps that end at EOF. When the drain window
/// closes with a pump still open, whatever we captured is NOT a measurement of the
/// child's output: an empty capture then means "we read nothing", which is a
/// different fact from "the child printed nothing" — and the two used to be the
/// same `string`. A test asserting `Assert.Equal("hi", out)` against a starved
/// drain saw `""` and reported an empty output as if it had observed one; that is
/// the same disease as a `top` that cannot sample reporting a healthy box.
///
/// So the two facts are now different VALUES. A caller that only renders text says
/// so (`ProcessOutput.text`); a caller that decides anything must match and handle
/// `DrainTimedOut` explicitly. Unrepresentable beats detected; detected beats silent.
[<RequireQualifiedAccess>]
type ProcessOutput =
    /// Both pumps reached EOF inside the drain window: `text` is the child's
    /// COMPLETE stdout+stderr (trimmed). The only capture you may assert against.
    | Drained of text: string
    /// We never saw EOF on both streams — either the drain window elapsed with a
    /// pump still blocked on a pipe a GRANDCHILD holds open (the 16 h-wedge shape,
    /// see `PostExitDrainWindow`), or a pump's read DIED on a pipe torn down by the
    /// timeout-kill. `captured` is whatever bytes had arrived by then. It is not a
    /// measurement: `""` here means "we read nothing", NEVER "the child printed
    /// nothing".
    | DrainTimedOut of captured: string * window: TimeSpan

[<RequireQualifiedAccess>]
module ProcessOutput =

    /// The captured bytes, untagged — for callers that only SEARCH the text for a
    /// marker they either find or don't (a runner's "Zero tests ran" line), where a
    /// short capture can only ever cost a hit, never invent one.
    ///
    /// Do NOT use this to conclude something from an ABSENCE. If the emptiness (or
    /// the exact value) of the output is the thing you are deciding on, match the
    /// `ProcessOutput` and treat `DrainTimedOut` as the non-answer that it is.
    let text (output: ProcessOutput) : string =
        match output with
        | ProcessOutput.Drained text -> text
        | ProcessOutput.DrainTimedOut(captured, _) -> captured

/// Outcome of running an external process. Tagged so callers can tell a
/// nonzero exit from a timeout-induced kill without parsing the output.
///
/// The VERDICT rides on the exit code, never on stream EOF — a child that exits 0
/// while a grandchild holds its stdout pipe has still succeeded. So a drain that
/// could not finish does not change the case; it is carried INSIDE the output, where
/// a caller that reads the text cannot help but see it (cf. `RunVerdict`: a terminal
/// status must carry its evidence).
type ProcessOutcome =
    /// Process exited with code 0. `output` is combined stdout+stderr (trimmed).
    | Succeeded of output: ProcessOutput
    /// Process exited with a nonzero code. `output` is combined stdout+stderr.
    | Failed of exitCode: int * output: ProcessOutput
    /// Process did not exit within the timeout and was killed (along with its
    /// child process tree). `tail` is whatever stdout+stderr we drained before
    /// the kill — best-effort, and typically a `DrainTimedOut` because the kill
    /// tears the pipes down under the pumps.
    | TimedOut of after: TimeSpan * tail: ProcessOutput

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

/// Render a capture for a HUMAN — a log line, a plugin status, an error entry. An
/// incomplete drain is NAMED in the rendered text, so nobody reads a silence here
/// and trusts it. (`ProcessOutput.text` is the untagged form for text-searching.)
let renderOutput (output: ProcessOutput) : string =
    match output with
    | ProcessOutput.Drained text -> text
    | ProcessOutput.DrainTimedOut(captured, window) ->
        $"%s{captured}\n[fshw] OUTPUT INCOMPLETE: the child exited but its streams never reached EOF within \
          %d{int window.TotalSeconds}s (a grandchild is holding the pipe open, or the read died with it). The text \
          above is what we caught, not what the child said."

/// Combined output regardless of outcome — for callers that just want the text
/// to render in a status line. Preserves the historical message format.
let outputOf (outcome: ProcessOutcome) : string =
    match outcome with
    | Succeeded out -> renderOutput out
    | Failed(_, out) -> renderOutput out
    | TimedOut(after, tail) -> $"timed out after %d{int after.TotalSeconds}s\n%s{renderOutput tail}"

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

/// Did a stream pump that has STOPPED reading do so at EOF?
///
/// `None` — the read loop ran to EOF: the stream is exhausted, nothing more will
/// ever arrive from it, and what we captured from it is complete.
///
/// `Some ex` — the read DIED. On the timeout-kill path that is expected (F18: the
/// kill tears the pipe down under the pump), and it means the capture is NOT
/// provably complete, so the pump reports `false` and the drain is classified as
/// timed-out rather than drained. Anything OUTSIDE the expected classes is a real
/// bug: it is re-raised on the pump's task and surfaces through the drain wait,
/// because a read that failed for a reason we do not understand must never be
/// laundered into "the child printed nothing".
///
/// Pure so all three arms are covered deterministically — the live paths need a
/// torn-down pipe (exercised end-to-end in FsHotWatch.IntegrationTests).
let internal pumpReachedEof (failure: exn option) : bool =
    match failure with
    | None -> true
    | Some ex when isExpectedDrainException ex -> false
    | Some ex -> raise ex

/// Classify the bounded post-exit drain. The capture is the child's COMPLETE output
/// only if the wait returned inside the window AND both pumps ended at EOF; anything
/// else is a capture we cannot vouch for.
///
/// The EOF flags are THUNKS because they are backed by `Task.Result`, which blocks
/// on a pump that is still running: they may only be forced once `waitReturned`
/// proves both pumps are done. `&&` short-circuits, so the types enforce the order.
let internal classifyDrain
    (waitReturned: bool)
    (stdoutReachedEof: unit -> bool)
    (stderrReachedEof: unit -> bool)
    (captured: string)
    (window: TimeSpan)
    : ProcessOutput =
    if waitReturned && stdoutReachedEof () && stderrReachedEof () then
        ProcessOutput.Drained captured
    else
        ProcessOutput.DrainTimedOut(captured, window)

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

// ---------------------------------------------------------------------------
// ONE spawn primitive (AUTOMATION-98).
//
// There used to be TWO: `runProcessWithTimeout` (a single blocking
// `WaitForExit(timeoutMs)` + an UNBOUNDED `Task.WaitAll` drain on the success
// path) and `runProcessWithLaunchWatchdog` (polled liveness + a bounded
// post-exit drain). Every caller except TestPrune used the unsafe one, so the
// two wedges the watchdog was built to close were still wide open everywhere
// else:
//
//   * `WaitForExit(-1)` never returns if a machine sleep kills the child
//     mid-launch — nothing raises, the plugin stays `Running` forever.
//   * `Task.WaitAll(stdout, stderr)` on the SUCCESS path never returns if the
//     child exited but a GRANDCHILD (an MSBuild node, a Playwright driver)
//     inherited the stdout pipe and outlives it — EOF never comes. That is the
//     16 h wedge, and it is reachable from any hook / build / fileCommand.
//
// Adding a safe sibling did not work (callers kept reaching for the footgun),
// so the two are COLLAPSED: `runProcess` is the only spawn, and it always
// polls `HasExited` and always bounds the post-exit drain. What varies per call
// site is `ProcessBounds` — and there is no way to construct bounds that mean
// "wait forever with no escape".
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
/// spawning a real process — mirrors `waitForDaemonReadyWith`.
///
/// Either deadline may be `InfiniteTimeSpan`, which DISABLES that one:
///  * `overallTimeout = Infinite` — no total cap (the common TestPrune case),
///    leaving the launch deadline as the sole escape from an infinite wait.
///  * `launchDeadline = Infinite` — output cannot prove liveness for this child
///    (a silent `dotnet build`), so a no-output window means nothing and only the
///    overall timeout can end the wait. `ProcessBounds.silent` is the only way to
///    ask for this, and it demands a finite total timeout in exchange.
///
/// NOTE the `Infinite` handling must be explicit: `InfiniteTimeSpan` is -1 ms, so
/// `start.Add launchDeadline` would land in the PAST and stall every spawn on its
/// first poll.
let launchWatchdogLoopWith
    (observe: unit -> bool * bool)
    (now: unit -> DateTime)
    (sleep: int -> unit)
    (pollMs: int)
    (launchDeadline: TimeSpan)
    (overallTimeout: TimeSpan)
    : LaunchOutcome =
    let start = now ()

    let deadlineAt (span: TimeSpan) =
        if span = Threading.Timeout.InfiniteTimeSpan then
            None
        else
            Some(start.Add span)

    let launchDeadlineAt = deadlineAt launchDeadline
    let overallTimeoutAt = deadlineAt overallTimeout

    let reached (at: DateTime option) =
        match at with
        | Some t -> now () >= t
        | None -> false

    let rec loop () =
        let exited, sawOutput = observe ()

        let launchReached = reached launchDeadlineAt
        let overallReached = reached overallTimeoutAt

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
///
/// AUTOMATION-126: a wall clock is the RIGHT bound here and the only sound one —
/// a pipe a grandchild holds open has no "work" left to wait for, so no
/// work-completion signal can ever arrive and only a clock can end the wait. What
/// was wrong was not the clock but what it was measuring: the pumps used to be
/// `task {}` continuations on the THREAD POOL, so on a saturated box the reader
/// was never scheduled and the window expired having read nothing — the clock was
/// measuring the pool, not the pipe. The pumps now own dedicated threads (below),
/// so this window once again measures the thing it names; and when it does expire,
/// it says so (`ProcessOutput.DrainTimedOut`) instead of handing back `""`.
let internal PostExitDrainWindow = TimeSpan.FromSeconds 2.0

/// The bounds ONE spawned child runs under. Construct only via
/// `ProcessBounds.streaming` / `ProcessBounds.silent` — the fields are private
/// precisely so a call site cannot assemble "no bound at all" out of two
/// `InfiniteTimeSpan`s, which is what `runProcess command args dir env
/// Timeout.InfiniteTimeSpan` used to mean and what wedged the daemon for 8h36m.
///
/// The two constructors encode a real property of the CHILD (does its output
/// prove it is alive?), not two safety levels — every spawn, either way, polls
/// `HasExited` and bounds its post-exit drain.
[<NoComparison; NoEquality>]
type ProcessBounds =
    private
        {
            /// Hard cap on total run duration; `InfiniteTimeSpan` = no total cap.
            Timeout: TimeSpan
            /// Window in which the child must show its first sign of life;
            /// `InfiniteTimeSpan` = output does not prove liveness for this child,
            /// so no launch bound applies.
            LaunchDeadline: TimeSpan
        }

[<RequireQualifiedAccess>]
module ProcessBounds =

    /// A child that STREAMS as it works — a test runner printing its discovery
    /// banner, a compiler at normal verbosity. Its FIRST byte (or its exit) is a
    /// sound liveness proof, so a `launchDeadline` of silence means the spawn went
    /// nowhere: the tree is killed and `LaunchStalledException` is raised. Once a
    /// byte arrives the launch deadline never fires again, so a slow-but-alive
    /// suite is never launch-killed — only `timeout` (which MAY be infinite here,
    /// because the launch deadline is already an escape from an infinite wait) can
    /// end it.
    let streaming (timeout: TimeSpan) (launchDeadline: TimeSpan) : ProcessBounds =
        { Timeout = timeout
          LaunchDeadline = launchDeadline }

    /// A child that may be SILENT for its entire run — `dotnet build -v q`, or a
    /// `sh -c "cmd > /tmp/log; echo done"` wrapper that buffers everything until
    /// the end. Output proves NOTHING about liveness here, so applying a launch
    /// deadline would false-kill a healthy slow build. The finite `timeout` is
    /// therefore the bound, and passing `InfiniteTimeSpan` is the one way left to
    /// ask for an unbounded wait — so it must be a DELIBERATE act (an explicit
    /// `"timeoutSec": false` in `.fshw.json`), never an omission. It is logged.
    let silent (timeout: TimeSpan) : ProcessBounds =
        if timeout = Threading.Timeout.InfiniteTimeSpan then
            Logging.warn
                "process"
                "spawning a silent child with NO timeout: output cannot prove liveness and no clock \
                 can end the wait, so a hung child will hold this operation until the daemon is \
                 restarted. Set `timeoutSec` in .fshw.json to bound it."

        { Timeout = timeout
          LaunchDeadline = Threading.Timeout.InfiniteTimeSpan }

/// THE spawn. Polls `HasExited` (never a single blocking `WaitForExit(-1)`, which
/// a machine sleep turns into a permanent wait) and ALWAYS bounds the post-exit
/// drain (never an unbounded `Task.WaitAll` on the redirected streams, which a
/// grandchild holding the inherited stdout pipe turns into a permanent wait —
/// even on the SUCCESS path, even for a child that already exited cleanly).
///
/// A process that EXITS — for any reason, with any code — is classified normally
/// by its exit code: a nonzero exit with no output is a genuine test failure /
/// zero-match, INDISTINGUISHABLE from a spawn-death at the process boundary, so
/// it must NOT be force-aborted. Only a `ProcessBounds.streaming` child that has
/// neither exited nor emitted a byte within its launch deadline raises
/// `LaunchStalledException`.
///
/// Reads stdout/stderr incrementally so the FIRST byte is observed as liveness.
/// For `dotnet` commands, injects `MSBUILDDISABLENODEREUSE=1` unless the caller
/// already set it (via `makeChildProcessStartInfo`).
let runProcess
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome =
    let timeout = bounds.Timeout
    let launchDeadline = bounds.LaunchDeadline
    let psi = makeChildProcessStartInfo command args workDir env

    use proc = Process.Start(psi)
    // Register so a daemon shutdown can tear down in-flight children.
    ProcessRegistry.track proc

    // Incremental output capture via explicit stream pumps. We can NOT use the
    // event API (`BeginOutputReadLine`) here: draining it requires the
    // parameterless `WaitForExit()` (the timed overload does NOT flush the async
    // handlers), and that overload is exactly the unbounded, grandchild-pipe-
    // wedging wait we must avoid. A chunk-at-a-time `Read` loop gives us a Task
    // handle we can bound-wait AND flips a latch on the FIRST byte — the "alive &
    // progressing" signal the launch deadline keys off (`ReadToEnd` only returns at
    // EOF, which a wedged launch never reaches).
    let output = StringBuilder()
    let outputLock = obj ()
    let mutable sawOutput = 0

    // Each pump owns a DEDICATED thread (`LongRunning`) and reads SYNCHRONOUSLY.
    //
    // AUTOMATION-126: it used to be a `task {}` over `ReadAsync`, whose every
    // continuation is scheduled on the thread pool. Under a saturated pool — a
    // `check` running the full suite in parallel, exactly when a spawn's output
    // matters most — the reader simply never ran, the 2 s drain window expired
    // having read zero bytes, and the child's output came back as `""`. The clock
    // was measuring the POOL, not the process. `runWithCancellableTimeout` below
    // already learned this lesson (its work runs `LongRunning` for the same
    // reason); the pumps had not. A reader that cannot be scheduled cannot read,
    // and a read that cannot happen must not be reported as a read of nothing.
    //
    // Returns TRUE iff the loop ended at EOF — i.e. the stream is exhausted and
    // what we captured from it is all there ever was. See `pumpReachedEof`.
    let pump (reader: IO.StreamReader) : Task<bool> =
        Task.Factory.StartNew(
            (fun () ->
                let mutable failure = None

                try
                    let buf = Array.zeroCreate<char> 4096
                    let mutable go = true

                    while go do
                        let n = reader.Read(buf, 0, buf.Length)

                        if n = 0 then
                            go <- false
                        else
                            Volatile.Write(&sawOutput, 1)
                            lock outputLock (fun () -> output.Append(buf, 0, n) |> ignore)
                with ex ->
                    failure <- Some ex

                pumpReachedEof failure),
            TaskCreationOptions.LongRunning
        )

    let stdoutTask = pump proc.StandardOutput
    let stderrTask = pump proc.StandardError

    let drainedOutput () =
        lock outputLock (fun () -> output.ToString().Trim())

    // BOUNDED drain of the stream pumps. A normal process's pipes reach EOF the
    // instant it exits (returns in ms); only a grandchild holding the pipe makes
    // this block, and then only for the window — never forever.
    //
    // The window expiring is a FACT ABOUT THE MEASUREMENT, not a fact about the
    // child, so it rides out on the value: `DrainTimedOut` can never be mistaken
    // for a child that said nothing.
    let drainPumps () : ProcessOutput =
        let waitReturned =
            Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], int PostExitDrainWindow.TotalMilliseconds)

        classifyDrain
            waitReturned
            (fun () -> stdoutTask.Result)
            (fun () -> stderrTask.Result)
            (drainedOutput ())
            PostExitDrainWindow

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
            let out = drainPumps ()

            if proc.ExitCode = 0 then
                Succeeded out
            else
                Failed(proc.ExitCode, out)
        | LaunchOutcome.TimedOut ->
            killTree ()
            TimedOut(timeout, drainPumps ())
        | LaunchOutcome.Stalled ->
            killTree ()

            // A stall is DEFINED as "not one byte within the launch deadline", so
            // there is no capture to report and nothing a drain could tell us: we
            // run it only to let the pumps close their pipes, and discard the
            // (necessarily empty) result deliberately. The diagnostic is the
            // exception, not the text.
            drainPumps () |> ignore

            raise (
                LaunchStalledException(
                    $"launch produced no live process within %d{int launchDeadline.TotalSeconds}s — box overloaded or process died at spawn; re-run when quiet"
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
