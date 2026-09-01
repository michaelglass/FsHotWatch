module FsHotWatch.Ipc

open System
open System.IO.Pipes
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open StreamJsonRpc
open FsHotWatch.Logging
open FsHotWatch.PluginHost

/// Filter for cache clear operations.
type CacheClearFilter =
    | ClearAll
    | ClearPlugin of plugin: string
    | ClearFile of file: string
    | ClearPluginFile of plugin: string * file: string

open FsHotWatch.Events
open FsHotWatch.ErrorLedger

let private severityToString = DiagnosticSeverity.toString

/// Distinctive prefix marking the `RunCommand` reply as "the plugin host did not
/// recognize this command" (host `RunCommand` returned `None`). A fixed sentinel
/// rather than free-form JSON so the CLI can detect it with a total
/// `String.StartsWith` check. Real plugin commands return their own textual or
/// JSON payloads and never emit this `fshw-`-namespaced control reply.
[<Literal>]
let UnknownCommandSentinelPrefix = "fshw-unknown-command:"

/// Sentinel returned by `RunCommand` over IPC when the daemon's plugin host does not
/// recognize the requested command. Carries the command name so the consumer can
/// render an actionable error without re-deriving it.
let unknownCommandReply (name: string) : string = UnknownCommandSentinelPrefix + name

/// True when `reply` is the `RunCommand` unknown-command sentinel produced by
/// `unknownCommandReply`. Total — any other (real) plugin output returns false.
let isUnknownCommandReply (reply: string) : bool =
    not (isNull reply)
    && reply.StartsWith(UnknownCommandSentinelPrefix, StringComparison.Ordinal)

/// Default hard bound on a `WaitForComplete` verdict wait when the client
/// imposes no timeout of its own. Deliberately generous — a legitimate cold
/// full-suite check (build + every test project) finishes well inside an hour,
/// so this only ever trips on a genuinely-wedged plugin.
let DefaultVerdictDeadline = TimeSpan.FromMinutes 60.0

/// Resolve the verdict-wait deadline from an optional override string (the
/// `FSHW_VERDICT_DEADLINE_SEC` env value). A positive integer count of seconds
/// wins; anything else (absent, unparseable, non-positive) falls back to
/// `DefaultVerdictDeadline` — there is intentionally NO "infinite" setting.
/// Pure so the precedence is unit-testable without touching process env.
/// Mirrors `ProcessHelper.resolveLaunchDeadline`.
let resolveVerdictDeadline (overrideSec: string option) : TimeSpan =
    match overrideSec with
    | Some s ->
        match Int32.TryParse(s: string) with
        | true, n when n > 0 -> TimeSpan.FromSeconds(float n)
        | _ -> DefaultVerdictDeadline
    | None -> DefaultVerdictDeadline

/// The ambient RPC deadline: `FSHW_VERDICT_DEADLINE_SEC`, else 60 min.
let internal ambientRpcDeadline () =
    Environment.GetEnvironmentVariable "FSHW_VERDICT_DEADLINE_SEC"
    |> Option.ofObj
    |> resolveVerdictDeadline

/// Grace added to the deadline at the tracking SEAM, on top of whatever an RPC
/// bounds itself by.
///
/// The seam (`trackedTask`) is a backstop, not the primary bound. An RPC that
/// bounds its own wait — `WaitForComplete` does, and names the still-running
/// plugins when it fires — gives a far better diagnostic than a generic "RPC
/// exceeded its deadline", so it must win the race; the grace guarantees it does.
/// In exchange the seam guarantees that an RPC bounding itself by NOTHING still
/// cannot wait forever, without anyone having to remember to bound it.
let internal RpcDeadlineGrace = TimeSpan.FromSeconds 30.0

/// Serialize PluginStatus as a tagged JSON variant so consumers can parse the
/// state without string matching. Carries the state tag, timestamps, and the
/// failure diagnosis ONLY — the verdict (summary + elapsed) travels exclusively in
/// `lastRun`. One channel on the wire, so the status line and the run record cannot
/// disagree.
let private statusPayload (status: PluginStatus) : obj =
    match status with
    | Idle -> {| tag = "idle" |} :> obj
    | Running since ->
        {| tag = "running"
           since = since.ToString("O") |}
        :> obj
    | Completed(at, _) ->
        {| tag = "completed"
           at = at.ToString("O") |}
        :> obj
    | Failed(error, at, _) ->
        {| tag = "failed"
           error = error
           at = at.ToString("O") |}
        :> obj

