module TestPrune.Database

open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer

let private schema =
    """
    CREATE TABLE IF NOT EXISTS symbols (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        full_name TEXT NOT NULL UNIQUE,
        kind TEXT NOT NULL,
        source_file TEXT NOT NULL,
        line_start INTEGER NOT NULL,
        line_end INTEGER NOT NULL,
        content_hash TEXT NOT NULL DEFAULT '',
        is_extern INTEGER NOT NULL DEFAULT 0,
        indexed_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS dependencies (
        from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        dep_kind TEXT NOT NULL,
        source TEXT NOT NULL DEFAULT 'core',
        PRIMARY KEY (from_symbol_id, to_symbol_id, dep_kind)
    );

    CREATE TABLE IF NOT EXISTS test_methods (
        symbol_id INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
        test_project TEXT NOT NULL,
        test_class TEXT NOT NULL,
        test_method TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS route_handlers (
        url_pattern TEXT NOT NULL,
        http_method TEXT NOT NULL,
        handler_source_file TEXT NOT NULL,
        PRIMARY KEY (url_pattern, http_method)
    );

    CREATE TABLE IF NOT EXISTS project_keys (
        project_name TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS file_keys (
        source_file TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS analysis_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        run_id TEXT NOT NULL,
        timestamp TEXT NOT NULL,
        event_type TEXT NOT NULL,
        event_data TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS symbol_attributes (
        symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        attribute_name TEXT NOT NULL,
        args_json TEXT NOT NULL DEFAULT '[]',
        PRIMARY KEY (symbol_id, attribute_name, args_json)
    );

    CREATE INDEX IF NOT EXISTS idx_symbols_by_file ON symbols (source_file);
    CREATE INDEX IF NOT EXISTS idx_deps_to ON dependencies (to_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_from ON dependencies (from_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_route_handlers_source ON route_handlers (handler_source_file);
    CREATE INDEX IF NOT EXISTS idx_events_run_id ON analysis_events(run_id);
    CREATE INDEX IF NOT EXISTS idx_events_type ON analysis_events(event_type);
    CREATE INDEX IF NOT EXISTS idx_symbol_attrs_by_symbol ON symbol_attributes (symbol_id);
    """

let private symbolKindToString (kind: SymbolKind) =
    match kind with
    | Function -> "Function"
    | Type -> "Type"
    | DuCase -> "DuCase"
    | Module -> "Module"
    | Value -> "Value"
    | Property -> "Property"
    | ExternRef -> "ExternRef"

let private stringToSymbolKind (warned: HashSet<string>) (s: string) =
    match s with
    | "Function" -> Function
    | "Type" -> Type
    | "DuCase" -> DuCase
    | "Module" -> Module
    | "Value" -> Value
    | "Property" -> Property
    | "ExternRef" -> ExternRef
    | unknown ->
        if warned.Add($"SymbolKind:%s{unknown}") then
            eprintfn $"Warning: unknown SymbolKind '%s{unknown}' in database, defaulting to Value"

        Value

let private depKindToString =
    function
    | Calls -> "calls"
    | UsesType -> "uses_type"
    | PatternMatches -> "pattern_matches"
    | References -> "references"
    | SharedState -> "shared_state"

let private stringToDepKind (warned: HashSet<string>) =
    function
    | "calls" -> Calls
    | "uses_type" -> UsesType
    | "pattern_matches" -> PatternMatches
    | "references" -> References
    | "shared_state" -> SharedState
    | unknown ->
        if warned.Add($"DependencyKind:%s{unknown}") then
            eprintfn $"Warning: unknown DependencyKind '%s{unknown}' in database, defaulting to References"

        References

