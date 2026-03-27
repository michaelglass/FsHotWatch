module TestPrune.Tests.DeadCodeTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

let private withDb (f: Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f db
    finally
        if File.Exists path then
            File.Delete path

        let walPath = path + "-wal"
        let shmPath = path + "-shm"

        if File.Exists walPath then
            File.Delete walPath

        if File.Exists shmPath then
            File.Delete shmPath

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.usedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Lib.unusedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.usedHelper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.funcA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Lib.funcB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" }
                      { FullName = "App.Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = "" } ]
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
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "Tests.MyTests.testSomething"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.testSomething"
                        TestProject = "Tests"
                        TestClass = "MyTests"
                        TestMethod = "testSomething" } ] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.OldModule"
                        Kind = Module
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 1
                        LineEnd = 20
                        ContentHash = "" }
                      { FullName = "App.OldModule.orphanFunc"
                        Kind = Function
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 3
                        LineEnd = 8
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.unusedFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = "" }
                      { FullName = "localHelper"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "depCmd"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 5
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.outerFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = "" }
                      { FullName = "App.Lib.innerFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.unusedFunc"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Lib.unusedValue"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 7
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "localVar"
                        Kind = Value
                        SourceFile = "src/App/Program.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "_param"
                        Kind = Value
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 1
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.deadA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Lib.deadB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] true

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
                        ContentHash = "" }
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "TestHelpers.unusedHelper"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.MyTest.testSomething"
                        ToSymbol = "TestHelpers.setup"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTest.testSomething"
                        TestProject = "Tests"
                        TestClass = "MyTest"
                        TestMethod = "testSomething" } ] }

            db.RebuildProjects([ "Tests", graph ])

            // Use test method as entry point, include tests in report
            let result = findDeadCode db [ "Tests.MyTest.testSomething" ] true

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
                        ContentHash = "" }
                      { FullName = "App.Unrelated"
                        Kind = Function
                        SourceFile = "src/App/Other.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // *Route* should match App.MyRouteHandler and make it reachable
            let result = findDeadCode db [ "*Route*" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Unrelated"
                        Kind = Function
                        SourceFile = "src/App/Other.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // *Route* must not match App.Unrelated
            let result = findDeadCode db [ "*Route*" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // *.main matches Program.main, making it the sole entry point
            let result = findDeadCode db [ "*.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // *.main must not match App.Lib.helper
            let result = findDeadCode db [ "*.main" ] false

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
                        ContentHash = "" }
                      { FullName = "Other.Lib.helper"
                        Kind = Function
                        SourceFile = "src/Other/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // App.* must not match Other.Lib.helper
            let result = findDeadCode db [ "App.*" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // Exact pattern must not match App.Lib.helper
            let result = findDeadCode db [ "App.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "App.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Shape"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Shape.Circle"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 2
                        LineEnd = 2
                        ContentHash = "" }
                      { FullName = "App.Shape.Square"
                        Kind = DuCase
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 3
                        LineEnd = 3
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.Program.main" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.funcB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Lib.funcA"
                        ToSymbol = "App.Lib.funcB"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.nonexistent" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Worker.run"
                        Kind = Function
                        SourceFile = "src/App/Worker.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Shared.helper"
                        Kind = Function
                        SourceFile = "src/App/Shared.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "App.Orphan.dead"
                        Kind = Function
                        SourceFile = "src/App/Orphan.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Api.handler"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls }
                      { FromSymbol = "App.Worker.run"
                        ToSymbol = "App.Shared.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            let result = findDeadCode db [ "*.handler"; "*.run" ] false

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
                        ContentHash = "" }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "App", graph ])

            // App.* matches App.Program.main, reachability follows to App.Lib.helper
            let result = findDeadCode db [ "App.*" ] false

            test <@ result.UnreachableSymbols |> List.isEmpty @>)
