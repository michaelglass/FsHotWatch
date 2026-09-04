[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.DaemonTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Build
open FsHotWatch.Daemon
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

type private BlockingWorkspaceLoader(results: Types.ProjectOptions list) =
    let entered = new Threading.ManualResetEventSlim(false)
    let resume = new Threading.ManualResetEventSlim(false)
    let notifications = Event<Types.WorkspaceProjectState>()

    member _.Entered = entered
    member _.Resume() = resume.Set()

    member private _.Load() =
        entered.Set()
        resume.Wait()
        results :> seq<_>

    interface IWorkspaceLoader with
        member this.LoadProjects(_projectPaths) = this.Load()
        member this.LoadProjects(_projectPaths, _customProperties, _binaryLog) = this.Load()
        member this.LoadSln(_solutionPath) = this.Load()
        member this.LoadSln(_solutionPath, _customProperties, _binaryLog) = this.Load()

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

type private SequencedWorkspaceLoader(resultsByAttempt: Types.ProjectOptions list list) =
    let entered =
        resultsByAttempt
        |> List.map (fun _ -> new Threading.ManualResetEventSlim(false))
        |> List.toArray

    let resume =
        resultsByAttempt
        |> List.map (fun _ -> new Threading.ManualResetEventSlim(false))
        |> List.toArray

    let notifications = Event<Types.WorkspaceProjectState>()
    let mutable attempt = -1

    member _.Entered(index: int) = entered[index]
    member _.Resume(index: int) = resume[index].Set()

    member private _.Load() =
        // Repeat the terminal outcome for later attempts. A daemon's real watcher may
        // legitimately queue another discovery (for example when restore materializes
        // project.assets.json); tests using this loader care about the transition from
        // the first outcome to the later stable outcome, not an exact call count.
        let requested = Threading.Interlocked.Increment(&attempt)
        let index = min requested (resultsByAttempt.Length - 1)

        entered[index].Set()
        resume[index].Wait()
        resultsByAttempt[index] :> seq<_>

    interface IWorkspaceLoader with
        member this.LoadProjects(_projectPaths) = this.Load()
        member this.LoadProjects(_projectPaths, _customProperties, _binaryLog) = this.Load()
        member this.LoadSln(_solutionPath) = this.Load()
        member this.LoadSln(_solutionPath, _customProperties, _binaryLog) = this.Load()

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private minimalLoadedProject (projectPath: string) : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = []
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UtcNow
      TargetPath = Path.ChangeExtension(projectPath, ".dll")
      TargetRefPath = None
      ProjectOutputType = Types.ProjectOutputType.Library
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = false
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = []
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = ""
          RestoreSuccess = true
          Configurations = [ "Debug" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = None
          RunCommand = None
          IsPublishable = None }
      Items = []
      Properties = []
      CustomProperties = []
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers = [] }

// ============================================================================
// parseNowarnCodes tests
// Workaround for https://github.com/dotnet/fsharp/issues/9796 —
// FCS TransparentCompiler ignores #nowarn directives for warnaserror codes.
// When that issue is resolved, parseNowarnCodes and these tests can be removed.
// ============================================================================

[<Fact(Timeout = 15000)>]
let ``parseNowarnCodes extracts single nowarn code`` () =
    let source =
        """#nowarn "3536"
module Foo
let x = 1"""

    test <@ parseNowarnCodes source = Set.ofList [ 3536 ] @>

[<Fact(Timeout = 15000)>]
let ``parseNowarnCodes extracts multiple nowarn directives`` () =
    let source =
        """#nowarn "1182"
#nowarn "3536"
module Foo"""

    test <@ parseNowarnCodes source = Set.ofList [ 1182; 3536 ] @>

[<Fact(Timeout = 15000)>]
let ``parseNowarnCodes returns empty set when no directives`` () =
    let source =
        """module Foo
let x = 1"""

    test <@ parseNowarnCodes source = Set.empty @>

[<Fact(Timeout = 15000)>]
let ``parseNowarnCodes ignores non-numeric nowarn`` () =
    let source =
        """#nowarn "notanumber"
module Foo"""

    test <@ parseNowarnCodes source = Set.empty @>

[<Fact(Timeout = 15000)>]
let ``parseNowarnCodes handles multiple codes on one line`` () =
    let source =
        """#nowarn "1182" "3536"
module Foo"""

    test <@ parseNowarnCodes source = Set.ofList [ 1182; 3536 ] @>

// Two `waitForPluginTerminalIfRunning` tests with Task.Delay-based timing and
// elapsed-bound assertions (300ms release scenarios, 3.5s elapsed upper bound)
// moved to FsHotWatch.IntegrationTests 2026-05-02 — they systematically busted
// Fact(Timeout=5000) under unit-suite parallel load.
//
// The "not registered → return after settle" path stays here as a deterministic
// unit test: completes when the function returns, no Task.Delay timing,
// generous Fact(Timeout) so heavy parallel load can't cancel it.

[<Fact(Timeout = 15000)>]
let ``waitForPluginTerminalIfRunningWith returns when status reader yields no plugin`` () =
    let getStatus _ = None

    waitForPluginTerminalIfRunningWith getStatus "build" (TimeSpan.FromSeconds(5.0))
    |> Async.RunSynchronously

[<Fact(Timeout = 15000)>]
let ``waitForPluginTerminalIfRunningWith returns when plugin already terminal`` () =
    let getStatus _ = Some(completedAt DateTime.UtcNow)

    waitForPluginTerminalIfRunningWith getStatus "build" (TimeSpan.FromSeconds(5.0))
    |> Async.RunSynchronously

[<Fact(Timeout = 15000)>]
let ``waitForPluginTerminalIfRunningWith waits while Running, returns when terminal`` () =
    // Status reader transitions from Running to Completed after the 4th call.
    // The settle window will see Running first, then the polling loop sees
    // Completed on a subsequent poll.
    let calls = ref 0

    let getStatus _ =
        calls.Value <- calls.Value + 1

        if calls.Value < 4 then
            Some(PluginStatus.Running(since = DateTime.UtcNow))
        else
            Some(completedAt DateTime.UtcNow)

    waitForPluginTerminalIfRunningWith getStatus "build" (TimeSpan.FromSeconds(5.0))
    |> Async.RunSynchronously

    // The polling loop visited at least 4 entries: settle + at least one Running poll + one terminal.
    test <@ calls.Value >= 4 @>

[<Fact(Timeout = 15000)>]
let ``waitForPluginTerminalIfRunningWith returns after timeout when plugin stays Running`` () =
    // Status reader always reports Running. The timeout is 200ms; settle window
    // is 200ms, polling 50ms. Function must return without throwing when timeout
    // elapses with plugin still Running.
    let getStatus _ =
        Some(PluginStatus.Running(since = DateTime.UtcNow))

    waitForPluginTerminalIfRunningWith getStatus "build" (TimeSpan.FromMilliseconds(200.0))
    |> Async.RunSynchronously
// No assertion on elapsed — test that the function completes (doesn't deadlock).

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// Probe the watched directory until the daemon processes an event (proves watcher is live).
/// Uses repeated file writes so however long watcher setup takes doesn't cause timeouts.
/// Then waits for events to stabilize before returning.
let private waitForDaemonReady (srcDir: string) (changeCount: unit -> int) =
    probeUntilEvent srcDir (fun () -> changeCount () > 0) 60000

    // Wait for event storm to settle (create + potential debounce events)
    let mutable lastCount = changeCount ()
    let mutable stable = 0

    while stable < 3 do
        Thread.Sleep(200)
        let c = changeCount ()

        if c = lastCount then
            stable <- stable + 1
        else
            lastCount <- c
            stable <- 0


