/// The impact queries the plugin runs against its index, in one place, so a test can count them per handler instance.
module FsHotWatch.TestPrune.PluginQueries

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.TestPrune.TestReports

/// The impact queries the plugin runs against its index, in one place, so a test can
/// count them per handler instance.
type internal ImpactQueries =
    {
        /// The tests `QueryAffectedTests` selects for a set of changed symbols.
        AffectedTests: string list -> TestMethodInfo list
        /// For each symbol, the test projects its single-seed query would select from.
        CoveringProjectsBySeed: string list -> Map<string, Set<string>>
        /// Every test project the index holds a test method for. Not a covering query:
        /// one scan, whatever is queued. `None` when the index cannot be read this way
        /// (an in-memory index is private to the connection that made it).
        IndexedTestProjects: unit -> Set<string> option
    }

module internal ImpactQueries =
    let private indexedTestProjects (db: Database) () : Set<string> option =
        try
            use conn = db.OpenConnection()
            use query = conn.CreateCommand()
            query.CommandText <- "SELECT DISTINCT test_project FROM test_methods;"
            use reader = query.ExecuteReader()

            [ while reader.Read() do
                  reader.GetString 0 ]
            |> Set.ofList
            |> Some
        with :? Microsoft.Data.Sqlite.SqliteException ->
            None

    let ofDatabase (db: Database) : ImpactQueries =
        { AffectedTests = db.QueryAffectedTests
          CoveringProjectsBySeed = db.QueryCoveringProjectsBySeed
          IndexedTestProjects = indexedTestProjects db }
