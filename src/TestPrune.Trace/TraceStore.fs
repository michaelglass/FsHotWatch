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

/// What kind of run a shadow row measured.
type ShadowRunMode =
    /// The project ran a selection (`check`): the static side is what it launched.
    | ImpactRun
    /// The project ran in full (a `confirm`, a nightly, or a widened `check`): every test ran,
    /// so the trace selection's recall over this run's failures can be measured.
    | FullSuiteRun
    /// A row written before the run kind was stored (trace schema 3). It counts toward no bar.
    | ModeNotRecorded

/// Whether the fingerprint whose traces shadow read is the one the run itself recorded.
type FingerprintOutcome =
    /// Shadow read the traces of the environment the run recorded.
    | PredictionHit
    /// They differ, and nothing was stored under the fingerprint shadow read.
    | SafeMiss
    /// They differ, and shadow selected from traces of another environment.
    | UnsafeMiss
    /// The run recorded no traces for the project, so there is nothing to compare.
    | NotTraced
    /// A row written before the outcome was stored (trace schema 3).
    | OutcomeNotRecorded

/// The stored code of a fingerprint outcome.
let fingerprintOutcomeCode =
    function
    | PredictionHit -> "hit"
    | SafeMiss -> "safe-miss"
    | UnsafeMiss -> "unsafe-miss"
    | NotTraced -> "untraced"
    | OutcomeNotRecorded -> "not-recorded"

let private fingerprintOutcomeOf =
    function
    | "hit" -> PredictionHit
    | "safe-miss" -> SafeMiss
    | "unsafe-miss" -> UnsafeMiss
    | "untraced" -> NotTraced
    | _ -> OutcomeNotRecorded

let private modeCode =
    function
    | ImpactRun -> "impact"
    | FullSuiteRun -> "full"
    | ModeNotRecorded -> "not-recorded"

let private modeOf =
    function
    | "impact" -> ImpactRun
    | "full" -> FullSuiteRun
    | _ -> ModeNotRecorded

/// What a mutation sample makes throw.
type MutationKind =
    /// The probes of one symbol.
    | SymbolMutant
    /// Every probe and reflective use of one union type.
    | UnionTypeMutant

/// What became of one mutation sample.
type MutationOutcome =
    /// The mutant made at least one test fail: the sample measured something.
    | Measured
    /// No test failed.
    | NoFailure
    /// No probe id maps to the symbol.
    | NotMutable
    /// The mutated run produced no report.
    | RunFailed

/// One stored mutation sample: the tests the mutant broke that trace selection, for "this
/// symbol changed", would not have selected are its `Missed` tests.
type MutationSample =
    { TestProject: string
      Kind: MutationKind
      Symbol: string
      TreeHash: string
      RecordedAt: DateTimeOffset
      Outcome: MutationOutcome
      Failing: int
      Selected: int
      Missed: string list }

/// A mutation kind as the store and the reports name it: `symbol` or `union-type`.
let mutationKindCode =
    function
    | SymbolMutant -> "symbol"
    | UnionTypeMutant -> "union-type"

let private mutationOutcomeCode =
    function
    | Measured -> "measured"
    | NoFailure -> "no-failure"
    | NotMutable -> "not-mutable"
    | RunFailed -> "run-failed"

let private mutationOutcomeOf =
    function
    | "measured" -> Measured
    | "no-failure" -> NoFailure
    | "not-mutable" -> NotMutable
    | _ -> RunFailed

/// A fact ingestion records about a symbol, per (test project, fingerprint), that tells
/// selection whether traces can verify a change to it.
type SymbolFact =
    /// Some probe of the latest weave joins to the symbol. Replaced by every ingestion.
    | ProbedFact
    /// The symbol ran inside a type initializer whose scope no test of the run inherited, so
    /// no trace names it. Accumulated.
    | StaticInitFact
    /// The symbol appeared in or vanished from a test's trace while nothing the test ran or
    /// read changed (order or memoisation). Accumulated.
    | UnstableFact

/// The symbol facts of one project under one fingerprint.
type SymbolFacts =
    { Probed: Set<string>
      StaticInit: Set<string>
      Unstable: Set<string> }

let private factCode =
    function
    | ProbedFact -> "probed"
    | StaticInitFact -> "static-init"
    | UnstableFact -> "unstable"

