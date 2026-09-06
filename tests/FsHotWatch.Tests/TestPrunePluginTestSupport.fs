/// Harness helpers shared by the TestPrune plugin test files.
///
/// These were private to `TestPrunePluginTests` until that file outgrew TestPrune's
/// symbol-traversal budget (32,768 nodes) and had to be split. They live here, first in
/// compile order, because F# is order-dependent and every one of them is used by more
/// than one of the parts.
module FsHotWatch.Tests.TestPrunePluginTestSupport

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

/// An empty launch: commits nothing and covers nothing, so it clears no outstanding red.
/// For tests that build `TestsFinished` directly. Runs that must CLEAR something use
/// `fullSuiteLaunch` / `filteredLaunch` below.
let emptyLaunch: TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection = Map.empty
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

/// A launch that ran every named project UNFILTERED — the scope a full suite (or a
/// plain `test-rerun`) has, and the only one whose green may clear an arbitrary red.
let fullSuiteLaunch (projects: string list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection = projects |> List.map (fun p -> p, ProjectInFull) |> Map.ofList
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

/// A launch that ran only `classes` in each named project — an impact-filtered
/// selection. Projects NOT named were skipped entirely.
let filteredLaunch (selection: (string * string list) list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection =
        selection
        |> List.map (fun (p, classes) -> p, ProjectClasses(Set.ofList classes))
        |> Map.ofList
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

let waitForPluginIdle (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForSettled host pluginName (int (timeoutSecs * 1000.0))

let waitForPluginTerminal (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForTerminalStatus host pluginName (int (timeoutSecs * 1000.0))

/// Emit a FileChecked and wait for the mailbox to drain. FileChecked persists symbol
/// analysis as an in-handler side-effect with no status transition, so
/// `beginAwaitNextTerminal` would hang the full timeout — quiescence is the right sync.
let emitFileAndQuiesce (host: PluginHost) (result: FileCheckResult) =
    host.EmitFileChecked result
    waitForQuiescent host 10000

/// Emit the BatchChecked cohort-complete signal over `files` and wait for the mailbox to
/// drain. This is what flushes accumulated PendingAnalysis to the symbol DB; like
/// FileChecked it is an in-handler side-effect with no status transition.
let emitBatchAndQuiesce (host: PluginHost) (files: string list) =
    host.EmitBatchChecked(fakeBatchChecked files)
    waitForQuiescent host 10000

/// Emit a successful BuildCompleted and wait for a terminal status. This handler spawns
/// the test run via `Async.Start`, so the work outlives it and quiescence could return
/// early — a terminal await is the right sync.
///
/// Tests that index files emit this FIRST: the sidecar's `markClean` only fires for
/// FileChecked events arriving after a BuildCompleted has been observed in the session,
/// mirroring fshw's cold scan where BuildPlugin's terminal status gates the FCS tiers.
let emitBuildAndWaitTerminal (host: PluginHost) =
    let generationBefore =
        host.WorkCycleGenerations()
        |> Map.tryFind "test-prune"
        |> Option.defaultValue 0L

    host.EmitBuildCompleted(BuildSucceeded)

    let completedNewCycle =
        waitUntilTrue
            (fun () ->
                let generationAfter =
                    host.WorkCycleGenerations()
                    |> Map.tryFind "test-prune"
                    |> Option.defaultValue 0L

                let terminal =
                    match host.GetStatus("test-prune") with
                    | Some(Completed _)
                    | Some(Failed _) -> true
                    | _ -> false

                generationAfter > generationBefore && terminal && not (host.AnyPluginBusy()))
            20000

    test <@ completedNewCycle @>

/// Stand up a test-prune plugin around a single one-project test config whose
/// command is `sh -c "touch <sentinel>"`. Returns `(host, sentinel)`.
let withSingleProjectHarness (tmpDir: string) (projectName: string) =
    let sentinel = Path.Combine(tmpDir, "ran")

    let configs =
        [ { Project = projectName
            Command = "sh"
            Args = $"-c \"touch {sentinel}\""
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = None
            ReportVerificationFormat = AutoDetect } ]

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create ":memory:" tmpDir (Some configs) None None None None []
    host.RegisterHandler(handler)
    host, sentinel

/// The run log these tests pretend was written. Most are about the per-test MATCHER and
/// don't care which arm this is; the ones that ARE about the log pass their own.
let savedLog =
    FsHotWatch.RunLog.Ref.Written "/repo/.fshw/test-runs/deadbeef/FsHotWatch.Tests.output.log"

let writeAt (path: string) (contents: string) (mtime: DateTime) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    File.SetLastWriteTimeUtc(path, mtime)

let p (parts: string list) = Path.Combine(List.toArray parts)

/// A repo root no other test shares. AUTOMATION-110 made two more TestPrune ledgers
/// durable (`outstanding-failures.json`, `full-suite-baseline.json`) beside the pending
/// queue that already was, and a plugin created over a SHARED root (`"/tmp"`) loads
/// whatever the previous test left there — a red from one test quarantined into the
/// next test's selection. The queue had the same leak and got away with it only because
/// most tests end with it empty; reds are the opposite. Not deleted afterwards: these
/// are a few small JSON files, and a cleanup that raced the plugin's own writes would
/// be a second source of flakiness.
let isolatedRoot () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), $"fshw-tp-{Guid.NewGuid():N}")
        |> Path.GetFullPath

    Directory.CreateDirectory(dir) |> ignore
    dir

/// AUTOMATION-110. Record a full-suite baseline over `projects` for a repo root, so a
/// test about some OTHER rule (the zero-affected skip, cache replay, an empty ledger)
/// is not widened to the full suite by the one rule this fixture satisfies: a
/// repository with no baseline must earn one before a filtered run can be green.
let seedBaseline (root: string) (projects: string list) : unit =
    FsHotWatch.TestPrune.FullSuiteBaseline.save
        root
        { RunId = Guid.NewGuid()
          EarnedAt = DateTime.UtcNow
          Projects = Set.ofList projects }

module PendingQueueHelpers =
    open FsHotWatch.TestPrune

    /// Seed the symbol DB so `QueryAffectedTests [symbolFullName]` returns a test in
    /// `testProject`, mirroring the dependency-edge + TestMethodInfo shape the analyzer
    /// produces.
    let seedCoveredSymbol
        (db: Database)
        (symbolFullName: string)
        (sourceFile: string)
        (testProject: string)
        (testClass: string)
        (testMethod: string)
        =
        let symbol: SymbolInfo =
            { FullName = symbolFullName
              Kind = SymbolKind.Value
              SourceFile = sourceFile
              LineStart = 1
              LineEnd = 1
              ContentHash = "seed-hash"
              IsExtern = false }

        // The test method is ALSO a symbol: `dependencies.from_symbol_id` and
        // `test_methods.symbol_id` are NOT-NULL FKs into symbols(id), so without a row for
        // the test's own full name the edge and test-method are silently dropped and
        // QueryAffectedTests returns nothing.
        let testSymbol: SymbolInfo =
            { FullName = $"{testClass}.{testMethod}"
              Kind = SymbolKind.Value
              SourceFile = $"{testClass}.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "seed-test-hash"
              IsExtern = false }

        let tm: TestMethodInfo =
            { SymbolFullName = $"{testClass}.{testMethod}"
              TestProject = testProject
              TestClass = testClass
              TestMethod = testMethod }

        let analysis =
            AnalysisResult.Create(
                [ symbol; testSymbol ],
                [ { FromSymbol = $"{testClass}.{testMethod}"
                    ToSymbol = symbolFullName
                    Kind = DependencyKind.Calls
                    Source = "core" } ],
                [ tm ]
            )

        db.RebuildProjects([ analysis ])
        // The plugin opens its OWN Database.create(dbPath) connection, which a pooled
        // stale snapshot can hide these writes from. Clear the pool so its first read sees
        // the seed.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

    /// A test config whose runner exits 1 iff `flag` exists, and 0 otherwise.
    let flagConfig (tmpDir: string) (project: string) (flag: string) : TestConfig =
        { Project = project
          Command = "sh"
          Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    /// The durable pending-verification queue for a repo root. An UNREADABLE ledger is a
    /// test failure, not an empty queue: these tests assert on what is owed, and reading
    /// an unreadable ledger as `empty` is the bug AUTOMATION-150 closes. The tests that
    /// WANT the unreadable case match on `LoadedQueue` themselves.
    let loadQueue (tmpDir: string) : Set<string> =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Loaded queue -> queue
        | PendingVerification.LoadedQueue.Unreadable reason ->
            failwith $"expected a readable pending-verification ledger, got Unreadable: {reason}"

let testConfigNamed (project: string) : TestConfig =
    { Project = project
      Command = "dotnet"
      Args = "test"
      Group = "default"
      Environment = []
      FilterTemplate = Some "--filter-class {classes}"
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }
