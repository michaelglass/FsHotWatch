module FsHotWatch.Tests.AnalysisEvidenceTests

open System
open System.IO
open System.Threading
open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.CheckPipeline
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private available generation =
    ProjectModel.ofCompleted generation
        { Discovered = 1; Loaded = 1; OptionsMapped = 1; Registered = 1 }

let private withCheckedSource action =
    withTempDir "analysis-evidence" (fun root ->
        let sourceFile = Path.Combine(root, "Lib.fs")
        let source = "module Lib\nlet answer = 42\n"
        File.WriteAllText(sourceFile, source)
        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let options = getScriptOptions checker sourceFile source |> Async.RunSynchronously
        pipeline.RegisterProject(sourceFile, options)
        let result =
            pipeline.CheckFile(AbsFilePath.create sourceFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "FCS returned no check result")
        action root { result with ModelGeneration = Some 1L })

[<Fact(Timeout = 30000)>]
let ``analysis-only handler earns completion from a sealed actual analysis without inventing tests`` () =
    withCheckedSource (fun root result ->
        let host = PluginHost.create sharedChecker.Value root
        let files = Set.singleton result.File
        host.WorkStore.PublishProjectModelWithFiles(available 1L, files)
        host.SetProjectGraph
            { ProjectGraphAccessor.none with
                ObserveModel = fun () -> host.WorkSnapshot.ProjectModel
                ObserveCheckableFiles = fun () -> host.WorkSnapshot.ProjectModelFiles }
        let handler =
            FsHotWatch.TestPrune.TestPrunePlugin.create
                (Path.Combine(root, "analysis.db")) root None None None None None []
        host.RegisterHandler handler
        host.EmitFileCheckedTracked result
        |> List.iter (fun receipt -> receipt.Wait(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())
        let batch = { fakeBatchChecked [ AbsFilePath.value result.File ] with ModelGeneration = Some 1L }
        host.EmitBatchCheckedTracked batch
        |> List.iter (fun receipt -> receipt.Wait(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())
        // Positive controls: the real FCS result was processed and the complete cohort drained.
        Assert.True(host.IsFileChecked result.File)
        Assert.False(host.AnyPluginBusy())
        Daemon.waitForVerdict host (TimeSpan.FromSeconds 1.0) CancellationToken.None
        |> fun pending -> pending.GetAwaiter().GetResult()
        Assert.Empty host.WorkSnapshot.Evidence
        let analysis = Assert.Single host.WorkSnapshot.AnalysisEvidence
        Assert.Empty analysis.FailureReasons
        Assert.Equal(files, analysis.CheckedFiles))

[<Fact(Timeout = 30000)>]
let ``analysis proof refuses missing stale and failed file outcomes and configured tests`` () =
    withCheckedSource (fun _ result ->
        let files = Set.singleton result.File
        let good = AnalysisFileEvidence.fromResult result (Ok ())
        let outcomes = Map.ofList [ result.File, good ]
        let proof entries = AnalysisEvidence.fromCompleted (Some 1L) files Set.empty entries |> Option.get
        Assert.Empty((proof outcomes).FailureReasons)
        Assert.NotEmpty((proof Map.empty).FailureReasons)
        let stale = AnalysisFileEvidence.fromResult { result with ModelGeneration = Some 0L } (Ok ())
        Assert.NotEmpty((proof (Map.ofList [ result.File, stale ])).FailureReasons)
        let failed = AnalysisFileEvidence.fromResult result (Error "symbol persistence failed")
        Assert.NotEmpty((proof (Map.ofList [ result.File, failed ])).FailureReasons)
        let parseOnly = AnalysisFileEvidence.fromResult { result with CheckResults = ParseOnly } (Ok ())
        Assert.NotEmpty((proof (Map.ofList [ result.File, parseOnly ])).FailureReasons)
        Assert.True((AnalysisEvidence.fromCompleted None files Set.empty outcomes).IsNone)
        Assert.True((AnalysisEvidence.fromCompleted (Some 1L) Set.empty Set.empty outcomes).IsNone)
        Assert.True((AnalysisEvidence.fromCompleted (Some 1L) files (Set.singleton "Tests.fsproj") outcomes).IsNone))

[<Fact>]
let ``model replacement atomically retires previous checkable membership`` () =
    let store = PluginWorkOwner.Store()
    let oldFiles = Set.singleton (AbsFilePath.create "/tmp/Old.fs")
    let newFiles = Set.singleton (AbsFilePath.create "/tmp/New.fs")
    store.PublishProjectModelWithFiles(available 1L, oldFiles)
    let before = store.Snapshot
    store.PublishProjectModel(ProjectModel.Observation.Rediscovering 2L)
    Assert.True store.Snapshot.ProjectModelFiles.IsNone
    store.PublishProjectModelWithFiles(available 2L, newFiles)
    Assert.Equal(Some(2L, newFiles), store.Snapshot.ProjectModelFiles)
    Assert.Equal(Some(1L, oldFiles), before.ProjectModelFiles)
