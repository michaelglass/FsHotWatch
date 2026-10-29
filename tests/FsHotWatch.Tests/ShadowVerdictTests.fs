/// ADR-059 §7: the shadow files what a concluded publication established, looks the tree
/// up from the configured requirements, and logs how that compares with today's verdict.
/// Every time below is a value the test chose; nothing reads the wall clock.
module FsHotWatch.Tests.ShadowVerdictTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private counts total failed : OutcomeStore.Counts =
    { Total = total
      Passed = total - failed
      Failed = failed
      Skipped = 0 }

let private suite run project failed : Suite =
    { RunId = Some run
      Project = project
      Counts = counts 3 failed
      Passed = failed = 0 }

/// A green confirm over tree T1 that ran both configured projects in full.
let private confirmGreen: Observation =
    { Command = "confirm"
      Tree = "sha256:t1"
      Producer = "fshw 1"
      Worktree = "/w"
      Today = Class.Green
      TodayOutcome = "green"
      ExitCode = 0
      Concluded = true
      Plugins =
        [ "build", Some OutcomeStore.Outcome.Pass
          "lint", Some OutcomeStore.Outcome.Pass ]
      DiagnosticsFailed = false
      GradedRunId = Some "r1"
      FullSuite = true
      ImpactFiltered = false
      Suites = [ suite "r1" "A.Tests" 0; suite "r1" "B.Tests" 0 ]
      RecordedAt = t0 }

let private projects = [ "A.Tests"; "B.Tests" ]

let private withStore (body: string -> OutcomeStore.Store -> unit) =
    withTempDir "shadow" (fun repo ->
        let store =
            OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        body repo store)

let private at (path: string list) (n: Nodes.JsonNode) =
    path |> List.fold (fun (x: Nodes.JsonNode) (k: string) -> x[k]) n

let private str path n = (at path n).GetValue<string>()
let private flag path n = (at path n).GetValue<bool>()
let private num path n = (at path n).GetValue<int>()

let private logLines (repo: string) =
    File.ReadAllLines(logPath repo)
    |> Array.map (fun line -> Nodes.JsonNode.Parse(line, Nullable(), Text.Json.JsonDocumentOptions()))
    |> List.ofArray

[<Fact>]
let ``a green confirm is looked up green from what it recorded, and the log says they agree`` () =
    withStore (fun repo store ->
        let answer = observeWith repo store projects confirmGreen
        test <@ answer = LookupVerdict.Answer.Green @>
        let line = logLines repo |> List.exactlyOne
        test <@ (str [ "schema" ] line) = LogSchema @>
        test <@ (flag [ "agree" ] line) @>
        test <@ (str [ "direction" ] line) = "agree" @>
        test <@ (str [ "today"; "outcome" ] line) = "green" @>
        test <@ (str [ "lookup"; "outcome" ] line) = "green" @>
        // two plugins, diagnostics, two full suites and their two baselines
        test <@ (num [ "recorded" ] line) = 7 @>)

[<Fact>]
let ``a later publication that ran nothing answers from the records already on file`` () =
    // The restart case: a new daemon, nothing in memory, the same bytes. Today's verdict
    // could not conclude; the lookup still knows the tree was verified.
    withStore (fun repo store ->
        observeWith repo store projects confirmGreen |> ignore

        let ranNothing =
            { confirmGreen with
                Today = Class.Missing
                TodayOutcome = "incomplete"
                ExitCode = 3
                Concluded = false
                Suites = []
                GradedRunId = None
                RecordedAt = t0.AddMinutes 5.0 }

        test <@ observeWith repo store projects ranNothing = LookupVerdict.Answer.Green @>
        let line = logLines repo |> List.last
        test <@ (str [ "direction" ] line) = "looser" @>
        test <@ (num [ "recorded" ] line) = 0 @>)

[<Fact>]
let ``a store opened afresh over the same directory gives the same answer`` () =
    withStore (fun repo store ->
        observeWith repo store projects confirmGreen |> ignore

        let reopened =
            OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        let ranNothing =
            { confirmGreen with
                Concluded = false
                Suites = [] }

        test <@ observeWith repo reopened projects ranNothing = LookupVerdict.Answer.Green @>)

[<Fact>]
let ``another tree finds none of those records`` () =
    withStore (fun repo store ->
        observeWith repo store projects confirmGreen |> ignore

        let otherTree =
            { confirmGreen with
                Tree = "sha256:t2"
                Concluded = false
                Suites = [] }

        test
            <@
                observeWith repo store projects otherTree = LookupVerdict.Answer.Missing
                    [ "plugin:build"; "plugin:lint"; "diagnostics"; "test:A.Tests"; "test:B.Tests" ]
            @>)

