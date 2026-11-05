/// Trace selection in shadow: computed at a run's launch next to the static selection,
/// logged, and stored with the run's verified-failure misses at completion. It never
/// changes what runs.
module FsHotWatch.Tests.TraceShadowTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginTypes

let private test' cls meth : TestMethodInfo =
    { SymbolFullName = $"%s{cls}.%s{meth}"
      TestProject = "P"
      TestClass = cls
      TestMethod = meth }

let private settings select : TraceSettings =
    { Record = RecordEveryRun
      DbPath = "traces.db"
      WeaveTests = WeaveTestSites
      FingerprintInputs = []
      FingerprintEnv = []
      VerifyTimeoutSec = 60
      Select = select
      ShadowDbPath = Some "shadow.db" }

/// A repository root holding a trace store with one recorded run of project P: test
/// `C.a` executed `N.f`, test `C.b` executed `N.g`.
let private withStore (f: string -> unit) =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-").FullName

    try
        do
            use store = Store.Open(Path.Combine(root, "traces.db"))

            let scope key syms : ScopeContent =
                { Key = key
                  Symbols = syms
                  Inputs = [] }

            let passed key scopes : TestTrace =
                { TestKey = key
                  Status = Some Passed
                  Reasons = []
                  ScopeKeys = scopes }

            store.RecordRun(
                { RunId = "r1"
                  TestProject = "P"
                  TreeHash = "tree"
                  EnvFingerprint = "E"
                  RecordedAt = DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)
                  Kind = FullRun
                  Status = Recorded
                  Reason = ""
                  StatsJson = "{}" },
                [ scope "T:a" [ "N.f", "h1" ]; scope "T:b" [ "N.g", "h2" ] ],
                [ passed "P|C|a" [ "T:a" ]; passed "P|C|b" [ "T:b" ] ]
            )

        f root
    finally
        Directory.Delete(root, true)

let private universe = [ test' "C" "a"; test' "C" "b"; test' "C" "c" ]

let private versions = Map [ "N.f", "h1"; "N.g", "changed" ]

let private launch root select (lines: ResizeArray<string>) =
    TraceShadow.atLaunch
        (settings select)
        root
        [ "P"; "Q" ]
        (fun p -> if p = "P" then universe else [])
        (lazy versions)
        [ test' "C" "a" ]
        ShadowFallbacks.none
        lines.Add

[<Fact>]
let ``at launch, shadow compares the trace selection with the static one and logs it per traced project`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        let r = readings |> List.exactlyOne
        test <@ r.Fingerprint = "E" @>

        test
            <@
                r.Selection.Selected = Map
                    [ "P|C|b", [ TraceSelect.SymbolChanged "N.g" ]
                      "P|C|c", [ TraceSelect.NoTrace ] ]
            @>

        test <@ r.Comparison.StaticOnly = set [ "P|C|a" ] @>
        // Q has no recorded run: nothing to select from, so no reading and no line.
        test
            <@
                lines.Count = 1
                && lines.[0].StartsWith "trace-shadow: P static 1/1c, trace 2/1c"
            @>)

[<Fact>]
let ``with select off nothing is read, logged or stored`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        test <@ List.isEmpty (launch root SelectOff lines) && lines.Count = 0 @>)

[<Fact>]
let ``an unreadable store is one log line and no reading, never an exception`` () =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-bad-").FullName

    try
        File.WriteAllText(Path.Combine(root, "traces.db"), "not a database")
        let lines = ResizeArray<string>()
        test <@ List.isEmpty (launch root SelectShadow lines) @>
        test <@ lines.Count = 1 && lines.[0].StartsWith "trace-shadow: skipped" @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``at completion the rows are stored under the run, and a failed test the trace left out is a miss`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        TraceShadow.atCompletion
            (settings SelectShadow)
            root
            readings
            "run-7"
            (fun _ -> false)
            (Some [ "P|C|a"; "P|C|b" ])
            lines.Add

        use store = Store.Open(Path.Combine(root, "shadow.db"))
        let rows = store.ShadowRows "P"
        test <@ rows |> List.map _.RunId = [ "run-7" ] @>
        test <@ rows.[0].TraceOnly = [ "P|C|b"; "P|C|c" ] @>
        test <@ store.ShadowMisses "P" = [ "run-7", "P|C|a" ] @>

        test
            <@
                lines
                |> Seq.exists (fun l -> l.Contains "verified-failure miss" && l.Contains "P|C|a")
            @>)

[<Fact>]
let ``a failure's key is its project, class and method`` () =
    test <@ TraceShadow.failureKey "P" "Ns.C+Inner" "a test" = "P|Ns.C+Inner|a test" @>

[<Fact>]
let ``with no reading, completion opens nothing and stores nothing`` () =
    let root = Directory.CreateTempSubdirectory("fshw-trace-shadow-none-").FullName

    try
        let lines = ResizeArray<string>()
        TraceShadow.atCompletion (settings SelectShadow) root [] "run-1" (fun _ -> false) (Some [ "P|C|a" ]) lines.Add
        test <@ lines.Count = 0 && not (File.Exists(Path.Combine(root, "shadow.db"))) @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a run with no failed test stores its rows and no miss`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines
        TraceShadow.atCompletion (settings SelectShadow) root readings "run-2" (fun _ -> false) (Some []) lines.Add

        use store = Store.Open(Path.Combine(root, "shadow.db"))
        test <@ store.ShadowRows "P" |> List.map _.RunId = [ "run-2" ] @>
        test <@ List.isEmpty (store.ShadowMisses "P") && lines.Count = 1 @>)

