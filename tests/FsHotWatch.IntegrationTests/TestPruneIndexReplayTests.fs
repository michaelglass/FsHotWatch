/// A file's analysis must reach the impact index before a cached replay may stand in for
/// it. Real FCS results: the index's edges come from the compiler's symbol uses.
module FsHotWatch.Tests.TestPruneIndexReplayTests

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

let private checkAll (root: string) (files: string list) =
    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
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

    files
    |> List.map (fun file ->
        pipeline.CheckFile(AbsFilePath.create file)
        |> Async.RunSynchronously
        |> Option.defaultWith (fun () -> failwith $"no check result for {file}"))

let private stamped generation (result: FileCheckResult) =
    { result with
        ModelGeneration = Some generation }

let private batch generation (files: string list) =
    { fakeBatchChecked files with
        ModelGeneration = Some generation }

/// Project `P`, whose command records each run in `runs`.
let private config root : TestPrunePlugin.TestConfig =
    let runs = Path.Combine(root, "runs")

    { Project = "P"
      Command = "sh"
      Args = $"-c \"echo run >> {runs}\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = TestPrunePlugin.AutoDetect }

[<Fact(Timeout = 120000)>]
let ``analysis retired by a model change is indexed when the same check is delivered again`` () =
    withTempDir "tp-index-replay" (fun root ->
        let files = writeProject root
        let results = checkAll root files

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host =
            FsHotWatch.PluginHost.PluginHost(sharedChecker.Value, root, taskCache = cache)

        host.WorkStore.PublishProjectModel(model 1L)
        let dbPath = Path.Combine(root, "tp.db")
        // Tests are configured: an analysis-only daemon selects nothing from the index.
        host.RegisterHandler(TestPrunePlugin.create dbPath root (Some [ config root ]) None None None None [])

        try
            // The first delivery is analysed under model 1, and the model is replaced
            // before its cohort seals: that analysis is retired, never indexed.
            for result in results do
                host.EmitFileChecked(stamped 1L result)

            waitForQuiescent host 20000
            host.WorkStore.PublishProjectModel(model 2L)

            // The same files, unchanged, checked again under the new model, then sealed.
            for result in results do
                host.EmitFileChecked(stamped 2L result)

            waitForQuiescent host 20000
            host.EmitBatchChecked(batch 2L files)
            waitForQuiescent host 20000

            let db = TestPrune.Database.Database.create dbPath

            let covering =
                db.QueryAffectedTests [ "P.Value.answer" ]
                |> List.map (fun t -> t.TestClass + "." + t.TestMethod)

            // Before the fix the second delivery replayed from the task cache, skipped the
            // analysis, and the index never learned the test: a change to `answer` read as
            // covered by nothing.
            test <@ covering |> List.exists (fun name -> name.Contains "observes the value") @>
        finally
            host.Teardown())

[<Fact(Timeout = 120000)>]
let ``a change is never read as covered by nothing while a retired file is missing from the index`` () =
    withTempDir "tp-index-unindexed" (fun root ->
        let files = writeProject root
        let value = files[1]
        let results = checkAll root files
        let runs = Path.Combine(root, "runs")

        let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value root
        host.WorkStore.PublishProjectModel(model 1L)

        host.RegisterHandler(
            TestPrunePlugin.create (Path.Combine(root, "tp.db")) root (Some [ config root ]) None None None None []
        )

        let runCount () =
            if File.Exists runs then
                File.ReadAllLines(runs).Length
            else
                0

        let settle (emit: unit -> unit) =
            emit ()
            waitForQuiescent host 30000

        try
            // Analysed under model 1; the model is replaced before the cohort seals, and
            // the test file is never delivered again. The seal launches the run the retired
            // debt owes.
            for result in results do
                host.EmitFileChecked(stamped 1L result)

            waitForQuiescent host 20000
            host.WorkStore.PublishProjectModel(model 2L)
            settle (fun () -> host.EmitBatchChecked(batch 2L files))
            settle (fun () -> host.EmitBuildCompleted BuildSucceeded)
            let before = runCount ()
            test <@ before >= 1 @>

            // A real change to what the test calls. Only this file is analysed again, so
            // the index knows `answer` but not the test that calls it.
            File.WriteAllText(value, "module P.Value\nlet answer () = 1 + 1\n")

            let edited =
                checkAll root files |> List.find (fun r -> AbsFilePath.value r.File = value)

            // A scan's order: the build lands first and holds its launch for the diff
            // (`DiffWait`), which the seal then folds and launches from.
            settle (fun () -> host.EmitBuildCompleted BuildSucceeded)
            settle (fun () -> host.EmitFileChecked(stamped 2L edited))
            settle (fun () -> host.EmitBatchChecked(batch 2L [ value ]))

            // Before the fix `answer` read as covered by nothing: the held launch took the
            // zero-test green, and the test that calls it never ran.
            test <@ runCount () > before @>
        finally
            host.Teardown())
