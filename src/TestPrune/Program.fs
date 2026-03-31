module TestPrune.Program

open System
open System.Diagnostics
open System.IO
open TestPrune.AuditSink
open TestPrune.Database
open TestPrune.Orchestration
open TestPrune.ProjectLoader

type Command =
    | Index
    | Run
    | Status
    | DeadCodeCmd of entryPatterns: string list * includeTests: bool * verbose: bool
    | Help

let rec private parseDeadCodeFlags (args: string list) (acc: string list) (includeTests: bool) (verbose: bool) =
    match args with
    | "--entry" :: pattern :: rest -> parseDeadCodeFlags rest (pattern :: acc) includeTests verbose
    | "--include-tests" :: rest -> parseDeadCodeFlags rest acc true verbose
    | "--verbose" :: rest -> parseDeadCodeFlags rest acc includeTests true
    | [] -> Ok(acc |> List.rev, includeTests, verbose)
    | unknown :: _ -> Error $"Unknown flag: %s{unknown}"

type ParsedCommand =
    { Command: Command
      RepoRoot: string option
      Parallelism: int }

let rec private parseGlobalFlags
    (args: string list)
    (repoRoot: string option)
    (parallelism: int option)
    : Result<string list * string option * int, string> =
    match args with
    | "--repo" :: path :: rest -> parseGlobalFlags rest (Some path) parallelism
    | "--parallelism" :: n :: rest ->
        match System.Int32.TryParse(n) with
        | true, value when value > 0 -> parseGlobalFlags rest repoRoot (Some value)
        | _ -> Error $"Invalid parallelism value: %s{n}"
    | _ -> Ok(args, repoRoot, parallelism |> Option.defaultValue Environment.ProcessorCount)

let parseArgs (args: string array) : Result<ParsedCommand, string> =
    match parseGlobalFlags (args |> Array.toList) None None with
    | Error msg -> Error msg
    | Ok(commandArgs, repoRoot, parallelism) ->
        let cmdResult =
            match commandArgs with
            | [] -> Ok Help
            | [ "index" ] -> Ok Index
            | [ "run" ] -> Ok Run
            | [ "status" ] -> Ok Status
            | "dead-code" :: rest ->
                match parseDeadCodeFlags rest [] false false with
                | Ok([], includeTests, verbose) -> Ok(DeadCodeCmd(defaultEntryPatterns, includeTests, verbose))
                | Ok(patterns, includeTests, verbose) -> Ok(DeadCodeCmd(patterns, includeTests, verbose))
                | Error msg -> Error msg
            | [ "help" ]
            | [ "--help" ]
            | [ "-h" ] -> Ok Help
            | unknown :: _ -> Error $"Unknown command: %s{unknown}"

        cmdResult
        |> Result.map (fun cmd ->
            { Command = cmd
              RepoRoot = repoRoot
              Parallelism = parallelism })

let showHelp () =
    printfn "TestPrune - Test impact analysis tool"
    printfn ""
    printfn "Usage: test-prune [--repo <path>] <command>"
    printfn ""
    printfn "Global options:"
    printfn "  --repo <path>         Use <path> as the repo root (default: auto-detect from cwd)"
    printfn "  --parallelism <n>     Max parallel project analyses (default: processor count)"
    printfn ""
    printfn "Commands:"
    printfn "  index      Build the dependency graph from source"
    printfn "  run        Run affected tests based on changes"
    printfn "  status     Show what tests would run (dry-run)"
    printfn "  dead-code  Detect unreachable symbols from entry points"
    printfn "  help       Show this help message"
    printfn ""
    printfn "dead-code options:"
    printfn "  --entry <pattern>   Add entry point pattern (repeatable)"
    printfn "                      Default: *.main, *.Program.*, *.Routes.*, *.Scheduler.*"
    printfn "  --include-tests     Include symbols from test files in dead code report"
    printfn "  --verbose           Show why each symbol is unreachable"

let private buildTimeoutMs = 600_000 // 10 minutes

