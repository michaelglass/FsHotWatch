/// The repository host's endpoint: one pipe for every worktree of a repository.
///
/// Every connection opens with a PREAMBLE, one length-prefixed JSON frame naming
/// what the connection is for, and the host answers with one frame before anything
/// else happens:
///
/// - an attach request (`AttachHandshake`): answered with the attach response, and the
///   connection closes;
/// - a session call, carrying the `SessionId` and the caller's `InvocationId`: answered
///   `accepted`, after which the connection is ordinary JSON-RPC against that session's
///   daemon — exactly the per-worktree daemon's RPC surface — or `refused` with why;
/// - a repository call, carrying the `InvocationId`: JSON-RPC against the host itself
///   (list sessions, stop the host).
///
/// The client opens one connection per RPC, so every RPC carries its session and
/// invocation without any RPC method changing shape.
module FsHotWatch.RepositoryIpc

open System
open System.IO
open System.IO.Pipes
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open StreamJsonRpc
open FsHotWatch.Ipc
open FsHotWatch.RepositoryIdentity

/// One CLI invocation. Every RPC it sends carries it, so the host's log and watchdog
/// can say which invocation asked for what.
type InvocationId =
    private
    | InvocationId of string

    member this.Value =
        let (InvocationId v) = this
        v

    override this.ToString() = this.Value

module InvocationId =
    let mint () =
        InvocationId(Guid.NewGuid().ToString "N")

    let tryParse (s: string) : InvocationId option =
        if isLowerHex 32 s then Some(InvocationId s) else None

// ---------------------------------------------------------------------------
// Frames
// ---------------------------------------------------------------------------

/// The largest preamble or reply accepted. An attach carries the client's whole
/// environment, which is kilobytes; anything near this bound is not a preamble.
[<Literal>]
let MaxFrameBytes = 1048576

/// Why a frame could not be read or written.
[<RequireQualifiedAccess>]
type FrameError =
    /// The stream ended before a whole frame arrived.
    | Truncated
    | TooLarge of bytes: int
    /// The peer closed the connection while a frame was being read or written (a broken
    /// pipe, a reset). An ordinary outcome for either end of a connection: the other
    /// process may exit, or give up on it, at any moment.
    | HungUp of reason: string

module FrameError =
    let describe (error: FrameError) : string =
        match error with
        | FrameError.Truncated -> "the connection ended before a whole frame arrived"
        | FrameError.TooLarge bytes -> $"a %d{bytes}-byte frame exceeds %d{MaxFrameBytes} bytes"
        | FrameError.HungUp reason -> $"the other end hung up (%s{reason})"

let private readExactly (stream: Stream) (buffer: byte[]) (ct: CancellationToken) =
    task {
        let mutable read = 0
        let mutable ended = false

        while not ended && read < buffer.Length do
            let! n = stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)

            if n = 0 then ended <- true else read <- read + n

        return not ended
    }

/// Write one frame: a 4-byte big-endian length, then the UTF-8 text. A peer that hangs
/// up before the frame is through is `FrameError.HungUp`; cancellation still raises.
let writeFrame (stream: Stream) (text: string) (ct: CancellationToken) : Task<Result<unit, FrameError>> =
    task {
        let payload = Encoding.UTF8.GetBytes text
        let length = BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder payload.Length)

        try
            do! stream.WriteAsync(length.AsMemory(), ct)
            do! stream.WriteAsync(payload.AsMemory(), ct)
            do! stream.FlushAsync ct
            return Ok()
        with :? IOException as ex ->
            return Error(FrameError.HungUp ex.Message)
    }