[<Fact(Timeout = 20000)>]
let ``daemon starts and stops without error`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

// --- AUTOMATION-435: `RunMode` is the ONLY thing that decides watcher construction ---

/// A watcher factory that records how often the daemon asked for a watcher and
/// hands back one that observes nothing — no FSEvents, no `FileSystemWatcher`.
let private countingWatcherFactory (calls: int ref) : Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        Interlocked.Increment(&calls.contents) |> ignore

        let inert: FsHotWatch.Watcher.FileWatcher =
            { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
              Disposables = [] }

        inert

[<Fact(Timeout = 20000)>]
[<Trait("Issue", "AUTOMATION-435")>]
let ``a Watching daemon constructs its watcher exactly once`` () =
    // Positive control for the OneShot test below: the same construction path, the
    // default `RunMode`, and the factory IS reached — once, at construction.
    withTempDir "daemon-watching" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let calls = ref 0

        use _daemon =
            Daemon.createWithWatcherFactory
                nullChecker
                tmpDir
                Daemon.DaemonOptions.defaults
                (countingWatcherFactory calls)

        test <@ calls.Value = 1 @>)

[<Fact(Timeout = 20000)>]
[<Trait("Issue", "AUTOMATION-435")>]
let ``a OneShot daemon never touches the watcher factory`` () =
    // The factory THROWS: had construction reached it, `createWithWatcherFactory`
    // would raise and the test would fail on that exception, not on an assertion.
    withTempDir "daemon-oneshot" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let throwingFactory: Daemon.WatcherFactory =
            fun _ _ _ _ _ -> failwith "a OneShot host must never construct a file watcher"

        use daemon =
            Daemon.createWithWatcherFactory
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                throwingFactory

        // The host is a fully usable daemon in every other respect.
        test <@ daemon.GetScanState() = ScanIdle @>)

[<Fact(Timeout = 15000)>]
let ``daemon Dispose kills tracked child processes`` () =
    // Regression: `dotnet fshw stop` used to leak in-flight test runners.
    withTrackedSleep 60 (fun proc ->
        withTempDir "daemon-stop" (fun tmpDir ->
            let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
            // Track manually against this daemon's registry — bypasses the AsyncLocal
            // path and directly verifies Dispose drains the registry it owns.
            daemon.ProcessRegistry.Track proc
            (daemon :> IDisposable).Dispose())

        proc.WaitForExit(5000) |> ignore
        test <@ proc.HasExited @>)

[<Fact(Timeout = 15000)>]
let ``daemon suppresses watcher events for preprocessor-modified files`` () =
    withTempDir "daemon" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let preprocessor =
            { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                member _.Name = "test-formatter"

                member _.Process (changedFiles: string list) (_repoRoot: string) = changedFiles

                member _.Dispose() = () }

        daemon.RegisterPreprocessor(preprocessor)

        let mutable sourceChangedEvents: string list list = []

        let handler =
            { Name = PluginName.create "suppression-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged(SourceChanged files) -> sourceChangedEvents <- files :: sourceChangedEvents
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let host = daemon.Host
        let testFiles = [ Path.Combine(srcDir, "Fmt.fs") ]
        let modified = host.RunPreprocessors(testFiles)
        test <@ modified.Length = testFiles.Length @>

        let status = host.GetStatus("test-formatter")
        test <@ status.IsSome @>

        cts.Cancel())

[<Fact(Timeout = 150000)>]
let ``daemon dispatches file change events to plugins`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "test-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged change -> receivedChanges <- change :: receivedChanges
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        waitForDaemonReady (Path.Combine(tmpDir, "src")) (fun () -> receivedChanges.Length)
        receivedChanges <- []

        let newFile = Path.Combine(tmpDir, "src", "New.fs")
        // Probe-loop: keep writing New.fs until a FileChanged event fires.
        // After a large probe batch in waitForDaemonReady, events can lag;
        // repeated writes handle that.
        probeLoop
            (fun n -> File.WriteAllText(newFile, $"module New // v{n}"))
            (fun () -> receivedChanges.Length >= 1)
            60000

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ receivedChanges.Length >= 1 @>)

[<Fact(Timeout = 150000)>]
let ``daemon debounces rapid file changes into one batch`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "debounce-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged change -> receivedChanges <- change :: receivedChanges
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        waitForDaemonReady (Path.Combine(tmpDir, "src")) (fun () -> receivedChanges.Length)
        receivedChanges <- []

        let fileA = Path.Combine(tmpDir, "src", "A.fs")
        let fileB = Path.Combine(tmpDir, "src", "B.fs")
        let fileC = Path.Combine(tmpDir, "src", "C.fs")
        // Probe-loop: write all 3 files together each iteration so they're still
        // rapid-fire for debounce purposes, but we retry if fseventsd batches them.
        probeLoop
            (fun n ->
                File.WriteAllText(fileA, $"module A // v{n}")
                File.WriteAllText(fileB, $"module B // v{n}")
                File.WriteAllText(fileC, $"module C // v{n}"))
            (fun () ->
                let allFiles =
                    receivedChanges
                    |> List.collect (fun c ->
                        match c with
                        | SourceChanged files -> files
                        | _ -> [])

                allFiles.Length >= 3)
            60000

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        let sourceChanges =
            receivedChanges
            |> List.choose (fun c ->
                match c with
                | SourceChanged files -> Some files
                | _ -> None)

        test <@ sourceChanges.Length >= 1 @>

        let allFiles = sourceChanges |> List.collect id
        test <@ allFiles.Length >= 3 @>)

[<Fact(Timeout = 150000)>]
let ``daemon handles ProjectChanged events`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "project-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged change -> receivedChanges <- change :: receivedChanges
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        waitForDaemonReady (Path.Combine(tmpDir, "src")) (fun () -> receivedChanges.Length)
        receivedChanges <- []

        let projFile = Path.Combine(tmpDir, "src", "Test.fsproj")
        // Probe-loop: keep writing Test.fsproj until a ProjectChanged event fires.
        probeLoop
            (fun n -> File.WriteAllText(projFile, $"<Project Sdk=\"Microsoft.NET.Sdk\"><!-- v{n} --></Project>"))
            (fun () ->
                receivedChanges
                |> List.exists (fun c ->
                    match c with
                    | ProjectChanged _ -> true
                    | _ -> false))
            60000

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        let projectChanges =
            receivedChanges
            |> List.exists (fun c ->
                match c with
                | ProjectChanged _ -> true
                | _ -> false)

        test <@ projectChanges @>)

