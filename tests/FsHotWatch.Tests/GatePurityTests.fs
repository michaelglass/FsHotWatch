/// A gate must be a PURE FUNCTION OF THE TREE IT JUDGES.
///
/// A verdict is content-addressed by that tree (`TreeHash`), and `.fshw.json`
/// `verdictInputs.hashed` names the gate-deciding files the discovery walk does not
/// reach — the coverage floors among them, declared for exactly this reason: "Lower one
/// and a verdict earned under the higher floor must stop applying; hashing it is the
/// only thing that makes that true." A step INSIDE the gate that rewrites one of those
/// files therefore moves the subject of the claim the gate is making, and `fshw verdict`
/// answers `StaleTree` about this repository's own check.
///
/// `mise run check` used to depend on `coverage-ratchet`, which did exactly that — and
/// did something worse on the way. When a file had drifted BELOW its floor the wrapper
/// fell back to `coverageratchet loosen`, so the inner-loop gate answered a coverage
/// regression by lowering the threshold until it passed: its success was unconditional.
/// Measured on an UNMODIFIED tree on 2026-09-21, the first `mise run check` of a fresh
/// workspace printed "Coverage below threshold for: CheckPipeline.fs,
/// OperationWatchdog.fs" and then "Loosen complete: thresholds set to current coverage",
/// lowering `OperationWatchdog.fs` branch 100 -> 91 — against a `reason` field in that
/// very entry reading "Do not re-tighten from a single lucky run." It also RAISED
/// `DepsFreshness.fs` from one local measurement, which is the move that left a
/// 2026-08-07 repair note on 32 of the file's 131 floor entries.
///
/// Observing a violation and repairing it are two jobs, and only the first belongs
/// inside a check. The plugin already knew this: `CoveragePlugin` serves
/// `coverage-ratchet` as an explicit IPC command that takes the check's own exclusive
/// slot, "because a check READS the config the ratchet REWRITES". It was the task graph
/// that put the writer in the reader's path. These tests pin the separation to the file
/// so it cannot be undone by adding one `depends` entry.
module FsHotWatch.Tests.GatePurityTests

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The tasks that decide a merge: the local inner-loop gate, and the one CI runs. Both
/// are named deliberately — the defect reached `check` alone, and "local green, CI red"
/// is the shape it took.
let private gateTasks = [ "check"; "ci" ]

/// The fast LANDING gate (2026-11-12 fast-gate rule): `check`'s command without its direct
/// coverage run. It consults no coverage at all — floors are graded by the post-land spot
/// check — so it is held to every purity rule below except the ones that require coverage
/// to be PRESENT.
let private landingGate = "land-check"

/// Every task that decides a merge, the landing gate included.
let private everyGate = gateTasks @ [ landingGate ]

/// `coverageratchet` verbs that only READ the floors file. Everything else the tool
/// offers (`ratchet`, `loosen`, `loosen-from-ci`, `baseline-lines`, `refresh-baseline`)
/// writes, and writing is what a gate may not do to its own inputs.
let private readOnlyRatchetVerbs =
    Set.ofList [ "check"; "check-json"; "targets"; "gaps" ]

let private ratchetVerbsIn mise taskName =
    Regex.Matches(commandLines mise taskName, @"coverageratchet\s+(\S+)")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.toList

/// The floors files in the repository root, by file name.
let private floorsFiles root =
    Directory.GetFiles(root, "coverage-ratchet-*.json")
    |> Array.map Path.GetFileName
    |> Array.sort
    |> Array.toList

/// `.fshw.json`'s coverage floor sets: each floors file and the test projects whose
/// run-scoped reports it grades (`[]` for a set with another report source).
let private daemonFloorSets root =
    use config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".fshw.json")))
    let coverage = config.RootElement.GetProperty("coverage")

    let sets =
        if coverage.ValueKind = JsonValueKind.Array then
            coverage.EnumerateArray() |> Seq.toList
        else
            [ coverage ]

    sets
    |> List.map (fun set ->
        let projects =
            match set.TryGetProperty("projects") with
            | true, ps -> ps.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            | false, _ -> []

        set.GetProperty("configPath").GetString(), projects)

