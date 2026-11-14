/// Declared exclusions and verification debt: a symbol's debt waits on every covering
/// test project except an unconfigured one that `tests.excluded` declares with a reason.
/// Excluding a project never proves its tests passed; it only removes that project from
/// this gate's claim.
module FsHotWatch.Tests.TestPruneDebtScopeTests

open System
open System.IO
open Xunit
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.Database

/// Register a handler under `resolve`, land one build, and wait for it to settle.
let private settleBuild (tmpDir: string) (dbPath: string) (configs: TestConfig list) resolve =
    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    host.RegisterHandler(createWithScope resolve dbPath tmpDir (Some configs) None None None None [])
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    Assert.True(terminal.Wait(HangWait), "test-prune never reported a terminal status")
    waitForQuiescent host 20000
    host

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(true, "separate integration gate", false)>]
[<InlineData(false, "separate integration gate", false)>]
[<InlineData(true, null, true)>]
[<InlineData(true, "", true)>]
[<InlineData(true, "   ", true)>]
let ``declared exclusions retire only governed covering debt`` (mixed: bool) (reason: string) (remainsOwed: bool) =
    withTempDir "tp-declared-scope" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P2" "P2Tests" "sharedTest"

        if mixed then
            PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P1" "P1Tests" "sharedTest"

        let coverers =
            db.QueryAffectedTests [ "Lib.shared" ]
            |> List.map (fun t -> t.TestProject)
            |> Set.ofList

        Assert.Contains("P2", coverers)

        if mixed then
            Assert.Contains("P1", coverers)

        PendingVerification.save tmpDir (Set.singleton "Lib.shared")

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

        let run exclusions =
            let host = settleBuild tmpDir dbPath configs (fun () -> exclusions)

            try
                PendingQueueHelpers.loadQueue tmpDir
            finally
                host.Teardown()

        let exclusions =
            if isNull reason then
                Map.empty
            else
                Map.ofList [ "P2", reason ]

        Assert.Equal(remainsOwed, run exclusions |> Set.contains "Lib.shared")

        if not remainsOwed then
            // A later edit is new debt, and removing the declaration restores the
            // requirement despite the earlier successful configured-suite run.
            PendingVerification.save tmpDir (Set.singleton "Lib.shared")
            Assert.Contains("Lib.shared", run Map.empty))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a configured project stays required even when an exclusion names it`` () =
    withTempDir "tp-configured-excluded" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P1" "P1Tests" "sharedTest"
        PendingVerification.save tmpDir (Set.singleton "Lib.shared")
        let failing = Path.Combine(tmpDir, "p1-fails")
        File.WriteAllText(failing, "")
        let configs = [ PendingQueueHelpers.flagConfig tmpDir "P1" failing ]

        let host =
            settleBuild tmpDir dbPath configs (fun () -> Map.ofList [ "P1", "a stale declaration" ])

        try
            // P1 runs here and failed, so its declaration cannot write the debt off.
            Assert.Contains("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
        finally
            host.Teardown())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``scope resolution failure at completion settles owned work and retains debt`` () =
    withTempDir "tp-scope-failure" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P1" "P1Tests" "sharedTest"
        PendingVerification.save tmpDir (Set.singleton "Lib.shared")
        let completedFlag = Path.Combine(tmpDir, "runner-completed")

        let config =
            { PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) with
                Args = $"-c \"touch {completedFlag}; exit 0\"" }

        let failure =
            InvalidOperationException("ambiguous exclusion project after runner completion")

        // The covering project IS configured, so nothing before completion needs the
        // declarations. Completion resolves them regardless, before it retires anything.
        let resolve () =
            if File.Exists completedFlag then
                raise failure

            Map.empty

        let host = settleBuild tmpDir dbPath [ config ] resolve

        try
            Assert.True(File.Exists completedFlag, "the runner must have completed before resolution failed")
            Assert.False(host.AnyPluginBusy())

            match host.GetStatus("test-prune") with
            | Some(PluginStatus.Failed(message, _, _)) -> Assert.Contains(failure.Message, message)
            | other -> Assert.Fail($"a completion that cannot resolve its scope must settle red: %A{other}")

            Assert.Contains("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
        finally
            host.Teardown())

