/// Trace selection (shadow): which tests a recorded trace says must run now, and how that
/// compares with the static selection.
module TestPrune.Trace.Tests.TraceSelectTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.TraceStore

let private trace key complete reasons (symbols: (string * string) list) (inputs: (string * string * string) list) =
    { TestKey = key
      EnvFingerprint = "E"
      RunId = "r1"
      Complete = complete
      Reasons = reasons
      Symbols = Set.ofList symbols
      Inputs = Set.ofList inputs }

let private stored =
    Map
        [ "P|C|unchanged", trace "P|C|unchanged" true [] [ "N.f", "h1" ] [ "read", "a.txt", "ha" ]
          "P|C|symbol", trace "P|C|symbol" true [] [ "N.f", "h1"; "N.g", "old" ] []
          "P|C|gone", trace "P|C|gone" true [] [ "N.removed", "h9" ] []
          "P|C|input", trace "P|C|input" true [] [] [ "read", "b.txt", "old" ]
          "P|C|incomplete", trace "P|C|incomplete" false [ "no-outcome" ] [ "N.f", "h1" ] [] ]

let private versions = Map [ "N.f", "h1"; "N.g", "new" ]

let private inputs = Map [ ("read", "a.txt"), "ha"; ("read", "b.txt"), "new" ]

let private selectAll universe =
    TraceSelect.select "P" universe stored.TryFind versions.TryFind inputs.TryFind

[<Fact>]
let ``a test is selected when it has no trace, its trace changed, or its trace is incomplete`` () =
    let s =
        selectAll
            [ "P|C|unchanged"
              "P|C|symbol"
              "P|C|gone"
              "P|C|input"
              "P|C|incomplete"
              "P|C|new" ]

    test
        <@
            s.Selected = Map
                [ "P|C|symbol", [ TraceSelect.SymbolChanged "N.g" ]
                  "P|C|gone", [ TraceSelect.SymbolChanged "N.removed" ]
                  "P|C|input", [ TraceSelect.InputChanged("read", "b.txt") ]
                  "P|C|incomplete", [ TraceSelect.Incomplete [ "no-outcome" ] ]
                  "P|C|new", [ TraceSelect.NoTrace ] ]
        @>

    test <@ s.Universe.Count = 6 @>
    test <@ abs (s.CompleteShare - 4.0 / 6.0) < 1e-9 @>

[<Fact>]
let ``rule codes count each selected test once per rule`` () =
    let s = selectAll [ "P|C|symbol"; "P|C|new"; "P|C|unchanged" ]
    test <@ TraceSelect.ruleCounts s = Map [ "R1", 1; "R2", 1 ] @>

[<Fact>]
let ``the comparison splits static extras from trace additions, by test and by class`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|D|x"; "P|C|new" ]
    let c = TraceSelect.compare [ "P|C|unchanged"; "P|D|x" ] s

    test <@ c.StaticOnly = set [ "P|C|unchanged" ] @>
    test <@ c.TraceOnly = set [ "P|C|symbol"; "P|C|new" ] @>
    test <@ c.StaticClasses = set [ "P|C"; "P|D" ] && c.TraceClasses = set [ "P|C"; "P|D" ] @>

[<Fact>]
let ``a failed test the trace selection left out is a verified-failure miss`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol" ]

    test
        <@ TraceSelect.verifiedFailureMisses s [ "P|C|unchanged"; "P|C|symbol"; "P|X|unknown" ] = [ "P|C|unchanged" ] @>