/// Serialize RunOutcome as a tagged JSON variant.
let private outcomePayload (outcome: RunOutcome) : obj =
    match outcome with
    | CompletedRun -> {| tag = "completed" |} :> obj
    | FailedRun e -> {| tag = "failed"; error = e |} :> obj
    | TimedOut r -> {| tag = "timedOut"; reason = r |} :> obj

let private pluginStatusPayload
    (host: PluginHost)
    (counts: Map<string, DiagnosticCounts>)
    (name: string)
    (status: PluginStatus)
    : obj =
    let snap = host.GetActivitySnapshot(name)

    let subtasks =
        snap.Subtasks
        |> List.map (fun t ->
            {| key = t.Key
               label = t.Label
               startedAt = t.StartedAt.ToString("O") |}
            :> obj)

    let lastRun: obj =
        match snap.LastRun with
        | None -> null
        | Some r ->
            let summary =
                match r.Summary with
                | Some s -> box s
                | None -> null

            {| startedAt = r.StartedAt.ToString("O")
               elapsedMs = int64 r.Elapsed.TotalMilliseconds
               outcome = outcomePayload r.Outcome
               summary = summary
               activityTail = r.ActivityTail |}
            :> obj

    let d = Map.tryFind name counts |> Option.defaultValue DiagnosticCounts.empty

    {| status = statusPayload status
       subtasks = subtasks
       activityTail = snap.ActivityTail
       lastRun = lastRun
       diagnostics =
        {| errors = d.Errors
           warnings = d.Warnings |} |}
    :> obj

/// Configuration record for DaemonRpcTarget.
[<NoComparison; NoEquality>]
type DaemonRpcConfig =
    {
        Host: PluginHost
        RequestShutdown: unit -> unit
        RequestScan: unit -> unit
        GetScanStatus: unit -> string
        GetScanGeneration: unit -> int64
        TriggerBuild: unit -> Async<unit>
        FormatAll: unit -> Async<string>
        WaitForScanGeneration: int64 -> Task<unit>
        WaitForAllTerminal: TimeSpan -> Task<unit>
        RerunPlugin: string -> Async<Result<unit, string>>
        /// Drop every persisted task-result entry for this workspace. The task
        /// boundary keeps filesystem enumeration off the RPC dispatch thread.
        InvalidateCache: unit -> Task<unit>
        /// Request-time count of registered files that currently lack a valid
        /// full-check result (the completeness signal carried in the check
        /// response). Computed from the live PluginHost coverage set against the
        /// pipeline's registered-files denominator — NOT the stale ScanComplete
        /// snapshot.
        GetUncheckedCount: unit -> int
    }

/// Sentinel key under which a wedge report is carried in the status JSON map.
/// Namespaced with the `fshw-` control prefix so it can never collide with a
/// real plugin name. When present, its value is the `WEDGED: ...` message from
/// `OperationWatchdog.wedgeReport`; the CLI surfaces it directly.
[<Literal>]
let WedgeStatusKey = "fshw-wedge"

