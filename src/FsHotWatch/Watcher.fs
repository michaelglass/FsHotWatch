module FsHotWatch.Watcher

open System
open System.IO
open System.Threading
open FsHotWatch.Events

/// Holds disposable watchers monitoring a repository for F# file changes.
[<NoComparison; NoEquality>]
type FileWatcher =
    { Disposables: IDisposable list }

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
    let syntheticPath (pattern: FilePattern) : string =
        match pattern with
        | FilePattern.Wildcard suffix -> "_fshw_rerun_" + suffix
        | FilePattern.Literal name -> name

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

/// One content-addressed view of the files a polling watcher is responsible for.
type internal PollingSnapshot =
    { Files: Map<string, string>
      UnreadableFiles: Set<string>
      Holes: Set<string> }

/// Assets live under obj, but bin can never contain a restore graph input.
let internal pollingAssetsExcludedDirs = SafeWalk.ToolingExcludedDirs |> Set.add "bin"

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
        walk pollingAssetsExcludedDirs "project.assets.json" root |> collect isProjectAssetsJson

    for extra in extraPatterns do
        let accept path = FilePattern.matches extra path && not (PathFilter.isGeneratedPath path)
        walk SafeWalk.SourceExcludedDirs (FilePattern.toString extra) repoRoot |> collect accept

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
            | :? UnauthorizedAccessException ->
                [], Set.singleton rootPath

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
                match Map.tryFind path previous with
                | Some prior when ContentHash.isReadable hash && String.Equals(prior, hash, StringComparison.Ordinal) ->
                    None
                | _ -> Some path)
            |> Set.ofSeq

        let deleted =
            previous
            |> Map.toSeq
            |> Seq.choose (fun (path, _) -> if Map.containsKey path current.Files then None else Some path)
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
                let systemTimer = new Timer(TimerCallback(fun _ -> onTick ()), null, Timeout.Infinite, Timeout.Infinite)

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
    type internal NativeStreamFactory =
        string list -> (string -> unit) -> (string -> unit) -> float -> IDisposable

    type internal SystemWatcherSpec =
        { Directory: string
          IncludeSubdirectories: bool
          Filters: string list }

    type internal SystemWatcherFactory = (string -> unit) -> SystemWatcherSpec -> IDisposable
    type internal PollingWatcherFactory = string -> (FileChangeKind -> unit) -> FilePattern list -> IDisposable

    let private defaultSystemWatcherFactory handle spec =
        let watcher = new FileSystemWatcher(spec.Directory)

        try
            watcher.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
            watcher.IncludeSubdirectories <- spec.IncludeSubdirectories

            for filter in spec.Filters do
                watcher.Filters.Add(filter)

            let handleEvent (event: FileSystemEventArgs) = handle event.FullPath
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

    let private createMacOS
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStreamFactory: NativeStreamFactory)
        (systemWatcherFactory: SystemWatcherFactory)
        (pollingWatcherFactory: PollingWatcherFactory)
        : FileWatcher =
        let handle path =
            if isRelevantFileOrExtra extraPatterns path then
                onChange (classifyChange path)

        let partial = ResizeArray<IDisposable>()

        let register disposable =
            partial.Add(disposable)
            disposable

        try
            let dirs = Discovery.existingDiscoveryRoots repoRoot

            if not dirs.IsEmpty then
                // SafeWalk, not SearchOption.AllDirectories: a coalesced native
                // event means Apple requires a recursive scan of that subtree.
                let onCoalesced dirPath =
                    if Directory.Exists(dirPath) then
                        for pattern in [| "*.fs"; "*.fsx"; "*.fsproj"; "*.props"; "project.assets.json" |] do
                            for file in SafeWalk.bestEffortFilePaths SafeWalk.ToolingExcludedDirs pattern dirPath do
                                if isRelevantFile file then
                                    onChange (classifyChange file)

                nativeStreamFactory dirs handle onCoalesced latencySeconds |> register |> ignore

            systemWatcherFactory
                handle
                { Directory = repoRoot
                  IncludeSubdirectories = false
                  Filters = [ "*.sln"; "*.slnx" ] }
            |> register
            |> ignore

            for pattern in extraPatterns do
                systemWatcherFactory
                    handle
                    { Directory = repoRoot
                      IncludeSubdirectories = true
                      Filters = [ FilePattern.toString pattern ] }
                |> register
                |> ignore

            { Disposables = partial |> Seq.toList }
        with ex ->
            for disposable in Seq.rev partial do
                try
                    disposable.Dispose()
                with disposeEx ->
                    Logging.warn "watcher" $"partial watcher disposal failed: %s{disposeEx.Message}"

            Logging.warn
                "watcher"
                $"macOS file-event setup failed (%s{ex.Message}); using a 1-second content-snapshot polling watcher"

            let polling = pollingWatcherFactory repoRoot onChange extraPatterns
            { Disposables = [ polling ] }

    /// macOS construction seam used by deterministic failure-path tests.
    let internal createWithNativeStream
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStreamFactory: NativeStreamFactory)
        =
        createMacOS
            repoRoot
            onChange
            extraPatterns
            latencySeconds
            nativeStreamFactory
            defaultSystemWatcherFactory
            defaultPollingWatcherFactory

    /// Complete setup seam used to prove native-first ordering and transactional rollback.
    let internal createWithFactories
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (extraPatterns: FilePattern list)
        (latencySeconds: float)
        (nativeStreamFactory: NativeStreamFactory)
        (systemWatcherFactory: SystemWatcherFactory)
        (pollingWatcherFactory: PollingWatcherFactory)
        =
        createMacOS
            repoRoot
            onChange
            extraPatterns
            latencySeconds
            nativeStreamFactory
            systemWatcherFactory
            pollingWatcherFactory

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
        let handle (path: string) =
            if isRelevantFileOrExtra extraPatterns path then
                onChange (classifyChange path)

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
                (fun dirs onFile onCoalesced latency ->
                    MacFsEvents.createWithCoalesced dirs onFile onCoalesced latency :> IDisposable)
                defaultSystemWatcherFactory
                defaultPollingWatcherFactory
        else
            let slnWatcher =
                defaultSystemWatcherFactory
                    handle
                    { Directory = repoRoot
                      IncludeSubdirectories = false
                      Filters = [ "*.sln"; "*.slnx" ] }

            // Each FileCommandPlugin pattern gets its own recursive watcher at the
            // repo root. .NET handles wildcard and literal filter forms.
            let extraWatchers =
                extraPatterns
                |> List.map (fun pattern ->
                    defaultSystemWatcherFactory
                        handle
                        { Directory = repoRoot
                          IncludeSubdirectories = true
                          Filters = [ FilePattern.toString pattern ] })

            let createFsw (dir: string) =
                if Directory.Exists(dir) then
                    Some(
                        defaultSystemWatcherFactory
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

            { Disposables = watchers @ extraWatchers }
