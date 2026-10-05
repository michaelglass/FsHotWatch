module FsHotWatch.Watcher

open System
open System.IO
open System.Threading
open FsHotWatch.Events

/// How the repository is being observed. Decided once, at watcher construction.
[<RequireQualifiedAccess>]
type WatcherMode =
    /// Kernel-delivered events: FSEvents on macOS, `FileSystemWatcher` elsewhere.
    | NativeEvents
    /// A non-refusal setup fault (a `FileSystemWatcher` that would not start, a
    /// callback that would not pin); a 1-second content-snapshot poller carries the
    /// same observation contract. `reason` is the setup error, for startup diagnostics.
    /// A native stream macOS REFUSED past the retry budget never lands here — that is
    /// `NativeStreamRefusedPastBudgetException`, and the daemon fails closed on it.
    | ContentPolling of reason: string

/// How the native FSEvents stream was refused: macOS said no on every
/// attempt of the retry budget. A persistent failure, not a transient one — nothing
/// was built, and the daemon's startup diagnosis names these three facts.
type NativeStartRefusal =
    {
        /// Attempts made: one per backoff entry, plus the first.
        Attempts: int
        /// Backoff actually waited, in milliseconds, across all retries.
        BackoffSpentMs: int
        /// The last refusal's message (`FSEventStreamStart returned false — ...`).
        LastRefusal: string
    }

module NativeStartRefusal =
    /// The one sentence the daemon prints for a refused start: what was refused,
    /// how much of the budget was spent, and what macOS said last.
    let describe (refusal: NativeStartRefusal) =
        $"macOS refused the native FSEvents stream on all %d{refusal.Attempts} attempts (%d{refusal.BackoffSpentMs} ms of backoff spent; last: %s{refusal.LastRefusal})"

/// Watcher construction did NOT produce a watcher: the native FSEvents stream was
/// refused past the retry budget. Raised out of `FileWatcher.create` on macOS with no
/// partial resource left behind; the polling fallback is never the answer to it.
type NativeStreamRefusedPastBudgetException(refusal: NativeStartRefusal) =
    inherit Exception(NativeStartRefusal.describe refusal)
    member _.Refusal = refusal

/// Holds disposable watchers monitoring a repository for F# file changes.
[<NoComparison; NoEquality>]
type FileWatcher =
    { Mode: WatcherMode
      Disposables: IDisposable list }

    interface IDisposable with
        member this.Dispose() =
            for d in this.Disposables do
                d.Dispose()

/// True if the file is `project.assets.json` — dotnet restore's materialized
/// package graph. Lives under `obj/`, so this check intentionally bypasses
/// `isGeneratedPath` (every other obj/ entry stays excluded).
let internal isProjectAssetsJson (path: string) =
    Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)

/// Returns true if the file path has a relevant extension and is not in obj/ or bin/.
/// `project.assets.json` is the documented exception: it lives in obj/ but is the
/// canonical post-`dotnet restore` signal that a project's package graph changed.
/// See docs/fr-auto-refresh-fsproj-changes.md.
let internal isRelevantFile (path: string) =
    if isProjectAssetsJson path then
        true
    else
        let ext = Path.GetExtension(path).ToLowerInvariant()

        let isRelevantExt =
            ext = ".fs"
            || ext = ".fsx"
            || ext = ".fsproj"
            || ext = ".sln"
            || ext = ".slnx"
            || ext = ".props"

        isRelevantExt && not (PathFilter.isGeneratedPath path)

/// How a FileCommandPlugin pattern string matches paths. Parsed once at
/// config-load time via `FilePattern.parse` so downstream code never has to
/// re-inspect string shape.
[<RequireQualifiedAccess; NoComparison>]
type FilePattern =
    /// `*.ratchet.json` → matches any path ending with the suffix (including the leading dot).
    | Wildcard of suffix: string
    /// `coverage-ratchet.json` → matches only paths whose basename equals the given filename.
    | Literal of fileName: string

