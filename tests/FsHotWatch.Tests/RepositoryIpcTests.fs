/// The repository endpoint's wire: length-prefixed frames, the preamble every
/// connection opens with, and the host's one-frame answer to it.
module FsHotWatch.Tests.RepositoryIpcTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.RepositoryIdentity
open FsHotWatch.RepositoryIpc

let private session =
    { Repository = (RepositoryId.tryParse (String.replicate 32 "a")).Value
      Worktree = (WorktreeId.tryParse (String.replicate 32 "b")).Value
      Incarnation = SessionIncarnation.mint () }

[<Fact(Timeout = 5000)>]
let ``invocation ids are 32 lowercase hex characters, and parse strictly`` () =
    let id = InvocationId.mint ()
    test <@ InvocationId.tryParse id.Value = Some id @>
    test <@ string id = id.Value @>
    test <@ InvocationId.tryParse "NOT-HEX" = None @>
    test <@ InvocationId.tryParse (id.Value.ToUpperInvariant()) = None @>

[<Fact(Timeout = 5000)>]
let ``every preamble round-trips`` () =
    let invocation = InvocationId.mint ()

    for preamble in
        [ Preamble.Session(session, invocation)
          Preamble.Repository invocation
          Preamble.Attach "{\"schema\":\"fshw.attach\",\"protocol\":2}" ] do
        test <@ decodePreamble (encodePreamble preamble) = Ok preamble @>

[<Fact(Timeout = 5000)>]
let ``a preamble without a usable schema, session or invocation is an error that says so`` () =
    let cases =
        [ "not json", "not a JSON object"
          "[1]", "not a JSON object"
          "{}", "no known schema"
          "{\"schema\":\"fshw.session\",\"invocation\":\"x\"}", "`session`"
          $"{{\"schema\":\"fshw.session\",\"session\":\"%s{SessionId.render session}\"}}", "`invocation`"
          "{\"schema\":\"fshw.repository\",\"invocation\":7}", "`invocation`" ]

    for json, says in cases do
        test
            <@
                match decodePreamble json with
                | Error reason -> reason.Contains says
                | Ok _ -> false
            @>

[<Fact(Timeout = 5000)>]
let ``replies round-trip, and anything else is not a reply`` () =
    for reply in [ PreambleReply.Accepted; PreambleReply.Refused("unknown-session", "gone") ] do
        test <@ decodeReply (encodeReply reply) = Ok reply @>

    for json in
        [ "nope"
          "{\"schema\":\"other\",\"outcome\":\"accepted\"}"
          "{\"schema\":\"fshw.preamble\",\"outcome\":\"refused\"}"
          "{\"schema\":\"fshw.preamble\"}" ] do
        test <@ Result.isError (decodeReply json) @>

[<Fact(Timeout = 5000)>]
let ``a frame round-trips, and a short or oversized one is refused`` () =
    use stream = new MemoryStream()
    (writeFrame stream "héllo" CancellationToken.None).Wait()
    stream.Position <- 0L
    test <@ (readFrame stream CancellationToken.None).Result = Ok "héllo" @>

    use empty = new MemoryStream()
    test <@ (readFrame empty CancellationToken.None).Result = Error FrameError.Truncated @>

    use truncated = new MemoryStream([| 0uy; 0uy; 0uy; 9uy; 65uy |])
    test <@ (readFrame truncated CancellationToken.None).Result = Error FrameError.Truncated @>

    let negative = BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder -1)
    use backwards = new MemoryStream(negative)
    test <@ (readFrame backwards CancellationToken.None).Result = Error(FrameError.TooLarge -1) @>

    let huge =
        BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder(MaxFrameBytes + 1))

    use oversized = new MemoryStream(huge)
    test <@ (readFrame oversized CancellationToken.None).Result = Error(FrameError.TooLarge(MaxFrameBytes + 1)) @>

