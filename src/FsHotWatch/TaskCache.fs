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
    /// Test run started; captured so replays emit a clean lifecycle.
    | CachedTestRunStarted of FsHotWatch.Events.TestRunStarted
    /// Test progress emission; a per-group delta captured for replay.
    | CachedTestProgress of FsHotWatch.Events.TestProgress
    /// Test run completed; the canonical summary event.
    | CachedTestRunCompleted of FsHotWatch.Events.TestRunCompleted
    /// A command completed event captured for replay.
    | CachedCommandCompleted of FsHotWatch.Events.CommandCompletedResult

/// The terminal outcome a cache entry may replay. Scope rule: a cache entry may
/// only assert facts derivable from its key's scope.
///
/// Per-file entries (composite key `File = Some _`) are keyed on ONE file's content,
/// so they cannot testify to a whole-session claim — yet plugins build their status
/// summaries from whole-session state (analyzers' `DiagnosticsByFile`, lint's
/// `WarningsByFile`). So the `CachedFile*` variants carry no summary (replay derives
/// one from the live ledger) and no timestamp (replay re-stamps `now`).
///
/// Whole-run entries (`File = None`, e.g. BuildPlugin keyed on the full project-graph
/// content hash) store a verdict that IS a pure function of the key, so `CachedRun*`
/// keeps it for verbatim replay. Non-terminal statuses (Idle/Running) are
/// unrepresentable here by construction.
[<NoComparison>]
type CachedStatus =
    /// Per-file completion: elapsed only — the summary is derived at replay.
    | CachedFileCompleted of elapsed: System.TimeSpan
    /// Per-file failure: the diagnosis and elapsed — the summary is derived at replay.
    | CachedFileFailed of error: string * elapsed: System.TimeSpan
    /// Whole-run completion: the stored verdict is a pure function of the key.
    | CachedRunCompleted of verdict: FsHotWatch.Events.RunVerdict
    /// Whole-run failure: diagnosis plus the key-pure verdict.
    | CachedRunFailed of error: string * verdict: FsHotWatch.Events.RunVerdict

/// The full result of a plugin processing an event, captured for replay.
[<NoComparison>]
type TaskCacheResult =
    {
        /// Content-based key used to validate cache freshness.
        CacheKey: ContentHash
        /// Errors produced by the plugin, keyed by file path.
        Errors: (string * FsHotWatch.ErrorLedger.ErrorEntry list) list
        /// Terminal outcome of the plugin after processing, scoped to the key.
        Status: CachedStatus
        /// Side-effect events emitted by the plugin during processing.
        EmittedEvents: CachedEvent list
    }

/// Why a cache lookup produced nothing. A cold start must be able to say WHICH
/// input moved, not merely that it missed: with content-addressed keys shared
/// across checkouts, "miss" is the only observable difference between a key that
/// correctly noticed an edit and a key that is accidentally salted with something
/// machine-local (an absolute path, a temp directory). Naming the differing label
/// is what makes the second case visible instead of merely slow.
[<RequireQualifiedAccess>]
type CacheMissReason =
    /// Nothing has ever been written under this plugin (+ file). A genuinely cold key.
    | NoEntryForKey
    /// An entry for this plugin (+ file) exists under a DIFFERENT key, and these
    /// merkle input labels are the ones whose values differ. Sorted, so the reason
    /// is stable across runs.
    | InputsChanged of labels: string list
    /// An entry exists under a different key, but the two keys cannot be compared:
    /// one of them was not minted by `merkleCacheKey` (e.g. a commit-id key), or the
    /// stored entry predates input recording. Honest "different, reason unknown".
    | InputsNotComparable
    /// The entry file was found but could not be read as a result — corrupt, written
    /// by another entry format, or carrying paths that cannot be rebound into THIS
    /// checkout. Always a miss, never a partial replay.
    | UnreadableEntry of detail: string