[<Fact>]
let ``the log line names the project, both selections, the rules and the misses`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|C|new" ]
    let c = TraceSelect.compare [ "P|C|unchanged" ] s

    test
        <@
            TraceSelect.logLine s c 12L = "trace-shadow: P static 1/1c, trace 2/1c, universe 3, trace-only 2, static-only 1, R1 1 R2 1, complete 0.667, 12 ms"
        @>

[<Fact>]
let ``a shadow row round-trips through the store, and so do its misses`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-shadow-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path
        let s = selectAll [ "P|C|unchanged"; "P|C|symbol" ]
        let c = TraceSelect.compare [ "P|C|unchanged" ] s
        let at = DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)

        let row =
            TraceSelect.row
                { RunId = "run-1"
                  Mode = FullSuiteRun
                  RecordPolicy = "every-run"
                  Fingerprint = "E"
                  ActualFingerprint = Some "F"
                  PredictedHadTraces = true
                  RecordedAt = at
                  SelectMs = 5L
                  TraceRecall = "1/2" }
                s
                c

        test <@ row.FingerprintOutcome = UnsafeMiss && row.Eligible = Some true @>
        store.RecordShadow(row)
        store.RecordShadowMisses("run-1", "P", at, [ "P|C|unchanged" ])

        test <@ store.ShadowRows "P" = [ row ] @>
        test <@ store.ShadowMisses "P" = [ "run-1", "P|C|unchanged" ] @>
    finally
        File.Delete path

[<Fact>]
let ``an input's hash now is the hash ingestion stores for it`` () =
    let root = Directory.CreateTempSubdirectory("tp-input-now-").FullName

    try
        File.WriteAllText(Path.Combine(root, "a.txt"), "x")
        Directory.CreateDirectory(Path.Combine(root, "d")) |> ignore

        for kind in
            [ Model.FileRead
              Model.ExistenceProbe
              Model.DirectoryListing
              Model.DeepDirectoryListing
              Model.MetadataRead ] do
            for path in [ "a.txt"; "d"; "missing" ] do
                let kindCode, key, hash =
                    TraceIngest.inputOf
                        root
                        { Kind = kind
                          Path = Path.Combine(root, path) }
                    |> Option.get

                test <@ TraceIngest.currentInputHash root (kindCode, key) = Some hash @>

        test <@ TraceIngest.currentInputHash root ("weird", "a.txt") = None @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``every rule has its code`` () =
    let s = selectAll [ "P|C|new"; "P|C|symbol"; "P|C|input"; "P|C|incomplete" ]

    test <@ TraceSelect.ruleCounts s = Map [ "R1", 1; "R2", 1; "R3", 1; "R4", 1 ] @>

[<Fact>]
let ``an empty universe is wholly complete, and a run where no rule fired says so`` () =
    let s = selectAll []
    test <@ s.CompleteShare = 1.0 @>
    let c = TraceSelect.compare [ "unkeyed" ] s
    test <@ c.StaticClasses = set [ "unkeyed" ] @>
    test <@ (TraceSelect.logLine s c 0L).Contains ", no rule fired, " @>

[<Fact>]
let ``a failed theory row is judged under its test's key, and a key naming no test is listed, not judged`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol" ]

    let failed =
        [ "P|C|unchanged(x: 1)" // a theory row of a test the trace left out
          "P|C|symbol(x: 2)" // a theory row of a test the trace selected
          "P|C|gone(x: 3)" // a theory row of a test outside the universe
          "P|C|plain" // no argument list, outside the universe
          "Q|D|other" ] // another project's failure

    test <@ TraceSelect.verifiedFailureMisses s failed = [ "P|C|unchanged" ] @>
    test <@ TraceSelect.unknownFailures s failed = [ "P|C|gone(x: 3)"; "P|C|plain" ] @>
    test <@ TraceSelect.universeKeyOf s.Universe "P|C|unchanged" = Some "P|C|unchanged" @>

// --- the row's measurement columns (AUTOMATION-975, plan Task 6) ------------------------

[<Fact>]
let ``a project is eligible when at least 0.99 of its universe has a complete trace`` () =
    test <@ TraceSelect.EligibilityThreshold = 0.99 @>
    test <@ not (selectAll [ "P|C|unchanged"; "P|C|incomplete" ]).Eligible @>
    test <@ (selectAll [ "P|C|unchanged"; "P|C|symbol" ]).Eligible @>
    // R1 (no trace) counts against eligibility as much as an incomplete trace does.
    test <@ not (selectAll [ "P|C|unchanged"; "P|C|new" ]).Eligible @>

