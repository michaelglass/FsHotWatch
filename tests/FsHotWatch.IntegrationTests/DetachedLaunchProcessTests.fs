/// The detached launch through its REAL helper process: a copy of the CLI that calls
/// `setsid` and execs the launch shell.
///
/// Integration suite, not FsHotWatch.Tests (AUTOMATION-1651): the helper runs the
/// coverage-instrumented CLI, so lines only it reaches (`DetachedLaunch.runHelper`, the
/// entry point's routing) were graded as unit coverage that came and went with the
/// helper's timing. The unit suite covers them in process instead.
module FsHotWatch.Tests.DetachedLaunchProcessTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``detached launch reports original failed command`` () =
    withTempDir "detached-launch-exit" (fun root ->
        let error =
            Assert.Throws<InvalidOperationException>(fun () -> DetachedLaunch.launch root "exit 7")

        Assert.Equal("Detached daemon launch helper exited 7 launching: exit 7\nHelper stderr: empty.", error.Message))

[<Fact(Timeout = 60000)>]
let ``detached launch bounds and reaps a stuck helper`` () =
    withTempDir "detached-launch-stuck" (fun root ->
        let receipt = Path.Combine(root, "helper.pid")
        // `exec` keeps the announced pid the helper's own for the whole of its life.
        let command = $"echo $$ > %s{DetachedLaunch.shellQuote receipt}; exec /bin/sleep 30"

        try
            let error =
                Assert.Throws<TimeoutException>(fun () ->
                    DetachedLaunch.launchWithin ignore (TimeSpan.FromSeconds 10.0) root command)

            Assert.Contains("exceeded 10 seconds and was killed", error.Message)
            Assert.True(File.Exists receipt, "positive control: the helper must have become the shell")
            let pid = Int32.Parse(File.ReadAllText(receipt).Trim())

            let alive =
                try
                    use proc = Process.GetProcessById pid
                    not proc.HasExited
                with :? ArgumentException ->
                    false

            Assert.False(alive, $"the stuck helper (pid %d{pid}) must be reaped")
        finally
            // Only the pid this test's own helper announced, and only if it survived.
            if File.Exists receipt then
                try
                    use proc = Process.GetProcessById(Int32.Parse(File.ReadAllText(receipt).Trim()))

                    if not proc.HasExited then
                        proc.Kill()
                with :? ArgumentException ->
                    ())

// Positive control for the abort diagnostics: a real helper that writes to stderr and
// then dies of SIGABRT, through the real pipe and the real exit status.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``detached launch failure names the signal and carries the helper's stderr`` () =
    withTempDir "detached-launch-abort" (fun root ->
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                DetachedLaunch.launch root "echo 'about to abort' >&2; kill -ABRT $$")

        Assert.StartsWith("Detached daemon launch helper exited 134 (killed by signal 6, SIGABRT)", error.Message)
        Assert.Contains("launching: echo 'about to abort' >&2; kill -ABRT $$", error.Message)
        Assert.Contains("Helper stderr:\nabout to abort", error.Message))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a successful helper hands its stderr to the caller's report, and a silent one hands nothing`` () =
    withTempDir "detached-launch-report" (fun root ->
        let messages = ResizeArray<string>()
        DetachedLaunch.launchWithin messages.Add (TimeSpan.FromSeconds 20.0) root "echo 'a note' >&2; exit 0"
        Assert.Equal<string list>([ "a note\n" ], List.ofSeq messages)

        messages.Clear()
        DetachedLaunch.launchWithin messages.Add (TimeSpan.FromSeconds 20.0) root "exit 0"
        Assert.Empty(messages))

// The backgrounded sleep inherits the helper's stderr and holds it until the test kills it,
// so the stream outlives the caller's 5 s drain whatever the box's load. The helper's own
// bound is `HangWait`: it starts a dotnet host, which a loaded box took more than the 2 s
// this test once gave it (AUTOMATION-1461), and only a hang reaches it. Under the old 2 s
// bound the 4 s sleep was also the drain's competitor, so load decided the outcome.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a helper whose stderr outlives it is reported as still open, not waited on`` () =
    withTempDir "detached-launch-held-stderr" (fun root ->
        let command = "/bin/sleep 600 & echo $! > holder.pid; exit 3"

        try
            let error =
                Assert.Throws<InvalidOperationException>(fun () ->
                    DetachedLaunch.launchWithin ignore HangWait root command)

            Assert.Equal(
                $"Detached daemon launch helper exited 3 launching: %s{command}\nHelper stderr: still open when the wait for it ended.",
                error.Message
            )
        finally
            let pidFile = Path.Combine(root, "holder.pid")

            if File.Exists pidFile then
                try
                    // The sleep this test's own command started, by the pid it wrote.
                    (Process.GetProcessById(int (File.ReadAllText(pidFile).Trim()))).Kill()
                with
                | :? ArgumentException
                | :? InvalidOperationException -> ())
