/// Test evidence end to end: several checkouts of one repository, each its own daemon,
/// sharing one box-wide store. Every test counts what was LAUNCHED (a runner that logs
/// each start) and reads what each completion reported, because "reuse" is only sound
/// if it never stands in for a run whose inputs differ.
module FsHotWatch.Tests.TestEvidenceRunTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// ---------------------------------------------------------------------------
// A repository: three libraries, a test project over each, and one shared library
// ---------------------------------------------------------------------------

/// Logs the project it was started for, writes a one-row CTRF report where fshw asked,
/// and fails when `src/<lib>/fail` exists, or `flake-<project>` at the root (outside every
/// closure, so the failure does not move the project's key: a flake). `edit-during-<project>` at the root names a
/// library the runner appends to while it runs: an edit made mid-run.
let internal runner =
    """#!/bin/sh
proj="$1"; shift
name=''; dir=''; cov=''
while [ $# -gt 0 ]; do
  case "$1" in
    --report-ctrf-filename) name="$2"; shift ;;
    --results-directory) dir="$2"; shift ;;
    --coverage-output) cov="$2"; shift ;;
  esac
  shift
done
printf '%s\n' "$proj" >> runs.log
if [ -f "edit-during-$proj" ]; then printf '// edited\n' >> "src/$(cat "edit-during-$proj")/Lib.fs"; fi
lib="src/${proj#T}"
if [ -f "$lib/fail" ] || [ -f "flake-$proj" ]; then status=failed; passed=0; failed=1; code=1; else status=passed; passed=1; failed=0; code=0; fi
if [ -n "$dir" ] && [ -n "$name" ]; then
  printf '{"results":{"summary":{"tests":1,"passed":%s,"failed":%s,"pending":0,"skipped":0,"other":0},"tests":[{"name":"%s.t","status":"%s"}]}}' "$passed" "$failed" "$proj" "$status" > "$dir/$name"
fi
if [ -n "$cov" ] && [ -f "badcov-$proj" ]; then printf 'not coverage' > "$cov"; cov=''; fi
if [ -n "$cov" ]; then
  printf '<?xml version="1.0"?><coverage line-rate="1" branch-rate="1" version="1" timestamp="0"><sources><source>%s</source></sources><packages><package name="%s" line-rate="1" branch-rate="1"><classes><class name="%s" filename="src/%s/Lib.fs" line-rate="1" branch-rate="1"><methods/><lines><line number="2" hits="1"/></lines></class></classes></package></packages></coverage>' "$PWD" "$proj" "$proj" "${proj#T}" > "$cov"
fi
exit $code
"""

let internal write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let internal libs = [ "X"; "Y"; "Z"; "S" ]
let internal testProjects = [ "TX"; "TY"; "TZ" ]

/// TX and TY reference the shared library S as well as their own.
let internal referencesOf =
    Map.ofList [ "TX", [ "X"; "S" ]; "TY", [ "Y"; "S" ]; "TZ", [ "Z" ] ]

let internal fsprojOf (root: string) (name: string) =
    if name.StartsWith "T" then
        Path.Combine(root, "tests", name, name + ".fsproj")
    else
        Path.Combine(root, "src", name, name + ".fsproj")

let internal populate (root: string) =
    write root "runner.sh" runner

    for lib in libs do
        write root $"src/%s{lib}/%s{lib}.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        write root $"src/%s{lib}/Lib.fs" $"module %s{lib}\nlet value = 1\n"

    for t in testProjects do
        write root $"tests/%s{t}/%s{t}.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        write root $"tests/%s{t}/Tests.fs" $"module %s{t}\n"

let internal configs =
    testProjects
    |> List.map (fun t ->
        { Project = t
          Command = "sh"
          Args = $"runner.sh %s{t}"
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = Ctrf
          Starvation = None
          CheckSkip = None
          NameHungTests = false
          Weight = 1.0
          AlwaysClasses = [] })

/// The model the host publishes: every project, its references, and an identity per
/// project that is the same in every checkout (the options do not change in these tests;
/// what changes is content, which the key hashes itself).
let internal graphOf (root: string) : ProjectGraphAccessor =
    let all = (libs @ testProjects) |> List.map (fsprojOf root)

    { ProjectGraphAccessor.none with
        GetAllProjects = fun () -> all
        GetProjectReferences =
            fun fsproj ->
                let name = Path.GetFileNameWithoutExtension fsproj

                Map.tryFind name referencesOf
                |> Option.defaultValue []
                |> List.map (fsprojOf root) }

