/// `fshw headroom`: the machine's memory headroom and the fshw daemons recorded under
/// this repository, as a REPORT. It stops no process, deletes no file and contacts no
/// daemon, so running it cannot change what it measures.
///
/// A repository that runs fshw in several jj workspaces (`.workspaces/<name>`, the
/// convention this repository's CLAUDE.md describes) can hold a daemon per workspace,
/// each one large: its footprint is dominated by native FCS allocations and scales with
/// the tree. Before starting another heavy run, the questions are how much memory is
/// left and which of those daemons are still there. This answers both and leaves acting
/// on the answer to the caller: `fshw stop` in a worktree stops its daemon, and any
/// other verb there removes a stale pidfile as routine hygiene.
module FsHotWatch.Cli.Headroom

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open FsHotWatch.IdleExit

/// What a worktree's `.fshw/daemon.pid` says about its daemon.
[<RequireQualifiedAccess>]
type DaemonState =
    /// The pid's process started no later than the pidfile was written: the daemon.
    | Live of pid: int
    /// No process holds the pid: the daemon is gone and left its pidfile behind.
    | Stale of pid: int
    /// A process holds the pid but started after the pidfile was written, so the OS
    /// handed the gone daemon's pid to something else. Stale, and NOT that process's.
    | Recycled of pid: int
    /// The pidfile holds no positive pid, or could not be read.
    | Unreadable

/// One worktree's daemon record.
type DaemonRecord = { Root: string; State: DaemonState }

/// How far a process's reported start time may trail the write of the pidfile it
/// names and still count as the process that wrote it. The daemon writes its pid only
/// once its runtime is up, so the real gap is the other way round; this absorbs the OS's
/// start-time arithmetic (Linux derives it from boot time and clock ticks), not a race.
let internal PidfileStartSlack = TimeSpan.FromSeconds 1.0

/// Whether the process holding a pidfile's pid now can be the one that wrote it: it
/// started no later than the pidfile was written. A process that started afterwards
/// was handed a recycled pid after the daemon that wrote the file was gone.
let internal startedBeforePidfile (pidfileWritten: DateTime) (processStarted: DateTime) : bool =
    processStarted <= pidfileWritten + PidfileStartSlack

/// Pure: a pidfile's state from its text, its write time and a probe answering the
/// start time of the process holding a pid (`None`: no such process).
let internal classify
    (processStart: int -> DateTime option)
    (pidfileWritten: DateTime)
    (pidText: string)
    : DaemonState =
    match Int32.TryParse(pidText.Trim()) with
    | true, pid when pid > 0 ->
        match processStart pid with
        | None -> DaemonState.Stale pid
        | Some started when startedBeforePidfile pidfileWritten started -> DaemonState.Live pid
        | Some _ -> DaemonState.Recycled pid
    | _ -> DaemonState.Unreadable

/// The live probe: the start time of the process holding `pid`, `None` when there is
/// none. A process whose start time cannot be read (another user's) answers
/// `DateTime.MinValue`, which reads as the daemon: an unknown is reported as live,
/// never as stale.
let internal liveProcessStart (pid: int) : DateTime option =
    try
        use proc = Process.GetProcessById pid
        Some(proc.StartTime.ToUniversalTime())
    with
    // No such process, or one that has exited and not yet been reaped.
    | :? ArgumentException
    | :? InvalidOperationException -> None
    | _ -> Some DateTime.MinValue

/// The worktrees whose daemon records are surveyed: `repoRoot`, then each directory
/// directly under `<repoRoot>/.workspaces`, in name order. Only those that have a
/// `.fshw/daemon.pid` are reported. Reads files; writes none.
let internal survey (processStart: int -> DateTime option) (repoRoot: string) : DaemonRecord list =
    let workspacesDir = Path.Combine(repoRoot, ".workspaces")

    let workspaces =
        if Directory.Exists workspacesDir then
            Directory.GetDirectories workspacesDir |> Array.sort |> Array.toList
        else
            []

    (repoRoot :: workspaces)
    |> List.choose (fun root ->
        let pidPath = Path.Combine(root, ".fshw", "daemon.pid")

        if File.Exists pidPath then
            let state =
                try
                    classify processStart (File.GetLastWriteTimeUtc pidPath) (File.ReadAllText pidPath)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> DaemonState.Unreadable

            Some { Root = root; State = state }
        else
            None)

