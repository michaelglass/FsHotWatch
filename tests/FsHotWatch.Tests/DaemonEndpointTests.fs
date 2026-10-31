module FsHotWatch.Tests.DaemonEndpointTests

open System
open System.IO
open System.IO.Pipes
open Xunit
open Swensen.Unquote
open FsHotWatch.Ipc
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers

// .NET resolves a bare pipe name to `$TMPDIR/CoreFxPipe_<name>` on Unix, so a client
// whose TMPDIR differs from the daemon's (an agent sandbox's `/tmp` against a shell's
// `/var/folders/…`) looks for a socket that does not exist and reports a live daemon as
// unreachable. The daemon records the socket it listens on; a client connects to that.

let private shortId () =
    Guid.NewGuid().ToString("N").Substring(0, 8)

/// A listening socket at a rooted path outside this process's TMPDIR — a daemon that
/// resolved its pipe under a different TMPDIR than this client's.
let private listenElsewhere () =
    let socket = Path.Combine("/tmp", $"fshw-ep-%s{shortId ()}")

    let server =
        new NamedPipeServerStream(socket, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)

    socket, server

let private writePid (repoRoot: string) (pid: int) =
    Directory.CreateDirectory(Path.Combine(repoRoot, ".fshw")) |> ignore
    File.WriteAllText(Path.Combine(repoRoot, ".fshw", "daemon.pid"), string pid)