/// Whether `gate`'s own command runs `confirm`, the full-suite run whose coverage plugin
/// grades `.fshw.json`'s floor sets.
let private runsConfirm mise gate =
    Regex.IsMatch(commandLines mise gate, @"FsHotWatch\.Cli\b[^\n&]*--\s+confirm\b")

/// Shell redirection, or one of the in-place-write commands a mise task could plausibly
/// reach for, AIMED AT `path`. Text analysis, and it says so: see the test that uses it.
let private writesTo (line: string) (path: string) =
    line.Contains path
    && (Regex.IsMatch(line, @">>?\s*\S*" + Regex.Escape path)
        || Regex.IsMatch(line, @"\b(tee|sed\s+-i|mv|cp)\b[^\n]*" + Regex.Escape path))

[<Fact>]
let ``no task a gate depends on invokes a coverageratchet verb that writes`` () =
    let mise = miseToml (repoRoot ())

    for gate in everyGate do
        let verbs =
            dependencyClosure mise gate |> Set.toList |> List.collect (ratchetVerbsIn mise)

        // PRESENT, not merely absent: a gate that consults coverage at all must be seen
        // to do it, so a graph that dropped the check entirely cannot pass this by
        // saying nothing. `ci` grades in its own `confirm` (pinned by the next test);
        // `check` runs an impact-filtered suite, which grades nothing, so it keeps the
        // direct run. The landing gate consults none, by design.
        test
            <@
                not (List.isEmpty verbs)
                || (gate = "ci" && runsConfirm mise gate)
                || gate = landingGate
            @>

        let writing =
            verbs |> List.filter (fun v -> not (Set.contains v readOnlyRatchetVerbs))

        test <@ List.isEmpty writing @>

[<Fact>]
let ``the writing ratchet tasks exist, and no gate depends on them`` () =
    let mise = miseToml (repoRoot ())

    // Tightening and loosening did not disappear; they became things a person asks for,
    // in a commit that is reviewable and that re-earns a verdict — because the floors
    // file is a hashed verdict input, moving it invalidates the green earned under the
    // old floors, which is the behaviour the declaration was added for.
    let writers = [ "coverage-ratchet"; "coverage-loosen"; "loosen-from-ci" ]
    let declared = taskNames mise
    let missing = writers |> List.filter (fun w -> not (List.contains w declared))

    test <@ List.isEmpty missing @>

    for gate in everyGate do
        let closure = dependencyClosure mise gate
        let reachable = writers |> List.filter (fun w -> Set.contains w closure)
        test <@ List.isEmpty reachable @>

[<Fact>]
let ``the tighten task cannot loosen`` () =
    // The old wrapper ran `ratchet`, read exit 2 — "a file is below its floor" — and
    // answered it with `loosen`. A command that repairs the thing it just measured
    // reports nothing.
    let mise = miseToml (repoRoot ())

    test <@ ratchetVerbsIn mise "coverage-ratchet" = [ "ratchet" ] @>

[<Fact>]
let ``ci grades every floors file in the confirm CI runs, on that run's own reports`` () =
    // `ci` used to run the suite TWICE: `test-direct` and `tp-test` only to write the
    // reports `coverage-check` / `tp-coverage-check` graded, then `confirm` again for the
    // verdict (AUTOMATION-1642: 368 s + 112 s of a 1172 s gate). The reports had to come
    // from a run of their own because the daemon's were shared files a later run — or a
    // second daemon alive from an earlier gate — rewrote: 113 files graded instead of 134,
    // and a pass. Now `confirm` grades every floors file itself, each on the report its
    // project wrote into that run's own directory, which nothing else writes; and the
    // command is the one GitHub CI runs (`CiParityTests`), so the two gates cannot drift.
    let root = repoRoot ()
    let mise = miseToml root
    let sets = daemonFloorSets root

    test <@ runsConfirm mise "ci" @>
    test <@ not (List.isEmpty (floorsFiles root)) @>
    test <@ (sets |> List.map fst |> List.sort) = floorsFiles root @>
    test <@ sets |> List.forall (fun (_, projects) -> not (List.isEmpty projects)) @>

