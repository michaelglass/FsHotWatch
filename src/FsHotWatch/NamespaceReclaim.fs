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

/// Mark `namespaceDir` as used now.
let markUsed (_namespaceDir: string) : unit = ()

/// Remove, from the namespaces in `tasksDir` other than `keep`, the ones nothing uses:
/// an empty one older than `EmptyGrace`, and one with entries whose owner last opened it
/// more than `MaxIdle` before `now`. Returns the names removed, at most `ReclaimBound`.
let reclaimIn (_tasksDir: string) (_keep: string) (_now: DateTime) : string list = []
