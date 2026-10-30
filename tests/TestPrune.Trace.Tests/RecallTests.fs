module TestPrune.Trace.Tests.RecallTests

// The recall check against a small index: a writer W, a reader R of W's table (the SQL
// edge R → W), a leaf L that W calls, and an unrelated symbol Z.
//
//   TW calls W        traced executing W, L      selected for both under both policies
//   TR calls R        traced executing R         selected under both
//   TD calls R        traced executing R, W      W reaches TD only through the SQL edge:
//                                                selected following every edge, missed
//                                                once the SQL source is test-scoped
//   TZ calls R        traced executing Z         Z reaches no test: missed under both

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore
open TestPrune.Trace.Cli

[<Literal>]
let private SqlSource = "sql-hydra"

let private symbol (name: string) =
    { FullName = name
      Kind = Function
      SourceFile = $"src/%s{name}.fs"
      LineStart = 1
      LineEnd = 2
      ContentHash = $"h-%s{name}"
      IsExtern = false }

let private edge (source: string) (from: string) (target: string) =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = source }

let private testMethod (name: string) =
    { SymbolFullName = $"Tests.%s{name}"
      TestProject = "P"
      TestClass = "Tests"
      // The index keeps the source name, backticks included; a trace keeps xUnit's name.
      TestMethod = $"``%s{name}``" }

let private graph (tests: string list) : AnalysisResult =
    { Symbols =
        [ "App.W"; "App.R"; "App.L"; "App.Z" ]
        @ (tests |> List.map (sprintf "Tests.%s"))
        |> List.map symbol
      Dependencies =
        [ edge "core" "App.W" "App.L"
          edge SqlSource "App.R" "App.W"
          yield!
              tests
              |> List.map (fun t -> edge "core" $"Tests.%s{t}" (if t = "TW" then "App.W" else "App.R")) ]
      TestMethods = tests |> List.map testMethod
      Attributes = []
      ParentLinks = []
      Diagnostics = AnalysisDiagnostics.Zero }

let private keyOf (test: string) = TraceIngest.testKey "P" "Tests" test

let private trace (test: string) (complete: bool) (executed: (string * string) list) : StoredTrace =
    { TestKey = keyOf test
      EnvFingerprint = "E"
      RunId = "r1"
      Complete = complete
      Reasons = if complete then [] else [ "no-outcome" ]
      Symbols = Set.ofList executed
      Inputs = Set.empty }

let private at (name: string) = name, $"h-%s{name}"

let private traces =
    [ trace "TW" true [ at "App.W"; at "App.L" ]
      trace "TR" true [ at "App.R" ]
      trace "TD" true [ at "App.R"; at "App.W" ]
      trace "TZ" true [ at "App.Z" ] ]

let private sqlScoped = { TestScopedSources = Set.ofList [ SqlSource ] }

let private checkOn (tests: string list) (traces: StoredTrace list) =
    let g = graph tests

    Recall.check
        2
        (TestPrune.InMemoryStore.fromAnalysisResultsWith WalkPolicy.FollowAll [ g ])
        (TestPrune.InMemoryStore.fromAnalysisResultsWith sqlScoped [ g ])
        traces

let private allTests = [ "TW"; "TR"; "TD"; "TZ" ]

let private miss test sym : Recall.Miss = { Test = keyOf test; Symbol = sym }

[<Fact>]
let ``a test reached only through a test-scoped edge is a miss the narrower policy adds`` () =
    let r = checkOn allTests traces
    test <@ r.NarrowedOnly = [ miss "TD" "App.W" ] @>
    test <@ not (Recall.passes r) @>

[<Fact>]
let ``a symbol no walk reaches the test from is a miss under both policies, not an added one`` () =
    let r = checkOn allTests traces
    test <@ r.FollowAll = [ miss "TZ" "App.Z" ] @>
    test <@ r.Narrowed = [ miss "TD" "App.W"; miss "TZ" "App.Z" ] @>

[<Fact>]
let ``every executed indexed symbol of every complete trace is one checked pair`` () =
    let r = checkOn allTests traces
    test <@ r.Traces = 4 @>
    test <@ r.Pairs = 6 @>

