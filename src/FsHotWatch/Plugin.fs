module FsHotWatch.Plugin

/// A function that handles a named command with string arguments and returns a result.
type CommandHandler = string array -> Async<string>

/// A preprocessor runs before events are dispatched to plugins.
/// Use for format-on-save: the preprocessor may rewrite files,
/// and the daemon suppresses watcher events for those writes.
type IFsHotWatchPreprocessor =
    abstract Name: string
    /// Process changed files before they're dispatched. Returns the list
    /// of files that were modified (so the daemon can suppress re-triggers).
    abstract Process: changedFiles: string list -> repoRoot: string -> string list
    abstract Dispose: unit -> unit
