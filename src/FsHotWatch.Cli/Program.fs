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

/// Default slot-wait budget (seconds) sent to the daemon's `run-tests` command
/// when `--wait-sec` is not given. Generous so a long `tests.beforeRun` chain
/// (90 s+) can't make an explicit `test-rerun` give up before the prior in-flight
/// run releases the slot.
[<Literal>]
let DefaultTestRerunWaitSec = 600

/// Render `RerunFlag list` to the raw arg string the xUnit v3 standalone
/// runner expects, quoting values that contain whitespace / shell metachars.
/// Empty flag list renders to "".
module RerunFilter =
    let private needsQuoting (s: string) =
        s
        |> Seq.exists (fun c -> Char.IsWhiteSpace(c) || c = '"' || c = '\'' || c = '\\')

    let private quoteIfNeeded (s: string) =
        if needsQuoting s then
            let escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            $"\"%s{escaped}\""
        else
            s

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
            | WaitSec _ -> None)
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

type Command =
    | [<CmdExample("", "--no-cache"); Cmd("Start the daemon")>] Start
    | [<Cmd("Stop the daemon")>] Stop
    | [<CmdExample("", "--run-once");
        Cmd("Run all checks. The fast inner loop — the tests are IMPACT-FILTERED, which is a latency optimization and not the basis for a merge decision (use `confirm` for that)")>] Check of
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
    | [<CmdArg("plugin name (optional)"); CmdExample("", "build", "test-prune"); Cmd("Show current status")>] Status of
        plugin: string option
    | [<Cmd("Scan for file changes")>] Scan
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

/// Walk up from startDir looking for .jj or .git directory.
let findRepoRoot (startDir: string) =
    let rec walk (dir: string) =
        if
            Directory.Exists(Path.Combine(dir, ".jj"))
            || Directory.Exists(Path.Combine(dir, ".git"))
        then
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
      IsRunning = IpcClient.isRunning
      LaunchDaemon =
        fun repoRoot extraArgs logFile ->
            let entryDll =
                System.Reflection.Assembly.GetEntryAssembly()
                |> Option.ofObj
                |> Option.map (fun a -> a.Location)

            let (exe, toolPrefix) = computeLaunchCommand Environment.ProcessPath entryDll

            let psi =
                System.Diagnostics.ProcessStartInfo(
                    "/bin/sh",
                    $"-c \"nohup '%s{exe}' %s{toolPrefix}%s{extraArgs}start >> '%s{logFile}' 2>&1 &\""
                )

            psi.WorkingDirectory <- repoRoot
            psi.UseShellExecute <- false
            let proc = System.Diagnostics.Process.Start(psi)
            proc.WaitForExit() }

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
    | TimedOut of TimeoutException
    | Other of exn

/// StreamJsonRpc's header-delimited reader is the evidence that distinguishes an
/// absurd Content-Length allocation from an unrelated allocation failure in this
/// process. Kept as a pure seam because Exception.StackTrace cannot be constructed
/// portably in a unit test.
let internal classifyIpcFaultAt (stackTrace: string option) (inner: exn) : IpcFault =
    let aroseInFrameReader =
        stackTrace
        |> Option.exists (fun trace -> trace.Contains("HeaderDelimitedMessageHandler", StringComparison.Ordinal))

    match inner with
    | :? OutOfMemoryException as oom ->
        if aroseInFrameReader then
            IpcFault.CorruptedFrame inner
        else
            IpcFault.ClientOutOfMemory oom
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

let classifyIpcFault (inner: exn) : IpcFault =
    classifyIpcFaultAt (inner.StackTrace |> Option.ofObj) inner

