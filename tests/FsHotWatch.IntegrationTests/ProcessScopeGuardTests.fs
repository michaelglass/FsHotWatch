module FsHotWatch.Tests.ProcessScopeGuardTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ProcessScopeGuard

let private alive (pid: int) =
    try
        use p = Process.GetProcessById pid
        not p.HasExited
    with _ ->
        false

/// Start `sleep 30` from a pool thread, which carries the caller's context, and
/// return its pid once it has been admitted. The run returns when the child is reaped.
let private startSleeper () : int * Task =
    let started = TaskCompletionSource<int>()

    let run =
        Task.Run(fun () ->
            runProcessObserved
                started.SetResult
                "sleep"
                "30"
                (Path.GetTempPath())
                []
                (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
            |> ignore)

    started.Task.Result, run

let private spawnsIntoTheTestScope () =
    if not (OperatingSystem.IsWindows()) then
        let pid, _run = startSleeper ()
        // The per-test scope reaps it after the test.
        test <@ ProcessRegistry.snapshot () |> List.exists (fun p -> p.Id = pid) @>

[<Fact>]
let ``a test's own spawn is admitted to the per-test scope`` () = spawnsIntoTheTestScope ()

[<Fact(Timeout = 30000)>]
let ``a test with a timeout spawns into the per-test scope too`` () = spawnsIntoTheTestScope ()

[<Fact(Timeout = 30000)>]
let ``closing a scope reaps what is still running in it`` () =
    if not (OperatingSystem.IsWindows()) then
        let scope = openScope ()
        let pid, run = startSleeper ()
        close scope
        test <@ waitUntilTrue (fun () -> not (alive pid)) 5000 @>
        test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>

[<Fact>]
let ``closing a scope fails when a spawn in it had no registry`` () =
    let scope = openScope ()
    scope.Lines.Enqueue $"  [process-registry] spawned pid 1 with %s{UnscopedMarker}"
    let ex = Assert.ThrowsAny<exn>(fun () -> close scope)
    test <@ ex.Message.Contains "spawned pid 1" @>

[<Fact>]
let ``a scope records warnings whatever the stderr verbosity`` () =
    let scope = openScope ()

    try
        Logging.warn "scope-guard-test" "recorded"
        test <@ scope.Lines |> Seq.exists (fun l -> l.Contains "recorded") @>
    finally
        close scope
