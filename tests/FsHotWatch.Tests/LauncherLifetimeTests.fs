module FsHotWatch.Tests.LauncherLifetimeTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

[<DllImport("libc", EntryPoint = "getpgid", SetLastError = true)>]
extern int private processGroupOf(int pid)

let private groupOf (pid: int) =
    let group = processGroupOf pid
    Assert.True(group > 0, $"getpgid(%d{pid}) failed (errno %d{Marshal.GetLastPInvokeError()})")
    group

/// Single-quote a value for `/bin/sh`, so the fixture's own script can name a
/// receipt path containing any character.
let private shQuote (value: string) =
    "'" + value.Replace("'", "'\"'\"'") + "'"

// The second stem carries a quote, a dollar and a backtick: the launch shell
// must hand the executable path over as ONE word, not run parts of it.
[<Theory(Timeout = 30000)>]
[<InlineData("owned-child")>]
[<InlineData("owned '$child`")>]
let ``production launcher gives its child a process group independent of the caller`` (fileStem: string) =
    withTempDir "launcher-lifetime" (fun root ->
        let receipt = Path.Combine(root, "child.pid")
        let script = Path.Combine(root, fileStem + ".sh")
        // The child announces its own pid, then execs so that pid stays the child's.
        // The sleep is a final bound on its life even if the cleanup below never runs.
        File.WriteAllText(
            script,
            "#!/bin/sh\nprintf '%s\\n' \"$$\" > "
            + shQuote receipt
            + "\nexec /bin/sleep 20\n"
        )

        File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let mutable child: Process option = None

        try
            launchDaemonProcess script "" root "" (Path.Combine(root, "daemon.log"))

            Assert.True(
                waitUntilTrue (fun () -> File.Exists receipt && FileInfo(receipt).Length > 0L) 10000,
                "positive control: the launched child must announce itself"
            )

            let pid = Int32.Parse(File.ReadAllText(receipt).Trim())
            let owned = Process.GetProcessById pid
            child <- Some owned
            Assert.False(owned.HasExited, "positive control: the child must be alive before its group is compared")
            Assert.NotEqual(groupOf Environment.ProcessId, groupOf pid)
        finally
            match child with
            | Some owned ->
                use owned = owned

                // Only the child this test announced itself — never a group.
                if not owned.HasExited then
                    owned.Kill()

                Assert.True(owned.WaitForExit 5000, "the fixture child must be reaped")
            | None -> ())
