module FsHotWatch.Tests.PluginFrameworkTests

open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

/// Shared FSharpChecker for tests.
let private checker = TestHelpers.sharedChecker.Value

/// No-op PluginHostServices — tests override just the fields they observe.
let private defaultServices: PluginHostServices =
    { Checker = checker
      RepoRoot = "/tmp/repo"
      ReportStatus = fun _ _ -> ()
      ReportErrors = fun _ _ _ -> ()
      ClearErrors = fun _ _ -> ()
      ClearPlugin = fun _ -> ()
      GetPluginDiagnostics = fun _ -> Map.empty
      EmitBuildCompleted = fun _ -> ()
      EmitTestRunStarted = fun _ -> ()
      EmitTestProgress = fun _ -> ()
      EmitTestRunCompleted = fun _ -> ()
      EmitCommandCompleted = fun _ -> ()
      RegisterCommand = fun _ -> ()
      TaskCache = None
      StartSubtask = fun _ _ _ -> ()
      UpdateSubtask = fun _ _ _ -> ()
      EndSubtask = fun _ _ -> ()
      Log = fun _ _ -> ()
      SetNextTerminalOutcome = fun _ _ -> ()
      FcsSuppressedCodes = Set.empty
      ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

/// Helper: register a handler with no-op host functions by default.
let private registerWith
    (handler: PluginHandler<'State, 'Msg>)
    (registerCommand: (string * CommandHandler -> unit) option)
    =
    let registerCommand = defaultArg registerCommand (fun _ -> ())

    registerHandler
        { defaultServices with
            RegisterCommand = registerCommand }
        handler

/// Register with all defaults.
let private registerDefault handler = registerWith handler None

[<Fact(Timeout = 15000)>]
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
          Commands = [ "was-called", fun _ctx state _args -> async { return $"%b{state}" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    // Running a command is how these tests synchronize: it queues behind the dispatched
    // message in the same mailbox, so awaiting it proves that message was processed.
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

[<Fact(Timeout = 15000)>]
let ``registered plugin skips unsubscribed events`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler =
        { Name = PluginName.create "test-skip"
          Init = 0
          Update = fun _ctx state _event -> async { return state + 1 }
          Commands = [ "get-count", fun _ctx state _args -> async { return $"%d{state}" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged; SubscribeTestRunCompleted ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    reg.Dispatch(
        DispatchFileChecked
            { File = AbsFilePath.create "/tmp/repo/Foo.fs"
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

    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "1" @>

[<Fact(Timeout = 20000)>]
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
              Commands = [ "get-count", fun _ctx state _args -> async { return $"%d{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let _reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        test <@ registeredCmd.IsSome @>
        let (cmdName, cmdHandler) = registeredCmd.Value
        test <@ cmdName = "get-count" @>

        let! result = cmdHandler [||]
        test <@ result = "42" @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 15000)>]
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
              Commands = [ "got-custom", fun _ctx state _args -> async { return $"%b{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

        let (_, cmdHandler) = registeredCmd.Value

        waitUntil
            (fun () ->
                let r = cmdHandler [||] |> Async.RunSynchronously
                r = "true")
            5000

        test <@ customReceived @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 20000)>]
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
              Commands = [ "get-state", fun _ctx state _args -> async { return $"%d{state}" } ]
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/throw" ]))
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/ok" ]))

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

[<Fact(Timeout = 15000)>]
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
          Commands = [ "was-called", fun _ctx state _args -> async { return $"%b{state}" } ]
          Subscriptions = Set.ofList [ SubscribeCommandCompleted ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    reg.Dispatch(
        DispatchCommandCompleted
            { Name = "my-cmd"
              Outcome = CommandSucceeded "done" }
    )

    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

// --- Framework invariant: handler throws -> plugin status reaches terminal Failed ---

[<Fact(Timeout = 15000)>]
let ``handler that throws after ReportStatus(Running) still transitions status to Failed`` () =
    // A handler that reported Running and then threw (e.g. TestPrune flushing a
    // schema-drifted DB) used to leave the plugin displaying Running forever with no work
    // dispatched. The framework catches the throw and forces Failed.
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
          Commands = [ "noop", fun _ctx _state _args -> async { return "ok" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ status -> reportedStatuses.Enqueue(status)
                RegisterCommand = fun (_, cmd) -> registeredCmd <- Some cmd }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/Foo.fs" ]))

    // Drains the agent: the command queues behind the failing FileChanged, so awaiting it
    // guarantees both statuses have been recorded by the time we read.
    registeredCmd.Value [||] |> Async.RunSynchronously |> ignore

    let statuses = reportedStatuses.ToArray() |> List.ofArray

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
                | Failed(msg, _, _) -> msg.Contains("simulated DB schema drift")
                | _ -> false)
        @>

    // The LAST observed status must be terminal — Running-then-Failed is not enough if
    // something stamps Running back over it.
    let lastStatus = statuses |> List.last

    test
        <@
            match lastStatus with
            | Failed _ -> true
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``handler that throws records ex.ToString() (full type+stack) in Failed status, not just ex.Message`` () =
    // The user-visible Failed status must carry the full exception, or the user has to grep
    // daemon.log to work out which throw site fired.
    let reportedStatuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let mutable registeredCmd: CommandHandler option = None

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "throwing-detail-handler"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> raise (System.InvalidOperationException("kaboom-distinctive-msg"))
                    | _ -> return state
                }
          Commands = [ "noop", fun _ctx _state _args -> async { return "ok" } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ status -> reportedStatuses.Enqueue(status)
                RegisterCommand = fun (_, cmd) -> registeredCmd <- Some cmd }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/Foo.fs" ]))
    registeredCmd.Value [||] |> Async.RunSynchronously |> ignore

    let statuses = reportedStatuses.ToArray() |> List.ofArray

    let failedMsg =
        statuses
        |> List.tryPick (function
            | Failed(m, _, _) -> Some m
            | _ -> None)

    test <@ failedMsg.IsSome @>
    let msg = failedMsg.Value
    // Must include the original message (ex.Message contents).
    test <@ msg.Contains("kaboom-distinctive-msg") @>
    // Must include the exception type name (only ex.ToString() supplies this).
    test <@ msg.Contains("InvalidOperationException") @>

// --- Cache replay: pre-populated cache hits on first dispatch (no warm-start gate) ---

/// Build host services with a provided TaskCache for these tests.
let private servicesWithCache (cache: TaskCache.ITaskCache) (registerCommand: string * CommandHandler -> unit) =
    { defaultServices with
        RegisterCommand = registerCommand
        TaskCache = Some cache }

[<Fact(Timeout = 20000)>]
let ``pre-populated cache replays on the very first dispatch`` () =
    // The old `RequireWarmStart` gate made plugins skip replay until they had completed once
    // per session. BatchChecked closes the half-formed-key window it existed for, so the
    // gate is gone and replay fires immediately.
    async {
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "k"
        let pluginNameStr = "cache-replay"
        let compKey: TaskCache.CompositeKey = { Plugin = pluginNameStr; File = None }

        cache.Set
            compKey
            cacheKey
            { CacheKey = cacheKey
              Errors = []
              Status = cachedRunDone
              EmittedEvents = [] }

        let updateCalls = ref 0
        let mutable registeredCmd: CommandHandler option = None

        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            System.Threading.Interlocked.Increment(updateCalls) |> ignore
                            ctx.ReportStatus(completedAt System.DateTime.UtcNow)
                        | _ -> ()

                        return state
                    }
              Commands = [ "drain", fun _ctx _state _args -> async { return "ok" } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = Some(fun _ -> Some cacheKey)
              Teardown = None }

        let reg =
            registerHandler (servicesWithCache cache (fun (_, cmd) -> registeredCmd <- Some cmd)) handler

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/A.fs" ]))
        let! _ = registeredCmd.Value [||]
        test <@ !updateCalls = 0 @>
    }
    |> Async.RunSynchronously

// --- Cache key computed ONCE per dispatched event (lookup + store share it) ---

[<Fact(Timeout = 20000)>]
let ``cache key is computed exactly once per dispatched event on a cache miss`` () =
    // Perf regression guard: the framework called `cacheKeyFn event` twice per dispatch,
    // once for the LOOKUP and once for the STORE. For BuildPlugin that key is a full
    // content-hash of the project graph, so a miss — common while editing — paid two SHA-256
    // passes per trigger. A miss exercises both paths, so one call is the whole assertion.
    async {
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "k"
        let pluginNameStr = "key-once-miss"
        let keyCalls = ref 0
        let mutable registeredCmd: CommandHandler option = None

        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ -> ctx.ReportStatus(completedAt System.DateTime.UtcNow)
                        | _ -> ()

                        return state
                    }
              Commands = [ "drain", fun _ctx _state _args -> async { return "ok" } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey =
                Some(fun _ ->
                    System.Threading.Interlocked.Increment(keyCalls) |> ignore
                    Some cacheKey)
              Teardown = None }

        let reg =
            registerHandler (servicesWithCache cache (fun (_, cmd) -> registeredCmd <- Some cmd)) handler

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/A.fs" ]))
        let! _ = registeredCmd.Value [||]
        test <@ !keyCalls = 1 @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 20000)>]
let ``cache key is computed exactly once per dispatched event on a cache hit`` () =
    // A hit never reaches the store path, so even the old code computed the key once here —
    // this pins that the once-per-event threading did not add a recompute on the hit path.
    async {
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "k"
        let pluginNameStr = "key-once-hit"
        let compKey: TaskCache.CompositeKey = { Plugin = pluginNameStr; File = None }

        cache.Set
            compKey
            cacheKey
            { CacheKey = cacheKey
              Errors = []
              Status = cachedRunDone
              EmittedEvents = [] }

        let keyCalls = ref 0
        let mutable registeredCmd: CommandHandler option = None

        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update = fun _ctx state _event -> async { return state }
              Commands = [ "drain", fun _ctx _state _args -> async { return "ok" } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey =
                Some(fun _ ->
                    System.Threading.Interlocked.Increment(keyCalls) |> ignore
                    Some cacheKey)
              Teardown = None }

        let reg =
            registerHandler (servicesWithCache cache (fun (_, cmd) -> registeredCmd <- Some cmd)) handler

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/A.fs" ]))
        let! _ = registeredCmd.Value [||]
        test <@ !keyCalls = 1 @>
    }
    |> Async.RunSynchronously

// --- RunExclusive primitive: per-handler single-flight (always-Drop) ---

/// Drives RunExclusive end-to-end through the agent. `started` increments at the top of the
/// gated work; `completed` increments inside the agent when the framework posts the work's
/// return value back as `Custom`, which is what proves the framework posts it at all.
type private RxMsg = RxDone of int

[<Fact(Timeout = 20000)>]
let ``RunExclusive does not start a second run while the first holds the slot`` () =
    async {
        let started = ref 0
        let completed = ref 0
        let gate = new System.Threading.ManualResetEventSlim(false)
        let mutable registeredCmd: CommandHandler option = None

        let handler: PluginHandler<int, RxMsg> =
            { Name = PluginName.create "rx-drop"
              Init = 0
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            // Both outcomes are the subject here: the first dispatch claims,
                            // the second lands on SlotBusy. The CALLER decides what a
                            // refusal means — this one skips.
                            match
                                ctx.RunExclusive
                                    "k"
                                    (async {
                                        System.Threading.Interlocked.Increment(started) |> ignore
                                        gate.Wait()
                                        return RxDone 1
                                    })
                            with
                            | Claimed
                            | SlotBusy -> ()

                            return state
                        | Custom(RxDone n) ->
                            System.Threading.Interlocked.Increment(completed) |> ignore
                            return state + n
                        | _ -> return state
                    }
              Commands = [ "get", fun _ctx s _ -> async { return string s } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/a.fs" ]))
        waitUntil (fun () -> !started = 1) 12000
        test <@ !started = 1 @>

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/b.fs" ]))
        // Drains the agent, so the second FileChanged is known to have been processed
        // before the assertion — otherwise "not started" would just mean "not yet".
        let! _ = registeredCmd.Value [||]
        test <@ !started = 1 @>

        gate.Set()
        waitUntil (fun () -> !completed = 1) 12000
        test <@ !completed = 1 @>

        // After completion, a fresh dispatch must run.
        gate.Reset()
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/c.fs" ]))
        waitUntil (fun () -> !started = 2) 12000
        gate.Set()
        waitUntil (fun () -> !completed = 2) 12000
        test <@ !started = 2 @>
        test <@ !completed = 2 @>
    }
    |> Async.RunSynchronously

// --- Cache replay: emitted events + error-replay branches ---

[<Fact(Timeout = 30000)>]
let ``cache replay re-emits BuildCompleted, TestRunStarted, TestProgress, TestRunCompleted, CommandCompleted from EmittedEvents``
    ()
    =
    async {
        // A cached result carrying every CachedEvent variant the framework knows how to
        // replay: on dispatch, each must be re-fired through its host service.
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "k"
        let pluginNameStr = "replay-emit"
        let compKey: TaskCache.CompositeKey = { Plugin = pluginNameStr; File = None }

        let runId = System.Guid.NewGuid()
        let startedAt = System.DateTime.UtcNow
        let testResults = Map.empty

        let emitted: TaskCache.CachedEvent list =
            [ TaskCache.CachedBuildCompleted BuildSucceeded
              TaskCache.CachedTestRunStarted { RunId = runId; StartedAt = startedAt }
              TaskCache.CachedTestProgress
                  { RunId = runId
                    NewResults = testResults }
              TaskCache.CachedTestRunCompleted
                  { RunId = runId
                    TotalElapsed = System.TimeSpan.Zero
                    Outcome = TestRunOutcome.Normal
                    Results = testResults
                    Verification = Ran RunScope.FullSuite }
              TaskCache.CachedCommandCompleted
                  { Name = "noop"
                    Outcome = CommandSucceeded "ok" } ]

        cache.Set
            compKey
            cacheKey
            { CacheKey = cacheKey
              // The three forms the replay error loop branches on: file="*" (ClearPlugin),
              // empty entries for a real file (ClearErrors), and a real entry list
              // (ReportErrors).
              Errors =
                [ ("*", [])
                  ("/tmp/clear-me.fs", [])
                  ("/tmp/has-errors.fs", [ ErrorEntry.error "x" ]) ]
              Status = cachedRunDone
              EmittedEvents = emitted }

        let buildSeen = ref 0
        let trsSeen = ref 0
        let tpSeen = ref 0
        let trcSeen = ref 0
        let ccSeen = ref 0
        let clearedPlugin = ref 0
        let clearedFiles = System.Collections.Generic.List<string>()
        let reportedFiles = System.Collections.Generic.List<string>()
        let mutable registeredCmd: CommandHandler option = None

        let services =
            { defaultServices with
                ReportErrors = fun _ file _ -> lock reportedFiles (fun () -> reportedFiles.Add(file))
                ClearErrors = fun _ file -> lock clearedFiles (fun () -> clearedFiles.Add(file))
                ClearPlugin = fun _ -> System.Threading.Interlocked.Increment(clearedPlugin) |> ignore
                EmitBuildCompleted = fun _ -> System.Threading.Interlocked.Increment(buildSeen) |> ignore
                EmitTestRunStarted = fun _ -> System.Threading.Interlocked.Increment(trsSeen) |> ignore
                EmitTestProgress = fun _ -> System.Threading.Interlocked.Increment(tpSeen) |> ignore
                EmitTestRunCompleted = fun _ -> System.Threading.Interlocked.Increment(trcSeen) |> ignore
                EmitCommandCompleted = fun _ -> System.Threading.Interlocked.Increment(ccSeen) |> ignore
                RegisterCommand = fun (_, cmd) -> registeredCmd <- Some cmd
                TaskCache = Some cache }

        let updateCalls = ref 0

        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update =
                fun _ctx state _event ->
                    async {
                        System.Threading.Interlocked.Increment(updateCalls) |> ignore
                        return state
                    }
              Commands = [ "drain", fun _ _ _ -> async { return "ok" } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = Some(fun _ -> Some cacheKey)
              Teardown = None }

        let reg = registerHandler services handler
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/A.fs" ]))
        let! _ = registeredCmd.Value [||]

        test <@ !updateCalls = 0 @>

        test <@ !buildSeen = 1 @>
        test <@ !trsSeen = 1 @>
        test <@ !tpSeen = 1 @>
        test <@ !trcSeen = 1 @>
        test <@ !ccSeen = 1 @>

        // `>=` rather than `=`: the pre-replay ClearPlugin (FileChanged carries File=None)
        // fires in addition to the one the "*" entry replays.
        test <@ !clearedPlugin >= 1 @>
        test <@ clearedFiles |> Seq.contains "/tmp/clear-me.fs" @>
        test <@ reportedFiles |> Seq.contains "/tmp/has-errors.fs" @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 30000)>]
let ``RunExclusive releases slot when work raises and logs without re-posting completion`` () =
    // When work throws, completion stays ValueNone (no Custom message posted) and the slot
    // is released so a subsequent RunExclusive can run.
    async {
        let started = ref 0
        let completed = ref 0
        let mutable registeredCmd: CommandHandler option = None
        let mutable capturedCtx: PluginCtx<RxMsg> option = None

        let handler: PluginHandler<int, RxMsg> =
            { Name = PluginName.create "rx-throw"
              Init = 0
              Update =
                fun ctx state event ->
                    async {
                        capturedCtx <- Some ctx

                        match event with
                        | FileChanged(SourceChanged [ "/throw" ]) ->
                            let claim =
                                ctx.RunExclusive
                                    "k"
                                    (async {
                                        System.Threading.Interlocked.Increment(started) |> ignore
                                        return failwith "boom"
                                    })

                            test <@ claim = Claimed @>
                            return state
                        | FileChanged _ ->
                            let claim =
                                ctx.RunExclusive
                                    "k"
                                    (async {
                                        System.Threading.Interlocked.Increment(started) |> ignore
                                        return RxDone 1
                                    })

                            test <@ claim = Claimed @>
                            return state
                        | Custom(RxDone n) ->
                            System.Threading.Interlocked.Increment(completed) |> ignore
                            return state + n
                        | _ -> return state
                    }
              Commands = [ "get", fun _ s _ -> async { return string s } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/throw" ]))
        let! _ = registeredCmd.Value [||]
        // The 20s polls are deliberately generous: under heavy parallel-collection load the
        // thread-pool can lag scheduling the runOne async by several seconds.
        waitUntil (fun () -> !started = 1) 20000

        waitUntil (fun () -> not (capturedCtx.Value.IsRunning "k")) 20000
        test <@ not (capturedCtx.Value.IsRunning "k") @>
        // No completion posted — Custom RxDone never fired.
        test <@ !completed = 0 @>

        // A subsequent dispatch running is what proves the slot was released.
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/ok.fs" ]))
        waitUntil (fun () -> !completed = 1) 20000
        test <@ !started = 2 @>
        test <@ !completed = 1 @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 30000)>]
let ``RunExclusive forces a terminal Failed status when work raises (no strand)`` () =
    // AUTOMATION-65's fresh-workspace daemon wedge. On the fault path the completion message
    // that normally drives the plugin to terminal is never posted, and plugins routinely
    // report Running just before launching the work (test-prune does, immediately before
    // `RunExclusive "tests"`). Without the framework forcing a terminal the plugin sits
    // Running forever while IsBusy/AnyPluginBusy report false — which hangs the check's
    // WaitForComplete and then lets idle-exit fire mid-wait.
    async {
        let statuses =
            System.Collections.Concurrent.ConcurrentQueue<PluginName * PluginStatus>()

        let mutable capturedCtx: PluginCtx<RxMsg> option = None

        let isFailed =
            fun () ->
                statuses
                |> Seq.exists (fun (_, s) ->
                    match s with
                    | Failed _ -> true
                    | _ -> false)

        let services: PluginHostServices =
            { defaultServices with
                ReportStatus = fun name status -> statuses.Enqueue(name, status) }

        let handler: PluginHandler<int, RxMsg> =
            { Name = PluginName.create "rx-strand"
              Init = 0
              Update =
                fun ctx state event ->
                    async {
                        capturedCtx <- Some ctx

                        match event with
                        | FileChanged _ ->
                            // The framework reports Running at the claim; the
                            // exclusive work faults before posting completion.
                            let claim = ctx.RunExclusive "k" (async { return failwith "boom" })
                            test <@ claim = Claimed @>
                            return state
                        | _ -> return state
                    }
              Commands = [ "get", fun _ s _ -> async { return string s } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              Teardown = None }

        let reg = registerHandler services handler
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/throw" ]))

        waitUntil isFailed 20000
        test <@ isFailed () @>

        // ...and the exclusion slot is released so subsequent runs can proceed.
        waitUntil (fun () -> not (capturedCtx.Value.IsRunning "k")) 20000
        test <@ not (capturedCtx.Value.IsRunning "k") @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 20000)>]
let ``IsRunning reports true while work in flight, false after completion`` () =
    async {
        let gate = new System.Threading.ManualResetEventSlim(false)
        let observedRunning = ref false
        let mutable registeredCmd: CommandHandler option = None
        let mutable capturedCtx: PluginCtx<RxMsg> option = None

        let handler: PluginHandler<int, RxMsg> =
            { Name = PluginName.create "rx-isrunning"
              Init = 0
              Update =
                fun ctx state event ->
                    async {
                        capturedCtx <- Some ctx

                        match event with
                        | FileChanged _ ->
                            let claim =
                                ctx.RunExclusive
                                    "k"
                                    (async {
                                        gate.Wait()
                                        return RxDone 1
                                    })

                            test <@ claim = Claimed @>
                            return state
                        | Custom(RxDone _) -> return state + 1
                        | _ -> return state
                    }
              Commands = [ "get", fun _ctx s _ -> async { return string s } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              Teardown = None }

        let reg = registerWith handler (Some(fun (_, cmd) -> registeredCmd <- Some cmd))

        reg.Dispatch(DispatchFileChanged(SourceChanged [ "x" ]))
        // Drains, so ctx is captured and RunExclusive has been called.
        let! _ = registeredCmd.Value [||]
        waitUntil (fun () -> capturedCtx.Value.IsRunning "k") 12000
        observedRunning.Value <- capturedCtx.Value.IsRunning "k"
        test <@ !observedRunning @>
        test <@ not (capturedCtx.Value.IsRunning "other") @>

        gate.Set()
        waitUntil (fun () -> not (capturedCtx.Value.IsRunning "k")) 12000
        test <@ not (capturedCtx.Value.IsRunning "k") @>
    }
    |> Async.RunSynchronously

// --- BatchChecked dispatch (commit 1: framework adds the event) ---

[<Fact(Timeout = 15000)>]
let ``plugin subscribing to BatchChecked receives event`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None
    let received = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

    let handler =
        { Name = PluginName.create "test-bc"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | PluginEvent.BatchChecked b ->
                        received.Enqueue(b)
                        return state
                    | _ -> return state
                }
          Commands = [ "drain", fun _ctx _state _args -> async { return "ok" } ]
          Subscriptions = Set.ofList [ SubscribeBatchChecked ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    let now = System.DateTime.UtcNow

    let batch =
        { Trigger = BootScan
          Files = [ AbsFilePath.create "/tmp/repo/Foo.fs" ]
          Generation = 7L
          StartedAt = now
          CompletedAt = now.AddMilliseconds(50.0) }

    reg.Dispatch(DispatchBatchChecked batch)

    let (_, cmdHandler) = registeredCmd.Value
    cmdHandler [||] |> Async.RunSynchronously |> ignore

    test <@ received.Count = 1 @>
    let observed = received.ToArray().[0]
    test <@ observed.Generation = 7L @>
    test <@ observed.Files.Length = 1 @>

    match observed.Trigger with
    | BootScan -> ()
    | _ -> failwith "expected BootScan trigger"

[<Fact(Timeout = 15000)>]
let ``plugin not subscribing to BatchChecked does not receive event`` () =
    let mutable registeredCmd: (string * CommandHandler) option = None
    let mutable batchSeen = false

    let handler =
        { Name = PluginName.create "test-bc-skip"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | PluginEvent.BatchChecked _ ->
                        batchSeen <- true
                        return state
                    | _ -> return state
                }
          Commands = [ "drain", fun _ctx _state _args -> async { return "ok" } ]
          // Subscribed to FileChanged only — must NOT see BatchChecked.
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg = registerWith handler (Some(fun cmd -> registeredCmd <- Some cmd))

    let now = System.DateTime.UtcNow

    reg.Dispatch(
        DispatchBatchChecked
            { Trigger = BootScan
              Files = []
              Generation = 1L
              StartedAt = now
              CompletedAt = now }
    )

    // A subscribed event afterwards, so the mailbox can be drained at all.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    let (_, cmdHandler) = registeredCmd.Value
    cmdHandler [||] |> Async.RunSynchronously |> ignore

    test <@ not batchSeen @>

// --- safeUpdate × exclusive runs (AUTOMATION-99) ----------------------------

[<Fact(Timeout = 20000)>]
let ``a handler throw while an exclusive run is in flight does not stomp a terminal status over it`` () =
    // The forced-Failed net exists for "threw before any terminal report, and nothing else
    // will ever report one". While an exclusive run is in flight that premise is false — the
    // run's completion path will deliver a terminal — so stomping Failed over the live
    // Running is exactly the AUTOMATION-99 manufactured terminal. The crash is still logged.
    let statuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    use runGate = new System.Threading.SemaphoreSlim(0, 1)

    let handler: PluginHandler<unit, string> =
        { Name = PluginName.create "throw-mid-run"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "/start" ]) ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! runGate.WaitAsync() |> Async.AwaitTask
                                    return "run-finished"
                                })

                        test <@ claim = Claimed @>
                        return state
                    | FileChanged _ -> return failwith "boom mid-run"
                    | Custom _ ->
                        // The run's earned verdict.
                        ctx.ReportStatus(
                            Completed(System.DateTime.UtcNow, RunVerdict.create "run finished" System.TimeSpan.Zero)
                        )

                        return state
                    | _ -> return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ s -> statuses.Enqueue s }
            handler

    // Launch the gated run, then crash the handler while the run is in flight.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/start" ]))
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/boom" ]))

    // "Both mailbox messages are done" is not directly observable — the run token keeps
    // IsBusy true throughout — so this waits on the recorded statuses instead, then sleeps
    // to give a stomping terminal time to appear if one is coming.
    waitUntil (fun () -> statuses.Count >= 1) 10000
    System.Threading.Thread.Sleep 300

    test
        <@
            statuses
            |> Seq.forall (fun s ->
                match s with
                | Failed _ -> false
                | _ -> true)
        @>

    test <@ reg.IsBusy() @> // the run token still holds the plugin busy

    // Release the run: its completion message delivers the earned verdict.
    runGate.Release() |> ignore

    waitUntil
        (fun () ->
            statuses
            |> Seq.exists (fun s ->
                match s with
                | Completed(_, v) -> v.Summary = "run finished"
                | _ -> false))
        10000

    test
        <@
            statuses
            |> Seq.exists (fun s ->
                match s with
                | Completed(_, v) -> v.Summary = "run finished"
                | _ -> false)
        @>

