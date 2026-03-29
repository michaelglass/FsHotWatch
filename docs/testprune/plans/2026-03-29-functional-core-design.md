# TestPrune Re-Architecture: Functional Core, Imperative Shell

**Date:** 2026-03-29
**Status:** Approved
**Approach:** Functional Core, Imperative Shell

## Goals

1. **Observability** — full audit trail explaining every decision (why was this test selected? why was this file skipped?)
2. **Concurrency correctness** — eliminate shared mutable state (`ConcurrentBag`, `Interlocked`, `ConcurrentDictionary`); use pure return values + fold instead
3. **Making impossible states impossible** — encode invariants in types, typed errors, DU-driven domain
4. **Type-driven development** — let the type system guide design; illegal states are unrepresentable
5. **Programmatic control** — Core is a library first; CLI is a reference implementation; future daemon consumes Core directly

## Design Principles

- Pure functions take data, return data + events. No side effects in Core decision logic.
- Side effects live at the edges: CLI wiring, port implementations (SQLite, FCS, process execution).
- Record-of-functions for ports — testable, swappable, no OO interfaces needed.
- MailboxProcessor only where it genuinely fits: the audit event sink (many producers, one serialized consumer).
- Parallelism via `Async.Parallel` with configurable `maxDegreeOfParallelism`. Each worker returns immutable results; fold after batch completes.

## Section 1: Domain Types

### Typed Wrappers

```fsharp
type FilePath = FilePath of string
type SymbolName = SymbolName of string
type ContentHash = ContentHash of string
type ProjectName = ProjectName of string
```

### Typed Errors

```fsharp
type AnalysisError =
    | ParseFailed of file: FilePath * errors: string list
    | CheckerAborted of file: FilePath
    | DiffProviderFailed of reason: string
    | ProjectBuildFailed of project: ProjectName * exitCode: int
    | DatabaseError of operation: string * exn
```

### File Analysis State

```fsharp
type FileState =
    | Unindexed of FilePath
    | Indexed of IndexedFile
    | AnalysisFailed of FilePath * AnalysisError

and IndexedFile = {
    Path: FilePath
    Symbols: SymbolInfo list
    Dependencies: Dependency list
    TestMethods: TestMethodInfo list
    ContentHash: ContentHash
}
```

### Test Selection with Reasons

```fsharp
type SelectionReason =
    | SymbolChanged of SymbolName * SymbolChange
    | TransitiveDependency of chain: SymbolName list
    | FsprojChanged of FilePath
    | NewFileNotIndexed of FilePath
    | AnalysisFailedFallback of FilePath

type TestSelection =
    | RunSubset of (TestMethodInfo * SelectionReason) list
    | RunAll of SelectionReason
```

## Section 2: Event Stream and Audit Trail

### Event DU

```fsharp
type AnalysisEvent =
    | FileAnalyzed of FilePath * symbolCount: int * depCount: int * testCount: int
    | FileCacheHit of FilePath * reason: string
    | FileSkipped of FilePath * reason: string
    | ProjectCacheHit of ProjectName
    | ProjectIndexed of ProjectName * fileCount: int
    | SymbolChangeDetected of FilePath * SymbolName * SymbolChange
    | TestSelected of TestMethodInfo * SelectionReason
    | TestSelectionDecision of TestSelection
    | DiffParsed of changedFiles: FilePath list
    | IndexStarted of projectCount: int
    | IndexCompleted of totalSymbols: int * totalDeps: int * totalTests: int
    | ErrorOccurred of AnalysisError
    | DeadCodeFound of SymbolName list

type Timestamped<'a> = { Timestamp: DateTimeOffset; Event: 'a }
```

### Pure Functions Return Events

Pure functions return `'result * AnalysisEvent list`. They never emit events directly. The orchestration shell collects events and posts them to the audit sink.

### Audit Sink (MailboxProcessor)

```fsharp
type AuditSink = MailboxProcessor<Timestamped<AnalysisEvent>>

let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
            let! event = inbox.Receive()
            do! persist event
            return! loop ()
        }
        loop ()
    )
```

Single-writer serialized. The shell timestamps events and posts fire-and-forget. Consumers provide the `persist` function (SQLite table, stderr logger, both, or noop).

### Queryable Audit Trail

A new `analysis_events` table in SQLite stores serialized events with timestamps and run IDs, enabling post-hoc queries like "why did run X select test Y?"

## Section 3: Ports (Record-of-Functions)

```fsharp
type SymbolStore = {
    GetSymbolsInFile: FilePath -> Async<SymbolInfo list>
    GetDependenciesFromFile: FilePath -> Async<Dependency list>
    GetTestMethodsInFile: FilePath -> Async<TestMethodInfo list>
    GetFileKey: FilePath -> Async<string option>
    GetProjectKey: ProjectName -> Async<string option>
    QueryAffectedTests: SymbolName list -> Async<TestMethodInfo list>
    GetAllSymbols: unit -> Async<SymbolInfo list>
    GetReachableSymbols: SymbolName list -> Async<Set<SymbolName>>
}

type SymbolSink = {
    RebuildProjects: AnalysisResult list -> (string * string) list -> (string * string) list -> Async<unit>
}

type DiffProvider = unit -> Async<Result<string, AnalysisError>>

type SourceAnalyzer = {
    Analyze: FilePath -> Async<Result<AnalysisResult, AnalysisError>>
}

type AnalysisConfig = {
    Parallelism: int
    RepoRoot: FilePath
}
```

