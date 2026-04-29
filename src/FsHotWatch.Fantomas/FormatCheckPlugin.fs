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
type FormatPreprocessor() =
    let ignoreCache = FsHotWatch.PathFilter.IgnoreFilterCache()

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

                        let formatted =
                            CodeFormatter.FormatDocumentAsync(isSignature, source) |> Async.RunSynchronously

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

                    ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    ctx.StartSubtask PrimarySubtaskKey $"checking format of %d{files.Length} files"

                    let mutable newUnformatted = state.Unformatted
                    let mutable failed = false

                    let mutable timedOut = false

                    for file in files do
                        if File.Exists(file) && not (isIgnored file) && not failed && not timedOut then
                            let work () =
                                match slowHook with
                                | Some h -> h ()
                                | None -> ()

                                let source = File.ReadAllText(file)
                                let isSignature = file.EndsWith(".fsi")

                                let formatted =
                                    CodeFormatter.FormatDocumentAsync(isSignature, source) |> Async.RunSynchronously

                                source, formatted

                            try
                                match runWithTimeout formatTimeout work with
                                | WorkTimedOut after ->
                                    let reason = $"timed out after %d{int after.TotalSeconds}s"
                                    Logging.error "format" $"Format check TIMED OUT for %s{file}: %s{reason}"

                                    ctx.CompleteWithTimeout reason

                                    ctx.ReportStatus(
                                        PluginStatus.Failed($"format check timed out: {reason}", DateTime.UtcNow)
                                    )

                                    timedOut <- true
                                | WorkCompleted(source, formatted) ->
                                    let isUnformatted = formatted.Code <> source

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
                                ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                                failed <- true

                    ctx.EndSubtask PrimarySubtaskKey

                    if not failed && not timedOut then
                        let summary =
                            if newUnformatted.Count = 0 then
                                "format OK"
                            else
                                $"%d{newUnformatted.Count} files need formatting"

                        PluginCtxHelpers.completeWith ctx summary

                    return { Unformatted = newUnformatted }
                | _ -> return state
            }
      Commands =
        [ "unformatted",
          fun state _args ->
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
        let cacheKey (event: PluginEvent<unit>) : ContentHash option =
            match event with
            | FileChanged(SourceChanged files) when not (List.isEmpty files) ->
                let fileInputs =
                    files
                    |> List.sort
                    |> List.collect (fun f ->
                        let source =
                            try
                                File.ReadAllText(f)
                            with _ ->
                                ""

                        [ $"file:{f}", f; $"source:{f}", source ])

                Some(FsHotWatch.TaskCache.merkleCacheKey ([ "plugin-version", "format-check-merkle-v1" ] @ fileInputs))
            | _ -> None

        Some cacheKey
      Teardown = None }

/// Read-only format check plugin (reports unformatted files without modifying them).
/// Use this instead of FormatPreprocessor if you don't want auto-formatting.
/// Respects .gitignore and .fantomasignore files in the repo root.
/// Per-file format work is wrapped in `runWithTimeout`; on expiry the run is
/// recorded as `TimedOut` and the orphan work continues running (result
/// discarded).
let createFormatCheck (timeoutSec: int option) : PluginHandler<FormatCheckState, unit> =
    createFormatCheckWithSlowHook timeoutSec None
