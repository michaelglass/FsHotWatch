/// The process-driving measurements, run against the FxTests fixture: each test prepares
/// its own scratch copy of the fixture's build output and launches the real apphosts.
module TestPrune.Trace.Tests.MeasurementTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Tests

let private request projectDir : TraceSession.PrepareRequest =
    { RepoRoot = Fixtures.repoRoot
      ProjectDir = projectDir
      AssemblyName = "FxTests"
      TestProject = "FxTests"
      WeaveTests = SitesOnly
      RunDir = Directory.CreateTempSubdirectory("tp-measure-").FullName
      VerifyTimeout = TimeSpan.FromMinutes 2.0 }

let private withScratch (f: TraceSession.PrepareRequest -> unit) =
    let projectDir = ShadowBinTests.scratchProject ()

    try
        f (request projectDir)
    finally
        ShadowBinTests.deleteProject projectDir

let private ok (r: Result<'a, string>) = r |> Result.defaultWith failwith

let private timeout = TimeSpan.FromMinutes 5.0

let private noBuildOutput () =
    request (Directory.CreateTempSubdirectory("tp-measure-none-").FullName)

[<Fact>]
let ``getrusage sees a child's CPU`` () =
    let struct (before, _) = Rusage.children ()

    Launch.run "/bin/sh" [ "-c"; "i=0; while [ $i -lt 200000 ]; do i=$((i+1)); done" ] [] "/" timeout
    |> ignore

    let struct (after, rss) = Rusage.children ()
    test <@ after > before && rss > 0L @>

[<Fact>]
let ``a getrusage that fails before a launch launches nothing, and after one is an error too`` () =
    let marker =
        Path.Combine(Path.GetTempPath(), "tp-measure-" + Guid.NewGuid().ToString "N")

    let launch read =
        Overhead.measure read false "/bin/sh" [ "-c"; $"touch '%s{marker}'" ] [] "/" timeout

    let failing () : struct (TimeSpan * int64) = invalidOp "getrusage failed (errno 38)"

    let expected: Result<Overhead.Sample, string> =
        Error "cannot measure CPU overhead: getrusage failed: getrusage failed (errno 38)"

    test <@ launch failing = expected && not (File.Exists marker) @>

    let reads = ref 0

    let secondFails () =
        reads.Value <- reads.Value + 1

        if reads.Value = 1 then
            struct (TimeSpan.Zero, 0L)
        else
            failing ()

    try
        test <@ launch secondFails = expected && File.Exists marker @>
    finally
        File.Delete marker

[<Fact>]
let ``the isolation audit finds no id attributed in parallel that the test does not run alone`` () =
    withScratch (fun req ->
        let report = Audit.run req [] 1.0 7 timeout |> ok
        test <@ report.Sampled = 9 @>
        test <@ report.ExtraTotal = 0 @>
        test <@ report.Tests |> List.forall (fun t -> t.IsolatedFound) @>
        test <@ report.Incomplete = Map [ "child-process-untraced:echo", 1 ] @>
        test <@ Audit.passes report @>)

[<Fact>]
let ``overhead reports medians and a ratio over interleaved runs`` () =
    withScratch (fun req ->
        let r = Overhead.run req [] 1 timeout |> ok

        test <@ r.Samples |> List.map (fun s -> s.Traced) = [ false; true ] @>
        test <@ r.Samples |> List.forall (fun s -> s.ExitCode = 0) @>
        test <@ r.BaseMedianCpu > TimeSpan.Zero && r.Ratio > 0.0 @>)

[<Fact>]
let ``the file census names the reading test and the tests that break outside the repository`` () =
    // FxTests' one repository read is guarded by TESTPRUNE_TRACE_REPO_ROOT: traced (in the
    // repository) it reads global.json; untraced outside the repository it skips the read
    // and passes. So the census must report the reader and no outside failures. This pins
    // both halves of the measurement, not a ratio the fixture cannot make meaningful.
    withScratch (fun req ->
        let r = FileCensus.run req [] timeout |> ok
        test <@ r.ReadsRepo = set [ "FxTests.AttributionTests.ClassC.c reads a repo file" ] @>
        test <@ r.FailOutside = Set.empty && r.FailInRepo = Set.empty @>)

[<Fact>]
let ``the file census refuses a run that wrote no CTRF report`` () =
    withScratch (fun req ->
        match FileCensus.run req [ "--list-tests" ] timeout with
        | Error why ->
            test <@ why.StartsWith "no CTRF report from the run in the repository (exit 0); its output ends:\n" @>
            // --list-tests prints the tests it would run: the tail shows what the app did.
            test <@ why.Contains "c starts a child process" @>
        | Ok r -> failwith $"measured a run with no report: %A{r}")

[<Fact>]
let ``every measurement refuses a project it cannot prepare`` () =
    let errorOf (r: Result<'a, string>) =
        match r with
        | Error e -> e
        | Ok _ -> ""

    let audit = errorOf (Audit.run (noBuildOutput ()) [] 1.0 1 timeout)
    let overhead = errorOf (Overhead.run (noBuildOutput ()) [] 1 timeout)
    let census = errorOf (FileCensus.run (noBuildOutput ()) [] timeout)

    test
        <@
            [ audit; overhead; census ]
            |> List.forall (fun e -> e.StartsWith "no build output under ")
        @>

[<Fact>]
let ``overhead refuses fewer than one rep before preparing anything`` () =
    test <@ Overhead.run (noBuildOutput ()) [] 0 timeout = Error "reps must be at least 1, got 0" @>
