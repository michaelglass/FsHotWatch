module TestPrune.Trace.Tests.CensusTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceIngest
open TestPrune.Trace.TraceStore

let private tempDb () =
    Path.Combine(Directory.CreateTempSubdirectory().FullName, "t.db")

let private counters test ambient =
    { Test = test
      Class = 0L
      Collection = 0L
      Assembly = 0L
      Override = 0L
      StaticInit = 0L
      Ambient = ambient
      Overflow = 0L }

/// The stats_json exactly as ingestion writes it.
let private stats executed traced complete (c: HitCounters) =
    JsonSerializer.Serialize(
        { executed = executed
          traced = traced
          complete = complete
          unmappedIds = 0
          cpuMs = 1L
          counters = c
          rejected = [||] }
        : RunStats
    )

let private run project runId status statsJson : TraceRun =
    { RunId = runId
      TestProject = project
      TreeHash = "t"
      EnvFingerprint = "E"
      RecordedAt = DateTimeOffset.UtcNow
      Kind = FullRun
      Status = status
      Reason = ""
      StatsJson = statsJson }

let private scope key symbols : ScopeContent =
    { Key = key
      Symbols = symbols
      Inputs = [] }

let private trace key reasons scopes : TestTrace =
    { TestKey = key
      Status = Some Passed
      Reasons = reasons
      ScopeKeys = scopes }

[<Fact>]
let ``census reads a run's ratios, ambient symbols and pool scopes`` () =
    let path = tempDb ()

    do
        use store = Store.Open path

        let stats =
            """{"executed":200,"traced":199,"complete":190,"unmappedIds":0,"cpuMs":1,
                "counters":{"Test":9990,"Class":0,"Collection":0,"Assembly":0,"Override":0,"StaticInit":0,"Ambient":10,"Overflow":0},"rejected":[]}"""

        store.RecordRun(
            run "P" "r" Recorded stats,
            [ scope "T:1" [ "N.f", "h" ]
              scope "P:pool" [ "N.handler", "h"; "N.job", "h" ]
              scope AmbientScope [ "N.timerTick", "h" ] ],
            [ trace "P|C|m" [] [ "T:1"; "P:pool" ] ]
        )

    let c = Census.latest path None |> List.exactlyOne
    test <@ c.TestProject = "P" && c.RunId = "r" @>
    test <@ (c.Executed, c.Traced, c.Complete) = (200, 199, 190) @>
    test <@ Census.tracedRatio c = 0.995 @>
    test <@ Census.untraced c = 1 @>
    test <@ (c.AmbientHits, c.TotalHits) = (10L, 10000L) @>
    test <@ Census.ambientRatio c = 0.001 @>
    test <@ c.AmbientSymbols = [ "N.timerTick" ] @>

    test
        <@
            c.Pools = [ { Key = "P:pool"
                          Symbols = 2
                          Inputs = 0
                          LinkedTests = 1 } ]
        @>

    test <@ Census.passes Census.defaultBars c @>
    test <@ not (Census.passes Census.defaultBars { c with Traced = 197 }) @>

[<Fact>]
let ``the ratios of an empty run are the passing identities`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 0
          Traced = 0
          Complete = 0
          TotalHits = 0L
          AmbientHits = 0L
          AmbientSymbols = []
          ReasonCounts = Map.empty
          Pools = [] }

    test <@ Census.tracedRatio c = 1.0 @>
    test <@ Census.ambientRatio c = 0.0 @>
    test <@ Census.untraced c = 0 @>
    test <@ Census.passes Census.defaultBars c @>

[<Fact>]
let ``ambient hits over the bar pass only when they are listed for explanation`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 10
          Traced = 10
          Complete = 10
          TotalHits = 100L
          AmbientHits = 5L
          AmbientSymbols = []
          ReasonCounts = Map.empty
          Pools = [] }

    test <@ not (Census.passes Census.defaultBars c) @>
    test <@ Census.passes Census.defaultBars { c with AmbientSymbols = [ "N.tick" ] } @>
    // Under the bar, nothing needs listing.
    test <@ Census.passes Census.defaultBars { c with AmbientHits = 0L } @>

