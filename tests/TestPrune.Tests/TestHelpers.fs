module TestPrune.Tests.TestHelpers

open System
open System.IO
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode
open TestPrune.Domain

let tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

/// Drop the pooled SQLite connections of the database at `dbPath`, and only that one:
/// `Database` pools under `Data Source=<path>`. `SqliteConnection.ClearAllPools` disposes
/// the handles of every class opening a database in parallel, and fails one that did
/// nothing wrong (AUTOMATION-842, AUTOMATION-1022).
let clearSqlitePool (dbPath: string) =
    use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath}")
    Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

let cleanupDb (path: string) =
    for ext in [ ""; "-wal"; "-shm" ] do
        let p = path + ext

        if File.Exists p then
            File.Delete p

let withDb (f: Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f db
    finally
        cleanupDb path

let runDeadCodeVerbose (db: Database) (patterns: string list) (includeTests: bool) =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
    let entryPoints = findEntryPoints allNames patterns
    let reachable = db.GetReachableSymbols(entryPoints)
    let testMethodNames = db.GetTestMethodSymbolNames()
    findDeadCodeVerbose allSymbols reachable testMethodNames includeTests db.GetIncomingEdgesBatch

let runDeadCode (db: Database) (patterns: string list) (includeTests: bool) =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
    let entryPoints = findEntryPoints allNames patterns
    let reachable = db.GetReachableSymbols(entryPoints)
    let testMethodNames = db.GetTestMethodSymbolNames()
    findDeadCode allSymbols reachable testMethodNames includeTests

/// Standard test graph: testA -> funcB -> TypeC, plus an unrelated symbol.
let standardGraph =
    { Symbols =
        [ { FullName = "Tests.testA"
            Kind = Function
            Location = InSource("tests/Tests.fs", 1, 5, "") }
          { FullName = "Lib.funcB"
            Kind = Function
            Location = InSource("src/Lib.fs", 1, 5, "") }
          { FullName = "Domain.TypeC"
            Kind = Type
            Location = InSource("src/Domain.fs", 1, 3, "") }
          { FullName = "Other.unrelated"
            Kind = Function
            Location = InSource("src/Other.fs", 1, 5, "") } ]
      Dependencies =
        [ { FromSymbol = "Tests.testA"
            ToSymbol = "Lib.funcB"
            Kind = Calls
            Source = "core" }
          { FromSymbol = "Lib.funcB"
            ToSymbol = "Domain.TypeC"
            Kind = UsesType
            Source = "core" } ]
      TestMethods =
        [ { SymbolFullName = "Tests.testA"
            TestProject = "MyTests"
            TestClass = "Tests"
            TestMethod = "testA" } ]
      Attributes = []
      ParentLinks = []
      Diagnostics = AnalysisDiagnostics.Zero }

let withDbPath (f: string -> Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f path db
    finally
        cleanupDb path

/// The hang cap for a wait whose outcome, not its duration, is the claim. FSHW-WALL-001
/// flags an asserted wait with a literal budget under 30 s: on a loaded box such a wait
/// fails on load, not on behaviour. A named cap at or above 30 s only decides how long a
/// genuine hang takes to report (as `HangWaitMs` in FsHotWatch's TestHelpers).
let HangWaitMs = 30_000

/// `HangWaitMs` for the `Wait(TimeSpan)` overloads.
let HangWait = TimeSpan.FromMilliseconds(float HangWaitMs)
