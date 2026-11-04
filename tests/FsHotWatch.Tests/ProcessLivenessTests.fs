/// One process-alive probe, and each caller's stated reading of what it cannot tell
/// (AUTOMATION-1456).
///
/// fshw asked "is this pid alive?" three ways: the repository host and the run claims
/// through `Process.GetProcessById`, the CLI's daemon checks through `kill(pid, 0)`. Their
/// answers differed by OS for a pid that is not positive (`GetProcessById -1` finds a
/// process on macOS and throws on Linux), and each read an undecidable answer its own
/// way without saying so. `ProcessHelper.isProcessAlive` is now the one probe, and each
/// caller maps its `Liveness` through a policy written down next to it.
module FsHotWatch.Tests.ProcessLivenessTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

[<Literal>]
let private ESRCH = 3

[<Literal>]
let private EPERM = 1

// --- the probe: what `kill(pid, 0)` establishes ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a probe that succeeds finds the process alive`` () =
    test <@ livenessFrom (fun _ -> 0) (fun () -> 0) 4242 = Liveness.Alive @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``ESRCH is the one answer that proves a process gone`` () =
    test <@ livenessFrom (fun _ -> -1) (fun () -> ESRCH) 4242 = Liveness.Gone @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``EPERM finds a process alive that is not ours to signal`` () =
    test <@ livenessFrom (fun _ -> -1) (fun () -> EPERM) 4242 = Liveness.Alive @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``any other failure is unknown, and says which`` () =
    test <@ livenessFrom (fun _ -> -1) (fun () -> 22) 4242 = Liveness.Unknown "kill(4242, 0) failed with errno 22" @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(0)>]
[<InlineData(-1)>]
let ``a pid that is not positive is never probed and is unknown`` (pid: int) =
    // `kill` reads 0 as this process group and -1 as every process it may signal: neither
    // is one process, so no answer about it is an answer about a recorded pid.
    match livenessFrom (fun _ -> failwith "should not be probed") (fun () -> 0) pid with
    | Liveness.Unknown reason -> test <@ reason.Contains $"pid %d{pid}" @>
    | other -> failwith $"expected Unknown, got %A{other}"

// --- each caller's policy for what the probe cannot tell ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the repository host routes only to a host proven alive`` () =
    test <@ FsHotWatch.RepositoryHost.isLive Liveness.Alive @>
    test <@ not (FsHotWatch.RepositoryHost.isLive Liveness.Gone) @>
    test <@ not (FsHotWatch.RepositoryHost.isLive (Liveness.Unknown "x")) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a run claim is released only when its process is proven gone`` () =
    test <@ FsHotWatch.RunClaim.isRunning Liveness.Alive @>
    test <@ not (FsHotWatch.RunClaim.isRunning Liveness.Gone) @>
    test <@ FsHotWatch.RunClaim.isRunning (Liveness.Unknown "x") @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the CLI declares a daemon dead only when its process is proven gone`` () =
    test <@ FsHotWatch.Cli.Program.daemonAlive Liveness.Alive @>
    test <@ not (FsHotWatch.Cli.Program.daemonAlive Liveness.Gone) @>
    test <@ FsHotWatch.Cli.Program.daemonAlive (Liveness.Unknown "x") @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(0)>]
[<InlineData(-1)>]
let ``every caller reads a pid that is not positive by its own policy, on every OS`` (pid: int) =
    // `GetProcessById -1` found a process on macOS and threw on Linux, so a run claim and
    // the CLI's pidfile check gave different answers for this pid on the two.
    withTempDir "liveness-nonpositive" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, ".fshw")) |> ignore
        File.WriteAllText(Path.Combine(root, ".fshw", "daemon.pid"), string pid)

        test <@ not (FsHotWatch.RepositoryHost.processAlive pid) @>
        test <@ FsHotWatch.RunClaim.processAlive pid @>
        test <@ FsHotWatch.Cli.Program.daemonProcessAliveWith FsHotWatch.Cli.Program.defaultFileOps root @>)

// --- a zombie: exited, not yet reaped ---

/// What `ps` says of `pid` in column `field` (`stat`, `comm`), or "" once it is gone.
let private psField (field: string) (pid: int) =
    let ps =
        ProcessStartInfo("/bin/ps", $"-o %s{field}= -p %d{pid}", UseShellExecute = false, RedirectStandardOutput = true)

    use p = Process.Start ps
    let text = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    text.Trim()

/// A shell that starts a child in the background and then becomes `sleep`, which never
/// reaps: the child, once it exits, stays a zombie until the shell is killed and init
/// reaps the orphan. The child exits only when the test creates `go`, after the shell has
/// become `sleep`. A child that exited first could be reaped by the shell before its exec
/// (dash does), which made this flaky on Linux. The zombie's pid.
let private withZombie (body: int -> unit) =
    withTempDir "liveness-zombie" (fun dir ->
        let psi =
            ProcessStartInfo("/bin/sh", UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = dir)

        psi.ArgumentList.Add "-c"

        psi.ArgumentList.Add "/bin/sh -c 'while [ ! -f go ]; do /bin/sleep 0.05; done' & echo $!; exec /bin/sleep 600"

        use shell = Process.Start psi

        try
            let zombie = int (shell.StandardOutput.ReadLine().Trim())

            waitUntilOr
                "the shell to become sleep"
                (fun () -> Path.GetFileName(psField "comm" shell.Id) = "sleep")
                HangWaitMs

            File.WriteAllText(Path.Combine(dir, "go"), "")

            waitUntilOr
                "the background child to exit and become a zombie"
                (fun () -> (psField "stat" zombie).StartsWith "Z")
                HangWaitMs

            body zombie
        finally
            shell.Kill()
            shell.WaitForExit())

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a zombie is alive to the probe and to every caller until it is reaped`` () =
    // It has exited, but it still holds its pid, and `kill(pid, 0)` finds it. A teardown
    // names it as a survivor (a parent that does not reap is itself a survivor), and the
    // callers wait it out rather than call it gone: the daemon's and the host's parent is
    // init after the detached launch, which reaps at once.
    withZombie (fun zombie ->
        test <@ isProcessAlive zombie = Liveness.Alive @>
        test <@ FsHotWatch.RepositoryHost.processAlive zombie @>
        test <@ FsHotWatch.RunClaim.processAlive zombie @>
        test <@ FsHotWatch.Cli.Program.daemonPidAlive zombie @>)
