/// `fshw-bench` — see bench/README.md.
module FsHotWatch.Bench.Program

open System
open System.IO

let private usage =
    """fshw-bench — repository-host memory / scan / throughput benchmark

  fshw-bench run --repo <path> [--rev <rev>] [--sessions 1,2,4] [--reps 5]
                 [--out <jsonl>] [--label per-worktree] [--cli <FsHotWatch.Cli.dll>]
                 [--prepare "<shell cmd>"] [--strip <key>]... [--set <dotted.path>=<json>]... [--env KEY=VALUE]... [--tests] [--no-heap]
                 [--warm-cache] [--settle-sec 60] [--sample-sec 5]
                 [--scan-timeout-min 60] [--test-timeout-min 120]
                 [--keep-worktrees] [--allow-contended] [--keep-traces <dir>] [--no-retention]
                 [--mode legacy|host] [--edits K --edit-file <worktree-relative path>]
                 [--settle-timeout-sec 120] [--max-load <load1 ceiling>]
  fshw-bench probe --pid <pid> [--port <socket>] [--worktree <path>] [--no-heap] [--no-retention]
                   [--out <jsonl>] [--label <text>]
  fshw-bench port --pid <pid>
  fshw-bench graph <kept.nettrace> [--roots TypeA,TypeB] [--top 40]
                   [--path-from TypeA --path-to TypeB [--block TypeC,…]]
  fshw-bench summarize <jsonl> [--allow-contended]
"""

/// Parse `--key value` / `--flag` arguments. Repeated keys accumulate.
let parseArgs (args: string list) : Map<string, string list> * string list =
    let rec go (acc: Map<string, string list>) (positional: string list) (rest: string list) =
        match rest with
        | [] -> acc, List.rev positional
        | key :: value :: tail when
            key.StartsWith("--", StringComparison.Ordinal)
            && not (value.StartsWith("--", StringComparison.Ordinal))
            ->
            let k = key.Substring 2
            let existing = acc |> Map.tryFind k |> Option.defaultValue []
            go (acc |> Map.add k (existing @ [ value ])) positional tail
        | key :: tail when key.StartsWith("--", StringComparison.Ordinal) ->
            go (acc |> Map.add (key.Substring 2) [ "true" ]) positional tail
        | value :: tail -> go acc (value :: positional) tail

    go Map.empty [] args

let private one (opts: Map<string, string list>) (key: string) =
    opts |> Map.tryFind key |> Option.bind List.tryLast

let private flag (opts: Map<string, string list>) (key: string) = (one opts key).IsSome

let private defaultCli () =
    // The CLI built from this checkout, next to the harness's own tree.
    let here = AppContext.BaseDirectory
    let root = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."))
    Path.Combine(root, "src", "FsHotWatch.Cli", "bin", "Debug", "net10.0", "FsHotWatch.Cli.dll")

