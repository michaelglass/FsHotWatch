module FsHotWatch.Lint.LintPlugin

open FsHotWatch.Events
open FsHotWatch.Plugin
open FSharpLint.Application

/// FSharpLint plugin — lints files using pre-parsed AST and check results
/// from the daemon's warm FSharpChecker. Uses lintParsedSource to avoid
/// re-parsing files that have already been type-checked.
type LintPlugin(?configPath: string) =
    let mutable warningsByFile: Map<string, string list> = Map.empty

    let lintParams =
        match configPath with
        | Some path ->
            { Lint.OptionalLintParameters.Default with
                Configuration = Lint.ConfigurationParam.FromFile path }
        | None -> Lint.OptionalLintParameters.Default

    interface IFsHotWatchPlugin with
        member _.Name = "lint"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = System.DateTime.UtcNow))

                try
                    let parsedInfo: Lint.ParsedFileInformation =
                        { Ast = result.ParseResults.ParseTree
                          Source = result.Source
                          TypeCheckResults = Some result.CheckResults
                          ProjectCheckResults = None }

                    match Lint.lintParsedSource lintParams parsedInfo with
                    | Lint.LintResult.Success warnings ->
                        let msgs = warnings |> List.map (fun w -> w.Details.Message)
                        warningsByFile <- warningsByFile |> Map.add result.File msgs
                        ctx.ReportStatus(Completed(box warningsByFile, System.DateTime.UtcNow))
                    | Lint.LintResult.Failure failure ->
                        let msg = $"Lint failed for %s{result.File}: %A{failure}"
                        ctx.ReportStatus(PluginStatus.Failed(msg, System.DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, System.DateTime.UtcNow)))

            ctx.RegisterCommand(
                "warnings",
                fun _args ->
                    async {
                        let count =
                            warningsByFile |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)

                        return $"{{\"files\": %d{warningsByFile.Count}, \"warnings\": %d{count}}}"
                    }
            )

        member _.Dispose() = ()