let private readAll (reader: SqliteDataReader) (f: SqliteDataReader -> 'T) : 'T list =
    let mutable results = []

    while reader.Read() do
        results <- f reader :: results

    results |> List.rev

let private buildPlaceholders (names: string list) =
    let paramNames = names |> List.mapi (fun i _ -> $"@p%d{i}")
    String.Join(", ", paramNames)

let private bindPlaceholders (cmd: SqliteCommand) (names: string list) =
    names
    |> List.iteri (fun i name -> cmd.Parameters.AddWithValue($"@p%d{i}", name) |> ignore)

let private openConnection (dbPath: string) =
    let connStr = $"Data Source=%s{dbPath}"
    let conn = new SqliteConnection(connStr)

    try
        conn.Open()

        use pragmaCmd = conn.CreateCommand()
        pragmaCmd.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"
        pragmaCmd.ExecuteNonQuery() |> ignore

        conn
    with ex ->
        conn.Dispose()
        raise ex

/// Increment this whenever the schema changes in a backwards-incompatible way.
/// A mismatch causes the database file to be deleted and recreated.
[<Literal>]
let private SchemaVersion = 3

let private deleteDbFiles (dbPath: string) =
    File.Delete(dbPath)

    for ext in [ "-wal"; "-shm" ] do
        let p = dbPath + ext

        if File.Exists(p) then
            File.Delete(p)

/// Open a connection, deleting and recreating the database if the schema version is incompatible.
let private openCheckedConnection (dbPath: string) =
    if File.Exists(dbPath) then
        let conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version;"
        let version = cmd.ExecuteScalar() :?> int64 |> int

        if version <> 0 && version <> SchemaVersion then
            eprintfn $"Schema version mismatch (found v%d{version}, expected v%d{SchemaVersion}). Recreating database."

            SqliteConnection.ClearPool(conn)
            conn.Dispose()
            deleteDbFiles dbPath
            openConnection dbPath
        else
            conn
    else
        openConnection dbPath

/// SQLite-backed dependency graph storage.
type Database(dbPath: string) =
    let warnedUnknownKinds = HashSet<string>()

    do
        use conn = openCheckedConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

        use versionCmd = conn.CreateCommand()
        versionCmd.CommandText <- "PRAGMA user_version;"
        let currentVersion = versionCmd.ExecuteScalar() :?> int64 |> int

        if currentVersion <> SchemaVersion then
            use setCmd = conn.CreateCommand()
            setCmd.CommandText <- $"PRAGMA user_version = %d{SchemaVersion};"
            setCmd.ExecuteNonQuery() |> ignore

    /// Create a Database instance, initializing the schema if needed.
    static member create(dbPath: string) = Database(dbPath)

    /// Clear and re-insert symbols, dependencies, and test methods.
    /// All symbols are inserted before any dependencies, so cross-project edges resolve correctly.
    /// When called with a subset of projects, dependency edges to symbols in other projects will
    /// only resolve if those symbols already exist in the database from a prior call.
    /// Optional fileKeys/projectKeys are written in the same transaction for atomicity.
    member _.RebuildProjects
        (results: AnalysisResult list, ?fileKeys: (string * string) list, ?projectKeys: (string * string) list)
        =
        let sourceFiles =
            results
            |> List.collect (fun r -> r.Symbols |> List.map (fun s -> s.SourceFile))
            |> List.distinct
            |> List.filter (fun f -> f <> ExternSourceFile)

        use conn = openConnection dbPath

        use txn = conn.BeginTransaction()

        try
            // Batch delete using parameterized IN-clause
            if not sourceFiles.IsEmpty then
                let paramNames = sourceFiles |> List.mapi (fun i _ -> $"@f%d{i}")
                let placeholders = String.Join(", ", paramNames)

                use delCmd = conn.CreateCommand()
                delCmd.Transaction <- txn

                // ON DELETE CASCADE on dependencies and test_methods handles child rows
                delCmd.CommandText <- $"DELETE FROM symbols WHERE source_file IN (%s{placeholders})"

                sourceFiles
                |> List.iteri (fun i file -> delCmd.Parameters.AddWithValue($"@f%d{i}", file) |> ignore)

                delCmd.ExecuteNonQuery() |> ignore

            let now = DateTime.UtcNow.ToString("o")

            let makeSymbolCmd conflictClause =
                let cmd = conn.CreateCommand()
                cmd.Transaction <- txn

                cmd.CommandText <-
                    $"""
                    INSERT OR %s{conflictClause} INTO symbols (full_name, kind, source_file, line_start, line_end, content_hash, is_extern, indexed_at)
                    VALUES (@fullName, @kind, @sourceFile, @lineStart, @lineEnd, @contentHash, @isExtern, @indexedAt)
                    """

                cmd.Parameters.Add("@fullName", SqliteType.Text) |> ignore
                cmd.Parameters.Add("@kind", SqliteType.Text) |> ignore
                cmd.Parameters.Add("@sourceFile", SqliteType.Text) |> ignore
                cmd.Parameters.Add("@lineStart", SqliteType.Integer) |> ignore
                cmd.Parameters.Add("@lineEnd", SqliteType.Integer) |> ignore
                cmd.Parameters.Add("@contentHash", SqliteType.Text) |> ignore
                cmd.Parameters.Add("@isExtern", SqliteType.Integer) |> ignore
                cmd.Parameters.Add("@indexedAt", SqliteType.Text) |> ignore
                cmd

            // Real symbols use REPLACE to update on re-index; extern symbols use
            // IGNORE so they don't overwrite real symbols already in the DB.
            use insCmd = makeSymbolCmd "REPLACE"
            use externCmd = makeSymbolCmd "IGNORE"

            let setSymbolParams (cmd: SqliteCommand) (sym: SymbolInfo) =
                cmd.Parameters["@fullName"].Value <- sym.FullName
                cmd.Parameters["@kind"].Value <- symbolKindToString sym.Kind
                cmd.Parameters["@sourceFile"].Value <- sym.SourceFile
                cmd.Parameters["@lineStart"].Value <- sym.LineStart
                cmd.Parameters["@lineEnd"].Value <- sym.LineEnd
                cmd.Parameters["@contentHash"].Value <- sym.ContentHash
                cmd.Parameters["@isExtern"].Value <- if sym.IsExtern then 1 else 0
                cmd.Parameters["@indexedAt"].Value <- now

            for result in results do
                for sym in result.Symbols do
                    let cmd = if sym.IsExtern then externCmd else insCmd
                    setSymbolParams cmd sym
                    cmd.ExecuteNonQuery() |> ignore

            // Dependencies are inserted after all symbols so cross-project edges resolve
            use depCmd = conn.CreateCommand()
            depCmd.Transaction <- txn

            depCmd.CommandText <-
                """
                INSERT OR IGNORE INTO dependencies (from_symbol_id, to_symbol_id, dep_kind, source)
                SELECT f.id, t.id, @depKind, @source
                FROM symbols f, symbols t
                WHERE f.full_name = @fromSymbol AND t.full_name = @toSymbol
                """

            let pFromSymbol = depCmd.Parameters.Add("@fromSymbol", SqliteType.Text)
            let pToSymbol = depCmd.Parameters.Add("@toSymbol", SqliteType.Text)
            let pDepKind = depCmd.Parameters.Add("@depKind", SqliteType.Text)
            let pSource = depCmd.Parameters.Add("@source", SqliteType.Text)

            for result in results do
                for dep in result.Dependencies do
                    pFromSymbol.Value <- dep.FromSymbol
                    pToSymbol.Value <- dep.ToSymbol
                    pDepKind.Value <- depKindToString dep.Kind
                    pSource.Value <- dep.Source
                    depCmd.ExecuteNonQuery() |> ignore

            use tmCmd = conn.CreateCommand()
            tmCmd.Transaction <- txn

            tmCmd.CommandText <-
                """
                INSERT OR IGNORE INTO test_methods (symbol_id, test_project, test_class, test_method)
                SELECT id, @testProject, @testClass, @testMethod
                FROM symbols WHERE full_name = @symbolFullName
                """

            let pSymbolFullName = tmCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
            let pTestProject = tmCmd.Parameters.Add("@testProject", SqliteType.Text)
            let pTestClass = tmCmd.Parameters.Add("@testClass", SqliteType.Text)
            let pTestMethod = tmCmd.Parameters.Add("@testMethod", SqliteType.Text)

            for result in results do
                for tm in result.TestMethods do
                    pSymbolFullName.Value <- tm.SymbolFullName
                    pTestProject.Value <- tm.TestProject
                    pTestClass.Value <- tm.TestClass
                    pTestMethod.Value <- tm.TestMethod
                    tmCmd.ExecuteNonQuery() |> ignore

            use attrCmd = conn.CreateCommand()
            attrCmd.Transaction <- txn

            attrCmd.CommandText <-
                """
                INSERT OR IGNORE INTO symbol_attributes (symbol_id, attribute_name, args_json)
                SELECT id, @attrName, @argsJson
                FROM symbols WHERE full_name = @symbolFullName
                """

            let pAttrSymbol = attrCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
            let pAttrName = attrCmd.Parameters.Add("@attrName", SqliteType.Text)
            let pArgsJson = attrCmd.Parameters.Add("@argsJson", SqliteType.Text)

            for result in results do
                for attr in result.Attributes do
                    pAttrSymbol.Value <- attr.SymbolFullName
                    pAttrName.Value <- attr.AttributeName
                    pArgsJson.Value <- attr.ArgsJson
                    attrCmd.ExecuteNonQuery() |> ignore

            // Write cache keys in the same transaction
            match fileKeys with
            | Some keys when not keys.IsEmpty ->
                use fkCmd = conn.CreateCommand()
                fkCmd.Transaction <- txn

                fkCmd.CommandText <- "INSERT OR REPLACE INTO file_keys (source_file, key) VALUES (@sourceFile, @key)"

                let pFkFile = fkCmd.Parameters.Add("@sourceFile", SqliteType.Text)
                let pFkKey = fkCmd.Parameters.Add("@key", SqliteType.Text)

                for (file, key) in keys do
                    pFkFile.Value <- file
                    pFkKey.Value <- key
                    fkCmd.ExecuteNonQuery() |> ignore
            | _ -> ()

            match projectKeys with
            | Some keys when not keys.IsEmpty ->
                use pkCmd = conn.CreateCommand()
                pkCmd.Transaction <- txn

                pkCmd.CommandText <-
                    "INSERT OR REPLACE INTO project_keys (project_name, key) VALUES (@projectName, @key)"

                let pPkName = pkCmd.Parameters.Add("@projectName", SqliteType.Text)
                let pPkKey = pkCmd.Parameters.Add("@key", SqliteType.Text)

                for (name, key) in keys do
                    pPkName.Value <- name
                    pPkKey.Value <- key
                    pkCmd.ExecuteNonQuery() |> ignore
            | _ -> ()

            txn.Commit()
        with ex ->
            txn.Rollback()
            raise ex

    /// Find test methods transitively depending on the given changed symbol names.
    member _.QueryAffectedTests(changedSymbolNames: string list) : TestMethodInfo list =
        if changedSymbolNames.IsEmpty then
            []
        else
            use conn = openConnection dbPath

            // Build parameter placeholders
            let placeholders = buildPlaceholders changedSymbolNames

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                WITH RECURSIVE transitive_deps AS (
                    SELECT from_symbol_id FROM dependencies WHERE to_symbol_id IN (
                        SELECT id FROM symbols WHERE full_name IN (%s{placeholders})
                    )
                    UNION
                    SELECT d.from_symbol_id FROM dependencies d
                    JOIN transitive_deps td ON d.to_symbol_id = td.from_symbol_id
                )
                SELECT DISTINCT s.full_name, tm.test_project, tm.test_class, tm.test_method
                FROM transitive_deps td
                JOIN test_methods tm ON tm.symbol_id = td.from_symbol_id
                JOIN symbols s ON s.id = tm.symbol_id
                """

            bindPlaceholders cmd changedSymbolNames

            use reader = cmd.ExecuteReader()

            readAll reader (fun r ->
                { SymbolFullName = r.GetString(0)
                  TestProject = r.GetString(1)
                  TestClass = r.GetString(2)
                  TestMethod = r.GetString(3) })

    /// Return all symbols stored for a given source file path.
    member _.GetSymbolsInFile(sourceFile: string) : SymbolInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT full_name, kind, source_file, line_start, line_end, content_hash, is_extern
            FROM symbols WHERE source_file = @sourceFile
            ORDER BY line_start
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FullName = r.GetString(0)
              Kind = stringToSymbolKind warnedUnknownKinds (r.GetString(1))
              SourceFile = r.GetString(2)
              LineStart = r.GetInt32(3)
              LineEnd = r.GetInt32(4)
              ContentHash = r.GetString(5)
              IsExtern = r.GetInt32(6) = 1 })

    /// Return the set of all symbol full names.
    member _.GetAllSymbolNames() : Set<string> =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT full_name FROM symbols"

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0)) |> Set.ofList

    /// Return all symbols ordered by file and line.
    member _.GetAllSymbols() : SymbolInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT full_name, kind, source_file, line_start, line_end, content_hash, is_extern
            FROM symbols ORDER BY source_file, line_start
            """

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FullName = r.GetString(0)
              Kind = stringToSymbolKind warnedUnknownKinds (r.GetString(1))
              SourceFile = r.GetString(2)
              LineStart = r.GetInt32(3)
              LineEnd = r.GetInt32(4)
              ContentHash = r.GetString(5)
              IsExtern = r.GetInt32(6) = 1 })

    /// Return the set of symbol names that are test methods.
    member _.GetTestMethodSymbolNames() : Set<string> =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name FROM test_methods tm
            JOIN symbols s ON s.id = tm.symbol_id
            """

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0)) |> Set.ofList

    /// Get all symbols reachable from the given root symbol names (transitively).
    member _.GetReachableSymbols(rootSymbolNames: string list) : Set<string> =
        if rootSymbolNames.IsEmpty then
            Set.empty
        else
            use conn = openConnection dbPath

            let placeholders = buildPlaceholders rootSymbolNames

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                WITH RECURSIVE reachable AS (
                    SELECT id, full_name FROM symbols WHERE full_name IN (%s{placeholders})
                    UNION
                    SELECT s.id, s.full_name FROM symbols s
                    JOIN dependencies d ON d.to_symbol_id = s.id
                    JOIN reachable r ON r.id = d.from_symbol_id
                )
                SELECT DISTINCT full_name FROM reachable
                """

            bindPlaceholders cmd rootSymbolNames

            use reader = cmd.ExecuteReader()
            readAll reader (fun r -> r.GetString(0)) |> Set.ofList

    /// Store route -> handler source file mappings. Clears and rebuilds all entries.
    member _.RebuildRouteHandlers(entries: RouteHandlerEntry list) =
        use conn = openConnection dbPath
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
                INSERT OR REPLACE INTO route_handlers (url_pattern, http_method, handler_source_file)
                VALUES (@urlPattern, @httpMethod, @handlerSourceFile)
                """

            let pUrlPattern = insCmd.Parameters.Add("@urlPattern", SqliteType.Text)
            let pHttpMethod = insCmd.Parameters.Add("@httpMethod", SqliteType.Text)

            let pHandlerSourceFile =
                insCmd.Parameters.Add("@handlerSourceFile", SqliteType.Text)

            for entry in entries do
                pUrlPattern.Value <- entry.UrlPattern
                pHttpMethod.Value <- entry.HttpMethod
                pHandlerSourceFile.Value <- entry.HandlerSourceFile
                insCmd.ExecuteNonQuery() |> ignore

            txn.Commit()
        with ex ->
            txn.Rollback()
            raise ex

    /// Get all URL patterns served by a given handler source file.
    member _.GetUrlPatternsForSourceFile(sourceFile: string) : string list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT url_pattern FROM route_handlers
            WHERE handler_source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0))

    /// Get all handler source files in the route_handlers table.
    member _.GetAllHandlerSourceFiles() : Set<string> =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT DISTINCT handler_source_file FROM route_handlers"

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0)) |> Set.ofList

    /// Get all route handler entries.
    member _.GetAllRouteHandlers() : RouteHandlerEntry list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT url_pattern, http_method, handler_source_file FROM route_handlers"

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { UrlPattern = r.GetString(0)
              HttpMethod = r.GetString(1)
              HandlerSourceFile = r.GetString(2) })

    /// Get the stored cache key for a project, or None if not yet indexed.
    member _.GetProjectKey(projectName: string) : string option =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT key FROM project_keys WHERE project_name = @projectName"
        cmd.Parameters.AddWithValue("@projectName", projectName) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then Some(reader.GetString(0)) else None

    /// Get the stored cache key for a source file, or None if not yet indexed.
    member _.GetFileKey(sourceFile: string) : string option =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT key FROM file_keys WHERE source_file = @sourceFile"
        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then Some(reader.GetString(0)) else None

    /// Get incoming edges for a batch of symbol names (who depends on each).
    member _.GetIncomingEdgesBatch(symbolNames: string list) : Map<string, string list> =
        if symbolNames.IsEmpty then
            Map.empty
        else
            use conn = openConnection dbPath

            let placeholders = buildPlaceholders symbolNames

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                SELECT s_to.full_name, s_from.full_name
                FROM dependencies d
                JOIN symbols s_to ON s_to.id = d.to_symbol_id
                JOIN symbols s_from ON s_from.id = d.from_symbol_id
                WHERE s_to.full_name IN (%s{placeholders})
                """

            bindPlaceholders cmd symbolNames

            use reader = cmd.ExecuteReader()

            readAll reader (fun r -> r.GetString(0), r.GetString(1))
            |> List.groupBy fst
            |> List.map (fun (k, vs) -> k, vs |> List.map snd)
            |> Map.ofList

    /// Get dependencies originating from symbols defined in the given source file.
    member _.GetDependenciesFromFile(sourceFile: string) : Dependency list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT f.full_name, t.full_name, d.dep_kind, d.source
            FROM dependencies d
            JOIN symbols f ON f.id = d.from_symbol_id
            JOIN symbols t ON t.id = d.to_symbol_id
            WHERE f.source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FromSymbol = r.GetString(0)
              ToSymbol = r.GetString(1)
              Kind = stringToDepKind warnedUnknownKinds (r.GetString(2))
              Source = r.GetString(3) })

    /// Insert an audit event into the analysis_events table.
    member _.InsertEvent(runId: string, timestamp: string, eventType: string, eventData: string) =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "INSERT INTO analysis_events (run_id, timestamp, event_type, event_data) VALUES (@runId, @ts, @type, @data)"

        cmd.Parameters.AddWithValue("@runId", runId) |> ignore
        cmd.Parameters.AddWithValue("@ts", timestamp) |> ignore
        cmd.Parameters.AddWithValue("@type", eventType) |> ignore
        cmd.Parameters.AddWithValue("@data", eventData) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Get all events for a given run ID, ordered by insertion order.
    member _.GetEvents(runId: string) : (string * string * string) list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "SELECT timestamp, event_type, event_data FROM analysis_events WHERE run_id = @runId ORDER BY id"

        cmd.Parameters.AddWithValue("@runId", runId) |> ignore
        use reader = cmd.ExecuteReader()

        readAll reader (fun r -> r.GetString(0), r.GetString(1), r.GetString(2))

    /// Delete all events for a given run ID.
    member _.ClearEvents(runId: string) =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM analysis_events WHERE run_id = @runId"
        cmd.Parameters.AddWithValue("@runId", runId) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Get distinct edge sources in the transitive closure reachable from changed symbols.
    member _.QueryEdgeSourcesForTest(changedSymbolNames: string list) : string list =
        if changedSymbolNames.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            let paramNames = changedSymbolNames |> List.mapi (fun i _ -> $"@p%d{i}")
            let placeholders = String.Join(", ", paramNames)
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                WITH RECURSIVE transitive_path AS (
                    SELECT from_symbol_id, source
                    FROM dependencies
                    WHERE to_symbol_id IN (
                        SELECT id FROM symbols WHERE full_name IN (%s{placeholders})
                    )
                    UNION
                    SELECT d.from_symbol_id, d.source
                    FROM dependencies d
                    JOIN transitive_path tp ON d.to_symbol_id = tp.from_symbol_id
                )
                SELECT DISTINCT source FROM transitive_path
                """

            bindPlaceholders cmd changedSymbolNames

            use reader = cmd.ExecuteReader()
            readAll reader (fun r -> r.GetString(0))

    /// Get all symbol attributes, grouped by symbol full name.
    member _.GetAllAttributes() : Map<string, (string * string) list> =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name, sa.attribute_name, sa.args_json
            FROM symbol_attributes sa
            JOIN symbols s ON s.id = sa.symbol_id
            """

        use reader = cmd.ExecuteReader()

        readAll reader (fun r -> r.GetString(0), (r.GetString(1), r.GetString(2)))
        |> List.groupBy fst
        |> List.map (fun (sym, pairs) -> sym, pairs |> List.map snd)
        |> Map.ofList

    /// Get attributes for a symbol by its full name.
    member _.GetAttributesForSymbol(symbolFullName: string) : (string * string) list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT sa.attribute_name, sa.args_json
            FROM symbol_attributes sa
            JOIN symbols s ON s.id = sa.symbol_id
            WHERE s.full_name = @symbolFullName
            """

        cmd.Parameters.AddWithValue("@symbolFullName", symbolFullName) |> ignore
        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0), r.GetString(1))

    /// Get test methods whose symbol is defined in the given source file.
    member _.GetTestMethodsInFile(sourceFile: string) : TestMethodInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name, tm.test_project, tm.test_class, tm.test_method
            FROM test_methods tm
            JOIN symbols s ON s.id = tm.symbol_id
            WHERE s.source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { SymbolFullName = r.GetString(0)
              TestProject = r.GetString(1)
              TestClass = r.GetString(2)
              TestMethod = r.GetString(3) })