/// One shadow comparison: what trace selection would have run for a test project at one
/// run's launch, next to what the static selection did run. Shadow rows never change a run.
type ShadowRow =
    {
        RunId: string
        TestProject: string
        /// The fingerprint whose traces the selection read.
        EnvFingerprint: string
        RecordedAt: DateTimeOffset
        /// Tests the selection considered.
        Universe: int
        /// Share of the universe with a complete trace under `EnvFingerprint`.
        CompleteShare: float
        StaticTests: int
        TraceTests: int
        StaticClasses: int
        TraceClasses: int
        /// Rule code ("R1".."R5") -> tests that rule selected.
        RuleCounts: Map<string, int>
        /// Selected by static, not by trace: the payoff (at most 200, sorted).
        StaticOnly: string list
        /// Selected by trace, not by static: a static-graph miss (at most 200, sorted).
        TraceOnly: string list
        SelectMs: int64
        /// Whether the project ran a selection or in full.
        Mode: ShadowRunMode
        /// The `tests.traces.record` policy the run recorded under; empty before schema 4.
        RecordPolicy: string
        /// At least 0.99 of the universe had a complete trace. `None` before schema 4.
        Eligible: bool option
        /// The fingerprint the run recorded the project's traces under, if it recorded any.
        ActualFingerprint: string option
        FingerprintOutcome: FingerprintOutcome
        /// On a full run, `r/t`: of the `t` failed tests the universe knows, `r` were in the
        /// trace selection. Otherwise `n/a: <why>`.
        TraceRecall: string
        /// The tests the static selection launches once its classes are expanded.
        StaticClassTests: int
        /// The tests the trace selection would launch once its classes are expanded.
        TraceClassTests: int
        /// Selected tests whose only reason is an incomplete trace caused by an untraced
        /// child process.
        ChildUntracedTests: int
        /// Seeds traces could not verify, by why (`TraceSelect.fallCode`): the seeds R6 walked
        /// statically. Empty before schema 5.
        FallbackSeeds: Map<string, string>
    }

// Public API: consumers catch `TraceSchemaNewerThanConsumer` by name, so a rename breaks them.
// fsharplint:disable ExceptionNames
/// Raised by `Store.Open` when the file's trace schema is newer than this TestPrune.Trace
/// supports. The file is left exactly as it was found.
exception TraceSchemaNewerThanConsumer of path: string * found: int * supported: int with
    override this.Message =
        $"%s{this.path} was written by trace schema v%d{this.found}; this TestPrune.Trace supports v%d{this.supported}. "
        + "Upgrade TestPrune.Trace; the file was left untouched."
// fsharplint:enable ExceptionNames

