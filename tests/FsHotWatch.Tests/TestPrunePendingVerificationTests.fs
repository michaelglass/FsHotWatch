/// The pending-verification queue: its sidecar, the drain rules that make "0 affected
/// tests" mean "test-equivalent to the last green run", the convergence guarantee
/// (AUTOMATION-95/99), the unreadable-ledger boundary (AUTOMATION-150) and outcome
/// classification.
///
/// Split out of `TestPrunePluginTests`; shared harness in `TestPrunePluginTestSupport`.
module FsHotWatch.Tests.TestPrunePendingVerificationTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestPrunePluginTestSupport

// =============================================================================
// PendingVerification sidecar — deterministic unit tests for load/save/hash. These pin
// both sides of every branch in `load` (missing file, whitespace-only, corrupt JSON,
// well-formed) so branch coverage is stable run-to-run rather than depending on which
// states the end-to-end queue tests happen to leave the sidecar in.
// =============================================================================

module private LedgerHelpers =
    open FsHotWatch.TestPrune

    /// Write raw bytes to the sidecar — the only way to produce the torn/corrupt shapes
    /// `save` itself can never write.
    let writeRawSidecar (tmpDir: string) (contents: string) =
        let path = PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, contents)

    /// The queue, or a test failure naming the reason it could not be read.
    let expectLoaded (tmpDir: string) : Set<string> =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Loaded queue -> queue
        | PendingVerification.LoadedQueue.Unreadable reason -> failwith $"expected Loaded, got Unreadable: {reason}"

    /// Assert the ledger is UNREADABLE — the fact that must never be spelled with the same
    /// value as an empty queue.
    let expectUnreadable (tmpDir: string) : string =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Unreadable reason -> reason
        | PendingVerification.LoadedQueue.Loaded queue ->
            failwith
                $"expected Unreadable, got Loaded %A{queue} — an unreadable ledger read as an empty queue is AUTOMATION-150 itself"

