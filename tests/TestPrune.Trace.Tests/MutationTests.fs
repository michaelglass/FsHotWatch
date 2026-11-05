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

/// A trace holding `xs` and reflecting over no union.
let private ids (xs: int list) : Mutation.TestTrace = { Ids = set xs; Types = Set.empty }

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

    test
        <@
            targets = [ { Name = "Lib.A::f"
                          Ids = [ 1; 2 ]
                          Types = [] }
                        { Name = "Lib.A::g"
                          Ids = [ 3 ]
                          Types = [] } ]
        @>

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
                [ "Ns.C.one", Some(ids [ 1; 9 ])
                  "Ns.C.two(x: 1)", Some(ids [ 2 ])
                  "Ns.C.three", None ]
        @>

let private traces =
    Map
        [ "hits-f", Some(ids [ 1 ])
          "hits-g", Some(ids [ 3 ])
          "untraced", None
          "red-before", Some(ids [ 1 ]) ]

let private baseline =
    [ outcome "hits-f" Passed
      outcome "hits-g" Passed
      outcome "untraced" Passed
      outcome "red-before" Failed ]

let private f: Mutation.Target =
    { Name = "Lib.A::f"
      Ids = [ 1; 2 ]
      Types = [] }

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

[<Fact>]
let ``the JSON report names each outcome as render does, with a failed run's reason`` () =
    let measured =
        Mutation.judge traces baseline f (Ok [ outcome "hits-f" Failed; outcome "untraced" Passed ])

    let quiet = Mutation.judge traces baseline f (Ok baseline)
    let crashed = Mutation.judge traces baseline f (Error "no CTRF report")
    let report = Mutation.summarize 4 [] 1 [ measured; quiet; crashed ]

    let json = System.Text.Json.JsonSerializer.Serialize(Mutation.toJson report)

    use doc = System.Text.Json.JsonDocument.Parse json
    let samples = doc.RootElement.GetProperty("Samples").EnumerateArray() |> List.ofSeq

    let named =
        samples
        |> List.map (fun s ->
            let reason = s.GetProperty("Reason")

            s.GetProperty("Outcome").GetString(),
            (if reason.ValueKind = System.Text.Json.JsonValueKind.Null then
                 None
             else
                 Some(reason.GetString())))

    test <@ named = [ "measured", None; "no-failure", None; "run-failed", Some "no CTRF report" ] @>

    test <@ (Mutation.render report).Contains "run-failed: no CTRF report" @>

// --- samples from real commits, stored (AUTOMATION-975, plan Task 7) --------------------

let private diff =
    """diff --git a/src/Lib/A.fs b/src/Lib/A.fs
index 1111111..2222222 100644
--- a/src/Lib/A.fs
+++ b/src/Lib/A.fs
@@ -3 +3 @@ module A
-let f () = 1
+let f () = 2
@@ -10,0 +11,3 @@ let g () =
+    a
+    b
+    c
@@ -20,2 +23,0 @@ let h () =
-    gone
-    gone too
diff --git a/src/Lib/New.fs b/src/Lib/New.fs
new file mode 100644
--- /dev/null
+++ b/src/Lib/New.fs
@@ -0,0 +1,2 @@
+module New
+let n () = 0
diff --git a/src/Lib/Old.fs b/src/Lib/Old.fs
deleted file mode 100644
--- a/src/Lib/Old.fs
+++ /dev/null
@@ -1 +0,0 @@
-module Old
"""

[<Fact>]
let ``a unified diff's changed lines are read on the new side, a deletion marking the lines around it`` () =
    test <@ Mutation.changedSpans diff = Map [ "src/Lib/A.fs", [ 3, 3; 11, 13; 23, 24 ]; "src/Lib/New.fs", [ 1, 2 ] ] @>

    test <@ Mutation.changedSpans "" |> Map.isEmpty @>
    // A hunk header outside a file, and a context-free one-line hunk without a count.
    test <@ Mutation.changedSpans "@@ -1 +1 @@\n+++ b/x.fs\n@@ -4 +5 @@\n" = Map [ "x.fs", [ 5, 5 ] ] @>
    test <@ Mutation.changedSpans "+++ y.fs\n@@ -1,0 +2,0 @@\n" = Map [ "y.fs", [ 2, 3 ] ] @>