[<Fact>]
let ``recall passes when the narrower policy adds no miss, even with misses both share`` () =
    let r = checkOn allTests (traces |> List.filter (fun t -> t.TestKey <> keyOf "TD"))
    test <@ r.FollowAll = [ miss "TZ" "App.Z" ] @>
    test <@ Recall.passes r @>

[<Fact>]
let ``incomplete traces, unindexed tests and symbols, and stale hashes are skipped and counted`` () =
    let r =
        checkOn
            [ "TW"; "TR" ]
            [ trace "TW" true [ at "App.W"; "App.L", "h-old"; "Gone.symbol", "h" ]
              trace "TR" false [ at "App.R" ]
              trace "TD" true [ at "App.W" ] ]

    test <@ r.Traces = 1 @>
    test <@ r.IncompleteTraces = 1 @>
    test <@ r.UnindexedTests = [ keyOf "TD" ] @>
    test <@ r.StalePairs = 1 @>
    test <@ r.UnindexedSymbols = 1 @>
    test <@ r.Pairs = 1 @>
    test <@ r.FollowAll.IsEmpty && r.Narrowed.IsEmpty @>

[<Fact>]
let ``nothing checked is not a pass`` () =
    let r = checkOn allTests []
    test <@ r.Pairs = 0 @>
    test <@ not (Recall.passes r) @>

// ---------------------------------------------------------------------------
// The verb, over an index file and a trace database
// ---------------------------------------------------------------------------

let private run: TraceRun =
    { RunId = "r1"
      TestProject = "P"
      TreeHash = "t"
      EnvFingerprint = "E"
      RecordedAt = DateTimeOffset.UtcNow
      Kind = FullRun
      Status = Recorded
      Reason = ""
      StatsJson = "{}" }

/// An index of `graph tests` and a trace database holding `traces`, in a fresh directory.
let private files (tests: string list) (traces: StoredTrace list) =
    let dir = Directory.CreateTempSubdirectory().FullName
    let index = Path.Combine(dir, "index.db")
    let db = Path.Combine(dir, "traces.db")
    TestPrune.Database.Database.create(index).RebuildProjects([ graph tests ])

    using (Store.Open db) (fun store ->
        store.RecordRun(
            run,
            traces
            |> List.map (fun t ->
                ({ Key = "T:" + t.TestKey
                   Symbols = Set.toList t.Symbols
                   Inputs = [] }
                : ScopeContent)),
            traces
            |> List.map (fun t ->
                ({ TestKey = t.TestKey
                   Status = Some Passed
                   Reasons = if t.Complete then [] else [ NoOutcome ]
                   ScopeKeys = [ "T:" + t.TestKey ] }
                : TestTrace))
        ))

    dir, index, db

let private recall cwd args = Program.run cwd ("recall" :: args)

[<Fact>]
let ``recall exits 1 and names the added miss`` () =
    let dir, index, db = files allTests traces
    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    test <@ o.Exit = 1 @>
    test <@ o.Stdout.StartsWith "recall  FAIL  traces 4  pairs 6  follow-all misses 1  narrowed misses 2" @>
    let td = keyOf "TD"
    test <@ o.Stdout.Contains $"  added miss  %s{td}  App.W\n" @>

[<Fact>]
let ``recall exits 0 when narrowing adds no miss`` () =
    let dir, index, db =
        files allTests (traces |> List.filter (fun t -> t.TestKey <> keyOf "TD"))

    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    test <@ o.Exit = 0 @>
    test <@ o.Stdout.StartsWith "recall  PASS" @>

[<Fact>]
let ``recall --json lists the added misses`` () =
    let dir, index, db = files allTests traces

    let o =
        recall
            dir
            [ "--index"
              index
              "--db"
              db
              "--test-scoped"
              "sql,sql-hydra"
              "--json"
              "--jobs"
              "1" ]

    use doc = Text.Json.JsonDocument.Parse o.Stdout

    let added =
        doc.RootElement.GetProperty("NarrowedOnly").EnumerateArray()
        |> Seq.map (fun m -> m.GetProperty("Symbol").GetString())
        |> List.ofSeq

    test <@ added = [ "App.W" ] @>

