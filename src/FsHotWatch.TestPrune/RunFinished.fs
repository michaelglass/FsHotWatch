/// A test run's completion folded into state, over a RunFinishedEnv.
module FsHotWatch.TestPrune.RunFinished

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
open FsHotWatch.TestPrune.RunSupport
open FsHotWatch.TestPrune.RunExecution
open FsHotWatch.TestPrune.AffectedSelection
open FsHotWatch.TestPrune.ImpactRun
open FsHotWatch.TestPrune.FileCheckedHandler
open FsHotWatch.TestPrune.CheckHandlers

/// The debt accounting a run's completion reads (`commitPending`, `debtScope` and the rest).
type internal CompletionDebt =
    { CommitPending: Set<string> -> VerificationDebt -> VerificationDebt
      CoveringOf: string seq -> string -> Set<string>
      DebtScope: Lazy<Map<string, string>> -> (string -> Set<string>) -> string -> Set<string>
      IndexNamesOnlyRunnable: Lazy<Map<string, string>> -> bool
      OwedElsewhere: (string -> Set<string>) -> string seq -> Set<string>
      RevisionOf: VerificationDebt -> string -> int64 }

/// What a completed run's terminal status is reported from (`reportRunOutcome`).
type internal RunOutcomeFacts =
    { Completed: TestRunCompleted
      CarriedFailures: OutstandingFailure list
      QueueAfterCommit: Set<string>
      PendingDescription: unit -> string
      ReceiptTransition: ReceiptTransition
      EvidenceReceipt: TestEvidenceReceipt option }

/// A run's per-project tally and the summary fragments its status line is built from.
type internal RunTally =
    { DeferredList: (string * TestResult) list
      FailedList: (string * TestResult) list
      Failed: int
      Deferred: int
      Aborted: int
      Passed: int
      NoMatchSuffix: string
      SelectedSuffix: string
      TimedOutProjects: string list
      SlowestSuffix: string
      DeferredSuffix: string
      AbortedSuffix: string
      AbortedNames: string }

/// Tally a run that executed: what failed, was deferred, aborted, passed, matched nothing
/// or timed out.
let internal tallyRun (results: TestResults) (total: int) : RunTally =

    // Non-green = anything this run did not verify green, MINUS
    // the zero matches (counted separately below). Split further
    // into genuine failures vs deferred (never-ran) so the verdict
    // can be honest: deferred is non-green but is "could not run /
    // waiting on build", NOT "failed".
    //
    // Written out per case, at the site. `not verifiedGreen`
    // would sweep the zero matches in here and report them as
    // failures; matching only `Refuted` would drop the
    // Deferred/Errored projects, which ARE non-green — they owed
    // a result and produced none.
    //
    // Per result, this match is the exact negation of
    // `noProjectRefutedOrUnrun` in `cacheKeyFor`, kept in agreement
    // BY HAND — see that site for why a shared helper would not
    // enforce it (the conditions around the two differ).
    let nonGreen =
        results.Results
        |> Map.toList
        |> List.filter (fun (_, r) ->
            match TestResult.verdict r with
            | Verified -> false
            | Refuted -> true
            | NothingVerified -> not (TestResult.isNoMatch r))

    let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

    // An ABORTED project — its test host died
    // mid-run — is counted OUT of `failed`, which used to sweep
    // it in (`not isDeferred`). Nothing failed there: the runner
    // never finished asking. Reporting it as "N failed: X" is a
    // non-result rendered as a definite negative, and it is the
    // exact shape a reader mistakes for a mass regression.
    let abortedList = nonGreen |> List.filter (fun (_, r) -> TestResult.isErrored r)

    let failedList =
        nonGreen
        |> List.filter (fun (_, r) -> not (TestResult.isDeferred r) && not (TestResult.isErrored r))

    let failed = failedList.Length
    let deferred = deferredList.Length
    let aborted = abortedList.Length

    // Zero-match projects are counted OUT of `passed`. `passed` is
    // derived by exclusion, and the `nonGreen` fold above
    // deliberately excludes them (a zero match is not a failure to
    // report), so without this subtraction a project that executed
    // no test is reported as one that passed — and the status line
    // then disagrees with the CLI, which counts them separately.
    let noMatch =
        results.Results |> Map.filter (fun _ r -> TestResult.isNoMatch r) |> Map.count

    let passed = total - failed - deferred - aborted - noMatch

    let noMatchSuffix = if noMatch = 0 then "" else $", %d{noMatch} matched nothing"

    let anyFiltered =
        results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

    let selectedSuffix = if anyFiltered then "yes" else "no"

    let timedOutProjects =
        failedList
        |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

    // When 2+ projects ran and at least one has recorded elapsed,
    // surface the slowest in the summary so a bottlenecked project
    // is visible without having to query test-results JSON.
    let slowestSuffix =
        if total < 2 then
            ""
        else
            let withElapsed =
                results.Results
                |> Map.toList
                |> List.choose (fun (name, r) ->
                    let e = TestResult.elapsed r
                    if e > TimeSpan.Zero then Some(name, e) else None)

            match withElapsed with
            | [] -> ""
            | _ ->
                let (n, e) = withElapsed |> List.maxBy snd
                $", slowest: %s{n} %.1f{e.TotalSeconds}s"

    let deferredSuffix =
        if deferred > 0 then
            $", %d{deferred} waiting on build"
        else
            ""

    // Never folded into the `failed` number it sits beside: the
    // summary is the one line most readers see, so it is the one
    // line that must not call a dead host a failure.
    let abortedSuffix =
        if aborted > 0 then
            $", %d{aborted} ABORTED (host died, nothing verified)"
        else
            ""

    let abortedNames = abortedList |> List.map fst |> String.concat ", "


    { DeferredList = deferredList
      FailedList = failedList
      Failed = failed
      Deferred = deferred
      Aborted = aborted
      Passed = passed
      NoMatchSuffix = noMatchSuffix
      SelectedSuffix = selectedSuffix
      TimedOutProjects = timedOutProjects
      SlowestSuffix = slowestSuffix
      DeferredSuffix = deferredSuffix
      AbortedSuffix = abortedSuffix
      AbortedNames = abortedNames }

