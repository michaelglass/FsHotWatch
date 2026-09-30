module FsHotWatch.Coverage.CoveragePlugin

open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

/// Where a check reads a run's Cobertura reports.
[<RequireQualifiedAccess>]
type CoverageReports =
    /// Every `coverage.cobertura.xml` under the directory, whatever the run's scope.
    | SearchUnder of dir: string
    /// Exactly these files: a full-suite run's own runner output. Only a full-suite run
    /// writes them, so no other run is judged by them.
    | Named of paths: string list

/// The floors a run's coverage fell below, as the `coverageratchet check` CLI reads
/// them: per-file percentage floors and absolute count floors.
[<NoComparison; NoEquality>]
type FloorFailures =
    {
        /// Files below a line or branch percentage floor.
        Files: FileResult list
        /// Files below a covered-lines or covered-branches count floor.
        Counts: CountResult list
    }

/// The gated coverage verdict for a cycle, after applying the impact-filter
/// guard. Distinct from the raw floor check so the message that drives the
/// plugin's pass/fail status carries the already-gated decision.
[<NoComparison; NoEquality>]
type CoverageVerdict =
    /// Every evaluated file met its floors (or no coverage XML was produced).
    | Passed
    /// A full-suite run found real shortfalls — these GATE (exit non-zero).
    | Failed of FloorFailures
    /// An impact-filtered run produced shortfalls, but it did NOT run every
    /// project's tests this cycle, so an un-run source file reads `0.0%`
    /// indistinguishably from a genuine zero. We do NOT gate on a filtered
    /// run (raise-only); instead we surface a loud notice naming the count so
    /// the shortfall is visible without a false red.
    | NotGatedFiltered of belowFloorCount: int
    /// The floor file's reader options could not be read (e.g. an invalid
    /// `includedExtensions` list), or a named report was not written by the run it is
    /// judged for, so nothing was judged. Gates whatever the run's scope: a check that
    /// cannot read what it judges has verified nothing.
    | Unreadable of reason: string

/// What a completed run's coverage comes to.
[<NoComparison; NoEquality>]
type CoverageJudgement =
    /// The run's reports were judged against the floors.
    | Judged of CoverageVerdict
    /// The run is not one the configured reports describe, so nothing was judged.
    | NotJudged of reason: string

[<NoComparison; NoEquality>]
type CoverageMsg =
    | CheckDone of judgement: CoverageJudgement * elapsed: System.TimeSpan
    /// A `coverage-ratchet` IPC command asking the MAILBOX to rewrite the
    /// thresholds config. The rewrite runs under the SAME "coverage-check"
    /// exclusive slot as a check, because a check READS the config the ratchet
    /// rewrites, so running the two concurrently would let them race. `reply`
    /// carries the outcome back to the awaiting IPC command; every path must
    /// resolve it.
    | RatchetRequested of cfgPath: string * reply: System.Threading.Tasks.TaskCompletionSource<string>
    /// Completion of a `RatchetRequested` run — reports the terminal status
    /// (the framework reported Running at the claim).
    | RatchetDone of message: string * elapsed: System.TimeSpan

/// What the plugin carries between events.
///
/// `Owed` exists because the "coverage-check" key is held from the claim until the
/// run's RESULT FOLD commits — not until the worker finishes. A `TestRunCompleted`
/// folded in that window has its claim refused while no check is live, and a refused
/// trigger that is merely logged is work that silently vanishes: nothing re-requests
/// it, so the latest test run's coverage is never judged. A refused claim keeps the
/// trigger here and the fold that holds the key drains it.
type CoverageState =
    {
        /// The last COMMITTED check verdict — the answer `coverage-status` gives.
        /// `None` until a check has run; a ratchet rewrite never touches it.
        LastCheckPassed: bool option
        /// The scope of a trigger whose claim was refused, newest wins. A check reads
        /// whatever Cobertura XML is on disk when it runs, which is the newest run's
        /// output, so the newest run's scope is the one that describes what it reads.
        /// Cleared the moment a check for it is claimed.
        Owed: (RunScope * System.DateTime) option
        /// When each run still in flight started (`TestRunStarted`), by run id: a
        /// named report older than its run's start was not written by that run.
        Started: Map<System.Guid, System.DateTime>
    }

[<RequireQualifiedAccess>]
module CoverageState =

    /// No check has run and nothing is owed.
    let initial =
        { LastCheckPassed = None
          Owed = None
          Started = Map.empty }

