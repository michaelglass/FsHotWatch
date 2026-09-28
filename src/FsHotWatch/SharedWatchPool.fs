/// The repository host's file watching: one native stream per worktree session, and one
/// routing table per watch anchor.
///
/// Each session's stream covers its own root with that root's tooling directories and
/// nested checkouts excluded in the kernel (`Watcher.kernelExclusions`), exactly as a
/// standalone daemon's stream does. FSEvents takes at most 8 exclusion prefixes per
/// stream, so one stream over a whole repository could only exclude the primary
/// checkout's tooling directories; every nested session's `node_modules`, `.fshw`, `.jj`
/// and build output would still be queued in fseventsd for the host. A stream per
/// session keeps every session's exclusions.
///
/// The anchor — the repository's primary checkout when the worktree lies beneath it,
/// otherwise the worktree itself — groups the sessions whose roots may nest. An event
/// is delivered only when the anchor's routing table (`WatchRouter`) names the session
/// whose stream reported it: a checkout nested outside the excluded directories is
/// covered by its own session's stream and by its parent's, and reaches only its own.
///
/// Each session keeps the legacy watch rules after routing: F# inputs under its
/// discovery roots, solutions at its top level, and its FileCommand patterns anywhere
/// beneath its root — each passed through its own `ContentLedger`. Deliveries run under
/// the ExecutionContext the session subscribed from, so its process registry and log
/// sink are the ones in scope; each stream thread is started with flow suppressed so it
/// carries no session's context.
module FsHotWatch.SharedWatchPool

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Watcher
open FsHotWatch.WatchRouter
open FsHotWatch.RepositoryIdentity

/// Opens a native stream: directories, kernel exclusions, file handler, must-scan
/// handler, latency in seconds.
type internal NativeFactory = FileWatcher.NativeStreamFactory

/// A session's own watcher, used when the shared stream is unavailable (off macOS, or
/// the native stream refused to start): root, change handler, FileCommand patterns,
/// latency.
type internal FallbackFactory = string -> (FileChangeKind -> unit) -> FilePattern list -> float -> FileWatcher

/// What the pool holds and what its streams have delivered, since it was created.
type PoolStats =
    {
        NativeStreams: int
        Subscribers: int
        EventsReceived: int64
        /// Events routed to a session (before that session's own rules).
        EventsDelivered: int64
        /// Events no attached session owns — a sibling worktree nobody attached.
        EventsUnowned: int64
        /// Events owned by a session nested in the one whose stream reported them. That
        /// session's own stream carries them too; they are not delivered twice.
        EventsOwnedElsewhere: int64
        /// Events inside a worktree's VCS metadata, used only to learn worktree roots.
        MetadataEvents: int64
        /// Deliveries whose session handler threw.
        DeliveryFailures: int64
    }

/// One subscribed session: its rules, its ledger, its context.
type private Subscriber
    (root: string, extraPatterns: FilePattern list, onChange: FileChangeKind -> unit, context: ExecutionContext) =
    let ledger = ContentLedger()
    let accepts = acceptsUnderRoot root extraPatterns

    let report (path: string) =
        if ledger.Observe path then
            onChange (classifyChange path)

    member _.Root = root

    /// Run `work` under the context this session subscribed from.
    member _.InContext(work: unit -> unit) =
        match context with
        | null -> work ()
        | ctx -> ExecutionContext.Run(ctx.CreateCopy(), (fun _ -> work ()), null)

    member _.OnFile(path: string) =
        if accepts path then
            report path

    /// Rescan what `dir` covers of this session's discovery roots.
    member _.Rescan(dir: string) =
        rescanUnderRoot root dir |> Seq.iter report

/// The sessions under one anchor. Routes to the subscribers themselves; a subscriber
/// is its own key.
type private AnchorTable = RoutingTable<Subscriber>

