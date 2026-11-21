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
    {
        WarningsByFile: Map<AbsFilePath, string list>
        /// Each file's findings as the ledger holds them, by the same rule as `WarningsByFile`:
        /// what the nightly shadow compares reuse's answer with (AUTOMATION-1816 step 2b).
        FindingsByFile: Map<AbsFilePath, ErrorEntry list>
    }

    // Out-of-repo compile items are never linted (see the `FileChecked` arm).
    interface ISkipsOutOfRepoFiles

    // A replayed lint is recorded by the rule a live fold applies: only a result published
    // against the model published now is linted (see `notCurrent`), and a completed lint
    // replaces the file's warnings with the ones it reported. A failed one leaves them.
    interface IFileReplayState<LintState> with
        member this.Replayed _ currentModel result analysis =
            match analysis with
            | Result.Ok findings when result.ModelGeneration.IsSome && result.ModelGeneration = currentModel ->
                { WarningsByFile = this.WarningsByFile |> Map.add result.File (findings |> List.map _.Message)
                  FindingsByFile = this.FindingsByFile |> Map.add result.File findings }
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
// checkout could have read anyway. v3 orphans every entry keyed without the shape of
// what the file resolved upstream (`used-signatures`).
let private PluginCacheSalt = "lint-merkle-v3"

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

/// The labelled inputs of lint's cache key for one check, BEFORE the two that describe what the
/// check resolved (`used-signatures`, `fcs-signature`), in the order `merkleCacheKey` folds them.
type private KeyBase = (string * string) list

/// Lint's cache key: `keyBase`, then the check's used signatures and FCS signature. The one
/// definition both the live key and the key reuse would have read are made by.
let private keyOf (keyBase: KeyBase) (usedSignatures: string) (fcsSignature: string) : ContentHash =
    FsHotWatch.TaskCache.merkleCacheKey (
        keyBase
        @ [
            // A typed rule (NoPartialFunctions, say) reads the types this
            // file resolved in other files. Neither the source nor the
            // file's own diagnostics move when one of those changes shape
            // and the file still compiles.
            "used-signatures", usedSignatures
            // The file's own diagnostics: a check that newly fails or
            // recovers changes what the typed tree holds.
            "fcs-signature", fcsSignature ]
    )

/// What the nightly shadow keeps of one file's latest lint: its key base, the key this run built
/// from it, and the options it was checked under (what the spot-check re-checks it with).
[<NoComparison; NoEquality>]
type private Keyed =
    { Base: KeyBase
      Key: ContentHash
      Options: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions }

/// Each file's `Keyed`, kept only for the nightly shadow, with what reads the entries lint's writes
/// displaced and how the spot-check gets a live check.
[<NoComparison; NoEquality>]
type private ReuseKeyBases =
    | NotKept
    | Kept of
        bases: Collections.Concurrent.ConcurrentDictionary<AbsFilePath, Keyed> *
        displaced: FsHotWatch.TaskCache.DisplacedEntries *
        recheck: FsHotWatch.ReferencesShadowReport.Recheck

/// Lint's part in the nightly shadow (AUTOMATION-1816 step 2b, the daemon's `cache.nightlyShadow`).
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type Shadow =
    | Off
    /// Read the entries lint's writes displaced, and re-check sampled `sameKey` files live with
    /// `recheck` to lint them again with the cache bypassed.
    | On of displaced: FsHotWatch.TaskCache.DisplacedEntries * recheck: FsHotWatch.ReferencesShadowReport.Recheck

/// The ledger entries for FSharpLint's `warnings` on one file.
let private entriesOf (warnings: FSharpLint.Framework.Suggestion.LintWarning list) : ErrorEntry list =
    warnings
    |> List.map (fun w ->
        { Message = w.Details.Message
          Severity = DiagnosticSeverity.Warning
          Line = w.Details.Range.StartLine
          Column = w.Details.Range.StartColumn
          Code = None
          Detail = None })