/// Forward-only. Append a new (version, DDL) pair for every change; never edit or remove a
/// published entry. The file is never deleted by code.
///
/// Version 3 (AUTOMATION-1490): a test's static-init scopes are one shared, per-run scope set
/// (`trace_scope_sets`, by its members), which the test names in `scope_set_id`, instead of a
/// `trace_test_scopes` row each. Nearly every test inherits nearly the same type
/// initializers, and those rows were 99% of the links and most of the file. Rows written
/// before it stay valid: a test's trace is its linked scopes plus its set's members.
///
/// Version 4 (AUTOMATION-975): a shadow row records what it measured (run kind, recording
/// policy, eligibility, the fingerprint outcome, trace recall, launched tests after class
/// expansion, tests held by untraced children), and `mutation_samples` holds the mutation
/// harness's samples. Rows written before it read back as not recorded.
///
/// Version 5 (AUTOMATION-975): `trace_symbol_facts` holds what ingestion learnt about symbols
/// traces cannot verify (probed, static-init, unstable), and a shadow row its fallback seeds.
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
      """
      2,
      """
      CREATE TABLE shadow_selections (
          id INTEGER PRIMARY KEY,
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          universe INTEGER NOT NULL,
          complete_share REAL NOT NULL,
          static_tests INTEGER NOT NULL,
          trace_tests INTEGER NOT NULL,
          static_classes INTEGER NOT NULL,
          trace_classes INTEGER NOT NULL,
          rule_counts TEXT NOT NULL,
          static_only TEXT NOT NULL,
          trace_only TEXT NOT NULL,
          select_ms INTEGER NOT NULL,
          UNIQUE (run_id, test_project));
      CREATE TABLE shadow_misses (
          id INTEGER PRIMARY KEY,
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          test_key TEXT NOT NULL,
          kind TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          UNIQUE (run_id, test_key, kind));
      CREATE INDEX shadow_misses_by_project ON shadow_misses (test_project);
      """
      3,
      """
      CREATE TABLE trace_scope_sets (
          id INTEGER PRIMARY KEY,
          trace_run_id INTEGER NOT NULL REFERENCES trace_runs(id) ON DELETE CASCADE,
          members_key TEXT NOT NULL,
          UNIQUE (trace_run_id, members_key));
      CREATE TABLE trace_scope_set_members (
          set_id INTEGER NOT NULL REFERENCES trace_scope_sets(id) ON DELETE CASCADE,
          scope_id INTEGER NOT NULL REFERENCES trace_scopes(id),
          PRIMARY KEY (set_id, scope_id)) WITHOUT ROWID;
      CREATE INDEX trace_scope_set_members_by_scope ON trace_scope_set_members (scope_id);
      ALTER TABLE trace_tests ADD COLUMN scope_set_id INTEGER REFERENCES trace_scope_sets(id);
      CREATE INDEX trace_tests_by_scope_set ON trace_tests (scope_set_id);
      """
      4,
      """
      ALTER TABLE shadow_selections ADD COLUMN mode TEXT NOT NULL DEFAULT 'not-recorded';
      ALTER TABLE shadow_selections ADD COLUMN record_policy TEXT NOT NULL DEFAULT '';
      ALTER TABLE shadow_selections ADD COLUMN eligible INTEGER;
      ALTER TABLE shadow_selections ADD COLUMN actual_fingerprint TEXT;
      ALTER TABLE shadow_selections ADD COLUMN fingerprint_outcome TEXT NOT NULL DEFAULT 'not-recorded';
      ALTER TABLE shadow_selections ADD COLUMN trace_recall TEXT NOT NULL DEFAULT 'n/a: not recorded';
      ALTER TABLE shadow_selections ADD COLUMN static_class_tests INTEGER NOT NULL DEFAULT 0;
      ALTER TABLE shadow_selections ADD COLUMN trace_class_tests INTEGER NOT NULL DEFAULT 0;
      ALTER TABLE shadow_selections ADD COLUMN child_untraced_tests INTEGER NOT NULL DEFAULT 0;
      CREATE TABLE mutation_samples (
          id INTEGER PRIMARY KEY,
          test_project TEXT NOT NULL,
          kind TEXT NOT NULL,
          symbol TEXT NOT NULL,
          tree_hash TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          outcome TEXT NOT NULL,
          failing INTEGER NOT NULL,
          selected INTEGER NOT NULL,
          missed TEXT NOT NULL);
      CREATE INDEX mutation_samples_by_project ON mutation_samples (test_project);
      """
      5,
      """
      CREATE TABLE trace_symbol_facts (
          test_project TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          fact TEXT NOT NULL,
          symbol TEXT NOT NULL,
          PRIMARY KEY (test_project, env_fingerprint, fact, symbol)) WITHOUT ROWID;
      ALTER TABLE shadow_selections ADD COLUMN fallback_seeds TEXT NOT NULL DEFAULT '{}';
      """
      6,
      """
      CREATE TABLE trace_file_gaps (
          test_project TEXT NOT NULL,
          test_name TEXT NOT NULL,
          PRIMARY KEY (test_project, test_name)) WITHOUT ROWID;
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
    | UnknownInput kind -> $"unknown-input-kind:%s{kind}"

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

/// How many commands each connection has created: the measure `Store.CommandsCreated` reads,
/// so a test can pin that a write compiles its statements once, not once per row.
let private created =
    System.Runtime.CompilerServices.ConditionalWeakTable<SqliteConnection, int64 ref>()

let private newCommand (conn: SqliteConnection) =
    // A store is used by one writer at a time (see `Store`), so a plain increment is enough.
    let count = created.GetValue(conn, fun _ -> ref 0L)
    count.Value <- count.Value + 1L
    conn.CreateCommand()