[<Fact(Timeout = 150000)>]
let ``daemon handles SolutionChanged events`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "solution-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged change -> receivedChanges <- change :: receivedChanges
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        waitForDaemonReady (Path.Combine(tmpDir, "src")) (fun () -> receivedChanges.Length)
        receivedChanges <- []

        let slnFile = Path.Combine(tmpDir, "Test.sln")
        // Probe-loop: keep writing Test.sln until a SolutionChanged event fires.
        probeLoop
            (fun n -> File.WriteAllText(slnFile, $"Microsoft Visual Studio Solution File <!-- v{n} -->"))
            (fun () ->
                receivedChanges
                |> List.exists (fun c ->
                    match c with
                    | SolutionChanged -> true
                    | _ -> false))
            60000

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        let solutionChanges =
            receivedChanges
            |> List.exists (fun c ->
                match c with
                | SolutionChanged -> true
                | _ -> false)

        test <@ solutionChanges @>)

[<Fact(Timeout = 20000)>]
let ``daemon Run completes when cancellation is immediate`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        cts.Cancel()
        let task = Async.StartAsTask(daemon.Run(cts.Token))

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact(Timeout = 15000)>]
let ``Daemon.create creates a working daemon with real checker`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.create tmpDir Daemon.DaemonOptions.defaults
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
        test <@ daemon.RepoRoot = tmpDir @>)

[<Fact(Timeout = 20000)>]
let ``daemon RunWithIpc starts and stops cleanly`` () =
    withTempDir "daemon-ipc" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact(Timeout = 20000)>]
let ``daemon RunWithIpc with idle-exit threshold arms the timer and stops cleanly`` () =
    // Exercises the RunWithIpc idle-exit timer construction path (IdleExitMin
    // Some) and confirms the daemon still starts/stops cleanly with the timer
    // armed. The 30min threshold is far beyond the test lifetime, so the timer
    // never fires here; the firing decision is unit-tested in IdleExitTests.
    withTempDir "daemon-idle-exit" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"

        let daemon =
            Daemon.createWith
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    IdleExitMin = Some 30
                    PressureIdleFloorMin = Some 2 }

        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact(Timeout = 20000)>]
let ``daemon RunWithIpc idle-exit with pressure floor disabled arms the timer and stops cleanly`` () =
    // PressureIdleFloorMin None → pressure-shortening off, full window only. The
    // idle-exit timer still arms (IdleExitMin Some) and the daemon stops cleanly.
    // The pressure decision is unit-tested in IdleExitTests.
    withTempDir "daemon-idle-exit-nofloor" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"

        let daemon =
            Daemon.createWith
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    IdleExitMin = Some 30
                    PressureIdleFloorMin = None }

        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact(Timeout = 20000)>]
let ``daemon RunWithIpc without idle-exit threshold creates no timer and stops cleanly`` () =
    // IdleExitMin None → no timer is constructed (the no-op disposable branch);
    // the daemon starts and stops cleanly as before.
    withTempDir "daemon-idle-exit-off" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"

        let daemon =
            Daemon.createWith
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    IdleExitMin = None }

        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact(Timeout = 20000)>]
let ``daemon RunWithIpc responds to IPC queries`` () =
    withTempDir "daemon-ipc" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "ipc-test"
              Init = ()
              Update = fun _ctx state _event -> async { return state }
              Commands = []
              Subscriptions = PluginSubscriptions.none
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore

        let result = FsHotWatch.Ipc.IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("ipc-test") @>

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ())

[<Fact(Timeout = 60000)>]
let ``check forces a from-disk scan: forceScanAndWait advances the scan generation, wait-only does not`` () =
    // Regression for the 221/224 disease: `check`/`confirm`'s first daemon step
    // must FORCE a from-disk scan, not merely WAIT for one. `WaitForScan -1L`
    // alone returns the last completed scan's generation IMMEDIATELY on a warm
    // daemon (`WaitForScanGeneration(-1L)` is already-satisfied once any scan has
    // run), so a watcher-missed idle edit is never re-read and a stale verdict is
    // replayed. `performScan` — the ONLY path that re-reads every file from disk —
    // is also the ONLY path that advances the scan generation, so a generation
    // bump is the direct observable that a fresh from-disk scan actually ran.
    // Fails against the pre-fix wait-only thunk (generation never advances); passes
    // with `forceScanAndWait` (Scan → performScan → generation++).
    withTempDir "force-scan" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        // Keep the pipe name short: on macOS the Unix-domain-socket path
        // (tempdir + "CoreFxPipe_" + name) must stay under the 104-char sun_path
        // limit, and the temp dir alone is already ~50 chars.
        let pipeName = $"fshw-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore

        try
            // Drain the startup scan + one forced scan down to a stable baseline
            // generation, so nothing is in flight when we measure below.
            FsHotWatch.Cli.Program.forceScanAndWait FsHotWatch.Cli.Program.defaultIpcOps pipeName
            |> ignore

            waitUntil (fun () -> daemon.GetScanGeneration() >= 1L) 20000
            test <@ daemon.GetScanGeneration() >= 1L @>
            Thread.Sleep(500)
            let genBaseline = daemon.GetScanGeneration()

            // Simulate a watcher-missed idle edit: change disk with no scan. The
            // real watcher's change path never advances the SCAN generation, so
            // this cannot perturb the measurement either way.
            File.WriteAllText(Path.Combine(tmpDir, "src", "Idle.fs"), "module Idle\nlet x = 1\n")

            // OLD behavior — wait only. Triggers NO scan: the generation is
            // unchanged, so the idle edit would never be re-read (the stale-verdict
            // bug this fix closes).
            FsHotWatch.Cli.Program.defaultIpcOps.WaitForScan pipeName -1L
            |> Async.RunSynchronously
            |> ignore

            Thread.Sleep(300)
            test <@ daemon.GetScanGeneration() = genBaseline @>

            // FIXED behavior — forceScanAndWait forces a fresh from-disk
            // `performScan`, which advances the generation (and re-reads the edit).
            FsHotWatch.Cli.Program.forceScanAndWait FsHotWatch.Cli.Program.defaultIpcOps pipeName
            |> ignore

            waitUntil (fun () -> daemon.GetScanGeneration() > genBaseline) 20000
            test <@ daemon.GetScanGeneration() > genBaseline @>
        finally
            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ())

[<Fact(Timeout = 15000)>]
let ``daemon RegisterProject stores options in pipeline`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        let sourceFile = Path.Combine(tmpDir, "src", "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")

        let absSource = Path.GetFullPath(sourceFile)

        let options, _ =
            checker.GetProjectOptionsFromScript(
                absSource,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText absSource)
            )
            |> Async.RunSynchronously

        // Force-include absSource — GetProjectOptionsFromScript's SourceFiles
        // array varies across platforms for .fs (non-script) files.
        let options =
            { options with
                SourceFiles = Array.append options.SourceFiles [| absSource |] |> Array.distinct }

        daemon.RegisterProject("/tmp/Test.fsproj", options)

        // Assert the registration happened — independent of CheckFile's own
        // lookup, which was a Linux-only flake when the stored SourceFiles
        // entries didn't match what Path.GetFullPath produced for the lookup.
        test <@ daemon.Pipeline.GetProjectOptions("/tmp/Test.fsproj").IsSome @>

        test
            <@
                daemon.Pipeline.GetAllRegisteredFiles()
                |> List.contains (AbsFilePath.create absSource)
            @>)

// --- FormatScanStatus tests ---

[<Fact(Timeout = 15000)>]
let ``FormatScanStatus returns idle for ScanIdle`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        daemon.SetScanState(ScanIdle)
        test <@ daemon.FormatScanStatus() = "idle" @>)

[<Fact(Timeout = 15000)>]
let ``FormatScanStatus returns progress for Scanning`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        daemon.SetScanState(Scanning(10, 5, DateTime.UtcNow))
        let status = daemon.FormatScanStatus()
        test <@ status.Contains("5/10") @>
        test <@ status.Contains("50%") @>)

// FormatScanStatus now reads completeness LIVE (registered minus currently-
// checked) rather than from a frozen ScanComplete snapshot, so the count can no
// longer be injected through ScanState. The pure render function is unit-tested
// directly instead — same string shapes, live-supplied numbers.

