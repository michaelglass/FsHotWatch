module FsHotWatch.ProjectDirtyTracker

open System.Collections.Concurrent

/// Thread-safe per-project dirty set. Keys are project name stems
/// (Path.GetFileNameWithoutExtension of the .fsproj path), e.g. "FsHotWatch.Tests".
type ProjectDirtyTracker() =
    let dirty =
        ConcurrentDictionary<string, unit>(System.StringComparer.OrdinalIgnoreCase)

    member _.MarkDirty(projects: string list) =
        for p in projects do
            dirty.[p] <- ()

    member _.ClearDirty(project: string) = dirty.TryRemove(project) |> ignore

    member _.IsDirty(project: string) = dirty.ContainsKey(project)

    member _.AllDirty = dirty.Keys |> Seq.toList
