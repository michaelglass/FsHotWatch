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

/// A run that EXECUTED but reported nothing — an EMPTY directory. This is the case
/// the whole layout exists for: it is a stated fact ("this run ran no tests"), not a
/// silence that a reader has to decode.
let private emptyRun (root: string) (runId: Guid) =
    Directory.CreateDirectory(Ctrf.runDir root runId) |> ignore

/// What a test wants to SAY about a verdict.
///
/// The verdict itself is no longer hand-assemblable: `Verdict.create` is the only door,
/// it stamps `producedAt` and the producer from the running process, and it REFUSES a
/// green carrying a failing plugin. So a test declares its claim as a spec and lets the
/// constructor enforce the invariant — which is the point. A test that could still
/// hand-build `{outcome: green, plugins: [fail]}` would be testing a type that has been
/// deliberately abolished.
type private Spec =
    { Command: Verdict.Command
      RunId: Guid option
      Scope: TestScope
      Outcome: Verdict.Outcome
      ExitCode: int
      Plugins: Verdict.PluginVerdict list
      Suites: Verdict.SuiteVerdict list
      Tree: TreeHash.Tree }

let private build (s: Spec) : Verdict.Verdict =
    Verdict.create s.Command { Scope = s.Scope; RunId = s.RunId } s.Tree s.Outcome s.ExitCode s.Plugins s.Suites

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
      Tree =
        { Hash = treeHash
          FileCount = fileCount } }

let private writeSpec (root: string) (s: Spec) : unit = Verdict.write root (build s)
let private serializeSpec (s: Spec) : string = Verdict.serialize (build s)

let private hintsFor (s: Spec) : string list =
    ProgressRenderer.AgentHints.forVerdict (build s)

/// A verdict FILE that claims a DIFFERENT fshw produced it.
///
/// fshw cannot build one — `Verdict.create` stamps the running binary's identity, and a
/// caller that could supply it is a caller that could lie about it. So this forges the
/// artifact on disk, which is the only way one can honestly exist: another build wrote
/// it. Patching the serialized JSON keeps the test on the real wire format rather than
/// inventing a second one.
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

        // A green, earned over THIS tree.
        let before = TreeHash.compute root []
        writeSpec root (greenVerdict before.Hash before.FileCount)

        // CONTROL: it applies RIGHT NOW. Without this, a test that only proved
        // "stale after an edit" would also pass if the verdict never applied at all.
        match Verdict.report root [] with
        | Verdict.Report.Applies v ->
            test <@ v.Outcome = Verdict.Green @>
            test <@ Verdict.reportExitCode (Verdict.Report.Applies v) = 0 @>
        | other -> failwith $"expected the verdict to apply to the tree it was earned on, got %A{other}"

        // Now edit a source file. Nothing else changes: same verdict file, same
        // daemon, same everything. The verdict is now a claim about a tree that no
        // longer exists.
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet answer = 43\n")

        match Verdict.report root [] with
        | Verdict.Report.Stale(v, currentTree) ->
            // It is STILL a green verdict on disk — and that is precisely the point:
            // a green from a different tree is still a green, so the reader must be
            // told it does not apply, not left to notice.
            test <@ v.Outcome = Verdict.Green @>
            test <@ currentTree <> before.Hash @>
            test <@ Verdict.reportExitCode (Verdict.Report.Stale(v, currentTree)) = 4 @>
        | other -> failwith $"a verdict from a different tree must be STALE, got %A{other}")

