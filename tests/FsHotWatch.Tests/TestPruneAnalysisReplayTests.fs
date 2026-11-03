/// AUTOMATION-1482: a TestPrune with an empty impact index answers a replayed check from the
/// analysis another instance filed for the same check, with no live check, and its index
/// learns the analysis. A replay with no filed analysis is still demanded live.
module FsHotWatch.Tests.TestPruneAnalysisReplayTests

open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

let private source =
    """module Replayed

type FactAttribute() =
    inherit System.Attribute()

let compute x = x + 1

[<Fact>]
let computeTest () =
    let _ = compute 1
    ()
"""

/// One live check of `Replayed.fsx` under `root`, analysed by a TestPrune over `dbPath`.
let private analysedOnce (root: string) =
    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let file = Path.Combine(root, "Replayed.fsx")
    File.WriteAllText(file, source)
    let options = getScriptOptions checker file source |> Async.RunSynchronously
    pipeline.RegisterProject(file, options)

    let live =
        (pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously).Value

    let host = createModelHost checker root
    host.RegisterHandler(create (Path.Combine(root, "first.db")) root None None None None None [])

    try
        host.EmitFileChecked(stampFixture live)
        waitForQuiescent host 20000
    finally
        host.Teardown()

    live

/// `live` as a restart replays it from its check record.
let private replayOf (root: string) (live: FileCheckResult) =
    { live with
        CheckResults = Replayed (CheckRecords.CheckOutputs.ofResult (Some root) live).Value }

/// A TestPrune over a fresh index, handed `replayed`: the files it demanded live, its
/// status, and the symbols its index then holds for the file.
let private freshInstanceGiven (root: string) (replayed: FileCheckResult) =
    let checker = sharedChecker.Value
    let host = createModelHost checker root
    let demanded = ConcurrentQueue<string>()
    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject(AbsFilePath.value replayed.File, replayed.ProjectOptions)

    host.SetCheckDemand(fun replay ->
        demanded.Enqueue(Path.GetFileName(AbsFilePath.value replay.File))
        pipeline.CheckFileLive(replay.File, replay.ProjectOptions))

    let dbPath = Path.Combine(root, "fresh.db")
    host.RegisterHandler(create dbPath root None None None None None [])

    try
        host.EmitFileChecked(stampFixture replayed)
        waitForQuiescent host 20000
        // The cohort seals: its analyses are flushed to the index.
        host.EmitBatchChecked(fakeBatchChecked [ AbsFilePath.value replayed.File ])
        waitForQuiescent host 20000

        let symbols =
            (TestPrune.Database.Database.create dbPath).GetSymbolsInFile "Replayed.fsx"
            |> List.map _.FullName

        List.ofSeq demanded, host.GetStatus FsHotWatch.PluginActivity.TestPrunePluginName, symbols
    finally
        host.Teardown()

[<Fact(Timeout = 120000)>]
let ``a fresh index answers a replayed check from its filed analysis, and learns it`` () =
    withTempDir "tp-analysis-replay" (fun root ->
        let live = analysedOnce root
        let demanded, status, symbols = freshInstanceGiven root (replayOf root live)
        test <@ List.isEmpty demanded @>

        test
            <@
                match status with
                | Some(Failed _) -> false
                | _ -> true
            @>

        // The false-green guard: the index holds what a live analysis would have written.
        test <@ symbols |> List.exists (fun name -> name.EndsWith "compute") @>
        test <@ symbols |> List.exists (fun name -> name.EndsWith "computeTest") @>)

[<Fact(Timeout = 120000)>]
let ``a replay whose analysis was never filed is demanded live`` () =
    withTempDir "tp-analysis-replay-miss" (fun root ->
        let live = analysedOnce root
        // Another compiler answer for the same bytes: no analysis is filed under it.
        let other =
            match (replayOf root live).CheckResults with
            | Replayed outputs ->
                { live with
                    CheckResults =
                        Replayed
                            { outputs with
                                FcsSignature = outputs.FcsSignature + "-other" } }
            | _ -> failwith "not a replay"

        let demanded, _, symbols = freshInstanceGiven root other
        test <@ demanded = [ "Replayed.fsx" ] @>
        test <@ symbols |> List.exists (fun name -> name.EndsWith "computeTest") @>)
