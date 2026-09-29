/// What an overrunning build REPORTS: which command/root it was building, which
/// process tree the kill was aimed at, and which members of that tree survived it.
///
/// Everything here is pure (the process table and the kill are injected), so the
/// tree-kill-that-throws / blocks / leaves-a-survivor arms are deterministic. The
/// live-fire counterparts spawn real `sh` trees and live in
/// FsHotWatch.IntegrationTests/PluginTimeoutTests.fs.
module FsHotWatch.Tests.BuildOverrunTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.Build.BuildPlugin

let private row pid ppid command : ProcessRow =
    { Pid = pid
      ParentPid = ppid
      Zombie = false
      Command = command }

// ---------------------------------------------------------------------------
// parseProcessTable — `ps -A -o pid=,ppid=,stat=,command=`

[<Fact>]
let ``parseProcessTable reads pid, parent, zombie state and the full command`` () =
    let text =
        "  101     1 Ss   /sbin/launchd\n\
         45128 45000 S    sh -c echo hi; sleep 60 & sleep 60\n\
         45130 45128 Z    (sleep)\n\
         \n\
         garbage line\n"

    test
        <@
            parseProcessTable text = [ row 101 1 "/sbin/launchd"
                                       row 45128 45000 "sh -c echo hi; sleep 60 & sleep 60"
                                       { row 45130 45128 "(sleep)" with
                                           Zombie = true } ]
        @>

// ---------------------------------------------------------------------------
// treeOf — the root and every descendant, by parent pid

[<Fact>]
let ``treeOf names the root first and then every descendant, transitively`` () =
    let rows =
        [ row 1 0 "launchd"
          row 10 1 "sh -c build"
          row 11 10 "dotnet build"
          row 12 11 "MSBuild node"
          row 20 1 "unrelated" ]

    test <@ treeOf 10 rows |> List.map _.Pid = [ 10; 11; 12 ] @>

[<Fact>]
let ``treeOf of a root that is not in the table is its descendants only`` () =
    test <@ treeOf 10 [ row 11 10 "orphan-to-be" ] |> List.map _.Pid = [ 11 ] @>

[<Fact>]
let ``treeOf leaves out a zombie — it has already exited`` () =
    let rows =
        [ row 10 1 "sh -c x"
          { row 11 10 "(sleep)" with
              Zombie = true } ]

    test <@ treeOf 10 rows |> List.map _.Pid = [ 10 ] @>

// ---------------------------------------------------------------------------
// isProcessAlive — kill(pid, 0), never `ps -p`'s exit code

[<Fact>]
let ``isProcessAlive says a live process is alive`` () =
    test <@ isProcessAlive Environment.ProcessId = Ok true @>

[<Fact>]
let ``isProcessAlive says an exited, reaped process is gone`` () =
    use p =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("true", UseShellExecute = false))

    p.WaitForExit()
    test <@ isProcessAlive p.Id = Ok false @>

// ---------------------------------------------------------------------------
// accountTeardown — snapshot, kill, poll liveness, name survivors

let private noPause () = ()

[<Fact>]
let ``settlePause waits the settle pause between liveness polls`` () =
    // Production's pause must actually wait: a zero pause spends the whole settle
    // window in microseconds and names a dying process as a survivor.
    let clock = Diagnostics.Stopwatch.StartNew()
    settlePause ()
    test <@ clock.Elapsed >= SettlePause - TimeSpan.FromMilliseconds 1.0 @>


let private table rows () = Ok rows

/// A liveness probe backed by a set the test mutates.
let private aliveIn (alive: Set<int> ref) (pid: int) = Ok(alive.Value.Contains pid)

