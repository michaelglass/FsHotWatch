/// AUTOMATION-110 — SELECTION soundness: a green must prove what it SKIPPED was green too.
///
/// Impact selection is sound only if the unselected set was green, and until this
/// ticket nothing recorded that. Three mechanisms close it, each pinned here beside its
/// negative control:
///
///   1. RED-TEST QUARANTINE (AUTOMATION-67) is DURABLE: a test red in the last run that
///      executed it is re-selected on every run until it passes — in this session AND
///      after a daemon restart. The restart is the AUTOMATION-108 shape reproduced: a
///      queued symbol makes the restarted daemon's first run impact-filtered, so a red
///      the filter does not reach was never selected again.
///   2. FULL-SUITE BASELINE: an impact-filtered green is relative to the last run that
///      executed every configured project. With none — a cold repository, or
///      `tests.projects` grown since — the run widens to the full suite to earn one, and
///      the verdict names it.
///   3. OWED-BUT-UNRUNNABLE: a changed symbol whose only covering tests live in a project
///      `tests.projects` does not list is dropped from the queue (AUTOMATION-99) but
///      REPORTED, naming the project — never silently written off.
///
/// These drive the real BuildCompleted → run → TestsFinished flow with `sh` runners that
/// leave a receipt per launch, so what was SELECTED is read from disk, not inferred.
module FsHotWatch.Tests.TestPruneSelectionSoundnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A runner that appends one line to `runs` per launch and, while `failFlag` exists,
/// fails naming `failedTest` in the runner's own wording (so the red is CLASS-scoped, as
/// in the field). No `FilterTemplate`: a quarantined class then runs the project
/// unfiltered, which is what lets the pass retire the red (`CoveredWholeProject`).
let private runner (tmpDir: string) (project: string) (failedTest: string) : TestConfig =
    let runs = Path.Combine(tmpDir, $"%s{project}-runs")
    let failFlag = Path.Combine(tmpDir, $"%s{project}-fail")

    { Project = project
      Command = "sh"
      Args =
        $"-c \"echo run >> %s{runs}; if [ -f %s{failFlag} ]; then echo 'failed %s{failedTest} (1ms)'; exit 1; fi; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

let private runsOf (tmpDir: string) (project: string) : int =
    let path = Path.Combine(tmpDir, $"%s{project}-runs")

    if File.Exists path then
        File.ReadAllLines path |> Array.length
    else
        0

let private setFailing (tmpDir: string) (project: string) (failing: bool) =
    let flag = Path.Combine(tmpDir, $"%s{project}-fail")

    if failing then
        File.WriteAllText(flag, "")
    elif File.Exists flag then
        File.Delete flag

/// One session: a host with the plugin registered over `tmpDir`/`dbPath`.
let private session (tmpDir: string) (dbPath: string) (configs: TestConfig list) =
    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create dbPath tmpDir (Some configs) None None None None []
    host.RegisterHandler(handler)
    host

/// A build lands; wait for the plugin's terminal status and return it.
let private buildAndSettle (host: PluginHost) : PluginStatus =
    let await = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    test <@ await.Wait(TimeSpan.FromSeconds 20.0) @>
    waitForQuiescent host 10000

    match host.GetStatus("test-prune") with
    | Some status -> status
    | None -> failwith "test-prune reported no status"

let private isFailed (status: PluginStatus) =
    match status with
    | Failed _ -> true
    | _ -> false

let private isCompleted (status: PluginStatus) =
    match status with
    | Completed _ -> true
    | _ -> false

let private outstandingProjects (tmpDir: string) : string list =
    match OutstandingFailure.load tmpDir with
    | OutstandingFailure.LoadedFailures.Loaded failures -> failures |> List.map (fun f -> f.Project) |> List.distinct
    | OutstandingFailure.LoadedFailures.Unreadable reason ->
        failwith $"outstanding-failures ledger unreadable: {reason}"

let private baselineOf (tmpDir: string) : FullSuiteBaseline.Baseline option =
    match FullSuiteBaseline.load tmpDir with
    | FullSuiteBaseline.LoadedBaseline.Loaded b -> b
    | FullSuiteBaseline.LoadedBaseline.Unreadable reason -> failwith $"baseline unreadable: {reason}"

/// Two projects, each covering one symbol, so a queued `Lib.foo` selects P1 and NOTHING
/// in P2 — the "unaffected" half of the AUTOMATION-108 shape.
let private twoProjectDb (tmpDir: string) : string =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
    PendingQueueHelpers.seedCoveredSymbol db "Lib.bar" "Bar.fs" "P2" "P2Tests" "barTest"
    dbPath

let private testScope (host: PluginHost) : FsHotWatch.Cli.IpcParsing.TestRunReport =
    match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
    | Some reply -> FsHotWatch.Cli.IpcParsing.parseTestRunReport reply
    | None -> failwith "no test-scope command"

// ---------------------------------------------------------------------------
// 1. Red-test quarantine — same session, then across a restart
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-108 replay: a red test the next change does not reach is still selected, and the verdict stays red until it passes``
    ()
    =
    withTempDir "a110-quarantine" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        setFailing tmpDir "P2" true
        let host = session tmpDir dbPath configs

        // Run N: cold, nothing queued → the full suite. P2 is red.
        test <@ isFailed (buildAndSettle host) @>
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>
        test <@ outstandingProjects tmpDir = [ "P2" ] @>

        // Run N+1: nothing changed, nothing queued — impact analysis reaches NOTHING.
        // Before AUTOMATION-67 this was the zero-affected green; P2's red was invisible
        // until someone ran the whole suite by hand (17 tests, for weeks). Quarantine
        // re-selects P2 — and ONLY P2: P1 is not re-run, which is what distinguishes
        // quarantine from a full-suite fallback.
        test <@ isFailed (buildAndSettle host) @>
        test <@ runsOf tmpDir "P2" = 2 @>
        test <@ runsOf tmpDir "P1" = 1 @>

        // Run N+2: the red is fixed. Quarantine runs it once more; the pass retires it.
        setFailing tmpDir "P2" false
        test <@ isCompleted (buildAndSettle host) @>
        test <@ runsOf tmpDir "P2" = 3 @>
        test <@ runsOf tmpDir "P1" = 1 @>
        test <@ List.isEmpty (outstandingProjects tmpDir) @>)

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: a red survives a daemon restart and is quarantined into a FILTERED first run`` () =
    // The argument that reds could stay session-scoped was "a restarted daemon runs the
    // full suite". It runs the full suite only when the durable queue is EMPTY; with a
    // symbol queued its first run is impact-filtered, and a red the filter does not
    // reach is gone. This pins both halves: with the ledger the red is re-selected;
    // with the ledger DELETED (the pre-fix state) the same restart goes green over it.
    withTempDir "a110-restart" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        setFailing tmpDir "P2" true

        // Session 1: the full suite finds P2 red.
        let first = session tmpDir dbPath configs
        test <@ isFailed (buildAndSettle first) @>
        test <@ outstandingProjects tmpDir = [ "P2" ] @>
        test <@ (baselineOf tmpDir).IsSome @>

        // Between sessions a change to `Lib.foo` (P1 only) is queued — the ordinary
        // reason a restarted daemon does NOT run the full suite.
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // Session 2: the first run is filtered to P1 by the queue, and P2 rides along
        // by quarantine. Still red.
        let second = session tmpDir dbPath configs
        test <@ isFailed (buildAndSettle second) @>
        test <@ runsOf tmpDir "P1" = 2 @>
        test <@ runsOf tmpDir "P2" = 2 @>

        // NEGATIVE CONTROL — the hole, reproduced on demand. Delete the durable reds (the
        // pre-AUTOMATION-110 state on restart) and queue the same change: the filtered
        // first run selects P1 alone, P2 is never executed, and the session ends GREEN
        // with a red test outstanding. This is what session-scoped quarantine cost.
        File.Delete(OutstandingFailure.sidecarPath tmpDir)
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        let third = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle third) @>
        test <@ runsOf tmpDir "P1" = 3 @>
        test <@ runsOf tmpDir "P2" = 2 @>)

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: an UNREADABLE outstanding-failures ledger widens to the full suite rather than forgetting the reds``
    ()
    =
    withTempDir "a110-reds-unreadable" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir
        seedBaseline tmpDir [ "P1"; "P2" ]

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        // A torn write: the file exists and is not a ledger.
        let path = OutstandingFailure.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "[{\"proj")

        // A queued P1-only change would ordinarily select P1 alone.
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        // The recovering full suite passed everything, so the ledger is rewritten clean.
        test <@ List.isEmpty (outstandingProjects tmpDir) @>)

