# SQL Coupling & Extension System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add source attribution to dependency edges, a `SharedState` dependency kind, a revised extension interface that injects edges at index time, a TestPrune.Sql plugin with `[<ReadsFrom>]`/`[<WritesTo>]` attributes, and a TestPrune.SqlHydra plugin that automates those attributes for SqlHydra codebases.

**Architecture:** Two-layer plugin system. TestPrune.Sql defines attributes and coupling semantics (shared table/column access creates edges). TestPrune.SqlHydra consumes Sql's API and automates detection via SqlHydra's typed AST symbols. Core gets `source` field on edges, `SharedState` DependencyKind, and a revised `ITestPruneExtension` that returns edges instead of `AffectedTest`.

**Tech Stack:** F# / .NET 10, SQLite, FSharp.Compiler.Service, xUnit v3, Unquote, jj

**VCS:** jj — use `jj commit -m "..."` (not git). Use `jj describe` only for the working change.

**Test commands:** `dotnet test` or `mise run test`

**Build command:** `dotnet build` or `mise run build`

---

## Task 1: Add `source` field to Dependency record

Add a `Source` string field to the `Dependency` record so every edge tracks what produced it.

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:50-54`
- Modify: `src/TestPrune.Core/Database.fs:9-68` (schema)
- Modify: `src/TestPrune.Core/Database.fs:273-294` (insert)
- Modify: `src/TestPrune.Core/Database.fs:630-648` (read)
- Modify: `src/TestPrune.Core/Database.fs:141` (SchemaVersion)
- Modify: `src/TestPrune.Core/InMemoryStore.fs`
- Modify: `tests/TestPrune.Tests/TestHelpers.fs:76-82` (standardGraph)
- Test: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Write failing test**

Add to `tests/TestPrune.Tests/DatabaseTests.fs`:

```fsharp
[<Fact>]
let ``dependency source is stored and retrieved`` () =
    withDb (fun db ->
        let result =
            AnalysisResult.Create(
                [ { FullName = "A.func"
                    Kind = Function
                    SourceFile = "src/A.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "abc"
                    IsExtern = false }
                  { FullName = "B.func"
                    Kind = Function
                    SourceFile = "src/B.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "def"
                    IsExtern = false } ],
                [ { FromSymbol = "A.func"
                    ToSymbol = "B.func"
                    Kind = Calls
                    Source = "core" } ],
                []
            )

        db.RebuildProjects([ result ], [], [])
        let deps = db.GetDependenciesFromFile("src/A.fs")
        test <@ deps.Length = 1 @>
        test <@ deps[0].Source = "core" @>)
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "dependency source is stored"`
Expected: Compile error — `Source` field doesn't exist on `Dependency`

**Step 3: Add Source field to Dependency record**

In `src/TestPrune.Core/AstAnalyzer.fs:50-54`, change:

```fsharp
type Dependency =
    { FromSymbol: string
      ToSymbol: string
      Kind: DependencyKind
      Source: string }
```

**Step 4: Fix all compilation errors**

The compiler will guide you. Every place that constructs a `Dependency` needs `Source = "core"`. Key locations:

- `src/TestPrune.Core/AstAnalyzer.fs` — all dependency construction in `extractResults` (search for `Kind = Calls`, `Kind = UsesType`, `Kind = PatternMatches`, `Kind = References`)
- `src/TestPrune.Core/Database.fs:645-648` — reading deps, add `Source = "core"` (temporary — will read from DB once schema updated)
- `tests/TestPrune.Tests/TestHelpers.fs:76-82` — standardGraph dependencies
- Any other test files constructing `Dependency` records

**Step 5: Update SQLite schema**

In `src/TestPrune.Core/Database.fs`, update the schema (line 23-28):

```sql
CREATE TABLE IF NOT EXISTS dependencies (
    from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    dep_kind TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT 'core',
    PRIMARY KEY (from_symbol_id, to_symbol_id, dep_kind)
);
```

Bump `SchemaVersion` from 1 to 2 (line 141).

**Step 6: Update dependency insert**

In `src/TestPrune.Core/Database.fs:277-293`, add the source parameter:

```fsharp
depCmd.CommandText <-
    """
    INSERT OR IGNORE INTO dependencies (from_symbol_id, to_symbol_id, dep_kind, source)
    SELECT f.id, t.id, @depKind, @source
    FROM symbols f, symbols t
    WHERE f.full_name = @fromSymbol AND t.full_name = @toSymbol
    """