[<Fact>]
let ``ci runs the test suite once`` () =
    // The direct coverage runs stay for what they are still for — `mise run check`, whose
    // impact-filtered suite grades nothing, and the explicit ratchet/loosen tasks — but
    // not in `ci`, where `confirm` has already run every project with coverage.
    let mise = miseToml (repoRoot ())
    let closure = dependencyClosure mise "ci"

    test <@ not (Set.contains "test-direct" closure) @>
    test <@ not (Set.contains "tp-test" closure) @>

[<Fact>]
let ``the inner loop still grades coverage with the direct run`` () =
    let mise = miseToml (repoRoot ())
    test <@ Set.contains "coverage-check" (dependencyClosure mise "check") @>

[<Fact>]
let ``no gate task redirects, tees or copies onto a declared verdict input`` () =
    // A tripwire for the NEXT writer, which will not be `coverageratchet`. Named for
    // exactly what it inspects and no wider: it reads the task TEXT for shell
    // redirection and for four in-place-write commands, aimed at a path `.fshw.json`
    // declares gate-deciding. It does NOT establish that the gate writes nothing — a
    // tool that rewrites a declared input from inside its own process is invisible to
    // it, which is why the one that did is pinned by name above rather than left to
    // this.
    let root = repoRoot ()
    let mise = miseToml root
    let declared = declaredVerdictInputs root

    test <@ not (List.isEmpty declared) @>

    let violations =
        [ for gate in everyGate do
              for taskName in dependencyClosure mise gate do
                  for line in (commandLines mise taskName).Split('\n') do
                      for path in declared do
                          if writesTo line path then
                              yield $"%s{taskName}: %s{path} in %s{line.Trim()}" ]

    test <@ List.isEmpty violations @>

[<Fact>]
let ``the landing gate runs check's command without any direct test or coverage run`` () =
    // `check`'s `coverage-check` → `test-direct` runs the whole FsHotWatch.Tests suite under
    // coverage (~420 s) before the daemon's filtered run starts. The landing gate exists to
    // drop exactly that; one `depends` entry would quietly put it back.
    let mise = miseToml (repoRoot ())
    let closure = dependencyClosure mise landingGate

    let direct =
        [ "test-direct"; "coverage-check"; "tp-test"; "tp-coverage-check" ]
        |> List.filter (fun task -> Set.contains task closure)

    test <@ List.isEmpty direct @>

    let runLine task =
        (commandLines mise task).Split('\n')
        |> Array.tryFind (fun line -> line.StartsWith("run =", System.StringComparison.Ordinal))

    test <@ (runLine "check").IsSome @>
    test <@ runLine landingGate = runLine "check" @>
    test <@ Set.contains "compile" closure @>

    // AUTOMATION-1700: `compile` is no longer a bare `dotnet build`. It runs before the daemon,
    // so it settles stale reference assemblies itself, through the build plugin's own
    // `RefAssemblyFreshness` (scripts/compile.sh -> settle-refs.fsx; CompileScriptTests).
    test <@ runLine "compile" = Some "run = \"bash scripts/compile.sh\"" @>

/// The directory `test-direct` writes its Cobertura report into, relative to the repo root.
let private testDirectReportDir mise =
    let m =
        Regex.Match(commandLines mise "test-direct", @"--coverage-output\s+""?\$PWD/(\S+?)/coverage\.cobertura\.xml")

    if m.Success then Some m.Groups[1].Value else None

