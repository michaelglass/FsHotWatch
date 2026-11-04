/// What a test run carries besides its launch: the configs a red run failed, an aborted run's lifecycle, outstanding-failure reporting, the trace host, and the collection its parallel launches write.
module FsHotWatch.TestPrune.RunSupport

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.TestPrune.TestReports
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.PluginCommands
open FsHotWatch.TestPrune.IndexMaintenance

/// The test projects `run-tests --only-failed` re-runs: every non-green project of the
/// last run, plus every project still owing an outstanding failure.
///
/// "Failed" is the OUTSTANDING set, not merely the last run's results: after an
/// impact-filtered run the failing project is not in `LastResults` at all (it wasn't
/// selected), and `--only-failed` would have re-run nothing — "no matching test
/// projects" — while a red sat there. The outstanding ledger is what still owes a
/// re-run, so it is what this verb must re-run.
let internal failedTestConfigs
    (allConfigs: TestConfig list)
    (lastResults: TestResults option)
    (outstandingFailures: OutstandingFailure list)
    : Result<TestConfig list, string> =
    let outstandingProjects =
        outstandingFailures |> List.map (fun f -> f.Project) |> Set.ofList

    let lastRunFailed =
        match lastResults with
        | Some prev ->
            prev.Results
            |> Map.toList
            |> List.choose (fun (name, r) ->
                match r with
                | TestsFailed _
                | TestsTimedOut _
                // A deferred project never ran, and an errored one aborted without a
                // verdict — both are non-green, so `--only-failed` must pick them up.
                | TestsDeferred _
                | TestsErrored _ -> Some name
                | _ -> None)
            |> Set.ofList
        | None -> Set.empty

    let failedNames = Set.union lastRunFailed outstandingProjects

    if lastResults.IsNone && Set.isEmpty failedNames then
        Error "no previous results — cannot determine failed projects"
    else
        Ok(allConfigs |> List.filter (fun c -> failedNames.Contains(c.Project)))

/// Build the degenerate Started→Aborted lifecycle a faulted run posts back so the
/// synchronous `TestsFinished` handler drives the plugin to a NON-green terminal status.
/// A `beforeRun` throw / `executeTests` fault means the suite it guards NEVER RAN — that
/// must surface as a failure, never a stale prior green. Shared by the
/// impact path and the manual `run-tests` command so the two stay in lockstep.
/// `Results = Map.empty` ⇒ the handler commits nothing from the pending queue; `reason`
/// carries the hook's failure output so `fshw check` / `fshw errors` shows WHY.
/// What a run that threw says about why it aborted: its message, except that a database
/// schema mismatch (a table without a column or table this fshw reads) is named as one. A
/// mismatch aborts every re-run the same way on a quiet box too, so reading it as load
/// sends its reader to wait for headroom that changes nothing (AUTOMATION-1434).
let internal abortReasonOf (ex: exn) : string =
    let causes =
        match ex with
        | :? AggregateException as many -> List.ofSeq (many.Flatten().InnerExceptions)
        | one -> [ one ]

    let isSchemaMismatch (e: exn) =
        match e with
        | :? Microsoft.Data.Sqlite.SqliteException as sqlite ->
            Text.RegularExpressions.Regex.IsMatch(sqlite.Message, "no such (column|table)|has no column named")
        | _ -> false

    match List.tryFind isSchemaMismatch causes with
    | Some schema ->
        $"a database schema mismatch, not load: %s{schema.Message}. A table does not have the shape this fshw \
          reads, so every re-run aborts the same way, on a quiet box too. Stop the daemon and delete \
          .fshw/test-impact.db (the test-impact index); the next daemon rebuilds it"
    | None -> ex.Message

let internal abortedRunLifecycle
    (emittedStart: TestRunStarted option)
    (reason: string)
    : TestRunStarted * TestRunCompleted =
    let started =
        emittedStart
        |> Option.defaultWith (fun () ->
            { RunId = Guid.NewGuid()
              StartedAt = DateTime.UtcNow })

    let completed: TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Aborted reason
          Results = Map.empty
          // No project was invoked, so that is what it says. Under the old `bool`
          // this had to pick between claiming a suite that never ran and claiming a
          // filtering that never happened — the comment here read "a lie either
          // way". There is now a case for what actually occurred.
          Verification = NoProjectsSelected }

    started, completed










/// Rewrite this plugin's whole slice of the error ledger to be exactly what is still
/// OUTSTANDING.
///
/// `ClearAllErrors` (== `ClearPlugin "test-prune"`) wipes the slate, and MUST always be
/// paired with a re-report of everything the plugin still owes: every outstanding red,
/// including ones carried from earlier runs this one did not cover, and every
/// unanalysable-file warning. Re-reporting only THIS run's findings
/// lets a narrower run erase reds it never executed, and drops the warning that is
/// supposed to deny the check its green verdict.
///
/// Clearing-and-re-reporting is the ONLY path to the ledger, so an entry can disappear
/// only by leaving the outstanding set: a red needs a run that COVERED it, a warning
/// needs the file to analyse cleanly.
let internal reportOutstanding
    (ctx: PluginCtx<TestPruneMsg>)
    (unanalyzable: Map<string, UnanalyzableFile>)
    (failedExtensions: Map<string, string>)
    (outstanding: OutstandingFailure list)
    =
    ctx.ClearAllErrors()

    let failureEntries = outstanding |> List.map (fun f -> f.File, f.Entry)

    let unanalyzableEntries =
        unanalyzable
        |> Map.toList
        |> List.map (fun (_, u) -> u.File, unanalyzableFileDiagnostic u.RelPath u.Reason)

    let extensionEntries =
        failedExtensions
        |> Map.toList
        |> List.map (fun (name, reason) -> extensionLedgerKey name, extensionFailedDiagnostic name reason)

    failureEntries @ unanalyzableEntries @ extensionEntries
    |> List.groupBy fst
    |> List.iter (fun (file, entries) -> ctx.ReportErrors file (entries |> List.map snd))