[<Fact>]
let ``ambient code that joined only to a file is listed as that file`` () =
    let path = tempDb ()

    do
        use store = Store.Open path

        store.RecordRun(
            run "P" "r" Recorded (stats 1 1 1 (counters 99L 1L)),
            [ scope "T:1" [ "N.f", "h" ]
              { Key = AmbientScope
                Symbols = [ "N.tick", "h" ]
                Inputs = [ "file-level", "src/Plain.fs", "fh"; "read", "cfg/app.json", "rh" ] } ],
            [ trace "P|C|m" [] [ "T:1" ] ]
        )

    let c = Census.latest path None |> List.exactlyOne
    test <@ c.AmbientSymbols = [ "N.tick"; "file:src/Plain.fs" ] @>

[<Fact>]
let ``reason counts count tests per reason kind, not reasons`` () =
    let path = tempDb ()

    do
        use store = Store.Open path

        store.RecordRun(
            run "P" "r" Recorded (stats 3 3 1 (counters 10L 0L)),
            [ scope "T:1" []; scope "T:2" []; scope "T:3" [] ],
            [ trace
                  "P|C|a"
                  [ UnmappedCode "Q.Nowhere::x (no-document)"
                    UnmappedCode "Q.Other::y (no-version)"
                    NotPassed Failed ]
                  [ "T:1" ]
              trace "P|C|b" [ UnmappedCode "#7 (no-row)" ] [ "T:2" ]
              trace "P|C|c" [] [ "T:3" ] ]
        )

    let c = Census.latest path None |> List.exactlyOne
    test <@ c.ReasonCounts = Map.ofList [ "not-passed", 1; "unmapped-code", 2 ] @>

[<Fact>]
let ``latest takes each project's newest recorded run and skips runs that stored no traces`` () =
    let path = tempDb ()

    do
        use store = Store.Open path
        store.RecordRun(run "P" "r1" Recorded (stats 4 2 2 (counters 1L 0L)), [], [])
        store.RecordRun(run "P" "r2" TreeMovedDuringRun (stats 4 3 0 (counters 1L 0L)), [], [])
        store.RecordRunWithoutTraces(run "P" "r3" FailedToRecord "{}")
        store.RecordRun(run "Q" "r1" Recorded (stats 5 5 5 (counters 1L 0L)), [], [])
        store.RecordRunWithoutTraces(run "R" "r1" Refused "{}")

    let byProject =
        Census.latest path None |> List.map (fun c -> c.TestProject, c.RunId, c.Traced)

    test <@ byProject = [ "P", "r2", 3; "Q", "r1", 5 ] @>

    let r1 =
        Census.latest path (Some "r1")
        |> List.map (fun c -> c.TestProject, c.RunId, c.Traced)

    test <@ r1 = [ "P", "r1", 2; "Q", "r1", 5 ] @>
    test <@ List.isEmpty (Census.latest path (Some "nope")) @>

[<Fact>]
let ``latest refuses a missing database rather than creating one`` () =
    let path = tempDb ()
    raises<FileNotFoundException> <@ Census.latest path None @>
    test <@ not (File.Exists path) @>

[<Fact>]
let ``latest refuses a database written by a newer trace schema`` () =
    let path = tempDb ()

    do
        use store = Store.Open path
        store.RecordRun(run "P" "r" Recorded (stats 1 1 1 (counters 1L 0L)), [], [])

    do
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"PRAGMA user_version = %d{TraceSchemaVersion + 1};"
        cmd.ExecuteNonQuery() |> ignore
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

    raises<TraceSchemaNewerThanConsumer> <@ Census.latest path None @>

