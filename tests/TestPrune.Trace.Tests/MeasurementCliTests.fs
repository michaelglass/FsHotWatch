/// The audit, overhead and file-census verbs: argument parsing, verdicts, output and
/// exit codes over substituted measurements, plus the real wiring on a refused project.
module TestPrune.Trace.Tests.MeasurementCliTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Cli

/// One call a substituted measurement received.
type Call =
    { Request: TraceSession.PrepareRequest
      AppArgs: string list
      Sample: float
      Seed: int
      Reps: int
      Only: string list
      Changed: Map<string, (int * int) list> option
      Timeout: TimeSpan }

let private auditReport extra : Audit.AuditReport =
    Audit.summarize
        [ { Display = "N.C.m"
            Extra = extra
            MissingByKind = Map.empty
            MissingUser = []
            UncoveredInit = []
            IsolatedFound = true
            IsolationError = None } ]
        Map.empty

let private overheadReport (traced: float) : Overhead.OverheadReport =
    Overhead.summarize
        [ { Traced = false
            Cpu = TimeSpan.FromMilliseconds 100.0
            MaxRssBytes = 1L
            ExitCode = 0
            Output = ""
            Wall = TimeSpan.Zero
            LiveDescendants = 0
            DumpBytes = 0L
            DumpFiles = 0 }
          { Traced = true
            Cpu = TimeSpan.FromMilliseconds traced
            MaxRssBytes = 1L
            ExitCode = 0
            Output = ""
            Wall = TimeSpan.Zero
            LiveDescendants = 0
            DumpBytes = 0L
            DumpFiles = 0 } ]

/// Measurements that record their calls and return the given results.
let private mutationReport: Result<Mutation.MutationReport, string> =
    Ok(
        Mutation.summarize
            3
            []
            1
            [ { Target = "Lib.A::f"
                Outcome = Mutation.Measured
                Failing = [ "t1"; "t2" ]
                Selected = 2
                Missed = [ "t2" ] } ]
    )

let private fake audit overhead census =
    let calls = ResizeArray<Call>()

    let callChanged req args sample seed reps only changed timeout =
        calls.Add
            { Request = req
              AppArgs = args
              Sample = sample
              Seed = seed
              Reps = reps
              Only = only
              Changed = changed
              Timeout = timeout }

    let callOnly req args sample seed reps only timeout =
        callChanged req args sample seed reps only None timeout

    let call req args sample seed reps timeout =
        callOnly req args sample seed reps [] timeout

    let m: Program.Measures =
        { Audit =
            fun req args sample seed timeout ->
                call req args sample seed 0 timeout
                audit
          Overhead =
            fun req args reps timeout ->
                call req args 0.0 0 reps timeout
                overhead
          FileCensus =
            fun req args timeout ->
                call req args 0.0 0 0 timeout
                census
          Mutate =
            fun req args samples seed only changed timeout ->
                callChanged req args 0.0 seed samples only changed timeout
                mutationReport }

    m, calls

let private passing () =
    fake
        (Ok(auditReport []))
        (Ok(overheadReport 100.0))
        (Ok(FileCensus.summarize Set.empty Set.empty Set.empty Set.empty))

let private cwd = Path.Combine(Path.GetTempPath(), "tp-cli-cwd")

[<Fact>]
let ``audit defaults: repository is the working directory, 1 % sample, seed 1, 30 minutes`` () =
    let m, calls = passing ()

    let o =
        Program.runWith m cwd [ "audit"; "--project-dir"; "tests/P"; "--assembly"; "P" ]

    test <@ o.Exit = 0 && o.Stderr = "" @>
    test <@ o.Stdout = Audit.render (auditReport []) @>
    let c = calls |> Seq.exactlyOne

    test
        <@
            c.Request.RepoRoot = cwd
            && c.Request.ProjectDir = Path.Combine(cwd, "tests", "P")
        @>

    test <@ c.Request.AssemblyName = "P" && c.Request.TestProject = "P" @>

    test
        <@
            c.Request.WeaveTests = Model.SitesOnly
            && c.Request.VerifyTimeout = TimeSpan.FromMinutes 30.0
        @>

    test <@ (c.Sample, c.Seed, c.Timeout, c.AppArgs) = (0.01, 1, TimeSpan.FromMinutes 30.0, []) @>
    // The run directory is scratch: removed once the verb finishes.
    test <@ not (Directory.Exists c.Request.RunDir) @>

