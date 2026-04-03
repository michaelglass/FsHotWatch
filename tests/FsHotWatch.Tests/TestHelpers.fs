module FsHotWatch.Tests.TestHelpers

open System
open System.IO
open System.Threading

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
        { Name = "build-recorder"
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
          Subscriptions =
            { FsHotWatch.PluginFramework.PluginSubscriptions.none with
                BuildCompleted = true }
          CacheKey = None }

    ((fun () -> receivedBuild), handler)

/// Create a plugin that records CommandCompleted events.
/// Returns (getCommandResult, handler) where getCommandResult() returns the captured result.
let commandRecorder () =
    let mutable receivedCommand: FsHotWatch.Events.CommandCompletedResult option = None

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = "command-recorder"
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
          Subscriptions =
            { FsHotWatch.PluginFramework.PluginSubscriptions.none with
                CommandCompleted = true }
          CacheKey = None }

    ((fun () -> receivedCommand), handler)

/// Create an ErrorEntry for tests.
let errorEntry msg (sev: FsHotWatch.ErrorLedger.DiagnosticSeverity) : FsHotWatch.ErrorLedger.ErrorEntry =
    { Message = msg
      Severity = sev
      Line = 0
      Column = 0
      Detail = None }

/// Create a temp directory with the given prefix, run the body, then clean up.
/// Returns the result of the body function.
let withTempDir (prefix: string) (body: string -> 'a) =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-{prefix}-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        body tmpDir
    finally
        if Directory.Exists(tmpDir) then
            Directory.Delete(tmpDir, true)