[<Fact>]
let ``render prints the ratios, the untraced line, ambient symbols, reasons and pools`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 200
          Traced = 199
          Complete = 190
          TotalHits = 10000L
          AmbientHits = 10L
          AmbientSymbols = [ "N.timerTick" ]
          ReasonCounts = Map.ofList [ "no-outcome", 1; "unmapped-code", 8 ]
          Pools =
            [ { Key = "P:pool"
                Symbols = 2
                Inputs = 3
                LinkedTests = 4 } ] }

    let lines = (Census.render [ c ]).Split('\n') |> Array.map _.TrimEnd()

    test
        <@
            lines = [| "P  run r  PASS"
                       "  traced     199/200 (0.9950, bar >= 0.99)  complete 190"
                       "  untraced   1 executed test(s) recorded no test scope: hit no probe, or name not joined (see no-outcome)"
                       "  ambient    10/10000 (0.00100, bar < 0.001, or listed)"
                       "    ambient symbol: N.timerTick"
                       "  incomplete no-outcome                   1"
                       "  incomplete unmapped-code                8"
                       "  pool P:pool: 2 symbols, 3 inputs, 4 tests"
                       "" |]
        @>

[<Fact>]
let ``render marks a failing project and omits empty sections`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 10
          Traced = 5
          Complete = 5
          TotalHits = 0L
          AmbientHits = 0L
          AmbientSymbols = []
          ReasonCounts = Map.empty
          Pools = [] }

    let lines = (Census.render [ c ]).Split('\n') |> Array.map _.TrimEnd()

    test
        <@
            lines = [| "P  run r  FAIL"
                       "  traced     5/10 (0.5000, bar >= 0.99)  complete 5"
                       "  untraced   5 executed test(s) recorded no test scope: hit no probe, or name not joined (see no-outcome)"
                       "  ambient    0/0 (0.00000, bar < 0.001, or listed)"
                       "" |]
        @>

[<Fact>]
let ``render omits the untraced line when every executed test was traced`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 1
          Traced = 1
          Complete = 1
          TotalHits = 1L
          AmbientHits = 0L
          AmbientSymbols = []
          ReasonCounts = Map.empty
          Pools = [] }

    test <@ not ((Census.render [ c ]).Contains "untraced") @>

[<Fact>]
let ``a report carries the ratios and the verdict for json`` () =
    let c: Census.ProjectCensus =
        { TestProject = "P"
          RunId = "r"
          Executed = 4
          Traced = 3
          Complete = 3
          TotalHits = 0L
          AmbientHits = 0L
          AmbientSymbols = []
          ReasonCounts = Map.empty
          Pools = [] }

    let r = Census.report Census.defaultBars c

    test <@ r.Census = c @>
    test <@ (r.TracedRatio, r.AmbientRatio, r.Untraced, r.Passes) = (0.75, 0.0, 1, false) @>

[<Fact>]
let ``the census of an ingested run agrees with the ingestion summary`` () =
    let w = TraceIngestTests.World()

    let asTest (st: TestPrune.Trace.Recorder.RecorderState) key meth =
        st.EnterScope key
        let s = st.CurrentScope()
        s.TestClass <- "N.Tests"
        s.TestMethod <- meth
        s.TestDisplay <- "N.Tests." + meth
        s.Parents <- [| "C:N.Tests"; "A:assembly" |]

    w.Process(
        10,
        null,
        fun st ->
            st.EnterScope "P:pool"
            st.Hit 1
            st.EnterScope "C:N.Tests"
            st.CurrentScope().Links.TryAdd("P:pool", 0uy) |> ignore
            asTest st "T:1" "t"
            st.Hit 0
            asTest st "T:2" "u"
            st.Hit 2
            st.ExitScope()
            // Unattributed: a symbol, and code in a file the index does not hold.
            st.Hit 0
            st.Hit 4
    )

    let s =
        using (Store.Open w.TraceDb) (fun store ->
            ingest
                store
                (w.Request
                    [ { Name = "N.Tests.t"; Outcome = Passed }
                      { Name = "N.Tests.u"; Outcome = Passed }
                      // Ran, but hit no probe: no test scope.
                      { Name = "N.Tests.nohit"
                        Outcome = Passed } ]))

    let c = Census.latest w.TraceDb None |> List.exactlyOne
    test <@ (c.TestProject, c.RunId) = ("P", "run1") @>
    test <@ (c.Executed, c.Traced, c.Complete) = (s.Executed, s.Traced, s.Complete) @>
    test <@ (c.Executed, c.Traced, Census.untraced c) = (3, 2, s.UntracedExecuted.Length) @>
    test <@ c.ReasonCounts = s.ReasonCounts @>
    test <@ c.AmbientSymbols = [ "N.M.f"; "file:src/Plain.fs" ] @>

    test
        <@
            c.Pools = [ { Key = "P:pool"
                          Symbols = 1
                          Inputs = 0
                          LinkedTests = 2 } ]
        @>
