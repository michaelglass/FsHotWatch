/// Identity of the analyzer assemblies for the per-file analyzer cache key.
///
/// The cache key's `analyzer-assemblies` slot must answer one question: "were these
/// verdicts produced by analyzers built from the same rules?" A SHA-256 over the DLL
/// bytes answers a different one. fsc writes the absolute path of the portable PDB
/// into the PE's CodeView debug entry, so two checkouts of identical source at two
/// paths produce two different DLLs — every fresh workspace of a repository that
/// builds its own house rules misses the shared cache, permanently.
///
/// The compiler's receipt is a better identity than its output. The portable PDB's
/// Document table records, for every source file fsc compiled, a checksum of the bytes
/// it read (SHA-256 by default). That table is the compiler's own statement of what
/// went into the assembly, and it is path-free once each document is named relative to
/// the repository. Together with the assembly's references and the project file that
/// drove the build, it identifies the rules regardless of where they were built —
/// which is exactly the property the shared store needs.
///
/// A DLL that carries no such receipt for this repository (no PDB, or no document
/// under the repository root — a NuGet package, a bundled dependency) keeps the byte
/// digest: its bytes are already stable across checkouts.
///
/// Trust is bounded by verification. The receipt names what the compiler READ; the
/// identity is only meaningful if that is still what the tree HOLDS. Every in-repo
/// document is re-hashed from disk and compared to the recorded checksum, and any
/// disagreement is a `Refusal`, never a quiet fallback: a stale DLL sitting beside
/// edited sources is the one case where the byte digest was honest and the receipt
/// would lie.
module internal FsHotWatch.Analyzers.AnalyzerIdentity

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Security.Cryptography

[<RequireQualifiedAccess>]
type AnalyzerAssemblyIdentity =
    /// Built from sources under the repository root: identified by the compiler's
    /// receipt (document checksums, assembly references, producer project).
    | FirstParty of semantic: string
    /// No receipt for this repository (a package, or nothing to relate it to the
    /// tree): identified by the SHA-256 of its bytes.
    | PackageBytes of digest: string

/// Why a first-party identity could not be established. Each case names the evidence
/// that was expected and not found, so the plugin can say precisely which assumption
/// failed rather than silently falling back to a weaker identity.
[<RequireQualifiedAccess>]
type Refusal =
    /// The DLL could not be read at all (absent, locked, permissions).
    | Unreadable of dll: string * reason: string
    /// The DLL's CodeView entry names a PDB under the repository root — this
    /// repository's compiler produced it — but no portable PDB is beside it or
    /// embedded in it.
    | MissingPdb of dll: string
    /// The sidecar PDB beside the DLL is not the one the DLL's CodeView entry names
    /// (a stale PDB from an earlier build).
    | PdbMismatch of dll: string * pdb: string
    /// An in-repo document's recorded checksum uses an algorithm this module cannot
    /// recompute, so the receipt cannot be verified against the tree.
    | UnverifiableChecksum of file: string * algorithm: Guid
    /// The receipt names an in-repo document that is not in the tree.
    | DocumentMissing of file: string
    /// The document's bytes on disk no longer match the checksum the compiler recorded.
    | DocumentDrift of file: string * recorded: string * actual: string
    /// No unique `*.fsproj` in any directory from the DLL's up to the repository root.
    | ProducerNotFound of dll: string
    /// The DLL is older than its project file. A refusal trigger only, never a proof
    /// of freshness: mtimes can be equal after a copy, and only the checksums decide.
    | OutputOlderThanProject of dll: string * project: string

/// Identity of a whole analyzer set (every DLL the loader inspects).
type AnalyzerSetIdentity =
    private
        { Semantic: string
          Materialization: string }

    /// The cache-key slot: stable across checkouts of the same rules.
    member this.SemanticKey = this.Semantic

    /// SHA-256 over the raw bytes of every DLL in path order: changes whenever any
    /// byte on disk changes, so a loaded analyzer set can be compared to the files it
    /// was loaded from.
    member this.MaterializationDigest = this.Materialization

// ---------------------------------------------------------------------------
// Hashing
// ---------------------------------------------------------------------------

let private sha256Lower (bytes: byte array) : string =
    SHA256.HashData bytes |> Convert.ToHexStringLower

let private sha256OfText (text: string) : string =
    sha256Lower (Text.Encoding.UTF8.GetBytes text)

/// Hash-algorithm GUIDs from the portable PDB specification (Document table,
/// `HashAlgorithm` column).
let private sha1Algorithm = Guid "ff1816ec-aa5e-4d10-87f7-6f4963833460"
let private sha256Algorithm = Guid "8829d00f-11b8-4213-878b-770e8597ac16"

