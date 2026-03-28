module FsHotWatch.Ipc

open System
open System.IO.Pipes
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open StreamJsonRpc
open FsHotWatch.Logging
open FsHotWatch.PluginHost
open FsHotWatch.Events

let private formatStatus (status: PluginStatus) =
    match status with
    | Idle -> "Idle"
    | Running since -> $"Running since {since:O}"
    | Completed(_, at) -> $"Completed at {at:O}"
    | Failed(error, at) -> $"Failed at {at:O}: {error}"

/// Configuration record for DaemonRpcTarget.
[<NoComparison; NoEquality>]
type DaemonRpcConfig =
    { Host: PluginHost
      RequestShutdown: unit -> unit
      RequestScan: bool -> unit
      GetScanStatus: unit -> string
      GetScanGeneration: unit -> int64
      TriggerBuild: unit -> Async<unit>
      FormatAll: unit -> Async<string>
      WaitForScanGeneration: int64 -> Task<unit>
      WaitForAllTerminal: unit -> Task<unit>
      InvalidateAndRecheck: string -> Async<string> }

/// RPC target object exposed to clients via StreamJsonRpc.
type DaemonRpcTarget(config: DaemonRpcConfig) =

    /// Returns a JSON string of all plugin statuses.
    member _.GetStatus() : string =
        let statuses = config.Host.GetAllStatuses()
        let entries = statuses |> Map.map (fun _name status -> formatStatus status)
        JsonSerializer.Serialize(entries)

    /// Returns a single plugin's status or "not found".
    member _.GetPluginStatus(pluginName: string) : string =
        match config.Host.GetStatus(pluginName) with
        | Some status -> formatStatus status
        | None -> "not found"

    /// Runs a registered command by name and returns the result or "unknown command".
    member _.RunCommand(name: string, argsJson: string) : Task<string> =
        task {
            let args =
                if String.IsNullOrEmpty(argsJson) then
                    [||]
                else
                    [| argsJson |]

            let! result = config.Host.RunCommand(name, args) |> Async.StartAsTask

            match result with
            | Some r -> return r
            | None -> return "unknown command"
        }

    /// Gracefully shut down the daemon.
    member _.Shutdown() : string =
        config.RequestShutdown()
        "shutting down"

    /// Trigger a full scan. Returns the generation counter to pass to WaitForScan.
    /// When force=true, bypasses the jj guard to re-check all files regardless of commit_id.
    member _.Scan(force: bool) : string =
        let gen = config.GetScanGeneration()
        config.RequestScan(force)
        $"scan started:%d{gen}"

    /// Get current scan progress without blocking.
    member _.ScanStatus() : string = config.GetScanStatus()

    /// Query the error ledger. If pluginFilter is empty, return all errors; otherwise filter to that plugin.
    member _.GetErrors(pluginFilter: string) : string =
        let allErrors =
            if System.String.IsNullOrEmpty(pluginFilter) then
                config.Host.GetErrors()
                |> Map.map (fun _file entries ->
                    entries
                    |> List.map (fun (plugin, e) ->
                        {| plugin = plugin
                           message = e.Message
                           severity = e.Severity
                           line = e.Line
                           column = e.Column |}))
            else
                config.Host.GetErrorsByPlugin(pluginFilter)
                |> Map.map (fun _file entries ->
                    entries
                    |> List.map (fun e ->
                        {| plugin = pluginFilter
                           message = e.Message
                           severity = e.Severity
                           line = e.Line
                           column = e.Column |}))

        let count = allErrors |> Map.fold (fun acc _ entries -> acc + entries.Length) 0

        let statuses =
            config.Host.GetAllStatuses()
            |> Map.map (fun _name status -> formatStatus status)

        let result =
            {| count = count
               files = allErrors
               statuses = statuses |}

        JsonSerializer.Serialize(result)

    /// Wait for scan generation to advance past afterGeneration, then return the final status.
    /// Negative afterGeneration means "wait for any scan completion" (legacy path).
    member _.WaitForScan(afterGeneration: int64) : Task<string> =
        task {
            Logging.debug "rpc" $"WaitForScan(%d{afterGeneration}) called"
            do! config.WaitForScanGeneration(afterGeneration)
            return config.GetScanStatus()
        }

    /// Wait for all plugins to reach a terminal state with 1s stability confirmation.
    member this.WaitForComplete() : Task<string> =
        task {
            let statuses = config.Host.GetAllStatuses()

            let statusMap =
                statuses
                |> Map.map (fun _ s ->
                    match s with
                    | Events.Running _ -> "Running"
                    | Events.Completed _ -> "Completed"
                    | Events.Failed _ -> "Failed"
                    | Events.Idle -> "Idle")

            Logging.debug "rpc" $"WaitForComplete() called, statuses: %A{statusMap}"

            do! config.WaitForAllTerminal()
            Logging.debug "rpc" "WaitForComplete() resolved"
            return this.GetStatus()
        }

    /// Trigger a build by emitting SourceChanged for all registered files, then wait for completion.
    member this.TriggerBuild() : Task<string> =
        task {
            do! config.TriggerBuild() |> Async.StartAsTask
            let! _ = this.WaitForComplete()
            return this.GetStatus()
        }

    /// Invalidate cache for a file and re-check it.
    member _.InvalidateCache(filePath: string) : Task<string> =
        task {
            let! result = config.InvalidateAndRecheck filePath |> Async.StartAsTask
            return result
        }

    /// Run all preprocessors on all registered files and return a summary.
    member _.FormatAll() : Task<string> =
        task {
            let! result = config.FormatAll() |> Async.StartAsTask
            return result
        }

