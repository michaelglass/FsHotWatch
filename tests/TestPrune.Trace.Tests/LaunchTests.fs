module TestPrune.Trace.Tests.LaunchTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open TestPrune.Trace

[<Fact>]
let ``the dotnet root holds a shared runtime`` () =
    test <@ Directory.Exists(Path.Combine(Launch.dotnetRoot (), "shared", "Microsoft.NETCore.App")) @>

[<Fact>]
let ``run returns the exit code and both output streams, and passes the environment`` () =
    let code, output =
        Launch.run
            "/bin/sh"
            [ "-c"; "echo out-$LAUNCH_TEST_VAR; echo err >&2; exit 3" ]
            [ "LAUNCH_TEST_VAR", "yes" ]
            (Path.GetTempPath())
            (TimeSpan.FromMinutes 1.0)

    test <@ code = 3 @>
    test <@ output.Contains "out-yes" && output.Contains "err" @>

[<Fact>]
let ``run keeps the end of each stream of a long failing run, and says it dropped the rest`` () =
    // About 250 KB on stdout, well past the kept tail, then the lines that say why it failed.
    let script =
        "i=0; while [ $i -lt 5000 ]; do echo out-line-$i-padding-padding-padding-padding; i=$((i+1)); done; "
        + "echo out-last; echo err-last >&2; exit 2"

    let code, output =
        Launch.run "/bin/sh" [ "-c"; script ] [] (Path.GetTempPath()) (TimeSpan.FromMinutes 1.0)

    test <@ code = 2 @>
    test <@ output.Contains "out-last" && output.Contains "err-last" @>
    test <@ not (output.Contains "out-line-0-") && output.Contains "out-line-4999-" @>
    test <@ output.StartsWith Launch.DroppedOutputMarker @>
    test <@ output.Length <= 2 * Launch.OutputTailChars + Launch.DroppedOutputMarker.Length @>

[<Fact>]
let ``run starts the child outside the caller's trace, passing only the trace variables it is given`` () =
    // A traced test process holds TESTPRUNE_TRACE_*: a child that inherited them would dump
    // into the parent's directory. This name is unique and read by nothing, so setting it on
    // this process cannot disturb a concurrent test.
    let inherited = "TESTPRUNE_TRACE_LAUNCH_TEST_" + Guid.NewGuid().ToString "N"
    Environment.SetEnvironmentVariable(inherited, "from-parent")

    try
        let code, output =
            Launch.run
                "/usr/bin/env"
                []
                [ Recorder.Contract.OutEnv, "/explicit/dumps" ]
                (Path.GetTempPath())
                (TimeSpan.FromMinutes 1.0)

        // Only the trace lines: a failure message must not print the rest of the environment.
        let traceVars =
            output.Split '\n'
            |> Array.filter (fun l -> l.StartsWith "TESTPRUNE_TRACE_")
            |> Array.sort

        test <@ code = 0 @>
        test <@ traceVars = [| "TESTPRUNE_TRACE_OUT=/explicit/dumps" |] @>
    finally
        Environment.SetEnvironmentVariable(inherited, null)

[<Fact>]
let ``run kills a process that outlives its timeout`` () =
    // A `sleep 30` left to finish exits 0, so a non-zero code alone proves the kill: no
    // stopwatch, which a loaded box can push past any bound.
    let code, _ =
        Launch.run "/bin/sleep" [ "30" ] [] (Path.GetTempPath()) (TimeSpan.FromMilliseconds 200.0)

    test <@ code <> 0 @>

let private request exe args =
    { Launch.LaunchRequest.Exe = exe
      Launch.LaunchRequest.Args = args
      Launch.LaunchRequest.Env = []
      Launch.LaunchRequest.WorkDir = Path.GetTempPath()
      Launch.LaunchRequest.Timeout = TimeSpan.FromMinutes 1.0 }

