/// The trace database: a SQLite file separate from TestPrune.Core's index, so a core
/// `SchemaVersion` bump (which deletes the index) never costs a recording run. It has its
/// own `TraceSchemaVersion` in `PRAGMA user_version` and forward-only migrations; a file
/// written by a newer schema is refused and left untouched, and no code path deletes it.
///
/// Symbols are keyed by full name and content hash, never by the index's row ids, which a
/// re-index reassigns. Content is stored per *scope* (a test, a fixture class, a
/// collection, a pool, static init): a fixture's or pool's content is stored once and each
/// test links to the scopes it inherits.
module TestPrune.Trace.TraceStore

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open TestPrune.Trace.Model

/// Whether a run executed the project's whole suite or a selection of it.
type RunKind =
    | FullRun
    | PartialRun

/// What became of a traced run of one test project.
type RunStatus =
    | Recorded
    | Refused
    | FailedToRecord
    | TreeMovedDuringRun

/// One traced run of one test project.
type TraceRun =
    { RunId: string
      TestProject: string
      TreeHash: string
      EnvFingerprint: string
      RecordedAt: DateTimeOffset
      Kind: RunKind
      Status: RunStatus
      Reason: string
      StatsJson: string }

/// One scope's content, already joined to symbols.
type ScopeContent =
    {
        Key: string
        /// (symbol full name, content hash)
        Symbols: (string * string) list
        /// (kind, key, hash); kind is read, exists, list or file-level.
        Inputs: (string * string * string) list
    }

/// A test's trace as recorded: its outcome, why it is incomplete (empty when complete),
/// and every scope it draws from.
type TestTrace =
    {
        /// "<project>|<class>|<method>"
        TestKey: string
        Status: OutcomeKind option
        /// Empty means complete.
        Reasons: IncompleteReason list
        /// The test's own T scopes plus inherited scopes; each must be in the run's scope list.
        ScopeKeys: string list
    }

/// A test's current trace under one fingerprint, read back as the union of its scopes.
type StoredTrace =
    { TestKey: string
      EnvFingerprint: string
      RunId: string
      Complete: bool
      Reasons: string list
      Symbols: Set<string * string>
      Inputs: Set<string * string * string> }

/// Raised by `Store.Open` when the file's trace schema is newer than this TestPrune.Trace
/// supports. The file is left exactly as it was found.
exception TraceSchemaNewerThanConsumer of path: string * found: int * supported: int with
    override this.Message =
        $"%s{this.path} was written by trace schema v%d{this.found}; this TestPrune.Trace supports v%d{this.supported}. "
        + "Upgrade TestPrune.Trace; the file was left untouched."

/// Forward-only. Append a new (version, DDL) pair for every change; never edit or remove a
/// published entry. The file is never deleted by code.
let internal migrations: (int * string) list =
    [ 1,
      """
      CREATE TABLE trace_runs (
          id INTEGER PRIMARY KEY,
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          tree_hash TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('full', 'partial')),
          status TEXT NOT NULL CHECK (status IN ('recorded', 'refused', 'failed', 'tree-moved')),
          reason TEXT NOT NULL DEFAULT '',
          stats_json TEXT NOT NULL DEFAULT '{}',
          UNIQUE (run_id, test_project));
      CREATE TABLE trace_scopes (
          id INTEGER PRIMARY KEY,
          trace_run_id INTEGER NOT NULL REFERENCES trace_runs(id) ON DELETE CASCADE,
          scope_key TEXT NOT NULL,
          UNIQUE (trace_run_id, scope_key));
      CREATE TABLE symbol_versions (
          id INTEGER PRIMARY KEY,
          symbol_full_name TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          UNIQUE (symbol_full_name, content_hash));
      CREATE TABLE trace_entries (
          symbol_version_id INTEGER NOT NULL REFERENCES symbol_versions(id),
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id) ON DELETE CASCADE,
          PRIMARY KEY (symbol_version_id, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_entries_by_scope ON trace_entries (scope_id);
      CREATE TABLE trace_inputs (
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id) ON DELETE CASCADE,
          kind TEXT NOT NULL,
          key TEXT NOT NULL,
          hash TEXT NOT NULL,
          PRIMARY KEY (kind, key, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_inputs_by_scope ON trace_inputs (scope_id);
      CREATE TABLE trace_tests (
          id INTEGER PRIMARY KEY,
          test_key TEXT NOT NULL,
          test_project TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          trace_run_id INTEGER NOT NULL REFERENCES trace_runs(id),
          status TEXT NOT NULL,
          complete INTEGER NOT NULL,
          incomplete_reasons TEXT NOT NULL DEFAULT '[]',
          UNIQUE (test_key, env_fingerprint));
      CREATE INDEX trace_tests_by_project ON trace_tests (test_project, env_fingerprint);
      CREATE TABLE trace_test_scopes (
          test_id INTEGER NOT NULL REFERENCES trace_tests(id) ON DELETE CASCADE,
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id),
          PRIMARY KEY (test_id, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_test_scopes_by_scope ON trace_test_scopes (scope_id);
      """ ]

