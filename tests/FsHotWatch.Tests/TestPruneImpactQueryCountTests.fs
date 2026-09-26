/// How many impact queries one impact-selection flush runs.
///
/// A flush classifies every queued symbol by the test projects covering it, which decides
/// what may drop from the queue and what stays owed. Done as one single-seed walk per
/// symbol, a wide queue made the flush the longest step in the plugin: thousands of walks
/// over the same hubs, minutes of work that finished no event, holding a finished run's
/// result fold behind it. The classification is one grouped query per flush.
module FsHotWatch.Tests.TestPruneImpactQueryCountTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database

/// Queued symbols, more than the per-seed attribution budget, so nothing in the flush is
/// entitled to ask about them one at a time.
let private queued = MaxSeedsToAttribute + 50

let private symbolName i = $"Lib.f%d{i}"

/// `Lib.fs` declares every queued symbol; one test in `tests/P.Tests` calls them all.
let private seedIndex (dbPath: string) =
    let db = Database.create dbPath

    let lib =
        { Symbols =
            [ for i in 1..queued ->
                  { FullName = symbolName i
                    Kind = Function
                    SourceFile = "Lib.fs"
                    LineStart = i
                    LineEnd = i
                    ContentHash = symbolName i
                    IsExtern = false } ]
          Dependencies = []
          TestMethods = []
          Attributes = []
          ParentLinks = []
          Diagnostics = AnalysisDiagnostics.Zero }

    let tests =
        { Symbols =
            [ { FullName = "Tests.callsEverything"
                Kind = Function
                SourceFile = "Tests.fs"
                LineStart = 1
                LineEnd = 1
                ContentHash = "t"
                IsExtern = false } ]
          Dependencies =
            [ for i in 1..queued ->
                  { FromSymbol = "Tests.callsEverything"
                    ToSymbol = symbolName i
                    Kind = Calls
                    Source = "core" } ]
          TestMethods =
            [ { SymbolFullName = "Tests.callsEverything"
                TestProject = "tests/P.Tests"
                TestClass = "Tests"
                TestMethod = "callsEverything" } ]
          Attributes = []
          ParentLinks = []
          Diagnostics = AnalysisDiagnostics.Zero }

    db.RebuildProjects [ lib; tests ]

/// Impact queries over the real index, recording each call.
type private Recorded() =
    let gate = obj ()
    let affected = Collections.Generic.List<string list>()
    let grouped = Collections.Generic.List<string list>()

    member _.Queries(db: Database) : ImpactQueries =
        let real = ImpactQueries.ofDatabase db

        { real with
            AffectedTests =
                fun seeds ->
                    lock gate (fun () -> affected.Add seeds)
                    real.AffectedTests seeds
            CoveringProjectsBySeed =
                fun seeds ->
                    lock gate (fun () -> grouped.Add seeds)
                    real.CoveringProjectsBySeed seeds }

    member _.SingleSeedWalks =
        lock gate (fun () -> affected |> Seq.filter (fun s -> s.Length = 1) |> Seq.length)

    member _.GroupedQueries = lock gate (fun () -> List.ofSeq grouped)

[<Fact(Timeout = 60000)>]
let ``a flush classifies its whole queue with one grouped query`` () =
    withTempDir "tp-query-count" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        seedIndex dbPath

        let queue = [ for i in 1..queued -> symbolName i ] |> Set.ofList
        PendingVerification.save tmpDir queue

        let recorded = Recorded()
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        // Analysis-only: the build flushes and selects, and launches nothing, so every
        // query recorded belongs to the flush.
        host.RegisterHandler(
            createWithQueries
                recorded.Queries
                (TimeSpan.FromMinutes 5.0)
                (fun () -> Map.empty)
                dbPath
                tmpDir
                None
                None
                None
                None
                None
                []
                None
        )

        host.EmitBuildCompleted(BuildSucceeded)
        waitForQuiescent host 30000

        test <@ recorded.SingleSeedWalks = 0 @>
        test <@ recorded.GroupedQueries |> List.map Set.ofList = [ queue ] @>)
