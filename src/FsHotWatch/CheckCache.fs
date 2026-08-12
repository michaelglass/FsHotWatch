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

/// Pluggable strategy for computing file hashes (cache keys).
/// Returns None when the file cannot be read — callers must treat this as a
/// cache miss (no key produced, no cache write) so a transient lock that
/// resolves on retry produces a real read instead of a poisoned cache entry.
type ICacheKeyProvider =
    /// Compute a content hash for a file. Returns None on read failure
    /// (cache miss + retry).
    abstract member GetFileHash: filePath: string -> string option

/// Content-addressed cache key provider. SHA-256 of the file bytes — two files
/// with identical content hash the same regardless of mtime, size-only metadata,
/// or VCS state (matching what the plugin task cache does at the merkle level).
/// Despite the "Timestamp" in the name (kept for backward compatibility), the
/// implementation reads and hashes file CONTENT.
type TimestampCacheKeyProvider() =
    interface ICacheKeyProvider with
        member _.GetFileHash(filePath: string) : string option =
            let normalizedPath = Path.GetFullPath(filePath)

            try
                let bytes = File.ReadAllBytes(normalizedPath)
                let hash = System.Security.Cryptography.SHA256.HashData(bytes)
                Some(System.Convert.ToHexString(hash).ToLowerInvariant())
            with ex ->
                // None forces a cache miss (no key, no write), so a transient lock
                // (editor save, antivirus scan) produces a real hash on the next call
                // rather than an "unreadable" entry that pins stale data forever.
                Logging.debug "cache" $"Could not read %s{normalizedPath}: %s{ex.Message}"
                None

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

/// Hash a thunk that produces diagnostic signatures. If the thunk throws,
/// fold the exception's type and message into a synthesized hash payload so
/// distinct failure modes produce distinct cache keys (instead of all
/// collapsing to a single magic literal). The exception is logged at error so a
/// real bug isn't silently absorbed.
///
/// Extracted as a thunk-taking helper so the failure path is unit-testable
/// without constructing a real (or breakable) FSharpCheckFileResults.
let hashDiagnosticsOrFailure (extract: unit -> DiagnosticSignature seq) : string =
    try
        extract () |> hashDiagnosticSignatures
    with ex ->
        Logging.error "cache" $"diagnostic-hash failed (%s{ex.GetType().FullName}): %s{ex.ToString()}"

        // Hashing the synthesized payload (rather than returning a literal prefix)
        // keeps the failure output the same shape as the success path — a hex digest —
        // so callers need no separate branch.
        sha256Hex $"diagnostic-hash-failed:%s{ex.GetType().FullName}:%s{ex.Message}"

/// Signature of FCS check results, suitable as an oracle answer for plugin cache
/// keys. Two runs of the same file with an identical FCS view hash the same. When a
/// cross-file change shifts FCS's view of this file (a new error from an upstream
/// symbol change), the signature differs even though the file's source bytes are
/// identical — invalidating downstream plugin caches that include the signature.
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
        hashDiagnosticsOrFailure (fun () ->
            results.Diagnostics
            |> Array.map (fun d ->
                { StartLine = d.StartLine
                  StartColumn = d.StartColumn
                  ErrorNumber = d.ErrorNumber
                  Severity = $"%A{d.Severity}"
                  Message = d.Message })
            :> DiagnosticSignature seq)

/// Compute a CacheKey for a file using the given provider. Returns None when
/// the file cannot be read — callers must treat this as a cache miss.
let makeCacheKey (provider: ICacheKeyProvider) (filePath: string) (options: FSharpProjectOptions) : CacheKey option =
    provider.GetFileHash(filePath)
    |> Option.map (fun fileHash ->
        { FileHash = ContentHash.create fileHash
          ProjectOptionsHash = ContentHash.create (getProjectOptionsHash options) })