[<Fact(Timeout = 15000)>]
let ``a client whose TMPDIR resolves a different socket reaches the daemon at the socket it recorded`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "endpoint-recorded" (fun repoRoot ->
            let socket, server = listenElsewhere ()
            use _server = server
            writePid repoRoot Environment.ProcessId
            // The daemon's own pipe name IS the rooted socket here: what it records is
            // where it listens.
            DaemonEndpoint.record repoRoot socket

            let clientName = $"fshw-ep-client-%s{shortId ()}"
            // The control: the name this client computes reaches nothing.
            test <@ not (IpcClient.isRunning clientName) @>

            let resolved =
                DaemonEndpoint.resolve FsHotWatch.RepositoryHost.processAlive repoRoot clientName

            test <@ resolved = socket @>
            test <@ IpcClient.isRunning resolved @>)

[<Fact(Timeout = 15000)>]
let ``the recorded socket path is absolute, so it does not depend on the reader's TMPDIR`` () =
    if not (OperatingSystem.IsWindows()) then
        let socket = DaemonEndpoint.socketPath "fshw-0123456789ab"
        test <@ Path.IsPathRooted socket @>
        test <@ socket = Path.Combine(Path.GetTempPath(), "CoreFxPipe_fshw-0123456789ab") @>
        // A rooted name is already a socket path; .NET uses it as is.
        test <@ DaemonEndpoint.socketPath "/tmp/x" = "/tmp/x" @>

[<Fact(Timeout = 15000)>]
let ``a recorded socket is ignored once the daemon that recorded it is gone`` () =
    withTempDir "endpoint-dead" (fun repoRoot ->
        writePid repoRoot Int32.MaxValue
        DaemonEndpoint.record repoRoot "/tmp/fshw-ep-gone"

        test <@ DaemonEndpoint.resolve (fun _ -> false) repoRoot "fshw-computed" = "fshw-computed" @>)

[<Fact(Timeout = 15000)>]
let ``no recorded socket resolves to the computed pipe name`` () =
    withTempDir "endpoint-none" (fun repoRoot ->
        writePid repoRoot Environment.ProcessId
        test <@ DaemonEndpoint.resolve (fun _ -> true) repoRoot "fshw-computed" = "fshw-computed" @>)

[<Fact(Timeout = 15000)>]
let ``clear removes the record`` () =
    withTempDir "endpoint-clear" (fun repoRoot ->
        writePid repoRoot Environment.ProcessId
        DaemonEndpoint.record repoRoot "/tmp/fshw-ep-clear"
        DaemonEndpoint.clear repoRoot
        test <@ not (File.Exists(DaemonEndpoint.path repoRoot)) @>
        test <@ DaemonEndpoint.resolve (fun _ -> true) repoRoot "fshw-computed" = "fshw-computed" @>)

[<Fact(Timeout = 15000)>]
let ``a record that is not an absolute path resolves to the computed pipe name`` () =
    // Windows records the bare pipe name: it is not a socket path, so it is not used.
    withTempDir "endpoint-relative" (fun repoRoot ->
        writePid repoRoot Environment.ProcessId
        File.WriteAllText(DaemonEndpoint.path repoRoot, "fshw-bare-name")
        test <@ DaemonEndpoint.resolve (fun _ -> true) repoRoot "fshw-computed" = "fshw-computed" @>)

[<Fact(Timeout = 15000)>]
let ``record and clear never throw when the state directory cannot hold the record`` () =
    withTempDir "endpoint-unwritable" (fun repoRoot ->
        // `.fshw` is a FILE, so nothing can be written under it.
        File.WriteAllText(Path.Combine(repoRoot, ".fshw"), "")
        DaemonEndpoint.record repoRoot "fshw-x"
        test <@ not (Directory.Exists(Path.Combine(repoRoot, ".fshw"))) @>)

[<Fact(Timeout = 15000)>]
let ``clear never throws when the record cannot be deleted`` () =
    withTempDir "endpoint-undeletable" (fun repoRoot ->
        // A directory where the record belongs: File.Delete refuses it.
        Directory.CreateDirectory(DaemonEndpoint.path repoRoot) |> ignore
        DaemonEndpoint.clear repoRoot
        test <@ Directory.Exists(DaemonEndpoint.path repoRoot) @>)

[<Fact(Timeout = 15000)>]
let ``a daemon pid that does not parse leaves the record unused`` () =
    withTempDir "endpoint-bad-pid" (fun repoRoot ->
        Directory.CreateDirectory(Path.Combine(repoRoot, ".fshw")) |> ignore
        File.WriteAllText(Path.Combine(repoRoot, ".fshw", "daemon.pid"), "not-a-pid")
        DaemonEndpoint.record repoRoot "/tmp/fshw-ep-badpid"
        test <@ DaemonEndpoint.resolve (fun _ -> true) repoRoot "fshw-computed" = "fshw-computed" @>)

/// Every IPC call a client makes, with the pipe name it was handed.
let private recordingIpc (seen: Collections.Generic.List<string>) : Program.IpcOps =
    let reply pipe =
        seen.Add pipe
        async { return "" }

    { Shutdown = reply
      Scan = fun pipe _ -> reply pipe
      ScanStatus = reply
      GetStatus = reply
      GetPluginStatus = fun pipe _ -> reply pipe
      RunCommand = fun pipe _ _ -> reply pipe
      RunCommandFor = fun pipe _ _ _ -> reply pipe
      GetDiagnostics = fun pipe _ -> reply pipe
      WaitForScan = fun pipe _ -> reply pipe
      WaitForComplete = fun pipe _ -> reply pipe
      WaitForRest = fun pipe _ -> reply pipe
      TriggerBuild = reply
      FormatAll = reply
      RerunPlugin = fun pipe _ -> reply pipe
      Invalidate = reply
      IsRunning =
        fun pipe ->
            seen.Add pipe
            true
      LaunchDaemon = fun _ _ _ -> seen.Add "launch" }

[<Fact(Timeout = 15000)>]
let ``every worktree IPC call goes to the resolved socket, and launching is left alone`` () =
    let seen = Collections.Generic.List<string>()

    let ipc =
        Program.resolvingIpcOps (fun pipe -> "resolved:" + pipe) (recordingIpc seen)

    [ ipc.Shutdown "p"
      ipc.Scan "p" None
      ipc.ScanStatus "p"
      ipc.GetStatus "p"
      ipc.GetPluginStatus "p" "lint"
      ipc.RunCommand "p" "c" "{}"
      ipc.RunCommandFor "p" "id" "c" "{}"
      ipc.GetDiagnostics "p" ""
      ipc.WaitForScan "p" 0L
      ipc.WaitForComplete "p" 0
      ipc.WaitForRest "p" 0
      ipc.TriggerBuild "p"
      ipc.FormatAll "p"
      ipc.RerunPlugin "p" "lint"
      ipc.Invalidate "p" ]
    |> Async.Sequential
    |> Async.RunSynchronously
    |> ignore

    test <@ ipc.IsRunning "p" @>
    ipc.LaunchDaemon "root" "" "log"

    test <@ seen.Count = 17 @>
    test <@ seen |> Seq.take 16 |> Seq.forall ((=) "resolved:p") @>
    test <@ seen[16] = "launch" @>

[<Fact(Timeout = 15000)>]
let ``the worktree IPC re-reads the record on every call, so it follows a daemon that replaced the one it first found``
    ()
    =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "endpoint-follows" (fun repoRoot ->
            writePid repoRoot Environment.ProcessId
            let ipc = Program.worktreeIpcOps repoRoot
            let clientName = $"fshw-ep-client-%s{shortId ()}"

            let first, firstServer = listenElsewhere ()
            DaemonEndpoint.record repoRoot first
            test <@ ipc.IsRunning clientName @>
            firstServer.Dispose()
            // The old daemon is gone and its replacement records a different socket.
            let second, secondServer = listenElsewhere ()
            use _second = secondServer
            DaemonEndpoint.record repoRoot second

            test <@ ipc.IsRunning clientName @>)
