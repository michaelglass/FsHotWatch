/// `tests.selection`: the walk policy the plugin's impact queries follow.
///
/// TestPrune stores every edge and lets the reader choose which ones the affected-test walk
/// follows (`TestPrune.Domain.WalkPolicy`). A policy that names `sql` and `sql-hydra` as
/// test-scoped stops a changed writer from selecting a test only because it calls a reader
/// of the same table. These pin that the configured policy reaches the index the plugin
/// selects from, and that it changes what runs.
module FsHotWatch.Tests.TestPruneWalkPolicyTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Domain

let private gated = TestPrune.Sql.SqlCoupling.reachabilityGated

[<Theory(Timeout = 30000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``the plugin opens its index under the policy it is given`` (scoped: bool) =
    withTempDir "tp-walk-policy-open" (fun tmpDir ->
        let policy = if scoped then gated else WalkPolicy.FollowAll
        let mutable opened = None

        let queriesOf (db: Database) =
            opened <- Some db.WalkPolicy
            ImpactQueries.ofDatabase db

        createWithQueries
            queriesOf
            (TimeSpan.FromMinutes 1.0)
            None
            (fun () -> Map.empty)
            (Path.Combine(tmpDir, "tp.db"))
            policy
            tmpDir
            None
            None
            None
            None
            None
            []
            None
            FsHotWatch.TestPrune.TestMode.initial
        |> ignore

        test <@ opened = Some policy @>)

// ---------------------------------------------------------------------------
// Selection: a writer, a reader of its table, and one test calling each
// ---------------------------------------------------------------------------
//
//   WriterTests.runsWriter → Repo.recordEdit (writer) ←~sql~ Repo.getById (reader) ← ReaderTests.readerOnly
//
// Following every edge, a change to the writer selects both tests. Under the gated policy
// it selects only the test that runs the writer.

[<Literal>]
let private Writer = "Repo.recordEdit"

[<Literal>]
let private Reader = "Repo.getById"

let private symbol (name: string) (file: string) : SymbolInfo =
    { FullName = name
      Kind = Function
      SourceFile = file
      LineStart = 1
      LineEnd = 1
      ContentHash = name
      IsExtern = false }

let private seedIndex (dbPath: string) =
    Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
    let db = Database.create dbPath

    let analysis =
        AnalysisResult.Create(
            [ symbol Writer "Repo.fs"
              symbol Reader "Repo.fs"
              symbol "WriterTests.runsWriter" "WriterTests.fs"
              symbol "ReaderTests.readerOnly" "ReaderTests.fs" ],
            [ { FromSymbol = Reader
                ToSymbol = Writer
                Kind = SharedState
                Source = TestPrune.Sql.SqlCoupling.Source }
              { FromSymbol = "WriterTests.runsWriter"
                ToSymbol = Writer
                Kind = Calls
                Source = "core" }
              { FromSymbol = "ReaderTests.readerOnly"
                ToSymbol = Reader
                Kind = Calls
                Source = "core" } ],
            [ { SymbolFullName = "WriterTests.runsWriter"
                TestProject = "WriterProject"
                TestClass = "WriterTests"
                TestMethod = "runsWriter" }
              { SymbolFullName = "ReaderTests.readerOnly"
                TestProject = "ReaderProject"
                TestClass = "ReaderTests"
                TestMethod = "readerOnly" } ]
        )

    db.RebuildProjects [ analysis ]
    clearSqlitePoolForDb db

let private runsOf (tmpDir: string) (project: string) =
    let path = Path.Combine(tmpDir, $"%s{project}-runs")

    if File.Exists path then
        File.ReadAllLines path |> Array.length
    else
        0

/// The projects a build launches when `Writer` is owed, configured through `.fshw.json`
/// with `selection` as the `tests.selection` value (`None`: the key absent).
let private projectsRunFor (selection: string option) =
    withTempDir "tp-walk-policy-select" (fun tmpDir ->
        seedIndex (FsHotWatch.Cli.DaemonConfig.testImpactDbPath tmpDir)
        seedBaseline tmpDir [ "WriterProject"; "ReaderProject" ]
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.singleton Writer)

        let runner (project: string) =
            let receipt = Path.Combine(tmpDir, $"%s{project}-runs")

            $"""{{"project":"%s{project}","command":"sh","args":"-c \"echo run >> %s{receipt}\"","coverage":false,"timeoutSec":10}}"""

        let selectionKey =
            selection
            |> Option.map (fun value -> $"\"selection\":%s{value},")
            |> Option.defaultValue ""

        let json =
            $"""{{"tests":{{%s{selectionKey}"projects":[%s{runner "WriterProject"},%s{runner "ReaderProject"}]}}}}"""

        let config =
            FsHotWatch.Cli.DaemonConfig.parseConfig
                json
                (FsHotWatch.Cli.DaemonConfig.stripConfig (defaultTestConfig ()))

        let config =
            { config with
                Format = FsHotWatch.Cli.DaemonConfig.FormatMode.Off }

        let daemon =
            FsHotWatch.Daemon.Daemon.createWith (Unchecked.defaultof<_>) tmpDir oneShotDaemonOptions

        FsHotWatch.Cli.DaemonConfig.registerPlugins daemon tmpDir config

        try
            let terminal = beginAwaitNextTerminal daemon.Host "test-prune"
            daemon.Host.EmitBuildCompleted(BuildSucceeded)
            test <@ terminal.Wait(TimeSpan.FromSeconds 20.0) @>
            waitForQuiescent daemon.Host 15000

            [ "WriterProject"; "ReaderProject" ]
            |> List.filter (fun project -> runsOf tmpDir project > 0)
        finally
            daemon.Host.Teardown())

[<Fact(Timeout = 60000)>]
let ``following every edge, a changed writer runs the test that only calls a reader of its table`` () =
    test <@ projectsRunFor None = [ "WriterProject"; "ReaderProject" ] @>

[<Fact(Timeout = 60000)>]
let ``with sql edges test-scoped, a changed writer runs only the test that reaches it`` () =
    test <@ projectsRunFor (Some """{"testScopedSources":["sql","sql-hydra"]}""") = [ "WriterProject" ] @>
