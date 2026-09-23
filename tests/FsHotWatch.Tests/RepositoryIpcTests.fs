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

[<Fact(Timeout = 15000)>]
let ``a client cannot reach an endpoint nobody serves`` () =
    let endpoint = "fshw-test-" + Guid.NewGuid().ToString "N"
    test <@ not (isRunning endpoint) @>

    Assert.ThrowsAny<exn>(fun () -> attach endpoint "{}" |> Async.RunSynchronously |> ignore)
    |> ignore

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
        let ex =
            Assert.ThrowsAny<exn>(fun () -> attach endpoint "{}" |> Async.RunSynchronously |> ignore)

        test <@ ex.Message.Contains "no reply" || ex.InnerException <> null @>)

[<Fact(Timeout = 30000)>]
let ``a reply that is not a preamble reply is an error, not an answer`` () =
    withFakeServer
        (fun server -> (writeFrame server "{\"schema\":\"nope\"}" CancellationToken.None).Wait())
        (fun endpoint ->
            let ex =
                Assert.ThrowsAny<exn>(fun () ->
                    invoke endpoint (Preamble.Repository(InvocationId.mint ())) "ListSessions" [||]
                    |> Async.RunSynchronously
                    |> ignore)

            test <@ ex.Message.Contains "unreadable" @>)
