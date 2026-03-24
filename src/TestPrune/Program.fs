module TestPrune.Program

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DiffParser
open TestPrune.ImpactAnalysis
open TestPrune.DeadCode
open TestPrune.ProjectLoader
open TestPrune.TestRunner

type Command =
    | Index
    | Run
    | Status
    | DeadCodeCmd of entryPatterns: string list
    | Help

let defaultEntryPatterns =
    [ "*.main"; "*.Program.*"; "*.Routes.*"; "*.Scheduler.*" ]

let rec private parseEntryFlags (args: string list) (acc: string list) =
    match args with
    | "--entry" :: pattern :: rest -> parseEntryFlags rest (pattern :: acc)
    | [] -> Ok(acc |> List.rev)
    | unknown :: _ -> Error $"Unknown flag: %s{unknown}"

type ParsedCommand = { Command: Command; RepoRoot: string option }

let rec private parseGlobalFlags (args: string list) (repoRoot: string option) : Result<string list * string option, string> =
    match args with
    | "--repo" :: path :: rest -> parseGlobalFlags rest (Some path)
    | _ -> Ok(args, repoRoot)

let parseArgs (args: string array) : Result<ParsedCommand, string> =
    match parseGlobalFlags (args |> Array.toList) None with
    | Error msg -> Error msg
    | Ok(commandArgs, repoRoot) ->
        let cmdResult =
            match commandArgs with
            | [] -> Ok Help
            | [ "index" ] -> Ok Index
            | [ "run" ] -> Ok Run
            | [ "status" ] -> Ok Status
            | "dead-code" :: rest ->
                match parseEntryFlags rest [] with
                | Ok [] -> Ok(DeadCodeCmd defaultEntryPatterns)
                | Ok patterns -> Ok(DeadCodeCmd patterns)
                | Error msg -> Error msg
            | [ "help" ]
            | [ "--help" ]
            | [ "-h" ] -> Ok Help
            | unknown :: _ -> Error $"Unknown command: %s{unknown}"

        cmdResult |> Result.map (fun cmd -> { Command = cmd; RepoRoot = repoRoot })

let showHelp () =
    printfn "TestPrune - Test impact analysis tool"
    printfn ""
    printfn "Usage: test-prune [--repo <path>] <command>"
    printfn ""
    printfn "Global options:"
    printfn "  --repo <path>  Use <path> as the repo root (default: auto-detect from cwd)"
    printfn ""
    printfn "Commands:"
    printfn "  index      Build the dependency graph from source"
    printfn "  run        Run affected tests based on changes"
    printfn "  status     Show what tests would run (dry-run)"
    printfn "  dead-code  Detect unreachable symbols from entry points"
    printfn "  help       Show this help message"
    printfn ""
    printfn "dead-code options:"
    printfn "  --entry <pattern>  Add entry point pattern (repeatable)"
    printfn "                     Default: *.main, *.Program.*, *.Routes.*, *.Scheduler.*"

/// Walk up from startDir looking for .jj or .git directory.
let findRepoRoot (startDir: string) : string option =
    let rec walk (dir: string) =
        if
            Directory.Exists(Path.Combine(dir, ".jj"))
            || Directory.Exists(Path.Combine(dir, ".git"))
        then
            Some dir
        else
            let parent = Directory.GetParent(dir)

            if isNull parent then None else walk parent.FullName

    walk startDir

/// Find all .fs files in src/ and tests/ directories, excluding obj/ and bin/.
let findSourceFiles (repoRoot: string) : string list =
    let searchDirs = [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]

    searchDirs
    |> List.filter Directory.Exists
    |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fs", SearchOption.AllDirectories) |> Array.toList)
    |> List.filter (fun path ->
        let normalized = path.Replace('\\', '/')

        not (normalized.Contains("/obj/", StringComparison.Ordinal))
        && not (normalized.Contains("/bin/", StringComparison.Ordinal)))
    |> List.sort

/// Parse a single .fs file with FCS, returning analysis result or error message.
let parseFile (checker: FSharpChecker) (filePath: string) : Result<AnalysisResult, string> =
    try
        let source = File.ReadAllText(filePath)
        let fileName = Path.GetFullPath(filePath)

        let projOptions = getScriptOptions checker fileName source |> Async.RunSynchronously

        analyzeSource checker fileName source projOptions |> Async.RunSynchronously
    with ex ->
        Error $"Exception reading %s{filePath}: %s{ex.Message}"

