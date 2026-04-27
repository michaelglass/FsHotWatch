/// Always-on append-only log of every plugin cache lookup. Each hit/miss
/// appends one line:
///
///     {ISO-8601 timestamp UTC}\t{plugin}\t{hit|miss}\t{repoRoot}\t{triggerFile}
///
/// triggerFile is the file that triggered the lookup (empty for non-file events).
/// Hardcoded to a single shared file so all daemons (FsHotWatch's own,
/// thellma's, etc.) write to one place — point grep at it after a few
/// hours of use to compute per-plugin hit rate without trawling each
/// daemon's verbose log.
///
/// Concurrent writers across daemons rely on the OS appending each
/// `File.AppendAllText` call atomically. Within a single daemon, an
/// internal lock serialises calls so they don't interleave.
module FsHotWatch.CacheEventLog

open System
open System.IO

/// Shared event log path. Hardcoded to the FsHotWatch repo on this machine
/// so a single grep across the file gives a unified view of all daemons'
/// cache behaviour. Switch later to a config setting if needed.
[<Literal>]
let LogPath = "/Users/michaelglass/Developer/opensource/FsHotWatch/cache-events.log"

/// Lock guarding the file append. Multiple plugins can call `record`
/// concurrently from different mailbox loops; serialise to avoid
/// interleaved partial lines within a single daemon.
let private writeLock = obj ()

/// Format a single event line. Pure: separated from IO so tests can verify
/// the wire format without touching the filesystem.
let internal formatEvent
    (now: DateTime)
    (plugin: string)
    (hit: bool)
    (repoRoot: string)
    (triggerFile: string)
    : string =
    let outcome = if hit then "hit" else "miss"
    let timestamp = now.ToString("O")
    sprintf "%s\t%s\t%s\t%s\t%s\n" timestamp plugin outcome repoRoot triggerFile

/// Internal: append to a specific path. Used by `record` (with `LogPath`)
/// and by tests (with a temp file). Failures are swallowed so telemetry IO
/// can't disturb the daemon's hot path.
let internal appendTo (path: string) (line: string) =
    lock writeLock (fun () ->
        try
            File.AppendAllText(path, line)
        with _ ->
            ())

/// Append a single hit/miss event to the shared log.
let record (plugin: string) (hit: bool) (repoRoot: string) (triggerFile: string) =
    let line = formatEvent DateTime.UtcNow plugin hit repoRoot triggerFile
    appendTo LogPath line
