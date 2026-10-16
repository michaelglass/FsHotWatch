/// Publishing a green to the evidence store is the one outcome that has to be earned:
/// every way a run can fall short of "this project ran in full, every test passed first
/// time, every graded artifact exists, and the tree held still" must publish nothing.
module FsHotWatch.Tests.TestEvidenceProducerTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.TestPrune

let private key (closure: string) =
    Ok(TestEvidenceKey.ofLabels [ "closure", closure ]): Result<TestEvidenceKey.Key, string>

let private report (rows: string list) (failed: int) =
    let passed = rows.Length - failed
    let body = String.concat "," rows

    "{\"results\":{\"summary\":{\"tests\":"
    + string rows.Length
    + ",\"passed\":"
    + string passed
    + ",\"failed\":"
    + string failed
    + ",\"pending\":0,\"skipped\":0,\"other\":0},\"tests\":["
    + body
    + "]}}"

let private cleanReport =
    report [ """{"name":"a","status":"passed"}"""; """{"name":"b","status":"passed"}""" ] 0

let private green: TestEvidenceRun.ProducedRun =
    { Project = "T"
      Result = TestsPassed("", false, TimeSpan.FromSeconds 3.0)
      Report = Some cleanReport
      CoverageRequired = false
      Coverage = None }

let private refused (launch: Result<TestEvidenceKey.Key, string>) completion (run: TestEvidenceRun.ProducedRun) =
    match TestEvidenceRun.qualifies (Some launch) (Some completion) run with
    | Ok _ -> None
    | Error why -> Some why

[<Fact>]
let ``a full green run with a clean report and a still tree qualifies`` () =
    test <@ refused (key "c") (key "c") green = None @>

[<Fact>]
let ``a failed run publishes nothing`` () =
    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsFailed("", false, TimeSpan.Zero) } = Some "the run failed"
        @>

[<Fact>]
let ``an impact-filtered run publishes nothing`` () =
    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsPassed("", true, TimeSpan.Zero) } = Some "the run was filtered"
        @>

[<Fact>]
let ``a timed-out or aborted run publishes nothing`` () =
    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsTimedOut("", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero) } = Some
                "the run timed out"
        @>

    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsErrored "exit 7" } = Some "the run verified nothing"
        @>

    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsDeferred "apphost" } = Some "the run verified nothing"
        @>

[<Fact>]
let ``a test that passed only on retry publishes nothing`` () =
    let retried =
        report
            [ """{"name":"a","status":"passed","retries":1}"""
              """{"name":"b","status":"passed"}""" ]
            0

    let flaky =
        report
            [ """{"name":"a","status":"passed","flaky":true}"""
              """{"name":"b","status":"passed"}""" ]
            0

    test <@ refused (key "c") (key "c") { green with Report = Some retried } = Some "a test passed only on retry" @>
    test <@ refused (key "c") (key "c") { green with Report = Some flaky } = Some "a test passed only on retry" @>

[<Fact>]
let ``a tree edited mid-run publishes nothing`` () =
    test <@ refused (key "c") (key "d") green = Some "the tree moved during the run" @>

[<Fact>]
let ``a run that cannot be keyed publishes nothing`` () =
    test <@ refused (Error "options unknown") (key "c") green = Some "no key: options unknown" @>

    test <@ TestEvidenceRun.qualifies None (Some(key "c")) green |> Result.isError @>

[<Fact>]
let ``a coverage-graded project with no coverage report publishes nothing`` () =
    test
        <@ refused (key "c") (key "c") { green with CoverageRequired = true } = Some "its coverage report is missing" @>

    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    CoverageRequired = true
                    Coverage = Some("/r/cov.xml", "<coverage/>") } = None
        @>

[<Fact>]
let ``a missing, failing, empty or unreadable report publishes nothing`` () =
    test <@ refused (key "c") (key "c") { green with Report = None } = Some "no CTRF report" @>

    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Report = Some(report [ """{"name":"a","status":"failed"}""" ] 1) } = Some
                "the CTRF report counts failures"
        @>

    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Report = Some(report [] 0) } = Some "the CTRF report counts no tests"
        @>

    test
        <@
            refused (key "c") (key "c") { green with Report = Some "{" }
            |> Option.exists (fun why -> why.StartsWith "the CTRF report is not verdict evidence")
        @>

[<Fact>]
let ``the closure of a project follows references transitively and excludes itself`` () =
    let refs = Map.ofList [ "T", [ "A"; "B" ]; "A", [ "C" ]; "B", [ "C" ]; "C", [] ]

    test <@ TestEvidenceRun.closureOf (fun p -> Map.tryFind p refs |> Option.defaultValue []) "T" = [ "A"; "B"; "C" ] @>

