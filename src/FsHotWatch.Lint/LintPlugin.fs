module FsHotWatch.Lint.LintPlugin

open FsHotWatch.AgentHost
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.ErrorLedger
open FSharpLint.Application

type private LintState =
    { WarningsByFile: Map<string, string list> }

type private LintMsg = FileChecked of FileCheckResult

/// FSharpLint plugin — lints files using pre-parsed AST and check results
/// from the daemon's warm FSharpChecker. Uses lintParsedSource to avoid
/// re-parsing files that have already been type-checked.
type LintPlugin(?configPath: string) =
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

            let agent =
                createAgent "lint" { WarningsByFile = Map.empty } (fun state msg ->
                    async {
                        match msg with
                        | FileChecked result ->
                            Logging.debug "lint" $"FileChecked received: %s{result.File}"
                            ctx.ReportStatus(Running(since = System.DateTime.UtcNow))

                            if isNull (box result.ParseResults) then
                                Logging.warn "lint" $"Skipping %s{result.File} — no parse results"
                                return state
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

                                    let newWarnings = state.WarningsByFile |> Map.add result.File msgs

                                    if warnings.IsEmpty then
                                        ctx.ClearErrors result.File
                                    else
                                        let entries =
                                            warnings
                                            |> List.map (fun w ->
                                                { Message = w.Details.Message
                                                  Severity = DiagnosticSeverity.Warning
                                                  Line = w.Details.Range.StartLine
                                                  Column = w.Details.Range.StartColumn })

                                        ctx.ReportErrors result.File entries

                                    let newState = { WarningsByFile = newWarnings }
                                    ctx.ReportStatus(Completed(System.DateTime.UtcNow))
                                    return newState
                                | Lint.LintResult.Failure failure ->
                                    let msg = $"Lint failed for %s{result.File}: %A{failure}"
                                    ctx.ReportStatus(PluginStatus.Failed(msg, System.DateTime.UtcNow))
                                    return state
                    })

            ctx.OnFileChecked.Add(fun result ->
                agent.Post(FileChecked result)
                agent.GetState() |> Async.RunSynchronously |> ignore)

            ctx.RegisterCommand(
                "warnings",
                fun _args ->
                    async {
                        let! state = agent.GetState()
                        let current = state.WarningsByFile

                        let count = current |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)

                        return $"{{\"files\": %d{current.Count}, \"warnings\": %d{count}}}"
                    }
            )

        member _.Dispose() = ()
