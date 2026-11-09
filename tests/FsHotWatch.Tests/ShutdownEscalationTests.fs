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
type private FakeChild(pid: int, kill: unit -> unit, escalate: unit -> EscalationResult) =
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

// A shutdown budget shorter than the escalation it waits on books a tree that is being
// reaped as LEAKED, which is the outcome this escalation exists to remove.
[<Fact(Timeout = 10000)>]
let ``the shutdown escalation budget covers the escalation.s own worst case`` () =
    Assert.True(
        EscalationBudget >= escalationWorstCase,
        $"budget %O{EscalationBudget} is below the escalation worst case %O{escalationWorstCase}"
    )

let private shutDownWith (child: IOwnedChild) : Registry =
    let registry = Registry(None, fast)
    Assert.True(registry.AdmitChild(obj (), child))
    registry.KillAll()
    registry

[<Fact(Timeout = 10000)>]
let ``POSITIVE CONTROL: a shutdown whose kill returns never escalates`` () =
    let child = FakeChild(4242, ignore, (fun () -> EscalationResult.TreeGone))
    let registry = shutDownWith child

    Assert.Equal(0, child.Escalations)
    Assert.Empty(registry.Leaks)

[<Fact(Timeout = 10000)>]
let ``a hung kill at shutdown is escalated, and a tree escalation saw leave is not LEAKED`` () =
    let kill, gate = hungKill ()
    let lines, sink = captureLogs ()

    try
        let child = FakeChild(4242, kill, (fun () -> EscalationResult.TreeGone))
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

        let registry =
            shutDownWith (FakeChild(4242, kill, (fun () -> EscalationResult.Survived survivors)))

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
        let registry =
            shutDownWith (FakeChild(4242, kill, (fun () -> EscalationResult.NoIdentity)))

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
                    EscalationResult.TreeGone
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
            FakeChild(4242, kill, (fun () -> EscalationResult.Survived [ 4243, "still running after SIGKILL" ]))

        Assert.False(registry.AdmitChild(obj (), child))
        Assert.Equal(1, child.Escalations)

        match registry.Leaks with
        | [ leak ] -> Assert.Equal(4243, leak.Pid)
        | other -> Assert.Fail $"expected the survivor booked, got %A{other}"
    finally
        gate.Set()

[<Fact>]
let ``an escalation that left no member running is a gone tree, never an empty survivor list`` () =
    Assert.Equal(EscalationResult.TreeGone, EscalationResult.ofSurvivors [])

    Assert.Equal(
        EscalationResult.Survived [ 4243, "still running after SIGKILL" ],
        EscalationResult.ofSurvivors [ 4243, "still running after SIGKILL" ]
    )

[<Fact>]
let ``no escalation leaves the child as it was`` () =
    let child =
        FakeChild(4242, ignore, (fun () -> EscalationResult.NoIdentity)) :> IOwnedChild

    Assert.Same(child, escalatingWith None child)
    // A child registered by `Process` alone has no recorded identity to escalate against.
    use self = Process.GetCurrentProcess()
    Assert.Equal(EscalationResult.NoIdentity, (ownedProcess self).Escalate())

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
        member _.Escalate() = EscalationResult.NoIdentity }

// AUTOMATION-1354: this test once failed under a loaded full run, its 15 s escalation
// unfinished. The cause is the one AUTOMATION-1312 measured for the kill-side LIVE tests:
// `liveEscalationOps` bounds each process-table read at 2 s, a loaded box misses it, and a
// missed read leaves the tree unproven, which escalation rightly never signals — so ten
// polls of missed reads outlast the budget. The escalation now runs on
// `SettledEscalationOps` (retry the read until `ps` answers; after SIGKILL, wait for the
// members to be gone), and the registry's budget only ends a hang. What the test proves is
// unchanged: a real SIGTERM-ignoring tree, behind a kill that hangs, is SIGKILLed (137) and
// reaped, and nothing is LEAKED.
[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
[<WallClock("LIVE: a real sh tree, real signals and the real process table — the claim is about the real OS, so it runs on real time; every wait in it is a named hang budget")>]
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
            escalatingWith
                (ProcessHelper.shutdownEscalationWith (SettledEscalationOps.settledOps ()) owned)
                (hangingKillOf p kill)

        let registry =
            Registry(
                None,
                { fast with
                    Escalation = FsHotWatch.Tests.TestHelpers.HangWait }
            )

        Assert.True(registry.AdmitChild(p, child))
        registry.KillAll()

        Assert.True(p.WaitForExit FsHotWatch.Tests.TestHelpers.HangWaitMs, "the escalated child is still running")
        // Killed by SIGKILL — the 128 + signum convention.
        Assert.Equal(137, p.ExitCode)
        Assert.Empty(registry.Leaks)
    finally
        gate.Set()

        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)
                p.WaitForExit FsHotWatch.Tests.TestHelpers.HangWaitMs |> ignore
        finally
            p.Dispose()

// ---------------------------------------------------------------------------
// A shutdown that lands DURING a kill escalation (AUTOMATION-1425). The escalation's own
// outcome line is written when it finishes; a shutdown that does not wait for it would
// end the process first, and the tree would go unreported.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``a shutdown during a kill escalation names the tree, what it killed and what it had not`` () =
    let lines, sink = captureLogs ()

    try
        let registry =
            Registry(
                None,
                { fast with
                    Escalation = TimeSpan.FromMilliseconds 100.0 }
            )

        use _ = install registry
        use _escalation = beginEscalation 4242 "`dotnet test` (pid 4242)"
        reportEscalationProgress 4242 "sent [SIGTERM→4242, SIGTERM→4243]; exited [4243]; not yet accounted for [4242]"

        registry.KillAll()

        match registry.Leaks with
        | [ leak ] ->
            Assert.Equal(4242, leak.Pid)
            Assert.Contains("dotnet test", leak.Description)
            Assert.Contains("kill escalation", leak.Reason)
            Assert.Contains("exited [4243]", leak.Reason)
            Assert.Contains("not yet accounted for [4242]", leak.Reason)
        | other -> Assert.Fail $"expected the escalated tree booked, got %A{other}"

        Assert.Contains(lines, fun line -> line.Contains "LEAKED process tree: pid 4242")
    finally
        sink.Dispose()

[<Fact(Timeout = 10000)>]
let ``POSITIVE CONTROL: a finished kill escalation leaves nothing for shutdown to name`` () =
    let registry = Registry(None, fast)
    use _ = install registry

    do
        use _escalation = beginEscalation 4242 "`dotnet test` (pid 4242)"
        ()

    registry.KillAll()
    Assert.Empty(registry.Leaks)

[<Fact(Timeout = 10000)>]
let ``a kill escalation in an operation's scope is named once, by whichever shutdown books it`` () =
    let daemon =
        Registry(
            None,
            { fast with
                Escalation = TimeSpan.FromMilliseconds 50.0 }
        )

    let operation =
        Registry(
            Some daemon,
            { fast with
                Escalation = TimeSpan.FromMilliseconds 50.0 }
        )

    use _ = install operation
    use _escalation = beginEscalation 4242 "`dotnet test` (pid 4242)"

    operation.KillAll()
    daemon.KillAll()

    Assert.Equal(1, operation.Leaks.Length)
    // The operation's leak is forwarded to the daemon, which does not book it a second time.
    Assert.Equal(1, daemon.Leaks.Length)