/// A stream whose peer has gone: every read and write fails as a reset connection does.
type private HungUpStream() =
    inherit MemoryStream()

    override _.ReadAsync(_: Memory<byte>, _: CancellationToken) =
        Threading.Tasks.ValueTask<int>(Threading.Tasks.Task.FromException<int>(IOException "Connection reset by peer"))

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        Threading.Tasks.ValueTask(Threading.Tasks.Task.FromException(IOException "Broken pipe"))

[<Fact(Timeout = 5000)>]
let ``a peer that hangs up while a frame is read is a frame error, not an exception`` () =
    use stream = new HungUpStream()

    test <@ (readFrame stream CancellationToken.None).Result = Error(FrameError.HungUp "Connection reset by peer") @>

[<Fact(Timeout = 5000)>]
let ``a peer that hangs up while a frame is written is a frame error, not an exception`` () =
    use stream = new HungUpStream()

    test <@ (writeFrame stream "héllo" CancellationToken.None).Result = Error(FrameError.HungUp "Broken pipe") @>

[<Fact(Timeout = 5000)>]
let ``a cancelled write still raises: cancellation is the caller's, not a hang-up`` () =
    use stream = new MemoryStream()
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()

    let ex =
        Assert.ThrowsAny<exn>(fun () -> (writeFrame stream "héllo" cancelled.Token).Wait())

    test <@ (ex :? AggregateException) && (ex.InnerException :? OperationCanceledException) @>

[<Fact(Timeout = 5000)>]
let ``every exchange error says what happened, and raises as the daemon surface expects`` () =
    let cases =
        [ ExchangeError.Unreachable "timed out", "could not be reached: timed out"
          ExchangeError.NoReply FrameError.Truncated, "before a whole frame arrived"
          ExchangeError.NoReply(FrameError.TooLarge 7), "a 7-byte frame exceeds"
          ExchangeError.NoReply(FrameError.HungUp "Broken pipe"), "hung up (Broken pipe)"
          ExchangeError.UnreadableReply "nope", "unreadable: nope"
          ExchangeError.Refused("stale-incarnation", "gone"), "refused the call (stale-incarnation): gone" ]

    for error, says in cases do
        test <@ (ExchangeError.describe error).Contains says @>

    test
        <@
            match exchangeException (ExchangeError.Refused("stale-incarnation", "gone")) with
            | RepositoryRefusedException("stale-incarnation", "gone") -> true
            | _ -> false
        @>

    let hungUp =
        exchangeException (ExchangeError.NoReply(FrameError.HungUp "Broken pipe"))

    test <@ hungUp :? IOException && hungUp.Message.Contains "hung up" @>

[<Fact(Timeout = 30000)>]
let ``a client cannot reach an endpoint nobody serves`` () =
    let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"
    test <@ not (isRunning endpoint) @>

    test
        <@
            match attach endpoint "{}" |> Async.RunSynchronously with
            | Error(ExchangeError.Unreachable _) -> true
            | _ -> false
        @>

    test
        <@
            match
                invoke endpoint (Preamble.Repository(InvocationId.mint ())) "ListSessions" [||]
                |> Async.RunSynchronously
            with
            | Error(ExchangeError.Unreachable _) -> true
            | _ -> false
        @>

/// A server that reads one frame and then does `respond` with the connection.
let private withFakeServer (respond: IO.Pipes.NamedPipeServerStream -> unit) (body: string -> unit) =
    let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"

    use server =
        new IO.Pipes.NamedPipeServerStream(
            endpoint,
            IO.Pipes.PipeDirection.InOut,
            1,
            IO.Pipes.PipeTransmissionMode.Byte,
            IO.Pipes.PipeOptions.Asynchronous
        )

    let serving =
        Threading.Tasks.Task.Run(fun () ->
            server.WaitForConnection()
            (readFrame server CancellationToken.None).Result |> ignore
            respond server)

    body endpoint
    serving.Wait(TimeSpan.FromSeconds 10.0) |> ignore

