/// The OS process table, read inside this process instead of by starting `ps`.
///
/// Escalating a kill that did not return is what a box does when it is overloaded, and an
/// overloaded box is exactly when a `ps` we start misses its budget: the escalation then
/// knew nothing about the tree, signalled nothing and left it LEAKED. Reading the table
/// through `proc_listallpids`/`proc_pidinfo` on macOS and `/proc` on Linux needs no fork,
/// so it answers in the moment escalation needs it.
///
/// A process that leaves while the table is being read, or that this user may not ask
/// about, is simply absent: that is what a table read means at any instant.
[<RequireQualifiedAccess>]
module internal FsHotWatch.InProcessProcessTable

open System
open System.IO
open System.Runtime.InteropServices
open System.Text

/// One process, as the kernel reports it.
[<NoComparison>]
type Row =
    {
        Pid: int
        ParentPid: int
        /// Exited but not yet reaped.
        Zombie: bool
        Command: string
        /// The process group, when the kernel reported one.
        ProcessGroup: int option
    }

/// `sizeof(struct proc_bsdinfo)`: `proc_pidinfo` answers a flavor only for a buffer of
/// exactly its size.
[<Literal>]
let private BsdInfoSize = 136

[<Literal>]
let private PidTaskBsdInfo = 3

/// `SZOMB` in `pbi_status`.
[<Literal>]
let private DarwinZombieStatus = 5u

[<DllImport("libc", EntryPoint = "proc_listallpids", SetLastError = true)>]
extern int private procListAllPids(int[] buffer, int bufferSize)

[<DllImport("libc", EntryPoint = "proc_pidinfo", SetLastError = true)>]
extern int private procPidInfo(int pid, int flavor, uint64 arg, byte[] buffer, int bufferSize)

/// Pure: a `struct proc_bsdinfo`, as `proc_pidinfo` fills it. `None` for a buffer of any
/// other size.
let internal decodeBsdInfo (info: byte[]) : Row option =
    if info.Length <> BsdInfoSize then
        None
    else
        let comm = Encoding.ASCII.GetString(info, 48, 16)

        Some
            { Pid = int (BitConverter.ToUInt32(info, 12))
              ParentPid = int (BitConverter.ToUInt32(info, 16))
              Zombie = BitConverter.ToUInt32(info, 4) = DarwinZombieStatus
              Command = comm.TrimEnd('\000')
              ProcessGroup = Some(int (BitConverter.ToUInt32(info, 100))) }

/// Pure: the text of `/proc/<pid>/stat` — `pid (comm) state ppid …`. The command may hold
/// spaces and parentheses, so the fields after it are found from the LAST `)`.
let internal parseProcStat (text: string) : Row option =
    let opened = text.IndexOf '('
    let closed = text.LastIndexOf ')'

    if opened < 0 || closed < opened then
        None
    else
        let fields =
            text.Substring(closed + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)

        match Int32.TryParse(text.Substring(0, opened).Trim()) with
        | true, pid when fields.Length >= 2 ->
            match Int32.TryParse fields[1] with
            | true, ppid ->
                Some
                    { Pid = pid
                      ParentPid = ppid
                      Zombie = fields[0] = "Z"
                      Command = text.Substring(opened + 1, closed - opened - 1)
                      ProcessGroup = (if fields.Length >= 3 then (match Int32.TryParse fields[2] with | true, g -> Some g | _ -> None) else None) }
            | _ -> None
        | _ -> None

/// The pids `list` returns, growing the buffer until the answer fits: a buffer filled to
/// the brim may have dropped some.
let internal listPidsWith (list: int[] -> int) (lastError: unit -> int) (initial: int) : Result<int[], string> =
    let rec attempt capacity =
        let buffer = Array.zeroCreate<int> capacity
        let count = list buffer

        if count < 0 then
            Error $"proc_listallpids failed with errno %d{lastError ()}"
        elif count >= capacity then
            attempt (capacity * 2)
        else
            Ok(Array.truncate count buffer)

    attempt initial

/// The table on macOS.
let private readDarwin () : Result<Row list, string> =
    listPidsWith (fun buffer -> procListAllPids (buffer, buffer.Length * sizeof<int>)) Marshal.GetLastPInvokeError 4096
    |> Result.map (fun pids ->
        pids
        |> Array.choose (fun pid ->
            let info = Array.zeroCreate<byte> BsdInfoSize

            if procPidInfo (pid, PidTaskBsdInfo, 0UL, info, BsdInfoSize) = BsdInfoSize then
                decodeBsdInfo info
            else
                None)
        |> List.ofArray)

/// One `/proc/<pid>` entry. A process that left between the listing and this read has
/// no row.
let private procEntry (dir: string) : Row option =
    match Int32.TryParse(Path.GetFileName dir) with
    | true, _ ->
        try
            parseProcStat (File.ReadAllText(Path.Combine(dir, "stat")))
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None
    | _ -> None

/// The table on Linux.
let private readLinux () : Result<Row list, string> =
    try
        Ok(Directory.GetDirectories "/proc" |> Array.choose procEntry |> List.ofArray)
    with ex ->
        Error $"/proc could not be read: %s{ex.GetType().Name}: %s{ex.Message}"

/// Read the table with the platform's own reader; `Error` on a platform that has neither.
let internal readOn
    (isMacOS: bool)
    (isLinux: bool)
    (macOS: unit -> Result<Row list, string>)
    (linux: unit -> Result<Row list, string>)
    : Result<Row list, string> =
    if isMacOS then macOS ()
    elif isLinux then linux ()
    else Error "this platform has no in-process process table"

/// The table of this host, read without starting a process.
let read () : Result<Row list, string> =
    readOn (OperatingSystem.IsMacOS()) (OperatingSystem.IsLinux()) readDarwin readLinux
