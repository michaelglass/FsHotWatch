module FsHotWatch.CheckCache

open System
open System.IO
open System.Security.Cryptography
open FsHotWatch.Logging
open System.Text
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

/// Compute a SHA256 hex digest of a string
let sha256Hex (content: string) : string =
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content))
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

/// Hash a CacheKey to produce a stable, unique identifier
let hashCacheKey (key: CacheKey) : string =
    sha256Hex $"%s{key.FileHash}||%s{key.ProjectOptionsHash}"

/// Backend interface for storing/retrieving cached results
type ICheckCacheBackend =
    /// Retrieve a cached result if it exists
    abstract member TryGet: key: CacheKey -> FileCheckResult option

    /// Store a check result in the cache
    abstract member Set: key: CacheKey -> result: FileCheckResult -> unit

    /// Invalidate a specific cache entry
    abstract member Invalidate: key: CacheKey -> unit

    /// Clear all cache entries
    abstract member Clear: unit -> unit

/// Pluggable strategy for computing file hashes (cache keys)
type ICacheKeyProvider =
    /// Compute a content hash for a file
    abstract member GetFileHash: filePath: string -> string

/// Timestamp-based cache key provider (works everywhere, no VCS dependency).
/// Uses file size + last-write-time as the cache key.
type TimestampCacheKeyProvider() =
    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string =
            let normalizedPath = Path.GetFullPath(filePath)

            try
                let info = FileInfo(normalizedPath)
                sha256Hex $"%s{normalizedPath}:%d{info.Length}:%d{info.LastWriteTimeUtc.Ticks}"
            with ex ->
                Logging.debug "cache" $"Could not stat %s{normalizedPath}: %s{ex.Message}"
                sha256Hex $"unreadable:%s{normalizedPath}"

/// Computes ProjectOptionsHash from FSharpProjectOptions
let getProjectOptionsHash (options: FSharpProjectOptions) : string =
    let parts =
        [ string options.ProjectFileName
          String.concat "|" options.SourceFiles
          string (Array.length options.ReferencedProjects)
          String.concat "|" options.OtherOptions ]

    sha256Hex (String.concat "||" parts)

/// jj-based cache key provider. Per-file hashing uses timestamp
/// (jj commit_id is tree-wide, not per-file).
/// Preserved as a distinct type so jj-specific optimizations
/// (e.g., global cache guard via commit_id comparison) can be added later.
type JjCacheKeyProvider(_repoRoot: string) =
    let timestampFallback = TimestampCacheKeyProvider() :> ICacheKeyProvider

    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string = timestampFallback.GetFileHash(filePath)

/// Compute a CacheKey for a file using the given provider
let makeCacheKey (provider: ICacheKeyProvider) (filePath: string) (options: FSharpProjectOptions) : CacheKey =
    { FileHash = provider.GetFileHash(filePath)
      ProjectOptionsHash = getProjectOptionsHash options }
