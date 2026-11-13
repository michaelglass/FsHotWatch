/// AUTOMATION-1657: a `--run-once` gate must never run BESIDE a live daemon on the same
/// workspace. On the laptop, `confirm --run-once` went red 3/3 in a workspace where an
/// earlier `fshw check` had left its daemon running; the same tree was green where no
/// daemon was left. The gate now refuses up front instead of producing a verdict that
/// depends on what else is running in the worktree.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.RunOnceBesideDaemonTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.Program
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

/// Raised by the host factory below: reaching it means the gate started its own run.
exception RunStartedException

let private config: DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = Off
        Lint = false
        Cache = FsHotWatch.Cli.DaemonConfig.NoCache }

let private liveDaemonIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "" }
      ReleaseIfSole = fun _ -> async { return "released" }
      Scan = fun _ _ -> async { return "" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ name _ -> async { return unknownCommandReply name }
      RunCommandFor = fun _ _ name _ -> async { return unknownCommandReply name }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      WaitForRest = fun _ _ -> async { return "rest" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      Invalidate = fun _ -> async { return "" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> failwith "a --run-once gate must never launch a daemon" }

/// A temp repository with one project and `.fshw/daemon.pid` naming `pid`.
let private withDaemonPid (name: string) (pid: int) (body: string -> 'a) : 'a =
    withTempDir name (fun repoRoot ->
        let srcDir = Path.Combine(repoRoot, "src")
        Directory.CreateDirectory srcDir |> ignore

        File.WriteAllText(
            Path.Combine(srcDir, "MyProject.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        Directory.CreateDirectory(Path.Combine(repoRoot, ".fshw")) |> ignore
        File.WriteAllText(FsHotWatch.PidFile.daemonPath repoRoot, string pid)
        body repoRoot)

type private GateRun =
    { ExitCode: int option
      Stderr: string
      RunStarted: bool }

/// Run the gate with a host factory that records being reached and stops there.
let private runGate (command: Command) (repoRoot: string) : GateRun =
    let reached = ref false

    let createDaemon (_: string) : FsHotWatch.Daemon.Daemon =
        reached.Value <- true
        raise RunStartedException

    let original = Console.Error
    use sw = new StringWriter()
    Console.SetError sw

    let exitCode =
        try
            try
                Some(
                    executeCommand
                        ""
                        createDaemon
                        (liveDaemonIpc ())
                        repoRoot
                        "fshw-1657-pipe"
                        command
                        defaultGlobalOptions
                        config
                        5.0
                )
            with RunStartedException ->
                None
        finally
            Console.SetError original

    { ExitCode = exitCode
      Stderr = sw.ToString()
      RunStarted = reached.Value }

let private gateCommand (confirm: bool) : Command =
    if confirm then
        Confirm [ ConfirmFlag.RunOnce ]
    else
        Check [ RunOnce ]

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a run-once gate refuses beside a live daemon, naming its pid and fshw stop`` (confirm: bool) =
    // This test process stands in for the live daemon: its pid answers `kill(pid, 0)`.
    let livePid = Environment.ProcessId

    withDaemonPid "run-once-beside-live-daemon" livePid (fun repoRoot ->
        let run = runGate (gateCommand confirm) repoRoot

        test <@ not run.RunStarted @>
        test <@ run.ExitCode = Some 2 @>
        test <@ run.Stderr.Contains(string livePid) @>
        test <@ run.Stderr.Contains "fshw stop" @>
        // The daemon's pid file is the daemon's: the refusal leaves it alone.
        test <@ File.Exists(FsHotWatch.PidFile.daemonPath repoRoot) @>)

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a run-once gate is not refused by a stale daemon pid file`` (confirm: bool) =
    // A pid no process holds: the leftover of a crash or a kill -9.
    withDaemonPid "run-once-beside-stale-pid" 2_000_000_000 (fun repoRoot ->
        let run = runGate (gateCommand confirm) repoRoot

        test <@ run.RunStarted @>
        test <@ not (run.Stderr.Contains "fshw stop") @>)

// --- `inspect`, with the liveness probe injected ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``inspect reports a live daemon only when the pid file names a live process`` () =
    withDaemonPid "run-once-inspect" 4242 (fun repoRoot ->
        test
            <@
                FsHotWatch.Cli.RunOnceBesideDaemon.inspect (fun _ -> true) repoRoot = FsHotWatch.Cli.RunOnceBesideDaemon.LiveDaemon
                    4242
            @>

        test
            <@
                FsHotWatch.Cli.RunOnceBesideDaemon.inspect (fun _ -> false) repoRoot = FsHotWatch.Cli.RunOnceBesideDaemon.NoLiveDaemon
            @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``inspect finds no daemon without a usable pid file`` () =
    withTempDir "run-once-inspect-none" (fun repoRoot ->
        test
            <@
                FsHotWatch.Cli.RunOnceBesideDaemon.inspect (fun _ -> true) repoRoot = FsHotWatch.Cli.RunOnceBesideDaemon.NoLiveDaemon
            @>

        Directory.CreateDirectory(Path.Combine(repoRoot, ".fshw")) |> ignore
        File.WriteAllText(FsHotWatch.PidFile.daemonPath repoRoot, "not-a-pid")

        test
            <@
                FsHotWatch.Cli.RunOnceBesideDaemon.inspect (fun _ -> true) repoRoot = FsHotWatch.Cli.RunOnceBesideDaemon.NoLiveDaemon
            @>)