// ---------------------------------------------------------------------------
// RunClaim: a refused claim is a VALUE the caller cannot drop (AUTOMATION-99)
// ---------------------------------------------------------------------------

type private ClaimMsg = ClaimDone

/// A one-shot gate for holding a background run open.
///
/// Deliberately NOT a disposable primitive: a `use`d SemaphoreSlim is disposed when the
/// test's scope ends, which can happen while the gated run is still resuming on it. The run
/// then faults with ObjectDisposedException, the framework forces a terminal, and the
/// thread-pool churn destabilises NEIGHBOURING tests. A TaskCompletionSource has no such
/// lifetime.
type private Gate() =
    let tcs =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    /// Await the gate from inside the run.
    member _.Wait: Async<unit> = Async.AwaitTask tcs.Task

    /// Let the run proceed. Idempotent.
    member _.Open() = tcs.TrySetResult(()) |> ignore

/// Open the gate and wait for the plugin to go fully quiet, so no background
/// run outlives the test that started it.
let private openAndDrain (gate: Gate) (reg: RegisteredPlugin) =
    gate.Open()
    waitUntil (fun () -> not (reg.IsBusy())) 15000

[<Fact(Timeout = 20000)>]
let ``RunExclusive reports Running at the claim — the framework owns the start`` () =
    // The FRAMEWORK reports Running when it claims the slot, so a plugin cannot launch work
    // and forget to announce it — CoveragePlugin shipped exactly that gap, which also
    // starved the host's work-cycle generation counter.
    let statuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "silent-launcher"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        // NOTE: no ReportStatus(Running) here — deliberately.
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        test <@ claim = Claimed @>
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ s -> statuses.Enqueue s }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/a.fs" ]))

    waitUntil
        (fun () ->
            statuses
            |> Seq.exists (function
                | Running _ -> true
                | _ -> false))
        10000

    test
        <@
            statuses
            |> Seq.exists (function
                | Running _ -> true
                | _ -> false)
        @>

    openAndDrain gate reg

