// A kill that does not return is ESCALATED against the identity recorded at launch
// (pid + start time): SIGTERM, then SIGKILL, each followed by a bounded liveness poll.
// Only a member still running, and still provably ours, after SIGKILL stays LEAKED.
//
// Touches Logging globals (the loud-survivor test) and spawns real children, hence the
// serialized collection the other kill tests use.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.KillEscalationTests

open System
open System.Diagnostics
open Xunit
open FsHotWatch.ProcessHelper

let private t0 = DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)

let private row pid ppid zombie =
    { Pid = pid
      ParentPid = ppid
      Zombie = zombie
      Command = $"proc-%d{pid}"
      ProcessGroup = None }

/// A process in process group `group`, as the in-process table reports it.
let private inGroup group pid ppid =
    { row pid ppid false with
        ProcessGroup = Some group }

/// A fake OS: a process table, each pid's start time, and how each pid answers a signal.
/// `onSignal pid signum` returns the new table state for that pid: `Some zombie` keeps
/// it (as a zombie or not), `None` removes it.
type private FakeOs(rows: ProcessRow list, startTimes: Map<int, DateTime>, onSignal: int -> int -> bool option) =
    let mutable table = rows
    let sent = Collections.Generic.List<int * int>()

    member _.Sent = List.ofSeq sent

    member _.Ops(attempts: int) : EscalationOps =
        { ReadTable = fun () -> Ok table
          StartTimeOf =
            fun pid ->
                match Map.tryFind pid startTimes with
                | Some t -> Ok t
                | None -> Error "no such process"
          Signal =
            fun pid signum ->
                sent.Add((pid, signum))

                table <-
                    table
                    |> List.choose (fun r ->
                        if r.Pid <> pid then
                            Some r
                        else
                            onSignal pid signum |> Option.map (fun zombie -> { r with Zombie = zombie }))

                Ok()
          SignalGroup =
            fun pgid signum ->
                sent.Add((-pgid, signum))

                table <-
                    table
                    |> List.choose (fun r ->
                        if r.ProcessGroup <> Some pgid then
                            Some r
                        else
                            onSignal r.Pid signum |> Option.map (fun zombie -> { r with Zombie = zombie }))

                Ok()
          Pause = ignore
          Attempts = attempts }

/// SIGTERM is ignored; SIGKILL removes the process.
let private ignoresTerm _ signum = if signum = 9 then None else Some false

let private root = { Pid = 100; StartedAt = t0 }