[<Fact(Timeout = 60000)>]
let ``a declared exclusion writes debt off on the record, naming the project`` () =
    withTempDir "tp-declared-report" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.orphan" "Orphan.fs" "Excluded" "ExcludedTests" "orphanTest"
        seedBaseline tmpDir [ "P1" ]
        PendingVerification.save tmpDir (Set.ofList [ "Lib.orphan" ])
        let ran = Path.Combine(tmpDir, "P1-ran")

        let configs =
            [ { PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) with
                  Args = $"-c \"touch {ran}; exit 0\"" } ]

        let host =
            settleBuild tmpDir dbPath configs (fun () -> Map.ofList [ "Excluded", "separate harness gate" ])

        try
            // Nothing governed covers the symbol, so nothing ran and it left the queue...
            Assert.False(File.Exists ran)
            Assert.DoesNotContain("Lib.orphan", PendingQueueHelpers.loadQueue tmpDir)

            // ...and the write-off is on the verdict, naming the declared project.
            let report =
                match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
                | Some reply -> FsHotWatch.Cli.IpcParsing.parseTestRunReport reply
                | None -> failwith "no test-scope command"

            match report.Scope with
            | FsHotWatch.Cli.IpcParsing.NoTestsRun(FsHotWatch.Cli.IpcParsing.NoTestsReason.ChangesUncovered(symbols,
                                                                                                            _,
                                                                                                            unrunnable)) ->
                Assert.Equal<string list>([ "Lib.orphan" ], symbols)
                Assert.Equal<string list>([ "Excluded" ], unrunnable.Projects)
            | other -> Assert.Fail($"expected a changes-uncovered scope naming the excluded project, got %A{other}")
        finally
            host.Teardown())

// --- A covering project that is not in the current tree (AUTOMATION-1717) ---
//
// A checkout ran a branch with test project Sim, so the index holds Sim's tests and the
// symbols they cover. It then checked out a tree without Sim. Every configured test passed,
// yet the run went red: "7 symbol(s) still owed to tests in FsHotWatch.Simulation.Tests,
// which this daemon does not run". An obligation names only test projects the current tree
// has (configured, or in the model's discovered inventory). One the tree still has, even
// unconfigured, still owes.

/// A model host whose discovered inventory is `inTree`, settled after one build.
let private settleBuildOver (tmpDir: string) (dbPath: string) (configs: TestConfig list) (inTree: string list) =
    let host = createModelHost Unchecked.defaultof<_> tmpDir

    host.SetProjectGraph
        { FsHotWatch.PluginFramework.ProjectGraphAccessor.none with
            GetAllProjects =
                fun () ->
                    inTree
                    |> List.map (fun name -> Path.Combine(tmpDir, "tests", name, name + ".fsproj")) }

    host.RegisterHandler(createWithScope (fun () -> Map.empty) dbPath tmpDir (Some configs) None None None None [])
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    Assert.True(terminal.Wait(HangWait), "test-prune never reported a terminal status")
    waitForQuiescent host 20000
    host

/// `Lib.shared` is covered by configured P1 and by Sim, and is owed.
let private owedToP1AndSim (tmpDir: string) (dbPath: string) =
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P1" "P1Tests" "sharedTest"
    PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "Sim" "SimTests" "sharedTest"
    seedBaseline tmpDir [ "P1" ]
    PendingVerification.save tmpDir (Set.singleton "Lib.shared")
    [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

let private failedMessage (host: PluginHost) =
    match host.GetStatus("test-prune") with
    | Some(PluginStatus.Failed(message, _, _)) -> Some message
    | _ -> None

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a covering project absent from the current tree owes nothing, and a green run is green`` () =
    withTempDir "tp-absent-coverer" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let configs = owedToP1AndSim tmpDir dbPath
        let host = settleBuildOver tmpDir dbPath configs [ "P1" ]

        try
            Assert.DoesNotContain("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
            Assert.Equal(None, failedMessage host)
        finally
            host.Teardown())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a covering project the current tree has but does not run still owes`` () =
    // Control: Sim is in the tree, unconfigured and undeclared, so nothing here can run it.
    withTempDir "tp-present-coverer" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let configs = owedToP1AndSim tmpDir dbPath
        let host = settleBuildOver tmpDir dbPath configs [ "P1"; "Sim" ]

        try
            Assert.Contains("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
            let message = failedMessage host
            Assert.True(message.IsSome, "a symbol owed to a project in the tree must keep the run red")
            Assert.Contains("still owed to tests in Sim", message.Value)
        finally
            host.Teardown())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``with no model to say what the tree holds, an unrunnable coverer still owes`` () =
    // No inventory is no evidence of absence: the obligation stands.
    withTempDir "tp-no-model-coverer" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let configs = owedToP1AndSim tmpDir dbPath
        let host = settleBuild tmpDir dbPath configs (fun () -> Map.empty)

        try
            Assert.Contains("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
        finally
            host.Teardown())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a covering project the inventory does not name but whose test sources are here still owes`` () =
    // Control: a script's tests, or a project outside the discovery roots, are named by no
    // `.fsproj` in the inventory. Their sources in the tree are evidence enough that they exist.
    withTempDir "tp-unnamed-coverer" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let configs = owedToP1AndSim tmpDir dbPath
        File.WriteAllText(Path.Combine(tmpDir, "SimTests.fs"), "module SimTests\n")
        let host = settleBuildOver tmpDir dbPath configs [ "P1" ]

        try
            Assert.Contains("Lib.shared", PendingQueueHelpers.loadQueue tmpDir)
            Assert.True((failedMessage host).IsSome, "a coverer whose tests are in the tree keeps the run red")
        finally
            host.Teardown())

