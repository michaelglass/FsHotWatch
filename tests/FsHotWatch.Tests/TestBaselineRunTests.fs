/// AUTOMATION-1587 step 2, end to end: a real test-prune daemon over a small repository,
/// wired to a box-wide outcome store, files a shadow `test-baseline` record only when a
/// completion earned the full-suite baseline, every runnable project filed this run's
/// full-pass K_P record, and the index was sealed at the receipt tree. Every completion
/// says, in one countable log line, whether it wrote one and if not why. Nothing reads the
/// record: the last test runs the same checks with and without it and compares.
module FsHotWatch.Tests.TestBaselineRunTests

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
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// Logs the project it was started for and writes a CTRF report where fshw asked. Fails
/// when `src/<lib>/fail` exists; with `src/<lib>/stop-on-fail` it fails the way an xUnit v3
/// host under `--stop-on-fail on` does: the report names only the failing test.
let private runner =
    """#!/bin/sh
proj="$1"; shift
name=''; dir=''
while [ $# -gt 0 ]; do
  case "$1" in
    --report-ctrf-filename) name="$2"; shift ;;
    --results-directory) dir="$2"; shift ;;
  esac
  shift
done
printf '%s\n' "$proj" >> runs.log
lib="src/${proj#T}"
code=0
if [ -n "$dir" ] && [ -n "$name" ]; then
  if [ -f "$lib/fail" ]; then
    printf '{"results":{"summary":{"tests":2,"passed":1,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"%s.a","status":"passed"},{"name":"%s.b","status":"failed"}]}}' "$proj" "$proj" > "$dir/$name"
    code=1
  elif [ -f "$lib/stop-on-fail" ]; then
    printf '{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"%s.a","status":"failed"}]}}' "$proj" > "$dir/$name"
    code=1
  else
    printf '{"results":{"summary":{"tests":2,"passed":2,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"%s.a","status":"passed"},{"name":"%s.b","status":"passed"}]}}' "$proj" "$proj" > "$dir/$name"
  fi
fi
exit $code
"""

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private libs = [ "X"; "Y" ]
let private testProjects = [ "TX"; "TY" ]

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
          ReportVerificationFormat = Ctrf
          Starvation = None
          CheckSkip = None
          NameHungTests = false
          Weight = 1.0
          AlwaysClasses = [] })

let private graphOf (root: string) : ProjectGraphAccessor =
    let all = (libs @ testProjects) |> List.map (fsprojOf root)

    { ProjectGraphAccessor.none with
        GetAllProjects = fun () -> all
        GetProjectReferences =
            fun fsproj ->
                match Path.GetFileNameWithoutExtension fsproj with
                | "TX" -> [ fsprojOf root "X" ]
                | "TY" -> [ fsprojOf root "Y" ]
                | _ -> [] }

let private identitiesOf (root: string) =
    (libs @ testProjects)
    |> List.map (fun name -> fsprojOf root name, "identity:" + name)
    |> Map.ofList

/// One repository's box-wide stores: the evidence store and the outcome store.
type private Stores =
    { Evidence: TestEvidenceStore.Store
      Outcomes: OutcomeStore.Store }

let private storesIn (dir: string) =
    { Evidence =
        TestEvidenceStore.Store(
            { Root = Path.Combine(dir, "evidence")
              Ttl = TimeSpan.FromHours 72.0
              MaxBytes = 64L * 1024L * 1024L
              Now = fun () -> DateTimeOffset.UtcNow
              Log = ignore }
        )
      Outcomes =
        OutcomeStore.Store(
            { Root = Path.Combine(dir, "outcomes")
              Ttl = TimeSpan.FromDays 14.0
              MaxBytes = 64L * 1024L * 1024L
              Log = ignore }
        ) }

let private wiringFor (stores: Stores) : TestEvidenceRun.Wiring =
    { Store = stores.Evidence
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
      Outcomes = Some stores.Outcomes }

/// One checkout's daemon, what it completed, and every log line it wrote.
type private Checkout =
    { Root: string
      Host: PluginHost
      Completions: Collections.Concurrent.ConcurrentQueue<TestRunCompleted>
      Lines: Collections.Concurrent.ConcurrentQueue<string> }