/// The floor check the `coverageratchet check` CLI makes: percentage floors, then count
/// floors. `None` when every floor holds.
let internal floorCheck (config: Config) (coverage: FileCoverage list) : FloorFailures option =
    let files =
        match check config coverage with
        | AllPassed -> []
        | SomeFailed results -> results

    let counts =
        match checkCounts config coverage with
        | CountsAllPassed -> []
        | CountsFailed results -> results

    if List.isEmpty files && List.isEmpty counts then
        None
    else
        Some { Files = files; Counts = counts }

/// Decide the gated verdict from a raw floor check and what the run established. Pure,
/// so the gating policy is unit-testable without spinning a daemon.
///
/// Takes the scope a run PROVED, not a bool. `FullSuite` gates normally; `Partial`
/// downgrades a shortfall to a non-gating notice, because un-run files cannot be
/// distinguished from genuine zeros. A run that executed nothing has no `RunScope`
/// to pass, so it cannot reach here at all and cannot be mistaken for a filtered
/// one — the caller has already had to decide what to do about it.
let internal gateVerdict (scope: RunScope) (failures: FloorFailures option) : CoverageVerdict =
    match failures, scope with
    | None, _ -> Passed
    | Some failures, FullSuite -> Failed failures
    | Some failures, Partial ->
        let files =
            (failures.Files |> List.map _.File.FileName)
            @ (failures.Counts |> List.map _.File.FileName)
            |> List.distinct

        NotGatedFiltered files.Length

let private pollForFiles (searchDir: string) (maxAttempts: int) (delayMs: int) =
    async {
        let mutable result = []
        let mutable attempt = 0

        while List.isEmpty result && attempt < maxAttempts do
            let files = findCoverageFiles searchDir

            if not (List.isEmpty files) then
                result <- files
            else
                do! Async.Sleep delayMs
                attempt <- attempt + 1

        return result
    }

/// Read the run's reports the way the `coverageratchet` CLI does: with the reader
/// options the floor file asks for (`loadReaderOptions`), which root the directory
/// rules at the floor file's directory. The parameterless `parseFiles` matches rules
/// against the whole absolute path, so a checkout under a directory named `tests`,
/// `test` or `obj` would read nothing, and an `includedExtensions` list in the floor
/// file would be ignored; either way the daemon's verdict would disagree with the
/// CLI's on the same reports.
let private readCoverage (configPath: string) (xmlPaths: string list) : Result<FileCoverage list, string> =
    loadReaderOptions configPath
    |> Result.map (fun options ->
        (readReports options (xmlPaths |> List.map System.IO.File.ReadAllText)).Lines
        |> buildCoverage)

/// Judge `xmlPaths` against the floor file and gate the result by `scope`.
let private verdictOf (configPath: string) (scope: RunScope) (xmlPaths: string list) : CoverageVerdict =
    match readCoverage configPath xmlPaths with
    | Ok coverage -> gateVerdict scope (floorCheck (loadConfig configPath) coverage)
    | Result.Error reason -> Unreadable reason

/// The reports of `reports` that exist now.
let private existingReports (reports: CoverageReports) : string list =
    match reports with
    | CoverageReports.SearchUnder dir -> findCoverageFiles dir
    | CoverageReports.Named paths -> paths |> List.filter System.IO.File.Exists

/// Why a run of `scope` is not judged by `reports`, or `None` when it is. Decided
/// from the run's kind alone, before anything is read.
let internal notJudgedReason (reports: CoverageReports) (scope: RunScope) : string option =
    match reports, scope with
    | CoverageReports.Named _, Partial ->
        Some "an impact-filtered run does not write the named coverage reports, which a full-suite run does"
    | CoverageReports.Named _, FullSuite
    | CoverageReports.SearchUnder _, _ -> None

/// Whether a run's coverage is judged, and against what, before any report is read.
/// `Named` reports are a full-suite run's own output: a run that did not run the full
/// suite did not write them, so it is not judged by them; a full-suite run is, and a
/// named report it did not write (missing, or older than `runStartedAt`) is
/// `Unreadable`, never a verdict on an earlier run's output. `found` is what a
/// `SearchUnder` check found on disk.
let internal judge
    (configPath: string)
    (reports: CoverageReports)
    (scope: RunScope)
    (runStartedAt: System.DateTime)
    (found: string list)
    : CoverageJudgement =
    match notJudgedReason reports scope, reports with
    | Some reason, _ -> NotJudged reason
    | None, CoverageReports.Named paths ->
        let notWritten =
            paths
            |> List.filter (fun path ->
                not (System.IO.File.Exists path)
                || System.IO.File.GetLastWriteTimeUtc path < runStartedAt)

        if List.isEmpty notWritten then
            Judged(verdictOf configPath scope paths)
        else
            let listed = String.concat ", " notWritten

            Judged(
                Unreadable $"coverage report(s) not written by this run (missing, or older than its start): %s{listed}"
            )
    | None, CoverageReports.SearchUnder _ ->
        if List.isEmpty found then
            Judged Passed
        else
            Judged(verdictOf configPath scope found)

