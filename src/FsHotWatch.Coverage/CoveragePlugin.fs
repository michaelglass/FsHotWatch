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
    /// the shortfall is visible without a false red. Verdict-reliability
    /// 2026-06-02 Issue B.
    | NotGatedFiltered of belowFloorCount: int

type CoverageMsg = CheckDone of verdict: CoverageVerdict * elapsed: System.TimeSpan

/// Decide the gated verdict from a raw ratchet `CheckResult` and whether this
/// cycle ran the full suite. Pure, so the gating policy is unit-testable
/// without spinning a daemon. On a full-suite run the ratchet gates normally;
/// on an impact-filtered run a `SomeFailed` is downgraded to a non-gating
/// notice because un-run files cannot be distinguished from genuine zeros.
let internal gateVerdict (ranFullSuite: bool) (result: CheckResult) : CoverageVerdict =
    match result with
    | AllPassed -> Passed
    | SomeFailed results ->
        if ranFullSuite then
            Failed results
        else
            NotGatedFiltered results.Length

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
                  let cfgPath =
                      if args.Length > 0 && args.[0] <> "" then
                          args.[0]
                      else
                          configPath

                  let xmlPaths = findCoverageFiles searchDir

                  if List.isEmpty xmlPaths then
                      return "coverage-ratchet: no coverage.cobertura.xml found"
                  else
                      let coverage = parseFiles xmlPaths
                      let raw = loadRawConfig cfgPath
                      let newRaw = ratchetRaw raw coverage
                      saveRawConfig cfgPath newRaw
                      ctx.Log $"coverage-ratchet: updated %s{cfgPath}"
                      return $"coverage-ratchet: thresholds updated in %s{cfgPath}"
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
                match trc.Outcome with
                | Aborted _ -> async { return state }
                | Normal ->
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

                            // The TestPrune DB is the coverage high-watermark now:
                            // each run ingests into it (max-merge across projects)
                            // and emits the single shared cobertura. There is no
                            // separate baseline to refresh here.
                            return CheckDone(gateVerdict trc.RanFullSuite result, System.DateTime.UtcNow - runStarted)
                        })

                    async { return state }

            | Custom(CheckDone(Passed, elapsed)) ->
                async {
                    ctx.ClearAllErrors()

                    ctx.ReportStatus(
                        PluginStatus.Completed(
                            System.DateTime.UtcNow,
                            { Summary = "coverage floors passed"
                              Elapsed = elapsed }
                        )
                    )

                    return Some true
                }

            | Custom(CheckDone(NotGatedFiltered belowFloorCount, elapsed)) ->
                async {
                    // Impact-filtered run: do NOT gate. The shortfalls are very likely
                    // un-run files reading a false 0.0% (no current coverage, stale/missing
                    // baseline) — indistinguishable in the cobertura data from a genuine
                    // zero. Clear any prior reds so the verdict is a deterministic ✓ on an
                    // unchanged commit, and surface a LOUD notice so the gap stays visible.
                    // A real regression is caught by the next full-suite run, which gates.
                    ctx.ClearAllErrors()

                    let summary =
                        $"%d{belowFloorCount} file(s) below floor not gated (impact-filtered run; run a full suite to gate coverage)"

                    ctx.Log $"coverage: %s{summary}"

                    ctx.ReportStatus(
                        PluginStatus.Completed(System.DateTime.UtcNow, { Summary = summary; Elapsed = elapsed })
                    )

                    return Some true
                }

            | Custom(CheckDone(Failed results, _elapsed)) ->
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
                    ctx.ReportStatus(PluginStatus.Failed(summary, System.DateTime.Now))
                    return Some false
                }

            | _ -> async { return state } }
