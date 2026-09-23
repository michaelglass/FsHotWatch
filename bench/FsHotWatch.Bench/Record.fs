/// The benchmark's machine-readable record: one JSON object per line, appended.
///
/// Append-only JSON Lines like `ScanMetrics`, so a later run (the repository host) is
/// compared with an earlier one (per-worktree daemons) by reading the same file, and a
/// half-written last line from a killed harness drops one sample rather than the series.
///
/// Every record says which scenario, repetition, session and phase it is; the box's load
/// and whether that makes it contended; and why it is invalid, if it is. The memory
/// fields keep the raw instrument readings AND a derived partition of the footprint
/// (see `split`), so the partition can be recomputed if its method changes.
module FsHotWatch.Bench.Record

open System
open System.Text.Json
open System.Text.Json.Nodes

/// Schema tag written into every record. Bump when a field changes meaning.
[<Literal>]
let Schema = "fshw-bench/1"

/// The GC's own view of the heap after the collection the heap walk induced.
type GcReading =
    {
        /// Sum of object sizes the heap walk enumerated: the live set.
        LiveBytes: int64
        LiveObjects: int64
        /// `GCHeapStats` total for the induced collection: live objects plus the free
        /// space between them.
        HeapSizeAfterGc: int64
        /// Gen0, gen1, gen2, LOH, POH sizes from the same `GCHeapStats`.
        GenerationBytes: int64 list
        /// `GC/CommittedUsage.TotalCommittedInUse` for the induced collection: bytes the
        /// GC has committed for heap regions in use. `None` when the runtime did not
        /// emit it.
        CommittedBytes: int64 option
        /// `GC/CommittedUsage.TotalBookkeepingCommitted`: the GC's own tables (card
        /// table, mark array …), committed alongside the heap.
        BookkeepingBytes: int64 option
        /// The System.Runtime `gc-committed` counter after the collection. Measured equal
        /// to `CommittedBytes` (in use, WITHOUT bookkeeping) to the byte on the
        /// calibration process; kept as the independent cross-check.
        CounterCommittedBytes: int64 option
        /// The System.Runtime `gc-fragmentation` counter after the collection: free space
        /// as a percentage of the heap.
        FragmentationPercent: float option
        /// Events EventPipe dropped during the session. Non-zero means the walk missed
        /// objects and is not trusted.
        EventsLost: int
        /// `Error` text when the walk did not reconcile with `HeapSizeAfterGc`.
        WalkProblem: string option
    }

/// The footprint partitioned into managed and native parts, bytes. `Untagged` splits
/// into the three GC parts plus runtime-native by construction, so the parts sum to the
/// footprint.
type Split =
    {
        ManagedLive: int64
        /// GC heap that is not live objects (free space inside the heap).
        GcHeapNotLive: int64
        /// Committed in-use GC memory outside the heap proper: allocation budgets and free
        /// regions not yet decommitted.
        GcCommittedBeyondHeap: int64
        /// Untagged dirty bytes outside the GC's in-use commit: loader heaps, JIT code,
        /// stubs, and the touched part of the GC's bookkeeping tables. Negative means some
        /// committed GC pages were never touched (not dirty).
        RuntimeNative: int64
        Malloc: int64
        MappedFile: int64
        Image: int64
        StackAndOther: int64
    }

/// Managed = all GC-committed bytes; the rest is native.
let managedOf (s: Split) =
    s.ManagedLive + s.GcHeapNotLive + s.GcCommittedBeyondHeap

