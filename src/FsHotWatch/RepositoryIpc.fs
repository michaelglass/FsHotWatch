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

/// Why a frame could not be read.
[<RequireQualifiedAccess>]
type FrameError =
    /// The stream ended before a whole frame arrived.
    | Truncated
    | TooLarge of bytes: int

let private readExactly (stream: Stream) (buffer: byte[]) (ct: CancellationToken) =
    task {
        let mutable read = 0
        let mutable ended = false

        while not ended && read < buffer.Length do
            let! n = stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)

            if n = 0 then ended <- true else read <- read + n

        return not ended
    }

/// Write one frame: a 4-byte big-endian length, then the UTF-8 text.
let writeFrame (stream: Stream) (text: string) (ct: CancellationToken) : Task =
    task {
        let payload = Encoding.UTF8.GetBytes text
        let length = BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder payload.Length)
        do! stream.WriteAsync(length.AsMemory(), ct)
        do! stream.WriteAsync(payload.AsMemory(), ct)
        do! stream.FlushAsync ct
    }

/// Read one frame written by `writeFrame`.
let readFrame (stream: Stream) (ct: CancellationToken) : Task<Result<string, FrameError>> =
    task {
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
/// serving, before the connection is refused.
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
        with
        | :? OperationCanceledException
        | :? IOException -> ()
    }

/// What the host does with each kind of connection.
[<NoComparison; NoEquality>]
type EndpointHandlers =
    {
        /// Answer an attach request (JSON in, JSON out).
        Attach: string -> string
        /// The RPC configuration of session `id`, or why the call is refused (kind,
        /// message). May wait for a just-attached session to begin serving.
        Session: SessionId -> InvocationId -> Task<Result<DaemonRpcConfig, string * string>>
        /// The target serving repository-level RPCs for one invocation.
        Repository: InvocationId -> obj
    }

/// The connection opener for the repository endpoint: read the preamble, answer it,
/// and hand back what serves the rest of the connection.
let internal opener (handlers: EndpointHandlers) (watchdog: OperationWatchdog.Watchdog) : IpcServer.ConnectionOpener =
    fun pipe disconnected ->
        async {
            use bound = CancellationTokenSource.CreateLinkedTokenSource disconnected
            bound.CancelAfter PreambleBound

            let reply text =
                writeFrame pipe text bound.Token |> Async.AwaitTask

            let refuse kind message =
                reply (encodeReply (PreambleReply.Refused(kind, message)))

            let! frame = readFrame pipe bound.Token |> Async.AwaitTask

            match frame with
            | Error FrameError.Truncated ->
                // The client left without a preamble: a liveness probe (`isRunning`)
                // connects and closes. Nobody is left to answer.
                return None
            | Error(FrameError.TooLarge bytes) ->
                do! refuse "malformed-preamble" $"a %d{bytes}-byte preamble exceeds %d{MaxFrameBytes} bytes"
                // The only refusal sent before the client's frame is read. Closing with
                // its bytes unread resets the connection on Linux, discarding the refusal
                // before the client reads it, so discard them first.
                do! drainUnread pipe RefusalDrainBound |> Async.AwaitTask
                return None
            | Ok text ->

                match decodePreamble text with
                | Error reason ->
                    do! refuse "malformed-preamble" reason
                    return None
                | Ok(Preamble.Attach json) ->
                    do! reply (handlers.Attach json)
                    return None
                | Ok(Preamble.Repository invocation) ->
                    do! reply (encodeReply PreambleReply.Accepted)
                    return Some(handlers.Repository invocation)
                | Ok(Preamble.Session(session, invocation)) ->
                    match! handlers.Session session invocation |> Async.AwaitTask with
                    | Error(kind, message) ->
                        do! refuse kind message
                        return None
                    | Ok config ->
                        do! reply (encodeReply PreambleReply.Accepted)
                        return Some(box (DaemonRpcTarget(config, watchdog, disconnected = disconnected)))
        }

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

/// The host refused a session or repository call.
exception RepositoryRefusedException of kind: string * message: string

/// How long a client waits to connect to the endpoint.
let ConnectBound = TimeSpan.FromSeconds 5.0

let private connect (endpoint: string) =
    async {
        let pipe =
            new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous)

        try
            do! pipe.ConnectAsync(int ConnectBound.TotalMilliseconds) |> Async.AwaitTask
            return pipe
        with ex ->
            pipe.Dispose()
            return raise ex
    }

let private readReplyFrame (pipe: Stream) =
    async {
        match! readFrame pipe CancellationToken.None |> Async.AwaitTask with
        | Ok text -> return text
        | Error e -> return raise (IOException $"the repository host sent no reply (%A{e})")
    }

/// Send an attach request and return the host's response JSON.
let attach (endpoint: string) (requestJson: string) : Async<string> =
    async {
        use! pipe = connect endpoint
        do! writeFrame pipe requestJson CancellationToken.None |> Async.AwaitTask
        return! readReplyFrame pipe
    }

/// Open a connection for `preamble`, and invoke `methodName` once it is accepted.
/// A refusal raises `RepositoryRefusedException`.
let invoke (endpoint: string) (preamble: Preamble) (methodName: string) (args: obj array) : Async<string> =
    async {
        use! pipe = connect endpoint

        do!
            writeFrame pipe (encodePreamble preamble) CancellationToken.None
            |> Async.AwaitTask

        let! replyText = readReplyFrame pipe

        match decodeReply replyText with
        | Ok PreambleReply.Accepted ->
            let handler = new HeaderDelimitedMessageHandler(pipe :> Stream)
            use rpc = new JsonRpc(handler)
            rpc.StartListening()
            return! rpc.InvokeAsync<string>(methodName, args) |> Async.AwaitTask
        | Ok(PreambleReply.Refused(kind, message)) -> return raise (RepositoryRefusedException(kind, message))
        | Error reason -> return raise (IOException $"the repository host's reply is unreadable: %s{reason}")
    }

/// Whether anything accepts connections on `endpoint` right now.
let isRunning (endpoint: string) : bool = IpcClient.isRunning endpoint