/// Report the terminal status of a run that executed, from its tally: a timeout, or the
/// green, carried-red, owed, deferred, aborted or failed verdict.
let internal reportRanRun
    (ctx: PluginCtx<TestPruneMsg>)
    (facts: RunOutcomeFacts)
    (results: TestResults)
    (total: int)
    (carriedNote: string)
    (tally: RunTally)
    =
    let carriedFailures = facts.CarriedFailures
    let carriedCount = carriedFailures.Length
    let queueAfterCommit = facts.QueueAfterCommit
    let pendingDescription = facts.PendingDescription
    let receiptTransition = facts.ReceiptTransition
    let evidenceReceipt = facts.EvidenceReceipt
    let deferredList = tally.DeferredList
    let failedList = tally.FailedList
    let failed = tally.Failed
    let deferred = tally.Deferred
    let aborted = tally.Aborted
    let passed = tally.Passed
    let noMatchSuffix = tally.NoMatchSuffix
    let selectedSuffix = tally.SelectedSuffix
    let timedOutProjects = tally.TimedOutProjects
    let slowestSuffix = tally.SlowestSuffix
    let deferredSuffix = tally.DeferredSuffix
    let abortedSuffix = tally.AbortedSuffix
    let abortedNames = tally.AbortedNames

    if not timedOutProjects.IsEmpty then
        let names = timedOutProjects |> String.concat ", "
        // Flip the recorded outcome to TimedOut; the verdict on
        // the Failed below carries the summary (one channel).
        ctx.CompleteWithTimeout $"test project(s): {names}"

        ctx.ReportStatus(
            PluginStatus.failedNow
                $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                results.Elapsed
        )
    else
        let runSummary =
            $"%d{passed} passed, %d{failed} failed%s{abortedSuffix}%s{deferredSuffix}%s{noMatchSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

        // EVERY terminal below CARRIES the run's evidence —
        // `runSummary` + measured duration — on the status
        // itself. There is no separate summary channel left to
        // forget or contradict.
        if
            failed = 0
            && aborted = 0
            && deferred = 0
            && Set.isEmpty queueAfterCommit
            && carriedCount = 0
        then
            // Nothing failed, nothing is owed — and on a
            // run that EXECUTED NOTHING that is not a pass, it is an
            // absence of evidence. `runSummary` would report it as
            // "0 passed, 0 failed in 0 projects", which is how a plugin
            // line goes `✓` for a check that verified nothing while the
            // verdict layer is (correctly) refusing it exit 0.
            //
            // The STATUS stays `Completed`: nothing failed, and a
            // `Failed` here would claim one — turning `check`'s honest
            // exit 3 "NO VERDICT" into an exit 1 "failures found". It is
            // the VERDICT that carries the fact — the run record becomes
            // `RunOutcome.VerifiedNothing` — and every renderer keys its
            // glyph off that case (`ParsedPluginStatus.verifiedNothing`).
            //
            // Asked of `RunVerification`, THE derivation, so this is a
            // question about the RUN ("did anything execute?") and not
            // about one selection bug: any future path that lands an
            // executed-nothing run here is covered without a new arm.
            let verdict =
                match receiptTransition, evidenceReceipt with
                | ReceiptTransition.Noop, Some retained when
                    RunVerification.verifiedNothing (verificationOf results.Results)
                    ->
                    // Nothing needed running because an earlier run
                    // already verified this tree and model, and its
                    // receipt stands: that run is the evidence, so
                    // the line names it rather than the empty skip.
                    let retainedRun = retained.RunId.ToString("N")

                    RunVerdict.create
                        $"no test needed: nothing changed since run %s{retainedRun}, which verified this tree"
                        results.Elapsed
                | _ when RunVerification.verifiedNothing (verificationOf results.Results) ->
                    RunVerdict.verifiedNothing $"%d{total} test project(s) ran, no test executed" results.Elapsed
                | _ -> RunVerdict.create runSummary results.Elapsed

            ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))
        elif failed = 0 && aborted = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
            // Everything this run RAN passed, the
            // queue is drained — and yet an earlier failure it did
            // not execute is still outstanding. A narrower run
            // cannot vindicate a test it never ran, so this is
            // NON-green: the red survives until a run that COVERS
            // it passes (`dotnet fshw test-rerun` runs every
            // project unfiltered and will clear it if it is fixed).
            ctx.ReportStatus(
                PluginStatus.failedNow
                    $"%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures}"
                    $"%s{runSummary}%s{carriedNote}"
                    results.Elapsed
            )
        elif failed = 0 && aborted = 0 && deferred = 0 then
            // Everything that RAN passed, but the pending queue
            // still holds symbols this (e.g. filtered) run did not
            // cover green — NOT test-equivalent to a green run yet.
            // Non-green with the honest "waiting on build" wording;
            // the next BuildCompleted re-selects and runs them.
            ctx.ReportStatus(
                PluginStatus.failedNow
                    $"%s{pendingDescription ()}%s{carriedNote}"
                    $"%s{runSummary}%s{carriedNote}"
                    results.Elapsed
            )
        elif failed = 0 && aborted = 0 then
            // Only deferred projects — nothing FAILED, but
            // nothing was verified either. Non-green, honest
            // "waiting on build" (never "failed").
            let names = deferredList |> List.map fst |> String.concat ", "

            if carriedCount = 0 then
                // PURE defer: no red this run or carried. A
                // NON-failing terminal, so the verdict reads the
                // `Deferred`-severity ledger entry and routes it to
                // `Incomplete`/exit 2, not the exit 1 a red earns —
                // a build-ordering race left one project unrun.
                // A defer's `verdict` is `NothingVerified`, so its
                // symbols never commit (the next build re-runs them)
                // and the result is uncacheable.
                ctx.ReportStatus(
                    PluginStatus.completedNow
                        $"%s{runSummary} — %d{deferred} waiting on build (tests did not run): %s{names}"
                        results.Elapsed
                )
            else
                // A carried RED from an earlier run is still
                // outstanding — that dominates a defer. Stay
                // Failed/red (exit 1); the ledger's carried Error
                // entry independently keeps the verdict red.
                ctx.ReportStatus(
                    PluginStatus.failedNow
                        $"%d{deferred} waiting on build (tests did not run): %s{names}%s{carriedNote}"
                        $"%s{runSummary}%s{carriedNote}"
                        results.Elapsed
                )
        elif failed = 0 then
            // Nothing failed — a test HOST DIED.
            // Killed by a signal under load, or gone before it
            // could write a report. No test was shown to fail and
            // no test was shown to pass, so this is the third
            // answer and it must LOOK like the third answer: a
            // dead host reported as "N failed: X" sends the reader
            // hunting a regression that is not there, which is the
            // hours this arm exists to stop burning.
            //
            // A NON-failing terminal, exactly like the pure-defer
            // arm above: the `HostAborted`-severity ledger entries
            // route the verdict to `RunnerAborted`/exit 2 rather
            // than the exit 1 a red earns. The verdict is a
            // verified-nothing one (`RunOutcome.VerifiedNothing` on
            // the run record), so no renderer can give this run a
            // bare `✓` either.
            let deferredNote =
                if deferred > 0 then
                    let dn = deferredList |> List.map fst |> String.concat ", "
                    $" (+%d{deferred} waiting on build: %s{dn})"
                else
                    ""

            let abortLine =
                $"%d{aborted} test host(s) ABORTED — killed mid-run, nothing verified (NOT a test failure): %s{abortedNames}%s{deferredNote}"

            if carriedCount = 0 then
                ctx.ReportStatus(PluginStatus.verifiedNothingNow abortLine results.Elapsed)
            else
                // A carried RED from an earlier run outranks an
                // abort — same rule the defer arm applies, and for
                // the same reason: an aborted run cannot vindicate
                // a test it never ran. Stay red (exit 1); the
                // carried Error entry keeps the ledger red anyway.
                ctx.ReportStatus(
                    PluginStatus.failedNow
                        $"%s{abortLine}%s{carriedNote}"
                        $"%s{runSummary}%s{carriedNote}"
                        results.Elapsed
                )
        else
            let names = failedList |> List.map fst |> String.concat ", "

            let deferredNote =
                if deferred > 0 then
                    let dn = deferredList |> List.map fst |> String.concat ", "
                    $" (%d{deferred} waiting on build: %s{dn})"
                else
                    ""

            // A real red DOMINATES an abort beside it — but the
            // abort is still NAMED, and named as an abort: those
            // projects proved nothing, and a reader must not add
            // them to the failure count.
            let abortNote =
                if aborted > 0 then
                    $" (+%d{aborted} ABORTED, nothing verified: %s{abortedNames})"
                else
                    ""

            ctx.ReportStatus(
                PluginStatus.failedNow
                    $"%d{failed} failed: %s{names}%s{abortNote}%s{deferredNote}%s{carriedNote}"
                    $"%s{runSummary}%s{carriedNote}"
                    results.Elapsed
            )

