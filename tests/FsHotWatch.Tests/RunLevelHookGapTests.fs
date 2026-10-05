/// Tests for the run-level `beforeRun`/`afterRun` hook pair.
///
/// GOAL of the feature: a consumer can bracket a WHOLE `fshw check`/`confirm` run
/// with hooks configured by TOP-LEVEL `.fshw.json` keys — a `beforeRun` that runs
/// BEFORE the daemon is contacted (fail-closed) and an `afterRun` that is a
/// `finally`, GUARANTEED to fire on success, failure, AND abort (including signal),
/// never cache-replayed. The first consumer (a large private downstream repository) needs it
/// to RELEASE a box-wide gate-lock so two concurrent runs serialize with zero manual lock
/// commands. These hooks are DISTINCT from `tests.beforeRun` (TestPrune), which runs
/// inside the daemon per test run.
///
/// This file exercises `Program.withRunHooks` and its helpers directly — the
/// transport-agnostic bracket that both `check` arms and `confirm`'s `MustEarn` arm
/// wrap the action in — plus `withRunHooksUnclaimedFor`, the hooks-only half that
/// `confirm`'s "verdict still applies" fast path takes instead: the same hooks under
/// the same verb policy, without the in-flight claim or the terminal verdict that
/// belong to a run doing heavy work.
module FsHotWatch.Tests.RunLevelHookGapTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.Program
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// The hooks run in a working directory; the sentinels they touch are absolute
/// paths under the system temp dir, so cwd is irrelevant and any real dir works.
/// a FRESH directory, not the system temp dir itself — every bracket
/// is an invocation now, and an invocation hashes its repo tree and leaves a verdict
/// under `.fshw/` in it.
let private tmpRoot =
    let id = Guid.NewGuid().ToString("N")
    let path = Path.Combine(Path.GetTempPath(), $"fshw-runhooks-tests-%s{id}")
    Directory.CreateDirectory path |> ignore
    path

/// A fresh absolute sentinel path. A hook `touch <sentinel>` creates it iff the
/// hook actually fired; `File.Exists` is then a pure observation of "the hook ran".
let private freshSentinel () =
    let id = Guid.NewGuid().ToString("N")
    Path.Combine(Path.GetTempPath(), $"fshw-runhook-%s{id}.sentinel")

let private touch (path: string) = "touch '" + path + "'"

let private tryDelete (path: string) =
    try
        File.Delete path
    with _ ->
        ()

/// A config carrying the run-level hooks and nothing else load-bearing.
let private hooks (before: string option) (after: string option) : DaemonConfiguration =
    { defaultTestConfig () with
        BeforeRun = before
        AfterRun = after
        RunHookTimeoutSec = Some 30 }

/// A do-nothing `IpcOps` whose `IsRunning` is the only field the tests here
/// exercise (the `confirm` fast path returns before any other IPC call).
let private dummyIpc (isRunning: string -> bool) : IpcOps =
    { Shutdown = fun _ -> async { return "" }
      Scan = fun _ -> async { return "" }
      ScanStatus = fun _ -> async { return "" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name }
      GetDiagnostics =
        fun _ _ ->
            async {
                return
                    """{"count": 0, "files": {}, "projectModel": {"schema": "fshw-project-model-v1", "status": "available", "generation": 7, "counts": {"discovered": 3, "loaded": 3, "optionsMapped": 3, "registered": 3}, "reasonCode": null}}"""
            }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      Invalidate = fun _ -> async { return "invalidated" }
      IsRunning = isRunning
      LaunchDaemon = fun _ _ _ -> () }

/// What a transport publishes on a clean run: a green owned by
/// `invocationId`, with a plugin record, a `tests.beforeRun` hook and both on the
/// timeline — the evidence a later terminal downgrade must keep.
let private publishCleanInvocation (invocationId: string) (root: string) =
    let tree = TreeHash.compute root []

    FsHotWatch.Cli.Verdict.create
        FsHotWatch.Cli.Verdict.Check
        (BaselineFixtures.reportOf (FullSuite 1))
        tree
        (Some [])
        (FsHotWatch.Cli.Verdict.Green BaselineFixtures.baseline)
        0
        [ { Name = "test-prune"
            Outcome = FsHotWatch.Cli.Verdict.PluginOutcome.Ok
            ElapsedMs = Some 5L
            Summary = Some "tests passed" } ]
        []
        FsHotWatch.Cli.Verdict.CheckComparison.notRecorded
        []
        ProjectModelFixtures.available
    |> FsHotWatch.Cli.Verdict.withAttribution
        { Hooks =
            [ { Scope = "tests.beforeRun"
                StepIndex = 1
                StepCount = 1
                Command = "true"
                ElapsedMs = 5L
                Outcome = "ok" } ]
          TimingSpans =
            [ { Scope = "plugin.test-prune"
                StartOffsetMs = 0L
                ElapsedMs = 5L
                Detail = Some "tests passed" }
              { Scope = "tests.beforeRun"
                StartOffsetMs = 0L
                ElapsedMs = 5L
                Detail = Some "true" } ]
          RefusedEvidence = []
          ObservedElapsedMs = Some 20L
          InvocationId = Some invocationId }
    |> FsHotWatch.Cli.Verdict.write root

    // So the wrapper's own observed wall time is at least what the transport claimed.
    Thread.Sleep 20

