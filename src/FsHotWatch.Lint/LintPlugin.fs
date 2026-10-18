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
    { WarningsByFile: Map<AbsFilePath, string list> }

    // A replayed lint is recorded by the rule a live fold applies: only a result published
    // against the model published now is linted (see `notCurrent`), and a completed lint
    // replaces the file's warnings with the ones it reported. A failed one leaves them.
    interface IFileReplayState<LintState> with
        member this.Replayed _ currentModel result analysis =
            match analysis with
            | Result.Ok findings when result.ModelGeneration.IsSome && result.ModelGeneration = currentModel ->
                { WarningsByFile = this.WarningsByFile |> Map.add result.File (findings |> List.map _.Message) }
            | Result.Ok _
            | Result.Error _ -> this

/// A lint run that did not actually lint: FSharpLint failed inside, or never reached the
/// end of the file. Raised so the run reports FAILURE instead of a clean file.
type LintCoreFailedException(message: string, inner: exn) =
    inherit Exception(message, inner)

/// Run one FSharpLint invocation (`core`) and turn its silent failures into an exception.
///
/// FSharpLint catches every exception its linter meets (an FCS binary mismatch throws
/// `MissingMethodException` on every file, for example). It reports the exception ONLY as
/// `ProjectProgress.Failed` through `ReportLinterProgress`, then returns `Success []`. Read
/// alone, that result says "0 issues" about a file nothing linted. So a run that reported a
/// failure, or never reported reaching the end of the file, raises
/// `LintCoreFailedException` naming the cause. A `Failure` result is FSharpLint's own
/// failure channel and passes through unchanged.
let lintReportingFailures
    (lintParams: Lint.OptionalLintParameters)
    (core: Lint.OptionalLintParameters -> Lint.LintResult)
    : Lint.LintResult =
    let progress = System.Collections.Concurrent.ConcurrentQueue<Lint.ProjectProgress>()

    let report (p: Lint.ProjectProgress) =
        progress.Enqueue p
        lintParams.ReportLinterProgress |> Option.iter (fun forward -> forward p)

    let result =
        core
            { lintParams with
                ReportLinterProgress = Some report }

    match result with
    | Lint.LintResult.Failure _ -> result
    | Lint.LintResult.Success _ ->
        let failure =
            progress
            |> Seq.tryPick (fun p ->
                match p with
                | Lint.ProjectProgress.Failed(_, ex) -> Some ex
                | Lint.ProjectProgress.Starting _
                | Lint.ProjectProgress.ReachedEnd _ -> None)

        let reachedEnd =
            progress
            |> Seq.exists (fun p ->
                match p with
                | Lint.ProjectProgress.ReachedEnd _ -> true
                | Lint.ProjectProgress.Starting _
                | Lint.ProjectProgress.Failed _ -> false)

        match failure with
        | Some ex ->
            raise (
                LintCoreFailedException(
                    $"FSharpLint failed inside and linted nothing: %s{ex.GetType().FullName}: %s{ex.Message}",
                    ex
                )
            )
        | None when not reachedEnd ->
            raise (LintCoreFailedException("FSharpLint never reached the end of the file: nothing was linted", null))
        | None -> result

/// The message a failed lint reports: a core failure already names its cause, and any
/// other exception is named by its type.
let private describeLintException (ex: exn) =
    match ex with
    | :? LintCoreFailedException -> ex.Message
    | _ -> $"%s{ex.GetType().FullName}: %s{ex.Message}"


/// FSharpLint build identity embedded into cache keys so a tool upgrade invalidates
/// every entry without manual flushing. The module version id, not the assembly
/// version: the build FsHotWatch.Lint ships (bundled by the analyzer shim) is stamped
/// 1.0.0.0 whatever it contains, so a version would let a new build read an old one's
/// results.
let private fsharpLintVersion =
    typeof<FSharpLint.Application.Lint.OptionalLintParameters>.Assembly.ManifestModule.ModuleVersionId
    |> string

/// Bumped manually when this plugin's caching semantics change in a way that
/// previously-cached results must be discarded. Independent of FSharpLint's
/// own version.
[<Literal>]
// v2 orphans every entry written under the path-ABSOLUTE key (v1), which no other
// checkout could have read anyway.
let private PluginCacheSalt = "lint-merkle-v2"