/// AUTOMATION-1816 step 2b: what reuse would have handed lint for `servable`, read from the entry
/// lint's own write displaced. Reuse would have read the key built from this run's key base and
/// the record's signatures. When that IS the key this run built, this run's result may be the
/// cached entry itself: `SameKey`, never compared. Otherwise the displaced entry answers only when
/// it was filed under exactly the reused key; any other key, or nothing displaced, is
/// unverifiable, never equal.
let private reusedFor
    (repoRoot: string)
    (bases: Collections.Concurrent.ConcurrentDictionary<AbsFilePath, Keyed>)
    (displaced: FsHotWatch.TaskCache.DisplacedEntries)
    (servable: FsHotWatch.ReferencesShadowReport.ServableFile)
    : FsHotWatch.ReferencesShadowReport.Reused =
    let file = IO.Path.Combine(repoRoot, servable.File)

    let keyed =
        match bases.TryGetValue(AbsFilePath.create file) with
        | true, keyed -> Some keyed
        | false, _ -> None

    match keyed, servable.UsedSignatures with
    | Some keyed, FsHotWatch.ReferencesShadowReport.UsedSignaturesKey.Described used ->
        let reused = keyOf keyed.Base used servable.FcsSignature

        if reused = keyed.Key then
            FsHotWatch.ReferencesShadowReport.Reused.SameKey
        else
            let reusedKey = displaced.Stored reused

            let compositeKey: FsHotWatch.TaskCache.CompositeKey =
                { Plugin = "lint"
                  File =
                    Some(
                        FsHotWatch.CachePathIdentity.ofPath repoRoot file
                        |> FsHotWatch.CachePathIdentity.toKey
                    ) }

            match displaced.Displaced compositeKey with
            | [] ->
                FsHotWatch.ReferencesShadowReport.Reused.Unverifiable
                    FsHotWatch.ReferencesShadowReport.UnverifiableReason.NoReusedEntry
            | entries ->
                match entries |> List.tryFind (fun e -> e.Key = reusedKey) with
                | Some entry ->
                    FsHotWatch.TaskCache.findingsFor file entry.Result
                    |> List.map FsHotWatch.ReferencesShadowReport.Finding.ofEntry
                    |> FsHotWatch.ReferencesShadowReport.Reused.Found
                | None ->
                    FsHotWatch.ReferencesShadowReport.Reused.Unverifiable
                        FsHotWatch.ReferencesShadowReport.UnverifiableReason.DisplacedKeyDiffers
    | None, _
    | _, FsHotWatch.ReferencesShadowReport.UsedSignaturesKey.Undescribed ->
        FsHotWatch.ReferencesShadowReport.Reused.Unverifiable
            FsHotWatch.ReferencesShadowReport.UnverifiableReason.NoReusedKey

