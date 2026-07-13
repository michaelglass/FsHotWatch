module FsHotWatch.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open CommandTree
open FsHotWatch.Cli.DaemonConfig
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
    | [<CmdFlag(Description = "Seconds to wait for an in-flight background test run to release the slot before reporting busy (default 600). Raise it above a long tests.beforeRun chain so an explicit rerun isn't defeated.");
        CmdArg("seconds")>] WaitSec of int

/// Default slot-wait budget (seconds) sent to the daemon's `run-tests` command
/// when `--wait-sec` is not given. Well above the old fixed 120 s so a long
/// `tests.beforeRun` chain (90 s+) can't make an explicit `test-rerun` give up
/// before the prior in-flight run releases the slot.
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
            // `--wait-sec` is a client-side slot-wait knob, not an xUnit filter —
            // it travels in the run-tests payload, never in the runner arg string.
            | WaitSec _ -> None)
        |> String.concat " "

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

type Command =
    | [<CmdExample("", "--no-cache"); Cmd("Start the daemon")>] Start
    | [<Cmd("Stop the daemon")>] Stop
    | [<CmdExample("", "--run-once"); Cmd("Run all checks")>] Check of RunFlag list
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

/// Compute the launch command for re-starting the daemon.
/// Returns (exe, argPrefix) where argPrefix is prepended to "start" when launching
/// (i.e. the daemon spawn becomes `exe argPrefix start`).
///
/// `processPath` is `Environment.ProcessPath` and `entryAssemblyDll` is
/// `Assembly.GetEntryAssembly().Location` (passed in so this stays pure/testable).
///
/// Three cases:
///   1. Native single-file exe (processPath is not `dotnet`): launch it directly.
///   2. `dotnet <local-dll>` (processPath is `dotnet`, entry-assembly location is
///      a real `.dll` path): spawn THAT SAME DLL — `dotnet "<dll>" start`. This
///      makes a local dev build dogfood itself; reconstructing `tool run fshw`
///      here would silently launch the PINNED tool instead of the running build
///      (the real 2026-06-05 mis-diagnosis). Note the dll path is quoted because
///      it may contain spaces, and the caller appends `start` after a space.
///      The published dotnet tool ALSO resolves a real dll path
///      (`~/.nuget/packages/fshotwatch.cli/<ver>/.../FsHotWatch.Cli.dll`), so this
///      branch spawns that dll directly for the tool too — equivalent to and more
///      precise than `tool run fshw` (no tool-resolution indirection).
///   3. `dotnet` but no usable entry-assembly path (single-file/tool-shim where
///      `GetEntryAssembly().Location` is empty): fall back to `tool run fshw`.
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
            // `dotnet <dll>` — spawn that same dll so a local build dogfoods itself
            // (and the published tool spawns its own resolved dll, not a re-resolve).
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
      GetLastWriteTimeUtc: string -> DateTime
      CreateDirectory: string -> unit }

