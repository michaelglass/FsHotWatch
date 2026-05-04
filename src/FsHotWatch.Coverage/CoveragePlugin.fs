module FsHotWatch.Coverage.CoveragePlugin

open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open CoverageRatchet.Cobertura
open CoverageRatchet.Merge
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

type CoverageMsg = CheckDone of CheckResult

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

                  mergeIntoBaselines searchDir
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
                            mergeIntoBaselines searchDir
                            let! xmlPaths = pollForFiles searchDir 50 100

                            let result =
                                if List.isEmpty xmlPaths then
                                    AllPassed
                                else
                                    runCheck configPath xmlPaths

                            if trc.RanFullSuite then
                                match result with
                                | AllPassed -> refreshBaselines searchDir
                                | SomeFailed _ -> ()

                            return CheckDone result
                        })

                    async { return state }

            | Custom(CheckDone AllPassed) ->
                async {
                    ctx.ClearAllErrors()
                    ctx.ReportStatus(PluginStatus.Completed System.DateTime.Now)
                    return Some true
                }

            | Custom(CheckDone(SomeFailed results)) ->
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
