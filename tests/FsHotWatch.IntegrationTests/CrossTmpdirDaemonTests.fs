/// A client and a daemon started under different TMPDIRs. On Unix .NET resolves a bare
/// pipe name to `$TMPDIR/CoreFxPipe_<name>` in the process doing the resolving, so a daemon
/// started under `env -i` (TMPDIR unset, so `/tmp/`) and a client started from a login shell
/// (`/var/folders/…/T/`) compute the same name and look in different directories. The
/// daemon records the socket it listens on in `.fshw/daemon.endpoint`; every client
/// connection must go through that record, including the probes a client makes while it
/// restarts the daemon.
module FsHotWatch.Tests.CrossTmpdirDaemonTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FsHotWatch.Cli
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

let private cliAssembly () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let assembly =
        Path.Combine(root, "src", "FsHotWatch.Cli", "bin", configuration, "net10.0", "FsHotWatch.Cli.dll")

    Assert.True(File.Exists assembly, $"build the real CLI before running this integration test: {assembly}")
    assembly

/// A fresh, short temp directory under `/tmp`: a Unix socket path is capped near 104
/// bytes, so the TMPDIRs under test must leave room for `CoreFxPipe_fshw-<12 hex>`.
let private freshTmpdir (tag: string) =
    let id = Guid.NewGuid().ToString("N").Substring(0, 8)
    let dir = Path.Combine("/tmp", $"fshw-%s{tag}-%s{id}")
    Directory.CreateDirectory dir |> ignore
    dir + "/"

let private startCli (root: string) (tmpdir: string) (arguments: string list) =
    let info = ProcessStartInfo("dotnet")
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.Environment["TMPDIR"] <- tmpdir
    info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
    info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"
    cliAssembly () :: arguments |> List.iter info.ArgumentList.Add
    let child = Process.Start info
    child, child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()

/// Run one CLI verb to completion under `tmpdir`: its exit code and all its output.
let private runCli (root: string) (tmpdir: string) (arguments: string list) =
    let child, stdout, stderr = startCli root tmpdir arguments
    use child = child
    // A real `check` restarts a real daemon: a hang cap, not a claim about speed.
    let exited = child.WaitForExit 300_000

    if not exited then
        child.Kill(entireProcessTree = true)

    Assert.True(exited, $"fshw %A{arguments} did not exit")
    child.ExitCode, stdout.Result + "\n" + stderr.Result

