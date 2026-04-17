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

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> fileChanges.Length >= 1) 5000
    test <@ fileChanges.Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``plugin registers command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "cmd-test"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = [ "greet", fun _state _args -> async { return "hello" } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    let result = host.RunCommand("greet", [||]) |> Async.RunSynchronously
    test <@ result = Some "hello" @>

[<Fact(Timeout = 5000)>]
let ``RunCommand returns None for unknown command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let result = host.RunCommand("bogus", [||]) |> Async.RunSynchronously
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
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
    waitUntil (fun () -> (host.GetStatus("status-test")) <> Some Idle) 5000

    let status = host.GetStatus("status-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Running _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> receivedBuild.IsSome) 5000
    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> receivedBuild.IsSome) 5000

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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

    waitUntil (fun () -> received1 && received2 && received3) 5000
    test <@ received1 @>
    test <@ received2 @>
    test <@ received3 @>

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 5000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

    let errors = host.GetErrors()
    test <@ errors.ContainsKey "/src/A.fs" @>

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 5000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> not (host.HasFailingReasons(warningsAreFailures = true))) 5000
    test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 10000)>]
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
        { File = "/tmp/test.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    host.EmitFileChecked(dummyResult)

    waitUntil (fun () -> ref1.Value && ref2.Value) 5000
    test <@ ref1.Value @>
    test <@ ref2.Value @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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
    waitUntil (fun () -> statusEvents.Length >= 3) 5000
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

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 10000)>]
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