[<Fact>]
let ``--keep runs a verb in the given directory and leaves it for inspection`` () =
    let m, calls = passing ()

    let keep =
        Path.Combine(Path.GetTempPath(), "tp-cli-keep-" + Guid.NewGuid().ToString "N")

    try
        let o =
            Program.runWith m cwd [ "file-census"; "--project-dir"; "tests/P"; "--assembly"; "P"; "--keep"; keep ]

        test <@ o.Exit = 0 && o.Stderr = $"kept the run directory %s{keep}\n" @>
        test <@ (calls |> Seq.exactlyOne).Request.RunDir = keep && Directory.Exists keep @>
    finally
        Directory.Delete(keep, true)

[<Fact>]
let ``audit takes its flags and passes everything after -- to the app`` () =
    let m, calls = passing ()

    let o =
        Program.runWith
            m
            cwd
            [ "audit"
              "--project-dir"
              "/abs/P"
              "--assembly"
              "P"
              "--repo"
              "/abs"
              "--sample"
              "0.5"
              "--seed"
              "3"
              "--timeout-min"
              "2.5"
              "--"
              "--filter-class"
              "X" ]

    test <@ o.Exit = 0 @>
    let c = calls |> Seq.exactlyOne
    test <@ c.Request.RepoRoot = "/abs" && c.Request.ProjectDir = "/abs/P" @>
    test <@ (c.Sample, c.Seed, c.Timeout) = (0.5, 3, TimeSpan.FromMinutes 2.5) @>
    test <@ c.AppArgs = [ "--filter-class"; "X" ] @>

[<Fact>]
let ``a failing audit exits 1 and --json prints the report as JSON`` () =
    let m, _ = fake (Ok(auditReport [ 4 ])) (Ok(overheadReport 100.0)) (Error "unused")

    let o =
        Program.runWith m cwd [ "audit"; "--project-dir"; "P"; "--assembly"; "P"; "--json" ]

    test <@ o.Exit = 1 @>

    use doc = JsonDocument.Parse o.Stdout
    test <@ doc.RootElement.GetProperty("ExtraTotal").GetInt32() = 1 @>

[<Fact>]
let ``overhead takes --reps and fails over the bar`` () =
    let m, calls = fake (Error "unused") (Ok(overheadReport 200.0)) (Error "unused")

    let o =
        Program.runWith m cwd [ "overhead"; "--project-dir"; "P"; "--assembly"; "P"; "--reps"; "5" ]

    test <@ o.Exit = 1 && o.Stdout = Overhead.render (overheadReport 200.0) @>
    test <@ (calls |> Seq.exactlyOne).Reps = 5 @>

[<Fact>]
let ``overhead defaults to 3 reps and its JSON survives a ratio that is not a number`` () =
    let nan =
        { overheadReport 100.0 with
            Ratio = Double.NaN }

    let m, calls = fake (Error "unused") (Ok nan) (Error "unused")

    let o =
        Program.runWith m cwd [ "overhead"; "--project-dir"; "P"; "--assembly"; "P"; "--json" ]

    test <@ (calls |> Seq.exactlyOne).Reps = 3 @>
    test <@ o.Exit = 1 && o.Stdout.Contains "\"Ratio\":\"NaN\"" @>

[<Fact>]
let ``file-census prints its table and exits 0 when it passes`` () =
    let m, _ = passing ()

    let o =
        Program.runWith m cwd [ "file-census"; "--project-dir"; "P"; "--assembly"; "P" ]

    test
        <@
            o.Exit = 0
            && o.Stdout = FileCensus.render (FileCensus.summarize Set.empty Set.empty Set.empty Set.empty)
        @>

[<Fact>]
let ``a measurement that cannot run exits 2 with its reason`` () =
    let m, _ =
        fake (Ok(auditReport [])) (Ok(overheadReport 100.0)) (Error "no build output under x")

    let o =
        Program.runWith m cwd [ "file-census"; "--project-dir"; "P"; "--assembly"; "P" ]

    test <@ o.Exit = 2 && o.Stdout = "" && o.Stderr = "no build output under x\n" @>

