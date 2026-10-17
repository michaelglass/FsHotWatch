// A shutdown whose kill of a tracked child does not return ESCALATES through the child
// (`IOwnedChild.Escalate`), against the identity recorded at launch: SIGTERM, then
// SIGKILL, member by member. Only a member escalation leaves running stays LEAKED.
//
// Spawns a real child and reads log lines, hence the serialized collection the other
// kill tests use.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ShutdownEscalationTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open Xunit
open FsHotWatch
open FsHotWatch.ProcessRegistry

/// Budgets short enough that a hung kill costs a test a fraction of a second.
let private fast =
    { Termination = TimeSpan.FromMilliseconds 100.0
      Grace = TimeSpan.FromMilliseconds 50.0
      Escalation = TimeSpan.FromSeconds 2.0 }

/// An owned child whose observation, kill and escalation are scripted. `kill` runs on
/// the teardown's thread; a kill that blocks is the hung kill under test.
type private FakeChild(pid: int, kill: unit -> unit, escalate: unit -> (int * string) list option) =
    let mutable exited = false
    let mutable escalations = 0

    member _.Escalations = Volatile.Read(&escalations)

    interface IOwnedChild with
        member _.Pid = pid

        member _.Observe() =
            if Volatile.Read(&exited) then
                ExitObservation.Exited
            else
                ExitObservation.Running

        member _.KillTree() =
            kill ()
            Volatile.Write(&exited, true)

        member _.WaitForExit _ = ()

        member _.Escalate() =
            Interlocked.Increment(&escalations) |> ignore
            escalate ()

/// A kill that blocks until the test releases it.
let private hungKill () =
    let gate = new ManualResetEventSlim(false)
    (fun () -> gate.Wait()), gate

let private captureLogs () =
    let lines = ConcurrentQueue<string>()

    let sink =
        Logging.installSink
            { Write = lines.Enqueue
              Level = Logging.LogLevel.Debug
              File = None }

    lines, sink

let private shutDownWith (child: IOwnedChild) : Registry =
    let registry = Registry(None, fast)
    Assert.True(registry.AdmitChild(obj (), child))
    registry.KillAll()
    registry

[<Fact(Timeout = 10000)>]
let ``POSITIVE CONTROL: a shutdown whose kill returns never escalates`` () =
    let child = FakeChild(4242, ignore, (fun () -> Some []))
    let registry = shutDownWith child

    Assert.Equal(0, child.Escalations)
    Assert.Empty(registry.Leaks)

[<Fact(Timeout = 10000)>]
let ``a hung kill at shutdown is escalated, and a tree escalation saw leave is not LEAKED`` () =
    let kill, gate = hungKill ()
    let lines, sink = captureLogs ()

    try
        let child = FakeChild(4242, kill, (fun () -> Some []))
        let registry = shutDownWith child

        Assert.Equal(1, child.Escalations)
        Assert.Empty(registry.Leaks)
        Assert.Contains(lines, fun line -> line.Contains "ESCALATED tracked child pid 4242")
    finally
        sink.Dispose()
        gate.Set()

[<Fact(Timeout = 10000)>]
let ``a survivor of escalation at shutdown stays LEAKED and loud, each by its own pid`` () =
    let kill, gate = hungKill ()
    let lines, sink = captureLogs ()

    try
        let survivors =
            [ 4242, "still running after SIGKILL"
              4243, "its ownership could not be re-established" ]

        let registry = shutDownWith (FakeChild(4242, kill, (fun () -> Some survivors)))

        match registry.Leaks with
        | [ first; second ] ->
            Assert.Equal(4242, first.Pid)
            Assert.Contains("did not return", first.Reason)
            Assert.Contains("still running after SIGKILL", first.Reason)
            Assert.Equal(4243, second.Pid)
            Assert.Contains("in the tree of tracked child pid 4242", second.Description)
        | other -> Assert.Fail $"expected both survivors booked, got %A{other}"

        Assert.Contains(lines, fun line -> line.Contains "SURVIVED escalation")
        Assert.Contains(lines, fun line -> line.Contains "LEAKED process tree: pid 4243")
    finally
        sink.Dispose()
        gate.Set()

[<Fact(Timeout = 10000)>]
let ``a hung kill with no recorded identity is LEAKED without escalating`` () =
    let kill, gate = hungKill ()

    try
        let registry = shutDownWith (FakeChild(4242, kill, (fun () -> None)))

        match registry.Leaks with
        | [ leak ] ->
            Assert.Equal(4242, leak.Pid)
            Assert.Contains("did not return", leak.Reason)
            Assert.DoesNotContain("escalation", leak.Reason)
        | other -> Assert.Fail $"expected the hung child booked, got %A{other}"
    finally
        gate.Set()