let private completionRecorder (completions: Collections.Concurrent.ConcurrentQueue<TestRunCompleted>) =
    { Name = PluginName.create "baseline-completion-recorder"
      Init = ()
      Update =
        fun _ state event ->
            async {
                match event with
                | TestRunCompleted completed -> completions.Enqueue completed
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeTestRunCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }
    : PluginHandler<unit, obj>

/// A long-lived checkout (it holds a full-suite baseline already), so its first run is
/// the ordinary full run that earns one.
let private checkout (stores: Stores) (root: string) =
    seedBaseline root testProjects
    let lines = Collections.Concurrent.ConcurrentQueue<string>()

    let sink: Logging.LogSink =
        { Write = lines.Enqueue
          Level = Logging.LogLevel.Info
          File = None }

    // Installed before the host exists, so its work carries the sink with it.
    Logging.installSink sink |> ignore
    let host = createModelHost Unchecked.defaultof<_> root
    host.SetProjectGraph(graphOf root)
    host.WorkStore.PublishProjectModelWithInputs(fixtureModel, Set.empty, Some(identitiesOf root))
    let completions = Collections.Concurrent.ConcurrentQueue<TestRunCompleted>()
    host.RegisterHandler(completionRecorder completions)

    host.RegisterHandler(
        createWithEvidenceAndQueries
            (Some(wiringFor stores))
            ImpactQueries.ofDatabase
            (TimeSpan.FromMinutes 5.0)
            None
            (fun () -> Map.empty)
            (Path.Combine(root, "test-prune.db"))
            TestPrune.Domain.WalkPolicy.FollowAll
            root
            (Some configs)
            None
            None
            None
            None
            []
            None
            TestMode.initial
    )

    { Root = root
      Host = host
      Completions = completions
      Lines = lines }

let private launches (c: Checkout) =
    let log = Path.Combine(c.Root, "runs.log")

    if File.Exists log then
        File.ReadAllLines log |> List.ofArray
    else
        []

[<Literal>]
let private RunsTestCapMs = 3 * SyncHangBackstopMs

/// Seal the index at the tree as it stands: the boot scan's cohort folds.
let private seal (c: Checkout) = emitBatchAndQuiesce c.Host []

/// Run one check: the projects it launched and the completion it reported.
let private run (c: Checkout) =
    let before = launches c |> List.length
    let completedBefore = c.Completions.Count
    emitBuildAndWaitTerminal c.Host
    waitUntilOr "the run's completion to be recorded" (fun () -> c.Completions.Count > completedBefore) HangWaitMs
    let launched = launches c |> List.skip before |> List.sort
    launched, Seq.last c.Completions

/// The completion's one baseline line.
let private baselineLine (c: Checkout) (completed: TestRunCompleted) =
    let runId = completed.RunId.ToString("N")

    waitUntilOr
        "the completion's test-baseline line"
        (fun () ->
            c.Lines
            |> Seq.exists (fun l -> l.Contains $"test-baseline: " && l.Contains $"run=%s{runId}"))
        HangWaitMs

    c.Lines
    |> Seq.filter (fun l -> l.Contains "test-baseline: " && l.Contains $"run=%s{runId}")
    |> List.ofSeq

let private treeOf (root: string) =
    (InputTrees.ReceiptInputTree.readTree root).Value

let private withRepo (body: Stores -> (string -> string) -> 'a) : 'a =
    withTempDir "baseline-run" (fun dir ->
        let stores = storesIn (Path.Combine(dir, "store"))

        let rootNamed (name: string) =
            let root = Path.Combine(dir, name)
            Directory.CreateDirectory root |> ignore
            populate root
            root

        body stores rootNamed)

[<Fact(Timeout = RunsTestCapMs)>]
let ``an all-pass full run over a sealed index files one baseline and one index entry`` () =
    withRepo (fun stores rootNamed ->
        let c = checkout stores (rootNamed "a")
        seal c
        let launched, completed = run c
        test <@ launched = testProjects @>
        test <@ completed.Verification = Ran FullSuite @>

        let tree = treeOf c.Root
        let key = TestBaseline.keyOf tree.Identity
        let lines = baselineLine c completed
        test <@ lines.Length = 1 @>
        test <@ lines.Head.Contains "test-baseline: written" && lines.Head.Contains "bytes=" @>
        test <@ stores.Outcomes.History key |> List.length = 1 @>

        test
            <@
                TestBaseline.readIndex stores.Outcomes |> List.map (fun e -> e.Tree, e.RunId) = [ tree.Identity,
                                                                                                  completed
                                                                                                      .RunId
                                                                                                      .ToString("N") ]
            @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a red full run files no baseline and names why`` () =
    withRepo (fun stores rootNamed ->
        let root = rootNamed "a"
        write root "src/Y/fail" ""
        let c = checkout stores root
        seal c
        let launched, completed = run c
        test <@ launched = testProjects @>
        let lines = baselineLine c completed
        test <@ lines.Length = 1 && lines.Head.Contains "reason=not-earned" @>
        test <@ stores.Outcomes.Lookup(TestBaseline.keyOf (treeOf root).Identity) = None @>
        test <@ List.isEmpty (TestBaseline.readIndex stores.Outcomes) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a stop-on-fail full run, whose report names only the failure, files no baseline`` () =
    withRepo (fun stores rootNamed ->
        let root = rootNamed "a"
        write root "src/X/stop-on-fail" ""
        let c = checkout stores root
        seal c
        let launched, completed = run c
        test <@ launched = testProjects @>
        let lines = baselineLine c completed
        test <@ lines.Length = 1 && lines.Head.Contains "reason=not-earned" @>
        test <@ stores.Outcomes.Lookup(TestBaseline.keyOf (treeOf root).Identity) = None @>
        test <@ List.isEmpty (TestBaseline.readIndex stores.Outcomes) @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``an index not sealed at the receipt tree files no baseline and logs both trees`` () =
    withRepo (fun stores rootNamed ->
        let root = rootNamed "a"
        let c = checkout stores root
        seal c
        let sealedTree = (treeOf root).Identity
        // The tree moves after the seal: the run's receipt describes bytes no fold has seen.
        File.AppendAllText(Path.Combine(root, "src", "X", "Lib.fs"), "let moved = 2\n")
        let _, completed = run c
        let receipt = (treeOf root).Identity
        test <@ receipt <> sealedTree @>
        let lines = baselineLine c completed
        test <@ lines.Length = 1 && lines.Head.Contains "reason=not-sealed" @>

        test
            <@
                lines.Head.Contains $"sealed=%s{sealedTree}"
                && lines.Head.Contains $"receipt=%s{receipt}"
            @>

        test <@ stores.Outcomes.Lookup(TestBaseline.keyOf receipt) = None @>)

[<Fact(Timeout = RunsTestCapMs)>]
let ``a run that reused every project earns the ledger baseline but files no test-baseline of its own`` () =
    withRepo (fun stores rootNamed ->
        let a = checkout stores (rootNamed "a")
        seal a
        let _, completedA = run a
        test <@ (baselineLine a completedA).Head.Contains "test-baseline: written" @>

        let b = checkout stores (rootNamed "b")
        seal b
        let launchedB, completedB = run b
        test <@ List.isEmpty launchedB @>
        let lines = baselineLine b completedB
        test <@ lines.Length = 1 && lines.Head.Contains "reason=lacks-full-pass" @>
        test <@ lines.Head.Contains "TX" && lines.Head.Contains "TY" @>)

/// What a check decided, as far as the daemon shows it: what it launched, how it verified,
/// and what each project reported.
let private decided (launched: string list, completed: TestRunCompleted) =
    let outcome (r: TestResult) =
        match r with
        | TestsPassed(output, filtered, _) ->
            let reused = output.StartsWith("reused:", StringComparison.Ordinal)
            $"passed filtered=%b{filtered} reused=%b{reused}"
        | other -> other.GetType().Name

    launched, completed.Verification, completed.Results |> Map.map (fun _ r -> outcome r)

[<Fact(Timeout = RunsTestCapMs)>]
let ``selection and the skip decide the same with and without a baseline record present`` () =
    // Two identical repositories. In one the first run's baseline record and index stay;
    // in the other they are deleted. Every later check — an edit, an unchanged tree, a
    // restarted daemon over the moved tree — must decide exactly the same.
    let scenario (keepRecord: bool) =
        withRepo (fun stores rootNamed ->
            let root = rootNamed "a"
            let c = checkout stores root
            seal c
            let first = run c
            let tree = (treeOf root).Identity
            let filed = stores.Outcomes.Lookup(TestBaseline.keyOf tree)
            test <@ filed.IsSome @>

            if not keepRecord then
                for key in [ TestBaseline.keyOf tree; TestBaseline.indexKey ] do
                    Directory.Delete(stores.Outcomes.DirectoryOf key, true)

                test <@ stores.Outcomes.Lookup(TestBaseline.keyOf tree) = None @>

            File.AppendAllText(Path.Combine(root, "src", "Y", "Lib.fs"), "let edited = 2\n")
            let afterEdit = run c
            let unchanged = run c
            let restarted = checkout stores root
            let afterRestart = run restarted

            [ first; afterEdit; unchanged; afterRestart ] |> List.map decided)

    let withRecord = scenario true
    let withoutRecord = scenario false
    test <@ withRecord.Length = 4 @>
    test <@ withRecord = withoutRecord @>
