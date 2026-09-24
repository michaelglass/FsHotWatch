module FsHotWatch.Cli.Program

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open CommandTree
open FsHotWatch
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Daemon
open FsHotWatch.Ipc

type RunFlag = | [<CmdFlag(Short = "r", Description = "Run once without daemon")>] RunOnce

/// Flags for `fshw test-rerun`. Forward-progress `fshw test` deliberately
/// has no filter knobs — the test-prune plugin runs everything downstream of
/// a change by design. `test-rerun` is the explicit investigation verb that
/// slices what just ran (or what you ask for) via xUnit v3's --filter-* args.
type RerunFlag =
    | [<CmdFlag(Description = "Pass --filter-class <pattern> to the underlying test runner (xUnit v3)")>] FilterClass of
        string
    | [<CmdFlag(Description = "Pass --filter-trait <name=value> to the underlying test runner (xUnit v3)")>] FilterTrait of
        string
    | [<CmdFlag(Description = "Limit the rerun to this test project (repeatable; matches the project name in your test config). Without it the filter is fanned out across EVERY configured test project, so a class living in one of them makes all the others report zero matches.");
        CmdArg("project")>] Project of string
    | [<CmdFlag(Description = "Seconds to wait for an in-flight background test run to release the slot before reporting busy (default 600). Raise it above a long tests.beforeRun chain so an explicit rerun isn't defeated.");
        CmdArg("seconds")>] WaitSec of int
    | [<CmdFlag(Description =
                    "Start the daemon for a filtered rerun even though this workspace has no valid full-suite baseline. Without one, the daemon's warm-up test pass runs EVERY configured test project; without this flag a filtered rerun refuses rather than silently buying that run.",
                Name = "allow-full-suite",
                Short = "F")>] AllowFullSuite

/// Default slot-wait budget (seconds) sent to the daemon's `run-tests` command
/// when `--wait-sec` is not given. Generous so a long `tests.beforeRun` chain
/// (90 s+) can't make an explicit `test-rerun` give up before the prior in-flight
/// run releases the slot.
[<Literal>]
let DefaultTestRerunWaitSec = 600

/// Render `RerunFlag list` to the raw arg string the xUnit v3 standalone
/// runner expects, quoting values that contain whitespace / double quotes by the
/// `ProcessStartInfo.Arguments` rule the spawn word-splits with.
/// Empty flag list renders to "".
module RerunFilter =
    // The ONE per-argument quoting rule (`ProcessHelper.quoteArg`) — shared with
    // TestPrune's `buildFilterArgs`, so `test-rerun` and `check` hand the runner the
    // same token for the same class name.
    let private quoteIfNeeded (s: string) = ProcessHelper.quoteArg s

    let private quoteTrait (s: string) =
        match s.IndexOf('=') with
        | -1 -> quoteIfNeeded s
        | i ->
            let name = s.Substring(0, i)
            let value = s.Substring(i + 1)
            $"%s{name}=%s{quoteIfNeeded value}"

    let render (flags: RerunFlag list) : string =
        flags
        |> List.choose (function
            | FilterClass p -> Some $"--filter-class %s{quoteIfNeeded p}"
            | FilterTrait t -> Some $"--filter-trait %s{quoteTrait t}"
            // Neither `--wait-sec` nor `--project` is an xUnit filter: the first is a
            // client-side slot-wait knob, the second selects WHICH projects the daemon
            // invokes. Both travel in the run-tests payload, never in the runner arg
            // string — passing `--project` to the runner would make it choke on an
            // unknown option.
            | Project _
            | WaitSec _
            | AllowFullSuite -> None)
        |> String.concat " "

    /// The test projects named with `--project`, in the order given. Empty means "every
    /// configured project", which is the historical behaviour and stays the default.
    let projects (flags: RerunFlag list) : string list =
        flags
        |> List.choose (function
            | Project p -> Some p
            | _ -> None)
        |> List.distinct

    /// The slot-wait budget (seconds) from the flags, or `DefaultTestRerunWaitSec`.
    let waitSec (flags: RerunFlag list) : int =
        flags
        |> List.tryPick (function
            | WaitSec n -> Some n
            | _ -> None)
        |> Option.defaultValue DefaultTestRerunWaitSec

/// Whether a NARROWED `test-rerun` may start a daemon in a workspace with no valid
/// full-suite baseline.
///
/// A daemon's startup scan ends in an impact run, and with no valid baseline the
/// test-prune plugin widens that run to EVERY configured test project — correctly, for
/// the verdict it serves. The rerun's own filter is still honoured, but a user who asked
/// for one class would also be buying the full suite, with nothing on the command's
/// output to say so. So the rerun decides BEFORE it starts anything, and never widens
/// silently: it refuses (naming the reason and the two ways forward), or it proceeds and
/// says what else is running.
module RerunBaseline =
    open FsHotWatch.TestPrune

    [<RequireQualifiedAccess>]
    type Decision =
        | Proceed
        /// Run, after printing these lines.
        | ProceedWithNotice of lines: string list
        /// Do not start or send anything; print these lines.
        | Refuse of lines: string list

    /// Why the workspace's full-suite baseline cannot vouch for `runnable` — `None` when
    /// it can, or when no test project is configured (then no baseline is owed). The same
    /// reading the test-prune plugin makes from the same sidecar.
    let invalidReason (repoRoot: string) (runnable: Set<string>) : string option =
        if Set.isEmpty runnable then
            None
        else
            match FullSuiteBaseline.load repoRoot with
            | FullSuiteBaseline.LoadedBaseline.Loaded None -> Some FullSuiteBaseline.absentReason
            | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> FullSuiteBaseline.staleness runnable baseline
            | FullSuiteBaseline.LoadedBaseline.Unreadable reason ->
                Some
                    $"the full-suite baseline (%s{FullSuiteBaseline.sidecarPath repoRoot}) exists but could not be read: %s{reason}"

    /// The narrowing flags as `(flag, value)` pairs, in the order given.
    let private narrowing (flags: RerunFlag list) : (string * string) list =
        flags
        |> List.choose (function
            | FilterClass p -> Some("--filter-class", p)
            | FilterTrait t -> Some("--filter-trait", t)
            | Project p -> Some("--project", p)
            | WaitSec _
            | AllowFullSuite -> None)

    /// POSIX single-quoting for a value the user will paste back into a shell — a
    /// `--filter-class *Foo*` glob left bare is expanded (or rejected) by zsh.
    let private shellQuote (s: string) : string =
        if
            s
            |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || "-_.=+/,:@".Contains c))
        then
            "'" + s.Replace("'", "'\\''") + "'"
        else
            s

    let private render (quote: string -> string) (pairs: (string * string) list) : string =
        pairs
        |> List.map (fun (flag, value) -> $"%s{flag} %s{quote value}")
        |> String.concat " "

    let decide (flags: RerunFlag list) (daemonRunning: bool) (invalid: string option) : Decision =
        let pairs = narrowing flags

        match invalid with
        | None -> Decision.Proceed
        // A plain `test-rerun` already asks for every project: nothing is widened.
        | Some _ when List.isEmpty pairs -> Decision.Proceed
        | Some reason ->
            let asked = render id pairs

            let header =
                $"fshw test-rerun: no valid full-suite baseline in this workspace — %s{reason}."

            let noVerdict =
                "  A filtered rerun earns no verdict: there is no baseline for the tests it skips to be \
                 equivalent to. `fshw confirm` runs the full suite and earns one."

            if daemonRunning then
                Decision.ProceedWithNotice
                    [ header
                      $"  This rerun runs only what you asked for (%s{asked}), but until a baseline is earned the \
                        daemon widens its own impact runs to the FULL SUITE, and this rerun may queue behind one."
                      noVerdict ]
            elif List.contains AllowFullSuite flags then
                Decision.ProceedWithNotice
                    [ header
                      $"  --allow-full-suite: starting the daemon anyway. Its warm-up test pass runs the FULL SUITE \
                        (every configured test project); your rerun (%s{asked}) runs alongside it."
                      noVerdict ]
            else
                Decision.Refuse
                    [ $"fshw test-rerun: refusing — no valid full-suite baseline in this workspace: %s{reason}."
                      $"  You asked for %s{asked}. No daemon is running here, and starting one runs a warm-up \
                        test pass that, with no baseline, is widened to the FULL SUITE (every configured test \
                        project) — far more than you asked for. Nothing was started."
                      "  To earn the baseline (runs the full suite):              fshw confirm"
                      $"  To start the daemon anyway and run your filter too:     fshw test-rerun %s{render shellQuote pairs} --allow-full-suite" ]

type ConfigCommand = | [<Cmd("Validate .fshw.json without starting the daemon")>] Check

type CoverageCommand =
    | [<Cmd("Delete coverage baseline + partial JSON so the next full run rebuilds from scratch",
            Name = "refresh-baseline")>] RefreshBaseline

/// Flags for `fshw dead-code`. Mirrors the standalone `test-prune dead-code`
/// CLI: `--entry` is repeatable and REPLACES the defaults when given;
/// `--include-tests` widens the report to test-file symbols. The standalone
/// CLI's `--verbose` (show why each symbol is unreachable) is driven by this
/// CLI's existing GLOBAL `-v/--verbose` flag — CommandTree rejects a command
/// flag that collides with a global, and the global already means "more detail".
type DeadCodeFlag =
    | [<CmdFlag(Description = "Entry-point name pattern (repeatable; replaces the defaults: *.main, *.Program.*, *.Routes.*, *.Scheduler.*)");
        CmdArg("pattern")>] Entry of string
    | [<CmdFlag(Description = "Include symbols from test files in the report", Name = "include-tests")>] IncludeTests

/// The column CommandTree indents a command's description to in the `fshw --help`
/// listing (two spaces, then the name padded to 17). A description is emitted VERBATIM
/// on both surfaces, so a multi-line one must carry its own continuation indent — an
/// un-indented second line starts at column 0 in the listing and reads as if it were
/// another COMMAND.
///
/// The cost is that `fshw confirm --help`, which prints the description as a left-aligned
/// block, shows those continuation lines indented: readable but ragged. The fix would be
/// for CommandTree to re-indent per surface; until then the LISTING wins, because that is
/// where the verb is discovered.
[<Literal>]
let private HelpIndent = "                   "

/// `--repository`: act on the repository host and every worktree it serves.
type RepositoryFlag =
    | [<CmdFlag(Description = "Act on the repository host and every worktree it serves, not only this worktree")>] Repository

type Command =
    | [<CmdExample("", "--no-cache"); Cmd("Start the daemon")>] Start
    | [<CmdExample("", "--repository"); Cmd("Stop the daemon (in host mode: detach this worktree)")>] Stop of
        RepositoryFlag list
    | [<CmdExample("", "--run-once");
        Cmd("Run all checks. The fast inner loop — the tests are IMPACT-FILTERED, so a green says nothing you changed broke anything the selector chose to look at, NOT that the whole suite is green. `confirm` runs the same checks unfiltered when you need that stronger claim")>] Check of
        RunFlag list
    /// Run the full suite and CONFIRM THAT `check` TOLD THE TRUTH. Same checks as
    /// `check`, but the tests run UNFILTERED — and the verdict is refused unless they
    /// actually did, because a merge is a correctness claim and cannot rest on a
    /// heuristic selection.
    ///
    /// Every disagreement between the two is a BUG in one of them:
    ///
    ///   * failed here, never selected by `check` → the SELECTOR missed a test. A
    ///     TestPrune bug, not a test bug.
    ///   * passed here, but `check` says failed → a stale ledger entry, a flake, or a
    ///     test-isolation defect that only passes because another test set up the state
    ///     it depends on. There, `check` is the honest one.
    ///
    /// `--run-once` runs it WITHOUT a daemon, which is how CI must invoke it: a merge
    /// verdict reachable only over a socket is one CI cannot ask for.
    | [<CmdExample("", "--run-once");
        Cmd("Run the FULL suite and confirm `check` told the truth.\n"
            + HelpIndent
            + "Any disagreement is a BUG:\n"
            + HelpIndent
            + "  failed here, not selected by check  → the selector MISSED a test\n"
            + HelpIndent
            + "  passed here, but check says failed  → a stale red, a flake, or a\n"
            + HelpIndent
            + "                                        test that only passes with company\n"
            + HelpIndent
            + "Refuses a green verdict from anything less than the full suite (exit 3).")>] Confirm of RunFlag list
    /// Read `.fshw/verdict.json` and report whether it still applies to the tree on
    /// disk. Touches NO socket, starts no daemon, triggers no run, so reading cannot
    /// perturb what it measures (see the `Verdict` module).
    ///
    /// A CONVENIENCE over the file, not a second source of truth: it prints the same
    /// verdict the check wrote, plus the one judgement a consumer must not get wrong —
    /// does this verdict describe the tree I have?
    | [<Cmd("Report the last check's verdict and whether it still applies to the current tree (reads .fshw/verdict.json; never contacts the daemon)")>] Verdict
    | [<CmdExample("--filter-class *CryptoTests*", "--filter-trait Category=Browser");
        Cmd("Rerun tests with an xUnit v3 --filter-class / --filter-trait slice", Name = "test-rerun")>] TestRerun of
        RerunFlag list
    | [<CmdExample("", "--run-once"); Cmd("Format code")>] Format of RunFlag list
    | [<CmdArg("plugin name (optional)", FieldIndex = 0);
        CmdExample("", "build", "test-prune", "--repository");
        Cmd("Show current status")>] Status of plugin: string option * flags: RepositoryFlag list
    | [<Cmd("Scan for file changes")>] Scan
    | [<Cmd("Invalidate cached task results without stopping the daemon")>] Invalidate
    | [<CmdArg("plugin name");
        CmdExample("build", "test-prune", "analyzers");
        Cmd("Force a plugin to re-run, clearing its cached state")>] Rerun of pluginName: string
    | [<Cmd("Generate initial config")>] Init
    | [<Cmd("Configuration commands")>] Config of ConfigCommand
    | [<Cmd("Coverage commands")>] Coverage of CoverageCommand
    | [<CmdExample("", "--include-tests", "--entry *.Cli.* --entry *.Worker.*");
        Cmd("Report unreachable symbols from entry points (TestPrune dead-code analysis over the daemon DB)",
            Name = "dead-code")>] DeadCode of DeadCodeFlag list
    | [<Cmd("Install fish completions")>] Completions
    | [<CmdArg("worktree root");
        Cmd("Run the repository host for the repository containing <root> (launched on demand when `repositoryHost` is enabled)")>] Host of
        root: string

type GlobalFlag =
    | [<CmdFlag(Short = "v", Description = "Enable debug-level logging")>] Verbose
    | [<CmdFlag(Description = "Set log level: error|warning|info|debug"); CmdArg("level", Default = "info")>] LogLevel of
        string
    | [<CmdFlag(Description = "Disable on-disk task result cache")>] NoCache
    | [<CmdFlag(Description = "Treat warnings as non-fatal (errors still fail)")>] NoWarnFail
    | [<CmdFlag(Short = "q", Description = "Compact one-line-per-plugin output")>] Compact
    | [<CmdFlag(Short = "a", Description = "Agent-friendly parseable output with next-step hint")>] Agent

let globalSpec =
    CommandReflection.fromUnionWithGlobalsAndEnv<Command, GlobalFlag>
        "FsHotWatch — F# file watcher daemon"
        "FS_HOT_WATCH"

let commandTree = globalSpec.Tree

let cliName = "fshw"

let private isRunOnce = List.contains RunOnce

/// The run mode a command's in-process host is constructed with. `--run-once`
/// scans, settles and exits, so its host is `OneShot` and constructs no file
/// watcher; every persistent command keeps `Watching`.
let internal runModeFor (command: Command) : Daemon.RunMode =
    match command with
    | Check flags
    | Confirm flags
    | Format flags when isRunOnce flags -> Daemon.RunMode.OneShot
    | _ -> Daemon.RunMode.Watching

/// Pick a render mode from the global `--agent` / `--compact` flags. `--agent`
/// wins when both are set.
let private pickMode (agentMode: bool) (compactMode: bool) : ProgressRenderer.RenderMode =
    if agentMode then ProgressRenderer.Agent
    elif compactMode then ProgressRenderer.Compact
    else ProgressRenderer.Verbose

let private renderLines mode warningsAreFailures statuses =
    ProgressRenderer.renderAll mode warningsAreFailures System.DateTime.UtcNow statuses

let private renderBlock mode warningsAreFailures statuses =
    renderLines mode warningsAreFailures statuses |> String.concat "\n"

/// Compute the launch command for re-starting the daemon. Returns (exe, argPrefix),
/// where argPrefix is prepended to "start" — the spawn becomes `exe argPrefix start`.
///
/// `processPath` is `Environment.ProcessPath` and `entryAssemblyDll` is
/// `Assembly.GetEntryAssembly().Location` (passed in so this stays pure/testable).
///
/// The `dotnet <dll>` case spawns THAT SAME dll rather than reconstructing
/// `tool run fshw`, which would silently launch the PINNED tool instead of the running
/// build. The published tool also resolves a real dll path, so it takes the same branch
/// — equivalent, with no tool-resolution indirection.
let computeLaunchCommand (processPath: string) (entryAssemblyDll: string option) : string * string =
    let lowerPath = processPath.ToLowerInvariant()
    let isDotnet = lowerPath.EndsWith("dotnet") || lowerPath.EndsWith("dotnet.exe")

    if not isDotnet then
        // Native single-file exe — launch it directly.
        (processPath, "")
    else
        match entryAssemblyDll with
        | Some dll when
            not (String.IsNullOrWhiteSpace dll)
            && dll.ToLowerInvariant().EndsWith(".dll")
            && File.Exists dll
            ->
            // Quoted: the path may contain spaces, and the caller appends `start`.
            (processPath, $"\"%s{dll}\" ")
        | _ ->
            // No usable entry-assembly path (single-file / shim) — fall back to the
            // tool-run form so the published tool still launches.
            (processPath, $"tool run %s{cliName} ")

/// Whether `dir` has either ordinary Git metadata or a valid Git worktree
/// pointer file. The pointer's target must resolve to a Git directory with a
/// `HEAD`; a dangling pointer or arbitrary `.git` file/directory does not
/// establish a repository.
let private hasGitMetadata (dir: string) =
    let dotGit = Path.Combine(dir, ".git")

    let isGitDirectory path =
        Directory.Exists(path) && File.Exists(Path.Combine(path, "HEAD"))

    isGitDirectory dotGit
    || (File.Exists(dotGit)
        && try
            // Git writes a one-line `gitdir: <path>` pointer for linked
            // worktrees. Read only that line: root discovery runs on every CLI
            // invocation and must not load an arbitrary `.git` file in full.
            use reader = File.OpenText(dotGit)
            let pointer = reader.ReadLine()

            if isNull pointer || not (pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) then
                false
            else
                // ReadLine has already removed the line ending. Keep every
                // remaining character: spaces are valid path characters, and
                // trimming one would make a valid pointer dangle.
                let gitDirPath = pointer.Substring("gitdir: ".Length)

                if String.IsNullOrEmpty(gitDirPath) then
                    false
                else
                    let resolvedGitDir =
                        if Path.IsPathRooted(gitDirPath) then
                            gitDirPath
                        else
                            Path.GetFullPath(Path.Combine(dir, gitDirPath))

                    isGitDirectory resolvedGitDir
           with _ ->
               false)

/// Walk up from startDir looking for a jj directory or either Git repository
/// shape: a normal `.git` directory or a linked-worktree `.git` pointer file.
let findRepoRoot (startDir: string) =
    let rec walk (dir: string) =
        if Directory.Exists(Path.Combine(dir, ".jj")) || hasGitMetadata dir then
            Some dir
        else
            let parent = Directory.GetParent(dir)
            if isNull parent then None else walk parent.FullName

    walk startDir

/// Compute a deterministic pipe name from repo root path.
let computePipeName (repoRoot: string) =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot))
    let short = Convert.ToHexStringLower(hash).Substring(0, 12)
    $"fshw-{short}"

