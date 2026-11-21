/// AUTOMATION-1816 step 2b: after a fresh full-suite `confirm` has written `.fshw/verdict.json`,
/// compare what reuse would have handed each plugin with what this run produced, and file the
/// report (`ReferencesShadowReport.write`).
///
/// It acts only for a `--fresh` confirm that kept no prior verdict and graded a run, with
/// `cache.nightlyShadow` on: a filtered `check`, a reused confirm and the switch off write nothing
/// and ask the daemon nothing. On another host than the land host it says `hostMismatch` before
/// any lookup. RECORD AND COMPARE ONLY: nothing here touches the verdict or the exit code, and a
/// failure is logged and dropped.
module FsHotWatch.Cli.ReferencesShadowTrigger

open System
open System.IO
open FsHotWatch
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.Cli.ScopeCommands

/// What the trigger reads before the run, because the run consumes it: C (the pending
/// verification queue), the quarantined classes, and the watermark run (the full-suite run the
/// previous verdict was relative to).
type Before =
    { Changed: string list
      QuarantinedClasses: Set<string>
      WatermarkRun: Guid option }

/// Read `Before` in `repoRoot`. Read only; an unreadable ledger reads as empty and is said so.
let readBefore (repoRoot: string) : Before =
    let changed =
        match FsHotWatch.TestPrune.PendingVerification.load repoRoot with
        | FsHotWatch.TestPrune.PendingVerification.LoadedQueue.Loaded queue -> Set.toList queue
        | FsHotWatch.TestPrune.PendingVerification.LoadedQueue.Unreadable reason ->
            Logging.warn "references-shadow" $"pending verification unreadable, C read as empty: %s{reason}"
            []

    let quarantined =
        match FsHotWatch.TestPrune.OutstandingFailures.OutstandingFailure.load repoRoot with
        | FsHotWatch.TestPrune.OutstandingFailures.OutstandingFailure.LoadedFailures.Loaded failures ->
            failures |> List.choose _.Class |> Set.ofList
        | FsHotWatch.TestPrune.OutstandingFailures.OutstandingFailure.LoadedFailures.Unreadable reason ->
            Logging.warn "references-shadow" $"outstanding failures unreadable, none read as quarantined: %s{reason}"
            Set.empty

    let watermark =
        match Verdict.read repoRoot with
        | Verdict.Reading.Found v ->
            match v.Outcome with
            | Verdict.Green(CheckVerdict.Baseline.FullSuiteRun baseline) -> Some baseline.RunId
            | _ -> None
        | Verdict.Reading.Missing
        | Verdict.Reading.Unreadable _ -> None

    { Changed = changed
      QuarantinedClasses = quarantined
      WatermarkRun = watermark }

/// Whether the confirm that just published is one the nightly compares.
[<RequireQualifiedAccess>]
type Graded =
    | Compare of runId: Guid
    | Skip of reason: string

/// A `--fresh` confirm (`freshness`) that kept no prior verdict and graded the run `runId`.
let graded (freshness: CheckVerdict.Freshness) (keptPriorVerdict: bool) (runId: Guid option) : Graded =
    match freshness, keptPriorVerdict, runId with
    | CheckVerdict.Freshness.MayReuse, _, _ -> Graded.Skip "not a --fresh confirm"
    | CheckVerdict.Freshness.RequireNewRun, true, _ -> Graded.Skip "the prior verdict was kept"
    | CheckVerdict.Freshness.RequireNewRun, false, None -> Graded.Skip "no run was graded"
    | CheckVerdict.Freshness.RequireNewRun, false, Some runId -> Graded.Compare runId

/// A test's outcome as the comparison reads it from a CTRF row, and whether the row alone makes
/// it unstable (it timed out, was cancelled, or has a status CTRF does not define).
let private outcomeOf (outcome: TestRows.Outcome) : TestOutcome * bool =
    match outcome with
    | TestRows.Outcome.Passed -> TestOutcome.Passed, false
    | TestRows.Outcome.Failed -> TestOutcome.Failed, false
    | TestRows.Outcome.TimedOut
    | TestRows.Outcome.Cancelled -> TestOutcome.Failed, true
    | TestRows.Outcome.Skipped -> TestOutcome.Skipped, false
    | TestRows.Outcome.Other -> TestOutcome.Failed, true

