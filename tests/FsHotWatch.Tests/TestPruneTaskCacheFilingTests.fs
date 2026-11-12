/// AUTOMATION-1609: a TestPrune whose per-workspace task cache answers a file still leaves
/// that file's analysis filed in the box-wide store, so a fresh workspace replays it rather
/// than demanding a live check. Filed once per key, never again (AUTOMATION-1596), and
/// only under the analysis's own key.
module FsHotWatch.Tests.TestPruneTaskCacheFilingTests

open System
open System.IO
open FSharp.Compiler.Text
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.CheckPipeline
open FsHotWatch.Tests.TestHelpers

module TestPrunePlugin = FsHotWatch.TestPrune.TestPrunePlugin
module PluginTypes = FsHotWatch.TestPrune.PluginTypes

let private model generation =
    ProjectModel.ofCompleted
        generation
        { Discovered = 1
          Loaded = 1
          OptionsMapped = 1
          Registered = 1 }

/// One project `P`: a test attribute, a value, and a test that calls it.
let private writeProject (root: string) =
    let dir = Path.Combine(root, "tests", "P")
    Directory.CreateDirectory dir |> ignore
    let attr = Path.Combine(dir, "Attr.fs")
    let value = Path.Combine(dir, "Value.fs")
    let tests = Path.Combine(dir, "Tests.fs")

    File.WriteAllText(attr, "namespace Xunit\ntype FactAttribute() =\n    inherit System.Attribute()\n")
    File.WriteAllText(value, "module P.Value\nlet answer () = 2\n")

    File.WriteAllText(
        tests,
        "module P.Tests\nopen Xunit\n[<Fact>]\nlet ``observes the value`` () = P.Value.answer () = 2\n"
    )

    [ attr; value; tests ]

let private stamped generation (result: FileCheckResult) =
    { result with
        ModelGeneration = Some generation }

let private batch generation (files: string list) =
    { fakeBatchChecked files with
        ModelGeneration = Some generation }