/// Injectable file system operations for testability.
type FileOps =
    { FileExists: string -> bool
      ReadAllText: string -> string
      WriteAllText: string -> string -> unit
      DeleteFile: string -> unit
      CreateDirectory: string -> unit }

/// Default file system operations.
let defaultFileOps: FileOps =
    { FileExists = File.Exists
      ReadAllText = File.ReadAllText
      WriteAllText = fun path content -> File.WriteAllText(path, content)
      DeleteFile = File.Delete
      CreateDirectory = fun path -> Directory.CreateDirectory(path) |> ignore }

/// Injectable process operations for testability.
type ProcessOps =
    { GetProcessById: int -> System.Diagnostics.Process
      KillProcess: System.Diagnostics.Process -> unit
      WaitForExit: System.Diagnostics.Process -> int -> bool }

/// Default process operations.
let defaultProcessOps: ProcessOps =
    { GetProcessById = System.Diagnostics.Process.GetProcessById
      KillProcess = fun proc -> proc.Kill()
      WaitForExit = fun proc timeout -> proc.WaitForExit(timeout) }

/// The production daemon launch: background `exe toolPrefix extraArgs start` with its
/// output appended to `logFile`, in a new session and process group of its own, so the
/// daemon outlives both this CLI and anything that signals this CLI's group.
/// Raises when the launch itself fails; see `DetachedLaunch`.
let launchDaemonProcess (exe: string) (toolPrefix: string) (repoRoot: string) (extraArgs: string) (logFile: string) =
    DetachedLaunch.launch repoRoot (DetachedLaunch.daemonShellCommand exe toolPrefix extraArgs logFile)

/// Injectable IPC operations for testability.
type IpcOps =
    { Shutdown: string -> Async<string>
      Scan: string -> Async<string>
      ScanStatus: string -> Async<string>
      GetStatus: string -> Async<string>
      GetPluginStatus: string -> string -> Async<string>
      RunCommand: string -> string -> string -> Async<string>
      GetDiagnostics: string -> string -> Async<string>
      WaitForScan: string -> int64 -> Async<string>
      WaitForComplete: string -> int -> Async<string>
      TriggerBuild: string -> Async<string>
      FormatAll: string -> Async<string>
      RerunPlugin: string -> string -> Async<string>
      Invalidate: string -> Async<string>
      IsRunning: string -> bool
      LaunchDaemon: string -> string -> string -> unit }

/// Default IPC operations using the real IpcClient.
let defaultIpcOps: IpcOps =
    { Shutdown = IpcClient.shutdown
      Scan = IpcClient.scan
      ScanStatus = IpcClient.scanStatus
      GetStatus = IpcClient.getStatus
      GetPluginStatus = IpcClient.getPluginStatus
      RunCommand = IpcClient.runCommand
      GetDiagnostics = IpcClient.getDiagnostics
      WaitForScan = IpcClient.waitForScan
      WaitForComplete = IpcClient.waitForComplete
      TriggerBuild = IpcClient.triggerBuild
      FormatAll = IpcClient.formatAll
      RerunPlugin = IpcClient.rerunPlugin
      Invalidate = IpcClient.invalidate
      IsRunning = IpcClient.isRunning
      LaunchDaemon =
        fun repoRoot extraArgs logFile ->
            let entryDll =
                System.Reflection.Assembly.GetEntryAssembly()
                |> Option.ofObj
                |> Option.map (fun a -> a.Location)

            let (exe, toolPrefix) = computeLaunchCommand Environment.ProcessPath entryDll
            launchDaemonProcess exe toolPrefix repoRoot extraArgs logFile }

/// Where `fshw completions` writes its fish completion script.
///
/// Resolved HERE rather than left to `CommandTree.FishCompletions.writeToFile`, which
/// hardcodes `~/.config/fish/completions`. Two measurements (.NET 10, macOS) say that is
/// the wrong destination often enough to own the decision locally:
///
///   * **fish reads `$XDG_CONFIG_HOME/fish` when that variable is set**, falling back to
///     `~/.config/fish` only when it is not. Writing to the fallback unconditionally means
///     that on a machine which sets `XDG_CONFIG_HOME`, fshw wrote a completions file into a
///     directory fish never reads — the command reported success and the completions
///     silently did not work.
///   * **`Environment.GetFolderPath SpecialFolder.UserProfile` returns `""`** — not the
///     home directory, and not an exception — when `HOME` is unset. `Path.Combine` then
///     produces the RELATIVE path `.config/fish/completions`, so the write lands in
///     whatever the current working directory happens to be. A tool that scatters a
///     `.config/` tree into the user's repo on a misconfigured shell is worse than one
///     that refuses, so this returns `Error` instead of a path it cannot justify.
///
/// Injectable by construction: `XDG_CONFIG_HOME` is the documented way to move a fish
/// config tree, so a caller that needs the write to land elsewhere — a test, a sandbox,
/// a packaging script — sets the same variable a fish user would.
let fishCompletionsDir () : Result<string, string> =
    match Environment.GetEnvironmentVariable "XDG_CONFIG_HOME" with
    | xdg when not (String.IsNullOrWhiteSpace xdg) -> Ok(Path.Combine(xdg, "fish", "completions"))
    | _ ->
        match Environment.GetFolderPath Environment.SpecialFolder.UserProfile with
        | home when String.IsNullOrWhiteSpace home ->
            Error
                "could not resolve a home directory (HOME is unset, and SpecialFolder.UserProfile \
                 resolved to an empty path). Set HOME, or set XDG_CONFIG_HOME to the fish config \
                 directory you want the completions written under."
        | home -> Ok(Path.Combine(home, ".config", "fish", "completions"))

/// Write the fish completion script for `cliName` and return where it landed.
/// Uses `FishCompletions.generateContent` — the pure half of CommandTree's API — so the
/// destination stays this module's decision rather than the library's.
let writeFishCompletions (tree: CommandTree<'Cmd>) (cliName: string) : Result<string, string> =
    fishCompletionsDir ()
    |> Result.map (fun dir ->
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, $"%s{cliName}.fish")
        File.WriteAllText(path, FishCompletions.generateContent tree cliName)
        path)

/// Unwrap nested AggregateException down to the most informative inner exception
/// so we don't print "One or more errors occurred. (...)" wrapping the real message.
let rec unwrapIpcException (ex: exn) : exn =
    match ex with
    | :? AggregateException as agg when agg.InnerExceptions.Count = 1 -> unwrapIpcException agg.InnerExceptions.[0]
    | :? AggregateException as agg when agg.InnerException <> null -> unwrapIpcException agg.InnerException
    | _ -> ex

/// The recovery-relevant cause of a failed IPC call. In particular, an OOM is not
/// itself evidence of bad framing: the CLI process can genuinely exhaust its heap.
[<RequireQualifiedAccess>]
type IpcFault =
    | CorruptedFrame of exn
    | ClientOutOfMemory of OutOfMemoryException
    /// The DAEMON exhausted memory serving this call — most often
    /// building a reply too large to represent, not running short of heap.
    ///
    /// Its own case because the old code had none, and a remote OOM therefore landed in
    /// `ClientOutOfMemory` and printed "the fshw CLI ran out of memory". That sentence
    /// is a diagnosis, and it sent seven consecutive investigations at the client:
    /// `DOTNET_GCConserveMemory` was set on the CLI, the box's free memory was measured
    /// and re-measured, and none of it could ever have mattered, because the process
    /// that failed was the other one.
    | DaemonOutOfMemory of exn
    | TimedOut of TimeoutException
    /// The daemon dropped the connection while the call was in flight —
    /// `StreamJsonRpc.ConnectionLostException`, raised when the pipe ends mid-request.
    /// This is the shape a daemon that EXITED takes once the CLI is already talking to
    /// it (a crash, an OOM killer, a `fshw stop` from another shell); a daemon that was
    /// already gone before the call presents as `TimedOut` instead, because
    /// `NamedPipeClientStream.ConnectAsync` retries every connect error it gets.
    | ConnectionLost of exn
    /// The daemon answered and does not have the method this CLI called —
    /// `StreamJsonRpc.RemoteMethodNotFoundException`, raised both for a method the
    /// remote target lacks entirely and for one whose signature no longer matches.
    /// That is a VERSION MISMATCH: a daemon started from a different fshw build.
    /// It is NOT a `RemoteInvocationException` — the two are siblings under
    /// `RemoteRpcException` — which is why it used to fall through to `Other`.
    | DaemonMethodMissing of exn
    /// The daemon's own RPC method raised, and the fault is not one of the
    /// corrupted-pipe/out-of-memory family reconstructed above: an ordinary daemon-side
    /// error (a plugin bug, a refused scan). The pipe is healthy and the daemon is
    /// alive, so nothing here is fixed by restarting it.
    | DaemonThrew of exn
    /// No evidence in the fault named a cause. Still carries a hint — see
    /// `ipcErrorHint`, which is TOTAL: an unclassified fault is the one case where the
    /// reader has the least to go on and needs the generic recovery steps most.
    | Other of exn

/// Which process an IPC fault was RAISED in. Not derivable from the exception once it
/// has crossed the wire: a daemon-side fault is reconstructed on this side from an
/// error payload, so it arrives as an ordinary local exception object and only the
/// CALLER knows where it came from.
[<RequireQualifiedAccess>]
type FaultOrigin =
    /// Thrown in this CLI process.
    | Client
    /// Reported by the daemon and rebuilt here from its error payload.
    | Daemon

/// StreamJsonRpc's header-delimited reader is the evidence that distinguishes an
/// absurd Content-Length allocation from an unrelated allocation failure in this
/// process. Kept as a pure seam because Exception.StackTrace cannot be constructed
/// portably in a unit test.
///
/// `origin` is the second piece of evidence, and it is the one a stack trace cannot
/// supply: a frame-reader trace proves a corrupted frame wherever it ran, but an OOM
/// with any OTHER trace is only attributable to a process by knowing which process
/// reported it.
let internal classifyIpcFaultAt (origin: FaultOrigin) (stackTrace: string option) (inner: exn) : IpcFault =
    let aroseInFrameReader =
        stackTrace
        |> Option.exists (fun trace -> trace.Contains("HeaderDelimitedMessageHandler", StringComparison.Ordinal))

    match inner with
    | :? OutOfMemoryException as oom ->
        match aroseInFrameReader, origin with
        | true, _ -> IpcFault.CorruptedFrame inner
        | false, FaultOrigin.Daemon -> IpcFault.DaemonOutOfMemory inner
        | false, FaultOrigin.Client -> IpcFault.ClientOutOfMemory oom
    | :? OverflowException when origin = FaultOrigin.Daemon && not aroseInFrameReader ->
        // Outside the frame reader, on the daemon, an overflow is System.Text.Json's
        // UTF-8 transcoder refusing a single string token it cannot encode — the same
        // "this reply is too large to exist" fact as the OOM above, and never a hint
        // about this client's heap.
        IpcFault.DaemonOutOfMemory inner
    | :? OverflowException ->
        // Overflow was observed in HeaderDelimitedMessageHandler's frame-length
        // arithmetic, but OverflowException is otherwise a generic client fault. The
        // exception type alone cannot authorize destroying a healthy daemon.
        if aroseInFrameReader then
            IpcFault.CorruptedFrame inner
        else
            IpcFault.Other inner
    | :? TimeoutException as timeout -> IpcFault.TimedOut timeout
    | _ -> IpcFault.Other inner

/// A fault thrown by the DAEMON's own RPC method arrives on the client as
/// `RemoteInvocationException`, never as the daemon's own exception type —
/// `JsonRpc.ExceptionStrategy` defaults to `ExceptionProcessing.CommonErrorData`,
/// which serializes the remote exception's type name, message AND stack trace into
/// `DeserializedErrorData` rather than preserving its .NET type or reconstructing a
/// real cross-process stack trace. Without this, a corrupted-frame fault that
/// happened server-side could never reach `classifyIpcFaultAt`'s
/// `HeaderDelimitedMessageHandler` check — `RemoteInvocationException` is never
/// `OutOfMemoryException`/`OverflowException`/`TimeoutException`, so it would fall
/// to `IpcFault.Other` and self-heal would never fire on a fault the tool already
/// knows how to recover from. Returns the reconstructed exception paired with the
/// REMOTE stack trace (not the local one, which is empty on a never-thrown
/// reconstruction) so `classifyIpcFaultAt` classifies it using the evidence from
/// where the fault actually happened.
let private remoteFaultDetails (remote: StreamJsonRpc.RemoteInvocationException) : (exn * string option) option =
    let rec fromCommonErrorData (data: StreamJsonRpc.Protocol.CommonErrorData) =
        match data.TypeName with
        | "System.OutOfMemoryException" ->
            Some(OutOfMemoryException(data.Message) :> exn, data.StackTrace |> Option.ofObj)
        | "System.OverflowException" -> Some(OverflowException(data.Message) :> exn, data.StackTrace |> Option.ofObj)
        | "System.TimeoutException" -> Some(TimeoutException(data.Message) :> exn, data.StackTrace |> Option.ofObj)
        | "System.AggregateException" -> data.Inner |> Option.ofObj |> Option.bind fromCommonErrorData
        | _ -> None

    match remote.DeserializedErrorData with
    | :? StreamJsonRpc.Protocol.CommonErrorData as data -> fromCommonErrorData data
    | _ -> None

/// Classify an unwrapped IPC exception.
///
/// `RemoteMethodNotFoundException` and `ConnectionLostException` are matched FIRST and
/// separately: neither derives from `RemoteInvocationException` (all three are siblings
/// under `RemoteRpcException`), so neither ever reached the reconstruction below — both
/// landed in `IpcFault.Other`, which printed the raw exception and no hint at all. They
/// are also the two faults with the most specific remedies: an fshw build mismatch, and
/// a daemon that died mid-call.
let classifyIpcFault (inner: exn) : IpcFault =
    match inner with
    | :? StreamJsonRpc.RemoteMethodNotFoundException -> IpcFault.DaemonMethodMissing inner
    | :? StreamJsonRpc.ConnectionLostException -> IpcFault.ConnectionLost inner
    | :? StreamJsonRpc.RemoteInvocationException as remote ->
        match remoteFaultDetails remote with
        | Some(reconstructed, remoteStackTrace) -> classifyIpcFaultAt FaultOrigin.Daemon remoteStackTrace reconstructed
        // The daemon ANSWERED and its method threw. Not `Other`: "the daemon reported an
        // error" is itself the diagnosis, and it rules out every pipe-level remedy.
        | None -> IpcFault.DaemonThrew inner
    | _ -> classifyIpcFaultAt FaultOrigin.Client (inner.StackTrace |> Option.ofObj) inner

/// Map an unwrapped IPC exception to a user-actionable hint. Pure so it can be
/// unit-tested without round-tripping through a real pipe.
///
/// TOTAL — `string`, not `string option`. The old signature let a fault be classified
/// and then silently lose its hint, and `IpcFault.Other` did exactly that: every IPC
/// failure that was not a timeout printed a bare exception and nothing else. Since the
/// case set is a closed union, the compiler is the thing that guarantees a new fault
/// cannot be added without an answer to "and what should the reader DO?" — a guarantee
/// no `Some`/`None` lookup can make. The unclassifiable case gets the generic recovery
/// steps rather than silence: the reader who has the least to go on needs them most.
///
/// A corrupted-frame fault only reaches its hint AFTER `runIpcWithSelfHeal`
/// already tried the automatic restart-and-retry. A client OOM deliberately
/// takes the no-restart path and says so.
let ipcErrorHint (inner: exn) : string =
    match classifyIpcFault inner with
    | IpcFault.CorruptedFrame _ ->
        "The IPC pipe returned a corrupted or oversized frame. fshw already restarted \
         the daemon and retried; since it recurred, check `logs/daemon.log` for a \
         second daemon, a crash loop, or runaway memory growth."
    | IpcFault.ClientOutOfMemory _ ->
        "The fshw CLI ran out of memory while handling the IPC call. The daemon was not \
         restarted because this failure carries no evidence of a corrupted frame; \
         inspect the client process memory limit and reduce concurrent work."
    | IpcFault.DaemonOutOfMemory _ ->
        "The DAEMON ran out of memory building this reply — the failure is on that side, so \
         the client's memory limit and the box's free memory are not the lever. The usual \
         cause is a reply whose size is a PRODUCT rather than a sum: a broadly red suite \
         whose per-failure diagnostics each carry the whole run's output. Check the ledger \
         size in `logs/daemon.log`, and see `.fshw/test-runs/` — the run's own evidence is \
         written by the daemon and survives this."
    | IpcFault.TimedOut _ ->
        // Every connect failure arrives here too, not just a slow reply: on Unix
        // `NamedPipeClientStream.ConnectAsync` retries a missing socket, a stale socket
        // left by a dead daemon, and a socket path this process may not open, and
        // surfaces all three as the SAME `TimeoutException` once the bound expires
        // (measured against .NET 10 on macOS). So the hint has to name that whole set —
        // "busy or hung" alone sends the reader to look at a daemon that is not there.
        "Daemon did not respond in time — it may be busy or hung, or there may be no daemon \
         listening at all (a dead daemon's stale socket, or one this user cannot open, fails \
         the same way). Check `logs/daemon.log` and `fshw status`; `fshw stop` clears a stale \
         socket, and the next fshw command starts a fresh daemon."
    | IpcFault.ConnectionLost _ ->
        "The daemon dropped the connection mid-call, which means it EXITED while serving this \
         request — a crash, an out-of-memory kill, or a `fshw stop` from another shell. The \
         last lines of `logs/daemon.log` are from just before it died. Re-run the command: the \
         next one starts a fresh daemon."
    | IpcFault.DaemonMethodMissing _ ->
        "The running daemon does not have the method this CLI called — it was started from a \
         DIFFERENT fshw build, so the two no longer agree on the RPC surface. Run `fshw stop` \
         and re-run the command; the next one starts a daemon from this binary."
    | IpcFault.DaemonThrew _ ->
        "The daemon answered and its own call failed, so the pipe is healthy and restarting the \
         daemon will not change the outcome. The daemon-side stack trace is in \
         `logs/daemon.log`."
    | IpcFault.Other _ ->
        "fshw has no recovery story for this fault — it is not a timeout, a lost connection, a \
         corrupted frame, an out-of-memory, or an error the daemon reported. Check \
         `logs/daemon.log` for what the daemon was doing; if it is wedged, `fshw stop` and \
         re-run (the next command starts a fresh one)."

/// The first line of an IPC failure must name the process whose failure is known.
/// A bare OOM is evidence about this CLI, not about daemon connectivity; retaining
/// the generic daemon-connection headline there sends operators toward the healthy
/// process and contradicts the no-restart recovery policy below.
let ipcErrorHeadline (inner: exn) : string =
    // A daemon that ANSWERED and named its own terminal has already written the
    // headline; wrapping it in a connection failure contradicts it. The scan
    // supersession terminal travels as a message across the RPC boundary (the
    // concrete type does not survive), which is why it is recognized by prefix
    // here rather than classified as a fault.
    match classifyIpcFault inner with
    | _ when FsHotWatch.Daemon.isModelKeptChangingDuringScanMessage inner.Message -> inner.Message
    | IpcFault.ClientOutOfMemory _ -> $"The fshw CLI ran out of memory while handling daemon IPC: %s{inner.Message}"
    | IpcFault.DaemonOutOfMemory _ -> $"The fshw DAEMON ran out of memory serving this IPC call: %s{inner.Message}"
    // The same rule as the scan-supersession branch above, generalized: a daemon that
    // answered has DISPROVED "could not connect", and saying it anyway sends the reader
    // to the pipe for a fault that was never in the pipe.
    | IpcFault.DaemonMethodMissing _ ->
        $"The daemon did not recognize this call — it is running a different fshw build: %s{inner.Message}"
    | IpcFault.DaemonThrew _ -> $"The daemon reported an error: %s{inner.Message}"
    | IpcFault.ConnectionLost _
    | IpcFault.CorruptedFrame _
    | IpcFault.TimedOut _
    | IpcFault.Other _ ->
        // A daemon that has been squeezed out by machine memory pressure presents
        // here, not as an OOM: it stops answering, or the connect times out, and
        // the message names the transport rather than the cause. fshw's footprint
        // is ~85% native FCS and scales with tree size, so on a machine whose RAM
        // the tree has outgrown this is the SHAPE the failure takes — measured
        // 2026-09-20 at a 37 GB phys_footprint on a 32 GB box, where the same
        // session also saw daemons die mid-scan and a supervisor reaped by an
        // agent harness's low-memory killer. Saying so costs one GC read and
        // turns four different-looking failures into one named cause.
        let pressure =
            FsHotWatch.IdleExit.disconnectPressureNote (FsHotWatch.IdleExit.readGcPressure ())

        $"Could not connect to daemon: %s{inner.Message}%s{pressure}"