/// IPC server that listens on a named pipe and exposes plugin host methods via StreamJsonRpc.
module IpcServer =

    /// Accept a single connection, handle it, and clean up when done.
    let private acceptOne (pipeName: string) (target: DaemonRpcTarget) (ct: CancellationToken) : Async<unit> =
        async {
            let pipeServer =
                new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                )

            try
                do! pipeServer.WaitForConnectionAsync(ct) |> Async.AwaitTask

                let handler = new HeaderDelimitedMessageHandler(pipeServer :> System.IO.Stream)

                let rpc = new JsonRpc(handler, target)
                rpc.StartListening()

                rpc.Completion.ContinueWith(fun _ ->
                    rpc.Dispose()
                    pipeServer.Dispose())
                |> ignore
            with
            | :? OperationCanceledException -> pipeServer.Dispose()
            | _ -> pipeServer.Dispose()
        }

    /// Start the IPC server. Keeps multiple accept tasks running concurrently
    /// so clients don't have to wait for the accept loop to cycle.
    let start (pipeName: string) (config: DaemonRpcConfig) (cts: CancellationTokenSource) : Async<unit> =
        async {
            let target = DaemonRpcTarget(config)

            // Keep 3 accept tasks running at all times so clients can connect immediately
            let mutable acceptTasks: Task list = []

            let startAccept () =
                Async.StartAsTask(acceptOne pipeName target cts.Token) :> Task

            // Seed with 3 concurrent acceptors
            acceptTasks <- [ startAccept (); startAccept (); startAccept () ]

            while not cts.Token.IsCancellationRequested do
                try
                    // Wait for any accept to complete
                    let! completed = Task.WhenAny(acceptTasks |> List.toArray) |> Async.AwaitTask

                    // Replace completed task with a new accept
                    acceptTasks <-
                        acceptTasks
                        |> List.map (fun t ->
                            if Object.ReferenceEquals(t, completed) then
                                startAccept ()
                            else
                                t)
                with
                | :? OperationCanceledException -> ()
                | _ -> ()
        }

/// IPC client that connects to the daemon's named pipe and calls methods via StreamJsonRpc.
module IpcClient =

    /// Connect to the named pipe, invoke a method, and return the result.
    let private invoke (pipeName: string) (methodName: string) (args: obj array) : Async<string> =
        async {
            use pipeClient =
                new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            do! pipeClient.ConnectAsync(5000) |> Async.AwaitTask

            let handler = new HeaderDelimitedMessageHandler(pipeClient :> System.IO.Stream)

            use rpc = new JsonRpc(handler)
            rpc.StartListening()
            let! result = rpc.InvokeAsync<string>(methodName, args) |> Async.AwaitTask
            return result
        }

    /// Get all plugin statuses as a JSON string.
    let getStatus (pipeName: string) : Async<string> = invoke pipeName "GetStatus" [||]

    /// Get a single plugin's status.
    let getPluginStatus (pipeName: string) (pluginName: string) : Async<string> =
        invoke pipeName "GetPluginStatus" [| pluginName |]

    /// Run a registered command by name.
    let runCommand (pipeName: string) (name: string) (argsJson: string) : Async<string> =
        invoke pipeName "RunCommand" [| name; argsJson |]

    /// Shut down the daemon gracefully.
    let shutdown (pipeName: string) : Async<string> = invoke pipeName "Shutdown" [||]

    /// Trigger a full scan of all registered files. When force=true, bypasses jj guard.
    let scan (pipeName: string) (force: bool) : Async<string> = invoke pipeName "Scan" [| force |]

    /// Get current scan progress.
    let scanStatus (pipeName: string) : Async<string> = invoke pipeName "ScanStatus" [||]

    /// Get errors, optionally filtered by plugin name.
    let getErrors (pipeName: string) (pluginFilter: string) : Async<string> =
        invoke pipeName "GetErrors" [| pluginFilter |]

    /// Wait for scan to complete, then return final status.
    let waitForScan (pipeName: string) (afterGeneration: int64) : Async<string> =
        invoke pipeName "WaitForScan" [| afterGeneration |]

    /// Wait for all plugins to reach a terminal state, then return full status.
    let waitForComplete (pipeName: string) : Async<string> = invoke pipeName "WaitForComplete" [||]

    /// Trigger a build and wait for it to complete.
    let triggerBuild (pipeName: string) : Async<string> = invoke pipeName "TriggerBuild" [||]

    /// Run all preprocessors on all registered files.
    let formatAll (pipeName: string) : Async<string> = invoke pipeName "FormatAll" [||]

    /// Invalidate cache for a file and re-check it.
    let invalidateCache (pipeName: string) (filePath: string) : Async<string> =
        invoke pipeName "InvalidateCache" [| filePath |]

    /// Quick probe to check if a daemon is listening on the named pipe.
    let isRunning (pipeName: string) : bool =
        try
            use pipe =
                new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            pipe.ConnectAsync(500).Wait()
            true
        with _ ->
            false
