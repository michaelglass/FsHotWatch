/// `Insights`: what the retained run directories say about where a suite's wall clock goes
/// — the slowest classes, the serial tail, the budget headroom, how classes grow under
/// load, and what regressed against recent greens (AUTOMATION-1300, slice 3).
module FsHotWatch.Tests.InsightsTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

let private at (seconds: float) =
    DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc).AddSeconds seconds

let private epochMs (t: DateTime) =
    DateTimeOffset(t).ToUnixTimeMilliseconds()

let private timing (cls: string) (startSec: float) (stopSec: float) : Profile.ClassTiming =
    { Class = cls
      Tests = 1
      DurationMs = int64 ((stopSec - startSec) * 1000.0)
      Start = at startSec
      Stop = at stopSec }

/// A CTRF report: one row per (class, startSecond, stopSecond), `failed` rows failing.
let private ctrfOf (failed: int) (rows: (string * float * float) list) =
    let tests =
        rows
        |> List.mapi (fun i (cls, s, e) ->
            {| name = $"%s{cls}.t%d{i}"
               status = (if i < failed then "failed" else "passed")
               duration = int ((e - s) * 1000.0)
               start = epochMs (at s)
               stop = epochMs (at e)
               extra = {| ``type`` = cls |} |})

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

let private runsRoot () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-insights-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

/// A run directory `runId` under `root`, its write time `age` minutes ago.
let private runDir (root: string) (runId: string) (age: float) (files: (string * string) list) =
    let dir = Path.Combine(root, runId)
    Directory.CreateDirectory dir |> ignore

    for name, text in files do
        File.WriteAllText(Path.Combine(dir, name), text)

    Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(-age))
    dir

[<Fact>]
let ``the serial tail is the stretch at the end where at most one class runs, and names it`` () =
    let tail =
        Insights.serialTail [ timing "Long.Pole" 0.0 90.0; timing "Short" 0.0 30.0; timing "Mid" 10.0 40.0 ]

    match tail with
    | Some t ->
        test <@ t.Start = at 40.0 @>
        test <@ t.LengthMs = 50_000.0 @>
        test <@ t.Classes = [ "Long.Pole", 50_000.0 ] @>
    | None -> failwith "no tail"

[<Fact>]
let ``a run that ends with two classes running has no serial tail`` () =
    test <@ Insights.serialTail [ timing "A" 0.0 10.0; timing "B" 5.0 10.0 ] = None @>
    test <@ Insights.serialTail [] = None @>

[<Fact>]
let ``a lone class is its own tail`` () =
    match Insights.serialTail [ timing "Only" 2.0 5.0 ] with
    | Some t -> test <@ t.Classes = [ "Only", 3_000.0 ] && t.LengthMs = 3_000.0 @>
    | None -> failwith "no tail"

[<Fact>]
let ``loadRun reads a report-only project, a killed one, and prefers a profile to the report`` () =
    let root = runsRoot ()

    let profileLines =
        [ Profile.Start("Profiled", at 0.0, 4, Some 100, Some 1.0)
          Profile.End(at 50.0, 50_000L, Some 1.0, Some 9.0, "passed")
          Profile.Class(
              timing "P.Slow" 0.0 40.0,
              { Samples = 3
                LoadPerCoreMean = Some 2.0
                LoadPerCoreMax = Some 3.0
                Contended = Some true },
              Profile.ClassSource.Report
          ) ]
        |> List.map Profile.render
        |> String.concat "\n"

    let timedOut =
        """{"project":"Killed","spawnedAt":"2026-10-21T03:00:00.0000000Z","timeoutSec":300,
            "stop":{"at":"2026-10-21T03:05:01.0000000Z"}}"""

    let dir =
        runDir
            root
            "r1"
            0.0
            [ "Reported.ctrf.json", ctrfOf 1 [ "R.A", 0.0, 20.0; "R.B", 5.0, 8.0 ]
              "Killed.timed-out.json", timedOut
              "Profiled.profile.jsonl", profileLines
              // Ignored in favour of the profile.
              "Profiled.ctrf.json", ctrfOf 0 [ "From.Report", 0.0, 1.0 ] ]

    let runs = Insights.loadRun dir |> List.map (fun p -> p.Project, p) |> Map.ofList

    test <@ runs.Keys |> List.ofSeq = [ "Killed"; "Profiled"; "Reported" ] @>

    let reported = runs["Reported"]
    test <@ reported.RunId = "r1" @>
    test <@ reported.Outcome = Some "failed" @>
    test <@ reported.WallMs = Some 20_000.0 @>
    test <@ reported.Classes |> List.map _.Timing.Class = [ "R.A"; "R.B" ] @>
    test <@ reported.Classes |> List.forall (fun c -> c.Contended = None) @>

    let killed = runs["Killed"]
    test <@ killed.Outcome = Some "timed-out" @>
    test <@ killed.TimeoutSec = Some 300 @>
    test <@ killed.WallMs = Some 301_000.0 @>
    test <@ List.isEmpty killed.Classes @>

    let profiled = runs["Profiled"]
    test <@ profiled.Outcome = Some "passed" @>
    test <@ profiled.TimeoutSec = Some 100 @>
    test <@ profiled.WallMs = Some 50_000.0 @>
    test <@ profiled.Classes |> List.map (fun c -> c.Timing.Class, c.Contended) = [ "P.Slow", Some true ] @>