/// Read one frame written by `writeFrame`. A peer that hangs up mid-frame is
/// `Truncated` when the stream ends cleanly and `HungUp` when the read itself fails;
/// cancellation still raises.
let readFrame (stream: Stream) (ct: CancellationToken) : Task<Result<string, FrameError>> =
    task {
        try
            let header = Array.zeroCreate<byte> 4
            let! gotHeader = readExactly stream header ct

            if not gotHeader then
                return Error FrameError.Truncated
            else
                let length = Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0))

                if length < 0 || length > MaxFrameBytes then
                    return Error(FrameError.TooLarge length)
                else
                    let payload = Array.zeroCreate<byte> length
                    let! gotPayload = readExactly stream payload ct

                    if gotPayload then
                        return Ok(Encoding.UTF8.GetString payload)
                    else
                        return Error FrameError.Truncated
        with :? IOException as ex ->
            return Error(FrameError.HungUp ex.Message)
    }

// ---------------------------------------------------------------------------
// Preambles and their replies
// ---------------------------------------------------------------------------

[<Literal>]
let SessionSchema = "fshw.session"

[<Literal>]
let RepositorySchema = "fshw.repository"

[<Literal>]
let ReplySchema = "fshw.preamble"

/// What a connection is for.
[<RequireQualifiedAccess>]
type Preamble =
    /// An attach request, as `AttachHandshake.encodeRequest` wrote it.
    | Attach of requestJson: string
    | Session of session: SessionId * invocation: InvocationId
    | Repository of invocation: InvocationId

let private stringField (node: JsonObject) (name: string) =
    match node[name] with
    | :? JsonValue as v ->
        match v.TryGetValue<string>() with
        | true, s -> Some s
        | _ -> None
    | _ -> None

let private tryObject (json: string) : JsonObject option =
    try
        match JsonNode.Parse json with
        | :? JsonObject as o -> Some o
        | _ -> None
    with _ ->
        None

let encodePreamble (preamble: Preamble) : string =
    match preamble with
    | Preamble.Attach json -> json
    | Preamble.Session(session, invocation) ->
        let node = JsonObject()
        node["schema"] <- JsonValue.Create SessionSchema
        node["session"] <- JsonValue.Create(SessionId.render session)
        node["invocation"] <- JsonValue.Create invocation.Value
        node.ToJsonString()
    | Preamble.Repository invocation ->
        let node = JsonObject()
        node["schema"] <- JsonValue.Create RepositorySchema
        node["invocation"] <- JsonValue.Create invocation.Value
        node.ToJsonString()

/// Read a preamble. An attach is passed through whole: the handshake decodes it and
/// answers even a malformed one with a refusal naming why.
let decodePreamble (json: string) : Result<Preamble, string> =
    match tryObject json with
    | None -> Error "the preamble is not a JSON object"
    | Some node ->
        let invocation () =
            stringField node "invocation"
            |> Option.bind InvocationId.tryParse
            |> Option.map Ok
            |> Option.defaultValue (Error "the preamble has no valid `invocation`")

        match stringField node "schema" with
        | Some AttachHandshake.Schema -> Ok(Preamble.Attach json)
        | Some SessionSchema ->
            match stringField node "session" |> Option.bind SessionId.tryParse with
            | None -> Error "the preamble has no valid `session`"
            | Some session -> invocation () |> Result.map (fun i -> Preamble.Session(session, i))
        | Some RepositorySchema -> invocation () |> Result.map Preamble.Repository
        | _ -> Error "the preamble names no known schema"

/// The host's answer to a session or repository preamble.
[<RequireQualifiedAccess>]
type PreambleReply =
    | Accepted
    | Refused of kind: string * message: string

let encodeReply (reply: PreambleReply) : string =
    let node = JsonObject()
    node["schema"] <- JsonValue.Create ReplySchema

    match reply with
    | PreambleReply.Accepted -> node["outcome"] <- JsonValue.Create "accepted"
    | PreambleReply.Refused(kind, message) ->
        node["outcome"] <- JsonValue.Create "refused"
        node["kind"] <- JsonValue.Create kind
        node["message"] <- JsonValue.Create message

    node.ToJsonString()

