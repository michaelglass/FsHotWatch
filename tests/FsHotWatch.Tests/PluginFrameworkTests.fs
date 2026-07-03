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
          SetNextTerminalOutcome = fun _ _ -> ()
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }
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

    // Poll the command deterministically — it queues behind the FileChanged message
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

    // Dispatch subscribed events — should increment state
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    // Dispatch unsubscribed events — should be ignored
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

    // Only the FileChanged should have incremented
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

        // Query initial state
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

    // Poll the command deterministically — it queues behind the CommandCompleted message
    let (_, cmdHandler) = registeredCmd.Value
    let result = cmdHandler [||] |> Async.RunSynchronously
    test <@ result = "true" @>

// --- Framework invariant: handler throws -> plugin status reaches terminal Failed ---

[<Fact(Timeout = 15000)>]
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
          Commands = [ "noop", fun _ctx _state _args -> async { return "ok" } ]
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
              SetNextTerminalOutcome = fun _ _ -> ()
              FcsSuppressedCodes = Set.empty
              ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }
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

[<Fact(Timeout = 15000)>]
let ``handler that throws records ex.ToString() (full type+stack) in Failed status, not just ex.Message`` () =
    // Item D: when a plugin's Update throws, the user-visible Failed status must
    // include the full exception (ex.ToString() — type name and stack trace),
    // not just ex.Message. Otherwise the user has to grep daemon.log to figure
    // out which throw site fired.
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
              SetNextTerminalOutcome = fun _ _ -> ()
              FcsSuppressedCodes = Set.empty
              ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }
            handler

    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/Foo.fs" ]))
    registeredCmd.Value [||] |> Async.RunSynchronously |> ignore

    let statuses = reportedStatuses.ToArray() |> List.ofArray

    let failedMsg =
        statuses
        |> List.tryPick (function
            | Failed(m, _) -> Some m
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
      TaskCache = Some cache
      StartSubtask = fun _ _ _ -> ()
      UpdateSubtask = fun _ _ _ -> ()
      EndSubtask = fun _ _ -> ()
      Log = fun _ _ -> ()
      SetSummary = fun _ _ -> ()
      SetNextTerminalOutcome = fun _ _ -> ()
      FcsSuppressedCodes = Set.empty
      ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

[<Fact(Timeout = 20000)>]
let ``pre-populated cache replays on the very first dispatch`` () =
    // Regression: with the old `RequireWarmStart` gate, plugins skipped replay
    // until they completed once per session. Now that BatchChecked closes the
    // half-formed-key window, the gate is gone and replay fires immediately.
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
              Status = Completed System.DateTime.UtcNow
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
                            ctx.ReportStatus(Completed System.DateTime.UtcNow)
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
    // Perf regression guard: the framework used to call `cacheKeyFn event` twice
    // per dispatch — once in tryReplayCache (LOOKUP) and once in runAndCache
    // (STORE). For BuildPlugin that key is a full content-hash of the project
    // graph, so a miss (common while editing) paid TWO SHA-256 passes per
    // trigger. The dispatch loop now computes the key ONCE and threads the single
    // value to both lookup and store. On a miss (empty cache) the handler runs,
    // reports a terminal status, and the result is stored — exercising both the
    // lookup and store paths — yet the key function must fire only once.
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
                        | FileChanged _ -> ctx.ReportStatus(Completed System.DateTime.UtcNow)
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
        // Miss: handler ran AND result was stored, but the key was computed once.
        test <@ !keyCalls = 1 @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 20000)>]
let ``cache key is computed exactly once per dispatched event on a cache hit`` () =
    // On a hit the lookup path replays and the store path is never reached, so
    // even the old code computed the key once here. This pins that the
    // once-per-event threading does not accidentally recompute on the hit path.
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
              Status = Completed System.DateTime.UtcNow
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

/// Drive RunExclusive end-to-end via the agent: each FileChanged dispatch invokes
/// `ctx.RunExclusive "k" work`, where `work` waits on `gate` and then
/// returns a synthetic completion BuildMsg. We track:
///   - `started`: increments at the top of `work`, observed before gate release.
///   - `completed`: increments inside the agent when the framework posts the
///     completion msg back as Custom (-> proves "framework posts return value").
type private RxMsg = RxDone of int

