module FsHotWatch.ProjectGraph

open System.Collections.Concurrent
open System.IO
open System.Xml.Linq
open FsHotWatch.Events

/// Read-only view of the project graph for consumers that don't need mutation.
type IProjectGraphReader =
    abstract GetProjectForFile: filePath: AbsFilePath -> AbsProjectPath option
    abstract GetProjectsForFile: filePath: AbsFilePath -> AbsProjectPath list
    abstract GetSourceFiles: projectPath: AbsProjectPath -> AbsFilePath list
    abstract GetDependents: projectPath: AbsProjectPath -> AbsProjectPath list
    abstract GetAffectedProjects: changedFiles: AbsFilePath list -> AbsProjectPath list
    abstract GetAllProjects: unit -> AbsProjectPath list
    abstract GetAllFiles: unit -> AbsFilePath list

/// Tracks project structure: which files belong to which project,
/// which projects reference which, and dependency ordering.
type ProjectGraph() as this =
    let fileToProjects = ConcurrentDictionary<AbsFilePath, AbsProjectPath list>()
    let projectFiles = ConcurrentDictionary<AbsProjectPath, AbsFilePath list>()
    let projectReferences = ConcurrentDictionary<AbsProjectPath, AbsProjectPath list>()
    let projectDependents = ConcurrentDictionary<AbsProjectPath, AbsProjectPath list>()

    let appendIfAbsent (dict: ConcurrentDictionary<'K, 'V list>) (key: 'K) (value: 'V) =
        dict.AddOrUpdate(
            key,
            [ value ],
            fun _ existing ->
                if List.contains value existing then
                    existing
                else
                    value :: existing
        )
        |> ignore

    /// Clear all state so the next round of RegisterProject/RegisterFromFsproj calls
    /// rebuild from scratch. Call before re-discovery to remove deleted projects,
    /// removed files, and stale dependent relationships.
    member _.PrepareForRediscovery() =
        fileToProjects.Clear()
        projectFiles.Clear()
        projectReferences.Clear()
        projectDependents.Clear()

    /// Register a project with its source files and references.
    member _.RegisterProject
        (projectPath: AbsProjectPath, sourceFiles: AbsFilePath list, references: AbsProjectPath list)
        =
        projectFiles[projectPath] <- sourceFiles

        for file in sourceFiles do
            appendIfAbsent fileToProjects file projectPath

        projectReferences[projectPath] <- references

        for ref in references do
            appendIfAbsent projectDependents ref projectPath

    /// Parse a .fsproj file and register it. Returns (sourceFiles, projectReferences).
    member this.RegisterFromFsproj(fsprojPath: string) =
        let absPath = AbsProjectPath.create fsprojPath
        let absPathStr = AbsProjectPath.value absPath
        let doc = XDocument.Load(absPathStr)
        let projDir = Path.GetDirectoryName(absPathStr)

        let sourceFiles =
            doc.Descendants(XName.Get "Compile")
            |> Seq.choose (fun el ->
                let inc = el.Attribute(XName.Get "Include")

                if inc <> null then
                    Some(AbsFilePath.create (Path.Combine(projDir, inc.Value)))
                else
                    None)
            |> Seq.toList

        let references =
            doc.Descendants(XName.Get "ProjectReference")
            |> Seq.choose (fun el ->
                let inc = el.Attribute(XName.Get "Include")

                if inc <> null then
                    Some(AbsProjectPath.create (Path.Combine(projDir, inc.Value)))
                else
                    None)
            |> Seq.toList

        this.RegisterProject(absPath, sourceFiles, references)
        (sourceFiles, references)

    /// Get the project that owns a file, or None.
    /// For shared files, returns the first registered project.
    member _.GetProjectForFile(filePath: AbsFilePath) : AbsProjectPath option =
        match fileToProjects.TryGetValue(filePath) with
        | true, (first :: _) -> Some first
        | _ -> None

    /// Get all projects that contain the given file.
    member _.GetProjectsForFile(filePath: AbsFilePath) : AbsProjectPath list =
        match fileToProjects.TryGetValue(filePath) with
        | true, projects -> projects
        | false, _ -> []

    /// Get all source files for a project.
    member _.GetSourceFiles(projectPath: AbsProjectPath) : AbsFilePath list =
        match projectFiles.TryGetValue(projectPath) with
        | true, files -> files
        | false, _ -> []

    /// Get direct project references for a project.
    member _.GetReferences(projectPath: AbsProjectPath) : AbsProjectPath list =
        match projectReferences.TryGetValue(projectPath) with
        | true, refs -> refs
        | false, _ -> []

    /// Get projects that directly depend on the given project.
    member _.GetDependents(projectPath: AbsProjectPath) : AbsProjectPath list =
        match projectDependents.TryGetValue(projectPath) with
        | true, deps -> deps
        | false, _ -> []

    /// Get all projects transitively affected by a change in the given project.
    /// Returns in topological order (changed project first, then dependents).
    member this.GetTransitiveDependents(projectPath: AbsProjectPath) : AbsProjectPath list =
        let mutable visited = Set.empty
        let mutable result = []

        let rec walk proj =
            if not (visited |> Set.contains proj) then
                visited <- visited |> Set.add proj
                result <- proj :: result

                for dep in this.GetDependents(proj) do
                    walk dep

        walk projectPath
        result |> List.rev

    /// Get all projects affected by changes to the given files.
    /// Single DFS from all changed projects (efficient when multiple projects change).
    member this.GetAffectedProjects(changedFiles: AbsFilePath list) : AbsProjectPath list =
        let roots =
            changedFiles
            |> List.collect (fun f -> this.GetProjectsForFile(f))
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
    member _.GetAllProjects() : AbsProjectPath list = projectFiles.Keys |> Seq.toList

    /// Get all registered file paths across all projects.
    member _.GetAllFiles() : AbsFilePath list = fileToProjects.Keys |> Seq.toList

    /// Group projects into parallel tiers where each tier's projects
    /// have all dependencies satisfied by earlier tiers.
    member this.GetParallelTiers() : AbsProjectPath list list =
        let projects = this.GetAllProjects()
        let allProjects = projects |> Set.ofList

        let rec buildTiers remaining (sortedSet: Set<AbsProjectPath>) acc =
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
    member this.GetTopologicalOrder() : AbsProjectPath list =
        this.GetParallelTiers() |> List.collect id

    interface IProjectGraphReader with
        member _.GetProjectForFile(filePath) = this.GetProjectForFile(filePath)
        member _.GetProjectsForFile(filePath) = this.GetProjectsForFile(filePath)
        member _.GetSourceFiles(projectPath) = this.GetSourceFiles(projectPath)
        member _.GetDependents(projectPath) = this.GetDependents(projectPath)
        member _.GetAffectedProjects(changedFiles) = this.GetAffectedProjects(changedFiles)
        member _.GetAllProjects() = this.GetAllProjects()
        member _.GetAllFiles() = this.GetAllFiles()
