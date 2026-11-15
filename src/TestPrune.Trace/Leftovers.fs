/// What a launched child leaves running once it has exited, and how a launch ends it
/// (AUTOMATION-1734). A process the child started is re-parented when the child exits, so it is
/// outside `Process.Kill(true)`'s tree; one that inherited the child's output pipes holds them
/// open, and a launch waiting for them to close would wait for it.
module TestPrune.Trace.Leftovers

open System
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

/// Where a launch looks for the processes it left: the machine's process table.
type ProcessTable =
    /// A `/proc` tree at `root`, as Linux has.
    | Proc of root: string
    /// `ps` and `lsof` at these paths, as macOS has.
    | Tools of ps: string * lsof: string

/// This machine's process table: the one choice made by platform.
let current: ProcessTable =
    if OperatingSystem.IsLinux() then
        Proc "/proc"
    else
        Tools("/bin/ps", "/usr/sbin/lsof")

let private isAlive (pid: int) = sysKill (pid, 0) = 0

let private pidOf (text: string) =
    match Int32.TryParse(text.Trim()) with
    | true, pid -> Some pid
    | _ -> None

/// What `read` reads of another process's state, or nothing when it cannot be read: the process
/// exited since it was listed, or belongs to another user. Either way it is not one to stop.
let private readable (read: unit -> 'T) : 'T option =
    try
        Some(read ())
    with _ ->
        None

/// Run a tool to its end: its standard output, or why it could not be run.
let private tool (exe: string) (args: string list) : Result<string, string> =
    let psi =
        ProcessStartInfo(exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    args |> List.iter psi.ArgumentList.Add

    try
        using (Process.Start psi) (fun p ->
            let err = p.StandardError.ReadToEndAsync()
            let text = p.StandardOutput.ReadToEnd()
            p.WaitForExit()
            err.Wait()
            Ok text)
    with e ->
        Error $"%s{exe} could not be run: %s{e.Message}"

/// The pids named by the directories directly under a `/proc` tree.
let private procPids (root: string) : int list =
    Directory.GetDirectories root
    |> Array.choose (fun dir -> pidOf (Path.GetFileName dir))
    |> List.ofArray

/// The pids `ps -axww -E -o pid=,command=` lists with `needle` among their environment.
let internal psCarrying (needle: string) (listing: string) : int list =
    listing.Split '\n'
    |> Array.choose (fun line ->
        let line = line.Trim()

        match line.IndexOf ' ' with
        | -1 -> None
        | at when (" " + line.Substring(at + 1) + " ").Contains($" %s{needle} ") -> pidOf (line.Substring(0, at))
        | _ -> None)
    |> List.ofArray

/// The processes other than this one whose environment carries `launchId` (`LaunchIdEnv`).
/// A `/proc` tree gives each process's `environ`; `ps -E` prints the environment of each of this
/// user's processes that exec'd. A forked process that never exec'd shows none there, so `Tools`
/// misses it; `holders` finds it while it holds the output.
let carrying (table: ProcessTable) (launchId: string) : Result<int list, string> =
    let needle = $"%s{LaunchIdEnv}=%s{launchId}"

    match table with
    | Proc root ->
        procPids root
        |> List.filter (fun pid ->
            readable (fun () -> File.ReadAllText(Path.Combine(root, string pid, "environ")))
            |> Option.exists (fun environ -> environ.Split '\000' |> Array.contains needle))
        |> Ok
    | Tools(ps, _) ->
        tool ps [ "-axww"; "-E"; "-o"; "pid=,command=" ]
        |> Result.map (psCarrying needle)

/// The file descriptor under a stream this process reads a child's output from.
let private fdOf (stream: Stream) : Result<int, string> =
    match stream with
    | :? PipeStream as p -> Ok(int (p.SafePipeHandle.DangerousGetHandle()))
    | :? FileStream as f -> Ok(int (f.SafeFileHandle.DangerousGetHandle()))
    | other -> Error $"the output is a %s{other.GetType().Name}, not a pipe"

/// The peers `lsof -F n` names for our read ends (`n->0x…`): the pipes' write ends.
let internal lsofPeers (listing: string) : Result<Set<string>, string> =
    match
        listing.Split '\n'
        |> Array.choose (fun line ->
            if line.StartsWith "n->" then
                Some(line.Substring 3)
            else
                None)
        |> Set.ofArray
    with
    | ends when ends.IsEmpty -> Error "lsof named no peer for the output pipes"
    | ends -> Ok ends

/// The processes other than `self` that `lsof -F pd` lists with a descriptor on one of `ends`.
let internal lsofHolders (ends: Set<string>) (self: int) (listing: string) : int list =
    listing.Split '\n'
    |> Array.fold
        (fun (owner, found) line ->
            if line.StartsWith "p" then
                pidOf (line.Substring 1), found
            elif line.StartsWith "d" && ends.Contains(line.Substring 1) then
                match owner with
                | Some pid when pid <> self -> owner, Set.add pid found
                | _ -> owner, found
            else
                owner, found)
        (None, Set.empty)
    |> snd
    |> Set.toList

/// The target a `/proc` fd link names (`pipe:[…]`), when it can be read.
let private linkTarget (link: string) : string option =
    readable (fun () -> FileInfo(link).LinkTarget) |> Option.bind Option.ofObj

/// The processes other than this one holding the pipes this process reads through `streams`,
/// matched by the pipes' identity, never by a process's name: in a `/proc` tree, any descriptor
/// naming the same pipe; with `lsof`, any descriptor on the write end it names as our read end's
/// peer.
let holders (table: ProcessTable) (streams: Stream list) : Result<int list, string> =
    let self = Environment.ProcessId

    streams
    |> List.fold
        (fun fds stream -> fds |> Result.bind (fun fds -> fdOf stream |> Result.map (fun fd -> fd :: fds)))
        (Ok [])
    |> Result.bind (fun fds ->
        match table with
        | Proc root ->
            let pipes =
                fds
                |> List.choose (fun fd -> linkTarget (Path.Combine(root, string self, "fd", string fd)))
                |> Set.ofList

            procPids root
            |> List.filter (fun pid ->
                pid <> self
                && readable (fun () -> Directory.GetFiles(Path.Combine(root, string pid, "fd")))
                   |> Option.exists (Array.exists (fun link -> linkTarget link |> Option.exists pipes.Contains)))
            |> Ok
        | Tools(_, lsof) ->
            tool
                lsof
                [ "-a"
                  "-p"
                  string self
                  "-d"
                  fds |> List.map string |> String.concat ","
                  "-F"
                  "n" ]
            |> Result.bind lsofPeers
            |> Result.bind (fun ends ->
                tool lsof [ "-u"; Environment.UserName; "-F"; "pd" ]
                |> Result.map (lsofHolders ends self)))

/// Stop each of `pids` still alive: TERM, then KILL once `TermGrace` has passed for any that
/// ignored it. How many were alive to stop.
let terminate (pids: int list) : int =
    let living = pids |> List.distinct |> List.filter isAlive
    living |> List.iter (fun pid -> sysKill (pid, SigTerm) |> ignore)

    // On the awake clock: a sleep of the box must not spend the grace (AUTOMATION-1488).
    let waited = Stopwatch.StartNew()

    while waited.Elapsed < TermGrace && living |> List.exists isAlive do
        Thread.Sleep 50

    living
    |> List.filter isAlive
    |> List.iter (fun pid -> sysKill (pid, SigKill) |> ignore)

    living.Length