[<EntryPoint>]
let main argv =
    // The harness spawns short-lived tools (footprint, lsof, kill) through the core's
    // ProcessHelper outside any daemon's process registry; its per-spawn warning about
    // that is noise here.
    FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

    let command, rest =
        match List.ofArray argv with
        | c :: r -> c, r
        | [] -> "", []

    let opts, positional = parseArgs rest

    match command with
    | "run" ->
        match one opts "repo" with
        | None ->
            eprintf "%s" usage
            2
        | Some repo ->
            let repo = Path.GetFullPath repo

            // Every --set is parsed before anything is created: a typo exits here, not
            // after worktrees were built.
            let sets =
                opts
                |> Map.tryFind "set"
                |> Option.defaultValue []
                |> List.map ConfigOverride.parseSet

            let envs =
                opts
                |> Map.tryFind "env"
                |> Option.defaultValue []
                |> List.map (fun e ->
                    match Scenario.parseEnv e with
                    | Ok kv -> kv
                    | Error msg ->
                        eprintfn "%s" msg
                        exit 2)

            if
                (one opts "edits" |> Option.map int |> Option.defaultValue 0) > 0
                && (one opts "edit-file").IsNone
            then
                eprintfn "--edits needs --edit-file <worktree-relative path>"
                exit 2

            match
                sets
                |> List.choose (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | first :: _ ->
                eprintfn "%s" first
                exit 2
            | [] -> ()

            Scenario.runMatrix
                { Repo = repo
                  Rev = one opts "rev" |> Option.defaultValue "@-"
                  Sessions =
                    one opts "sessions"
                    |> Option.defaultValue "1,2,4"
                    |> fun s -> s.Split(',') |> Array.map int |> Array.toList
                  Reps = one opts "reps" |> Option.map int |> Option.defaultValue 5
                  Out =
                    one opts "out"
                    |> Option.defaultValue (Path.Combine(repo, ".fshw", "bench", "memory.jsonl"))
                  Label = one opts "label" |> Option.defaultValue "per-worktree"
                  Cli = one opts "cli" |> Option.defaultValue (defaultCli ())
                  Prepare = one opts "prepare" |> Option.defaultValue "dotnet build"
                  Strip = opts |> Map.tryFind "strip" |> Option.defaultValue []
                  Set = sets |> List.choose Result.toOption
                  Env = envs
                  Tests = flag opts "tests"
                  Heap = not (flag opts "no-heap")
                  WarmCache = flag opts "warm-cache"
                  SettleSec = one opts "settle-sec" |> Option.map int |> Option.defaultValue 60
                  SampleSec = one opts "sample-sec" |> Option.map int |> Option.defaultValue 5
                  ScanTimeout =
                    TimeSpan.FromMinutes(one opts "scan-timeout-min" |> Option.map float |> Option.defaultValue 60.0)
                  TestTimeout =
                    TimeSpan.FromMinutes(one opts "test-timeout-min" |> Option.map float |> Option.defaultValue 120.0)
                  KeepWorktrees = flag opts "keep-worktrees"
                  AllowContended = flag opts "allow-contended"
                  KeepTraces = one opts "keep-traces"
                  Retention = not (flag opts "no-retention")
                  Mode =
                    match one opts "mode" with
                    | Some "host" -> Scenario.RunMode.Host
                    | Some "legacy"
                    | None -> Scenario.RunMode.Legacy
                    | Some other ->
                        eprintfn "--mode must be legacy or host, not %s" other
                        exit 2
                  Edits = one opts "edits" |> Option.map int |> Option.defaultValue 0
                  EditFile = one opts "edit-file"
                  MaxLoad1 = one opts "max-load" |> Option.map float
                  SettleTimeout =
                    TimeSpan.FromSeconds(one opts "settle-timeout-sec" |> Option.map float |> Option.defaultValue 120.0) }
    | "probe" ->
        match one opts "pid" with
        | None ->
            eprintf "%s" usage
            2
        | Some pid ->
            let worktree = one opts "worktree" |> Option.map Path.GetFullPath

            let out =
                one opts "out"
                |> Option.orElse (
                    worktree
                    |> Option.map (fun wt -> Path.Combine(wt, ".fshw", "bench", "memory.jsonl"))
                )
                |> Option.defaultValue "bench-memory.jsonl"

            Scenario.probe
                out
                (one opts "label" |> Option.defaultValue "probe")
                (int pid)
                (one opts "port")
                worktree
                (not (flag opts "no-heap"))
                (not (flag opts "no-retention"))
    | "port" ->
        match one opts "pid" |> Option.map int with
        | None ->
            eprintf "%s" usage
            2
        | Some pid ->
            match Instruments.diagnosticPort pid None with
            | Ok p ->
                printfn "%s" p
                0
            | Error e ->
                eprintfn "%s" e
                1
    | "graph" ->
        match positional with
        | [ trace ] ->
            match Instruments.readTrace trace true with
            | Error e ->
                eprintfn "%s" e
                1
            | Ok { Graph = Some(g, report) } ->
                let roots =
                    one opts "roots"
                    |> Option.map (fun r -> r.Split(',') |> Array.toList)
                    |> Option.defaultValue []

                let top = one opts "top" |> Option.map int |> Option.defaultValue 40

                match one opts "path-from", one opts "path-to" with
                | Some from, Some target ->
                    let block =
                        one opts "block"
                        |> Option.map (fun b -> b.Split(',') |> Array.toList)
                        |> Option.defaultValue []

                    printfn "%s" (GraphReport.path g from target block)
                | _ -> printfn "%s" (GraphReport.render g report roots top)

                0
            | Ok _ ->
                eprintfn "no graph in %s" trace
                1
        | _ ->
            eprintf "%s" usage
            2
    | "summarize" ->
        match positional with
        | [ file ] ->
            let rows = Record.parseSeries (File.ReadAllText file)
            printfn "%s" (Summary.render (Summary.summarize (flag opts "allow-contended") rows))
            0
        | _ ->
            eprintf "%s" usage
            2
    | _ ->
        eprintf "%s" usage
        2