let private recomputeChecksum (algorithm: Guid) (bytes: byte array) : byte array option =
    if algorithm = sha256Algorithm then
        Some(SHA256.HashData bytes)
    elif algorithm = sha1Algorithm then
        Some(SHA1.HashData bytes)
    else
        None

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

/// A path as the segments between separators. Recorded document paths use the
/// separator of the machine that ran fsc, so both are honoured; the on-disk path is
/// rebuilt with this machine's.
let private segmentsOf (path: string) : string list =
    path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

let private rootSegments (root: string) : string list = segmentsOf (Path.GetFullPath root)

let private startsWith (prefix: string list) (segments: string list) : bool =
    List.length segments > List.length prefix
    && List.take (List.length prefix) segments = prefix

/// Index of the LAST occurrence of `run` as a contiguous segment run, if any.
let private lastIndexOfRun (run: string list) (segments: string list) : int option =
    let n = List.length run

    [ 0 .. List.length segments - n ]
    |> List.tryFindBack (fun i -> List.skip i segments |> List.take n = run)

let private joinRepoRelative (segments: string list) : string = String.concat "/" segments

let private onDisk (repoRoot: string) (key: string) : string =
    Path.Combine(repoRoot, key.Replace('/', Path.DirectorySeparatorChar))

// ---------------------------------------------------------------------------
// The compiler's receipt
// ---------------------------------------------------------------------------

type private RecordedDocument =
    { Path: string
      Algorithm: Guid
      Checksum: byte array }

/// What the PE and its portable PDB say about the build, before any relation to
/// the tree is established.
type private Receipt =
    { Documents: RecordedDocument list
      AssemblyRefs: (string * string) list }

let private readDocuments (pdb: MetadataReader) : RecordedDocument list =
    pdb.Documents
    |> Seq.map (fun handle ->
        let document = pdb.GetDocument handle

        { Path = pdb.GetString document.Name
          Algorithm = pdb.GetGuid document.HashAlgorithm
          Checksum = pdb.GetBlobBytes document.Hash })
    |> Seq.toList

let private readAssemblyRefs (assembly: MetadataReader) : (string * string) list =
    assembly.AssemblyReferences
    |> Seq.map (fun handle ->
        let reference = assembly.GetAssemblyReference handle
        assembly.GetString reference.Name, string reference.Version)
    |> Seq.sort
    |> Seq.toList

let private pdbIdOf (pdb: MetadataReader) : Guid =
    Guid(pdb.DebugMetadataHeader.Id.AsSpan().Slice(0, 16))

type private PdbLookup =
    | Found of MetadataReaderProvider
    | Absent
    | Stale of pdb: string

/// The portable PDB for `pe`: the sidecar beside the DLL when it is the one the
/// CodeView entry names, else an embedded one. The sidecar is located by the DLL's
/// own name rather than the CodeView path: that path is where the PDB was WRITTEN,
/// which is the very salt this module exists to see past.
let private locatePdb (dll: string) (pe: PEReader) (codeView: CodeViewDebugDirectoryData option) : PdbLookup =
    let sidecar = Path.ChangeExtension(dll, ".pdb")

    let sidecarLookup =
        if File.Exists sidecar then
            let provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead sidecar)

            match codeView with
            | Some cv when pdbIdOf (provider.GetMetadataReader()) <> cv.Guid ->
                provider.Dispose()
                Some(Stale sidecar)
            | _ -> Some(Found provider)
        else
            None

    match sidecarLookup with
    | Some lookup -> lookup
    | None ->
        pe.ReadDebugDirectory()
        |> Seq.tryFind (fun entry -> entry.Type = DebugDirectoryEntryType.EmbeddedPortablePdb)
        |> Option.map (fun entry -> Found(pe.ReadEmbeddedPortablePdbDebugDirectoryData entry))
        |> Option.defaultValue Absent

/// Everything the module reads from the PE itself, before any relation to the tree.
type private Parsed =
    /// A portable PDB was found: the receipt is available.
    | Receipt of Receipt
    /// No portable PDB beside or inside the DLL; the CodeView path says where fsc
    /// wrote one, if it wrote one at all.
    | NoPdb of codeViewPdbPath: string option
    /// The sidecar PDB is not the one the CodeView entry names.
    | StalePdb of pdb: string
    /// Bytes that are not a managed assembly (the loader would skip them too).
    | NotAnAssembly

