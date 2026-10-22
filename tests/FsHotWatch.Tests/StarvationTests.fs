module FsHotWatch.Tests.StarvationTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune

let private at (seconds: float) =
    DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc).AddSeconds seconds

let private reading (cores: int) (loads: float list) : Starvation.Reading =
    { Cores = cores
      Samples =
        loads
        |> List.mapi (fun i load -> ({ At = at (float i * 5.0); Load = load }: Starvation.Sample)) }

let private everyClass = Starvation.Policy.rerunAlone []

/// 12 cores at load 30–90 — the 2026-10-20/21 nights the policy exists for.
let private starved = reading 12 [ 40.0; 65.0; 90.0; 70.0 ]

/// The same box idle.
let private quiet = reading 12 [ 3.0; 4.0; 2.5; 5.0 ]

let private timedOut =
    TestsTimedOut("killed", TimeSpan.FromSeconds 600.0, false, TimeSpan.FromSeconds 600.0)

let private ctrfOf (rows: (string * string * string * string) list) =
    let tests =
        rows
        |> List.map (fun (cls, meth, status, exceptionType) ->
            {| name = $"%s{cls}.%s{meth}"
               status = status
               message =
                if exceptionType = "Xunit.Sdk.TestTimeoutException" then
                    "Test execution timed out after 5000 milliseconds"
                else
                    "Assert.Equal() Failure"
               extra =
                {| ``type`` = cls
                   ``method`` = meth
                   ``exception`` = exceptionType |} |})

    let failed = rows |> List.filter (fun (_, _, s, _) -> s = "failed") |> List.length

    JsonSerializer.Serialize
        {| results =
            {| summary =
                {| tests = rows.Length
                   passed = rows.Length - failed
                   failed = failed
                   skipped = 0
                   pending = 0
                   other = 0 |}
               tests = tests |} |}

let private timeoutRow cls meth =
    cls, meth, "failed", "Xunit.Sdk.TestTimeoutException"

let private assertionRow cls meth =
    cls, meth, "failed", "Xunit.Sdk.EqualException"

let private passRow cls meth = cls, meth, "passed", ""

let private failedRed = TestsFailed("1 failed", false, TimeSpan.FromSeconds 30.0)

// ---------------------------------------------------------------------------------------
// Measuring starvation
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``a box at load 40 to 90 on 12 cores is starved under the default threshold`` () =
    test <@ Starvation.Reading.isStarved everyClass starved @>

[<Fact>]
let ``an idle box is not starved`` () =
    test <@ not (Starvation.Reading.isStarved everyClass quiet) @>

[<Fact>]
let ``no samples is never starvation: a platform with no load average cannot qualify`` () =
    test <@ not (Starvation.Reading.isStarved everyClass (reading 12 [])) @>

[<Fact>]
let ``starvation is MOST of the wall, not one spike`` () =
    // One sample over the threshold out of four: a spike, not a starved run.
    let spike = reading 12 [ 3.0; 4.0; 90.0; 5.0 ]
    test <@ not (Starvation.Reading.isStarved everyClass spike) @>
    // Half or more is.
    let half = reading 12 [ 3.0; 40.0; 90.0; 5.0 ]
    test <@ Starvation.Reading.isStarved everyClass half @>

[<Fact>]
let ``the description names the load, the cores and the share over the threshold`` () =
    let text = Starvation.Reading.describe everyClass starved
    test <@ text.Contains "12 cores" @>
    test <@ text.Contains "4 of 4" @>
    test <@ text.Contains "90.0" @>

// ---------------------------------------------------------------------------------------
// The decision
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``a project timeout under starvation is re-run alone`` () =
    match Starvation.decide (Some everyClass) None timedOut None starved with
    | Starvation.RerunAlone(Starvation.ProjectTimedOut after, _) -> test <@ after = TimeSpan.FromSeconds 600.0 @>
    | other -> failwith $"expected a rerun, got %A{other}"

[<Fact>]
let ``a project timeout on a quiet box is reported, and says it was not starvation`` () =
    match Starvation.decide (Some everyClass) None timedOut None quiet with
    | Starvation.Report(Some reason) -> test <@ reason.Contains "not starved" @>
    | other -> failwith $"expected a report, got %A{other}"