let internal identitiesOf (root: string) =
    (libs @ testProjects)
    |> List.map (fun name -> fsprojOf root name, "identity:" + name)
    |> Map.ofList

/// One checkout: its root, its daemon, and what it completed.
type internal Checkout =
    { Root: string
      Host: PluginHost
      Completions: Collections.Concurrent.ConcurrentQueue<TestRunCompleted> }

/// Every result any TestProgress carried, in arrival order.
let internal progressSeen =
    Collections.Concurrent.ConcurrentDictionary<string, Collections.Concurrent.ConcurrentQueue<TestResult>>()

let internal completionRecorder () =
    let completions = Collections.Concurrent.ConcurrentQueue<TestRunCompleted>()
    let progress = Collections.Concurrent.ConcurrentQueue<TestResult>()

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "evidence-completion-recorder"
          Init = ()
          Update =
            fun _ state event ->
                async {
                    match event with
                    | TestRunCompleted completed -> completions.Enqueue completed
                    | TestProgress p ->
                        for KeyValue(_, r) in p.NewResults do
                            progress.Enqueue r
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeTestRunCompleted; SubscribeTestProgress ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }

    completions, progress, handler

let internal wiringFor (store: TestEvidenceStore.Store) : TestEvidenceRun.Wiring =
    { Store = store
      Configs =
        testProjects
        |> List.map (fun t ->
            t,
            { TestEvidenceKey.defaultConfig with
                Reads = TestEvidenceKey.Reads.Closure })
        |> Map.ofList
      Toolchain = lazy "test-toolchain"
      Loaded = TestEvidenceKey.LoadedEnvironment.empty
      ProducerHash = "test-producer"
      ExcludePatterns = []
      Outcomes = None }

/// Stand a daemon up over `root` with `evidence` wiring and a scope, as a LONG-LIVED
/// checkout: one that earned a full-suite baseline before these runs, the way the default
/// checkout has by the time anything lands in it. (A checkout owed a baseline consults no
/// evidence at all; `a checkout owed a full-suite baseline` below pins that.)
let internal checkoutSetUp
    (coverage: (string -> string option) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (seeded: bool)
    (root: string)
    (evidence: TestEvidenceRun.Wiring option)
    (confirm: bool)
    =
    if seeded then
        seedBaseline root testProjects

    let host = createModelHost Unchecked.defaultof<_> root
    host.SetProjectGraph(graphOf root)
    host.WorkStore.PublishProjectModelWithInputs(fixtureModel, Set.empty, Some(identitiesOf root))
    let completions, progress, recorder = completionRecorder ()
    progressSeen[root] <- progress
    host.RegisterHandler recorder

    host.RegisterHandler(
        createWithEvidenceAndQueries
            evidence
            ImpactQueries.ofDatabase
            (TimeSpan.FromMinutes 5.0)
            None
            (fun () -> Map.empty)
            (Path.Combine(root, "test-prune.db"))
            TestPrune.Domain.WalkPolicy.FollowAll
            root
            (Some configs)
            None
            beforeRun
            None
            (coverage
             |> Option.map (fun baselineOf ->
                 fun (project: string) ->
                     baselineOf project
                     |> Option.map (fun baseline ->
                         { Baseline = baseline
                           Partial = Path.Combine(root, "coverage", project, PartialName)
                           Cobertura = Path.Combine(root, "coverage", CoberturaName)
                           IncludeInRatchet = true
                           ArgsTemplate = "--coverage-output {output}"
                           RuntimeSelection = SelectionMode.RuntimeDecides })))
            []
            None
            TestMode.initial
    )

    if confirm then
        host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
        |> Async.RunSynchronously
        |> ignore

    { Root = root
      Host = host
      Completions = completions }

let internal checkoutFull coverage = checkoutSetUp coverage None

let internal checkoutWith = checkoutFull None

let internal checkout = checkoutWith true

/// Every test project's coverage graded, its baseline under `coverage/<project>/`.
let internal coverageIn (root: string) =
    Some(fun (project: string) -> Some(Path.Combine(root, "coverage", project, BaselineName)))

let internal launches (c: Checkout) =
    let log = Path.Combine(c.Root, "runs.log")

    if File.Exists log then
        File.ReadAllLines log |> List.ofArray
    else
        []

/// The xUnit cap of a test here: up to three `run`s, each two `HangWaitMs` waits over in-process
/// work and a trivial child, so only a hang reaches it (AUTOMATION-1461).
[<Literal>]
let internal RunsTestCapMs = 3 * SyncHangBackstopMs

/// Run one check and return the projects it LAUNCHED and the results it completed with.
let internal run (c: Checkout) =
    let before = launches c |> List.length
    let completedBefore = c.Completions.Count
    emitBuildAndWaitTerminal c.Host
    waitUntilOr "the run's completion to be recorded" (fun () -> c.Completions.Count > completedBefore) HangWaitMs
    let launched = launches c |> List.skip before |> List.sort
    let completed = c.Completions |> Seq.last
    launched, completed

let internal isReused (result: TestResult) =
    match result with
    | TestsPassed(output, false, _) -> output.StartsWith("reused:", StringComparison.Ordinal)
    | _ -> false

let internal reusedIn (completed: TestRunCompleted) =
    completed.Results
    |> Map.filter (fun _ r -> isReused r)
    |> Map.keys
    |> List.ofSeq
    |> List.sort

/// Every project verified green, in a run that selected at least one: the run-level
/// guard is the run's own verification, since `forall` over no results is vacuously true.
let internal allGreen (completed: TestRunCompleted) =
    let selectedAny = completed.Verification <> NoProjectsSelected

    selectedAny
    && completed.Results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)

