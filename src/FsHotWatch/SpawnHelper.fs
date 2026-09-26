/// A spawn helper: a small, long-lived process that starts children on the daemon's
/// behalf, so that the daemon, once its heap has grown to gigabytes, never has to fork.
///
/// Every `Process.Start` forks the calling process. Forking a multi-gigabyte process
/// takes longer than forking a small one (the page tables are copied), and while it is
/// in progress the runtime may be suspending threads for a garbage collection. On
/// macOS 27 that overlap coincides with the runtime's thread-suspend signal arriving at
/// a null handler, which kills the daemon. The helper is started while the daemon is
/// still small; afterwards the daemon asks it, over a pair of pipes, to start, kill and
/// release children, and the helper streams each child's output and exit back.
///
/// The daemon keeps every ownership duty: each helper child is admitted to and
/// untracked from the caller's `ProcessRegistry` scope exactly like a local child
/// (`HelperChild` is an `IOwnedChild`), and its tree is killed through the helper.
/// A helper that stops answering makes each of its children unobservable, never
/// "exited": the registry then records them as leaks instead of claiming a reap.
///
/// When the daemon closes its end of the request pipe (it exited, or it crashed), the
/// helper kills every child it still holds and exits, so a dead daemon's children do
/// not run on unowned.
module FsHotWatch.SpawnHelper

open System
open System.Collections.Concurrent
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

/// The helper could not do what it was asked, or stopped answering.
///
/// Deliberately not an `InvalidOperationException`: the kill path reads that type as
/// "the child had already exited", and a child behind a lost helper has not been shown
/// to have exited.
type SpawnHelperException(message: string) =
    inherit Exception(message)

/// Run `work`, returning the exception it raised, if any.
let internal attempt (work: unit -> unit) : exn option =
    try
        work ()
        None
    with ex ->
        Some ex

/// What went wrong on the helper's side, carried over the pipe so the daemon can raise
/// the exception it would have caught had it made the call itself.
[<NoComparison>]
type internal HelperFailure =
    {
        /// `Win32Exception.NativeErrorCode`, when the failure was one.
        NativeErrorCode: int option
        TypeName: string
        Message: string
    }

[<RequireQualifiedAccess>]
module internal HelperFailure =
    let ofException (ex: exn) : HelperFailure =
        { NativeErrorCode =
            match ex with
            | :? Win32Exception as win32 -> Some win32.NativeErrorCode
            | _ -> None
          TypeName = ex.GetType().Name
          Message = ex.Message }

    /// The exception the daemon raises for `failure`: a `Win32Exception` (a command
    /// that could not start, a kill the OS refused) and an `InvalidOperationException`
    /// (a kill racing the child's own exit) keep their types, because callers classify
    /// by type; anything else is a `SpawnHelperException` naming the original.
    let toException (failure: HelperFailure) : exn =
        match failure.NativeErrorCode with
        | Some code -> Win32Exception(code, failure.Message)
        | None when failure.TypeName = nameof InvalidOperationException -> InvalidOperationException(failure.Message)
        | None -> SpawnHelperException $"%s{failure.TypeName}: %s{failure.Message}"

/// Daemon to helper.
[<RequireQualifiedAccess; NoComparison>]
type internal Request =
    /// Start a child. `env` is the child's complete environment.
    | Start of id: int64 * fileName: string * arguments: string * workDir: string * env: (string * string) list
    /// Kill child `id`'s process tree; the reply is `KillDone killId`.
    | Kill of id: int64 * killId: int64
    /// The daemon is done with child `id`: forget it.
    | Release of id: int64

/// Helper to daemon.
[<RequireQualifiedAccess; NoComparison>]
type internal Event =
    | Started of id: int64 * pid: int
    | StartFailed of id: int64 * failure: HelperFailure
    /// A chunk from the child's stdout or stderr, in the order the helper read it.
    | Output of id: int64 * text: string
    /// One of the child's two streams stopped: at end of stream, or not.
    | Eof of id: int64 * reachedEof: bool
    | Exited of id: int64 * exitCode: int
    | KillDone of killId: int64 * failure: HelperFailure option

