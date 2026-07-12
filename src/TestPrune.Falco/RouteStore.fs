namespace TestPrune.Falco

open System
open Microsoft.Data.Sqlite
open TestPrune.Ports

/// Maps an HTTP route (method + URL pattern) to its handler's source file and, when known,
/// the specific handler function serving it. `HandlerFunction` is the short
/// `Module.function` name (e.g. `Landing.index`); `None` means the function is
/// unresolved/legacy, in which case route edges fall back to a file-level match.
///
/// Routes are seeded from OUTSIDE the AST — they live in a route DU plus runtime wiring
/// that no F# symbol reveals — which is why TestPrune.Falco persists them itself rather
/// than deriving them at analysis time like the SQL extensions do.
type RouteHandlerEntry =
    { UrlPattern: string
      HttpMethod: string
      HandlerSourceFile: string
      HandlerFunction: string option }

module private RouteTable =

    /// DDL for the route table TestPrune.Falco owns inside TestPrune's cache database.
    /// Issued before every read and write, never once at startup: core deletes and
    /// recreates the database file whenever its own `SchemaVersion` moves, taking plugin
    /// tables with it (core cannot migrate a table it knows nothing about). `IF NOT EXISTS`
    /// makes that drop a non-event — the next call recreates the table, and the routes
    /// themselves are re-seeded every run, so nothing here is the sole copy of anything.
    [<Literal>]
    let ddl =
        """
        CREATE TABLE IF NOT EXISTS route_handlers (
            url_pattern TEXT NOT NULL,
            http_method TEXT NOT NULL,
            handler_source_file TEXT NOT NULL,
            handler_function TEXT,
            PRIMARY KEY (url_pattern, http_method)
        );

        CREATE INDEX IF NOT EXISTS idx_route_handlers_source ON route_handlers (handler_source_file);
        """

    let readAll (reader: SqliteDataReader) (f: SqliteDataReader -> 'T) : 'T list =
        let mutable results = []

        while reader.Read() do
            results <- f reader :: results

        results |> List.rev

    /// Read a row selected as (url_pattern, http_method, handler_source_file,
    /// handler_function). The trailing column is nullable — a NULL maps to `None`.
    let readEntry (r: SqliteDataReader) : RouteHandlerEntry =
        { UrlPattern = r.GetString(0)
          HttpMethod = r.GetString(1)
          HandlerSourceFile = r.GetString(2)
          HandlerFunction = if r.IsDBNull(3) then None else Some(r.GetString(3)) }

    /// Open a connection through the core seam and guarantee the route table exists on it
    /// before the body runs. Every public operation goes through here, so "the table might
    /// have been dropped by a core schema bump" is not a state any of them can observe.
    let withConnection (store: PluginStore) (body: SqliteConnection -> 'T) : 'T =
        use conn = store.OpenConnection()

        use ddlCmd = conn.CreateCommand()
        ddlCmd.CommandText <- ddl
        ddlCmd.ExecuteNonQuery() |> ignore

        body conn

/// The route → handler table, owned by TestPrune.Falco and stored in TestPrune's cache
/// database via the core `PluginStore` seam (`TestPrune.Ports.toPluginStore db`).
///
/// TestPrune.Core knows nothing about routes: it hands out a connection to its cache DB
/// and this type owns the table's schema, reads and writes end to end.
type RouteStore(store: PluginStore) =

    /// Store route → handler mappings. Clears and rebuilds all entries: routes are
    /// re-seeded in full on every indexing run, so a stale row is never carried forward.
    member _.Rebuild(entries: RouteHandlerEntry list) =
        RouteTable.withConnection store (fun conn ->
            use txn = conn.BeginTransaction()

            try
                use delCmd = conn.CreateCommand()
                delCmd.Transaction <- txn
                delCmd.CommandText <- "DELETE FROM route_handlers"
                delCmd.ExecuteNonQuery() |> ignore

                use insCmd = conn.CreateCommand()
                insCmd.Transaction <- txn

                insCmd.CommandText <-
                    """
                    INSERT OR REPLACE INTO route_handlers (url_pattern, http_method, handler_source_file, handler_function)
                    VALUES (@urlPattern, @httpMethod, @handlerSourceFile, @handlerFunction)
                    """

                let pUrlPattern = insCmd.Parameters.Add("@urlPattern", SqliteType.Text)
                let pHttpMethod = insCmd.Parameters.Add("@httpMethod", SqliteType.Text)

                let pHandlerSourceFile =
                    insCmd.Parameters.Add("@handlerSourceFile", SqliteType.Text)

                let pHandlerFunction = insCmd.Parameters.Add("@handlerFunction", SqliteType.Text)

                for entry in entries do
                    pUrlPattern.Value <- entry.UrlPattern
                    pHttpMethod.Value <- entry.HttpMethod
                    pHandlerSourceFile.Value <- entry.HandlerSourceFile

                    pHandlerFunction.Value <-
                        match entry.HandlerFunction with
                        | Some fn -> box fn
                        | None -> box DBNull.Value

                    insCmd.ExecuteNonQuery() |> ignore

                txn.Commit()
            with ex ->
                txn.Rollback()
                raise ex)

    /// Every route entry currently stored.
    member _.GetAll() : RouteHandlerEntry list =
        RouteTable.withConnection store (fun conn ->
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "SELECT url_pattern, http_method, handler_source_file, handler_function FROM route_handlers"

            use reader = cmd.ExecuteReader()
            RouteTable.readAll reader RouteTable.readEntry)

    /// Every source file that serves at least one route.
    member _.GetAllHandlerSourceFiles() : Set<string> =
        RouteTable.withConnection store (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT DISTINCT handler_source_file FROM route_handlers"

            use reader = cmd.ExecuteReader()
            RouteTable.readAll reader (fun r -> r.GetString(0)) |> Set.ofList)

    /// The URL patterns served by a given handler source file.
    member _.GetUrlPatternsForSourceFile(sourceFile: string) : string list =
        RouteTable.withConnection store (fun conn ->
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                SELECT url_pattern FROM route_handlers
                WHERE handler_source_file = @sourceFile
                """

            cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

            use reader = cmd.ExecuteReader()
            RouteTable.readAll reader (fun r -> r.GetString(0)))

    /// The full route entries served by a given handler source file. Carries
    /// `HandlerFunction` so edges can be scoped to the specific function serving each
    /// route rather than the whole file.
    member _.GetRouteHandlersForSourceFile(sourceFile: string) : RouteHandlerEntry list =
        RouteTable.withConnection store (fun conn ->
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                SELECT url_pattern, http_method, handler_source_file, handler_function FROM route_handlers
                WHERE handler_source_file = @sourceFile
                """

            cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

            use reader = cmd.ExecuteReader()
            RouteTable.readAll reader RouteTable.readEntry)
