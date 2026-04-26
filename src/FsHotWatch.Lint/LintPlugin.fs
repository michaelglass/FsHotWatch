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

/// FSharpLint version embedded into cache keys so a tool upgrade invalidates
/// every entry without manual flushing.
let private fsharpLintVersion =
    typeof<FSharpLint.Application.Lint.OptionalLintParameters>.Assembly.GetName().Version
    |> string

/// Bumped manually when this plugin's caching semantics change in a way that
/// previously-cached results must be discarded. Independent of FSharpLint's
/// own version.
[<Literal>]
let private pluginCacheSalt = "lint-merkle-v1"

/// Creates a framework plugin handler that lints files using pre-parsed AST
/// and check results from the daemon's warm FSharpChecker.
///
/// `getCommitId` is retained for backward compatibility but unused: the cache
/// key is now content-merkle (file source + tool/config hashes), so the cache
/// is reachable across commits when file content reverts. See design doc §2a.
let create
    (lintConfigPath: string option)
    (getCommitId: (unit -> string option) option)
    (lintRunner: (FileCheckResult -> Lint.LintResult) option)
    (timeoutSec: int option)
    : PluginHandler<LintState, unit> =
    ignore getCommitId

    let lintTimeout =
        let secs = defaultArg timeoutSec LintTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    let configHash =
        match lintConfigPath with
        | Some path when System.IO.File.Exists path -> FsHotWatch.CheckCache.sha256Hex (System.IO.File.ReadAllText path)
        | Some _ -> "missing-config"
        | None -> "no-config"

    let cacheKey (event: PluginEvent<unit>) : ContentHash option =
        match event with
        | FileChecked r ->
            // §1: include FCS check signature so cross-file changes that shift
            // FCS's view of this file invalidate the cache, even when the file's
            // own source bytes are unchanged. Without this, a change to an
            // upstream symbol's signature would let stale lint results serve
            // through cache hits keyed on source-only.
            let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

            Some(
                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", pluginCacheSalt
                      "tool", fsharpLintVersion
                      "config", configHash
                      "file", r.File
                      "source", r.Source
                      "fcs-signature", fcsSignature ]
            )
        | _ -> None


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

                                        let entries =
                                            warnings
                                            |> List.map (fun w ->
                                                { Message = w.Details.Message
                                                  Severity = DiagnosticSeverity.Warning
                                                  Line = w.Details.Range.StartLine
                                                  Column = w.Details.Range.StartColumn
                                                  Detail = None })

                                        PluginCtxHelpers.reportOrClearFile ctx result.File entries

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
      CacheKey = Some cacheKey
      Teardown = None }