[<Fact>]
let ``a changed CONTENT fixture makes the verdict stale — the dsa-scope-4 false green`` () =
    // APPLIC-24, exactly. `dsa-scope-4.json` changed (36 -> 40 leaf facts); MSBuild
    // deemed the consuming test project up-to-date and SKIPPED the copy, so the
    // suite ran against the OLD fixture and passed — 5136 tests, 0 failed. Fake
    // green, merge landed, main red for hours.
    //
    // A fixture is an input to the verdict exactly as a source file is, so it is
    // inside the tree hash exactly as a source file is: change it, and the previous
    // green stops applying. No MSBuild involved, nothing to remember.
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

        // `producedAt` is stamped by `create` (it is a fact about the producing process,
        // not something a caller supplies) and round-trips through ISO-8601 "O" at full
        // tick precision — so equality below tests the CONTRACT, not the clock.
        let written =
            build
                { greenVerdict tree.Hash tree.FileCount with
                    Command = Verdict.Check
                    Scope = ImpactFiltered(2, 6)
                    Outcome = Verdict.Red
                    ExitCode = 1
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
            // EVERY field, not just the ones the exit code happens to need. A read
            // that silently drops `plugins` and `suites` makes `fshw verdict` a LOSSY
            // re-serialization of the file it claims to be reporting — an agent piping
            // it would see `"suites": []` for a run that produced reports, and go
            // looking for them itself. Which is the whole bug.
            test <@ v = written @>
        | other -> failwith $"expected a readable verdict, got %A{other}")

[<Fact>]
let ``a confirm verdict round-trips as a CONFIRM — the writer and the reader agree on the token`` () =
    // AUTOMATION-160. The `command` field is a WIRE token: `Verdict.write` emits
    // `Command.token`, and `Verdict.read` matches a string literal back. Nothing makes
    // the two sides agree except this test.
    //
    // The failure mode is silent and it under-claims in a way no other test would
    // catch: the reader's `else` branch is `Check`, so a writer emitting "confirm"
    // against a reader still looking for "gate" parses a full-suite merge verdict back
    // as an impact-scoped inner-loop one — a DOWNGRADE, with no parse error, no
    // exception, and a green exit code. That is precisely the class of quiet
    // disagreement this verb exists to catch, so it may not live in the verb's own
    // serializer.
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
    // The shape a reader can act on without first discriminating a JSON string
    // from a JSON object. A shape you have to sniff is a shape that invites a regex.
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
    // `confirm`'s whole reason for existing: an impact-filtered run is not
    // the claim a merge needs. Nothing is reported broken — and nothing is
    // reported sound either. That must survive the trip to disk.
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
    // "Waiting on build" is INCOMPLETE (exit 2) in the file — never Red, never
    // Green. This is the 224 reclassification: a deferred project is a retry
    // signal, not a test failure.
    test <@ incomplete CheckVerdict.CheckOutcome.WaitingOnBuild @>
    test <@ Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.WaitingOnBuild <> Verdict.Red @>
    test <@ Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.WaitingOnBuild <> Verdict.Green @>
    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope NoTestsRun) @>
    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope ScopeUnknown) @>
    test <@ incomplete (CheckVerdict.CheckOutcome.UnearnedScope(FullSuite 2)) @>

