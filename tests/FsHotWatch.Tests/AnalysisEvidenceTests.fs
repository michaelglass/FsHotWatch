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
    ProjectModel.ofCompleted
        generation
        { Discovered = 1
          Loaded = 1
          OptionsMapped = 1
          Registered = 1 }

let private withCheckedSource action =
    withTempDir "analysis-evidence" (fun root ->
        let sourceFile = Path.Combine(root, "Lib.fs")
        let source = "module Lib\nlet answer = 42\n"
        File.WriteAllText(sourceFile, source)
        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let options =
            checker.GetProjectOptionsFromScript(
                sourceFile,
                FSharp.Compiler.Text.SourceText.ofString source,
                assumeDotNetFramework = false
            )
            |> Async.RunSynchronously
            |> fst

        pipeline.RegisterProject(sourceFile, options)

        let result =
            pipeline.CheckFile(AbsFilePath.create sourceFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "FCS returned no check result")

        action
            root
            { result with
                ModelGeneration = Some 1L })

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
                (Path.Combine(root, "analysis.db"))
                root
                None
                None
                None
                None
                None
                []

        host.RegisterHandler handler

        host.EmitFileCheckedTracked result
        |> List.iter (fun receipt -> receipt.Wait(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())

        let batch =
            { fakeBatchChecked [ AbsFilePath.value result.File ] with
                ModelGeneration = Some 1L }

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
        Assert.Equal<Set<AbsFilePath>>(files, analysis.CheckedFiles)
        let local = FsHotWatch.Cli.IpcParsing.DaemonEvidence.ofHost host

        let localReceipt =
            Assert.Single(FsHotWatch.Cli.IpcParsing.DaemonEvidence.receipts local)

        Assert.True localReceipt.RunId.IsNone
        Assert.Equal(1L, localReceipt.Generation)
        Assert.Empty localReceipt.Refusals

        let rpcConfig: FsHotWatch.Ipc.DaemonRpcConfig =
            { Host = host
              RequestShutdown = ignore
              RequestScan = ignore
              GetScanStatus = fun () -> "idle"
              GetScanGeneration = fun () -> 1L
              TriggerBuild = fun () -> async.Return()
              FormatAll = fun () -> async.Return ""
              WaitForScanGeneration = fun _ -> Tasks.Task.FromResult(())
              WaitForAllTerminal = fun _ -> Tasks.Task.FromResult(())
              RerunPlugin = fun _ -> async.Return(Ok())
              InvalidateCache = fun () -> Tasks.Task.FromResult(())
              GetUncheckedCount = fun () -> 0 }

        let wire = FsHotWatch.Ipc.DaemonRpcTarget(rpcConfig).GetDiagnostics("")
        let remote = FsHotWatch.Cli.IpcParsing.DaemonEvidence.parse wire

        Assert.Equal<FsHotWatch.Cli.IpcParsing.ModelReceipt list>(
            FsHotWatch.Cli.IpcParsing.DaemonEvidence.receipts local,
            FsHotWatch.Cli.IpcParsing.DaemonEvidence.receipts remote
        ))

