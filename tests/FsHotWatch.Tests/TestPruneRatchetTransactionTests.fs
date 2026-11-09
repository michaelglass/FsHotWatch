/// The per-project ratchet maps covered lines to symbol occurrence ids and then writes
/// those ids into a table whose `occurrence_id` is a foreign key into
/// `symbol_occurrences`. The mapping and the write must be one transaction: a graph
/// rebuild that deletes the occurrence in between turns the write into SQLite error 19
/// (FOREIGN KEY constraint failed).
module FsHotWatch.Tests.TestPruneRatchetTransactionTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.IndexMaintenance
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database

/// What a graph rebuild's orphan removal does to a declaration that vanished from its file.
[<Literal>]
let private DeleteCoveredOccurrence =
    "DELETE FROM symbol_occurrences WHERE symbol_id = (SELECT id FROM symbols WHERE full_name = 'Fixture.covered');"

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``ratchet mapping retains symbol ownership until its coverage write commits`` (partial: bool) =
    withTempDir "ratchet-map-transaction" (fun directory ->
        let db = Database.create (Path.Combine(directory, "impact.db"))

        let symbol: SymbolInfo =
            { FullName = "Fixture.covered"
              Kind = SymbolKind.Value
              Location = InSource("src/Fixture.fs", 10, 10, "original") }

        db.RebuildProjects([ AnalysisResult.Create([ symbol ], [], []) ])

        let input: CoverageInput =
            { Project = "Fixture.Tests"
              RawPath = Path.Combine(directory, "coverage.xml")
              IncludeInRatchet = true
              Scope =
                if partial then
                    CoverageRunScope.Partial
                else
                    CoverageRunScope.Full }

        let xml =
            """<coverage><packages><package name="Fixture"><classes><class name="Fixture" filename="src/Fixture.fs"><lines><line number="10" hits="7" /></lines></class></classes></package></packages></coverage>"""

        let mutable attemptedRemoval = false
        let mutable removalBlocked = false

        let removeMappedSymbol () =
            // A separate real connection plays the graph rebuild's orphan removal. The
            // seam runs it AFTER the line-to-symbol lookup and BEFORE the write, so the
            // interleaving is deterministic: no scheduler race and no sleep.
            attemptedRemoval <- true
            use contender = db.OpenConnection()
            use remove = contender.CreateCommand()
            // Microsoft.Data.Sqlite reads zero as "wait forever", not "fail now". Bound
            // the genuine lock wait while the writer owns the mapping.
            remove.CommandTimeout <- 1
            remove.CommandText <- DeleteCoveredOccurrence

            try
                Assert.Equal(1, remove.ExecuteNonQuery())
            with :? SqliteException as failure when failure.SqliteErrorCode = 5 || failure.SqliteErrorCode = 6 ->
                removalBlocked <- true

        let outcome =
            try
                persistProjectRatchetCoverageWithMapped removeMappedSymbol db directory input xml
                Ok()
            with :? SqliteException as failure ->
                Error failure

        Assert.True(attemptedRemoval, "the competing graph mutation must actually reach the mapped-symbol boundary")

        match outcome with
        | Error failure ->
            Assert.Fail(
                $"ratchet mapping lost its symbol before persistence: SQLite {failure.SqliteErrorCode}/{failure.SqliteExtendedErrorCode}: {failure.Message}"
            )
        | Ok() -> ()

        Assert.True(removalBlocked, "the coverage transaction must exclude symbol deletion until its write commits")
        use inspection = db.OpenConnection()
        use coverage = inspection.CreateCommand()

        coverage.CommandText <-
            """SELECT COUNT(*) FROM fshw_project_ratchet_coverage c
               JOIN symbol_occurrences o ON o.id = c.occurrence_id
               JOIN symbols s ON s.id = o.symbol_id
               WHERE c.project = 'Fixture.Tests' AND s.full_name = 'Fixture.covered'
                 AND c.line_offset = 0 AND c.hits = 7;"""

        Assert.Equal(1L, Convert.ToInt64(coverage.ExecuteScalar()))

        // The exclusion lasts only for the transaction. The waiting graph mutation may
        // proceed afterwards, and the ordinary FK cascade removes the obsolete points.
        use removeAfterCommit = inspection.CreateCommand()
        removeAfterCommit.CommandText <- DeleteCoveredOccurrence
        Assert.Equal(1, removeAfterCommit.ExecuteNonQuery())
        Assert.Equal(0L, Convert.ToInt64(coverage.ExecuteScalar())))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a ratchet table left in its symbol-keyed shape by an older fshw is rebuilt, not aborted on`` () =
    // AUTOMATION-1434: a TestPrune index rebuild keeps every non-core table, so a DB an
    // older fshw wrote kept `fshw_project_ratchet_coverage` keyed on `symbol_id`, with no
    // `occurrence_id`, and its metadata still said schema 1. `CREATE TABLE IF NOT EXISTS`
    // left it alone, and every run then aborted on "no such column: occurrence_id".
    withTempDir "ratchet-old-shape" (fun directory ->
        let db = Database.create (Path.Combine(directory, "impact.db"))

        let symbol: SymbolInfo =
            { FullName = "Fixture.covered"
              Kind = SymbolKind.Value
              Location = InSource("src/Fixture.fs", 10, 10, "original") }

        db.RebuildProjects([ AnalysisResult.Create([ symbol ], [], []) ])

        do
            use conn = db.OpenConnection()
            use old = conn.CreateCommand()

            old.CommandText <-
                """CREATE TABLE fshw_project_ratchet_coverage (
                       project TEXT NOT NULL,
                       symbol_id INTEGER NOT NULL,
                       line_offset INTEGER NOT NULL,
                       hits INTEGER NOT NULL DEFAULT 0,
                       PRIMARY KEY (project, symbol_id, line_offset));
                   CREATE INDEX idx_fshw_project_ratchet_coverage_symbol ON fshw_project_ratchet_coverage (symbol_id);
                   INSERT INTO fshw_project_ratchet_coverage VALUES ('Fixture.Tests', 1, 0, 3);
                   CREATE TABLE fshw_project_ratchet_metadata (
                       singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                       schema_version INTEGER NOT NULL,
                       legacy_output_invalidated INTEGER NOT NULL CHECK (legacy_output_invalidated IN (0, 1)));
                   INSERT INTO fshw_project_ratchet_metadata VALUES (1, 1, 1);"""

            old.ExecuteNonQuery() |> ignore

        let raw = Path.Combine(directory, "coverage.xml")

        File.WriteAllText(
            raw,
            """<coverage><packages><package name="Fixture"><classes><class name="Fixture" filename="src/Fixture.fs"><lines><line number="10" hits="7" /></lines></class></classes></package></packages></coverage>"""
        )

        let input: CoverageInput =
            { Project = "Fixture.Tests"
              RawPath = raw
              IncludeInRatchet = true
              Scope = CoverageRunScope.Full }

        let output = Path.Combine(directory, "out", "coverage.cobertura.xml")

        let outcome =
            ingestAndEmitCoverageForProjects
                db
                directory
                "run-1"
                (Set.ofList [ "Fixture.Tests" ])
                (Some output)
                [ input ]

        Assert.Empty outcome.Failures
        use inspection = db.OpenConnection()
        use coverage = inspection.CreateCommand()

        coverage.CommandText <-
            """SELECT COUNT(*) FROM fshw_project_ratchet_coverage c
               JOIN symbol_occurrences o ON o.id = c.occurrence_id
               WHERE c.project = 'Fixture.Tests' AND c.line_offset = 0 AND c.hits = 7;"""

        Assert.Equal(1L, Convert.ToInt64(coverage.ExecuteScalar()))
        use columns = inspection.CreateCommand()

        columns.CommandText <-
            "SELECT COUNT(*) FROM pragma_table_info('fshw_project_ratchet_coverage') WHERE name = 'symbol_id';"

        Assert.Equal(0L, Convert.ToInt64(columns.ExecuteScalar())))

