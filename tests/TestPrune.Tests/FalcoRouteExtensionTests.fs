module TestPrune.Tests.FalcoRouteExtensionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports
open TestPrune.Extensions
open TestPrune.Falco

let private createTempDir () =
    let dir = Path.Combine(Path.GetTempPath(), $"falco-route-test-%A{Guid.NewGuid()}")
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupDir dir =
    if Directory.Exists dir then
        Directory.Delete(dir, true)

/// A route store over a fresh core database — the same wiring a consumer uses:
/// core owns the file, `toPluginStore` hands Falco a connection to it.
let private withRouteStore (f: string -> Database -> RouteStore -> unit) =
    let tempDir = createTempDir ()

    try
        let dbPath = Path.Combine(tempDir, "test.db")
        let db = Database.create dbPath
        f dbPath db (RouteStore(toPluginStore db))
    finally
        cleanupDir tempDir

let private withTestSetup
    (routeEntries: RouteHandlerEntry list)
    (testFiles: (string * string) list)
    (integrationTestProject: string)
    (integrationTestSubDir: string)
    (changedFiles: string list)
    (f: AffectedTest list -> unit)
    =
    let tempDir = createTempDir ()

    try
        let dbPath = Path.Combine(tempDir, "test.db")
        let db = Database.create dbPath
        let routeStore = RouteStore(toPluginStore db)
        routeStore.Rebuild(routeEntries)

        let testDir = Path.Combine(tempDir, integrationTestSubDir)

        if testFiles |> List.isEmpty |> not then
            Directory.CreateDirectory(testDir) |> ignore

            for (fileName, content) in testFiles do
                File.WriteAllText(Path.Combine(testDir, fileName), content)

        let extension =
            FalcoRouteExtension(integrationTestProject, integrationTestSubDir, routeStore)

        let result = extension.FindAffectedTestClasses(changedFiles, tempDir)

        f result
    finally
        cleanupDir tempDir

// -----------------------------------------------------------------------------
// RouteStore: the route table TestPrune.Falco owns inside core's cache database
// -----------------------------------------------------------------------------

