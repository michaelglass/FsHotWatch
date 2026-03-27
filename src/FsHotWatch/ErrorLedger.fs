module FsHotWatch.ErrorLedger

open System.Collections.Concurrent
open System.Threading

/// A single diagnostic entry from a plugin.
type ErrorEntry =
    { Message: string
      Severity: string
      Line: int
      Column: int }

/// Accumulates per-file errors from plugins. Errors auto-clear when a file
/// is re-checked and passes. Thread-safe via ConcurrentDictionary.
/// Supports optional version-guarded updates: when a version is provided,
/// stale updates (version < last accepted) are silently ignored.
type ErrorLedger() =
    let errors = ConcurrentDictionary<struct (string * string), ErrorEntry list>()
    let versions = ConcurrentDictionary<struct (string * string), int64>()

    /// Atomically check and advance the version for a key.
    /// Returns true if the version was accepted (>= last seen), false if stale.
    let tryAcceptVersion key (v: int64) =
        let mutable accepted = false

        versions.AddOrUpdate(
            key,
            (fun _ ->
                accepted <- true
                v),
            (fun _ last ->
                if v >= last then
                    accepted <- true
                    v
                else
                    accepted <- false
                    last)
        )
        |> ignore

        accepted

    /// Set errors for a plugin + file. Replaces previous. Empty list clears.
    /// When version is provided, updates with version < last accepted are ignored.
    member _.Report(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        let key = struct (pluginName, filePath)

        let accepted =
            match version with
            | Some v -> tryAcceptVersion key v
            | None -> true

        if accepted then
            if entries.IsEmpty then
                errors.TryRemove(key) |> ignore
            else
                errors[key] <- entries

    /// Clear all errors for a plugin + file.
    /// When version is provided, clears with version < last accepted are ignored.
    member _.Clear(pluginName: string, filePath: string, ?version: int64) =
        let key = struct (pluginName, filePath)

        let accepted =
            match version with
            | Some v -> tryAcceptVersion key v
            | None -> true

        if accepted then
            errors.TryRemove(key) |> ignore

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
