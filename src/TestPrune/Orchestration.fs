module TestPrune.Orchestration

open System
open System.IO
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.AuditSink
open TestPrune.Database
open TestPrune.DiffParser
open TestPrune.Domain
open TestPrune.ImpactAnalysis
open TestPrune.DeadCode
open TestPrune.Ports
open TestPrune.ProjectLoader
open TestPrune.TestRunner

let defaultEntryPatterns =
    [ "*.main"; "*.Program.*"; "*.Routes.*"; "*.Scheduler.*" ]

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

let private isOutputPath (path: string) =
    let n = path.Replace('\\', '/')

    n.Contains("/obj/", StringComparison.Ordinal)
    || n.Contains("/bin/", StringComparison.Ordinal)

/// Find all .fs files in src/ and tests/ directories, excluding obj/ and bin/.
let findSourceFiles (repoRoot: string) : string list =
    let searchDirs = [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]

    searchDirs
    |> List.filter Directory.Exists
    |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fs", SearchOption.AllDirectories) |> Array.toList)
    |> List.filter (fun path -> not (isOutputPath path))
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
    |> List.filter (fun p -> not (isOutputPath p))

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
        |> List.map FileInfo
        |> List.filter (fun fi -> fi.Exists)
        |> List.sortBy (fun fi -> fi.FullName)
        |> List.map (fun fi ->
            let mtime = fi.LastWriteTimeUtc.ToString("o")
            $"%s{fi.FullName}:%d{fi.Length}:%s{mtime}")

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

type ProjectResult =
    { ProjectName: string
      ProjectPath: string
      Analysis: AnalysisResult option
      FileKeys: (string * string) list
      ProjectKey: (string * string) option
      Reindexed: bool
      SymbolCount: int
      DepCount: int
      TestCount: int
      SkippedFiles: int
      AnalyzedFiles: int
      TotalFiles: int
      Events: AnalysisEvent list }

/// Sort project infos into topological levels based on project references.
let topoLevels
    (projectPathSet: Set<string>)
    (projectInfos: (string * string list * string list) list)
    : (string * string list * string list) list list =
    let rec loop remaining (processedSet: Set<string>) =
        if remaining |> List.isEmpty then
            []
        else
            let ready, blocked =
                remaining
                |> List.partition (fun (_, _, refs) ->
                    refs |> List.filter projectPathSet.Contains |> List.forall processedSet.Contains)

            if ready.IsEmpty then
                [ remaining ]
            else
                let newSet = ready |> List.fold (fun s (p, _, _) -> Set.add p s) processedSet

                ready :: loop blocked newSet

    loop projectInfos Set.empty

