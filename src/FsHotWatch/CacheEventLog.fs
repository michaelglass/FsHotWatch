/// Always-on append-only log of cache misses. Each miss appends one line:
///
///     {ISO-8601 timestamp UTC}\t{plugin}\t{repoRoot}\t{triggerFile}
///
/// triggerFile is the file that triggered the lookup (empty for non-file events).
/// Hardcoded to a single shared file so all daemons (FsHotWatch's own,
/// thellma's, etc.) write to one place — point grep at it after a few
/// hours of use to identify which files drive cache misses per plugin.
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

/// Lock guarding the file append. Multiple plugins can call `recordMiss`
/// concurrently from different mailbox loops; serialise to avoid
/// interleaved partial lines within a single daemon.
let private writeLock = obj ()

/// Format a single miss line. Pure: separated from IO so tests can verify
/// the wire format without touching the filesystem.
let internal formatMiss (now: DateTime) (plugin: string) (repoRoot: string) (triggerFile: string) : string =
    let timestamp = now.ToString("O")
    sprintf "%s\t%s\t%s\t%s\n" timestamp plugin repoRoot triggerFile

/// Internal: append to a specific path. Used by `recordMiss` (with `LogPath`)
/// and by tests (with a temp file). Failures are swallowed so telemetry IO
/// can't disturb the daemon's hot path.
let internal appendTo (path: string) (line: string) =
    lock writeLock (fun () ->
        try
            File.AppendAllText(path, line)
        with _ ->
            ())

/// Append a cache miss to the shared log.
let recordMiss (plugin: string) (repoRoot: string) (triggerFile: string) =
    let line = formatMiss DateTime.UtcNow plugin repoRoot triggerFile
    appendTo LogPath line
