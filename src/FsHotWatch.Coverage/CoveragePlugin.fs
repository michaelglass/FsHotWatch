module FsHotWatch.Coverage.CoveragePlugin

open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

type CoverageMsg =
    | CheckDone of failedFiles: (string * string) list

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

let private runCheck (configPath: string) (xmlPaths: string list) =
    async {
        let coverage = parseFiles xmlPaths
        let config = loadConfig configPath
        let results = buildFileResults config coverage

        return
            results
            |> List.choose (fun r ->
                if FileResult.passed r then
                    None
                else
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
                    Some(r.File.FileName, $"coverage: %s{detail}"))
    }

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
                      | Some false -> "coverage: FAILED (run `fshw errors` for details)" } ]
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
                              let! xmlPaths = pollForFiles searchDir 50 100

                              if List.isEmpty xmlPaths then
                                  return CheckDone []
                              else
                                  let! failures = runCheck configPath xmlPaths
                                  return CheckDone failures
                          })

                      async { return state }

              | Custom(CheckDone failures) ->
                  async {
                      if List.isEmpty failures then
                          ctx.ClearAllErrors()
                          ctx.ReportStatus(PluginStatus.Completed System.DateTime.Now)
                          return Some true
                      else
                          for (file, msg) in failures do
                              ctx.ReportErrors file [ ErrorEntry.error msg ]

                          let summary = $"%d{failures.Length} file(s) below threshold"
                          ctx.ReportStatus(PluginStatus.Failed(summary, System.DateTime.Now))
                          return Some false
                  }

              | _ -> async { return state } }
