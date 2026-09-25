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
    /// The kill CALL never RETURNED inside the teardown budget: no success, no
    /// refusal, no answer at all. We stopped waiting so this run could still reach a
    /// verdict — an unbounded wait here is a phase that cannot finish, and a phase
    /// that cannot finish cannot report.
    ///
    /// Distinct from `KillFailed`: that is the OS telling us no, this is the OS
    /// telling us nothing. Both leave the tree unaccounted for and both fail closed,
    /// but only one of them has a reason to give.
    | KillTimedOut of budget: TimeSpan

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
/// transcript, not a result. The bug this fixes: a saturated box that gets its test
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

/// Render a teardown budget the way an operator reads it: whole seconds when it is at
/// least one, milliseconds when it is shorter. A 250 ms budget rendered as `int
/// TotalSeconds` is the string "0s" — a diagnostic that hides its own number, and
/// reads as "we did not wait at all".
let internal renderBudget (budget: TimeSpan) : string =
    if budget.TotalSeconds >= 1.0 then
        $"%d{int budget.TotalSeconds}s"
    else
        $"%d{int budget.TotalMilliseconds}ms"

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
    | KillOutcome.KillTimedOut budget ->
        $"\n[fshw] KILL TIMED OUT: tearing down this process tree did not return within %s{renderBudget budget}, \
          so we stopped waiting in order to report this run at all. We never \
          established that the child (or any grandchild it spawned) is dead, and it is no longer being watched — \
          assume it is STILL RUNNING. It may hold a lock, a port or a pipe; kill it by hand."

/// The SHORT form of `renderKill`, for the one-line summaries and verdicts a plugin
/// puts on a status line (`"tests: timed out after 30s"`), where the paragraph above
/// will not fit. Empty when the tree is dead. A bare "timed out after 30s" would read
/// as "the runaway is over", which a failed kill makes false.
let renderKillBrief (kill: KillOutcome) : string =
    match kill with
    | KillOutcome.Killed
    | KillOutcome.AlreadyExited -> ""
    | KillOutcome.KillFailed _ -> " (KILL FAILED — process tree STILL RUNNING)"
    | KillOutcome.KillTimedOut _ -> " (KILL TIMED OUT — process tree UNACCOUNTED FOR)"

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

/// THE SECOND BUDGET. A run's own timeout bounds how long the child may take; this
/// bounds how long TEARING IT DOWN may take, and it exists because those are
/// different failures with the same symptom.
///
/// `Process.Kill(entireProcessTree = true)` is not a signal, it is a WALK: the
/// runtime enumerates the tree (a `/proc` scan on Linux, a `sysctl` process-table
/// snapshot on macOS) and kills it from the leaves up. A walk over a starved box, or
/// over a table the kernel is contending on, can take arbitrarily long — and an
/// arbitrarily long teardown makes every phase downstream of it arbitrarily long too:
/// the project task never returns, `Async.Parallel` never completes, the plugin never
/// emits `TestRunCompleted`, and the only thing that eventually ends the run is the
/// daemon's hour-scale wedge watchdog, which reports nothing about what happened.
///
/// 10s is far past any healthy teardown (a real tree dies in milliseconds) and far
/// short of the watchdog. A kill that has not returned in 10s is not coming back, and
/// waiting longer buys evidence nobody will read.
let internal TeardownBudget = TimeSpan.FromSeconds 10.0

/// What the kill CALL did — the raw, unclassified fact, separated from what it MEANS
/// so `classifyKill` can stay pure and total.
[<RequireQualifiedAccess; NoComparison>]
type internal KillCall =
    /// The call returned without throwing.
    | Returned
    /// The call threw. Whether that is benign is `isExpectedKillException`'s call.
    | Threw of exn
    /// The call did not return inside `budget`. We are no longer waiting on it, and
    /// the thread it is running on is abandoned.
    | DidNotReturn of budget: TimeSpan

