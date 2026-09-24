module FsHotWatch.Bench.Tests.HashCostTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

// No test here bounds the measured cost. The whole reason the measurement lives in the
// harness is that a µs/call ceiling asserted on a shared box fails on load, not on code.

[<Fact>]
let ``the representative source is about the repository's average file size`` () =
    let source =
        HashCost.representativeInputs ()
        |> List.find (fun (label, _) -> label = "source")
        |> snd

    test <@ source.Length > 10_000 && source.Length < 16_000 @>

[<Fact>]
let ``a measurement reports the iterations it timed and a positive cost`` () =
    let reading = HashCost.measure 2 5 (HashCost.representativeInputs ())

    test <@ reading.Iterations = 5 @>
    test <@ reading.SourceBytes > 0 @>
    test <@ reading.PerCallMicros > 0.0 @>
    test <@ reading.Total > TimeSpan.Zero @>

[<Fact>]
let ``the rendered line carries the size, the per-call cost and the iteration count`` () =
    let line =
        HashCost.render
            { SourceBytes = 12480
              Iterations = 1000
              PerCallMicros = 42.31
              Total = TimeSpan.FromMilliseconds 42.31 }

    test <@ line = "merkleCacheKey on 12480-byte source: 42.3 µs/call (1000 iters in 42 ms)" @>
