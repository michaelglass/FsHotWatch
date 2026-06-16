module UsageExample

// The regions below are the single source of truth for the TestPrune.Core
// walkthrough in docs/integration.md. SyncDocs copies everything between the
// `// sync:NAME:start` / `// sync:NAME:end` markers into the matching fenced
// code block in the docs, and CI compiles this file — so the snippets can never
// silently drift from the live TestPrune.Core API.
//
// Each region is written to *compile* (it type-checks against the real API),
// not to *run*: the scaffolding values it leans on (repoRoot, changedFiles, the
// various cache keys, …) are bound in `Scaffolding` below, OUTSIDE the regions,
// so each rendered snippet stays focused on the API calls it demonstrates.
// Each numbered section lives in its own nested module so the snippets can reuse
// natural names (`store`, `db`, `_events`) without colliding.

open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Domain

/// Values the doc regions lean on, kept OUT of the rendered snippets.
module Scaffolding =
    /// Repo root used to normalize absolute symbol paths to repo-relative ones.
    let repoRoot = "/abs/path/to/repo"

    /// Content-addressed cache keys the caller supplies (a VCS tree hash, say).
    let currentKey = "project-content-hash"
    let currentFileKey = "file-content-hash"

    /// Repo-relative source files that changed since the last index.
    let changedFiles: string list = [ "src/Lib.fs" ]

    /// Current symbols grouped by file (produced by analyzing the working tree).
    let currentSymbolsByFile: Map<string, SymbolInfo list> = Map.empty

    /// A shared database handle for the read-only snippets below.
    let db = Database.create ".test-prune.db"

// --- 1. Index your project ---------------------------------------------------

module Index =
    open Scaffolding

    // sync:index:start
    open TestPrune.AstAnalyzer
    open TestPrune.Database

    let checker = FSharpChecker.Create()
    let db = Database.create ".test-prune.db"

    let fileName = "/abs/path/to/src/Lib.fs"
    let source = System.IO.File.ReadAllText fileName
    let projectName = "MyProject"

    let projOptions = getScriptOptions checker fileName source |> Async.RunSynchronously

    match
        analyzeSource checker fileName source projOptions projectName
        |> Async.RunSynchronously
    with
    | Ok result ->
        let normalized =
            { result with
                Symbols = normalizeSymbolPaths repoRoot result.Symbols }

        db.RebuildProjects([ normalized ])
    | Error msg -> eprintfn $"Failed: %s{msg}"
// sync:index:end

// --- 2. Cache for speed ------------------------------------------------------

module Cache =
    open Scaffolding

    // A re-analysis result reusing the index's symbols (a real build merges cached
    // rows with freshly-analyzed ones); kept out of the rendered region.
    let combined =
        AnalysisResult.Create(db.GetSymbolsInFile "src/Lib.fs", db.GetDependenciesFromFile "src/Lib.fs", [])

    // sync:cache:start
    match db.GetProjectKey("MyProject") with
    | Some key when key = currentKey -> () // unchanged — skip the whole project
    | _ ->
        // For files that haven't changed, reuse cached rows instead of re-analyzing:
        match db.GetFileKey("src/Lib.fs") with
        | Some key when key = currentFileKey ->
            let symbols = db.GetSymbolsInFile("src/Lib.fs")
            let deps = db.GetDependenciesFromFile("src/Lib.fs")
            let tests = db.GetTestMethodsInFile("src/Lib.fs")
            () // ... use cached data
        | _ -> () // file changed — run analyzeSource as above

        // Write symbols and both sets of cache keys atomically.
        db.RebuildProjects(
            [ combined ],
            fileKeys = [ "src/Lib.fs", currentFileKey ],
            projectKeys = [ "MyProject", currentKey ]
        )
// sync:cache:end

// --- 3. Find affected tests --------------------------------------------------

module SelectTests =
    open Scaffolding

    // sync:select:start
    open TestPrune.Ports
    open TestPrune.ImpactAnalysis

    let store = toSymbolStore db

    let selection, _events = selectTests store changedFiles currentSymbolsByFile

    match selection with
    | RunSubset tests -> () // only these test methods need to run
    | RunAll reason -> () // can't analyze the change — run everything
// sync:select:end

// --- 4. (Bonus) Find dead code ----------------------------------------------

module DeadCodeScan =
    open Scaffolding
    open TestPrune.Ports

    // sync:dead-code:start
    open TestPrune.DeadCode

    let store = toSymbolStore db
    let allSymbols = store.GetAllSymbols()
    let allNames = store.GetAllSymbolNames()

    let entryPatterns = [ "*.main"; "*.Program.*" ]
    let entryPoints = findEntryPoints allNames entryPatterns
    let reachable = store.GetReachableSymbols(entryPoints)
    let testMethodNames = store.GetTestMethodSymbolNames()

    // result.UnreachableSymbols — symbols nothing reaches from the entry points
    let result, _events = findDeadCode allSymbols reachable testMethodNames false
// sync:dead-code:end

// --- Extensions --------------------------------------------------------------

// An extension injects dependency edges the AST analyzer can't see (e.g. an
// HTTP route mapping to a handler file). Implementing the interface against the
// live API is what keeps the doc snippet honest.

module ExtensionExample =
    open TestPrune.Ports

    // sync:extension:start
    open TestPrune.Extensions

    type ExampleExtension() =
        interface ITestPruneExtension with
            member _.Name = "example"

            member _.AnalyzeEdges
                (symbolStore: SymbolStore)
                (changedFiles: string list)
                (repoRoot: string)
                : Dependency list =
                // Map out-of-band coupling (routes, snapshots, config) to edges here.
                []
// sync:extension:end
