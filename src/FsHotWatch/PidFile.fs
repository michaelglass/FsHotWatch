/// A pid file: one process id, as text. `.fshw/daemon.pid` is the per-worktree daemon's;
/// a repository host keeps its own in its control directory.
///
/// Read in one place so every reader agrees on what a pid file can hold. Only a
/// positive integer names one process: the OS reads zero and negative pids as process
/// groups, so a file holding one is as unusable as a file holding text.
module FsHotWatch.PidFile

open System
open System.IO

/// `.fshw/daemon.pid` — the pid of the worktree's running daemon.
let daemonPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "daemon.pid")

/// Pure: the pid a pid file's text names. `None` unless the text, surrounding
/// whitespace ignored, is a positive integer.
let parse (text: string) : int option =
    match Int32.TryParse(text.Trim()) with
    | true, pid when pid > 0 -> Some pid
    | _ -> None

/// The pid `file` names. `None` when the file is absent, unreadable, or holds no
/// positive pid: "nothing to read", never "nothing is running".
let tryRead (file: string) : int option =
    try
        parse (File.ReadAllText file)
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

/// The pid `.fshw/daemon.pid` names under `repoRoot` (`tryRead`).
let tryReadDaemon (repoRoot: string) : int option = tryRead (daemonPath repoRoot)