[<Fact>]
let ``a confirm that did not run a configured project is missing it, whatever today said`` () =
    withStore (fun repo store ->
        let onlyA =
            { confirmGreen with
                Suites = [ suite "r1" "A.Tests" 0 ] }

        test <@ observeWith repo store projects onlyA = LookupVerdict.Answer.Missing [ "test:B.Tests" ] @>
        test <@ (str [ "direction" ] (logLines repo |> List.last)) = "stricter" @>)

[<Fact>]
let ``a failed suite is red and records no baseline`` () =
    withStore (fun repo store ->
        let red =
            { confirmGreen with
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1
                Suites = [ suite "r1" "A.Tests" 1; suite "r1" "B.Tests" 0 ] }

        test <@ observeWith repo store projects red = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>
        test <@ store.Lookup (baselineKey red.Producer "A.Tests").Value = None @>
        test <@ store.Lookup (baselineKey red.Producer "B.Tests").Value <> None @>)

[<Fact>]
let ``a failing plugin and a red diagnostic are both red`` () =
    withStore (fun repo store ->
        let red =
            { confirmGreen with
                Today = Class.Red
                Plugins =
                    [ "build", Some OutcomeStore.Outcome.Pass
                      "lint", Some OutcomeStore.Outcome.Fail ]
                DiagnosticsFailed = true }

        test <@ observeWith repo store projects red = LookupVerdict.Answer.Red [ "plugin:lint"; "diagnostics" ] @>)

[<Fact>]
let ``a plugin still running records nothing and is missing`` () =
    withStore (fun repo store ->
        let running =
            { confirmGreen with
                Plugins = [ "build", Some OutcomeStore.Outcome.Pass; "lint", None ] }

        test <@ observeWith repo store projects running = LookupVerdict.Answer.Missing [ "plugin:lint" ] @>)

[<Fact>]
let ``an impact-filtered check is green on its selection plus each project's full baseline`` () =
    withStore (fun repo store ->
        // A full green on an earlier tree earns each project's baseline.
        observeWith repo store projects { confirmGreen with Tree = "sha256:t0" }
        |> ignore

        let check =
            { confirmGreen with
                Command = "check"
                Tree = "sha256:t1"
                GradedRunId = Some "r2"
                FullSuite = false
                ImpactFiltered = true
                Suites = [ suite "r2" "A.Tests" 0 ]
                RecordedAt = t0.AddMinutes 1.0 }

        // B.Tests had nothing selected: an impact pass of zero tests, under its baseline.
        test <@ observeWith repo store projects check = LookupVerdict.Answer.Green @>

        test
            <@
                (store.Lookup (testKey check "B.Tests" ImpactScope).Value)
                |> Option.bind _.Counts
                |> Option.map _.Total = Some 0
            @>)

[<Fact>]
let ``an impact-filtered check with no full baseline is missing, as today's exit 3 is`` () =
    withStore (fun repo store ->
        let check =
            { confirmGreen with
                Command = "check"
                Today = Class.Missing
                TodayOutcome = "incomplete"
                ExitCode = 3
                Concluded = false
                FullSuite = false
                ImpactFiltered = true
                Suites = [ suite "r1" "A.Tests" 0 ] }

        test <@ classOf (observeWith repo store projects check) = Class.Missing @>
        test <@ (flag [ "agree" ] (logLines repo |> List.last)) @>)

[<Fact>]
let ``an impact batch never stands in for the full suite under confirm`` () =
    withStore (fun repo store ->
        // A confirm whose graded run was full, plus an earlier impact batch of B only.
        let o =
            { confirmGreen with
                Suites = [ suite "r0" "B.Tests" 0; suite "r1" "A.Tests" 0 ] }

        test <@ observeWith repo store projects o = LookupVerdict.Answer.Missing [ "test:B.Tests" ] @>)

[<Fact>]
let ``an unconcluded publication files nothing`` () =
    test <@ List.isEmpty (recordsOf { confirmGreen with Concluded = false } projects) @>

[<Fact>]
let ``direction names which side is greener`` () =
    test <@ direction Class.Green Class.Green = "agree" @>
    test <@ direction Class.Missing Class.Green = "looser" @>
    test <@ direction Class.Red Class.Green = "looser" @>
    test <@ direction Class.Green Class.Missing = "stricter" @>
    test <@ direction Class.Green Class.Red = "stricter" @>
    test <@ direction Class.Red Class.Missing = "red-vs-missing" @>
    test <@ direction Class.Missing Class.Red = "red-vs-missing" @>