[<Fact>]
let ``the changed targets are the repository methods whose lines a change touches`` () =
    let root = "/repo"

    let at id typeName memberName doc first last : ManifestRow =
        { row id UserMethod "Lib" typeName memberName with
            Document = Some(System.IO.Path.Combine(root, doc))
            FirstLine = first
            LastLine = last }

    let m =
        manifest
            [ at 0 "A" "f" "src/Lib/A.fs" 3 3
              at 1 "A" "g" "src/Lib/A.fs" 9 12
              at 2 "A" "untouched" "src/Lib/A.fs" 30 40
              at 3 "A" "h" "src/Lib/A.fs" 21 23
              at 4 "Other" "x" "src/Lib/Other.fs" 1 99
              { at 5 "T" "test" "src/Lib/A.fs" 3 3 with
                  Assembly = "Tests" } ]

    test <@ Mutation.changedTargets root "Tests" (Mutation.changedSpans diff) m = set [ "A::f"; "A::g"; "A::h" ] @>

[<Fact>]
let ``a report becomes stored samples: measured, quiet, failed, and changed methods no test ran`` () =
    let report =
        { Mutation.summarize
              10
              []
              3
              [ { Target = "A::f"
                  Kind = TraceStore.SymbolMutant
                  Outcome = Mutation.Measured
                  Failing = [ "t1"; "t2" ]
                  Selected = 4
                  Missed = [ "t2" ] }
                { Target = "A::g"
                  Kind = TraceStore.SymbolMutant
                  Outcome = Mutation.NoFailure
                  Failing = []
                  Selected = 1
                  Missed = [] }
                { Target = "A::h"
                  Kind = TraceStore.UnionTypeMutant
                  Outcome = Mutation.RunFailed "no report"
                  Failing = []
                  Selected = 0
                  Missed = [] } ] with
            NotMutable = [ "A::cold" ] }

    let at = System.DateTimeOffset(2026, 11, 5, 0, 0, 0, System.TimeSpan.Zero)
    let samples = Mutation.toSamples "P" at "diff:abc" report

    // Each judged sample keeps its kind; a changed method no test ran is a symbol.
    test
        <@
            samples |> List.map _.Kind = [ TraceStore.SymbolMutant
                                           TraceStore.SymbolMutant
                                           TraceStore.UnionTypeMutant
                                           TraceStore.SymbolMutant ]
        @>

    test
        <@
            samples
            |> List.map (fun s -> s.Symbol, s.Outcome, s.Failing, s.Selected, s.Missed) = [ "A::f",
                                                                                            TraceStore.Measured,
                                                                                            2,
                                                                                            4,
                                                                                            [ "t2" ]
                                                                                            "A::g",
                                                                                            TraceStore.NoFailure,
                                                                                            0,
                                                                                            1,
                                                                                            []
                                                                                            "A::h",
                                                                                            TraceStore.RunFailed,
                                                                                            0,
                                                                                            0,
                                                                                            []
                                                                                            "A::cold",
                                                                                            TraceStore.NotMutable,
                                                                                            0,
                                                                                            0,
                                                                                            [] ]
        @>

    test
        <@
            samples
            |> List.forall (fun s -> s.TestProject = "P" && s.TreeHash = "diff:abc" && s.RecordedAt = at)
        @>

    test <@ (Mutation.render report).Contains "not mutable: 1 changed method(s) no test ran: A::cold" @>

// --- union-type samples (AUTOMATION-1495, phase-2 plan Task 7 with Task 11) ---------------

