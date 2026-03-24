module FsHotWatch.Fantomas.FormatCheckPlugin

open System.IO
open FsHotWatch.Events
open FsHotWatch.Plugin
open Fantomas.Core

type FormatCheckPlugin() =
    let mutable unformatted: Set<string> = Set.empty

    interface IFsHotWatchPlugin with
        member _.Name = "format-check"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change ->
                try
                    let files =
                        match change with
                        | SourceChanged files -> files
                        | _ -> []

                    for file in files do
                        if File.Exists(file) then
                            ctx.ReportStatus(Running(since = System.DateTime.UtcNow))
                            let source = File.ReadAllText(file)
                            let isSignature = file.EndsWith(".fsi")

                            let formatted =
                                CodeFormatter.FormatDocumentAsync(isSignature, source)
                                |> Async.RunSynchronously

                            if formatted.Code <> source then
                                unformatted <- unformatted |> Set.add file
                            else
                                unformatted <- unformatted |> Set.remove file

                    ctx.ReportStatus(Completed(box unformatted, System.DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, System.DateTime.UtcNow)))

            ctx.RegisterCommand(
                "unformatted",
                fun _args ->
                    async {
                        let files = unformatted |> Set.toList |> String.concat ", "
                        return $"{{\"count\": {unformatted.Count}, \"files\": \"{files}\"}}"
                    }
            )

        member _.Dispose() = ()