module FilePattern =
    /// Parse a pattern string. A leading `*` denotes a wildcard suffix;
    /// anything else is treated as a literal filename.
    ///
    /// Patterns with an embedded (non-leading) `*` are REJECTED: the raw pattern
    /// doubles as the `FileSystemWatcher.Filter` glob (which would happily glob
    /// `schema.*.sql`), while the in-process `matches` treats it as a literal
    /// suffix/basename that never matches the same files — so the OS event would fire
    /// and then be silently dropped. Rejecting at config-load time makes that loud.
    let parse (pattern: string) : FilePattern =
        if String.IsNullOrEmpty(pattern) then
            invalidArg (nameof pattern) "File pattern must not be empty."
        elif pattern.StartsWith("*") then
            let suffix = pattern.Substring(1)

            if suffix.Contains("*") then
                invalidArg
                    (nameof pattern)
                    $"Unsupported file pattern '%s{pattern}': only a single leading '*' is supported (e.g. '*.ratchet.json' or a literal filename)."

            FilePattern.Wildcard suffix
        elif pattern.Contains("*") then
            invalidArg
                (nameof pattern)
                $"Unsupported file pattern '%s{pattern}': '*' is only supported as a leading wildcard (e.g. '*.ratchet.json' or a literal filename)."
        else
            FilePattern.Literal pattern

    /// Serialize back to the original pattern string. Used for the underlying
    /// `FileSystemWatcher.Filter` glob and for human-readable diagnostics.
    let toString (pattern: FilePattern) : string =
        match pattern with
        | FilePattern.Wildcard suffix -> "*" + suffix
        | FilePattern.Literal name -> name

    /// True when `path` matches the pattern.
    let matches (pattern: FilePattern) (path: string) : bool =
        match pattern with
        | FilePattern.Wildcard suffix -> path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        | FilePattern.Literal name ->
            let fileName = Path.GetFileName(path)
            fileName.Equals(name, StringComparison.OrdinalIgnoreCase)

    /// A synthetic path that `matches` this pattern — used by rerun to emit a
    /// fake FileChanged event that triggers only the target plugin.
    ///
    /// Rooted at `root`, and takes it as an argument so a caller cannot produce the
    /// bare relative form by omission. A relative path here is resolved against the
    /// PROCESS working directory downstream, which is a directory that can be
    /// deleted out from under a long-lived daemon — and then a rerun fails on the
    /// vanished cwd rather than on anything to do with the plugin being re-fired.
    /// Rooting keeps matching intact: a literal still compares by file name, and a
    /// wildcard still compares by suffix.
    let syntheticPath (root: string) (pattern: FilePattern) : string =
        let name =
            match pattern with
            | FilePattern.Wildcard suffix -> "_fshw_rerun_" + suffix
            | FilePattern.Literal name -> name

        Path.Combine(root, name)

/// Like `isRelevantFile`, but also accepts files matching any of the given
/// FileCommandPlugin patterns (for non-source extensions like `.ratchet.json`).
let internal isRelevantFileOrExtra (extraPatterns: FilePattern list) (path: string) =
    if isRelevantFile path then
        true
    else
        let matchesExtra =
            extraPatterns |> List.exists (fun p -> FilePattern.matches p path)

        matchesExtra && not (PathFilter.isGeneratedPath path)

/// Classify a file path as a solution, project, or source change.
/// `obj/project.assets.json` routes through `ProjectChanged` so the daemon
/// reacts to it identically to a raw `.fsproj` edit — same dispatch path,
/// same downstream invalidation + re-check semantics.
let internal classifyChange (path: string) =
    let ext = Path.GetExtension(path).ToLowerInvariant()

    if ext = ".sln" || ext = ".slnx" then
        SolutionChanged
    elif ext = ".fsproj" || ext = ".props" || isProjectAssetsJson path then
        ProjectChanged [ path ]
    else
        SourceChanged [ path ]

