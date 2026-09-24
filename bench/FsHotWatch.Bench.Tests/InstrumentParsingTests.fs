module FsHotWatch.Bench.Tests.InstrumentParsingTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

// A trimmed `footprint --json` capture from a live .NET process on this box. The
// property that makes the reading trustworthy: phys_footprint equals the SUM of dirty,
// and swapped is a subset of dirty (dirty includes compressed pages).
let private footprintJson =
    """{"unit":"byte","processes":[
      {"name":"other","pid":1,"footprint":10,"auxiliary":{"phys_footprint":10,"phys_footprint_peak":10},
       "categories":{"Untagged":{"dirty":10,"swapped":0,"clean":0,"reclaimable":0,"wired":0,"regions":1}}},
      {"name":"dotnet","pid":8239,"footprint":400,"auxiliary":{"phys_footprint_peak":900,"phys_footprint":400},
       "categories":{
         "Untagged":{"dirty":300,"swapped":120,"clean":0,"reclaimable":0,"wired":0,"regions":9},
         "Malloc Small":{"dirty":50,"swapped":5,"clean":0,"reclaimable":0,"wired":0,"regions":2},
         "Malloc Large":{"dirty":10,"swapped":0,"clean":0,"reclaimable":0,"wired":0,"regions":1},
         "mapped file":{"dirty":20,"swapped":0,"clean":7000,"reclaimable":0,"wired":0,"regions":3},
         "__DATA":{"dirty":8,"swapped":0,"clean":0,"reclaimable":0,"wired":0,"regions":4},
         "__TEXT":{"dirty":0,"swapped":0,"clean":900,"reclaimable":0,"wired":0,"regions":4},
         "Stack":{"dirty":7,"swapped":0,"clean":0,"reclaimable":0,"wired":0,"regions":2},
         "page table":{"dirty":5,"swapped":0,"clean":0,"reclaimable":0,"wired":0,"regions":1}}}]}"""

let private reading () =
    match Footprint.parseJson 8239 footprintJson with
    | Ok r -> r
    | Error e -> failwith e

[<Fact>]
let ``footprint reads the named pid's kernel footprint and lifetime peak, not another process's`` () =
    let r = reading ()
    test <@ r.PhysFootprint = 400L && r.PhysFootprintPeak = 900L @>

[<Fact>]
let ``swapped is summed across categories and resident is footprint minus swapped`` () =
    let r = reading ()
    test <@ r.Swapped = 125L @>
    test <@ Footprint.resident r = 275L @>

[<Fact>]
let ``categories fold into buckets and clean pages never count toward dirty`` () =
    let r = reading ()
    test <@ Footprint.dirtyIn Footprint.Bucket.Untagged r = 300L @>
    test <@ Footprint.dirtyIn Footprint.Bucket.Malloc r = 60L @>
    test <@ Footprint.dirtyIn Footprint.Bucket.MappedFile r = 20L @>
    test <@ Footprint.dirtyIn Footprint.Bucket.Image r = 8L @>
    test <@ Footprint.dirtyIn Footprint.Bucket.Stack r = 7L @>
    test <@ Footprint.dirtyIn Footprint.Bucket.Other r = 5L @>

    let bucketTotal =
        Footprint.allBuckets |> List.sumBy (fun b -> Footprint.dirtyIn b r)

    test <@ bucketTotal = r.PhysFootprint @>

[<Fact>]
let ``an absent pid or a changed format is an error, never a zero reading`` () =
    test <@ Result.isError (Footprint.parseJson 4242 footprintJson) @>
    test <@ Result.isError (Footprint.parseJson 8239 """{"processes":[{"pid":8239}]}""") @>
    test <@ Result.isError (Footprint.parseJson 8239 "not json") @>

let private share name moduleName = HeapHistogram.classify name moduleName

[<Fact>]
let ``bare nested FCS type names classify by the module they came from`` () =
    test <@ share "TType_app" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.TypedTree @>
    test <@ share "EntityRef" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.TypedTree @>
    test <@ share "ILTypeDef" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.Metadata @>
    test <@ share "ILMethodDefs" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.Metadata @>
    test <@ share "SynExpr+App" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.PerSession @>
    test <@ share "CapturedNameResolution" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.PerSession @>

