/// The plugin's IPC commands: the observers that answer from state, and the run and scope requests.
module FsHotWatch.TestPrune.PluginCommands

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

/// What the plugin's IPC commands read from the plugin that builds them: its fixed
/// configuration, and the questions only it can answer.
type internal CommandEnv =
    {
        RepoRoot: string
        TestConfigs: TestConfig list option
        /// The configured test projects.
        RunnableProjects: Set<string>
        /// Each project's current outcome key under the model `graph` names.
        CurrentOutcomeKeys: ProjectGraphAccessor -> Map<string, string>
        /// Why the full-suite baseline cannot vouch for a filtered run, if it cannot.
        /// The impact queries the plugin runs against its index.
        Queries: ImpactQueries
        BaselineInvalidReason: VerificationDebt -> string option
    }

/// The `run-tests` request: a forced run of the projects and filter it names, answered once the run completes.
let internal runTestsCommand (allConfigs: TestConfig list) =

    PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
        async {
            // FORCE semantics: `test-rerun` is the explicit "prove it
            // ran" verb. The run NEVER executes here — it is an intent on
            // the "tests" key, queued behind the run in flight (see
            // `RunTestsRequested`). A force-run is owed work, never
            // refused. The only thing bounded here is the WAIT:
            // `waitSec` caps queue time plus run time, and on expiry
            // this reports a DISTINCT `busy` status so the CLI exits
            // non-zero rather than reporting a verdict no run produced.
            try
                let argStr = if args.Length > 0 then args.[0].Trim() else "{}"
                let waitForResultMs = parseRunTestsWaitMs argStr DefaultRunTestsWaitMs

                let parseResult =
                    try
                        Ok(JsonDocument.Parse(argStr))
                    with ex ->
                        Error ex.Message

                match parseResult with
                | Error msg ->
                    return
                        TestPruneReplies.RunReply.serialize (
                            TestPruneReplies.RunReply.Refused $"invalid JSON: %s{msg}"
                        )
                | Ok doc ->

                    use doc = doc
                    let root = doc.RootElement

                    let filter =
                        match root.TryGetProperty("filter") with
                        | true, v -> Some(v.GetString())
                        | false, _ -> None

                    let onlyFailed =
                        match root.TryGetProperty("only-failed") with
                        | true, v -> v.GetBoolean()
                        | false, _ -> false

                    let projectFilter =
                        match root.TryGetProperty("projects") with
                        | true, v -> v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                        | false, _ -> None

                    // `--only-failed` depends on what failed, which is plugin state. A
                    // request never reads state, so the choice is made by `Update`
                    // against the state it folds the request into.
                    let selection =
                        if onlyFailed then
                            None
                        else
                            match projectFilter with
                            | Some names -> Some(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                            | None -> Some allConfigs

                    match selection with
                    | Some configs when configs.IsEmpty ->
                        // Name what was asked for and what exists. A bare
                        // "no matching test projects" is unactionable for the
                        // one case that actually produces it — a mistyped or
                        // renamed `--project` — and the configured names are
                        // right here.
                        let msg =
                            match projectFilter with
                            | Some names ->
                                let asked = names |> Set.toList |> List.sort |> String.concat ", "

                                let known =
                                    allConfigs |> List.map (fun c -> c.Project) |> List.sort |> String.concat ", "

                                $"no test project matches --project %s{asked}. Configured test projects: %s{known}"
                            | None -> "no matching test projects"

                        return TestPruneReplies.RunReply.serialize (TestPruneReplies.RunReply.Refused msg)
                    | _ ->
                        let reply =
                            Tasks.TaskCompletionSource<string>(
                                Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        // An intent on the "tests" key: it waits behind the run
                        // in flight, owned, and launches from the fold it
                        // becomes. A receipt that fails means no fold will
                        // launch it, so the wait below ends with that failure.
                        let request =
                            match selection with
                            | Some configs -> RunTestsRequested(configs, filter, reply)
                            | None -> RunFailedTestsRequested(filter, reply)

                        let receipt = ctx.EnqueueExclusiveIntent "tests" None request

                        receipt.ContinueWith(
                            (fun (admitted: Tasks.Task) ->
                                if admitted.IsFaulted then
                                    reply.TrySetResult(
                                        TestPruneReplies.RunReply.serialize (
                                            TestPruneReplies.RunReply.Refused(
                                                admitted.Exception.GetBaseException().Message
                                            )
                                        )
                                    )
                                    |> ignore),
                            Tasks.TaskContinuationOptions.ExecuteSynchronously
                        )
                        |> ignore

                        // Bounded await: the reply resolves
                        // when the run finishes — behind the test-prune
                        // mailbox and possibly behind a run already in
                        // flight — so an unbounded wait here could pin the
                        // IPC caller for as long as the daemon is wedged.
                        let! winner =
                            Tasks.Task.WhenAny(reply.Task, Tasks.Task.Delay(waitForResultMs))
                            |> Async.AwaitTask

                        if winner = (reply.Task :> Tasks.Task) then
                            return reply.Task.Result
                        else
                            return
                                TestPruneReplies.RunReply.serialize (
                                    TestPruneReplies.RunReply.Busy
                                        $"the test run did not produce a result within %d{waitForResultMs / 1000}s (still queued or running); retry, or raise --wait-sec"
                                )
            with ex ->
                // Command-local faults only (the run itself executes in
                // the mailbox-launched work, which owns run faults per
                // and always resolves the reply + posts
                // the Aborted lifecycle). Nothing to post here: no run
                // was launched.
                Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                return TestPruneReplies.RunReply.serialize (TestPruneReplies.RunReply.Refused ex.Message)
        })

/// The `check-reach` observer: how far the checks of a run reached, from the session's run ledger.
let internal checkReachCommand (allConfigs: TestConfig list) =

    PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (args: string array) ->
        async {
            let notRecorded (reason: string) =
                JsonSerializer.Serialize({| recorded = false; reason = reason |})

            let requested =
                match args |> Array.tryHead |> Option.map _.Trim() with
                | None
                | Some "" -> Ok None
                | Some text ->
                    match Guid.TryParse text with
                    | true, runId -> Ok(Some runId)
                    | _ -> Error $"'%s{text}' is not a run id"

            let entry =
                match requested with
                | Error why -> Error why
                | Ok None ->
                    match List.tryHead state.CheckReaches with
                    | Some found -> Ok found
                    | None -> Error "no test run has completed in this session, so there is no result to project"
                | Ok(Some wanted) ->
                    match state.CheckReaches |> List.tryFind (fun (id, _, _, _) -> id = wanted) with
                    | Some found -> Ok found
                    | None ->
                        let wantedId = wanted.ToString("N")

                        Error
                            $"run %s{wantedId} has no retained projection in this session — it never \
                               completed here, or it is older than the last %d{SessionRunLedger} completions"

            match entry with
            | Error why -> return notRecorded why
            | Ok(runId, wouldHaveRun, reach, recall) ->
                let projects = allConfigs |> List.map (fun c -> c.Project)

                let kind, ranProjects, totalProjects =
                    match wouldHaveRun |> Option.map (scopeOfSelection projects) with
                    | Some(ScopeFull n) -> box "full", n, n
                    | Some(ScopeFiltered(ran, total)) -> box "filtered", ran, total
                    | Some(ScopeNone total) -> box "none", 0, total
                    // No retained selection ⇒ no scope to describe. `null`,
                    // never a count: a zero here would read as "check would
                    // have run nothing", which is a claim, and this is an
                    // absence.
                    | None -> null, 0, List.length projects

                let failingSuites =
                    match reach with
                    | ReachedAFailure ps -> List.toArray ps
                    | ReachedNoFailure _
                    | NoFailuresToReach
                    | ReachUnknown _ -> [||]

                let missed =
                    match reach with
                    | ReachedNoFailure failures ->
                        failures
                        |> List.map (fun failure ->
                            {| project = failure.Project
                               ``class`` = failure.Class
                               cause =
                                (match failure.Cause with
                                 | MissCause.ProjectNotSelected -> "project-not-selected"
                                 | MissCause.ClassNotInFilter -> "class-not-in-filter") |})
                        |> List.toArray
                    | _ -> [||]

                let reason =
                    match reach with
                    | ReachUnknown r -> box r
                    | ReachedAFailure _
                    | ReachedNoFailure _
                    | NoFailuresToReach -> null

                let (recallMeasured, recallReached, recallTotal, recallThreshold, recallAcceptable, recallReason) =
                    match recall with
                    | RecallMeasured(reached, total, threshold, acceptable) ->
                        true, reached, total, threshold, box acceptable, null
                    | RecallNotMeasurable why -> false, 0, 0, 1.0, null, box why

                return
                    JsonSerializer.Serialize(
                        {| recorded = true
                           runId = runId.ToString("N")
                           kind = kind
                           ranProjects = ranProjects
                           totalProjects = totalProjects
                           reach = CheckReach.token reach
                           failingSuites = failingSuites
                           missed = missed
                           reason = reason
                           conditionalFailureRecall =
                            {| measured = recallMeasured
                               reached = recallReached
                               total = recallTotal
                               threshold = recallThreshold
                               acceptable = recallAcceptable
                               reason = recallReason |} |}
                    )
        })