[<Fact(Timeout = 20000)>]
let ``RunExclusive returns SlotBusy when the slot is held — the work is NOT started`` () =
    // `runExclusive` used to return unit and drop the refused work with a debug log, so the
    // caller could not tell: a `run-tests` whose claim was refused replied "busy" and exited
    // 0 having run nothing, and a reply TCS resolved inside the dropped work never resolved.
    let started = ref 0
    let claims = System.Collections.Concurrent.ConcurrentQueue<RunClaim>()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "claim-refused"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    System.Threading.Interlocked.Increment(started) |> ignore
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        claims.Enqueue claim
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let reg = registerDefault handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/a.fs" ]))
    waitUntil (fun () -> System.Threading.Volatile.Read(&started.contents) = 1) 10000

    // Second trigger while the first run holds the slot.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/b.fs" ]))
    waitUntil (fun () -> claims.Count = 2) 10000

    let observed = claims.ToArray() |> List.ofArray
    test <@ observed = [ Claimed; SlotBusy ] @>
    // The refused work NEVER ran, which is what makes dropping it something the caller has
    // to answer for rather than a silent no-op.
    let startedCount = System.Threading.Volatile.Read(&started.contents)
    test <@ startedCount = 1 @>

    openAndDrain gate reg

[<Fact(Timeout = 20000)>]
let ``a terminal stamped by ANY plugin path while a run is in flight is suppressed at the funnel`` () =
    // The ownership rule lives at the ONE choke point every plugin-originated status passes
    // through, rather than being re-implemented as `if not (ctx.IsRunning "tests")` in each
    // handler — the duplication class that caused this bug in the first place.
    let statuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "stomper"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "/start" ]) ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        test <@ claim = Claimed @>
                    | FileChanged _ ->
                        // A per-file handler stamping a terminal WHILE the run is live — the
                        // manufactured ✓ that made `check` exit 0.
                        ctx.ReportStatus(PluginStatus.completedNow "per-file work, no run due" System.TimeSpan.Zero)
                    | Custom ClaimDone ->
                        ctx.ReportStatus(
                            PluginStatus.completedNow "the run's earned verdict" (System.TimeSpan.FromSeconds 1.0)
                        )
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ s -> statuses.Enqueue s }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/start" ]))

    waitUntil
        (fun () ->
            statuses
            |> Seq.exists (function
                | Running _ -> true
                | _ -> false))
        10000

    // Stomp attempt, while the run is provably still gated.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/stomp" ]))
    System.Threading.Thread.Sleep 500

    let midRun = statuses.ToArray() |> List.ofArray

    test
        <@
            midRun
            |> List.forall (function
                | Completed _
                | Failed _ -> false
                | Idle
                | Running _ -> true)
        @>

    // Releasing the run lets its OWN verdict through: suppression is scoped to the live run,
    // not a permanent silencing of the plugin.
    gate.Open()

    waitUntil
        (fun () ->
            statuses
            |> Seq.exists (function
                | Completed(_, v) -> v.Summary = "the run's earned verdict"
                | _ -> false))
        10000

    test
        <@
            statuses
            |> Seq.exists (function
                | Completed(_, v) -> v.Summary = "the run's earned verdict"
                | _ -> false)
        @>

    waitUntil (fun () -> not (reg.IsBusy())) 15000

