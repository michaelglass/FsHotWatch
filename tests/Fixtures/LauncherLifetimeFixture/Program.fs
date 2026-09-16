/// The isolated CALLER for the launcher group-survival integration test: a process
/// that owns a fresh session and process group, launches a child through the
/// production daemon launcher, and then waits to be killed by that group signal.
module FsHotWatch.LauncherLifetimeFixture

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading

[<DllImport("libc", EntryPoint = "setsid", SetLastError = true)>]
extern int private createSession()

[<EntryPoint>]
let main args =
    match args with
    | [| root; script; log; ready |] ->
        // A group of its OWN, so the test can signal it without touching anyone else.
        if createSession () <> Environment.ProcessId then
            eprintfn $"launcher fixture could not create its own session (errno %d{Marshal.GetLastPInvokeError()})"
            1
        else
            FsHotWatch.Cli.Program.launchDaemonProcess script "" root "" log
            File.WriteAllText(ready, string Environment.ProcessId)
            // A natural bound on this process's life if the test never signals it.
            Thread.Sleep(TimeSpan.FromSeconds 30.0)
            0
    | _ ->
        eprintfn "usage: <root> <script> <log> <ready-receipt>"
        2
