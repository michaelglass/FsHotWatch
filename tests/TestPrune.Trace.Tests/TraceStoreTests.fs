module TestPrune.Trace.Tests.TraceStoreTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore

let private tempPath () =
    Path.Combine(Directory.CreateTempSubdirectory().FullName, "traces.db")

let private run id : TraceRun =
    { RunId = id
      TestProject = "P"
      TreeHash = "tree"
      EnvFingerprint = "E1"
      RecordedAt = DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero)
      Kind = FullRun
      Status = Recorded
      Reason = ""
      StatsJson = "{}" }

let private classScope: ScopeContent =
    { Key = "C:Ns.C"
      Symbols = [ "Lib.fixtureSetup", "h0" ]
      Inputs = [] }

let private testScope key syms : ScopeContent =
    { Key = key
      Symbols = syms
      Inputs = [ "read", "cfg/app.json", "fh" ] }

let private passed key scopes : TestTrace =
    { TestKey = key
      Status = Some Passed
      Reasons = []
      ScopeKeys = scopes }

let private scalarAt (path: string) (sql: string) : int64 =
    use conn = new SqliteConnection($"Data Source=%s{path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    let v = cmd.ExecuteScalar() :?> int64
    SqliteConnection.ClearPool conn
    v

[<Fact>]
let ``a test's trace is the union of its own scopes and the scopes it inherits`` () =
    use store = Store.Open(tempPath ())

    store.RecordRun(
        run "r1",
        [ classScope; testScope "T:1" [ "Lib.f", "h1" ] ],
        [ passed "P|Ns.C|m" [ "T:1"; "C:Ns.C" ] ]
    )

    let t = store.TryRead("P|Ns.C|m", "E1") |> Option.get
    test <@ t.Complete @>
    test <@ t.RunId = "r1" @>
    test <@ t.Symbols = set [ "Lib.f", "h1"; "Lib.fixtureSetup", "h0" ] @>
    test <@ t.Inputs = set [ "read", "cfg/app.json", "fh" ] @>

[<Fact>]
let ``a fixture scope linked from many tests is stored once`` () =
    let path = tempPath ()

    do
        use store = Store.Open path

        store.RecordRun(
            run "r1",
            [ classScope
              testScope "T:1" [ "Lib.f", "h1" ]
              testScope "T:2" [ "Lib.g", "h2" ] ],
            [ passed "P|Ns.C|a" [ "T:1"; "C:Ns.C" ]; passed "P|Ns.C|b" [ "T:2"; "C:Ns.C" ] ]
        )

        test <@ (store.TryRead("P|Ns.C|a", "E1") |> Option.get).Symbols.Contains("Lib.fixtureSetup", "h0") @>
        test <@ (store.TryRead("P|Ns.C|b", "E1") |> Option.get).Symbols.Contains("Lib.fixtureSetup", "h0") @>

    test <@ scalarAt path "SELECT COUNT(*) FROM trace_scopes WHERE scope_key = 'C:Ns.C'" = 1L @>

    test
        <@
            scalarAt
                path
                "SELECT COUNT(*) FROM trace_entries e JOIN symbol_versions v ON v.id = e.symbol_version_id WHERE v.symbol_full_name = 'Lib.fixtureSetup'" = 1L
        @>

    test <@ scalarAt path "SELECT COUNT(*) FROM trace_test_scopes" = 4L @>

[<Fact>]
let ``re-recording a test replaces its trace; other fingerprints are kept`` () =
    use store = Store.Open(tempPath ())
    let t1 = passed "P|Ns.C|m" [ "T:1" ]
    store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ t1 ])
    store.RecordRun({ run "r2" with EnvFingerprint = "E2" }, [ testScope "T:1" [ "Lib.g", "h9" ] ], [ t1 ])
    store.RecordRun(run "r3", [ testScope "T:9" [ "Lib.g", "h2" ] ], [ { t1 with ScopeKeys = [ "T:9" ] } ])
    test <@ (store.TryRead("P|Ns.C|m", "E1") |> Option.get).Symbols = set [ "Lib.g", "h2" ] @>
    test <@ (store.TryRead("P|Ns.C|m", "E2") |> Option.get).Symbols = set [ "Lib.g", "h9" ] @>
    test <@ store.TryRead("P|Ns.C|m", "E3") = None @>