/// The `test-scope` observer: what the test evidence held for the tree a verdict asks about covers, or why none does.
let internal testScopeCommand (env: CommandEnv) (allConfigs: TestConfig list) =
    let repoRoot = env.RepoRoot
    let runnableProjects = env.RunnableProjects
    let currentOutcomeKeys = env.CurrentOutcomeKeys
    let baselineInvalidReason = env.BaselineInvalidReason

    PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
        async {
            // What the last completed run ACTUALLY covered — the evidence a
            // merge verdict is computed from. Never a restatement of what was
            // requested: `set-scope full` is a request, this is the receipt.
            // A run still in flight reports `running` while its worker is live
            // and `settling` until its result fold commits. `confirm` treats
            // both as "no verdict yet" rather than as a scope.
            //
            // The reply carries the RUN ID as well, so the CLI can DECLARE
            // which CTRF reports belong to this run
            // (`.fshw/test-runs/<runId>/`) instead of inferring membership
            // from mtimes.
            // Check again at the read boundary: inputs may have changed
            // after completion, even before the next watcher event arrives.
            //
            // And against the model: a receipt earned under a replaced model
            // grades nothing, so a `confirm` reading it escalates to a run under
            // the model it is graded against instead of grading a run that no
            // evidence for that model names.
            //
            // In flight covers the fold window too: once the worker is gone,
            // `IsRunning "tests"` is false, but until the fold holding the key
            // commits, the state's receipt, scope and completed runs are the
            // PREVIOUS run's. Graded as settled, they would hand `confirm` that
            // run's scope as the current one; `settling` names no run instead.
            let currentTree = lazy (ReceiptInputTree.read repoRoot)

            let inFlight =
                if ctx.IsRunning "tests" then Some "running"
                elif ctx.IsSettling "tests" then Some "settling"
                else None

            let receipt =
                match inFlight, state.EvidenceReceipt with
                | None, Some evidence ->
                    let currentModel = ObservedModel.generation ctx.ProjectGraph

                    if not (ReceiptInputTree.matches evidence.InputTreeHash currentTree.Value) then
                        None
                    elif not (receiptSpeaksFor currentModel (ObservedModel.inputs ctx.ProjectGraph) evidence) then
                        let withheld = evidence.RunId.ToString("N")

                        ctx.Log
                            $"test-scope: run %s{withheld} earned its receipt under project model generation %s{ObservedModel.describe evidence.ModelGeneration}; the model is now %s{ObservedModel.describe currentModel}, so it grades nothing until a run under this model earns one"

                        None
                    else
                        Some evidence
                | _ -> None

            let runId =
                match receipt with
                | Some receipt -> box (receipt.RunId.ToString("N"))
                | None -> null

            // The evidence this state holds for the run it grades, read from the
            // SAME state as `runId`. The verdict grades that run by its evidence;
            // read here, the two come from one snapshot instead of from two
            // replies (this one and the diagnostics ledger) that a completion can
            // land between. `null` when no evidence for that run is held under
            // the current model, which refuses the green as surely as an absent
            // ledger entry does.
            let evidence =
                receipt
                |> Option.bind (fun receipt ->
                    EarnedEvidence.retainedForRun
                        (ObservedModel.generation ctx.ProjectGraph)
                        (ObservedModel.inputs ctx.ProjectGraph)
                        receipt.RunId
                        state.Earned)
                |> Option.map (AnalysisEvidence.refuseThrough state.CohortSeal)
                |> Option.map (fun held ->
                    box
                        {| runId = held.RunId.ToString("N")
                           modelGeneration = held.Generation
                           refusals = List.toArray held.FailureReasons |})
                |> Option.toObj

            // The scope is a PROJECTION of `LastCoverage` — the very value the
            // ledger uses to decide what a run is entitled to CLEAR.
            // See `scopeOf`.
            let projects = allConfigs |> List.map (fun c -> c.Project)

            // The change that selected the last run's tests. Sent so a
            // check that selects NOTHING can still say what the last
            // change that DID trigger tests was — otherwise that fact
            // exists only in a daemon log, which is exactly where a
            // reader will not look before concluding the selector is
            // broken.
            //
            // Truncated on the WIRE, with the full count beside it: a
            // pathological flush can carry thousands of seeds, and a
            // reply that grows without bound to serve a diagnostic line
            // is a new failure mode in the path that earns verdicts.
            //
            // A zero-test completion no longer writes a
            // receipt (`ReceiptTransition`), so the seeds and the zero
            // reason it used to carry are read from the state the same
            // completion wrote: `LastSeeds` is the change that last
            // selected tests, `LastZeroSelection` why this one selected
            // none. A retained receipt still speaks for itself.
            let evidenceSeeds =
                receipt
                |> Option.map (fun receipt -> receipt.Seeds)
                |> Option.defaultValue state.LastSeeds

            let seeds = evidenceSeeds |> List.truncate 8 |> List.toArray
            let seedCount = List.length evidenceSeeds

            // Every run this session has completed, newest
            // first. Sent on EVERY branch — including `running`, which is
            // what a check reads when it takes its baseline against a busy
            // daemon, and a baseline that came back silent would hand the
            // check its predecessor's runs.
            let runIds =
                state.CompletedRuns |> List.map (fun id -> id.ToString("N")) |> List.toArray

            // The full-suite baseline this ledger's greens are
            // relative to, on EVERY branch — a `running` reply still names the
            // baseline a later reading will be judged against. `baseline` is
            // the reference; `baselineAbsent` says why there is none. Both
            // null only for an analysis-only daemon, which makes no test claim.
            let baseline, baselineAbsent =
                match baselineInvalidReason state.Debt, state.Debt.Baseline with
                | None, Some b when not (Set.isEmpty runnableProjects) ->
                    box
                        {| runId = b.RunId.ToString("N")
                           earnedAt = b.EarnedAt.ToString("o")
                           projects = b.Projects |> Set.toArray |},
                    null
                | None, _ -> null, null
                | Some reason, _ -> null, box reason

            // The changed files whose changes selected no tests, on EVERY branch:
            // they are a fact about the current selection, whichever run the
            // scope is read from. Always an array, so an empty one says "none".
            let notSelected =
                state.NotSelected
                |> Map.toArray
                |> Array.map (fun (file, reason) -> NotSelectedFile.wire { File = file; Reason = reason })

            match inFlight with
            | Some kind ->
                return
                    JsonSerializer.Serialize(
                        {| kind = kind
                           notSelected = notSelected
                           runId = runId
                           runIds = runIds
                           baseline = baseline
                           baselineAbsent = baselineAbsent |}
                    )
            | None ->
                // ADR-059 step 7b: each project's full-run outcome key over this tree,
                // for the shadow lookup verdict. Settled replies only: a run in flight
                // has no answer yet, and keying the tree is a walk.
                let outcomeKeys =
                    System.Collections.Generic.Dictionary<string, string>(currentOutcomeKeys ctx.ProjectGraph)

                let evidenceCoverage =
                    receipt
                    |> Option.map (fun receipt -> receipt.Coverage)
                    |> Option.defaultValue RunCoverage.none

                // Which cause selected each project of the graded run. Null only
                // where no receipt says, which a `full` or `filtered` scope never is.
                let selectedBy =
                    receipt
                    |> Option.map (fun receipt ->
                        receipt.SelectedBy |> List.map ProjectAttribution.wire |> Array.ofList)
                    |> Option.toObj

                // The seeds that alone selected the most tests, beside `selectedBy`.
                let seedFanout =
                    receipt
                    |> Option.map (fun receipt -> SeedFanoutReading.wire receipt.SeedFanout)
                    |> Option.toObj

                // What the graded run was priced at before it ran. Null where it was not priced.
                let price =
                    receipt
                    |> Option.bind (fun receipt -> receipt.Price)
                    |> Option.map SelectionPrice.Reading.wire
                    |> Option.toObj

                match scopeOf projects evidenceCoverage with
                | ScopeFull n ->
                    // Why this run took every project. Always present here: a
                    // `full` scope is read from a receipt, and the receipt
                    // carries its launch's cause.
                    let cause =
                        receipt
                        |> Option.map (fun receipt -> FullSuiteCause.token receipt.FullSuiteCause)
                        |> Option.toObj

                    return
                        JsonSerializer.Serialize(
                            {| kind = "full"
                               cause = cause
                               selectedBy = selectedBy
                               seedFanout = seedFanout
                               price = price
                               notSelected = notSelected
                               runIds = runIds
                               baseline = baseline
                               baselineAbsent = baselineAbsent
                               ranProjects = n
                               totalProjects = n
                               runId = runId
                               evidence = evidence
                               seeds = seeds
                               seedCount = seedCount
                               outcomeKeys = outcomeKeys |}
                        )
                | ScopeFiltered(ran, total) ->
                    return
                        JsonSerializer.Serialize(
                            {| kind = "filtered"
                               selectedBy = selectedBy
                               seedFanout = seedFanout
                               price = price
                               notSelected = notSelected
                               runIds = runIds
                               baseline = baseline
                               baselineAbsent = baselineAbsent
                               ranProjects = ran
                               totalProjects = total
                               runId = runId
                               evidence = evidence
                               seeds = seeds
                               seedCount = seedCount
                               outcomeKeys = outcomeKeys |}
                        )
                | ScopeNone total ->
                    let zero =
                        receipt
                        |> Option.map (fun receipt -> receipt.ZeroSelection)
                        |> Option.defaultValue state.LastZeroSelection

                    // A completion that selected tests names no zero reason, so
                    // the reason is why no evidence speaks for this tree.
                    let reason, detail =
                        match ZeroSelection.token zero with
                        | Some token -> token, None
                        | None ->
                            UnverifiedTree.classify
                                state.EvidenceReceipt
                                currentTree.Value
                                state.ReceiptRevoked
                                state.CompletedRuns
                            |> UnverifiedTree.wire

                    return
                        JsonSerializer.Serialize(
                            {| kind = "none"
                               notSelected = notSelected
                               runIds = runIds
                               baseline = baseline
                               baselineAbsent = baselineAbsent
                               ranProjects = 0
                               totalProjects = total
                               runId = runId
                               evidence = evidence
                               seeds = seeds
                               seedCount = seedCount
                               noTestsReason = reason
                               noTestsDetail = Option.toObj detail
                               uncoveredSymbols = (ZeroSelection.symbols zero |> List.truncate 8 |> List.toArray)
                               uncoveredSymbolCount = List.length (ZeroSelection.symbols zero)
                               // Of the uncovered symbols, how many
                               // HAVE covering tests this daemon cannot run, and where.
                               unrunnableSymbolCount = Map.count (ZeroSelection.unrunnable zero)
                               unrunnableProjects =
                                (ZeroSelection.unrunnable zero |> UnrunnableCoverage.projects |> Set.toArray)
                               outcomeKeys = outcomeKeys |}
                        )
        })