// The absence decision is on the RUN'S summary, not only in the daemon log: a project wrongly
// judged absent then shows even on a green run, in `fshw status`, `check` and the agent JSON.

let private completedSummary (host: PluginHost) =
    match host.GetStatus("test-prune") with
    | Some(PluginStatus.Completed(_, verdict)) -> verdict.Summary
    | other -> failwith $"expected a completed test-prune run, got %A{other}"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a run with an absent coverer names it in the run's summary`` () =
    withTempDir "tp-absent-summary" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let configs = owedToP1AndSim tmpDir dbPath
        let host = settleBuildOver tmpDir dbPath configs [ "P1" ]

        try
            Assert.Contains("1 symbol(s) not owed: project Sim is no longer in this tree", completedSummary host)
        finally
            host.Teardown())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a run with no absent coverer carries no absence note`` () =
    withTempDir "tp-no-absent-summary" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.shared" "Lib.fs" "P1" "P1Tests" "sharedTest"
        seedBaseline tmpDir [ "P1" ]
        PendingVerification.save tmpDir (Set.singleton "Lib.shared")

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

        let host = settleBuildOver tmpDir dbPath configs [ "P1" ]

        try
            let summary = completedSummary host
            Assert.DoesNotContain("not owed", summary)
            Assert.DoesNotContain("no longer in this tree", summary)
        finally
            host.Teardown())

// --- `ImpactQueries.TestSourceFiles`, the third signal of absence, against a real index ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``TestSourceFiles names each requested project's test sources, and omits a project with none`` () =
    withTempDir "tp-test-sources" (fun tmpDir ->
        let db = Database.create (Path.Combine(tmpDir, "tp.db"))
        PendingQueueHelpers.seedCoveredSymbol db "Lib.a" "Lib.fs" "P1" "P1Tests" "aTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.b" "Lib.fs" "P2" "P2Tests" "bTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.c" "Other.fs" "P2" "P2More" "cTest"
        let sources = (PluginQueries.ImpactQueries.ofDatabase db).TestSourceFiles

        Assert.Equal(
            Some(
                Map.ofList
                    [ "P1", Set.ofList [ "P1Tests.fs" ]
                      "P2", Set.ofList [ "P2Tests.fs"; "P2More.fs" ] ]
            ),
            sources [ "P1"; "P2"; "Unindexed" ]
        )

        Assert.Equal(Some(Map.ofList [ "P2", Set.ofList [ "P2Tests.fs"; "P2More.fs" ] ]), sources [ "P2" ])
        Assert.Equal(Some Map.empty, sources [ "Unindexed" ])
        Assert.Equal(Some Map.empty, sources []))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``TestSourceFiles over an index it cannot read answers None, so nothing is judged absent`` () =
    // An in-memory index is private to the connection that made it: a new connection finds no
    // `test_methods` table. Unknown is not absent.
    let db = Database.create ":memory:"
    Assert.Equal(None, (PluginQueries.ImpactQueries.ofDatabase db).TestSourceFiles [ "P1" ])

// --- `RunOutcomeReport.absentCoverersNote`, the absence note every terminal summary appends ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the absence note is empty when no coverer was judged absent`` () =
    Assert.Equal("", RunOutcomeReport.absentCoverersNote Map.empty)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the absence note names one absent project in the singular`` () =
    Assert.Equal(
        " (2 symbol(s) not owed: project Sim is no longer in this tree)",
        RunOutcomeReport.absentCoverersNote (Map.ofList [ "Sim", Set.ofList [ "Lib.a"; "Lib.b" ] ])
    )

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the absence note names several absent projects and counts each symbol once`` () =
    // Lib.b was owed to both: it is one symbol not owed, not two.
    Assert.Equal(
        " (3 symbol(s) not owed: projects Old, Sim are no longer in this tree)",
        RunOutcomeReport.absentCoverersNote (
            Map.ofList
                [ "Sim", Set.ofList [ "Lib.a"; "Lib.b" ]
                  "Old", Set.ofList [ "Lib.b"; "Lib.c" ] ]
        )
    )