/// Derive the partition from one footprint reading and one GC reading taken together.
let split (fp: Footprint.Reading) (gc: GcReading) : Split =
    // In-use commit only. The bookkeeping commit (card table, mark array) covers the whole
    // region range and is mostly never touched: on a real daemon it was 712 MB committed
    // while Untagged's entire dirty total left no room for it. Untouched pages are not
    // dirty and not in the footprint, so adding it here drove runtimeNative negative; its
    // touched part lands in runtimeNative instead.
    let committed =
        gc.CommittedBytes
        |> Option.orElse gc.CounterCommittedBytes
        |> Option.defaultValue gc.HeapSizeAfterGc

    let dirty b = Footprint.dirtyIn b fp

    { ManagedLive = gc.LiveBytes
      GcHeapNotLive = gc.HeapSizeAfterGc - gc.LiveBytes
      GcCommittedBeyondHeap = committed - gc.HeapSizeAfterGc
      RuntimeNative = dirty Footprint.Bucket.Untagged - committed
      Malloc = dirty Footprint.Bucket.Malloc
      MappedFile = dirty Footprint.Bucket.MappedFile
      Image = dirty Footprint.Bucket.Image
      StackAndOther = dirty Footprint.Bucket.Stack + dirty Footprint.Bucket.Other }

/// The config overrides a record ran under, and what the daemon echoed back.
type ConfigProvenance =
    {
        /// `--strip` keys, in order.
        Strip: string list
        /// `--set` paths and their JSON values, in order.
        Set: (string * string) list
        /// The daemon's `[config] section: key=value` echo from its log window
        /// (`section.key → value`). `None` when the log was not read for this record.
        Echo: (string * string) list option
    }

/// No overrides (a probe, or a run without `--strip`/`--set`).
let noConfig = { Strip = []; Set = []; Echo = None }

/// Where in a scenario a record was taken.
type Position =
    {
        /// The scenario's session count (1, 2, 4 …).
        Sessions: int
        /// Repetition index, from 1.
        Rep: int
        /// Session index within the scenario, from 1.
        Session: int
        /// `cold-scan`, `settled`, `post-gc`, `after-tests`, `probe` …
        Phase: string
    }

/// One sample.
type BenchRecord =
    {
        RunId: string
        RecordedAt: DateTime
        /// Free-form: which binary / variant this is.
        Label: string
        /// `legacy` (one daemon per worktree) or `host` (one repository host; its
        /// session-0 record carries the N-session total).
        Mode: string
        Position: Position
        Worktree: string
        Pid: int
        Alive: bool
        /// Wall-clock minus sleep-excluding monotonic time since the repetition's window
        /// opened, ms: time the machine spent asleep. Above `Sleep.Tolerance` the record
        /// is invalid.
        SleepGapMs: float
        Load: Load.Snapshot
        Contended: string list
        Footprint: Footprint.Reading option
        /// With a heap walk: the footprint read just BEFORE it. The walk's EventPipe
        /// session buffers live in the daemon, so the difference from `Footprint` is the
        /// instrument's own cost, and a budget should be read from this one.
        FootprintBeforeWalk: Footprint.Reading option
        Gc: GcReading option
        Heap: (HeapHistogram.ShareEstimate * HeapHistogram.TypeStat list) option
        /// The heap-graph partition that narrows the type-based shareable range.
        Retention: Retention.Reading option
        Config: ConfigProvenance
        Scan: FsHotWatch.ScanMetrics.ScanSample option
        Tests: DaemonLog.TestTotals option
        /// Wall time of the phase the sample closes (scan, test run), ms. For an `edit`
        /// record, the session's own settle latency.
        PhaseMs: float option
        /// For an `edit` record: files the settle re-checked (`files=K`), so every
        /// latency carries the fan-out it was measured at.
        SettleFiles: int option
        /// Why this sample must not be scored; empty when valid.
        Invalid: string list
    }

let private opt (f: 'a -> JsonNode) (value: 'a option) : JsonNode =
    match value with
    | Some v -> f v
    | None -> null

let private n64 (v: int64) : JsonNode = JsonValue.Create(v)
let private nf (v: float) : JsonNode = JsonValue.Create(v)
let private ni (v: int) : JsonNode = JsonValue.Create(v)
let private ns (v: string) : JsonNode = JsonValue.Create(v)

let private arr (items: JsonNode seq) : JsonNode =
    let a = JsonArray()

    for i in items do
        a.Add(i)

    a