[<Fact(Timeout = 30000)>]
let ``a host that hangs up without replying is an error, not an answer`` () =
    withFakeServer (fun server -> server.Disconnect()) (fun endpoint ->
        test
            <@
                match attach endpoint "{}" |> Async.RunSynchronously with
                | Error(ExchangeError.NoReply _) -> true
                | _ -> false
            @>)

[<Fact(Timeout = 30000)>]
let ``a reply that is not a preamble reply is an error, not an answer`` () =
    withFakeServer
        (fun server -> (writeFrame server "{\"schema\":\"nope\"}" CancellationToken.None).Wait())
        (fun endpoint ->
            test
                <@
                    match
                        invoke endpoint (Preamble.Repository(InvocationId.mint ())) "ListSessions" [||]
                        |> Async.RunSynchronously
                    with
                    | Error(ExchangeError.UnreadableReply _) -> true
                    | _ -> false
                @>)

/// A real endpoint serving `handlers` for the length of `body`.
let private withEndpoint (handlers: EndpointHandlers) (body: string -> unit) =
    let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"
    use cts = new CancellationTokenSource()
    let serving = Async.StartImmediateAsTask(serve endpoint handlers cts)

    try
        body endpoint
    finally
        cts.Cancel()
        serving.Wait(TimeSpan.FromSeconds 10.0) |> ignore

let private refusingHandlers (attachWith: string -> string) : EndpointHandlers =
    { Attach = attachWith
      Undelivered = ignore
      Session = fun _ _ -> Threading.Tasks.Task.FromResult(Error("unknown-session", "no sessions in this test"))
      Repository = fun _ -> obj () }

/// The smallest frame the endpoint reads as an attach; the handler decides the rest.
let private attachPreamble =
    $"{{\"schema\":\"%s{FsHotWatch.AttachHandshake.Schema}\"}}"

// The orphaned-host incident: an attach that took longer than the preamble bound on a
// loaded box registered its session, then had its reply cancelled. The client read
// "no reply", and the session it never learned of kept the host up for 11 hours.
[<Fact(Timeout = 60000)>]
let ``an attach that outlasts the preamble bound is still answered`` () =
    let slowAttach _ =
        Thread.Sleep(PreambleBound + TimeSpan.FromSeconds 1.0)
        "attached"

    withEndpoint (refusingHandlers slowAttach) (fun endpoint ->
        test <@ attach endpoint attachPreamble |> Async.RunSynchronously = Ok "attached" @>)

/// The repository endpoint's opener for one connection, reading its preamble within
/// `preambleBound`, with its log lines captured: no accept loop and no scheduling of it,
/// so what the opener does with a connection is the only thing a test observes. `client`
/// runs against the connected client end; the opener's answer and its log follow.
let private openOnce
    (preambleBound: TimeSpan)
    (handlers: EndpointHandlers)
    (client: IO.Pipes.NamedPipeClientStream -> unit)
    : Threading.Tasks.Task<FsHotWatch.Ipc.IpcServer.Served option> * Collections.Concurrent.ConcurrentQueue<string> =
    let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"
    let logged = Collections.Concurrent.ConcurrentQueue<string>()

    let server =
        new IO.Pipes.NamedPipeServerStream(
            endpoint,
            IO.Pipes.PipeDirection.InOut,
            1,
            IO.Pipes.PipeTransmissionMode.Byte,
            IO.Pipes.PipeOptions.Asynchronous
        )

    let opened =
        Threading.Tasks.Task.Run(fun () ->
            use _ =
                FsHotWatch.Logging.installSink
                    { Write = logged.Enqueue
                      Level = FsHotWatch.Logging.LogLevel.Debug
                      File = None }

            use watchdog =
                new FsHotWatch.OperationWatchdog.Watchdog(
                    FsHotWatch.OperationWatchdog.DefaultThreshold,
                    heartbeatEvery = TimeSpan.FromSeconds 30.0,
                    now = (fun () -> DateTime.UtcNow),
                    log = ignore
                )

            server.WaitForConnection()

            try
                openerWithin preambleBound handlers watchdog server CancellationToken.None
                |> Async.RunSynchronously
            finally
                server.Dispose())

    let pipe =
        new IO.Pipes.NamedPipeClientStream(".", endpoint, IO.Pipes.PipeDirection.InOut)

    pipe.Connect 5000
    client pipe

    // The client end stays open until the opener is done with the connection: dropped
    // early (collected, say), the host reads end-of-stream rather than the silence a test
    // means to send.
    opened.Wait(TimeSpan.FromSeconds 30.0) |> ignore
    pipe.Dispose()
    opened, logged

