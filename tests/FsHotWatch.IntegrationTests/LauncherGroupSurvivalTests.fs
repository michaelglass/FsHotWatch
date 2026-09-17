module FsHotWatch.Tests.LauncherGroupSurvivalTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsHotWatch.Tests.TestHelpers

[<DllImport("libc", EntryPoint = "getpgid", SetLastError = true)>]
extern int private processGroupOf(int pid)

[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private signal(int pid, int signal)

[<Literal>]
let private SIGTERM = 15

let private groupOf (pid: int) =
    let group = processGroupOf pid
    Assert.True(group > 0, $"getpgid(%d{pid}) failed (errno %d{Marshal.GetLastPInvokeError()})")
    group

let private shQuote (value: string) =
    "'" + value.Replace("'", "'\"'\"'") + "'"

let private hasContent (path: string) =
    File.Exists path && FileInfo(path).Length > 0L

/// Kill one process this test started or announced — by pid, never by group.
let private reap (proc: Process) =
    if not proc.HasExited then
        proc.Kill()

    Assert.True(proc.WaitForExit 5000, $"fixture process %d{proc.Id} must be reaped")

[<Fact(Timeout = 60000)>]
let ``daemon child survives termination of its isolated caller process group`` () =
    withTempDir "launcher-group-survival" (fun root ->
        // bin/<Configuration>/<tfm>/ of this test project, mirrored by the fixture's.
        let tfmDir = DirectoryInfo(AppContext.BaseDirectory)

        let fixture =
            Path.Combine(
                tfmDir.Parent.Parent.Parent.Parent.FullName,
                "Fixtures",
                "LauncherLifetimeFixture",
                "bin",
                tfmDir.Parent.Name,
                tfmDir.Name,
                "FsHotWatch.LauncherLifetimeFixture.dll"
            )

        Assert.True(File.Exists fixture, $"the referenced fixture project must be built: %s{fixture}")

        let childReceipt = Path.Combine(root, "child.pid")
        let outerReceipt = Path.Combine(root, "outer.ready")
        let script = Path.Combine(root, "owned-child.sh")

        File.WriteAllText(
            script,
            "#!/bin/sh\nprintf '%s\\n' \"$$\" > "
            + shQuote childReceipt
            + "\nexec /bin/sleep 30\n"
        )

        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let info =
            ProcessStartInfo(
                Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))
            )

        for arg in [ fixture; root; script; Path.Combine(root, "child.log"); outerReceipt ] do
            info.ArgumentList.Add arg

        info.UseShellExecute <- false
        use outer = Process.Start info
        let mutable detached: Process option = None

        try
            Assert.True(
                waitUntilTrue (fun () -> hasContent outerReceipt && hasContent childReceipt) 20000,
                "positive control: the caller and its launched child must both announce themselves"
            )

            // Mandatory BEFORE any signal: only the fixture's own fresh group is a target.
            let fixtureGroup = groupOf outer.Id
            Assert.Equal(outer.Id, Int32.Parse(File.ReadAllText(outerReceipt).Trim()))
            Assert.Equal(outer.Id, fixtureGroup)
            Assert.NotEqual(groupOf Environment.ProcessId, fixtureGroup)

            let child =
                Process.GetProcessById(Int32.Parse(File.ReadAllText(childReceipt).Trim()))

            detached <- Some child
            Assert.False(child.HasExited, "positive control: the child is alive before the signal")

            Assert.Equal(0, signal (-fixtureGroup, SIGTERM))
            Assert.True(outer.WaitForExit 10000, "the isolated caller must actually die with its group")
            Assert.False(child.WaitForExit 1000, "the launched child must survive its caller's group")
        finally
            try
                reap outer
            finally
                detached
                |> Option.iter (fun child ->
                    use child = child
                    reap child))