module ``RouteStore round-trip`` =

    [<Fact>]
    let ``Rebuild and GetAll returns inserted entries`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = Some "Users.list" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = Some "Users.create" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs"
                    HandlerFunction = None } ]
            )

            let all = routes.GetAll()
            test <@ all.Length = 3 @>

            let patterns = all |> List.map (fun e -> e.UrlPattern) |> Set.ofList
            test <@ patterns = set [ "/api/users"; "/api/orders" ] @>

            let methods = all |> List.map (fun e -> e.HttpMethod) |> Set.ofList
            test <@ methods = set [ "GET"; "POST" ] @>

            // HandlerFunction round-trips, including a NULL back to None.
            let handlerFns = all |> List.map (fun e -> e.HandlerFunction) |> Set.ofList
            test <@ handlerFns = set [ Some "Users.list"; Some "Users.create"; None ] @>)

    [<Fact>]
    let ``GetUrlPatternsForSourceFile returns patterns for a given source file`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs"
                    HandlerFunction = None } ]
            )

            let patterns = routes.GetUrlPatternsForSourceFile("src/UsersHandler.fs")
            test <@ patterns.Length = 2 @>
            test <@ patterns |> List.contains "/api/users" @>

            let ordersPatterns = routes.GetUrlPatternsForSourceFile("src/OrdersHandler.fs")
            test <@ ordersPatterns = [ "/api/orders" ] @>

            let none = routes.GetUrlPatternsForSourceFile("src/NotAHandler.fs")
            test <@ none |> List.isEmpty @>)

    [<Fact>]
    let ``GetRouteHandlersForSourceFile returns only that file's entries`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = Some "Users.list" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs"
                    HandlerFunction = None } ]
            )

            let entries = routes.GetRouteHandlersForSourceFile("src/UsersHandler.fs")

            test
                <@
                    entries = [ { UrlPattern = "/api/users"
                                  HttpMethod = "GET"
                                  HandlerSourceFile = "src/UsersHandler.fs"
                                  HandlerFunction = Some "Users.list" } ]
                @>

            test <@ routes.GetRouteHandlersForSourceFile("src/Unknown.fs") |> List.isEmpty @>)

    [<Fact>]
    let ``GetAllHandlerSourceFiles returns distinct source files`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs"
                    HandlerFunction = None } ]
            )

            let files = routes.GetAllHandlerSourceFiles()
            test <@ files = set [ "src/UsersHandler.fs"; "src/OrdersHandler.fs" ] @>)

    [<Fact>]
    let ``Rebuild replaces all previous entries`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/old/route"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OldHandler.fs"
                    HandlerFunction = None } ]
            )

            routes.Rebuild(
                [ { UrlPattern = "/new/route"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/NewHandler.fs"
                    HandlerFunction = None } ]
            )

            let all = routes.GetAll()
            test <@ all.Length = 1 @>
            test <@ all[0].UrlPattern = "/new/route" @>
            test <@ all[0].HandlerSourceFile = "src/NewHandler.fs" @>

            let files = routes.GetAllHandlerSourceFiles()
            test <@ files |> Set.contains "src/OldHandler.fs" |> not @>
            test <@ files |> Set.contains "src/NewHandler.fs" @>)

    [<Fact>]
    let ``Rebuild with empty list clears all entries`` () =
        withRouteStore (fun _ _ routes ->
            routes.Rebuild(
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None } ]
            )

            routes.Rebuild([])

            test <@ routes.GetAll() |> List.isEmpty @>
            test <@ routes.GetAllHandlerSourceFiles() = Set.empty @>)

    [<Fact>]
    let ``a failed Rebuild rolls back, leaving the previous routes intact`` () =
        // Re-seeding is DELETE-then-INSERT in one transaction. If an entry is rejected
        // mid-flight (here: a null url_pattern from a malformed seed, which the parameter
        // binding refuses), the whole rebuild must roll back — a half-applied reseed would
        // leave the route table missing routes whose tests would then never be selected.
        withRouteStore (fun _ _ routes ->
            let good =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs"
                    HandlerFunction = None } ]

            routes.Rebuild(good)

            let malformed =
                [ { UrlPattern = null
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OtherHandler.fs"
                    HandlerFunction = None } ]

            raises<InvalidOperationException> <@ routes.Rebuild(malformed) @>

            test <@ routes.GetAll() = good @>)

    [<Fact>]
    let ``queries on a never-seeded store return empty, not an error`` () =
        // The table does not exist until the store creates it: every read must issue its
        // own DDL first, so a fresh core DB (or one a schema bump just recreated) reads as
        // "no routes" rather than throwing "no such table: route_handlers".
        withRouteStore (fun _ _ routes ->
            test <@ routes.GetAll() |> List.isEmpty @>
            test <@ routes.GetAllHandlerSourceFiles() = Set.empty @>
            test <@ routes.GetUrlPatternsForSourceFile "nonexistent.fs" |> List.isEmpty @>
            test <@ routes.GetRouteHandlersForSourceFile "nonexistent.fs" |> List.isEmpty @>)

module ``RouteStore survives a core schema recreate`` =

    let private openRawConnection (dbPath: string) =
        let conn = new SqliteConnection($"Data Source=%s{dbPath}")
        conn.Open()
        conn

    let private setUserVersion (dbPath: string) (version: int) =
        use conn = openRawConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{version};"
        cmd.ExecuteNonQuery() |> ignore

    let private routeTableExists (dbPath: string) =
        use conn = openRawConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='route_handlers'"

        cmd.ExecuteScalar() :?> int64 > 0L

    /// THE CONTRACT: core owns the FILE and deletes it on a `SchemaVersion` mismatch,
    /// dropping the plugin's table with it — core cannot migrate a table it knows nothing
    /// about. That is only safe because the plugin recreates its table on demand and its
    /// contents are re-seeded every run. This test drives exactly that drop → recreate
    /// path: a store that assumed its table existed would throw "no such table" here.
    [<Fact>]
    let ``a core schema bump drops the route table; the store recreates it on demand`` () =
        let tempDir = createTempDir ()

        try
            let dbPath = Path.Combine(tempDir, "test.db")

            let db = Database.create dbPath
            let routes = RouteStore(toPluginStore db)

            routes.Rebuild(
                [ { UrlPattern = "/api/users/{id}"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/Handlers/Users.fs"
                    HandlerFunction = Some "Users.get" } ]
            )

            test <@ routeTableExists dbPath @>
            test <@ routes.GetAll().Length = 1 @>

            // Stamp an incompatible (older) core schema version: the next core open
            // delete+recreates the file.
            setUserVersion dbPath (SchemaVersion - 1)

            let db2 = Database.create dbPath
            test <@ db2.WasRecreated @>

            // The plugin's table really is gone — the test below is not vacuous.
            test <@ not (routeTableExists dbPath) @>

            // Reads recreate it and report an honest empty, rather than throwing.
            let routes2 = RouteStore(toPluginStore db2)
            test <@ routes2.GetAll() |> List.isEmpty @>
            test <@ routeTableExists dbPath @>

            // And the next seed (routes are re-seeded every run) restores the data.
            routes2.Rebuild(
                [ { UrlPattern = "/api/users/{id}"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/Handlers/Users.fs"
                    HandlerFunction = Some "Users.get" } ]
            )

            test <@ routes2.GetAll().Length = 1 @>

            // A store constructed BEFORE the recreate is equally fine: it holds a
            // connection factory, not a connection, and re-issues its DDL per call.
            test <@ routes.GetAllHandlerSourceFiles() = set [ "src/Handlers/Users.fs" ] @>
        finally
            cleanupDir tempDir

