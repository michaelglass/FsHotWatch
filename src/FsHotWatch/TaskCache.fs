/// Cache for plugin task results, enabling replay of cached side effects.
module FsHotWatch.TaskCache

open System.Collections.Concurrent
open FsHotWatch.Events

/// Structured key for task cache entries, replacing ambiguous "plugin--file" strings.
[<Struct>]
type CompositeKey = { Plugin: string; File: string option }

/// A captured side effect emitted by a plugin during execution.
type CachedEvent =
    /// A build completed event captured for replay.
    | CachedBuildCompleted of FsHotWatch.Events.BuildResult
    /// A test completed event captured for replay.
    | CachedTestCompleted of FsHotWatch.Events.TestResults
    /// A command completed event captured for replay.
    | CachedCommandCompleted of FsHotWatch.Events.CommandCompletedResult

/// The full result of a plugin processing an event, captured for replay.
[<NoComparison>]
type TaskCacheResult =
    {
        /// Content-based key used to validate cache freshness.
        CacheKey: ContentHash
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
    abstract TryGet: compositeKey: CompositeKey -> cacheKey: ContentHash -> TaskCacheResult option
    /// Store a result under the given compositeKey.
    abstract Set: compositeKey: CompositeKey -> cacheKey: ContentHash -> result: TaskCacheResult -> unit
    /// Remove all cached entries.
    abstract Clear: unit -> unit
    /// Remove entries for a specific plugin.
    abstract ClearPlugin: plugin: string -> unit
    /// Remove entries for a specific file.
    abstract ClearFile: file: string -> unit
    /// Remove the specific plugin+file entry.
    abstract ClearPluginFile: plugin: string -> file: string -> unit

/// In-memory implementation using ConcurrentDictionary.
/// Keyed by (compositeKey, cacheKey) so multiple versions coexist.
type InMemoryTaskCache() =
    let cache =
        ConcurrentDictionary<struct (CompositeKey * ContentHash), TaskCacheResult>()

    let tryGet (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        match cache.TryGetValue(struct (compositeKey, cacheKey)) with
        | true, result -> Some result
        | _ -> None

    let set (compositeKey: CompositeKey) (cacheKey: ContentHash) (result: TaskCacheResult) =
        cache.[struct (compositeKey, cacheKey)] <- result

    let clear () = cache.Clear()

    let clearPlugin (plugin: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.Plugin = plugin then
                cache.TryRemove(key) |> ignore

    let clearFile (file: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.File = Some file then
                cache.TryRemove(key) |> ignore

    let clearPluginFile (plugin: string) (file: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.Plugin = plugin && compKey.File = Some file then
                cache.TryRemove(key) |> ignore

    /// Try to retrieve a cached result.
    member _.TryGet(compositeKey: CompositeKey, cacheKey: ContentHash) = tryGet compositeKey cacheKey

    /// Store a result under the given compositeKey.
    member _.Set(compositeKey: CompositeKey, cacheKey: ContentHash, result: TaskCacheResult) =
        set compositeKey cacheKey result

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

/// Cache key: jj commit_id, salted per-event by `getSalt`. None for Custom events (uncacheable).
/// Plugins whose cache validity depends on extra state beyond the commit (e.g. a config file
/// whose edits don't change the commit) should salt the key with a hash of that state.
let saltedCacheKey
    (getSalt: PluginEvent<'Msg> -> string)
    (getCommitId: unit -> string option)
    (event: PluginEvent<'Msg>)
    : ContentHash option =
    match event with
    | Custom _ -> None
    | _ ->
        getCommitId ()
        |> Option.map (fun commit ->
            match getSalt event with
            | "" -> ContentHash.create commit
            | salt -> ContentHash.create $"%s{commit}:%s{salt}")

/// Default cache key: jj commit_id for framework events, None for Custom events (uncacheable).
let defaultCacheKey (getCommitId: unit -> string option) (event: PluginEvent<'Msg>) : ContentHash option =
    saltedCacheKey (fun _ -> "") getCommitId event

/// Build an optional CacheKey from an optional getCommitId function.
/// Convenience for plugins that use the default cache key.
let optionalCacheKey (getCommitId: (unit -> string option) option) =
    getCommitId |> Option.map defaultCacheKey

/// Build an optional salted CacheKey from an optional getCommitId function.
let optionalSaltedCacheKey (getSalt: PluginEvent<'Msg> -> string) (getCommitId: (unit -> string option) option) =
    getCommitId |> Option.map (saltedCacheKey getSalt)