let pFromSymbol = depCmd.Parameters.Add("@fromSymbol", SqliteType.Text)
let pToSymbol = depCmd.Parameters.Add("@toSymbol", SqliteType.Text)
let pDepKind = depCmd.Parameters.Add("@depKind", SqliteType.Text)
let pSource = depCmd.Parameters.Add("@source", SqliteType.Text)

for result in results do
    for dep in result.Dependencies do
        pFromSymbol.Value <- dep.FromSymbol
        pToSymbol.Value <- dep.ToSymbol
        pDepKind.Value <- depKindToString dep.Kind
        pSource.Value <- dep.Source
        depCmd.ExecuteNonQuery() |> ignore
```

**Step 7: Update dependency read**

In `src/TestPrune.Core/Database.fs:632-648`, update the SELECT and reader:

```fsharp
cmd.CommandText <-
    """
    SELECT f.full_name, t.full_name, d.dep_kind, d.source
    FROM dependencies d
    JOIN symbols f ON f.id = d.from_symbol_id
    JOIN symbols t ON t.id = d.to_symbol_id
    WHERE f.source_file = @sourceFile
    """
```

And the reader:

```fsharp
readAll reader (fun r ->
    { FromSymbol = r.GetString(0)
      ToSymbol = r.GetString(1)
      Kind = stringToDepKind warnedUnknownKinds (r.GetString(2))
      Source = r.GetString(3) })
```

**Step 8: Run all tests**

Run: `mise run test`
Expected: All tests pass, including the new one.

**Step 9: Commit**

```bash
jj commit -m "feat: add source attribution to dependency edges"
```

---

## Task 2: Add `SharedState` to DependencyKind

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:44-48`
- Modify: `src/TestPrune.Core/Database.fs:95-112`
- Test: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Write failing test**

Add to `tests/TestPrune.Tests/DatabaseTests.fs`:

```fsharp
[<Fact>]
let ``SharedState dependency kind round-trips through database`` () =
    withDb (fun db ->
        let result =
            AnalysisResult.Create(
                [ { FullName = "Writer.save"
                    Kind = Function
                    SourceFile = "src/Writer.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "aaa"
                    IsExtern = false }
                  { FullName = "Reader.load"
                    Kind = Function
                    SourceFile = "src/Reader.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "bbb"
                    IsExtern = false } ],
                [ { FromSymbol = "Writer.save"
                    ToSymbol = "Reader.load"
                    Kind = SharedState
                    Source = "sql" } ],
                []
            )

        db.RebuildProjects([ result ], [], [])
        let deps = db.GetDependenciesFromFile("src/Writer.fs")
        test <@ deps.Length = 1 @>
        test <@ deps[0].Kind = SharedState @>
        test <@ deps[0].Source = "sql" @>)
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "SharedState dependency kind"`
Expected: Compile error — `SharedState` not defined

**Step 3: Add SharedState to DependencyKind**

In `src/TestPrune.Core/AstAnalyzer.fs:44-48`:

```fsharp
type DependencyKind =
    | Calls
    | UsesType
    | PatternMatches
    | References
    | SharedState
```

**Step 4: Add serialization**

In `src/TestPrune.Core/Database.fs:95-100`:

```fsharp
let private depKindToString =
    function
    | Calls -> "calls"
    | UsesType -> "uses_type"
    | PatternMatches -> "pattern_matches"
    | References -> "references"
    | SharedState -> "shared_state"
```

In `src/TestPrune.Core/Database.fs:102-112`:

```fsharp
let private stringToDepKind (warned: HashSet<string>) =
    function
    | "calls" -> Calls
    | "uses_type" -> UsesType
    | "pattern_matches" -> PatternMatches
    | "references" -> References
    | "shared_state" -> SharedState
    | unknown ->
        if warned.Add($"DependencyKind:%s{unknown}") then
            eprintfn $"Warning: unknown DependencyKind '%s{unknown}' in database, defaulting to References"

        References
```

**Step 5: Run all tests**

Run: `mise run test`
Expected: All pass.

**Step 6: Commit**