[<Fact>]
let ``accountTeardown: a clean kill names the tree and no survivors`` () =
    let alive = ref (Set.ofList [ 10; 11 ])

    let t =
        accountTeardown (table [ row 10 1 "sh -c x"; row 11 10 "sleep 60" ]) (aliveIn alive) 3 noPause 10 (fun () ->
            alive.Value <- Set.empty
            KillOutcome.Killed)

    test <@ t.RootPid = 10 @>
    test <@ t.Tree = Ok [ row 10 1 "sh -c x"; row 11 10 "sleep 60" ] @>
    test <@ t.Survivors = Ok [] @>
    test <@ t.Kill = KillOutcome.Killed @>

[<Fact>]
let ``accountTeardown: a descendant that outlives the kill is named`` () =
    let alive = ref (Set.ofList [ 10; 11 ])

    let t =
        accountTeardown (table [ row 10 1 "sh -c x"; row 11 10 "sleep 60" ]) (aliveIn alive) 3 noPause 10 (fun () ->
            // Only the root dies: the child is re-parented and keeps running.
            alive.Value <- Set.ofList [ 11 ]
            KillOutcome.Killed)

    test <@ t.Survivors = Ok [ row 11 10 "sleep 60" ] @>

[<Fact>]
let ``accountTeardown: stops polling the moment the tree is gone`` () =
    let alive = ref (Set.ofList [ 10; 11 ])
    let pauses = ref 0

    let t =
        accountTeardown
            (table [ row 10 1 "sh -c x"; row 11 10 "sleep 60" ])
            (aliveIn alive)
            40
            (fun () ->
                // The child dies during the first pause.
                pauses.Value <- pauses.Value + 1
                alive.Value <- Set.empty)
            10
            (fun () ->
                alive.Value <- Set.ofList [ 11 ]
                KillOutcome.Killed)

    test <@ t.Survivors = Ok [] @>
    test <@ pauses.Value = 1 @>

[<Fact>]
let ``accountTeardown: an unreadable table before the kill makes the tree AND its survivors unknown`` () =
    let t =
        accountTeardown (fun () -> Error "ps: not found") (fun _ -> Ok false) 3 noPause 10 (fun () ->
            KillOutcome.Killed)

    test <@ t.Tree = Error "ps: not found" @>

    test
        <@
            match t.Survivors with
            | Error _ -> true
            | Ok _ -> false
        @>

[<Fact>]
let ``accountTeardown: a liveness probe that cannot answer is UNKNOWN, never "gone"`` () =
    let t =
        accountTeardown (table [ row 10 1 "sh -c x" ]) (fun _ -> Error "errno 22") 3 noPause 10 (fun () ->
            KillOutcome.Killed)

    test
        <@
            match t.Survivors with
            | Error reason -> reason.Contains "pid 10"
            | Ok _ -> false
        @>

[<Fact>]
let ``accountTeardown: a kill that throws still accounts for the tree`` () =
    let failure = ComponentModel.Win32Exception("denied") :> exn

    let t =
        accountTeardown (table [ row 10 1 "sh -c x" ]) (fun _ -> Ok true) 1 noPause 10 (fun () ->
            KillOutcome.KillFailed failure)

    test <@ t.Kill = KillOutcome.KillFailed failure @>
    test <@ t.Survivors = Ok [ row 10 1 "sh -c x" ] @>

// ---------------------------------------------------------------------------
// lastFinishedProject — the one progress fact an MSBuild log gives us

[<Fact>]
let ``lastFinishedProject is the last 'Name -> output' line`` () =
    let output =
        "  Core -> /repo/src/Core/bin/Debug/net10.0/Core.dll\n\
         warning FS0064: whatever\n\
         \x20 Lib -> /repo/src/Lib/bin/Debug/net10.0/Lib.dll\n"

    test <@ lastFinishedProject output = Some "Lib" @>

[<Fact>]
let ``lastFinishedProject is None when the build reported finishing nothing`` () =
    test <@ lastFinishedProject "Determining projects to restore...\n" = None @>

// ---------------------------------------------------------------------------
// describe — the report

let private teardown tree survivors kill : TreeTeardown =
    { RootPid = 45128
      Interrupt = None
      Tree = tree
      KillTook = TimeSpan.FromMilliseconds 74.0
      Kill = kill
      Survivors = survivors }

