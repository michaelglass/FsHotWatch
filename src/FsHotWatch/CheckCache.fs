module FsHotWatch.CheckCache

open System
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events
open FsHotWatch.JjHelper

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
let getFileHash (filePath: string) : string =
    let normalizedPath = System.IO.Path.GetFullPath(filePath)

    match JjHelper.currentCommitId () with
    | Some commitId ->
        // Use jj snapshot + file path as hash (content-addressed)
        let content = $"jj:%s{commitId}|%s{normalizedPath}"
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()
    | None ->
        // Fallback: use file metadata (size + mtime)
        try
            let info = System.IO.FileInfo(normalizedPath)
            let content = $"%s{normalizedPath}:%d{info.Length}:%d{info.LastWriteTimeUtc.Ticks}"
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
            BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()
        with _ ->
            // File doesn't exist or is unreadable — use path only
            let content = $"unreadable:%s{normalizedPath}"
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
            BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

/// Computes ProjectOptionsHash from FSharp.Compiler.CodeAnalysis.FSharpProjectOptions
let getProjectOptionsHash (options: FSharpProjectOptions) : string =
    let parts =
        [ string options.ProjectFileName
          string options.TargetFramework
          string (List.length options.SourceFiles)
          string (List.length options.ReferencedProjects)
          String.concat "|" options.CompilerFlags ]

    let content = String.concat "||" parts
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content))
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()
