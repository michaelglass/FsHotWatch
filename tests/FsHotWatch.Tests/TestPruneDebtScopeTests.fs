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
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.Database

/// Register a handler under `resolve`, land one build, and wait for it to settle.
let private settleBuild (tmpDir: string) (dbPath: string) (configs: TestConfig list) resolve =
    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    host.RegisterHandler(createWithScope resolve dbPath tmpDir (Some configs) None None None None [])
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    Assert.True(terminal.Wait(TimeSpan.FromSeconds 15.0), "test-prune never reported a terminal status")
    waitForQuiescent host 20000
    host

[<Theory(Timeout = 30000)>]
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

[<Fact(Timeout = 30000)>]
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

[<Fact(Timeout = 30000)>]
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