/// Render a failed IPC call to stderr: a process-accurate headline plus the
/// unwrapped error's recovery hint (if any). Shared by every IPC entry point so
/// the message and hint stay identical across the CLI.
let reportDaemonError (ex: exn) : unit =
    let inner = unwrapIpcException ex
    eprintfn "%s" (ipcErrorHeadline inner)
    eprintfn "  hint: %s" (ipcErrorHint inner)

/// Run one IPC action with corrupted-pipe self-healing.
/// A corrupted-pipe fault means the daemon (or a rogue sibling sharing the
/// pipe) is emitting garbage frames, and the known cure is `fshw stop` +
/// `fshw start`, which the tool performs automatically: force-restart the daemon
/// (announcing what it is doing), retry the action ONCE, and only then fail through
/// `onFailure`.
/// Every other fault goes straight to `onFailure`, unchanged. Internal so the
/// heal-and-retry contract is unit-testable without a real pipe.
let internal runIpcWithSelfHeal (forceRestart: unit -> bool) (onFailure: exn -> int) (action: unit -> int) : int =
    try
        action ()
    with ex ->
        let inner = unwrapIpcException ex

        match classifyIpcFault inner with
        | IpcFault.CorruptedFrame frameFault ->
            // `frameFault`, not the outer `inner`, names the fault's real type — for a
            // fault reconstructed from a RemoteInvocationException, `inner` would only
            // ever say "RemoteInvocationException", hiding which corrupted-pipe shape
            // (OOM vs Overflow) actually happened.
            eprintfn
                "⚠ the daemon returned a corrupted IPC reply (%s) — restarting the daemon and retrying..."
                (frameFault.GetType().Name)

            if forceRestart () then
                try
                    action ()
                with retryEx ->
                    onFailure retryEx
            else
                onFailure ex
        | IpcFault.ClientOutOfMemory _
        // No restart, for the same reason as a client OOM and one more: restarting a
        // daemon that has just FINISHED a twenty-minute run is how the run's warm state
        // gets thrown away along with its result.
        | IpcFault.DaemonOutOfMemory _
        | IpcFault.TimedOut _
        // A daemon that is already gone (`ConnectionLost`) has nothing to restart here —
        // the next command's `ensureDaemon` starts one. A build mismatch
        // (`DaemonMethodMissing`) and a daemon-side throw (`DaemonThrew`) both come from
        // a daemon that ANSWERED, and neither is a corrupted frame; retrying the same
        // call against a restarted daemon would only repeat it.
        | IpcFault.ConnectionLost _
        | IpcFault.DaemonMethodMissing _
        | IpcFault.DaemonThrew _
        | IpcFault.Other _ -> onFailure ex

/// Wrap an IPC call with connection error handling and corrupted-pipe
/// self-healing (`forceRestart` is the executeCommand-scoped restart).
let private withIpc (forceRestart: unit -> bool) (action: unit -> int) : int =
    runIpcWithSelfHeal
        forceRestart
        (fun ex ->
            reportDaemonError ex
            1)
        action

/// Like `withIpc` but with NO self-heal — for `stop`, where restarting a
/// daemon in order to stop it would be absurd.
let private withIpcNoHeal (action: unit -> int) : int =
    try
        action ()
    with ex ->
        reportDaemonError ex
        1

/// Whether the daemon is answering RPCs, as decided by the readiness gate before
/// a check is issued. `Ready` proceeds; the two failure cases are both
/// UN-COMPLETABLE (exit 2), distinguished only for the message shown.
[<RequireQualifiedAccess>]
type DaemonReadiness =
    /// The daemon answered a probe — proceed with the check.
    | Ready
    /// The daemon process is provably gone (crashed during startup).
    | Crashed
    /// The daemon never became responsive within the readiness deadline.
    | TimedOut

/// Like `withIpc`, but for the `check` path. An IPC/connect fault here means the
/// daemon never produced a verdict, so the check is UN-COMPLETABLE — exit 2
/// ("completeness unachievable"), NEVER exit 1 (which a programmatic consumer
/// reads as "the daemon ran and found failures"). Reports the connection error
/// plus a pointer to the daemon log so the failure is actionable, never a bare
/// non-zero that an autonomous loop misreads as diagnostics. Corrupted-pipe
/// faults get the same self-heal-and-retry as `withIpc` before failing.
let private withCheckIpc (forceRestart: unit -> bool) (action: unit -> int) : int =
    runIpcWithSelfHeal
        forceRestart
        (fun ex ->
            reportDaemonError ex
            eprintfn "  The check could not complete — see logs/daemon.log."
            2)
        action

/// The scope commands over this transport — the daemon's IPC socket. The bodies, and
/// every sentence they print, live ONCE in `ScopeCommands`, shared with `--run-once`;
/// see there for what each way of not getting an answer means.
let private scopeSend (ipc: IpcOps) (pipeName: string) : ScopeCommands.Send =
    ScopeCommands.overIpc (fun name args -> ipc.RunCommand pipeName name args |> Async.RunSynchronously)

/// `ScopeCommands.readTestRun` over the daemon.
let internal readTestRun (ipc: IpcOps) (pipeName: string) : TestRunReport =
    ScopeCommands.readTestRun (scopeSend ipc pipeName)

/// `ScopeCommands.readCheckReach` over the daemon.
let internal readCheckReach (ipc: IpcOps) (pipeName: string) : IpcParsing.CheckReachReading =
    ScopeCommands.readCheckReach (scopeSend ipc pipeName)

/// `ScopeCommands.requestFullSuiteScope` over the daemon.
let internal requestFullSuiteScope (ipc: IpcOps) (pipeName: string) : unit =
    ScopeCommands.requestFullSuiteScope (scopeSend ipc pipeName)

/// Tell the build plugin the next build must be REAL, not a cache replay (see
/// `IpcParsing.ForceRebuildCommand`). Best-effort and non-fatal by design, exactly like
/// `forceFullSuiteRun`: a daemon without the command (no build plugin configured) must
/// not turn a verdict into an error. The verdict still refuses anything less than a
/// full, complete run on its own evidence, so a missed force degrades to the previous
/// behaviour rather than to a false green.
let internal forceRealBuild (ipc: IpcOps) (pipeName: string) : unit =
    try
        let reply =
            ipc.RunCommand pipeName ForceRebuildCommand "{}" |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply reply then
            FsHotWatch.Logging.warn
                "cli-confirm"
                $"the daemon has no `%s{ForceRebuildCommand}` command — a cached build cannot be forced"
        else
            FsHotWatch.Logging.debug "cli-confirm" $"force-rebuild reply: %s{reply}"
    with ex ->
        FsHotWatch.Logging.warn "cli-confirm" $"the forced rebuild request failed: %s{ex.Message}"

/// `ScopeCommands.requestFullRun` over the daemon — how `fshw confirm` FORCES the run it
/// demands. The caller's `settle` is the authoritative bound.
let internal forceFullSuiteRun (ipc: IpcOps) (pipeName: string) : unit =
    ScopeCommands.requestFullRun (scopeSend ipc pipeName)

/// Force a fresh from-disk scan, then ride the daemon's next scan completion.
///
/// This is what makes `check`/`confirm` trust DISK rather than the watcher: on a warm
/// daemon `WaitForScan` ALONE returns the last completed scan's generation immediately
/// (`WaitForScanGeneration(-1L)` is already-satisfied once any scan has ever run), so an
/// idle edit the file-watcher missed would never be re-read and the verdict would replay
/// a stale content-addressed result. `Scan` calls `RequestScan` → `performScan`, which
/// re-reads every registered file from disk; the `-1L` wait then hands off to the
/// caller's authoritative `settle`.
///
/// The pre-verdict scan, and the ONE definition of "make the tree fresh": the watcher is
/// an optimization, never the source of truth, and `fshw scan` is not a required manual
/// pre-step. It runs once, before the reading the verdict is decided from — nothing
/// re-scans afterwards to look for a better answer.
let internal forceScanAndWait (ipc: IpcOps) (pipeName: string) : string =
    ipc.Scan pipeName |> Async.RunSynchronously |> ignore
    // No client timeout by design: `-1L` is a scan generation, and the daemon's RPC seam deadline bounds this wait.
    ipc.WaitForScan pipeName -1L |> Async.RunSynchronously

let private ensureAndQueryErrors
    (mode: ProgressRenderer.RenderMode)
    (checkMode: CheckVerdict.CheckMode)
    (invocation: Verdict.Invocation)
    (repoRoot: string)
    (excludePatterns: string list)
    (noWarnFail: bool)
    (ensureDaemon: unit -> bool)
    (waitReady: unit -> DaemonReadiness)
    (forceRestart: unit -> bool)
    (ipc: IpcOps)
    (pipeName: string)
    (pluginFilter: string)
    : int =
    // A daemon that can't even be launched, that crashed during startup, or that
    // never became responsive is UN-COMPLETABLE — exit 2, never exit 1. Only once
    // the readiness gate confirms the daemon is answering RPCs do we issue the
    // check; a connect fault after that (mid-check crash) is likewise exit 2 via
    // `withCheckIpc`. This closes the startup-race hole where an RPC issued while
    // the daemon was still cold-scanning timed out and poisoned the verdict as
    // exit 1 ("failures found") for an autonomous loop.
    if not (ensureDaemon ()) then
        eprintfn "%s" (DaemonStartupFailure.describeLaunchFailure (DaemonStartupFailure.tryRead repoRoot))
        2
    else
        match waitReady () with
        | DaemonReadiness.Crashed ->
            eprintfn
                "The daemon stopped responding during startup (it appears to have exited). \
                 Nothing was checked — see logs/daemon.log, then re-run `fshw check`."

            2
        | DaemonReadiness.TimedOut ->
            eprintfn
                "The daemon did not become ready in time — it may be wedged mid-startup. \
                 Nothing was checked — see logs/daemon.log."

            2
        | DaemonReadiness.Ready ->
            // `confirm` declares its scope BEFORE anything runs: the scan below provokes
            // the test run, and that run must already be unfiltered — asking afterwards
            // would only learn that it wasn't.
            if checkMode = CheckVerdict.Confirmation then
                requestFullSuiteScope ipc pipeName
                // Ordered BEFORE the forced scan below: the scan is what triggers the
                // build, so the flag has to be set by the time the build plugin computes
                // its cache key. A forced scan alone does not help — it re-reads the same
                // bytes, so the source merkle is unchanged and the cache hits again.
                forceRealBuild ipc pipeName

            withCheckIpc forceRestart (fun () ->
                IpcOutput.pollAndRenderForInvocation
                    invocation
                    mode
                    checkMode
                    repoRoot
                    excludePatterns
                    (renderLines mode (not noWarnFail))
                    noWarnFail
                    // Force a fresh from-disk scan up front — do NOT merely WAIT for one;
                    // see `forceScanAndWait`.
                    (fun () -> forceScanAndWait ipc pipeName)
                    // Authoritative settle: block until the daemon reports its sound
                    // verdict (`waitForVerdict`). `-1` = no client-imposed timeout; the
                    // daemon bounds the wait with its hard verdict deadline
                    // (`resolveVerdictDeadline`, FSHW_VERDICT_DEADLINE_SEC, default 60
                    // min) so this can never block forever — a breach surfaces via
                    // `isVerdictWaitTimeout` as a diagnostic exit 2 naming the wedged
                    // plugin.
                    (fun () -> ipc.WaitForComplete pipeName -1 |> Async.RunSynchronously)
                    (fun () -> ipc.GetStatus pipeName |> Async.RunSynchronously)
                    (fun () -> ipc.GetDiagnostics pipeName pluginFilter |> Async.RunSynchronously)
                    // What the last completed run actually covered. Read fresh at every
                    // verdict point, from the daemon, never inferred from what we asked
                    // for. The inner loop reads it too — and ignores it — so there is one
                    // verdict path, not two that can drift.
                    (fun () -> readTestRun ipc pipeName)
                    // What `check` would have reached in the run this
                    // confirm did not have to escalate. Read at publish time, not here.
                    (fun () -> readCheckReach ipc pipeName)
                    // `confirm`'s teeth — see `CheckVerdict.confirmNeedsFullRun`.
                    (fun () -> forceFullSuiteRun ipc pipeName))

/// The identity of one `.fshw.json` text (`""` for no file): what `.fshw/config.hash`
/// records. A daemon publishes it for the text it PARSED; a CLI compares it with the
/// hash of the file as it is now.
let configContentHash (configContent: string) : string =
    let hash =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configContent))

    Convert.ToHexStringLower(hash).Substring(0, 16)

/// Compute a hash of the `.fshw.json` config content for restart-on-config-change
/// detection (injectable). The CLI BINARY is deliberately NOT part of this hash:
/// binary staleness is the DaemonIdentity handshake's job (assembly version +
/// content hash, recorded by the daemon, compared by the CLI), not an mtime here.
let computeConfigHashWith (fileOps: FileOps) (repoRoot: string) =
    let configPath = Path.Combine(repoRoot, ".fshw.json")

    let configContent =
        if fileOps.FileExists configPath then
            fileOps.ReadAllText configPath
        else
            ""

    configContentHash configContent

/// Compute a hash of the config file for staleness detection.
let private computeConfigHash (repoRoot: string) =
    computeConfigHashWith defaultFileOps repoRoot

/// What `ensureDaemon` should do with a daemon KNOWN to be listening. The
/// not-running case is deliberately NOT representable here — `ensureDaemon`
/// always starts fresh in that case, so there is no decision to encode.
type RunningDaemonAction =
    | Reuse
    /// The running daemon's recorded binary identity is not this CLI's — a different
    /// binary, or no record at all (a build that predates the handshake). Restart it
    /// with THIS binary: a new CLI must never silently talk to an old daemon.
    | RestartStaleBinary of DaemonIdentity.StaleReason
    /// `.fshw.json` changed since the daemon started — restart to load it.
    | RestartConfigChanged

/// Decide what to do with a listening daemon. Identity is checked FIRST: a
/// stale binary must restart even when the config hash happens to match,
/// because every answer that daemon gives comes from the wrong code.
let decideRunningDaemonAction
    (identity: DaemonIdentity.IdentityVerdict)
    (storedHash: string)
    (currentHash: string)
    : RunningDaemonAction =
    match identity with
    | DaemonIdentity.IdentityVerdict.Stale reason -> RestartStaleBinary reason
    | DaemonIdentity.IdentityVerdict.Match ->
        if storedHash = currentHash then
            Reuse
        else
            RestartConfigChanged

/// The one-line reason printed when `ensureDaemon` restarts a running daemon —
/// the tool says WHAT it found and WHAT it is doing, never a bare restart.
let restartReasonLine (action: RunningDaemonAction) : string option =
    match action with
    | Reuse -> None
    | RestartStaleBinary DaemonIdentity.StaleReason.NotRecorded ->
        Some
            "  The running daemon has no recorded binary identity (an fshw build that \
             predates the identity handshake) — restarting it with this binary..."
    | RestartStaleBinary(DaemonIdentity.StaleReason.DifferentBinary recorded) ->
        Some
            $"  The running daemon was started from a different fshw binary \
               (%s{recorded.Version}) — restarting it with this one..."
    | RestartConfigChanged -> Some "  Daemon config changed — restarting..."

/// Kill a stale daemon process by PID file (injectable).
let killStaleDaemonWith (fileOps: FileOps) (processOps: ProcessOps) (repoRoot: string) =
    let pidPath = Path.Combine(repoRoot, ".fshw", "daemon.pid")

    if fileOps.FileExists pidPath then
        try
            let pid = (fileOps.ReadAllText pidPath).Trim() |> int

            try
                let proc = processOps.GetProcessById pid
                eprintfn "  Killing stale daemon (PID %d)..." pid
                processOps.KillProcess proc
                processOps.WaitForExit proc 5000 |> ignore
            with ex ->
                eprintfn "  Could not kill PID %d: %s" pid ex.Message

            fileOps.DeleteFile pidPath
        with ex ->
            eprintfn "  Could not clean up stale daemon: %s" ex.Message

/// Kill a stale daemon process by PID file.
let private killStaleDaemon (repoRoot: string) =
    killStaleDaemonWith defaultFileOps defaultProcessOps repoRoot

/// Start a fresh daemon process (injectable for testing).
let startFreshDaemonWith
    (fileOps: FileOps)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    let logDir =
        if Path.IsPathRooted(logDirName) then
            logDirName
        else
            Path.Combine(repoRoot, logDirName)

    fileOps.CreateDirectory logDir
    let logFile = Path.Combine(logDir, DaemonConfig.DaemonLog.FileName)
    // A refusal recorded by an EARLIER launch must never be read back as this one's.
    let startupFailure = DaemonStartupFailure.path repoRoot

    if fileOps.FileExists startupFailure then
        fileOps.DeleteFile startupFailure

    eprintfn "Starting daemon... (log: %s)" logFile
    // No `config.hash` is written here: only the daemon knows which configuration it
    // actually parsed, and it publishes that before its pipe listens. A hash written by
    // the launcher would attest to a file the daemon may never have loaded.
    let launched =
        try
            ipc.LaunchDaemon repoRoot extraArgs logFile
            true
        with ex ->
            // Nothing was started, so there is no pipe worth waiting for.
            eprintfn "  Could not launch the daemon: %s" ex.Message
            false

    let deadline =
        if launched then
            DateTime.UtcNow.AddSeconds(startupTimeoutSeconds)
        else
            DateTime.MinValue

    let mutable isUp = ipc.IsRunning pipeName
    // A daemon that recorded a refusal has exited: stop waiting for a pipe that will
    // never come up, so the reason is printed now rather than after the timeout.
    let mutable refused = fileOps.FileExists startupFailure

    while not isUp && not refused && DateTime.UtcNow < deadline do
        Thread.Sleep(100)
        isUp <- ipc.IsRunning pipeName
        refused <- fileOps.FileExists startupFailure

    isUp

let private startFreshDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    startFreshDaemonWith defaultFileOps ipc repoRoot pipeName extraArgs logDirName startupTimeoutSeconds

let private ensureDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fshw")
    let hashPath = Path.Combine(stateDir, "config.hash")
    let currentHash = computeConfigHash repoRoot

    if not (ipc.IsRunning pipeName) then
        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName extraArgs logDirName startupTimeoutSeconds
    else
        let storedHash =
            if File.Exists hashPath then
                File.ReadAllText(hashPath).Trim()
            else
                ""

        // The identity handshake: compare the running daemon's recorded binary identity
        // (assembly version + content hash, written by the daemon at startup) against
        // this CLI's own. The comparison is UNILATERAL — an old daemon that never
        // recorded an identity needs no cooperation to be found stale; it reads as
        // `NotRecorded`, which restarts it. So a new CLI can never silently "verify"
        // anything through an old daemon.
        match decideRunningDaemonAction (DaemonIdentity.verdictFor repoRoot) storedHash currentHash with
        | Reuse -> true
        | restart ->
            restartReasonLine restart |> Option.iter (eprintfn "%s")

            try
                ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
                Thread.Sleep(1000)
            with ex ->
                eprintfn "  Shutdown request failed: %s" ex.Message

            killStaleDaemon repoRoot
            startFreshDaemon ipc repoRoot pipeName extraArgs logDirName startupTimeoutSeconds

