module FsHotWatch.Watcher

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open FsHotWatch.Events

/// Track file content hashes to skip watcher events where mtime changed but content didn't.
let private fileHashes = ConcurrentDictionary<string, byte[]>()

/// Returns true if the file content actually changed since last check.
/// Updates the stored hash on change. Returns true for new/deleted files.
let internal hasContentChanged (path: string) =
    try
        if not (File.Exists(path)) then
            fileHashes.TryRemove(path) |> ignore
            true
        else
            let content = File.ReadAllBytes(path)
            let hash = SHA256.HashData(content)

            match fileHashes.TryGetValue(path) with
            | true, previous when ReadOnlySpan(previous).SequenceEqual(ReadOnlySpan(hash)) -> false
            | _ ->
                fileHashes[path] <- hash
                true
    with
    | :? IOException -> true
    | :? UnauthorizedAccessException -> true

/// Holds disposable watchers monitoring a repository for F# file changes.
[<NoComparison; NoEquality>]
type FileWatcher =
    { Disposables: IDisposable list }

    interface IDisposable with
        member this.Dispose() =
            for d in this.Disposables do
                d.Dispose()

/// Returns true if the file path has a relevant extension and is not in obj/ or bin/.
let internal isRelevantFile (path: string) =
    let ext = Path.GetExtension(path).ToLowerInvariant()

    let isRelevantExt =
        ext = ".fs"
        || ext = ".fsx"
        || ext = ".fsproj"
        || ext = ".sln"
        || ext = ".slnx"
        || ext = ".props"

    isRelevantExt && not (PathFilter.isGeneratedPath path)

/// Like `isRelevantFile`, but also accepts files whose path ends with any
/// of the provided suffixes. Used by FileCommandPlugin patterns that match
/// non-source files (e.g. `.ratchet.json` config files).
let internal isRelevantFileOrExtra (extraSuffixes: string list) (path: string) =
    if isRelevantFile path then
        true
    else
        let matchesExtra =
            extraSuffixes
            |> List.exists (fun s -> path.EndsWith(s, StringComparison.OrdinalIgnoreCase))

        matchesExtra && not (PathFilter.isGeneratedPath path)

/// Classify a file path as a solution, project, or source change.
let internal classifyChange (path: string) =
    let ext = Path.GetExtension(path).ToLowerInvariant()

    if ext = ".sln" || ext = ".slnx" then
        SolutionChanged path
    elif ext = ".fsproj" || ext = ".props" then
        ProjectChanged [ path ]
    else
        SourceChanged [ path ]

/// Functions for creating file watchers.
module FileWatcher =
    /// Create a FileWatcher that monitors src/ and tests/ for F#-relevant file changes,
    /// plus any files matching `extraSuffixes` (from FileCommandPlugin patterns) across
    /// the full repo root.
    /// Pass isMacOSOverride to force a specific code path (useful for testing).
    let create
        (repoRoot: string)
        (onChange: FileChangeKind -> unit)
        (isMacOSOverride: bool option)
        (extraSuffixes: string list)
        : FileWatcher =
        let handle (path: string) =
            if isRelevantFileOrExtra extraSuffixes path then
                onChange (classifyChange path)

        let handleFsw (e: FileSystemEventArgs) = handle e.FullPath

        let isMacOS =
            defaultArg
                isMacOSOverride
                (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX
                ))

        // Solution file watcher: always FileSystemWatcher (single dir, non-recursive)
        let slnWatcher =
            let w = new FileSystemWatcher(repoRoot)
            w.Filters.Add("*.sln")
            w.Filters.Add("*.slnx")
            w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
            w.Changed.Add(handleFsw)
            w.Created.Add(handleFsw)
            w.Deleted.Add(handleFsw)
            w.Renamed.Add(handleFsw)
            w.EnableRaisingEvents <- true
            w :> IDisposable

        // Non-source patterns from FileCommandPlugins (e.g. *.ratchet.json). One
        // recursive FileSystemWatcher per suffix covering the whole repo. Goes
        // through `handle` so the shared `isRelevantFileOrExtra` + obj/bin
        // exclusion logic is applied.
        let extraWatchers =
            extraSuffixes
            |> List.map (fun suffix ->
                let w = new FileSystemWatcher(repoRoot)
                w.IncludeSubdirectories <- true
                w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
                w.Filter <- $"*%s{suffix}"
                w.Changed.Add(handleFsw)
                w.Created.Add(handleFsw)
                w.Deleted.Add(handleFsw)
                w.Renamed.Add(handleFsw)
                w.EnableRaisingEvents <- true
                w :> IDisposable)

        if isMacOS then
            let dirs =
                [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
                |> List.filter Directory.Exists

            if dirs.IsEmpty then
                { Disposables = slnWatcher :: extraWatchers }
            else
                let onCoalesced (dirPath: string) =
                    try
                        if Directory.Exists(dirPath) then
                            for pattern in [| "*.fs"; "*.fsx"; "*.fsproj"; "*.props" |] do
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
                    let w = new FileSystemWatcher(dir)
                    w.IncludeSubdirectories <- true
                    w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName
                    w.Filters.Add("*.fs")
                    w.Filters.Add("*.fsx")
                    w.Filters.Add("*.fsproj")
                    w.Filters.Add("*.props")
                    w.Changed.Add(handleFsw)
                    w.Created.Add(handleFsw)
                    w.Deleted.Add(handleFsw)
                    w.Renamed.Add(handleFsw)
                    w.EnableRaisingEvents <- true
                    Some(w :> IDisposable)
                else
                    None

            let watchers =
                [ createFsw (Path.Combine(repoRoot, "src"))
                  createFsw (Path.Combine(repoRoot, "tests"))
                  Some slnWatcher ]
                |> List.choose id

            { Disposables = watchers @ extraWatchers }