// ---------------------------------------------------------------------------
// The SAME guarantees on the CACHE-ENABLED path (the capturing ctx).
//
// `runAndCache` hands the handler a DIFFERENT PluginCtx — the one that records side effects
// for the task cache. Every ownership rule must hold there too, or the guarantee only covers
// plugins with no cache key, which is exactly the ones TestPrune and Build are not.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``cache path: a terminal stamped while a run is in flight is neither reported NOR cached`` () =
    // The suppressed terminal must not sneak into the task cache either: a cached
    // manufactured ✓ would be REPLAYED on the next matching event, outliving the run that
    // caused it.
    let cache = TaskCache.InMemoryTaskCache()
    let statuses = System.Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "cached-stomper"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "/start" ]) ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        test <@ claim = Claimed @>
                    | FileChanged _ ->
                        ctx.ReportStatus(PluginStatus.completedNow "per-file work, no run due" System.TimeSpan.Zero)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = Some(fun _ -> Some(ContentHash.create "k"))
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                ReportStatus = fun _ s -> statuses.Enqueue s
                TaskCache = Some(cache :> TaskCache.ITaskCache) }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/start" ]))

    waitUntil
        (fun () ->
            statuses
            |> Seq.exists (function
                | Running _ -> true
                | _ -> false))
        10000

    // Stomp attempt while the run is provably gated.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/stomp" ]))
    System.Threading.Thread.Sleep 500

    // Not reported …
    test
        <@
            statuses
            |> Seq.forall (function
                | Completed _
                | Failed _ -> false
                | Idle
                | Running _ -> true)
        @>

    // … and not cached (nothing to replay as a false green later).
    let cached =
        (cache :> TaskCache.ITaskCache).TryGet
            { Plugin = "cached-stomper"
              File = None }
            (ContentHash.create "k")

    test <@ cached.IsNone @>

    openAndDrain gate reg