/// A one-project repository whose daemon runs no build, lint or format.
let private prepare (root: string) =
    let src = Path.Combine(root, "src", "Stub")
    Directory.CreateDirectory src |> ignore

    File.WriteAllText(
        Path.Combine(src, "Stub.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>
"""
    )

    File.WriteAllText(Path.Combine(src, "Lib.fs"), "module Lib\nlet x = 1\n")
    File.WriteAllText(Path.Combine(root, ".fshw.json"), """{ "build": false, "format": false, "lint": false }""")
    // A checkout root is what the CLI looks for; `git init` alone makes one.
    let git = ProcessStartInfo("git", "init --quiet")
    git.WorkingDirectory <- root
    git.UseShellExecute <- false
    use init = Process.Start git
    Assert.True(init.WaitForExit HangWaitMs, "git init did not exit")
    Assert.Equal(0, init.ExitCode)

let private pidOf (root: string) =
    let file = Path.Combine(root, ".fshw", "daemon.pid")

    if File.Exists file then
        match Int32.TryParse(File.ReadAllText(file).Trim()) with
        | true, pid -> Some pid
        | _ -> None
    else
        None

/// Kill whichever daemon `.fshw/daemon.pid` names, if it is still alive.
let private reapRecordedDaemon (root: string) =
    pidOf root
    |> Option.iter (fun pid ->
        try
            use daemon = Process.GetProcessById pid

            if not daemon.HasExited then
                daemon.Kill(entireProcessTree = true)
                daemon.WaitForExit HangWaitMs |> ignore
        with :? ArgumentException ->
            ())

/// Own a foreground daemon started under `daemonTmpdir` for `body`, and reap every daemon
/// left serving `root` afterwards (a restart in `body` replaces the owned one).
let private withDaemonUnder (root: string) (daemonTmpdir: string) (body: string -> 'a) =
    let daemon, _, _ = startCli root daemonTmpdir [ "start" ]
    use daemon = daemon
    let endpointFile = DaemonEndpoint.path root

    try
        let listening =
            waitUntilTrue
                (fun () ->
                    daemon.HasExited
                    || (File.Exists endpointFile
                        && IpcClient.isRunning (File.ReadAllText(endpointFile).Trim())))
                120_000

        Assert.True(listening && not daemon.HasExited, "the owned daemon did not open its IPC endpoint")
        let recorded = File.ReadAllText(endpointFile).Trim()
        // The control: the daemon really listens under ITS TMPDIR.
        Assert.StartsWith(daemonTmpdir, recorded)
        body recorded
    finally
        try
            if not daemon.HasExited then
                daemon.Kill(entireProcessTree = true)
                daemon.WaitForExit HangWaitMs |> ignore
        finally
            reapRecordedDaemon root

/// Run `body` with two fresh TMPDIRs, removed afterwards.
let private withTwoTmpdirs (first: string) (second: string) body =
    let a = freshTmpdir first
    let b = freshTmpdir second

    try
        body a b
    finally
        for dir in [ a; b ] do
            try
                Directory.Delete(dir, true)
            with _ ->
                ()

[<WallClock("a real daemon and a real client process, each with its own TMPDIR: the claim is about the real OS socket each resolves")>]
[<Fact(Timeout = 600000)>]
let ``a client under another TMPDIR reaches a running daemon, in both directions`` () =
    withTempDir "cross-tmpdir-status" (fun root ->
        prepare root

        withTwoTmpdirs "a" "b" (fun a b ->
            for daemonTmp, clientTmp in [ a, b; b, a ] do
                withDaemonUnder root daemonTmp (fun recorded ->
                    // The control: nothing listens where the client's TMPDIR alone would look.
                    Assert.False(IpcClient.isRunning (Path.Combine(clientTmp, Path.GetFileName recorded)))
                    let code, text = runCli root clientTmp [ "status" ]

                    Assert.True(
                        (code = 0),
                        $"status under %s{clientTmp} against a daemon under %s{daemonTmp}:\n%s{text}"
                    ))))

[<WallClock("a real client restarting a real daemon that listens under another TMPDIR: the claim is about the real OS socket each resolves")>]
[<Fact(Timeout = 600000)>]
let ``a client under another TMPDIR restarts a daemon from another binary and reaches the new one`` () =
    withTempDir "cross-tmpdir-restart" (fun root ->
        prepare root

        withTwoTmpdirs "d" "c" (fun daemonTmp clientTmp ->
            withDaemonUnder root daemonTmp (fun _ ->
                let replaced = pidOf root |> Option.get
                // The running daemon now reads as started from another fshw binary, so the
                // client must replace it. Not a `.fshw.json` edit: the daemon exits on its
                // own when it sees one, and whether it or the client gets there first
                // depends on the platform's file-event latency.
                File.WriteAllText(
                    FsHotWatch.DaemonIdentity.identityFilePath root,
                    "0.0.0-another-binary 0000000000000000\n"
                )

                let code, text = runCli root clientTmp [ "check" ]

                Assert.True(
                    (code = 0),
                    $"check under %s{clientTmp} after replacing a daemon under %s{daemonTmp}:\n%s{text}"
                )

                // The outcome, not the log: a new daemon serves, started by this client, so
                // it listens under the client's TMPDIR, and the one it replaced is gone.
                let current = pidOf root |> Option.get
                Assert.NotEqual(replaced, current)
                Assert.StartsWith(clientTmp, File.ReadAllText(DaemonEndpoint.path root).Trim())
                Assert.True(IpcClient.isRunning (File.ReadAllText(DaemonEndpoint.path root).Trim())))))
