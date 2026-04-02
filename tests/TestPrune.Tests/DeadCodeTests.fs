module TestPrune.Tests.DeadCodeTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode
open TestPrune.Domain
open TestPrune.Tests.TestHelpers

module ``All symbols reachable`` =

    [<Fact>]
    let ``entry point reaches everything — empty unreachable list`` () =
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
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>
            test <@ result.TotalSymbols = 2 @>
            test <@ result.ReachableSymbols = 2 @>)

module ``Unreachable function detected`` =

    [<Fact>]
    let ``function with no callers from entry point is unreachable`` () =
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
                      { FullName = "App.Lib.usedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.unusedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.usedHelper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols.Length = 1 @>
            test <@ result.UnreachableSymbols[0].FullName = "App.Lib.unusedHelper" @>)

module ``Transitive reachability`` =

    [<Fact>]
    let ``entry to funcA to funcB to TypeC — all reachable`` () =
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
                      { FullName = "App.Lib.funcA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.funcB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.funcA"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.funcA"
                        ToSymbol = "App.Lib.funcB"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.funcB"
                        ToSymbol = "App.Domain.TypeC"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>
            test <@ result.ReachableSymbols = 4 @>)

module ``Test methods excluded`` =

    [<Fact>]
    let ``test methods not reported as dead code`` () =
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
                      { FullName = "Tests.MyTests.testSomething"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.testSomething"
                        TestProject = "Tests"
                        TestClass = "MyTests"
                        TestMethod = "testSomething" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Test method is unreachable from production entry but should be excluded
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Module symbols excluded`` =

    [<Fact>]
    let ``modules are containers and not reported as dead code`` () =
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
                      { FullName = "App.OldModule"
                        Kind = Module
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 1
                        LineEnd = 20
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.OldModule.orphanFunc"
                        Kind = Function
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 3
                        LineEnd = 8
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Module excluded, but the orphan function should still be reported
            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.OldModule.orphanFunc" ] @>)

module ``Only shallowest unreachable reported`` =

    [<Fact>]
    let ``nested symbols within an unreachable function are not reported`` () =
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
                      { FullName = "App.Lib.unusedFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "localHelper"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "depCmd"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 5
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Lib.unusedFunc" ] @>)

    [<Fact>]
    let ``symbol starting at same line but shorter than parent is filtered`` () =
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
                      { FullName = "App.Lib.outerFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.innerFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            // innerFunc starts at same line as outerFunc but is shorter, so it's contained
            test <@ names = [ "App.Lib.outerFunc" ] @>)

    [<Fact>]
    let ``top-level unreachable value is still reported`` () =
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
                      { FullName = "App.Lib.unusedFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.unusedValue"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 7
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            // Both are top-level, neither contains the other
            test <@ names = [ "App.Lib.unusedFunc"; "App.Lib.unusedValue" ] @>)

    [<Fact>]
    let ``local bindings without dots are not reported`` () =
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
                      { FullName = "localVar"
                        Kind = Value
                        SourceFile = "src/App/Program.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "_param"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 1
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Local bindings/params (no dot in name) should not be reported
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

    [<Fact>]
    let ``sibling unreachable functions in same file are both reported`` () =
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
                      { FullName = "App.Lib.deadA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.deadB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Lib.deadA"; "App.Lib.deadB" ] @>)

module ``Test file symbols excluded`` =

    [<Fact>]
    let ``anything in tests/ is excluded from dead code report by default`` () =
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
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // TestHelpers.setup is in tests/ directory, should be excluded
            test <@ result.UnreachableSymbols |> List.isEmpty @>)

    [<Fact>]
    let ``test file symbols included when includeTests is true`` () =
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
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] true

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "TestHelpers.setup" ] @>)

    [<Fact>]
    let ``reachable test helper not reported when includeTests is true`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Tests.MyTest.testSomething"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "TestHelpers.unusedHelper"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.MyTest.testSomething"
                        ToSymbol = "TestHelpers.setup"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTest.testSomething"
                        TestProject = "Tests"
                        TestClass = "MyTest"
                        TestMethod = "testSomething" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // Use test method as entry point, include tests in report
            let result, _events = runDeadCode db [ "Tests.MyTest.testSomething" ] true

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            // setup is reachable from the test entry point, only unusedHelper is dead
            test <@ names = [ "TestHelpers.unusedHelper" ] @>)

module ``matchesPattern — both wildcards (true, true)`` =

    [<Fact>]
    let ``*Route* matches symbol whose name contains Route`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.MyRouteHandler"
                        Kind = Function
                        SourceFile = "src/App/Routes.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Unrelated"
                        Kind = Function
                        SourceFile = "src/App/Other.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // *Route* should match App.MyRouteHandler and make it reachable
            let result, _events = runDeadCode db [ "*Route*" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Unrelated" ] @>)

    [<Fact>]
    let ``*Route* does not match symbol whose name lacks Route`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.MyRouteHandler"
                        Kind = Function
                        SourceFile = "src/App/Routes.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Unrelated"
                        Kind = Function
                        SourceFile = "src/App/Other.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // *Route* must not match App.Unrelated
            let result, _events = runDeadCode db [ "*Route*" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names |> List.contains "App.Unrelated" @>)

module ``matchesPattern — start wildcard only (true, false)`` =

    [<Fact>]
    let ``*.main matches symbol ending with .main`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // *.main matches Program.main, making it the sole entry point
            let result, _events = runDeadCode db [ "*.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Lib.helper" ] @>)

    [<Fact>]
    let ``*.main does not match symbol that does not end with .main`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // *.main must not match App.Lib.helper
            let result, _events = runDeadCode db [ "*.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names |> List.contains "App.Lib.helper" @>)

module ``matchesPattern — end wildcard only (false, true)`` =

    [<Fact>]
    let ``App.* does not match symbol outside the App namespace`` () =
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
                      { FullName = "Other.Lib.helper"
                        Kind = Function
                        SourceFile = "src/Other/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // App.* must not match Other.Lib.helper
            let result, _events = runDeadCode db [ "App.*" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names |> List.contains "Other.Lib.helper" @>)

module ``matchesPattern — exact match (false, false)`` =

    [<Fact>]
    let ``exact pattern does not match a different symbol name`` () =
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
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // Exact pattern must not match App.Lib.helper
            let result, _events = runDeadCode db [ "App.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names |> List.contains "App.Lib.helper" @>)

    [<Fact>]
    let ``exact pattern matches and seeds reachability`` () =
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
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "App.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``DU case symbols excluded`` =

    [<Fact>]
    let ``DU cases are not reported as dead code`` () =
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
                  Dependencies = []
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            // Type reported, but DU cases excluded
            test <@ names = [ "App.Shape" ] @>)

module ``No matching entry points`` =

    [<Fact>]
    let ``when no pattern matches, everything is unreachable`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Lib.funcA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.funcB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Lib.funcA"
                        ToSymbol = "App.Lib.funcB"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.nonexistent" ] false

            test <@ result.ReachableSymbols = 0 @>
            test <@ result.UnreachableSymbols.Length = 2 @>)

