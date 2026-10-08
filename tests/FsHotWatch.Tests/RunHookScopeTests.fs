/// The CLI's run-level hooks (`beforeRun`/`afterRun`) run in the CLI's own process,
/// before and after the daemon's work. Their process trees belong to the run: a run that
/// ends, or is interrupted, leaves none of them behind.
module FsHotWatch.Tests.RunHookScopeTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
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
    fun (_signal: RunSignal) (finalize: unit -> unit) (_exitWith: int -> unit) ->
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
    (bracket: (RunSignal -> (unit -> unit) -> (int -> unit) -> IDisposable) -> DaemonConfiguration -> string -> int)
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

/// Start a run through `bracket` whose beforeRun is still running, then deliver SIGHUP
/// through the handler's own contract (`onRunSignal`, with an `exitWith` that does not
/// end the test host). The handler's teardown kills the hook, so the run's thread sees
/// the hook fail: the run must still exit with the signal's code, not the hook-failure
/// code. Returns the run's exit code.
let private signalledMidHookWith
    (afterRun: string option)
    (bracket: (RunSignal -> (unit -> unit) -> (int -> unit) -> IDisposable) -> DaemonConfiguration -> string -> int)
    (root: string)
    =
    let hookPid = Path.Combine(root, "hook.pid")
    let installed = ref None

    let signals (signal: RunSignal) (finalize: unit -> unit) (_exitWith: int -> unit) =
        installed.Value <- Some(signal, finalize)

        { new IDisposable with
            member _.Dispose() = () }

    let run =
        Task.Run(fun () -> bracket signals (hooks (Some $"echo $$ > '%s{hookPid}'; sleep 30 & wait") afterRun) root)

    test <@ waitUntilTrue (fun () -> File.Exists hookPid && (readPid hookPid).IsSome) 20000 @>

    match installed.Value with
    | Some(signal, finalize) -> onRunSignal signal finalize ignore 129
    | None -> failwith "the signal handlers were not installed"

    test <@ run.Wait(TimeSpan.FromSeconds 30.0) @>
    run.Result

let private signalledMidHook bracket root = signalledMidHookWith None bracket root

[<Fact(Timeout = 60000)>]
let ``a run signalled mid-hook exits with the signal's code and a signalled verdict`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-signalled-code" (fun root ->
            let code =
                signalledMidHook
                    (fun signals config root ->
                        withRunHooksCommandUsingSignals signals FsHotWatch.Cli.Verdict.Check root config (fun _ -> 0))
                    root

            test <@ code = 129 @>

            let verdict = File.ReadAllText(Path.Combine(root, ".fshw", "verdict.json"))
            test <@ verdict.Contains "the run was signalled before the check could finish" @>)

/// The record a run signalled mid-hook leaves: its exit code, its outcome, and how its
/// beforeRun ended.
let private signalledRecord (root: string) =
    use verdict =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".fshw", "verdict.json")))

    let r = verdict.RootElement

    let hooks =
        [ for h in r.GetProperty("hooks").EnumerateArray() ->
              h.GetProperty("scope").GetString(), h.GetProperty("outcome").GetString() ]

    r.GetProperty("exitCode").GetInt32(), r.GetProperty("outcome").GetProperty("kind").GetString(), hooks

[<Fact(Timeout = 60000)>]
let ``a run signalled mid-hook records the signal's code and does not blame the hook`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-signalled-record" (fun root ->
            signalledMidHook
                (fun signals config root ->
                    withRunHooksCommandUsingSignals signals FsHotWatch.Cli.Verdict.Check root config (fun _ -> 0))
                root
            |> ignore

            test <@ signalledRecord root = (129, "incomplete", [ "run.beforeRun", "signalled" ]) @>)

[<Fact(Timeout = 60000)>]
let ``a confirm fast path signalled mid-hook records the signal's code and does not blame the hook`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-signalled-record-unclaimed" (fun root ->
            signalledMidHook
                (fun signals config root ->
                    withRunHooksUnclaimedUsingSignals signals FsHotWatch.Cli.Verdict.Confirm root config (fun () -> 0))
                root
            |> ignore

            let verdict = File.ReadAllText(Path.Combine(root, ".fshw", "verdict.json"))
            test <@ verdict.Contains signalledReason @>
            test <@ signalledRecord root = (129, "incomplete", [ "run.beforeRun", "signalled" ]) @>)

/// The signal's teardown runs afterRun after the signal arrived: a failure there is the
/// hook's own, and is recorded as one.
[<Fact(Timeout = 60000)>]
let ``an afterRun the signal's teardown runs keeps its own failure`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-signalled-after-run" (fun root ->
            signalledMidHookWith
                (Some "false")
                (fun signals config root ->
                    withRunHooksCommandUsingSignals signals FsHotWatch.Cli.Verdict.Check root config (fun _ -> 0))
                root
            |> ignore

            // The teardown files afterRun while the run's thread files beforeRun.
            let code, kind, hookOutcomes = signalledRecord root
            test <@ (code, kind) = (129, "incomplete") @>
            test <@ List.sort hookOutcomes = [ "run.afterRun", "fail"; "run.beforeRun", "signalled" ] @>)