/// Report a completed run's terminal status. Pushing a terminal Completed/Failed status is
/// what appends the run to history; both rerun and final-idle branches must call this.
let internal reportRunOutcome (ctx: PluginCtx<TestPruneMsg>) (facts: RunOutcomeFacts) (results: TestResults) =
    let completed = facts.Completed
    let carriedFailures = facts.CarriedFailures
    let queueAfterCommit = facts.QueueAfterCommit
    let pendingDescription = facts.PendingDescription

    let total = results.Results.Count

    // Reds this run did not cover are still RED —
    // they must deny it a green verdict exactly as its own failures
    // would, or `check` exits 0 with a failing test outstanding
    // (the whole defect). Named in every non-green message below so
    // the reason a passing run is not green is never a mystery.
    let carriedCount = carriedFailures.Length

    let carriedNote =
        if carriedCount = 0 then
            ""
        else
            $" (+%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures})"

    // Consult `completed.Outcome` FIRST. An Aborted run (beforeRun
    // threw, runner crashed, run cancelled) must be non-green
    // regardless of result counts — empty results trivially satisfy
    // "failed = 0 && deferred = 0" and would otherwise false-green.
    // Likewise a run that executed ZERO projects while the pending
    // queue still holds symbols verified nothing, so it takes the
    // honest "waiting on build (tests did not run)" path.
    let abortMessage =
        match completed.Outcome with
        | Aborted reason -> Some reason
        | Normal -> None

    match abortMessage with
    | Some reason ->
        ctx.ReportStatus(
            PluginStatus.failedNow
                $"test run aborted (tests did not run): %s{reason}%s{carriedNote}"
                $"test run aborted: %s{reason}%s{carriedNote}"
                results.Elapsed
        )
    | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
        // Zero projects executed but symbols still await
        // verification — honest non-green, same wording/path as a
        // deferred (never-ran) project.
        ctx.ReportStatus(
            PluginStatus.failedNow
                $"%s{pendingDescription ()}%s{carriedNote}"
                $"0 projects ran; symbols still awaiting verification%s{carriedNote}"
                results.Elapsed
        )
    // Projects RAN and every one matched zero tests, so
    // nothing executed and nothing was verified — not a green. The
    // ladder below counts zero matches OUT of both `passed` and
    // `failed`, so without this check `failed = 0 && deferred = 0`
    // holds, the green branch fires, and it reports "0 passed, 0
    // failed in N projects" about N projects that ran no test at all.
    //
    // Scoped to ALL projects deliberately: a zero match in ONE project
    // is a correct pass for it (an impact selection naming no class in
    // the Integration project must not fail that project). Only the
    // run-level verdict changes, and only when nothing matched
    // anywhere — a mis-aimed filter, never a verified pass. An
    // empty-results run stays on its existing "nothing to verify" path
    // because `Map.forall` is vacuously true for an empty map.
    | None when allZeroMatchOf results.Results ->
        ctx.ReportStatus(
            PluginStatus.failedNow
                $"%d{total} project(s) ran and matched ZERO tests — nothing was verified (not a pass)%s{carriedNote}"
                $"%d{total} project(s) discovered their tests; the active filter matched none of them, so no test executed%s{carriedNote}"
                results.Elapsed
        )
    | None -> reportRanRun ctx facts results total carriedNote (tallyRun results total)

/// What a completion's earned evidence is decided from (`earnedEvidenceOf`).
type internal CompletionEvidenceFacts =
    { State: TestPruneState
      Started: TestRunStarted
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      Debt: VerificationDebt
      RunnableProjects: Set<string>
      Aborted: bool
      ExecutedFullSuite: bool
      ProjectPassed: string -> bool
      Unanalyzable: Map<string, UnanalyzableFile>
      OutstandingFailures: OutstandingFailure list
      CurrentInputTree: string option
      CurrentInputs: Map<string, string> option
      CurrentModelGeneration: int64 option
      ReceiptTransition: ReceiptTransition
      EvidenceReceipt: TestEvidenceReceipt option
      StandingEvidence: EarnedEvidence option }

/// The evidence a completion earns, or keeps from the standing receipt, and the log line
/// that describes it.
let internal earnedEvidenceOf (ctx: PluginCtx<TestPruneMsg>) (facts: CompletionEvidenceFacts) : EarnedEvidence option =
    let state = facts.State
    let started = facts.Started
    let completed = facts.Completed
    let launch = facts.Launch
    let debt = facts.Debt
    let runnableProjects = facts.RunnableProjects
    let aborted = facts.Aborted
    let executedFullSuite = facts.ExecutedFullSuite
    let projectPassed = facts.ProjectPassed
    let unanalyzable = facts.Unanalyzable
    let outstandingFailures = facts.OutstandingFailures
    let currentInputTree = facts.CurrentInputTree
    let currentInputs = facts.CurrentInputs
    let currentModelGeneration = facts.CurrentModelGeneration
    let receiptTransition = facts.ReceiptTransition
    let evidenceReceipt = facts.EvidenceReceipt
    let standingEvidence = facts.StandingEvidence

    // What this completion EARNED. What it left OWED is a refusal it
    // carries: a proof that records a red is still a proof of what ran.
    //
    // Owed is what THIS run did not cover, not everything the queue holds
    // at this instant. A cold tree queues obligations while the run is
    // already executing — the build fires the full suite first and the
    // scan's file events land during it — so they are absent from the
    // launch snapshot and survive the launch-scoped retirement above. A run
    // that executed every runnable project IN FULL and passed, over the
    // tree it launched against, covered those files whenever they were
    // queued; refusing them turned a green check into exit 2 with nothing
    // failing anywhere. A run that did not (a project absent, failed, or
    // filtered) covers nothing extra, and every one of them still refuses.
    let coveredEverythingRunnable =
        executedFullSuite
        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
        && runnableProjects |> Set.forall projectPassed

    let owedSymbols =
        if coveredEverythingRunnable then
            Set.empty
        else
            debt.PendingQueue

    let owedRuntimeObligations =
        if coveredEverythingRunnable then
            0
        else
            debt.RuntimeObligations |> Map.values |> Seq.sumBy Map.count

    let pendingObligations =
        Set.count owedSymbols
        + owedRuntimeObligations
        + unanalyzable.Count
        + outstandingFailures.Length
        + Map.count debt.CoverageNotIngested
        + (if debt.RecoveryOutstanding then 1 else 0)

    // A completion that leaves the receipt standing — a narrower run, or a
    // quiet one that found the tree already verified — leaves the evidence
    // THAT receipt's run earned standing with it: the verdict grades the
    // receipt's run and looks its evidence up by run id. Under a model,
    // `classify` keeps a receipt only when this evidence is held.
    //
    // Including a quiet completion that folds while an obligation is owed
    // (symbols a scan delivered while it was in flight). It used to publish
    // an evidence of its own, refusing for the obligation, beside the
    // receipt it kept: the two named different runs, and the narrower run
    // that then discharged the obligation found no evidence for the
    // receipt's run to keep and published its own, so `check` graded the
    // full suite and found nothing vouching for it.
    //
    // A receipt that covers the whole suite on the tree it is bound to has
    // run every test that tree has, so an obligation owed on that same tree
    // is one it already verified, as it is after a narrower run. A
    // narrower receipt cannot say so: its evidence refuses for what is owed.
    let keptWithReceipt =
        match receiptTransition, evidenceReceipt with
        | ReceiptTransition.Noop, Some receipt when
            pendingObligations > 0
            && not (RunCoverage.coversWholeSuite (Set.toList runnableProjects) receipt.Coverage)
            ->
            standingEvidence
            |> Option.map (
                EarnedEvidence.withRefusals [ $"%d{pendingObligations} verification obligation(s) remain pending" ]
            )
        | ReceiptTransition.Noop, _
        | ReceiptTransition.Narrower, _ -> standingEvidence
        | ReceiptTransition.Earned _, _
        | ReceiptTransition.Revoked _, _ -> None

    let earned =
        match keptWithReceipt, completed.Verification with
        | Some kept, _ -> Some kept
        | None, NoProjectsSelected when
            pendingObligations = 0
            && not aborted
            && launch.ZeroSelection <> ZeroSelection.NotAZero
            && launch.ModelGeneration = currentModelGeneration
            ->
            // Nothing needed running, so this completion proves nothing new.
            // The evidence it was already verified by stands, while it belongs
            // to the current model.
            EarnedEvidence.retainedForZeroSelection currentModelGeneration currentInputs state.Earned
        | None, NoProjectsSelected
        | None, AllZeroMatch _
        | None, NothingExecuted
        | None, Ran _ ->
            EarnedEvidence.fromCompletion
                started.RunId
                launch.ModelGeneration
                currentModelGeneration
                currentInputs
                (ObservedModel.projectFiles ctx.ProjectGraph)
                runnableProjects
                pendingObligations
                (TestPruneState.coverageEvidence state)
                completed

    Logging.info
        "test-prune"
        (describeCompletionEvidence
            completed.RunId
            launch.ModelGeneration
            currentModelGeneration
            receiptTransition
            evidenceReceipt
            earned)

    match receiptEvidenceMismatch currentModelGeneration evidenceReceipt earned with
    | Some mismatch -> Logging.warn "test-prune" mismatch
    | None -> ()

    earned

