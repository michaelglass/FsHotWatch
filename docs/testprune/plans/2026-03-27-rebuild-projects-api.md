# Replace `RebuildForProject` with `RebuildProjects`

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace `RebuildForProject` with `RebuildProjects` so cross-project dependency edges can never be silently dropped due to insertion order.

**Architecture:** Single new method `RebuildProjects` takes all `(projectName, AnalysisResult)` pairs at once. One transaction does: delete all stale data, insert all symbols across all projects, then insert all deps (target symbols guaranteed to exist). Remove old per-project methods.

**Tech Stack:** F#, SQLite via Microsoft.Data.Sqlite, xUnit v3 + Unquote

---

### Task 1: Add `RebuildProjects` to Database.fs

**Files:**
- Modify: `src/TestPrune.Core/Database.fs:122-227` (replace `RebuildForProject`)

**Step 1: Write the new method**

Replace `RebuildForProject` (lines 122-226) and `RebuildForProjectIfChanged` (lines 464-471) with a single `RebuildProjects` method. The key difference from the old code: all symbols across all projects are inserted before any dependencies.

```fsharp
    /// Clear and re-insert symbols, dependencies, and test methods for the given projects.
    /// All symbols are inserted before any dependencies, so cross-project edges resolve correctly.
    member _.RebuildProjects(projects: (string * AnalysisResult) list) =
        let allResults = projects |> List.map snd

        let sourceFiles =
            allResults
            |> List.collect (fun r -> r.Symbols |> List.map (fun s -> s.SourceFile))
            |> List.distinct

        use conn = openConnection dbPath

        use txn = conn.BeginTransaction()

        try
            // Phase 1: Delete existing data for all source files
            for file in sourceFiles do
                use delCmd = conn.CreateCommand()
                delCmd.Transaction <- txn

                delCmd.CommandText <-
                    """
                    DELETE FROM dependencies WHERE from_symbol_id IN (SELECT id FROM symbols WHERE source_file = @file)
                        OR to_symbol_id IN (SELECT id FROM symbols WHERE source_file = @file);
                    DELETE FROM test_methods WHERE symbol_id IN (SELECT id FROM symbols WHERE source_file = @file);
                    DELETE FROM symbols WHERE source_file = @file;
                    """

                delCmd.Parameters.AddWithValue("@file", file) |> ignore
                delCmd.ExecuteNonQuery() |> ignore

            let now = DateTime.UtcNow.ToString("o")

            // Phase 2: Insert ALL symbols across ALL projects
            use insCmd = conn.CreateCommand()
            insCmd.Transaction <- txn

            insCmd.CommandText <-
                """
                INSERT OR REPLACE INTO symbols (full_name, kind, source_file, line_start, line_end, content_hash, indexed_at)
                VALUES (@fullName, @kind, @sourceFile, @lineStart, @lineEnd, @contentHash, @indexedAt)
                """

            let pFullName = insCmd.Parameters.Add("@fullName", SqliteType.Text)
            let pKind = insCmd.Parameters.Add("@kind", SqliteType.Text)
            let pSourceFile = insCmd.Parameters.Add("@sourceFile", SqliteType.Text)
            let pLineStart = insCmd.Parameters.Add("@lineStart", SqliteType.Integer)
            let pLineEnd = insCmd.Parameters.Add("@lineEnd", SqliteType.Integer)
            let pContentHash = insCmd.Parameters.Add("@contentHash", SqliteType.Text)
            let pIndexedAt = insCmd.Parameters.Add("@indexedAt", SqliteType.Text)

            for result in allResults do
                for sym in result.Symbols do
                    pFullName.Value <- sym.FullName
                    pKind.Value <- symbolKindToString sym.Kind
                    pSourceFile.Value <- sym.SourceFile
                    pLineStart.Value <- sym.LineStart
                    pLineEnd.Value <- sym.LineEnd
                    pContentHash.Value <- sym.ContentHash
                    pIndexedAt.Value <- now
                    insCmd.ExecuteNonQuery() |> ignore

            // Phase 3: Insert ALL dependencies (target symbols now guaranteed to exist)
            use depCmd = conn.CreateCommand()
            depCmd.Transaction <- txn

            depCmd.CommandText <-
                """
                INSERT OR IGNORE INTO dependencies (from_symbol_id, to_symbol_id, dep_kind)
                SELECT f.id, t.id, @depKind
                FROM symbols f, symbols t
                WHERE f.full_name = @fromSymbol AND t.full_name = @toSymbol
                """

            let pFromSymbol = depCmd.Parameters.Add("@fromSymbol", SqliteType.Text)
            let pToSymbol = depCmd.Parameters.Add("@toSymbol", SqliteType.Text)
            let pDepKind = depCmd.Parameters.Add("@depKind", SqliteType.Text)

            for result in allResults do
                for dep in result.Dependencies do
                    pFromSymbol.Value <- dep.FromSymbol
                    pToSymbol.Value <- dep.ToSymbol
                    pDepKind.Value <- depKindToString dep.Kind
                    depCmd.ExecuteNonQuery() |> ignore

            // Phase 4: Insert ALL test methods
            use tmCmd = conn.CreateCommand()
            tmCmd.Transaction <- txn

            tmCmd.CommandText <-
                """
                INSERT OR IGNORE INTO test_methods (symbol_id, test_project, test_class, test_method)
                SELECT id, @testProject, @testClass, @testMethod
                FROM symbols WHERE full_name = @symbolFullName
                """

            let pSymbolFullName = tmCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
            let pTestProject = tmCmd.Parameters.Add("@testProject", SqliteType.Text)
            let pTestClass = tmCmd.Parameters.Add("@testClass", SqliteType.Text)
            let pTestMethod = tmCmd.Parameters.Add("@testMethod", SqliteType.Text)

            for result in allResults do
                for tm in result.TestMethods do
                    pSymbolFullName.Value <- tm.SymbolFullName
                    pTestProject.Value <- tm.TestProject
                    pTestClass.Value <- tm.TestClass
                    pTestMethod.Value <- tm.TestMethod
                    tmCmd.ExecuteNonQuery() |> ignore

            txn.Commit()
        with ex ->
            txn.Rollback()
            raise ex
```

