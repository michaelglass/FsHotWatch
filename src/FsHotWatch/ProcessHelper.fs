module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks

/// What a spawned child ACTUALLY said — tagged with whether we managed to hear all
/// of it.
///
/// A child's output is read by stream pumps that end at EOF. When the drain window
/// closes with a pump still open, an empty capture means "we read nothing", which is
/// a different fact from "the child printed nothing" — so the two are different
/// VALUES. A caller that only renders text can use `ProcessOutput.text`; a caller
/// that decides anything must match and handle `DrainTimedOut` explicitly.
[<RequireQualifiedAccess>]
type ProcessOutput =
    /// Both pumps reached EOF inside the drain window: `text` is the child's
    /// COMPLETE stdout+stderr (trimmed). The only capture you may assert against.
    | Drained of text: string
    /// We never saw EOF on both streams — either the drain window elapsed with a
    /// pump still blocked on a pipe a GRANDCHILD holds open (see
    /// `PostExitDrainWindow`), or a pump's read DIED on a pipe torn down by the
    /// timeout-kill. `captured` is whatever bytes had arrived by then; `""` here
    /// means "we read nothing", NEVER "the child printed nothing".
    | DrainTimedOut of captured: string * window: TimeSpan

[<RequireQualifiedAccess>]
module ProcessOutput =

    /// The captured bytes, untagged — for callers that only SEARCH the text for a
    /// marker they either find or don't (a runner's "Zero tests ran" line), where a
    /// short capture can only ever cost a hit, never invent one.
    ///
    /// Do NOT use this to conclude something from an ABSENCE. If the emptiness (or
    /// the exact value) of the output is what you are deciding on, match the
    /// `ProcessOutput` and treat `DrainTimedOut` as the non-answer that it is.
    let text (output: ProcessOutput) : string =
        match output with
        | ProcessOutput.Drained text -> text
        | ProcessOutput.DrainTimedOut(captured, _) -> captured

/// What happened when we tried to tear down a timed-out child's process tree.
/// "I could not kill it" must never be spelled the same way as "I killed it": a
/// caller who reads `TimedOut` as a promise the tree is dead walks away from a
/// runaway that may still hold a lock, a port or a pipe.
///
/// `NoComparison`: this carries the failing `exn` itself, and exceptions do not
/// order. Equality still holds, which is all any call site asks of it.
[<RequireQualifiedAccess; NoComparison>]
type KillOutcome =
    /// `Kill(entireProcessTree = true)` returned: the tree is dead.
    | Killed
    /// The child had ALREADY exited when the kill landed — the documented race
    /// between the timeout firing and the child's natural exit
    /// (`isExpectedKillException`). Nobody had to kill it and the tree is dead
    /// either way, so this is benign: a kill we did not need, NOT a kill we failed.
    | AlreadyExited
    /// The kill FAILED, for a reason that is not the already-exited race. As far as
    /// we know the child — and every grandchild it spawned — is STILL RUNNING, and
    /// we are about to stop watching it. This is the case that may never be silent.
    | KillFailed of reason: exn

/// Outcome of running an external process. Tagged so callers can tell a
/// nonzero exit from a timeout-induced kill without parsing the output.
///
/// The VERDICT rides on the exit code, never on stream EOF — a child that exits 0
/// while a grandchild holds its stdout pipe has still succeeded. So a drain that
/// could not finish does not change the case; it is carried INSIDE the output.
///
/// `NoComparison` follows from `KillOutcome`'s.
[<NoComparison>]
type ProcessOutcome =
    /// Process exited with code 0. `output` is combined stdout+stderr (trimmed).
    | Succeeded of output: ProcessOutput
    /// Process exited with a nonzero code. `output` is combined stdout+stderr.
    | Failed of exitCode: int * output: ProcessOutput
    /// Process did not exit within the timeout, so we killed its tree. `tail` is
    /// whatever stdout+stderr we drained before the kill — best-effort, and
    /// typically a `DrainTimedOut` because the kill tears the pipes down under the
    /// pumps.
    ///
    /// `kill` is whether the teardown ACTUALLY happened. It rides on the value
    /// rather than in a log line, because "timed out, tree killed" and "timed out,
    /// tree still running" are different facts.
    | TimedOut of after: TimeSpan * tail: ProcessOutput * kill: KillOutcome

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

