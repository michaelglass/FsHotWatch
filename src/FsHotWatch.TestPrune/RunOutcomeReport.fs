/// A completed run's terminal status: its tally and the verdict reported from it.
module FsHotWatch.TestPrune.RunOutcomeReport

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
open FsHotWatch.TestPrune.ImpactLaunch
open FsHotWatch.TestPrune.ImpactRun
open FsHotWatch.TestPrune.FileCheckedHandler
open FsHotWatch.TestPrune.CheckHandlers

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

/// The status headline of a run in which a project timed out. It also names the projects
/// that failed without timing out (AUTOMATION-1569): naming only the timeouts left a
/// reader of the status chasing a slow project while an ordinary failure sat unnamed.
let internal timeoutHeadline (tally: RunTally) (carriedNote: string) : string =
    let names = tally.TimedOutProjects |> String.concat ", "

    let failedNames =
        tally.FailedList
        |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then None else Some name)

    let failedClause =
        match failedNames with
        | [] -> ""
        | failed ->
            let joined = failed |> String.concat ", "
            $"; %d{failed.Length} failed: %s{joined}"

    $"%d{tally.TimedOutProjects.Length} timed out: %s{names}%s{failedClause}%s{carriedNote}"

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

        let headline = timeoutHeadline tally carriedNote
        ctx.ReportStatus(PluginStatus.failedNow headline headline results.Elapsed)
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