// -----------------------------------------------------------------------------
// FindAffectedTestClasses: URL-matching test selection
// -----------------------------------------------------------------------------

module ``debug db roundtrip`` =

    [<Fact>]
    let ``route handlers survive roundtrip`` () =
        let tempDir = createTempDir ()

        try
            let dbPath = Path.Combine(tempDir, "test.db")
            let db = Database.create dbPath
            let routeStore = RouteStore(toPluginStore db)

            routeStore.Rebuild(
                [ { UrlPattern = "/api/users/{id}"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/Handlers/Users.fs"
                    HandlerFunction = None } ]
            )

            let hsf = routeStore.GetAllHandlerSourceFiles()
            test <@ hsf = set [ "src/Handlers/Users.fs" ] @>

            let urls = routeStore.GetUrlPatternsForSourceFile("src/Handlers/Users.fs")
            test <@ urls = [ "/api/users/{id}" ] @>

            // Now test the extension end-to-end
            let testDir = Path.Combine(tempDir, "tests/IntTests")
            Directory.CreateDirectory(testDir) |> ignore

            let testContent =
                "type UsersTests(output: obj) =\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

            File.WriteAllText(Path.Combine(testDir, "UsersTests.fs"), testContent)

            // Verify the file exists at the expected location
            let expectedDir = Path.Combine(tempDir, "tests/IntTests")

            let files =
                Directory.GetFiles(expectedDir, "*.fs", SearchOption.AllDirectories)
                |> Array.toList

            test <@ files.Length = 1 @>

            let extension = FalcoRouteExtension("IntTests", "tests/IntTests", routeStore)

            let result = extension.FindAffectedTestClasses([ "src/Handlers/Users.fs" ], tempDir)

            test <@ result.Length = 1 @>
        finally
            cleanupDir tempDir

module ``no changed handler files returns empty`` =

    [<Fact>]
    let ``changed files not in handler source files produces empty result`` () =
        let testContent =
            "type UsersTests() =\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("UsersTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Other/Unrelated.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

module ``changed handler file returns affected test classes`` =

    [<Fact>]
    let ``type-style test class is found when URL matches`` () =
        let testContent =
            "type UsersTests(output: ITestOutputHelper) =\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("UsersTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

module ``changed handler with module-style test file`` =

    [<Fact>]
    let ``module-style test is found when URL matches`` () =
        let testContent =
            "module UsersTests =\n    let getUser () =\n        let url = \"/api/users/123\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("UsersTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

module ``no matching URL in test files returns empty`` =

    [<Fact>]
    let ``handler changed but no test file contains the URL`` () =
        let testContent =
            "type OrderTests() =\n    member _.GetOrder() =\n        let url = \"/api/orders/456\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("OrderTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

