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

/// The reply to a command-shaped RPC (`RunCommand`, `RunCommandFor`, `FormatAll`,
/// `RerunPlugin`), tagged on the wire so a client parses it ONCE, at the boundary, and
/// matches on what it is instead of probing a payload for the fields it happens to have.
[<RequireQualifiedAccess>]
type CommandReply =
    /// A plugin command's own output, and the schema the plugin declares it has.
    | Output of command: string * schema: Plugin.ReplySchema * output: string
    /// No registered plugin serves the command, so nothing ran.
    | UnknownCommand of command: string
    /// The plugin statuses after a re-run (`GetStatus`'s JSON).
    | Statuses of json: string
    /// The daemon refused the request, and why.
    | Refused of reason: string
    /// What a format pass did.
    | Formatted of summary: string

/// What a client made of a command reply.
[<RequireQualifiedAccess>]
type ReplyReading =
    | Tagged of CommandReply
    /// Not a reply in this format: a daemon from another fshw build, which answers untagged,
    /// or a malformed reply. Never read as output, a pass or a refusal.
    | Unrecognised of reply: string

module CommandReply =
    /// Names the wire format. A reply that does not name it is not one.
    [<Literal>]
    let Schema = "fshw-reply-v1"

    /// What a client says of a reply it cannot parse: the daemon is from another fshw build.
    let untaggedMessage =
        "the daemon's reply carries no fshw reply tag: it comes from another fshw build, so it is not read as output, a pass or a refusal; `fshw stop` restarts the daemon from this build — no verdict"

    /// `reply` on the wire.
    let toJson (reply: CommandReply) : string =
        let o = Nodes.JsonObject()
        o["schema"] <- Schema

        match reply with
        | CommandReply.Output(command, schema, output) ->
            o["kind"] <- "output"
            o["command"] <- command
            o["output"] <- output

            match schema with
            | Plugin.ReplySchema.Typed id -> o["replySchema"] <- id
            | Plugin.ReplySchema.Raw reason -> o["rawReason"] <- reason
        | CommandReply.UnknownCommand command ->
            o["kind"] <- "unknown-command"
            o["command"] <- command
        | CommandReply.Statuses json ->
            o["kind"] <- "statuses"
            o["statuses"] <- json
        | CommandReply.Refused reason ->
            o["kind"] <- "refused"
            o["reason"] <- reason
        | CommandReply.Formatted summary ->
            o["kind"] <- "formatted"
            o["summary"] <- summary

        o.ToJsonString()

    /// The reply a tagged object states; `None` for one that states none.
    let private replyOf (o: JsonElement) : CommandReply option =
        let field = Json.tryString o

        match field "kind" with
        | Some "output" ->
            let schema =
                match field "replySchema", field "rawReason" with
                | Some id, None -> Some(Plugin.ReplySchema.Typed id)
                | None, Some reason -> Some(Plugin.ReplySchema.Raw reason)
                | _ -> None

            match field "command", schema, field "output" with
            | Some command, Some schema, Some output -> Some(CommandReply.Output(command, schema, output))
            | _ -> None
        | Some "unknown-command" -> field "command" |> Option.map CommandReply.UnknownCommand
        | Some "statuses" -> field "statuses" |> Option.map CommandReply.Statuses
        | Some "refused" -> field "reason" |> Option.map CommandReply.Refused
        | Some "formatted" -> field "summary" |> Option.map CommandReply.Formatted
        | _ -> None

    /// The reply `text` is; anything that is not exactly this format is `Unrecognised`.
    let parse (text: string) : ReplyReading =
        let reply =
            if isNull text then
                None
            else
                try
                    use doc = JsonDocument.Parse text
                    let o = doc.RootElement

                    if o.ValueKind = JsonValueKind.Object && Json.tryString o "schema" = Some Schema then
                        replyOf o
                    else
                        None
                with :? JsonException ->
                    None

        match reply with
        | Some reply -> ReplyReading.Tagged reply
        | None -> ReplyReading.Unrecognised text

/// The reply `RunCommand` sends when the daemon's plugin host does not recognise the
/// command, as it goes on the wire.
let unknownCommandReply (name: string) : string =
    CommandReply.toJson (CommandReply.UnknownCommand name)

/// The reply `RunCommand` sends for a plugin command's `output` whose reply has `schema`, as
/// it goes on the wire.
let declaredOutputReply (name: string) (schema: Plugin.ReplySchema) (output: string) : string =
    CommandReply.toJson (CommandReply.Output(name, schema, output))

/// `declaredOutputReply` for a command that declares no reply schema.
let commandOutputReply (name: string) (output: string) : string =
    declaredOutputReply name Plugin.ReplySchema.undeclared output

/// Default hard bound on a `WaitForComplete` verdict wait when the client
/// imposes no timeout of its own. Deliberately generous — a legitimate cold
/// full-suite check (build + every test project) finishes well inside an hour,
/// so this only ever trips on a genuinely-wedged plugin.
let DefaultVerdictDeadline = TimeSpan.FromMinutes 60.0

/// Resolve the verdict-wait deadline from an optional override string (the
/// `FSHW_VERDICT_DEADLINE_SEC` env value). A positive integer count of seconds
/// wins; anything else (absent, unparseable, non-positive) falls back to
/// `DefaultVerdictDeadline` — there is intentionally NO "infinite" setting.
/// Pure so the precedence is unit-testable without touching process env; the override
/// is read by `ProcessHelper.tryPositiveSeconds`.
let resolveVerdictDeadline (overrideSec: string option) : TimeSpan =
    ProcessHelper.tryPositiveSeconds overrideSec
    |> Option.defaultValue DefaultVerdictDeadline

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
///
/// A `running` status also carries `awakeElapsedMs`: how long the run has gone on in the
/// daemon's awake time (`PluginHost.AwakeAgeOf`), from which the CLI judges a wedge
/// (AUTOMATION-1489). `since` is a wall-clock stamp, and a CLI that subtracts it from its
/// own wall clock counts a sleep of the box as running time.
let private statusPayload (awakeAgeOf: DateTime -> TimeSpan option) (status: PluginStatus) : obj =
    match status with
    | Idle -> {| tag = "idle" |} :> obj
    | Running since ->
        // `null` when the host holds no stamp for this `since`: the CLI then reads the reply
        // as an older daemon's.
        {| tag = "running"
           since = since.ToString("O")
           awakeElapsedMs =
            awakeAgeOf since
            |> Option.map (fun age -> int64 age.TotalMilliseconds)
            |> Option.toNullable |}
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
    | VerifiedNothing d ->
        {| tag = "verifiedNothing"
           detail = d |}
        :> obj

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

            // Present only when lines were dropped; a reader takes its absence as none.
            let activityDropped =
                match r.ActivityTrim with
                | Trimmed n -> box n
                | Untrimmed -> null

            {| startedAt = r.StartedAt.ToString("O")
               elapsedMs = int64 r.Elapsed.TotalMilliseconds
               outcome = outcomePayload r.Outcome
               summary = summary
               activityTail = r.ActivityTail
               activityDropped = activityDropped |}
            :> obj

    let d = Map.tryFind name counts |> Option.defaultValue DiagnosticCounts.empty

    {| status = statusPayload (fun since -> host.AwakeAgeOf(name, since)) status
       subtasks = subtasks
       activityTail = snap.ActivityTail
       lastRun = lastRun
       diagnostics =
        {| errors = d.Errors
           warnings = d.Warnings |} |}
    :> obj