// ---------------------------------------------------------------------------
// 2. The full-suite baseline
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: with no full-suite baseline a filtered selection widens to the full suite and earns one`` () =
    withTempDir "a110-no-baseline" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        test <@ (baselineOf tmpDir).IsNone @>

        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>

        // P2 ran although nothing queued reaches it: the run was widened to earn the
        // baseline, and the baseline now covers both projects.
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        match baselineOf tmpDir with
        | Some b -> test <@ b.Projects = Set.ofList [ "P1"; "P2" ] @>
        | None -> failwith "a full-suite run that accounted for every project must record the baseline")

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: with a valid baseline the same queued change runs the filtered selection only`` () =
    // The positive control for the widening above: the ONLY difference is the baseline.
    withTempDir "a110-baseline-valid" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir
        seedBaseline tmpDir [ "P1"; "P2" ]

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 0 @>)

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: a baseline that never executed a newly configured project is STALE and widens`` () =
    withTempDir "a110-baseline-stale" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir
        // Earned when only P1 was configured.
        seedBaseline tmpDir [ "P1" ]

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        match baselineOf tmpDir with
        | Some b -> test <@ b.Projects = Set.ofList [ "P1"; "P2" ] @>
        | None -> failwith "the widened run must re-earn the baseline over both projects")

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: a red full suite still records the baseline, because its reds are carried`` () =
    // Not only a GREEN full suite: a red one proves what every other test did, and the
    // red is quarantined. Without this the inner loop would run the whole suite until the
    // last red was fixed instead of the impact set plus the red.
    withTempDir "a110-red-baseline" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        setFailing tmpDir "P2" true

        let host = session tmpDir dbPath configs
        test <@ isFailed (buildAndSettle host) @>
        test <@ (baselineOf tmpDir).IsSome @>
        test <@ outstandingProjects tmpDir = [ "P2" ] @>)

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: test-scope says why there is no baseline, then names the run that earned it`` () =
    withTempDir "a110-scope-baseline" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        let host = session tmpDir dbPath configs

        match (testScope host).Baseline with
        | FsHotWatch.Cli.IpcParsing.BaselineReading.Absent reason -> test <@ reason.Contains "no full-suite run" @>
        | other -> failwithf "before any run the daemon must say the baseline is absent, got %A" other

        test <@ isCompleted (buildAndSettle host) @>
        let report = testScope host

        match report.Baseline with
        | FsHotWatch.Cli.IpcParsing.BaselineReading.Valid b ->
            test <@ Some b.RunId = report.RunId @>
            test <@ b.Projects = 2 @>
        | other -> failwithf "after a full run the daemon must name the baseline, got %A" other)

