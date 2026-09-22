module FsHotWatch.Logging

/// Log severity levels, ordered from most to least severe.
type LogLevel =
    | Error = 0
    | Warning = 1
    | Info = 2
    | Debug = 3

/// Current log level. Messages at this level or more severe are printed.
let mutable logLevel = LogLevel.Info

/// When false, suppress noisy per-file status transitions.
/// Kept for backward compatibility with --verbose flag.
/// Prefer using logLevel/setLogLevel directly for new code.
let mutable verbose = false

/// Set the log level directly.
let setLogLevel level =
    logLevel <- level
    verbose <- level >= LogLevel.Debug

/// Check if a given level is enabled.
let isEnabled level = level <= logLevel

/// Log a message at the given level, with a component tag and timestamp.
let log (level: LogLevel) (tag: string) (msg: string) =
    if isEnabled level then
        // Dated, and explicitly UTC. `logs/daemon.log` is not rotated per run or per
        // day — an 11 MB file spanning several days is ordinary — so a time-only stamp
        // cannot place a line on a day, and "did this happen before or after the
        // version bump" is the first question asked of it. The `Z` is not decoration:
        // these stamps are UTC while the surrounding shell and `pmset` are local, and
        // reading one as the other has produced both a phantom wedge and an empty
        // `find -newermt` window that looked like a refutation.
        let ts = System.DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        eprintfn "  [%s] %s %s" tag ts msg

/// Log at Debug level (verbose only).
let debug tag msg = log LogLevel.Debug tag msg

/// Log at Info level.
let info tag msg = log LogLevel.Info tag msg

/// Log at Warning level.
let warn tag msg = log LogLevel.Warning tag msg

/// Log at Error level.
let error tag msg = log LogLevel.Error tag msg
