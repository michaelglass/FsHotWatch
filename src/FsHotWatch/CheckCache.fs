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

/// A backend that caches only some projects, and whose useful size depends on the
/// working set. `CheckPipeline` asks `Admits` before computing a key (an excluded
/// project costs no fingerprint and no lookup) and reports the registered working set
/// — (project, source-file count) for every project — as projects register. A bound
/// below the ADMITTED working set gets ~0% hits on a repeated scan.
type IScopedCheckCache =
    abstract member Admits: projectPath: string -> bool
    abstract member ObserveWorkingSet: filesPerProject: (string * int) list -> unit

    /// A warning when the bound cannot hold the admitted working set (the thrash
    /// case), or None. Read once registration has settled — see
    /// `CheckPipeline.BeginGeneration` — so the count it names is the final one.
    abstract member FitWarning: string option

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

/// The placeholder an in-repo path collapses to inside a compiler-option string.
/// Not a path that can exist: `$` and `{}` are excluded from `CachePathIdentity`'s
/// portable grammar, so it cannot be confused with a real relative path.
[<Literal>]
let RepoRootPlaceholder = "${repo}"

/// Rewrite one compiler option so that everything inside `repoRoot` is named
/// relatively. Options are opaque strings (`-r:/abs/path.dll`, `--define:X`,
/// `--out:/abs/obj/x.dll`), so this is a prefix substitution rather than a path
/// parse — which is exactly right: it touches only the machine-local prefix and
/// leaves every flag, separator and out-of-repo reference untouched.
let internal relativizeOption (repoRoot: string option) (option: string) =
    match repoRoot with
    | None -> option
    | Some root ->
        let full =
            try
                Path.GetFullPath root
            with _ ->
                root

        let withSeparator =
            full.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        option
            .Replace(withSeparator, RepoRootPlaceholder + string Path.DirectorySeparatorChar)
            .Replace(full, RepoRootPlaceholder)

/// Computes ProjectOptionsHash from FSharpProjectOptions.
///
/// With a `repoRoot`, every path inside the repository is named RELATIVELY, so two
/// checkouts of the same repository at the same revision produce the SAME hash for
/// byte-identical compiler options. That is a prerequisite for a single shared daemon:
/// with absolute paths in here, one daemon serving N workspaces would hold N disjoint
/// compiler snapshots of identical code.
///
/// Paths OUTSIDE the repository (the NuGet cache, the SDK) stay absolute. They are
/// machine-local, and on one machine they are the same for every workspace — so they
/// separate two machines' entries, which is correct, and never two workspaces'.
let getProjectOptionsHashRelativeTo (repoRoot: string option) (options: FSharpProjectOptions) : string =
    let relativize = relativizeOption repoRoot

    // One spelling of "relativize these and join them", used by both string arrays.
    // Two copies of it would be two places for the encoding to drift apart, and a
    // drift there is a hash that silently stops matching across checkouts.
    let relativizeJoined (values: string array) =
        values |> Array.map relativize |> String.concat "|"

    let parts =
        [ relativize (string options.ProjectFileName)
          relativizeJoined options.SourceFiles
          string (Array.length options.ReferencedProjects)
          relativizeJoined options.OtherOptions ]

    sha256Hex (String.concat "||" parts)

/// Computes ProjectOptionsHash from FSharpProjectOptions, with no repository root to
/// relativize against — every path is hashed as written.
let getProjectOptionsHash (options: FSharpProjectOptions) : string =
    getProjectOptionsHashRelativeTo None options

/// Content hash of a file, re-read only when its (last-write time, length) stamp moves.
///
/// `upstreamFingerprint` hashes every file a check result depends on, for every
/// lookup; re-reading them each time would turn one scan into O(files²) reads. The
/// stamp check is one `stat` per file. A missing or unreadable file hashes to a fixed
/// marker, which is what FCS sees too (it reports the file as missing).
type FileContentHasher() =
    let memo =
        System.Collections.Concurrent.ConcurrentDictionary<string, struct (int64 * int64 * string)>()

    member _.Hash(path: string) : string =
        try
            let info = FileInfo(path)

            if not info.Exists then
                "missing"
            else
                let ticks = info.LastWriteTimeUtc.Ticks
                let length = info.Length

                match memo.TryGetValue path with
                | true, struct (t, l, hash) when t = ticks && l = length -> hash
                | _ ->
                    let hash =
                        System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

                    memo[path] <- struct (ticks, length, hash)
                    hash
        with ex ->
            Logging.debug "cache" $"Could not hash %s{path}: %s{ex.Message}"
            "unreadable"