[<Fact>]
let ``waiting on build persists as a DISTINCT incomplete verdict (exit 2), never red`` () =
    // The deploy preflight reads `.fshw/verdict.json`'s STRUCTURED outcome, never
    // the prose. A `TestsDeferred` (build-ordering) run must serialize as
    // `outcome.kind = "incomplete"` with a "waiting on build" reason — distinct
    // from the `red` a real test failure earns — and carry exit 2.
    let outcome = Verdict.outcomeOfCheck CheckVerdict.CheckOutcome.WaitingOnBuild

    match outcome with
    | Verdict.Incomplete reason -> test <@ reason.ToLowerInvariant().Contains "waiting on build" @>
    | other -> failwith $"waiting on build must be incomplete, never red/green, got %A{other}"

    test <@ CheckVerdict.exitCode CheckVerdict.CheckOutcome.WaitingOnBuild = 2 @>

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

        // Build output must never move the hash — otherwise every build would
        // invalidate the verdict it just earned.
        Directory.CreateDirectory(Path.Combine(root, "src", "Lib", "obj")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib", "obj", "gen.fs"), "// generated")
        Directory.CreateDirectory(Path.Combine(root, "src", "Lib", "bin")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Lib", "bin", "Lib.dll"), "binary")

        test <@ (TreeHash.compute root []).Hash = baseline.Hash @>

        // A config-excluded file must not move it either.
        File.WriteAllText(Path.Combine(root, "src", "Lib", "Scratch.fs"), "module Scratch")
        test <@ (TreeHash.compute root [ "src/Lib/Scratch.fs" ]).Hash = baseline.Hash @>
        // ...but WITHOUT the exclude it does. (Control: proves the exclude, not
        // an inert hash.)
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

        // Another run's reports. They sit right there on disk, and under the old flat
        // layout the only way to exclude them was to sort by mtime and hope. Now they
        // are simply in a different directory, so they are not mine. Full stop.
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
    // The absence that fooled two readers. It now has a shape: the run-dir EXISTS
    // (the run happened) and is EMPTY (it tested nothing). Neither "cleaned up" nor
    // "wrong glob" is a possible reading.
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
    // The number must not depend on the CTRF file still being readable. A count that
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

        // The dead formats, loose at the top level: a flat CTRF (which nothing could
        // attribute to a run) and a `.log` (written only on failure, so its date read
        // as "when tests last ran" — it said 2026-06-30, and tests had run hundreds of
        // times since).
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
    // Housekeeping "must never fail the run that produced the evidence" — its own words.
    // But it enumerated the run directory TWICE and applied the second enumeration's
    // COUNT to the first enumeration's LIST:
    //
    //     runDirs repoRoot |> List.skip (min keepRuns (List.length (runDirs repoRoot)))
    //
    // A run directory appearing between the two (a second fshw process, a concurrent
    // workspace, a parallel test project finishing its own run) makes the skip count
    // exceed the list — `List.skip` raises `ArgumentException`, which the enclosing
    // `IOException | UnauthorizedAccessException` handler does not catch. The tidy then
    // faults the very run it was tidying up after.
    //
    // So: hammer it. A creator racing the tidy, and the tidy must survive every one.
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

    test <@ text.Contains "AGENTS: don't parse this output" @>
    test <@ text.Contains ".fshw/verdict.json" @>
    test <@ text.Contains "treeHash" @>
    // The ACTUAL paths for THIS run — not a generic pointer. A hint that makes you
    // go and find the file is a hint you will ignore.
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

    let confirmed =
        { greenVerdict "sha256:abc" 12 with
            Command = Verdict.Confirm
            Scope = ImpactFiltered(2, 6) }

    test <@ not ((hintsFor full |> String.concat "\n").Contains "fshw confirm") @>
    test <@ not ((hintsFor confirmed |> String.concat "\n").Contains "fshw confirm") @>

[<Fact>]
let ``a run with no suites SAYS so rather than pointing at nothing`` () =
    let text = hintsFor (greenVerdict "sha256:abc" 12) |> String.concat "\n"

    test <@ text.Contains "NO TEST RUN" @>

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
    // They are the SAME function. This pins that they stay so: a plugin cannot
    // report `ok` on one surface and `fail` on the other.
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
// Every one of these is a way of NOT having a usable answer. None of them may be
// rounded to a green — that rounding is the entire bug this subsystem exists to
// prevent — so each arm is pinned rather than left to a default.
// ---------------------------------------------------------------------------

[<Fact>]
let ``every scope round-trips through the file`` () =
    withTempDir "verdict-scopes" (fun root ->
        let roundTrip (scope: TestScope) =
            writeSpec
                root
                { greenVerdict "sha256:abc" 1 with
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

        let unreadable (scope: TestScope) =
            match scope with
            | ScopeUnreadable _ -> true
            | _ -> false

        // POSITIVE CONTROL: the reader CAN produce a plain `ScopeUnknown` — from the one
        // input that means it. Without this, "everything is unreadable" would pass on a
        // reader that had simply stopped recognizing anything.
        test <@ write """{"kind":"unknown"}""" = ScopeUnknown @>

        // A kind from a future fshw; a `full` whose counts disagree (which is not a full
        // suite, whatever it calls itself); a scope that is not even an object. Every one
        // is a reading this build could not make — which is a different fact from the
        // daemon reporting that there was no scope, and is no longer spelled the same way.
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
            // The verdict's OWN outcome must agree with the plugin's — `Verdict.create`
            // refuses a green carrying a failing plugin, which is the whole point of it.
            // So a failing plugin here rides a red verdict, exactly as it would in life.
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

        // An outcome token this build has never heard of. It is NOT dropped and NOT
        // rounded to `ok`: an unknown state is not a passing state. The verdict carrying
        // it is `red`, so the file is internally consistent and READABLE — the point
        // here is the TOKEN, and a green would confound it with the invariant below.
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
    // THE contradiction, on disk. `{"outcome":"green", "plugins":[{"outcome":"fail"}]}`
    // is what the `--run-once` path used to WRITE when a plugin crashed, and any reader
    // lifting the `green` out of it would be reading a check that never ran.
    //
    // `Verdict.create` makes it unbuildable; `Verdict.read` makes it unliftable. Both
    // doors, because a file can also arrive from a hand edit, a future schema, or a
    // build whose two surfaces have drifted apart again.
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
    // The cache-replay shape: the plugin is Idle, but it HAS a verdict from the run
    // whose result was replayed. Reading Idle as "nothing to say" would drop a real
    // failure out of the verdict entirely.
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
    // A file the daemon cannot read is a file we cannot claim to have verified. It
    // hashes to a marker, which is NOT the hash of the same tree with the file
    // readable — so the verdict reads STALE rather than green over a tree we could
    // not actually see. (Dropping it would fail OPEN: the hash would match, and the
    // verdict would apply to a tree containing a file nobody looked at.)
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

// ---------------------------------------------------------------------------
// CTRF — malformed reports are never counted as evidence.
// ---------------------------------------------------------------------------



[<Fact>]
let ``the status hint lists the LATEST RUN's reports — not a pile spanning many runs`` () =
    withTempDir "hint-status-many" (fun root ->
        makeRepo root

        // An older run. Its reports are real, and under the flat layout they would have
        // been mixed into the listing with no way to tell them apart.
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
// The reader used to default `elapsedMs` to 0L and every suite count to 0. That is
// the AUTOMATION-99 signature — `started:` with no `elapsed:` — rebuilt inside the
// very file that exists to prevent it. `0` is a MEASUREMENT ("this ran
// instantaneously"); absence is the absence of one. And `total: 0, failed: 0`
// manufactured from missing fields reads as "this suite ran cleanly" — a vacuous
// green conjured out of a truncated file.
//
// These cases cannot arise from our own writer, which is exactly why they need
// pinning: they arise from the files a verdict exists to SURVIVE — truncated,
// hand-edited, half-written, or from an older schema.
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
// `treeHash` addresses the verdict's SUBJECT. Without the producer, a STALE DAEMON —
// an older, buggier fshw — writes a verdict for an UNCHANGED tree, the treeHash
// matches, and the verdict reads as current. The provenance chain has a hole in the
// middle. (AUTOMATION-147 makes the same argument for the daemon handshake; this is
// its file-layer half.)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a verdict produced by a DIFFERENT binary does not apply — even with a matching tree`` () =
    withTempDir "verdict-producer" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        // Same tree. Different fshw.
        //
        // `Verdict.create` STAMPS the producer from the running process, so this verdict
        // cannot be built here — and that is correct: it is a foreign artifact, and the
        // only way to have one is for a foreign binary to have written it. So the test
        // writes the FILE, exactly as that other build would have.
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
                ExitCode = 1 }

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
// The two hard integration points. Each is a place where a SECOND answer to an
// existing question would have been the easy thing to ship — and a second answer is
// the disease this release is the cure for.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the verdict's producer IS the daemon's BinaryIdentity — not a fourth binary hash`` () =
    // 147 hashes the binary to decide "restart the daemon?"; the verdict hashes it to
    // decide "does this claim apply?". ONE hash, one sentinel — two conclusions, each
    // stated. A second hasher with a second sentinel policy is exactly what
    // AUTOMATION-155 exists to stamp out; the verdict must not be the fourth.
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
    // 147 gave the status line a `Wedged` token. Because the status line and the
    // verdict file are two renderings of ONE value, the file gets it for free — and a
    // consumer polling `.fshw/verdict.json` can never read an 8h36m wedge as "still
    // running, be patient".
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
    // `totalProjects: 0` for a repo with six test projects is a fabricated number, and
    // "a missing number is not zero" is this file's own rule. `kind: "none"` carries the
    // load-bearing fact; the counts are simply absent.
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
// not moved the honest answer to "is the suite green?" was settled the first time. It
// used to refuse anyway (exit 3, "NO TESTS RAN") — a false NEGATIVE, and the exact
// inverse of the failure this release is about: not a green without evidence, but a
// REFUSAL despite evidence.
//
// The fast path runs through the ONE artifact built to be trusted across a process
// boundary: the verdict file, content-addressed to its SUBJECT (`treeHash`) and its
// PRODUCER. Both must match, and the verdict must be a FULL-SUITE GREEN. Everything else
// is `MustEarn`, and these tests pin each road to it — because a fast path that fails
// open is not a fast path, it is the bug with better latency.
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
            // It must NAME its evidence — a green a reader cannot audit is a green a
            // reader has to take on trust.
            let described = Verdict.describeStillApplies v
            test <@ described.Contains "still applies" @>
            test <@ described.Contains "treeHash + producer match" @>
            test <@ described.Contains "full suite" @>
            test <@ described.Contains "1965 passed" @>
        | Verdict.PriorConfirmation.MustEarn -> failwith "a full-suite green over this very tree must still apply")

[<Fact>]
let ``AUTOMATION-161: a CHANGED tree is never satisfied by the stale verdict`` () =
    // The converse, and the one that matters most: edit a byte and the green is not an
    // answer any more. `confirm` must go and earn a new one.
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
    withTempDir "confirm-filtered" (fun root ->
        makeRepo root
        let tree = TreeHash.compute root []

        writeSpec
            root
            { greenVerdict tree.Hash tree.FileCount with
                Scope = ImpactFiltered(1, 4) }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: NoTestsRun is an absence of evidence, and stays a refusal`` () =
    // The bug was that a replayed full-suite pass got MISREPORTED as `NoTestsRun`. The
    // rule itself does not move: nothing ran ⇒ nothing was verified ⇒ never a green, and
    // never a shortcut past the run either.
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
                ExitCode = 1 }

        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

[<Fact>]
let ``AUTOMATION-161: no verdict on disk means earn one — a fresh checkout is not green`` () =
    withTempDir "confirm-no-verdict" (fun root ->
        makeRepo root
        test <@ Verdict.priorConfirmation root [] = Verdict.PriorConfirmation.MustEarn @>)

// ---------------------------------------------------------------------------
// A RED run whose test table is all zeros. The failure lives in a plugin, and
// the agent hint block used to omit plugins entirely — so the reader saw six
// passing projects on a failing run and concluded the red belonged to someone
// else. That happened twice in one day before anyone opened `plugins`.
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
    // The summary carries the actionable detail — a bare plugin name still makes
    // the reader go digging, which is the behaviour this fixes.
    test <@ joined.Contains "3 findings" @>

[<Fact>]
let ``the failing-plugin hint is NOT a blanket — a green run names no plugin as failing`` () =
    // Positive control. Without this, a change that printed "FAILING" unconditionally
    // would satisfy the test above while telling every reader their green run failed.
    let hints =
        hintsFor
            { greenVerdict "deadbeef" 10 with
                Suites = allSuitesGreen }

    let joined = String.Join("\n", hints)

    test <@ not (joined.Contains "FAILING") @>
    test <@ not (joined.Contains "UNEXPLAINED") @>

[<Fact>]
let ``a red run that names NOTHING says so — a tidy block with a non-zero exit is the worst case`` () =
    let hints =
        hintsFor
            { greenVerdict "deadbeef" 10 with
                Outcome = Verdict.Red
                ExitCode = 1
                Plugins = []
                Suites = allSuitesGreen }

    let joined = String.Join("\n", hints)

    test <@ joined.Contains "UNEXPLAINED" @>
    test <@ joined.Contains "do NOT read this as a pass" @>