/// Top-level directories whose events FSEvents drops in the kernel, before they are
/// queued for the daemon. fseventsd buffers every per-file event for every client
/// until that client drains, so a directory no watcher reports from is still memory
/// in fseventsd while the daemon is busy. Every name is one the polling walk also
/// prunes (`SafeWalk.ToolingExcludedDirs`): what the native stream never sees, the
/// polling fallback never visits either. `.workspaces` holds sibling checkouts, each
/// watched by its own stream.
let private kernelExcludedDirs =
    [ ".jj"
      ".git"
      ".fshw"
      "node_modules"
      ".devenv"
      ".direnv"
      ".idea"
      ".workspaces" ]

/// The exclusion prefixes for a native stream over `root` (at most
/// `MacFsEvents.MaxExclusionPaths`).
let internal kernelExclusions (root: string) =
    kernelExcludedDirs |> List.map (fun name -> Path.Combine(root, name))

/// What a stream over the whole of `root` passes on: F# inputs under the discovery
/// roots, solutions at the top level, and FileCommand patterns anywhere outside
/// build output.
let internal acceptsUnderRoot (root: string) (extraPatterns: FilePattern list) : string -> bool =
    let discoveryRoots = Discovery.discoveryRoots root

    fun path ->
        let ext = Path.GetExtension(path).ToLowerInvariant()

        (discoveryRoots |> List.exists (fun d -> WatchRouter.isWithin d path)
         && isRelevantFileOrExtra extraPatterns path)
        || ((ext = ".sln" || ext = ".slnx") && Path.GetDirectoryName path = root)
        || (extraPatterns |> List.exists (fun p -> FilePattern.matches p path)
            && not (PathFilter.isGeneratedPath path))

/// The F# inputs a must-scan of `dir` finds under `root`'s discovery roots: the part
/// of `dir` inside one, or the whole discovery root when `dir` contains it.
let internal rescanUnderRoot (root: string) (dir: string) : string seq =
    seq {
        for discoveryRoot in Discovery.discoveryRoots root do
            let target =
                if WatchRouter.isWithin discoveryRoot dir then
                    Some dir
                elif WatchRouter.isWithin dir discoveryRoot then
                    Some discoveryRoot
                else
                    None

            match target with
            | Some target when Directory.Exists target ->
                for pattern in [| "*.fs"; "*.fsx"; "*.fsproj"; "*.props"; "project.assets.json" |] do
                    yield!
                        SafeWalk.bestEffortFilePaths SafeWalk.ToolingExcludedDirs pattern target
                        |> Seq.filter isRelevantFile
            | _ -> ()
    }

/// The one question both watchers ask of a path: have its BYTES changed since we
/// last looked at it?
///
/// A kernel notification does not answer that. FSEvents and `FileSystemWatcher`
/// both fire for a touch, for an open-for-write that wrote nothing, for a rewrite
/// with identical content, and for every path named in a coalesced batch. One
/// predicate, used by the native watcher and the polling one, is what keeps the two
/// from disagreeing about what a change is.
module internal ContentChange =
    /// True when `current` must be reported as a change against `previous`.
    /// `None` on either side means the file was not there, so absent-then-present
    /// and present-then-absent stay real changes instead of collapsing into
    /// "the hash differs".
    ///
    /// An unreadable file differs from everything, itself included: `ContentHash`'s
    /// sentinel is fail-closed, and a file nobody could read is never evidence that
    /// nothing happened.
    let differs (previous: string option) (current: string option) : bool =
        match previous, current with
        | None, None -> false
        | Some prior, Some latest ->
            not (ContentHash.isReadable latest)
            || not (String.Equals(prior, latest, StringComparison.Ordinal))
        | Some _, None
        | None, Some _ -> true

/// How far before a native stream started a file's last write must lie for a
/// coalesced rescan to record its first sighting without reporting it. File times
/// come from a coarser clock than `DateTime.UtcNow` (whole seconds on some
/// filesystems), so a write just after the start can be stamped just before it.
let internal coalescedFirstSightMargin = TimeSpan.FromSeconds 2.0

