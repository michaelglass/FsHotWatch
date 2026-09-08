[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.LauncherLifetimeTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

let private processGroup pid =
    let psi = ProcessStartInfo("/bin/ps")
    psi.ArgumentList.Add("-o")
    psi.ArgumentList.Add("pgid=")
    psi.ArgumentList.Add("-p")
    psi.ArgumentList.Add(string pid)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    use probe = Process.Start(psi)
    Assert.True(probe.WaitForExit(5000), "owned process-group observation must finish")
    Assert.Equal(0, probe.ExitCode)
    Int32.Parse(probe.StandardOutput.ReadToEnd().Trim())

[<Theory(Timeout = 20000)>]
[<InlineData("owned-child")>]
[<InlineData("owned '$child`")>]
[<Trait("ProcessSupervision", "LauncherLifetime")>]
let ``production launcher gives its child a process group independent of the caller`` (fileStem: string) =
    withTempDir "launcher-lifetime" (fun root ->
        let receipt = Path.Combine(root, "child.pid")
        let script = Path.Combine(root, fileStem + ".sh")
        // The child reports its own identity before exec, whose PID is stable.
        // Its ten-second lifetime is a final bound even if fixture cleanup fails.
        let quote (value: string) =
            "'" + value.Replace("'", "'\"'\"'") + "'"

        File.WriteAllText(script, "#!/bin/sh\nprintf '%s\\n' \"$$\" > " + quote receipt + "\nexec /bin/sleep 10\n")
        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let mutable child: Process option = None

        try
            launchDaemonProcess script "" root "" (Path.Combine(root, fileStem + ".log"))

            Assert.True(
                SpinWait.SpinUntil(
                    (fun () -> File.Exists receipt && FileInfo(receipt).Length > 0L),
                    TimeSpan.FromSeconds 5.0
                ),
                "fixture child must announce successful launch"
            )

            let pid = Int32.Parse(File.ReadAllText(receipt).Trim())
            let owned = Process.GetProcessById(pid)
            child <- Some owned
            Assert.False(owned.HasExited, "positive control: child must be alive before checking separation")
            Assert.NotEqual(processGroup Environment.ProcessId, processGroup pid)
        finally
            match child with
            | Some owned ->
                use owned = owned

                if not owned.HasExited then
                    owned.Kill(entireProcessTree = true)

                Assert.True(owned.WaitForExit(5000), "fixture must reap only its own announced child")
            | None -> ())