[<Fact>]
let ``the comparison counts the tests each side launches once its classes are expanded`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|D|x"; "P|D|y"; "P|E|z" ]
    let c = TraceSelect.compare [ "P|D|x"; "P|E|z" ] s
    // Static launches classes D and E: 3 tests. Trace selected C.symbol and the untraced
    // D and E tests, so it launches classes C, D and E: all 5, C.unchanged included.
    test <@ c.StaticClassTests = 3 && c.TraceClassTests = 5 @>
    test <@ c.TraceTests.Count = 4 @>

[<Fact>]
let ``trace recall is the failed tests the trace selection selected, out of the failed tests it knows`` () =
    let s = selectAll [ "P|C|unchanged"; "P|C|symbol"; "P|C|new" ]
    test <@ TraceSelect.traceRecall s [ "P|C|symbol"; "P|C|unchanged(x: 1)"; "P|X|unknown" ] = "1/2" @>
    test <@ TraceSelect.traceRecall s [] = "0/0" @>
    test <@ TraceSelect.traceRecall s [ "P|C|new"; "P|C|new" ] = "1/1" @>

[<Theory>]
[<InlineData("E", "E", true, "hit")>]
[<InlineData("E", "F", true, "unsafe-miss")>]
[<InlineData("E", "F", false, "safe-miss")>]
[<InlineData("E", null, true, "untraced")>]
let ``fingerprint outcomes`` (predicted: string, actual: string, hadTraces: bool, expected: string) =
    test
        <@
            TraceStore.fingerprintOutcomeCode (TraceSelect.fingerprintOutcome predicted (Option.ofObj actual) hadTraces) = expected
        @>

[<Fact>]
let ``a test held only by an untraced child process is counted, one held by anything else is not`` () =
    let child =
        trace "P|C|child" false [ "child-process-untraced:sh" ] [ "N.f", "h1" ] []

    let both =
        trace "P|C|both" false [ "child-process-untraced:sh" ] [ "N.g", "old" ] []

    let other = trace "P|C|other" false [ "no-outcome" ] [] []
    let traces = Map [ "P|C|child", child; "P|C|both", both; "P|C|other", other ]

    let s =
        TraceSelect.select "P" [ "P|C|child"; "P|C|both"; "P|C|other" ] traces.TryFind versions.TryFind inputs.TryFind

    test <@ TraceSelect.childUntracedTests s = 1 @>

[<Fact>]
let ``a row written before the measurement columns reads back as not recorded`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-shadow-old-" + Guid.NewGuid().ToString "N" + ".db")

    try
        do
            use old = Store.OpenWith(migrations |> List.take 3, path)
            ignore old

        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{path}")
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """INSERT INTO shadow_selections (run_id, test_project, env_fingerprint, recorded_at, universe,
                       complete_share, static_tests, trace_tests, static_classes, trace_classes, rule_counts,
                       static_only, trace_only, select_ms)
                   VALUES ('old', 'P', 'E', '2026-11-01T00:00:00.0000000+00:00', 3, 1.0, 1, 1, 1, 1, '', '[]', '[]', 9)"""

            cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool conn

        use store = Store.Open path
        let row = store.ShadowRows "P" |> List.exactlyOne

        test
            <@
                (row.Mode, row.Eligible, row.FingerprintOutcome, row.ActualFingerprint) = (ModeNotRecorded,
                                                                                           None,
                                                                                           OutcomeNotRecorded,
                                                                                           None)
            @>

        test <@ row.TraceRecall.StartsWith "n/a" && row.RecordPolicy = "" @>
    finally
        File.Delete path