[<Fact>]
let ``overhead on Windows exits 2 naming why`` () =
    let m, _ = passing ()

    let windows =
        { m with
            Overhead = Overhead.runWith true (fun () -> failwith "read on Windows") (fun () -> failwith "ps on Windows") }

    let o =
        Program.runWith windows cwd [ "overhead"; "--project-dir"; "P"; "--assembly"; "P" ]

    test
        <@
            o.Exit = 2
            && o.Stdout = ""
            && o.Stderr = "cannot measure CPU overhead: getrusage is macOS/Linux only\n"
        @>

[<Fact>]
let ``usage errors exit 2 with the verb's usage`` () =
    let m, calls = passing ()
    let err args = Program.runWith m cwd args

    let missing = err [ "audit"; "--assembly"; "P" ]

    test
        <@
            missing.Exit = 2
            && missing.Stderr.StartsWith "--project-dir and --assembly are required\nusage: test-prune-traces audit"
        @>

    let wrongVerb =
        err [ "audit"; "--project-dir"; "P"; "--assembly"; "P"; "--reps"; "2" ]

    test <@ wrongVerb.Exit = 2 && wrongVerb.Stderr.StartsWith "unknown argument --reps\n" @>

    let lastUnknown = err [ "file-census"; "--bogus" ]

    test
        <@
            lastUnknown.Exit = 2
            && lastUnknown.Stderr.StartsWith "unknown argument --bogus\n"
        @>

    let noValue = err [ "overhead"; "--project-dir" ]

    test
        <@
            noValue.Exit = 2
            && noValue.Stderr.StartsWith "--project-dir needs a value\nusage: test-prune-traces overhead"
        @>

    let flagAsValue = err [ "file-census"; "--project-dir"; "--json" ]
    test <@ flagAsValue.Stderr.StartsWith "--project-dir needs a value\nusage: test-prune-traces file-census" @>

    let notNumber = err [ "audit"; "--sample"; "lots" ]

    test
        <@
            notNumber.Exit = 2
            && notNumber.Stderr.StartsWith "--sample needs a positive number, got lots\n"
        @>

    let notInt = err [ "overhead"; "--reps"; "2.5" ]
    test <@ notInt.Stderr.StartsWith "--reps needs a positive whole number, got 2.5\n" @>

    test <@ calls.Count = 0 @>

[<Fact>]
let ``--weave-tests takes sites or full and nothing else`` () =
    let m, calls = passing ()

    let weave value =
        Program.runWith m cwd [ "overhead"; "--project-dir"; "P"; "--assembly"; "P"; "--weave-tests"; value ]

    test <@ (weave "sites").Exit = 0 @>
    test <@ (calls |> Seq.exactlyOne).Request.WeaveTests = Model.SitesOnly @>

    let bad = weave "all"

    test
        <@
            bad.Exit = 2
            && bad.Stderr.StartsWith "--weave-tests needs sites or full, got all\nusage: test-prune-traces overhead"
        @>

    let missing =
        Program.runWith m cwd [ "audit"; "--project-dir"; "P"; "--assembly"; "P"; "--weave-tests" ]

    test <@ missing.Exit = 2 && missing.Stderr.StartsWith "--weave-tests needs a value\n" @>
    test <@ calls.Count = 1 @>

[<Fact>]
let ``--weave-tests full reaches every verb's request`` () =
    let m, calls = passing ()

    for verb in [ "audit"; "overhead"; "file-census" ] do
        let o =
            Program.runWith m cwd [ verb; "--project-dir"; "P"; "--assembly"; "P"; "--weave-tests"; "full" ]

        test <@ o.Exit = 0 @>

    test <@ calls |> Seq.map (fun c -> c.Request.WeaveTests) |> List.ofSeq = List.replicate 3 Model.Full @>

[<Fact>]
let ``the real measurements are wired to the verbs`` () =
    let empty = Directory.CreateTempSubdirectory("tp-cli-none-").FullName

    for verb in [ "audit"; "overhead"; "file-census" ] do
        let o = Program.run empty [ verb; "--project-dir"; "."; "--assembly"; "P" ]
        test <@ o.Exit = 2 && o.Stderr.StartsWith "no build output under " @>

