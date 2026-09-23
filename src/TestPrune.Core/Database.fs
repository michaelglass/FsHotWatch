module TestPrune.Database

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer

[<Literal>]
let internal IndexIncompleteLookupKey = "\u0000testprune:index-incomplete"

[<Literal>]
let internal IndexGenerationLookupKey = "\u0000testprune:index-generation"

[<Literal>]
let internal IndexIncompleteValue = "\u0000testprune:incomplete"

let private schema =
    """
    CREATE TABLE IF NOT EXISTS symbols (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        full_name TEXT NOT NULL UNIQUE,
        kind TEXT NOT NULL,
        is_extern INTEGER NOT NULL DEFAULT 0,
        parent_symbol_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL,
        CONSTRAINT symbols_full_name_is_qualified
            CHECK (kind = 'Module' OR full_name LIKE '%.%')
    );

    CREATE TABLE IF NOT EXISTS symbol_occurrences (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        source_file TEXT NOT NULL,
        line_start INTEGER NOT NULL,
        line_end INTEGER NOT NULL,
        content_hash TEXT NOT NULL DEFAULT '',
        indexed_at TEXT NOT NULL,
        UNIQUE (symbol_id, source_file)
    );

    CREATE TABLE IF NOT EXISTS dependencies (
        from_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        to_symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
        dep_kind TEXT NOT NULL,
        source TEXT NOT NULL DEFAULT 'core',
        source_file TEXT NOT NULL,
        PRIMARY KEY (from_symbol_id, to_symbol_id, dep_kind, source_file)
    );

    CREATE TABLE IF NOT EXISTS test_methods (
        symbol_id INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
        test_project TEXT NOT NULL,
        test_class TEXT NOT NULL,
        test_method TEXT NOT NULL,
        source_file TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS project_keys (
        project_name TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS file_keys (
        source_file TEXT PRIMARY KEY,
        key TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS index_metadata (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
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
        source_file TEXT NOT NULL,
        PRIMARY KEY (symbol_id, attribute_name, args_json, source_file)
    );

    CREATE TABLE IF NOT EXISTS coverage_points (
        occurrence_id INTEGER NOT NULL REFERENCES symbol_occurrences(id) ON DELETE CASCADE,
        line_offset INTEGER NOT NULL,
        kind TEXT NOT NULL DEFAULT 'line',
        hits INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (occurrence_id, line_offset, kind)
    );

    CREATE TABLE IF NOT EXISTS runtime_coverage (
        test_project TEXT NOT NULL,
        source_file TEXT NOT NULL,
        PRIMARY KEY (test_project, source_file)
    );

    CREATE TABLE IF NOT EXISTS runtime_coverage_baselines (
        test_project TEXT PRIMARY KEY,
        run_id TEXT NOT NULL,
        observed_at TEXT NOT NULL
    );

    CREATE INDEX IF NOT EXISTS idx_occurrences_by_file ON symbol_occurrences (source_file, line_start);
    CREATE INDEX IF NOT EXISTS idx_occurrences_by_symbol ON symbol_occurrences (symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_by_file ON dependencies (source_file);
    CREATE INDEX IF NOT EXISTS idx_test_methods_by_file ON test_methods (source_file);
    CREATE INDEX IF NOT EXISTS idx_symbol_attrs_by_file ON symbol_attributes (source_file);
    CREATE INDEX IF NOT EXISTS idx_symbols_by_parent ON symbols (parent_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_to ON dependencies (to_symbol_id, from_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_deps_from ON dependencies (from_symbol_id);
    CREATE INDEX IF NOT EXISTS idx_events_run_id ON analysis_events(run_id);
    CREATE INDEX IF NOT EXISTS idx_events_type ON analysis_events(event_type);
    CREATE INDEX IF NOT EXISTS idx_symbol_attrs_by_symbol ON symbol_attributes (symbol_id);
    CREATE INDEX IF NOT EXISTS idx_coverage_by_occurrence ON coverage_points (occurrence_id);
    CREATE INDEX IF NOT EXISTS idx_runtime_coverage_by_file ON runtime_coverage (source_file);
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
    | SharedLiteral -> "shared_literal"

let private stringToDepKind (warned: HashSet<string>) =
    function
    | "calls" -> Calls
    | "uses_type" -> UsesType
    | "pattern_matches" -> PatternMatches
    | "references" -> References
    | "shared_state" -> SharedState
    | "shared_literal" -> SharedLiteral
    | unknown ->
        if warned.Add($"DependencyKind:%s{unknown}") then
            eprintfn $"Warning: unknown DependencyKind '%s{unknown}' in database, defaulting to References"

        References

let private readAll (reader: SqliteDataReader) (f: SqliteDataReader -> 'T) : 'T list =
    let mutable results = []

    while reader.Read() do
        results <- f reader :: results

    results |> List.rev

/// The right-hand side of an `IN (...)` membership test over a caller-supplied set of
/// names, as a subquery over ONE bound parameter carrying the whole set as a JSON array.
/// Pair it with `bindNameSetNamed` under the same prefix.
///
/// The obvious spelling — `IN (@p0, @p1, ..., @pN)` — spends one SQLite HOST PARAMETER
/// per name, and SQLite caps those per prepared statement at `SQLITE_MAX_VARIABLE_NUMBER`:
/// 32766 in the `SQLitePCLRaw.lib.e_sqlite3` build this package pins, which is not a number
/// to take on faith — `SELECT sqlite_compileoption_get(n)` reports it, and
/// `SqliteVariableLimitTests` reads it back from the loaded library rather than asserting a
/// remembered constant. Every such query therefore carried a ceiling set by REPOSITORY SIZE
/// rather than by anything its caller could see, which is exactly why it survived: the
/// impact-filtered path passes a handful of changed symbols, and only a full unfiltered
/// pass passes the whole graph. One over a 64,913-symbol index did, and the flush died with
/// `SQLite Error 1: 'too many SQL variables'`.
///
/// One parameter per SET rather than per name removes the ceiling by construction: no list
/// length changes the parameter count, so there is no longer a length at which these
/// queries begin to fail. Chunking would have raised the ceiling without removing it — and
/// here it would also be WRONG: `QueryAffectedTestsCore` defines its `barriers` CTE by
/// exclusion from the seed set, so a per-chunk walk treats seeds outside its own chunk as
/// barriers, stops expanding through them, and silently selects fewer tests.
///
/// This is not a performance trade either way; see
/// `docs/adr/0003-name-sets-travel-as-one-json-parameter.md` for the measurement and for
/// the per-connection temp table that was the other candidate.
///
/// The prefix exists so one command can carry two independent sets without their parameter
/// names colliding.
let private nameSetNamed (prefix: string) =
    $"SELECT value FROM json_each(@%s{prefix})"

let private bindNameSetNamed (prefix: string) (cmd: SqliteCommand) (names: string list) =
    cmd.Parameters.AddWithValue($"@%s{prefix}", JsonSerializer.Serialize names)
    |> ignore

/// The default-prefix pair. A command needing two independent sets uses
/// `nameSetNamed`/`bindNameSetNamed` with distinct prefixes instead.
let private nameSet = nameSetNamed "p"

let private bindNameSet (cmd: SqliteCommand) (names: string list) = bindNameSetNamed "p" cmd names

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
///
/// Every added column or table needs a bump: `CREATE TABLE IF NOT EXISTS` does not
/// migrate an existing table, so without one a stale DB survives open and throws
/// "no such column"/"no such table" on the first write. A recreate loses nothing
/// durable — the index is regenerated on the next scan.
///
/// v1..v2 — pre-extern cross-project schema
/// v3     — ExternRef + extern symbols (2.0.0)
/// v4     — `dependencies.source`, `symbol_attributes`, `symbols.is_extern` (3.0.0)
/// v5     — `symbols.parent_symbol_id` (self-referential nullable FK) for aggregate-type
///          invalidation: a change to any member of a type lifts the "changed" set to
///          include the type and its sibling members, so tests that touched any part of
///          the type are selected.
/// v6     — `coverage_points` table, keyed by `(symbol_id, line_offset)` so coverage
///          survives source edits: a moved symbol's coverage follows via the derived
///          `line_start + line_offset`, a changed symbol's coverage is purged.
/// v7     — nullable `route_handlers.handler_function`, scoping route edges to the
///          specific handler function serving each route instead of the whole file.
/// v8     — removed `route_handlers` from the core schema. HTTP routes are not a core
///          concept: the table (and the `RouteHandlerEntry` type that shaped it) belong
///          to TestPrune.Falco, which owns them through the `PluginStore` seam
///          (`Ports.toPluginStore`). A v7 DB is still readable by this schema, but the
///          bump keeps `user_version` an honest description of what core manages, and
///          the recreate is free: the plugin re-creates and re-seeds its table.
/// v9     — `CHECK (kind = 'Module' OR full_name LIKE '%.%')` on `symbols`. An
///          unqualified `full_name` is never a real symbol, and because `full_name` is
///          UNIQUE with `ON CONFLICT DO UPDATE`, every same-named thing in the repo
///          UPSERTs onto that ONE row: a single row named `name` acquired 413 dependents
///          and selected 2,837 tests per run. SQLite has no `ALTER TABLE ADD
///          CONSTRAINT`, so the constraint can only arrive by rebuilding the table.
///
///          `kind = 'Module'` is scoping the constraint needs, not a loophole: a
///          top-level single-segment module (`module Alpha`) has NO qualifier to have —
///          FCS reports its `FullName` as `Alpha` — so an unconditional constraint would
///          hard-fail indexing on any project containing one. That is the ONLY
///          unqualified category.
/// v10    — no schema text change; a REBUILD, because v9 shipped the constraint inert and
///          the rows it should have rejected are still in every v9 database.
///
///          v9's extern placeholder pass chose a kind from the shape of the string — no
///          dot, therefore a module — so every unqualified name was relabelled into the
///          one kind the CHECK exempts and no input could fail it. Measured on a v9 index
///          of a ~9,300-test repo: 32 such rows, all `Module`, all computation-expression
///          query keywords (`where` with 451 direct dependents reaching 1,828 test
///          methods, `select` 337/1,826, `entity`, `set`, `take`, `orderBy`, …).
///
///          Two changes retire them. `AstAnalyzer.qualifyThroughDeclaringEntity` names a
///          custom operation after the member it resolves to (`SqlHydra.Query...Where`,
///          the name the definition side already records) instead of after the keyword,
///          and the placeholder pass now takes `Module` only on evidence that FCS
///          classified that name as a module. Neither reaches an existing row: an extern
///          row's `source_file` is never in a re-indexed set, so orphan cleanup cannot
///          collect it and the junk would outlive the fix in place. The recreate is what
///          removes it.
/// v11    — `SharedLiteral` dependency edges and their synthetic literal nodes. Existing
///          files may otherwise remain cache hits forever, leaving the new graph edges
///          absent even though the executable understands them. Rebuilding guarantees
///          that every indexed file was analyzed under the same literal-edge semantics.
/// v12    — project-attributed runtime coverage. `coverage_points` deliberately merges
///          every report into a run-independent high-water mark and therefore cannot
///          answer which test project executed a file. The two runtime-coverage tables
///          retain that provenance plus the last complete baseline run per project.
/// v13    — durable index-attempt metadata. Selection must remain fail-closed across
///          failed, crashed, or concurrent indexing attempts, so older caches are
///          recreated before the completion protocol is used.
/// v14    — `symbol_occurrences`: a symbol is ONE row with one source occurrence per
///          declaring file. A `.fsi` declaration and its `.fs` implementation share one
///          compiler identity, and a single `source_file`/`content_hash` per row made the
///          last-indexed file overwrite the other. Occurrences carry the location and
///          hash; dependencies, test methods and attributes carry the `source_file` that
///          contributed them, so re-indexing one file replaces only that file's facts;
///          coverage points belong to the occurrence whose lines they were measured on.
///          See ADR 0004, which supersedes ADR 0002's synthetic signature nodes.
///
/// A `SchemaVersion` bump DELETES the database file, so it drops every PLUGIN-owned
/// table too — core cannot migrate a table it does not know about. That is safe only
/// because a plugin store must treat its table as absent until it has issued its own
/// `CREATE TABLE IF NOT EXISTS` (see `Ports.PluginStore`), and its contents must be
/// derivable again (seeded per run), never the sole copy of anything.
///
/// Public so external read-only consumers (e.g. FsHotWatch's `fshw dead-code`) can probe
/// a live DB's `PRAGMA user_version` for compatibility BEFORE opening via
/// `Database.create`, whose recreate-on-mismatch would wipe a daemon's symbol graph. A
/// consumer hardcoding this value instead would have its protection silently invert on
/// the next bump: an old-version DB would pass the stale probe and then be recreated by
/// the newer open path.
[<Literal>]
let SchemaVersion = 14

/// Delete the SQLite database file at `dbPath` along with its WAL mode
/// sidecars (`-wal`, `-shm`). Deleting only the main file leaves stale
/// sidecars that a later SQLite connection may attempt to "recover"
/// against a freshly-created empty DB, producing a 0-byte main DB with
/// no tables. Consumers recovering from schema drift should call this,
/// not `File.Delete` directly.
let deleteCacheFiles (dbPath: string) =
    if File.Exists(dbPath) then
        File.Delete(dbPath)

    for ext in [ "-wal"; "-shm" ] do
        let p = dbPath + ext

        if File.Exists(p) then
            File.Delete(p)

let private hasUserTables (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()

    cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"

    cmd.ExecuteScalar() :?> int64 > 0L

/// Open a connection, deleting and recreating the database if the schema version is
/// incompatible. Returns `(connection, wasFresh)`; see `Database.WasRecreated` for what
/// `wasFresh` obliges a caller with a derived cache to do.
let private openCheckedConnection (dbPath: string) : SqliteConnection * bool =
    if File.Exists(dbPath) then
        let conn = openConnection dbPath
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version;"
        let version = cmd.ExecuteScalar() :?> int64 |> int

        // `user_version = 0` on a file with existing tables is a pre-versioning stale
        // DB — CREATE TABLE IF NOT EXISTS won't migrate its schema, so recreate.
        //
        // `version > SchemaVersion` means a NEWER process wrote this DB. Older
        // code opening it must not clobber: the newer schema likely has
        // additive columns we don't know about but don't need. Nuking causes
        // data loss across version skew (e.g. a consumer's build tool
        // pinned to v3.0.2 clobbering a v3.1 daemon's DB before every test run
        // — then the daemon hits "no such column" on the next flush).
        let isIncompatible =
            if version = SchemaVersion then false
            elif version > SchemaVersion then false // forward-compat
            elif version = 0 then hasUserTables conn
            else true

        if isIncompatible then
            eprintfn $"Schema version mismatch (found v%d{version}, expected v%d{SchemaVersion}). Recreating database."

            SqliteConnection.ClearPool(conn)
            conn.Dispose()
            deleteCacheFiles dbPath
            (openConnection dbPath, true)
        else
            (conn, false)
    else
        // First-ever creation: no prior symbols, so any sibling cache is stale too.
        (openConnection dbPath, true)

/// SQLite-backed dependency graph storage.
type Database(dbPath: string) =
    let warnedUnknownKinds = HashSet<string>()
    let mutable wasRecreated = false

    do
        let openedConn, fresh = openCheckedConnection dbPath
        wasRecreated <- fresh
        use conn = openedConn
        use cmd = conn.CreateCommand()
        cmd.CommandText <- schema
        cmd.ExecuteNonQuery() |> ignore

        use versionCmd = conn.CreateCommand()
        versionCmd.CommandText <- "PRAGMA user_version;"
        let currentVersion = versionCmd.ExecuteScalar() :?> int64 |> int

        // Only stamp when our schema is newer (or the file was unversioned).
        // Never downgrade a future version — that would erase the marker a
        // newer process relies on to detect older clients touching its DB.
        if currentVersion < SchemaVersion then
            use setCmd = conn.CreateCommand()
            setCmd.CommandText <- $"PRAGMA user_version = %d{SchemaVersion};"
            setCmd.ExecuteNonQuery() |> ignore

    /// True when this DB had no usable prior state when opened — the file did not exist,
    /// or an incompatible schema (a `SchemaVersion` bump) forced a delete+recreate. A
    /// consumer with a sibling cache derived from this DB's symbols (e.g. an FCS check
    /// cache that decides which files to re-index) should invalidate it when this is true:
    /// the recreated DB has lost every symbol the stale cache assumes is still indexed.
    member _.WasRecreated = wasRecreated

    /// Create a Database instance, initializing the schema if needed.
    static member create(dbPath: string) = Database(dbPath)

    /// Open a fresh connection to the cache database (WAL, foreign keys on); the caller
    /// disposes it. This is the storage seam for extensions that must persist facts core
    /// knows nothing about — data the AST cannot see and that is seeded from outside
    /// (e.g. TestPrune.Falco's HTTP route table). Reach it through `Ports.toPluginStore`,
    /// never by constructing a connection string: a plugin store obtained from a live
    /// `Database` is guaranteed to have gone through the `SchemaVersion` check first.
    ///
    /// A plugin owns its tables; core owns the FILE. Core will delete and recreate the
    /// file on a `SchemaVersion` mismatch, dropping plugin tables with it, so a plugin
    /// must issue idempotent `CREATE TABLE IF NOT EXISTS` DDL before every use and must
    /// never store anything it cannot re-derive.
    member _.OpenConnection() : SqliteConnection = openConnection dbPath

    /// Capture the literal nodes that point at changed production symbols in the
    /// currently persisted graph. Call this before replacing those symbols with a
    /// fresh analysis, then include the returned names in the subsequent impact query.
    ///
    /// A message edit removes the producer's OLD literal edge during rebuild, while an
    /// unchanged test still points at that old literal. Seeding the old node carries
    /// precisely that pre-change evidence across the rebuild without retaining a stale
    /// producer edge in the graph. The seed is caller-owned and therefore lasts only as
    /// long as the caller's ordinary pending-verification lifecycle.
    member _.GetPriorSharedLiteralSeeds(changedSymbolNames: string list) : string list =
        if changedSymbolNames.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                SELECT DISTINCT literal.full_name
                FROM dependencies dependency
                JOIN symbols literal ON literal.id = dependency.from_symbol_id
                JOIN symbols producer ON producer.id = dependency.to_symbol_id
                WHERE dependency.dep_kind = 'shared_literal'
                  AND producer.full_name IN (%s{nameSet})
                ORDER BY literal.full_name
                """

            bindNameSet cmd changedSymbolNames

            use reader = cmd.ExecuteReader()
            let seeds = ResizeArray<string>()

            while reader.Read() do
                seeds.Add(reader.GetString(0))

            List.ofSeq seeds

    /// Clear and re-insert symbols, dependencies, and test methods.
    ///
    /// Pass ONE `AnalysisResult` PER SOURCE FILE. Each edge, test method and attribute is
    /// owned by the file whose result carried it (`AstAnalyzer.factOwner`), and re-indexing
    /// a file replaces exactly the facts it owns. Do not merge a project's results into
    /// one: a result declaring the same name in two files (a `.fsi` and its `.fs`) is
    /// rejected with an `ArgumentException`, because the owner of that name's facts would
    /// be ambiguous.
    ///
    /// All symbols are inserted before any dependencies, so cross-project edges resolve correctly.
    /// When called with a subset of projects, dependency edges to symbols in other projects will
    /// only resolve if those symbols already exist in the database from a prior call.
    /// Optional fileKeys/projectKeys are written in the same transaction for atomicity.
    ///
    /// Symbol row ids are preserved across re-indexing (UPSERT on full_name), so incoming
    /// dependency edges from files NOT being re-analyzed survive. This is essential for
    /// cross-file impact analysis: changing Lib.fs must not cascade-delete edges from
    /// Tests.fs → Lib.fs symbols, or QueryAffectedTests would miss the dependent tests.
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
            // Clear the facts each re-indexed file contributed — its edges, test methods
            // and attributes, whichever symbol they hang off (`AstAnalyzer.factOwner`).
            // Facts another file contributed survive, including edges INTO these files'
            // symbols (row ids are preserved by the UPSERT below) and a signature file's
            // edges when only its implementation is re-indexed, or vice versa.
            if not sourceFiles.IsEmpty then
                for table in [ "dependencies"; "test_methods"; "symbol_attributes" ] do
                    use delCmd = conn.CreateCommand()
                    delCmd.Transaction <- txn

                    delCmd.CommandText <- $"DELETE FROM %s{table} WHERE source_file IN (%s{nameSet})"

                    bindNameSet delCmd sourceFiles
                    delCmd.ExecuteNonQuery() |> ignore

            let now = DateTime.UtcNow.ToString("o")

            // A symbol is one row per name; each declaring file has its own occurrence
            // row carrying location and hash. UPSERT preserves both ids on conflict, so
            // incoming foreign-key references (edges, coverage) remain valid.
            //
            // A real occurrence marks its symbol real. An extern placeholder is a symbol
            // row with no occurrence at all and never overwrites a real one.
            use realSymbolCmd = conn.CreateCommand()
            realSymbolCmd.Transaction <- txn

            realSymbolCmd.CommandText <-
                """
                INSERT INTO symbols (full_name, kind, is_extern)
                VALUES (@fullName, @kind, 0)
                ON CONFLICT(full_name) DO UPDATE SET
                    kind = excluded.kind,
                    is_extern = 0
                RETURNING id
                """

            use externSymbolCmd = conn.CreateCommand()
            externSymbolCmd.Transaction <- txn

            externSymbolCmd.CommandText <-
                """
                INSERT INTO symbols (full_name, kind, is_extern)
                VALUES (@fullName, @kind, 1)
                ON CONFLICT(full_name) DO NOTHING
                """

            for cmd in [ realSymbolCmd; externSymbolCmd ] do
                cmd.Parameters.Add("@fullName", SqliteType.Text) |> ignore
                cmd.Parameters.Add("@kind", SqliteType.Text) |> ignore

            use occurrenceCmd = conn.CreateCommand()
            occurrenceCmd.Transaction <- txn

            occurrenceCmd.CommandText <-
                """
                INSERT INTO symbol_occurrences (symbol_id, source_file, line_start, line_end, content_hash, indexed_at)
                VALUES (@symbolId, @sourceFile, @lineStart, @lineEnd, @contentHash, @indexedAt)
                ON CONFLICT(symbol_id, source_file) DO UPDATE SET
                    line_start = excluded.line_start,
                    line_end = excluded.line_end,
                    content_hash = excluded.content_hash,
                    indexed_at = excluded.indexed_at
                """

            let pOccSymbol = occurrenceCmd.Parameters.Add("@symbolId", SqliteType.Integer)
            let pOccFile = occurrenceCmd.Parameters.Add("@sourceFile", SqliteType.Text)
            let pOccStart = occurrenceCmd.Parameters.Add("@lineStart", SqliteType.Integer)
            let pOccEnd = occurrenceCmd.Parameters.Add("@lineEnd", SqliteType.Integer)
            let pOccHash = occurrenceCmd.Parameters.Add("@contentHash", SqliteType.Text)
            occurrenceCmd.Parameters.AddWithValue("@indexedAt", now) |> ignore

            // Must run BEFORE the upsert overwrites each occurrence's stored content_hash.
            // A changed hash means the stored coverage offsets were computed against the
            // old body and are now invalid, so purge them; the impact re-run re-ingests
            // fresh coverage. A MOVED occurrence (same hash, shifted line_start) keeps its
            // coverage — offsets are stable and GetFileCoverage re-derives the absolute
            // line. New occurrences and removed ones (CASCADE via the coverage_points FK on
            // the orphan DELETE below) need nothing here.
            use purgeCovCmd = conn.CreateCommand()
            purgeCovCmd.Transaction <- txn

            purgeCovCmd.CommandText <-
                """
                DELETE FROM coverage_points
                WHERE occurrence_id IN (
                    SELECT o.id FROM symbol_occurrences o
                    JOIN symbols s ON s.id = o.symbol_id
                    WHERE s.full_name = @fullName
                      AND o.source_file = @sourceFile
                      AND o.content_hash <> @contentHash
                )
                """

            let pPurgeFullName = purgeCovCmd.Parameters.Add("@fullName", SqliteType.Text)
            let pPurgeFile = purgeCovCmd.Parameters.Add("@sourceFile", SqliteType.Text)
            let pPurgeHash = purgeCovCmd.Parameters.Add("@contentHash", SqliteType.Text)

            // Only an index that has ingested coverage has anything to purge; skipping the
            // per-occurrence probe otherwise keeps a plain re-index at one write per row.
            let hasCoverage =
                use probe = conn.CreateCommand()
                probe.Transaction <- txn
                probe.CommandText <- "SELECT EXISTS(SELECT 1 FROM coverage_points)"

                match probe.ExecuteScalar() with
                | :? int64 as n -> n <> 0L
                | _ -> true

            for result in results do
                for sym in result.Symbols do
                    if hasCoverage && not sym.IsExtern then
                        pPurgeFullName.Value <- sym.FullName
                        pPurgeFile.Value <- sym.SourceFile
                        pPurgeHash.Value <- sym.ContentHash
                        purgeCovCmd.ExecuteNonQuery() |> ignore

            for result in results do
                for sym in result.Symbols do
                    let cmd = if sym.IsExtern then externSymbolCmd else realSymbolCmd
                    cmd.Parameters["@fullName"].Value <- sym.FullName
                    cmd.Parameters["@kind"].Value <- symbolKindToString sym.Kind

                    // SQLite reports only the constraint name
                    // (`symbols_full_name_is_qualified`), never the offending row. Since the
                    // whole point of the constraint is to surface a bad name, the failure
                    // has to carry that name and its file.
                    let symbolId =
                        try
                            cmd.ExecuteScalar()
                        with :? SqliteException as ex ->
                            raise (
                                SqliteException(
                                    $"Symbol '%s{sym.FullName}' (kind %A{sym.Kind}, from %s{sym.SourceFile}) was rejected by the symbols table: %s{ex.Message}",
                                    ex.SqliteErrorCode,
                                    ex.SqliteExtendedErrorCode
                                )
                            )

                    if not sym.IsExtern then
                        pOccSymbol.Value <- symbolId
                        pOccFile.Value <- sym.SourceFile
                        pOccStart.Value <- sym.LineStart
                        pOccEnd.Value <- sym.LineEnd
                        pOccHash.Value <- sym.ContentHash
                        occurrenceCmd.ExecuteNonQuery() |> ignore

            // Orphan cleanup: every occurrence touched by this pass had its indexed_at
            // bumped to `now`. An occurrence in the re-indexed files still carrying an older
            // timestamp wasn't in the new analysis — delete it (CASCADE takes its coverage).
            // A symbol survives while ANY file still declares it; a real symbol whose every
            // occurrence is such a stale one was removed from the code, so delete it and
            // CASCADE its edges. Only symbols with a stale occurrence are candidates, so a
            // one-file re-index does not scan the whole symbol table.
            if not sourceFiles.IsEmpty then
                use delUndeclared = conn.CreateCommand()
                delUndeclared.Transaction <- txn

                delUndeclared.CommandText <-
                    $"""
                    DELETE FROM symbols
                    WHERE is_extern = 0
                      AND id IN (
                          SELECT symbol_id FROM symbol_occurrences
                          WHERE source_file IN (%s{nameSet}) AND indexed_at < @now
                      )
                      AND NOT EXISTS (
                          SELECT 1 FROM symbol_occurrences o
                          WHERE o.symbol_id = symbols.id
                            AND (o.indexed_at >= @now OR o.source_file NOT IN (%s{nameSet}))
                      )
                    """

                bindNameSet delUndeclared sourceFiles
                delUndeclared.Parameters.AddWithValue("@now", now) |> ignore
                delUndeclared.ExecuteNonQuery() |> ignore

                use delOrphan = conn.CreateCommand()
                delOrphan.Transaction <- txn

                delOrphan.CommandText <-
                    $"DELETE FROM symbol_occurrences WHERE source_file IN (%s{nameSet}) AND indexed_at < @now"

                bindNameSet delOrphan sourceFiles
                delOrphan.Parameters.AddWithValue("@now", now) |> ignore
                delOrphan.ExecuteNonQuery() |> ignore

            // Reset parent links for re-indexed files before re-populating below. Parent links
            // from non-re-indexed files are untouched (their row ids were preserved by the
            // symbol UPSERT). Clearing first handles the case where a member moved to a
            // different type or out of a type entirely.
            if not sourceFiles.IsEmpty then
                use clrParent = conn.CreateCommand()
                clrParent.Transaction <- txn

                clrParent.CommandText <-
                    $"UPDATE symbols SET parent_symbol_id = NULL WHERE id IN (SELECT symbol_id FROM symbol_occurrences WHERE source_file IN (%s{nameSet}))"

                bindNameSet clrParent sourceFiles
                clrParent.ExecuteNonQuery() |> ignore

            // Populate parent_symbol_id from analyzer-supplied links. Only members of
            // non-module types produce links, so module bindings stay with NULL parents
            // and aggregate-type invalidation doesn't fan out across module siblings.
            use plCmd = conn.CreateCommand()
            plCmd.Transaction <- txn

            plCmd.CommandText <-
                """
                UPDATE symbols
                SET parent_symbol_id = (SELECT id FROM symbols p WHERE p.full_name = @parent)
                WHERE full_name = @child
                """

            let pPlChild = plCmd.Parameters.Add("@child", SqliteType.Text)
            let pPlParent = plCmd.Parameters.Add("@parent", SqliteType.Text)

            for result in results do
                for link in result.ParentLinks do
                    pPlChild.Value <- link.Child
                    pPlParent.Value <- link.Parent
                    plCmd.ExecuteNonQuery() |> ignore

            // Dependencies are inserted after all symbols so cross-project edges resolve
            use depCmd = conn.CreateCommand()
            depCmd.Transaction <- txn

            depCmd.CommandText <-
                """
                INSERT OR IGNORE INTO dependencies (from_symbol_id, to_symbol_id, dep_kind, source, source_file)
                SELECT f.id, t.id, @depKind, @source, @sourceFile
                FROM symbols f, symbols t
                WHERE f.full_name = @fromSymbol AND t.full_name = @toSymbol
                """

            let pFromSymbol = depCmd.Parameters.Add("@fromSymbol", SqliteType.Text)
            let pToSymbol = depCmd.Parameters.Add("@toSymbol", SqliteType.Text)
            let pDepKind = depCmd.Parameters.Add("@depKind", SqliteType.Text)
            let pSource = depCmd.Parameters.Add("@source", SqliteType.Text)
            let pDepFile = depCmd.Parameters.Add("@sourceFile", SqliteType.Text)

            for result in results do
                let owner = factOwner result

                for dep in result.Dependencies do
                    pFromSymbol.Value <- dep.FromSymbol
                    pToSymbol.Value <- dep.ToSymbol
                    pDepKind.Value <- depKindToString dep.Kind
                    pSource.Value <- dep.Source
                    pDepFile.Value <- owner (dependencyAnchor dep)
                    depCmd.ExecuteNonQuery() |> ignore

            // A literal node is shared by every test/producer that contains its decoded
            // value, so no one file owns its row. Collect it only after every fresh edge
            // has been inserted, and only when neither side of any edge still mentions it.
            // Prefix + is_extern keeps this sweep away from ordinary extern placeholders.
            use delUnusedLiteralNodes = conn.CreateCommand()
            delUnusedLiteralNodes.Transaction <- txn

            delUnusedLiteralNodes.CommandText <-
                """
                DELETE FROM symbols
                WHERE is_extern = 1
                  AND substr(full_name, 1, length(@literalPrefix)) = @literalPrefix
                  AND NOT EXISTS (
                      SELECT 1 FROM dependencies
                      WHERE from_symbol_id = symbols.id OR to_symbol_id = symbols.id
                  )
                """

            delUnusedLiteralNodes.Parameters.AddWithValue("@literalPrefix", SyntheticLiteralPrefix)
            |> ignore

            delUnusedLiteralNodes.ExecuteNonQuery() |> ignore

            use tmCmd = conn.CreateCommand()
            tmCmd.Transaction <- txn

            tmCmd.CommandText <-
                """
                INSERT OR REPLACE INTO test_methods (symbol_id, test_project, test_class, test_method, source_file)
                SELECT id, @testProject, @testClass, @testMethod, @sourceFile
                FROM symbols WHERE full_name = @symbolFullName
                """

            let pSymbolFullName = tmCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
            let pTestProject = tmCmd.Parameters.Add("@testProject", SqliteType.Text)
            let pTestClass = tmCmd.Parameters.Add("@testClass", SqliteType.Text)
            let pTestMethod = tmCmd.Parameters.Add("@testMethod", SqliteType.Text)
            let pTestFile = tmCmd.Parameters.Add("@sourceFile", SqliteType.Text)

            for result in results do
                let owner = factOwner result

                for tm in result.TestMethods do
                    pSymbolFullName.Value <- tm.SymbolFullName
                    pTestProject.Value <- tm.TestProject
                    pTestClass.Value <- tm.TestClass
                    pTestMethod.Value <- tm.TestMethod
                    pTestFile.Value <- owner tm.SymbolFullName
                    tmCmd.ExecuteNonQuery() |> ignore

            use attrCmd = conn.CreateCommand()
            attrCmd.Transaction <- txn

            attrCmd.CommandText <-
                """
                INSERT OR IGNORE INTO symbol_attributes (symbol_id, attribute_name, args_json, source_file)
                SELECT id, @attrName, @argsJson, @sourceFile
                FROM symbols WHERE full_name = @symbolFullName
                """

            let pAttrSymbol = attrCmd.Parameters.Add("@symbolFullName", SqliteType.Text)
            let pAttrName = attrCmd.Parameters.Add("@attrName", SqliteType.Text)
            let pArgsJson = attrCmd.Parameters.Add("@argsJson", SqliteType.Text)
            let pAttrFile = attrCmd.Parameters.Add("@sourceFile", SqliteType.Text)

            for result in results do
                let owner = factOwner result

                for attr in result.Attributes do
                    pAttrSymbol.Value <- attr.SymbolFullName
                    pAttrName.Value <- attr.AttributeName
                    pArgsJson.Value <- attr.ArgsJson
                    pAttrFile.Value <- owner attr.SymbolFullName
                    attrCmd.ExecuteNonQuery() |> ignore

            // Cache keys go in this same transaction, so they can never claim a file is
            // indexed when its symbols rolled back.
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
    ///
    /// `applyBarriers` decides whether `[<TestPrune.CompositionRoot>]` markers are
    /// honoured. When any marker exists `QueryAffectedTests` runs this BOTH ways and
    /// merges the two results per test project — see the fail-safe there. (It does not
    /// "retry on empty": a global emptiness check is the version that was wrong, because
    /// surviving unit tests mask an emptied integration project.)
    member private _.QueryAffectedTestsCore
        (changedSymbolNames: string list, applyBarriers: bool)
        : TestMethodInfo list =
        if changedSymbolNames.IsEmpty then
            []
        else
            use conn = openConnection dbPath

            use cmd = conn.CreateCommand()

            // Aggregate-type invalidation: before the transitive walk, expand the set of
            // "changed" symbol ids in both directions along the containment relation, but
            // ONLY when the containing entity is a Type (never a Module). The two phases
            // must be ordered — the lift has to happen first for a lone-member change to
            // reach its siblings through the type it lifted to.
            //
            //   member → parent type:  a lone-member edit lifts to the type, so tests that
            //                          touched the type itself or sibling members are hit.
            //   parent type → members: a type-body edit expands to every member, so tests
            //                          that accessed any specific member through the type
            //                          are hit even though their direct edge is to that
            //                          sibling member, not to the type.
            //
            // Module members deliberately do not have parent_symbol_id set (see
            // AstAnalyzer.fs parentLinks logic), so the expansion can't fan out across
            // unrelated module siblings.
            let typeKindStr = symbolKindToString Type

            // Composition-root barriers. A symbol marked `[<TestPrune.CompositionRoot>]`
            // (a routing table, a DI registration block) references the whole application
            // in order to WIRE IT UP, so an integration fixture that boots the app depends
            // on it and every handler transitively reaches every fixture-using test. The
            // edges are real; the relevance they imply is not, and nothing in the graph
            // tells composition apart from use — hence the marker.
            //
            // `barriers` excludes anything in `expanded`, and that exclusion is the whole
            // asymmetry: a barrier REACHED from something it aggregates stops the walk,
            // while a barrier that CHANGED is an ordinary seed whose dependents are walked
            // in full. The wiring changing IS what host-booting tests verify.
            //
            // The barrier is still reported affected (it enters `transitive_deps` by the
            // ordinary branches); only its own outward expansion is withheld. Any other,
            // non-barrier path to the same dependents is unaffected — this is a set union,
            // so blocking one route never removes what another route supplies.
            //
            // `applyBarriers = false` matches the marker against `NULL`, which SQLite never
            // satisfies, so the `barriers` CTE selects no rows and the `NOT IN (barriers)`
            // guard in the recursive branch holds for EVERY row — the walk is then exactly
            // the historical one. (Note the CTE is still evaluated in that case; see the
            // deliberately-not-taken optimisation noted on `HasCompositionRoots`.)
            let markerNameSet = if applyBarriers then nameSetNamed "cr" else "NULL"

            cmd.CommandText <-
                $"""
                WITH after_lift AS (
                    SELECT id FROM symbols WHERE full_name IN (%s{nameSet})
                    UNION
                    SELECT parent.id
                    FROM symbols child
                    JOIN symbols parent ON child.parent_symbol_id = parent.id
                    WHERE child.full_name IN (%s{nameSet})
                      AND parent.kind = @typeKind
                ),
                expanded AS (
                    SELECT id FROM after_lift
                    UNION
                    SELECT child.id
                    FROM symbols child
                    JOIN symbols parent ON child.parent_symbol_id = parent.id
                    WHERE parent.id IN (SELECT id FROM after_lift)
                      AND parent.kind = @typeKind
                ),
                barriers AS (
                    SELECT sa.symbol_id AS id
                    FROM symbol_attributes sa
                    WHERE sa.attribute_name IN (%s{markerNameSet})
                      AND sa.symbol_id NOT IN (SELECT id FROM expanded)
                ),
                transitive_deps AS (
                    -- Seed inclusion: the changed/expanded symbols are themselves
                    -- in the affected set, not only their dependents. A changed
                    -- test method has no incoming edges, so without this anchor it
                    -- selected zero tests — diverging from the in-memory reference
                    -- store, whose `transitiveClosure` includes its seeds.
                    -- (FsHotWatch ISSUE B: a fixed test stayed pinned red.)
                    SELECT id AS from_symbol_id FROM expanded
                    UNION
                    -- Direct dependents of the seeds, barrier or not: a barrier is
                    -- reported affected, it just doesn't expand below.
                    SELECT from_symbol_id FROM dependencies
                    WHERE to_symbol_id IN (SELECT id FROM expanded)
                    UNION
                    SELECT d.from_symbol_id FROM dependencies d
                    JOIN transitive_deps td ON d.to_symbol_id = td.from_symbol_id
                    WHERE td.from_symbol_id NOT IN (SELECT id FROM barriers)
                )
                SELECT DISTINCT s.full_name, tm.test_project, tm.test_class, tm.test_method
                FROM transitive_deps td
                JOIN test_methods tm ON tm.symbol_id = td.from_symbol_id
                JOIN symbols s ON s.id = tm.symbol_id
                """

            bindNameSet cmd changedSymbolNames
            cmd.Parameters.AddWithValue("@typeKind", typeKindStr) |> ignore

            if applyBarriers then
                bindNameSetNamed "cr" cmd Domain.CompositionRoot.Names

            use reader = cmd.ExecuteReader()

            readAll reader (fun r ->
                { SymbolFullName = r.GetString(0)
                  TestProject = r.GetString(1)
                  TestClass = r.GetString(2)
                  TestMethod = r.GetString(3) })

    /// Is any symbol carrying the composition-root marker? Kept separate so an
    /// un-annotated repo runs ONE walk and takes exactly the historical code path
    /// rather than the two-walk fail-safe below.
    ///
    /// COST, measured rather than assumed: `symbol_attributes` is indexed on
    /// `symbol_id` only (see `schema`), so this is a full table SCAN — ~0.45 ms over
    /// 12k attribute rows, and proving ABSENCE is its worst case, which is the
    /// un-annotated repo this is supposed to be cheap for. It is still far cheaper
    /// than the second recursive walk it avoids. An index on `attribute_name` would
    /// cut it ~14x and needs no `SchemaVersion` bump (the ctor re-runs `schema` on
    /// every open); better still, a single fused query could derive the answer from
    /// the `barriers` CTE it already scans and drop this probe altogether. Neither is
    /// done here — both are performance work, tracked separately, not a correctness
    /// concern.
    member private _.HasCompositionRoots() : bool =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        let markerNameSet = nameSetNamed "cr"

        cmd.CommandText <- $"SELECT EXISTS(SELECT 1 FROM symbol_attributes WHERE attribute_name IN (%s{markerNameSet}))"

        bindNameSetNamed "cr" cmd Domain.CompositionRoot.Names

        match cmd.ExecuteScalar() with
        | :? int64 as n -> n <> 0L
        | _ -> false

    /// Find test methods transitively depending on the given changed symbol names.
    ///
    /// Three cases, one reason each. Nothing changed: no query. No composition-root
    /// marker anywhere: one walk, the historical behaviour. Otherwise: walk it both
    /// ways and let the shared fail-safe reconcile them per test project — the
    /// unbarriered walk is what the fail-safe compares against, so it cannot be
    /// skipped. `Domain.CompositionRoot.restoreEmptiedProjects` owns that rule, and
    /// `InMemoryStore` calls the same function, so the two engines cannot drift on the
    /// one part of this feature that can silently drop a real test.
    member this.QueryAffectedTests(changedSymbolNames: string list) : TestMethodInfo list =
        let walk applyBarriers =
            this.QueryAffectedTestsCore(changedSymbolNames, applyBarriers)

        if changedSymbolNames.IsEmpty then
            []
        elif not (this.HasCompositionRoots()) then
            walk false
        else
            Domain.CompositionRoot.restoreEmptiedProjects _.TestProject (walk true) (walk false)

    /// Return every symbol occurrence declared in a given source file path, with that
    /// file's location and content hash.
    member _.GetSymbolsInFile(sourceFile: string) : SymbolInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name, s.kind, o.source_file, o.line_start, o.line_end, o.content_hash, s.is_extern
            FROM symbol_occurrences o
            JOIN symbols s ON s.id = o.symbol_id
            WHERE o.source_file = @sourceFile
            ORDER BY o.line_start
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

    /// Return every symbol occurrence, plus one `ExternSourceFile` entry per extern
    /// placeholder, ordered by file and line. A symbol declared in several files (a
    /// signature and its implementation) appears once per declaring file.
    member _.GetAllSymbols() : SymbolInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name, s.kind, o.source_file, o.line_start, o.line_end, o.content_hash, s.is_extern
            FROM symbol_occurrences o
            JOIN symbols s ON s.id = o.symbol_id
            UNION ALL
            SELECT full_name, kind, @externFile, 0, 0, '', is_extern
            FROM symbols WHERE is_extern = 1
            ORDER BY 3, 4
            """

        cmd.Parameters.AddWithValue("@externFile", ExternSourceFile) |> ignore

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

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                WITH RECURSIVE reachable AS (
                    SELECT id, full_name FROM symbols WHERE full_name IN (%s{nameSet})
                    UNION
                    SELECT s.id, s.full_name FROM symbols s
                    JOIN dependencies d ON d.to_symbol_id = s.id
                    JOIN reachable r ON r.id = d.from_symbol_id
                )
                SELECT DISTINCT full_name FROM reachable
                """

            bindNameSet cmd rootSymbolNames

            use reader = cmd.ExecuteReader()
            readAll reader (fun r -> r.GetString(0)) |> Set.ofList

    /// Get the stored cache key for a project, or None if not yet indexed.
    member this.GetProjectKey(projectName: string) : string option =
        if projectName = IndexIncompleteLookupKey then
            if this.IsIndexIncomplete() then
                Some IndexIncompleteValue
            else
                None
        elif projectName = IndexGenerationLookupKey then
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT value FROM index_metadata WHERE key = 'index_completed' LIMIT 1"
            cmd.ExecuteScalar() |> Option.ofObj |> Option.map string
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT key FROM project_keys WHERE project_name = @projectName"
            cmd.Parameters.AddWithValue("@projectName", projectName) |> ignore

            use reader = cmd.ExecuteReader()

            if reader.Read() then Some(reader.GetString(0)) else None

    /// Persist a fail-closed marker when an index attempt cannot produce a complete graph.
    member internal _.MarkIndexIncomplete() : string =
        let token = Guid.NewGuid().ToString("N")
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <- "INSERT OR REPLACE INTO index_metadata (key, value) VALUES ('index_incomplete', @value)"

        cmd.Parameters.AddWithValue("@value", token) |> ignore

        cmd.ExecuteNonQuery() |> ignore
        token

    /// Complete an index attempt only while it still owns the durable marker. A newer
    /// concurrent attempt supersedes an older one, whose success must not make selection
    /// trust a graph that the newer attempt may still be mutating.
    member internal _.CompleteIndex(token: string) : bool =
        use conn = openConnection dbPath
        use transaction = conn.BeginTransaction()
        use cmd = conn.CreateCommand()
        cmd.Transaction <- transaction

        cmd.CommandText <- "DELETE FROM index_metadata WHERE key = 'index_incomplete' AND value = @token"

        cmd.Parameters.AddWithValue("@token", token) |> ignore
        let ownsAttempt = cmd.ExecuteNonQuery() = 1

        if ownsAttempt then
            cmd.Parameters.Clear()
            cmd.CommandText <- "INSERT OR REPLACE INTO index_metadata (key, value) VALUES ('index_completed', @value)"

            cmd.Parameters.AddWithValue("@value", token) |> ignore

            cmd.ExecuteNonQuery() |> ignore

        transaction.Commit()
        ownsAttempt

    /// Whether the last index attempt failed before producing a complete graph.
    member internal this.IsIndexIncomplete() : bool =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """SELECT CASE
                 WHEN EXISTS (SELECT 1 FROM index_metadata WHERE key = 'index_incomplete')
                   OR NOT EXISTS (SELECT 1 FROM index_metadata WHERE key = 'index_completed')
                 THEN 1 ELSE 0 END"""

        cmd.ExecuteScalar() :?> int64 = 1L

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

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                SELECT DISTINCT s_to.full_name, s_from.full_name
                FROM dependencies d
                JOIN symbols s_to ON s_to.id = d.to_symbol_id
                JOIN symbols s_from ON s_from.id = d.from_symbol_id
                WHERE s_to.full_name IN (%s{nameSet})
                """

            bindNameSet cmd symbolNames

            use reader = cmd.ExecuteReader()

            readAll reader (fun r -> r.GetString(0), r.GetString(1))
            |> List.groupBy fst
            |> List.map (fun (k, vs) -> k, vs |> List.map snd)
            |> Map.ofList

    /// Get the dependency edges the given source file's analysis contributed
    /// (`AstAnalyzer.factOwner`).
    member _.GetDependenciesFromFile(sourceFile: string) : Dependency list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT f.full_name, t.full_name, d.dep_kind, d.source
            FROM dependencies d
            JOIN symbols f ON f.id = d.from_symbol_id
            JOIN symbols t ON t.id = d.to_symbol_id
            WHERE d.source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { FromSymbol = r.GetString(0)
              ToSymbol = r.GetString(1)
              Kind = stringToDepKind warnedUnknownKinds (r.GetString(2))
              Source = r.GetString(3) })

    /// Return parent containment links for every symbol in the given source file whose
    /// parent type is resolved. Used by the orchestrator's cached-file path to survive
    /// the parent_symbol_id clearance in RebuildProjects when a file wasn't re-analyzed.
    member _.GetParentLinksInFile(sourceFile: string) : SymbolParentLink list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT c.full_name, p.full_name
            FROM symbol_occurrences o
            JOIN symbols c ON c.id = o.symbol_id
            JOIN symbols p ON p.id = c.parent_symbol_id
            WHERE o.source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { Child = r.GetString(0)
              Parent = r.GetString(1) })

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
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                WITH RECURSIVE transitive_path AS (
                    SELECT from_symbol_id, source
                    FROM dependencies
                    WHERE to_symbol_id IN (
                        SELECT id FROM symbols WHERE full_name IN (%s{nameSet})
                    )
                    UNION
                    SELECT d.from_symbol_id, d.source
                    FROM dependencies d
                    JOIN transitive_path tp ON d.to_symbol_id = tp.from_symbol_id
                )
                SELECT DISTINCT source FROM transitive_path
                """

            bindNameSet cmd changedSymbolNames

            use reader = cmd.ExecuteReader()
            readAll reader (fun r -> r.GetString(0))

    /// Get all symbol attributes, grouped by symbol full name.
    member _.GetAllAttributes() : Map<string, (string * string) list> =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT DISTINCT s.full_name, sa.attribute_name, sa.args_json
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
            SELECT DISTINCT sa.attribute_name, sa.args_json
            FROM symbol_attributes sa
            JOIN symbols s ON s.id = sa.symbol_id
            WHERE s.full_name = @symbolFullName
            """

        cmd.Parameters.AddWithValue("@symbolFullName", symbolFullName) |> ignore
        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0), r.GetString(1))

    /// Get the test methods the given source file's analysis contributed.
    member _.GetTestMethodsInFile(sourceFile: string) : TestMethodInfo list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT s.full_name, tm.test_project, tm.test_class, tm.test_method
            FROM test_methods tm
            JOIN symbols s ON s.id = tm.symbol_id
            WHERE tm.source_file = @sourceFile
            """

        cmd.Parameters.AddWithValue("@sourceFile", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()

        readAll reader (fun r ->
            { SymbolFullName = r.GetString(0)
              TestProject = r.GetString(1)
              TestClass = r.GetString(2)
              TestMethod = r.GetString(3) })

    /// Find the symbol a coverage `line` belongs to in `sourceFile`: the symbol whose
    /// declaration most-recently PRECEDES the line (largest `line_start <= line`).
    ///
    /// TestPrune stores symbols as declaration-point markers (`line_end = line_start`),
    /// not body spans, so a strict `line_start <= line <= line_end` containment test only
    /// matches lines that land exactly on a declaration. Instead we attribute each line to
    /// the nearest preceding declaration — symbols tile the file, and a line belongs to the
    /// binding it falls under. A later/inner declaration has a larger `line_start`, so this
    /// naturally picks the innermost (most specific) enclosing symbol. Returns
    /// `(occurrence_id, line_start)` — the occurrence in THIS file, since a symbol declared
    /// in a signature and an implementation has one occurrence in each — or None only when
    /// the line precedes the file's first symbol.
    member _.FindSymbolContainingLine(sourceFile: string, line: int) : (int64 * int) option =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT id, line_start FROM symbol_occurrences
            WHERE source_file = @f AND line_start <= @l
            ORDER BY line_start DESC
            LIMIT 1
            """

        cmd.Parameters.AddWithValue("@f", sourceFile) |> ignore
        cmd.Parameters.AddWithValue("@l", line) |> ignore

        use reader = cmd.ExecuteReader()

        if reader.Read() then
            Some(reader.GetInt64(0), reader.GetInt32(1))
        else
            None

    /// Record `hits` for an absolute `(file, line)` coverage point. Resolves the
    /// innermost containing symbol and stores the hit symbol-relative as
    /// `(occurrence_id, line - line_start)`, max-merging with any existing value so a
    /// partial run can never lower a previously-observed hit count. If no symbol
    /// spans the line it is a no-op (per-file fallback is a later phase).
    member this.RecordCoverage(sourceFile: string, line: int, hits: int) : unit =
        match this.FindSymbolContainingLine(sourceFile, line) with
        | None -> ()
        | Some(symbolId, lineStart) ->
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                INSERT INTO coverage_points (occurrence_id, line_offset, kind, hits)
                VALUES (@s, @o, 'line', @h)
                ON CONFLICT(occurrence_id, line_offset, kind)
                DO UPDATE SET hits = MAX(hits, excluded.hits)
                """

            cmd.Parameters.AddWithValue("@s", symbolId) |> ignore
            cmd.Parameters.AddWithValue("@o", line - lineStart) |> ignore
            cmd.Parameters.AddWithValue("@h", hits) |> ignore
            cmd.ExecuteNonQuery() |> ignore

    /// Ingest many `(sourceFile, line, hits)` rows in ONE connection + transaction,
    /// reusing a single lookup command and a single upsert command across every row.
    /// This is the hot path for cobertura ingest (a report has hundreds of thousands
    /// of lines); calling `RecordCoverage` per row would instead open ~3 connections
    /// per line. Semantics are identical to `RecordCoverage`: each line attaches to its
    /// nearest preceding declaration and is max-merged symbol-relative; a line with no
    /// preceding symbol is skipped. Returns `(ingested, skipped)`.
    member _.RecordCoverageBatch(rows: (string * int * int) seq) : int * int =
        use conn = openConnection dbPath
        use txn = conn.BeginTransaction()

        use findCmd = conn.CreateCommand()
        findCmd.Transaction <- txn

        findCmd.CommandText <-
            """
            SELECT id, line_start FROM symbol_occurrences
            WHERE source_file = @f AND line_start <= @l
            ORDER BY line_start DESC
            LIMIT 1
            """

        let pFindFile = findCmd.Parameters.Add("@f", SqliteType.Text)
        let pFindLine = findCmd.Parameters.Add("@l", SqliteType.Integer)

        use insCmd = conn.CreateCommand()
        insCmd.Transaction <- txn

        insCmd.CommandText <-
            """
            INSERT INTO coverage_points (occurrence_id, line_offset, kind, hits)
            VALUES (@s, @o, 'line', @h)
            ON CONFLICT(occurrence_id, line_offset, kind)
            DO UPDATE SET hits = MAX(hits, excluded.hits)
            """

        let pInsSym = insCmd.Parameters.Add("@s", SqliteType.Integer)
        let pInsOff = insCmd.Parameters.Add("@o", SqliteType.Integer)
        let pInsHits = insCmd.Parameters.Add("@h", SqliteType.Integer)

        let mutable ingested = 0
        let mutable skipped = 0

        for (sourceFile, line, hits) in rows do
            pFindFile.Value <- sourceFile
            pFindLine.Value <- line

            let resolved =
                use reader = findCmd.ExecuteReader()

                if reader.Read() then
                    Some(reader.GetInt64(0), reader.GetInt32(1))
                else
                    None

            match resolved with
            | Some(symbolId, lineStart) ->
                pInsSym.Value <- symbolId
                pInsOff.Value <- line - lineStart
                pInsHits.Value <- hits
                insCmd.ExecuteNonQuery() |> ignore
                ingested <- ingested + 1
            | None -> skipped <- skipped + 1

        txn.Commit()
        (ingested, skipped)

    /// Durably revoke one test project's runtime evidence before a new complete
    /// receipt is parsed. Deleting both the positive map and its availability
    /// watermark in one transaction makes a crash or parse failure conservative:
    /// reopening the DB cannot resurrect stale evidence as current.
    member _.InvalidateRuntimeCoverage(testProject: string) : unit =
        use conn = openConnection dbPath
        use txn = conn.BeginTransaction()

        use coverageCmd = conn.CreateCommand()
        coverageCmd.Transaction <- txn
        coverageCmd.CommandText <- "DELETE FROM runtime_coverage WHERE test_project = @project"
        coverageCmd.Parameters.AddWithValue("@project", testProject) |> ignore
        coverageCmd.ExecuteNonQuery() |> ignore

        use baselineCmd = conn.CreateCommand()
        baselineCmd.Transaction <- txn
        baselineCmd.CommandText <- "DELETE FROM runtime_coverage_baselines WHERE test_project = @project"
        baselineCmd.Parameters.AddWithValue("@project", testProject) |> ignore
        baselineCmd.ExecuteNonQuery() |> ignore

        txn.Commit()

    /// Replace one test project's runtime file map with a complete, successful
    /// coverage baseline. Absence is meaningful only for a full run, so replacement
    /// and its baseline watermark commit in one transaction.
    member _.ReplaceRuntimeCoverage(testProject: string, runId: string, sourceFiles: string seq) : unit =
        use conn = openConnection dbPath
        use txn = conn.BeginTransaction()

        use deleteCmd = conn.CreateCommand()
        deleteCmd.Transaction <- txn
        deleteCmd.CommandText <- "DELETE FROM runtime_coverage WHERE test_project = @project"
        deleteCmd.Parameters.AddWithValue("@project", testProject) |> ignore
        deleteCmd.ExecuteNonQuery() |> ignore

        use insertCmd = conn.CreateCommand()
        insertCmd.Transaction <- txn

        insertCmd.CommandText <-
            "INSERT OR IGNORE INTO runtime_coverage (test_project, source_file) VALUES (@project, @file)"

        let projectParameter = insertCmd.Parameters.Add("@project", SqliteType.Text)
        let fileParameter = insertCmd.Parameters.Add("@file", SqliteType.Text)

        for sourceFile in sourceFiles |> Seq.distinct do
            projectParameter.Value <- testProject
            fileParameter.Value <- sourceFile
            insertCmd.ExecuteNonQuery() |> ignore

        use baselineCmd = conn.CreateCommand()
        baselineCmd.Transaction <- txn

        baselineCmd.CommandText <-
            """
            INSERT INTO runtime_coverage_baselines (test_project, run_id, observed_at)
            VALUES (@project, @run, @observedAt)
            ON CONFLICT(test_project) DO UPDATE SET
                run_id = excluded.run_id,
                observed_at = excluded.observed_at
            """

        baselineCmd.Parameters.AddWithValue("@project", testProject) |> ignore
        baselineCmd.Parameters.AddWithValue("@run", runId) |> ignore

        baselineCmd.Parameters.AddWithValue("@observedAt", DateTimeOffset.UtcNow.ToString("O"))
        |> ignore

        baselineCmd.ExecuteNonQuery() |> ignore
        txn.Commit()

    /// Add positive runtime observations from an impact-filtered run. A partial run
    /// cannot prove a file is no longer covered, so it never deletes rows or advances
    /// the complete-baseline watermark.
    member _.MergeRuntimeCoverage(testProject: string, sourceFiles: string seq) : unit =
        use conn = openConnection dbPath
        use txn = conn.BeginTransaction()
        use cmd = conn.CreateCommand()
        cmd.Transaction <- txn

        cmd.CommandText <- "INSERT OR IGNORE INTO runtime_coverage (test_project, source_file) VALUES (@project, @file)"

        let projectParameter = cmd.Parameters.Add("@project", SqliteType.Text)
        let fileParameter = cmd.Parameters.Add("@file", SqliteType.Text)

        for sourceFile in sourceFiles |> Seq.distinct do
            projectParameter.Value <- testProject
            fileParameter.Value <- sourceFile
            cmd.ExecuteNonQuery() |> ignore

        txn.Commit()

    /// Test projects with positive runtime observations for any changed file.
    member _.GetRuntimeCoverageProjects(sourceFiles: string list) : string list =
        if sourceFiles.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"SELECT DISTINCT test_project FROM runtime_coverage WHERE source_file IN (%s{nameSet}) ORDER BY test_project"

            bindNameSet cmd sourceFiles
            use reader = cmd.ExecuteReader()
            readAll reader (fun row -> row.GetString(0))

    /// Exact file/project runtime edges for the requested files. Runners use
    /// this shape to retain per-file verification debt and attribution rather
    /// than cross-producting every selected project onto every changed symbol.
    member _.GetRuntimeCoverageAttributions(sourceFiles: string list) : (string * string) list =
        if sourceFiles.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"SELECT source_file, test_project FROM runtime_coverage WHERE source_file IN (%s{nameSet}) ORDER BY source_file, test_project"

            bindNameSet cmd sourceFiles
            use reader = cmd.ExecuteReader()
            readAll reader (fun row -> row.GetString(0), row.GetString(1))

    /// Every indexed test method belonging to one of the named projects. Runtime
    /// Cobertura is project-granular, so the safe selection is the whole project.
    member _.GetTestMethodsInProjects(testProjects: string list) : TestMethodInfo list =
        if testProjects.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                SELECT s.full_name, tm.test_project, tm.test_class, tm.test_method
                FROM test_methods tm
                JOIN symbols s ON s.id = tm.symbol_id
                WHERE tm.test_project IN (%s{nameSet})
                ORDER BY tm.test_project, tm.test_class, tm.test_method
                """

            bindNameSet cmd testProjects
            use reader = cmd.ExecuteReader()

            readAll reader (fun row ->
                { SymbolFullName = row.GetString(0)
                  TestProject = row.GetString(1)
                  TestClass = row.GetString(2)
                  TestMethod = row.GetString(3) })

    /// Complete runtime-coverage baseline watermarks, ordered for deterministic
    /// diagnostics and tests.
    member _.GetRuntimeCoverageBaselines() : (string * string) list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <- "SELECT test_project, run_id FROM runtime_coverage_baselines ORDER BY test_project"

        use reader = cmd.ExecuteReader()
        readAll reader (fun row -> row.GetString(0), row.GetString(1))

    /// Expected projects whose last complete coverage baseline is absent or older
    /// than the caller's freshness boundary. Callers widen those projects rather
    /// than interpreting unavailable evidence as proof that no runtime edge exists.
    member this.GetUnavailableRuntimeCoverageProjects
        (expectedProjects: string list, staleBefore: DateTimeOffset)
        : string list =
        this.GetRuntimeCoverageAvailability(expectedProjects, staleBefore)
        |> List.choose (fun (project, availability) ->
            match availability with
            | Domain.Current -> None
            | Domain.Missing
            | Domain.Stale _ -> Some project)

    /// Availability of every expected project, retaining missing-vs-stale so the
    /// caller can explain why it widened selection.
    member _.GetRuntimeCoverageAvailability
        (expectedProjects: string list, staleBefore: DateTimeOffset)
        : (string * Domain.RuntimeCoverageAvailability) list =
        if expectedProjects.IsEmpty then
            []
        else
            use conn = openConnection dbPath
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                $"""
                SELECT test_project, observed_at
                FROM runtime_coverage_baselines
                WHERE test_project IN (%s{nameSet})
                """

            bindNameSet cmd expectedProjects
            use reader = cmd.ExecuteReader()

            let observedByProject =
                readAll reader (fun row -> row.GetString(0), DateTimeOffset.Parse(row.GetString(1)))
                |> Map.ofList

            expectedProjects
            |> List.distinct
            |> List.map (fun project ->
                match Map.tryFind project observedByProject with
                | None -> project, Domain.Missing
                | Some observedAt when observedAt < staleBefore -> project, Domain.Stale observedAt
                | Some _ -> project, Domain.Current)
            |> List.sortBy fst

    /// Get all stored line-coverage points for a source file as `(absoluteLine, hits)`.
    /// The absolute line is DERIVED from each symbol's CURRENT `line_start`, so a symbol
    /// that moved since ingest reports its coverage at the new location automatically.
    member _.GetFileCoverage(sourceFile: string) : (int * int) list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT o.line_start + cp.line_offset AS line, cp.hits
            FROM coverage_points cp
            JOIN symbol_occurrences o ON o.id = cp.occurrence_id
            WHERE o.source_file = @f AND cp.kind = 'line'
            ORDER BY line
            """

        cmd.Parameters.AddWithValue("@f", sourceFile) |> ignore

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetInt32(0), r.GetInt32(1))

    /// Return every distinct source file that has at least one stored coverage point.
    /// Used to enumerate the files to emit a current cobertura report for.
    member _.GetCoveredFiles() : string list =
        use conn = openConnection dbPath
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT DISTINCT o.source_file
            FROM symbol_occurrences o
            JOIN coverage_points cp ON cp.occurrence_id = o.id
            ORDER BY o.source_file
            """

        use reader = cmd.ExecuteReader()
        readAll reader (fun r -> r.GetString(0))
