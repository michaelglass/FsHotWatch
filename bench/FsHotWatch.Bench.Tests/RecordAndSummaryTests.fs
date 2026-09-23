module FsHotWatch.Bench.Tests.RecordAndSummaryTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

let private fp (phys: int64) : Footprint.Reading =
    { Pid = 1
      PhysFootprint = phys
      PhysFootprintPeak = phys * 2L
      Swapped = 10L
      Categories =
        Map.ofList
            [ "Untagged",
              { Dirty = phys - 100L
                Swapped = 10L
                Clean = 0L }
              "Malloc Small",
              { Dirty = 60L
                Swapped = 0L
                Clean = 0L }
              "mapped file",
              { Dirty = 20L
                Swapped = 0L
                Clean = 999L }
              "__DATA",
              { Dirty = 10L
                Swapped = 0L
                Clean = 0L }
              "Stack",
              { Dirty = 10L
                Swapped = 0L
                Clean = 0L } ] }

let private gc: Record.GcReading =
    { LiveBytes = 400L
      LiveObjects = 7L
      HeapSizeAfterGc = 450L
      GenerationBytes = [ 1L; 2L; 3L; 4L; 5L ]
      CommittedBytes = Some 600L
      BookkeepingBytes = Some 50L
      CounterCommittedBytes = Some 600L
      FragmentationPercent = Some 10.0
      EventsLost = 0
      WalkProblem = None }

[<Fact>]
let ``the managed-native split partitions the footprint exactly`` () =
    let f = fp 1000L
    let s = Record.split f gc

    let total =
        Record.managedOf s
        + s.RuntimeNative
        + s.Malloc
        + s.MappedFile
        + s.Image
        + s.StackAndOther

    test <@ total = f.PhysFootprint @>
    // Bookkeeping (50) is NOT added: it is mostly untouched, so not dirty.
    test <@ Record.managedOf s = 600L @>
    test <@ s.GcHeapNotLive = 50L && s.GcCommittedBeyondHeap = 150L @>
    test <@ s.RuntimeNative = 900L - 600L @>

[<Fact>]
let ``without CommittedUsage the split falls back to the counter, then to the heap size`` () =
    let noEvent =
        { gc with
            CommittedBytes = None
            BookkeepingBytes = None }

    test <@ Record.managedOf (Record.split (fp 1000L) noEvent) = 600L @>

    let nothing =
        { noEvent with
            CounterCommittedBytes = None }

    test <@ Record.managedOf (Record.split (fp 1000L) nothing) = 450L @>

let private record sessions rep session phase phys contended invalid : Record.BenchRecord =
    { RunId = "run1"
      RecordedAt = DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc)
      Label = "per-worktree"
      Mode = "legacy"
      Position =
        { Sessions = sessions
          Rep = rep
          Session = session
          Phase = phase }
      Worktree = "/w"
      Pid = 42
      Alive = true
      SleepGapMs = 0.0
      Load =
        { Load1 = 1.0
          Load5 = 1.0
          Load15 = 1.0
          Cpus = 12
          MemBytes = 1L
          MemFreePercent = Some 70
          SwapUsedBytes = None
          ForeignDaemons = [] }
      Contended = contended
      Footprint = Some(fp phys)
      FootprintBeforeWalk = None
      Gc = Some gc
      Heap =
        Some(
            HeapHistogram.estimate
                [ { TypeName = "ILTypeDef"
                    Module = "FSharp.Compiler.Service.dll"
                    Bytes = 100L
                    Count = 1L } ],
            []
        )
      Retention = None
      Config =
        { Strip = [ "tests" ]
          Set = [ "checker.cacheSizeFactor", "10" ]
          Echo = Some [ "checker.cacheSizeFactor", "10" ] }
      Scan = None
      Tests =
        Some
            { Total = 3
              Failed = 0
              Succeeded = 3
              Skipped = 0 }
      PhaseMs = Some 1500.0
      SettleFiles = None
      Invalid = invalid }