[<Fact>]
let ``a mutation sample round-trips through the store`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-mutation-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path

        let sample =
            { TestProject = "P"
              Kind = UnionTypeMutant
              Symbol = "N.U"
              TreeHash = "t"
              RecordedAt = DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
              Outcome = Measured
              Failing = 3
              Selected = 9
              Missed = [ "P|C|a" ] }

        store.RecordMutationSample sample

        store.RecordMutationSample
            { sample with
                Kind = SymbolMutant
                Outcome = NoFailure
                Missed = [] }

        test
            <@
                store.MutationSamples "P" = [ sample
                                              { sample with
                                                  Kind = SymbolMutant
                                                  Outcome = NoFailure
                                                  Missed = [] } ]
            @>

        test <@ List.isEmpty (store.MutationSamples "Q") @>
    finally
        File.Delete path

// --- R6 static fallback and R7 untraced inputs (AUTOMATION-975, plan Task 5) ------------

let private facts probed staticInit unstable : SymbolFacts =
    { Probed = set probed
      StaticInit = set staticInit
      Unstable = set unstable }

let private universe6 =
    [ "P|C|unchanged"; "P|C|symbol"; "P|C|input"; "P|D|x"; "P|D|y" ]

/// Fallback inputs where `N.f` and `N.g` are probed, `N.s` ran only in an unlinked
/// initializer, `N.u` is order-unstable; `a.txt` is read and `fixtures` listed by a trace.
let private fallbacks (overrides: TraceSelect.Fallbacks -> TraceSelect.Fallbacks) : TraceSelect.Fallbacks =
    overrides
        { Seeds = []
          ChangedFiles = []
          Facts = facts [ "N.f"; "N.g"; "N.u" ] [ "N.s" ] [ "N.u" ]
          ClaimedInputs = [ "read", "a.txt"; "list", "fixtures"; "list-deep", "data" ]
          IsIndexed = fun f -> f.EndsWith ".fs"
          AttributesOf = fun _ -> []
          StaticWalk = fun _ -> []
          FileFallback = fun _ -> Some [] }

[<Theory>]
[<InlineData("N.u", "order-unstable")>]
[<InlineData("N.s", "static-init")>]
[<InlineData("N.lit", "literal")>]
[<InlineData("N.inl", "inline")>]
[<InlineData("N.unprobed", "not-probed")>]
[<InlineData("N.f", "")>]
let ``why a changed symbol cannot be verified by traces`` (symbol: string, expected: string) =
    let attrs =
        function
        | "N.lit" -> [ "LiteralAttribute" ]
        | "N.inl" -> [ TestPrune.AstAnalyzer.InlineAttributeName ]
        | _ -> []

    let reason =
        TraceSelect.fallReason (facts [ "N.f"; "N.u"; "N.lit"; "N.inl" ] [ "N.s" ] [ "N.u" ]) attrs symbol

    test <@ (reason |> Option.map TraceSelect.fallCode |> Option.defaultValue "") = expected @>

[<Fact>]
let ``a seed the probes cannot represent selects what the static walk from it reaches (R6)`` () =
    let walked = ResizeArray<string list>()
    let s = selectAll universe6

    let r =
        TraceSelect.withFallbacks
            (fallbacks (fun f ->
                { f with
                    Seeds = [ "N.s"; "N.f"; "N.unprobed" ]
                    StaticWalk =
                        fun seeds ->
                            walked.Add seeds

                            match seeds with
                            | [ "N.s" ] -> [ "P|D|x"; "Q|K|other" ]
                            | [ "N.unprobed" ] -> [ "P|C|unchanged" ]
                            | _ -> [] }))
            s

    // Each fallback reason walks its own seeds; the probed seed N.f is never walked.
    test <@ walked |> Seq.concat |> Set.ofSeq = set [ "N.s"; "N.unprobed" ] @>

    test
        <@
            r.Selected.["P|D|x"]
            |> List.contains (TraceSelect.StaticFallback TraceSelect.StaticInitOnly)
        @>

    test <@ r.Selected.["P|C|unchanged"] = [ TraceSelect.StaticFallback TraceSelect.NotProbed ] @>
    // Another project's tests are never selected.
    test <@ not (r.Selected.ContainsKey "Q|K|other") @>
    test <@ r.FallbackSeeds = Map [ "N.s", TraceSelect.StaticInitOnly; "N.unprobed", TraceSelect.NotProbed ] @>
    // What R1-R4 selected stays selected, with its rules first.
    test <@ r.Selected.["P|C|symbol"] = s.Selected.["P|C|symbol"] @>
    test <@ TraceSelect.ruleCounts r |> Map.find "R6" = 2 @>

[<Fact>]
let ``a changed file no trace claims selects what the static file rules select (R7)`` () =
    let asked = ResizeArray<string list>()

    let r =
        TraceSelect.withFallbacks
            (fallbacks (fun f ->
                { f with
                    ChangedFiles =
                        [ "config/app.json"
                          "a.txt"
                          "src/M.fs"
                          "fixtures/new.json"
                          "data/deep/x.json" ]
                    FileFallback =
                        fun files ->
                            asked.Add files
                            Some [ "P|D|y" ] }))
            (selectAll universe6)

    // Read, indexed, listed and deep-listed files are claimed: only config/app.json is asked.
    test <@ List.ofSeq asked = [ [ "config/app.json" ] ] @>
    test <@ r.Selected.["P|D|y"] = [ TraceSelect.NoTrace; TraceSelect.UntracedInput "config/app.json" ] @>
    test <@ TraceSelect.ruleCode (TraceSelect.UntracedInput "x") = "R7" @>

[<Fact>]
let ``a changed file the static rules answer with everything selects every test (R7)`` () =
    let r =
        TraceSelect.withFallbacks
            (fallbacks (fun f ->
                { f with
                    ChangedFiles = [ "src/P.fsproj" ]
                    FileFallback = fun _ -> None }))
            (selectAll universe6)

    test <@ r.Selected |> Map.keys |> Set.ofSeq = set universe6 @>

[<Fact>]
let ``with no seed and no changed file, fallbacks change nothing`` () =
    let s = selectAll universe6
    let r = TraceSelect.withFallbacks (fallbacks id) s
    test <@ r.Selected = s.Selected && r.FallbackSeeds.IsEmpty @>

[<Fact>]
let ``fallbacks never leave out a test the static walk reaches from a seed traces cannot verify`` () =
    // The false-green guard: for every seed with a fall reason, the static walk's tests of
    // this project are a subset of the selection, whatever the traces say.
    let walk (seeds: string list) =
        seeds
        |> List.collect (fun seed -> universe6 |> List.filter (fun k -> (hash (seed + k)) % 2 = 0))

    for seeds in [ [ "N.s" ]; [ "N.u"; "N.f" ]; [ "N.unprobed"; "N.s"; "N.g" ] ] do
        let r =
            TraceSelect.withFallbacks
                (fallbacks (fun f ->
                    { f with
                        Seeds = seeds
                        StaticWalk = walk }))
                (selectAll universe6)

        let unverifiable = seeds |> List.filter r.FallbackSeeds.ContainsKey
        let reached = walk unverifiable |> Set.ofList
        test <@ Set.isSubset reached (r.Selected |> Map.keys |> Set.ofSeq) @>

[<Fact>]
let ``symbol facts: probed is replaced by each ingestion, static-init and unstable accumulate`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-facts-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path
        store.RecordSymbolFacts("P", "E", ProbedFact, [ "a"; "b" ])
        store.RecordSymbolFacts("P", "E", ProbedFact, [ "b"; "c" ])
        store.RecordSymbolFacts("P", "E", StaticInitFact, [ "s1" ])
        store.RecordSymbolFacts("P", "E", StaticInitFact, [ "s2" ])
        store.RecordSymbolFacts("P", "E", UnstableFact, [ "u" ])
        store.RecordSymbolFacts("P", "F", ProbedFact, [ "z" ])
        test <@ store.SymbolFacts("P", "E") = facts [ "b"; "c" ] [ "s1"; "s2" ] [ "u" ] @>
        test <@ store.SymbolFacts("Q", "E") = facts [] [] [] @>
    finally
        File.Delete path