let private command (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
    let cmd = newCommand conn
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

/// A command compiled once and re-bound per row: `names` are its parameters, in the order
/// `runPrepared` and `scalarPrepared` take their values. Added to `owned`, which the caller
/// disposes.
let private prepared
    (owned: ResizeArray<SqliteCommand>)
    (conn: SqliteConnection)
    (tx: SqliteTransaction)
    (sql: string)
    (names: string list)
    =
    let cmd = newCommand conn
    owned.Add cmd
    cmd.Transaction <- tx
    cmd.CommandText <- sql

    for n in names do
        cmd.Parameters.Add(SqliteParameter(n, null)) |> ignore

    cmd.Prepare()
    cmd

let private bind (cmd: SqliteCommand) (values: obj list) =
    values |> List.iteri (fun i v -> cmd.Parameters.[i].Value <- v)

let private runPrepared (cmd: SqliteCommand) (values: obj list) =
    bind cmd values
    cmd.ExecuteNonQuery() |> ignore

let private scalarPrepared (cmd: SqliteCommand) (values: obj list) : int64 =
    bind cmd values
    cmd.ExecuteScalar() :?> int64

/// The scopes test `@t` holds: the ones linked to it, and the members of its static-init
/// scope set (schema 3).
let private scopesOfTest =
    """SELECT scope_id FROM trace_test_scopes WHERE test_id = @t
       UNION SELECT m.scope_id FROM trace_tests t
       JOIN trace_scope_set_members m ON m.set_id = t.scope_set_id WHERE t.id = @t"""

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

            exec conn null "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-65536;" []

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

    /// Commands this store's connection has created so far.
    member internal _.CommandsCreated: int64 =
        created.GetValue(conn, fun _ -> ref 0L).Value

    /// Store a run that produced no traces (refused, failed, tree moved) as bookkeeping.
    member this.RecordRunWithoutTraces(run: TraceRun) : unit =
        inTransaction conn (fun tx -> this.UpsertRun(tx, run) |> ignore)

    /// One transaction: the run, its scopes with their content, and each test's trace,
    /// replacing any earlier trace of the same (test, fingerprint). Re-recording a run
    /// replaces the content of the scopes it lists. Raises `ArgumentException`, writing
    /// nothing, when a test links a scope the run does not contain.
    ///
    /// Each statement is compiled once and re-bound per row, and each symbol version's id
    /// is looked up once per call: a full run writes millions of rows, and compiling a
    /// command per row was most of its ingestion time (AUTOMATION-1484).
    member this.RecordRun(run: TraceRun, scopes: ScopeContent list, tests: TestTrace list) : unit =
        inTransaction conn (fun tx ->
            let owned = ResizeArray<SqliteCommand>()

            try
                this.WriteRun(owned, tx, run, scopes, tests)
            finally
                for cmd in owned do
                    cmd.Dispose())

    member private this.WriteRun
        (
            owned: ResizeArray<SqliteCommand>,
            tx: SqliteTransaction,
            run: TraceRun,
            scopes: ScopeContent list,
            tests: TestTrace list
        ) : unit =
        let runId = this.UpsertRun(tx, run)

        let scopeInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO trace_scopes (trace_run_id, scope_key) VALUES (@r, @k)"
                [ "@r"; "@k" ]

        let scopeSelect =
            prepared
                owned
                conn
                tx
                "SELECT id FROM trace_scopes WHERE trace_run_id = @r AND scope_key = @k"
                [ "@r"; "@k" ]

        let entriesClear =
            prepared owned conn tx "DELETE FROM trace_entries WHERE scope_id = @s" [ "@s" ]

        let inputsClear =
            prepared owned conn tx "DELETE FROM trace_inputs WHERE scope_id = @s" [ "@s" ]

        let versionInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO symbol_versions (symbol_full_name, content_hash) VALUES (@n, @h)"
                [ "@n"; "@h" ]

        let versionSelect =
            prepared
                owned
                conn
                tx
                "SELECT id FROM symbol_versions WHERE symbol_full_name = @n AND content_hash = @h"
                [ "@n"; "@h" ]

        let entryInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO trace_entries (symbol_version_id, scope_id) VALUES (@v, @s)"
                [ "@v"; "@s" ]

        let inputInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO trace_inputs (scope_id, kind, key, hash) VALUES (@s, @k, @key, @h)"
                [ "@s"; "@k"; "@key"; "@h" ]

        let testUpsert =
            prepared
                owned
                conn
                tx
                """INSERT INTO trace_tests (test_key, test_project, env_fingerprint, trace_run_id, status, complete, incomplete_reasons, scope_set_id)
                   VALUES (@k, @p, @e, @r, @s, @c, @why, @set)
                   ON CONFLICT (test_key, env_fingerprint) DO UPDATE SET
                       test_project = excluded.test_project, trace_run_id = excluded.trace_run_id,
                       status = excluded.status, complete = excluded.complete,
                       incomplete_reasons = excluded.incomplete_reasons, scope_set_id = excluded.scope_set_id"""
                [ "@k"; "@p"; "@e"; "@r"; "@s"; "@c"; "@why"; "@set" ]

        let testSelect =
            prepared
                owned
                conn
                tx
                "SELECT id FROM trace_tests WHERE test_key = @k AND env_fingerprint = @e"
                [ "@k"; "@e" ]

        let linksClear =
            prepared owned conn tx "DELETE FROM trace_test_scopes WHERE test_id = @t" [ "@t" ]

        let linkInsert =
            prepared owned conn tx "INSERT INTO trace_test_scopes (test_id, scope_id) VALUES (@t, @s)" [ "@t"; "@s" ]

        let setInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO trace_scope_sets (trace_run_id, members_key) VALUES (@r, @m)"
                [ "@r"; "@m" ]

        let setSelect =
            prepared
                owned
                conn
                tx
                "SELECT id FROM trace_scope_sets WHERE trace_run_id = @r AND members_key = @m"
                [ "@r"; "@m" ]

        let memberInsert =
            prepared
                owned
                conn
                tx
                "INSERT OR IGNORE INTO trace_scope_set_members (set_id, scope_id) VALUES (@set, @s)"
                [ "@set"; "@s" ]

        // A version recurs in most scopes of a run: look its id up once.
        let versionIds = Collections.Generic.Dictionary<struct (string * string), int64>()

        let versionId (name: string) (hash: string) =
            match versionIds.TryGetValue(struct (name, hash)) with
            | true, id -> id
            | _ ->
                runPrepared versionInsert [ box name; box hash ]
                let id = scalarPrepared versionSelect [ box name; box hash ]
                versionIds.[struct (name, hash)] <- id
                id

        let scopeId (s: ScopeContent) =
            runPrepared scopeInsert [ box runId; box s.Key ]
            let sid = scalarPrepared scopeSelect [ box runId; box s.Key ]
            runPrepared entriesClear [ box sid ]
            runPrepared inputsClear [ box sid ]

            for name, hash in List.distinct s.Symbols do
                runPrepared entryInsert [ box (versionId name hash); box sid ]

            for kind, key, hash in List.distinct s.Inputs do
                runPrepared inputInsert [ box sid; box kind; box key; box hash ]

            s.Key, sid

        let scopeIds = scopes |> List.map scopeId |> Map.ofList

        // A static-init scope set is stored once per run, keyed by its members: most tests of
        // a run inherit one of a few such sets.
        let setIds = Collections.Generic.Dictionary<string, int64>()

        let setId (members: (string * int64) list) =
            let membersKey =
                members
                |> List.map fst
                |> List.sort
                |> String.concat "\n"
                |> Text.Encoding.UTF8.GetBytes
                |> Security.Cryptography.SHA256.HashData
                |> Convert.ToHexString

            match setIds.TryGetValue membersKey with
            | true, id -> id
            | _ ->
                runPrepared setInsert [ box runId; box membersKey ]
                let id = scalarPrepared setSelect [ box runId; box membersKey ]

                for _, sid in members do
                    runPrepared memberInsert [ box id; box sid ]

                setIds.[membersKey] <- id
                id

        for t in tests do
            let linked =
                t.ScopeKeys
                |> List.distinct
                |> List.map (fun key ->
                    match scopeIds.TryFind key with
                    | Some sid -> key, sid
                    | None ->
                        invalidArg
                            (nameof tests)
                            $"test %s{t.TestKey} links scope %s{key}, which this run does not contain")

            let statics, own = linked |> List.partition (fst >> RunScopes.isStaticInit)

            let scopeSet =
                match statics with
                | [] -> box DBNull.Value
                | _ -> box (setId statics)

            runPrepared
                testUpsert
                [ box t.TestKey
                  box run.TestProject
                  box run.EnvFingerprint
                  box runId
                  box (outcomeCode t.Status)
                  box (if List.isEmpty t.Reasons then 1 else 0)
                  box (JsonSerializer.Serialize(t.Reasons |> List.map reasonCode |> List.toArray))
                  scopeSet ]

            let tid = scalarPrepared testSelect [ box t.TestKey; box run.EnvFingerprint ]
            runPrepared linksClear [ box tid ]

            for _, sid in own do
                runPrepared linkInsert [ box tid; box sid ]

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
                    readRows conn $"""SELECT v.symbol_full_name, v.content_hash FROM (%s{scopesOfTest}) ts
                           JOIN trace_entries e ON e.scope_id = ts.scope_id
                           JOIN symbol_versions v ON v.id = e.symbol_version_id""" ps (fun r ->
                        r.GetString 0, r.GetString 1)
                    |> Set.ofList
                  Inputs =
                    readRows conn $"""SELECT i.kind, i.key, i.hash FROM (%s{scopesOfTest}) ts
                           JOIN trace_inputs i ON i.scope_id = ts.scope_id""" ps (fun r ->
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

    /// Every test project that has a run, sorted.
    member _.TestProjects() : string list =
        readRows conn "SELECT DISTINCT test_project FROM trace_runs ORDER BY test_project" [] (fun r -> r.GetString 0)

    /// Store one shadow comparison, replacing an earlier row of the same (run, project).
    member _.RecordShadow(row: ShadowRow) : unit =
        let rules =
            row.RuleCounts
            |> Map.toList
            |> List.map (fun (code, n) -> $"%s{code}:%d{n}")
            |> String.concat ","

        inTransaction conn (fun tx ->
            exec
                conn
                tx
                """INSERT INTO shadow_selections (run_id, test_project, env_fingerprint, recorded_at, universe,
                       complete_share, static_tests, trace_tests, static_classes, trace_classes, rule_counts,
                       static_only, trace_only, select_ms, mode, record_policy, eligible, actual_fingerprint,
                       fingerprint_outcome, trace_recall, static_class_tests, trace_class_tests, child_untraced_tests, fallback_seeds)
                   VALUES (@r, @p, @e, @at, @u, @cs, @st, @tt, @sc, @tc, @rules, @so, @to, @ms, @mode, @rp, @el, @af,
                       @fo, @rec, @sct, @tct, @cut, @fs)
                   ON CONFLICT (run_id, test_project) DO UPDATE SET
                       env_fingerprint = excluded.env_fingerprint, recorded_at = excluded.recorded_at,
                       universe = excluded.universe, complete_share = excluded.complete_share,
                       static_tests = excluded.static_tests, trace_tests = excluded.trace_tests,
                       static_classes = excluded.static_classes, trace_classes = excluded.trace_classes,
                       rule_counts = excluded.rule_counts, static_only = excluded.static_only,
                       trace_only = excluded.trace_only, select_ms = excluded.select_ms, mode = excluded.mode,
                       record_policy = excluded.record_policy, eligible = excluded.eligible,
                       actual_fingerprint = excluded.actual_fingerprint,
                       fingerprint_outcome = excluded.fingerprint_outcome, trace_recall = excluded.trace_recall,
                       static_class_tests = excluded.static_class_tests, trace_class_tests = excluded.trace_class_tests,
                       child_untraced_tests = excluded.child_untraced_tests, fallback_seeds = excluded.fallback_seeds"""
                [ "@r", box row.RunId
                  "@p", box row.TestProject
                  "@e", box row.EnvFingerprint
                  "@at", box (row.RecordedAt.ToString "O")
                  "@u", box row.Universe
                  "@cs", box row.CompleteShare
                  "@st", box row.StaticTests
                  "@tt", box row.TraceTests
                  "@sc", box row.StaticClasses
                  "@tc", box row.TraceClasses
                  "@rules", box rules
                  "@so", box (JsonSerializer.Serialize(Array.ofList row.StaticOnly))
                  "@to", box (JsonSerializer.Serialize(Array.ofList row.TraceOnly))
                  "@ms", box row.SelectMs
                  "@mode", box (modeCode row.Mode)
                  "@rp", box row.RecordPolicy
                  "@el",
                  (match row.Eligible with
                   | Some e -> box (if e then 1 else 0)
                   | None -> box DBNull.Value)
                  "@af",
                  (match row.ActualFingerprint with
                   | Some fp -> box fp
                   | None -> box DBNull.Value)
                  "@fo", box (fingerprintOutcomeCode row.FingerprintOutcome)
                  "@rec", box row.TraceRecall
                  "@sct", box row.StaticClassTests
                  "@tct", box row.TraceClassTests
                  "@cut", box row.ChildUntracedTests
                  "@fs", box (JsonSerializer.Serialize(row.FallbackSeeds)) ])

    /// A project's shadow rows, oldest first.
    member _.ShadowRows(testProject: string) : ShadowRow list =
        let rulesOf (text: string) =
            text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun pair ->
                let i = pair.LastIndexOf ':'
                pair.Substring(0, i), int (pair.Substring(i + 1)))
            |> Map.ofArray

        let listOf (json: string) =
            JsonSerializer.Deserialize<string[]> json |> List.ofArray

        readRows conn """SELECT run_id, env_fingerprint, recorded_at, universe, complete_share, static_tests, trace_tests,
                      static_classes, trace_classes, rule_counts, static_only, trace_only, select_ms, mode, record_policy,
                      eligible, actual_fingerprint, fingerprint_outcome, trace_recall, static_class_tests,
                      trace_class_tests, child_untraced_tests, fallback_seeds
               FROM shadow_selections WHERE test_project = @p ORDER BY id""" [ "@p", box testProject ] (fun r ->
            { RunId = r.GetString 0
              TestProject = testProject
              EnvFingerprint = r.GetString 1
              RecordedAt = DateTimeOffset.Parse(r.GetString 2, Globalization.CultureInfo.InvariantCulture)
              Universe = r.GetInt32 3
              CompleteShare = r.GetDouble 4
              StaticTests = r.GetInt32 5
              TraceTests = r.GetInt32 6
              StaticClasses = r.GetInt32 7
              TraceClasses = r.GetInt32 8
              RuleCounts = rulesOf (r.GetString 9)
              StaticOnly = listOf (r.GetString 10)
              TraceOnly = listOf (r.GetString 11)
              SelectMs = r.GetInt64 12
              Mode = modeOf (r.GetString 13)
              RecordPolicy = r.GetString 14
              Eligible = (if r.IsDBNull 15 then None else Some(r.GetInt64 15 <> 0L))
              ActualFingerprint = (if r.IsDBNull 16 then None else Some(r.GetString 16))
              FingerprintOutcome = fingerprintOutcomeOf (r.GetString 17)
              TraceRecall = r.GetString 18
              StaticClassTests = r.GetInt32 19
              TraceClassTests = r.GetInt32 20
              ChildUntracedTests = r.GetInt32 21
              FallbackSeeds =
                JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(r.GetString 22)
                |> Seq.map (fun kv -> kv.Key, kv.Value)
                |> Map.ofSeq })

    /// Store tests that failed in a run while trace selection, computed at that run's
    /// launch, did not select them: verified-failure misses. A repeat is ignored.
    member _.RecordShadowMisses(runId: string, testProject: string, at: DateTimeOffset, testKeys: string list) =
        inTransaction conn (fun tx ->
            for key in testKeys do
                exec
                    conn
                    tx
                    """INSERT INTO shadow_misses (run_id, test_project, test_key, kind, recorded_at)
                       VALUES (@r, @p, @k, 'verified-failure', @at) ON CONFLICT DO NOTHING"""
                    [ "@r", box runId
                      "@p", box testProject
                      "@k", box key
                      "@at", box (at.ToString "O") ])

    /// A project's verified-failure misses as (run, test key), oldest first.
    member _.ShadowMisses(testProject: string) : (string * string) list =
        readRows
            conn
            "SELECT run_id, test_key FROM shadow_misses WHERE test_project = @p ORDER BY id"
            [ "@p", box testProject ]
            (fun r -> r.GetString 0, r.GetString 1)

    /// Replace a project's file gaps with `names` (`FileCensus.gaps`, normalised `Class.Method`
    /// names): the latest census is the answer, so an older one is dropped whole.
    member _.RecordFileGaps(testProject: string, names: string list) : unit =
        inTransaction conn (fun tx ->
            exec conn tx "DELETE FROM trace_file_gaps WHERE test_project = @p" [ "@p", box testProject ]

            for name in List.distinct names do
                exec
                    conn
                    tx
                    "INSERT INTO trace_file_gaps (test_project, test_name) VALUES (@p, @n)"
                    [ "@p", box testProject; "@n", box name ])

    /// A project's file gaps: the tests the last census found reading a file no trace records.
    member _.FileGaps(testProject: string) : Set<string> =
        readRows
            conn
            "SELECT test_name FROM trace_file_gaps WHERE test_project = @p"
            [ "@p", box testProject ]
            (fun r -> r.GetString 0)
        |> Set.ofList

    /// Store one mutation sample. Every sample is kept: two samples of one symbol at two
    /// trees are two measurements.
    member _.RecordMutationSample(sample: MutationSample) : unit =
        inTransaction conn (fun tx ->
            exec
                conn
                tx
                """INSERT INTO mutation_samples (test_project, kind, symbol, tree_hash, recorded_at, outcome, failing,
                       selected, missed)
                   VALUES (@p, @k, @s, @t, @at, @o, @f, @sel, @m)"""
                [ "@p", box sample.TestProject
                  "@k", box (mutationKindCode sample.Kind)
                  "@s", box sample.Symbol
                  "@t", box sample.TreeHash
                  "@at", box (sample.RecordedAt.ToString "O")
                  "@o", box (mutationOutcomeCode sample.Outcome)
                  "@f", box sample.Failing
                  "@sel", box sample.Selected
                  "@m", box (JsonSerializer.Serialize(Array.ofList sample.Missed)) ])

    /// A project's mutation samples, oldest first.
    member _.MutationSamples(testProject: string) : MutationSample list =
        readRows conn """SELECT kind, symbol, tree_hash, recorded_at, outcome, failing, selected, missed
               FROM mutation_samples WHERE test_project = @p ORDER BY id""" [ "@p", box testProject ] (fun r ->
            { TestProject = testProject
              Kind =
                (if r.GetString 0 = "union-type" then
                     UnionTypeMutant
                 else
                     SymbolMutant)
              Symbol = r.GetString 1
              TreeHash = r.GetString 2
              RecordedAt = DateTimeOffset.Parse(r.GetString 3, Globalization.CultureInfo.InvariantCulture)
              Outcome = mutationOutcomeOf (r.GetString 4)
              Failing = r.GetInt32 5
              Selected = r.GetInt32 6
              Missed = JsonSerializer.Deserialize<string[]>(r.GetString 7) |> List.ofArray })

    /// Record `fact` for `symbols` of a project under a fingerprint. `ProbedFact` replaces the
    /// earlier set (it describes the latest weave); the others accumulate. The set travels as
    /// one JSON parameter through `json_each` (ADR 0003).
    member _.RecordSymbolFacts(testProject: string, envFingerprint: string, fact: SymbolFact, symbols: string list) =
        inTransaction conn (fun tx ->
            let ps =
                [ "@p", box testProject; "@e", box envFingerprint; "@f", box (factCode fact) ]

            if fact = ProbedFact then
                exec
                    conn
                    tx
                    "DELETE FROM trace_symbol_facts WHERE test_project = @p AND env_fingerprint = @e AND fact = @f"
                    ps

            exec
                conn
                tx
                """INSERT OR IGNORE INTO trace_symbol_facts (test_project, env_fingerprint, fact, symbol)
                   SELECT @p, @e, @f, value FROM json_each(@s)"""
                (ps @ [ "@s", box (JsonSerializer.Serialize(Array.ofList symbols)) ]))

    /// A project's symbol facts under a fingerprint.
    member _.SymbolFacts(testProject: string, envFingerprint: string) : SymbolFacts =
        let rows =
            readRows
                conn
                "SELECT fact, symbol FROM trace_symbol_facts WHERE test_project = @p AND env_fingerprint = @e"
                [ "@p", box testProject; "@e", box envFingerprint ]
                (fun r -> r.GetString 0, r.GetString 1)

        let of' fact =
            rows
            |> List.filter (fun (f, _) -> f = factCode fact)
            |> List.map snd
            |> Set.ofList

        { Probed = of' ProbedFact
          StaticInit = of' StaticInitFact
          Unstable = of' UnstableFact }

    /// The distinct (kind, key) inputs of a project's scopes under a fingerprint, sorted: the
    /// files a changed-file check can leave to R3.
    member _.ClaimedInputs(testProject: string, envFingerprint: string) : (string * string) list =
        readRows conn """SELECT DISTINCT i.kind, i.key FROM trace_inputs i
               JOIN trace_scopes s ON s.id = i.scope_id
               JOIN trace_runs r ON r.id = s.trace_run_id
               WHERE r.test_project = @p AND r.env_fingerprint = @e
               ORDER BY i.kind, i.key""" [ "@p", box testProject; "@e", box envFingerprint ] (fun r ->
            r.GetString 0, r.GetString 1)

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
    /// static-init scope sets no test names, scopes no test links or holds through a set (except the static-init and ambient scopes of each project's
    /// latest recorded run), runs no test, scope or set references (keeping the newest 50 run
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

            // A static-init scope set no test names any more goes first, its members with it.
            exec
                conn
                tx
                """DELETE FROM trace_scope_sets
                     WHERE id NOT IN (SELECT scope_set_id FROM trace_tests WHERE scope_set_id IS NOT NULL)"""
                []

            // Run-level scopes (static init no test inherited, ambient) are linked to no test;
            // the latest recorded run of each project keeps them for the census.
            exec
                conn
                tx
                """DELETE FROM trace_scopes WHERE id NOT IN (SELECT scope_id FROM trace_test_scopes)
                     AND id NOT IN (SELECT scope_id FROM trace_scope_set_members)
                     AND NOT ((scope_key LIKE 'S:%' OR scope_key = 'A:ambient') AND trace_run_id IN
                         (SELECT MAX(id) FROM trace_runs WHERE status IN ('recorded', 'tree-moved') GROUP BY test_project))"""
                []

            exec
                conn
                tx
                """DELETE FROM trace_runs WHERE test_project = @p
                     AND id NOT IN (SELECT trace_run_id FROM trace_tests)
                     AND id NOT IN (SELECT trace_run_id FROM trace_scopes)
                     AND id NOT IN (SELECT trace_run_id FROM trace_scope_sets)
                     AND id NOT IN (SELECT id FROM trace_runs WHERE test_project = @p ORDER BY id DESC LIMIT 50)"""
                [ "@p", box testProject ]

            exec conn tx "DELETE FROM symbol_versions WHERE id NOT IN (SELECT symbol_version_id FROM trace_entries)" [])

    interface IDisposable with
        member _.Dispose() =
            SqliteConnection.ClearPool conn
            conn.Dispose()
