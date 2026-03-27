module FsHotWatch.CheckCache

open System
open System.IO
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

/// Compute a SHA256 hex digest of a string
let private sha256Hex (content: string) : string =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
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

    /// Check if everything is unchanged since last run (fast global guard).
    /// Returns true if nothing changed — caller can skip all per-file checks.
    abstract member IsGlobalCacheValid: unit -> bool

/// Timestamp-based cache key provider (works everywhere, no VCS dependency).
/// Uses file size + last-write-time as the cache key.
type TimestampCacheKeyProvider() =
    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string =
            let normalizedPath = Path.GetFullPath(filePath)

            try
                let info = FileInfo(normalizedPath)
                sha256Hex $"%s{normalizedPath}:%d{info.Length}:%d{info.LastWriteTimeUtc.Ticks}"
            with _ ->
                sha256Hex $"unreadable:%s{normalizedPath}"

        member _.IsGlobalCacheValid() = false

/// jj-based cache key provider. Uses jj commit_id as a fast global guard
/// (if nothing in the tree changed, skip everything). Falls back to timestamp
/// for per-file hashing since jj commit_id is tree-wide, not per-file.
type JjCacheKeyProvider() =
    let timestampFallback = TimestampCacheKeyProvider() :> ICacheKeyProvider

    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string =
            // Per-file hash uses timestamp (jj commit_id is tree-wide, not per-file)
            timestampFallback.GetFileHash(filePath)

        member _.IsGlobalCacheValid() : bool =
            // If jj commit_id hasn't changed since last check, nothing in the tree changed
            match JjHelper.currentCommitId () with
            | Some _ -> false // TODO: compare with stored commit_id once file backend exists
            | None -> false

/// Computes ProjectOptionsHash from FSharpProjectOptions
let getProjectOptionsHash (options: FSharpProjectOptions) : string =
    let parts =
        [ string options.ProjectFileName
          String.concat "|" options.SourceFiles
          string (Array.length options.ReferencedProjects)
          String.concat "|" options.OtherOptions ]

    sha256Hex (String.concat "||" parts)

/// Compute a CacheKey for a file using the given provider
let makeCacheKey (provider: ICacheKeyProvider) (filePath: string) (options: FSharpProjectOptions) : CacheKey =
    { FileHash = provider.GetFileHash(filePath)
      ProjectOptionsHash = getProjectOptionsHash options }
