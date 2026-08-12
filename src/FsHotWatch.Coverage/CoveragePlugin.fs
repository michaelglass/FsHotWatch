module FsHotWatch.Coverage.CoveragePlugin

open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

/// The gated coverage verdict for a cycle, after applying the impact-filter
/// guard. Distinct from the raw `CheckResult` so the message that drives the
/// plugin's pass/fail status carries the already-gated decision.
[<NoComparison; NoEquality>]
type CoverageVerdict =
    /// Every evaluated file met its floor (or no coverage XML was produced).
    | Passed
    /// A full-suite run found real shortfalls — these GATE (exit non-zero).
    | Failed of CoverageRatchet.Thresholds.FileResult list
    /// An impact-filtered run produced shortfalls, but it did NOT run every
    /// project's tests this cycle, so an un-run source file reads `0.0%`
    /// indistinguishably from a genuine zero. We do NOT gate on a filtered
    /// run (raise-only); instead we surface a loud notice naming the count so
    /// the shortfall is visible without a false red.
    | NotGatedFiltered of belowFloorCount: int

[<NoComparison; NoEquality>]
type CoverageMsg =
    | CheckDone of verdict: CoverageVerdict * elapsed: System.TimeSpan
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

/// Decide the gated verdict from a raw ratchet `CheckResult` and what the run
/// established. Pure, so the gating policy is unit-testable without spinning a
/// daemon.
///
/// Takes the scope a run PROVED, not a bool. `FullSuite` gates normally; `Partial`
/// downgrades a `SomeFailed` to a non-gating notice, because un-run files cannot be
/// distinguished from genuine zeros. A run that executed nothing has no `RunScope`
/// to pass, so it cannot reach here at all and cannot be mistaken for a filtered
/// one — the caller has already had to decide what to do about it.
let internal gateVerdict (scope: RunScope) (result: CheckResult) : CoverageVerdict =
    match result, scope with
    | AllPassed, _ -> Passed
    | SomeFailed results, FullSuite -> Failed results
    | SomeFailed results, Partial -> NotGatedFiltered results.Length

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

let private runCheck (configPath: string) (xmlPaths: string list) : CheckResult =
    check (loadConfig configPath) (parseFiles xmlPaths)

/// <summary>Create a CoveragePlugin handler that checks per-file line and branch coverage
/// thresholds after each <c>TestRunCompleted</c> event.</summary>
/// <param name="configPath">Path to the coverage-ratchet.json thresholds config.</param>
/// <param name="searchDir">Directory tree to search for <c>coverage.cobertura.xml</c> files.</param>
let create (configPath: string) (searchDir: string) : PluginHandler<bool option, CoverageMsg> =
    { Name = PluginName.create "coverage"
      Init = None
      Subscriptions = Set.singleton SubscribeTestRunCompleted
      CacheKey = None
      Teardown = None
      Commands =
        [ "coverage-ratchet",
          fun ctx _state args ->
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
              }

          "coverage-status",
          fun _ctx state _args ->
              async {
                  return
                      match state with
                      | None -> "coverage: no check run yet"
                      | Some true -> "coverage: OK"
                      | Some false -> "coverage: FAILED (run `fshw errors` for details)"
              } ]
      Update =
        fun ctx state event ->
            match event with
            | TestRunCompleted trc ->
                match trc.Outcome, RunVerification.scope trc.Verification with
                | Aborted _, _ -> async { return state }
                | Normal, None ->
                    // No scope established because the run executed nothing, so it
                    // produced no coverage and there is nothing to judge. Declining is
                    // the same move `Aborted` above makes, for the same reason.
                    ctx.Log "coverage check skipped — the run executed no tests, so it produced no coverage to judge"
                    async { return state }
                | Normal, Some scope ->
                    let claim =
                        ctx.RunExclusive
                            "coverage-check"
                            (async {
                                let runStarted = System.DateTime.UtcNow
                                let! xmlPaths = pollForFiles searchDir 50 100

                                let result =
                                    if List.isEmpty xmlPaths then
                                        AllPassed
                                    else
                                        runCheck configPath xmlPaths

                                // No baseline to refresh here: the TestPrune DB is the
                                // coverage high-watermark, ingested (max-merged across
                                // projects) per run, and emits the shared cobertura.
                                return CheckDone(gateVerdict scope result, System.DateTime.UtcNow - runStarted)
                            })

                    match claim with
                    | Claimed -> ()
                    | SlotBusy ->
                        // A check (or a ratchet rewrite) is already in flight;
                        // skipping is correct — the next TestRunCompleted
                        // re-checks against its own fresh cobertura output.
                        ctx.Log "coverage check already in flight — skipping this trigger"

                    async { return state }

            | Custom(RatchetRequested(cfgPath, reply)) ->
                let work =
                    async {
                        let runStarted = System.DateTime.UtcNow

                        try
                            try
                                let xmlPaths = findCoverageFiles searchDir

                                let message =
                                    if List.isEmpty xmlPaths then
                                        "coverage-ratchet: no coverage.cobertura.xml found"
                                    else
                                        let coverage = parseFiles xmlPaths
                                        let raw = loadRawConfig cfgPath
                                        let newRaw = ratchetRaw raw coverage
                                        saveRawConfig cfgPath newRaw
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

                    return state
                }

            | Custom(CheckDone(Passed, elapsed)) ->
                async {
                    ctx.ClearAllErrors()

                    ctx.ReportStatus(
                        PluginStatus.Completed(
                            System.DateTime.UtcNow,
                            RunVerdict.create "coverage floors passed" elapsed
                        )
                    )

                    return Some true
                }

            | Custom(CheckDone(NotGatedFiltered belowFloorCount, elapsed)) ->
                async {
                    // Clear any prior reds so the verdict is a deterministic ✓ on an
                    // unchanged commit; the notice below keeps the gap visible. A real
                    // regression is caught by the next full-suite run, which gates.
                    ctx.ClearAllErrors()

                    let summary =
                        $"%d{belowFloorCount} file(s) below floor not gated (impact-filtered run; run a full suite to gate coverage)"

                    ctx.Log $"coverage: %s{summary}"

                    ctx.ReportStatus(PluginStatus.Completed(System.DateTime.UtcNow, RunVerdict.create summary elapsed))

                    return Some true
                }

            | Custom(CheckDone(Failed results, elapsed)) ->
                async {
                    for r in results do
                        let lineMsg =
                            if not (FileResult.linePassed r) then
                                [ $"line=%.1f{r.File.LinePct}%% < min %.1f{r.LineThreshold}%%" ]
                            else
                                []

                        let branchMsg =
                            if not (FileResult.branchPassed r) then
                                [ $"branch=%.1f{r.File.BranchPct}%% < min %.1f{r.BranchThreshold}%%" ]
                            else
                                []

                        let detail = String.concat ", " (lineMsg @ branchMsg)
                        ctx.ReportErrors r.File.FileName [ ErrorEntry.error $"coverage: %s{detail}" ]

                    let summary = $"%d{results.Length} file(s) below threshold"
                    ctx.ReportStatus(PluginStatus.failedNow summary summary elapsed)
                    return Some false
                }

            | _ -> async { return state } }
