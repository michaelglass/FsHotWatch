# Dead Code False Positives Regression Tests

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add regression tests for all 6 false positive patterns from TODO-dead-code-false-positives.md, fix any real bugs, delete the TODO.

**Architecture:** Two test layers — graph-level tests in DeadCodeTests.fs (construct AnalysisResult manually, insert into DB, run reachability) and FCS-level tests in AstAnalyzerTests.fs (compile real F# code, verify edges are produced). Graph-level tests verify the dead code *algorithm* handles these patterns. FCS-level tests verify the *analyzer* produces the right edges.

**Tech Stack:** F#, xUnit v3, Unquote, FSharp.Compiler.Service

---

### Task 1: Graph-level regression tests for patterns #1-#3

**Files:**
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs` (append after line 999)

**Step 1: Write failing tests**

Append these three test modules to DeadCodeTests.fs:

```fsharp
module ``Generic type parameter reachability`` =

    [<Fact>]
    let ``type used as generic parameter is reachable when generic usage is reachable`` () =
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
                      { FullName = "App.Agent.create"
                        Kind = Function
                        SourceFile = "src/App/Agent.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.BuildState"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.BuildMsg"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 5
                        LineEnd = 8
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Agent.create"
                        Kind = Calls }
                      // Agent.create uses BuildState and BuildMsg as generic type args
                      { FromSymbol = "App.Agent.create"
                        ToSymbol = "App.BuildState"
                        Kind = UsesType }
                      { FromSymbol = "App.Agent.create"
                        ToSymbol = "App.BuildMsg"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Record type reachable via field construction`` =

    [<Fact>]
    let ``record type reached through field usage edge is not dead`` () =
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
                      { FullName = "App.Lib.createPerson"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Person"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.createPerson"
                        Kind = Calls }
                      // createPerson constructs Person via fields — edge to record type
                      { FromSymbol = "App.Lib.createPerson"
                        ToSymbol = "App.Person"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``DU type reachable via case usage`` =

    [<Fact>]
    let ``DU type reached through case usage edge is not dead`` () =
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
                      { FullName = "App.Lib.process"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shape.Square"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.process"
                        Kind = Calls }
                      // process pattern-matches on Circle — edge to case AND parent type
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Shape.Circle"
                        Kind = PatternMatches }
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Shape"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Shape type is reachable via the UsesType edge from process
            // DU cases are excluded from reporting by the DuCase filter
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

    [<Fact>]
    let ``DU type without direct edge but only case edges is still unreachable at graph level`` () =
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
                      { FullName = "App.Lib.process"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.process"
                        Kind = Calls }
                      // Only edge to the case, NO edge to parent type
                      // This is the false positive scenario — if analyzer misses the parent edge
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Shape.Circle"
                        Kind = PatternMatches } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Without the parent edge, Shape type IS unreachable at graph level
            // The fix is in AstAnalyzer always producing the parent edge
            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Shape" ] @>)
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/TestPrune.Tests/ --filter "Generic type parameter reachability|Record type reachable via field construction|DU type reachable via case usage"`
Expected: all 4 tests PASS (these test the algorithm with correct edges already present)

**Step 3: Commit**

```
feat: add graph-level dead code regression tests for patterns #1-#3
```

---

### Task 2: Graph-level regression tests for patterns #4-#6

**Files:**
- Modify: `tests/TestPrune.Tests/DeadCodeTests.fs` (append after Task 1's additions)

**Step 1: Write tests**

Append these test modules:

```fsharp
module ``Module function reachable when called from another module function`` =

    [<Fact>]
    let ``private module function called from entry point is reachable`` () =
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
                      { FullName = "App.Daemon.createWith"
                        Kind = Function
                        SourceFile = "src/App/Daemon.fs"
                        LineStart = 1
                        LineEnd = 20
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Daemon.processChanges"
                        Kind = Function
                        SourceFile = "src/App/Daemon.fs"
                        LineStart = 22
                        LineEnd = 40
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Daemon.createWith"
                        Kind = Calls }
                      { FromSymbol = "App.Daemon.createWith"
                        ToSymbol = "App.Daemon.processChanges"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Interface implementation reachability`` =

    [<Fact>]
    let ``implementor reachable when interface method edge exists to implementor`` () =
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
                      { FullName = "App.IHandler"
                        Kind = Type
                        SourceFile = "src/App/Handler.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.ConcreteHandler"
                        Kind = Type
                        SourceFile = "src/App/Handler.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.ConcreteHandler.Handle"
                        Kind = Function
                        SourceFile = "src/App/Handler.fs"
                        LineStart = 6
                        LineEnd = 9
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.IHandler"
                        Kind = UsesType }
                      // ConcreteHandler uses IHandler (interface implementation)
                      { FromSymbol = "App.ConcreteHandler"
                        ToSymbol = "App.IHandler"
                        Kind = UsesType }
                      // main calls the interface method
                      { FromSymbol = "App.Program.main"
                        ToSymbol = "App.ConcreteHandler"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // When main has direct edge to ConcreteHandler, it's reachable
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

    [<Fact>]
    let ``implementor unreachable when only interface is referenced`` () =
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
                      { FullName = "App.IHandler"
                        Kind = Type
                        SourceFile = "src/App/Handler.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.ConcreteHandler"
                        Kind = Type
                        SourceFile = "src/App/Handler.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.IHandler"
                        Kind = UsesType }
                      // ConcreteHandler implements IHandler, but no one references ConcreteHandler directly
                      { FromSymbol = "App.ConcreteHandler"
                        ToSymbol = "App.IHandler"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // ConcreteHandler only has outgoing edge to IHandler, no incoming
            // This IS the known limitation — interface dispatch doesn't reverse-resolve
            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.ConcreteHandler" ] @>)
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests/ --filter "Module function reachable|Interface implementation reachability"`
Expected: all 3 tests PASS

**Step 3: Commit**

```
feat: add graph-level dead code regression tests for patterns #4-#6
```

---

### Task 3: AstAnalyzer-level test for closure/local function edge attribution (#4)

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs` (append new module)

**Step 1: Write test**

Add a module that tests that a nested helper function called within a module-level binding gets its edge attributed to the enclosing binding, not dropped:

```fsharp
module ``Closure and nested function edge attribution`` =

    [<Fact>]
    let ``nested helper call inside module binding is attributed to enclosing function`` () =
        let result =
            analyze
                """
module M

let helper x = x + 1

let outerFunc () =
    let innerHelper y = helper y
    innerHelper 42
"""

        let outerCallsHelper =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("outerFunc", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("helper", StringComparison.Ordinal))

        test <@ outerCallsHelper @>

    [<Fact>]
    let ``multi-level nested closures attribute edges to outermost binding`` () =
        let result =
            analyze
                """
module M

let utility x = x * 2

let topLevel () =
    let mid () =
        let inner () = utility 5
        inner ()
    mid ()
"""

        let topCallsUtility =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("topLevel", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("utility", StringComparison.Ordinal))

        test <@ topCallsUtility @>
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests/ --filter "Closure and nested function edge attribution"`
Expected: PASS if enclosing attribution works; FAIL if edges are dropped. If FAIL → fix `findEnclosing` in AstAnalyzer.fs.

**Step 3: Commit**

```
test: add AstAnalyzer tests for closure edge attribution (pattern #4)
```

---

### Task 4: AstAnalyzer-level test for interface implementation edges (#6)

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs` (append new module)

**Step 1: Write test**

```fsharp
module ``Interface implementation edge extraction`` =

    [<Fact>]
    let ``implementing interface creates edge from implementor to interface type`` () =
        let result =
            analyze
                """
module M

type IProcessor =
    abstract member Process: string -> string

type UpperProcessor() =
    interface IProcessor with
        member _.Process s = s.ToUpper()
"""

        let implEdge =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("UpperProcessor", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("IProcessor", StringComparison.Ordinal))

        test <@ implEdge @>

    [<Fact>]
    let ``calling interface method creates edge to interface not implementor`` () =
        let result =
            analyze
                """
module M

type IProcessor =
    abstract member Process: string -> string

type UpperProcessor() =
    interface IProcessor with
        member _.Process s = s.ToUpper()

let run (p: IProcessor) =
    p.Process "hello"
"""

        // run should have edge to IProcessor (the declared type)
        let usesInterface =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("run", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("IProcessor", StringComparison.Ordinal))

        test <@ usesInterface @>

        // run should NOT have direct edge to UpperProcessor
        // (this is the known limitation — FCS resolves to interface, not implementor)
        let usesImplementor =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("run", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("UpperProcessor", StringComparison.Ordinal))

        test <@ not usesImplementor @>
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests/ --filter "Interface implementation edge extraction"`
Expected: PASS — documents current FCS behavior

**Step 3: Commit**

```
test: add AstAnalyzer tests for interface implementation edges (pattern #6)
```

---

### Task 5: AstAnalyzer-level regression tests for patterns #1-#3 (strengthen existing)

**Files:**
- Modify: `tests/TestPrune.Tests/AstAnalyzerTests.fs`

**Step 1: Write tests**

Add end-to-end tests that verify the full dead code scenario: a type that's ONLY used as a generic arg / record field / DU case parent produces correct edges.

```fsharp
module ``Dead code false positive regression — edge extraction`` =

    [<Fact>]
    let ``type only used as generic argument has edge from usage site`` () =
        let result =
            analyze
                """
module M

type State = { Count: int }

let agent : MailboxProcessor<State> = MailboxProcessor.Start(fun _ -> async { return () })
"""

        let hasEdgeToState =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("agent", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("State", StringComparison.Ordinal))

        test <@ hasEdgeToState @>

    [<Fact>]
    let ``record type only used via field construction has edge`` () =
        let result =
            analyze
                """
module M

type Config = { Host: string; Port: int }

let defaultConfig () = { Host = "localhost"; Port = 8080 }
"""

        let hasEdgeToConfig =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("defaultConfig", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Config", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToConfig @>

    [<Fact>]
    let ``DU type only referenced via case construction has parent edge`` () =
        let result =
            analyze
                """
module M

type Msg =
    | Start
    | Stop

let initial () = Start
"""

        let hasEdgeToMsg =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.EndsWith("initial", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Msg", StringComparison.Ordinal)
                && d.Kind = UsesType)

        test <@ hasEdgeToMsg @>
```

**Step 2: Run tests**

Run: `dotnet test tests/TestPrune.Tests/ --filter "Dead code false positive regression"`
Expected: PASS — edges already extracted correctly

**Step 3: Commit**

```
test: add AstAnalyzer regression tests for patterns #1-#3 false positives
```

---

### Task 6: Delete TODO and commit

**Files:**
- Delete: `TODO-dead-code-false-positives.md`

**Step 1: Delete the file**

```bash
rm TODO-dead-code-false-positives.md
```

**Step 2: Commit**

```
chore: remove TODO-dead-code-false-positives.md — all patterns have regression tests
```