[<Fact(Timeout = 20000)>]
let ``RunExclusive ignores second call while first is running`` () =
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
                            ctx.RunExclusive
                                "k"
                                (async {
                                    System.Threading.Interlocked.Increment(started) |> ignore
                                    gate.Wait()
                                    return RxDone 1
                                })

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
        // Wait for first work to enter (gate-blocked).
        waitUntil (fun () -> !started = 1) 12000
        test <@ !started = 1 @>

        // Second dispatch while running: must be dropped.
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/b.fs" ]))
        // Drain the agent so we know the second FileChanged was processed.
        let! _ = registeredCmd.Value [||]
        test <@ !started = 1 @>

        // Release first work -> completion msg posts back to mailbox.
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
        // Pre-populate the cache with a result whose EmittedEvents list contains
        // every CachedEvent variant the framework knows how to replay. On
        // dispatch (RequireWarmStart=false), the framework must re-fire each
        // one through the corresponding host service. Covers the replay arm in
        // PluginFramework that walks `result.EmittedEvents` (lines 347-356).
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
                    RanFullSuite = true }
              TaskCache.CachedCommandCompleted
                  { Name = "noop"
                    Outcome = CommandSucceeded "ok" } ]

        cache.Set
            compKey
            cacheKey
            { CacheKey = cacheKey
              // Errors uses three forms: file="*" (ClearPlugin), entries=[] for a real
              // file (ClearErrors), and a real entry list (ReportErrors). Covers all
              // three branches in the replay error loop.
              Errors =
                [ ("*", [])
                  ("/tmp/clear-me.fs", [])
                  ("/tmp/has-errors.fs", [ ErrorEntry.error "x" ]) ]
              Status = Completed System.DateTime.UtcNow
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
            { Checker = checker
              RepoRoot = "/tmp/repo"
              ReportStatus = fun _ _ -> ()
              ReportErrors = fun _ file _ -> lock reportedFiles (fun () -> reportedFiles.Add(file))
              ClearErrors = fun _ file -> lock clearedFiles (fun () -> clearedFiles.Add(file))
              ClearPlugin = fun _ -> System.Threading.Interlocked.Increment(clearedPlugin) |> ignore
              EmitBuildCompleted = fun _ -> System.Threading.Interlocked.Increment(buildSeen) |> ignore
              EmitTestRunStarted = fun _ -> System.Threading.Interlocked.Increment(trsSeen) |> ignore
              EmitTestProgress = fun _ -> System.Threading.Interlocked.Increment(tpSeen) |> ignore
              EmitTestRunCompleted = fun _ -> System.Threading.Interlocked.Increment(trcSeen) |> ignore
              EmitCommandCompleted = fun _ -> System.Threading.Interlocked.Increment(ccSeen) |> ignore
              RegisterCommand = fun (_, cmd) -> registeredCmd <- Some cmd
              TaskCache = Some cache
              StartSubtask = fun _ _ _ -> ()
              UpdateSubtask = fun _ _ _ -> ()
              EndSubtask = fun _ _ -> ()
              Log = fun _ _ -> ()
              SetSummary = fun _ _ -> ()
              SetNextTerminalOutcome = fun _ _ -> ()
              FcsSuppressedCodes = Set.empty
              ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

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
        // Drain past dispatch.
        let! _ = registeredCmd.Value [||]

        // Cache replayed → Update must NOT have been called.
        test <@ !updateCalls = 0 @>

        // Each emitted-event variant fired exactly once.
        test <@ !buildSeen = 1 @>
        test <@ !trsSeen = 1 @>
        test <@ !tpSeen = 1 @>
        test <@ !trcSeen = 1 @>
        test <@ !ccSeen = 1 @>

        // Error-replay branches all exercised.
        // The replay loop emits ClearPlugin (for "*"), ClearErrors (for empty entries
        // on a real file), ReportErrors (for non-empty entries). The pre-replay
        // ClearPlugin (FileChanged event with File=None) also fires once.
        test <@ !clearedPlugin >= 1 @>
        test <@ clearedFiles |> Seq.contains "/tmp/clear-me.fs" @>
        test <@ reportedFiles |> Seq.contains "/tmp/has-errors.fs" @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 30000)>]