/// True when `dir` holds a worktree's VCS metadata.
let private looksLikeWorktreeRoot (dir: string) =
    Directory.Exists(Path.Combine(dir, ".jj"))
    || Directory.Exists(Path.Combine(dir, ".git"))
    || File.Exists(Path.Combine(dir, ".git"))

/// Run `f` with ExecutionContext flow suppressed, so anything it starts (the native
/// stream's run-loop thread) captures no caller's AsyncLocals.
let private withoutFlow (f: unit -> 'T) : 'T =
    if ExecutionContext.IsFlowSuppressed() then
        f ()
    else
        use _ = ExecutionContext.SuppressFlow()
        f ()

/// Real FSEvents streams.
let internal defaultNative: NativeFactory =
    fun dirs exclusions onFile onMustScan latency ->
        MacFsEvents.createExcluding dirs exclusions onFile onMustScan latency :> IDisposable

/// The per-worktree watcher a session would have had without the pool.
let internal defaultFallback: FallbackFactory =
    fun root onChange patterns latency -> FileWatcher.create root onChange None patterns latency

type WatchPool internal (nativeFactory: NativeFactory, fallback: FallbackFactory) =
    let gate = Lock()

    let locked work = Locking.locked gate work

    let mutable anchors: Map<string, AnchorTable> = Map.empty
    let streams = ConcurrentDictionary<Subscriber, IDisposable>(HashIdentity.Reference)
    let mutable received = 0L
    let mutable delivered = 0L
    let mutable unowned = 0L
    let mutable elsewhere = 0L
    let mutable metadata = 0L
    let mutable failures = 0L

    // Whether a directory is a worktree root, remembered. A worktree created after a
    // directory was remembered as "not one" is learned from its metadata event.
    let knownRoots = ConcurrentDictionary<string, bool>()

    let isWorktreeRoot (dir: string) =
        knownRoots.GetOrAdd(dir, looksLikeWorktreeRoot)

    let snapshot anchor =
        Map.tryFind anchor (Volatile.Read(&anchors))

    let deliver (sub: Subscriber) (work: unit -> unit) =
        try
            sub.InContext work
        with ex ->
            Interlocked.Increment(&failures) |> ignore
            Logging.warn "watch-pool" $"delivery to the session at %s{sub.Root} failed: %s{ex.Message}"

    /// A file event on `own`'s stream.
    let onFile (anchor: string) (own: Subscriber) (path: string) =
        Interlocked.Increment(&received) |> ignore

        match worktreeRootOfMetadataPath path with
        | Some root ->
            Interlocked.Increment(&metadata) |> ignore
            knownRoots[root] <- true
        | None ->
            match snapshot anchor |> Option.bind (RoutingTable.route isWorktreeRoot path) with
            | Some sub when obj.ReferenceEquals(sub, own) ->
                Interlocked.Increment(&delivered) |> ignore
                deliver sub (fun () -> sub.OnFile path)
            | Some _ -> Interlocked.Increment(&elsewhere) |> ignore
            | None -> Interlocked.Increment(&unowned) |> ignore

    /// A must-scan on `own`'s stream rescans `own` only: every other session's stream
    /// reports its own.
    let onMustScan (anchor: string) (own: Subscriber) (dir: string) =
        snapshot anchor
        |> Option.iter (fun table ->
            for sub, scanDir in RoutingTable.routeMustScan isWorktreeRoot dir table do
                if obj.ReferenceEquals(sub, own) then
                    deliver sub (fun () -> sub.Rescan scanDir))

    /// Remove `sub` and close its stream. A subscriber that is already gone (a second
    /// Dispose) changes nothing.
    let unsubscribe (anchor: string) (sub: Subscriber) =
        let closing =
            locked (fun () ->
                match Map.tryFind anchor anchors with
                | Some table when RoutingTable.sessions table |> List.contains sub ->
                    let table = RoutingTable.detach sub table

                    anchors <-
                        if List.isEmpty (RoutingTable.sessions table) then
                            anchors.Remove anchor
                        else
                            anchors.Add(anchor, table)

                    // Attached is exactly when it has a stream: both change under the gate.
                    let _, stream = streams.TryRemove sub
                    Some stream
                | _ -> None)

        closing |> Option.iter (fun stream -> stream.Dispose())

    /// The pool over real FSEvents streams, falling back to each session's own
    /// `FileWatcher`.
    new() = WatchPool(defaultNative, defaultFallback)

    /// Subscribe the session rooted at `root`, routed among the sessions under
    /// `anchor`: open its own stream over `root`, with `root`'s tooling directories and
    /// nested checkouts excluded in the kernel. Changes reach `onChange` under the
    /// ExecutionContext this call runs in. Disposing the result unsubscribes and closes
    /// the stream.
    member _.Subscribe
        (anchor: string, root: string, extraPatterns: FilePattern list, latency: float, onChange: FileChangeKind -> unit) : IDisposable =
        let sub = Subscriber(root, extraPatterns, onChange, ExecutionContext.Capture())

        locked (fun () ->
            let stream =
                withoutFlow (fun () ->
                    nativeFactory [ root ] (kernelExclusions root) (onFile anchor sub) (onMustScan anchor sub) latency)

            streams[sub] <- stream
            let table = Map.tryFind anchor anchors |> Option.defaultValue RoutingTable.empty
            anchors <- anchors.Add(anchor, RoutingTable.attach root sub table))

        { new IDisposable with
            member _.Dispose() = unsubscribe anchor sub }

    /// A `Daemon` watcher factory whose watchers subscribe under `anchor`. Off macOS,
    /// or when the stream cannot start, each session gets its own watcher instead.
    member internal this.WatcherFactoryFor
        (anchor: string, onMacOS: bool)
        : string -> (FileChangeKind -> unit) -> bool option -> FilePattern list -> float -> FileWatcher =
        fun root onChange _ extraPatterns latency ->
            if not onMacOS then
                fallback root onChange extraPatterns latency
            else
                try
                    { Mode = WatcherMode.NativeEvents
                      Disposables = [ this.Subscribe(anchor, root, extraPatterns, latency, onChange) ] }
                with ex ->
                    Logging.warn
                        "watch-pool"
                        $"the native stream over %s{root} could not start (%s{ex.Message}); it watches on its own"

                    fallback root onChange extraPatterns latency

    /// A `Daemon` watcher factory routing among the sessions under `anchor`.
    member this.WatcherFactoryFor(anchor: string) =
        this.WatcherFactoryFor(anchor, OperatingSystem.IsMacOS())

    member _.Stats: PoolStats =
        let current = Volatile.Read(&anchors)

        { NativeStreams = streams.Count
          Subscribers =
            current
            |> Map.fold (fun n _ table -> n + (RoutingTable.sessions table).Length) 0
          EventsReceived = Interlocked.Read(&received)
          EventsDelivered = Interlocked.Read(&delivered)
          EventsUnowned = Interlocked.Read(&unowned)
          EventsOwnedElsewhere = Interlocked.Read(&elsewhere)
          MetadataEvents = Interlocked.Read(&metadata)
          DeliveryFailures = Interlocked.Read(&failures) }

/// Which sessions a worktree's events are routed among: the repository's primary
/// checkout when the worktree lies beneath it (so a checkout nested in another reaches
/// only its own session), otherwise the worktree's own root.
let anchorOf (worktree: ResolvedWorktree) : string =
    let store = worktree.Store.Path.Value

    let up (levels: int) =
        Some(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(store, String.replicate levels "../"))))

    // A jj store is `<primary>/.jj/repo`; a git common dir is `<primary>/.git`.
    let primary =
        match worktree.Store.Provider with
        | VcsProvider.Jujutsu -> up 2
        | VcsProvider.Git when Path.GetFileName store = ".git" -> up 1
        | VcsProvider.Git
        | VcsProvider.Standalone -> None

    match primary with
    | Some p when isWithin p worktree.Root.Value -> p
    | _ -> worktree.Root.Value