/// Map an unwrapped IPC exception to a user-actionable hint, or None if the
/// exception type isn't one we have a known recovery story for. Pure so it can
/// be unit-tested without round-tripping through a real pipe.
///
/// A corrupted-frame fault only reaches its hint AFTER `runIpcWithSelfHeal`
/// already tried the automatic restart-and-retry. A client OOM deliberately
/// takes the no-restart path and says so.
let ipcErrorHint (inner: exn) : string option =
    match classifyIpcFault inner with
    | IpcFault.CorruptedFrame _ ->
        Some
            "The IPC pipe returned a corrupted or oversized frame. fshw already restarted \
             the daemon and retried; since it recurred, check `logs/daemon.log` for a \
             second daemon, a crash loop, or runaway memory growth."
    | IpcFault.ClientOutOfMemory _ ->
        Some
            "The fshw CLI ran out of memory while handling the IPC call. The daemon was not \
             restarted because this failure carries no evidence of a corrupted frame; \
             inspect the client process memory limit and reduce concurrent work."
    | IpcFault.TimedOut _ -> Some "Daemon did not respond in time. It may be busy or hung — check `logs/daemon.log`."
    | IpcFault.Other _ -> None

/// The first line of an IPC failure must name the process whose failure is known.
/// A bare OOM is evidence about this CLI, not about daemon connectivity; retaining
/// the generic daemon-connection headline there sends operators toward the healthy
/// process and contradicts the no-restart recovery policy below.
let ipcErrorHeadline (inner: exn) : string =
    match classifyIpcFault inner with
    | IpcFault.ClientOutOfMemory _ -> $"The fshw CLI ran out of memory while handling daemon IPC: %s{inner.Message}"
    | IpcFault.CorruptedFrame _
    | IpcFault.TimedOut _
    | IpcFault.Other _ -> $"Could not connect to daemon: %s{inner.Message}"

/// Render a failed IPC call to stderr: a process-accurate headline plus the
/// unwrapped error's recovery hint (if any). Shared by every IPC entry point so
/// the message and hint stay identical across the CLI.
let reportDaemonError (ex: exn) : unit =
    let inner = unwrapIpcException ex
    eprintfn "%s" (ipcErrorHeadline inner)

    match ipcErrorHint inner with
    | Some h -> eprintfn "  hint: %s" h
    | None -> ()

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
        | IpcFault.CorruptedFrame _ ->
            eprintfn
                "⚠ the daemon returned a corrupted IPC reply (%s) — restarting the daemon and retrying..."
                (inner.GetType().Name)

            if forceRestart () then
                try
                    action ()
                with retryEx ->
                    onFailure retryEx
            else
                onFailure ex
        | IpcFault.ClientOutOfMemory _
        | IpcFault.TimedOut _
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

/// Ask the test-prune plugin what the last completed run actually covered — the daemon
/// twin of `RunOnceCheck.readTestRun`, which documents why the two ways of not getting
/// an answer are kept apart:
///
///   * an UNKNOWN-COMMAND reply → `ScopeUnknown` (no test projects configured);
///   * a TRANSPORT FAULT → `ScopeUnreadable`, refused in BOTH modes.
///
/// Nothing here can round UP to `FullSuite`, and both failures are WARNED about rather
/// than folded in silently.
let internal readTestRun (ipc: IpcOps) (pipeName: string) : TestRunReport =
    try
        let reply = ipc.RunCommand pipeName TestScopeCommand "" |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply reply then
            FsHotWatch.Logging.warn
                "cli-confirm"
                $"the daemon has no `%s{TestScopeCommand}` command — it has no test projects configured, so a merge \
                   verdict cannot be earned from it. `fshw confirm` will report NO VERDICT."

            IpcParsing.TestRunReport.ofScopeOnly ScopeUnknown
        else
            IpcParsing.parseTestRunReport reply
    with ex ->
        FsHotWatch.Logging.warn
            "cli-confirm"
            $"could not read the test scope: %s{ex.Message}. This is NOT \"no tests were needed\" — the check will \
               report NO VERDICT rather than pass on a reading it does not have."

        IpcParsing.TestRunReport.ofScopeOnly (
            ScopeUnreadable $"the `%s{TestScopeCommand}` request to the daemon faulted: %s{ex.Message}"
        )

