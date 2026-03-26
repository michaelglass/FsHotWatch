module FsHotWatch.Watcher

open System
open System.IO
open FsHotWatch.Events

/// Holds a set of FileSystemWatchers monitoring a repository for F# file changes.
[<NoComparison; NoEquality>]
type FileWatcher =
    { Watchers: FileSystemWatcher list }

    interface IDisposable with
        member this.Dispose() =
            for w in this.Watchers do
                w.Dispose()

/// Returns true if the file path has a relevant extension and is not in obj/ or bin/.
let internal isRelevantFile (path: string) =
    let normalized = path.Replace('\\', '/')
    let ext = Path.GetExtension(path).ToLowerInvariant()
    let fileName = Path.GetFileName(path)

    // Project infrastructure files (allowed even in obj/)
    if fileName = "project.assets.json" || ext = ".props" then
        true
    else
        let isRelevantExt =
            ext = ".fs" || ext = ".fsx" || ext = ".fsproj" || ext = ".sln" || ext = ".slnx"

        let isExcluded = normalized.Contains("/obj/") || normalized.Contains("/bin/")

        isRelevantExt && not isExcluded

/// Classify a file path as a solution, project, or source change.
let internal classifyChange (path: string) =
    let ext = Path.GetExtension(path).ToLowerInvariant()
    let fileName = Path.GetFileName(path)

    if ext = ".sln" || ext = ".slnx" then
        SolutionChanged
    elif ext = ".fsproj" || ext = ".props" || fileName = "project.assets.json" then
        ProjectChanged [ path ]
    else
        SourceChanged [ path ]

/// Functions for creating file watchers.
module FileWatcher =
    /// Create a FileWatcher that monitors src/ and tests/ for F#-relevant file changes.
    let create (repoRoot: string) (onChange: FileChangeKind -> unit) : FileWatcher =
        let createWatcher (dir: string) =
            if Directory.Exists(dir) then
                let w = new FileSystemWatcher(dir)
                w.IncludeSubdirectories <- true
                w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName

                w.Filters.Add("*.fs")
                w.Filters.Add("*.fsx")
                w.Filters.Add("*.fsproj")
                w.Filters.Add("*.props")
                w.Filters.Add("project.assets.json")

                let handle (e: FileSystemEventArgs) =
                    if isRelevantFile e.FullPath then
                        onChange (classifyChange e.FullPath)

                w.Changed.Add(handle)
                w.Created.Add(handle)
                w.Deleted.Add(handle)
                w.Renamed.Add(fun e -> handle e)
                w.EnableRaisingEvents <- true
                Some w
            else
                None

        let slnWatcher =
            let w = new FileSystemWatcher(repoRoot)
            w.Filters.Add("*.sln")
            w.Filters.Add("*.slnx")
            w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName

            let handle (e: FileSystemEventArgs) =
                if isRelevantFile e.FullPath then
                    onChange (classifyChange e.FullPath)

            w.Changed.Add(handle)
            w.Created.Add(handle)
            w.EnableRaisingEvents <- true
            Some w

        let watchers =
            [ createWatcher (Path.Combine(repoRoot, "src"))
              createWatcher (Path.Combine(repoRoot, "tests"))
              slnWatcher ]
            |> List.choose id

        { Watchers = watchers }