let private writeFailure (writer: BinaryWriter) (failure: HelperFailure) =
    writer.Write(Option.isSome failure.NativeErrorCode)
    writer.Write(Option.defaultValue 0 failure.NativeErrorCode)
    writer.Write failure.TypeName
    writer.Write failure.Message

let private readFailure (reader: BinaryReader) : HelperFailure =
    let hasCode = reader.ReadBoolean()
    let code = reader.ReadInt32()

    { NativeErrorCode = if hasCode then Some code else None
      TypeName = reader.ReadString()
      Message = reader.ReadString() }

/// The tag that opens every message; -1 at a clean end of stream. `BinaryReader` reads
/// no further ahead than each value it is asked for, so reading the tag from the
/// underlying stream leaves the reader in step.
let private readTag (reader: BinaryReader) : int = reader.BaseStream.ReadByte()

let private unknownTag (tag: int) : 'a =
    raise (InvalidDataException $"unknown spawn helper message tag %d{tag}")

let internal writeRequest (writer: BinaryWriter) (request: Request) =
    match request with
    | Request.Start(id, fileName, arguments, workDir, env) ->
        writer.Write 1uy
        writer.Write id
        writer.Write fileName
        writer.Write arguments
        writer.Write workDir
        writer.Write(List.length env)

        for key, value in env do
            writer.Write key
            writer.Write value
    | Request.Kill(id, killId) ->
        writer.Write 2uy
        writer.Write id
        writer.Write killId
    | Request.Release id ->
        writer.Write 3uy
        writer.Write id

    writer.Flush()

/// The next request, or `None` at a clean end of stream. A stream that ends inside a
/// message raises `EndOfStreamException`.
let internal readRequest (reader: BinaryReader) : Request option =
    match readTag reader with
    | -1 -> None
    | 1 ->
        let id = reader.ReadInt64()
        let fileName = reader.ReadString()
        let arguments = reader.ReadString()
        let workDir = reader.ReadString()

        let env =
            List.init (reader.ReadInt32()) (fun _ ->
                let key = reader.ReadString()
                key, reader.ReadString())

        Some(Request.Start(id, fileName, arguments, workDir, env))
    | 2 ->
        let id = reader.ReadInt64()
        Some(Request.Kill(id, reader.ReadInt64()))
    | 3 -> Some(Request.Release(reader.ReadInt64()))
    | tag -> unknownTag tag

let internal writeEvent (writer: BinaryWriter) (event: Event) =
    match event with
    | Event.Started(id, pid) ->
        writer.Write 1uy
        writer.Write id
        writer.Write pid
    | Event.StartFailed(id, failure) ->
        writer.Write 2uy
        writer.Write id
        writeFailure writer failure
    | Event.Output(id, text) ->
        writer.Write 3uy
        writer.Write id
        writer.Write text
    | Event.Eof(id, reachedEof) ->
        writer.Write 4uy
        writer.Write id
        writer.Write reachedEof
    | Event.Exited(id, exitCode) ->
        writer.Write 5uy
        writer.Write id
        writer.Write exitCode
    | Event.KillDone(killId, failure) ->
        writer.Write 6uy
        writer.Write killId
        writer.Write(Option.isSome failure)
        failure |> Option.iter (writeFailure writer)

    writer.Flush()

/// The next event, or `None` at a clean end of stream.
let internal readEvent (reader: BinaryReader) : Event option =
    match readTag reader with
    | -1 -> None
    | 1 ->
        let id = reader.ReadInt64()
        Some(Event.Started(id, reader.ReadInt32()))
    | 2 ->
        let id = reader.ReadInt64()
        Some(Event.StartFailed(id, readFailure reader))
    | 3 ->
        let id = reader.ReadInt64()
        Some(Event.Output(id, reader.ReadString()))
    | 4 ->
        let id = reader.ReadInt64()
        Some(Event.Eof(id, reader.ReadBoolean()))
    | 5 ->
        let id = reader.ReadInt64()
        Some(Event.Exited(id, reader.ReadInt32()))
    | 6 ->
        let killId = reader.ReadInt64()

        let failure =
            if reader.ReadBoolean() then
                Some(readFailure reader)
            else
                None

        Some(Event.KillDone(killId, failure))
    | tag -> unknownTag tag

