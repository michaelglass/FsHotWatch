module FsHotWatch.Fantomas.FormatCheckPlugin

open System.IO
open System.Threading
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

                            let current = Volatile.Read(&unformatted)

                            if formatted.Code <> source then
                                Volatile.Write(&unformatted, current |> Set.add file)
                            else
                                Volatile.Write(&unformatted, current |> Set.remove file)

                    ctx.ReportStatus(Completed(box (Volatile.Read(&unformatted)), System.DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, System.DateTime.UtcNow)))

            ctx.RegisterCommand(
                "unformatted",
                fun _args ->
                    async {
                        let current = Volatile.Read(&unformatted)
                        let files = current |> Set.toList |> String.concat ", "
                        return $"{{\"count\": {current.Count}, \"files\": \"{files}\"}}"
                    }
            )

        member _.Dispose() = ()