[<Fact>]
let ``loadRuns lists every run directory under the root, newest first`` () =
    let root = runsRoot ()
    runDir root "old" 30.0 [ "P.ctrf.json", ctrfOf 0 [ "C", 0.0, 1.0 ] ] |> ignore
    runDir root "new" 1.0 [ "P.ctrf.json", ctrfOf 0 [ "C", 0.0, 1.0 ] ] |> ignore
    File.WriteAllText(Path.Combine(root, "not-a-run.txt"), "")

    test <@ Insights.loadRuns root |> List.map (fun (id, _) -> id) = [ "new"; "old" ] @>
    test <@ List.isEmpty (Insights.loadRuns (Path.Combine(root, "missing"))) @>

let private projectRun (runId: string) (outcome: string) (spans: (string * float) list) : Insights.ProjectRun =
    { RunId = runId
      Project = "P"
      Outcome = Some outcome
      WallMs = None
      TimeoutSec = None
      LoadPerCoreMean = None
      Classes =
        spans
        |> List.map (fun (cls, sec) ->
            { Timing = timing cls 0.0 sec
              Contended = None
              LoadPerCoreMean = None }) }

[<Fact>]
let ``a class regressed when its span grew past the ratio and the floor over the median of recent greens`` () =
    let target =
        projectRun "now" "passed" [ "Grew", 30.0; "Same", 10.0; "Tiny", 0.3; "New", 50.0 ]

    // Newest first. The red run and the target itself are not baselines.
    let history =
        [ target
          projectRun "red" "failed" [ "Grew", 99.0 ]
          projectRun "g1" "passed" [ "Grew", 10.0; "Same", 10.0; "Tiny", 0.1 ]
          projectRun "g2" "passed" [ "Grew", 12.0; "Same", 9.0; "Tiny", 0.1 ]
          projectRun "g3" "passed" [ "Grew", 500.0 ] ]

    let found = Insights.regressions 2 target history

    match found with
    | [ r ] ->
        test <@ r.Class = "Grew" @>
        test <@ r.SpanMs = 30_000.0 @>
        // g1 and g2 only: the last 2 greens.
        test <@ r.BaselineMedianMs = 11_000.0 @>
        test <@ r.Baselines = 2 @>
    | other -> failwith $"unexpected regressions: %A{other}"

[<Fact>]
let ``growth under load compares a class's median span when contended with its median when quiet`` () =
    let row cls sec contended : Insights.ClassRow =
        { Timing = timing cls 0.0 sec
          Contended = Some contended
          LoadPerCoreMean = None }

    let run (rows: Insights.ClassRow list) : Insights.ProjectRun =
        { projectRun "r" "passed" [] with
            Classes = rows }

    let growth =
        Insights.growth
            [ run [ row "Load.Sensitive" 10.0 false; row "Steady" 5.0 false ]
              run [ row "Load.Sensitive" 12.0 false; row "Steady" 5.0 true ]
              run [ row "Load.Sensitive" 33.0 true; row "QuietOnly" 1.0 false ] ]

    test <@ growth |> List.map (fun g -> g.Class, g.Ratio) = [ "Load.Sensitive", 3.0; "Steady", 1.0 ] @>