// ---------------------------------------------------------------------------
// The helper's side.
// ---------------------------------------------------------------------------

/// Run `work` on a dedicated thread: a pump blocks for its child's whole life.
let private background (work: unit -> unit) : Task =
    Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)

/// Serve requests from `input` until it ends, writing events to `output`; then kill
/// every child still held and return. This is the whole of the helper process.
///
/// A child's `Exited` is sent the moment it exits, independent of its streams: a
/// grandchild that inherited the stdout pipe can hold it open long after, and the
/// daemon bounds that wait itself. The `Process` is disposed only once the child has
/// exited AND both pumps have stopped: disposing it closes the streams, which would cut
/// short a pump still reading the child's last output.
let internal serve (input: Stream) (output: Stream) : unit =
    let reader = new BinaryReader(input)
    let writer = new BinaryWriter(output)
    let gate = obj ()
    let children = ConcurrentDictionary<int64, Process>()

    // A daemon that has gone away cannot be told anything; the end of `input` then
    // ends this loop.
    let send (event: Event) =
        lock gate (fun () -> attempt (fun () -> writeEvent writer event) |> ignore)

    let pump (id: int64) (stream: StreamReader) () =
        let buffer = Array.zeroCreate<char> 4096

        let failure =
            attempt (fun () ->
                let mutable read = stream.Read(buffer, 0, buffer.Length)

                while read > 0 do
                    send (Event.Output(id, String(buffer, 0, read)))
                    read <- stream.Read(buffer, 0, buffer.Length))

        send (Event.Eof(id, Option.isNone failure))

    let start (id: int64) (fileName: string) (arguments: string) (workDir: string) (env: (string * string) list) =
        let psi =
            ProcessStartInfo(
                fileName,
                arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workDir
            )

        psi.Environment.Clear()

        for key, value in env do
            psi.Environment[key] <- value

        // FSHW-SPAWN-001 ok: this IS the spawn the daemon delegates to the helper. The
        // daemon admits the child to its process scope when `Started` arrives, and the
        // helper kills whatever it still holds when the daemon goes away.
        match attempt (fun () -> children[id] <- Process.Start psi) with
        | Some failure -> send (Event.StartFailed(id, HelperFailure.ofException failure))
        | None ->
            let child = children[id]
            send (Event.Started(id, child.Id))

            let exited =
                child.WaitForExitAsync().ContinueWith(fun (_: Task) -> send (Event.Exited(id, child.ExitCode)))

            Task
                .WhenAll(background (pump id child.StandardOutput), background (pump id child.StandardError), exited)
                .ContinueWith(fun (_: Task) -> child.Dispose())
            |> ignore

    let kill id killId =
        background (fun () ->
            let failure =
                match children.TryGetValue id with
                | true, child -> attempt (fun () -> child.Kill(entireProcessTree = true))
                | _ -> Some(InvalidOperationException $"child %d{id} was released")

            send (Event.KillDone(killId, failure |> Option.map HelperFailure.ofException)))
        |> ignore

    let rec loop () =
        match readRequest reader with
        | Some(Request.Start(id, fileName, arguments, workDir, env)) ->
            start id fileName arguments workDir env
            loop ()
        | Some(Request.Kill(id, killId)) ->
            kill id killId
            loop ()
        | Some(Request.Release id) ->
            children.TryRemove id |> ignore
            loop ()
        | None -> ()

    loop ()

    children.Values
    |> Seq.iter (fun child -> attempt (fun () -> child.Kill(entireProcessTree = true)) |> ignore)

// ---------------------------------------------------------------------------
// The daemon's side.
// ---------------------------------------------------------------------------

/// One item of a child's output stream, delivered in the order it arrived.
[<RequireQualifiedAccess>]
type private Delivery =
    | Chunk of text: string
    /// One of the child's two streams stopped.
    | Stopped of reachedEof: bool

