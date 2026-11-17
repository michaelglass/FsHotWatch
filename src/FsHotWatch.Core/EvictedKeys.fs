/// Tombstones of the keys an outcome store's GC evicted (AUTOMATION-1765), so a lookup that finds
/// nothing filed can tell a key GC evicted from one never filed.
///
/// Without them the two read the same: a GC logs counts per kind and no key names, and a lookup
/// that misses has nothing to compare against. On the server every key GC evicted at the byte cap
/// was a testprune-analysis key, and how many of those were looked up again could not be counted.
///
/// FILES: one per GC that evicted anything, under the store root,
/// `evicted/<UTC ticks, 19 digits>-<nonce>.tsv`, written once and never rewritten; each line is
/// `<key value>\t<kind>`. A file per GC means two daemons collecting one box-wide store at once
/// never write the same file. The tombstones live outside `records/`, so they are no key, change
/// no key, and are not counted against the store's byte budget: they have their own retention
/// (`Retention`), applied by every GC, by whole files.
module FsHotWatch.EvictedKeys

open System
open System.Collections.Generic
open System.Globalization
open System.IO

/// The tombstones' directory under the store root.
[<Literal>]
let DirectoryName = "evicted"

/// What every tombstone file's name ends with.
[<Literal>]
let Extension = ".tsv"

/// A key GC evicted: the kind of its newest record, and when.
type Tombstone =
    { Kind: string
      EvictedAt: DateTimeOffset }

/// Why nothing is filed under a key, as far as the store can tell.
[<RequireQualifiedAccess>]
type Absence =
    /// No tombstone names the key: it was never filed, or GC evicted it longer ago than tombstones
    /// are kept.
    | NoTombstone
    /// GC evicted it.
    | Evicted of Tombstone

/// How long tombstone files are kept, and how many bytes they may take together.
type Retention = { Ttl: TimeSpan; MaxBytes: int64 }

module Retention =
    /// 16 MiB: a line is about 90 bytes, so about 180,000 evicted keys. Measured on the server
    /// (2026-11-17): 10,748 keys evicted in 19 GCs over four days, across two stores.
    let defaultMaxBytes = 16L * 1024L * 1024L

    /// The store's own TTL and `defaultMaxBytes`.
    let ofStoreTtl (ttl: TimeSpan) : Retention =
        { Ttl = ttl
          MaxBytes = defaultMaxBytes }

/// The name of the tombstone file of a GC at `at`: it sorts by time, and `nonce` keeps two GCs at
/// one instant apart.
let fileNameOf (at: DateTimeOffset) (nonce: string) : string =
    at.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)
    + "-"
    + nonce
    + Extension

