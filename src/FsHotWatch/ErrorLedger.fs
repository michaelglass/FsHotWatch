module FsHotWatch.ErrorLedger

open System.Collections.Concurrent

/// A single diagnostic entry from a plugin.
type ErrorEntry =
    { Message: string
      Severity: string
      Line: int
      Column: int }

/// Accumulates per-file errors from plugins. Errors auto-clear when a file
/// is re-checked and passes. Thread-safe via ConcurrentDictionary.
type ErrorLedger() =
    let errors = ConcurrentDictionary<struct (string * string), ErrorEntry list>()

    /// Set errors for a plugin + file. Replaces previous. Empty list clears.
    member _.Report(pluginName: string, filePath: string, entries: ErrorEntry list) =
        if entries.IsEmpty then
            errors.TryRemove(struct (pluginName, filePath)) |> ignore
        else
            errors[struct (pluginName, filePath)] <- entries

    /// Clear all errors for a plugin + file.
    member _.Clear(pluginName: string, filePath: string) =
        errors.TryRemove(struct (pluginName, filePath)) |> ignore

    /// Clear all errors for a plugin.
    member _.ClearPlugin(pluginName: string) =
        for key in errors.Keys do
            let struct (p, _) = key

            if p = pluginName then
                errors.TryRemove(key) |> ignore

    /// Get all errors grouped by file path. Each entry includes the plugin name.
    member _.GetAll() : Map<string, (string * ErrorEntry) list> =
        errors
        |> Seq.collect (fun kvp ->
            let struct (plugin, file) = kvp.Key
            kvp.Value |> List.map (fun e -> file, (plugin, e)))
        |> Seq.groupBy fst
        |> Seq.map (fun (file, entries) -> file, entries |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq

    /// Get errors for a specific plugin only.
    member _.GetByPlugin(pluginName: string) : Map<string, ErrorEntry list> =
        errors
        |> Seq.choose (fun kvp ->
            let struct (p, file) = kvp.Key
            if p = pluginName then Some(file, kvp.Value) else None)
        |> Map.ofSeq

    /// True if any errors exist.
    member _.HasErrors() = not errors.IsEmpty

    /// Total error count across all plugins and files.
    member _.Count() = errors.Values |> Seq.sumBy List.length
