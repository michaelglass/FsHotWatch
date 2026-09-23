/// The harness's contact with the outside world: running `footprint`, checking a pid is
/// alive, finding a daemon's diagnostic socket, walking its heap, reading the box's load.
///
/// ## Why `dotnet-gcdump -p <pid>` could not attach, and how this module attaches
///
/// A .NET process creates its diagnostic socket at `$TMPDIR/dotnet-diagnostic-<pid>-…`,
/// falling back to `/tmp` when `TMPDIR` is unset — and the devenv shells that launch the
/// downstream daemons under the nix-store SDK run with `TMPDIR` UNSET. A diagnostic tool
/// run from an ordinary macOS shell has `TMPDIR=/var/folders/…/T/` and looks only there,
/// so it reports no such process while the socket sits in `/tmp` (287 live sockets were
/// in `/tmp` against 1,154 in `/var/folders` on the day this was diagnosed). Separately,
/// the globally installed `dotnet-gcdump` apphost could not find a runtime at all under a
/// mise-installed SDK without `DOTNET_ROOT`.
///
/// So nothing here depends on the two processes agreeing about `TMPDIR`:
///
/// * a daemon the harness starts gets `DOTNET_DiagnosticPorts=<known path>,listen,nosuspend`,
///   an extra listening socket at a path the harness chose;
/// * any other daemon's socket is found by asking the kernel which unix sockets that pid
///   holds (`lsof -U`), with a `$TMPDIR`-then-`/tmp` glob as the fallback;
/// * the connection is `DiagnosticsClient.FromDiagnosticPort(<path>,connect)` — the same
///   thing as `dotnet-gcdump collect --diagnostic-port <path>,connect`.
module FsHotWatch.Bench.Instruments

open System
open System.Collections.Generic
open System.Diagnostics.Tracing
open System.IO
open System.Threading
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers
open Microsoft.Diagnostics.Tracing.Parsers.Clr
open FsHotWatch

/// Run a command to completion with extra environment; `Ok stdout+stderr` on exit 0.
let runWith
    (env: (string * string) list)
    (command: string)
    (args: string)
    (workDir: string)
    (timeout: TimeSpan)
    : Result<string, string> =
    match ProcessHelper.runProcess command args workDir env (ProcessHelper.ProcessBounds.silent timeout) with
    | ProcessHelper.ProcessOutcome.Succeeded out -> Ok(ProcessHelper.ProcessOutput.text out)
    | outcome -> Error(ProcessHelper.outputOf outcome)

/// Run a command to completion; `Ok stdout+stderr` on exit 0.
let run (command: string) (args: string) (workDir: string) (timeout: TimeSpan) : Result<string, string> =
    runWith [] command args workDir timeout

/// Liveness by `kill -0`. On this box `ps -p <pid>` exits 1 for a LIVE pid, so it is
/// never used for this.
let isAlive (pid: int) : bool =
    match run "kill" $"-0 %d{pid}" "/" (TimeSpan.FromSeconds 10.0) with
    | Ok _ -> true
    | Error _ -> false

/// One `footprint --json` reading of `pid`.
let footprint (pid: int) : Result<Footprint.Reading, string> =
    let nonce = Guid.NewGuid().ToString("N")
    let file = Path.Combine(Path.GetTempPath(), $"fshw-bench-fp-%d{pid}-%s{nonce}.json")

    try
        match run "footprint" $"--json %s{ProcessHelper.quoteArg file} %d{pid}" "/" (TimeSpan.FromSeconds 120.0) with
        | Error e when not (File.Exists file) -> Error $"footprint failed: %s{e}"
        | _ -> Footprint.parseJson pid (File.ReadAllText file)
    finally
        try
            File.Delete file
        with _ ->
            ()

