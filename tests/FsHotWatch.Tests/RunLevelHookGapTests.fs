/// AUTOMATION-188 — the run-level `beforeRun`/`afterRun` hook pair.
///
/// GOAL of the feature: a consumer can bracket a WHOLE `fshw check`/`confirm` run
/// with hooks configured by TOP-LEVEL `.fshw.json` keys — a `beforeRun` that runs
/// BEFORE the daemon is contacted (fail-closed) and an `afterRun` that is a
/// `finally`, GUARANTEED to fire on success, failure, AND abort (including signal),
/// never cache-replayed. The first consumer (intelligence) needs it to RELEASE a
/// box-wide gate-lock so two concurrent runs serialize with zero manual lock
/// commands. These hooks are DISTINCT from `tests.beforeRun` (TestPrune), which runs
/// inside the daemon per test run.
///
/// This file exercises `Program.withRunHooks` and its helpers directly — the
/// transport-agnostic bracket that both `check` arms and `confirm`'s `MustEarn` arm
/// wrap the action in. The original RED test anchored at the `afterTests`
/// fileCommand seam because `afterRun` did not exist; it now targets the real
/// run-level hook and is GREEN (an aborted run writes the afterRun sentinel).
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
let private tmpRoot = Path.GetTempPath()

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
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      IsRunning = isRunning
      LaunchDaemon = fun _ _ _ -> () }

// ---------------------------------------------------------------------------
// afterRun: a `finally` that fires on success, a red verdict, an abort, a throw
// ---------------------------------------------------------------------------

/// POSITIVE CONTROL (retargeted). A clean success (exit 0) fires afterRun — so the
/// sentinel/hook wiring is proven sound, and the abort test's success can only mean
/// "the finally fired", never "the harness could never have written the file".
[<Fact(Timeout = 20000)>]
let ``afterRun fires on a clean success (exit 0)`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 0)
        test <@ code = 0 @>
        test <@ File.Exists sentinel @>
    finally
        tryDelete sentinel

/// RETARGETED RED→GREEN (AUTOMATION-188). A run that ABORTS — the daemon's
/// `TestRunCompleted { Outcome = Aborted }` shape, surfaced at the CLI as an
/// un-completable exit 2 — MUST still fire the run-level afterRun. This is exactly
/// the release path a gate-lock cannot afford to miss. Under the old `afterTests`
/// seam nothing fired on abort; the run-level `finally` does.
[<Fact(Timeout = 20000)>]
let ``an aborted run (exit 2) still fires afterRun (finally) — AUTOMATION-188`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 2)
        // afterRun fired even though the run aborted with no verdict ...
        test <@ File.Exists sentinel @>
        // ... and it did NOT alter the run's exit code.
        test <@ code = 2 @>
    finally
        tryDelete sentinel

/// afterRun fires on a RED verdict too (exit 1) — the release is unconditional.
[<Fact(Timeout = 20000)>]
let ``afterRun fires on a red verdict (exit 1)`` () =
    let sentinel = freshSentinel ()

    try
        let code = withRunHooks tmpRoot (hooks None (Some(touch sentinel))) (fun () -> 1)
        test <@ File.Exists sentinel @>
        test <@ code = 1 @>
    finally
        tryDelete sentinel

/// afterRun is a `finally`, not a success callback: it fires even when the action
/// THROWS, and the exception then propagates (afterRun does not swallow it).
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
    // Green action, afterRun exits non-zero — the verdict stays green.
    let codeGreen = withRunHooks tmpRoot (hooks None (Some "exit 9")) (fun () -> 0)
    test <@ codeGreen = 0 @>

    // Red action, afterRun exits non-zero — the verdict stays red (not laundered green).
    let codeRed = withRunHooks tmpRoot (hooks None (Some "exit 9")) (fun () -> 1)
    test <@ codeRed = 1 @>

// ---------------------------------------------------------------------------
// beforeRun: fail-closed, and ordered before the action
// ---------------------------------------------------------------------------

/// FAIL-CLOSED: a non-zero beforeRun aborts with exit 2 (NOT 1) and runs NO plugin
/// work — the action never executes.
[<Fact(Timeout = 20000)>]
let ``a non-zero beforeRun is fail-closed: exit 2 and NO plugin work`` () =
    let ran = ref false

    let code =
        withRunHooks tmpRoot (hooks (Some "exit 7") None) (fun () ->
            ran.Value <- true
            0)

    test <@ code = 2 @>
    test <@ not ran.Value @>