// ---------------------------------------------------------------------------
// 3. Owed-but-unrunnable (AUTOMATION-108's candidate cause (c))
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: a symbol covered only by an unlisted test project is REPORTED as owed-but-unrunnable, not silently dropped``
    ()
    =
    withTempDir "a110-unrunnable" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        // Covered — but only by a project `tests.projects` does not list.
        PendingQueueHelpers.seedCoveredSymbol db "Lib.orphan" "Orphan.fs" "Unlisted" "UnlistedTests" "orphanTest"
        seedBaseline tmpDir [ "P1" ]
        let configs = [ runner tmpDir "P1" "P1Tests.fooTest" ]
        PendingVerification.save tmpDir (Set.ofList [ "Lib.orphan" ])

        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>

        // Nothing runnable was selected, so nothing ran (AUTOMATION-99's drop stands)...
        test <@ runsOf tmpDir "P1" = 0 @>
        test <@ not ((PendingQueueHelpers.loadQueue tmpDir).Contains "Lib.orphan") @>

        // ...and the write-off is on the record, naming the project.
        let report = testScope host

        match report.Scope with
        | FsHotWatch.Cli.IpcParsing.NoTestsRun(FsHotWatch.Cli.IpcParsing.NoTestsReason.ChangesUncovered(symbols,
                                                                                                        total,
                                                                                                        unrunnable)) ->
            test <@ symbols = [ "Lib.orphan" ] && total = 1 @>
            test <@ unrunnable.SymbolCount = 1 @>
            test <@ unrunnable.Projects = [ "Unlisted" ] @>
        | other -> failwithf "expected a changes-uncovered scope naming the unlisted project, got %A" other)

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-110: a symbol with no covering test anywhere is uncovered, and names no unrunnable project`` () =
    // The control: genuinely uncovered stays genuinely uncovered.
    withTempDir "a110-uncovered" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        let orphan: TestPrune.AstAnalyzer.SymbolInfo =
            { FullName = "Lib.lonely"
              Kind = TestPrune.AstAnalyzer.SymbolKind.Value
              SourceFile = "Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "lonely"
              IsExtern = false }

        db.RebuildProjects([ TestPrune.AstAnalyzer.AnalysisResult.Create([ orphan ], [], []) ])
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        seedBaseline tmpDir [ "P1" ]
        let configs = [ runner tmpDir "P1" "P1Tests.fooTest" ]
        PendingVerification.save tmpDir (Set.ofList [ "Lib.lonely" ])

        let host = session tmpDir dbPath configs
        test <@ isCompleted (buildAndSettle host) @>

        match (testScope host).Scope with
        | FsHotWatch.Cli.IpcParsing.NoTestsRun(FsHotWatch.Cli.IpcParsing.NoTestsReason.ChangesUncovered(_,
                                                                                                        _,
                                                                                                        unrunnable)) ->
            test <@ unrunnable = FsHotWatch.Cli.IpcParsing.UnrunnableCoverage.none @>
        | other -> failwithf "expected a changes-uncovered scope, got %A" other)