/// Every CTRF row of run `runId`, by test name.
let private runRows (repoRoot: string) (runId: Guid) : Map<string, TestRows.Outcome> =
    let dir = Ctrf.runDir repoRoot runId

    if Directory.Exists dir then
        Directory.GetFiles(dir, "*" + Ctrf.ReportSuffix)
        |> Seq.collect (File.ReadAllText >> FsHotWatch.TestPrune.Flakiness.parseCtrfRows)
        |> Seq.map (fun row -> row.Name, row.Outcome)
        |> Map.ofSeq
    else
        Map.empty

/// The rows `compareTests` reads: every test the nightly or the watermark run reported, its
/// outcome in each, whether it is quarantined, and whether it is unstable (a non-zero flakiness
/// score over its last 20 runs, or it timed out or was cancelled in either run).
let testRows (repoRoot: string) (before: Before) (runId: Guid) : TestRow list =
    let nightly = runRows repoRoot runId

    let watermark =
        before.WatermarkRun
        |> Option.map (runRows repoRoot)
        |> Option.defaultValue Map.empty

    let history =
        FsHotWatch.TestPrune.Flakiness.loadHistory (FsHotWatch.TestPrune.Flakiness.historyPath repoRoot)

    let flaky (test: string) =
        match Map.tryFind test history with
        | Some records ->
            records
            |> List.truncate 20
            |> List.map _.Outcome
            |> FsHotWatch.TestPrune.Flakiness.computeFlakiness
            |> fun score -> score > 0.0
        | None -> false

    let read (rows: Map<string, TestRows.Outcome>) (test: string) =
        match Map.tryFind test rows with
        | Some outcome -> outcomeOf outcome
        | None -> TestOutcome.Absent, false

    Set.union (nightly |> Map.keys |> Set.ofSeq) (watermark |> Map.keys |> Set.ofSeq)
    |> Set.toList
    |> List.map (fun test ->
        let nightlyOutcome, nightlyStopped = read nightly test
        let watermarkOutcome, watermarkStopped = read watermark test

        { Test = test
          Watermark = watermarkOutcome
          Nightly = nightlyOutcome
          Quarantined =
            before.QuarantinedClasses
            |> Set.exists (fun cls -> test.StartsWith(cls + ".", StringComparison.Ordinal))
          Unstable = nightlyStopped || watermarkStopped || flaky test })

/// One plugin's comparison, through `send`. A host without the command (the plugin is not
/// registered) leaves every file unverifiable: no key would have been read.
let private pluginComparison (send: Send) (command: string) (plugin: string) (servable: ServableFile list) =
    match send command (Servable.toJson servable) with
    | Some reply ->
        match PluginReply.tryOfJson reply with
        | Some comparison -> comparison
        | None -> invalidOp $"`%s{command}` answered something that is not a plugin comparison"
    | None ->
        servable
        |> List.map (fun f -> f.File, FileOutcome.Unverifiable UnverifiableReason.NoReusedKey)
        |> PluginComparison.ofFiles plugin

/// One plugin's spot-check, through `send`: up to `SpotCheckSize` of its `sameKey` files, drawn by
/// `seed`, recomputed by the plugin with its cache bypassed. No `sameKey` file asks nothing; a host
/// without the command leaves each sampled file not recomputed, saying so.
let private spotCheck (send: Send) (command: string) (seed: int) (comparison: PluginComparison) =
    match SpotCheck.sample seed SpotCheckSize (PluginComparison.sameKeyFiles comparison) with
    | [] -> []
    | files ->
        match send command (SpotCheckRequest.toJson files) with
        | Some reply ->
            match SpotCheckReply.tryOfJson reply with
            | Some outcomes -> outcomes
            | None -> invalidOp $"`%s{command}` answered something that is not a spot-check"
        | None ->
            files
            |> List.map (fun file -> file, SpotOutcome.NotRecomputed $"the daemon has no `%s{command}` command")

