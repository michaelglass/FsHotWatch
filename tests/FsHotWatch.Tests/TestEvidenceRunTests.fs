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
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// ---------------------------------------------------------------------------
// A repository: three libraries, a test project over each, and one shared library
// ---------------------------------------------------------------------------

/// Logs the project it was started for, writes a one-row CTRF report where fshw asked,
/// and fails when `src/<lib>/fail` exists. `edit-during-<project>` at the root names a
/// library the runner appends to while it runs: an edit made mid-run.
let private runner =
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
if [ -f "$lib/fail" ]; then status=failed; passed=0; failed=1; code=1; else status=passed; passed=1; failed=0; code=0; fi
if [ -n "$dir" ] && [ -n "$name" ]; then
  printf '{"results":{"summary":{"tests":1,"passed":%s,"failed":%s,"pending":0,"skipped":0,"other":0},"tests":[{"name":"%s.t","status":"%s"}]}}' "$passed" "$failed" "$proj" "$status" > "$dir/$name"
fi
if [ -n "$cov" ] && [ -f "badcov-$proj" ]; then printf 'not coverage' > "$cov"; cov=''; fi
if [ -n "$cov" ]; then
  printf '<?xml version="1.0"?><coverage line-rate="1" branch-rate="1" version="1" timestamp="0"><sources><source>%s</source></sources><packages><package name="%s" line-rate="1" branch-rate="1"><classes><class name="%s" filename="src/%s/Lib.fs" line-rate="1" branch-rate="1"><methods/><lines><line number="2" hits="1"/></lines></class></classes></package></packages></coverage>' "$PWD" "$proj" "$proj" "${proj#T}" > "$cov"