/// A store in its own directory, and a root per checkout, all cleaned up together.
let internal withRepo (body: TestEvidenceStore.Store -> (string -> string) -> unit) =
    withTempDir "evrun" (fun dir ->
        let store =
            TestEvidenceStore.Store(
                { Root = Path.Combine(dir, "store")
                  Ttl = TimeSpan.FromHours 72.0
                  MaxBytes = 64L * 1024L * 1024L
                  Now = fun () -> DateTimeOffset.UtcNow
                  Log = ignore }
            )

        let rootNamed (name: string) =
            let root = Path.Combine(dir, name)
            Directory.CreateDirectory root |> ignore
            populate root
            root

        body store rootNamed)

let internal edit (root: string) (lib: string) (line: string) =
    File.AppendAllText(Path.Combine(root, "src", lib, "Lib.fs"), line + "\n")

/// The run a checkout's full-suite baseline names, when it has one.
let internal baselineRunOf (root: string) =
    match FullSuiteBaseline.load root with
    | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> Some baseline.RunId
    | _ -> None

// ---------------------------------------------------------------------------
// Producer and consumer
// ---------------------------------------------------------------------------

[<Fact(Timeout = RunsTestCapMs)>]
let ``a fresh checkout of an earned tree launches nothing and reports every project reused`` () =
    withRepo (fun store rootNamed ->
        let a = checkout (rootNamed "a") (Some(wiringFor store)) false
        let launchedA, completedA = run a
        test <@ launchedA = testProjects @>
        test <@ allGreen completedA @>

        let b = checkout (rootNamed "b") (Some(wiringFor store)) false
        let launchedB, completedB = run b
        test <@ List.isEmpty (launchedB) @>
        test <@ reusedIn completedB = testProjects @>
        test <@ allGreen completedB @>
        test <@ completedB.Verification = Ran FullSuite @>
        // Every project's tests ran at exactly these inputs, here or in A, so the run is a
        // full-suite watermark like A's (AUTOMATION-1508).
        test <@ baselineRunOf a.Root = Some completedA.RunId @>
        test <@ baselineRunOf b.Root = Some completedB.RunId @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a checkout owed a full-suite baseline reuses byte-identical evidence and earns the baseline from it`` () =
    // AUTOMATION-1508. A fresh checkout's first run is a full suite, because it holds no
    // baseline. It looks every project up, launches none whose inputs are byte-identical
    // to a green another checkout earned, and the run earns the baseline: every project's
    // tests ran at these inputs. Before, a fresh checkout of an already-tested tree ran
    // its whole suite again before it reused anything.
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let lookupsBefore = store.Lookups
        let fresh = checkoutWith false (rootNamed "b") (Some(wiringFor store)) false
        let launched, completed = run fresh
        test <@ List.isEmpty launched @>
        test <@ reusedIn completed = testProjects @>
        test <@ store.Lookups = lookupsBefore + List.length testProjects @>
        test <@ allGreen completed @>
        test <@ completed.Verification = Ran FullSuite @>
        test <@ baselineRunOf fresh.Root = Some completed.RunId @>

        // Earned: an edit is selected as usual, and the store is consulted for what the
        // selection launches.
        let lookupsEarned = store.Lookups
        edit fresh.Root "Z" "let z = 3"
        let launchedNext, _ = run fresh
        test <@ launchedNext = [ "TZ" ] @>
        test <@ store.Lookups > lookupsEarned @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a fresh checkout launches every project whose closure differs, and reuses only the identical ones`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // A file APPEARING in TX's closure: TX's key moves, TY's and TZ's do not.
        write b "src/X/Extra.fs" "module Extra\n"
        let launched, completed = run (checkoutWith false b (Some(wiringFor store)) false)
        test <@ launched = [ "TX" ] @>
        test <@ reusedIn completed = [ "TY"; "TZ" ] @>
        test <@ not (isReused completed.Results["TX"]) @>
        test <@ TestResult.verifiedGreen completed.Results["TX"] @>
        test <@ baselineRunOf b = Some completed.RunId @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a fresh checkout of a failing tree never reuses the green of the passing one`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        write b "src/X/fail" ""
        let launched, completed = run (checkoutWith false b (Some(wiringFor store)) false)
        test <@ launched = [ "TX" ] @>

        test
            <@
                match completed.Results["TX"] with
                | TestsFailed _ -> true
                | _ -> false
            @>

        test <@ not (allGreen completed) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a fresh checkout whose reuse is withdrawn mid-run earns no baseline from that run`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // TZ must launch (its closure differs), and while it runs it edits X: the closure
        // of TX, which this run reused.
        edit b "Z" "let z = 2"
        write b "edit-during-TZ" "X"
        let c = checkoutWith false b (Some(wiringFor store)) false
        let launched1, completed1 = run c
        test <@ launched1 = [ "TZ" ] @>

        test
            <@
                match completed1.Results["TX"] with
                | TestsDeferred _ -> true
                | _ -> false
            @>

        test <@ not (allGreen completed1) @>
        test <@ baselineRunOf b <> Some completed1.RunId @>

        // Still owed a baseline: the next run looks TX up again, misses (X moved), runs it,
        // and only then earns one.
        File.Delete(Path.Combine(b, "edit-during-TZ"))
        let launched2, completed2 = run c
        test <@ List.contains "TX" launched2 @>
        test <@ TestResult.verifiedGreen completed2.Results["TX"] @>
        test <@ baselineRunOf b = Some completed2.RunId @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a fresh checkout whose verification ledger cannot be read consults no evidence`` () =
    // What is owed is unknown, and only a full suite that RAN and passed discharges it.
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        write b ".fshw/test-prune/pending-verification.json" "{ not json"
        let lookupsBefore = store.Lookups
        let launched, completed = run (checkoutWith false b (Some(wiringFor store)) false)
        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>
        test <@ store.Lookups = lookupsBefore @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a merge launches exactly the projects whose closure differs from both parents`` () =
    // A touched X, B touched Y, and the merge M has both plus an edit of its own to Z.
    // TX's closure in M is A's, TY's is B's, and TZ's is nobody's.
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        edit a "X" "let fromA = 1"
        let launchedA, _ = run (checkout a (Some(wiringFor store)) false)
        test <@ launchedA = testProjects @>

        let b = rootNamed "b"
        edit b "Y" "let fromB = 1"
        let launchedB, _ = run (checkout b (Some(wiringFor store)) false)
        test <@ launchedB = [ "TX"; "TY" ] @>

        let m = rootNamed "m"
        edit m "X" "let fromA = 1"
        edit m "Y" "let fromB = 1"
        edit m "Z" "let fromMerge = 1"
        let launchedM, completedM = run (checkout m (Some(wiringFor store)) false)
        test <@ launchedM = [ "TZ" ] @>
        test <@ reusedIn completedM = [ "TX"; "TY" ] @>
        test <@ allGreen completedM @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a merge that changes a shared closure re-verifies every test project over it`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let m = rootNamed "m"
        edit m "S" "let shared = 2"
        let launchedM, completedM = run (checkout m (Some(wiringFor store)) false)
        test <@ launchedM = [ "TX"; "TY" ] @>
        test <@ reusedIn completedM = [ "TZ" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``evidence from a non-identical closure is never reused`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // A file APPEARING in a closure directory, not only an edit to one.
        write b "src/X/Extra.fs" "module Extra\n"
        let launchedB, completedB = run (checkout b (Some(wiringFor store)) false)
        test <@ launchedB = [ "TX" ] @>
        test <@ not (List.contains "TX" (reusedIn completedB)) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``an old green never covers a failing current tree`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        write b "src/X/fail" ""
        let launchedB, completedB = run (checkout b (Some(wiringFor store)) false)
        test <@ launchedB = [ "TX" ] @>

        test
            <@
                match completedB.Results["TX"] with
                | TestsFailed _ -> true
                | _ -> false
            @>

        test <@ not (allGreen completedB) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``an outstanding failure blocks reuse until the project earns a green itself`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        write b "src/X/fail" ""
        let c = checkout b (Some(wiringFor store)) false
        let launched1, _ = run c
        test <@ launched1 = [ "TX" ] @>
        // Back to A's bytes: the store holds a green for exactly this key, but this
        // daemon still owes TX a passing run of its own.
        File.Delete(Path.Combine(b, "src/X/fail"))
        let launched2, completed2 = run c
        test <@ List.contains "TX" launched2 @>
        test <@ not (List.contains "TX" (reusedIn completed2)) @>
        test <@ TestResult.verifiedGreen completed2.Results["TX"] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a newer full fail at a project's key is not answered by an older green under that key`` () =
    // ADR-059 §2: the answer for a key is its NEWEST record, and §25 keeps a newer fail at
    // the same key red. Checkout A earns TX's green under K_P; checkout B, on the same bytes,
    // runs TX in full under confirm and it fails (a flake: the marker is outside every
    // closure, so the key does not move). A fresh checkout C of those bytes, with no local
    // ledger of B's failure, must run TX rather than reuse A's green.
    withTempDir "evkpfail" (fun dir ->
        let store =
            TestEvidenceStore.Store(
                { Root = Path.Combine(dir, "store")
                  Ttl = TimeSpan.FromHours 72.0
                  MaxBytes = 64L * 1024L * 1024L
                  Now = fun () -> DateTimeOffset.UtcNow
                  Log = ignore }
            )

        let outcomes =
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "outcomes")
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        let wiring =
            Some
                { wiringFor store with
                    Outcomes = Some outcomes }

        let rootNamed (name: string) =
            let root = Path.Combine(dir, name)
            Directory.CreateDirectory root |> ignore
            populate root
            root

        let launchedA, completedA = run (checkout (rootNamed "a") wiring false)
        test <@ List.contains "TX" launchedA @>
        test <@ TestResult.verifiedGreen completedA.Results["TX"] @>

        let b = rootNamed "b"
        write b "flake-TX" ""
        let launchedB, completedB = run (checkout b wiring true)
        test <@ List.contains "TX" launchedB @>

        test
            <@
                match completedB.Results["TX"] with
                | TestsFailed _ -> true
                | _ -> false
            @>

        // Positive control: both runs are on file under ONE full key, and its answer is the fail.
        let tx =
            (OutcomeStore.readAll outcomes.Root).Records
            |> List.filter (fun r -> r.Subject = "TX" && r.Key.Inputs |> List.contains ("run-scope", "full"))

        test <@ tx |> List.map _.Outcome = [ OutcomeStore.Outcome.Pass; OutcomeStore.Outcome.Fail ] @>
        test <@ tx |> List.map _.Key.Value |> List.distinct |> List.length = 1 @>
        test <@ (outcomes.Lookup tx.Head.Key) |> Option.map _.Outcome = Some OutcomeStore.Outcome.Fail @>

        let launchedC, completedC = run (checkout (rootNamed "c") wiring false)
        test <@ not (List.contains "TX" (reusedIn completedC)) @>
        test <@ List.contains "TX" launchedC @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a reused project whose closure moves mid-run is withdrawn and launched next`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // TZ must launch (its closure differs), and while it runs it edits X — the
        // closure of TX, which this run reused.
        edit b "Z" "let z = 2"
        write b "edit-during-TZ" "X"
        let c = checkout b (Some(wiringFor store)) false
        let launched1, completed1 = run c
        test <@ launched1 = [ "TZ" ] @>

        test
            <@
                match completed1.Results["TX"] with
                | TestsDeferred reason -> reason.Contains "closure"
                | _ -> false
            @>

        test <@ not (allGreen completed1) @>
        File.Delete(Path.Combine(b, "edit-during-TZ"))
        let launched2, completed2 = run c
        test <@ List.contains "TX" launched2 @>
        test <@ TestResult.verifiedGreen completed2.Results["TX"] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``confirm performs zero lookups and launches every project`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let lookupsBefore = store.Lookups

        let launched, completed =
            run (checkout (rootNamed "b") (Some(wiringFor store)) true)

        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>
        test <@ store.Lookups = lookupsBefore @>

        // A fresh checkout, owed a baseline, is no exception: confirm owes a run of everything.
        let launchedFresh, completedFresh =
            run (checkoutWith false (rootNamed "c") (Some(wiringFor store)) true)

        test <@ launchedFresh = testProjects @>
        test <@ List.isEmpty (reusedIn completedFresh) @>
        test <@ store.Lookups = lookupsBefore @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``with the kill switch off every project launches exactly as without a store`` () =
    withRepo (fun store rootNamed ->
        let first, completedFirst =
            run (checkout (rootNamed "a") (Some(wiringFor store)) false)

        let off, completedOff = run (checkout (rootNamed "b") None false)
        test <@ off = first @>

        let verdicts (c: TestRunCompleted) =
            c.Results |> Map.map (fun _ r -> TestResult.verdict r)

        test <@ verdicts completedOff = verdicts completedFirst @>
        test <@ List.isEmpty (reusedIn completedOff) @>
        test <@ completedOff.Verification = completedFirst.Verification @>
        test <@ evidenceWiringWhen false [] (rootNamed "c") |> Option.isNone @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a failing run writes no evidence`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        write a "src/X/fail" ""
        run (checkout a (Some(wiringFor store)) false) |> ignore
        // The same tree, freshly checked out: TX must run again, the others may not.
        let b = rootNamed "b"
        write b "src/X/fail" ""
        let launched, _ = run (checkout b (Some(wiringFor store)) false)
        test <@ launched = [ "TX" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a run whose tree moved writes no evidence for any project`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        // TZ edits Y while the run is in flight: the completion walk differs from the
        // launch walk for TY, so TY's green is not published.
        write a "edit-during-TZ" "Y"
        run (checkout a (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // b holds the bytes a LAUNCHED with, so only what a published can be reused.
        let launched, _ = run (checkout b (Some(wiringFor store)) false)
        test <@ List.contains "TY" launched @>)

/// A `beforeRun` that writes `rel` (repo-relative) with the text `contentOf root`: the
/// setup step that generates what the tests read at run time (a bundle) before they start.
let internal generating (rel: string) (contentOf: string -> string) (root: string) =
    Some(fun (_: Guid) (_: HookStep.Tracker) -> write root rel (contentOf root))

/// A seeded checkout of `root` whose `beforeRun` is `generating rel contentOf`.
let private checkoutGenerating (rel: string) (contentOf: string -> string) store (root: string) =
    checkoutSetUp None (generating rel contentOf root) true root (Some(wiringFor store)) false

[<Fact(Timeout = RunsTestCapMs)>]
let ``a run whose beforeRun generates a runtime file publishes, and a fresh checkout's first run reuses it`` () =
    // AUTOMATION-1510: the key a run publishes under names the tree its tests ran
    // against, which is the tree AFTER beforeRun. Keyed before it, a fresh checkout's
    // first run (the generated file absent) saw its own key move and published nothing,
    // and the next fresh checkout looked up a key no settled run ever has.
    withRepo (fun store rootNamed ->
        let bundle = "src/S/wwwroot/app.js"

        let launchedA, completedA =
            run (checkoutGenerating bundle (fun _ -> "bundle") store (rootNamed "a"))

        test <@ launchedA = testProjects @>
        test <@ allGreen completedA @>

        let launchedB, completedB =
            run (checkoutGenerating bundle (fun _ -> "bundle") store (rootNamed "b"))

        test <@ List.isEmpty launchedB @>
        test <@ reusedIn completedB = testProjects @>
        test <@ allGreen completedB @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``with a generating beforeRun a source edit still launches exactly the projects over it`` () =
    withRepo (fun store rootNamed ->
        let bundle = "src/S/wwwroot/app.js"

        run (checkoutGenerating bundle (fun _ -> "bundle") store (rootNamed "a"))
        |> ignore

        let b = rootNamed "b"
        edit b "X" "// a real change"

        let launched, completed =
            run (checkoutGenerating bundle (fun _ -> "bundle") store b)

        test <@ launched = [ "TX" ] @>
        test <@ reusedIn completed = [ "TY"; "TZ" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a beforeRun whose output differs between checkouts never reuses a green earned over other output`` () =
    withRepo (fun store rootNamed ->
        // The generated text is the checkout's own path, so no two checkouts agree on it.
        let bundle = "src/S/wwwroot/app.js"
        run (checkoutGenerating bundle id store (rootNamed "a")) |> ignore
        let launched, completed = run (checkoutGenerating bundle id store (rootNamed "b"))
        test <@ launched = [ "TX"; "TY" ] @>
        test <@ reusedIn completed = [ "TZ" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a beforeRun that rewrites a compile input neither reuses nor publishes evidence`` () =
    // The binaries predate what beforeRun wrote, so the settled tree is not the tree the
    // tests were built from: no key names what they ran. A `.props` is a compile input
    // whatever the model says.
    withRepo (fun store rootNamed ->
        let generated = "src/S/Directory.Build.props"
        let props = "<Project />"

        run (checkoutGenerating generated (fun _ -> props) store (rootNamed "a"))
        |> ignore

        let launched, completed =
            run (checkoutGenerating generated (fun _ -> props) store (rootNamed "b"))

        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a reused report names its producer in the run directory`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        run (checkout a (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        let _, completed = run (checkout b (Some(wiringFor store)) false)
        let report = Path.Combine(Ctrf.runDir b completed.RunId, "TX" + Ctrf.ReportSuffix)
        let text = File.ReadAllText report
        test <@ text.Contains TestEvidenceRun.ReusedFromField @>
        test <@ text.Contains(Path.GetFullPath a) @>
        test <@ Ctrf.tryVerdictReport text |> Result.isOk @>

        // The verdict reads the same file and names the producer for exactly the reused
        // suites: here, every one of them.
        let suites = FsHotWatch.Cli.Verdict.suiteVerdicts b (Some completed.RunId)
        test <@ suites |> List.map _.Project |> List.sort = testProjects @>

        test
            <@
                suites
                |> List.forall (fun s ->
                    s.ReusedFrom
                    |> Option.exists (fun r -> r.Worktree = Path.GetFullPath a && r.RunId.Length = 32))
            @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``an executed suite names no producer`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        let _, completed = run (checkout a (Some(wiringFor store)) false)
        let suites = FsHotWatch.Cli.Verdict.suiteVerdicts a (Some completed.RunId)
        test <@ suites.Length = 3 @>
        test <@ suites |> List.forall (fun s -> Option.isNone s.ReusedFrom) @>)

// ---------------------------------------------------------------------------
// Coverage the gate grades
// ---------------------------------------------------------------------------

[<Fact(Timeout = RunsTestCapMs)>]
let ``a coverage-graded project reuses its stored coverage, rebound into this checkout`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        run (checkoutFull (coverageIn a) true a (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"

        let launched, completed =
            run (checkoutFull (coverageIn b) true b (Some(wiringFor store)) false)

        test <@ List.isEmpty (launched) @>
        test <@ reusedIn completed = testProjects @>
        let baseline = File.ReadAllText(Path.Combine(b, "coverage", "TX", BaselineName))
        // Matched as a suffix: the runner reports the physical path (/private/var/... on
        // macOS), which ends in the root it was given.
        test <@ baseline.Contains $"%s{b}</source>" @>
        test <@ not (baseline.Contains a) @>
        test <@ File.Exists(Path.Combine(Ctrf.runDir b completed.RunId, "TX.coverage.cobertura.xml")) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``evidence without the coverage a checkout grades is not reused`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"

        let launched, completed =
            run (checkoutFull (coverageIn b) true b (Some(wiringFor store)) false)

        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``evidence that cannot be filed is launched instead`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        run (checkoutFull (coverageIn a) true a (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // A DIRECTORY where TX's coverage report belongs: the reuse cannot write the report
        // there, so TX is launched like any other project.
        Directory.CreateDirectory(Path.Combine(b, "coverage", "TX", BaselineName))
        |> ignore

        let launched, completed =
            run (checkoutFull (coverageIn b) true b (Some(wiringFor store)) false)

        test <@ launched = [ "TX" ] @>
        test <@ reusedIn completed = [ "TY"; "TZ" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``no progress subscriber sees a reused green before the completion re-check`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        edit b "Z" "let z = 2"
        let _, completed = run (checkout b (Some(wiringFor store)) false)
        test <@ reusedIn completed = [ "TX"; "TY" ] @>
        test <@ progressSeen[b] |> Seq.exists isReused |> not @>
        test <@ progressSeen[b] |> Seq.isEmpty |> not @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a green whose coverage failed to ingest is not published`` () =
    withRepo (fun store rootNamed ->
        let a = rootNamed "a"
        write a "badcov-TX" ""

        let _, completedA =
            run (checkoutFull (coverageIn a) true a (Some(wiringFor store)) false)

        test <@ not (TestResult.verifiedGreen completedA.Results["TX"]) @>
        let b = rootNamed "b"

        let launched, _ =
            run (checkoutFull (coverageIn b) true b (Some(wiringFor store)) false)

        test <@ launched = [ "TX" ] @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``every project a run launched is recorded in the outcome store under its K_P (ADR-059 step 7)`` () =
    withRepo (fun store rootNamed ->
        let root = rootNamed "a"

        let outcomes =
            OutcomeStore.Store(
                { Root = Path.Combine(root, "..", "outcomes")
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 64L * 1024L * 1024L
                  Log = ignore }
            )

        let wiring =
            { wiringFor store with
                Outcomes = Some outcomes }

        let a = checkout root (Some wiring) false
        let launched, completed = run a
        test <@ launched = testProjects @>
        test <@ allGreen completed @>

        let recorded =
            allFilesUnder "*.json" (Path.Combine(root, "..", "outcomes", "records"))
            |> List.length

        test <@ recorded = List.length testProjects @>)

/// The `outcomeKeys` a settled `test-scope` reply names, by project.
let private outcomeKeysIn (c: Checkout) : Map<string, string> =
    match c.Host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
    | Some reply ->
        let root = (System.Text.Json.JsonDocument.Parse reply).RootElement.Clone()

        match root.TryGetProperty "outcomeKeys" with
        | true, keys when keys.ValueKind = System.Text.Json.JsonValueKind.Object ->
            keys.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.GetString())
            |> Map.ofSeq
        | _ -> Map.empty
    | None -> Map.empty

[<Fact(Timeout = RunsTestCapMs)>]
let ``a settled test-scope reply names the outcome key each project's full run over THIS tree has (ADR-059 step 7b)``
    ()
    =
    withRepo (fun store rootNamed ->
        let root = rootNamed "a"

        let outcomes =
            OutcomeStore.Store(
                { Root = Path.Combine(root, "..", "outcomes")
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 64L * 1024L * 1024L
                  Log = ignore }
            )

        let a =
            checkout
                root
                (Some
                    { wiringFor store with
                        Outcomes = Some outcomes })
                false

        run a |> ignore

        // Each named key is the one the run's own record was filed under: the lookup a
        // shadow verdict makes finds the pass this run earned.
        let keys = outcomeKeysIn a
        test <@ Set.ofSeq keys.Keys = Set.ofList testProjects @>

        for KeyValue(_, key) in keys do
            test
                <@
                    outcomes.Lookup(Option.get (ActionKey.KeyValue.tryParse key))
                    |> Option.map _.Outcome = Some OutcomeStore.Outcome.Pass
                @>

        // The keys describe the tree as it is NOW: after an edit no run has seen, they move,
        // and nothing on file answers for them.
        edit root (List.head libs) "let edited = 2"
        let moved = outcomeKeysIn a
        test <@ not (Map.isEmpty moved) @>
        test <@ moved |> Map.exists (fun project key -> keys[project] <> key) @>

        test
            <@
                moved
                |> Map.forall (fun project key ->
                    key = keys[project]
                    || (outcomes.Lookup(Option.get (ActionKey.KeyValue.tryParse key))).IsNone)
            @>)

// ---------------------------------------------------------------------------
// A dependency fanout whose every project already holds evidence at these inputs
// (AUTOMATION-1534). The cycle reused every receipt and still paid for a launch: the
// beforeRun steps, the freshness gates, and a whole run's bookkeeping.
// ---------------------------------------------------------------------------

/// A checkout that counts its beforeRun steps, which only a launched run takes.
let private countingCheckout (root: string) (evidence: TestEvidenceRun.Wiring option) =
    let launches = ref 0

    let c =
        checkoutSetUp
            None
            (Some(fun _ _ -> System.Threading.Interlocked.Increment launches |> ignore))
            true
            root
            evidence
            false

    c, launches

/// The file a referenced project's restored package graph lives in. Build output, so it
/// is outside the verdict tree, and the dependency fingerprint reads it.
let private assetsFile (root: string) =
    Path.Combine(root, "src", libs.Head, "obj", "project.assets.json")

let private seedAssets (root: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(assetsFile root)) |> ignore
    File.WriteAllText(assetsFile root, "{\"version\":1}")

/// Rewrites what the fingerprint reads and nothing the symbol diff or tree can see.
let private moveFingerprint (root: string) =
    File.WriteAllText(assetsFile root, "{\"version\":2}")

[<Fact(Timeout = RunsTestCapMs)>]
let ``a dependency fanout every project of which holds evidence launches no run`` () =
    withRepo (fun store rootNamed ->
        let root = rootNamed "a"
        seedAssets root
        let c, launches = countingCheckout root (Some(wiringFor store))
        run c |> ignore
        test <@ launches.Value = 1 @>

        moveFingerprint c.Root
        run c |> ignore
        test <@ launches.Value = 1 @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a dependency fanout with no evidence still force-runs (positive control)`` () =
    withRepo (fun _ rootNamed ->
        let root = rootNamed "a"
        seedAssets root
        let c, launches = countingCheckout root None
        run c |> ignore
        test <@ launches.Value = 1 @>

        moveFingerprint c.Root
        run c |> ignore
        test <@ launches.Value = 2 @>)
