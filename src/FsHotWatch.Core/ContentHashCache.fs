/// A memo of each file's content hash, kept per checkout and checked against the file's
/// identity before it is believed (E5, AUTOMATION-1696). It changes HOW a hash is obtained,
/// never WHAT it is: a hash it returns is `ContentHash.ofFile`'s for the same file, either
/// remembered or read now.
///
/// It is an ACCELERATOR, equal to a full read under the assumption git's index makes: a file
/// whose size, mtime, ctime, inode and device are all as they were has the bytes it had. It
/// is not a proof, so a full re-read (`TreeHash.Hashing.FullRehash`) stays available, and the
/// audit and the verdict before a land or a release use it.
///
/// The racy window. A filesystem records times to its own granularity, so a second write
/// soon after the first can leave every time as it was. An entry is trusted only when the
/// file's last change is more than `RacyWindowNs` older than the moment the entry's read
/// began (`VerifiedAtNs`, taken before the read): a write after that moment carries a later
/// ctime, and one before it was seen by the read. Two seconds covers one-second filesystems
/// and clock rounding. A file read too soon after its last change is read again on the next
/// look, and trusted once it has aged.
///
/// Two checks guard an entry, and each stands alone against the common case: the identity
/// must be the one remembered, and the racy rule (which reads ctime) rejects any change after
/// the read began. Both assume, as git's index does, a wall clock that does not step back: a
/// clock set back into the granule of the file's last change could let a same-size write with
/// a restored mtime match on every field. That is why the full re-read stays the land's mode.
module FsHotWatch.ContentHashCache

open System
open System.Globalization
open System.Text

/// A remembered hash: the identity the file had when it was read, the hash of what was
/// read, and when the read began.
type Entry =
    { Identity: FileIdentity.Identity
      Hash: string
      VerifiedAtNs: int64 }

/// Why a hash was read rather than remembered.
[<RequireQualifiedAccess>]
type UntrustedReason =
    /// Nothing remembered for the path.
    | NotCached
    /// The file's identity is not the one remembered.
    | Moved
    /// Remembered, but read too soon after the file's last change to rule out a second
    /// change within the filesystem's timestamp granularity.
    | Racy
    /// The kernel gave no identity for the file (a platform or filesystem without one).
    | NoIdentity
    /// The path holds a tab or a line break, which an entry line cannot.
    | UnstorablePath

/// Whether a hash came from the memo or from a read.
[<RequireQualifiedAccess>]
type Answer =
    | Trusted
    | Untrusted of UntrustedReason

/// What the cache asks of the world; tests substitute a model filesystem and clock.
[<NoEquality; NoComparison>]
type Sources =
    { Identify: string -> FileIdentity.Identity voption
      HashFile: string -> string
      NowNs: unit -> int64 }

