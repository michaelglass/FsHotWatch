module FsHotWatch.FileErrorReporter

open System.IO
open System.Text.Json
open FsHotWatch.ErrorLedger

let private sanitizeFileName = FsHotWatch.StringHelpers.sanitizeFileName

let private errorFileName (plugin: string) (file: string) =
    $"%s{plugin}--%s{sanitizeFileName file}.json"

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

let private tryDelete path = File.Delete(path)

/// Writes per-plugin per-file JSON error files to a directory.
type FileErrorReporter(errorDir: string) =
    do Directory.CreateDirectory(errorDir) |> ignore

    let serializeEntries (entries: ErrorEntry list) =
        let records =
            entries
            |> List.map (fun e ->
                {| message = e.Message
                   severity =
                    match e.Severity with
                    | Error -> "error"
                    | Warning -> "warning"
                    | Info -> "info"
                    | Hint -> "hint"
                   line = e.Line
                   column = e.Column
                   detail = e.Detail |})

        JsonSerializer.Serialize(records, jsonOptions)

    let writePath plugin file =
        Path.Combine(errorDir, errorFileName plugin file)

    interface IErrorReporter with
        member _.Report plugin file entries =
            let path = writePath plugin file

            if entries.IsEmpty then
                tryDelete path
            else
                File.WriteAllText(path, serializeEntries entries)

        member _.Clear plugin file = tryDelete (writePath plugin file)

        member _.ClearPlugin plugin =
            let prefix = $"%s{plugin}--"

            for f in Directory.EnumerateFiles(errorDir, "*.json") do
                if Path.GetFileName(f).StartsWith(prefix) then
                    File.Delete(f)

        member _.ClearAll() =
            for f in Directory.EnumerateFiles(errorDir, "*.json") do
                File.Delete(f)