[<Fact>]
let ``without a declared policy nothing is ever re-run`` () =
    test <@ Starvation.decide None None timedOut None starved = Starvation.Report None @>

[<Fact>]
let ``per-test timeouts under starvation are re-run alone, naming the tests`` () =
    let json =
        ctrfOf
            [ timeoutRow "A.Slow" "one"
              passRow "A.Slow" "two"
              timeoutRow "B.Other" "three" ]

    match Starvation.decide (Some everyClass) None failedRed (Some json) starved with
    | Starvation.RerunAlone(Starvation.TestTimeouts names, _) -> test <@ names = [ "A.Slow.one"; "B.Other.three" ] @>
    | other -> failwith $"expected a rerun, got %A{other}"

[<Fact>]
let ``an assertion failure is never re-run, even beside timeouts on a starved box`` () =
    let json = ctrfOf [ timeoutRow "A.Slow" "one"; assertionRow "A.Slow" "two" ]

    match Starvation.decide (Some everyClass) None failedRed (Some json) starved with
    | Starvation.Report(Some reason) -> test <@ reason.Contains "A.Slow.two" @>
    | other -> failwith $"expected a report, got %A{other}"

[<Fact>]
let ``a red with no readable report is not re-run: only a report can show every failure was a timeout`` () =
    match Starvation.decide (Some everyClass) None failedRed None starved with
    | Starvation.Report(Some _) -> ()
    | other -> failwith $"expected a report, got %A{other}"

[<Fact>]
let ``a timeout in a class the policy does not declare is not re-run`` () =
    let policy = Starvation.Policy.rerunAlone [ "A.Slow" ]
    let json = ctrfOf [ timeoutRow "A.Slow" "one"; timeoutRow "B.Other" "three" ]

    match Starvation.decide (Some policy) None failedRed (Some json) starved with
    | Starvation.Report(Some reason) -> test <@ reason.Contains "B.Other" @>
    | other -> failwith $"expected a report, got %A{other}"

[<Fact>]
let ``a nested type counts as its declared class`` () =
    let policy = Starvation.Policy.rerunAlone [ "A.Slow" ]
    let json = ctrfOf [ timeoutRow "A.Slow+Inner" "one" ]

    match Starvation.decide (Some policy) None failedRed (Some json) starved with
    | Starvation.RerunAlone _ -> ()
    | other -> failwith $"expected a rerun, got %A{other}"

[<Fact>]
let ``a project timeout under a class-scoped policy qualifies only when the run selected declared classes alone`` () =
    let policy = Starvation.Policy.rerunAlone [ "A.Slow" ]
    // Unfiltered: the timeout cannot be pinned on the declared class.
    test
        <@
            Starvation.decide (Some policy) None timedOut None starved
            |> Starvation.isRerun
            |> not
        @>
    // Filtered to the declared class: it can.
    test
        <@
            Starvation.decide (Some policy) (Some [ "A.Slow" ]) timedOut None starved
            |> Starvation.isRerun
        @>
    // Filtered to more than it: it cannot.
    test
        <@
            Starvation.decide (Some policy) (Some [ "A.Slow"; "B.Other" ]) timedOut None starved
            |> Starvation.isRerun
            |> not
        @>

[<Fact>]
let ``a pass, an abort or a deferral is never re-run`` () =
    for result in
        [ TestsPassed("", false, TimeSpan.Zero)
          TestsErrored "killed by SIGKILL"
          TestsDeferred "apphost"
          TestsNoMatch("", TimeSpan.Zero) ] do
        test <@ Starvation.decide (Some everyClass) None result None starved = Starvation.Report None @>

// ---------------------------------------------------------------------------------------
// The sampler
// ---------------------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``the sampler reads at start and stop even for a run shorter than its interval`` () =
    let mutable reads = 0

    let read () =
        reads <- reads + 1
        Some 42.0

    let sampler = Starvation.Sampler.start (TimeSpan.FromHours 1.0) read
    let samples = Starvation.Sampler.stop sampler
    test <@ samples.Length = 2 @>
    test <@ samples |> List.forall (fun s -> s.Load = 42.0) @>