/// A child the helper started for us. It is its own registry key and owned view, so
/// `ProcessRegistry` scopes admit, kill and untrack it like a local `Process`.
///
/// Output is queued per child and delivered to the attached reader by one run at a
/// time on a `DeadlineWorkers` thread, never on the connection's reader thread. A
/// reader that blocks (a sink writing to a stalled disk) therefore holds up only its
/// own child. The queue is unbounded: the in-memory capture holds the same text anyway,
/// and a bound would have to either block the reader thread or drop output. A stream's
/// stop is queued behind its output, so `Attach`'s task completes only after every
/// chunk before it has been delivered. Output that arrives before a reader attaches
/// waits in the queue.
type internal HelperChild(id: int64, kill: int64 -> HelperFailure option, release: int64 -> unit) =
    let gate = obj ()

    let started =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let drained =
        TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

    // Set once the child exits or the helper is lost; `exitCode` tells the two apart.
    let settled = new ManualResetEventSlim(false)
    let mutable exitCode: int option = None
    let mutable lost: string option = None
    // Set when the caller stops waiting for the start; see `Abandon`.
    let mutable abandoned = false
    let mutable streamsStopped = 0
    let mutable bothReachedEof = true
    let pending = Collections.Generic.Queue<Delivery>()
    // True while a delivery run owns the queue.
    let mutable delivering = false
    let mutable reader: (string -> unit) option = None

    let stopped (reachedEof: bool) =
        let both =
            lock gate (fun () ->
                streamsStopped <- streamsStopped + 1
                bothReachedEof <- bothReachedEof && reachedEof
                streamsStopped = 2)

        if both then
            drained.TrySetResult bothReachedEof |> ignore

    // A reader that throws loses that chunk only; the run goes on to the next one.
    let rec deliverAll (read: string -> unit) =
        let next =
            lock gate (fun () ->
                if pending.Count = 0 then
                    delivering <- false
                    None
                else
                    Some(pending.Dequeue()))

        match next with
        | Some(Delivery.Chunk text) ->
            attempt (fun () -> read text) |> ignore
            deliverAll read
        | Some(Delivery.Stopped reachedEof) ->
            stopped reachedEof
            deliverAll read
        | None -> ()

    // Called under `gate`: the reader a new delivery run must start with, when one is
    // attached, something is queued and no run is going.
    let claimRun () =
        match reader with
        | Some read when not delivering && pending.Count > 0 ->
            delivering <- true
            Some read
        | _ -> None

    let startRun (read: (string -> unit) option) =
        read
        |> Option.iter (fun read ->
            DeadlineWorkers.shared.Post(fun () ->
                deliverAll read
                ignore))

    let enqueue (item: Delivery) =
        lock gate (fun () ->
            pending.Enqueue item
            claimRun ())
        |> startRun

    member _.Id = id

    /// Completes with the pid once the helper has started the child.
    member _.Started: Task<int> = started.Task

    member _.Pid: int = started.Task.Result

    /// Record the pid. False when the caller had already given up waiting: nobody owns
    /// the child, and the connection kills and releases it.
    member internal _.OnStarted(pid: int) : bool =
        lock gate (fun () ->
            if not abandoned then
                started.TrySetResult pid |> ignore

            not abandoned)

    /// The caller stops waiting for the start. True when the child had started anyway,
    /// in which case the caller keeps it. Decided under the same lock as `OnStarted`, so
    /// a child is either handed to the caller or abandoned, never neither.
    member internal _.Abandon() : bool =
        lock gate (fun () ->
            abandoned <- true
            started.Task.IsCompletedSuccessfully)

    member internal _.OnStartFailed(failure: HelperFailure) =
        started.TrySetException(HelperFailure.toException failure) |> ignore

    member internal _.OnOutput(text: string) = enqueue (Delivery.Chunk text)

    member internal _.OnEof(reachedEof: bool) = enqueue (Delivery.Stopped reachedEof)

    member internal _.OnExited(code: int) =
        lock gate (fun () -> exitCode <- Some code)
        settled.Set()

    /// The helper is gone: the child can no longer be observed, killed or read.
    member internal _.OnLost(reason: string) =
        lock gate (fun () -> lost <- Some reason)
        started.TrySetException(SpawnHelperException reason) |> ignore
        drained.TrySetResult false |> ignore
        settled.Set()

    /// Deliver output to `read`, starting with anything already received. The task
    /// completes once both streams have stopped and everything before the stops has
    /// been delivered: `true` only if both reached their end.
    member _.Attach(read: string -> unit) : Task<bool> =
        lock gate (fun () ->
            reader <- Some read
            claimRun ())
        |> startRun

        drained.Task

    /// Whether the child has exited. Raises `SpawnHelperException` once the helper is
    /// lost: whether the child is still running is then unknown.
    member _.HasExited: bool =
        match lock gate (fun () -> exitCode, lost) with
        | Some _, _ -> true
        | None, Some reason -> raise (SpawnHelperException reason)
        | None, None -> false

    member _.ExitCode: int = lock gate (fun () -> exitCode.Value)

    /// Wait up to `milliseconds` for the child to exit; true once it has. Returns early
    /// when the helper is lost.
    member _.WaitForExit(milliseconds: int) : bool =
        settled.Wait milliseconds |> ignore
        lock gate (fun () -> exitCode.IsSome)

    /// Kill the child's tree through the helper, raising what `Process.Kill` would.
    member _.KillTree() =
        kill id |> Option.iter (HelperFailure.toException >> raise)

    member _.Dispose() = release id

    interface ProcessRegistry.IOwnedChild with
        member this.Pid = this.Pid

        member _.Observe() =
            match lock gate (fun () -> exitCode, lost) with
            | Some _, _ -> ProcessRegistry.ExitObservation.Exited
            | None, Some reason -> ProcessRegistry.ExitObservation.Unobservable(SpawnHelperException reason)
            | None, None -> ProcessRegistry.ExitObservation.Running

        member this.KillTree() = this.KillTree()
        member this.WaitForExit milliseconds = this.WaitForExit milliseconds |> ignore

