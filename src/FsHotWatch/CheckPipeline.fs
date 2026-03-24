module FsHotWatch.CheckPipeline

open System.Collections.Concurrent
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.Events

/// Manages project options and performs incremental file checking with the warm FSharpChecker.
type CheckPipeline(checker: FSharpChecker) =
    let projectOptionsByFile = ConcurrentDictionary<string, FSharpProjectOptions>()
    let projectOptionsByProject = ConcurrentDictionary<string, FSharpProjectOptions>()

    /// Register project options for a project. Maps each source file to this project's options.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        projectOptionsByProject[projectPath] <- options

        for sourceFile in options.SourceFiles do
            projectOptionsByFile[sourceFile] <- options

    /// Check a single file using the warm checker. Returns FileCheckResult if successful.
    member _.CheckFile(filePath: string) : Async<FileCheckResult option> =
        async {
            let absPath = Path.GetFullPath(filePath)

            match projectOptionsByFile.TryGetValue(absPath) with
            | false, _ -> return None
            | true, options ->
                let source =
                    if File.Exists(absPath) then
                        File.ReadAllText(absPath)
                    else
                        ""

                let sourceText = SourceText.ofString source

                let! parseResults, checkAnswer =
                    checker.ParseAndCheckFileInProject(absPath, 0, sourceText, options)

                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded checkResults ->
                    return
                        Some
                            { File = absPath
                              Source = source
                              ParseResults = parseResults
                              CheckResults = checkResults }
                | FSharpCheckFileAnswer.Aborted -> return None
        }

    /// Check all registered files for a project. Returns results keyed by file path.
    member this.CheckProject(projectPath: string) : Async<ProjectCheckResult option> =
        async {
            match projectOptionsByProject.TryGetValue(projectPath) with
            | false, _ -> return None
            | true, options ->
                let mutable fileResults = Map.empty

                for sourceFile in options.SourceFiles do
                    let! result = this.CheckFile(sourceFile)

                    match result with
                    | Some r -> fileResults <- fileResults |> Map.add sourceFile r
                    | None -> ()

                return
                    Some
                        { Project = projectPath
                          FileResults = fileResults }
        }