/// A child that did NOT choose its exit code — the OS terminated it.
///
/// The distinction is the whole point. A test runner's exit code is normally
/// EVIDENCE: 0 means the suite passed, Microsoft.Testing.Platform's small codes say
/// which way it failed. For a SIGNALLED child it is evidence of nothing about the
/// tests — whatever the runner had written when the signal landed is a partial
/// transcript, not a result. `AUTOMATION-294`: a saturated box that gets its test
/// host killed used to report the transcript's half-written per-test rows as a mass
/// regression, which is a non-result rendered as a definite negative.
type TerminatingSignal =
    {
        /// The POSIX signal number (`SIGKILL` = 9).
        Number: int
        /// Its conventional name (`"SIGKILL"`).
        Name: string
    }

module TerminatingSignal =
    /// The signals whose delivery is recognised as "this child was killed".
    ///
    /// A NAMED, CLOSED list, not the whole `129..160` band. Two reasons, and they pull
    /// the same way:
    ///
    ///   * every number here means the SAME signal on Linux and on macOS, so the name
    ///     reported is never a platform guess. The ones that differ (SIGBUS is 10 on
    ///     macOS and 7 on Linux; 10 is SIGUSR1 on Linux) are deliberately ABSENT — an
    ///     unrecognised code falls through to the pre-existing behaviour, which is a
    ///     red, so the omission costs a wrong-but-still-non-green verdict rather than
    ///     inventing a signal name;
    ///   * every number here is far outside the exit codes a test runner CHOOSES.
    ///     MTP's documented codes are single digits, so nothing a runner deliberately
    ///     returns can be mistaken for a signal death. That is the direction that
    ///     matters: a REAL mass failure must never be reported as an abort.
    let private named =
        Map
            [ 1, "SIGHUP"
              2, "SIGINT"
              3, "SIGQUIT"
              4, "SIGILL"
              6, "SIGABRT"
              8, "SIGFPE"
              9, "SIGKILL"
              11, "SIGSEGV"
              13, "SIGPIPE"
              15, "SIGTERM"
              24, "SIGXCPU"
              25, "SIGXFSZ" ]

    /// The shell convention .NET follows on Unix: a child terminated by signal N is
    /// reported through `Process.ExitCode` as `128 + N`.
    [<Literal>]
    let SignalExitBase = 128

    /// Was this exit code produced by a SIGNAL rather than chosen by the program?
    /// `None` for every ordinary exit code, including every code a test runner emits.
    ///
    /// Pure and total, so both directions are assertable without killing a real
    /// process — and `ProcessHelperTests` additionally kills one, so the `128 + N`
    /// convention is a MEASURED fact about this runtime and not a remembered one.
    ///
    /// The convention is POSIX; on Windows nothing produces these codes and a program
    /// that returned one anyway would be reported as an abort rather than a red. Both
    /// are non-green, so the gate still refuses — this can never manufacture a pass.
    let tryOfExitCode (exitCode: int) : TerminatingSignal option =
        named
        |> Map.tryFind (exitCode - SignalExitBase)
        |> Option.map (fun name ->
            { Number = exitCode - SignalExitBase
              Name = name })

/// Was this process TERMINATED by a signal? The `ProcessOutcome`-level form of
/// `TerminatingSignal.tryOfExitCode`.
///
/// `TimedOut` answers `None` deliberately, even though we killed that child ourselves:
/// a timeout already HAS its own outcome, carrying who killed it and whether the kill
/// landed. Folding it in here would replace a specific fact ("it overran its budget")
/// with a vaguer one ("something killed it").
let terminatingSignalOf (outcome: ProcessOutcome) : TerminatingSignal option =
    match outcome with
    | Failed(code, _) -> TerminatingSignal.tryOfExitCode code
    | Succeeded _
    | TimedOut _ -> None

/// Render a capture for a HUMAN — a log line, a plugin status, an error entry. An
/// incomplete drain is NAMED in the rendered text. (`ProcessOutput.text` is the
/// untagged form for text-searching.)
let renderOutput (output: ProcessOutput) : string =
    match output with
    | ProcessOutput.Drained text -> text
    | ProcessOutput.DrainTimedOut(captured, window) ->
        $"%s{captured}\n[fshw] OUTPUT INCOMPLETE: the child exited but its streams never reached EOF within \
          %d{int window.TotalSeconds}s (a grandchild is holding the pipe open, or the read died with it). The text \
          above is what we caught, not what the child said."