[<Fact>]
let ``recall --limit caps the misses listed per policy`` () =
    let dir, index, db = files allTests traces

    let o =
        recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource; "--limit"; "1" ]

    test <@ o.Stdout.Contains "  narrowed: 1 more misses (--json lists them all)\n" @>

[<Fact>]
let ``recall reads only the run it is given`` () =
    let dir, index, db = files allTests traces

    let o =
        recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource; "--run"; "other" ]

    test <@ o.Exit = 2 @>
    test <@ o.Stdout.StartsWith "recall  NOTHING CHECKED" @>

[<Fact>]
let ``recall refuses an index of another schema without touching it`` () =
    let dir, index, db = files allTests traces

    using (new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{index}")) (fun conn ->
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version = 1"
        cmd.ExecuteNonQuery() |> ignore
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn)

    let before = File.ReadAllBytes index
    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "has index schema v1" @>
    test <@ File.ReadAllBytes index = before @>

[<Theory>]
[<InlineData("")>]
[<InlineData("--index x.db")>]
[<InlineData("--test-scoped sql")>]
[<InlineData("--index x.db --test-scoped sql --jobs 0")>]
[<InlineData("--index x.db --test-scoped sql --limit many")>]
[<InlineData("--index x.db --test-scoped sql --frobnicate y")>]
[<InlineData("--index x.db --test-scoped sql --bogus")>]
[<InlineData("--index x.db --test-scoped")>]
[<InlineData("--test-scoped --json --index x.db")>]
let ``recall rejects bad arguments with exit 2 and its usage`` (args: string) =
    let o =
        recall
            (Directory.CreateTempSubdirectory().FullName)
            (args.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "usage: test-prune-traces recall" @>

[<Fact>]
let ``recall exits 2 for a missing index or trace database`` () =
    let dir, index, _ = files allTests traces
    let noIndex = recall dir [ "--index"; "absent.db"; "--test-scoped"; SqlSource ]
    test <@ noIndex.Exit = 2 && noIndex.Stderr.StartsWith "no index at" @>

    let noDb =
        recall dir [ "--index"; index; "--db"; "absent.db"; "--test-scoped"; SqlSource ]

    test <@ noDb.Exit = 2 && noDb.Stderr.StartsWith "no trace database at" @>

[<Fact>]
let ``recall exits 2 for a trace database it cannot read`` () =
    let dir, index, _ = files allTests traces
    let junk = Path.Combine(dir, "junk.db")
    File.WriteAllText(junk, "not a database")
    let o = recall dir [ "--index"; index; "--db"; junk; "--test-scoped"; SqlSource ]
    test <@ o.Exit = 2 @>

[<Fact>]
let ``recall reads the newest recorded run, passing over a newer refused one`` () =
    let dir, index, db = files allTests traces

    using (Store.Open db) (fun store ->
        store.RecordRunWithoutTraces
            { run with
                RunId = "r2"
                EnvFingerprint = ""
                Status = Refused
                Reason = "untraced" })

    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    test <@ o.Stdout.StartsWith "recall  FAIL  traces 4  pairs 6" @>

[<Fact>]
let ``recall lists a traced test the index does not know`` () =
    let dir, index, db = files [ "TW"; "TR"; "TZ" ] traces
    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    let td = keyOf "TD"
    test <@ o.Stdout.Contains $"  unindexed test  %s{td}\n" @>

[<Fact>]
let ``recall exits 2 for a trace database of a newer trace schema`` () =
    let dir, index, db = files allTests traces

    using (new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{db}")) (fun conn ->
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA user_version = 9999"
        cmd.ExecuteNonQuery() |> ignore
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn)

    let o = recall dir [ "--index"; index; "--db"; db; "--test-scoped"; SqlSource ]
    test <@ o.Exit = 2 @>
    test <@ o.Stderr.Contains "trace schema v9999" @>
