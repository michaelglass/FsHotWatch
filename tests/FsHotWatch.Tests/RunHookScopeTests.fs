/// The CLI's run-level hooks (`beforeRun`/`afterRun`) run in the CLI's own process,
/// before and after the daemon's work. Their process trees belong to the run: a run that
/// ends, or is interrupted, leaves none of them behind.
module FsHotWatch.Tests.RunHookScopeTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.Program
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private hooks (before: string option) (after: string option) : DaemonConfiguration =
    { defaultTestConfig () with
        BeforeRun = before
        AfterRun = after
        RunHookTimeoutSec = Some 60 }

/// Signal handlers that capture the finalizer instead of listening for a real signal,
/// so a test can deliver the "signal" itself.
let private capturingSignals (slot: (unit -> unit) option ref) =
    fun (finalize: unit -> unit) (_exitWith: int -> unit) ->
        slot.Value <- Some finalize

        { new IDisposable with
            member _.Dispose() = () }

let private alive (pid: int) =
    try
        use p = Process.GetProcessById pid
        not p.HasExited
    with _ ->
        false

let private readPid (path: string) =
    match Int32.TryParse((File.ReadAllText path).Trim()) with
    | true, pid -> Some pid
    | _ -> None

[<Fact(Timeout = 30000)>]
let ``a run-level hook runs inside a process scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-scope" (fun root ->
            let lines = ConcurrentQueue<string>()

            isolated (fun () ->
                use _ =
                    Logging.installSink
                        { Write = lines.Enqueue
                          Level = Logging.LogLevel.Debug }

                withRunHooksCommandUsingSignals
                    (capturingSignals (ref None))
                    FsHotWatch.Cli.Verdict.Check
                    root
                    (hooks (Some "true") (Some "true"))
                    (fun _ -> 0)
                |> ignore)

            test <@ lines |> Seq.exists (fun l -> l.Contains "Running beforeRun") @>
            test <@ not (lines |> Seq.exists (fun l -> l.Contains "no registry in scope")) @>)

/// Start a run through `bracket` whose beforeRun is still running when the run is
/// interrupted, with a child of its own, then interrupt it: neither may outlive the run.
let private interruptedMidHook
    (bracket: ((unit -> unit) -> (int -> unit) -> IDisposable) -> DaemonConfiguration -> string -> int)
    =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-interrupted" (fun root ->
            let hookPid = Path.Combine(root, "hook.pid")
            let childPid = Path.Combine(root, "child.pid")

            let hook = $"echo $$ > '%s{hookPid}'; sleep 30 & echo $! > '%s{childPid}'; wait"

            let signal = ref None

            let run =
                Task.Run(fun () -> bracket (capturingSignals signal) (hooks (Some hook) None) root)

            let pids () =
                [ hookPid; childPid ]
                |> List.choose (fun p -> if File.Exists p then readPid p else None)

            try
                test <@ waitUntilTrue (fun () -> (pids ()).Length = 2) 20000 @>
                test <@ pids () |> List.forall alive @>

                match signal.Value with
                | Some finalize -> finalize ()
                | None -> failwith "the signal finalizer was not installed"

                test <@ waitUntilTrue (fun () -> pids () |> List.forall (alive >> not)) 5000 @>
            finally
                // Whatever the outcome, nothing of this test outlives it.
                for pid in pids () do
                    try
                        (Process.GetProcessById pid).Kill(true)
                    with _ ->
                        ()

                run.Wait(TimeSpan.FromSeconds 30.0) |> ignore)

[<Fact(Timeout = 60000)>]
let ``an interrupted run leaves no hook process behind`` () =
    interruptedMidHook (fun signals config root ->
        withRunHooksCommandUsingSignals signals FsHotWatch.Cli.Verdict.Check root config (fun _ -> 0))

[<Fact(Timeout = 60000)>]
let ``an interrupted confirm fast path leaves no hook process behind`` () =
    interruptedMidHook (fun signals config root ->
        withRunHooksUnclaimedUsingSignals signals FsHotWatch.Cli.Verdict.Confirm root config (fun () -> 0))