[<Fact>]
let ``re-recording the same run replaces its scope content`` () =
    use store = Store.Open(tempPath ())
    store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ passed "P|Ns.C|m" [ "T:1" ] ])
    store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.g", "h2" ] ], [ passed "P|Ns.C|m" [ "T:1" ] ])
    test <@ (store.TryRead("P|Ns.C|m", "E1") |> Option.get).Symbols = set [ "Lib.g", "h2" ] @>
    test <@ store.Runs "P" |> List.map (fun r -> r.RunId) = [ "r1" ] @>

[<Fact>]
let ``an incomplete trace keeps its reasons and says so`` () =
    use store = Store.Open(tempPath ())

    store.RecordRun(
        run "r1",
        [ testScope "T:1" [] ],
        [ { TestKey = "P|Ns.C|m"
            Status = Some Failed
            Reasons = [ NotPassed Failed; ChildProcessUntraced "dotnet" ]
            ScopeKeys = [ "T:1" ] } ]
    )

    let t = store.TryRead("P|Ns.C|m", "E1") |> Option.get
    test <@ not t.Complete @>
    test <@ t.Reasons = [ "not-passed:failed"; "child-process-untraced:dotnet" ] @>

[<Fact>]
let ``every incomplete reason has a distinct, stable code`` () =
    let codes =
        [ NoOutcome
          NotPassed Passed
          NotPassed Failed
          NotPassed Skipped
          NotPassed OtherOutcome
          SourceDrift "src/A.fs"
          NotIndexed "src/B.fs"
          ChildProcessUntraced "git"
          RecorderOverflow
          TreeMoved
          DumpRejected "truncated"
          UnmappedCode "Ns.T::m" ]
        |> List.map reasonCode

    test
        <@
            codes = [ "no-outcome"
                      "not-passed:passed"
                      "not-passed:failed"
                      "not-passed:skipped"
                      "not-passed:other"
                      "source-drift:src/A.fs"
                      "not-indexed:src/B.fs"
                      "child-process-untraced:git"
                      "recorder-overflow"
                      "tree-moved"
                      "dump-rejected:truncated"
                      "unmapped-code:Ns.T::m" ]
        @>

[<Fact>]
let ``a test status is stored for every outcome, and unknown when there is none`` () =
    let path = tempPath ()

    do
        use store = Store.Open path

        let t key status : TestTrace =
            { TestKey = key
              Status = status
              Reasons = []
              ScopeKeys = [ "T:1" ] }

        store.RecordRun(
            run "r1",
            [ testScope "T:1" [] ],
            [ t "a" (Some Passed)
              t "b" (Some Failed)
              t "c" (Some Skipped)
              t "d" (Some OtherOutcome)
              t "e" None ]
        )

    use conn = new SqliteConnection($"Data Source=%s{path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT group_concat(status, ',') FROM (SELECT status FROM trace_tests ORDER BY test_key)"
    let statuses = cmd.ExecuteScalar() :?> string
    SqliteConnection.ClearPool conn
    test <@ statuses = "passed,failed,skipped,other,unknown" @>

[<Fact>]
let ``a test linking a scope its run does not contain is rejected and nothing is written`` () =
    use store = Store.Open(tempPath ())

    raises<ArgumentException>
        <@ store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ passed "P|Ns.C|m" [ "T:1"; "C:gone" ] ]) @>

    test <@ store.TryRead("P|Ns.C|m", "E1") = None @>
    test <@ List.isEmpty (store.Runs "P") @>