[<Fact(Timeout = 60000)>]
let ``an attach whose client left before the answer is undone`` () =
    // Driven through the opener directly, with no bound on the preamble for the test's
    // own timing to race: the client sends its attach and closes, the host answers into
    // the closed pipe, and the attach it started is undone.
    let clientGone = new ManualResetEventSlim(false)
    let undone = Threading.Tasks.TaskCompletionSource<string>()

    let handlers =
        { refusingHandlers (fun _ ->
              clientGone.Wait() |> ignore
              "attached") with
            Undelivered = fun answer -> undone.TrySetResult answer |> ignore }

    let opened, logged =
        openOnce (TimeSpan.FromMinutes 5.0) handlers (fun client ->
            (writeFrame client attachPreamble CancellationToken.None).Wait()
            client.Dispose()
            clientGone.Set())

    test <@ opened.Wait(TimeSpan.FromSeconds 30.0) && opened.Result.IsNone @>
    test <@ undone.Task.IsCompleted && undone.Task.Result = "attached" @>
    test <@ logged |> Seq.exists (fun line -> line.Contains "could not be sent") @>

[<Fact(Timeout = 60000)>]
let ``a session call whose client left before it was accepted is not served`` () =
    // The client sends its preamble and hangs up while the host looks the session up: the
    // acceptance cannot be sent, and the connection closes instead of being served.
    let clientGone = new ManualResetEventSlim(false)

    let handlers =
        { refusingHandlers (fun _ -> "attached") with
            Session =
                fun _ _ ->
                    Threading.Tasks.Task.Run(fun () ->
                        clientGone.Wait() |> ignore
                        // Never served: the acceptance cannot reach the client.
                        Ok Unchecked.defaultof<FsHotWatch.Ipc.DaemonRpcConfig>) }

    let opened, logged =
        openOnce (TimeSpan.FromMinutes 5.0) handlers (fun client ->
            (writeFrame
                client
                (encodePreamble (Preamble.Session(session, InvocationId.mint ())))
                CancellationToken.None)
                .Wait()

            client.Dispose()
            clientGone.Set())

    test <@ opened.Wait(TimeSpan.FromSeconds 30.0) && opened.Result.IsNone @>
    test <@ logged |> Seq.exists (fun line -> line.Contains "could not be sent") @>

[<Fact(Timeout = 60000)>]
let ``a connection that sends no preamble within the bound is closed, and says so`` () =
    // It was closed silently: the read's cancellation cancelled the whole opener, which no
    // `with` sees, so an attach whose bytes arrived late on a loaded box vanished from the
    // log. The connection is still closed unanswered; the log now names why.
    let attachSeen = ref false

    let handlers =
        refusingHandlers (fun _ ->
            attachSeen.Value <- true
            "attached")

    let opened, logged = openOnce (TimeSpan.FromMilliseconds 200.0) handlers ignore

    test <@ opened.Wait(TimeSpan.FromSeconds 30.0) && opened.Result.IsNone @>
    test <@ not attachSeen.Value @>
    test <@ logged |> Seq.exists (fun line -> line.Contains "no preamble within") @>