module ``Multiple entry point patterns`` =

    [<Fact>]
    let ``two patterns each matching different symbols seeds both as roots`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Api.handler"
                        Kind = Function
                        SourceFile = "src/App/Api.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Worker.run"
                        Kind = Function
                        SourceFile = "src/App/Worker.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Shared.helper"
                        Kind = Function
                        SourceFile = "src/App/Shared.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Orphan.dead"
                        Kind = Function
                        SourceFile = "src/App/Orphan.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Api.handler"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls }
                      { FromSymbol = "App.Worker.run"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.handler"; "*.run" ] false

            let deadNames = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            // handler, run, and shared.helper all reachable; only Orphan.dead is dead
            test <@ deadNames = [ "App.Orphan.dead" ] @>)

module ``matchesPattern — prefix positive`` =

    [<Fact>]
    let ``App.* matches symbols in App namespace and makes them reachable`` () =
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
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            // App.* matches App.Program.main, reachability follows to App.Lib.helper
            let result, _events = runDeadCode db [ "App.*" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

module ``Event emission`` =

    [<Fact>]
    let ``dead code emits DeadCodeFoundEvent with symbol names`` () =
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
                      { FullName = "App.Lib.usedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.unusedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.usedHelper"
                        Kind = Calls } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let _result, events = runDeadCode db [ "*.Program.main" ] false

            let deadCodeEvents =
                events
                |> List.choose (fun e ->
                    match e with
                    | DeadCodeFoundEvent names -> Some names
                    | _ -> None)

            test <@ deadCodeEvents.Length = 1 @>
            test <@ deadCodeEvents[0] = [ "App.Lib.unusedHelper" ] @>)

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
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)

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
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            test <@ result.UnreachableSymbols.Length = 1 @>
            let sym = result.UnreachableSymbols[0]
            test <@ sym.Symbol.FullName = "App.Lib.orphan" @>
            test <@ sym.Reason = NoIncomingEdges @>)

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
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCodeVerbose db [ "*.Program.main" ] false

            // root: lines 1-5, helper: lines 7-12 — NOT contained, both reported
            test <@ result.UnreachableSymbols.Length = 2 @>

            let root =
                result.UnreachableSymbols
                |> List.find (fun s -> s.Symbol.FullName = "App.Island.root")

            let helper =
                result.UnreachableSymbols
                |> List.find (fun s -> s.Symbol.FullName = "App.Island.helper")

            // root has NO incoming edges
            test <@ root.Reason = NoIncomingEdges @>

            // helper HAS an incoming edge from root, but root is unreachable
            test
                <@
                    match helper.Reason with
                    | DisconnectedFromEntryPoints _ -> true
                    | _ -> false
                @>)

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
                      { FullName = "App.Domain.BuildState"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.BuildMsg"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 7
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Agent.create"
                        Kind = Calls }
                      { FromSymbol = "App.Agent.create"
                        ToSymbol = "App.Domain.BuildState"
                        Kind = UsesType }
                      { FromSymbol = "App.Agent.create"
                        ToSymbol = "App.Domain.BuildMsg"
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
                      { FullName = "App.Domain.Person"
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
                      { FromSymbol = "App.Lib.createPerson"
                        ToSymbol = "App.Domain.Person"
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
                      { FullName = "App.Domain.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.Shape.Square"
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
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape.Circle"
                        Kind = PatternMatches }
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

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
                      { FullName = "App.Domain.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.Shape.Square"
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
                      { FromSymbol = "App.Lib.process"
                        ToSymbol = "App.Domain.Shape.Circle"
                        Kind = PatternMatches } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            let unreachableNames = result.UnreachableSymbols |> List.map (fun s -> s.FullName)

            test <@ unreachableNames |> List.contains "App.Domain.Shape" @>)

module ``Edge coverage for test impact`` =

    [<Fact>]
    let ``changed DU type affects test that pattern matches its cases`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Domain.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Domain.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.process"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.ShapeTests.test_process"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
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
                        TestMethod = "test_process" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

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
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.loadConfigs"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.ConfigTests.test_load"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.ConfigTests.test_load"
                        ToSymbol = "App.Lib.loadConfigs"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.loadConfigs"
                        ToSymbol = "App.Domain.Config"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.ConfigTests.test_load"
                        TestProject = "Tests"
                        TestClass = "ConfigTests"
                        TestMethod = "test_load" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

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
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.AlphaTests.test_alpha"
                        Kind = Function
                        SourceFile = "tests/Alpha.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.BetaTests.test_beta"
                        Kind = Function
                        SourceFile = "tests/Beta.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
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
                        TestMethod = "test_beta" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

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
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Lib.greet"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.GreetTests.test_greet"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
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
                        TestMethod = "test_greet" } ]
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let affected = db.QueryAffectedTests([ "App.Domain.Person" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test_greet" @>)

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
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Daemon.processChanges"
                        Kind = Function
                        SourceFile = "src/App/Daemon.fs"
                        LineStart = 12
                        LineEnd = 20
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
                      { FullName = "App.Handlers.IHandler"
                        Kind = Type
                        SourceFile = "src/App/Handlers.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Handlers.ConcreteHandler"
                        Kind = Type
                        SourceFile = "src/App/Handlers.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Handlers.ConcreteHandler.Handle"
                        Kind = Function
                        SourceFile = "src/App/Handlers.fs"
                        LineStart = 6
                        LineEnd = 9
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Handlers.IHandler"
                        Kind = UsesType }
                      { FromSymbol = "App.Handlers.ConcreteHandler"
                        ToSymbol = "App.Handlers.IHandler"
                        Kind = UsesType }
                      { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Handlers.ConcreteHandler"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Handle is contained within ConcreteHandler (lines 6-9 inside 5-10), filtered by isContainedByAnother
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
                      { FullName = "App.Handlers.IHandler"
                        Kind = Type
                        SourceFile = "src/App/Handlers.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "App.Handlers.ConcreteHandler"
                        Kind = Type
                        SourceFile = "src/App/Handlers.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Handlers.IHandler"
                        Kind = UsesType }
                      { FromSymbol = "App.Handlers.ConcreteHandler"
                        ToSymbol = "App.Handlers.IHandler"
                        Kind = UsesType } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])

            let result, _events = runDeadCode db [ "*.Program.main" ] false

            // Known limitation: ConcreteHandler is unreachable because there's no direct edge from main
            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.Handlers.ConcreteHandler" ] @>)
