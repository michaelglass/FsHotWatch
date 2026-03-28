module FsHotWatch.Lint.LintPlugin

open System.Threading
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.ErrorLedger
open FSharpLint.Application

/// FSharpLint plugin — lints files using pre-parsed AST and check results
/// from the daemon's warm FSharpChecker. Uses lintParsedSource to avoid
/// re-parsing files that have already been type-checked.
type LintPlugin(?configPath: string) =
    let mutable warningsByFile: Map<string, string list> = Map.empty

    let lintParams =
        try
            match configPath with
            | Some path ->
                Logging.info "lint" $"Loading config from: %s{path}"

                { Lint.OptionalLintParameters.Default with
                    Configuration = Lint.ConfigurationParam.FromFile path }
            | None ->
                Logging.info "lint" "Using default lint config"
                Lint.OptionalLintParameters.Default
        with ex ->
            Logging.error "lint" $"Failed to load lint config: %s{ex.Message} — using defaults"
            Lint.OptionalLintParameters.Default

    interface IFsHotWatchPlugin with
        member _.Name = "lint"

        member _.Initialize(ctx) =
            Logging.info "lint" "Subscribing to OnFileChecked"

            ctx.OnFileChecked.Add(fun result ->
                Logging.debug "lint" $"FileChecked received: %s{result.File}"
                ctx.ReportStatus(Running(since = System.DateTime.UtcNow))

                try
                    if isNull (box result.ParseResults) then
                        Logging.warn "lint" $"Skipping %s{result.File} — no parse results"
                    else

                        let typeCheckResults =
                            if isNull (box result.CheckResults) then
                                None
                            else
                                Some result.CheckResults

                        let parsedInfo: Lint.ParsedFileInformation =
                            { Ast = result.ParseResults.ParseTree
                              Source = result.Source
                              TypeCheckResults = typeCheckResults
                              ProjectCheckResults = None }

                        match Lint.lintParsedSource lintParams parsedInfo with
                        | Lint.LintResult.Success warnings ->
                            Logging.debug
                                "lint"
                                $"Linted %s{System.IO.Path.GetFileName result.File}: %d{warnings.Length} warnings"

                            let msgs = warnings |> List.map (fun w -> w.Details.Message)
                            let current = Volatile.Read(&warningsByFile)
                            Volatile.Write(&warningsByFile, current |> Map.add result.File msgs)

                            if warnings.IsEmpty then
                                ctx.ClearErrors result.File
                            else
                                let entries =
                                    warnings
                                    |> List.map (fun w ->
                                        { Message = w.Details.Message
                                          Severity = "warning"
                                          Line = w.Details.Range.StartLine
                                          Column = w.Details.Range.StartColumn })

                                ctx.ReportErrors result.File entries

                            ctx.ReportStatus(Completed(box (Volatile.Read(&warningsByFile)), System.DateTime.UtcNow))
                        | Lint.LintResult.Failure failure ->
                            let msg = $"Lint failed for %s{result.File}: %A{failure}"
                            ctx.ReportStatus(PluginStatus.Failed(msg, System.DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, System.DateTime.UtcNow)))

            ctx.RegisterCommand(
                "warnings",
                fun _args ->
                    async {
                        let current = Volatile.Read(&warningsByFile)

                        let count = current |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)

                        return $"{{\"files\": %d{current.Count}, \"warnings\": %d{count}}}"
                    }
            )

        member _.Dispose() = ()
