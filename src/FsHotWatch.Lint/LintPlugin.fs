module FsHotWatch.Lint.LintPlugin

open System
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Logging
open FsHotWatch.ErrorLedger
open FsHotWatch.ProcessHelper
open FSharpLint.Application

/// Default per-event lint timeout (seconds). Used when no override is
/// configured. Chosen to match DaemonConfig.LintTimeoutDefaultSec.
[<Literal>]
let LintTimeoutDefaultSec = 120

type LintState =
    { WarningsByFile: Map<string, string list> }

// TODO: lintParsedSource misses hint-based rules (e.g. FL0065 "x = [] ===> List.isEmpty x")
// that the solution-level linter (dotnet fsharplint lint) catches. Investigate using
// Lint.lintProject with a warm FSharpChecker instead of per-file lintParsedSource.
// See ../FsharpLint for the FSharpLint.Core source.

/// Creates a framework plugin handler that lints files using pre-parsed AST
/// and check results from the daemon's warm FSharpChecker.
let create
    (lintConfigPath: string option)
    (getCommitId: (unit -> string option) option)
    (lintRunner: (FileCheckResult -> Lint.LintResult) option)
    (timeoutSec: int option)
    : PluginHandler<LintState, unit> =

    let lintTimeout =
        let secs = defaultArg timeoutSec LintTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)


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

    let runLint =
        defaultArg lintRunner (fun result ->
            let typeCheckResults =
                match result.CheckResults with
                | FullCheck r -> Some r
                | ParseOnly -> None

            let parsedInfo: Lint.ParsedFileInformation =
                { Ast = result.ParseResults.ParseTree
                  Source = result.Source
                  TypeCheckResults = typeCheckResults
                  ProjectCheckResults = None }

            Lint.lintParsedSource lintParams parsedInfo)

    { Name = PluginName.create "lint"
      Init = { WarningsByFile = Map.empty }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    Logging.debug "lint" $"FileChecked received: %s{result.File}"
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    return!
                        PluginCtxHelpers.withSubtask
                            ctx
                            result.File
                            $"linting {System.IO.Path.GetFileName result.File}"
                            (async {
                                if isNull (box result.ParseResults) then
                                    Logging.warn "lint" $"Skipping %s{result.File} — no parse results"
                                    return state
                                else
                                    match runWithTimeout lintTimeout (fun () -> runLint result) with
                                    | WorkTimedOut after ->
                                        let reason = $"timed out after %d{int after.TotalSeconds}s"
                                        Logging.error "lint" $"Lint TIMED OUT for %s{result.File}: %s{reason}"

                                        ctx.CompleteWithTimeout reason

                                        ctx.ReportStatus(
                                            PluginStatus.Failed($"lint timed out: {reason}", DateTime.UtcNow)
                                        )

                                        return state
                                    | WorkCompleted(Lint.LintResult.Success warnings) ->
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

                                        let totalIssues =
                                            newWarnings |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)

                                        PluginCtxHelpers.completeWith
                                            ctx
                                            $"linted {newWarnings.Count} files, {totalIssues} issues"

                                        return newState
                                    | WorkCompleted(Lint.LintResult.Failure failure) ->
                                        let msg = $"Lint failed for %s{result.File}: %A{failure}"

                                        ctx.ReportErrors result.File [ ErrorEntry.error msg ]

                                        ctx.CompleteWithSummary
                                            $"lint failed on {System.IO.Path.GetFileName result.File}"

                                        ctx.ReportStatus(PluginStatus.Failed(msg, DateTime.UtcNow))
                                        return state
                            })
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
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId
      Teardown = None }
