# Functional Core, Imperative Shell — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Re-architect TestPrune into a pure functional core with side effects at the edges, typed events for audit trail, and no shared mutable state for concurrency.

**Architecture:** Introduce Domain.fs with typed errors, events, and selection reasons. Make ImpactAnalysis and DeadCode pure by extracting DB calls to the shell. Replace ConcurrentBag/Interlocked with immutable return values folded after Async.Parallel. Add a MailboxProcessor-based audit sink for event persistence.

**Tech Stack:** F# 10, FSharp.Compiler.Service, Microsoft.Data.Sqlite, xUnit v3

**Design doc:** `docs/plans/2026-03-29-functional-core-design.md`

---

### Task 1: Create Domain.fs with Core Domain Types

Add the foundation types that other tasks build on. This is purely additive — no existing code changes.

**Files:**
- Create: `src/TestPrune.Core/Domain.fs`
- Modify: `src/TestPrune.Core/TestPrune.Core.fsproj` (add Domain.fs first in compile order)

**Step 1: Write tests for SelectionReason display**

```fsharp
// tests/TestPrune.Tests/DomainTests.fs
module TestPrune.Tests.DomainTests

open Xunit
open Swensen.Unquote
open TestPrune.Domain

module ``AnalysisError formatting`` =

    [<Fact>]
    let ``ParseFailed includes file and errors`` () =
        let err = ParseFailed("src/Lib.fs", [ "unexpected token" ])
        let msg = AnalysisError.describe err
        test <@ msg.Contains("src/Lib.fs") @>
        test <@ msg.Contains("unexpected token") @>

    [<Fact>]
    let ``CheckerAborted includes file`` () =
        let err = CheckerAborted "src/Lib.fs"
        let msg = AnalysisError.describe err
        test <@ msg.Contains("src/Lib.fs") @>

module ``SelectionReason describe`` =

    [<Fact>]
    let ``FsprojChanged includes path`` () =
        let reason = FsprojChanged "src/MyProject.fsproj"
        let msg = SelectionReason.describe reason
        test <@ msg.Contains("fsproj") @>

    [<Fact>]
    let ``NewFileNotIndexed includes path`` () =
        let reason = NewFileNotIndexed "src/New.fs"
        let msg = SelectionReason.describe reason
        test <@ msg.Contains("src/New.fs") @>
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TestPrune.Tests/ --filter "FullyQualifiedName~DomainTests"`
Expected: Compile error — `TestPrune.Domain` does not exist

**Step 3: Create Domain.fs**

```fsharp
// src/TestPrune.Core/Domain.fs
module TestPrune.Domain

/// Typed error for analysis operations.
type AnalysisError =
    | ParseFailed of file: string * errors: string list
    | CheckerAborted of file: string
    | DiffProviderFailed of reason: string
    | ProjectBuildFailed of project: string * exitCode: int
    | DatabaseError of operation: string * exn

module AnalysisError =
    let describe (error: AnalysisError) : string =
        match error with
        | ParseFailed(file, errors) -> $"Parse failed for %s{file}: %s{errors |> String.concat "; "}"
        | CheckerAborted file -> $"Type checker aborted for %s{file}"
        | DiffProviderFailed reason -> $"Diff provider failed: %s{reason}"
        | ProjectBuildFailed(project, exitCode) -> $"Build failed for %s{project} (exit code %d{exitCode})"
        | DatabaseError(op, ex) -> $"Database error during %s{op}: %s{ex.Message}"

/// Why a test was selected for execution.
type SelectionReason =
    | SymbolChanged of symbolName: string * changeKind: string
    | TransitiveDependency of chain: string list
    | FsprojChanged of file: string
    | NewFileNotIndexed of file: string
    | AnalysisFailedFallback of file: string

module SelectionReason =
    let describe (reason: SelectionReason) : string =
        match reason with
        | SymbolChanged(name, kind) -> $"Symbol %s{name} was %s{kind}"
        | TransitiveDependency chain -> $"Transitive dependency via %s{chain |> String.concat " -> "}"
        | FsprojChanged file -> $"fsproj file changed: %s{file}"
        | NewFileNotIndexed file -> $"New file not yet indexed: %s{file}"
        | AnalysisFailedFallback file -> $"Analysis failed for %s{file}, running all as fallback"

/// Events emitted during analysis for observability and audit trail.
type AnalysisEvent =
    | FileAnalyzedEvent of file: string * symbolCount: int * depCount: int * testCount: int
    | FileCacheHitEvent of file: string * reason: string
    | FileSkippedEvent of file: string * reason: string
    | ProjectCacheHitEvent of project: string
    | ProjectIndexedEvent of project: string * fileCount: int
    | SymbolChangeDetectedEvent of file: string * symbolName: string * changeKind: string
    | TestSelectedEvent of testMethod: string * reason: SelectionReason
    | DiffParsedEvent of changedFiles: string list
    | IndexStartedEvent of projectCount: int
    | IndexCompletedEvent of totalSymbols: int * totalDeps: int * totalTests: int
    | ErrorEvent of AnalysisError
    | DeadCodeFoundEvent of symbolNames: string list

type Timestamped<'a> = { Timestamp: System.DateTimeOffset; Event: 'a }

/// Configuration for analysis operations.
type AnalysisConfig =
    { Parallelism: int
      RepoRoot: string }
```

