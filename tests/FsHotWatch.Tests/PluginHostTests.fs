module FsHotWatch.Tests.PluginHostTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact(Timeout = 20000)>]
let ``plugin receives file change events`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable fileChanges: FileChangeKind list = []

    let handler =
        { Name = PluginName.create "recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged c -> fileChanges <- c :: fileChanges
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> fileChanges.Length >= 1) 12000
    test <@ fileChanges.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``plugin registers command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "cmd-test"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = [ "greet", fun _ctx _state _args -> async { return "hello" } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    let result = host.RunCommand("greet", [||]) |> Async.RunSynchronously
    test <@ result = Some "hello" @>

[<Fact(Timeout = 15000)>]
let ``RunCommand returns None for unknown command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let result = host.RunCommand("bogus", [||]) |> Async.RunSynchronously
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "status-test"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> (host.GetStatus("status-test")) <> Some Idle) 12000

    let status = host.GetStatus("status-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Running _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``GetAllStatuses returns all plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let makeHandler name =
        { Name = PluginName.create name
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "a")
    host.RegisterHandler(makeHandler "b")
    let all = host.GetAllStatuses()
    test <@ all.Count = 2 @>
    test <@ all |> Map.containsKey "a" @>
    test <@ all |> Map.containsKey "b" @>

[<Fact(Timeout = 20000)>]
let ``EmitBuildCompleted reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let handler =
        { Name = PluginName.create "build-listener"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitBuildCompleted(BuildSucceeded)
    waitUntil (fun () -> receivedBuild.IsSome) 12000
    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact(Timeout = 20000)>]
let ``EmitBuildCompleted with failure reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let handler =
        { Name = PluginName.create "build-fail-listener"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    let errors = [ "error CS0001: Something broke" ]
    host.EmitBuildCompleted(BuildFailed errors)
    waitUntil (fun () -> receivedBuild.IsSome) 12000

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``preprocessor runs before events are dispatched`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable preprocessorCalled = false

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "tracker"

            member _.Process (changedFiles: string list) (_repoRoot: string) =
                preprocessorCalled <- true
                []

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let _ = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ preprocessorCalled @>

[<Fact(Timeout = 15000)>]
let ``preprocessor modified files are returned`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "modifier"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = [ "src/Formatted.fs"; "src/Other.fs" ]

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let modified = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ modified = [ "src/Formatted.fs"; "src/Other.fs" ] @>

[<Fact(Timeout = 15000)>]
let ``preprocessor status is tracked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "status-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = [ "a.fs" ]

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)

    // Before running, status should be Idle
    let statusBefore = host.GetStatus("status-pp")
    test <@ statusBefore = Some Idle @>

    let _ = host.RunPreprocessors([ "src/Lib.fs" ])

    let statusAfter = host.GetStatus("status-pp")
    test <@ statusAfter.IsSome @>

    test
        <@
            match statusAfter.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 20000)>]
let ``multiple plugins receive the same event`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable received1 = false
    let mutable received2 = false
    let mutable received3 = false

    let makeHandler name (setter: unit -> unit) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> setter ()
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "p1" (fun () -> received1 <- true))
    host.RegisterHandler(makeHandler "p2" (fun () -> received2 <- true))
    host.RegisterHandler(makeHandler "p3" (fun () -> received3 <- true))

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil (fun () -> received1 && received2 && received3) 12000
    test <@ received1 @>
    test <@ received2 @>
    test <@ received3 @>

