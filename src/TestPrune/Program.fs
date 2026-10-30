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

let private buildTimeoutMs = 600_000

/// Hang-detector timeout for the `jj diff` spawn, in milliseconds. `jj diff` is
/// normally near-instant, so 60s is generous; it only fires if jj is wedged.
let private jjDiffTimeoutMs = 60_000

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
        let sw = Stopwatch.StartNew()

        // Read async to avoid deadlock if a buffer fills while waiting for the other.
        let stdoutTask = buildProc.StandardOutput.ReadToEndAsync()
        let stderrTask = buildProc.StandardError.ReadToEndAsync()

        let completed = buildProc.WaitForExit(buildTimeoutMs)

        if not completed then
            buildProc.Kill(entireProcessTree = true)
            eprintfn $"Build timed out after {buildTimeoutMs / 60_000} minutes — aborting index"
            1
        else
            // Bound the post-exit drain: `dotnet build` spawns MSBuild-worker / VBCSCompiler
            // grandchildren that inherit stdout and can outlive the direct build process,
            // wedging an unbounded read forever. The verdict here is the EXIT CODE, so a
            // drain-timeout keeps the same exit-code path with the partial output (the helper
            // emits its own diagnostic) — it must NOT turn a successful build into a failure.
            // Only the diff path treats the drained TEXT as authoritative and maps a wedge to
            // Error; see runBoundedDiff.
            let drain =
                TestRunner.drainOutputWithin TestRunner.drainOutputTimeoutMs "dotnet build" stdoutTask stderrTask

            let stdoutOutput, stderrOutput = drain.Stdout, drain.Stderr

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

/// Run `command` with `sink`, then flush the sink so the events the command queued are
/// persisted before the process exits. A flush that times out is reported on stderr; the
/// command's exit code stands, because a missing audit record does not change what the
/// command did. The flush is a parameter so tests can use a short bound.
let runWithAuditSink (flush: AuditSink -> FlushOutcome) (sink: AuditSink) (command: AuditSink -> int) : int =
    let exitCode = command sink

    match flush sink with
    | Flushed -> ()
    | FlushTimedOut _ ->
        eprintfn "Warning: the audit sink did not finish writing; audit events for this run may be missing"

    exitCode

/// Run `command` with the repo's audit sink, flushed within the sink's production bound.
let private withRepoAuditSink (repoRoot: string) (command: AuditSink -> int) : int =
    runWithAuditSink (fun sink -> sink.Flush()) (createAuditSinkForRepo repoRoot) command

/// Run the index command: build projects, then parse with real project options.
let runIndex (repoRoot: string) (parallelism: int) : int =
    withIndexLease repoRoot (fun () ->
        // The lease covers Database.create in the audit sink too: on first upgrade that
        // call may recreate the entire v12 cache before runOwnedIndexWith opens it.
        let checker = createChecker ()
        withRepoAuditSink repoRoot (runOwnedIndexWith dotnetBuildRunner getProjectOptions repoRoot checker parallelism))

/// Run a `jj diff`-style command, capturing stdout, bounded by a hang-detector `timeoutMs`.
///
/// The command name, arguments and `drainTimeoutMs` are parameters (rather than hard-coded
/// `jj diff --git`) solely so the bounded-wait paths are unit-testable with stub commands:
/// a hanging stub proves the timeout branch kills the process tree instead of hanging the
/// CLI, and a stub that leaves a grandchild holding the stdout pipe proves the drain-wedge
/// maps to `Error` rather than a silent empty diff. `jjDiffProvider` is the only production
/// caller and always passes `"jj" "diff --git"`.
let runBoundedDiff
    (timeoutMs: int)
    (drainTimeoutMs: int)
    (fileName: string)
    (arguments: string)
    : Result<string, string> =
    try
        let psi = ProcessStartInfo(fileName, arguments)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start(psi)

        // Read async so a full pipe can't deadlock the wait, and bound the wait so a
        // wedged jj can't hang the CLI forever.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        if not (proc.WaitForExit(timeoutMs)) then
            proc.Kill(entireProcessTree = true)
            eprintfn $"jj diff exceeded {timeoutMs / 1000}s — jj appears wedged; aborting"
            Error "jj diff timed out — jj appears wedged"
        else
            // Bound the post-exit drain so a grandchild that inherited jj's stdout cannot
            // wedge an unbounded read after jj itself has exited.
            let drain =
                TestRunner.drainOutputWithin drainTimeoutMs $"%s{fileName} %s{arguments}" stdoutTask stderrTask

            // Here the drained TEXT is authoritative data — it becomes the changed-file set —
            // so a truncated read must NOT surface as a valid diff: the partial "" would flow
            // on as "no changed files" and zero tests would run green. Checked BEFORE the exit
            // code precisely because a wedged jj still exits 0.
            if not drain.Completed then
                Error
                    $"jj diff output drain exceeded {drainTimeoutMs / 1000}s — a grandchild is still holding jj's stdout pipe open; jj appears wedged and the diff is truncated"
            elif proc.ExitCode = 0 then
                Ok drain.Stdout
            else
                Error "jj diff failed — is this a jj repository?"
    with ex ->
        Error $"Failed to run jj: %s{ex.Message}"

/// Get jj diff output.
let jjDiffProvider: DiffProvider =
    fun () -> runBoundedDiff jjDiffTimeoutMs TestRunner.drainOutputTimeoutMs "jj" "diff --git"

/// Run the status command: show what would run without executing.
let runStatus (repoRoot: string) : int =
    withRepoAuditSink repoRoot (runStatusWith jjDiffProvider repoRoot)

/// Run the run command: determine and execute affected tests.
let runRun (repoRoot: string) : int =
    withRepoAuditSink repoRoot (runRunWith jjDiffProvider repoRoot)

/// Run a parsed command, discovering the repo root by walking up from `startDir` when no
/// `--repo` was given (help needs no repository). Failures come back as exit codes; only `main`
/// ends the process.
let runCommandFrom (startDir: string) (parsed: ParsedCommand) : int =
    let repoRoot () =
        match parsed.RepoRoot with
        | Some path -> Some(Path.GetFullPath(path))
        | None -> findRepoRoot startDir

    let withRepoRoot (command: string -> int) : int =
        match repoRoot () with
        | None ->
            eprintfn "Error: not in a jj or git repository"
            1
        | Some root -> command root

    match parsed.Command with
    // Help needs no repository: it prints usage and succeeds anywhere.
    | Help ->
        showHelp ()
        0
    | Index -> withRepoRoot (fun root -> runIndex root parsed.Parallelism)
    | Run -> withRepoRoot runRun
    | Status -> withRepoRoot runStatus
    | DeadCodeCmd(patterns, includeTests, verbose) ->
        withRepoRoot (fun root -> withRepoAuditSink root (runDeadCode root patterns includeTests verbose))

/// Run a parsed command from the current working directory.
let runCommand (parsed: ParsedCommand) : int =
    runCommandFrom (Directory.GetCurrentDirectory()) parsed

[<EntryPoint>]
let main args =
    match parseArgs args with
    | Ok parsed -> runCommand parsed
    | Error message ->
        eprintfn $"Error: %s{message}"
        showHelp ()
        1