[<Fact(Timeout = 15000)>]
let ``PendingVerification: load on a MISSING file is Loaded empty, never Unreadable`` () =
    withTempDir "pv-missing" (fun tmpDir ->
        // The fresh-clone boundary: nothing was ever queued, so nothing is owed — a
        // PROVABLE empty. `Unreadable` here would wedge every fresh clone into a
        // permanent full suite.
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save then load round-trips the queue`` () =
    withTempDir "pv-roundtrip" (fun tmpDir ->
        let original = Set.ofList [ "Lib.foo"; "Lib.bar"; "Mod.baz" ]
        FsHotWatch.TestPrune.PendingVerification.save tmpDir original
        test <@ LedgerHelpers.expectLoaded tmpDir = original @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save empty then load is Loaded empty (a provable 'nothing owed')`` () =
    withTempDir "pv-empty" (fun tmpDir ->
        // `save` of an empty queue writes `[]`: well-formed, readable, and provably
        // "nothing is owed".
        FsHotWatch.TestPrune.PendingVerification.save tmpDir Set.empty
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: an EMPTY file is Unreadable (a torn write, not an empty queue)`` () =
    withTempDir "pv-whitespace" (fun tmpDir ->
        // `save` writes through an atomic tmp+rename and always emits at least `[]`, so a
        // zero-byte/whitespace file is a TORN WRITE — and reading it as `empty` absorbs
        // whatever the ledger held.
        LedgerHelpers.writeRawSidecar tmpDir "   \n  "
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: corrupt JSON is Unreadable, not empty (and never throws)`` () =
    withTempDir "pv-corrupt" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "{ this is not valid json [[["
        // Must not throw — but the failure is REPORTED, not swallowed into an empty queue
        // a caller would read as "nothing owed".
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a TRUNCATED array is Unreadable, not empty`` () =
    withTempDir "pv-truncated" (fun tmpDir ->
        // The crash-mid-write shape: valid JSON right up to where it stops.
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", \"Lib.ba"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: well-formed JSON that is not an array is Unreadable`` () =
    withTempDir "pv-not-array" (fun tmpDir ->
        // Parses cleanly, but it is not a queue: `AsArray` throws, and catching that to
        // return `empty` is the bug.
        LedgerHelpers.writeRawSidecar tmpDir "{\"pending\": [\"Lib.foo\"]}"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a bare JSON null is Unreadable`` () =
    withTempDir "pv-null" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "null"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a NON-STRING entry makes the whole ledger Unreadable`` () =
    withTempDir "pv-bad-entry" (fun tmpDir ->
        // A `Seq.choose` here silently DROPS the entry it cannot read, absorbing that
        // symbol's debt while the rest of the queue looks healthy. A symbol we cannot name
        // is a symbol we cannot verify.
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", 42, \"Lib.bar\"]"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a null entry makes the whole ledger Unreadable`` () =
    withTempDir "pv-null-entry" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", null]"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: hash is order-independent and empty-distinct`` () =
    let pv = FsHotWatch.TestPrune.PendingVerification.hash
    test <@ pv (Set.ofList [ "a"; "b"; "c" ]) = pv (Set.ofList [ "c"; "a"; "b" ]) @>
    test <@ pv (Set.ofList [ "a" ]) <> pv FsHotWatch.TestPrune.PendingVerification.empty @>

// =============================================================================
// The pending-verification queue: a changed symbol leaves it ONLY when a test run that
// covered it completed green, so that "0 affected tests" provably means "test-equivalent
// to the last green run". Three holes it closes:
//   1. The verdict ignored run outcome, so an Aborted run false-greened.
//   2. The queue drained unconditionally, so Aborted/failed runs forgot what still
//      needed testing.
//   3. Without a durable queue, a restart absorbed unverified symbols.
// These drive the real BuildCompleted → run → TestsFinished flow, seeding the symbol DB
// directly (deterministic, no FCS) and asserting against the on-disk
// `.fshw/test-prune/pending-verification.json`.
// =============================================================================

[<Fact(Timeout = 20000)>]
[<Trait("Regression", "LifecycleMailboxOrder")>]
let ``incident: a beforeRun throw aborts the run, is NOT green, and re-flags the symbols`` () =
    // A beforeRun throw propagates out of executeTests, `runTestsWithImpact` catches it,
    // and the completion carries Outcome = Aborted with Results = Map.empty. Empty results
    // trivially satisfy "failed = 0 && deferred = 0", so the verdict greened AND the queue
    // drained, permanently absorbing the symbol.
    withTempDir "tp-incident-abort" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Set directly; a restart that loaded this queue is the realistic source.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

        let beforeRun = Some(fun _ -> failwith "beforeRun boom")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let mutable startedId: Guid option = None
        let mutable completedId: Guid option = None

        let lifecycleRecorder: PluginHandler<unit, unit> =
            { Name = PluginName.create "abort-lifecycle-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | TestRunStarted started -> startedId <- Some started.RunId
                        | TestRunCompleted completed ->
                            // Keep the recorder behind the producer long enough to prove
                            // that observing TestPrune's terminal status does not imply a
                            // separate plugin mailbox has consumed the lifecycle event.
                            do! Async.Sleep 250
                            completedId <- Some completed.RunId
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeTestRunStarted; SubscribeTestRunCompleted ]
              CacheKey = None
              Teardown = None }

        host.RegisterHandler(lifecycleRecorder)
        let handler = create dbPath tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        test <@ await.Wait(TimeSpan.FromSeconds 15.0) @>

        // TestPrune publishes its terminal status from its own mailbox after queuing the
        // lifecycle event. The recorder consumes that event on another mailbox, so wait
        // for both mailboxes to drain before reading recorder-owned state.
        waitForQuiescent host 10000

        // An aborted run verified nothing.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> Assert.Fail("aborted run was reported as Completed (false green)")
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"expected Failed for an aborted run, got %A{other}")

        // Still queued, so a subsequent run re-flags it.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ queue.Contains("Lib.foo") @>

        // The abort closes the exact lifecycle opened before beforeRun. Consumers such
        // as Build use this identity to release their active-host deferral gate.
        test <@ startedId.IsSome @>
        test <@ completedId = startedId @>)

[<Fact(Timeout = 15000)>]
let ``incident: a beforeRun throw in the run-tests command surfaces as Failed, not a swallowed error`` () =
    // The `run-tests` command ran `executeTests` inside a try/with that, on a `beforeRun`
    // throw, returned a command-level JSON error and posted NOTHING back — leaving the
    // status at its prior, possibly green, value. A concurrent `fshw check` read the
    // daemon aggregate, saw no Failed status, and exited 0 while the preflight-guarded
    // suite never ran. The impact path's catch builds an Aborted lifecycle; the command
    // path must post the same one.
    withTempDir "tp-cmd-beforerun-throw" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // A preflight failure, modelling a real csrf-gate step.
        let beforeRun = Some(fun _ -> failwith "csrf-gate failed")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        // The command posts the Aborted TestsFinished asynchronously, so await the
        // terminal transition it drives before reading status.
        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        // The direct caller still hears about it ...
        test <@ result.IsSome @>
        test <@ result.Value.Contains("csrf-gate failed") @>

        // ... and so does the seam `fshw check` reads: `anyPluginFailed`
        // (IpcOutput.hasFailures) keys off exactly this status, so a non-zero verdict
        // follows rather than a stale green.
        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) -> test <@ msg.Contains("csrf-gate failed") @>
        | other -> Assert.Fail($"expected Failed with the hook output surfaced, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``incident: a test child that never becomes a live process drives the run to Failed, not a wedge`` () =
    // The launch gap: between a config's spawn and its first sign of life nothing watched
    // the wait, so an overloaded box left an infinite `WaitForExit` hanging with no child
    // ever appearing — the plugin stayed Running and `check` streamed "Waiting for
    // plugins" for hours. `sleep 30` reproduces it: no output, no exit, so with a tiny
    // a handler-scoped launch deadline the watchdog kills the tree and raises
    // `LaunchStalledException`, which the command's catch turns into the same Aborted
    // lifecycle a beforeRun throw does.
    //
    // The deadline is injected into THIS handler. A process-global env mutation here used
    // to retroactively shorten already-created handlers' deadlines while the full suite
    // ran in parallel, killing unrelated silent children on Linux and cascading into four
    // false state-machine failures.
    withTempDir "tp-launch-gap-stall" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "sleep"
                Args = "30"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            createWithLaunchDeadline (TimeSpan.FromSeconds 1.0) ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        test <@ result.IsSome @>
        test <@ result.Value.Contains("no live process") @>

        // Failed, naming the config and the launch gap — not a stale green, and not a
        // plugin stuck Running.
        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) ->
            test <@ msg.Contains("no live process") @>
            test <@ msg.Contains("TestProject") @>
        | other -> Assert.Fail($"expected Failed for a launch-stalled run, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``run-tests command with a passing beforeRun runs normally and reports Completed`` () =
    // The pass-path pair for the failing-beforeRun regression above.
    withTempDir "tp-cmd-beforerun-ok" (fun tmpDir ->
        let ran = ref false

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let beforeRun = Some(fun _ -> ran.Value <- true)

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        test <@ ran.Value @>
        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        let projects = doc.RootElement.GetProperty("projects")
        Assert.True(projects.GetArrayLength() > 0)
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString())

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed for a passing beforeRun run, got %A{other}"))

[<Fact(Timeout = 25000)>]
let ``partial failure: symbols whose only covering project passed commit; symbols touching a failed project stay queued``
    ()
    =
    // SymA's tests live only in P1 (passes), SymB's only in P2 (fails). The whole queue
    // used to drain on any completion, regardless of per-project outcome.
    withTempDir "tp-partial-fail" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symA" "A.fs" "P1" "P1Tests" "aTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symB" "B.fs" "P2" "P2Tests" "bTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.symA"; "Lib.symB" ])

        let p2flag = Path.Combine(tmpDir, "p2fail")
        File.WriteAllText(p2flag, "")

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never"))
              PendingQueueHelpers.flagConfig tmpDir "P2" p2flag ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        let queue = PendingQueueHelpers.loadQueue tmpDir

        test <@ not (queue.Contains("Lib.symA")) @>
        test <@ queue.Contains("Lib.symB") @>)

[<Fact(Timeout = 30000)>]
let ``mid-run change: a green run commits only its launch set; a symbol that arrives mid-run stays queued and triggers a rerun``
    ()
    =
    // Run 1 launches against {Lib.foo} and sleeps ~1.5s. Mid-flight a real FCS FileChecked
    // changes `bar`, which the plugin enqueues through the genuine write-through path, and
    // a BuildCompleted sets PendingRerun. Run 1's launch SNAPSHOT was {Lib.foo}, so its
    // green completion commits only that; `bar` survives and the rerun covers it. No
    // file-rewrite simulation: the snapshot is captured at dispatch and the commit is
    // launch-set-scoped.
    withTempDir "tp-midrun" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        // One test per exported function, so a change to either selects its own test.
        let libSource1 = "module Lib\nlet foo (x: int) = x + 1\nlet bar (x: int) = x + 1\n"

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let fooTest () = assert (foo 1 = 2)

[<Fact>]
let barTest () = assert (bar 1 = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // The sleep is the window the mid-run injection needs.
        let configs =
            [ { Project = "Lib"
                Command = "sh"
                Args = "-c \"sleep 1.5; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        emitBuildAndWaitTerminal host

        for f in [ libFile; testsFile ] do
            match pipeline.CheckFile(AbsFilePath.create f) |> Async.RunSynchronously with
            | Some r -> host.EmitFileChecked(r)
            | None -> failwith $"CheckFile failed for {f}"

        waitForPluginIdle host "test-prune" 10.0
        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Change `foo`'s body, so `fooTest` is the affected test.
        let libSource2 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 1\n"
        File.WriteAllText(libFile, libSource2)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (foo change) failed"

        waitForPluginIdle host "test-prune" 10.0

        // Run 1 covers fooTest, and sleeps 1.5s.
        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        // Mid-run, while run 1 is still sleeping: change `bar`'s body, so a real
        // FileChecked enqueues it, then a BuildCompleted sets PendingRerun.
        let libSource3 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 99\n"
        File.WriteAllText(libFile, libSource3)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (bar change) failed"

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for the CONVERGED state — empty queue AND Completed — not a single terminal
        // transition: the rerun re-enters Running between run 1's completion and the final
        // settle, so a one-shot terminal wait races it. The invariant that makes
        // convergence possible is that `bar` is not committed by run 1: had run 1
        // committed its non-launch-set arrival, the rerun would never have re-tested it.
        let converged () =
            let q = PendingQueueHelpers.loadQueue tmpDir

            let green =
                match host.GetStatus("test-prune") with
                | Some(Completed _) -> true
                | _ -> false

            Set.isEmpty q && green

        waitUntil converged 20000

        let queueFinal = PendingQueueHelpers.loadQueue tmpDir
        test <@ Set.isEmpty queueFinal @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed after launch-set commit + rerun drained the queue, got %A{other}"))

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-228: a rerun queued for debt the active run clears preserves that run's evidence`` () =
    // The production lifecycle this pins is RED test -> production edit -> green retry.
    // While the retry is in flight, BatchChecked sees the same durable debt and queues a
    // rerun. The green retry then clears that debt. The queued rerun is stale now: if it
    // launches, it selects zero projects and its NoProjectsSelected completion overwrites
    // the passing evidence the gate was waiting for.
    withTempDir "tp-stale-rerun" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.singleton "Lib.foo")

        let started = Path.Combine(tmpDir, "started")
        let release = Path.Combine(tmpDir, "release")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch '{started}'; while [ ! -f '{release}' ]; do sleep 0.05; done\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists started) 10000

        // Re-observe the same debt while its covering run is active. RunCommand is a
        // mailbox barrier: when it returns, the preceding BatchChecked has set
        // PendingRerun, so releasing the runner cannot race the setup.
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously |> ignore

        File.WriteAllText(release, "")
        waitForQuiescent host 20000

        let completed = getCompleted ()
        Assert.Single(completed) |> ignore
        test <@ not (RunVerification.verifiedNothing completed.Head.Verification) @>

        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ Set.isEmpty queue @>)

type private A163ScenarioOutcome =
    { RunCount: int
      Queue: Set<string>
      Status: PluginStatus option }

let private runA163CohortScenario name trigger testExitCode =
    withTempDir name (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let runMarker = Path.Combine(tmpDir, "runs")
        let started = Path.Combine(tmpDir, "started")
        let release = Path.Combine(tmpDir, "release")
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let libSource1 = "module Lib\nlet foo (x: int) = x + 1\n"
        let libSource2 = "module Lib\nlet foo (x: int) = x + 2\n"

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let fooTest () = assert (foo 1 = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Prime the persisted symbol graph in an analysis-only host. The second host is
        // the cold daemon: empty in-memory state over a warm on-disk impact database.
        let primingHost = PluginHost.create checker tmpDir
        primingHost.RegisterHandler(create dbPath tmpDir None None None None None [])
        primingHost.EmitBuildCompleted(BuildSucceeded)
        waitForPluginIdle primingHost "test-prune" 5.0

        for file in [ libFile; testsFile ] do
            match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
            | Some result -> primingHost.EmitFileChecked(result)
            | None -> failwith $"priming check failed for {file}"

        emitBatchAndQuiesce primingHost [ libFile; testsFile ]

        let configs =
            [ { Project = "Lib"
                Command = "sh"
                Args =
                  $"-c \"printf 'run\\n' >> '{runMarker}'; touch '{started}'; while [ ! -f '{release}' ]; do sleep 0.05; done; exit {testExitCode}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 15
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create checker tmpDir
        host.RegisterHandler(create dbPath tmpDir (Some configs) None None None None [])

        host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
        |> Async.RunSynchronously
        |> ignore

        File.WriteAllText(libFile, libSource2)
        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists started) 10000

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some result -> host.EmitFileChecked(result)
        | None -> failwith "cold-scan changed-file check failed"

        host.EmitBatchChecked(
            { fakeBatchChecked [ libFile ] with
                Trigger = trigger }
        )

        // The command is deliberately held until the cohort seal is observed. A fixed
        // sleep made this test assert scheduler speed on loaded Linux runners: the full
        // run could finish before CheckFile, turning BootScan into a real second run.
        host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously |> ignore
        File.WriteAllText(release, "")

        waitForQuiescent host 20000

        { RunCount = File.ReadAllLines(runMarker).Length
          Queue = PendingQueueHelpers.loadQueue tmpDir
          Status = host.GetStatus("test-prune") })

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: boot-scan symbols discovered during a green full run are covered without a second run`` () =
    // The production change this catches is treating `BootScan` like `InSessionBatch`
    // when its cohort seal arrives during the full run that a cold confirm launched.
    // The scan is a baseline over the same built tree, so that full run covers its
    // symbols; queueing another run silently doubles CI.
    let outcome = runA163CohortScenario "tp-a163-boot-scan" BootScan 0
    Assert.Equal(1, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>

    match outcome.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the one full run to complete green, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: an in-session cohort discovered during a full run still queues exactly one rerun`` () =
    // Mutation caught: matching every BatchChecked as BootScan would disable the real
    // edit queue. The only difference from the regression above is cohort provenance.
    let trigger = InSessionBatch [ SourceChanged [ "Lib.fsx" ] ]
    let outcome = runA163CohortScenario "tp-a163-in-session" trigger 0
    Assert.Equal(2, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>

    match outcome.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the edit rerun to converge green, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: a failing full run cannot discharge boot-scan debt`` () =
    // Mutation caught: absorbing boot debt on the requested scope rather than the
    // completed run's actual green evidence would erase work that no passing test proved.
    let outcome = runA163CohortScenario "tp-a163-failed-full" BootScan 1
    Assert.Equal(1, outcome.RunCount)
    test <@ outcome.Queue.Contains "Lib.foo" @>

    match outcome.Status with
    | Some(Failed _) -> ()
    | other -> Assert.Fail($"expected the failed full run to stay red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``restart persistence: a non-empty queue survives a daemon restart and is re-flagged`` () =
    // Session 1 queues Lib.foo (covered by P1) but never proves it green. Session 2 — a
    // fresh plugin over the same on-disk sidecar and DB — must load the queue, re-flag
    // Lib.foo and run P1 again. An in-memory-only queue dies with the daemon, so the
    // restart silent-greens.
    withTempDir "tp-restart" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Session-1 residue: a symbol never proven green.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // P1 passes this time, so the restart-driven run covers Lib.foo and commits it —
        // proving it was re-flagged and actually re-tested, not silently absorbed.
        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch {ranMarker}; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        test <@ File.Exists ranMarker @>

        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed after the re-flagged symbol tested green, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``no-covering-test symbol drops from the queue at flush without wedging it`` () =
    // Retaining it would wedge the queue forever: every run selects zero tests, the queue
    // never empties, and the verdict is permanently non-green.
    withTempDir "tp-uncovered" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        // A covered symbol too, so the DB is non-empty and indexed.
        PendingQueueHelpers.seedCoveredSymbol db "Lib.covered" "Lib.fs" "P1" "P1Tests" "coveredTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.uncovered"; "Lib.covered" ])

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        let queue = PendingQueueHelpers.loadQueue tmpDir

        // The uncovered symbol drops with no test to wait on; the covered one commits
        // because P1 passed. An empty queue is the not-wedged condition.
        test <@ not (queue.Contains("Lib.uncovered")) @>
        test <@ not (queue.Contains("Lib.covered")) @>
        test <@ Set.isEmpty queue @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed (queue drained, not wedged), got %A{other}"))

// AUTOMATION-278 — the FIFTH aggregator, and the one that survived the first fix.
//
// The per-symbol green-commit folded `TestResult.isPassed` over the covering projects.
// `isPassed` was TRUE for `TestsNoMatch`, so a symbol whose covering project ran under an
// impact-derived class filter that matched ZERO tests had its test debt DISCHARGED and
// left `pending-verification.json` — verified by a project that executed nothing. That is
// the harm AUTOMATION-275 exists to prevent ("widen, never wipe"), one fold over, and the
// repo-local FSHW-VERDICT-001 analyzer cannot see it: the predicate sits behind a
// `match` in a lookup lambda.
//
// End to end rather than a unit fold, because the bug was in the WIRING: the fold looked
// correct in isolation and was wrong about which results it was folding over.
[<Fact(Timeout = 20000)>]
let ``a covering project that matched ZERO tests does not discharge a pending symbol`` () =
    withTempDir "tp-nomatch-commit" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // POSITIVE CONTROL for the fixture itself. Without it, a run that never invoked
        // P1 at all would also leave `Lib.foo` queued and this test would pass having
        // proved nothing.
        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                // Exit 8 is Microsoft.Testing.Platform's "Zero tests ran". A
                // `FilterTemplate` is required for the run to count as FILTERED, which
                // is what makes exit 8 a zero MATCH rather than a plain failure.
                Args = $"-c \"touch {ranMarker}; exit 8\""
                Group = "default"
                Environment = []
                FilterTemplate = Some "--filter-class {classes}"
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // The runner really was invoked and really did report zero matches.
        test <@ File.Exists ranMarker @>

        // THE ASSERTION. The debt is still owed: nothing verified `Lib.foo`.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ queue.Contains("Lib.foo") @>

        // And the run-level verdict says so too, rather than reporting a green over a
        // project that executed nothing.
        match host.GetStatus("test-prune") with
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"expected a non-green terminal for a run that matched zero tests, got %A{other}"))

// --- classifyTestOutcome: the structured report, not the process exit code, decides
// green/red; the exit code is a tie-break only when no report exists.

open FsHotWatch.ProcessHelper

let private rep total passed failed skipped other : Flakiness.TestReport =
    { Total = total
      Passed = passed
      Failed = failed
      Skipped = skipped
      Other = other }

let private isFailed result =
    match result with
    | TestsFailed _ -> true
    | _ -> false

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with a clean report is GREEN (the shutdown flake)`` () =
    // Exit 7 is MTP's dirty shutdown; the report shows zero failures and >= 1 test.
    let report = Some(rep 12 12 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "host crashed during shutdown"))

    test <@ TestResult.verifiedGreen result @>

[<Fact(Timeout = 5000)>]
let ``classify: report with a failed test is RED even on exit 0`` () =
    let report = Some(rep 3 2 1 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Succeeded(ProcessOutput.Drained ""))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: report with an other (raw-throw) result is RED`` () =
    let report = Some(rep 3 2 0 0 1)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(2, ProcessOutput.Drained ""))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with NO report from a capable runner is ERRORED, not failed`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "aborted"))

    test <@ TestResult.isErrored result @>
    test <@ not (isFailed result) @>
    test <@ not (TestResult.verifiedGreen result) @>

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with no report from an UNKNOWN runner stays FAILED (no regression)`` () =
    // Under NoReportRequested the exit code is the only signal there is.
    let result =
        classifyTestOutcome
            NoReportRequested
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(1, ProcessOutput.Drained "boom"))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: clean exit with no report is PASSED`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            TimeSpan.Zero
            (ProcessOutcome.Succeeded(ProcessOutput.Drained "ok"))

    test <@ TestResult.verifiedGreen result @>

[<Fact(Timeout = 5000)>]
let ``classify: unfiltered zero-test report with non-zero exit is RED (empty suite is a problem)`` () =
    let report = Some(rep 0 0 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(8, ProcessOutput.Drained "Zero tests ran"))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: a timeout is TimedOut regardless of a flushed report`` () =
    let report = Some(rep 5 5 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            (TimeSpan.FromSeconds 30.0)
            (ProcessOutcome.TimedOut(TimeSpan.FromSeconds 30.0, ProcessOutput.Drained "stuck", KillOutcome.Killed))

    test <@ TestResult.isTimedOut result @>

// ---------------------------------------------------------------------------
// AUTOMATION-294 — a KILLED host is an abort, and a real red is still a red.
//
// Under CPU load the gate reported large numbers of 0ms "failures". A 0ms failure is a
// test that never ran: the host was killed and everything it had not reached was written
// out in the same shape as a test that ran and failed. That is a non-result rendered as a
// definite negative — the fail-open degrade inverted, and more expensive, because a red
// gets investigated where a green merely gets trusted.
//
// Every test in this block has a partner asserting the OTHER direction. A fix that made a
// genuine mass failure look like an abort would be the same lie with the sign flipped.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: a SIGKILLed host is an ABORT even though it flushed a report full of failures`` () =
    // The exact shape the ticket records: the host dies mid-suite and MTP still leaves a
    // report behind whose rows for tests it never reached are marked failed at 0ms.
    // Reading that report as the verdict is what minted the phantom mass regression.
    let phantomMassRegression = Some(rep 2171 2032 139 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested phantomMassRegression)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(137, ProcessOutput.Drained "failed FsHotWatch.Tests.AbsFilePath.roundtrips (0ms)"))

    test <@ TestResult.isErrored result @>
    test <@ not (isFailed result) @>
    test <@ not (TestResult.verifiedGreen result) @>
    // Never a pass and never a failure: NOTHING was verified.
    test <@ TestResult.verdict result = NothingVerified @>

    // And the reason SAYS what killed it, so nobody has to infer "0ms means never ran".
    let reason = TestResult.output result
    test <@ reason.Contains "SIGKILL" @>
    test <@ reason.Contains "137" @>
    test <@ reason.ToUpperInvariant().Contains "NOTHING" @>
    // The partial report is named as a transcript, with its counts, rather than hidden.
    test <@ reason.Contains "PARTIAL" @>
    test <@ reason.Contains "139" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: THE OTHER DIRECTION — a real mass failure is still RED, not an abort`` () =
    // Same report, same counts, same 139 failures. The ONLY difference is that the runner
    // reached its own exit and chose the code (MTP's 2 = "at least one test failed")
    // instead of being killed. This must stay a red, or the fix has merely inverted the
    // lie: a gate that reported every genuine regression as "the machine was busy" would
    // be worse than the bug it replaced.
    let realMassRegression = Some(rep 2171 2032 139 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested realMassRegression)
            false
            (TimeSpan.FromMinutes 4.0)
            (ProcessOutcome.Failed(2, ProcessOutput.Drained "failed FsHotWatch.Tests.Foo.bar (312ms)"))

    test <@ isFailed result @>
    test <@ not (TestResult.isErrored result) @>
    test <@ TestResult.verdict result = Refuted @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: a SIGABRTed host is an abort even when it wrote a CLEAN report`` () =
    // The direction that must NOT go green. Exit 134 is what a real gate run produced
    // (an unhandled TimeoutException → the runtime aborts). A clean report from a process
    // that never reached its own exit describes the part of the suite it got through, and
    // outcome 2 ("a report showing zero failures beats the exit code") would have called
    // that a pass.
    let partialButClean = Some(rep 812 812 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested partialButClean)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(134, ProcessOutput.Drained ""))

    test <@ not (TestResult.verifiedGreen result) @>
    test <@ TestResult.isErrored result @>
    test <@ (TestResult.output result).Contains "SIGABRT" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: the dirty-shutdown flake (exit 7) is STILL green — no regression`` () =
    // The guard against over-reach. Exit 7 is MTP's dirty shutdown, a code the runner
    // CHOSE; it is not a signal death, so the clean report still decides. If the new arm
    // swallowed it, every dirty shutdown would stop being a pass.
    let clean = Some(rep 12 12 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested clean)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "host crashed during shutdown"))

    test <@ TestResult.verifiedGreen result @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: an abort report never counts the killed run's transcript as failures`` () =
    // The CONSOLE half. A killed host's capture still holds per-test rows, and
    // `formatFailureReport` would head them "N test(s) failed" — which is the sentence
    // that sent people hunting a regression that was not there.
    let transcript =
        "Discovering: probe\nfailed FsHotWatch.Tests.AbsFilePath.roundtrips (0ms)\nfailed FsHotWatch.Tests.Foo.bar (0ms)"

    let abort =
        formatAbortReport "FsHotWatch.Tests" savedLog "test host was KILLED by SIGKILL (exit 137)" transcript
        |> String.concat "\n"

    test <@ abort.Contains "ABORTED" @>
    test <@ abort.Contains "SIGKILL" @>
    test <@ abort.ToUpperInvariant().Contains "NOTHING WAS VERIFIED" @>
    test <@ abort.Contains "NOT a test failure" @>
    // It must NEVER produce the count-of-failures headline.
    test <@ not (abort.Contains "test(s) failed") @>
    // The transcript is still shown — it is the only evidence of how far the run got.
    test <@ abort.Contains "AbsFilePath.roundtrips" @>

    // THE OTHER DIRECTION: the same lines through the FAILURE report still say "failed",
    // because for a run that finished they are findings.
    let failure =
        formatFailureReport "FsHotWatch.Tests" savedLog transcript |> String.concat "\n"

    test <@ failure.Contains "2 test(s) failed" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: an aborted project is a HostAborted ledger entry, and a failed one still Error`` () =
    // The VERDICT half. At `Error` severity the abort was counted by `failingDiagnostics`,
    // so the exit code said 1 and `verdict.json` said `red` about a run in which nothing
    // failed.
    let aborted: TestResults =
        { Results = Map.ofList [ "ProjA", TestsErrored "test host was KILLED by SIGKILL (exit 137)" ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty aborted |> List.exactlyOne).Entry

    test <@ entry.Severity = FsHotWatch.ErrorLedger.HostAborted @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isRunnerAbort entry @>
    // Never a failure, under EITHER warn-fail policy.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry) @>
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing false entry) @>
    // And not a DEFER either: the remedies are opposite, and "re-run once the build
    // settles" is advice that never arrives for a host killed by a busy box.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild entry) @>
    test <@ entry.Message.ToLowerInvariant().Contains "aborted" @>

    // THE OTHER DIRECTION: a genuine failure is untouched — still Error, still failing.
    let failed: TestResults =
        { Results = Map.ofList [ "ProjB", TestsFailed("Some.Test FAILED", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let realFailures = failuresOf Map.empty failed
    test <@ not realFailures.IsEmpty @>

    test
        <@
            realFailures
            |> List.forall (fun f ->
                f.Entry.Severity = FsHotWatch.ErrorLedger.Error
                && FsHotWatch.ErrorLedger.ErrorEntry.isFailing true f.Entry
                && not (FsHotWatch.ErrorLedger.ErrorEntry.isRunnerAbort f.Entry))
        @>

// AUTOMATION-454 — the teardown boundary, seen from the plugin that consumes it.
//
// A per-project timeout whose TEARDOWN also failed still has to become a terminal project
// result, and it has to stay distinguishable from the two things it is not: a suite whose
// tests failed, and a run that reported nothing at all.
[<Fact(Timeout = 5000)>]
let ``classify: a timeout whose teardown never answered is still terminal, and says so`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            (TimeSpan.FromSeconds 300.0)
            (ProcessOutcome.TimedOut(
                TimeSpan.FromSeconds 300.0,
                ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0)
            ))

    // Terminal, and TIMED OUT — never `TestsFailed`. Conflating the two would put a
    // wedged runner in the same bucket as a suite that ran and went red.
    test <@ TestResult.isTimedOut result @>
    test <@ not (isFailed result) @>

    // ...and the leaked tree rides on the text the operator reads, so "the project timed
    // out" is not mistaken for "and the runaway is over".
    match result with
    | TestsTimedOut(output, _, _, _) ->
        test <@ output.Contains "KILL TIMED OUT" @>
        test <@ output.Contains "STILL RUNNING" @>
    | other -> failwith $"expected TestsTimedOut, got %A{other}"

// =============================================================================
// AUTOMATION-95 / AUTOMATION-99 — the check must CONVERGE, never rest on a verdict
// nobody earned. One defect, two polarities.
//
// The pending-verification queue had exactly ONE drain trigger, the `BuildCompleted`
// handler. But on a scan `performScan` awaits BuildPlugin before dispatching the FCS
// tiers, so every symbol the SCAN discovers lands in the queue strictly AFTER the only
// event that could have run its tests. `BatchChecked` flushed those symbols, even
// computed their affected tests, then returned without running anything — so the queue
// was never drained and `check` reported whatever terminal status test-prune happened to
// hold: a stale `Completed` is a false green with symbols pending; a stale `Failed` is a
// permanently stuck red whose work never runs. Live: `check` returned in one second,
// exit 0, zero daemon activity, while the plugin's own log said "24 affected tests".
//
// Whoever DISCOVERS unverified symbols is responsible for RUNNING them.
// =============================================================================

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95/99: BatchChecked drains a pending queue instead of resting on a stale verdict`` () =
    // BatchChecked is the cohort seal — the first moment the scan's symbols are known —
    // and so the only event left that can drain them.
    withTempDir "tp-batch-drain" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // A symbol awaiting verification, as a scan's FileChecked pass would leave it.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch {ranMarker}; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // Deliberately no BuildCompleted: on a scan it has come and gone before these
        // symbols existed.
        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // It RAN the covering tests rather than reporting on them ...
        test <@ File.Exists ranMarker @>

        // ... and only then went green.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected an EARNED Completed after the drain, got %A{other}"))

// =============================================================================
// AUTOMATION-150 — an unreadable ledger is not an empty one.
//
// The queue file records what is still OWED, so when it cannot be READ the debt is
// UNKNOWN — and "unknown" is not "nothing". `load` swallowed a corrupt/truncated sidecar
// into `empty`, byte-identical to what a genuinely clean queue produces, so the entire
// outstanding test debt vanished into a `with _ -> empty` and the module broke its own
// stated invariant: the queue may only err toward OVER-testing.
//
// The boundary that keeps the fix honest: "the file does not exist" (first run, fresh
// clone) and "the file exists and I could not read it" are DIFFERENT facts. The first is
// legitimately empty; collapsing them either wedges every fresh clone into a permanent
// full suite or re-opens the hole. All three tests below pin that boundary.
// =============================================================================

/// The two-project runner these tests share. P1 covers `Lib.foo` and P2 covers `Lib.debt`,
/// so an impact-filtered selection driven by a changed `Lib.foo` runs P1 and SKIPS P2,
/// while a widened full-suite run touches both.
let private ledgerRunner (project: string) (marker: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch {marker}; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: an UNREADABLE ledger widens to the FULL suite rather than greening on nothing`` () =
    // The sidecar EXISTS but is truncated mid-array — a crashed/torn write — and it once
    // held real debt (Lib.debt, covered by P2, never proven green). Catching the parse
    // throw and returning `empty` makes the drain gate (`if Set.isEmpty pendingQueueRef
    // then return`) read "nothing owed", run ZERO tests, and rest on a green verdict.
    //
    // `Unreadable` is a DIFFERENT VALUE that cannot be mistaken for an empty queue, and a
    // selection made without the ledger cannot be trusted — so the run widens to every
    // configured project in full, the only scope that discharges a debt of unknown
    // membership.
    withTempDir "tp-ledger-unreadable" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        // The file EXISTS: this is emphatically not a fresh clone.
        let path = FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "[\"Lib.deb")

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // The cold-scan shape again: BatchChecked is the only event that can drain what
        // the scan discovered.
        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // It RAN — an unreadable ledger owes MORE testing, never less ...
        test <@ File.Exists p1Ran @>
        // ... and it ran EVERYTHING. P2 is the project a filtered selection skips, and
        // the one holding the debt the corrupt file swallowed.
        test <@ File.Exists p2Ran @>

        // And it SELF-HEALS: a full suite passed every configured project, so every symbol
        // the lost ledger could have held is verified and the corrupt file is rewritten.
        // The next session loads a readable ledger and goes back to impact filtering
        // rather than grinding a full suite forever.
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: a MISSING ledger (fresh clone) is legitimately empty and does NOT force a full suite`` () =
    // The trade this fix must NOT make: fail-open swapped for a stuck full suite. A fresh
    // clone has no ledger at all, which is a provable "nothing owed" and must stay a fast
    // no-op.
    //
    // AUTOMATION-110: "fresh clone" here means a fresh LEDGER. A repository with no
    // full-suite baseline does widen — once, to earn it — which is a different rule with
    // its own tests; this one is about the missing-vs-unreadable ledger boundary, so the
    // baseline is given.
    withTempDir "tp-ledger-missing" (fun tmpDir ->
        seedBaseline tmpDir [ "P1"; "P2" ]
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        test <@ not (File.Exists(FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir)) @>

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])

        // Give a run every chance to start before concluding none did.
        waitUntil (fun () -> File.Exists p1Ran || File.Exists p2Ran) 3000
        waitForQuiescent host 5000

        test <@ not (File.Exists p1Ran) @>
        test <@ not (File.Exists p2Ran) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: a genuinely EMPTY ledger stays a fast no-op (not a widened run)`` () =
    // The other half of the boundary. Misclassify `[]` as unreadable and every idle daemon
    // grinds a full suite forever.
    withTempDir "tp-ledger-empty" (fun tmpDir ->
        // AUTOMATION-110: a baseline, so the only rule under test is the ledger's.
        seedBaseline tmpDir [ "P1"; "P2" ]
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir Set.empty
        test <@ File.Exists(FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir) @>

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])

        waitUntil (fun () -> File.Exists p1Ran || File.Exists p2Ran) 3000
        waitForQuiescent host 5000

        test <@ not (File.Exists p1Ran) @>
        test <@ not (File.Exists p2Ran) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-99: a symbol covered only by an unconfigured test project drops instead of wedging the verdict red``
    ()
    =
    // The symbol DB indexes test methods from EVERY project it analyzed, which is not the
    // set of projects fshw is configured to run. A symbol covered only by an unconfigured
    // project can never be proven green: its covering project never executes, so it never
    // lands in a run's results and never commits. Live: two full suites passed
    // back-to-back and `check` still exited 1, because the only covering tests lived in
    // FsHotWatch.IntegrationTests, which the daemon does not run.
    //
    // "Covered" means "covered by a test we can actually run"; anything else is
    // indistinguishable from having no covering test and drops by the same rule.
    withTempDir "tp-unrunnable" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath

        // Lib.orphan's ONLY covering test lives in P2 — which is not in `configs`.
        PendingQueueHelpers.seedCoveredSymbol db "Lib.orphan" "Orphan.fs" "P2" "P2Tests" "orphanTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.orphan" ])

        // Only P1 is runnable. P2 is indexed but will never execute.
        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // Unverifiable by construction, so dropped rather than retained forever.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.orphan")) @>

        match host.GetStatus("test-prune") with
        | Some(PluginStatus.Failed(msg, _, _)) ->
            Assert.Fail($"check wedged red on a symbol no runnable test covers: %s{msg}")
        | _ -> ())

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95: a plugin with a test run in flight reports BUSY, so no verdict can resolve mid-run`` () =
    // The third facet: `check` handed back a verdict WHILE the run that would have
    // produced it was still executing. "Busy" meant only "has events queued in its
    // mailbox", blind to the background work a handler launches via RunExclusive and then
    // returns from — so the host saw an idle mailbox and WaitForComplete resolved mid-run.
    // Live: a run launched 11:30:17, still executing at 11:30:34, and the daemon logged
    // "all plugins already terminal" while `check` exited 0.
    withTempDir "tp-busy-during-run" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"sleep 2; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        // By the time status reaches Running the BuildCompleted handler has returned, so
        // the MAILBOX is drained and the only thing that can keep the plugin busy is the
        // background run itself.
        let isRunning () =
            match host.GetStatus("test-prune") with
            | Some(Running _) -> true
            | _ -> false

        waitUntil isRunning 5000
        test <@ isRunning () @>

        test <@ host.AnyPluginBusy() @>

        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed once the in-flight run finished, got %A{other}"))