/// Find `pid`'s diagnostic socket. `preferred` (a harness-chosen
/// `DOTNET_DiagnosticPorts` listen path) wins when it exists.
let diagnosticPort (pid: int) (preferred: string option) : Result<string, string> =
    match preferred with
    | Some p when File.Exists p -> Ok p
    | _ ->
        let marker = $"dotnet-diagnostic-%d{pid}-"

        let fromLsof =
            match run "lsof" $"-a -U -p %d{pid} -F n" "/" (TimeSpan.FromSeconds 30.0) with
            | Ok out ->
                out.Split('\n')
                |> Array.tryPick (fun l ->
                    if
                        l.StartsWith("n", StringComparison.Ordinal)
                        && l.Contains(marker, StringComparison.Ordinal)
                    then
                        Some(l.Substring(1).Trim())
                    else
                        None)
            | Error _ -> None

        let fromGlob () =
            [ Path.GetTempPath(); "/tmp" ]
            |> List.distinct
            |> List.tryPick (fun dir ->
                try
                    Directory.GetFiles(dir, marker + "*-socket") |> Array.tryHead
                with _ ->
                    None)

        match fromLsof |> Option.orElseWith fromGlob with
        | Some path -> Ok path
        | None -> Error $"no diagnostic socket for pid %d{pid} (checked lsof, %s{Path.GetTempPath()} and /tmp)"

/// A read-only view of a file another thread is still appending to: a read at the
/// current end waits for more bytes instead of returning end-of-stream, until the writer
/// says it is done.
///
/// This is what keeps the heap walk from losing events. The runtime drops EventPipe
/// events when its session buffer fills, and it fills whenever the READER lags; parsing a
/// multi-GB heap walk while the box is loaded lagged enough to drop 20,139 events on this
/// repository's own daemon. So one thread only drains the socket into a file, which keeps
/// up, and the parser reads that file through this stream at whatever pace it manages.
type internal TailStream(path: string, writerDone: unit -> bool) =
    inherit Stream()

    let file =
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = file.Position
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.Read(buffer: byte[], offset: int, count: int) =
        let rec loop () =
            let n = file.Read(buffer, offset, count)

            if n > 0 then
                n
            elif writerDone () then
                // One last read: the writer may have flushed between the two checks.
                file.Read(buffer, offset, count)
            else
                Thread.Sleep 10
                loop ()

        loop ()

    override _.Dispose(disposing: bool) =
        if disposing then
            file.Dispose()

        base.Dispose(disposing)

/// EventPipe session buffer for a heap walk, MB. See `walkHeap`.
[<Literal>]
let WalkBufferMB = 4096

/// What one heap walk produced.
type HeapWalk =
    {
        Stats: HeapHistogram.TypeStat list
        Gc: Record.GcReading
        /// The object graph, when the walk was asked to keep it.
        Graph: (HeapGraph.Graph * HeapGraph.BuildReport) option
    }

let private add (d: Dictionary<'k, int64>) (k: 'k) (v: int64) =
    match d.TryGetValue k with
    | true, cur -> d.[k] <- cur + v
    | _ -> d.[k] <- v

let private payloadInt64 (e: TraceEvent) (name: string) : int64 option =
    try
        match e.PayloadByName name with
        | null -> None
        | v -> Some(Convert.ToInt64 v)
    with _ ->
        None