let private parse (dll: string) (bytes: byte array) : Parsed =
    try
        use pe = new PEReader(ImmutableArray.Create<byte>(bytes: byte array))

        if not pe.HasMetadata then
            NotAnAssembly
        else
            let assembly = pe.GetMetadataReader()

            let codeView =
                pe.ReadDebugDirectory()
                |> Seq.tryFind (fun entry -> entry.Type = DebugDirectoryEntryType.CodeView)
                |> Option.map pe.ReadCodeViewDebugDirectoryData

            match locatePdb dll pe codeView with
            | Found provider ->
                use provider = provider
                let pdb = provider.GetMetadataReader()

                Receipt
                    { Documents = readDocuments pdb
                      AssemblyRefs = readAssemblyRefs assembly }
            | Absent -> NoPdb(codeView |> Option.map (fun cv -> cv.Path))
            | Stale pdb -> StalePdb pdb
    with :? BadImageFormatException ->
        NotAnAssembly

// ---------------------------------------------------------------------------
// Relating the receipt to the tree
// ---------------------------------------------------------------------------

/// The project that produced the DLL: the nearest directory, from the DLL's own up
/// to and including the repository root, holding exactly one `*.fsproj`. A DLL
/// outside the repository root has no producer here.
let private findProducer (repoRoot: string) (dll: string) : string option =
    let root = Path.GetFullPath repoRoot
    let rootSegs = segmentsOf root

    let rec up (directory: string) =
        let projects = Directory.GetFiles(directory, "*.fsproj")

        if projects.Length = 1 then
            Some projects[0]
        elif projects.Length > 1 then
            None
        elif segmentsOf directory = rootSegs then
            None
        else
            match Path.GetDirectoryName directory with
            | null -> None
            | parent -> up parent

    let directory = Path.GetDirectoryName(Path.GetFullPath dll)

    if startsWith rootSegs (segmentsOf directory) then
        up directory
    else
        None

/// A document related to the tree: its repository-relative key and its recorded
/// checksum.
type private InRepoDocument =
    { Key: string
      Recorded: RecordedDocument }

/// The recorded root — the directory the compiler's absolute document paths are
/// relative to. Recorded paths are absolute on the machine that ran fsc, so this
/// is recovered rather than read:
///
///  * When any document lies under the current repository root, the build happened
///    in place and the recorded root IS the repository root.
///  * Otherwise the receipt came from another checkout. The producer project's
///    repository-relative directory (e.g. `analyzers/FsHotWatch.Rules`) is a run of
///    segments every document under it also carries; the recorded root is what
///    precedes the last such run in the first (sorted) document that has one.
///
/// Documents under the recorded root are in-repo, keyed by their path relative to it
/// with `/` separators; every other document (SDK sources, generated files outside
/// the tree) is excluded from the identity and does not disqualify the DLL.
let private relateDocuments (repoRoot: string) (producerDir: string option) (documents: RecordedDocument list) =
    let rootSegs = rootSegments repoRoot
    let documents = documents |> List.sortBy (fun d -> d.Path)

    let recordedRoot =
        if documents |> List.exists (fun d -> startsWith rootSegs (segmentsOf d.Path)) then
            Some rootSegs
        else
            producerDir
            |> Option.bind (fun dir ->
                let run = List.skip (List.length rootSegs) (rootSegments dir)

                documents
                |> List.tryPick (fun d ->
                    let segs = segmentsOf d.Path
                    lastIndexOfRun run segs |> Option.map (fun i -> List.take i segs)))

    recordedRoot
    |> Option.map (fun recorded ->
        documents
        |> List.choose (fun d ->
            let segs = segmentsOf d.Path

            if startsWith recorded segs then
                Some
                    { Key = joinRepoRelative (List.skip (List.length recorded) segs)
                      Recorded = d }
            else
                None))
    |> Option.defaultValue []

/// The receipt, verified: the bytes now in the tree are the bytes the compiler read.
let private verifyDocument (repoRoot: string) (document: InRepoDocument) : Result<string * string, Refusal> =
    let file = onDisk repoRoot document.Key

    if not (File.Exists file) then
        Error(Refusal.DocumentMissing file)
    else
        match recomputeChecksum document.Recorded.Algorithm (File.ReadAllBytes file) with
        | None -> Error(Refusal.UnverifiableChecksum(file, document.Recorded.Algorithm))
        | Some actual ->
            let recordedHex = Convert.ToHexStringLower document.Recorded.Checksum
            let actualHex = Convert.ToHexStringLower actual

            if recordedHex = actualHex then
                Ok(document.Key, actualHex)
            else
                Error(Refusal.DocumentDrift(file, recordedHex, actualHex))

let private collect (results: Result<'a, 'e> list) : Result<'a list, 'e> =
    (Ok [], results)
    ||> List.fold (fun acc r ->
        match acc, r with
        | Ok xs, Ok x -> Ok(xs @ [ x ])
        | Error e, _ -> Error e
        | Ok _, Error e -> Error e)

