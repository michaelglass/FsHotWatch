module FsHotWatch.Tests.VerdictTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Cli
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/// A repo with one source file and one CONTENT/fixture file, laid out the way
/// the daemon's discovery expects (`src/`, `tests/`).
let private makeRepo (root: string) =
    Directory.CreateDirectory(Path.Combine(root, "src", "Lib")) |> ignore

    Directory.CreateDirectory(Path.Combine(root, "tests", "Lib.Tests", "fixtures"))
    |> ignore

    File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 42\n")
    File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fsproj"), "<Project />")

    File.WriteAllText(Path.Combine(root, "tests", "Lib.Tests", "fixtures", "dsa-scope-4.json"), """{"leafFacts": 36}""")

/// A CTRF report shaped exactly as xUnit.v3 / Microsoft.Testing.Platform writes
/// it: the counts live under `results.summary`, and `stop` is epoch milliseconds.
let private ctrfJson (tests: int) (passed: int) (failed: int) (stop: DateTime) =
    let ms = DateTimeOffset(stop, TimeSpan.Zero).ToUnixTimeMilliseconds()

    let summary =
        JsonSerializer.Serialize
            {| tests = tests
               passed = passed
               failed = failed
               pending = 0
               skipped = 0
               other = 0
               suites = 1
               start = ms - 1000L
               stop = ms |}

    let results =
        $"""{{"tool":{{"name":"xUnit.net v3"}},"summary":%s{summary},"tests":[]}}"""

    $"""{{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"%s{Guid.NewGuid().ToString()}","results":%s{results}}}"""

/// A run that EXECUTED — its directory exists — and reported `project`.
let private writeReport (root: string) (runId: Guid) (project: string) (tests: int) (failed: int) =
    let dir = Ctrf.runDir root runId
    Directory.CreateDirectory(dir) |> ignore
    let path = Path.Combine(dir, project + Ctrf.ReportSuffix)
    File.WriteAllText(path, ctrfJson tests (tests - failed) failed DateTime.UtcNow)
    path

/// A run that EXECUTED but reported nothing — an EMPTY directory. The case the whole layout
/// exists for: a stated fact ("this run ran no tests"), not a silence a reader must decode.
let private emptyRun (root: string) (runId: Guid) =
    Directory.CreateDirectory(Ctrf.runDir root runId) |> ignore

/// What a test wants to SAY about a verdict.
///
/// The verdict itself is not hand-assemblable: `Verdict.create` is the only door, it stamps
/// `producedAt` and the producer from the running process, and it REFUSES a green carrying
/// a failing plugin. A test therefore declares its claim as a spec and lets the constructor
/// enforce the invariant — a test that could hand-build `{outcome: green, plugins: [fail]}`
/// would be testing a type that has been deliberately abolished.
type private Spec =
    {
        Command: Verdict.Command
        RunId: Guid option
        Scope: TestScope
        Outcome: Verdict.Outcome
        ExitCode: int
        Plugins: Verdict.PluginVerdict list
        Suites: Verdict.SuiteVerdict list
        Comparison: Verdict.CheckComparison
        /// AUTOMATION-303. The failing ledger diagnostics behind a red. Defaults empty.
        RedCauses: Verdict.RedCause list
        /// The change that selected this run's tests. Defaults empty; set it to
        /// exercise the trigger line in the no-suite report.
        Seeds: string list
        /// The TRUE seed count before truncation. Set it above `Seeds.Length` to
        /// exercise the "and N more" suffix; 0 means "however many Seeds there are".
        SeedTotal: int
        /// AUTOMATION-158. The declared, reasoned gaps in this run's scope.
        /// `Some []` — a positive "nothing was excluded" — is the default, because
        /// that is what a governed repo records.
        Excluded: SolutionScope.Exclusion list option
        Tree: TreeHash.Tree
    }

let private build (s: Spec) : Verdict.Verdict =
    Verdict.create
        s.Command
        { Scope = s.Scope
          RunId = s.RunId
          Seeds = s.Seeds
          SeedCount = max s.SeedTotal (List.length s.Seeds) }
        s.Tree
        s.Excluded
        s.Outcome
        s.ExitCode
        s.Plugins
        s.Suites
        s.Comparison
        s.RedCauses

let private greenVerdict (treeHash: string) (fileCount: int) : Spec =
    { Command = Verdict.Confirm
      RunId = None
      Scope = FullSuite 2
      Outcome = Verdict.Green
      ExitCode = 0
      Plugins =
        [ { Name = "test-prune"
            Outcome = Verdict.PluginOutcome.Ok
            ElapsedMs = Some 211_000L
            Summary = Some "6 passed, 0 failed in 6 projects" } ]
      Suites = []
      Comparison = Verdict.CheckComparison.notRecorded
      RedCauses = []
      Seeds = []
      SeedTotal = 0
      Excluded = Some []
      Tree =
        { Hash = treeHash
          FileCount = fileCount
          SkippedCount = 0
          DeclaredCount = 0
          AbsentDeclarationCount = 0 } }

let private structuralRedCause: Verdict.RedCause =
    { Source = "test-fixture"
      File = "src/Lib/Thing.fs"
      Severity = "error"
      Message = "the fixture's structural failure"
      Kind = Verdict.AboutThisTree }

let private writeSpec (root: string) (s: Spec) : unit = Verdict.write root (build s)
let private serializeSpec (s: Spec) : string = Verdict.serialize (build s)

/// No prior verdict — the default for tests that are not about prior evidence.
let private hintsFor (s: Spec) : string list =
    ProgressRenderer.AgentHints.forVerdict None (build s)

/// A verdict FILE that claims a DIFFERENT fshw produced it.
///
/// fshw cannot build one — `Verdict.create` stamps the running binary's identity. So this
/// forges the artifact on disk, the only way one can honestly exist. Patching the serialized
/// JSON keeps the test on the real wire format rather than inventing a second one.
let private writeVerdictClaimingAnotherBinary (root: string) (s: Spec) : unit =
    let node = System.Text.Json.Nodes.JsonNode.Parse(serializeSpec s)
    let producer = node.["producer"]
    producer.["version"] <- System.Text.Json.Nodes.JsonValue.Create "9.9.9-from-another-build"
    producer.["contentHash"] <- System.Text.Json.Nodes.JsonValue.Create "deadbeef00000000"
    Directory.CreateDirectory(Path.GetDirectoryName(Verdict.path root)) |> ignore
    File.WriteAllText(Verdict.path root, node.ToJsonString() + "\n")

// ---------------------------------------------------------------------------
// THE staleness guard. Everything else in this file is scaffolding for it.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a green verdict goes STALE the moment a source file is edited — it is never re-read as green`` () =
    withTempDir "verdict-stale" (fun root ->
        makeRepo root

        let before = TreeHash.compute root []
        writeSpec root (greenVerdict before.Hash before.FileCount)

        // CONTROL: it applies RIGHT NOW. Without this, a test proving only "stale after an
        // edit" would also pass if the verdict never applied at all.
        match Verdict.report root [] with
        | Verdict.Report.Applies v ->
            test <@ v.Outcome = Verdict.Green @>
            test <@ Verdict.reportExitCode (Verdict.Report.Applies v) = 0 @>
        | other -> failwith $"expected the verdict to apply to the tree it was earned on, got %A{other}"

        // Edit a source file; nothing else changes. The verdict is now a claim about a tree
        // that no longer exists.
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 43\n")

        match Verdict.report root [] with
        | Verdict.Report.Stale(v, currentTree) ->
            // Still a green verdict on disk — which is the point: a green from a different
            // tree is still a green, so the reader must be TOLD it does not apply.
            test <@ v.Outcome = Verdict.Green @>
            test <@ currentTree <> before.Hash @>
            test <@ Verdict.reportExitCode (Verdict.Report.Stale(v, currentTree)) = 4 @>
        | other -> failwith $"a verdict from a different tree must be STALE, got %A{other}")

[<Fact>]
let ``a changed CONTENT fixture makes the verdict stale — the dsa-scope-4 false green`` () =
    // APPLIC-24: `dsa-scope-4.json` changed (36 -> 40 leaf facts), MSBuild deemed the
    // consuming test project up-to-date and SKIPPED the copy, so the suite ran against the
    // OLD fixture and passed — 5136 tests, 0 failed, and main went red for hours.
    //
    // A fixture is an input to the verdict exactly as a source file is, so it is inside the
    // tree hash: change it and the previous green stops applying, with no MSBuild involved.
    withTempDir "verdict-content" (fun root ->
        makeRepo root

        let before = TreeHash.compute root []
        writeSpec root (greenVerdict before.Hash before.FileCount)

        let fixture =
            Path.Combine(root, "tests", "Lib.Tests", "fixtures", "dsa-scope-4.json")

        File.WriteAllText(fixture, """{"leafFacts": 40}""")

        match Verdict.report root [] with
        | Verdict.Report.Stale(v, _) -> test <@ v.Outcome = Verdict.Green @>
        | other -> failwith $"a changed fixture must make the verdict stale, got %A{other}")

[<Fact>]
let ``a changed .fshw.json makes the verdict stale — the config is an input too`` () =
    withTempDir "verdict-config" (fun root ->
        makeRepo root
        File.WriteAllText(FsHwPaths.configFile root, """{"format": true}""")

        let before = TreeHash.compute root []
        writeSpec root (greenVerdict before.Hash before.FileCount)

        File.WriteAllText(FsHwPaths.configFile root, """{"format": false}""")

        match Verdict.report root [] with
        | Verdict.Report.Stale _ -> ()
        | other -> failwith $"a changed config must make the verdict stale, got %A{other}")

[<Fact>]
let ``a NEW source file makes the verdict stale — the hash covers the file SET, not just contents`` () =
    // The fail-open shape to avoid: a hash over a fixed list of files would not
    // notice a file that was ADDED. The walk is over the tree, so it does.
    withTempDir "verdict-added" (fun root ->
        makeRepo root

        let before = TreeHash.compute root []
        writeSpec root (greenVerdict before.Hash before.FileCount)

        File.WriteAllText(Path.Combine(root, "src", "Lib", "New.fs"), "module New\n")

        match Verdict.report root [] with
        | Verdict.Report.Stale _ -> ()
        | other -> failwith $"an added file must make the verdict stale, got %A{other}")

[<Fact>]
let ``no verdict on disk is exit 5, never a green`` () =
    withTempDir "verdict-missing" (fun root ->
        makeRepo root

        match Verdict.report root [] with
        | Verdict.Report.NoVerdict _ -> test <@ Verdict.reportExitCode (Verdict.report root []) = 5 @>
        | other -> failwith $"expected NoVerdict, got %A{other}")

[<Fact>]
let ``a truncated verdict file is Unreadable, never a green`` () =
    withTempDir "verdict-torn" (fun root ->
        makeRepo root
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore
        File.WriteAllText(Verdict.path root, """{"schema":"fshw-verdict-v1","outcome":{"kind":"gr""")

        match Verdict.read root with
        | Verdict.Reading.Unreadable _ -> ()
        | other -> failwith $"a torn write must never parse as a verdict, got %A{other}")

[<Fact>]
let ``a verdict from a future schema is Unreadable, never a green`` () =
    withTempDir "verdict-schema" (fun root ->
        makeRepo root
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v9","treeHash":"sha256:x","outcome":{"kind":"green"}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "fshw-verdict-v9" @>
        | other -> failwith $"an unknown schema must never be read as a verdict, got %A{other}")

[<Fact>]
let ``a verdict with no treeHash is Unreadable — a verdict that cannot say WHICH tree is not a verdict`` () =
    withTempDir "verdict-notree" (fun root ->
        makeRepo root
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore
        File.WriteAllText(Verdict.path root, """{"schema":"fshw-verdict-v1","outcome":{"kind":"green"}}""")

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "treeHash" @>
        | other -> failwith $"expected Unreadable, got %A{other}")

// ---------------------------------------------------------------------------
// Round-trip: the CLI reads the same file it writes. No second truth.
// ---------------------------------------------------------------------------

[<Fact>]
let ``verdict round-trips through the file — the CLI reads what it wrote`` () =
    withTempDir "verdict-roundtrip" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        // `producedAt` is stamped by `create` and round-trips through ISO-8601 "O" at full
        // tick precision, so the equality below tests the CONTRACT, not the clock.
        let written =
            build
                { greenVerdict tree.Hash tree.FileCount with
                    Command = Verdict.Check
                    Scope = ImpactFiltered(2, 6)
                    Outcome = Verdict.Red
                    ExitCode = 1
                    RedCauses = [ structuralRedCause ]
                    Suites =
                        [ { Project = "Lib.Tests"
                            Ctrf = ".fshw/test-runs/Lib.Tests-0123456789abcdef0123456789abcdef.ctrf.json"
                            Total = 63
                            Passed = 60
                            Failed = 3
                            Skipped = 0 } ] }

        Verdict.write root written

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            // EVERY field, not just the ones the exit code needs. A read that silently
            // drops `plugins` and `suites` makes `fshw verdict` a LOSSY re-serialization of
            // the file it reports on: an agent piping it sees `"suites": []` for a run that
            // produced reports, and goes looking for them itself.
            test <@ v = written @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``a confirm verdict round-trips as a CONFIRM — the writer and the reader agree on the token`` () =
    // AUTOMATION-160. The `command` field is a WIRE token: `Verdict.write` emits
    // `Command.token` and `Verdict.read` matches a string literal back, and nothing makes
    // the two sides agree except this test.
    //
    // The failure is silent: the reader's `else` branch is `Check`, so a writer emitting
    // "confirm" against a reader still looking for "gate" parses a full-suite merge verdict
    // back as an impact-scoped inner-loop one — a DOWNGRADE with no parse error and a green
    // exit code.
    withTempDir "verdict-confirm-roundtrip" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Command = Verdict.Confirm }

        match Verdict.read root with
        | Verdict.Reading.Found v -> test <@ v.Command = Verdict.Confirm @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``the verdict is written atomically — no .tmp is left behind`` () =
    withTempDir "verdict-atomic" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []
        writeSpec root (greenVerdict tree.Hash tree.FileCount)

        test <@ File.Exists(Verdict.path root) @>
        test <@ not (File.Exists(Verdict.path root + ".tmp")) @>)

[<Fact>]
let ``the serialized outcome is UNIFORMLY tagged — a consumer never type-switches on a field`` () =
    // A reader must be able to act on this without first discriminating a JSON string from
    // a JSON object — a shape you have to sniff is a shape that invites a regex.
    let json =
        serializeSpec
            { greenVerdict "sha256:abc" 3 with
                Outcome = Verdict.Incomplete "tests did not run" }

    use doc = JsonDocument.Parse(json)

    let read (path: string list) =
        let mutable el = doc.RootElement

        for p in path do
            el <- el.GetProperty(p)

        el.GetString()

    test <@ read [ "outcome"; "kind" ] = "incomplete" @>
    test <@ read [ "outcome"; "reason" ] = "tests did not run" @>
    test <@ read [ "scope"; "kind" ] = "full" @>
    test <@ read [ "treeHashAlgorithm" ] = TreeHash.Algorithm @>

[<Fact>]
let ``an UnearnedScope confirm is INCOMPLETE in the file, never green`` () =
    // An impact-filtered run is not the claim a merge needs: nothing is reported broken,
    // and nothing is reported sound either. That must survive the trip to disk.
    let outcome =
        Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.UnearnedScope(ImpactFiltered(2, 6)))

    match outcome with
    | Verdict.Incomplete reason -> test <@ reason.Contains "not the full suite" @>
    | other -> failwith $"an unearned scope must never be green, got %A{other}"

[<Fact>]
let ``every check outcome maps to a file outcome — and only Clean is green`` () =
    test <@ Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.Clean = Verdict.Green @>
    test <@ Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.FailuresFound = Verdict.Red @>

    let incomplete o =
        match Verdict.outcomeOfCheck o with
        | Verdict.Incomplete _ -> true
        | _ -> false

    test <@ incomplete (CheckVerdict.CheckOutcome.Incomplete 3) @>
    test <@ incomplete (CheckVerdict.CheckOutcome.Incomplete -1) @>
    // "Waiting on build" is INCOMPLETE (exit 2), never Red or Green: a deferred project is
    // a retry signal, not a test failure.
    test <@ incomplete (CheckVerdict.CheckOutcome.WaitingOnBuild []) @>

    test
        <@
            Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.WaitingOnBuild [])
            <> Verdict.Red
        @>

    test
        <@
            Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.WaitingOnBuild [])
            <> Verdict.Green
        @>

    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun) @>
    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope ScopeUnknown) @>
    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope(FullSuite 2)) @>
    // AUTOMATION-294. A killed test host is INCOMPLETE, never Red: nothing failed there.
    test <@ incomplete (CheckVerdict.CheckOutcome.RunnerAborted [ "x: aborted — killed" ]) @>

[<Fact>]
let ``AUTOMATION-294: a killed host serializes as incomplete with an ABORT reason, never red`` () =
    // The MACHINE-READABLE half of the fix. A consumer reads the structured outcome, and
    // it used to read `red` — a definite negative about code that had not been tested at
    // all. It must now be able to tell "the runner died" from "a test failed" WITHOUT
    // inferring anything from 0ms durations in a suite listing.
    let aborts =
        [ "FsHotWatch.Tests: aborted — test host was KILLED by SIGKILL (exit 137)" ]

    match Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.RunnerAborted aborts) with
    | Verdict.Incomplete reason ->
        test <@ reason.ToUpperInvariant().Contains "NO VERDICT" @>
        test <@ reason.ToUpperInvariant().Contains "KILLED" @>
        // The projects are NAMED — one list, carried, not a second one to keep in step.
        test <@ reason.Contains "FsHotWatch.Tests" @>
        test <@ reason.Contains "SIGKILL" @>
        // And it tells the reader what the per-test lines they are staring at actually
        // are, which is the sentence that saves the investigation.
        test <@ reason.ToUpperInvariant().Contains "TRANSCRIPT" @>
    | other -> failwith $"a killed test host must be incomplete, never red/green, got %A{other}"

    test
        <@
            Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.RunnerAborted aborts)
            <> Verdict.Red
        @>

    test
        <@
            Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.RunnerAborted aborts)
            <> Verdict.Green
        @>

    test
        <@ Verdict.Outcome.tag (Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.RunnerAborted aborts)) = "incomplete" @>

    // THE OTHER DIRECTION, on the surface a consumer actually reads: a genuine failure
    // still tags `red`, so this cannot have turned every regression into "the box was
    // busy".
    test <@ Verdict.Outcome.tag (Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.FailuresFound) = "red" @>
    test <@ CheckVerdict.exitCode (CheckVerdict.CheckOutcome.RunnerAborted aborts) = 2 @>
    test <@ CheckVerdict.exitCode CheckVerdict.CheckOutcome.FailuresFound = 1 @>

[<Fact>]
let ``waiting on build persists as a DISTINCT incomplete verdict (exit 2), never red`` () =
    // The deploy preflight reads the STRUCTURED outcome, never the prose. A build-ordering
    // deferral must serialize as `outcome.kind = "incomplete"` with a "waiting on build"
    // reason — distinct from the `red` a real test failure earns — and carry exit 2.
    let outcome = Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.WaitingOnBuild [])

    match outcome with
    | Verdict.Incomplete reason -> test <@ reason.ToLowerInvariant().Contains "waiting on build" @>
    | other -> failwith $"waiting on build must be incomplete, never red/green, got %A{other}"

    test <@ CheckVerdict.exitCode (CheckVerdict.CheckOutcome.WaitingOnBuild []) = 2 @>

    let json =
        serializeSpec
            { greenVerdict "sha256:abc" 3 with
                Outcome = outcome }

    use doc = JsonDocument.Parse(json)
    let outcomeEl = doc.RootElement.GetProperty("outcome")
    let kind = outcomeEl.GetProperty("kind").GetString()
    let reason = outcomeEl.GetProperty("reason").GetString().ToLowerInvariant()
    test <@ kind = "incomplete" @>
    test <@ reason.Contains "waiting on build" @>

[<Fact>]
let ``the report envelope always says whether the verdict applies`` () =
    withTempDir "verdict-envelope" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []
        writeSpec root (greenVerdict tree.Hash tree.FileCount)

        let applies (envelope: string) =
            use doc = JsonDocument.Parse(envelope)
            doc.RootElement.GetProperty("applies").GetBoolean()

        let reason (envelope: string) =
            use doc = JsonDocument.Parse(envelope)
            doc.RootElement.GetProperty("reason").GetString()

        test <@ applies (Verdict.serializeReport (Verdict.report root [])) @>

        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 99\n")

        let stale = Verdict.serializeReport (Verdict.report root [])
        // A stale green must not LOOK like a green to anything reading stdout.
        test <@ not (applies stale) @>
        test <@ (reason stale).Contains "stale" @>)

// ---------------------------------------------------------------------------
// Tree hash
// ---------------------------------------------------------------------------

[<Fact>]
let ``the tree hash is stable across repeated computation`` () =
    withTempDir "tree-stable" (fun root ->
        makeRepo root
        let a = TreeHash.compute root []
        let b = TreeHash.compute root []
        test <@ a.Hash = b.Hash @>
        test <@ a.FileCount = b.FileCount @>
        test <@ a.FileCount > 0 @>)