/// One coverage check: find this run's Cobertura output, judge it, and gate the raw
/// result by the scope the run PROVED.
let private checkWork
    (configPath: string)
    (reports: CoverageReports)
    (scope: RunScope)
    (runStartedAt: System.DateTime)
    =
    async {
        let checkStarted = System.DateTime.UtcNow

        let! found =
            match reports with
            | CoverageReports.SearchUnder dir -> pollForFiles dir 50 100
            | CoverageReports.Named _ -> async { return [] }

        let judgement = judge configPath reports scope runStartedAt found

        // No baseline to refresh here: the TestPrune DB is the coverage
        // high-watermark, ingested (max-merged across projects) per run, and emits
        // the shared cobertura.
        return CheckDone(judgement, System.DateTime.UtcNow - checkStarted)
    }

/// Launch a check for a run of `scope` that started at `runStartedAt`, returning what
/// the launch leaves OWED.
///
/// A claim the framework accepted owns the trigger, so nothing is owed. A refused one
/// means "coverage-check" is held — by a live check, or by a finished check whose
/// result fold has not committed yet (`SlotHolder.Fold`).
/// Either way the holder's fold drains what is kept here, so the trigger waits rather
/// than disappearing.
let private startCheck
    (ctx: PluginCtx<CoverageMsg>)
    (configPath: string)
    (reports: CoverageReports)
    (scope: RunScope)
    (runStartedAt: System.DateTime)
    : (RunScope * System.DateTime) option =
    match ctx.RunExclusive "coverage-check" (checkWork configPath reports scope runStartedAt) with
    | Claimed -> None
    | SlotBusy ->
        ctx.Log "coverage-check slot held — keeping this trigger for the holder's result fold to run"
        Some(scope, runStartedAt)

/// Run whatever a refused claim left owed. Called from the folds that HOLD
/// "coverage-check": a result fold may claim the next run under its own key before it
/// commits, so this is the one place the retained trigger can actually start.
let private drainOwed
    (ctx: PluginCtx<CoverageMsg>)
    (configPath: string)
    (reports: CoverageReports)
    (state: CoverageState)
    : CoverageState =
    match state.Owed with
    | None -> state
    | Some(scope, runStartedAt) ->
        { state with
            Owed = startCheck ctx configPath reports scope runStartedAt }

/// The ratchet's per-file error text for a file below its percentage floors.
let private percentDetail (r: FileResult) =
    [ if not (FileResult.linePassed r) then
          $"line=%.1f{r.File.LinePct}%% < min %.1f{r.LineThreshold}%%"
      if not (FileResult.branchPassed r) then
          $"branch=%.1f{r.File.BranchPct}%% < min %.1f{r.BranchThreshold}%%" ]

/// The ratchet's per-file error text for a file below its count floors.
let private countDetail (r: CountResult) =
    [ if not (CountResult.linesPassed r) then
          $"covered lines %d{r.File.LinesCovered} < %d{r.Floor.CoveredLines}"
      if not (CountResult.branchesPassed r) then
          $"covered branches %d{r.File.BranchesCovered} < %d{r.Floor.CoveredBranches}" ]

