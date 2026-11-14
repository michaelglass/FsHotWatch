/// AUTOMATION-1686: `runtimeSelection: "runtime/record-static"`. The pure parts: the set
/// static selection alone would have launched, the judgement of a record, the outcomes a
/// completed run's reports establish, and the store a record is filed to.
module FsHotWatch.Tests.StaticRecordTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.StaticRecord
open FsHotWatch.TestPrune.StaticRecordStore
open FsHotWatch.Tests.TestHelpers

let private t project cls name =
    { Project = project
      Class = cls
      Name = $"%s{cls}.%s{name}" }

let private record (staticSel: Map<string, ProjectSelection>) (ran: Map<string, ProjectSelection>) outcomes =
    { Tree = Tree "tree-1"
      RunId = Guid.Parse "11111111-1111-1111-1111-111111111111"
      Static = staticSel
      Ran = ran
      Outcomes = outcomes
      KnownFlaky = Set.empty }

let private nes (values: 'T list) =
    NonEmptySet.ofSet (Set.ofList values) |> Option.get

// Static selection took classes A of P; runtime coverage added all of Q.
let private staticSel = Map.ofList [ "P", ProjectClasses(set [ "Ns.A" ]) ]

let private ran =
    Map.ofList [ "P", ProjectClasses(set [ "Ns.A" ]); "Q", ProjectInFull ]

let private insideA = t "P" "Ns.A" "a1"
let private outsideQ = t "Q" "Ns.Q" "q1"

let private outcomes (rows: (TestId * TestOutcome) list) =
    rows
    |> List.groupBy (fun (test, _) -> test.Project)
    |> List.map (fun (project, entries) -> project, Ran(Map.ofList entries))
    |> Map.ofList

// ---------------------------------------------------------------------------
// The judgement table.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``a record whose tests all passed is green`` () =
    let r = record staticSel ran (outcomes [ insideA, Passed; outsideQ, Passed ])
    test <@ judge r = Green @>

[<Fact(Timeout = 10000)>]
let ``a failure inside the static set is caught by static`` () =
    let r = record staticSel ran (outcomes [ insideA, Failed; outsideQ, Passed ])
    test <@ judge r = CaughtByStatic(nes [ TestFailed insideA ], Set.empty) @>

[<Fact(Timeout = 10000)>]
let ``a failure outside the static set while every inside test passed is missed by static`` () =
    let r = record staticSel ran (outcomes [ insideA, Passed; outsideQ, Failed ])
    test <@ judge r = MissedByStatic(nes [ TestFailed outsideQ ]) @>

[<Fact(Timeout = 10000)>]
let ``failures inside and outside are caught by static, with the outside one listed`` () =
    let r = record staticSel ran (outcomes [ insideA, Failed; outsideQ, Failed ])
    test <@ judge r = CaughtByStatic(nes [ TestFailed insideA ], set [ TestFailed outsideQ ]) @>

[<Fact(Timeout = 10000)>]
let ``a record whose static set is what ran carries no evidence, even over a failure`` () =
    let r = record ran ran (outcomes [ insideA, Passed; outsideQ, Failed ])
    test <@ judge r = StaticRanEverything @>

[<Fact(Timeout = 10000)>]
let ``a timed-out project outside the static set counts as a failure static missed`` () =
    let r =
        record staticSel ran (Map.ofList [ "P", Ran(Map.ofList [ insideA, Passed ]); "Q", TimedOut ])

    test <@ judge r = MissedByStatic(nes [ ProjectTimedOut "Q" ]) @>

[<Fact(Timeout = 10000)>]
let ``a timed-out project static launched counts as a failure static caught`` () =
    let r = record staticSel ran (Map.ofList [ "P", TimedOut; "Q", Ran Map.empty ])
    test <@ judge r = CaughtByStatic(nes [ ProjectTimedOut "P" ], Set.empty) @>

[<Fact(Timeout = 10000)>]
let ``a red project that named no failed test is a project-level failure`` () =
    let r =
        record staticSel ran (Map.ofList [ "P", Ran(Map.ofList [ insideA, Passed ]); "Q", RedUnnamed ])

    test <@ judge r = MissedByStatic(nes [ ProjectRedUnnamed "Q" ]) @>

[<Fact(Timeout = 10000)>]
let ``a project that verified nothing leaves the record unjudged`` () =
    let r =
        record staticSel ran (Map.ofList [ "P", Ran(Map.ofList [ insideA, Passed ]); "Q", ProvedNothing "exit 7" ])

    test <@ judge r = OutcomesUnknown(nes [ "Q" ]) @>

[<Fact(Timeout = 10000)>]
let ``membership is by class: classes name what is inside, all-but names what is outside`` () =
    let classes = Map.ofList [ "P", ProjectClasses(set [ "Ns.A" ]) ]
    let allBut = Map.ofList [ "P", ProjectExcept(set [ "Ns.A" ]) ]
    let whole = Map.ofList [ "P", ProjectInFull ]
    let a = TestFailed(t "P" "Ns.A" "x")
    let b = TestFailed(t "P" "Ns.B" "x")
    let elsewhere = TestFailed(t "R" "Ns.A" "x")

    test <@ isInside classes a && not (isInside classes b) @>
    test <@ not (isInside allBut a) && isInside allBut b @>
    test <@ isInside whole a && isInside whole b @>
    test <@ not (isInside whole elsewhere) @>

[<Fact(Timeout = 10000)>]
let ``a known-flaky failure is reported beside the miss, never dropped from it`` () =
    let r =
        { record staticSel ran (outcomes [ insideA, Passed; outsideQ, Failed ]) with
            KnownFlaky = set [ outsideQ ] }

    let judgement = judge r
    test <@ judgement = MissedByStatic(nes [ TestFailed outsideQ ]) @>

    match judgement with
    | MissedByStatic outside -> test <@ isFlakyOnly r outside @>
    | other -> failwith $"expected a miss, got %A{other}"

    // A miss with any failure that has no flaky history is not flaky-only.
    let other = t "Q" "Ns.Q" "q2"

    let mixed =
        { record staticSel ran (outcomes [ insideA, Passed; outsideQ, Failed; other, Failed ]) with
            KnownFlaky = set [ outsideQ ] }

    match judge mixed with
    | MissedByStatic outside -> test <@ not (isFlakyOnly mixed outside) @>
    | other -> failwith $"expected a miss, got %A{other}"

// ---------------------------------------------------------------------------
// The static set, derived from what ran.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``a project only runtime coverage forced is not in the static set`` () =
    let ran = Map.ofList [ "P", ProjectClasses(set [ "Ns.A" ]); "Q", ProjectInFull ]

    test <@ staticOf ran (set [ "Q" ]) Map.empty Map.empty = Map.ofList [ "P", ProjectClasses(set [ "Ns.A" ]) ] @>

[<Fact(Timeout = 10000)>]
let ``a runtime-only project keeps the classes static causes named in it`` () =
    let ran = Map.ofList [ "Q", ProjectInFull; "R", ProjectInFull; "S", ProjectInFull ]
    let outstanding = Map.ofList [ "Q", [ "Ns.Red" ]; "R", [] ]
    let always = Map.ofList [ "Q", [ "Ns.Always" ]; "S", [ "Ns.Tree" ] ]

    test
        <@
            staticOf ran (set [ "Q"; "R"; "S" ]) outstanding always = Map.ofList
                [ "Q", ProjectClasses(set [ "Ns.Red"; "Ns.Always" ])
                  // A project-level red is the whole project.
                  "R", ProjectInFull
                  "S", ProjectClasses(set [ "Ns.Tree" ]) ]
        @>

[<Fact(Timeout = 10000)>]
let ``with no runtime-only project the static set is what ran`` () =
    let ran = Map.ofList [ "P", ProjectExcept(set [ "Ns.Slow" ]); "Q", ProjectInFull ]
    test <@ staticOf ran Set.empty (Map.ofList [ "Q", [ "Ns.Red" ] ]) Map.empty = ran @>

// ---------------------------------------------------------------------------
// Outcomes, from a completed run's results and reports.
// ---------------------------------------------------------------------------

let private ctrf (rows: (string * string * string) list) =
    let tests =
        rows
        |> List.map (fun (cls, meth, status) ->
            $"""{{"name": "%s{cls}.%s{meth}", "status": "%s{status}", "duration": 1, "suite": ["asm", "%s{cls}", "%s{meth}"]}}""")
        |> String.concat ","

    $"""{{"results": {{"summary": {{"tests": %d{List.length rows}}}, "tests": [%s{tests}]}}}}"""

[<Fact(Timeout = 10000)>]
let ``a run's outcomes come from each project's report, and a retried-green test passed`` () =
    let report =
        ctrf
            [ "Ns.A", "a1", "passed"
              "Ns.A", "a2", "failed"
              "Ns.A", "a3", "failed"
              "Ns.A", "a3", "passed"
              "Ns.B", "b1", "skipped" ]

    let reports = Map.ofList [ "P", report ]

    let results =
        Map.ofList
            [ "P", TestsFailed("", true, TimeSpan.Zero)
              "Green", TestsPassed("", true, TimeSpan.Zero)
              "Red", TestsFailed("", true, TimeSpan.Zero)
              "Slow", TestsTimedOut("", TimeSpan.FromSeconds 1.0, true, TimeSpan.Zero)
              "Crashed", TestsErrored "exit 7, no report written"
              "Waiting", TestsDeferred "apphost not produced"
              "NoMatch", TestsNoMatch("", TimeSpan.Zero) ]

    let got = outcomesOf (fun project -> Map.tryFind project reports) results

    test
        <@
            got.["P"] = Ran(
                Map.ofList
                    [ t "P" "Ns.A" "a1", Passed
                      t "P" "Ns.A" "a2", Failed
                      t "P" "Ns.A" "a3", Passed
                      t "P" "Ns.B" "b1", Skipped ]
            )
        @>

    test <@ got.["Green"] = Ran Map.empty @>
    test <@ got.["Red"] = RedUnnamed @>
    test <@ got.["Slow"] = TimedOut @>
    test <@ got.["Crashed"] = ProvedNothing "exit 7, no report written" @>
    test <@ got.["Waiting"] = ProvedNothing "apphost not produced" @>
    test <@ got.["NoMatch"] = Ran Map.empty @>

[<Fact(Timeout = 10000)>]
let ``only a run that completed can be filed`` () =
    let completion outcome =
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = outcome
          Results = Map.empty
          Verification = RunVerification.ofResults Map.empty }

    test <@ CompletedRun.ofCompletion (completion (Aborted "cancelled")) = Error "cancelled" @>
    test <@ Result.isOk (CompletedRun.ofCompletion (completion Normal)) @>