/// Configuration record for DaemonRpcTarget.
/// The diagnostics reply's two reads, statuses FIRST. A plugin reports its findings and
/// then goes terminal, so a status read before the ledger can only be older than the
/// ledger it is paired with: a Completed status is never read without the findings
/// reported ahead of it. Ledger first, a plugin that finished between the two reads
/// would be returned as "Completed, 0 errors" — a false green.
let internal readStatusesThenLedger (readStatuses: unit -> 'S) (readLedger: unit -> 'L) : 'S * 'L =
    let statuses = readStatuses ()
    let ledger = readLedger ()
    statuses, ledger

/// What `ReleaseIfSole` answers, shared by the daemon and its clients.
module ReleaseReply =
    /// Nobody else was using the daemon: it is stopping.
    [<Literal>]
    let Released = "released"

    let private keptPrefix = "kept: "

    /// Someone else is using the daemon; `who` names them.
    let kept (who: string) : string = keptPrefix + who

    /// Whom a reply says the daemon was kept for; `None` when it is stopping.
    let keptFor (reply: string) : string option =
        if reply.StartsWith(keptPrefix, System.StringComparison.Ordinal) then
            Some(reply.Substring keptPrefix.Length)
        else
            None

/// How long `ReleaseIfSole` lets a reading of "others" settle before believing it. The
/// releasing client's last connection can still be closing when it asks.
let private releaseSettle = System.TimeSpan.FromSeconds 1.0

[<NoComparison; NoEquality>]
type DaemonRpcConfig =
    {
        Host: PluginHost
        /// Stop the daemon for an in-process caller holding this configuration, which names
        /// no client: logged as `DaemonStop.InProcessCaller`. The stop RPCs use `RequestStop`.
        RequestShutdown: unit -> unit
        /// Stop the daemon for a client's stop RPC; the request names which RPC and who sent
        /// it, and is what the daemon logs as its stop cause (`DaemonStop`).
        RequestStop: DaemonStop.RpcStopRequest -> unit
        /// Who else holds this repository's run claims, described for a person. Read inside
        /// `ReleaseIfSole`, so a claim taken between a client asking and the daemon answering
        /// is still seen.
        LiveRunClaims: unit -> string list
        /// Admit a scan for the invocation that asked for it, if it named one: the scan's
        /// work is held through that invocation's run claim (`RunClaim.leaseFor`).
        RequestScan: string option -> unit
        /// The token that fires once the CLI invocation named has gone: when its run
        /// claim is released (`RunClaim.releasedFor`). One that never fires when there is
        /// no claim to watch, so the daemon holds what that invocation asks for.
        InvocationReleased: string -> CancellationToken
        GetScanStatus: unit -> ScanProgress.Progress
        GetScanGeneration: unit -> int64
        TriggerBuild: unit -> Async<unit>
        FormatAll: unit -> Async<string>
        WaitForScanGeneration: int64 -> Task<unit>
        WaitForAllTerminal: TimeSpan -> Task<unit>
        /// Settle: return once the host owns no work, faulting with `TimeoutException`
        /// at the bound. Asks nothing about evidence and starts nothing — the wait a
        /// reader such as `fshw findings` needs, where `WaitForAllTerminal` is a check's.
        WaitForRest: TimeSpan -> Task<unit>
        /// How long the daemon's work has shown no movement: a checked file, a dispatch, a
        /// plugin status change. Zero while a plugin is busy, since a plugin's own run has its
        /// own wedge detector. What a client wait is judged by (`OperationWatchdog.WaitStallBound`).
        WorkQuietFor: unit -> TimeSpan
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
        /// What the daemon believes about its project model at request
        /// time — `Rediscovering` while a discovery attempt is in flight, `Available`
        /// only once every discovery stage produced evidence. Served in the SAME reply
        /// as `unchecked` and the statuses, so a verdict is graded against the model
        /// that was current when its other inputs were read, never a later one.
        GetProjectModel: unit -> ProjectModel.Observation
        /// How the daemon's model-guarded publications ended so far (ADR-044), for `status`.
        GetPublications: unit -> ProjectModel.Publications
        /// This session's snapshot epoch, manifest id and stale refusals (ADR-046), for `status`.
        GetSnapshot: unit -> SnapshotEpoch.SnapshotStatus
        /// The context the daemon's RPCs run in: its process scope, and in a repository
        /// host its session's log sink and client environment. `None`: the context of
        /// whatever serves them.
        Context: ExecutionContext option
    }

/// Sentinel key under which a wedge report is carried in the status JSON map.
/// Namespaced with the `fshw-` control prefix so it can never collide with a
/// real plugin name. When present, its value is the `WEDGED: ...` message from
/// `OperationWatchdog.wedgeReport`; the CLI surfaces it directly.
[<Literal>]
let WedgeStatusKey = "fshw-wedge"

/// The status-map key under which the heap valve's report travels once it has fired
/// (`OperationWatchdog.Watchdog.HeapValveReport`).
let HeapValveStatusKey = "fshw-heap-valve"

