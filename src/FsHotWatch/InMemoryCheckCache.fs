/// In-memory LRU cache backend for check results.
module FsHotWatch.InMemoryCheckCache

open System.Collections.Concurrent
open System.Collections.Generic
open FsHotWatch.Events
open FsHotWatch.CheckCache

/// In-memory LRU cache for check results with configurable max size.
/// Thread-safe using lock for LRU tracking.
type InMemoryCheckCache(maxSize: int) =
    let store = ConcurrentDictionary<string, FileCheckResult>()
    let lruList = LinkedList<string>()
    let lruNodes = Dictionary<string, LinkedListNode<string>>()
    let lockObj = obj ()

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

    /// Evict the least-recently-used entry.
    let evictLru () =
        if lruList.Count > 0 then
            let oldest = lruList.First.Value
            lruList.RemoveFirst()
            lruNodes.Remove(oldest) |> ignore
            store.TryRemove(oldest) |> ignore

    interface ICheckCacheBackend with
        member _.TryGet(key: CacheKey) : FileCheckResult option =
            let hashedKey = hashCacheKey key

            match store.TryGetValue(hashedKey) with
            | true, result ->
                lock lockObj (fun () -> moveToEnd hashedKey)
                Some result
            | false, _ -> None

        member _.Set (key: CacheKey) (result: FileCheckResult) : unit =
            let hashedKey = hashCacheKey key

            lock lockObj (fun () ->
                if lruNodes.ContainsKey(hashedKey) then
                    // Key exists: update value and move to end
                    store[hashedKey] <- result
                    moveToEnd hashedKey
                else
                    // New key: evict if at capacity, then add
                    if lruList.Count >= maxSize then
                        evictLru ()

                    store[hashedKey] <- result
                    addToEnd hashedKey)

        member _.Invalidate(key: CacheKey) : unit =
            let hashedKey = hashCacheKey key

            lock lockObj (fun () ->
                match lruNodes.TryGetValue(hashedKey) with
                | true, node ->
                    lruList.Remove(node)
                    lruNodes.Remove(hashedKey) |> ignore
                    store.TryRemove(hashedKey) |> ignore
                | false, _ -> ())

        member _.Clear() : unit =
            lock lockObj (fun () ->
                store.Clear()
                lruList.Clear()
                lruNodes.Clear())
