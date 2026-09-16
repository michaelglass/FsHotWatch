/// The daemon's own account of why it refused to start.
///
/// A daemon is launched detached (`nohup … >> daemon.log`), so when it refuses to start
/// over an expected, user-correctable condition — a `ConfigError` such as analyzers that
/// have not been built — the CLI that launched it only observes "the pipe never came up".
/// Pointing the user at `daemon.log` makes them go and find the reason; this record
/// carries the reason itself back to the CLI, which prints it (content first, not a
/// pointer — the same principle applies to verdicts).
///
/// Lifecycle: the CLI clears it before every launch, the daemon writes it when it
/// refuses, and clears it once it has registered its plugins — so a record is only ever
/// read back about the launch that just failed.
module FsHotWatch.Cli.DaemonStartupFailure

open System
open System.IO
open FsHotWatch

/// `.fshw/startup-failure` — the refusal message of the most recent launch, if it refused.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "startup-failure")

/// Record why the daemon refused to start. Never throws: the daemon is exiting anyway,
/// and its stderr (daemon.log) already carries the same message.
let record (repoRoot: string) (message: string) : unit =
    try
        FsHwPaths.atomicWriteAllText (path repoRoot) message
    with ex ->
        Logging.debug "startup" $"could not record the startup failure: %s{ex.GetType().Name}: %s{ex.Message}"

/// Remove any recorded refusal. Never throws.
let clear (repoRoot: string) : unit =
    try
        File.Delete(path repoRoot)
    with ex ->
        Logging.debug "startup" $"could not clear the startup failure: %s{ex.GetType().Name}: %s{ex.Message}"

/// The recorded refusal, if the last launch left one. `None` when absent, blank or unreadable.
///
/// Reads first and classifies the failure, rather than probing `File.Exists` and then
/// reading: absence is the normal case and is not worth a log line, while an unreadable
/// record (a directory in its place, no permission) is logged and still reads as `None`,
/// so the CLI falls back to pointing at daemon.log instead of crashing.
let tryRead (repoRoot: string) : string option =
    try
        let content = File.ReadAllText(path repoRoot)

        if String.IsNullOrWhiteSpace content then
            None
        else
            Some(content.Trim())
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> None
    | ex ->
        Logging.debug "startup" $"could not read the startup failure: %s{ex.GetType().Name}: %s{ex.Message}"
        None

/// What the CLI prints when the daemon it launched never came up.
let describeLaunchFailure (recorded: string option) : string =
    match recorded with
    | Some message -> $"Failed to start daemon — the check could not run. The daemon refused to start:\n%s{message}"
    | None -> "Failed to start daemon — the check could not run. See logs/daemon.log."
