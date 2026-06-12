module FsHotWatch.Watcher

open System
open System.IO
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
/// `project.assets.json` is the documented exception: it lives in obj/ but is
/// the canonical post-`dotnet restore` signal that a project's package graph
/// changed (FR docs/fr-auto-refresh-fsproj-changes.md, 2026-05-25).
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
    /// anything else is treated as a literal filename. Embedded `*` in
    /// non-leading position is not a glob — it's part of the literal name.
    let parse (pattern: string) : FilePattern =
        if pattern.StartsWith("*") then
            FilePattern.Wildcard(pattern.Substring(1))
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

/// Functions for creating file watchers.
module FileWatcher =
    /// Create a FileWatcher that monitors src/ and tests/ for F#-relevant file changes,
    /// plus any files matching `extraPatterns` (from FileCommandPlugin patterns) across
    /// the full repo root. Patterns support both wildcard-suffix form (`*.ratchet.json`)
    /// and literal filenames (`coverage-ratchet.json`).
    /// Pass isMacOSOverride to force a specific code path (useful for testing).
    let create
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (isMacOSOverride: bool option)
        (extraPatterns: FilePattern list)
        : FileWatcher =
        let handle (path: string) =
            if isRelevantFileOrExtra extraPatterns path then
                onChange (classifyChange path)

        let handleFsw (e: FileSystemEventArgs) = handle e.FullPath

        // Construct a FileSystemWatcher with the shared LastWrite+FileName notify
        // filter and standard Changed/Created/Deleted/Renamed handlers. `configure`
        // sets per-watcher specifics (filters, recursion).
        let mkWatcher (dir: string) (configure: FileSystemWatcher -> unit) : IDisposable =
            let w = new FileSystemWatcher(dir)
            w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
            configure w
            w.Changed.Add(handleFsw)
            w.Created.Add(handleFsw)
            w.Deleted.Add(handleFsw)
            w.Renamed.Add(handleFsw)
            w.EnableRaisingEvents <- true
            w :> IDisposable

        let isMacOS =
            defaultArg
                isMacOSOverride
                (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX
                ))

        let slnWatcher =
            mkWatcher repoRoot (fun w ->
                w.Filters.Add("*.sln")
                w.Filters.Add("*.slnx"))

        // Each FileCommandPlugin pattern gets its own recursive watcher at the
        // repo root. The pattern is passed directly as the Filter glob — .NET
        // FileSystemWatcher handles both `*.ratchet.json` and literal filenames.
        let extraWatchers =
            extraPatterns
            |> List.map (fun pattern ->
                mkWatcher repoRoot (fun w ->
                    w.IncludeSubdirectories <- true
                    w.Filter <- FilePattern.toString pattern))

        if isMacOS then
            let dirs = Discovery.existingDiscoveryRoots repoRoot

            if dirs.IsEmpty then
                { Disposables = slnWatcher :: extraWatchers }
            else
                let onCoalesced (dirPath: string) =
                    try
                        if Directory.Exists(dirPath) then
                            for pattern in [| "*.fs"; "*.fsx"; "*.fsproj"; "*.props"; "project.assets.json" |] do
                                for file in Directory.EnumerateFiles(dirPath, pattern, SearchOption.AllDirectories) do
                                    if isRelevantFile file then
                                        onChange (classifyChange file)
                    with
                    | :? DirectoryNotFoundException -> ()
                    | :? UnauthorizedAccessException -> ()

                let stream = MacFsEvents.createWithCoalesced dirs handle onCoalesced
                { Disposables = (stream :> IDisposable) :: slnWatcher :: extraWatchers }
        else
            let createFsw (dir: string) =
                if Directory.Exists(dir) then
                    Some(
                        mkWatcher dir (fun w ->
                            w.IncludeSubdirectories <- true
                            w.Filters.Add("*.fs")
                            w.Filters.Add("*.fsx")
                            w.Filters.Add("*.fsproj")
                            w.Filters.Add("*.props")
                            w.Filters.Add("project.assets.json"))
                    )
                else
                    None

            let watchers =
                (Discovery.discoveryRoots repoRoot |> List.map createFsw) @ [ Some slnWatcher ]
                |> List.choose id

            { Disposables = watchers @ extraWatchers }
