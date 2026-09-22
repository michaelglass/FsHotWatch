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
      Tree = tree
      KillTook = TimeSpan.FromMilliseconds 74.0
      Kill = kill
      Survivors = survivors }

/// The node-reuse value fshw set on the child, as `mergeDotnetEnv` computes it.
let private describe = describeBuildOverrun "1"

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
            "0"
            whole
            budget
            elapsed
            ""
            (Some(teardown (Ok tree) (Ok []) KillOutcome.Killed))
            KillOutcome.Killed

    test <@ report.Contains "MSBUILDDISABLENODEREUSE=0" @>
