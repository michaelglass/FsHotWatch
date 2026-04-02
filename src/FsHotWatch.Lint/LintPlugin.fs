module FsHotWatch.Lint.LintPlugin

open System
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Logging
open FsHotWatch.ErrorLedger
open FSharpLint.Application

type LintState =
    { WarningsByFile: Map<string, string list> }

/// Creates a framework plugin handler that lints files using pre-parsed AST
/// and check results from the daemon's warm FSharpChecker.
let create (lintConfigPath: string option) : PluginHandler<LintState, unit> =
    let lintParams =
        try
            match lintConfigPath with
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

    { Name = "lint"
      Init = { WarningsByFile = Map.empty }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    Logging.debug "lint" $"FileChecked received: %s{result.File}"
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    if isNull (box result.ParseResults) then
                        Logging.warn "lint" $"Skipping %s{result.File} — no parse results"
                        return state
                    else

                        let typeCheckResults = result.CheckResults

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
                                          Column = w.Details.Range.StartColumn
                                          Detail = None })

                                ctx.ReportErrors result.File entries

                            let newState = { WarningsByFile = newWarnings }
                            ctx.ReportStatus(Completed(DateTime.UtcNow))
                            return newState
                        | Lint.LintResult.Failure failure ->
                            let msg = $"Lint failed for %s{result.File}: %A{failure}"

                            ctx.ReportErrors result.File [ ErrorEntry.error msg ]

                            ctx.ReportStatus(PluginStatus.Failed(msg, DateTime.UtcNow))
                            return state
                | _ -> return state
            }
      Commands =
        [ "warnings",
          fun state _args ->
              async {
                  let current = state.WarningsByFile
                  let count = current |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)
                  return $"{{\"files\": %d{current.Count}, \"warnings\": %d{count}}}"
              } ]
      Subscriptions =
        { PluginSubscriptions.none with
            FileChecked = true }
      CacheKey = None }
