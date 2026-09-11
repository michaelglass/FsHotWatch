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

/// As `withTestSetup`, but also writes non-test app source files under `<repo>/src` so the
/// extension can read them: a Falco.UnionRoutes route DU (case→URL derivation) or a module of
/// named URL constants (`let settingsUrl = "/settings"`). `appSourceFiles` is empty for the
/// literal-URL tests, which must behave identically.
let private withTestSetupCore
    (routeEntries: RouteHandlerEntry list)
    (appSourceFiles: (string * string) list)
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

        if appSourceFiles |> List.isEmpty |> not then
            let srcDir = Path.Combine(tempDir, "src")
            Directory.CreateDirectory(srcDir) |> ignore

            for (fileName, content) in appSourceFiles do
                File.WriteAllText(Path.Combine(srcDir, fileName), content)

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

let private withTestSetup
    (routeEntries: RouteHandlerEntry list)
    (testFiles: (string * string) list)
    (integrationTestProject: string)
    (integrationTestSubDir: string)
    (changedFiles: string list)
    (f: AffectedTest list -> unit)
    =
    withTestSetupCore routeEntries [] testFiles integrationTestProject integrationTestSubDir changedFiles f

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
                "type UsersTests(output: obj) =\n    [<Fact>]\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

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
            "type UsersTests() =\n    [<Fact>]\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

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
            "type UsersTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

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
        // The module carries a [<Fact>]: a module without test attributes is a
        // helper, not a test container, and is deliberately never selected.
        let testContent =
            "module UsersTests =\n    [<Fact>]\n    let getUser () =\n        let url = \"/api/users/123\"\n        ()\n"

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
            "type OrderTests() =\n    [<Fact>]\n    member _.GetOrder() =\n        let url = \"/api/orders/456\"\n        ()\n"

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
    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/123"
        ()

type AdminUsersTests(output: ITestOutputHelper) =
    [<Fact>]
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

module ``per-declaration selection`` =

    /// R1: a URL match is attributed to the declaration whose textual span
    /// contains it — the sibling class in the same file is NOT dragged in.
    [<Fact>]
    let ``URL inside only one class's span selects only that class`` () =
        let testContent =
            """type UsersTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/123"
        ()

type OrdersTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetOrder() =
        let url = "/api/orders/456"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// R2: the URL lives only in a helper module with no test attributes. The
    /// conservative fallback fires (the match is outside every test span), so
    /// the file's test class IS selected — but the helper module never is.
    [<Fact>]
    let ``match only inside a non-test helper module falls back to the file's test classes`` () =
        let testContent =
            """module Urls =
    let users = "/api/users/123"

type UsersTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetUser() = ()
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// R3: a test-bearing module stays selectable, and selection is per-module —
    /// the sibling test module without the URL is not selected.
    [<Fact>]
    let ``URL inside one of two test-bearing modules selects only that module`` () =
        let testContent =
            """module UsersTests =
    [<Fact>]
    let ``gets a user`` () =
        let url = "/api/users/123"
        ()

module OrdersTests =
    [<Fact>]
    let ``gets an order`` () =
        let url = "/api/orders/456"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// R4: the helper module's URL is an out-of-span match, so the fallback
    /// fires — but the fallback set is every SELECTABLE declaration, and the
    /// helper module is not one, so it is still excluded. The class is both
    /// directly matched and the whole fallback set: either way, [UsersTests].
    [<Fact>]
    let ``helper module is excluded even when a class span also matches`` () =
        let testContent =
            """module Urls =
    let users = "/api/users/123"

type UsersTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/456"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// C1: a helper module holds the shared route constant, one test class
    /// exercises the route through the helper (no literal of its own), and a
    /// sibling test class inlines a literal for the SAME route. The helper's
    /// match lies outside every selectable span, so the fallback must union in
    /// ALL selectable declarations — a direct match elsewhere in the file must
    /// not suppress it and silently drop the indirect test class.
    [<Fact>]
    let ``out-of-span helper match selects indirect test classes alongside the direct match`` () =
        let testContent =
            """module Urls =
    let users = "/api/users/123"

type UsersIndirectTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetUser() = ignore Urls.users

type UsersDirectTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetOther() =
        let url = "/api/users/999"
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
                let classes = result |> List.map (fun r -> r.TestClass) |> Set.ofList
                test <@ classes = set [ "UsersIndirectTests"; "UsersDirectTests" ] @>)

    /// C2: a combined attribute list (`[<Trait(...); Fact>]`) is still a test
    /// marker — a module whose only tests are attributed that way must count as
    /// test-bearing and be selected when its span matches the URL.
    [<Fact>]
    let ``module whose only test marker is a combined attribute list is selected`` () =
        let testContent =
            """module UsersTests =
    [<Trait("Category", "Integration"); Fact>]
    let ``gets a user`` () =
        let url = "/api/users/123"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// B8: an attribute-like name in ORDINARY code (`[ users; TestCase(1) ]`)
    /// must not make a helper module count as test-bearing. If it does, the
    /// helper's URL match registers as a direct match instead of an out-of-span
    /// one, the fallback is suppressed, and the truly-affected indirect test
    /// class is dropped while the non-runnable helper is selected.
    [<Fact>]
    let ``attribute-like name in ordinary code does not make a helper module selectable`` () =
        let testContent =
            """module Urls =
    let users = "/api/users/123"
    let cases = [ users; TestCase(1) ]

type UsersIndirectTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.GetUser() = ignore Urls.users
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
                let classes = result |> List.map (fun r -> r.TestClass)
                test <@ List.contains "UsersIndirectTests" classes @>
                test <@ not (List.contains "Urls" classes) @>)

module ``fixture classes are not test classes`` =

    /// A fixture-shaped class carries no test attribute, so it holds no runnable
    /// test and must not be returned as an "affected test class" — the shape of
    /// the consumer's `IntegrationTestFixture`, whose span holds the login URL
    /// every authenticated test goes through.
    [<Fact>]
    let ``fixture class without test attributes is not selected while the real test class is`` () =
        let testContent =
            """type IntegrationTestFixture() =
    let httpClient = new HttpClient()

    member _.Login() =
        let url = "/api/users/123"
        ()

