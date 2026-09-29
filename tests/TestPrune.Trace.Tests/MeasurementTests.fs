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
let ``a sample that exits nonzero keeps its output, and the overhead table prints it`` () =
    let noCpu () = struct (TimeSpan.Zero, 0L)

    let launch traced script =
        Overhead.measure noCpu traced "/bin/sh" [ "-c"; script ] [] "/" timeout |> ok

    let failing = launch true "echo 'failed N.C.t'; echo boom >&2; exit 2"
    let passing = launch false "echo fine"

    test <@ failing.ExitCode = 2 && failing.Output = "failed N.C.t\nboom\n" @>
    // A passing launch's output says nothing the report needs.
    test <@ passing.ExitCode = 0 && passing.Output = "" @>

    let text = Overhead.render (Overhead.summarize [ passing; failing ])

    test
        <@
            text.EndsWith(
                String.concat
                    "\n"
                    [ $"  sample untraced  0 ms  wall %.0f{passing.Wall.TotalMilliseconds} ms  live 0  exit 0"
                      $"  sample traced    0 ms  wall %.0f{failing.Wall.TotalMilliseconds} ms  live 0  exit 2"
                      "    output ends:"
                      "      failed N.C.t"
                      "      boom"
                      "" ]
            )
        @>

[<Fact>]
let ``the isolation audit finds no id attributed in parallel that the test does not run alone`` () =
    withScratch (fun req ->
        let report = Audit.run req [] 1.0 7 timeout |> ok
        test <@ report.Sampled = 14 @>
        test <@ report.ExtraTotal = 0 @>

        test
            <@
                report.Tests
                |> List.forall (fun t -> t.IsolatedFound && t.IsolationError.IsNone)
            @>
        // Rows whose names hold a filter wildcard, parentheses and quotes are each run alone.
        test
            <@
                report.Tests
                |> List.filter (fun t -> t.Display.Contains "b theory row names")
                |> List.map (fun t -> t.Display.Contains "*") = [ true; true ]
            @>

        test <@ report.Incomplete = Map [ "child-process-untraced:echo", 1 ] @>
        test <@ Audit.passes report @>)

[<Fact>]
let ``a sampled test the isolated run cannot select is an audit error`` () =
    withScratch (fun req ->
        let launch = TraceSession.prepareProject req |> ok
        let dir name = Path.Combine(req.RunDir, name)
        let parallelIds = Map [ "No.Such.test", set [ 1 ] ]

        let alone args display =
            Audit.auditAlone launch req.RepoRoot Map.empty parallelIds args timeout (dir display) display

        let unmatched = alone [] "No.Such.test"

        test <@ unmatched.IsolationError = Some "could not isolate No.Such.test: the filter selected nothing" @>
        test <@ List.isEmpty unmatched.Extra @>

        // A run that ends before it reports (an option the app rejects) is an error too,
        // even in a directory where an earlier run left its report.
        let crashed = alone [ "--no-such-option" ] "No.Such.test"

        test
            <@
                crashed.IsolationError
                |> Option.exists (fun e ->
                    e.StartsWith "could not isolate No.Such.test: no CTRF report from the run alone (exit ")
            @>)

[<Fact>]
let ``overhead reports medians and a ratio over interleaved runs`` () =
    withScratch (fun req ->
        let r = Overhead.run req [] 1 timeout |> ok

        test <@ r.Samples |> List.map (fun s -> s.Traced) = [ false; true ] @>
        test <@ r.Samples |> List.forall (fun s -> s.ExitCode = 0) @>
        test <@ r.BaseMedianCpu > TimeSpan.Zero && r.Ratio > 0.0 @>)

[<Fact>]
let ``a child still running is a live descendant, and one that exited is not`` () =
    let child () =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("/bin/sleep", "30", UseShellExecute = false))

    let live () =
        Overhead.descendantsOf Environment.ProcessId (Overhead.processTable ())

    use p = child ()

    try
        test <@ (live ()).Contains p.Id @>
        test <@ Overhead.liveDescendants () >= 1 @>
    finally
        p.Kill()
        p.WaitForExit()

    test <@ not ((live ()).Contains p.Id) @>

/// A traced launch dumps into the run's dump directory. Were it not emptied first, every
/// later launch would start with the earlier launches' dumps there, and the dumps measured
/// after rep N would be N reps' worth.
[<Fact>]
let ``each traced rep dumps its own work into an emptied directory, and the work does not grow`` () =
    withScratch (fun req ->
        let r = Overhead.run req [] 3 timeout |> ok

        let dumps =
            r.Samples |> List.filter (fun s -> s.Traced) |> List.map (fun s -> s.DumpBytes)

        test <@ dumps.Length = 3 && List.min dumps > 0L @>

        test
            <@
                r.Samples
                |> List.filter (fun s -> s.Traced)
                |> List.forall (fun s -> s.DumpFiles >= 1)
            @>

        test
            <@
                r.Samples
                |> List.forall (fun s -> s.Wall > TimeSpan.Zero && s.LiveDescendants >= 0)
            @>
        // The header's pid, CPU time and counters vary by a few bytes; a leftover rep's dumps
        // would at least double the total.
        test <@ float (List.max dumps) <= 1.2 * float (List.min dumps) @>

        test
            <@
                r.Samples
                |> List.filter (fun s -> not s.Traced)
                |> List.forall (fun s -> s.DumpBytes = 0L && s.DumpFiles = 0)
            @>

        test <@ (Overhead.render r).Contains " KiB in " @>)

[<Fact>]
let ``the file census names the reading tests, those that break outside the repository and those that reach it from there``
    ()
    =
    // "d reads a module value" reads mise.toml in a type initializer it found by walking up
    // from the binary: outside the repository that walk fails, and so does the test. "c reads
    // a repo file" reads global.json at an absolute path (the recorder's repository root):
    // from outside it still reaches the repository and passes. Both traces hold the read.
    // The test project's own module value reads LICENSE at startup; the test assembly is
    // woven without method probes, so nothing places that initializer and every test
    // inherits the read. Only the two "e" tests use it and fail outside; the census reports
    // the rest as reading the repository while passing outside it.
    withScratch (fun req ->
        let r = FileCensus.run req [] timeout |> ok
        let t (name: string) = "FxTests.AttributionTests." + name

        let users =
            set
                [ t "ClassD.d reads a module value"
                  t "ClassE.e first licence reader"
                  t "ClassE.e second licence reader" ]

        test <@ r.ReadsRepo.Count = 12 && Set.isSubset users r.ReadsRepo @>
        test <@ r.FailOutside = users @>
        test <@ r.ReachOutside = set [ t "ClassC.c reads a repo file" ] @>
        test <@ r.FailInRepo = Set.empty @>

        test
            <@
                r.SymmetricDifference = Set.difference r.ReadsRepo (Set.add (t "ClassC.c reads a repo file") users)
                && r.SymmetricDifference.Count = 8
            @>)

[<Fact>]
let ``the file census refuses a run that wrote no CTRF report`` () =
    withScratch (fun req ->
        match FileCensus.run req [ "--list-tests" ] timeout with
        | Error why ->
            test <@ why.StartsWith "no CTRF report from the run in the repository (exit 0); its output ends:\n" @>
            // --list-tests prints the tests it would run: the tail shows what the app did.
            test <@ why.Contains "e second licence reader" @>
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