/// Pure: `MemAvailable` from Linux's `/proc/meminfo`, in bytes.
let internal parseMemAvailable (meminfo: string) : int64 option =
    meminfo.Split('\n')
    |> Array.tryPick (fun line ->
        if line.StartsWith("MemAvailable:", StringComparison.Ordinal) then
            let parts =
                line.Substring("MemAvailable:".Length).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)

            match Int64.TryParse parts[0] with
            | true, kib -> Some(kib * 1024L)
            | _ -> None
        else
            None)

/// Pure: the macOS kernel's `kern.memorystatus_level` (the percentage of memory
/// available) as bytes of `totalBytes`.
let internal availableFromLevel (totalBytes: int64) (levelPercent: int) : int64 = totalBytes * int64 levelPercent / 100L

[<DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)>]
extern int private sysctlByNameInt32(
    string name,
    int& value,
    unativeint& length,
    nativeint newValue,
    unativeint newLength
)

/// `kern.memorystatus_level`, macOS only.
let private macMemoryLevel () : int option =
    let mutable level = 0
    let mutable length = unativeint sizeof<int>

    let status =
        sysctlByNameInt32 ("kern.memorystatus_level", &level, &length, IntPtr.Zero, 0un)

    if status = 0 && length = unativeint sizeof<int> then
        Some level
    else
        None

/// Available memory where the platform reports it: the kernel's level on macOS,
/// `MemAvailable` on Linux, `None` elsewhere or when the read fails. The platform and
/// both reads are passed in, so every branch is testable on any host.
let internal availableOn
    (isMacOS: bool)
    (isLinux: bool)
    (macLevel: unit -> int option)
    (readMeminfo: unit -> string)
    (totalBytes: int64)
    : int64 option =
    if isMacOS then
        macLevel () |> Option.map (availableFromLevel totalBytes)
    elif isLinux then
        try
            parseMemAvailable (readMeminfo ())
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None
    else
        None

let private readAvailable (totalBytes: int64) : int64 option =
    availableOn
        (OperatingSystem.IsMacOS())
        (OperatingSystem.IsLinux())
        macMemoryLevel
        (fun () -> File.ReadAllText "/proc/meminfo")
        totalBytes

let private gib (bytes: int64) = float bytes / 1073741824.0

/// Pure: the report's lines.
let internal render
    (pressure: PressureReading)
    (availableBytes: int64 option)
    (totalBytes: int64)
    (records: DaemonRecord list)
    : string list =
    let memory =
        match availableBytes with
        | Some available -> $"memory: %.1f{gib available} GiB available of %.1f{gib totalBytes} GiB"
        | None -> $"memory: available memory unknown on this platform; %.1f{gib totalBytes} GiB physical"

    let line (record: DaemonRecord) =
        match record.State with
        | DaemonState.Live pid -> $"  %s{record.Root}: live (pid %d{pid})"
        | DaemonState.Stale pid -> $"  %s{record.Root}: stale (pid %d{pid} is gone)"
        | DaemonState.Recycled pid ->
            $"  %s{record.Root}: stale (pid %d{pid} now belongs to a process started after the pidfile)"
        | DaemonState.Unreadable -> $"  %s{record.Root}: unreadable daemon.pid"

    let count predicate =
        records |> List.filter (fun r -> predicate r.State) |> List.length

    let live =
        count (function
            | DaemonState.Live _ -> true
            | _ -> false)

    let stale =
        count (function
            | DaemonState.Stale _
            | DaemonState.Recycled _ -> true
            | _ -> false)

    let daemons =
        if List.isEmpty records then
            [ "daemons: no daemon recorded here or under .workspaces" ]
        else
            $"daemons: %d{live} live, %d{stale} stale" :: List.map line records

    [ memory; $"pressure: %s{PressureReading.describe pressure}" ]
    @ daemons
    @ [ "report only: nothing was stopped or deleted. `fshw stop` in a worktree stops its daemon; any other verb there clears a stale pidfile." ]

/// Print the report for `repoRoot`. Always exit 0: it reports, it does not judge.
let run (repoRoot: string) : int =
    let total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes

    render (readPressure ()) (readAvailable total) total (survey liveProcessStart repoRoot)
    |> List.iter (printfn "%s")

    0