/// What the report says when the side log could not be read.
let private noSideLog = SideLogReading.Unavailable "not attached: test"

/// The node-reuse value fshw set on the child, as `mergeDotnetEnv` computes it.
let private describe = describeBuildOverrun (Some "1") noSideLog

let private whole = BuildScope.WholeCommand "sh -c \"dotnet build\""
let private budget = TimeSpan.FromSeconds 600.0
let private elapsed = TimeSpan.FromSeconds 600.4

let private tree =
    [ row 45128 1 "sh -c dotnet build"
      row 45129 45128 "dotnet build"
      row 45140 45129 "dotnet MSBuild.dll /nodemode:1" ]

[<Fact>]
let ``the report names the command, the budget and where to change it`` () =
    let summary, report =
        describe whole budget elapsed "" (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)) KillOutcome.Killed

    test <@ summary.Contains "sh -c \"dotnet build\"" @>
    test <@ summary.Contains "600s budget" @>
    test <@ report.Contains "sh -c \"dotnet build\"" @>
    test <@ report.Contains "600s" @>
    test <@ report.Contains "timeoutSec" @>
    test <@ report.Contains ".fshw.json" @>

[<Fact>]
let ``the report names the last project the build finished and its last output line`` () =
    let output = "  Core -> /r/Core.dll\n  Lib -> /r/Lib.dll\nCompiling Api...\n"

    let _, report =
        describe whole budget elapsed output (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)) KillOutcome.Killed

    test <@ report.Contains "Lib" @>
    test <@ report.Contains "Compiling Api..." @>

[<Fact>]
let ``a template overrun names the root in flight, the finished roots and the queued ones`` () =
    let scope =
        BuildScope.TemplateRoot(
            "dotnet build /r/Api/Api.fsproj",
            "/r/Api/Api.fsproj",
            [ "/r/Core/Core.fsproj", TimeSpan.FromSeconds 41.0 ],
            [ "/r/Web/Web.fsproj" ]
        )

    let summary, report =
        describe scope budget elapsed "" (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)) KillOutcome.Killed

    test <@ summary.Contains "/r/Api/Api.fsproj" @>
    test <@ report.Contains "/r/Core/Core.fsproj" @>
    test <@ report.Contains "41" @>
    test <@ report.Contains "/r/Web/Web.fsproj" @>

[<Fact>]
let ``the report names every pid the kill was aimed at`` () =
    let _, report =
        describe whole budget elapsed "" (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)) KillOutcome.Killed

    for pid in [ "45128"; "45129"; "45140" ] do
        test <@ report.Contains pid @>

    test <@ report.Contains "dotnet MSBuild.dll /nodemode:1" @>

[<Fact>]
let ``a clean teardown says the tree is gone — and only because it re-read the table`` () =
    let summary, report =
        describe whole budget elapsed "" (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)) KillOutcome.Killed

    test <@ not (report.Contains "LEAKED") @>
    test <@ not (summary.Contains "LEAKED") @>
    test <@ report.Contains "none of the 3" @>

[<Fact>]
let ``a survivor is named as LEAKED in the summary and the report`` () =
    let survivor = row 45140 1 "dotnet MSBuild.dll /nodemode:1"

    let summary, report =
        describe
            whole
            budget
            elapsed
            ""
            (Some(teardown (Ok tree) (Ok [ survivor ]) KillOutcome.Killed))
            KillOutcome.Killed

    test <@ summary.Contains "LEAKED" @>
    test <@ summary.Contains "45140" @>
    test <@ report.Contains "LEAKED" @>
    test <@ report.Contains "45140" @>

[<Fact>]
let ``an unreadable process table is reported as UNKNOWN, never as a clean kill`` () =
    let summary, report =
        describe
            whole
            budget
            elapsed
            ""
            (Some(teardown (Error "ps: not found") (Error "tree unknown") KillOutcome.Killed))
            KillOutcome.Killed

    test <@ summary.Contains "UNKNOWN" @>
    test <@ report.Contains "ps: not found" @>
    test <@ not (summary.Contains "killed") @>