**Step 4: Add Domain.fs to fsproj compile order (first position) and DomainTests.fs to test fsproj**

In `src/TestPrune.Core/TestPrune.Core.fsproj`, add `<Compile Include="Domain.fs" />` as the first item.

In `tests/TestPrune.Tests/TestPrune.Tests.fsproj`, add `<Compile Include="DomainTests.fs" />` after TestHelpers.fs.

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TestPrune.Tests/ --filter "FullyQualifiedName~DomainTests"`
Expected: PASS

**Step 6: Run full test suite**

Run: `mise run test`
Expected: All tests pass (Domain.fs is additive, nothing depends on it yet)

**Step 7: Commit**

```bash
jj commit -m "feat: add Domain.fs with typed errors, selection reasons, and analysis events"
```

---

### Task 2: Make ImpactAnalysis Pure

Remove `Database` dependency from `ImpactAnalysis.selectTests`. Instead of calling `db.GetSymbolsInFile` and `db.QueryAffectedTests`, accept data and a function parameter. Return events alongside the selection.

**Files:**
- Modify: `src/TestPrune.Core/ImpactAnalysis.fs`
- Modify: `tests/TestPrune.Tests/ImpactAnalysisTests.fs`
- Modify: `src/TestPrune/Program.fs` (update call sites)
- Modify: `tests/TestPrune.Tests/IntegrationTests.fs` (update call sites)
- Modify: `tests/TestPrune.Tests/ProgramTests.fs` (update call sites)

**Step 1: Update ImpactAnalysis.fs to remove DB dependency**

Change `selectTests` signature from:
```fsharp
let selectTests (db: Database) (changedFiles: string list) (currentSymbolsByFile: Map<string, SymbolInfo list>) : TestSelection
```

To:
```fsharp
let selectTests
    (getStoredSymbols: string -> SymbolInfo list)
    (queryAffectedTests: string list -> TestMethodInfo list)
    (changedFiles: string list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    : TestSelection
```

Replace `db.GetSymbolsInFile file` with `getStoredSymbols file` and `db.QueryAffectedTests allChangedNames` with `queryAffectedTests allChangedNames`.

Remove `open TestPrune.Database`.

**Step 2: Update ImpactAnalysisTests.fs**

Change all `selectTests db` calls to `selectTests db.GetSymbolsInFile db.QueryAffectedTests`. The tests still use a real DB — they just pass the methods as functions now.

For example:
```fsharp
// Before:
let result = selectTests db [ "src/Lib.fs" ] currentSymbols

// After:
let result = selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols
```

**Step 3: Update Program.fs call sites**

In `analyzeChanges` (line ~466):
```fsharp
// Before:
let selection = selectTests db changedFiles currentSymbolsByFile

// After:
let selection = selectTests db.GetSymbolsInFile db.QueryAffectedTests changedFiles currentSymbolsByFile
```

**Step 4: Update IntegrationTests.fs call sites**

Same pattern — pass `db.GetSymbolsInFile db.QueryAffectedTests` where `db` was passed before.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "refactor: make ImpactAnalysis.selectTests pure — accept functions instead of Database"
```

---

### Task 3: Make DeadCode Pure

Remove `Database` dependency from `DeadCode.findDeadCode`. Accept data directly instead of a DB handle.

**Files:**
- Modify: `src/TestPrune.Core/DeadCode.fs`
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs`
- Modify: `src/TestPrune/Program.fs` (update call site)
- Modify: `tests/TestPrune.Tests/IntegrationTests.fs` (update call sites)

**Step 1: Update DeadCode.fs signature**

Change from:
```fsharp
let findDeadCode (db: Database) (entryPointPatterns: string list) (includeTests: bool) : DeadCodeResult
```

To:
```fsharp
let findDeadCode
    (allSymbols: SymbolInfo list)
    (reachable: Set<string>)
    (testMethodNames: Set<string>)
    (entryPointPatterns: string list)
    (includeTests: bool)
    : DeadCodeResult
```

The function body changes:
- Remove `let allSymbols = db.GetAllSymbols()` — it's now a parameter
- Remove `let reachable = db.GetReachableSymbols(entryPoints)` — caller computes reachable set and passes it in
- Remove `let testMethodNames = db.GetTestMethodSymbolNames()` — it's now a parameter
- Keep the entry point matching logic (`matchesPattern`) since it filters `allSymbols` before the caller can know what to pass for `reachable`

Actually, we need to split this differently. The entry point matching depends on `allSymbols`, and `reachable` depends on entry points. So the cleanest split is:

```fsharp
/// Match entry point patterns against symbol names.
let findEntryPoints (allNames: Set<string>) (entryPointPatterns: string list) : string list =
    allNames
    |> Set.filter (fun name -> entryPointPatterns |> List.exists (fun pat -> matchesPattern pat name))
    |> Set.toList

/// Find dead code given pre-computed data. Pure function.
let findDeadCode
    (allSymbols: SymbolInfo list)
    (reachable: Set<string>)
    (testMethodNames: Set<string>)
    (includeTests: bool)
    : DeadCodeResult
```

The caller (Program.fs) does:
```fsharp
let allSymbols = db.GetAllSymbols()
let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
let entryPoints = findEntryPoints allNames entryPatterns
let reachable = db.GetReachableSymbols(entryPoints)
let testMethodNames = db.GetTestMethodSymbolNames()
let result = findDeadCode allSymbols reachable testMethodNames includeTests
```

Remove `open TestPrune.Database` from DeadCode.fs.

**Step 2: Update DeadCodeTests.fs**

Each test that called `findDeadCode db patterns includeTests` now needs to:
1. Call `db.GetAllSymbols()` to get symbols
2. Call `findEntryPoints` to get entry points
3. Call `db.GetReachableSymbols` for reachable set
4. Call `db.GetTestMethodSymbolNames()` for test names
5. Call `findDeadCode` with the data

Create a helper to reduce boilerplate:
```fsharp
let private runDeadCode (db: Database) (patterns: string list) (includeTests: bool) =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
    let entryPoints = DeadCode.findEntryPoints allNames patterns
    let reachable = db.GetReachableSymbols(entryPoints)
    let testMethodNames = db.GetTestMethodSymbolNames()
    DeadCode.findDeadCode allSymbols reachable testMethodNames includeTests
```

Replace all `findDeadCode db patterns includeTests` with `runDeadCode db patterns includeTests`.

**Step 3: Update Program.fs call site**

In `runDeadCode` function (~line 589):
```fsharp
// Before:
let result = findDeadCode db entryPatterns includeTests

// After:
let allSymbols = db.GetAllSymbols()
let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
let entryPoints = DeadCode.findEntryPoints allNames entryPatterns
let reachable = db.GetReachableSymbols(entryPoints)
let testMethodNames = db.GetTestMethodSymbolNames()
let result = DeadCode.findDeadCode allSymbols reachable testMethodNames includeTests
```

**Step 4: Update IntegrationTests.fs call sites**

Same pattern as DeadCodeTests.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "refactor: make DeadCode.findDeadCode pure — accept data instead of Database"
```

---

### Task 4: Add Events to ImpactAnalysis

Now that `selectTests` is pure, add event emission so callers can trace every decision.

**Files:**
- Modify: `src/TestPrune.Core/ImpactAnalysis.fs`
- Modify: `tests/TestPrune.Tests/ImpactAnalysisTests.fs`
- Modify: `src/TestPrune/Program.fs` (ignore events at call site for now)
- Modify: `tests/TestPrune.Tests/IntegrationTests.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Write tests for events**

Add a new test module in ImpactAnalysisTests.fs:

```fsharp
module ``Event emission`` =

    [<Fact>]
    let ``symbol change emits SymbolChangeDetectedEvent`` () =
        withDb (fun db ->
            db.RebuildProjects([ standardGraph ])

            let currentSymbols =
                Map.ofList
                    [ "src/Lib.fs",
                      [ { FullName = "Lib.funcB"
                          Kind = Function
                          SourceFile = "src/Lib.fs"
                          LineStart = 1
                          LineEnd = 10
                          ContentHash = "changed" } ] ]

            let result, events =
                selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

            let changeEvents =
                events
                |> List.choose (fun e ->
                    match e with
                    | SymbolChangeDetectedEvent _ -> Some e
                    | _ -> None)

            test <@ changeEvents.Length >= 1 @>)

    [<Fact>]
    let ``fsproj change emits no symbol events`` () =
        withDb (fun db ->
            db.RebuildProjects([ standardGraph ])

            let _result, events =
                selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/MyProject.fsproj" ] Map.empty

            let changeEvents =
                events
                |> List.choose (fun e ->
                    match e with
                    | SymbolChangeDetectedEvent _ -> Some e
                    | _ -> None)

            test <@ changeEvents |> List.isEmpty @>)
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TestPrune.Tests/ --filter "FullyQualifiedName~ImpactAnalysisTests.Event"`
Expected: Compile error — selectTests returns `TestSelection`, not a tuple

**Step 3: Update selectTests to return events**

Change return type from `TestSelection` to `TestSelection * AnalysisEvent list`:

```fsharp
let selectTests
    (getStoredSymbols: string -> SymbolInfo list)
    (queryAffectedTests: string list -> TestMethodInfo list)
    (changedFiles: string list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    : TestSelection * AnalysisEvent list =
    if changedFiles.IsEmpty then
        RunSubset [], []
    elif DiffParser.hasFsprojChanges changedFiles then
        RunAll "fsproj file changed", []
    else
        // ... existing logic, plus accumulate events:
        // For each detected change, emit SymbolChangeDetectedEvent
        // For each file with no stored symbols but has current symbols, note it
        let mutable events = []
        // ... in the fold, when changes detected:
        //   for each change, add SymbolChangeDetectedEvent to events
        // Return (selection, events |> List.rev)
```

Add `open TestPrune.Domain` at the top.

**Step 4: Update all existing call sites to destructure the tuple**

In Program.fs:
```fsharp
// Before:
let selection = selectTests db.GetSymbolsInFile db.QueryAffectedTests changedFiles currentSymbolsByFile

// After:
let selection, _events = selectTests db.GetSymbolsInFile db.QueryAffectedTests changedFiles currentSymbolsByFile
```

In existing ImpactAnalysisTests.fs tests:
```fsharp
// Before:
let result = selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

// After:
let result, _events = selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols
```

Same for IntegrationTests.fs, ProgramTests.fs.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "feat: ImpactAnalysis.selectTests emits AnalysisEvents for symbol changes"
```

---

### Task 5: Add Events to DeadCode

**Files:**
- Modify: `src/TestPrune.Core/DeadCode.fs`
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs`
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/IntegrationTests.fs`

**Step 1: Write test for dead code events**

```fsharp
module ``Event emission`` =

    [<Fact>]
    let ``dead code emits DeadCodeFoundEvent with symbol names`` () =
        withDb (fun db ->
            // Graph with one unreachable function
            let graph = ... // same as "Unreachable function detected" test

            db.RebuildProjects([ graph ])

            let result, events = runDeadCodeWithEvents db [ "*.Program.main" ] false

            let deadCodeEvents =
                events |> List.choose (fun e -> match e with DeadCodeFoundEvent _ -> Some e | _ -> None)

            test <@ deadCodeEvents.Length = 1 @>)
```

**Step 2: Run tests to verify they fail**

Expected: Compile error — `runDeadCodeWithEvents` doesn't exist

**Step 3: Update findDeadCode to return events**

Change return type to `DeadCodeResult * AnalysisEvent list`. Emit `DeadCodeFoundEvent` with the unreachable symbol names.

**Step 4: Update test helper and all call sites**

Update the `runDeadCode` helper in tests to destructure the tuple. Update Program.fs.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "feat: DeadCode.findDeadCode emits AnalysisEvents"
```

---

### Task 6: Add Events to SymbolDiff

**Files:**
- Modify: `src/TestPrune.Core/SymbolDiff.fs`
- Modify: `tests/TestPrune.Tests/SymbolDiffTests.fs`

**Step 1: Write test for diff events**

```fsharp
module ``Event emission`` =

    [<Fact>]
    let ``modified symbol emits SymbolChangeDetectedEvent`` () =
        let stored = [ { FullName = "Lib.func"; Kind = Function; SourceFile = "src/Lib.fs"; LineStart = 1; LineEnd = 5; ContentHash = "old" } ]
        let current = [ { FullName = "Lib.func"; Kind = Function; SourceFile = "src/Lib.fs"; LineStart = 1; LineEnd = 5; ContentHash = "new" } ]

        let changes, events = detectChanges current stored

        test <@ changes.Length = 1 @>
        let changeEvents = events |> List.choose (fun e -> match e with SymbolChangeDetectedEvent _ -> Some e | _ -> None)
        test <@ changeEvents.Length = 1 @>
```

**Step 2: Run test to verify it fails**

**Step 3: Update detectChanges to return events**

Change return from `SymbolChange list` to `SymbolChange list * AnalysisEvent list`.

**Step 4: Update all call sites**

ImpactAnalysis.fs already calls `detectChanges` — update to destructure. SymbolDiffTests need `let changes, _events = ...` everywhere.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "feat: SymbolDiff.detectChanges emits AnalysisEvents"
```

---

### Task 7: Create AuditSink

**Files:**
- Create: `src/TestPrune.Core/AuditSink.fs`
- Modify: `src/TestPrune.Core/TestPrune.Core.fsproj`
- Create: `tests/TestPrune.Tests/AuditSinkTests.fs`
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj`

**Step 1: Write tests**

```fsharp
module TestPrune.Tests.AuditSinkTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open TestPrune.Domain
open TestPrune.AuditSink

module ``AuditSink basics`` =

    [<Fact>]
    let ``posted events are persisted in order`` () =
        let received = System.Collections.Generic.List<Timestamped<AnalysisEvent>>()
        let gate = new ManualResetEventSlim(false)

        let persist event = async {
            received.Add(event)
            if received.Count = 2 then gate.Set()
        }

        let sink = createAuditSink persist

        let event1 = { Timestamp = DateTimeOffset.UtcNow; Event = IndexStartedEvent 5 }
        let event2 = { Timestamp = DateTimeOffset.UtcNow; Event = IndexCompletedEvent(100, 50, 10) }

        sink.Post(event1)
        sink.Post(event2)

        test <@ gate.Wait(TimeSpan.FromSeconds(5.0)) @>
        test <@ received.Count = 2 @>
        test <@ received[0].Event = IndexStartedEvent 5 @>

module ``noopSink`` =

    [<Fact>]
    let ``noop sink does not throw`` () =
        let sink = createNoopSink ()
        sink.Post({ Timestamp = DateTimeOffset.UtcNow; Event = IndexStartedEvent 1 })
        // No assertion needed — just verify it doesn't crash
```

**Step 2: Run tests to verify they fail**

**Step 3: Create AuditSink.fs**

```fsharp
module TestPrune.AuditSink

open TestPrune.Domain

type AuditSink = MailboxProcessor<Timestamped<AnalysisEvent>>

let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () =
            async {
                let! event = inbox.Receive()
                do! persist event
                return! loop ()
            }

        loop ())

let createNoopSink () : AuditSink =
    createAuditSink (fun _ -> async { return () })

let timestamp (event: AnalysisEvent) : Timestamped<AnalysisEvent> =
    { Timestamp = System.DateTimeOffset.UtcNow
      Event = event }
```

**Step 4: Add to fsproj (after Domain.fs, before TestRunner.fs)**

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "feat: add AuditSink with MailboxProcessor-based event persistence"
```

---

### Task 8: Refactor runIndexWith — Eliminate Shared Mutable State

Replace `ConcurrentBag`, `ConcurrentDictionary`, and `Interlocked` with immutable `ProjectResult` records returned from each parallel worker, then folded.

**Files:**
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Define ProjectResult type in Program.fs**

```fsharp
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
      Events: AnalysisEvent list }
```

**Step 2: Refactor indexProject to return ProjectResult**

Change `indexProject` from returning `AnalysisResult option` to `ProjectResult`. Move all `Interlocked` counter updates into the return value. Move `allFileKeys.Add` and `allProjectKeys.Add` into the return value. Move `reindexedProjects.TryAdd` into the return value's `Reindexed` field.

The function signature becomes:
```fsharp
let indexProject
    (repoRoot: string)
    (db: Database)
    (getOptions: ProjectOptionsProvider)
    (checker: FSharpChecker)
    (reindexedSet: Set<string>)
    (fsprojPath: string, compileFiles: string list, projectRefs: string list)
    : ProjectResult
```

Note: `reindexedSet` replaces the `ConcurrentDictionary` — it's the immutable set of projects reindexed in previous levels.

**Step 3: Refactor the level loop to fold results**

```fsharp
let mutable allResults = []
let mutable reindexedSet = Set.empty

for level in levels do
    let levelResults =
        if level.Length = 1 then
            [ indexProject repoRoot db getOptions checker reindexedSet level.Head ]
        else
            level
            |> List.map (fun proj ->
                async { return indexProject repoRoot db getOptions checker reindexedSet proj })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList

    // Fold results
    let newReindexed =
        levelResults
        |> List.choose (fun r -> if r.Reindexed then Some r.ProjectPath else None)
        |> Set.ofList

    reindexedSet <- Set.union reindexedSet newReindexed
    allResults <- allResults @ (levelResults |> List.choose (fun r -> r.Analysis))

// Aggregate from results instead of mutable counters
let allFileKeys = allResults' |> List.collect (fun r -> r.FileKeys)
let allProjectKeys = allResults' |> List.choose (fun r -> r.ProjectKey)
let totalSymbols = allResults' |> List.sumBy (fun r -> r.SymbolCount)
let totalDeps = allResults' |> List.sumBy (fun r -> r.DepCount)
let totalTests = allResults' |> List.sumBy (fun r -> r.TestCount)
let skippedProjects = allResults' |> List.filter (fun r -> r.Analysis.IsNone && not r.Reindexed) |> List.length
let skippedFiles = allResults' |> List.sumBy (fun r -> r.SkippedFiles)
```

Note: we keep `mutable allResults` and `mutable reindexedSet` at the level-loop scope — these are sequential, not concurrent. The key win is that no mutable state is shared across parallel workers.

**Step 4: Remove ConcurrentBag, ConcurrentDictionary, and Interlocked imports**

Remove:
- `let allFileKeys = System.Collections.Concurrent.ConcurrentBag<string * string>()`
- `let allProjectKeys = System.Collections.Concurrent.ConcurrentBag<string * string>()`
- `let reindexedProjects = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()`
- All `Threading.Interlocked.Add` and `Threading.Interlocked.Increment` calls
- The mutable counters `totalSymbols`, `totalDeps`, `totalTests`, `skippedProjects`, `skippedFiles`

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "refactor: eliminate shared mutable state from runIndexWith — use immutable ProjectResult + fold"
```

---

### Task 9: Add Parallelism Configuration

Thread `maxDegreeOfParallelism` through the index command so consumers can control it.

**Files:**
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Add config parameter to runIndexWith**

```fsharp
let runIndexWith
    (buildRunner: BuildRunner)
    (getOptions: ProjectOptionsProvider)
    (repoRoot: string)
    (checker: FSharpChecker)
    (parallelism: int)
    : int
```

Use `parallelism` in the `Async.Parallel` call:
```fsharp
|> fun tasks -> Async.Parallel(tasks, maxDegreeOfParallelism = parallelism)
```

Default to `System.Environment.ProcessorCount` when not specified.

**Step 2: Update tests**

Pass `System.Environment.ProcessorCount` (or 1 for deterministic tests) to `runIndexWith` calls.

**Step 3: Add --parallelism CLI flag**

In `parseArgs`, add:
```fsharp
| "--parallelism" :: n :: rest -> // parse int, thread through
```

**Step 4: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 5: Commit**

```bash
jj commit -m "feat: add --parallelism flag for configurable concurrent analysis"
```

---

### Task 10: Split Program.fs into Orchestration.fs and Cli.fs

Separate orchestration logic (wiring ports, managing commands) from CLI concerns (argument parsing, real implementations, entry point).

**Files:**
- Create: `src/TestPrune/Orchestration.fs`
- Modify: `src/TestPrune/Program.fs` (rename to Cli.fs or keep as thin entry point)
- Modify: `src/TestPrune/TestPrune.fsproj`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Create Orchestration.fs with the core functions**

Move these from Program.fs to Orchestration.fs:
- `findRepoRoot`
- `findSourceFiles`
- `findProjectFiles`
- `computeFileKey`
- `computeProjectHash`
- `createChecker`
- `topoLevels`
- `indexProject`
- `runIndexWith`
- `analyzeChanges`
- `runStatusWith`
- `runRunWith`
- `runDeadCode` (the orchestration function, not the pure DeadCode module)
- `ProjectResult` type
- `BuildRunner`, `ProjectOptionsProvider`, `DiffProvider` type aliases

Keep in Program.fs (now thin CLI layer):
- `Command`, `ParsedCommand` types
- `parseArgs`, `parseGlobalFlags`, `parseDeadCodeFlags`
- `showHelp`
- `dotnetBuildRunner`, `jjDiffProvider` (real implementations)
- `runIndex`, `runRun`, `runStatus` (the no-arg versions that wire real implementations)
- `runCommand`
- `main`

**Step 2: Update module names**

`Orchestration.fs` uses `module TestPrune.Orchestration`.
`Program.fs` keeps `module TestPrune.Program` and adds `open TestPrune.Orchestration`.

**Step 3: Update fsproj compile order**

```xml
<Compile Include="ProjectLoader.fs" />
<Compile Include="Orchestration.fs" />
<Compile Include="Program.fs" />
```

**Step 4: Update test imports**

Tests that reference moved functions need `open TestPrune.Orchestration`.

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "refactor: split Program.fs into Orchestration.fs (logic) and Program.fs (CLI)"
```

---

### Task 11: Wire AuditSink into Orchestration

Connect the audit sink so events from pure functions are actually persisted.

**Files:**
- Modify: `src/TestPrune/Orchestration.fs`
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Add auditSink parameter to runIndexWith**

```fsharp
let runIndexWith
    (buildRunner: BuildRunner)
    (getOptions: ProjectOptionsProvider)
    (repoRoot: string)
    (checker: FSharpChecker)
    (parallelism: int)
    (auditSink: AuditSink)
    : int
```

After each topo level completes, post events from all `ProjectResult`s:
```fsharp
for r in levelResults do
    for event in r.Events do
        auditSink.Post(AuditSink.timestamp event)
```

**Step 2: Wire noop sink in tests, real sink in CLI**

In Program.fs:
```fsharp
let runIndex (repoRoot: string) : int =
    let checker = createChecker ()
    let auditSink = AuditSink.createNoopSink ()  // or create a stderr logger
    runIndexWith dotnetBuildRunner getProjectOptions repoRoot checker Environment.ProcessorCount auditSink
```

Tests pass `AuditSink.createNoopSink()`.

**Step 3: Do the same for analyzeChanges, runStatusWith, runRunWith**

Add audit sink parameter, post events from selectTests.

**Step 4: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 5: Commit**

```bash
jj commit -m "feat: wire AuditSink into orchestration — events flow from pure core through sink"
```

---

### Task 12: Create Port Types and Database Adapter

Define record-of-functions port types and a function to create them from the existing Database class.

**Files:**
- Modify: `src/TestPrune.Core/Domain.fs` (add port types)
- Create: `src/TestPrune.Core/DatabaseAdapter.fs` (or add to Database.fs)
- Modify: `src/TestPrune.Core/TestPrune.Core.fsproj`
- Create: `tests/TestPrune.Tests/DatabaseAdapterTests.fs`
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj`

**Step 1: Define port types in Domain.fs**

```fsharp
/// Port for reading symbol data from storage.
type SymbolStore =
    { GetSymbolsInFile: string -> SymbolInfo list
      GetDependenciesFromFile: string -> Dependency list
      GetTestMethodsInFile: string -> TestMethodInfo list
      GetFileKey: string -> string option
      GetProjectKey: string -> string option
      QueryAffectedTests: string list -> TestMethodInfo list
      GetAllSymbols: unit -> SymbolInfo list
      GetAllSymbolNames: unit -> Set<string>
      GetReachableSymbols: string list -> Set<string>
      GetTestMethodSymbolNames: unit -> Set<string> }

/// Port for writing symbol data to storage.
type SymbolSink =
    { RebuildProjects: AnalysisResult list -> (string * string) list -> (string * string) list -> unit }
```

Note: keeping these synchronous for now since the SQLite implementation is synchronous. Can add Async later if needed.

**Step 2: Write test for adapter**

```fsharp
module TestPrune.Tests.DatabaseAdapterTests

open Xunit
open Swensen.Unquote
open TestPrune.Domain
open TestPrune.Database
open TestPrune.Tests.TestHelpers

module ``SymbolStore from Database`` =

    [<Fact>]
    let ``store wraps database methods`` () =
        withDb (fun db ->
            let store = DatabaseAdapter.toSymbolStore db
            // Should work the same as calling db directly
            let symbols = store.GetSymbolsInFile "nonexistent.fs"
            test <@ symbols |> List.isEmpty @>)
```

**Step 3: Create DatabaseAdapter module**

```fsharp
module TestPrune.DatabaseAdapter

open TestPrune.Domain
open TestPrune.Database

let toSymbolStore (db: Database) : SymbolStore =
    { GetSymbolsInFile = db.GetSymbolsInFile
      GetDependenciesFromFile = db.GetDependenciesFromFile
      GetTestMethodsInFile = db.GetTestMethodsInFile
      GetFileKey = db.GetFileKey
      GetProjectKey = db.GetProjectKey
      QueryAffectedTests = db.QueryAffectedTests
      GetAllSymbols = db.GetAllSymbols
      GetAllSymbolNames = fun () -> db.GetAllSymbolNames()
      GetReachableSymbols = db.GetReachableSymbols
      GetTestMethodSymbolNames = db.GetTestMethodSymbolNames }

let toSymbolSink (db: Database) : SymbolSink =
    { RebuildProjects = fun results fileKeys projectKeys -> db.RebuildProjects(results, fileKeys, projectKeys) }
```

**Step 4: Add to fsproj (after Database.fs)**

**Step 5: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 6: Commit**

```bash
jj commit -m "feat: add SymbolStore/SymbolSink port types and DatabaseAdapter"
```

---

### Task 13: Migrate Orchestration to Use Port Types

Replace direct `Database` usage in orchestration with `SymbolStore`/`SymbolSink`.

**Files:**
- Modify: `src/TestPrune/Orchestration.fs`
- Modify: `src/TestPrune/Program.fs`
- Modify: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Change orchestration function signatures**

Replace `db: Database` parameters with `store: SymbolStore` and `sink: SymbolSink`.

For example in `analyzeChanges`:
```fsharp
// Before:
let analyzeChanges (getDiff: DiffProvider) (repoRoot: string) (db: Database) (checker: FSharpChecker)

// After:
let analyzeChanges (getDiff: DiffProvider) (repoRoot: string) (store: SymbolStore) (checker: FSharpChecker)
```

Replace `db.GetSymbolsInFile` with `store.GetSymbolsInFile`, etc.

**Step 2: Wire adapters in Program.fs**

```fsharp
let db = Database.create dbPath
let store = DatabaseAdapter.toSymbolStore db
let sink = DatabaseAdapter.toSymbolSink db
```

**Step 3: Update tests to use port types**

Tests can either:
- Keep using `DatabaseAdapter.toSymbolStore db` (simplest migration)
- Or create in-memory stores for purely unit-level tests (future improvement)

**Step 4: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 5: Commit**

```bash
jj commit -m "refactor: orchestration uses SymbolStore/SymbolSink ports instead of Database directly"
```

---

### Task 14: Update Extensions to Use Port Types

Replace the `Database` parameter in `ITestPruneExtension` with `SymbolStore`.

**Files:**
- Modify: `src/TestPrune.Core/Extensions.fs`
- Modify: `src/TestPrune.Falco/FalcoRouteAnalysis.fs`
- Modify: `tests/TestPrune.Tests/FalcoRouteExtensionTests.fs`

**Step 1: Update ITestPruneExtension**

```fsharp
type ITestPruneExtension =
    abstract Name: string
    abstract FindAffectedTests: store: SymbolStore -> changedFiles: string list -> repoRoot: string -> AffectedTest list
```

Note: `Extensions.fs` will need `open TestPrune.Domain` for `SymbolStore`.

**Step 2: Update FalcoRouteExtension**

The Falco extension currently uses `db.GetAllHandlerSourceFiles()` and `db.GetUrlPatternsForSourceFile()` which aren't in `SymbolStore`. Options:
- Add route-handler methods to SymbolStore (pollutes generic port with Falco-specific concerns)
- Keep a separate `RouteStore` port for extensions that need route data
- Pass a more specific record to the extension

Best approach: add a `RouteStore` type:
```fsharp
type RouteStore =
    { GetAllHandlerSourceFiles: unit -> Set<string>
      GetUrlPatternsForSourceFile: string -> (string * string) list }
```

And update the extension interface or the Falco extension to accept it.

**Step 3: Update tests**

**Step 4: Run full test suite**

Run: `mise run test`
Expected: All tests pass

**Step 5: Commit**

```bash
jj commit -m "refactor: extensions use port types instead of Database"
```

---

### Future Tasks (Not in This Plan)

These are follow-up improvements that build on the foundation above:

1. **Typed wrappers** (`FilePath`, `SymbolName`, etc.) — introduce gradually, module by module
2. **SQLite audit trail persistence** — create `analysis_events` table, wire a real persister into the audit sink
3. **In-memory SymbolStore for tests** — replace `withDb` test helper with pure in-memory stores where appropriate
4. **Async ports** — make `SymbolStore`/`SymbolSink` methods return `Async<'a>` for non-blocking I/O
5. **FileState DU** — replace the current `Option`-based file handling with `Unindexed | Indexed | AnalysisFailed`
6. **TestSelection with reasons** — migrate from `RunAll of string` to `RunAll of SelectionReason` (requires updating all match sites)
