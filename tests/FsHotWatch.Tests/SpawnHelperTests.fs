// Children started through a spawn helper keep every guarantee a local child has.
//
// `InProcessHelper` runs the helper's own serve loop on a thread of this process, over
// the same pipes the daemon would use, so these tests start real children through the
// real protocol. `ScriptedHelper` plays the helper's side by hand, for the arms a real
// helper cannot be made to take on demand: an answer that never comes, a late answer,
// a helper that disappears mid-run. Registry diagnostics are logged, so the class
// shares the serialized logging collection.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.SpawnHelperTests

open System
open System.ComponentModel
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.ProcessHelper
open FsHotWatch.SpawnHelper
open FsHotWatch.Tests.TestHelpers

/// Two one-way pipes, both ends in this process.
type private PipePair() =
    let writeEnd = new AnonymousPipeServerStream(PipeDirection.Out)

    let readEnd =
        new AnonymousPipeClientStream(PipeDirection.In, writeEnd.ClientSafePipeHandle)

    member _.Write: Stream = writeEnd
    member _.Read: Stream = readEnd

    interface IDisposable with
        member _.Dispose() =
            writeEnd.Dispose()
            readEnd.Dispose()

/// The helper's serve loop on a dedicated thread, and the daemon's connection to it.
type private InProcessHelper() =
    let requests = new PipePair()
    let events = new PipePair()

    let server =
        Task.Factory.StartNew(
            (fun () ->
                serve requests.Read events.Write
                // The helper process exiting closes its output.
                events.Write.Dispose()),
            TaskCreationOptions.LongRunning
        )

    let connection = new Connection(requests.Write, events.Read)

    member _.Connection = connection

    /// Close the request pipe, as a daemon that exits does, and wait for the helper to
    /// finish its teardown.
    member _.Shutdown() =
        (connection :> IDisposable).Dispose()
        Assert.True(server.Wait(10000), "the helper must finish once its request pipe closes")

    interface IDisposable with
        member this.Dispose() =
            this.Shutdown()
            (requests :> IDisposable).Dispose()
            (events :> IDisposable).Dispose()

/// The helper's side of the pipes, driven by the test.
type private ScriptedHelper() =
    let requests = new PipePair()
    let events = new PipePair()
    let requestReader = new BinaryReader(requests.Read)
    let eventWriter = new BinaryWriter(events.Write)
    let connection = new Connection(requests.Write, events.Read)

    member _.Connection = connection
    member _.NextRequest() : Request option = readRequest requestReader
    member _.Send(event: Event) = writeEvent eventWriter event

    member _.SendRaw(bytes: byte array) =
        events.Write.Write(bytes, 0, bytes.Length)

    /// The helper goes away: its output closes.
    member _.Vanish() = events.Write.Dispose()
    /// The helper stops reading requests.
    member _.StopReading() = requests.Read.Dispose()

    member _.WaitUntilLost() =
        Assert.True(SpinWait.SpinUntil((fun () -> connection.IsLost), 10000), "the connection must notice the loss")

    interface IDisposable with
        member _.Dispose() =
            (requests :> IDisposable).Dispose()
            (events :> IDisposable).Dispose()

let private startId (request: Request option) : int64 =
    match request with
    | Some(Request.Start(id, _, _, _, _)) -> id
    | other -> failwith $"expected a start request, got %A{other}"

let private isGone (pid: int) =
    SpinWait.SpinUntil((fun () -> isProcessAlive pid = Ok false), 10000)

let private shell (script: string) = "-c \"" + script + "\""

let private mkfifo (directory: string) (name: string) =
    match runProcess "mkfifo" name directory [] (ProcessBounds.silent (TimeSpan.FromSeconds 10.0)) with
    | Succeeded _ -> Path.Combine(directory, name)
    | other -> failwith $"mkfifo failed: %A{other}"

let private tenSeconds = ProcessBounds.silent (TimeSpan.FromSeconds 10.0)

