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

    /// Register a project with its source files and references.
    member _.RegisterProject(projectPath: string, sourceFiles: string list, references: string list) =
        let absProject = Path.GetFullPath(projectPath)
        projectFiles[absProject] <- sourceFiles

        for file in sourceFiles do
            fileToProject[Path.GetFullPath(file)] <- absProject

        let absRefs = references |> List.map Path.GetFullPath
        projectReferences[absProject] <- absRefs

        // Update reverse index (dependents)
        for ref in absRefs do
            let existing =
                match projectDependents.TryGetValue(ref) with
                | true, deps -> deps
                | false, _ -> []

            if not (existing |> List.contains absProject) then
                projectDependents[ref] <- absProject :: existing

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
    /// Returns in topological order.
    member this.GetAffectedProjects(changedFiles: string list) : string list =
        let changedProjects =
            changedFiles
            |> List.choose (fun f -> this.GetProjectForFile(f))
            |> List.distinct

        changedProjects
        |> List.collect (fun p -> this.GetTransitiveDependents(p))
        |> List.distinct

    /// Get all registered projects.
    member _.GetAllProjects() : string list =
        projectFiles.Keys |> Seq.toList

    /// Topological sort of all registered projects (dependencies before dependents).
    member this.GetTopologicalOrder() : string list =
        let allProjects = this.GetAllProjects() |> Set.ofList

        let rec topoSort remaining sorted (sortedSet: Set<string>) =
            if remaining |> List.isEmpty then
                sorted |> List.rev
            else
                let ready, blocked =
                    remaining
                    |> List.partition (fun proj ->
                        this.GetReferences(proj)
                        |> List.filter allProjects.Contains
                        |> List.forall sortedSet.Contains)

                if ready.IsEmpty then
                    List.rev sorted @ remaining
                else
                    let newSet =
                        ready |> List.fold (fun s p -> Set.add p s) sortedSet

                    topoSort blocked (ready @ sorted) newSet

        topoSort (this.GetAllProjects()) [] Set.empty