// ---------------------------------------------------------------------------
// The durable ledgers themselves
// ---------------------------------------------------------------------------

[<Fact>]
let ``FullSuiteBaseline round-trips, reads a missing file as none, and an existing unreadable one as Unreadable`` () =
    withTempDir "a110-baseline-file" (fun tmpDir ->
        test <@ FullSuiteBaseline.load tmpDir = FullSuiteBaseline.LoadedBaseline.Loaded None @>

        let baseline: FullSuiteBaseline.Baseline =
            { RunId = Guid.NewGuid()
              EarnedAt = DateTime(2026, 9, 6, 10, 30, 0, DateTimeKind.Utc)
              Projects = Set.ofList [ "A"; "B" ] }

        FullSuiteBaseline.save tmpDir baseline
        test <@ FullSuiteBaseline.load tmpDir = FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) @>

        File.WriteAllText(FullSuiteBaseline.sidecarPath tmpDir, "{\"runId\":")

        match FullSuiteBaseline.load tmpDir with
        | FullSuiteBaseline.LoadedBaseline.Unreadable _ -> ()
        | other -> failwithf "a torn baseline must be Unreadable, got %A" other)

[<Fact>]
let ``FullSuiteBaseline.staleness names exactly the configured projects the baseline never executed`` () =
    let baseline: FullSuiteBaseline.Baseline =
        { RunId = Guid.NewGuid()
          EarnedAt = DateTime.UtcNow
          Projects = Set.ofList [ "A"; "B" ] }

    test <@ FullSuiteBaseline.staleness (Set.ofList [ "A" ]) baseline = None @>
    test <@ FullSuiteBaseline.staleness (Set.ofList [ "A"; "B" ]) baseline = None @>

    match FullSuiteBaseline.staleness (Set.ofList [ "A"; "C" ]) baseline with
    | Some reason -> test <@ reason.Contains "C" && not (reason.Contains "A —") @>
    | None -> failwith "a project the baseline never executed makes it stale"

[<Fact>]
let ``OutstandingFailure ledger round-trips without the detail, and a torn file is Unreadable`` () =
    withTempDir "a110-reds-file" (fun tmpDir ->
        test <@ OutstandingFailure.load tmpDir = OutstandingFailure.LoadedFailures.Loaded [] @>

        let red: OutstandingFailure =
            { Project = "P2"
              Class = Some "P2Tests"
              Method = Some "barTest"
              File = "/repo/tests/P2Tests.fs"
              Entry =
                { FsHotWatch.ErrorLedger.ErrorEntry.errorWithDetail "failed P2Tests.barTest" "the whole transcript" with
                    Line = 12 } }

        let projectLevel: OutstandingFailure =
            { Project = "P3"
              Class = None
              Method = None
              File = "<tests/P3>"
              Entry = FsHotWatch.ErrorLedger.ErrorEntry.deferredWithDetail "P3: waiting on build" "apphost missing" }

        OutstandingFailure.save tmpDir [ red; projectLevel ]

        let expected =
            [ { red with
                  Entry = { red.Entry with Detail = None } }
              { projectLevel with
                  Entry =
                      { projectLevel.Entry with
                          Detail = None } } ]

        test <@ OutstandingFailure.load tmpDir = OutstandingFailure.LoadedFailures.Loaded expected @>

        File.WriteAllText(OutstandingFailure.sidecarPath tmpDir, "[{\"project\":\"P2\"}]")

        match OutstandingFailure.load tmpDir with
        | OutstandingFailure.LoadedFailures.Unreadable _ -> ()
        | other -> failwithf "an entry missing its fields must make the ledger Unreadable, got %A" other)
