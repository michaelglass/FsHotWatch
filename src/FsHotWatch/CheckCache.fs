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
    sha256Hex $"%s{ContentHash.value key.FileHash}||%s{ContentHash.value key.ProjectOptionsHash}"

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

/// Content-addressed cache key provider. SHA-256 of the file bytes — two files
/// with identical content hash the same regardless of mtime, size-only metadata,
/// or VCS state. This was the original design intent for the FCS cache key
/// (matching what the plugin task cache already does at the merkle level).
///
/// `TimestampCacheKeyProvider` is preserved as a name for backward compatibility;
/// the implementation now reads and hashes file content.
type TimestampCacheKeyProvider() =
    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string =
            let normalizedPath = Path.GetFullPath(filePath)

            try
                let bytes = File.ReadAllBytes(normalizedPath)
                let hash = System.Security.Cryptography.SHA256.HashData(bytes)
                System.Convert.ToHexString(hash).ToLowerInvariant()
            with ex ->
                Logging.debug "cache" $"Could not read %s{normalizedPath}: %s{ex.Message}"
                sha256Hex $"unreadable:%s{normalizedPath}"

/// Computes ProjectOptionsHash from FSharpProjectOptions
let getProjectOptionsHash (options: FSharpProjectOptions) : string =
    let parts =
        [ string options.ProjectFileName
          String.concat "|" options.SourceFiles
          string (Array.length options.ReferencedProjects)
          String.concat "|" options.OtherOptions ]

    sha256Hex (String.concat "||" parts)

/// Compact tuple representation of an FCS diagnostic — what the hash actually
/// depends on. Extracted from fcsCheckSignature so the hashing/sorting logic
/// can be unit-tested without constructing a real FSharpCheckFileResults
/// (which has no public constructor and requires a live FCS instance).
type DiagnosticSignature =
    { StartLine: int
      StartColumn: int
      ErrorNumber: int
      Severity: string
      Message: string }

/// Hash a sequence of diagnostic signatures. Sorting by (line, column, error)
/// makes the hash stable across FCS internal ordering changes; encoding is
/// length-implicit-via-newline-separator (FCS diagnostic fields don't contain
/// newlines in normal usage).
let hashDiagnosticSignatures (signatures: DiagnosticSignature seq) : string =
    let parts =
        signatures
        |> Seq.sortBy (fun d -> d.StartLine, d.StartColumn, d.ErrorNumber)
        |> Seq.map (fun d -> $"%d{d.StartLine}:%d{d.StartColumn}:%d{d.ErrorNumber}:%s{d.Severity}:%s{d.Message}")
        |> String.concat "\n"

    sha256Hex parts

/// §1: signature of FCS check results, suitable as an oracle answer for plugin
/// cache keys. Two runs of the same file with identical FCS view (i.e., the
/// transitive cross-file state that affects this file's compilation produced
/// identical diagnostics) hash the same. When a cross-file change shifts FCS's
/// view of this file (new error introduced by an upstream symbol change),
/// the signature differs even though the file's source bytes are identical —
/// invalidating downstream plugin caches that include the signature.
///
/// Returns "parse-only" for ParseOnly results (FCS aborted before type
/// checking, so no useful signature is available).
let fcsCheckSignature (checkResults: FileCheckState) : string =
    match checkResults with
    | ParseOnly -> "parse-only"
    | FullCheck results when isNull (box results) ->
        // Test fixtures pass Unchecked.defaultof<FSharpCheckFileResults>; treat
        // the same as ParseOnly so callers get a stable signature.
        "full-check-null"
    | FullCheck results ->
        try
            results.Diagnostics
            |> Array.map (fun d ->
                { StartLine = d.StartLine
                  StartColumn = d.StartColumn
                  ErrorNumber = d.ErrorNumber
                  Severity = $"%A{d.Severity}"
                  Message = d.Message })
            |> hashDiagnosticSignatures
        with _ ->
            "full-check-error"

/// Compute a CacheKey for a file using the given provider
let makeCacheKey (provider: ICacheKeyProvider) (filePath: string) (options: FSharpProjectOptions) : CacheKey =
    { FileHash = ContentHash.create (provider.GetFileHash(filePath))
      ProjectOptionsHash = ContentHash.create (getProjectOptionsHash options) }
