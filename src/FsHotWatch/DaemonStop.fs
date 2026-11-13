/// Why a daemon stopped. Every way a daemon ends is a cancel of its run, and before
/// AUTOMATION-1675 the log said only "Daemon stopped." whatever asked for it, so three
/// stops of one daemon could not be told apart. Each stop path now records its cause
/// here, and the daemon logs that one line as it goes.
module FsHotWatch.DaemonStop

open System.Runtime.InteropServices
open System.Threading

/// The signals a daemon handles. Each ends it as the default disposition would.
type DaemonSignal =
    | SigInt
    | SigTerm
    | SigHup

module DaemonSignal =
    let posix (signal: DaemonSignal) : PosixSignal =
        match signal with
        | SigInt -> PosixSignal.SIGINT
        | SigTerm -> PosixSignal.SIGTERM
        | SigHup -> PosixSignal.SIGHUP

    let name (signal: DaemonSignal) : string =
        match signal with
        | SigInt -> "SIGINT"
        | SigTerm -> "SIGTERM"
        | SigHup -> "SIGHUP"

    /// 128 + the signal's number: the exit code the signal's default action gives.
    let exitCode (signal: DaemonSignal) : int =
        match signal with
        | SigInt -> 130
        | SigTerm -> 143
        | SigHup -> 129

/// The RPC a client stopped the daemon with.
type StopRpc =
    /// Stop unconditionally (`fshw stop`, a restart, a heal).
    | Shutdown
    /// Stop only when the caller is the daemon's sole user (a one-shot verb's release).
    | ReleaseIfSole

/// The CLI process behind a stop RPC, as it describes itself.
type StopClient =
    {
        /// Its command line: the verb, with whatever else it was given.
        Command: string
        Pid: int
        Cwd: string
    }

module StopClient =
    /// This process, as a stop RPC describes it.
    let current () : StopClient =
        { Command = System.Environment.GetCommandLineArgs() |> String.concat " "
          Pid = System.Environment.ProcessId
          Cwd = System.Environment.CurrentDirectory }

/// Who sent a stop RPC. A CLI from before AUTOMATION-1675 sends no description.
type StopCaller =
    | Described of StopClient
    | UnknownClient

type RpcStopRequest = { Rpc: StopRpc; Caller: StopCaller }

type StopCause =
    | RpcStop of RpcStopRequest
    | Signal of DaemonSignal
    /// `.fshw.json` now holds a different configuration than the one loaded.
    | ConfigChanged
    | IdleExit
    /// The repository root this daemon serves was deleted.
    | RootVanished
    /// A plugin stopped making progress past its bound.
    | Wedged
    /// The run was cancelled by something that named no cause: a repository host ending
    /// a session, or a test.
    | Cancelled

let private rpcName (rpc: StopRpc) : string =
    match rpc with
    | Shutdown -> "Shutdown"
    | ReleaseIfSole -> "ReleaseIfSole"

/// The line a stopping daemon logs, naming the cause.
let line (cause: StopCause) : string =
    let what =
        match cause with
        | RpcStop { Rpc = rpc; Caller = Described client } ->
            $"%s{rpcName rpc} RPC from pid=%d{client.Pid} cwd=%s{client.Cwd} command=%s{client.Command}"
        | RpcStop { Rpc = rpc; Caller = UnknownClient } ->
            $"%s{rpcName rpc} RPC from a client that did not describe itself (an fshw CLI older than this daemon)"
        | Signal signal -> $"signal %s{DaemonSignal.name signal}"
        | ConfigChanged -> ".fshw.json changed"
        | IdleExit -> "idle exit"
        | RootVanished -> "the repository root vanished"
        | Wedged -> "a plugin wedged"
        | Cancelled -> "run cancelled without a recorded cause"

    $"stop cause: %s{what}"

/// The first cause a daemon was asked to stop for. Later requests repeat the cancel,
/// which is idempotent, and leave the cause alone: the daemon is already going.
[<Sealed>]
type StopRecord() =
    let mutable first: StopCause option = None
    let gate = Lock()

    /// Record `cause` if none is yet, then cancel `run`.
    member _.Request(cause: StopCause, run: CancellationTokenSource) : unit =
        Locking.locked gate (fun () ->
            if first.IsNone then
                first <- Some cause)

        run.Cancel()

    /// What stopped the daemon; `Cancelled` when its run ended with nothing recorded.
    member _.Cause: StopCause = Locking.locked gate (fun () -> defaultArg first Cancelled)