/// Index a single project, returning its analysis results.
let indexProject
    (repoRoot: string)
    (store: SymbolStore)
    (getOptions: ProjectOptionsProvider)
    (checker: FSharpChecker)
    (reindexedSet: Set<string>)
    (fsprojPath: string, compileFiles: string list, projectRefs: string list)
    : ProjectResult =
    let projName = Path.GetFileNameWithoutExtension(fsprojPath)

    try
        let hash = computeProjectHash compileFiles

        let depReindexed = projectRefs |> List.exists reindexedSet.Contains

        match store.GetProjectKey(projName) with
        | Some stored when stored = hash && not depReindexed ->
            eprintfn $"  %s{projName}: unchanged, skipping"

            { ProjectName = projName
              ProjectPath = fsprojPath
              Analysis = None
              FileKeys = []
              ProjectKey = None
              Reindexed = false
              SymbolCount = 0
              DepCount = 0
              TestCount = 0
              SkippedFiles = 0
              AnalyzedFiles = 0
              TotalFiles = compileFiles.Length
              Events = [ ProjectCacheHitEvent projName ] }
        | _ ->
            let projOptions = lazy (getOptions checker fsprojPath)

            let projSnapshot =
                lazy (createProjectSnapshot (projOptions.Force()) |> Async.RunSynchronously)

            let mutable analyzedFiles = 0
            let mutable firstChangedIndex = None
            let mutable localFileKeys = []
            let mutable localSkippedFiles = 0
            let mutable localEvents = []

            let results =
                compileFiles
                |> List.mapi (fun idx sourceFile ->
                    if not (File.Exists(sourceFile)) then
                        None
                    else
                        let relPath = Path.GetRelativePath(repoRoot, sourceFile).Replace('\\', '/')

                        let fileKey = computeFileKey sourceFile

                        let forcedByCompilationOrder =
                            match firstChangedIndex with
                            | Some firstIdx when idx > firstIdx -> true
                            | _ -> false

                        let cached =
                            not forcedByCompilationOrder
                            && match store.GetFileKey(relPath) with
                               | Some stored when stored = fileKey -> true
                               | _ -> false

                        if cached then
                            localSkippedFiles <- localSkippedFiles + 1
                            localEvents <- FileCacheHitEvent(relPath, "file unchanged") :: localEvents

                            Some
                                {| Symbols = store.GetSymbolsInFile(relPath)
                                   Dependencies = store.GetDependenciesFromFile(relPath)
                                   TestMethods = store.GetTestMethodsInFile(relPath) |}
                        else
                            let source = File.ReadAllText(sourceFile)

                            match
                                analyzeSourceWithSnapshot checker sourceFile source (projSnapshot.Force())
                                |> Async.RunSynchronously
                            with
                            | Ok result ->
                                analyzedFiles <- analyzedFiles + 1

                                if firstChangedIndex.IsNone then
                                    firstChangedIndex <- Some idx

                                localFileKeys <- (relPath, fileKey) :: localFileKeys

                                let symbols = normalizeSymbolPaths repoRoot result.Symbols
                                let deps = result.Dependencies

                                let testMethods =
                                    result.TestMethods |> List.map (fun t -> { t with TestProject = projName })

                                localEvents <-
                                    FileAnalyzedEvent(relPath, symbols.Length, deps.Length, testMethods.Length)
                                    :: localEvents

                                Some
                                    {| Symbols = symbols
                                       Dependencies = deps
                                       TestMethods = testMethods |}
                            | Error msg ->
                                eprintfn $"  Warning: %s{sourceFile}: %s{msg}"
                                None)
                |> List.choose id

            let combined =
                { Symbols = results |> List.collect (fun r -> r.Symbols)
                  Dependencies = results |> List.collect (fun r -> r.Dependencies)
                  TestMethods = results |> List.collect (fun r -> r.TestMethods) }

            let fileCount = compileFiles.Length

            eprintfn
                $"  %s{projName}: %d{combined.Symbols.Length} symbols, %d{combined.Dependencies.Length} deps, %d{combined.TestMethods.Length} tests (%d{analyzedFiles}/%d{fileCount} files analyzed)"

            let allEvents =
                (ProjectIndexedEvent(projName, fileCount) :: localEvents) |> List.rev

            { ProjectName = projName
              ProjectPath = fsprojPath
              Analysis = if analyzedFiles > 0 then Some combined else None
              FileKeys = localFileKeys |> List.rev
              ProjectKey = Some(projName, hash)
              Reindexed = true
              SymbolCount = combined.Symbols.Length
              DepCount = combined.Dependencies.Length
              TestCount = combined.TestMethods.Length
              SkippedFiles = localSkippedFiles
              AnalyzedFiles = analyzedFiles
              TotalFiles = fileCount
              Events = allEvents }
    with ex ->
        eprintfn $"  Error processing %s{projName}: %s{ex.Message}"

        { ProjectName = projName
          ProjectPath = fsprojPath
          Analysis = None
          FileKeys = []
          ProjectKey = None
          Reindexed = false
          SymbolCount = 0
          DepCount = 0
          TestCount = 0
          SkippedFiles = 0
          AnalyzedFiles = 0
          TotalFiles = 0
          Events = [] }