/// The plugin's IPC commands: the observers that answer from state, and, when test
/// projects are configured, the run and scope requests.
let internal pluginCommands (env: CommandEnv) =
    let repoRoot = env.RepoRoot
    let testConfigs = env.TestConfigs
    let queries = env.Queries

    let commands =
        [ "affected-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  // Compute on demand from state.ChangedSymbols against current DB
                  // state. ChangedSymbols accumulates across FileChecked events and
                  // is reset by flushAndQueryAffected on BuildCompleted.
                  let symbols = state.ChangedSymbols |> List.distinct

                  let tests =
                      if symbols.IsEmpty then
                          []
                      else
                          (queries.AffectedTests symbols).Tests

                  return
                      tests
                      |> List.map (fun t ->
                          ({ Project = t.TestProject
                             Class = t.TestClass
                             Method = t.TestMethod }
                          : TestPruneReplies.AffectedTest))
                      |> TestPruneReplies.AffectedTests.serialize
              })
          |> PluginCommand.typed TestPruneReplies.AffectedTestsSchema

          "changed-files",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async { return TestPruneReplies.ChangedFiles.serialize state.ChangedFiles })
          |> PluginCommand.typed TestPruneReplies.ChangedFilesSchema

          "test-results",
          // The last COMMITTED result set, and only once it is the current one. While a
          // run's worker is live this answers `running`. Between the worker finishing and
          // its `TestsFinished`/`CommandTestsFinished` fold committing, `state` is still
          // the pre-fold state — the PREVIOUS run's results — so this answers `settling`
          // rather than hand those back as though they were the run that just finished.
          PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  let reply =
                      if ctx.IsRunning "tests" then
                          TestPruneReplies.RunReply.Running
                      elif ctx.IsSettling "tests" then
                          TestPruneReplies.RunReply.Settling
                      else
                          match state.LastResults with
                          // No filter: `test-results` reports the LAST stored result set,
                          // which does not remember what launched it. Saying nothing is
                          // correct here; inventing "(none)" would claim it was unfiltered.
                          | Some results ->
                              TestPruneReplies.RunReply.Report(formatTestResultsJson None Map.empty results)
                          | None -> TestPruneReplies.RunReply.NotRun

                  return TestPruneReplies.RunReply.serialize reply
              })
          |> PluginCommand.typed TestPruneReplies.RunSchema

          "flaky-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (_state: TestPruneState) (_args: string array) ->
              async {
                  let history = Flakiness.loadHistory (Flakiness.historyPath repoRoot)
                  let top = Flakiness.topFlaky 10 history

                  return
                      top
                      |> List.map (fun (name, score) ->
                          ({ Name = name
                             Flakiness = score
                             Runs = history |> Map.tryFind name |> Option.map List.length |> Option.defaultValue 0 }
                          : TestPruneReplies.FlakyTest))
                      |> TestPruneReplies.FlakyTests.serialize
              })
          |> PluginCommand.typed TestPruneReplies.FlakyTestsSchema ]

    // run-tests / scope commands (only if testConfigs are provided). The scope verbs
    // live behind the same condition on purpose: a repo with no test projects has no suite
    // to run in full, so `fshw confirm` finds no `set-scope` command, cannot establish
    // the full-suite scope, and refuses to produce a merge verdict — rather than
    // silently issuing a green one it never earned.
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "set-scope",
                PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
                    async {
                        // `fshw confirm` calls this BEFORE triggering its
                        // scan, so the test run the scan provokes is already unfiltered.
                        // It replies once the owner has committed the scope, so the scan
                        // it triggers next is folded against it.
                        let requested =
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                            try
                                use doc = JsonDocument.Parse(argStr)

                                match doc.RootElement.TryGetProperty("scope") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> "impact"
                            with _ ->
                                "impact"

                        match requested with
                        | "owed" ->
                            do! ctx.EnqueueExclusiveIntent "scope" None OwedRunRequested |> Async.AwaitTask

                            return
                                TestPruneReplies.ScopeReply.serialize (TestPruneReplies.ScopeReply.ScopeSet requested)
                        | "full"
                        | "impact" ->
                            // Its own intent key: a scope change queued behind a running
                            // suite would hold `confirm` for that suite's whole length.
                            do!
                                ctx.EnqueueExclusiveIntent "scope" None (ScopeRequested(TestMode.ofScope requested))
                                |> Async.AwaitTask

                            return
                                TestPruneReplies.ScopeReply.serialize (TestPruneReplies.ScopeReply.ScopeSet requested)
                        | other ->
                            return
                                TestPruneReplies.ScopeReply.serialize (
                                    TestPruneReplies.ScopeReply.ScopeRefused
                                        $"unknown scope '%s{other}' (expected 'full', 'impact' or 'owed')"
                                )
                    })
                |> PluginCommand.typed TestPruneReplies.SetScopeSchema

                "test-scope",
                testScopeCommand env allConfigs
                |> PluginCommand.typed TestPruneReplies.TestScopeSchema

                // What `check` WOULD have concluded about the run
                // `confirm` just widened to full — the sample `confirm` used to destroy.
                //
                // A SEPARATE command from `test-scope`, not another field on it. The two
                // answer opposite questions (`test-scope`: what DID run; this: what would
                // NOT have) and `test-scope` is on the path that earns every verdict,
                // where a new field is a new way for the reply to fail to parse. A daemon
                // that has never heard of this command returns the unknown-command
                // sentinel, which the CLI reads as "no sample" — never as agreement.
                "check-reach",
                //
                // The optional argument is the run to answer for — the run the verdict
                // GRADES, which need not be the newest completion. Without one, the newest.
                checkReachCommand allConfigs
                |> PluginCommand.typed TestPruneReplies.CheckReachSchema

                "run-tests", runTestsCommand allConfigs |> PluginCommand.typed TestPruneReplies.RunSchema ]
        | _ -> commands

    allCommands
