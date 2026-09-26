/// Entry point of the `test-prune-traces` tool.
module TestPrune.Trace.Cli.Program

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
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

/// The process-driving measurements the verbs run (substituted in tests).
type Measures =
    { Audit: TraceSession.PrepareRequest -> string list -> float -> int -> TimeSpan -> Result<Audit.AuditReport, string>
      Overhead: TraceSession.PrepareRequest -> string list -> int -> TimeSpan -> Result<Overhead.OverheadReport, string>
      FileCensus: TraceSession.PrepareRequest -> string list -> TimeSpan -> Result<FileCensus.FileCensusReport, string> }

/// The real measurements.
let measures =
    { Audit = Audit.run
      Overhead = Overhead.run
      FileCensus = FileCensus.run }

let private measureTail =
    "  [--repo <root>] [--timeout-min 30] [--json] [-- <app args>]\n"
    + "  exit 0 when the project meets the bar, 1 when it does not, 2 on a usage error or a project that cannot be measured"

/// The usage of the audit verb.
let auditUsage =
    "usage: test-prune-traces audit --project-dir <dir> --assembly <name> [--sample 0.01] [--seed 1]\n"
    + measureTail

/// The usage of the overhead verb.
let overheadUsage =
    "usage: test-prune-traces overhead --project-dir <dir> --assembly <name> [--reps 3]\n"
    + measureTail

/// The usage of the file-census verb.
let fileCensusUsage =
    "usage: test-prune-traces file-census --project-dir <dir> --assembly <name>\n"
    + measureTail

type private MeasureArgs =
    { ProjectDir: string option
      Assembly: string option
      Repo: string option
      Sample: float
      Seed: int
      Reps: int
      TimeoutMin: float
      Json: bool
      AppArgs: string list }

let private number (flag: string) (v: string) (set: float -> MeasureArgs) =
    match Double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, n -> Ok(set n)
    | _ -> Error $"%s{flag} needs a number, got %s{v}"

let private whole (flag: string) (v: string) (set: int -> MeasureArgs) =
    match Int32.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, n -> Ok(set n)
    | _ -> Error $"%s{flag} needs a whole number, got %s{v}"

/// Every valued flag of the measurement verbs and how it sets its argument.
let private setters: Map<string, string -> MeasureArgs -> Result<MeasureArgs, string>> =
    Map
        [ "--project-dir", (fun v a -> Ok { a with ProjectDir = Some v })
          "--assembly", (fun v a -> Ok { a with Assembly = Some v })
          "--repo", (fun v a -> Ok { a with Repo = Some v })
          "--sample", (fun v a -> number "--sample" v (fun n -> { a with Sample = n }))
          "--seed", (fun v a -> whole "--seed" v (fun n -> { a with Seed = n }))
          "--reps", (fun v a -> whole "--reps" v (fun n -> { a with Reps = n }))
          "--timeout-min", (fun v a -> number "--timeout-min" v (fun n -> { a with TimeoutMin = n })) ]

let private common = [ "--project-dir"; "--assembly"; "--repo"; "--timeout-min" ]

let rec private parseMeasure (flags: Set<string>) (acc: MeasureArgs) (args: string list) =
    match args with
    | [] -> Ok acc
    | "--" :: rest -> Ok { acc with AppArgs = rest }
    | "--json" :: rest -> parseMeasure flags { acc with Json = true } rest
    | flag :: v :: rest when flags.Contains flag && not (v.StartsWith "--") ->
        setters.[flag] v acc |> Result.bind (fun a -> parseMeasure flags a rest)
    | flag :: _ when flags.Contains flag -> Error $"%s{flag} needs a value"
    | other :: _ -> Error $"unknown argument %s{other}"

let private jsonOptions =
    JsonSerializerOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)

/// Parse a measurement verb's arguments, run it in a scratch run directory, and print
/// its report (or JSON) with the verdict as the exit code.
let private measure
    (cwd: string)
    (verbUsage: string)
    (flags: string list)
    (args: string list)
    (go: TraceSession.PrepareRequest -> MeasureArgs -> Result<'report, string>)
    (passes: 'report -> bool)
    (render: 'report -> string)
    : Outcome =
    let defaults =
        { ProjectDir = None
          Assembly = None
          Repo = None
          Sample = 0.01
          Seed = 1
          Reps = 3
          TimeoutMin = 30.0
          Json = false
          AppArgs = [] }

    let parsed =
        parseMeasure (Set.ofList (common @ flags)) defaults args
        |> Result.bind (fun a ->
            match a.ProjectDir, a.Assembly with
            | Some dir, Some name -> Ok(a, dir, name)
            | _ -> Error "--project-dir and --assembly are required")

    match parsed with
    | Error why -> fail 2 $"%s{why}\n%s{verbUsage}"
    | Ok(a, dir, name) ->
        let runDir = Directory.CreateTempSubdirectory("test-prune-traces-").FullName
        let timeout = TimeSpan.FromMinutes a.TimeoutMin

        let req: TraceSession.PrepareRequest =
            { RepoRoot = Path.GetFullPath(defaultArg a.Repo ".", cwd)
              ProjectDir = Path.GetFullPath(dir, cwd)
              AssemblyName = name
              TestProject = name
              WeaveTests = Model.SitesOnly
              RunDir = runDir
              VerifyTimeout = timeout }

        try
            match go req a with
            | Error why -> fail 2 why
            | Ok report ->
                { Exit = if passes report then 0 else 1
                  Stdout =
                    if a.Json then
                        JsonSerializer.Serialize(report, jsonOptions) + "\n"
                    else
                        render report
                  Stderr = "" }
        finally
            Directory.Delete(runDir, true)

/// Runs a verb against `cwd` with the given measurements, returning its output rather
/// than printing it.
let runWith (m: Measures) (cwd: string) (args: string list) : Outcome =
    match args with
    | "census" :: rest -> census cwd rest
    | "audit" :: rest ->
        measure
            cwd
            auditUsage
            [ "--sample"; "--seed" ]
            rest
            (fun req a -> m.Audit req a.AppArgs a.Sample a.Seed (TimeSpan.FromMinutes a.TimeoutMin))
            Audit.passes
            Audit.render
    | "overhead" :: rest ->
        measure
            cwd
            overheadUsage
            [ "--reps" ]
            rest
            (fun req a -> m.Overhead req a.AppArgs a.Reps (TimeSpan.FromMinutes a.TimeoutMin))
            Overhead.passes
            Overhead.render
    | "file-census" :: rest ->
        measure
            cwd
            fileCensusUsage
            []
            rest
            (fun req a -> m.FileCensus req a.AppArgs (TimeSpan.FromMinutes a.TimeoutMin))
            FileCensus.passes
            FileCensus.render
    | _ -> fail 2 usage

/// Runs a verb against `cwd`, returning its output rather than printing it.
let run (cwd: string) (args: string list) : Outcome = runWith measures cwd args

/// Dispatches a verb; prints the usage and returns 2 for an unknown one.
[<EntryPoint>]
let main (argv: string array) =
    let o = run Environment.CurrentDirectory (List.ofArray argv)
    Console.Out.Write o.Stdout
    Console.Error.Write o.Stderr
    o.Exit