/// Run a process-tree kill under a wall-clock budget and report what the CALL did.
///
/// The kill runs on a DEDICATED thread (`LongRunning`), never a pool item. The whole
/// reason this bound exists is a box loaded flat enough that a walk of the process
/// table stalls — exactly the state in which a queued pool item may never be
/// scheduled at all. Reporting "the kill did not return" for a kill that never
/// STARTED would be a different lie in the same shape as the one we are fixing.
///
/// A budget that expires ABANDONS the thread rather than aborting it: there is no
/// safe way to interrupt a thread inside a syscall, and the caller's problem is that
/// it needs to report NOW, not that the kill must stop. If the OS eventually answers,
/// nobody is listening — which is why the tree is booked as leaked either way.
let internal callKillWithin (budget: TimeSpan) (kill: unit -> unit) : KillCall =
    let attempt =
        Task.Factory.StartNew(
            (fun () ->
                try
                    kill ()
                    None
                with ex ->
                    Some ex),
            TaskCreationOptions.LongRunning
        )

    if attempt.Wait(int budget.TotalMilliseconds) then
        match attempt.Result with
        | None -> KillCall.Returned
        | Some ex -> KillCall.Threw ex
    else
        KillCall.DidNotReturn budget

/// Classify what the kill CALL did into what it means for the tree.
/// `isExpectedKillException` decides which throw is the benign already-exited race
/// and which is a real failure.
///
/// Pure, so all four arms — including the failure arm, which live-fire would need a
/// process we are genuinely forbidden to kill, and the no-answer arm, which would
/// need an unkillable one — are covered deterministically.
let internal classifyKill (call: KillCall) : KillOutcome =
    match call with
    | KillCall.Returned -> KillOutcome.Killed
    | KillCall.Threw ex when isExpectedKillException ex -> KillOutcome.AlreadyExited
    | KillCall.Threw ex -> KillOutcome.KillFailed ex
    | KillCall.DidNotReturn budget -> KillOutcome.KillTimedOut budget

/// Tear down a child's process tree WITHIN `budget` and SAY what happened — the whole
/// of the kill policy, with the actual `Process.Kill` injected.
///
/// `kill` is a parameter because the two arms that matter are the one where the OS
/// refuses us and the one where it never answers, and no test can conjure a process
/// that is unkillable — or slow to kill — reliably on every platform.
///
/// `pid` is passed as data as well as appearing inside `describe ()`: a tree we could
/// not account for is registered with `ProcessRegistry` so shutdown can name it, and
/// a pid buried in prose cannot be registered.
///
/// Instrumented at both ends. The begin line is what the original report's evidence was
/// missing: with only "the project timed out" retained, a run that stopped inside the
/// teardown is indistinguishable from one that stopped before reaching it, and the
/// blocked project cannot be named from the log after the fact.
let internal killTreeWith (budget: TimeSpan) (pid: int) (describe: unit -> string) (kill: unit -> unit) : KillOutcome =
    let what = describe ()

    Logging.info "process" $"killing process tree for %s{what} (teardown budget %s{renderBudget budget})"

    let clock = Stopwatch.StartNew()
    let outcome = classifyKill (callKillWithin budget kill)
    let took = int clock.ElapsedMilliseconds

    // The returned value carries the outcome to the caller; these lines are so it
    // also reaches a human who is only reading stderr — and so the DURATION of the
    // teardown survives in the log whether or not the run produced a verdict.
    match outcome with
    | KillOutcome.Killed -> Logging.info "process" $"killed the process tree for %s{what} in %d{took}ms"
    | KillOutcome.AlreadyExited ->
        Logging.info "process" $"process tree for %s{what} had already exited (%d{took}ms) — nothing to kill"
    | KillOutcome.KillFailed reason ->
        Logging.error
            "process"
            $"FAILED to kill the process tree for %s{what} after %d{took}ms: %s{reason.GetType().Name}: \
              %s{reason.Message}. The child and any grandchildren it spawned are STILL RUNNING, and we are about to \
              stop tracking them — they may hold a lock, a port or a pipe. Kill them by hand."

        ProcessRegistry.reportLeak pid what $"the kill was refused (%s{reason.GetType().Name}: %s{reason.Message})"
    | KillOutcome.KillTimedOut _ ->
        Logging.error
            "process"
            $"TIMED OUT killing the process tree for %s{what}: the kill did not return within \
              %s{renderBudget budget}. We are no longer waiting on it, so this run can still report a verdict, \
              but we never established that the child or its grandchildren are dead — assume they are STILL RUNNING. \
              They may hold a lock, a port or a pipe. Kill them by hand."

        ProcessRegistry.reportLeak pid what $"the kill did not return within %s{renderBudget budget}"

    outcome