/// The outcome of a cache lookup. `CacheMiss` carries its reason so the caller can
/// log why a task is about to be recomputed.
[<NoComparison>]
type CacheLookup =
    | CacheHit of TaskCacheResult
    | CacheMiss of CacheMissReason

/// Human-readable rendering of a miss reason, for the daemon log.
[<RequireQualifiedAccess>]
module CacheMissReason =
    let describe (reason: CacheMissReason) =
        match reason with
        | CacheMissReason.NoEntryForKey -> "no-entry"
        | CacheMissReason.InputsChanged labels -> "inputs-changed:" + String.concat "," (List.sort labels)
        | CacheMissReason.InputsNotComparable -> "key-differs (inputs not comparable)"
        | CacheMissReason.UnreadableEntry detail -> $"unreadable-entry (%s{detail})"

/// Per-label digests of the inputs that produced a merkle cache key, remembered in
/// process so a miss can be explained.
///
/// Diagnostics only. Nothing here participates in a hit/miss DECISION — the decision
/// is `storedKey = requestedKey`, exactly as before. A registry that has forgotten a
/// key degrades the reason to `InputsNotComparable`; it can never turn a miss into a
/// hit.
module KeyFingerprints =
    open System.Collections.Concurrent

    /// Bounded, because a long-lived daemon mints a key per file per event and this
    /// map would otherwise grow without limit. On overflow the whole map is dropped
    /// rather than evicted one entry at a time: losing a fingerprint costs a less
    /// specific miss reason and nothing else, so the simplest bound is the right one.
    [<Literal>]
    let internal Capacity = 8192

    let private fingerprints = ConcurrentDictionary<string, (string * string) list>()

    /// Digest of one input VALUE. Truncated: the fingerprint is stored in every cache
    /// entry, and 16 hex characters is far past the point where two genuinely
    /// different values collide often enough to mislabel a miss reason.
    let digest (value: string) =
        (FsHotWatch.CheckCache.sha256Hex value).Substring(0, 16)

    /// Remember the labelled digests behind `hash`.
    let record (hash: string) (labelDigests: (string * string) list) =
        if fingerprints.Count >= Capacity then
            fingerprints.Clear()

        fingerprints[hash] <- labelDigests

    /// The labelled digests behind `hash`, if this process minted it.
    let tryGet (hash: string) =
        match fingerprints.TryGetValue hash with
        | true, inputs -> Some inputs
        | false, _ -> None

    /// The labels on which two fingerprints disagree — including labels present in
    /// only one of them, because an input APPEARING or DISAPPEARING is exactly as
    /// much of a difference as its value moving.
    let differingLabels (stored: (string * string) list) (requested: (string * string) list) =
        let storedMap = Map.ofList stored
        let requestedMap = Map.ofList requested

        Set.union (storedMap |> Map.keys |> Set.ofSeq) (requestedMap |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun label -> Map.tryFind label storedMap <> Map.tryFind label requestedMap)
        |> Set.toList

/// Explain a key mismatch: name the differing inputs when both fingerprints are
/// known, and say so honestly when they are not.
///
/// `storedInputs` is `None` when the entry carries no recorded fingerprint (a
/// commit-id key, or an entry written before fingerprints were stored).
let missReasonForKeys (storedInputs: (string * string) list option) (requestedKey: ContentHash) : CacheMissReason =
    match storedInputs, KeyFingerprints.tryGet (ContentHash.value requestedKey) with
    | Some stored, Some requested ->
        match KeyFingerprints.differingLabels stored requested with
        // Different hashes with identical labelled digests means the difference lies
        // somewhere the fingerprint does not see. Do not claim to know which input.
        | [] -> CacheMissReason.InputsNotComparable
        | labels -> CacheMissReason.InputsChanged labels
    | _ -> CacheMissReason.InputsNotComparable

