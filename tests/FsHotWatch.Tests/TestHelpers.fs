module FsHotWatch.Tests.TestHelpers

open System
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis

/// Shared FSharpChecker for tests that only need basic compilation.
/// Lazy so it's only created if actually used.
let sharedChecker =
    lazy FSharpChecker.Create(projectCacheSize = 200, keepAssemblyContents = true, keepAllBackgroundResolutions = true)

/// Poll until condition is true or timeout (default 50ms poll interval).
let waitUntil (condition: unit -> bool) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep(50)

/// Run `write` every 2s until `hasEvent` returns true or timeout expires.
/// Use this for FSEvents tests: brand-new temp directories can have 4-20s cold-start
/// latency, and after a large initial event batch, fseventsd may batch subsequent events
/// for 15-30s regardless of kFSEventStreamCreateFlagNoDefer. Repeated writes ensure the
/// event fires as soon as fseventsd is ready, without relying on fixed timeouts.
let probeLoop (write: int -> unit) (hasEvent: unit -> bool) (timeoutMs: int) =
    let overall = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable probe = 0

    while not (hasEvent ()) && DateTime.UtcNow < overall do
        write probe
        probe <- probe + 1
        // Poll for up to 2s per write before retrying
        let batchEnd = min overall (DateTime.UtcNow.AddMilliseconds(2000.0))

        while not (hasEvent ()) && DateTime.UtcNow < batchEnd do
            Thread.Sleep(50)

/// Repeatedly write probe files to a directory every 2s until hasEvent returns true.
let probeUntilEvent (dir: string) (hasEvent: unit -> bool) (timeoutMs: int) =
    probeLoop (fun n -> File.WriteAllText(Path.Combine(dir, $"_fshw-probe-{n}.fs"), $"// probe {n}")) hasEvent timeoutMs

/// Poll until the plugin reaches a terminal status (Completed or Failed).
let waitForTerminalStatus (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus(pluginName) with
            | Some(FsHotWatch.Events.Completed _)
            | Some(FsHotWatch.Events.Failed _) -> true
            | _ -> false)
        timeoutMs

/// Subscribe to `OnStatusChanged` BEFORE querying current status, returning a
/// `Task<PluginStatus>` that completes the first time `pred` matches a status
/// for `pluginName`. Use this when a test needs to observe a plugin's terminal
/// state deterministically (no polling, no xUnit `Fact(Timeout)` race).
///
/// Usage pattern — subscribe, then trigger the work, then await:
///   let completion = TestHelpers.beginAwaitStatus host "plugin" (function Completed _ -> true | _ -> false)
///   host.EmitBuildCompleted(BuildSucceeded)
///   let status = completion.Wait(TimeSpan.FromSeconds 15.0)
let beginAwaitStatusWith
    (host: FsHotWatch.PluginHost.PluginHost)
    (pluginName: string)
    (matchCurrent: bool)
    (pred: FsHotWatch.Events.PluginStatus -> bool)
    : System.Threading.Tasks.Task<FsHotWatch.Events.PluginStatus> =
    let tcs =
        System.Threading.Tasks.TaskCompletionSource<FsHotWatch.Events.PluginStatus>()

    let handler =
        Handler<string * FsHotWatch.Events.PluginStatus>(fun _ (n, s) ->
            if n = pluginName && pred s then
                tcs.TrySetResult(s) |> ignore)

    host.OnStatusChanged.AddHandler(handler)
    // Fast path: the plugin may already be at the desired status when we subscribe.
    // Subscribe-then-check ordering is required — check-then-subscribe races.
    // Callers that need to observe the *next* transition (e.g. after an emit
    // that'll cycle Running→Completed) pass matchCurrent=false.
    if matchCurrent then
        match host.GetStatus(pluginName) with
        | Some s when pred s -> tcs.TrySetResult(s) |> ignore
        | _ -> ()
    // Unsubscribe once completed so handlers don't accumulate across tests.
    tcs.Task.ContinueWith(
        System.Action<System.Threading.Tasks.Task<FsHotWatch.Events.PluginStatus>>(fun _ ->
            host.OnStatusChanged.RemoveHandler(handler))
    )
    |> ignore

    tcs.Task

let beginAwaitStatus host pluginName pred =
    beginAwaitStatusWith host pluginName true pred

/// Convenience: await a terminal status (Completed or Failed).
let private isTerminalStatus =
    function
    | FsHotWatch.Events.Completed _
    | FsHotWatch.Events.Failed _ -> true
    | _ -> false

let beginAwaitTerminal (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) =
    beginAwaitStatus host pluginName isTerminalStatus

/// Await the *next* terminal transition, ignoring current status. Use after
/// emitting an event that'll cycle the plugin through Running→Completed when
/// it's already at Completed from an earlier event.
let beginAwaitNextTerminal (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) =
    beginAwaitStatusWith host pluginName false isTerminalStatus

/// Poll until the plugin status is no longer Running, with a timeout.
let waitForSettled (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus(pluginName) with
            | Some(FsHotWatch.Events.Running _) -> false
            | _ -> true)
        timeoutMs

/// Create a plugin that records BuildCompleted events.
/// Returns (getBuildResult, handler) where getBuildResult() returns the captured result.
let buildRecorder () =
    let mutable receivedBuild: FsHotWatch.Events.BuildResult option = None

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> receivedBuild), handler)

/// Create a plugin that records CommandCompleted events.
/// Returns (getCommandResult, handler) where getCommandResult() returns the captured result.
let commandRecorder () =
    let mutable receivedCommand: FsHotWatch.Events.CommandCompletedResult option = None

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "command-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.CommandCompleted result -> receivedCommand <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeCommandCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> receivedCommand), handler)

/// Create a plugin that records TestCompleted events in order.
/// Returns (getEvents, handler) — getEvents returns a snapshot of all received
/// TestResults in FIFO order.
let testCompletedRecorder () =
    let received = System.Collections.Concurrent.ConcurrentQueue<FsHotWatch.Events.TestResults>()

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "test-completed-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.TestCompleted results -> received.Enqueue(results)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeTestCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> received |> Seq.toList), handler)

/// Create an ErrorEntry for tests.
let errorEntry msg (sev: FsHotWatch.ErrorLedger.DiagnosticSeverity) : FsHotWatch.ErrorLedger.ErrorEntry =
    { Message = msg
      Severity = sev
      Line = 0
      Column = 0
      Detail = None }

/// Create a temp directory with the given prefix, run the body, then clean up.
/// Returns the result of the body function.
/// Construct an FSharpProjectOptions with sensible defaults for tests that
/// only care about ProjectFileName / SourceFiles / OtherOptions.
let makeProjectOptions (projectFile: string) (sourceFiles: string list) (otherOptions: string list) =
    { ProjectFileName = projectFile
      ProjectId = None
      SourceFiles = Array.ofList sourceFiles
      OtherOptions = Array.ofList otherOptions
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

let withTempDir (prefix: string) (body: string -> 'a) =
    // Canonicalize so /var/folders/... and /private/var/folders/... don't diverge
    // across test+plugin views of the same path (macOS temp dir is a symlink).
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-{prefix}-{Guid.NewGuid():N}")
        |> Path.GetFullPath

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        body tmpDir
    finally
        if Directory.Exists(tmpDir) then
            Directory.Delete(tmpDir, true)
