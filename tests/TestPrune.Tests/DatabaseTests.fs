module TestPrune.Tests.DatabaseTests

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
                        Kind = Calls }
                      { FromSymbol = "Lib.funcB"
                        ToSymbol = "Domain.TypeC"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
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
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
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
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
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
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.test1"
                        TestProject = "ProjectB"
                        TestClass = "Tests.MyTests"
                        TestMethod = "test1" } ]
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
                        Kind = Calls } ],
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
                        Kind = Calls } ],
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
                        Kind = Calls } ],
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
                        Kind = Calls } ],
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
                        Kind = Calls } ],
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
                        Kind = Calls }
                      { FromSymbol = "Tests.test2"
                        ToSymbol = "Lib.sharedFunc"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.test1"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test1" }
                      { SymbolFullName = "Tests.test2"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test2" } ]
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
                        Kind = UsesType } ]
                  TestMethods = []
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
                        Kind = PatternMatches } ]
                  TestMethods = []
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
                        Kind = References } ]
                  TestMethods = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].Kind = References @>)

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
                        Kind = Calls } ]
                  TestMethods = []
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
                        Kind = Type
                        SourceFile = "_extern"
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testConstructsType"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testConstructsType"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "testConstructsType" } ]
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
                        Kind = Type
                        SourceFile = "_extern"
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.myTest"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.myTest"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "myTest" } ]
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
                        Kind = Type
                        SourceFile = "_extern"
                        LineStart = 0
                        LineEnd = 0
                        ContentHash = ""
                        IsExtern = true } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.myTest"
                        ToSymbol = "Lib.MyType"
                        Kind = UsesType } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.myTest"
                        TestProject = "TestProject"
                        TestClass = "Tests"
                        TestMethod = "myTest" } ]
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