// --- AUTOMATION-113: an unanalysable file must not vanish from the impact graph ---
//
// A file whose symbol analysis fails contributes NO symbols, and the `Error` branch
// simply `return state`d: the file was dropped, the impact graph never saw it, a change
// to it diffed against nothing and selected NO tests, and the check went green having run
// nothing relevant — silently. It now forces the COARSE selection (every test project, in
// full) and says so loudly. Safe over-selection beats silent under-selection.

let private threeProjects =
    [ testConfigNamed "Alpha.Tests"
      testConfigNamed "Beta.Tests"
      testConfigNamed "Gamma.Tests" ]

[<Fact(Timeout = 15000)>]
let ``coarseFallbackProjects is a no-op while every file analyses cleanly`` () =
    // A healthy tree pays nothing: the dependency-fanout set passes through untouched.
    let fanout = Set.ofList [ "Beta.Tests" ]

    let result = coarseFallbackProjects threeProjects Set.empty fanout

    test <@ result = fanout @>

[<Fact(Timeout = 15000)>]
let ``one unanalysable file force-runs EVERY test project`` () =
    // The file is invisible to the symbol graph, so no per-symbol selection can be trusted
    // to cover it, and the whole suite is the only sound response to "I cannot tell you
    // what is affected".
    let unanalyzable = Set.ofList [ "src/Lib/Broken.fs" ]

    let result = coarseFallbackProjects threeProjects unanalyzable Set.empty

    test <@ result = Set.ofList [ "Alpha.Tests"; "Beta.Tests"; "Gamma.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``the coarse fallback is a superset of the dependency fanout, never a replacement`` () =
    // Both widenings are safe directions; neither may cancel the other out.
    let unanalyzable = Set.ofList [ "src/Lib/Broken.fs" ]
    let fanout = Set.ofList [ "Beta.Tests" ]

    let result = coarseFallbackProjects threeProjects unanalyzable fanout

    test <@ Set.isSubset fanout result @>
    test <@ result = Set.ofList [ "Alpha.Tests"; "Beta.Tests"; "Gamma.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``a non-empty coarse fallback disables the zero-affected skip gate`` () =
    // The skip gate in `runTestsWithImpact` greens with 0 tests run when there are no
    // affected classes AND no force-run projects, so a non-empty force-run set is what
    // keeps an unanalysable file away from that verdict. Asserted through the same
    // emptiness predicate `confirm` reads.
    let forceRun =
        coarseFallbackProjects threeProjects (Set.ofList [ "src/Lib/Broken.fs" ]) Set.empty

    test <@ not (Set.isEmpty forceRun) @>

[<Fact(Timeout = 15000)>]
let ``an unanalysable file is reported LOUDLY, naming the file and the reason`` () =
    // A log line and a plugin status the next file's `Completed` overwrites is nothing a
    // consumer can see. The diagnostic must name the file, carry the reason, and be at
    // least Warning severity so the default warn-fail policy denies the check a green.
    let reason = "Parse errors: XML comment is not placed on a valid language element."

    let entry = unanalyzableFileDiagnostic "src/Lib/Broken.fs" reason

    test <@ entry.Severity = FsHotWatch.ErrorLedger.Warning @>
    test <@ entry.Message.Contains "src/Lib/Broken.fs" @>
    test <@ entry.Message.Contains "XML comment is not placed on a valid language element." @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry @>

    let detail = entry.Detail |> Option.defaultValue ""
    test <@ detail.Contains "INVISIBLE to the impact graph" @>

// --- AUTOMATION-112: the full-suite scope is part of the task cache key ---

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a confirm cannot replay an impact-filtered run's cached verdict`` () =
    // Everything else about the tree is identical — same symbols, empty queue, same deps
    // — so without the scope in the key, the first thing `fshw confirm` does on an
    // unchanged tree is hit the entry an earlier impact-filtered `check` wrote, replay its
    // green, and never start a test process: a filtered verdict laundered into a merge
    // verdict with no run at all.
    let keyWithScope (fullSuiteScope: string option) =
        cacheKeyFor
            (fun () -> "same-symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> fullSuiteScope)
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    let innerLoopKey = keyWithScope None
    let fullSuiteKey = keyWithScope (Some "full")

    test <@ innerLoopKey.IsSome @>
    test <@ fullSuiteKey.IsSome @>
    test <@ innerLoopKey <> fullSuiteKey @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: the inner-loop key is unchanged by the scope salt`` () =
    // `None` rather than "impact" for the inner loop keeps the merkle entry OMITTED, so
    // the ordinary key stays byte-identical to the pre-feature one and existing on-disk
    // entries keep hitting. `confirm` pays for its own scope; the fast loop pays nothing.
    let withScopeThunk =
        cacheKeyFor
            (fun () -> "s")
            (fun () -> None)
            (fun () -> Some "deps")
            (fun () -> "struct")
            (fun () -> None)
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    // The same inputs, hand-built with no full-suite-scope entry at all.
    let expected =
        FsHotWatch.TaskCache.merkleCacheKey
            [ "plugin-version", "test-prune-merkle-v2"
              "event", "BuildCompleted"
              "changed-symbols", "s"
              "project-structure", "struct"
              "build-outcome", "succeeded"
              "depends-on", "deps" ]

    test <@ withScopeThunk = Some expected @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: two full-suite runs over the same tree DO share a key`` () =
    // Determinism of the key is not equivalence of the world. Reading this as "a second
    // `confirm` over an unchanged tree may replay a run that genuinely WAS full-suite" is
    // the belief that produced AUTOMATION-161: the key does not pin the TREE, because on a
    // cold scan BuildCompleted is dispatched before the FCS pass and `changed-symbols` is
    // empty whatever the tree holds.
    //
    // Sharing the key is still right — it lets a WARM daemon skip a redundant in-session
    // run, and lets the entry a `TestsFinished` writes be found by the next
    // `BuildCompleted`. What must not follow is a REPLAY into a process with no run of its
    // own; the session-evidence gate below forbids that.
    let fullSuiteKey () =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    test <@ fullSuiteKey () = fullSuiteKey () @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-161: cacheKeyFor refuses BuildCompleted while the process has NO test evidence`` () =
    // A cached BuildCompleted entry ASSERTS a test result, and a process whose own state
    // records no run may not make that assertion. `None` means no replay and no write,
    // exactly as a non-empty pending queue and an outstanding failure already do.
    let keyWithEvidence (hasEvidence: bool) =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            (fun () -> hasEvidence)
            (BuildCompleted BuildSucceeded)

    test <@ (keyWithEvidence false).IsNone @>
    // Once a run has covered something, the warm in-session fast path is back.
    test <@ (keyWithEvidence true).IsSome @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-161: the TestsFinished WRITE is not gated on session evidence`` () =
    // The WRITE mints the entry the next BuildCompleted hits, and it is computed at
    // DISPATCH time — before the run this message carries has been folded into state, so
    // there IS no evidence to see. Gating it would mean the cache is never written and the
    // warm in-session fast path dies with it. Safe, because this key is never used for a
    // LOOKUP: the framework does not replay over a `Custom` message, whose payload is not
    // in its key (see PluginFrameworkTests).
    let allPassed =
        Custom(
            TestsFinished(
                { RunId = Guid.NewGuid()
                  StartedAt = DateTime.UtcNow },
                { RunId = Guid.NewGuid()
                  TotalElapsed = TimeSpan.Zero
                  Outcome = Normal
                  Results = Map.ofList [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero) ]
                  Verification = Ran RunScope.FullSuite },
                fullSuiteLaunch [ "ProjA" ]
            )
        )

    let key =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            // No evidence yet — this run is the one about to provide it.
            (fun () -> false)
            allPassed

    test <@ key.IsSome @>

// --- AUTOMATION-129: `confirm`'s scope is a PROJECTION of RunCoverage ---
//
// `classifyRunScope` derived `confirm`'s scope independently from `LastResults`, while the
// ledger decided what a run may CLEAR from `RunCoverage` — two answers to one question
// with nothing making them agree, so `confirm` could go green on a scope the ledger would
// never have granted. `scopeOf` is a VIEW of the ledger's own value, so they cannot
// disagree by construction.

[<Fact(Timeout = 10000)>]
let ``scopeOf: every project executed in FULL is the only whole-suite scope`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]

    let everything =
        Map.ofList [ "Alpha.Tests", CoveredWholeProject; "Beta.Tests", CoveredWholeProject ]

    test <@ scopeOf projects everything = ScopeFull 2 @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: a class-filtered project makes the run a SUBSET, never full-suite`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]

    let oneFiltered =
        Map.ofList
            [ "Alpha.Tests", CoveredClasses(Set.ofList [ "SomeTests" ])
              "Beta.Tests", CoveredWholeProject ]

    test <@ scopeOf projects oneFiltered = ScopeFiltered(2, 2) @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: an unfiltered run that SKIPPED a project is a subset`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]
    let oneMissing = Map.ofList [ "Alpha.Tests", CoveredWholeProject ]
    test <@ scopeOf projects oneMissing = ScopeFiltered(1, 2) @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: the zero-affected skip's empty green is NO SCOPE, not a full suite`` () =
    // The trap: `RanFullSuite` is vacuously TRUE for an empty map, and a run whose
    // coverage is empty verified nothing. `ScopeNone` is what the CLI refuses to call
    // green in either mode.
    test <@ scopeOf [ "Alpha.Tests" ] RunCoverage.none = ScopeNone 1 @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: a repo with no test projects is not a covered suite`` () =
    // There is no evidence in a run of nothing.
    test <@ scopeOf [] RunCoverage.none = ScopeNone 0 @>

// A test run the daemon cannot SEE is evidence it cannot judge. The `run-tests` IPC
// command called `executeTests` directly on the IPC thread: no `RunExclusive "tests"`
// slot, no `Running` status, no busy accounting. During such a run the daemon's whole
// model read "at rest", so `fshw check` could exit 0 while the test process was alive and
// any concurrent FileChecked stamped a terminal status over it (the "✓ test-prune,
// started: with no elapsed:" signature).

/// A single-project config whose command touches `started`, waits (bounded) for `release`,
/// then touches `done` — so the test controls the in-flight window deterministically. The
/// script lives in a file so no argument-quoting rules apply.
let private gatedRunConfig (tmpDir: string) =
    let started = Path.Combine(tmpDir, "started")
    let release = Path.Combine(tmpDir, "release")
    let doneFile = Path.Combine(tmpDir, "done")
    let scriptPath = Path.Combine(tmpDir, "gated-run.sh")

    File.WriteAllText(
        scriptPath,
        $"touch {started}\n"
        + $"n=0\n"
        + $"while [ ! -f {release} ] && [ \"$n\" -lt 100 ]; do sleep 0.1; n=$((n+1)); done\n"
        + $"touch {doneFile}\n"
    )

    let config =
        { Project = "GatedProject"
          Command = "sh"
          Args = scriptPath
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = AutoDetect }

    config, started, release, doneFile

[<Fact(Timeout = 30000)>]
let ``run-tests: an in-flight command-driven run is visible to the daemon model`` () =
    withTempDir "tp-cmd-visible" (fun tmpDir ->
        let config, started, release, _doneFile = gatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let cmdTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        try
            waitUntil (fun () -> File.Exists started) 15000
            test <@ File.Exists started @>

            // The test process is running, so the plugin must hold the exclusive "tests"
            // slot and report Running — otherwise a concurrent `fshw check` sees "at rest"
            // and exits 0 mid-execution.
            test <@ host.AnyPluginBusy() @>

            let statusDuringRun = host.GetStatus("test-prune")

            test
                <@
                    match statusDuringRun with
                    | Some(Running _) -> true
                    | _ -> false
                @>
        finally
            File.WriteAllText(release, "")

        cmdTask.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        test <@ cmdTask.IsCompleted @>
        // The results JSON is unchanged by the accounting.
        test <@ cmdTask.Result.IsSome @>
        test <@ cmdTask.Result.Value.Contains("projects") @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked while a test run is in flight must not report a terminal status`` () =
    withTempDir "tp-midrun-stamp" (fun tmpDir ->
        let config, started, release, doneFile = gatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let cmdTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        try
            waitUntil (fun () -> File.Exists started) 15000
            test <@ File.Exists started @>

            // An editor save during a long suite. Whatever its analysis outcome, the run
            // owns the status until TestsFinished delivers the earned verdict — analysis
            // diagnostics still reach the error ledger, so nothing is lost by staying
            // Running.
            let srcFile = Path.Combine(tmpDir, "Lib.fs")
            File.WriteAllText(srcFile, "module Lib\nlet x = 1\n")

            // Subscribe BEFORE the mid-run event: the bug is a TRANSIENT terminal, stamped
            // and immediately overwritten, which a polling sampler misses entirely.
            let terminalDuringRun = beginAwaitNextTerminal host "test-prune"

            host.EmitFileChecked(
                { fakeFileCheckResult srcFile with
                    Source = "module Lib\nlet x = 1\n" }
            )

            // The run is provably still gated (`done` unwritten), so any terminal
            // transition observed inside this window is the manufactured status.
            let stampedMidRun = terminalDuringRun.Wait(TimeSpan.FromSeconds 3.0)
            test <@ not (File.Exists doneFile) @>
            test <@ not stampedMidRun @>
        finally
            File.WriteAllText(release, "")

        cmdTask.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        test <@ cmdTask.IsCompleted @>)

[<Fact(Timeout = 30000)>]
let ``a green run's Completed status carries its verdict`` () =
    // A ✓ with nothing to say is unrepresentable: the status carries what the run did, and
    // the history record holds the SAME summary — one channel, host-routed.
    withTempDir "tp-verdict" (fun tmpDir ->
        let host, _sentinel = withSingleProjectHarness tmpDir "VerdictProject"
        emitBuildAndWaitTerminal host

        match host.GetStatus("test-prune") with
        | Some(PluginStatus.Completed(_, v)) ->
            test <@ v.Summary.Contains "1 passed" @>
            test <@ v.Summary.Contains "0 failed" @>

            let record = List.head (host.GetHistory("test-prune"))
            test <@ record.Summary = Some v.Summary @>
        | other -> Assert.Fail($"expected Completed carrying a verdict, got: %A{other}"))

// `test-rerun` is the repo's "prove it ran" verb: it must never report success without
// running, so a slot held by another run QUEUES the force-run rather than declining it.

/// Like `gatedRunConfig`, but the script appends one line per invocation to a `runs` file,
/// so a test can COUNT executions rather than trusting a status.
let private countingGatedRunConfig (tmpDir: string) =
    let started = Path.Combine(tmpDir, "started")
    let release = Path.Combine(tmpDir, "release")
    let runs = Path.Combine(tmpDir, "runs")
    let scriptPath = Path.Combine(tmpDir, "counting-gated-run.sh")

    File.WriteAllText(
        scriptPath,
        $"echo run >> {runs}\n"
        + $"touch {started}\n"
        + $"n=0\n"
        + $"while [ ! -f {release} ] && [ \"$n\" -lt 100 ]; do sleep 0.1; n=$((n+1)); done\n"
    )

    let config =
        { Project = "GatedProject"
          Command = "sh"
          Args = scriptPath
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = AutoDetect }

    config, started, release, runs

let private runCount (runs: string) =
    if File.Exists runs then
        File.ReadAllLines(runs)
        |> Array.filter (fun l -> l.Trim() <> "")
        |> Array.length
    else
        0

[<Fact(Timeout = 60000)>]
let ``run-tests refused the slot is QUEUED and still runs — never a green it did not earn`` () =
    withTempDir "tp-rerun-queued" (fun tmpDir ->
        let config, started, release, runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        // Run #1 claims the slot and blocks on the gate.
        let first = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        waitUntil (fun () -> File.Exists started) 20000
        test <@ runCount runs = 1 @>

        // Run #2 arrives while the slot is HELD. Replying `busy` here means exit 0 having
        // executed nothing.
        let second = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        // Still queued, and still owed a reply.
        Thread.Sleep 500
        test <@ runCount runs = 1 @>
        test <@ not second.IsCompleted @>

        File.WriteAllText(release, "")

        first.Wait(TimeSpan.FromSeconds 30.0) |> ignore
        second.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        test <@ first.IsCompleted @>
        test <@ second.IsCompleted @>

        // The suite executed TWICE, and the second reply is a real results payload rather
        // than a "busy" non-verdict.
        waitUntil (fun () -> runCount runs = 2) 20000
        test <@ runCount runs = 2 @>

        test <@ second.Result.IsSome @>
        let json = second.Result.Value
        test <@ json.Contains("projects") @>
        test <@ not (json.Contains("\"busy\"")) @>)

[<Fact(Timeout = 60000)>]
let ``a queued run-tests reply resolves — a refused claim can never strand the IPC caller`` () =
    // The hang the RunClaim DU makes impossible: the reply TCS lives inside the work
    // async, so a silently-dropped claim resolved nothing and the command's
    // `Async.AwaitTask reply.Task` waited forever.
    withTempDir "tp-rerun-noStrand" (fun tmpDir ->
        let config, started, release, _runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let first = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        waitUntil (fun () -> File.Exists started) 20000

        // Three force-runs pile up behind the in-flight one; none may be stranded.
        let queued =
            [ for _ in 1..3 -> host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask ]

        File.WriteAllText(release, "")

        first.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        for t in queued do
            t.Wait(TimeSpan.FromSeconds 30.0) |> ignore
            test <@ t.IsCompleted @>
            test <@ t.Result.IsSome @>)

[<Fact(Timeout = 30000)>]
let ``run-tests bounds its wait: a run that outlives the budget reports busy, never a verdict`` () =
    // The last unbounded seam is the reply wait. A 1-second budget against a gated,
    // never-releasing run must return the DISTINCT `busy` status, which the CLI maps to a
    // non-zero exit so it can never read as a pass the run did not produce.
    withTempDir "tp-rerun-bounded" (fun tmpDir ->
        let config, started, release, _runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        try
            let json =
                host.RunCommand("run-tests", [| """{"waitSec":1}""" |])
                |> Async.RunSynchronously

            test <@ json.IsSome @>
            test <@ json.Value.Contains("\"busy\"") @>
            test <@ not (json.Value.Contains("\"projects\"")) @>
            test <@ File.Exists started @>
        finally
            // Let the daemon-side run finish so the temp dir can be cleaned.
            File.WriteAllText(release, "")
            waitUntil (fun () -> not (host.AnyPluginBusy())) 30000)