/// Whether a coalesced rescan's FIRST sighting of a file last written at
/// `lastWriteUtc` is one the stream started at `streamStartedAt` cannot owe anyone.
///
/// FSEvents coalesces or drops only events that happened after its stream started,
/// and any write since then moves the file's time past that start (less
/// `coalescedFirstSightMargin`). A file whose time lies before it was not written
/// while the stream ran; it is part of the tree the daemon's cold scan reads. With no
/// stream start recorded, nothing predates it.
///
/// Residual risk: a tool that writes a never-seen file with an OLD time (`cp -p`,
/// `tar -x`, `touch -t`) is missed by a coalesced rescan until that file's next write.
let internal predatesStream (streamStartedAt: DateTime option) (lastWriteUtc: DateTime) : bool =
    match streamStartedAt with
    | Some started -> lastWriteUtc < started - coalescedFirstSightMargin
    | None -> false

/// What a watcher last saw at each path it reported on.
///
/// Starts empty: the first notification for a path finds nothing recorded, reads as
/// a creation, and emits. That costs one redundant change per path over a daemon's
/// lifetime and no startup tree walk, and it errs toward reporting rather than
/// toward missing an edit made before the watcher existed.
///
/// A coalesced rescan is the exception (`ObserveRescanned`): it names every file
/// under a root, so a cold ledger would report the whole tree as created. There, a
/// first sighting of a file last written before the stream started is recorded
/// silently (`predatesStream`).
///
/// FSEvents callbacks arrive on their own thread, so read-hash-record is one step
/// under the lock rather than three racing ones.
type internal ContentLedger() =
    let syncRoot = obj ()
    let mutable known: Map<string, string> = Map.empty
    let mutable streamStartedAt: DateTime option = None

    // `silentFirstSight` sees a path the ledger has no record of, holding a readable
    // file; true records its hash without reporting a change.
    let observe (silentFirstSight: string -> bool) (path: string) : bool =
        lock syncRoot (fun () ->
            let previous = Map.tryFind path known

            let current =
                if File.Exists(path) then
                    Some(ContentHash.ofFile path)
                else
                    None

            known <-
                match current with
                | Some hash -> Map.add path hash known
                | None -> Map.remove path known

            match previous, current with
            | None, Some hash when ContentHash.isReadable hash && silentFirstSight path -> false
            | _ -> ContentChange.differs previous current)

    /// Record that the native stream feeding this ledger is starting now. Called
    /// before the stream is created, so the recorded time is never later than the
    /// stream's real start.
    member _.StreamStarting() =
        lock syncRoot (fun () -> streamStartedAt <- Some DateTime.UtcNow)

    /// Read `path`, record what is there now, and report whether it differs from
    /// the last recorded observation.
    member _.Observe(path: string) : bool = observe (fun _ -> false) path

    /// `Observe` for a path a coalesced rescan found: a first sighting of a file last
    /// written before the stream started is recorded without reporting a change. The
    /// bytes are read BEFORE the write time, so a write landing between the two moves
    /// the time past the start and is reported.
    member _.ObserveRescanned(path: string) : bool =
        observe (fun path -> predatesStream streamStartedAt (File.GetLastWriteTimeUtc path)) path

/// One content-addressed view of the files a polling watcher is responsible for.
type internal PollingSnapshot =
    { Files: Map<string, string>
      UnreadableFiles: Set<string>
      Holes: Set<string> }

/// Assets live under obj, but bin can never contain a restore graph input.
let internal pollingAssetsExcludedDirs =
    SafeWalk.ToolingExcludedDirs |> Set.add "bin"

[<NoComparison; NoEquality>]
type internal PollTimer =
    { Arm: int -> unit
      Dispose: unit -> unit }

type internal PollTimerFactory = (unit -> unit) -> PollTimer