// ---------------------------------------------------------------------------
// Real children through the real protocol
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a hook step through the helper is owned while it runs and reports its output and exit`` () =
    withTempDir "spawn-helper-owned" (fun directory ->
        let gate = mkfifo directory "gate"
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry
        let mutable observedPid = 0
        let mutable ownedWhileRunning = []
        let mutable localHandlesWhileRunning = []

        let onStarted pid =
            observedPid <- pid
            ownedWhileRunning <- registry.LivePids()
            localHandlesWhileRunning <- registry.Snapshot()
            // The child is blocked reading the gate, so it was alive for the reads above.
            File.WriteAllText(gate, "go\n")

        let outcome =
            runProcessObserved
                onStarted
                "/bin/sh"
                (shell "echo out; echo err >&2; read line < gate; exit 3")
                directory
                []
                tenSeconds

        Assert.Equal<int list>([ observedPid ], ownedWhileRunning)
        // Owned without a local `Process`: the helper started it, not this process.
        Assert.Empty(localHandlesWhileRunning)

        match outcome with
        | Failed(3, ProcessOutput.Drained text) ->
            Assert.Contains("out", text)
            Assert.Contains("err", text)
        | other -> failwith $"expected exit 3 with a complete capture, got %A{other}"

        Assert.Empty(registry.LivePids())
        Assert.Empty(registry.Leaks))