/// The operator-facing note for a kill that did NOT happen — empty string when the
/// tree is dead (whether we killed it or it beat us to it).
///
/// Callers that render `outputOf` get it for free; callers that compose their OWN
/// timeout message (DepsFreshness, the plugins) must append it, so a leaked process
/// tree is reported whichever site phrased the timeout.
let renderKill (kill: KillOutcome) : string =
    match kill with
    | KillOutcome.Killed
    | KillOutcome.AlreadyExited -> ""
    | KillOutcome.KillFailed reason ->
        $"\n[fshw] KILL FAILED: %s{reason.GetType().Name}: %s{reason.Message} — we could NOT tear down this process \
          tree, so the child (and any grandchild it spawned) is STILL RUNNING and is no longer being watched. It may \
          hold a lock, a port or a pipe; kill it by hand."

/// The SHORT form of `renderKill`, for the one-line summaries and verdicts a plugin
/// puts on a status line (`"tests: timed out after 30s"`), where the paragraph above
/// will not fit. Empty when the tree is dead. A bare "timed out after 30s" would read
/// as "the runaway is over", which a failed kill makes false.
let renderKillBrief (kill: KillOutcome) : string =
    match kill with
    | KillOutcome.Killed
    | KillOutcome.AlreadyExited -> ""
    | KillOutcome.KillFailed _ -> " (KILL FAILED — process tree STILL RUNNING)"

/// Combined output regardless of outcome — for callers that just want the text
/// to render in a status line. Names a kill we FAILED to perform: every plugin
/// surfaces its diagnostic through here, so this one site reaches an operator no
/// matter which spawn leaked the tree.
let outputOf (outcome: ProcessOutcome) : string =
    match outcome with
    | Succeeded out -> renderOutput out
    | Failed(_, out) -> renderOutput out
    | TimedOut(after, tail, kill) ->
        $"timed out after %d{int after.TotalSeconds}s%s{renderKill kill}\n%s{renderOutput tail}"

/// Exception classes we treat as benign on the timeout-kill path. `Process.Kill`
/// raises InvalidOperationException only when the process has already exited (a
/// race between WaitForExit's timeout and the child's natural exit). Anything else
/// (Win32Exception permission failure, NullReferenceException, etc.) is a real
/// problem and propagates.
let isExpectedKillException (ex: exn) : bool =
    match ex with
    | :? InvalidOperationException -> true
    | _ -> false

/// Exception classes we treat as benign on the post-kill drain path. Task.WaitAll
/// bundles task failures as AggregateException; the underlying stream reads can
/// raise IOException (pipe broken after kill) or ObjectDisposedException (stream
/// closed by the kill). All are expected. Anything else propagates.
let isExpectedDrainException (ex: exn) : bool =
    match ex with
    | :? AggregateException -> true
    | :? IO.IOException -> true
    | :? ObjectDisposedException -> true
    | _ -> false

/// Did a stream pump that has STOPPED reading do so at EOF?
///
/// `None` — the read loop ran to EOF: the stream is exhausted and what we captured
/// from it is complete.
///
/// `Some ex` — the read DIED. On the timeout-kill path that is expected (the kill
/// tears the pipe down under the pump) and means the capture is not provably
/// complete, so this reports `false` and the drain is classified as timed-out.
/// Anything OUTSIDE the expected classes is re-raised on the pump's task rather than
/// laundered into "the child printed nothing".
///
/// Pure so all three arms are covered deterministically — the live paths need a
/// torn-down pipe (exercised end-to-end in FsHotWatch.IntegrationTests).
let internal pumpReachedEof (failure: exn option) : bool =
    match failure with
    | None -> true
    | Some ex when isExpectedDrainException ex -> false
    | Some ex -> raise ex