[<Fact>]
let ``a store that cannot be written at completion is one log line, never an exception`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines
        File.WriteAllText(Path.Combine(root, "shadow.db"), "not a database")

        TraceShadow.atCompletion
            (settings SelectShadow)
            root
            readings
            "run-3"
            (fun _ -> false)
            (Some [ "P|C|a" ])
            lines.Add

        test <@ lines |> Seq.last |> (fun l -> l.StartsWith "trace-shadow: not stored") @>)

[<Fact>]
let ``a failed theory row of a nested class with a backticked name is keyed as the index keys its test`` () =
    // AUTOMATION-975 QA: a failure key that does not match its universe key is dropped by
    // `verifiedFailureMisses` without a word, so a real miss would never be recorded. The
    // runner reports a nested class with '+', a backticked method by its compiled name and
    // a theory row with its arguments in the name only; the index stores the class with
    // '+' and the method with backticks.
    let json =
        """{"results": {"summary": {"tests": 3, "passed": 1, "failed": 2, "skipped": 0, "pending": 0, "other": 0},
           "tests": [
             {"name": "Ns.Outer+Inner.a theory, with a comma(x: 1, y: \"a\")", "status": "failed", "message": "boom",
              "extra": {"type": "Ns.Outer+Inner", "method": "a theory, with a comma"}},
             {"name": "Ns.Outer+Inner.a theory, with a comma(x: 2, y: \"b\")", "status": "passed",
              "extra": {"type": "Ns.Outer+Inner", "method": "a theory, with a comma"}},
             {"name": "Ns.Plain.plain", "status": "failed", "message": "boom",
              "extra": {"type": "Ns.Plain", "method": "plain"}}]}}"""

    let failedKeys =
        TestReports.failedTestsOfReport json
        |> Option.defaultValue []
        |> List.map (fun (cls, meth) -> TraceShadow.failureKey "P" cls meth)

    let indexed =
        [ { SymbolFullName = "Ns.Outer.Inner.a theory"
            TestProject = "P"
            TestClass = "Ns.Outer+Inner"
            TestMethod = "``a theory, with a comma``" }
          { SymbolFullName = "Ns.Plain.plain"
            TestProject = "P"
            TestClass = "Ns.Plain"
            TestMethod = "plain" } ]

    // A selection that left both out records both as misses, under their index keys.
    let selection =
        TraceSelect.select "P" (indexed |> List.map TraceShadow.keyOf) (fun _ -> None) (fun _ -> None) (fun _ -> None)

    let nothingSelected = { selection with Selected = Map.empty }

    test
        <@
            TraceSelect.verifiedFailureMisses nothingSelected failedKeys = (indexed
                                                                            |> List.map TraceShadow.keyOf
                                                                            |> List.sort)
        @>

[<Fact>]
let ``a failed test the traced universe does not know is said out loud, not dropped`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        TraceShadow.atCompletion
            (settings SelectShadow)
            root
            readings
            "run-4"
            (fun _ -> false)
            (Some [ "P|Gone|x" ])
            lines.Add

        test
            <@
                lines
                |> Seq.exists (fun l -> l.Contains "1 failed test(s) in P that no traced key names: P|Gone|x")
            @>)

// --- the shadow window spans checkouts (AUTOMATION-975) --------------------------------
// Each checkout has its own trace database, and checkouts are short-lived, so no single
// trace database ever holds a window's worth of rows. The rows go to one shadow store
// instead: box-wide per repository by default, like the outcome and evidence stores.

