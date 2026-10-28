/// Shared fixtures for the plugin framework's owner tests.
module FsHotWatch.Tests.PluginFrameworkFixtures

open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework

/// No-op PluginHostServices — tests override just the fields they observe.
let defaultServices: PluginHostServices =
    { Checker = TestHelpers.sharedChecker.Value
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
      ProjectGraph = ProjectGraphAccessor.none
      StartAsync = Async.Start
      ClaimOrQueueSharedRun = fun _ _ -> Some Ready
      ReleaseSharedRun = fun _ _ -> () }

/// Register a handler with no-op host services, capturing its commands.
let registerWith (handler: PluginHandler<'State, 'Msg>) (registerCommand: (string * CommandHandler -> unit) option) =
    registerHandler
        { defaultServices with
            RegisterCommand = defaultArg registerCommand ignore }
        handler

/// Register with all defaults.
let registerDefault handler = registerWith handler None

/// Wait for this event's commit, including cache replay and failure bookkeeping. This
/// does not wait for background work that the event launches.
let dispatchAndAwait (registration: RegisteredPlugin) event =
    match registration.DispatchTracked event with
    | Some receipt -> receipt.Wait(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
    | None -> failwith "fixture expected a subscribed event"

type SharedWakeMsg =
    | SharedFinished
    | SharedFailed of string

/// A plugin that claims one shared run per FileChanged and reports the run's result.
let sharedWakeHandlerWithClassifier
    name
    (workFor: SharedResourceState -> Async<SharedWakeMsg>)
    (classify: SharedWakeMsg -> SharedResourceState)
    =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged _ ->
                    let classifyResult =
                        function
                        | SharedFailed reason -> Invalid reason
                        | message -> classify message

                    match
                        ctx.RunExclusiveShared "work" "artifacts" workFor classifyResult (fun ex ->
                            SharedFailed ex.Message)
                    with
                    | SharedClaimed
                    | SharedQueued -> ()
                    | LocalSlotBusy -> failwith "unexpected local contention"
                | Custom SharedFinished -> ctx.ReportStatus(PluginStatus.completedNow "done" System.TimeSpan.Zero)
                | Custom(SharedFailed reason) ->
                    ctx.ReportStatus(PluginStatus.failedNow reason reason System.TimeSpan.Zero)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.singleton SubscribeFileChanged
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

let sharedWakeHandler name workFor =
    sharedWakeHandlerWithClassifier name workFor (fun _ -> Ready)