module ``missing test directory returns empty`` =

    [<Fact>]
    let ``nonexistent integrationTestDir produces empty result`` () =
        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            []
            "IntTests"
            "tests/IntTests/nonexistent"
            [ "src/Handlers/Users.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

module ``multiple test classes in one file`` =

    [<Fact>]
    let ``both classes are returned when URL matches`` () =
        let testContent =
            """type UsersTests(output: ITestOutputHelper) =
    member _.GetUser() =
        let url = "/api/users/123"
        ()

type AdminUsersTests(output: ITestOutputHelper) =
    member _.GetAdmin() =
        let url = "/api/users/admin"
        ()
"""

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("UsersTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test <@ result.Length = 2 @>
                let classes = result |> List.map (fun r -> r.TestClass) |> Set.ofList
                test <@ classes = set [ "UsersTests"; "AdminUsersTests" ] @>)

module ``multiple handlers affecting different test files`` =

    [<Fact>]
    let ``returns tests from all affected files`` () =
        let usersTest =
            "type UsersTests() =\n    member _.Get() =\n        let url = \"/api/users/1\"\n        ()\n"

        let ordersTest =
            "type OrdersTests() =\n    member _.Get() =\n        let url = \"/api/orders/1\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None }
              { UrlPattern = "/api/orders/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Orders.fs"
                HandlerFunction = None } ]
            [ ("UsersTests.fs", usersTest); ("OrdersTests.fs", ordersTest) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs"; "src/Handlers/Orders.fs" ]
            (fun result ->
                test <@ result.Length = 2 @>
                let classes = result |> List.map (fun r -> r.TestClass) |> Set.ofList
                test <@ classes = set [ "UsersTests"; "OrdersTests" ] @>)

module ``URL pattern with path parameters matches correctly`` =

    [<Fact>]
    let ``multi-segment path parameters match concrete values`` () =
        let testContent =
            "type UserPostsTests() =\n    member _.GetUserPosts() =\n        let url = \"/api/users/abc/posts/123\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/api/users/{id}/posts/{postId}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/UserPosts.fs"
                HandlerFunction = None } ]
            [ ("UserPostsTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/UserPosts.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UserPostsTests" } ]
                    @>)

// -----------------------------------------------------------------------------
// AnalyzeEdges: function-scoped route edges
// -----------------------------------------------------------------------------

/// A test-method symbol for the in-memory symbol store, tagged as a test method
/// so it participates in the route-edge query the same way a real one would.
let private fn (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 2
      ContentHash = "h"
      IsExtern = false }

/// Drive `AnalyzeEdges` with a DB-backed route store and an in-memory symbol
/// store, writing the given integration test files to disk so per-route URL
/// matching resolves against real content.
let private withAnalyzeEdges
    (routeEntries: RouteHandlerEntry list)
    (symbols: SymbolInfo list)
    (testFiles: (string * string) list)
    (changedFiles: string list)
    (f: Dependency list -> unit)
    =
    let tempDir = createTempDir ()

    try
        let dbPath = Path.Combine(tempDir, "test.db")
        let db = Database.create dbPath
        let routeStore = RouteStore(toPluginStore db)
        routeStore.Rebuild(routeEntries)

        let testDir = Path.Combine(tempDir, "tests/IntTests")
        Directory.CreateDirectory(testDir) |> ignore

        for (fileName, content) in testFiles do
            File.WriteAllText(Path.Combine(testDir, fileName), content)

        let symbolStore =
            TestPrune.InMemoryStore.fromAnalysisResults [ AnalysisResult.Create(symbols, [], []) ]

        let extension =
            FalcoRouteExtension("IntTests", "tests/IntTests", routeStore) :> ITestPruneExtension

        let edges = extension.AnalyzeEdges symbolStore changedFiles tempDir
        f edges
    finally
        cleanupDir tempDir

/// Test files for a two-route handler file: one class per route's URL.
let private usersTestFile =
    "type UsersTests() =\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

let private ordersTestFile =
    "type OrdersTests() =\n    member _.GetOrder() =\n        let url = \"/api/orders/456\"\n        ()\n"