[<Fact>]
let ``the tree hash ignores build output and honours config excludes`` () =
    withTempDir "tree-excludes" (fun root ->
        makeRepo root
        let baseline = TreeHash.compute root []

        // Build output must never move the hash, or every build would invalidate the
        // verdict it just earned.
        Directory.CreateDirectory(Path.Combine(root, "src", "Lib", "obj")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib", "obj", "gen.fs"), "// generated")
        Directory.CreateDirectory(Path.Combine(root, "src", "Lib", "bin")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib", "bin", "Lib.dll"), "binary")

        test <@ (TreeHash.compute root []).Hash = baseline.Hash @>

        // A config-excluded file must not move it either.
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Scratch.fs"), "module Scratch")
        test <@ (TreeHash.compute root [ "src/Lib/Scratch.fs" ]).Hash = baseline.Hash @>
        // ...but WITHOUT the exclude it does — the control that proves the exclude works,
        // rather than the hash being inert.
        test <@ (TreeHash.compute root []).Hash <> baseline.Hash @>)

[<Fact>]
let ``hashEntries is order-independent in input but sensitive to content`` () =
    let a = [ "src/A.fs", "aaa"; "src/B.fs", "bbb" ]
    let b = [ "src/A.fs", "aaa"; "src/B.fs", "ccc" ]
    test <@ TreeHash.hashEntries a = TreeHash.hashEntries a @>
    test <@ TreeHash.hashEntries a <> TreeHash.hashEntries b @>
    test <@ (TreeHash.hashEntries a).StartsWith "sha256:" @>

[<Fact>]
let ``the NUL separator makes two different trees unable to collide`` () =
    // With a space separator, ("a b", hash) and ("a", "b hash") produce the same
    // byte stream. With NUL they cannot.
    let x = [ "a b", "deadbeef" ]
    let y = [ "a", "b deadbeef" ]
    test <@ TreeHash.hashEntries x <> TreeHash.hashEntries y @>

// ---------------------------------------------------------------------------
// CTRF discovery — the suites pointer
// ---------------------------------------------------------------------------

[<Fact>]
let ``suites are the reports in THIS RUN's directory — membership is declared, never inferred`` () =
    withTempDir "ctrf-run-dir" (fun root ->
        makeRepo root

        let mine = Guid.NewGuid()
        let someoneElses = Guid.NewGuid()

        // Another run's reports, sitting right there on disk. Under the old flat layout the
        // only way to exclude them was to sort by mtime and hope.
        writeReport root someoneElses "Old.Tests" 10 0 |> ignore
        writeReport root someoneElses "Lib.Tests" 999 7 |> ignore

        writeReport root mine "Lib.Tests" 63 0 |> ignore

        let suites = Verdict.suiteVerdicts root (Some mine)

        test <@ suites |> List.map (fun s -> s.Project) = [ "Lib.Tests" ] @>
        test <@ suites.Head.Total = 63 @>
        test <@ suites.Head.Failed = 0 @>
        let mineDir = mine.ToString("N")
        test <@ suites.Head.Ctrf = $".fshw/test-runs/%s{mineDir}/Lib.Tests.ctrf.json" @>)

[<Fact>]
let ``a run that EXECUTED but ran no tests has an EMPTY directory — a fact, not a silence`` () =
    // The absence has a shape: the run-dir EXISTS (the run happened) and is EMPTY (it
    // tested nothing), so neither "cleaned up" nor "wrong glob" is a possible reading.
    withTempDir "ctrf-empty-run" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        emptyRun root runId

        test <@ Ctrf.runExists root runId @>
        test <@ List.isEmpty (Verdict.suiteVerdicts root (Some runId)) @>)

[<Fact>]
let ``no run id means NO RUN HAPPENED — and there is no directory to find`` () =
    withTempDir "ctrf-no-run" (fun root ->
        makeRepo root
        test <@ not (Ctrf.runExists root (Guid.NewGuid())) @>
        test <@ List.isEmpty (Verdict.suiteVerdicts root None) @>)

[<Fact>]
let ``failing counts survive into the suites — the verdict answers "how many failed" INLINE`` () =
    // The number must not depend on the CTRF file still being readable — a count that
    // evaporates when a file is rotated away is not an answer.
    withTempDir "ctrf-failed" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        let path = writeReport root runId "Lib.Tests" 63 3

        let suites = Verdict.suiteVerdicts root (Some runId)
        test <@ suites.Head.Failed = 3 @>
        test <@ suites.Head.Passed = 60 @>

        // Delete the report; the counts already copied into the verdict stand.
        File.Delete path

        writeSpec
            root
            { greenVerdict "sha256:abc" 1 with
                Suites = suites }

        match Verdict.read root with
        | Verdict.Reading.Found back -> test <@ back.Suites.Head.Failed = 3 @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``tidyRunsDir rotates old RUNS and purges the pre-AUTOMATION-129 flat layout`` () =
    withTempDir "ctrf-tidy" (fun root ->
        makeRepo root

        // 13 runs — more than the retention bound.
        let runs = [ for _ in 1..13 -> Guid.NewGuid() ]

        for r in runs do
            writeReport root r "Lib.Tests" 10 0 |> ignore
            // The dirs are created in order; nudge mtimes so "newest" is well-defined.
            Directory.SetLastWriteTimeUtc(Ctrf.runDir root r, DateTime.UtcNow)
            System.Threading.Thread.Sleep 5

        // The dead formats, loose at the top level: a flat CTRF nothing could attribute to
        // a run, and a `.log` written only on failure — whose date read as "when tests last
        // ran" while tests had in fact run hundreds of times since.
        let deadLog =
            Path.Combine(Ctrf.reportsDir root, "Lib.Tests-20260630T140735525Z.log")

        let flatCtrf =
            Path.Combine(Ctrf.reportsDir root, "Lib.Tests-0123456789abcdef0123456789abcdef.ctrf.json")

        File.WriteAllText(deadLog, "raw runner output")
        File.WriteAllText(flatCtrf, ctrfJson 5 5 0 DateTime.UtcNow)

        Ctrf.tidyRunsDir root Ctrf.RetainedRuns

        test <@ not (File.Exists deadLog) @>
        test <@ not (File.Exists flatCtrf) @>

        let survivors = runs |> List.filter (Ctrf.runExists root)
        test <@ survivors.Length = Ctrf.RetainedRuns @>
        // History is EVIDENCE: the newest runs survive, and nothing is wiped on start.
        test <@ survivors = List.skip (runs.Length - Ctrf.RetainedRuns) runs @>)

[<Fact(Timeout = 60000)>]
let ``tidyRunsDir cannot FAULT the run it is cleaning up after, however the directory moves`` () =
    // The tidy once enumerated the run directory TWICE and applied the second
    // enumeration's COUNT to the first enumeration's LIST:
    //
    //     runDirs repoRoot |> List.skip (min keepRuns (List.length (runDirs repoRoot)))
    //
    // A run directory appearing between the two (a second fshw process, a parallel test
    // project finishing its own run) makes the skip count exceed the list, and
    // `List.skip`'s `ArgumentException` escapes the enclosing IOException handler — so the
    // tidy faults the very run it was tidying up after. Hence a creator racing 200 tidies.
    withTempDir "ctrf-tidy-race" (fun root ->
        makeRepo root
        Directory.CreateDirectory(Ctrf.reportsDir root) |> ignore

        use stop = new System.Threading.CancellationTokenSource()

        let creator =
            System.Threading.Tasks.Task.Run(fun () ->
                while not stop.IsCancellationRequested do
                    Directory.CreateDirectory(Ctrf.runDir root (Guid.NewGuid())) |> ignore
                    System.Threading.Thread.Sleep 1)

        try
            // A retention bound well above the directory count, so the skip count is the
            // LIVE count: any dir that appears mid-call pushes it past the list's length.
            for _ in 1..200 do
                Ctrf.tidyRunsDir root 100_000
        finally
            stop.Cancel()
            creator.Wait(5_000) |> ignore)

[<Fact>]
let ``a report that is not JSON, or has no summary, yields no counts`` () =
    test <@ Option.isNone (Ctrf.trySummary "{{{ not json") @>
    test <@ Option.isNone (Ctrf.trySummary "null") @>
    test <@ Option.isNone (Ctrf.trySummary """{"results":{}}""") @>
    test <@ Option.isNone (Ctrf.trySummary """{"results":{"summary":null}}""") @>
    // A flattened (top-level `summary`) variant IS understood.
    test <@ (Ctrf.trySummary """{"summary":{"tests":3,"passed":3}}""").Value.Total = 3 @>
    // A summary with no counts is zeros, not a crash — and zeros are not a pass.
    test <@ (Ctrf.trySummary """{"results":{"summary":{}}}""").Value.Total = 0 @>

[<Fact>]
let ``a report whose counts are not numbers reads as zeros, never as a pass`` () =
    let summary =
        Ctrf.trySummary """{"results":{"summary":{"tests":"many","passed":true}}}"""

    test <@ summary.Value.Total = 0 @>
    test <@ summary.Value.Passed = 0 @>

[<Fact>]
let ``junk in a run directory is not evidence`` () =
    withTempDir "ctrf-junk" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        let dir = Ctrf.runDir root runId
        Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(Path.Combine(dir, "Lib.Tests" + Ctrf.ReportSuffix), "not json")
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "hello")

        test <@ List.isEmpty (Ctrf.reportsForRun root runId) @>
        // ...but the run still EXISTS. "Ran and reported nothing usable" is not the
        // same fact as "never ran", and the directory keeps them apart.
        test <@ Ctrf.runExists root runId @>

        Ctrf.tidyRunsDir root Ctrf.RetainedRuns |> ignore)

[<Fact>]
let ``an UNREADABLE report is not evidence — it is skipped, never counted as a pass`` () =
    withTempDir "ctrf-perm" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        let path = writeReport root runId "Lib.Tests" 63 0
        test <@ (Ctrf.reportsForRun root runId).Length = 1 @>

        File.SetUnixFileMode(path, UnixFileMode.None)

        try
            test <@ List.isEmpty (Ctrf.reportsForRun root runId) @>
        finally
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

// ---------------------------------------------------------------------------
// The steering hints — a contract now, so they are pinned like one.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the agent hint names the verdict file and THIS run's real CTRF paths`` () =
    let v =
        { greenVerdict "sha256:abc" 12 with
            Suites =
                [ { Project = "Intelligence.Tests.Unit"
                    Ctrf = ".fshw/test-runs/Intelligence.Tests.Unit-8134092fe179432cae568c393a563131.ctrf.json"
                    Total = 5136
                    Passed = 5136
                    Failed = 0
                    Skipped = 0 }
                  { Project = "Intelligence.Tests.Integration"
                    Ctrf = ".fshw/test-runs/Intelligence.Tests.Integration-7134d9cfbee943df9cdf24d622dc31ca.ctrf.json"
                    Total = 210
                    Passed = 210
                    Failed = 0
                    Skipped = 0 } ] }

    let lines = hintsFor v
    let text = String.concat "\n" lines

    test <@ text.Contains "AGENTS: READ the above" @>
    test <@ text.Contains "SCREEN-SCRAPE" @>
    test <@ text.Contains ".fshw/verdict.json" @>
    test <@ text.Contains "treeHash" @>
    // The ACTUAL paths for THIS run, not a generic pointer — a hint that makes you go and
    // find the file is a hint you will ignore.
    test <@ text.Contains ".fshw/test-runs/Intelligence.Tests.Unit-8134092fe179432cae568c393a563131.ctrf.json" @>

    test <@ text.Contains ".fshw/test-runs/Intelligence.Tests.Integration-7134d9cfbee943df9cdf24d622dc31ca.ctrf.json" @>

[<Fact>]
let ``an impact-scoped check is TOLD it is impact-scoped, and pointed at confirm`` () =
    let v =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Check
            Scope = ImpactFiltered(2, 6) }

    let text = hintsFor v |> String.concat "\n"

    test <@ text.Contains "impact-scoped (2/6 test projects)" @>
    test <@ text.Contains "fshw confirm" @>

[<Fact>]
let ``a check that ran no tests is pointed at confirm too — the emptiest evidence needs the loudest hint`` () =
    let v =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Check
            Scope = NoTestsRun }

    let text = hintsFor v |> String.concat "\n"
    test <@ text.Contains "fshw confirm" @>

[<Fact>]
let ``a full-suite check is not nagged, and a confirm never is`` () =
    let full =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Check
            Scope = FullSuite 6 }

    // A confirm that did NOT reach full-suite scope — the escalation-failure shape, which
    // now records `ScopeUnreadable` rather than the filtered reading it was left holding
    // (AUTOMATION-258). Still never nagged to "use `fshw confirm`": it IS confirm.
    let confirmed =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Confirm
            Scope = ScopeUnreadable "confirm forced the full suite and that run did not complete" }

    test <@ not ((hintsFor full |> String.concat "\n").Contains "fshw confirm") @>
    test <@ not ((hintsFor confirmed |> String.concat "\n").Contains "fshw confirm") @>

[<Fact>]
let ``a run with no suites SAYS so rather than pointing at nothing`` () =
    let text = hintsFor (greenVerdict "sha256:abc" 12) |> String.concat "\n"

    test <@ text.Contains "NO TEST RUN" @>

/// "no tests ran" is TWO different facts wearing one sentence: NOTHING WAS
/// VERIFIED, and NOTHING NEEDED RE-VERIFYING because the tree is unchanged since
/// a run that did verify it. Only the first is a gap.
///
/// Reporting them identically is not merely terse, it misleads: an agent read
/// `NONE — the run executed no tests`, concluded it had no evidence, and spent an
/// afternoon hunting a phantom test-selection bug — while the passing run for
/// that exact tree sat in `verdict.json` the whole time. This is the same
/// principle `ScopeUnknown`/`ScopeUnreadable` already encode (AUTOMATION-150):
/// different facts are different values, and a report that collapses them turns
/// a reader against the tool.
///
/// The verdict itself is UNCHANGED — `NoTestsRun` is still not merge evidence.
/// This adds context to a refusal; it must never soften one.
[<Fact>]
let ``a no-suite run names the prior verdict that DID verify this same tree`` () =
    // The prior verdict is PASSED IN rather than read from disk here, because by
    // the time the hints render, `Verdict.write` has already overwritten the file
    // (IpcOutput.fs writes at :563 and renders at :568). The caller must capture
    // the prior reading BEFORE the write; making that an argument keeps this
    // renderer pure and the ordering constraint impossible to get wrong silently.
    let prior =
        build
            { greenVerdict "sha256:same" 12 with
                Suites =
                    [ { Project = "Lib.Tests"
                        Ctrf = ".fshw/test-runs/aaaaaaaa/Lib.Tests.ctrf.json"
                        Total = 63
                        Passed = 63
                        Failed = 0
                        Skipped = 0 } ] }

    // THIS run selected nothing, over the SAME tree.
    let current =
        build
            { greenVerdict "sha256:same" 12 with
                Suites = []
                RunId = Some(Guid.NewGuid()) }

    let text =
        ProgressRenderer.AgentHints.forVerdict (Some prior) current
        |> String.concat "\n"

    test <@ text.Contains "tree unchanged since" @>
    test <@ text.Contains "Lib.Tests" @>

/// The guard on the above. `forVerdict`'s existing rule is "NEVER print a path
/// for a file that was not written" — naming a prior run that does NOT apply to
/// this tree is the same sin in a new place, and a worse one: it would present
/// stale evidence as current. A treeHash mismatch must produce silence.
[<Fact>]
let ``a no-suite run does NOT name a prior verdict from a different tree`` () =
    let prior =
        build
            { greenVerdict "sha256:OLD-TREE" 12 with
                Suites =
                    [ { Project = "Lib.Tests"
                        Ctrf = ".fshw/test-runs/bbbbbbbb/Lib.Tests.ctrf.json"
                        Total = 63
                        Passed = 63
                        Failed = 0
                        Skipped = 0 } ] }

    let current =
        build
            { greenVerdict "sha256:NEW-TREE" 12 with
                Suites = []
                RunId = Some(Guid.NewGuid()) }

    let text =
        ProgressRenderer.AgentHints.forVerdict (Some prior) current
        |> String.concat "\n"

    test <@ not (text.Contains "tree unchanged since") @>
    test <@ not (text.Contains "Lib.Tests") @>

/// Knowing WHEN the tree was last verified is only half an answer. The reader's
/// actual question is whether the run that happened was the run their edit
/// deserved, and they cannot judge that without knowing WHAT triggered it.
[<Fact>]
let ``a no-suite run names the CHANGE that triggered the prior run`` () =
    let prior =
        build
            { greenVerdict "sha256:same" 12 with
                Suites =
                    [ { Project = "Lib.Tests"
                        Ctrf = ".fshw/test-runs/cccccccc/Lib.Tests.ctrf.json"
                        Total = 63
                        Passed = 63
                        Failed = 0
                        Skipped = 0 } ]
                Seeds = [ "Lib.Config.deployVars" ] }

    let current =
        build
            { greenVerdict "sha256:same" 12 with
                Suites = []
                RunId = Some(Guid.NewGuid()) }

    let text =
        ProgressRenderer.AgentHints.forVerdict (Some prior) current
        |> String.concat "\n"

    test <@ text.Contains "triggered by Lib.Config.deployVars" @>

/// The seed list is truncated on the wire, so the line must say how many it is
/// NOT showing. A short list presented as the whole story is the same class of
/// lie as "no tests ran" — technically true, and it sends the reader off with a
/// wrong picture of what happened.
[<Fact>]
let ``a truncated trigger says how many seeds it is not showing`` () =
    let prior =
        build
            { greenVerdict "sha256:same" 12 with
                Suites =
                    [ { Project = "Lib.Tests"
                        Ctrf = ".fshw/test-runs/dddddddd/Lib.Tests.ctrf.json"
                        Total = 4
                        Passed = 4
                        Failed = 0
                        Skipped = 0 } ]
                Seeds = [ "Lib.A.one"; "Lib.B.two" ]
                SeedTotal = 5 }

    let current =
        build
            { greenVerdict "sha256:same" 12 with
                Suites = []
                RunId = Some(Guid.NewGuid()) }

    let text =
        ProgressRenderer.AgentHints.forVerdict (Some prior) current
        |> String.concat "\n"

    test <@ text.Contains "Lib.A.one, Lib.B.two" @>
    test <@ text.Contains "and 3 more" @>

/// A prior verdict that ALSO ran no tests carries nothing to report. Naming it
/// would answer "when was this last verified?" with "it wasn't" dressed up as an
/// answer — worse than the silence it replaces.
[<Fact>]
let ``a no-suite run does NOT name a prior verdict that itself ran no tests`` () =
    let prior =
        build
            { greenVerdict "sha256:same" 12 with
                Suites = [] }

    let current =
        build
            { greenVerdict "sha256:same" 12 with
                Suites = []
                RunId = Some(Guid.NewGuid()) }

    let text =
        ProgressRenderer.AgentHints.forVerdict (Some prior) current
        |> String.concat "\n"

    test <@ not (text.Contains "tree unchanged since") @>

[<Fact>]
let ``the status hint names the latest run's reports and admits it triggered no run`` () =
    withTempDir "hint-status" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        writeReport root runId "Lib.Tests" 63 0 |> ignore

        let text = ProgressRenderer.AgentHints.forStatus root |> String.concat "\n"
        let dir = runId.ToString("N")

        test <@ text.Contains ".fshw/verdict.json" @>
        test <@ text.Contains $".fshw/test-runs/%s{dir}/Lib.Tests.ctrf.json" @>
        test <@ text.Contains "triggers no run" @>)

[<Fact>]
let ``the status hint is honest when nothing has ever run`` () =
    withTempDir "hint-status-empty" (fun root ->
        makeRepo root
        let text = ProgressRenderer.AgentHints.forStatus root |> String.concat "\n"
        test <@ text.Contains "no test run has produced a report yet" @>)

// ---------------------------------------------------------------------------
// Plugin verdicts — one value, two surfaces
// ---------------------------------------------------------------------------

let private status (view: StatusView) (elapsed: TimeSpan) (outcome: RunOutcome) (summary: string option) =
    { Status = view
      Subtasks = []
      ActivityTail = []
      LastRun =
        Some
            { StartedAt = DateTime.UtcNow
              Elapsed = elapsed
              Outcome = outcome
              Summary = summary
              ActivityTail = [] }
      Diagnostics = ErrorLedger.DiagnosticCounts.empty }

[<Fact>]
let ``plugin verdicts carry outcome, elapsed and summary`` () =
    let statuses =
        Map.ofList
            [ "test-prune",
              status
                  (StatusView.Completed DateTime.UtcNow)
                  (TimeSpan.FromSeconds 211.0)
                  Events.CompletedRun
                  (Some "6 passed, 0 failed in 6 projects") ]

    match Verdict.pluginVerdicts true DateTime.UtcNow statuses with
    | [ p ] ->
        test <@ p.Name = "test-prune" @>
        test <@ p.Outcome = Verdict.PluginOutcome.Ok @>
        test <@ p.ElapsedMs = Some 211_000L @>
        test <@ p.Summary = Some "6 passed, 0 failed in 6 projects" @>
    | other -> failwith $"expected one plugin verdict, got %A{other}"

[<Fact>]
let ``a plugin that never ran is OMITTED, never invented as a pass`` () =
    let idle =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = ErrorLedger.DiagnosticCounts.empty }

    test <@ List.isEmpty (Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "coverage", idle ])) @>

[<Fact>]
let ``the verdict file and the agent status line resolve a plugin identically`` () =
    // One function behind both, so a plugin cannot report `ok` on one surface and `fail` on
    // the other.
    let failed =
        status (StatusView.Failed("boom", DateTime.UtcNow)) (TimeSpan.FromSeconds 1.0) (Events.FailedRun "boom") None

    let fromFile =
        Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "build", failed ])

    let fromLine =
        ProgressRenderer.renderAll ProgressRenderer.Agent true DateTime.UtcNow (Map.ofList [ "build", failed ])
        |> String.concat "\n"

    test <@ fromFile.Head.Outcome = Verdict.PluginOutcome.Fail @>
    test <@ fromLine.Contains "build: fail" @>

// ---------------------------------------------------------------------------
// Totality of the parse layer.
//
// Every one of these is a way of NOT having a usable answer, and none may be rounded to a
// green — that rounding is the bug this subsystem exists to prevent — so each arm is pinned
// rather than left to a default.
// ---------------------------------------------------------------------------

[<Fact>]
let ``every scope round-trips through the file`` () =
    withTempDir "verdict-scopes" (fun root ->
        // Under `check`, which is the ONE command every scope is legal on: `confirm` may not
        // record an `ImpactFiltered` (AUTOMATION-258), so a confirm fixture could not
        // exercise the whole wire format this test exists to pin.
        let roundTrip (scope: TestScope) =
            writeSpec
                root
                { greenVerdict "sha256:abc" 1 with
                    Command = Verdict.Check
                    Scope = scope }

            match Verdict.read root with
            | Verdict.Reading.Found v -> v.Scope
            | other -> failwith $"expected a readable verdict, got %A{other}"

        test <@ roundTrip (FullSuite 6) = FullSuite 6 @>
        test <@ roundTrip (ImpactFiltered(2, 6)) = ImpactFiltered(2, 6) @>
        test <@ roundTrip NoTestsRun = NoTestsRun @>
        test <@ roundTrip ScopeUnknown = ScopeUnknown @>
        // The reason travels with it: a consumer that has to ask "unreadable why?" and
        // gets no answer will treat the check as flaky rather than as broken.
        test
            <@ roundTrip (ScopeUnreadable "the daemon's reply faulted") = ScopeUnreadable "the daemon's reply faulted" @>)

[<Fact>]
let ``a scope this build cannot read is ScopeUnreadable — distinct from "no scope reported", never full-suite`` () =
    withTempDir "verdict-badscope" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        let write (scopeJson: string) =
            File.WriteAllText(
                Verdict.path root,
                $$"""{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},"scope":{{scopeJson}}}"""
            )

            match Verdict.read root with
            | Verdict.Reading.Found v -> v.Scope
            | other -> failwith $"expected a readable verdict, got %A{other}"

        // The shared, exhaustive predicate — this was a local copy with a wildcard.
        let unreadable = TestScope.isUnreadable

        // POSITIVE CONTROL: the reader CAN produce a plain `ScopeUnknown`, from the one
        // input that means it. Without this, "everything is unreadable" would pass on a
        // reader that had simply stopped recognizing anything.
        test <@ write """{"kind":"unknown"}""" = ScopeUnknown @>

        // A kind from a future fshw; a `full` whose counts disagree (not a full suite,
        // whatever it calls itself); a scope that is not even an object. Each is a reading
        // this build could not make, which is a different fact from the daemon reporting
        // that there was no scope.
        test <@ unreadable (write """{"kind":"cosmic"}""") @>
        test <@ unreadable (write """{"kind":"full","ranProjects":2,"totalProjects":6}""") @>
        test <@ unreadable (write """{"kind":"full","ranProjects":0,"totalProjects":0}""") @>
        test <@ unreadable (write """"full" """) @>

        // ...and none of them is full-suite, which is the property that actually guards
        // the merge door.
        test <@ not (TestScope.isFullSuite (write """{"kind":"cosmic"}""")) @>)

