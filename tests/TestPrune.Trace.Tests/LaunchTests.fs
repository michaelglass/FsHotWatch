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
    let started = Diagnostics.Stopwatch.StartNew()

    let code, _ =
        Launch.run "/bin/sleep" [ "30" ] [] (Path.GetTempPath()) (TimeSpan.FromMilliseconds 200.0)

    test <@ code <> 0 @>
    test <@ started.Elapsed < TimeSpan.FromSeconds 20.0 @>

let private request exe args =
    { Launch.LaunchRequest.Exe = exe
      Launch.LaunchRequest.Args = args
      Launch.LaunchRequest.Env = []
      Launch.LaunchRequest.WorkDir = Path.GetTempPath()
      Launch.LaunchRequest.Timeout = TimeSpan.FromMinutes 1.0 }

[<Fact>]
let ``cancelling the direct launcher kills its child and raises`` () =
    use cts = new CancellationTokenSource()
    cts.CancelAfter(TimeSpan.FromMilliseconds 200.0)
    let started = Diagnostics.Stopwatch.StartNew()

    raises<OperationCanceledException> <@ Launch.direct (request "/bin/sleep" [ "30" ]) cts.Token @>
    // Unkilled, the child would hold the launch for its full 30 seconds.
    test <@ started.Elapsed < TimeSpan.FromSeconds 20.0 @>

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