[<Fact>]
let ``the inputs a project's traces claim, under one fingerprint`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-claims-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path

        let run fp : TraceRun =
            { RunId = "r-" + fp
              TestProject = "P"
              TreeHash = "t"
              EnvFingerprint = fp
              RecordedAt = DateTimeOffset.UnixEpoch
              Kind = FullRun
              Status = Recorded
              Reason = ""
              StatsJson = "{}" }

        store.RecordRun(
            run "E",
            [ { Key = "T:a"
                Symbols = []
                Inputs = [ "read", "a.txt", "h"; "list", "fixtures", "h" ] } ],
            [ { TestKey = "P|C|a"
                Status = Some Model.Passed
                Reasons = []
                ScopeKeys = [ "T:a" ] } ]
        )

        store.RecordRun(
            run "F",
            [ { Key = "T:a"
                Symbols = []
                Inputs = [ "read", "other.txt", "h" ] } ],
            [ { TestKey = "P|C|a"
                Status = Some Model.Passed
                Reasons = []
                ScopeKeys = [ "T:a" ] } ]
        )

        test <@ store.ClaimedInputs("P", "E") = [ "list", "fixtures"; "read", "a.txt" ] @>
    finally
        File.Delete path

[<Fact>]
let ``an incomplete trace with no reason is not held by an untraced child`` () =
    let none = trace "P|C|none" false [] [ "N.f", "h1" ] []
    let traces = Map [ "P|C|none", none ]

    let s =
        TraceSelect.select "P" [ "P|C|none" ] traces.TryFind versions.TryFind inputs.TryFind

    test <@ s.Selected.ContainsKey "P|C|none" && TraceSelect.childUntracedTests s = 0 @>

