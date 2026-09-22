/// In-memory LRU cache backend for check results.
module FsHotWatch.InMemoryCheckCache

open System.Collections.Generic
open FsHotWatch.Events
open FsHotWatch.CheckCache

/// In-memory LRU cache for check results, holding the live `FileCheckResult` (FCS
/// typed tree and all — measured at roughly 250 KB of retention per entry beyond
/// what the checker already keeps).
///
/// Two properties make it useful to a daemon whose dominant access pattern is a full
/// scan that visits every file in the same order:
///
/// - **Sized to the working set.** An LRU smaller than the working set, driven by a
///   repeated sequential scan, evicts every entry just before it is needed again: its
///   hit rate is zero, not merely low. `maxSize` is therefore a floor, and
///   `EnsureCapacity` (called by `CheckPipeline` as projects register) raises it to
///   the number of (file, project) pairs registered.
/// - **One entry per (file, project).** A new result for a file supersedes the old one
///   outright, since the old key can never be produced again. So growth is bounded by
///   the working set, not by the number of edits made while the daemon runs.
///
/// Thread-safe: a single lock guards the store, the LRU list and the slot map.
type InMemoryCheckCache(maxSize: int) =
    let store = Dictionary<string, FileCheckResult>()
    let lruList = LinkedList<string>()
    let lruNodes = Dictionary<string, LinkedListNode<string>>()
    /// (file, project) → the hashed key currently holding that slot's result.
    let slots = Dictionary<struct (string * string), string>()
    let lockObj = obj ()
    let mutable capacity = maxSize

    let slotOf (result: FileCheckResult) =
        // Test fixtures pass null options; they share one project slot.
        let project =
            if isNull (box result.ProjectOptions) then
                ""
            else
                result.ProjectOptions.ProjectFileName

        struct (AbsFilePath.value result.File, project)

    /// Move a key to the most-recently-used end of the LRU list.
    let moveToEnd (hashedKey: string) =
        match lruNodes.TryGetValue(hashedKey) with
        | true, node ->
            lruList.Remove(node)
            let newNode = lruList.AddLast(hashedKey)
            lruNodes[hashedKey] <- newNode
        | false, _ -> ()

    /// Add a key to the most-recently-used end of the LRU list.
    let addToEnd (hashedKey: string) =
        let node = lruList.AddLast(hashedKey)
        lruNodes[hashedKey] <- node

    /// Remove one entry from every index.
    let remove (hashedKey: string) =
        match lruNodes.TryGetValue(hashedKey) with
        | true, node ->
            lruList.Remove(node)
            lruNodes.Remove(hashedKey) |> ignore

            match store.TryGetValue(hashedKey) with
            | true, result ->
                let slot = slotOf result

                match slots.TryGetValue slot with
                | true, owner when owner = hashedKey -> slots.Remove slot |> ignore
                | _ -> ()

                store.Remove(hashedKey) |> ignore
            | false, _ -> ()
        | false, _ -> ()

    /// Evict the least-recently-used entry.
    let evictLru () =
        if lruList.Count > 0 then
            remove lruList.First.Value

    /// Entries currently held.
    member _.Count = lock lockObj (fun () -> store.Count)

    /// Current bound on entries: the configured size, or the working set if larger.
    member _.Capacity = lock lockObj (fun () -> capacity)

    /// Raise the bound to at least `workingSet` entries. Never shrinks.
    member _.EnsureCapacity(workingSet: int) =
        lock lockObj (fun () ->
            if workingSet > capacity then
                capacity <- workingSet)

    interface IWorkingSetSized with
        member this.EnsureCapacity(workingSet: int) = this.EnsureCapacity workingSet

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
                else
                    if lruList.Count >= capacity then
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
        $"check-result cache: in-memory, %d{cache.Capacity}-entry floor, grows to the working set as projects register"
    | Some other -> $"check-result cache: %s{other.GetType().Name}"
