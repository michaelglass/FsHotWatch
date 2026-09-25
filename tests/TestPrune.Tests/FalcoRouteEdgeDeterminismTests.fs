/// Route edges are a function of the TREE: the route table, the handler symbols and the
/// integration test sources. They must not depend on which files the building run happened
/// to see as changed, nor on what earlier builds left behind in the index.
module TestPrune.Tests.FalcoRouteEdgeDeterminismTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports
open TestPrune.Extensions
open TestPrune.Falco

let private usersHandler = "src/Handlers/Users.fs"
let private ordersHandler = "src/Handlers/Orders.fs"
let private unrelatedSource = "src/Other.fs"

let private usersRoute =
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = usersHandler
      HandlerFunction = Some "Users.getUser" }

let private ordersRoute =
    { UrlPattern = "/api/orders/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = ordersHandler
      HandlerFunction = Some "Orders.getOrder" }

let private symbol (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 2
      ContentHash = "h"
      IsExtern = false }

/// One source file of the tree: its path, its text (written to disk for test files), and the
/// symbols its AST analysis would record.
type private TreeFile =
    { Path: string
      Text: string
      Symbols: string list }

let private testFile (name: string) (className: string) (url: string) =
    { Path = $"tests/IntTests/%s{name}"
      Text = $"type %s{className}() =\n    [<Fact>]\n    member _.Run() =\n        let url = \"%s{url}\"\n        ()\n"
      Symbols = [ $"App.Tests.%s{className}.Run" ] }

let private baseTree =
    [ { Path = usersHandler
        Text = ""
        Symbols = [ "App.Handlers.Users.getUser"; "App.Handlers.Users.helper" ] }
      { Path = ordersHandler
        Text = ""
        Symbols = [ "App.Handlers.Orders.getOrder" ] }
      { Path = unrelatedSource
        Text = ""
        Symbols = [ "App.Other.compute" ] }
      testFile "UsersTests.fs" "UsersTests" "/api/users/123"
      testFile "OrdersTests.fs" "OrdersTests" "/api/orders/456" ]

let private baseEdges =
    set
        [ "App.Tests.UsersTests.Run", "App.Handlers.Users.getUser"
          "App.Tests.OrdersTests.Run", "App.Handlers.Orders.getOrder" ]

/// A repo on disk plus the index a host keeps for it across builds.
type private Repo =
    { Root: string
      Db: Database
      Routes: RouteStore
      Extension: ITestPruneExtension }

/// Write `files` into the repo and record their AST facts, as a host does for the files a
/// build re-analyses.
let private analyse (repo: Repo) (files: TreeFile list) =
    for file in files do
        if file.Text <> "" then
            let path = Path.Combine(repo.Root, file.Path)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, file.Text)

    repo.Db.RebuildProjects(
        files
        |> List.map (fun file ->
            AnalysisResult.Create(file.Symbols |> List.map (fun name -> symbol name file.Path), [], []))
    )

/// One index build as a host runs it: the build has already re-analysed the files it saw
/// change, and now refreshes the extension-contributed edges. The change set is part of each
/// scenario because a host has one, but nothing the refresh does may depend on it.
let private indexBuild (repo: Repo) (_changedFiles: string list) =
    let outcome = refreshExtensionEdges repo.Db repo.Root [ repo.Extension ]

    match outcome with
    | [ Refreshed("Falco Routes", _) ] -> ()
    | other -> failwith $"Falco edge refresh did not complete: %A{other}"

/// The Falco edges the index currently holds, as (from, to) pairs.
let private storedRouteEdges (repo: Repo) : Set<string * string> =
    use conn = repo.Db.OpenConnection()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        """
        SELECT f.full_name, t.full_name
        FROM dependencies d
        JOIN symbols f ON f.id = d.from_symbol_id
        JOIN symbols t ON t.id = d.to_symbol_id
        WHERE d.source = 'falco'
        """

    use reader = cmd.ExecuteReader()

    [ while reader.Read() do
          yield reader.GetString 0, reader.GetString 1 ]
    |> Set.ofList

/// A fresh repo holding `tree`, analysed in full, with `routes` seeded.
let private withRepo (tree: TreeFile list) (routes: RouteHandlerEntry list) (body: Repo -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"falco-determinism-{Guid.NewGuid():N}")

    Directory.CreateDirectory root |> ignore

    try
        let db = Database.create (Path.Combine(root, "index.db"))
        let routeStore = RouteStore(toPluginStore db)
        routeStore.Rebuild routes

        let repo =
            { Root = root
              Db = db
              Routes = routeStore
              Extension = FalcoRouteExtension("IntTests", "tests/IntTests", routeStore) }

        analyse repo tree
        body repo
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

/// The change sets a cold first build can see. The route edges it leaves behind must not
/// depend on which one it was.
let firstBuildChangeSets: TheoryData<string list> =
    TheoryData<string list>(
        [ [ usersHandler; ordersHandler; unrelatedSource ]
          [ usersHandler ]
          [ unrelatedSource ]
          [] ]
    )