[<Fact>]
let ``at completion the rows go to the shadow store, never the checkout's trace database`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines

        TraceShadow.atCompletion
            (settings SelectShadow)
            root
            readings
            "run-8"
            (fun _ -> false)
            (Some [ "P|C|a" ])
            lines.Add

        do
            use traces = Store.Open(Path.Combine(root, "traces.db"))
            test <@ List.isEmpty (traces.ShadowRows "P") && List.isEmpty (traces.ShadowMisses "P") @>

        use shadow = Store.Open(Path.Combine(root, "shadow.db"))
        test <@ shadow.ShadowRows "P" |> List.map _.RunId = [ "run-8" ] @>
        test <@ shadow.ShadowMisses "P" = [ "run-8", "P|C|a" ] @>)

[<Fact>]
let ``two checkouts' runs count in one shadow store`` () =
    let shared = Directory.CreateTempSubdirectory("fshw-trace-shadow-shared-").FullName

    try
        let sharedDb = Path.Combine(shared, "box", "shadow.db")

        let settings' =
            { settings SelectShadow with
                ShadowDbPath = Some sharedDb }

        for runId in [ "run-a"; "run-b" ] do
            withStore (fun root ->
                let lines = ResizeArray<string>()
                let readings = launch root SelectShadow lines
                TraceShadow.atCompletion settings' root readings runId (fun _ -> false) (Some []) lines.Add)

        use store = Store.Open sharedDb
        test <@ store.ShadowRows "P" |> List.map _.RunId = [ "run-a"; "run-b" ] @>
    finally
        Directory.Delete(shared, true)

[<Fact>]
let ``with no shadowDb the shadow store is the repository's box-wide one, outside the checkout`` () =
    withStore (fun root ->
        let path =
            TraceShadow.shadowStorePath
                { settings SelectShadow with
                    ShadowDbPath = None }
                root

        test
            <@
                path = Path.Combine(
                    FsHotWatch.FsHwPaths.sharedCacheHome (),
                    "cache",
                    "trace-shadow",
                    FsHotWatch.RepoIdentity.namespaceOf root,
                    "shadow.db"
                )
            @>

        test <@ not (path.StartsWith(root, StringComparison.Ordinal)) @>)

[<Fact>]
let ``a relative shadowDb is resolved against the checkout`` () =
    test <@ TraceShadow.shadowStorePath (settings SelectShadow) "/repo" = Path.Combine("/repo", "shadow.db") @>

// --- the row's measurement columns (AUTOMATION-975, plan Task 6) ------------------------

/// Record a trace run of P under `runId` and fingerprint `fp` in the checkout's trace store.
let private recordRun (root: string) (runId: string) (fp: string) (status: RunStatus) =
    use store = Store.Open(Path.Combine(root, "traces.db"))

    store.RecordRunWithoutTraces
        { RunId = runId
          TestProject = "P"
          TreeHash = "tree"
          EnvFingerprint = fp
          RecordedAt = DateTimeOffset.UtcNow
          Kind = PartialRun
          Status = status
          Reason = ""
          StatsJson = "{}" }

/// Launch, run `during` (what the run itself records), then complete; the stored row.
let private completedRowWith root runId ranInFull failed (during: unit -> unit) =
    let lines = ResizeArray<string>()
    let readings = launch root SelectShadow lines
    during ()
    TraceShadow.atCompletion (settings SelectShadow) root readings runId ranInFull failed lines.Add
    use store = Store.Open(Path.Combine(root, "shadow.db"))
    store.ShadowRows "P" |> List.exactlyOne

let private completedRow root runId ranInFull failed =
    completedRowWith root runId ranInFull failed ignore

[<Fact>]
let ``a project that ran in full stores a full-run row with the trace selection's recall`` () =
    withStore (fun root ->
        // The trace selection holds C.b and C.c; of the two failed tests it selected C.b.
        let row = completedRow root "run-f" (fun p -> p = "P") (Some [ "P|C|a"; "P|C|b" ])
        test <@ row.Mode = FullSuiteRun && row.TraceRecall = "1/2" @>
        test <@ row.RecordPolicy = "every-run" @>
        // Two of three tests have a complete trace: under 0.99, so not eligible.
        test <@ row.Eligible = Some false @>
        test <@ (row.StaticClassTests, row.TraceClassTests) = (3, 3) @>)

[<Fact>]
let ``an impact row does not measure recall, and a full run without a complete report cannot`` () =
    withStore (fun root ->
        let impact = completedRow root "run-i" (fun _ -> false) (Some [ "P|C|a" ])
        test <@ impact.Mode = ImpactRun && impact.TraceRecall = "n/a: impact run" @>)

    withStore (fun root ->
        let full = completedRow root "run-n" (fun _ -> true) None
        test <@ full.Mode = FullSuiteRun && full.TraceRecall = "n/a: no complete report" @>)

[<Fact>]
let ``the fingerprint outcome compares the traces shadow read with what the run recorded`` () =
    withStore (fun root ->
        let row =
            completedRowWith root "run-h" (fun _ -> false) (Some []) (fun () -> recordRun root "run-h" "E" Recorded)

        test <@ row.FingerprintOutcome = PredictionHit @>)

    withStore (fun root ->
        let row =
            completedRowWith root "run-u" (fun _ -> false) (Some []) (fun () -> recordRun root "run-u" "F" Recorded)

        // Shadow read E's traces while the run recorded under F: it trusted another environment.
        test <@ row.FingerprintOutcome = UnsafeMiss && row.ActualFingerprint = Some "F" @>)

    withStore (fun root ->
        let row =
            completedRowWith root "run-r" (fun _ -> false) (Some []) (fun () -> recordRun root "run-r" "F" Refused)

        test <@ row.FingerprintOutcome = NotTraced @>)

    withStore (fun root ->
        test <@ (completedRow root "run-none" (fun _ -> false) (Some [])).FingerprintOutcome = NotTraced @>)

[<Fact>]
let ``a completion reads ran-in-full projects and failed keys from the run, and nothing from an incomplete report`` () =
    let failure cls meth : OutstandingFailure =
        { Project = "P"
          Class = cls
          Method = meth
          File = "t.fs"
          Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "boom" }

    let selection =
        Map
            [ "Full", ProjectInFull
              "Reused", ProjectInFull
              "Filtered", ProjectClasses(set [ "C" ])
              "NotRun", ProjectInFull ]

    let ranInFull, failedKeys =
        CompletionStages.shadowFacts
            selection
            (set [ "Full"; "Reused"; "Filtered" ])
            (set [ "Reused" ])
            (Ok [ failure (Some "C") (Some "a"); failure None None ])

    test <@ [ "Full"; "Reused"; "Filtered"; "NotRun"; "Absent" ] |> List.filter ranInFull = [ "Full" ] @>
    // A project-level red names no test, so it has no key.
    test <@ failedKeys = Some [ "P|C|a" ] @>

    let _, none =
        CompletionStages.shadowFacts selection Set.empty Set.empty (Error "a project timed out")

    test <@ none = None @>

[<Fact>]
let ``a row records the run's recording policy, and a checkout with no trace database is untraced`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let readings = launch root SelectShadow lines
        File.Delete(Path.Combine(root, "traces.db"))

        let fullRuns =
            { settings SelectShadow with
                Record = RecordFullRuns }

        TraceShadow.atCompletion fullRuns root readings "run-p" (fun _ -> false) (Some []) lines.Add
        use store = Store.Open(Path.Combine(root, "shadow.db"))
        let row = store.ShadowRows "P" |> List.exactlyOne
        test <@ row.RecordPolicy = "full-runs" && row.FingerprintOutcome = NotTraced @>)

