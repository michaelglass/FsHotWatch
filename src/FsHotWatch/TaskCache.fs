/// Cache for plugin task results, enabling replay of cached side effects.
module FsHotWatch.TaskCache

open System.Collections.Concurrent
open FsHotWatch.Events

/// A captured side effect emitted by a plugin during execution.
type CachedEvent =
    /// A build completed event captured for replay.
    | CachedBuildCompleted of FsHotWatch.Events.BuildResult
    /// A test completed event captured for replay.
    | CachedTestCompleted of FsHotWatch.Events.TestResults

/// The full result of a plugin processing an event, captured for replay.
[<NoComparison>]
type TaskCacheResult =
    {
        /// Content-based key used to validate cache freshness.
        CacheKey: string
        /// Errors produced by the plugin, keyed by file path.
        Errors: (string * FsHotWatch.ErrorLedger.ErrorEntry list) list
        /// Final status of the plugin after processing.
        Status: FsHotWatch.Events.PluginStatus
        /// Side-effect events emitted by the plugin during processing.
        EmittedEvents: CachedEvent list
    }

/// Cache for plugin task results.
type ITaskCache =
    /// Try to retrieve a cached result. Returns Some only when the compositeKey
    /// matches AND the stored result's CacheKey matches the provided cacheKey.
    abstract TryGet: compositeKey: string -> cacheKey: string -> TaskCacheResult option
    /// Store a result under the given compositeKey.
    abstract Set: compositeKey: string -> cacheKey: string -> result: TaskCacheResult -> unit
    /// Remove all cached entries.
    abstract Clear: unit -> unit
    /// Remove entries for a specific plugin (keys starting with "{plugin}--" or equal to "{plugin}").
    abstract ClearPlugin: plugin: string -> unit
    /// Remove entries for a specific file (keys ending with "--{file}").
    abstract ClearFile: file: string -> unit
    /// Remove the specific "{plugin}--{file}" entry.
    abstract ClearPluginFile: plugin: string -> file: string -> unit

/// In-memory implementation using ConcurrentDictionary.
type InMemoryTaskCache() =
    let cache = ConcurrentDictionary<string, TaskCacheResult>()

    let tryGet (compositeKey: string) (cacheKey: string) =
        match cache.TryGetValue(compositeKey) with
        | true, result when result.CacheKey = cacheKey -> Some result
        | _ -> None

    let set (compositeKey: string) (_cacheKey: string) (result: TaskCacheResult) = cache.[compositeKey] <- result

    let clear () = cache.Clear()

    let clearPlugin (plugin: string) =
        let prefix = plugin + "--"

        for key in cache.Keys do
            if key.StartsWith(prefix) || key = plugin then
                cache.TryRemove(key) |> ignore

    let clearFile (file: string) =
        let suffix = "--" + file

        for key in cache.Keys do
            if key.EndsWith(suffix) then
                cache.TryRemove(key) |> ignore

    let clearPluginFile (plugin: string) (file: string) =
        let key = plugin + "--" + file
        cache.TryRemove(key) |> ignore

    /// Try to retrieve a cached result. Returns Some only when the compositeKey
    /// matches AND the stored result's CacheKey matches the provided cacheKey.
    member _.TryGet(compositeKey: string, cacheKey: string) = tryGet compositeKey cacheKey

    /// Store a result under the given compositeKey.
    member _.Set(compositeKey: string, cacheKey: string, result: TaskCacheResult) = set compositeKey cacheKey result

    /// Remove all cached entries.
    member _.Clear() = clear ()

    /// Remove entries for a specific plugin.
    member _.ClearPlugin(plugin: string) = clearPlugin plugin

    /// Remove entries for a specific file.
    member _.ClearFile(file: string) = clearFile file

    /// Remove the specific plugin+file entry.
    member _.ClearPluginFile(plugin: string, file: string) = clearPluginFile plugin file

    interface ITaskCache with
        member _.TryGet compositeKey cacheKey = tryGet compositeKey cacheKey
        member _.Set compositeKey cacheKey result = set compositeKey cacheKey result
        member _.Clear() = clear ()
        member _.ClearPlugin plugin = clearPlugin plugin
        member _.ClearFile file = clearFile file
        member _.ClearPluginFile plugin file = clearPluginFile plugin file

/// Default cache key: jj commit_id for framework events, None for Custom events (uncacheable).
let defaultCacheKey (getCommitId: unit -> string option) (event: PluginEvent<'Msg>) : string option =
    match event with
    | Custom _ -> None
    | _ -> getCommitId ()