let internal takePollingSnapshotWith
    (walk: Set<string> -> string -> string -> SafeWalk.WalkResult)
    (topLevelSolutions: string -> string list * Set<string>)
    (repoRoot: string)
    (roots: string list)
    (extraPatterns: FilePattern list)
    =
    let files = ResizeArray<string>()
    let holes = ResizeArray<string>()

    let collect (accept: string -> bool) (walked: SafeWalk.WalkResult) =
        walked.Files
        |> List.iter (fun file ->
            if accept file.FullName then
                files.Add(file.FullName))

        walked.Skipped |> List.iter (fun skipped -> holes.Add(skipped.Path))

    for root in roots do
        // One source walk prunes every generated subtree. A second narrow walk
        // admits obj solely for project.assets.json while still pruning bin.
        walk SafeWalk.SourceExcludedDirs "*" root |> collect isRelevantFile

        walk pollingAssetsExcludedDirs "project.assets.json" root
        |> collect isProjectAssetsJson

    for extra in extraPatterns do
        let accept path =
            FilePattern.matches extra path && not (PathFilter.isGeneratedPath path)

        walk SafeWalk.SourceExcludedDirs (FilePattern.toString extra) repoRoot
        |> collect accept

    let solutions, solutionHoles = topLevelSolutions repoRoot
    files.AddRange(solutions)
    holes.AddRange(solutionHoles)

    let hashes =
        files
        |> Seq.distinct
        |> Seq.map (fun path -> path, ContentHash.ofFile path)
        |> Map.ofSeq

    { Files = hashes
      UnreadableFiles =
        hashes
        |> Map.toSeq
        |> Seq.choose (fun (path, hash) -> if ContentHash.isReadable hash then None else Some path)
        |> Set.ofSeq
      Holes = holes |> Set.ofSeq }

/// A deterministic snapshot watcher used when macOS cannot start its native
/// FSEvents stream. The first snapshot is only a baseline.
type internal PollingFileWatcher
    (
        repoRoot: string,
        onChange: FileChangeKind -> unit,
        extraPatterns: FilePattern list,
        startAutomatically: bool,
        snapshotOverride: (unit -> PollingSnapshot) option,
        timerFactoryOverride: PollTimerFactory option
    ) =
    let syncRoot = obj ()
    let roots = Discovery.existingDiscoveryRoots repoRoot
    let mutable disposed = false
    let mutable polling = false
    let mutable timer: PollTimer option = None

    let takeFilesystemSnapshot () =
        let topLevelSolutions rootPath =
            try
                let root = DirectoryInfo(rootPath)

                [ for pattern in [ "*.sln"; "*.slnx" ] do
                      yield!
                          root.GetFiles(pattern, SearchOption.TopDirectoryOnly)
                          |> Array.map (fun file -> file.FullName) ],
                Set.empty
            with
            | :? IOException
            | :? UnauthorizedAccessException -> [], Set.singleton rootPath

        takePollingSnapshotWith SafeWalk.walk topLevelSolutions repoRoot roots extraPatterns

    let takeSnapshot = defaultArg snapshotOverride takeFilesystemSnapshot
    let mutable previous = (takeSnapshot ()).Files

    let emit change =
        if not disposed then
            try
                onChange change
            with ex ->
                Logging.warn "polling-watcher" $"change callback failed: %s{ex.Message}"

    let pollUnsafe () =
        let current = takeSnapshot ()

        let createdOrModified =
            current.Files
            |> Map.toSeq
            |> Seq.choose (fun (path, hash) ->
                if ContentChange.differs (Map.tryFind path previous) (Some hash) then
                    Some path
                else
                    None)
            |> Set.ofSeq

        let deleted =
            previous
            |> Map.toSeq
            |> Seq.choose (fun (path, _) ->
                if Map.containsKey path current.Files then
                    None
                else
                    Some path)
            |> Set.ofSeq

        previous <- current.Files

        Set.unionMany [ createdOrModified; deleted; current.UnreadableFiles ]
        |> Set.iter (classifyChange >> emit)

        if not current.Holes.IsEmpty then
            for hole in current.Holes do
                Logging.warn "polling-watcher" $"snapshot could not read %s{hole}; requesting conservative full refresh"

            emit SolutionChanged

    let tryPoll () =
        if Monitor.TryEnter(syncRoot) then
            try
                if not disposed && not polling then
                    polling <- true

                    try
                        pollUnsafe ()
                    finally
                        polling <- false
            finally
                Monitor.Exit(syncRoot)

    let scheduleNext () =
        lock syncRoot (fun () ->
            if not disposed then
                timer |> Option.iter (fun handle -> handle.Arm(1000)))

    do
        if startAutomatically then
            let defaultTimerFactory onTick =
                let systemTimer =
                    new Timer(TimerCallback(fun _ -> onTick ()), null, Timeout.Infinite, Timeout.Infinite)

                { Arm = fun dueMs -> systemTimer.Change(dueMs, Timeout.Infinite) |> ignore
                  Dispose = systemTimer.Dispose }

            let timerFactory = defaultArg timerFactoryOverride defaultTimerFactory

            timer <-
                Some(
                    timerFactory (fun () ->
                        try
                            tryPoll ()
                        with ex ->
                            Logging.warn "polling-watcher" $"snapshot failed: %s{ex.Message}"

                        scheduleNext ())
                )

            scheduleNext ()

    /// Run one snapshot/diff cycle. Overlapping or re-entrant polls are skipped.
    member _.Poll() = tryPoll ()

    interface IDisposable with
        member _.Dispose() =
            lock syncRoot (fun () ->
                if not disposed then
                    disposed <- true

                    timer |> Option.iter (fun handle -> handle.Dispose())
                    timer <- None)