let private firstPartySemantic
    (documents: (string * string) list)
    (assemblyRefs: (string * string) list)
    (projectDigest: string)
    : string =
    [ yield "analyzer-identity-v1"
      for key, checksum in documents do
          yield $"doc %s{key} %s{checksum}"
      for name, version in assemblyRefs do
          yield $"ref %s{name} %s{version}"
      yield $"project %s{projectDigest}" ]
    |> String.concat "\n"
    |> sha256OfText

let private firstPartyOf
    (repoRoot: string)
    (dll: string)
    (receipt: Receipt)
    : Result<AnalyzerAssemblyIdentity, Refusal> option =
    let producer = findProducer repoRoot dll

    let inRepo =
        relateDocuments repoRoot (producer |> Option.map Path.GetDirectoryName) receipt.Documents

    if List.isEmpty inRepo then
        None
    else
        match producer with
        | None -> Some(Error(Refusal.ProducerNotFound dll))
        | Some project when File.GetLastWriteTimeUtc dll < File.GetLastWriteTimeUtc project ->
            Some(Error(Refusal.OutputOlderThanProject(dll, project)))
        | Some project ->
            inRepo
            |> List.map (verifyDocument repoRoot)
            |> collect
            |> Result.map (fun documents ->
                let projectDigest = sha256Lower (File.ReadAllBytes project)
                AnalyzerAssemblyIdentity.FirstParty(firstPartySemantic documents receipt.AssemblyRefs projectDigest))
            |> Some

/// Whether the DLL's CodeView entry says this repository's compiler wrote its PDB.
let private compiledHere (repoRoot: string) (pdbPath: string option) : bool =
    pdbPath
    |> Option.exists (fun path -> startsWith (rootSegments repoRoot) (segmentsOf path))

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

let private readBytes (dll: string) : Result<byte array, Refusal> =
    try
        Ok(File.ReadAllBytes dll)
    with ex ->
        Error(Refusal.Unreadable(dll, ex.Message))

let private identityOfBytes
    (repoRoot: string option)
    (dll: string)
    (bytes: byte array)
    : Result<AnalyzerAssemblyIdentity, Refusal> =
    let packageBytes = AnalyzerAssemblyIdentity.PackageBytes(sha256Lower bytes)

    match repoRoot with
    | None -> Ok packageBytes
    | Some root ->
        match parse dll bytes with
        | NotAnAssembly -> Ok packageBytes
        | StalePdb pdb -> Error(Refusal.PdbMismatch(dll, pdb))
        | Receipt receipt -> firstPartyOf root dll receipt |> Option.defaultValue (Ok packageBytes)
        | NoPdb codeViewPdbPath ->
            // Only a DLL whose CodeView entry places its PDB under this repository is
            // a first-party build that lost its receipt; a package DLL names a PDB
            // path on the machine that packed it.
            if compiledHere root codeViewPdbPath then
                Error(Refusal.MissingPdb dll)
            else
                Ok packageBytes

/// Identity of one analyzer DLL. With no repository root nothing can be related to
/// the tree, so every DLL is `PackageBytes`.
let identityOf (repoRoot: string option) (dll: string) : Result<AnalyzerAssemblyIdentity, Refusal> =
    readBytes dll |> Result.bind (identityOfBytes repoRoot dll)

/// Identity of the analyzer set. Every DLL is examined and every refusal is
/// reported, so one run names everything that stands between the set and a
/// checkout-independent identity.
let ofPaths (repoRoot: string option) (dllPaths: string list) : Result<AnalyzerSetIdentity, Refusal list> =
    let perDll =
        dllPaths
        |> List.map (fun dll ->
            readBytes dll
            |> Result.bind (fun bytes -> identityOfBytes repoRoot dll bytes |> Result.map (fun id -> dll, bytes, id)))

    let refusals =
        perDll
        |> List.choose (function
            | Error e -> Some e
            | Ok _ -> None)

    if not (List.isEmpty refusals) then
        Error refusals
    else
        let identified =
            perDll
            |> List.choose (function
                | Ok x -> Some x
                | Error _ -> None)

        let semantic =
            identified
            |> List.map (fun (dll, _, id) ->
                match id with
                | AnalyzerAssemblyIdentity.FirstParty s -> $"%s{Path.GetFileName dll} first-party %s{s}"
                | AnalyzerAssemblyIdentity.PackageBytes d -> $"%s{Path.GetFileName dll} package %s{d}")
            |> List.sort
            |> String.concat "\n"
            |> sha256OfText

        use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256

        for _, bytes, _ in identified do
            hash.AppendData bytes

        Ok
            { Semantic = semantic
              Materialization = Convert.ToHexStringLower(hash.GetHashAndReset()) }