let ``RunExclusive releases slot when work raises and logs without re-posting completion`` () =
    // Covers runOne's try/with around `let! msg = w` (PluginFramework lines 212-213, 219):
    // when work throws, completion stays ValueNone (no Custom message posted),
    // and the slot is released so a subsequent RunExclusive can run.
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
                            ctx.RunExclusive
                                "k"
                                (async {
                                    System.Threading.Interlocked.Increment(started) |> ignore
                                    return failwith "boom"
                                })

                            return state
                        | FileChanged _ ->
                            ctx.RunExclusive
                                "k"
                                (async {
                                    System.Threading.Interlocked.Increment(started) |> ignore
                                    return RxDone 1
                                })

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
        // Wait for the throwing work to complete (synchronously raises in the async).
        // Generous polling timeouts: under heavy parallel-collection load the
        // thread-pool can lag scheduling our runOne async by several seconds.
        waitUntil (fun () -> !started = 1) 20000

        // Slot released — IsRunning "k" must report false even though work raised.
        waitUntil (fun () -> not (capturedCtx.Value.IsRunning "k")) 20000
        test <@ not (capturedCtx.Value.IsRunning "k") @>
        // No completion posted (Custom RxDone never fired).
        test <@ !completed = 0 @>

        // Subsequent dispatch can run — proves the slot was released.
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/ok.fs" ]))
        waitUntil (fun () -> !completed = 1) 20000
        test <@ !started = 2 @>
        test <@ !completed = 1 @>
    }
    |> Async.RunSynchronously

[<Fact(Timeout = 30000)>]
let ``RunExclusive forces a terminal Failed status when work raises (no strand)`` () =
    // Regression for AUTOMATION-65 (fresh-workspace daemon wedge). A faulted
    // exclusive run MUST NOT strand the plugin in a non-terminal (Running) status:
    // the completion message that normally drives the plugin to terminal is never
    // posted on the fault path, and plugins routinely report Running just before
    // launching the work (test-prune reports Running immediately before
    // `RunExclusive "tests"`). Without the framework forcing a terminal here the
    // plugin sits Running forever while IsBusy/AnyPluginBusy report false — exactly
    // the wedge that hangs the check's WaitForComplete and then lets idle-exit fire
    // mid-wait. This asserts the fault transitions the plugin to a terminal Failed.
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
            { Checker = checker
              RepoRoot = "/tmp/repo"
              ReportStatus = fun name status -> statuses.Enqueue(name, status)
              ReportErrors = fun _ _ _ -> ()
              ClearErrors = fun _ _ -> ()
              ClearPlugin = fun _ -> ()
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
              SetSummary = fun _ _ -> ()
              SetNextTerminalOutcome = fun _ _ -> ()
              FcsSuppressedCodes = Set.empty
              ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

        let handler: PluginHandler<int, RxMsg> =
            { Name = PluginName.create "rx-strand"
              Init = 0
              Update =
                fun ctx state event ->
                    async {
                        capturedCtx <- Some ctx

                        match event with
                        | FileChanged _ ->
                            // Mirror test-prune: announce Running, THEN launch the
                            // exclusive work that faults before posting completion.
                            ctx.ReportStatus(PluginStatus.Running(since = System.DateTime.UtcNow))
                            ctx.RunExclusive "k" (async { return failwith "boom" })
                            return state
                        | _ -> return state
                    }
              Commands = [ "get", fun _ s _ -> async { return string s } ]
              Subscriptions = Set.singleton SubscribeFileChanged
              CacheKey = None
              Teardown = None }

        let reg = registerHandler services handler
        reg.Dispatch(DispatchFileChanged(SourceChanged [ "/throw" ]))

        // The fault path must report a terminal Failed (never leaving the plugin
        // stranded Running).
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
                            ctx.RunExclusive
                                "k"
                                (async {
                                    gate.Wait()
                                    return RxDone 1
                                })

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
        // Drain to ensure ctx captured + RunExclusive called.
        let! _ = registeredCmd.Value [||]
        // While work blocked on gate, IsRunning must report true.
        waitUntil (fun () -> capturedCtx.Value.IsRunning "k") 12000
        observedRunning.Value <- capturedCtx.Value.IsRunning "k"
        test <@ !observedRunning @>
        test <@ not (capturedCtx.Value.IsRunning "other") @>

        gate.Set()
        // After completion msg posts back, drain again.
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

    // Drain agent — `drain` queues behind the BatchChecked dispatch.
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

    // Dispatch a subscribed event after, so we can drain the mailbox.
    reg.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Foo.fs" ]))

    let (_, cmdHandler) = registeredCmd.Value
    cmdHandler [||] |> Async.RunSynchronously |> ignore

    test <@ not batchSeen @>
