module FsHotWatch.Tests.AnalysisEvidenceTests

open System.IO
open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.CheckPipeline
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

let private available generation =
    ProjectModel.ofCompleted
        generation
        { Discovered = 1
          Loaded = 1
          OptionsMapped = 1
          Registered = 1 }

[<Fact>]
let ``model replacement atomically retires previous checkable membership`` () =
    let store = PluginWorkOwner.Store()
    let oldFiles = Set.singleton (AbsFilePath.create "/tmp/Old.fs")
    let newFiles = Set.singleton (AbsFilePath.create "/tmp/New.fs")
    store.PublishProjectModelWithFiles(available 1L, oldFiles)
    let before = store.Snapshot
    Assert.Equal(available 1L, before.ProjectModel)
    store.PublishProjectModel(ProjectModel.Observation.Rediscovering 2L)
    Assert.Equal(ProjectModel.Observation.Rediscovering 2L, store.Snapshot.ProjectModel)
    Assert.True store.Snapshot.ProjectModelFiles.IsNone
    store.PublishProjectModelWithFiles(available 2L, newFiles)
    Assert.Equal(Some(2L, newFiles), store.Snapshot.ProjectModelFiles)
    // A pinned publication keeps the membership it was published with.
    Assert.Equal(Some(1L, oldFiles), before.ProjectModelFiles)

/// Check a real source with the warm checker, stamped as captured against generation 1.
let private withCheckedSource action =
    withTempDir "analysis-evidence" (fun root ->
        let source = "module Lib\nlet answer = 42\n"
        let sourceFile = Path.Combine(root, "Lib.fs")
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

        match result.CheckResults with
        | FullCheck _ -> ()
        | ParseOnly -> failwith "the fixture requires an actual completed type check"

        action
            root
            { result with
                ModelGeneration = Some 1L })

/// A TestPrune context whose host currently publishes `generation` as its model.
let private analysisContext root generation : PluginCtx<TestPruneMsg> =
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
      EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
      StartSubtask = fun _ _ -> ()
      UpdateSubtask = fun _ _ -> ()
      EndSubtask = ignore
      Log = ignore
      CompleteWithTimeout = ignore
      RunExclusive = fun _ _ -> Claimed
      RunExclusiveShared = fun _ _ _ _ _ -> SharedClaimed
      IsRunning = fun _ -> false
      FcsSuppressedCodes = Set.empty
      ProjectGraph =
        { ProjectGraphAccessor.none with
            ObserveModel = fun () -> available generation } }

let private analysisHandler root name =
    create (Path.Combine(root, name + ".db")) root None None None None None []

[<Fact(Timeout = 30000)>]
let ``queued old-model FileChecked cannot enter a newer analysis owner`` () =
    withCheckedSource (fun root result ->
        let update generation name =
            let handler = analysisHandler root name

            handler.Update (analysisContext root generation) handler.Init (FileChecked result)
            |> fun work -> Async.RunSynchronously(work, timeout = 5000)

        // Positive control: the analysis folds the actual FCS result under its own model.
        let current = update 1L "current"
        Assert.False(current.PendingAnalysis.IsEmpty)

        // A dispatch admitted under generation 1 may still be queued when generation 2
        // is published. Folded then, it must not enter the newer model's analysis.
        let stale = update 2L "stale"
        Assert.True(stale.PendingAnalysis.IsEmpty)
        Assert.True(stale.ChangedSymbols.IsEmpty)
        Assert.True(stale.TestClassFiles.IsEmpty))

[<Fact(Timeout = 30000)>]
let ``new-model BuildCompleted cannot persist accepted old-model pending analysis`` () =
    let persistedNames afterGeneration =
        withCheckedSource (fun root result ->
            let handler = analysisHandler root "pending"

            let accepted =
                handler.Update (analysisContext root 1L) handler.Init (FileChecked result)
                |> fun work -> Async.RunSynchronously(work, timeout = 5000)

            Assert.False(accepted.PendingAnalysis.IsEmpty)
            let db = TestPrune.Database.Database.create (Path.Combine(root, "pending.db"))
            Assert.Empty(db.GetAllSymbolNames())

            handler.Update (analysisContext root afterGeneration) accepted (BuildCompleted BuildSucceeded)
            |> fun work -> Async.RunSynchronously(work, timeout = 5000)
            |> ignore

            db.GetAllSymbolNames())

    // Positive control: a same-model BuildCompleted flushes the actual FCS symbols.
    Assert.NotEmpty(persistedNames 1L)
    // A rediscovery can publish its build before its first FCS result. The previous
    // model's pending analysis must be retired before that build can flush it.
    Assert.Empty(persistedNames 2L)

[<Fact(Timeout = 30000)>]
let ``an unstamped FileChecked or BatchChecked cannot enter the analysis owner`` () =
    withCheckedSource (fun root result ->
        let handler = analysisHandler root "unstamped"
        let ctx = analysisContext root 1L

        let fold state event =
            handler.Update ctx state event
            |> fun work -> Async.RunSynchronously(work, timeout = 5000)

        let unstamped = { result with ModelGeneration = None }
        let refused = fold handler.Init (FileChecked unstamped)
        // A result nobody captured against a model cannot say which model it describes.
        Assert.True(refused.PendingAnalysis.IsEmpty)
        Assert.True(refused.ChangedSymbols.IsEmpty)

        // Positive control: the stamped result folds, then an unstamped seal cannot flush it.
        let accepted = fold handler.Init (FileChecked result)
        Assert.False(accepted.PendingAnalysis.IsEmpty)

        let sealedWithoutModel =
            fold
                accepted
                (BatchChecked
                    { fakeBatchChecked [ AbsFilePath.value result.File ] with
                        ModelGeneration = None })

        Assert.False(sealedWithoutModel.PendingAnalysis.IsEmpty)
        let db = TestPrune.Database.Database.create (Path.Combine(root, "unstamped.db"))
        Assert.Empty(db.GetAllSymbolNames()))

/// What a host publishing generation 1 persists after a FileChecked and its seal, each
/// stamped as given. The stamps are explicit: no fixture helper may supply them.
let private persistedThroughHost (fileStamp: int64 option) (sealStamp: int64 option) =
    withCheckedSource (fun root result ->
        let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value root
        host.WorkStore.PublishProjectModel(available 1L)
        host.RegisterHandler(analysisHandler root "host")

        host.EmitFileChecked
            { result with
                ModelGeneration = fileStamp }

        host.EmitBatchChecked
            { fakeBatchChecked [ AbsFilePath.value result.File ] with
                ModelGeneration = sealStamp }

        waitForQuiescent host 10000
        let db = TestPrune.Database.Database.create (Path.Combine(root, "host.db"))
        db.GetAllSymbolNames())

[<Theory(Timeout = 60000)>]
[<InlineData("file", "none")>]
[<InlineData("file", "other")>]
[<InlineData("seal", "none")>]
[<InlineData("seal", "other")>]
let ``a host with a published model refuses a result or seal that names no model or another one``
    (refusedEvent: string, stamp: string)
    =
    let refused =
        match stamp with
        | "none" -> None
        | _ -> Some 2L

    // Positive control: both stamped with the published generation persist real symbols.
    Assert.NotEmpty(persistedThroughHost (Some 1L) (Some 1L))

    let persisted =
        match refusedEvent with
        | "file" -> persistedThroughHost refused (Some 1L)
        | _ -> persistedThroughHost (Some 1L) refused

    Assert.Empty persisted