// ----------------------------------------------------------------------------
// Daemon readiness gate.
//
// `ensureDaemon` returns as soon as the named pipe is *listening* (`IsRunning` — a
// 500 ms probe connect). But a daemon that just (re)started is often still mid
// cold-scan (analyzer reflection load pegging cores), so the FIRST real RPC issued by
// a check — `ConnectAsync(5000)` inside `IpcClient.invoke` — can time out because the
// acceptor is starved, or hit a pipe endpoint briefly torn down during a stop→start.
// Such a transient fault must not surface as exit 1 ("failures found"), which would
// poison an autonomous loop's verdict. The gate below RETRIES transient connect faults
// against a startup deadline (distinct from the per-RPC connect timeout) until the
// daemon answers, fails FAST (exit 2) if the daemon process is provably gone, and
// gives up (exit 2) if it never becomes responsive.
// ----------------------------------------------------------------------------

/// True when `ex` is a connect-phase transient — the daemon is reachable-in-principle
/// but not yet answering because it is mid-startup (cold scan / analyzer load) or
/// briefly tore down a pipe endpoint during a restart. These are RETRIED by the
/// readiness gate rather than surfaced as a hard failure.
///
/// `ConnectionLostException` is matched by type-name substring so there is no
/// compile-time dependency on the transport assembly. Walks `InnerException` so an
/// `AggregateException` from `Async.RunSynchronously` is seen through.
let rec isTransientConnectFault (ex: exn) : bool =
    match ex with
    | null -> false
    | :? TimeoutException -> true
    | :? System.IO.IOException -> true
    | :? System.ObjectDisposedException -> true
    | _ ->
        ex.GetType().FullName.Contains("ConnectionLost", StringComparison.Ordinal)
        || (not (isNull ex.InnerException) && isTransientConnectFault ex.InnerException)

/// The next action for one readiness-probe iteration.
[<RequireQualifiedAccess>]
type ReadinessStep =
    | ProceedReady
    | KeepWaiting
    | FailCrashed
    | FailTimedOut

/// Pure decision for one readiness-probe iteration. Ordering matters: a
/// NON-transient probe error means the daemon was reached (it answered, just not
/// with a clean status) so we PROCEED and let the real check surface it; only a
/// transient connect fault consults liveness (fail fast if the process is gone)
/// and the deadline (give up as un-completable) before waiting again.
let decideReadinessStep (probe: Result<unit, exn>) (daemonAlive: bool) (deadlineReached: bool) : ReadinessStep =
    match probe with
    | Ok() -> ReadinessStep.ProceedReady
    | Error ex ->
        if not (isTransientConnectFault ex) then
            ReadinessStep.ProceedReady
        elif not daemonAlive then
            ReadinessStep.FailCrashed
        elif deadlineReached then
            ReadinessStep.FailTimedOut
        else
            ReadinessStep.KeepWaiting

/// True unless the daemon's recorded PID is PROVABLY gone. Reads `.fshw/daemon.pid`;
/// a missing or unparseable pidfile is treated as ALIVE (unknown ⇒ keep waiting,
/// never mis-declare a crash), while a pid whose process no longer exists is a
/// proven crash (fail fast). Injectable file ops for testing.
let daemonProcessAliveWith (fileOps: FileOps) (repoRoot: string) : bool =
    let pidPath = Path.Combine(repoRoot, ".fshw", "daemon.pid")

    if not (fileOps.FileExists pidPath) then
        true
    else
        match Int32.TryParse((fileOps.ReadAllText pidPath).Trim()) with
        | false, _ -> true
        | true, pid ->
            try
                not (System.Diagnostics.Process.GetProcessById(pid).HasExited)
            with
            | :? ArgumentException -> false // no process with that id — proven dead
            | _ -> true // any other probe error — assume alive rather than false-crash

/// Stale pidfile hygiene: delete `.fshw/daemon.pid` when the process it names is
/// PROVABLY dead — cleaned up on the next command, not left for an external reaper.
/// Uses the SAME liveness read as the readiness gate (`daemonProcessAliveWith`), whose
/// unknowns all lean ALIVE, so a missing, unparseable, or undecidable pidfile is never
/// deleted (it might belong to a live daemon). Returns true iff a stale pidfile was
/// removed, and says so on stderr: hygiene the user can see, not infer.
let cleanStalePidfileWith (fileOps: FileOps) (repoRoot: string) : bool =
    let pidPath = Path.Combine(repoRoot, ".fshw", "daemon.pid")

    if fileOps.FileExists pidPath && not (daemonProcessAliveWith fileOps repoRoot) then
        try
            fileOps.DeleteFile pidPath
            eprintfn "  Cleaned up a stale daemon.pid (its process is gone)."
            true
        with ex ->
            FsHotWatch.Logging.debug
                "cli-hygiene"
                $"could not delete stale daemon.pid: %s{ex.GetType().Name}: %s{ex.Message}"

            false
    else
        false

let private cleanStalePidfile (repoRoot: string) : unit =
    cleanStalePidfileWith defaultFileOps repoRoot |> ignore

// --- `stop`: proving the daemon PROCESS is gone -------------------------------
//
// The client and the daemon are separate processes. `stop` puts a shutdown
// REQUEST on the pipe; the daemon acknowledges it and then unwinds its own work
// in its own time. So a quiet pipe proves the LISTENER is gone and nothing more
// — the process can still be scanning files after the last endpoint closed. The
// code below never conflates the two: only a process proven to have left the
// process table earns the success line.

/// `kill(2)`. Signal 0 runs the existence and permission checks and delivers
/// nothing. It is the only liveness probe that holds for this daemon: its argv is
/// a bare `FsHotWatch.Cli.dll start` carrying neither the tool name nor the
/// workspace path, so no name- or path-based match can find it, and the `ps` and
/// `%cpu` readings available here are not dependable either.
[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private signalProcess(int pid, int signal)

/// `errno` for "no such process" — 3 on both macOS and Linux.
[<Literal>]
let private ESRCH = 3

/// True unless `pid` is PROVABLY gone, as decided by a `kill(pid, 0)` probe:
///
/// | result      | meaning                                       | verdict |
/// |-------------|-----------------------------------------------|---------|
/// | `0`         | the process exists and we may signal it        | alive   |
/// | `-1` ESRCH  | no such process — the one answer proving death | dead    |
/// | `-1` other  | exists but not ours (EPERM), or probe failed   | alive   |
///
/// Every unknown leans ALIVE, the same convention `daemonProcessAliveWith` uses:
/// mis-declaring a live daemon dead is how a pidfile gets deleted out from under a
/// running process. Probe and `errno` reader are injected so the table above is
/// testable without a process to kill.
let internal processAliveByProbe (probe: int -> int) (lastError: unit -> int) (pid: int) : bool =
    if pid <= 0 then false
    elif probe pid = 0 then true
    else lastError () <> ESRCH

/// The production liveness probe. Falls back to the managed process table where
/// there is no `libc` `kill` — a host that could not have launched this daemon in
/// the first place, since the detached launch itself calls `setsid`/`execv`.
let private daemonPidAlive (pid: int) : bool =
    try
        processAliveByProbe (fun p -> signalProcess (p, 0)) Marshal.GetLastPInvokeError pid
    with
    | :? EntryPointNotFoundException
    | :? DllNotFoundException ->
        try
            not (System.Diagnostics.Process.GetProcessById(pid).HasExited)
        with
        | :? ArgumentException -> false
        | _ -> true

/// The pid `.fshw/daemon.pid` names, or `None` when there is no pidfile or it does
/// not hold a positive number. `None` means "nothing to watch", never "nothing is
/// running" — the callers keep those apart.
let internal readDaemonPidWith (fileOps: FileOps) (repoRoot: string) : int option =
    let pidPath = Path.Combine(repoRoot, ".fshw", "daemon.pid")

    if not (fileOps.FileExists pidPath) then
        None
    else
        try
            match Int32.TryParse((fileOps.ReadAllText pidPath).Trim()) with
            | true, pid when pid > 0 -> Some pid
            | _ -> None
        with _ ->
            None

/// The process-liveness, clock and sleep boundaries `stop` needs, injected so its
/// outcome can be decided in a test with no daemon to kill and no real waiting.
type StopOps =
    { IsProcessAlive: int -> bool
      Now: unit -> DateTime
      SleepMs: int -> unit }

let defaultStopOps: StopOps =
    { IsProcessAlive = daemonPidAlive
      Now = fun () -> DateTime.UtcNow
      SleepMs = fun (ms: int) -> Thread.Sleep ms }

/// How long `stop` keeps asking a live pipe to shut down before giving up.
[<Literal>]
let internal StopPipeQuietSeconds = 30.0

/// How long `stop` then waits for the daemon PROCESS to leave the process table
/// before saying, in as many words, that it is still there.
[<Literal>]
let internal StopProcessExitSeconds = 10.0

/// What a `stop` established about the daemon PROCESS — never about the pipe alone.
[<RequireQualifiedAccess>]
type StopOutcome =
    /// Nothing answered the pipe and no recorded process is alive. A no-op.
    | NothingRunning
    /// Shutdown was delivered and the recorded process is gone. The only success.
    | Stopped of daemons: int
    /// The recorded process was still alive at the deadline — whether or not the
    /// pipe ever answered. Carries the pid so the operator can check it directly.
    | StillAlive of pid: int * daemons: int
    /// Shutdown was delivered and the pipe went quiet, but there was no usable
    /// `.fshw/daemon.pid` to watch, so the exit is unproven in either direction.
    | Unverified of daemons: int

/// Ask every daemon on `pipeName` to shut down, then wait — bounded — for the
/// process `.fshw/daemon.pid` names to actually leave the process table.
///
/// What is signalled is unchanged: the shutdown REQUEST over IPC, iterated until
/// the pipe has been quiet for two consecutive probes (several daemons may share
/// one pipe, and the OS may still be tearing down the last endpoint). Nothing here
/// signals a pid. That matters for pid reuse: this code never delivers anything to
/// the number in the pidfile, so it cannot hit an unrelated process that inherited
/// it. Reuse can still make a dead daemon LOOK alive, which under-claims — and
/// under-claiming is the safe direction for a verb whose whole job is not to
/// over-claim.
let internal stopDaemonWith
    (ipc: IpcOps)
    (fileOps: FileOps)
    (stopOps: StopOps)
    (repoRoot: string)
    (pipeName: string)
    : StopOutcome =
    let pidPath = Path.Combine(repoRoot, ".fshw", "daemon.pid")

    // Read the pid BEFORE asking anything to shut down: a daemon that exits
    // cleanly deletes its own pidfile on the way out, so afterwards there is
    // nothing left to watch and every stop would read as unverifiable.
    let recordedPid = readDaemonPidWith fileOps repoRoot
    let pipeDeadline = stopOps.Now().AddSeconds StopPipeQuietSeconds
    let mutable delivered = 0
    let mutable consecutiveQuiet = 0

    while consecutiveQuiet < 2 && stopOps.Now() < pipeDeadline do
        if ipc.IsRunning pipeName then
            consecutiveQuiet <- 0

            // One failed shutdown is tolerated — the OS may be tearing down the
            // pipe mid-call. Logged at debug so it stays diagnosable.
            try
                ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
                delivered <- delivered + 1
            with ex ->
                FsHotWatch.Logging.debug "cli-stop" $"Shutdown attempt failed: %s{ex.GetType().Name}: %s{ex.Message}"
        else
            consecutiveQuiet <- consecutiveQuiet + 1

        stopOps.SleepMs 100

    match recordedPid with
    | None ->
        // No pid to watch. If nothing answered the pipe either, nothing was
        // running; if something did, the exit is simply unproven — say so.
        if delivered = 0 then
            StopOutcome.NothingRunning
        else
            StopOutcome.Unverified delivered
    | Some pid ->
        // Two independent proofs that the daemon we were watching is gone, because
        // either alone can be wrong:
        //   * the pidfile no longer names `pid` — the daemon deletes its own on a
        //     clean exit, and this still holds if the pid has since been REUSED;
        //   * `kill(pid, 0)` reports no such process — this holds when the daemon
        //     was killed hard and never reached its own cleanup.
        let gone () =
            readDaemonPidWith fileOps repoRoot <> Some pid
            || not (stopOps.IsProcessAlive pid)

        let exitDeadline = stopOps.Now().AddSeconds StopProcessExitSeconds

        while not (gone ()) && stopOps.Now() < exitDeadline do
            stopOps.SleepMs 100

        if not (gone ()) then
            // The pidfile stays: it names a process that is alive, and stranding a
            // live daemon beyond the reach of the next `stop` is the worse failure.
            StopOutcome.StillAlive(pid, delivered)
        else
            // Hygiene, in the one case that is safe: the pidfile still names the pid
            // we just watched leave the process table, which is what a `kill -9` or a
            // SIGTERM leaves behind. A pidfile naming anything else belongs to some
            // other daemon and is not ours to delete.
            if
                readDaemonPidWith fileOps repoRoot = Some pid
                && not (stopOps.IsProcessAlive pid)
            then
                try
                    fileOps.DeleteFile pidPath
                with ex ->
                    FsHotWatch.Logging.debug
                        "cli-stop"
                        $"could not delete daemon.pid: %s{ex.GetType().Name}: %s{ex.Message}"

            if delivered = 0 then
                StopOutcome.NothingRunning
            else
                StopOutcome.Stopped delivered

/// What `stop` prints, and the exit code that goes with it. The exit code is part of
/// the report, not decoration: a wrapper reads the code and never the prose, so a
/// message that claims nothing beside an exit 0 that claims everything is the same
/// lie in a quieter voice. Three codes, and they are the ones this CLI already uses
/// everywhere else (see `withCheckIpc`):
///
///   0 — established: the daemon is gone, or there was never one to stop.
///   1 — established the opposite: it is still running. `stop` did not stop it.
///   2 — NOT established either way. The same "completeness unachievable" this tool
///       gives a check that could not reach a verdict, for the same reason: the run
///       did not produce the evidence its success would be made of.
let internal reportStopOutcome (outcome: StopOutcome) : int =
    match outcome with
    | StopOutcome.NothingRunning ->
        UI.info "No daemon running"
        0
    | StopOutcome.Stopped 1 ->
        UI.success "Daemon stopped"
        0
    | StopOutcome.Stopped n ->
        UI.success $"%d{n} daemons stopped"
        0
    | StopOutcome.Unverified n ->
        // Exit 2, not 0. The benign reading — a daemon already on its way out, which
        // deleted its own pidfile before this `stop` looked — is real but
        // indistinguishable from a pidfile somebody removed out from under a daemon
        // that is still very much alive. Nothing here can tell those apart, and
        // "probably fine" is the claim this whole verb exists to stop making.
        UI.warn
            $"Shutdown delivered to %d{n} daemon(s) and the pipe is quiet, but .fshw/daemon.pid names no \
              process — whether one exited cannot be shown from here."

        UI.dimInfo "Nothing above says a daemon stopped, and nothing says one is running."
        UI.dimInfo "`fshw status` shows whether one is still answering."
        2
    | StopOutcome.StillAlive(pid, delivered) ->
        let sent =
            if delivered = 0 then
                "The pipe did not answer"
            else
                $"Shutdown was delivered to %d{delivered} daemon(s)"

        UI.warn
            $"%s{sent}, but the daemon process (pid %d{pid}) is STILL RUNNING \
              %.0f{StopProcessExitSeconds}s later. It was NOT stopped."

        UI.dimInfo $"Check it:  kill -0 %d{pid}    (exits 0 while the process is there)"
        UI.dimInfo $"End it:    kill %d{pid}       (plain SIGTERM)"
        UI.dimInfo ".fshw/daemon.pid is left in place — it names a process that is alive."
        1

/// The words `fshw status` prints when the running daemon's binary is not this CLI's:
/// status output computed by the wrong binary must never be presented silently as
/// current. Status itself does not restart (it is a read-only observer and a fresh
/// daemon would have nothing to report); the work-triggering verbs do, automatically.
let staleIdentityStatusWarning (reason: DaemonIdentity.StaleReason) : string =
    let what =
        match reason with
        | DaemonIdentity.StaleReason.NotRecorded ->
            "has no recorded binary identity (an fshw build that predates the identity handshake)"
        | DaemonIdentity.StaleReason.DifferentBinary recorded ->
            $"was started from a different fshw binary (%s{recorded.Version})"

    $"⚠ the running daemon %s{what} — this status reflects THAT build; \
      the next check/confirm/format/scan command restarts it automatically"

/// Effectful readiness gate. Probes the daemon with a lightweight RPC until it
/// answers (`Ready`), the daemon process is proven gone (`Crashed`), or the
/// readiness deadline elapses (`TimedOut`). Transient connect faults during a
/// cold-scan startup are retried after a one-time visible progress line. All
/// effects (probe / liveness / clock / sleep / progress) are injected so the loop
/// is deterministically testable.
let waitForDaemonReadyWith
    (probe: unit -> Result<unit, exn>)
    (isDaemonAlive: unit -> bool)
    (now: unit -> DateTime)
    (sleep: int -> unit)
    (onWaiting: unit -> unit)
    (pollMs: int)
    (deadlineSeconds: float)
    : DaemonReadiness =
    let deadline = now().AddSeconds(deadlineSeconds)
    let mutable announced = false

    let rec loop () =
        match probe () with
        | Ok() -> DaemonReadiness.Ready
        | Error ex ->
            match decideReadinessStep (Error ex) (isDaemonAlive ()) (now () >= deadline) with
            | ReadinessStep.ProceedReady -> DaemonReadiness.Ready
            | ReadinessStep.FailCrashed -> DaemonReadiness.Crashed
            | ReadinessStep.FailTimedOut -> DaemonReadiness.TimedOut
            | ReadinessStep.KeepWaiting ->
                if not announced then
                    onWaiting ()
                    announced <- true

                sleep pollMs
                loop ()

    loop ()

/// Readiness deadline for the check path. Deliberately generous (and DISTINCT
/// from the 5 s per-RPC connect timeout) so a cold-scan startup — analyzer
/// reflection load pegging cores — has room to become RPC-responsive before the
/// check is declared un-completable.
[<Literal>]
let DaemonReadinessTimeoutSeconds = 60.0

/// Production readiness gate: probe the daemon with a cheap `GetStatus` RPC,
/// retrying transient connect faults until it answers or the deadline elapses.
let private waitForDaemonReady
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (deadlineSeconds: float)
    : DaemonReadiness =
    let probe () =
        try
            ipc.GetStatus pipeName |> Async.RunSynchronously |> ignore
            Ok()
        with ex ->
            Error(unwrapIpcException ex)

    waitForDaemonReadyWith
        probe
        (fun () -> daemonProcessAliveWith defaultFileOps repoRoot)
        (fun () -> DateTime.UtcNow)
        (fun ms -> Thread.Sleep ms)
        (fun () -> eprintfn "  Waiting for the daemon to finish starting...")
        200
        deadlineSeconds

/// Options assembled from parsed global flags.
type GlobalOptions =
    {
        NoCache: bool
        NoWarnFail: bool
        AgentMode: bool
        CompactMode: bool
        /// Global `-v/--verbose` was set. Drives debug logging AND `dead-code`'s
        /// "why is this unreachable" reasons (the standalone test-prune CLI spells
        /// the latter as its own `--verbose`; here the global flag carries it).
        Verbose: bool
        DaemonExtraArgs: string
    }

let defaultGlobalOptions =
    { NoCache = false
      NoWarnFail = false
      AgentMode = false
      CompactMode = false
      Verbose = false
      DaemonExtraArgs = "" }

/// Delete a file, swallowing any exception: in a bulk cleanup loop one bad item
/// shouldn't halt the rest. The exception class is logged at debug so failures are
/// still diagnosable on `--verbose`.
let tryDeleteForCleanup (path: string) : string option =
    try
        File.Delete(path)
        Some path
    with ex ->
        FsHotWatch.Logging.debug "cli-cleanup" $"Skipping %s{path}: %s{ex.GetType().Name}: %s{ex.Message}"
        None

/// Delete coverage baseline + partial JSON for every configured test project
/// (skipping those with coverage opted out). Returns the list of paths that
/// were actually removed — empty when nothing was present. Pure wrt. the
/// filesystem inputs; safe to call when the coverage directory doesn't exist.
let refreshCoverageBaseline (repoRoot: string) (config: DaemonConfiguration) : string list =
    match config.Tests with
    | None -> []
    | Some t ->
        t.Projects
        |> List.filter (fun p -> p.Coverage)
        |> List.collect (fun p ->
            let dir = Path.Combine(repoRoot, t.CoverageDir, p.Project)

            [ FsHotWatch.TestPrune.TestPrunePlugin.BaselineName
              FsHotWatch.TestPrune.TestPrunePlugin.PartialName ]
            |> List.map (fun name -> Path.Combine(dir, name))
            |> List.filter File.Exists
            |> List.choose tryDeleteForCleanup)

// ----------------------------------------------------------------------------
// Run-level hooks.
//
// A `beforeRun`/`afterRun` pair that brackets a WHOLE `check`/`confirm` run —
// distinct from `tests.beforeRun`, which the daemon runs per test run inside its
// tests slot. The first consumer uses them to acquire and release a box-wide
// gate-lock so two concurrent runs serialize.
//
//   * `beforeRun` is a FAIL-CLOSED preflight: it runs BEFORE the daemon is
//     contacted (it IS the lock acquire), and a non-zero exit aborts with exit 2
//     (un-completable, NOT the "failures found" exit 1) and runs no plugin work.
//   * `afterRun` is a `finally`: it fires on success, on a red verdict, and on a
//     throw. A plain `finally` does NOT run when the process is signalled, so
//     SIGINT/SIGTERM handlers fire it too — sharing ONE idempotency latch, so it
//     runs EXACTLY once. Its own exit code is best-effort: a failing afterRun is
//     logged loudly but never flips the run's verdict.
//
// SIGKILL is OUT OF SCOPE — it cannot be trapped, so a lock leaked by `kill -9` is
// the consumer's TTL problem to reclaim.
// ----------------------------------------------------------------------------

/// The timeout (seconds) bounding each run-level hook: `runHookTimeoutSec` →
/// global `timeoutSec` → `DefaultGlobalTimeoutSec`. A run-level hook is ALWAYS
/// bounded — even when the global timeout is explicitly disabled — because a lock
/// hook that hangs forever is worse than the lock it guards.
let internal resolveRunHookTimeoutSec (config: DaemonConfiguration) : int =
    config.RunHookTimeoutSec
    |> Option.orElse config.TimeoutSec
    |> Option.defaultValue DefaultGlobalTimeoutSec

/// Wrap a run-hook callback in an idempotency latch: the returned closure runs
/// `run` at most ONCE, no matter how many times (or from how many threads) it is
/// invoked. The `finally` and every signal handler call the SAME latched closure,
/// so afterRun fires exactly once. Pure wrt the injected `run`, so the
/// exactly-once contract is unit-testable without a real shell-out or signal.
let internal makeRunOnce (run: unit -> unit) : unit -> unit =
    let latch = ref 0

    fun () ->
        if Interlocked.Exchange(latch, 1) = 0 then
            run ()

/// What a run-level signal handler does: run `afterRun` (once, via the shared latch the
/// caller wove into it) then `exitWith code`. Extracted from the registration lambdas so
/// the contract is unit-testable WITHOUT delivering a real OS signal, which in a test
/// host could trip the runner's own signal handling. `128 + signum` is the shell
/// convention for "killed by signal N".
let internal onRunSignal (afterRun: unit -> unit) (exitWith: int -> unit) (code: int) : unit =
    afterRun ()
    exitWith code

/// Install SIGINT (via `Console.CancelKeyPress`) and SIGTERM (via POSIX
/// `PosixSignalRegistration`) handlers that run `onRunSignal afterRun exitWith` — a
/// plain `finally` does NOT run when the process is signalled, so without this afterRun
/// would be skipped on exactly the abort path a gate-lock release cannot afford to miss.
/// Each handler cancels the default terminate (`e.Cancel`/`ctx.Cancel <- true`) so the
/// process stays alive long enough to run afterRun. Returns a disposable that
/// unregisters both.
///
/// `exitWith` is INJECTED so a test can signal itself and observe afterRun fire without
/// the handler terminating the test process; production passes `exit`.
let internal installRunSignalHandlers (afterRun: unit -> unit) (exitWith: int -> unit) : IDisposable =
    let onCancelKey =
        ConsoleCancelEventHandler(fun _ (e: ConsoleCancelEventArgs) ->
            e.Cancel <- true
            onRunSignal afterRun exitWith 130) // 128 + SIGINT(2)

    Console.CancelKeyPress.AddHandler onCancelKey

    let sigterm =
        PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            fun (ctx: PosixSignalContext) ->
                ctx.Cancel <- true
                onRunSignal afterRun exitWith 143 // 128 + SIGTERM(15)
        )

    { new IDisposable with
        member _.Dispose() =
            Console.CancelKeyPress.RemoveHandler onCancelKey
            sigterm.Dispose() }