// The child sleeps far past the xUnit cap, so a launcher that waited it out instead of
// killing it is a reported hang, not a slow pass: no stopwatch, which a loaded box can push
// past any bound.
[<Fact(Timeout = 120_000)>]
let ``cancelling the direct launcher kills its child and raises`` () =
    use cts = new CancellationTokenSource()
    cts.CancelAfter(TimeSpan.FromMilliseconds 200.0)

    let unbounded =
        { request "/bin/sleep" [ "600" ] with
            Timeout = TimeSpan.FromMinutes 10.0 }

    raises<OperationCanceledException> <@ Launch.direct unbounded cts.Token @>

[<Fact>]
let ``an already-cancelled launch never starts its child`` () =
    let marker =
        Path.Combine(Directory.CreateTempSubdirectory("tp-launch-").FullName, "started")

    raises<OperationCanceledException>
        <@ Launch.direct (request "/bin/sh" [ "-c"; $"touch '%s{marker}'" ]) (CancellationToken(true)) @>

    test <@ not (File.Exists marker) @>

[<Fact>]
let ``an explicit DOTNET_ROOT wins over the running runtime's root`` () =
    test <@ Launch.dotnetRootFrom "/opt/dotnet" = "/opt/dotnet" @>
    test <@ Launch.dotnetRootFrom "" = Launch.dotnetRootFrom null @>

/// Whether `pid` still runs, as `Leftovers.terminate` judges it: a zombie waiting for its reaper
/// does not (GitHub run 44083201746).
let private alive (pid: int) =
    Leftovers.isRunning Leftovers.current pid

// AUTOMATION-1734: a mutant's test left a TERM-ignoring loop re-parented to init, holding the
// inherited stdout, and the harness waited for the pipe to close until its 20-minute timeout.
// The orphan here loops forever, far past the xUnit cap, so a launcher that waited on the pipe
// is a reported hang, not a slow pass: no stopwatch, which a loaded box can push past any bound.
[<Fact(Timeout = 120_000)>]
let ``a run returns when its child exits, killing what the child left running on its output`` () =
    let script =
        "trap '' TERM; ( trap '' TERM; while :; do sleep 1; done ) & echo orphan=$!; echo started"

    let code, output =
        Launch.run "/bin/sh" [ "-c"; script ] [] (Path.GetTempPath()) (TimeSpan.FromMinutes 10.0)

    let orphan =
        output.Split '\n'
        |> Array.pick (fun l ->
            if l.StartsWith "orphan=" then
                Some(int (l.Substring 7))
            else
                None)

    try
        test <@ code = 0 && output.Contains "started" @>
        test <@ not (alive orphan) @>
        // The loop and the `sleep` it is in: how many depends on when the kill lands.
        test <@ output.Contains Launch.LeftRunningNote @>
    finally
        if alive orphan then
            Launch.run "/bin/kill" [ "-9"; string orphan ] [] (Path.GetTempPath()) (TimeSpan.FromMinutes 1.0)
            |> ignore

[<Fact>]
let ``a run whose child leaves nothing running says nothing about it`` () =
    let _, output =
        Launch.run "/bin/sh" [ "-c"; "echo done" ] [] (Path.GetTempPath()) (TimeSpan.FromMinutes 1.0)

    test <@ output = "done\n" @>

[<Fact>]
let ``output held open where lsof cannot run still ends after the grace, and says why`` () =
    // The `lsof` table, on any platform: without lsof the launch must still return, naming what
    // it could not do, never throw and never stay silent.
    use server = new IO.Pipes.AnonymousPipeServerStream(IO.Pipes.PipeDirection.In)
    let neverClosed = Threading.Tasks.TaskCompletionSource().Task

    let notes =
        Launch.endLeftovers (Leftovers.Tools("/bin/ps", "/nonexistent/lsof")) "no-such-launch" [ server ] neverClosed

    test <@ notes.StartsWith Launch.HeldOpenNote @>
    test <@ notes.Contains "/nonexistent/lsof" @>

