module FsHotWatch.ContentDedup

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography

/// Track file content hashes to skip watcher events where mtime changed but content didn't.
let private fileHashes = ConcurrentDictionary<string, byte[]>()

/// Returns true if the file content actually changed since last check.
/// Updates the stored hash on change. Returns true for new/deleted files.
let hasContentChanged (path: string) =
    try
        if not (File.Exists(path)) then
            fileHashes.TryRemove(path) |> ignore
            true
        else
            let content = File.ReadAllBytes(path)
            let hash = SHA256.HashData(content)

            match fileHashes.TryGetValue(path) with
            | true, previous when ReadOnlySpan(previous).SequenceEqual(ReadOnlySpan(hash)) -> false
            | _ ->
                fileHashes[path] <- hash
                true
    with
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true