let private obj (fields: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()

    for k, v in fields do
        o.[k] <- v

    o

let private footprintNode (fp: Footprint.Reading) : JsonNode =
    let buckets = Footprint.byBucket fp

    obj
        [ "physFootprint", n64 fp.PhysFootprint
          "physFootprintPeak", n64 fp.PhysFootprintPeak
          "swapped", n64 fp.Swapped
          "resident", n64 (Footprint.resident fp)
          "buckets",
          obj
              [ for b in Footprint.allBuckets ->
                    let c =
                        buckets
                        |> Map.tryFind b
                        |> Option.defaultValue { Dirty = 0L; Swapped = 0L; Clean = 0L }

                    Footprint.bucketKey b, obj [ "dirty", n64 c.Dirty; "swapped", n64 c.Swapped ] ] ]

let private gcNode (gc: GcReading) : JsonNode =
    obj
        [ "liveBytes", n64 gc.LiveBytes
          "liveObjects", n64 gc.LiveObjects
          "heapSizeAfterGc", n64 gc.HeapSizeAfterGc
          "generationBytes", arr (gc.GenerationBytes |> List.map n64)
          "committedBytes", opt n64 gc.CommittedBytes
          "bookkeepingBytes", opt n64 gc.BookkeepingBytes
          "counterCommittedBytes", opt n64 gc.CounterCommittedBytes
          "fragmentationPercent", opt nf gc.FragmentationPercent
          "eventsLost", ni gc.EventsLost
          "walkProblem", opt ns gc.WalkProblem ]

let private splitNode (s: Split) : JsonNode =
    obj
        [ "managed", n64 (managedOf s)
          "managedLive", n64 s.ManagedLive
          "gcHeapNotLive", n64 s.GcHeapNotLive
          "gcCommittedBeyondHeap", n64 s.GcCommittedBeyondHeap
          "runtimeNative", n64 s.RuntimeNative
          "malloc", n64 s.Malloc
          "mappedFile", n64 s.MappedFile
          "image", n64 s.Image
          "stackAndOther", n64 s.StackAndOther ]

let private heapNode (estimate: HeapHistogram.ShareEstimate, top: HeapHistogram.TypeStat list) : JsonNode =
    obj
        [ "totalBytes", n64 estimate.TotalBytes
          "shareableLow", nf estimate.ShareableLow
          "shareableHigh", nf estimate.ShareableHigh
          "buckets",
          obj
              [ for s in
                    [ HeapHistogram.Share.Metadata
                      HeapHistogram.Share.PerSession
                      HeapHistogram.Share.TypedTree
                      HeapHistogram.Share.Unattributed ] ->
                    HeapHistogram.shareKey s, n64 (estimate.Buckets |> Map.tryFind s |> Option.defaultValue 0L) ]
          "top",
          arr (
              top
              |> List.map (fun t ->
                  obj
                      [ "type", ns t.TypeName
                        "module", ns t.Module
                        "bytes", n64 t.Bytes
                        "count", n64 t.Count
                        "share", ns (HeapHistogram.shareKey (HeapHistogram.classify t.TypeName t.Module)) ])
          ) ]

let private reachNode (r: HeapGraph.ReachBytes) : JsonNode =
    obj
        [ "importOnly", n64 r.ImportOnly
          "overlap", n64 r.Overlap
          "sessionOnly", n64 r.SessionOnly
          "unreached", n64 r.Unreached ]

let private retentionNode (r: Retention.Reading) : JsonNode =
    obj
        [ "rootSet", ns r.Set.Name
          "rootTypes", arr (r.Set.Types |> List.map ns)
          "barriers", arr (r.Set.Barriers |> List.map ns)
          "owners", arr (r.Set.Owners |> List.map ns)
          "roots", ni r.Result.Roots
          "graph",
          obj
              [ "nodes", ni r.Graph.Nodes
                "edges", ni r.Graph.Edges
                "unresolvedEdges", ni r.Graph.UnresolvedEdges
                "unresolvedRoots", ni r.Graph.UnresolvedRoots
                "edgeCountMismatch", n64 r.Graph.EdgeCountMismatch ]
          "all", reachNode r.Result.All
          "byShare",
          obj [ for KeyValue(share, bytes) in r.Result.ByShare -> HeapHistogram.shareKey share, reachNode bytes ]
          "shareableLow", nf r.Narrowed.Low
          "shareableHigh", nf r.Narrowed.High
          "typedTreeShareableLow", nf r.Narrowed.TypedTreeLow
          "typedTreeShareableHigh", nf r.Narrowed.TypedTreeHigh
          "perSessionOnImportSide", n64 r.Narrowed.PerSessionOnImportSide
          "accountingError", nf r.Narrowed.AccountingError
          "problems", arr (r.Problems |> List.map ns) ]

let private configNode (c: ConfigProvenance) : JsonNode =
    obj
        [ "strip", arr (c.Strip |> List.map ns)
          "set", obj [ for path, json in c.Set -> path, JsonNode.Parse(json) ]
          "echo", opt (fun (pairs: (string * string) list) -> obj [ for k, v in pairs -> k, ns v ]) c.Echo ]

let private loadNode (l: Load.Snapshot) : JsonNode =
    obj
        [ "load1", nf l.Load1
          "load5", nf l.Load5
          "load15", nf l.Load15
          "cpus", ni l.Cpus
          "memBytes", n64 l.MemBytes
          "memFreePercent", opt ni l.MemFreePercent
          "swapUsedBytes", opt n64 l.SwapUsedBytes
          "foreignDaemons", arr (l.ForeignDaemons |> List.map ni) ]

/// Render one record as a single JSON line (no trailing newline).
let toJsonLine (r: BenchRecord) : string =
    let node =
        obj
            [ "schema", ns Schema
              "runId", ns r.RunId
              "recordedAt", ns (r.RecordedAt.ToString("o"))
              "label", ns r.Label
              "mode", ns r.Mode
              "sessions", ni r.Position.Sessions
              "rep", ni r.Position.Rep
              "session", ni r.Position.Session
              "phase", ns r.Position.Phase
              "worktree", ns r.Worktree
              "pid", ni r.Pid
              "alive", JsonValue.Create(r.Alive)
              "sleepGapMs", nf r.SleepGapMs
              "load", loadNode r.Load
              "contended", arr (r.Contended |> List.map ns)
              "footprint", opt footprintNode r.Footprint
              "footprintBeforeWalk", opt footprintNode r.FootprintBeforeWalk
              "gc", opt gcNode r.Gc
              "split",
              (match r.Footprint, r.Gc with
               | Some fp, Some gc -> splitNode (split fp gc)
               | _ -> null)
              "heap", opt heapNode r.Heap
              "retention", opt retentionNode r.Retention
              "config", configNode r.Config
              "scan",
              opt
                  (fun (s: FsHotWatch.ScanMetrics.ScanSample) -> JsonNode.Parse(FsHotWatch.ScanMetrics.toJsonLine s))
                  r.Scan
              "tests",
              opt
                  (fun (t: DaemonLog.TestTotals) ->
                      obj
                          [ "total", ni t.Total
                            "failed", ni t.Failed
                            "succeeded", ni t.Succeeded
                            "skipped", ni t.Skipped ])
                  r.Tests
              "phaseMs", opt nf r.PhaseMs
              "settleFiles", opt ni r.SettleFiles
              "invalid", arr (r.Invalid |> List.map ns) ]

    node.ToJsonString(JsonSerializerOptions(WriteIndented = false))

/// The fields the summary scores, read back from one line.
type Row =
    {
        RunId: string
        Label: string
        /// `legacy` or `host`; lines written before the field existed read as `legacy`.
        Mode: string
        Position: Position
        PhysFootprint: int64 option
        PhysFootprintPeak: int64 option
        Resident: int64 option
        Managed: int64 option
        ManagedLive: int64 option
        Native: int64 option
        ShareableLow: float option
        ShareableHigh: float option
        /// From the heap-graph partition, only when its reading has no problems.
        RetentionLow: float option
        RetentionHigh: float option
        RetentionTypedTreeHigh: float option
        FilesChecked: int option
        FilesUnchecked: int option
        TestsTotal: int option
        PhaseMs: float option
        SettleFiles: int option
        Contended: bool
        Invalid: string list
    }

let private path (node: JsonNode) (keys: string list) : JsonNode option =
    keys
    |> List.fold
        (fun (acc: JsonNode option) (k: string) ->
            acc
            |> Option.bind (fun n ->
                match n with
                | :? JsonObject as o ->
                    let mutable v: JsonNode = null

                    if o.TryGetPropertyValue(k, &v) && not (isNull v) then
                        Some v
                    else
                        None
                | _ -> None))
        (Some node)

let private int64At node keys =
    path node keys |> Option.map (fun v -> v.GetValue<int64>())

let private intAt node keys =
    path node keys |> Option.map (fun v -> v.GetValue<int>())

let private floatAt node keys =
    path node keys |> Option.map (fun v -> v.GetValue<float>())

let private strings node keys =
    match path node keys with
    | Some(:? JsonArray as a) -> a |> Seq.map (fun v -> v.GetValue<string>()) |> Seq.toList
    | _ -> []

/// Parse one line. `None` for blank, malformed, or foreign-schema lines — a half-written
/// last line must not poison the series.
// MGA-ERROR-REPORT-001:ok — a malformed line is a normal input for an append-only log
// whose writer can be killed mid-line; it is dropped, not thrown.
let tryParseLine (line: string) : Row option =
    if String.IsNullOrWhiteSpace line then
        None
    else
        try
            let node = JsonNode.Parse(line)

            match path node [ "schema" ] |> Option.map (fun v -> v.GetValue<string>()) with
            | Some s when s = Schema ->
                let managed = int64At node [ "split"; "managed" ]

                let trusted (v: float option) =
                    if List.isEmpty (strings node [ "retention"; "problems" ]) then
                        v
                    else
                        None

                let phys = int64At node [ "footprint"; "physFootprint" ]

                Some
                    { RunId = (path node [ "runId" ]).Value.GetValue<string>()
                      Label = (path node [ "label" ]).Value.GetValue<string>()
                      Mode =
                        path node [ "mode" ]
                        |> Option.map (fun v -> v.GetValue<string>())
                        |> Option.defaultValue "legacy"
                      Position =
                        { Sessions = (intAt node [ "sessions" ]).Value
                          Rep = (intAt node [ "rep" ]).Value
                          Session = (intAt node [ "session" ]).Value
                          Phase = (path node [ "phase" ]).Value.GetValue<string>() }
                      PhysFootprint = phys
                      PhysFootprintPeak = int64At node [ "footprint"; "physFootprintPeak" ]
                      Resident = int64At node [ "footprint"; "resident" ]
                      Managed = managed
                      ManagedLive = int64At node [ "gc"; "liveBytes" ]
                      Native = Option.map2 (fun p m -> p - m) phys managed
                      ShareableLow = floatAt node [ "heap"; "shareableLow" ]
                      ShareableHigh = floatAt node [ "heap"; "shareableHigh" ]
                      RetentionLow = trusted (floatAt node [ "retention"; "shareableLow" ])
                      RetentionHigh = trusted (floatAt node [ "retention"; "shareableHigh" ])
                      RetentionTypedTreeHigh = trusted (floatAt node [ "retention"; "typedTreeShareableHigh" ])
                      FilesChecked = intAt node [ "scan"; "filesChecked" ]
                      FilesUnchecked = intAt node [ "scan"; "filesUnchecked" ]
                      TestsTotal = intAt node [ "tests"; "total" ]
                      PhaseMs = floatAt node [ "phaseMs" ]
                      SettleFiles = intAt node [ "settleFiles" ]
                      Contended = not (List.isEmpty (strings node [ "contended" ]))
                      Invalid = strings node [ "invalid" ] }
            | _ -> None
        with _ ->
            None

/// Parse a whole JSON Lines document, dropping unparseable lines.
let parseSeries (text: string) : Row list =
    text.Split('\n') |> Array.toList |> List.choose tryParseLine

/// Append one record, creating the directory.
let append (path: string) (r: BenchRecord) : unit =
    let dir = IO.Path.GetDirectoryName(path: string)

    if not (String.IsNullOrEmpty dir) then
        IO.Directory.CreateDirectory(dir) |> ignore

    IO.File.AppendAllText(path, toJsonLine r + "\n")
