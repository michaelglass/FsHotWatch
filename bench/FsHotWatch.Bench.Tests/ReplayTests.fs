module FsHotWatch.Bench.Tests.ReplayTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench
open FsHotWatch.Bench.ReplayModel

let private typeFault: FaultSpec =
    { Kind = "type"
      File = "src/A.fs"
      Append = "let x : int = \"no\"" }

let private testFault: FaultSpec =
    { Kind = "test"
      File = "tests/T.fs"
      Append = "[<Fact>] let t () = Assert.True false" }

let private plan commits every offset allKinds : Plan =
    { Source = "first_ancestors(main, 5)"
      Commits = commits
      FaultEvery = every
      FaultOffset = offset
      AllKinds = allKinds
      Faults = [ typeFault; testFault ] }

// ─── Plan and steps ──────────────────────────────────────────────────────────

[<Fact>]
let ``every commit gets a clean step, oldest first, and no faults when sampling is off`` () =
    let s = steps (plan [ "a"; "b"; "c" ] 0 0 false)
    test <@ s |> List.map (fun x -> x.Commit, x.Phase) = [ "a", Phase.Clean; "b", Phase.Clean; "c", Phase.Clean ] @>
    test <@ s |> List.map _.Index = [ 0; 1; 2 ] @>

[<Fact>]
let ``sampled commits rotate through the fault kinds after their clean step`` () =
    let s = steps (plan [ "a"; "b"; "c"; "d"; "e" ] 2 0 false)

    let expected =
        [ "a", "clean"
          "a", "fault:type"
          "b", "clean"
          "c", "clean"
          "c", "fault:test"
          "d", "clean"
          "e", "clean"
          "e", "fault:type" ]

    test <@ s |> List.map (fun x -> x.Commit, Phase.token x.Phase) = expected @>

[<Fact>]
let ``an offset delays the first sample and all-kinds seeds every kind at each sample`` () =
    let s = steps (plan [ "a"; "b"; "c"; "d" ] 3 1 true)

    test
        <@
            s
            |> List.filter (fun x -> x.Phase <> Phase.Clean)
            |> List.map (fun x -> x.Commit, Phase.token x.Phase) = [ "b", "fault:type"; "b", "fault:test" ]
        @>

[<Fact>]
let ``a phase token round-trips and an unknown one is refused`` () =
    for p in [ Phase.Clean; Phase.Fault "analyzer" ] do
        test <@ Phase.parse (Phase.token p) = Some p @>

    test <@ Phase.parse "dirty" = None @>

[<Fact>]
let ``a plan round-trips through its file form`` () =
    let p = plan [ "a"; "b" ] 4 2 true
    test <@ Plan.ofJson (Plan.toJson p) = Ok p @>

[<Fact>]
let ``pending leaves out exactly the steps the ledger already holds`` () =
    let all = steps (plan [ "a"; "b"; "c" ] 2 0 false)
    let done' = set [ "a", "clean"; "a", "fault:type" ]

    test <@ pending all done' |> List.map stepKey = [ "b", "clean"; "c", "clean"; "c", "fault:test" ] @>

// ─── Diagnostics ─────────────────────────────────────────────────────────────

let private errorFile =
    """[
  { "column": 4, "detail": "long", "line": 445, "message": "single tuple", "severity": "info" },
  { "column": 1, "detail": null, "line": 2, "message": "boom", "severity": "error" }
]"""

[<Fact>]
let ``an error-ledger file names its plugin and its file relative to the checkout`` () =
    let ds =
        Diagnostics.parseErrorFile "/w/repo" "fcs---w-repo-src-Lib-Json.fs.json" errorFile

    test
        <@
            ds = [ { Plugin = "fcs"
                     File = "src-Lib-Json.fs"
                     Line = 445
                     Column = 4
                     Severity = "info"
                     Code = ""
                     Message = "single tuple" }
                   { Plugin = "fcs"
                     File = "src-Lib-Json.fs"
                     Line = 2
                     Column = 1
                     Severity = "error"
                     Code = ""
                     Message = "boom" } ]
        @>

[<Fact>]
let ``an error-ledger file outside the checkout keeps its whole name, and a non-ledger name is skipped`` () =
    let ds = Diagnostics.parseErrorFile "/w/repo" "lint---elsewhere-X.fs.json" errorFile
    test <@ ds |> List.map (fun d -> d.Plugin, d.File) |> List.distinct = [ "lint", "-elsewhere-X.fs" ] @>
    test <@ Diagnostics.parseErrorFile "/w/repo" "notes.json" errorFile = [] @>

