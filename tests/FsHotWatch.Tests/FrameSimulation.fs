/// Seeded histories of the repository endpoint's frames (`RepositoryIpc.writeFrame`,
/// `readFrame`, `drainUnread`) over scripted streams on the deterministic runtime
/// (AUTOMATION-1695).
///
/// The frame layer touches no pool and no timer: its every await waits on the stream, so the
/// stream is its one timing source. A scripted stream delivers its bytes in chunks of a seeded
/// size, and for each read or write decides from the seed whether it completes inside the call
/// (the await's already-complete arm) or later, from the simulation's queue (the suspend arm).
/// It can end early or fail with an `IOException` after any byte. Each history is a few
/// exchanges, each a frame written to one scripted stream and read back from another holding
/// what was written (or a header claiming too many bytes), cut short or failed as the seed says.
/// After each:
///
///   * ROUND TRIP: a frame read whole is the text written, whatever the chunking and timing.
///   * ENDING: a stream that ends before a whole frame is `Truncated`; one whose read fails is
///     `HungUp`; a header outside 0..MaxFrameBytes is `TooLarge` with its length, its payload
///     unread; a write that fails is `HungUp`. Never an exception.
///   * SETTLEMENT: every read, write and drain completes (no hang), and a drain reads to the end.
///   * RUNTIME: no deadlock, no escape.
module FsHotWatch.Tests.FrameSimulation

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FsHotWatch
open FsHotWatch.Tests.SimRuntime

/// How a scripted stream's reads end.
[<RequireQualifiedAccess>]
type Ending =
    | Clean
    /// The stream ends after this many bytes.
    | EndsAfter of bytes: int
    /// A read fails with an `IOException` once this many bytes have been read.
    | FailsAfter of bytes: int

/// What one exchange writes, and how its streams behave.
[<RequireQualifiedAccess>]
type Exchange =
    /// Write `text`, then read the written bytes back through a stream ending as `ending`.
    | Frame of text: string * ending: Ending * writeFailsAfter: int option
    /// Read a header claiming `length` bytes (outside the allowed range), then drain the rest.
    | Oversized of length: int

[<RequireQualifiedAccess>]
type Mutant =
    /// A reader that takes one read for the header and one for the payload (the AUTOMATION-1681
    /// shape: a payload that arrives in pieces is cut short).
    | OneReadPerPart
    /// A reader that answers a payload cut short with what it read.
    | PartialPayloadAsWhole
    /// Data promised later that never arrives: a lost wakeup in the stream.
    | LostCompletion

[<RequireQualifiedAccess>]
type Subject =
    | Current
    | Mutated of Mutant

[<RequireQualifiedAccess>]
type Theorem =
    | RoundTrip
    | Ending
    | Settlement
    | Runtime

type Violation =
    { Seed: int
      Step: int
      Theorem: Theorem
      Detail: string
      History: Exchange list
      Choices: Choice list }

/// Both arms of each frame-layer await, reached by the seed.
type Census =
    {
        ReadsAtOnce: int
        ReadsLater: int
        /// Frames whose every payload read completed inside its call: readFrame's payload
        /// await finds it complete.
        PayloadsAtOnce: int
        /// Frames with a payload read that completed later: that await suspends.
        PayloadsLater: int
        WritesAtOnce: int
        WritesLater: int
    }

module Census =
    let zero =
        { ReadsAtOnce = 0
          ReadsLater = 0
          PayloadsAtOnce = 0
          PayloadsLater = 0
          WritesAtOnce = 0
          WritesLater = 0 }

    let add (a: Census) (b: Census) =
        { ReadsAtOnce = a.ReadsAtOnce + b.ReadsAtOnce
          ReadsLater = a.ReadsLater + b.ReadsLater
          PayloadsAtOnce = a.PayloadsAtOnce + b.PayloadsAtOnce
          PayloadsLater = a.PayloadsLater + b.PayloadsLater
          WritesAtOnce = a.WritesAtOnce + b.WritesAtOnce
          WritesLater = a.WritesLater + b.WritesLater }

[<Literal>]
let HistoryLength = 5

let private ending (e: Ending) =
    match e with
    | Ending.Clean -> "whole"
    | Ending.EndsAfter n -> $"ends after %d{n} bytes"
    | Ending.FailsAfter n -> $"fails after %d{n} bytes"

let exchangeText (x: Exchange) : string =
    match x with
    | Exchange.Frame(text, e, writeFails) ->
        let writes =
            match writeFails with
            | Some n -> $", its write failing after %d{n} bytes"
            | None -> ""

        $"a %d{Encoding.UTF8.GetByteCount text}-byte frame, read %s{ending e}%s{writes}"
    | Exchange.Oversized length -> $"a header claiming %d{length} bytes"

