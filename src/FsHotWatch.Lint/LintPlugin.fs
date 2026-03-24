module FsHotWatch.Lint.LintPlugin

open System.IO
open FsHotWatch.Events
open FsHotWatch.Plugin
open FSharpLint.Application

/// FSharpLint plugin — lints source files on FileChecked events.
/// Uses FSharpLint.Core's lintSource API with default configuration.
///
/// Note: The standard FSharpLint.Core 0.26.10 doesn't support passing
/// a shared FSharpChecker. A future version with the Checker field on
/// OptionalLintParameters would allow sharing the warm checker.
type LintPlugin() =
    let mutable warningsByFile: Map<string, string list> = Map.empty

    interface IFsHotWatchPlugin with
        member _.Name = "lint"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = System.DateTime.UtcNow))

                if File.Exists(result.File) then
                    try
                        let source = File.ReadAllText(result.File)

                        match Lint.lintSource Lint.OptionalLintParameters.Default source with
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
