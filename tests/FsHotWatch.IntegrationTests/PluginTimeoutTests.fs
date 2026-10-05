module FsHotWatch.Tests.PluginTimeoutTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph
open FsHotWatch.FileCommand.FileCommandPlugin
open FsHotWatch.Tests.TestHelpers

// Both tests spawn a real `sleep 10` with a 1s timeout to force the plugin's
// TimedOut outcome, driving runProcessWithTimeout's kill + bounded post-kill drain
// (the Kill catch arm, the Task.WaitAll(500ms) drain catch, and the
// stdoutTask/stderrTask IsCompletedSuccessfully else-branches). Whether the drain
// beats 500ms is OS scheduling, so in the unit suite these made ProcessHelper.fs /
// BuildPlugin.fs / FileCommandPlugin.fs coverage jitter under CPU load. They live
// here (no coverage package) to keep that metric deterministic.

[<Fact(Timeout = 15000)>]
let ``build plugin honors timeoutSec and records TimedOut outcome`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        FsHotWatch.Build.BuildPlugin.create "sleep" "10" [] (ProjectGraph()) [] None [] (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 8000

    let history = host.GetHistory("build")
    test <@ not history.IsEmpty @>
    let last = List.last history

    test
        <@
            match last.Outcome with
            | TimedOut _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``FileCommandPlugin honors timeoutSec and records TimedOut`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "slow-cmd")
            { FilePattern = Some(fun _ -> true)
              AfterTests = None }
            "sleep"
            "10"
            "/tmp"
            (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("slow-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        8000

    let history = host.GetHistory("slow-cmd")
    test <@ not history.IsEmpty @>
    let last = List.last history

    test
        <@
            match last.Outcome with
            | TimedOut _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

/// The overrun report, live: a real `sh` tree (the shell plus a backgrounded child)
/// overruns a 1s budget. The report must name the command, the budget and its knob,
/// the last project the build said it finished, every process the kill was aimed at,
/// and — from re-reading the process table, not from the kill call returning — that
/// none of them was left running.
///
/// The tree is read by the product's teardown, with `TeardownBudget` (10 s) for `ps`: a
/// trivial child that only a wedged box keeps past it. The wait sits above the product's
/// whole teardown (1 s timeout, 10 s read, 10 s kill, ~2 s settle, 2 s drain), so the
/// product reports first.
[<Fact(Timeout = 60000)>]
let ``an overrunning build names its command, budget, tree and what the kill left`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        FsHotWatch.Build.BuildPlugin.create
            "sh"
            "-c \"echo '  Lib -> /tmp/Lib.dll'; sleep 61 & sleep 62\""
            []
            (ProjectGraph())
            []
            None
            []
            (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 45000

    let summary =
        match (List.last (host.GetHistory "build")).Outcome with
        | TimedOut summary -> summary
        | other -> failwith $"expected TimedOut, got %A{other}"

    let report =
        host.GetErrorsByPlugin "build"
        |> Map.toList
        |> List.collect snd
        |> List.map _.Message
        |> String.concat "\n"

    test <@ summary.Contains "overran its 1s budget" @>
    test <@ summary.Contains "sleep 62" @>
    test <@ summary.Contains "none left running" @>
    test <@ report.Contains "timeoutSec" @>
    // The command never names dotnet, so fshw gave it no node-reuse setting.
    test <@ report.Contains "MSBUILDDISABLENODEREUSE not set by fshw" @>
    test <@ report.Contains "last project the build reported finishing: Lib" @>
    // The shell AND both of its children, by command, read off the process table.
    test <@ report.Contains "`sleep 61`" @>
    test <@ report.Contains "`sleep 62`" @>
    test <@ report.Contains "none of the 3 process(es) in the tree was still running" @>

/// A backgrounded descendant that outlives the shell that spawned it is NAMED as
/// leaked. The kill is injected to take only the root — the shape of a tree kill that
/// misses a descendant — and the real process table must then show the child alive.
[<Fact(Timeout = 60000)>]
let ``a descendant that outlives the killed shell is named as a survivor`` () =
    let psi =
        System.Diagnostics.ProcessStartInfo("sh", "-c \"sleep 63 & wait\"", UseShellExecute = false)

    use shell = System.Diagnostics.Process.Start psi
    let root = shell.Id

    // The claim is what the table shows after the kill, not how fast `ps` answers: the
    // product's 3 s read budget is a teardown's bound, and a loaded box can spend it, which
    // would turn this into "`ps` did not answer". The test's reads get a budget that only
    // a wedged `ps` reaches, inside the xUnit cap.
    let readTable () =
        FsHotWatch.ProcessHelper.readProcessTableWithin (System.TimeSpan.FromSeconds 15.0)

    let treeSize () =
        match readTable () with
        | Ok rows -> FsHotWatch.ProcessHelper.treeOf root rows |> List.length
        | Error _ -> 0

    test <@ waitUntilTrue (fun () -> treeSize () = 2) 15000 @>

    let teardown =
        FsHotWatch.ProcessHelper.accountTeardown
            readTable
            FsHotWatch.ProcessHelper.isProcessAlive
            5
            (fun () -> System.Threading.Thread.Sleep 100)
            root
            (fun () ->
                shell.Kill(entireProcessTree = false)
                FsHotWatch.ProcessHelper.KillOutcome.Killed)

    let survivors =
        match teardown.Survivors with
        | Ok rows -> rows
        | Error reason -> failwith reason

    try
        test <@ survivors |> List.map _.Command = [ "sleep 63" ] @>
    finally
        for s in survivors do
            try
                (System.Diagnostics.Process.GetProcessById s.Pid).Kill()
            with _ ->
                ()

/// A real `dotnet build`, run quiet (`-v q`) inside a shell wrapper, stalls in a target
/// whose name is known. The report must name THAT target — read from the side binlog fshw
/// attaches, not from the console, which at quiet verbosity names nothing — and must not
/// name the target that finished before it.
///
/// The build's 1 s timeout is armed when the build reaches `StallsHere` (its `Exec`
/// touches a marker first), not at the spawn: a slow start cannot spend it in an
/// earlier target. `FinishesFirst` takes 3 s, so a timeout armed at the spawn fires
/// there, and the test is red if the arm is lost. Before the marker only the wait
/// bounds the build: a `dotnet build` of a project with no references. After it, the
/// product's own bounds (15 s interrupt grace, 10 s table read, 10 s kill, 60 s binlog
/// replay) decide well inside the wait.
[<Fact(Timeout = 300000)>]
let ``an overrunning quiet dotnet build names the target it was stalled in`` () =
    let root =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fshw-stall-" + System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory root |> ignore

    System.IO.File.WriteAllText(
        System.IO.Path.Combine(root, "stall.proj"),
        """<Project>
  <Target Name="Build" DependsOnTargets="FinishesFirst;StallsHere" />
  <Target Name="FinishesFirst"><Exec Command="sleep 3" /></Target>
  <Target Name="StallsHere"><Exec Command="touch reached-stall &amp;&amp; sleep 120" /></Target>
</Project>"""
    )

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) root

        let reachedStall = System.IO.Path.Combine(root, "reached-stall")

        let handler =
            FsHotWatch.Build.BuildPlugin.createArmedWith
                (fun _ -> System.IO.File.Exists reachedStall)
                true
                "sh"
                "-c \"dotnet build stall.proj -v q -nologo 2> build-stderr.log; rc=$?; echo EXITCODE=$rc; exit $rc\""
                []
                (ProjectGraph())
                []
                None
                []
                (Some 1)

        host.RegisterHandler(handler)
        host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
        waitForTerminalStatus host "build" 240000

        let summary =
            match (List.last (host.GetHistory "build")).Outcome with
            | TimedOut summary -> summary
            | other -> failwith $"expected TimedOut, got %A{other}"

        let report =
            host.GetErrorsByPlugin "build"
            |> Map.toList
            |> List.collect snd
            |> List.map _.Message
            |> String.concat "\n"

        test <@ summary.Contains "StallsHere" @>
        test <@ report.Contains "target StallsHere" @>
        test <@ report.Contains "stall.proj" @>
        test <@ not (report.Contains "target FinishesFirst") @>
        test <@ not (report.Contains "LEAKED") @>
    finally
        System.IO.Directory.Delete(root, true)