/// The stream's behaviour, drawn once per history.
type private Script = { MaxChunk: int; ReadyShare: float }

let generate (seed: int) (length: int) : Exchange list =
    let rng = Random seed

    let text () =
        let roll = rng.NextDouble()

        let size =
            if roll < 0.15 then 0
            elif roll < 0.75 then 1 + rng.Next 64
            elif roll < 0.98 then 1 + rng.Next 8192
            else RepositoryIpc.MaxFrameBytes / 4 - rng.Next 64

        // Mostly ASCII, with multi-byte characters so byte and character counts differ.
        String(
            Array.init size (fun _ ->
                if rng.NextDouble() < 0.9 then
                    char (97 + rng.Next 26)
                else
                    'é')
        )

    [ for _ in 1..length do
          if rng.NextDouble() < 0.1 then
              Exchange.Oversized(
                  if rng.NextDouble() < 0.5 then
                      RepositoryIpc.MaxFrameBytes + 1 + rng.Next 1000
                  else
                      -(1 + rng.Next 1000)
              )
          else
              let t = text ()
              let total = 4 + Encoding.UTF8.GetByteCount t
              let roll = rng.NextDouble()

              let e =
                  if roll < 0.6 then Ending.Clean
                  elif roll < 0.8 then Ending.EndsAfter(rng.Next total)
                  else Ending.FailsAfter(rng.Next total)

              let writeFails =
                  if rng.NextDouble() < 0.1 then
                      Some(rng.Next total)
                  else
                      None

              Exchange.Frame(t, e, writeFails) ]

/// A stream whose reads serve `input` and whose writes collect what is written, each completing
/// inside its call or later as the seed says.
type private ScriptedStream
    (sim: Sim, rng: Random, script: Script, input: byte[], ending: Ending, writeFailsAfter: int option, lost: bool) =
    inherit Stream()
    let written = Collections.Generic.List<byte>()
    let mutable position = 0
    let mutable lostOne = false

    // Every read's (offset, ready) and every write's ready, for the census.
    member val Reads = Collections.Generic.List<int * bool>()
    member val Writes = Collections.Generic.List<bool>()
    member _.Written = written.ToArray()
    member _.Position' = position

    // Complete `work` inside the call or later, by the seed.
    member private this.Complete<'T>(record: bool -> unit, work: unit -> 'T) : ValueTask<'T> =
        if rng.NextDouble() < script.ReadyShare then
            record true

            match
                (try
                    Ok(work ())
                 with failure ->
                     Error failure)
            with
            | Ok value -> ValueTask<'T>(value)
            | Error failure -> ValueTask<'T>(Task.FromException<'T> failure)
        else
            record false

            let later =
                TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

            if lost && not lostOne then
                // The completion this read was promised never comes.
                lostOne <- true
            else
                sim.Later(fun () ->
                    match
                        (try
                            Ok(work ())
                         with failure ->
                             Error failure)
                    with
                    | Ok value -> later.SetResult value
                    | Error failure -> later.SetException failure)

            ValueTask<'T>(later.Task)

    override this.ReadAsync(buffer: Memory<byte>, _: CancellationToken) : ValueTask<int> =
        sim.CheckOwner "a stream read ran off the simulation's thread"
        let offset = position

        let limit =
            match ending with
            | Ending.Clean -> input.Length
            | Ending.EndsAfter n
            | Ending.FailsAfter n -> min n input.Length

        this.Complete(
            (fun ready -> this.Reads.Add(offset, ready)),
            fun () ->
                if position >= limit then
                    match ending with
                    | Ending.FailsAfter _ when limit < input.Length ->
                        raise (IOException "the peer reset the connection")
                    | Ending.Clean
                    | Ending.EndsAfter _
                    | Ending.FailsAfter _ -> 0
                else
                    // At most about 256 reads a frame, so a large frame in tiny chunks stays cheap.
                    let chunk = max (1 + rng.Next script.MaxChunk) (input.Length / 256)
                    let n = min (min chunk (limit - position)) buffer.Length
                    input.AsMemory(position, n).CopyTo(buffer)
                    position <- position + n
                    n
        )

    override this.WriteAsync(buffer: ReadOnlyMemory<byte>, _: CancellationToken) : ValueTask =
        sim.CheckOwner "a stream write ran off the simulation's thread"
        let bytes = buffer.ToArray()

        let completed =
            this.Complete(
                this.Writes.Add,
                fun () ->
                    match writeFailsAfter with
                    | Some n when written.Count + bytes.Length > n ->
                        written.AddRange(Array.truncate (max 0 (n - written.Count)) bytes)
                        raise (IOException "broken pipe")
                    | Some _
                    | None ->
                        written.AddRange bytes
                        true
            )

        ValueTask(completed.AsTask() :> Task)

    override this.FlushAsync(_: CancellationToken) : Task =
        this.Complete(this.Writes.Add, (fun () -> true)).AsTask() :> Task

    override _.CanRead = true
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() =
        raise (NotSupportedException "the frame layer flushes asynchronously")

    override _.Read(_: byte[], _: int, _: int) =
        raise (NotSupportedException "the frame layer reads asynchronously")

    override _.Write(_: byte[], _: int, _: int) =
        raise (NotSupportedException "the frame layer writes asynchronously")

    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())