[<Fact>]
let ``every row mode and fingerprint outcome, and every sample outcome, round-trips through the store`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "tp-shadow-all-" + Guid.NewGuid().ToString "N" + ".db")

    try
        use store = Store.Open path
        let s = selectAll [ "P|C|unchanged" ]
        let c = TraceSelect.compare [] s
        let at = DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)

        let baseRow =
            TraceSelect.row
                { RunId = "r"
                  Mode = ImpactRun
                  RecordPolicy = "every-run"
                  Fingerprint = "E"
                  ActualFingerprint = None
                  PredictedHadTraces = false
                  RecordedAt = at
                  SelectMs = 1L
                  TraceRecall = "n/a: impact run" }
                s
                c

        let rows =
            [ for i, (mode, outcome) in
                  List.indexed
                      [ ImpactRun, PredictionHit
                        FullSuiteRun, SafeMiss
                        ModeNotRecorded, UnsafeMiss
                        ImpactRun, NotTraced
                        ImpactRun, OutcomeNotRecorded ] ->
                  { baseRow with
                      RunId = $"r%d{i}"
                      Mode = mode
                      FingerprintOutcome = outcome
                      Eligible = (if i = 0 then None else Some(i % 2 = 0)) } ]

        for r in rows do
            store.RecordShadow r

        test <@ store.ShadowRows "P" = rows @>

        test
            <@
                [ PredictionHit; SafeMiss; UnsafeMiss; NotTraced; OutcomeNotRecorded ]
                |> List.map fingerprintOutcomeCode = [ "hit"; "safe-miss"; "unsafe-miss"; "untraced"; "not-recorded" ]
            @>

        let sample outcome =
            { TestProject = "P"
              Kind = SymbolMutant
              Symbol = "N.f"
              TreeHash = "t"
              RecordedAt = at
              Outcome = outcome
              Failing = 0
              Selected = 0
              Missed = [] }

        let samples = [ Measured; NoFailure; NotMutable; RunFailed ] |> List.map sample

        for x in samples do
            store.RecordMutationSample x

        test <@ store.MutationSamples "P" = samples @>
    finally
        File.Delete path

[<Fact>]
let ``a top-level changed file is unclaimed, a key the walk reaches twice is selected once, and the row names the fallback seeds``
    ()
    =
    let asked = ResizeArray<string list>()

    let r =
        TraceSelect.withFallbacks
            (fallbacks (fun f ->
                { f with
                    Seeds = [ "N.s" ]
                    StaticWalk = fun _ -> [ "P|D|x"; "P|D|x" ]
                    ChangedFiles = [ "settings.json" ]
                    FileFallback =
                        fun files ->
                            asked.Add files
                            Some [] }))
            (selectAll universe6)

    test <@ List.ofSeq asked = [ [ "settings.json" ] ] @>

    test <@ r.Selected.["P|D|x"] = [ TraceSelect.NoTrace; TraceSelect.StaticFallback TraceSelect.StaticInitOnly ] @>

    let row =
        TraceSelect.row
            { RunId = "r"
              Mode = ImpactRun
              RecordPolicy = "every-run"
              Fingerprint = "E"
              ActualFingerprint = Some "E"
              PredictedHadTraces = true
              RecordedAt = DateTimeOffset.UnixEpoch
              SelectMs = 1L
              TraceRecall = "n/a: impact run" }
            r
            (TraceSelect.compare [] r)

    test <@ row.FallbackSeeds = Map [ "N.s", "static-init" ] @>
