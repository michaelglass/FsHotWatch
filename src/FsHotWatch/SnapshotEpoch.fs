/// A session's snapshot epoch and its manifest (AUTOMATION-681, ADR-045).
///
/// The epoch counts the snapshots a session's own accepted change cohorts produced, and the
/// manifest names what each path they changed holds at that epoch: repository-relative
/// path → content id. Each manifest is an immutable value with its own identity; a cohort
/// that changes it produces the next epoch's manifest and leaves the previous one intact.
///
/// Only the session's own cohorts advance it. Another session of the same repository host
/// owns its own ledger, so a save in one worktree never moves a sibling's epoch.
///
/// Epoch 0 is the tree the session's discovery loaded, which the manifest does not list:
/// it is an overlay of every path a cohort changed since then, with a removed path kept as
/// a tombstone. Seeding epoch 0 from a scan, and attaching a new session from the nearest
/// sibling's manifest, are later work (ADR-045).
module FsHotWatch.SnapshotEpoch

open System
open System.IO
open System.Threading

/// The snapshot a session is at. Monotonic per session, from `SnapshotEpoch.initial`.
[<Struct>]
type SnapshotEpoch =
    | SnapshotEpoch of int64

    member this.Value =
        let (SnapshotEpoch n) = this
        n

[<RequireQualifiedAccess>]
module SnapshotEpoch =
    let initial = SnapshotEpoch 0L
    let value (epoch: SnapshotEpoch) = epoch.Value
    let next (SnapshotEpoch n) = SnapshotEpoch(n + 1L)

/// What reading one path found.
[<RequireQualifiedAccess>]
type ContentRead =
    | Absent
    | Unreadable
    /// The content id (SHA-256, lowercase hex) and how many bytes were hashed for it.
    | Read of contentId: string * bytes: int64

/// What a manifest records for one path.
[<RequireQualifiedAccess>]
type ManifestEntry =
    | Content of contentId: string
    /// It existed and could not be read: never equal to any content.
    | Unreadable
    /// A cohort found it gone. Kept, because "not listed" means "as the base tree has it".
    | Removed

/// One epoch's manifest. `Id` is its identity: equal manifests have equal ids.
type Manifest =
    { Epoch: SnapshotEpoch
      Entries: Map<string, ManifestEntry>
      Id: string }

/// How much content a ledger read to produce its manifests.
type Hashed = { Files: int; Bytes: int64 }

[<RequireQualifiedAccess>]
module Hashed =
    let none = { Files = 0; Bytes = 0L }

/// A cohort's effect: the manifest it produced, and what it hashed to produce it.
type Advance = { Manifest: Manifest; Hashed: Hashed }

[<RequireQualifiedAccess>]
module Manifest =
    let private entryText (entry: ManifestEntry) =
        match entry with
        | ManifestEntry.Content id -> "content:" + id
        | ManifestEntry.Unreadable -> "unreadable"
        | ManifestEntry.Removed -> "removed"

    /// The identity of a set of entries. Each path is length-prefixed, so no path can
    /// spell another entry; a `Map` enumerates in ordinal key order, so the order in
    /// which a cohort named its paths never changes the id.
    let idOf (entries: Map<string, ManifestEntry>) : string =
        entries
        |> Seq.map (fun entry -> $"%d{entry.Key.Length}:%s{entry.Key}\t%s{entryText entry.Value}\n")
        |> String.concat ""
        |> ContentHash.ofText

    /// Epoch 0: the tree the session's discovery loaded, with nothing changed since.
    let empty: Manifest =
        { Epoch = SnapshotEpoch.initial
          Entries = Map.empty
          Id = idOf Map.empty }

    /// `path` relative to the first of `roots` it lies under, with forward slashes. A
    /// session's root has more than one spelling: as configured, and in the real form a
    /// native watcher reports (`/private/var/…` for a macOS `/var/…`).
    let relativePath (roots: string list) (path: string) : string =
        let trimmed =
            roots |> List.map (fun root -> root.TrimEnd(Path.DirectorySeparatorChar))

        let under (root: string) =
            path.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

        // A path under none of them is named against the configured root.
        let root =
            trimmed
            |> List.tryFind under
            |> Option.defaultWith (fun () -> List.head trimmed)

        Path.GetRelativePath(root, path).Replace('\\', '/')

    /// Hash `path`'s bytes, streamed, and count them.
    let readFile (path: string) : ContentRead =
        try
            use stream = File.OpenRead path

            let id =
                Convert.ToHexString(Security.Cryptography.SHA256.HashData stream).ToLowerInvariant()

            ContentRead.Read(id, stream.Length)
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> ContentRead.Absent
        | :? IOException
        | :? UnauthorizedAccessException -> ContentRead.Unreadable

    /// The manifest after a cohort that carried `paths`, and what reading them hashed.
    /// Each distinct path is read once, and no other path is read. The epoch advances
    /// only when an entry changes; an unchanged manifest is returned as it was.
    let advance
        (read: string -> ContentRead)
        (roots: string list)
        (paths: string list)
        (manifest: Manifest)
        : Manifest * Hashed =
        let entries, hashed =
            paths
            |> List.distinct
            |> List.fold
                (fun (entries: Map<string, ManifestEntry>, hashed: Hashed) path ->
                    let entry, hashed =
                        match read path with
                        | ContentRead.Read(id, bytes) ->
                            ManifestEntry.Content id,
                            { Files = hashed.Files + 1
                              Bytes = hashed.Bytes + bytes }
                        | ContentRead.Unreadable -> ManifestEntry.Unreadable, hashed
                        | ContentRead.Absent -> ManifestEntry.Removed, hashed

                    Map.add (relativePath roots path) entry entries, hashed)
                (manifest.Entries, Hashed.none)

        if entries = manifest.Entries then
            manifest, hashed
        else
            { Epoch = SnapshotEpoch.next manifest.Epoch
              Entries = entries
              Id = idOf entries },
            hashed

/// A session's current snapshot. Thread-safe; the change worker is its only writer.
type Ledger(roots: string list, read: string -> ContentRead) =
    let gate = Lock()
    let mutable current = Manifest.empty
    let mutable hashed = Hashed.none

    new(roots: string list) = Ledger(roots, Manifest.readFile)

    /// The current epoch's manifest.
    member _.Current: Manifest = Locking.locked gate (fun () -> current)

    /// Everything this ledger has hashed, summed over its cohorts.
    member _.Hashed: Hashed = Locking.locked gate (fun () -> hashed)

    /// Record an accepted cohort's paths. `None` when it changed no entry, so the epoch
    /// stayed where it was; what it hashed is counted either way.
    member _.Advance(paths: string list) : Advance option =
        if List.isEmpty paths then
            None
        else
            Locking.locked gate (fun () ->
                let next, cohort = Manifest.advance read roots paths current

                hashed <-
                    { Files = hashed.Files + cohort.Files
                      Bytes = hashed.Bytes + cohort.Bytes }

                if next.Epoch = current.Epoch then
                    None
                else
                    current <- next
                    Some { Manifest = next; Hashed = cohort })
