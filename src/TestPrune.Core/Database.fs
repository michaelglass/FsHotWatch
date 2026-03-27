module TestPrune.Database

open System
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
        indexed_at TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS dependencies (
        from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        dep_kind TEXT NOT NULL,
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

    CREATE INDEX IF NOT EXISTS idx_symbols_by_file ON symbols (source_file);
    CREATE INDEX IF NOT EXISTS idx_deps_to ON dependencies (to_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_from ON dependencies (from_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_route_handlers_source ON route_handlers (handler_source_file);
    """

let private symbolKindToString (kind: SymbolKind) =
    match kind with
    | Function -> "Function"
    | Type -> "Type"
    | DuCase -> "DuCase"
    | Module -> "Module"
    | Value -> "Value"
    | Property -> "Property"

let private stringToSymbolKind (s: string) =
    match s with
    | "Function" -> Function
    | "Type" -> Type
    | "DuCase" -> DuCase
    | "Module" -> Module
    | "Value" -> Value
    | "Property" -> Property
    | _ -> Value // fallback for unknown kinds

let private depKindToString =
    function
    | Calls -> "calls"
    | UsesType -> "uses_type"
    | PatternMatches -> "pattern_matches"
    | References -> "references"

let private stringToDepKind =
    function
    | "calls" -> Calls
    | "uses_type" -> UsesType
    | "pattern_matches" -> PatternMatches
    | "references" -> References
    | _ -> References // fallback for unknown values

let private readAll (reader: SqliteDataReader) (f: SqliteDataReader -> 'T) : 'T list =
    let mutable results = []

    while reader.Read() do
        results <- f reader :: results

    results |> List.rev

let private openConnection (dbPath: string) =
    let connStr = $"Data Source=%s{dbPath}"
    let conn = new SqliteConnection(connStr)
    conn.Open()

    use pragmaCmd = conn.CreateCommand()
    pragmaCmd.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"
    pragmaCmd.ExecuteNonQuery() |> ignore

    conn

/// SQLite-backed dependency graph storage.
type Database(dbPath: string) =

    do
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

    /// Create a Database instance, initializing the schema if needed.
    static member create(dbPath: string) = Database(dbPath)

    /// Clear and re-insert symbols, dependencies, and test methods.
    /// All symbols are inserted before any dependencies, so cross-project edges resolve correctly.
    /// When called with a subset of projects, dependency edges to symbols in other projects will
    /// only resolve if those symbols already exist in the database from a prior call.
    member _.RebuildProjects(results: AnalysisResult list) =
        let sourceFiles =
            results
            |> List.collect (fun r -> r.Symbols |> List.map (fun s -> s.SourceFile))
            |> List.distinct

        use conn = openConnection dbPath

        use txn = conn.BeginTransaction()

        try
            for file in sourceFiles do
                use delCmd = conn.CreateCommand()
                delCmd.Transaction <- txn

                delCmd.CommandText <-
                    """
                    DELETE FROM dependencies WHERE from_symbol_id IN (SELECT id FROM symbols WHERE source_file = @file)
                        OR to_symbol_id IN (SELECT id FROM symbols WHERE source_file = @file);
                    DELETE FROM test_methods WHERE symbol_id IN (SELECT id FROM symbols WHERE source_file = @file);
                    DELETE FROM symbols WHERE source_file = @file;
                    """

                delCmd.Parameters.AddWithValue("@file", file) |> ignore
                delCmd.ExecuteNonQuery() |> ignore

            let now = DateTime.UtcNow.ToString("o")

            use insCmd = conn.CreateCommand()
            insCmd.Transaction <- txn

            insCmd.CommandText <-
                """
                INSERT OR REPLACE INTO symbols (full_name, kind, source_file, line_start, line_end, content_hash, indexed_at)
                VALUES (@fullName, @kind, @sourceFile, @lineStart, @lineEnd, @contentHash, @indexedAt)
                """

            let pFullName = insCmd.Parameters.Add("@fullName", SqliteType.Text)
            let pKind = insCmd.Parameters.Add("@kind", SqliteType.Text)
            let pSourceFile = insCmd.Parameters.Add("@sourceFile", SqliteType.Text)
            let pLineStart = insCmd.Parameters.Add("@lineStart", SqliteType.Integer)
            let pLineEnd = insCmd.Parameters.Add("@lineEnd", SqliteType.Integer)
            let pContentHash = insCmd.Parameters.Add("@contentHash", SqliteType.Text)
            let pIndexedAt = insCmd.Parameters.Add("@indexedAt", SqliteType.Text)

            for result in results do
                for sym in result.Symbols do
                    pFullName.Value <- sym.FullName
                    pKind.Value <- symbolKindToString sym.Kind
                    pSourceFile.Value <- sym.SourceFile
                    pLineStart.Value <- sym.LineStart
                    pLineEnd.Value <- sym.LineEnd
                    pContentHash.Value <- sym.ContentHash
                    pIndexedAt.Value <- now
                    insCmd.ExecuteNonQuery() |> ignore

            // Dependencies are inserted after all symbols so cross-project edges resolve
            use depCmd = conn.CreateCommand()
            depCmd.Transaction <- txn

            depCmd.CommandText <-
                """
                INSERT OR IGNORE INTO dependencies (from_symbol_id, to_symbol_id, dep_kind)
                SELECT f.id, t.id, @depKind
                FROM symbols f, symbols t
                WHERE f.full_name = @fromSymbol AND t.full_name = @toSymbol
                """

            let pFromSymbol = depCmd.Parameters.Add("@fromSymbol", SqliteType.Text)
            let pToSymbol = depCmd.Parameters.Add("@toSymbol", SqliteType.Text)
            let pDepKind = depCmd.Parameters.Add("@depKind", SqliteType.Text)

            for result in results do
                for dep in result.Dependencies do
                    pFromSymbol.Value <- dep.FromSymbol
                    pToSymbol.Value <- dep.ToSymbol
                    pDepKind.Value <- depKindToString dep.Kind
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
            let paramNames = changedSymbolNames |> List.mapi (fun i _ -> $"@p%d{i}")

            let placeholders = String.Join(", ", paramNames)

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

            changedSymbolNames
            |> List.iteri (fun i name -> cmd.Parameters.AddWithValue($"@p%d{i}", name) |> ignore)

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
            SELECT full_name, kind, source_file, line_start, line_end, content_hash
            FROM symbols WHERE source_file = @sourceFile
            ORDER BY line_start
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FullName = r.GetString(0)
              Kind = stringToSymbolKind (r.GetString(1))
              SourceFile = r.GetString(2)
              LineStart = r.GetInt32(3)
              LineEnd = r.GetInt32(4)
              ContentHash = r.GetString(5) })

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
            SELECT full_name, kind, source_file, line_start, line_end, content_hash
            FROM symbols ORDER BY source_file, line_start
            """

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FullName = r.GetString(0)
              Kind = stringToSymbolKind (r.GetString(1))
              SourceFile = r.GetString(2)
              LineStart = r.GetInt32(3)
              LineEnd = r.GetInt32(4)
              ContentHash = r.GetString(5) })

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

            let paramNames = rootSymbolNames |> List.mapi (fun i _ -> $"@p%d{i}")
            let placeholders = String.Join(", ", paramNames)

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

            rootSymbolNames
            |> List.iteri (fun i name -> cmd.Parameters.AddWithValue($"@p%d{i}", name) |> ignore)

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

    /// Store a cache key for a project (insert or update).
    member _.SetProjectKey(projectName: string, key: string) =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <- "INSERT OR REPLACE INTO project_keys (project_name, key) VALUES (@projectName, @key)"

        cmd.Parameters.AddWithValue("@projectName", projectName) |> ignore
        cmd.Parameters.AddWithValue("@key", key) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Get the stored cache key for a source file, or None if not yet indexed.
    member _.GetFileKey(sourceFile: string) : string option =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT key FROM file_keys WHERE source_file = @sourceFile"
        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then Some(reader.GetString(0)) else None

    /// Store a cache key for a source file (insert or update).
    member _.SetFileKey(sourceFile: string, key: string) =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <- "INSERT OR REPLACE INTO file_keys (source_file, key) VALUES (@sourceFile, @key)"

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore
        cmd.Parameters.AddWithValue("@key", key) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Get dependencies originating from symbols defined in the given source file.
    member _.GetDependenciesFromFile(sourceFile: string) : Dependency list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT f.full_name, t.full_name, d.dep_kind
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
              Kind = stringToDepKind (r.GetString(2)) })

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