[<Fact>]
let ``a kill that did not return is reported as such, not as a kill`` () =
    let kill = KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0)

    let summary, report =
        describe whole budget elapsed "" (Some(teardown (Ok tree) (Ok tree) kill)) kill

    test <@ report.Contains "did not return" @>
    test <@ summary.Contains "LEAKED" @>

[<Fact>]
let ``an already-exited root is named as such`` () =
    let _, report =
        describe
            whole
            budget
            elapsed
            ""
            (Some(teardown (Ok []) (Ok []) KillOutcome.AlreadyExited))
            KillOutcome.AlreadyExited

    test <@ report.Contains "already exited" @>

[<Fact>]
let ``the report says which MSBuild node-reuse setting the child was given`` () =
    let _, report =
        describeBuildOverrun
            (Some "0")
            noSideLog
            whole
            budget
            elapsed
            ""
            (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed))
            KillOutcome.Killed

    test <@ report.Contains "MSBUILDDISABLENODEREUSE=0" @>

[<Fact>]
let ``with no teardown record the report says the tree was not recorded and times nothing`` () =
    let summary, report =
        describe whole budget elapsed "" None (KillOutcome.KillFailed(System.ComponentModel.Win32Exception()))

    test <@ summary.Contains "KILL FAILED" @>
    test <@ report.Contains "process tree: not recorded" @>
    test <@ report.Contains "  kill: KILL FAILED — " @>

[<Fact>]
let ``a command that never names dotnet is reported as given no node-reuse setting`` () =
    let _, report =
        describeBuildOverrun
            None
            noSideLog
            whole
            budget
            elapsed
            ""
            (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed))
            KillOutcome.Killed

    test <@ report.Contains "MSBUILDDISABLENODEREUSE not set by fshw" @>
    test <@ not (report.Contains "MSBUILDDISABLENODEREUSE=") @>

// ---------------------------------------------------------------------------
// the side log in the report — what was running when the build was interrupted

let private target name project (seconds: float) : InFlightTarget =
    { Target = name
      Project = project
      Running = TimeSpan.FromSeconds seconds }

let private cleanTeardown () =
    Some(teardown (Ok tree) (Ok []) KillOutcome.Killed)

[<Fact>]
let ``the innermost in-flight target leads the summary and every one is listed with its time`` () =
    let reading =
        SideLogReading.Read
            [ "/r/.fshw/build-binlog/a.binlog",
              Ok
                  [ target "Build" "/r/src/Api/Api.fsproj" 580.0
                    target "CoreCompile" "/r/src/Api/Api.fsproj" 312.5 ] ]

    let summary, report =
        describeBuildOverrun (Some "1") reading whole budget elapsed "" (cleanTeardown ()) KillOutcome.Killed

    test <@ summary.Contains "while in target CoreCompile (Api.fsproj) and 1 other target(s)" @>
    test <@ report.Contains "target CoreCompile in /r/src/Api/Api.fsproj — running 312.5s" @>
    test <@ report.Contains "target Build in /r/src/Api/Api.fsproj — running 580.0s" @>
    test <@ report.IndexOf "CoreCompile in" < report.IndexOf "Build in" @>

[<Fact>]
let ``a single in-flight target is named without an 'others' count`` () =
    let reading =
        SideLogReading.Read [ "/r/a.binlog", Ok [ target "StallsHere" "/r/stall.proj" 18.0 ] ]

    let summary, _ =
        describeBuildOverrun (Some "1") reading whole budget elapsed "" (cleanTeardown ()) KillOutcome.Killed

    test <@ summary.Contains "while in target StallsHere (stall.proj);" @>