[<Fact>]
let ``the report names the long pole first, its tail, the budget headroom and what regressed`` () =
    let root = runsRoot ()

    let shape (pole: float) =
        [ "Suite.LongPoleTests", 0.0, pole
          "Suite.QuickTests", 0.0, 20.0
          "Suite.OtherTests", 5.0, 30.0 ]

    runDir root "g1" 20.0 [ "Suite.Tests.ctrf.json", ctrfOf 0 (shape 40.0) ]
    |> ignore

    runDir root "g2" 10.0 [ "Suite.Tests.ctrf.json", ctrfOf 0 (shape 44.0) ]
    |> ignore

    runDir root "now" 1.0 [ "Suite.Tests.ctrf.json", ctrfOf 0 (shape 90.0) ]
    |> ignore

    let text =
        Insights.report
            { Insights.Options.defaults with
                Budgets = Map.ofList [ "Suite.Tests", 600 ] }
            (Insights.loadRuns root)
        |> String.concat "\n"

    test <@ text.Contains "run now" @>
    test <@ text.Contains "Suite.Tests: passed, wall 90.0s of a 600s budget (15%, 510.0s headroom)" @>

    let slowest =
        text.Split('\n') |> Array.find (fun l -> l.Contains "Suite.LongPoleTests")

    test <@ slowest.Contains "90.0s" @>
    test <@ text.IndexOf "Suite.LongPoleTests" < text.IndexOf "Suite.OtherTests" @>
    test <@ text.Contains "serial tail: 60.0s, Suite.LongPoleTests alone" @>
    test <@ text.Contains "long pole: Suite.LongPoleTests, 100% of the wall" @>
    test <@ text.Contains "regressed vs the last 2 greens:" @>
    test <@ text.Contains "Suite.LongPoleTests 90.0s, median 42.0s (2.1x)" @>

[<Fact>]
let ``the report of a run that holds nothing says so`` () =
    test <@ Insights.report Insights.Options.defaults [] = [ "no test runs found" ] @>

[<Fact>]
let ``a tail of classes run one after another names the three longest and counts the rest`` () =
    let root = runsRoot ()

    // The pole ends at 90; four short classes then run one at a time, as a
    // non-parallel collection does after the parallel ones.
    runDir
        root
        "now"
        1.0
        [ "S.Tests.ctrf.json",
          ctrfOf
              0
              [ "S.Pole", 0.0, 90.0
                "S.Beside", 0.0, 60.0
                "S.A", 90.0, 91.0
                "S.B", 91.0, 93.0
                "S.C", 93.0, 93.5
                "S.D", 93.5, 93.7 ] ]
    |> ignore

    let text =
        Insights.report Insights.Options.defaults (Insights.loadRuns root)
        |> String.concat "\n"

    test <@ text.Contains "serial tail: 33.7s, one class at a time: S.Pole 30.0s, S.B 2.0s, S.A 1.0s, and 2 more" @>
    // 90 of 93.7 s: the pole.
    test <@ text.Contains "long pole: S.Pole, 96% of the wall" @>
    test <@ text.Contains "no earlier green of this project to compare with" @>
    test <@ text.Contains "(no budget known)" @>

[<Fact>]
let ``the report says when a run went over its budget, and when a profile measured the load`` () =
    let root = runsRoot ()

    let row cls sec contended load : Profile.Line =
        Profile.Class(
            timing cls 0.0 sec,
            { Samples = 2
              LoadPerCoreMean = Some load
              LoadPerCoreMax = Some load
              Contended = Some contended },
            Profile.ClassSource.Console
        )

    let profile (wallMs: int64) (outcome: string) (rows: Profile.Line list) =
        Profile.Start("P", at 0.0, 4, Some 10, Some 1.0)
        :: Profile.End(at 12.0, wallMs, None, None, outcome)
        :: rows
        |> List.map Profile.render
        |> String.concat "\n"

    runDir root "quiet" 5.0 [ "P.profile.jsonl", profile 4_000L "passed" [ row "P.Sensitive" 4.0 false 0.2 ] ]
    |> ignore

    runDir root "now" 1.0 [ "P.profile.jsonl", profile 12_000L "timed-out" [ row "P.Sensitive" 12.0 true 3.0 ] ]
    |> ignore

    let text =
        Insights.report Insights.Options.defaults (Insights.loadRuns root)
        |> String.concat "\n"

    test <@ text.Contains "P: timed-out, wall 12.0s OVER its 10s budget by 2.0s" @>
    test <@ text.Contains "contended (3.00 load/core)" @>
    test <@ text.Contains "load-sensitive classes" @>
    test <@ text.Contains "3.0x  P.Sensitive  quiet 4.0s, contended 12.0s" @>

    let quietText =
        Insights.report Insights.Options.defaults [ "quiet", Insights.loadRun (Path.Combine(root, "quiet")) ]
        |> String.concat "\n"

    test <@ quietText.Contains "quiet (0.20 load/core)" @>
    test <@ quietText.Contains "P: passed, wall 4.0s of a 10s budget (40%, 6.0s headroom)" @>

