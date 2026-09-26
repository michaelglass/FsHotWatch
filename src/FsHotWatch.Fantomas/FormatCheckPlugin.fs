module FsHotWatch.Fantomas.FormatCheckPlugin

open System
open System.IO
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.Fantomas.FantomasTool

/// Default per-event format timeout (seconds). Used when no override is
/// configured. Chosen to match DaemonConfig.FormatTimeoutDefaultSec.
[<Literal>]
let FormatTimeoutDefaultSec = 60

/// The files a batch holds for the formatter: existing F# sources the repository's
/// ignore files (`.gitignore`, `.fantomasignore`) do not exclude.
let private formattable (isIgnored: string -> bool) (files: string list) : string list =
    files
    |> List.filter (fun file ->
        File.Exists(file)
        && (file.EndsWith(".fs") || file.EndsWith(".fsx") || file.EndsWith(".fsi"))
        && not (isIgnored file))

/// What decides the pinned tool's output for a file: its version, the `.editorconfig`
/// files above the file, and the file's bytes. `None` when the file cannot be read, so
/// an unreadable file is never remembered as formatted. The configuration is read once
/// per directory per call.
let private settledKeys (repoRoot: string) (pin: FantomasPin) : string -> string option =
    let configByDir = Collections.Generic.Dictionary<string, string>()

    fun (file: string) ->
        let dir = Path.GetDirectoryName(Path.GetFullPath file)

        let config =
            match configByDir.TryGetValue dir with
            | true, config -> config
            | false, _ ->
                let config =
                    FsHotWatch.CacheInputs.editorConfigInputs repoRoot [ file ]
                    |> List.map (fun (label, content) -> $"%s{label}\n%s{content}")
                    |> String.concat "\n"

                configByDir[dir] <- config
                config

        try
            let bytes = File.ReadAllBytes file

            use sha = Security.Cryptography.SHA256.Create()
            let header = Text.Encoding.UTF8.GetBytes($"%s{pin.Version}\n%s{config}\n")
            sha.TransformBlock(header, 0, header.Length, null, 0) |> ignore
            sha.TransformFinalBlock(bytes, 0, bytes.Length) |> ignore
            Some(Convert.ToHexString sha.Hash)
        with ex ->
            Logging.debug
                "format"
                $"not remembering %s{file}: could not read it: %s{ex.GetType().FullName}: %s{ex.Message}"

            None