[<Fact(Timeout = 20000)>]
let ``cache path: a handler that LAUNCHES a run does not cache the terminal it reported first`` () =
    // TestPrune's queued-rerun shape: report the completed run's verdict, then immediately
    // launch the next run. That terminal is about to be superseded, so caching it would
    // replay a verdict the rerun exists to overturn.
    let cache = TaskCache.InMemoryTaskCache()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "relauncher"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        // A terminal FIRST — nothing is running yet, so it is reported …
                        ctx.ReportStatus(PluginStatus.completedNow "prior cycle" System.TimeSpan.Zero)

                        // … then a new run in the SAME capture window.
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        test <@ claim = Claimed @>
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = Some(fun _ -> Some(ContentHash.create "k"))
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                TaskCache = Some(cache :> TaskCache.ITaskCache) }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/a.fs" ]))
    waitUntil (fun () -> reg.IsBusy()) 10000
    System.Threading.Thread.Sleep 500

    let cached =
        (cache :> TaskCache.ITaskCache).TryGet { Plugin = "relauncher"; File = None } (ContentHash.create "k")

    test <@ cached.IsNone @>

    openAndDrain gate reg

[<Fact(Timeout = 20000)>]
let ``cache path: RunExclusive still refuses a second claim and says so`` () =
    let cache = TaskCache.InMemoryTaskCache()
    let claims = System.Collections.Concurrent.ConcurrentQueue<RunClaim>()
    let gate = Gate()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "cached-claimer"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        claims.Enqueue claim
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = Some(fun _ -> Some(ContentHash.create "k"))
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                TaskCache = Some(cache :> TaskCache.ITaskCache) }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/a.fs" ]))
    waitUntil (fun () -> claims.Count = 1) 10000
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/b.fs" ]))
    waitUntil (fun () -> claims.Count = 2) 10000

    test <@ (claims.ToArray() |> List.ofArray) = [ Claimed; SlotBusy ] @>

    openAndDrain gate reg