/// A confirm fast path signalled after its beforeRun passed has no refusal to record:
/// it leaves the verdict on disk as it was.
[<Fact(Timeout = 30000)>]
let ``a confirm fast path signalled after beforeRun writes no record`` () =
    withTempDir "run-hook-signalled-after-before" (fun root ->
        let finalize = ref None

        let code =
            withRunHooksUnclaimedUsingSignals
                (capturingSignals finalize)
                FsHotWatch.Cli.Verdict.Confirm
                root
                (hooks (Some "true") None)
                (fun () ->
                    finalize.Value |> Option.iter (fun f -> f ())
                    0)

        test <@ code = 0 @>
        test <@ not (File.Exists(Path.Combine(root, ".fshw", "verdict.json"))) @>)

/// A signal's teardown waits for the hook in flight to be recorded, but only so long.
[<Fact(Timeout = 30000)>]
let ``a hook runner waits for its hook in flight, up to the timeout`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-await-idle" (fun root ->
            let hookPid = Path.Combine(root, "hook.pid")

            let runner =
                makeRunHookRunner root (hooks None None) (FsHotWatch.Cli.Verdict.Invocation.start ()) (RunSignal())

            test <@ runner.AwaitIdle TimeSpan.Zero @>

            let run =
                Task.Run(fun () -> runner.RunTimed "run.beforeRun" "beforeRun" $"echo $$ > '%s{hookPid}'; sleep 2")

            test <@ waitUntilTrue (fun () -> File.Exists hookPid) 20000 @>
            test <@ not (runner.AwaitIdle(TimeSpan.FromMilliseconds 50.0)) @>
            test <@ runner.AwaitIdle(TimeSpan.FromSeconds 20.0) @>
            test <@ fst run.Result @>
            test <@ fst (runner.Collected()) |> List.map _.Outcome = [ "ok" ] @>)

[<Fact(Timeout = 60000)>]
let ``a confirm fast path signalled mid-hook exits with the signal's code`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-signalled-code-unclaimed" (fun root ->
            let code =
                signalledMidHook
                    (fun signals config root ->
                        withRunHooksUnclaimedUsingSignals
                            signals
                            FsHotWatch.Cli.Verdict.Confirm
                            root
                            config
                            (fun () -> 0))
                    root

            test <@ code = 129 @>)

/// Signal handlers that deliver the "signal" the moment they are installed: the run is
/// interrupted, and its process scope shut, before its beforeRun can launch. The same
/// ordering as a signal landing between the hook's spawn and its admission, without
/// having to win that race.
let private signalledOnInstall (_signal: RunSignal) (finalize: unit -> unit) (_exitWith: int -> unit) =
    finalize ()

    { new IDisposable with
        member _.Dispose() = () }

/// A hook launched after its run was interrupted is refused by the run's shut scope. The
/// refusal is how the run ended, so the bracket reports it as the failed beforeRun it is
/// (exit 2), and no process of the hook's ever runs.
let private refusedAtLaunch (bracket: DaemonConfiguration -> string -> int) =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "run-hook-refused" (fun root ->
            let hookPid = Path.Combine(root, "hook.pid")
            let exitCode = bracket (hooks (Some $"echo $$ > '%s{hookPid}'") None) root

            test <@ exitCode = 2 @>
            test <@ not (File.Exists hookPid) @>)

[<Fact(Timeout = 30000)>]
let ``a run interrupted before its beforeRun launches refuses the hook and exits 2`` () =
    refusedAtLaunch (fun config root ->
        withRunHooksCommandUsingSignals signalledOnInstall FsHotWatch.Cli.Verdict.Check root config (fun _ -> 0))

[<Fact(Timeout = 30000)>]
let ``a confirm fast path interrupted before its beforeRun launches refuses the hook and exits 2`` () =
    refusedAtLaunch (fun config root ->
        withRunHooksUnclaimedUsingSignals signalledOnInstall FsHotWatch.Cli.Verdict.Confirm root config (fun () -> 0))

[<Fact>]
let ``the daemon shutdown line names the signal and the reaped pids`` () =
    test <@ daemonShutdownLine "SIGTERM" [ 11; 12 ] [] = "shutdown: signal=SIGTERM reaped=11,12" @>
    test <@ daemonShutdownLine "SIGHUP" [] [] = "shutdown: signal=SIGHUP reaped=none" @>

    test <@ daemonShutdownLine "SIGINT" [ 11; 12 ] [ 12 ] = "shutdown: signal=SIGINT reaped=11 unconfirmed=12" @>

/// The handler's contract, without delivering a real signal: the children the daemon's
/// registry owns are gone before it asks the daemon to stop, and the exit code is the
/// signal's.
[<Fact(Timeout = 60000)>]
let ``a signalled daemon reaps its registry's children before it stops`` () =
    if not (OperatingSystem.IsWindows()) then
        let registry = ProcessRegistry.Registry()
        let info = ProcessStartInfo("sleep", [ "30" ])
        info.UseShellExecute <- false
        use child = Process.Start info
        registry.Track child
        let signal = RunSignal()
        let mutable aliveWhenStopped = None

        onDaemonSignal signal registry (fun () -> aliveWhenStopped <- Some(alive child.Id)) "SIGTERM" 143

        test <@ aliveWhenStopped = Some false @>
        test <@ signal.Settle 0 = 143 @>