module Sources =
    /// The kernel, the file's bytes and the wall clock.
    let real: Sources =
        { Identify = FileIdentity.read
          HashFile = ContentHash.ofFile
          NowNs = fun () -> (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100L }

/// How much older than the read a file's last change must be for its entry to be trusted.
[<Literal>]
let RacyWindowNs = 2_000_000_000L

/// No entries.
let empty: Map<string, Entry> = Map.empty

/// The memo for one look over a tree: the entries loaded from disk, and the entries this look
/// confirmed or wrote, which are all `Entries` holds (a path not asked about is dropped).
[<Sealed>]
type Cache(loaded: Map<string, Entry>, sources: Sources) =
    let mutable entries: Map<string, Entry> = Map.empty

    let unstorable (rel: string) =
        rel.IndexOfAny([| '\t'; '\n'; '\r' |]) >= 0

    member _.Entries: Map<string, Entry> = entries

    /// The content hash of `abs` (filed under the repo-relative `rel`), and whether it was
    /// remembered.
    member _.Hash(rel: string, abs: string) : string * Answer =
        if unstorable rel then
            sources.HashFile abs, Answer.Untrusted UntrustedReason.UnstorablePath
        else
            match sources.Identify abs with
            | ValueNone -> sources.HashFile abs, Answer.Untrusted UntrustedReason.NoIdentity
            | ValueSome identity ->
                let remembered = Map.tryFind rel loaded

                match remembered with
                | Some entry when
                    entry.Identity = identity
                    && identity.LatestChangeNs < entry.VerifiedAtNs - RacyWindowNs
                    ->
                    entries <- Map.add rel entry entries
                    entry.Hash, Answer.Trusted
                | _ ->
                    let reason =
                        match remembered with
                        | None -> UntrustedReason.NotCached
                        | Some entry when entry.Identity <> identity -> UntrustedReason.Moved
                        | Some _ -> UntrustedReason.Racy

                    // Read now, and remember it only when the read succeeded and the identity
                    // held still across it. An unreadable file is never remembered.
                    let verifiedAt = sources.NowNs()
                    let hash = sources.HashFile abs

                    if ContentHash.isReadable hash && sources.Identify abs = ValueSome identity then
                        entries <-
                            Map.add
                                rel
                                { Identity = identity
                                  Hash = hash
                                  VerifiedAtNs = verifiedAt }
                                entries

                    hash, Answer.Untrusted reason

// --- the file on disk ------------------------------------------------------------------

let private headerLine = "fshw-content-hash-cache 1"
let private trailerPrefix = "end "

let private text (n: int64) =
    n.ToString(CultureInfo.InvariantCulture)

let private utext (n: uint64) =
    n.ToString(CultureInfo.InvariantCulture)

/// The entries as the cache file's text: a version line, one tab-separated line per entry,
/// and a trailer naming how many, so a torn write reads as torn.
let serialize (entries: Map<string, Entry>) : string =
    let sb = StringBuilder()
    sb.Append(headerLine).Append('\n') |> ignore

    for KeyValue(rel, e) in entries do
        sb
            .Append(rel)
            .Append('\t')
            .Append(text e.Identity.Size)
            .Append('\t')
            .Append(text e.Identity.ModifiedNs)
            .Append('\t')
            .Append(text e.Identity.ChangedNs)
            .Append('\t')
            .Append(utext e.Identity.Inode)
            .Append('\t')
            .Append(utext e.Identity.Device)
            .Append('\t')
            .Append(text e.VerifiedAtNs)
            .Append('\t')
            .Append(e.Hash)
            .Append('\n')
        |> ignore

    sb.Append(trailerPrefix).Append(text (int64 entries.Count)).Append('\n').ToString()

let private parseLine (line: string) : (string * Entry) option =
    let int64Of (s: string) =
        match Int64.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None

    let uint64Of (s: string) =
        match UInt64.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None

    match line.Split '\t' with
    | [| rel; size; mtime; ctime; inode; device; verifiedAt; hash |] ->
        match int64Of size, int64Of mtime, int64Of ctime, uint64Of inode, uint64Of device, int64Of verifiedAt with
        | Some size, Some mtime, Some ctime, Some inode, Some device, Some verifiedAt when hash <> "" ->
            Some(
                rel,
                { Identity =
                    { Size = size
                      ModifiedNs = mtime
                      ChangedNs = ctime
                      Inode = inode
                      Device = device }
                  Hash = hash
                  VerifiedAtNs = verifiedAt }
            )
        | _ -> None
    | _ -> None

/// The entries in a cache file's text, or none at all when it is not whole: another version,
/// a torn write, a line that does not parse. A bad cache costs a full read, never an error.
let parse (contents: string) : Map<string, Entry> =
    match contents.Split '\n' |> List.ofArray |> List.rev with
    | "" :: trailer :: body when trailer.StartsWith(trailerPrefix, StringComparison.Ordinal) ->
        match List.rev body with
        | header :: lines when
            header = headerLine
            && text (int64 lines.Length) = trailer.Substring trailerPrefix.Length
            ->
            let parsed = lines |> List.map parseLine

            if parsed |> List.forall Option.isSome then
                parsed |> List.choose id |> Map.ofList
            else
                empty
        | _ -> empty
    | _ -> empty

/// The entries in the cache file at `path`, or none when it cannot be read.
let load (path: string) : Map<string, Entry> =
    try
        parse (IO.File.ReadAllText path)
    with
    | :? IO.IOException
    | :? UnauthorizedAccessException -> empty

/// Write `entries` to `path` (whole, by rename) when they differ from `loaded`. A cache that
/// cannot be written is skipped: the next look reads the files again.
let save (path: string) (loaded: Map<string, Entry>) (entries: Map<string, Entry>) : unit =
    if entries <> loaded then
        try
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName path) |> ignore
            FsHwPaths.atomicWriteAllText path (serialize entries)
        with
        | :? IO.IOException
        | :? UnauthorizedAccessException -> ()