/// Run the index command with injectable build runner and project options provider.
let runIndexWith
    (buildRunner: BuildRunner)
    (getOptions: ProjectOptionsProvider)
    (repoRoot: string)
    (checker: FSharpChecker)
    (parallelism: int)
    (auditSink: AuditSink)
    : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")
    let db = Database.create dbPath
    let store = toSymbolStore db
    let sink = toSymbolSink db

    let projectFiles = findProjectFiles repoRoot
    eprintfn $"Found %d{projectFiles.Length} projects"

    eprintfn "Building projects..."
    let buildExitCode = buildRunner repoRoot

    if buildExitCode <> 0 then
        eprintfn "Build failed — cannot index"
        1
    else
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

        let projectPathSet = projectInfos |> List.map (fun (p, _, _) -> p) |> Set.ofList

        let levels = topoLevels projectPathSet projectInfos

        auditSink.Post(timestamp (IndexStartedEvent projectInfos.Length))

        let mutable allProjectResults = []
        let mutable reindexedSet = Set.empty

        for level in levels do
            let levelResults =
                if level.Length = 1 then
                    [ indexProject repoRoot store getOptions checker reindexedSet level.Head ]
                else
                    level
                    |> List.map (fun proj ->
                        async { return indexProject repoRoot store getOptions checker reindexedSet proj })
                    |> fun tasks -> Async.Parallel(tasks, maxDegreeOfParallelism = parallelism)
                    |> Async.RunSynchronously
                    |> Array.toList

            for r in levelResults do
                for event in r.Events do
                    auditSink.Post(timestamp event)

            let newReindexed =
                levelResults
                |> List.choose (fun r -> if r.Reindexed then Some r.ProjectPath else None)
                |> Set.ofList

            reindexedSet <- Set.union reindexedSet newReindexed
            allProjectResults <- levelResults :: allProjectResults

        let allProjectResults = allProjectResults |> List.rev |> List.collect id
        let allResults = allProjectResults |> List.choose (fun r -> r.Analysis)
        let allFileKeys = allProjectResults |> List.collect (fun r -> r.FileKeys)
        let allProjectKeys = allProjectResults |> List.choose (fun r -> r.ProjectKey)
        let totalSymbols = allProjectResults |> List.sumBy (fun r -> r.SymbolCount)
        let totalDeps = allProjectResults |> List.sumBy (fun r -> r.DepCount)
        let totalTests = allProjectResults |> List.sumBy (fun r -> r.TestCount)

        let skippedProjects =
            allProjectResults
            |> List.filter (fun r -> r.Analysis.IsNone && not r.Reindexed)
            |> List.length

        let skippedFiles = allProjectResults |> List.sumBy (fun r -> r.SkippedFiles)

        if not allResults.IsEmpty then
            sink.RebuildProjects allResults allFileKeys allProjectKeys

        eprintfn $"Indexed %d{totalSymbols} symbols, %d{totalDeps} dependencies, %d{totalTests} test methods"

        if skippedProjects > 0 then
            eprintfn $"Skipped %d{skippedProjects} unchanged project(s)"

        if skippedFiles > 0 then
            eprintfn $"Skipped %d{skippedFiles} unchanged file(s)"

        auditSink.Post(timestamp (IndexCompletedEvent(totalSymbols, totalDeps, totalTests)))

        0

type DiffProvider = unit -> Result<string, string>

/// Determine test selection from current jj diff.
let analyzeChanges
    (getDiff: DiffProvider)
    (repoRoot: string)
    (store: SymbolStore)
    (checker: FSharpChecker)
    (auditSink: AuditSink)
    : Result<TestSelection * string list, string> =
    match getDiff () with
    | Error msg -> Error msg
    | Ok diffText ->
        let changedFiles = parseChangedFiles diffText

        auditSink.Post(timestamp (DiffParsedEvent changedFiles))

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
                Ok(RunAll(AnalysisFailedFallback failedFiles), changedFiles)
            else
                let selection, events =
                    selectTests store.GetSymbolsInFile store.QueryAffectedTests changedFiles currentSymbolsByFile

                for event in events do
                    auditSink.Post(timestamp event)

                Ok(selection, changedFiles)

let private withAnalysis
    (getDiff: DiffProvider)
    (repoRoot: string)
    (auditSink: AuditSink)
    (f: TestSelection * string list -> int)
    : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if not (File.Exists(dbPath)) then
        eprintfn "No index found. Run 'test-prune index' first."
        1
    else
        let db = Database.create dbPath
        let store = toSymbolStore db
        let checker = createChecker ()

        match analyzeChanges getDiff repoRoot store checker auditSink with
        | Error msg ->
            eprintfn $"Error: %s{msg}"
            1
        | Ok result -> f result

/// Run the status command with an injectable diff provider.
let runStatusWith (getDiff: DiffProvider) (repoRoot: string) (auditSink: AuditSink) : int =
    withAnalysis getDiff repoRoot auditSink (fun (selection, changedFiles) ->
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
            printfn $"Would run ALL tests (reason: %s{SelectionReason.describe reason})"
            0)

/// Run the run command with an injectable diff provider.
let runRunWith (getDiff: DiffProvider) (repoRoot: string) (auditSink: AuditSink) : int =
    withAnalysis getDiff repoRoot auditSink (fun (selection, _changedFiles) ->
        match selection with
        | RunSubset [] ->
            printfn "No tests affected — nothing to run."
            0
        | RunAll reason ->
            eprintfn $"Running ALL tests (reason: %s{SelectionReason.describe reason})"
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

            exitCode)

/// Run the dead-code command: detect unreachable symbols from entry points.
let runDeadCode (repoRoot: string) (entryPatterns: string list) (includeTests: bool) (auditSink: AuditSink) : int =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if not (File.Exists(dbPath)) then
        eprintfn "No index found. Run 'test-prune index' first."
        1
    else
        let db = Database.create dbPath
        let store = toSymbolStore db
        let allSymbols = store.GetAllSymbols()
        let allNames = store.GetAllSymbolNames()
        let entryPoints = findEntryPoints allNames entryPatterns
        let reachable = store.GetReachableSymbols(entryPoints)
        let testMethodNames = store.GetTestMethodSymbolNames()
        let result, events = findDeadCode allSymbols reachable testMethodNames includeTests

        for event in events do
            auditSink.Post(timestamp event)

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
