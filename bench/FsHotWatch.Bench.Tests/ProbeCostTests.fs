module FsHotWatch.Bench.Tests.ProbeCostTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

[<Fact>]
let ``the key probe finds every filed record and reads no payload, where a read reads them all`` () =
    let root =
        Path.Combine(Path.GetTempPath(), "fshw-probe-cost-" + System.Guid.NewGuid().ToString "N")

    try
        match ProbeCost.compare root 20 4096 with
        | [ read; probe ] ->
            test <@ read.Hits = 20 && probe.Hits = 20 @>
            test <@ read.PayloadBytesRead >= 20L * 4096L @>
            test <@ probe.PayloadBytesRead = 0L @>
            test <@ probe.AllocatedBytes < read.AllocatedBytes @>
            test <@ (ProbeCost.render probe).Contains "keys=20" @>
        | other -> failwith $"expected two readings, got %d{other.Length}"
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)