/// Creates a framework plugin handler that lints files using pre-parsed AST
/// and check results from the daemon's warm FSharpChecker. Cache key is
/// content-merkle (file source, tool/config hashes, and the shape of every symbol the
/// file's check resolved outside the file); jj commit_id is not
/// consulted.
///
/// With `shadow` on (the daemon's `cache.nightlyShadow`), lint keeps each file's key base and
/// answers `references-shadow-lint` from the entries its writes displaced, and
/// `references-shadow-lint-spot-check` by linting sampled files again on a live check, its cache
/// bypassed. That changes nothing it lints, reports, files or serves.
let createWith
    (shadow: Shadow)
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

    let reuseKeyBases =
        match shadow with
        | Shadow.On(displaced, recheck) ->
            Kept(Collections.Concurrent.ConcurrentDictionary<AbsFilePath, Keyed>(), displaced, recheck)
        | Shadow.Off -> NotKept

    let cacheKey (event: PluginEvent<unit>) : ContentHash option =
        match event with
        | FileChecked r ->
            // A shape that cannot be described leaves no key: the lint runs and nothing
            // is read from or written to the cache.
            FsHotWatch.CacheInputs.usedSignaturesHash repoRoot r.ProjectOptions r.CheckResults
            |> Option.map (fun usedSignatures ->
                let keyBase: KeyBase =
                    [ "plugin-version", PluginCacheSalt
                      "tool", fsharpLintVersion
                      "config", (currentConfig ()).Hash
                      // REPO-RELATIVE. FSharpLint's verdict depends on where a file
                      // sits in the repository (naming rules read the path), never on
                      // which checkout of that repository it was read from — so the
                      // absolute path that used to be here was salt, and it was the
                      // only reason two workspaces could not share a lint result.
                      "file", FsHotWatch.CachePathIdentity.keyOf repoRoot (AbsFilePath.value r.File)
                      "source", r.Source ]

                let key = keyOf keyBase usedSignatures (FsHotWatch.CheckCache.fcsCheckSignature r)

                match reuseKeyBases with
                | Kept(bases, _, _) ->
                    bases[r.File] <-
                        { Base = keyBase
                          Key = key
                          Options = r.ProjectOptions }
                | NotKept -> ()

                key)
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
                // Lint runs on a live check only: the framework never hands Update a replay.
                FileCheckResult.requireTypedTree "lint" result

            let parsedInfo: Lint.ParsedFileInformation =
                { Ast = (FileCheckResult.requireParseResults "lint" result).ParseTree
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
      Init =
        { WarningsByFile = Map.empty
          FindingsByFile = Map.empty }
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

                                        PluginCtxHelpers.timedOutOnFiles ctx "lint" [ fileStr ] reason after

                                        return state
                                    | WorkCompleted(Result.Ok(Lint.LintResult.Success warnings)) ->
                                        Logging.debug
                                            "lint"
                                            $"Linted %s{System.IO.Path.GetFileName fileStr}: %d{warnings.Length} warnings"

                                        let msgs = warnings |> List.map (fun w -> w.Details.Message)

                                        let newWarnings = state.WarningsByFile |> Map.add result.File msgs

                                        let entries = entriesOf warnings

                                        PluginCtxHelpers.reportOrClearFile ctx fileStr entries

                                        let newState =
                                            { WarningsByFile = newWarnings
                                              FindingsByFile = state.FindingsByFile |> Map.add result.File entries }

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

                  return
                      LintReplies.Warnings.serialize
                          { Files = current.Count
                            Warnings = count }
              })
          |> PluginCommand.typed LintReplies.WarningsSchema
          // AUTOMATION-1816 step 2b: what reuse would have handed lint for each servable file
          // (the request: `FsHotWatch.ReferencesShadowJson.Servable`), against what it found here.
          FsHotWatch.ReferencesShadowJson.LintCommand,
          PluginCommand.Observe(fun ctx state args ->
              async {
                  let servable =
                      match
                          args
                          |> Array.tryHead
                          |> Option.bind FsHotWatch.ReferencesShadowJson.Servable.tryOfJson
                      with
                      | Some servable -> servable
                      | None ->
                          invalidArg "args" "expects the servable files (FsHotWatch.ReferencesShadowJson.Servable)"

                  let reused =
                      match reuseKeyBases with
                      | Kept(bases, displaced, _) -> reusedFor ctx.RepoRoot bases displaced
                      | NotKept ->
                          fun _ ->
                              FsHotWatch.ReferencesShadowReport.Reused.Unverifiable
                                  FsHotWatch.ReferencesShadowReport.UnverifiableReason.NoReusedKey

                  let fresh (file: string) =
                      state.FindingsByFile
                      |> Map.tryFind (AbsFilePath.create (IO.Path.Combine(ctx.RepoRoot, file)))
                      |> Option.defaultValue []

                  return
                      FsHotWatch.ReferencesShadowJson.PluginReply.toJson (
                          FsHotWatch.ReferencesShadowReport.comparePlugin "lint" reused fresh servable
                      )
              })
          |> PluginCommand.typed FsHotWatch.ReferencesShadowJson.PluginReplySchema
          // AUTOMATION-1816 step 2b: each sampled `sameKey` file (the request:
          // `FsHotWatch.ReferencesShadowJson.SpotCheckRequest`) linted again on a live check, the
          // task cache bypassed, against what this run served. Nothing linted here is reported,
          // filed or served.
          FsHotWatch.ReferencesShadowJson.LintSpotCheckCommand,
          PluginCommand.Observe(fun ctx state args ->
              async {
                  let files =
                      match
                          args
                          |> Array.tryHead
                          |> Option.bind FsHotWatch.ReferencesShadowJson.SpotCheckRequest.tryOfJson
                      with
                      | Some files -> files
                      | None ->
                          invalidArg
                              "args"
                              "expects the sampled files (FsHotWatch.ReferencesShadowJson.SpotCheckRequest)"

                  let absolute (file: string) =
                      AbsFilePath.create (IO.Path.Combine(ctx.RepoRoot, file))

                  // MGA-ERROR-REPORT-001:ok — a runner that throws on the re-lint is that file's
                  // answer (not recomputed, named by its exception); it decides nothing.
                  let recompute (file: string) : Async<Result<ErrorEntry list, string>> =
                      async {
                          match reuseKeyBases with
                          | NotKept -> return Result.Error "the nightly shadow is off"
                          | Kept(bases, _, recheck) ->
                              match bases.TryGetValue(absolute file) with
                              | false, _ -> return Result.Error "this run linted no check of the file"
                              | true, keyed ->
                                  match! recheck (absolute file) keyed.Options with
                                  | Some({ CheckResults = FullCheck _ } as live) ->
                                      let lintOrError _ct =
                                          try
                                              Result.Ok(runLint live)
                                          with ex ->
                                              Result.Error ex

                                      match runWithCancellableTimeout lintTimeout lintOrError with
                                      | WorkTimedOut after ->
                                          return Result.Error $"timed out after %d{int after.TotalSeconds}s"
                                      | WorkCompleted(Result.Ok(Lint.LintResult.Success warnings)) ->
                                          return Result.Ok(entriesOf warnings)
                                      | WorkCompleted(Result.Ok(Lint.LintResult.Failure failure)) ->
                                          return Result.Error failure.Description
                                      | WorkCompleted(Result.Error ex) -> return Result.Error(describeLintException ex)
                                  | Some _ -> return Result.Error "the live check did not complete fully"
                                  | None -> return Result.Error "the live check did not complete"
                      }

                  let cached (file: string) =
                      state.FindingsByFile |> Map.tryFind (absolute file) |> Option.defaultValue []

                  let! outcomes = FsHotWatch.ReferencesShadowReport.SpotCheck.run files recompute cached
                  return FsHotWatch.ReferencesShadowJson.SpotCheckReply.toJson outcomes
              })
          |> PluginCommand.typed FsHotWatch.ReferencesShadowJson.SpotCheckReplySchema ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = Some(fun _state event -> cacheKey event)
      Teardown = None }

/// `createWith` with nothing captured: no nightly shadow.
let create
    (repoRoot: string option)
    (lintConfigPath: string option)
    (lintRunner: (FileCheckResult -> Lint.LintResult) option)
    (timeoutSec: int option)
    : PluginHandler<LintState, unit> =
    createWith Shadow.Off repoRoot lintConfigPath lintRunner timeoutSec
