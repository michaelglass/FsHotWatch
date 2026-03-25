module FsHotWatch.Coverage.CoveragePlugin

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open FsHotWatch.Events
open FsHotWatch.Plugin

type CoverageThreshold = { Line: float; Branch: float }

type CoverageResult =
    { Project: string
      LineRate: float
      BranchRate: float
      MeetsThreshold: bool }

/// Checks coverage thresholds after tests complete.
/// Reads Cobertura XML reports from coverageDir and compares against thresholds.
type CoveragePlugin(coverageDir: string, ?thresholdsFile: string, ?afterCheck: unit -> unit) =
    let mutable lastResults: CoverageResult list = []

    let loadThresholds () : Map<string, CoverageThreshold> =
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
            with _ ->
                Map.empty
        | _ -> Map.empty

    let parseCoberturaXml (path: string) : (float * float) option =
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
        with _ ->
            None

    interface IFsHotWatchPlugin with
        member _.Name = "coverage"

        member _.Initialize(ctx) =
            ctx.OnTestCompleted.Add(fun testResults ->
                if testResults.Results.IsEmpty then
                    eprintfn "  [coverage] No test results, skipping coverage check"
                else
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    try
                        let thresholds = loadThresholds ()

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
                                    let projectName =
                                        Path.GetDirectoryName(xmlPath) |> Path.GetFileName

                                    let meetsThreshold =
                                        match thresholds |> Map.tryFind projectName with
                                        | Some threshold ->
                                            lineRate >= threshold.Line
                                            && branchRate >= threshold.Branch
                                        | None -> true

                                    Some
                                        { Project = projectName
                                          LineRate = lineRate
                                          BranchRate = branchRate
                                          MeetsThreshold = meetsThreshold }
                                | None -> None)

                        lastResults <- results

                        if coverageFiles.IsEmpty then
                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"No coverage files found in %s{coverageDir}",
                                    DateTime.UtcNow
                                )
                            )
                        else
                            let allPass =
                                results |> List.forall (fun r -> r.MeetsThreshold)

                            match afterCheck with
                            | Some hook -> hook ()
                            | None -> ()

                            if allPass then
                                ctx.ReportStatus(Completed(box results, DateTime.UtcNow))
                            else
                                let failures =
                                    results
                                    |> List.filter (fun r -> not r.MeetsThreshold)
                                    |> List.map (fun r ->
                                        $"%s{r.Project}: line=%.1f{r.LineRate}%% branch=%.1f{r.BranchRate}%%")
                                    |> String.concat "; "

                                ctx.ReportStatus(
                                    PluginStatus.Failed(
                                        $"Coverage below threshold: %s{failures}",
                                        DateTime.UtcNow
                                    )
                                )
                    with ex ->
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            ctx.RegisterCommand(
                "coverage",
                fun _args ->
                    async {
                        let entries =
                            lastResults
                            |> List.map (fun r ->
                                $"\"%s{r.Project}\": {{\"line\": %.1f{r.LineRate}, \"branch\": %.1f{r.BranchRate}, \"pass\": %b{r.MeetsThreshold}}}")
                            |> String.concat ", "

                        return $"{{%s{entries}}}"
                    }
            )

        member _.Dispose() = ()