SQLite `Database` module implements `SymbolStore` and `SymbolSink`. Tests provide in-memory maps. The future daemon can layer caching in front.

## Section 4: Pure Core Module Signatures

```fsharp
module SymbolDiff =
    val detectChanges:
        stored: SymbolInfo list
        -> current: SymbolInfo list
        -> SymbolChange list * AnalysisEvent list

module ImpactAnalysis =
    val selectTests:
        storedSymbols: Map<FilePath, SymbolInfo list>
        -> currentSymbols: Map<FilePath, SymbolInfo list>
        -> affectedTests: (SymbolName list -> TestMethodInfo list)
        -> changedFiles: FilePath list
        -> hasFsprojChanges: bool
        -> TestSelection * AnalysisEvent list

module DeadCode =
    val findDeadCode:
        allSymbols: SymbolInfo list
        -> reachable: Set<SymbolName>
        -> testMethodNames: Set<SymbolName>
        -> entryPatterns: string list
        -> DeadCodeResult * AnalysisEvent list
```

`affectedTests` is a function parameter (partially applied from the store by the shell). Core never knows where data comes from.

## Section 5: Orchestration Shell

### Parallelism Model

Each topo level runs `Async.Parallel` with configurable `maxDegreeOfParallelism`. Each worker returns an immutable `ProjectResult`:

```fsharp
type ProjectResult = {
    Project: ProjectName
    Analysis: AnalysisResult option
    FileKeys: (string * string) list
    ProjectKey: string * string
    Events: AnalysisEvent list
    Reindexed: bool
}
```

No `ConcurrentBag`, no `Interlocked`, no `ConcurrentDictionary`. The `reindexedProjects` tracking becomes a `Set` built by folding prior level results.

### Orchestration Function

```fsharp
let runIndex
    (config: AnalysisConfig)
    (store: SymbolStore)
    (sink: SymbolSink)
    (analyzer: SourceAnalyzer)
    (auditSink: AuditSink)
    (projects: ProjectInfo list)
    : Async<IndexResult> =

    let levels = topoLevels projects Set.empty
    let rec processLevels reindexed levels = async {
        match levels with
        | [] -> return ...
        | level :: rest ->
            let! results =
                level
                |> List.map (indexProject config store analyzer reindexed)
                |> fun tasks -> Async.Parallel(tasks, maxDegreeOfParallelism = config.Parallelism)

            results |> Array.iter (fun r -> r.Events |> List.iter auditSink.Post)

            let newReindexed =
                results
                |> Array.choose (fun r -> if r.Reindexed then Some r.Project else None)
                |> Set.ofArray
                |> Set.union reindexed

            return! processLevels newReindexed rest
    }
    processLevels Set.empty levels
```

### Program.fs Splits Into

- **Orchestration.fs** — wires ports, manages parallelism, coordinates commands
- **Cli.fs** — argument parsing, real port wiring (SQLite, jj, FCS), exit codes

## Section 6: File/Module Layout

```
src/TestPrune.Core/
    Domain.fs          -- All types: FilePath, SymbolInfo, AnalysisEvent, errors, ports
    DiffParser.fs      -- Unchanged (already pure)
    SymbolDiff.fs      -- Unchanged + events
    ImpactAnalysis.fs  -- Pure (no DB dependency)
    DeadCode.fs        -- Pure (no DB dependency)
    Extensions.fs      -- ITestPruneExtension (unchanged)
    AstAnalyzer.fs     -- FCS integration (behind SourceAnalyzer port)
    Database.fs        -- SQLite implementation of SymbolStore/SymbolSink ports
    AuditSink.fs       -- MailboxProcessor + SQLite audit persistence
    TestRunner.fs      -- Process execution (behind ports for testing)

src/TestPrune/
    Orchestration.fs   -- Wires ports, manages parallelism, coordinates commands
    ProjectLoader.fs   -- MSBuild integration (unchanged)
    Cli.fs             -- Arg parsing, real port wiring, entry point
```

Compile order enforces the dependency direction: Domain -> pure modules -> port implementations -> orchestration -> CLI.

## Concurrency Decision

MailboxProcessor is used **only** for the audit event sink (many producers, one serialized consumer, fire-and-forget). All other parallelism uses `Async.Parallel` with configurable `maxDegreeOfParallelism` and immutable return values folded after each batch.

Rationale: MailboxProcessors add testing friction (async message protocols vs direct function calls), don't compose like functions, and silently swallow exceptions. The audit sink is the one place where the pattern genuinely fits. The future daemon project may use actors for file watching and debouncing, but that's a separate consumer of Core.
