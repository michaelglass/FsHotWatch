module FsHotWatch.ContentDedup

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography

/// Compute the change verdict for `path` against `store`, mutating `store`.
/// Extracted so both the per-instance `Tracker` and the process-global default
/// share one implementation. Returns true if the content actually changed since
/// the store last saw it (and true for new/deleted/unreadable files).
let private evaluate (store: ConcurrentDictionary<string, byte[]>) (path: string) =
    try
        if not (File.Exists(path)) then
            store.TryRemove(path) |> ignore
            true
        else
            let content = File.ReadAllBytes(path)
            let hash = SHA256.HashData(content)

            match store.TryGetValue(path) with
            | true, previous when ReadOnlySpan(previous).SequenceEqual(ReadOnlySpan(hash)) -> false
            | _ ->
                store[path] <- hash
                true
    with
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true

/// Per-daemon content-hash store. Scoped per instance (like `ProcessRegistry.Registry`)
/// so a hash written by one daemon never suppresses a genuine first-observation change
/// event in another daemon sharing the process — the key is the absolute file path, so
/// daemon A's stale entry would collide exactly with daemon B's first read.
type Tracker() =
    let fileHashes = ConcurrentDictionary<string, byte[]>()

    /// Returns true if the file content actually changed since this tracker last
    /// checked it. Updates the stored hash on change. Returns true for
    /// new/deleted files.
    member _.HasContentChanged(path: string) = evaluate fileHashes path

/// Process-global fallback tracker backing the module-level `hasContentChanged`.
let private defaultTracker = Tracker()

/// Returns true if the file content actually changed since the process-global default
/// tracker last checked it. Returns true for new/deleted files. Daemons do NOT use this
/// path — they hold a per-instance `Tracker` so cross-daemon hashes never collide.
let hasContentChanged (path: string) = defaultTracker.HasContentChanged(path)