/// A `/proc` tree with nothing in it: no process holds anything there.
let private emptyProc () =
    let root = Path.Combine(Path.GetTempPath(), "proc-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore
    root

[<Fact(Timeout = 30_000)>]
let ``output still held after every holder found was stopped ends after the grace, and says so`` () =
    // Nothing in the table holds the pipe, yet it stays open: something not found holds it.
    let root = emptyProc ()

    try
        use server = new IO.Pipes.AnonymousPipeServerStream(IO.Pipes.PipeDirection.In)
        let neverClosed = Threading.Tasks.TaskCompletionSource().Task

        let notes =
            Launch.endLeftovers (Leftovers.Proc root) "no-such-launch" [ server ] neverClosed

        test <@ notes.StartsWith(Launch.HeldOpenNote + " by a process not found (stopped 0") @>
    finally
        Directory.Delete(root, true)

[<Fact(Timeout = 30_000)>]
let ``a launch that cannot look for what its run left running says so`` () =
    let closed = Threading.Tasks.Task.CompletedTask

    let notes =
        Launch.endLeftovers (Leftovers.Tools("/nonexistent/ps", "/nonexistent/lsof")) "no-such-launch" [] closed

    test <@ notes.StartsWith "[launch: could not look for the processes the run left running: /nonexistent/ps" @>

[<Fact>]
let ``a stream's tail keeps its last characters and marks what it dropped`` () =
    let tail = Launch.Tail 4
    let chars (s: string) = s.ToCharArray()
    tail.Append(chars "ab", 2)
    test <@ tail.Text() = "ab" @>
    tail.Append(chars "cdef", 4)
    // Six characters against a limit of four: kept until twice the limit, read as the last four.
    test <@ tail.Text() = Launch.DroppedOutputMarker + "cdef" @>
    tail.Append(chars "ghij", 4)
    // Ten characters pass twice the limit, so the buffer trims to the last four.
    test <@ tail.Text() = Launch.DroppedOutputMarker + "ghij" @>

[<Fact(Timeout = 60_000)>]
let ``output held by a process the table names closes once that process is stopped, and says so`` () =
    // A real process stands in for a leftover: the table lists it holding our pipe, and the
    // output stays open until it is gone.
    let root = emptyProc ()
    let holder = Diagnostics.Process.Start("/bin/sleep", "30")

    try
        use server = new IO.Pipes.AnonymousPipeServerStream(IO.Pipes.PipeDirection.In)
        let fd = int (server.SafePipeHandle.DangerousGetHandle())

        let link (pid: int) (n: int) =
            let fdDir = Path.Combine(root, string pid, "fd")
            Directory.CreateDirectory fdDir |> ignore
            File.CreateSymbolicLink(Path.Combine(fdDir, string n), "pipe:[777]") |> ignore

        link Environment.ProcessId fd
        link holder.Id 4
        let closedWhenGone = Threading.Tasks.Task.Run(fun () -> holder.WaitForExit())

        let notes =
            Launch.endLeftovers (Leftovers.Proc root) "no-such-launch" [ server ] closedWhenGone

        test <@ notes = $"[launch: stopped 1 %s{Launch.LeftRunningNote}, holding its output]\n" @>
        test <@ holder.HasExited @>
    finally
        if not holder.HasExited then
            holder.Kill()

        Directory.Delete(root, true)

[<Fact>]
let ``a launch's note names what it stopped and what still runs after KILL`` () =
    test <@ Launch.taggedNoteOf (Ok(Leftovers.Termination.Stopped 0)) = "" @>

    test
        <@
            Launch.taggedNoteOf (Ok(Leftovers.Termination.Stopped 2)) = $"[launch: stopped 2 %s{Launch.LeftRunningNote}]\n"
        @>

    test
        <@
            Launch.taggedNoteOf (Ok(Leftovers.Termination.StillRunning(1, 12, []))) = $"[launch: stopped 1 %s{Launch.LeftRunningNote}; still running after KILL: 12]\n"
        @>
