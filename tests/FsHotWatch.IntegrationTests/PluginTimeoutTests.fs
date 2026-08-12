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