[<Fact(Timeout = 30000)>]
let ``output reaches the sink while the helper's child is still running`` () =
    withTempDir "spawn-helper-streaming" (fun directory ->
        let gate = mkfifo directory "gate"
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection

        let ready =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        // The child cannot exit until the sink has seen its first line.
        let opener =
            ready.Task.ContinueWith(fun (_: Task) -> File.WriteAllText(gate, "go\n"))

        let sink (chunk: string) =
            if chunk.Contains "ready" then
                ready.TrySetResult() |> ignore

        let outcome, _ =
            runProcessCore
                true
                false
                (Some sink)
                ignore
                "/bin/sh"
                (shell "echo ready; read line < gate; echo done")
                directory
                []
                tenSeconds

        opener.Wait()

        match outcome with
        | Succeeded(ProcessOutput.Drained text) -> Assert.Equal("ready\ndone", text)
        | other -> failwith $"expected success, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``a helper child that overruns its timeout is killed through the helper`` () =
    withTempDir "spawn-helper-timeout" (fun directory ->
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry
        let mutable pid = 0

        let outcome =
            runProcessObserved
                (fun started -> pid <- started)
                "/bin/sh"
                (shell "sleep 30")
                directory
                []
                (ProcessBounds.silent (TimeSpan.FromSeconds 1.0))

        match outcome with
        | TimedOut(_, _, KillOutcome.Killed) -> ()
        | other -> failwith $"expected a timeout whose kill succeeded, got %A{other}"

        Assert.True(isGone pid, "the timed-out child must be dead")
        Assert.Empty(registry.Leaks))

[<Fact(Timeout = 30000)>]
let ``shutting down the scope kills a running helper child`` () =
    withTempDir "spawn-helper-shutdown" (fun directory ->
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry

        let started =
            TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

        let run =
            Task.Run(fun () ->
                runProcessObserved
                    (fun pid -> started.TrySetResult pid |> ignore)
                    "/bin/sh"
                    (shell "sleep 30")
                    directory
                    []
                    (ProcessBounds.silent (TimeSpan.FromSeconds 25.0)))

        let pid = started.Task.Result
        registry.KillAll()

        Assert.True(run.Wait(10000), "the run must end once its child is killed")
        Assert.False(isSucceeded run.Result)
        Assert.True(isGone pid, "the scope's shutdown must have killed the child")
        Assert.Empty(registry.Leaks))

[<Fact(Timeout = 30000)>]
let ``a closed scope refuses a hook step before the helper starts anything`` () =
    withTempDir "spawn-helper-refused" (fun directory ->
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry
        registry.KillAll()

        Assert.Throws<OperationCanceledException>(fun () ->
            runProcessObserved ignore "/bin/sh" (shell "echo target > executed") directory [] tenSeconds
            |> ignore)
        |> ignore

        Assert.False(File.Exists(Path.Combine(directory, "executed")), "the refused target must never have run"))

[<Fact(Timeout = 30000)>]
let ``a command the helper cannot start raises what Process.Start would`` () =
    withTempDir "spawn-helper-missing" (fun directory ->
        use helper = new InProcessHelper()
        use _ = SpawnHelper.install helper.Connection

        Assert.Throws<Win32Exception>(fun () ->
            runProcessObserved ignore "/nonexistent/fshw-no-such-command" "" directory [] tenSeconds
            |> ignore)
        |> ignore)

[<Fact(Timeout = 30000)>]
let ``a helper child is observable through its exit and a released child cannot be killed`` () =
    withTempDir "spawn-helper-exit" (fun directory ->
        use helper = new InProcessHelper()

        let child =
            helper.Connection.Start("/bin/sh", shell "exit 7", directory, [], StartBudget)

        Assert.True(child.WaitForExit 10000, "the child must exit")
        Assert.True(child.HasExited)
        Assert.Equal(7, child.ExitCode)

        match (child :> ProcessRegistry.IOwnedChild).Observe() with
        | ProcessRegistry.ExitObservation.Exited -> ()
        | other -> failwith $"expected Exited, got %A{other}"

        child.Dispose()

        // The helper no longer holds it, so the kill is refused as a kill of a child
        // that already exited would be.
        Assert.Throws<InvalidOperationException>(fun () -> child.KillTree()) |> ignore)

[<Fact(Timeout = 30000)>]
let ``closing the request pipe makes the helper kill what it still holds`` () =
    withTempDir "spawn-helper-orphans" (fun directory ->
        let helper = new InProcessHelper()

        let child =
            helper.Connection.Start("/bin/sh", shell "sleep 30", directory, [], StartBudget)

        helper.Shutdown()

        Assert.True(isGone child.Pid, "the helper must kill a child the daemon left behind")
        Assert.True(SpinWait.SpinUntil((fun () -> helper.Connection.IsLost), 10000))

        Assert.Throws<SpawnHelperException>(fun () ->
            helper.Connection.Start("/bin/sh", shell "exit 0", directory, [], StartBudget)
            |> ignore)
        |> ignore)

// ---------------------------------------------------------------------------
// A helper that misbehaves
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a helper that disappears mid-run leaves its child recorded as a leak, never as reaped`` () =
    use helper = new ScriptedHelper()
    use _ = SpawnHelper.install helper.Connection
    let registry = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install registry
    let fakePid = 424242

    let run =
        Task.Run(fun () -> runProcessObserved ignore "/bin/sh" (shell "sleep 30") "/" [] tenSeconds)

    helper.Send(Event.Started(startId (helper.NextRequest()), fakePid))
    helper.Vanish()

    let failure =
        Assert.ThrowsAny<exn>(fun () -> run.GetAwaiter().GetResult() |> ignore)

    Assert.IsType<SpawnHelperException>(failure) |> ignore
    Assert.Contains(fakePid, registry.Leaks |> List.map _.Pid)

[<Fact(Timeout = 30000)>]
let ``a lost helper leaves its child unobservable and unkillable, and fails a pending kill`` () =
    use helper = new ScriptedHelper()

    let starting =
        Task.Run(fun () -> helper.Connection.Start("/bin/sh", "", "/", [ "KEY", "value" ], StartBudget))

    let request = helper.NextRequest()

    match request with
    | Some(Request.Start(_, "/bin/sh", "", "/", [ "KEY", "value" ])) -> ()
    | other -> failwith $"unexpected request %A{other}"

    helper.Send(Event.Started(startId request, 1))
    let child = starting.Result
    let killing = Task.Run(fun () -> child.KillTree())

    match helper.NextRequest() with
    | Some(Request.Kill(id, _)) -> Assert.Equal(child.Id, id)
    | other -> failwith $"expected a kill request, got %A{other}"

    helper.Vanish()
    helper.WaitUntilLost()

    Assert.ThrowsAny<exn>(fun () -> killing.Wait()) |> ignore
    Assert.IsType<SpawnHelperException>(killing.Exception.InnerException) |> ignore

    Assert.Throws<SpawnHelperException>(fun () -> child.HasExited |> ignore)
    |> ignore

    Assert.False(child.WaitForExit 0)
    Assert.Throws<SpawnHelperException>(fun () -> child.KillTree()) |> ignore

    match (child :> ProcessRegistry.IOwnedChild).Observe() with
    | ProcessRegistry.ExitObservation.Unobservable reason -> Assert.IsType<SpawnHelperException>(reason) |> ignore
    | other -> failwith $"expected Unobservable, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``output received before a reader attaches is replayed in order, and a broken stream is not a drain`` () =
    use helper = new ScriptedHelper()

    let starting =
        Task.Run(fun () -> helper.Connection.Start("/bin/sh", "", "/", [], StartBudget))

    let id = startId (helper.NextRequest())
    helper.Send(Event.Started(id, 1))
    let child = starting.Result
    let owned = child :> ProcessRegistry.IOwnedChild

    match owned.Observe() with
    | ProcessRegistry.ExitObservation.Running -> ()
    | other -> failwith $"expected Running, got %A{other}"

    Assert.False(child.HasExited)
    helper.Send(Event.Output(id, "first "))
    helper.Send(Event.Exited(id, 0))
    // Events are dispatched in order, so the exit being seen means the output was too.
    owned.WaitForExit 10000
    Assert.Equal(1, owned.Pid)

    let seen = Collections.Generic.List<string>()
    let drained = child.Attach seen.Add
    helper.Send(Event.Output(id, "second"))
    helper.Send(Event.Eof(id, false))
    helper.Send(Event.Eof(id, true))

    Assert.False(drained.Result, "a stream that stopped short is not a complete capture")
    Assert.Equal<string list>([ "first "; "second" ], List.ofSeq seen)

[<Fact(Timeout = 30000)>]
let ``events for children the connection does not hold are ignored`` () =
    use helper = new ScriptedHelper()

    let starting =
        Task.Run(fun () -> helper.Connection.Start("/missing", "", "/", [], StartBudget))

    let id = startId (helper.NextRequest())
    let stranger = id + 100L
    helper.Send(Event.Started(stranger, 1))
    helper.Send(Event.Output(stranger, "noise"))
    helper.Send(Event.Eof(stranger, true))
    helper.Send(Event.Exited(stranger, 0))
    helper.Send(Event.KillDone(stranger, None))

    helper.Send(
        Event.StartFailed(
            stranger,
            { NativeErrorCode = None
              TypeName = "X"
              Message = "x" }
        )
    )

    helper.Send(
        Event.StartFailed(
            id,
            { NativeErrorCode = Some 2
              TypeName = nameof Win32Exception
              Message = "No such file or directory" }
        )
    )

    let failure =
        Assert.ThrowsAny<exn>(fun () -> starting.GetAwaiter().GetResult() |> ignore)

    Assert.Equal(2, (failure :?> Win32Exception).NativeErrorCode)
    Assert.False(helper.Connection.IsLost)

[<Fact(Timeout = 30000)>]
let ``a start the helper does not answer in time fails, and the late child is killed and released`` () =
    use helper = new ScriptedHelper()

    let failure =
        Assert.Throws<SpawnHelperException>(fun () ->
            helper.Connection.Start("/bin/sh", "", "/", [], TimeSpan.FromMilliseconds 50.0)
            |> ignore)

    Assert.Contains("did not start", failure.Message)

    let id = startId (helper.NextRequest())
    helper.Send(Event.Started(id, 1))

    match helper.NextRequest(), helper.NextRequest() with
    | Some(Request.Kill(killed, _)), Some(Request.Release released) ->
        Assert.Equal(id, killed)
        Assert.Equal(id, released)
    | other -> failwith $"expected the late child to be killed and released, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``a helper that stops reading is lost at the next request`` () =
    use helper = new ScriptedHelper()
    helper.StopReading()

    let failure =
        Assert.Throws<SpawnHelperException>(fun () ->
            helper.Connection.Start("/bin/sh", "", "/", [], StartBudget) |> ignore)

    Assert.Contains("writing to the spawn helper failed", failure.Message)
    Assert.True(helper.Connection.IsLost)

    Assert.Throws<SpawnHelperException>(fun () -> helper.Connection.Kill 1L |> ignore)
    |> ignore

[<Fact(Timeout = 30000)>]
let ``a helper that writes garbage is lost`` () =
    use helper = new ScriptedHelper()
    helper.SendRaw [| 99uy |]
    helper.WaitUntilLost()

// ---------------------------------------------------------------------------
// The wire format and the failure mapping
// ---------------------------------------------------------------------------

let private roundTrip (write: BinaryWriter -> 'a -> unit) (read: BinaryReader -> 'a option) (value: 'a) =
    use stream = new MemoryStream()
    write (new BinaryWriter(stream)) value
    stream.Position <- 0L
    read (new BinaryReader(stream))

let private sampleFailure =
    { NativeErrorCode = Some 13
      TypeName = "Win32Exception"
      Message = "denied" }

[<Fact>]
let ``every request survives the wire`` () =
    for request in
        [ Request.Start(1L, "cmd", "a b", "/w", [ "K", "V"; "L", "" ])
          Request.Kill(2L, 3L)
          Request.Release 4L ] do
        Assert.Equal(sprintf "%A" (Some request), sprintf "%A" (roundTrip writeRequest readRequest request))

[<Fact>]
let ``every event survives the wire`` () =
    for event in
        [ Event.Started(1L, 42)
          Event.StartFailed(2L, sampleFailure)
          Event.StartFailed(
              3L,
              { sampleFailure with
                  NativeErrorCode = None }
          )
          Event.Output(4L, "text")
          Event.Eof(5L, true)
          Event.Exited(6L, -1)
          Event.KillDone(7L, None)
          Event.KillDone(8L, Some sampleFailure) ] do
        Assert.Equal(sprintf "%A" (Some event), sprintf "%A" (roundTrip writeEvent readEvent event))

[<Fact>]
let ``an empty stream is the end, an unknown tag and a truncated message are errors`` () =
    use empty = new MemoryStream()
    Assert.True((readRequest (new BinaryReader(empty))).IsNone)
    Assert.True((readEvent (new BinaryReader(new MemoryStream()))).IsNone)

    Assert.Throws<InvalidDataException>(fun () -> readRequest (new BinaryReader(new MemoryStream([| 9uy |]))) |> ignore)
    |> ignore

    Assert.Throws<InvalidDataException>(fun () -> readEvent (new BinaryReader(new MemoryStream([| 9uy |]))) |> ignore)
    |> ignore

    Assert.Throws<EndOfStreamException>(fun () -> readRequest (new BinaryReader(new MemoryStream([| 2uy |]))) |> ignore)
    |> ignore

[<Fact>]
let ``a failure is raised again as the exception type callers classify by`` () =
    let win32 = HelperFailure.ofException (Win32Exception(2, "missing"))
    Assert.Equal(Some 2, win32.NativeErrorCode)
    Assert.Equal(2, (HelperFailure.toException win32 :?> Win32Exception).NativeErrorCode)

    let exited = HelperFailure.ofException (InvalidOperationException "already exited")
    Assert.Equal(None, exited.NativeErrorCode)

    Assert.IsType<InvalidOperationException>(HelperFailure.toException exited)
    |> ignore

    let other =
        HelperFailure.toException (HelperFailure.ofException (ArgumentException "odd"))

    Assert.IsType<SpawnHelperException>(other) |> ignore
    Assert.Contains("ArgumentException", other.Message)

[<Fact>]
let ``attempt reports what the work raised`` () =
    Assert.True((attempt ignore).IsNone)

    match attempt (fun () -> failwith "boom") with
    | Some ex -> Assert.Equal("boom", ex.Message)
    | None -> failwith "the failure must be reported"

[<Fact(Timeout = 30000)>]
let ``a tracked child that has exited is not listed as live`` () =
    let registry = ProcessRegistry.Registry()

    use exited =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("/bin/sh", shell "exit 0", UseShellExecute = false))

    Assert.True(exited.WaitForExit 10000, "the fixture child must exit")
    registry.Track exited
    Assert.Empty(registry.LivePids())
    Assert.Empty(registry.Snapshot())
    registry.Untrack exited