[<Fact>]
let ``the kill switch is 0 and only 0`` () =
    test <@ enabledBy null @>
    test <@ enabledBy "" @>
    test <@ enabledBy "1" @>
    test <@ not (enabledBy "0") @>
    test <@ not (enabledBy " 0 ") @>

[<Fact>]
let ``observe reads the configured test projects and never throws`` () =
    withTempDir "shadow-observe" (fun repo ->
        Directory.CreateDirectory(Path.Combine(repo, ".fshw")) |> ignore

        File.WriteAllText(
            Path.Combine(repo, ".fshw.json"),
            """{ "tests": { "projects": [ { "project": "A.Tests" }, { "project": "B.Tests" } ] } }"""
        )

        test <@ configuredTestProjects repo = Ok [ "A.Tests"; "B.Tests" ] @>
        observe repo confirmGreen
        let line = logLines repo |> List.exactlyOne
        test <@ (str [ "lookup"; "outcome" ] line) = "green" @>

        // An unreadable config is logged, never thrown.
        File.WriteAllText(Path.Combine(repo, ".fshw.json"), "{ not json")
        observe repo confirmGreen
        test <@ logLines repo |> List.length = 1 @>)

// ---------------------------------------------------------------------------
// `fshw verdict` reads (ADR-059 §7): the CURRENT tree looked up beside what the read reports.
// ---------------------------------------------------------------------------

let private publishedVerdict (command: Verdict.Command) (outcome: Verdict.Outcome) (exitCode: int) =
    Verdict.create
        command
        (BaselineFixtures.reportOf (IpcParsing.FullSuite(2, None)))
        ({ Hash = "sha256:published"
           FileCount = 1
           SkippedCount = 0
           DeclaredCount = 0
           AbsentDeclarationCount = 0 }
        : TreeHash.Tree)
        (Some [])
        outcome
        exitCode
        [ { Name = "lint"
            Outcome = Verdict.PluginOutcome.Ok
            ElapsedMs = Some 1L
            Summary = None } ]
        []
        Verdict.CheckComparison.notRecorded
        []
        ProjectModelFixtures.available

let private greenConfirm () =
    publishedVerdict Verdict.Confirm (Verdict.Green BaselineFixtures.baseline) 0

[<Fact>]
let ``a read with no verdict on disk has nothing to compare`` () =
    test <@ ofRead "/w" (Verdict.Report.NoVerdict "none") "sha256:t1" "fshw 1" t0 = None @>

[<Fact>]
let ``a read that applies keeps the verdict's class, judged against the current tree and binary`` () =
    let o =
        ofRead "/w" (Verdict.Report.Applies(greenConfirm ())) "sha256:now" "fshw 2" t0
        |> Option.get

    test <@ o.Today = Class.Green @>
    test <@ o.TodayOutcome = "applies:green" @>
    test <@ o.Tree = "sha256:now" @>
    test <@ o.Producer = "fshw 2" @>
    test <@ o.Command = "confirm" @>
    test <@ not o.Concluded @>
    test <@ List.isEmpty o.Suites @>
    test <@ o.RecordedAt = t0 @>

[<Fact>]
let ``a stale or in-flight read is no answer today`` () =
    let stale =
        ofRead "/w" (Verdict.Report.Stale(greenConfirm (), "moved")) "sha256:now" "fshw 1" t0
        |> Option.get

    let inFlight =
        ofRead "/w" (Verdict.Report.InFlight(greenConfirm (), "running")) "sha256:now" "fshw 1" t0
        |> Option.get

    test <@ (stale.Today, stale.TodayOutcome, stale.ExitCode) = (Class.Missing, "stale", 4) @>
    test <@ (inFlight.Today, inFlight.TodayOutcome, inFlight.ExitCode) = (Class.Missing, "in-flight", 6) @>

[<Fact>]
let ``a read answers from what publications filed, labels its line verdict, and files nothing`` () =
    withStore (fun repo store ->
        observeWith repo store projects confirmGreen |> ignore
        let before = List.length (allFilesUnder "*.json" (storeRoot repo))

        let read =
            { confirmGreen with
                Concluded = false
                Suites = []
                RecordedAt = t0.AddMinutes 3.0 }

        test <@ observeReadWith repo store projects read = LookupVerdict.Answer.Green @>
        let line = logLines repo |> List.last
        test <@ (str [ "command" ] line) = "verdict" @>
        test <@ (str [ "direction" ] line) = "agree" @>
        test <@ (num [ "recorded" ] line) = 0 @>
        let after = List.length (allFilesUnder "*.json" (storeRoot repo))
        test <@ after = before @>)