/// What a completion's results and failures are assembled from.
type internal CompletionRunFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      RepoRoot: string
      RunnableProjects: Set<string>
      Traces: TraceWiring option }

/// A completed run's results and the failures it leaves outstanding (`assembleRunResults`).
type internal AssembledRunResults =
    { CompletedRuns: Guid list
      TestResults: TestResults
      Coverage: RunCoverage
      CheckReach: CheckReach
      ConditionalFailureRecall: FailureRecall
      CarriedFailures: OutstandingFailure list
      OutstandingFailures: OutstandingFailure list }

/// The first stage of a run's completion: its results, coverage and check reach, the
/// shadow trace comparison, and the failures it found, retired and carried.
let internal assembleRunResults (facts: CompletionRunFacts) : AssembledRunResults =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let repoRoot = facts.RepoRoot
    let runnableProjects = facts.RunnableProjects
    let traces = facts.Traces

    // This run joins the session ledger HERE, so every state this fold
    // returns carries it. A run that completed is a run whose directory a
    // reader may need, whatever the handler goes on to decide about its
    // results.
    //
    // Except a skip: a launch that selected nothing and ran nothing wrote
    // no directory, and counting it tells a reader the tests ran twice.
    let skipped =
        launch.ZeroSelection <> ZeroSelection.NotAZero && Map.isEmpty completed.Results

    let completedRuns =
        if skipped then
            state.CompletedRuns
        else
            completed.RunId
            :: (state.CompletedRuns
                |> List.filter (fun id -> id <> completed.RunId)
                |> List.truncate (SessionRunLedger - 1))

    // Apply error reporting synchronously here too — live emission from
    // the async wouldn't be captured for cache replay.
    let testResults: TestResults =
        { Results = completed.Results
          Elapsed = completed.TotalElapsed }

    // A run may clear ONLY what it COVERED.
    //
    // Two properties must hold together: the ledger is REWRITTEN each
    // cycle (so a superseded red cannot linger), and it is
    // rewritten from the OUTSTANDING set — what this run found PLUS every
    // earlier red it did not cover. Rewriting from this run's failures
    // alone lets a narrower run's green erase reds it never executed.
    // Coverage comes from the run's own launch selection, so a red dies
    // only to evidence that executed it, and dies the moment that evidence
    // exists (no stuck-red).
    //
    // The launch selection cannot express the reach of a raw `--filter`
    // passthrough — it records every project as `ProjectInFull` — so hand
    // `ofRun` the classes the run's OWN report shows passing.
    // Read from THIS run's directory only, and empty
    // whenever the report is missing or incomplete.
    let coverage =
        RunCoverage.ofRun launch.Selection completed.Results (passedClassesOfRun repoRoot completed.RunId)

    let passedTests = passedTestsOfRun repoRoot completed.RunId

    // The run log a project-level cause points at. Named
    // only when it is on disk: `Written` is otherwise reserved for the
    // code holding the open handle, and a message must never point at
    // a log nobody wrote.
    let runLogOf (project: string) : RunLog.Ref =
        let path = RunLog.pathIn (Ctrf.runDir repoRoot completed.RunId) project

        if File.Exists path then
            RunLog.Ref.Written path
        else
            RunLog.Ref.Unavailable "no output log is on disk for this run"

    // The failed rows and declared classes of the CTRF report each
    // project wrote in this run's directory, for naming a red the console
    // line did not and for splitting every red's name.
    let reportOf =
        let reports =
            Ctrf.reportsForRun repoRoot completed.RunId
            |> List.map (fun report -> report.Project, report.Path)
            |> Map.ofList

        fun (project: string) ->
            match Map.tryFind project reports with
            | None -> ReportedFailures.none
            | Some path ->
                try
                    ReportedFailures.ofReport (File.ReadAllText path)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> ReportedFailures.none

    let foundFailures = failuresOf runLogOf reportOf state.TestClassFiles testResults

    let failedTests = failedTestsOfRun repoRoot completed.RunId testResults

    let checkReach, conditionalFailureRecall =
        failedTests |> CheckReach.classifyEvidence launch.WouldHaveRun

    // Shadow trace selection: store the launch's comparison with this
    // run's failed tests. A run with no complete report judges no miss.
    match traces with
    | Some wiring when not launch.TraceShadow.IsEmpty ->
        let failedKeys =
            match failedTests with
            | Ok failures ->
                failures
                |> List.choose (fun f ->
                    match f.Class, f.Method with
                    | Some cls, Some meth -> Some(TraceShadow.failureKey f.Project cls meth)
                    | _ -> None)
            | Error _ -> []

        TraceShadow.atCompletion
            wiring.Policy
            repoRoot
            launch.TraceShadow
            (completed.RunId.ToString "N")
            failedKeys
            (Logging.info "test-prune")
    | _ -> ()

    // Classified HERE, against THIS run's failures and the
    // selection retained at its launch. Nothing extra runs and nothing is
    // re-read: both inputs are already in hand.

    // A prior red naming a class this run selected, in a project that then
    // passed with a complete report holding no row of that class, names no
    // test the project has. No pass can ever retire it, so it goes now.
    let absentFailures, priorFailures =
        OutstandingFailure.partitionAbsent
            coverage
            (classesRunOfPassedProjects repoRoot completed.RunId completed.Results)
            state.OutstandingFailures

    if not absentFailures.IsEmpty then
        Logging.info
            "test-prune"
            $"%d{absentFailures.Length} failure(s) from an earlier run name a test class this run selected and its project does not have (%s{OutstandingFailure.summarize absentFailures}) — retired, since no run can ever execute them"

    let carriedFailures =
        OutstandingFailure.carriedOver runnableProjects coverage passedTests priorFailures

    let outstandingFailures =
        OutstandingFailure.carry runnableProjects coverage passedTests foundFailures priorFailures

    if not carriedFailures.IsEmpty then
        Logging.info
            "test-prune"
            $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

    { CompletedRuns = completedRuns
      TestResults = testResults
      Coverage = coverage
      CheckReach = checkReach
      ConditionalFailureRecall = conditionalFailureRecall
      CarriedFailures = carriedFailures
      OutstandingFailures = outstandingFailures }