[<Fact>]
let ``the general usage names every verb`` () =
    let o = Program.run cwd [ "nope" ]
    test <@ o.Exit = 2 && o.Stderr = Program.usage + "\n" @>

[<Fact>]
let ``mutate defaults to 20 samples, seed 1, no target filter, and exits 1 on a miss`` () =
    let m, calls = passing ()

    let o = Program.runWith m cwd [ "mutate"; "--project-dir"; "P"; "--assembly"; "P" ]

    test <@ o.Exit = 1 @>
    test <@ o.Stdout = Mutation.render (Result.defaultWith failwith mutationReport) @>
    let c = calls |> Seq.exactlyOne
    test <@ (c.Reps, c.Seed, c.Only) = (20, 1, []) @>

[<Fact>]
let ``mutate takes --samples and repeated --only`` () =
    let m, calls = passing ()

    let o =
        Program.runWith
            m
            cwd
            [ "mutate"
              "--project-dir"
              "P"
              "--assembly"
              "P"
              "--samples"
              "5"
              "--only"
              "Lib.A"
              "--only"
              "Lib.B" ]

    test <@ o.Exit = 1 @>
    let c = calls |> Seq.exactlyOne
    test <@ (c.Reps, c.Only) = (5, [ "Lib.A"; "Lib.B" ]) @>

[<Theory>]
[<InlineData("mutate", "--seed", "x", "--seed needs a whole number, got x")>]
[<InlineData("mutate", "--samples", "0", "--samples needs a positive whole number, got 0")>]
[<InlineData("mutate", "--samples", "-3", "--samples needs a positive whole number, got -3")>]
[<InlineData("overhead", "--reps", "0", "--reps needs a positive whole number, got 0")>]
[<InlineData("audit", "--sample", "0", "--sample needs a positive number, got 0")>]
[<InlineData("audit", "--sample", "NaN", "--sample needs a positive number, got NaN")>]
[<InlineData("file-census", "--timeout-min", "-1", "--timeout-min needs a positive number, got -1")>]
[<InlineData("file-census", "--timeout-min", "Infinity", "--timeout-min needs a positive number, got Infinity")>]
let ``a count, fraction or timeout that is not positive is refused before anything runs``
    (verb: string, flag: string, value: string, message: string)
    =
    let m, calls = passing ()

    let o =
        Program.runWith m cwd [ verb; "--project-dir"; "P"; "--assembly"; "P"; flag; value ]

    test <@ o.Exit = 2 && o.Stderr.StartsWith(message + "\n") @>
    test <@ calls.Count = 0 @>

[<Fact>]
let ``mutate --json prints the report as JSON, each sample's outcome by name`` () =
    let m, _ = passing ()

    let o =
        Program.runWith m cwd [ "mutate"; "--project-dir"; "P"; "--assembly"; "P"; "--json" ]

    test <@ o.Exit = 1 @>

    use doc = JsonDocument.Parse o.Stdout
    let sample = doc.RootElement.GetProperty("Samples").[0]
    let outcome = sample.GetProperty("Outcome").GetString()
    let missed = sample.GetProperty("Missed").[0].GetString()
    let missedTotal = doc.RootElement.GetProperty("MissedTotal").GetInt32()

    test <@ (outcome, missed, missedTotal) = ("measured", "t2", 1) @>

// --- shadow-report (AUTOMATION-975, plan Task 8) ----------------------------------------

/// A store holding `rows` and `samples`, deleted after `f`.
let private withShadowStore (rows: TraceStore.ShadowRow list) (samples: TraceStore.MutationSample list) f =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-shadow-report-" + Guid.NewGuid().ToString "N" + ".db")

    try
        do
            use store = TraceStore.Store.Open path

            for r in rows do
                store.RecordShadow r

            for s in samples do
                store.RecordMutationSample s

        f path
    finally

        for p in [ path; path + "-wal"; path + "-shm" ] do
            File.Delete p

