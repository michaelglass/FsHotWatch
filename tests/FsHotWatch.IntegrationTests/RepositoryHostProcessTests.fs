/// The repository host through REAL CLI processes: `fshw scan`, `stop`, a plugin command,
/// each started as its own process against the test bin's CLI, which starts the host as a
/// detached process of its own.
///
/// Integration suite, not FsHotWatch.Tests (AUTOMATION-1651): every process these tests
/// start runs the coverage-instrumented CLI, so what those processes executed was graded
/// as the unit suite's coverage. How far a child got, and whether its hits reached the
/// report at all, depends on timing the test does not control: Program.fs graded 753 of
/// 957 branches on one run of a tree and 697 on another, ThreadSuspendInjection.fs 96.4%
/// and 85.5%, with no test failing. Coverage must be deterministic, so the unit suite
/// covers what these processes run in process instead.
module FsHotWatch.Tests.RepositoryHostProcessTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open CommandTree
open FsHotWatch.Cli
open FsHotWatch.Cli.Program
open FsHotWatch.RepositoryIdentity
open FsHotWatch.Tests.TestHelpers

/// A jj primary with one F# project, configured for the smallest possible session.
let private withRepository (body: string -> string -> unit) =
    withTempDir "mode-host" (fun dir ->
        let root =
            match canonicalize dir with
            | Ok c -> Path.Combine(c.Value, "r")
            | Error e -> failwith (IdentityError.describe e)

        Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
        let lib = Path.Combine(root, "src", "Lib")
        Directory.CreateDirectory lib |> ignore

        File.WriteAllText(
            Path.Combine(lib, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""
        )

        File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\nlet answer = 42\n")

        let config =
            """{ "build": false, "format": false, "lint": false, "repositoryHost": true }"""

        File.WriteAllText(Path.Combine(root, ".fshw.json"), config)
        body root (Path.Combine(Path.GetDirectoryName root, "state")))

/// The built CLI, run in `root` as an opted-in shell would run it, under the `dotnet`
/// host of the runtime this test runs on. Its apphost would need a runtime it can
/// find on its own, which a test host started by `dotnet exec` does not guarantee.
/// Returns its exit code and everything it printed, stdout and stderr interleaved.
let private runCli (stateHome: string) (root: string) (args: string list) : int * string =
    let cli = typeof<HostLink>.Assembly.Location

    let host =
        DetachedLaunch.helperHost
            Environment.ProcessPath
            (Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
            File.Exists

    let psi = ProcessStartInfo(host)
    psi.ArgumentList.Add cli

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- root
    psi.Environment["FSHW_STATE_HOME"] <- stateHome
    psi.Environment[RepositoryHostMode.EnvVar] <- "1"
    psi.UseShellExecute <- false
    psi.RedirectStandardError <- true
    psi.RedirectStandardOutput <- true
    let output = Collections.Concurrent.ConcurrentQueue<string>()
    use p = new Process(StartInfo = psi)

    p.OutputDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            output.Enqueue e.Data)

    p.ErrorDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            output.Enqueue e.Data)

    p.Start() |> ignore
    p.BeginErrorReadLine()
    p.BeginOutputReadLine()

    if not (p.WaitForExit 180000) then
        p.Kill true
        failwith $"fshw %A{args} did not finish:\n%s{String.concat Environment.NewLine output}"

    // The parameterless wait returns once both redirected streams have drained.
    p.WaitForExit()
    p.ExitCode, String.concat Environment.NewLine output

/// The log and event log of the repository host serving `root`'s repository under
/// `stateHome`. The failure a host explains is reported from inside a scratch tree that
/// is deleted with the test, so a failure message that only names the log names a file
/// nobody can read.
let private hostLogs (stateHome: string) (root: string) : string =
    match resolveWorktree root with
    | Error e -> $"(no repository identity: %s{IdentityError.describe e})"
    | Ok worktree ->
        let control = repositoryControlPaths stateHome worktree.Repository

        [ control.HostLog; HostLifecycle.eventLogPath control ]
        |> List.map (fun f ->
            let text =
                if File.Exists f then
                    try
                        File.ReadAllText f
                    with ex ->
                        $"(unreadable: %s{ex.Message})"
                else
                    "(absent)"

            $"--- %s{f}\n%s{text}")
        |> String.concat "\n"

/// `runCli`, failing with everything the CLI printed, and the repository host's logs,
/// unless it exits 0.
let private runCliOk (stateHome: string) (root: string) (args: string list) : unit =
    let exitCode, output = runCli stateHome root args

    if exitCode <> 0 then
        Assert.Fail $"fshw %A{args} exited %d{exitCode}:\n%s{output}\n%s{hostLogs stateHome root}"