/// What a completion's receipt transition is decided from.
type internal ReceiptFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      Coverage: RunCoverage
      RepoRoot: string
      RunnableProjects: Set<string>
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option }

/// The tree and model a completion is judged against, and what it does to the receipt.
type internal ReceiptOutcome =
    { CurrentTree: ReceiptTree option
      CurrentInputTree: string option
      CurrentModelGeneration: int64 option
      CurrentInputs: Map<string, string> option
      StandingEvidence: EarnedEvidence option
      ReceiptTransition: ReceiptTransition
      EvidenceReceipt: TestEvidenceReceipt option }

/// Read the tree and model as they stand now, classify what the completion does to the
/// test-evidence receipt, log a revocation and the paths that moved, and apply it.
let internal decideReceipt (ctx: PluginCtx<TestPruneMsg>) (facts: ReceiptFacts) : ReceiptOutcome =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let coverage = facts.Coverage
    let repoRoot = facts.RepoRoot
    let runnableProjects = facts.RunnableProjects
    let observeModelGeneration = facts.ObserveModelGeneration

    let currentTree = ReceiptInputTree.readTree repoRoot
    let currentInputTree = currentTree |> Option.map _.Identity
    let currentModelGeneration = observeModelGeneration ctx
    let currentInputs = ObservedModel.inputs ctx.ProjectGraph

    // A typed transition, not a candidate-then-guard: see
    // `ReceiptTransition`. `Noop` and `Narrower` keep whatever was earned,
    // `Revoked` clears it, and only `Earned` can write.
    let writtenByTestRun =
        ReceiptInputTree.writtenByTestRun
            repoRoot
            (ReceiptInputTree.watcherObserves repoRoot (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd))
            (state.InFlight |> Option.map (fun run -> run.WatcherReported))
            launch.TestHostStartedAt
            launch.InputTree
            currentTree

    // The evidence the standing receipt's own run earned, under the
    // current model. A completion that keeps the receipt (`Noop`,
    // `Narrower`) publishes exactly this, so the run `test-scope` grades
    // and the run the published evidence names are one run.
    let standingEvidence =
        state.EvidenceReceipt
        |> Option.bind (fun receipt ->
            EarnedEvidence.retainedForRun currentModelGeneration currentInputs receipt.RunId state.Earned)

    let receiptTransition =
        ReceiptTransition.classify
            (Set.toList runnableProjects)
            state.EvidenceReceipt
            standingEvidence
            currentInputTree
            currentModelGeneration
            currentInputs
            writtenByTestRun
            launch
            completed
            coverage

    match receiptTransition with
    | ReceiptTransition.Revoked reason ->
        ctx.Log $"  ↳ test evidence receipt revoked: %s{reason}"

        // WHICH paths, so a revocation with no watcher event behind it
        // (a gitignored build output under a discovery root, say) is
        // answered by this line rather than by an afternoon of ctimes.
        match ReceiptInputTree.movedPaths launch.InputTree currentTree with
        | Some paths -> ctx.Log $"    moved paths: %s{paths}"
        | None -> ()
    | ReceiptTransition.Earned _
    | ReceiptTransition.Noop
    | ReceiptTransition.Narrower -> ()

    let evidenceReceipt =
        ReceiptTransition.apply state.EvidenceReceipt receiptTransition

    { CurrentTree = currentTree
      CurrentInputTree = currentInputTree
      CurrentModelGeneration = currentModelGeneration
      CurrentInputs = currentInputs
      StandingEvidence = standingEvidence
      ReceiptTransition = receiptTransition
      EvidenceReceipt = evidenceReceipt }

/// The state a completion leaves before its debt is settled: the session ledgers, the
/// failures and coverage it assembled, the receipt it holds, the red it stands on, and the
/// mode `endRun` leaves.
let internal recordCompletion
    (endRun: bool -> TestPruneState -> TestPruneState)
    (state: TestPruneState)
    (completed: TestRunCompleted)
    (launch: TestRunLaunch)
    (assembled: AssembledRunResults)
    (receipt: ReceiptOutcome)
    (unanalyzable: Map<string, UnanalyzableFile>)
    : TestPruneState =
    let completedRuns = assembled.CompletedRuns
    let checkReach = assembled.CheckReach
    let conditionalFailureRecall = assembled.ConditionalFailureRecall
    let outstandingFailures = assembled.OutstandingFailures
    let coverage = assembled.Coverage
    let currentTree = receipt.CurrentTree
    let currentInputTree = receipt.CurrentInputTree
    let currentModelGeneration = receipt.CurrentModelGeneration
    let receiptTransition = receipt.ReceiptTransition

    let state =
        { state with
            CompletedRuns = completedRuns
            CheckReaches =
                (completed.RunId, launch.WouldHaveRun, checkReach, conditionalFailureRecall)
                :: (state.CheckReaches
                    |> List.filter (fun (id, _, _, _) -> id <> completed.RunId)
                    |> List.truncate (SessionRunLedger - 1))
            OutstandingFailures = outstandingFailures
            LastCoverage = coverage
            LastZeroSelection = launch.ZeroSelection
            Receipt = ReceiptTransition.hold state.Receipt receiptTransition currentTree
            // Debt is scoped to exactly the run that was active when the
            // BootScan cohort sealed. Failure keeps it durable, but must not
            // let a later unrelated run claim it implicitly.
            DebtDuringFullRun = Map.empty
            InFlight = None
            // A conclusion only when the run executed to a red over the
            // tree and model it launched under (`onlyRetriesRed`).
            LastRed =
                match completed.Outcome, launch.InputTreeHash, currentInputTree with
                | Normal, Some launched, Some current when
                    launched = current
                    && launch.ModelGeneration = currentModelGeneration
                    && completed.Results
                       |> Map.exists (fun _ result -> TestResult.verdict result = Refuted)
                    ->
                    Some
                        { SymbolRevisions = launch.SymbolRevisions
                          Tree = current }
                | _ -> None
            Mode =
                (endRun
                    (completed.Results
                     |> Map.exists (fun _ result -> TestResult.executedTests result))
                    state)
                    .Mode
            // Carried with them: the pruned map is what the ledger was
            // just written from, so the next run's coarse-fallback
            // widening reads the same set the user was shown.
            UnanalyzableFiles = unanalyzable }

    state

/// What a completion's debt is settled from.
type internal DebtSettlementFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      RunnableProjects: Set<string>
      OutstandingFailures: OutstandingFailure list
      CurrentInputTree: string option
      CurrentModelGeneration: int64 option
      DebtDuringFullRun: Map<string, int64>
      OwedTo: string -> Set<string>
      NoUnrunnableCoverers: Lazy<bool>
      Debt: CompletionDebt }

/// What a completion settled: the debt it leaves, and the facts about the run the rest of
/// the fold reads.
type internal DebtSettlement =
    { Aborted: bool
      ProjectPassed: string -> bool
      CoveredByConstruction: bool
      Debt: VerificationDebt
      AbsorbedProjects: Set<string>
      ExecutedFullSuite: bool
      EarnsBaseline: bool }

