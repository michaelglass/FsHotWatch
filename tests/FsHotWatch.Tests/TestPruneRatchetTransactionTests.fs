module FsHotWatch.Tests.TestPruneRatchetTransactionTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database

[<Theory(Timeout = 10000)>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("RatchetCoverage", "RatchetMappingTransaction")>]
let ``ratchet mapping retains symbol ownership until its coverage write commits`` (partial: bool) =
    withTempDir "ratchet-map-transaction" (fun directory ->
        let db = Database.create (Path.Combine(directory, "impact.db"))
        let symbol: SymbolInfo =
            { FullName = "Fixture.covered"
              Kind = SymbolKind.Value
              SourceFile = "src/Fixture.fs"
              LineStart = 10
              LineEnd = 10
              ContentHash = "original"
              IsExtern = false }

        db.RebuildProjects([ AnalysisResult.Create([ symbol ], [], []) ])
        let input: CoverageInput =
            { Project = "Fixture.Tests"
              RawPath = Path.Combine(directory, "coverage.xml")
              IncludeInRatchet = true
              Scope = if partial then CoverageRunScope.Partial else CoverageRunScope.Full }

        let xml =
            """<coverage><packages><package name="Fixture"><classes><class name="Fixture" filename="src/Fixture.fs"><lines><line number="10" hits="7" /></lines></class></classes></package></packages></coverage>"""

        let mutable attemptedRemoval = false
        let mutable removalBlocked = false

        let removeMappedSymbol () =
            // A separate real connection plays the graph reindexer's orphan
            // removal. The seam orders it AFTER lookup and BEFORE the actual
            // upsert, so this test needs no scheduler race or artificial sleep.
            attemptedRemoval <- true
            use contender = db.OpenConnection()
            use remove = contender.CreateCommand()
            // Microsoft.Data.Sqlite defines zero as infinite, not immediate.
            // Bound this genuine lock acquisition while the writer owns mapping.
            remove.CommandTimeout <- 1
            remove.CommandText <- "DELETE FROM symbols WHERE full_name = 'Fixture.covered';"

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
               JOIN symbols s ON s.id = c.symbol_id
               WHERE c.project = 'Fixture.Tests' AND s.full_name = 'Fixture.covered'
                 AND c.line_offset = 0 AND c.hits = 7;"""
        Assert.Equal(1L, Convert.ToInt64(coverage.ExecuteScalar()))

        // Exclusion lasts only for the transaction. The waiting graph mutation
        // may proceed afterwards; normal FK cascade must remove obsolete points.
        use removeAfterCommit = inspection.CreateCommand()
        removeAfterCommit.CommandText <- "DELETE FROM symbols WHERE full_name = 'Fixture.covered';"
        Assert.Equal(1, removeAfterCommit.ExecuteNonQuery())
        Assert.Equal(0L, Convert.ToInt64(coverage.ExecuteScalar())))