/// Discover all .fsproj files in src/ and tests/ directories.
let findProjectFiles (repoRoot: string) : string list =
    [ "src"; "tests" ]
    |> List.collect (fun dir ->
        let fullDir = Path.Combine(repoRoot, dir)

        if Directory.Exists(fullDir) then
            Directory.GetFiles(fullDir, "*.fsproj", SearchOption.AllDirectories)
            |> Array.toList
        else
            [])
    |> List.filter (fun p ->
        let n = p.Replace('\\', '/')

        not (n.Contains("/obj/", StringComparison.Ordinal))
        && not (n.Contains("/bin/", StringComparison.Ordinal)))

/// Builds the solution. Returns exit code (0 = success).
type BuildRunner = string -> int

/// Gets FSharpProjectOptions for a project file.
type ProjectOptionsProvider = FSharpChecker -> string -> FSharpProjectOptions

/// Compute a cache key for a single source file based on path, size, and modification time.
let computeFileKey (filePath: string) : string =
    let info = FileInfo(filePath)
    let mtime = info.LastWriteTimeUtc.ToString("o")
    $"%s{filePath}:%d{info.Length}:%s{mtime}"

/// Compute a fingerprint for a project's source files based on paths, sizes, and modification times.
let computeProjectHash (sourceFiles: string list) : string =
    let entries =
        sourceFiles
        |> List.filter File.Exists
        |> List.sort
        |> List.map (fun path ->
            let info = FileInfo(path)
            let mtime = info.LastWriteTimeUtc.ToString("o")
            $"%s{path}:%d{info.Length}:%s{mtime}")

    let combined = String.concat "\n" entries
    let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined))
    Convert.ToHexStringLower(bytes)

/// Create an FSharpChecker configured for TestPrune indexing.
/// Callers can reuse a single instance across multiple index runs to benefit
/// from FCS's internal IncrementalBuilder caches.
let createChecker () =
    FSharpChecker.Create(
        projectCacheSize = 200,
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        parallelReferenceResolution = true
    )

