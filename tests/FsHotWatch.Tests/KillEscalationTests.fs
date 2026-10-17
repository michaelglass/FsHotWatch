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
      Command = $"proc-%d{pid}" }

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
          Pause = ignore
          Attempts = attempts }

/// SIGTERM is ignored; SIGKILL removes the process.
let private ignoresTerm _ signum = if signum = 9 then None else Some false

let private root = { Pid = 100; StartedAt = t0 }

// ---------------------------------------------------------------------------
// escalateWith — the policy, against a fake OS
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``a tree that ignores SIGTERM is SIGKILLed and ends reaped`` () =
    let os =
        FakeOs([ row 100 1 false; row 101 100 false ], Map [ 100, t0; 101, t0.AddSeconds 1.0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<int list>([ 100; 101 ], List.sort e.Exited)
    // Both members got SIGTERM first, and SIGKILL only after it was ignored.
    Assert.Equal<(int * int) list>([ 100, 15; 101, 15; 100, 9; 101, 9 ], os.Sent)

[<Fact(Timeout = 5000)>]
let ``a tree that leaves on SIGTERM is never SIGKILLed`` () =
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ _ -> None))

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(e.Survivors)
    Assert.Equal<(int * int) list>([ 100, 15 ], os.Sent)

[<Fact(Timeout = 5000)>]
let ``a member left a zombie by SIGKILL counts as exited — it holds nothing`` () =
    let os =
        FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ signum -> Some(signum = 9)))

    let e = escalateWith (os.Ops 2) root

    Assert.Empty(e.Survivors)
    Assert.Equal<int list>([ 100 ], e.Exited)

[<Fact(Timeout = 5000)>]
let ``a recycled root pid is never signalled and is not booked as ours`` () =
    // Same pid, different process: it started a minute after the one we recorded.
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0.AddMinutes 1.0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(os.Sent)
    Assert.Equal<int list>([ 100 ], e.Recycled)
    Assert.Empty(e.Survivors)

[<Fact(Timeout = 5000)>]
let ``a root already gone is reported exited without a signal`` () =
    let os = FakeOs([ row 7 1 false ], Map [ 7, t0 ], ignoresTerm)

    let e = escalateWith (os.Ops 3) root

    Assert.Empty(os.Sent)
    Assert.Equal<int list>([ 100 ], e.Exited)
    Assert.Empty(e.Survivors)

[<Fact(Timeout = 5000)>]
let ``a tree that survives SIGKILL stays a NAMED survivor`` () =
    let os = FakeOs([ row 100 1 false ], Map [ 100, t0 ], (fun _ _ -> Some false))

    let e = escalateWith (os.Ops 2) root

    Assert.Equal<(int * int) list>([ 100, 15; 100, 9 ], os.Sent)

    match e.Survivors with
    | [ pid, reason ] ->
        Assert.Equal(100, pid)
        Assert.Contains("SIGKILL", reason)
    | other -> Assert.Fail $"expected pid 100 to survive, got %A{other}"

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``a root whose ownership cannot be proven is never signalled`` () =
    let os = FakeOs([ row 100 1 false ], Map.empty, ignoresTerm)

    let e = escalateWith (os.Ops 2) root

    Assert.Empty(os.Sent)
    Assert.Equal(100, fst (Assert.Single e.Survivors))

[<Fact(Timeout = 5000)>]
let ``an unreadable process table signals nothing and leaves the root a survivor`` () =
    let ops =
        { ReadTable = fun () -> Error "ps did not answer"
          StartTimeOf = fun _ -> Ok t0
          Signal = fun _ _ -> failwith "nothing may be signalled blind"
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    match e.Survivors with
    | [ pid, reason ] ->
        Assert.Equal(100, pid)
        Assert.Contains("ps did not answer", reason)
    | other -> Assert.Fail $"expected the root as a survivor, got %A{other}"

[<Fact(Timeout = 5000)>]
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
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Contains("ps did not answer", snd (Assert.Single e.Survivors))

[<Fact(Timeout = 5000)>]
let ``a refused SIGKILL is named in the survivor's reason`` () =
    let ops =
        { ReadTable = fun () -> Ok [ row 100 1 false ]
          StartTimeOf = fun _ -> Ok t0
          Signal = fun pid signum -> Error $"kill(%d{pid}, %d{signum}) failed with errno 1"
          Pause = ignore
          Attempts = 1 }

    let e = escalateWith ops root

    Assert.Contains("errno 1", snd (Assert.Single e.Survivors))

[<Fact(Timeout = 5000)>]
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
          Pause = ignore
          Attempts = 1 }

    let e = escalateWith ops root

    Assert.Contains(101, e.Recycled)
    Assert.DoesNotContain(e.Survivors, fun (pid, _) -> pid = 101)

[<Fact(Timeout = 5000)>]
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
          Pause = ignore
          Attempts = 2 }

    let e = escalateWith ops root

    Assert.Equal<(int * int) list>([ 100, 15 ], List.ofSeq sent)
    let pid, reason = Assert.Single e.Survivors
    Assert.Equal(100, pid)
    Assert.Contains("ownership", reason)

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 30000)>]
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
                (escalationFor owned)

        Assert.Equal(KillOutcome.Killed, outcome)
        Assert.True(p.WaitForExit 5000, "the escalated child is still running")
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

        let e = escalateWith liveEscalationOps stranger

        Assert.Equal<int list>([ p.Id ], e.Recycled)
        Assert.Empty(e.Sent)
        Assert.False(p.WaitForExit 500, "a process that was not provably ours was killed")
    finally
        cleanUp p

[<Fact(Timeout = 5000)>]
let ``an identity that cannot be recorded means no escalation`` () =
    Assert.True((escalationFor (Error "gone")).IsNone)
    Assert.True(Result.isError (processStartTime -1))
    Assert.True(Result.isError (recordOwnedPid -1))

[<Fact(Timeout = 5000)>]
let ``signalPid reports a pid it could not signal`` () =
    // Signal 0 delivers nothing, and no process holds the largest pid.
    match signalPid Int32.MaxValue 0 with
    | Error reason -> Assert.Contains("errno", reason)
    | Ok() -> Assert.Fail "a signal to a pid that does not exist was reported as delivered"