/// Classify the result of `Process.Kill(entireProcessTree = true)` — `None` for a
/// kill that returned, `Some ex` for one that threw. `isExpectedKillException`
/// decides which throw is the benign already-exited race and which is a real
/// failure.
///
/// Pure, so all three arms — including the failure arm, which live-fire would need a
/// process we are genuinely forbidden to kill — are covered deterministically.
let internal classifyKill (failure: exn option) : KillOutcome =
    match failure with
    | None -> KillOutcome.Killed
    | Some ex when isExpectedKillException ex -> KillOutcome.AlreadyExited
    | Some ex -> KillOutcome.KillFailed ex

/// Tear down a child's process tree and SAY what happened — the whole of the kill
/// policy, with the actual `Process.Kill` injected.
///
/// `kill` is a parameter because the arm that matters is the one where the OS refuses
/// us, and no test can conjure an unkillable process reliably on every platform.
///
/// `describe` is a thunk so the formatting cost is paid only on the bad-news path.
let internal killTreeWith (describe: unit -> string) (kill: unit -> unit) : KillOutcome =
    let outcome =
        try
            kill ()
            KillOutcome.Killed
        with ex ->
            classifyKill (Some ex)

    match outcome with
    | KillOutcome.KillFailed reason ->
        // The returned value carries this fact to the caller; the log is so it also
        // reaches a human who is only reading stderr.
        Logging.error
            "process"
            $"FAILED to kill the process tree for %s{describe ()}: %s{reason.GetType().Name}: %s{reason.Message}. \
              The child and any grandchildren it spawned are STILL RUNNING, and we are about to stop tracking them — \
              they may hold a lock, a port or a pipe. Kill them by hand."
    | KillOutcome.Killed
    | KillOutcome.AlreadyExited -> ()

    outcome

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

/// Env keys that are only meaningful to the parent's in-process hosts (the .NET
/// host, the in-process MSBuild ProjInfo stood up) and poison spawned children if
/// inherited. Stripped unconditionally on every spawn: they're a no-op for
/// non-dotnet children, and a spawned `dotnet` re-resolves the correct values from
/// its own argv[0] / SDK. Caller-supplied overrides still win — the strip runs
/// before the env overlay.
///
/// Arch-specific DOTNET_ROOT_* — the .NET host writes these into its parent's env
/// from argv[0]'s dir so child apphosts inherit the same runtime. On Nix-wrapped
/// SDKs (and similar shims) that dir lacks shared/Microsoft.NETCore.App, so a child
/// dotnet trusts the value and fails to find the runtime.
///
/// Ionide.ProjInfo MSBuild discovery (MSBUILD_EXE_PATH / MSBuildExtensionsPath /
/// MSBuildSDKsPath) — `Init.init` writes these into the daemon's OWN process
/// environment for in-process design-time evaluation, pinning MSBuild at the SDK
/// band ProjInfo selected at startup. Inherited by a spawned `dotnet build`, they
/// force the child's MSBuild to that band even when the muxer resolves a different
/// (or, on a multi-SDK box, incomplete) one — restore-graph generation then fails
/// with exit 1 and ZERO diagnostics ("Build FAILED / 0 Error(s)") while a
/// plain-shell build of the same tree is clean. See docs/leaked-msbuild-env-bug.md.
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
    // directory containing shared/Microsoft.NETCore.App. A no-op on normal
    // installs. On Nix-wrapped SDKs the wrapper bin/ has no shared/ sibling but
    // the unwrapped target does — without this, child apphosts die with
    // "apphost_version not found" because the muxer reads DOTNET_HOST_PATH
    // literally. See memory/dotnet_tool_launcher_truncates_nix_profiles.md.
    //
    // Applied AFTER the explicit env overlay so callers passing DOTNET_HOST_PATH
    // explicitly also get the symlink resolved — which lets tests exercise the
    // contract via explicit env rather than mutating process env, which would race
    // other tests' subprocess spawns.
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
// ONE spawn primitive.
//
// `runProcess` is the ONLY spawn: it always polls `HasExited` and always bounds
// the post-exit drain, closing two wedges an unbounded wait leaves open:
//
//   * `WaitForExit(-1)` never returns if a machine sleep kills the child
//     mid-launch — nothing raises, the plugin stays `Running` forever.
//   * `Task.WaitAll(stdout, stderr)` on the SUCCESS path never returns if the
//     child exited but a GRANDCHILD (an MSBuild node, a Playwright driver)
//     inherited the stdout pipe and outlives it — EOF never comes. Reachable from
//     any hook / build / fileCommand.
//
// What varies per call site is `ProcessBounds`, which cannot express "wait forever
// with no escape".
// ---------------------------------------------------------------------------