/// Cache for plugin task results.
type ITaskCache =
    /// Try to retrieve a cached result. Returns Some only when the compositeKey
    /// matches AND the stored result's CacheKey matches the provided cacheKey.
    abstract TryGet: compositeKey: CompositeKey -> cacheKey: ContentHash -> TaskCacheResult option
    /// As `TryGet`, but a miss carries WHY. Same hit/miss decision by construction —
    /// implementations define `TryGet` in terms of this one so the two cannot drift.
    abstract Lookup: compositeKey: CompositeKey -> cacheKey: ContentHash -> CacheLookup
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

    /// The key each composite was last written under, so a miss can name the inputs
    /// that moved without scanning every entry. Mirrors `FileTaskCache`'s `livePaths`.
    let latestKey = ConcurrentDictionary<CompositeKey, ContentHash>()

    let lookup (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        match cache.TryGetValue(struct (compositeKey, cacheKey)) with
        | true, result -> CacheHit result
        | _ ->
            match latestKey.TryGetValue compositeKey with
            | true, storedKey ->
                missReasonForKeys (KeyFingerprints.tryGet (ContentHash.value storedKey)) cacheKey
                |> CacheMiss
            | false, _ -> CacheMiss CacheMissReason.NoEntryForKey

    let tryGet (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        match lookup compositeKey cacheKey with
        | CacheHit result -> Some result
        | CacheMiss _ -> None

    let set (compositeKey: CompositeKey) (cacheKey: ContentHash) (result: TaskCacheResult) =
        cache.[struct (compositeKey, cacheKey)] <- result
        latestKey[compositeKey] <- cacheKey

    let clear () =
        cache.Clear()
        latestKey.Clear()

    let clearPlugin (plugin: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.Plugin = plugin then
                cache.TryRemove(key) |> ignore
                latestKey.TryRemove(compKey) |> ignore

    let clearFile (file: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.File = Some file then
                cache.TryRemove(key) |> ignore
                latestKey.TryRemove(compKey) |> ignore

    let clearPluginFile (plugin: string) (file: string) =
        for key in cache.Keys |> Seq.toArray do
            let struct (compKey, _) = key

            if compKey.Plugin = plugin && compKey.File = Some file then
                cache.TryRemove(key) |> ignore
                latestKey.TryRemove(compKey) |> ignore

    /// Try to retrieve a cached result.
    member _.TryGet(compositeKey: CompositeKey, cacheKey: ContentHash) = tryGet compositeKey cacheKey

    /// Retrieve a cached result, or the reason there is none.
    member _.Lookup(compositeKey: CompositeKey, cacheKey: ContentHash) = lookup compositeKey cacheKey

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
        member _.Lookup compositeKey cacheKey = lookup compositeKey cacheKey
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

/// Content-merkle cache key. Hashes a list of (label, value) inputs into a stable
/// ContentHash that depends only on the values — no commit_id, so a file reverted to
/// its earlier content hits its earlier cache entry.
///
/// Encoding is length-prefixed to avoid `("x","ab"),("y","")` colliding with
/// `("x","a"),("y","b")`. Sorted by label to make order-of-construction
/// irrelevant at call sites.
let merkleCacheKey (inputs: (string * string) list) : ContentHash =
    let sb = System.Text.StringBuilder()

    for (label, value) in inputs |> List.sortBy fst do
        sb.Append(label.Length) |> ignore
        sb.Append(':') |> ignore
        sb.Append(label) |> ignore
        sb.Append('|') |> ignore
        sb.Append(value.Length) |> ignore
        sb.Append(':') |> ignore
        sb.Append(value) |> ignore
        sb.Append('|') |> ignore

    let hash = FsHotWatch.CheckCache.sha256Hex (sb.ToString())

    // Remember what went into this key so a later MISS can name the input that moved.
    // Diagnostics only: the returned hash is byte-for-byte what it was before this
    // line existed, so no hit/miss decision depends on the registry.
    KeyFingerprints.record hash (inputs |> List.map (fun (label, value) -> label, KeyFingerprints.digest value))

    ContentHash.create hash
