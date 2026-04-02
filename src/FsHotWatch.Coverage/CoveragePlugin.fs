module FsHotWatch.Coverage.CoveragePlugin

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Logging
open FsHotWatch.PluginFramework

/// Line and branch coverage thresholds for a project.
type CoverageThreshold = { Line: float; Branch: float }

/// Coverage result for a single project.
type CoverageResult =
    {
        /// The project name derived from the coverage report directory.
        Project: string
        /// Line coverage percentage (0-100).
        LineRate: float
        /// Branch coverage percentage (0-100).
        BranchRate: float
        /// Whether both line and branch rates meet the configured threshold.
        MeetsThreshold: bool
    }

type CoverageState = { Results: CoverageResult list }

let private loadThresholds (thresholdsFile: string option) : Map<string, CoverageThreshold> =
    match thresholdsFile with
    | Some path when File.Exists(path) ->
        try
            let json = File.ReadAllText(path)
            let doc = JsonDocument.Parse(json)

            doc.RootElement.EnumerateObject()
            |> Seq.map (fun prop ->
                let line =
                    match prop.Value.TryGetProperty("line") with
                    | true, el -> el.GetDouble()
                    | false, _ -> 0.0

                let branch =
                    match prop.Value.TryGetProperty("branch") with
                    | true, el -> el.GetDouble()
                    | false, _ -> 0.0

                prop.Name, { Line = line; Branch = branch })
            |> Map.ofSeq
        with ex ->
            Logging.warn "coverage" $"Failed to parse thresholds file: %s{ex.Message}"
            Map.empty
    | _ -> Map.empty

let private parseCoberturaXml (path: string) : (float * float) option =
    try
        let doc = XDocument.Load(path)
        let root = doc.Root

        let lineRate =
            match root.Attribute(XName.Get "line-rate") with
            | null -> 0.0
            | attr -> Double.Parse(attr.Value) * 100.0

        let branchRate =
            match root.Attribute(XName.Get "branch-rate") with
            | null -> 0.0
            | attr -> Double.Parse(attr.Value) * 100.0

        Some(lineRate, branchRate)
    with ex ->
        Logging.warn "coverage" $"Failed to parse Cobertura XML %s{path}: %s{ex.Message}"
        None

/// Creates a framework plugin handler that checks coverage thresholds after tests complete.
let create
    (coverageDir: string)
    (thresholdsFile: string option)
    (afterCheck: (unit -> unit) option)
    : PluginHandler<CoverageState, unit> =
    { Name = "coverage"
      Init = { Results = [] }
      Update =
        fun ctx state event ->
            async {
                match event with
                | TestCompleted testResults ->
                    if testResults.Results.IsEmpty then
                        Logging.warn "coverage" "No test results, skipping coverage check"
                        return state
                    else
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        try
                            let thresholds = loadThresholds thresholdsFile

                            let coverageFiles =
                                if Directory.Exists(coverageDir) then
                                    Directory.GetFiles(coverageDir, "*.xml", SearchOption.AllDirectories)
                                    |> Array.filter (fun f ->
                                        f.EndsWith("cobertura.xml", StringComparison.OrdinalIgnoreCase))
                                    |> Array.toList
                                else
                                    []

                            let results =
                                coverageFiles
                                |> List.choose (fun xmlPath ->
                                    match parseCoberturaXml xmlPath with
                                    | Some(lineRate, branchRate) ->
                                        let projectName = Path.GetDirectoryName(xmlPath) |> Path.GetFileName

                                        let meetsThreshold =
                                            match thresholds |> Map.tryFind projectName with
                                            | Some threshold ->
                                                lineRate >= threshold.Line && branchRate >= threshold.Branch
                                            | None -> true

                                        Some
                                            { Project = projectName
                                              LineRate = lineRate
                                              BranchRate = branchRate
                                              MeetsThreshold = meetsThreshold }
                                    | None -> None)

                            if coverageFiles.IsEmpty then
                                ctx.ReportStatus(
                                    PluginStatus.Failed($"No coverage files found in %s{coverageDir}", DateTime.UtcNow)
                                )

                                return { Results = results }
                            else
                                let allPass = results |> List.forall (fun r -> r.MeetsThreshold)

                                match afterCheck with
                                | Some hook -> hook ()
                                | None -> ()

                                if allPass then
                                    ctx.ClearErrors "<coverage>"
                                    ctx.ReportStatus(Completed(DateTime.UtcNow))
                                else
                                    let failedResults = results |> List.filter (fun r -> not r.MeetsThreshold)

                                    let entries =
                                        failedResults
                                        |> List.map (fun r ->
                                            ErrorEntry.error
                                                $"%s{r.Project}: line=%.1f{r.LineRate}%% branch=%.1f{r.BranchRate}%%")

                                    ctx.ReportErrors "<coverage>" entries

                                    let failures = entries |> List.map (fun e -> e.Message) |> String.concat "; "

                                    ctx.ReportStatus(
                                        PluginStatus.Failed($"Coverage below threshold: %s{failures}", DateTime.UtcNow)
                                    )

                                return { Results = results }
                        with ex ->
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                            return state
                | _ -> return state
            }
      Commands =
        [ "coverage",
          fun state _args ->
              async {
                  let entries =
                      state.Results
                      |> List.map (fun r ->
                          $"\"%s{r.Project}\": {{\"line\": %.1f{r.LineRate}, \"branch\": %.1f{r.BranchRate}, \"pass\": %b{r.MeetsThreshold}}}")
                      |> String.concat ", "

                  return $"{{%s{entries}}}"
              } ]
      Subscriptions =
        { PluginSubscriptions.none with
            TestCompleted = true }
      CacheKey = None }
