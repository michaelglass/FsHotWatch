/// Per-`FileChecked` hashing cost: how long `TaskCache.merkleCacheKey` takes on a
/// representative source file.
///
/// A number, not a verdict. A throughput bound measured on shared hardware is a
/// function of what else the box is doing, so it cannot decide pass/fail: asserted in
/// the unit suite, it fails on load and passes alone. It is reported here instead,
/// beside the other measurements this harness takes, for a reader who runs it on a
/// quiet box and compares against the last reading they took.
module FsHotWatch.Bench.HashCost

open System
open System.Diagnostics
open FsHotWatch.TaskCache

/// One measurement of `merkleCacheKey` over a fixed input set.
type Reading =
    {
        /// Bytes in the synthetic source the key hashed.
        SourceBytes: int
        /// Timed calls (after the untimed warm-up).
        Iterations: int
        /// Mean wall-clock cost per call across the timed iterations.
        PerCallMicros: float
        /// Wall-clock for all timed iterations.
        Total: TimeSpan
    }

/// The repository's average `.fs` file is about 12 KB; the key hashes a source of that
/// size plus the labels the lint plugin keys a per-file entry by.
let representativeInputs () : (string * string) list =
    let source =
        String.replicate 240 "let aReasonablyLongIdentifier = someValue + otherValue\n"

    [ "plugin-version", "lint-merkle-v1"
      "tool", "1.2.3.4"
      "config", "abc123def456"
      "file", "/Users/me/repo/src/SomeModule/SomeFile.fs"
      "source", source ]

/// Time `iterations` calls of `merkleCacheKey` over `inputs`, after `warmup` untimed
/// calls so JIT and allocator warm-up are not in the number.
let measure (warmup: int) (iterations: int) (inputs: (string * string) list) : Reading =
    for _ in 1..warmup do
        merkleCacheKey inputs |> ignore

    let clock = Stopwatch.StartNew()

    for _ in 1..iterations do
        merkleCacheKey inputs |> ignore

    clock.Stop()

    let sourceBytes =
        inputs
        |> List.tryFind (fun (label, _) -> label = "source")
        |> Option.map (fun (_, value) -> value.Length)
        |> Option.defaultValue 0

    { SourceBytes = sourceBytes
      Iterations = iterations
      PerCallMicros = clock.Elapsed.TotalMicroseconds / float iterations
      Total = clock.Elapsed }

/// One line, in the shape the retired test printed, so readings stay comparable with
/// the ones already recorded.
let render (reading: Reading) : string =
    sprintf
        "merkleCacheKey on %d-byte source: %.1f µs/call (%d iters in %d ms)"
        reading.SourceBytes
        reading.PerCallMicros
        reading.Iterations
        (int reading.Total.TotalMilliseconds)