/// Kill every host this test may have started for `root`: the pids it saw, and the one
/// the repository's pid file names now, which a command that failed part-way leaves
/// unseen. A pid file can outlive its host and its pid be reused, so only a dotnet
/// process started since `since` is taken for the host. Fails if one survives; returns
/// the pids it killed.
let private reapHosts (since: DateTime) (stateHome: string) (root: string) (seen: int seq) =
    let named =
        match resolveWorktree root with
        | Ok w ->
            let pidFile = (repositoryControlPaths stateHome w.Repository).PidFile

            match
                Int32.TryParse(
                    try
                        File.ReadAllText(pidFile).Trim()
                    with _ ->
                        ""
                )
            with
            | true, pid -> [ pid ]
            | _ -> []
        | Error _ -> []

    let ours (pid: int) =
        try
            use p = Process.GetProcessById pid

            not p.HasExited
            && p.ProcessName.StartsWith "dotnet"
            && p.StartTime >= since.ToLocalTime()
        with _ ->
            false

    let candidates =
        Seq.append seen named |> Seq.distinct |> Seq.filter ours |> List.ofSeq

    for pid in candidates do
        try
            use p = Process.GetProcessById pid
            p.Kill true
            p.WaitForExit 30000 |> ignore
        with _ ->
            // Gone between the look and the kill.
            ()

    let survivors = candidates |> List.filter ours
    Assert.True(List.isEmpty survivors, $"host process(es) %A{survivors} outlived the test")
    candidates

[<Fact(Timeout = 600000)>]
let ``a host killed outright leaves the worktree to the next command, which starts a new host`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root stateHome ->
            let worktree =
                match resolveWorktree root with
                | Ok w -> w
                | Error e -> failwith (IdentityError.describe e)

            let control = repositoryControlPaths stateHome worktree.Repository

            let hostPid () =
                int (File.ReadAllText(control.PidFile).Trim())

            let seen = Collections.Generic.HashSet<int>()
            let since = DateTime.UtcNow.AddSeconds -1.0

            try
                runCliOk stateHome root [ "scan" ]
                let first = hostPid ()
                seen.Add first |> ignore

                let firstSession =
                    (RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root).Value

                test <@ firstSession.HostPid = first @>

                // SIGKILL: no shutdown path runs, nothing is cleaned up.
                use killed = Process.GetProcessById first
                killed.Kill()
                test <@ killed.WaitForExit HangWaitMs @>

                // What the dead host left behind says so.
                test
                    <@
                        RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root
                        |> Option.isNone
                    @>

                runCliOk stateHome root [ "scan" ]
                let second = hostPid ()
                seen.Add second |> ignore
                test <@ second <> first @>

                let secondSession =
                    RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root

                test
                    <@
                        secondSession
                        |> Option.exists (fun r -> r.HostPid = second && r.Session <> firstSession.Session)
                    @>

                // A clean stop hands the worktree back.
                runCliOk stateHome root [ "stop"; "--repository" ]
                test <@ waitUntilTrue (fun () -> not (RepositoryHost.processAlive second)) 30000 @>
                use relock = (RepositoryHost.tryLockWorktree root).Value
                ()
            finally
                // Not only the pids seen: the incident this guards against was a first
                // `scan` that failed after launching its host, before any pid was seen.
                reapHosts since stateHome root seen |> ignore)

[<Fact(Timeout = 600000)>]
let ``a host the test never saw is reaped all the same`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root stateHome ->
            let since = DateTime.UtcNow.AddSeconds -1.0

            let reaped = ref []

            try
                // The command succeeds here; in the incident it failed after launching
                // the host. Either way the test holds no pid for it.
                runCliOk stateHome root [ "scan" ]
            finally
                reaped.Value <- reapHosts since stateHome root []

            test <@ reaped.Value.Length = 1 @>)

