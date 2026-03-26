module FsHotWatch.Fantomas.FormatCheckPlugin

open System.IO
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.Plugin
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

/// Read-only format check plugin (reports unformatted files without modifying them).
/// Use this instead of FormatPreprocessor if you don't want auto-formatting.
type FormatCheckPlugin() =
    let mutable unformatted: Set<string> = Set.empty

    interface IFsHotWatchPlugin with
        member _.Name = "format-check"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change ->
                try
                    let files =
                        match change with
                        | FsHotWatch.Events.SourceChanged files -> files
                        | _ -> []

                    for file in files do
                        if File.Exists(file) then
                            ctx.ReportStatus(FsHotWatch.Events.Running(since = System.DateTime.UtcNow))
                            let source = File.ReadAllText(file)
                            let isSignature = file.EndsWith(".fsi")

                            let formatted =
                                CodeFormatter.FormatDocumentAsync(isSignature, source) |> Async.RunSynchronously

                            if formatted.Code <> source then
                                unformatted <- unformatted |> Set.add file
                            else
                                unformatted <- unformatted |> Set.remove file

                    ctx.ReportStatus(FsHotWatch.Events.Completed(box unformatted, System.DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(FsHotWatch.Events.PluginStatus.Failed(ex.Message, System.DateTime.UtcNow)))

            ctx.RegisterCommand(
                "unformatted",
                fun _args ->
                    async {
                        let files = unformatted |> Set.toList |> String.concat ", "
                        return $"{{\"count\": %d{unformatted.Count}, \"files\": \"%s{files}\"}}"
                    }
            )

        member _.Dispose() = ()
