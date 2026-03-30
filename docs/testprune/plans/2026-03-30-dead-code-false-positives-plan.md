# Dead Code False Positives Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce dead-code false positives from ~108 to <10 by fixing missing dependency edges and adding verbose diagnostics.

**Architecture:** Fix edge extraction in `AstAnalyzer.fs` (DU parent from case, generic type params, record type from field), exclude DllImport functions, and add verbose diagnostics showing WHY each symbol is unreachable. All changes are in TestPrune.Core with CLI wiring in TestPrune.

**Tech Stack:** F#, FSharp.Compiler.Service, SQLite, xUnit v3, Swensen.Unquote

---

### Task 1: Add DU parent type edge — test

Add an AstAnalyzer test verifying that pattern matching on a DU case creates an edge to the parent DU type.

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs`

**Step 1: Write the failing test**

Add a new test module after the existing `Dependency extraction` module (~line 88):

```fsharp
module ``DU parent type edge from case usage`` =

    [<Fact>]
    let ``pattern matching on DU case creates edge to parent type`` () =
        let result =
            analyze
                """
module M

type Shape =
    | Circle of float
    | Square of float

let process s =
    match s with
    | Circle r -> r
    | Square s -> s
"""

        let deps = result.Dependencies
        let hasEdgeToShape =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("process", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Shape", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToShape @>

    [<Fact>]
    let ``constructing DU case creates edge to parent type`` () =
        let result =
            analyze
                """
module M

type Msg =
    | Increment
    | Decrement

let init () = Increment
"""

        let deps = result.Dependencies
        let hasEdgeToMsg =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("init", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Msg", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToMsg @>
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~DU parent type edge"`
Expected: FAIL — no `UsesType` edge to parent type exists today.

---

### Task 2: Add DU parent type edge — implementation

When a use of an `FSharpUnionCase` is encountered, also emit a `UsesType` edge to its parent DU type.

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:307-326`

**Step 1: Add helper to extract parent type from union case**

Add after `classifyDependency` (after line 132):

```fsharp
/// When a union case is used, extract the parent DU type's full name.
let private tryGetUnionParentType (symbol: FSharpSymbol) : string option =
    match symbol with
    | :? FSharpUnionCase as uc ->
        try
            Some uc.ReturnType.TypeDefinition.FullName
        with :? InvalidOperationException ->
            None
    | _ -> None
```

**Step 2: Modify dependency extraction to emit parent type edges**

In `extractResults`, replace the dependency extraction block (lines 307-326) with:

```fsharp
            let dependencies =
                allUses
                |> List.collect (fun u ->
                    if u.IsFromDefinition then
                        []
                    else
                        match classifySymbol u.Symbol with
                        | None -> []
                        | Some(_, usedFullName) ->
                            match findEnclosing u.Range with
                            | None -> []
                            | Some enclosingSi ->
                                if enclosingSi.FullName = usedFullName then
                                    []
                                else
                                    let primary =
                                        { FromSymbol = enclosingSi.FullName
                                          ToSymbol = usedFullName
                                          Kind = classifyDependency u.Symbol }

                                    let parentEdge =
                                        tryGetUnionParentType u.Symbol
                                        |> Option.bind (fun parentName ->
                                            if parentName = enclosingSi.FullName || parentName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = parentName
                                                      Kind = UsesType })

                                    primary :: (parentEdge |> Option.toList))
                |> List.distinct
```

Note: Changed from `List.choose` to `List.collect` to emit multiple edges per use.

**Step 3: Run test to verify it passes**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~DU parent type edge"`
Expected: PASS

**Step 4: Run full test suite**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass.

**Step 5: Commit**

```
feat: add edge from DU case usage to parent type

When a union case is used (pattern match or construction), also emit
a UsesType edge to the parent DU type. This prevents the parent type
from appearing as dead code when only its cases are referenced.
```

---

### Task 3: Add generic type parameter edges — test

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs`

**Step 1: Write the failing tests**

```fsharp
module ``Generic type parameter edges`` =

    [<Fact>]
    let ``using generic type with concrete arg creates edge to arg type`` () =
        let result =
            analyze
                """
module M

type MyData = { Value: int }

let items : list<MyData> = []
"""

        let deps = result.Dependencies
        let hasEdgeToMyData =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("items", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("MyData", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToMyData @>

    [<Fact>]
    let ``function with generic return type creates edge to type arg`` () =
        let result =
            analyze
                """
module M

type Config = { Host: string }

let loadConfigs () : Config list = []
"""

        let deps = result.Dependencies
        let hasEdgeToConfig =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("loadConfigs", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Config", StringComparison.Ordinal))

        test <@ hasEdgeToConfig @>

    [<Fact>]
    let ``multiple generic args each get edges`` () =
        let result =
            analyze
                """
module M

type Key = { Id: int }
type Val = { Data: string }

let lookup : Map<Key, Val> = Map.empty
"""

        let deps = result.Dependencies
        let hasEdgeToKey =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("lookup", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Key", StringComparison.Ordinal))
        let hasEdgeToVal =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("lookup", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Val", StringComparison.Ordinal))

        test <@ hasEdgeToKey @>
        test <@ hasEdgeToVal @>
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Generic type parameter edges"`
Expected: FAIL

---

### Task 4: Add generic type parameter edges — implementation

When a symbol use involves a type with generic arguments, inspect the `FSharpType` and emit `UsesType` edges to each concrete type argument's definition entity.

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs`

**Step 1: Add helper to extract type argument entities**

Add after `tryGetUnionParentType`:

```fsharp
/// Recursively extract all concrete type argument entities from a type.
let private extractGenericTypeArgs (fsharpType: FSharpType) : string list =
    let rec collect (t: FSharpType) =
        try
            [ if t.HasTypeDefinition && not t.TypeDefinition.IsFSharpModule then
                  yield t.TypeDefinition.FullName
              for arg in t.GenericArguments do
                  yield! collect arg ]
        with :? InvalidOperationException ->
            []

    try
        if fsharpType.IsGenericParameter then
            []
        else
            fsharpType.GenericArguments
            |> Seq.toList
            |> List.collect collect
    with :? InvalidOperationException ->
        []

/// Extract generic type argument edges from a symbol use.
let private tryGetGenericTypeArgEdges (symbol: FSharpSymbol) : string list =
    match symbol with
    | :? FSharpEntity as entity ->
        try
            // For entity uses like `MyType<A, B>`, extract A and B
            entity.GenericParameters |> ignore // force resolution
            []
        with :? InvalidOperationException ->
            []
    | :? FSharpMemberOrFunctionOrValue as mfv ->
        try
            extractGenericTypeArgs mfv.FullType
        with :? InvalidOperationException ->
            []
    | _ -> []
```

**Step 2: Integrate into dependency extraction**

In the dependency extraction block (the `List.collect` from Task 2), add generic type arg edges alongside the primary and parent edges. After the `parentEdge` binding, add:

```fsharp
                                    let genericArgEdges =
                                        tryGetGenericTypeArgEdges u.Symbol
                                        |> List.choose (fun argName ->
                                            if argName = enclosingSi.FullName || argName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = argName
                                                      Kind = UsesType })

                                    primary :: (parentEdge |> Option.toList) @ genericArgEdges)
```

**Step 3: Run test to verify it passes**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Generic type parameter edges"`
Expected: PASS

**Step 4: Run full test suite**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass.

**Step 5: Commit**

```
feat: add edges for generic type arguments

When a symbol use involves generic type parameters (e.g., List<MyType>),
emit UsesType edges to each concrete type argument's definition. This
prevents types used only as generic parameters from appearing as dead code.
```

---

### Task 5: Add record type edge from field usage — test

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs`

**Step 1: Write the failing test**

```fsharp
module ``Record type edge from field usage`` =

    [<Fact>]
    let ``constructing record via fields creates edge to record type`` () =
        let result =
            analyze
                """
module M

type Person = { Name: string; Age: int }

let makePerson () = { Name = "Alice"; Age = 30 }
"""

        let deps = result.Dependencies
        let hasEdgeToPerson =
            deps
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("makePerson", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Person", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToPerson @>

    [<Fact>]
    let ``accessing record field creates edge to record type`` () =
        let result =
            analyze
                """
module M

type Config = { Host: string; Port: int }

let getHost (c: Config) = c.Host
"""

        let deps = result.Dependencies
        // Should have edge to Config from the type annotation AND from field access
        let configEdges =
            deps
            |> List.filter (fun d ->
                d.FromSymbol.EndsWith("getHost", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Config", StringComparison.Ordinal))

        test <@ configEdges.Length >= 1 @>
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Record type edge from field"`
Expected: FAIL — the record construction test should fail (no edge to Person when only fields are used).

---

### Task 6: Add record type edge from field usage — implementation

When a record field (property on an FSharpRecord) is used, also emit a `UsesType` edge to the containing record type.

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs`

**Step 1: Add helper to extract record type from field**

Add after the generic type arg helpers:

```fsharp
/// When a record field is used, extract the containing record type's full name.
let private tryGetRecordTypeFromField (symbol: FSharpSymbol) : string option =
    match symbol with
    | :? FSharpMemberOrFunctionOrValue as mfv ->
        try
            match mfv.DeclaringEntity with
            | Some entity when entity.IsFSharpRecord -> Some entity.FullName
            | _ -> None
        with :? InvalidOperationException ->
            None
    | _ -> None
```

**Step 2: Integrate into dependency extraction**

In the dependency extraction block, add record type edges. After the `genericArgEdges` binding:

```fsharp
                                    let recordTypeEdge =
                                        tryGetRecordTypeFromField u.Symbol
                                        |> Option.bind (fun recName ->
                                            if recName = enclosingSi.FullName || recName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = recName
                                                      Kind = UsesType })

                                    primary :: (parentEdge |> Option.toList) @ genericArgEdges @ (recordTypeEdge |> Option.toList))
```

**Step 3: Run test to verify it passes**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Record type edge from field"`
Expected: PASS

**Step 4: Run full test suite**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass.

**Step 5: Commit**

```
feat: add edge from record field usage to record type

When a record field is accessed or used in construction, also emit a
UsesType edge to the containing record type. This prevents record types
from appearing as dead code when used only via field syntax.
```

---

### Task 7: Add DllImport exclusion — test

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs`
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs`

**Step 1: Write the AstAnalyzer test for IsExtern detection**

```fsharp
module ``DllImport detection`` =

    [<Fact>]
    let ``function with DllImport attribute is marked as extern`` () =
        let result =
            analyze
                """
module M

open System.Runtime.InteropServices

[<DllImport("native.dll")>]
extern void nativeFunc()
"""

        let externSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("nativeFunc", StringComparison.Ordinal))

        test <@ externSym.IsSome @>
        test <@ externSym.Value.IsExtern @>
```

**Step 2: Write the DeadCode test for DllImport exclusion**

```fsharp
module ``DllImport symbols excluded`` =

    [<Fact>]
    let ``extern functions are not reported as dead code`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Native.nativeFunc"
                        Kind = Function
                        SourceFile = "src/App/Native.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // nativeFunc is unreachable but IsExtern, should be excluded
            test <@ result.UnreachableSymbols |> List.isEmpty @>)
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~DllImport"`
Expected: FAIL — `IsExtern` field doesn't exist yet.

---

### Task 8: Add DllImport exclusion — implementation

Add `IsExtern` field to `SymbolInfo`, detect `[<DllImport>]` in AstAnalyzer, exclude in DeadCode, persist in Database.

**Files:**
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:24-30` (add IsExtern to SymbolInfo)
- Modify: `src/TestPrune.Core/AstAnalyzer.fs:238-348` (detect DllImport in extractResults)
- Modify: `src/TestPrune.Core/DeadCode.fs:54-62` (filter IsExtern)
- Modify: `src/TestPrune.Core/Database.fs` (persist is_extern column)

**Step 1: Add IsExtern to SymbolInfo**

In `AstAnalyzer.fs`, modify the `SymbolInfo` record:

```fsharp
type SymbolInfo =
    { FullName: string
      Kind: SymbolKind
      SourceFile: string
      LineStart: int
      LineEnd: int
      ContentHash: string
      IsExtern: bool }
```

**Step 2: Add DllImport detection helper**

After `isTestAttribute` (~line 162):

```fsharp
let private isDllImport (mfv: FSharpMemberOrFunctionOrValue) : bool =
    try
        mfv.Attributes
        |> Seq.exists (fun attr ->
            let name = attr.AttributeType.DisplayName
            name = "DllImportAttribute" || name = "DllImport")
    with :? InvalidOperationException ->
        false
```

**Step 3: Set IsExtern in extractResults**

In the `definitions` block where `SymbolInfo` records are created (~line 261), set `IsExtern`:

```fsharp
                    if u.IsFromDefinition then
                        classifySymbol u.Symbol
                        |> Option.map (fun (kind, fullName) ->
                            let isExtern =
                                match u.Symbol with
                                | :? FSharpMemberOrFunctionOrValue as mfv -> isDllImport mfv
                                | _ -> false

                            { FullName = fullName
                              Kind = kind
                              SourceFile = sourceFileName
                              LineStart = u.Range.StartLine
                              LineEnd = u.Range.EndLine
                              ContentHash = hashSourceLines source u.Range.StartLine u.Range.EndLine
                              IsExtern = isExtern },
                            u)
```

**Step 4: Filter IsExtern in DeadCode.fs**

In `findDeadCode`, add `&& not s.IsExtern` to the filter in `unreachableSymbols` (~line 62):

```fsharp
            && (includeTests
                || not (s.SourceFile.StartsWith("tests/", StringComparison.Ordinal)))
            && not s.IsExtern)
```

**Step 5: Update Database schema and queries**

Add `is_extern` column to the `symbols` table DDL, and update `GetAllSymbols`, `GetSymbolsInFile`, and `RebuildProjects` to read/write it.

In the CREATE TABLE statement, add: `is_extern INTEGER NOT NULL DEFAULT 0`

In `GetAllSymbols` reader: add `IsExtern = r.GetInt32(6) = 1`

In `RebuildProjects` insert: include `is_extern` column.

**Step 6: Fix all existing test code**

Every place that constructs a `SymbolInfo` literal (in test files and `TestHelpers.fs`) must now include `IsExtern = false`. Search for `ContentHash =` in test files and add `IsExtern = false` after each.

**Step 7: Run tests**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass including the new DllImport tests.

**Step 8: Commit**

```
feat: exclude DllImport functions from dead code report

Add IsExtern flag to SymbolInfo, detect [<DllImport>] attribute during
indexing, and filter extern symbols from the dead code report. P/Invoke
stubs are always needed at runtime regardless of static reachability.
```

---

### Task 9: Add verbose diagnostics — UnreachabilityReason type and tests

**Files:**
- Modify: `src/TestPrune.Core/DeadCode.fs`
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs`
- Modify: `tests/TestPrune.Tests/TestHelpers.fs`

**Step 1: Write the tests**

```fsharp
module ``Verbose diagnostics`` =

    [<Fact>]
    let ``symbol with no incoming edges reports NoIncomingEdges`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.orphan"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols.Length = 1 @>
            let sym = result.UnreachableSymbols[0]
            test <@ sym.Symbol.FullName = "App.Lib.orphan" @>
            test <@ sym.Reason = NoIncomingEdges @>)

    [<Fact>]
    let ``symbol with edges but disconnected reports DisconnectedFromEntryPoints`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.islandA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.islandB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Lib.islandA"
                        ToSymbol = "App.Lib.islandB"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            // islandA calls islandB but neither connects to main
            // islandA is shallowest; it should report "disconnected" because islandB has incoming edge from islandA
            // Actually islandA itself has no incoming edges. islandB has incoming from islandA.
            // But islandB is contained/filtered, so only islandA is reported.
            test <@ result.UnreachableSymbols.Length = 1 @>
            test <@ result.UnreachableSymbols[0].Reason = NoIncomingEdges @>)

    [<Fact>]
    let ``disconnected chain reports incoming sources`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.used"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.deadFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.used"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.used"
                        ToSymbol = "App.Lib.deadFunc"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            // deadFunc IS reachable (main -> used -> deadFunc), so it should NOT be in unreachable
            test <@ result.UnreachableSymbols |> List.isEmpty @>)
```

Wait — that last test actually tests that everything IS reachable (which it should be). Let me write a proper disconnected test:

```fsharp
    [<Fact>]
    let ``symbol called only from unreachable code reports DisconnectedFromEntryPoints`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Island.root"
                        Kind = Function
                        SourceFile = "src/App/Island.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Island.helper"
                        Kind = Function
                        SourceFile = "src/App/Island.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Island.root"
                        ToSymbol = "App.Island.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            // root has no incoming edges -> NoIncomingEdges (shallowest reported)
            // helper has incoming from root but root is unreachable -> DisconnectedFromEntryPoints
            // But helper is contained within root's file range... depends on line ranges.
            // With different files/ranges, both would be reported.
            // root: lines 1-5, helper: lines 7-12 — NOT contained, both reported
            test <@ result.UnreachableSymbols.Length = 2 @>

            let root = result.UnreachableSymbols |> List.find (fun s -> s.Symbol.FullName = "App.Island.root")
            let helper = result.UnreachableSymbols |> List.find (fun s -> s.Symbol.FullName = "App.Island.helper")

            test <@ root.Reason = NoIncomingEdges @>
            test <@ match helper.Reason with DisconnectedFromEntryPoints _ -> true | _ -> false @>)
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Verbose diagnostics"`
Expected: FAIL — `UnreachabilityReason` type and `runDeadCodeVerbose` don't exist yet.

---

### Task 10: Add verbose diagnostics — implementation

**Files:**
- Modify: `src/TestPrune.Core/DeadCode.fs`
- Modify: `src/TestPrune.Core/Database.fs` (add `GetIncomingEdges` method)
- Modify: `src/TestPrune.Core/Ports.fs` (add to SymbolStore)
- Modify: `src/TestPrune.Core/InMemoryStore.fs` (add in-memory impl)
- Modify: `tests/TestPrune.Tests/TestHelpers.fs` (add `runDeadCodeVerbose`)

**Step 1: Add GetIncomingEdges to Database**

In `Database.fs`, add a method to query incoming edges for a set of symbols:

```fsharp
    /// Get the names of symbols that have edges pointing TO the given symbol.
    member _.GetIncomingEdges(symbolName: string) : string list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s_from.full_name
            FROM dependencies d
            JOIN symbols s_to ON s_to.id = d.to_symbol_id
            JOIN symbols s_from ON s_from.id = d.from_symbol_id
            WHERE s_to.full_name = @name
            """

        cmd.Parameters.AddWithValue("@name", symbolName) |> ignore
        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0))
```

**Step 2: Add to Ports and InMemoryStore**

In `Ports.fs`, add to `SymbolStore`:
```fsharp
      GetIncomingEdges: string -> string list
```

In `Ports.fs` `toSymbolStore`, add:
```fsharp
      GetIncomingEdges = db.GetIncomingEdges
```

In `InMemoryStore.fs`, add:
```fsharp
      GetIncomingEdges =
        fun symbolName ->
            allDeps
            |> List.filter (fun d -> d.ToSymbol = symbolName)
            |> List.map (fun d -> d.FromSymbol)
```

**Step 3: Add UnreachabilityReason type and verbose findDeadCode**

In `DeadCode.fs`, add:

```fsharp
type UnreachabilityReason =
    | NoIncomingEdges
    | DisconnectedFromEntryPoints of incomingFrom: string list

type UnreachableSymbolInfo =
    { Symbol: SymbolInfo
      Reason: UnreachabilityReason }

type VerboseDeadCodeResult =
    { TotalSymbols: int
      ReachableSymbols: int
      UnreachableSymbols: UnreachableSymbolInfo list }

/// Find dead code with verbose reasons for why each symbol is unreachable.
let findDeadCodeVerbose
    (allSymbols: SymbolInfo list)
    (reachable: Set<string>)
    (testMethodNames: Set<string>)
    (includeTests: bool)
    (getIncomingEdges: string -> string list)
    : VerboseDeadCodeResult * AnalysisEvent list =
    let result, events = findDeadCode allSymbols reachable testMethodNames includeTests

    let verboseUnreachable =
        result.UnreachableSymbols
        |> List.map (fun s ->
            let incoming = getIncomingEdges s.FullName
            let reason =
                if List.isEmpty incoming then NoIncomingEdges
                else DisconnectedFromEntryPoints incoming
            { Symbol = s; Reason = reason })

    { TotalSymbols = result.TotalSymbols
      ReachableSymbols = result.ReachableSymbols
      UnreachableSymbols = verboseUnreachable },
    events
```

**Step 4: Add runDeadCodeVerbose to TestHelpers**

In `TestHelpers.fs`:

```fsharp
let runDeadCodeVerbose (db: Database) (patterns: string list) (includeTests: bool) =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
    let entryPoints = findEntryPoints allNames patterns
    let reachable = db.GetReachableSymbols(entryPoints)
    let testMethodNames = db.GetTestMethodSymbolNames()
    findDeadCodeVerbose allSymbols reachable testMethodNames includeTests db.GetIncomingEdges
```

**Step 5: Run tests**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Verbose diagnostics"`
Expected: PASS

**Step 6: Commit**

```
feat: add verbose dead code diagnostics

Add UnreachabilityReason type that distinguishes NoIncomingEdges (likely
dead or missing edge) from DisconnectedFromEntryPoints (edge exists but
chain doesn't reach entry point). Queryable via findDeadCodeVerbose.
```

---

### Task 11: Wire verbose diagnostics into CLI

**Files:**
- Modify: `src/TestPrune/Program.fs` (add `--verbose` flag)
- Modify: `src/TestPrune/Orchestration.fs` (use findDeadCodeVerbose when verbose)

**Step 1: Add --verbose flag to parseDeadCodeFlags**

In `Program.fs`, modify `parseDeadCodeFlags` to accept a `verbose` parameter:

```fsharp
let rec private parseDeadCodeFlags (args: string list) (acc: string list) (includeTests: bool) (verbose: bool) =
    match args with
    | "--entry" :: pattern :: rest -> parseDeadCodeFlags rest (pattern :: acc) includeTests verbose
    | "--include-tests" :: rest -> parseDeadCodeFlags rest acc true verbose
    | "--verbose" :: rest -> parseDeadCodeFlags rest acc includeTests true
    | [] -> Ok(acc |> List.rev, includeTests, verbose)
    | unknown :: _ -> Error $"Unknown flag: %s{unknown}"
```

Update `Command` type:

```fsharp
    | DeadCodeCmd of entryPatterns: string list * includeTests: bool * verbose: bool
```

Update `parseArgs`:

```fsharp
            | "dead-code" :: rest ->
                match parseDeadCodeFlags rest [] false false with
                | Ok([], includeTests, verbose) -> Ok(DeadCodeCmd(defaultEntryPatterns, includeTests, verbose))
                | Ok(patterns, includeTests, verbose) -> Ok(DeadCodeCmd(patterns, includeTests, verbose))
                | Error msg -> Error msg
```

**Step 2: Update Orchestration.fs to use verbose output**

Modify `runDeadCode` to accept `verbose: bool` parameter. When verbose, use `findDeadCodeVerbose` and print reasons:

```fsharp
let runDeadCode (repoRoot: string) (entryPatterns: string list) (includeTests: bool) (verbose: bool) (auditSink: AuditSink) : int =
    // ... existing setup ...

    if verbose then
        let result, events = findDeadCodeVerbose allSymbols reachable testMethodNames includeTests store.GetIncomingEdges
        // ... print with reasons ...
        for s in symbols |> List.sortBy (fun s -> s.Symbol.LineStart) do
            let reason =
                match s.Reason with
                | NoIncomingEdges -> "no incoming edges"
                | DisconnectedFromEntryPoints sources ->
                    let names = sources |> List.truncate 3 |> String.concat ", "
                    $"has edges from: %s{names}"
            printfn $"    - %s{s.Symbol.FullName} (%A{s.Symbol.Kind}, line %d{s.Symbol.LineStart}) — %s{reason}"
    else
        // ... existing non-verbose output ...
```

Update the call site:

```fsharp
    | DeadCodeCmd(patterns, includeTests, verbose) ->
        let auditSink = createAuditSinkForRepo repoRoot
        runDeadCode repoRoot patterns includeTests verbose auditSink
```

Update help text:

```fsharp
    printfn "  --verbose           Show why each symbol is unreachable"
```

**Step 3: Run full test suite**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass.

**Step 4: Commit**

```
feat: add --verbose flag to dead-code command

Shows why each unreachable symbol is unreachable: "no incoming edges"
(truly isolated) or "has edges from: X, Y" (connected but disconnected
from entry points, suggesting a missing edge upstream).
```

---

### Task 12: Dead-code integration tests for new edge patterns

Add end-to-end dead-code tests verifying that the new edges make symbols reachable.

**Files:**
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs`

**Step 1: Write the tests**

```fsharp
module ``DU type reachable via case usage`` =

    [<Fact>]
    let ``DU type is reachable when its cases are pattern matched`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Domain.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Domain.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2; LineEnd = 2
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.process"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.process"
                        Kind = Calls }
                      // process pattern matches Circle (edge to case)
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape.Circle"
                        Kind = PatternMatches }
                      // NEW: process also has edge to parent type Shape
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape"
                        Kind = UsesType } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Shape should now be reachable via the UsesType edge from process
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Generic type arg reachable`` =

    [<Fact>]
    let ``type used as generic parameter is reachable`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Domain.Config"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 3
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.loadAll"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.loadAll"
                        Kind = Calls }
                      // loadAll returns list<Config> — edge to Config from generic arg
                      { FromSymbol = "App.Lib.loadAll"
                        ToSymbol = "App.Domain.Config"
                        Kind = UsesType } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Record type reachable via field usage`` =

    [<Fact>]
    let ``record type is reachable when its fields are used`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Domain.Person"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 3
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.greet"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.greet"
                        Kind = Calls }
                      // greet constructs Person via fields — edge to Person type
                      { FromSymbol = "App.Lib.greet"
                        ToSymbol = "App.Domain.Person"
                        Kind = UsesType } ]
                  TestMethods = [] }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~reachable"`