/// Everything the walk's events carry, accumulated by handlers attached to one event
/// source — the same handlers whether the source is a live session or a saved trace.
type private WalkState(source: EventPipeEventSource, captureGraph: bool) =
    let bytes = Dictionary<uint64, int64>()
    let counts = Dictionary<uint64, int64>()
    let names = Dictionary<uint64, string>()
    let typeModule = Dictionary<uint64, uint64>()
    let modules = Dictionary<uint64, string>()
    let addresses = ResizeArray<int64>()
    let sizes = ResizeArray<int64>()
    let typeIds = ResizeArray<uint64>()
    let edgeCounts = ResizeArray<int>()
    let edgeTargets = ResizeArray<int64>()
    let roots = ResizeArray<int64>()
    let weakEdges = ResizeArray<int64 * int64>()
    let mutable currentGc = -1
    let mutable walkGc = -1
    let mutable walkDone = false
    let mutable heapStats: (int64 * int64 list) option = None
    let mutable committed: int64 option = None
    let mutable committedBookkeeping: int64 option = None
    let mutable counterCommitted: int64 option = None
    let mutable fragmentationPercent: float option = None
    let mutable countersAfter = 0

    do
        source.Clr.add_GCStart (fun e -> currentGc <- e.Count)

        source.Clr.add_GCStop (fun e ->
            if e.Count = walkGc then
                walkDone <- true)

        source.Clr.add_GCBulkNode (fun e ->
            if walkGc < 0 then
                walkGc <- currentGc

            for i in 0 .. e.Count - 1 do
                let v = e.Values(i)
                add bytes v.TypeID (int64 v.Size)
                add counts v.TypeID 1L

                if captureGraph then
                    addresses.Add(int64 v.Address)
                    sizes.Add(int64 v.Size)
                    typeIds.Add v.TypeID
                    edgeCounts.Add(int v.EdgeCount))

        if captureGraph then
            source.Clr.add_GCBulkEdge (fun e ->
                for i in 0 .. e.Count - 1 do
                    edgeTargets.Add(int64 (e.Values(i).Target)))

            source.Clr.add_GCBulkRootEdge (fun e ->
                for i in 0 .. e.Count - 1 do
                    roots.Add(int64 (e.Values(i).RootedNodeAddress)))

            source.Clr.add_GCBulkRootStaticVar (fun e ->
                for i in 0 .. e.Count - 1 do
                    roots.Add(int64 (e.Values(i).ObjectID)))

            source.Clr.add_GCBulkRootConditionalWeakTableElementEdge (fun e ->
                for i in 0 .. e.Count - 1 do
                    let v = e.Values(i)
                    weakEdges.Add(int64 v.GCKeyNodeID, int64 v.GCValueNodeID))

        source.Clr.add_TypeBulkType (fun e ->
            for i in 0 .. e.Count - 1 do
                let v = e.Values(i)
                names.[v.TypeID] <- v.TypeName
                typeModule.[v.TypeID] <- v.ModuleID)

        source.Clr.add_GCHeapStats (fun e ->
            if walkDone && heapStats.IsNone then
                heapStats <-
                    Some(
                        e.TotalHeapSize,
                        [ e.GenerationSize0
                          e.GenerationSize1
                          e.GenerationSize2
                          e.GenerationSize3
                          e.GenerationSize4 ]
                    ))

        // Rundown on: module names only arrive as rundown events when the session stops,
        // and nested F# types reach the walk BARE (`TType_app`), so the module is what
        // tells an FCS type from anything else.
        let rundown = ClrRundownTraceEventParser(source)
        rundown.add_LoaderModuleDCStop (fun e -> modules.[uint64 e.ModuleID] <- e.ModuleILPath)
        source.Clr.add_LoaderModuleLoad (fun e -> modules.[uint64 e.ModuleID] <- e.ModuleILPath)

        // `GC/CommittedUsage` has no typed parser in this TraceEvent; it arrives decoded on
        // AllEvents. The LATEST one once the walk's collection has begun is that
        // collection's (it is blocking and the last GC the session waits for).
        source.add_AllEvents (fun e ->
            if e.EventName = "GC/CommittedUsage" && walkGc >= 0 then
                match payloadInt64 e "TotalCommittedInUse", payloadInt64 e "TotalBookkeepingCommitted" with
                | Some inUse, bookkeeping ->
                    committed <- Some inUse
                    committedBookkeeping <- bookkeeping
                | None, _ -> ())

        // EventCounters need the DYNAMIC parser: without a subscriber there, they arrive
        // undecoded (no name, no payload).
        source.Dynamic.add_All (fun e ->
            if e.EventName = "EventCounters" && walkDone then
                match e.PayloadValue(0) with
                | :? IDictionary<string, obj> as outer ->
                    match outer.TryGetValue "Payload" with
                    | true, (:? IDictionary<string, obj> as p) when string p.["Name"] = "gc-committed" ->
                        // EventCounters report MB as bytes / 1,000,000.
                        counterCommitted <- Some(int64 (Convert.ToDouble p.["Mean"] * 1_000_000.0))
                        countersAfter <- countersAfter + 1
                    | true, (:? IDictionary<string, obj> as p) when string p.["Name"] = "gc-fragmentation" ->
                        // The GC's own FragmentedBytes / HeapSizeBytes for its last collection, %.
                        fragmentationPercent <- Some(Convert.ToDouble p.["Mean"])
                    | _ -> ()
                | _ -> ())

    /// The walk's collection finished and the post-GC readings have arrived.
    member _.Settled = countersAfter >= 2 && heapStats.IsSome
    member _.WalkDone = walkDone

    member _.Describe() =
        $"gc=%d{currentGc} walkGc=%d{walkGc} done=%b{walkDone} types=%d{bytes.Count} stats=%b{heapStats.IsSome} counters=%d{countersAfter}"

    member private _.TypeOf(typeId: uint64) : string * string =
        let moduleName =
            match typeModule.TryGetValue typeId with
            | true, m ->
                match modules.TryGetValue m with
                | true, path -> Path.GetFileName path
                | _ -> ""
            | _ -> ""

        let typeName =
            match names.TryGetValue typeId with
            | true, n -> n
            | _ -> $"<type %x{typeId}>"

        typeName, moduleName

    member this.Result() : Result<HeapWalk, string> =
        match heapStats with
        | None when not walkDone -> Error "the heap walk did not complete before the timeout"
        | _ ->
            let stats =
                [ for KeyValue(typeId, b) in bytes ->
                      let typeName, moduleName = this.TypeOf typeId

                      ({ TypeName = typeName
                         Module = moduleName
                         Bytes = b
                         Count = counts.[typeId] }
                      : HeapHistogram.TypeStat) ]

            let live = stats |> List.sumBy _.Bytes
            let heapSize, gens = heapStats |> Option.defaultValue (0L, [])

            let graph =
                if captureGraph then
                    Some(
                        HeapGraph.build
                            { Addresses = addresses.ToArray()
                              Sizes = sizes.ToArray()
                              TypeIds = typeIds.ToArray()
                              EdgeCounts = edgeCounts.ToArray()
                              EdgeTargets = edgeTargets.ToArray()
                              RootAddresses = roots.ToArray()
                              WeakTableEdges = weakEdges.ToArray() }
                            this.TypeOf
                    )
                else
                    None

            Ok
                { Stats = stats
                  Graph = graph
                  Gc =
                    { LiveBytes = live
                      LiveObjects = stats |> List.sumBy _.Count
                      HeapSizeAfterGc = heapSize
                      GenerationBytes = gens
                      CommittedBytes = committed
                      BookkeepingBytes = committedBookkeeping
                      CounterCommittedBytes = counterCommitted
                      FragmentationPercent = fragmentationPercent
                      EventsLost = source.EventsLost
                      WalkProblem =
                        if source.EventsLost > 0 then
                            Some $"EventPipe dropped %d{source.EventsLost} event(s): the walk is incomplete"
                        else
                            match HeapHistogram.reconcile live heapSize fragmentationPercent with
                            | Ok() -> None
                            | Error e -> Some e } }