[<Fact>]
let ``runs without traces are kept as bookkeeping, newest first`` () =
    use store = Store.Open(tempPath ())

    store.RecordRunWithoutTraces(
        { run "r1" with
            Status = Refused
            Reason = "optimized" }
    )

    store.RecordRunWithoutTraces(
        { run "r2" with
            Status = FailedToRecord }
    )

    store.RecordRunWithoutTraces(
        { run "r3" with
            Status = TreeMovedDuringRun
            Kind = PartialRun }
    )

    store.RecordRun(
        { run "r4" with
            StatsJson = """{"executed":1}""" },
        [],
        []
    )

    let runs = store.Runs "P"
    test <@ runs |> List.map (fun r -> r.RunId) = [ "r4"; "r3"; "r2"; "r1" ] @>
    test <@ runs |> List.map (fun r -> r.Status) = [ Recorded; TreeMovedDuringRun; FailedToRecord; Refused ] @>
    test <@ runs |> List.map (fun r -> r.Kind) = [ FullRun; PartialRun; FullRun; FullRun ] @>
    test <@ (List.last runs).Reason = "optimized" @>
    test <@ (List.head runs).StatsJson = """{"executed":1}""" @>
    test <@ (List.head runs).RecordedAt = DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero) @>
    test <@ (List.head runs).TreeHash = "tree" && (List.head runs).EnvFingerprint = "E1" @>
    test <@ List.isEmpty (store.Runs "other") @>

[<Fact>]
let ``garbage collection drops dead tests, unreferenced scopes and versions`` () =
    let path = tempPath ()

    do
        use store = Store.Open path
        let t name scope = passed $"P|Ns.C|%s{name}" [ scope ]

        store.RecordRun(
            run "r1",
            [ testScope "T:a" [ "Lib.a", "1" ]; testScope "T:b" [ "Lib.b", "1" ] ],
            [ t "a" "T:a"; t "b" "T:b" ]
        )

        store.RecordRun(run "r2", [ testScope "T:a2" [ "Lib.a", "2" ] ], [ t "a" "T:a2" ])
        store.CollectGarbage("P", "E1", Some(set [ "P|Ns.C|a" ]))
        test <@ store.TestKeysOf("P", "E1") = set [ "P|Ns.C|a" ] @>
        test <@ (store.TryRead("P|Ns.C|a", "E1") |> Option.get).Symbols = set [ "Lib.a", "2" ] @>

    test <@ scalarAt path "SELECT COUNT(*) FROM symbol_versions" = 1L @>
    test <@ scalarAt path "SELECT COUNT(*) FROM trace_scopes" = 1L @>
    test <@ scalarAt path "SELECT COUNT(*) FROM trace_inputs" = 1L @>

[<Fact>]
let ``garbage collection keeps the run scopes of each project's latest recorded run only`` () =
    use store = Store.Open(tempPath ())

    let runScopes id project =
        store.RecordRun(
            { run id with TestProject = project },
            [ testScope "T:1" [ "Lib.f", "h1" ]
              testScope "S:static-init" [ "Lib.init", "h" ]
              testScope "A:ambient" [ "Lib.tick", "h" ]
              testScope "C:unlinked" [ "Lib.c", "h" ] ],
            [ passed $"%s{project}|Ns.C|m" [ "T:1" ] ]
        )

    runScopes "r1" "P"
    runScopes "r1" "Q"
    runScopes "r2" "P"
    // A later run that recorded nothing does not make the last recording's scopes history.
    store.RecordRunWithoutTraces(
        { run "r3" with
            Status = FailedToRecord }
    )

    store.CollectGarbage("P", "E1", None)
    test <@ List.isEmpty (store.RunScopeKeys("r1", "P")) @>
    test <@ store.RunScopeKeys("r2", "P") = [ "A:ambient"; "S:static-init"; "T:1" ] @>
    test <@ store.RunScopeKeys("r1", "Q") = [ "A:ambient"; "S:static-init"; "T:1" ] @>
    test <@ List.isEmpty (store.RunScopeKeys("r3", "P")) @>

[<Fact>]
let ``garbage collection without a live set keeps every test`` () =
    use store = Store.Open(tempPath ())
    store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ passed "P|Ns.C|m" [ "T:1" ] ])
    store.CollectGarbage("P", "E1", None)
    test <@ store.TestKeysOf("P", "E1") = set [ "P|Ns.C|m" ] @>
    test <@ store.Runs "P" |> List.map (fun r -> r.RunId) = [ "r1" ] @>

[<Fact>]
let ``garbage collection keeps the newest 50 runs of a project as history`` () =
    use store = Store.Open(tempPath ())

    for i in 1..52 do
        store.RecordRunWithoutTraces({ run $"r%d{i}" with Status = Refused })

    store.CollectGarbage("P", "E1", None)
    let ids = store.Runs "P" |> List.map (fun r -> r.RunId)
    test <@ ids.Length = 50 @>
    test <@ List.head ids = "r52" && List.last ids = "r3" @>