[<Fact(Timeout = 20000)>]
let ``cache path: a Failed terminal IS cached — with its verdict intact`` () =
    // A failure is a real, replayable result — that is how `check` stays red on an unchanged
    // tree without re-running everything — and because the verdict rides the terminal
    // TRANSITION, the replayed failure carries its summary and elapsed too.
    let cache = TaskCache.InMemoryTaskCache()

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "cached-failer"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                "2 failed: Foo, Bar"
                                "1 passed, 2 failed"
                                (System.TimeSpan.FromSeconds 7.0)
                        )
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = Some(fun _ -> Some(ContentHash.create "k"))
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                TaskCache = Some(cache :> TaskCache.ITaskCache) }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/a.fs" ]))
    waitUntil (fun () -> not (reg.IsBusy())) 10000

    let cached =
        (cache :> TaskCache.ITaskCache).TryGet
            { Plugin = "cached-failer"
              File = None }
            (ContentHash.create "k")

    test <@ cached.IsSome @>

    match cached.Value.Status with
    | TaskCache.CachedRunFailed(err, v) ->
        test <@ err = "2 failed: Foo, Bar" @>
        test <@ v.Summary = "1 passed, 2 failed" @>
        test <@ v.Elapsed = System.TimeSpan.FromSeconds 7.0 @>
    | other -> failwithf "expected a cached run Failed carrying its verdict, got %A" other

[<Fact(Timeout = 25000)>]
let ``cache path: only an EARNED terminal is written — every other shape is skipped`` () =
    // The cache-write gate decides whether a result can be REPLAYED as a verdict later, so
    // it admits exactly one thing: a terminal this cycle actually earned. Nothing reported,
    // still Running, or a terminal immediately superseded by a run the same handler launched
    // must all write NOTHING, or a future hit hands `check` a verdict no run produced.
    let cache = TaskCache.InMemoryTaskCache()
    let c = cache :> TaskCache.ITaskCache
    let gate = Gate()

    let keyFor (event: PluginEvent<ClaimMsg>) =
        match event with
        | FileChanged(SourceChanged [ f ]) -> Some(ContentHash.create f)
        | _ -> Some(ContentHash.create "other")

    let handler: PluginHandler<unit, ClaimMsg> =
        { Name = PluginName.create "gatekeeper"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    // (a) reports NOTHING — no status, so nothing was earned.
                    | FileChanged(SourceChanged [ "/silent" ]) -> ()
                    // (b) reports only Running — the work isn't done yet.
                    | FileChanged(SourceChanged [ "/running" ]) ->
                        ctx.ReportStatus(Running(since = System.DateTime.UtcNow))
                    // (c) a Failed, then launches a run that will supersede it.
                    | FileChanged(SourceChanged [ "/failed-then-run" ]) ->
                        ctx.ReportStatus(PluginStatus.failedNow "prior red" "prior red" System.TimeSpan.Zero)

                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! gate.Wait
                                    return ClaimDone
                                })

                        test <@ claim = Claimed @>
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = Some keyFor
          Teardown = None }

    let reg =
        registerHandler
            { defaultServices with
                TaskCache = Some c }
            handler

    let cachedFor (f: string) =
        c.TryGet { Plugin = "gatekeeper"; File = None } (ContentHash.create f)

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/silent" ]))
    waitUntil (fun () -> not (reg.IsBusy())) 10000
    test <@ (cachedFor "/silent").IsNone @>

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/running" ]))
    waitUntil (fun () -> not (reg.IsBusy())) 10000
    test <@ (cachedFor "/running").IsNone @>

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/failed-then-run" ]))
    System.Threading.Thread.Sleep 500
    test <@ (cachedFor "/failed-then-run").IsNone @>

    openAndDrain gate reg

// --- AUTOMATION-118: is the terminal-status guard ATOMIC against a slot claim? ---
//
// The guard reads the run slots under `runSlotsLock` and used to report the status AFTER
// releasing it, which made it a narrowing rather than a cure: a `RunExclusive` claim landing
// between the check and the report publishes `Running`, and the stale terminal then lands ON
// TOP of the live run — the "content-free ✓ while tests are still running" signature.
//
// These two settle it against the REAL framework, not a model of it. Both PARK the mailbox
// thread inside the host's `ReportStatus` for the stale terminal — a faithful widening of a
// window that genuinely exists, not an invented one — and race a slot claim into it. They
// differ ONLY in where the claim comes from, which is the variable that decides the verdict:
//
//   * from a FOREIGN thread — legal, since `PluginCtx` is a record of closures and nothing
//     confines it to the mailbox. Before the fix this REPRODUCED the race.
//   * from the MAILBOX — the production shape, which cannot reach the window at all,
//     because the mailbox is parked inside the report and cannot dequeue the claim.

type private A118Msg = A118Done

/// Outcome of one race: every status the host observed, plus any terminal it was handed
/// WHILE a run slot was held — the invariant being that no terminal may reach the host while
/// an exclusive run is in flight. `IsRunning` is the framework's OWN view of the slot, so
/// the check cannot drift from what the guard is guarding.
type private A118Result =
    { Statuses: PluginStatus list
      Violations: string list }