[<Fact>]
let ``every plugin outcome round-trips — and an unrecognized one is FAIL, not ok`` () =
    withTempDir "verdict-plugins" (fun root ->
        let roundTrip (outcome: Verdict.PluginOutcome) =
            // `Verdict.create` refuses a green carrying a failing plugin, so a failing
            // plugin here has to ride a red verdict, exactly as it would in life.
            let verdictOutcome =
                if Verdict.PluginOutcome.isFailing outcome then
                    Verdict.Red
                else
                    Verdict.Green

            writeSpec
                root
                { greenVerdict "sha256:abc" 1 with
                    Outcome = verdictOutcome
                    ExitCode = (if verdictOutcome = Verdict.Red then 1 else 0)
                    Plugins =
                        [ { Name = "p"
                            Outcome = outcome
                            ElapsedMs = Some 5L
                            Summary = None } ] }

            match Verdict.read root with
            | Verdict.Reading.Found v -> v.Plugins.Head.Outcome
            | other -> failwith $"expected a readable verdict, got %A{other}"

        test <@ roundTrip Verdict.PluginOutcome.Ok = Verdict.PluginOutcome.Ok @>
        test <@ roundTrip Verdict.PluginOutcome.Warn = Verdict.PluginOutcome.Warn @>
        test <@ roundTrip Verdict.PluginOutcome.Fail = Verdict.PluginOutcome.Fail @>
        test <@ roundTrip Verdict.PluginOutcome.TimedOut = Verdict.PluginOutcome.TimedOut @>
        test <@ roundTrip Verdict.PluginOutcome.Running = Verdict.PluginOutcome.Running @>

        // An outcome token this build has never heard of: not dropped, not rounded to `ok`.
        // The carrying verdict is `red` so the file stays internally consistent and
        // READABLE — a green would confound the TOKEN rule with the invariant below.
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"red"},
                "plugins":[{"name":"p","outcome":"transcendent"},{"name":"q"}]}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            test <@ v.Plugins |> List.forall (fun p -> p.Outcome = Verdict.PluginOutcome.Fail) @>
            // Missing numbers/strings degrade to stated defaults, never to silence.
            test
                <@
                    v.Plugins
                    |> List.forall (fun p -> Option.isNone p.ElapsedMs && Option.isNone p.Summary)
                @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``a verdict file that says GREEN while a plugin says FAIL is not a verdict — it is UNREADABLE`` () =
    // `{"outcome":"green", "plugins":[{"outcome":"fail"}]}` is what `--run-once` used to
    // WRITE when a plugin crashed, and a reader lifting the `green` out of it would be
    // reading a check that never ran. `Verdict.create` makes it unbuildable and
    // `Verdict.read` makes it unliftable — both doors, because such a file can also arrive
    // from a hand edit, a future schema, or two surfaces that drifted apart again.
    withTempDir "verdict-contradiction" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        let contradictory (pluginOutcome: string) =
            File.WriteAllText(
                Verdict.path root,
                $$"""{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                    "plugins":[{"name":"p","outcome":"{{pluginOutcome}}"}]}"""
            )

            Verdict.read root

        for failing in [ "fail"; "timed-out"; "wedged"; "a-token-from-the-future" ] do
            match contradictory failing with
            | Verdict.Reading.Unreadable reason ->
                test <@ reason.Contains "GREEN verdict cannot contain failing plugins" @>
            | other -> failwithf "a green carrying a %s plugin must be UNREADABLE, got %A" failing other

        // The control: a green carrying only healthy plugins is a perfectly good verdict.
        // Without this, a `read` that refused everything would pass the loop above.
        match contradictory "ok" with
        | Verdict.Reading.Found v -> test <@ v.Outcome = Verdict.Green @>
        | other -> failwithf "a green carrying an ok plugin must READ, got %A" other

        // ...and `running` is not failing: a plugin still going is not a plugin that failed.
        match contradictory "running" with
        | Verdict.Reading.Found _ -> ()
        | other -> failwithf "a green carrying a running plugin must READ, got %A" other)

// ---------------------------------------------------------------------------
// AUTOMATION-258 — a CONFIRM verdict may not carry an impact-filtered scope.
// ---------------------------------------------------------------------------

[<Fact>]
let ``Verdict.create refuses a CONFIRM carrying an impact-filtered scope`` () =
    // `confirm` never accepts a filtered run: it DETECTS one and escalates
    // (`CheckVerdict.confirmNeedsFullRun`), and `CheckVerdict.verdict` has no branch that
    // reaches `Clean` without a `FullSuite`. So the pair can only ever be the reading of the
    // run confirm REFUSED — and a reader who lifted `scope: filtered` off a `command:
    // confirm` record reported that confirm was impact-filtering, the exact opposite of what
    // it had just done.
    let spec = greenVerdict "sha256:abc" 1

    raises<ArgumentException>
        <@
            build
                { spec with
                    Scope = ImpactFiltered(5, 6)
                    Outcome = Verdict.Red
                    ExitCode = 1
                    RedCauses = [ structuralRedCause ] }
        @>

    // The rule is about the PAIR, not about being non-green: an INCOMPLETE confirm cannot
    // record a filtered scope either, and that is the shape the escalation failure produced.
    raises<ArgumentException>
        <@
            build
                { spec with
                    Scope = ImpactFiltered(5, 6)
                    Outcome = Verdict.Incomplete "the tests that ran were impact-filtered"
                    ExitCode = 3 }
        @>

    // CONTROLS. Without these, a `create` that had simply started refusing everything would
    // pass both `raises` above. `confirm` + full suite is the verdict the verb exists to
    // produce...
    test <@ (build { spec with Scope = FullSuite 6 }).Scope = FullSuite 6 @>

    // ...and `check` + filtered is not merely tolerated, it is what the inner loop IS. The
    // rule must not over-fire onto the one command whose whole point is filtering.
    test
        <@
            (build
                { spec with
                    Command = Verdict.Check
                    Scope = ImpactFiltered(5, 6) })
                .Scope = ImpactFiltered(5, 6)
        @>

[<Fact>]
let ``a legacy confirm+filtered verdict FILE is UNREADABLE — it is re-earned, never migrated`` () =
    // Such files exist on disk: this is what `confirm` wrote whenever its forced full run
    // failed to complete. Refusing them costs nothing real — a confirm+filtered verdict can
    // never have been GREEN (`CheckVerdict.verdict` routes `Confirmation, ImpactFiltered` to
    // `UnearnedScope`), so no earned green is discarded, and `priorConfirmation` already
    // answered `MustEarn` for every one of them. And refusing is a `Reading.Unreadable`, not
    // a throw: the consumer re-earns the verdict instead of crashing on the way in.
    withTempDir "verdict-legacy-confirm-filtered" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        let legacy (command: string) =
            File.WriteAllText(
                Verdict.path root,
                $$"""{"schema":"fshw-verdict-v1","treeHash":"sha256:x","command":"{{command}}",
                    "scope":{"kind":"filtered","ranProjects":5,"totalProjects":6},
                    "outcome":{"kind":"red"},"exitCode":1,"plugins":[]}"""
            )

            Verdict.read root

        match legacy "confirm" with
        | Verdict.Reading.Unreadable reason ->
            test <@ reason.Contains "CONFIRM verdict cannot carry an impact-filtered scope" @>
            // The refusal quotes the counts it refused, so the file it rejected is still
            // diagnosable from the message alone.
            test <@ reason.Contains "5/6" @>
        | other -> failwithf "a confirm carrying a filtered scope must be UNREADABLE, got %A" other

        // The control, and the whole reason the rule is on the PAIR: the byte-identical file
        // under `command: check` is an ordinary inner-loop record and still reads.
        match legacy "check" with
        | Verdict.Reading.Found v -> test <@ v.Scope = ImpactFiltered(5, 6) @>
        | other -> failwithf "a check carrying a filtered scope must READ, got %A" other)

[<Fact>]
let ``a confirm whose forced full run did not complete records no filtered scope — and keeps its red`` () =
    // THE REGRESSION, and why the constructor test above is not enough on its own: with only
    // the `create` rule, `publishVerdict` would still assemble the refused value, turning the
    // lie into an `ArgumentException` escaping a handler that catches only IO faults — a
    // crash instead of a wrong file, which is worse for the caller.
    //
    // So this drives the PRODUCER with the observed input: the daemon's `test-scope` answers
    // with the PRE-escalation coverage whenever the forced run never finished (the scope is a
    // projection of `LastCoverage`), and publishing must not write that down verbatim.
    withTempDir "verdict-confirm-escalation" (fun root ->
        let publish (mode: CheckVerdict.CheckMode) (scope: TestScope) (outcome: CheckVerdict.CheckOutcome) =
            let redCauses =
                match outcome with
                | CheckVerdict.CheckOutcome.FailuresFound -> [ structuralRedCause ]
                | _ -> []

            IpcOutput.publishVerdict
                root
                []
                mode
                false
                (TestRunReport.ofScopeOnly scope)
                Verdict.NoReading
                Map.empty
                redCauses
                (IpcOutput.SettledTree.capture root [])
                outcome
            |> ignore

            match Verdict.read root with
            | Verdict.Reading.Found v -> v
            | other -> failwithf "publishVerdict must leave a READABLE verdict, got %A" other

        // The observed artifact: confirm escalated, the forced full run died on compile
        // errors, and the only scope on record was the earlier 5-of-6 run.
        let red =
            publish CheckVerdict.Confirmation (ImpactFiltered(5, 6)) CheckVerdict.CheckOutcome.FailuresFound

        test <@ red.Command = Verdict.Confirm @>
        test <@ not (TestScope.isFullSuite red.Scope) @>

        match red.Scope with
        | ImpactFiltered _ -> failwith "a confirm verdict must never record a filtered scope"
        // The record explains itself without the log beside it — the failure that motivated
        // the ticket was a reader who had only the file.
        | s -> test <@ (TestScope.describe s).Contains "did not complete" @>

        // The RED is PRESERVED, deliberately. Downgrading it to `incomplete` would tell a
        // deploy preflight to retry a tree whose build is broken.
        test <@ red.Outcome = Verdict.Red @>
        test <@ red.ExitCode = 1 @>

        // The same escalation failure with nothing actually failing. `UnearnedScope` is
        // already an incomplete; its SCOPE must not be filtered either.
        let unearned =
            publish
                CheckVerdict.Confirmation
                (ImpactFiltered(5, 6))
                (CheckVerdict.CheckOutcome.UnearnedScope(ImpactFiltered(5, 6)))

        match unearned.Outcome with
        | Verdict.Incomplete _ -> ()
        | other -> failwithf "an unearned confirm scope must be INCOMPLETE, got %A" other

        test <@ unearned.ExitCode = 3 @>

        match unearned.Scope with
        | ImpactFiltered _ -> failwith "a confirm verdict must never record a filtered scope"
        | _ -> ()

        // CONTROLS. The producer rewrites ONE pair, not every scope it is handed — without
        // these, a producer that blanked every scope would pass everything above.
        let earned =
            publish CheckVerdict.Confirmation (FullSuite 6) CheckVerdict.CheckOutcome.Clean

        test <@ earned.Scope = FullSuite 6 @>
        test <@ earned.Outcome = Verdict.Green @>

        // ...and `check` KEEPS its filtered scope. Hiding it would be the same lie reversed.
        let inner =
            publish CheckVerdict.InnerLoop (ImpactFiltered(5, 6)) CheckVerdict.CheckOutcome.Clean

        test <@ inner.Command = Verdict.Check @>
        test <@ inner.Scope = ImpactFiltered(5, 6) @>)

// ---------------------------------------------------------------------------
// AUTOMATION-259 — the check-vs-confirm sample a `confirm` leaves behind.
// ---------------------------------------------------------------------------

/// The pre-escalation reading, built the way the transports build it. Deliberately NOT a
/// hand-stamped `Outcome`: the claim under test is that the sub-record is graded as
/// `check` would grade it (`InnerLoop`), and a test that stamped the answer would pass
/// even if production graded it as `Confirmation` — which refuses every filtered scope and
/// would make the sample say "no verdict" every single time.
let private impactScopedReading (root: string) (scope: TestScope) (failingDiagnostics: int) (coverage: Coverage) =
    Verdict.impactScopedRun
        root
        (TestRunReport.ofScopeOnly scope)
        { PluginStatuses = Map.empty
          FailingDiagnostics = failingDiagnostics
          UnattributableDiagnostics = 0
          WaitingOnBuild = CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = CheckVerdict.RunnerAbort.NoAbort
          Coverage = coverage
          Scope = scope }

/// Drive the PRODUCER, as the 258 tests do: the transports hand `publishVerdict` what they
/// observed, and what lands on disk is what a reader gets.
let private publishConfirm
    (root: string)
    (finalScope: TestScope)
    (impactScoped: Verdict.ImpactScopedRun option)
    (outcome: CheckVerdict.CheckOutcome)
    : Verdict.Verdict =
    let redCauses =
        match outcome with
        | CheckVerdict.CheckOutcome.FailuresFound -> [ structuralRedCause ]
        | _ -> []

    IpcOutput.publishVerdict
        root
        []
        CheckVerdict.Confirmation
        false
        (TestRunReport.ofScopeOnly finalScope)
        (match impactScoped with
         | Some reading -> Verdict.ExecutedReading(reading, ReachUnavailable "test fixture")
         | None -> Verdict.NoReading)
        Map.empty
        redCauses
        (IpcOutput.SettledTree.capture root [])
        outcome
    |> ignore

    match Verdict.read root with
    | Verdict.Reading.Found v -> v
    | other -> failwithf "publishVerdict must leave a READABLE verdict, got %A" other

// =============================================================================
// AUTOMATION-167 — the file and the exit code are ONE decision
// =============================================================================

[<Fact>]
let ``a terminal infrastructure failure is published incomplete with its exact reason`` () =
    withTempDir "verdict-terminal-incomplete" (fun root ->
        let reason =
            "PROJECT LOADING FAILED: MSBuild evaluation loaded 0 of 18 discovered project(s). Read LoadProject FAILED."

        let exitCode =
            IpcOutput.publishTerminalIncomplete
                root
                []
                CheckVerdict.InnerLoop
                reason
                (IpcOutput.SettledTree.capture root [])

        test <@ exitCode = 2 @>

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            test <@ v.ExitCode = 2 @>

            match v.Outcome with
            | Verdict.Incomplete persisted -> test <@ persisted = reason @>
            | other -> failwithf "expected the exact infrastructure reason in an incomplete verdict, got %A" other
        | other -> failwithf "expected a published verdict, got %A" other)

[<Fact>]
let ``a moved tree is not assigned an earlier terminal infrastructure failure`` () =
    withTempDir "verdict-terminal-moved" (fun root ->
        let src = Path.Combine(root, "src")
        Directory.CreateDirectory(src) |> ignore
        let tracked = Path.Combine(src, "Tracked.fs")
        File.WriteAllText(tracked, "module Tracked\nlet value = 1")
        let settled = IpcOutput.SettledTree.capture root []
        File.WriteAllText(tracked, "module Tracked\nlet value = 2")

        let exitCode =
            IpcOutput.publishTerminalIncomplete
                root
                []
                CheckVerdict.InnerLoop
                "PROJECT LOADING FAILED: belongs to the earlier tree"
                settled

        test <@ exitCode = 2 @>

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            match v.Outcome with
            | Verdict.Incomplete persisted ->
                test <@ persisted.Contains("working tree changed") @>
                test <@ not (persisted.Contains("PROJECT LOADING FAILED")) @>
            | other -> failwithf "expected moved-tree incomplete, got %A" other
        | other -> failwithf "expected a published verdict, got %A" other)

[<Fact>]
let ``publishVerdict RETURNS the exit code it wrote, so a caller cannot compute a second one`` () =
    // The defect: `publishVerdict` downgrades to `incomplete`/2 when the tree moves
    // during a check, and the caller then re-derived the code from the ORIGINAL
    // outcome and returned 0. The file said "no verdict"; the shell said "pass"; CI
    // reads the shell. Returning the code removes the second computation entirely.
    //
    // This is the INVARIANT half only — it pins that the two renderings cannot be
    // computed separately. The half that proves the tree-move is actually SEEN lives at
    // the transports, where the move can happen between settling and publishing:
    // `IpcOutputTests` (daemon) and `RunOnceOutputTests` (`--run-once`).
    withTempDir "verdict-167-returns-code" (fun root ->
        let publishedFor (outcome: CheckVerdict.CheckOutcome) =
            IpcOutput.publishVerdict
                root
                []
                CheckVerdict.InnerLoop
                false
                (TestRunReport.ofScopeOnly (FullSuite 6))
                Verdict.NoReading
                Map.empty
                []
                (IpcOutput.SettledTree.capture root [])
                outcome

        // Asserted against the FILE rather than a duplicate of the production
        // expression, so changing one without the other fails here.
        for outcome in [ CheckVerdict.CheckOutcome.Clean; CheckVerdict.CheckOutcome.Incomplete -1 ] do
            let returned = publishedFor outcome

            match Verdict.read root with
            | Verdict.Reading.Found v -> test <@ returned = v.ExitCode @>
            | other -> failwithf "publishVerdict must leave a READABLE verdict, got %A" other)

[<Fact>]
let ``an escalated confirm records what the impact-scoped run concluded, and that they AGREED`` () =
    withTempDir "verdict-259-agreed" (fun root ->
        // The observed shape: a warm daemon whose last run was 5 of 6 projects and clean,
        // `confirm` escalating past it, and the forced full suite agreeing.
        let earned =
            publishConfirm
                root
                (FullSuite 6)
                (Some(impactScopedReading root (ImpactFiltered(5, 6)) 0 Complete))
                CheckVerdict.CheckOutcome.Clean

        test <@ earned.Outcome = Verdict.Green @>
        test <@ earned.Scope = FullSuite 6 @>
        test <@ earned.Divergence = Verdict.Divergence.Agreed @>

        // The sub-record is where being impact-filtered is CORRECT — the top-level scope
        // may not say it (AUTOMATION-258), and the counts now live here rather than in
        // anyone's prose.
        match earned.ImpactScopedRun with
        | Some pre ->
            test <@ pre.Scope = ImpactFiltered(5, 6) @>
            test <@ pre.Outcome = Verdict.Green @>
            test <@ List.isEmpty pre.FailingSuites @>
        | None -> failwith "an escalated confirm must record the run it escalated away from")

[<Fact>]
let ``check green + confirm red records CHECK MISSED FAILURES — the fshw defect, named`` () =
    withTempDir "verdict-259-missed" (fun root ->
        // AUTOMATION-160: this is not a merge saved, it is a SELECTOR BUG — `check` told
        // someone their change was fine and the full suite says it is not. The one case the
        // whole record exists to surface, so it gets its own token rather than "diverged".
        let earned =
            publishConfirm
                root
                (FullSuite 6)
                (Some(impactScopedReading root (ImpactFiltered(5, 6)) 0 Complete))
                CheckVerdict.CheckOutcome.FailuresFound

        test <@ earned.Outcome = Verdict.Red @>
        test <@ earned.Divergence = Verdict.Divergence.CheckMissedFailures @>

        // And the mirror image, which is a different fact with a different cause (a stale
        // red, a flake, a test-isolation defect) and must not share a token with it. Without
        // this, a classifier that returned `CheckMissedFailures` for ANY disagreement would
        // pass the assertion above.
        let reversed =
            publishConfirm
                root
                (FullSuite 6)
                (Some(impactScopedReading root (ImpactFiltered(5, 6)) 3 Complete))
                CheckVerdict.CheckOutcome.Clean

        test <@ reversed.Divergence = Verdict.Divergence.CheckOnlyFailures @>

        match reversed.ImpactScopedRun with
        | Some pre -> test <@ pre.Outcome = Verdict.Red @>
        | None -> failwith "the impact-scoped reading must survive a disagreement")

[<Fact>]
let ``a confirm that did NOT escalate records the fact POSITIVELY — absence is never agreement`` () =
    withTempDir "verdict-259-noescalation" (fun root ->
        // THE requirement most easily got wrong. If a non-escalating confirm simply omitted
        // the record, "the run was already full-suite" and "nobody recorded anything" would
        // be the same bytes, and an analysis counting samples could not tell them apart.
        let earned = publishConfirm root (FullSuite 6) None CheckVerdict.CheckOutcome.Clean

        // ASSERTED ON THE VALUE, not on the absence of one.
        test <@ earned.Divergence = Verdict.Divergence.NoImpactScopedRun @>

        // ...and it is a DIFFERENT value from "nothing was recorded here", which is what a
        // pre-259 verdict reads as. Same field, two distinct facts, neither of them
        // agreement — see the round-trip and legacy-file tests below.
        test <@ earned.Divergence <> Verdict.Divergence.NotRecorded @>
        test <@ earned.Divergence <> Verdict.Divergence.Agreed @>
        test <@ earned.ImpactScopedRun = None @>

        // It reaches DISK as its own token, not as an omitted key.
        let json = Verdict.serialize earned
        test <@ json.Contains "\"no-impact-scoped-run\"" @>)

[<Fact>]
let ``an escalated run that never completed records COULD-NOT-COMPARE, never agreement`` () =
    withTempDir "verdict-259-incomparable" (fun root ->
        // The observed artifact from AUTOMATION-258: confirm escalated, the forced full run
        // died on compile errors, and the daemon's `test-scope` still answered with the
        // PRE-escalation coverage. There is no full-suite reading to compare with — and
        // "could not compare" collapsing into "agreed" is the same trap as an omitted
        // record, one field along.
        let stalled =
            publishConfirm
                root
                (ImpactFiltered(5, 6))
                (Some(impactScopedReading root (ImpactFiltered(5, 6)) 0 Complete))
                (CheckVerdict.CheckOutcome.UnearnedScope(ImpactFiltered(5, 6)))

        match stalled.Divergence with
        | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "escalated full-suite run" @>
        | other -> failwithf "an escalated run with no result must be INCOMPARABLE, got %A" other

        // The reading confirm escalated away from is STILL recorded — the sample survives
        // even though the comparison does not, which is the whole point of keeping the two
        // as separate fields.
        match stalled.ImpactScopedRun with
        | Some pre -> test <@ pre.Scope = ImpactFiltered(5, 6) @>
        | None -> failwith "the impact-scoped reading must survive an incomplete escalation"

        // AUTOMATION-258's rewrite still stands, and its prose no longer restates the counts
        // — they are typed, one nesting down, in the record above.
        test <@ not (TestScope.isFullSuite stalled.Scope) @>
        test <@ (TestScope.describe stalled.Scope).Contains "5/6" |> not @>
        test <@ (TestScope.describe stalled.Scope).Contains "impactScopedRun" @>

        // The other side of "no answer to compare": the ESCALATED run settled, but the
        // impact-scoped one had never reached a verdict of its own.
        let preNeverSettled =
            publishConfirm
                root
                (FullSuite 6)
                (Some(impactScopedReading root (ImpactFiltered(5, 6)) 0 (Incomplete 4)))
                CheckVerdict.CheckOutcome.Clean

        match preNeverSettled.Divergence with
        | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "impact-scoped run" @>
        | other -> failwithf "an unsettled impact-scoped reading must be INCOMPARABLE, got %A" other)