/// Settle what a completion owes: commit the symbols it covered green, retire and absorb
/// runtime obligations and fanout, record ingested coverage, recover unknown debt, and
/// decide whether it earns a full-suite baseline.
let internal settleDebt (facts: DebtSettlementFacts) : DebtSettlement =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let runnableProjects = facts.RunnableProjects
    let outstandingFailures = facts.OutstandingFailures
    let currentInputTree = facts.CurrentInputTree
    let currentModelGeneration = facts.CurrentModelGeneration
    let debtDuringFullRun = facts.DebtDuringFullRun
    let owedTo = facts.OwedTo
    let noUnrunnableCoverers = facts.NoUnrunnableCoverers
    let commitPending = facts.Debt.CommitPending
    let revisionOf = facts.Debt.RevisionOf

    // Outcome-conditional, per-project green-commit. A launched
    // symbol leaves the needs-testing queue ONLY when the run
    // genuinely covered it green:
    //   - the run did NOT abort (a beforeRun throw / crash gives
    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
    //   - EVERY project covering the symbol produced a PASSED result.
    // A project counts as passed only if it appears in completed.Results
    // with `TestResult.verifiedGreen` — a covering project ABSENT from the
    // results (didn't run this cycle) blocks the commit (we can't claim
    // it green). A symbol with NO covering project was already dropped at
    // flush time, but if one slipped through it commits here (nothing to
    // wait on). Genuine in-session mid-run arrivals are NOT in
    // launch.Symbols, and a launched symbol edited again during the run is
    // at a newer revision than the launch captured: both stay queued and
    // the queued impact run re-runs them. BootScan debt may join the candidate set only when
    // this completion proves the run was actually full-suite.
    //
    // This fold read `TestResult.isPassed`, which was TRUE
    // for `TestsNoMatch` — so a symbol whose covering project ran under an
    // impact-derived class filter that matched ZERO tests had its test debt
    // DISCHARGED by a project that executed nothing, and left
    // pending-verification.json unverified. `verifiedGreen` is `Verified`
    // only, so a project that ran nothing can no longer retire anything.
    // A run selected under a model this host has since replaced verified
    // that model, not the current one: it discharges nothing, like an abort.
    let aborted =
        launch.ModelGeneration <> currentModelGeneration
        || match completed.Outcome with
           | Aborted _ -> true
           | Normal -> false

    let projectPassed (proj: string) =
        match Map.tryFind proj completed.Results with
        | Some r -> TestResult.verifiedGreen r
        | None -> false

    // A run that executed every runnable project in full, over the input tree
    // it launched against, and passed all of them, covered every symbol that
    // tree holds: there is nothing left to ask per symbol, unless the index
    // names a coverer this daemon neither runs nor declares excluded.
    let coveredByConstruction =
        not aborted
        && not (Set.isEmpty runnableProjects)
        && completed.Verification = Ran FullSuite
        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
        && runnableProjects |> Set.forall projectPassed
        && noUnrunnableCoverers.Value

    let committedSymbols =
        if aborted then
            Set.empty
        else
            // An attached symbol may borrow this run only when the run was
            // actually full, over the input tree it launched against, and the
            // symbol is still at the revision captured when it attached.
            let attachedCandidates =
                match completed.Verification with
                | Ran FullSuite when ReceiptInputTree.matches launch.InputTreeHash currentInputTree ->
                    debtDuringFullRun
                    |> Map.filter (fun symbol captured -> revisionOf state.Debt symbol = captured)
                    |> Map.keys
                    |> Set.ofSeq
                | _ -> Set.empty

            let launchedCurrent =
                launch.Symbols
                |> Set.filter (fun symbol ->
                    revisionOf state.Debt symbol = (Map.tryFind symbol launch.SymbolRevisions |> Option.defaultValue 0L))

            let candidates = Set.union launchedCurrent attachedCandidates

            if coveredByConstruction then
                candidates
            else
                candidates
                |> Set.filter (fun s ->
                    match Map.tryFind s launch.CoveringProjectsBySymbol with
                    | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                    | Some _ -> true
                    | None -> owedTo s |> Set.forall projectPassed)

    if not (Set.isEmpty committedSymbols) then
        Logging.info
            "test-prune"
            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

    let debt = commitPending committedSymbols state.Debt

    if not aborted && not (Map.isEmpty launch.RuntimeProjectsByFile) then
        for KeyValue(file, launchedProjects) in launch.RuntimeProjectsByFile do
            let passed = launchedProjects |> Map.keys |> Seq.filter projectPassed |> Set.ofSeq

            if not (Set.isEmpty passed) then
                let projectNames = passed |> Set.toList |> String.concat ", "

                Logging.info "test-prune" $"runtime coverage verified %s{file} by %s{projectNames}"

    // A run covered by construction discharges every obligation owed now,
    // not only those it launched with: what attached to it arrived over the
    // tree it ran.
    let retiredRuntime =
        if coveredByConstruction then
            debt.RuntimeObligations
        else
            launch.RuntimeProjectsByFile

    let debt =
        if aborted || Map.isEmpty retiredRuntime then
            debt
        else
            { debt with
                RuntimeObligations =
                    retireRuntimeCoverageObligations debt.RuntimeObligations retiredRuntime projectPassed }

    // A project this run executed UNFILTERED and passed, over the input
    // tree it launched against, ran every test a trigger raised for it
    // during the run could ask for: a scan re-deriving its runtime
    // obligation (a newer generation over the same bytes) or a dependency
    // fanout from a build of that tree. Those triggers are absorbed here,
    // per project, as `coveredByConstruction` absorbs them for the whole
    // suite; otherwise the queued re-run executes the project in full
    // again. A filtered or reused project, a red, an abort, or a tree that
    // moved since launch absorbs nothing.
    let absorbedProjects =
        if aborted || not (ReceiptInputTree.matches launch.InputTreeHash currentInputTree) then
            Set.empty
        else
            launch.Selection
            |> Map.filter (fun project selection ->
                selection = ProjectInFull
                && not (Map.containsKey project launch.Reused)
                && projectPassed project)
            |> Map.keys
            |> Set.ofSeq

    let debt =
        if Set.isEmpty absorbedProjects then
            debt
        else
            { debt with
                RuntimeObligations =
                    debt.RuntimeObligations
                    |> Map.map (fun _ projects ->
                        projects
                        |> Map.filter (fun project _ -> not (Set.contains project absorbedProjects)))
                    |> Map.filter (fun _ projects -> not (Map.isEmpty projects)) }

    // A project whose runtime coverage did not ingest is paid by a run that executed
    // it in full and passed: a pass means its coverage ingested this time (a run
    // that could not ingest it reports the project errored, never passed). A
    // filtered or reused result, a red or an abort pays nothing.
    let ingested =
        if aborted then
            Set.empty
        else
            debt.CoverageNotIngested
            |> Map.keys
            |> Seq.filter (fun project ->
                Map.tryFind project launch.Selection = Some ProjectInFull
                && not (Map.containsKey project launch.Reused)
                && projectPassed project)
            |> Set.ofSeq

    if not (Set.isEmpty ingested) then
        let names = ingested |> Set.toList |> String.concat ", "

        Logging.info
            "test-prune"
            $"Runtime coverage ingested again for %s{names} after a whole-project run passed; no longer owed"

    let debt =
        if Set.isEmpty ingested then
            debt
        else
            { debt with
                CoverageNotIngested =
                    debt.CoverageNotIngested
                    |> Map.filter (fun project _ -> not (Set.contains project ingested)) }

    // Discharge an UNREADABLE ledger's debt.
    //
    // The debt is owed in FULL, because its membership is unknown: the only
    // run that can retire it is one that executed EVERY runnable project,
    // unfiltered, and passed. At that point every symbol the lost ledger
    // could possibly have held has been verified by an actual test run, so
    // there is nothing left for it to owe.
    //
    // Each conjunct is load-bearing:
    //  * `not aborted` — an aborted run has empty Results and verified nothing.
    //  * every RUNNABLE project passed — `projectPassed` demands the project
    //    be PRESENT in the results AND green, so a project that never ran
    //    cannot be counted.
    //  * `Ran FullSuite` — the run EXECUTED and none of it was
    //    impact-FILTERED. A case, not a bool: scope is unreachable unless
    //    something ran, so emptiness needs no separate check.
    //  * a non-empty `runnableProjects` — an analysis-only daemon runs no
    //    tests, so it can never prove anything and must not discharge. It
    //    asks about the SELECTION, not the results.
    //
    // Only a state that has discharged it rewrites the ledger: `PrepareCommit`
    // leaves the corrupt file untouched while the debt is unknown, so that a
    // crash mid-recovery leaves the next session the same honest "unknown"
    // rather than a clean, empty, WRONG ledger.
    let executedFullSuite =
        not aborted
        && not (Set.isEmpty runnableProjects)
        && completed.Verification = Ran FullSuite

    let recovers =
        debt.RecoveryOutstanding
        && executedFullSuite
        && runnableProjects |> Set.forall projectPassed

    // Obligations this run launched were retired above; any left are newer
    // than the run and stay owed.
    if recovers then
        let causes = describeUnknownDebt debt.UnknownDebt

        Logging.info
            "test-prune"
            $"A full suite passed every configured project — the unknown verification debt is discharged (%s{causes}), and the ledgers are rewritten from this run. Impact filtering resumes."

    let debt = if recovers then { debt with UnknownDebt = [] } else debt

    // The full-suite WATERMARK. Written when a full-suite
    // run has ACCOUNTED for every configured project: passed, or red with
    // the red recorded in the durable outstanding list. Not only for a
    // green run — a red full suite still proves what every other test did,
    // and its reds are quarantined until they pass — so the inner loop can
    // resume impact filtering (plus quarantine) after one full run rather
    // than running the whole suite until the last red is fixed. A project
    // that produced no accountable outcome (deferred, errored) is neither,
    // and `Ran FullSuite` is already false for it.
    //
    // The same `executedFullSuite` the unreadable-ledger discharge reads: two
    // readings of what a full-suite run is would let one recover a ledger
    // the other refused to baseline.
    let accountedFor (proj: string) =
        projectPassed proj
        || outstandingFailures |> List.exists (fun f -> f.Project = proj)

    // A run that reused evidence ran less than the suite here; its greens
    // stand, but a full-suite baseline is only ever earned by running.
    let earnsBaseline =
        executedFullSuite
        && Map.isEmpty launch.Reused
        && runnableProjects |> Set.forall accountedFor

    let debt =
        if earnsBaseline then
            { debt with
                Baseline =
                    Some
                        { RunId = completed.RunId
                          EarnedAt = DateTime.UtcNow
                          Projects = runnableProjects } }
        else
            debt

    { Aborted = aborted
      ProjectPassed = projectPassed
      CoveredByConstruction = coveredByConstruction
      Debt = debt
      AbsorbedProjects = absorbedProjects
      ExecutedFullSuite = executedFullSuite
      EarnsBaseline = earnsBaseline }

