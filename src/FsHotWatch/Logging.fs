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

/// Where one session's log lines go, and how verbose that session is.
///
/// A per-worktree daemon logs to stderr, which its launcher redirects to its
/// `daemon.log`. Sessions that share a repository host each install a sink instead, as
/// an `AsyncLocal` flowing with their work, so each session's lines land in its own
/// worktree's log. Work that carries no session's context logs to stderr: the host log.
[<NoComparison; NoEquality>]
type LogSink =
    { Write: string -> unit
      Level: LogLevel }

let private currentSink = System.Threading.AsyncLocal<LogSink option>()

/// Route this context's log lines to `sink` until the result is disposed, when the
/// previous sink (or stderr) takes over again.
let installSink (sink: LogSink) : System.IDisposable =
    let prior = currentSink.Value
    currentSink.Value <- Some sink

    { new System.IDisposable with
        member _.Dispose() = currentSink.Value <- prior }

/// A sink appending to `path` (created, with its directory, if missing). Lines are
/// written whole and flushed, so two sinks on one file interleave by line.
let fileSink (path: string) (level: LogLevel) : LogSink =
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName path)
    |> ignore

    let gate = obj ()

    { Write = fun line -> lock gate (fun () -> System.IO.File.AppendAllText(path, line + "\n"))
      Level = level }

/// Check if a given level is enabled.
let isEnabled level =
    match currentSink.Value with
    | Some sink -> level <= sink.Level
    | None -> level <= logLevel

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
        let line = $"  [%s{tag}] %s{ts} %s{msg}"

        match currentSink.Value with
        | Some sink -> sink.Write line
        | None -> eprintfn "%s" line

/// Log at Debug level (verbose only).
let debug tag msg = log LogLevel.Debug tag msg

/// Log at Info level.
let info tag msg = log LogLevel.Info tag msg

/// Log at Warning level.
let warn tag msg = log LogLevel.Warning tag msg

/// Log at Error level.
let error tag msg = log LogLevel.Error tag msg