/// RPC target object exposed to clients via StreamJsonRpc.
///
/// `watchdog`: each RPC method that does real work brackets itself
/// with `watchdog.Begin`/`End` so the watchdog always knows the single in-flight
/// daemon operation. When one op wedges, a `status` call lands on a free accept task
/// (the IPC server keeps several acceptors running) and reads the watchdog, so
/// `GetStatus`/`ScanStatus` report the wedge + stuck op + recovery instead of the
/// consumer blindly timing out on the socket.
///
/// `disconnected`: fires when the client this target serves has gone away. The IPC
/// server builds one target per connection and cancels it when that connection drops,
/// so a killed client does not leave behind work that existed only for it (see
/// `trackedTask`). Absent, nothing is ever cancelled.
type DaemonRpcTarget
    (
        config: DaemonRpcConfig,
        ?watchdog: OperationWatchdog.Watchdog,
        ?deadline: TimeSpan,
        ?disconnected: CancellationToken,
        ?connectedClients: unit -> int
    ) =

    let disconnected = defaultArg disconnected CancellationToken.None

    let connectedClients = defaultArg connectedClients (fun () -> 0)

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
    ///
    /// The clock starts BEFORE the callback runs, and the callback runs on the pool:
    /// a callback can block before it ever returns its Task (a `task { }` body runs
    /// inline up to its first real await), and calling it inline would leave that
    /// synchronous prefix outside the deadline.
    ///
    /// A client that disconnects ends the RPC the same way: the call is released and
    /// retired from the watchdog at once, because nobody is left to read its reply. The
    /// callback receives the disconnect token so it can cancel what it started SOLELY
    /// for this client; anything it merely waits on that others share (a plugin run,
    /// the daemon-wide terminal wait) must be left running — see each method.
    let trackedWith
        (start: OperationWatchdog.Watchdog -> OperationWatchdog.OpToken)
        (deadlineMessage: TimeSpan -> string)
        (name: string)
        (f: CancellationToken -> Task<'a>)
        : Task<'a> =
        let token = watchdog |> Option.map start

        task {
            try
                let d = seamDeadline ()

                use timeoutCts = new CancellationTokenSource()
                let expiry = Task.Delay(d, timeoutCts.Token)
                let dropped = Task.Delay(Timeout.Infinite, disconnected)
                let work = Task.Run<'a>(Func<Task<'a>>(fun () -> f disconnected))
                let! winner = Task.WhenAny(work :> Task, expiry, dropped)

                if obj.ReferenceEquals(winner, dropped) then
                    timeoutCts.Cancel()
                    // Observe the abandoned work's eventual fault: nobody awaits it now.
                    work.ContinueWith(fun (t: Task<'a>) -> t.Exception |> ignore) |> ignore

                    return
                        raise (OperationCanceledException($"%s{name} abandoned: its client disconnected", disconnected))
                elif obj.ReferenceEquals(winner, expiry) then
                    return raise (TimeoutException(deadlineMessage d))
                else
                    // Cancel the timer so its registration doesn't outlive the call.
                    timeoutCts.Cancel()
                    return! work
            finally
                match watchdog, token with
                | Some w, Some t -> w.End t
                | _ -> ()
        }

    let wedgedAtDeadline (name: string) (d: TimeSpan) =
        $"%s{name} exceeded its %d{int d.TotalSeconds}s deadline — the daemon is wedged on this \
          operation. %s{OperationWatchdog.recoveryAction ()}"

    /// `trackedWith` for an RPC that does its own work: stuck past the threshold is stuck.
    let trackedTask (name: string) (f: CancellationToken -> Task<'a>) : Task<'a> =
        trackedWith (fun w -> w.Begin name) (wedgedAtDeadline name) name f

    /// `trackedWith` for a client's wait on daemon work. It lasts as long as that work, so it
    /// is judged by whether the work is moving: a cold scan holds one for minutes, and
    /// telling that reader to `fshw stop` discards the scan (AUTOMATION-1520).
    let trackedWait (name: string) (f: CancellationToken -> Task<'a>) : Task<'a> =
        trackedWith
            (fun w -> w.BeginWait(name, config.WorkQuietFor))
            (fun d ->
                if OperationWatchdog.isAdvancing (config.WorkQuietFor()) then
                    $"%s{name} gave up after %d{int d.TotalSeconds}s, but the work it waits on is still moving: \
                      the daemon is not wedged. Re-run to wait again; raise FSHW_VERDICT_DEADLINE_SEC to wait longer."
                else
                    wedgedAtDeadline name d)
            name
            f

    /// Run a registered command under `requester`, the token its requests hold their
    /// intents for, and reply with its result or the unknown-command sentinel.
    let runCommandUnder (requester: CancellationToken) (name: string) (argsJson: string) =
        task {
            let args =
                if String.IsNullOrEmpty(argsJson) then
                    [||]
                else
                    [| argsJson |]

            let! result = Async.StartAsTask(config.Host.RunCommandReply(name, args), cancellationToken = requester)

            match result with
            | Some(schema, r) -> return declaredOutputReply name schema r
            | None -> return unknownCommandReply name
        }

    /// The wedge entry to splice into a status map, if the daemon is wedged.
    let wedgeEntry () : (string * obj) option =
        watchdog
        |> Option.bind (fun w -> w.WedgeReport())
        |> Option.map (fun msg -> WedgeStatusKey, (box msg))

    /// The heap valve's report, once it has fired.
    let heapValveReport () : string option =
        watchdog |> Option.bind (fun w -> w.HeapValveReport())

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

        let withValve =
            match heapValveReport () with
            | Some report -> withWedge |> Map.add HeapValveStatusKey (box report)
            | None -> withWedge

        JsonSerializer.Serialize(withValve)

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
    /// `unknownCommandReply` when the plugin host doesn't recognize it, tagged (`CommandReply`).
    member _.RunCommand(name: string, argsJson: string) : Task<string> =
        // The command's own async runs under the disconnect token, so whatever it does
        // inline stops at its next cancellation point once its client is gone. Work it
        // hands to a plugin (an owned run, a queued intent) belongs to the plugin, is
        // shared with the watcher and other clients, and is not cancelled here. A request
        // holds the intents it enqueues for that token: for this connection alone.
        trackedTask $"RunCommand:%s{name}" (fun disconnected -> runCommandUnder disconnected name argsJson)

    /// `RunCommand` on behalf of the CLI invocation `invocationId`. The intents the
    /// command enqueues are held by this connection AND by that invocation's run claim,
    /// so they outlive the reply: a CLI closes the connection the moment the reply
    /// arrives, then waits on other connections for the work it asked for. They are
    /// withdrawn, or their run cancelled, once both have gone (`InvocationReleased`).
    member _.RunCommandFor(name: string, argsJson: string, invocationId: string) : Task<string> =
        trackedTask $"RunCommand:%s{name}" (fun disconnected ->
            let requester =
                PluginWorkOwner.ConsumerLeases.merge
                    (PluginWorkOwner.ConsumerLeases.ofClient disconnected)
                    (PluginWorkOwner.ConsumerLeases.ofClient (config.InvocationReleased invocationId))
                |> PluginWorkOwner.ConsumerLeases.releasedToken

            runCommandUnder requester name argsJson)

    /// The caller a stop RPC carried: `Described` when the CLI sent its command line, pid
    /// and cwd, the overloads below with none of them from an older CLI.
    static member private Described(command: string, pid: int, cwd: string) : DaemonStop.StopCaller =
        DaemonStop.Described
            { Command = command
              Pid = pid
              Cwd = cwd }

    /// Gracefully shut down the daemon for `caller`.
    member private _.ShutdownFor(caller: DaemonStop.StopCaller) : string =
        config.RequestStop
            { Rpc = DaemonStop.Shutdown
              Caller = caller }

        "shutting down"

    /// Gracefully shut down the daemon. A CLI from before AUTOMATION-1675 calls this
    /// overload, with no description of itself.
    member this.Shutdown() : string =
        this.ShutdownFor DaemonStop.UnknownClient

    /// Gracefully shut down the daemon, for the CLI process described.
    member this.Shutdown(command: string, pid: int, cwd: string) : string =
        this.ShutdownFor(DaemonRpcTarget.Described(command, pid, cwd))

    /// Stop the daemon, but only if nobody else is using it: no other open connection and
    /// no live run claim. This is decided here, not by the client, because only the daemon can
    /// see a `test-rerun` holding a call and no claim. Asked for by a one-shot verb that
    /// started this daemon. The caller's own last connection may still be closing, so the
    /// reading settles for up to `releaseSettle` before it counts as another client.
    member private _.ReleaseIfSoleFor(caller: DaemonStop.StopCaller) : Task<string> =
        task {
            let reading () =
                max 0 (connectedClients () - 1), config.LiveRunClaims()

            let settling = Diagnostics.Stopwatch.StartNew()
            let mutable current = reading ()

            while current <> (0, []) && settling.Elapsed < releaseSettle do
                do! Task.Delay 25
                current <- reading ()

            match current with
            | 0, [] ->
                config.RequestStop
                    { Rpc = DaemonStop.ReleaseIfSole
                      Caller = caller }

                return ReleaseReply.Released
            | clients, claims ->
                let others =
                    [ if clients > 0 then
                          yield $"%d{clients} other connected client(s)"
                      yield! claims ]
                    |> String.concat "; "

                return ReleaseReply.kept others
        }

    /// `ReleaseIfSole` from a CLI from before AUTOMATION-1675, with no description of itself.
    member this.ReleaseIfSole() : Task<string> =
        this.ReleaseIfSoleFor DaemonStop.UnknownClient

    /// `ReleaseIfSole` for the CLI process described.
    member this.ReleaseIfSole(command: string, pid: int, cwd: string) : Task<string> =
        this.ReleaseIfSoleFor(DaemonRpcTarget.Described(command, pid, cwd))

    /// Trigger a full scan. Returns the generation counter to pass to WaitForScan. The
    /// scan is the daemon's: it runs to completion whoever is left to read it.
    member _.Scan() : string =
        let gen = config.GetScanGeneration()
        config.RequestScan None
        $"scan started:%d{gen}"

    /// `Scan` on behalf of the CLI invocation `invocationId`. Its test runs are cancelled,
    /// and their hosts reaped, once that invocation's run claim is released, unless
    /// another client or the daemon itself still wants them.
    member _.ScanFor(invocationId: string) : string =
        let gen = config.GetScanGeneration()
        config.RequestScan(Some invocationId)
        $"scan started:%d{gen}"

    /// Get current scan progress without blocking, as `ScanProgress.toJson`: the progress,
    /// and the `WEDGED: ...` report when the daemon is wedged on a stuck op, so a client
    /// polling only this RPC sees the wedge. `fshw status` prints the scanning line from it.
    ///
    /// An answer that reports a scan in progress is logged at Info, so a cold scan's
    /// progress is in the daemon log and not only in the polling client's terminal. An
    /// idle or finished scan is not: a waiting client polls this repeatedly.
    member _.ScanStatus() : string =
        let clock = Diagnostics.Stopwatch.StartNew()
        let progress = config.GetScanStatus()
        let wedge = wedgeEntry () |> Option.map (fun (_, v) -> unbox<string> v)

        match ScanProgress.progressLine progress with
        | Some line ->
            let shown =
                match wedge with
                | Some report -> $"%s{report}\n%s{line}"
                | None -> line

            Logging.info "rpc" $"scan status answered in %d{clock.ElapsedMilliseconds}ms: %s{shown}"
        | None -> ()

        ScanProgress.toJson wedge progress

    /// Query the error ledger. If pluginFilter is empty, return all errors; otherwise filter to that plugin.
    ///
    /// The reply is SIZE-BOUNDED (`ErrorLedger.Transport`). Every entry
    /// still travels — the count and the severities are what the CLI's exit code and
    /// the verdict's `reddenedBy` are computed from — but each entry's text is capped
    /// and the response as a whole has a `detail` budget. Without it this reply is
    /// `failing tests × captured project output` characters, which on a broadly red
    /// full suite is tens of gigabytes: the serializer cannot build a string that long,
    /// so an admitted run that the daemon had already FINISHED died here, at the very
    /// last call, and reported as an out-of-memory fault that named the wrong process.
    member _.GetDiagnostics(pluginFilter: string) : string =
        let readLedger () : (string * (string * ErrorLedger.ErrorEntry) list) list =
            if System.String.IsNullOrEmpty(pluginFilter) then
                config.Host.GetErrors() |> Map.toList
            else
                config.Host.GetErrorsByPlugin(pluginFilter)
                |> Map.toList
                |> List.map (fun (file, entries) -> file, entries |> List.map (fun e -> pluginFilter, e))

        let rawStatuses, ledger =
            readStatusesThenLedger config.Host.GetAllStatuses readLedger

        // `Map.toList` order, then one fold across the whole response: the budget is
        // spent in a deterministic order, so the same ledger always produces the same
        // reply. A per-file budget would not bound the response at all — it is the
        // number of FILES that grows on a broadly red run.
        let files, _spent =
            ledger
            |> List.mapFold
                (fun spent (file, entries) ->
                    let projected, spent' =
                        entries
                        |> List.mapFold
                            (fun spent (plugin, e: ErrorLedger.ErrorEntry) ->
                                let detail, spent' = ErrorLedger.Transport.takeDetail spent e.Detail

                                {| plugin = plugin
                                   message = ErrorLedger.Transport.truncateField e.Message
                                   severity = severityToString e.Severity
                                   line = e.Line
                                   column = e.Column
                                   detail = detail |},
                                spent')
                            spent

                    (file, projected), spent')
                0

        let allErrors = Map.ofList files

        let count = allErrors |> Map.fold (fun acc _ entries -> acc + List.length entries) 0

        let counts = config.Host.GetDiagnosticCountsByPlugin()

        let statuses =
            rawStatuses
            |> Map.map (fun name status -> pluginStatusPayload config.Host counts name status)

        // Wall-time attribution rework. Every phase the daemon spent wall time in — its own
        // and every plugin's `Running` interval, superseded runs included — so the
        // verdict can cover the time the check waited on, by name. Wall-clock stamped:
        // the CLI places each one against its own invocation origin.
        let daemonPhases =
            config.Host.Phases.Snapshot(DateTime.UtcNow)
            |> List.map (fun phase ->
                {| scope = phase.Scope
                   startedAt = phase.StartedAt.ToString("O")
                   elapsedMs = int64 phase.Elapsed.TotalMilliseconds
                   detail =
                    (match phase.Detail with
                     | Some d -> box d
                     | None -> null) |})

        // The receipts this daemon holds for the current project model, read from ONE
        // publication so a receipt cannot belong to a different snapshot than the work it
        // retired. `runId` is null for the analysis-only receipt: no run earned it.
        // `refusals` are the reasons it denies a green, carried rather than dropped.
        let snapshot = config.Host.WorkSnapshot

        let modelReceipts =
            [ for evidence in snapshot.Evidence do
                  yield
                      {| runId = box (evidence.RunId.ToString("N"))
                         modelGeneration = evidence.Generation
                         refusals = evidence.FailureReasons |}
              for analysis in snapshot.AnalysisEvidence do
                  yield
                      {| runId = null
                         modelGeneration = analysis.Generation
                         refusals = analysis.FailureReasons |} ]

        // `unchecked` is the request-time completeness signal: registered files that
        // currently lack a valid full-check result. The CLI parses it into a `Coverage`
        // verdict (0 -> Complete, n>0 -> Incomplete n, absent -> Unknown). A number,
        // not a parsed string, so the verdict cannot be misread.
        let result =
            {| count = count
               files = allErrors
               statuses = statuses
               daemonPhases = daemonPhases
               // ABSENT, not empty, when no registered plugin mints evidence: an empty
               // array says "a plugin owed a receipt and has none", which is a refusal.
               modelReceipts = (if snapshot.OffersEvidence then box modelReceipts else null)
               unchecked = config.GetUncheckedCount()
               // The versioned `fshw-project-model-v1` payload. Without
               // it an empty model mid-rediscovery and a healthy model that selected
               // nothing reach the CLI as the same reply, and the second is a green.
               projectModel = ProjectModelWire.payload (config.GetProjectModel())
               // ADR-044: how this daemon's model-guarded publications ended, for `status`.
               publications = ProjectModelWire.publicationsPayload (config.GetPublications())
               // ADR-046: this session's snapshot epoch, manifest id and stale refusals.
               snapshot = SnapshotEpoch.SnapshotStatus.payload (config.GetSnapshot())
               // null until the heap valve fires; then its report, for `status`.
               heapValve =
                (match heapValveReport () with
                 | Some report -> report
                 | None -> null) |}

        JsonSerializer.Serialize(result)

    /// Wait for scan generation to advance past afterGeneration, then return the final status.
    /// Negative afterGeneration means "wait for any scan completion" (legacy path).
    ///
    /// The wait itself (`WaitForScanGeneration`) races only daemon shutdown, never a
    /// clock — the scan has no meaningful per-scan budget of its own. Its boundedness
    /// comes from the `trackedTask` seam instead.
    member _.WaitForScan(afterGeneration: int64) : Task<string> =
        // The scan is the daemon's, shared by every waiter: a dropped client only stops
        // waiting on it.
        trackedWait "WaitForScan" (fun _ ->
            task {
                Logging.debug "rpc" $"WaitForScan(%d{afterGeneration}) called"
                do! config.WaitForScanGeneration(afterGeneration)
                return ScanProgress.render (config.GetScanStatus())
            })

    /// Wait for all plugins to reach a terminal state with 1s stability confirmation.
    /// timeoutMs <= 0 means no CLIENT-imposed timeout — the daemon then applies its own
    /// hard bound (`resolveVerdictDeadline`), so the wait is never unbounded: a wedged
    /// plugin surfaces as a TimeoutException naming the still-running plugin.
    member this.WaitForComplete(timeoutMs: int) : Task<string> =
        // Plugin runs are the daemon's, shared by every waiter and the watcher: a dropped
        // client only stops waiting on them.
        trackedWait "WaitForComplete" (fun _ ->
            task {
                let statuses = config.Host.GetAllStatuses()

                let running =
                    statuses
                    |> Map.toList
                    |> List.choose (fun (name, s) -> if Events.PluginStatus.isTerminal s then None else Some name)

                let timeout =
                    if timeoutMs <= 0 then
                        ambientRpcDeadline ()
                    else
                        TimeSpan.FromMilliseconds(float timeoutMs)

                // Logged as the bound actually applied: a client that imposes none still
                // waits under the daemon's deadline, never without one.
                match running with
                | [] when statuses.IsEmpty ->
                    Logging.info "rpc" $"WaitForComplete(bound %O{timeout}) called — no plugins registered"
                | [] -> Logging.info "rpc" $"WaitForComplete(bound %O{timeout}) called — all plugins already terminal"
                | plugins ->
                    let joined = plugins |> String.concat ", "
                    Logging.info "rpc" $"WaitForComplete(bound %O{timeout}) called — waiting for: %s{joined}"

                do! config.WaitForAllTerminal(timeout)
                config.Host.PruneVanishedErrors(System.IO.File.Exists) |> ignore
                Logging.info "rpc" "WaitForComplete() resolved"
                return this.GetStatus()
            })

    /// Whether the host owns no work, waiting up to `timeoutMs` for it to come to rest:
    /// `"rest"`, or `"busy: <why>"` when it still owned work at the bound. `timeoutMs <= 0`
    /// answers at once without waiting.
    ///
    /// The READER's wait. `WaitForComplete` is a check's: it also waits for test evidence
    /// about the current model, re-checks suspect files once per tree and prunes vanished
    /// paths from the ledger. A reader must neither start work nor wait on a run nobody
    /// asked for, so this asks the one question both share — is anything still owned? —
    /// and nothing else. A wedged or dead plugin answers `busy` with the detector's words.
    member _.WaitForRest(timeoutMs: int) : Task<string> =
        trackedWait "WaitForRest" (fun _ ->
            task {
                if timeoutMs <= 0 then
                    return
                        if config.Host.WorkSnapshot.IsBusy then
                            "busy: the daemon owns work"
                        else
                            "rest"
                else
                    // Bounded below the RPC seam's own deadline, so the answer is this
                    // method's rather than the seam's timeout fault.
                    let timeout =
                        min (TimeSpan.FromMilliseconds(float timeoutMs)) (ambientRpcDeadline ())

                    try
                        do! config.WaitForRest timeout
                        return "rest"
                    with :? TimeoutException as ex ->
                        return $"busy: %s{ex.Message}"
            })

    /// Trigger a build by emitting SourceChanged for all registered files, then wait for completion.
    member this.TriggerBuild() : Task<string> =
        // The build it triggers is a plugin run, shared like any other.
        trackedTask "TriggerBuild" (fun _ ->
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
        // The re-run is a plugin run, shared like any other.
        trackedTask $"RerunPlugin:%s{name}" (fun _ ->
            task {
                match! config.RerunPlugin name |> Async.StartAsTask with
                | Result.Ok() ->
                    let! _ = this.WaitForComplete(0)
                    return CommandReply.toJson (CommandReply.Statuses(this.GetStatus()))
                | Result.Error msg -> return CommandReply.toJson (CommandReply.Refused msg)
            })

    /// Invalidate every cached plugin task result without stopping the daemon.
    /// The shared RPC seam supplies the hard deadline and keeps a wedged cache
    /// backend from holding the caller forever; the FCS process stays warm.
    member _.Invalidate() : Task<string> =
        trackedTask "Invalidate" (fun _ ->
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
        // Formatting runs on the daemon's change agent, in order with watcher input.
        trackedTask "FormatAll" (fun _ ->
            task {
                let! result = config.FormatAll() |> Async.StartAsTask
                return CommandReply.toJson (CommandReply.Formatted result)
            })

/// IPC server that listens on a named pipe and exposes plugin host methods via StreamJsonRpc.
module IpcServer =

    /// How long a stopping server lets its open connections finish before closing them.
    /// A connection normally ends the moment its client has read the reply — including
    /// the reply to `Shutdown` itself — but under load that takes far longer than it
    /// looks: half a second lost that reply in a parallel test run. Only a client that
    /// holds a connection open without a call in flight ever reaches this bound.
    let ConnectionDrainBound = TimeSpan.FromSeconds 5.0

    /// How long a stopped server waits for its name to stop accepting connections.
    let ReleaseBound = TimeSpan.FromSeconds 1.0

    /// Whether anything accepts a connection on `pipeName` at this instant: one attempt,
    /// no retry. Only a listening socket can accept, even with nobody left to serve it.
    let internal acceptsConnection (pipeName: string) : bool =
        use probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)

        try
            probe.Connect 0
            true
        with _ ->
            false

    /// Poll `accepts` until it is false or `bound` has passed; true when it went false.
    let internal waitUntilRefused (accepts: unit -> bool) (bound: TimeSpan) : Async<bool> =
        async {
            let clock = Diagnostics.Stopwatch.StartNew()
            let mutable accepting = accepts ()

            while accepting && clock.Elapsed < bound do
                do! Async.Sleep 1
                accepting <- accepts ()

            return not accepting
        }

    /// What serves a connection: its RPC target, and the context its calls run in
    /// (`None`: the server's own).
    [<NoComparison; NoEquality>]
    type internal Served =
        { Target: obj
          Context: ExecutionContext option }

    /// A daemon's RPCs, served in the daemon's context whichever server accepted them:
    /// its own pipe, or a repository host's endpoint.
    let internal servedDaemon
        (config: DaemonRpcConfig)
        (watchdog: OperationWatchdog.Watchdog)
        (disconnected: CancellationToken)
        (connectedClients: unit -> int)
        : Served =
        { Target =
            box (DaemonRpcTarget(config, watchdog, disconnected = disconnected, connectedClients = connectedClients))
          Context = config.Context }

    /// Decides what serves a newly connected client: the RPC target for it, or `None`
    /// when the connection has already been answered in full and should close. Given
    /// the connected pipe and a token cancelled when the client goes away.
    type internal ConnectionOpener = NamedPipeServerStream -> CancellationToken -> Async<Served option>

    /// Serve one accepted connection and clean up when done. Until its teardown has
    /// finished — the pipe disposed, not merely the RPC completed — the connection stays
    /// in `connections`, so shutdown can wait for it or close it.
    let private handle
        (pipeServer: NamedPipeServerStream)
        (opener: ConnectionOpener)
        (connections: Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, Task>)
        =
        let served =
            async {
                // One target per connection, cancelled when this client goes away, so
                // its calls stop rather than run on for nobody.
                use clientGone = new CancellationTokenSource()

                try
                    match! opener pipeServer clientGone.Token with
                    | None -> pipeServer.Dispose()
                    | Some served ->
                        let handler = new HeaderDelimitedMessageHandler(pipeServer :> System.IO.Stream)
                        use rpc = new JsonRpc(handler, served.Target)

                        rpc.Disconnected.Add(fun _ ->
                            try
                                clientGone.Cancel()
                            with :? ObjectDisposedException ->
                                // Torn down already: nothing is left running to cancel.
                                ())

                        // Calls are dispatched from the read loop this starts, in the
                        // context it is started in.
                        match served.Context with
                        | None -> rpc.StartListening()
                        | Some context ->
                            ExecutionContext.Run(
                                context.CreateCopy(),
                                ContextCallback(fun _ -> rpc.StartListening()),
                                null
                            )

                        try
                            // A client that vanishes faults the completion: an ordinary
                            // end of the connection, not a failure to report.
                            do! rpc.Completion.ContinueWith(fun (_: Task) -> ()) |> Async.AwaitTask
                        finally
                            pipeServer.Dispose()
                with ex ->
                    // A single bad connection (client vanished mid-handshake, broken
                    // pipe, RPC wiring failure) must not kill the server, nor vanish
                    // silently.
                    pipeServer.Dispose()
                    Logging.warn "ipc" $"IPC connection handler failed: %s{ex.ToString()}"
            }
            |> Async.StartAsTask

        connections[pipeServer] <- served

        served.ContinueWith(fun (_: Task) -> connections.TryRemove pipeServer |> ignore)
        |> ignore

    /// Wait for one client on `pipeServer`, then hand the connection to `handle` and
    /// return at once, so the accept loop can replace this acceptor while the connection
    /// is still being served. It can also run only after the connection has closed: what
    /// keeps the name listening meanwhile is `holdListener`.
    let private acceptOne
        (pipeServer: NamedPipeServerStream)
        (opener: ConnectionOpener)
        (connections: Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, Task>)
        (ct: CancellationToken)
        : Async<unit> =
        async {
            try
                do! pipeServer.WaitForConnectionAsync(ct) |> Async.AwaitTask
                handle pipeServer opener connections
            with
            | :? OperationCanceledException ->
                // Normal shutdown: the daemon's CancellationToken fired while
                // waiting for a client. Quiet by design.
                pipeServer.Dispose()
            | ex ->
                pipeServer.Dispose()
                Logging.warn "ipc" $"IPC connection handler failed: %s{ex.ToString()}"
        }

    /// A server instance of a pipe name that never accepts, held for as long as a server
    /// serves that name.
    ///
    /// Every server instance of a name shares ONE listening socket on Unix, which the
    /// runtime closes, unlinking its path, the moment the last instance is disposed: a
    /// client still in its backlog is reset (Linux), and one that arrives before a new
    /// instance binds the path again finds nothing to connect to. The acceptors and the
    /// connections they accepted do not cover every moment: the accept loop replaces an
    /// acceptor only once the thread pool runs it, which can be after every accepted
    /// connection has been answered and closed (AUTOMATION-1481). This instance spans
    /// those gaps. Not on Windows, where every instance is a pipe of its own, a client
    /// could connect to this one and never be served, and no listener is shared. `None`
    /// when it cannot be created; the acceptors then fail alike, and the loop logs them.
    let private holdListener (create: unit -> NamedPipeServerStream) : NamedPipeServerStream option =
        if OperatingSystem.IsWindows() then
            None
        else
            try
                Some(create ())
            with ex ->
                Logging.warn "ipc" $"IPC listener could not be held open: %s{ex.Message}"
                None

    /// Serve `pipeName` until `cts` is cancelled, handing every connection to
    /// `opener`. Keeps multiple accept tasks running concurrently so clients don't
    /// have to wait for the accept loop to cycle. On shutdown, connections still open
    /// after `drainBound` are closed, and this returns once the name no longer accepts
    /// connections (or `ReleaseBound` has passed, logged). The accept loop awaits
    /// `beforeReplacing` each time an acceptor finishes, before it replaces that acceptor:
    /// a test holds it there to stand for a loop the thread pool runs late.
    let internal serveConnectionsPaced
        (beforeReplacing: unit -> Async<unit>)
        (drainBound: TimeSpan)
        (pipeName: string)
        (opener: ConnectionOpener)
        (cts: CancellationTokenSource)
        : Async<unit> =
        async {
            let connections =
                Collections.Concurrent.ConcurrentDictionary<NamedPipeServerStream, Task>(HashIdentity.Reference)

            // Keep 3 accept tasks running at all times so clients can connect immediately
            let mutable acceptTasks: Task list = []

            let instance () =
                new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                )

            // Before any acceptor, and released only once serving is over (see
            // `holdListener`): the shared listening socket outlives every acceptor's and
            // connection's instance, however late the loop below replaces an acceptor.
            let held = holdListener instance

            let startAccept () =
                try
                    Async.StartAsTask(acceptOne (instance ()) opener connections cts.Token) :> Task
                with ex ->
                    Task.FromException ex

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

                    do! beforeReplacing ()

                    // Never replace an acceptor once shutdown has begun: a replacement
                    // would only open a new pipe server for shutdown to wait out.
                    if not cts.Token.IsCancellationRequested then
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

            // Return only once nothing holds this pipe name. On Unix every server
            // stream — a connected one included — keeps the shared listening socket
            // open until it is disposed, so a CLI probing just after this returned
            // would otherwise connect to a daemon that has stopped.
            //
            // Acceptors first: each waits on the same cancelled token, so this is
            // prompt, and once they are done no connection can be added.
            try
                do! Task.WhenAll(acceptTasks |> List.toArray) |> Async.AwaitTask
            with ex ->
                Logging.warn "ipc" $"IPC acceptor failed during shutdown: %s{ex.Message}"

            let open' = connections.ToArray()

            let teardowns =
                Task.WhenAll(open' |> Array.map (fun connection -> connection.Value))

            let! first = Task.WhenAny(teardowns, Task.Delay drainBound) |> Async.AwaitTask

            // Disposing the pipe ends its RPC too, whose own teardown then finds both
            // already disposed.
            if not (Object.ReferenceEquals(first, teardowns)) then
                for connection in open' do
                    connection.Key.Dispose()

            held |> Option.iter (fun listener -> listener.Dispose())

            // Disposal is not yet release: a cancelled accept still holds the listening
            // socket, and the runtime closes it moments later (measured: about 1 ms, in
            // roughly half of shutdowns). Only a refused connection proves it is gone.
            let! released = waitUntilRefused (fun () -> acceptsConnection pipeName) ReleaseBound

            if not released then
                Logging.warn
                    "ipc"
                    $"IPC pipe %s{pipeName} still accepts connections %g{ReleaseBound.TotalSeconds}s after shutdown — another server may own it"
        }

    /// Serve `pipeName` until `cts` is cancelled; see `serveConnectionsPaced`, whose accept
    /// loop here replaces an acceptor as soon as it runs.
    let internal serveConnections
        (drainBound: TimeSpan)
        (pipeName: string)
        (opener: ConnectionOpener)
        (cts: CancellationTokenSource)
        : Async<unit> =
        serveConnectionsPaced (fun () -> async.Return()) drainBound pipeName opener cts

    /// Start the IPC server. Keeps multiple accept tasks running concurrently
    /// so clients don't have to wait for the accept loop to cycle. On shutdown,
    /// connections still open after `drainBound` are closed.
    ///
    /// Every RPC it serves is tracked on `watchdog` (see `DaemonRpcTarget`), whose
    /// background timer logs the structured "operation exceeded Ns" record plus a
    /// periodic heartbeat. The caller owns and disposes it.
    let internal serveWith
        (watchdog: OperationWatchdog.Watchdog)
        (drainBound: TimeSpan)
        (pipeName: string)
        (config: DaemonRpcConfig)
        (cts: CancellationTokenSource)
        : Async<unit> =
        let connected = ref 0

        serveConnections
            drainBound
            pipeName
            (fun _ disconnected ->
                async {
                    // Held from accept to disconnect, and registered here because this runs before
                    // the connection serves its first call: `ReleaseIfSole` always counts itself.
                    Interlocked.Increment connected |> ignore

                    disconnected.Register(fun () -> Interlocked.Decrement connected |> ignore)
                    |> ignore

                    return
                        Some(servedDaemon config watchdog disconnected (fun () -> Volatile.Read(&connected.contents)))
                })
            cts

    /// Serve with a watchdog this server creates, and disposes when the server loop
    /// exits (daemon shutdown); see `serveWith`.
    let internal startWithin
        (drainBound: TimeSpan)
        (pipeName: string)
        (config: DaemonRpcConfig)
        (cts: CancellationTokenSource)
        : Async<unit> =
        async {
            use watchdog =
                new OperationWatchdog.Watchdog(
                    OperationWatchdog.DefaultThreshold,
                    heartbeatEvery = TimeSpan.FromSeconds(30.0),
                    now = (fun () -> DateTime.UtcNow),
                    log = Logging.info "watchdog",
                    awake = HostClock.system.AwakeNow
                )

            return! serveWith watchdog drainBound pipeName config cts
        }

    /// Start the IPC server; see `startWithin`. When this returns, the pipe name no
    /// longer accepts connections (or `ReleaseBound` has passed, logged).
    let start (pipeName: string) (config: DaemonRpcConfig) (cts: CancellationTokenSource) : Async<unit> =
        startWithin ConnectionDrainBound pipeName config cts

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

    /// `runCommand` on behalf of `invocationId`: what it asks for is held through that
    /// invocation's run claim as well as this connection (`RunCommandFor`).
    let runCommandFor (pipeName: string) (invocationId: string) (name: string) (argsJson: string) : Async<string> =
        invoke pipeName "RunCommandFor" [| name; argsJson; invocationId |]

    /// A stop RPC, describing `client` so the daemon can log who stopped it. A daemon from
    /// before AUTOMATION-1675 has no overload taking the description and answers that the
    /// method is missing; it is asked again without one, as an older CLI would.
    let internal invokeDescribed
        (invokeWith: obj array -> Async<string>)
        (client: DaemonStop.StopClient)
        : Async<string> =
        async {
            try
                return! invokeWith [| box client.Command; box client.Pid; box client.Cwd |]
            // `Async.AwaitTask` raises a faulted call as the task's `AggregateException`.
            with ex when (ex.GetBaseException() :? RemoteMethodNotFoundException) ->
                return! invokeWith [||]
        }

    /// Shut down the daemon gracefully, naming no client: the daemon logs the stop as from
    /// a client that did not describe itself. `shutdownAs` names one.
    let shutdown (pipeName: string) : Async<string> = invoke pipeName "Shutdown" [||]

    /// Shut down the daemon gracefully, on behalf of `client`.
    let shutdownAs (pipeName: string) (client: DaemonStop.StopClient) : Async<string> =
        invokeDescribed (invoke pipeName "Shutdown") client

    /// Stop the daemon only if this client is its sole user (`DaemonRpcTarget.ReleaseIfSole`),
    /// naming no client. `releaseIfSoleAs` names one.
    let releaseIfSole (pipeName: string) : Async<string> = invoke pipeName "ReleaseIfSole" [||]

    /// `releaseIfSole` on behalf of `client`.
    let releaseIfSoleAs (pipeName: string) (client: DaemonStop.StopClient) : Async<string> =
        invokeDescribed (invoke pipeName "ReleaseIfSole") client

    /// Trigger a full scan of all registered files.
    let scan (pipeName: string) : Async<string> = invoke pipeName "Scan" [||]

    /// `scan` held through `invocationId`'s run claim.
    let scanFor (pipeName: string) (invocationId: string) : Async<string> =
        invoke pipeName "ScanFor" [| invocationId |]

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

    /// Whether the daemon owns no work, waiting up to `timeoutMs` (`<= 0`: answer at once).
    /// `"rest"`, or `"busy: <why>"`.
    let waitForRest (pipeName: string) (timeoutMs: int) : Async<string> =
        invoke pipeName "WaitForRest" [| timeoutMs |]

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
    ///
    /// The connect runs on the caller's thread. `ConnectAsync` would queue it to the thread
    /// pool, so a caller blocking on it would wait for a pool thread as well as for the
    /// daemon, and a starved pool would read as "not running".
    let isRunning (pipeName: string) : bool =
        try
            use pipe =
                new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

            pipe.Connect 500
            true
        with _ex ->
            false
