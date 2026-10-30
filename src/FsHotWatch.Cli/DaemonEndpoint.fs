/// Where the running daemon's IPC socket actually is.
///
/// On Unix .NET resolves a bare pipe name to `$TMPDIR/CoreFxPipe_<name>`, read from the
/// environment of the process doing the resolving. A client whose TMPDIR differs from the
/// daemon's — an agent sandbox on `/tmp`, a login shell on `/var/folders/…` — computes the
/// same pipe name, looks for a socket that does not exist, and times out against a live
/// daemon with nothing to say it was alive. So the daemon records the absolute socket path
/// it listens on, and a client connects to that path (a rooted name is used as is), whatever
/// its own TMPDIR says.
///
/// Lifecycle: written beside `daemon.pid` when the daemon starts, removed with it on every
/// way out. A record left by a daemon that died without cleanup is ignored, because the pid
/// in `daemon.pid` is checked first.
module FsHotWatch.Cli.DaemonEndpoint

open System
open System.IO
open FsHotWatch

/// `.fshw/daemon.endpoint` — the absolute socket path the running daemon listens on.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "daemon.endpoint")

/// The socket file `pipeName` names in THIS process: a rooted name as is, otherwise
/// .NET's `CoreFxPipe_` file under this process's temp directory. On Windows a pipe
/// name is not a file and no TMPDIR is involved, so the name is returned unchanged.
let socketPath (pipeName: string) : string =
    if OperatingSystem.IsWindows() || Path.IsPathRooted pipeName then
        pipeName
    else
        Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName)

/// Record the socket this daemon listens on. Never throws: a daemon without a record
/// is still reached by every client that shares its TMPDIR, as before.
let record (repoRoot: string) (pipeName: string) : unit =
    // On Windows this records the bare pipe name, which `resolve` ignores (not rooted).
    try
        FsHwPaths.atomicWriteAllText (path repoRoot) (socketPath pipeName)
    with ex ->
        Logging.debug "ipc" $"could not record the daemon endpoint: %s{ex.GetType().Name}: %s{ex.Message}"

/// Remove the record. Never throws.
let clear (repoRoot: string) : unit =
    try
        File.Delete(path repoRoot)
    with ex ->
        Logging.debug "ipc" $"could not clear the daemon endpoint: %s{ex.GetType().Name}: %s{ex.Message}"

let private tryReadText (file: string) : string option =
    try
        Some(File.ReadAllText(file).Trim())
    with _ ->
        None

/// The name a client should connect to: the socket the running daemon recorded, when
/// `daemon.pid` names a live process and the record is an absolute path; otherwise
/// `pipeName`, resolved by this process as before.
let resolve (processAlive: int -> bool) (repoRoot: string) (pipeName: string) : string =
    let recordedPid =
        tryReadText (Path.Combine(FsHwPaths.root repoRoot, "daemon.pid"))
        |> Option.bind (fun text ->
            match Int32.TryParse text with
            | true, pid -> Some pid
            | false, _ -> None)

    match recordedPid with
    | Some pid when processAlive pid ->
        match tryReadText (path repoRoot) with
        | Some recorded when Path.IsPathRooted recorded -> recorded
        | _ -> pipeName
    | _ -> pipeName
