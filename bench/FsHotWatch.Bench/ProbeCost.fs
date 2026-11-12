/// What a warm start pays to ask "is this analysis filed?" once per file (AUTOMATION-1609).
///
/// A store of `count` synthetic records, each with `outputBytes` of outputs, is filed under
/// a temp root. Every key is then asked twice: by READING its record (`LookupWithOutputs`,
/// which reads, checksums and decodes the whole file) and by the key-only probe
/// (`HoldsKey`, which only lists the key's directory). For each: the payload bytes it read
/// (a read opens the whole file; the probe opens none) and the bytes it allocated on the
/// measuring thread. The read is a LOWER bound of what TestPrune's reader paid: it also
/// parsed the outputs into an analysis.
///
/// A number, not a gate: see `HashCost`.
module FsHotWatch.Bench.ProbeCost

open System
open System.Diagnostics
open System.IO
open FsHotWatch

/// One way of asking, over every key.
type Reading =
    { Label: string
      Keys: int
      Hits: int
      PayloadBytesRead: int64
      AllocatedBytes: int64
      Elapsed: TimeSpan }

/// File `count` records of about `outputBytes` of outputs each in `store`; their keys.
let seed (store: OutcomeStore.Store) (count: int) (outputBytes: int) : ActionKey.ActionKey list =
    let filler = String('x', max 0 (outputBytes - 64))

    [ for i in 1..count do
          let key = ActionKey.create "probe-cost" [ "file", $"src/File%d{i}.fs" ]

          let record =
              OutcomeStore.Record.ofAction
                  key
                  $"src/File%d{i}.fs"
                  OutcomeStore.Outcome.Pass
                  "bench"
                  "/bench"
                  DateTimeOffset.UtcNow

          let outputs = $"{{\"schema\":\"probe-cost\",\"n\":%d{i},\"blob\":\"%s{filler}\"}}"

          if not (store.RecordWithOutputs(record, Some outputs)) then
              failwith $"could not file record %d{i}"

          key ]

/// Ask `holds` of every key; `opens` is whether asking reads the key's record files whole.
let measure
    (label: string)
    (store: OutcomeStore.Store)
    (opens: bool)
    (holds: ActionKey.ActionKey -> bool)
    (keys: ActionKey.ActionKey list)
    : Reading =
    let payloadBytes =
        if opens then
            keys
            |> List.sumBy (fun key ->
                Directory.GetFiles(store.DirectoryOf key, "*.json")
                |> Array.sumBy (fun file -> FileInfo(file).Length))
        else
            0L

    GC.Collect()
    GC.WaitForPendingFinalizers()
    let before = GC.GetAllocatedBytesForCurrentThread()
    let clock = Stopwatch.StartNew()
    let hits = keys |> List.filter holds |> List.length
    clock.Stop()

    { Label = label
      Keys = keys.Length
      Hits = hits
      PayloadBytesRead = payloadBytes
      AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before
      Elapsed = clock.Elapsed }

/// The read and the probe over a fresh store of `count` records of `outputBytes` under `root`.
let compare (root: string) (count: int) (outputBytes: int) : Reading list =
    let store =
        OutcomeStore.Store(
            { Root = root
              Ttl = OutcomeStore.defaultTtl
              MaxBytes = Int64.MaxValue
              Log = ignore }
        )

    let keys = seed store count outputBytes

    [ measure "read (before)" store true (fun key -> (store.LookupWithOutputs key).IsSome) keys
      measure "key probe (after)" store false store.HoldsKey keys ]

/// One line per reading.
let render (reading: Reading) : string =
    let mb (bytes: int64) = float bytes / 1048576.0

    $"%-18s{reading.Label} keys=%d{reading.Keys} hits=%d{reading.Hits} payload-read=%.1f{mb reading.PayloadBytesRead} MB allocated=%.1f{mb reading.AllocatedBytes} MB elapsed=%.0f{reading.Elapsed.TotalMilliseconds} ms"