/// The timed-hook plumbing BOTH run brackets share: run ONE hook, measure it against
/// the invocation's clock, and accumulate the `HookVerdict` + `TimingSpan` pair a
/// verdict records as its evidence. Locked, because the ordinary path and a signal
/// finalizer can both reach it.
type internal RunHookRunner =
    {
        /// `scope` (`run.beforeRun` / `run.afterRun`) -> label -> command ->
        /// (succeeded, captured output).
        RunTimed: string -> string -> string -> bool * string
        /// Everything measured so far, in the order it ran.
        Collected: unit -> Verdict.HookVerdict list * Verdict.TimingSpan list
    }

let internal makeRunHookRunner
    (repoRoot: string)
    (config: DaemonConfiguration)
    (invocation: Verdict.Invocation)
    : RunHookRunner =
    let timeoutSec = Some(resolveRunHookTimeoutSec config)
    let hookEvidence = ResizeArray<Verdict.HookVerdict * Verdict.TimingSpan>()

    { RunTimed =
        fun scope label cmd ->
            let startOffsetMs = Verdict.Invocation.elapsedMs invocation
            let stopwatch = Diagnostics.Stopwatch.StartNew()
            let success, output = makeShellHookWithResult label timeoutSec repoRoot cmd ()
            stopwatch.Stop()

            lock hookEvidence (fun () ->
                hookEvidence.Add(
                    { Scope = scope
                      StepIndex = 1
                      StepCount = 1
                      Command = cmd
                      ElapsedMs = stopwatch.ElapsedMilliseconds
                      Outcome = if success then "ok" else "fail" },
                    { Scope = scope
                      StartOffsetMs = startOffsetMs
                      ElapsedMs = stopwatch.ElapsedMilliseconds
                      Detail = Some cmd }
                ))

            success, output
      Collected = fun () -> lock hookEvidence (fun () -> hookEvidence |> Seq.toList |> List.unzip) }

/// afterRun as a latched, best-effort teardown. `makeShellHookWithResult`
/// already logs a failure (and its output) at error; the extra line here says
/// the ONE thing that matters at this layer — the verdict is unchanged.
let internal makeAfterRunHook (config: DaemonConfiguration) (runner: RunHookRunner) : unit -> unit =
    makeRunOnce (fun () ->
        match config.AfterRun with
        | None -> ()
        | Some cmd ->
            let (success, _) = runner.RunTimed "run.afterRun" "afterRun" cmd

            if not success then
                FsHotWatch.Logging.error
                    "afterRun"
                    "afterRun hook exited non-zero (see the failure above) — the run's exit code is \
                     UNCHANGED; a run-level teardown failure never alters the verdict.")

/// The `beforeRun` preflight both brackets run: timed, its output surfaced on failure
/// like `tests.beforeRun` does, so a refused preflight shows WHY and not merely that it
/// refused. `true` means "proceed".
let internal runBeforeRunHook (config: DaemonConfiguration) (runner: RunHookRunner) : bool =
    match config.BeforeRun with
    | None -> true
    | Some cmd ->
        let (success, output) = runner.RunTimed "run.beforeRun" "beforeRun" cmd

        if not success then
            eprintfn
                "fshw: beforeRun hook failed — aborting the run before any check ran (the daemon was not contacted):"

            eprintfn "%s" output

        success

/// Which verdict verb a run-hook verb brackets.
let private verdictCommandOf (verb: RunHookCommand) : Verdict.Command =
    match verb with
    | RunHookCommand.Check -> Verdict.Check
    | RunHookCommand.Confirm -> Verdict.Confirm

/// The processes a run-level bracket starts in the CLI's own process: its hooks. The
/// daemon reaps what its plugins start; nothing reaps what the CLI starts unless the
/// run does, so a run owns a process scope for its whole length.
[<Sealed>]
type internal RunProcessScope() =
    let processes = ProcessRegistry.Registry()
    let installed = ProcessRegistry.install processes
    let reap = makeRunOnce (fun () -> processes.KillAll())

    /// Kill whatever the run still has running, such as a hook it was interrupted in.
    /// Once; the run starts nothing after it.
    member _.Reap() : unit = reap ()

    /// Run `teardown` (afterRun) in a scope of its own, and reap what it leaves
    /// running. Its own scope, because the run's is shut by then, and because on a
    /// signal this runs on the handler's thread, which the run's scope does not reach.
    member _.Teardown(teardown: unit -> unit) : unit =
        let scope = ProcessRegistry.Registry()

        try
            using (ProcessRegistry.install scope) (fun _ -> teardown ())
        finally
            scope.KillAll()

    interface IDisposable with
        member _.Dispose() =
            reap ()
            installed.Dispose()

/// Bracket a `check`/`confirm` run with the run-level `beforeRun`/`afterRun` hooks.
/// See the section header above for the full contract.
///
/// EVERY run is an invocation now, hooks or no hooks: it gets an id
/// the verdict is stamped with, a clock the observed wall time is read from, and a
/// signal finalizer — because the wrapper is the only party that knows the run was
/// interrupted, and a prior green must not survive a Ctrl-C as the answer. What the
/// hooks add on top is their own timed, named evidence: each `run.beforeRun` /
/// `run.afterRun` step is measured here and attached to the verdict the action
/// published, by invocation id, so an overlapping check cannot receive them.
///
/// Works for BOTH transports because it wraps the ACTION, whatever it is: the
/// daemon path (`queryPluginIn`) and `--run-once` (`RunOnceCheck.runOnceAndVerdict`)
/// are the same `action invocation` from here.
///
/// `installSignals` is INJECTED so a test can drive the signal finalizer without
/// delivering a real OS signal to the test host; production passes
/// `installRunSignalHandlers`.
let internal withRunHooksCommandUsingSignals
    (installSignals: (unit -> unit) -> (int -> unit) -> IDisposable)
    (command: Verdict.Command)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: Verdict.Invocation -> int)
    : int =
    let invocation = Verdict.Invocation.start ()

    // Claim the repo BEFORE anything runs, and hold it until the
    // verdict for this invocation is on disk. This bracket is the right — and the only
    // — place for it: both transports reach it (`queryPluginIn` and
    // `RunOnceCheck.runOnceAndVerdict` are the same `action invocation` from here), and
    // it already owns the one finalizer that every way out passes through, so the claim
    // cannot outlive the run it describes.
    //
    // Best-effort, like publishing the verdict: `acquire` returns `None` when `.fshw/`
    // cannot be written, and the run proceeds. A marker is an additional surface, never
    // a new way to fail.
    let claim = RunClaim.acquire repoRoot (Verdict.Command.token command) invocation.Id

    // Latched, because the ordinary finalizer, a late signal and the scope exit below
    // all reach it and a released claim must not warn about being released twice.
    let releaseClaim = makeRunOnce (fun () -> RunClaim.release repoRoot claim)

    // The backstop for the paths `finalize` does not run through — the beforeRun
    // refusal, and any escape this function grows later. `Environment.Exit` does not
    // unwind, so the signal path releases explicitly inside `finalize` rather than
    // relying on this.
    use _claimHeld =
        { new IDisposable with
            member _.Dispose() = releaseClaim () }

    // The run's own processes: its hooks, which run here in the CLI rather than in the
    // daemon. A run that ends, or is interrupted in one of them, reaps them.
    use processes = new RunProcessScope()

    let runner = makeRunHookRunner repoRoot config invocation
    let evidence = runner.Collected
    let afterRun = makeAfterRunHook config runner


    // Attach the wrapper's evidence — hook steps, their spans, the observed wall time
    // — to the verdict THIS invocation produced. Latched: the ordinary finalizer and a
    // late signal must not append the same hooks twice. Refused (and said so) when a
    // newer invocation already owns the file.
    let attach =
        makeRunOnce (fun () ->
            let hooks, spans = evidence ()

            if
                not (
                    Verdict.tryAugment
                        repoRoot
                        invocation.Id
                        hooks
                        spans
                        []
                        (Some(Verdict.Invocation.elapsedMs invocation))
                )
            then
                FsHotWatch.Logging.warn
                    "hooks"
                    "The run verdict changed before hook timing could be attached; left the newer verdict untouched.")

    // The one finalizer for every way out. `terminal` names how the run ended when it
    // did not end by publishing normally: `downgrade` says whether a verdict this
    // invocation ALREADY published must be downgraded to incomplete (an exception or
    // signal after the publish) or left as it is (the action returned normally and
    // simply never published — the fallback is written only where nothing owns the
    // file). Publishing the terminal record is best-effort like every other verdict
    // write: a `.fshw/` that cannot be written must not turn into a second failure.
    let finalize (reason: string) (downgrade: bool) : unit =
        // Whatever the run still has running goes first: interrupted in a hook, that
        // hook's tree must not outlive the run, and it must not race the teardown.
        processes.Reap()
        processes.Teardown afterRun

        try
            Verdict.tryPublishTerminal repoRoot config.Exclude command invocation reason downgrade
            |> ignore

            attach ()
        with
        | :? IOException as ex ->
            FsHotWatch.Logging.warn "verdict" $"could not publish the terminal verdict: %s{ex.Message}"
        | :? UnauthorizedAccessException as ex ->
            FsHotWatch.Logging.warn "verdict" $"could not publish the terminal verdict: %s{ex.Message}"

        // LAST, and outside the try: the claim is released only once
        // this invocation's verdict is on disk, so there is no instant in which the file
        // is both unclaimed and describing an earlier run. A publish that threw still
        // releases — a run that has ended is not in flight, whatever it managed to
        // write.
        releaseClaim ()

    // Installed for EVERY invocation, not only those with an afterRun: a signalled run
    // has no verdict of its own unless this writes one, and a prior green left on disk
    // would read as the answer.
    use _signals =
        installSignals (fun () -> finalize "the run was signalled before the check could finish" true) exit

    // beforeRun FIRST — before `action`, hence before the daemon is contacted —
    // and FAIL-CLOSED.
    let proceed = runBeforeRunHook config runner

    if not proceed then
        // Fail-closed: exit 2, NOT 1. afterRun does NOT fire here — beforeRun is
        // the acquire, and a failed acquire has nothing for afterRun to release
        // (afterRun brackets the ACTION, which never began). Nothing else will
        // publish, so the refusal — and the timed hook that made it — is the record.
        let hooks, spans = evidence ()

        Verdict.writeHookFailure
            repoRoot
            config.Exclude
            command
            invocation
            hooks
            spans
            "the top-level beforeRun hook failed before the daemon was contacted"

        2
    else
        let mutable captured: Runtime.ExceptionServices.ExceptionDispatchInfo option = None

        let exitCode =
            try
                action invocation
            with ex ->
                captured <- Some(Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex)
                2

        match captured with
        | Some error ->
            finalize
                $"the check terminated with an exception before it could finish: %s{error.SourceException.Message}"
                true

            error.Throw()
            exitCode
        | None ->
            finalize "the check ended without publishing a verdict for this invocation" false
            exitCode

let internal withRunHooksCommand
    (command: Verdict.Command)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: Verdict.Invocation -> int)
    : int =
    withRunHooksCommandUsingSignals installRunSignalHandlers command repoRoot config action

/// The bracket as a `check`, for an action that does not need its invocation.
let withRunHooks (repoRoot: string) (config: DaemonConfiguration) (action: unit -> int) : int =
    withRunHooksCommand Verdict.Check repoRoot config (fun _ -> action ())

/// Does the config select `verb` for run-level hook bracketing?
///
/// Pure, so the verb policy is unit-testable without spawning a shell, touching a
/// daemon, or running a hook. An absent `runHookCommands` resolves to BOTH verbs
/// upstream in `DaemonConfig`, so a `false` here always reflects a deliberate
/// config entry rather than a defaulting accident.
let internal runHooksApplyTo (config: DaemonConfiguration) (verb: RunHookCommand) : bool =
    Set.contains verb config.RunHookCommands

/// Bracket `action` with the run-level hooks IFF `runHookCommands` selects `verb`.
///
/// A verb the config does not select is a straight `action ()` — no latch, no signal
/// handlers, no shell-out — so a consumer gating only `confirm` pays nothing on the
/// `check` inner loop it runs constantly. Sits OUTSIDE `withRunHooks` so the bracket
/// mechanics and the verb policy stay separately testable.
///
/// The rule is purely "which verb was invoked": `--run-once` and the daemon path get
/// the identical decision, and there is no cheapness heuristic through which CI could
/// silently lose the gate.
let internal withRunHooksForInvocation
    (verb: RunHookCommand)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: Verdict.Invocation -> int)
    : int =
    let command = verdictCommandOf verb

    // A verb the config does not select still runs as an invocation
    // (id, clock, signal finalizer) — it just has no hooks to time.
    let selectedConfig =
        if runHooksApplyTo config verb then
            config
        else
            { config with
                BeforeRun = None
                AfterRun = None }

    withRunHooksCommand command repoRoot selectedConfig action

let withRunHooksFor
    (verb: RunHookCommand)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: unit -> int)
    : int =
    withRunHooksForInvocation verb repoRoot config (fun _ -> action ())

/// The run-level hooks WITHOUT the in-flight claim, the terminal verdict, or any
/// ownership of `.fshw/verdict.json`.
///
/// Run-level hooks do TWO separable jobs. They BRACKET heavy work, so a consumer's
/// gate-lock has something to guard and a claim can say a run is in flight. And they
/// CHECK what the verdict's tree hash does NOT cover — an index, a doc set, any file a
/// consumer deliberately excludes from the verdict's inputs so that editing it does not
/// invalidate a green. `confirm`'s fast path needs the second job and must not take the
/// first: it starts no daemon, sets no scope and runs no test, but it DOES certify a
/// tree.
///
/// Fusing the two would be wrong, not merely wasteful. `withRunHooksCommand` CLAIMS the
/// repo before the action: an on-disk assertion that a check is in flight over this tree
/// RIGHT NOW, which `fshw verdict` answers with exit 6 — "the answer is being computed;
/// waiting is what closes this, not another run". A re-check that reads two files and
/// computes nothing must not make that assertion to every concurrent reader. The same
/// bracket would also run its terminal-verdict and hook-attach steps against a verdict
/// owned by an EARLIER invocation, which they can only decline — and say so, on every
/// fast path.
///
/// What is KEPT is the whole of the hook contract: beforeRun fail-closed (exit 2, its
/// refusal published as the invocation's record so the green it declined to certify
/// cannot be read back out of `.fshw/verdict.json` as the answer), afterRun as a
/// `finally`, and the signal finalizer — a Ctrl-C between the two must still release
/// whatever beforeRun acquired.
///
/// `installSignals` is INJECTED for the same reason as in `withRunHooksCommandUsingSignals`.
let internal withRunHooksUnclaimedUsingSignals
    (installSignals: (unit -> unit) -> (int -> unit) -> IDisposable)
    (command: Verdict.Command)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: unit -> int)
    : int =
    // An invocation id and a clock, for the same reason every run has them: the hooks
    // this runs are timed against it, and a refusal has to be attributable. It claims
    // nothing and publishes nothing UNLESS beforeRun refuses, which is the one outcome
    // that must leave a record.
    let invocation = Verdict.Invocation.start ()
    use processes = new RunProcessScope()
    let runner = makeRunHookRunner repoRoot config invocation
    let afterRun = makeAfterRunHook config runner

    let finish () =
        processes.Reap()
        processes.Teardown afterRun

    use _signals = installSignals finish exit

    if not (runBeforeRunHook config runner) then
        // Fail-closed, exit 2 and NOT 1, exactly as the bracketing path fails: afterRun
        // does not fire (beforeRun is the acquire, and a failed acquire has nothing to
        // release), and the refusal REPLACES the verdict it declined to certify. Leaving
        // the green readable would hand the next reader the answer this run refused to
        // give.
        let hooks, spans = runner.Collected()

        Verdict.writeHookFailure
            repoRoot
            config.Exclude
            command
            invocation
            hooks
            spans
            "the top-level beforeRun hook failed before the daemon was contacted"

        2
    else
        try
            action ()
        finally
            finish ()