let decodeReply (json: string) : Result<PreambleReply, string> =
    match tryObject json with
    | Some node when stringField node "schema" = Some ReplySchema ->
        match stringField node "outcome", stringField node "kind", stringField node "message" with
        | Some "accepted", _, _ -> Ok PreambleReply.Accepted
        | Some "refused", Some kind, Some message -> Ok(PreambleReply.Refused(kind, message))
        | _ -> Error "the reply has no recognisable outcome"
    | _ -> Error "not a preamble reply"

// ---------------------------------------------------------------------------
// Server side
// ---------------------------------------------------------------------------

/// How long a connection may take to send its preamble, and a session to begin
/// serving, before the connection is refused. It bounds the client, never the host's
/// answer: an attach that takes longer on a loaded box is still answered.
let PreambleBound = TimeSpan.FromSeconds 10.0

/// How long a refused connection's unread bytes are drained before it closes.
let RefusalDrainBound = TimeSpan.FromSeconds 1.0

/// Read and discard what the client has still to send, until it stops (end of stream)
/// or `bound` passes.
let private drainUnread (stream: Stream) (bound: TimeSpan) : Task =
    task {
        use timeout = new CancellationTokenSource(bound)
        let buffer = Array.zeroCreate<byte> 4096

        try
            let mutable reading = true

            while reading do
                let! n = stream.ReadAsync(buffer.AsMemory(), timeout.Token)
                reading <- n > 0
        with _ ->
            // The bound passing, the client resetting, or anything else: the connection
            // closes next either way.
            ()
    }

/// What the host does with each kind of connection.
[<NoComparison; NoEquality>]
type EndpointHandlers =
    {
        /// Answer an attach request (JSON in, JSON out).
        Attach: string -> string
        /// An attach's answer (as `Attach` returned it) could not be sent: undo what it
        /// started, which no client knows of and so none would ever end.
        Undelivered: string -> unit
        /// The RPC configuration of session `id`, or why the call is refused (kind,
        /// message). May wait for a just-attached session to begin serving.
        Session: SessionId -> InvocationId -> Task<Result<DaemonRpcConfig, string * string>>
        /// The target serving repository-level RPCs for one invocation.
        Repository: InvocationId -> obj
    }

