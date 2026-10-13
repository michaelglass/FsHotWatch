/// Joining the runner's test names with the index's test classes.
///
/// The runner reports a test as `<class>.<method>`, both compiled names: a backticked
/// method's backticks are gone and everything between them is kept — `.` included, and
/// a backticked class keeps its spaces. The index stores the same class string, so a
/// red, a recall sample and a class filter all join on the class. These tests use a real
/// backticked fixture so the names are the ones the runner prints.
module FsHotWatch.Tests.TestPruneTestNameTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A test class written the way most F# suites write them.
type ``Backticked fixture``() =
    member _.``parses v1.5 input``() = ()
    member _.``plain name``() = ()

let private fixture = typeof<``Backticked fixture``>

/// The class as the runner reports it and the index stores it.
let private runnerClass = fixture.FullName

/// The runner's method names: compiled, so unbackticked, and the first keeps its `.`.
let private dottedMethod = fixture.GetMethod("parses v1.5 input").Name
let private plainMethod = fixture.GetMethod("plain name").Name

let private runnerName (methodName: string) = $"%s{runnerClass}.%s{methodName}"

let private noRunLog (_: string) : FsHotWatch.RunLog.Ref =
    FsHotWatch.RunLog.Ref.Unavailable "no run log in this test"

/// A complete CTRF report whose rows carry the `suite` path xUnit writes: two hash
/// segments, then the class, then the method.
let private ctrfReport (rows: (string * string) list) =
    let tests =
        rows
        |> List.map (fun (methodName, status) ->
            {| name = runnerName methodName
               status = status
               duration = 1
               suite = [ "a1"; "b2"; runnerClass; methodName ] |})

    let count status =
        rows |> List.filter (fun (_, s) -> s = status) |> List.length

    JsonSerializer.Serialize
        {| results =
            {| summary =
                {| tests = rows.Length
                   passed = count "passed"
                   failed = count "failed"
                   pending = 0
                   skipped = 0
                   other = 0 |}
               tests = tests |} |}

let private failureIn (className: string) (methodName: string) : OutstandingFailure =
    { Project = "P"
      Class = Some className
      Method = Some methodName
      File = "<tests/P>"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "observed full-run failure" }

[<Fact(Timeout = 10000)>]
let ``the fixture's runner name has a dot inside the method`` () =
    test <@ dottedMethod = "parses v1.5 input" @>
    test <@ runnerClass.Contains ' ' @>

[<Fact(Timeout = 10000)>]
let ``a CTRF row of a backticked method with a dot is read under the class the index stores`` () =
    let failing = ctrfReport [ dottedMethod, "failed"; plainMethod, "passed" ]
    test <@ failedTestsOfReport failing = Some [ runnerClass, dottedMethod ] @>

    let passing = ctrfReport [ dottedMethod, "passed"; plainMethod, "passed" ]
    test <@ passedClassesOfReport passing = Set.ofList [ runnerClass ] @>

    test <@ passedTestsOfReport passing = Set.ofList [ runnerClass, dottedMethod; runnerClass, plainMethod ] @>

[<Fact(Timeout = 10000)>]
let ``check reach finds a failing backticked test inside a class-filtered selection`` () =
    let failures =
        ctrfReport [ dottedMethod, "failed" ]
        |> failedTestsOfReport
        |> Option.defaultValue []
        |> List.map (fun (className, methodName) -> failureIn className methodName)

    let selection = Map.ofList [ "P", ProjectClasses(Set.ofList [ runnerClass ]) ]

    test <@ CheckReach.classify (Some selection) failures = ReachedAFailure [ "P" ] @>
    test <@ CheckReach.measure (Some selection) failures = RecallMeasured(1, 1, 1.0, true) @>