```bash
jj commit -m "feat: add SharedState dependency kind"
```

---

## Task 3: Revise ITestPruneExtension interface

Change the extension interface from returning `AffectedTest` to returning `Dependency` edges at index time.

**Files:**
- Modify: `src/TestPrune.Core/Extensions.fs`
- Modify: `src/TestPrune.Falco/FalcoRouteAnalysis.fs`
- Modify: `tests/TestPrune.Tests/FalcoRouteExtensionTests.fs`

**Step 1: Write failing test for the new interface shape**

Add a new test to `tests/TestPrune.Tests/FalcoRouteExtensionTests.fs` (or a new test file if cleaner) that tests the new interface:

```fsharp
[<Fact>]
let ``extension returns Dependency edges with source attribution`` () =
    // This test will drive the interface change.
    // Create a mock extension that returns SharedState edges.
    let extension =
        { new ITestPruneExtension with
            member _.Name = "test-extension"
            member _.AnalyzeEdges _symbolStore _changedFiles _repoRoot =
                [ { FromSymbol = "A.write"
                    ToSymbol = "B.read"
                    Kind = SharedState
                    Source = "test-extension" } ] }

    let edges = extension.AnalyzeEdges Unchecked.defaultof<_> [] ""
    test <@ edges.Length = 1 @>
    test <@ edges[0].Kind = SharedState @>
    test <@ edges[0].Source = "test-extension" @>
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "extension returns Dependency edges"`
Expected: Compile error — `AnalyzeEdges` not on `ITestPruneExtension`

**Step 3: Update the interface**

In `src/TestPrune.Core/Extensions.fs`:

```fsharp
module TestPrune.Extensions

open TestPrune.AstAnalyzer
open TestPrune.Ports

/// Extension interface for custom dependency sources beyond AST analysis.
/// Implement this to add framework-specific edge injection (e.g., SQL table coupling,
/// route-based dependencies, or manual hints).
type ITestPruneExtension =
    /// Unique name for this extension (used in logging and edge source attribution).
    abstract Name: string

    /// Given a symbol store and a list of changed source files (repo-relative paths),
    /// return additional dependency edges to inject into the graph.
    abstract AnalyzeEdges:
        symbolStore: SymbolStore -> changedFiles: string list -> repoRoot: string -> Dependency list
```

Remove the `AffectedTest` type if nothing else uses it. If Falco tests still reference it, keep it but mark deprecated — check compilation.

**Step 4: Update FalcoRouteExtension to implement new interface**

In `src/TestPrune.Falco/FalcoRouteAnalysis.fs`, update the interface implementation. The Falco extension currently returns `AffectedTest` — it should now return `Dependency` edges. This requires Falco to construct symbol names for the edges. For now, have it continue working via its existing route-matching logic but wrapping results as dependencies.

This is a larger refactor — Falco needs to map route matches to actual symbol names from the `SymbolStore`. The key change:

```fsharp
interface ITestPruneExtension with
    member _.Name = "Falco Routes"

    member _.AnalyzeEdges (symbolStore: SymbolStore) (changedFiles: string list) (repoRoot: string) =
        // ... existing route matching logic ...
        // Convert matched test classes into edges
        // For each (changedHandlerFile, matchedTestFile) pair,
        // look up symbols in both files via symbolStore
        // and create SharedState edges between them
```

The exact implementation will depend on what symbols are available. The key invariant: the extension returns `Dependency` list with `Source = "falco"`.

**Step 5: Update Falco tests**

Update all tests in `tests/TestPrune.Tests/FalcoRouteExtensionTests.fs` to test the new interface. Tests that checked for `AffectedTest` results should now check for `Dependency` edges.

**Step 6: Run all tests**

Run: `mise run test`
Expected: All pass.

**Step 7: Commit**

```bash
jj commit -m "feat: revise ITestPruneExtension to inject edges instead of returning AffectedTests"
```

---

## Task 4: Create TestPrune.Sql project with attributes