let private isUnderRoot (repoRoot: string option) (path: string) =
    match repoRoot with
    | None -> true
    | Some root ->
        let full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
        path.StartsWith(full + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

/// The fingerprint of everything a file's check result depends on besides its own
/// bytes and its project's options, for EVERY file of one project at once:
///
/// - the files BEFORE it in its project's compile order (F# sees only those);
/// - every source file of every project it references, TRANSITIVELY — C referencing
///   B referencing A is type-checked against A's sources too;
/// - the bytes of each `-r:` assembly inside the repository that is not the output of
///   a referenced F# project (a C# project's output, a vendored dll). Assemblies
///   outside the repository — NuGet, the SDK — live at version-qualified paths, so the
///   path in the options hash already identifies them, and hashing hundreds of them
///   per lookup would spend the CPU the cache exists to save.
///
/// SOURCE content, not referenced projects' outputs: the checker (TransparentCompiler)
/// type-checks a file against its references' in-memory sources, so those are what
/// its diagnostics depend on.
///
/// The closure is hashed once, then a running hash walks the compile order, so a
/// whole project costs one pass. Returns each source file's fingerprint and the
/// fingerprint of the complete project (for a file not in `SourceFiles`).
///
/// Without this a cached result survives a change to a file it was type-checked
/// against, and the cache serves diagnostics FCS would no longer produce.
let upstreamFingerprints
    (hashFile: string -> string)
    (repoRoot: string option)
    (options: FSharpProjectOptions)
    : System.Collections.Generic.IReadOnlyDictionary<string, string> * string =
    let relativize = relativizeOption repoRoot
    let parts = System.Collections.Generic.List<string>()
    let visited = System.Collections.Generic.HashSet<string>()

    let hashAssemblyRefs (opts: FSharpProjectOptions) =
        let projectOutputs =
            opts.ReferencedProjects |> Array.map (fun r -> r.OutputFile) |> Set.ofArray

        for opt in opts.OtherOptions do
            if opt.StartsWith("-r:", StringComparison.Ordinal) then
                let path = opt.Substring 3

                if not (projectOutputs.Contains path) && isUnderRoot repoRoot path then
                    parts.Add $"ref:%s{relativize path}=%s{hashFile path}"

    let rec addProject (opts: FSharpProjectOptions) =
        if visited.Add opts.ProjectFileName then
            parts.Add $"project:%s{relativize opts.ProjectFileName}"

            for source in opts.SourceFiles do
                parts.Add $"%s{relativize source}=%s{hashFile source}"

            hashAssemblyRefs opts
            addReferences opts

    and addReferences (opts: FSharpProjectOptions) =
        for reference in opts.ReferencedProjects do
            match reference with
            | FSharpReferencedProject.FSharpReference(_, referenced) -> addProject referenced
            | other -> parts.Add $"other-ref:%s{relativize other.OutputFile}"

    visited.Add options.ProjectFileName |> ignore
    hashAssemblyRefs options
    addReferences options

    let table = System.Collections.Generic.Dictionary<string, string>()
    let mutable running = sha256Hex (String.concat "\n" parts)

    for source in options.SourceFiles do
        // First occurrence wins: a file listed twice is checked at its first position.
        if not (table.ContainsKey source) then
            table[source] <- running

        running <- sha256Hex $"%s{running}\n%s{relativize source}=%s{hashFile source}"

    table :> System.Collections.Generic.IReadOnlyDictionary<string, string>, running

/// `upstreamFingerprints` for one file.
let upstreamFingerprint
    (hashFile: string -> string)
    (repoRoot: string option)
    (filePath: string)
    (options: FSharpProjectOptions)
    : string =
    let table, whole = upstreamFingerprints hashFile repoRoot options

    match table.TryGetValue filePath with
    | true, fingerprint -> fingerprint
    | false, _ -> whole

/// `upstreamFingerprints`, computed once per project per GENERATION and reused.
///
/// Per lookup, the fingerprint stats every file in the project's closure: for a scan
/// that is O(files × closure). A generation is one scan or one change batch
/// (`CheckPipeline.BeginGeneration`), so a project's table is built once per scan
/// instead of once per file. Inside a generation the table is a SNAPSHOT: an edit
/// landing mid-scan is seen by the next generation, which the watcher's change batch
/// (and every `check`'s forced rescan) starts. Nothing computed against a moved
/// snapshot is stored — `CheckPipeline` re-derives the key with `Fresh` before it
/// writes.
///
/// Until the first `BeginGeneration` every lookup is fresh: a caller that never
/// declares generations gets correct keys, just not the memo.
type UpstreamFingerprints(repoRoot: string option) =
    let hasher = FileContentHasher()

    let memo =
        System.Collections.Concurrent.ConcurrentDictionary<
            string,
            Lazy<System.Collections.Generic.IReadOnlyDictionary<string, string> * string>
         >()

    let mutable generations = false
    let mutable computeTicks = 0L
    let mutable tablesBuilt = 0L

    /// Time only the computation, never a wait on another thread's table.
    let timed (compute: unit -> 'T) =
        let started = System.Diagnostics.Stopwatch.GetTimestamp()
        let result = compute ()

        System.Threading.Interlocked.Add(&computeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started)
        |> ignore

        result

    let lookup (table: System.Collections.Generic.IReadOnlyDictionary<string, string>, whole) filePath =
        match table.TryGetValue filePath with
        | true, fingerprint -> fingerprint
        | false, _ -> whole

    /// Drop every memoized table; the next lookup per project rebuilds it.
    member _.BeginGeneration() =
        memo.Clear()
        System.Threading.Volatile.Write(&generations, true)

    /// Fingerprint for `filePath`, from this generation's table for its project.
    /// `optionsHash` distinguishes two option sets for one project file.
    member this.For(filePath: string, options: FSharpProjectOptions, optionsHash: string) : string =
        if System.Threading.Volatile.Read(&generations) then
            let table =
                memo.GetOrAdd(
                    $"%s{options.ProjectFileName}|%s{optionsHash}",
                    fun _ ->
                        lazy
                            (System.Threading.Interlocked.Increment(&tablesBuilt) |> ignore
                             timed (fun () -> upstreamFingerprints hasher.Hash repoRoot options))
                )

            lookup table.Value filePath
        else
            this.Fresh(filePath, options)

    /// Fingerprint for `filePath` from disk now, bypassing the generation memo.
    member _.Fresh(filePath: string, options: FSharpProjectOptions) : string =
        timed (fun () -> upstreamFingerprint hasher.Hash repoRoot filePath options)

    /// Time spent computing fingerprints (tables and fresh lookups), excluding waits.
    member _.ComputeTime =
        System.TimeSpan(
            System.Threading.Volatile.Read(&computeTicks) * System.TimeSpan.TicksPerSecond
            / System.Diagnostics.Stopwatch.Frequency
        )

    /// Per-project tables built across all generations.
    member _.TablesBuilt = System.Threading.Volatile.Read(&tablesBuilt)

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

/// The check-result cache key: the file's own bytes, and — in the options half — its
/// project's options hash combined with its upstream fingerprint
/// (`upstreamFingerprints`). The one definition `CheckPipeline` looks results up by.
/// Returns None when the file cannot be read — callers must treat this as a miss.
let makeCacheKeyWith
    (provider: ICacheKeyProvider)
    (optionsHash: string)
    (upstream: string)
    (filePath: string)
    : CacheKey option =
    provider.GetFileHash(filePath)
    |> Option.map (fun fileHash ->
        { FileHash = ContentHash.create fileHash
          ProjectOptionsHash = ContentHash.create (sha256Hex $"%s{optionsHash}|%s{upstream}") })

/// `makeCacheKeyWith` with no repository root: every path hashed as written.
let makeCacheKey (provider: ICacheKeyProvider) (filePath: string) (options: FSharpProjectOptions) : CacheKey option =
    let upstream = upstreamFingerprint (FileContentHasher().Hash) None filePath options
    makeCacheKeyWith provider (getProjectOptionsHash options) upstream filePath