[<Fact(Timeout = 30000)>]
let ``analysis proof refuses missing stale and failed file outcomes and configured tests`` () =
    withCheckedSource (fun _ result ->
        let files = Set.singleton result.File
        let good = AnalysisFileEvidence.fromResult result (Ok())
        let outcomes = Map.ofList [ result.File, good ]

        let proof entries =
            AnalysisEvidence.fromCompleted (Some 1L) files Set.empty entries |> Option.get

        Assert.Empty((proof outcomes).FailureReasons)
        Assert.NotEmpty((proof Map.empty).FailureReasons)

        let stale =
            AnalysisFileEvidence.fromResult
                { result with
                    ModelGeneration = Some 0L }
                (Ok())

        Assert.NotEmpty((proof (Map.ofList [ result.File, stale ])).FailureReasons)

        let failed =
            AnalysisFileEvidence.fromResult result (Error "symbol persistence failed")

        Assert.NotEmpty((proof (Map.ofList [ result.File, failed ])).FailureReasons)

        let parseOnly =
            AnalysisFileEvidence.fromResult { result with CheckResults = ParseOnly } (Ok())

        Assert.NotEmpty((proof (Map.ofList [ result.File, parseOnly ])).FailureReasons)
        Assert.True((AnalysisEvidence.fromCompleted None files Set.empty outcomes).IsNone)
        Assert.True((AnalysisEvidence.fromCompleted (Some -1L) files Set.empty outcomes).IsNone)
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


let private analysisContext root (result: FileCheckResult) generation =
    let graph =
        { ProjectGraphAccessor.none with
            ObserveModel = fun () -> available generation
            ObserveCheckableFiles = fun () -> Some(generation, Set.singleton result.File) }

    let ctx: PluginCtx<FsHotWatch.TestPrune.TestPrunePlugin.TestPruneMsg> =
        { ReportStatus = ignore
          ReportErrors = fun _ _ -> ()
          ClearErrors = ignore
          ClearAllErrors = ignore
          EmitBuildCompleted = ignore
          EmitTestRunStarted = ignore
          EmitTestProgress = ignore
          EmitTestRunCompleted = ignore
          EmitCommandCompleted = ignore
          Checker = sharedChecker.Value
          RepoRoot = root
          Post = ignore
          EnqueueExclusiveIntent = fun _ _ _ -> Tasks.Task.FromResult(())
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = ignore
          Log = ignore
          CompleteWithTimeout = ignore
          RunExclusive = fun _ _ -> Claimed
          RunExclusiveShared = fun _ _ _ _ _ -> SharedClaimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = graph }

    ctx

[<Fact(Timeout = 30000)>]
let ``queued old-model FileChecked cannot enter a newer analysis owner`` () =
    withCheckedSource (fun root result ->
        let update generation name =
            let ctx = analysisContext root result generation

            let handler =
                FsHotWatch.TestPrune.TestPrunePlugin.create
                    (Path.Combine(root, name + ".db"))
                    root
                    None
                    None
                    None
                    None
                    None
                    []

            handler.Update ctx handler.Init (FileChecked result)
            |> fun work -> Async.RunSynchronously(work, timeout = 5000)

        // The detector processes actual FCS evidence when its captured model is current.
        let current = update 1L "current"
        Assert.False(current.PendingAnalysis.IsEmpty)
        Assert.True(current.AnalysisFiles.ContainsKey result.File)

        // A dispatch admitted in generation 1 may sit in the mailbox until generation 2.
        // It must be rejected when folded, even though dispatch was valid at admission.
        let stale = update 2L "stale"
        Assert.True(stale.PendingAnalysis.IsEmpty)
        Assert.True(stale.AnalysisFiles.IsEmpty)
        Assert.True(stale.AnalysisReceipt.IsNone))


[<Fact(Timeout = 30000)>]
let ``new-model BuildCompleted cannot persist accepted old-model pending analysis`` () =
    let persistedNames afterGeneration =
        withCheckedSource (fun root result ->
            let dbPath = Path.Combine(root, "pending.db")

            let handler =
                FsHotWatch.TestPrune.TestPrunePlugin.create dbPath root None None None None None []

            let accepted =
                handler.Update (analysisContext root result 1L) handler.Init (FileChecked result)
                |> fun work -> Async.RunSynchronously(work, timeout = 5000)

            Assert.False(accepted.PendingAnalysis.IsEmpty)
            let db = TestPrune.Database.Database.create dbPath
            Assert.Empty(db.GetAllSymbolNames())

            handler.Update (analysisContext root result afterGeneration) accepted (BuildCompleted BuildSucceeded)
            |> fun work -> Async.RunSynchronously(work, timeout = 5000)
            |> ignore

            db.GetAllSymbolNames())

    // Same-model BuildCompleted really flushes the real FCS symbols into this database.
    Assert.NotEmpty(persistedNames 1L)
    // Cold scan ordering sends the new build before the new FCS results. The old
    // pending cohort must be retired before that build can flush into the new model.
    Assert.Empty(persistedNames 2L)

[<Fact(Timeout = 15000)>]
let ``available empty model earns no-suite analysis only after its actual batch seal`` () =
    withTempDir "analysis-empty-model" (fun root ->
        let host = PluginHost.create sharedChecker.Value root
        host.WorkStore.PublishProjectModelWithFiles(available 1L, Set.empty)

        host.SetProjectGraph
            { ProjectGraphAccessor.none with
                ObserveModel = fun () -> host.WorkSnapshot.ProjectModel
                ObserveCheckableFiles = fun () -> host.WorkSnapshot.ProjectModelFiles }

        host.RegisterHandler(
            FsHotWatch.TestPrune.TestPrunePlugin.create
                (Path.Combine(root, "analysis.db"))
                root
                None
                None
                None
                None
                None
                []
        )

        Assert.Empty(host.WorkSnapshot.AnalysisEvidence)

        let batch =
            { fakeBatchChecked [] with
                ModelGeneration = Some 1L }

        host.EmitBatchCheckedTracked batch
        |> List.iter (fun receipt -> receipt.Wait(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())

        let proof = Assert.Single(host.WorkSnapshot.AnalysisEvidence)
        Assert.Equal(1L, proof.Generation)
        Assert.Empty(proof.CheckedFiles)
        Assert.Empty(proof.FailureReasons)
        Assert.Empty(host.WorkSnapshot.Evidence))