let private storeEntry (k: TestEvidenceKey.Key) : TestEvidenceStore.Entry =
    { Key = k.Value
      Project = "tests/T/T.fsproj"
      KeyInputs = k.Labels
      Producer =
        { Worktree = "/work/a"
          RunId = "r1"
          TreeHash = "t"
          ProducerHash = "p"
          EarnedAt = DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero) }
      Summary =
        { Tests = 2
          Passed = 2
          Failed = 0
          Skipped = 0
          DurationMs = 0L }
      Report = "[1]"
      Coverage = None }

[<Fact>]
let ``a reuse holds only while its key holds, and a withdrawn one names what moved`` () =
    let atLaunch = TestEvidenceKey.ofLabels [ "closure", "c"; "shared", "s" ]
    let moved = TestEvidenceKey.ofLabels [ "closure", "c2"; "shared", "s" ]

    let reused =
        Map.ofList
            [ "Held", storeEntry atLaunch
              "Moved", storeEntry atLaunch
              "Unkeyable", storeEntry atLaunch
              "Missing", storeEntry atLaunch ]

    let held, withdrawn =
        TestEvidenceRun.recheck
            reused
            (Map.ofList [ "Held", Ok atLaunch; "Moved", Ok moved; "Unkeyable", Error "options unknown" ])

    test <@ held |> Map.keys |> List.ofSeq = [ "Held" ] @>

    test
        <@
            withdrawn = [ "Missing", [ "not keyed" ]
                          "Moved", [ "closure" ]
                          "Unkeyable", [ "options unknown" ] ]
        @>

let private wiring (store: TestEvidenceStore.Store) (toolchain: Lazy<string>) : TestEvidenceRun.Wiring =
    { Store = store
      Configs = Map.empty
      Toolchain = toolchain
      ProducerHash = "p"
      ExcludePatterns = [] }

let private tempStore (dir: string) =
    TestEvidenceStore.Store(
        { Root = IO.Path.Combine(dir, "store")
          Ttl = TimeSpan.FromHours 1.0
          MaxBytes = 1_000_000L
          Now = fun () -> DateTimeOffset.UtcNow
          Log = ignore }
    )

[<Fact>]
let ``a plan over no candidates walks nothing and looks nothing up`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "evplan-empty" (fun dir ->
        let store = tempStore dir

        let plan =
            TestEvidenceRun.plan (wiring store (lazy "t")) dir (fun _ -> Some "o") [] (fun _ -> true)

        test <@ plan.Lookups = 0 && plan.BytesHashed = 0L && Map.isEmpty plan.LaunchKeys @>
        test <@ store.Lookups = 0 @>)

[<Fact>]
let ``a plan whose walk fails keys nobody and launches everybody`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "evplan-throws" (fun dir ->
        let store = tempStore dir

        let candidate: TestEvidenceRun.Candidate =
            { Project = "T"
              ProjectFile = IO.Path.Combine(dir, "tests/T/T.fsproj")
              Closure = []
              TestConfig = "{}" }

        let plan =
            TestEvidenceRun.plan
                (wiring store (lazy (failwith "no toolchain")))
                dir
                (fun _ -> Some "o")
                [ candidate ]
                (fun _ -> true)

        test <@ Map.isEmpty plan.Reused @>
        test <@ plan.Lookups = 0 @>

        test
            <@
                plan.Misses
                |> Map.tryFind "T"
                |> Option.exists (fun why -> why.StartsWith "no key: could not key the tree")
            @>)

[<Fact>]
let ``retries are read from every shape a report can take, and doubt counts as a retry`` () =
    let rows (body: string) =
        "{\"results\":{\"tests\":[" + body + "]}}"

    test <@ not (TestEvidenceRun.anyRetried (rows """{"retries":0,"flaky":false}""")) @>
    test <@ not (TestEvidenceRun.anyRetried "{\"tests\":[{\"status\":\"passed\"}]}") @>
    test <@ TestEvidenceRun.anyRetried (rows """{"retries":"x"}""") @>
    test <@ TestEvidenceRun.anyRetried (rows """{"retries":{}}""") @>
    test <@ TestEvidenceRun.anyRetried (rows """{"flaky":"yes"}""") @>
    test <@ TestEvidenceRun.anyRetried (rows """{"flaky":[]}""") @>
    test <@ TestEvidenceRun.anyRetried (rows "7") @>
    test <@ TestEvidenceRun.anyRetried "{\"results\":{}}" @>
    test <@ TestEvidenceRun.anyRetried "[]" @>
    test <@ TestEvidenceRun.anyRetried "{" @>