/// One row of the OS process table, as `ps -A -o pid=,ppid=,stat=,command=` prints it.
///
/// The COMMAND rides along with the pid because a pid alone cannot tell "the same
/// process, still running" from "a recycled pid, somebody else's process" — and a
/// report that names a stranger as our leak sends an operator to kill the wrong thing.
type internal ProcessRow =
    {
        Pid: int
        ParentPid: int
        /// Exited but not yet reaped. Dead: it holds no lock, port or pipe.
        Zombie: bool
        Command: string
    }

/// Parse `ps -A -o pid=,ppid=,stat=,command=` output. Lines that do not start with two
/// integers and a state are skipped — a header, a blank line, a truncated tail.
let internal parseProcessTable (text: string) : ProcessRow list =
    text.Split('\n')
    |> Array.choose (fun line ->
        let parts =
            line.Trim().Split([| ' '; '\t' |], 4, StringSplitOptions.RemoveEmptyEntries)

        match parts with
        | [| pid; ppid; stat; command |] ->
            match Int32.TryParse pid, Int32.TryParse ppid with
            | (true, p), (true, pp) ->
                Some
                    { Pid = p
                      ParentPid = pp
                      Zombie = stat.StartsWith "Z"
                      Command = command.Trim() }
            | _ -> None
        | _ -> None)
    |> Array.toList

/// The root (when it is still in the table) followed by every process descended from
/// it by parent pid, transitively — the tree `Kill(entireProcessTree = true)` aims at.
///
/// A descendant that was re-parented away BEFORE this snapshot (a double fork) is not
/// in it: the table no longer links it to the root, and nothing else does either.
let internal treeOf (rootPid: int) (rows: ProcessRow list) : ProcessRow list =
    let children = rows |> List.groupBy _.ParentPid |> Map.ofList

    let rec below pid =
        match Map.tryFind pid children with
        | Some kids -> kids |> List.collect (fun kid -> kid :: below kid.Pid)
        | None -> []

    let root = rows |> List.filter (fun r -> r.Pid = rootPid)
    // A zombie has already exited: nothing to aim a kill at, nothing that can leak.
    root @ below rootPid |> List.filter (fun r -> not r.Zombie)

/// What tearing down a timed-out child's tree ESTABLISHED, as data a caller can put in
/// its report — the kill outcome alone says what the kill CALL did, not what is left.
[<NoComparison>]
type internal TreeTeardown =
    {
        RootPid: int
        /// The root and its descendants read from the process table immediately BEFORE
        /// the kill. `Error` = the table could not be read, so the tree is unknown.
        Tree: Result<ProcessRow list, string>
        /// How long the kill call took (or was waited on, for `KillTimedOut`).
        KillTook: TimeSpan
        Kill: KillOutcome
        /// Members of `Tree` still alive once the settle window is spent. `Error` = we
        /// could not establish it either way — never to be read as "none".
        Survivors: Result<ProcessRow list, string>
    }

