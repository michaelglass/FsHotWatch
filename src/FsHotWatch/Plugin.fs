module FsHotWatch.Plugin

/// A function that handles a named command with string arguments and returns a result.
type CommandHandler = string array -> Async<string>

/// What one preprocessor pass did — as evidence, not as a count.
///
/// `Modified` alone (the old return value) could not tell "every file was already clean"
/// from "no formatter ran at all": a pass that never consulted its tool also rewrote
/// nothing, and the daemon rendered both as `formatted 0 files` (AUTOMATION-447). So a
/// pass now says what it examined and what ran, and a pass that could not run at all is
/// an `Error` rather than an empty list.
type PreprocessResult =
    {
        /// Files whose bytes this pass rewrote. The daemon suppresses their watcher echo.
        Modified: string list
        /// Files the pass actually examined, after its own filters (extension, ignore
        /// files). `0` with a non-empty batch means the batch held nothing for this pass.
        Considered: int
        /// What ran, for the status line — a formatter's binary and version, pinned where.
        Evidence: string
    }

/// A preprocessor runs before events are dispatched to plugins.
/// Use for format-on-save: the preprocessor may rewrite files,
/// and the daemon suppresses watcher events for those writes.
type IFsHotWatchPreprocessor =
    abstract Name: string
    /// Process changed files before they're dispatched. `Ok` carries the files that
    /// were modified (so the daemon can suppress re-triggers) and the evidence of what
    /// ran; `Error reason` means the pass could not run at all — its tool is not pinned
    /// or not restored — and NO file was examined. The host reports that as a failure,
    /// never as "none rewritten".
    abstract Process: changedFiles: string list -> repoRoot: string -> Result<PreprocessResult, string>
    abstract Dispose: unit -> unit