[<Fact>]
let ``a findings answer is read past any leading noise, with codes`` () =
    let text =
        """  [config] Loaded .fshw.json
{"schema":"fshw-findings-v1","outcome":"failing","exit":1,
 "findings":[{"file":"src/A.fs","line":3,"column":7,"severity":"error","code":"MGA-NOOBJ-001","message":"no obj"}]}"""

    test
        <@
            Diagnostics.parseFindings text = Ok(
                "failing",
                [ { Plugin = "findings"
                    File = "src/A.fs"
                    Line = 3
                    Column = 7
                    Severity = "error"
                    Code = "MGA-NOOBJ-001"
                    Message = "no obj" } ]
            )
        @>

    test <@ Result.isError (Diagnostics.parseFindings "no json here") @>

let private d plugin file line msg : Diagnostic =
    { Plugin = plugin
      File = file
      Line = line
      Column = 1
      Severity = "error"
      Code = ""
      Message = msg }

[<Fact>]
let ``the diff is a multiset difference over the compared plugins only`` () =
    let replay =
        [ d "fcs" "A" 1 "x"
          d "fcs" "A" 1 "x"
          d "fcs" "B" 2 "y"
          d "test-prune" "T" 1 "t" ]

    let forced = [ d "fcs" "A" 1 "x"; d "fcs" "C" 3 "z" ]
    let r = Diagnostics.diff (set [ "fcs" ]) replay forced
    test <@ r.OnlyReplay = [ d "fcs" "A" 1 "x"; d "fcs" "B" 2 "y" ] @>
    test <@ r.OnlyForced = [ d "fcs" "C" 3 "z" ] @>
    test <@ Diagnostics.agrees r = false @>
    test <@ Diagnostics.agrees (Diagnostics.diff (set [ "fcs" ]) forced forced) @>

[<Fact>]
let ``the diff ignores the order the two runs listed their diagnostics in`` () =
    let a = [ d "fcs" "A" 1 "x"; d "lint" "B" 2 "y" ]
    test <@ Diagnostics.agrees (Diagnostics.diff (set [ "fcs"; "lint" ]) a (List.rev a)) @>

// ─── Shadow lines ────────────────────────────────────────────────────────────

let private shadowJson command direction today lookup missing failing =
    let arr (xs: string list) =
        xs |> List.map (sprintf "\"%s\"") |> String.concat "," |> sprintf "[%s]"

    $"""{{"schema":"fshw-shadow-v1","at":"2026-11-01T00:00:00Z","command":"%s{command}","tree":"sha256:t","producer":"p",
"today":{{"outcome":"%s{today}","class":"%s{today}","exit":0}},
"lookup":{{"outcome":"%s{lookup}","missing":%s{arr missing},"failing":%s{arr failing}}},
"agree":%s{(if direction = "agree" then "true" else "false")},"direction":"%s{direction}","recorded":3}}"""
        .Replace("\n", "")

[<Fact>]
let ``a shadow line is read with its direction and its missing kinds`` () =
    let line =
        shadowJson "check" "stricter" "green" "missing" [ "test:B.Tests"; "test:A.Tests"; "plugin:lint" ] []

    let s = (Shadow.parse line).Value
    test <@ s.Command = "check" && s.Direction = "stricter" && not s.Agree @>

    test
        <@
            s.Today = "green"
            && s.Lookup = "missing"
            && s.Recorded = Some 3
            && s.Tree = "sha256:t"
        @>

    test <@ Shadow.missingKinds s = [ "plugin"; "test" ] @>
    test <@ Shadow.classOf s = "stricter check: today green, lookup missing, missing plugin+test" @>

[<Fact>]
let ``a red lookup's class names what failed, and a torn line is not a shadow line`` () =
    let s =
        (Shadow.parse (shadowJson "verdict" "red-vs-missing" "red" "missing" [] [ "plugin:build" ])).Value

    test <@ Shadow.classOf s = "red-vs-missing verdict: today red, lookup missing, failing plugin:build" @>
    test <@ Shadow.parse "{\"schema\":\"fshw-shadow-v1\",\"comm" = None @>
    test <@ Shadow.parse "" = None @>

[<Fact>]
let ``new shadow lines are the ones past the count taken before the run`` () =
    let before = [ "l1"; "l2" ]
    let after = [ "l1"; "l2"; "l3"; "l4" ]
    test <@ Shadow.since (List.length before) after = [ "l3"; "l4" ] @>
    // A log that was replaced (shorter than before) is read whole.
    test <@ Shadow.since 5 after = after @>

// ─── Ledger and resume ───────────────────────────────────────────────────────

let private run mode exit outcome (shadow: ShadowLine list) : RunResult =
    { Mode = mode
      Exit = exit
      TimedOut = false
      ElapsedMs = 1000L
      Verdict =
        Some
            { Command = "check"
              Outcome = outcome
              Exit = exit
              Tree = "sha256:t" }
      Shadow = shadow
      Diagnostics = 2
      Findings = Some "clean"
      RecordsLog = [ "check-records: this daemon: 2 replayed" ]
      Artifacts = "/out/0-a-clean/" + mode }

