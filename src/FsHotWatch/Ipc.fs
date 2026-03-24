module FsHotWatch.Ipc

open System
open System.IO.Pipes
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open StreamJsonRpc
open FsHotWatch.PluginHost
open FsHotWatch.Events

/// RPC target object exposed to clients via StreamJsonRpc.
type DaemonRpcTarget(host: PluginHost) =

    /// Returns a JSON string of all plugin statuses.
    member _.GetStatus() : string =
        let statuses = host.GetAllStatuses()

        let entries =
            statuses
            |> Map.map (fun _name status ->
                match status with
                | Idle -> "Idle"
                | Running since -> $"Running since {since:O}"
                | Completed(_, at) -> $"Completed at {at:O}"
                | Failed(error, at) -> $"Failed at {at:O}: {error}")

        JsonSerializer.Serialize(entries)

    /// Returns a single plugin's status or "not found".
    member _.GetPluginStatus(pluginName: string) : string =
        match host.GetStatus(pluginName) with
        | Some status ->
            match status with
            | Idle -> "Idle"
            | Running since -> $"Running since {since:O}"
            | Completed(_, at) -> $"Completed at {at:O}"
            | Failed(error, at) -> $"Failed at {at:O}: {error}"
        | None -> "not found"

    /// Runs a registered command by name and returns the result or "unknown command".
    member _.RunCommand(name: string, argsJson: string) : Task<string> =
        task {
            let args =
                if String.IsNullOrEmpty(argsJson) then
                    [||]
                else
                    [| argsJson |]

            let! result = host.RunCommand(name, args) |> Async.StartAsTask

            match result with
            | Some r -> return r
            | None -> return "unknown command"
        }

/// IPC server that listens on a named pipe and exposes plugin host methods via StreamJsonRpc.
module IpcServer =

    /// Start the IPC server. Accepts connections in a loop until cancelled.
    let start (pipeName: string) (host: PluginHost) (ct: CancellationToken) : Async<unit> =
        async {
            let target = DaemonRpcTarget(host)

            while not ct.IsCancellationRequested do
                let pipeServer =
                    new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)

                try
                    do!
                        pipeServer.WaitForConnectionAsync(ct)
                        |> Async.AwaitTask

                    let handler =
                        new HeaderDelimitedMessageHandler(pipeServer :> System.IO.Stream)

                    use rpc = new JsonRpc(handler, target)
                    rpc.StartListening()
                    do! rpc.Completion |> Async.AwaitTask
                with
                | :? OperationCanceledException -> pipeServer.Dispose()
                | _ -> pipeServer.Dispose()
        }

/// IPC client that connects to the daemon's named pipe and calls methods via StreamJsonRpc.
module IpcClient =

    /// Connect to the named pipe, invoke a method, and return the result.
    let private invoke (pipeName: string) (methodName: string) (args: obj array) : Async<string> =
        async {
            use pipeClient =
                new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            do! pipeClient.ConnectAsync(5000) |> Async.AwaitTask

            let handler =
                new HeaderDelimitedMessageHandler(pipeClient :> System.IO.Stream)

            use rpc = new JsonRpc(handler)
            rpc.StartListening()
            let! result = rpc.InvokeAsync<string>(methodName, args) |> Async.AwaitTask
            return result
        }

    /// Get all plugin statuses as a JSON string.
    let getStatus (pipeName: string) : Async<string> =
        invoke pipeName "GetStatus" [||]

    /// Get a single plugin's status.
    let getPluginStatus (pipeName: string) (pluginName: string) : Async<string> =
        invoke pipeName "GetPluginStatus" [| pluginName |]

    /// Run a registered command by name.
    let runCommand (pipeName: string) (name: string) (argsJson: string) : Async<string> =
        invoke pipeName "RunCommand" [| name; argsJson |]