/// Run the index command with injectable build runner and project options provider.
let runIndexWith
    (buildRunner: BuildRunner)
    (getOptions: ProjectOptionsProvider)
    (repoRoot: string)
    (checker: FSharpChecker)
    : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")
    let db = Database.create dbPath

    let projectFiles = findProjectFiles repoRoot
    eprintfn $"Found %d{projectFiles.Length} projects"

    eprintfn "Building projects..."
    let buildExitCode = buildRunner repoRoot

    if buildExitCode <> 0 then
        eprintfn "Build failed — cannot index"
        1
    else
        let mutable totalSymbols = 0
        let mutable totalDeps = 0
        let mutable totalTests = 0

        let mutable skippedProjects = 0
        let mutable skippedFiles = 0

        let projectInfos =
            projectFiles
            |> List.choose (fun fsprojPath ->
                try
                    let fullPath = Path.GetFullPath(fsprojPath)
                    let compileFiles, projectRefs = parseProjectFile fsprojPath
                    Some(fullPath, compileFiles, projectRefs)
                with ex ->
                    eprintfn $"  Error parsing %s{fsprojPath}: %s{ex.Message}"
                    None)

        let reindexedProjects = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

        let projectPathSet = projectInfos |> List.map (fun (p, _, _) -> p) |> Set.ofList

        let rec topoLevels remaining (processedSet: Set<string>) =
            if remaining |> List.isEmpty then
                []
            else
                let ready, blocked =
                    remaining
                    |> List.partition (fun (_, _, refs) ->
                        refs
                        |> List.filter projectPathSet.Contains
                        |> List.forall processedSet.Contains)

                if ready.IsEmpty then
                    [ remaining ]
                else
                    let newSet =
                        ready |> List.fold (fun s (p, _, _) -> Set.add p s) processedSet

                    ready :: topoLevels blocked newSet

        let levels = topoLevels projectInfos Set.empty

        let indexProject (fsprojPath: string, compileFiles, projectRefs) =
            let projName = Path.GetFileNameWithoutExtension(fsprojPath)

            try
                let hash = computeProjectHash compileFiles

                let depReindexed =
                    projectRefs |> List.exists (fun r -> reindexedProjects.ContainsKey(r))

                match db.GetProjectKey(projName) with
                | Some stored when stored = hash && not depReindexed ->
                    Threading.Interlocked.Increment(&skippedProjects) |> ignore
                    eprintfn $"  %s{projName}: unchanged, skipping"
                | _ ->
                    let projOptions = lazy (getOptions checker fsprojPath)
                    let mutable analyzedFiles = 0
                    let mutable firstChangedIndex = None

                    let results =
                        compileFiles
                        |> List.mapi (fun idx sourceFile ->
                            if not (File.Exists(sourceFile)) then
                                None
                            else
                                let relPath =
                                    Path.GetRelativePath(repoRoot, sourceFile).Replace('\\', '/')

                                let fileKey = computeFileKey sourceFile

                                let forcedByCompilationOrder =
                                    match firstChangedIndex with
                                    | Some firstIdx when idx > firstIdx -> true
                                    | _ -> false

                                let cached =
                                    not forcedByCompilationOrder
                                    && match db.GetFileKey(relPath) with
                                       | Some stored when stored = fileKey -> true
                                       | _ -> false

                                if cached then
                                    Threading.Interlocked.Increment(&skippedFiles) |> ignore

                                    Some
                                        {| Symbols = db.GetSymbolsInFile(relPath)
                                           Dependencies = db.GetDependenciesFromFile(relPath)
                                           TestMethods = db.GetTestMethodsInFile(relPath) |}
                                else
                                    let source = File.ReadAllText(sourceFile)

                                    match
                                        analyzeSource checker sourceFile source (projOptions.Force())
                                        |> Async.RunSynchronously
                                    with
                                    | Ok result ->
                                        analyzedFiles <- analyzedFiles + 1

                                        if firstChangedIndex.IsNone then
                                            firstChangedIndex <- Some idx

                                        db.SetFileKey(relPath, fileKey)

                                        Some
                                            {| Symbols = normalizeSymbolPaths repoRoot result.Symbols
                                               Dependencies = result.Dependencies
                                               TestMethods =
                                                result.TestMethods
                                                |> List.map (fun t -> { t with TestProject = projName }) |}
                                    | Error msg ->
                                        eprintfn $"  Warning: %s{sourceFile}: %s{msg}"
                                        None)
                        |> List.choose id

                    let combined =
                        { Symbols = results |> List.collect (fun r -> r.Symbols)
                          Dependencies = results |> List.collect (fun r -> r.Dependencies)
                          TestMethods = results |> List.collect (fun r -> r.TestMethods) }

                    if analyzedFiles > 0 then
                        db.RebuildForProject(projName, combined)

                    db.SetProjectKey(projName, hash)
                    reindexedProjects.TryAdd(fsprojPath, true) |> ignore
                    Threading.Interlocked.Add(&totalSymbols, combined.Symbols.Length) |> ignore
                    Threading.Interlocked.Add(&totalDeps, combined.Dependencies.Length) |> ignore
                    Threading.Interlocked.Add(&totalTests, combined.TestMethods.Length) |> ignore

                    let fileCount = compileFiles.Length

                    eprintfn
                        $"  %s{projName}: %d{combined.Symbols.Length} symbols, %d{combined.Dependencies.Length} deps, %d{combined.TestMethods.Length} tests (%d{analyzedFiles}/%d{fileCount} files analyzed)"
            with ex ->
                eprintfn $"  Error processing %s{projName}: %s{ex.Message}"

        for level in levels do
            if level.Length = 1 then
                indexProject level.Head
            else
                level
                |> List.map (fun proj -> async { indexProject proj })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> ignore

        eprintfn $"Indexed %d{totalSymbols} symbols, %d{totalDeps} dependencies, %d{totalTests} test methods"

        if skippedProjects > 0 then
            eprintfn $"Skipped %d{skippedProjects} unchanged project(s)"

        if skippedFiles > 0 then
            eprintfn $"Skipped %d{skippedFiles} unchanged file(s)"

        0