[<Fact>]
let ``every comparison round-trips through the verdict JSON`` () =
    withTempDir "verdict-259-roundtrip" (fun root ->
        let withRun (d: Verdict.Divergence) : Verdict.CheckComparison =
            { Divergence = d
              ImpactScoped =
                Some
                    { Scope = ImpactFiltered(5, 6)
                      Outcome = Verdict.Red
                      FailingSuites = [ "Lib.Tests"; "Api.Tests" ]
                      Basis = Verdict.SampleBasis.Executed }
              FailureRecall = None }

        let cases =
            [ { withRun Verdict.Divergence.Agreed with
                  FailureRecall = Some(FailureRecallMeasured(3, 4, 1.0, false)) }
              withRun Verdict.Divergence.CheckMissedFailures
              withRun Verdict.Divergence.CheckOnlyFailures
              withRun (Verdict.Divergence.Incomparable "the escalated full-suite run reached no verdict")
              { Divergence = Verdict.Divergence.NoImpactScopedRun
                ImpactScoped = None
                FailureRecall = None }
              Verdict.CheckComparison.notRecorded ]

        for comparison in cases do
            writeSpec
                root
                { greenVerdict "sha256:rt" 1 with
                    Comparison = comparison }

            match Verdict.read root with
            // Structural equality on the WHOLE value: scope, outcome and the suite names
            // all have to survive, not just the classification token.
            | Verdict.Reading.Found v -> test <@ v.Comparison = comparison @>
            | other -> failwithf "a verdict carrying %A must read back, got %A" comparison other)

[<Fact>]
let ``a verdict written before AUTOMATION-259 still reads — as NOT RECORDED, never as agreement`` () =
    withTempDir "verdict-259-legacy" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        // A verdict file that predates the field. It is not corrupt and it is not a schema
        // break (the field is additive), so it must READ — and the one thing it may never
        // read as is a comparison that happened.
        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","command":"confirm",
                "scope":{"kind":"full","ranProjects":6,"totalProjects":6},
                "outcome":{"kind":"green"},"exitCode":0,"plugins":[]}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            test <@ v.Divergence = Verdict.Divergence.NotRecorded @>
            test <@ v.ImpactScopedRun = None @>
            // The fast path still works on it: an old green is still a green, and 259 is
            // not allowed to invalidate evidence that was honestly earned.
            test <@ Verdict.isFullSuiteGreen v @>
        | other -> failwithf "a pre-259 verdict must still read, got %A" other

        // A classification from a LATER build is not "not recorded" either: something WAS
        // written here and this build cannot read it. Neither fact is agreement, and the
        // two must not share a value.
        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","command":"confirm",
                "scope":{"kind":"full","ranProjects":6,"totalProjects":6},
                "outcome":{"kind":"green"},"exitCode":0,"plugins":[],
                "checkComparison":{"divergence":{"kind":"agreed-modulo-flakes"}}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            match v.Divergence with
            | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "agreed-modulo-flakes" @>
            | other -> failwithf "an unknown classification must never be readable as agreement, got %A" other
        | other -> failwithf "an unknown classification must not make the verdict unreadable, got %A" other)

[<Fact>]
let ``a verdict cannot claim a comparison it did not make`` () =
    // The classification and the reading are ONE fact in two fields, so `validate` — the
    // choke point both `create` and `read` pass through — refuses the two pairs that state
    // it inconsistently. Without this, `{"divergence":"agreed"}` with no reading beside it
    // would count as a sample in an analysis that only reads the token.
    let spec = greenVerdict "sha256:abc" 1

    let reading: Verdict.ImpactScopedRun option =
        Some
            { Scope = ImpactFiltered(5, 6)
              Outcome = Verdict.Green
              FailingSuites = []
              Basis = Verdict.SampleBasis.Executed }

    // Claims a comparison, records nothing to have compared against.
    raises<ArgumentException>
        <@
            build
                { spec with
                    Comparison =
                        { Divergence = Verdict.Divergence.Agreed
                          ImpactScoped = None
                          FailureRecall = None } }
        @>

    // ...and the mirror: records a reading under a classification that says there was none.
    raises<ArgumentException>
        <@
            build
                { spec with
                    Comparison =
                        { Divergence = Verdict.Divergence.NoImpactScopedRun
                          ImpactScoped = reading
                          FailureRecall = None } }
        @>

    // `check` never escalates (`CheckVerdict.confirmNeedsFullRun` is false in `InnerLoop`),
    // so it never has a second reading and may not claim one. Confirm-only in the type.
    raises<ArgumentException>
        <@
            build
                { spec with
                    Command = Verdict.Check
                    Scope = ImpactFiltered(5, 6)
                    Comparison =
                        { Divergence = Verdict.Divergence.Agreed
                          ImpactScoped = reading
                          FailureRecall = None } }
        @>

    // CONTROLS. Without these, a `create` that had started refusing every comparison would
    // pass all three `raises` above.
    test
        <@
            (build
                { spec with
                    Comparison =
                        { Divergence = Verdict.Divergence.Agreed
                          ImpactScoped = reading
                          FailureRecall = None } })
                .Divergence = Verdict.Divergence.Agreed
        @>

    test
        <@
            (build
                { spec with
                    Comparison =
                        { Divergence = Verdict.Divergence.NoImpactScopedRun
                          ImpactScoped = None
                          FailureRecall = None } })
                .Divergence = Verdict.Divergence.NoImpactScopedRun
        @>

    // `Incomparable` is permissive in BOTH directions, and deliberately: confirm escalated
    // and the reading is there, or the classification came from a build this one cannot read
    // and it is not.
    for impactScoped in [ reading; None ] do
        let v =
            build
                { spec with
                    Comparison =
                        { Divergence = Verdict.Divergence.Incomparable "no full-suite result"
                          ImpactScoped = impactScoped
                          FailureRecall = None } }

        test <@ v.ImpactScopedRun = impactScoped @>

[<Fact>]
let ``an outcome this build cannot read is Unreadable, never a green`` () =
    withTempDir "verdict-badoutcome" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"chartreuse"}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "outcome" @>
        | other -> failwith $"an unknown outcome must never be a verdict, got %A{other}")

[<Fact>]
let ``an incomplete verdict with no recorded reason still reads as incomplete`` () =
    withTempDir "verdict-noreason" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"incomplete"},"exitCode":2}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            match v.Outcome with
            | Verdict.Incomplete reason -> test <@ reason.Contains "no reason recorded" @>
            | other -> failwith $"expected Incomplete, got %A{other}"
        | other -> failwith $"expected a readable verdict, got %A{other}")


[<Fact>]
let ``a verdict whose exitCode is missing defaults to 2 — 'unconfirmed', never 0`` () =
    withTempDir "verdict-noexit" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v -> test <@ v.ExitCode = 2 @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``the report envelope for a MISSING verdict says why, and never carries a verdict`` () =
    withTempDir "verdict-envelope-none" (fun root ->
        makeRepo root
        let envelope = Verdict.serializeReport (Verdict.report root [])

        use doc = JsonDocument.Parse(envelope)
        let root' = doc.RootElement
        let applies = root'.GetProperty("applies").GetBoolean()
        let reason = root'.GetProperty("reason").GetString()
        let hasVerdict = fst (root'.TryGetProperty("verdict"))

        test <@ not applies @>
        test <@ reason.Contains ".fshw/verdict.json" @>
        test <@ not hasVerdict @>)

[<Fact>]
let ``an unreadable verdict file reports NoVerdict — exit 5, never a green`` () =
    withTempDir "verdict-unreadable" (fun root ->
        makeRepo root
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore
        File.WriteAllText(Verdict.path root, "this is not JSON")

        match Verdict.report root [] with
        | Verdict.Report.NoVerdict reason ->
            test <@ reason.Contains "unusable" @>
            test <@ Verdict.reportExitCode (Verdict.Report.NoVerdict reason) = 5 @>
        | other -> failwith $"expected NoVerdict, got %A{other}")

[<Fact>]
let ``the wire tokens are stable — consumers key off them`` () =
    test <@ Verdict.Outcome.tag Verdict.Green = "green" @>
    test <@ Verdict.Outcome.tag Verdict.Red = "red" @>
    test <@ Verdict.Outcome.tag (Verdict.Incomplete "x") = "incomplete" @>
    test <@ Verdict.Command.token Verdict.Check = "check" @>
    test <@ Verdict.Command.token Verdict.Confirm = "confirm" @>
    test <@ Verdict.Command.ofCheckMode CheckVerdict.InnerLoop = Verdict.Check @>
    test <@ Verdict.Command.ofCheckMode CheckVerdict.Confirmation = Verdict.Confirm @>

    let tokens =
        [ Verdict.PluginOutcome.Ok
          Verdict.PluginOutcome.Warn
          Verdict.PluginOutcome.Fail
          Verdict.PluginOutcome.TimedOut
          Verdict.PluginOutcome.Running ]
        |> List.map Verdict.PluginOutcome.token

    test <@ tokens = [ "ok"; "warn"; "fail"; "timed-out"; "running" ] @>

// ---------------------------------------------------------------------------
// Plugin outcome resolution — every arm.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a running plugin is running; a timed-out one is timed-out, never merely failed`` () =
    let running =
        status (StatusView.Running DateTime.UtcNow) TimeSpan.Zero CompletedRun None

    let timedOut =
        status
            (StatusView.Failed("slow", DateTime.UtcNow))
            (TimeSpan.FromMinutes 60.0)
            (TimedOut "still running: test-prune (1h 0m)")
            None

    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow running = Some Verdict.PluginOutcome.Running @>
    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow timedOut = Some Verdict.PluginOutcome.TimedOut @>

[<Fact>]
let ``warnings are a WARN when they fail the build, and invisible when they do not`` () =
    let withWarnings =
        { status (StatusView.Completed DateTime.UtcNow) (TimeSpan.FromSeconds 1.0) CompletedRun None with
            Diagnostics = { Errors = 0; Warnings = 3 } }

    // Under the default warn-fail policy a warning denies the green.
    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow withWarnings = Some Verdict.PluginOutcome.Warn @>
    // Under `--no-warn-fail` it does not.
    test <@ Verdict.pluginOutcomeOf false DateTime.UtcNow withWarnings = Some Verdict.PluginOutcome.Ok @>

[<Fact>]
let ``an error-carrying plugin is a FAIL even when its run "completed"`` () =
    let withErrors =
        { status (StatusView.Completed DateTime.UtcNow) (TimeSpan.FromSeconds 1.0) CompletedRun None with
            Diagnostics = { Errors = 2; Warnings = 0 } }

    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow withErrors = Some Verdict.PluginOutcome.Fail @>

[<Fact>]
let ``an IDLE plugin is judged by the run it last completed`` () =
    // The cache-replay shape: the plugin is Idle but HAS a verdict from the replayed run.
    // Reading Idle as "nothing to say" drops a real failure out of the verdict entirely.
    let idleAfterFailure =
        status StatusView.Idle (TimeSpan.FromSeconds 2.0) (FailedRun "boom") None

    let idleAfterTimeout =
        status StatusView.Idle (TimeSpan.FromMinutes 60.0) (TimedOut "wedged") None

    let idleAfterPass =
        status StatusView.Idle (TimeSpan.FromSeconds 2.0) CompletedRun (Some "ok")

    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow idleAfterFailure = Some Verdict.PluginOutcome.Fail @>
    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow idleAfterTimeout = Some Verdict.PluginOutcome.TimedOut @>
    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow idleAfterPass = Some Verdict.PluginOutcome.Ok @>

// ---------------------------------------------------------------------------
// Tree hash — the arms that only a hostile filesystem reaches.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a repo with no .fshw.json still hashes — the config is optional, not assumed`` () =
    withTempDir "tree-noconfig" (fun root ->
        makeRepo root
        test <@ not (File.Exists(FsHwPaths.configFile root)) @>
        let tree = TreeHash.compute root []
        test <@ tree.FileCount = 3 @>

        // Adding the config CHANGES the hash — it is an input, and its absence is a
        // fact about the tree, not a blank to be ignored.
        File.WriteAllText(FsHwPaths.configFile root, "{}")
        test <@ (TreeHash.compute root []).Hash <> tree.Hash @>
        test <@ (TreeHash.compute root []).FileCount = 4 @>)

[<Fact>]
let ``an empty repo hashes to a stable, non-crashing value`` () =
    withTempDir "tree-empty" (fun root ->
        let tree = TreeHash.compute root []
        test <@ tree.FileCount = 0 @>
        test <@ tree.Hash = TreeHash.hashEntries [] @>)

[<Fact>]
let ``an UNREADABLE file fails the hash CLOSED — it never silently drops out of the tree`` () =
    // A file the daemon cannot read is a file we cannot claim to have verified, so it
    // hashes to a marker distinct from the same tree with the file readable and the verdict
    // reads STALE. Dropping it would fail OPEN: the hash would match, and the verdict would
    // apply to a tree containing a file nobody looked at.
    withTempDir "tree-unreadable" (fun root ->
        makeRepo root
        let readable = TreeHash.compute root []

        let secret = Path.Combine(root, "src", "Lib", "Secret.fs")
        File.WriteAllText(secret, "module Secret")

        let withFile = TreeHash.compute root []
        test <@ withFile.Hash <> readable.Hash @>

        // chmod 000 — readable only by root.
        File.SetUnixFileMode(secret, UnixFileMode.None)

        try
            let unreadable = TreeHash.compute root []
            // Still IN the tree (the count is unchanged) — but with a different hash.
            test <@ unreadable.FileCount = withFile.FileCount @>
            test <@ unreadable.Hash <> withFile.Hash @>
        finally
            File.SetUnixFileMode(secret, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``an UNREADABLE DIRECTORY fails the hash CLOSED — a hole is not an empty tree`` () =
    // AUTOMATION-164. `ContentHash` already made an unreadable FILE hash to a sentinel,
    // but the walker one level up deleted that file from the list before the sentinel
    // could be reached — the "skip the file" answer ContentHash's own header names as
    // failing OPEN. A directory we cannot enumerate is now an entry in its own right, so
    // the tree we could only partly see cannot hash like the tree we saw whole.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tree-unreadable-dir" (fun root ->
            makeRepo root

            let vault = Path.Combine(root, "src", "Lib", "Vault")
            Directory.CreateDirectory vault |> ignore
            File.WriteAllText(Path.Combine(vault, "Hidden.fs"), "module Hidden")

            let readable = TreeHash.compute root []
            test <@ readable.SkippedCount = 0 @>

            File.SetUnixFileMode(vault, UnixFileMode.None)

            try
                let blind = TreeHash.compute root []

                // The file inside it is gone from the count — we genuinely never saw it —
                // but the HOLE is counted and hashed, so the two trees are distinguishable
                // and a verdict earned over the readable one reads STALE against this.
                test <@ blind.FileCount = readable.FileCount - 1 @>
                test <@ blind.SkippedCount = 1 @>
                test <@ blind.Hash <> readable.Hash @>

                // POSITIVE CONTROL: restoring the permission restores the exact hash, so
                // the difference is the hole and not incidental churn (an mtime, a
                // reordering, a nondeterministic walk).
                File.SetUnixFileMode(
                    vault,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                test <@ (TreeHash.compute root []).Hash = readable.Hash @>
            finally
                File.SetUnixFileMode(
                    vault,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// ---------------------------------------------------------------------------
// CTRF — malformed reports are never counted as evidence.
// ---------------------------------------------------------------------------



[<Fact>]
let ``the status hint lists the LATEST RUN's reports — not a pile spanning many runs`` () =
    withTempDir "hint-status-many" (fun root ->
        makeRepo root

        // An older run whose reports are real — under the flat layout they were mixed into
        // the listing with no way to tell them apart.
        let older = Guid.NewGuid()
        writeReport root older "Alpha.Tests" 10 0 |> ignore
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root older, DateTime.UtcNow.AddMinutes(-30.0))

        let latest = Guid.NewGuid()
        writeReport root latest "Alpha.Tests" 12 0 |> ignore
        writeReport root latest "Beta.Tests" 20 0 |> ignore
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root latest, DateTime.UtcNow)

        let lines = ProgressRenderer.AgentHints.forStatus root
        let text = String.concat "\n" lines

        let latestDir = latest.ToString("N")
        let olderDir = older.ToString("N")
        test <@ text.Contains latestDir @>
        test <@ not (text.Contains olderDir) @>
        // One heading, aligned continuations.
        test <@ (lines |> List.filter (fun l -> l.Contains "suites")).Length = 1 @>)

[<Fact>]
let ``a confirm that ran the full suite is told nothing extra — the hint is a nudge, not noise`` () =
    let v =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Confirm
            Scope = FullSuite 6
            Suites =
                [ { Project = "A.Tests"
                    Ctrf = ".fshw/test-runs/A.Tests-0123456789abcdef0123456789abcdef.ctrf.json"
                    Total = 10
                    Passed = 10
                    Failed = 0
                    Skipped = 0 } ] }

    let lines = hintsFor v
    let text = String.concat "\n" lines

    test <@ text.Contains ".fshw/verdict.json" @>
    test <@ not (text.Contains "impact-scoped") @>
    test <@ not (text.Contains "did not establish") @>

[<Fact>]
let ``an UNKNOWN scope on a check is nudged toward confirm too — an unknown scope is not a full one`` () =
    let v =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Check
            Scope = ScopeUnknown }

    let text = hintsFor v |> String.concat "\n"
    test <@ text.Contains "did not establish a full-suite scope" @>
    test <@ text.Contains "fshw confirm" @>

// ---------------------------------------------------------------------------
// A MISSING NUMBER IS NOT ZERO.
//
// The reader used to default `elapsedMs` to 0L and every suite count to 0. `0` is a
// MEASUREMENT ("this ran instantaneously"); absence is the absence of one. And
// `total: 0, failed: 0` manufactured from missing fields reads as "this suite ran cleanly"
// — a vacuous green conjured out of a truncated file.
//
// These cases cannot arise from our own writer, which is why they need pinning: they arise
// from the files a verdict exists to SURVIVE — truncated, hand-edited, or older schema.
// ---------------------------------------------------------------------------

let private writeRaw (root: string) (json: string) =
    Directory.CreateDirectory(FsHwPaths.root root) |> ignore
    File.WriteAllText(Verdict.path root, json)

[<Fact>]
let ``a plugin with no elapsedMs is NOT a zero-length run — it is an unmeasured one`` () =
    withTempDir "verdict-noelapsed" (fun root ->
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "plugins":[{"name":"test-prune","outcome":"ok"}]}"""

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            // NOT `Some 0L`. The distinction is the whole point.
            test <@ Option.isNone v.Plugins.Head.ElapsedMs @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``a genuinely instantaneous run is still MEASURED — Some 0L, not None`` () =
    // The control. Without it, "absent => None" could be satisfied by a reader that
    // simply lost every elapsed value.
    withTempDir "verdict-zeroelapsed" (fun root ->
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "plugins":[{"name":"build","outcome":"ok","elapsedMs":0}]}"""

        match Verdict.read root with
        | Verdict.Reading.Found v -> test <@ v.Plugins.Head.ElapsedMs = Some 0L @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``a suite missing its counts makes the verdict UNREADABLE — never a clean zero-test pass`` () =
    withTempDir "verdict-nocounts" (fun root ->
        // The catastrophic misreading this prevents: `total: 0, failed: 0` invented
        // from thin air reads as "the suite ran and nothing failed".
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "suites":[{"project":"Lib.Tests","ctrf":".fshw/test-runs/x/Lib.Tests.ctrf.json"}]}"""

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason ->
            test <@ reason.Contains "Lib.Tests" @>
            test <@ reason.Contains "not a count of zero" @>
        | other -> failwith $"a suite with no counts must not be readable as evidence, got %A{other}")

[<Fact>]
let ``a suite missing ONE count is as unreadable as one missing all of them`` () =
    withTempDir "verdict-partialcounts" (fun root ->
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "suites":[{"project":"Lib.Tests","total":63,"passed":63,"skipped":0}]}"""

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "failed" @>
        | other -> failwith $"expected Unreadable, got %A{other}")