[<Fact(Timeout = 20000)>]
let ``plugin can report and query errors via host`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "error-reporter"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportErrors
                            "/src/A.fs"
                            [ { Message = "bad"
                                Severity = DiagnosticSeverity.Warning
                                Line = 1
                                Column = 0
                                Detail = None } ]
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 12000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

    let errors = host.GetErrors()
    test <@ errors.ContainsKey "/src/A.fs" @>

[<Fact(Timeout = 20000)>]
let ``plugin ClearErrors removes errors from ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "clear-test"
          Init = false
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ when not state ->
                        ctx.ReportErrors
                            "/src/B.fs"
                            [ { Message = "oops"
                                Severity = DiagnosticSeverity.Error
                                Line = 5
                                Column = 0
                                Detail = None } ]

                        return true
                    | FileChanged _ when state ->
                        ctx.ClearErrors "/src/B.fs"
                        return state
                    | _ -> return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 12000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> not (host.HasFailingReasons(warningsAreFailures = true))) 12000
    test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 20000)>]
let ``GetErrorsByPlugin returns only that plugin's errors`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    // Use direct ledger reporting via host for simplicity
    host.ReportErrors(
        "pluginA",
        "/src/A.fs",
        [ { Message = "from A"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ]
    )

    host.ReportErrors(
        "pluginB",
        "/src/B.fs",
        [ { Message = "from B"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ]
    )

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 2
        @>

    let aErrors = host.GetErrorsByPlugin("pluginA")
    test <@ aErrors.Count = 1 @>
    test <@ aErrors.ContainsKey "/src/A.fs" @>

[<Fact(Timeout = 20000)>]
let ``EmitFileChecked dispatches to framework plugin handlers`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let ref1 = ref false
    let ref2 = ref false

    let makeHandler name (r: bool ref) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChecked _ -> r.Value <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "p1" ref1)
    host.RegisterHandler(makeHandler "p2" ref2)

    let dummyResult =
        { File = AbsFilePath.create "/tmp/test.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    host.EmitFileChecked(dummyResult)

    waitUntil (fun () -> ref1.Value && ref2.Value) 12000
    test <@ ref1.Value @>
    test <@ ref2.Value @>

[<Fact(Timeout = 15000)>]
let ``preprocessor exception sets Failed status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "boom-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = failwith "preprocessor kaboom"

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let modified = host.RunPreprocessors([ "src/Lib.fs" ])

    // No modified files returned from a failing preprocessor
    test <@ modified |> List.isEmpty @>

    let status = host.GetStatus("boom-pp")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed(msg, _) -> msg.Contains("preprocessor kaboom")
            | _ -> false
        @>

[<Fact(Timeout = 20000)>]
let ``ReportErrors with version passes through to ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "v2 error"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 2L
    )

    // Stale version should be ignored
    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "v1 stale"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 1L
    )

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

    let errors = host.GetErrors()
    let fileErrors = errors.["/src/A.fs"]
    test <@ (snd fileErrors.[0]).Message = "v2 error" @>

[<Fact(Timeout = 15000)>]
let ``ClearErrors with version passes through to ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "error"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 2L
    )

    // Stale clear should be ignored
    host.ClearErrors("fcs", "/src/A.fs", version = 1L)
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    // Current version clear should work
    host.ClearErrors("fcs", "/src/A.fs", version = 3L)
    test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 20000)>]
let ``OnStatusChanged event fires when plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable statusEvents: (string * PluginStatus) list = []
    host.OnStatusChanged.Add(fun (name, status) -> statusEvents <- (name, status) :: statusEvents)

    let handler =
        { Name = PluginName.create "status-eventer"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Status agent dispatches asynchronously — wait for events
    // Initial Idle from RegisterHandler + Running + Completed = at least 3
    waitUntil (fun () -> statusEvents.Length >= 3) 12000
    test <@ statusEvents.Length >= 3 @>

    test
        <@
            statusEvents
            |> List.exists (fun (name, s) ->
                name = "status-eventer"
                && match s with
                   | Running _ -> true
                   | _ -> false)
        @>

    test
        <@
            statusEvents
            |> List.exists (fun (name, s) ->
                name = "status-eventer"
                && match s with
                   | Completed _ -> true
                   | _ -> false)
        @>

[<Fact(Timeout = 20000)>]
let ``OnStatusChanged subscriber re-entrantly calling GetAllStatuses does not deadlock`` () =
    // Pins the invariant that statusChanged.Trigger fires OUTSIDE the status agent's
    // serialization boundary. If the trigger fired inside the agent's loop, a
    // subscriber doing PostAndReply (GetAllStatuses) would block waiting for the
    // agent that is itself blocked inside the trigger.
    let host = PluginHost.create nullChecker "/tmp/test"

    let observedFromHandler = ref None
    let handlerEntered = new ManualResetEventSlim(false)
    let handlerDone = new ManualResetEventSlim(false)

    host.OnStatusChanged.Add(fun (name, _status) ->
        if name = "reentrant" then
            handlerEntered.Set()
            // Re-entrant read must not deadlock.
            let snapshot = host.GetAllStatuses()
            observedFromHandler.Value <- Some snapshot
            handlerDone.Set())

    let handler =
        { Name = PluginName.create "reentrant"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let entered = handlerEntered.Wait(TimeSpan.FromSeconds(5.0))
    test <@ entered @>
    let finished = handlerDone.Wait(TimeSpan.FromSeconds(5.0))
    test <@ finished @>
    test <@ observedFromHandler.Value.IsSome @>
    test <@ observedFromHandler.Value.Value |> Map.containsKey "reentrant" @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal does not deadlock when OnStatusChanged subscriber calls GetAllStatuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "deadlock-test"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        Thread.Sleep(50)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    // Start waitForAllTerminal — this subscribes to OnStatusChanged and calls
    // GetAllStatuses() inside the handler. If OnStatusChanged fires synchronously
    // inside a MailboxProcessor, GetAllStatuses (which would do PostAndReply to
    // the same agent) will deadlock.
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) ()

    // Trigger a status change
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // If deadlocked, this will time out
    let completed = waitTask.Wait(TimeSpan.FromSeconds(8.0))
    test <@ completed @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal with TimeSpan.MaxValue does not overflow deadline arithmetic`` () =
    // Regression: DateTime.UtcNow + TimeSpan.MaxValue throws; MaxValue must be treated
    // as "no deadline" so the RPC path that passes it through (WaitForComplete with
    // timeoutMs <= 0) doesn't crash on a live daemon.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "instant-terminal"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let waitTask = waitForAllTerminal host TimeSpan.MaxValue ()
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let completed = waitTask.Wait(TimeSpan.FromSeconds(5.0))
    test <@ completed @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal waits for downstream plugin that hasn't yet picked up its event`` () =
    // Repro: plugin A is subscribed to FileChanged, emits BuildCompleted then
    // transitions Completed. Plugin B is subscribed to BuildCompleted but
    // delays its Idle->Running transition by longer than the legacy 1s
    // stability window, so there is a wide window where:
    //   - A is Completed (terminal)
    //   - B is still Idle (mailbox not yet processed BuildCompleted)
    //   - allTerminal=true, but B is about to start work
    // Today's wait returns prematurely; the fix must wait until B is terminal.
    let host = PluginHost.create nullChecker "/tmp/test"
    let bDelay = TimeSpan.FromMilliseconds(1500.0)
    let mutable bReachedRunning = false
    let mutable bReachedCompleted = false

    let aHandler =
        { Name = PluginName.create "plugin-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "plugin-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        // Simulate slow handler entry: wait long enough that any
                        // sub-1s stability window in the waiter would expire while
                        // B is still Idle.
                        do! Async.Sleep(int bDelay.TotalMilliseconds)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        bReachedRunning <- true
                        do! Async.Sleep(50)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                        bReachedCompleted <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(15.0)) ()

    let completed = waitTask.Wait(TimeSpan.FromSeconds(20.0))
    test <@ completed @>
    // Both plugins must have completed their actual work cycle. The bug:
    // waitForAllTerminal returns before B even started.
    test <@ bReachedRunning @>
    test <@ bReachedCompleted @>

    let final = host.GetAllStatuses()

    let isCompleted s =
        match s with
        | Completed _ -> true
        | _ -> false

    test <@ final |> Map.forall (fun _ s -> isCompleted s) @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal does not return while a downstream plugin still has events queued in its mailbox after it has already advanced a generation``
    ()
    =
    // Repro of the BuildCompleted -> Pending -> Running edge race.
    //
    // Plugin B subscribes to BOTH FileChanged and BuildCompleted.
    //   - On FileChanged: Running -> Completed (advances B's generation to 1)
    //   - On BuildCompleted: long delay before transitioning to Running, then
    //     Completed.
    //
    // Plugin A emits BuildCompleted some time after FileChanged. By that time, B
    // has already transitioned to Completed (gen=1) for its first cycle.
    //
    // Bug window: between A.Completed (gen 1>0) emitting BuildCompleted and
    // B's handler picking it up,
    //   - A is Completed, gen advanced past snapshot
    //   - B is Completed, gen advanced past snapshot
    //   - B has a BuildCompleted queued in its mailbox (inflight=1)
    //
    // The legacy `allPluginsAdvancedToTerminal` predicate returns true here
    // even though B has not yet started its second cycle, so WaitForComplete
    // returns prematurely. The fix must keep the wait pending until B's queued
    // event has been drained.
    let host = PluginHost.create nullChecker "/tmp/test"
    let bRunningCount = ref 0

    let aHandler =
        { Name = PluginName.create "plugin-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        // Long enough for B to finish its FileChanged cycle and
                        // settle into Completed before BuildCompleted is emitted.
                        do! Async.Sleep(300)
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "plugin-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(50)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | BuildCompleted _ ->
                        // Sleep before transitioning to Running. This widens the
                        // race window: B is in Completed (from the prior
                        // FileChanged cycle) with the BuildCompleted event sitting
                        // in its mailbox / handler.
                        do! Async.Sleep(500)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        Interlocked.Increment(&bRunningCount.contents) |> ignore

                        do! Async.Sleep(20)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged; SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(15.0)) ()

    let completed = waitTask.Wait(TimeSpan.FromSeconds(20.0))
    test <@ completed @>
    // The wait must not have returned before B picked up its second event and
    // ran through its second cycle. With the bug, the wait returns while B's
    // BuildCompleted is still queued in its mailbox and bRunningCount=1.
    let observed = Volatile.Read(&bRunningCount.contents)
    // bRunningCount=1 means B drained its queued BuildCompleted and ran the
    // second cycle to its terminal Completed. With the bug, the wait returns
    // while BuildCompleted is still queued / handler is still in its
    // pre-Running sleep, so bRunningCount=0.
    test <@ observed = 1 @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal waits for full cascade A -> B -> C`` () =
    // Cascade: A (FileChanged -> emits BuildCompleted), B (BuildCompleted ->
    // emits TestRunCompleted), C (TestRunCompleted -> terminal). Calling
    // waitForAllTerminal at the start must wait for the full chain even though
    // each downstream plugin starts in Idle.
    let host = PluginHost.create nullChecker "/tmp/test"

    let aHandler =
        { Name = PluginName.create "cascade-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "cascade-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        do! Async.Sleep(300)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)

                        ctx.EmitTestRunCompleted
                            { RunId = Guid.NewGuid()
                              TotalElapsed = TimeSpan.Zero
                              Outcome = Normal
                              Results = Map.empty
                              RanFullSuite = true }

                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    let mutable cCompleted = false

    let cHandler =
        { Name = PluginName.create "cascade-c"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | TestRunCompleted _ ->
                        do! Async.Sleep(300)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                        cCompleted <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeTestRunCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)
    host.RegisterHandler(cHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(10.0)) ()

    let completed = waitTask.Wait(TimeSpan.FromSeconds(15.0))
    test <@ completed @>
    test <@ cCompleted @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal completes when plugin fails mid-cycle`` () =
    // Crash recovery: a plugin that throws/reports Failed must still satisfy
    // the wait — Failed is a terminal status.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "crasher"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.ReportStatus(Failed("boom", DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) ()

    let completed = waitTask.Wait(TimeSpan.FromSeconds(8.0))
    test <@ completed @>

    let final = host.GetAllStatuses()

    let isFailed s =
        match s with
        | Failed _ -> true
        | _ -> false

    test <@ final |> Map.forall (fun _ s -> isFailed s) @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal returns within quiescence window when no work is pending`` () =
    // Edge case: a plugin that is Idle and stays Idle (never receives an event)
    // must not cause WaitForComplete to hang forever. With nothing happening,
    // the quiescence window should fire and the wait should return.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "never-fires"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let started = DateTime.UtcNow
    let waitTask = waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) ()
    let completed = waitTask.Wait(TimeSpan.FromSeconds(10.0))
    let elapsed = DateTime.UtcNow - started
    test <@ completed @>
    // Should be much faster than the 5s wait timeout — quiescence should fire
    // within about a second of the call when nothing is happening.
    test <@ elapsed < TimeSpan.FromSeconds(3.0) @>

[<Fact(Timeout = 20000)>]
let ``HasFailingReasons distinguishes warnings from errors`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    // Report only warnings
    host.ReportErrors(
        "linter",
        "/src/A.fs",
        [ { Message = "style warning"
            Severity = DiagnosticSeverity.Warning
            Line = 1
            Column = 0
            Detail = None } ]
    )

    // With warningsAreFailures=true, warnings count as failures
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    // With warningsAreFailures=false, only errors count as failures
    test <@ not (host.HasFailingReasons(warningsAreFailures = false)) @>

    // FailingReasons with warningsAreFailures=true includes the warning
    let withWarnings = host.FailingReasons(warningsAreFailures = true)
    test <@ withWarnings.Count = 1 @>

    // FailingReasons with warningsAreFailures=false excludes the warning
    let withoutWarnings = host.FailingReasons(warningsAreFailures = false)
    test <@ withoutWarnings.Count = 0 @>

    // Now add an actual error
    host.ReportErrors(
        "fcs",
        "/src/B.fs",
        [ { Message = "real error"
            Severity = DiagnosticSeverity.Error
            Line = 5
            Column = 0
            Detail = None } ]
    )

    // Both modes should now report failures
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    test <@ host.HasFailingReasons(warningsAreFailures = false) @>

    // warningsAreFailures=true: 2 files (warning + error)
    let allFailing = host.FailingReasons(warningsAreFailures = true)
    test <@ allFailing.Count = 2 @>

    // warningsAreFailures=false: 1 file (only error)
    let errorsOnly = host.FailingReasons(warningsAreFailures = false)
    test <@ errorsOnly.Count = 1 @>
    test <@ errorsOnly.ContainsKey "/src/B.fs" @>

// --- FileCommand pattern registry ---

let private parsePattern = FsHotWatch.Watcher.FilePattern.parse

[<Fact(Timeout = 15000)>]
let ``RegisterFileCommandPattern stores pattern retrievable by name`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("coverage-ratchet", parsePattern "*.ratchet.json")
    test <@ host.GetFileCommandPattern("coverage-ratchet") = Some(parsePattern "*.ratchet.json") @>

[<Fact(Timeout = 15000)>]
let ``GetFileCommandPattern returns None for unregistered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    test <@ host.GetFileCommandPattern("nonexistent") = None @>

[<Fact(Timeout = 15000)>]
let ``RegisterFileCommandPattern overwrites on re-register`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("plugin-a", parsePattern "*.ratchet.json")
    host.RegisterFileCommandPattern("plugin-a", parsePattern "coverage-ratchet.json")
    test <@ host.GetFileCommandPattern("plugin-a") = Some(parsePattern "coverage-ratchet.json") @>

[<Fact(Timeout = 15000)>]
let ``RerunFileCommandPlugin returns Error for unregistered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    let result = host.RerunFileCommandPlugin("nonexistent")

    match result with
    | Result.Error msg -> test <@ msg.Contains("nonexistent") @>
    | Result.Ok() -> failwith "expected Error"

[<Fact(Timeout = 15000)>]
let ``RerunFileCommandPlugin returns Ok for registered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("coverage-ratchet", parsePattern "*.ratchet.json")
    test <@ host.RerunFileCommandPlugin("coverage-ratchet") = Result.Ok() @>

[<Fact(Timeout = 15000)>]
let ``Teardown logs failing plugin Teardown with exception class (F14)`` () =
    // F14: plugin Teardown is a third-party-extension boundary; the catch
    // keeps cleanup going across other plugins. Previously logged ex.Message
    // only, stripping the stack trace just when a misbehaving plugin needed
    // diagnosing. Verify the log now includes the exception type name.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "boom"
          Init = ()
          Update = fun _ _ _ -> async { return () }
          Commands = []
          Subscriptions = Set.empty
          CacheKey = None
          Teardown = Some(fun () -> raise (System.InvalidOperationException("teardown boom"))) }

    host.RegisterHandler(handler)

    let original = FsHotWatch.Logging.logLevel
    let sb = System.Text.StringBuilder()
    let writer = new System.IO.StringWriter(sb)
    let prevErr = System.Console.Error

    try
        System.Console.SetError(writer)
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
        host.Teardown()
        writer.Flush()
        let output = sb.ToString()
        test <@ output.Contains("Teardown failed") @>
        test <@ output.Contains("InvalidOperationException") @>
    finally
        System.Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original