[<Fact>]
let ``a file written by a newer trace schema is refused and left byte-identical`` () =
    let path = tempPath ()
    (Store.Open path :> IDisposable).Dispose()

    do
        use conn = new SqliteConnection($"Data Source=%s{path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{TraceSchemaVersion + 1};"
        cmd.ExecuteNonQuery() |> ignore

    SqliteConnection.ClearAllPools()
    let before = File.ReadAllBytes path

    let ex =
        Assert.Throws<TraceSchemaNewerThanConsumer>(fun () -> Store.Open path |> ignore)

    test <@ ex.found = TraceSchemaVersion + 1 && ex.supported = TraceSchemaVersion @>
    test <@ ex.Message.Contains path && ex.Message.Contains "left untouched" @>
    test <@ File.Exists path @>
    test <@ File.ReadAllBytes path = before @>

[<Fact>]
let ``a fresh file is stamped with the current trace schema`` () =
    let path = tempPath ()
    (Store.Open path :> IDisposable).Dispose()
    test <@ scalarAt path "PRAGMA user_version" = int64 TraceSchemaVersion @>

[<Fact>]
let ``migrations run forward and keep every row`` () =
    let path = tempPath ()

    do
        use store = Store.OpenWith(migrations, path)
        store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ passed "P|Ns.C|m" [ "T:1" ] ])

    let plusOne =
        migrations
        @ [ TraceSchemaVersion + 1, "ALTER TABLE trace_runs ADD COLUMN note TEXT;" ]

    do
        use store = Store.OpenWith(plusOne, path)
        test <@ (store.TryRead("P|Ns.C|m", "E1") |> Option.get).Symbols = set [ "Lib.f", "h1" ] @>

    test <@ scalarAt path "PRAGMA user_version" = int64 (TraceSchemaVersion + 1) @>
    test <@ scalarAt path "SELECT COUNT(*) FROM pragma_table_info('trace_runs') WHERE name = 'note'" = 1L @>

[<Fact>]
let ``a failing migration rolls back and leaves the file at its old version`` () =
    let path = tempPath ()
    (Store.Open path :> IDisposable).Dispose()

    let broken =
        migrations
        @ [ TraceSchemaVersion + 1, "ALTER TABLE trace_runs ADD COLUMN note TEXT; SELECT * FROM no_such_table;" ]

    raises<SqliteException> <@ Store.OpenWith(broken, path) @>
    test <@ scalarAt path "PRAGMA user_version" = int64 TraceSchemaVersion @>
    test <@ scalarAt path "SELECT COUNT(*) FROM pragma_table_info('trace_runs') WHERE name = 'note'" = 0L @>

[<Fact>]
let ``a migration list that is not strictly increasing is refused before the file is opened`` () =
    let path = tempPath ()
    raises<ArgumentException> <@ Store.OpenWith(migrations @ migrations, path) @>
    raises<ArgumentException> <@ Store.OpenWith([], path) @>
    test <@ not (File.Exists path) @>

[<Fact>]
let ``a TestPrune.Core schema recreate of the index never touches the trace file`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let tracePath = Path.Combine(dir, "test-traces.db")
    let indexPath = Path.Combine(dir, "test-impact.db")

    do
        use store = Store.Open tracePath
        store.RecordRun(run "r1", [ testScope "T:1" [ "Lib.f", "h1" ] ], [ passed "P|Ns.C|m" [ "T:1" ] ])

    // An index stamped with an older core schema: Database.create deletes and recreates it.
    do
        use conn = new SqliteConnection($"Data Source=%s{indexPath}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "CREATE TABLE junk (x INTEGER); PRAGMA user_version = 1;"
        cmd.ExecuteNonQuery() |> ignore

    SqliteConnection.ClearAllPools()
    let db = TestPrune.Database.Database.create indexPath
    test <@ db.WasRecreated @>
    use store = Store.Open tracePath
    test <@ store.TryRead("P|Ns.C|m", "E1") |> Option.isSome @>