fi
exit $code
"""

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private libs = [ "X"; "Y"; "Z"; "S" ]
let private testProjects = [ "TX"; "TY"; "TZ" ]

/// TX and TY reference the shared library S as well as their own.
let private referencesOf =
    Map.ofList [ "TX", [ "X"; "S" ]; "TY", [ "Y"; "S" ]; "TZ", [ "Z" ] ]

let private fsprojOf (root: string) (name: string) =
    if name.StartsWith "T" then
        Path.Combine(root, "tests", name, name + ".fsproj")
    else
        Path.Combine(root, "src", name, name + ".fsproj")

let private populate (root: string) =
    write root "runner.sh" runner

    for lib in libs do
        write root $"src/%s{lib}/%s{lib}.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        write root $"src/%s{lib}/Lib.fs" $"module %s{lib}\nlet value = 1\n"

    for t in testProjects do
        write root $"tests/%s{t}/%s{t}.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        write root $"tests/%s{t}/Tests.fs" $"module %s{t}\n"

let private configs =
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
          ReportVerificationFormat = Ctrf })

/// The model the host publishes: every project, its references, and an identity per
/// project that is the same in every checkout (the options do not change in these tests;
/// what changes is content, which the key hashes itself).
let private graphOf (root: string) : ProjectGraphAccessor =
    let all = (libs @ testProjects) |> List.map (fsprojOf root)

    { ProjectGraphAccessor.none with
        GetAllProjects = fun () -> all
        GetProjectReferences =
            fun fsproj ->
                let name = Path.GetFileNameWithoutExtension fsproj

                Map.tryFind name referencesOf
                |> Option.defaultValue []
                |> List.map (fsprojOf root) }

let private identitiesOf (root: string) =
    (libs @ testProjects)
    |> List.map (fun name -> fsprojOf root name, "identity:" + name)
    |> Map.ofList

/// One checkout: its root, its daemon, and what it completed.
type private Checkout =
    { Root: string
      Host: PluginHost
      Completions: Collections.Concurrent.ConcurrentQueue<TestRunCompleted> }

/// Every result any TestProgress carried, in arrival order.
let private progressSeen =
    Collections.Concurrent.ConcurrentDictionary<string, Collections.Concurrent.ConcurrentQueue<TestResult>>()

let private completionRecorder () =
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

let private wiringFor (store: TestEvidenceStore.Store) : TestEvidenceRun.Wiring =
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
      ExcludePatterns = [] }

/// Stand a daemon up over `root` with `evidence` wiring and a scope, as a LONG-LIVED
/// checkout: one that earned a full-suite baseline before these runs, the way the default
/// checkout has by the time anything lands in it. (A checkout owed a baseline consults no
/// evidence at all; `a checkout owed a full-suite baseline` below pins that.)
let private checkoutFull
    (coverage: (string -> string option) option)
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
            (fun () -> Map.empty)
            (Path.Combine(root, "test-prune.db"))
            TestPrune.Domain.WalkPolicy.FollowAll
            root
            (Some configs)
            None
            None
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
                           ArgsTemplate = "--coverage-output {output}" })))
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

let private checkoutWith = checkoutFull None

let private checkout = checkoutWith true

/// Every test project's coverage graded, its baseline under `coverage/<project>/`.
let private coverageIn (root: string) =
    Some(fun (project: string) -> Some(Path.Combine(root, "coverage", project, BaselineName)))

let private launches (c: Checkout) =
    let log = Path.Combine(c.Root, "runs.log")

    if File.Exists log then
        File.ReadAllLines log |> List.ofArray
    else
        []

/// Run one check and return the projects it LAUNCHED and the results it completed with.
let private run (c: Checkout) =
    let before = launches c |> List.length
    let completedBefore = c.Completions.Count
    emitBuildAndWaitTerminal c.Host
    test <@ waitUntilTrue (fun () -> c.Completions.Count > completedBefore) 20000 @>
    let launched = launches c |> List.skip before |> List.sort
    let completed = c.Completions |> Seq.last
    launched, completed

let private isReused (result: TestResult) =
    match result with
    | TestsPassed(output, false, _) -> output.StartsWith("reused:", StringComparison.Ordinal)
    | _ -> false

let private reusedIn (completed: TestRunCompleted) =
    completed.Results
    |> Map.filter (fun _ r -> isReused r)
    |> Map.keys
    |> List.ofSeq
    |> List.sort

/// Every project verified green, in a run that selected at least one: the run-level
/// guard is the run's own verification, since `forall` over no results is vacuously true.
let private allGreen (completed: TestRunCompleted) =
    let selectedAny = completed.Verification <> NoProjectsSelected

    selectedAny
    && completed.Results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)

/// A store in its own directory, and a root per checkout, all cleaned up together.
let private withRepo (body: TestEvidenceStore.Store -> (string -> string) -> unit) =
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

let private edit (root: string) (lib: string) (line: string) =
    File.AppendAllText(Path.Combine(root, "src", lib, "Lib.fs"), line + "\n")

// ---------------------------------------------------------------------------
// Producer and consumer
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
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
        // Its greens stand, but only running the suite earns a full-suite baseline: a
        // fully reused run leaves the one it had in place.
        let runIdOf (root: string) =
            match FullSuiteBaseline.load root with
            | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> Some baseline.RunId
            | _ -> None

        test <@ runIdOf a.Root = Some completedA.RunId @>
        test <@ runIdOf b.Root <> Some completedB.RunId @>
        test <@ Option.isSome (runIdOf b.Root) @>)

[<Fact(Timeout = 60000)>]
let ``a checkout owed a full-suite baseline consults no evidence and runs every project`` () =
    // A green is graded relative to a full-suite baseline, and only running earns one. A
    // checkout owed one that reused instead would answer "no verdict" on every check for
    // as long as the evidence lasted, and never earn it.
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let lookupsBefore = store.Lookups
        let fresh = checkoutWith false (rootNamed "b") (Some(wiringFor store)) false
        let launched, completed = run fresh
        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>
        test <@ store.Lookups = lookupsBefore @>

        match FullSuiteBaseline.load fresh.Root with
        | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> test <@ baseline.RunId = completed.RunId @>
        | other -> failwith $"expected the run to earn a baseline, got %A{other}"

        // Once it has earned one, an edit is selected as usual and the store is consulted
        // for what the selection launches.
        let lookupsEarned = store.Lookups
        edit fresh.Root "Z" "let z = 3"
        let launchedNext, _ = run fresh
        test <@ launchedNext = [ "TZ" ] @>
        test <@ store.Lookups > lookupsEarned @>)

[<Fact(Timeout = 90000)>]
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

[<Fact(Timeout = 60000)>]
let ``a merge that changes a shared closure re-verifies every test project over it`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let m = rootNamed "m"
        edit m "S" "let shared = 2"
        let launchedM, completedM = run (checkout m (Some(wiringFor store)) false)
        test <@ launchedM = [ "TX"; "TY" ] @>
        test <@ reusedIn completedM = [ "TZ" ] @>)

[<Fact(Timeout = 60000)>]
let ``evidence from a non-identical closure is never reused`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        // A file APPEARING in a closure directory, not only an edit to one.
        write b "src/X/Extra.fs" "module Extra\n"
        let launchedB, completedB = run (checkout b (Some(wiringFor store)) false)
        test <@ launchedB = [ "TX" ] @>
        test <@ not (List.contains "TX" (reusedIn completedB)) @>)

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 90000)>]
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

[<Fact(Timeout = 90000)>]
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

[<Fact(Timeout = 60000)>]
let ``confirm performs zero lookups and launches every project`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let lookupsBefore = store.Lookups

        let launched, completed =
            run (checkout (rootNamed "b") (Some(wiringFor store)) true)

        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>
        test <@ store.Lookups = lookupsBefore @>)

[<Fact(Timeout = 60000)>]
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
        test <@ evidenceWiringWhen false (rootNamed "c") |> Option.isNone @>)

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
let ``evidence without the coverage a checkout grades is not reused`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"

        let launched, completed =
            run (checkoutFull (coverageIn b) true b (Some(wiringFor store)) false)

        test <@ launched = testProjects @>
        test <@ List.isEmpty (reusedIn completed) @>)

[<Fact(Timeout = 60000)>]
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

[<Fact(Timeout = 60000)>]
let ``no progress subscriber sees a reused green before the completion re-check`` () =
    withRepo (fun store rootNamed ->
        run (checkout (rootNamed "a") (Some(wiringFor store)) false) |> ignore
        let b = rootNamed "b"
        edit b "Z" "let z = 2"
        let _, completed = run (checkout b (Some(wiringFor store)) false)
        test <@ reusedIn completed = [ "TX"; "TY" ] @>
        test <@ progressSeen[b] |> Seq.exists isReused |> not @>
        test <@ progressSeen[b] |> Seq.isEmpty |> not @>)

[<Fact(Timeout = 60000)>]
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