/// AUTOMATION-259. Ask what `check`'s impact selection WOULD have reached in the run
/// this `confirm` did not have to escalate.
///
/// Costs nothing and RUNS nothing: the answer was computed at the launch chokepoint and
/// has been sitting in the plugin since. Every way of not getting one is a value, not an
/// exception — a daemon without the command (an older build, or no test projects), a
/// fault, a reply this build cannot read — and every one of them reaches the verdict as
/// "no sample", never as agreement.
let internal readCheckReach (ipc: IpcOps) (pipeName: string) : IpcParsing.CheckReachReading =
    try
        let reply = ipc.RunCommand pipeName CheckReachCommand "" |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply reply then
            IpcParsing.ReachUnavailable
                $"the daemon has no `%s{CheckReachCommand}` command — it predates AUTOMATION-259's projection, or it \
                   has no test projects configured"
        else
            IpcParsing.parseCheckReach reply
    with ex ->
        IpcParsing.ReachUnavailable $"the `%s{CheckReachCommand}` request to the daemon faulted: %s{ex.Message}"

/// Put the daemon's test-prune plugin into full-suite scope for the rest of this
/// session. Called BEFORE `confirm` triggers its scan, so the test run the scan provokes
/// is already unfiltered and `confirm` never pays for two runs.
///
/// A failure here is NOT fatal on its own — `confirm` does not trust this call's return
/// value anyway. It trusts `readTestRun`, which reports what actually ran. If the scope
/// could not be set, the run comes back impact-filtered and the verdict is
/// `UnearnedScope`. The request is not the evidence.
let internal requestFullSuiteScope (ipc: IpcOps) (pipeName: string) : unit =
    try
        let reply =
            ipc.RunCommand pipeName SetScopeCommand FullSuiteScopeArgs
            |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply reply then
            eprintfn
                $"fshw confirm: the daemon has no `%s{SetScopeCommand}` command (no test projects configured). \
                   The verdict will be refused."
        else
            FsHotWatch.Logging.debug "cli-confirm" $"set-scope reply: %s{reply}"
    with ex ->
        eprintfn
            $"fshw confirm: could not put the daemon in full-suite scope (%s{ex.Message}). \
               The tests will run impact-filtered, and the verdict will be refused."

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

/// Run EVERY configured test project on the daemon, now — `run-tests` with no filter
/// and no project selection. This is how `fshw confirm` FORCES the run it demands:
/// `set-scope full` only makes the NEXT run unfiltered, and on a warm daemon whose
/// impact DB says nothing changed there is no next run.
/// `CheckVerdict.confirmNeedsFullRun` decides when this fires — never in the inner
/// loop, and never when the scan already produced a full suite.
///
/// Sends no `waitSec`, so the plugin's own default budget applies. An expired budget
/// does NOT cancel the run (it was already launched; only the wait gave up), and the
/// caller's `settle` is the authoritative bound.
let internal forceFullSuiteRun (ipc: IpcOps) (pipeName: string) : unit =
    try
        let reply = ipc.RunCommand pipeName RunTestsCommand "{}" |> Async.RunSynchronously

        if FsHotWatch.Ipc.isUnknownCommandReply reply then
            FsHotWatch.Logging.warn
                "cli-confirm"
                $"the daemon has no `%s{RunTestsCommand}` command — no tests can be forced"
        else
            FsHotWatch.Logging.debug "cli-confirm" $"run-tests reply: %s{reply}"
    with ex ->
        FsHotWatch.Logging.warn "cli-confirm" $"the forced full-suite run failed: %s{ex.Message}"

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
/// Shared by BOTH the initial pre-verdict scan AND the convergence re-scan, so there is
/// ONE definition of "make the tree fresh": the watcher is an optimization, never the
/// source of truth, and `fshw scan` is not a required manual pre-step.
let internal forceScanAndWait (ipc: IpcOps) (pipeName: string) : string =
    ipc.Scan pipeName |> Async.RunSynchronously |> ignore
    ipc.WaitForScan pipeName -1L |> Async.RunSynchronously