let private isIncomplete (outcome: FsHotWatch.Cli.Verdict.Outcome) =
    match outcome with
    | FsHotWatch.Cli.Verdict.Incomplete _ -> true
    | _ -> false

/// A signal installer that hands the finalizer to the test instead of the OS.
let private captureSignalFinalizer (slot: (unit -> unit) option ref) =
    fun (finalize: unit -> unit) (_exitWith: int -> unit) ->
        slot.Value <- Some finalize

        { new IDisposable with
            member _.Dispose() = () }

let private fireCapturedSignal (slot: (unit -> unit) option ref) =
    match slot.Value with
    | Some finalize -> finalize ()
    | None -> failwith "the signal finalizer was not installed"

// ---------------------------------------------------------------------------
// afterRun: a `finally` that fires on success, a red verdict, an abort, a throw
// ---------------------------------------------------------------------------

/// POSITIVE CONTROL. A clean success fires afterRun, so the sentinel/hook wiring is
/// proven sound and the abort test's success can only mean "the finally fired", never
/// "the harness could never have written the file".
[<Fact(Timeout = 20000)>]
let ``afterRun fires on a clean success (exit 0)`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 0)
        test <@ code = 0 @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

/// A run that ABORTS — the daemon's `TestRunCompleted { Outcome = Aborted }` shape,
/// surfaced at the CLI as an un-completable exit 2 — MUST still fire afterRun: it is
/// the release path a gate-lock cannot afford to miss, and the one the older
/// `afterTests` seam missed.
[<Fact(Timeout = 20000)>]
let ``an aborted run (exit 2) still fires afterRun (finally)`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 2)
        test <@ File.Exists sentinel @>
        // The hook did not alter the run's exit code.
        test <@ code = 2 @>
    finally
        tryDelete sentinel

