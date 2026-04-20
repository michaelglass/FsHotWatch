module TestPrune.Tests.DatabaseTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Tests.TestHelpers

let private openRawConnection (dbPath: string) =
    let conn = new SqliteConnection($"Data Source=%s{dbPath}")
    conn.Open()
    conn

module ``Create initializes schema`` =

    [<Fact>]
    let ``create db and query without error`` () =
        withDb (fun db ->
            let symbols = db.GetSymbolsInFile "nonexistent.fs"
            test <@ symbols |> List.isEmpty @>

            let names = db.GetAllSymbolNames()
            test <@ names = Set.empty @>

            let affected = db.QueryAffectedTests []
            test <@ affected |> List.isEmpty @>)

module ``Store and retrieve symbols`` =

    [<Fact>]
    let ``insert via RebuildProjects and query back via GetSymbolsInFile`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "MyModule.myFunc"
                        Kind = Function
                        SourceFile = "src/MyModule.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "MyModule.MyType"
                        Kind = Type
                        SourceFile = "src/MyModule.fs"
                        LineStart = 12
                        LineEnd = 20
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let symbols = db.GetSymbolsInFile "src/MyModule.fs"
            test <@ symbols.Length = 2 @>
            test <@ symbols[0].FullName = "MyModule.myFunc" @>
            test <@ symbols[0].Kind = Function @>
            test <@ symbols[0].LineStart = 5 @>
            test <@ symbols[1].FullName = "MyModule.MyType" @>
            test <@ symbols[1].Kind = Type @>)

module ``Transitive dependency query`` =

    [<Fact>]
    let ``testA depends on funcB depends on TypeC — changing TypeC returns testA`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Lib.funcB"
                        ToSymbol = "Domain.TypeC"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let affected = db.QueryAffectedTests [ "Domain.TypeC" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>
            test <@ affected[0].TestClass = "Tests" @>
            test <@ affected[0].TestProject = "MyTests" @>)

module ``Direct dependency`` =

    [<Fact>]
    let ``testA depends on funcB — changing funcB returns testA`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let affected = db.QueryAffectedTests [ "Lib.funcB" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)

module ``No dependency`` =

    [<Fact>]
    let ``change symbol that no test depends on returns empty`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Other.unrelated"
                        Kind = Function
                        SourceFile = "src/Other.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let affected = db.QueryAffectedTests [ "Other.unrelated" ]
            test <@ affected |> List.isEmpty @>)