/// How long a start request may wait for the helper's answer. A small process starts a
/// child in milliseconds; a helper that has not answered in this long is not coming
/// back. The child, if it starts later, is killed and released on arrival.
let internal StartBudget = TimeSpan.FromSeconds 30.0

/// The daemon's end of the pipes to one helper. One reader thread, started here and
/// living as long as the helper answers, dispatches every event.
type internal Connection(toHelper: Stream, fromHelper: Stream) =
    let writer = new BinaryWriter(toHelper)
    let gate = obj ()
    let children = ConcurrentDictionary<int64, HelperChild>()

    let kills =
        ConcurrentDictionary<int64, TaskCompletionSource<HelperFailure option>>()

    let mutable nextId = 0L
    let mutable lost: string option = None

    // Every child and every pending kill hears about the loss. A request made after
    // this point is refused by `send`, which checks under the same lock.
    let markLost (reason: string) =
        let first =
            lock gate (fun () ->
                let first = lost.IsNone

                if first then
                    lost <- Some reason

                first)

        if first then
            Logging.warn
                "spawn-helper"
                $"the spawn helper is gone (%s{reason}). Its running children are recorded as leaks, and every \
                  later spawn starts directly from this process; no new helper is started."

        children.Values |> Seq.iter (fun child -> child.OnLost reason)

        kills.Values
        |> Seq.iter (fun reply -> reply.TrySetException(SpawnHelperException reason) |> ignore)

    let send (request: Request) =
        lock gate (fun () ->
            match lost with
            | Some reason -> raise (SpawnHelperException reason)
            | None ->
                match attempt (fun () -> writeRequest writer request) with
                | None -> ()
                | Some failure ->
                    let reason = $"writing to the spawn helper failed: %s{failure.Message}"
                    markLost reason
                    raise (SpawnHelperException reason))

    let withChild (id: int64) (act: HelperChild -> unit) =
        match children.TryGetValue id with
        | true, child -> act child
        | _ -> ()

    // A start the caller stopped waiting for: nobody owns this child, so it goes.
    let abandon (id: int64) =
        children.TryRemove id |> ignore

        attempt (fun () ->
            send (Request.Kill(id, Interlocked.Increment &nextId))
            send (Request.Release id))
        |> ignore

    let dispatch (event: Event) =
        match event with
        | Event.Started(id, pid) ->
            withChild id (fun child ->
                if not (child.OnStarted pid) then
                    abandon id)
        | Event.StartFailed(id, failure) ->
            match children.TryRemove id with
            | true, child -> child.OnStartFailed failure
            | _ -> ()
        | Event.Output(id, text) -> withChild id (fun child -> child.OnOutput text)
        | Event.Eof(id, reachedEof) -> withChild id (fun child -> child.OnEof reachedEof)
        | Event.Exited(id, exitCode) -> withChild id (fun child -> child.OnExited exitCode)
        | Event.KillDone(killId, failure) ->
            match kills.TryRemove killId with
            | true, reply -> reply.TrySetResult failure |> ignore
            | _ -> ()

    let readLoop () =
        let reader = new BinaryReader(fromHelper)

        let rec loop () =
            let mutable next = None

            match attempt (fun () -> next <- readEvent reader) with
            | Some failure -> markLost $"reading from the spawn helper failed: %s{failure.Message}"
            | None ->
                match next with
                | Some event ->
                    dispatch event
                    loop ()
                | None -> markLost "the spawn helper closed its output"

        loop ()

    do Thread(readLoop, IsBackground = true, Name = "fshw-spawn-helper-reader").Start()

    /// Whether the helper has stopped answering.
    member _.IsLost: bool = lock gate (fun () -> lost.IsSome)

    /// Ask the helper to start a child and wait, up to `budget`, for its pid. Raises
    /// the start failure as the exception `Process.Start` would have raised.
    member this.Start
        (fileName: string, arguments: string, workDir: string, env: (string * string) list, budget: TimeSpan)
        : HelperChild =
        let id = Interlocked.Increment &nextId
        let child = HelperChild(id, this.Kill, this.Release)
        children[id] <- child
        send (Request.Start(id, fileName, arguments, workDir, env))
        Task.WaitAny([| child.Started :> Task |], budget) |> ignore

        if child.Abandon() then
            child
        else
            match child.Started.Exception with
            | null ->
                raise (
                    SpawnHelperException
                        $"the spawn helper did not start `%s{fileName}` within %d{int budget.TotalSeconds}s"
                )
            | failure -> raise failure.InnerException

    /// Kill child `id`'s tree and wait for the helper's answer: `None` when the kill
    /// returned, the failure otherwise. Raises `SpawnHelperException` if the helper is
    /// lost first. Unbounded here: callers bound it (`ProcessHelper.killTreeWith`).
    member _.Kill(id: int64) : HelperFailure option =
        let killId = Interlocked.Increment &nextId

        let reply =
            TaskCompletionSource<HelperFailure option>(TaskCreationOptions.RunContinuationsAsynchronously)

        kills[killId] <- reply
        send (Request.Kill(id, killId))
        reply.Task.GetAwaiter().GetResult()

    /// Forget child `id`, here and in the helper. A lost helper has nothing to release.
    member _.Release(id: int64) =
        children.TryRemove id |> ignore
        attempt (fun () -> send (Request.Release id)) |> ignore

    /// Close the request pipe. The helper kills every child it still holds and exits.
    interface IDisposable with
        member _.Dispose() = toHelper.Dispose()

let private installed = AsyncLocal<Connection>()

/// Route eligible spawns in the current scope through `connection`. Returns an
/// IDisposable that restores the prior routing. Like `ProcessRegistry.install`, the
/// setting flows to work started from this context.
let internal install (connection: Connection) : IDisposable =
    let prior = installed.Value
    installed.Value <- connection

    { new IDisposable with
        member _.Dispose() = installed.Value <- prior }

/// The helper eligible spawns in this scope go through, if one is installed.
let internal current () : Connection option =
    let connection = installed.Value
    if isNull (box connection) then None else Some connection