[<Fact(Timeout = 20000)>]
let ``afterRun fires on a red verdict (exit 1)`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 1)
        test <@ File.Exists sentinel @>
        test <@ code = 1 @>
    finally
        tryDelete sentinel

/// afterRun is a `finally`, not a success callback: it fires when the action THROWS,
/// and the exception still propagates (afterRun does not swallow it).
[<Fact(Timeout = 20000)>]
let ``afterRun fires even when the action throws (finally)`` () =
    let sentinel = freshSentinel ()

    try
        let thrown =
            try
                withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> failwith "boom")
                |> ignore

                false
            with _ ->
                true

        test <@ thrown @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

/// BEST-EFFORT / NON-VERDICT-ALTERING: a failing afterRun is logged loudly but
/// MUST NOT change the run's exit code — a lock-release hiccup may never flip
/// green↔red.
[<Fact(Timeout = 20000)>]
let ``a failing afterRun does NOT change the run's exit code`` () =
    let codeGreen = withRunHooks tmpRoot (hooks None (Some "exit 9")) (fun () -> 0)
    test <@ codeGreen = 0 @>

    // ... and a red is not laundered green either.
    let codeRed = withRunHooks tmpRoot (hooks None (Some "exit 9")) (fun () -> 1)
    test <@ codeRed = 1 @>

// ---------------------------------------------------------------------------
// beforeRun: fail-closed, and ordered before the action
// ---------------------------------------------------------------------------

/// Exit 2, NOT 1: a blocked run is incomplete, not failed.
[<Fact(Timeout = 20000)>]
let ``a non-zero beforeRun is fail-closed: exit 2 and NO plugin work`` () =
    let ran = ref false

    let code =
        withRunHooks tmpRoot (hooks (Some "exit 7") None) (fun () ->
            ran.Value <- true
            0)

    test <@ code = 2 @>
    test <@ not ran.Value @>

[<Fact(Timeout = 20000)>]
let ``a zero beforeRun proceeds and afterRun still fires`` () =
    let sentinel = freshSentinel ()
    let ran = ref false

    try
        let code =
            withRunHooks tmpRoot (hooks (Some "true") (Some(touch sentinel))) (fun () ->
                ran.Value <- true
                0)

        test <@ code = 0 @>
        test <@ ran.Value @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

/// ORDERING: beforeRun runs BEFORE the action — hence before the daemon is contacted,
/// since contacting the daemon is the first thing the wrapped action does.
[<Fact(Timeout = 20000)>]
let ``beforeRun runs BEFORE the action (hence before the daemon is contacted)`` () =
    let beforeSentinel = freshSentinel ()
    let observedByAction = ref false

    try
        let code =
            withRunHooks tmpRoot (hooks (Some(touch beforeSentinel)) None) (fun () ->
                observedByAction.Value <- File.Exists beforeSentinel
                0)

        test <@ code = 0 @>
        test <@ observedByAction.Value @>
    finally
        tryDelete beforeSentinel

/// A failed beforeRun does NOT fire afterRun: beforeRun is the acquire, and a failed
/// acquire has nothing for afterRun (which brackets the ACTION) to release.
[<Fact(Timeout = 20000)>]
let ``a failed beforeRun does NOT fire afterRun (nothing was bracketed)`` () =
    let afterSentinel = freshSentinel ()

    try
        let code =
            withRunHooks tmpRoot (hooks (Some "exit 1") (Some(touch afterSentinel))) (fun () -> 0)

        test <@ code = 2 @>
        test <@ not (File.Exists afterSentinel) @>
    finally
        tryDelete afterSentinel

// ---------------------------------------------------------------------------
// No hooks configured → straight passthrough (no latch, no machinery)
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``with no hooks configured, withRunHooks is a straight passthrough`` () =
    let calls = ref 0

    let code =
        withRunHooks tmpRoot (defaultTestConfig ()) (fun () ->
            incr calls
            3)

    test <@ code = 3 @>
    test <@ calls.Value = 1 @>

// ---------------------------------------------------------------------------
// The exactly-once latch (finally + signal handlers share it)
// ---------------------------------------------------------------------------

/// `makeRunOnce` runs its callback exactly once no matter how many callers race —
/// the guarantee that lets the `finally` and every signal handler all call the same
/// closure while afterRun still fires exactly once.
[<Fact(Timeout = 20000)>]
let ``makeRunOnce runs the callback exactly once under concurrent callers`` () =
    let count = ref 0
    let once = makeRunOnce (fun () -> Interlocked.Increment(count) |> ignore)

    let threads = [ for _ in 1..50 -> Thread(fun () -> once ()) ]
    threads |> List.iter (fun t -> t.Start())
    threads |> List.iter (fun t -> t.Join())

    // ... and again, serially, afterwards.
    once ()
    once ()

    test <@ count.Value = 1 @>

/// `onRunSignal` runs afterRun then exits with the given code, and because the
/// afterRun closure is the SAME latched one the `finally` calls, a signal followed
/// by the finally (or a second signal) fires afterRun exactly once.
[<Fact(Timeout = 20000)>]
let ``onRunSignal fires afterRun once and shares the latch with the finally`` () =
    let ran = ref 0
    let exits = ResizeArray<int>()
    let afterRun = makeRunOnce (fun () -> incr ran)

    onRunSignal afterRun exits.Add 143
    // The finally's later call to the SAME latched closure is a no-op for afterRun.
    afterRun ()
    // A second signal is likewise a no-op for afterRun, though it still records its code.
    onRunSignal afterRun exits.Add 130

    test <@ ran.Value = 1 @>
    test <@ List.ofSeq exits = [ 143; 130 ] @>

// ---------------------------------------------------------------------------
// Timeout resolution
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``resolveRunHookTimeoutSec prefers runHookTimeoutSec, then global, then the default`` () =
    let cfg = defaultTestConfig ()

    test
        <@
            resolveRunHookTimeoutSec
                { cfg with
                    RunHookTimeoutSec = Some 5
                    TimeoutSec = Some 99 } = 5
        @>

    test
        <@
            resolveRunHookTimeoutSec
                { cfg with
                    RunHookTimeoutSec = None
                    TimeoutSec = Some 99 } = 99
        @>

    // Both absent → the baked-in default: a run-level hook is ALWAYS bounded, even
    // when the global timeout is disabled.
    test
        <@
            resolveRunHookTimeoutSec
                { cfg with
                    RunHookTimeoutSec = None
                    TimeoutSec = None } = DefaultGlobalTimeoutSec
        @>

// ---------------------------------------------------------------------------
// runHookCommands — WHICH verbs the run-level hooks bracket
//
// The policy exists so a box-wide gate can guard the merge verdict (`confirm`)
// without taxing the inner loop (`check`), which runs constantly. A verb the
// config does not select must be indistinguishable from having no hooks at all.
// ---------------------------------------------------------------------------

/// Hooks plus an explicit verb selection.
let private hooksFor (verbs: RunHookCommand list) (before: string option) (after: string option) =
    { hooks before after with
        RunHookCommands = Set.ofList verbs }

[<Fact(Timeout = 20000)>]
let ``by default both verbs are bracketed`` () =
    // The upgrade-safety property at the behavioural level: an untouched config
    // brackets check exactly as it did before `runHookCommands` existed.
    let sentinel = freshSentinel ()

    try
        let code =
            withRunHooksFor RunHookCommand.Check tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 0)

        test <@ code = 0 @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

[<Fact(Timeout = 20000)>]
let ``runHookCommands confirm-only leaves a check run COMPLETELY unwrapped`` () =
    // A beforeRun that FAILS would abort a bracketed run with exit 2 and never invoke
    // the action, so seeing the action's own exit code proves beforeRun never ran; the
    // absent sentinel proves afterRun never ran either. Together: the straight
    // `action ()` path — no latch, no signal handlers, no shell-out.
    let sentinel = freshSentinel ()
    let calls = ref 0

    try
        let config =
            hooksFor [ RunHookCommand.Confirm ] (Some "exit 7") (Some(touch sentinel))

        let code =
            withRunHooksFor RunHookCommand.Check tmpRoot config (fun () ->
                calls.Value <- calls.Value + 1
                3)

        test <@ code = 3 @>
        test <@ calls.Value = 1 @>
        test <@ not (File.Exists sentinel) @>
    finally
        tryDelete sentinel

[<Fact(Timeout = 20000)>]
let ``runHookCommands confirm-only still brackets a confirm run`` () =
    let sentinel = freshSentinel ()

    try
        let config = hooksFor [ RunHookCommand.Confirm ] None (Some(touch sentinel))
        let code = withRunHooksFor RunHookCommand.Confirm tmpRoot config (fun () -> 0)
        test <@ code = 0 @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

[<Fact(Timeout = 20000)>]
let ``an empty runHookCommands brackets neither verb`` () =
    let config = hooksFor [] (Some "exit 7") None

    // A failing beforeRun would abort with 2 if either verb were bracketed.
    test <@ withRunHooksFor RunHookCommand.Check tmpRoot config (fun () -> 5) = 5 @>
    test <@ withRunHooksFor RunHookCommand.Confirm tmpRoot config (fun () -> 5) = 5 @>

[<Fact(Timeout = 20000)>]
let ``the verb filter behaves identically on both transports`` () =
    // `--run-once` and the daemon path differ ONLY in the action passed here, so the
    // bracketing decision cannot diverge between them by construction. Pinned anyway:
    // the earlier per-transport rule was how CI could silently lose the gate.
    let config = hooksFor [ RunHookCommand.Confirm ] (Some "exit 7") None

    // check — unwrapped on BOTH transports: the failing beforeRun never runs, so
    // each action's own exit code comes straight back.
    test <@ withRunHooksFor RunHookCommand.Check tmpRoot config (fun () -> 11) = 11 @>
    test <@ withRunHooksFor RunHookCommand.Check tmpRoot config (fun () -> 12) = 12 @>

    // confirm — bracketed on BOTH: the failing beforeRun fails closed with exit 2,
    // whichever action would have followed.
    test <@ withRunHooksFor RunHookCommand.Confirm tmpRoot config (fun () -> 11) = 2 @>
    test <@ withRunHooksFor RunHookCommand.Confirm tmpRoot config (fun () -> 12) = 2 @>

[<Fact(Timeout = 15000)>]
let ``runHooksApplyTo reads the configured verb set`` () =
    let cfg = defaultTestConfig ()

    test <@ runHooksApplyTo cfg RunHookCommand.Check @>
    test <@ runHooksApplyTo cfg RunHookCommand.Confirm @>

    let confirmOnly =
        { cfg with
            RunHookCommands = Set.singleton RunHookCommand.Confirm }

    test <@ not (runHooksApplyTo confirmOnly RunHookCommand.Check) @>
    test <@ runHooksApplyTo confirmOnly RunHookCommand.Confirm @>

    let none = { cfg with RunHookCommands = Set.empty }
    test <@ not (runHooksApplyTo none RunHookCommand.Check) @>
    test <@ not (runHooksApplyTo none RunHookCommand.Confirm) @>

// ---------------------------------------------------------------------------
// Every bracket is an invocation: it owns the verdict its action
// published, attaches its own timing to that verdict and no other, and leaves an
// invocation-owned `incomplete` behind on every way out that did not publish.
// ---------------------------------------------------------------------------

/// The bracket's evidence lands on the verdict the action published: both run-level
/// hooks, timed and placed on the timeline, plus the observed wall time.
[<Fact(Timeout = 20000)>]
let ``run-level hooks are attached to the invocation's own verdict with observed wall time`` () =
    withTempDir "run-hook-attached" (fun root ->
        let code =
            withRunHooksForInvocation RunHookCommand.Check root (hooks (Some "true") (Some "true")) (fun invocation ->
                publishCleanInvocation invocation.Id root
                0)

        test <@ code = 0 @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ BaselineFixtures.isGreen (verdict.Outcome) @>

            test <@ verdict.Hooks |> List.map _.Scope = [ "tests.beforeRun"; "run.beforeRun"; "run.afterRun" ] @>

            test <@ verdict.TimingSpans |> List.exists (fun span -> span.Scope = "run.afterRun") @>
            test <@ verdict.ObservedElapsedMs |> Option.exists (fun observed -> observed >= 20L) @>
        | other -> failwith $"expected the attached verdict, got %A{other}")

/// The failed `beforeRun` is the ONLY record of the invocation, and it is timed and
/// named like any other hook.
[<Fact(Timeout = 20000)>]
let ``a refused beforeRun publishes an invocation-owned incomplete naming the hook`` () =
    withTempDir "run-hook-refused" (fun root ->
        let code =
            withRunHooksForInvocation RunHookCommand.Check root (hooks (Some "exit 7") None) (fun _ ->
                failwith "the action must not run after a refused beforeRun")

        test <@ code = 2 @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ isIncomplete verdict.Outcome @>
            test <@ verdict.InvocationId.IsSome @>
            test <@ verdict.Hooks |> List.map (fun h -> h.Scope, h.Outcome) = [ "run.beforeRun", "fail" ] @>
        | other -> failwith $"expected the refusal verdict, got %A{other}")

[<Fact(Timeout = 15000)>]
let ``transport exit without a verdict publishes correlated incomplete timing`` () =
    withTempDir "run-hook-terminal-verdict" (fun root ->
        let code =
            withRunHooksForInvocation RunHookCommand.Check root (hooks None None) (fun _ -> 2)

        test <@ code = 2 @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId.IsSome @>
            test <@ isIncomplete verdict.Outcome @>
            test <@ verdict.ObservedElapsedMs.IsSome @>

            test
                <@
                    verdict.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "without publishing")
                @>
        | other -> failwith $"expected terminal invocation verdict, got %A{other}")

[<Fact(Timeout = 15000)>]
let ``transport exception publishes correlated incomplete timing before rethrow`` () =
    withTempDir "run-hook-exception-verdict" (fun root ->
        let thrown =
            Record.Exception(fun () ->
                withRunHooksForInvocation RunHookCommand.Check root (hooks None None) (fun _ ->
                    raise (InvalidOperationException "transport boom"))
                |> ignore)

        test <@ not (isNull thrown) @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId.IsSome @>
            test <@ isIncomplete verdict.Outcome @>

            test
                <@
                    verdict.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "terminated with an exception")
                @>
        | other -> failwith $"expected exception terminal verdict, got %A{other}")

[<Fact(Timeout = 15000)>]
let ``transport exception replaces its own already-published normal verdict with incomplete`` () =
    withTempDir "run-hook-exception-after-verdict" (fun root ->
        let mutable ownedInvocation = None

        let thrown =
            Record.Exception(fun () ->
                withRunHooksForInvocation RunHookCommand.Check root (hooks None None) (fun invocation ->
                    ownedInvocation <- Some invocation.Id
                    publishCleanInvocation invocation.Id root
                    raise (InvalidOperationException "transport failed after publishing"))
                |> ignore)

        test <@ not (isNull thrown) @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId = ownedInvocation @>
            test <@ isIncomplete verdict.Outcome @>
            test <@ verdict.ExitCode = 2 @>
            // Downgraded, not discarded: the published evidence survives.
            test <@ verdict.Plugins |> List.exists (fun plugin -> plugin.Name = "test-prune") @>

            test
                <@
                    verdict.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "terminated with an exception")
                @>
        | other -> failwith $"expected premature terminal verdict, got %A{other}")

[<Fact(Timeout = 15000)>]
let ``transport exception replaces malformed verdict JSON with its terminal fallback`` () =
    withTempDir "run-hook-exception-malformed-verdict" (fun root ->
        let mutable ownedInvocation = None

        let thrown =
            Record.Exception(fun () ->
                withRunHooksForInvocation RunHookCommand.Check root (hooks None None) (fun invocation ->
                    ownedInvocation <- Some invocation.Id

                    Directory.CreateDirectory(Path.GetDirectoryName(FsHotWatch.Cli.Verdict.path root))
                    |> ignore

                    File.WriteAllText(FsHotWatch.Cli.Verdict.path root, "{malformed")
                    raise (InvalidOperationException "transport failed over malformed verdict"))
                |> ignore)

        test <@ not (isNull thrown) @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId = ownedInvocation @>
            test <@ isIncomplete verdict.Outcome @>
        | other -> failwith $"malformed verdict was not replaced by terminal evidence: %A{other}")

[<Fact(Timeout = 15000)>]
let ``signal replaces a non-object verdict with its terminal fallback`` () =
    withTempDir "run-hook-signal-nonobject-verdict" (fun root ->
        let signalFinalize = ref None
        let mutable ownedInvocation = None

        let code =
            withRunHooksCommandUsingSignals
                (captureSignalFinalizer signalFinalize)
                FsHotWatch.Cli.Verdict.Check
                root
                (hooks None None)
                (fun invocation ->
                    ownedInvocation <- Some invocation.Id

                    Directory.CreateDirectory(Path.GetDirectoryName(FsHotWatch.Cli.Verdict.path root))
                    |> ignore

                    File.WriteAllText(FsHotWatch.Cli.Verdict.path root, "[]")
                    fireCapturedSignal signalFinalize
                    2)

        test <@ code = 2 @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId = ownedInvocation @>
            test <@ isIncomplete verdict.Outcome @>

            test
                <@
                    verdict.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "signalled")
                @>
        | other -> failwith $"non-object verdict was not replaced by terminal evidence: %A{other}")

[<Fact(Timeout = 15000)>]
let ``premature terminal fallback never overwrites a newer invocation verdict`` () =
    withTempDir "run-hook-newer-verdict" (fun root ->
        let thrown =
            Record.Exception(fun () ->
                withRunHooksForInvocation RunHookCommand.Check root (hooks None None) (fun _ ->
                    publishCleanInvocation "newer-invocation" root
                    raise (InvalidOperationException "older transport failed"))
                |> ignore)

        test <@ not (isNull thrown) @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ verdict.InvocationId = Some "newer-invocation" @>
            test <@ BaselineFixtures.isGreen (verdict.Outcome) @>
            test <@ verdict.Hooks |> List.map _.Scope = [ "tests.beforeRun" ] @>
        | other -> failwith $"expected newer verdict to survive, got %A{other}")

/// `--run-once` over a tree with no projects: the hooks still bracket it, and the
/// refusal is published as an invocation-owned incomplete instead of a bare exit 2
/// that would leave a prior green readable.
[<Fact(Timeout = 30000)>]
let ``zero-project run-once failure remains inside the run hook bracket`` () =
    withTempDir "run-hook-zero-project" (fun root ->
        let beforeSentinel = freshSentinel ()
        let afterSentinel = freshSentinel ()

        try
            let config = hooks (Some(touch beforeSentinel)) (Some(touch afterSentinel))

            let code =
                executeCommand
                    ""
                    (fun _ -> failwith "zero-project run must not create a daemon")
                    (dummyIpc (fun _ -> false))
                    root
                    "pipe"
                    (FsHotWatch.Cli.Program.Command.Check [ RunOnce ])
                    defaultGlobalOptions
                    config
                    30.0

            test <@ code = 2 @>
            test <@ File.Exists beforeSentinel @>
            test <@ File.Exists afterSentinel @>

            match FsHotWatch.Cli.Verdict.read root with
            | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
                test <@ verdict.ExitCode = 2 @>
                test <@ verdict.InvocationId.IsSome @>

                match verdict.Outcome with
                | FsHotWatch.Cli.Verdict.Incomplete reason -> test <@ reason.Contains "no projects" @>
                | other -> failwith $"expected zero-project incomplete verdict, got %A{other}"

                test <@ verdict.ObservedElapsedMs.IsSome @>

                test
                    <@
                        verdict.TimingIncompleteReasons
                        |> List.exists (fun reason -> reason.Contains "no projects")
                    @>

                test <@ verdict.Hooks |> List.map _.Scope = [ "run.beforeRun"; "run.afterRun" ] @>
            | other -> failwith $"zero-project run did not publish a verdict: %A{other}"
        finally
            tryDelete beforeSentinel
            tryDelete afterSentinel)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``every active invocation installs signal finalization even without afterRun`` (beforeRunOnly: bool) =
    withTempDir "run-hook-signal-finalize" (fun root ->
        let installedFinalize = ref None
        let config = hooks (if beforeRunOnly then Some "true" else None) None

        let code =
            withRunHooksCommandUsingSignals
                (captureSignalFinalizer installedFinalize)
                FsHotWatch.Cli.Verdict.Check
                root
                config
                (fun invocation ->
                    publishCleanInvocation invocation.Id root
                    fireCapturedSignal installedFinalize
                    2)

        test <@ code = 2 @>
        test <@ installedFinalize.Value.IsSome @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
            test <@ isIncomplete verdict.Outcome @>
            test <@ verdict.InvocationId.IsSome @>

            test
                <@
                    verdict.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "signalled")
                @>
        | other -> failwith $"signal finalizer did not publish its terminal verdict: %A{other}")

[<Fact(Timeout = 15000)>]
let ``signal downgrade still wins after ordinary finalization already won the teardown latch`` () =
    withTempDir "run-hook-signal-after-ordinary" (fun root ->
        let signalFinalize = ref None
        let mutable ownedInvocation = None
        use releaseSignal = new ManualResetEventSlim(false)

        let signalTask =
            Threading.Tasks.Task.Run(fun () ->
                releaseSignal.Wait()
                fireCapturedSignal signalFinalize)

        let code =
            withRunHooksCommandUsingSignals
                (captureSignalFinalizer signalFinalize)
                FsHotWatch.Cli.Verdict.Check
                root
                (hooks None None)
                (fun invocation ->
                    ownedInvocation <- Some invocation.Id
                    publishCleanInvocation invocation.Id root
                    0)

        test <@ code = 0 @>

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found ordinary -> test <@ BaselineFixtures.isGreen (ordinary.Outcome) @>
        | other -> failwith $"ordinary finalization did not preserve green: %A{other}"

        releaseSignal.Set()
        signalTask.GetAwaiter().GetResult()

        match FsHotWatch.Cli.Verdict.read root with
        | FsHotWatch.Cli.Verdict.Reading.Found terminal ->
            test <@ terminal.InvocationId = ownedInvocation @>
            test <@ isIncomplete terminal.Outcome @>
            test <@ terminal.ObservedElapsedMs |> Option.exists (fun elapsed -> elapsed >= 20L) @>

            test
                <@
                    terminal.Plugins
                    |> List.exists (fun plugin -> plugin.Name = "test-prune" && plugin.ElapsedMs = Some 5L)
                @>

            test
                <@
                    terminal.Hooks
                    |> List.exists (fun hook -> hook.Scope = "tests.beforeRun" && hook.ElapsedMs = 5L)
                @>

            test
                <@
                    terminal.TimingSpans
                    |> List.exists (fun span -> span.Scope = "plugin.test-prune" && span.ElapsedMs = 5L)
                @>

            test
                <@
                    terminal.TimingSpans
                    |> List.exists (fun span -> span.Scope = "tests.beforeRun" && span.ElapsedMs = 5L)
                @>

            test
                <@
                    terminal.TimingIncompleteReasons
                    |> List.exists (fun reason -> reason.Contains "signalled")
                @>
        | other -> failwith $"late signal did not downgrade the same-owner verdict: %A{other}")

// ---------------------------------------------------------------------------
// `confirm` StillApplies fast-path: hooks YES, claim NO
// ---------------------------------------------------------------------------

/// Seed `root` with the exact precondition for the fast path: a full-suite-green
/// `confirm` verdict for THIS tree, from THIS binary. Asserts the precondition holds,
/// so a test that reaches the slow path fails as a setup error rather than as a
/// silently-passing negative.
let private seedStillApplyingGreen (root: string) =
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(Path.Combine(root, "src", "Lib.fs"), "module Lib\nlet x = 1\n")
    let tree = TreeHash.compute root []

    let verdict =
        FsHotWatch.Cli.Verdict.create
            FsHotWatch.Cli.Verdict.Confirm
            (BaselineFixtures.reportOf (FullSuite 1))
            ({ Hash = tree.Hash
               FileCount = tree.FileCount
               SkippedCount = tree.SkippedCount
               DeclaredCount = tree.DeclaredCount
               AbsentDeclarationCount = tree.AbsentDeclarationCount }
            : TreeHash.Tree)
            (Some [])
            (FsHotWatch.Cli.Verdict.Green BaselineFixtures.baseline)
            0
            ([ { Name = "test-prune"
                 Outcome = FsHotWatch.Cli.Verdict.PluginOutcome.Ok
                 ElapsedMs = Some 1000L
                 Summary = Some "ok" } ]
            : FsHotWatch.Cli.Verdict.PluginVerdict list)
            []
            FsHotWatch.Cli.Verdict.CheckComparison.notRecorded
            []
            ProjectModelFixtures.available

    FsHotWatch.Cli.Verdict.write root verdict

    match FsHotWatch.Cli.Verdict.priorConfirmation root [] with
    | FsHotWatch.Cli.Verdict.PriorConfirmation.StillApplies _ -> ()
    | FsHotWatch.Cli.Verdict.PriorConfirmation.MustEarn ->
        failwith "setup: expected the StillApplies fast path to be taken"

/// A config whose only load-bearing content is the run-level hook pair and the verbs
/// they apply to.
let private fastPathConfig (verbs: Set<RunHookCommand>) (before: string option) (after: string option) =
    { defaultTestConfig () with
        Build = None
        Format = Off
        Lint = false
        BeforeRun = before
        AfterRun = after
        RunHookCommands = verbs
        RunHookTimeoutSec = Some 30 }

/// `IsRunning=true` skips the zero-projects precheck; the fast path then answers
/// before any daemon contact, so a daemon factory that throws is the assertion that
/// none was started.
let private runConfirm (root: string) (config: DaemonConfiguration) =
    executeCommand
        ""
        (fun _ -> failwith "the confirm fast path must not create a daemon")
        (dummyIpc (fun _ -> true))
        root
        "pipe"
        (Confirm [])
        defaultGlobalOptions
        config
        30.0

/// THE DEFECT. `beforeRun` exists to check what the verdict's tree hash does NOT
/// cover — an index, a doc set, anything deliberately excluded from the verdict's
/// inputs. A fast path that certifies a stored green without running it certifies a
/// claim nobody checked: edit only excluded files after a green, and `confirm`
/// answers 0 having verified nothing about them.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path fires the run-level hooks`` () =
    withTempDir "confirm-fastpath-hooks" (fun root ->
        seedStillApplyingGreen root

        // Fires under the default verb set AND under an explicit `["confirm"]`, so
        // neither can be mistaken for the reason the hooks ran.
        for verbs in [ DefaultRunHookCommands; Set.singleton RunHookCommand.Confirm ] do
            let beforeSentinel = freshSentinel ()
            let afterSentinel = freshSentinel ()

            try
                let code =
                    runConfirm root (fastPathConfig verbs (Some(touch beforeSentinel)) (Some(touch afterSentinel)))

                test <@ code = 0 @>
                test <@ File.Exists beforeSentinel @>
                test <@ File.Exists afterSentinel @>
            finally
                tryDelete beforeSentinel
                tryDelete afterSentinel)

/// The verb policy is the SAME one the slow path obeys: a consumer that brackets only
/// `check` gets no `confirm` hooks here either.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path obeys runHookCommands`` () =
    withTempDir "confirm-fastpath-verb" (fun root ->
        seedStillApplyingGreen root
        let beforeSentinel = freshSentinel ()
        let afterSentinel = freshSentinel ()

        try
            let config =
                fastPathConfig
                    (Set.singleton RunHookCommand.Check)
                    (Some(touch beforeSentinel))
                    (Some(touch afterSentinel))

            test <@ runConfirm root config = 0 @>
            test <@ not (File.Exists beforeSentinel) @>
            test <@ not (File.Exists afterSentinel) @>
        finally
            tryDelete beforeSentinel
            tryDelete afterSentinel)

/// THE HALF THAT WAS ALREADY RIGHT, pinned so a later change cannot fix the defect
/// above by making the fast path heavy. The fast path runs no test, starts no daemon
/// — and takes NO run claim: a claim says "a check is in flight over this tree RIGHT
/// NOW", which would make a concurrent `fshw verdict` withhold an answer for a run
/// that runs nothing. The `beforeRun` hook reports the claim directory AS IT RAN, so
/// this observes the window rather than the tidy-up after it.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path claims nothing and rewrites nothing`` () =
    withTempDir "confirm-fastpath-unclaimed" (fun root ->
        seedStillApplyingGreen root
        let before = File.ReadAllText(FsHotWatch.Cli.Verdict.path root)
        let claimProbe = freshSentinel ()

        try
            let config =
                fastPathConfig
                    DefaultRunHookCommands
                    (Some $"ls '%s{root}/.fshw/in-flight' 2>/dev/null > '%s{claimProbe}'; true")
                    None

            test <@ runConfirm root config = 0 @>

            // The hook ran (the probe exists) and saw no claim held.
            test <@ File.Exists claimProbe @>
            test <@ File.ReadAllText(claimProbe).Trim() = "" @>

            // No terminal verdict, no augmentation: the stored green is byte-identical.
            test <@ File.ReadAllText(FsHotWatch.Cli.Verdict.path root) = before @>
        finally
            tryDelete claimProbe)

/// The window the hooks OPEN is closed by re-asking after them. `priorConfirmation`
/// hashes the tree BEFORE `beforeRun` runs, so a write landing while the hook runs
/// would otherwise be certified by a hash taken before it existed. Here the hook itself
/// writes into the tree — the tightest possible version of that race — and `confirm`
/// must refuse rather than certify the pre-write hash. The stored verdict is left
/// alone: it still describes the tree that earned it, and `fshw verdict` already
/// reports it stale against this one.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path refuses a tree that moved while the hooks ran`` () =
    withTempDir "confirm-fastpath-moved" (fun root ->
        seedStillApplyingGreen root
        let before = File.ReadAllText(FsHotWatch.Cli.Verdict.path root)
        let afterSentinel = freshSentinel ()

        try
            let config =
                fastPathConfig
                    DefaultRunHookCommands
                    (Some $"printf 'module Added\nlet y = 2\n' > '%s{root}/src/Added.fs'")
                    (Some(touch afterSentinel))

            test <@ runConfirm root config = 2 @>

            // afterRun is still a `finally` on the refusal path — whatever beforeRun
            // acquired is released.
            test <@ File.Exists afterSentinel @>

            // Nothing written: the green that described the PREVIOUS tree survives, and
            // is stale rather than reusable.
            test <@ File.ReadAllText(FsHotWatch.Cli.Verdict.path root) = before @>

            match FsHotWatch.Cli.Verdict.priorConfirmation root [] with
            | FsHotWatch.Cli.Verdict.PriorConfirmation.MustEarn -> ()
            | FsHotWatch.Cli.Verdict.PriorConfirmation.StillApplies _ ->
                failwith "the moved tree still reads as confirmed"
        finally
            tryDelete afterSentinel)

/// Fail-closed, exactly as the slow path fails: a refused `beforeRun` exits 2 and
/// leaves an invocation-owned `incomplete` behind, so the green it declined to
/// certify cannot be read back out of `.fshw/verdict.json` as the answer. `afterRun`
/// does NOT fire — a failed acquire has nothing to release.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path fails closed when beforeRun refuses`` () =
    withTempDir "confirm-fastpath-refused" (fun root ->
        seedStillApplyingGreen root
        let afterSentinel = freshSentinel ()

        try
            let config =
                fastPathConfig DefaultRunHookCommands (Some "exit 7") (Some(touch afterSentinel))

            test <@ runConfirm root config = 2 @>
            test <@ not (File.Exists afterSentinel) @>

            match FsHotWatch.Cli.Verdict.read root with
            | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
                test <@ isIncomplete verdict.Outcome @>
                test <@ verdict.InvocationId.IsSome @>
                test <@ verdict.Hooks |> List.map _.Scope = [ "run.beforeRun" ] @>
            | other -> failwith $"a refused beforeRun left no record: %A{other}"
        finally
            tryDelete afterSentinel)

// ---------------------------------------------------------------------------
// Signal-handler installation.
//
// The exactly-once + exit-code CONTRACT of a signal firing is tested via `onRunSignal`
// above. Real in-process SIGINT/SIGTERM delivery CANNOT be tested here: the MTP test
// host installs its OWN handler and ABORTS the whole test session on receipt
// (empirically confirmed), regardless of our `Cancel <- true`. So this only checks
// that installation registers and disposes cleanly without firing afterRun.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``installRunSignalHandlers registers and disposes without firing afterRun`` () =
    let ran = ref 0
    let afterRun = makeRunOnce (fun () -> incr ran)

    let handlers = installRunSignalHandlers afterRun ignore
    // Merely installing the handlers must not run the teardown.
    test <@ ran.Value = 0 @>

    // Dispose unregisters both handlers cleanly (no throw), and STILL does not fire.
    handlers.Dispose()
    test <@ ran.Value = 0 @>