[<Fact(Timeout = 15000)>]
let ``formatScanStatusWith renders idle for ScanIdle`` () =
    test <@ formatScanStatusWith 0 0 ScanIdle = "idle" @>

[<Fact(Timeout = 15000)>]
let ``formatScanStatusWith renders scanning progress`` () =
    let status = formatScanStatusWith 0 0 (Scanning(10, 5, DateTime.UtcNow))

    test <@ status = "scanning: 5/10 files (50%)" @>

[<Fact(Timeout = 15000)>]
let ``formatScanStatusWith renders complete when nothing unchecked`` () =
    let status = formatScanStatusWith 70 0 (ScanComplete(TimeSpan.FromSeconds(15.5)))

    test <@ status = "complete: 70 files checked in 15.5s" @>

// --- runChecksWithRetry tests (scan silent-truncation race fix) ---
//
// Invariant 1 (no silent truncation): files whose check returns None
// (cancelled/aborted/failed) are retried within a bounded budget and the
// common cancellation case converges to all files checked.
// Invariant 2 (honest completion): files still None after the retry budget are
// reported as an unchecked count so the scan cannot present as clean.
// Invariant 3 (no regression): retry re-invokes the SAME check function, so a
// newer user edit superseding an in-flight check is observed (the retry reads
// current content); a file that is genuinely still cancelled stays counted.

[<Fact(Timeout = 15000)>]
let ``runChecksWithRetry returns zero unchecked when all files check first pass`` () =
    let files = [ "/a.fs"; "/b.fs"; "/c.fs" ] |> List.map AbsFilePath.create
    let emitted = System.Collections.Concurrent.ConcurrentBag<int>()
    let check (_: AbsFilePath) = async { return Some 1 }

    let unchecked =
        runChecksWithRetry 3 check (fun r -> emitted.Add r) files
        |> Async.RunSynchronously

    test <@ unchecked = 0 @>
    test <@ emitted.Count = 3 @>

[<Fact(Timeout = 15000)>]
let ``runChecksWithRetry retries a transiently-cancelled file until it converges`` () =
    // /b.fs returns None on its first attempt (simulating a same-file
    // CancelPreviousCheck race) then Some on retry — must converge to 0 unchecked.
    let attempts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

    let check (f: AbsFilePath) =
        async {
            let path = AbsFilePath.value f
            let n = attempts.AddOrUpdate(path, 1, fun _ c -> c + 1)

            if path = "/b.fs" && n = 1 then
                return None
            else
                return Some 1
        }

    let emitted = System.Collections.Concurrent.ConcurrentBag<int>()
    let files = [ "/a.fs"; "/b.fs"; "/c.fs" ] |> List.map AbsFilePath.create

    let unchecked =
        runChecksWithRetry 3 check (fun r -> emitted.Add r) files
        |> Async.RunSynchronously

    test <@ unchecked = 0 @>
    // Every file emitted exactly once (no duplicate emit for the converged file).
    test <@ emitted.Count = 3 @>
    test <@ attempts["/b.fs"] = 2 @>
    // Files that succeeded first pass are NOT re-checked on the retry round.
    test <@ attempts["/a.fs"] = 1 @>

[<Fact(Timeout = 15000)>]
let ``runChecksWithRetry reports persistently-cancelled files as unchecked`` () =
    // /b.fs always returns None (a file under continuous concurrent edit, or a
    // hard failure): after the retry budget it must be surfaced as unchecked,
    // not silently dropped — honest completion.
    let attempts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

    let check (f: AbsFilePath) =
        async {
            let path = AbsFilePath.value f
            attempts.AddOrUpdate(path, 1, fun _ c -> c + 1) |> ignore
            return (if path = "/b.fs" then None else Some 1)
        }

    let files = [ "/a.fs"; "/b.fs"; "/c.fs" ] |> List.map AbsFilePath.create

    let unchecked =
        runChecksWithRetry 2 check (fun _ -> ()) files |> Async.RunSynchronously

    test <@ unchecked = 1 @>
    // Bounded: 1 initial attempt + 2 retries = 3 total for the failing file.
    test <@ attempts["/b.fs"] = 3 @>
    // Succeeding files are only attempted once.
    test <@ attempts["/a.fs"] = 1 @>

[<Fact(Timeout = 15000)>]
let ``formatScanStatusWith surfaces unchecked count as non-ok when incomplete`` () =
    let status = formatScanStatusWith 70 5 (ScanComplete(TimeSpan.FromSeconds(15.5)))

    // Must not read as clean: surfaces the unchecked files explicitly, with the
    // checked count as (registered - unchecked).
    test <@ status = "incomplete: 65 files checked, 5 unchecked in 15.5s" @>
    test <@ status.ToLowerInvariant().Contains("unchecked") @>

// Pins the invariant the agent migration must preserve: once ScanAll's
// reply lands, the scan state is observable as ScanComplete (not stale ScanIdle)
// and the generation has advanced. With the wrapper-with-volatile-fields
// design this was happenstance ordering of Volatile.Write before reply.Reply;
// after collapsing to a single agent that owns its state in the loop's
// recursion, it's inherent — the state lives in the next loop iteration so
// any subsequent GetState round-trip observes it.
[<Fact(Timeout = 20000)>]
let ``GetScanState returns ScanComplete and generation advances after ScanAll`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let gen0 = daemon.GetScanGeneration()
        daemon.ScanAll() |> Async.RunSynchronously
        let gen1 = daemon.GetScanGeneration()
        let state1 = daemon.GetScanState()

        test <@ gen1 = gen0 + 1L @>

        match state1 with
        | ScanComplete _ -> ()
        | other -> failwithf "Expected ScanComplete after ScanAll, got %A" other

        daemon.ScanAll() |> Async.RunSynchronously
        let gen2 = daemon.GetScanGeneration()
        test <@ gen2 = gen1 + 1L @>)

[<Fact(Timeout = 20000)>]
let ``RunOnce completes and returns plugin statuses`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let handler =
            { Name = PluginName.create "runonce-test"
              Init = ()
              Update = fun _ctx state _event -> async { return state }
              Commands = []
              Subscriptions = PluginSubscriptions.none
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let statuses = Async.RunSynchronously(daemon.RunOnce(), timeout = 30000)
        test <@ statuses.ContainsKey("runonce-test") @>)

[<Fact(Timeout = 30000)>]
let ``DiscoverAndRegisterProjects warns when no projects are discovered`` () =
    withTempDir "daemon-zero-projects" (fun tmpDir ->
        // Empty src/ directory — no .fsproj files anywhere.
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        let originalLevel = FsHotWatch.Logging.logLevel
        let sb = System.Text.StringBuilder()
        let writer = new System.IO.StringWriter(sb)
        let prevErr = System.Console.Error

        try
            System.Console.SetError(writer)
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Warning
            Async.RunSynchronously(daemon.DiscoverAndRegisterProjects(), timeout = 25000)
            writer.Flush()
            let output = sb.ToString()

            test <@ output.Contains("No .fsproj files discovered") @>
            test <@ output.Contains("[discover]") @>
        finally
            System.Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel originalLevel)