[<Fact(Timeout = 10000)>]
let ``a test whose history flipped is known flaky; one that never flipped is not`` () =
    let at outcome : Flakiness.TestRunRecord =
        { Name = ""
          Outcome = outcome
          DurationMs = 1
          RunStartedAt = DateTime.UnixEpoch }

    let history =
        Map.ofList
            [ "Ns.Q.q1", [ at Flakiness.Passed; at Flakiness.Failed ]
              "Ns.A.a1", [ at Flakiness.Passed; at Flakiness.Passed ] ]

    test <@ flakyNamesOf history = set [ "Ns.Q.q1" ] @>

// ---------------------------------------------------------------------------
// The store.
// ---------------------------------------------------------------------------

let private aMiss =
    record staticSel ran (outcomes [ insideA, Passed; outsideQ, Failed ])

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a filed record is one line in the store and one info line in the log`` () =
    withTempDir "static-record-store" (fun dir ->
        let store = Path.Combine(dir, "nested", "records.jsonl")
        let infos = ResizeArray<string>()
        let warns = ResizeArray<string>()

        file infos.Add warns.Add store aMiss
        file infos.Add warns.Add store { aMiss with RunId = Guid.NewGuid() }

        let lines = File.ReadAllLines store
        test <@ lines.Length = 2 @>

        let first = Text.Json.JsonDocument.Parse(lines.[0]).RootElement

        let at (path: string list) =
            (path
             |> List.fold (fun (e: Text.Json.JsonElement) (key: string) -> e.GetProperty key) first)
                .GetString()

        test <@ at [ "tree" ] = "tree-1" @>
        test <@ at [ "judgement"; "kind" ] = "missed-by-static" @>
        test <@ at [ "static"; "P"; "scope" ] = "classes" @>
        test <@ at [ "ran"; "Q"; "scope" ] = "whole" @>

        test <@ warns.Count = 0 @>

        test
            <@ infos.[0] = "selection-record: {tree: tree-1, staticTests: 1, ranTests: 2, judgement: missed-by-static}" @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a store that cannot be written is one warning and no record`` () =
    withTempDir "static-record-unwritable" (fun dir ->
        // A FILE where the store's directory must be: nothing can be created under it.
        let blocked = Path.Combine(dir, "blocked")
        File.WriteAllText(blocked, "")
        let store = Path.Combine(blocked, "records.jsonl")
        let infos = ResizeArray<string>()
        let warns = ResizeArray<string>()

        file infos.Add warns.Add store aMiss

        test <@ not (File.Exists store) @>
        test <@ warns.Count = 1 @>
        test <@ warns.[0].Contains "could not file the record" @>
        // The trace stays in the log.
        test <@ infos.Count = 1 @>)

[<Fact(Timeout = 10000)>]
let ``every outcome, failure, selection and tree kind is written into the stored line`` () =
    let r =
        { record
              (Map.ofList [ "P", ProjectExcept(set [ "Ns.Slow" ]) ])
              (Map.ofList
                  [ "P", ProjectInFull
                    "Q", ProjectInFull
                    "R", ProjectInFull
                    "S", ProjectInFull ])
              (Map.ofList
                  [ "P", Ran(Map.ofList [ t "P" "Ns.A" "a1", Skipped ])
                    "Q", RedUnnamed
                    "R", TimedOut
                    "S", ProvedNothing "exit 7" ]) with
            Tree = Unidentified }

    let line = Json.render r (judge r)
    let root = Text.Json.JsonDocument.Parse(line).RootElement

    let at (path: string list) =
        (path
         |> List.fold (fun (e: Text.Json.JsonElement) (key: string) -> e.GetProperty key) root)
            .ToString()

    test <@ at [ "tree" ] = "" @>
    test <@ at [ "static"; "P"; "scope" ] = "all-but" @>
    test <@ at [ "outcomes"; "Q"; "kind" ] = "red-unnamed" @>
    test <@ at [ "outcomes"; "R"; "kind" ] = "timed-out" @>
    test <@ at [ "outcomes"; "S"; "reason" ] = "exit 7" @>
    test <@ line.Contains "\"outcome\":\"skipped\"" @>
    test <@ at [ "judgement"; "kind" ] = "outcomes-unknown" @>
    test <@ line.Contains "\"projects\":[\"S\"]" @>

    test
        <@
            logLine r (judge r) = "selection-record: {tree: unidentified, staticTests: 1, ranTests: 1, judgement: outcomes-unknown}"
        @>

    // A project-level failure names its project.
    let failures =
        { r with
            Outcomes = Map.ofList [ "Q", RedUnnamed; "R", TimedOut ] }

    let failed = Json.render failures (judge failures)
    test <@ failed.Contains "{\"kind\":\"red-unnamed\",\"project\":\"Q\"}" @>
    test <@ failed.Contains "{\"kind\":\"timed-out\",\"project\":\"R\"}" @>

let private completionOf outcome (results: Map<string, TestResult>) =
    { RunId = Guid.NewGuid()
      TotalElapsed = TimeSpan.Zero
      Outcome = outcome
      Results = results
      Verification = RunVerification.ofResults results }

let private launched: Launched =
    { Ran = Map.ofList [ "Q", ProjectInFull ]
      RuntimeOnly = set [ "Q" ]
      Outstanding = Map.empty
      Always = Map.empty }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a run that did not complete files nothing, and one that did is filed`` () =
    withTempDir "static-record-prepare" (fun repo ->
        let store = Path.Combine(repo, "store", "records.jsonl")
        let infos = ResizeArray<string>()
        let warns = ResizeArray<string>()
        let recording = Recording.For(set [ "Q" ], store)

        let fileRun = prepare infos.Add warns.Add repo recording launched
        fileRun (completionOf (Aborted "cancelled") Map.empty) (Some "tree-1")

        test <@ not (File.Exists store) @>

        test
            <@
                infos
                |> Seq.exists (fun l -> l.Contains "did not complete (cancelled); no record")
            @>

        // A completed run with no tree and no report: an unidentified tree, no named test.
        let results =
            Map.ofList
                [ "Q", TestsPassed("", false, TimeSpan.Zero)
                  "NotLaunched", TestsPassed("", true, TimeSpan.Zero) ]

        fileRun (completionOf Normal results) None

        let lines = File.ReadAllLines store
        test <@ lines.Length = 1 @>
        test <@ lines.[0].Contains "\"tree\":null" @>
        // Only what launched has outcomes.
        test <@ not (lines.[0].Contains "NotLaunched") @>
        test <@ lines.[0].Contains "\"kind\":\"green\"" @>
        test <@ warns.Count = 0 @>)

[<Fact(Timeout = 10000)>]
let ``with recording off nothing is captured or filed`` () =
    let infos = ResizeArray<string>()
    let fileRun = prepare infos.Add infos.Add "/nonexistent-repo" Recording.Off launched
    fileRun (completionOf Normal Map.empty) (Some "tree-1")
    test <@ infos.Count = 0 @>

[<Fact(Timeout = 10000)>]
let ``a miss with a project-level failure is never flaky-only`` () =
    let r =
        { record staticSel ran (Map.ofList [ "P", Ran(Map.ofList [ insideA, Passed ]); "Q", TimedOut ]) with
            KnownFlaky = set [ outsideQ ] }

    match judge r with
    | MissedByStatic outside -> test <@ not (isFlakyOnly r outside) @>
    | other -> failwith $"expected a miss, got %A{other}"

    let unnamed =
        { r with
            Outcomes = Map.add "Q" RedUnnamed r.Outcomes }

    match judge unnamed with
    | MissedByStatic outside -> test <@ not (isFlakyOnly unnamed outside) @>
    | other -> failwith $"expected a miss, got %A{other}"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a report that cannot be read costs the record and a warning, nothing else`` () =
    withTempDir "static-record-unreadable" (fun repo ->
        let store = Path.Combine(repo, "store", "records.jsonl")
        let warns = ResizeArray<string>()

        let completion =
            completionOf Normal (Map.ofList [ "Q", TestsFailed("", false, TimeSpan.Zero) ])

        let runDir = FsHotWatch.Ctrf.runDir repo completion.RunId
        Directory.CreateDirectory runDir |> ignore
        // A report no one may read: reading it throws.
        let report = Path.Combine(runDir, "Q" + FsHotWatch.Ctrf.ReportSuffix)
        File.WriteAllText(report, "{}")
        File.SetUnixFileMode(report, UnixFileMode.None)

        let fileRun =
            prepare ignore warns.Add repo (Recording.For(set [ "Q" ], store)) launched

        fileRun completion (Some "tree-1")

        test <@ not (File.Exists store) @>
        test <@ warns.Count = 1 @>
        test <@ warns.[0].Contains "building the record failed" @>)
