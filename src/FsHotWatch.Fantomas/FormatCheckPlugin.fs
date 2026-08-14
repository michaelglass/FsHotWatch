module FsHotWatch.Fantomas.FormatCheckPlugin

open System
open System.IO
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper
open Fantomas.Core

/// Default per-event format-check timeout (seconds). Used when no override is
/// configured. Chosen to match DaemonConfig.FormatTimeoutDefaultSec.
[<Literal>]
let FormatTimeoutDefaultSec = 60

/// Format-on-save preprocessor. Runs before other plugins receive events.
/// Formats unformatted files and returns the list of files that were rewritten.
/// Respects .gitignore and .fantomasignore files in the repo root.
///
/// The Fantomas call is bounded (`runWithCancellableTimeout`) because a
/// preprocessor runs inside the daemon's `processBatch` — a hang here wedges the
/// CHANGE AGENT, so the daemon silently never processes another file change — and
/// inside `performScan`, which `WaitForScan` blocks on as `check`'s very first
/// step. A stuck file is skipped, loudly (`WorkTimedOut`).
///
/// `slowHook` is a TEST SEAM, mirroring `createFormatCheckWithSlowHook`: it runs
/// inside the timeout-guarded region, before formatting, so a test can force the
/// `WorkTimedOut` branch deterministically rather than racing a zero-length timer
/// against Fantomas.
type FormatPreprocessor(?timeoutSec: int, ?slowHook: unit -> unit) =
    let ignoreCache = FsHotWatch.PathFilter.IgnoreFilterCache()

    let formatTimeout =
        TimeSpan.FromSeconds(float (defaultArg timeoutSec FormatTimeoutDefaultSec))

    interface IFsHotWatchPreprocessor with
        member _.Name = "format"

        member _.Process (changedFiles: string list) (repoRoot: string) =
            let isIgnored = ignoreCache.Get(repoRoot)
            let mutable modifiedFiles = []

            for file in changedFiles do
                if
                    File.Exists(file)
                    && (file.EndsWith(".fs") || file.EndsWith(".fsx") || file.EndsWith(".fsi"))
                    && not (isIgnored file)
                then
                    try
                        let source = File.ReadAllText(file)
                        let isSignature = file.EndsWith(".fsi")

                        // Driven under the timeout's token so a stuck format is
                        // actually CANCELLED (thread released) rather than orphaned
                        // while still holding the batch.
                        let work (ct: System.Threading.CancellationToken) =
                            match slowHook with
                            | Some hook -> hook ()
                            | None -> ()

                            Async.RunSynchronously(
                                CodeFormatter.FormatDocumentAsync(isSignature, source),
                                cancellationToken = ct
                            )

                        match runWithCancellableTimeout formatTimeout work with
                        | WorkTimedOut after ->
                            Logging.error
                                "format"
                                $"format TIMED OUT for %s{file} after %d{int after.TotalSeconds}s — file left \
                                  unformatted (the format-check plugin will still report it)"
                        | WorkCompleted formatted ->
                            if formatted.Code <> source then
                                File.WriteAllText(file, formatted.Code)
                                modifiedFiles <- file :: modifiedFiles
                    with ex ->
                        Logging.error "format" $"failed to format %s{file}: %s{ex.Message}"

            modifiedFiles

        member _.Dispose() = ()

/// State for the format-check framework plugin.
type FormatCheckState = { Unformatted: Set<string> }

