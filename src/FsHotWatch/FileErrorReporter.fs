module FsHotWatch.FileErrorReporter

open System.IO
open System.Text.Json
open FsHotWatch.ErrorLedger

let private sanitizeFileName = FsHotWatch.StringHelpers.sanitizeFileName

let private errorFileName (plugin: string) (file: string) =
    $"%s{plugin}--%s{sanitizeFileName file}.json"

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// System.Text.Json's UTF-8 transcoder throws OverflowException when serializing a
/// single string token larger than ~700M chars. A misbehaving plugin can stuff an
/// entire test-suite output into a diagnostic message/detail (observed when many
/// integration tests fail at once), which crashes the reporter and wedges the daemon.
/// Cap each string field far below that bound; the full output is preserved elsewhere
/// (e.g. per-test-run logs), so the on-disk ledger only needs a readable excerpt.
[<Literal>]
let maxFieldChars = 20000

let internal truncateField (s: string) : string =
    if isNull s || s.Length <= maxFieldChars then
        s
    else
        let dropped = s.Length - maxFieldChars
        s.Substring(0, maxFieldChars) + $"… [truncated %d{dropped} chars]"

let private tryDelete path = File.Delete(path)

/// Writes per-plugin per-file JSON error files to a directory.
type FileErrorReporter(errorDir: string) =
    do Directory.CreateDirectory(errorDir) |> ignore

    let serializeEntries (entries: ErrorEntry list) =
        let records =
            entries
            |> List.map (fun e ->
                {| message = truncateField e.Message
                   severity = DiagnosticSeverity.toString e.Severity
                   line = e.Line
                   column = e.Column
                   detail = e.Detail |> Option.map truncateField |})

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