// ---------------------------------------------------------------------------
// escalateWith — the policy, against a fake OS
// ---------------------------------------------------------------------------

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a tree that ignores SIGTERM is SIGKILLed and ends reaped`` () =
    let os =
        FakeOs([ row 100 1 false; row 101 100 false ], Map [ 100, t0; 101, t0.AddSeconds 1.0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<int list>([ 100; 101 ], List.sort e.Exited)
    // Both members got SIGTERM first, and SIGKILL only after it was ignored.
    Assert.Equal<(int * int) list>([ 100, 15; 101, 15; 100, 9; 101, 9 ], os.Sent)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a tree that leaves on SIGTERM is never SIGKILLed`` () =
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ _ -> None))

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<(int * int) list>([ 100, 15 ], os.Sent)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a member left a zombie by SIGKILL counts as exited — it holds nothing`` () =
    let os =
        FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ signum -> Some(signum = 9)))

    let e = escalateWith (os.Ops 2) root

    Assert.Empty(e.Survivors)
    Assert.Equal<int list>([ 100 ], e.Exited)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a recycled root pid is never signalled and is not booked as ours`` () =
    // Same pid, different process: it started a minute after the one we recorded.
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0.AddMinutes 1.0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(os.Sent)
    Assert.Equal<int list>([ 100 ], e.Recycled)
    Assert.Empty(e.Survivors)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a root already gone is reported exited without a signal`` () =
    let os = FakeOs([ row 7 1 false ], Map [ 7, t0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(os.Sent)
    Assert.Equal<int list>([ 100 ], e.Exited)
    Assert.Empty(e.Survivors)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a tree that survives SIGKILL stays a NAMED survivor`` () =
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ _ -> Some false))

    let e = escalateWith (os.Ops 2) root

    Assert.Equal<(int * int) list>([ 100, 15; 100, 9 ], os.Sent)

    match e.Survivors with
    | [ pid, reason ] ->
        Assert.Equal(100, pid)
        Assert.Contains("SIGKILL", reason)
    | other -> Assert.Fail $"expected pid 100 to survive, got %A{other}"

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a member whose ownership cannot be proven is never signalled, and stays a survivor`` () =
    // 101's start time cannot be read, so nothing proves it is still the child we found.
    let os =
        FakeOs([ row 100 1 false; row 101 100 false ], Map [ 100, t0 ], (fun _ _ -> None))

    let e = escalateWith (os.Ops 2) root

    Assert.DoesNotContain(os.Sent, fun (pid, _) -> pid = 101)

    match e.Survivors with
    | [ pid, reason ] ->
        Assert.Equal(101, pid)
        Assert.Contains("ownership", reason)
    | other -> Assert.Fail $"expected 101 as the only survivor, got %A{other}"

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a root whose ownership cannot be proven is never signalled`` () =
    let os = FakeOs([ row 100 1 false ], Map.empty, ignoresTerm)

    let e = escalateWith (os.Ops 2) root

    Assert.Empty(os.Sent)
    Assert.Equal(100, fst (Assert.Single e.Survivors))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``an unreadable table and an unreadable start time signal nothing and leave the root a survivor`` () =
    let ops =
        { ReadTable = fun () -> Error "ps did not answer"
          StartTimeOf = fun _ -> Error "no such process"
          Signal = fun _ _ -> failwith "nothing may be signalled blind"
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    match e.Survivors with
    | [ pid, reason ] ->
        Assert.Equal(100, pid)
        Assert.Contains("ps did not answer", reason)
    | other -> Assert.Fail $"expected the root as a survivor, got %A{other}"

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a table that becomes unreadable mid-escalation leaves the member a survivor`` () =
    let reads = ref 0

    let ops =
        { ReadTable =
            fun () ->
                reads.Value <- reads.Value + 1

                if reads.Value = 1 then
                    Ok [ row 100 1 false ]
                else
                    Error "ps did not answer"
          StartTimeOf = fun _ -> Ok t0
          Signal = fun _ _ -> Ok()
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Contains("ps did not answer", snd (Assert.Single e.Survivors))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a refused SIGKILL is named in the survivor's reason`` () =
    let ops =
        { ReadTable = fun () -> Ok [ row 100 1 false ]
          StartTimeOf = fun _ -> Ok t0
          Signal = fun pid signum -> Error $"kill(%d{pid}, %d{signum}) failed with errno 1"
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 1 }

    let e = escalateWith ops root

    Assert.Contains("errno 1", snd (Assert.Single e.Survivors))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a descendant recycled mid-escalation is left alone`` () =
    // 101 is ours at the snapshot; after SIGTERM its pid belongs to a newer process.
    let starts = ref (Map [ 100, t0; 101, t0 ])

    let ops =
        { ReadTable = fun () -> Ok [ row 100 1 false; row 101 100 false ]
          StartTimeOf = fun pid -> Ok(Map.find pid starts.Value)
          Signal =
            fun pid _ ->
                if pid = 101 then
                    starts.Value <- starts.Value.Add(101, t0.AddMinutes 5.0)

                Ok()
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 1 }

    let e = escalateWith ops root

    Assert.Contains(101, e.Recycled)
    Assert.DoesNotContain(e.Survivors, fun (pid, _) -> pid = 101)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a member whose start time becomes unreadable mid-escalation is not SIGKILLed`` () =
    // Ours at the snapshot and sent SIGTERM; then its start time can no longer be read,
    // so nothing proves the pid still names the same process.
    let readable = ref true
    let sent = Collections.Generic.List<int * int>()

    let ops =
        { ReadTable = fun () -> Ok [ row 100 1 false ]
          StartTimeOf = fun _ -> if readable.Value then Ok t0 else Error "no such process"
          Signal =
            fun pid signum ->
                sent.Add((pid, signum))
                readable.Value <- false
                Ok()
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Equal<(int * int) list>([ 100, 15 ], List.ofSeq sent)
    let pid, reason = Assert.Single e.Survivors
    Assert.Equal(100, pid)
    Assert.Contains("ownership", reason)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a member that exits between the table read and the start-time read is gone, not a survivor`` () =
    // The race a real `sh` loop hits: 101 is in the snapshot, but has exited by the time its
    // start time is read, and is absent from every later read.
    let reads = ref 0

    let ops =
        { ReadTable =
            fun () ->
                reads.Value <- reads.Value + 1

                if reads.Value = 1 then
                    Ok [ row 100 1 false; row 101 100 false ]
                else
                    Ok [ row 100 1 false ]
          StartTimeOf = fun pid -> if pid = 100 then Ok t0 else Error "no such process"
          Signal = fun _ _ -> Ok()
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Contains(101, e.Exited)
    Assert.DoesNotContain(e.Survivors, fun (pid, _) -> pid = 101)
    Assert.DoesNotContain(e.Sent, fun (pid, _, _) -> pid = 101)

// ---------------------------------------------------------------------------
// killTreeEscalatingWith — when escalation runs, and what it books
// ---------------------------------------------------------------------------

let private shortBudget = TimeSpan.FromMilliseconds 250.0

let private blockingKill () =
    let gate = new Threading.ManualResetEventSlim(false)
    (fun () -> gate.Wait()), gate

let private settled =
    { Root = 4242
      Sent = [ 4242, "SIGKILL", Ok() ]
      Exited = [ 4242 ]
      Recycled = []
      Survivors = [] }

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``POSITIVE CONTROL: a kill that returns takes the fast path, with no escalation`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let calls = ref 0

    let outcome =
        killTreeEscalatingWith
            shortBudget
            4242
            (fun () -> "child")
            id
            (Some(fun () ->
                calls.Value <- calls.Value + 1
                settled))

    Assert.Equal(KillOutcome.Killed, outcome)
    Assert.Equal(0, calls.Value)
    Assert.Empty(registry.Leaks)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a kill that does not return is escalated, and a reaped tree is not booked LEAKED`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let kill, gate = blockingKill ()

    try
        let outcome =
            killTreeEscalatingWith
                shortBudget
                4242
                (fun () -> "`dotnet test` (pid 4242)")
                kill
                (Some(fun () -> settled))

        Assert.Equal(KillOutcome.Killed, outcome)
        Assert.Empty(registry.Leaks)
    finally
        gate.Set()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a tree that survives escalation stays LEAKED, each survivor named`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let kill, gate = blockingKill ()

    let survived =
        { settled with
            Exited = []
            Survivors =
                [ 4242, "still running after SIGKILL"
                  4243, "its ownership could not be re-established" ] }

    try
        match
            killTreeEscalatingWith
                shortBudget
                4242
                (fun () -> "`dotnet test` (pid 4242)")
                kill
                (Some(fun () -> survived))
        with
        | KillOutcome.KillTimedOut _ -> ()
        | other -> Assert.Fail $"a tree that survived must stay unaccounted for, got %A{other}"

        match registry.Leaks with
        | [ first; second ] ->
            Assert.Equal(4242, first.Pid)
            Assert.Contains("SIGKILL", first.Reason)
            Assert.Contains("dotnet test", first.Description)
            Assert.Equal(4243, second.Pid)
        | other -> Assert.Fail $"expected both survivors booked, got %A{other}"
    finally
        gate.Set()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a survivor of escalation is logged at ERROR, naming it`` () =
    let original = FsHotWatch.Logging.logLevel
    let sb = Text.StringBuilder()
    let prevErr = Console.Error
    let kill, gate = blockingKill ()

    try
        Console.SetError(new IO.StringWriter(sb))
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

        killTreeEscalatingWith
            shortBudget
            4242
            (fun () -> "`dotnet test Unit` (pid 4242)")
            kill
            (Some(fun () ->
                { settled with
                    Survivors = [ 4242, "still running after SIGKILL" ] }))
        |> ignore

        let logged = sb.ToString()
        Assert.Contains("dotnet test Unit", logged)
        Assert.Contains("SURVIVED", logged)
        Assert.Contains("still running after SIGKILL", logged)
    finally
        gate.Set()
        Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a shutdown that lands mid-escalation names what the escalation had killed and what it had not`` () =
    // 100 ignores SIGTERM; 101 leaves on it. The escalation is held at its first poll
    // pause, after SIGTERM went to both and 101 was seen gone, and the scope shuts down.
    let os =
        FakeOs(
            [ row 100 1 false; row 101 100 false ],
            Map [ 100, t0; 101, t0.AddSeconds 1.0 ],
            fun pid signum -> if pid = 101 || signum = 9 then None else Some false
        )

    let paused = new Threading.ManualResetEventSlim(false)
    let release = new Threading.ManualResetEventSlim(false)

    let ops =
        { os.Ops 3 with
            Pause =
                fun () ->
                    paused.Set()
                    release.Wait() }

    let registry =
        FsHotWatch.ProcessRegistry.Registry(
            None,
            { Termination = TimeSpan.FromMilliseconds 100.0
              Grace = TimeSpan.FromMilliseconds 50.0
              Escalation = TimeSpan.FromMilliseconds 100.0 }
        )

    let kill, gate = blockingKill ()

    let teardown =
        Threading.Tasks.Task.Run(fun () ->
            use _ = FsHotWatch.ProcessRegistry.install registry

            killTreeEscalatingWith
                shortBudget
                100
                (fun () -> "`dotnet test` (pid 100)")
                kill
                (escalationWith ops (Ok root)))

    try
        Assert.True(paused.Wait FsHotWatch.Tests.TestHelpers.HangWaitMs, "the escalation never reached its poll")
        registry.KillAll()

        match registry.Leaks with
        | [ leak ] ->
            Assert.Equal(100, leak.Pid)
            Assert.Contains("dotnet test", leak.Description)
            Assert.Contains("SIGTERM→100", leak.Reason)
            Assert.Contains("SIGTERM→101", leak.Reason)
            Assert.Contains("exited [101]", leak.Reason)
            Assert.Contains("not yet accounted for [100]", leak.Reason)
        | other -> Assert.Fail $"expected the tree in escalation booked, got %A{other}"
    finally
        release.Set()
        gate.Set()
        teardown.Wait FsHotWatch.Tests.TestHelpers.HangWaitMs |> ignore

// ---------------------------------------------------------------------------
// Live fire: real children, the real OS operations, an injected kill that hangs.
// ---------------------------------------------------------------------------

/// Start `sh` running `script`, and wait for the line it prints once its traps are set.
let private startShell (script: string) : Process =
    let psi = ProcessStartInfo("sh", [| "-c"; script |])
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let p = Process.Start psi
    Assert.Equal("ready", p.StandardOutput.ReadLine())
    p

/// Kill a child THIS test started, if it is still running.
let private cleanUp (p: Process) =
    try
        if not p.HasExited then
            p.Kill(entireProcessTree = true)
            p.WaitForExit 5000 |> ignore
    finally
        p.Dispose()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``LIVE: a real tree that ignores SIGTERM, behind a kill that hangs, is SIGKILLed and reaped`` () =
    // `sh` and its `sleep` both ignore SIGTERM (an ignored disposition survives exec).
    let p = startShell "trap '' TERM; echo ready; while :; do sleep 1; done"

    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let kill, gate = blockingKill ()

    try
        let owned = recordOwnedPid p.Id

        Assert.True(Result.isOk owned, $"%A{owned}")

        let outcome =
            killTreeEscalatingWith
                (TimeSpan.FromMilliseconds 250.0)
                p.Id
                (fun () -> $"`sh` (pid %d{p.Id})")
                kill
                (escalationWith (SettledEscalationOps.settledOps ()) owned)

        Assert.Equal(KillOutcome.Killed, outcome)
        Assert.True(p.WaitForExit FsHotWatch.Tests.TestHelpers.HangWaitMs, "the escalated child is still running")
        // Killed by SIGKILL — the 128 + signum convention the positive control pins.
        Assert.Equal(137, p.ExitCode)
        Assert.Empty(registry.Leaks)
    finally
        gate.Set()
        cleanUp p

[<Fact(Timeout = 30000)>]
let ``LIVE: a pid whose recorded start time does not match is left alone`` () =
    let p = startShell "echo ready; while :; do sleep 1; done"

    try
        // The pid is real and running, but the identity we hold is a different process's.
        let stranger =
            { Pid = p.Id
              StartedAt = DateTime.UtcNow.AddDays -1.0 }

        let e = escalateWith (SettledEscalationOps.settledOps ()) stranger

        Assert.True((e.Recycled = [ p.Id ]), $"recycled is not [%d{p.Id}]: %A{e}")
        Assert.Empty(e.Sent)
        Assert.False(p.WaitForExit 500, "a process that was not provably ours was killed")
    finally
        cleanUp p

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``an identity that cannot be recorded means no escalation`` () =
    Assert.True((escalationFor (Error "gone")).IsNone)
    Assert.True(Result.isError (processStartTime -1))
    Assert.True(Result.isError (recordOwnedPid -1))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``signalPid reports a pid it could not signal`` () =
    // Signal 0 delivers nothing, and no process holds the largest pid.
    match signalPid Int32.MaxValue 0 with
    | Error reason -> Assert.Contains("errno", reason)
    | Ok() -> Assert.Fail "a signal to a pid that does not exist was reported as delivered"

// ---------------------------------------------------------------------------
// A kill that hangs is exactly when the box is too loaded for `ps` to answer. Escalation
// reads the table inside this process, so the tree is still reaped (AUTOMATION-1153).
// ---------------------------------------------------------------------------

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the escalation table is read in process and never starts ps when that succeeds`` () =
    let row' =
        { Pid = 7
          ParentPid = 1
          Zombie = false
          Command = "x"
          ProcessGroup = None }

    let table =
        readEscalationTable (fun () -> Ok [ row' ]) (fun () -> failwith "ps must not be started")

    Assert.Equal<ProcessRow list>([ row' ], Result.defaultValue [] table)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the escalation table falls back to ps only when the in-process read is unavailable`` () =
    let table =
        readEscalationTable (fun () -> Error "no table here") (fun () -> Ok [ row 9 1 false ])

    Assert.Equal<ProcessRow list>([ row 9 1 false ], Result.defaultValue [] table)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a hung tree is still reaped when ps does not answer`` () =
    // The field incident: every `ps` the escalation starts misses its budget. The kernel
    // still knows the tree, and SIGKILL reaps root and child.
    let os =
        FakeOs([ row 100 1 false; row 200 100 false ], Map.ofList [ 100, t0; 200, t0 ], ignoresTerm)

    let ops = os.Ops 2

    let e =
        escalateWith
            { ops with
                ReadTable =
                    fun () -> readEscalationTable ops.ReadTable (fun () -> Error "`ps` did not answer within 2s") }
            root

    Assert.Empty(e.Survivors)
    Assert.Contains((100, 9), os.Sent)
    Assert.Contains((200, 9), os.Sent)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the live escalation table reads a real process tree without starting ps`` () =
    // `sh` stays alive waiting for its `sleep` child, so both rows are in the table.
    use parent =
        Process.Start(
            ProcessStartInfo("sh", [ "-c"; "sleep 30 & wait" ], RedirectStandardOutput = true, UseShellExecute = false)
        )

    try
        let childOfParent () =
            match liveEscalationOps.ReadTable() with
            | Ok rows ->
                rows |> List.exists (fun r -> r.Pid = parent.Id)
                && rows |> List.exists (fun r -> r.ParentPid = parent.Id && not r.Zombie)
            | Error reason -> failwith reason

        Assert.True(FsHotWatch.Tests.TestHelpers.waitUntilTrue childOfParent 10000)

        let tree = liveEscalationOps.ReadTable() |> Result.map (treeOf parent.Id)
        Assert.Equal(2, (Result.defaultValue [] tree).Length)
    finally
        parent.Kill true
        parent.WaitForExit()

// ---------------------------------------------------------------------------
// The process group: every member, however it was re-parented, and with no `ps`
// ---------------------------------------------------------------------------

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``an unreadable process table still signals a root whose start time proves it ours`` () =
    let sent = Collections.Generic.List<int * int>()

    let ops =
        { ReadTable = fun () -> Error "ps did not answer"
          StartTimeOf = fun _ -> Ok t0
          Signal =
            fun pid signum ->
                sent.Add((pid, signum))
                Ok()
          SignalGroup = fun _ _ -> Error "no group in this table"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Equal<(int * int) list>([ 100, 15; 100, 9 ], List.ofSeq sent)
    Assert.Equal(100, fst (Assert.Single e.Survivors))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a root already gone still has its live process group reaped`` () =
    // The root has left; 200 was re-parented to init but stays in the root's group.
    let os = FakeOs([ inGroup 100 200 1 ], Map [ 200, t0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<(int * int) list>([ -100, 15; -100, 9 ], os.Sent)
    Assert.Equal<int list>([ 100; 200 ], List.sort e.Exited)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a group is signalled once, and a member that left the group is signalled by pid`` () =
    let os =
        FakeOs(
            [ inGroup 100 100 1; inGroup 100 101 100; row 102 100 false ],
            Map [ 100, t0; 101, t0; 102, t0 ],
            ignoresTerm
        )

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<(int * int) list>([ -100, 15; 102, 15; -100, 9; 102, 9 ], os.Sent)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the group of a recycled leader is never signalled`` () =
    // The pid and the group now belong to a process started a minute later.
    let os =
        FakeOs([ inGroup 100 100 1; inGroup 100 300 100 ], Map [ 100, t0.AddMinutes 1.0; 300, t0.AddMinutes 1.0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(os.Sent)
    Assert.Equal<int list>([ 100 ], e.Recycled)
    Assert.Empty(e.Survivors)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``signalGroup names no group for pgid 0 or 1`` () =
    Assert.True(Result.isError (signalGroup 0 9))
    Assert.True(Result.isError (signalGroup 1 9))

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a child leads its own process group through perl, with its command and arguments intact`` () =
    let psi = ProcessStartInfo("/opt/my tools/dotnet", "test --no-build")

    let wrapped = ownProcessGroupWith (Some "/usr/bin/perl") false Some psi

    Assert.Equal("/usr/bin/perl", wrapped.FileName)
    Assert.Equal(
        "-e \"setpgrp(0,0);$p=shift;exec{$p}@ARGV;die qq($p: $!)\" -- \"/opt/my tools/dotnet\" \"/opt/my tools/dotnet\" test --no-build",
        wrapped.Arguments
    )

[<Theory(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a child is left in the caller's group when perl is absent or the caller has a terminal`` (hasPerl: bool) =
    let interactive = hasPerl
    let psi = ProcessStartInfo("/bin/dotnet", "test")

    let wrapped =
        ownProcessGroupWith (if hasPerl then Some "/usr/bin/perl" else None) interactive Some psi

    Assert.Equal("/bin/dotnet", wrapped.FileName)
    Assert.Equal("test", wrapped.Arguments)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a command that cannot be started is left for Process.Start to refuse`` () =
    let psi = ProcessStartInfo("/nonexistent/fshw-no-such-command", "x")

    let wrapped = ownProcessGroupWith (Some "/usr/bin/perl") false startableCommand psi

    Assert.Equal("/nonexistent/fshw-no-such-command", wrapped.FileName)
    Assert.Equal("x", wrapped.Arguments)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a bare command resolves to its absolute path on PATH`` () =
    let resolved = startableCommand "sh"
    Assert.True(resolved |> Option.exists IO.Path.IsPathRooted, $"%A{resolved}")

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
[<WallClock("LIVE: a real tree and real signals — the claim is about the real OS, so it runs on real time; every wait in it is a named hang budget")>]
let ``LIVE: a grandchild that ignores SIGTERM, left by a root already gone, is reaped through the process group`` () =
    // The launch goes through the same shim every spawn does, so the group is real.
    let psi =
        ProcessStartInfo("sh", "-c \"(trap '' TERM; while :; do sleep 1; done) > /dev/null 2>&1 & echo $!; read _; exit 0\"")

    psi.RedirectStandardOutput <- true
    psi.RedirectStandardInput <- true
    psi.UseShellExecute <- false

    let p = Process.Start(ownProcessGroupWith (Some "/usr/bin/perl") false startableCommand psi)
    let grandchild = int (p.StandardOutput.ReadLine())

    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let kill, gate = blockingKill ()

    try
        let owned = recordOwnedPid p.Id
        Assert.True(Result.isOk owned, $"%A{owned}")

        // The root is gone before escalation looks: nothing links the grandchild to it.
        p.StandardInput.Close()
        Assert.True(p.WaitForExit FsHotWatch.Tests.TestHelpers.HangWaitMs, "the root did not exit")
        Assert.Equal(Liveness.Alive, isProcessAlive grandchild)

        let outcome =
            killTreeEscalatingWith
                (TimeSpan.FromMilliseconds 250.0)
                p.Id
                (fun () -> $"`sh` (pid %d{p.Id})")
                kill
                (escalationWith (SettledEscalationOps.settledOps ()) owned)

        Assert.Equal(KillOutcome.Killed, outcome)

        Assert.True(
            Threading.SpinWait.SpinUntil((fun () -> isProcessAlive grandchild = Liveness.Gone), FsHotWatch.Tests.TestHelpers.HangWaitMs),
            "the grandchild is still running"
        )

        Assert.Empty(registry.Leaks)
    finally
        gate.Set()
        signalPid grandchild 9 |> ignore
        cleanUp p