[<Fact>]
let ``syntax-namespace types the typed tree carries for imported members are ambiguous, not per session`` () =
    test
        <@ share "FSharp.Compiler.Syntax.SynMemberFlags" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.TypedTree @>

    test
        <@
            share "Microsoft.FSharp.Core.FSharpOption`1[FSharp.Compiler.Syntax.Ident]" "FSharp.Core.dll" = HeapHistogram.Share.TypedTree
        @>

    test
        <@
            share
                "Microsoft.FSharp.Collections.MapTreeNode`2[FSharp.Compiler.Syntax.PrettyNaming+NameArityPair,FSharp.Compiler.TypedTree+Entity]"
                "FSharp.Core.dll" = HeapHistogram.Share.TypedTree
        @>
    // A syntax TREE node is still the session's own source.
    test <@ share "FSharp.Compiler.Syntax.SynBinding" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.PerSession @>

[<Fact>]
let ``a type named IL-something only counts as metadata when IL begins a word`` () =
    // `Ilist`-like leaf names must not be swept into metadata by the prefix rule.
    test <@ share "Illegal" "FSharp.Compiler.Service.dll" = HeapHistogram.Share.TypedTree @>

[<Fact>]
let ``a BCL container of FCS types classifies by its FCS type argument`` () =
    test
        <@
            share "Microsoft.FSharp.Collections.FSharpList`1[FSharp.Compiler.AbstractIL.IL+ILTypeRef]" "FSharp.Core.dll" = HeapHistogram.Share.Metadata
        @>

    test
        <@
            share "Microsoft.FSharp.Collections.FSharpList`1[FSharp.Compiler.TypedTree+TType]" "FSharp.Core.dll" = HeapHistogram.Share.TypedTree
        @>

[<Fact>]
let ``BCL-only types are unattributed and daemon or project-model state is per session`` () =
    test <@ share "System.String" "System.Private.CoreLib.dll" = HeapHistogram.Share.Unattributed @>
    test <@ share "System.Byte[]" "System.Private.CoreLib.dll" = HeapHistogram.Share.Unattributed @>
    test <@ share "FsHotWatch.ErrorLedger" "FsHotWatch.dll" = HeapHistogram.Share.PerSession @>
    test <@ share "ProjectInstance" "Microsoft.Build.dll" = HeapHistogram.Share.PerSession @>
    test <@ share "MetadataReader" "System.Reflection.Metadata.dll" = HeapHistogram.Share.Metadata @>

[<Fact>]
let ``the shareable estimate is a range from certain metadata to everything not per session`` () =
    let stat name moduleName bytes : HeapHistogram.TypeStat =
        { TypeName = name
          Module = moduleName
          Bytes = bytes
          Count = 1L }

    let e =
        HeapHistogram.estimate
            [ stat "ILTypeDef" "FSharp.Compiler.Service.dll" 200L
              stat "SynExpr" "FSharp.Compiler.Service.dll" 300L
              stat "TType_app" "FSharp.Compiler.Service.dll" 400L
              stat "System.String" "System.Private.CoreLib.dll" 100L ]

    test <@ e.TotalBytes = 1000L @>
    test <@ e.ShareableLow = 0.2 @>
    test <@ e.ShareableHigh = 0.7 @>

[<Fact>]
let ``an empty histogram estimates zero rather than dividing by zero`` () =
    let e = HeapHistogram.estimate []
    test <@ e.ShareableLow = 0.0 && e.ShareableHigh = 0.0 @>

[<Fact>]
let ``a heap walk reconciles against the GC's live estimate, heap less fragmentation`` () =
    // Calibration process: walk 58.7 MB, heap 61.7 MB, ~3.6% free.
    test <@ HeapHistogram.reconcile 58_672_454L 61_702_248L (Some 3.6) = Ok() @>
    // Real daemon: walk 806 MB of a 1,986 MB heap is right when 59% of the heap is free …
    test <@ HeapHistogram.reconcile 805_738_545L 1_986_495_184L (Some 59.4) = Ok() @>
    // … and is a lost walk when the GC says the heap is nearly all live.
    test <@ Result.isError (HeapHistogram.reconcile 805_738_545L 1_986_495_184L (Some 2.0)) @>
    test <@ Result.isError (HeapHistogram.reconcile 805_738_545L 1_986_495_184L None) @>