[<Fact>]
let ``a side log that shows nothing running says so, and names a binlog it could not read`` () =
    let reading =
        SideLogReading.Read [ "/r/a.binlog", Ok []; "/r/b.binlog", Error "replay failed" ]

    let summary, report =
        describeBuildOverrun (Some "1") reading whole budget elapsed "" (cleanTeardown ()) KillOutcome.Killed

    test <@ not (summary.Contains "while in target") @>
    test <@ report.Contains "none of 2 side binary log(s) shows a target running at the interrupt" @>
    test <@ report.Contains "/r/b.binlog could not be read: replay failed" @>

[<Fact>]
let ``an unavailable side log is reported as unknown, with the reason`` () =
    let _, report =
        describeBuildOverrun
            (Some "1")
            (SideLogReading.Unavailable "no binary log in /r/x")
            whole
            budget
            elapsed
            ""
            (cleanTeardown ())
            KillOutcome.Killed

    test <@ report.Contains "in flight: unknown — fshw's side binary log is unavailable: no binary log in /r/x" @>

// ---------------------------------------------------------------------------
// the interrupt in the report

let private interrupted signalled refused exited : TreeInterrupt =
    { At = DateTime.UtcNow
      Signalled = signalled
      Refused = refused
      Waited = TimeSpan.FromSeconds 0.4
      Exited = exited }

let private reportWithInterrupt (i: TreeInterrupt) =
    let t =
        { teardown (Ok tree) (Ok []) KillOutcome.AlreadyExited with
            Interrupt = Some i }

    describe whole budget elapsed "" (Some t) KillOutcome.AlreadyExited |> snd

[<Fact>]
let ``an interrupt the tree answered is reported with how long it took`` () =
    let report = reportWithInterrupt (interrupted [ 45128; 45129 ] [] true)
    test <@ report.Contains "interrupt: sent SIGINT to 2 process(es)" @>
    test <@ report.Contains "the tree exited 0.4s later" @>
    test <@ report.IndexOf "interrupt:" < report.IndexOf "  kill:" @>

[<Fact>]
let ``an interrupt the tree ignored is reported as followed by the kill`` () =
    let report = reportWithInterrupt (interrupted [ 45128 ] [] false)
    test <@ report.Contains "it had not exited after 0.4s, so it was killed" @>

[<Fact>]
let ``an interrupt that could not be sent names the pid and the reason`` () =
    let report = reportWithInterrupt (interrupted [] [ 45128, "errno 1" ] false)
    test <@ report.Contains "nothing was signalled, so nothing was waited for" @>
    test <@ report.Contains "could not signal pid 45128 (errno 1)" @>

// ---------------------------------------------------------------------------
// interruptTree — SIGINT the snapshot, then wait for it to go

[<Fact>]
let ``interruptTree signals every member and stops waiting once they are gone`` () =
    let alive = ref (Set.ofList [ 10; 11 ])
    let signalled = ref []
    let pauses = ref 0

    let i =
        interruptTree
            (fun pid ->
                signalled.Value <- pid :: signalled.Value
                Ok())
            (aliveIn alive)
            50
            (fun () ->
                pauses.Value <- pauses.Value + 1
                alive.Value <- Set.empty)
            [ row 10 1 "sh -c x"; row 11 10 "dotnet build" ]

    test <@ List.rev signalled.Value = [ 10; 11 ] @>
    test <@ i.Signalled = [ 10; 11 ] @>
    test <@ i.Exited @>
    test <@ pauses.Value = 1 @>

[<Fact>]
let ``interruptTree gives up after its attempts and says the tree did not exit`` () =
    let pauses = ref 0

    let i =
        interruptTree (fun _ -> Ok()) (fun _ -> Ok true) 3 (fun () -> pauses.Value <- pauses.Value + 1) [ row 10 1 "x" ]

    test <@ not i.Exited @>
    test <@ pauses.Value = 2 @>

[<Fact>]
let ``interruptTree counts an unanswerable liveness probe as still running`` () =
    let i =
        interruptTree (fun _ -> Ok()) (fun _ -> Error "errno 22") 2 noPause [ row 10 1 "x" ]

    test <@ not i.Exited @>