[<Fact>]
let ``union targets are the reflected unions outside the test assembly`` () =
    let m =
        manifest
            [ row 1 UnionCase "Lib" "Lib.Shapes+Colour" "Red"
              row 2 UnionCase "Lib" "Lib.Shapes+Colour" "Green"
              row 3 UnionCase "Lib" "Lib.Shapes+Unseen" "A"
              row 4 UnionCase "Tests" "Tests.Local" "X"
              row 5 UserMethod "Lib" "Lib.Shapes" "f" ]

    let reflected = set [ "Lib.Shapes+Colour"; "Tests.Local"; "System.Object" ]

    test
        <@
            Mutation.unionTargets "Tests" reflected m = [ { Name = "Lib.Shapes+Colour"
                                                            Ids = []
                                                            Types = [ "Lib.Shapes+Colour" ] } ]
        @>

[<Fact>]
let ``a test's trace names the unions it and what it inherits reflected over`` () =
    let reflect (name: string) : RecordedInput =
        { Kind = TypeReflected
          Path = "/repo/bin/Lib.dll|" + name }

    let dumps =
        [ dump
              [ { scope "C:Ns.C" None [] [] with
                    Inputs = [ reflect "Lib.Shapes+Colour" ] }
                { testScope "Ns.C" "one" [ "C:Ns.C" ] [ 1 ] with
                    Inputs =
                        [ reflect "Lib.Shapes+Size"
                          { Kind = TypeReflected
                            Path = "no separator" }
                          { Kind = FileRead
                            Path = "/repo/a.txt" } ] }
                testScope "Ns.C" "two" [] [ 2 ] ] ]

    let traces =
        Mutation.tracesOf (manifest []) dumps [ outcome "Ns.C.one" Passed; outcome "Ns.C.two" Passed ]

    test
        <@
            traces = Map
                [ "Ns.C.one",
                  Some(
                      { Ids = set [ 1 ]
                        Types = set [ "Lib.Shapes+Colour"; "Lib.Shapes+Size" ] }
                      : Mutation.TestTrace
                  )
                  "Ns.C.two", Some(ids [ 2 ]) ]
        @>

[<Fact>]
let ``a union-type mutant selects the tests that reflected over the union and those with no trace`` () =
    let colour: Mutation.Target =
        { Name = "Lib.Shapes+Colour"
          Ids = []
          Types = [ "Lib.Shapes+Colour" ] }

    let traced =
        Map
            [ "enumerates",
              Some(
                  { Ids = set [ 1 ]
                    Types = set [ "Lib.Shapes+Colour" ] }
                  : Mutation.TestTrace
              )
              "constructs", Some(ids [ 1 ])
              "untraced", None ]

    test <@ Mutation.selected traced colour = set [ "enumerates"; "untraced" ] @>

    let s =
        Mutation.judge
            traced
            [ outcome "enumerates" Passed
              outcome "constructs" Passed
              outcome "untraced" Passed ]
            colour
            (Ok
                [ outcome "enumerates" Failed
                  outcome "constructs" Failed
                  outcome "untraced" Passed ])

    test <@ s.Kind = TraceStore.UnionTypeMutant @>
    test <@ s.Missed = [ "constructs" ] @>

// --- symbol-level judging (AUTOMATION-1497) ------------------------------------------------

[<Fact>]
let ``a target's symbols are the indexed ones its probes and union types name`` () =
    let ix: Joiner.SymbolIndex =
        { InFile = fun _ -> []
          Exists = fun n -> n = "Lib.Shapes.Colour"
          IsIndexedFile = fun _ -> true
          RepoRoot = "/repo" }

    let joined =
        [| Joiner.ToSymbol "Lib.A.f"
           Joiner.ToFile "src/A.fs"
           Joiner.Dropped
           Joiner.ToSymbol "Lib.A.f" |]

    let target: Mutation.Target =
        { Name = "t"
          Ids = [ 0; 1; 2; 3 ]
          Types = [ "Lib.Shapes+Colour"; "Lib.Unknown" ] }

    test <@ Mutation.targetSymbols ix joined target = [ "Lib.A.f"; "Lib.Shapes.Colour" ] @>

[<Fact>]
let ``a target with no indexed symbol is judged by probe`` () =
    let byProbe = set [ "probe" ]
    test <@ Mutation.bySymbolOr byProbe [] (fun _ -> set [ "symbol" ]) = byProbe @>
    test <@ Mutation.bySymbolOr byProbe [ "S" ] Set.ofList = set [ "S" ] @>