module ``AnalyzeEdges function-scoped routes`` =

    /// THE FIX: a change to a multi-route handler file, with each route mapped to
    /// its own handler function, links each route's tests ONLY to that route's
    /// function. Before the function-scoping change this asserted set contained the
    /// full cross-product (UsersTests -> getOrder, OrdersTests -> getUser too), so
    /// this test FAILS pre-change and PASSES post-change.
    [<Fact>]
    let ``one-function-per-route change scopes edges to that route's function`` () =
        let symbols =
            [ fn "App.Handlers.Multi.getUser" "src/Handlers/Multi.fs"
              fn "App.Handlers.Multi.getOrder" "src/Handlers/Multi.fs"
              fn "App.Tests.UsersTests.GetUser" "tests/IntTests/UsersTests.fs"
              fn "App.Tests.OrdersTests.GetOrder" "tests/IntTests/OrdersTests.fs" ]

        withAnalyzeEdges
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Multi.fs"
                HandlerFunction = Some "Multi.getUser" }
              { UrlPattern = "/api/orders/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Multi.fs"
                HandlerFunction = Some "Multi.getOrder" } ]
            symbols
            [ ("UsersTests.fs", usersTestFile); ("OrdersTests.fs", ordersTestFile) ]
            [ "src/Handlers/Multi.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

                // Only the route's own function is linked.
                test
                    <@
                        pairs = set
                            [ "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getUser"
                              "App.Tests.OrdersTests.GetOrder", "App.Handlers.Multi.getOrder" ]
                    @>

                // The cross-route edges the old file-level product emitted are gone.
                test <@ not (pairs.Contains("App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getOrder")) @>
                test <@ not (pairs.Contains("App.Tests.OrdersTests.GetOrder", "App.Handlers.Multi.getUser")) @>

                test <@ edges |> List.forall (fun e -> e.Kind = SharedState && e.Source = "falco") @>)

    /// config-applied handler: the seed carries the bare `Module.function`
    /// (`WellKnown.robots`), not a partial application, and the store holds the
    /// fully-qualified name. The suffix match still resolves it.
    [<Fact>]
    let ``config-applied bare handler function resolves by suffix`` () =
        let symbols =
            [ fn "App.Handlers.WellKnown.robots" "src/Handlers/WellKnown.fs"
              fn "App.Handlers.WellKnown.humans" "src/Handlers/WellKnown.fs"
              fn "App.Tests.RobotsTests.GetRobots" "tests/IntTests/RobotsTests.fs" ]

        let robotsTest =
            "type RobotsTests() =\n    member _.GetRobots() =\n        let url = \"/robots.txt\"\n        ()\n"

        withAnalyzeEdges
            [ { UrlPattern = "/robots.txt"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/WellKnown.fs"
                HandlerFunction = Some "WellKnown.robots" } ]
            symbols
            [ ("RobotsTests.fs", robotsTest) ]
            [ "src/Handlers/WellKnown.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

                test <@ pairs = set [ "App.Tests.RobotsTests.GetRobots", "App.Handlers.WellKnown.robots" ] @>

                // The sibling function in the same file is NOT linked.
                test <@ not (pairs.Contains("App.Tests.RobotsTests.GetRobots", "App.Handlers.WellKnown.humans")) @>)

module ``AnalyzeEdges fallback`` =

    /// FALLBACK PROOF: with HandlerFunction = None the route keeps the legacy
    /// file-level cross-product — every symbol in the changed file linked to the
    /// route-matched test's methods.
    [<Fact>]
    let ``None handler function keeps file-level cross-product`` () =
        let symbols =
            [ fn "App.Handlers.Multi.getUser" "src/Handlers/Multi.fs"
              fn "App.Handlers.Multi.helper" "src/Handlers/Multi.fs"
              fn "App.Tests.UsersTests.GetUser" "tests/IntTests/UsersTests.fs" ]

        withAnalyzeEdges
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Multi.fs"
                HandlerFunction = None } ]
            symbols
            [ ("UsersTests.fs", usersTestFile) ]
            [ "src/Handlers/Multi.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

                // The test's method links to BOTH file symbols (file-level fallback).
                test
                    <@
                        pairs = set
                            [ "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getUser"
                              "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.helper" ]
                    @>)

    /// UNDER-SELECTION GUARD: a seed naming a handler function that no longer resolves
    /// (renamed, moved, re-namespaced) must NOT silently emit zero edges for that route —
    /// its tests would stop being selected. It degrades to the same coarse file-level set
    /// as `None`.
    [<Fact>]
    let ``unresolvable handler function falls back to the file's symbols`` () =
        let symbols =
            [ fn "App.Handlers.Multi.getUser" "src/Handlers/Multi.fs"
              fn "App.Handlers.Multi.helper" "src/Handlers/Multi.fs"
              fn "App.Tests.UsersTests.GetUser" "tests/IntTests/UsersTests.fs" ]

        withAnalyzeEdges
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Multi.fs"
                HandlerFunction = Some "Multi.renamedAwayGetUser" } ]
            symbols
            [ ("UsersTests.fs", usersTestFile) ]
            [ "src/Handlers/Multi.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

                test
                    <@
                        pairs = set
                            [ "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getUser"
                              "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.helper" ]
                    @>)

    /// A change to a handler file NOT in the route table yields no edges.
    [<Fact>]
    let ``changed file with no routes yields no edges`` () =
        let symbols = [ fn "App.Handlers.Multi.getUser" "src/Handlers/Multi.fs" ]

        withAnalyzeEdges
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Multi.fs"
                HandlerFunction = Some "Multi.getUser" } ]
            symbols
            [ ("UsersTests.fs", usersTestFile) ]
            [ "src/Handlers/Unrelated.fs" ]
            (fun edges -> test <@ edges |> List.isEmpty @>)
