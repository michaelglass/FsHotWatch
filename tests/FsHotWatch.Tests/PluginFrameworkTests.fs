module FsHotWatch.Tests.PluginFrameworkTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

/// Create an FSharpChecker for tests.
let private checker = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()

/// Helper: register a handler with no-op host functions by default.
let private registerWith
    (handler: PluginHandler<'State, 'Msg>)
    (registerCommand: (string * CommandHandler -> unit) option)
    =
    let registerCommand = defaultArg registerCommand (fun _ -> ())

    registerHandler
        checker
        "/tmp/repo"
        (fun _ _ -> ())
        (fun _ _ _ -> ())
        (fun _ _ -> ())
        (fun _ -> ())
        (fun _ -> ())
        registerCommand
        handler

/// Register with all defaults.
let private registerDefault handler = registerWith handler None

[<Fact>]
let ``registered plugin dispatches FileChanged`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler =
        { Name = "test-fc"
          Init = false
          Update =
            fun _ctx _state event ->
                async {
                    match event with
                    | FileChanged _ -> return true
                    | _ -> return _state
                }
          Commands = [ "was-called", fun state _args -> async { return $"%b{state}" } ]
          Subscriptions =
            { PluginSubscriptions.none with
                FileChanged = true } }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    test <@ reg.OnFileChanged.IsSome @>
    reg.OnFileChanged.Value(SourceChanged [ "/tmp/repo/Foo.fs" ])

    // Poll the command deterministically — it queues behind the FileChanged message
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

[<Fact>]
let ``registered plugin skips unsubscribed events`` () =
    let handler =
        { Name = "test-skip"
          Init = 0
          Update = fun _ctx state _event -> async { return state + 1 }
          Commands = []
          Subscriptions =
            { FileChanged = true
              FileChecked = false
              BuildCompleted = false
              TestCompleted = true } }

    let reg = registerDefault handler

    test <@ reg.OnFileChanged.IsSome @>
    test <@ reg.OnFileChecked.IsNone @>
    test <@ reg.OnBuildCompleted.IsNone @>
    test <@ reg.OnTestCompleted.IsSome @>

[<Fact>]
let ``commands query agent state`` () =
    async {
        let mutable registeredCmd: (string * CommandHandler) option = None

        let handler =
            { Name = "test-cmd"
              Init = 42
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged _ -> return state + 1
                        | _ -> return state
                    }
              Commands = [ "get-count", fun state _args -> async { return $"%d{state}" } ]
              Subscriptions =
                { PluginSubscriptions.none with
                    FileChanged = true } }

        let _reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        test <@ registeredCmd.IsSome @>
        let (cmdName, cmdHandler) = registeredCmd.Value
        test <@ cmdName = "get-count" @>

        // Query initial state
        let! result = cmdHandler [||]
        test <@ result = "42" @>
    }
    |> Async.RunSynchronously

[<Fact>]
let ``Custom messages work for self-posting`` () =
    async {
        let mutable customReceived = false

        let mutable registeredCmd: (string * CommandHandler) option = None

        let handler =
            { Name = "test-custom"
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
              Subscriptions =
                { PluginSubscriptions.none with
                    FileChanged = true } }

        let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        reg.OnFileChanged.Value(SourceChanged [ "/tmp/repo/Foo.fs" ])

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

[<Fact>]
let ``handler errors are recovered`` () =
    async {
        let mutable callCount = 0
        let mutable registeredCmd: CommandHandler option = None

        let handler =
            { Name = "test-recover"
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
              Subscriptions =
                { PluginSubscriptions.none with
                    FileChanged = true } }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        // First: throw. Second: normal — should still work.
        reg.OnFileChanged.Value(SourceChanged [ "/throw" ])
        reg.OnFileChanged.Value(SourceChanged [ "/ok" ])

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