Also delete `RebuildForProjectIfChanged` (lines 464-471).

**Step 2: Verify it compiles**

Run: `dotnet build src/TestPrune.Core/TestPrune.Core.fsproj`
Expected: Build succeeds (no callers in Core itself). Tests and CLI will fail to compile — that's expected, we fix those in later tasks.

**Step 3: Commit**

```
jj commit -m "feat: replace RebuildForProject with RebuildProjects

All symbols are inserted before any dependencies, so cross-project
edges can never be silently dropped due to insertion order."
```

---

### Task 2: Migrate DatabaseTests.fs

**Files:**
- Modify: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Mechanical rename**

Every call site uses one of these patterns:

```fsharp
// Pattern A (most tests): single project, single call
db.RebuildForProject("MyProject", result)
// becomes:
db.RebuildProjects([ "MyProject", result ])

// Pattern B (IntegrationTests has this, DatabaseTests does not):
// two sequential calls for same project — combine into one list
```

Do a find-and-replace across `DatabaseTests.fs`:
- `db.RebuildForProject("MyProject", result)` → `db.RebuildProjects([ "MyProject", result ])`
- `db.RebuildForProject("MyProject", result1)` → `db.RebuildProjects([ "MyProject", result1 ])`
- `db.RebuildForProject("MyProject", result2)` → `db.RebuildProjects([ "MyProject", result2 ])`

Rename the test module `RebuildForProject replaces old data` → `RebuildProjects replaces old data`.

Delete the entire `RebuildForProjectIfChanged` test module (lines 604-694) — the method no longer exists.

Rename the test `insert via RebuildForProject and query back via GetSymbolsInFile` → `insert via RebuildProjects and query back via GetSymbolsInFile`.

**Step 2: Run DatabaseTests**

Run: `dotnet test tests/TestPrune.Tests/ --filter "FullyQualifiedName~DatabaseTests"`
Expected: All pass.

**Step 3: Commit**

```
jj commit -m "test: migrate DatabaseTests to RebuildProjects"
```

---

### Task 3: Add cross-project dependency test

**Files:**
- Modify: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Write the failing test**

Add a new test module after `RebuildProjects replaces old data`:

```fsharp
module ``Cross-project dependencies`` =

    [<Fact>]
    let ``cross-project dep edges survive regardless of list order`` () =
        withDb (fun db ->
            // Project A defines the symbol
            let projectA =
                "ProjectA",
                { Symbols =
                    [ { FullName = "LibModule.helper"
                        Kind = Function
                        SourceFile = "src/Lib/Helper.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            // Project B has a test that depends on A's symbol
            let projectB =
                "ProjectB",
                { Symbols =
                    [ { FullName = "Tests.MyTests.test1"
                        Kind = Function
                        SourceFile = "tests/MyTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.MyTests.test1"
                        ToSymbol = "LibModule.helper"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.test1"
                        TestProject = "ProjectB"
                        TestClass = "Tests.MyTests"
                        TestMethod = "test1" } ] }

            // Pass B before A — the old API would silently drop the edge
            db.RebuildProjects([ projectB; projectA ])

            let affected = db.QueryAffectedTests [ "LibModule.helper" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test1" @>)
```

