module TestPrune.Tests.ExtensionEdgeRefreshTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports
open TestPrune.Extensions

let private symbol (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 2
      ContentHash = "h"
      IsExtern = false }

let private edge (source: string) (fromSymbol: string) (toSymbol: string) : Dependency =
    { FromSymbol = fromSymbol
      ToSymbol = toSymbol
      Kind = SharedState
      Source = source }

/// An extension whose answer the test sets before each refresh.
type private ScriptedExtension(name: string) =
    member val Answer: unit -> Dependency list = (fun () -> []) with get, set

    interface ITestPruneExtension with
        member _.Name = name
        member this.AnalyzeEdges _ _ = this.Answer()

let private withDb (body: Database -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"extension-refresh-{Guid.NewGuid():N}")

    Directory.CreateDirectory dir |> ignore

    try
        let db = Database.create (Path.Combine(dir, "index.db"))

        db.RebuildProjects(
            [ AnalysisResult.Create(
                  [ symbol "App.Handler.run" "src/Handler.fs"
                    symbol "App.Handler.helper" "src/Handler.fs" ],
                  [ edge "core" "App.Handler.run" "App.Handler.helper" ],
                  []
              )
              AnalysisResult.Create([ symbol "App.Tests.A.test" "tests/A.fs" ], [], [])
              AnalysisResult.Create([ symbol "App.Tests.B.test" "tests/B.fs" ], [], []) ]
        )

        body db
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

let private storedEdges (db: Database) : Set<string * string * string> =
    use conn = db.OpenConnection()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        """
        SELECT d.source, f.full_name, t.full_name
        FROM dependencies d
        JOIN symbols f ON f.id = d.from_symbol_id
        JOIN symbols t ON t.id = d.to_symbol_id
        """

    use reader = cmd.ExecuteReader()

    [ while reader.Read() do
          yield reader.GetString 0, reader.GetString 1, reader.GetString 2 ]
    |> Set.ofList

let private astEdge = "core", "App.Handler.run", "App.Handler.helper"

[<Fact>]
let ``a refresh replaces the extension's previous edges rather than adding to them`` () =
    withDb (fun db ->
        let ext = ScriptedExtension "routes"
        ext.Answer <- fun () -> [ edge "routes" "App.Tests.A.test" "App.Handler.run" ]
        refreshExtensionEdges db "" [ ext ] |> ignore

        ext.Answer <- fun () -> [ edge "routes" "App.Tests.B.test" "App.Handler.run" ]
        let outcome = refreshExtensionEdges db "" [ ext ]

        test <@ outcome = [ Refreshed("routes", 1) ] @>
        test <@ storedEdges db = set [ astEdge; "routes", "App.Tests.B.test", "App.Handler.run" ] @>)

[<Fact>]
let ``an empty answer removes every edge the extension held`` () =
    withDb (fun db ->
        let ext = ScriptedExtension "routes"
        ext.Answer <- fun () -> [ edge "routes" "App.Tests.A.test" "App.Handler.run" ]
        refreshExtensionEdges db "" [ ext ] |> ignore

        ext.Answer <- fun () -> []
        refreshExtensionEdges db "" [ ext ] |> ignore

        test <@ storedEdges db = set [ astEdge ] @>)

[<Fact>]
let ``one extension's refresh leaves another extension's edges alone`` () =
    withDb (fun db ->
        let routes = ScriptedExtension "routes"
        let sql = ScriptedExtension "sql"
        routes.Answer <- fun () -> [ edge "routes" "App.Tests.A.test" "App.Handler.run" ]
        sql.Answer <- fun () -> [ edge "sql" "App.Tests.B.test" "App.Handler.helper" ]
        refreshExtensionEdges db "" [ routes; sql ] |> ignore

        routes.Answer <- fun () -> []
        refreshExtensionEdges db "" [ routes ] |> ignore

        test <@ storedEdges db = set [ astEdge; "sql", "App.Tests.B.test", "App.Handler.helper" ] @>)

/// Extension edges belong to the extension, not to a source file, so re-indexing the file at
/// either end of one does not delete it.
[<Fact>]
let ``re-indexing a file keeps the extension edges that touch it`` () =
    withDb (fun db ->
        let ext = ScriptedExtension "routes"
        ext.Answer <- fun () -> [ edge "routes" "App.Tests.A.test" "App.Handler.run" ]
        refreshExtensionEdges db "" [ ext ] |> ignore

        db.RebuildProjects([ AnalysisResult.Create([ symbol "App.Tests.A.test" "tests/A.fs" ], [], []) ])

        db.RebuildProjects(
            [ AnalysisResult.Create(
                  [ symbol "App.Handler.run" "src/Handler.fs"
                    symbol "App.Handler.helper" "src/Handler.fs" ],
                  [ edge "core" "App.Handler.run" "App.Handler.helper" ],
                  []
              ) ]
        )

        test <@ storedEdges db = set [ astEdge; "routes", "App.Tests.A.test", "App.Handler.run" ] @>)

[<Fact>]
let ``an extension that throws keeps its previous edges and reports the failure`` () =
    withDb (fun db ->
        let ext = ScriptedExtension "routes"
        ext.Answer <- fun () -> [ edge "routes" "App.Tests.A.test" "App.Handler.run" ]
        refreshExtensionEdges db "" [ ext ] |> ignore

        ext.Answer <- fun () -> failwith "route table unreadable"
        let outcome = refreshExtensionEdges db "" [ ext ]

        test
            <@
                match outcome with
                | [ Failed("routes", ex) ] -> ex.Message = "route table unreadable"
                | _ -> false
            @>

        test <@ storedEdges db = set [ astEdge; "routes", "App.Tests.A.test", "App.Handler.run" ] @>)

[<Fact>]
let ``an edge naming a symbol the index does not hold is skipped`` () =
    withDb (fun db ->
        let ext = ScriptedExtension "routes"

        ext.Answer <-
            fun () ->
                [ edge "routes" "App.Tests.A.test" "App.Handler.run"
                  edge "routes" "App.Tests.Missing.test" "App.Handler.run" ]

        refreshExtensionEdges db "" [ ext ] |> ignore

        test <@ storedEdges db = set [ astEdge; "routes", "App.Tests.A.test", "App.Handler.run" ] @>)

/// A v14 index holds extension edges written as never-replaced `_extern` rows, in whatever
/// mix its build history produced. Opening it recreates the store, so none of them survive
/// to stand beside the next refresh's answer.
[<Fact>]
let ``a v14 index's extension edges do not survive the upgrade`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), $"extension-refresh-v14-{Guid.NewGuid():N}")

    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "index.db")

    try
        do
            let db = Database.create path

            db.RebuildProjects(
                [ AnalysisResult.Create([ symbol "App.Handler.run" "src/Handler.fs" ], [], [])
                  AnalysisResult.Create([ symbol "App.Tests.A.test" "tests/A.fs" ], [], []) ]
            )

            db.RebuildProjects([ AnalysisResult.Create([], [ edge "falco" "App.Tests.A.test" "App.Handler.run" ], []) ])
            test <@ storedEdges db = set [ "falco", "App.Tests.A.test", "App.Handler.run" ] @>

            use conn = db.OpenConnection()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "PRAGMA user_version = 14;"
            cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

        let reopened = Database.create path

        test <@ reopened.WasRecreated @>
        test <@ storedEdges reopened |> Set.isEmpty @>
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)