[<Fact(Timeout = 15000)>]
let ``completed discovery keeps loader mapping and registration counts distinct`` () =
    let coordinator = DiscoveryCoordinator()

    coordinator.Run(fun () ->
        async {
            return
                { Discovered = 1
                  Loaded = 1
                  OptionsMapped = 0
                  Registered = 0 },
                ()
        })
    |> Async.RunSynchronously

    let snapshot = coordinator.Completed |> Option.get
    test <@ snapshot.Discovered = 1 @>
    test <@ snapshot.Loaded = 1 @>
    test <@ snapshot.OptionsMapped = 0 @>
    test <@ snapshot.Registered = 0 @>
    test <@ totalDiscoveryFailure snapshot.Discovered snapshot.Loaded = None @>

[<Fact(Timeout = 15000)>]
let ``discovery coordinator starts without a completed epoch and recovers after work faults`` () =
    let coordinator = DiscoveryCoordinator()
    test <@ coordinator.Completed = None @>
    test <@ coordinator.WaitForCompletion().GetAwaiter().GetResult() = None @>

    let faulting: Async<DiscoverySnapshot * unit> =
        async { return raise (InvalidOperationException "loader exploded") }

    Assert.Throws<InvalidOperationException>(fun () -> coordinator.Run(fun () -> faulting) |> Async.RunSynchronously)
    |> ignore

    test <@ coordinator.Completed = None @>

    coordinator.Run(fun () ->
        async {
            return
                { Discovered = 1
                  Loaded = 1
                  OptionsMapped = 1
                  Registered = 1 },
                ()
        })
    |> Async.RunSynchronously

    test <@ coordinator.Completed.IsSome @>

[<Fact(Timeout = 15000)>]
let ``stable discovery admission includes attempts queued behind the active loader`` () =
    let coordinator = DiscoveryCoordinator()
    let firstEntered = new Threading.ManualResetEventSlim(false)
    let firstResume = new Threading.ManualResetEventSlim(false)
    let secondEntered = new Threading.ManualResetEventSlim(false)
    let secondResume = new Threading.ManualResetEventSlim(false)

    let snapshot loaded =
        { Discovered = 1
          Loaded = loaded
          OptionsMapped = loaded
          Registered = loaded }

    let mutable first: System.Threading.Tasks.Task<unit> option = None
    let mutable second: System.Threading.Tasks.Task<unit> option = None

    try
        first <-
            Async.StartAsTask(
                coordinator.Run(fun () ->
                    async {
                        firstEntered.Set()
                        firstResume.Wait()
                        return snapshot 1, ()
                    })
            )
            |> Some

        test <@ firstEntered.Wait(TimeSpan.FromSeconds(10.0)) @>

        second <-
            Async.StartAsTask(
                coordinator.Run(fun () ->
                    async {
                        secondEntered.Set()
                        secondResume.Wait()
                        return snapshot 0, ()
                    })
            )
            |> Some

        test <@ waitUntilTrue (fun () -> coordinator.RequestedGeneration = 2L) 10000 @>
        let stable = coordinator.WaitForStableAdmission()
        firstResume.Set()
        test <@ secondEntered.Wait(TimeSpan.FromSeconds(10.0)) @>

        let premature =
            System.Threading.Tasks.Task.WhenAny(stable, System.Threading.Tasks.Task.Delay(1000))

        test <@ not (obj.ReferenceEquals(premature.GetAwaiter().GetResult(), stable)) @>

        secondResume.Set()
        let generation, completed = stable.GetAwaiter().GetResult()
        test <@ generation = 2L @>
        test <@ completed |> Option.map (fun value -> value.Loaded) = Some 0 @>
    finally
        firstResume.Set()
        secondResume.Set()

        for running in [ first; second ] |> List.choose id do
            try
                running.GetAwaiter().GetResult()
            with _ ->
                ()

[<Fact(Timeout = 15000)>]
let ``discovery failure marker rejects null and unrelated messages`` () =
    test <@ not (isTotalDiscoveryFailureMessage null) @>
    test <@ not (isTotalDiscoveryFailureMessage "some other configuration error") @>

[<Fact(Timeout = 30000)>]
let ``verdict admission waits while the real loader seam is between clear and completion`` () =
    withTempDir "daemon-discovery-race" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Blocked.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let loader = BlockingWorkspaceLoader([])

        use daemon =
            Daemon.createWithWorkspaceLoader nullChecker tmpDir Daemon.DaemonOptions.defaults loader (fun _ -> [])

        let mutable discovery: System.Threading.Tasks.Task<unit> option = None
        let mutable verdictWait: System.Threading.Tasks.Task option = None

        try
            discovery <- Async.StartAsTask(daemon.DiscoverAndRegisterProjects()) |> Some
            test <@ loader.Entered.Wait(TimeSpan.FromSeconds(10.0)) @>

            // The graph/pipeline have been cleared and the loader has not returned.
            // This exact window used to be misread as a completed zero-load result.
            let mutable ordinaryWaitCalled = false

            let runningVerdictWait =
                waitForVerdictUnlessDiscoveryFailed
                    daemon.WaitForDiscoveryAdmission
                    (fun _ ->
                        ordinaryWaitCalled <- true
                        System.Threading.Tasks.Task.FromResult(()))
                    (TimeSpan.FromHours(1.0))

            verdictWait <- Some runningVerdictWait
            test <@ not runningVerdictWait.IsCompleted @>
            test <@ not ordinaryWaitCalled @>
            test <@ daemon.TotalDiscoveryFailure() = None @>

            loader.Resume()
            discovery.Value.GetAwaiter().GetResult()

            Assert.Throws<System.InvalidOperationException>(fun () -> runningVerdictWait.GetAwaiter().GetResult())
            |> ignore

            test <@ not ordinaryWaitCalled @>

            let completed = daemon.DiscoverySnapshot() |> Option.get
            test <@ completed.Discovered = 1 @>
            test <@ completed.Loaded = 0 @>
            test <@ completed.OptionsMapped = 0 @>
            test <@ completed.Registered = 0 @>
            test <@ daemon.TotalDiscoveryFailure().IsSome @>
        finally
            loader.Resume()

            for running in
                [ discovery |> Option.map (fun task -> task :> System.Threading.Tasks.Task)
                  verdictWait ]
                |> List.choose id do
                try
                    running.GetAwaiter().GetResult()
                with _ ->
                    ())