/// Snapshot the tree, kill it, then poll each member's liveness until every one is
/// gone or `settleAttempts` polls have been spent (`pause` between them) — and name
/// whatever is left. A kill that RETURNED has only delivered signals; the poll is what
/// establishes that the processes are actually gone, and it stops the moment they are.
///
/// The tree must be read BEFORE the kill: afterwards a surviving descendant has been
/// re-parented to init and no walk from the root can find it.
///
/// A member still alive after the window is reported even if it is only a zombie its
/// parent has not reaped — a parent that is not reaping is itself a survivor, and a
/// false "nothing leaked" is the failure this exists to prevent. A pid recycled inside
/// the (sub-second) window would read as alive; that errs the same way.
///
/// The table reader, the liveness probe, the pause and the kill are injected so the
/// throwing, blocking and survivor arms are all deterministic.
let internal accountTeardown
    (readTable: unit -> Result<ProcessRow list, string>)
    (isAlive: int -> Result<bool, string>)
    (settleAttempts: int)
    (pause: unit -> unit)
    (rootPid: int)
    (kill: unit -> KillOutcome)
    : TreeTeardown =
    let tree = readTable () |> Result.map (treeOf rootPid)
    let clock = Stopwatch.StartNew()
    let outcome = kill ()
    let took = clock.Elapsed

    let rec stillAlive (members: ProcessRow list) (alive: ProcessRow list) =
        match members with
        | [] -> Ok(List.rev alive)
        | m :: rest ->
            match isAlive m.Pid with
            | Ok true -> stillAlive rest (m :: alive)
            | Ok false -> stillAlive rest alive
            | Error reason -> Error $"could not tell whether pid %d{m.Pid} is still running (%s{reason})"

    let rec settle attempt (members: ProcessRow list) =
        match stillAlive members [] with
        | Error reason -> Error reason
        | Ok left when List.isEmpty left || attempt >= settleAttempts -> Ok left
        | Ok left ->
            pause ()
            settle (attempt + 1) left

    let survivors =
        match tree with
        | Error reason -> Error $"the process table could not be read before the kill (%s{reason})"
        | Ok members -> settle 1 members

    { RootPid = rootPid
      Tree = tree
      KillTook = took
      Kill = outcome
      Survivors = survivors }

