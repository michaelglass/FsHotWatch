/// Reclaims shared task-cache namespace directories nothing uses any more.
///
/// The shared task cache keeps one directory per repository under
/// `<cache home>/cache/tasks/<namespace>`. Nothing removed one once its repository went
/// away, and a harness that created a namespace per run left 40 empty ones behind
/// (AUTOMATION-1424), on the way back to the file system's per-directory link limit.
module FsHotWatch.NamespaceReclaim

open System
open System.IO

/// The marker a namespace's owner touches each time it opens the store. A store that is
/// only read changes no entry, so its directory's own time cannot say it is in use.
[<Literal>]
let LastUsedMarker = ".last-used"

/// An empty namespace younger than this may be one a daemon is creating right now.
let EmptyGrace = TimeSpan.FromDays 1.0

/// A namespace with entries that no owner has opened for this long is reclaimed. It is a
/// cache: a repository that comes back rebuilds it.
let MaxIdle = TimeSpan.FromDays 30.0

/// At most this many namespaces are removed in one pass, so a pass is bounded however
/// many have piled up; the next pass takes the rest.
let ReclaimBound = 64

/// Mark `namespaceDir` as used now, creating it if absent. A failure is logged: not being
/// able to stamp the marker only makes the namespace look older than it is.
let markUsed (namespaceDir: string) : unit =
    try
        Directory.CreateDirectory namespaceDir |> ignore
        let marker = Path.Combine(namespaceDir, LastUsedMarker)

        if File.Exists marker then
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow)
        else
            File.WriteAllText(marker, "")
    with
    | :? IOException
    | :? UnauthorizedAccessException as failure ->
        Logging.warn "cache" $"could not mark %s{namespaceDir} as used: %s{failure.Message}"

/// When `namespaceDir`'s owner last used it: its last-used marker, or a change to its
/// entries, whichever is later.
let private lastUsed (namespaceDir: string) =
    let marker = Path.Combine(namespaceDir, LastUsedMarker)
    let changed = Directory.GetLastWriteTimeUtc namespaceDir

    if File.Exists marker then
        max changed (File.GetLastWriteTimeUtc marker)
    else
        changed

/// Whether nothing uses `namespaceDir` as of `now`.
let private unused (now: DateTime) (namespaceDir: string) =
    let hasEntries =
        Directory.EnumerateFileSystemEntries namespaceDir
        |> Seq.exists (fun entry -> Path.GetFileName entry <> LastUsedMarker)

    let idle = now - lastUsed namespaceDir
    if hasEntries then idle > MaxIdle else idle > EmptyGrace

/// Remove, from the namespaces in `tasksDir` other than `keep`, the ones nothing uses:
/// an empty one older than `EmptyGrace`, and one with entries whose owner last opened it
/// more than `MaxIdle` before `now`. Returns the names removed, at most `ReclaimBound`.
/// A namespace that cannot be read or removed is logged and left for a later pass.
let reclaimIn (tasksDir: string) (keep: string) (now: DateTime) : string list =
    let removable (namespaceDir: string) =
        try
            Path.GetFileName namespaceDir <> keep && unused now namespaceDir
        with
        | :? IOException
        | :? UnauthorizedAccessException as failure ->
            Logging.warn "cache" $"could not judge %s{namespaceDir}: %s{failure.Message}"
            false

    let remove (namespaceDir: string) =
        try
            Directory.Delete(namespaceDir, true)
            Some(Path.GetFileName namespaceDir)
        with
        | :? IOException
        | :? UnauthorizedAccessException as failure ->
            Logging.warn "cache" $"could not reclaim %s{namespaceDir}: %s{failure.Message}"
            None

    if Directory.Exists tasksDir then
        Directory.GetDirectories tasksDir
        |> Array.sort
        |> Seq.filter removable
        |> Seq.truncate ReclaimBound
        |> Seq.choose remove
        |> List.ofSeq
    else
        []