[<Fact>]
let ``interruptTree records a member it could not signal and does not wait on it`` () =
    let probed = ref 0

    let i =
        interruptTree
            (fun _ -> Error "errno 1")
            (fun _ ->
                probed.Value <- probed.Value + 1
                Ok true)
            5
            noPause
            [ row 10 1 "x" ]

    test <@ List.isEmpty i.Signalled @>
    test <@ i.Refused = [ 10, "errno 1" ] @>
    test <@ not i.Exited @>
    test <@ probed.Value = 0 @>

// ---------------------------------------------------------------------------
// accountTeardownWith — the interrupt runs on the snapshot, before the kill

[<Fact>]
let ``accountTeardownWith interrupts the snapshotted tree before the kill`` () =
    let order = ref []
    let alive = ref (Set.ofList [ 10; 11 ])

    let t =
        accountTeardownWith
            (table [ row 10 1 "sh -c x"; row 11 10 "sleep 60" ])
            (aliveIn alive)
            3
            noPause
            10
            (fun members ->
                order.Value <- "interrupt" :: order.Value
                Some(interrupted (members |> List.map _.Pid) [] true))
            (fun () ->
                order.Value <- "kill" :: order.Value
                alive.Value <- Set.empty
                KillOutcome.AlreadyExited)

    test <@ List.rev order.Value = [ "interrupt"; "kill" ] @>
    test <@ t.Interrupt |> Option.map _.Signalled = Some [ 10; 11 ] @>
    test <@ t.Survivors = Ok [] @>

[<Fact>]
let ``accountTeardownWith does not interrupt a tree it could not read`` () =
    let called = ref false

    let t =
        accountTeardownWith
            (fun () -> Error "ps: not found")
            (fun _ -> Ok false)
            3
            noPause
            10
            (fun _ ->
                called.Value <- true
                None)
            (fun () -> KillOutcome.Killed)

    test <@ not called.Value @>
    test <@ t.Interrupt = None @>

// ---------------------------------------------------------------------------
// sendInterrupt / interruptAttempts — the production signal and grace

[<Fact(Timeout = 20000)>]
let ``sendInterrupt stops a live process`` () =
    use p =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("sleep", "30", UseShellExecute = false))

    test <@ sendInterrupt p.Id = Ok() @>
    test <@ p.WaitForExit 10000 @>

[<Fact>]
let ``sendInterrupt to a process that does not exist is an error, not a success`` () =
    use p =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("true", UseShellExecute = false))

    p.WaitForExit()

    test
        <@
            match sendInterrupt p.Id with
            | Error reason -> reason.Contains "SIGINT"
            | Ok() -> false
        @>

[<Fact>]
let ``interruptAttempts fits the grace into polls, and never fewer than one`` () =
    test <@ interruptAttempts (TimeSpan.FromSeconds 15.0) = 150 @>
    test <@ interruptAttempts TimeSpan.Zero = 1 @>

[<Fact>]
let ``interruptPause waits one poll`` () =
    let clock = Diagnostics.Stopwatch.StartNew()
    interruptPause ()
    test <@ clock.Elapsed >= InterruptPoll - TimeSpan.FromMilliseconds 1.0 @>

[<Fact>]
let ``interruptWith no grace interrupts nothing`` () =
    test <@ interruptWith None [ row 10 1 "x" ] = None @>

[<Fact(Timeout = 20000)>]
let ``interruptWith a grace interrupts a live tree and sees it exit`` () =
    use p =
        Diagnostics.Process.Start(Diagnostics.ProcessStartInfo("sleep", "30", UseShellExecute = false))

    let i = interruptWith (Some(TimeSpan.FromSeconds 10.0)) [ row p.Id 1 "sleep 30" ]
    test <@ i |> Option.map _.Signalled = Some [ p.Id ] @>
    test <@ i |> Option.map _.Exited = Some true @>
    test <@ p.WaitForExit 10000 @>