/// Versions must start at 1 and increase by exactly one, so "apply every entry above the
/// file's version" is the whole migration rule. Returns the highest version.
let private checkMigrations (ms: (int * string) list) : int =
    let versions = ms |> List.map fst

    if versions.IsEmpty || versions <> List.init versions.Length ((+) 1) then
        invalidArg (nameof ms) $"trace migrations must be numbered 1, 2, 3, ... in order; got %A{versions}"

    versions.Length

/// The trace schema this TestPrune.Trace writes: the highest migration.
let TraceSchemaVersion: int = checkMigrations migrations

/// The stable text code stored for an incomplete-trace reason.
let reasonCode (reason: IncompleteReason) : string =
    let outcome =
        function
        | Passed -> "passed"
        | Failed -> "failed"
        | Skipped -> "skipped"
        | OtherOutcome -> "other"

    match reason with
    | NoOutcome -> "no-outcome"
    | NotPassed o -> $"not-passed:%s{outcome o}"
    | SourceDrift f -> $"source-drift:%s{f}"
    | NotIndexed f -> $"not-indexed:%s{f}"
    | ChildProcessUntraced f -> $"child-process-untraced:%s{f}"
    | RecorderOverflow -> "recorder-overflow"
    | TreeMoved -> "tree-moved"
    | DumpRejected why -> $"dump-rejected:%s{why}"
    | UnmappedCode detail -> $"unmapped-code:%s{detail}"

let private outcomeCode =
    function
    | Some Passed -> "passed"
    | Some Failed -> "failed"
    | Some Skipped -> "skipped"
    | Some OtherOutcome -> "other"
    | None -> "unknown"

let private runKindCode =
    function
    | FullRun -> "full"
    | PartialRun -> "partial"

let private runKindOf (code: string) =
    if code = "full" then FullRun else PartialRun

let private statusCode =
    function
    | Recorded -> "recorded"
    | Refused -> "refused"
    | FailedToRecord -> "failed"
    | TreeMovedDuringRun -> "tree-moved"

/// The table's CHECK constraint admits exactly the four codes `statusCode` writes.
let private statusOf (code: string) =
    match code with
    | "recorded" -> Recorded
    | "refused" -> Refused
    | "failed" -> FailedToRecord
    | _ -> TreeMovedDuringRun

