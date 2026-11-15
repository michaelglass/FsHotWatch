/// What a launched child leaves running once it has exited, and how a launch ends it
/// (AUTOMATION-1734). A process the child started is re-parented when the child exits, so it is
/// outside `Process.Kill(true)`'s tree; one that inherited the child's output pipes holds them
/// open, and a launch waiting for them to close would wait for it.
module TestPrune.Trace.Leftovers

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Threading

/// Names a launch in its child's environment, which everything the child starts inherits.
[<Literal>]
let LaunchIdEnv = "TESTPRUNE_LAUNCH_ID"

[<DllImport("libc", EntryPoint = "kill", SetLastError = true)>]
extern int private sysKill(int pid, int signal)

[<Literal>]
let private SigTerm = 15

[<Literal>]
let private SigKill = 9

/// How long a process asked to stop (TERM) has before it is killed (KILL).
let TermGrace = TimeSpan.FromMilliseconds 500.0

let private isAlive (pid: int) = sysKill (pid, 0) = 0

let private pidOf (text: string) =
    match Int32.TryParse(text.Trim()) with
    | true, pid -> Some pid
    | _ -> None

/// Run a tool to its end: its standard output, or why it could not be run.
let private tool (exe: string) (args: string list) : Result<string, string> =
    let psi =
        ProcessStartInfo(exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for a in args do
        psi.ArgumentList.Add a

    try
        using (Process.Start psi) (fun p ->
            let err = p.StandardError.ReadToEndAsync()
            let text = p.StandardOutput.ReadToEnd()
            p.WaitForExit()
            err.Wait()
            Ok text)
    with :? Win32Exception as e ->
        Error $"%s{exe} could not be run: %s{e.Message}"

/// The processes other than this one whose environment carries `launchId` (`LaunchIdEnv`).
/// Linux reads `/proc/<pid>/environ`; elsewhere `ps -E` prints the environment of each of this
/// user's processes that exec'd. A forked process that never exec'd shows none there, so this
/// misses it on macOS; `holders` finds it while it holds the output.
let carrying (launchId: string) : Result<int list, string> =
    let needle = $"%s{LaunchIdEnv}=%s{launchId}"

    if OperatingSystem.IsLinux() then
        Directory.GetDirectories "/proc"
        |> Array.choose (fun d -> pidOf (Path.GetFileName d))
        |> Array.filter (fun pid ->
            // One that exited since the listing, or is another user's, has no readable environ.
            try
                File.ReadAllText($"/proc/%d{pid}/environ").Split '\000' |> Array.contains needle
            with
            | :? IOException
            | :? UnauthorizedAccessException -> false)
        |> List.ofArray
        |> Ok
    else
        tool "/bin/ps" [ "-axww"; "-E"; "-o"; "pid=,command=" ]
        |> Result.map (fun listing ->
            listing.Split '\n'
            |> Array.choose (fun line ->
                match line.Trim().Split(' ', 2) with
                | [| pid; rest |] when (" " + rest + " ").Contains($" %s{needle} ") -> pidOf pid
                | _ -> None)
            |> List.ofArray)

/// The file descriptor under a stream this process reads a child's output from.
let private fdOf (stream: Stream) : Result<int, string> =
    match stream with
    | :? PipeStream as p -> Ok(int (p.SafePipeHandle.DangerousGetHandle()))
    | :? FileStream as f -> Ok(int (f.SafeFileHandle.DangerousGetHandle()))
    | other -> Error $"the output is a %s{other.GetType().Name}, not a pipe"

/// The processes other than this one holding the pipes this process reads through `streams`:
/// on Linux, any descriptor naming the same pipe (`/proc/<pid>/fd`); elsewhere, any descriptor
/// on the pipe's write end, which `lsof` (at `lsof`) names as our read end's peer. Matched by
/// the pipe's identity, never by a process's name.
let holders (lsof: string) (streams: Stream list) : Result<int list, string> =
    let self = Environment.ProcessId

    let fds =
        streams
        |> List.map fdOf
        |> List.fold
            (fun acc fd ->
                match acc, fd with
                | Ok fds, Ok fd -> Ok(fd :: fds)
                | Error e, _
                | _, Error e -> Error e)
            (Ok [])

    if OperatingSystem.IsLinux() then
        fds
        |> Result.map (fun fds ->
            let pipes =
                fds
                |> List.choose (fun fd -> FileInfo($"/proc/self/fd/%d{fd}").LinkTarget |> Option.ofObj)
                |> Set.ofList

            Directory.GetDirectories "/proc"
            |> Array.choose (fun d -> pidOf (Path.GetFileName d))
            |> Array.filter (fun pid ->
                pid <> self
                && (try
                        Directory.GetFiles $"/proc/%d{pid}/fd"
                        |> Array.exists (fun link ->
                            FileInfo(link).LinkTarget |> Option.ofObj |> Option.exists pipes.Contains)
                    with
                    // One that exited since the listing, or is another user's, has no readable fds.
                    | :? IOException
                    | :? UnauthorizedAccessException -> false))
            |> List.ofArray)
    else
        let writeEnds =
            fds
            |> Result.bind (fun fds ->
                tool
                    lsof
                    [ "-a"
                      "-p"
                      string self
                      "-d"
                      fds |> List.map string |> String.concat ","
                      "-F"
                      "n" ])
            |> Result.bind (fun listing ->
                match
                    listing.Split '\n'
                    |> Array.choose (fun l -> if l.StartsWith "n->" then Some(l.Substring 3) else None)
                    |> Set.ofArray
                with
                | ends when ends.IsEmpty -> Error "lsof named no peer for the output pipes"
                | ends -> Ok ends)

        writeEnds
        |> Result.bind (fun ends ->
            tool lsof [ "-u"; Environment.UserName; "-F"; "pd" ]
            |> Result.map (fun listing ->
                let mutable current = None
                let found = Collections.Generic.HashSet<int>()

                for line in listing.Split '\n' do
                    if line.StartsWith "p" then
                        current <- pidOf (line.Substring 1)
                    elif line.StartsWith "d" && ends.Contains(line.Substring 1) then
                        current |> Option.iter (found.Add >> ignore)

                found |> Seq.filter ((<>) self) |> List.ofSeq))

/// Stop each of `pids` still alive: TERM, then KILL once `TermGrace` has passed for any that
/// ignored it. How many were alive to stop.
let terminate (pids: int list) : int =
    let living = pids |> List.distinct |> List.filter isAlive

    for pid in living do
        sysKill (pid, SigTerm) |> ignore

    let deadline = DateTime.UtcNow + TermGrace

    while DateTime.UtcNow < deadline && living |> List.exists isAlive do
        Thread.Sleep 50

    for pid in living |> List.filter isAlive do
        sysKill (pid, SigKill) |> ignore

    living.Length