[<RequireQualifiedAccess>]
module private Libc =
    [<System.Runtime.InteropServices.DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

/// `kill(pid, 0)`: signal 0 delivers nothing and only checks the pid. 0 = it exists;
/// ESRCH = no such process; EPERM = it exists but is not ours to signal (alive).
///
/// Not `ps -p <pid>`: on macOS its EXIT CODE is 1 for a live pid it is not entitled to
/// inspect, which would read a survivor as dead. Called directly, not via `kill -0`,
/// so the settle poll spawns nothing on a box that is already overloaded.
let internal isProcessAlive (pid: int) : Result<bool, string> =
    try
        if Libc.kill (pid, 0) = 0 then
            Ok true
        else
            match System.Runtime.InteropServices.Marshal.GetLastPInvokeError() with
            | 3 -> Ok false // ESRCH
            | 1 -> Ok true // EPERM
            | errno -> Error $"kill(%d{pid}, 0) failed with errno %d{errno}"
    with ex ->
        Error $"kill(2) is unavailable: %s{ex.GetType().Name}: %s{ex.Message}"

/// How long ONE read of the process table may take. `ps` returns in milliseconds; a
/// box that cannot answer in this long is reported as "tree unknown", not waited on.
let internal ProcessTableBudget = TimeSpan.FromSeconds 3.0

/// Read the process table with `ps`, bounded by `ProcessTableBudget`. Spawned directly
/// rather than through `runProcess`: this runs INSIDE a teardown — possibly one the
/// process registry is performing at shutdown, when it refuses new admissions.
let internal readProcessTable () : Result<ProcessRow list, string> =
    try
        let psi =
            ProcessStartInfo(
                "ps",
                "-A -o pid=,ppid=,stat=,command=",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        use ps = Process.Start psi
        let text = ps.StandardOutput.ReadToEndAsync()

        if
            ps.WaitForExit(int ProcessTableBudget.TotalMilliseconds)
            && text.Wait ProcessTableBudget
        then
            if ps.ExitCode = 0 then
                Ok(parseProcessTable text.Result)
            else
                Error $"`ps` exited %d{ps.ExitCode}"
        else
            (try
                ps.Kill true
             with _ ->
                 ())

            Error $"`ps` did not answer within %s{renderBudget ProcessTableBudget}"
    with ex ->
        Error $"`ps` could not run: %s{ex.GetType().Name}: %s{ex.Message}"

/// Settle window after a tree kill: up to `SettleAttempts` liveness polls, `SettlePause`
/// apart, ending early the moment the tree is gone. A SIGKILLed process is gone in
/// milliseconds; one still alive after ~2s is not dying.
let internal SettleAttempts = 40
let internal SettlePause = TimeSpan.FromMilliseconds 50.0

/// The pause between settle polls in production. A named function, not a lambda at the
/// call site: that lambda ran only when a SIGKILLed tree member still answered the
/// first liveness poll (a zombie its parent had not yet reaped), so whether it ever
/// executed was a race — and so was its coverage. Here it is testable directly.
let internal settlePause () = Thread.Sleep SettlePause

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
/// or paths ending in either). Retained as a public command-classification helper;
/// the node-reuse guard itself now applies before every child process because a
/// non-dotnet wrapper can launch dotnet later.
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

/// Merge `MSBUILDDISABLENODEREUSE=1` into every child env unless the caller
/// already set the key. A shell or other wrapper may launch `dotnet` after the
/// immediate child starts, so deciding from the first executable loses the guard
/// at exactly that process boundary. See docs/msbuild-node-reuse-bug.md.
let mergeDotnetEnv (_command: string) (env: (string * string) list) : (string * string) list =
    if not (env |> List.exists (fun (k, _) -> k = "MSBUILDDISABLENODEREUSE")) then
        ("MSBUILDDISABLENODEREUSE", "1") :: env
    else
        env

// ---------------------------------------------------------------------------
// The child's argument string.
//
// `ProcessStartInfo.Arguments` is ONE string the runtime word-splits: double quotes
// group, a backslash escapes only a following quote (2n backslashes + `"` is n
// backslashes then a quote boundary; 2n+1 is n backslashes and a literal quote),
// everything else is literal. A value containing whitespace — a backticked
// sentence-style test module name, say — MUST be quoted by that rule or it lands on
// the child as several arguments. `quoteArg` is the writer and `splitArgs` the
// reader for that one rule; every builder of a runner arg string uses `quoteArg`.
// ---------------------------------------------------------------------------

/// Quote ONE argument for `ProcessStartInfo.Arguments`. An argument with no
/// whitespace and no double quote is returned byte-for-byte unchanged; anything else
/// (the empty string included) is wrapped in double quotes with embedded quotes and
/// the backslashes that precede them escaped so `splitArgs` yields it back verbatim.
let quoteArg (arg: string) : string =
    let needsQuotes =
        arg.Length = 0 || arg |> Seq.exists (fun c -> Char.IsWhiteSpace c || c = '"')

    if not needsQuotes then
        arg
    else
        let sb = StringBuilder()
        sb.Append('"') |> ignore
        let mutable index = 0

        while index < arg.Length do
            let start = index

            while index < arg.Length && arg[index] = '\\' do
                index <- index + 1

            let slashCount = index - start

            if index = arg.Length then
                // Trailing backslashes precede the closing quote: double them so
                // they stay literal instead of escaping it.
                sb.Append('\\', slashCount * 2) |> ignore
            elif arg[index] = '"' then
                sb.Append('\\', slashCount * 2 + 1).Append('"') |> ignore
                index <- index + 1
            else
                sb.Append('\\', slashCount).Append(arg[index]) |> ignore
                index <- index + 1

        sb.Append('"').ToString()

/// Split a `ProcessStartInfo.Arguments` string into the tokens the child receives —
/// the inverse of `quoteArg`. Single quotes are ordinary characters. An unfinished
/// quote is not a partial command line, so the split fails closed (`None`).
let splitArgs (args: string) : string[] option =
    if String.IsNullOrWhiteSpace args then
        Some [||]
    else
        let tokens = ResizeArray<string>()
        let token = StringBuilder()
        let mutable tokenStarted = false
        let mutable inQuotes = false
        let mutable index = 0

        let flush () =
            if tokenStarted then
                tokens.Add(token.ToString())
                token.Clear() |> ignore
                tokenStarted <- false

        while index < args.Length do
            match args[index] with
            | '\\' ->
                let start = index

                while index < args.Length && args[index] = '\\' do
                    index <- index + 1

                let slashCount = index - start

                if index < args.Length && args[index] = '"' then
                    token.Append('\\', slashCount / 2) |> ignore
                    tokenStarted <- true

                    if slashCount % 2 = 0 then
                        inQuotes <- not inQuotes
                    else
                        token.Append('"') |> ignore

                    index <- index + 1
                else
                    token.Append('\\', slashCount) |> ignore
                    tokenStarted <- true
            | '"' ->
                tokenStarted <- true
                inQuotes <- not inQuotes
                index <- index + 1
            | character when Char.IsWhiteSpace character && not inQuotes ->
                flush ()
                index <- index + 1
            | character ->
                token.Append(character) |> ignore
                tokenStarted <- true
                index <- index + 1

        if inQuotes then
            None
        else
            flush ()
            Some(tokens.ToArray())

/// True when `path` is a file this user may execute.
/// Whether an existing file may be run: on Windows any file (it has no execute bit),
/// elsewhere one with an execute bit set. Decided once for the platform.
let private runnable: string -> bool =
    if OperatingSystem.IsWindows() then
        fun _ -> true
    else
        let execute =
            IO.UnixFileMode.UserExecute
            ||| IO.UnixFileMode.GroupExecute
            ||| IO.UnixFileMode.OtherExecute

        fun path -> IO.File.GetUnixFileMode path &&& execute <> IO.UnixFileMode.None

let private isExecutableFile (path: string) = IO.File.Exists path && runnable path

/// The executable a bare `command` names on `path` (a PATH value): the first
/// directory holding an executable file of that name. `None` when `command` is bare and
/// nothing on `path` matches; a command that names a location is its own answer.
let tryResolveOnPath (path: string) (command: string) : string option =
    if IO.Path.GetFileName command <> command then
        Some command
    else
        path.Split(IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun dir -> IO.Path.Combine(dir, command))
        |> Array.tryFind isExecutableFile

/// `tryResolveOnPath`, leaving an unresolved command as it was.
let resolveOnPath (path: string) (command: string) : string =
    tryResolveOnPath path command |> Option.defaultValue command

/// The command a child is started with. Inside a session of a repository host, a bare
/// command is looked up on the SESSION's PATH: the runtime would look it up on this
/// process's own, which belongs to whichever worktree launched the host, and a
/// worktree's wrappers (its toolchain-bin, say) would silently be another's.
let private sessionCommand (command: string) : string =
    match SessionScope.SessionEnvironment.current () with
    | None -> command
    | Some session ->
        let path =
            SessionScope.SessionEnvironment.variables session
            |> Map.tryFind "PATH"
            |> Option.defaultValue ""

        match tryResolveOnPath path command with
        | Some resolved -> resolved
        | None ->
            raise (
                System.ComponentModel.Win32Exception(2, $"%s{command}: not found on this worktree's PATH (%s{path})")
            )

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
    let psi = ProcessStartInfo(sessionCommand command, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- workDir

    // A session of a repository host spawns from ITS client's environment, not from
    // the host process's, which belongs to whichever worktree launched the host.
    match SessionScope.SessionEnvironment.current () with
    | Some session ->
        psi.Environment.Clear()

        for KeyValue(key, value) in SessionScope.SessionEnvironment.variables session do
            psi.Environment[key] <- value
    | None -> ()

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

/// Everything between admitting a child and the watchdog's decision about it runs
/// under this guard. A failure there (a pump that cannot start, an observation that
/// throws) tears the child down before propagating, instead of leaving an admitted
/// child running with nothing left watching it. After the decision, every arm owns
/// the child itself.
let internal killIfUndecided (kill: unit -> 'Killed) (decide: unit -> 'T) : 'T =
    try
        decide ()
    with _ ->
        kill () |> ignore
        reraise ()

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
///
/// `accounted`: when true, a timeout's teardown also snapshots the process tree before
/// the kill and names its survivors after it (`accountTeardown`), returned alongside
/// the outcome. `runProcessTo` passes false; `runProcessAccounted` passes true.
///
/// `onStarted` receives the child's pid once it is admitted, before the call waits on
/// it: the one moment a caller can say which process it is waiting on while it waits.
/// A throwing observer is logged and otherwise ignored, like a throwing sink — the child
/// is already running and must stay watched.
let internal runProcessCore
    (accounted: bool)
    (sink: (string -> unit) option)
    (onStarted: int -> unit)
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome * TreeTeardown option =
    let timeout = bounds.Timeout
    let launchDeadline = bounds.LaunchDeadline

    // A process scope that has shut down refuses the launch BEFORE the target can have
    // any side effect. Admission re-checks after the spawn, for a shutdown in between.
    ProcessRegistry.ensureAdmitting $"`%s{command} %s{args}`"

    let psi = makeChildProcessStartInfo command args workDir env

    use proc = Process.Start(psi)

    // Read ONCE, while the handle is certainly live: this is what an operator needs
    // to hunt down a tree we failed to kill, and it must still be reportable on the
    // path where everything else about the child has gone wrong.
    let pid = proc.Id

    // Register so shutdown can tear down in-flight children. A scope that shut down
    // while this child was starting has already reaped it, and refuses it here.
    ProcessRegistry.admitOrRefuse proc $"`%s{command} %s{args}` (pid %d{pid})"

    try
        onStarted pid
    with ex ->
        Logging.warn
            "process"
            $"start observer for `%s{command}` (pid %d{pid}) failed: %s{ex.GetType().Name}: %s{ex.Message}"

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
    //
    // The pumps run on `TaskScheduler.Default`, never the CALLER's scheduler. A
    // parameterless `StartNew` inherits `TaskScheduler.Current`, and a caller running
    // on a scheduler it is itself blocking — this very call parks it in the watchdog
    // loop — would never start them: the child's output would go unread and a healthy
    // run would come back as a drain timeout.
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
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        )

    let drainedOutput () =
        lock outputLock (fun () -> output.ToString().Trim())

    // A killed tree still needs draining so partial output is reported. The
    // kill's OUTCOME is returned, never discarded: a tree we could not tear down
    // is still running. The policy — including the teardown budget that keeps a
    // blocked kill from wedging the whole run — lives in `killTreeWith`.
    let describe () = $"`%s{command} %s{args}` (pid %d{pid})"

    let plainKill () : KillOutcome =
        killTreeWith TeardownBudget pid describe (fun () -> proc.Kill(entireProcessTree = true))

    let teardown: TreeTeardown option ref = ref None

    // The accounted kill: same policy, bracketed by process-table reads so the caller
    // can NAME the tree and what survived it. Survivors are booked with the registry,
    // exactly like a kill that failed outright — they are just as unwatched.
    let killTree () : KillOutcome =
        if accounted then
            let t =
                accountTeardown readProcessTable isProcessAlive SettleAttempts settlePause pid plainKill

            match t.Survivors with
            | Ok survivors ->
                for s in survivors do
                    ProcessRegistry.reportLeak
                        s.Pid
                        $"`%s{s.Command}` (pid %d{s.Pid}, in the tree of %s{describe ()})"
                        "it was still running after the tree kill"
            | Error _ -> ()

            teardown.Value <- Some t
            t.Kill
        else
            plainKill ()

    let pollMs = 250

    let outcome =
        try
            let stdoutTask, stderrTask, outcome =
                killIfUndecided killTree (fun () ->
                    let stdoutTask = pump proc.StandardOutput
                    let stderrTask = pump proc.StandardError

                    // The "sleep" between polls IS a bounded `WaitForExit`: it wakes early
                    // the instant the child exits (so completion is observed promptly) but
                    // caps at `pollMs` so the launch/overall deadlines are still checked
                    // regularly. `observe` reads the independent liveness handle
                    // (`HasExited`) — the poll that closes the machine-sleep hole where a
                    // single blocking wait never returned.
                    let observe () =
                        proc.HasExited, (Volatile.Read &sawOutput = 1)

                    let sleep ms = proc.WaitForExit(ms: int) |> ignore

                    stdoutTask,
                    stderrTask,
                    launchWatchdogLoopWith observe (fun () -> DateTime.UtcNow) sleep pollMs launchDeadline timeout)

            // BOUNDED drain of the stream pumps. A normal process's pipes reach EOF the
            // instant it exits (returns in ms); only a grandchild holding the pipe makes
            // this block, and then only for the window. An expired window rides out on the
            // value as `DrainTimedOut` so it cannot be mistaken for a child that said
            // nothing.
            let drainPumps () : ProcessOutput =
                let waitReturned =
                    Task.WaitAll(
                        [| stdoutTask :> Task; stderrTask :> Task |],
                        int PostExitDrainWindow.TotalMilliseconds
                    )

                classifyDrain
                    waitReturned
                    (fun () -> stdoutTask.Result)
                    (fun () -> stderrTask.Result)
                    (drainedOutput ())
                    PostExitDrainWindow

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

    outcome, teardown.Value

/// See `runProcessCore`.
let runProcessTo
    (sink: (string -> unit) option)
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome =
    runProcessCore false sink ignore command args workDir env bounds |> fst

/// `runProcess`, plus — when the child overran and was torn down — WHICH tree the
/// kill was aimed at and which of its members survived (`TreeTeardown`). For callers
/// whose timeout report must name what it killed and what it leaked.
let internal runProcessAccounted
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome * TreeTeardown option =
    runProcessCore true None ignore command args workDir env bounds


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

/// `runProcess`, telling `onStarted` the child's pid as soon as it is running — for a
/// caller that has to name the process it is waiting on while it waits.
let runProcessObserved
    (onStarted: int -> unit)
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (bounds: ProcessBounds)
    : ProcessOutcome =
    runProcessCore false None onStarted command args workDir env bounds |> fst

/// The expiry policy of `runWithCancellableTimeoutTracked`, with the deadline wait
/// injected: `awaitWork task` returns true iff the work finished inside the deadline,
/// and `after` is the budget a timeout reports. Injected so the expiry can be fired at
/// an exact point in the work rather than raced against a wall clock.
let internal runWithCancellableDeadline
    (awaitWork: Task -> bool)
    (after: TimeSpan)
    (work: CancellationToken -> 'a)
    : WorkOutcome<'a> * Task =
    let cts = new CancellationTokenSource()

    // `TaskScheduler.Default`, never the caller's: a caller about to block on this
    // task must not be the scheduler it waits for.
    let task =
        Task.Factory.StartNew(
            (fun () -> ProcessRegistry.withChildScope cts.Token (fun () -> work cts.Token)),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        )

    let mutable completionOwnsCts = false

    try
        if awaitWork task then
            WorkCompleted task.Result, Task.CompletedTask
        else
            // Signal the work to unwind so it stops holding any lock, and tear down
            // the children its scope owns. We do NOT block on the orphan after
            // cancelling — a cooperative unit observes the token and exits promptly;
            // a non-cooperative one would hang us here, which is precisely what we
            // must avoid. Keep the CTS alive until the work really exits: an Async
            // may register against the token after the timeout signal, and disposing
            // it here turns that legitimate late observation into
            // ObjectDisposedException.
            cts.Cancel()

            let completion =
                task.ContinueWith(
                    (fun (completed: Task<'a>) ->
                        try
                            completed.GetAwaiter().GetResult() |> ignore
                        finally
                            cts.Dispose()),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )

            completionOwnsCts <- true
            WorkTimedOut after, completion
    finally
        // A synchronously completed or faulted task has no continuation to
        // own cleanup. A timed-out task transfers ownership before returning.
        if not completionOwnsCts then
            cts.Dispose()

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
///
/// The work runs in a process scope of its own (`ProcessRegistry.withChildScope`).
/// Cancellation cannot stop an OS child, so on expiry the scope tears down the
/// children THIS work spawned, and refuses any it tries to start afterwards, while
/// siblings in the caller's scope are left alone. Work that returns while one of its
/// children's termination could not be established does not complete successfully.
let runWithCancellableTimeoutTracked (timeout: TimeSpan) (work: CancellationToken -> 'a) : WorkOutcome<'a> * Task =
    if timeout = Threading.Timeout.InfiniteTimeSpan then
        WorkCompleted(ProcessRegistry.withChildScope CancellationToken.None (fun () -> work CancellationToken.None)),
        Task.CompletedTask
    else
        runWithCancellableDeadline (fun task -> task.Wait(timeout)) timeout work

let runWithCancellableTimeout (timeout: TimeSpan) (work: CancellationToken -> 'a) : WorkOutcome<'a> =
    runWithCancellableTimeoutTracked timeout work |> fst

/// Run a synchronous unit of work with a wall-clock timeout. Back-compat
/// shim over `runWithCancellableTimeout` for work that cannot observe
/// cancellation. PREFER `runWithCancellableTimeout` for new call sites so a
/// timed-out unit actually releases its locks instead of running on as an
/// orphan thread.
let runWithTimeout (timeout: TimeSpan) (work: unit -> 'a) : WorkOutcome<'a> =
    runWithCancellableTimeout timeout (fun _ct -> work ())