[<Theory>]
[<MemberData(nameof firstBuildChangeSets)>]
let ``two consecutive builds over an unchanged tree hold the same route edges`` (firstChangeSet: string list) =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo firstChangeSet
        let afterFirst = storedRouteEdges repo

        indexBuild repo []
        let afterSecond = storedRouteEdges repo

        test <@ afterFirst = baseEdges @>
        test <@ afterSecond = afterFirst @>)

[<Fact>]
let ``a build after an unrelated change neither adds nor loses route edges`` () =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo [ usersHandler; ordersHandler ]
        test <@ storedRouteEdges repo = baseEdges @>

        let edited =
            { Path = unrelatedSource
              Text = ""
              Symbols = [ "App.Other.compute"; "App.Other.computeMore" ] }

        analyse repo [ edited ]
        indexBuild repo [ unrelatedSource ]

        test <@ storedRouteEdges repo = baseEdges @>)

[<Fact>]
let ``a test added without touching its handler gets its route edge`` () =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo [ usersHandler; ordersHandler ]

        let added = testFile "MoreOrdersTests.fs" "MoreOrdersTests" "/api/orders/789"
        analyse repo [ added ]
        indexBuild repo [ added.Path ]

        let expected =
            baseEdges
            |> Set.add ("App.Tests.MoreOrdersTests.Run", "App.Handlers.Orders.getOrder")

        test <@ storedRouteEdges repo = expected @>)

[<Fact>]
let ``removing a handler from the route table removes its edges`` () =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo [ usersHandler; ordersHandler ]
        test <@ storedRouteEdges repo = baseEdges @>

        repo.Routes.Rebuild [ usersRoute ]
        indexBuild repo [ ordersHandler ]

        test <@ storedRouteEdges repo = set [ "App.Tests.UsersTests.Run", "App.Handlers.Users.getUser" ] @>)

[<Fact>]
let ``repointing a route at another handler function moves its edges`` () =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo [ usersHandler; ordersHandler ]

        repo.Routes.Rebuild
            [ { usersRoute with
                  HandlerFunction = Some "Users.helper" }
              ordersRoute ]

        indexBuild repo [ usersHandler ]

        let expected =
            set
                [ "App.Tests.UsersTests.Run", "App.Handlers.Users.helper"
                  "App.Tests.OrdersTests.Run", "App.Handlers.Orders.getOrder" ]

        test <@ storedRouteEdges repo = expected @>)

/// The extension outlives a single build in a daemon host, so nothing it read from the repo
/// on one build may stand in for the repo on the next.
[<Fact>]
let ``a URL constant introduced after the first build is seen by the next`` () =
    withRepo baseTree [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo [ usersHandler; ordersHandler ]

        let constants =
            { Path = "src/Routes.fs"
              Text = "namespace App\nmodule Routes =\n    let orderUrl = \"/api/orders/{id}\"\n"
              Symbols = [ "App.Routes.orderUrl" ] }

        let viaConstant =
            { Path = "tests/IntTests/ViaConstantTests.fs"
              Text = "type ViaConstantTests() =\n    [<Fact>]\n    member _.Run() = ignore Routes.orderUrl\n"
              Symbols = [ "App.Tests.ViaConstantTests.Run" ] }

        analyse repo [ constants; viaConstant ]
        indexBuild repo [ ordersHandler; constants.Path; viaConstant.Path ]

        let expected =
            baseEdges
            |> Set.add ("App.Tests.ViaConstantTests.Run", "App.Handlers.Orders.getOrder")

        test <@ storedRouteEdges repo = expected @>)

/// Parsed URL constants are reused between builds only while the file's text is unchanged.
[<Fact>]
let ``a URL constant repointed between builds moves its edge`` () =
    let constantsFor (url: string) =
        { Path = "src/Routes.fs"
          Text = $"namespace App\nmodule Routes =\n    let pageUrl = \"%s{url}\"\n"
          Symbols = [ "App.Routes.pageUrl" ] }

    let viaConstant =
        { Path = "tests/IntTests/ViaConstantTests.fs"
          Text = "type ViaConstantTests() =\n    [<Fact>]\n    member _.Run() = ignore Routes.pageUrl\n"
          Symbols = [ "App.Tests.ViaConstantTests.Run" ] }

    withRepo (baseTree @ [ constantsFor "/api/orders/{id}"; viaConstant ]) [ usersRoute; ordersRoute ] (fun repo ->
        indexBuild repo []

        test
            <@
                storedRouteEdges repo = (baseEdges
                                         |> Set.add ("App.Tests.ViaConstantTests.Run", "App.Handlers.Orders.getOrder"))
            @>

        analyse repo [ constantsFor "/api/users/{id}" ]
        indexBuild repo [ "src/Routes.fs" ]

        test
            <@
                storedRouteEdges repo = (baseEdges
                                         |> Set.add ("App.Tests.ViaConstantTests.Run", "App.Handlers.Users.getUser"))
            @>)
