module FsHotWatch.Tests.RunSignalTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsHotWatch.Tests.TestHelpers

[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private signal(int pid, int signal)

[<Literal>]
let private SIGHUP = 1

[<Literal>]
let private SIGKILL = 9

/// `pid`'s state letter, parent and command, as the OS reports them: `/proc/<pid>/stat`
/// on Linux, `ps` elsewhere. `None` when no such process exists.
let private processState (pid: int) : (string * int * string) option =
    if OperatingSystem.IsLinux() then
        try
            // `pid (comm) state ppid …`; comm may hold spaces and parentheses, so split at
            // the LAST `)`.
            let stat = File.ReadAllText $"/proc/%d{pid}/stat"
            let close = stat.LastIndexOf ')'
            let comm = stat.Substring(stat.IndexOf '(' + 1, close - stat.IndexOf '(' - 1)
            let rest = stat.Substring(close + 2).Split ' '
            Some(rest[0], int rest[1], comm)
        with :? IOException ->
            None
    else
        let info = ProcessStartInfo("ps", [ "-o"; "stat=,ppid=,comm="; "-p"; string pid ])
        info.RedirectStandardOutput <- true
        use ps = Process.Start info
        let line = ps.StandardOutput.ReadToEnd().Trim()
        ps.WaitForExit()

        match line.Split([| ' '; '\t' |], 3, StringSplitOptions.RemoveEmptyEntries) with
        | [| state; ppid; comm |] -> Some(state, int ppid, comm)
        | _ -> None

/// Whether `pid` is still running. `kill(pid, 0)` also succeeds for a ZOMBIE — a process
/// that has exited and only waits for its parent (or, orphaned, the reaper it was
/// re-parented to) to collect its status. A zombie runs nothing and holds no resource,
/// so it has not outlived the run.
let private alive (pid: int) =
    signal (pid, 0) = 0
    && (processState pid
        |> Option.forall (fun (state, _, _) -> not (state.StartsWith "Z")))

/// What the OS says about each pid, for a failure message.
let private describePids (pids: (string * int) list) =
    pids
    |> List.map (fun (name, pid) ->
        match processState pid with
        | Some(state, ppid, comm) -> $"%s{name} pid %d{pid}: state %s{state}, parent %d{ppid}, `%s{comm}`"
        | None -> $"%s{name} pid %d{pid}: gone")
    |> String.concat "; "

let private cliAssembly () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let assembly =
        Path.Combine(root, "src", "FsHotWatch.Cli", "bin", configuration, "net10.0", "FsHotWatch.Cli.dll")

    Assert.True(File.Exists assembly, $"build the real CLI before running this integration test: %s{assembly}")
    assembly

let private dotnetHost () =
    Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))

let private readPid (path: string) =
    match Int32.TryParse((if File.Exists path then File.ReadAllText path else "").Trim()) with
    | true, pid when pid > 0 -> Some pid
    | _ -> None