/// Park the mailbox inside the host's `ReportStatus` for the first TERMINAL status this
/// plugin reports, run `raceTheClaim` while it is parked, then unpark. `raceTheClaim` is
/// handed the SAME work async on both paths, so neither can pass by quietly failing to start
/// a run.
let private runA118Rig (raceTheClaim: PluginCtx<A118Msg> -> RegisteredPlugin -> Async<A118Msg> -> unit) =
    let statuses = ResizeArray<PluginStatus>()
    let violations = ResizeArray<string>()
    let statusesLock = obj ()

    let windowOpen = new System.Threading.ManualResetEventSlim(false)
    let unpark = new System.Threading.ManualResetEventSlim(false)
    let workEntered = new System.Threading.ManualResetEventSlim(false)
    let workGate = new System.Threading.ManualResetEventSlim(false)

    let mutable ctxRef: PluginCtx<A118Msg> option = None
    let mutable parked = false

    // The run the claim launches. Holds the slot until the test releases `workGate`,
    // so the run is unambiguously LIVE while we assert.
    let rigWork =
        async {
            workEntered.Set()
            workGate.Wait(20000) |> ignore
            return A118Done
        }

    let services =
        { defaultServices with
            ReportStatus =
                fun _name s ->
                    // Park ONLY the first terminal — the stale one. This is the instant
                    // the guard has decided "no run owns the status" and is committing.
                    if PluginStatus.isTerminal s && not parked then
                        parked <- true
                        windowOpen.Set()
                        unpark.Wait(20000) |> ignore

                    let runLive =
                        match ctxRef with
                        | Some c -> c.IsRunning "tests"
                        | None -> false

                    lock statusesLock (fun () ->
                        statuses.Add s

                        if PluginStatus.isTerminal s && runLive then
                            violations.Add $"terminal %A{s} landed while the 'tests' run slot was HELD") }

    let handler: PluginHandler<int, A118Msg> =
        { Name = PluginName.create "a118"
          Init = 0
          Update =
            fun ctx state event ->
                async {
                    ctxRef <- Some ctx

                    match event with
                    // The STALE terminal, reported through the plugin-facing funnel exactly
                    // as the illegitimate terminals the guard drops do: a cache replay, a
                    // FileChecked per-file completion, a `safeUpdate` crash-net stamp.
                    | FileChanged _ ->
                        ctx.ReportStatus(
                            PluginStatus.completedNow "stale per-file terminal" (System.TimeSpan.FromSeconds 1.0)
                        )

                        return state
                    // The CLAIM, made from the mailbox — the production shape.
                    | BuildCompleted _ ->
                        match ctx.RunExclusive "tests" rigWork with
                        | Claimed
                        | SlotBusy -> ()

                        return state
                    | _ -> return state
                }
          Commands = [ "get", fun _ctx s _ -> async { return string s } ]
          Subscriptions = Set.ofList [ SubscribeFileChanged; SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    let reg = registerHandler services handler

    // Dispatch the stale terminal. The mailbox parks inside the host's ReportStatus.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/a.fs" ]))
    windowOpen.Wait(20000) |> ignore

    // The window is OPEN: the guard has checked (no slot held) and is committing the report.
    raceTheClaim ctxRef.Value reg rigWork

    // Long enough for the claim to land in the window. This pins a real window rather than
    // creating one.
    System.Threading.Thread.Sleep 400

    unpark.Set()

    // The run must actually go live, or the assertions would be vacuous.
    workEntered.Wait(20000) |> ignore
    System.Threading.Thread.Sleep 300

    let result =
        lock statusesLock (fun () ->
            { Statuses = List.ofSeq statuses
              Violations = List.ofSeq violations })

    // Release the run and let the plugin settle.
    workGate.Set()
    waitUntil (fun () -> not (reg.IsBusy())) 20000

    result

/// The shared assertions. `isRunning` proves the race was REAL — the claim was made
/// and the slot went live — so a green result can never be vacuous.
let private assertNoTerminalOnLiveRun (result: A118Result) =
    let isRunning s =
        match s with
        | Running _ -> true
        | _ -> false

    // Anti-vacuity: the run genuinely started. Without this a rig that silently failed to
    // claim would "pass".
    test <@ result.Statuses |> List.exists isRunning @>

    // THE INVARIANT: no terminal may reach the host while an exclusive run is live.
    test <@ List.isEmpty result.Violations @>

    // And the plugin must not be left LOOKING finished while its run is still going — the
    // last thing the host saw must be the live run, not a verdict nobody earned.
    test <@ result.Statuses |> List.tryLast |> Option.map PluginStatus.isTerminal = Some false @>

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-118: a claim from a FOREIGN thread cannot land a stale terminal on the live run`` () =
    // The claim arrives on a thread that is NOT the plugin's mailbox — a legal use of the
    // framework API. Before the `statusLock` fix this reproduced the race: the guard checked
    // "no run is live", the foreign claim published `Running`, and the parked terminal
    // landed on top of it.
    runA118Rig (fun ctx _reg work ->
        System.Threading.Tasks.Task.Run(fun () ->
            match ctx.RunExclusive "tests" work with
            | Claimed
            | SlotBusy -> ())
        |> ignore

        // Give the claim time to reach the framework while the window is open.
        System.Threading.Thread.Sleep 150)
    |> assertNoTerminalOnLiveRun

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-118: a claim from the MAILBOX cannot enter the guard's window at all`` () =
    // The production shape, and the structural reason the shipped daemon never hit this:
    // every `ctx.RunExclusive` and `ctx.ReportStatus` in the shipped plugins is lexically
    // inside `Update`, and the agent loop awaits each `Update` before dequeuing the next
    // event, so check, claim and report are totally ordered on one logical thread. With the
    // mailbox parked INSIDE the report, the claim event cannot even be dequeued.
    runA118Rig (fun _ctx reg _work -> reg.Dispatch(DispatchBuildCompleted BuildSucceeded))
    |> assertNoTerminalOnLiveRun

// ---------------------------------------------------------------------------
// AUTOMATION-161 — a `Custom` message is a cache WRITER, never a cache READER.
//
// Every other event the framework dispatches is an OBSERVATION of the world, and its payload
// is what the cache key is computed from: same key ⇒ same input ⇒ the cached result is the
// result. A `Custom` message is the plugin's OWN post — the DELIVERY of work already done —
// and its payload is NOT in the key: TestPrune's `cacheKeyFor` reads its `TestRunCompleted`
// only far enough to decide whether the result is CACHEABLE, never far enough to IDENTIFY
// it. Two different all-passing runs therefore collide on one key.
//
// So a "hit" on a Custom message is a collision, not a proof of equivalence — and serving it
// SKIPS THE HANDLER, the only thing that folds the finished run into the plugin's state.
// Observed live: `confirm` ran the full suite for 102s, passed 1965 tests, wrote a complete
// CTRF report — and the framework then replayed a cached terminal over the `TestsFinished`
// carrying it, so `test-scope` still answered "no tests ran".
//
// The WRITE stays: a Custom window is how the entry the next `BuildCompleted` hits gets
// minted in the first place.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``a cache hit must NEVER be replayed over a Custom message — its payload is not in the key`` () =
    async {
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "k"
        let pluginNameStr = "custom-never-replayed"
        let compKey: TaskCache.CompositeKey = { Plugin = pluginNameStr; File = None }

        // A warm entry under EXACTLY the key the Custom message will compute.
        cache.Set
            compKey
            cacheKey
            { CacheKey = cacheKey
              Errors = []
              Status = cachedRunDone
              EmittedEvents = [] }

        let customHandled = ref 0
        let mutable registeredCmd: CommandHandler option = None

        let handler: PluginHandler<unit, string> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        // Posted by the command below; stands for `TestsFinished`, the
                        // results of a run that HAS ALREADY HAPPENED.
                        | Custom _ ->
                            System.Threading.Interlocked.Increment(customHandled) |> ignore
                            ctx.ReportStatus(completedAt System.DateTime.UtcNow)
                        | _ -> ()

                        return state
                    }
              Commands =
                [ "post-result",
                  (fun ctx _state _args ->
                      async {
                          ctx.Post "run-finished"
                          return "ok"
                      }) ]
              Subscriptions = Set.empty
              CacheKey = Some(fun _ -> Some cacheKey)
              Teardown = None }

        let reg =
            registerHandler (servicesWithCache cache (fun (_, cmd) -> registeredCmd <- Some cmd)) handler

        let! _ = registeredCmd.Value [||]

        // The handler MUST run: a cached status is no substitute for the result of a run
        // that actually executed.
        waitUntil (fun () -> !customHandled = 1) 10000
        test <@ !customHandled = 1 @>
    }
    |> Async.RunSynchronously

