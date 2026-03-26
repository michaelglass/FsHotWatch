module FsHotWatch.ProjectGraph

open System.Collections.Concurrent
open System.IO
open System.Xml.Linq

/// Tracks project structure: which files belong to which project,
/// which projects reference which, and dependency ordering.
type ProjectGraph() =
    let fileToProject = ConcurrentDictionary<string, string>()
    let projectFiles = ConcurrentDictionary<string, string list>()
    let projectReferences = ConcurrentDictionary<string, string list>()
    let projectDependents = ConcurrentDictionary<string, string list>()

    /// Clear all state so the next round of RegisterProject/RegisterFromFsproj calls
    /// rebuild from scratch. Call before re-discovery to remove deleted projects,
    /// removed files, and stale dependent relationships.
    member _.PrepareForRediscovery() =
        fileToProject.Clear()
        projectFiles.Clear()
        projectReferences.Clear()
        projectDependents.Clear()

    /// Register a project with its source files and references.
    member _.RegisterProject(projectPath: string, sourceFiles: string list, references: string list) =
        let absProject = Path.GetFullPath(projectPath)
        projectFiles[absProject] <- sourceFiles

        for file in sourceFiles do
            fileToProject[Path.GetFullPath(file)] <- absProject

        let absRefs = references |> List.map Path.GetFullPath
        projectReferences[absProject] <- absRefs

        for ref in absRefs do
            projectDependents.AddOrUpdate(
                ref,
                [ absProject ],
                fun _ existing ->
                    if List.contains absProject existing then
                        existing
                    else
                        absProject :: existing
            )
            |> ignore

    /// Parse a .fsproj file and register it. Returns (sourceFiles, projectReferences).
    member this.RegisterFromFsproj(fsprojPath: string) =
        let absPath = Path.GetFullPath(fsprojPath)
        let doc = XDocument.Load(absPath)
        let projDir = Path.GetDirectoryName(absPath)

        let sourceFiles =
            doc.Descendants(XName.Get "Compile")
            |> Seq.choose (fun el ->
                let inc = el.Attribute(XName.Get "Include")

                if inc <> null then
                    Some(Path.GetFullPath(Path.Combine(projDir, inc.Value)))
                else
                    None)
            |> Seq.toList

        let references =
            doc.Descendants(XName.Get "ProjectReference")
            |> Seq.choose (fun el ->
                let inc = el.Attribute(XName.Get "Include")

                if inc <> null then
                    Some(Path.GetFullPath(Path.Combine(projDir, inc.Value)))
                else
                    None)
            |> Seq.toList

        this.RegisterProject(absPath, sourceFiles, references)
        (sourceFiles, references)

    /// Get the project that owns a file, or None.
    member _.GetProjectForFile(filePath: string) : string option =
        match fileToProject.TryGetValue(Path.GetFullPath(filePath)) with
        | true, proj -> Some proj
        | false, _ -> None

    /// Get all source files for a project.
    member _.GetSourceFiles(projectPath: string) : string list =
        match projectFiles.TryGetValue(Path.GetFullPath(projectPath)) with
        | true, files -> files
        | false, _ -> []

    /// Get direct project references for a project.
    member _.GetReferences(projectPath: string) : string list =
        match projectReferences.TryGetValue(Path.GetFullPath(projectPath)) with
        | true, refs -> refs
        | false, _ -> []

    /// Get projects that directly depend on the given project.
    member _.GetDependents(projectPath: string) : string list =
        match projectDependents.TryGetValue(Path.GetFullPath(projectPath)) with
        | true, deps -> deps
        | false, _ -> []

    /// Get all projects transitively affected by a change in the given project.
    /// Returns in topological order (changed project first, then dependents).
    member this.GetTransitiveDependents(projectPath: string) : string list =
        let absPath = Path.GetFullPath(projectPath)
        let mutable visited = Set.empty
        let mutable result = []

        let rec walk proj =
            if not (visited |> Set.contains proj) then
                visited <- visited |> Set.add proj
                result <- proj :: result

                for dep in this.GetDependents(proj) do
                    walk dep

        walk absPath
        result |> List.rev

    /// Get all projects affected by changes to the given files.
    /// Single DFS from all changed projects (efficient when multiple projects change).
    member this.GetAffectedProjects(changedFiles: string list) : string list =
        let roots =
            changedFiles
            |> List.choose (fun f -> this.GetProjectForFile(f))
            |> List.distinct

        let mutable visited = Set.empty
        let mutable result = []

        let rec walk proj =
            if not (Set.contains proj visited) then
                visited <- Set.add proj visited
                result <- proj :: result

                for dep in this.GetDependents(proj) do
                    walk dep

        for root in roots do
            walk root

        result |> List.rev

    /// Get all registered projects.
    member _.GetAllProjects() : string list = projectFiles.Keys |> Seq.toList

    /// Group projects into parallel tiers where each tier's projects
    /// have all dependencies satisfied by earlier tiers.
    member this.GetParallelTiers() : string list list =
        let projects = this.GetAllProjects()
        let allProjects = projects |> Set.ofList

        let rec buildTiers remaining (sortedSet: Set<string>) acc =
            if remaining |> List.isEmpty then
                acc |> List.rev
            else
                let ready, blocked =
                    remaining
                    |> List.partition (fun proj ->
                        this.GetReferences(proj)
                        |> List.filter allProjects.Contains
                        |> List.forall sortedSet.Contains)

                if ready.IsEmpty then
                    List.rev (remaining :: acc)
                else
                    let newSet = ready |> List.fold (fun s p -> Set.add p s) sortedSet
                    buildTiers blocked newSet (ready :: acc)

        buildTiers projects Set.empty []

    /// Topological sort of all registered projects (dependencies before dependents).
    member this.GetTopologicalOrder() : string list =
        this.GetParallelTiers() |> List.collect id