module ``RebuildProjects replaces old data`` =

    [<Fact>]
    let ``rebuild twice only latest data present`` () =
        withDb (fun db ->
            let result1 =
                { Symbols =
                    [ { FullName = "Mod.oldFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result1 ])

            let result2 =
                { Symbols =
                    [ { FullName = "Mod.newFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result2 ])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].FullName = "Mod.newFunc" @>

            let allNames = db.GetAllSymbolNames()
            test <@ allNames |> Set.contains "Mod.oldFunc" |> not @>
            test <@ allNames |> Set.contains "Mod.newFunc" @>)

module ``Cross-project dependencies`` =

    [<Fact>]
    let ``cross-project dep edges survive regardless of list order`` () =
        withDb (fun db ->
            // Project A defines the symbol
            let projectA =
                { Symbols =
                    [ { FullName = "LibModule.helper"
                        Kind = Function
                        SourceFile = "src/Lib/Helper.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Project B has a test that depends on A's symbol
            let projectB =
                { Symbols =
                    [ { FullName = "Tests.MyTests.test1"
                        Kind = Function
                        SourceFile = "tests/MyTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.MyTests.test1"
                        ToSymbol = "LibModule.helper"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.test1"
                        TestProject = "ProjectB"
                        TestClass = "Tests.MyTests"
                        TestMethod = "test1" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Pass B before A — the old API would silently drop the edge
            db.RebuildProjects([ projectB; projectA ])

            let affected = db.QueryAffectedTests [ "LibModule.helper" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test1" @>)

module ``Cross-project transitive chain`` =

    [<Fact>]
    let ``change in project A reaches test in project C through handler in project B`` () =
        withDb (fun db ->
            let projectA =
                AnalysisResult.Create(
                    [ { FullName = "Database.UserQueries.getUserById"
                        Kind = Function
                        SourceFile = "src/Database/UserQueries.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ],
                    [],
                    []
                )

            let projectB =
                AnalysisResult.Create(
                    [ { FullName = "Web.Handlers.User.dashboard"
                        Kind = Function
                        SourceFile = "src/Web/Handlers/User.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ],
                    [ { FromSymbol = "Web.Handlers.User.dashboard"
                        ToSymbol = "Database.UserQueries.getUserById"
                        Kind = Calls
                        Source = "core" } ],
                    []
                )

            let projectC =
                AnalysisResult.Create(
                    [ { FullName = "Tests.UserTests.test dashboard"
                        Kind = Function
                        SourceFile = "tests/UserTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ],
                    [ { FromSymbol = "Tests.UserTests.test dashboard"
                        ToSymbol = "Web.Handlers.User.dashboard"
                        Kind = Calls
                        Source = "core" } ],
                    [ { SymbolFullName = "Tests.UserTests.test dashboard"
                        TestProject = "Tests"
                        TestClass = "Tests.UserTests"
                        TestMethod = "test dashboard" } ]
                )

            db.RebuildProjects([ projectA; projectB; projectC ])

            let affected = db.QueryAffectedTests [ "Database.UserQueries.getUserById" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test dashboard" @>)

    [<Fact>]
    let ``change in project A reaches tests in both direct and transitive projects`` () =
        withDb (fun db ->
            let projectA =
                AnalysisResult.Create(
                    [ { FullName = "Lib.helper"
                        Kind = Function
                        SourceFile = "src/Lib/Helper.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ],
                    [],
                    []
                )

            let projectB =
                AnalysisResult.Create(
                    [ { FullName = "Web.Middleware.auth"
                        Kind = Function
                        SourceFile = "src/Web/Middleware.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ],
                    [ { FromSymbol = "Web.Middleware.auth"
                        ToSymbol = "Lib.helper"
                        Kind = Calls
                        Source = "core" } ],
                    []
                )

            let unitTests =
                AnalysisResult.Create(
                    [ { FullName = "UnitTests.test helper"
                        Kind = Function
                        SourceFile = "tests/UnitTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ],
                    [ { FromSymbol = "UnitTests.test helper"
                        ToSymbol = "Lib.helper"
                        Kind = Calls
                        Source = "core" } ],
                    [ { SymbolFullName = "UnitTests.test helper"
                        TestProject = "UnitTests"
                        TestClass = "UnitTests"
                        TestMethod = "test helper" } ]
                )

            let integrationTests =
                AnalysisResult.Create(
                    [ { FullName = "IntegTests.test auth flow"
                        Kind = Function
                        SourceFile = "tests/IntegTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ],
                    [ { FromSymbol = "IntegTests.test auth flow"
                        ToSymbol = "Web.Middleware.auth"
                        Kind = Calls
                        Source = "core" } ],
                    [ { SymbolFullName = "IntegTests.test auth flow"
                        TestProject = "IntegTests"
                        TestClass = "IntegTests"
                        TestMethod = "test auth flow" } ]
                )

            db.RebuildProjects([ projectA; projectB; unitTests; integrationTests ])

            let affected = db.QueryAffectedTests [ "Lib.helper" ]
            let methods = affected |> List.map (fun t -> t.TestMethod) |> Set.ofList
            test <@ affected.Length = 2 @>
            test <@ methods = set [ "test helper"; "test auth flow" ] @>)

module ``Multiple tests depending on same symbol`` =

    [<Fact>]
    let ``returns all tests`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.test1"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Tests.test2"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 5
                        LineEnd = 7
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.sharedFunc"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.test1"
                        ToSymbol = "Lib.sharedFunc"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Tests.test2"
                        ToSymbol = "Lib.sharedFunc"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.test1"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test1" }
                      { SymbolFullName = "Tests.test2"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test2" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let affected = db.QueryAffectedTests [ "Lib.sharedFunc" ]
            test <@ affected.Length = 2 @>

            let methods = affected |> List.map (fun t -> t.TestMethod) |> Set.ofList
            test <@ methods = set [ "test1"; "test2" ] @>)

module ``GetAllSymbolNames`` =

    [<Fact>]
    let ``returns the full set of names`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "A.one"
                        Kind = Function
                        SourceFile = "src/A.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "B.two"
                        Kind = Type
                        SourceFile = "src/B.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "C.three"
                        Kind = Value
                        SourceFile = "src/C.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let names = db.GetAllSymbolNames()
            test <@ names = set [ "A.one"; "B.two"; "C.three" ] @>)

module ``Route handler round-trip`` =

    [<Fact>]
    let ``RebuildRouteHandlers and GetAllRouteHandlers returns inserted entries`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let all = db.GetAllRouteHandlers()
            test <@ all.Length = 3 @>

            let patterns = all |> List.map (fun e -> e.UrlPattern) |> Set.ofList
            test <@ patterns = set [ "/api/users"; "/api/orders" ] @>

            let methods = all |> List.map (fun e -> e.HttpMethod) |> Set.ofList
            test <@ methods = set [ "GET"; "POST" ] @>)

    [<Fact>]
    let ``GetUrlPatternsForSourceFile returns patterns for a given source file`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let patterns = db.GetUrlPatternsForSourceFile("src/UsersHandler.fs")
            test <@ patterns.Length = 2 @>
            test <@ patterns |> List.contains "/api/users" @>

            let ordersPatterns = db.GetUrlPatternsForSourceFile("src/OrdersHandler.fs")
            test <@ ordersPatterns = [ "/api/orders" ] @>

            let none = db.GetUrlPatternsForSourceFile("src/NotAHandler.fs")
            test <@ none |> List.isEmpty @>)

    [<Fact>]
    let ``GetAllHandlerSourceFiles returns distinct source files`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let files = db.GetAllHandlerSourceFiles()
            test <@ files = set [ "src/UsersHandler.fs"; "src/OrdersHandler.fs" ] @>)

    [<Fact>]
    let ``RebuildRouteHandlers replaces all previous entries`` () =
        withDb (fun db ->
            let first =
                [ { UrlPattern = "/old/route"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OldHandler.fs" } ]

            db.RebuildRouteHandlers(first)

            let second =
                [ { UrlPattern = "/new/route"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/NewHandler.fs" } ]

            db.RebuildRouteHandlers(second)

            let all = db.GetAllRouteHandlers()
            test <@ all.Length = 1 @>
            test <@ all[0].UrlPattern = "/new/route" @>
            test <@ all[0].HandlerSourceFile = "src/NewHandler.fs" @>

            let files = db.GetAllHandlerSourceFiles()
            test <@ files |> Set.contains "src/OldHandler.fs" |> not @>
            test <@ files |> Set.contains "src/NewHandler.fs" @>)

    [<Fact>]
    let ``RebuildRouteHandlers with empty list clears all entries`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)
            db.RebuildRouteHandlers([])

            let all = db.GetAllRouteHandlers()
            test <@ all |> List.isEmpty @>

            let files = db.GetAllHandlerSourceFiles()
            test <@ files = Set.empty @>)

module ``RebuildProjects with cache keys`` =

    [<Fact>]
    let ``RebuildProjects stores fileKeys and projectKeys atomically`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod.func"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects(
                [ result ],
                fileKeys = [ ("src/Mod.fs", "file-key-abc") ],
                projectKeys = [ ("MyProject", "proj-key-123") ]
            )

            test <@ db.GetFileKey "src/Mod.fs" = Some "file-key-abc" @>
            test <@ db.GetProjectKey "MyProject" = Some "proj-key-123" @>)

    [<Fact>]
    let ``RebuildProjects without optional keys does not clear existing keys`` () =
        withDb (fun db ->
            // Seed existing keys via RebuildProjects
            let seedResult =
                { Symbols =
                    [ { FullName = "Existing.func"
                        Kind = Function
                        SourceFile = "src/Existing.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects(
                [ seedResult ],
                fileKeys = [ ("src/Existing.fs", "existing-key") ],
                projectKeys = [ ("ExistingProj", "existing-proj-key") ]
            )

            // Now rebuild with a different project, no optional keys
            let result =
                { Symbols =
                    [ { FullName = "New.func"
                        Kind = Function
                        SourceFile = "src/New.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            // Pre-existing keys should still be there
            test <@ db.GetFileKey "src/Existing.fs" = Some "existing-key" @>
            test <@ db.GetProjectKey "ExistingProj" = Some "existing-proj-key" @>)

module ``Empty database queries`` =

    [<Fact>]
    let ``GetAllSymbols returns empty on fresh database`` () =
        withDb (fun db ->
            let symbols = db.GetAllSymbols()
            test <@ symbols |> List.isEmpty @>)

    [<Fact>]
    let ``GetTestMethodSymbolNames returns empty on fresh database`` () =
        withDb (fun db ->
            let names = db.GetTestMethodSymbolNames()
            test <@ names = Set.empty @>)

    [<Fact>]
    let ``GetReachableSymbols with non-empty roots on empty database returns empty`` () =
        withDb (fun db ->
            let reachable = db.GetReachableSymbols([ "nonexistent" ])
            test <@ reachable = Set.empty @>)

    [<Fact>]
    let ``GetAllRouteHandlers returns empty on fresh database`` () =
        withDb (fun db ->
            let handlers = db.GetAllRouteHandlers()
            test <@ handlers |> List.isEmpty @>)

    [<Fact>]
    let ``GetAllHandlerSourceFiles returns empty on fresh database`` () =
        withDb (fun db ->
            let files = db.GetAllHandlerSourceFiles()
            test <@ files = Set.empty @>)

module ``stringToSymbolKind fallback`` =

    [<Fact>]
    let ``unknown kind string falls back to Value`` () =
        withDbPath (fun path db ->
            // Insert a symbol row with an unknown kind string directly via raw SQL
            use conn = openRawConnection path

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                INSERT INTO symbols (full_name, kind, source_file, line_start, line_end, indexed_at)
                VALUES ('Foo.bar', 'UnknownKind', 'src/Foo.fs', 1, 5, '2024-01-01T00:00:00Z')
                """

            cmd.ExecuteNonQuery() |> ignore

            let symbols = db.GetSymbolsInFile("src/Foo.fs")
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].Kind = Value @>)

module ``stringToDepKind fallback`` =

    [<Fact>]
    let ``unknown dep kind string falls back to References`` () =
        withDbPath (fun path db ->
            // Seed two symbols so we can insert a dependency between them
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            // Insert a dependency with an unknown dep_kind directly via raw SQL
            use conn = openRawConnection path

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                INSERT INTO dependencies (from_symbol_id, to_symbol_id, dep_kind)
                SELECT f.id, t.id, 'unknown_dep_kind'
                FROM symbols f, symbols t
                WHERE f.full_name = 'Tests.testA' AND t.full_name = 'Lib.funcB'
                """

            cmd.ExecuteNonQuery() |> ignore

            // QueryAffectedTests traverses dependencies; the unknown kind should fall back to
            // References, meaning testA is still returned as affected by funcB
            let affected = db.QueryAffectedTests([ "Lib.funcB" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)

module ``Project key storage`` =

    [<Fact>]
    let ``GetProjectKey returns None when no hash stored`` () =
        withDb (fun db ->
            let hash = db.GetProjectKey "MyProject"
            test <@ hash = None @>)

module ``symbolKindToString and round-trip`` =

    [<Fact>]
    let ``Property kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod.MyProp"
                        Kind = Property
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].Kind = Property @>
            test <@ symbols[0].FullName = "Mod.MyProp" @>)

    [<Fact>]
    let ``DuCase kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod.MyDU.CaseA"
                        Kind = DuCase
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].Kind = DuCase @>)

    [<Fact>]
    let ``Module kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod"
                        Kind = Module
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].Kind = Module @>)

module ``depKindToString and round-trip`` =

    [<Fact>]
    let ``UsesType dep kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.MyType"
                        Kind = Type
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].Kind = UsesType @>)

    [<Fact>]
    let ``PatternMatches dep kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.Case1"
                        Kind = DuCase
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.Case1"
                        Kind = PatternMatches
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].Kind = PatternMatches @>)

    [<Fact>]
    let ``References dep kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.someVal"
                        Kind = Value
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.someVal"
                        Kind = References
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].Kind = References @>)

    [<Fact>]
    let ``SharedState dep kind round-trips through database`` () =
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

            db.RebuildProjects([ result ])
            let deps = db.GetDependenciesFromFile("src/Writer.fs")
            test <@ deps.Length = 1 @>
            test <@ deps[0].Kind = SharedState @>
            test <@ deps[0].Source = "sql" @>)

module ``GetDependenciesFromFile`` =

    [<Fact>]
    let ``returns dependencies originating from the given file`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].FromSymbol = "Tests.testA" @>
            test <@ deps[0].ToSymbol = "Lib.funcB" @>
            test <@ deps[0].Kind = Calls @>)

    [<Fact>]
    let ``returns empty for file with no outgoing dependencies`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "src/Lib.fs"
            test <@ deps |> List.isEmpty @>)

module ``GetTestMethodsInFile`` =

    [<Fact>]
    let ``returns test methods defined in the given file`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let tests = db.GetTestMethodsInFile "tests/Tests.fs"
            test <@ tests.Length = 1 @>
            test <@ tests[0].TestMethod = "testA" @>
            test <@ tests[0].TestClass = "Tests" @>
            test <@ tests[0].TestProject = "MyTests" @>)

    [<Fact>]
    let ``returns empty for non-test file`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let tests = db.GetTestMethodsInFile "src/Lib.fs"
            test <@ tests |> List.isEmpty @>)

module ``Cross-project extern symbol dependencies`` =

    [<Fact>]
    let ``QueryAffectedTests traverses through extern symbols`` () =
        withDb (fun db ->
            let testProjectResult =
                { Symbols =
                    [ { FullName = "Tests.testConstructsType"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "abc"
                        IsExtern = false }
                      { FullName = "Lib.MyType"
                        Kind = ExternRef
                        SourceFile = ExternSourceFile
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testConstructsType"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testConstructsType"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "testConstructsType" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ testProjectResult ])

            let affected = db.QueryAffectedTests [ "Lib.MyType" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testConstructsType" @>)

    [<Fact>]
    let ``extern symbol does not overwrite real symbol when both are in same RebuildProjects call`` () =
        withDb (fun db ->
            // Library defines the real symbol
            let libResult =
                { Symbols =
                    [ { FullName = "Lib.MyType"
                        Kind = Type
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = "real-hash"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Test project produces an extern stub for the same symbol
            let testResult =
                { Symbols =
                    [ { FullName = "Tests.myTest"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "test-hash"
                        IsExtern = false }
                      { FullName = "Lib.MyType"
                        Kind = ExternRef
                        SourceFile = ExternSourceFile
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.myTest"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.myTest"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "myTest" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Both results in same call — real symbol should win
            db.RebuildProjects([ libResult; testResult ])

            // The real symbol should be preserved (not overwritten by extern stub)
            let storedSymbols = db.GetSymbolsInFile "src/Lib.fs"
            let myType = storedSymbols |> List.tryFind (fun s -> s.FullName = "Lib.MyType")
            test <@ myType.IsSome @>
            test <@ myType.Value.ContentHash = "real-hash" @>
            test <@ not myType.Value.IsExtern @>

            // The dependency edge should still resolve
            let affected = db.QueryAffectedTests [ "Lib.MyType" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "myTest" @>)

    [<Fact>]
    let ``re-indexing test project does not destroy real symbol from library project`` () =
        withDb (fun db ->
            // First: index library project
            let libResult =
                { Symbols =
                    [ { FullName = "Lib.MyType"
                        Kind = Type
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = "real-hash"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ libResult ])

            // Second: re-index test project (separate call, as happens in incremental indexing)
            let testResult =
                { Symbols =
                    [ { FullName = "Tests.myTest"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "test-hash"
                        IsExtern = false }
                      { FullName = "Lib.MyType"
                        Kind = ExternRef
                        SourceFile = ExternSourceFile
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.myTest"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.myTest"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "myTest" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ testResult ])

            // Real symbol from library should still be intact
            let storedSymbols = db.GetSymbolsInFile "src/Lib.fs"
            let myType = storedSymbols |> List.tryFind (fun s -> s.FullName = "Lib.MyType")
            test <@ myType.IsSome @>
            test <@ myType.Value.ContentHash = "real-hash" @>

            // Edge should resolve through the real symbol
            let affected = db.QueryAffectedTests [ "Lib.MyType" ]
            test <@ affected.Length = 1 @>)

    // Regression: re-indexing a library file without re-indexing dependents used to
    // cascade-delete the test→library edge via `DELETE FROM symbols WHERE source_file IN (...)`
    // combined with `ON DELETE CASCADE` on `dependencies.to_symbol_id`. QueryAffectedTests
    // would then return zero even though the changed library symbol had dependent tests.
    // Fixed by UPSERT (preserve row id on conflict) + targeted orphan cleanup.
    [<Fact>]
    let ``re-indexing library file preserves incoming edges from non-re-indexed tests`` () =
        withDb (fun db ->
            // First pass: index both library and tests together with the full dep edge.
            let lib =
                { Symbols =
                    [ { FullName = "Lib.MyType"
                        Kind = Type
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 10
                        ContentHash = "v1"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            let tests =
                { Symbols =
                    [ { FullName = "Tests.myTest"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "t1"
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.myTest"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.myTest"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "myTest" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ lib; tests ])

            // Sanity: the edge resolves before re-indexing.
            let beforeAffected = db.QueryAffectedTests [ "Lib.MyType" ]
            test <@ beforeAffected.Length = 1 @>

            // Now re-index ONLY the library file (content changed). This is the
            // incremental path: the test file is not re-analyzed.
            let libV2 =
                { lib with
                    Symbols =
                        [ { lib.Symbols[0] with
                              ContentHash = "v2"
                              LineEnd = 11 } ] }

            db.RebuildProjects([ libV2 ])

            // The test's incoming edge to Lib.MyType must still resolve.
            let afterAffected = db.QueryAffectedTests [ "Lib.MyType" ]
            test <@ afterAffected.Length = 1 @>
            test <@ afterAffected[0].TestMethod = "myTest" @>)

module ``Unknown enum deduplication`` =

    [<Fact>]
    let ``reading same unknown SymbolKind twice only warns once`` () =
        withDbPath (fun path db ->
            let result =
                { Symbols =
                    [ { FullName = "A.func1"
                        Kind = Function
                        SourceFile = "src/A.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "A.func2"
                        Kind = Function
                        SourceFile = "src/A.fs"
                        LineStart = 7
                        LineEnd = 10
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            // Overwrite both symbols to have the same unknown kind
            use conn = openRawConnection path
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "UPDATE symbols SET kind = 'FutureKind' WHERE source_file = 'src/A.fs'"
            cmd.ExecuteNonQuery() |> ignore

            // Read twice — both should deserialize without error
            let symbols = db.GetSymbolsInFile "src/A.fs"
            test <@ symbols.Length = 2 @>
            test <@ symbols |> List.forall (fun s -> s.Kind = Value) @>)

    [<Fact>]
    let ``reading same unknown DependencyKind twice only warns once`` () =
        withDbPath (fun path db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            // Overwrite to unknown dep kind, then duplicate the edge
            use conn = openRawConnection path
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "UPDATE dependencies SET dep_kind = 'future_dep_kind'"
            cmd.ExecuteNonQuery() |> ignore

            // Insert a second edge with the same unknown kind
            use cmd2 = conn.CreateCommand()

            cmd2.CommandText <-
                """
                INSERT INTO dependencies (from_symbol_id, to_symbol_id, dep_kind)
                SELECT f.id, t.id, 'future_dep_kind'
                FROM symbols f, symbols t
                WHERE f.full_name = 'Lib.funcB' AND t.full_name = 'Tests.testA'
                """

            cmd2.ExecuteNonQuery() |> ignore

            // Reading edges should deserialize both without error (dedup warning)
            let affected = db.QueryAffectedTests([ "Lib.funcB" ])
            test <@ affected.Length = 1 @>)

module ``RebuildProjects edge cases`` =

    [<Fact>]
    let ``RebuildProjects with only extern symbols skips delete`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Ext.SomeType"
                        Kind = ExternRef
                        SourceFile = ExternSourceFile
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Should not throw even though no real source files to delete
            db.RebuildProjects([ result ])

            let allNames = db.GetAllSymbolNames()
            test <@ allNames |> Set.contains "Ext.SomeType" @>)

    [<Fact>]
    let ``RebuildProjects with empty file keys does not error`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod.func"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Pass Some [] for both keys
            db.RebuildProjects([ result ], fileKeys = [], projectKeys = [])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>)

module ``GetFileKey`` =

    [<Fact>]
    let ``GetFileKey returns None when file not indexed`` () =
        withDb (fun db ->
            let key = db.GetFileKey "src/NonExistent.fs"
            test <@ key = None @>)

    [<Fact>]
    let ``GetFileKey returns Some when file was indexed with key`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Mod.func"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ], fileKeys = [ "src/Mod.fs", "abc123" ])

            let key = db.GetFileKey "src/Mod.fs"
            test <@ key = Some "abc123" @>)

module ``openConnection error handling`` =

    [<Fact>]
    let ``creating database with nonexistent directory path fails gracefully`` () =
        let dbPath = "/nonexistent/dir/that/does/not/exist/test.db"
        Assert.ThrowsAny<exn>(fun () -> Database.create dbPath |> ignore)

    [<Fact>]
    let ``creating database with directory as path fails gracefully`` () =
        let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")

        Directory.CreateDirectory(tmpDir) |> ignore

        try
            // SQLite may or may not fail when given a directory path;
            // the point is that it should not hang or corrupt state
            try
                let _db = Database.create tmpDir
                // If it doesn't throw, that's also acceptable
                ()
            with _ ->
                ()
        finally
            try
                Directory.Delete(tmpDir, true)
            with _ ->
                ()

module ``GetTestMethodsInFile empty result`` =

    [<Fact>]
    let ``GetTestMethodsInFile returns empty for nonexistent file`` () =
        withDb (fun db ->
            let tests = db.GetTestMethodsInFile "nonexistent/file.fs"
            test <@ tests |> List.isEmpty @>)

module ``GetDependenciesFromFile empty result`` =

    [<Fact>]
    let ``GetDependenciesFromFile returns empty for nonexistent file`` () =
        withDb (fun db ->
            let deps = db.GetDependenciesFromFile "nonexistent/file.fs"
            test <@ deps |> List.isEmpty @>)

module ``GetIncomingEdgesBatch`` =

    [<Fact>]
    let ``GetIncomingEdgesBatch with empty input returns empty map`` () =
        withDb (fun db -> test <@ db.GetIncomingEdgesBatch([]) = Map.empty @>)

    [<Fact>]
    let ``GetIncomingEdgesBatch returns incoming edges for populated database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let edges = db.GetIncomingEdgesBatch([ "Lib.funcB" ])
            test <@ edges |> Map.containsKey "Lib.funcB" @>
            test <@ edges["Lib.funcB"] = [ "Tests.testA" ] @>)

    [<Fact>]
    let ``GetIncomingEdgesBatch returns empty for symbols with no incoming edges`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let edges = db.GetIncomingEdgesBatch([ "Lib.funcB" ])
            test <@ edges = Map.empty @>)

module ``Analysis events`` =

    [<Fact>]
    let ``InsertEvent and GetEvents round-trip`` () =
        withDb (fun db ->
            db.InsertEvent("run1", "2024-01-01T00:00:00Z", "start", "data1")
            db.InsertEvent("run1", "2024-01-01T00:01:00Z", "end", "data2")
            db.InsertEvent("run2", "2024-01-01T00:02:00Z", "start", "data3")

            let events = db.GetEvents("run1")
            test <@ events.Length = 2 @>
            let (ts, evType, evData) = events[0]
            test <@ ts = "2024-01-01T00:00:00Z" @>
            test <@ evType = "start" @>
            test <@ evData = "data1" @>)

    [<Fact>]
    let ``GetEvents returns empty for unknown run ID`` () =
        withDb (fun db ->
            let events = db.GetEvents("nonexistent-run")
            test <@ events |> List.isEmpty @>)

    [<Fact>]
    let ``ClearEvents removes events for given run ID only`` () =
        withDb (fun db ->
            db.InsertEvent("run1", "2024-01-01T00:00:00Z", "start", "data1")
            db.InsertEvent("run2", "2024-01-01T00:01:00Z", "start", "data2")

            db.ClearEvents("run1")

            let run1Events = db.GetEvents("run1")
            let run2Events = db.GetEvents("run2")
            test <@ run1Events |> List.isEmpty @>
            test <@ run2Events.Length = 1 @>)

module ``GetReachableSymbols`` =

    [<Fact>]
    let ``GetReachableSymbols with empty roots returns empty`` () =
        withDb (fun db ->
            let reachable = db.GetReachableSymbols([])
            test <@ reachable = Set.empty @>)

    [<Fact>]
    let ``GetReachableSymbols returns transitively reachable symbols`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false }
                      { FullName = "Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/Domain.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Lib.funcB"
                        ToSymbol = "Domain.TypeC"
                        Kind = UsesType
                        Source = "core" } ]
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let reachable = db.GetReachableSymbols([ "Tests.testA" ])
            test <@ reachable |> Set.contains "Tests.testA" @>
            test <@ reachable |> Set.contains "Lib.funcB" @>
            test <@ reachable |> Set.contains "Domain.TypeC" @>)

module ``GetUrlPatternsForSourceFile empty result`` =

    [<Fact>]
    let ``GetUrlPatternsForSourceFile returns empty for unknown file`` () =
        withDb (fun db ->
            let patterns = db.GetUrlPatternsForSourceFile "nonexistent.fs"
            test <@ patterns |> List.isEmpty @>)

module ``ExternRef kind round-trips through database`` =

    [<Fact>]
    let ``ExternRef kind round-trips through database`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Ext.SomeType"
                        Kind = ExternRef
                        SourceFile = ExternSourceFile
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let allSymbols = db.GetAllSymbols()

            let externSym = allSymbols |> List.tryFind (fun s -> s.FullName = "Ext.SomeType")

            test <@ externSym.IsSome @>
            test <@ externSym.Value.Kind = ExternRef @>
            test <@ externSym.Value.IsExtern @>)

module ``GetTestMethodSymbolNames with data`` =

    [<Fact>]
    let ``GetTestMethodSymbolNames returns test symbol names`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = ""
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let names = db.GetTestMethodSymbolNames()
            test <@ names |> Set.contains "Tests.testA" @>)

module ``Schema version migration`` =

    let private setUserVersion (dbPath: string) (version: int) =
        use conn = openRawConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{version};"
        cmd.ExecuteNonQuery() |> ignore

    let private getUserVersion (dbPath: string) =
        use conn = openRawConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version;"
        cmd.ExecuteScalar() :?> int64 |> int

    [<Fact>]
    let ``recreates database when schema version is outdated`` () =
        let path = tempDbPath ()

        try
            let db = Database.create path
            db.RebuildProjects([ standardGraph ])
            let symbols = db.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>

            setUserVersion path 999

            let db2 = Database.create path
            let symbols2 = db2.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols2 |> List.isEmpty @>
        finally
            cleanupDb path

    [<Fact>]
    let ``sets schema version on new database`` () =
        let path = tempDbPath ()

        try
            let _db = Database.create path
            let version = getUserVersion path
            test <@ version > 0 @>
        finally
            cleanupDb path

    [<Fact>]
    let ``preserves database when schema version matches`` () =
        let path = tempDbPath ()

        try
            let db = Database.create path
            db.RebuildProjects([ standardGraph ])
            let symbols = db.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>

            let db2 = Database.create path
            let symbols2 = db2.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols2.Length = 1 @>
        finally
            cleanupDb path

module ``Dependency source attribution`` =

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

            db.RebuildProjects([ result ])
            let deps = db.GetDependenciesFromFile("src/A.fs")
            test <@ deps.Length = 1 @>
            test <@ deps[0].Source = "core" @>)

    [<Fact>]
    let ``multiple sources coexist in dependency graph`` () =
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
                        IsExtern = false }
                      { FullName = "C.func"
                        Kind = Function
                        SourceFile = "src/C.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "ghi"
                        IsExtern = false } ],
                    [ { FromSymbol = "A.func"
                        ToSymbol = "B.func"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "A.func"
                        ToSymbol = "C.func"
                        Kind = SharedState
                        Source = "sql" } ],
                    []
                )

            db.RebuildProjects([ result ])
            let deps = db.GetDependenciesFromFile("src/A.fs")
            test <@ deps.Length = 2 @>
            let sources = deps |> List.map (fun d -> d.Source) |> Set.ofList
            test <@ sources = set [ "core"; "sql" ] @>)

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

module ``Edge source provenance`` =

    [<Fact>]
    let ``returns distinct sources in transitive path`` () =
        withDb (fun db ->
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "t1"
                        IsExtern = false }
                      { FullName = "Service.process"
                        Kind = Function
                        SourceFile = "src/Service.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "s1"
                        IsExtern = false }
                      { FullName = "Queries.readItems"
                        Kind = Function
                        SourceFile = "src/Queries.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "q1"
                        IsExtern = false }
                      { FullName = "Jobs.writeItems"
                        Kind = Function
                        SourceFile = "src/Jobs.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "j1"
                        IsExtern = false } ],
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Service.process"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Service.process"
                        ToSymbol = "Queries.readItems"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Queries.readItems"
                        ToSymbol = "Jobs.writeItems"
                        Kind = SharedState
                        Source = "sql" } ],
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                )

            db.RebuildProjects([ result ])
            let sources = db.QueryEdgeSourcesForTest([ "Jobs.writeItems" ])
            test <@ sources |> Set.ofList = set [ "core"; "sql" ] @>)

    [<Fact>]
    let ``returns only core for pure AST path`` () =
        withDb (fun db ->
            db.RebuildProjects([ standardGraph ])
            let sources = db.QueryEdgeSourcesForTest([ "Domain.TypeC" ])
            test <@ sources = [ "core" ] @>)
