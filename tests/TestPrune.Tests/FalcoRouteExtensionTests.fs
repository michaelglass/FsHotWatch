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
        let extension = FalcoRouteExtension(integrationTestProject, integrationTestSubDir, routeStore)

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
                    HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" } ]
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
                HandlerSourceFile = "src/Handlers/Users.fs" }
              { UrlPattern = "/api/orders/{id}"
                HttpMethod = "GET"
                HandlerSourceFile = "src/Handlers/Orders.fs" } ]
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
                HandlerSourceFile = "src/Handlers/UserPosts.fs" } ]
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