[<Fact>]
let ``a walk larger than the heap, or with no heap to compare, is never trusted`` () =
    test <@ Result.isError (HeapHistogram.reconcile 101L 100L (Some 0.0)) @>
    test <@ Result.isError (HeapHistogram.reconcile 10L 0L None) @>

[<Fact>]
let ``load, free memory and swap parse from the macOS tools' own text`` () =
    test <@ Load.parseLoadAvg "{ 37.03 116.03 149.15 }\n" = Some(37.03, 116.03, 149.15) @>
    test <@ Load.parseLoadAvg "garbage" = None @>

    test
        <@
            Load.parseMemFreePercent
                "The system has 34359738368 (2097152 pages)\nSystem-wide memory free percentage: 68%" = Some 68
        @>

    test <@ Load.parseSwapUsed "total = 7168.00M  used = 1024.00M  free = 877.19M  (encrypted)" = Some(1073741824L) @>
    test <@ Load.parseSwapUsed "used = 2.00G" = Some(2147483648L) @>
    test <@ Load.parseSwapUsed "nothing" = None @>

let private snapshot load1 free foreign : Load.Snapshot =
    { Load1 = load1
      Load5 = 0.0
      Load15 = 0.0
      Cpus = 12
      MemBytes = 0L
      MemFreePercent = free
      SwapUsedBytes = None
      ForeignDaemons = foreign
      PowerSource = Some "AC Power" }

[<Fact>]
let ``a quiet box has no contention reasons and each breach is named`` () =
    test <@ List.isEmpty (Load.contention Load.defaultBar (snapshot 2.0 (Some 60) [])) @>
    test <@ List.length (Load.contention Load.defaultBar (snapshot 229.6 (Some 20) [ 87070 ])) = 3 @>
    test <@ List.isEmpty (Load.contention Load.defaultBar (snapshot 5.0 None [])) @>

[<Fact>]
let ``the power source is read from pmset's first line`` () =
    test <@ Load.parsePowerSource "Now drawing from 'AC Power'\n -InternalBattery-0 (id=1)\t49%" = Some "AC Power" @>
    test <@ Load.parsePowerSource "Now drawing from 'Battery Power'\n" = Some "Battery Power" @>
    test <@ Load.parsePowerSource "garbage" = None @>

[<Fact>]
let ``a sample on battery is contended even when the load bar is off`` () =
    let onBattery =
        { snapshot 1.0 (Some 60) [] with
            PowerSource = Some "Battery Power" }

    let noLoadBar: Load.QuietBar =
        { MaxLoadPerCpu = System.Double.PositiveInfinity
          MinMemFreePercent = 0 }

    test
        <@
            Load.contention Load.defaultBar onBattery = [ "on Battery Power, not AC: throttling and sleep invalidate latency" ]
        @>

    test
        <@ Load.contention noLoadBar onBattery = [ "on Battery Power, not AC: throttling and sleep invalidate latency" ] @>
    // An unreadable power source (a desktop with no battery report) is not contention.
    test <@ List.isEmpty (Load.contention Load.defaultBar { onBattery with PowerSource = None }) @>

[<Fact>]
let ``--max-load sets the load1 ceiling and keeps every other quiet check`` () =
    test <@ Load.barFor None 12 = Load.defaultBar @>
    let loose = Load.barFor (Some 8.0) 12

    test
        <@
            loose.MaxLoadPerCpu * 12.0 = 8.0
            && loose.MinMemFreePercent = Load.defaultBar.MinMemFreePercent
        @>

    test <@ List.isEmpty (Load.contention loose (snapshot 7.5 (Some 60) [])) @>
    test <@ List.length (Load.contention loose (snapshot 8.5 (Some 60) [])) = 1 @>
    test <@ List.length (Load.contention loose (snapshot 7.5 (Some 60) [ 42 ])) = 1 @>