/// Default build runner: runs `dotnet build` on the solution.
let dotnetBuildRunner: BuildRunner =
    fun (repoRoot: string) ->
        let slnFiles =
            [| "*.slnx"; "*.sln" |]
            |> Array.collect (fun pattern -> Directory.GetFiles(repoRoot, pattern))

        let slnPath = if slnFiles.Length > 0 then slnFiles.[0] else repoRoot

        let buildPsi = ProcessStartInfo("dotnet", $"build \"%s{slnPath}\" -v quiet")
        buildPsi.UseShellExecute <- false
        buildPsi.RedirectStandardOutput <- true
        buildPsi.RedirectStandardError <- true
        use buildProc = Process.Start(buildPsi)
        buildProc.StandardOutput.ReadToEnd() |> ignore
        buildProc.StandardError.ReadToEnd() |> ignore
        buildProc.WaitForExit()
        buildProc.ExitCode

/// Run the index command: build projects, then parse with real project options.
let runIndex (repoRoot: string) : int =
    let checker = createChecker ()
    runIndexWith dotnetBuildRunner getProjectOptions repoRoot checker

type DiffProvider = unit -> Result<string, string>

/// Get jj diff output.
let jjDiffProvider: DiffProvider =
    fun () ->
        try
            let psi = ProcessStartInfo("jj", "diff --git")
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            use proc = Process.Start(psi)
            let output = proc.StandardOutput.ReadToEnd()
            let _stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode = 0 then
                Ok output
            else
                Error "jj diff failed — is this a jj repository?"
        with ex ->
            Error $"Failed to run jj: %s{ex.Message}"

