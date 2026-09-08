module FsHotWatch.LauncherLifetimeFixture

open System
open System.IO
open System.Runtime.InteropServices

[<DllImport("libc", EntryPoint = "setsid", SetLastError = true)>]
extern int private createSession()

[<EntryPoint>]
let main args =
    match args with
    | [| root; script; log; ready |] ->
        if createSession () <> Environment.ProcessId then
            eprintfn "Owned launcher fixture could not create its isolated session"
            1
        else
            FsHotWatch.Cli.Program.launchDaemonProcess script "" root "" log
            File.WriteAllText(ready, string Environment.ProcessId)
            // Final natural bound if the outer integration fixture fails before
            // sending its group signal. This process owns no shared daemon.
            Threading.Thread.Sleep(TimeSpan.FromSeconds 30.0)
            0
    | _ -> 2
