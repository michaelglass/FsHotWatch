/// One native file-event registration per watch anchor, shared by every worktree
/// session beneath it.
///
/// A per-worktree daemon registers its own FSEvents stream plus a `FileSystemWatcher`
/// for top-level solutions and one per FileCommand pattern — each its own fseventsd
/// client (ADR-009). A repository host instead opens ONE recursive stream over the
/// anchor — the repository's primary checkout when the worktree lies beneath it,
/// otherwise the worktree itself — with the anchor's `.jj`/`.git` excluded in the
/// kernel, and routes each event to the session that owns it (`WatchRouter`).
///
/// Each session keeps the legacy watch rules after routing: F# inputs under its
/// discovery roots, solutions at its top level, and its FileCommand patterns anywhere
/// beneath its root — each passed through its own `ContentLedger`. Deliveries run under
/// the ExecutionContext the session subscribed from, so its process registry and log
/// sink are the ones in scope; the stream thread itself is started with flow suppressed
/// so it carries no session's context.
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
type internal NativeFactory = string list -> string list -> (string -> unit) -> (string -> unit) -> float -> IDisposable

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
        /// Events inside a worktree's VCS metadata, used only to learn worktree roots.
        MetadataEvents: int64
        /// Deliveries whose session handler threw.
        DeliveryFailures: int64
    }

/// The patterns a must-scan rescan walks for, as the per-worktree watcher does.
let private rescanPatterns =
    [| "*.fs"; "*.fsx"; "*.fsproj"; "*.props"; "project.assets.json" |]

/// One subscribed session: its rules, its ledger, its context.
type private Subscriber
    (root: string, extraPatterns: FilePattern list, onChange: FileChangeKind -> unit, context: ExecutionContext) =
    let ledger = ContentLedger()
    let discoveryRoots = Discovery.discoveryRoots root

    let isTopLevelSolution (path: string) =
        let ext = Path.GetExtension(path).ToLowerInvariant()
        (ext = ".sln" || ext = ".slnx") && Path.GetDirectoryName path = root

    let accepts (path: string) =
        let underDiscovery = discoveryRoots |> List.exists (fun d -> isWithin d path)

        (underDiscovery && isRelevantFileOrExtra extraPatterns path)
        || isTopLevelSolution path
        || (extraPatterns |> List.exists (fun p -> FilePattern.matches p path)
            && not (PathFilter.isGeneratedPath path))

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
        for discoveryRoot in discoveryRoots do
            let target =
                if isWithin discoveryRoot dir then Some dir
                elif isWithin dir discoveryRoot then Some discoveryRoot
                else None

            match target with
            | Some target when Directory.Exists target ->
                for pattern in rescanPatterns do
                    SafeWalk.bestEffortFilePaths SafeWalk.ToolingExcludedDirs pattern target
                    |> Seq.filter isRelevantFile
                    |> Seq.iter report
            | _ -> ()