[<Fact>]
let ``unreadable or partial records leave a project's facts unknown, never a throw`` () =
    let root = runsRoot ()

    let dir =
        runDir
            root
            "odd"
            1.0
            [ "NotObject.timed-out.json", "[1]"
              "Torn.timed-out.json", "{ nope"
              "Partial.timed-out.json", """{"timeoutSec":"soon","spawnedAt":7}"""
              "NoEnd.profile.jsonl", Profile.render (Profile.Start("NoEnd", at 0.0, 2, None, None))
              "Empty.ctrf.json", "{}" ]

    let runs = Insights.loadRun dir |> List.map (fun p -> p.Project, p) |> Map.ofList

    for name in [ "NotObject"; "Torn"; "Partial" ] do
        let p = runs[name]
        test <@ p.TimeoutSec = None && p.WallMs = None && List.isEmpty p.Classes @>

    test <@ runs["NoEnd"].Outcome = None && runs["NoEnd"].WallMs = None @>
    test <@ runs["Empty"].WallMs = None && List.isEmpty runs["Empty"].Classes @>

    let text =
        Insights.report Insights.Options.defaults [ "odd", Insights.loadRun dir ]
        |> String.concat "\n"

    test <@ text.Contains "NoEnd: outcome unknown, wall unknown" @>
    test <@ text.Contains "no class timings" @>

// --- slice 4: one project's insight as a record, its JSON, and the run's load ---

/// A killed host's profile: 4 cores, a 10 s budget, two load samples, and the classes it
/// printed before the kill.
let private killedProfile (load1: float) =
    [ Profile.Start("Killed", at 0.0, 4, Some 10, Some 1.0)
      Profile.Sampled
          { At = at 2.0
            Load1 = Some load1
            CpuMs = None }
      Profile.Sampled
          { At = at 7.0
            Load1 = Some load1
            CpuMs = None }
      Profile.End(at 12.0, 12_000L, None, Some load1, "timed-out")
      Profile.Class(
          timing "K.Pole" 0.0 9.0,
          { Samples = 2
            LoadPerCoreMean = Some(load1 / 4.0)
            LoadPerCoreMax = Some(load1 / 4.0)
            Contended = Some(load1 / 4.0 >= 1.0) },
          Profile.ClassSource.Console
      )
      Profile.Class(
          timing "K.Quick" 0.0 1.0,
          { Samples = 1
            LoadPerCoreMean = None
            LoadPerCoreMax = None
            Contended = None },
          Profile.ClassSource.Console
      ) ]
    |> List.map Profile.render
    |> String.concat "\n"

[<Fact>]
let ``summarize names a killed project's long pole, its share of the wall, the headroom and the run's load`` () =
    let root = runsRoot ()
    let dir = runDir root "r" 0.0 [ "Killed.profile.jsonl", killedProfile 8.0 ]

    let p = Insights.loadRun dir |> List.exactlyOne
    test <@ p.LoadPerCoreMean = Some 2.0 @>

    let i = Insights.summarize Map.empty p
    test <@ i.RunId = "r" && i.Project = "Killed" && i.Outcome = Some "timed-out" @>
    test <@ i.WallMs = Some 12_000.0 && i.TimeoutSec = Some 10 @>
    test <@ i.HeadroomMs = Some -2_000.0 @>

    test
        <@
            i.LongPole = Some
                { Class = "K.Pole"
                  SpanMs = 9_000.0
                  Share = Some 0.75 }
        @>

    test <@ i.Slowest |> List.map _.Class = [ "K.Pole"; "K.Quick" ] @>
    test <@ i.LoadPerCoreMean = Some 2.0 && i.Contended = Some true @>

[<Fact>]
let ``summarize has no long pole below half the wall, and no load without a profile`` () =
    let quiet =
        Insights.summarize
            (Map.ofList [ "P", 100 ])
            { projectRun "r" "passed" [ "A", 4.0; "B", 3.0 ] with
                WallMs = Some 10_000.0 }

    test <@ quiet.LongPole = None @>
    test <@ quiet.TimeoutSec = Some 100 && quiet.HeadroomMs = Some 90_000.0 @>

    test
        <@
            quiet.Slowest.Head = { Class = "A"
                                   SpanMs = 4_000.0
                                   Share = Some 0.4 }
        @>

    test <@ quiet.Contended = None && quiet.LoadPerCoreMean = None @>

    let unknownWall =
        Insights.summarize Map.empty (projectRun "r" "passed" [ "A", 4.0 ])

    test <@ unknownWall.HeadroomMs = None && unknownWall.LongPole = None @>
    test <@ unknownWall.Slowest.Head.Share = None @>

