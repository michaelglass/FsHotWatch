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
        let ts = System.DateTime.UtcNow.ToString("HH:mm:ss.fff")
        eprintfn "  [%s] %s %s" tag ts msg

/// Log at Debug level (verbose only).
let debug tag msg = log LogLevel.Debug tag msg

/// Log at Info level.
let info tag msg = log LogLevel.Info tag msg

/// Log at Warning level.
let warn tag msg = log LogLevel.Warning tag msg

/// Log at Error level.
let error tag msg = log LogLevel.Error tag msg