let private ensureAndQueryErrors
    (mode: ProgressRenderer.RenderMode)
    (checkMode: CheckVerdict.CheckMode)
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
        eprintfn "Failed to start daemon — the check could not run. See logs/daemon.log."
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
                IpcOutput.pollAndRender
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
                    // AUTOMATION-259. What `check` would have reached in the run this
                    // confirm did not have to escalate. Read at publish time, not here.
                    (fun () -> readCheckReach ipc pipeName)
                    // `confirm`'s teeth — see `CheckVerdict.confirmNeedsFullRun`.
                    (fun () -> forceFullSuiteRun ipc pipeName)
                    // Convergence re-scan: the same helper as the initial scan above, so
                    // there is ONE definition of "make the tree fresh".
                    (fun () -> forceScanAndWait ipc pipeName))

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

    let hash =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configContent))

    Convert.ToHexStringLower(hash).Substring(0, 16)

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
    (currentHash: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fshw")

    let logDir =
        if Path.IsPathRooted(logDirName) then
            logDirName
        else
            Path.Combine(repoRoot, logDirName)

    fileOps.CreateDirectory logDir
    let logFile = Path.Combine(logDir, "daemon.log")
    eprintfn "Starting daemon... (log: %s)" logFile
    ipc.LaunchDaemon repoRoot extraArgs logFile
    fileOps.CreateDirectory stateDir
    fileOps.WriteAllText (Path.Combine(stateDir, "config.hash")) currentHash
    let deadline = DateTime.UtcNow.AddSeconds(startupTimeoutSeconds)
    let mutable isUp = ipc.IsRunning pipeName

    while not isUp && DateTime.UtcNow < deadline do
        Thread.Sleep(100)
        isUp <- ipc.IsRunning pipeName

    isUp

let private startFreshDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (currentHash: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    startFreshDaemonWith defaultFileOps ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds

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
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds
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
            startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds

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

/// Bracket a `check`/`confirm` run with the run-level `beforeRun`/`afterRun` hooks.
/// See the section header above for the full contract.
///
/// When NEITHER hook is configured this is a straight `action ()` — no latch, no
/// signal handlers, no shell machinery. Otherwise the hooks reuse `.fshw.json`'s
/// existing bounded-child spawn (`makeShellHookWithResult`, `/bin/sh -c`).
///
/// Works for BOTH transports because it wraps the ACTION, whatever it is: the
/// daemon path (`queryPluginIn`) and `--run-once` (`RunOnceCheck.runOnceAndVerdict`)
/// are the same `action ()` from here.
let withRunHooks (repoRoot: string) (config: DaemonConfiguration) (action: unit -> int) : int =
    match config.BeforeRun, config.AfterRun with
    | None, None -> action ()
    | beforeRunCmd, afterRunCmd ->
        let timeoutSec = Some(resolveRunHookTimeoutSec config)

        // afterRun as a latched, best-effort teardown. `makeShellHookWithResult`
        // already logs a failure (and its output) at error; the extra line here says
        // the ONE thing that matters at this layer — the verdict is unchanged.
        let afterRun =
            makeRunOnce (fun () ->
                match afterRunCmd with
                | None -> ()
                | Some cmd ->
                    let (success, _) = makeShellHookWithResult "afterRun" timeoutSec repoRoot cmd ()

                    if not success then
                        FsHotWatch.Logging.error
                            "afterRun"
                            "afterRun hook exited non-zero (see the failure above) — the run's exit code is \
                             UNCHANGED; a run-level teardown failure never alters the verdict.")

        // Trap signals only when there is an afterRun to run; otherwise leave the
        // default SIGINT/SIGTERM behaviour untouched.
        use _signals =
            match afterRunCmd with
            | Some _ -> installRunSignalHandlers afterRun exit
            | None ->
                { new IDisposable with
                    member _.Dispose() = () }

        // beforeRun FIRST — before `action`, hence before the daemon is contacted —
        // and FAIL-CLOSED. Surface the captured output like `tests.beforeRun` does,
        // so a refused preflight shows WHY, not just that it refused.
        let proceed =
            match beforeRunCmd with
            | None -> true
            | Some cmd ->
                let (success, output) =
                    makeShellHookWithResult "beforeRun" timeoutSec repoRoot cmd ()

                if not success then
                    eprintfn
                        "fshw: beforeRun hook failed — aborting the run before any check ran (the daemon was not contacted):"

                    eprintfn "%s" output

                success

        if not proceed then
            // Fail-closed: exit 2, NOT 1. afterRun does NOT fire here — beforeRun is
            // the acquire, and a failed acquire has nothing for afterRun to release
            // (afterRun brackets the ACTION, which never began).
            2
        else
            try
                action ()
            finally
                afterRun ()

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
let withRunHooksFor
    (verb: RunHookCommand)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (action: unit -> int)
    : int =
    if runHooksApplyTo config verb then
        withRunHooks repoRoot config action
    else
        action ()

/// Execute a parsed command with injectable dependencies.
let executeCommand
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
        | Stop
        | Scan
        | Status _
        | Init
        | Config _
        | Coverage _
        | DeadCode _
        | Completions -> false

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

        let ensureDaemonFn () =
            ensureDaemon ipc repoRoot pipeName opts.DaemonExtraArgs config.LogDir startupTimeoutSeconds

        // Gate the check on the daemon actually answering RPCs, not just the pipe
        // being listenable. The readiness deadline is at least
        // `DaemonReadinessTimeoutSeconds`, never shorter than the launch timeout.
        let waitReadyFn () =
            waitForDaemonReady ipc repoRoot pipeName (max startupTimeoutSeconds DaemonReadinessTimeoutSeconds)

        // Forced restart for corrupted-pipe self-healing: stop EVERYTHING answering on
        // the pipe (a corrupted reply usually means two daemons share it, so one
        // Shutdown is not enough), reap the pidfile, start fresh. Unlike
        // `ensureDaemonFn` this never reuses — the current pipe occupant is emitting
        // garbage.
        let forceRestartDaemon () : bool =
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

            startFreshDaemon
                ipc
                repoRoot
                pipeName
                (computeConfigHash repoRoot)
                opts.DaemonExtraArgs
                config.LogDir
                startupTimeoutSeconds

        // Shadow the module-level wrapper with the heal-capable one so every
        // IPC call site in this scope self-heals a corrupted pipe.
        let withIpc = withIpc forceRestartDaemon

        let queryPluginIn
            (checkMode: CheckVerdict.CheckMode)
            (mode: ProgressRenderer.RenderMode)
            (filter: string)
            : int =
            ensureAndQueryErrors
                mode
                checkMode
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
            queryPluginIn CheckVerdict.InnerLoop mode filter

        /// `check`/`confirm` with NO daemon (`--run-once`). Same verdict, same verdict
        /// file, same exit codes — only the transport differs (`PluginHost.RunCommand`
        /// in-process instead of a socket).
        ///
        /// A misconfiguration surfaced during plugin registration (e.g. the fail-loud
        /// analyzers guard) raises `ConfigError`. Report it cleanly with a RED exit code
        /// — same contract as the config-load handler — rather than crashing with an
        /// unhandled-exception stack trace.
        let runOnceIn (checkMode: CheckVerdict.CheckMode) : int =
            try
                RunOnceCheck.runOnceAndVerdict
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
                eprintfn "Failed to start daemon"
                1
            else
                action ()

        let withDaemonAndIpc (action: unit -> int) : int = withDaemon (fun () -> withIpc action)

        match command with
        | Start ->
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

                // Which process is which, when two exist for one repo root
                // (AUTOMATION-289). `ps` could not answer that after the fact:
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
                    eprintfn $"Starting FsHotWatch daemon for %s{repoRoot} — claimed the singleton lock, %s{argvLine}"
                    eprintfn $"Pipe: %s{pipeName}"

                    // Write our own PID so killStaleDaemon can find the actual daemon process,
                    // not the nohup wrapper that launched us.
                    File.WriteAllText(pidFile, string Environment.ProcessId)

                    // Record THIS binary's identity BEFORE the IPC pipe starts
                    // listening, so a CLI that observes a live pipe always finds the
                    // record. Any daemon that never wrote one reads as `NotRecorded`
                    // — and is restarted.
                    DaemonIdentity.recordCurrent repoRoot

                    let daemon = createDaemon repoRoot
                    registerPlugins daemon repoRoot config
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

                    if File.Exists pidFile then
                        File.Delete pidFile

                    0
        | Stop ->
            // No corrupted-pipe self-heal here: restarting a daemon in order
            // to stop it would defeat the command.
            withIpcNoHeal (fun () ->
                // Multiple daemons may be listening on the same pipe, so iterate Shutdown
                // until it has been quiet for two consecutive probes: that leaves no
                // orphans behind and does not misreport "No daemon running" while the OS
                // is still tearing down the last pipe endpoint.
                let overallTimeout = TimeSpan.FromSeconds(30.0)
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let mutable stopped = 0
                let mutable consecutiveQuiet = 0

                while consecutiveQuiet < 2 && sw.Elapsed < overallTimeout do
                    if ipc.IsRunning pipeName then
                        consecutiveQuiet <- 0

                        // Bulk-stop loop tolerates one shutdown failing (the OS may
                        // be tearing down the pipe mid-call). Log at debug so failures
                        // are diagnosable.
                        try
                            ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
                            stopped <- stopped + 1
                        with ex ->
                            FsHotWatch.Logging.debug
                                "cli-stop"
                                $"Shutdown attempt failed: %s{ex.GetType().Name}: %s{ex.Message}"
                    else
                        consecutiveQuiet <- consecutiveQuiet + 1

                    Thread.Sleep(100)

                match stopped with
                | 0 -> UI.info "No daemon running"
                | 1 -> UI.success "Daemon stopped"
                | n -> UI.success $"{n} daemons stopped"

                0)
        | Scan ->
            // A scan runs REAL WORK on the daemon — never on a stale-binary
            // one (its results would come from the wrong binary). Same decision as
            // ensureDaemon, which also announces why it restarts.
            if ipc.IsRunning pipeName then
                match DaemonIdentity.verdictFor repoRoot with
                | DaemonIdentity.IdentityVerdict.Stale _ -> ensureDaemonFn () |> ignore
                | DaemonIdentity.IdentityVerdict.Match -> ()

            withIpc (fun () ->
                let result = ipc.Scan pipeName |> Async.RunSynchronously
                UI.success $"Scan: %s{result}"
                0)
        | Status pluginName ->
            // Say it in words: a status computed by a daemon built from different code
            // than this CLI is never presented silently as current.
            if ipc.IsRunning pipeName then
                match DaemonIdentity.verdictFor repoRoot with
                | DaemonIdentity.IdentityVerdict.Stale reason -> eprintfn "%s" (staleIdentityStatusWarning reason)
                | DaemonIdentity.IdentityVerdict.Match -> ()

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

            withDaemon (fun () ->
                let result =
                    if UI.isInteractive then
                        UI.withSpinner "Rerunning tests" (fun () ->
                            ipc.RunCommand pipeName "run-tests" runArgsJson |> Async.RunSynchronously)
                    else
                        eprintfn "  Rerunning tests..."
                        ipc.RunCommand pipeName "run-tests" runArgsJson |> Async.RunSynchronously

                IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
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
            withRunHooksFor RunHookCommand.Check repoRoot config (fun () -> runOnceIn CheckVerdict.InnerLoop)
        | Check flags -> withRunHooksFor RunHookCommand.Check repoRoot config (fun () -> queryPluginWith (mode) "")
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
            | Verdict.PriorConfirmation.StillApplies v ->
                // DELIBERATELY UNWRAPPED by the run-level hooks: this fast path starts no
                // daemon, sets no scope and runs no test — it only reads
                // `.fshw/verdict.json` and re-checks it against the tree, so there is no
                // heavy work for a gate-lock to guard. Only the `MustEarn` arm below,
                // which actually runs the suite, is wrapped.
                UI.success $"confirm — %s{Verdict.describeStillApplies v}"
                eprintfn ""
                eprintfn "  AGENTS: READ the above — just don't SCREEN-SCRAPE it. The same facts, machine-readable:"
                eprintfn $"    verdict  %s{Verdict.RelativePath}   (this verdict, re-checked against the tree on disk)"
                0
            | Verdict.PriorConfirmation.MustEarn ->
                // Same pipeline as `check`, but full-suite scope is set FIRST (so the test
                // run the scan provokes is unfiltered), the suite is FORCED if the scan
                // did not produce one, and the verdict is computed in `Confirmation` mode,
                // which has no path to a green that skips a full-suite run.
                //
                // `--run-once` needs no daemon, which is the only reason CI can invoke
                // `confirm` at all. This arm does the heavy work, so it is the one the
                // gate-lock must guard.
                withRunHooksFor RunHookCommand.Confirm repoRoot config (fun () ->
                    if isRunOnce flags then
                        runOnceIn CheckVerdict.Confirmation
                    else
                        queryPluginIn CheckVerdict.Confirmation mode "")
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
                | Verdict.Green ->
                    say $"%s{Color.green}✓%s{Color.reset} %s{verb}: green — for THIS tree (%s{v.TreeHash})"
                | Verdict.Red -> say $"%s{Color.red}✗%s{Color.reset} %s{verb}: RED — for this tree"
                | Verdict.Incomplete reason -> say $"%s{Color.red}✗%s{Color.reset} %s{verb}: NO VERDICT — %s{reason}"
            | Verdict.Report.Stale(v, reason) ->
                // A green from a different tree — or a different BINARY — is still a green,
                // which is exactly why it may never be REPORTED as one.
                //
                // `Report.Stale`'s second field already names the fault precisely (which
                // provenance link broke) and begins "stale: ...", so it is printed as-is,
                // like the `NoVerdict` arm below.
                say $"%s{Color.red}✗%s{Color.reset} %s{reason}"
                say $"    verdict tree  %s{v.TreeHash}"
                say "  Re-run `fshw check` (or `fshw confirm` for a merge). Never reuse it."
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
            FishCompletions.writeToFile commandTree cliName
            eprintfn "%s" $"%s{Color.green}✓%s{Color.reset} Fish completions installed"
            eprintfn "  Wrote ~/.config/fish/completions/%s.fish" cliName
            0

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

[<EntryPoint>]
let main args =
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

                let config =
                    try
                        loadConfig repoRoot
                    with ConfigError msg ->
                        eprintfn $"fshw: config error: %s{msg}"
                        exit 2

                let cacheConfig = if opts.NoCache then DaemonConfig.NoCache else config.Cache
                let (backend, keyProvider) = DaemonConfig.createCacheComponents repoRoot cacheConfig

                let fileCommandPatterns =
                    config.FileCommands
                    |> List.choose (fun fc -> fc.Pattern)
                    |> List.map FsHotWatch.Watcher.FilePattern.parse

                let createDaemon (root: string) =
                    // Resolve the idle-exit threshold from the `idleExitMin`
                    // config + this daemon's repo path (AUTO-on for
                    // `/.workspaces/` checkouts). `None` leaves the timer off.
                    let idleExitMin = FsHotWatch.IdleExit.resolveThreshold config.IdleExitMin root

                    // Resolve the pressure floor from `pressureIdleFloorMin`
                    // (default-on at 2 min). Under memory pressure this shortens
                    // an already-eligible idle window to `min(idleExitMin, floor)`;
                    // `None` disables pressure-shortening. It never makes a
                    // non-eligible daemon (e.g. the default workspace) eligible.
                    let pressureIdleFloorMin =
                        FsHotWatch.IdleExit.resolvePressureFloor config.PressureIdleFloorMin

                    Daemon.create
                        root
                        { Daemon.DaemonOptions.defaults with
                            CacheBackend = backend
                            CacheKeyProvider = keyProvider
                            ExcludePatterns = config.Exclude
                            ExtraWatchPatterns = fileCommandPatterns
                            FsEventsLatencySeconds = float config.FsEventsLatencyMs / 1000.0
                            IdleExitMin = idleExitMin
                            PressureIdleFloorMin = pressureIdleFloorMin }

                executeCommand createDaemon defaultIpcOps repoRoot pipeName command opts config 30.0
            // ROOT-level unknown command: the dynamic plugin-passthrough. Forward `rest`
            // verbatim; if the daemon doesn't recognize it, fail hard with the canonical
            // error + help, so garbage CLI input fails uniformly.
            | RootUnknownCommand(input, rest, err) ->
                let argsStr = rest |> String.concat " "

                let opts =
                    { defaultGlobalOptions with
                        AgentMode = argList |> List.exists (fun a -> a = "--agent" || a = "-a")
                        CompactMode = argList |> List.exists (fun a -> a = "--compact" || a = "-q") }

                forwardRootUnknownCommand defaultIpcOps pipeName opts input argsStr (fun () -> reportParseError err)
            // RepoIndependent is fully handled above before the repo-root lookup.
            | RepoIndependent exitCode -> exitCode
