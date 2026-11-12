module FsHotWatch.Tests.DetachedLaunchTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsHotWatch.Cli
open FsHotWatch.Tests.TestHelpers

[<Fact>]
let ``detached helper does not intercept ordinary or malformed CLI arguments`` () =
    let mutable ran = false

    let run _ =
        ran <- true
        DetachedLaunch.HelperFailure.ExecFailed 0

    for args in
        [ [||]
          [| "status" |]
          [| DetachedLaunch.HelperFlag |]
          [| "other"; "exit 0" |]
          [| "status"; DetachedLaunch.HelperFlag; "exit 0" |]
          [| DetachedLaunch.HelperFlag; "exit 0"; "extra" |]
          null ] do
        Assert.Equal(None, DetachedLaunch.tryRunWith run ignore args)

    Assert.False(ran, "no ordinary command line may reach the native helper")
    // The production router too — safe here only because none of these is the helper shape.
    Assert.Equal(None, DetachedLaunch.tryRun [| "status" |])

[<Fact>]
let ``detached helper runs exactly its command and exits nonzero with the reason when it returns`` () =
    let commands = ResizeArray<string>()
    let messages = ResizeArray<string>()

    let run command =
        commands.Add command
        DetachedLaunch.HelperFailure.ExecFailed 8

    let result =
        DetachedLaunch.tryRunWith run messages.Add [| DetachedLaunch.HelperFlag; "exit 0" |]

    Assert.Equal(Some 1, result)
    Assert.Equal<string>([ "exit 0" ], List.ofSeq commands)
    Assert.Equal<string>([ "Detached daemon launch: execv failed (errno 8)" ], List.ofSeq messages)

[<Fact>]
let ``detached session failure never attempts exec and retains native errno`` () =
    let mutable execAttempted = false

    let result =
        DetachedLaunch.runHelperWith
            (fun () -> -1)
            (fun _ ->
                execAttempted <- true
                -1)
            (fun () -> 13)
            "must not execute"

    Assert.Equal(DetachedLaunch.HelperFailure.SessionFailed 13, result)
    Assert.False(execAttempted)
    Assert.Equal("Detached daemon launch: setsid failed (errno 13)", DetachedLaunch.describeHelperFailure result)

[<Fact>]
let ``detached exec receives complete null terminated UTF8 shell arguments and reports failure`` () =
    let command = "printf 'héllo 🌍' \"$HOME\""
    let mutable inspected = false

    let inspect (path: string, argv: nativeint) =
        let read index =
            Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(argv, index * IntPtr.Size))

        Assert.Equal("/bin/sh", path)
        Assert.Equal("/bin/sh", read 0)
        Assert.Equal("-c", read 1)
        Assert.Equal(command, read 2)
        Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(argv, 3 * IntPtr.Size))
        inspected <- true
        -1

    let result =
        DetachedLaunch.runHelperWith (fun () -> 4242) inspect (fun () -> 2) command

    Assert.True(inspected)
    Assert.Equal(DetachedLaunch.HelperFailure.ExecFailed 2, result)
    Assert.Equal("Detached daemon launch: execv failed (errno 2)", DetachedLaunch.describeHelperFailure result)

[<Fact>]
let ``daemon launch line quotes paths and keeps the rendered tool prefix and extra args`` () =
    let line =
        DetachedLaunch.daemonShellCommand "/opt/it's here/dotnet" "\"/x/fshw.dll\" " "--verbose " "/logs/a b.log"

    Assert.Equal(
        "FSHW_DAEMON_LOG='/logs/a b.log' nohup '/opt/it'\\''s here/dotnet' \"/x/fshw.dll\" --verbose start >> '/logs/a b.log' 2>&1 < /dev/null &",
        line
    )

[<Fact>]
let ``helper host prefers the running dotnet muxer`` () =
    let runtime = "/root/shared/Microsoft.NETCore.App/10.0.9/"

    Assert.Equal("/usr/bin/dotnet", DetachedLaunch.helperHost "/usr/bin/dotnet" runtime (fun _ -> true))