[<Fact(Timeout = 10000)>]
let ``a platform with no load average yields no samples`` () =
    let sampler = Starvation.Sampler.start (TimeSpan.FromHours 1.0) (fun () -> None)
    test <@ List.isEmpty (Starvation.Sampler.stop sampler) @>

// ---------------------------------------------------------------------------------------
// The record the verdict reads
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``the record round-trips, and says starved-then-passed-alone apart from a clean pass`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-starved-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore

    try
        let record: Starvation.Record =
            { Project = "Slow.Tests"
              TimedOut = Starvation.ProjectTimedOut(TimeSpan.FromSeconds 600.0)
              FirstAttempt = starved
              Rerun = quiet
              RerunOutcome = "passed"
              PassedAlone = true
              AttemptDir = Path.Combine(dir, Starvation.AttemptDirName) }

        Starvation.write everyClass dir record
        let stored = Starvation.tryRead (Starvation.pathIn dir "Slow.Tests")

        match stored with
        | Some s ->
            test <@ s.PassedAlone @>
            test <@ s.Summary.Contains "starved, then passed alone" @>
            test <@ s.FirstAttempt.Contains "600s" @>
            test <@ s.Load.Contains "12 cores" @>
        | None -> failwith "the record did not read back"
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a rerun that is still red says so`` () =
    let record: Starvation.Record =
        { Project = "Slow.Tests"
          TimedOut = Starvation.TestTimeouts [ "A.Slow.one" ]
          FirstAttempt = starved
          Rerun = quiet
          RerunOutcome = "failed"
          PassedAlone = false
          AttemptDir = "/r/starved-attempt" }

    let line = Starvation.summary record
    test <@ line.Contains "still red" @>
    test <@ line.Contains "A.Slow.one" @>

[<Fact>]
let ``setting the first attempt aside moves its artifacts out of the run directory's report set`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-starved-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore

    try
        File.WriteAllText(Path.Combine(dir, "P.ctrf.json"), "{}")
        File.WriteAllText(Path.Combine(dir, "P.output.log"), "log")
        File.WriteAllText(Path.Combine(dir, "Q.ctrf.json"), "{}")

        let moved =
            Starvation.setAside dir "P" [ ".ctrf.json"; ".output.log"; ".timed-out.json" ]

        test <@ moved.Length = 2 @>
        test <@ not (File.Exists(Path.Combine(dir, "P.ctrf.json"))) @>
        test <@ File.Exists(Path.Combine(dir, Starvation.AttemptDirName, "P.ctrf.json")) @>
        test <@ File.Exists(Path.Combine(dir, Starvation.AttemptDirName, "P.output.log")) @>
        // Another project's report is untouched.
        test <@ File.Exists(Path.Combine(dir, "Q.ctrf.json")) @>
    finally
        Directory.Delete(dir, true)

// ---------------------------------------------------------------------------------------
// The declaration
// ---------------------------------------------------------------------------------------

let private parse (json: string) =
    use doc = JsonDocument.Parse json
    Starvation.Policy.parse "tests.projects[P].starvation" doc.RootElement

[<Fact>]
let ``the declaration parses with defaults`` () =
    test <@ parse """{ "onTimeout": "rerun-alone" }""" = Ok everyClass @>

[<Fact>]
let ``the declaration takes classes and thresholds`` () =
    let parsed =
        parse """{ "onTimeout": "rerun-alone", "classes": ["A.Slow"], "loadPerCore": 1.5, "share": 0.75 }"""

    test
        <@
            parsed = Ok
                { Classes = [ "A.Slow" ]
                  LoadPerCore = 1.5
                  StarvedShare = 0.75 }
        @>

[<Fact>]
let ``the declaration refuses what it cannot honour`` () =
    for bad in
        [ """{ }"""
          """{ "onTimeout": "retry" }"""
          """{ "onTimeout": "rerun-alone", "retries": 3 }"""
          """{ "onTimeout": "rerun-alone", "loadPerCore": -1 }"""
          """{ "onTimeout": "rerun-alone", "share": 0 }"""
          """{ "onTimeout": "rerun-alone", "share": 1.5 }"""
          """{ "onTimeout": "rerun-alone", "classes": [""] }"""
          """ "rerun-alone" """ ] do
        test <@ Result.isError (parse bad) @>