**Files:**
- Create: `src/TestPrune.Sql/TestPrune.Sql.fsproj`
- Create: `src/TestPrune.Sql/Attributes.fs`
- Create: `src/TestPrune.Sql/SqlExtension.fs`
- Modify: `TestPrune.slnx`
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj`
- Test: `tests/TestPrune.Tests/SqlExtensionTests.fs`

**Step 1: Create project skeleton**

Create `src/TestPrune.Sql/TestPrune.Sql.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <NoWarn>$(NoWarn);NU1605;MSB3277;NETSDK1188</NoWarn>
        <PackageId>TestPrune.Sql</PackageId>
        <Authors>Michael Glass</Authors>
        <Description>SQL table/column coupling attributes for TestPrune test impact analysis</Description>
        <PackageLicenseExpression>MIT</PackageLicenseExpression>
        <RepositoryUrl>https://github.com/michaelglass/TestPrune</RepositoryUrl>
        <RepositoryType>git</RepositoryType>
        <Version>0.1.0</Version>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="Attributes.fs" />
        <Compile Include="SqlExtension.fs" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../TestPrune.Core/TestPrune.Core.fsproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Update="FSharp.Core" Version="10.1.*" />
    </ItemGroup>

</Project>
```

Add to `TestPrune.slnx` under `/src/` folder. Add project reference from test project.

**Step 2: Create the attributes**

Create `src/TestPrune.Sql/Attributes.fs`:

```fsharp
namespace TestPrune.Sql

open System

/// Declares that the annotated function reads from a database table.
/// Use with column name for column-level tracking, or without for table-level.
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = true)>]
type ReadsFromAttribute(table: string, column: string) =
    inherit Attribute()
    new(table: string) = ReadsFromAttribute(table, "*")
    member _.Table = table
    member _.Column = column

/// Declares that the annotated function writes to a database table.
/// Use with column name for column-level tracking, or without for table-level.
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = true)>]
type WritesToAttribute(table: string, column: string) =
    inherit Attribute()
    new(table: string) = WritesToAttribute(table, "*")
    member _.Table = table
    member _.Column = column
```

**Step 3: Write failing test for coupling logic**

Create `tests/TestPrune.Tests/SqlExtensionTests.fs` and add it to the test `.fsproj` compile list:

```fsharp
module TestPrune.Tests.SqlExtensionTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Sql