[<Fact>]
let ``a record round-trips the fields the summary scores`` () =
    let line = Record.toJsonLine (record 2 1 2 "post-gc" 1000L [] [])
    test <@ not (line.Contains "\n") @>
    test <@ line.Contains "\"sleepGapMs\":0" @>

    match Record.tryParseLine line with
    | None -> failwith "did not parse"
    | Some row ->
        test
            <@
                row.Position = { Sessions = 2
                                 Rep = 1
                                 Session = 2
                                 Phase = "post-gc" }
            @>

        test <@ row.PhysFootprint = Some 1000L && row.PhysFootprintPeak = Some 2000L @>
        test <@ row.Resident = Some 990L @>
        test <@ row.Managed = Some 600L && row.Native = Some 400L @>
        test <@ row.ManagedLive = Some 400L @>
        test <@ row.ShareableLow = Some 1.0 @>
        test <@ row.TestsTotal = Some 3 && row.PhaseMs = Some 1500.0 @>
        test <@ not row.Contended && List.isEmpty row.Invalid @>

[<Fact>]
let ``blank, truncated and foreign-schema lines are dropped, not thrown`` () =
    let good = Record.toJsonLine (record 1 1 1 "settled" 1000L [] [])

    let text =
        String.concat "\n" [ good; ""; good.Substring(0, 40); """{"schema":"other/9"}"""; good ]

    test <@ List.length (Record.parseSeries text) = 2 @>

[<Fact>]
let ``percentiles are nearest-rank and empty input has none`` () =
    test <@ Summary.median [ 3.0; 1.0; 2.0 ] = Some 2.0 @>
    test <@ Summary.percentile 95.0 [ 1.0 .. 20.0 ] = Some 19.0 @>
    test <@ Summary.median [] = None @>

let private rows (records: Record.BenchRecord list) =
    records |> List.map (Record.toJsonLine >> Record.tryParseLine >> Option.get)

[<Fact>]
let ``the N-session ratio divides the summed footprint of N sessions by one session's`` () =
    let series =
        rows
            [ record 1 1 1 "post-gc" 1000L [] []
              record 4 1 1 "post-gc" 1000L [] []
              record 4 1 2 "post-gc" 900L [] []
              record 4 1 3 "post-gc" 1100L [] []
              record 4 1 4 "post-gc" 1000L [] [] ]

    let report = Summary.summarize false series
    test <@ report.Ratios |> List.find (fun ((_, _, n), _) -> n = 4) |> snd = 4.0 @>
    test <@ report.Groups |> List.forall (fun g -> g.CompleteReps = 1) @>

[<Fact>]
let ``a repetition missing a session is not scored as a total`` () =
    let series = rows [ record 2 1 1 "settled" 1000L [] [] ]
    let report = Summary.summarize false series

    test
        <@
            report.Groups.[0].CompleteReps = 0
            && report.Groups.[0].TotalFootprintMedian = None
        @>

[<Fact>]
let ``invalid samples are never scored and contended ones only when allowed, which the report says`` () =
    let series =
        rows
            [ record 1 1 1 "settled" 1000L [] []
              record 1 2 1 "settled" 1000L [] [ "scan left 3 file(s) unchecked" ]
              record 1 3 1 "settled" 5000L [ "load1 229.6 > 6.0" ] [] ]

    let strict = Summary.summarize false series

    test
        <@
            strict.ExcludedInvalid = 1
            && strict.ExcludedContended = 1
            && not strict.ContendedIncluded
        @>

    test <@ strict.Groups.[0].SessionFootprintMedian = Some 1000.0 @>

    let loose = Summary.summarize true series
    test <@ loose.ContendedIncluded && loose.ExcludedContended = 0 @>
    test <@ (Summary.render loose).StartsWith "WARNING: contended" @>
    test <@ not ((Summary.render strict).Contains "WARNING") @>

[<Fact>]
let ``every record carries the config overrides it ran under and the daemon's echo`` () =
    let node =
        System.Text.Json.Nodes.JsonNode.Parse(Record.toJsonLine (record 1 1 1 "cold-scan" 1000L [] []))

    let config = node.["config"]
    test <@ config.["strip"].[0].GetValue<string>() = "tests" @>
    test <@ config.["set"].["checker.cacheSizeFactor"].GetValue<int>() = 10 @>
    test <@ config.["echo"].["checker.cacheSizeFactor"].GetValue<string>() = "10" @>

