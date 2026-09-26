/// Entry point of the `test-prune-traces` tool.
module TestPrune.Trace.Cli.Program

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open TestPrune.Trace

/// The usage line printed for an unknown or missing verb.
let usage = "usage: test-prune-traces census|audit|overhead|file-census [options]"

/// The usage of the census verb.
let censusUsage =
    "usage: test-prune-traces census [--db <path>] [--run <runId>] [--json]\n"
    + "  exit 0 when every project meets the phase-1 bars, 1 otherwise, 2 on a usage error or an unreadable database"

/// What a verb printed and its exit code.
type Outcome =
    { Exit: int
      Stdout: string
      Stderr: string }

let private fail code (message: string) =
    { Exit = code
      Stdout = ""
      Stderr = message + "\n" }

/// fshw's trace database when it exists under `cwd`, else the CLI's.
let private defaultDb (cwd: string) =
    let fshw = Path.Combine(cwd, ".fshw", "test-traces.db")

    if File.Exists fshw then
        fshw
    else
        Path.Combine(cwd, ".test-prune-traces.db")

type private CensusArgs =
    { Db: string option
      Run: string option
      Json: bool }

let rec private parseCensus (acc: CensusArgs) (args: string list) : Result<CensusArgs, string> =
    match args with
    | [] -> Ok acc
    | "--db" :: v :: rest when not (v.StartsWith "--") -> parseCensus { acc with Db = Some v } rest
    | "--run" :: v :: rest when not (v.StartsWith "--") -> parseCensus { acc with Run = Some v } rest
    | "--json" :: rest -> parseCensus { acc with Json = true } rest
    | ("--db" | "--run") as flag :: _ -> Error $"%s{flag} needs a value"
    | other :: _ -> Error $"unknown argument %s{other}"

let private census (cwd: string) (args: string list) : Outcome =
    match parseCensus { Db = None; Run = None; Json = false } args with
    | Error why -> fail 2 $"%s{why}\n%s{censusUsage}"
    | Ok a ->
        let db = a.Db |> Option.defaultWith (fun () -> defaultDb cwd)

        try
            match Census.latest db a.Run with
            | [] ->
                let which = a.Run |> Option.map (sprintf "run %s") |> Option.defaultValue "any run"

                fail 1 $"no recorded run (%s{which}) in %s{db}: nothing to measure"
            | cs ->
                let bars = Census.defaultBars

                { Exit = if cs |> List.forall (Census.passes bars) then 0 else 1
                  Stdout =
                    if a.Json then
                        JsonSerializer.Serialize(cs |> List.map (Census.report bars)) + "\n"
                    else
                        Census.render cs
                  Stderr = "" }
        with e ->
            match e with
            | :? FileNotFoundException
            | TraceStore.TraceSchemaNewerThanConsumer _
            | :? SqliteException -> fail 2 e.Message
            // Unexpected content (a stats_json this version cannot read): keep the whole trace.
            | _ -> fail 2 $"%s{db}: %O{e}"

/// Runs a verb against `cwd`, returning its output rather than printing it.
let run (cwd: string) (args: string list) : Outcome =
    match args with
    | "census" :: rest -> census cwd rest
    | _ -> fail 2 usage

/// Dispatches a verb; prints the usage and returns 2 for an unknown one.
[<EntryPoint>]
let main (argv: string array) =
    let o = run Environment.CurrentDirectory (List.ofArray argv)
    Console.Out.Write o.Stdout
    Console.Error.Write o.Stderr
    o.Exit
