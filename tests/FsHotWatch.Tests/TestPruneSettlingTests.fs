/// `test-results` and `test-scope` read the owner's committed state. Between a run's
/// worker finishing and its result fold committing, that state is still the PREVIOUS
/// run's: no worker is live, yet the run has not settled. These pause that fold through
/// the handler's own `Update` and read both observers inside the window, so the window
/// is held open deterministically rather than raced.
module FsHotWatch.Tests.TestPruneSettlingTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

/// The fold window, held open on demand. Once `Arm`ed, the next run-result fold signals
/// `Entered` and waits on `Release` before the real `Update` folds it.
type private FoldGate() =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member val Armed = false with get, set
    member _.Entered = entered.Task
    member _.Release() = release.TrySetResult() |> ignore

    member this.Wrap(handler: PluginHandler<TestPruneState, TestPruneMsg>) =
        { handler with
            Update =
                fun ctx state event ->
                    async {
                        match event with
                        | Custom(TestsFinished _)
                        | Custom(CommandTestsFinished _) when this.Armed ->
                            entered.TrySetResult() |> ignore
                            do! release.Task |> Async.AwaitTask
                        | _ -> ()

                        return! handler.Update ctx state event
                    } }

let private config: TestConfig =
    { Project = "TestProject"
      Command = "echo"
      Args = "ok"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      AlwaysClasses = [] }

let private read (host: FsHotWatch.PluginHost.PluginHost) name =
    host.RunCommand(name, [||]) |> Async.RunSynchronously |> Option.get

let private field (name: string) (json: string) =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
    | _ -> None

let private runIds (json: string) =
    use doc = JsonDocument.Parse json

    doc.RootElement.GetProperty("runIds").EnumerateArray()
    |> Seq.map (fun id -> id.GetString())
    |> Seq.toList

[<Fact(Timeout = 30000)>]
let ``test-results and test-scope report settling while a finished run's fold has not committed`` () =
    withTempDir "tp-settling" (fun tmpDir ->
        let host = createModelHost (Unchecked.defaultof<_>) tmpDir
        let gate = FoldGate()

        create ":memory:" tmpDir (Some [ config ]) None None None None []
        |> gate.Wrap
        |> host.RegisterHandler

        // A first run settles normally: its results become the committed ones.
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        let settledScope = read host "test-scope"
        let previousRuns = runIds settledScope
        test <@ List.length previousRuns = 1 @>
        // Positive control: outside the window neither observer says `settling`.
        test <@ field "status" (read host "test-results") <> Some "settling" @>
        test <@ field "kind" settledScope <> Some "settling" @>

        // A second run finishes; its result fold is held open.
        gate.Armed <- true
        let second = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        let entered = gate.Entered.Wait(TimeSpan.FromSeconds 20.0)
        test <@ entered @>

        try
            // The worker is gone and the fold has not committed. The committed state still
            // holds the FIRST run, so either observer answering from it would hand back the
            // previous run as though it were settled.
            let results = read host "test-results"
            let scope = read host "test-scope"
            test <@ (field "status" results, field "kind" scope) = (Some "settling", Some "settling") @>
            // It names no run as the one graded: the run it would name is the previous one.
            test <@ field "runId" scope = None @>
            // The session's completed runs still travel, so a reader's baseline is honest.
            test <@ runIds scope = previousRuns @>
        finally
            gate.Release()

        let finished = second.Wait(TimeSpan.FromSeconds 20.0)
        test <@ finished @>

        // Once the fold commits, both report the new run as settled.
        let after = read host "test-scope"
        test <@ field "kind" after <> Some "settling" @>
        test <@ List.length (runIds after) = 2 @>
        test <@ field "status" (read host "test-results") <> Some "settling" @>)