let private hostRecord sessions rep session phase phys : Record.BenchRecord =
    { record sessions rep session phase phys [] [] with
        Mode = "host"
        Footprint = (if session = 0 then Some(fp phys) else None) }

[<Fact>]
let ``a host record (session 0) IS the N-session total; per-session host rows carry no footprint`` () =
    let series =
        rows
            [ yield hostRecord 1 1 0 "post-gc" 1000L
              yield hostRecord 1 1 1 "post-gc" 0L
              yield hostRecord 4 1 0 "post-gc" 1500L
              for s in 1..4 do
                  yield hostRecord 4 1 s "post-gc" 0L ]

    let report = Summary.summarize false series
    let four = report.Groups |> List.find (fun g -> g.Sessions = 4)
    test <@ four.TotalFootprintMedian = Some 1500.0 && four.CompleteReps = 1 @>
    test <@ four.SessionFootprintMedian = None @>
    test <@ report.Ratios |> List.find (fun ((_, _, n), _) -> n = 4) |> snd = 1.5 @>

[<Fact>]
let ``host totals are compared with legacy totals for the same phase and session count`` () =
    let series =
        rows
            [ yield! [ for s in 1..4 -> record 4 1 s "post-gc" 1000L [] [] ]
              yield hostRecord 4 1 0 "post-gc" 1600L ]

    let report = Summary.summarize false series
    test <@ report.HostVsLegacy = [ ("per-worktree", "post-gc", 4), 0.4 ] @>
    test <@ (Summary.render report).Contains "host / legacy" @>

let private editRecord mode sessions rep session (ms: float) files : Record.BenchRecord =
    { record sessions rep session "edit" 0L [] [] with
        Label = "leaf"
        Mode = mode
        Footprint = None
        PhaseMs = Some ms
        SettleFiles = Some files }

[<Fact>]
let ``an edit record carries the files its settle re-checked`` () =
    let row =
        (Record.tryParseLine (Record.toJsonLine (editRecord "host" 1 1 1 2373.0 15))).Value

    test <@ row.SettleFiles = Some 15 && row.PhaseMs = Some 2373.0 && row.Mode = "host" @>

[<Fact>]
let ``the same label in two modes is two groups, with min, max and the files re-checked`` () =
    let series =
        rows
            [ editRecord "legacy" 1 1 1 2000.0 15
              editRecord "legacy" 1 2 1 2100.0 15
              editRecord "host" 1 1 1 2300.0 15
              editRecord "host" 1 2 1 2500.0 16 ]

    let report = Summary.summarize false series
    let legacy = report.Groups |> List.find (fun g -> g.Mode = "legacy")
    let host = report.Groups |> List.find (fun g -> g.Mode = "host")
    test <@ legacy.PhaseMsMin = Some 2000.0 && legacy.PhaseMsMax = Some 2100.0 @>
    test <@ host.SettleFilesSeen = [ 15; 16 ] @>

[<Fact>]
let ``host(1) settle p95 is judged against legacy(1) with the 10 percent abandon bar`` () =
    let within =
        Summary.summarize
            false
            (rows
                [ for i in 1..20 do
                      yield editRecord "legacy" 1 i 1 2000.0 15
                      yield editRecord "host" 1 i 1 2150.0 15 ])

    test <@ within.SettleP95 |> List.map (fun v -> v.Label, v.Sessions) = [ "leaf", 1 ] @>
    test <@ within.SettleP95.Head.Ratio = 1.075 && not within.SettleP95.Head.OverBar @>
    test <@ (Summary.render within).Contains "within the 10% bar" @>

    let over =
        Summary.summarize
            false
            (rows
                [ for i in 1..20 do
                      yield editRecord "legacy" 1 i 1 2000.0 15
                      yield editRecord "host" 1 i 1 2300.0 15 ])

    test <@ over.SettleP95.Head.OverBar @>
    test <@ (Summary.render over).Contains "OVER the 10% bar" @>
