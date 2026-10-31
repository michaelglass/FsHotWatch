/// `fshw headroom` reports memory headroom and the daemons recorded under a repository,
/// and NEVER acts on what it finds: a live daemon is reported, not stopped, and a stale
/// pidfile is reported, not deleted.
module FsHotWatch.Tests.HeadroomTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open CommandTree
open FsHotWatch.Cli
open FsHotWatch.Cli.Headroom
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

let private written = DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)

[<Fact(Timeout = 5000)>]
let ``headroom parses, is report-only and needs no daemon`` () =
    test <@ CommandTree.parse commandTree [| "headroom" |] = Ok Command.Headroom @>
    test <@ not (needsDaemon Command.Headroom) @>
    test <@ commandToken Command.Headroom = "headroom" @>
    test <@ agentGateFor Command.Headroom = AgentGate.OwnOutput @>

[<Fact(Timeout = 5000)>]
let ``a pid with no process is stale`` () =
    test <@ classify (fun _ -> None) written "4242\n" = DaemonState.Stale 4242 @>

[<Fact(Timeout = 5000)>]
let ``a process that started before the pidfile was written is the live daemon`` () =
    test <@ classify (fun _ -> Some(written.AddMinutes -5.0)) written "4242" = DaemonState.Live 4242 @>

[<Fact(Timeout = 5000)>]
let ``a process that started after the pidfile was written holds a recycled pid`` () =
    test <@ classify (fun _ -> Some(written.AddMinutes 5.0)) written "4242" = DaemonState.Recycled 4242 @>

[<Fact(Timeout = 5000)>]
let ``a pidfile with no positive pid is unreadable`` () =
    test <@ classify (fun _ -> failwith "must not probe") written "garbage" = DaemonState.Unreadable @>
    test <@ classify (fun _ -> failwith "must not probe") written "-3" = DaemonState.Unreadable @>

let private writePid (root: string) (pid: string) =
    let dir = Path.Combine(root, ".fshw")
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "daemon.pid")
    File.WriteAllText(path, pid)
    path

[<Fact(Timeout = 15000)>]
let ``survey reports the repository and its workspaces, and changes nothing`` () =
    withTempDir "headroom-survey" (fun root ->
        let rootPid = writePid root "100"
        let live = Path.Combine(root, ".workspaces", "live")
        let livePid = writePid live "200"
        // A workspace with no daemon record is not a daemon to report.
        Directory.CreateDirectory(Path.Combine(root, ".workspaces", "never-ran"))
        |> ignore

        let probe pid =
            match pid with
            | 200 -> Some(DateTime.UtcNow.AddHours -1.0)
            | _ -> None

        let records = survey probe root

        test
            <@
                records = [ { Root = root
                              State = DaemonState.Stale 100 }
                            { Root = live
                              State = DaemonState.Live 200 } ]
            @>

        // Report only: both pidfiles are still there, byte for byte.
        test <@ File.ReadAllText rootPid = "100" @>
        test <@ File.ReadAllText livePid = "200" @>)

[<Fact(Timeout = 5000)>]
let ``MemAvailable is read from /proc/meminfo in bytes`` () =
    let meminfo =
        "MemTotal:       16303032 kB\nMemFree:          512340 kB\nMemAvailable:    8000000 kB\nBuffers:  1 kB\n"

    test <@ parseMemAvailable meminfo = Some(8000000L * 1024L) @>
    test <@ parseMemAvailable "MemTotal: 1 kB\n" = None @>

[<Fact(Timeout = 5000)>]
let ``the macOS available-memory level is a percentage of physical memory`` () =
    test <@ availableFromLevel 32_000_000_000L 25 = 8_000_000_000L @>