/// Raised when a launched child never becomes a live, progressing process: no
/// output and no exit within the launch deadline. The message names the elapsed
/// launch budget; TestPrune catches it and drives the run's `Aborted` lifecycle so
/// `check` exits non-green with a "re-run when quiet" diagnostic rather than wedging.
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

/// The terminal verdict of the launch-watchdog loop. Distinct from `LaunchStep` so
/// the loop's result can never be `KeepWaiting` and the production match has no
/// dead arm.
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
/// spawning a real process.
///
/// Either deadline may be `InfiniteTimeSpan`, which DISABLES that one:
///  * `overallTimeout = Infinite` — no total cap (the common TestPrune case),
///    leaving the launch deadline as the sole escape from an infinite wait.
///  * `launchDeadline = Infinite` — output cannot prove liveness for this child
///    (a silent `dotnet build`), so a no-output window means nothing and only the
///    overall timeout can end the wait. `ProcessBounds.silent` is the only way to
///    ask for this, and it demands a finite total timeout in exchange.
///
/// The `Infinite` handling must be explicit: `InfiniteTimeSpan` is -1 ms, so
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
/// blocks forever on that pipe even though the process itself is gone. So we drain
/// only for a bounded window, then proceed with whatever output was captured; the
/// verdict rides on the exit code, not on stream EOF.
///
/// A wall clock is the only sound bound here: a pipe a grandchild holds open has no
/// work left to wait for, so no work-completion signal can arrive. The clock measures
/// the PIPE rather than the thread pool only because the pumps own dedicated threads
/// (below) — a saturated pool would otherwise starve the reader and expire the window
/// having read nothing. An expired window reports `ProcessOutput.DrainTimedOut`, not
/// `""`.
let internal PostExitDrainWindow = TimeSpan.FromSeconds 2.0

