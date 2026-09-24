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
    /// One session of a repository host, watching through the host's shared stream and
    /// checking through its partition's shared checker.
    | Hosted of watcherFactory: WatcherFactory * checkers: CheckerFactory

/// A per-worktree daemon.
let standalone () : Hosting = Hosting.Standalone

/// A session of a repository host, watching through `sharedWatcher` and checking
/// through the checker `checkers` hands out for its configuration.
let hostedBy (sharedWatcher: WatcherFactory) (checkers: CheckerFactory) : Hosting =
    Hosting.Hosted(sharedWatcher, checkers)

/// Everything a hosted session does differently from a per-worktree daemon.
[<NoComparison; NoEquality>]
type HostingSeams =
    {
        /// Whose resources scan metrics measure.
        ResourceScope: ResourceScope
        /// Whether a scan may force a full collection before sampling. A forced GC in a
        /// host would pause every sibling session to measure one of them.
        MayForceGc: bool
        /// The watcher the daemon uses, given the one it would build for itself.
        Watcher: WatcherFactory -> WatcherFactory
        /// The checker the daemon uses, given how it would build its own.
        Checker: CheckerFactory -> CheckerFactory
    }

/// The seams of a hosting mode.
let seams (hosting: Hosting) : HostingSeams =
    match hosting with
    | Hosting.Standalone ->
        { ResourceScope = ResourceScope.Process
          MayForceGc = true
          Watcher = id
          Checker = id }
    | Hosting.Hosted(sharedWatcher, checkers) ->
        { ResourceScope = ResourceScope.Host
          MayForceGc = false
          Watcher = fun _ -> sharedWatcher
          Checker = fun _ -> checkers }