[<Fact(Timeout = 10000)>]
let ``an escalation that throws leaves the child LEAKED, saying so`` () =
    let kill, gate = hungKill ()

    try
        let registry =
            shutDownWith (FakeChild(4242, kill, (fun () -> raise (InvalidOperationException "no process table"))))

        match registry.Leaks with
        | [ leak ] -> Assert.Contains("escalation threw InvalidOperationException: no process table", leak.Reason)
        | other -> Assert.Fail $"expected the hung child booked, got %A{other}"
    finally
        gate.Set()

[<Fact(Timeout = 10000)>]
let ``an escalation that does not finish is not waited on past its budget`` () =
    let kill, gate = hungKill ()
    let stuck = new ManualResetEventSlim(false)

    try
        let child =
            FakeChild(
                4242,
                kill,
                fun () ->
                    stuck.Wait()
                    Some []
            )

        let registry =
            Registry(
                None,
                { fast with
                    Escalation = TimeSpan.FromMilliseconds 100.0 }
            )

        Assert.True(registry.AdmitChild(obj (), child))
        registry.KillAll()

        match registry.Leaks with
        | [ leak ] -> Assert.Contains("escalation did not finish", leak.Reason)
        | other -> Assert.Fail $"expected the hung child booked, got %A{other}"
    finally
        stuck.Set()
        gate.Set()

[<Fact(Timeout = 10000)>]
let ``a child refused at admission whose kill hangs is escalated too`` () =
    let kill, gate = hungKill ()

    try
        let registry = Registry(None, fast)
        registry.KillAll()

        let child =
            FakeChild(4242, kill, (fun () -> Some [ 4243, "still running after SIGKILL" ]))

        Assert.False(registry.AdmitChild(obj (), child))
        Assert.Equal(1, child.Escalations)

        match registry.Leaks with
        | [ leak ] -> Assert.Equal(4243, leak.Pid)
        | other -> Assert.Fail $"expected the survivor booked, got %A{other}"
    finally
        gate.Set()

[<Fact>]
let ``no escalation leaves the child as it was`` () =
    let child = FakeChild(4242, ignore, (fun () -> None)) :> IOwnedChild
    Assert.Same(child, escalatingWith None child)
    // A child registered by `Process` alone has no recorded identity to escalate against.
    use self = Process.GetCurrentProcess()
    Assert.True((ownedProcess self).Escalate().IsNone)

// ---------------------------------------------------------------------------
// Live fire: a real tree, the real escalation, an injected kill that hangs.
// ---------------------------------------------------------------------------

/// `p` as an owned child whose kill hangs: everything else is the real process.
let private hangingKillOf (p: Process) (kill: unit -> unit) : IOwnedChild =
    { new IOwnedChild with
        member _.Pid = p.Id

        member _.Observe() =
            if p.HasExited then
                ExitObservation.Exited
            else
                ExitObservation.Running

        member _.KillTree() = kill ()
        member _.WaitForExit milliseconds = p.WaitForExit milliseconds |> ignore
        member _.Escalate() = None }

[<Fact(Timeout = 30000)>]
let ``LIVE: a real tree that ignores SIGTERM, behind a kill that hangs at shutdown, is SIGKILLed and reaped`` () =
    // `sh` and its `sleep` both ignore SIGTERM (an ignored disposition survives exec).
    let psi =
        ProcessStartInfo("sh", [| "-c"; "trap '' TERM; echo ready; while :; do sleep 1; done" |])

    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let p = Process.Start psi
    let kill, gate = hungKill ()

    try
        Assert.Equal("ready", p.StandardOutput.ReadLine())
        let owned = ProcessHelper.recordOwnedPid p.Id
        Assert.True(Result.isOk owned, $"%A{owned}")

        let child =
            escalatingWith (ProcessHelper.shutdownEscalationFor owned) (hangingKillOf p kill)

        let registry =
            Registry(
                None,
                { fast with
                    Escalation = TimeSpan.FromSeconds 15.0 }
            )

        Assert.True(registry.AdmitChild(p, child))
        registry.KillAll()

        Assert.True(p.WaitForExit 5000, "the escalated child is still running")
        // Killed by SIGKILL — the 128 + signum convention.
        Assert.Equal(137, p.ExitCode)
        Assert.Empty(registry.Leaks)
    finally
        gate.Set()

        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)
                p.WaitForExit 5000 |> ignore
        finally
            p.Dispose()