/// The planted readers, for `Mutant.OneReadPerPart` and `Mutant.PartialPayloadAsWhole`.
let private plantedRead (mutant: Mutant) (stream: Stream) : Task<Result<string, RepositoryIpc.FrameError>> =
    task {
        let header = Array.zeroCreate<byte> 4
        let! got = stream.ReadAsync(header.AsMemory())

        if got < 4 then
            return Error RepositoryIpc.FrameError.Truncated
        else
            let length = Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0))

            if length < 0 || length > RepositoryIpc.MaxFrameBytes then
                return Error(RepositoryIpc.FrameError.TooLarge length)
            else
                let payload = Array.zeroCreate<byte> length
                let mutable read = 0
                let mutable ended = false

                match mutant with
                | Mutant.OneReadPerPart ->
                    let! n = stream.ReadAsync(payload.AsMemory())
                    read <- n
                | Mutant.PartialPayloadAsWhole
                | Mutant.LostCompletion ->
                    while not ended && read < length do
                        let! n = stream.ReadAsync(payload.AsMemory(read, length - read))
                        if n = 0 then ended <- true else read <- read + n

                return Ok(Encoding.UTF8.GetString(payload, 0, read))
    }

let execute (subject: Subject) (seed: int) (history: Exchange list) : Violation list * Census =
    let sim = Sim(seed ^^^ 0xF4A3)
    let rng = Random(seed ^^^ 0x0BEE)

    let script =
        { MaxChunk =
            match rng.Next 3 with
            | 0 -> 1 + rng.Next 4
            | 1 -> 1 + rng.Next 64
            | _ -> 1 + rng.Next 65536
          ReadyShare = rng.NextDouble() }

    let lost =
        match subject with
        | Subject.Mutated Mutant.LostCompletion -> true
        | Subject.Current
        | Subject.Mutated _ -> false

    use _ = sim.Install()
    let violations = Collections.Generic.List<Violation>()
    let mutable census = Census.zero
    let mutable step = 0

    let violate (theorem: Theorem) (detail: string) =
        violations.Add
            { Seed = seed
              Step = step
              Theorem = theorem
              Detail = detail
              History = history
              Choices = sim.Choices }

    // Run until `task` settles; a task that cannot is a SETTLEMENT violation.
    let settle (what: string) (task: Task) =
        try
            sim.Runtime.Wait Timeout.InfiniteTimeSpan task |> ignore
            true
        with SimDeadlockException _ ->
            violate Theorem.Settlement $"%s{what} never completed"
            false

    let count (stream: ScriptedStream) =
        let reads = List.ofSeq stream.Reads
        let payloadReads = reads |> List.filter (fun (offset, _) -> offset >= 4)
        let writes = List.ofSeq stream.Writes

        census <-
            { census with
                ReadsAtOnce = census.ReadsAtOnce + (reads |> List.filter snd |> List.length)
                ReadsLater = census.ReadsLater + (reads |> List.filter (snd >> not) |> List.length)
                PayloadsAtOnce =
                    census.PayloadsAtOnce
                    + (if not payloadReads.IsEmpty && List.forall snd payloadReads then
                           1
                       else
                           0)
                PayloadsLater = census.PayloadsLater + (if List.exists (snd >> not) payloadReads then 1 else 0)
                WritesAtOnce = census.WritesAtOnce + (writes |> List.filter id |> List.length)
                WritesLater = census.WritesLater + (writes |> List.filter not |> List.length) }

    let read (stream: Stream) =
        match subject with
        | Subject.Mutated(Mutant.OneReadPerPart as m)
        | Subject.Mutated(Mutant.PartialPayloadAsWhole as m) -> plantedRead m stream
        | Subject.Current
        | Subject.Mutated Mutant.LostCompletion -> RepositoryIpc.readFrame stream CancellationToken.None

    let readBack (bytes: byte[]) (e: Ending) (expected: Result<string, RepositoryIpc.FrameError>) =
        let stream = new ScriptedStream(sim, rng, script, bytes, e, None, lost)
        let reading = read stream

        if settle "a frame read" reading then
            match
                (try
                    Ok reading.Result
                 with failure ->
                     Error failure)
            with
            | Error failure -> violate Theorem.Ending $"the read raised %O{failure}"
            | Ok got when got = expected -> ()
            | Ok(Ok text) ->
                match expected with
                | Ok expectedText ->
                    violate
                        Theorem.RoundTrip
                        $"read a %d{text.Length}-character frame for the %d{expectedText.Length} written"
                | Error error -> violate Theorem.Ending $"read a whole frame where %A{error} was due"
            | Ok(Error error) -> violate Theorem.Ending $"read %A{error} where %A{expected} was due"

            // What is left of a stream that ended cleanly drains to its end.
            match e with
            | Ending.Clean ->
                let drain = RepositoryIpc.drainUnread stream (TimeSpan.FromSeconds 1.0)

                if settle "a drain" drain && stream.Position' <> bytes.Length then
                    violate Theorem.Settlement $"a drain stopped at byte %d{stream.Position'} of %d{bytes.Length}"
            | Ending.EndsAfter _
            | Ending.FailsAfter _ -> ()

        count stream

    let apply (x: Exchange) =
        match x with
        | Exchange.Oversized length ->
            let header = BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder length)
            let tail = Array.create 16 0uy
            readBack (Array.append header tail) Ending.Clean (Error(RepositoryIpc.FrameError.TooLarge length))
        | Exchange.Frame(text, e, writeFails) ->
            let out = new ScriptedStream(sim, rng, script, [||], Ending.Clean, writeFails, lost)
            let writing = RepositoryIpc.writeFrame out text CancellationToken.None
            let total = 4 + Encoding.UTF8.GetByteCount text

            if settle "a frame write" writing then
                match
                    (try
                        Ok writing.Result
                     with failure ->
                         Error failure),
                    writeFails
                with
                | Error failure, _ -> violate Theorem.Ending $"the write raised %O{failure}"
                | Ok(Ok()), Some n when n < total ->
                    violate Theorem.Ending $"a write failing after %d{n} bytes succeeded"
                | Ok(Error(RepositoryIpc.FrameError.HungUp _)), Some n when n < total -> ()
                | Ok(Ok()), _ ->
                    count out
                    let bytes = out.Written

                    let expected =
                        let cut n =
                            if n >= bytes.Length then
                                Ok text
                            else
                                match e with
                                | Ending.FailsAfter _ ->
                                    Error(RepositoryIpc.FrameError.HungUp "the peer reset the connection")
                                | Ending.Clean
                                | Ending.EndsAfter _ -> Error RepositoryIpc.FrameError.Truncated

                        match e with
                        | Ending.Clean -> Ok text
                        | Ending.EndsAfter n
                        | Ending.FailsAfter n -> cut n

                    readBack bytes e expected
                | Ok other, _ -> violate Theorem.Ending $"the write ended %A{other}"

    let checkRuntime () =
        match sim.Faults |> List.filter (fun f -> not f.IsDeadlock) with
        | [] -> ()
        | faults -> violate Theorem.Runtime $"%A{faults}"

    let mutable remaining = history

    while violations.Count = 0 && not remaining.IsEmpty do
        step <- step + 1

        try
            apply remaining.Head
        with failure ->
            violate Theorem.Runtime $"the exchange raised %O{failure}"

        if violations.Count = 0 then
            checkRuntime ()

        remaining <- remaining.Tail

    List.ofSeq violations, census

let explore (subject: Subject) (seeds: int list) (stopAtFirst: bool) : Violation list * Census =
    let mutable violations = []
    let mutable census = Census.zero
    let mutable remaining = seeds

    while not remaining.IsEmpty && not (stopAtFirst && not violations.IsEmpty) do
        let seed = remaining.Head
        let found, seen = execute subject seed (generate seed HistoryLength)
        violations <- violations @ found
        census <- Census.add census seen
        remaining <- remaining.Tail

    violations, census

let describe (v: Violation) : string =
    let lines =
        v.History
        |> List.mapi (fun i x ->
            let mark = if i + 1 = v.Step then " <-- here" else ""
            $"  %d{i + 1}. %s{exchangeText x}%s{mark}")
        |> String.concat "\n"

    $"seed %d{v.Seed}, step %d{v.Step}: %A{v.Theorem}: %s{v.Detail}\n"
    + $"(%d{v.Choices.Length} runtime choices; replay with FSHW_SIM_SEED=%d{v.Seed} FSHW_SIM_HISTORIES=1)\n%s{lines}"