/// When the GC that wrote the tombstone file `name` ran; `None` for a name `fileNameOf` never gives.
let evictedAtOf (name: string) : DateTimeOffset option =
    if
        name.Length > 20
        && name.[19] = '-'
        && name.EndsWith(Extension, StringComparison.Ordinal)
    then
        match Int64.TryParse(name.Substring(0, 19), NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, ticks when ticks <= DateTimeOffset.MaxValue.UtcTicks -> Some(DateTimeOffset(ticks, TimeSpan.Zero))
        | _ -> None
    else
        None

/// The text of a tombstone file naming `evicted`, each a key value and the kind of its newest record.
let encode (evicted: (ActionKey.KeyValue * string) list) : string =
    evicted
    |> List.map (fun (key, kind) -> key.Text + "\t" + kind + "\n")
    |> String.concat ""

/// The tombstones a file written at `evictedAt` holds, by key value; a line that is not
/// `<key value>\t<kind>` is skipped.
let decode (evictedAt: DateTimeOffset) (text: string) : (ActionKey.KeyValue * Tombstone) list =
    [ for line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
          match line.Split('\t') with
          | [| key; kind |] when kind.Length > 0 ->
              match ActionKey.KeyValue.tryParse key with
              | Some key -> yield key, { Kind = kind; EvictedAt = evictedAt }
              | None -> ()
          | _ -> () ]

/// Every tombstone file under `root`, oldest first, with when its GC ran. A file whose name
/// `fileNameOf` never gives is not one, and is never read or deleted.
let private tombstoneFiles (root: string) : (string * DateTimeOffset) list =
    let paths =
        try
            Directory.GetFiles(Path.Combine(root, DirectoryName), "*" + Extension)
        with
        // No tombstones yet (DirectoryNotFoundException), or something else in their place: none
        // to read, and a lookup must not throw for them.
        | :? IOException
        | :? UnauthorizedAccessException -> [||]

    paths
    |> Array.choose (fun path -> evictedAtOf (Path.GetFileName path) |> Option.map (fun at -> path, at))
    |> Array.sortBy snd
    |> List.ofArray

/// Every tombstone under `root`, by key value text; a key evicted more than once is named by its
/// latest eviction. A file that cannot be read is skipped: a lookup costs nothing more for it.
let read (root: string) : IReadOnlyDictionary<string, Tombstone> =
    let tombstones = Dictionary<string, Tombstone>(StringComparer.Ordinal)

    for path, at in tombstoneFiles root do
        try
            for key, tombstone in decode at (File.ReadAllText path) do
                tombstones[key.Text] <- tombstone
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    tombstones

/// What `collect` deleted, by file name, and what it could not delete, with why.
type Collected =
    {
        /// Tombstone files older than the TTL, oldest first.
        Expired: string list
        /// Tombstone files deleted, oldest first, to bring the rest within the cap.
        OverCap: string list
        NotDeleted: (string * string) list
    }

/// Apply `retention` to the tombstones under `root` at `now`, by whole files: first every file
/// written before `now - Ttl`, then the oldest until the rest fit `MaxBytes`.
let collect (root: string) (now: DateTimeOffset) (retention: Retention) : Collected =
    let expired, kept =
        tombstoneFiles root |> List.partition (fun (_, at) -> now - at > retention.Ttl)

    let sized =
        kept
        |> List.choose (fun (path, _) ->
            try
                Some(path, FileInfo(path).Length)
            with :? IOException ->
                // Another daemon's GC deleted it since it was listed.
                None)

    // Oldest first: each file with the bytes the tombstones hold before it is deleted. Those
    // holding more than the cap go.
    let overCap =
        sized
        |> List.scan (fun (_, left) (path, size) -> Some(path, left), left - size) (None, List.sumBy snd sized)
        |> List.choose fst
        |> List.filter (fun (_, before) -> before > retention.MaxBytes)
        |> List.map fst

    let delete (path: string) =
        try
            File.Delete path
            Ok(Path.GetFileName path)
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex -> Error(Path.GetFileName path, ex.Message)

    let expiredResults = expired |> List.map (fst >> delete)
    let overCapResults = overCap |> List.map delete

    { Expired = expiredResults |> List.choose Result.toOption
      OverCap = overCapResults |> List.choose Result.toOption
      NotDeleted =
        expiredResults @ overCapResults
        |> List.choose (fun result ->
            match result with
            | Error failure -> Some failure
            | Ok _ -> None) }

/// The tombstones as read at one modification time of their directory.
[<NoComparison; NoEquality>]
type private Snapshot =
    { Stamp: DateTime
      Tombstones: IReadOnlyDictionary<string, Tombstone> }

/// The tombstones under `root`, read again only when their directory changed: a GC adds a file
/// and its retention deletes files, each of which moves the directory's modification time. A miss
/// pays one `stat` of the directory; only the first miss after a GC (every 6 hours at most)
/// reads the files.
type Reader(root: string) =
    let dir = Path.Combine(root, DirectoryName)
    let gate = obj ()
    // `None` until the first miss reads the tombstones.
    let mutable snapshot: Snapshot option = None

    /// Why nothing is filed under `key`.
    member _.AbsenceOf(key: ActionKey.KeyValue) : Absence =
        // A missing directory reads as one fixed instant, so its creation is a change too.
        let stamp = Directory.GetLastWriteTimeUtc dir

        let tombstones =
            lock gate (fun () ->
                match snapshot with
                | Some s when s.Stamp = stamp -> s.Tombstones
                | Some _
                | None ->
                    let tombstones = read root

                    snapshot <-
                        Some
                            { Stamp = stamp
                              Tombstones = tombstones }

                    tombstones)

        match tombstones.TryGetValue key.Text with
        | true, tombstone -> Absence.Evicted tombstone
        | false, _ -> Absence.NoTombstone

module Reader =
    let private shared =
        System.Collections.Concurrent.ConcurrentDictionary<string, Reader>(StringComparer.Ordinal)

    /// The one reader of the tombstones under `root` in this process. Code that opens a store per
    /// request (a verdict publication, the shadow lookup) shares it, so the tombstones are read once
    /// per GC per process, not once per store.
    let forRoot (root: string) : Reader = shared.GetOrAdd(root, Reader)