/// RPC target object exposed to clients via StreamJsonRpc.
///
/// `watchdog` (AUTOMATION-15): each RPC method that does real work brackets itself
/// with `watchdog.Begin`/`End` so the watchdog always knows the single in-flight
/// daemon operation. When one op wedges, a `status` call lands on a free accept task
/// (the IPC server keeps several acceptors running) and reads the watchdog, so
/// `GetStatus`/`ScanStatus` report the wedge + stuck op + recovery instead of the
/// consumer blindly timing out on the socket.
type DaemonRpcTarget(config: DaemonRpcConfig, ?watchdog: OperationWatchdog.Watchdog, ?deadline: TimeSpan) =

    /// The seam deadline. A caller-supplied one is honoured only when it is a
    /// real, finite bound — `Infinite`/zero/negative would reintroduce exactly the
    /// unbounded wait this seam exists to abolish, so they fall back to the
    /// ambient deadline rather than being obeyed. (Tests pass a short finite one.)
    let seamDeadline () =
        match deadline with
        | Some d when d > TimeSpan.Zero && d <> Threading.Timeout.InfiniteTimeSpan -> d
        | _ -> ambientRpcDeadline () + RpcDeadlineGrace

    /// Bracket a unit of RPC work: the watchdog tracks it, AND it is bounded.
    ///
    /// The bound lives HERE — at the seam every real RPC already passes through —
    /// rather than method by method, because a per-method bound leaves gaps that hang
    /// forever with no timeout, no error and no verdict. Bounding the bracket means an
    /// unbounded RPC cannot be written without deliberately stepping outside it.
    ///
    /// A timed-out RPC faults with `TimeoutException`, which StreamJsonRpc carries to
    /// the client as an error. The orphaned work is not force-killed (in-process work
    /// cannot be), but the client is released and the daemon stays answerable.
    ///
    /// `status`/`scanStatus`/`cache-clear` are intentionally NOT bracketed — they must
    /// stay cheap and readable even while another op is wedged, and reading the wedge
    /// report is how a consumer learns about the wedge.
    let trackedTask (name: string) (f: unit -> Task<'a>) : Task<'a> =
        let token = watchdog |> Option.map (fun w -> w.Begin name)

        task {
            try
                let work = f ()
                let d = seamDeadline ()

                use timeoutCts = new CancellationTokenSource()
                let expiry = Task.Delay(d, timeoutCts.Token)
                let! winner = Task.WhenAny(work :> Task, expiry)

                if obj.ReferenceEquals(winner, expiry) then
                    return
                        raise (
                            TimeoutException(
                                $"%s{name} exceeded its %d{int d.TotalSeconds}s deadline — the daemon is wedged on this \
                                  operation. %s{OperationWatchdog.RecoveryAction}"
                            )
                        )
                else
                    // Cancel the timer so its registration doesn't outlive the call.
                    timeoutCts.Cancel()
                    return! work
            finally
                match watchdog, token with
                | Some w, Some t -> w.End t
                | _ -> ()
        }

    /// The wedge entry to splice into a status map, if the daemon is wedged.
    let wedgeEntry () : (string * obj) option =
        watchdog
        |> Option.bind (fun w -> w.WedgeReport())
        |> Option.map (fun msg -> WedgeStatusKey, (box msg))

    /// Returns a JSON string of all plugin statuses. When the daemon's RPC loop
    /// is wedged on a stuck op, a `WedgeStatusKey` entry carrying the
    /// `WEDGED: ...` report (stuck op + inline recovery) is spliced in so the
    /// consumer learns it directly instead of timing out on the socket.
    member _.GetStatus() : string =
        let statuses = config.Host.GetAllStatuses()
        let counts = config.Host.GetDiagnosticCountsByPlugin()

        let entries =
            statuses
            |> Map.map (fun name status -> pluginStatusPayload config.Host counts name status)

        let withWedge =
            match wedgeEntry () with
            | Some(k, v) -> entries |> Map.add k v
            | None -> entries

        JsonSerializer.Serialize(withWedge)

    /// Returns a single plugin's status as a single-entry tagged JSON map,
    /// or an empty map JSON object when the plugin is not registered.
    member _.GetPluginStatus(pluginName: string) : string =
        match config.Host.GetStatus(pluginName) with
        | Some status ->
            let counts = config.Host.GetDiagnosticCountsByPlugin()
            let entry = pluginStatusPayload config.Host counts pluginName status
            let map = Map.ofList [ pluginName, entry ]
            JsonSerializer.Serialize(map)
        | None -> "{}"

    /// Runs a registered command by name and returns the result, or the
    /// `unknownCommandReply` sentinel when the plugin host doesn't recognize it.
    member _.RunCommand(name: string, argsJson: string) : Task<string> =
        trackedTask $"RunCommand:%s{name}" (fun () ->
            task {
                let args =
                    if String.IsNullOrEmpty(argsJson) then
                        [||]
                    else
                        [| argsJson |]

                let! result = config.Host.RunCommand(name, args) |> Async.StartAsTask

                match result with
                | Some r -> return r
                | None -> return unknownCommandReply name
            })

    /// Gracefully shut down the daemon.
    member _.Shutdown() : string =
        config.RequestShutdown()
        "shutting down"

    /// Trigger a full scan. Returns the generation counter to pass to WaitForScan.
    member _.Scan() : string =
        let gen = config.GetScanGeneration()
        config.RequestScan()
        $"scan started:%d{gen}"

    /// Get current scan progress without blocking. When the daemon is wedged on
    /// a stuck op, the scan line is prefixed with the `WEDGED: ...` report so a
    /// plain `fshw scan-status` poll surfaces the wedge inline.
    member _.ScanStatus() : string =
        let scan = config.GetScanStatus()

        match wedgeEntry () with
        | Some(_, v) -> $"%s{unbox<string> v}\n%s{scan}"
        | None -> scan

    /// Query the error ledger. If pluginFilter is empty, return all errors; otherwise filter to that plugin.
    member _.GetDiagnostics(pluginFilter: string) : string =
        let allErrors =
            if System.String.IsNullOrEmpty(pluginFilter) then
                config.Host.GetErrors()
                |> Map.map (fun _file entries ->
                    entries
                    |> List.map (fun (plugin, e) ->
                        {| plugin = plugin
                           message = e.Message
                           severity = severityToString e.Severity
                           line = e.Line
                           column = e.Column
                           detail = e.Detail |}))
            else
                config.Host.GetErrorsByPlugin(pluginFilter)
                |> Map.map (fun _file entries ->
                    entries
                    |> List.map (fun e ->
                        {| plugin = pluginFilter
                           message = e.Message
                           severity = severityToString e.Severity
                           line = e.Line
                           column = e.Column
                           detail = e.Detail |}))

        let count = allErrors |> Map.fold (fun acc _ entries -> acc + entries.Length) 0

        let counts = config.Host.GetDiagnosticCountsByPlugin()

        let statuses =
            config.Host.GetAllStatuses()
            |> Map.map (fun name status -> pluginStatusPayload config.Host counts name status)

        // `unchecked` is the request-time completeness signal: registered files that
        // currently lack a valid full-check result. The CLI parses it into a `Coverage`
        // verdict (0 -> Complete, n>0 -> Incomplete n, absent -> Unknown). A number,
        // not a parsed string, so the verdict cannot be misread.
        let result =
            {| count = count
               files = allErrors
               statuses = statuses
               unchecked = config.GetUncheckedCount() |}

        JsonSerializer.Serialize(result)

    /// Wait for scan generation to advance past afterGeneration, then return the final status.
    /// Negative afterGeneration means "wait for any scan completion" (legacy path).
    ///
    /// The wait itself (`WaitForScanGeneration`) races only daemon shutdown, never a
    /// clock — the scan has no meaningful per-scan budget of its own. Its boundedness
    /// comes from the `trackedTask` seam instead.
    member _.WaitForScan(afterGeneration: int64) : Task<string> =
        trackedTask "WaitForScan" (fun () ->
            task {
                Logging.debug "rpc" $"WaitForScan(%d{afterGeneration}) called"
                do! config.WaitForScanGeneration(afterGeneration)
                return config.GetScanStatus()
            })

    /// Wait for all plugins to reach a terminal state with 1s stability confirmation.
    /// timeoutMs <= 0 means no CLIENT-imposed timeout — the daemon then applies its own
    /// hard bound (`resolveVerdictDeadline`), so the wait is never unbounded: a wedged
    /// plugin surfaces as a TimeoutException naming the still-running plugin.
    member this.WaitForComplete(timeoutMs: int) : Task<string> =
        trackedTask "WaitForComplete" (fun () ->
            task {
                let statuses = config.Host.GetAllStatuses()

                let running =
                    statuses
                    |> Map.toList
                    |> List.choose (fun (name, s) -> if Events.PluginStatus.isTerminal s then None else Some name)

                match running with
                | [] -> Logging.info "rpc" $"WaitForComplete(%d{timeoutMs}ms) called — all plugins already terminal"
                | plugins ->
                    let joined = plugins |> String.concat ", "
                    Logging.info "rpc" $"WaitForComplete(%d{timeoutMs}ms) called — waiting for: %s{joined}"

                let timeout =
                    if timeoutMs <= 0 then
                        Environment.GetEnvironmentVariable "FSHW_VERDICT_DEADLINE_SEC"
                        |> Option.ofObj
                        |> resolveVerdictDeadline
                    else
                        TimeSpan.FromMilliseconds(float timeoutMs)

                do! config.WaitForAllTerminal(timeout)
                config.Host.PruneVanishedErrors(System.IO.File.Exists) |> ignore
                Logging.info "rpc" "WaitForComplete() resolved"
                return this.GetStatus()
            })

    /// Trigger a build by emitting SourceChanged for all registered files, then wait for completion.
    member this.TriggerBuild() : Task<string> =
        trackedTask "TriggerBuild" (fun () ->
            task {
                do! config.TriggerBuild() |> Async.StartAsTask
                let! _ = this.WaitForComplete(0)
                return this.GetStatus()
            })

    /// Force a specific plugin to re-run by clearing its task cache and
    /// emitting a synthetic FileChanged event whose path matches the plugin's
    /// registered pattern. Waits for all plugins to reach terminal state and
    /// returns the status JSON (or an error payload if the plugin has no
    /// registered pattern).
    member this.RerunPlugin(name: string) : Task<string> =
        trackedTask $"RerunPlugin:%s{name}" (fun () ->
            task {
                match! config.RerunPlugin name |> Async.StartAsTask with
                | Result.Ok() ->
                    let! _ = this.WaitForComplete(0)
                    return this.GetStatus()
                | Result.Error msg -> return JsonSerializer.Serialize {| error = msg |}
            })

    /// Invalidate every cached plugin task result without stopping the daemon.
    /// The shared RPC seam supplies the hard deadline and keeps a wedged cache
    /// backend from holding the caller forever; the FCS process stays warm.
    member _.Invalidate() : Task<string> =
        trackedTask "Invalidate" (fun () ->
            task {
                do! config.InvalidateCache()
                return "invalidated"
            })

    /// Clear task cache entries. Optionally filter by plugin and/or file.
    [<JsonRpcMethod("cache-clear")>]
    member _.CacheClear(plugin: string, file: string) : string =
        let pluginOpt = if plugin = null then None else Some plugin
        let fileOpt = if file = null then None else Some file

        match pluginOpt, fileOpt with
        | Some p, Some f -> config.Host.ClearTaskCachePluginFile(p, f)
        | Some p, None -> config.Host.ClearTaskCachePlugin(p)
        | None, Some f -> config.Host.ClearTaskCacheFile(f)
        | None, None -> config.Host.ClearTaskCache()

        "ok"

    /// Run all preprocessors on all registered files and return a summary.
    member _.FormatAll() : Task<string> =
        trackedTask "FormatAll" (fun () ->
            task {
                let! result = config.FormatAll() |> Async.StartAsTask
                return result
            })

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
            | :? OperationCanceledException ->
                // Normal shutdown: the daemon's CancellationToken fired while
                // waiting for a client. Quiet by design.
                pipeServer.Dispose()
            | ex ->
                // A single bad connection (client vanished mid-handshake, broken pipe,
                // RPC wiring failure) must not kill the server, nor vanish silently.
                pipeServer.Dispose()
                Logging.warn "ipc" $"IPC connection handler failed: %s{ex.ToString()}"
        }

    /// Start the IPC server. Keeps multiple accept tasks running concurrently
    /// so clients don't have to wait for the accept loop to cycle.
    ///
    /// Owns the `OperationWatchdog.Watchdog` (see `DaemonRpcTarget`): a background
    /// timer logs the structured "operation exceeded Ns" record plus a periodic
    /// heartbeat. Disposed when the server loop exits (daemon shutdown).
    let start (pipeName: string) (config: DaemonRpcConfig) (cts: CancellationTokenSource) : Async<unit> =
        async {
            use watchdog =
                new OperationWatchdog.Watchdog(
                    OperationWatchdog.DefaultThreshold,
                    heartbeatEvery = TimeSpan.FromSeconds(30.0),
                    now = (fun () -> DateTime.UtcNow),
                    log = Logging.info "watchdog"
                )

            let target = DaemonRpcTarget(config, watchdog)

            // Keep 3 accept tasks running at all times so clients can connect immediately
            let mutable acceptTasks: Task list = []

            let startAccept () =
                Async.StartAsTask(acceptOne pipeName target cts.Token) :> Task

            acceptTasks <- [ startAccept (); startAccept (); startAccept () ]

            while not cts.Token.IsCancellationRequested do
                try
                    let! completed = Task.WhenAny(acceptTasks |> List.toArray) |> Async.AwaitTask

                    // Task.WhenAny hands a faulted acceptor back WITHOUT throwing, so
                    // its exception must be observed here or it vanishes. A fault at
                    // this level means the acceptor died before serving (pipe
                    // creation/bind failed); per-connection failures are handled inside
                    // acceptOne. Back off before respawning, or a persistent bind
                    // failure busy-spins the loop with instantly-faulting acceptors.
                    if completed.IsFaulted then
                        Logging.error "ipc" $"IPC accept task faulted: %s{completed.Exception.ToString()}"
                        do! Task.Delay(1000, cts.Token) |> Async.AwaitTask

                    acceptTasks <-
                        acceptTasks
                        |> List.map (fun t ->
                            if Object.ReferenceEquals(t, completed) then
                                startAccept ()
                            else
                                t)
                with
                | :? OperationCanceledException ->
                    // Normal shutdown signal — the while condition exits the loop.
                    ()
                | ex ->
                    // A real server-loop fault. Log and keep serving: killing the
                    // daemon's IPC over one bad cycle is worse than a logged retry.
                    Logging.error "ipc" $"IPC accept loop error: %s{ex.ToString()}"
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

    /// Trigger a full scan of all registered files.
    let scan (pipeName: string) : Async<string> = invoke pipeName "Scan" [||]

    /// Get current scan progress.
    let scanStatus (pipeName: string) : Async<string> = invoke pipeName "ScanStatus" [||]

    /// Get diagnostics, optionally filtered by plugin name.
    let getDiagnostics (pipeName: string) (pluginFilter: string) : Async<string> =
        invoke pipeName "GetDiagnostics" [| pluginFilter |]

    /// Wait for scan to complete, then return final status.
    let waitForScan (pipeName: string) (afterGeneration: int64) : Async<string> =
        invoke pipeName "WaitForScan" [| afterGeneration |]

    /// Wait for all plugins to reach a terminal state, then return full status.
    /// timeoutMs <= 0 means no client-imposed timeout.
    let waitForComplete (pipeName: string) (timeoutMs: int) : Async<string> =
        invoke pipeName "WaitForComplete" [| timeoutMs |]

    /// Trigger a build and wait for it to complete.
    let triggerBuild (pipeName: string) : Async<string> = invoke pipeName "TriggerBuild" [||]

    /// Run all preprocessors on all registered files.
    let formatAll (pipeName: string) : Async<string> = invoke pipeName "FormatAll" [||]

    /// Clear task cache entries with a typed filter.
    let cacheClear (pipeName: string) (filter: CacheClearFilter) : Async<string> =
        let (plugin, file) =
            match filter with
            | ClearAll -> (null, null)
            | ClearPlugin p -> (p, null)
            | ClearFile f -> (null, f)
            | ClearPluginFile(p, f) -> (p, f)

        invoke pipeName "cache-clear" [| plugin; file |]

    /// Force a named plugin to re-run, then return the full status.
    let rerunPlugin (pipeName: string) (name: string) : Async<string> =
        invoke pipeName "RerunPlugin" [| name |]

    /// Invalidate all task-result cache entries while preserving the daemon.
    let invalidate (pipeName: string) : Async<string> = invoke pipeName "Invalidate" [||]

    /// Quick probe to check if a daemon is listening on the named pipe.
    let isRunning (pipeName: string) : bool =
        try
            use pipe =
                new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            pipe.ConnectAsync(500).Wait()
            true
        with _ex ->
            false
