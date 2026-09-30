module FsHotWatch.Coverage.CoveragePlugin

open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
// Last, so its `judge`, `Passed` and `Failed` shadow CoverageRatchet's.
open FsHotWatch.Coverage.Judgement

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
                    let details = FloorFailures.details failures

                    for file, detail in details do
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