/// `withRunHooksUnclaimedUsingSignals` under the SAME verb policy the bracketing path
/// obeys: a verb the config does not select is a straight `action ()`.
let internal withRunHooksUnclaimedFor
    (verb: RunHookCommand)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: unit -> int)
    : int =
    if runHooksApplyTo config verb then
        withRunHooksUnclaimedUsingSignals installRunSignalHandlers (verdictCommandOf verb) repoRoot config action
    else
        action ()

/// Execute a parsed command with injectable dependencies.
/// How a command reaches this worktree's daemon when the repository host serves it.
[<NoComparison; NoEquality>]
type HostLink =
    {
        Endpoint: string
        /// The session this worktree is attached as, now.
        Session: unit -> FsHotWatch.RepositoryIdentity.SessionId
        /// Attach again (the session may have been replaced); true when it serves.
        Reattach: unit -> bool
    }

/// The daemon operations, sent to this worktree's session of the repository host. Each
/// RPC carries the session and this CLI's invocation; the pipe name is not used.
let sessionIpcOps (link: HostLink) : IpcOps =
    let invocation = FsHotWatch.RepositoryIpc.InvocationId.mint ()

    let call (methodName: string) (args: obj array) =
        async {
            let preamble = FsHotWatch.RepositoryIpc.Preamble.Session(link.Session(), invocation)

            return! FsHotWatch.RepositoryIpc.invoke link.Endpoint preamble methodName args
        }

    { Shutdown = fun _ -> call "Shutdown" [||]
      Scan = fun _ -> call "Scan" [||]
      ScanStatus = fun _ -> call "ScanStatus" [||]
      GetStatus = fun _ -> call "GetStatus" [||]
      GetPluginStatus = fun _ plugin -> call "GetPluginStatus" [| plugin |]
      RunCommand = fun _ name argsJson -> call "RunCommand" [| name; argsJson |]
      GetDiagnostics = fun _ filter -> call "GetDiagnostics" [| filter |]
      WaitForScan = fun _ after -> call "WaitForScan" [| after |]
      WaitForComplete = fun _ timeoutMs -> call "WaitForComplete" [| timeoutMs |]
      TriggerBuild = fun _ -> call "TriggerBuild" [||]
      FormatAll = fun _ -> call "FormatAll" [||]
      RerunPlugin = fun _ name -> call "RerunPlugin" [| name |]
      Invalidate = fun _ -> call "Invalidate" [||]
      IsRunning = fun _ -> FsHotWatch.RepositoryIpc.isRunning link.Endpoint
      LaunchDaemon = fun _ _ _ -> () }

/// The repository host's control state for the repository `repoRoot` belongs to.
let private repositoryControl (repoRoot: string) =
    FsHotWatch.RepositoryIdentity.resolveWorktree repoRoot
    |> Result.map (fun w -> FsHotWatch.RepositoryIdentity.repositoryControlPaths (FsHwPaths.stateHome ()) w.Repository)

let private repositoryCall (endpoint: string) (methodName: string) =
    FsHotWatch.RepositoryIpc.invoke
        endpoint
        (FsHotWatch.RepositoryIpc.Preamble.Repository(FsHotWatch.RepositoryIpc.InvocationId.mint ()))
        methodName
        [||]
    |> Async.RunSynchronously

/// Render the host's `ListSessions` answer for a human.
let internal renderRepositoryStatus (json: string) : string list =
    let node = System.Text.Json.Nodes.JsonNode.Parse json
    let text (n: System.Text.Json.Nodes.JsonNode) (name: string) = n[name].GetValue<string>()
    let sessions = node["sessions"].AsArray() |> List.ofSeq
    let pid = node["hostPid"].GetValue<int>()
    let endpoint = text node "endpoint"

    [ yield $"Repository host pid %d{pid} on %s{endpoint}: %d{sessions.Length} session(s)"
      for s in sessions do
          let state =
              if s["serving"].GetValue<bool>() then
                  "serving"
              else
                  "starting"

          let generation = s["scanGeneration"].GetValue<int64>()
          let root = text s "root"
          yield $"  %s{root}  %s{state}, scan generation %d{generation}"
      match node["watch"] with
      | null -> ()
      | watch ->
          let rendered = watch.ToJsonString()
          yield $"  watch: %s{rendered}" ]

/// `status --repository`: every session the repository's host serves.
let internal repositoryStatus (agent: bool) (repoRoot: string) : int =
    match repositoryControl repoRoot with
    | Error e ->
        eprintfn $"fshw: %s{FsHotWatch.RepositoryIdentity.IdentityError.describe e}"
        2
    | Ok control when not (FsHotWatch.RepositoryIpc.isRunning control.Endpoint) ->
        eprintfn "No repository host is running for this repository."
        0
    | Ok control ->
        let json = repositoryCall control.Endpoint "ListSessions"

        if agent then
            printfn "%s" json
        else
            for line in renderRepositoryStatus json do
                eprintfn "%s" line

        0

/// `stop --repository`: stop the repository's host, ending every session.
let internal stopRepositoryHost (repoRoot: string) : int =
    match repositoryControl repoRoot with
    | Error e ->
        eprintfn $"fshw: %s{FsHotWatch.RepositoryIdentity.IdentityError.describe e}"
        2
    | Ok control when not (FsHotWatch.RepositoryIpc.isRunning control.Endpoint) ->
        eprintfn "No repository host is running for this repository."
        0
    | Ok control ->
        repositoryCall control.Endpoint "StopHost" |> ignore
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        while FsHotWatch.RepositoryIpc.isRunning control.Endpoint
              && DateTime.UtcNow < deadline do
            Thread.Sleep 100

        if FsHotWatch.RepositoryIpc.isRunning control.Endpoint then
            eprintfn $"The repository host is still serving 60s after it was asked to stop; see %s{control.HostLog}"
            1
        else
            eprintfn "Stopped the repository host."
            0

/// Build the daemon for the worktree at `root` from its configuration: a per-worktree
/// daemon (`Standalone`) or a session of the repository host (`Hosted`).
let internal daemonWith
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (runMode: Daemon.RunMode)
    (hosting: FsHotWatch.DaemonHosting.Hosting)
    (root: string)
    : Daemon =
    let cacheConfig = if opts.NoCache then DaemonConfig.NoCache else config.Cache
    let backend, keyProvider = DaemonConfig.createCacheComponents root cacheConfig

    let fileCommandPatterns =
        config.FileCommands
        |> List.choose (fun fc -> fc.Pattern)
        |> List.map FsHotWatch.Watcher.FilePattern.parse

    // Resolve the idle-exit threshold from the `idleExitMin` config + this daemon's
    // repo path (AUTO-on for `/.workspaces/` checkouts). `None` leaves the timer off. A
    // hosted session that goes idle detaches; the host outlives it.
    let idleExitMin = FsHotWatch.IdleExit.resolveThreshold config.IdleExitMin root

    // Resolve the pressure floor from `pressureIdleFloorMin` (default-on at 2 min).
    // Under memory pressure this shortens an already-eligible idle window to
    // `min(idleExitMin, floor)`; `None` disables pressure-shortening. It never makes a
    // non-eligible daemon (e.g. the default workspace) eligible.
    let pressureIdleFloorMin =
        FsHotWatch.IdleExit.resolvePressureFloor config.PressureIdleFloorMin

    Daemon.create
        root
        { Daemon.DaemonOptions.defaults with
            RunMode = runMode
            CacheBackend = backend
            CacheKeyProvider = keyProvider
            ExcludePatterns = config.Exclude
            ExtraWatchPatterns = fileCommandPatterns
            FsEventsLatencySeconds = float config.FsEventsLatencyMs / 1000.0
            IdleExitMin = idleExitMin
            PressureIdleFloorMin = pressureIdleFloorMin
            CheckerCacheSizeFactor = config.CheckerCacheSizeFactor
            Hosting = hosting }

/// `fshw host <root>`: serve the repository `root` belongs to until stopped or idle.
/// Launched on demand by an opted-in CLI, from the host's control directory, with the
/// launching shell's environment.
let internal runHostVerb (opts: GlobalOptions) (root: string) : int =
    match FsHotWatch.RepositoryIdentity.resolveWorktree root with
    | Error e ->
        eprintfn $"fshw host: %s{FsHotWatch.RepositoryIdentity.IdentityError.describe e}"
        2
    | Ok launchRoot ->
        let pool = FsHotWatch.SharedWatchPool.WatchPool()

        // Sessions of one checker configuration check through one checker.
        let partitions =
            FsHotWatch.CheckerPartitions.Partitions Daemon.createCheckerWithCacheSizes

        let sinkFor (worktree: FsHotWatch.RepositoryIdentity.ResolvedWorktree) =
            let logDir =
                try
                    (DaemonConfig.loadConfig worktree.Root.Value).LogDir
                with ConfigError _ ->
                    DaemonConfig.DefaultLogDir

            let dir =
                if Path.IsPathRooted logDir then
                    logDir
                else
                    Path.Combine(worktree.Root.Value, logDir)

            FsHotWatch.Logging.fileSink (Path.Combine(dir, DaemonConfig.DaemonLog.FileName)) FsHotWatch.Logging.logLevel

        let watchConfig (worktree: FsHotWatch.RepositoryIdentity.ResolvedWorktree) (onChange: unit -> unit) =
            DaemonConfig.watchRepoConfigFile worktree.Root.Value (fun reason ->
                FsHotWatch.Logging.info "config" reason
                onChange ())

        let describe () =
            let stats = pool.Stats
            let watch = System.Text.Json.Nodes.JsonObject()
            watch["nativeStreams"] <- System.Text.Json.Nodes.JsonValue.Create stats.NativeStreams
            watch["subscribers"] <- System.Text.Json.Nodes.JsonValue.Create stats.Subscribers
            watch["eventsReceived"] <- System.Text.Json.Nodes.JsonValue.Create stats.EventsReceived
            watch["eventsDelivered"] <- System.Text.Json.Nodes.JsonValue.Create stats.EventsDelivered
            watch["eventsUnowned"] <- System.Text.Json.Nodes.JsonValue.Create stats.EventsUnowned
            let node = System.Text.Json.Nodes.JsonObject()
            node["watch"] <- watch
            node

        let factory (spec: FsHotWatch.SessionRegistry.SessionSpec) =
            let worktreeRoot = spec.Worktree.Root.Value
            let config = DaemonConfig.loadConfig worktreeRoot

            match RunOnceOutput.failIfNoProjects worktreeRoot config.Exclude with
            | Some _ -> invalidOp $"no F# projects were discovered under %s{worktreeRoot}"
            | None -> ()

            let hosting =
                FsHotWatch.DaemonHosting.hostedBy
                    (pool.WatcherFactoryFor(FsHotWatch.SharedWatchPool.anchorOf spec.Worktree))
                    partitions.For

            let daemon = daemonWith opts config Daemon.RunMode.Watching hosting worktreeRoot

            registerPlugins daemon worktreeRoot config
            daemon

        // A session's TestPrune database connections are pooled per path, and the pool
        // outlives the session. A worktree recreated at the same path would otherwise be
        // handed a connection to its predecessor's file. Only this worktree's pool is
        // cleared: a process-wide clear would land between a sibling's open and its read.
        let sessionResources (worktree: FsHotWatch.RepositoryIdentity.ResolvedWorktree) =
            [ { new IDisposable with
                  member _.Dispose() =
                      FsHotWatch.TestPrune.ImpactDbPool.clear (DaemonConfig.testImpactDbPath worktree.Root.Value) } ]

        let settings =
            RepositoryHostMode.hostSettings launchRoot sinkFor watchConfig sessionResources describe

        // The host's working directory is its own control directory, never a worktree:
        // a hidden dependence on the cwd then fails the same way for every session.
        Directory.CreateDirectory settings.Control.Directory |> ignore
        Directory.SetCurrentDirectory settings.Control.Directory
        use cts = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            cts.Cancel())

        match FsHotWatch.RepositoryHost.run settings factory FsHotWatch.RepositoryHost.DefaultIdleGrace cts with
        | FsHotWatch.RepositoryHost.HostRun.AlreadyRunning pid ->
            let who = pid |> Option.map (sprintf " (pid %d)") |> Option.defaultValue ""

            eprintfn $"repository host already running%s{who}"
            0
        | FsHotWatch.RepositoryHost.HostRun.Stopped -> 0

/// How a command reaches this worktree's daemon: its own per-worktree daemon, or its
/// session of the repository host. Built once per invocation (`ownDaemonLink`,
/// `hostSessionLink`); the command bodies ask it and never ask which it is.
[<NoComparison; NoEquality>]
type DaemonLink =
    {
        /// Bring the daemon up, or attach; true when it serves.
        Ensure: unit -> bool
        /// Replace a daemon whose pipe answers garbage. A host is never restarted from a
        /// CLI, because siblings share it; the session is attached again instead.
        ForceRestart: unit -> bool
        /// Say so when the serving daemon was built from different code. A host
        /// session's binary was already checked by the attach handshake.
        WarnIfStale: unit -> unit
        /// Restart a daemon built from different code before real work runs on it.
        RestartIfStale: unit -> unit
        /// `fshw start`, given how a per-worktree daemon starts in this process.
        Start: (unit -> int) -> int
        /// `fshw stop`, given how a per-worktree daemon is stopped.
        Stop: (unit -> int) -> int
    }

/// The worktree's own per-worktree daemon.
let internal ownDaemonLink
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (startupTimeoutSeconds: float)
    : DaemonLink =
    let ensure () =
        ensureDaemon ipc repoRoot pipeName opts.DaemonExtraArgs config.LogDir startupTimeoutSeconds

    let staleness () =
        if ipc.IsRunning pipeName then
            match DaemonIdentity.verdictFor repoRoot with
            | DaemonIdentity.IdentityVerdict.Stale reason -> Some reason
            | DaemonIdentity.IdentityVerdict.Match -> None
        else
            None

    { Ensure = ensure
      // Stop EVERYTHING answering on the pipe (a corrupted reply usually means two
      // daemons share it, so one Shutdown is not enough), reap the pidfile, start
      // fresh. Unlike `Ensure` this never reuses: the pipe's occupant emits garbage.
      ForceRestart =
        fun () ->
            let sw = System.Diagnostics.Stopwatch.StartNew()

            while ipc.IsRunning pipeName && sw.Elapsed < TimeSpan.FromSeconds(10.0) do
                try
                    ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
                with ex ->
                    FsHotWatch.Logging.debug
                        "cli-heal"
                        $"shutdown attempt failed: %s{ex.GetType().Name}: %s{ex.Message}"

                Thread.Sleep(200)

            killStaleDaemon repoRoot
            startFreshDaemon ipc repoRoot pipeName opts.DaemonExtraArgs config.LogDir startupTimeoutSeconds
      WarnIfStale = fun () -> staleness () |> Option.iter (staleIdentityStatusWarning >> eprintfn "%s")
      RestartIfStale = fun () -> staleness () |> Option.iter (fun _ -> ensure () |> ignore)
      Start = fun startHere -> startHere ()
      Stop = fun stopHere -> stopHere () }

/// The worktree's session of the repository host.
let internal hostSessionLink (host: HostLink) : DaemonLink =
    let ipc = sessionIpcOps host

    { Ensure = host.Reattach
      ForceRestart = host.Reattach
      WarnIfStale = ignore
      RestartIfStale = ignore
      Start =
        fun _ ->
            let session = FsHotWatch.RepositoryIdentity.SessionId.render (host.Session())
            eprintfn $"Attached to the repository host as session %s{session}"
            0
      Stop =
        fun _ ->
            // Detach: this session's Shutdown ends it alone.
            ipc.Shutdown "" |> Async.RunSynchronously |> ignore
            eprintfn "Detached this worktree from the repository host."
            0 }