/// The bounds ONE spawned child runs under. Construct only via
/// `ProcessBounds.streaming` / `ProcessBounds.silent` — the fields are private so a
/// call site cannot assemble "no bound at all" out of two `InfiniteTimeSpan`s.
///
/// The two constructors encode a property of the CHILD (does its output prove it is
/// alive?), not two safety levels — every spawn, either way, polls `HasExited` and
/// bounds its post-exit drain.
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
/// A process that EXITS — for any reason, with any code — is classified by its exit
/// code: a nonzero exit with no output is a genuine test failure / zero-match,
/// indistinguishable from a spawn-death at the process boundary, so it must NOT be
/// force-aborted. Only a `ProcessBounds.streaming` child that has neither exited nor
/// emitted a byte within its launch deadline raises `LaunchStalledException`.
///
/// Reads stdout/stderr incrementally so the FIRST byte is observed as liveness.
/// For `dotnet` commands, injects `MSBUILDDISABLENODEREUSE=1` unless the caller
/// already set it (via `makeChildProcessStartInfo`).
///
/// `sink`, when given, receives every chunk AS IT ARRIVES, on the pump thread,
/// before the call returns. That ordering is the point: the returned `ProcessOutcome`
/// only exists for a child we outlived, so it is unavailable exactly where evidence
/// matters most — a child SIGKILLed on timeout, whose capture the kill truncates
/// (`DrainTimedOut`), and a daemon that dies mid-run and returns nothing at all.
///
/// A throwing sink is DISABLED, never fatal and never laundered into the capture:
/// it may not turn a complete drain into a `DrainTimedOut` (the pump's own
/// `failure` latch means "the STREAM died"), and a full disk may not fail a test
/// run. The first throw is logged; the rest are silent.
let runProcessTo
    (sink: (string -> unit) option)
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

    // Read ONCE, while the handle is certainly live: this is what an operator needs
    // to hunt down a tree we failed to kill, and it must still be reportable on the
    // path where everything else about the child has gone wrong.
    let pid = proc.Id

    // Incremental output capture via explicit stream pumps. The event API
    // (`BeginOutputReadLine`) is not usable here: draining it requires the
    // parameterless `WaitForExit()` (the timed overload does NOT flush the async
    // handlers), which is the unbounded grandchild-pipe-wedging wait we must avoid.
    // A chunk-at-a-time `Read` loop gives a Task handle we can bound-wait AND flips
    // a latch on the FIRST byte — the liveness signal the launch deadline keys off
    // (`ReadToEnd` only returns at EOF, which a wedged launch never reaches).
    let output = StringBuilder()
    let outputLock = obj ()
    let mutable sawOutput = 0
    let mutable sinkBroken = false

    // Fed from inside `outputLock`, so the sink sees the chunks in the SAME order
    // the in-memory capture does and the two pumps' writes are serialised against
    // each other — a caller's file and `ProcessOutput.text` can never disagree
    // about what the child said or in what order.
    let emit (chunk: string) =
        match sink with
        | None -> ()
        | Some write when not sinkBroken ->
            try
                write chunk
            with ex ->
                sinkBroken <- true

                Logging.warn
                    "process"
                    $"output sink for `%s{command}` failed and is now DISABLED for this run: \
                      %s{ex.GetType().Name}: %s{ex.Message}. The in-memory capture is unaffected, but whatever \
                      the sink was writing (a streamed run log) is now INCOMPLETE."
        | Some _ -> ()

    // Each pump owns a DEDICATED thread (`LongRunning`) and reads SYNCHRONOUSLY.
    //
    // A `task {}` over `ReadAsync` schedules every continuation on the thread pool,
    // and under a saturated pool — a `check` running the full suite in parallel,
    // exactly when a spawn's output matters most — the reader may never run, the 2 s
    // drain window expires having read zero bytes, and the child's output comes back
    // as `""`: the clock measuring the POOL, not the process.
    //
    // Returns TRUE iff the loop ended at EOF — the stream is exhausted and what we
    // captured from it is all there ever was. See `pumpReachedEof`.
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
                            let chunk = String(buf, 0, n)

                            lock outputLock (fun () ->
                                output.Append(chunk) |> ignore
                                emit chunk)
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
    // this block, and then only for the window. An expired window rides out on the
    // value as `DrainTimedOut` so it cannot be mistaken for a child that said
    // nothing.
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

        // A killed tree still needs draining so partial output is reported. The
        // kill's OUTCOME is returned, never discarded: a tree we could not tear down
        // is still running. The policy lives in `killTreeWith`.
        let killTree () : KillOutcome =
            killTreeWith (fun () -> $"`%s{command} %s{args}` (pid %d{pid})") (fun () ->
                proc.Kill(entireProcessTree = true))

        match outcome with
        | LaunchOutcome.Exited ->
            let out = drainPumps ()

            if proc.ExitCode = 0 then
                Succeeded out
            else
                Failed(proc.ExitCode, out)
        | LaunchOutcome.TimedOut ->
            let killed = killTree ()
            TimedOut(timeout, drainPumps (), killed)
        | LaunchOutcome.Stalled ->
            // The exception below is the diagnostic; a kill that FAILED here is
            // still logged by `killTree` itself, so the leaked tree is reported even
            // though this arm throws.
            killTree () |> ignore

            // A stall is DEFINED as "not one byte within the launch deadline", so
            // there is no capture to report: this runs only to let the pumps close
            // their pipes, and the (necessarily empty) result is discarded.
            drainPumps () |> ignore

            raise (
                LaunchStalledException(
                    $"launch produced no live process within %d{int launchDeadline.TotalSeconds}s — box overloaded or process died at spawn; re-run when quiet"
                )
            )
    finally
        ProcessRegistry.untrack proc

/// THE spawn, with no output sink — `runProcessTo None`. This is the shape every
/// caller that only wants the child's verdict and its capture should use.
let runProcess
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome =
    runProcessTo None command args workDir env bounds

/// Run a synchronous unit of work with a wall-clock timeout, threading a
/// `CancellationToken` into the work so a timed-out unit is ACTUALLY cancelled
/// rather than orphaned. The token is cancelled the instant the wait expires;
/// cooperative work (anything that polls `ct.IsCancellationRequested` or calls
/// `ct.ThrowIfCancellationRequested()`, or an `Async` driven under the token)
/// then unwinds and releases whatever lock it held — closing the "stuck unit
/// times out the WAIT but the runaway thread keeps holding a lock → daemon
/// stays wedged" hole.
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