[<Fact>]
let ``reading and deleting tolerate what cannot be read or deleted`` () =
    FsHotWatch.Tests.TestHelpers.withTempDir "evrun-files" (fun dir ->
        let file = IO.Path.Combine(dir, "a.txt")
        test <@ TestEvidenceRun.readIfPresent file = None @>
        TestEvidenceRun.deleteQuietly file
        IO.File.WriteAllText(file, "x")
        test <@ TestEvidenceRun.readIfPresent file = Some "x" @>

        if not (OperatingSystem.IsWindows()) then
            IO.File.SetUnixFileMode(file, IO.UnixFileMode.None)
            IO.File.SetUnixFileMode(dir, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserExecute)

            try
                test <@ TestEvidenceRun.readIfPresent file = None @>
                TestEvidenceRun.deleteQuietly file
                test <@ IO.File.Exists file @>
            finally
                IO.File.SetUnixFileMode(
                    dir,
                    IO.UnixFileMode.UserRead
                    ||| IO.UnixFileMode.UserWrite
                    ||| IO.UnixFileMode.UserExecute
                )

                IO.File.SetUnixFileMode(file, IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite))

[<Fact>]
let ``an entry names its project relatively and carries the run's own numbers`` () =
    let candidate: TestEvidenceRun.Candidate =
        { Project = "T"
          ProjectFile = "/work/a/tests/T/T.fsproj"
          Closure = []
          TestConfig = "{}" }

    let k = TestEvidenceKey.ofLabels [ "closure", "c" ]
    let producer = (storeEntry k).Producer

    let entry =
        TestEvidenceRun.entryOf
            "/work/a"
            candidate
            k
            producer
            { green with
                Coverage = Some("/work/a/cov/T.xml", "<source>/work/a/src</source>") }

    test <@ entry.Project = "tests/T/T.fsproj" @>

    test
        <@
            entry.Summary.Tests = 2
            && entry.Summary.Passed = 2
            && entry.Summary.DurationMs = 3000L
        @>

    test
        <@
            entry.Coverage = Some
                { Path = "cov/T.xml"
                  Content = $"<source>%s{TestEvidenceStore.RepoRootPlaceholder}/src</source>" }
        @>

    let noRun =
        TestEvidenceRun.entryOf
            "/work/a"
            candidate
            k
            producer
            { green with
                Result = TestsErrored "x"
                Report = None }

    test <@ noRun.Summary.Tests = 0 && noRun.Summary.DurationMs = 0L && noRun.Report = "" @>

[<Fact>]
let ``a stored report that is not an object is filed as it was stored`` () =
    let k = TestEvidenceKey.ofLabels [ "closure", "c" ]
    test <@ TestEvidenceRun.reusedReport "/work/b" (storeEntry k) = "[1]" @>

[<Fact>]
let ``a key that could not be named at launch or completion publishes nothing`` () =
    test <@ refused (key "c") (Error "options unknown") green = Some "no key: options unknown" @>
    test <@ TestEvidenceRun.qualifies None None green = Error "no key" @>

[<Fact>]
let ``a run that matched nothing, or a key lost by completion, publishes nothing`` () =
    test
        <@
            refused
                (key "c")
                (key "c")
                { green with
                    Result = TestsNoMatch("", TimeSpan.Zero) } = Some "the run verified nothing"
        @>

    test <@ TestEvidenceRun.qualifies (Some(key "c")) None green = Error "no key" @>

[<Fact>]
let ``the canonical config writes every field in full, however long`` () =
    let config (last: string) : FsHotWatch.TestPrune.TestPrunePlugin.TestConfig =
        { Project = "T"
          Command = "dotnet"
          Args = "run"
          Group = "default"
          Environment = [ for i in 1..250 -> $"V%d{i}", (if i = 250 then last else "x") ]
          FilterTemplate = Some "--filter-class {classes}"
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.Ctrf }

    let a = FsHotWatch.TestPrune.TestPrunePlugin.canonicalTestConfig (config "a")
    let b = FsHotWatch.TestPrune.TestPrunePlugin.canonicalTestConfig (config "b")
    test <@ a <> b @>
    test <@ a.Contains "V250" @>

    let bare =
        FsHotWatch.TestPrune.TestPrunePlugin.canonicalTestConfig
            { config "a" with
                FilterTemplate = None
                TimeoutSec = None }

    test <@ bare.Contains "\"filterTemplate\":null" && bare.Contains "\"timeoutSec\":null" @>
