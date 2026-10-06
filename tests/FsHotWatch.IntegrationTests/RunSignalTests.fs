module FsHotWatch.Tests.RunSignalTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Xunit
open FsHotWatch.Tests.TestHelpers

[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private signal(int pid, int signal)

[<Literal>]
let private SIGHUP = 1

[<Literal>]
let private SIGKILL = 9

/// `pid`'s state letter, parent and command, as the OS reports them: `/proc/<pid>/stat`
/// on Linux, `ps` elsewhere. `None` when no such process exists.
let private processState (pid: int) : (string * int * string) option =
    if OperatingSystem.IsLinux() then
        try
            // `pid (comm) state ppid …`; comm may hold spaces and parentheses, so split at
            // the LAST `)`.
            let stat = File.ReadAllText $"/proc/%d{pid}/stat"
            let close = stat.LastIndexOf ')'
            let comm = stat.Substring(stat.IndexOf '(' + 1, close - stat.IndexOf '(' - 1)
            let rest = stat.Substring(close + 2).Split ' '
            Some(rest[0], int rest[1], comm)
        with :? IOException ->
            None
    else
        let info = ProcessStartInfo("ps", [ "-o"; "stat=,ppid=,comm="; "-p"; string pid ])
        info.RedirectStandardOutput <- true
        use ps = Process.Start info
        let line = ps.StandardOutput.ReadToEnd().Trim()
        ps.WaitForExit()

        match line.Split([| ' '; '\t' |], 3, StringSplitOptions.RemoveEmptyEntries) with
        | [| state; ppid; comm |] -> Some(state, int ppid, comm)
        | _ -> None

/// Whether `pid` is still running. `kill(pid, 0)` also succeeds for a ZOMBIE — a process
/// that has exited and only waits for its parent (or, orphaned, the reaper it was
/// re-parented to) to collect its status. A zombie runs nothing and holds no resource,
/// so it has not outlived the run.
let private alive (pid: int) =
    signal (pid, 0) = 0
    && (processState pid
        |> Option.forall (fun (state, _, _) -> not (state.StartsWith "Z")))

/// What the OS says about each pid, for a failure message.
let private describePids (pids: (string * int) list) =
    pids
    |> List.map (fun (name, pid) ->
        match processState pid with
        | Some(state, ppid, comm) -> $"%s{name} pid %d{pid}: state %s{state}, parent %d{ppid}, `%s{comm}`"
        | None -> $"%s{name} pid %d{pid}: gone")
    |> String.concat "; "

let private cliAssembly () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let assembly =
        Path.Combine(root, "src", "FsHotWatch.Cli", "bin", configuration, "net10.0", "FsHotWatch.Cli.dll")

    Assert.True(File.Exists assembly, $"build the real CLI before running this integration test: %s{assembly}")
    assembly

let private dotnetHost () =
    Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"))

let private readPid (path: string) =
    match Int32.TryParse((if File.Exists path then File.ReadAllText path else "").Trim()) with
    | true, pid when pid > 0 -> Some pid
    | _ -> None

/// A terminal that closes sends SIGHUP. The run must end the way SIGINT and SIGTERM end
/// it: the hook it was interrupted in is reaped, a verdict says the run was signalled,
/// and the exit code is 128 + SIGHUP(1).
[<Fact(Timeout = 120000)>]
let ``SIGHUP during a beforeRun hook reaps the hook and exits 129`` () =
    withTempDir "run-sighup" (fun root ->
        use init = Process.Start(ProcessStartInfo("git", [ "init"; "--quiet"; root ]))
        Assert.True(init.WaitForExit 30000 && init.ExitCode = 0, "git init must make the fixture a checkout")

        let shellPid = Path.Combine(root, "shell.pid")
        let sleepPid = Path.Combine(root, "sleep.pid")

        // The hook's tree is three deep: fshw's shell, the `sh -c` it runs, and the
        // `sleep` that one backgrounds.
        File.WriteAllText(
            Path.Combine(root, ".fshw.json"),
            """{ "beforeRun": "sh -c 'echo $$ > shell.pid; sleep 300 & echo $! > sleep.pid; wait'" }"""
        )

        let info = ProcessStartInfo(dotnetHost ())
        info.WorkingDirectory <- root
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for arg in [ cliAssembly (); "check"; "--run-once" ] do
            info.ArgumentList.Add arg

        use cli = Process.Start info
        let stdout = cli.StandardOutput.ReadToEndAsync()
        let stderr = cli.StandardError.ReadToEndAsync()

        try
            Assert.True(
                waitUntilTrue (fun () -> (readPid shellPid).IsSome && (readPid sleepPid).IsSome) 60000,
                "positive control: the beforeRun hook must start and announce its processes"
            )

            let hookShell = (readPid shellPid).Value
            let hookSleep = (readPid sleepPid).Value
            Assert.True(alive hookSleep, "positive control: the hook's child is alive before the signal")

            Assert.Equal(0, signal (cli.Id, SIGHUP))
            Assert.True(cli.WaitForExit 30000, "the CLI must exit after SIGHUP")

            let output = stdout.Result + "\n" + stderr.Result
            Assert.True((cli.ExitCode = 129), $"expected exit 129, got %d{cli.ExitCode}:\n%s{output}")

            let reaped =
                waitUntilTrue (fun () -> not (alive hookSleep) && not (alive hookShell)) 10000

            let states = describePids [ ("hook shell", hookShell); ("hook sleep", hookSleep) ]
            Assert.True(reaped, $"the hook's process tree must not outlive the run (%s{states}):\n%s{output}")

            let verdict = File.ReadAllText(Path.Combine(root, ".fshw", "verdict.json"))
            Assert.Contains("the run was signalled before the check could finish", verdict)
        finally
            if not cli.HasExited then
                cli.Kill true
                cli.WaitForExit 10000 |> ignore

            // Only the processes this test's own hook announced.
            [ readPid sleepPid; readPid shellPid ]
            |> List.choose id
            |> List.filter alive
            |> List.iter (fun pid -> signal (pid, SIGKILL) |> ignore))