[<Fact>]
let ``an insight round-trips through its JSON, and a malformed one does not read`` () =
    let root = runsRoot ()
    let dir = runDir root "r" 0.0 [ "Killed.profile.jsonl", killedProfile 2.0 ]
    let i = Insights.loadRun dir |> List.exactlyOne |> Insights.summarize Map.empty

    let node = Insights.insightJson i
    test <@ Insights.tryReadInsight node = Some i @>
    test <@ (node["longPole"].Item("class").GetValue<string>()) = "K.Pole" @>
    test <@ (node["longPole"].Item("share").GetValue<float>()) = 0.75 @>
    test <@ (node["contended"].GetValue<bool>()) = false @>

    test <@ Insights.tryReadInsight (System.Text.Json.Nodes.JsonNode.Parse "[1]") = None @>
    test <@ Insights.tryReadInsight (System.Text.Json.Nodes.JsonNode.Parse """{"project":"P"}""") = None @>

[<Fact>]
let ``reportJson is the newest run's insights, or the run asked for, with its load-sensitive classes`` () =
    let root = runsRoot ()
    runDir root "old" 10.0 [ "Killed.profile.jsonl", killedProfile 1.0 ] |> ignore
    runDir root "new" 1.0 [ "Killed.profile.jsonl", killedProfile 8.0 ] |> ignore
    let runs = Insights.loadRuns root

    let parsed (options: Insights.Options) =
        System.Text.Json.Nodes.JsonNode.Parse(Insights.reportJson options runs)

    let newest = parsed Insights.Options.defaults
    test <@ (newest["runId"].GetValue<string>()) = "new" @>
    test <@ (newest["retained"].GetValue<int>()) = 2 @>

    let project = newest["projects"].AsArray()[0]
    test <@ Insights.tryReadInsight project = Some(Insights.summarize Map.empty (snd runs.Head).Head) @>

    // K.Pole: 9 s quiet (load 1 on 4 cores) and 9 s contended — a ratio of 1.
    test <@ newest["loadSensitive"].AsArray().Count = 1 @>
    test <@ (newest["loadSensitive"].Item(0).Item("class").GetValue<string>()) = "K.Pole" @>

    let old =
        parsed
            { Insights.Options.defaults with
                Run = Some "old" }

    test <@ (old["runId"].GetValue<string>()) = "old" @>

    let missing =
        parsed
            { Insights.Options.defaults with
                Run = Some "nope" }

    test <@ missing["runId"] = null && missing["projects"].AsArray().Count = 0 @>

[<Fact>]
let ``the text report can be aimed at one run, and says the run's load`` () =
    let root = runsRoot ()
    runDir root "old" 10.0 [ "Killed.profile.jsonl", killedProfile 8.0 ] |> ignore

    runDir root "new" 1.0 [ "Other.ctrf.json", ctrfOf 0 [ "O.A", 0.0, 1.0 ] ]
    |> ignore

    let text =
        Insights.report
            { Insights.Options.defaults with
                Run = Some "old" }
            (Insights.loadRuns root)
        |> String.concat "\n"

    test <@ text.StartsWith "run old" @>
    test <@ text.Contains "load over the run: 2.00 per core, contended" @>
    test <@ not (text.Contains "Other") @>

    test
        <@
            Insights.report
                { Insights.Options.defaults with
                    Run = Some "nope" }
                (Insights.loadRuns root) = [ "no retained run nope" ]
        @>

[<Fact>]
let ``a timeout line says the long pole, the overrun, the load and where to look`` () =
    let root = runsRoot ()
    let dir = runDir root "r1" 0.0 [ "Killed.profile.jsonl", killedProfile 8.0 ]
    let i = Insights.loadRun dir |> List.exactlyOne |> Insights.summarize Map.empty

    test
        <@
            Insights.timeoutLine i = "Killed timed out: long pole K.Pole (75% of the 12.0s wall), 2.0s over its 10s budget, contended (2.00 load/core) — fshw insights --run r1"
        @>

    let bare =
        { i with
            LongPole = None
            Slowest = []
            HeadroomMs = None
            TimeoutSec = None
            Contended = None
            LoadPerCoreMean = None }

    test
        <@
            Insights.timeoutLine bare = "Killed timed out: no class finished before the kill, load unknown — fshw insights --run r1"
        @>