[<Theory(Timeout = 30000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``verdict admission restarts when discovery begins after the host wait starts`` (secondAttemptLoads: bool) =
    withTempDir "daemon-discovery-after-admission" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Blocked.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let secondResult =
            if secondAttemptLoads then
                [ minimalLoadedProject projectPath ]
            else
                []

        let loader =
            SequencedWorkspaceLoader([ [ minimalLoadedProject projectPath ]; secondResult ])

        loader.Resume(0)

        // This test drives both discovery attempts explicitly. Keep the real macOS
        // watcher from racing a third project-change discovery into that sequence;
        // watcher behaviour has its own integration coverage.
        let daemonOptions =
            { Daemon.DaemonOptions.defaults with
                FsEventsLatencySeconds = 60.0 }

        use daemon =
            Daemon.createWithWorkspaceLoader nullChecker tmpDir daemonOptions loader (fun _ -> [])

        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously

        let hostWaitEntered = new Threading.ManualResetEventSlim(false)

        let hostWaitCompletion =
            System.Threading.Tasks.TaskCompletionSource<unit>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
            )

        let verdictWait =
            waitForVerdictUnlessDiscoveryFailed
                daemon.WaitForDiscoveryAdmission
                (fun _ ->
                    hostWaitEntered.Set()
                    hostWaitCompletion.Task)
                (TimeSpan.FromHours(1.0))

        let mutable rediscovery: System.Threading.Tasks.Task option = None

        try
            test <@ hostWaitEntered.Wait(TimeSpan.FromSeconds(10.0)) @>

            let running = Async.StartAsTask(daemon.DiscoverAndRegisterProjects())
            rediscovery <- Some(running :> System.Threading.Tasks.Task)
            test <@ loader.Entered(1).Wait(TimeSpan.FromSeconds(10.0)) @>

            // The loader began only after verdict admission. Releasing the already-running
            // host wait must not let the stale terminal host state win the race.
            hostWaitCompletion.SetResult()

            let premature =
                System.Threading.Tasks.Task.WhenAny(verdictWait, System.Threading.Tasks.Task.Delay(1000))

            let returnedBeforeDiscovery =
                obj.ReferenceEquals(premature.GetAwaiter().GetResult(), verdictWait)

            loader.Resume(1)
            running.GetAwaiter().GetResult()

            test <@ not returnedBeforeDiscovery @>

            if secondAttemptLoads then
                verdictWait.GetAwaiter().GetResult()
            else
                Assert.Throws<System.InvalidOperationException>(fun () -> verdictWait.GetAwaiter().GetResult())
                |> ignore
        finally
            hostWaitCompletion.TrySetResult(()) |> ignore
            loader.Resume(1)

            rediscovery
            |> Option.iter (fun running ->
                try
                    running.GetAwaiter().GetResult()
                with _ ->
                    ())

            try
                verdictWait.GetAwaiter().GetResult()
            with _ ->
                ())

[<Fact(Timeout = 15000)>]
let ``a loaded project that maps or registers as zero is not a loader failure`` () =
    withTempDir "daemon-mapping-distinct" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Loaded.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let loader = BlockingWorkspaceLoader([ minimalLoadedProject projectPath ])
        loader.Resume()

        let daemon =
            Daemon.createWithWorkspaceLoader nullChecker tmpDir Daemon.DaemonOptions.defaults loader (fun _ -> []) // Force the later mapping stage to produce nothing.

        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
        let completed = daemon.DiscoverySnapshot() |> Option.get
        test <@ completed.Discovered = 1 @>
        test <@ completed.Loaded = 1 @>
        test <@ completed.OptionsMapped = 0 @>
        test <@ completed.Registered = 0 @>
        test <@ daemon.TotalDiscoveryFailure() = None @>)

[<Fact(Timeout = 15000)>]
let ``verdict wait fails immediately when total discovery failed`` () =
    let mutable ordinaryWaitCalled = false
    let reason = "PROJECT LOADING FAILED: loaded 0 of 18"

    let task =
        waitForVerdictUnlessDiscoveryFailed
            (fun () ->
                System.Threading.Tasks.Task.FromResult(
                    { Generation = 1L
                      Failure = Some reason }
                ))
            (fun _ ->
                ordinaryWaitCalled <- true
                System.Threading.Tasks.Task.FromResult(()))
            (TimeSpan.FromHours(1.0))

    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () -> task.GetAwaiter().GetResult())

    test <@ ex.Message = reason @>
    test <@ not ordinaryWaitCalled @>

[<Fact(Timeout = 15000)>]
let ``verdict wait uses the ordinary host wait when discovery loaded projects`` () =
    let mutable observedTimeout = TimeSpan.Zero
    let expected = TimeSpan.FromSeconds(37.0)

    let task =
        waitForVerdictUnlessDiscoveryFailed
            (fun () -> System.Threading.Tasks.Task.FromResult({ Generation = 1L; Failure = None }))
            (fun timeout ->
                observedTimeout <- timeout
                System.Threading.Tasks.Task.FromResult(()))
            expected

    task.GetAwaiter().GetResult()

    test <@ observedTimeout = expected @>

// ============================================================================
// isTruthyEnv tests
// ============================================================================

// The isTruthyEnv / countReferences / dumpProjectOptions tests below are pure
// synchronous micro-tests (env reads, string counting, small file writes) that
// finish in microseconds. Their Fact(Timeout) is only a backstop against a
// hypothetical hang. The former 1000ms cap was tight enough to be CANCELED on a
// slow / loaded CI runner (JIT + parallel-class contention), so it's raised to
// the suite's standard 15000ms — still bounds a true infinite loop without
// flaking under load.
[<Fact(Timeout = 15000)>]
let ``isTruthyEnv returns false when unset`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    test <@ isTruthyEnv var = false @>

[<Fact(Timeout = 15000)>]
let ``isTruthyEnv returns false for empty string`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 15000)>]
let ``isTruthyEnv returns false for 0`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "0") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 15000)>]
let ``isTruthyEnv returns false for false case-insensitive`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "FaLsE") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 15000)>]
let ``isTruthyEnv returns true for 1`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "1") (fun () -> test <@ isTruthyEnv var = true @>)

[<Fact(Timeout = 15000)>]
let ``isTruthyEnv trims whitespace`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "  true  ") (fun () -> test <@ isTruthyEnv var = true @>)

// ============================================================================
// countReferences tests
// ============================================================================

[<Fact(Timeout = 15000)>]
let ``countReferences counts -r: prefixes`` () =
    let opts = [| "-r:/a.dll"; "--nowarn:42"; "-r:/b.dll"; "-r:/c.dll" |]
    test <@ countReferences opts = 3 @>

[<Fact(Timeout = 15000)>]
let ``countReferences returns 0 when no references`` () =
    test <@ countReferences [| "--nowarn:42" |] = 0 @>
    test <@ countReferences [||] = 0 @>

// ============================================================================
// dumpProjectOptions tests
// ============================================================================

[<Fact(Timeout = 15000)>]
let ``dumpProjectOptions writes options file`` () =
    withTempDir "dump-opts" (fun tmp ->
        let opts =
            makeProjectOptions
                "/tmp/Foo.fsproj"
                [ "/tmp/A.fs"; "/tmp/B.fs" ]
                [ "-r:/nuget/A.dll"; "--nowarn:42"; "-r:/nuget/B.dll" ]

        dumpProjectOptions tmp opts
        let written = File.ReadAllText(Path.Combine(tmp, "Foo.opts.txt"))
        test <@ written.Contains "# Project: /tmp/Foo.fsproj" @>
        test <@ written.Contains "/tmp/A.fs" @>
        test <@ written.Contains "-r:/nuget/A.dll" @>
        test <@ written.Contains "--nowarn:42" @>)

[<Fact(Timeout = 15000)>]
let ``dumpProjectOptions handles empty options`` () =
    withTempDir "dump-opts" (fun tmp ->
        let opts = makeProjectOptions "/tmp/Empty.fsproj" [] []
        dumpProjectOptions tmp opts
        test <@ File.Exists(Path.Combine(tmp, "Empty.opts.txt")) @>)

[<Fact(Timeout = 15000)>]
let ``dumpProjectOptions swallows IO errors`` () =
    // logDir does not exist and is not a directory — WriteAllLines will fail.
    let bogusDir =
        Path.Combine(Path.GetTempPath(), "does-not-exist-" + string (Guid.NewGuid()), "nope")

    let opts = makeProjectOptions "/tmp/X.fsproj" [] [ "-r:/a.dll" ]
    // Should not throw — errors are logged at debug level.
    dumpProjectOptions bogusDir opts