[<Fact(Timeout = 10000)>]
let ``a console-named backticked failure is filed under its indexed class and source file`` () =
    let output =
        $"failed %s{runnerName dottedMethod} (1ms)\nTest run summary: Failed!\n  total: 2\n  failed: 1"

    let results: TestResults =
        { Results = Map.ofList [ "P", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let classFiles = Map.ofList [ runnerClass, "tests/P/Fixture.fs" ]

    let red = failuresOf noRunLog noReport classFiles results |> List.exactlyOne

    test <@ red.Class = Some runnerClass @>
    test <@ red.Method = Some dottedMethod @>
    test <@ red.File = "tests/P/Fixture.fs" @>

[<Fact(Timeout = 10000)>]
let ``a backticked red filed from the console is retired by a class-filtered green`` () =
    let output =
        $"failed %s{runnerName dottedMethod} (1ms)\nTest run summary: Failed!\n  total: 2\n  failed: 1"

    let results: TestResults =
        { Results = Map.ofList [ "P", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let reds =
        failuresOf noRunLog noReport (Map.ofList [ runnerClass, "tests/P/Fixture.fs" ]) results

    // The class filter the next run launches with is the red's class: the one the index
    // and the runner both use, so the filter matches the test.
    test <@ OutstandingFailure.quarantine (Set.ofList [ "P" ]) reds Map.empty = Map.ofList [ "P", [ runnerClass ] ] @>

    let green = ctrfReport [ dottedMethod, "passed"; plainMethod, "passed" ]
    let coverage = Map.ofList [ "P", CoveredClasses(Set.ofList [ runnerClass ]) ]
    let passed = Map.ofList [ "P", passedTestsOfReport green ]

    test
        <@
            OutstandingFailure.carriedOver (Set.ofList [ "P" ]) coverage passed reds
            |> List.isEmpty
        @>

[<Fact(Timeout = 10000)>]
let ``a backticked red only the CTRF report names is filed under the class the report declares`` () =
    let output = "Test run summary: Failed!\n  total: 2\n  failed: 1\n  succeeded: 1"

    let results: TestResults =
        { Results = Map.ofList [ "P", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let report =
        ReportedFailures.ofReport (ctrfReport [ dottedMethod, "failed"; plainMethod, "passed" ])

    // No index entry for the class: the report's own `suite` path is what places the split.
    let red = failuresOf noRunLog (fun _ -> report) Map.empty results |> List.exactlyOne

    test <@ red.Class = Some runnerClass @>
    test <@ red.Method = Some dottedMethod @>

[<Fact(Timeout = 10000)>]
let ``a name no known class prefixes still splits at its last dot`` () =
    test <@ splitTestName Set.empty "Ns.Tests.plain" = ("Ns.Tests", "plain") @>
    test <@ splitTestName (Set.ofList [ "Other" ]) "Ns.Tests.plain" = ("Ns.Tests", "plain") @>
    test <@ splitTestName (Set.ofList [ "Ns" ]) "Ns" = ("Ns", "Ns") @>
    test <@ splitTestName (Set.ofList [ "Ns.Tests" ]) "Ns.Tests.v1.5" = ("Ns.Tests", "v1.5") @>

[<Fact(Timeout = 10000)>]
let ``a report row without a suite path declares no class`` () =
    let json =
        """{"results":{"tests":[{"name":"Ns.C.t","status":"passed"},{"name":"Ns.D.u","status":"passed","suite":["Other","x"]}]}}"""

    test <@ FsHotWatch.TestPrune.Flakiness.parseCtrfDeclaredClasses json = Set.empty @>
    test <@ FsHotWatch.TestPrune.Flakiness.parseCtrfDeclaredClasses "not json" = Set.empty @>

[<Theory(Timeout = 10000)>]
[<InlineData("null")>]
[<InlineData("""{"results":{}}""")>]
[<InlineData("""{"results":{"tests":[null]}}""")>]
[<InlineData("""{"results":{"tests":[{"name":"Ns.C.t","status":"passed","suite":[null,"t"]}]}}""")>]
[<InlineData("""{"results":{"tests":[{"name":"Ns.C.t","status":"passed","suite":[1,"t"]}]}}""")>]
[<InlineData("""{"results":{"tests":[{"name":".t","status":"passed","suite":["","t"]}]}}""")>]
let ``a malformed report declares no class`` (json: string) =
    test <@ FsHotWatch.TestPrune.Flakiness.parseCtrfDeclaredClasses json = Set.empty @>