[<Fact>]
let ``shadow-report over a green window exits 0 and prints every bar`` () =
    withShadowStore ShadowReportTests.greenRows ShadowReportTests.greenSamples (fun db ->
        let o = Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; db ]
        test <@ o.Exit = 0 && o.Stderr = "" @>
        test <@ o.Stdout.Contains "| window |" && o.Stdout.Contains "| payoff |" @>)

[<Fact>]
let ``shadow-report exits 1 when a bar fails, and on a project with no rows`` () =
    withShadowStore ShadowReportTests.greenRows [] (fun db ->
        let o = Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; db ]
        test <@ o.Exit = 1 && o.Stdout.Contains "| mutation |" @>
        let other = Program.run cwd [ "shadow-report"; "--project"; "Q"; "--db"; db ]
        test <@ other.Exit = 1 @>)

[<Fact>]
let ``shadow-report reads every --db and counts a run once`` () =
    let first, second = ShadowReportTests.greenRows |> List.splitAt 70
    // Both stores hold i60..i69: the same runs, recorded twice, count once.
    let overlap = first |> List.skip 60

    withShadowStore first ShadowReportTests.greenSamples (fun a ->
        withShadowStore (overlap @ second) [] (fun b ->
            let one = Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; a ]
            test <@ one.Exit = 1 @>

            let both =
                Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; a; "--db"; b; "--json" ]

            test <@ both.Exit = 0 @>
            use doc = JsonDocument.Parse both.Stdout
            test <@ doc.RootElement.GetProperty("rows").GetInt32() = 132 @>
            test <@ doc.RootElement.GetProperty("passes").GetBoolean() @>))

[<Fact>]
let ``shadow-report --since leaves out older rows`` () =
    withShadowStore ShadowReportTests.greenRows ShadowReportTests.greenSamples (fun db ->
        let o =
            Program.run
                cwd
                [ "shadow-report"
                  "--project"
                  "P"
                  "--db"
                  db
                  "--since"
                  "2026-10-10"
                  "--json" ]

        use doc = JsonDocument.Parse o.Stdout
        test <@ o.Exit = 1 && doc.RootElement.GetProperty("rows").GetInt32() < 132 @>)

[<Fact>]
let ``shadow-report usage errors and an unreadable store exit 2`` () =
    let noDb = Program.run cwd [ "shadow-report"; "--project"; "P" ]
    test <@ noDb.Exit = 2 && noDb.Stderr.Contains "usage: test-prune-traces shadow-report" @>

    let noProject = Program.run cwd [ "shadow-report"; "--db"; "x.db" ]
    test <@ noProject.Exit = 2 @>

    let badSince =
        Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; "x.db"; "--since"; "soon" ]

    test <@ badSince.Exit = 2 && badSince.Stderr.StartsWith "--since needs " @>

    let dir = Directory.CreateTempSubdirectory("tp-shadow-bad-").FullName

    try
        let bad = Path.Combine(dir, "bad.db")
        File.WriteAllText(bad, "not a database")
        let o = Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; bad ]
        test <@ o.Exit = 2 && o.Stderr.Contains "bad.db" @>

        let missing =
            Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; Path.Combine(dir, "none.db") ]

        test <@ missing.Exit = 2 && missing.Stderr.Contains "none.db" @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``shadow-report --since takes a number of days`` () =
    withShadowStore ShadowReportTests.greenRows ShadowReportTests.greenSamples (fun db ->
        // Every green row is from October 2026, long before 3 days ago.
        let o =
            Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; db; "--since"; "3d"; "--json" ]

        use doc = JsonDocument.Parse o.Stdout
        test <@ o.Exit = 1 && doc.RootElement.GetProperty("rows").GetInt32() = 0 @>)

[<Theory>]
[<InlineData("--since", "xd", "--since needs <n>d or an ISO date, got xd")>]
[<InlineData("--project", "--json", "--project needs a value")>]
[<InlineData("--db", "--json", "--db needs a value")>]
[<InlineData("--bogus", "x", "unknown argument --bogus")>]
[<InlineData("--since", "--json", "--since needs a value")>]
let ``shadow-report refuses a flag without its value and an unknown flag``
    (flag: string, value: string, message: string)
    =
    let o = Program.run cwd [ "shadow-report"; flag; value ]
    test <@ o.Exit = 2 && o.Stderr.StartsWith(message + "\n") @>

