/// `fshw-bench audit`: the clean-recompute audit (ADR-059 §35, AUTOMATION-1633) from the
/// harness, so a consumer that pins an older fshw can run it with the harness built from
/// FsHotWatch's main, beside `replay --forced-clean-home`, which files the clean store.
module FsHotWatch.Bench.Audit

open FsHotWatch

/// The request `opts` (`--key value` arguments) describe.
let request (opts: Map<string, string list>) : RecordAudit.Request =
    let one key =
        opts |> Map.tryFind key |> Option.bind List.tryLast

    let all key =
        opts |> Map.tryFind key |> Option.defaultValue []

    { Clean = one "clean"
      CleanHome = one "clean-home"
      Store = one "store"
      Kinds = all "kind"
      Sample =
        one "sample"
        |> Option.map (fun text ->
            match System.Int32.TryParse text with
            | true, n -> n
            | false, _ -> -1)
      Seed = one "seed"
      Keys = all "key"
      PreferInput = one "prefer-input" }

/// Audit `target`'s store; the exit code (0 agree, 1 mismatch, 3 vacuous, 2 usage).
let run (target: string) (opts: Map<string, string list>) : int =
    RecordAudit.runRequest
        target
        (request opts)
        (opts.ContainsKey "json")
        (opts |> Map.tryFind "out" |> Option.bind List.tryLast)
        (printfn "%s")
        (eprintfn "fshw-bench %s")
