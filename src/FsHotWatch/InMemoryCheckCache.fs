/// In-memory LRU cache backend for check results.
module FsHotWatch.InMemoryCheckCache

open System.Collections.Generic
open FsHotWatch.Events
open FsHotWatch.CheckCache

/// How many entries the cache may hold.
[<RequireQualifiedAccess>]
type CacheCapacity =
    /// A fixed bound — a memory budget. Below the admitted working set a repeated scan
    /// evicts each entry just before it is needed, so the cache says so
    /// (`ThrashWarning`) rather than looking like one that works.
    | Entries of int
    /// Every admitted (file, project) pair: the bound follows the registered working set.
    | WorkingSet

/// The warning for a fixed bound below the working set, or None.
let thrashWarning (maxEntries: int) (admittedWorkingSet: int) : string option =
    if maxEntries < admittedWorkingSet then
        Some(
            $"check-result cache: maxEntries %d{maxEntries} is below the %d{admittedWorkingSet} files it caches. "
            + "A scan visits every file in the same order, so each entry is evicted just before it is needed "
            + "again: expect ~0 hits, not a partial win. Set cache.maxEntries to \"all\", or narrow "
            + "cache.include/cache.exclude to what you are working on."
        )
    else
        None

/// In-memory LRU cache for check results, holding the live `FileCheckResult` (FCS
/// typed tree and all — measured at roughly 250 KB of retention per entry beyond
/// what the checker already keeps).
///
/// - **Bounded by `capacity`.** `CacheCapacity.WorkingSet` follows the admitted
///   working set `CheckPipeline` reports; `Entries n` is a fixed budget.
/// - **One entry per (file, project).** A new result for a file supersedes the old one
///   outright, since the old key can never be produced again. So growth is bounded by
///   the working set, not by the number of edits made while the daemon runs.
/// - **Scoped by `admits`** (project path → cache it?): an excluded project is never
///   looked up or stored, and does not count toward the working set.
///
/// Thread-safe: a single lock guards the store, the LRU list and the slot map.
type InMemoryCheckCache(capacity: CacheCapacity, admits: string -> bool) =
    let store = Dictionary<string, FileCheckResult>()
    let lruList = LinkedList<string>()
    let lruNodes = Dictionary<string, LinkedListNode<string>>()
    /// (file, project) → the hashed key currently holding that slot's result.
    let slots = Dictionary<struct (string * string), string>()
    let lockObj = obj ()

    let mutable bound =
        match capacity with
        | CacheCapacity.Entries n -> n
        | CacheCapacity.WorkingSet -> 0

    let mutable admittedWorkingSet = 0

    let slotOf (result: FileCheckResult) =
        // Test fixtures pass null options; they share one project slot.
        let project =
            if isNull (box result.ProjectOptions) then
                ""
            else
                result.ProjectOptions.ProjectFileName

        struct (AbsFilePath.value result.File, project)

    // Invariants, all under `lockObj`:
    // - `store` and `lruNodes` always hold the same keys.
    // - The slot of a held key's result maps to that key. `Set` writes the mapping, and
    //   the only way another key takes the slot is a `Set` that first removes this one.

    /// Move a held key to the most-recently-used end of the LRU list.
    let moveToEnd (hashedKey: string) =
        lruList.Remove(lruNodes[hashedKey])
        lruNodes[hashedKey] <- lruList.AddLast(hashedKey)

    /// Add a key to the most-recently-used end of the LRU list.
    let addToEnd (hashedKey: string) =
        let node = lruList.AddLast(hashedKey)
        lruNodes[hashedKey] <- node

    /// Remove one entry from every index. A key no longer held is a no-op: a slot can
    /// still name a key that was invalidated after its result moved to another file.
    let remove (hashedKey: string) =
        match store.TryGetValue(hashedKey) with
        | true, result ->
            lruList.Remove(lruNodes[hashedKey])
            lruNodes.Remove(hashedKey) |> ignore
            store.Remove(hashedKey) |> ignore
            slots.Remove(slotOf result) |> ignore
        | false, _ -> ()

    /// Evict the least-recently-used entry.
    let evictLru () =
        if lruList.Count > 0 then
            remove lruList.First.Value

    let currentThrashWarning () =
        match capacity with
        | CacheCapacity.Entries n -> thrashWarning n (lock lockObj (fun () -> admittedWorkingSet))
        | CacheCapacity.WorkingSet -> None

    /// A fixed-size cache admitting every project.
    new(maxSize: int) = InMemoryCheckCache(CacheCapacity.Entries maxSize, (fun _ -> true))

    /// A cache admitting every project.
    new(capacity: CacheCapacity) = InMemoryCheckCache(capacity, (fun _ -> true))

    /// Entries currently held.
    member _.Count = lock lockObj (fun () -> store.Count)

    /// Current bound on entries.
    member _.Capacity = lock lockObj (fun () -> bound)

    /// How the bound is set.
    member _.CapacityMode = capacity

    /// Set when a fixed bound is below the admitted working set: the thrash case.
    member _.ThrashWarning = currentThrashWarning ()

    interface IScopedCheckCache with
        member _.Admits(projectPath: string) = admits projectPath

        member _.ObserveWorkingSet(filesPerProject: (string * int) list) =
            let admitted =
                filesPerProject
                |> List.filter (fun (project, _) -> admits project)
                |> List.sumBy snd

            lock lockObj (fun () ->
                admittedWorkingSet <- admitted

                match capacity with
                | CacheCapacity.WorkingSet -> bound <- admitted
                | CacheCapacity.Entries _ -> ())

        member _.FitWarning = currentThrashWarning ()

    interface ICheckCacheBackend with
        member _.TryGet(key: CacheKey) : FileCheckResult option =
            let hashedKey = hashCacheKey key

            lock lockObj (fun () ->
                match store.TryGetValue(hashedKey) with
                | true, result ->
                    moveToEnd hashedKey
                    Some result
                | false, _ -> None)

        member _.Set (key: CacheKey) (result: FileCheckResult) : unit =
            let hashedKey = hashCacheKey key
            let slot = slotOf result

            lock lockObj (fun () ->
                match slots.TryGetValue slot with
                | true, previous when previous <> hashedKey -> remove previous
                | _ -> ()

                if lruNodes.ContainsKey(hashedKey) then
                    store[hashedKey] <- result
                    moveToEnd hashedKey
                    // The result may be another file's: its slot must name this key too.
                    slots[slot] <- hashedKey
                elif bound > 0 then
                    if lruList.Count >= bound then
                        evictLru ()

                    store[hashedKey] <- result
                    addToEnd hashedKey
                    slots[slot] <- hashedKey)

        member _.Invalidate(key: CacheKey) : unit =
            let hashedKey = hashCacheKey key
            lock lockObj (fun () -> remove hashedKey)

        member _.Clear() : unit =
            lock lockObj (fun () ->
                store.Clear()
                lruList.Clear()
                lruNodes.Clear()
                slots.Clear())

/// One line for the daemon's startup log naming the check-result cache's state. An
/// absent cache is stated, not implied: silence let an inert cache read as a working one.
let describeCheckCache (backend: ICheckCacheBackend option) : string =
    match backend with
    | None ->
        "check-result cache: OFF — every scan re-asks FCS for every file (\"cache\": \"memory\" in .fshw.json enables it)"
    | Some(:? InMemoryCheckCache as cache) ->
        match cache.CapacityMode with
        | CacheCapacity.WorkingSet -> "check-result cache: in-memory, sized to the working set (every file it caches)"
        | CacheCapacity.Entries n -> $"check-result cache: in-memory, at most %d{n} entries"
    | Some other -> $"check-result cache: %s{other.GetType().Name}"