/// A terminal that closes sends SIGHUP. The run must end the way SIGINT and SIGTERM end
/// it: the hook it was interrupted in is reaped, a verdict says the run was signalled,
/// and the exit code is 128 + SIGHUP(1).
[<Fact(Timeout = 120000)>]
let ``SIGHUP during a beforeRun hook reaps the hook and exits 129`` () =
    withTempDir "run-sighup" (fun root ->
        use init = Process.Start(ProcessStartInfo("git", [ "init"; "--quiet"; root ]))
        Assert.True(init.WaitForExit 30000 && init.ExitCode = 0, "git init must make the fixture a checkout")

        let shellPid = Path.Combine(root, "shell.pid")
        let sleepPid = Path.Combine(root, "sleep.pid")

        // The hook's tree is three deep: fshw's shell, the `sh -c` it runs, and the
        // `sleep` that one backgrounds.
        File.WriteAllText(
            Path.Combine(root, ".fshw.json"),
            """{ "beforeRun": "sh -c 'echo $$ > shell.pid; sleep 300 & echo $! > sleep.pid; wait'" }"""
        )

        let info = ProcessStartInfo(dotnetHost ())
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for arg in [ cliAssembly (); "check"; "--run-once" ] do
            info.ArgumentList.Add arg

        use cli = Process.Start info
        let stdout = cli.StandardOutput.ReadToEndAsync()
        let stderr = cli.StandardError.ReadToEndAsync()

        try
            Assert.True(
                waitUntilTrue (fun () -> (readPid shellPid).IsSome && (readPid sleepPid).IsSome) 60000,
                "positive control: the beforeRun hook must start and announce its processes"
            )

            let hookShell = (readPid shellPid).Value
            let hookSleep = (readPid sleepPid).Value
            Assert.True(alive hookSleep, "positive control: the hook's child is alive before the signal")

            Assert.Equal(0, signal (cli.Id, SIGHUP))
            Assert.True(cli.WaitForExit 30000, "the CLI must exit after SIGHUP")

            let output = stdout.Result + "\n" + stderr.Result
            Assert.True((cli.ExitCode = 129), $"expected exit 129, got %d{cli.ExitCode}:\n%s{output}")

            let reaped =
                waitUntilTrue (fun () -> not (alive hookSleep) && not (alive hookShell)) 10000

            let states = describePids [ ("hook shell", hookShell); ("hook sleep", hookSleep) ]
            Assert.True(reaped, $"the hook's process tree must not outlive the run (%s{states}):\n%s{output}")

            let verdict = File.ReadAllText(Path.Combine(root, ".fshw", "verdict.json"))
            Assert.Contains("the run was signalled before the check could finish", verdict)

            // The signal stopped the hook; the hook did not fail, and the record says so.
            Assert.DoesNotContain("beforeRun hook failed", output)
            use record = System.Text.Json.JsonDocument.Parse verdict
            Assert.Equal(129, record.RootElement.GetProperty("exitCode").GetInt32())

            let hookOutcomes =
                [ for h in record.RootElement.GetProperty("hooks").EnumerateArray() ->
                      h.GetProperty("outcome").GetString() ]

            Assert.Equal<string list>([ "signalled" ], hookOutcomes)
        finally
            if not cli.HasExited then
                cli.Kill true
                cli.WaitForExit 10000 |> ignore

            // Only the processes this test's own hook announced.
            [ readPid sleepPid; readPid shellPid ]
            |> List.choose id
            |> List.filter alive
            |> List.iter (fun pid -> signal (pid, SIGKILL) |> ignore))

[<Literal>]
let private SIGTERM = 15