/// `opener`, with the bound on reading the preamble supplied.
let internal openerWithin
    (preambleBound: TimeSpan)
    (handlers: EndpointHandlers)
    (watchdog: OperationWatchdog.Watchdog)
    : IpcServer.ConnectionOpener =
    fun pipe disconnected ->
        async {
            // Bounds reading the preamble only. Under it, a host's answer that took
            // longer than the bound (an attach building its session on a loaded box)
            // was cancelled after the session was registered, and the session outlived
            // the client that never learned of it.
            use bound = CancellationTokenSource.CreateLinkedTokenSource disconnected
            bound.CancelAfter preambleBound

            // Whether the answer reached the client. One that hung up first is told
            // nothing, and its connection closes: there is nobody left to serve.
            let reply text =
                async {
                    match! writeFrame pipe text disconnected |> Async.AwaitTask with
                    | Ok() -> return Ok()
                    | Error e ->
                        Logging.info "host" $"a connection's answer could not be sent: %s{FrameError.describe e}"
                        return Error e
                }

            let refuse kind message =
                reply (encodeReply (PreambleReply.Refused(kind, message))) |> Async.Ignore

            // Accept the connection, and serve it with `served` once the client knows.
            let accept (served: unit -> IpcServer.Served) =
                async {
                    match! reply (encodeReply PreambleReply.Accepted) with
                    | Error _ -> return None
                    | Ok() -> return Some(served ())
                }

            // Awaited through a continuation that never cancels: awaiting the read itself
            // turns its cancellation into the whole opener's, which no `with` sees, and a
            // connection whose preamble came late was dropped without a line in the log.
            let! read =
                (readFrame pipe bound.Token).ContinueWith(fun (t: Task<Result<string, FrameError>>) -> t)
                |> Async.AwaitTask

            match read with
            | read when read.IsCanceled ->
                // Only the bound cancels this read: the connection's own token is not
                // cancelled before its preamble is answered.
                Logging.warn
                    "host"
                    $"a connection sent no preamble within %s{string preambleBound}; it is closed unanswered"

                return None
            | read ->

                match read.GetAwaiter().GetResult() with
                | Error(FrameError.TooLarge bytes) ->
                    do! refuse "malformed-preamble" $"a %d{bytes}-byte preamble exceeds %d{MaxFrameBytes} bytes"
                    // The only refusal sent before the client's frame is read. Closing with
                    // its bytes unread resets the connection on Linux, discarding the refusal
                    // before the client reads it, so discard them first.
                    do! drainUnread pipe RefusalDrainBound |> Async.AwaitTask
                    return None
                | Error _ ->
                    // `Truncated` or `HungUp`: the client left without a preamble. A
                    // liveness probe (`isRunning`) connects and closes. Nobody is left to
                    // answer.
                    return None
                | Ok text ->

                    match decodePreamble text with
                    | Error reason ->
                        do! refuse "malformed-preamble" reason
                        return None
                    | Ok(Preamble.Attach json) ->
                        let answer = handlers.Attach json

                        match! reply answer with
                        | Ok() -> ()
                        | Error e ->
                            Logging.warn
                                "host"
                                $"an attach's answer could not be sent (%s{FrameError.describe e}); undoing it"

                            handlers.Undelivered answer

                        return None
                    | Ok(Preamble.Repository invocation) ->
                        return!
                            accept (fun () ->
                                { IpcServer.Served.Target = handlers.Repository invocation
                                  IpcServer.Served.Context = None })
                    | Ok(Preamble.Session(session, invocation)) ->
                        match! handlers.Session session invocation |> Async.AwaitTask with
                        | Error(kind, message) ->
                            do! refuse kind message
                            return None
                        | Ok config -> return! accept (fun () -> IpcServer.servedDaemon config watchdog disconnected)
        }

/// The connection opener for the repository endpoint: read the preamble, answer it,
/// and hand back what serves the rest of the connection.
let internal opener (handlers: EndpointHandlers) (watchdog: OperationWatchdog.Watchdog) : IpcServer.ConnectionOpener =
    openerWithin PreambleBound handlers watchdog

/// Serve the repository endpoint until `cts` is cancelled.
let serve (endpoint: string) (handlers: EndpointHandlers) (cts: CancellationTokenSource) : Async<unit> =
    async {
        use watchdog =
            new OperationWatchdog.Watchdog(
                OperationWatchdog.DefaultThreshold,
                heartbeatEvery = TimeSpan.FromSeconds(30.0),
                now = (fun () -> DateTime.UtcNow),
                log = Logging.info "watchdog"
            )

        return! IpcServer.serveConnections IpcServer.ConnectionDrainBound endpoint (opener handlers watchdog) cts
    }

// ---------------------------------------------------------------------------
// Client side
// ---------------------------------------------------------------------------

/// Why a client's exchange with the host came to nothing.
[<RequireQualifiedAccess>]
type ExchangeError =
    /// No connection to the endpoint: nothing accepted one within `ConnectBound`, or
    /// connecting failed.
    | Unreachable of reason: string
    /// The host hung up, or sent something that is not a frame, before it replied.
    | NoReply of FrameError
    /// The host replied with a frame that is not a preamble reply.
    | UnreadableReply of reason: string
    /// The host refused the session or repository call.
    | Refused of kind: string * message: string