/// Default file system operations.
let defaultFileOps: FileOps =
    { FileExists = File.Exists
      ReadAllText = File.ReadAllText
      WriteAllText = fun path content -> File.WriteAllText(path, content)
      DeleteFile = File.Delete
      GetLastWriteTimeUtc = fun path -> File.GetLastWriteTimeUtc(path)
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

/// Map an unwrapped IPC exception to a user-actionable hint, or None if the
/// exception type isn't one we have a known recovery story for. Pure so it can
/// be unit-tested without round-tripping through a real pipe.
let ipcErrorHint (inner: exn) : string option =
    match inner with
    // StreamJsonRpc reads a Content-Length header then allocates a buffer of
    // that size. A corrupted/garbage header (commonly: two daemons sharing the
    // same pipe, or a leftover stale daemon from an older version) makes the
    // length nonsensical, and the buffer alloc throws OutOfMemoryException —
    // which is misleading because the machine isn't actually out of memory.
    | :? OutOfMemoryException ->
        Some
            "The IPC pipe returned a corrupted message — usually caused by another \
             daemon (possibly an older version) writing to the same pipe. Try: \
             `dotnet fshw stop` then `dotnet fshw start`."
    // Same pipe-corruption family as OOM, but a different .NET path: when the
    // Content-Length parses to a value that overflows downstream Int32 arithmetic
    // (or stream-position bookkeeping wraps on a long-running socket carrying
    // huge payloads), StreamJsonRpc surfaces an OverflowException with
    // "Arithmetic operation resulted in an overflow." Observed in production
    // during the Intelligence stress test (fshw 0.10.0-stresstest4), where a
    // daemon at ~7.8 GB RSS started returning malformed frames.
    | :? OverflowException ->
        Some
            "The IPC pipe returned a corrupted or oversized message — usually a stale/leaky \
             daemon. Try: `dotnet fshw stop` then `dotnet fshw start`. If it recurs, \
             check `logs/daemon.log` for runaway memory growth."
    | :? TimeoutException -> Some "Daemon did not respond in time. It may be busy or hung — check `logs/daemon.log`."
    | _ -> None

/// Render a failed IPC call to stderr: the unwrapped daemon-connection error plus
/// its recovery hint (if any). Shared by every IPC entry point so the message and
/// hint stay identical across the CLI.
let reportDaemonError (ex: exn) : unit =
    let inner = unwrapIpcException ex
    eprintfn "Could not connect to daemon: %s" inner.Message

    match ipcErrorHint inner with
    | Some h -> eprintfn "  hint: %s" h
    | None -> ()

/// Wrap an IPC call with connection error handling.
let private withIpc (action: unit -> int) : int =
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
/// non-zero that an autonomous loop misreads as diagnostics.
let private withCheckIpc (action: unit -> int) : int =
    try
        action ()
    with ex ->
        reportDaemonError ex
        eprintfn "  The check could not complete — see logs/daemon.log."
        2

/// Ensure daemon, poll for progress, render colored output.
let private ensureAndQueryErrors
    (mode: ProgressRenderer.RenderMode)
    (noWarnFail: bool)
    (ensureDaemon: unit -> bool)
    (waitReady: unit -> DaemonReadiness)
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
            withCheckIpc (fun () ->
                IpcOutput.pollAndRender
                    mode
                    (renderLines mode (not noWarnFail))
                    noWarnFail
                    (fun () -> ipc.WaitForScan pipeName -1L |> Async.RunSynchronously)
                    // Authoritative settle: block until the daemon reports its sound
                    // verdict (`waitForVerdict`, which gates on plugin busy/inflight
                    // state + generation advancement + quiescence). This is what
                    // closes the false-green hole — `WaitForScan` alone only waits
                    // for the SCAN generation to be signalled, which can race ahead
                    // of the test-prune run launched by the build's BuildCompleted.
                    // `-1` = no client-imposed timeout; the daemon bounds the
                    // wait with its hard verdict deadline (`resolveVerdictDeadline`,
                    // FSHW_VERDICT_DEADLINE_SEC, default 60 min) so this can never
                    // block forever — a breach surfaces via `isVerdictWaitTimeout`
                    // as a diagnostic exit 2 naming the wedged plugin.
                    (fun () -> ipc.WaitForComplete pipeName -1 |> Async.RunSynchronously)
                    (fun () -> ipc.GetStatus pipeName |> Async.RunSynchronously)
                    (fun () -> ipc.GetDiagnostics pipeName pluginFilter |> Async.RunSynchronously)
                    // Convergence re-scan: start a scan and block until it (and the
                    // plugins it triggers) settle, so the next GetDiagnostics read
                    // reflects the fresh scan. `Scan` returns "scan started:<gen>";
                    // `WaitForScan -1L` waits for the next completion.
                    (fun () ->
                        ipc.Scan pipeName |> Async.RunSynchronously |> ignore
                        ipc.WaitForScan pipeName -1L |> Async.RunSynchronously))

/// Compute a hash of the config file + CLI binary for staleness detection (injectable).
let computeConfigHashWith (fileOps: FileOps) (repoRoot: string) (exePath: string) =
    let configPath = Path.Combine(repoRoot, ".fshw.json")

    let configContent =
        if fileOps.FileExists configPath then
            fileOps.ReadAllText configPath
        else
            ""

    let exeModTime =
        if fileOps.FileExists exePath then
            fileOps.GetLastWriteTimeUtc(exePath).Ticks.ToString()
        else
            ""

    let hash =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configContent + exeModTime))

    Convert.ToHexStringLower(hash).Substring(0, 16)

