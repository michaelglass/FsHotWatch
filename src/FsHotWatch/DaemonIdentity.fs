module FsHotWatch.DaemonIdentity

open System
open System.IO
open System.Security.Cryptography
open FsHotWatch.Logging

/// The daemon/CLI binary-identity handshake: the daemon writes its own binary
/// identity to `.fshw/daemon.identity` at startup, and every CLI compares that
/// record against its own binary before reusing a running daemon. The comparison
/// is UNILATERAL — an old-build daemon needs no cooperation to be found stale —
/// and a daemon that never recorded an identity reads as `NotRecorded`, which the
/// caller must treat exactly like a mismatch (an unknown daemon is never assumed
/// current).
///
/// The identity of an fshw binary: its assembly version AND a content hash of
/// the binary on disk. BOTH are load-bearing — a locally-repacked build can
/// share a version string and differ in content, so a version label alone can
/// lie; the content hash cannot.
type BinaryIdentity =
    { Version: string; ContentHash: string }

module BinaryIdentity =
    /// One-line disk format: `<version> <contentHash>`. The version is
    /// sanitized at construction so it can never contain the separator.
    let render (id: BinaryIdentity) : string = $"%s{id.Version} %s{id.ContentHash}"

    /// Strict parse of `render`'s format. Anything unrecognizable is `None` —
    /// an unreadable identity must read as "unknown", never as a match.
    /// The version is everything before the LAST space (defensive, should a
    /// historic record carry an unsanitized version).
    let parse (s: string) : BinaryIdentity option =
        match s with
        | null -> None
        | s ->
            let t = s.Trim()
            let i = t.LastIndexOf(' ')

            if i <= 0 || i = t.Length - 1 then
                None
            else
                Some
                    { Version = t.Substring(0, i).TrimEnd()
                      ContentHash = t.Substring(i + 1) }

/// Why a running daemon's identity is NOT this binary's. Kept as its own type
/// (rather than a bare mismatch bool) so callers can say precisely what they
/// found — the user is told what happened, never left to infer it.
[<RequireQualifiedAccess>]
type StaleReason =
    /// The daemon recorded an identity and it differs from this binary's.
    | DifferentBinary of recorded: BinaryIdentity
    /// The daemon never recorded an identity — a build that predates the
    /// handshake. Treated exactly like stale (fail closed).
    | NotRecorded

/// The CLI's comparison of the running daemon's recorded identity against its
/// own. Two-valued at the top so "not a match" cannot be ignored: any `Stale`
/// must restart the daemon, and the reason says which words to use.
[<RequireQualifiedAccess>]
type IdentityVerdict =
    | Match
    | Stale of StaleReason

/// Pure comparison. `None` (no recorded identity) is `Stale NotRecorded` — never
/// a match: an old daemon that never wrote an identity must be detected by the
/// NEW CLI alone.
///
/// Unhashable case: two binaries that both hash to `UnhashableContent` compare
/// EQUAL here and therefore MATCH — deliberately fail-OPEN, the opposite of the
/// verdict file (see `UnhashableContent`). The question here is "restart the
/// daemon?"; answering "yes, can't tell" would restart on every command and
/// thrash the FCS cache forever.
let compareIdentity (recorded: BinaryIdentity option) (current: BinaryIdentity) : IdentityVerdict =
    match recorded with
    | None -> IdentityVerdict.Stale StaleReason.NotRecorded
    | Some r when r = current -> IdentityVerdict.Match
    | Some r -> IdentityVerdict.Stale(StaleReason.DifferentBinary r)

/// Path of the identity record the daemon writes at startup.
let identityFilePath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "daemon.identity")

/// Sentinel hash used when the binary's bytes cannot be read/hashed. The hasher
/// and sentinel live in `FsHotWatch.ContentHash` — one value repo-wide. The VALUE
/// is shared but the CONCLUSION differs by question: here two unhashable binaries
/// MATCH (see `compareIdentity`), while the verdict file refuses an unhashable
/// producer outright.
[<Literal>]
let UnhashableContent = ContentHash.UnhashableContent

