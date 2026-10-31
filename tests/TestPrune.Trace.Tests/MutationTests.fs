/// The mutation harness: make one target's probes throw, run the suite, and check that every
/// test the mutant breaks is one the recorded traces would have selected for a change to it.
module TestPrune.Trace.Tests.MutationTests

open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private row id kind assembly typeName memberName : ManifestRow =
    { Id = id
      Kind = kind
      Assembly = assembly
      TypeName = typeName
      Member = memberName
      Document = None
      FirstLine = 0
      LastLine = 0 }

let private manifest (rows: ManifestRow list) : Manifest =
    { Rows = Array.ofList rows
      Documents = Map.empty
      IdCount = 100 }

let private scope key (test: TestIdentity option) parents (ids: int list) : RecordedScope =
    { Key = key
      Test = test
      Parents = parents
      Links = []
      Ids = Array.ofList ids
      Inputs = []
      Children = [] }

let private testScope (cls: string) (meth: string) parents ids =
    scope
        $"T:%s{cls}.%s{meth}"
        (Some
            { Class = cls
              Method = meth
              Display = $"%s{cls}.%s{meth}" })
        parents
        ids

let private dump (scopes: RecordedScope list) : ProcessDump =
    { Pid = 1
      ParentScope = None
      Runtime = ".NET 10.0.0"
      Os = "OSX"
      Arch = "Arm64"
      IdCount = 100
      CpuMs = 0L
      Counters =
        { Test = 0L
          Class = 0L
          Collection = 0L
          Assembly = 0L
          Override = 0L
          StaticInit = 0L
          Ambient = 0L
          Overflow = 0L }
      Scopes = scopes }

let private outcome name o : TestOutcome = { Name = name; Outcome = o }

[<Fact>]
let ``targets are the hit user methods outside the test assembly, one per member`` () =
    let m =
        manifest
            [ row 1 UserMethod "Lib" "Lib.A" "f"
              row 2 UserMethod "Lib" "Lib.A" "f"
              row 3 UserMethod "Lib" "Lib.A" "g"
              row 4 UserMethod "Lib" "Lib.B" "unhit"
              row 5 GeneratedMethod "Lib" "Lib.A+f@3" "Invoke"
              row 6 UserMethod "Tests" "Tests.T" "a test"
              row 7 StaticCtor "Lib" "Lib.A" ".cctor" ]

    let targets = Mutation.targets "Tests" (set [ 1; 3; 5; 6; 7 ]) m

    test <@ targets = [ { Name = "Lib.A::f"; Ids = [ 1; 2 ] }; { Name = "Lib.A::g"; Ids = [ 3 ] } ] @>

[<Fact>]
let ``a test's trace is every id it holds or inherits, and a test with no scope has none`` () =
    let m = manifest []

    let dumps =
        [ dump
              [ scope "C:Ns.C" None [] [ 9 ]
                testScope "Ns.C" "one" [ "C:Ns.C" ] [ 1 ]
                testScope "Ns.C" "two" [] [ 2 ] ] ]

    let traces =
        Mutation.tracesOf
            m
            dumps
            [ outcome "Ns.C.one" Passed
              outcome "Ns.C.two(x: 1)" Passed
              outcome "Ns.C.three" Passed ]

    test
        <@
            traces = Map
                [ "Ns.C.one", Some(set [ 1; 9 ])
                  "Ns.C.two(x: 1)", Some(set [ 2 ])
                  "Ns.C.three", None ]
        @>

let private traces =
    Map
        [ "hits-f", Some(set [ 1 ])
          "hits-g", Some(set [ 3 ])
          "untraced", None
          "red-before", Some(set [ 1 ]) ]

let private baseline =
    [ outcome "hits-f" Passed
      outcome "hits-g" Passed
      outcome "untraced" Passed
      outcome "red-before" Failed ]

let private f: Mutation.Target = { Name = "Lib.A::f"; Ids = [ 1; 2 ] }

[<Fact>]
let ``a change selects the tests whose trace names the target, and every test without a trace`` () =
    test <@ Mutation.selected traces f = set [ "hits-f"; "untraced"; "red-before" ] @>

[<Fact>]
let ``a test the mutant breaks that the trace did not select is a miss`` () =
    let mutant =
        Ok
            [ outcome "hits-f" Failed
              outcome "hits-g" Failed
              outcome "untraced" Passed
              outcome "red-before" Failed ]

    let s = Mutation.judge traces baseline f mutant

    test <@ s.Outcome = Mutation.Measured @>
    test <@ s.Failing = [ "hits-f"; "hits-g" ] @>
    test <@ s.Missed = [ "hits-g" ] @>
    test <@ s.Selected = 3 @>

[<Fact>]
let ``a test whose row the mutant run lost counts as failing`` () =
    let s =
        Mutation.judge traces baseline f (Ok [ outcome "hits-g" Passed; outcome "untraced" Passed ])

    test <@ s.Failing = [ "hits-f" ] && s.Missed.IsEmpty @>

[<Fact>]
let ``a mutant nothing fails is no-failure, and a run with no report is run-failed`` () =
    let quiet = Mutation.judge traces baseline f (Ok baseline)
    test <@ quiet.Outcome = Mutation.NoFailure && quiet.Failing.IsEmpty @>

    let crashed = Mutation.judge traces baseline f (Error "no CTRF report")
    test <@ crashed.Outcome = Mutation.RunFailed "no CTRF report" @>

[<Fact>]
let ``recall counts caught failing tests over failing tests, across measured samples`` () =
    let s1 =
        Mutation.judge
            traces
            baseline
            f
            (Ok [ outcome "hits-f" Failed; outcome "hits-g" Failed; outcome "untraced" Passed ])

    let s2 = Mutation.judge traces baseline f (Ok baseline)
    let report = Mutation.summarize 4 [ "red-before" ] 2 [ s1; s2 ]

    test <@ report.Measured = 1 && report.FailingTotal = 2 && report.MissedTotal = 1 @>
    test <@ report.Recall = Some 0.5 @>
    test <@ not (Mutation.passes report) @>
    test <@ (Mutation.render report).Contains "missed  hits-g  by Lib.A::f" @>

    let clean = Mutation.summarize 4 [] 2 [ s2 ]
    test <@ clean.Recall = None && not (Mutation.passes clean) @>

[<Fact>]
let ``the tests a baseline did not pass are listed and not judged`` () =
    test <@ Mutation.notPassed baseline = [ "red-before" ] @>

[<Fact>]
let ``the report says when nothing was measured, when it passes, and why a run failed`` () =
    let crashed = Mutation.judge traces baseline f (Error "no CTRF report")
    let nothing = Mutation.summarize 4 [] 1 [ crashed ]

    test <@ (Mutation.render nothing).StartsWith "mutation  NOTHING MEASURED  recall n/a" @>
    test <@ (Mutation.render nothing).Contains "run-failed: no CTRF report" @>

    let caught =
        Mutation.judge
            traces
            baseline
            f
            (Ok [ outcome "hits-f" Failed; outcome "hits-g" Passed; outcome "untraced" Passed ])

    let passing = Mutation.summarize 4 [ "red-before" ] 1 [ caught ]
    let text = Mutation.render passing

    test <@ Mutation.passes passing && text.StartsWith "mutation  PASS  recall 1/1 = 1.0000" @>

    test
        <@
            text.Contains "baseline: 1 test(s) did not pass"
            && text.Contains "Lib.A::f  measured"
        @>