/// A daemon run in the foreground under a supervisor (`timeout`, a service manager) is
/// ended with SIGTERM. The test host it spawned may sit outside the daemon's process
/// group, so a group signal does not reach it: the daemon itself must take its spawned
/// trees down through its process registry, and say so in its log.
[<Fact(Timeout = 180000)>]
let ``SIGTERM to a daemon reaps its spawned test host and logs the shutdown`` () =
    withTempDir "daemon-sigterm" (fun root ->
        use init = Process.Start(ProcessStartInfo("git", [ "init"; "--quiet"; root ]))
        Assert.True(init.WaitForExit 30000 && init.ExitCode = 0, "git init must make the fixture a checkout")

        let hostPid = Path.Combine(root, "host.pid")
        let childPid = Path.Combine(root, "child.pid")
        let project = Path.Combine(root, "tests", "Probe")
        Directory.CreateDirectory project |> ignore

        File.WriteAllText(
            Path.Combine(project, "Probe.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Probe.fs" /></ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(project, "Probe.fs"), "module Probe\nlet answer = 2\n")

        // fshw splits `args` on whitespace without a shell's quoting, so both fakes
        // are scripts. The build writes the output fshw verifies after a build; the
        // test host is two deep, the shell fshw runs and the `sleep` it backgrounds,
        // and never finishes on its own inside the test's budget.
        File.WriteAllText(
            Path.Combine(root, "build.sh"),
            "#!/bin/sh\nmkdir -p tests/Probe/bin/Debug/net10.0 && touch tests/Probe/bin/Debug/net10.0/Probe.dll\n"
        )

        File.WriteAllText(
            Path.Combine(root, "host.sh"),
            "#!/bin/sh\necho $$ > host.pid\nsleep 300 &\necho $! > child.pid\nwait\n"
        )

        File.WriteAllText(
            Path.Combine(root, ".fshw.json"),
            """{
  "build": { "command": "sh", "args": "build.sh" },
  "format": false,
  "lint": false,
  "tests": { "projects": [{ "project": "Probe", "command": "sh", "args": "host.sh" }] }
}
"""
        )

        let start (args: string list) =
            let info = ProcessStartInfo(dotnetHost ())
            info.WorkingDirectory <- root
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
            info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"

            for arg in cliAssembly () :: args do
                info.ArgumentList.Add arg

            Process.Start info

        // The daemon this test owns, in the foreground: its stderr is its log.
        use daemon = start [ "start" ]
        let daemonOut = daemon.StandardOutput.ReadToEndAsync()
        let daemonErr = daemon.StandardError.ReadToEndAsync()
        let daemonPidFile = Path.Combine(root, ".fshw", "daemon.pid")
        let mutable client: Process option = None

        try
            Assert.True(
                waitUntilTrue (fun () -> daemon.HasExited || readPid daemonPidFile = Some daemon.Id) 60000,
                "positive control: the daemon must start and record its pid"
            )

            Assert.False(daemon.HasExited, "positive control: the daemon must still be running")

            // A check makes the daemon run its test host. The client is only the trigger:
            // it is ended before the signal, so nothing but the daemon owns the host.
            let checkClient = start [ "check"; "--agent" ]
            client <- Some checkClient

            Assert.True(
                waitUntilTrue (fun () -> (readPid hostPid).IsSome && (readPid childPid).IsSome) 120000,
                "positive control: the daemon must start the fake test host"
            )

            let host = (readPid hostPid).Value
            let child = (readPid childPid).Value
            Assert.True(alive host && alive child, "positive control: the test host tree is alive before the signal")

            checkClient.Kill true
            checkClient.WaitForExit 10000 |> ignore
            Assert.True(alive host && alive child, "the test host must outlive the check client that triggered it")

            Assert.Equal(0, signal (daemon.Id, SIGTERM))
            Assert.True(daemon.WaitForExit 60000, "the daemon must exit after SIGTERM")
            daemon.WaitForExit()

            let log = daemonOut.Result + "\n" + daemonErr.Result

            let reaped = waitUntilTrue (fun () -> not (alive host) && not (alive child)) 10000

            let states = describePids [ ("test host", host); ("test host child", child) ]
            Assert.True(reaped, $"the daemon's test host tree must not outlive the daemon (%s{states}):\n%s{log}")

            let shutdownLine =
                log.Split '\n'
                |> Array.tryFind (fun line -> line.Contains "shutdown: signal=SIGTERM")

            Assert.True(shutdownLine.IsSome, $"the daemon must log its shutdown and the signal:\n%s{log}")
            Assert.Contains(string host, shutdownLine.Value)

            Assert.True(
                (daemon.ExitCode = 143),
                $"expected exit 143 (128 + SIGTERM), got %d{daemon.ExitCode}:\n%s{log}"
            )
        finally
            client
            |> Option.iter (fun c ->
                if not c.HasExited then
                    c.Kill true

                c.Dispose())

            if not daemon.HasExited then
                daemon.Kill true
                daemon.WaitForExit 10000 |> ignore

            // Only the processes this test's own fake host announced.
            [ readPid childPid; readPid hostPid ]
            |> List.choose id
            |> List.filter alive
            |> List.iter (fun pid -> signal (pid, SIGKILL) |> ignore))