/// Compute a hash of the config file + CLI binary for staleness detection.
let private computeConfigHash (repoRoot: string) =
    computeConfigHashWith defaultFileOps repoRoot Environment.ProcessPath

/// What action ensureDaemon should take.
type DaemonAction =
    | Reuse
    | Restart
    | StartFresh

/// Determine what daemon action is needed based on current state.
let decideDaemonAction (isRunning: bool) (storedHash: string) (currentHash: string) : DaemonAction =
    if isRunning then
        if storedHash = currentHash then Reuse else Restart
    else
        StartFresh

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
    let isRunning = ipc.IsRunning pipeName

    let storedHash =
        if File.Exists hashPath then
            File.ReadAllText(hashPath).Trim()
        else
            ""

    match decideDaemonAction isRunning storedHash currentHash with
    | Reuse -> true
    | Restart ->
        eprintfn "  Daemon config changed — restarting..."

        try
            ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
            Thread.Sleep(1000)
        with ex ->
            eprintfn "  Shutdown request failed: %s" ex.Message

        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds
    | StartFresh ->
        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds

// ----------------------------------------------------------------------------
// Daemon readiness gate (AUTOMATION-66).
//
// `ensureDaemon` returns as soon as the named pipe is *listening* (`IsRunning` —
// a 500 ms probe connect). But a daemon that just (re)started is often still
// mid cold-scan (analyzer reflection load pegging cores), so the FIRST real RPC
// issued by a check — `ConnectAsync(5000)` inside `IpcClient.invoke` — can time
// out because the acceptor is starved, or hit a pipe endpoint that was briefly
// torn down during a stop→start. The old code surfaced that as exit 1
// ("failures found"), poisoning an autonomous loop's verdict. The gate below
// RETRIES such transient connect faults against a startup deadline (distinct
// from the per-RPC connect timeout) until the daemon answers, fails FAST (exit
// 2) if the daemon process is provably gone, and gives up (exit 2) if it never
// becomes responsive.
// ----------------------------------------------------------------------------

/// True when `ex` is a connect-phase transient — the daemon is reachable-in-
/// principle but not yet answering because it is mid-startup (cold scan /
/// analyzer load) or briefly tore down a pipe endpoint during a restart. These
/// are RETRIED by the readiness gate rather than surfaced as a hard failure:
///  - `TimeoutException` — a `NamedPipeClientStream.ConnectAsync` connect timeout
///    ("The operation has timed out") while the acceptor is starved by scan work.
///  - StreamJsonRpc `ConnectionLostException` (matched by type-name substring so
///    there is no compile-time dependency on the transport assembly) — the pipe
///    dropped before the request completed.
///  - `IOException` / `EndOfStreamException` (an `IOException` subtype) /
///    `ObjectDisposedException` — a raw pipe teardown (an old daemon endpoint
///    disposed during a stop→start).
/// Walks `InnerException` so an `AggregateException` from `Async.RunSynchronously`
/// is seen through.
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

