module TestPrune.Tests.FalcoRouteExtensionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
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

        db.RebuildRouteHandlers(routeEntries)

        let testDir = Path.Combine(tempDir, integrationTestSubDir)

        if testFiles |> List.isEmpty |> not then
            Directory.CreateDirectory(testDir) |> ignore

            for (fileName, content) in testFiles do
                File.WriteAllText(Path.Combine(testDir, fileName), content)

        let routeStore = toRouteStore db

        let extension =
            FalcoRouteExtension(integrationTestProject, integrationTestSubDir, routeStore)

        let result = extension.FindAffectedTestClasses(changedFiles, tempDir)

        f result
    finally
        cleanupDir tempDir

module ``debug db roundtrip`` =

    [<Fact>]
    let ``route handlers survive roundtrip`` () =
        let tempDir = createTempDir ()

        try
            let dbPath = Path.Combine(tempDir, "test.db")
            let db = Database.create dbPath

            db.RebuildRouteHandlers(
                [ { UrlPattern = "/api/users/{id}"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/Handlers/Users.fs"
                    HandlerFunction = None } ]
            )

            let hsf = db.GetAllHandlerSourceFiles()
            test <@ hsf = set [ "src/Handlers/Users.fs" ] @>

            let urls = db.GetUrlPatternsForSourceFile("src/Handlers/Users.fs")
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

            let routeStore = toRouteStore db
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
        db.RebuildRouteHandlers(routeEntries)

        let testDir = Path.Combine(tempDir, "tests/IntTests")
        Directory.CreateDirectory(testDir) |> ignore

        for (fileName, content) in testFiles do
            File.WriteAllText(Path.Combine(testDir, fileName), content)

        let symbolStore =
            TestPrune.InMemoryStore.fromAnalysisResults [ AnalysisResult.Create(symbols, [], []) ]

        let extension =
            FalcoRouteExtension("IntTests", "tests/IntTests", toRouteStore db) :> ITestPruneExtension

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
