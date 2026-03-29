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
    | DeadCodeCmd of entryPatterns: string list * includeTests: bool
    | Help

let rec private parseDeadCodeFlags (args: string list) (acc: string list) (includeTests: bool) =
    match args with
    | "--entry" :: pattern :: rest -> parseDeadCodeFlags rest (pattern :: acc) includeTests
    | "--include-tests" :: rest -> parseDeadCodeFlags rest acc true
    | [] -> Ok(acc |> List.rev, includeTests)
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
                match parseDeadCodeFlags rest [] false with
                | Ok([], includeTests) -> Ok(DeadCodeCmd(defaultEntryPatterns, includeTests))
                | Ok(patterns, includeTests) -> Ok(DeadCodeCmd(patterns, includeTests))
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

/// Default build runner: runs `dotnet build` on the solution.
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
        buildProc.StandardOutput.ReadToEnd() |> ignore
        buildProc.StandardError.ReadToEnd() |> ignore
        buildProc.WaitForExit()
        buildProc.ExitCode

let private createAuditSinkForRepo (repoRoot: string) =
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")
    let db = Database.create dbPath
    let runId = System.Guid.NewGuid().ToString("N").[..7]
    createSqliteSink db.InsertEvent runId

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
    | DeadCodeCmd(patterns, includeTests) ->
        let auditSink = createAuditSinkForRepo repoRoot
        runDeadCode repoRoot patterns includeTests auditSink
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
