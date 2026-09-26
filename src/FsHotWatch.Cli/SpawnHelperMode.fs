/// The spawn helper (`FsHotWatch.SpawnHelper`) as a mode of this CLI, and its launch
/// when a daemon starts.
///
/// `fshw __spawn-helper` turns the process into the helper, serving requests on its
/// stdin and writing events to its stdout. A daemon started with `FSHW_SPAWN_HELPER=1`
/// launches one before it builds anything, while its own heap is still small, and
/// routes eligible spawns through it. Off by default: without the variable nothing is
/// launched and every spawn starts directly, as before.
///
/// A helper that cannot start, or that is lost later, is reported and then not
/// replaced: starting another one would be a fork of the large daemon, which is what
/// the helper exists to avoid. Spawns fall back to starting directly.
module FsHotWatch.Cli.SpawnHelperMode

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open FsHotWatch

/// The hidden argument that turns a CLI process into the spawn helper. Only the exact
/// argument vector `[| Flag |]` is honoured.
[<Literal>]
let Flag = "__spawn-helper"

/// `1` makes a starting daemon launch a spawn helper.
[<Literal>]
let EnableVar = "FSHW_SPAWN_HELPER"

/// How long stopping a helper waits for it to exit. A helper exits as soon as its
/// request pipe closes and it has killed what it still held.
let StopBudget = TimeSpan.FromSeconds 5.0

/// Whether a starting daemon should launch a spawn helper.
let enabled (getEnv: string -> string) : bool =
    (getEnv EnableVar |> Option.ofObj |> Option.map _.Trim()) = Some "1"

/// Serve as the helper when `args` asks for it, or `None` for every ordinary command
/// line. Checked before normal CLI parsing so the hidden flag never reaches the
/// command tree.
let internal tryRunWith (openInput: unit -> Stream) (openOutput: unit -> Stream) (args: string array) : int option =
    if args = [| Flag |] then
        SpawnHelper.serve (openInput ()) (openOutput ())
        Some 0
    else
        None

let tryRun (args: string array) : int option =
    tryRunWith Console.OpenStandardInput Console.OpenStandardOutput args

type private AssemblyAnchor = class end

/// The start info for a helper: this CLI's own assembly under `host`, with the flag.
/// stdin and stdout carry the protocol; stderr is inherited, so anything the helper
/// prints lands in the daemon's log. The environment is the daemon's, plus the
/// thread-suspend setting `ThreadSuspendInjection.launchOverride` decides, on every
/// platform: an explicit user value or the opt-out still wins.
let internal startInfo (getEnv: string -> string) (host: string) (cliAssembly: string) : ProcessStartInfo =
    let psi = ProcessStartInfo(host)
    psi.ArgumentList.Add cliAssembly
    psi.ArgumentList.Add Flag
    psi.UseShellExecute <- false
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true

    ThreadSuspendInjection.launchOverride true getEnv
    |> Option.iter (fun (name, value) -> psi.Environment[name] <- value)

    psi

/// The start info for a helper running this very CLI under the `dotnet` host that
/// `DetachedLaunch.helperHost` picks. Raises when there is no host.
let internal defaultStartInfo () : ProcessStartInfo =
    let host =
        DetachedLaunch.helperHost Environment.ProcessPath (RuntimeEnvironment.GetRuntimeDirectory()) File.Exists

    startInfo Environment.GetEnvironmentVariable host typeof<AssemblyAnchor>.Assembly.Location

/// A launched helper, as the daemon holds it.
[<NoComparison; NoEquality>]
type internal Running =
    {
        Pid: int
        Connection: SpawnHelper.Connection
        /// Close the request pipe and wait, up to `StopBudget`, for the helper to exit.
        Stop: unit -> unit
    }

/// Launch a helper. The connection is usable at once: requests wait in the pipe until
/// the helper's runtime has started.
let internal launch (makeStartInfo: unit -> ProcessStartInfo) () : Result<Running, string> =
    try
        // FSHW-SPAWN-001 ok: the helper is not a child of any operation. It lives as long
        // as the daemon, `Stop` ends it, and it exits by itself when the daemon's end of
        // its request pipe closes. Registering it would let a scope's teardown kill it
        // before the helper children that scope owns had been killed through it.
        let helper = Process.Start(makeStartInfo ())

        let connection =
            new SpawnHelper.Connection(helper.StandardInput.BaseStream, helper.StandardOutput.BaseStream)

        Ok
            { Pid = helper.Id
              Connection = connection
              Stop =
                fun () ->
                    (connection :> IDisposable).Dispose()
                    helper.WaitForExit(int StopBudget.TotalMilliseconds) |> ignore
                    helper.Dispose() }
    with ex ->
        Error $"%s{ex.GetType().Name}: %s{ex.Message}"

let private nothing =
    { new IDisposable with
        member _.Dispose() = () }

/// When `enabled`, launch a helper and install it for everything started from this
/// context. The result uninstalls and stops it. A helper that cannot start is reported
/// and the daemon runs without one.
let internal installWith (enabled: bool) (launch: unit -> Result<Running, string>) : IDisposable =
    if not enabled then
        nothing
    else
        match launch () with
        | Error reason ->
            Logging.warn
                "spawn-helper"
                $"%s{EnableVar}=1, but the spawn helper could not start (%s{reason}); spawns start directly"

            nothing
        | Ok running ->
            Logging.info "spawn-helper" $"spawn helper started (pid %d{running.Pid}); hook steps start through it"
            let installed = SpawnHelper.install running.Connection

            { new IDisposable with
                member _.Dispose() =
                    installed.Dispose()
                    running.Stop() }

/// The daemon's helper, per `FSHW_SPAWN_HELPER`. Called once, before the daemon is
/// created, so the helper is forked from a small process.
let installForDaemon () : IDisposable =
    installWith (enabled Environment.GetEnvironmentVariable) (launch defaultStartInfo)