[<Fact(Timeout = 600000)>]
let ``a scan whose host stops answering during the attach starts this worktree's own daemon`` () =
    // AUTOMATION-1333. The fallback said "using this worktree's own daemon", and then
    // `scan` sent its call to a pipe nothing had started: it timed out connecting
    // ("Could not connect to daemon: The operation has timed out."), where every other
    // command that falls back brings that daemon up first.
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root stateHome ->
            let worktree =
                match resolveWorktree root with
                | Ok w -> w
                | Error e -> failwith (IdentityError.describe e)

            let control = repositoryControlPaths stateHome worktree.Repository

            // A host that reads every attach and hangs up without answering.
            let opener: Ipc.IpcServer.ConnectionOpener =
                fun pipe _ ->
                    async {
                        do!
                            RepositoryIpc.readFrame pipe CancellationToken.None
                            |> Async.AwaitTask
                            |> Async.Ignore

                        return None
                    }

            use cts = new CancellationTokenSource()

            let server =
                Async.StartImmediateAsTask(
                    Ipc.IpcServer.serveConnections (TimeSpan.FromSeconds 5.0) control.Endpoint opener cts
                )

            let since = DateTime.UtcNow.AddSeconds -1.0

            try
                test <@ Ipc.IpcServer.acceptsConnection control.Endpoint @>

                let exitCode, output = runCli stateHome root [ "scan" ]

                Assert.True(
                    (exitCode = 0
                     && output.Contains "using this worktree's own daemon"
                     && output.Contains "stopped answering during attach"),
                    $"fshw scan exited %d{exitCode}:\n%s{output}"
                )
            finally
                cts.Cancel()
                server.Wait(TimeSpan.FromSeconds 10.0) |> ignore

                // The worktree's own daemon, which the scan started.
                let pidFile = Path.Combine(root, ".fshw", "daemon.pid")

                match
                    Int32.TryParse(
                        try
                            File.ReadAllText(pidFile).Trim()
                        with _ ->
                            ""
                    )
                with
                | true, pid -> reapHosts since stateHome root [ pid ] |> ignore
                | _ -> ())

// ---------------------------------------------------------------------------
// Plugin passthrough
// ---------------------------------------------------------------------------

/// Configure `worktree` for the smallest session, with one plugin command of its own:
/// `<name>-status`, which a file command named `name` registers.
let private withPluginCommand (worktree: string) (name: string) =
    File.WriteAllText(
        Path.Combine(worktree, ".fshw.json"),
        $"""{{ "build": false, "format": false, "lint": false, "repositoryHost": true,
              "fileCommands": [ {{ "name": "%s{name}", "pattern": "*.probe", "command": "true" }} ] }}"""
    )

[<Fact(Timeout = 600000)>]
let ``a plugin command reaches the session of the worktree it is run in, and no other`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root stateHome ->
            // A second worktree of the same repository, with its own project and a plugin
            // command its sibling does not have.
            let sibling = Path.Combine(Path.GetDirectoryName root, "b")
            let siblingJj = Path.Combine(sibling, ".jj")
            Directory.CreateDirectory siblingJj |> ignore

            File.WriteAllText(
                Path.Combine(siblingJj, "repo"),
                Path.GetRelativePath(siblingJj, Path.Combine(root, ".jj", "repo"))
            )

            let siblingLib = Path.Combine(sibling, "src", "Lib")
            Directory.CreateDirectory siblingLib |> ignore

            for file in [ "Lib.fsproj"; "Lib.fs" ] do
                File.Copy(Path.Combine(root, "src", "Lib", file), Path.Combine(siblingLib, file))

            withPluginCommand root "alpha"
            withPluginCommand sibling "beta"
            let since = DateTime.UtcNow.AddSeconds -1.0

            try
                runCliOk stateHome root [ "scan" ]
                runCliOk stateHome sibling [ "scan" ]

                let record worktree =
                    match RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive worktree with
                    | Some r -> r
                    | None -> failwith $"no host session serves %s{worktree}:\n%s{hostLogs stateHome worktree}"

                // One host serves both worktrees, each as its own session.
                test <@ (record root).HostPid = (record sibling).HostPid @>
                test <@ (record root).Session <> (record sibling).Session @>

                // Each worktree's command is recognised and answered by its own session: its file
                // command has not run (no `.probe` file changed), which is its status's answer,
                // no verdict (3). An unrecognised command would exit 1 naming it.
                for worktree, command in [ root, "alpha-status"; sibling, "beta-status" ] do
                    let exitCode, output = runCli stateHome worktree [ command ]

                    Assert.True(
                        (exitCode = 3 && output.Contains "the command has not run yet"),
                        $"fshw %s{command} exited %d{exitCode}:\n%s{output}"
                    )

                // The sibling's session has no `alpha` command: the command is not served
                // by another worktree's session, it fails as an unknown command.
                let unknown =
                    (CommandTree.renderParseError commandTree (UnknownCommand("alpha-status", [||], [])) cliName)
                        .Split('\n')[0]

                let exitCode, output = runCli stateHome sibling [ "alpha-status" ]
                Assert.True((exitCode = 1 && output.Contains unknown), $"exited %d{exitCode}:\n%s{output}")
            finally
                reapHosts since stateHome root [] |> ignore)