// ---------------------------------------------------------------------------
// formatElapsed / formatPluginWait — the [wait] log formatter
// ---------------------------------------------------------------------------
//
// Regression: a daemon stuck waiting on test-prune emitted
// `Waiting for plugins: test-prune (since 04/24/2026 19:00:39)` every 10s
// for 26+ minutes with no indication of WHICH test subprocess was still
// running. The new formatter includes subtask labels + per-task elapsed so a
// stuck daemon is diagnosable from a single log line.

[<Fact>]
let ``formatElapsed shows seconds under 1 minute`` () =
    test <@ FsHotWatch.Daemon.formatElapsed (TimeSpan.FromSeconds 45.0) = "45s" @>
    test <@ FsHotWatch.Daemon.formatElapsed (TimeSpan.FromSeconds 0.0) = "0s" @>

[<Fact>]
let ``formatElapsed shows minutes + seconds between 1 minute and 1 hour`` () =
    test <@ FsHotWatch.Daemon.formatElapsed (TimeSpan.FromSeconds 75.0) = "1m 15s" @>
    test <@ FsHotWatch.Daemon.formatElapsed (TimeSpan.FromSeconds 185.0) = "3m 5s" @>

[<Fact>]
let ``formatElapsed shows hours + minutes past 1 hour`` () =
    let ts = TimeSpan(1, 12, 30) // 1h 12m 30s
    test <@ FsHotWatch.Daemon.formatElapsed ts = "1h 12m" @>

[<Fact>]
let ``formatPluginWait shows only plugin name + elapsed when no subtasks`` () =
    let now = DateTime(2026, 4, 24, 19, 30, 0)
    let since = DateTime(2026, 4, 24, 19, 29, 15)

    let formatted = FsHotWatch.Daemon.formatPluginWait now "test-prune" since []
    test <@ formatted = "test-prune (45s)" @>

[<Fact>]
let ``formatPluginWait includes subtask labels + elapsed when present`` () =
    // The diagnostic line from a stuck daemon should show each in-flight
    // subtask so the user can tell what's still running.
    let now = DateTime(2026, 4, 24, 19, 30, 0)
    let since = DateTime(2026, 4, 24, 19, 0, 0) // 30m ago

    let subtasks =
        [ "Intelligence.Tests.Unit", DateTime(2026, 4, 24, 19, 18, 0) // 12m
          "Intelligence.Tests.Database", DateTime(2026, 4, 24, 19, 20, 0) ] // 10m

    let formatted = FsHotWatch.Daemon.formatPluginWait now "test-prune" since subtasks

    test <@ formatted.Contains("test-prune (30m 0s)") @>
    test <@ formatted.Contains("Intelligence.Tests.Unit 12m 0s") @>
    test <@ formatted.Contains("Intelligence.Tests.Database 10m 0s") @>

// ============================================================================
// F12 + F13 (audit 2026-05-02): mailbox-loop guards + processBatch/performScan
// broad catches in Daemon.fs. See docs/plans/2026-05-02-error-handling-audit.md
// ============================================================================

/// F12: ScanSignal's mailbox loop previously wrapped its typed pattern-match
/// in `with ex -> log; loop state`, silently swallowing programming bugs in a
/// daemon-internal control-plane component. The fix dropped that catch and
/// surfaces unhandled exceptions through the agent's Error event, exposed as
/// `AgentCrashed`. We inject a synthetic fault via the internal
/// `RaiseFaultForTest` seam (production messages don't have a natural failure
/// mode — the catch was guarding against future programming bugs) and assert
/// the event fires instead of being swallowed.
[<Fact(Timeout = 5000)>]
let ``F12: ScanSignal programming-bug surfaces via AgentCrashed instead of being swallowed`` () =
    let scanSignal = FsHotWatch.Daemon.ScanSignal()

    let crashed =
        System.Threading.Tasks.TaskCompletionSource<exn>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    use _ =
        scanSignal.AgentCrashed.Subscribe(fun ex -> crashed.TrySetResult(ex) |> ignore)

    let bug =
        InvalidOperationException("simulated programming bug inside ScanSignal loop")

    scanSignal.RaiseFaultForTest(bug)

    let observed = crashed.Task.Wait(TimeSpan.FromSeconds(2.0))
    test <@ observed @>
    test <@ obj.ReferenceEquals(crashed.Task.Result, bug) @>

/// F13: `runDaemonStep` is the single hoisted failure-handler that
/// `processBatch`/`performScan` call sites now route through, so the broad-
/// catch policy lives in one place rather than three inline `with ex ->` arms.
/// Two contracts to verify:
///   1. OperationCanceledException is NOT swallowed — cancellation is a normal
///      signal, not a daemon failure, and the inner code paths rely on it
///      bubbling to break out of async pipelines.
///   2. Other exceptions are admitted (logged + returned as `Error`) so the
///      caller can fall back to idle without crashing the daemon.
[<Fact(Timeout = 5000)>]
let ``F13: runDaemonStep lets OperationCanceledException propagate (cancellation is not failure)`` () =
    let cancelling: Async<int> =
        async { return raise (OperationCanceledException("simulated cancel")) }

    let work = FsHotWatch.Daemon.runDaemonStep "test-step" cancelling

    let ex =
        Assert.ThrowsAny<OperationCanceledException>(fun () -> Async.RunSynchronously(work) |> ignore)

    test <@ ex.Message.Contains("simulated cancel") @>

[<Fact(Timeout = 5000)>]
let ``F13: runDaemonStep returns Error for unexpected exceptions and admits the original instance`` () =
    let bug = InvalidOperationException("simulated processBatch bug")
    let failing: Async<int> = async { return raise bug }

    let result =
        Async.RunSynchronously(FsHotWatch.Daemon.runDaemonStep "test-step" failing)

    match result with
    | Ok _ -> Assert.Fail("expected Error result for unexpected exception")
    | Result.Error ex -> test <@ obj.ReferenceEquals(ex, bug) @>

[<Fact(Timeout = 5000)>]
let ``F13: runDaemonStep returns Ok with the work result on success`` () =
    let work: Async<int> = async { return 42 }

    let result =
        Async.RunSynchronously(FsHotWatch.Daemon.runDaemonStep "test-step" work)

    match result with
    | Ok v -> test <@ v = 42 @>
    | Result.Error ex -> Assert.Fail($"expected Ok 42, got Error {ex}")

// ===========================================================================
// resolveAffectedProjects — routes a project-tier change batch to the set of
// known projects whose FCS state should be scoped-invalidated, or None when a
// full re-discovery is the only safe response.
// ===========================================================================

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects maps a known .fsproj edit to that project`` () =
    let known = [ "/repo/src/A/A.fsproj"; "/repo/src/B/B.fsproj" ]

    let result =
        FsHotWatch.Daemon.resolveAffectedProjects known [ "/repo/src/A/A.fsproj" ]

    match result with
    | Some [ one ] -> test <@ one.Replace('\\', '/').EndsWith("/A/A.fsproj") @>
    | other -> Assert.Fail($"expected Some [A.fsproj], got %A{other}")

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects maps obj/project.assets.json to its owning project`` () =
    let known = [ "/repo/src/A/A.fsproj"; "/repo/src/B/B.fsproj" ]

    let result =
        FsHotWatch.Daemon.resolveAffectedProjects known [ "/repo/src/A/obj/project.assets.json" ]

    match result with
    | Some [ one ] -> test <@ one.Replace('\\', '/').EndsWith("/A/A.fsproj") @>
    | other -> Assert.Fail($"expected Some [A.fsproj], got %A{other}")

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects returns None for a .props change (repo-wide)`` () =
    let known = [ "/repo/src/A/A.fsproj" ]
    test <@ FsHotWatch.Daemon.resolveAffectedProjects known [ "/repo/Directory.Build.props" ] = None @>

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects returns None when any path is repo-wide even if others map`` () =
    let known = [ "/repo/src/A/A.fsproj" ]

    let changed = [ "/repo/src/A/A.fsproj"; "/repo/Directory.Build.props" ]

    test <@ FsHotWatch.Daemon.resolveAffectedProjects known changed = None @>

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects returns None for an unknown (new) project`` () =
    let known = [ "/repo/src/A/A.fsproj" ]
    test <@ FsHotWatch.Daemon.resolveAffectedProjects known [ "/repo/src/New/New.fsproj" ] = None @>

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects returns None for assets.json under an unknown project`` () =
    let known = [ "/repo/src/A/A.fsproj" ]

    test <@ FsHotWatch.Daemon.resolveAffectedProjects known [ "/repo/src/New/obj/project.assets.json" ] = None @>

