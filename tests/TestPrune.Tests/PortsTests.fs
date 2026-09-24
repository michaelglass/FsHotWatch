module TestPrune.Tests.PortsTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports
open TestPrune.Tests.TestHelpers

module ``SymbolStore from Database`` =

    [<Fact>]
    let ``store wraps database GetSymbolsInFile`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Lib.func"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "abc"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])
            let store = toSymbolStore db
            let symbols = store.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].FullName = "Lib.func" @>)

    [<Fact>]
    let ``store wraps database QueryAffectedTests`` () =
        withDb (fun db ->
            let store = toSymbolStore db
            let tests = store.QueryAffectedTests [ "nonexistent" ]
            test <@ tests |> List.isEmpty @>)

module ``SymbolSink from Database`` =

    [<Fact>]
    let ``sink wraps database RebuildProjects`` () =
        withDb (fun db ->
            let sink = toSymbolSink db
            let store = toSymbolStore db

            let graph =
                { Symbols =
                    [ { FullName = "Lib.func"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "abc"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            sink.RebuildProjects [ graph ] CacheKeys.Empty
            let symbols = store.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>)

/// `pluginStoreAt` is the DDL-free way to reach a plugin's own tables. A consumer that
/// only seeds or reads its plugin table (TestPrune.Falco's routes) has no stake in core's
/// schema, so it must not inherit core's version check: a store obtained this way opens a
/// database of ANY core schema version, runs no core DDL and moves no version marker.
module ``PluginStore without core schema`` =

    open System.IO
    open Microsoft.Data.Sqlite

    let private stampedDb (version: int) =
        let path = tempDbPath ()
        use conn = new SqliteConnection($"Data Source=%s{path}")
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            $"""
            CREATE TABLE sentinel (id INTEGER PRIMARY KEY, note TEXT);
            INSERT INTO sentinel(note) VALUES ('kept');
            PRAGMA user_version = %d{version};
            """

        cmd.ExecuteNonQuery() |> ignore
        conn.Close()
        SqliteConnection.ClearPool(conn)
        path

    let private scalar (conn: SqliteConnection) (sql: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteScalar()

    let private assertUntouchedAtVersion (path: string) (version: int) =
        use conn = new SqliteConnection($"Data Source=%s{path}")
        conn.Open()
        test <@ scalar conn "PRAGMA user_version;" :?> int64 = int64 version @>
        test <@ scalar conn "SELECT note FROM sentinel WHERE id = 1" :?> string = "kept" @>

        test
            <@ scalar conn "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'symbols'" :?> int64 = 0L @>

    let private roundTripPluginTable (store: PluginStore) =
        use conn = store.OpenConnection()

        use ddl = conn.CreateCommand()

        ddl.CommandText <-
            "CREATE TABLE IF NOT EXISTS plugin_facts (k TEXT PRIMARY KEY, v TEXT NOT NULL);
             INSERT OR REPLACE INTO plugin_facts VALUES ('route', '/health');"

        ddl.ExecuteNonQuery() |> ignore
        test <@ scalar conn "SELECT v FROM plugin_facts WHERE k = 'route'" :?> string = "/health" @>

    [<Theory>]
    [<InlineData(99)>]
    [<InlineData(1)>]
    let ``pluginStoreAt opens a database of any core schema version without core DDL`` (version: int) =
        let path = stampedDb version

        try
            roundTripPluginTable (pluginStoreAt path)
            assertUntouchedAtVersion path version
        finally
            cleanupDb path

    [<Fact>]
    let ``pluginStoreAt creates the file when none exists and leaves it unversioned`` () =
        // A plugin seeding before core has ever indexed writes into a file core will
        // later claim: `user_version = 0` plus user tables reads as a pre-versioning
        // database and core recreates it, which the plugin contract already survives.
        let path = tempDbPath ()

        try
            test <@ not (File.Exists path) @>
            roundTripPluginTable (pluginStoreAt path)
            test <@ File.Exists path @>

            use conn = new SqliteConnection($"Data Source=%s{path}")
            conn.Open()
            test <@ scalar conn "PRAGMA user_version;" :?> int64 = 0L @>
        finally
            cleanupDb path

    [<Fact>]
    let ``pluginStoreAt does not create a directory for the file`` () =
        // The store creates the FILE when it is absent, nothing more: a path under a
        // directory that does not exist is a wrong path, and it fails as one instead of
        // quietly materializing a cache somewhere nobody looks.
        let path = Path.Combine(tempDbPath () + ".dir", "test.db")
        test <@ not (Directory.Exists(Path.GetDirectoryName path)) @>

        Assert.Throws<SqliteException>(fun () -> (pluginStoreAt path).OpenConnection() |> ignore)
        |> ignore

        test <@ not (File.Exists path) @>

    [<Fact>]
    let ``core recreating an older database drops the plugin table with it`` () =
        // Pins the newer-on-older direction as it stands: core owns the file, and a
        // `SchemaVersion` bump deletes it wholesale. A plugin table written through
        // `pluginStoreAt` into an older-schema file does not survive core's next open.
        let path = stampedDb (SchemaVersion - 1)

        try
            roundTripPluginTable (pluginStoreAt path)
            let db = Database.create path
            test <@ db.WasRecreated @>

            use conn = (pluginStoreAt path).OpenConnection()

            test
                <@
                    scalar conn "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'plugin_facts'"
                    :?> int64 = 0L
                @>
        finally
            cleanupDb path