/// What a run's `executeTests` needs to record traces: the run's trace settings and
/// mode, the decision function, the plugin's activity log and subtasks, and a way to wait
/// for the plugin to fold its symbol index.
type internal TraceRunHost =
    {
        TraceRuntime: TraceRuntime
        TraceWiring: TraceWiring
        TraceLog: string -> unit
        HoldSubtask: string -> string -> IDisposable
        /// The plugin's own folds of the symbol index, which the traces are joined against.
        IndexFold: IndexFold
    }

/// What a run's parallel project launches collect, as one value. Everything but `Results`
/// is used SERIALLY once the parallel section ends, so no two launches race on a database
/// write, a shared output file or a history file's read-modify-write.
type internal RunCollection =
    {
        /// The run-wide results, built up as groups complete. Per-group deltas are emitted
        /// as TestProgress; the whole map is carried by TestRunCompleted (and returned to a
        /// caller with no host).
        Results: Map<string, TestResult>
        /// Raw-cobertura inputs, newest first, each with its project and ratchet intent;
        /// ingested and emitted once, after the parallel section.
        CoverageInputs: CoverageInput list
        /// Projects whose coverage receipt could not be read, newest first.
        CoverageFailures: CoverageIngestFailure list
        /// Per-test flakiness records, in arrival order. `Flakiness.appendRecords` parses
        /// and rewrites the whole history file, so it runs once, not once per project.
        Flakiness: Flakiness.TestRunRecord list
        /// Each launched project's trace decision, newest first: one writer to one store.
        TraceRuns: TracedProjectRun list
        /// The projects whose stored evidence this run filed instead of launching them.
        Materialized: Map<string, TestEvidenceStore.Entry>
        /// Timeouts held back on a starved box, newest first, to re-run alone at the end.
        Starved: (TestConfig * Starvation.TimedOut * Starvation.Reading) list
    }

/// One run's `RunCollection`, read and updated under a single lock. An update and the
/// action after it (`UpdateThen`) run under that lock together, so concurrent group
/// completions emit their progress in the order they were folded.
type internal RunCollector() =
    let gate = obj ()

    let mutable current: RunCollection =
        { Results = Map.empty
          CoverageInputs = []
          CoverageFailures = []
          Flakiness = []
          TraceRuns = []
          Materialized = Map.empty
          Starved = [] }

    member _.Update(f: RunCollection -> RunCollection) : unit =
        lock gate (fun () -> current <- f current)

    member _.UpdateThen(f: RunCollection -> RunCollection, after: unit -> unit) : unit =
        lock gate (fun () ->
            current <- f current
            after ())

    member _.Read(f: RunCollection -> 'a) : 'a = lock gate (fun () -> f current)

    /// Remove and return the trace decisions of the projects `pick` selects, oldest first.
    member _.TakeTraceRuns(pick: TracedProjectRun -> bool) : TracedProjectRun list =
        lock gate (fun () ->
            let taken, kept = current.TraceRuns |> List.partition pick
            current <- { current with TraceRuns = kept }
            List.rev taken)

/// Stores each traced project's traces as soon as its result is final, while the rest of
/// the run goes on, instead of storing every project after the last one finished. That
/// serial tail was the whole cost of tracing a confirm (AUTOMATION-1484): a project that
/// finishes early is now stored while the slower ones still run, and the run waits at its
/// end only for what is left.
///
/// Projects are stored one at a time, in the order they settled: the trace store has one
/// writer. `ingest` is `TraceRun.ingestAll` bound to the run, and never throws except that a
/// cancelled run raises `OperationCanceledException`, which `Finish` lets through.
type internal TraceIngestion
    (collected: RunCollector, ingest: TracedProjectRun list -> Async<unit>, runToken: CancellationToken) =
    let gate = obj ()
    let mutable tail: Tasks.Task = Tasks.Task.CompletedTask

    let enqueue (runs: TracedProjectRun list) =
        if not runs.IsEmpty then
            lock gate (fun () ->
                let previous = tail

                tail <-
                    Async.StartAsTask(
                        async {
                            do! Async.AwaitTask previous
                            do! ingest runs
                        },
                        cancellationToken = runToken
                    )
                    :> Tasks.Task)

    /// `project`'s result is final (a starvation re-run is not pending for it): store its
    /// traces now, after any project that settled before it.
    member _.Settled(project: string) =
        enqueue (collected.TakeTraceRuns(fun r -> r.Project = project))

    /// Every project has finished: wait for the projects already being stored, then store
    /// any project that never settled.
    member _.Finish() : Async<unit> =
        async {
            enqueue (collected.TakeTraceRuns(fun _ -> true))
            let pending = lock gate (fun () -> tail)
            do! Async.AwaitTask pending
        }