[<Fact>]
let ``a plugin entry with no name makes the verdict unreadable`` () =
    withTempDir "verdict-noname" (fun root ->
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "plugins":[{"outcome":"ok","elapsedMs":5}]}"""

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "name" @>
        | other -> failwith $"expected Unreadable, got %A{other}")

// ---------------------------------------------------------------------------
// The PRODUCER is content-addressed too.
//
// `treeHash` addresses the verdict's SUBJECT. Without the producer, an older, buggier fshw
// writes a verdict for an UNCHANGED tree, the treeHash matches, and the verdict reads as
// current — a hole in the middle of the provenance chain. (AUTOMATION-147 makes the same
// argument for the daemon handshake; this is its file-layer half.)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a verdict produced by a DIFFERENT binary does not apply — even with a matching tree`` () =
    withTempDir "verdict-producer" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        // Same tree, different fshw. `Verdict.create` STAMPS the producer from the running
        // process, so this verdict cannot be built here — the only way to have a foreign
        // artifact is for a foreign binary to have written the FILE.
        writeVerdictClaimingAnotherBinary root (greenVerdict tree.Hash tree.FileCount)

        match Verdict.report root [] with
        | Verdict.Report.Stale(v, reason) ->
            // The tree matches perfectly. That is precisely what makes this dangerous.
            test <@ v.TreeHash = tree.Hash @>
            test <@ reason.Contains "DIFFERENT fshw binary" @>
            test <@ Verdict.reportExitCode (Verdict.Report.Stale(v, reason)) = 4 @>
        | other -> failwith $"a verdict from another binary must not apply, got %A{other}")

[<Fact>]
let ``a verdict from THIS binary over THIS tree applies — the control`` () =
    withTempDir "verdict-producer-ok" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []
        writeSpec root (greenVerdict tree.Hash tree.FileCount)

        match Verdict.report root [] with
        | Verdict.Report.Applies _ -> ()
        | other -> failwith $"expected the verdict to apply, got %A{other}")

[<Fact>]
let ``a verdict with NO producer recorded does not apply — provenance unestablished`` () =
    withTempDir "verdict-noproducer" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeRaw
            root
            $$"""{"schema":"fshw-verdict-v1","treeHash":"{{tree.Hash}}","outcome":{"kind":"green"},"exitCode":0}"""

        match Verdict.report root [] with
        | Verdict.Report.Stale(_, reason) -> test <@ reason.Contains "DIFFERENT fshw binary" @>
        | other -> failwith $"a verdict that cannot say who made it must not apply, got %A{other}")

[<Fact>]
let ``two producers we could not read are NOT thereby the same producer`` () =
    let unknown: Verdict.Producer =
        { DaemonIdentity.Version = "unknown-version"
          DaemonIdentity.ContentHash = ContentHash.UnhashableContent }

    test <@ not (Verdict.Producer.same unknown unknown) @>

    let real: Verdict.Producer =
        { DaemonIdentity.Version = "1.0.0"
          DaemonIdentity.ContentHash = "abc123" }

    test <@ Verdict.Producer.same real real @>
    test <@ not (Verdict.Producer.same real unknown) @>

[<Fact>]
let ``the running fshw can identify itself`` () =
    let me = Verdict.Producer.current ()
    test <@ ContentHash.isReadable me.ContentHash @>
    test <@ Verdict.Producer.same me (Verdict.Producer.current ()) @>

// ---------------------------------------------------------------------------
// One hasher, one fail-closed policy.
// ---------------------------------------------------------------------------

[<Fact>]
let ``an unreadable file hashes to a sentinel that matches nothing real`` () =
    withTempDir "contenthash" (fun root ->
        let path = Path.Combine(root, "f.txt")
        File.WriteAllText(path, "hello")

        let readable = ContentHash.ofFile path
        test <@ ContentHash.isReadable readable @>
        test <@ readable = ContentHash.ofText "hello" @>

        File.SetUnixFileMode(path, UnixFileMode.None)

        try
            let unreadable = ContentHash.ofFile path
            test <@ not (ContentHash.isReadable unreadable) @>
            // The sentinel must not collide with the hash of the same file readable —
            // nor with the hash of an EMPTY file, which is the subtler failure.
            test <@ unreadable <> readable @>
            test <@ unreadable <> ContentHash.ofText "" @>
        finally
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

// ---------------------------------------------------------------------------
// Run-directory edges — the arms a hostile or empty filesystem reaches.
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty repo has no runs, and nothing throws`` () =
    withTempDir "ctrf-none-at-all" (fun root ->
        test <@ List.isEmpty (Ctrf.latestRunReports root) @>
        test <@ not (Ctrf.runExists root (Guid.NewGuid())) @>
        // Tidying a directory that does not exist is a no-op, not a crash.
        Ctrf.tidyRunsDir root Ctrf.RetainedRuns)

[<Fact>]
let ``latestRunReports picks the newest RUN, and is empty when that run ran nothing`` () =
    withTempDir "ctrf-latest" (fun root ->
        let older = Guid.NewGuid()
        writeReport root older "Lib.Tests" 10 0 |> ignore
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root older, DateTime.UtcNow.AddHours(-1.0))

        test <@ (Ctrf.latestRunReports root).Length = 1 @>

        // A newer run that executed and tested nothing. The latest run is now EMPTY —
        // and that is the honest answer, not "fall back to the older run's reports".
        let newer = Guid.NewGuid()
        emptyRun root newer
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root newer, DateTime.UtcNow)

        test <@ List.isEmpty (Ctrf.latestRunReports root) @>)

[<Fact>]
let ``the status hint reports an empty latest run as such, rather than borrowing an older one`` () =
    withTempDir "hint-empty-latest" (fun root ->
        makeRepo root
        let older = Guid.NewGuid()
        writeReport root older "Lib.Tests" 10 0 |> ignore
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root older, DateTime.UtcNow.AddHours(-1.0))

        let newer = Guid.NewGuid()
        emptyRun root newer
        Directory.SetLastWriteTimeUtc(Ctrf.runDir root newer, DateTime.UtcNow)

        let text = ProgressRenderer.AgentHints.forStatus root |> String.concat "\n"
        test <@ text.Contains "no test run has produced a report yet" @>)

// ---------------------------------------------------------------------------
// The report envelope, every arm.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the envelope carries the verdict when it applies, and the reason when it does not`` () =
    withTempDir "envelope-arms" (fun root ->
        makeRepo root

        let hasVerdict (envelope: string) =
            use doc = JsonDocument.Parse(envelope)
            fst (doc.RootElement.TryGetProperty("verdict"))

        let reason (envelope: string) =
            use doc = JsonDocument.Parse(envelope)
            doc.RootElement.GetProperty("reason").GetString()

        // NoVerdict: no verdict object at all — nothing for a reader to misuse.
        let none = Verdict.serializeReport (Verdict.report root [])
        test <@ not (hasVerdict none) @>

        // Stale: the verdict IS carried (you may want to see what it said) but the
        // envelope states plainly that it does not apply.
        let tree = TreeHash.compute root []
        writeSpec root (greenVerdict tree.Hash tree.FileCount)
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet x = 1\n")

        let stale = Verdict.serializeReport (Verdict.report root [])
        test <@ hasVerdict stale @>
        test <@ (reason stale).Contains "different tree" @>)

[<Fact>]
let ``every Report case has its own exit code, and none of them is a silent 0`` () =
    let spec = greenVerdict "sha256:x" 1
    let v = build spec
    test <@ Verdict.reportExitCode (Verdict.Report.Applies v) = 0 @>
    test <@ Verdict.reportExitCode (Verdict.Report.Stale(v, "because")) = 4 @>
    test <@ Verdict.reportExitCode (Verdict.Report.NoVerdict "because") = 5 @>

    // A red verdict that APPLIES still reports red — applicability is orthogonal to
    // the answer.
    let red =
        build
            { spec with
                Outcome = Verdict.Red
                ExitCode = 1
                RedCauses = [ structuralRedCause ] }

    test <@ Verdict.reportExitCode (Verdict.Report.Applies red) = 1 @>

[<Fact>]
let ``a verdict that says NO TESTS RAN is incomplete, and says so in words`` () =
    let outcome =
        Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun)

    match outcome with
    | Verdict.Incomplete reason ->
        test <@ reason.Contains "NO TESTS RAN" @>
        test <@ reason.Contains "not a pass" @>
    | other -> failwith $"expected Incomplete, got %A{other}"

[<Fact>]
let ``ContentHash.ofBytes and ofText agree, and differ on differing input`` () =
    test <@ ContentHash.ofText "a" = ContentHash.ofBytes (Text.Encoding.UTF8.GetBytes "a") @>
    test <@ ContentHash.ofText "a" <> ContentHash.ofText "b" @>
    test <@ ContentHash.isReadable (ContentHash.ofText "") @>

[<Fact>]
let ``an unreadable run directory yields no evidence — and does not throw`` () =
    withTempDir "ctrf-dir-perm" (fun root ->
        makeRepo root
        let runId = Guid.NewGuid()
        writeReport root runId "Lib.Tests" 10 0 |> ignore
        let dir = Ctrf.runDir root runId
        test <@ (Ctrf.reportsForRun root runId).Length = 1 @>

        File.SetUnixFileMode(dir, UnixFileMode.None)

        try
            // A directory we cannot enumerate produces NO evidence. It never produces
            // a zero-failure pass, and it never faults the caller.
            test <@ List.isEmpty (Ctrf.reportsForRun root runId) @>
            test <@ List.isEmpty (Ctrf.latestRunReports root) @>
            Ctrf.tidyRunsDir root Ctrf.RetainedRuns
        finally
            File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute))

[<Fact>]
let ``a bad entry ANYWHERE in plugins or suites makes the whole verdict unreadable`` () =
    // The fold must propagate the first Error rather than dropping the bad entry and
    // carrying on with a shorter, plausible-looking list.
    withTempDir "verdict-fold" (fun root ->
        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "plugins":[{"name":"build","outcome":"ok"},{"outcome":"ok"}]}"""

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "name" @>
        | other -> failwith $"expected Unreadable, got %A{other}"

        writeRaw
            root
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "suites":[{"project":"A","total":1,"passed":1,"failed":0,"skipped":0},
                          {"project":"B","total":1}]}"""

        match Verdict.read root with
        | Verdict.Reading.Unreadable reason -> test <@ reason.Contains "'B'" @>
        | other -> failwith $"expected Unreadable, got %A{other}")

// ---------------------------------------------------------------------------
// MERGE INTEGRATION (AUTOMATION-129 × 147 × 125)
//
// The two hard integration points. Each is a place where shipping a SECOND answer to an
// existing question would have been the easy thing to do.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the verdict's producer IS the daemon's BinaryIdentity — not a fourth binary hash`` () =
    // 147 hashes the binary to decide "restart the daemon?"; the verdict hashes it to
    // decide "does this claim apply?". ONE hash, one sentinel, two conclusions — a second
    // hasher with a second sentinel policy is what AUTOMATION-155 exists to stamp out.
    let fromVerdict: Verdict.Producer = Verdict.Producer.current ()
    let fromDaemon: DaemonIdentity.BinaryIdentity = DaemonIdentity.currentIdentity ()

    test <@ fromVerdict = fromDaemon @>
    test <@ ContentHash.isReadable fromVerdict.ContentHash @>

[<Fact>]
let ``the two hashers agree on the SENTINEL, and disagree on the CONCLUSION — deliberately`` () =
    // The same unhashable value reaches opposite (correct) answers, because the two
    // callers are asking different questions.
    let unhashable: DaemonIdentity.BinaryIdentity =
        { Version = "1.0.0"
          ContentHash = ContentHash.UnhashableContent }

    // 147 — "restart the daemon?" Two unhashable binaries MATCH: refusing would restart
    // the daemon on every command and thrash the warm FCS cache forever. Fail OPEN.
    test <@ DaemonIdentity.compareIdentity (Some unhashable) unhashable = DaemonIdentity.IdentityVerdict.Match @>

    // 129 — "does this claim apply?" The same pair does NOT match: a verdict whose
    // provenance we could not establish must never read as current. Fail CLOSED.
    test <@ not (Verdict.Producer.same unhashable unhashable) @>

[<Fact>]
let ``a WEDGED plugin is wedged in the verdict file too — never laundered into "running"`` () =
    // The status line and the verdict file are two renderings of ONE value, so the file
    // inherits 147's `Wedged` token for free — a consumer polling `.fshw/verdict.json` can
    // never read an 8h36m wedge as "still running, be patient".
    let now = DateTime.UtcNow

    let wedged =
        status (StatusView.Running(now - TimeSpan.FromHours 3.0)) TimeSpan.Zero CompletedRun None

    let healthy =
        status (StatusView.Running(now - TimeSpan.FromSeconds 5.0)) TimeSpan.Zero CompletedRun None

    test <@ Verdict.pluginOutcomeOf true now wedged = Some Verdict.PluginOutcome.Wedged @>
    test <@ Verdict.pluginOutcomeOf true now healthy = Some Verdict.PluginOutcome.Running @>
    test <@ Verdict.PluginOutcome.token Verdict.PluginOutcome.Wedged = "wedged" @>

    // ...and it survives the round-trip through the file.
    let vs = Verdict.pluginVerdicts true now (Map.ofList [ "test-prune", wedged ])
    test <@ vs.Head.Outcome = Verdict.PluginOutcome.Wedged @>

[<Fact>]
let ``a Completed plugin with NO run record is never OK in the verdict — 147's rule, inherited`` () =
    // The content-free ✓. 147 fixed it on the status line; because there is one
    // implementation, the verdict file cannot disagree.
    let noRecord =
        { Status = StatusView.Completed DateTime.UtcNow
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = ErrorLedger.DiagnosticCounts.empty }

    test <@ Verdict.pluginOutcomeOf true DateTime.UtcNow noRecord = Some Verdict.PluginOutcome.Warn @>

    // And its elapsed is NOT 0 — it is absent. A missing number is not zero.
    let vs =
        Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "build", noRecord ])

    test <@ Option.isNone vs.Head.ElapsedMs @>

[<Fact>]
let ``a "no tests ran" scope states NO counts rather than a fabricated zero`` () =
    // `totalProjects: 0` for a repo with six test projects is a fabricated number, and this
    // file's own rule is that a missing number is not zero. `kind: "none"` carries the fact;
    // the counts are simply absent.
    let json =
        serializeSpec
            { greenVerdict "sha256:abc" 1 with
                Scope = NoTestsRun }

    use doc = JsonDocument.Parse(json)
    let scope = doc.RootElement.GetProperty("scope")
    let kind = scope.GetProperty("kind").GetString()
    let hasTotal = fst (scope.TryGetProperty("totalProjects"))
    let hasRan = fst (scope.TryGetProperty("ranProjects"))

    test <@ kind = "none" @>
    test <@ not hasTotal @>
    test <@ not hasRan @>

    // ...and it is still never green.
    test
        <@
            Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun)
            <> Verdict.Green
        @>

// ---------------------------------------------------------------------------
// AUTOMATION-161 — `confirm` HONOURS a verdict it has already earned.
//
// `confirm` is the pre-merge verb, so it gets run more than once, and on a tree that has
// not moved the answer was settled the first time. It used to refuse anyway (exit 3, "NO
// TESTS RAN") — a false NEGATIVE: not a green without evidence, but a refusal despite it.
//
// The fast path runs through the verdict file, content-addressed to its SUBJECT
// (`treeHash`) and its PRODUCER. Both must match and the verdict must be a FULL-SUITE
// GREEN; everything else is `MustEarn`, and these tests pin each road to it.
// ---------------------------------------------------------------------------

[<Fact>]
let ``AUTOMATION-161: a full-suite green over THIS tree, from THIS binary, still applies`` () =
    withTempDir "confirm-still-applies" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        let runId = Guid.NewGuid()
        writeReport root runId "Lib.Tests" 1965 0 |> ignore

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                RunId = Some runId
                Scope = FullSuite 1
                Suites =
                    [ { Project = "Lib.Tests"
                        Ctrf = ".fshw/test-runs/x/Lib.Tests.ctrf.json"
                        Total = 1965
                        Passed = 1965
                        Failed = 0
                        Skipped = 0 } ] }

        match Verdict.priorConfirmation root [] with
        | Verdict.PriorConfirmation.StillApplies v ->
            test <@ Verdict.isFullSuiteGreen v @>
            // It must NAME its evidence — a green a reader cannot audit is one they have to
            // take on trust.
            let described = Verdict.describeStillApplies v
            test <@ described.Contains "still applies" @>
            test <@ described.Contains "treeHash + producer match" @>
            test <@ described.Contains "full suite" @>
            test <@ described.Contains "1965 passed" @>
        | Verdict.PriorConfirmation.MustEarn -> failwith "a full-suite green over this very tree must still apply")

[<Fact>]
let ``AUTOMATION-161: a CHANGED tree is never satisfied by the stale verdict`` () =
    // The converse: edit a byte and the green is no longer an answer, so `confirm` must go
    // and earn a new one.
    withTempDir "confirm-tree-moved" (fun root ->
        makeRepo root
        let before = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict before.Hash before.FileCount with
                Scope = FullSuite 1 }

        // Applies — until the tree moves.
        test <@ Verdict.priorConfirmation root [] <> Verdict.PriorConfirmation.MustEarn @>

        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 43\n")

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: a verdict from a DIFFERENT fshw binary is never satisfied`` () =
    // A stale daemon's green about an unchanged tree is still a stale daemon's green.
    // The tree matches perfectly here; the producer does not.
    withTempDir "confirm-other-binary" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeVerdictClaimingAnotherBinary
            root
            { greenVerdict tree.Hash tree.FileCount with
                Scope = FullSuite 1 }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: an impact-filtered green is NOT the claim confirm makes`` () =
    // The UnearnedScope rule, on the fast path. "Your change didn't break anything I
    // chose to look at" is not "the suite is green", however current the tree is.
    //
    // The subject is a CHECK verdict, and that is the whole point (AUTOMATION-258): a
    // filtered green is what the inner loop legitimately writes, so this is the artifact
    // that really turns up on disk and really has to be refused. `confirm` cannot even
    // produce one — `CheckVerdict.verdict` routes `Confirmation, ImpactFiltered` to
    // `UnearnedScope`, and `Verdict.create` now refuses the pair outright. `isFullSuiteGreen`
    // stays deliberately blind to the command, so it is THIS record its scope check guards.
    withTempDir "confirm-filtered" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Command = Verdict.Check
                Scope = ImpactFiltered(1, 4) }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: NoTestsRun is an absence of evidence, and stays a refusal`` () =
    // AUTOMATION-161 fixed a replayed full-suite pass being MISREPORTED as `NoTestsRun`;
    // the rule itself does not move: nothing ran ⇒ nothing was verified ⇒ never a green,
    // and never a shortcut past the run either.
    withTempDir "confirm-no-tests" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Scope = NoTestsRun
                Outcome = Verdict.Incomplete "NO TESTS RAN"
                ExitCode = 3 }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: a RED verdict does not short-circuit — confirm re-runs and reports it`` () =
    withTempDir "confirm-red" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Scope = FullSuite 1
                Outcome = Verdict.Red
                ExitCode = 1
                RedCauses = [ structuralRedCause ] }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: no verdict on disk means earn one — a fresh checkout is not green`` () =
    withTempDir "confirm-no-verdict" (fun root ->
        makeRepo root
        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

// ---------------------------------------------------------------------------
// A RED run whose test table is all green. The failure lives in a plugin, and the agent
// hint block used to omit plugins entirely — so the reader saw six passing projects on a
// failing run and concluded the red belonged to someone else.
// ---------------------------------------------------------------------------

let private allSuitesGreen: Verdict.SuiteVerdict list =
    [ { Project = "Intelligence.Tests.Unit"
        Ctrf = ".fshw/test-runs/x/Intelligence.Tests.Unit.ctrf.json"
        Total = 5559
        Passed = 5559
        Failed = 0
        Skipped = 0 }
      { Project = "Intelligence.Tests.Integration"
        Ctrf = ".fshw/test-runs/x/Intelligence.Tests.Integration.ctrf.json"
        Total = 575
        Passed = 575
        Failed = 0
        Skipped = 0 } ]

[<Fact>]
let ``a red run with every suite green NAMES the failing plugin — the test table alone reads as a pass`` () =
    let hints =
        hintsFor
            { greenVerdict "deadbeef" 10 with
                Outcome = Verdict.Red
                ExitCode = 1
                Plugins =
                    [ { Name = "analyzers"
                        Outcome = Verdict.PluginOutcome.Fail
                        ElapsedMs = Some 13L
                        Summary = Some "analyzed 1164 files, 3 findings (3 errors, 0 warnings)" } ]
                Suites = allSuitesGreen }

    let joined = String.Join("\n", hints)

    test <@ joined.Contains "analyzers" @>
    test <@ joined.Contains "FAILING" @>
    // The summary carries the actionable detail — a bare plugin name still makes the reader
    // go digging.
    test <@ joined.Contains "3 findings" @>

[<Fact>]
let ``the failing-plugin hint is NOT a blanket — a green run names no plugin as failing`` () =
    // Positive control: without it, a change printing "FAILING" unconditionally would
    // satisfy the test above while telling every reader their green run failed.
    let hints =
        hintsFor
            { greenVerdict "deadbeef" 10 with
                Suites = allSuitesGreen }

    let joined = String.Join("\n", hints)

    test <@ not (joined.Contains "FAILING") @>
    test <@ not (joined.Contains "UNEXPLAINED") @>

[<Fact>]
let ``AUTOMATION-357: Verdict.create refuses a red with no failing plugin and no structural cause`` () =
    let construct () =
        build
            { greenVerdict "deadbeef" 10 with
                Outcome = Verdict.Red
                ExitCode = 1
                Plugins = []
                Suites = allSuitesGreen
                RedCauses = [] }

    raises<ArgumentException> <@ construct () @>

// ---------------------------------------------------------------------------
// AUTOMATION-303 case 3 — the verdict must explain its own exit code
// ---------------------------------------------------------------------------
//
// 2026-08-12: `fshw confirm` exited 1 with all four plugins `ok` and 9,064 passed /
// 0 failed. The red was real — ~51 FCS diagnostics in the ledger — but FCS is not a
// plugin: the daemon reports its diagnostics under the pseudo-source `fcs`, which has
// no `PluginStatus` and so no line in `plugins[]`. Every field of the verdict said
// "fine" while its own `exitCode` said "broken", and the only way to find out which
// was true was to read the daemon log.

let private fcsCause (message: string) : Verdict.RedCause =
    { Source = "fcs"
      File = "src/Lib/Thing.fs"
      Severity = "error"
      Message = message
      // Classified by PRODUCTION, not stamped: a fixture that hand-picked the kind
      // would keep passing if `classify` stopped working, and these causes are the
      // exact shape (`fcs` + `internal error:`) the classifier exists to recognise.
      Kind = Verdict.RedCause.classify "fcs" "src/Lib/Thing.fs" message }

[<Fact>]
let ``AUTOMATION-303: a red with every plugin ok NAMES the diagnostics that reddened it`` () =
    withTempDir "verdict-red-causes" (fun root ->
        let spec =
            { greenVerdict "deadbeef" 10 with
                Outcome = Verdict.Red
                ExitCode = 1
                Suites = allSuitesGreen
                RedCauses = [ fcsCause "internal error: Object reference not set to an instance of an object." ] }

        let json = serializeSpec spec
        test <@ json.Contains "reddenedBy" @>
        test <@ json.Contains "internal error: Object reference not set" @>
        test <@ json.Contains "\"source\": \"fcs\"" @>

        // It ROUND-TRIPS: a consumer reading the file back gets the causes, not just a
        // string it has to grep.
        writeSpec root spec

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            test <@ v.RedCauseCount = 1 @>
            test <@ v.RedCauses |> List.exists (fun c -> c.Source = "fcs") @>
        | other -> failwithf "the verdict must read back, got %A" other

        // The steering block names it too, and therefore no longer calls it unexplained.
        let joined = String.Join("\n", hintsFor spec)
        test <@ joined.Contains "REDDENED" @>
        test <@ joined.Contains "fcs" @>
        test <@ not (joined.Contains "UNEXPLAINED") @>)

[<Fact>]
let ``AUTOMATION-357: a persisted unexplained red degrades to incomplete`` () =
    withTempDir "verdict-unexplained-red" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"red"},
                "exitCode":1,"plugins":[],"reddenedBy":[],"reddenedByCount":0}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found verdict ->
            match verdict.Outcome with
            | Verdict.Incomplete reason -> test <@ reason.Contains "no failing plugin" @>
            | other -> failwithf "an unexplained persisted red must degrade to INCOMPLETE, got %A" other

            test <@ verdict.ExitCode = 2 @>
        | other -> failwithf "an unexplained persisted red must remain a fail-closed reading, got %A" other)

[<Fact>]
let ``AUTOMATION-303: a flood of causes is truncated but its true count is not`` () =
    // A cross-file FCS fault arrives in the dozens (51 of them, that day). The file
    // records a readable sample and the REAL total — `redCauses.Length` answers "how
    // many are printed", which is a different question and the one nobody asked.
    let many = [ for i in 1..25 -> fcsCause $"diagnostic %d{i}" ]

    let spec =
        { greenVerdict "deadbeef" 10 with
            Outcome = Verdict.Red
            ExitCode = 1
            Suites = allSuitesGreen
            RedCauses = many }

    let v = build spec

    test <@ List.length v.RedCauses = Verdict.MaxRedCauses @>
    test <@ v.RedCauseCount = 25 @>

    let joined = String.Join("\n", hintsFor spec)
    test <@ joined.Contains "and 15 more" @>

[<Fact>]
let ``AUTOMATION-303: a GREEN verdict names no causes`` () =
    // The other direction: `reddenedBy` is not a place to accumulate noise. A green run
    // reddened by nothing says exactly that.
    let v =
        build
            { greenVerdict "deadbeef" 10 with
                Suites = allSuitesGreen }

    test <@ List.isEmpty v.RedCauses @>
    test <@ v.RedCauseCount = 0 @>

// ---------------------------------------------------------------------------
// AUTOMATION-303 (QA rework) — AC5. IS THIS RED A CLAIM ABOUT THIS TREE?
//
// The landed fix made a red NAME its cause. It did not make the red EARNED: two of the
// four incidents in the ticket were reds that no longer described the tree they were
// reported against, and the one thing that cleared them — `fshw stop` — is the one thing
// the output never said. AC5 asks for exactly that sentence.
//
// The classifier may only ever move a cause OUT of "your code is broken", so every test
// below is paired with the control that keeps a real red red.
// ---------------------------------------------------------------------------

/// A filesystem oracle that says only these paths exist. Injected rather than probed so
/// the "the file is gone" branch is pinned without deleting anything — and, far more
/// importantly, so its POSITIVE CONTROL (a present file still reddens) is expressible at
/// all.
let private existsOnly (present: string list) : string -> bool =
    fun p -> present |> List.exists (fun q -> String.Equals(q, p, StringComparison.Ordinal))

[<Fact>]
let ``AUTOMATION-303 AC5: an FCS internal error is a CHECKER FAULT, not a finding`` () =
    // Case 3, in one line. ~51 of these reddened a `confirm` with four plugins `ok` and
    // 9,064 tests passed, against code the session had not touched. An `internal error:`
    // is the checker reporting its OWN crash — the check did not complete, so nothing was
    // found, so there is nothing here to fix in the tree.
    let kind =
        Verdict.RedCause.classifyWith
            (existsOnly [])
            "fcs"
            "/repo/src/Thing.fs"
            "internal error: Object reference not set to an instance of an object."

    test <@ kind = Verdict.CheckerFault @>

[<Fact>]
let ``AUTOMATION-303 AC5: an ORDINARY fcs error against a present file stays a red`` () =
    // THE POSITIVE CONTROL, and the one that matters most: case 2 was a REAL compile
    // error arriving on the same `fcs` channel, and demoting it is how the gate ships a
    // non-compiling tree. Same source, same file, ordinary message — still a claim.
    let kind =
        Verdict.RedCause.classifyWith
            (existsOnly [ "/repo/src/Thing.fs" ])
            "fcs"
            "/repo/src/Thing.fs"
            "The value, namespace, type or module 'TextLimits' is not defined."

    test <@ kind = Verdict.AboutThisTree @>

[<Fact>]
let ``AUTOMATION-303 AC5: a diagnostic against a file that is GONE is not about this tree`` () =
    // Case 4's shape, generalised past the one map `pruneDeletedUnanalyzable` fixed. A
    // diagnostic pinned to an absolute path that is not on disk describes a tree that no
    // longer exists — it cannot be a claim about this one, whatever it says.
    let kind =
        Verdict.RedCause.classifyWith
            (existsOnly [ "/repo/src/StillHere.fs" ])
            "test-prune"
            "/repo/src/Deleted.fs"
            "symbol analysis failed — Parse errors: Files in libraries must begin with a namespace or module declaration"

    test <@ kind = Verdict.VanishedFile @>

    // THE POSITIVE CONTROL, from the same oracle in the same call: the file that IS
    // there, carrying the identical message, is still reported.
    let present =
        Verdict.RedCause.classifyWith
            (existsOnly [ "/repo/src/StillHere.fs" ])
            "test-prune"
            "/repo/src/StillHere.fs"
            "symbol analysis failed — Parse errors: Files in libraries must begin with a namespace or module declaration"

    test <@ present = Verdict.AboutThisTree @>

[<Fact>]
let ``AUTOMATION-303 AC5: a synthetic or relative ledger key is never demoted`` () =
    // The ledger's file key is whatever the reporting source passed. `BuildPlugin` passes
    // the literal `<build>`, `CoveragePlugin` passes a Cobertura filename — neither is a
    // path on disk, and neither proves anything about a tree. Treating "not found" as
    // proof there would silently demote every build failure in the repo to NO VERDICT.
    let synthetic =
        Verdict.RedCause.classifyWith (existsOnly []) "build" "<build>" "Build FAILED. 3 Error(s)"

    let relative =
        Verdict.RedCause.classifyWith (existsOnly []) "coverage" "src/Lib/Thing.fs" "coverage: below threshold"

    test <@ synthetic = Verdict.AboutThisTree @>
    test <@ relative = Verdict.AboutThisTree @>

    // THE CONTROL for those two absences: the same oracle, an absolute missing path, DOES
    // classify — so the equalities above are about the KEY SHAPE and not about a
    // classifier that never fires.
    test <@ Verdict.RedCause.classifyWith (existsOnly []) "fcs" "/repo/Gone.fs" "boom" = Verdict.VanishedFile @>

[<Fact>]
let ``AUTOMATION-303 AC5: an internal-error message from a PLUGIN is not a checker fault`` () =
    // Scoped to the checker's own channel. A plugin that quotes the phrase — a test whose
    // assertion message contains it, say — is not the compiler crashing, and a red that
    // any source could disown by wording is not a red.
    let kind =
        Verdict.RedCause.classifyWith
            (existsOnly [ "/repo/src/Thing.fs" ])
            "test-prune"
            "/repo/src/Thing.fs"
            "internal error: Object reference not set to an instance of an object."

    test <@ kind = Verdict.AboutThisTree @>

[<Fact>]
let ``AUTOMATION-303 AC5: the verdict file records each cause's kind`` () =
    // Per cause, not summarised: the interesting file is the MIXED one, and a single flag
    // would put the reader back to guessing which line was which.
    let spec =
        { greenVerdict "deadbeef" 10 with
            Outcome = Verdict.Red
            ExitCode = 1
            Suites = allSuitesGreen
            RedCauses =
                [ fcsCause "internal error: Object reference not set to an instance of an object."
                  fcsCause "The value or constructor 'foo' is not defined." ] }

    let json = serializeSpec spec
    test <@ json.Contains "checker-fault" @>
    test <@ json.Contains "about-this-tree" @>

[<Fact>]
let ``AUTOMATION-303 AC5: the steering block names fshw stop for a cause that is not this tree`` () =
    // THE DELIVERABLE. `fshw scan` was the documented remedy for this class and never
    // cleared it once; `fshw stop` did. The sentence that saves the cycle is the one
    // naming which remedy does NOT work, and it belongs where the reader is looking.
    let joined =
        String.Join(
            "\n",
            hintsFor
                { greenVerdict "deadbeef" 10 with
                    Outcome = Verdict.Red
                    ExitCode = 1
                    Suites = allSuitesGreen
                    RedCauses = [ fcsCause "internal error: Object reference not set to an instance of an object." ] }
        )

    test <@ joined.Contains "NOT-THIS-TREE" @>
    test <@ joined.Contains "fshw stop" @>
    test <@ joined.Contains "`fshw scan` does NOT clear it" @>

[<Fact>]
let ``AUTOMATION-303 AC5: an ordinary red says none of that`` () =
    // THE POSITIVE CONTROL for the three assertions above. A red that IS about this tree
    // must not be decorated with a stale-state remedy — an agent told to restart the
    // daemon over a genuine compile error loses the same cycle in the other direction,
    // which is the pair of mistakes the ticket was opened on.
    let joined =
        String.Join(
            "\n",
            hintsFor
                { greenVerdict "deadbeef" 10 with
                    Outcome = Verdict.Red
                    ExitCode = 1
                    Suites = allSuitesGreen
                    RedCauses = [ fcsCause "The value or constructor 'foo' is not defined." ] }
        )

    // Still named — the landed fix's guarantee is untouched.
    test <@ joined.Contains "REDDENED" @>
    test <@ not (joined.Contains "NOT-THIS-TREE") @>
    test <@ not (joined.Contains "fshw stop") @>

[<Fact>]
let ``AUTOMATION-303 AC5: the stale-state outcome is INCOMPLETE and names the remedy`` () =
    // Never `red`: the structured outcome is what a deploy preflight reads, and "the
    // daemon is stale" must route to retry-after-stop, not to "tests failed".
    match Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.StaleDaemonState 51) with
    | Verdict.Incomplete reason ->
        test <@ reason.Contains "51" @>
        test <@ reason.Contains "fshw stop" @>
        test <@ reason.Contains "does NOT" @>
    | other -> failwithf "stale daemon state must be INCOMPLETE, got %A" other

    // THE CONTROL: the same function still calls a real failure a red.
    test <@ Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.FailuresFound = Verdict.Red @>

// ---------------------------------------------------------------------------
// AUTOMATION-201 — the message an operator reads LAST, and acts on.
// ---------------------------------------------------------------------------

/// AC2: "fails with a message that names every affected project (no truncation) AND
/// states the concrete remedy." This is the top-level message — the one the terminal
/// prints as the verdict and the one `.fshw/verdict.json` carries. Every project has to
/// survive it whole, however many there are: the reported symptom was a headline cut
/// mid-name (`… Intelligence.Build.Dev.Tests, Intelli...`).
[<Fact>]
let ``AUTOMATION-201: the stale-output message names EVERY affected project, untruncated`` () =
    // Long, realistic project names, and more of them than the old 80-character budget
    // could have held even one of.
    let deferrals =
        [ for i in 1..6 ->
              $"Intelligence.Build.Dev.Tests.Number%d{i}: waiting on build — stale build output — \
                /repo/src/Intelligence.Build.Ops/bin/Debug/net10.0/Intelligence.Build.Ops.dll has not been copied \
                into the test output since it changed. Remedy: run `dotnet build`" ]

    let message = Verdict.CheckProse.staleBuildOutput deferrals

    for d in deferrals do
        test <@ message.Contains d @>

    // Not merely "contains" — nothing was dropped or abbreviated on the way in.
    test <@ message.Contains "6 test project(s)" @>
    test <@ not (message.Contains "...") @>
    test <@ not (message.Contains "more)") @>

/// The other half of AC2, and the ticket's third defect: the message must PRESCRIBE.
/// It must also rule out the remedies that cannot work — the pattern AUTOMATION-303 set
/// when its stale-state outcome had to say that `fshw scan` does not clear it.
[<Fact>]
let ``AUTOMATION-201: the stale-output message states the remedy and rules out the ones that cannot work`` () =
    let message =
        Verdict.CheckProse.staleBuildOutput [ "P: waiting on build — stale build output — /a.dll" ]

    // What to run.
    test <@ message.Contains "dotnet build" @>
    test <@ message.Contains "--no-incremental" @>

    // What NOT to bother with. Re-running is the natural wrong move and it is the one
    // this ticket exists to stop; `fshw confirm` and a daemon restart are the two the
    // generic "waiting on build" prose used to recommend for this cause.
    test <@ message.Contains "Re-running the gate does NOT clear this" @>
    test <@ message.Contains "fshw confirm" @>
    test <@ message.ToLowerInvariant().Contains "restarting the daemon" @>

    // And it must not repeat the claim that was FALSE here: the artifact WAS produced.
    test <@ not (message.Contains "was not produced") @>

/// POSITIVE CONTROL. The build-ordering defer keeps its own words — "re-run once the
/// build settles" is right for it, and a change that gave every defer the stale-output
/// message would be a regression wearing a fix's clothes.
[<Fact>]
let ``AUTOMATION-201: a build-ordering defer keeps the words that are true for IT`` () =
    match Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.WaitingOnBuild []) with
    | Verdict.Incomplete reason ->
        test <@ reason.Contains "was not produced" @>
        test <@ reason.Contains "re-run once the build settles" @>
        test <@ not (reason.Contains "--no-incremental") @>
    | other -> failwith $"a build-ordering defer must stay incomplete, got %A{other}"

/// The verdict FILE carries the stale-output reason, not only the terminal. The reader
/// that would otherwise retry forever — a deploy preflight, an autonomous loop — makes
/// its decision from this field and never sees the terminal at all.
[<Fact>]
let ``AUTOMATION-201: the verdict file's reason carries the stale-output remedy`` () =
    let deferral = "P: waiting on build — stale build output — /a.dll differs"

    match Verdict.outcomeOfCheck (CheckVerdict.CheckOutcome.WaitingOnBuild [ deferral ]) with
    | Verdict.Incomplete reason ->
        test <@ reason.Contains deferral @>
        test <@ reason.Contains "dotnet build" @>
    | other -> failwith $"a stale-output defer must stay incomplete, never red/green, got %A{other}"

// ---------------------------------------------------------------------------
// The failure summary must not sit under a header that reads as "skip me".
//
// Every line these tests are about was ALREADY being printed. The block was headed
// `AGENTS: don't parse this output.` — meant as "don't screen-scrape, read the JSON",
// read as "this section is not for you". A reader obeyed it, skipped the only place
// the failures are enumerated, and misdiagnosed one failing run twice (first "check is
// flaky", then "the selector is blind") while the exact file, the exact numbers and
// the next command were on screen. Ordering and wording are the whole fix; the
// machine-readable pointer is untouched and still authoritative.
// ---------------------------------------------------------------------------

/// A red `check` in the shape that caused the misdiagnosis: two failing plugins, a
/// ledger cause naming the real file and numbers, and no test run at all.
let private redCheckWithCauses (pluginSummary: string) =
    { greenVerdict "deadbeef" 10 with
        Command = Verdict.Check
        Scope = NoTestsRun
        Outcome = Verdict.Red
        ExitCode = 1
        Suites = []
        Plugins =
            [ { Name = "coverage-count-gate"
                Outcome = Verdict.PluginOutcome.Fail
                ElapsedMs = Some 12L
                Summary = Some pluginSummary } ]
        RedCauses = [ fcsCause "coverage count gate: FAILED — 1 file(s) below floor" ] }

[<Fact>]
let ``the causes come BEFORE the AGENTS pointer, never underneath it`` () =
    let joined =
        String.Join("\n", hintsFor (redCheckWithCauses "coverage-count-gate: failed (cached)"))

    let causes = joined.IndexOf("FAILING", StringComparison.Ordinal)
    let reddened = joined.IndexOf("REDDENED", StringComparison.Ordinal)
    let noTests = joined.IndexOf("NO TEST RUN", StringComparison.Ordinal)
    let agents = joined.IndexOf("AGENTS:", StringComparison.Ordinal)

    test <@ causes >= 0 && reddened >= 0 && noTests >= 0 && agents >= 0 @>
    test <@ causes < agents @>
    test <@ reddened < agents @>
    // "no tests ran" is a FACT about what was verified, not a path — so it belongs
    // above the pointer too. The CTRF paths, which are machine fodder, do not.
    test <@ noTests < agents @>

[<Fact>]
let ``the causes are introduced by a header that tells the reader to READ them`` () =
    let joined =
        String.Join("\n", hintsFor (redCheckWithCauses "coverage-count-gate: failed (cached)"))

    test <@ joined.Contains "WHAT FAILED" @>
    // The wording that caused the skip must be gone from every surface, not softened.
    test <@ not (joined.Contains "don't parse this output") @>
    test <@ joined.Contains "READ the above" @>
    test <@ joined.Contains "SCREEN-SCRAPE" @>
    // The machine-readable pointer is LOAD-BEARING and stays, with its staleness rule.
    test <@ joined.Contains ".fshw/verdict.json" @>
    test <@ joined.Contains "exit 4 = stale" @>

/// POSITIVE CONTROL for the header. A run with nothing failing must not be told what
/// failed — a header printed unconditionally would satisfy the test above and lie to
/// every green reader.
[<Fact>]
let ``a run with no causes prints no WHAT FAILED header`` () =
    let joined =
        String.Join(
            "\n",
            hintsFor
                { greenVerdict "deadbeef" 10 with
                    Suites = allSuitesGreen }
        )

    test <@ not (joined.Contains "WHAT FAILED") @>
    test <@ joined.Contains "AGENTS: READ the above" @>

[<Fact>]
let ``a failing plugin's cause is printed IN FULL — the 80-column cap belongs to the status line, not here`` () =
    // The line that cost the diagnosis ended `… (+20 more)` with the answer inside the
    // omitted part. `truncateTo80` is right for the redrawn fixed-width surface; it may
    // never be the ONLY copy.
    let long =
        "0 passed, 0 failed in 0 projects (selected: no) — "
        + String.Join(", ", [ for i in 1..20 -> $"Intelligence.Tests.Project%d{i}" ])

    test <@ long.Length > 200 @>

    let joined = String.Join("\n", hintsFor (redCheckWithCauses long))

    test <@ joined.Contains long @>
    test <@ joined.Contains "Intelligence.Tests.Project20" @>
    // The marker `truncateTo80` would have left behind.
    test <@ not (joined.Contains " more)") @>

[<Fact>]
let ``a failing plugin whose run set no summary still names its reason — never "(no summary)"`` () =
    // The upstream half of the same defect. `pluginVerdicts` took ONLY `LastRun.Summary`,
    // so a plugin that failed with an error and no summary reached the verdict as
    // `Summary = None` and printed `(no summary)` — while the error text existed and was
    // visible only on the 80-column `✗` line, truncated.
    let reason =
        "the test host exited with code 134 before writing a report; last output line: "
        + String.Join(" | ", [ for i in 1..15 -> $"frame%d{i}" ])

    let failed =
        status (StatusView.Failed(reason, DateTime.UtcNow)) (TimeSpan.FromSeconds 3.0) (Events.FailedRun reason) None

    match Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "test-prune", failed ]) with
    | [ p ] ->
        test <@ p.Outcome = Verdict.PluginOutcome.Fail @>
        test <@ p.Summary = Some reason @>
    | other -> failwith $"expected one plugin verdict, got %A{other}"

/// POSITIVE CONTROL for the fallback: a run that COMPLETED with no summary has no
/// reason to invent one, and must still report `None`.
[<Fact>]
let ``a completed run with no summary is still summary-less — the fallback is for FAILURES`` () =
    let completed =
        status (StatusView.Completed DateTime.UtcNow) (TimeSpan.FromSeconds 3.0) Events.CompletedRun None

    match Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "lint", completed ]) with
    | [ p ] -> test <@ p.Summary = None @>
    | other -> failwith $"expected one plugin verdict, got %A{other}"

[<Fact>]
let ``a timed-out plugin's reason reaches the verdict too`` () =
    let reason = "no output for 900s while running Intelligence.Tests.Integration"

    let timedOut =
        status (StatusView.Failed(reason, DateTime.UtcNow)) (TimeSpan.FromSeconds 900.0) (Events.TimedOut reason) None

    match Verdict.pluginVerdicts true DateTime.UtcNow (Map.ofList [ "test-prune", timedOut ]) with
    | [ p ] -> test <@ p.Summary = Some reason @>
    | other -> failwith $"expected one plugin verdict, got %A{other}"

// ---------------------------------------------------------------------------
// AUTOMATION-111 — a recall miss must SHOUT, in the output already being read
// ---------------------------------------------------------------------------
//
// `Divergence.CheckMissedFailures` means the impact-scoped run was GREEN over a
// tree the full suite finds RED: the selector did not choose a test that fails.
// It has been computed and written to `verdict.json` since AUTOMATION-259 — and
// rendered NOWHERE.
//
// That is the gap this ticket names. A fact filed in a document you must
// remember to open is not a safeguard; the only moment the hint is worth
// anything is the moment someone is looking at the output. Without it a recall
// miss is indistinguishable from an ordinary test failure, so it gets FIXED as
// one: the test is repaired, the selector's blind spot is never seen, and the
// same green-that-lied ships again.

let private comparisonWith (d: Verdict.Divergence) : Verdict.CheckComparison =
    { Divergence = d
      ImpactScoped =
        Some
            { Scope = ImpactFiltered(5, 6)
              Outcome = Verdict.Green
              FailingSuites = []
              Basis = Verdict.SampleBasis.Executed }
      FailureRecall = None }

[<Fact>]
let ``AUTOMATION-111: a recall miss is named as a SELECTION BUG in the verdict output`` () =
    let hints =
        hintsFor
            { greenVerdict "t" 1 with
                Outcome = Verdict.Red
                RedCauses = [ structuralRedCause ]
                Comparison = comparisonWith Verdict.Divergence.CheckMissedFailures }

    let text = String.concat "\n" hints

    // It must say what KIND of problem this is — a tool defect, not a test bug.
    test <@ text.Contains "SELECTION BUG" @>
    test <@ text.Contains "RECALL" @>
    // …and it must say what to do about it, which is NOT "fix the test".
    test <@ text.Contains "fix the selector" @>

[<Fact>]
let ``AUTOMATION-67: verdict output reports the persisted conditional recall fraction and threshold`` () =
    let comparison =
        { comparisonWith Verdict.Divergence.CheckMissedFailures with
            FailureRecall = Some(FailureRecallMeasured(3, 4, 1.0, false)) }

    let text =
        hintsFor
            { greenVerdict "t" 1 with
                Outcome = Verdict.Red
                RedCauses = [ structuralRedCause ]
                Comparison = comparison }
        |> String.concat "\n"

    test <@ text.Contains "conditional failing-test recall 3/4 (75.0%)" @>
    test <@ text.Contains "threshold 100%" @>
    test <@ text.Contains "BELOW THRESHOLD" @>

[<Fact>]
let ``AUTOMATION-67: zero-denominator recall is rendered as not measurable, never perfect`` () =
    let comparison =
        { comparisonWith Verdict.Divergence.Agreed with
            FailureRecall =
                Some(FailureRecallNotMeasurable "the full run observed no failures, so the recall denominator is zero") }

    let text =
        hintsFor
            { greenVerdict "t" 0 with
                Comparison = comparison }
        |> String.concat "\n"

    test <@ text.Contains "conditional failing-test recall not measurable" @>
    test <@ text.Contains "denominator is zero" @>
    test <@ not (text.Contains "100.0%") @>

[<Fact>]
let ``AUTOMATION-111: the recall alarm is the FIRST thing in the block`` () =
    // Placement is load-bearing. A reader who meets this after a wall of test
    // causes has already started debugging the wrong thing — the failing tests
    // are real, they are just not the story.
    let hints =
        hintsFor
            { greenVerdict "t" 1 with
                Outcome = Verdict.Red
                RedCauses = [ structuralRedCause ]
                Comparison = comparisonWith Verdict.Divergence.CheckMissedFailures }

    test <@ hints |> List.head |> (fun l -> l.Contains "RECALL") @>

[<Fact>]
let ``AUTOMATION-111: no recall alarm when the two readings agreed`` () =
    // THE CONTROL. An alarm that fires on every red is not an alarm — it is
    // noise, and the first person to see it on an ordinary failure learns to
    // scroll past the one time it matters. Every non-miss classification must
    // stay silent, including the ones that are also not agreement.
    // The two classifications that ASSERT there was nothing to compare must carry
    // no impact-scoped run — `Verdict.create` refuses the contradiction, which is
    // how this test learned the invariant rather than encoding a guess.
    let comparisons =
        [ comparisonWith Verdict.Divergence.Agreed
          comparisonWith Verdict.Divergence.CheckOnlyFailures
          { Divergence = Verdict.Divergence.NoImpactScopedRun
            ImpactScoped = None
            FailureRecall = None }
          Verdict.CheckComparison.notRecorded ]

    for c in comparisons do
        let hints =
            hintsFor
                { greenVerdict "t" 1 with
                    Outcome = Verdict.Red
                    RedCauses = [ structuralRedCause ]
                    Comparison = c }

        let text = String.concat "\n" hints
        test <@ not (text.Contains "SELECTION BUG") @>

// ---------------------------------------------------------------------------
// AUTOMATION-158 — the declared gaps travel INSIDE `scope`
// ---------------------------------------------------------------------------

[<Fact>]
let ``a declared exclusion round-trips inside scope, project and reason both`` () =
    // The acceptance criterion the ticket states in so many words: `confirm` runs
    // the suite, or DECLARES in verdict.json's scope that it did not, "so a
    // consumer of the verdict can SEE the gap". A reason that did not survive the
    // write would leave a consumer with a gap and no way to judge it.
    withTempDir "verdict-158-roundtrip" (fun root ->
        let gap: SolutionScope.Exclusion =
            { Project = "tests/App.IntegrationTests"
              Reason = "end-to-end; run by `mise run test-integration` and by CI's solution-wide dotnet test" }

        writeSpec
            root
            { greenVerdict "sha256:abc" 1 with
                Excluded = Some [ gap ] }

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            test <@ v.Excluded = Some [ gap ] @>
            // ...and the scope is still a FULL one. A declared, reasoned exclusion
            // is not the bug — the silence is — so it must not cost the green.
            test <@ TestScope.isFullSuite v.Scope @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``"nothing was excluded" and "this verdict does not say" are different bytes`` () =
    // The whole shape of AUTOMATION-158 one level down: an absent gap must not
    // read as no gap. `Some []` is a claim this build establishes by reconciling
    // the config with the solution before any test runs; `None` is what a verdict
    // written before the field existed is entitled to say, and no more.
    let claimsNothingExcluded =
        System.Text.Json.Nodes.JsonNode
            .Parse(
                serializeSpec
                    { greenVerdict "sha256:abc" 1 with
                        Excluded = Some [] }
            )
            .Item("scope")
            .Item("excluded")

    let saysNothing =
        System.Text.Json.Nodes.JsonNode
            .Parse(
                serializeSpec
                    { greenVerdict "sha256:abc" 1 with
                        Excluded = None }
            )
            .Item("scope")
            .Item("excluded")

    test <@ claimsNothingExcluded.ToJsonString() = "[]" @>
    test <@ isNull saysNothing @>

[<Fact>]
let ``a verdict written before the field existed does not get to claim completeness`` () =
    withTempDir "verdict-158-legacy" (fun root ->
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","outcome":{"kind":"green"},
                "scope":{"kind":"full","ranProjects":6,"totalProjects":6}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            // NOT `Some []`. The old file said nothing about exclusions, and
            // "said nothing" is not "there were none".
            test <@ v.Excluded = None @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``the gaps are stated on every scope kind, because they are a fact about the config`` () =
    // Not only on `full`. A `none` scope over a config with a declared exclusion
    // still has that exclusion, and a field that appears only sometimes is a field
    // a consumer has to guess about.
    let gapsOn (scope: TestScope) =
        System.Text.Json.Nodes.JsonNode
            .Parse(
                serializeSpec
                    { greenVerdict "sha256:abc" 1 with
                        Command = Verdict.Check
                        Scope = scope
                        Excluded = Some [ { Project = "tests/X"; Reason = "why" } ] }
            )
            .Item("scope")
            .Item("excluded")
            .ToJsonString()

    for scope in
        [ FullSuite 6
          ImpactFiltered(2, 6)
          NoTestsRun
          ScopeUnknown
          ScopeUnreadable "faulted" ] do
        test <@ (gapsOn scope).Contains "tests/X" @>

// ---------------------------------------------------------------------------
// AUTOMATION-259 (rework) — the PROJECTED check-scoped reading.
//
// The feature shipped and its premise did not close. `confirm` requests full scope
// BEFORE the scan that provokes the test run, in both transports, so the run is
// unfiltered by construction, the capture condition is false at the capture point, and
// every verdict records `no-impact-scoped-run`: a true statement that produced, across
// seventeen confirms in ten days, exactly zero comparisons.
//
// So the suite still runs ONCE, and the reading is derived instead: the impact selection
// is retained at the moment `confirm` widens past it, and the run's own result is
// projected back through it. These tests pin the ONE property that matters more than any
// other here — every way of not being able to decide lands somewhere that is NOT
// `Agreed`. An agreement that compared nothing is the defect the record exists to stop.
// ---------------------------------------------------------------------------

let private projectionRunId = Guid.Parse("22222222-2222-2222-2222-222222222222")

let private gradedRun =
    { TestRunReport.ofScopeOnly (FullSuite 6) with
        RunId = Some projectionRunId }

/// A daemon reply that names THIS run and reports `reach`.
let private reachOf (reach: CheckReach) : CheckReachReading =
    ReachRecorded
        { RunId = Some projectionRunId
          Scope = ImpactFiltered(2, 6)
          Reach = reach
          Recall = FailureRecallNotMeasurable "test fixture" }

/// The projection, then the classification the verdict would record for it.
let private classify
    (reading: CheckReachReading)
    (earned: Verdict.Outcome)
    (statuses: Map<string, ParsedPluginStatus>)
    (causes: Verdict.RedCause list)
    : Verdict.CheckComparison =
    Verdict.comparisonOf (Verdict.ProjectedThrough reading) gradedRun earned statuses causes

[<Fact>]
let ``AUTOMATION-67 projected confirm evidence carries conditional recall into the verdict comparison`` () =
    let reading =
        ReachRecorded
            { RunId = Some projectionRunId
              Scope = ImpactFiltered(2, 6)
              Reach = ReachedAFailure [ "Lib.Tests" ]
              Recall = FailureRecallMeasured(3, 4, 1.0, false) }

    let comparison = classify reading Verdict.Red Map.empty []
    test <@ comparison.FailureRecall = Some(FailureRecallMeasured(3, 4, 1.0, false)) @>

[<Fact>]
let ``AUTOMATION-67 escalated confirm evidence also carries full-run conditional recall`` () =
    let executed: Verdict.ImpactScopedRun =
        { Scope = ImpactFiltered(2, 6)
          Outcome = Verdict.Green
          FailingSuites = []
          Basis = Verdict.SampleBasis.Executed }

    let comparison =
        Verdict.comparisonOf
            (Verdict.ExecutedReading(
                executed,
                ReachRecorded
                    { RunId = Some projectionRunId
                      Scope = ImpactFiltered(2, 6)
                      Reach = ReachedAFailure [ "Lib.Tests" ]
                      Recall = FailureRecallMeasured(4, 4, 1.0, true) }
            ))
            gradedRun
            Verdict.Red
            Map.empty
            []

    test <@ comparison.FailureRecall = Some(FailureRecallMeasured(4, 4, 1.0, true)) @>

[<Fact>]
let ``AUTOMATION-67 projected recall from another run is not measurable`` () =
    let reading =
        ReachRecorded
            { RunId = Some(Guid.Parse("33333333-3333-3333-3333-333333333333"))
              Scope = ImpactFiltered(2, 6)
              Reach = ReachedAFailure [ "Lib.Tests" ]
              Recall = FailureRecallMeasured(4, 4, 1.0, true) }

    let comparison = classify reading Verdict.Red Map.empty []

    match comparison.FailureRecall with
    | Some(FailureRecallNotMeasurable reason) -> test <@ reason.Contains "graded run" @>
    | other -> failwithf "expected a run-bound not-measurable recall, got %A" other

[<Fact>]
let ``AUTOMATION-67 executed recall without the graded run id is not measurable`` () =
    let executed: Verdict.ImpactScopedRun =
        { Scope = ImpactFiltered(2, 6)
          Outcome = Verdict.Green
          FailingSuites = []
          Basis = Verdict.SampleBasis.Executed }

    let comparison =
        Verdict.comparisonOf
            (Verdict.ExecutedReading(
                executed,
                ReachRecorded
                    { RunId = None
                      Scope = ImpactFiltered(2, 6)
                      Reach = ReachedAFailure [ "Lib.Tests" ]
                      Recall = FailureRecallMeasured(4, 4, 1.0, true) }
            ))
            gradedRun
            Verdict.Red
            Map.empty
            []

    match comparison.FailureRecall with
    | Some(FailureRecallNotMeasurable reason) -> test <@ reason.Contains "graded run" @>
    | other -> failwithf "expected a run-bound not-measurable recall, got %A" other

[<Fact>]
let ``AUTOMATION-67 run-bound recall survives comparison creation and the verdict wire`` () =
    withTempDir "recall-run-binding" (fun root ->
        makeRepo root

        let comparison =
            classify
                (ReachRecorded
                    { RunId = Some projectionRunId
                      Scope = ImpactFiltered(2, 6)
                      Reach = ReachedAFailure [ "Lib.Tests" ]
                      Recall = FailureRecallMeasured(4, 4, 1.0, true) })
                Verdict.Red
                Map.empty
                []

        writeSpec
            root
            { greenVerdict "sha256:recall-wire" 2 with
                RunId = Some projectionRunId
                Comparison = comparison }

        match Verdict.read root with
        | Verdict.Reading.Found verdict ->
            test <@ verdict.Comparison.FailureRecall = Some(FailureRecallMeasured(4, 4, 1.0, true)) @>
        | other -> failwithf "expected a readable verdict, got %A" other)

let private aboutThisTree (source: string) : Verdict.RedCause =
    { Source = source
      File = "<build>"
      Severity = "error"
      Message = "boom"
      Kind = Verdict.AboutThisTree }

let private failedPlugin (name: string) : string * ParsedPluginStatus =
    name,
    { Status = StatusView.Failed("crashed", DateTime.UtcNow)
      Subtasks = []
      ActivityTail = []
      LastRun = None
      Diagnostics = ErrorLedger.DiagnosticCounts.empty }

[<Fact>]
let ``a confirm that did not escalate records a PROJECTED sample, and says it is projected`` () =
    // The case that produced no data: the run its own scan provoked was already
    // unfiltered, nothing failed, and `check`'s narrower selection could therefore not
    // have found anything either.
    let c = classify (reachOf NoFailuresToReach) Verdict.Green Map.empty []

    test <@ c.Divergence = Verdict.Divergence.Agreed @>

    match c.ImpactScoped with
    | Some pre ->
        test <@ pre.Basis = Verdict.SampleBasis.ProjectedFromFullRun @>
        test <@ pre.Outcome = Verdict.Green @>
        // The scope `check` WOULD have covered — not the full suite the verdict rests on.
        test <@ pre.Scope = ImpactFiltered(2, 6) @>
    | None -> failwith "a projected sample must record the reading it classified"

[<Fact>]
let ``the selection missing the failure is CHECK-MISSED-FAILURES, which is the whole point`` () =
    // AUTOMATION-160, caught on the tree that produced it: the suite is red, and the
    // tests `check` would have chosen do not include the one that failed.
    let c =
        classify (reachOf ReachedNoFailure) Verdict.Red Map.empty [ aboutThisTree "test-prune" ]

    test <@ c.Divergence = Verdict.Divergence.CheckMissedFailures @>

    match c.ImpactScoped with
    | Some pre -> test <@ pre.Outcome = Verdict.Green @>
    | None -> failwith "the missed-failure sample must record the projected reading"

[<Fact>]
let ``a selection that DOES reach the failure agrees, and names the suites it reaches`` () =
    let c =
        classify (reachOf (ReachedAFailure [ "Lib.Tests" ])) Verdict.Red Map.empty [ aboutThisTree "test-prune" ]

    test <@ c.Divergence = Verdict.Divergence.Agreed @>

    match c.ImpactScoped with
    | Some pre ->
        test <@ pre.Outcome = Verdict.Red @>
        // The suites its SELECTION reaches, not every suite that failed — the run's own
        // record already carries those.
        test <@ pre.FailingSuites = [ "Lib.Tests" ] @>
    | None -> failwith "a reached-failure sample must record the projected reading"

[<Fact>]
let ``a red that is not about the tests reddens check too, so the sample AGREES`` () =
    // `check` reads the same ledger and the same plugin statuses. Removing the test
    // failures it would not have run does not remove a failing lint plugin or an FCS
    // diagnostic — those redden it for the same reason `confirm` was red.
    let viaPlugin =
        classify (reachOf ReachedNoFailure) Verdict.Red (Map.ofList [ failedPlugin "lint" ]) []

    test <@ viaPlugin.Divergence = Verdict.Divergence.Agreed @>

    let viaDiagnostic =
        classify (reachOf ReachedNoFailure) Verdict.Red Map.empty [ aboutThisTree "test-prune"; aboutThisTree "fcs" ]

    test <@ viaDiagnostic.Divergence = Verdict.Divergence.Agreed @>

    // CONTROL. A failing TEST-PRUNE plugin is the test dimension itself and must NOT be
    // read as a red beyond the tests — otherwise every missed failure would agree, and
    // the classification above would be unreachable.
    let viaTestPrune =
        classify (reachOf ReachedNoFailure) Verdict.Red (Map.ofList [ failedPlugin "test-prune" ]) []

    test <@ viaTestPrune.Divergence = Verdict.Divergence.CheckMissedFailures @>

[<Fact>]
let ``a red made only of causes fshw cannot attribute is INCOMPARABLE, never agreement`` () =
    // Set the unreachable test failures aside and what is left is a ledger `check` would
    // route to `StaleDaemonState` — NO VERDICT, exit 3. That is neither the green nor the
    // red this sample would have to compare, so it is refused.
    let unattributable: Verdict.RedCause =
        { Source = "fcs"
          File = "/gone/Vanished.fs"
          Severity = "error"
          Message = "internal error: boom"
          Kind = Verdict.CheckerFault }

    let c =
        classify (reachOf ReachedNoFailure) Verdict.Red Map.empty [ aboutThisTree "test-prune"; unattributable ]

    match c.Divergence with
    | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "NO VERDICT" @>
    | other -> failwithf "an unattributable-only residue must be INCOMPARABLE, got %A" other

[<Fact>]
let ``every way of not being able to decide lands somewhere that is NOT agreement`` () =
    // THE property. Each of these is a different way of not having a comparison, and a
    // single one of them collapsing into `Agreed` would let "N confirms with zero
    // divergence" be satisfied by runs that compared nothing — the exact failure this
    // record was built to make impossible.
    let otherRun = Guid.Parse("33333333-3333-3333-3333-333333333333")

    let undecidable: (string * CheckReachReading * Verdict.Outcome) list =
        [ "the daemon has no projection to offer", ReachUnavailable "no such command", Verdict.Green
          "the daemon could not decide the reach", reachOf (ReachUnknown "a project-level red"), Verdict.Green
          "the projection belongs to another run",
          ReachRecorded
              { RunId = Some otherRun
                Scope = ImpactFiltered(2, 6)
                Reach = NoFailuresToReach
                Recall = FailureRecallNotMeasurable "test fixture" },
          Verdict.Green
          "the projection names no run",
          ReachRecorded
              { RunId = None
                Scope = ImpactFiltered(2, 6)
                Reach = NoFailuresToReach
                Recall = FailureRecallNotMeasurable "test fixture" },
          Verdict.Green
          "the escalated run reached no verdict", reachOf NoFailuresToReach, Verdict.Incomplete "the tree moved"
          "the run says failures exist and green at once", reachOf ReachedNoFailure, Verdict.Green ]

    for label, reading, earned in undecidable do
        let c = classify reading earned Map.empty []

        test <@ c.Divergence <> Verdict.Divergence.Agreed @>
        test <@ c.Divergence <> Verdict.Divergence.CheckMissedFailures @>
        test <@ c.Divergence <> Verdict.Divergence.CheckOnlyFailures @>

        match c.Divergence with
        | Verdict.Divergence.Incomparable _ -> ()
        | other -> failwithf "%s must be INCOMPARABLE, got %A" label other

    // CONTROL. Without it, a `classify` that had started refusing everything would pass
    // every assertion above.
    test <@ (classify (reachOf NoFailuresToReach) Verdict.Green Map.empty []).Divergence = Verdict.Divergence.Agreed @>

[<Fact>]
let ``a PROJECTION may never claim a check-only failure`` () =
    // A check-only failure asserts that the same code fails narrow and passes wide —
    // order, isolation, a shared fixture. Only a reading that RAN can have seen that; a
    // projection reads one run, so a test it saw pass is a test that passed. The pair is
    // a contradiction in the arithmetic, not a finding about the tests.
    let projected: Verdict.ImpactScopedRun =
        { Scope = ImpactFiltered(2, 6)
          Outcome = Verdict.Red
          FailingSuites = [ "Lib.Tests" ]
          Basis = Verdict.SampleBasis.ProjectedFromFullRun }

    match (Verdict.CheckComparison.ofRun (Some projected) Verdict.Green).Divergence with
    | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "projection" @>
    | other -> failwithf "a projected check-only failure must be INCOMPARABLE, got %A" other

    // CONTROL. The very same pair from an EXECUTED reading is a real, reportable
    // check-only failure — the classification is not simply switched off.
    match
        (Verdict.CheckComparison.ofRun
            (Some
                { projected with
                    Basis = Verdict.SampleBasis.Executed })
            Verdict.Green)
            .Divergence
    with
    | Verdict.Divergence.CheckOnlyFailures -> ()
    | other -> failwithf "an executed check-only failure must still be reported, got %A" other

[<Fact>]
let ``the sample's BASIS round-trips, and a verdict that predates the field reads as EXECUTED`` () =
    withTempDir "verdict-259-basis" (fun root ->
        for basis in
            [ Verdict.SampleBasis.Executed
              Verdict.SampleBasis.ProjectedFromFullRun
              Verdict.SampleBasis.UnknownBasis "extrapolated" ] do
            writeSpec
                root
                { greenVerdict "sha256:basis" 1 with
                    Comparison =
                        { Divergence = Verdict.Divergence.Agreed
                          ImpactScoped =
                            Some(
                                { Scope = ImpactFiltered(2, 6)
                                  Outcome = Verdict.Green
                                  FailingSuites = []
                                  Basis = basis }
                                : Verdict.ImpactScopedRun
                            )
                          FailureRecall = None } }

            match Verdict.read root with
            | Verdict.Reading.Found v ->
                match v.ImpactScopedRun with
                | Some pre -> test <@ pre.Basis = basis @>
                | None -> failwith "the reading must survive the round-trip"
            | other -> failwithf "a verdict carrying basis %A must read back, got %A" basis other

        // A sample written before the field existed WAS a second execution — every one of
        // them came from an escalation — so an ABSENT basis reads as `Executed`. A token
        // this build does not know is a different fact and keeps its own case, because
        // rounding it to `Executed` would claim a run that may never have happened.
        Directory.CreateDirectory(FsHwPaths.root root) |> ignore

        File.WriteAllText(
            Verdict.path root,
            """{"schema":"fshw-verdict-v1","treeHash":"sha256:x","command":"confirm",
                "scope":{"kind":"full","ranProjects":6,"totalProjects":6},
                "outcome":{"kind":"green"},"exitCode":0,"plugins":[],
                "checkComparison":{"divergence":{"kind":"agreed"},
                  "impactScopedRun":{"scope":{"kind":"filtered","ranProjects":2,"totalProjects":6},
                                     "outcome":{"kind":"green"},"failingSuites":[]}}}"""
        )

        match Verdict.read root with
        | Verdict.Reading.Found v ->
            match v.ImpactScopedRun with
            | Some pre -> test <@ pre.Basis = Verdict.SampleBasis.Executed @>
            | None -> failwith "a legacy sample must still read back"
        | other -> failwithf "a legacy verdict must read, got %A" other)

// ---------------------------------------------------------------------------
// AUTOMATION-165 — the tree hash covers what DECIDES the check, not just the source
//
// Every test below is paired. The POSITIVE half pins the defect: under the v2 hash
// (walk of `src`/`tests`, plus `.fshw.json`) each of these edits left a prior green
// still reporting `Applies`, so reverting the fix fails these by NAME rather than by
// some vague behavioural drift. The NEGATIVE half pins the opposite failure: a hash
// that moves on every edit anywhere would pass all the positive tests and be just as
// useless, because nothing would ever apply.
// ---------------------------------------------------------------------------

/// A repo that DECLARES what decides its checks, the way a consuming repo does:
/// the coverage floors and the analyzer rules, each with a reviewable `why`, plus
/// one file it deliberately states is NOT an input.
let private declaringRepo (root: string) =
    makeRepo root

    Directory.CreateDirectory(Path.Combine(root, "analyzers", "Rules")) |> ignore

    File.WriteAllText(
        Path.Combine(root, "analyzers", "Rules", "ConventionAnalyzers.fs"),
        "module Rules\nlet ``FSHW-CLAIM-001`` = 1\n"
    )

    File.WriteAllText(Path.Combine(root, "coverage-ratchet.json"), """{"src/Lib/Lib.fs": 80.0}""")
    File.WriteAllText(Path.Combine(root, "README.md"), "# prose\n")

    File.WriteAllText(
        FsHwPaths.configFile root,
        """
{
  "verdictInputs": {
    "hashed": [
      { "path": "coverage-ratchet.json",
        "why": "lower a floor and a verdict earned under the higher one must stop applying" },
      { "path": "analyzers/**/*.fs",
        "why": "these ARE the house rules the analyze plugin enforces" }
    ],
    "notInputs": [
      { "path": "README.md", "reason": "prose about the repo; no check reads it" }
    ]
  }
}
"""
    )

/// Earn a green over the tree as it stands, and CHECK that it applies. Every test
/// below needs that control: "stale after the edit" is also what a verdict that
/// never applied at all would report.
let private earnGreenOver (root: string) =
    let before = TreeHash.compute root []
    writeSpec root (greenVerdict before.Hash before.FileCount)

    match Verdict.report root [] with
    | Verdict.Report.Applies _ -> before
    | other -> failwith $"the verdict must apply to the tree it was earned on, got %A{other}"

let private expectStale (root: string) (what: string) =
    match Verdict.report root [] with
    | Verdict.Report.Stale(v, _) ->
        // Still a GREEN on disk. That is the whole danger: nothing about the file
        // says it should not be reused; only `applicability` does.
        test <@ v.Outcome = Verdict.Green @>
    | other -> failwith $"%s{what} must make the verdict stale, got %A{other}"

[<Fact>]
let ``lowering a coverage floor makes the verdict STALE — a green earned under the HIGHER floor never certifies the lower one``
    ()
    =
    // THE defect AUTOMATION-165 was filed on, in its cheapest form. Measured in the
    // consuming repo before this fix: floor lowered, `fshw verdict` still exit 0,
    // `applies: true`. The verdict answered a question about a tree that had changed
    // underneath it.
    withTempDir "a165-floor" (fun root ->
        declaringRepo root
        earnGreenOver root |> ignore

        File.WriteAllText(Path.Combine(root, "coverage-ratchet.json"), """{"src/Lib/Lib.fs": 40.0}""")
        expectStale root "a lowered coverage floor")

[<Fact>]
let ``editing the analyzer RULES makes the verdict stale — the rules are an input, not scenery`` () =
    // `analyzers/` is outside the discovery roots, so under v2 the house rules were
    // unhashed: break FSHW-CLAIM-001 and the green that was earned while it held
    // still reported `Applies`.
    withTempDir "a165-rules" (fun root ->
        declaringRepo root
        earnGreenOver root |> ignore

        File.WriteAllText(
            Path.Combine(root, "analyzers", "Rules", "ConventionAnalyzers.fs"),
            "module Rules\n// rule deleted\n"
        )

        expectStale root "an edited analyzer rule")

[<Fact>]
let ``an UNDECLARED, non-deciding file leaves the verdict APPLYING — the hash is derived, not merely churning`` () =
    // THE NEGATIVE CONTROL for every test above. A tree hash that moved on any edit
    // anywhere would satisfy all of them and be worthless: no verdict would ever be
    // reusable, and `confirm`'s fast path — the one thing allowed to carry a green
    // across a process boundary — would never hit.
    withTempDir "a165-negative" (fun root ->
        declaringRepo root
        earnGreenOver root |> ignore

        // A file the repo explicitly declared is NOT an input...
        File.WriteAllText(Path.Combine(root, "README.md"), "# prose, rewritten\n")
        // ...and one it never mentioned at all. Neither can change what a check concludes.
        File.WriteAllText(Path.Combine(root, "NOTES.md"), "scratch\n")

        match Verdict.report root [] with
        | Verdict.Report.Applies v -> test <@ v.Outcome = Verdict.Green @>
        | other -> failwith $"editing prose must NOT invalidate a verdict, got %A{other}")

[<Fact>]
let ``a declared input that matches NOTHING is an ENTRY in the hash, never a silent zero`` () =
    // The way a fix like this rots: someone renames the file, or fat-fingers the
    // declaration, and the tool goes back to hashing exactly what it hashed before
    // while the config still reads as protection. A declaration that resolves to no
    // file contributes a SENTINEL, so the tree with a broken declaration cannot hash
    // like the tree without one, and the hash moves again when the file appears.
    withTempDir "a165-absent" (fun root ->
        makeRepo root

        let declare (block: string) =
            File.WriteAllText(FsHwPaths.configFile root, block)
            TreeHash.compute root []

        // The hash of the FILES ALONE — every entry `compute` would produce if an
        // unresolved declaration contributed nothing. Reconstructed from the public
        // recipe rather than compared against another config on disk: changing the
        // config changes its own bytes, so "two configs hash differently" would be
        // true whether or not the sentinel exists, and would prove nothing.
        let filesOnlyHash () =
            let walked = TreeHash.files root []
            test <@ walked.Skipped |> List.isEmpty @>

            walked.Files
            |> List.map (fun (rel, abs) -> rel, ContentHash.ofFile abs)
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            |> TreeHash.hashEntries

        declare """{"format": true}""" |> ignore
        // CONTROL: with nothing declared, the tree IS its files — no sentinels, so the
        // reconstruction is exact. Without this the assertion below could be measuring
        // a reconstruction that never matches anything.
        test <@ filesOnlyHash () = (TreeHash.compute root []).Hash @>

        let declared =
            declare
                """{"format": true,
                     "verdictInputs": {"hashed": [
                       {"path": "probe-baseline.json", "why": "the census a finding is measured against"}]}}"""

        test <@ declared.AbsentDeclarationCount = 1 @>
        test <@ declared.DeclaredCount = 0 @>
        // The declaration is IN the hash even though the file is not on disk: the tree
        // no longer hashes to its files alone. A silently-skipped declaration would
        // land back on that reconstruction exactly.
        test <@ filesOnlyHash () <> declared.Hash @>

        File.WriteAllText(Path.Combine(root, "probe-baseline.json"), """{"keys": []}""")
        let present = TreeHash.compute root []

        test <@ present.AbsentDeclarationCount = 0 @>
        test <@ present.DeclaredCount = 1 @>
        test <@ present.Hash <> declared.Hash @>
        // The sentinel is GONE once the file resolves — the tree is its files again.
        test <@ filesOnlyHash () = present.Hash @>

        // POSITIVE CONTROL that the file is genuinely hashed rather than merely counted:
        // its CONTENT moves the hash.
        File.WriteAllText(Path.Combine(root, "probe-baseline.json"), """{"keys": ["probe"]}""")
        test <@ (TreeHash.compute root []).Hash <> present.Hash @>)

[<Fact>]
let ``Directory.Build.props moves the tree hash with NOTHING declared — the tool knows its own toolchain inputs`` () =
    // A repo that declares nothing must still be gated on the files that decide what
    // the compiler does at all. `TreatWarningsAsErrors` is the sharp case: flip it off
    // and every warning-as-error the green was earned under stops being one.
    withTempDir "a165-toolknown" (fun root ->
        makeRepo root
        let props = Path.Combine(root, "Directory.Build.props")

        File.WriteAllText(
            props,
            "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>"
        )

        let before = earnGreenOver root
        test <@ (TreeHash.compute root []).DeclaredCount = 0 @>

        File.WriteAllText(
            props,
            "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>"
        )

        expectStale root "flipping TreatWarningsAsErrors off"

        // NEGATIVE CONTROL: a root-level file that decides nothing must not move it.
        File.WriteAllText(
            props,
            "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>"
        )

        test <@ (TreeHash.compute root []).Hash = before.Hash @>
        File.WriteAllText(Path.Combine(root, "CONTRIBUTING.md"), "be nice\n")
        test <@ (TreeHash.compute root []).Hash = before.Hash @>)

[<Fact>]
let ``a DECLARED path under bin slash is hashed — an explicit declaration outranks the build-output filter`` () =
    // Measured in the consuming repo: append a NUL byte to the analyzer DLL that IS
    // the analyzer, and the verdict still reported green for that tree. The walk will
    // never offer a file under `bin/` — that heuristic is about what to go LOOKING at
    // — so the only way to reach it is an explicit declaration, and a declaration the
    // generated-path filter could veto would be a declaration that cannot be honoured.
    withTempDir "a165-bin" (fun root ->
        makeRepo root
        let binDir = Path.Combine(root, "analyzers", "Rules", "bin", "Debug")
        Directory.CreateDirectory binDir |> ignore
        File.WriteAllBytes(Path.Combine(binDir, "Rules.dll"), [| 1uy; 2uy; 3uy |])
        File.WriteAllBytes(Path.Combine(binDir, "Unrelated.dll"), [| 9uy |])

        File.WriteAllText(
            FsHwPaths.configFile root,
            """{"verdictInputs": {"hashed": [
                 {"path": "analyzers/Rules/bin/Debug/Rules.dll",
                  "why": "the assembly the analyze plugin actually loads"}]}}"""
        )

        let before = TreeHash.compute root []
        test <@ before.DeclaredCount = 1 @>
        test <@ before.AbsentDeclarationCount = 0 @>

        File.WriteAllBytes(Path.Combine(binDir, "Rules.dll"), [| 1uy; 2uy; 3uy; 0uy |])
        test <@ (TreeHash.compute root []).Hash <> before.Hash @>

        // NEGATIVE CONTROL: the UNDECLARED sibling in the same build directory stays
        // out. Declaring one file under `bin/` must not drag the whole of `bin/` in,
        // or every rebuild would invalidate the verdict it had just earned.
        File.WriteAllBytes(Path.Combine(binDir, "Rules.dll"), [| 1uy; 2uy; 3uy |])
        test <@ (TreeHash.compute root []).Hash = before.Hash @>
        File.WriteAllBytes(Path.Combine(binDir, "Unrelated.dll"), [| 9uy; 9uy |])
        test <@ (TreeHash.compute root []).Hash = before.Hash @>)

[<Fact>]
let ``a declared DIRECTORY may POINT AT build output but never SWEEPS into it`` () =
    // The asymmetry is deliberate. `analyzers/Rules` means the project, not the
    // project plus every artifact of every configuration ever built there — a
    // directory declaration that swept `bin/` in would churn the tree hash on every
    // rebuild and invalidate the verdict the run had just earned. Pointing AT the
    // output directory is a different, explicit statement, and it resolves.
    withTempDir "a165-dir-vs-bin" (fun root ->
        makeRepo root
        let projDir = Path.Combine(root, "analyzers", "Rules")
        let binDir = Path.Combine(projDir, "bin", "Debug")
        Directory.CreateDirectory binDir |> ignore
        File.WriteAllText(Path.Combine(projDir, "Rules.fs"), "module Rules")
        File.WriteAllBytes(Path.Combine(binDir, "Rules.dll"), [| 1uy |])

        let declare (block: string) =
            File.WriteAllText(FsHwPaths.configFile root, block)
            TreeHash.compute root []

        let swept =
            declare
                """{"verdictInputs": {"hashed": [
                     {"path": "analyzers/Rules", "why": "the rules project"}]}}"""

        // The source, and NOT the assembly beside it.
        test <@ swept.DeclaredCount = 1 @>

        let pointed =
            declare
                """{"verdictInputs": {"hashed": [
                     {"path": "analyzers/Rules/bin/Debug", "why": "the assemblies the plugin loads"}]}}"""

        test <@ pointed.DeclaredCount = 1 @>
        test <@ pointed.AbsentDeclarationCount = 0 @>

        // ...and it is the ASSEMBLY that is hashed, not the source: touching the DLL
        // moves this hash, which is what distinguishes "pointed at bin" from a second
        // spelling of the source declaration above.
        File.WriteAllBytes(Path.Combine(binDir, "Rules.dll"), [| 1uy; 1uy |])
        test <@ (TreeHash.compute root []).Hash <> pointed.Hash @>

        // A glob that would have to DESCEND into build output matches nothing — and
        // says so, rather than being quietly narrow.
        let descending =
            declare
                """{"verdictInputs": {"hashed": [
                     {"path": "analyzers/**/*.dll", "why": "every rules assembly"}]}}"""

        test <@ descending.DeclaredCount = 0 @>
        test <@ descending.AbsentDeclarationCount = 1 @>)

[<Fact>]
let ``a notInput records a DECISION and removes nothing from the hash — the config cannot weaken the gate`` () =
    // `notInputs` exists so "not hashed" can be a stated, reviewable decision rather
    // than an omission nobody noticed. It must not become a supported way to shrink
    // the hashed set: a config key that could delete a source file from the tree hash
    // would be a one-line, config-only route to exactly the fail-open this ticket is
    // about, with a `reason` field to make it look considered.
    withTempDir "a165-notinput" (fun root ->
        makeRepo root

        File.WriteAllText(
            FsHwPaths.configFile root,
            """{"verdictInputs": {"notInputs": [
                 {"path": "src/Lib/Lib.fs", "reason": "a lie, and it must not be honoured"}]}}"""
        )

        earnGreenOver root |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 43\n")
        expectStale root "editing a source file a notInput tried to excuse")

[<Fact>]
let ``the tree hash is stable across repeated computation WITH declarations — globs do not reorder`` () =
    // A glob is expanded by a directory walk, and a hash whose input order came from
    // the filesystem would differ run to run on the same tree — a verdict that never
    // applies, reported as a tree that never holds still.
    withTempDir "a165-stable" (fun root ->
        declaringRepo root
        let a = TreeHash.compute root []
        let b = TreeHash.compute root []
        test <@ a.Hash = b.Hash @>
        test <@ a.DeclaredCount = b.DeclaredCount @>
        // The glob matched the rules file and the literal matched the ratchet file: a
        // count of 0 here would make every assertion above vacuous.
        test <@ a.DeclaredCount = 2 @>
        test <@ a.AbsentDeclarationCount = 0 @>)

// --- The declaration itself: refused loudly, never half-understood ---

[<Fact>]
let ``a well-formed verdictInputs declaration parses with NO errors — the control for every rejection below`` () =
    let d =
        VerdictInputs.parse
            """{"verdictInputs": {
                 "hashed": [{"path": "coverage-counts.json", "why": "the floors"}],
                 "notInputs": [{"path": "CHANGELOG.md", "reason": "prose about already-gated work"}]}}"""

    test <@ d.Errors |> List.isEmpty @>
    test <@ d.Hashed |> List.map (fun h -> h.Path) = [ "coverage-counts.json" ] @>
    test <@ d.NotInputs |> List.map (fun n -> n.Path) = [ "CHANGELOG.md" ] @>

[<Fact>]
let ``a repo that declares nothing parses to EMPTY, not to an error — declaring nothing is a valid state`` () =
    test <@ (VerdictInputs.parse """{"format": true}""").Errors |> List.isEmpty @>
    test <@ (VerdictInputs.parse """{"format": true}""").Hashed |> List.isEmpty @>
    // Text that is not JSON at all is the CONFIG LOADER's complaint to make; inventing
    // a second one about the same broken file only buries the real one.
    test <@ (VerdictInputs.parse "{not json").Errors |> List.isEmpty @>

[<Theory>]
[<InlineData("""{"verdictInputs": {"hashed": [{"path": "x.json"}]}}""", "why")>]
[<InlineData("""{"verdictInputs": {"notInputs": [{"path": "x.json"}]}}""", "reason")>]
[<InlineData("""{"verdictInputs": {"hashed": [{"path": "x.json", "why": "  "}]}}""", "why")>]
let ``a declaration with no stated reason is refused — one nobody can review is one nobody will notice going wrong``
    (json: string)
    (expected: string)
    =
    let errors = (VerdictInputs.parse json).Errors
    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains expected @>

[<Fact>]
let ``a path declared as BOTH an input and a not-an-input is refused — it cannot be both`` () =
    let errors =
        (VerdictInputs.parse
            """{"verdictInputs": {
                 "hashed": [{"path": "floors.json", "why": "the floors"}],
                 "notInputs": [{"path": "floors.json", "reason": "not really"}]}}""")
            .Errors

    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains "BOTH" @>

[<Theory>]
[<InlineData("""{"verdictInputs": {"hashed": [{"path": "../elsewhere/x.json", "why": "escapes"}]}}""")>]
[<InlineData("""{"verdictInputs": {"hashed": [{"path": "/etc/passwd", "why": "rooted"}]}}""")>]
let ``a declared path outside the repo is refused — a verdict input must be part of the tree being addressed``
    (json: string)
    =
    let errors = (VerdictInputs.parse json).Errors
    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains "inside the repo" @>

[<Fact>]
let ``the same path declared twice is refused — one path, one stated reason`` () =
    let errors =
        (VerdictInputs.parse
            """{"verdictInputs": {"hashed": [
                 {"path": "floors.json", "why": "first reason"},
                 {"path": "floors.json", "why": "second, contradictory reason"}]}}""")
            .Errors

    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains "declared 2 times" @>

[<Fact>]
let ``verdictInputs that is not an object is refused rather than read as an empty declaration`` () =
    let errors = (VerdictInputs.parse """{"verdictInputs": ["coverage.json"]}""").Errors
    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains "must be an object" @>

// --- The hashing scheme names itself, and a verdict from another one is refused ---

[<Fact>]
let ``the tree-hash algorithm is v3 — the recipe changed, so the name must have`` () =
    // The field exists so a consumer can tell a hash it understands from one it does
    // not. A change to WHAT IS HASHED that left the name alone would make two
    // incomparable strings look comparable, which is worse than no field at all.
    test <@ TreeHash.Algorithm = "fshw-tree-sha256-v3" @>

[<Fact>]
let ``a verdict from an OLDER hashing scheme is inapplicable by ALGORITHM — not puzzled over as a stale tree`` () =
    // The sharp case, and the reason this check is not merely cosmetic: the treeHash
    // strings are made to MATCH. Under the old `applicability` — which recorded
    // `treeHashAlgorithm` and never read it — this verdict reported `Applies`, and a
    // green earned over a tree that did not include the coverage floors or the
    // analyzer rules would have been promoted to a claim about a tree that does.
    withTempDir "a165-algorithm" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        let node =
            System.Text.Json.Nodes.JsonNode.Parse(serializeSpec (greenVerdict tree.Hash tree.FileCount))

        node.["treeHashAlgorithm"] <- System.Text.Json.Nodes.JsonValue.Create "fshw-tree-sha256-v2"
        Directory.CreateDirectory(Path.GetDirectoryName(Verdict.path root)) |> ignore
        File.WriteAllText(Verdict.path root, node.ToJsonString() + "\n")

        match Verdict.report root [] with
        | Verdict.Report.Stale(_, reason) ->
            test <@ reason.Contains "DIFFERENT scheme" @>
            test <@ reason.Contains "fshw-tree-sha256-v2" @>
            test <@ reason.Contains TreeHash.Algorithm @>
        | other -> failwith $"a v2 verdict must not apply to a v3 hasher, got %A{other}"

        // POSITIVE CONTROL: the SAME file with the algorithm left alone applies. Without
        // it, this test would also pass against a build that refused every verdict.
        node.["treeHashAlgorithm"] <- System.Text.Json.Nodes.JsonValue.Create TreeHash.Algorithm
        File.WriteAllText(Verdict.path root, node.ToJsonString() + "\n")

        match Verdict.report root [] with
        | Verdict.Report.Applies _ -> ()
        | other -> failwith $"the same verdict under the CURRENT algorithm must apply, got %A{other}")

[<Fact>]
let ``the verdict RECORDS how many declared inputs it hashed — an ignored declaration is otherwise invisible`` () =
    // The failure this closes is the one that filed the ticket: a consuming repo
    // declared 29 inputs against a tool that read none of them, and nothing anywhere
    // said so. A count in the artifact is the only place a repo can check that its
    // declaration is being honoured rather than merely written down.
    withTempDir "a165-recorded" (fun root ->
        declaringRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Tree = tree }

        let json = File.ReadAllText(Verdict.path root)
        use doc = JsonDocument.Parse json
        test <@ doc.RootElement.GetProperty("treeDeclaredCount").GetInt32() = 2 @>
        test <@ doc.RootElement.GetProperty("treeAbsentDeclarationCount").GetInt32() = 0 @>
        test <@ doc.RootElement.GetProperty("treeHashAlgorithm").GetString() = TreeHash.Algorithm @>)

[<Theory>]
[<InlineData("""{"verdictInputs": {"hashed": "coverage.json"}}""", "must be an array")>]
[<InlineData("""{"verdictInputs": {"hashed": ["coverage.json"]}}""", "not an object")>]
[<InlineData("""{"verdictInputs": {"hashed": [{"why": "no path given"}]}}""", "no non-empty 'path'")>]
let ``a verdictInputs block shaped wrong is refused rather than read as declaring nothing``
    (json: string)
    (expected: string)
    =
    // The shape that matters most: a declaration fshw half-understands must not
    // degrade to "declares nothing", because that is byte-for-byte the behaviour
    // this whole feature exists to replace.
    let errors = (VerdictInputs.parse json).Errors
    test <@ errors.Length = 1 @>
    test <@ errors.Head.Contains expected @>

[<Fact>]
let ``a config whose top level is not an object declares nothing — that complaint belongs to the config loader`` () =
    // `.fshw.json` is itself a hashed input, and `loadConfig` already refuses a file
    // it cannot parse. A second complaint here about the same broken file would only
    // make the real one harder to find.
    test <@ (VerdictInputs.parse "[1, 2, 3]").Errors |> List.isEmpty @>
    test <@ (VerdictInputs.parse "[1, 2, 3]").Hashed |> List.isEmpty @>

[<Fact>]
let ``a root-level glob resolves from the repo root — the walk starts at the shallowest literal prefix`` () =
    // `*.json` has no literal prefix at all, so the walk root is the repo itself. The
    // arm exists because grouping every glob under its own prefix would otherwise walk
    // nothing for this one and report it ABSENT.
    withTempDir "a165-rootglob" (fun root ->
        makeRepo root
        File.WriteAllText(Path.Combine(root, "floors.json"), """{"a": 1}""")
        File.WriteAllText(Path.Combine(root, "baseline.json"), """{"b": 2}""")

        File.WriteAllText(
            FsHwPaths.configFile root,
            """{"verdictInputs": {"hashed": [{"path": "*.json", "why": "every root baseline"}]}}"""
        )

        let tree = TreeHash.compute root []
        test <@ tree.AbsentDeclarationCount = 0 @>
        // Both root baselines — and NOT `.fshw.json`, which the tool already hashes and
        // which is deduplicated rather than counted twice.
        test <@ tree.DeclaredCount = 2 @>

        File.WriteAllText(Path.Combine(root, "floors.json"), """{"a": 0}""")
        test <@ (TreeHash.compute root []).Hash <> tree.Hash @>)

[<Fact>]
let ``an UNREADABLE .fshw.json declares nothing — and the hash still fails closed on it`` () =
    // The config cannot be read, so its declarations cannot be honoured. That is not
    // laundered into "declares nothing is fine": `.fshw.json` is itself a hashed input,
    // so `ContentHash` marks it unhashable and the tree stops matching any prior
    // verdict. The refusal lives in one place rather than two.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "a165-unreadable-config" (fun root ->
            makeRepo root
            let config = FsHwPaths.configFile root

            File.WriteAllText(config, """{"verdictInputs": {"hashed": [{"path": "src", "why": "everything"}]}}""")

            let readable = TreeHash.compute root []
            test <@ (VerdictInputs.read root).Hashed.Length = 1 @>

            File.SetUnixFileMode(config, UnixFileMode.None)

            try
                test <@ (VerdictInputs.read root).Hashed |> List.isEmpty @>
                test <@ (TreeHash.compute root []).Hash <> readable.Hash @>
            finally
                File.SetUnixFileMode(config, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a repo root that cannot be listed contributes no tool-known inputs rather than throwing`` () =
    // Hashing a tree must not become a new way to crash. A root we cannot enumerate
    // still fails CLOSED elsewhere — the discovery walk reports the hole, and `.fshw.json`
    // hashes unreadable — so the honest answer here is an empty list, not an exception
    // that takes the whole verdict down.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "a165-blind-root" (fun root ->
            File.WriteAllText(Path.Combine(root, "global.json"), """{"sdk": {"version": "10.0.0"}}""")
            test <@ (VerdictInputs.toolKnownInputs root).Length = 1 @>

            File.SetUnixFileMode(root, UnixFileMode.None)

            try
                test <@ VerdictInputs.toolKnownInputs root |> List.isEmpty @>
            finally
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

[<Fact>]
let ``globWalkPrefix names the shallowest directory a glob must be walked from`` () =
    // The walk root, not the pattern: `analyzers/**/*.fs` cannot be answered without
    // walking `analyzers`, and walking the whole repo for it would be the difference
    // between one directory and a hundred thousand files on every `fshw verdict`.
    test <@ VerdictInputs.globWalkPrefix "analyzers/**/*.fs" = "analyzers" @>
    test <@ VerdictInputs.globWalkPrefix "a/b/*.fs" = "a/b" @>
    test <@ VerdictInputs.globWalkPrefix "a/*/c.fs" = "a" @>
    test <@ VerdictInputs.globWalkPrefix "*.json" = "" @>
