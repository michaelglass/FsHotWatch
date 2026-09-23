/// The one place a daemon's hosting mode is decided.
///
/// A per-worktree daemon and a session of a repository host run one `Daemon`
/// lifecycle. Hosting SUBTRACTS a named set of things from it, and `HostingSeams` is
/// that set. The rest of the daemon asks the seams a question, and never branches on
/// the mode itself: a branch elsewhere is a second path, and the next feature added
/// beside it would land on one side and be silently skipped on the other
/// (`HostingSeamTests` refuses one).
module FsHotWatch.DaemonHosting

// TransparentCompiler.CacheSizes is marked experimental; it is the checker's configuration.
#nowarn "57"

open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events
open FsHotWatch.Watcher

/// Constructs the repository watcher for a `Watching` daemon. The arguments are
/// `FileWatcher.create`'s.
type WatcherFactory = string -> (FileChangeKind -> unit) -> bool option -> FilePattern list -> float -> FileWatcher

/// Builds, or hands out, the checker for a checker configuration.
type CheckerFactory = TransparentCompiler.CacheSizes -> FSharpChecker

/// Whose resources a scan's RSS and managed figures measure.
[<RequireQualifiedAccess>]
type ResourceScope =
    /// The process is this worktree's own daemon.
    | Process
    /// The process is a repository host serving several worktrees: the figures are
    /// the host's totals, never this worktree's share.
    | Host

module ResourceScope =
    /// The wire spelling, as `scan-metrics.jsonl` carries it.
    let render (scope: ResourceScope) : string =
        match scope with
        | ResourceScope.Process -> "process"
        | ResourceScope.Host -> "host"

    /// Read the wire spelling. Absent or unknown is `Process`: every record written
    /// before scopes existed came from a daemon that owned its process.
    let parse (text: string) : ResourceScope =
        if text = "host" then
            ResourceScope.Host
        else
            ResourceScope.Process

/// Whether a daemon owns its process or is one session of a repository host.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type Hosting =
    /// One daemon per worktree, owning its process, its watcher and the process-wide
    /// compiler caches.
    | Standalone
    /// One session of a repository host, watching through the host's shared stream,
    /// checking through its partition's shared checker, each project under the frame
    /// `frames` gives it.
    | Hosted of watcherFactory: WatcherFactory * checkers: CheckerFactory * frames: PathFrame.FrameChoice

/// A per-worktree daemon.
let standalone () : Hosting = Hosting.Standalone

/// A session of a repository host, watching through `sharedWatcher` and checking
/// through the checker `checkers` hands out for its configuration.
let hostedBy (sharedWatcher: WatcherFactory) (checkers: CheckerFactory) : Hosting =
    Hosting.Hosted(sharedWatcher, checkers, PathFrame.realPaths)

/// `hostedBy`, with each project checked under the frame `frames` gives it.
let hostedUnderFrames
    (sharedWatcher: WatcherFactory)
    (checkers: CheckerFactory)
    (frames: PathFrame.FrameChoice)
    : Hosting =
    Hosting.Hosted(sharedWatcher, checkers, frames)

/// Everything a hosted session does differently from a per-worktree daemon.
[<NoComparison; NoEquality>]
type HostingSeams =
    {
        /// Whose resources scan metrics measure.
        ResourceScope: ResourceScope
        /// Whether a scan may force a full collection before sampling. A forced GC in a
        /// host would pause every sibling session to measure one of them.
        MayForceGc: bool
        /// Whether a full rediscovery may clear FCS's process-wide caches (and run the
        /// full collection that comes with it). A session must not do either under its
        /// siblings; it invalidates only its own checker.
        ClearsProcessCaches: bool
        /// The watcher the daemon uses, given the one it would build for itself.
        Watcher: WatcherFactory -> WatcherFactory
        /// The checker the daemon uses, given how it would build its own.
        Checker: CheckerFactory -> CheckerFactory
        /// Whether a full rediscovery may drop everything the checker holds. A hosted
        /// session's checker is shared with its partition's other sessions, so it drops
        /// only its own projects.
        InvalidatesWholeChecker: bool
        /// The frame each project is checked under.
        Frames: PathFrame.FrameChoice
    }

/// The seams of a hosting mode.
let seams (hosting: Hosting) : HostingSeams =
    match hosting with
    | Hosting.Standalone ->
        { ResourceScope = ResourceScope.Process
          MayForceGc = true
          ClearsProcessCaches = true
          Watcher = id
          Checker = id
          InvalidatesWholeChecker = true
          Frames = PathFrame.realPaths }
    | Hosting.Hosted(sharedWatcher, checkers, frames) ->
        { ResourceScope = ResourceScope.Host
          MayForceGc = false
          ClearsProcessCaches = false
          Watcher = fun _ -> sharedWatcher
          Checker = fun _ -> checkers
          InvalidatesWholeChecker = false
          Frames = frames }