module ``SqlExtension coupling`` =

    [<Fact>]
    let ``writer and reader of same table get SharedState edge`` () =
        let facts =
            [ { Symbol = "Queries.saveArticle"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "Queries.getArticle"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 1 @>
        test <@ edges[0].FromSymbol = "Queries.saveArticle" @>
        test <@ edges[0].ToSymbol = "Queries.getArticle" @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql" @>
```

**Step 4: Run test to verify it fails**

Run: `dotnet test --filter "writer and reader of same table"`
Expected: Compile error — `SqlCoupling` module doesn't exist

**Step 5: Implement the coupling engine**

Create `src/TestPrune.Sql/SqlExtension.fs`:

```fsharp
module TestPrune.Sql.SqlCoupling

open TestPrune.AstAnalyzer

/// Whether a fact represents a read or write access.
type AccessKind =
    | Read
    | Write

/// A fact about a symbol's database access.
type SqlFact =
    { Symbol: string
      Table: string
      Column: string
      Access: AccessKind }

/// Build SharedState dependency edges from SQL access facts.
/// For each (table, column) pair, connects every writer to every reader.
let buildEdges (facts: SqlFact list) : Dependency list =
    facts
    |> List.groupBy (fun f -> f.Table, f.Column)
    |> List.collect (fun (_, group) ->
        let writers = group |> List.filter (fun f -> f.Access = Write)
        let readers = group |> List.filter (fun f -> f.Access = Read)

        [ for w in writers do
              for r in readers do
                  if w.Symbol <> r.Symbol then
                      { FromSymbol = w.Symbol
                        ToSymbol = r.Symbol
                        Kind = SharedState
                        Source = "sql" } ])
```

**Step 6: Run test**

Run: `dotnet test --filter "writer and reader of same table"`
Expected: Pass

**Step 7: Write more tests for edge cases**

Add tests for:
- Column-level: writer of `articles.status` does NOT couple to reader of `articles.title`
- Table-level wildcard: writer of `articles.*` couples to reader of `articles.status`
- Self-coupling: a function that both reads and writes same table doesn't create self-edge
- Multiple writers, multiple readers: cartesian product of edges
- No writers or no readers: empty edge list

```fsharp
    [<Fact>]
    let ``column-level coupling only matches same column`` () =
        let facts =
            [ { Symbol = "W.save"; Table = "articles"; Column = "status"; Access = Write }
              { Symbol = "R.loadTitle"; Table = "articles"; Column = "title"; Access = Read }
              { Symbol = "R.loadStatus"; Table = "articles"; Column = "status"; Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 1 @>
        test <@ edges[0].ToSymbol = "R.loadStatus" @>

    [<Fact>]
    let ``wildcard writer couples to all column readers`` () =
        let facts =
            [ { Symbol = "W.save"; Table = "articles"; Column = "*"; Access = Write }
              { Symbol = "R.loadTitle"; Table = "articles"; Column = "title"; Access = Read }
              { Symbol = "R.loadStatus"; Table = "articles"; Column = "status"; Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 2 @>

    [<Fact>]
    let ``no self-edges`` () =
        let facts =
            [ { Symbol = "Q.upsert"; Table = "articles"; Column = "*"; Access = Write }
              { Symbol = "Q.upsert"; Table = "articles"; Column = "*"; Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``no edges when only writers or only readers`` () =
        let facts =
            [ { Symbol = "W.save"; Table = "articles"; Column = "*"; Access = Write }
              { Symbol = "W.update"; Table = "articles"; Column = "*"; Access = Write } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>
```

**Step 8: Make wildcard tests pass**

Update `buildEdges` to handle wildcard matching: a `*` column matches any specific column. Group by table first, then within each table match `*` writers against all readers and vice versa.

**Step 9: Run all tests**

Run: `mise run test`
Expected: All pass.

**Step 10: Commit**

```bash
jj commit -m "feat: add TestPrune.Sql with ReadsFrom/WritesTo attributes and coupling engine"
```

---

## Task 5: Sql attribute scanning via ITestPruneExtension

Wire the attributes into the extension system so TestPrune discovers `[<ReadsFrom>]`/`[<WritesTo>]` during indexing and injects edges.

**Files:**
- Modify: `src/TestPrune.Sql/SqlExtension.fs`
- Test: `tests/TestPrune.Tests/SqlExtensionTests.fs`

**Step 1: Write failing test for attribute-based fact extraction**

This test uses FCS to analyze F# source containing the attributes and verifies facts are extracted:

```fsharp
module ``SqlExtension attribute scanning`` =

    [<Fact>]
    let ``extracts ReadsFrom facts from attributed functions`` () =
        // Test that the extension can find [<ReadsFrom>] attributes
        // in analysis results and produce SqlFacts.
        // This will need to work with FCS checked results or
        // a simpler approach: scan AnalysisResult symbols for
        // attributes in the AST.
        //
        // The exact shape depends on what data is available.
        // Start with a unit test using manually constructed facts,
        // then add integration test with real FCS analysis.
        ()
```

Note: The exact approach for attribute scanning depends on whether FCS exposes custom attributes on symbol uses. This may require reading the AST declarations. Investigate FCS's `FSharpMemberOrFunctionOrValue.Attributes` property during implementation. The test should drive the discovery.

**Step 2: Implement SqlExtension as ITestPruneExtension**

```fsharp
type SqlExtension() =
    interface ITestPruneExtension with
        member _.Name = "SQL Coupling"

        member _.AnalyzeEdges symbolStore changedFiles repoRoot =
            // 1. Get all symbols from symbol store
            // 2. For each symbol, check if it has ReadsFrom/WritesTo attributes
            // 3. Build SqlFacts from the attributes
            // 4. Run buildEdges on the facts
            // 5. Return the dependency edges
            []
```

**Step 3: Run tests, iterate**

Run: `mise run test`
Expected: All pass.

**Step 4: Commit**

```bash
jj commit -m "feat: wire SqlExtension as ITestPruneExtension with attribute scanning"
```

---

## Task 6: Integration test — end-to-end SQL coupling

Verify the full pipeline: F# code with `[<ReadsFrom>]`/`[<WritesTo>]` attributes → index → change detection → correct test selection.

**Files:**
- Test: `tests/TestPrune.Tests/SqlExtensionTests.fs`

**Step 1: Write integration test**

```fsharp
module ``SqlExtension integration`` =

    [<Fact>]
    let ``changing a writer selects tests that read same table`` () =
        withDb (fun db ->
            // Build a graph where:
            // - testA calls readerFunc (which has [<ReadsFrom("articles")>])
            // - writerFunc has [<WritesTo("articles")>]
            // - writerFunc changes
            // Expected: testA is selected (via SharedState edge from writerFunc → readerFunc)
            //
            // This test constructs AnalysisResults manually with the
            // SharedState edges that SqlExtension.buildEdges would produce,
            // verifying the full DB round-trip and test selection.
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "t1"; IsExtern = false }
                      { FullName = "Queries.getArticle"
                        Kind = Function
                        SourceFile = "src/Queries.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "q1"; IsExtern = false }
                      { FullName = "Queries.saveArticle"
                        Kind = Function
                        SourceFile = "src/Commands.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "q2"; IsExtern = false } ],
                    [ // testA calls getArticle (direct)
                      { FromSymbol = "Tests.testA"
                        ToSymbol = "Queries.getArticle"
                        Kind = Calls
                        Source = "core" }
                      // saveArticle → getArticle via shared table (injected by SqlExtension)
                      { FromSymbol = "Queries.saveArticle"
                        ToSymbol = "Queries.getArticle"
                        Kind = SharedState
                        Source = "sql" } ],
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                )

            db.RebuildProjects([ result ], [], [])

            // When saveArticle changes, testA should be affected
            // (saveArticle → getArticle → testA, via transitive closure)
            let affected = db.QueryAffectedTests([ "Queries.saveArticle" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)
```

**Step 2: Run test**

Run: `dotnet test --filter "changing a writer selects tests"`
Expected: Pass (this uses existing transitive closure machinery with new edge types)

**Step 3: Commit**

```bash
jj commit -m "test: add integration test for SQL coupling end-to-end"
```

---

## Task 7: Create TestPrune.SqlHydra project

Automate SqlFact extraction from SqlHydra typed symbol references.

**Files:**
- Create: `src/TestPrune.SqlHydra/TestPrune.SqlHydra.fsproj`
- Create: `src/TestPrune.SqlHydra/SqlHydraAnalyzer.fs`
- Modify: `TestPrune.slnx`
- Modify: `tests/TestPrune.Tests/TestPrune.Tests.fsproj`
- Test: `tests/TestPrune.Tests/SqlHydraTests.fs`

**Step 1: Create project skeleton**

Create `src/TestPrune.SqlHydra/TestPrune.SqlHydra.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <NoWarn>$(NoWarn);NU1605;MSB3277;NETSDK1188</NoWarn>
        <PackageId>TestPrune.SqlHydra</PackageId>
        <Authors>Michael Glass</Authors>
        <Description>SqlHydra integration for TestPrune — automatic SQL coupling detection from typed queries</Description>
        <PackageLicenseExpression>MIT</PackageLicenseExpression>
        <RepositoryUrl>https://github.com/michaelglass/TestPrune</RepositoryUrl>
        <RepositoryType>git</RepositoryType>
        <Version>0.1.0</Version>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="SqlHydraAnalyzer.fs" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../TestPrune.Core/TestPrune.Core.fsproj" />
        <ProjectReference Include="../TestPrune.Sql/TestPrune.Sql.fsproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Update="FSharp.Core" Version="10.1.*" />
    </ItemGroup>

</Project>
```

Add to `TestPrune.slnx`. Add test project reference.

**Step 2: Write failing test**

The SqlHydra analyzer needs to examine FCS symbol uses to detect references to SqlHydra-generated table/column types and classify them as reads or writes based on the enclosing DSL context (`selectTask` vs `insertTask` etc.).

Create `tests/TestPrune.Tests/SqlHydraTests.fs`:

```fsharp
module TestPrune.Tests.SqlHydraTests

open Xunit
open Swensen.Unquote
open TestPrune.Sql
open TestPrune.SqlHydra

module ``SqlHydra fact extraction`` =

    [<Fact>]
    let ``identifies selectTask as read access`` () =
        // Test with symbol use data that mimics SqlHydra patterns.
        // The analyzer should recognize `selectTask` context as Read.
        let context = SqlHydraAnalyzer.classifyDslContext "selectTask"
        test <@ context = Some Read @>

    [<Fact>]
    let ``identifies insertTask as write access`` () =
        let context = SqlHydraAnalyzer.classifyDslContext "insertTask"
        test <@ context = Some Write @>

    [<Fact>]
    let ``identifies updateTask as write access`` () =
        let context = SqlHydraAnalyzer.classifyDslContext "updateTask"
        test <@ context = Some Write @>

    [<Fact>]
    let ``identifies deleteTask as write access`` () =
        let context = SqlHydraAnalyzer.classifyDslContext "deleteTask"
        test <@ context = Some Write @>

    [<Fact>]
    let ``unknown context returns None`` () =
        let context = SqlHydraAnalyzer.classifyDslContext "someOtherFunction"
        test <@ context = None @>
```

**Step 3: Implement DSL context classifier**

Create `src/TestPrune.SqlHydra/SqlHydraAnalyzer.fs`:

```fsharp
module TestPrune.SqlHydra.SqlHydraAnalyzer

open TestPrune.Sql.SqlCoupling

/// Classify a SqlHydra DSL function name as Read or Write access.
let classifyDslContext (functionName: string) : AccessKind option =
    match functionName with
    | "selectTask" | "selectAsync" | "select" -> Some Read
    | "insertTask" | "insertAsync" | "insert" -> Some Write
    | "updateTask" | "updateAsync" | "update" -> Some Write
    | "deleteTask" | "deleteAsync" | "delete" -> Some Write
    | _ -> None
```

**Step 4: Run tests**

Run: `dotnet test --filter "SqlHydra"`
Expected: All pass.

**Step 5: Write test for table/column extraction from symbol names**

The harder part: given FCS symbol use data, extract which SqlHydra table/column is being referenced. SqlHydra generates types like `Generated.``public``.briefs` for tables and properties like `d.status` for columns. The analyzer needs to recognize these patterns.

This task is intentionally left less prescriptive because it depends on FCS symbol representation details. The test should use real FCS analysis of a small F# snippet containing SqlHydra-style generated types and query expressions.

```fsharp
    [<Fact>]
    let ``extracts table name from SqlHydra generated type reference`` () =
        // Given a fully-qualified symbol name like "Generated.public.briefs"
        // the analyzer should extract table = "briefs", schema = "public"
        let result = SqlHydraAnalyzer.parseTableReference "Generated.public.briefs"
        test <@ result = Some { Schema = "public"; Table = "briefs" } @>
```

**Step 6: Implement and iterate via TDD**

Continue building out the analyzer with tests driving each piece:
- Table reference parsing
- Column reference parsing (from property access symbols)
- Full `SqlFact` extraction from a set of symbol uses
- Wiring as `ITestPruneExtension`

**Step 7: Run all tests**

Run: `mise run test`
Expected: All pass.

**Step 8: Commit**

```bash
jj commit -m "feat: add TestPrune.SqlHydra with automated SQL fact extraction"
```

---

## Task 8: Surface source attribution in status output

Add provenance info to the `status` command so users can see why a test was selected.

**Files:**
- Modify: `src/TestPrune/Orchestration.fs` (status output)
- Modify: `src/TestPrune.Core/ImpactAnalysis.fs` (carry source through selection)
- Test: `tests/TestPrune.Tests/ImpactAnalysisTests.fs`

**Step 1: Write failing test**

Test that impact analysis results include the source of edges in the selection reason chain. The exact shape depends on the current `SelectionReason` type — extend it to carry source info.

**Step 2: Implement, run tests, commit**

```bash
jj commit -m "feat: surface edge source attribution in status output"
```

---

## Execution Order & Dependencies

```
Task 1 (source field) ──→ Task 2 (SharedState kind) ──→ Task 3 (extension interface)
                                                              │
                                                    ┌────────┴────────┐
                                                    ▼                 ▼
                                            Task 4 (Sql plugin)   Task 3 includes
                                                    │             Falco migration
                                                    ▼
                                            Task 5 (Sql ITestPruneExtension)
                                                    │
                                            ┌───────┴───────┐
                                            ▼               ▼
                                    Task 6 (integration)  Task 7 (SqlHydra)
                                                            │
                                                            ▼
                                                    Task 8 (status output)
```

Tasks 1-3 are strictly sequential. After Task 3, Tasks 4-5 (Sql) and the Falco migration are independent. Task 7 (SqlHydra) depends on Task 4. Task 8 can happen any time after Task 1.