/// Internal constructor with a test seam. `slowHook` is invoked inside the
/// timeout-guarded region before formatting runs so tests can force the
/// timeout branch. The public `createFormatCheck` passes `None`.
let internal createFormatCheckWithSlowHook
    (timeoutSec: int option)
    (slowHook: (unit -> unit) option)
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

                    let isIgnored = ignoreCache.Get(ctx.RepoRoot)

                    let runStarted = DateTime.UtcNow
                    ctx.ReportStatus(Running(since = runStarted))
                    ctx.StartSubtask PrimarySubtaskKey $"checking format of %d{files.Length} files"

                    let mutable newUnformatted = state.Unformatted
                    let mutable failed = false

                    let mutable timedOut = false

                    // What THIS run compared, and what it found. The terminal summary
                    // is built from these and never from `newUnformatted`, whose
                    // whole-session count is not a function of this event.
                    //
                    // The framework keys a `FileChanged` event as a whole-run entry
                    // (`File = None`), whose stored verdict replays VERBATIM — so a
                    // summary drawn from session state would be re-asserted, unchanged,
                    // in a later session over a different tree: "1 files need
                    // formatting (cached)" beside an empty ledger and a green verdict
                    // (AUTOMATION-191, the `File = None` half of AUTOMATION-186).
                    // Counting only what this run touched keeps the summary a function
                    // of the cache key — the same bytes the merkle covers — so the
                    // replay says exactly what a cold run over those bytes says.
                    //
                    // The whole-session view is not lost, it moves to where it stays
                    // LIVE: every unformatted file is a ledger entry (`fshw status`
                    // lists them, and the verdict gates on them), and the `unformatted`
                    // command still answers with the accumulated set.
                    let mutable checkedInRun = 0
                    let mutable unformattedInRun = 0

                    for file in files do
                        if File.Exists(file) && not (isIgnored file) && not failed && not timedOut then
                            let work (ct: System.Threading.CancellationToken) =
                                match slowHook with
                                | Some h -> h ()
                                | None -> ()

                                let source = File.ReadAllText(file)
                                let isSignature = file.EndsWith(".fsi")

                                // Fantomas's async honours the token, so a stuck
                                // format is cancelled on expiry, not orphaned.
                                let formatted =
                                    Async.RunSynchronously(
                                        CodeFormatter.FormatDocumentAsync(isSignature, source),
                                        cancellationToken = ct
                                    )

                                source, formatted

                            try
                                match runWithCancellableTimeout formatTimeout work with
                                | WorkTimedOut after ->
                                    let reason = $"timed out after %d{int after.TotalSeconds}s"
                                    Logging.error "format" $"Format check TIMED OUT for %s{file}: %s{reason}"

                                    // Flip the recorded outcome to TimedOut; the verdict
                                    // carries the summary (one channel).
                                    ctx.CompleteWithTimeout reason

                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"format check timed out: {reason}"
                                            $"format check timed out: {reason}"
                                            after
                                    )

                                    timedOut <- true
                                | WorkCompleted(source, formatted) ->
                                    let isUnformatted = formatted.Code <> source

                                    checkedInRun <- checkedInRun + 1

                                    if isUnformatted then
                                        unformattedInRun <- unformattedInRun + 1

                                    newUnformatted <-
                                        if isUnformatted then
                                            newUnformatted |> Set.add file
                                        else
                                            newUnformatted |> Set.remove file

                                    let entries: FsHotWatch.ErrorLedger.ErrorEntry list =
                                        if isUnformatted then
                                            ctx.Log $"unformatted: {Path.GetFileName file}"

                                            [ { Message = "File is not formatted"
                                                Severity = FsHotWatch.ErrorLedger.Warning
                                                Line = 1
                                                Column = 0
                                                Detail = None } ]
                                        else
                                            []

                                    PluginCtxHelpers.reportOrClearFile ctx file entries
                            with ex ->
                                ctx.ReportStatus(
                                    PluginStatus.failedNow
                                        ex.Message
                                        $"format check crashed: %s{ex.Message}"
                                        (DateTime.UtcNow - runStarted)
                                )

                                failed <- true

                    ctx.EndSubtask PrimarySubtaskKey

                    if not failed && not timedOut then
                        let summary =
                            if checkedInRun = 0 then
                                // Every file in the event was missing, ignored, or not
                                // F#. "format OK" would be a green earned by checking
                                // nothing.
                                "no files to check"
                            elif unformattedInRun = 0 then
                                $"format OK (%d{checkedInRun} checked)"
                            else
                                $"%d{unformattedInRun} of %d{checkedInRun} files need formatting"

                        PluginCtxHelpers.completeWith ctx summary (DateTime.UtcNow - runStarted)

                    return { Unformatted = newUnformatted }
                | _ -> return state
            }
      Commands =
        [ "unformatted",
          fun _ctx state _args ->
              async {
                  let files = state.Unformatted |> Set.toList |> String.concat ", "
                  return $"{{\"count\": %d{state.Unformatted.Count}, \"files\": \"%s{files}\"}}"
              } ]
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey =
        // Pure-content key: merkle of (file path, file source) for each file
        // in the FileChanged event. Fantomas formatting is content-deterministic
        // — same source bytes always produce the same formatted output, so two
        // daemons agree on the cache value regardless of working-copy state.
        let tryReadFileForMerkle (f: string) : (string * string) list option =
            // Refuse to produce a key when any input is unreadable — substituting
            // "" would collide with real empty files and with each other, so the
            // cache is bypassed (cache miss + retry on the next event) instead of
            // poisoning a "format OK" verdict.
            try
                let source = File.ReadAllText(f)
                Some [ $"file:{f}", f; $"source:{f}", source ]
            with ex ->
                Logging.debug
                    "format-check"
                    $"cache-key skipped: could not read %s{f}: %s{ex.GetType().FullName}: %s{ex.Message}"

                None

        let cacheKey (event: PluginEvent<unit>) : ContentHash option =
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

                allInputs
                |> Option.map (fun fileInputs ->
                    FsHotWatch.TaskCache.merkleCacheKey ([ "plugin-version", "format-check-merkle-v1" ] @ fileInputs))
            | _ -> None

        Some cacheKey
      Teardown = None }

/// Read-only format check plugin (reports unformatted files without modifying them).
/// Use this instead of FormatPreprocessor if you don't want auto-formatting.
/// Respects .gitignore and .fantomasignore files in the repo root. Per-file format
/// work is bounded by `runWithCancellableTimeout`; on expiry the run is recorded as
/// `TimedOut` and the in-flight format is cancelled.
let createFormatCheck (timeoutSec: int option) : PluginHandler<FormatCheckState, unit> =
    createFormatCheckWithSlowHook timeoutSec None