[<Fact>]
let ``a run that aborts on a database schema error names the schema as the cause, not load`` () =
    // AUTOMATION-1434: the old-shape abort read as "uncompletable — re-run once there is
    // headroom". A schema mismatch aborts again on every re-run, quiet box or not.
    withTempDir "schema-abort" (fun directory ->
        use conn =
            new SqliteConnection($"""Data Source=%s{Path.Combine(directory, "impact.db")};Pooling=False""")

        conn.Open()
        use make = conn.CreateCommand()
        make.CommandText <- "CREATE TABLE fshw_project_ratchet_coverage (symbol_id INTEGER);"
        make.ExecuteNonQuery() |> ignore
        use query = conn.CreateCommand()
        query.CommandText <- "SELECT occurrence_id FROM fshw_project_ratchet_coverage;"

        let schemaError =
            Assert.Throws<SqliteException>(fun () -> query.ExecuteScalar() |> ignore)

        let reason =
            FsHotWatch.TestPrune.RunSupport.abortReasonOf (AggregateException(schemaError))

        Assert.Contains("no such column: occurrence_id", reason)
        Assert.Contains("schema", reason)
        Assert.Contains("not load", reason)
        // Any other failure, a database one included, keeps its own message.
        use broken = conn.CreateCommand()
        broken.CommandText <- "SELECT FROM;"

        let syntaxError =
            Assert.Throws<SqliteException>(fun () -> broken.ExecuteScalar() |> ignore)

        Assert.Equal(syntaxError.Message, FsHotWatch.TestPrune.RunSupport.abortReasonOf syntaxError)
        Assert.Equal("boom", FsHotWatch.TestPrune.RunSupport.abortReasonOf (InvalidOperationException "boom")))