[<Fact(Timeout = 5000)>]
let ``resolveAffectedProjects dedups .fsproj and its assets.json to one project`` () =
    let known = [ "/repo/src/A/A.fsproj" ]

    let changed = [ "/repo/src/A/A.fsproj"; "/repo/src/A/obj/project.assets.json" ]

    match FsHotWatch.Daemon.resolveAffectedProjects known changed with
    | Some [ _ ] -> ()
    | other -> Assert.Fail($"expected a single deduped project, got %A{other}")

// === Item 4: fsproj fingerprint vs discovery roots ===
//
// Reviewer claim (REFUTED): "repos with fsproj outside src/+tests/ get
// cold-scan fingerprint misses → full MSBuild re-evaluation every scan."
// The fingerprint is computed over the SAME discovery roots that
// discoverAndRegisterProjects enumerates, so a project under examples/ is
// invisible to both — the fingerprint is stable across scans and performScan's
// `currentFingerprint <> state.LastFingerprint` guard correctly skips
// re-evaluation. This test pins that stability.
[<Fact(Timeout = 15000)>]
let ``fsproj fingerprint is stable across scans when projects exist outside discovery roots`` () =
    withTempDir "fingerprint-stability" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "A")
        let examplesDir = Path.Combine(tmpDir, "examples", "B")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(examplesDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "A.fsproj"), "<Project />")
        File.WriteAllText(Path.Combine(examplesDir, "B.fsproj"), "<Project />")

        let fp1 = FsHotWatch.Daemon.fingerprintFsprojFiles tmpDir []
        let fp2 = FsHotWatch.Daemon.fingerprintFsprojFiles tmpDir []

        // Stable: a second scan over an unchanged repo sees the identical
        // fingerprint, so MSBuild re-evaluation is skipped (not "every scan").
        test <@ fp1 = fp2 @>
        // The in-roots project is fingerprinted...
        test <@ fp1 |> Set.exists (fun (p, _) -> p.EndsWith("A.fsproj")) @>
        // ...and the out-of-roots project is invisible to the fingerprint,
        // exactly as it is invisible to discovery (consistent, not a miss).
        test <@ fp1 |> Set.forall (fun (p, _) -> not (p.Contains("examples"))) @>)

// Real divergence found while characterizing the (refuted) claim above: the
// fingerprint used only the generated-path obj/bin filter while discovery
// applies the user's `.fshw.json` exclude patterns. A user-excluded fsproj was
// therefore fingerprinted, so editing it churned the fingerprint and triggered
// a spurious full MSBuild re-discovery of a project that discovery would never
// register. The fingerprint now shares discovery's exclude semantics.
[<Fact(Timeout = 15000)>]
let ``fsproj fingerprint applies user exclude patterns like discovery does`` () =
    withTempDir "fingerprint-excludes" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "A")
        let vendorDir = Path.Combine(tmpDir, "src", "Vendor")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(vendorDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "A.fsproj"), "<Project />")
        File.WriteAllText(Path.Combine(vendorDir, "V.fsproj"), "<Project />")

        let excludes = [ "src/Vendor/" ]
        let fp = FsHotWatch.Daemon.fingerprintFsprojFiles tmpDir excludes

        // The registered project is fingerprinted; the excluded one is not, so
        // editing it cannot trigger a spurious re-discovery.
        test <@ fp |> Set.exists (fun (p, _) -> p.EndsWith("A.fsproj")) @>
        test <@ fp |> Set.forall (fun (p, _) -> not (p.Contains("Vendor"))) @>

        // And the fingerprint must still react to edits of NON-excluded
        // projects (mtime is part of the key).
        let before = fp
        System.Threading.Thread.Sleep(20)
        File.SetLastWriteTimeUtc(Path.Combine(srcDir, "A.fsproj"), DateTime.UtcNow)
        let after = FsHotWatch.Daemon.fingerprintFsprojFiles tmpDir excludes
        test <@ before <> after @>)

// ============================================================================
// AUTOMATION-300 — the rename pruning that stops a vanished path being analysed
// ============================================================================

[<Fact>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``a renamed file leaves the scan set — the old path is vanished, the new one is not`` () =
    // The ticket's actual trigger. Before the fix a rename left the old path
    // registered, FCS was asked to parse a file that cannot be opened, and the
    // resulting finding was keyed to a path nothing would ever check again — so
    // the gate stayed red until the daemon was stopped, which is the one command
    // the docs tell you not to run.
    let oldPath = "/repo/src/OldName.fs"
    let newPath = "/repo/src/NewName.fs"
    let onDisk = set [ newPath; "/repo/src/Untouched.fs" ]

    let files, vanished =
        partitionVanished onDisk.Contains [ oldPath; newPath; "/repo/src/Untouched.fs" ]

    // The property that matters: the scan never sees the dead path. Clearing
    // AFTER scanning would leave the finding standing, and a scan that saw the
    // path is a scan the clear did not precede — so this catches a reordering,
    // not merely a missing filter.
    test <@ not (List.contains oldPath files) @>
    test <@ List.contains oldPath vanished @>

    // And the rename's destination is scanned rather than swept out with it.
    test <@ List.contains newPath files @>
    test <@ not (List.contains newPath vanished) @>

[<Fact>]
[<Trait("PositiveControl", "AUTOMATION-300")>]
let ``nothing vanishes when every registered file is still on disk`` () =
    // Without this, a partition that reported EVERYTHING as vanished would pass
    // the test above while clearing the findings of every file in the repository
    // on each scan — a far worse failure than the one being fixed, and invisible
    // from the refusal side alone.
    let registered = [ "/repo/src/A.fs"; "/repo/src/B.fs"; "/repo/tests/C.fs" ]
    let files, vanished = partitionVanished (fun _ -> true) registered

    test <@ files = registered @>
    test <@ List.isEmpty vanished @>

[<Fact>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``a deleted file is pruned the same way a renamed one is`` () =
    // The fix is existence-based rather than rename-aware on purpose: the
    // fsproj-fingerprint path does not fire for a glob-matched file whose
    // removal leaves every project file byte-identical, so existence is the
    // backstop that does not depend on how the removal happened to touch them.
    let deleted = "/repo/src/Gone.fs"

    let files, vanished =
        partitionVanished (fun p -> p <> deleted) [ deleted; "/repo/src/Stays.fs" ]

    test <@ vanished = [ deleted ] @>
    test <@ files = [ "/repo/src/Stays.fs" ] @>