/// Default build runner: runs `dotnet build` on the solution with a 10-minute timeout.
/// Reads stdout and stderr asynchronously to avoid deadlock when buffers fill.
let dotnetBuildRunner: BuildRunner =
    fun (repoRoot: string) ->
        let slnFiles =
            [| "*.slnx"; "*.sln" |]
            |> Array.collect (fun pattern -> Directory.GetFiles(repoRoot, pattern))

        let slnPath = if slnFiles.Length > 0 then slnFiles.[0] else repoRoot

        let buildPsi = ProcessStartInfo("dotnet", $"build \"%s{slnPath}\" -v quiet")
        buildPsi.UseShellExecute <- false
        buildPsi.RedirectStandardOutput <- true
        buildPsi.RedirectStandardError <- true

        use buildProc = Process.Start(buildPsi)
        let sw = Diagnostics.Stopwatch.StartNew()

        // Read async to avoid deadlock if a buffer fills while waiting for the other.
        let stdoutTask = buildProc.StandardOutput.ReadToEndAsync()
        let stderrTask = buildProc.StandardError.ReadToEndAsync()

        let completed = buildProc.WaitForExit(buildTimeoutMs)

        if not completed then
            buildProc.Kill(entireProcessTree = true)
            eprintfn $"Build timed out after {buildTimeoutMs / 60_000} minutes — aborting index"
            1
        else
            let stdoutOutput = stdoutTask.Result
            let stderrOutput = stderrTask.Result
            sw.Stop()

            if buildProc.ExitCode <> 0 then
                if not (String.IsNullOrWhiteSpace(stdoutOutput)) then
                    eprintfn "%s" stdoutOutput

                if not (String.IsNullOrWhiteSpace(stderrOutput)) then
                    eprintfn "%s" stderrOutput

            eprintfn $"[dotnet build] \u2192 exit %d{buildProc.ExitCode} in %.1f{sw.Elapsed.TotalSeconds}s"
            buildProc.ExitCode

let private createAuditSinkForRepo (repoRoot: string) =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if File.Exists(dbPath) then
        let db = Database.create dbPath
        let runId = System.Guid.NewGuid().ToString("N").[..7]
        createSqliteSink db.InsertEvent runId
    else
        createNoopSink ()

/// Run the index command: build projects, then parse with real project options.
let runIndex (repoRoot: string) (parallelism: int) : int =
    let checker = createChecker ()
    let auditSink = createAuditSinkForRepo repoRoot
    runIndexWith dotnetBuildRunner getProjectOptions repoRoot checker parallelism auditSink

/// Get jj diff output.
let jjDiffProvider: DiffProvider =
    fun () ->
        try
            let psi = ProcessStartInfo("jj", "diff --git")
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            use proc = Process.Start(psi)
            let output = proc.StandardOutput.ReadToEnd()
            let _stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            if proc.ExitCode = 0 then
                Ok output
            else
                Error "jj diff failed — is this a jj repository?"
        with ex ->
            Error $"Failed to run jj: %s{ex.Message}"

/// Run the status command: show what would run without executing.
let runStatus (repoRoot: string) : int =
    let auditSink = createAuditSinkForRepo repoRoot
    runStatusWith jjDiffProvider repoRoot auditSink

/// Run the run command: determine and execute affected tests.
let runRun (repoRoot: string) : int =
    let auditSink = createAuditSinkForRepo repoRoot
    runRunWith jjDiffProvider repoRoot auditSink

let runCommand (parsed: ParsedCommand) : int =
    let repoRoot =
        match parsed.RepoRoot with
        | Some path -> Path.GetFullPath(path)
        | None ->
            match findRepoRoot (Directory.GetCurrentDirectory()) with
            | Some root -> root
            | None ->
                eprintfn "Error: not in a jj or git repository"
                Environment.Exit(1)
                "" // unreachable

    match parsed.Command with
    | Index -> runIndex repoRoot parsed.Parallelism
    | Run -> runRun repoRoot
    | Status -> runStatus repoRoot
    | DeadCodeCmd(patterns, includeTests, verbose) ->
        let auditSink = createAuditSinkForRepo repoRoot
        runDeadCode repoRoot patterns includeTests verbose auditSink
    | Help ->
        showHelp ()
        0

[<EntryPoint>]
let main args =
    match parseArgs args with
    | Ok parsed -> runCommand parsed
    | Error message ->
        eprintfn $"Error: %s{message}"
        showHelp ()
        1
