/// A daemon accepts connections once it has started serving, however busy the thread
/// pool is.
///
/// A server creates its listening instances before its first wait, so a server started
/// on the caller's thread is listening when the start returns. One queued to the thread
/// pool is listening only once the pool gets to it: on a loaded box, after a client
/// probing for the daemon has given up. These tests hold every pool thread, so a start
/// that was queued has visibly not happened yet.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ServeOrderingTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 60000)>]
let ``a daemon accepts connections once RunWith has started serving, however busy the thread pool`` () =
    withTempDir "serve-ordering" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        let pipeName = FsHotWatch.Cli.Program.computePipeName dir
        use cts = new CancellationTokenSource()

        use daemon =
            Daemon.createWith (Unchecked.defaultof<_>) dir Daemon.DaemonOptions.defaults

        let listening, run =
            withEveryPoolThreadBusy (fun () ->
                // On this thread, `RunWith` runs up to its first wait, which comes after
                // it starts serving.
                let run = Async.StartImmediateAsTask(daemon.RunWithIpc(pipeName, cts))
                IpcServer.acceptsConnection pipeName, run)

        try
            test <@ listening @>
        finally
            cts.Cancel()
            run.Wait(TimeSpan.FromSeconds 30.0) |> ignore)
