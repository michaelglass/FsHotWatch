/// The session change runs in a fresh child, never in the calling agent/daemon.
module internal FsHotWatch.Cli.DetachedLaunch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

[<Literal>]
let private helperFlag = "--internal-detached-launch"

type private AssemblyAnchor = class end

[<DllImport("libc", EntryPoint = "setsid", SetLastError = true)>]
extern int private createSession()

[<DllImport("libc", EntryPoint = "execv", SetLastError = true)>]
extern int private replaceProcess([<MarshalAs(UnmanagedType.LPUTF8Str)>] string path, nativeint argv)

// Native calls stay in the dedicated child. The orchestration boundary lets
// controls inspect the actual UTF-8 argv allocation without replacing the test host.
let internal runHelperWith createSession replaceProcess lastError report (command: string) =
    if createSession () = -1 then
        let error = lastError ()
        report $"Detached daemon launch: setsid failed (errno {error})"
        1
    else
        // execv replaces this short-lived managed helper. No managed fork and
        // no background managed process is retained to supervise the shell.
        let values =
            [| "/bin/sh"; "-c"; command |] |> Array.map Marshal.StringToCoTaskMemUTF8

        let argv = Marshal.AllocHGlobal((values.Length + 1) * IntPtr.Size)

        try
            values
            |> Array.iteri (fun index value -> Marshal.WriteIntPtr(argv, index * IntPtr.Size, value))

            Marshal.WriteIntPtr(argv, values.Length * IntPtr.Size, IntPtr.Zero)
            replaceProcess ("/bin/sh", argv) |> ignore
            let error = lastError ()
            report $"Detached daemon launch: execv failed (errno {error})"
            1
        finally
            Marshal.FreeHGlobal(argv)
            values |> Array.iter Marshal.FreeCoTaskMem

let private runHelper command =
    runHelperWith createSession replaceProcess Marshal.GetLastPInvokeError (eprintfn "%s") command

/// Checked before ordinary command parsing, in the separate CLI process only.
let tryRun (args: string array) =
    match args with
    | [| flag; command |] when flag = helperFlag -> Some(runHelper command)
    | _ -> None

let launch (repoRoot: string) (command: string) =
    // Use this CLI assembly even when invoked by a test host or another apphost.
    // The runtime directory identifies the actual host installation, without
    // requiring a setsid executable, Python, or a different .NET version.
    let host =
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))

    let psi = ProcessStartInfo(host)
    psi.ArgumentList.Add(typeof<AssemblyAnchor>.Assembly.Location)
    psi.ArgumentList.Add(helperFlag)
    psi.ArgumentList.Add(command)
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    use child = Process.Start(psi)

    if not (child.WaitForExit(5000)) then
        // This handle is our own just-created helper, never a daemon pidfile.
        if not child.HasExited then
            child.Kill(entireProcessTree = true)

        if not (child.WaitForExit(5000)) then
            raise (TimeoutException("Detached daemon launch helper could not be reaped"))

        raise (TimeoutException("Detached daemon launch helper exceeded five seconds"))

    if child.ExitCode <> 0 then
        invalidOp $"Detached daemon launch helper exited {child.ExitCode}"
