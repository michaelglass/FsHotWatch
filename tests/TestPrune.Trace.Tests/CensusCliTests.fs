module TestPrune.Trace.Tests.CensusCliTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Model
open TestPrune.Trace.TraceIngest
open TestPrune.Trace.TraceStore
open TestPrune.Trace.Cli

/// Records one run of project P with `traced` of 100 executed tests traced.
let private recordAt (path: string) (runId: string) (traced: int) =
    use store = Store.Open path

    let stats: RunStats =
        { executed = 100
          traced = traced
          complete = traced
          unmappedIds = 0
          cpuMs = 1L
          counters =
            { Test = 100L
              Class = 0L
              Collection = 0L
              Assembly = 0L
              Override = 0L
              StaticInit = 0L
              Ambient = 0L
              Overflow = 0L }
          rejected = [||] }

    store.RecordRun(
        { RunId = runId
          TestProject = "P"
          TreeHash = "t"
          EnvFingerprint = "E"
          RecordedAt = DateTimeOffset.UtcNow
          Kind = FullRun
          Status = Recorded
          Reason = ""
          StatsJson = JsonSerializer.Serialize stats },
        [],
        []
    )

let private dir () =
    Directory.CreateTempSubdirectory().FullName

let private census cwd args = Program.run cwd ("census" :: args)

[<Fact>]
let ``census exits 0 and prints the table when every project passes`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 100
    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 0 @>
    test <@ o.Stdout.StartsWith "P  run r1  PASS" @>
    test <@ o.Stderr = "" @>

[<Fact>]
let ``census exits 1 when a project misses a bar`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 50
    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 1 @>
    test <@ o.Stdout.StartsWith "P  run r1  FAIL" @>

[<Fact>]
let ``census --run reads that run`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 50
    recordAt db "r2" 100
    test <@ (census cwd [ "--db"; db ]).Exit = 0 @>
    test <@ (census cwd [ "--db"; db; "--run"; "r1" ]).Exit = 1 @>

[<Fact>]
let ``census --json prints one report per project`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 99
    let o = census cwd [ "--json"; "--db"; db ]
    test <@ o.Exit = 0 @>
    use doc = JsonDocument.Parse o.Stdout
    let p = doc.RootElement.[0]
    let project = p.GetProperty("Census").GetProperty("TestProject").GetString()
    let ratio = p.GetProperty("TracedRatio").GetDouble()
    let untraced = p.GetProperty("Untraced").GetInt32()
    let passes = p.GetProperty("Passes").GetBoolean()
    test <@ (project, ratio, untraced, passes) = ("P", 0.99, 1, true) @>

[<Fact>]
let ``census without --db reads fshw's trace database first`` () =
    let cwd = dir ()
    recordAt (Path.Combine(cwd, ".test-prune-traces.db")) "cli" 100
    test <@ (census cwd []).Stdout.Contains "run cli" @>
    Directory.CreateDirectory(Path.Combine(cwd, ".fshw")) |> ignore
    recordAt (Path.Combine(cwd, ".fshw", "test-traces.db")) "fshw" 100
    test <@ (census cwd []).Stdout.Contains "run fshw" @>

[<Fact>]
let ``census with no recorded run fails rather than passing vacuously`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 100
    let o = census cwd [ "--db"; db; "--run"; "nope" ]
    test <@ o.Exit = 1 @>
    test <@ o.Stderr.Contains "no recorded run" @>

[<Fact>]
let ``census of a database with no recorded run at all fails`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    (Store.Open db :> IDisposable).Dispose()
    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 1 @>
    test <@ o.Stderr.Contains "no recorded run (any run)" @>

[<Fact>]
let ``census of a file that is not a database exits 2 naming the error`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    File.WriteAllText(db, String('x', 4096))
    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "not a database" @>

[<Fact>]
let ``census of a run whose stats it cannot read exits 2 with the whole error`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")

    do
        use store = Store.Open db

        store.RecordRun(
            { RunId = "r1"
              TestProject = "P"
              TreeHash = "t"
              EnvFingerprint = "E"
              RecordedAt = DateTimeOffset.UtcNow
              Kind = FullRun
              Status = Recorded
              Reason = ""
              StatsJson = "not json" },
            [],
            []
        )

    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.StartsWith(db + ": System.Text.Json.JsonException") @>

[<Fact>]
let ``census names a missing database and exits 2`` () =
    let cwd = dir ()
    let o = census cwd [ "--db"; Path.Combine(cwd, "missing.db") ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "missing.db" @>
    test <@ not (File.Exists(Path.Combine(cwd, "missing.db"))) @>

[<Fact>]
let ``census refuses a newer trace database with exit 2`` () =
    let cwd = dir ()
    let db = Path.Combine(cwd, "x.db")
    recordAt db "r1" 100

    do
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{db}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{TraceSchemaVersion + 1};"
        cmd.ExecuteNonQuery() |> ignore
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

    let o = census cwd [ "--db"; db ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "trace schema" @>

[<Theory>]
[<InlineData("--db")>]
[<InlineData("--run")>]
[<InlineData("--bogus")>]
let ``census rejects a flag with no value or an unknown flag with the usage and exit 2`` (flag: string) =
    let o = census (dir ()) [ flag ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains flag @>
    test <@ o.Stderr.Contains "test-prune-traces census [--db <path>] [--run <runId>] [--json]" @>

[<Fact>]
let ``an unknown verb prints the usage and exits 2`` () =
    let o = Program.run (dir ()) [ "nope" ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains Program.usage @>