**Step 2: Run the test to verify it passes**

Run: `dotnet test tests/TestPrune.Tests/ --filter "FullyQualifiedName~Cross-project"`
Expected: PASS — this is the whole point of the new API.

**Step 3: Commit**

```
jj commit -m "test: verify cross-project deps survive regardless of order"
```

---

### Task 4: Migrate remaining test files

**Files:**
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs` (~25 call sites)
- Modify: `tests/TestPrune.Tests/ImpactAnalysisTests.fs` (~10 call sites)
- Modify: `tests/TestPrune.Tests/IntegrationTests.fs` (~8 call sites)

**Step 1: Mechanical rename in DeadCodeTests.fs and ImpactAnalysisTests.fs**

Same pattern as DatabaseTests — all single-project calls:

```fsharp
db.RebuildForProject("App", graph)
// becomes:
db.RebuildProjects([ "App", graph ])
```

**Step 2: IntegrationTests.fs — special case at lines 106-107**

This test has two sequential calls for the same project:
```fsharp
db.RebuildForProject("MyProject", libAnalysis)
db.RebuildForProject("MyProject", testAnalysis)
```

Replace with a single call:
```fsharp
db.RebuildProjects([ "MyProject", libAnalysis; "MyProject", testAnalysis ])
```

All other IntegrationTests call sites are single-call, same mechanical rename.

**Step 3: Run all tests**

Run: `dotnet test tests/TestPrune.Tests/`
Expected: All pass.

**Step 4: Commit**

```
jj commit -m "test: migrate DeadCodeTests, ImpactAnalysisTests, IntegrationTests to RebuildProjects"
```

---

### Task 5: Migrate CLI (Program.fs)

**Files:**
- Modify: `src/TestPrune/Program.fs:222-360`

**Step 1: Collect results instead of writing per-project**

The current code calls `db.RebuildForProject` inside `indexProject` (line 327). Change `indexProject` to return an optional `(string * AnalysisResult)` instead:

In `indexProject`, replace lines 326-327:
```fsharp
                    if analyzedFiles > 0 then
                        db.RebuildForProject(projName, combined)
```

with:
```fsharp
                    if analyzedFiles > 0 then
                        Some(projName, combined)
                    else
                        None
```

Change the return type: the `try` block currently ends at line 338 printing stats. After `Some(projName, combined)`, keep the `SetProjectKey`, `reindexedProjects.TryAdd`, counter increments, and `eprintfn` calls — but return the `Some` at the end. In the skip path (line 257), return `None`. In the error handler (line 339), return `None`.

Then after the topo-level loop (lines 342-350), collect all results and call `RebuildProjects`:

```fsharp
        let mutable allProjectResults = []

        for level in levels do
            let levelResults =
                if level.Length = 1 then
                    [ indexProject level.Head ]
                else
                    level
                    |> List.map (fun proj -> async { return indexProject proj })
                    |> Async.Parallel
                    |> Async.RunSynchronously
                    |> Array.toList

            allProjectResults <- allProjectResults @ levelResults

        let projectsToRebuild = allProjectResults |> List.choose id

        if not projectsToRebuild.IsEmpty then
            db.RebuildProjects(projectsToRebuild)
```

**Step 2: Build and run**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests pass.

**Step 3: Smoke test with example solution**

Run: `mise run example`
Expected: Indexes the example solution, reports symbols/deps/tests.

**Step 4: Commit**

```
jj commit -m "feat: CLI uses RebuildProjects for correct cross-project deps"
```

---

### Task 6: Update documentation

**Files:**
- Modify: `README.md:65,89`
- Modify: `docs/index.md:65,89`

**Step 1: Update code examples**

In both files, replace:
```fsharp
    db.RebuildForProject("MyProject", normalized)
```
with:
```fsharp
    db.RebuildProjects([ "MyProject", normalized ])
```

And replace:
```fsharp
    db.RebuildForProject("MyProject", combined)
```
with:
```fsharp
    db.RebuildProjects([ "MyProject", combined ])
```

**Step 2: Commit**

```
jj commit -m "docs: update examples to use RebuildProjects"
```

---

### Task 7: Final verification

**Step 1: Full test suite**

Run: `dotnet test`
Expected: All tests pass.

**Step 2: Grep for any remaining references**

Run: `grep -r "RebuildForProject" src/ tests/ docs/ README.md`
Expected: No matches (only the design plan doc should have it).

**Step 3: Commit if any stragglers found**