/// <summary>Create a CoveragePlugin handler that checks per-file coverage floors (line and
/// branch percentages, and covered-line and covered-branch counts) after each
/// <c>TestRunCompleted</c> event, reading the reports <paramref name="reports"/> names.</summary>
/// <param name="configPath">Path to the coverage-ratchet.json thresholds config.</param>
/// <param name="reports">Where each check reads the run's Cobertura reports.</param>
let createWith (configPath: string) (reports: CoverageReports) : PluginHandler<CoverageState, CoverageMsg> =
    { Name = PluginName.create "coverage"
      Init = CoverageState.initial
      Subscriptions = Set.ofList [ SubscribeTestRunStarted; SubscribeTestRunCompleted ]
      CacheKey = None
      Teardown = None
      PrepareCommit = None
      Commands =
        [ "coverage-ratchet",
          PluginCommand.Request(fun ctx args ->
              async {
                  // The rewrite must NOT run here on the IPC thread: a
                  // concurrent `RunExclusive "coverage-check"` run reads the
                  // very config this rewrites. Post to the mailbox; the
                  // handler claims the same slot, serialising the two.
                  let cfgPath =
                      if args.Length > 0 && args.[0] <> "" then
                          args.[0]
                      else
                          configPath

                  let reply =
                      System.Threading.Tasks.TaskCompletionSource<string>(
                          System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                      )

                  ctx.Post(RatchetRequested(cfgPath, reply))

                  // Bounded await: the ratchet itself is quick, but the slot may
                  // be held by a full coverage check.
                  let! winner =
                      System.Threading.Tasks.Task.WhenAny(
                          reply.Task,
                          System.Threading.Tasks.Task.Delay(System.TimeSpan.FromMinutes 5.0)
                      )
                      |> Async.AwaitTask

                  if winner = (reply.Task :> System.Threading.Tasks.Task) then
                      return reply.Task.Result
                  else
                      return
                          "coverage-ratchet: did not complete within 5 minutes (a coverage check may be holding the slot); retry"
              })

          "coverage-status",
          PluginCommand.Observe(fun _ctx state _args ->
              async {
                  return
                      match state.LastCheckPassed with
                      | None -> "coverage: no check run yet"
                      | Some true -> "coverage: OK"
                      | Some false -> "coverage: FAILED (run `fshw errors` for details)"
              }) ]
      Update =
        fun ctx state event ->
            match event with
            | TestRunStarted started ->
                async {
                    return
                        { state with
                            Started = state.Started |> Map.add started.RunId started.StartedAt }
                }

            | TestRunCompleted trc ->
                // The run's own start bounds which reports it wrote. A run whose start
                // was not seen falls back to its completion less its elapsed time.
                let runStartedAt =
                    state.Started
                    |> Map.tryFind trc.RunId
                    |> Option.defaultWith (fun () -> System.DateTime.UtcNow - trc.TotalElapsed)

                let state =
                    { state with
                        Started = state.Started |> Map.remove trc.RunId }

                match trc.Outcome, RunVerification.scope trc.Verification with
                | Aborted _, _ -> async { return state }
                | Normal, None ->
                    // No scope established because the run executed nothing, so it
                    // produced no coverage and there is nothing to judge. Declining is
                    // the same move `Aborted` above makes, for the same reason.
                    ctx.Log "coverage check skipped — the run executed no tests, so it produced no coverage to judge"
                    async { return state }
                | Normal, Some scope ->
                    match notJudgedReason reports scope with
                    | Some reason ->
                        ctx.Log $"coverage not judged: %s{reason}"
                        async { return state }
                    | None ->
                        // A refused claim (a check or a ratchet rewrite holds the key, or
                        // a finished one's result fold still does) KEEPS this trigger
                        // owed; the holder's fold runs it. `startCheck` answers with what
                        // is left owed, so a claim that succeeded clears any older debt
                        // this run supersedes.
                        async {
                            return
                                { state with
                                    Owed = startCheck ctx configPath reports scope runStartedAt }
                        }

            | Custom(RatchetRequested(cfgPath, reply)) ->
                let work =
                    async {
                        let runStarted = System.DateTime.UtcNow

                        try
                            try
                                let xmlPaths = existingReports reports

                                let message =
                                    if List.isEmpty xmlPaths then
                                        match reports with
                                        | CoverageReports.SearchUnder _ ->
                                            "coverage-ratchet: no coverage.cobertura.xml found"
                                        | CoverageReports.Named _ ->
                                            "coverage-ratchet: none of the named coverage reports exists"
                                    else
                                        match readCoverage cfgPath xmlPaths with
                                        | Result.Error reason -> $"coverage-ratchet failed: %s{reason}"
                                        | Ok coverage ->
                                            let raw = loadRawConfig cfgPath
                                            saveRawConfig cfgPath (ratchetRaw raw coverage).Config
                                            $"coverage-ratchet: thresholds updated in %s{cfgPath}"

                                reply.TrySetResult(message) |> ignore
                                return RatchetDone(message, System.DateTime.UtcNow - runStarted)
                            with ex ->
                                let message = $"coverage-ratchet failed: %s{ex.Message}"
                                reply.TrySetResult(message) |> ignore
                                return RatchetDone(message, System.DateTime.UtcNow - runStarted)
                        finally
                            // Cancellation (daemon teardown) skips `with` but runs
                            // `finally`: never leave the IPC client awaiting a
                            // reply that cannot come. No-op if already set.
                            reply.TrySetResult("coverage-ratchet: daemon shut down before it completed")
                            |> ignore
                    }

                match ctx.RunExclusive "coverage-check" work with
                | Claimed -> ()
                | SlotBusy ->
                    // A coverage check is running and READS the config this
                    // would rewrite — refuse rather than race; the caller
                    // retries once the check settles.
                    reply.TrySetResult("coverage-ratchet: a coverage check is in flight; retry once it settles")
                    |> ignore

                async { return state }

            | Custom(RatchetDone(message, elapsed)) ->
                async {
                    ctx.Log message
                    // Terminal for the ratchet run (the framework reported
                    // Running at the claim). The state (last CHECK outcome) is
                    // untouched — a ratchet is not a check.
                    ctx.ReportStatus(PluginStatus.Completed(System.DateTime.UtcNow, RunVerdict.create message elapsed))

                    // This fold holds "coverage-check" until it commits, so a trigger
                    // refused while the ratchet ran starts here.
                    return drainOwed ctx configPath reports state
                }

            | Custom(CheckDone(NotJudged reason, elapsed)) ->
                async {
                    ctx.Log $"coverage not judged: %s{reason}"

                    ctx.ReportStatus(
                        PluginStatus.Completed(
                            System.DateTime.UtcNow,
                            RunVerdict.create $"coverage not judged: %s{reason}" elapsed
                        )
                    )

                    return drainOwed ctx configPath reports state
                }

            | Custom(CheckDone(Judged Passed, elapsed)) ->
                async {
                    ctx.ClearAllErrors()

                    ctx.ReportStatus(
                        PluginStatus.Completed(
                            System.DateTime.UtcNow,
                            RunVerdict.create "coverage floors passed" elapsed
                        )
                    )

                    // The terminal is reported BEFORE the owed trigger is drained: a
                    // claim makes this fold hold a live run again, and the status funnel
                    // drops a terminal reported while one is in flight.
                    return
                        drainOwed
                            ctx
                            configPath
                            reports
                            { state with
                                LastCheckPassed = Some true }
                }

            | Custom(CheckDone(Judged(NotGatedFiltered belowFloorCount), elapsed)) ->
                async {
                    // Clear any prior reds so the verdict is a deterministic ✓ on an
                    // unchanged commit; the notice below keeps the gap visible. A real
                    // regression is caught by the next full-suite run, which gates.
                    ctx.ClearAllErrors()

                    let summary =
                        $"%d{belowFloorCount} file(s) below floor not gated (impact-filtered run; run a full suite to gate coverage)"

                    ctx.Log $"coverage: %s{summary}"

                    ctx.ReportStatus(PluginStatus.Completed(System.DateTime.UtcNow, RunVerdict.create summary elapsed))

                    return
                        drainOwed
                            ctx
                            configPath
                            reports
                            { state with
                                LastCheckPassed = Some true }
                }

            | Custom(CheckDone(Judged(Failed failures), elapsed)) ->
                async {
                    let details =
                        (failures.Files |> List.map (fun r -> r.File.FileName, percentDetail r))
                        @ (failures.Counts |> List.map (fun r -> r.File.FileName, countDetail r))
                        |> List.groupBy fst
                        |> List.map (fun (file, parts) -> file, parts |> List.collect snd)

                    for file, parts in details do
                        let detail = String.concat ", " parts
                        ctx.ReportErrors file [ ErrorEntry.error $"coverage: %s{detail}" ]

                    let summary = $"%d{details.Length} file(s) below threshold"
                    ctx.ReportStatus(PluginStatus.failedNow summary summary elapsed)

                    return
                        drainOwed
                            ctx
                            configPath
                            reports
                            { state with
                                LastCheckPassed = Some false }
                }

            | Custom(CheckDone(Judged(Unreadable reason), elapsed)) ->
                async {
                    ctx.ReportErrors configPath [ ErrorEntry.error $"coverage: %s{reason}" ]
                    let summary = "coverage not readable"
                    ctx.ReportStatus(PluginStatus.failedNow reason summary elapsed)

                    return
                        drainOwed
                            ctx
                            configPath
                            reports
                            { state with
                                LastCheckPassed = Some false }
                }

            | _ -> async { return state } }

/// <summary>Create a CoveragePlugin handler that checks per-file coverage floors after each
/// <c>TestRunCompleted</c> event, reading every <c>coverage.cobertura.xml</c> under
/// <paramref name="searchDir"/>.</summary>
/// <param name="configPath">Path to the coverage-ratchet.json thresholds config.</param>
/// <param name="searchDir">Directory tree to search for <c>coverage.cobertura.xml</c> files.</param>
let create (configPath: string) (searchDir: string) : PluginHandler<CoverageState, CoverageMsg> =
    createWith configPath (CoverageReports.SearchUnder searchDir)