[<Fact(Timeout = 5000)>]
let ``the report names each daemon's state and says it acted on none`` () =
    let lines =
        render
            (FsHotWatch.IdleExit.PressureReading.KernelLevel 1)
            (Some 8_589_934_592L)
            34_359_738_368L
            [ { Root = "/r"
                State = DaemonState.Live 7 }
              { Root = "/r/.workspaces/a"
                State = DaemonState.Stale 8 }
              { Root = "/r/.workspaces/b"
                State = DaemonState.Recycled 9 }
              { Root = "/r/.workspaces/c"
                State = DaemonState.Unreadable } ]

    let text = String.Join("\n", lines)
    test <@ text.Contains "8.0 GiB available of 32.0 GiB" @>
    test <@ text.Contains "kernel memory-pressure level 1 (normal)" @>
    test <@ text.Contains "/r: live (pid 7)" @>
    test <@ text.Contains "/r/.workspaces/a: stale (pid 8 is gone)" @>
    test <@ text.Contains "/r/.workspaces/b: stale (pid 9 now belongs to a process started after the pidfile)" @>
    test <@ text.Contains "/r/.workspaces/c: unreadable daemon.pid" @>
    test <@ text.Contains "2 stale" @>
    test <@ text.Contains "nothing was stopped or deleted" @>

[<Fact(Timeout = 5000)>]
let ``the report says when no daemon is recorded and when available memory is unknown`` () =
    let text =
        String.Join("\n", render (FsHotWatch.IdleExit.PressureReading.GcHighLoad false) None 1_073_741_824L [])

    test <@ text.Contains "available memory unknown on this platform; 1.0 GiB physical" @>
    test <@ text.Contains "no daemon recorded" @>

[<Fact(Timeout = 15000)>]
let ``run against a real stale pidfile reports it and leaves it in place`` () =
    withTempDir "headroom-run" (fun root ->
        // A pid that provably has no process: one that has already exited and been reaped.
        use exited = Process.Start(ProcessStartInfo("true", UseShellExecute = false))
        exited.WaitForExit()
        let pidPath = writePid root (string exited.Id)
        test <@ run root = 0 @>
        test <@ File.Exists pidPath @>)

[<Fact(Timeout = 15000)>]
let ``the live probe answers a start time for a running process and none for a reaped one`` () =
    let self = Environment.ProcessId
    test <@ (liveProcessStart self).IsSome @>
    test <@ (liveProcessStart self).Value <= DateTime.UtcNow @>
    use exited = Process.Start(ProcessStartInfo("true", UseShellExecute = false))
    exited.WaitForExit()
    test <@ liveProcessStart exited.Id = None || exited.Id = self @>

[<Fact(Timeout = 15000)>]
let ``a pidfile that cannot be read is reported unreadable, not skipped`` () =
    if not (OperatingSystem.IsWindows()) && Environment.UserName <> "root" then
        withTempDir "headroom-unreadable" (fun root ->
            let pidPath = writePid root "100"
            File.SetUnixFileMode(pidPath, UnixFileMode.None)

            try
                test
                    <@
                        survey (fun _ -> None) root = [ { Root = root
                                                          State = DaemonState.Unreadable } ]
                    @>
            finally
                File.SetUnixFileMode(pidPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 5000)>]
let ``a MemAvailable line with no number reads as unknown`` () =
    test <@ parseMemAvailable "MemAvailable: lots kB\n" = None @>

[<Fact(Timeout = 5000)>]
let ``available memory comes from the kernel level on macOS, meminfo on Linux, nothing elsewhere`` () =
    let noRead () : string = failwith "must not read"
    test <@ availableOn true false (fun () -> Some 50) noRead 1000L = Some 500L @>
    test <@ availableOn true false (fun () -> None) noRead 1000L = None @>

    test
        <@
            availableOn false true (fun () -> failwith "must not ask") (fun () -> "MemAvailable: 2 kB\n") 1000L = Some
                2048L
        @>

    let gone () : string = raise (IOException "gone")
    test <@ availableOn false true (fun () -> None) gone 1000L = None @>
    test <@ availableOn false false (fun () -> failwith "must not ask") noRead 1000L = None @>