/// Format-on-save preprocessor. Runs before other plugins receive events.
/// Rewrites unformatted files with the repository's PINNED Fantomas
/// (`dotnet tool run fantomas`, resolved from `.config/dotnet-tools.json`) and
/// returns the files it changed, with the version it ran as evidence. Respects
/// .gitignore and .fantomasignore files in the repo root.
///
/// A repository that pins no Fantomas gets `Error` — the pass does not run, the
/// host records a failed status, and `fshw format` says why — rather than a
/// silently different formatter's opinion.
///
/// The tool is bounded (`ProcessBounds.silent timeout` inside the runner) because
/// a preprocessor runs inside the daemon's `processBatch` — a hang here wedges
/// the CHANGE AGENT — and inside `performScan`, which `WaitForScan` blocks on as
/// `check`'s very first step. A timed-out run leaves every file as it was.
///
/// `runner` is the TEST SEAM: production uses `FantomasTool.dotnetToolRunner`; a
/// test substitutes a recorder to prove which pin and arguments were handed over.
///
/// A scan hands it every registered file, and `check` forces a scan. Bytes the pinned
/// tool has already left unchanged, under the same version and the same `.editorconfig`
/// files, are not handed to it again: the tool is a pure function of those three, so the
/// run would be a no-op. Only an unchanged file is remembered. A file the tool rewrote,
/// could not format, or never finished (timeout) goes back to it on the next pass.
type FormatPreprocessor(?timeoutSec: int, ?runner: Runner) =
    let ignoreCache = FsHotWatch.PathFilter.IgnoreFilterCache()
    let runner = defaultArg runner dotnetToolRunner

    /// file → the key of the bytes the pinned tool last left unchanged.
    let settled = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

    let formatTimeout =
        TimeSpan.FromSeconds(float (defaultArg timeoutSec FormatTimeoutDefaultSec))

    interface IFsHotWatchPreprocessor with
        member _.Name = "format"

        member _.Process (changedFiles: string list) (repoRoot: string) =
            match readPin repoRoot with
            | Error pinError -> Error(PinError.render pinError)
            | Ok pin ->
                let evidence = describe repoRoot pin
                let files = formattable (ignoreCache.Get(repoRoot)) changedFiles
                let keyOf = settledKeys repoRoot pin
                // Keyed BEFORE the run, so a key only ever names bytes the tool was given.
                let keyed = files |> List.map (fun file -> file, keyOf file)

                let pending =
                    keyed
                    |> List.filter (fun (file, key) ->
                        match key, settled.TryGetValue file with
                        | Some key, (true, prior) -> key <> prior
                        | _ -> true)

                let toRun = pending |> List.map fst

                if toRun.Length < files.Length then
                    Logging.debug
                        "format"
                        $"%d{files.Length - toRun.Length} of %d{files.Length} file(s) unchanged since the pinned formatter left them as they are — not re-run"

                match FantomasTool.format runner pin repoRoot formatTimeout toRun with
                | Ok report ->
                    for (file, reason) in report.FormatErrors do
                        Logging.error "format" $"could not format %s{file}: %s{reason}"

                    let unsettled = Set.ofList (report.Modified @ List.map fst report.FormatErrors)

                    for (file, key) in pending do
                        match key with
                        | Some key when not (unsettled.Contains file) -> settled[file] <- key
                        | _ -> settled.TryRemove file |> ignore

                    Ok
                        { Modified = report.Modified
                          Considered = files.Length
                          Evidence = evidence }
                | Error(ToolFailure.TimedOut(after, kill)) ->
                    // Left every file unformatted; the format-check plugin still reports
                    // them, and the daemon must keep processing the batch.
                    Logging.error
                        "format"
                        $"format TIMED OUT after %d{int after.TotalSeconds}s (%s{ProcessHelper.renderKillBrief kill}) — %d{toRun.Length} file(s) left unformatted"

                    Ok
                        { Modified = []
                          Considered = files.Length
                          Evidence = evidence }
                | Error failure -> Error(ToolFailure.render failure)

        member _.Dispose() = ()

/// State for the format-check framework plugin.
type FormatCheckState = { Unformatted: Set<string> }