// --- R6 and R7 at launch (AUTOMATION-975) -------------------------------------------------

[<Fact>]
let ``at launch a seed no probe represents selects what the static walk reaches, and the row names it`` () =
    withStore (fun root ->
        let lines = ResizeArray<string>()
        let walked = ResizeArray<string list>()

        let fallbacks =
            { ShadowFallbacks.none with
                Seeds = [ "N.unprobed" ]
                StaticWalk =
                    fun seeds ->
                        walked.Add seeds
                        [ test' "C" "a" ] }

        let reading =
            TraceShadow.atLaunch
                (settings SelectShadow)
                root
                [ "P" ]
                (fun _ -> universe)
                (lazy versions)
                [ test' "C" "a" ]
                fallbacks
                lines.Add
            |> List.exactlyOne

        test <@ List.ofSeq walked = [ [ "N.unprobed" ] ] @>

        test <@ reading.Selection.Selected.["P|C|a"] = [ TraceSelect.StaticFallback TraceSelect.NotProbed ] @>

        // The static selection's test is no longer a static-only extra.
        test <@ reading.Comparison.StaticOnly.IsEmpty @>
        test <@ lines.[0].Contains "R6 1" @>
        TraceShadow.atCompletion (settings SelectShadow) root [ reading ] "run-6" (fun _ -> false) (Some []) lines.Add
        use store = Store.Open(Path.Combine(root, "shadow.db"))
        test <@ (store.ShadowRows "P" |> List.exactlyOne).FallbackSeeds = Map [ "N.unprobed", "not-probed" ] @>)

[<Fact>]
let ``fallback inputs that add nothing walk to nothing, select no file and index no file`` () =
    let none = ShadowFallbacks.none
    test <@ none.Seeds.IsEmpty && none.ChangedFiles.IsEmpty @>
    test <@ none.StaticWalk [ "N.f" ] |> List.isEmpty @>
    test <@ none.FileFallback [ "a.json" ] = Some [] @>
    test <@ not (none.IsIndexed "src/A.fs") && none.AttributesOf "N.f" |> List.isEmpty @>