// AUTOMATION-784: the mapping and the write are one IMMEDIATE transaction, so the writer now
// queues for the write lock rather than losing an id to a rebuild. A cold indexer flush
// holds that lock in one transaction (58s for 2523 files in a measured run, on a loaded
// box), so a writer bounded at SQLite's 30s default failed with `database is locked`
// exactly while the scenario this ticket is about was running.

/// A coverage input and report that map to one indexed symbol in `directory`.
let private ratchetInput (directory: string) (db: Database) =
    let symbol: SymbolInfo =
        { FullName = "Fixture.covered"
          Kind = SymbolKind.Value
          Location = InSource("src/Fixture.fs", 10, 10, "original") }

    db.RebuildProjects([ AnalysisResult.Create([ symbol ], [], []) ])

    let input: CoverageInput =
        { Project = "Fixture.Tests"
          RawPath = Path.Combine(directory, "coverage.xml")
          IncludeInRatchet = true
          Scope = CoverageRunScope.Full }

    let xml =
        """<coverage><packages><package name="Fixture"><classes><class name="Fixture" filename="src/Fixture.fs"><lines><line number="10" hits="7" /></lines></class></classes></package></packages></coverage>"""

    input, xml

/// A separate connection that takes the write lock the way an indexer flush does, and holds
/// it until the returned action releases it.
let private holdWriteLock (dbPath: string) =
    let contender = new SqliteConnection($"Data Source=%s{dbPath}")
    contender.Open()
    use begin' = contender.CreateCommand()
    begin'.CommandText <- "BEGIN IMMEDIATE;"
    begin'.ExecuteNonQuery() |> ignore

    fun () ->
        use rollback = contender.CreateCommand()
        rollback.CommandText <- "ROLLBACK;"
        rollback.ExecuteNonQuery() |> ignore
        contender.Dispose()

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the daemon's test-impact DB waits out an indexer flush that the library's 30s default would have refused`` () =
    withTempDir "ratchet-busy-bound" (fun directory ->
        let db =
            openOwnIndex (Path.Combine(directory, "impact.db")) TestPrune.Domain.WalkPolicy.FollowAll

        Assert.True(
            dbBusyTimeout db >= TimeSpan.FromMinutes 2.0,
            $"the daemon's own write lock waits are bounded at {dbBusyTimeout db}, shorter than the longest measured indexer flush"
        ))

[<WallClock("simulated work: a held write lock is the subject; the outcome is awaited, and the sleep only gives the holder a duration inside the writer's bound")>]
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a coverage write queued behind an indexer's write lock persists once the indexer finishes`` () =
    withTempDir "ratchet-busy-wait" (fun directory ->
        let dbPath = Path.Combine(directory, "impact.db")

        let db =
            Database.create (dbPath, TestPrune.Domain.WalkPolicy.FollowAll, TimeSpan.FromSeconds 20.0)

        let input, xml = ratchetInput directory db
        let release = holdWriteLock dbPath

        let writer =
            Threading.Tasks.Task.Run(fun () -> persistProjectRatchetCoverageWithMapped ignore db directory input xml)

        Threading.Thread.Sleep 1500
        Assert.False(writer.IsCompleted, "the writer must wait for the indexer's lock, not race it")
        release ()
        writer.GetAwaiter().GetResult()

        use inspection = db.OpenConnection()
        use coverage = inspection.CreateCommand()

        coverage.CommandText <-
            "SELECT COUNT(*) FROM fshw_project_ratchet_coverage WHERE project = 'Fixture.Tests' AND hits = 7;"

        Assert.Equal(1L, Convert.ToInt64(coverage.ExecuteScalar())))

// The honest failure the ticket asked to preserve: a holder that outlasts the writer's own
// bound fails the write, naming the lock, instead of writing partial or succeeding silently.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a holder that outlasts the writer's busy bound fails the write as a held lock`` () =
    withTempDir "ratchet-busy-timeout" (fun directory ->
        let dbPath = Path.Combine(directory, "impact.db")

        let db =
            Database.create (dbPath, TestPrune.Domain.WalkPolicy.FollowAll, TimeSpan.FromSeconds 1.0)

        let input, xml = ratchetInput directory db
        let release = holdWriteLock dbPath

        try
            let failure =
                Assert.ThrowsAny<SqliteException>(fun () ->
                    persistProjectRatchetCoverageWithMapped ignore db directory input xml)

            Assert.Equal(5, failure.SqliteErrorCode)
        finally
            release ())