/// Functions for creating file watchers.
module FileWatcher =
    /// Opens a native stream: directories, kernel exclusions, file handler, must-scan
    /// handler, latency in seconds.
    type internal NativeStreamFactory =
        string list -> string list -> (string -> unit) -> (string -> unit) -> float -> IDisposable

    type internal SystemWatcherSpec =
        { Directory: string
          IncludeSubdirectories: bool
          Filters: string list }

    type internal SystemWatcherFactory = (string -> unit) -> SystemWatcherSpec -> IDisposable
    type internal PollingWatcherFactory = string -> (FileChangeKind -> unit) -> FilePattern list -> IDisposable

    /// Bounded backoff for a native stream macOS transiently refuses.
    /// `BackoffMs` is the whole budget: one wait per retry, so an empty list means a
    /// single attempt. Only a `MacFsEvents.NativeStreamRefusedException` spends it.
    [<NoComparison; NoEquality>]
    type internal NativeStartRetry =
        { BackoffMs: int list
          Sleep: int -> unit }

    module internal NativeStartRetry =
        /// Three retries, 1.3 s in total. A healthy start pays none of it.
        let defaults =
            { BackoffMs = [ 100; 300; 900 ]
              Sleep = Thread.Sleep }

        /// One attempt, no waiting.
        let none = { BackoffMs = []; Sleep = ignore }

    /// What driving the native factory through the retry budget produced. A DU, not
    /// an exception the caller has to remember to re-raise: the fallback that handles
    /// `Faulted` cannot be reached by `RefusedPastBudget`.
    [<NoComparison; NoEquality>]
    type internal NativeStartOutcome =
        /// The stream started (on the first attempt or after a transient refusal).
        | Started of IDisposable
        /// Every attempt was refused. Persistent: the daemon must fail closed.
        | RefusedPastBudget of NativeStartRefusal
        /// A non-refusal fault. The generic polling fallback's case, unchanged.
        | Faulted of exn

    /// Run the native factory, retrying a refused start through the budget. Only a
    /// `MacFsEvents.NativeStreamRefusedException` is retried; any other exception is
    /// `Faulted` at once, and a refusal on the last attempt is `RefusedPastBudget`.
    let private startNativeWithRetry (retry: NativeStartRetry) (start: unit -> IDisposable) : NativeStartOutcome =
        let rec attempt n spentMs backoff =
            let started =
                try
                    Started(start ())
                with
                | :? MacFsEvents.NativeStreamRefusedException as ex ->
                    RefusedPastBudget
                        { Attempts = n
                          BackoffSpentMs = spentMs
                          LastRefusal = ex.Message }
                | ex -> Faulted ex

            match started, backoff with
            | RefusedPastBudget refusal, delay :: rest ->
                Logging.warn
                    "watcher"
                    $"native FSEvents setup refused (attempt %d{n}: %s{refusal.LastRefusal}); retrying in %d{delay} ms"

                retry.Sleep delay
                attempt (n + 1) (spentMs + delay) rest
            | outcome, _ -> outcome

        attempt 1 0 retry.BackoffMs

    /// Whether a system watcher built from `spec` passes `path` on. The native
    /// subscription is one broad `*`; this is the exact pattern set, applied in-process.
    let internal specAccepts (spec: SystemWatcherSpec) (path: string) =
        let fileName = Path.GetFileName(path)

        spec.Filters
        |> List.exists (fun filter ->
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, fileName, true))

    let private defaultSystemWatcherFactory handle spec =
        let watcher = new FileSystemWatcher(spec.Directory)

        try
            watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
            watcher.IncludeSubdirectories <- spec.IncludeSubdirectories
            // FileSystemWatcher.Filters silently misses later patterns on some
            // platforms. One broad native subscription keeps the handle/buffer
            // footprint constant; apply the exact pattern set in-process.
            watcher.Filter <- "*"

            let handleEvent (event: FileSystemEventArgs) =
                if specAccepts spec event.FullPath then
                    handle event.FullPath

            watcher.Changed.Add(handleEvent)
            watcher.Created.Add(handleEvent)
            watcher.Deleted.Add(handleEvent)
            watcher.Renamed.Add(handleEvent)
            watcher.EnableRaisingEvents <- true
            watcher :> IDisposable
        with ex ->
            watcher.Dispose()
            raise ex

    let private defaultPollingWatcherFactory repoRoot onChange extraPatterns =
        new PollingFileWatcher(repoRoot, onChange, extraPatterns, true, None, None) :> IDisposable

    /// One native stream over the whole worktree root, its tooling directories and
    /// nested checkouts excluded in the kernel, routing F# inputs under the discovery
    /// roots, top-level solutions and FileCommand patterns. One stream is one fseventsd
    /// client; a `FileSystemWatcher` per solution or pattern would each be another
    /// recursive, per-file client over the same root that no exclusion reaches.
    let private createMacOS
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStartRetry: NativeStartRetry)
        (nativeStreamFactory: NativeStreamFactory)
        (pollingWatcherFactory: PollingWatcherFactory)
        : FileWatcher =
        // A notification names a path; it does not establish that the path changed.
        // One ledger for the per-file and must-scan callbacks, so a coalesced batch
        // and a single-file event agree about a given path.
        let ledger = ContentLedger()

        // FSEvents reports real paths. A root spelled through a symlink (`/var` is
        // `/private/var`) is watched at its real path and matched under both spellings.
        let watchRoot =
            match RepositoryIdentity.canonicalize repoRoot with
            | Ok canonical -> canonical.Value
            | Error _ -> repoRoot

        let roots = List.distinct [ repoRoot; watchRoot ]
        let accepts = roots |> List.map (fun root -> acceptsUnderRoot root extraPatterns)

        let handle path =
            if accepts |> List.exists (fun accept -> accept path) && ledger.Observe path then
                onChange (classifyChange path)

        // A coalesced native event means Apple requires a recursive scan of that subtree.
        let onCoalesced dirPath =
            for root in roots do
                for path in rescanUnderRoot root dirPath do
                    if ledger.ObserveRescanned path then
                        onChange (classifyChange path)

        // Matched OUTSIDE any exception handler: the refusal is a value here, so
        // the polling fallback below cannot catch it (case 2).
        match
            startNativeWithRetry nativeStartRetry (fun () ->
                ledger.StreamStarting()
                nativeStreamFactory [ watchRoot ] (kernelExclusions watchRoot) handle onCoalesced latencySeconds)
        with
        | Started nativeStream ->
            { Mode = WatcherMode.NativeEvents
              Disposables = [ nativeStream ] }
        | Faulted ex ->
            // The generic fallback: a non-refusal fault demotes this daemon to polling.
            Logging.warn
                "watcher"
                $"macOS file-event setup failed (%s{ex.Message}); using a 1-second content-snapshot polling watcher"

            { Mode = WatcherMode.ContentPolling ex.Message
              Disposables = [ pollingWatcherFactory repoRoot onChange extraPatterns ] }
        | RefusedPastBudget refusal ->
            // Nothing was built, so there is nothing to roll back; the daemon owns
            // the pidfile and lock and releases them.
            Logging.error "watcher" (NativeStartRefusal.describe refusal)
            raise (NativeStreamRefusedPastBudgetException refusal)

    /// macOS construction seam used by deterministic failure-path tests.
    let internal createWithNativeStream
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStartRetry: NativeStartRetry)
        (nativeStreamFactory: NativeStreamFactory)
        =
        createMacOS
            repoRoot
            onChange
            extraPatterns
            latencySeconds
            nativeStartRetry
            nativeStreamFactory
            defaultPollingWatcherFactory

    /// Complete setup seam: native stream and polling fallback both injected.
    let internal createWithFactories
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStartRetry: NativeStartRetry)
        (nativeStreamFactory: NativeStreamFactory)
        (pollingWatcherFactory: PollingWatcherFactory)
        =
        createMacOS
            repoRoot
            onChange
            extraPatterns
            latencySeconds
            nativeStartRetry
            nativeStreamFactory
            pollingWatcherFactory

    /// The non-macOS layout: one recursive system watcher per existing discovery
    /// root, one for top-level solutions, one per extra pattern. The factory is a
    /// parameter so the layout and routing are testable without live file events.
    let internal createPortable
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (systemWatcherFactory: SystemWatcherFactory)
        : FileWatcher =
        let handle (path: string) =
            if isRelevantFileOrExtra extraPatterns path then
                onChange (classifyChange path)

        let slnWatcher =
            systemWatcherFactory
                handle
                { Directory = repoRoot
                  IncludeSubdirectories = false
                  Filters = [ "*.sln"; "*.slnx" ] }

        // Each FileCommandPlugin pattern gets its own recursive watcher at the
        // repo root. .NET handles wildcard and literal filter forms.
        let extraWatchers =
            extraPatterns
            |> List.map (fun pattern ->
                systemWatcherFactory
                    handle
                    { Directory = repoRoot
                      IncludeSubdirectories = true
                      Filters = [ FilePattern.toString pattern ] })

        let createFsw (dir: string) =
            if Directory.Exists(dir) then
                Some(
                    systemWatcherFactory
                        handle
                        { Directory = dir
                          IncludeSubdirectories = true
                          Filters = [ "*.fs"; "*.fsx"; "*.fsproj"; "*.props"; "project.assets.json" ] }
                )
            else
                None

        let watchers =
            (Discovery.discoveryRoots repoRoot |> List.map createFsw) @ [ Some slnWatcher ]
            |> List.choose id

        { Mode = WatcherMode.NativeEvents
          Disposables = watchers @ extraWatchers }

    /// Create a FileWatcher that monitors src/ and tests/ for F#-relevant file changes,
    /// plus any files matching `extraPatterns` (from FileCommandPlugin patterns) across
    /// the full repo root. Patterns support both wildcard-suffix form (`*.ratchet.json`)
    /// and literal filenames (`coverage-ratchet.json`).
    /// Pass isMacOSOverride to force a specific code path (useful for testing).
    /// `latencySeconds` is the macOS FSEvents coalescing window (ignored on
    /// non-macOS, where .NET FileSystemWatcher has no equivalent knob).
    let create
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (isMacOSOverride: bool option)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        : FileWatcher =
        let isMacOS =
            defaultArg
                isMacOSOverride
                (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX
                ))

        if isMacOS then
            createMacOS
                repoRoot
                onChange
                extraPatterns
                latencySeconds
                NativeStartRetry.defaults
                (fun dirs exclusions onFile onCoalesced latency ->
                    MacFsEvents.createExcluding dirs exclusions onFile onCoalesced latency :> IDisposable)
                defaultPollingWatcherFactory
        else
            createPortable repoRoot onChange extraPatterns defaultSystemWatcherFactory