type UsersTests(fixture: IntegrationTestFixture) =
    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/456"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// A file that is ONLY fixtures and helpers contributes nothing, even though
    /// its text matches the route. There is no test in it to run, so returning
    /// its class names could only fabricate edges out of fixture members.
    [<Fact>]
    let ``file of nothing but fixtures contributes no test classes`` () =
        let testContent =
            """type TestErrorSink() =
    member _.Clear() = ()

type TestServer(dbName: string) =
    member _.Login() =
        let url = "/api/users/123"
        ()
"""

        withTestSetup
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = None } ]
            [ ("TestServerFixture.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

    /// UNDER-SELECTION GUARD: xUnit runs the test methods a BASE class declares,
    /// so a derived class with no attribute of its own is still test-bearing. An
    /// `inherit` clause is the evidence, and dropping such a class would lose a
    /// real test — the one failure mode this tool must not have.
    [<Fact>]
    let ``class with no attributes of its own but an inherit clause is selected`` () =
        let testContent =
            """type PostgresUsersTests() =
    inherit UsersContractTests(postgres)

    member _.Endpoint = "/api/users/123"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "PostgresUsersTests" } ]
                    @>)

    /// A collection marker (`[<CollectionDefinition>] type FooCollection() = class end`)
    /// and a bare `IClassFixture` implementation both declare zero tests. Neither
    /// counts as evidence — only the sibling class carrying a `[<Fact>]` does.
    [<Fact>]
    let ``collection marker and IClassFixture implementation are not selected`` () =
        let testContent =
            """[<CollectionDefinition("Users", DisableParallelization = true)>]
type UsersCollection() =
    class
    end

type UsersFixtureOnly(fixture: IntegrationTestFixture) =
    interface IClassFixture<IntegrationTestFixture>

    member _.Endpoint = "/api/users/123"

type UsersTests(fixture: IntegrationTestFixture) =
    interface IClassFixture<IntegrationTestFixture>

    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/456"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// A `FactAttribute` SUBCLASS (`[<SkippableFact>]`) declares real tests. An
    /// unmarked class is dropped, so failing to recognise the subclass would
    /// silently lose those tests — hence the marker match admits a prefix
    /// before `Fact`.
    [<Fact>]
    let ``custom Fact subclass attribute counts as a test marker`` () =
        let testContent =
            """type UsersTests(fixture: IntegrationTestFixture) =
    [<SkippableFact>]
    member _.GetUser() =
        let url = "/api/users/123"
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
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

    /// Fixtures are not selectable, so a URL living only in a fixture's span is an
    /// OUT-OF-SPAN match: the conservative fallback fires and every test-bearing
    /// declaration in the file is selected — including the one that reaches the
    /// route only through the fixture.
    [<Fact>]
    let ``URL only in a fixture span still falls back to the file's test classes`` () =
        let testContent =
            """type UsersFixture() =
    member _.Endpoint = "/api/users/123"

type UsersIndirectTests(fixture: UsersFixture) =
    [<Fact>]
    member _.GetUser() = ignore fixture.Endpoint

type OrdersTests(fixture: UsersFixture) =
    [<Fact>]
    member _.GetOrder() = ignore "/api/orders/456"
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
                let classes = result |> List.map (fun r -> r.TestClass) |> Set.ofList
                test <@ classes = set [ "UsersIndirectTests"; "OrdersTests" ] @>)

module ``multiple handlers affecting different test files`` =

    [<Fact>]
    let ``returns tests from all affected files`` () =
        let usersTest =
            "type UsersTests() =\n    [<Fact>]\n    member _.Get() =\n        let url = \"/api/users/1\"\n        ()\n"

        let ordersTest =
            "type OrdersTests() =\n    [<Fact>]\n    member _.Get() =\n        let url = \"/api/orders/1\"\n        ()\n"

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
            "type UserPostsTests() =\n    [<Fact>]\n    member _.GetUserPosts() =\n        let url = \"/api/users/abc/posts/123\"\n        ()\n"

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

/// The ticket's own quantified reproduction: a route with no literal path text of its
/// own — the root route `/` — used to match the F# COMMENT token `//`, so every commented
/// line in the repo read as a reference to it. On a real consumer that was 4,886
/// comment openers against 43 real URL literals, and 65 of 65 integration test files
/// selected for a one-line landing-page edit.
///
/// Both directions are pinned here. The narrowing tests fail against the pre-fix regex;
/// the recall tests fail against the obvious wrong fix — dropping the degenerate route
/// from matching altogether — which would silently stop selecting the landing page's own
/// tests. Selecting FEWER tests than are affected is the worse defect of the two.
module ``degenerate short route does not match comment syntax`` =

    let private rootRoute =
        [ { UrlPattern = "/"
            HttpMethod = "GET"
            HandlerSourceFile = "src/Handlers/Landing.fs"
            HandlerFunction = Some "Landing.index" } ]

    let private selectingRoot testFiles f =
        withTestSetup rootRoute testFiles "IntTests" "tests/IntTests" [ "src/Handlers/Landing.fs" ] f

    // -- narrowing: a comment is not a route reference ------------------------------

    [<Fact>]
    let ``a line comment does not select the file's tests`` () =
        let testContent =
            """type UnrelatedTests() =
    [<Fact>]
    member _.DoesSomethingElse() =
        // this comment mentions nothing, but its own `//` used to match route `/`
        ()
"""

        selectingRoot [ ("UnrelatedTests.fs", testContent) ] (fun result -> test <@ result |> List.isEmpty @>)

    [<Fact>]
    let ``a doc comment does not select the file's tests`` () =
        let testContent =
            """type UnrelatedTests() =
    /// Doc comments open with `///`, which also used to match route `/`.
    [<Fact>]
    member _.DoesSomethingElse() = ()
"""

        selectingRoot [ ("UnrelatedTests.fs", testContent) ] (fun result -> test <@ result |> List.isEmpty @>)

    [<Fact>]
    let ``a file-opening license header does not select the file's tests`` () =
        // The header sits at position 0, so a `^` alternative in the opening boundary would
        // let it match even after the `/` alternative is dropped. Requiring a quote closes it.
        let testContent =
            """// Copyright 2026. Licensed under the MIT licence.
type UnrelatedTests() =
    [<Fact>]
    member _.DoesSomethingElse() = ()
"""

        selectingRoot [ ("UnrelatedTests.fs", testContent) ] (fun result -> test <@ result |> List.isEmpty @>)

    [<Fact>]
    let ``a path separator inside a longer url does not select the file's tests`` () =
        let testContent =
            """type UnrelatedTests() =
    [<Fact>]
    member _.PostsToSomethingElse() =
        let url = "/admin/journal/translate"
        ()
"""

        selectingRoot [ ("UnrelatedTests.fs", testContent) ] (fun result -> test <@ result |> List.isEmpty @>)

    // -- recall: the degenerate route is still matched where it is really used ------

    [<Fact>]
    let ``a quoted root url still selects the test that navigates to it`` () =
        let testContent =
            """type LandingTests() =
    [<Fact>]
    member _.LoadsTheLandingPage() =
        // a comment here too, so the file cannot pass by having no `//` at all
        let url = "/"
        ()
"""

        selectingRoot [ ("LandingTests.fs", testContent) ] (fun result ->
            test
                <@
                    result = [ { TestProject = "IntTests"
                                 TestClass = "LandingTests" } ]
                @>)

    [<Fact>]
    let ``a root url carrying a query string still selects its test`` () =
        let testContent =
            """type LandingTests() =
    [<Fact>]
    member _.LoadsLocalisedLandingPage() =
        let url = "/?lang=en"
        ()
"""

        selectingRoot [ ("LandingTests.fs", testContent) ] (fun result ->
            test
                <@
                    result = [ { TestProject = "IntTests"
                                 TestClass = "LandingTests" } ]
                @>)

    [<Fact>]
    let ``a single-quoted root url still selects its test`` () =
        let testContent =
            """type LandingTests() =
    [<Fact>]
    member _.RunsScriptAgainstRoot() =
        let script = "window.location.pathname === '/'"
        ()
"""

        selectingRoot [ ("LandingTests.fs", testContent) ] (fun result ->
            test
                <@
                    result = [ { TestProject = "IntTests"
                                 TestClass = "LandingTests" } ]
                @>)

    [<Fact>]
    let ``only the class naming the root url is selected, not its commented sibling`` () =
        // Narrowing and recall in one file: the guard has to keep one class and drop the
        // other, so neither "select everything" nor "select nothing" can pass.
        let testContent =
            """type LandingTests() =
    [<Fact>]
    member _.LoadsTheLandingPage() =
        let url = "/"
        ()

type BillingTests() =
    [<Fact>]
    member _.ChargesACard() =
        // navigates nowhere near the landing page
        ()
"""

        selectingRoot [ ("LandingTests.fs", testContent) ] (fun result ->
            test
                <@
                    result = [ { TestProject = "IntTests"
                                 TestClass = "LandingTests" } ]
                @>)

    // -- the rule generalises past the root route, and stops there -------------------

    [<Fact>]
    let ``a param-only route does not match a comment but still matches a concrete url`` () =
        let testContent =
            """type LocaleTests() =
    [<Fact>]
    member _.LoadsGerman() =
        // a comment, which `/{lang}` also used to match
        let url = "/de"
        ()

type BillingTests() =
    [<Fact>]
    member _.ChargesACard() =
        // only a comment here
        ()
"""

        withTestSetup
            [ { UrlPattern = "/{lang}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Locale.fs"
                HandlerFunction = None } ]
            [ ("LocaleTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Locale.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "LocaleTests" } ]
                    @>)

    [<Fact>]
    let ``a route with literal text keeps matching after a doubled separator`` () =
        // The guard is scoped to text-free patterns. A normal route keeps the broader
        // opening boundary, so a doubled separator still reads as a path start.
        let testContent =
            """type UsersTests() =
    [<Fact>]
    member _.GetsUsers() =
        let url = "https://example.test//users"
        ()
"""

        withTestSetup
            [ { UrlPattern = "/users"
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


    // -- A `>]` inside an attribute string must not truncate -----------------------
    //
    // `[<Trait("k","v>]"); Fact>]` used to be read as `Trait("k","v` — the scan
    // stopped at the FIRST `>]`, which here sits inside a string literal. `Fact`
    // never appeared, `hasTestAttribute` returned false, and the test became
    // invisible to impact selection.
    //
    // The direction is what makes it serious. Over-selection costs time;
    // UNDER-selection costs a verdict — a genuinely affected test silently not
    // run, and a gate green over it. Driven end-to-end through `selectingRoot`
    // rather than against the scanner alone, so what is pinned is that the test
    // is SELECTED, not merely that a helper parses.

    [<Fact>]
    let ``a >] inside an attribute string still leaves the test selectable`` () =
        let testContent =
            "type TruncatedTests() =\n"
            + "    [<Trait(\"k\",\"v>]\"); Fact>]\n"
            + "    member _.NavigatesToRoot() =\n"
            + "        let url = \"/\"\n"
            + "        ignore url\n"

        selectingRoot [ ("TruncatedTests.fs", testContent) ] (fun result -> test <@ not (List.isEmpty result) @>)

    [<Fact>]
    let ``a verbatim string containing >] still leaves the test selectable`` () =
        // Verbatim strings escape a quote by DOUBLING it, so a scanner written for
        // backslash escapes walks off the end of this one.
        let testContent =
            "type VerbatimTests() =\n"
            + "    [<Trait(\"path\", @\"c:\\x[=<n>]\"); Fact>]\n"
            + "    member _.NavigatesToRoot() =\n"
            + "        let url = \"/\"\n"
            + "        ignore url\n"

        selectingRoot [ ("VerbatimTests.fs", testContent) ] (fun result -> test <@ not (List.isEmpty result) @>)

    [<Fact>]
    let ``a triple-quoted string containing >] still leaves the test selectable`` () =
        // Triple-quoted strings have no escapes at all — the only terminator is
        // the closing triple quote.
        let testContent =
            "type TripleTests() =\n"
            + "    [<Trait(\"doc\", \"\"\"use --wait[=<minutes>]\"\"\"); Fact>]\n"
            + "    member _.NavigatesToRoot() =\n"
            + "        let url = \"/\"\n"
            + "        ignore url\n"

        selectingRoot [ ("TripleTests.fs", testContent) ] (fun result -> test <@ not (List.isEmpty result) @>)

    [<Fact>]
    let ``a class with no test attribute is still NOT selected`` () =
        // The control, and the one that matters most here. A scanner that became
        // over-eager — treating any `[<…` as an attribute block, or failing to
        // close one and swallowing the file — would make every helper look
        // test-bearing. That is silent over-selection replacing silent
        // under-selection, and the first three tests above would not notice.
        let testContent =
            "type PlainHelper() =\n"
            + "    member _.Build() =\n"
            + "        let url = \"/\"\n"
            + "        ignore url\n"

        selectingRoot [ ("PlainHelper.fs", testContent) ] (fun result -> test <@ List.isEmpty result @>)

module ``symbolic route navigation`` =

    // A minimal Falco.UnionRoutes route DU with the `Admin → Settings` nesting the repro
    // navigates. `Route.Admin` carries the segment "admin", `AdminPages.Settings` the segment
    // "settings", so the composed URL is "/admin/settings". Marker/param types need not be
    // defined — the derivation reads only the DU's own `[<Route(Path=...)>]` attributes and
    // field TYPE NAMES (PreCondition is classified as a marker by name).
    let private adminRouteDu =
        """namespace Test.Routes

open Falco.UnionRoutes

type AdminPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
    | [<Route(RouteMethod.Get, Path = "qa")>] Qa

type Route =
    | [<Route(Path = "admin")>] Admin of PreCondition<AdminUserId> * AdminPages
"""

    /// A test that navigates to the route by its SYMBOLIC identifier —
    ///
    ///     Route.link (Route.Admin(NoPreCondition, AdminPages.Settings))
    ///
    /// the spelling the app encourages over hard-coding "/admin/settings" — carries no
    /// "/admin/settings" substring in its span, so a purely-textual URL matcher drops the
    /// covering test (under-selection). The extension instead derives
    /// `AdminPages.Settings → /admin/settings` from the route DU's `[<Route(Path=...)>]`
    /// attributes and matches the qualified case reference, resolving down to the SAME URL
    /// the literal matcher uses. Models a real consumer, where this `Route.link`
    /// spelling occurs in practice.
    [<Fact>]
    let ``test navigating a route only symbolically is selected`` () =
        let testContent =
            "type AdminSettingsTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.LoadsSettings() =\n        let url = Route.link (Route.Admin(NoPreCondition, AdminPages.Settings))\n        client.GetAsync(url) |> ignore\n"

        withTestSetupCore
            [ { UrlPattern = "/admin/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Admin.fs"
                HandlerFunction = Some "settings" } ]
            [ ("Routes.fs", adminRouteDu) ]
            [ ("AdminSettingsTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Admin.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "AdminSettingsTests" } ]
                    @>)

    /// A DIFFERENT route's symbolic navigation must NOT be selected: the `Qa` sibling case
    /// composes to "/admin/qa", which is not the changed handler's route. Guards against the
    /// derivation over-matching every case in the DU.
    [<Fact>]
    let ``symbolic navigation to a sibling route is not selected`` () =
        let testContent =
            "type AdminQaTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.LoadsQa() =\n        let url = Route.link (Route.Admin(NoPreCondition, AdminPages.Qa))\n        client.GetAsync(url) |> ignore\n"

        withTestSetupCore
            [ { UrlPattern = "/admin/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Admin.fs"
                HandlerFunction = Some "settings" } ]
            [ ("Routes.fs", adminRouteDu) ]
            [ ("AdminQaTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Admin.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

    /// A literal-URL test in the SAME repo still matches: the additive symbolic support does not
    /// disturb the existing string-route path.
    [<Fact>]
    let ``literal url still matches when a route DU is present`` () =
        let testContent =
            "type AdminSettingsLiteralTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.LoadsSettings() =\n        let! resp = client.GetAsync(\"/admin/settings\")\n        ()\n"

        withTestSetupCore
            [ { UrlPattern = "/admin/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Admin.fs"
                HandlerFunction = Some "settings" } ]
            [ ("Routes.fs", adminRouteDu) ]
            [ ("AdminSettingsLiteralTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Admin.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "AdminSettingsLiteralTests" } ]
                    @>)

    /// CONSTRAINT-INSENSITIVE matching. The host seeds the route table from `Route.info`, so a
    /// param route's pattern carries a constraint (`/admin/users/{id:guid}`) the source-derived
    /// composition cannot infer from a wrapped id type (it composes `/admin/users/{id}`). Matching
    /// the leaf constraint-insensitively selects the symbolic-nav test anyway — closing the gap
    /// with NO Falco.UnionRoutes dependency. This is the win the AST-normalized option delivers.
    [<Fact>]
    let ``symbolic nav to a constrained param route is selected constraint-insensitively`` () =
        let routeDu =
            """namespace Test.Routes

open Falco.UnionRoutes

type AdminUsers =
    | [<Route(RouteMethod.Get, Path = "users/{id}")>] Show of id: System.Guid

type Route =
    | [<Route(Path = "admin")>] Admin of AdminUsers
"""

        let testContent =
            "type AdminUserShowTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let url = Route.link (Route.Admin(AdminUsers.Show userId))\n        client.GetAsync(url) |> ignore\n"

        withTestSetupCore
            // The route table pattern carries the :guid constraint the AST cannot infer.
            [ { UrlPattern = "/admin/users/{id:guid}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/AdminUsers.fs"
                HandlerFunction = Some "show" } ]
            [ ("Routes.fs", routeDu) ]
            [ ("AdminUserShowTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/AdminUsers.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "AdminUserShowTests" } ]
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
    "type UsersTests() =\n    [<Fact>]\n    member _.GetUser() =\n        let url = \"/api/users/123\"\n        ()\n"

let private ordersTestFile =
    "type OrdersTests() =\n    [<Fact>]\n    member _.GetOrder() =\n        let url = \"/api/orders/456\"\n        ()\n"

module ``AnalyzeEdges function-scoped routes`` =

    let private declarationChecker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()

    [<Theory>]
    [<InlineData("type internal UsersTests() =", "UsersTests", true)>]
    [<InlineData("type public UsersTests() =", "UsersTests", true)>]
    [<InlineData("type private UsersTests() =", "UsersTests", true)>]
    [<InlineData("type internal ``Users contract``() =", "Users contract", true)>]
    [<InlineData("module Company.Product.UsersTests", "UsersTests", false)>]
    [<InlineData("module internal ``Users contract`` =", "Users contract", false)>]
    let ``declaration spellings preserve selection and handler edges`` (declaration: string) name isClass =
        let body =
            if isClass then
                "    [<Fact>]\n    member _.Exercises() = ignore \"/api/users/123\"\n"
            else
                "    [<Fact>]\n    let Exercises () = ignore \"/api/users/123\"\n"

        let source =
            if not isClass && not (declaration.Contains '=') then
                declaration + "\n\nopen Xunit\n\n" + body.Replace("    ", "")
            else
                "module App.Tests\n\nopen Xunit\n\n" + declaration + "\n" + body

        let testFiles =
            [ "UsersTests.fs", source
              "OrdersTests.fs", ordersTestFile
              "Helper.fs", "module Helper =\n    let url = \"/api/users/123\"\n" ]

        let route =
            { UrlPattern = "/api/users/{id}"
              HttpMethod = "GET"
              HandlerSourceFile = "src/Handlers/Users.fs"
              HandlerFunction = Some "App.Handlers.Users.getUser" }

        let fileName =
            Path.Combine(Path.GetTempPath(), $"FalcoDeclaration-{Guid.NewGuid():N}.fsx")

        let scriptOptions =
            getScriptOptions declarationChecker fileName source |> Async.RunSynchronously

        let options =
            { scriptOptions with
                OtherOptions =
                    Array.append scriptOptions.OtherOptions [| "-r:" + typeof<FactAttribute>.Assembly.Location |] }

        let analysis =
            match
                analyzeSource declarationChecker fileName source options "IntTests"
                |> Async.RunSynchronously
            with
            | Ok result -> result
            | Error message -> failwith message

        let sourceSymbol =
            analysis.TestMethods
            |> List.find (fun testMethod -> testMethod.TestMethod = "Exercises")
            |> _.SymbolFullName

        let symbols =
            (analysis.Symbols |> List.filter (fun symbol -> symbol.FullName = sourceSymbol))
            @ [ fn "App.Tests.OrdersTests.GetOrder" "tests/IntTests/OrdersTests.fs"
                fn "App.Handlers.Users.getUser" route.HandlerSourceFile ]

        withTestSetup [ route ] testFiles "IntTests" "tests/IntTests" [ route.HandlerSourceFile ] (fun selected ->
            test
                <@
                    selected = [ { TestProject = "IntTests"
                                   TestClass = name } ]
                @>)

        withAnalyzeEdges [ route ] symbols testFiles [ route.HandlerSourceFile ] (fun edges ->
            test <@ edges.Length = 1 @>
            test <@ edges.Head.FromSymbol = sourceSymbol @>
            test <@ edges.Head.ToSymbol = "App.Handlers.Users.getUser" @>
            test <@ edges.Head.Kind = SharedState && edges.Head.Source = "falco" @>)

    [<Fact>]
    let ``file module container retains shared header route attribution`` () =
        let content =
            "module Company.Product.Tests\n\nlet sharedUrl = \"/api/users/123\"\n\ntype UsersTests() =\n    [<Fact>]\n    member _.Exercises() = ignore sharedUrl\n\ntype OtherTests() =\n    [<Fact>]\n    member _.Exercises() = ignore sharedUrl\n"

        let route =
            { UrlPattern = "/api/users/{id}"
              HttpMethod = "GET"
              HandlerSourceFile = "src/Handlers/Users.fs"
              HandlerFunction = Some "App.Handlers.Users.getUser" }

        let testFiles = [ "Tests.fs", content ]

        withTestSetup [ route ] testFiles "IntTests" "tests/IntTests" [ route.HandlerSourceFile ] (fun selected ->
            test <@ selected |> List.map _.TestClass |> Set.ofList = set [ "UsersTests"; "OtherTests" ] @>)

        let symbols =
            [ fn "App.Tests.UsersTests.Exercises" "tests/IntTests/Tests.fs"
              fn "App.Tests.OtherTests.Exercises" "tests/IntTests/Tests.fs"
              fn "App.Handlers.Users.getUser" route.HandlerSourceFile ]

        withAnalyzeEdges [ route ] symbols testFiles [ route.HandlerSourceFile ] (fun edges ->
            let sources = edges |> List.map _.FromSymbol |> Set.ofList
            test <@ sources = set [ "App.Tests.UsersTests.Exercises"; "App.Tests.OtherTests.Exercises" ] @>
            test <@ edges |> List.forall (fun edge -> edge.ToSymbol = "App.Handlers.Users.getUser") @>)

    [<Fact>]
    let ``file module facts remain attributable before child declarations`` () =
        let content =
            "module Company.Product.Tests\n\n[<Fact>]\nlet Exercises () = ignore \"/api/users/123\"\n\ntype OtherTests() =\n    [<Fact>]\n    member _.Exercises() = ignore \"/api/orders/456\"\n"

        let route =
            { UrlPattern = "/api/users/{id}"
              HttpMethod = "GET"
              HandlerSourceFile = "src/Handlers/Users.fs"
              HandlerFunction = Some "App.Handlers.Users.getUser" }

        let testFiles = [ "Tests.fs", content ]

        withTestSetup [ route ] testFiles "IntTests" "tests/IntTests" [ route.HandlerSourceFile ] (fun selected ->
            test <@ selected |> List.map _.TestClass = [ "Tests" ] @>)

        let symbols =
            [ fn "Company.Product.Tests.Exercises" "tests/IntTests/Tests.fs"
              fn "Company.Product.Tests.OtherTests.Exercises" "tests/IntTests/Tests.fs"
              fn "App.Handlers.Users.getUser" route.HandlerSourceFile ]

        withAnalyzeEdges [ route ] symbols testFiles [ route.HandlerSourceFile ] (fun edges ->
            test
                <@
                    edges
                    |> List.exists (fun edge -> edge.FromSymbol = "Company.Product.Tests.Exercises")
                @>

            test <@ edges |> List.forall (fun edge -> edge.ToSymbol = "App.Handlers.Users.getUser") @>)

    /// A change to a multi-route handler file, with each route mapped to its own
    /// handler function, links each route's tests ONLY to that route's function —
    /// not the file-level cross-product (UsersTests -> getOrder and back).
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

                // No cross-route edges.
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
            "type RobotsTests() =\n    [<Fact>]\n    member _.GetRobots() =\n        let url = \"/robots.txt\"\n        ()\n"

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

module ``run selection and edge participation are different questions`` =

    /// The same file, the same route, the same scan — two different right answers
    /// (see `RouteMatch` in FalcoRouteAnalysis.fs):
    ///
    ///   * `FindAffectedTestClasses` must NOT return the fixture. It holds no
    ///     test method, so filtering to it would run nothing.
    ///   * `AnalyzeEdges` MUST emit the fixture's symbols. The fixture is what
    ///     calls the endpoint, and what the tests in every other file depend on.
    ///
    /// The test class here reaches the route ONLY through the fixture: its own
    /// span carries no URL literal, exactly like a real integration test.
    [<Fact>]
    let ``fixture is excluded from the test-class list but included in the route's edges`` () =
        let fixtureAndTest =
            """type IntegrationTestFixture() =
    member _.Login() =
        let url = "/api/users/123"
        ()

type UsersTests(fixture: IntegrationTestFixture) =
    [<Fact>]
    member _.GetUser() = ignore (fixture.Login())
"""

        let routes =
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "Users.getUser" } ]

        // (1) run selection: the fixture is not a test class.
        withTestSetup
            routes
            [ ("UsersTests.fs", fixtureAndTest) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTests" } ]
                    @>)

        // (2) edges: the fixture's symbols DO participate, alongside the test's.
        let symbols =
            [ fn "App.Handlers.Users.getUser" "src/Handlers/Users.fs"
              fn "App.Tests.IntegrationTestFixture.Login" "tests/IntTests/UsersTests.fs"
              fn "App.Tests.UsersTests.GetUser" "tests/IntTests/UsersTests.fs" ]

        withAnalyzeEdges routes symbols [ ("UsersTests.fs", fixtureAndTest) ] [ "src/Handlers/Users.fs" ] (fun edges ->
            let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

            test
                <@
                    pairs = set
                        [ "App.Tests.IntegrationTestFixture.Login", "App.Handlers.Users.getUser"
                          "App.Tests.UsersTests.GetUser", "App.Handlers.Users.getUser" ]
                @>)

    /// The same asymmetry where the fixture is the ONLY declaration that can
    /// reach the route: a file of pure fixtures yields no test class to run, yet
    /// still carries the route on the edge path. Returning nothing from BOTH
    /// would strand every test that depends on that fixture.
    [<Fact>]
    let ``file of nothing but fixtures still contributes edges`` () =
        let fixtureOnly =
            """type TestServer(dbName: string) =
    member _.Login() =
        let url = "/api/users/123"
        ()
"""

        let routes =
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "Users.getUser" } ]

        let symbols =
            [ fn "App.Handlers.Users.getUser" "src/Handlers/Users.fs"
              fn "App.Tests.TestServer.Login" "tests/IntTests/TestServerFixture.fs" ]

        withAnalyzeEdges
            routes
            symbols
            [ ("TestServerFixture.fs", fixtureOnly) ]
            [ "src/Handlers/Users.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList
                test <@ pairs = set [ "App.Tests.TestServer.Login", "App.Handlers.Users.getUser" ] @>)

    /// The one case where the edge path must still fall back: a URL in the file
    /// HEADER belongs to no declaration, so any of them could reach the route
    /// through it. Every declaration becomes a carrier — here the fixture, which
    /// is the file's only declaration and yields no test class to run.
    [<Fact>]
    let ``URL in the file header makes every declaration an edge carrier`` () =
        let headerUrl =
            """module Tests.Fixtures.BrowserFixtures

let loginUrl = "/api/users/123"

type BrowserErrorTracker() =
    member _.Track() = ()
"""

        let routes =
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "Users.getUser" } ]

        // No test-bearing declaration, so nothing to run...
        withTestSetup
            routes
            [ ("BrowserFixtures.fs", headerUrl) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

        // ...yet the fixture still carries the route on the edge path.
        let symbols =
            [ fn "App.Handlers.Users.getUser" "src/Handlers/Users.fs"
              fn "App.Tests.BrowserErrorTracker.Track" "tests/IntTests/BrowserFixtures.fs" ]

        withAnalyzeEdges routes symbols [ ("BrowserFixtures.fs", headerUrl) ] [ "src/Handlers/Users.fs" ] (fun edges ->
            let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList
            test <@ pairs = set [ "App.Tests.BrowserErrorTracker.Track", "App.Handlers.Users.getUser" ] @>)

    /// Edge participation stays scoped to the route: a fixture that mentions a
    /// DIFFERENT route contributes nothing to this one. Widening the edge path
    /// must not reintroduce the file-level cross-product.
    [<Fact>]
    let ``fixture matching a different route contributes no edges to this one`` () =
        let fixtureAndTest =
            """type OrdersFixture() =
    member _.Setup() =
        let url = "/api/orders/456"
        ()

type UsersTests(fixture: OrdersFixture) =
    [<Fact>]
    member _.GetUser() =
        let url = "/api/users/123"
        ()
"""

        let symbols =
            [ fn "App.Handlers.Users.getUser" "src/Handlers/Users.fs"
              fn "App.Tests.OrdersFixture.Setup" "tests/IntTests/UsersTests.fs"
              fn "App.Tests.UsersTests.GetUser" "tests/IntTests/UsersTests.fs" ]

        withAnalyzeEdges
            [ { UrlPattern = "/api/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "Users.getUser" } ]
            symbols
            [ ("UsersTests.fs", fixtureAndTest) ]
            [ "src/Handlers/Users.fs" ]
            (fun edges ->
                let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

                test <@ pairs = set [ "App.Tests.UsersTests.GetUser", "App.Handlers.Users.getUser" ] @>
                test <@ not (pairs.Contains("App.Tests.OrdersFixture.Setup", "App.Handlers.Users.getUser")) @>)

module ``AnalyzeEdges fallback`` =

    /// With HandlerFunction = None the route falls back to the coarse file-level
    /// cross-product — every symbol in the changed file linked to the
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

// -----------------------------------------------------------------------------
// UnionRouteLinks: case → URL derivation from the route DU
// -----------------------------------------------------------------------------

module ``UnionRouteLinks case-to-url composition`` =

    let private linksOf (du: string) =
        UnionRouteLinks.buildLinkMap [ ("Routes.fs", du) ]

    [<Fact>]
    let ``explicit paths concatenate up the nesting`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type AdminPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
type Route =
    | [<Route(Path = "admin")>] Admin of PreCondition<AdminUserId> * AdminPages
"""

        let map = linksOf du
        test <@ Map.tryFind "AdminPages.Settings" map = Some(set [ "/admin/settings" ]) @>

    [<Fact>]
    let ``an empty parent path is skipped in the concatenation`` () =
        // `User` is a pass-through parent (Path = ""), so the child's segment stands alone.
        let du =
            """namespace R
open Falco.UnionRoutes
type UserPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
type Route =
    | [<Route(Path = "")>] User of PreCondition<UserId> * UserPages
"""

        let map = linksOf du
        test <@ Map.tryFind "UserPages.Settings" map = Some(set [ "/settings" ]) @>

    [<Fact>]
    let ``a case with no path attribute falls back to its kebab-cased name`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type AdminPages =
    | HealthScores
type Route =
    | [<Route(Path = "admin")>] Admin of AdminPages
"""

        let map = linksOf du
        test <@ Map.tryFind "AdminPages.HealthScores" map = Some(set [ "/admin/health-scores" ]) @>

    [<Fact>]
    let ``an empty-segment convention name contributes no segment`` () =
        // `Root` is an EmptySegmentName, so `Journal.Root` is just the parent's "journal".
        let du =
            """namespace R
open Falco.UnionRoutes
type JournalRoute =
    | Root
type Route =
    | [<Route(Path = "journal")>] Journal of JournalRoute
"""

        let map = linksOf du
        test <@ Map.tryFind "JournalRoute.Root" map = Some(set [ "/journal" ]) @>

    [<Fact>]
    let ``a repo with no route DU yields an empty map`` () =
        test <@ UnionRouteLinks.buildLinkMap [] = Map.empty @>

    [<Fact>]
    let ``leaf regexes fire only for in-scope urls and require a qualified reference`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type AdminPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
    | [<Route(RouteMethod.Get, Path = "qa")>] Qa
type Route =
    | [<Route(Path = "admin")>] Admin of AdminPages
"""

        let map = linksOf du
        let regexes = UnionRouteLinks.leafReferenceRegexes map (set [ "/admin/settings" ])

        // Exactly one leaf (Settings) is in scope; Qa is not.
        test <@ regexes.Length = 1 @>

        let matches (text: string) =
            regexes |> List.exists (fun r -> r.IsMatch text)

        test <@ matches "Route.link (Route.Admin(NoPreCondition, AdminPages.Settings))" @>
        test <@ not (matches "Route.link (Route.Admin(NoPreCondition, AdminPages.Qa))") @>
        // A longer identifier that merely ENDS in the leaf name must not match.
        test <@ not (matches "MyAdminPages.SettingsExtra") @>

module ``UnionRouteLinks additional composition rules`` =

    let private linksOf (du: string) =
        UnionRouteLinks.buildLinkMap [ ("Routes.fs", du) ]

    [<Fact>]
    let ``a non-empty-segment param case is kebab plus the field name`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type UserPages =
    | Profile of id: System.Guid
type Route =
    | [<Route(Path = "")>] User of UserPages
"""

        let map = linksOf du
        test <@ Map.tryFind "UserPages.Profile" map = Some(set [ "/profile/{id}" ]) @>

    [<Fact>]
    let ``an empty-segment param case is just the field name`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type AdminBriefs =
    | [<Route(Path = "briefs")>] Show of id: System.Guid
type Route =
    | [<Route(Path = "admin")>] Admin of AdminBriefs
"""

        // `Show` carries an explicit Path here, so it stays "briefs"; the param field is
        // still surfaced as `{id}` on the explicit path is NOT auto-appended — explicit wins.
        let map = linksOf du
        test <@ Map.tryFind "AdminBriefs.Show" map = Some(set [ "/admin/briefs" ]) @>

    [<Fact>]
    let ``a convention empty-segment case with a param surfaces only the param`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type AdminBriefs =
    | Show of id: System.Guid
type Route =
    | [<Route(Path = "admin")>] Admin of AdminBriefs
"""

        let map = linksOf du
        test <@ Map.tryFind "AdminBriefs.Show" map = Some(set [ "/admin/{id}" ]) @>

    [<Fact>]
    let ``route DUs declared inside a nested module are still found`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
module Domain =
    type AdminPages =
        | [<Route(RouteMethod.Get, Path = "settings")>] Settings
    type Route =
        | [<Route(Path = "admin")>] Admin of AdminPages
"""

        let map = linksOf du
        test <@ Map.tryFind "AdminPages.Settings" map = Some(set [ "/admin/settings" ]) @>

    [<Fact>]
    let ``a self-referential route union terminates without a leaf`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type Route =
    | [<Route(Path = "a")>] A of Route
"""

        // The cycle guard stops the walk; `A` never reaches a terminal leaf, so no link.
        test <@ linksOf du = Map.empty @>

    [<Fact>]
    let ``buildLinkMapFromRepo on a missing directory is empty`` () =
        test <@ UnionRouteLinks.buildLinkMapFromRepo "/no/such/dir/testprune-223" = Map.empty @>

module ``UnionRouteLinks nesting edge cases`` =

    let private linksOf (du: string) =
        UnionRouteLinks.buildLinkMap [ ("Routes.fs", du) ]

    [<Fact>]
    let ``a leaf shared by two parents maps to both composed urls`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type Shared =
    | [<Route(RouteMethod.Get, Path = "x")>] X
type Route =
    | [<Route(Path = "a")>] A of Shared
    | [<Route(Path = "b")>] B of Shared
"""

        let map = linksOf du
        test <@ Map.tryFind "Shared.X" map = Some(set [ "/a/x"; "/b/x" ]) @>

    [<Fact>]
    let ``a top-level leaf route with no nesting composes its own segment`` () =
        let du =
            """namespace R
open Falco.UnionRoutes
type Route =
    | [<Route(RouteMethod.Get, Path = "health")>] Health
"""

        let map = linksOf du
        test <@ Map.tryFind "Route.Health" map = Some(set [ "/health" ]) @>

// -----------------------------------------------------------------------------
// String-route navigation via a named URL constant (string-route brief)
// -----------------------------------------------------------------------------

module ``string route via named url constant`` =

    // An app module that defines route URLs as named constants in ONE place — the pattern that
    // makes `navigateTo Routes.settingsUrl` (no "/settings" literal in the test) possible.
    let private routesModule =
        """namespace App

module Routes =
    let settingsUrl = "/settings"
    let dashboardUrl = "/dashboard"
"""

    /// A test navigates via a CROSS-FILE named URL constant, so its own span holds no
    /// "/settings" literal. The constant is resolved to its literal value and the
    /// reference matched; a purely-textual matcher would drop the test.
    [<Fact>]
    let ``a test referencing a cross-file url constant for an affected route is selected`` () =
        let testContent =
            """type SettingsNavTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.Loads() =
        navigateTo Routes.settingsUrl |> ignore
"""

        withTestSetupCore
            [ { UrlPattern = "/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/User.fs"
                HandlerFunction = Some "settings" } ]
            [ ("Routes.fs", routesModule) ]
            [ ("SettingsNavTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/User.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "SettingsNavTests" } ]
                    @>)

    /// A same-file URL constant reference is selected too.
    [<Fact>]
    let ``a test referencing a same-file url constant is selected`` () =
        let testContent =
            """module Urls =
    let settingsUrl = "/settings"

type SettingsSameFileTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.Loads() = navigateTo Urls.settingsUrl |> ignore
"""

        withTestSetupCore
            [ { UrlPattern = "/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/User.fs"
                HandlerFunction = Some "settings" } ]
            []
            [ ("SettingsSameFileTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/User.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "SettingsSameFileTests" } ]
                    @>)

    /// PRECISION GUARD: a test referencing a constant whose value is NOT the affected route is
    /// NOT selected. `Routes.dashboardUrl = "/dashboard"` must not fire when only `/settings`
    /// changed — proving constants contribute only when their literal value matches an affected
    /// route, so there is no blanket over-selection.
    [<Fact>]
    let ``a constant whose value is not the affected route does not select`` () =
        let testContent =
            """type DashboardNavTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.Loads() = navigateTo Routes.dashboardUrl |> ignore
"""

        withTestSetupCore
            [ { UrlPattern = "/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/User.fs"
                HandlerFunction = Some "settings" }
              { UrlPattern = "/dashboard"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Dashboard.fs"
                HandlerFunction = Some "dashboard" } ]
            [ ("Routes.fs", routesModule) ]
            [ ("DashboardNavTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/User.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

    /// PRECISION GUARD: an unrelated URL constant referenced by a test does not select when its
    /// route is not affected — `let apiBase = "/api"` fires only if `/api` itself changed.
    [<Fact>]
    let ``an unrelated url constant does not cause selection`` () =
        let constantsFile =
            """namespace App

module Endpoints =
    let apiBase = "/api"
"""

        let testContent =
            """type ApiBaseTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.Loads() = navigateTo Endpoints.apiBase |> ignore
"""

        withTestSetupCore
            [ { UrlPattern = "/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/User.fs"
                HandlerFunction = Some "settings" } ]
            [ ("Endpoints.fs", constantsFile) ]
            [ ("ApiBaseTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/User.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

    /// Trailing-slash tolerance (#3): a literal `"/users/"` matches route `/users`. Proves the
    /// `/?` boundary addition, which must NOT enable parent-prefix matching (see the guard below).
    [<Fact>]
    let ``a trailing-slash literal matches the slashless route`` () =
        let testContent =
            "type UsersTrailingTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let r = client.GetAsync(\"/users/\")\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/users"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "list" } ]
            [ ("UsersTrailingTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "UsersTrailingTests" } ]
                    @>)

    /// SOUNDNESS GUARD for #3: trailing-slash tolerance must NOT become parent-prefix matching.
    /// A test hitting `/users/123` must NOT be selected for a change to route `/users` (a
    /// different route, `/users/{id}`, owns that request).
    [<Fact>]
    let ``a child path does not match the slashless parent route`` () =
        let testContent =
            "type UsersChildTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let r = client.GetAsync(\"/users/123\")\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/users"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "list" } ]
            [ ("UsersChildTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result -> test <@ result |> List.isEmpty @>)

// -----------------------------------------------------------------------------
// Dynamic URL forms already matched by the raw-text matcher (#1 lock-in)
// -----------------------------------------------------------------------------

module ``dynamic url forms are matched incidentally`` =

    // urlPatternToRegex compiles a route param to `[^/]+` and matches raw source text, so several
    // dynamic URL spellings already resolve WITHOUT dedicated machinery. These lock that in as
    // intentional, documented behaviour. The residual gap — a LEADING dynamic prefix
    // (`$"{computedBase}/settings"`), where the route start is not at a clean boundary — stays
    // unmatched unless the base is a NAMED constant resolvable to a full route URL.

    /// An interpolated URL `$"/users/{userId}"` matches route `/users/{id}`: the `{userId}` fill is
    /// slash-free, so `[^/]+` matches it and the surrounding quotes are clean boundaries.
    [<Fact>]
    let ``an interpolated url with a brace fill matches a param route`` () =
        let testContent =
            "type InterpolatedNavTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let url = $\"/users/{userId}\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "get" } ]
            [ ("InterpolatedNavTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "InterpolatedNavTests" } ]
                    @>)

    /// A printf-style format `sprintf "/users/%d" id` matches route `/users/{id}`: `%d` is
    /// slash-free.
    [<Fact>]
    let ``a printf-format url matches a param route`` () =
        let testContent =
            "type PrintfNavTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let url = sprintf \"/users/%d\" id\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/users/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Users.fs"
                HandlerFunction = Some "get" } ]
            [ ("PrintfNavTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/Users.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "PrintfNavTests" } ]
                    @>)

    /// A literal-suffix concatenation `baseUrl + "/settings"` matches route `/settings`: the
    /// `"/settings"` literal is quote-bounded in the text, so it matches like any literal. (Only a
    /// DYNAMIC leading prefix would hide the route start — see the module doc.)
    [<Fact>]
    let ``a literal-suffix concatenation matches its route`` () =
        let testContent =
            "type ConcatNavTests(output: ITestOutputHelper) =\n    [<Fact>]\n    member _.Loads() =\n        let url = baseUrl + \"/settings\"\n        ()\n"

        withTestSetup
            [ { UrlPattern = "/settings"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/User.fs"
                HandlerFunction = Some "settings" } ]
            [ ("ConcatNavTests.fs", testContent) ]
            "IntTests"
            "tests/IntTests"
            [ "src/Handlers/User.fs" ]
            (fun result ->
                test
                    <@
                        result = [ { TestProject = "IntTests"
                                     TestClass = "ConcatNavTests" } ]
                    @>)

// -----------------------------------------------------------------------------
// StringRouteConstants: constant → URL derivation (unit)
// -----------------------------------------------------------------------------

module ``StringRouteConstants derivation`` =

    open System.Text.RegularExpressions

    let private mapOf (source: string) (affected: string list) =
        StringRouteConstants.buildConstantMap [ ("Routes.fs", source) ] (Set.ofList affected)

    [<Fact>]
    let ``a module-qualified url constant is captured with its value`` () =
        let source =
            """namespace App
module Routes =
    let settingsUrl = "/settings"
"""

        let map = mapOf source [ "/settings" ]
        test <@ Map.tryFind "Routes.settingsUrl" map = Some(set [ "/settings" ]) @>

    [<Fact>]
    let ``a constant in a nested module is qualified by the innermost module`` () =
        let source =
            """namespace App
module Web =
    module Routes =
        let settingsUrl = "/settings"
"""

        let map = mapOf source [ "/settings" ]
        test <@ Map.tryFind "Routes.settingsUrl" map = Some(set [ "/settings" ]) @>

    [<Fact>]
    let ``a typed url constant binding is still captured`` () =
        let source =
            """namespace App
module Routes =
    let settingsUrl: string = "/settings"
"""

        let map = mapOf source [ "/settings" ]
        test <@ Map.tryFind "Routes.settingsUrl" map = Some(set [ "/settings" ]) @>

    [<Fact>]
    let ``a non-url string constant is ignored`` () =
        // Value does not start with '/', so it is not a route URL. The file still mentions the
        // affected url in a comment, so it is parsed — but the binding is not captured.
        let source =
            """namespace App
// affected: /settings
module Config =
    let greeting = "hello"
"""

        let map = mapOf source [ "/settings" ]
        test <@ map = Map.empty @>

    [<Fact>]
    let ``a file not mentioning an affected url is not parsed`` () =
        let source =
            """namespace App
module Routes =
    let ordersUrl = "/orders"
"""

        // Only /settings is affected; the file mentions neither /settings nor any affected url.
        let map = mapOf source [ "/settings" ]
        test <@ map = Map.empty @>

    [<Fact>]
    let ``reference regexes fire only for constants matching an affected url`` () =
        let source =
            """namespace App
module Routes =
    let settingsUrl = "/settings"
    let dashboardUrl = "/dashboard"
"""

        let map = mapOf source [ "/settings"; "/dashboard" ]
        // Only /settings is affected at match time.
        let affected = [ Regex(@"^/settings$") ]
        let regexes = StringRouteConstants.constantReferenceRegexes map affected

        let matches (t: string) =
            regexes |> List.exists (fun r -> r.IsMatch t)

        test <@ matches "Routes.settingsUrl" @>
        test <@ matches "settingsUrl" @> // bare, unique
        test <@ not (matches "Routes.dashboardUrl") @>
        test <@ not (matches "dashboardUrl") @>
        // A longer identifier merely ending in the name must not match.
        test <@ not (matches "MyRoutes.settingsUrlX") @>

    [<Fact>]
    let ``a bare name shared by two constants is not emitted as a bare regex`` () =
        // Two modules both define `settingsUrl`; the bare form is ambiguous, so only the qualified
        // references are emitted.
        let source =
            """namespace App
module UserRoutes =
    let settingsUrl = "/settings"
module AdminRoutes =
    let settingsUrl = "/settings"
"""

        let map =
            StringRouteConstants.buildConstantMap [ ("Routes.fs", source) ] (set [ "/settings" ])

        let affected = [ Regex(@"^/settings$") ]
        let regexes = StringRouteConstants.constantReferenceRegexes map affected

        let matches (t: string) =
            regexes |> List.exists (fun r -> r.IsMatch t)

        test <@ matches "UserRoutes.settingsUrl" @>
        test <@ matches "AdminRoutes.settingsUrl" @>
        // Bare `settingsUrl` is ambiguous across the two modules → not emitted.
        test <@ not (matches " settingsUrl ") @>

module ``UnionRouteLinks repo scan`` =

    [<Fact>]
    let ``buildLinkMapFromRepo reads route DU files under a directory`` () =
        let dir = Path.Combine(Path.GetTempPath(), $"url-links-repo-%A{Guid.NewGuid()}")

        Directory.CreateDirectory dir |> ignore

        try
            let src = Path.Combine(dir, "src")
            Directory.CreateDirectory src |> ignore

            File.WriteAllText(
                Path.Combine(src, "Routes.fs"),
                """namespace R
open Falco.UnionRoutes
type AdminPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
type Route =
    | [<Route(Path = "admin")>] Admin of AdminPages
"""
            )

            let map = UnionRouteLinks.buildLinkMapFromRepo dir
            test <@ Map.tryFind "AdminPages.Settings" map = Some(set [ "/admin/settings" ]) @>
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

module ``StringRouteConstants extra`` =

    [<Fact>]
    let ``a parenthesised url literal is captured`` () =
        let source =
            """namespace App
module Routes =
    let settingsUrl = ("/settings")
"""

        let map =
            StringRouteConstants.buildConstantMap [ ("Routes.fs", source) ] (set [ "/settings" ])

        test <@ Map.tryFind "Routes.settingsUrl" map = Some(set [ "/settings" ]) @>