/// The generation of the model the host currently publishes, when it is available.
/// `None` while no model is observable (never discovered, mid-rediscovery, or
/// unavailable): membership never outlives its model, so nothing can be shown to
/// belong to the model in force.
let private currentModelGeneration (ctx: PluginCtx<'Msg>) =
    match ctx.ProjectGraph.ObserveModel() with
    | FsHotWatch.ProjectModel.Observation.Available model -> Some model.Generation
    | FsHotWatch.ProjectModel.Observation.Unobserved
    | FsHotWatch.ProjectModel.Observation.Rediscovering _
    | FsHotWatch.ProjectModel.Observation.Unavailable _ -> None

/// What one reading of the fsharplint.json decides: the config's cache-key component,
/// its shape error, and whether its `ignoreFiles` excludes a file.
type private LoadedConfig =
    { Hash: string
      ShapeError: string option
      IsIgnored: string -> bool }

/// True when `configPath`'s `ignoreFiles` excludes a file, decided by FSharpLint's own
/// matcher. FSharpLint applies `ignoreFiles` only when it enumerates a project itself;
/// `lintParsedSource`, which this plugin calls per file, never consults it. A config
/// FSharpLint cannot load ignores nothing: the lint itself reports that failure.
let private ignoredBy (configPath: string) : string -> bool =
    let globs =
        Lint.getConfig (Lint.ConfigurationParam.FromFile configPath)
        |> Result.toOption
        |> Option.bind _.ignoreFiles

    match globs with
    | Some globs ->
        let parsed =
            globs
            |> Array.map FSharpLint.Framework.Configuration.IgnoreFiles.parseIgnorePath
            |> Array.toList

        fun file -> FSharpLint.Framework.Configuration.IgnoreFiles.shouldFileBeIgnored parsed (IO.Path.GetFullPath file)
    | None -> fun _ -> false

/// Creates a framework plugin handler that lints files using pre-parsed AST
/// and check results from the daemon's warm FSharpChecker. Cache key is
/// content-merkle (file source + tool/config hashes); jj commit_id is not
/// consulted.
let create
    (repoRoot: string option)
    (lintConfigPath: string option)
    (lintRunner: (FileCheckResult -> Lint.LintResult) option)
    (timeoutSec: int option)
    : PluginHandler<LintState, unit> =

    let lintTimeout =
        let secs = defaultArg timeoutSec LintTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    // The daemon outlives edits to fsharplint.json, so the file is re-read on every event
    // and everything derived from it is recomputed whenever its text changes. Read once at
    // start-up, an edit left results keyed, and files ignored, by settings it no longer held.
    let loadConfig (text: string option) : LoadedConfig =
        { Hash =
            match lintConfigPath, text with
            | Some _, Some text -> FsHotWatch.CheckCache.sha256Hex text
            | Some _, None -> "missing-config"
            | None, _ -> "no-config"
          // A config with keys FSharpLint binds to nothing would lint every file "clean"
          // for the rules under them. Fail every run, naming the keys, instead of linting.
          ShapeError = lintConfigPath |> Option.bind LintConfigShape.check
          IsIgnored =
            match lintConfigPath, text with
            | Some path, Some _ -> ignoredBy path
            | _ -> fun _ -> false }

    let loaded = ref (None: (string option * LoadedConfig) option)

    let currentConfig () =
        let text =
            lintConfigPath |> Option.filter IO.File.Exists |> Option.map IO.File.ReadAllText

        match loaded.Value with
        | Some(seen, config) when seen = text -> config
        | _ ->
            let config = loadConfig text
            loaded.Value <- Some(text, config)
            config

    let cacheKey (event: PluginEvent<unit>) : ContentHash option =
        match event with
        | FileChecked r ->
            // the FCS check signature, so a change to an upstream symbol's
            // signature invalidates this file's entry even though its own source
            // bytes are unchanged — a source-only key serves stale lint results.
            let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

            Some(
                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", PluginCacheSalt
                      "tool", fsharpLintVersion
                      "config", (currentConfig ()).Hash
                      // REPO-RELATIVE. FSharpLint's verdict depends on where a file
                      // sits in the repository (naming rules read the path), never on
                      // which checkout of that repository it was read from — so the
                      // absolute path that used to be here was salt, and it was the
                      // only reason two workspaces could not share a lint result.
                      "file", FsHotWatch.CachePathIdentity.keyOf repoRoot (AbsFilePath.value r.File)
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

    let lintFile =
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

            lintReportingFailures lintParams (fun p -> Lint.lintParsedSource p parsedInfo))

    let runLint (result: FileCheckResult) =
        let config = currentConfig ()
        let file = AbsFilePath.value result.File

        match config.ShapeError with
        | Some message -> Lint.LintResult.Failure(Lint.LintFailure.FailedToLoadConfig message)
        | None when config.IsIgnored file ->
            Logging.debug "lint" $"Skipping %s{file}: matched by ignoreFiles"
            Lint.LintResult.Success []
        | None -> lintFile result

    { Name = PluginName.create "lint"
      Init = { WarningsByFile = Map.empty }
      Update =
        fun ctx state event ->
            async {
                let modelGeneration = currentModelGeneration ctx

                // Lint only a result published against the model this host publishes
                // NOW. One from a replaced model was admitted before the replacement,
                // and one with no model was captured against none, so it cannot
                // describe this one.
                let notCurrent (published: int64 option) =
                    published.IsNone || published <> modelGeneration

                match event with
                // A rediscovery that drops a file clears that file's findings in every
                // plugin ledger, and it clears them BEFORE the new model is published.
                // Nothing checks a dropped path again, so an old-model result folded
                // after that clear re-reports a finding for a file that is no longer in
                // the build and nothing will ever clear it again. A file the new model
                // still has is re-checked against it and republishes, so refusing costs
                // that file nothing.
                | FileChecked result when notCurrent result.ModelGeneration ->
                    Logging.info
                        "lint"
                        $"ignoring FileChecked for %s{AbsFilePath.value result.File} from model %A{result.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | FileChecked result ->
                    let fileStr = AbsFilePath.value result.File

                    // Skip out-of-repo compile items (NuGet-injected `_content`
                    // etc.) — not ours to lint, and a latent crash surface.
                    // See PathFilter.isOutsideRepoScoped.
                    if FsHotWatch.PathFilter.isOutsideRepoScoped repoRoot fileStr then
                        Logging.debug "lint" $"Skipping out-of-repo compile item %s{fileStr}"
                        return state
                    else

                        Logging.debug "lint" $"FileChecked received: %s{fileStr}"
                        let elapsed = PluginCtxHelpers.beginRun ctx

                        return!
                            PluginCtxHelpers.withSubtask
                                ctx
                                fileStr
                                $"linting {System.IO.Path.GetFileName fileStr}"
                                (async {
                                    if isNull (box result.ParseResults) then
                                        Logging.warn "lint" $"Skipping %s{fileStr} — no parse results"
                                        return state
                                    else
                                        // A runner that throws (FSharpLint's binary mismatch with
                                        // FCS, a core failure surfaced by `lintReportingFailures`)
                                        // is a FAILED lint for this file, named by its exception.
                                        let lintOrError _ct =
                                            try
                                                Result.Ok(runLint result)
                                            with ex ->
                                                Result.Error ex

                                        let reportFailure (msg: string) =
                                            ctx.ReportErrors fileStr [ ErrorEntry.error msg ]

                                            ctx.ReportStatus(
                                                PluginStatus.failedNow
                                                    msg
                                                    $"lint failed on {System.IO.Path.GetFileName fileStr}"
                                                    (elapsed ())
                                            )

                                        match runWithCancellableTimeout lintTimeout lintOrError with
                                        | WorkTimedOut after ->
                                            let reason = $"timed out after %d{int after.TotalSeconds}s"
                                            Logging.error "lint" $"Lint TIMED OUT for %s{fileStr}: %s{reason}"

                                            PluginCtxHelpers.timedOutWith ctx "lint" reason after

                                            return state
                                        | WorkCompleted(Result.Ok(Lint.LintResult.Success warnings)) ->
                                            Logging.debug
                                                "lint"
                                                $"Linted %s{System.IO.Path.GetFileName fileStr}: %d{warnings.Length} warnings"

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

                                            PluginCtxHelpers.reportOrClearFile ctx fileStr entries

                                            let newState = { WarningsByFile = newWarnings }

                                            let totalIssues =
                                                newWarnings |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)

                                            PluginCtxHelpers.completeWith
                                                ctx
                                                $"linted {newWarnings.Count} files, {totalIssues} issues"
                                                (elapsed ())

                                            return newState
                                        | WorkCompleted(Result.Ok(Lint.LintResult.Failure failure)) ->
                                            reportFailure $"Lint failed for %s{fileStr}: %s{failure.Description}"
                                            return state
                                        | WorkCompleted(Result.Error ex) ->
                                            let msg = $"Lint failed for %s{fileStr}: %s{describeLintException ex}"
                                            Logging.error "lint" msg
                                            reportFailure msg
                                            return state
                                })
                | _ -> return state
            }
      PrepareCommit = None
      Commands =
        [ "warnings",
          PluginCommand.Observe(fun _ctx state _args ->
              async {
                  let current = state.WarningsByFile
                  let count = current |> Map.toList |> List.sumBy (fun (_, w) -> w.Length)
                  return $"{{\"files\": %d{current.Count}, \"warnings\": %d{count}}}"
              }) ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = Some(fun _state event -> cacheKey event)
      Teardown = None }