// ---------------------------------------------------------------------------------------
// Reports and records that do not say enough
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``an unreadable report, or one with no test array, shows nothing`` () =
    test <@ Result.isError (Starvation.timeoutsOfReport everyClass "{ not json") @>
    test <@ Result.isError (Starvation.timeoutsOfReport everyClass """{"results": {"tests": 3}}""") @>
    test <@ Result.isError (Starvation.timeoutsOfReport everyClass "null") @>

[<Fact>]
let ``a report with no failed row names no timeout`` () =
    match Starvation.timeoutsOfReport everyClass (ctrfOf [ passRow "A.Slow" "one" ]) with
    | Error reason -> test <@ reason.Contains "no failed test" @>
    | Ok names -> failwith $"expected an error, got %A{names}"

[<Fact>]
let ``a top-level test array with no extra block is read by name, the class taken before the last dot`` () =
    let json =
        """{"tests": [{"name": "A.Slow.one", "status": "failed", "message": "Test execution timed out after 100 milliseconds"},
                      {"name": "A.Slow.two", "status": "passed"}]}"""

    test <@ Starvation.timeoutsOfReport (Starvation.Policy.rerunAlone [ "A.Slow" ]) json = Ok [ "A.Slow.one" ] @>
    test <@ Result.isError (Starvation.timeoutsOfReport (Starvation.Policy.rerunAlone [ "B.Other" ]) json) @>

[<Fact>]
let ``a class-scoped project timeout with an empty selection is not pinned on the class`` () =
    let policy = Starvation.Policy.rerunAlone [ "A.Slow" ]

    test
        <@
            Starvation.decide (Some policy) (Some []) timedOut None starved
            |> Starvation.isRerun
            |> not
        @>

[<Fact>]
let ``a red on a quiet box with only timeouts says it was not starved`` () =
    let json = ctrfOf [ timeoutRow "A.Slow" "one" ]

    match Starvation.decide (Some everyClass) None failedRed (Some json) quiet with
    | Starvation.Report(Some reason) ->
        test <@ reason.Contains "not starved" @>
        test <@ reason.Contains "1 test(s) timed out: A.Slow.one" @>
    | other -> failwith $"expected a report, got %A{other}"

[<Fact>]
let ``no samples is described as such`` () =
    test <@ (Starvation.Reading.describe everyClass (reading 8 [])).Contains "no load samples" @>

[<Fact>]
let ``a record that is absent, unreadable or has no outcome reads as none`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-starved-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore

    try
        test <@ Starvation.tryRead (Path.Combine(dir, "absent.starved.json")) = None @>
        let torn = Path.Combine(dir, "torn.starved.json")
        File.WriteAllText(torn, "{ \"passedAlone\": ")
        test <@ Starvation.tryRead torn = None @>
        let partial = Path.Combine(dir, "partial.starved.json")
        File.WriteAllText(partial, """{ "summary": "x" }""")
        test <@ Starvation.tryRead partial = None @>
        let minimal = Path.Combine(dir, "minimal.starved.json")
        File.WriteAllText(minimal, """{ "passedAlone": false, "load": 3 }""")

        test
            <@
                Starvation.tryRead minimal = Some
                    { PassedAlone = false
                      Summary = ""
                      FirstAttempt = ""
                      Load = "" }
            @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``an unknown key or a non-array classes is named in the refusal`` () =
    match parse """{ "onTimeout": "rerun-alone", "retries": 3 }""" with
    | Error reason -> test <@ reason.Contains "retries" @>
    | Ok p -> failwith $"expected a refusal, got %A{p}"

    match parse """{ "onTimeout": "rerun-alone", "classes": "A.Slow" }""" with
    | Error reason -> test <@ reason.Contains "classes" @>
    | Ok p -> failwith $"expected a refusal, got %A{p}"

    match parse """{ "onTimeout": 1 }""" with
    | Error reason -> test <@ reason.Contains "onTimeout" @>
    | Ok p -> failwith $"expected a refusal, got %A{p}"