/// Project `P`, whose command records each run in `runs`.
let private config root : PluginTypes.TestConfig =
    let runs = Path.Combine(root, "runs")

    { Project = "P"
      Command = "sh"
      Args = $"-c \"echo run >> {runs}\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = PluginTypes.AutoDetect
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

/// Project `P` registered with a pipeline that files check records under `root/outcomes`,
/// as a daemon with `checkRecords` on does, on its own checker: each call is a new process.
let private recordingPipeline (root: string) (files: string list) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()

    let store =
        OutcomeStore.Store(
            { Root = Path.Combine(root, "outcomes")
              Ttl = OutcomeStore.defaultTtl
              MaxBytes = OutcomeStore.defaultMaxBytes
              Log = ignore }
        )

    let pipeline = CheckPipeline(checker, repoRoot = root, checkRecords = store)
    let first = List.head files

    let options, _ =
        checker.GetProjectOptionsFromScript(
            first,
            SourceText.ofString (File.ReadAllText first),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    pipeline.RegisterProject(
        "P",
        { options with
            ProjectFileName = Path.Combine(root, "tests", "P", "P.fsproj")
            SourceFiles = List.toArray files }
    )

    pipeline

let private checkEach (pipeline: CheckPipeline) (files: string list) =
    files
    |> List.map (fun file ->
        pipeline.CheckFile(AbsFilePath.create file)
        |> Async.RunSynchronously
        |> Option.defaultWith (fun () -> failwith $"no check result for {file}"))

module AnalysisRecords = FsHotWatch.TestPrune.AnalysisRecords

/// How many copies of each analysis `store` holds under `keys`.
let private copiesIn (store: OutcomeStore.Store) (keys: ActionKey.ActionKey list) =
    keys |> List.map (fun key -> (store.History key).Length)

/// The analysis keys of `results`, as TestPrune files them under `root`.
let private analysisKeysOf (root: string) (results: FileCheckResult list) =
    results
    |> List.map (fun r ->
        AnalysisRecords.keyFor root r
        |> Option.defaultWith (fun () -> failwith $"no analysis key for {r.File}"))

/// AUTOMATION-1609, seed A's shape: workspace A analyses every file of `P` live, indexes it
/// and files its checks. Then the box-wide analysis records are lost (a fresh cache home)
/// while A's task cache and index stay warm, and A restarts `restarts` times, each handed
/// the same live checks: its task cache can answer every file. `beforeRestart` sees the
/// emptied store first. Returns the files, the live checks, the pipeline and the store.
let private warmRestartsIn
    (root: string)
    (restarts: int)
    (beforeRestart: OutcomeStore.Store -> FileCheckResult list -> unit)
    =
    let files = writeProject root
    let dbPath = Path.Combine(root, "tp.db")

    let cache =
        FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

    let first = recordingPipeline root files
    let live = checkEach first files

    let run () =
        let host =
            FsHotWatch.PluginHost.PluginHost(sharedChecker.Value, root, taskCache = cache)

        host.WorkStore.PublishProjectModel(model 1L)
        host.RegisterHandler(TestPrunePlugin.create dbPath root (Some [ config root ]) None None None None [])

        try
            for result in live do
                host.EmitFileChecked(stamped 1L result)

            waitForQuiescent host 20000
            host.EmitBatchChecked(batch 1L files)
            waitForQuiescent host 20000
        finally
            host.Teardown()

    run ()
    test <@ first.PersistCheckRecords() = files.Length @>

    let analyses = OutcomeStore.directoryFor root

    if Directory.Exists analyses then
        Directory.Delete(analyses, true)

    let store = OutcomeStore.openFor root ignore
    beforeRestart store live

    for _ in 1..restarts do
        run ()

    files, live, store

/// A fresh workspace at `root`'s tree: a new task cache and an empty index, handed every
/// file replayed from its check record. The files it demanded live, and the tests its
/// index then finds covering `P.Value.answer`.
let private freshWorkspaceDemands (root: string) (files: string list) =
    let pipeline = recordingPipeline root files
    let replayed = checkEach pipeline files

    test
        <@
            replayed
            |> List.forall (fun r ->
                match r.CheckResults with
                | Replayed _ -> true
                | _ -> false)
        @>

    let demanded = Collections.Concurrent.ConcurrentQueue<string>()
    let dbPath = Path.Combine(root, "fresh.db")

    let fresh =
        FsHotWatch.PluginHost.PluginHost(
            sharedChecker.Value,
            root,
            taskCache = (FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache)
        )

    fresh.WorkStore.PublishProjectModel(model 1L)

    fresh.SetCheckDemand(fun replay ->
        demanded.Enqueue(Path.GetFileName(AbsFilePath.value replay.File))
        pipeline.CheckFileLive(replay.File, replay.ProjectOptions))

    fresh.RegisterHandler(TestPrunePlugin.create dbPath root (Some [ config root ]) None None None None [])

    try
        for result in replayed do
            fresh.EmitFileChecked(stamped 1L result)

        waitForQuiescent fresh 20000
        fresh.EmitBatchChecked(batch 1L files)
        waitForQuiescent fresh 20000

        let covering =
            (TestPrune.Database.Database.create dbPath).QueryAffectedTests [ "P.Value.answer" ]
            |> List.map (fun t -> t.TestClass + "." + t.TestMethod)

        List.ofSeq demanded |> List.sort, covering
    finally
        fresh.Teardown()

/// AUTOMATION-1609 positive control: a workspace whose task cache answers every file still
/// leaves one analysis record per file in the store, so a fresh workspace at the same tree
/// demands no live check for TestPrune. Before the fix the cache hit skipped the analysis,
/// nothing was filed, and the fresh workspace demanded every file.
[<Fact(Timeout = 240000)>]
let ``a workspace answered by its task cache still files every analysis, so a fresh workspace demands none`` () =
    withTempDir "tp-1609-seed" (fun root ->
        let files, live, store = warmRestartsIn root 1 (fun _ _ -> ())
        test <@ copiesIn store (analysisKeysOf root live) = List.replicate files.Length 1 @>

        let demanded, covering = freshWorkspaceDemands root files
        test <@ List.isEmpty demanded @>
        // The false-green guard: the filed analyses index the test that calls `answer`.
        test <@ covering |> List.exists (fun name -> name.Contains "observes the value") @>)

/// AUTOMATION-1609 negative control (AUTOMATION-1596): a second task-cache hit on a key the
/// store already holds files nothing new.
[<Fact(Timeout = 240000)>]
let ``a second task-cache hit on a filed analysis files no second copy`` () =
    withTempDir "tp-1609-dup" (fun root ->
        let files, live, store = warmRestartsIn root 2 (fun _ _ -> ())
        test <@ copiesIn store (analysisKeysOf root live) = List.replicate files.Length 1 @>)

/// AUTOMATION-1609 key completeness: a record of the same file under a DIFFERENT analysis
/// key does not count as this analysis being filed. The task-cache hit still files under
/// its own key, and leaves the other record alone.
[<Fact(Timeout = 240000)>]
let ``a filed analysis under another key is not taken for this one`` () =
    withTempDir "tp-1609-key" (fun root ->
        let empty: TestPrune.AstAnalyzer.AnalysisResult =
            { Symbols = []
              Dependencies = []
              TestMethods = []
              Attributes = []
              ParentLinks = []
              Diagnostics =
                { DroppedEdges = 0
                  FilteredSymbols = 0
                  TotalDefinitions = 0 } }

        let foreignKeys = Collections.Generic.List<ActionKey.ActionKey>()

        let fileForeign (store: OutcomeStore.Store) (live: FileCheckResult list) =
            for result in live do
                let relPath =
                    Path.GetRelativePath(root, AbsFilePath.value result.File).Replace('\\', '/')

                // The same file and project under another analysis identity.
                let key = AnalysisRecords.keyOf "another-analysis" relPath "P"
                test <@ AnalysisRecords.file store root relPath key empty @>
                foreignKeys.Add key

        let files, live, store = warmRestartsIn root 1 fileForeign
        test <@ copiesIn store (analysisKeysOf root live) = List.replicate files.Length 1 @>
        test <@ copiesIn store (List.ofSeq foreignKeys) = List.replicate files.Length 1 @>)
