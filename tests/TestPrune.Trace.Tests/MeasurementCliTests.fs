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
      Timeout: TimeSpan }

let private auditReport extra : Audit.AuditReport =
    Audit.summarize
        [ { Display = "N.C.m"
            Extra = extra
            MissingByKind = Map.empty
            MissingUser = []
            IsolatedFound = true } ]
        Map.empty

let private overheadReport (traced: float) : Overhead.OverheadReport =
    Overhead.summarize
        [ { Traced = false
            Cpu = TimeSpan.FromMilliseconds 100.0
            MaxRssBytes = 1L
            ExitCode = 0 }
          { Traced = true
            Cpu = TimeSpan.FromMilliseconds traced
            MaxRssBytes = 1L
            ExitCode = 0 } ]

/// Measurements that record their calls and return the given results.
let private fake audit overhead census =
    let calls = ResizeArray<Call>()

    let call req args sample seed reps timeout =
        calls.Add
            { Request = req
              AppArgs = args
              Sample = sample
              Seed = seed
              Reps = reps
              Timeout = timeout }

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
                census }

    m, calls

let private passing () =
    fake (Ok(auditReport [])) (Ok(overheadReport 100.0)) (Ok(FileCensus.summarize Set.empty Set.empty Set.empty))

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
            && o.Stdout = FileCensus.render (FileCensus.summarize Set.empty Set.empty Set.empty)
        @>

[<Fact>]
let ``a measurement that cannot run exits 2 with its reason`` () =
    let m, _ =
        fake (Ok(auditReport [])) (Ok(overheadReport 100.0)) (Error "no build output under x")

    let o =
        Program.runWith m cwd [ "file-census"; "--project-dir"; "P"; "--assembly"; "P" ]

    test <@ o.Exit = 2 && o.Stdout = "" && o.Stderr = "no build output under x\n" @>

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
            && notNumber.Stderr.StartsWith "--sample needs a number, got lots\n"
        @>

    let notInt = err [ "overhead"; "--reps"; "2.5" ]
    test <@ notInt.Stderr.StartsWith "--reps needs a whole number, got 2.5\n" @>

    test <@ calls.Count = 0 @>

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