let private agreeLine =
    (Shadow.parse (shadowJson "check" "agree" "green" "green" [] [])).Value

let private strictLine =
    (Shadow.parse (shadowJson "check" "stricter" "green" "missing" [ "test:A" ] [])).Value

let private entry index commit phase (replay: RunResult) (forced: RunResult option) diff fault : Entry =
    { Index = index
      Commit = commit
      Phase = phase
      StartedAt = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
      FinishedAt = DateTimeOffset(2026, 11, 1, 0, 10, 0, TimeSpan.Zero)
      Replay = replay
      Forced = forced
      Diff = diff
      Fault = fault }

let private noDiff: Diff = { OnlyReplay = []; OnlyForced = [] }

[<Fact>]
let ``a ledger entry round-trips through its line`` () =
    let e =
        entry
            3
            "abc"
            (Phase.Fault "type")
            (run "replay" 1 "red" [ agreeLine ])
            (Some(run "forced" 1 "red" [ strictLine ]))
            { OnlyReplay = [ d "fcs" "A" 1 "x" ]
              OnlyForced = [] }
            (Some
                { Kind = "type"
                  File = "src/A.fs"
                  ReplayRed = true
                  LookupRed = true
                  ForcedRed = true })

    test <@ Ledger.parseLine (Ledger.toLine e) = Some e @>

[<Fact>]
let ``reading a ledger skips a line torn by an interruption and keeps every whole one`` () =
    let e1 = entry 0 "a" Phase.Clean (run "replay" 0 "green" []) None noDiff None
    let e2 = entry 1 "b" Phase.Clean (run "replay" 0 "green" []) None noDiff None

    let text =
        Ledger.toLine e1 + "\n" + Ledger.toLine e2 + "\n" + "{\"schema\":\"fshw-replay"

    test <@ Ledger.parse text = [ e1; e2 ] @>
    test <@ Ledger.completed [ e1; e2 ] = set [ "a", "clean"; "b", "clean" ] @>