[<NoComparison; NoEquality>]
type private AnchorEntry =
    {
        Stream: IDisposable
        /// Routes to the subscribers themselves; a subscriber is its own key.
        Table: RoutingTable<Subscriber>
    }

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
    // `Lock.Enter`/`Exit`, not `lock`: `lock` leaves a never-taken "was the lock
    // acquired" branch behind in every caller.
    let gate = Lock()

    let locked (work: unit -> 'T) : 'T =
        gate.Enter()

        try
            work ()
        finally
            gate.Exit()

    let mutable anchors: Map<string, AnchorEntry> = Map.empty
    let mutable received = 0L
    let mutable delivered = 0L
    let mutable unowned = 0L
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

    let onFile (anchor: string) (path: string) =
        Interlocked.Increment(&received) |> ignore

        match worktreeRootOfMetadataPath path with
        | Some root ->
            Interlocked.Increment(&metadata) |> ignore
            knownRoots[root] <- true
        | None ->
            match
                snapshot anchor
                |> Option.bind (fun e -> RoutingTable.route isWorktreeRoot path e.Table)
            with
            | Some sub ->
                Interlocked.Increment(&delivered) |> ignore
                deliver sub (fun () -> sub.OnFile path)
            | None -> Interlocked.Increment(&unowned) |> ignore

    let onMustScan (anchor: string) (dir: string) =
        snapshot anchor
        |> Option.iter (fun entry ->
            for sub, scanDir in RoutingTable.routeMustScan isWorktreeRoot dir entry.Table do
                deliver sub (fun () -> sub.Rescan scanDir))

    /// Remove `sub`, closing the stream when it was the last. A subscriber that is
    /// already gone (a second Dispose) changes nothing.
    let unsubscribe (anchor: string) (sub: Subscriber) =
        let closing =
            locked (fun () ->
                match Map.tryFind anchor anchors with
                | Some entry when RoutingTable.sessions entry.Table |> List.contains sub ->
                    let table = RoutingTable.detach sub entry.Table

                    if List.isEmpty (RoutingTable.sessions table) then
                        anchors <- anchors.Remove anchor
                        Some entry.Stream
                    else
                        anchors <- anchors.Add(anchor, { entry with Table = table })
                        None
                | _ -> None)

        closing |> Option.iter (fun stream -> stream.Dispose())

    /// The pool over real FSEvents streams, falling back to each session's own
    /// `FileWatcher`.
    new() = WatchPool(defaultNative, defaultFallback)

    /// Subscribe the session rooted at `root` to the stream over `anchor`, opening
    /// that stream if this is its first subscriber. `latency` applies only when the
    /// stream is opened; later subscribers share it. Changes reach `onChange` under the
    /// ExecutionContext this call runs in. Disposing the result unsubscribes; the last
    /// unsubscription closes the stream.
    member _.Subscribe
        (anchor: string, root: string, extraPatterns: FilePattern list, latency: float, onChange: FileChangeKind -> unit) : IDisposable =
        let sub = Subscriber(root, extraPatterns, onChange, ExecutionContext.Capture())

        locked (fun () ->
            let entry =
                match Map.tryFind anchor anchors with
                | Some entry -> entry
                | None ->
                    let exclusions = [ Path.Combine(anchor, ".jj"); Path.Combine(anchor, ".git") ]

                    { Stream =
                        withoutFlow (fun () ->
                            nativeFactory [ anchor ] exclusions (onFile anchor) (onMustScan anchor) latency)
                      Table = RoutingTable.empty }

            anchors <-
                anchors.Add(
                    anchor,
                    { entry with
                        Table = RoutingTable.attach root sub entry.Table }
                ))

        { new IDisposable with
            member _.Dispose() = unsubscribe anchor sub }

    /// A `Daemon` watcher factory whose watchers subscribe to the stream over
    /// `anchor`. Off macOS, or when the stream cannot start, each session gets its own
    /// watcher instead.
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
                        $"the shared stream over %s{anchor} could not start (%s{ex.Message}); %s{root} watches on its own"

                    fallback root onChange extraPatterns latency

    /// A `Daemon` watcher factory over the stream at `anchor`.
    member this.WatcherFactoryFor(anchor: string) =
        this.WatcherFactoryFor(anchor, OperatingSystem.IsMacOS())

    member _.Stats: PoolStats =
        let current = Volatile.Read(&anchors)

        { NativeStreams = current.Count
          Subscribers = current |> Map.fold (fun n _ e -> n + (RoutingTable.sessions e.Table).Length) 0
          EventsReceived = Interlocked.Read(&received)
          EventsDelivered = Interlocked.Read(&delivered)
          EventsUnowned = Interlocked.Read(&unowned)
          MetadataEvents = Interlocked.Read(&metadata)
          DeliveryFailures = Interlocked.Read(&failures) }

/// Where a worktree's shared stream is anchored: the repository's primary checkout
/// when the worktree lies beneath it (so every worktree nested in the primary shares
/// one stream), otherwise the worktree's own root.
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