/// Determine test selection from current jj diff.
let analyzeChanges
    (getDiff: DiffProvider)
    (repoRoot: string)
    (db: Database)
    (checker: FSharpChecker)
    : Result<TestSelection * string list, string> =
    match getDiff () with
    | Error msg -> Error msg
    | Ok diffText ->
        let changedFiles = parseChangedFiles diffText

        if changedFiles.IsEmpty then
            Ok(RunSubset [], [])
        else
            let mutable parseFailures = []

            let currentSymbolsByFile =
                changedFiles
                |> List.filter (fun f ->
                    f.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
                    && not (f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
                |> List.choose (fun relPath ->
                    let fullPath = Path.Combine(repoRoot, relPath)

                    if File.Exists(fullPath) then
                        match parseFile checker fullPath with
                        | Ok result -> Some(relPath, normalizeSymbolPaths repoRoot result.Symbols)
                        | Error msg ->
                            eprintfn $"  Warning: could not parse %s{relPath}: %s{msg}"
                            parseFailures <- relPath :: parseFailures
                            None
                    else
                        None)
                |> Map.ofList

            if not parseFailures.IsEmpty then
                let failedFiles = parseFailures |> List.rev |> String.concat ", "
                Ok(RunAll $"could not parse: %s{failedFiles}", changedFiles)
            else
                let selection = selectTests db changedFiles currentSymbolsByFile
                Ok(selection, changedFiles)

/// Run the status command with an injectable diff provider.
let runStatusWith (getDiff: DiffProvider) (repoRoot: string) : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if not (File.Exists(dbPath)) then
        eprintfn "No index found. Run 'test-prune index' first."
        1
    else
        let db = Database.create dbPath
        let checker = FSharpChecker.Create()

        match analyzeChanges getDiff repoRoot db checker with
        | Error msg ->
            eprintfn $"Error: %s{msg}"
            1
        | Ok(selection, changedFiles) ->
            printfn $"Changed files: %d{changedFiles.Length}"

            for f in changedFiles do
                printfn $"  %s{f}"

            match selection with
            | RunSubset [] ->
                printfn "No tests affected."
                0
            | RunSubset tests ->
                printfn $"Would run %d{tests.Length} test(s):"

                let byProject = tests |> List.groupBy (fun t -> t.TestProject)

                for (projName, projTests) in byProject do
                    printfn $"  %s{projName}:"

                    let byClass = projTests |> List.groupBy (fun t -> t.TestClass)

                    for (cls, methods) in byClass do
                        printfn $"    %s{cls}"

                        for m in methods do
                            printfn $"      - %s{m.TestMethod}"

                0
            | RunAll reason ->
                printfn $"Would run ALL tests (reason: %s{reason})"
                0

/// Run the status command: show what would run without executing.
let runStatus (repoRoot: string) : int = runStatusWith jjDiffProvider repoRoot

/// Run the run command with an injectable diff provider.
let runRunWith (getDiff: DiffProvider) (repoRoot: string) : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if not (File.Exists(dbPath)) then
        eprintfn "No index found. Run 'test-prune index' first."
        1
    else
        let db = Database.create dbPath
        let checker = FSharpChecker.Create()

        match analyzeChanges getDiff repoRoot db checker with
        | Error msg ->
            eprintfn $"Error: %s{msg}"
            1
        | Ok(selection, _changedFiles) ->
            match selection with
            | RunSubset [] ->
                printfn "No tests affected — nothing to run."
                0
            | RunAll reason ->
                eprintfn $"Running ALL tests (reason: %s{reason})"
                let testProjects = discoverTestProjects repoRoot
                let mutable exitCode = 0

                for projPath in testProjects do
                    let dll = findTestDll projPath
                    eprintfn $"Running: %s{Path.GetFileName(projPath)}"
                    let result = runAllTests dll
                    printfn "%s" result.Output

                    if result.ExitCode <> 0 then
                        exitCode <- result.ExitCode

                exitCode
            | RunSubset tests ->
                let byProject = tests |> List.groupBy (fun t -> t.TestProject)
                let projects = discoverTestProjects repoRoot
                let mutable exitCode = 0

                for (projName, projTests) in byProject do
                    let classes = projTests |> List.map (fun t -> t.TestClass) |> List.distinct

                    match
                        projects
                        |> List.tryFind (fun p -> Path.GetFileNameWithoutExtension(p) = projName)
                    with
                    | Some projPath ->
                        let dll = findTestDll projPath
                        eprintfn $"Running %d{classes.Length} class(es) in %s{projName}"
                        let result = runFilteredTests dll classes
                        printfn "%s" result.Output

                        if result.ExitCode <> 0 then
                            exitCode <- result.ExitCode
                    | None -> eprintfn $"WARNING: project %s{projName} not found"

                exitCode

/// Run the run command: determine and execute affected tests.
let runRun (repoRoot: string) : int = runRunWith jjDiffProvider repoRoot

/// Run the dead-code command: detect unreachable symbols from entry points.
let runDeadCode (repoRoot: string) (entryPatterns: string list) : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if not (File.Exists(dbPath)) then
        eprintfn "No index found. Run 'test-prune index' first."
        1
    else
        let db = Database.create dbPath
        let result = findDeadCode db entryPatterns

        printfn "Dead code analysis:"
        printfn $"  Total symbols: %d{result.TotalSymbols}"
        printfn $"  Reachable: %d{result.ReachableSymbols}"
        printfn $"  Potentially unreachable: %d{result.UnreachableSymbols.Length}"

        if not result.UnreachableSymbols.IsEmpty then
            printfn ""

            let byFile =
                result.UnreachableSymbols
                |> List.groupBy (fun s -> s.SourceFile)
                |> List.sortBy fst

            for (file, symbols) in byFile do
                printfn $"  %s{file} (%d{symbols.Length} unreachable):"

                for s in symbols |> List.sortBy (fun s -> s.LineStart) do
                    printfn $"    - %s{s.FullName} (%A{s.Kind}, line %d{s.LineStart})"

        0

let runCommand (parsed: ParsedCommand) : int =
    let repoRoot =
        match parsed.RepoRoot with
        | Some path -> Path.GetFullPath(path)
        | None ->
            match findRepoRoot (Directory.GetCurrentDirectory()) with
            | Some root -> root
            | None ->
                eprintfn "Error: not in a jj or git repository"
                Environment.Exit(1)
                "" // unreachable

    match parsed.Command with
    | Index -> runIndex repoRoot
    | Run -> runRun repoRoot
    | Status -> runStatus repoRoot
    | DeadCodeCmd patterns -> runDeadCode repoRoot patterns
    | Help ->
        showHelp ()
        0

[<EntryPoint>]
let main args =
    match parseArgs args with
    | Ok parsed -> runCommand parsed
    | Error message ->
        eprintfn $"Error: %s{message}"
        showHelp ()
        1