/// Resolve which on-disk file *is* "this binary" for identity purposes:
/// the entry assembly's dll when it resolves to a real file (local `dotnet
/// <dll>` builds AND the published dotnet tool both do), else the process
/// executable (native single-file). `None` when neither exists — the caller
/// falls back to `UnhashableContent`.
let internal identitySourceFile (processPath: string) (entryAssemblyLocation: string option) : string option =
    match entryAssemblyLocation with
    | Some dll when not (String.IsNullOrWhiteSpace dll) && File.Exists dll -> Some dll
    | _ ->
        if not (String.IsNullOrWhiteSpace processPath) && File.Exists processPath then
            Some processPath
        else
            None

/// SHA256 of the file's bytes (via the ONE hasher), shortened to 16 hex chars for the
/// one-line disk format. `None` when the file could not be read — the caller
/// substitutes the deterministic `UnhashableContent` sentinel.
let internal hashFile (path: string) : string option =
    let hash = ContentHash.ofFile path

    if ContentHash.isReadable hash then
        Some(hash.Substring(0, 16))
    else
        debug "identity" $"could not hash %s{path}"
        None

/// Pure assembly-independent core of `currentIdentity`, split out so the
/// version-sanitization and file-resolution rules are unit-testable.
let internal buildIdentity
    (version: string option)
    (processPath: string)
    (entryAssemblyLocation: string option)
    : BinaryIdentity =
    let sanitizedVersion =
        match version with
        | Some v when not (String.IsNullOrWhiteSpace v) -> v.Trim().Replace(' ', '_')
        | _ -> "unknown-version"

    let contentHash =
        match identitySourceFile processPath entryAssemblyLocation with
        | Some file -> hashFile file |> Option.defaultValue UnhashableContent
        | None -> UnhashableContent

    { Version = sanitizedVersion
      ContentHash = contentHash }

/// The version an assembly reports: its informational version (which carries the
/// full `1.2.3-alpha.4+sha` string), falling back to the plain assembly version.
/// `None` when it reports neither.
let internal versionOf (asm: System.Reflection.Assembly) : string option =
    asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
    |> Seq.tryHead
    |> Option.map (fun attr -> (attr :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
    |> Option.orElseWith (fun () -> asm.GetName().Version |> Option.ofObj |> Option.map string)

/// The identity of a given entry assembly + process path. Split out of
/// `currentIdentity` so the assembly-ABSENT case — a single-file or shim host
/// where `GetEntryAssembly()` returns null — is a value a test can pass rather
/// than a hosting condition a test would have to reproduce.
let internal identityOf (entry: System.Reflection.Assembly option) (processPath: string) : BinaryIdentity =
    let version = entry |> Option.bind versionOf
    let entryLocation = entry |> Option.map (fun a -> a.Location)
    buildIdentity version processPath entryLocation

/// Compute THIS process's binary identity: the entry assembly's informational
/// version plus a content hash of the binary on disk.
let currentIdentity () : BinaryIdentity =
    identityOf (System.Reflection.Assembly.GetEntryAssembly() |> Option.ofObj) Environment.ProcessPath

/// The daemon records its own identity at startup (atomic write, torn-write
/// safe). Written BEFORE the IPC pipe starts listening so a CLI that observes
/// a live pipe always finds the record.
let recordCurrent (repoRoot: string) : unit =
    FsHwPaths.atomicWriteAllText (identityFilePath repoRoot) (BinaryIdentity.render (currentIdentity ()))

/// Read the identity the running daemon recorded. Injectable file ops so the
/// parse/absence semantics are unit-testable. Any read/parse failure is `None`
/// (→ `Stale NotRecorded` at the comparison: fail closed, restart).
let readRecordedWith
    (fileExists: string -> bool)
    (readAllText: string -> string)
    (repoRoot: string)
    : BinaryIdentity option =
    let path = identityFilePath repoRoot

    if not (fileExists path) then
        None
    else
        try
            BinaryIdentity.parse (readAllText path)
        with ex ->
            debug "identity" $"could not read %s{path}: %s{ex.GetType().Name}: %s{ex.Message}"
            None

let readRecorded (repoRoot: string) : BinaryIdentity option =
    readRecordedWith File.Exists File.ReadAllText repoRoot

/// One-call verdict for the CLI: compare the running daemon's recorded
/// identity against this process's own. Only meaningful when a daemon is
/// actually listening — the caller checks that first.
let verdictFor (repoRoot: string) : IdentityVerdict =
    compareIdentity (readRecorded repoRoot) (currentIdentity ())