/// Internal constructor with the runner seam exposed. The public
/// `createFormatCheck` passes `FantomasTool.dotnetToolRunner`.
let internal createFormatCheckWith
    (runner: Runner)
    (repoRoot: string)
    (timeoutSec: int option)
    : PluginHandler<FormatCheckState, unit> =
    let ignoreCache = FsHotWatch.PathFilter.IgnoreFilterCache()

    let formatTimeout =
        let secs = defaultArg timeoutSec FormatTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    { Name = PluginName.create "format-check"
      Init = { Unformatted = Set.empty }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged change ->
                    let files =
                        match change with
                        | SourceChanged files -> files
                        | _ -> []

                    let runStarted = DateTime.UtcNow
                    ctx.ReportStatus(Running(since = runStarted))
                    ctx.StartSubtask PrimarySubtaskKey $"checking format of %d{files.Length} files"

                    let files = formattable (ignoreCache.Get(ctx.RepoRoot)) files

                    // What THIS run compared, and what it found. The terminal summary
                    // is built from these and never from the whole-session set.
                    //
                    // The framework keys a `FileChanged` event as a whole-run entry
                    // (`File = None`), whose stored verdict replays VERBATIM — so a
                    // summary drawn from session state would be re-asserted, unchanged,
                    // in a later session over a different tree: "1 files need
                    // formatting (cached)" beside an empty ledger and a green verdict
                    // (the `File = None` half of the cache scope rule).
                    // Counting only what this run touched keeps the summary a function
                    // of the cache key — the same bytes the merkle covers — so the
                    // replay says exactly what a cold run over those bytes says.
                    //
                    // The whole-session view is not lost, it moves to where it stays
                    // LIVE: every unformatted file is a ledger entry (`fshw status`
                    // lists them, and the verdict gates on them), and the `unformatted`
                    // command still answers with the accumulated set.
                    let outcome =
                        match readPin ctx.RepoRoot with
                        // Every file in the event was missing, ignored, or not F#:
                        // nothing was checked, and no formatter needs consulting to
                        // say so. The pin is only demanded when there is work for it.
                        | _ when List.isEmpty files -> Ok None
                        | Error pinError -> Error(PinError.render pinError, None)
                        | Ok pin ->
                            match FantomasTool.check runner pin ctx.RepoRoot formatTimeout files with
                            | Ok report -> Ok(Some(pin, report))
                            | Error(ToolFailure.TimedOut(after, kill)) ->
                                Error(ToolFailure.render (ToolFailure.TimedOut(after, kill)), Some after)
                            | Error failure -> Error(ToolFailure.render failure, None)

                    ctx.EndSubtask PrimarySubtaskKey

                    match outcome with
                    | Error(reason, Some after) ->
                        Logging.error "format" $"Format check TIMED OUT: %s{reason}"

                        // Flip the recorded outcome to TimedOut; the verdict
                        // carries the summary (one channel).
                        ctx.CompleteWithTimeout reason

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"format check timed out: {reason}"
                                $"format check timed out: {reason}"
                                after
                        )

                        return state
                    | Error(reason, None) ->
                        // No verdict was earned: the pinned tool could not be run at
                        // all. A failed status, never a green — and never cached (the
                        // cache key is `None` for the same reason).
                        ctx.Log $"format check refused: %s{reason}"

                        PluginCtxHelpers.failedWith
                            ctx
                            reason
                            $"format check refused: %s{reason}"
                            (DateTime.UtcNow - runStarted)

                        return state
                    | Ok None ->
                        // "format OK" would be a green earned by checking nothing.
                        PluginCtxHelpers.completeWith ctx "no files to check" (DateTime.UtcNow - runStarted)
                        return state
                    | Ok(Some(pin, report)) ->
                        let unformatted = Set.ofList report.NeedsFormatting
                        let errored = Map.ofList report.FormatErrors
                        let evidence = describe ctx.RepoRoot pin

                        let mutable newUnformatted = state.Unformatted

                        for file in files do
                            let isUnformatted = unformatted.Contains file

                            newUnformatted <-
                                if isUnformatted then
                                    newUnformatted |> Set.add file
                                else
                                    newUnformatted |> Set.remove file

                            let entries: FsHotWatch.ErrorLedger.ErrorEntry list =
                                match Map.tryFind file errored with
                                | Some reason ->
                                    ctx.Log $"could not format: {Path.GetFileName file}: {reason}"

                                    [ { Message = $"%s{evidence} could not format this file: %s{reason}"
                                        Severity = FsHotWatch.ErrorLedger.Error
                                        Line = 1
                                        Column = 0
                                        Detail = None } ]
                                | None when isUnformatted ->
                                    ctx.Log $"unformatted: {Path.GetFileName file}"

                                    [ { Message = $"File is not formatted (%s{evidence})"
                                        Severity = FsHotWatch.ErrorLedger.Warning
                                        Line = 1
                                        Column = 0
                                        Detail = None } ]
                                | None -> []

                            PluginCtxHelpers.reportOrClearFile ctx file entries

                        let checkedInRun = files.Length
                        let unformattedInRun = unformatted.Count
                        let erroredInRun = errored.Count

                        let summary =
                            if erroredInRun > 0 then
                                $"%d{erroredInRun} of %d{checkedInRun} files could not be formatted — %s{evidence}"
                            elif unformattedInRun = 0 then
                                $"format OK (%d{checkedInRun} checked) — %s{evidence}"
                            else
                                $"%d{unformattedInRun} of %d{checkedInRun} files need formatting — %s{evidence}"

                        PluginCtxHelpers.completeWith ctx summary (DateTime.UtcNow - runStarted)

                        return { Unformatted = newUnformatted }
                | _ -> return state
            }
      PrepareCommit = None
      Commands =
        [ "unformatted",
          PluginCommand.Observe(fun _ctx state _args ->
              async {
                  let files = state.Unformatted |> Set.toList |> String.concat ", "
                  return $"{{\"count\": %d{state.Unformatted.Count}, \"files\": \"%s{files}\"}}"
              }) ]
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey =
        // Content key: merkle of (file path, file source) for each file in the
        // FileChanged event, PLUS the two things that decide what the pinned tool
        // says about those bytes — its version and the `.editorconfig` files above
        // them. Formatting is deterministic in (version, config, source), so two
        // daemons agree on the cache value regardless of working-copy state, and a
        // pin bump or a config edit is a miss rather than a replayed `format OK`.
        // REPO-RELATIVE spelling of a path, for both the label and the value. Fantomas
        // formats a file as a pure function of its bytes, the pinned tool version and
        // the `.editorconfig` files above it — never of which checkout it was read
        // from. The absolute path that used to sit here was the ONLY thing keeping two
        // workspaces of one repository from sharing a `format OK`, and this plugin's
        // whole-tree scan is the largest measured cold-start cost.
        let pathKey (f: string) =
            FsHotWatch.CachePathIdentity.keyOf (Some repoRoot) f

        let tryReadFileForMerkle (f: string) : (string * string) list option =
            // Refuse to produce a key when any input is unreadable — substituting
            // "" would collide with real empty files and with each other, so the
            // cache is bypassed (cache miss + retry on the next event) instead of
            // poisoning a "format OK" verdict.
            try
                let source = File.ReadAllText(f)
                let key = pathKey f
                Some [ $"file:{key}", key; $"source:{key}", source ]
            with ex ->
                Logging.debug
                    "format-check"
                    $"cache-key skipped: could not read %s{f}: %s{ex.GetType().FullName}: %s{ex.Message}"

                None

        let cacheKey (repoRoot: string) (event: PluginEvent<unit>) : ContentHash option =
            match event with
            | FileChanged(SourceChanged files) when not (List.isEmpty files) ->
                let sortedFiles = List.sort files

                // Read every file or surrender the key: one unreadable file must
                // produce None for the whole event.
                let allInputs =
                    (Some [], sortedFiles)
                    ||> List.fold (fun acc f ->
                        acc
                        |> Option.bind (fun accList ->
                            tryReadFileForMerkle f |> Option.map (fun pairs -> accList @ pairs)))

                // No pin, no key: a refusal is never cached, it is re-earned on every
                // event until the manifest is fixed.
                match readPin repoRoot, allInputs with
                | Ok pin, Some fileInputs ->
                    Some(
                        FsHotWatch.TaskCache.merkleCacheKey (
                            // v3 orphans every entry written under the path-ABSOLUTE
                            // key (v2), which no other checkout could have read.
                            [ "plugin-version", "format-check-pinned-tool-v3"; "fantomas-pin", pin.Version ]
                            @ FsHotWatch.CacheInputs.editorConfigInputs repoRoot sortedFiles
                            @ fileInputs
                        )
                    )
                | _ -> None
            | _ -> None

        Some(fun _state event -> cacheKey repoRoot event)
      Teardown = None }

/// Read-only format check plugin (reports unformatted files without modifying them).
/// Use this instead of FormatPreprocessor if you don't want auto-formatting.
/// Runs the repository's PINNED Fantomas (`dotnet tool run fantomas --check`) and
/// names the version in every summary; refuses with a failed status when
/// `.config/dotnet-tools.json` pins none. Respects .gitignore and .fantomasignore
/// files in the repo root. Each run is bounded by the tool timeout; on expiry the
/// run is recorded as `TimedOut` and the child process tree is killed.
///
/// `repoRoot` is where the tool manifest lives and where the tool runs; it is a
/// parameter (rather than read from the event context) because the cache key needs
/// the pin before any context exists.
let createFormatCheck (repoRoot: string) (timeoutSec: int option) : PluginHandler<FormatCheckState, unit> =
    createFormatCheckWith dotnetToolRunner repoRoot timeoutSec