Expected: PASS (these use manually constructed graphs with the new edges already present).

**Step 3: Commit**

```
test: add dead-code integration tests for new edge patterns

Verify that DU types, generic type args, and record types are correctly
marked reachable when the new dependency edges are present in the graph.
```

---

### Task 13: Test impact tests — changed symbol affects tests

Add tests verifying that edge fixes improve test selection (changed symbol -> correct set of affected tests).

**Files:**
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs` (or a new section in `ImpactAnalysisTests.fs`)

**Step 1: Write the tests**

Check which file contains impact analysis tests first. Add these test scenarios:

```fsharp
module ``Edge coverage for test impact`` =

    [<Fact>]
    let ``changed DU type affects test that pattern matches its cases`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Domain.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Domain.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2; LineEnd = 2
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.process"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "Tests.ShapeTests.test_process"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.ShapeTests.test_process"
                        ToSymbol = "App.Lib.process"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape.Circle"
                        Kind = PatternMatches }
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.ShapeTests.test_process"
                        TestProject = "Tests"
                        TestClass = "ShapeTests"
                        TestMethod = "test_process" } ] }

            db.RebuildProjects([ graph ])

            // Change the Shape type -> should affect test_process
            let affected = db.QueryAffectedTests([ "App.Domain.Shape" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test_process" @>)

    [<Fact>]
    let ``changed type used as generic arg affects test that uses the generic`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Domain.Config"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 3
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.loadConfigs"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "Tests.ConfigTests.test_load"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.ConfigTests.test_load"
                        ToSymbol = "App.Lib.loadConfigs"
                        Kind = Calls }
                      // loadConfigs returns list<Config> — edge from generic arg
                      { FromSymbol = "App.Lib.loadConfigs"
                        ToSymbol = "App.Domain.Config"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.ConfigTests.test_load"
                        TestProject = "Tests"
                        TestClass = "ConfigTests"
                        TestMethod = "test_load" } ] }

            db.RebuildProjects([ graph ])

            let affected = db.QueryAffectedTests([ "App.Domain.Config" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test_load" @>)

    [<Fact>]
    let ``changed symbol affects two test classes`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Shared.helper"
                        Kind = Function
                        SourceFile = "src/App/Shared.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "Tests.AlphaTests.test_alpha"
                        Kind = Function
                        SourceFile = "tests/Alpha.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "Tests.BetaTests.test_beta"
                        Kind = Function
                        SourceFile = "tests/Beta.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.AlphaTests.test_alpha"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls }
                      { FromSymbol = "Tests.BetaTests.test_beta"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.AlphaTests.test_alpha"
                        TestProject = "Tests"
                        TestClass = "AlphaTests"
                        TestMethod = "test_alpha" }
                      { SymbolFullName = "Tests.BetaTests.test_beta"
                        TestProject = "Tests"
                        TestClass = "BetaTests"
                        TestMethod = "test_beta" } ] }

            db.RebuildProjects([ graph ])

            let affected = db.QueryAffectedTests([ "App.Shared.helper" ])
            let methods = affected |> List.map (fun t -> t.TestMethod) |> List.sort
            test <@ methods = [ "test_alpha"; "test_beta" ] @>)

    [<Fact>]
    let ``changed record type affects test via field usage edge`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Domain.Person"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1; LineEnd = 3
                        ContentHash = ""; IsExtern = false }
                      { FullName = "App.Lib.greet"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false }
                      { FullName = "Tests.GreetTests.test_greet"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1; LineEnd = 5
                        ContentHash = ""; IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.GreetTests.test_greet"
                        ToSymbol = "App.Lib.greet"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.greet"
                        ToSymbol = "App.Domain.Person"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.GreetTests.test_greet"
                        TestProject = "Tests"
                        TestClass = "GreetTests"
                        TestMethod = "test_greet" } ] }

            db.RebuildProjects([ graph ])

            let affected = db.QueryAffectedTests([ "App.Domain.Person" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test_greet" @>)
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests --filter "FullyQualifiedName~Edge coverage for test impact"`
Expected: PASS (these tests use manually-constructed graphs that include the new edges).

**Step 3: Commit**

```
test: add test impact tests for new edge patterns

Verify that changing a DU type, generic type arg, record type, or shared
dependency correctly selects the right test classes. Covers 1-to-1 and
1-to-many test selection scenarios.
```

---

### Task 14: Final validation — run all tests and format

**Step 1: Run full test suite**

Run: `dotnet test tests/TestPrune.Tests`
Expected: All tests pass.

**Step 2: Format code**

Run: `mise run format`

**Step 3: Lint**

Run: `mise run lint`

**Step 4: Commit any formatting changes**

```
style: format code with Fantomas
```

---

Plan complete and saved to `docs/plans/2026-03-30-dead-code-false-positives-plan.md`. Two execution options:

**1. Subagent-Driven (this session)** - I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** - Open new session with executing-plans, batch execution with checkpoints

Which approach?