/// The report for the run `runId`, through `send`, on `host`: `hostMismatch` before any lookup
/// anywhere but `landHost`.
/// Each plugin's `sameKey` sample is drawn by `seed`, and recorded with it.
let compare
    (landHost: string)
    (host: string)
    (send: Send)
    (before: Before)
    (rows: unit -> TestRow list)
    (runId: Guid)
    (seed: int)
    (writtenAt: DateTimeOffset)
    : Report =
    let result =
        onHost host landHost (fun () ->
            let servable =
                match send ServableCommand "" with
                | Some reply ->
                    match Servable.tryOfJson reply with
                    | Some files -> files
                    | None -> invalidOp $"`%s{ServableCommand}` answered something that is not a file list"
                | None -> invalidOp $"the daemon has no `%s{ServableCommand}` command"

            let testPrune =
                let request: TestPruneRequest =
                    { RunId = runId.ToString "N"
                      Changed = before.Changed
                      Files = servable }

                match send TestPruneCommand (TestPruneRequest.toJson request) with
                | Some reply ->
                    match SelectionsReply.tryOfJson reply with
                    | Some(SelectionsAnswer.Selected selections) ->
                        TestPruneSection.Compared(compareTests selections (rows ()))
                    // Loud in the report, silent in the gate: the confirm already published its verdict.
                    | Some(SelectionsAnswer.Refused reason) ->
                        Logging.warn
                            "references-shadow"
                            $"test-prune refused S', the comparison is invalid: %s{reason}"

                        TestPruneSection.Invalid reason
                    | None -> invalidOp $"`%s{TestPruneCommand}` answered something that is not selections"
                | None -> TestPruneSection.NotCompared "no test-prune plugin"

            let analyzers = pluginComparison send AnalyzersCommand "analyzers" servable
            let lint = pluginComparison send LintCommand "lint" servable

            Compared
                { Servable = servable.Length
                  Analyzers = analyzers
                  Lint = lint
                  TestPrune = testPrune
                  SpotChecks =
                    { Seed = seed
                      Analyzers = spotCheck send AnalyzersSpotCheckCommand seed analyzers
                      Lint = spotCheck send LintSpotCheckCommand seed lint } })

    { RunId = runId.ToString "N"
      WrittenAt = writtenAt
      Result = result }

/// Run the confirm (`run`, which publishes `.fshw/verdict.json` and returns the exit code), and,
/// with the nightly shadow on, compare and file the report after it when the run is `graded` for
/// it. With it off, nothing is read and `send` is never called. The exit code is `run`'s,
/// whatever the comparison does: a failure is logged and dropped.
// MGA-ERROR-REPORT-001:ok — the report is measurement; nothing it does may fail the confirm.
let aroundConfirm
    (repoRoot: string)
    (nightly: FsHotWatch.InMemoryCheckCache.NightlyShadow)
    (host: string)
    (freshness: CheckVerdict.Freshness)
    (send: Send)
    (keptPriorVerdict: unit -> bool)
    (run: unit -> int)
    : int =
    match nightly with
    | FsHotWatch.InMemoryCheckCache.NightlyShadow.Off -> run ()
    | FsHotWatch.InMemoryCheckCache.NightlyShadow.OnLandHost landHost ->
        let before = readBefore repoRoot
        let exitCode = run ()

        let runId =
            match Verdict.read repoRoot with
            | Verdict.Reading.Found v -> v.RunId
            | Verdict.Reading.Missing
            | Verdict.Reading.Unreadable _ -> None

        match graded freshness (keptPriorVerdict ()) runId with
        | Graded.Skip reason -> Logging.info "references-shadow" $"no report: %s{reason}"
        | Graded.Compare runId ->
            try
                compare
                    landHost
                    host
                    send
                    before
                    (fun () -> testRows repoRoot before runId)
                    runId
                    (Random.Shared.Next())
                    DateTimeOffset.UtcNow
                |> write repoRoot
            with ex ->
                let run = runId.ToString "N"
                Logging.warn "references-shadow" $"report for run %s{run} not made: %s{ex.Message}"

        exitCode