/// Walk `port`'s heap: open an EventPipe session with the GC heap-snapshot keywords,
/// which makes the runtime induce a full blocking collection and enumerate every live
/// object (and, with `captureGraph`, every reference and GC root) during it. The GC's
/// own `GCHeapStats` and `GC/CommittedUsage` for that same collection, and the
/// System.Runtime counters just after it, are captured alongside for reconciliation.
/// `keepTrace` moves the raw `.nettrace` there instead of deleting it, so the graph can
/// be re-analysed offline (`fshw-bench graph`) without walking the daemon again.
let walkHeap
    (port: string)
    (timeout: TimeSpan)
    (captureGraph: bool)
    (keepTrace: string option)
    : Result<HeapWalk, string> =
    try
        let client =
            DiagnosticsClient.FromDiagnosticPort($"%s{port},connect", CancellationToken.None).GetAwaiter().GetResult()

        let providers =
            [ EventPipeProvider(
                  "Microsoft-Windows-DotNETRuntime",
                  EventLevel.Verbose,
                  int64 ClrTraceEventParser.Keywords.GCHeapSnapshot
              )
              EventPipeProvider("System.Runtime", EventLevel.Informational, 0L, dict [ "EventCounterIntervalSec", "1" ]) ]

        // The runtime writes the whole walk while the world is stopped, faster than any
        // socket drains, so the session buffer IN THE DAEMON must hold the burst: 256 MB
        // dropped 31,881 events on this repository's own ~200-file daemon. The buffer is
        // committed on demand and freed when the session ends; the record keeps a
        // footprint read before the walk so its cost is never mistaken for the daemon's.
        use session = client.StartEventPipeSession(providers, true, WalkBufferMB)

        let nonce = Guid.NewGuid().ToString("N")

        let traceFile =
            Path.Combine(Path.GetTempPath(), $"fshw-bench-walk-%s{nonce}.nettrace")

        let mutable drained = false

        let drain =
            Tasks.Task.Run(fun () ->
                try
                    use out =
                        new FileStream(traceFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)

                    session.EventStream.CopyTo(out, 1 <<< 20)
                finally
                    Volatile.Write(&drained, true))

        while not (File.Exists traceFile) do
            Thread.Sleep 10

        let result =
            use tail = new TailStream(traceFile, (fun () -> Volatile.Read(&drained)))
            use source = new EventPipeEventSource(tail)
            let state = WalkState(source, captureGraph)
            let processing = Tasks.Task.Run(fun () -> source.Process() |> ignore)
            let deadline = DateTime.UtcNow + timeout

            // Two counter ticks after the walk's collection: the committed figure then
            // reflects the post-GC heap, and GCHeapStats has certainly arrived.
            while not state.Settled && DateTime.UtcNow < deadline && not processing.IsCompleted do
                Thread.Sleep 100

                if not (isNull (Environment.GetEnvironmentVariable "FSHW_BENCH_DEBUG")) then
                    eprintfn "[walk] %s" (state.Describe())

            session.Stop()
            drain.Wait(TimeSpan.FromSeconds 120.0) |> ignore
            processing.Wait(TimeSpan.FromMinutes 30.0) |> ignore
            state.Result()

        match keepTrace with
        | Some dest ->
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath dest))
            |> ignore

            File.Move(traceFile, dest, true)
        | None ->
            try
                File.Delete traceFile
            with _ ->
                ()

        result
    with ex ->
        Error $"heap walk failed on %s{port}: %s{ex.Message}"

