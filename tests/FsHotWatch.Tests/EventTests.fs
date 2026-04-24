module FsHotWatch.Tests.EventTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events

[<Fact(Timeout = 5000)>]
let ``FileChangeKind constructors work`` () =
    let source = SourceChanged [ "src/Lib.fs" ]
    let proj = ProjectChanged [ "src/Lib.fsproj" ]
    let sln = SolutionChanged "test.sln"

    test
        <@
            match source with
            | SourceChanged files -> files.Length = 1
            | _ -> false
        @>

    test
        <@
            match proj with
            | ProjectChanged _ -> true
            | _ -> false
        @>

    test
        <@
            match sln with
            | SolutionChanged _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``PluginStatus constructors work`` () =
    let idle = Idle
    let running = Running(since = System.DateTime.UtcNow)

    test
        <@
            match idle with
            | Idle -> true
            | _ -> false
        @>

    test
        <@
            match running with
            | Running _ -> true
            | _ -> false
        @>

// --- path/hash wrappers ---

[<Fact(Timeout = 5000)>]
let ``AbsFilePath round-trips and normalizes`` () =
    let p = AbsFilePath.create "foo.fs"
    test <@ System.IO.Path.IsPathRooted(AbsFilePath.value p) @>

[<Fact(Timeout = 5000)>]
let ``AbsProjectPath round-trips`` () =
    let p = AbsProjectPath.create "x.fsproj"
    test <@ AbsProjectPath.value p |> System.IO.Path.IsPathRooted @>

[<Fact(Timeout = 5000)>]
let ``ContentHash round-trips`` () =
    let h = ContentHash.create "abc123"
    test <@ ContentHash.value h = "abc123" @>

// --- PluginStatus predicates ---

[<Fact(Timeout = 5000)>]
let ``isTerminal is true for Completed and Failed, false for Idle and Running`` () =
    let now = System.DateTime.UtcNow
    test <@ PluginStatus.isTerminal (Completed now) @>
    test <@ PluginStatus.isTerminal (Failed("err", now)) @>
    test <@ not (PluginStatus.isTerminal Idle) @>
    test <@ not (PluginStatus.isTerminal (Running(since = now))) @>

[<Fact(Timeout = 5000)>]
let ``isQuiescent is true for Idle, Completed and Failed, false for Running`` () =
    let now = System.DateTime.UtcNow
    test <@ PluginStatus.isQuiescent Idle @>
    test <@ PluginStatus.isQuiescent (Completed now) @>
    test <@ PluginStatus.isQuiescent (Failed("err", now)) @>
    test <@ not (PluginStatus.isQuiescent (Running(since = now))) @>

// --- TestResult helpers ---

[<Fact(Timeout = 5000)>]
let ``TestResult.output returns the output string for both cases`` () =
    test <@ TestResult.output (TestsPassed("ok", false)) = "ok" @>
    test <@ TestResult.output (TestsFailed("bad", true)) = "bad" @>

[<Fact(Timeout = 5000)>]
let ``TestResult.wasFiltered reflects the filter flag`` () =
    test <@ not (TestResult.wasFiltered (TestsPassed("ok", false))) @>
    test <@ TestResult.wasFiltered (TestsPassed("ok", true)) @>
    test <@ TestResult.wasFiltered (TestsFailed("bad", true)) @>

[<Fact(Timeout = 5000)>]
let ``TestResult.isPassed distinguishes Passed and Failed`` () =
    test <@ TestResult.isPassed (TestsPassed("ok", false)) @>
    test <@ not (TestResult.isPassed (TestsFailed("bad", false))) @>

[<Fact(Timeout = 5000)>]
let ``TestResult.ranFullSuite is true for empty map`` () =
    test <@ TestResult.ranFullSuite Map.empty @>

[<Fact(Timeout = 5000)>]
let ``TestResult.ranFullSuite is true when every project ran unfiltered`` () =
    let results =
        Map.ofList [ "A", TestsPassed("ok", false); "B", TestsFailed("fail", false) ]

    test <@ TestResult.ranFullSuite results @>

[<Fact(Timeout = 5000)>]
let ``TestResult.ranFullSuite is false if any project was filtered`` () =
    let results =
        Map.ofList [ "A", TestsPassed("ok", false); "B", TestsPassed("ok", true) ]

    test <@ not (TestResult.ranFullSuite results) @>
