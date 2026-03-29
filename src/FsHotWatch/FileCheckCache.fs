/// File-based cache backend for cold start improvements.
/// Stores partial FileCheckResult data to disk as JSON.
module FsHotWatch.FileCheckCache

open System.IO
open FsHotWatch.Logging
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.CheckCache

/// Serializable representation of a cached check result.
/// FCS types can't be serialized, so we store what we can.
type CachedFileEntry = { File: string; Version: int64 }

let private serializerOptions = JsonSerializerOptions(WriteIndented = true)

/// File-based cache backend for cold start improvements.
/// Stores partial FileCheckResult data to disk as JSON.
/// On TryGet, returns FileCheckResult with Unchecked.defaultof for FCS types.
type FileCheckCache(cacheDir: string) =
    do
        if not (Directory.Exists(cacheDir)) then
            Directory.CreateDirectory(cacheDir) |> ignore

    let cacheFilePath (keyHash: string) =
        Path.Combine(cacheDir, $"%s{keyHash}.json")

    interface ICheckCacheBackend with
        member _.TryGet(key: CacheKey) : FileCheckResult option =
            let keyHash = hashCacheKey key
            let path = cacheFilePath keyHash

            try
                let json = File.ReadAllText(path)
                let entry = JsonSerializer.Deserialize<CachedFileEntry>(json)

                Some
                    { File = entry.File
                      Source = ""
                      ParseResults = Unchecked.defaultof<_>
                      CheckResults = None
                      ProjectOptions = Unchecked.defaultof<_>
                      Version = entry.Version }
            with ex ->
                Logging.debug "cache" $"FileCheckCache TryGet failed: %s{ex.Message}"
                None

        member _.Set (key: CacheKey) (result: FileCheckResult) : unit =
            let keyHash = hashCacheKey key
            let path = cacheFilePath keyHash

            try
                let entry =
                    { File = result.File
                      Version = result.Version }

                let json = JsonSerializer.Serialize(entry, serializerOptions)
                File.WriteAllText(path, json)
            with ex ->
                Logging.error "file-cache" $"Failed to write cache %s{keyHash}: %s{ex.Message}"

        member _.Invalidate(key: CacheKey) : unit =
            let keyHash = hashCacheKey key
            let path = cacheFilePath keyHash

            try
                File.Delete(path)
            with
            | :? FileNotFoundException -> ()
            | ex -> Logging.error "file-cache" $"Failed to invalidate cache: %s{ex.Message}"

        member _.Clear() : unit =
            try
                if Directory.Exists(cacheDir) then
                    for file in Directory.GetFiles(cacheDir, "*.json") do
                        File.Delete(file)
            with ex ->
                Logging.error "file-cache" $"Failed to clear cache: %s{ex.Message}"