/// What `onRunFinishedWith` reads from the plugin. The declared exclusions are resolved
/// when the handler calls `ResolveExcludedProjects`, as before.
type internal RunFinishedEnv =
    { RepoRoot: string
      RunnableProjects: Set<string>
      Traces: TraceWiring option
      ResolveExcludedProjects: unit -> Map<string, string>
      Debt: CompletionDebt
      EndRun: bool -> TestPruneState -> TestPruneState
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option }

/// A run's completion (`TestsFinished`, or a requested run's `CommandTestsFinished`), folded into state.
let internal onRunFinishedWith
    (env: RunFinishedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (message: TestPruneMsg)
    (started: TestRunStarted)
    (launchedCompletion: TestRunCompleted)
    (launchedAs: TestRunLaunch)
    =
    let repoRoot = env.RepoRoot
    let runnableProjects = env.RunnableProjects
    let traces = env.Traces
    let resolveExcludedProjects = env.ResolveExcludedProjects
    let coveringOf = env.Debt.CoveringOf
    let debtScope = env.Debt.DebtScope
    let indexNamesOnlyRunnable = env.Debt.IndexNamesOnlyRunnable
    let owedElsewhere = env.Debt.OwedElsewhere
    let endRun = env.EndRun
    let observeModelGeneration = env.ObserveModelGeneration

    async {
        // A completion that lands while the model is being re-discovered has
        // nothing to be compared against yet: wait for the re-discovery to
        // settle, so it is carried or revoked per project instead of whole.
        let! waited = ObservedModel.awaitSettled ctx.ProjectGraph launchedAs.ModelGeneration

        if waited > TimeSpan.Zero then
            let waitedRun = launchedCompletion.RunId.ToString("N")

            Logging.info
                "test-prune"
                $"Run %s{waitedRun} completed while the project model was being re-discovered; waited %.1f{waited.TotalSeconds}s for it to settle before folding"

        // A model replaced while the run was in flight takes back only the
        // results of the projects whose compile inputs it changed.
        let modelCarry, completed, launch =
            acrossModelChange
                (observeModelGeneration ctx)
                (ObservedModel.inputs ctx.ProjectGraph)
                launchedCompletion
                launchedAs

        let runText = launchedCompletion.RunId.ToString("N")
        let launchedText = ObservedModel.describe launchedAs.ModelGeneration
        let nowText = ObservedModel.describe (observeModelGeneration ctx)

        match modelCarry with
        | ModelCarry.SameModel -> ()
        | ModelCarry.Carried ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, but no test project's compile inputs changed — its results stand"
        | ModelCarry.Revoked changed ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran and changed the compile inputs of %s{describeAll (Set.toList changed)} — their results are revoked; the others stand"
        | ModelCarry.Unprovable ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, and no per-project compile-input identities can show what changed — the whole run is revoked"

        // The declarations this completion retires debt under, resolved ONCE and
        // BEFORE any side effect. A resolution that throws (an ambiguous or
        // unobserved excluded project) fails this handler before it has emitted,
        // recorded or committed anything, so the framework settles the work as a
        // failure and every symbol stays owed.
        // Every symbol this completion can ask about: what the run launched,
        // what was attached to it, and what is queued. One grouped query,
        // and only if something asks.
        let covering =
            coveringOf (
                Seq.concat
                    [ Set.toSeq launch.Symbols
                      Map.keys state.DebtDuringFullRun
                      Set.toSeq state.Debt.PendingQueue ]
            )

        let excluded =
            if Set.isEmpty runnableProjects then
                lazy (resolveExcludedProjects ())
            else
                Lazy<_>.CreateFromValue(resolveExcludedProjects ())

        let owedTo = debtScope excluded covering

        // Whether the index names a test project no run here can discharge:
        // neither configured nor declared excluded. If not, no symbol can be
        // owed elsewhere, so nothing needs asking per symbol. One scan of the
        // index, not a covering query, and only if something asks. An index
        // that cannot be scanned may name one.
        let noUnrunnableCoverers = lazy (indexNamesOnlyRunnable excluded)

        // Emit the lifecycle events synchronously here, inside the framework's
        // per-event capture window, so they land in the cached EmittedEvents
        // and re-fire on cache replay — subscribers that key off
        // TestRunCompleted (FileCommandPlugin) must see it on a hit.
        ctx.EmitTestRunCompleted completed

        let assembled =
            assembleRunResults
                { State = state
                  Completed = completed
                  Launch = launch
                  RepoRoot = repoRoot
                  RunnableProjects = runnableProjects
                  Traces = traces }

        let testResults = assembled.TestResults
        let coverage = assembled.Coverage
        let carriedFailures = assembled.CarriedFailures
        let outstandingFailures = assembled.OutstandingFailures

        // Discharged HERE, immediately before the ledger
        // rewrite, because this is the one place the outstanding set is
        // recomputed — pruning anywhere else would leave the ledger and the
        // state disagreeing about what is still owed.
        let unanalyzable = pruneDeletedUnanalyzable File.Exists state.UnanalyzableFiles

        // The ONLY path to the ledger: clear the slate, re-report the whole
        // outstanding set. There is no wholesale clear a filtered run can
        // reach for.
        reportOutstanding ctx unanalyzable state.FailedExtensions outstandingFailures

        // `LastCoverage` is the moment the process acquires test evidence — a
        // run completed and we know what it covered. Until a state carrying it
        // is supplied, the cache key refuses to let a cached BuildCompleted
        // assert a result this process never ran.
        let debtDuringFullRun = state.DebtDuringFullRun

        let receipt =
            decideReceipt
                ctx
                { State = state
                  Completed = completed
                  Launch = launch
                  Coverage = coverage
                  RepoRoot = repoRoot
                  RunnableProjects = runnableProjects
                  ObserveModelGeneration = observeModelGeneration }

        let currentInputTree = receipt.CurrentInputTree
        let currentModelGeneration = receipt.CurrentModelGeneration
        let currentInputs = receipt.CurrentInputs
        let standingEvidence = receipt.StandingEvidence
        let receiptTransition = receipt.ReceiptTransition
        let evidenceReceipt = receipt.EvidenceReceipt

        let state =
            recordCompletion endRun state completed launch assembled receipt unanalyzable


        let settled =
            settleDebt
                { State = state
                  Completed = completed
                  Launch = launch
                  RunnableProjects = runnableProjects
                  OutstandingFailures = outstandingFailures
                  CurrentInputTree = currentInputTree
                  CurrentModelGeneration = currentModelGeneration
                  DebtDuringFullRun = debtDuringFullRun
                  OwedTo = owedTo
                  NoUnrunnableCoverers = noUnrunnableCoverers
                  Debt = env.Debt }

        let aborted = settled.Aborted
        let projectPassed = settled.ProjectPassed
        let coveredByConstruction = settled.CoveredByConstruction
        let debt = settled.Debt
        let absorbedProjects = settled.AbsorbedProjects
        let executedFullSuite = settled.ExecutedFullSuite
        let earnsBaseline = settled.EarnsBaseline

        let earned =
            earnedEvidenceOf
                ctx
                { State = state
                  Started = started
                  Completed = completed
                  Launch = launch
                  Debt = debt
                  RunnableProjects = runnableProjects
                  Aborted = aborted
                  ExecutedFullSuite = executedFullSuite
                  ProjectPassed = projectPassed
                  Unanalyzable = unanalyzable
                  OutstandingFailures = outstandingFailures
                  CurrentInputTree = currentInputTree
                  CurrentInputs = currentInputs
                  CurrentModelGeneration = currentModelGeneration
                  ReceiptTransition = receiptTransition
                  EvidenceReceipt = evidenceReceipt
                  StandingEvidence = standingEvidence }

        let state =
            { state with
                Debt = debt
                Earned = earned
                PendingForceRunProjects = Set.difference state.PendingForceRunProjects absorbedProjects
                CoveredFullSuite =
                    match launch.InputTree with
                    | Some tree when coveredByConstruction -> Some(tree, currentModelGeneration)
                    | _ -> None }

        // `PrepareCommit` writes no sidecar while what is owed is unknown, so a
        // baseline earned then lives only in this daemon's memory. Saying it was
        // recorded would claim a write that never happens.
        if earnsBaseline then
            let runId = completed.RunId.ToString("N")

            if debt.RecoveryOutstanding then
                Logging.info
                    "test-prune"
                    $"Full-suite baseline earned by run %s{runId} but not written: what is owed is still UNKNOWN (%s{describeUnknownDebt debt.UnknownDebt}), and nothing is written until a full suite passes every configured project. Until then every run, in this daemon and the next, is the full suite."
            else
                Logging.info
                    "test-prune"
                    $"Full-suite baseline recorded: run %s{runId} accounted for every configured project (%d{Set.count runnableProjects}); impact-filtered greens are now relative to it"

        // The in-memory hot view must shed ONLY the committed symbols,
        // never the whole list — symbols left in the queue (mid-run
        // arrivals, projects that failed/aborted) must keep selecting
        // tests until a covering run passes. `queueAfterCommit` is the
        // post-commit queue; it drives the cleared ChangedSymbols.
        let queueAfterCommit = debt.PendingQueue
        let remainingChangedSymbols = queueAfterCommit |> Set.toList

        // Why the queue is still non-empty, in words. Symbols owed to projects
        // this daemon cannot run are not "waiting on build": no build will
        // discharge them, so the message names the projects and the way out.
        // A function: it queries each queued symbol, and only non-green
        // branches ask.
        let pendingDescription () =
            let elsewhere =
                if noUnrunnableCoverers.Value then
                    Set.empty
                else
                    owedElsewhere owedTo queueAfterCommit

            match Set.toList elsewhere with
            | [] -> $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)"
            | projects ->
                let names = projects |> String.concat ", "

                $"%d{Set.count queueAfterCommit} symbol(s) still owed to tests in %s{names}, which this \
                  daemon does not run: list them in `tests.projects`, or declare them in \
                  `tests.excluded` with a reason"


        // Whatever this run left owed is queued behind it as an intent by
        // the trigger that owed it (`enqueueImpactRun`, a queued `run-tests`),
        // and is delivered only after this fold is committed. Nothing is
        // launched from here.
        reportRunOutcome
            ctx
            { Completed = completed
              CarriedFailures = carriedFailures
              QueueAfterCommit = queueAfterCommit
              PendingDescription = pendingDescription
              ReceiptTransition = receiptTransition
              EvidenceReceipt = evidenceReceipt }
            testResults

        let replies =
            match message with
            | CommandTestsFinished(_, _, _, reply, response) -> [ reply, response ]
            | _ -> []

        return
            { state with
                LastResults = Some testResults
                // Only the files this run launched against are consumed; a
                // file that changed during the run selects the next one.
                ChangedFiles =
                    if coveredByConstruction then
                        []
                    else
                        state.ChangedFiles
                        |> List.filter (fun file -> not (List.contains file launch.ChangedFiles))
                ChangedSymbols = remainingChangedSymbols
                AffectedTests = Analyzed []
                Replies = replies }

    // An unavailable launch ran nothing. Its receipt is revoked and the fanout
    // it consumed is owed again. No run is queued: the next build or cohort
    // seal is what can make the artifacts or the host available, and it
    // launches with this debt.
    }
