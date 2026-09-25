/// A hook step is a subtask of the run it holds: while a `tests.beforeRun` step runs,
/// the wait and wedge lines name it by index, command, pid and bound, and it stops being
/// named the moment the step exits — green, red, or reaped.
module FsHotWatch.Tests.HookStepSubtaskTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.PluginHost
open FsHotWatch.Daemon
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

/// Every step a tracker was handed, and which of them it has released.
type private Recorder() =
    let started = Collections.Concurrent.ConcurrentQueue<HookStep.Running>()
    let ended = Collections.Concurrent.ConcurrentQueue<int>()

    member _.Track: HookStep.Tracker =
        fun step ->
            started.Enqueue step

            { new IDisposable with
                member _.Dispose() = ended.Enqueue step.Pid }

    member _.Started = List.ofSeq started
    member _.Ended = List.ofSeq ended

let private pidIn (text: string) =
    let m = Regex.Match(text, @"\(pid (\d+),")
    if m.Success then Some(int m.Groups.[1].Value) else None

let private isAlive (pid: int) =
    try
        not (Process.GetProcessById(pid).HasExited)
    with :? ArgumentException ->
        false

let private stepSubtasks (host: PluginHost) =
    host.GetActivitySnapshot(PluginActivity.TestPrunePluginName).Subtasks
    |> List.filter (fun t -> t.Key.StartsWith("beforeRun step ", StringComparison.Ordinal))

[<Fact>]
let ``a running step is described by label, index, command, pid and bound`` () =
    let step: HookStep.Running =
        { Label = "beforeRun"
          StepIndex = 9
          StepCount = 17
          Command = "dotnet run --project src/Build --no-build -- csrf-gate"
          Pid = 12345
          Bound = Some(TimeSpan.FromMinutes 10.0) }

    test
        <@
            HookStep.describe step = "beforeRun step 9/17 `dotnet run --project src/Build --no-build -- csrf-gate` (pid 12345, bound 10m 0s)"
        @>

    test
        <@
            HookStep.describe { step with Bound = None }
            |> fun s -> s.EndsWith("(pid 12345, no bound)")
        @>

[<Fact(Timeout = 30000)>]
let ``a held-open tests.beforeRun step is named by index, command and pid in the wait and wedge lines`` () =
    withTempDir "hookstep-held" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let beforeRun =
            Some(fun (_: Guid) (track: HookStep.Tracker) ->
                match runShellSteps "beforeRun" (Some 60) tmpDir track [ "true"; "sleep 30" ] with
                | HookOk _ -> ()
                | HookFailed(_, failure) -> failwith (HookFailure.describe failure))

        let host = createModelHost (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create ":memory:" tmpDir (Some configs) None beforeRun None None [])

        let await = beginAwaitNextTerminal host PluginActivity.TestPrunePluginName

        host.RunCommand("run-tests", [| "{}" |])
        |> Async.Ignore
        |> Async.StartAsTask
        |> ignore

        let holdingStep2 () =
            stepSubtasks host
            |> List.exists (fun t -> t.Key.StartsWith "beforeRun step 2/2 ")

        test <@ waitUntilTrue holdingStep2 15000 @>

        // Only the step holding the run is named: step 1 ended when its child exited.
        let held = stepSubtasks host
        test <@ List.length held = 1 @>

        test
            <@
                held
                |> List.map _.Key
                |> List.forall (fun k -> k.StartsWith "beforeRun step 2/2 `sleep 30` (pid ")
            @>

        test <@ held |> List.map _.Key |> List.forall (fun k -> k.EndsWith ", bound 1m 0s)") @>

        let pid = held |> List.head |> _.Key |> pidIn |> Option.get
        test <@ isAlive pid @>

        let subtasks =
            host.GetActivitySnapshot(PluginActivity.TestPrunePluginName).Subtasks
            |> List.map (fun t -> t.Key, t.StartedAt)

        let now = DateTime.UtcNow
        let waitLine = formatPluginWait now PluginActivity.TestPrunePluginName now subtasks

        let wedgeLine =
            PluginWedge.wedgeRecoveryMessage PluginActivity.TestPrunePluginName now TimeSpan.Zero
            + PluginWedge.awaitingSuffix (PluginWedge.describeAwaiting now subtasks [] 0)

        test <@ waitLine.Contains $"beforeRun step 2/2 `sleep 30` (pid %d{pid}, bound 1m 0s)" @>
        test <@ wedgeLine.Contains $"— on: " @>
        test <@ wedgeLine.Contains $"beforeRun step 2/2 `sleep 30` (pid %d{pid}, bound 1m 0s)" @>

        test <@ not (waitLine.Contains "step 1/2") @>

        // A step that exits red stops being named: its subtask ends with it.
        Process.GetProcessById(pid).Kill()
        test <@ waitUntilTrue (fun () -> List.isEmpty (stepSubtasks host)) 10000 @>
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore)

[<Fact(Timeout = 15000)>]
let ``every step is released when it exits, including the one that fails`` () =
    withTempDir "hookstep-fail" (fun tmpDir ->
        let recorder = Recorder()

        match runShellSteps "beforeRun" (Some 60) tmpDir recorder.Track [ "true"; "exit 3"; "true" ] with
        | HookOk _ -> failwith "expected the chain to fail"
        | HookFailed _ -> ()

        test <@ recorder.Started |> List.map (fun s -> s.StepIndex, s.StepCount) = [ (1, 3); (2, 3) ] @>
        test <@ recorder.Started |> List.map _.Command = [ "true"; "exit 3" ] @>

        test
            <@
                recorder.Started
                |> List.forall (fun s -> s.Bound = Some(TimeSpan.FromSeconds 60.0))
            @>

        test <@ recorder.Ended = (recorder.Started |> List.map _.Pid) @>)

[<Fact(Timeout = 15000)>]
let ``a step reaped by its process scope is released, not left named`` () =
    withTempDir "hookstep-reaped" (fun tmpDir ->
        let recorder = Recorder()
        let scope = ProcessRegistry.Registry()

        let chain =
            Tasks.Task.Run(fun () ->
                use _installed = ProcessRegistry.install scope
                runShellSteps "beforeRun" None tmpDir recorder.Track [ "sleep 30"; "true" ])

        test <@ waitUntilTrue (fun () -> not (List.isEmpty recorder.Started)) 10000 @>
        let pid = (List.head recorder.Started).Pid
        test <@ isAlive pid @>
        test <@ (List.head recorder.Started).Bound = None @>
        test <@ List.isEmpty recorder.Ended @>

        // What an interrupted run does to the hook it is inside.
        scope.KillAll()

        test <@ chain.Wait(TimeSpan.FromSeconds 10.0) @>
        test <@ recorder.Ended = [ pid ] @>
        test <@ recorder.Started |> List.length = 1 @>

        match chain.Result with
        | HookOk _ -> failwith "a reaped step cannot be green"
        | HookFailed _ -> ())
