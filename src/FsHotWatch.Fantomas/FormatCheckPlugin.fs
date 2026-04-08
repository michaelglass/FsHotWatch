module FsHotWatch.Fantomas.FormatCheckPlugin

open System
open System.IO
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open Fantomas.Core

/// Format-on-save preprocessor. Runs before other plugins receive events.
/// Formats unformatted files and returns the list of files that were rewritten.
type FormatPreprocessor() =
    interface IFsHotWatchPreprocessor with
        member _.Name = "format"

        member _.Process (changedFiles: string list) (_repoRoot: string) =
            let mutable modifiedFiles = []

            for file in changedFiles do
                if
                    File.Exists(file)
                    && (file.EndsWith(".fs") || file.EndsWith(".fsx") || file.EndsWith(".fsi"))
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

/// Read-only format check plugin (reports unformatted files without modifying them).
/// Use this instead of FormatPreprocessor if you don't want auto-formatting.
let createFormatCheck (getCommitId: (unit -> string option) option) : PluginHandler<FormatCheckState, unit> =
    { Name = "format-check"
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

                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    let mutable newUnformatted = state.Unformatted
                    let mutable failed = false

                    for file in files do
                        if File.Exists(file) && not failed then
                            try
                                let source = File.ReadAllText(file)
                                let isSignature = file.EndsWith(".fsi")

                                let formatted =
                                    CodeFormatter.FormatDocumentAsync(isSignature, source) |> Async.RunSynchronously

                                if formatted.Code <> source then
                                    newUnformatted <- newUnformatted |> Set.add file

                                    ctx.ReportErrors
                                        file
                                        [ { Message = "File is not formatted"
                                            Severity = FsHotWatch.ErrorLedger.Warning
                                            Line = 1
                                            Column = 0
                                            Detail = None } ]
                                else
                                    newUnformatted <- newUnformatted |> Set.remove file
                                    ctx.ClearErrors file
                            with ex ->
                                ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                                failed <- true

                    if not failed then
                        ctx.ReportStatus(Completed(DateTime.UtcNow))

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
      Subscriptions =
        { PluginSubscriptions.none with
            FileChanged = true }
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId }
