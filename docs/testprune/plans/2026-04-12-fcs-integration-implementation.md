# FCS Integration & Status Provenance Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Automate SQL fact extraction via generic attribute storage and SqlHydra graph analysis, and show edge source provenance in status output.

**Architecture:** Three independent streams. (A) Core extracts all symbol attributes into a `symbol_attributes` table; Sql extension queries it for `ReadsFrom`/`WritesTo`. (B) SqlHydra extension post-processes existing dependency edges to detect SqlHydra query patterns. (D) Status command queries distinct edge sources in the transitive closure for each affected test.

**Tech Stack:** F# / .NET 10, SQLite, FSharp.Compiler.Service, xUnit v3, Unquote, jj

**VCS:** jj — use `jj commit -m "..."` (not git).

**Test commands:** `dotnet test` or `mise run test`

**Build command:** `dotnet build` or `mise run build`

---

## Stream A: Generic Attribute Extraction

### Task A1: Add symbol_attributes table and SymbolStore query

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:97-109` — add `Attributes` field to `AnalysisResult`
- Modify: `src/TestPrune.Core/Database.fs:9-68` — add schema table
- Modify: `src/TestPrune.Core/Database.fs:141` — bump SchemaVersion to 3
- Modify: `src/TestPrune.Core/Database.fs:198-362` — insert attributes in `RebuildProjects`
- Modify: `src/TestPrune.Core/Ports.fs:7-18` — add `GetAttributesForSymbol` to `SymbolStore`
- Modify: `src/TestPrune.Core/Ports.fs:25-36` — wire it in `toSymbolStore`
- Modify: `src/TestPrune.Core/InMemoryStore.fs` — implement for in-memory store
- Modify: `tests/TestPrune.Tests/TestHelpers.fs:46-88` — add `Attributes` to `standardGraph`
- Test: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Define SymbolAttribute type and add to AnalysisResult**

In `src/TestPrune.Core/AstAnalyzer.fs`, add before `AnalysisDiagnostics`:

```fsharp
/// A custom attribute on a symbol, with its name and JSON-encoded constructor arguments.
type SymbolAttribute =
    { SymbolFullName: string
      AttributeName: string
      ArgsJson: string }
```

Update `AnalysisResult`:

```fsharp
type AnalysisResult =
    { Symbols: SymbolInfo list
      Dependencies: Dependency list
      TestMethods: TestMethodInfo list
      Attributes: SymbolAttribute list
      Diagnostics: AnalysisDiagnostics }

    static member Create(symbols, dependencies, testMethods) =
        { Symbols = symbols
          Dependencies = dependencies
          TestMethods = testMethods
          Attributes = []
          Diagnostics = AnalysisDiagnostics.Zero }
```

**Step 2: Write failing test for attribute storage**

In `tests/TestPrune.Tests/DatabaseTests.fs`, add a new module:

```fsharp
module ``Symbol attribute storage`` =

    [<Fact>]
    let ``attributes are stored and retrieved`` () =
        withDb (fun db ->
            let result =
                { AnalysisResult.Create(
                      [ { FullName = "Queries.getArticle"
                          Kind = Function
                          SourceFile = "src/Queries.fs"
                          LineStart = 1
                          LineEnd = 5
                          ContentHash = "abc"
                          IsExtern = false } ],
                      [],
                      []
                  ) with
                    Attributes =
                        [ { SymbolFullName = "Queries.getArticle"
                            AttributeName = "ReadsFromAttribute"
                            ArgsJson = "[\"articles\", \"status\"]" } ] }

            db.RebuildProjects([ result ])
            let store = TestPrune.Ports.toSymbolStore db
            let attrs = store.GetAttributesForSymbol "Queries.getArticle"
            test <@ attrs.Length = 1 @>
            test <@ fst attrs[0] = "ReadsFromAttribute" @>
            test <@ snd attrs[0] = "[\"articles\", \"status\"]" @>)

    [<Fact>]
    let ``multiple attributes on same symbol`` () =
        withDb (fun db ->
            let result =
                { AnalysisResult.Create(
                      [ { FullName = "Queries.upsert"
                          Kind = Function
                          SourceFile = "src/Queries.fs"
                          LineStart = 1
                          LineEnd = 5
                          ContentHash = "abc"
                          IsExtern = false } ],
                      [],
                      []
                  ) with
                    Attributes =
                        [ { SymbolFullName = "Queries.upsert"
                            AttributeName = "ReadsFromAttribute"
                            ArgsJson = "[\"articles\"]" }
                          { SymbolFullName = "Queries.upsert"
                            AttributeName = "WritesToAttribute"
                            ArgsJson = "[\"articles\"]" } ] }

            db.RebuildProjects([ result ])
            let store = TestPrune.Ports.toSymbolStore db
            let attrs = store.GetAttributesForSymbol "Queries.upsert"
            test <@ attrs.Length = 2 @>)

    [<Fact>]
    let ``symbol with no attributes returns empty`` () =
        withDb (fun db ->
            db.RebuildProjects([ standardGraph ])
            let store = TestPrune.Ports.toSymbolStore db
            let attrs = store.GetAttributesForSymbol "Lib.funcB"
            test <@ attrs.IsEmpty @>)
```

**Step 3: Run test — should fail (compile errors)**

Run: `dotnet build`
Expected: Errors about `Attributes` field not in `AnalysisResult`, `GetAttributesForSymbol` not in `SymbolStore`

**Step 4: Implement schema, DB operations, and SymbolStore query**

Add to schema in `Database.fs`:

```sql
CREATE TABLE IF NOT EXISTS symbol_attributes (
    symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    attribute_name TEXT NOT NULL,
    args_json TEXT NOT NULL DEFAULT '[]',
    PRIMARY KEY (symbol_id, attribute_name, args_json)
);
CREATE INDEX IF NOT EXISTS idx_symbol_attrs_by_symbol ON symbol_attributes (symbol_id);
```

Bump `SchemaVersion` to 3.

Add attribute insertion after test_methods insertion in `RebuildProjects`:

```fsharp
use attrCmd = conn.CreateCommand()
attrCmd.Transaction <- txn

attrCmd.CommandText <-
    """
    INSERT OR IGNORE INTO symbol_attributes (symbol_id, attribute_name, args_json)
    SELECT id, @attrName, @argsJson
    FROM symbols WHERE full_name = @symbolFullName
    """

let pAttrSymbol = attrCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
let pAttrName = attrCmd.Parameters.Add("@attrName", SqliteType.Text)
let pArgsJson = attrCmd.Parameters.Add("@argsJson", SqliteType.Text)

for result in results do
    for attr in result.Attributes do
        pAttrSymbol.Value <- attr.SymbolFullName
        pAttrName.Value <- attr.AttributeName
        pArgsJson.Value <- attr.ArgsJson
        attrCmd.ExecuteNonQuery() |> ignore
```

Add query method to `Database`:

```fsharp
member _.GetAttributesForSymbol(symbolFullName: string) : (string * string) list =
    use conn = openConnection dbPath
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        """
        SELECT sa.attribute_name, sa.args_json
        FROM symbol_attributes sa
        JOIN symbols s ON s.id = sa.symbol_id
        WHERE s.full_name = @symbolFullName
        """

    cmd.Parameters.AddWithValue("@symbolFullName", symbolFullName) |> ignore
    use reader = cmd.ExecuteReader()
    readAll reader (fun r -> r.GetString(0), r.GetString(1))
```

Add to `SymbolStore` in `Ports.fs`:

```fsharp
type SymbolStore =
    { // ... existing fields ...
      GetAttributesForSymbol: string -> (string * string) list }
```

Wire in `toSymbolStore`:

```fsharp
GetAttributesForSymbol = db.GetAttributesForSymbol
```

Add to `InMemoryStore.fs`:

```fsharp
let attrsBySymbol =
    results
    |> List.collect (fun r -> r.Attributes)
    |> List.groupBy (fun a -> a.SymbolFullName)
    |> Map.ofList

// In the record:
GetAttributesForSymbol =
    fun symbolName ->
        attrsBySymbol
        |> Map.tryFind symbolName
        |> Option.defaultValue []
        |> List.map (fun a -> a.AttributeName, a.ArgsJson)
```

Update `standardGraph` in `TestHelpers.fs` — add `Attributes = []` if using record syntax directly (the `Create` static method already defaults it).

**Step 5: Fix all compilation errors, run tests**

Run: `mise run test`
Expected: All pass.

**Step 6: Commit**

```bash
jj commit -m "feat: add generic symbol attribute extraction and storage"
```

---

### Task A2: Extract attributes from FCS during analysis

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:742-802` — extract attributes in `extractResults`
- Test: `tests/TestPrune.Tests/AstAnalyzerTests.fs`

**Step 1: Write failing test**

Find a test in `AstAnalyzerTests.fs` that analyzes F# source with FCS. Add a test that analyzes source code containing a custom attribute and verifies it appears in `result.Attributes`.

```fsharp
module ``Custom attribute extraction`` =

    [<Fact>]
    let ``extracts custom attributes with constructor arguments`` () =
        // Analyze source that has a function with a custom attribute
        let source =
            """
module TestModule

open System

[<AttributeUsage(AttributeTargets.Method, AllowMultiple = true)>]
type ReadsFromAttribute(table: string, column: string) =
    inherit Attribute()
    member _.Table = table
    member _.Column = column

[<ReadsFrom("articles", "status")>]
let getArticles () = ()
"""

        let result = analyzeTestSource "test.fs" source
        let attrs = result.Attributes

        let readsFromAttrs =
            attrs |> List.filter (fun a -> a.AttributeName = "ReadsFromAttribute")

        test <@ readsFromAttrs.Length = 1 @>
        test <@ readsFromAttrs[0].SymbolFullName.EndsWith("getArticles") @>
        test <@ readsFromAttrs[0].ArgsJson.Contains("articles") @>
        test <@ readsFromAttrs[0].ArgsJson.Contains("status") @>
```

Note: `analyzeTestSource` is a helper — check `AstAnalyzerTests.fs` for how tests currently analyze F# source. Adapt the test to use the existing test infrastructure.

**Step 2: Run test — should fail (Attributes always empty)**

Run: `dotnet test --filter "extracts custom attributes"`
Expected: FAIL — `readsFromAttrs.Length = 0`

**Step 3: Implement attribute extraction**

In `AstAnalyzer.fs`, in the `extractResults` function, after the `testMethods` extraction (around line 766), add:

```fsharp
let attributes =
    allUses
    |> List.choose (fun u ->
        if u.IsFromDefinition then
            match u.Symbol with
            | :? FSharpMemberOrFunctionOrValue as mfv ->
                try
                    let attrs =
                        mfv.Attributes
                        |> Seq.choose (fun attr ->
                            try
                                let name = attr.AttributeType.DisplayName
                                let args =
                                    attr.ConstructorArguments
                                    |> Seq.map (fun (_ty, value) ->
                                        match value with
                                        | :? string as s -> $"\"%s{s}\""
                                        | v -> string v)
                                    |> String.concat ", "
                                let argsJson = $"[%s{args}]"
                                Some
                                    { SymbolFullName = mfv.FullName
                                      AttributeName = name
                                      ArgsJson = argsJson }
                            with _ -> None)
                        |> Seq.toList

                    if attrs.IsEmpty then None else Some attrs
                with :? InvalidOperationException -> None
            | _ -> None
        else
            None)
    |> List.collect id
```

Then include `Attributes = attributes` in the `Ok` result at line 796.

**Step 4: Run tests**

Run: `mise run test`
Expected: All pass.

**Step 5: Commit**

```bash
jj commit -m "feat: extract custom attributes from FCS during analysis"
```

---

### Task A3: Sql extension auto-discovers attributes from SymbolStore

**Files:**
- Modify: `src/TestPrune.Sql/SqlExtension.fs`
- Test: `tests/TestPrune.Tests/SqlCouplingTests.fs`

**Step 1: Write failing test**

```fsharp
module ``SqlExtension auto-discovery`` =

    [<Fact>]
    let ``discovers ReadsFrom and WritesTo from symbol attributes`` () =
        // Build an in-memory store with attributes
        let result =
            { AnalysisResult.Create(
                  [ { FullName = "Queries.save"
                      Kind = Function
                      SourceFile = "src/Queries.fs"
                      LineStart = 1; LineEnd = 5
                      ContentHash = "a"; IsExtern = false }
                    { FullName = "Queries.load"
                      Kind = Function
                      SourceFile = "src/Queries.fs"
                      LineStart = 6; LineEnd = 10
                      ContentHash = "b"; IsExtern = false } ],
                  [],
                  []
              ) with
                Attributes =
                    [ { SymbolFullName = "Queries.save"
                        AttributeName = "WritesToAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" }
                      { SymbolFullName = "Queries.load"
                        AttributeName = "ReadsFromAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" } ] }

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let extension = SqlExtension()
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store [] ""
        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
```

**Step 2: Run test — should fail**

Expected: `SqlExtension()` constructor doesn't exist (still takes `SqlFact list`)

**Step 3: Update SqlExtension to query SymbolStore**

```fsharp
type SqlExtension() =

    let parseArgsJson (json: string) : string list =
        // Simple parser for ["arg1", "arg2"] format
        json.Trim('[', ']').Split(',')
        |> Array.map (fun s -> s.Trim().Trim('"'))
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

    let extractFacts (symbolStore: SymbolStore) : SqlFact list =
        symbolStore.GetAllSymbols()
        |> List.collect (fun sym ->
            let attrs = symbolStore.GetAttributesForSymbol sym.FullName

            attrs
            |> List.choose (fun (attrName, argsJson) ->
                let args = parseArgsJson argsJson

                match attrName with
                | "ReadsFromAttribute"
                | "ReadsFrom" ->
                    let table = args |> List.tryHead |> Option.defaultValue ""
                    let column = args |> List.tryItem 1 |> Option.defaultValue "*"
                    Some { Symbol = sym.FullName; Table = table; Column = column; Access = Read }
                | "WritesToAttribute"
                | "WritesTo" ->
                    let table = args |> List.tryHead |> Option.defaultValue ""
                    let column = args |> List.tryItem 1 |> Option.defaultValue "*"
                    Some { Symbol = sym.FullName; Table = table; Column = column; Access = Write }
                | _ -> None))

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            let facts = extractFacts symbolStore
            SqlCoupling.buildEdges facts
```

**Step 4: Fix existing tests that used the old constructor**

Update `SqlCouplingTests.fs` — the `SqlExtension as ITestPruneExtension` tests that passed `SqlFact list` to the constructor need updating. Either remove them (covered by auto-discovery test) or test `SqlCoupling.buildEdges` directly.

**Step 5: Run tests**

Run: `mise run test`
Expected: All pass.

**Step 6: Commit**

```bash
jj commit -m "feat: SqlExtension auto-discovers ReadsFrom/WritesTo from symbol attributes"
```

---

## Stream B: SqlHydra Graph-Based Fact Extraction

### Task B1: SqlHydraExtension analyzes dependency graph

**Files:**
- Modify: `src/TestPrune.SqlHydra/SqlHydraAnalyzer.fs`
- Test: `tests/TestPrune.Tests/SqlHydraAnalyzerTests.fs`

**Step 1: Write failing test**

```fsharp
module ``SqlHydraExtension graph analysis`` =

    [<Fact>]
    let ``detects read when function calls selectTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1; LineEnd = 10
                    ContentHash = "a"; IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0; LineEnd = 0
                    ContentHash = ""; IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1; LineEnd = 5
                    ContentHash = "t"; IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let extension = SqlHydraExtension("Generated")
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store [] ""

        // No edges yet — we need a writer too. But facts should be extractable.
        // Test the fact extraction directly:
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Read @>

    [<Fact>]
    let ``detects write when function calls insertTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1; LineEnd = 10
                    ContentHash = "a"; IsExtern = false }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0; LineEnd = 0
                    ContentHash = ""; IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1; LineEnd = 5
                    ContentHash = "t"; IsExtern = false } ],
                [ { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Write @>

    [<Fact>]
    let ``produces SharedState edges when reader and writer exist`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1; LineEnd = 10
                    ContentHash = "a"; IsExtern = false }
                  { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1; LineEnd = 10
                    ContentHash = "b"; IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0; LineEnd = 0
                    ContentHash = ""; IsExtern = true }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0; LineEnd = 0
                    ContentHash = ""; IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1; LineEnd = 5
                    ContentHash = "t"; IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls; Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType; Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls; Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType; Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let extension = SqlHydraExtension("Generated")
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store [] ""
        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql-hydra" @>

    [<Fact>]
    let ``ignores functions that use table type but no DSL function`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Helpers.mapArticle"
                    Kind = Function
                    SourceFile = "src/Helpers.fs"
                    LineStart = 1; LineEnd = 5
                    ContentHash = "a"; IsExtern = false }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1; LineEnd = 5
                    ContentHash = "t"; IsExtern = false } ],
                [ { FromSymbol = "Helpers.mapArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType; Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.IsEmpty @>
```

**Step 2: Run test — should fail (SqlHydraExtension not defined)**

**Step 3: Implement SqlHydraExtension**

In `src/TestPrune.SqlHydra/SqlHydraAnalyzer.fs`, add after the existing module:

```fsharp
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// Extension that detects SqlHydra query patterns in the dependency graph
/// and produces SharedState edges via SqlCoupling.
type SqlHydraExtension(generatedModulePrefix: string) =

    /// Extract SqlFacts by analyzing the dependency graph for SqlHydra patterns.
    /// A function is classified as a reader/writer if it:
    /// 1. Has a Calls edge to a SqlHydra DSL function (selectTask, insertTask, etc.)
    /// 2. Has a UsesType edge to a type matching the generated module prefix
    static member extractFacts (generatedModulePrefix: string) (store: SymbolStore) : SqlFact list =
        let allSymbols = store.GetAllSymbols()

        allSymbols
        |> List.collect (fun sym ->
            if sym.IsExtern then
                []
            else
                let deps =
                    store.GetDependenciesFromFile sym.SourceFile
                    |> List.filter (fun d -> d.FromSymbol = sym.FullName)

                // Find DSL function calls (selectTask, insertTask, etc.)
                let dslAccess =
                    deps
                    |> List.choose (fun d ->
                        if d.Kind = Calls then
                            let funcName =
                                let i = d.ToSymbol.LastIndexOf('.')
                                if i >= 0 then d.ToSymbol.[i + 1 ..] else d.ToSymbol

                            classifyDslContext funcName
                        else
                            None)
                    |> List.tryHead

                // Find SqlHydra table type references
                let tableRefs =
                    deps
                    |> List.choose (fun d ->
                        if d.Kind = UsesType && d.ToSymbol.Contains(generatedModulePrefix) then
                            parseTableReference d.ToSymbol
                        else
                            None)

                match dslAccess with
                | Some access ->
                    tableRefs
                    |> List.map (fun tref ->
                        { Symbol = sym.FullName
                          Table = tref.Table
                          Column = "*"
                          Access = access })
                | None -> [])

    interface ITestPruneExtension with
        member _.Name = "SqlHydra"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            let facts = SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            SqlCoupling.buildEdges facts
            |> List.map (fun d -> { d with Source = "sql-hydra" })
```

**Step 4: Run tests**

Run: `mise run test`
Expected: All pass.

**Step 5: Commit**

```bash
jj commit -m "feat: SqlHydraExtension detects query patterns from dependency graph"
```

---

## Stream D: Status Provenance

### Task D1: Query edge sources for affected tests

**Files:**
- Modify: `src/TestPrune.Core/Database.fs` — add `QueryEdgeSourcesForTest` method
- Modify: `src/TestPrune.Core/Ports.fs` — add to `SymbolStore`
- Modify: `src/TestPrune.Core/InMemoryStore.fs` — implement
- Test: `tests/TestPrune.Tests/DatabaseTests.fs`

**Step 1: Write failing test**

```fsharp
module ``Edge source provenance`` =

    [<Fact>]
    let ``returns distinct sources in transitive path`` () =
        withDb (fun db ->
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "t1"; IsExtern = false }
                      { FullName = "Service.process"
                        Kind = Function
                        SourceFile = "src/Service.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "s1"; IsExtern = false }
                      { FullName = "Queries.readItems"
                        Kind = Function
                        SourceFile = "src/Queries.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "q1"; IsExtern = false }
                      { FullName = "Jobs.writeItems"
                        Kind = Function
                        SourceFile = "src/Jobs.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = "j1"; IsExtern = false } ],
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Service.process"
                        Kind = Calls; Source = "core" }
                      { FromSymbol = "Service.process"
                        ToSymbol = "Queries.readItems"
                        Kind = Calls; Source = "core" }
                      { FromSymbol = "Queries.readItems"
                        ToSymbol = "Jobs.writeItems"
                        Kind = SharedState; Source = "sql" } ],
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                )

            db.RebuildProjects([ result ])
            let sources = db.QueryEdgeSourcesForTest("Tests.testA", [ "Jobs.writeItems" ])
            test <@ sources |> Set.ofList = set [ "core"; "sql" ] @>)

    [<Fact>]
    let ``returns only core for pure AST path`` () =
        withDb (fun db ->
            db.RebuildProjects([ standardGraph ])
            let sources = db.QueryEdgeSourcesForTest("Tests.testA", [ "Domain.TypeC" ])
            test <@ sources = [ "core" ] @>)
```

**Step 2: Run test — should fail**

**Step 3: Implement QueryEdgeSourcesForTest**

In `Database.fs`:

```fsharp
/// Get distinct edge sources in the transitive path from changed symbols to a test.
member _.QueryEdgeSourcesForTest(testSymbolName: string, changedSymbolNames: string list) : string list =
    if changedSymbolNames.IsEmpty then
        []
    else
        use conn = openConnection dbPath
        let paramNames = changedSymbolNames |> List.mapi (fun i _ -> $"@p%d{i}")
        let placeholders = String.Join(", ", paramNames)
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            $"""
            WITH RECURSIVE transitive_path AS (
                SELECT from_symbol_id, source FROM dependencies WHERE to_symbol_id IN (
                    SELECT id FROM symbols WHERE full_name IN (%s{placeholders})
                )
                UNION
                SELECT d.from_symbol_id, d.source FROM dependencies d
                JOIN transitive_path tp ON d.to_symbol_id = tp.from_symbol_id
            )
            SELECT DISTINCT tp.source
            FROM transitive_path tp
            JOIN symbols s ON s.id = tp.from_symbol_id
            WHERE EXISTS (
                SELECT 1 FROM symbols ts
                JOIN test_methods tm ON tm.symbol_id = ts.id
                WHERE ts.full_name = @testSymbol
            )
            """

        changedSymbolNames
        |> List.iteri (fun i name -> cmd.Parameters.AddWithValue($"@p%d{i}", name) |> ignore)

        cmd.Parameters.AddWithValue("@testSymbol", testSymbolName) |> ignore
        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0))
```

Add to `SymbolStore` and wire in `toSymbolStore` and `InMemoryStore`.

**Step 4: Run tests**

Run: `mise run test`
Expected: All pass.

**Step 5: Commit**

```bash
jj commit -m "feat: add QueryEdgeSourcesForTest for provenance tracking"
```

---

### Task D2: Show source set in status output

**Files:**
- Modify: `src/TestPrune/Orchestration.fs:520-551` — add source info to status output
- Test: `tests/TestPrune.Tests/ProgramTests.fs`

**Step 1: Write failing test**

Find an existing status output test in `ProgramTests.fs` and add a variant that checks for source annotation in output. Or add a new test.

**Step 2: Update status output**

In `Orchestration.fs`, the `runStatusWith` function's `RunSubset tests` branch currently prints test names. After printing each test method, also query and print edge sources:

```fsharp
| RunSubset tests ->
    printfn $"Would run %d{tests.Length} test(s):"

    let byProject = tests |> List.groupBy (fun t -> t.TestProject)

    for (projName, projTests) in byProject do
        printfn $"  %s{projName}:"

        let byClass = projTests |> List.groupBy (fun t -> t.TestClass)

        for (cls, methods) in byClass do
            printfn $"    %s{cls}"

            for m in methods do
                let sources = store.QueryEdgeSourcesForTest(m.SymbolFullName, changedSymbolNames)
                let sourceTag =
                    if sources.IsEmpty || sources = [ "core" ] then ""
                    else
                        let s = sources |> List.sort |> String.concat ", "
                        $"  [sources: %s{s}]"
                printfn $"      - %s{m.TestMethod}%s{sourceTag}"
```

Note: this requires `changedSymbolNames` to be available in the status handler. The `withAnalysis` callback currently receives `TestSelection * string list` (selection + changed files). The changed symbol names would need to be threaded through or recomputed. Adjust as needed — the exact wiring depends on what's available in scope.

**Step 3: Run tests**

Run: `mise run test`
Expected: All pass.

**Step 4: Commit**

```bash
jj commit -m "feat: show edge source provenance in status output"
```

---

## Execution Order & Dependencies

```
Stream A: A1 (schema + store) ──→ A2 (FCS extraction) ──→ A3 (SqlExtension auto-discovery)

Stream B: B1 (SqlHydraExtension graph analysis)
          — depends on InMemoryStore having GetDependenciesFromFile, which exists

Stream D: D1 (QueryEdgeSourcesForTest) ──→ D2 (status output)
```

All three streams are independent. Within a stream, tasks are sequential.
A1 must land before A3. B1 and D1 can start immediately.
