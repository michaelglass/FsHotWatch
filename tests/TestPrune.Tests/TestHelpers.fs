module TestPrune.Tests.TestHelpers

open System
open System.IO
open TestPrune.Database

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

let withDbPath (f: string -> Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f path db
    finally
        cleanupDb path