/// A zero-exit beforeRun proceeds to the action, and afterRun still brackets it.
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

/// ORDERING: beforeRun runs BEFORE the action — hence before the daemon is
/// contacted, since contacting the daemon is the first thing the wrapped action
/// does. The action observes beforeRun's effect already on disk.
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

    // First signal: afterRun runs, exit 143 recorded.
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

    // runHookTimeoutSec wins over the global timeout.
    test
        <@
            resolveRunHookTimeoutSec
                { cfg with
                    RunHookTimeoutSec = Some 5
                    TimeoutSec = Some 99 } = 5
        @>

    // Absent hook timeout falls through to the global timeout.
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
// `confirm` StillApplies fast-path is DELIBERATELY unwrapped (no hooks fire)
// ---------------------------------------------------------------------------

/// The `confirm` fast path — a full-suite green that still applies to the tree —
/// starts no daemon, runs no test, and MUST NOT fire the run-level hooks: there is
/// no heavy work to serialize, so nothing for a gate-lock to guard. Driven through
/// the real `executeCommand` `Confirm` arm.
[<Fact(Timeout = 30000)>]
let ``confirm StillApplies fast-path does NOT fire the run-level hooks`` () =
    withTempDir "confirm-fastpath-nohooks" (fun root ->
        // A minimal tree with a full-suite-green verdict for THIS tree, THIS binary.
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib.fs"), "module Lib\nlet x = 1\n")
        let tree = TreeHash.compute root []

        let verdict =
            FsHotWatch.Cli.Verdict.create
                FsHotWatch.Cli.Verdict.Confirm
                ({ Scope = FullSuite 1; RunId = None }: TestRunReport)
                ({ Hash = tree.Hash
                   FileCount = tree.FileCount }
                : TreeHash.Tree)
                FsHotWatch.Cli.Verdict.Green
                0
                ([ { Name = "test-prune"
                     Outcome = FsHotWatch.Cli.Verdict.PluginOutcome.Ok
                     ElapsedMs = Some 1000L
                     Summary = Some "ok" } ]
                : FsHotWatch.Cli.Verdict.PluginVerdict list)
                []

        FsHotWatch.Cli.Verdict.write root verdict

        // Sanity: the fast path is actually the one taken.
        match FsHotWatch.Cli.Verdict.priorConfirmation root [] with
        | FsHotWatch.Cli.Verdict.PriorConfirmation.StillApplies _ -> ()
        | FsHotWatch.Cli.Verdict.PriorConfirmation.MustEarn ->
            failwith "expected the StillApplies fast path to be taken"

        let beforeSentinel = freshSentinel ()
        let afterSentinel = freshSentinel ()

        let config =
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false
                BeforeRun = Some(touch beforeSentinel)
                AfterRun = Some(touch afterSentinel) }

        // IsRunning=true so the zero-projects precheck is skipped; the StillApplies
        // arm then returns before any daemon contact or hook.
        let ipc = dummyIpc (fun _ -> true)

        try
            let code =
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    root
                    "pipe"
                    (Confirm [])
                    defaultGlobalOptions
                    config
                    30.0

            test <@ code = 0 @>
            // Neither hook fired — the fast path is unwrapped.
            test <@ not (File.Exists beforeSentinel) @>
            test <@ not (File.Exists afterSentinel) @>
        finally
            tryDelete beforeSentinel
            tryDelete afterSentinel)

// ---------------------------------------------------------------------------
// Signal-handler installation.
//
// The exactly-once + exit-code CONTRACT of a signal firing is tested via
// `onRunSignal` above (the handler body, with an injected exit and the shared
// latch). Real in-process SIGINT/SIGTERM delivery CANNOT be tested here: the MTP
// test host installs its OWN SIGINT/SIGTERM handler and ABORTS the whole test
// session on receipt (empirically confirmed), regardless of our `Cancel <- true`.
// So this only checks that installation itself registers and disposes cleanly and
// does not fire afterRun on its own (no phantom teardown).
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
