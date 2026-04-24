module FsHotWatch.Tests.PluginFrameworkTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

/// Shared FSharpChecker for tests.
let private checker = TestHelpers.sharedChecker.Value

/// Helper: register a handler with no-op host functions by default.
let private registerWith
    (handler: PluginHandler<'State, 'Msg>)
    (registerCommand: (string * CommandHandler -> unit) option)
    =
    let registerCommand = defaultArg registerCommand (fun _ -> ())

    registerHandler
        { Checker = checker
          RepoRoot = "/tmp/repo"
          ReportStatus = fun _ _ -> ()
          ReportErrors = fun _ _ _ -> ()
          ClearErrors = fun _ _ -> ()
          ClearPlugin = fun _ -> ()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          RegisterCommand = registerCommand
          TaskCache = None
          StartSubtask = fun _ _ _ -> ()
          UpdateSubtask = fun _ _ _ -> ()
          EndSubtask = fun _ _ -> ()
          Log = fun _ _ -> ()
          SetSummary = fun _ _ -> ()
          SetNextTerminalOutcome = fun _ _ -> () }
        handler

/// Register with all defaults.
let private registerDefault handler = registerWith handler None

[<Fact(Timeout = 5000)>]
let ``registered plugin dispatches FileChanged`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler =
        { Name = PluginName.create "test-fc"
          Init = false
          Update =
            fun _ctx _state event ->
                async {
                    match event with
                    | FileChanged _ -> return true
                    | _ -> return _state
                }
          Commands = [ "was-called", fun state _args -> async { return $"%b{state}" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    // Poll the command deterministically — it queues behind the FileChanged message
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

[<Fact(Timeout = 5000)>]
let ``registered plugin skips unsubscribed events`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler =
        { Name = PluginName.create "test-skip"
          Init = 0
          Update = fun _ctx state _event -> async { return state + 1 }
          Commands = [ "get-count", fun state _args -> async { return $"%d{state}" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged; SubscribeTestRunCompleted ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    // Dispatch subscribed events — should increment state
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    // Dispatch unsubscribed events — should be ignored
    reg.Dispatch(
        DispatchFileChecked
            { File = "/tmp/repo/Foo.fs"
              Source = ""
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }
    )

    reg.Dispatch(DispatchBuildCompleted BuildSucceeded)

    reg.Dispatch(
        DispatchCommandCompleted
            { Name = "test"
              Outcome = CommandSucceeded "" }
    )

    // Only the FileChanged should have incremented
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "1" @>

[<Fact(Timeout = 10000)>]
let ``commands query agent state`` () =
    async {
        let mutable registeredCmd: (string * CommandHandler) option = None

        let handler =
            { Name = PluginName.create "test-cmd"
              Init = 42
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged _ -> return state + 1
                        | _ -> return state
                    }
              Commands = [ "get-count", fun state _args -> async { return $"%d{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let _reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        test <@ registeredCmd.IsSome @>
        let (cmdName, cmdHandler) = registeredCmd.Value
        test <@ cmdName = "get-count" @>

        // Query initial state
        let! result = cmdHandler [||]
        test <@ result = "42" @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 5000)>]
let ``Custom messages work for self-posting`` () =
    async {
        let mutable customReceived = false

        let mutable registeredCmd: (string * CommandHandler) option = None

        let handler =
            { Name = PluginName.create "test-custom"
              Init = false
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            ctx.Post "hello"
                            return state
                        | Custom "hello" ->
                            customReceived <- true
                            return true
                        | _ -> return state
                    }
              Commands = [ "got-custom", fun state _args -> async { return $"%b{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

        // Poll command deterministically — queues behind FileChanged AND Custom messages
        let (_, cmdHandler) = registeredCmd.Value

        waitUntil
            (fun () ->
                let r = cmdHandler [||] |> Async.RunSynchronously
                r = "true")
            5000

        test <@ customReceived @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 10000)>]
let ``handler errors are recovered`` () =
    async {
        let mutable callCount = 0
        let mutable registeredCmd: CommandHandler option = None

        let handler =
            { Name = PluginName.create "test-recover"
              Init = 0
              Update =
                fun _ctx state event ->
                    async {
                        callCount <- callCount + 1

                        match event with
                        | FileChanged(SourceChanged [ "/throw" ]) -> return failwith "boom"
                        | FileChanged _ -> return state + 1
                        | _ -> return state
                    }
              Commands = [ "get-state", fun state _args -> async { return $"%d{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        // First: throw. Second: normal — should still work.
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/throw" ]))
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/ok" ]))

        // Poll command — deterministic, queues behind both messages
        waitUntil
            (fun () ->
                let r = registeredCmd.Value [||] |> Async.RunSynchronously
                r = "1")
            5000

        let! result = registeredCmd.Value [||]
        test <@ result = "1" @>
        test <@ callCount = 2 @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 5000)>]
let ``plugin subscribing to CommandCompleted receives event`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler =
        { Name = PluginName.create "test-cc"
          Init = false
          Update =
            fun _ctx _state event ->
                async {
                    match event with
                    | CommandCompleted _ -> return true
                    | _ -> return _state
                }
          Commands = [ "was-called", fun state _args -> async { return $"%b{state}" } ]
          Subscriptions = Set.ofList [ SubscribeCommandCompleted ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    reg.Dispatch(
        DispatchCommandCompleted
            { Name = "my-cmd"
              Outcome = CommandSucceeded "done" }
    )

    // Poll the command deterministically — it queues behind the CommandCompleted message
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

// --- Framework invariant: handler throws -> plugin status reaches terminal Failed ---

[<Fact(Timeout = 5000)>]
let ``handler that throws after ReportStatus(Running) still transitions status to Failed`` () =
    // Regression: before the fix, a handler that reported Running and then threw
    // (e.g. TestPrune flushing a schema-drifted DB) would leave the plugin
    // indefinitely displaying Running with no work dispatched — the classic
    // stuck-state hang. The framework now catches the throw and forces Failed.
    let reportedStatuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let mutable registeredCmd: CommandHandler option = None

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "throwing-handler"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(System.DateTime.UtcNow))
                        failwith "simulated DB schema drift"
                        return state
                    | _ -> return state
                }
          Commands = [ "noop", fun _state _args -> async { return "ok" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { Checker = checker
              RepoRoot = "/tmp/repo"
              ReportStatus = fun _ status -> reportedStatuses.Enqueue(status)
              ReportErrors = fun _ _ _ -> ()
              ClearErrors = fun _ _ -> ()
              ClearPlugin = fun _ -> ()
              EmitBuildCompleted = fun _ -> ()
              EmitTestRunStarted = fun _ -> ()
              EmitTestProgress = fun _ -> ()
              EmitTestRunCompleted = fun _ -> ()
              EmitCommandCompleted = fun _ -> ()
              RegisterCommand = fun (_, cmd) -> registeredCmd <- Some cmd
              TaskCache = None
              StartSubtask = fun _ _ _ -> ()
              UpdateSubtask = fun _ _ _ -> ()
              EndSubtask = fun _ _ -> ()
              Log = fun _ _ -> ()
              SetSummary = fun _ _ -> ()
              SetNextTerminalOutcome = fun _ _ -> () }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/Foo.fs" ]))

    // Drain the agent: the command queues behind the failing FileChanged, so
    // awaiting it guarantees both statuses have been recorded by the time we read.
    registeredCmd.Value [||] |> Async.RunSynchronously |> ignore

    let statuses = reportedStatuses.ToArray() |> List.ofArray
    // Must have seen Running first, then Failed — never just Running.
    test
        <@
            statuses
            |> List.exists (function
                | Running _ -> true
                | _ -> false)
        @>

    test
        <@
            statuses
            |> List.exists (function
                | Failed(msg, _) -> msg.Contains("simulated DB schema drift")
                | _ -> false)
        @>

    // Critically: the last observed status must be terminal (not Running).
    let lastStatus = statuses |> List.last

    test
        <@
            match lastStatus with
            | Failed _ -> true
            | Completed _ -> true
            | _ -> false
        @>