// ---------------------------------------------------------------------------
// AUTOMATION-186: the derived per-file replay summary feeds `RunVerdict.create`, which
// THROWS on an empty summary. `ledgerSummary` must therefore render a non-empty string for
// EVERY ledger state — including the empty ledger a clean per-file replay derives from — or
// the replay path crashes instead of reporting a green "0 findings".
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``ledgerSummary is non-empty on an empty ledger and survives RunVerdict.create`` () =
    let emptySummary = ledgerSummary Map.empty
    test <@ emptySummary <> "" @>

    // The replay path builds exactly this; RunVerdict.create must not throw.
    let verdict = RunVerdict.create emptySummary System.TimeSpan.Zero
    test <@ verdict.Summary = emptySummary @>

[<Fact(Timeout = 15000)>]
let ``ledgerSummary counts a populated ledger and stays non-empty`` () =
    let populated =
        Map.ofList
            [ "/a.fs", [ ErrorEntry.error "boom"; ErrorEntry.warningWithDetail "meh" "d" ]
              "/b.fs", [ ErrorEntry.error "kaput" ] ]

    let summary = ledgerSummary populated
    test <@ summary <> "" @>
    // Three findings total across the two files (two errors, one warning).
    test <@ summary.Contains "3 findings" @>
    test <@ (RunVerdict.create summary System.TimeSpan.Zero).Summary = summary @>

// ---------------------------------------------------------------------------
// AUTOMATION-343 — a cache HIT must leave the ledger where a cache MISS leaves it
// ---------------------------------------------------------------------------
//
// The replay used to call `ClearPlugin` — the plugin's ENTIRE ledger — for every
// non-`FileChecked` event, then replay only the errors captured in that one
// batch. Findings for files OUTSIDE the batch were silently destroyed.
//
// A real run never does that: `reportOrClearFile` touches one file at a time, so
// an earlier batch's findings stand. So a cache hit erased findings a cache miss
// keeps — and the verdict gates on the ledger, making it a FALSE GREEN, strictly
// worse than a stale summary.
//
// The assertion is EQUALITY between the two ledgers, deliberately. "Both
// non-empty" would have passed throughout the bug: the batch's own findings were
// always replayed; it was the other files' that vanished.

/// A ledger that records what the framework actually did to it, so a cold run
/// and a cached run can be compared rather than eyeballed.
type private RecordingLedger() =
    let entries = System.Collections.Generic.Dictionary<string, ErrorEntry list>()

    // LOCKED, and not incidentally. The plugin writes from its own async handler
    // while the test thread reads, so an unsynchronised `Dictionary` threw
    // `Collection was modified; enumeration operation may not execute` from
    // inside `Snapshot` at roughly 1 run in 6.
    //
    // I introduced that flake with this test (AUTOMATION-343), then made it MORE
    // frequent by replacing a fixed sleep with a poll (AUTOMATION-111) — the poll
    // calls `Snapshot` in a tight loop exactly while the handler is writing. The
    // poll is still the right wait; a fixed sleep encodes an assumption about the
    // machine. It just has to read a structure that tolerates being read.
    let gate = obj ()

    member _.Report (file: string) (es: ErrorEntry list) =
        lock gate (fun () -> entries[file] <- es)

    member _.ClearFile(file: string) =
        lock gate (fun () -> entries.Remove file |> ignore)

    member _.ClearAll() = lock gate (fun () -> entries.Clear())

    /// Sorted so equality is about content, not insertion order. Materialised
    /// INSIDE the lock — returning a lazy `Seq` would move the enumeration back
    /// outside it and reinstate the race in a subtler form.
    member _.Snapshot() =
        lock gate (fun () -> entries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy fst |> Seq.toList)

[<Fact(Timeout = 20000)>]
let ``a cached whole-run replay leaves findings for files outside the batch`` () =
    async {
        let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
        let cacheKey = ContentHash.create "batch-1"
        let pluginNameStr = "whole-run"

        let entry message =
            { Message = message
              Severity = DiagnosticSeverity.Error
              Line = 1
              Column = 0
              Detail = None }

        // The handler reports ONLY about the file in its batch — the shape a
        // per-file plugin (format-check) has, under a whole-run cache key.
        let handler: PluginHandler<unit, unit> =
            { Name = PluginName.create pluginNameStr
              Init = ()
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            ctx.ReportErrors "/tmp/repo/B.fs" [ entry "B is bad" ]
                            ctx.ReportStatus(completedAt System.DateTime.UtcNow)
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = Some(fun _ -> Some cacheKey)
              Teardown = None }

        let run (useCache: TaskCache.ITaskCache) =
            async {
                let ledger = RecordingLedger()

                let services =
                    { defaultServices with
                        TaskCache = Some useCache
                        ReportErrors = fun _ file es -> ledger.Report file es
                        ClearErrors = fun _ file -> ledger.ClearFile file
                        ClearPlugin = fun _ -> ledger.ClearAll() }

                // A finding from an EARLIER batch, about a file this batch never
                // mentions. This is the one the blanket clear destroyed.
                ledger.Report "/tmp/repo/A.fs" [ entry "A is bad" ]

                let reg = registerHandler services handler
                reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/B.fs" ]))

                // Poll for the handler's own effect rather than sleeping a fixed
                // 150ms. The sleep passed in isolation and FAILED under full-suite
                // load — a flake I shipped in AUTOMATION-343 and hit the same day.
                // A fixed wait encodes an assumption about the machine, which is
                // exactly the assumption a loaded CI box breaks.
                let deadline = System.DateTime.UtcNow.AddSeconds 10.0
                let mutable seen = false

                while not seen && System.DateTime.UtcNow < deadline do
                    if ledger.Snapshot() |> List.exists (fun (f, _) -> f = "/tmp/repo/B.fs") then
                        seen <- true
                    else
                        do! Async.Sleep 10

                return ledger.Snapshot()
            }

        // Cold: populates the cache.
        let! cold = run cache
        // Warm: same batch, served from the cache.
        let! cached = run cache

        // Both must know about A (the earlier batch) and B (this one).
        test <@ cold |> List.map fst = [ "/tmp/repo/A.fs"; "/tmp/repo/B.fs" ] @>

        // THE ASSERTION. Equality, not "both non-empty" — the batch's own finding
        // was replayed even while the bug was live; A.fs was what disappeared.
        test <@ cached = cold @>
    }
    |> Async.RunSynchronously
