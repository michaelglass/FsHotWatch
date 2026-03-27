module FsHotWatch.CheckCache

open System
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

/// Hash a CacheKey to produce a stable, unique identifier
let hashCacheKey (key: CacheKey) : string =
    use sha = SHA256.Create()
    let content = $"%s{key.FileHash}||%s{key.ProjectOptionsHash}"
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

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

/// Computes FileHash from file content (using jj commit_id when available, falls back to mtime)
let getFileHash (_filePath: string) : string option =
    // Placeholder — will be implemented in Task 2
    None

/// Computes ProjectOptionsHash from FSharp.Compiler.CodeAnalysis.FSharpProjectOptions
let getProjectOptionsHash (_options: FSharpProjectOptions) : string =
    // Placeholder — will be implemented in Task 2
    ""