[<Fact>]
let ``a stale read of a tree the store already verified is looser, not a disagreement about the code`` () =
    withStore (fun repo store ->
        observeWith repo store projects confirmGreen |> ignore

        let staleRead =
            { confirmGreen with
                Today = Class.Missing
                TodayOutcome = "stale"
                ExitCode = 4
                Concluded = false
                Suites = [] }

        test <@ observeReadWith repo store projects staleRead = LookupVerdict.Answer.Green @>
        test <@ (str [ "direction" ] (logLines repo |> List.last)) = "looser" @>)

[<Fact>]
let ``observeRead with no tree hashed logs nothing`` () =
    withTempDir "shadow-read" (fun repo ->
        observeRead repo (Verdict.Report.NoVerdict "none") None
        test <@ not (File.Exists(logPath repo)) @>)

[<Fact>]
let ``observeRead logs a verdict line for a read with a tree, and nothing when the config is unreadable`` () =
    withTempDir "shadow-read-io" (fun repo ->
        Directory.CreateDirectory(Path.Combine(repo, ".fshw")) |> ignore

        File.WriteAllText(
            Path.Combine(repo, ".fshw.json"),
            """{ "tests": { "projects": [ { "project": "A.Tests" } ] } }"""
        )

        let tree: TreeHash.Tree =
            { Hash = "sha256:now"
              FileCount = 1
              SkippedCount = 0
              DeclaredCount = 0
              AbsentDeclarationCount = 0 }

        observeRead repo (Verdict.Report.Stale(greenConfirm (), "moved")) (Some tree)
        let line = logLines repo |> List.exactlyOne
        test <@ (str [ "command" ] line) = "verdict" @>
        test <@ (str [ "tree" ] line) = "sha256:now" @>
        test <@ (str [ "today"; "outcome" ] line) = "stale" @>

        // No verdict to read: nothing to compare, nothing logged.
        observeRead repo (Verdict.Report.NoVerdict "none") (Some tree)
        test <@ logLines repo |> List.length = 1 @>

        // An unreadable config is logged, never thrown.
        File.WriteAllText(Path.Combine(repo, ".fshw.json"), "{ not json")
        observeRead repo (Verdict.Report.Applies(greenConfirm ())) (Some tree)
        test <@ logLines repo |> List.length = 1 @>)

[<Fact>]
let ``a shadow that cannot write its log is logged and never thrown, on publication and read`` () =
    withTempDir "shadow-unwritable" (fun repo ->
        Directory.CreateDirectory(Path.Combine(repo, ".fshw")) |> ignore

        File.WriteAllText(
            Path.Combine(repo, ".fshw.json"),
            """{ "tests": { "projects": [ { "project": "A.Tests" } ] } }"""
        )

        // A FILE where the shadow directory belongs: every write under it fails.
        File.WriteAllText(directory repo, "in the way")

        let tree: TreeHash.Tree =
            { Hash = "sha256:now"
              FileCount = 1
              SkippedCount = 0
              DeclaredCount = 0
              AbsentDeclarationCount = 0 }

        observe repo confirmGreen
        observeRead repo (Verdict.Report.Applies(greenConfirm ())) (Some tree)
        test <@ File.Exists(directory repo) @>)

[<Fact>]
let ``a plugin still running in a read verdict records nothing and is required`` () =
    let v =
        Verdict.create
            Verdict.Check
            (BaselineFixtures.reportOf (IpcParsing.FullSuite(1, None)))
            ({ Hash = "sha256:published"
               FileCount = 1
               SkippedCount = 0
               DeclaredCount = 0
               AbsentDeclarationCount = 0 }
            : TreeHash.Tree)
            (Some [])
            (Verdict.Incomplete "still running")
            2
            [ { Name = "lint"
                Outcome = Verdict.PluginOutcome.Running
                ElapsedMs = None
                Summary = None }
              { Name = "build"
                Outcome = Verdict.PluginOutcome.TimedOut
                ElapsedMs = Some 1L
                Summary = None } ]
            []
            Verdict.CheckComparison.notRecorded
            []
            ProjectModelFixtures.available

    let o =
        ofRead "/w" (Verdict.Report.Applies v) "sha256:now" "fshw 1" t0 |> Option.get

    test <@ o.Plugins = [ "lint", None; "build", Some OutcomeStore.Outcome.Fail ] @>
    test <@ o.TodayOutcome = "applies:incomplete" @>
