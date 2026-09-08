module FsHotWatch.Tests.LauncherGroupSurvivalTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open FsHotWatch.Tests.TestHelpers

[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private signalProcessGroup(int pid, int signal)

let private groupOf pid =
    let info = ProcessStartInfo("/bin/ps")

    for arg in [ "-o"; "pgid="; "-p"; string pid ] do
        info.ArgumentList.Add arg

    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    use probe = Process.Start info
    Assert.True(probe.WaitForExit(5000))
    Assert.Equal(0, probe.ExitCode)
    Int32.Parse(probe.StandardOutput.ReadToEnd().Trim())

let private stopOwned (child: Process) =
    if not child.HasExited then
        child.Kill(entireProcessTree = true)

    Assert.True(child.WaitForExit(5000), "owned fixture process must be reaped")

[<Fact(Timeout = 30000)>]
[<Trait("A106Supervision", "LauncherGroupSurvival")>]
let ``daemon child survives termination of its isolated caller process group`` () =
    withTempDir "launcher-group-survival" (fun root ->
        let repo =
            FsHotWatch.Cli.Program.findRepoRoot AppContext.BaseDirectory |> Option.get

        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name

        let fixture =
            Path.Combine(
                repo,
                "tests",
                "Fixtures",
                "LauncherLifetimeFixture",
                "bin",
                configuration,
                "net10.0",
                "FsHotWatch.LauncherLifetimeFixture.dll"
            )

        Assert.True(File.Exists fixture, "the referenced fixture project must be built before integration execution")

        let quote (value: string) =
            "'" + value.Replace("'", "'\"'\"'") + "'"

        let childReceipt = Path.Combine(root, "child.pid")
        let outerReceipt = Path.Combine(root, "outer.ready")
        let script = Path.Combine(root, "owned-child.sh")

        File.WriteAllText(
            script,
            "#!/bin/sh\nprintf '%s\\n' \"$$\" > "
            + quote childReceipt
            + "\nexec /bin/sleep 30\n"
        )

        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let host =
            Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))

        let info = ProcessStartInfo(host)

        for arg in [ fixture; root; script; Path.Combine(root, "child.log"); outerReceipt ] do
            info.ArgumentList.Add arg

        info.UseShellExecute <- false
        use outer = Process.Start info
        let mutable detached: Process option = None

        try
            Assert.True(
                SpinWait.SpinUntil(
                    (fun () ->
                        File.Exists outerReceipt
                        && FileInfo(outerReceipt).Length > 0L
                        && File.Exists childReceipt
                        && FileInfo(childReceipt).Length > 0L),
                    TimeSpan.FromSeconds 7.0
                ),
                "both fixture processes must announce successful startup"
            )

            Assert.Equal(outer.Id, Int32.Parse(File.ReadAllText(outerReceipt)))
            Assert.False(outer.HasExited)
            // These are mandatory safety assertions BEFORE any group signal:
            // only the fresh fixture's own isolated group may be targeted.
            let fixtureGroup = groupOf outer.Id
            Assert.Equal(outer.Id, fixtureGroup)
            Assert.NotEqual(groupOf Environment.ProcessId, fixtureGroup)
            let child = Process.GetProcessById(Int32.Parse(File.ReadAllText(childReceipt)))
            detached <- Some child
            Assert.False(child.HasExited, "positive control: detached child is alive before the signal")
            Assert.Equal(0, signalProcessGroup (-fixtureGroup, 15))
            Assert.True(outer.WaitForExit(5000), "the isolated caller must actually terminate")
            Assert.False(child.WaitForExit(1000), "daemon child must survive the caller group's termination")
        finally
            try
                stopOwned outer
            finally
                match detached with
                | Some child ->
                    use child = child
                    stopOwned child
                | None -> ())