let private command (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
    let cmd = conn.CreateCommand()
    cmd.Transaction <- tx
    cmd.CommandText <- sql

    for n, v in ps do
        cmd.Parameters.AddWithValue(n, v) |> ignore

    cmd

// `using` rather than `use` throughout: `use` compiles a null check on the disposable that
// is never null here, a branch no test can take.

let private exec conn tx sql ps =
    using (command conn tx sql ps) (fun cmd -> cmd.ExecuteNonQuery() |> ignore)

let private scalar conn tx sql ps : int64 =
    using (command conn tx sql ps) (fun cmd -> cmd.ExecuteScalar() :?> int64)

let private readRows conn sql ps (f: SqliteDataReader -> 'a) : 'a list =
    using (command conn null sql ps) (fun cmd ->
        using (cmd.ExecuteReader()) (fun r ->
            let rows = ResizeArray()

            while r.Read() do
                rows.Add(f r)

            List.ofSeq rows))

/// Run `f` in one transaction, committed only when `f` returns; an exception rolls it back.
let private inTransaction (conn: SqliteConnection) (f: SqliteTransaction -> 'a) : 'a =
    using (conn.BeginTransaction()) (fun tx ->
        let result = f tx
        tx.Commit()
        result)

/// An open trace database. Not thread-safe: one writer, used serially.
type Store private (conn: SqliteConnection) =

    /// Open (creating if absent) with an explicit migration list. `Open` is the production
    /// entry point; this seam lets a test simulate a later TestPrune.Trace.
    static member internal OpenWith(migrations: (int * string) list, path: string) : Store =
        let supported = checkMigrations migrations

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path))
        |> ignore

        let conn = new SqliteConnection($"Data Source=%s{path}")

        try
            conn.Open()
            let found = scalar conn null "PRAGMA user_version;" [] |> int

            // Refuse before any statement that could write: no pragma, no DDL.
            if found > supported then
                raise (TraceSchemaNewerThanConsumer(path, found, supported))

            exec conn null "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;" []

            // One transaction per migration: a failing step rolls back to the last good version.
            for version, ddl in migrations |> List.filter (fun (v, _) -> v > found) do
                inTransaction conn (fun tx ->
                    exec conn tx ddl []
                    exec conn tx $"PRAGMA user_version = %d{version};" [])

            new Store(conn)
        with _ ->
            SqliteConnection.ClearPool conn
            conn.Dispose()
            reraise ()

    /// Open (creating if absent) the trace database at `path`, applying any missing
    /// migrations. Raises `TraceSchemaNewerThanConsumer` for a newer file.
    static member Open(path: string) : Store = Store.OpenWith(migrations, path)

    member private _.UpsertRun(tx: SqliteTransaction, run: TraceRun) : int64 =
        exec
            conn
            tx
            """INSERT INTO trace_runs (run_id, test_project, tree_hash, env_fingerprint, recorded_at, kind, status, reason, stats_json)
               VALUES (@r, @p, @t, @e, @at, @k, @s, @why, @stats)
               ON CONFLICT (run_id, test_project) DO UPDATE SET
                   tree_hash = excluded.tree_hash, env_fingerprint = excluded.env_fingerprint,
                   recorded_at = excluded.recorded_at, kind = excluded.kind, status = excluded.status,
                   reason = excluded.reason, stats_json = excluded.stats_json"""
            [ "@r", box run.RunId
              "@p", box run.TestProject
              "@t", box run.TreeHash
              "@e", box run.EnvFingerprint
              "@at", box (run.RecordedAt.ToString "O")
              "@k", box (runKindCode run.Kind)
              "@s", box (statusCode run.Status)
              "@why", box run.Reason
              "@stats", box run.StatsJson ]

        scalar
            conn
            tx
            "SELECT id FROM trace_runs WHERE run_id = @r AND test_project = @p"
            [ "@r", box run.RunId; "@p", box run.TestProject ]

    /// Store a run that produced no traces (refused, failed, tree moved) as bookkeeping.
    member this.RecordRunWithoutTraces(run: TraceRun) : unit =
        inTransaction conn (fun tx -> this.UpsertRun(tx, run) |> ignore)

    /// One transaction: the run, its scopes with their content, and each test's trace,
    /// replacing any earlier trace of the same (test, fingerprint). Re-recording a run
    /// replaces the content of the scopes it lists. Raises `ArgumentException`, writing
    /// nothing, when a test links a scope the run does not contain.
    member this.RecordRun(run: TraceRun, scopes: ScopeContent list, tests: TestTrace list) : unit =
        inTransaction conn (fun tx ->
            let runId = this.UpsertRun(tx, run)

            let scopeId (s: ScopeContent) =
                let ps = [ "@r", box runId; "@k", box s.Key ]
                exec conn tx "INSERT OR IGNORE INTO trace_scopes (trace_run_id, scope_key) VALUES (@r, @k)" ps

                let sid =
                    scalar conn tx "SELECT id FROM trace_scopes WHERE trace_run_id = @r AND scope_key = @k" ps

                exec conn tx "DELETE FROM trace_entries WHERE scope_id = @s" [ "@s", box sid ]
                exec conn tx "DELETE FROM trace_inputs WHERE scope_id = @s" [ "@s", box sid ]

                for name, hash in List.distinct s.Symbols do
                    let ps = [ "@s", box sid; "@n", box name; "@h", box hash ]

                    exec
                        conn
                        tx
                        "INSERT OR IGNORE INTO symbol_versions (symbol_full_name, content_hash) VALUES (@n, @h)"
                        ps

                    exec
                        conn
                        tx
                        """INSERT OR IGNORE INTO trace_entries (symbol_version_id, scope_id)
                           SELECT id, @s FROM symbol_versions WHERE symbol_full_name = @n AND content_hash = @h"""
                        ps

                for kind, key, hash in List.distinct s.Inputs do
                    exec
                        conn
                        tx
                        "INSERT OR IGNORE INTO trace_inputs (scope_id, kind, key, hash) VALUES (@s, @k, @key, @h)"
                        [ "@s", box sid; "@k", box kind; "@key", box key; "@h", box hash ]

                s.Key, sid

            let scopeIds = scopes |> List.map scopeId |> Map.ofList

            for t in tests do
                exec
                    conn
                    tx
                    """INSERT INTO trace_tests (test_key, test_project, env_fingerprint, trace_run_id, status, complete, incomplete_reasons)
                       VALUES (@k, @p, @e, @r, @s, @c, @why)
                       ON CONFLICT (test_key, env_fingerprint) DO UPDATE SET
                           test_project = excluded.test_project, trace_run_id = excluded.trace_run_id,
                           status = excluded.status, complete = excluded.complete,
                           incomplete_reasons = excluded.incomplete_reasons"""
                    [ "@k", box t.TestKey
                      "@p", box run.TestProject
                      "@e", box run.EnvFingerprint
                      "@r", box runId
                      "@s", box (outcomeCode t.Status)
                      "@c", box (if List.isEmpty t.Reasons then 1 else 0)
                      "@why", box (JsonSerializer.Serialize(t.Reasons |> List.map reasonCode |> List.toArray)) ]

                let tid =
                    scalar
                        conn
                        tx
                        "SELECT id FROM trace_tests WHERE test_key = @k AND env_fingerprint = @e"
                        [ "@k", box t.TestKey; "@e", box run.EnvFingerprint ]

                exec conn tx "DELETE FROM trace_test_scopes WHERE test_id = @t" [ "@t", box tid ]

                for key in List.distinct t.ScopeKeys do
                    match scopeIds.TryFind key with
                    | Some sid ->
                        exec
                            conn
                            tx
                            "INSERT INTO trace_test_scopes (test_id, scope_id) VALUES (@t, @s)"
                            [ "@t", box tid; "@s", box sid ]
                    | None ->
                        invalidArg
                            (nameof tests)
                            $"test %s{t.TestKey} links scope %s{key}, which this run does not contain")

    /// The current trace of a test under a fingerprint: the union of its scopes' content.
    member _.TryRead(testKey: string, envFingerprint: string) : StoredTrace option =
        let head =
            readRows
                conn
                """SELECT t.id, r.run_id, t.complete, t.incomplete_reasons FROM trace_tests t
                   JOIN trace_runs r ON r.id = t.trace_run_id WHERE t.test_key = @k AND t.env_fingerprint = @e"""
                [ "@k", box testKey; "@e", box envFingerprint ]
                (fun r -> r.GetInt64 0, r.GetString 1, r.GetInt64 2 = 1L, r.GetString 3)

        match head with
        | [] -> None
        | (tid, runId, complete, reasons) :: _ ->
            let ps = [ "@t", box tid ]

            Some
                { TestKey = testKey
                  EnvFingerprint = envFingerprint
                  RunId = runId
                  Complete = complete
                  Reasons = JsonSerializer.Deserialize<string[]> reasons |> List.ofArray
                  Symbols =
                    readRows conn """SELECT v.symbol_full_name, v.content_hash FROM trace_test_scopes ts
                           JOIN trace_entries e ON e.scope_id = ts.scope_id
                           JOIN symbol_versions v ON v.id = e.symbol_version_id WHERE ts.test_id = @t""" ps (fun r ->
                        r.GetString 0, r.GetString 1)
                    |> Set.ofList
                  Inputs =
                    readRows conn """SELECT i.kind, i.key, i.hash FROM trace_test_scopes ts
                           JOIN trace_inputs i ON i.scope_id = ts.scope_id WHERE ts.test_id = @t""" ps (fun r ->
                        r.GetString 0, r.GetString 1, r.GetString 2)
                    |> Set.ofList }

    /// Every test of a project that has a trace under a fingerprint.
    member _.TestKeysOf(testProject: string, envFingerprint: string) : Set<string> =
        readRows
            conn
            "SELECT test_key FROM trace_tests WHERE test_project = @p AND env_fingerprint = @e"
            [ "@p", box testProject; "@e", box envFingerprint ]
            (fun r -> r.GetString 0)
        |> Set.ofList

    /// The scope keys stored for one run, sorted.
    member _.RunScopeKeys(runId: string, testProject: string) : string list =
        readRows
            conn
            """SELECT s.scope_key FROM trace_scopes s JOIN trace_runs r ON r.id = s.trace_run_id
               WHERE r.run_id = @r AND r.test_project = @p ORDER BY s.scope_key"""
            [ "@r", box runId; "@p", box testProject ]
            (fun r -> r.GetString 0)

    /// A project's runs, newest first.
    member _.Runs(testProject: string) : TraceRun list =
        readRows conn """SELECT run_id, tree_hash, env_fingerprint, recorded_at, kind, status, reason, stats_json
               FROM trace_runs WHERE test_project = @p ORDER BY id DESC""" [ "@p", box testProject ] (fun r ->
            { RunId = r.GetString 0
              TestProject = testProject
              TreeHash = r.GetString 1
              EnvFingerprint = r.GetString 2
              RecordedAt = DateTimeOffset.Parse(r.GetString 3, Globalization.CultureInfo.InvariantCulture)
              Kind = runKindOf (r.GetString 4)
              Status = statusOf (r.GetString 5)
              Reason = r.GetString 6
              StatsJson = r.GetString 7 })

    /// A full run passes the tests it saw as `liveTestKeys`: every other trace of the
    /// project under that fingerprint belongs to a test that no longer exists. Then drop
    /// scopes no test links (except the static-init and ambient scopes of each project's
    /// latest recorded run), runs no test or scope references (keeping the newest 50 run
    /// rows per project as history), and versions no entry references. The test-key set
    /// travels as one JSON parameter through `json_each` (ADR 0003), never as an `IN` list.
    member _.CollectGarbage(testProject: string, envFingerprint: string, liveTestKeys: Set<string> option) : unit =
        inTransaction conn (fun tx ->

            match liveTestKeys with
            | Some live ->
                exec
                    conn
                    tx
                    """DELETE FROM trace_tests WHERE test_project = @p AND env_fingerprint = @e
                         AND test_key NOT IN (SELECT value FROM json_each(@live))"""
                    [ "@p", box testProject
                      "@e", box envFingerprint
                      "@live", box (JsonSerializer.Serialize(Set.toArray live)) ]
            | None -> ()

            // Run-level scopes (static init, ambient) are linked to no test; the latest
            // recorded run of each project keeps them for the census.
            exec
                conn
                tx
                """DELETE FROM trace_scopes WHERE id NOT IN (SELECT scope_id FROM trace_test_scopes)
                     AND NOT (scope_key IN ('S:static-init', 'A:ambient') AND trace_run_id IN
                         (SELECT MAX(id) FROM trace_runs WHERE status IN ('recorded', 'tree-moved') GROUP BY test_project))"""
                []

            exec
                conn
                tx
                """DELETE FROM trace_runs WHERE test_project = @p
                     AND id NOT IN (SELECT trace_run_id FROM trace_tests)
                     AND id NOT IN (SELECT trace_run_id FROM trace_scopes)
                     AND id NOT IN (SELECT id FROM trace_runs WHERE test_project = @p ORDER BY id DESC LIMIT 50)"""
                [ "@p", box testProject ]

            exec conn tx "DELETE FROM symbol_versions WHERE id NOT IN (SELECT symbol_version_id FROM trace_entries)" [])

    interface IDisposable with
        member _.Dispose() =
            SqliteConnection.ClearPool conn
            conn.Dispose()