[<Fact>]
let ``appending to a ledger file and reading it back resumes after the last whole step`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-replay-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        let path = Path.Combine(dir, "ledger.jsonl")
        let p = plan [ "a"; "b" ] 0 0 false
        let e1 = entry 0 "a" Phase.Clean (run "replay" 0 "green" []) None noDiff None
        Ledger.append path e1
        File.AppendAllText(path, "{\"torn")
        let read = Ledger.read path
        test <@ read = [ e1 ] @>
        test <@ pending (steps p) (Ledger.completed read) |> List.map stepKey = [ "b", "clean" ] @>
        // A torn tail is cut before the next append, so the new line stays whole.
        let e2 = entry 1 "b" Phase.Clean (run "replay" 0 "green" []) None noDiff None
        Ledger.append path e2
        test <@ Ledger.read path = [ e1; e2 ] @>
    finally
        Directory.Delete(dir, true)

// ─── Fault judgement ─────────────────────────────────────────────────────────

[<Fact>]
let ``a fault is detected when the replayed verdict, its lookup and the forced verdict are all red`` () =
    let redLookup =
        (Shadow.parse (shadowJson "check" "agree" "red" "red" [] [ "plugin:build" ])).Value

    let missingLookup =
        (Shadow.parse (shadowJson "check" "red-vs-missing" "red" "missing" [ "test:A" ] [])).Value

    let f =
        Fault.judge typeFault (run "replay" 1 "red" [ redLookup ]) (Some(run "forced" 1 "red" []))

    test <@ f.ReplayRed && f.LookupRed && f.ForcedRed @>

    let g =
        Fault.judge typeFault (run "replay" 1 "red" [ missingLookup ]) (Some(run "forced" 0 "green" []))

    test <@ g.ReplayRed && not g.LookupRed && not g.ForcedRed @>

[<Fact>]
let ``a verdict is red only on a failures exit, never on no-verdict or incomplete`` () =
    test <@ Fault.isRed (run "replay" 1 "red" []) @>
    test <@ not (Fault.isRed (run "replay" 3 "incomplete" [])) @>
    test <@ not (Fault.isRed (run "replay" 2 "incomplete" [])) @>

[<Fact>]
let ``appending a fault keeps the file's last line and ends with a newline`` () =
    test <@ Fault.applyTo "module A\nlet a = 1" "let b = 2" = "module A\nlet a = 1\n\nlet b = 2\n" @>
    test <@ Fault.applyTo "module A\n" "let b = 2" = "module A\n\nlet b = 2\n" @>

// ─── Summary ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``the summary counts shadow directions per run mode, disagreement classes, diffs and detections`` () =
    let cleanA =
        entry
            0
            "a"
            Phase.Clean
            (run "replay" 0 "green" [ agreeLine ])
            (Some(run "forced" 0 "green" [ agreeLine ]))
            noDiff
            None

    let cleanB =
        entry
            1
            "b"
            Phase.Clean
            (run "replay" 0 "green" [ strictLine; agreeLine ])
            (Some(run "forced" 1 "red" [ agreeLine ]))
            { OnlyReplay = []
              OnlyForced = [ d "fcs" "A" 1 "x"; d "fcs" "B" 1 "y"; d "lint" "C" 1 "z" ] }
            None

    let faultB =
        entry
            1
            "b"
            (Phase.Fault "type")
            (run "replay" 1 "red" [ agreeLine ])
            (Some(run "forced" 1 "red" []))
            noDiff
            (Some
                { Kind = "type"
                  File = "src/A.fs"
                  ReplayRed = true
                  LookupRed = false
                  ForcedRed = true })

    let s = Summary.summarize [ cleanA; cleanB; faultB ]
    test <@ s.Commits = 2 && s.CleanSteps = 2 && s.FaultSteps = 1 @>

    test
        <@
            s.ShadowByDirection = [ ("forced", "check", "agree"), 2
                                    ("replay", "check", "agree"), 3
                                    ("replay", "check", "stricter"), 1 ]
        @>

    test <@ s.ShadowDisagreements = [ "replay stricter check: today green, lookup missing, missing test", 1 ] @>
    test <@ s.DiffSteps = 1 @>
    test <@ s.DiffByPlugin = [ ("fcs", "forced-only"), 2; ("lint", "forced-only"), 1 ] @>
    test <@ s.VerdictMismatches = [ "b", "green", "red" ] @>

    test
        <@
            s.Faults = [ { Kind = "type"
                           Seeded = 1
                           ReplayRed = 1
                           LookupRed = 0
                           ForcedRed = 1
                           DiagnosticsAgree = 1 } ]
        @>

    let text = Summary.render s
    test <@ text.Contains "commits replayed: 2" @>
    test <@ text.Contains "stricter" @>

[<Fact>]
let ``a forced run is due at every seeded fault and at every K-th commit's clean step`` () =
    let at index phase =
        { Index = index
          Commit = "c"
          Phase = phase }

    test <@ forcedDue 1 (at 7 Phase.Clean) @>
    test <@ forcedDue 5 (at 10 Phase.Clean) && not (forcedDue 5 (at 11 Phase.Clean)) @>
    test <@ forcedDue 5 (at 11 (Phase.Fault "type")) @>
    test <@ not (forcedDue 0 (at 0 Phase.Clean)) && forcedDue 0 (at 3 (Phase.Fault "test")) @>

[<Fact>]
let ``a forced run that published no verdict is not a verdict mismatch, only green against red is`` () =
    let e forcedExit forcedOutcome : Entry =
        { Index = 0
          Commit = "a"
          Phase = Phase.Clean
          StartedAt = DateTimeOffset.UnixEpoch
          FinishedAt = DateTimeOffset.UnixEpoch
          Replay = run "replay" 0 "green" []
          Forced = Some(run "forced" forcedExit forcedOutcome [])
          Diff = { OnlyReplay = []; OnlyForced = [] }
          Fault = None }

    test <@ (Summary.summarize [ e 3 "incomplete" ]).VerdictMismatches = [] @>
    test <@ (Summary.summarize [ e 1 "red" ]).VerdictMismatches = [ "a", "green", "red" ] @>

[<Fact>]
let ``a verdict's test runs are named in the order it lists them, each once`` () =
    let verdict =
        """{"runs":[{"runId":"b","suites":[]},{"runId":"a","suites":[]},{"runId":"b"}],"runId":"b"}"""

    test <@ Diagnostics.verdictRunIds verdict = [ "b"; "a" ] @>
    test <@ Diagnostics.verdictRunIds "{}" = [] @>
    test <@ Diagnostics.verdictRunIds "not json" = [] @>

[<Fact>]
let ``a step whose forced run did not conclude is incomparable, not a diagnostics disagreement`` () =
    let interrupted: Entry =
        { Index = 50
          Commit = "c"
          Phase = Phase.Clean
          StartedAt = DateTimeOffset.UnixEpoch
          FinishedAt = DateTimeOffset.UnixEpoch
          Replay = run "replay" 0 "green" []
          Forced = Some(run "forced" 2 "incomplete" [])
          Diff =
            { OnlyReplay = [ d "fcs" "A" 1 "x" ]
              OnlyForced = [] }
          Fault = None }

    let s = Summary.summarize [ interrupted ]
    test <@ s.DiffSteps = 0 && s.DiffByPlugin = [] @>
    test <@ s.Incomparable = [ "c" ] @>
    test <@ (Summary.render s).Contains "not compared" @>