[<Fact>]
let ``shadow-report on a store a newer trace schema wrote exits 2 and names it`` () =
    let dir = Directory.CreateTempSubdirectory("tp-shadow-newer-").FullName

    try
        let path = Path.Combine(dir, "newer.db")

        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{path}")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "PRAGMA user_version = 999;"
            cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

        let o = Program.run cwd [ "shadow-report"; "--project"; "P"; "--db"; path ]
        test <@ o.Exit = 2 && o.Stderr.Contains "newer.db" && o.Stderr.Contains "v999" @>
    finally
        Directory.Delete(dir, true)

[<Theory>]
[<InlineData("--project")>]
[<InlineData("--db")>]
[<InlineData("--since")>]
let ``shadow-report refuses a valued flag at the end of the line`` (flag: string) =
    let o = Program.run cwd [ "shadow-report"; flag ]
    test <@ o.Exit = 2 && o.Stderr.StartsWith(flag + " needs a value\n") @>

// --- mutate over a real change, stored (AUTOMATION-975, plan Task 7) ----------------------

[<Fact>]
let ``mutate --diff samples the methods a change touched and --db stores every sample`` () =
    let dir = Directory.CreateTempSubdirectory("tp-mutate-store-").FullName

    try
        let diffFile = Path.Combine(dir, "change.diff")

        File.WriteAllText(
            diffFile,
            "diff --git a/src/A.fs b/src/A.fs\n--- a/src/A.fs\n+++ b/src/A.fs\n@@ -2 +2 @@\n-x\n+y\n"
        )

        let db = Path.Combine(dir, "shadow.db")
        let m, calls = passing ()

        let o =
            Program.runWith
                m
                dir
                [ "mutate"
                  "--project-dir"
                  "P"
                  "--assembly"
                  "P"
                  "--diff"
                  "change.diff"
                  "--db"
                  "shadow.db"
                  "--project"
                  "P.Tests" ]

        test <@ o.Exit = 1 @>
        test <@ (calls |> Seq.exactlyOne).Changed = Some(Map [ "src/A.fs", [ 2, 2 ] ]) @>

        use store = TraceStore.Store.Open db
        let stored = store.MutationSamples "P.Tests"

        test
            <@
                stored |> List.map (fun s -> s.Symbol, s.Outcome, s.Missed) = [ "Lib.A::f",
                                                                                TraceStore.Measured,
                                                                                [ "t2" ] ]
            @>

        test <@ stored |> List.forall (fun s -> s.TreeHash.StartsWith "diff:") @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``mutate without --diff samples every hit method and stores under the assembly's name`` () =
    let dir = Directory.CreateTempSubdirectory("tp-mutate-store2-").FullName

    try
        let m, calls = passing ()

        let o =
            Program.runWith m dir [ "mutate"; "--project-dir"; "P"; "--assembly"; "P"; "--db"; "s.db" ]

        test <@ o.Exit = 1 && (calls |> Seq.exactlyOne).Changed = None @>
        use store = TraceStore.Store.Open(Path.Combine(dir, "s.db"))
        test <@ (store.MutationSamples "P" |> List.exactlyOne).TreeHash = "" @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``mutate with a --diff it cannot read exits 2 before anything runs`` () =
    let m, calls = passing ()

    let o =
        Program.runWith
            m
            cwd
            [ "mutate"
              "--project-dir"
              "P"
              "--assembly"
              "P"
              "--diff"
              "/nowhere/x.diff" ]

    test <@ o.Exit = 2 && o.Stderr.Contains "x.diff" && calls.Count = 0 @>

[<Fact>]
let ``mutate with a --db it cannot write exits 2 and names it`` () =
    let dir = Directory.CreateTempSubdirectory("tp-mutate-bad-db-").FullName

    try
        File.WriteAllText(Path.Combine(dir, "bad.db"), "not a database")
        let m, _ = passing ()

        let o =
            Program.runWith m dir [ "mutate"; "--project-dir"; "P"; "--assembly"; "P"; "--db"; "bad.db" ]

        test <@ o.Exit = 2 && o.Stderr.StartsWith "--db bad.db: " @>
    finally
        Directory.Delete(dir, true)