/// Re-read a heap walk kept with `walkHeap … (Some path)`.
let readTrace (path: string) (captureGraph: bool) : Result<HeapWalk, string> =
    try
        use source = new EventPipeEventSource(path)
        let state = WalkState(source, captureGraph)
        source.Process() |> ignore
        state.Result()
    with ex ->
        Error $"could not read %s{path}: %s{ex.Message}"

let private daemonPattern = "(FsHotWatch\\.Cli(\\.dll)?|/fshw) start( |$)"

/// Pids of every running FsHotWatch daemon.
let daemonPids () : int list =
    match run "pgrep" $"-f %s{ProcessHelper.quoteArg daemonPattern}" "/" (TimeSpan.FromSeconds 10.0) with
    | Ok out ->
        out.Split('\n')
        |> Array.choose (fun s ->
            match Int32.TryParse(s.Trim()) with
            | true, p -> Some p
            | _ -> None)
        |> Array.toList
    | Error _ -> []

let private sysctl (name: string) =
    run "sysctl" $"-n %s{name}" "/" (TimeSpan.FromSeconds 10.0)
    |> Result.toOption
    |> Option.map _.Trim()

/// Read the box now. `ours` are the daemon pids this harness started (not foreign).
let loadSnapshot (ours: int list) : Load.Snapshot =
    let l1, l5, l15 =
        sysctl "vm.loadavg"
        |> Option.bind Load.parseLoadAvg
        |> Option.defaultValue (nan, nan, nan)

    { Load1 = l1
      Load5 = l5
      Load15 = l15
      Cpus = Environment.ProcessorCount
      MemBytes = sysctl "hw.memsize" |> Option.map int64 |> Option.defaultValue 0L
      MemFreePercent =
        run "memory_pressure" "-Q" "/" (TimeSpan.FromSeconds 30.0)
        |> Result.toOption
        |> Option.bind Load.parseMemFreePercent
      SwapUsedBytes = sysctl "vm.swapusage" |> Option.bind Load.parseSwapUsed
      ForeignDaemons =
        daemonPids ()
        |> List.filter (fun p -> not (List.contains p ours) && p <> Environment.ProcessId) }