/// Delete a file, swallowing any exception. F9 (audit 2026-05-02): in a bulk
/// cleanup loop one bad item shouldn't halt the rest, but the bare `with _`
/// previously hid permission and sharing-violation failures. We log the
/// exception class at debug so a future reader sees this isn't load-bearing
/// and so failures are diagnosable on `--verbose`.
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

    // Fail-fast on misconfiguration BEFORE starting (or polling for) a daemon
    // for any project-requiring command. The daemon's `start` path performs
    // the same check internally and exits 2; if we reached `ensureDaemon`
    // without checking here, the freshly-launched daemon would exit 2, the
    // CLI's `IsRunning` poll would never observe a live daemon, and the user
    // would see "Failed to start daemon" + exit 1 instead of the structured
    // "no projects discovered" + exit 2 contract. Status/Stop/Scan/Init/etc.
    // either tolerate or aren't relevant to a zero-projects workspace, so
    // they skip this check.
    let needsProjects =
        match command with
        | Start
        | Check _
        | TestRerun _
        | Format _
        | Rerun _ -> true
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

        let queryPluginWith (mode: ProgressRenderer.RenderMode) (filter: string) : int =
            ensureAndQueryErrors mode noWarnFail ensureDaemonFn waitReadyFn ipc pipeName filter

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
            // Fail-fast on misconfiguration BEFORE acquiring the lockfile,
            // writing the pidfile, or creating the daemon. Same contract as
            // the run-once paths so every entry point behaves consistently:
            // zero projects almost always means a wrong cwd or an over-eager
            // `.fshw.json` exclude pattern, and there is no useful behaviour
            // the daemon can provide.
            match RunOnceOutput.failIfNoProjects repoRoot config.Exclude with
            | Some exitCode -> exitCode
            | None ->

                let stateDir = Path.Combine(repoRoot, ".fshw")
                let pidFile = Path.Combine(stateDir, "daemon.pid")
                let lockFile = Path.Combine(stateDir, "daemon.lock")
                Directory.CreateDirectory(stateDir) |> ignore

                // OS-enforced singleton: hold an exclusive lock on daemon.lock for the
                // daemon's lifetime. Two concurrent `start` invocations cannot both
                // acquire it; the second exits cleanly. Replaces the earlier probe-based
                // guard which had a TOCTOU window between IsRunning check and pipe claim.
                let acquired =
                    try
                        Some(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    with :? IOException ->
                        None

                match acquired with
                | None ->
                    let pidInfo =
                        if File.Exists pidFile then
                            $" (pid %s{File.ReadAllText(pidFile).Trim()})"
                        else
                            ""

                    eprintfn $"Daemon already running at pipe %s{pipeName}%s{pidInfo}"
                    0
                | Some lockStream ->
                    use _lock = lockStream
                    eprintfn $"Starting FsHotWatch daemon for %s{repoRoot}"
                    eprintfn $"Pipe: %s{pipeName}"

                    // Write our own PID so killStaleDaemon can find the actual daemon process,
                    // not the nohup wrapper that launched us.
                    File.WriteAllText(pidFile, string (System.Diagnostics.Process.GetCurrentProcess().Id))

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
            withIpc (fun () ->
                // Multiple daemons may be listening on the same pipe (historically the
                // start command spawned duplicates); iterate Shutdown until the pipe
                // has been quiet for two consecutive probes so we don't leave orphans
                // behind and don't misreport "No daemon running" while the OS is still
                // tearing down the last pipe endpoint.
                let overallTimeout = TimeSpan.FromSeconds(30.0)
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let mutable stopped = 0
                let mutable consecutiveQuiet = 0

                while consecutiveQuiet < 2 && sw.Elapsed < overallTimeout do
                    if ipc.IsRunning pipeName then
                        consecutiveQuiet <- 0

                        // F9 (audit 2026-05-02): bulk-stop loop tolerates one
                        // shutdown failing (the OS may be tearing down the pipe
                        // mid-call), but the bare `with _` hid real permission
                        // / IPC bugs. Log at debug so failures are diagnosable.
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
            withIpc (fun () ->
                let result = ipc.Scan pipeName |> Async.RunSynchronously
                UI.success $"Scan: %s{result}"
                0)
        | Status pluginName ->


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
                    IpcOutput.exitCodeFromResponse noWarnFail scoped)
        | TestRerun flags ->
            // Filter knobs live here, not on `fshw test`, so the
            // forward-progress contract (everything downstream runs) stays intact.
            // `waitSec` (the slot-wait budget) always travels so a long
            // `beforeRun` chain can't defeat an explicit rerun.
            let waitSec = RerunFilter.waitSec flags

            let runArgsJson =
                match RerunFilter.render flags with
                | "" -> JsonSerializer.Serialize {| waitSec = waitSec |}
                | filter -> JsonSerializer.Serialize {| filter = filter; waitSec = waitSec |}

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
        | Check flags when isRunOnce flags ->

            // A misconfiguration surfaced during plugin registration (e.g. the
            // fail-loud analyzers guard) raises ConfigError. Report it cleanly
            // with a RED exit code — same contract as the config-load handler —
            // rather than crashing with an unhandled-exception stack trace.
            try
                RunOnceOutput.runOnceAndReport
                    (renderBlock mode (not noWarnFail))
                    noWarnFail
                    createDaemon
                    repoRoot
                    config
                    None
            with ConfigError msg ->
                eprintfn $"fshw: config error: %s{msg}"
                2
        | Check flags -> queryPluginWith (mode) ""
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

/// Resolve a ROOT-level unknown command to an exit code: forward it to the daemon
/// as a dynamic plugin command, and FAIL HARD with the canonical parse error + help
/// if the daemon doesn't recognize it (the strict-CLI contract — garbage input never
/// silently succeeds). `renderErr` re-renders the original parse error on the
/// not-recognized path. Pure wrt. its injected `ipc`, so it's unit-testable without
/// the `[<EntryPoint>]` and repo-root plumbing.
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
/// uniform renderer — a clear one-line message plus the nearest subcommand/group
/// help — and return the exit code. `CommandTree.isError` is the source of truth
/// for the code (true → non-zero). Help/Version are handled by the caller before
/// this and never reach here.
let reportParseError (err: ParseError) : int =
    eprintfn "%s" (CommandTree.renderParseError commandTree err cliName)
    if CommandTree.isError err then 1 else 0

/// Classification of a parse result with respect to what `main` must do next.
/// Separates the repo-INDEPENDENT decisions (help, version, and every genuine
/// flag/arg error — plus a NESTED unknown command, which is a real typo against a
/// known group with no daemon passthrough) from the two paths that need the repo
/// root: running a successfully-parsed command, and forwarding a ROOT-level unknown
/// command to the per-repo daemon. Pure and total, so it's unit-testable without
/// the `[<EntryPoint>]` plumbing — which is where the strict-CLI ordering lives.
type ParseDispatch =
    /// Repo-independent: print the canonical help/error to the right stream and exit
    /// with this code. Covers Help (0), Version (0), and all genuine input errors
    /// except a root-level unknown command. Fixes the out-of-repo masking bug.
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
        RepoIndependent 0
    // ROOT-level unknown command (empty groupPath) is the only error that defers to
    // the daemon; everything else fails hard here, BEFORE any repo-root lookup, so
    // running outside a jj/git checkout no longer masks flag/arg errors.
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
            // ROOT-level unknown command: the dynamic plugin-passthrough. Forward the
            // raw remaining args (`rest`) straight to the daemon. If the daemon doesn't
            // recognize it, fail hard with the canonical error + help instead of today's
            // confusing daemon echo/timeout — garbage CLI input fails uniformly.
            | RootUnknownCommand(input, rest, err) ->
                let argsStr = rest |> String.concat " "

                let opts =
                    { defaultGlobalOptions with
                        AgentMode = argList |> List.exists (fun a -> a = "--agent" || a = "-a")
                        CompactMode = argList |> List.exists (fun a -> a = "--compact" || a = "-q") }

                forwardRootUnknownCommand defaultIpcOps pipeName opts input argsStr (fun () -> reportParseError err)
            // RepoIndependent is fully handled above before the repo-root lookup.
            | RepoIndependent exitCode -> exitCode