let executeCommandWith
    (link: DaemonLink)
    (loadedConfigIdentity: string)
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (startupTimeoutSeconds: float)
    : int =
    let mode = pickMode opts.AgentMode opts.CompactMode
    let noWarnFail = opts.NoWarnFail

    // State-dir hygiene BEFORE any daemon decision:
    //   1. A `daemon.pid` whose process is provably dead is deleted NOW — the
    //      leftover of a crash or a kill, cleaned on the next command instead
    //      of accumulating for an external reaper.
    //   2. If the daemon restarted ITSELF over a wedge, say what it did — the
    //      last-wedge breadcrumb is the recovery notice the daemon could not
    //      print to a terminal it never had. Printed once, then consumed.
    cleanStalePidfile repoRoot

    match PluginWedge.consumeBreadcrumb repoRoot with
    | Some message -> eprintfn "⚠ %s" message
    | None -> ()

    // Fail-fast on misconfiguration BEFORE starting (or polling for) a daemon. The
    // daemon's `start` path checks the same thing and exits 2, so without this check
    // the freshly-launched daemon would exit 2, the CLI's `IsRunning` poll would never
    // observe a live daemon, and the user would see "Failed to start daemon" + exit 1
    // instead of the structured "no projects discovered" + exit 2. Status/Stop/Scan/
    // Init/etc. tolerate or don't care about a zero-projects workspace and skip it.
    let needsProjects =
        match command with
        // `--run-once` makes the same check INSIDE the run bracket
        // (`RunOnceCheck`), where the refusal is published as an invocation-owned
        // `incomplete` and the run-level hooks still fire around it; pre-checking here
        // would exit before either could happen.
        | Check flags
        | Confirm flags when isRunOnce flags -> false
        | Start
        | Check _
        | Confirm _
        | TestRerun _
        | Format _
        | Rerun _ -> true
        // `verdict` is a pure read of `.fshw/verdict.json`. It starts nothing and
        // asks the daemon nothing, so a workspace with zero projects is not an
        // error for it — it simply has no verdict yet (exit 5).
        | Verdict
        | Stop _
        | Scan
        | Invalidate
        | Status _
        | Init
        | Config _
        | Coverage _
        | DeadCode _
        | Completions
        | Host _ -> false

    // Only pre-check when we're about to launch (or have launched) a fresh
    // daemon. A reused already-running daemon is already past discovery,
    // and tests/integration paths that bypass real launch (with stub IPCs)
    // don't need the pre-check.
    let zeroProjectsExit =
        if needsProjects && not (ipc.IsRunning pipeName) then
            RunOnceOutput.failIfNoProjects repoRoot config.Exclude
        else
            None

    match zeroProjectsExit with
    | Some exitCode -> exitCode
    | None ->

        let ensureDaemonFn () = link.Ensure()

        // Gate the check on the daemon actually answering RPCs, not just the pipe
        // being listenable. The readiness deadline is at least
        // `DaemonReadinessTimeoutSeconds`, never shorter than the launch timeout.
        let waitReadyFn () =
            waitForDaemonReady ipc repoRoot pipeName (max startupTimeoutSeconds DaemonReadinessTimeoutSeconds)

        // Forced restart for corrupted-pipe self-healing (see `DaemonLink.ForceRestart`).
        let forceRestartDaemon () : bool = link.ForceRestart()

        // Shadow the module-level wrapper with the heal-capable one so every
        // IPC call site in this scope self-heals a corrupted pipe.
        let withIpc = withIpc forceRestartDaemon

        let queryPluginIn
            (invocation: Verdict.Invocation)
            (checkMode: CheckVerdict.CheckMode)
            (mode: ProgressRenderer.RenderMode)
            (filter: string)
            : int =
            ensureAndQueryErrors
                mode
                checkMode
                invocation
                repoRoot
                config.Exclude
                noWarnFail
                ensureDaemonFn
                waitReadyFn
                forceRestartDaemon
                ipc
                pipeName
                filter

        let queryPluginWith (mode: ProgressRenderer.RenderMode) (filter: string) : int =
            queryPluginIn (Verdict.Invocation.start ()) CheckVerdict.InnerLoop mode filter

        /// `check`/`confirm` with NO daemon (`--run-once`). Same verdict, same verdict
        /// file, same exit codes — only the transport differs (`PluginHost.RunCommand`
        /// in-process instead of a socket).
        ///
        /// A misconfiguration surfaced during plugin registration (e.g. the fail-loud
        /// analyzers guard) raises `ConfigError`. Report it cleanly with a RED exit code
        /// — same contract as the config-load handler — rather than crashing with an
        /// unhandled-exception stack trace.
        let runOnceIn (invocation: Verdict.Invocation) (checkMode: CheckVerdict.CheckMode) : int =
            try
                RunOnceCheck.runOnceAndVerdictForInvocation
                    invocation
                    (renderBlock mode (not noWarnFail))
                    checkMode
                    noWarnFail
                    createDaemon
                    repoRoot
                    config
                    None
            with ConfigError msg ->
                eprintfn $"fshw: config error: %s{msg}"
                2

        let queryPlugin filter =
            queryPluginWith ProgressRenderer.Verbose filter

        let withDaemon (action: unit -> int) : int =
            if not (ensureDaemonFn ()) then
                match DaemonStartupFailure.tryRead repoRoot with
                | Some message -> eprintfn $"Failed to start daemon. The daemon refused to start:\n%s{message}"
                | None -> eprintfn "Failed to start daemon"

                1
            else
                action ()

        let withDaemonAndIpc (action: unit -> int) : int = withDaemon (fun () -> withIpc action)

        match command with
        | Start ->
            link.Start(fun () ->
                // Fail-fast on misconfiguration BEFORE acquiring the lockfile, writing the
                // pidfile, or creating the daemon — the same contract as the run-once paths.
                match RunOnceOutput.failIfNoProjects repoRoot config.Exclude with
                | Some exitCode -> exitCode
                | None ->

                    let stateDir = Path.Combine(repoRoot, ".fshw")
                    let pidFile = Path.Combine(stateDir, "daemon.pid")
                    let lockFile = Path.Combine(stateDir, "daemon.lock")
                    Directory.CreateDirectory(stateDir) |> ignore

                    // OS-enforced singleton: hold an exclusive lock on daemon.lock for the
                    // daemon's lifetime. Two concurrent `start` invocations cannot both
                    // acquire it; the second exits cleanly. Not a probe-based guard — that
                    // has a TOCTOU window between the IsRunning check and the pipe claim.
                    let acquired =
                        try
                            Some(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                        with :? IOException ->
                            None

                    // Which process is which, when two exist for one repo root?
                    // `ps` could not answer that after the fact:
                    // `confirm` is the same binary in the same cwd as a daemon and runs
                    // for 20+ minutes, so "two FsHotWatch.Cli processes" may be one
                    // daemon plus a client. argv settles it.
                    //
                    // Not a separate log write: a launched daemon's stderr IS
                    // daemon.log (`LaunchDaemon` runs `nohup … >> logFile 2>&1`), so
                    // both lines below already land there for the nohup-launched
                    // population this is about.
                    let argvLine =
                        let argv = Environment.GetCommandLineArgs() |> String.concat " "
                        $"pid=%d{Environment.ProcessId} argv=%s{argv}"

                    match acquired with
                    | None when
                        (FsHotWatch.RepositoryHost.HostSessionRecord.tryReadLive
                            FsHotWatch.RepositoryHost.processAlive
                            repoRoot)
                            .IsSome
                        ->
                        let record =
                            (FsHotWatch.RepositoryHost.HostSessionRecord.tryReadLive
                                FsHotWatch.RepositoryHost.processAlive
                                repoRoot)
                                .Value

                        eprintfn
                            $"fshw: daemon not started — this worktree is served by the repository host (pid %d{record.HostPid}) as session %s{FsHotWatch.RepositoryIdentity.SessionId.render record.Session}; `fshw stop` detaches it"

                        2
                    | None ->
                        let pidInfo =
                            if File.Exists pidFile then
                                $" (pid %s{File.ReadAllText(pidFile).Trim()})"
                            else
                                ""

                        eprintfn $"Daemon already running at pipe %s{pipeName}%s{pidInfo} — refused %s{argvLine}"
                        0
                    | Some lockStream ->
                        use _lock = lockStream

                        eprintfn
                            $"Starting FsHotWatch daemon for %s{repoRoot} — claimed the singleton lock, %s{argvLine}"

                        eprintfn $"Pipe: %s{pipeName}"

                        // Write our own PID so killStaleDaemon can find the actual daemon process,
                        // not the nohup wrapper that launched us.
                        File.WriteAllText(pidFile, string Environment.ProcessId)

                        // Record THIS binary's identity BEFORE the IPC pipe starts
                        // listening, so a CLI that observes a live pipe always finds the
                        // record. Any daemon that never wrote one reads as `NotRecorded`
                        // — and is restarted.
                        DaemonIdentity.recordCurrent repoRoot

                        // Likewise the identity of the configuration THIS process parsed:
                        // whoever launched it, and whatever the file says by now, a CLI
                        // must compare against what is actually running.
                        File.WriteAllText(Path.Combine(stateDir, "config.hash"), loadedConfigIdentity)

                        // The pidfile is released on EVERY way out of this block — a clean
                        // stop, a refused watcher start, an unexpected exception — and the
                        // singleton lock goes with `_lock` above. The next `fshw start`
                        // therefore never finds a pidfile naming a process that never ran.
                        try
                            try
                                let daemon = createDaemon repoRoot
                                registerPlugins daemon repoRoot config
                                // Registered: any refusal a previous launch recorded no longer
                                // describes this daemon.
                                DaemonStartupFailure.clear repoRoot
                                let cts = new CancellationTokenSource()

                                Console.CancelKeyPress.Add(fun e ->
                                    e.Cancel <- true
                                    cts.Cancel())

                                // Stop the daemon cleanly if `.fshw.json` is edited. The
                                // user then runs the daemon again to pick up the new config (or
                                // sees the error if the edit was invalid). No hot-reload.
                                use _configWatcher =
                                    watchRepoConfigFile repoRoot (fun reason ->
                                        FsHotWatch.Logging.info "config" reason
                                        cts.Cancel())

                                try
                                    Async.RunSynchronously(daemon.RunWithIpc(pipeName, cts))
                                with :? OperationCanceledException ->
                                    ()

                                eprintfn "Daemon stopped."
                                0
                            with
                            | :? FsHotWatch.Watcher.NativeStreamRefusedPastBudgetException as ex ->
                                // The persistent-refusal case: macOS refused the native FSEvents
                                // stream on every attempt of the retry budget. Persistent, so
                                // fail closed — `Daemon.create` already disposed the partial
                                // daemon, no watcher exists, and the finally + `_lock` release
                                // the pidfile and the singleton lock. Exit 2, the fail-closed
                                // code every other startup refusal uses, never 1.
                                eprintfn
                                    $"fshw: daemon not started — %s{ex.Message}. Nothing is left running (no watcher, no pidfile, no lock). Run `fshw start` again once fseventsd is healthy; if it keeps refusing, the repository is on a volume FSEvents cannot watch — move it to a local volume."

                                2
                            | ConfigError message ->
                                // An expected, user-correctable refusal (e.g.
                                // analyzers.paths that have not been built) — never an unhandled
                                // exception with a stack trace. Exit 2, the fail-closed startup
                                // code: nothing ran, so nothing may read as green. Recorded so the
                                // CLI that launched this detached daemon can print the reason
                                // itself instead of pointing at daemon.log.
                                eprintfn $"fshw: daemon not started — config error:\n%s{message}"
                                DaemonStartupFailure.record repoRoot message
                                2
                        finally
                            if File.Exists pidFile then
                                File.Delete pidFile)
        | Host root -> runHostVerb opts root
        | Stop flags when List.contains Repository flags -> stopRepositoryHost repoRoot
        | Stop _ ->
            // No corrupted-pipe self-heal here: restarting a daemon in order
            // to stop it would defeat the command.
            link.Stop(fun () ->
                withIpcNoHeal (fun () ->
                    stopDaemonWith ipc defaultFileOps defaultStopOps repoRoot pipeName
                    |> reportStopOutcome))
        | Scan ->
            // A scan runs REAL WORK on the daemon — never on a stale-binary
            // one (its results would come from the wrong binary). Same decision as
            // ensureDaemon, which also announces why it restarts.
            link.RestartIfStale()

            withIpc (fun () ->
                let result = ipc.Scan pipeName |> Async.RunSynchronously
                UI.success $"Scan: %s{result}"
                0)
        | Invalidate ->
            // Cache invalidation is daemon-owned: clearing it through IPC updates
            // the live cache instance as well as its persisted files, while keeping
            // the expensive compiler service warm for the next check.
            withDaemonAndIpc (fun () ->
                let result = ipc.Invalidate pipeName |> Async.RunSynchronously
                UI.success $"Cache %s{result}; daemon preserved"
                0)
        | Status(_, flags) when List.contains Repository flags ->
            repositoryStatus (mode = ProgressRenderer.Agent) repoRoot
        | Status(pluginName, _) ->
            // Say it in words: a status computed by a daemon built from different code
            // than this CLI is never presented silently as current.
            link.WarnIfStale()

            withIpc (fun () ->
                let filter = pluginName |> Option.defaultValue ""
                let json = ipc.GetDiagnostics pipeName filter |> Async.RunSynchronously
                let resp = IpcParsing.parseDiagnosticsResponse json

                // GetDiagnostics filters files by plugin but returns all plugin statuses.
                // Narrow Statuses client-side when a specific plugin was requested.
                let scoped =
                    match pluginName with
                    | None -> resp
                    | Some name ->
                        { resp with
                            Statuses = resp.Statuses |> Map.filter (fun k _ -> k = name) }

                match pluginName with
                | Some name when Map.isEmpty scoped.Statuses ->
                    eprintfn "not found: %s" name
                    1
                | _ ->
                    let output =
                        IpcOutput.formatDiagnosticsResponse mode (renderLines mode (not noWarnFail)) scoped

                    eprintfn "%s" output

                    // Same nudge as `check`/`confirm`: when a machine is reading, name the
                    // files rather than leaving it to scrape a display built for a human.
                    if not UI.isInteractive then
                        eprintfn ""

                        for line in ProgressRenderer.AgentHints.forStatus repoRoot do
                            eprintfn "%s" line

                    IpcOutput.exitCodeFromResponse noWarnFail scoped)
        | TestRerun flags ->
            // Filter knobs live here, not on `fshw test`, so the
            // forward-progress contract (everything downstream runs) stays intact.
            // `waitSec` (the slot-wait budget) always travels so a long
            // `beforeRun` chain can't defeat an explicit rerun.
            let waitSec = RerunFilter.waitSec flags

            // `projects` selects WHICH test projects the daemon invokes. Without it a
            // `--filter-class` is fanned out across every configured project, so a filter
            // naming a real class can run only the wrong project and report no match.
            let projects = RerunFilter.projects flags

            let runnable =
                config.Tests
                |> Option.map (fun t -> t.Projects |> List.map (fun p -> p.Project) |> Set.ofList)
                |> Option.defaultValue Set.empty

            let baselineDecision =
                RerunBaseline.decide flags (ipc.IsRunning pipeName) (RerunBaseline.invalidReason repoRoot runnable)

            let runArgsJson =
                match RerunFilter.render flags, projects with
                | "", [] -> JsonSerializer.Serialize {| waitSec = waitSec |}
                | "", ps -> JsonSerializer.Serialize {| waitSec = waitSec; projects = ps |}
                | filter, [] -> JsonSerializer.Serialize {| filter = filter; waitSec = waitSec |}
                | filter, ps ->
                    JsonSerializer.Serialize
                        {| filter = filter
                           waitSec = waitSec
                           projects = ps |}

            let rerun () =
                withDaemon (fun () ->
                    let result =
                        if UI.isInteractive then
                            UI.withSpinner "Rerunning tests" (fun () ->
                                ipc.RunCommand pipeName "run-tests" runArgsJson |> Async.RunSynchronously)
                        else
                            eprintfn "  Rerunning tests..."
                            ipc.RunCommand pipeName "run-tests" runArgsJson |> Async.RunSynchronously

                    IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)

            match baselineDecision with
            | RerunBaseline.Decision.Proceed -> rerun ()
            | RerunBaseline.Decision.ProceedWithNotice lines ->
                for line in lines do
                    eprintfn "%s" line

                rerun ()
            | RerunBaseline.Decision.Refuse lines ->
                for line in lines do
                    eprintfn "%s" line

                2
        | Format flags when isRunOnce flags ->
            let formatConfig =
                { stripConfig config with
                    Format = FormatMode.Auto }



            RunOnceOutput.runOnceAndReport
                (renderBlock mode (not noWarnFail))
                noWarnFail
                createDaemon
                repoRoot
                formatConfig
                (Some "format")
        | Format flags ->


            withDaemon (fun () ->
                let result =
                    if UI.isInteractive then
                        UI.withSpinner "Formatting" (fun () -> ipc.FormatAll pipeName |> Async.RunSynchronously)
                    else
                        eprintfn "  Formatting..."
                        ipc.FormatAll pipeName |> Async.RunSynchronously

                IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
        | Rerun pluginName ->
            withDaemonAndIpc (fun () ->
                let result =
                    if UI.isInteractive then
                        UI.withSpinner $"Running %s{pluginName}" (fun () ->
                            ipc.RerunPlugin pipeName pluginName |> Async.RunSynchronously)
                    else
                        eprintfn "  Running %s..." pluginName
                        ipc.RerunPlugin pipeName pluginName |> Async.RunSynchronously

                IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
        | Init ->
            let configPath = Path.Combine(repoRoot, ".fshw.json")
            let projects = InitConfig.discoverProjects repoRoot None
            let config = InitConfig.generateConfig projects
            let json = InitConfig.serializeConfig config

            try
                use fs = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write)
                use sw = new StreamWriter(fs)
                sw.Write(json + "\n")
                printfn "%s" json
                eprintfn "Wrote %s" configPath
                0
            with :? IOException ->
                eprintfn "%s already exists" configPath
                1
        // Both `check` arms bracket the run with the run-level hooks, but only if
        // `runHookCommands` selects `check` — a consumer gating only the merge verdict
        // leaves the inner loop completely unwrapped.
        | Check flags when isRunOnce flags ->
            withRunHooksForInvocation RunHookCommand.Check repoRoot config (fun invocation ->
                runOnceIn invocation CheckVerdict.InnerLoop)
        | Check flags ->
            withRunHooksForInvocation RunHookCommand.Check repoRoot config (fun invocation ->
                queryPluginIn invocation CheckVerdict.InnerLoop mode "")
        | Confirm flags ->
            // The evidence may ALREADY have been earned. `confirm` is run repeatedly
            // before a merge, and on a tree that has not moved, asking again is the SAME
            // question about the SAME bytes. So look at `.fshw/verdict.json` first: both
            // the tree hash and the producing binary must match byte for byte, and the
            // recorded verdict must be a full-suite green. Anything else — a moved tree, a
            // different fshw, a filtered green, a red — is not an answer, and `confirm`
            // goes and earns one. See `Verdict.priorConfirmation` for why this is the only
            // green in fshw allowed to cross a process boundary.
            match Verdict.priorConfirmation repoRoot config.Exclude with
            | Verdict.PriorConfirmation.StillApplies _ ->
                // HOOKS YES, CLAIM NO — the two halves of the run-level bracket are
                // separated here, because this fast path needs exactly one of them.
                //
                // NOT the bracketing half: it starts no daemon, sets no scope and runs no
                // test, so it takes no in-flight claim, publishes no verdict of its own
                // and leaves `.fshw/verdict.json` byte-identical. A claim held around a
                // read-only re-check would tell every concurrent reader — `fshw verdict`
                // answers exit 6 on one — that a run is verifying this tree right now,
                // when nothing is.
                //
                // YES the checking half: `beforeRun` is where a consumer checks what the
                // tree hash deliberately does NOT cover — an index, a doc set, any file
                // excluded from the verdict's inputs so that editing it does not
                // invalidate a green. Certifying a stored verdict without running it would
                // certify a claim nobody checked, and precisely for the files that were
                // excluded from the hash BECAUSE a hook covers them. Hence
                // `withRunHooksUnclaimedFor` and not `withRunHooksForInvocation`; only the
                // `MustEarn` arm below, which actually runs the suite, takes the full
                // bracket.
                withRunHooksUnclaimedFor RunHookCommand.Confirm repoRoot config (fun () ->
                    // ASK AGAIN, now that the hooks have run. The answer above describes the
                    // tree as it was hashed BEFORE `beforeRun`, and a preflight worth running
                    // takes seconds — a write landing inside that window would otherwise be
                    // certified by a hash taken before it. The slow path closes a window of
                    // exactly this shape by hashing twice around the work it did
                    // (`IpcOutput.SettledTree` and `atWrite`), and refusing the pair when
                    // they differ; this is the same rule at the fast path's scale.
                    //
                    // The second hash is what makes this affordable: it re-reads files the
                    // first hash read seconds ago, so it is always the warm case. Measured
                    // 17ms over this repo (246 files) and 156ms over 1,884 files / 11MB —
                    // low single-digit percent of any hook worth running.
                    match Verdict.priorConfirmation repoRoot config.Exclude with
                    | Verdict.PriorConfirmation.StillApplies confirmed ->
                        UI.success $"confirm — %s{Verdict.describeStillApplies confirmed}"
                        eprintfn ""

                        eprintfn
                            "  AGENTS: READ the above — just don't SCREEN-SCRAPE it. The same facts, machine-readable:"

                        eprintfn
                            $"    verdict  %s{Verdict.RelativePath}   (this verdict, re-checked against the tree on disk)"

                        0
                    | Verdict.PriorConfirmation.MustEarn ->
                        // Exit 2 — "could not complete, retry" — and NOT a fall-through to the
                        // suite: the heavy path is wrapped in its own hook bracket, so falling
                        // through would run `beforeRun` a second time with no `afterRun`
                        // between, which for the gate-lock this feature exists to serve is a
                        // deadlock against itself.
                        //
                        // Nothing is written. The stored verdict now addresses a tree that is
                        // no longer on disk, so `fshw verdict` already reports it stale (exit
                        // 4) and it cannot be read as this tree's answer; overwriting it would
                        // destroy evidence that is still valid for the tree that earned it.
                        UI.fail "the working tree changed while the run-level hooks ran — nothing is claimed about it"

                        eprintfn
                            "  The verdict on disk was earned over the PREVIOUS tree. Re-run `fshw confirm` for this one."

                        2)
            | Verdict.PriorConfirmation.MustEarn ->
                // Same pipeline as `check`, but full-suite scope is set FIRST (so the test
                // run the scan provokes is unfiltered), the suite is FORCED if the scan
                // did not produce one, and the verdict is computed in `Confirmation` mode,
                // which has no path to a green that skips a full-suite run.
                //
                // `--run-once` needs no daemon, which is the only reason CI can invoke
                // `confirm` at all. This arm does the heavy work, so it is the one the
                // gate-lock must guard.
                withRunHooksForInvocation RunHookCommand.Confirm repoRoot config (fun invocation ->
                    if isRunOnce flags then
                        runOnceIn invocation CheckVerdict.Confirmation
                    else
                        queryPluginIn invocation CheckVerdict.Confirmation mode "")
        | Verdict ->
            // Pure read: no daemon, no IPC, no run, so it costs nothing to call in a loop.
            let report = Verdict.report repoRoot config.Exclude

            // STDOUT IS THE MACHINE SURFACE, and nothing else may touch it: an agent
            // piping this must get an envelope that parses, every time. (`UI.success`
            // prints to stdout — using it here would have wedged a trailing prose line,
            // ANSI and all, into the JSON. Exactly the class of thing that makes an
            // agent give up and go back to grepping.) Every human line below goes to
            // stderr, where the rest of the CLI's prose already lives.
            printfn "%s" (Verdict.serializeReport report)

            let say (line: string) = eprintfn "%s" line

            match report with
            | Verdict.Report.Applies v ->
                let verb = Verdict.Command.token v.Command

                match v.Outcome with
                | Verdict.Green baseline ->
                    say $"%s{Color.green}✓%s{Color.reset} %s{verb}: green — for THIS tree (%s{v.TreeHash})"
                    // Say what the green is relative to, so "green" is
                    // read as a claim about the whole suite and can be audited as one.
                    say $"  %s{CheckVerdict.Baseline.describe baseline}"
                | Verdict.Red -> say $"%s{Color.red}✗%s{Color.reset} %s{verb}: RED — for this tree"
                | Verdict.Incomplete reason -> say $"%s{Color.red}✗%s{Color.reset} %s{verb}: NO VERDICT — %s{reason}"
                // The reason already opens with "NO VERDICT — PROJECT MODEL …".
                | Verdict.ModelUnavailable reason -> say $"%s{Color.red}✗%s{Color.reset} %s{verb}: %s{reason}"
            | Verdict.Report.Stale(v, reason) ->
                // A green from a different tree — or a different BINARY — is still a green,
                // which is exactly why it may never be REPORTED as one.
                //
                // `Report.Stale`'s second field already names the fault precisely (which
                // provenance link broke) and begins "stale: ...", so it is printed as-is,
                // like the `NoVerdict` arm below.
                say $"%s{Color.red}✗%s{Color.reset} %s{reason}"
                say $"    verdict tree  %s{v.TreeHash}"
                say "  Re-run `fshw check` (or `fshw confirm` for unfiltered full-suite evidence). Never reuse it."
            | Verdict.Report.InFlight(v, reason) ->
                // Deliberately NOT the stale wording: the fix here is to
                // WAIT, not to run again. Telling a reader to re-run a tree that is
                // already being verified is how a box ends up with two checks racing for
                // the same answer.
                say $"%s{Color.yellow}…%s{Color.reset} %s{reason}"
                say $"    verdict tree  %s{v.TreeHash}   (the tree on disk — the SUBJECT matches; the RUN does not)"
                say $"    claims        %s{RunClaim.RelativeDir}"
                say "  Wait for the run to publish, then read again. Do NOT start a second check."
            | Verdict.Report.NoVerdict reason -> say $"%s{Color.red}✗%s{Color.reset} %s{reason}"

            Verdict.reportExitCode report
        | Config ConfigCommand.Check ->
            // Config has already been parsed by main; reaching here means it's valid.
            printfn "config: OK (%d plugins configured)" (countPlugins config)
            0
        | Coverage CoverageCommand.RefreshBaseline ->
            let deleted = refreshCoverageBaseline repoRoot config

            if deleted.IsEmpty then
                printfn "No coverage baseline/partial JSON files found to remove."
            else
                printfn "Removed:"

                for p in deleted do
                    printfn "  %s" p

            0
        | DeadCode flags ->
            // Reads the daemon's symbol DB directly (no IPC, no running daemon).
            // `--entry` repeats and REPLACES the defaults; `--include-tests`
            // widens the report; the global `-v/--verbose` adds unreachability
            // reasons (matching the standalone test-prune CLI's `--verbose`).
            let entryPatterns =
                flags
                |> List.choose (function
                    | Entry p -> Some p
                    | _ -> None)

            let includeTests = flags |> List.contains IncludeTests

            let opts: FsHotWatch.Cli.DeadCode.DeadCodeOptions =
                { EntryPatterns = FsHotWatch.Cli.DeadCode.resolveEntryPatterns entryPatterns
                  IncludeTests = includeTests
                  Verbose = opts.Verbose }

            FsHotWatch.Cli.DeadCode.runDefault repoRoot opts
        | Completions ->
            match writeFishCompletions commandTree cliName with
            | Ok path ->
                eprintfn "%s" $"%s{Color.green}✓%s{Color.reset} Fish completions installed"
                // The resolved path, not the assumed one: the two differ whenever
                // XDG_CONFIG_HOME is set, and printing the assumption is how a write
                // that went somewhere fish never reads still looked like success.
                eprintfn "  Wrote %s" path
                0
            | Error reason ->
                eprintfn "Could not install fish completions: %s" reason
                1

/// `executeCommandWith` for a worktree served by its own daemon.
let executeCommand
    (loadedConfigIdentity: string)
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (startupTimeoutSeconds: float)
    : int =
    executeCommandWith
        (ownDaemonLink ipc repoRoot pipeName opts config startupTimeoutSeconds)
        loadedConfigIdentity
        createDaemon
        ipc
        repoRoot
        pipeName
        command
        opts
        config
        startupTimeoutSeconds

/// Outcome of forwarding a root-level unknown command to the daemon.
///   `Handled exitCode` — the daemon recognized and ran the command (a real plugin
///     command); `exitCode` is its rendered result.
///   `NotRecognized` — the daemon replied with the unknown-command sentinel, so the
///     CLI must fail hard with the canonical parse error + nearest help.
///   `DaemonUnavailable` — the IPC call itself failed (already reported to stderr).
type PluginCommandOutcome =
    | Handled of int
    | NotRecognized
    | DaemonUnavailable

/// Forward an unknown root-level command to the daemon as a dynamic plugin command.
/// Distinguishes a genuinely-unknown command (daemon returned the unknown-command
/// sentinel) from a real plugin result so the caller can fail hard on the former.
let executePluginCommand
    (ipc: IpcOps)
    (pipeName: string)
    (opts: GlobalOptions)
    (cmd: string)
    (argsStr: string)
    : PluginCommandOutcome =
    let mode = pickMode opts.AgentMode opts.CompactMode

    try
        let result = ipc.RunCommand pipeName cmd argsStr |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply result then
            NotRecognized
        else
            Handled(IpcOutput.renderIpcResult mode (renderLines mode true) false result)
    with ex ->
        reportDaemonError ex
        DaemonUnavailable

/// Resolve a ROOT-level unknown command to an exit code: forward it to the daemon as a
/// dynamic plugin command, and FAIL HARD with the canonical parse error + help if the
/// daemon doesn't recognize it — the strict-CLI contract, where garbage input never
/// silently succeeds. Pure wrt. its injected `ipc`, so it's unit-testable without the
/// `[<EntryPoint>]` and repo-root plumbing.
let forwardRootUnknownCommand
    (ipc: IpcOps)
    (pipeName: string)
    (opts: GlobalOptions)
    (cmd: string)
    (argsStr: string)
    (renderErr: unit -> int)
    : int =
    match executePluginCommand ipc pipeName opts cmd argsStr with
    | Handled exitCode -> exitCode
    | NotRecognized -> renderErr ()
    | DaemonUnavailable -> 1

/// Apply parsed global flags: configure logging and return the resolved options.
let applyGlobalFlags (globals: GlobalFlag list) : GlobalOptions =
    let folder (opts: GlobalOptions, parts) flag =
        match flag with
        | Verbose ->
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
            { opts with Verbose = true }, "--verbose" :: parts
        | LogLevel level ->
            match level with
            | "error" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
            | "warning" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Warning
            | "info" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info
            | "debug" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
            | other ->
                eprintfn "Unknown log level: %s (using info)" other
                FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info

            opts, $"--log-level %s{level}" :: parts
        | NoCache -> { opts with NoCache = true }, "--no-cache" :: parts
        | NoWarnFail -> { opts with NoWarnFail = true }, parts
        // --agent and --compact are client-side render selectors; don't forward to the daemon.
        | Agent -> { opts with AgentMode = true }, parts
        | Compact -> { opts with CompactMode = true }, parts

    let opts, parts = globals |> List.fold folder (defaultGlobalOptions, [])

    let extraArgs =
        match parts with
        | [] -> ""
        | _ -> (parts |> List.rev |> String.concat " ") + " "

    { opts with
        DaemonExtraArgs = extraArgs }

/// Render a genuine (non-Help/Version) parse error to stderr using CommandTree's
/// uniform renderer — a one-line message plus the nearest subcommand/group help — and
/// return the exit code. `CommandTree.isError` is the source of truth for the code.
/// Help/Version are handled by the caller and never reach here.
let reportParseError (err: ParseError) : int =
    eprintfn "%s" (CommandTree.renderParseError commandTree err cliName)
    if CommandTree.isError err then 1 else 0

/// Classification of a parse result with respect to what `main` must do next.
/// Separates the repo-INDEPENDENT decisions (help, version, and every genuine flag/arg
/// error — plus a NESTED unknown command, which is a typo against a known group with no
/// daemon passthrough) from the two paths that need the repo root. Pure and total, so
/// the strict-CLI ordering is unit-testable without the `[<EntryPoint>]` plumbing.
type ParseDispatch =
    /// Repo-independent: print the canonical help/error to the right stream and exit
    /// with this code. Covers Help (0), Version (0), and all genuine input errors
    /// except a root-level unknown command.
    | RepoIndependent of int
    /// A successfully-parsed command — needs the repo root + daemon to execute.
    | RunCommand of globals: GlobalFlag list * command: Command
    /// A ROOT-level unknown command (empty groupPath): the dynamic plugin-passthrough.
    /// Carries the token, the raw remaining argv to forward verbatim, and the error to
    /// re-render if the daemon doesn't recognize it.
    | RootUnknownCommand of input: string * rest: string array * err: ParseError

/// Classify a parse result. Help/Version print to stdout (exit 0); genuine
/// repo-independent errors render via `reportParseError` (non-zero); Ok and a
/// root-level unknown command defer to the repo-root branch in `main`.
let classifyParse (parsed: Result<GlobalFlag list * Command, ParseError>) : ParseDispatch =
    match parsed with
    | Ok(globals, command) -> RunCommand(globals, command)
    | Error(HelpRequested path) ->
        printfn "%s" (CommandTree.helpForPath commandTree path cliName)
        RepoIndependent 0
    | Error VersionRequested ->
        printfn "%s" (CommandTree.renderVersion cliName)
        // Name the source ref this binary was built from — the human-readable complement
        // to the binary-identity handshake. See `SourceRef`.
        printfn "%s" (SourceRef.line (CommandTree.entryAssemblyVersion ()))
        RepoIndependent 0
    // ROOT-level unknown command (empty groupPath) is the only error that defers to the
    // daemon; everything else fails hard here, BEFORE any repo-root lookup, so running
    // outside a jj/git checkout does not mask flag/arg errors.
    | Error(UnknownCommand(input, rest, []) as err) -> RootUnknownCommand(input, rest, err)
    | Error err -> RepoIndependent(reportParseError err)

/// Why a root-level plugin command must not run here, or `None` when it may.
///
/// Plugin commands do not reach a repository-host session yet. In a worktree that uses
/// the host (it opts in, or the host already serves it), sending one to the
/// per-worktree pipe would talk to a daemon that is not the one serving the worktree,
/// so the command is refused and says so.
let internal passthroughRefusal
    (configText: string)
    (getEnv: string -> string)
    (servedByHost: bool)
    (command: string)
    : string option =
    if servedByHost || RepositoryHostMode.enabled configText getEnv then
        Some
            $"fshw: `%s{command}` is a plugin command, and plugin commands do not reach a repository host \
              session yet. Run it in a shell with %s{RepositoryHostMode.EnvVar}=0 against this worktree's own \
              daemon (`fshw stop` first if the repository host serves it)."
    else
        None

/// Launch the repository host detached, from its control directory.
let private launchHost (repoRoot: string) (control: FsHotWatch.RepositoryIdentity.RepositoryControlPaths) =
    let entryDll =
        System.Reflection.Assembly.GetEntryAssembly()
        |> Option.ofObj
        |> Option.map (fun a -> a.Location)

    let exe, toolPrefix = computeLaunchCommand Environment.ProcessPath entryDll
    Directory.CreateDirectory control.Directory |> ignore
    eprintfn $"Starting the repository host... (log: %s{control.HostLog})"

    DetachedLaunch.launch
        control.Directory
        (RepositoryHostMode.hostShellCommand exe toolPrefix repoRoot control.HostLog)

/// Whether a command brings this worktree's daemon up (in host mode: attaches a
/// session). `status` and `stop` only ever observe the one that is already there.
let internal needsDaemon (command: Command) : bool =
    match command with
    | Check flags
    | Confirm flags
    | Format flags when isRunOnce flags -> false
    | Status _
    | Stop _
    | Verdict
    | Init
    | Config _
    | Coverage _
    | DeadCode _
    | Completions
    | Host _ -> false
    | _ -> true

/// How this invocation reaches its daemon: `None` for the worktree's own daemon, a
/// `HostLink` for a repository-host session, or an exit code when it must stop here.
/// How this invocation reaches its daemon, and the IPC it sends through: the
/// worktree's own daemon, or its repository-host session. This is where the CLI decides
/// between the two; everything after asks the `DaemonLink`. An `Error` is the exit code
/// when the invocation must stop here.
let private chooseLink
    (repoRoot: string)
    (pipeName: string)
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (configText: string)
    (command: Command)
    : Result<DaemonLink * IpcOps, int> =
    let ownDaemon =
        Ok(ownDaemonLink defaultIpcOps repoRoot pipeName opts config 30.0, defaultIpcOps)

    let live =
        FsHotWatch.RepositoryHost.HostSessionRecord.tryReadLive FsHotWatch.RepositoryHost.processAlive repoRoot

    let linkTo endpoint session =
        let current = ref session

        let host =
            { Endpoint = endpoint
              Session = fun () -> current.Value
              Reattach =
                fun () ->
                    match
                        RepositoryHostMode.attach
                            (FsHwPaths.stateHome ())
                            (launchHost repoRoot)
                            (TimeSpan.FromSeconds 30.0)
                            repoRoot
                            configText
                    with
                    | RepositoryHostMode.Attach.Serving(_, id) ->
                        current.Value <- id
                        true
                    | RepositoryHostMode.Attach.OwnDaemon reason
                    | RepositoryHostMode.Attach.Refused reason ->
                        eprintfn $"fshw: %s{reason}"
                        false }

        Ok(hostSessionLink host, sessionIpcOps host)

    let enabled =
        RepositoryHostMode.enabled configText Environment.GetEnvironmentVariable

    match command, live with
    // Observing and detaching need no attach: the worktree's record names its session.
    | Status(_, flags), Some record
    | Stop flags, Some record when not (List.contains Repository flags) -> linkTo record.Endpoint record.Session
    | _ when not (needsDaemon command) -> ownDaemon
    | _ when enabled ->
        match
            RepositoryHostMode.attach
                (FsHwPaths.stateHome ())
                (launchHost repoRoot)
                (TimeSpan.FromSeconds 30.0)
                repoRoot
                configText
        with
        | RepositoryHostMode.Attach.Serving(endpoint, session) -> linkTo endpoint session
        | RepositoryHostMode.Attach.OwnDaemon reason ->
            eprintfn $"fshw: using this worktree's own daemon: %s{reason}"
            ownDaemon
        | RepositoryHostMode.Attach.Refused reason ->
            eprintfn $"fshw: %s{reason}"
            Error 2
    | _, Some record ->
        eprintfn
            $"fshw: this worktree is served by the repository host (pid %d{record.HostPid}), but this shell has not \
              opted in; set %s{RepositoryHostMode.EnvVar}=1 (or \"repositoryHost\": true in .fshw.json) to use it, \
              or run `fshw stop` to detach it"

        Error 2
    | _ -> ownDaemon

let private runCli (args: string array) : int =
    let argList = args |> Array.toList

    // Bare `--help` / `-h` / `help` (no subcommand) prints global help with global flags.
    // Subcommand help (e.g. `errors --help`) is handled by Parse via HelpRequested below
    // so per-command flags like --wait and --timeout actually appear in the output.
    let isHelpToken (a: string) = a = "--help" || a = "-h" || a = "help"

    let onlyHelpRequested =
        match argList with
        | [] -> true
        | args when args |> List.forall isHelpToken -> true
        | _ -> false

    if onlyHelpRequested then
        printfn "%s" (CommandTree.helpWithGlobals commandTree globalSpec.GlobalFlags cliName)
        0
    else

        // Parse before locating the repo root so `<cmd> --help`, `--version`, and —
        // critically — flag/arg errors all work (and are NOT masked) outside a
        // jj/git checkout. `classifyParse` does the repo-independent dispatch.
        let parsed = globalSpec.Parse args

        match classifyParse parsed with
        | RepoIndependent exitCode -> exitCode
        // The host is launched from its own control directory, outside any checkout:
        // it is told its repository, never finds it from the cwd.
        | RunCommand(globals, Host root) -> runHostVerb (applyGlobalFlags globals) root
        | dispatch ->
            // Both remaining cases need the repo root. A root-level unknown command
            // can't reach a daemon outside a repo, so fail hard with the canonical
            // error+help rather than the misleading "not in a repository" message.
            let repoRoot =
                match findRepoRoot (Directory.GetCurrentDirectory()) with
                | Some root -> root
                | None ->
                    match dispatch with
                    | RootUnknownCommand(_, _, err) ->
                        exit (reportParseError err)
                        ""
                    | _ ->
                        eprintfn "Error: not in a jj or git repository"
                        exit 1
                        ""

            let pipeName = computePipeName repoRoot

            match dispatch with
            | RunCommand(globals, command) ->
                let opts = applyGlobalFlags globals

                let config, configSource =
                    try
                        loadConfigWithSource repoRoot
                    with ConfigError msg ->
                        eprintfn $"fshw: config error: %s{msg}"
                        exit 2

                let createDaemon (root: string) =
                    daemonWith opts config (runModeFor command) (FsHotWatch.DaemonHosting.standalone ()) root

                let loadedIdentity = configContentHash configSource

                match chooseLink repoRoot pipeName opts config configSource command with
                | Error exitCode -> exitCode
                | Ok(link, ipc) ->
                    executeCommandWith link loadedIdentity createDaemon ipc repoRoot pipeName command opts config 30.0
            // ROOT-level unknown command: the dynamic plugin-passthrough. Forward `rest`
            // verbatim; if the daemon doesn't recognize it, fail hard with the canonical
            // error + help, so garbage CLI input fails uniformly.
            | RootUnknownCommand(input, rest, err) ->
                let argsStr = rest |> String.concat " "

                let opts =
                    { defaultGlobalOptions with
                        AgentMode = argList |> List.exists (fun a -> a = "--agent" || a = "-a")
                        CompactMode = argList |> List.exists (fun a -> a = "--compact" || a = "-q") }

                let configText =
                    let path = Path.Combine(repoRoot, ".fshw.json")
                    if File.Exists path then File.ReadAllText path else ""

                let servedByHost =
                    (FsHotWatch.RepositoryHost.HostSessionRecord.tryReadLive
                        FsHotWatch.RepositoryHost.processAlive
                        repoRoot)
                        .IsSome

                match passthroughRefusal configText Environment.GetEnvironmentVariable servedByHost input with
                | Some refusal ->
                    eprintfn "%s" refusal
                    2
                | None ->
                    forwardRootUnknownCommand defaultIpcOps pipeName opts input argsStr (fun () -> reportParseError err)
            // RepoIndependent is fully handled above before the repo-root lookup.
            | RepoIndependent exitCode -> exitCode

[<EntryPoint>]
let main args =
    // The detached-launch helper is a copy of this CLI; it must never reach parsing.
    match DetachedLaunch.tryRun args with
    | Some exitCode -> exitCode
    | None -> runCli args