module ExchangeError =
    let describe (error: ExchangeError) : string =
        match error with
        | ExchangeError.Unreachable reason -> $"the repository host could not be reached: %s{reason}"
        | ExchangeError.NoReply e -> $"the repository host sent no reply: %s{FrameError.describe e}"
        | ExchangeError.UnreadableReply reason -> $"the repository host's reply is unreadable: %s{reason}"
        | ExchangeError.Refused(kind, message) -> $"the repository host refused the call (%s{kind}): %s{message}"

/// The host refused a session or repository call that was sent on the daemon RPC
/// surface (`IpcOps`), which reports failures by raising.
exception RepositoryRefusedException of kind: string * message: string

/// `error` as the exception the daemon RPC surface raises for it: a refusal as
/// `RepositoryRefusedException`, a host that was not there or left as `IOException`,
/// the same shape a per-worktree daemon's dropped pipe takes.
let exchangeException (error: ExchangeError) : exn =
    match error with
    | ExchangeError.Refused(kind, message) -> RepositoryRefusedException(kind, message)
    | other -> IOException(ExchangeError.describe other)

/// How long a client waits to connect to the endpoint.
let ConnectBound = TimeSpan.FromSeconds 5.0

let private connect (endpoint: string) : Async<Result<NamedPipeClientStream, ExchangeError>> =
    async {
        let pipe =
            new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous)

        let! connected =
            pipe.ConnectAsync(int ConnectBound.TotalMilliseconds)
            |> Async.AwaitTask
            |> Async.Catch

        match connected with
        | Choice1Of2() -> return Ok pipe
        | Choice2Of2 ex ->
            pipe.Dispose()
            // `Async.AwaitTask` on a non-generic task raises its fault wrapped in an
            // `AggregateException`; the base exception is the connect's own.
            return Error(ExchangeError.Unreachable(ex.GetBaseException().Message))
    }

/// Connect, send `frame`, and read the host's one-frame reply.
let private exchange (endpoint: string) (frame: string) : Async<Result<NamedPipeClientStream * string, ExchangeError>> =
    async {
        match! connect endpoint with
        | Error e -> return Error e
        | Ok pipe ->
            let! sent = writeFrame pipe frame CancellationToken.None |> Async.AwaitTask

            let! replied =
                match sent with
                | Error e -> async.Return(Error e)
                | Ok() -> readFrame pipe CancellationToken.None |> Async.AwaitTask

            match replied with
            | Ok reply -> return Ok(pipe, reply)
            | Error e ->
                pipe.Dispose()
                return Error(ExchangeError.NoReply e)
    }

/// Send an attach request and return the host's response JSON.
let attach (endpoint: string) (requestJson: string) : Async<Result<string, ExchangeError>> =
    async {
        match! exchange endpoint requestJson with
        | Error e -> return Error e
        | Ok(pipe, reply) ->
            pipe.Dispose()
            return Ok reply
    }

/// Open a connection for `preamble`, and invoke `methodName` once it is accepted. An
/// exchange that never reaches the RPC is an `ExchangeError`; the RPC's own failures
/// raise, as the daemon RPC surface's do.
let invoke
    (endpoint: string)
    (preamble: Preamble)
    (methodName: string)
    (args: obj array)
    : Async<Result<string, ExchangeError>> =
    async {
        match! exchange endpoint (encodePreamble preamble) with
        | Error e -> return Error e
        | Ok(pipe, replyText) ->
            use pipe = pipe

            match decodeReply replyText with
            | Error reason -> return Error(ExchangeError.UnreadableReply reason)
            | Ok(PreambleReply.Refused(kind, message)) -> return Error(ExchangeError.Refused(kind, message))
            | Ok PreambleReply.Accepted ->
                let handler = new HeaderDelimitedMessageHandler(pipe :> Stream)
                use rpc = new JsonRpc(handler)
                rpc.StartListening()
                let! result = rpc.InvokeAsync<string>(methodName, args) |> Async.AwaitTask
                return Ok result
    }

/// Whether anything accepts connections on `endpoint` right now.
let isRunning (endpoint: string) : bool = IpcClient.isRunning endpoint
