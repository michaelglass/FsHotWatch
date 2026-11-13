/// A stopping daemon logs one line naming what stopped it (AUTOMATION-1675). Before,
/// "Daemon stopped." read the same for every cause, so nobody could tell who stopped a
/// measurement daemon three times in one afternoon.
module FsHotWatch.Tests.DaemonStopCauseTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.Program
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

/// A null checker is fine: these daemons never compile anything.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// Serve a daemon over IPC in `tmpDir`, stop it with `stop` once it is ready, and return
/// every line it logged up to the end of its run.
let private stoppedDaemonLog
    (tmpDir: string)
    (stop: Daemon -> string -> CancellationTokenSource -> unit)
    : string list =
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let logged = ConcurrentQueue<string>()

    use _sink =
        Logging.installSink
            { Write = logged.Enqueue
              Level = Logging.LogLevel.Info
              File = None }

    use cts = new CancellationTokenSource()
    let pipeName = $"fshw-{Guid.NewGuid():N}"
    let daemon = Daemon.createWith nullChecker tmpDir oneShotDaemonOptions
    let run = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    test <@ daemon.Ready.Wait(HangWait) @>
    stop daemon pipeName cts

    let ended =
        try
            run.Wait(HangWait)
        with :? AggregateException ->
            run.IsCompleted

    test <@ ended @>
    List.ofSeq logged

let private causeLines (logged: string list) =
    logged |> List.filter (fun line -> line.Contains "stop cause:")

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a daemon stopped over the Shutdown RPC logs the client's command, pid and cwd`` () =
    withTempDir "stop-rpc" (fun tmpDir ->
        let client: DaemonStop.StopClient =
            { Command = "fshw stop"
              Pid = Environment.ProcessId
              Cwd = tmpDir }

        let logged =
            stoppedDaemonLog tmpDir (fun _ pipeName _ ->
                Ipc.IpcClient.shutdown pipeName client |> Async.RunSynchronously |> ignore)

        let expected =
            $"stop cause: Shutdown RPC from pid=%d{Environment.ProcessId} cwd=%s{tmpDir} command=fshw stop"

        test <@ causeLines logged |> List.map (fun line -> line.EndsWith expected) = [ true ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an older CLI's bare Shutdown still stops the daemon, logged as a client that did not describe itself`` () =
    withTempDir "stop-bare" (fun tmpDir ->
        let logged =
            stoppedDaemonLog tmpDir (fun _ pipeName _ ->
                // What every CLI before AUTOMATION-1675 sends: the method, no arguments.
                use pipe =
                    new Pipes.NamedPipeClientStream(
                        ".",
                        pipeName,
                        Pipes.PipeDirection.InOut,
                        Pipes.PipeOptions.Asynchronous
                    )

                pipe.Connect(HangWaitMs)

                use rpc =
                    new StreamJsonRpc.JsonRpc(new StreamJsonRpc.HeaderDelimitedMessageHandler(pipe))

                rpc.StartListening()
                let reply = rpc.InvokeAsync<string>("Shutdown", [||]).Result
                test <@ reply = "shutting down" @>)

        test
            <@
                causeLines logged
                |> List.map (fun line ->
                    line.EndsWith
                        "stop cause: Shutdown RPC from a client that did not describe itself (an fshw CLI older than this daemon)") = [ true ]
            @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a daemon stopped by SIGTERM through the signal handler logs the signal`` () =
    withTempDir "stop-signal" (fun tmpDir ->
        let logged =
            stoppedDaemonLog tmpDir (fun daemon _ cts ->
                // The handler's seam, wired as the daemon's `installDaemonSignalHandlers` is.
                onDaemonSignal
                    (RunSignal())
                    (fun () -> [ daemon.ProcessRegistry ])
                    (fun cause -> daemon.Stop(cause, cts))
                    DaemonStop.SigTerm)

        test
            <@
                causeLines logged
                |> List.map (fun line -> line.EndsWith "stop cause: signal SIGTERM") = [ true ]
            @>)

/// The first cause wins: a daemon already stopping for one reason does not log a later one.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a stop record keeps the first cause and cancels on every request`` () =
    let record = DaemonStop.StopRecord()
    use first = new CancellationTokenSource()
    use second = new CancellationTokenSource()
    test <@ record.Cause = DaemonStop.Cancelled @>
    record.Request(DaemonStop.IdleExit, first)
    record.Request(DaemonStop.ConfigChanged, second)
    test <@ record.Cause = DaemonStop.IdleExit @>
    test <@ first.IsCancellationRequested && second.IsCancellationRequested @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every stop cause has its own line`` () =
    let described: DaemonStop.StopClient =
        { Command = "fshw test-rerun"
          Pid = 7
          Cwd = "/w" }

    let lines =
        [ DaemonStop.RpcStop
              { Rpc = DaemonStop.ReleaseIfSole
                Caller = DaemonStop.Described described }
          DaemonStop.Signal DaemonStop.SigInt
          DaemonStop.Signal DaemonStop.SigHup
          DaemonStop.ConfigChanged
          DaemonStop.IdleExit
          DaemonStop.RootVanished
          DaemonStop.Wedged
          DaemonStop.Cancelled ]
        |> List.map DaemonStop.line

    test <@ lines.Head = "stop cause: ReleaseIfSole RPC from pid=7 cwd=/w command=fshw test-rerun" @>
    test <@ lines |> List.distinct |> List.length = lines.Length @>

/// The stop RPCs of a daemon from before AUTOMATION-1675: no overload takes a description.
type OldDaemonTarget(accepted: ConcurrentQueue<string>) =
    member _.Shutdown() : string =
        accepted.Enqueue "Shutdown"
        "shutting down"

/// A new CLI against a daemon from before AUTOMATION-1675: that daemon has no overload
/// taking a description and answers that the method is missing, so the stop is asked
/// again bare rather than failing.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a described stop falls back to the bare call when the daemon lacks the overload`` () =
    let pipeName = $"fshw-{Guid.NewGuid():N}"
    let accepted = ConcurrentQueue<string>()

    // Two connections: the described call the old daemon refuses, then the bare one.
    let serve =
        Tasks.Task.Run(fun () ->
            task {
                for _ in 1..2 do
                    use server =
                        new Pipes.NamedPipeServerStream(
                            pipeName,
                            Pipes.PipeDirection.InOut,
                            1,
                            Pipes.PipeTransmissionMode.Byte,
                            Pipes.PipeOptions.Asynchronous
                        )

                    do! server.WaitForConnectionAsync()

                    use rpc =
                        new StreamJsonRpc.JsonRpc(
                            new StreamJsonRpc.HeaderDelimitedMessageHandler(server),
                            OldDaemonTarget accepted
                        )

                    rpc.StartListening()
                    do! rpc.Completion
            }
            :> Tasks.Task)

    let client = DaemonStop.StopClient.current ()
    let reply = Ipc.IpcClient.shutdown pipeName client |> Async.RunSynchronously
    test <@ reply = "shutting down" @>
    test <@ serve.Wait(HangWait) @>
    test <@ List.ofSeq accepted = [ "Shutdown" ] @>
    test <@ client.Pid = Environment.ProcessId && client.Cwd = Environment.CurrentDirectory @>