[<Fact>]
let ``helper host falls back to the loaded runtime's own muxer for an apphost or test host`` () =
    let runtime = "/root/shared/Microsoft.NETCore.App/10.0.9/"

    Assert.Equal("/root/dotnet", DetachedLaunch.helperHost "/bin/FsHotWatch.Tests" runtime (fun _ -> true))
    Assert.Equal("/root/dotnet", DetachedLaunch.helperHost null runtime (fun _ -> true))

    Assert.Equal(
        "/root/dotnet",
        DetachedLaunch.helperHost "/usr/bin/dotnet" runtime (fun path -> path = "/root/dotnet")
    )

[<Fact>]
let ``helper host refuses to launch when no muxer exists`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            DetachedLaunch.helperHost "/usr/bin/dotnet" "/root/shared/x/1/" (fun _ -> false)
            |> ignore)

    Assert.Contains("no dotnet host found", error.Message)

[<Fact>]
let ``launch failure names the outcome and the command for every helper observation`` () =
    let bound = TimeSpan.FromSeconds 5.0

    let failure =
        DetachedLaunch.launchFailure bound "the command" (Some "shell said no\n")

    Assert.Equal(None, failure (DetachedLaunch.HelperObservation.Exited 0))

    let exited = (failure (DetachedLaunch.HelperObservation.Exited 7)).Value
    Assert.IsType<InvalidOperationException>(exited) |> ignore

    Assert.Equal(
        "Detached daemon launch helper exited 7 launching: the command\nHelper stderr:\nshell said no",
        exited.Message
    )

    let killed = (failure (DetachedLaunch.HelperObservation.Stuck true)).Value
    Assert.IsType<TimeoutException>(killed) |> ignore
    Assert.Contains("exceeded 5 seconds and was killed", killed.Message)
    Assert.EndsWith("\nHelper stderr:\nshell said no", killed.Message)

    let unreaped = (failure (DetachedLaunch.HelperObservation.Stuck false)).Value
    Assert.IsType<TimeoutException>(unreaped) |> ignore
    Assert.Contains("exceeded 5 seconds and could not be reaped", unreaped.Message)
    Assert.EndsWith("\nHelper stderr:\nshell said no", unreaped.Message)

[<Fact>]
let ``exit codes in the signal range name the signal that killed the helper`` () =
    Assert.Equal("7", DetachedLaunch.describeExitCode 7)
    Assert.Equal("128", DetachedLaunch.describeExitCode 128)
    Assert.Equal("160", DetachedLaunch.describeExitCode 160)
    Assert.Equal("134 (killed by signal 6, SIGABRT)", DetachedLaunch.describeExitCode 134)
    Assert.Equal("139 (killed by signal 11, SIGSEGV)", DetachedLaunch.describeExitCode 139)
    Assert.Equal("137 (killed by signal 9, SIGKILL)", DetachedLaunch.describeExitCode 137)
    // 10 is SIGBUS on macOS and SIGUSR1 on Linux, so no single name is right.
    Assert.Equal("138 (killed by signal 10, a platform-numbered signal)", DetachedLaunch.describeExitCode 138)

[<Fact>]
let ``helper stderr is described whether it held text, held nothing, or never closed`` () =
    Assert.Equal(
        "Helper stderr:\nline one\nline two",
        DetachedLaunch.describeHelperStderr (Some "line one\nline two\n")
    )

    Assert.Equal("Helper stderr: empty.", DetachedLaunch.describeHelperStderr (Some " \n"))

    Assert.Equal("Helper stderr: still open when the wait for it ended.", DetachedLaunch.describeHelperStderr None)

// ---------------------------------------------------------------------------
// The caller's half of a launch, against a stand-in helper (AUTOMATION-1651).
//
// The real helper is a copy of this CLI, and every CLI process a test starts runs the
// coverage-instrumented assembly, so what it executed was graded as this suite's coverage
// on some runs and not on others. The tests that start the real helper are in
// FsHotWatch.IntegrationTests (DetachedLaunchProcessTests). Here `/bin/sh` stands in for
// it: the caller's waits, kill, reaping and stderr drain are the same code whatever the
// helper is, and every outcome below is decided by the command, not by timing.
// ---------------------------------------------------------------------------

/// Start info for a stand-in helper that runs `command` in `/bin/sh`, redirected as the
/// real helper's start info is.
let private standIn (directory: string) (command: string) : ProcessStartInfo =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add command
    psi.WorkingDirectory <- directory
    psi.UseShellExecute <- false
    psi.RedirectStandardInput <- true
    psi.RedirectStandardError <- true
    psi

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a helper that exits non-zero is a failure naming the command and its stderr`` () =
    withTempDir "detached-through-exit" (fun root ->
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                DetachedLaunch.launchThrough
                    ignore
                    HangWait
                    "the command"
                    (standIn root "echo 'shell said no' >&2; exit 7"))

        Assert.Equal(
            "Detached daemon launch helper exited 7 launching: the command\nHelper stderr:\nshell said no",
            error.Message
        ))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a helper that succeeds hands its stderr to the report, and a silent one hands nothing`` () =
    withTempDir "detached-through-report" (fun root ->
        let messages = ResizeArray<string>()
        DetachedLaunch.launchThrough messages.Add HangWait "noted" (standIn root "echo 'a note' >&2; exit 0")
        Assert.Equal<string list>([ "a note\n" ], List.ofSeq messages)

        messages.Clear()
        DetachedLaunch.launchThrough messages.Add HangWait "silent" (standIn root "exit 0")
        Assert.Empty(messages))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a helper still running at the bound is killed, reaped and reported`` () =
    withTempDir "detached-through-stuck" (fun root ->
        let receipt = Path.Combine(root, "helper.pid")

        try
            // `exec` keeps the pid the shell announced the sleeper's for its whole life;
            // a 30 s sleep outlasts the 10 s bound however loaded the box, and the bound is
            // also the reaping budget once it is killed.
            let error =
                Assert.Throws<TimeoutException>(fun () ->
                    DetachedLaunch.launchThrough
                        ignore
                        (TimeSpan.FromSeconds 10.0)
                        "the sleeper"
                        (standIn root $"echo $$ > %s{DetachedLaunch.shellQuote receipt}; exec /bin/sleep 30"))

            Assert.Contains("exceeded 10 seconds and was killed, launching: the sleeper", error.Message)
            let pid = Int32.Parse(File.ReadAllText(receipt).Trim())

            let alive =
                try
                    use proc = Process.GetProcessById pid
                    not proc.HasExited
                with :? ArgumentException ->
                    false

            Assert.False(alive, $"the stuck helper (pid %d{pid}) must be reaped")
        finally
            if File.Exists receipt then
                try
                    use proc = Process.GetProcessById(Int32.Parse(File.ReadAllText(receipt).Trim()))

                    if not proc.HasExited then
                        proc.Kill()
                with :? ArgumentException ->
                    ())

// The backgrounded sleep inherits the helper's stderr and holds it for ten minutes, so the
// stream is still open when the caller's drain gives up, however loaded the box.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a helper whose stderr outlives it is reported as still open`` () =
    withTempDir "detached-through-held-stderr" (fun root ->
        let command = "/bin/sleep 600 & echo $! > holder.pid; exit 3"

        try
            let error =
                Assert.Throws<InvalidOperationException>(fun () ->
                    DetachedLaunch.launchThrough ignore HangWait command (standIn root command))

            Assert.Equal(
                $"Detached daemon launch helper exited 3 launching: %s{command}\nHelper stderr: still open when the wait for it ended.",
                error.Message
            )
        finally
            let pidFile = Path.Combine(root, "holder.pid")

            if File.Exists pidFile then
                try
                    (Process.GetProcessById(int (File.ReadAllText(pidFile).Trim()))).Kill()
                with
                | :? ArgumentException
                | :? InvalidOperationException -> ())

// A launch whose working directory does not exist fails at the start, in this process:
// nothing is started, so no copy of the CLI runs.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a launch from a directory that does not exist fails before any helper runs`` () =
    let missing = "/nonexistent/fshw-detached-launch-directory"

    Assert.ThrowsAny<ComponentModel.Win32Exception>(fun () -> DetachedLaunch.launch missing "exit 0")
    |> ignore
