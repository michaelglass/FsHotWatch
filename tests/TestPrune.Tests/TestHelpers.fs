module TestPrune.Tests.TestHelpers

open System
open System.IO
open TestPrune.Database
open TestPrune.DeadCode
open TestPrune.Domain

let tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

let private cleanupDb (path: string) =
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

let runDeadCode (db: Database) (patterns: string list) (includeTests: bool) =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
    let entryPoints = findEntryPoints allNames patterns
    let reachable = db.GetReachableSymbols(entryPoints)
    let testMethodNames = db.GetTestMethodSymbolNames()
    findDeadCode allSymbols reachable testMethodNames includeTests

let withDbPath (f: string -> Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f path db
    finally
        cleanupDb path