[<Fact>]
let ``every coverageratchet task reads only the report test-direct writes`` () =
    // With no `--search-dir`, coverageratchet walks the tree from the repo root and merges
    // EVERY coverage.cobertura.xml it finds (hidden directories aside), keyed by file
    // basename. A CI report downloaded into the gitignored `tmp/` (an older version of
    // the same files, at other line numbers) then merged into the fresh one: measured
    // 2026-10-17, FileStamp.fs read 15/18 branches and failed its floor while the report
    // test-direct had just written said 10/10.
    let mise = miseToml (repoRoot ())
    let reportDir = testDirectReportDir mise

    test <@ reportDir.IsSome @>

    let invocations =
        [ for taskName in taskNames mise do
              for line in (commandLines mise taskName).Split('\n') do
                  if Regex.IsMatch(line, @"coverageratchet\s+\S") then
                      yield taskName, line.Trim() ]

    test <@ not (List.isEmpty invocations) @>

    let required = $"--search-dir %s{reportDir.Value}"

    // TestPrune's tasks (tp-*, AUTOMATION-1367) grade TestPrune's floors against the reports
    // `tp-test` writes into coverage-tp/<project>/, a directory nothing else writes.
    let tpRequired = "--search-dir \"coverage-tp/$project\""

    test <@ (commandLines mise "tp-test").Contains "--coverage-output \"$PWD/coverage-tp/$project_name/" @>

    let unscoped =
        invocations
        |> List.filter (fun (task, line) ->
            not (Regex.IsMatch(line, Regex.Escape required + @"(\s|""|'|$)"))
            && not (
                task.StartsWith("tp-", System.StringComparison.Ordinal)
                && line.Contains tpRequired
            ))

    test <@ List.isEmpty unscoped @>

/// Whether one repo-relative directory is the other or sits inside it, compared by path
/// segment, so `coverage-direct` is not mistaken for a child of `coverage`.
let private overlaps (a: string) (b: string) =
    let segments (path: string) =
        path.Split([| '/'; '\\' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter ((<>) ".")
        |> Array.toList

    let rec isPrefix xs ys =
        match xs, ys with
        | [], _ -> true
        | x :: xs', y :: ys' when x = y -> isPrefix xs' ys'
        | _ -> false

    let sa, sb = segments a, segments b
    isPrefix sa sb || isPrefix sb sa

[<Fact>]
let ``test-direct writes its report where the daemon never writes`` () =
    // The daemon's TestPrune emits a shared coverage.cobertura.xml under `tests.coverageDir`
    // after EVERY test run it makes, including the impact-filtered ones. When test-direct
    // wrote to that same directory, a daemon still alive from an earlier gate rewrote the
    // report between test-direct and coverage-check: measured 2026-10-16, the check graded
    // 113 files instead of 134, every FsHotWatch.Cli file absent, and passed. A report the
    // gate reads must be one only the gate wrote.
    let root = repoRoot ()
    let mise = miseToml root
    let reportDir = testDirectReportDir mise
    let daemonDir = daemonCoverageDir root

    test <@ reportDir.IsSome @>
    test <@ not (overlaps reportDir.Value daemonDir) @>

    // And test-direct's own cleanup wipes only its own directory, never the daemon's.
    let removed =
        Regex.Matches(commandLines mise "test-direct", @"rm\s+-rf\s+""?([^\s""]+)")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.toList

    test <@ removed = [ reportDir.Value ] @>

[<Fact>]
let ``a traced test project measures the modules its plain launch measures`` () =
    // `confirm` launches TestPrune.Tests TRACED (`tests.traces`): its woven host loads the
    // trace recorder, which a plain `dotnet test` of the project never does. Graded on
    // that run's own report, the recorder's 12 files appeared with no floor (the 100%
    // default) and failed, a file set the direct run — and hosted CI's — never had.
    // Measured 2026-11-12 on one build: 46 files traced against 34 direct, the 34 equal
    // line for line. The project's own filter excludes the recorder, so both launches
    // grade the same files.
    let root = repoRoot ()

    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "TestPrune.Tests", "testconfig.json")))

    let excludes =
        config.RootElement
            .GetProperty("codeCoverage")
            .GetProperty("Configuration")
            .GetProperty("CodeCoverage")
            .GetProperty("ModulePaths")
            .GetProperty("Exclude")
            .EnumerateArray()
        |> Seq.map _.GetString()
        |> Seq.toList

    let recorder =
        "/repo/tests/TestPrune.Tests/bin/Traced/net10.0/TestPrune.Trace.Recorder.dll"

    let core = "/repo/tests/TestPrune.Tests/bin/Traced/net10.0/TestPrune.Core.dll"

    test <@ excludes |> List.exists (fun pattern -> Regex.IsMatch(recorder, pattern)) @>
    test <@ not (excludes |> List.exists (fun pattern -> Regex.IsMatch(core, pattern))) @>
