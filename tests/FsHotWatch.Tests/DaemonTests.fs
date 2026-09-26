[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.DaemonTests

// FS0057: `TransparentCompiler.CacheSizes` is marked experimental in FCS 43.*.
#nowarn "57"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
// `DiscoverySnapshot` is an alias for `ProjectModel.Counts`, so
// its record labels resolve from the ProjectModel module rather than from Daemon.
open FsHotWatch.ProjectModel
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

/// Counts how many times the daemon actually evaluated the workspace. Used where the
/// question is "did a rediscovery happen at all", which no other seam answers.
type private CountingWorkspaceLoader(results: Types.ProjectOptions list) =
    let notifications = Event<Types.WorkspaceProjectState>()
    let loads = ref 0

    member _.Loads = Threading.Volatile.Read(&loads.contents)

    member private _.Load() =
        Threading.Interlocked.Increment(&loads.contents) |> ignore
        results :> seq<_>

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

// --- `RunMode` is the ONLY thing that decides watcher construction ---

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

                member _.Process (changedFiles: string list) (_repoRoot: string) =
                    Ok
                        { Modified = changedFiles
                          Considered = changedFiles.Length
                          Evidence = "test-formatter" }

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
              PrepareCommit = None
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let host = daemon.Host
        let testFiles = [ Path.Combine(srcDir, "Fmt.fs") ]
        let modified = host.RunPreprocessors(testFiles).Modified
        test <@ modified.Length = testFiles.Length @>

        let status = host.GetStatus("test-formatter")
        test <@ status.IsSome @>

        cts.Cancel())

[<Fact(Timeout = 150000)>]
let ``a file a preprocessor rewrites joins the batch the plugins receive`` () =
    // A generator rewrites `Gen.fs` when `New.fs` changes. `Gen.fs` was never in the
    // batch, but it is what the build must now see, so it is dispatched with the batch.
    withTempDir "daemon-pre-join" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let genFile = Path.Combine(srcDir, "Gen.fs")
        let mutable receivedBatches: string list list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        daemon.RegisterPreprocessor(
            { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                member _.Name = "generator"

                member _.Process files _ =
                    File.WriteAllText(genFile, $"module Gen // {Guid.NewGuid():N}")

                    Ok
                        { Modified = [ FsHotWatch.CommandPreprocessor.realPathOf genFile ]
                          Considered = files.Length
                          Evidence = "generator" }

                member _.Dispose() = () }
        )

        let handler =
            { Name = PluginName.create "batch-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged(SourceChanged files) -> receivedBatches <- files :: receivedBatches
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              PrepareCommit = None
              CacheKey = None
              Teardown = None }

        daemon.RegisterHandler(handler)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        waitForDaemonReady srcDir (fun () -> receivedBatches.Length)
        receivedBatches <- []

        let newFile = Path.Combine(srcDir, "New.fs")

        // The watcher names paths in their real form (`/private/var/…` on macOS).
        let real = FsHotWatch.CommandPreprocessor.realPathOf

        let joined () =
            receivedBatches
            |> List.exists (fun batch ->
                let batch = batch |> List.map real
                List.contains (real newFile) batch && List.contains (real genFile) batch)

        probeLoop (fun n -> File.WriteAllText(newFile, $"module New // v{n}")) joined 60000

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        Assert.True(joined (), $"no batch held both %s{newFile} and %s{genFile}; batches: %A{receivedBatches}"))

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
              PrepareCommit = None
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
              PrepareCommit = None
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
              PrepareCommit = None
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
              PrepareCommit = None
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

[<Fact(Timeout = 15000)>]
let ``Daemon.create hands the configured cache size factor to the checker`` () =
    withTempDir "daemon-cache-sizes" (fun tmpDir ->
        let mutable captured = None

        let make (sizes: FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes) =
            captured <- Some sizes
            nullChecker

        use daemon =
            Daemon.createUsing
                make
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    CheckerCacheSizeFactor = 10 }

        test <@ daemon.RepoRoot = tmpDir @>
        test <@ captured = Some(FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes.Create 10) @>

        test
            <@
                captured
                <> Some(FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes.Create 100)
            @>)

[<Fact(Timeout = 15000)>]
let ``Daemon.create logs the effective cache size factor as one key=value config line`` () =
    // The bench harness scrapes this exact line from daemon.log to prove which factor
    // a record ran at, so its wording is an interface: `checker:` then `key=value` pairs.
    withTempDir "daemon-cache-sizes-log" (fun tmpDir ->
        let originalErr = Console.Error
        let originalLevel = Logging.logLevel
        use sw = new StringWriter()
        Console.SetError sw
        Logging.setLogLevel Logging.LogLevel.Info

        try
            use _daemon =
                Daemon.createUsing
                    (fun _ -> nullChecker)
                    tmpDir
                    { Daemon.DaemonOptions.defaults with
                        CheckerCacheSizeFactor = 20 }

            ()
        finally
            Console.SetError originalErr
            Logging.setLogLevel originalLevel

        let lines =
            sw.ToString().Split('\n')
            |> Array.filter (fun l ->
                Text.RegularExpressions.Regex.IsMatch(l, @"^  \[config\] \S+ checker: cacheSizeFactor=20\r?$"))

        test <@ lines.Length = 1 @>)

[<Fact(Timeout = 15000)>]
let ``DaemonOptions default cache size factor is the FCS default`` () =
    test <@ Daemon.DaemonOptions.defaults.CheckerCacheSizeFactor = Daemon.DefaultCheckerCacheSizeFactor @>

    // FCS's own default (`CacheSizes.Default`, used when no sizes are passed) is
    // hidden by its signature file, so read it reflectively: leaving the key out must
    // change nothing, and this fails if an FCS upgrade moves the default.
    let fcsDefault =
        typeof<FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes>
            .GetProperty("Default", Reflection.BindingFlags.Static ||| Reflection.BindingFlags.NonPublic)
            .GetValue(null)
        :?> FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes

    test
        <@
            FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes.Create Daemon.DefaultCheckerCacheSizeFactor = fcsDefault
        @>

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
              PrepareCommit = None
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

    let outcome =
        runChecksWithRetry 3 check (fun r -> emitted.Add r) files
        |> Async.RunSynchronously

    // A converged scan says so in the type, and reports ZERO
    // extra rounds — the amplification measure the retry budget exists to bound.
    test <@ outcome = ScanCheckOutcome.AllChecked 0 @>
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

    let outcome =
        runChecksWithRetry 3 check (fun r -> emitted.Add r) files
        |> Async.RunSynchronously

    // Converged, but it cost exactly ONE extra round — the number that
    // distinguishes a single self-cancellation from runaway amplification.
    test <@ outcome = ScanCheckOutcome.AllChecked 1 @>
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

    let outcome =
        runChecksWithRetry 2 check (fun _ -> ()) files |> Async.RunSynchronously

    // Reaching the bound is a NAMED outcome carrying the files,
    // the rounds spent, and the budget that was exhausted — not an integer
    // folded into a total that reads the same as "nothing to do".
    test <@ outcome = ScanCheckOutcome.BudgetExhausted([ AbsFilePath.create "/b.fs" ], 2, 2) @>

    test <@ ScanCheckOutcome.uncheckedCount outcome = 1 @>
    test <@ ScanCheckOutcome.extraRounds outcome = 2 @>
    // Bounded: 1 initial attempt + 2 retries = 3 total for the failing file.
    test <@ attempts["/b.fs"] = 3 @>
    // Succeeding files are only attempted once.
    test <@ attempts["/a.fs"] = 1 @>

[<Fact(Timeout = 15000)>]
let ``runChecksWithRetry stops at the bound and never exceeds it`` () =
    // The ceiling is exact and asserted,: for a file that is
    // ALWAYS cancelled, total attempts are 1 + budget, whatever the budget is.
    for budget in 0..4 do
        let attempts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

        let check (f: AbsFilePath) =
            async {
                attempts.AddOrUpdate(AbsFilePath.value f, 1, fun _ c -> c + 1) |> ignore
                return None
            }

        let files = [ AbsFilePath.create "/never.fs" ]

        let outcome =
            runChecksWithRetry budget check (fun (_: int) -> ()) files
            |> Async.RunSynchronously

        test <@ attempts["/never.fs"] = budget + 1 @>
        test <@ outcome = ScanCheckOutcome.BudgetExhausted(files, budget, budget) @>

[<Fact(Timeout = 15000)>]
let ``runChecksWithRetry on an empty cohort converges with no rounds`` () =
    let outcome =
        runChecksWithRetry 3 (fun _ -> async { return Some 1 }) (fun (_: int) -> ()) []
        |> Async.RunSynchronously

    test <@ outcome = ScanCheckOutcome.AllChecked 0 @>

[<Fact(Timeout = 15000)>]
let ``formatScanStatusWith surfaces unchecked count as non-ok when incomplete`` () =
    let status = formatScanStatusWith 70 5 (ScanComplete(TimeSpan.FromSeconds(15.5)))

    // Must not read as clean: surfaces the unchecked files explicitly, with the
    // checked count as (registered - unchecked).
    test <@ status = "incomplete: 65 files checked, 5 unchecked in 15.5s" @>
    test <@ status.ToLowerInvariant().Contains("unchecked") @>

[<Theory(Timeout = 30000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``blocked scan discovery remains owned and observable`` (readStatus: bool) =
    withTempDir "daemon-scan-owner" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Blocked.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let loader = BlockingWorkspaceLoader([])

        use daemon =
            Daemon.createWithWorkspaceLoader
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                loader
                (fun _ -> [])

        let scan = Async.StartAsTask(daemon.ScanAll())
        let mutable observation: Task<ScanState * int64> option = None

        try
            Assert.True(loader.Entered.Wait(TimeSpan.FromSeconds 10.0), "scan must enter controlled discovery")

            if readStatus then
                let reading = Task.Run(fun () -> daemon.GetScanState(), daemon.GetScanGeneration())

                observation <- Some reading
                Assert.True(reading.Wait(TimeSpan.FromSeconds 5.0), "scan observation must not wait for the loader")
                let state, generation = reading.GetAwaiter().GetResult()

                match state with
                | Scanning _ -> ()
                | other -> failwithf "Expected a published active scan while discovery is blocked, got %A" other

                Assert.Equal(0L, generation)
            else
                Assert.True(daemon.Host.AnyPluginBusy(), "an admitted scan must be owned work")
                Assert.Contains("scan", daemon.Host.BusyPluginNames())
        finally
            loader.Resume()
            scan.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

            observation
            |> Option.iter (fun reading ->
                reading.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult() |> ignore)

        Assert.Equal(1L, daemon.GetScanGeneration())
        Assert.False(daemon.Host.AnyPluginBusy(), "a settled scan must retire its own work"))

[<Theory(Timeout = 30000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``watcher change remains owned through debounce and blocked rediscovery`` (waitForDiscovery: bool) =
    withTempDir "daemon-change-owner" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Blocked.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let loader = BlockingWorkspaceLoader([])
        let callback = ref None

        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                callback.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                nullChecker
                tmpDir
                Daemon.DaemonOptions.defaults
                loader
                (fun _ -> [])
                watcher

        let deliver = callback.Value |> Option.get

        try
            deliver SolutionChanged

            if waitForDiscovery then
                Assert.True(
                    loader.Entered.Wait(TimeSpan.FromSeconds 5.0),
                    "the change must enter controlled discovery"
                )

            Assert.True(
                daemon.Host.AnyPluginBusy(),
                "an accepted watcher change must be owned before any plugin receives work"
            )

            Assert.Contains("changes", daemon.Host.BusyPluginNames())
        finally
            loader.Resume()
            // One controlled change and an inert watcher make this completed phase an
            // exact witness that the real batch callback returned.
            Assert.True(
                SpinWait.SpinUntil(
                    (fun () ->
                        daemon.Host.Phases.Snapshot(DateTime.UtcNow)
                        |> List.exists (fun phase -> phase.Scope = "daemon.check" && phase.Detail <> Some "in flight")),
                    TimeSpan.FromSeconds 10.0
                ),
                "the controlled batch must drain before the fixture is disposed"
            ))

[<Fact(Timeout = 20000)>]
let ``disposed daemon rejects scan admission instead of abandoning its receipt`` () =
    withTempDir "daemon-scan-after-dispose" (fun tmpDir ->
        use daemon =
            Daemon.createWith
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }

        (daemon :> IDisposable).Dispose()
        use requestLifetime = new CancellationTokenSource()

        let request =
            Async.StartAsTask(daemon.ScanAll(), cancellationToken = requestLifetime.Token)

        try
            Assert.Throws<ObjectDisposedException>(fun () ->
                request.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())
            |> ignore
        finally
            // A receipt nobody will answer would outlive this test: cancel the caller
            // and drain it, so a failing assertion leaves no waiter behind.
            requestLifetime.Cancel()

            try
                request.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            with
            | :? OperationCanceledException -> ()
            | :? ObjectDisposedException -> ())

[<Fact(Timeout = 30000)>]
let ``failed scan receipt reaches IPC and a later scan recovers`` () =
    withTempDir "scan-failed-receipt" (fun tmpDir ->
        let sourceDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(sourceDir) |> ignore
        let projectPath = Path.Combine(sourceDir, "Probe.fsproj")
        File.WriteAllText(projectPath, "<Project />")
        let loaded = minimalLoadedProject projectPath
        let loader = SequencedWorkspaceLoader([ [ loaded ]; [ loaded ] ])
        loader.Resume(0)

        let options =
            { Daemon.DaemonOptions.defaults with
                RunMode = Daemon.RunMode.OneShot }

        use daemon =
            Daemon.createWithWorkspaceLoader nullChecker tmpDir options loader (fun projects ->
                projects
                |> List.map (fun project -> makeProjectOptions project.ProjectFileName [] []))

        use serverLifetime = new CancellationTokenSource()
        use requestLifetime = new CancellationTokenSource()
        let pipeName = $"scanf-{Guid.NewGuid():N}"
        let server = Async.StartAsTask(daemon.RunWithIpc(pipeName, serverLifetime))

        try
            waitUntil (fun () -> daemon.GetScanGeneration() > 0L) 5000
            let before = daemon.GetScanGeneration()
            File.AppendAllText(projectPath, "<!-- force rediscovery -->")

            let scan =
                Async.StartAsTask(daemon.ScanAll(), cancellationToken = requestLifetime.Token)

            test <@ loader.Entered(1).Wait(TimeSpan.FromSeconds 5.0) @>
            requestLifetime.Cancel()
            loader.Resume(1)

            let failedScan =
                Assert.ThrowsAny<Exception>(fun () ->
                    scan.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())

            test <@ failedScan.GetBaseException() :? OperationCanceledException @>
            // Registered after the failure: the receipt must survive this ordinary RPC race.
            let waiter = Async.StartAsTask(FsHotWatch.Ipc.IpcClient.waitForScan pipeName before)

            let observed =
                Assert.ThrowsAny<Exception>(fun () ->
                    waiter.WaitAsync(TimeSpan.FromSeconds 3.0).GetAwaiter().GetResult() |> ignore)

            test <@ not (observed :? TimeoutException) @>
            test <@ daemon.GetScanGeneration() = before @>
            // The same sequence `fshw check` sends: request a scan, then wait for it. The
            // wait belongs to the new request, never to the earlier failure.
            FsHotWatch.Ipc.IpcClient.scan pipeName |> Async.RunSynchronously |> ignore

            let recovered =
                FsHotWatch.Ipc.IpcClient.waitForScan pipeName before
                |> Async.StartAsTask
                |> fun pending -> pending.WaitAsync(TimeSpan.FromSeconds 3.0).GetAwaiter().GetResult()

            test <@ recovered.Contains("complete") @>
            test <@ daemon.GetScanGeneration() > before @>
        finally
            loader.Resume(1)
            serverLifetime.Cancel()

            try
                server.Wait(TimeSpan.FromSeconds 5.0) |> ignore
            with _ ->
                ())

[<Theory(Timeout = 30000)>]
[<InlineData(false, false)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``scan waits for discovery and re-scans when its captured model is invalidated``
    (hasSource: bool, rediscoverAfterCapture: bool)
    =
    withTempDir "daemon-scan-discovery-race" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Stable.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let sourcePath = Path.Combine(srcDir, "Stable.fs")
        let sources = if hasSource then [ sourcePath ] else []

        if hasSource then
            File.WriteAllText(sourcePath, "module Stable\nlet value = 1\n")

        let loaded =
            { minimalLoadedProject projectPath with
                SourceFiles = sources }

        let loader = SequencedWorkspaceLoader([ [ loaded ]; [ loaded ] ])
        loader.Resume(0)

        // The injected loader supplies evaluated compiler options; give the
        // independent deps guard its fresh assets marker so this race test does
        // not attempt a real restore of the deliberately minimal project.
        let objDir = Path.Combine(srcDir, "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")
        let checker = sharedChecker.Value

        let fcsOptions =
            if hasSource then
                let options, _ =
                    checker.GetProjectOptionsFromScript(
                        sourcePath,
                        FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText(sourcePath))
                    )
                    |> Async.RunSynchronously

                { options with
                    ProjectFileName = projectPath
                    SourceFiles = [| sourcePath |] }
            else
                makeProjectOptions projectPath [] []

        let options =
            { Daemon.DaemonOptions.defaults with
                RunMode = Daemon.RunMode.OneShot }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                tmpDir
                options
                loader
                (fun projects -> projects |> List.map (fun _ -> fcsOptions))
                (fun _ _ _ _ _ -> failwith "controlled discovery race must not construct an ambient watcher")

        // This test owns every discovery admission. Native filesystem history
        // must not add a third writer before the controlled concurrency begins.
        // Establish both a healthy registry and the scan's unchanged fingerprint.
        daemon.ScanAll() |> Async.RunSynchronously
        test <@ daemon.Pipeline.GetRegisteredProjects().Length = 1 @>

        // Every seal published from here on. A scan that recovers must stamp its
        // cohort with the model it actually checked, never the superseded one.
        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "race-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        let originalProject = File.ReadAllBytes(projectPath)
        let originalWriteTime = File.GetLastWriteTimeUtc(projectPath)
        use preprocessorEntered = new Threading.ManualResetEventSlim(false)
        use preprocessorResume = new Threading.ManualResetEventSlim(false)
        let mutable rediscovery: System.Threading.Tasks.Task<unit> option = None
        let mutable scan: System.Threading.Tasks.Task<unit> option = None

        if rediscoverAfterCapture then
            daemon.RegisterPreprocessor(
                { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                    member _.Name = "capture-barrier"

                    member _.Process files _ =
                        preprocessorEntered.Set()
                        preprocessorResume.Wait()

                        Ok
                            { Modified = []
                              Considered = files.Length
                              Evidence = "capture barrier released" }

                    member _.Dispose() = () }
            )

        try
            if rediscoverAfterCapture then
                scan <- Some(Async.StartAsTask(daemon.ScanAll()))
                test <@ preprocessorEntered.Wait(TimeSpan.FromSeconds(10.0)) @>

            rediscovery <- Some(Async.StartAsTask(daemon.DiscoverAndRegisterProjects()))
            test <@ loader.Entered(1).Wait(TimeSpan.FromSeconds(10.0)) @>
            test <@ daemon.Pipeline.GetRegisteredProjects().IsEmpty @>

            if not rediscoverAfterCapture then
                scan <- Some(Async.StartAsTask(daemon.ScanAll()))

            let runningScan = scan.Value
            preprocessorResume.Set()

            let first =
                System.Threading.Tasks.Task
                    .WhenAny(runningScan, System.Threading.Tasks.Task.Delay(1000))
                    .GetAwaiter()
                    .GetResult()

            let completedWhileCleared = obj.ReferenceEquals(first, runningScan)
            loader.Resume(1)
            rediscovery.Value.GetAwaiter().GetResult()
            runningScan.GetAwaiter().GetResult()

            // A mid-scan rediscovery is a legitimate event, not a reason to abandon
            // the caller's scan. The refusal to publish stale results stands; the
            // recovery from it is to capture the model that replaced it and scan again.
            test
                <@
                    daemon.Host.WorkSnapshot.OperationFaults
                    |> List.forall (fun (name, _) -> name <> "scan")
                @>

            test <@ File.ReadAllBytes(projectPath) = originalProject @>
            test <@ File.GetLastWriteTimeUtc(projectPath) = originalWriteTime @>
            test <@ daemon.Pipeline.GetRegisteredProjects().Length = 1 @>
            // Neither shape completes while the model is cleared: one waits for the
            // attempt in flight before capturing, the other waits for it before
            // re-capturing. A scan that finished here would have published against a
            // model no plugin can still see.
            test <@ not completedWhileCleared @>

            // The whole point of the refusal, stated positively: the cohort the scan
            // sealed is stamped with the generation the rediscovery installed (2),
            // not the one it originally captured.
            Assert.True(
                SpinWait.SpinUntil((fun () -> not seals.IsEmpty), TimeSpan.FromSeconds 10.0),
                "the completed scan must seal its cohort"
            )

            let sealed' = seals.ToArray() |> Array.last
            test <@ sealed'.ModelGeneration = Some 2L @>

            let scans =
                FsHotWatch.ScanMetrics.readSeries (FsHotWatch.ScanMetrics.recordPath tmpDir)

            test <@ scans.Length = 2 @>

            for sample in scans do
                test <@ sample.FilesRegistered = sources.Length @>
                test <@ sample.FilesChecked = sources.Length @>
                test <@ sample.FilesUnchecked = 0 @>
        finally
            preprocessorResume.Set()
            loader.Resume(1)
            rediscovery |> Option.iter (fun running -> running.GetAwaiter().GetResult())

            scan |> Option.iter (fun running -> running.GetAwaiter().GetResult()))

[<Fact(Timeout = 30000)>]
let ``observing project content answers the next echo of the same bytes as unchanged`` () =
    withTempDir "content-observe-projects" (fun root ->
        let directory = Path.Combine(root, "src")
        Directory.CreateDirectory directory |> ignore
        let kept = Path.Combine(directory, "Kept.fsproj")
        let edited = Path.Combine(directory, "Edited.fsproj")
        File.WriteAllText(kept, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(edited, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        // Restore's output for those projects. A build rewrites it, the watcher admits
        // it at the project tier, so the tracker needs a prior for it exactly as much
        // as for the `.fsproj` beside it.
        let objDirectory = Path.Combine(directory, "obj")
        Directory.CreateDirectory objDirectory |> ignore
        let assets = Path.Combine(objDirectory, "project.assets.json")
        File.WriteAllText(assets, "{}")

        let tracker = FsHotWatch.ContentDedup.Tracker()

        // An UNSEEDED tracker calls both of them changed — correct for a file it has
        // no prior for, and the false positive that re-discovered a cold workspace.
        let unseeded = FsHotWatch.ContentDedup.Tracker()
        test <@ unseeded.HasContentChanged kept @>
        test <@ unseeded.HasContentChanged assets @>

        FsHotWatch.Daemon.observeProjectContent root [] tracker

        test <@ not (tracker.HasContentChanged kept) @>
        test <@ not (tracker.HasContentChanged edited) @>

        // Seeded even though it sits under `obj/`, which every exclude filter drops.
        test <@ not (tracker.HasContentChanged assets) @>

        // Content, not identity: a real edit is still a change.
        File.WriteAllText(edited, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>")
        test <@ tracker.HasContentChanged edited @>
        test <@ not (tracker.HasContentChanged kept) @>

        // And a restore that genuinely changes the package graph is still a change.
        File.WriteAllText(assets, "{\"version\": 3}")
        test <@ tracker.HasContentChanged assets @>

        // A project file an exclude pattern hides is not observed, so nothing about it
        // is claimed: it keeps the no-prior answer.
        let excluded = FsHotWatch.ContentDedup.Tracker()
        FsHotWatch.Daemon.observeProjectContent root [ "src" ] excluded
        test <@ excluded.HasContentChanged kept @>

        // The assets file is derived from the project that owns it, so an excluded
        // project takes its restore output out of scope with it rather than leaving a
        // seeded orphan behind.
        test <@ excluded.HasContentChanged assets @>)

[<Fact(Timeout = 60000)>]
let ``a cold daemon does not re-discover on a watcher echo of an unchanged project file`` () =
    withTempDir "daemon-cold-project-echo" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Echo.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let sourcePath = Path.Combine(srcDir, "Echo.fs")
        File.WriteAllText(sourcePath, "module Echo\nlet value = 1\n")

        let objDir = Path.Combine(srcDir, "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")

        let loaded =
            { minimalLoadedProject projectPath with
                SourceFiles = [ sourcePath ] }

        let loader = CountingWorkspaceLoader([ loaded ])
        let checker = sharedChecker.Value

        let fcsOptions =
            let scriptOptions, _ =
                checker.GetProjectOptionsFromScript(
                    sourcePath,
                    FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText(sourcePath))
                )
                |> Async.RunSynchronously

            { scriptOptions with
                ProjectFileName = projectPath
                SourceFiles = [| sourcePath |] }

        let callback = ref None

        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                callback.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                tmpDir
                Daemon.DaemonOptions.defaults
                loader
                (fun projects -> projects |> List.map (fun _ -> fcsOptions))
                watcher

        // IN-SESSION cohorts only. The cold scan seals too, and counting that one would
        // let every wait below return before the watcher's batch had even been admitted.
        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "echo-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | BatchChecked batch ->
                            match batch.Trigger with
                            | InSessionBatch _ -> seals.Enqueue batch
                            | BootScan -> ()
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        // The cold scan — the daemon's first and only evaluation of this workspace.
        daemon.ScanAll() |> Async.RunSynchronously
        test <@ loader.Loads = 1 @>
        let deliver = callback.Value |> Option.get

        // The echo a cold `check` provokes on itself: the watcher reports the project
        // file, whose bytes are exactly the ones discovery has just read. The source
        // change behind it is genuinely new, so its cohort seals — and the queue is
        // FIFO, so that seal proves the project echo has already been dealt with.
        deliver (ProjectChanged [ projectPath ])
        deliver (SourceChanged [ sourcePath ])

        Assert.True(
            SpinWait.SpinUntil((fun () -> not seals.IsEmpty), TimeSpan.FromSeconds 20.0),
            "the source change must produce a sealed cohort"
        )

        test <@ loader.Loads = 1 @>

        // The same echo for restore's OUTPUT. `dotnet build` rewrites every project's
        // assets file, so of the paths the project tier admits this is the one a cold
        // `check` is guaranteed to provoke against itself — and it arrives while the
        // scan that triggered the build is still waiting for it to finish.
        File.WriteAllText(sourcePath, "module Echo\nlet value = 2\n")
        deliver (ProjectChanged [ Path.Combine(objDir, "project.assets.json") ])
        deliver (SourceChanged [ sourcePath ])

        Assert.True(
            SpinWait.SpinUntil((fun () -> seals.Count >= 2), TimeSpan.FromSeconds 20.0),
            "the second source change must produce a sealed cohort"
        )

        test <@ loader.Loads = 1 @>

        // And the suppression is about CONTENT, not about project files: the same path
        // with different bytes still re-discovers.
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>")
        deliver (ProjectChanged [ projectPath ])

        Assert.True(
            SpinWait.SpinUntil((fun () -> loader.Loads > 1), TimeSpan.FromSeconds 20.0),
            "a project file whose content really changed must re-discover"
        )

        test <@ loader.Loads = 2 @>)

[<Fact(Timeout = 90000)>]
let ``a scan whose model changes on every attempt fails by name instead of looping`` () =
    withTempDir "daemon-scan-model-storm" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let projectPath = Path.Combine(srcDir, "Storm.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let sourcePath = Path.Combine(srcDir, "Storm.fs")
        File.WriteAllText(sourcePath, "module Storm\nlet value = 1\n")

        let objDir = Path.Combine(srcDir, "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")

        let loaded =
            { minimalLoadedProject projectPath with
                SourceFiles = [ sourcePath ] }

        // One outcome, repeated: the storm needs an unbounded number of successful
        // rediscoveries, and every one of them installs a NEW generation.
        let loader = SequencedWorkspaceLoader([ [ loaded ] ])
        loader.Resume(0)
        let checker = sharedChecker.Value

        let fcsOptions =
            let scriptOptions, _ =
                checker.GetProjectOptionsFromScript(
                    sourcePath,
                    FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText(sourcePath))
                )
                |> Async.RunSynchronously

            { scriptOptions with
                ProjectFileName = projectPath
                SourceFiles = [| sourcePath |] }

        let options =
            { Daemon.DaemonOptions.defaults with
                RunMode = Daemon.RunMode.OneShot }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                tmpDir
                options
                loader
                (fun projects -> projects |> List.map (fun _ -> fcsOptions))
                (fun _ _ _ _ _ -> failwith "the scan storm must not construct an ambient watcher")

        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
        test <@ daemon.Pipeline.GetRegisteredProjects().Length = 1 @>

        let attempts = ref 0

        // The preprocessor runs inside every scan attempt, AFTER that attempt captured
        // its model — exactly where a concurrent rediscovery lands in production. This
        // one replaces the model on every single attempt, so no attempt can ever publish.
        daemon.RegisterPreprocessor(
            { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                member _.Name = "scan-model-storm"

                member _.Process files _ =
                    Interlocked.Increment(&attempts.contents) |> ignore
                    daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously

                    Ok
                        { Modified = []
                          Considered = files.Length
                          Evidence = "storm barrier" }

                member _.Dispose() = () }
        )

        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "storm-seal-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        let failure =
            Assert.ThrowsAny<Exception>(fun () -> daemon.ScanAll() |> Async.RunSynchronously)

        let cause =
            match failure with
            | :? AggregateException as aggregate -> aggregate.Flatten().InnerExceptions |> Seq.head
            | other -> other

        // The recovery TERMINATES, and says why in words an operator can act on. The
        // alternative it replaces is `Could not connect to daemon`, which sends the
        // reader to a pipe that was working the whole time.
        test <@ FsHotWatch.Daemon.isModelKeptChangingDuringScanMessage cause.Message @>
        test <@ cause.Message.Contains("replaced during each of 5 scan attempts") @>
        test <@ not (cause.Message.Contains("connect to daemon")) @>

        // Bounded: it retried, and it stopped at the bound rather than spinning.
        test <@ attempts.Value = FsHotWatch.Daemon.scanAttemptLimit @>

        // Nothing was published about any of the models it captured.
        Assert.Empty(seals.ToArray())

        test
            <@
                daemon.Host.WorkSnapshot.OperationFaults
                |> List.exists (fun (name, fault) ->
                    name = "scan"
                    && FsHotWatch.Daemon.isModelKeptChangingDuringScanMessage fault.Message)
            @>)

[<Fact(Timeout = 45000)>]
let ``superseded owned change retries its admitted source against the current model`` () =
    withTempDir "daemon-change-model-successor" (fun root ->
        let directory = Path.Combine(root, "src")
        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Probe.fsproj")
        let source = Path.Combine(directory, "Probe.fs")
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(source, "module Probe\nlet value = 1\n")
        let assets = FsHotWatch.DepsFreshness.assetsPath project
        Directory.CreateDirectory(Path.GetDirectoryName assets) |> ignore
        File.WriteAllText(assets, "{}")
        // Own script resolution and FCS caches: other fixtures use the shared
        // checker concurrently, while this test requires an actual full-check result.
        let checker =
            FSharp.Compiler.CodeAnalysis.FSharpChecker.Create(
                projectCacheSize = 200,
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true
            )

        let options, optionDiagnostics =
            checker.GetProjectOptionsFromScript(
                source,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText source),
                assumeDotNetFramework = false
            )
            |> Async.RunSynchronously

        Assert.Empty(
            optionDiagnostics
            |> List.filter (fun diagnostic ->
                diagnostic.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        )

        let options =
            { options with
                ProjectFileName = project
                SourceFiles = [| source |] }

        let loaded =
            { minimalLoadedProject project with
                SourceFiles = [ source ] }

        let loader = SequencedWorkspaceLoader([ [ loaded ]; [ loaded ] ])
        loader.Resume(0)
        loader.Resume(1)
        let callback = ref None

        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                callback.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                root
                Daemon.DaemonOptions.defaults
                loader
                (fun _ -> [ options ])
                watcher

        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
        use admitted = new Threading.ManualResetEventSlim(false)
        use release = new Threading.ManualResetEventSlim(false)
        let mutable preprocessingAttempts = 0

        daemon.RegisterPreprocessor(
            { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                member _.Name = "admitted-change-barrier"

                member _.Process files _ =
                    if Threading.Interlocked.Increment(&preprocessingAttempts) = 1 then
                        admitted.Set()

                        Assert.True(
                            release.Wait(TimeSpan.FromSeconds 20.0),
                            "the controlled model replacement must release preprocessing"
                        )

                    Ok
                        { Modified = []
                          Considered = files.Length
                          Evidence = "admitted source released" }

                member _.Dispose() = () }
        )

        let results = System.Collections.Concurrent.ConcurrentQueue<FileCheckResult>()
        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "successor-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | FileChecked result -> results.Enqueue result
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChecked; SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        try
            // The barrier is after both model capture and ContentTracker admission.
            // Deliver no second event: a retry that rechecks the dedup tracker loses this input.
            (callback.Value |> Option.get) (SourceChanged [ source ])
            Assert.True(admitted.Wait(TimeSpan.FromSeconds 10.0), "the original change must be admitted")
            daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
            release.Set()

            Assert.True(
                SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 15.0),
                "the original owned change and its current-model successor must settle"
            )

            Assert.True(daemon.Host.WorkSnapshot.Faults.IsEmpty, "model supersession must not leave an owned failure")
            let completed = Assert.Single(seals.ToArray())
            Assert.Equal(Some 2L, completed.ModelGeneration)
            Assert.Contains(AbsFilePath.create source, completed.Files)
            let checkedSource = Assert.Single(results.ToArray())
            Assert.Equal(AbsFilePath.create source, checkedSource.File)
            Assert.Equal(Some 2L, checkedSource.ModelGeneration)

            match checkedSource.CheckResults with
            | FullCheck _ -> ()
            | ParseOnly -> failwith "the successor must publish actual completed FCS analysis"

            Assert.True(preprocessingAttempts >= 2, "the admitted source must reach the successor attempt")
        finally
            release.Set()

            Assert.True(
                SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 15.0),
                "all change ownership must retire before fixture disposal"
            ))

// Pins the invariant the scan supervisor must preserve: once ScanAll's receipt
// settles, the scan state is observable as ScanComplete (not stale ScanIdle) and the
// generation has advanced. The supervisor publishes the completed state before the
// request retires and before its receipt settles, so any later read observes it.
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
              PrepareCommit = None
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
// the rename pruning that stops a vanished path being analysed
// ============================================================================

[<Fact>]
let ``a renamed file leaves the scan set — the old path is vanished, the new one is not`` () =
    // The actual trigger. Before the fix a rename left the old path
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

// --- a scan dispatches each file once, across tiers as well as within one ---

[<Fact>]
let ``a file reached through two different tiers is dispatched by the first of them`` () =
    // The defect this pins: the per-tier thunk dictionary collapses a file shared by
    // two projects in the SAME tier, and cannot see one shared across tiers, so the
    // file is checked and emitted twice and the scan reports checking more files than
    // it registered.
    let shared = AbsFilePath.create "/repo/tests/Unit/TestHelpers.fs"
    let ownFile = AbsFilePath.create "/repo/tests/Unit/UnitTests.fs"
    let downstream = AbsFilePath.create "/repo/tests/Integration/IntegrationTests.fs"

    // Tier 0 owns the shared helper.
    let tier0, afterTier0 = Daemon.freshForTier Set.empty [ ownFile; shared ]
    // Tier 1 links the same helper in via a Compile Include/Link.
    let tier1, _ = Daemon.freshForTier afterTier0 [ downstream; shared ]

    test <@ tier0 = [ ownFile; shared ] @>
    test <@ tier1 = [ downstream ] @>

    // The whole point, stated as the scan states it: dispatched never exceeds distinct.
    let dispatched = tier0 @ tier1
    test <@ dispatched.Length = 3 @>
    test <@ dispatched = List.distinct dispatched @>

[<Fact>]
let ``two projects in the same tier still collapse a file they share`` () =
    // A positive control for the behaviour that already worked: the fix must not be
    // purchased by breaking within-tier collapsing, which the thunk dictionary used to
    // provide on its own.
    let shared = AbsFilePath.create "/repo/src/Shared.fs"
    let a = AbsFilePath.create "/repo/src/A.fs"
    let b = AbsFilePath.create "/repo/src/B.fs"

    let firstProject, afterFirst = Daemon.freshForTier Set.empty [ a; shared ]
    let secondProject, _ = Daemon.freshForTier afterFirst [ b; shared ]

    test <@ firstProject = [ a; shared ] @>
    test <@ secondProject = [ b ] @>

[<Fact>]
let ``a tier of entirely new files dispatches all of them, in order`` () =
    // Without this, a helper that simply returned [] would satisfy every
    // no-duplicates assertion above while checking nothing at all — a far worse
    // failure than the one being fixed, and invisible from the duplicate side alone.
    let files =
        [ "/repo/src/A.fs"; "/repo/src/B.fs"; "/repo/src/C.fs" ]
        |> List.map AbsFilePath.create

    let fresh, seen = Daemon.freshForTier Set.empty files

    test <@ fresh = files @>
    test <@ Set.count seen = 3 @>

[<Fact>]
let ``a file an earlier tier never claimed is still available to a later one`` () =
    // The deps-freshness gate refuses whole projects. Those files are never
    // dispatched, so they must not be recorded as claimed either, or a later tier
    // whose project passes the gate would silently skip them and the scan would
    // report them unchecked.
    let refused = AbsFilePath.create "/repo/src/GatedOut.fs"
    let dispatched = AbsFilePath.create "/repo/src/Dispatched.fs"

    // Only the dispatched project's files are threaded through the claim set.
    let _, afterGatedTier = Daemon.freshForTier Set.empty [ dispatched ]
    let laterTier, _ = Daemon.freshForTier afterGatedTier [ refused ]

    test <@ laterTier = [ refused ] @>

// --- a scoped invalidation says WHICH projects, not just how many ---

[<Fact>]
let ``a scoped invalidation names the projects it invalidated`` () =
    // "2 changed + 14 dependent" cost a reader on another machine a 45-minute
    // re-run at DEBUG to learn WHICH two, from paths this call site already holds.
    let rendered =
        Daemon.describeChangedProjects
            "/repo"
            [ "/repo/src/Intelligence/Intelligence.fsproj"
              "/repo/src/Shared/Shared.fsproj" ]

    test <@ rendered = " [src/Intelligence/Intelligence.fsproj, src/Shared/Shared.fsproj]" @>

[<Fact>]
let ``two projects sharing a file name stay distinguishable`` () =
    // Reported repo-relative rather than by file name on purpose. A basename that
    // merges two subjects is how a report becomes WRONG rather than merely coarse —
    // the same collision that puts a caveat on every basename-matched measurement.
    let rendered =
        Daemon.describeChangedProjects "/repo" [ "/repo/a/Tests.fsproj"; "/repo/b/Tests.fsproj" ]

    test <@ rendered = " [a/Tests.fsproj, b/Tests.fsproj]" @>

[<Fact>]
let ``a wide invalidation names some and says how many it elided`` () =
    // A tree-wide invalidation must not turn one log line into a screenful, and must
    // not truncate SILENTLY either — a reader who cannot tell a list was cut will
    // read the shown set as the whole set.
    let projects = [ for i in 1..12 -> $"/repo/p%d{i}/P%d{i}.fsproj" ]
    let rendered = Daemon.describeChangedProjects "/repo" projects

    test <@ rendered.Contains "p1/P1.fsproj" @>
    test <@ rendered.Contains "p8/P8.fsproj" @>
    test <@ not (rendered.Contains "p9/P9.fsproj") @>
    test <@ rendered.EndsWith ", and 4 more]" @>

[<Fact>]
let ``no changed projects renders nothing at all`` () =
    // The fragment is spliced into a sentence, so the empty case has to contribute
    // no text rather than an empty bracket pair.
    test <@ Daemon.describeChangedProjects "/repo" [] = "" @>

[<Fact>]
let ``a project outside the repo root keeps its absolute path`` () =
    // Relativising something outside the root yields a ladder of `..` segments that
    // is longer and harder to read than the path it replaced.
    let rendered = Daemon.describeChangedProjects "/repo" [ "/elsewhere/Other.fsproj" ]

    test <@ rendered = " [/elsewhere/Other.fsproj]" @>

// --- per-scan measurement is emitted and comparable ---

[<Fact(Timeout = 60000)>]
let ``five scan generations emit five parseable, fittable measurement records`` () =
    // The requirement is a measurement a LATER RUN CAN COMPARE, over at least
    // five generations. This drives the real scan path five times and reads the
    // record back the way a later run would: from
    // `<repoRoot>/.fshw/scan-metrics.jsonl`, through the same parser.
    withTempDir "scan-metrics-daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        use daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        for _ in 1..5 do
            daemon.ScanAll() |> Async.RunSynchronously

        let series =
            FsHotWatch.ScanMetrics.readSeries (FsHotWatch.ScanMetrics.recordPath tmpDir)

        test <@ series |> List.map (fun s -> s.Generation) = [ 1L; 2L; 3L; 4L; 5L ] @>
        // The first scan is the cold one; every later scan is forced.
        test <@ series |> List.map (fun s -> s.Kind) = [ "cold"; "forced"; "forced"; "forced"; "forced" ] @>
        // Every record carries a live process reading, so the series is fittable.
        test <@ series |> List.forall (fun s -> s.RssBytes > 0L && s.ManagedBytes > 0L) @>
        // A quiet scan needs no retries at all — the amplification floor.
        test <@ series |> List.forall (fun s -> s.RetryRounds = 0 && s.FilesUnchecked = 0) @>

        // And the fit is a NUMBER, not an impression: with data present it is
        // never `NotEnoughData`, whichever side of the bound it lands on.
        match FsHotWatch.ScanMetrics.fitRetention FsHotWatch.ScanMetrics.DefaultRetentionBound series with
        | FsHotWatch.ScanMetrics.RetentionVerdict.NotEnoughData n ->
            failwith $"five generations fitted as NotEnoughData %d{n}"
        | FsHotWatch.ScanMetrics.RetentionVerdict.WithinBound(slope, bound)
        | FsHotWatch.ScanMetrics.RetentionVerdict.ExceedsBound(slope, bound) ->
            test <@ bound = FsHotWatch.ScanMetrics.DefaultRetentionBound @>
            test <@ Double.IsFinite slope @>)

// --- timing attribution rework: the scan and discovery are named phases ---

/// The cold scan is the largest phase a `check` waits on and no plugin owns it. After
/// a forced scan the daemon's ledger must carry it — and the startup before it — with
/// a scope the verdict can name and a non-zero elapsed. (Discovery is not provoked
/// here: the fixture has no `.fsproj`, so the fingerprint never changes; its bracket
/// is the same handle `DaemonPhasesTests` covers.)
[<Fact(Timeout = 60000)>]
let ``a forced scan records daemon.scan and daemon.startup phases on the ledger`` () =
    withTempDir "scan-phase" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore

        try
            FsHotWatch.Cli.Program.forceScanAndWait FsHotWatch.Cli.Program.defaultIpcOps pipeName
            |> ignore

            waitUntil (fun () -> daemon.GetScanGeneration() >= 1L) 20000

            let completedScans () =
                daemon.Host.Phases.Snapshot(DateTime.UtcNow)
                |> List.filter (fun r -> r.Scope = "daemon.scan" && r.Detail <> Some "in flight")

            waitUntil (fun () -> not (List.isEmpty (completedScans ()))) 20000

            match completedScans () with
            | scan :: _ ->
                test <@ scan.Elapsed > TimeSpan.Zero @>
                test <@ scan.Detail |> Option.exists (fun d -> d.Contains "scan: checked") @>
            | [] -> failwith "the forced scan left no daemon.scan phase on the ledger"

            let phases = daemon.Host.Phases.Snapshot(DateTime.UtcNow)

            test
                <@
                    phases
                    |> List.exists (fun r -> r.Scope = "daemon.startup" && r.Elapsed > TimeSpan.Zero)
                @>
        finally
            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ())

// `Completed` returns None both while an attempt is in flight and
// when nothing was ever discovered. A scan that cannot tell those apart reads an
// in-flight rediscovery as an empty project model and reports NO TESTS RAN, which
// a consumer can read as a pass. These pin that the two are now distinguishable.

[<Fact(Timeout = 15000)>]
let ``an unobserved coordinator is not the same answer as one that is rediscovering`` () =
    let coordinator = DiscoveryCoordinator()
    test <@ coordinator.Observation = ProjectModel.Observation.Unobserved @>

[<Fact(Timeout = 15000)>]
let ``THE RACE: a discovery in flight reports rediscovering, never an empty model`` () =
    let coordinator = DiscoveryCoordinator()

    let started =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let inFlight =
        coordinator.Run(fun () ->
            async {
                started.TrySetResult() |> ignore
                do! release.Task |> Async.AwaitTask

                return
                    { Discovered = 3
                      Loaded = 3
                      OptionsMapped = 3
                      Registered = 3 },
                    ()
            })
        |> Async.StartAsTask

    started.Task.GetAwaiter().GetResult()

    // Mid-flight: `Completed` is None — the pre-fix reading that looks like an
    // empty model. The observation says WHY it is None.
    test <@ coordinator.Completed = None @>

    match coordinator.Observation with
    | ProjectModel.Observation.Rediscovering generation -> test <@ generation > 0L @>
    | other -> failwith $"expected Rediscovering while discovery was in flight, got %A{other}"

    release.TrySetResult() |> ignore
    inFlight.GetAwaiter().GetResult()

    match coordinator.Observation with
    | ProjectModel.Observation.Available snapshot -> test <@ snapshot.Counts.Registered = 3 @>
    | other -> failwith $"expected Available once discovery completed, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``a faulted discovery reports unobserved rather than a stale success`` () =
    let coordinator = DiscoveryCoordinator()

    coordinator.Run(fun () ->
        async {
            return
                { Discovered = 2
                  Loaded = 2
                  OptionsMapped = 2
                  Registered = 2 },
                ()
        })
    |> Async.RunSynchronously

    test <@ coordinator.Observation <> ProjectModel.Observation.Unobserved @>

    let faulting: Async<DiscoverySnapshot * unit> =
        async { return raise (InvalidOperationException "loader exploded") }

    Assert.Throws<InvalidOperationException>(fun () -> coordinator.Run(fun () -> faulting) |> Async.RunSynchronously)
    |> ignore

    test <@ coordinator.Observation = ProjectModel.Observation.Unobserved @>

[<Fact(Timeout = 15000)>]
let ``the coordinator announces rediscovery before it announces a settled model`` () =
    let announced =
        System.Collections.Concurrent.ConcurrentQueue<ProjectModel.Observation>()

    let coordinator = DiscoveryCoordinator(announced.Enqueue)

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

    let observed = announced.ToArray() |> List.ofArray

    match observed with
    | [ ProjectModel.Observation.Rediscovering _; ProjectModel.Observation.Available snapshot ] ->
        test <@ snapshot.Counts.Registered = 1 @>
    | other -> failwith $"expected rediscovering then available, got %A{other}"

let private discoveredOne: DiscoverySnapshot =
    { Discovered = 1
      Loaded = 1
      OptionsMapped = 1
      Registered = 1 }

[<Fact(Timeout = 15000)>]
let ``a cohort cannot capture a settled model before it is published`` () =
    use availableEntered = new ManualResetEventSlim(false)
    use releaseAvailable = new ManualResetEventSlim(false)

    let coordinator =
        DiscoveryCoordinator(fun observation ->
            match observation with
            | ProjectModel.Observation.Available _ ->
                availableEntered.Set()
                releaseAvailable.Wait()
            | _ -> ())

    let run =
        coordinator.Run(fun () -> async { return discoveredOne, () })
        |> Async.StartAsTask

    try
        Assert.True(availableEntered.Wait(TimeSpan.FromSeconds 5.0), "the attempt must announce its model")
        let captured = coordinator.Capture id |> Async.StartAsTask
        let first = Task.WhenAny(captured, Task.Delay 500).GetAwaiter().GetResult()
        // A cohort captured now could be dispatched before any plugin can observe the
        // model it was captured against.
        Assert.False(obj.ReferenceEquals(first, captured), "a capture ran before the model was published")
        releaseAvailable.Set()
        test <@ captured.GetAwaiter().GetResult() = (1L, Some discoveredOne) @>
    finally
        releaseAvailable.Set()
        run.GetAwaiter().GetResult()

[<Fact(Timeout = 15000)>]
let ``a captured model refuses publication once any later attempt begins or settles`` () =
    let coordinator = DiscoveryCoordinator()
    let succeed () = async { return discoveredOne, () }
    coordinator.Run succeed |> Async.RunSynchronously
    let captured = coordinator.Capture id |> Async.RunSynchronously
    test <@ modelGenerationOf captured = Some 1L @>
    test <@ coordinator.WithCurrent(captured, fun () -> 42) = 42 @>

    let refuses epoch =
        Assert.Throws<ModelSupersededException>(fun () -> coordinator.WithCurrent(epoch, ignore))
        |> ignore

    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let pending =
        coordinator.Run(fun () ->
            async {
                entered.Set()
                release.Wait()
                return discoveredOne, ()
            })
        |> Async.StartAsTask

    Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
    // Same counts, but an attempt is between clear and completion.
    refuses captured
    release.Set()
    pending.GetAwaiter().GetResult()
    // Identical counts from a later attempt are still a different model.
    refuses captured

    let failing: Async<DiscoverySnapshot * unit> =
        async { return raise (InvalidOperationException "loader exploded") }

    let beforeFailure = coordinator.Capture id |> Async.RunSynchronously

    Assert.Throws<InvalidOperationException>(fun () -> coordinator.Run(fun () -> failing) |> Async.RunSynchronously)
    |> ignore

    refuses beforeFailure
    // With no completed model the epoch carries no generation to stamp.
    let unobserved = coordinator.Capture id |> Async.RunSynchronously
    test <@ modelGenerationOf unobserved = None @>
    test <@ coordinator.WithCurrent(unobserved, fun () -> true) @>

[<Fact(Timeout = 90000)>]
let ``a change batch whose model changes on every attempt fails by name and keeps its source owed`` () =
    withTempDir "daemon-change-model-storm" (fun root ->
        let directory = Path.Combine(root, "src")
        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Probe.fsproj")
        let source = Path.Combine(directory, "Probe.fs")
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(source, "module Probe\nlet value = 1\n")
        let assets = FsHotWatch.DepsFreshness.assetsPath project
        Directory.CreateDirectory(Path.GetDirectoryName assets) |> ignore
        File.WriteAllText(assets, "{}")
        let checker = sharedChecker.Value

        let options =
            let scriptOptions, _ =
                checker.GetProjectOptionsFromScript(
                    source,
                    FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText source),
                    assumeDotNetFramework = false
                )
                |> Async.RunSynchronously

            { scriptOptions with
                ProjectFileName = project
                SourceFiles = [| source |] }

        let loaded =
            { minimalLoadedProject project with
                SourceFiles = [ source ] }

        let loader = SequencedWorkspaceLoader([ [ loaded ]; [ loaded ] ])
        loader.Resume(0)
        loader.Resume(1)
        let callback = ref None

        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                callback.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                root
                Daemon.DaemonOptions.defaults
                loader
                (fun _ -> [ options ])
                watcher

        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously
        let storming = ref true
        let attempts = ref 0

        // Every attempt of the storm replaces the model after the attempt captured it,
        // exactly as a rediscovery racing each retry would.
        daemon.RegisterPreprocessor(
            { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                member _.Name = "model-storm"

                member _.Process files _ =
                    if storming.Value then
                        Interlocked.Increment(&attempts.contents) |> ignore
                        daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously

                    Ok
                        { Modified = []
                          Considered = files.Length
                          Evidence = "storm barrier" }

                member _.Dispose() = () }
        )

        let results = System.Collections.Concurrent.ConcurrentQueue<FileCheckResult>()
        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "storm-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | FileChecked result -> results.Enqueue result
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChecked; SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        let deliver = callback.Value |> Option.get

        let stormFailure () =
            daemon.Host.WorkSnapshot.OperationFaults
            |> List.tryFind (fun (name, failure) ->
                name = "changes"
                && failure.Message.Contains("model kept changing during the batch"))

        try
            deliver (SourceChanged [ source ])

            Assert.True(
                SpinWait.SpinUntil((fun () -> (stormFailure ()).IsSome), TimeSpan.FromSeconds 30.0),
                "a batch superseded on every attempt must end in a named failed receipt"
            )

            Assert.True(
                SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 15.0),
                "the failed batch must retire its ownership"
            )

            let stormAttempts = attempts.Value
            Assert.True(stormAttempts > 1, "the batch must retry before it gives up")
            Assert.Empty(seals.ToArray())

            // The storm is over. The same bytes arrive again: the content tracker has
            // already seen them, so only the owed admission can still run the source.
            storming.Value <- false

            let generation =
                match daemon.ProjectModel() with
                | ProjectModel.Observation.Available model -> Some model.Generation
                | other -> failwith $"the storm must settle on an available model, got %A{other}"

            deliver (SourceChanged [ source ])

            Assert.True(
                SpinWait.SpinUntil((fun () -> not seals.IsEmpty), TimeSpan.FromSeconds 30.0),
                "the next stable model must run the owed source"
            )

            Assert.True(SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 15.0))

            let completed = Assert.Single(seals.ToArray())
            Assert.Equal(generation, completed.ModelGeneration)
            Assert.Contains(AbsFilePath.create source, completed.Files)
            Assert.Equal(stormAttempts, attempts.Value)
            Assert.True((stormFailure ()).IsNone, "a successful batch clears the storm failure")
        finally
            storming.Value <- false

            Assert.True(
                SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 30.0),
                "all change ownership must retire before fixture disposal"
            ))

[<Theory(Timeout = 45000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a scan seals its cohort exactly once, with files or without`` (hasSource: bool) =
    withTempDir "daemon-scan-seal-count" (fun root ->
        let directory = Path.Combine(root, "src")
        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Probe.fsproj")
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let source = Path.Combine(directory, "Probe.fs")
        let sources = if hasSource then [ source ] else []

        if hasSource then
            File.WriteAllText(source, "module Probe\nlet value = 1\n")

        let assets = FsHotWatch.DepsFreshness.assetsPath project
        Directory.CreateDirectory(Path.GetDirectoryName assets) |> ignore
        File.WriteAllText(assets, "{}")
        let checker = sharedChecker.Value

        let options =
            if hasSource then
                let scriptOptions, _ =
                    checker.GetProjectOptionsFromScript(
                        source,
                        FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText source),
                        assumeDotNetFramework = false
                    )
                    |> Async.RunSynchronously

                { scriptOptions with
                    ProjectFileName = project
                    SourceFiles = [| source |] }
            else
                makeProjectOptions project [] []

        let loaded =
            { minimalLoadedProject project with
                SourceFiles = sources }

        let loader = SequencedWorkspaceLoader([ [ loaded ]; [ loaded ] ])
        loader.Resume(0)
        loader.Resume(1)

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                root
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                loader
                (fun _ -> [ options ])
                (fun _ _ _ _ _ -> failwith "this scan fixture must not construct a watcher")

        let seals = System.Collections.Concurrent.ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "seal-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        daemon.ScanAll() |> Async.RunSynchronously

        Assert.True(
            SpinWait.SpinUntil((fun () -> not daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 15.0),
            "the scan's dispatch must retire before the seals are counted"
        )

        // One seal per scan: the answer about the model, whether or not any file was
        // dispatched. An empty model still gets one; a non-empty one does not get two.
        let sealed' = Assert.Single(seals.ToArray())
        Assert.Equal(sources.Length, sealed'.Files.Length)
        Assert.Equal(Some 1L, sealed'.ModelGeneration)

        daemon.ScanAll() |> Async.RunSynchronously

        Assert.True(
            SpinWait.SpinUntil((fun () -> seals.Count = 2), TimeSpan.FromSeconds 15.0),
            "a second scan seals its own cohort"
        ))

// ---------------------------------------------------------------------------
// The wedge detector reads OWNED work, not completed dispatches alone. A scan that is
// legitimately inside its supervisor deadline is working, however long it takes; one
// that is past it with nothing moving is the wedge the detector exists to name.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``a scan blocked in discovery beyond the stall threshold is not a wedge`` () =
    withTempDir "daemon-scan-not-wedged" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Blocked.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let loader = BlockingWorkspaceLoader([])

        use daemon =
            Daemon.createWithWorkspaceLoader
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                loader
                (fun _ -> [])

        let scan = Async.StartAsTask(daemon.ScanAll())

        try
            Assert.True(loader.Entered.Wait(TimeSpan.FromSeconds 10.0), "the scan must enter controlled discovery")

            // The scan owns work and no plugin dispatch is completing, which is exactly
            // the shape the old detector called WEDGED. It is inside its deadline, so the
            // wait must simply keep waiting: a timeout here, not a wedge.
            let waiting =
                waitForAllTerminalCore
                    daemon.Host
                    (TimeSpan.FromMilliseconds 900.0)
                    (TimeSpan.FromMilliseconds 150.0)
                    CancellationToken.None

            let failure =
                Assert.Throws<TimeoutException>(fun () -> waiting.GetAwaiter().GetResult())

            test <@ not (failure.Message.Contains "WEDGED") @>
            test <@ failure.Message.Contains "timed out" @>
        finally
            loader.Resume()
            scan.WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult())

[<Fact(Timeout = 60000)>]
let ``supervised work held past its own deadline still reads as wedged`` () =
    // The negative control for the test above: the bound is the supervisor's deadline, so
    // work that outlived it with nothing published is named, not waited on forever.
    let host =
        FsHotWatch.PluginHost.PluginHost.create nullChecker "/tmp/fshw-wedge-past-deadline"

    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let queue =
        SupervisedWork.Queue(
            host.WorkStore,
            "hung-operation",
            (),
            TimeSpan.FromMilliseconds 200.0,
            (fun state _ -> state),
            (fun state _ -> state),
            ignore,
            fun state (_: unit) _ _ ->
                async {
                    entered.Set()
                    release.Wait()
                    return state
                }
        )

    try
        queue.Submit((), CancellationToken.None) |> ignore
        Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "the controlled operation must start")

        let waiting =
            waitForAllTerminalCore
                host
                (TimeSpan.FromSeconds 20.0)
                (TimeSpan.FromMilliseconds 400.0)
                CancellationToken.None

        let failure =
            Assert.Throws<TimeoutException>(fun () -> waiting.GetAwaiter().GetResult())

        test <@ failure.Message.Contains "WEDGED" @>
        test <@ failure.Message.Contains "hung-operation" @>
    finally
        release.Set()

// ---------------------------------------------------------------------------
// Naming memory pressure when a daemon stops answering
// ---------------------------------------------------------------------------
//
// A daemon squeezed out by MACHINE memory pressure does not raise an
// out-of-memory error. It stops answering, or dies mid-scan, and the CLI reports
// the transport — "could not connect", "the daemon shut down" — which reads as an
// fshw bug and sends the reader to logs/daemon.log. Measured 2026-09-20 on a
// consumer repository: a 37 GB phys_footprint on a 32 GB box, on a tree grown
// from 745 files (last benchmarked at ~3 GB settled) to 2047. Four
// different-looking failures in one session had that single cause.

[<Fact>]
let ``no memory pressure adds nothing to a daemon-disconnect message`` () =
    // A genuine transport fault must not be given a memory story it does not
    // deserve — that would send the next reader chasing RAM for a broken pipe.
    test <@ IdleExit.disconnectPressureNote false = "" @>

[<Fact>]
let ``under memory pressure the disconnect message names the cause and the instrument`` () =
    let note = IdleExit.disconnectPressureNote true

    test <@ note <> "" @>

    // The three things a reader needs and cannot get from "could not connect":
    // that memory is the suspect, that fshw's footprint scales with the tree, and
    // which instrument answers (ps refuses the field; top's MEM is a different
    // number).
    test <@ note.Contains "memory pressure" @>
    test <@ note.Contains "scales with the size of the tree" @>
    test <@ note.Contains "footprint" @>

[<Fact>]
let ``the pressure note refuses to promise that a re-run helps`` () =
    // The surrounding message says "re-run `fshw check`". That advice is right for
    // a transport blip and wrong for a tree that does not fit, and following it is
    // how an afternoon disappears into identical failures. POSITIVE CONTROL for
    // the pairing: the no-pressure note must stay silent on re-running, so this
    // assertion cannot pass by the sentence being present unconditionally.
    test <@ (IdleExit.disconnectPressureNote true).Contains "a re-run will not help" @>
    test <@ not ((IdleExit.disconnectPressureNote false).Contains "re-run") @>

// ============================================================================
// Self-incompatible type diagnostics
//
// FCS can report a type mismatch whose two sides render to the SAME string:
//
//     This expression was expected to have type
//         'Intelligence.Domain.BriefEntryEditV3.Edit'
//     but here has type
//         'Intelligence.Domain.BriefEntryEditV3.Edit'
//
// One full `fshw confirm` produced 335 reddenedBy entries, 334 of this shape;
// `dotnet build` on the same tree produced 0 errors. Every one was phantom.
//
// These tests pin both directions. A self-identical render must never reach the
// ledger; a GENUINE mismatch ('int' vs 'string') must still redden. The second
// half is the one that matters most — a guard that swallows real errors is
// worse than the bug it was written for.
// ============================================================================

/// The real FS0001 text, with the multi-line layout and padding runs FCS emits.
let private expectedButHasMessage (expected: string) (actual: string) =
    $"This expression was expected to have type\n    '%s{expected}'    \nbut here has type\n    '%s{actual}'    "

let private fcsError (message: string) =
    FSharp.Compiler.Diagnostics.FSharpDiagnostic.Create(
        FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error,
        message,
        1,
        FSharp.Compiler.Text.Range.range0
    )

[<Fact(Timeout = 15000)>]
let ``a self-incompatible type diagnostic never becomes a ledger entry`` () =
    let diagnostic =
        fcsError (
            expectedButHasMessage
                "Intelligence.Domain.BriefEntryEditV3.Edit"
                "Intelligence.Domain.BriefEntryEditV3.Edit"
        )

    let reportable, selfIncompatible =
        Daemon.classifyFcsDiagnostics Set.empty [| diagnostic |]

    test <@ List.isEmpty reportable @>
    test <@ selfIncompatible |> List.map _.RenderedType = [ "Intelligence.Domain.BriefEntryEditV3.Edit" ] @>

[<Fact(Timeout = 15000)>]
let ``NEGATIVE CONTROL - a genuine int-vs-string mismatch still reddens`` () =
    let diagnostic = fcsError (expectedButHasMessage "int" "string")

    let reportable, selfIncompatible =
        Daemon.classifyFcsDiagnostics Set.empty [| diagnostic |]

    test <@ List.isEmpty selfIncompatible @>
    test <@ reportable |> List.map _.Severity = [ ErrorLedger.DiagnosticSeverity.Error ] @>

[<Fact(Timeout = 15000)>]
let ``NEGATIVE CONTROL - two same-named types from different assemblies still redden`` () =
    // Measured against FSharp.Compiler.Service 43.12.401: when two types share a
    // display name but come from different assemblies, the compiler appends the
    // assembly identity so a reader can tell them apart. That escalation is the
    // whole reason an IDENTICAL render is safe to treat as internal — so the
    // distinguished form must keep reddening.
    let diagnostic =
        fcsError (
            expectedButHasMessage
                "Dup.T (LibA, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null)"
                "Dup.T (LibB, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null)"
        )

    let reportable, selfIncompatible =
        Daemon.classifyFcsDiagnostics Set.empty [| diagnostic |]

    test <@ List.isEmpty selfIncompatible @>
    test <@ reportable |> List.length = 1 @>

[<Fact(Timeout = 15000)>]
let ``a self-incompatible diagnostic is dropped without taking real errors with it`` () =
    let phantom = fcsError (expectedButHasMessage "Foo.Bar" "Foo.Bar")
    let real = fcsError (expectedButHasMessage "int" "string")

    let reportable, selfIncompatible =
        Daemon.classifyFcsDiagnostics Set.empty [| phantom; real; phantom |]

    test <@ reportable |> List.length = 1 @>
    test <@ selfIncompatible |> List.length = 2 @>

[<Fact(Timeout = 15000)>]
let ``suppression wins over the self-incompatible classification`` () =
    // A code the operator already silenced must read as silenced, not as an
    // internal fault — otherwise the self-incompatible count stops being a
    // measure of how often FCS goes stale.
    let diagnostic = fcsError (expectedButHasMessage "Foo.Bar" "Foo.Bar")

    let reportable, selfIncompatible =
        Daemon.classifyFcsDiagnostics (Set.ofList [ 1 ]) [| diagnostic |]

    test <@ List.isEmpty reportable @>
    test <@ List.isEmpty selfIncompatible @>

// --- the predicate itself -------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``every two-type message family is recognised when both sides match`` () =
    let messages =
        [ "This expression was expected to have type\n    'A.T'    \nbut here has type\n    'A.T'    "
          "Type mismatch. Expecting a\n    'A.T'    \nbut given a\n    'A.T'    \n"
          "Type constraint mismatch. The type \n    'A.T'    \nis not compatible with type\n    'A.T'    \n" ]

    test <@ messages |> List.map trySelfIncompatibleType = [ Some "A.T"; Some "A.T"; Some "A.T" ] @>

[<Fact(Timeout = 15000)>]
let ``every two-type message family is left alone when the sides differ`` () =
    let messages =
        [ "This expression was expected to have type\n    'int'    \nbut here has type\n    'string'    "
          "Type mismatch. Expecting a\n    'int'    \nbut given a\n    'string'    \n"
          "Type constraint mismatch. The type \n    'int'    \nis not compatible with type\n    'string'    \n" ]

    test <@ messages |> List.forall (isSelfIncompatibleTypeMessage >> not) @>

[<Fact(Timeout = 15000)>]
let ``a trailing constraint explanation defeats the guard`` () =
    // `{2}` in the templates is where the compiler puts the distinguishing
    // information when the two rendered names alone do not carry it. If it is
    // filled, the compiler DID say what differs, so the diagnostic is real and
    // must be reported.
    let message =
        "This expression was expected to have type\n    ''a'    \nbut here has type\n    ''a'    The type ''a' does not match the type ''b'"

    test <@ isSelfIncompatibleTypeMessage message = false @>

[<Fact(Timeout = 15000)>]
let ``tuple-shaped mismatch families are not treated as two renderings of one question`` () =
    // `ErrorFromAddingTypeEquation1Tuple` compares a type against a TUPLE of a
    // type; an identical render there does not mean what it means for the
    // equation families, so the guard must not claim it.
    let message =
        "This expression was expected to have type\n    'int'    \nbut is a tuple of type\n    'int'    "

    test <@ isSelfIncompatibleTypeMessage message = false @>

[<Fact(Timeout = 15000)>]
let ``unrelated diagnostics are never claimed by the guard`` () =
    let messages =
        [ "The value or constructor 'foo' is not defined."
          "Incomplete pattern matches on this expression."
          ""
          "This expression was expected to have type"
          "Type mismatch. Expecting a\n    'int'    " ]

    test <@ messages |> List.forall (isSelfIncompatibleTypeMessage >> not) @>

[<Fact(Timeout = 15000)>]
let ``a type name that itself ends in a prime still round-trips`` () =
    // `type Foo' = ...` is legal F#, so a rendered type can end with the same
    // character the template quotes with. The split must not mistake that for a
    // trailing explanation.
    let message = expectedButHasMessage "Foo'" "Foo'"

    test <@ trySelfIncompatibleType message = Some "Foo'" @>

[<Fact(Timeout = 15000)>]
let ``the stale-project retry is bounded to one per project per cooldown`` () =
    let cooldown = TimeSpan.FromMinutes 5.0
    let t0 = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)

    let firstEver = shouldRecheckProject cooldown t0 None
    let insideCooldown = shouldRecheckProject cooldown (t0.AddMinutes 4.0) (Some t0)
    let atCooldown = shouldRecheckProject cooldown (t0.AddMinutes 5.0) (Some t0)

    test <@ firstEver @>
    test <@ insideCooldown = false @>
    test <@ atCooldown @>

// --- dropping stale checker state -----------------------------------------
//
// `recheckIfSelfIncompatible` is generic in the answer so the recovery can be driven
// without a compiler. FCS goes stale by accident, under load, in a warm
// long-lived daemon; nothing a test can write makes it happen on demand, so the
// alternative to a seam here is shipping the recovery path untested.

let private phantom = expectedButHasMessage "Foo.Bar" "Foo.Bar"
let private realMismatch = expectedButHasMessage "int" "string"

/// Drive `recheckIfSelfIncompatible` over lists of messages in a generation that has
/// not moved, recording whether the state was dropped. Returns (answer, drops).
let private runRecheck (budgetAllows: bool) (first: string list) (second: string list) =
    let retries = ref 0

    let decide () =
        if budgetAllows then
            incr retries
            RecheckOutcome.StateDropped
        else
            RecheckOutcome.BudgetSpent("Other.fs", DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))

    let answer, _ =
        recheckIfSelfIncompatible
            Seq.ofList
            isSelfIncompatibleTypeMessage
            decide
            (fun () -> async { return second })
            first
        |> Async.RunSynchronously

    answer, retries.Value

[<Fact(Timeout = 15000)>]
let ``a self-incompatible answer is thrown away and the second ask is returned`` () =
    let answer, retries = runRecheck true [ phantom ] [ realMismatch ]

    test <@ answer = [ realMismatch ] @>
    test <@ retries = 1 @>

[<Fact(Timeout = 15000)>]
let ``a clean answer is returned untouched and spends no budget`` () =
    let answer, retries =
        runRecheck true [ realMismatch ] [ "should never be asked for" ]

    test <@ answer = [ realMismatch ] @>
    test <@ retries = 0 @>

[<Fact(Timeout = 15000)>]
let ``an answer with no diagnostics at all spends no budget`` () =
    let answer, retries = runRecheck true [] [ "should never be asked for" ]

    test <@ List.isEmpty answer @>
    test <@ retries = 0 @>

[<Fact(Timeout = 15000)>]
let ``a spent budget keeps the first answer rather than asking again`` () =
    // The bound is the point: the incident produced 334 of these in one run, and
    // one invalidation clears the stale entity for every file in the project.
    let answer, retries = runRecheck false [ phantom ] [ realMismatch ]

    test <@ answer = [ phantom ] @>
    test <@ retries = 0 @>

[<Fact(Timeout = 15000)>]
let ``one phantom among real errors is enough to drop the checker's state`` () =
    let answer, retries = runRecheck true [ realMismatch; phantom ] [ realMismatch ]

    test <@ answer = [ realMismatch ] @>
    test <@ retries = 1 @>

[<Fact(Timeout = 15000)>]
let ``the budget is spent before the second ask, so a failing re-check cannot spend it twice`` () =
    let retries = ref 0

    let attempt () =
        recheckIfSelfIncompatible
            Seq.ofList
            isSelfIncompatibleTypeMessage
            (fun () ->
                incr retries
                RecheckOutcome.StateDropped)
            (fun () -> async { return failwith "re-check exploded" })
            [ phantom ]
        |> Async.RunSynchronously
        |> ignore

    raises<exn> <@ attempt () @>
    test <@ retries.Value = 1 @>

[<Fact(Timeout = 15000)>]
let ``a trailing explanation that does not end in a quote is still a real diagnostic`` () =
    // The `{2}` slot is free text. It may end mid-sentence, which leaves the
    // second fragment not ending at a closing quote — the unquote must refuse it
    // rather than swallow the explanation into the type name.
    let message =
        "This expression was expected to have type\n    'Foo.Bar'    \nbut here has type\n    'Foo.Bar'    The types have incompatible constraints"

    test <@ tryRenderedTypePair message = None @>
    test <@ isSelfIncompatibleTypeMessage message = false @>

[<Fact(Timeout = 15000)>]
let ``a family message whose slots are not quoted at all is refused`` () =
    // Nothing FCS emits looks like this, which is the point: the parse must
    // refuse what it does not recognise instead of guessing at the boundaries.
    let messages =
        [ "Type mismatch. Expecting a\n    int    \nbut given a\n    int    "
          "Type mismatch. Expecting a\n        \nbut given a\n        " ]

    test <@ messages |> List.map tryRenderedTypePair = [ None; None ] @>

[<Fact(Timeout = 15000)>]
let ``the internal fault a self-incompatible diagnostic becomes says whose fault it is, at a severity that cannot redden``
    ()
    =
    let fault: FcsDiagnosticFilter.SelfIncompatibleDiagnostic =
        { RenderedType = "Foo.Bar"
          Line = 12
          Column = 4 }

    let entries = selfIncompatibleLedgerEntries "Widgets.fsproj" [ fault ]

    test <@ entries |> List.map _.Severity = [ ErrorLedger.DiagnosticSeverity.Info ] @>
    test <@ entries |> List.forall (fun e -> ErrorLedger.ErrorEntry.isFailing true e |> not) @>
    test <@ entries |> List.map _.Line = [ 12 ] @>
    test <@ entries.Head.Message.Contains "NOT an error in your code" @>
    test <@ entries.Head.Message.Contains "Widgets.fsproj" @>
    test <@ List.isEmpty (selfIncompatibleLedgerEntries "Widgets.fsproj" []) @>

[<Fact(Timeout = 15000)>]
let ``the warn lines name the project, the file and the type`` () =
    let fault: FcsDiagnosticFilter.SelfIncompatibleDiagnostic =
        { RenderedType = "Foo.Bar"
          Line = 12
          Column = 4 }

    let logged =
        selfIncompatibleLogLines "Widgets.fsproj" "Thing.fs" [ fault ]
        |> List.exactlyOne

    test <@ List.isEmpty (selfIncompatibleLogLines "Widgets.fsproj" "Thing.fs" []) @>

    test
        <@
            logged.Contains "Widgets.fsproj"
            && logged.Contains "Thing.fs(12,4)"
            && logged.Contains "Foo.Bar"
        @>

    test <@ logged.Contains "not an error in your code" @>

    let retryLine = recheckLogLine "Widgets.fsproj" "Thing.fs"

    test <@ retryLine.Contains "Widgets.fsproj" && retryLine.Contains "Thing.fs" @>
    test <@ retryLine.Contains "re-checking once" @>

[<Fact(Timeout = 15000)>]
let ``a fragment too short to be a quoted type is refused`` () =
    // The unquote must reject a fragment that cannot carry both quotes, not
    // index past its end.
    let message = "Type mismatch. Expecting a x but given a 'int'"

    test <@ tryRenderedTypePair message = None @>

[<Fact(Timeout = 15000)>]
let ``the per-project budget allows one drop, then holds until the cooldown`` () =
    let t0 = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    let budget = FcsDiagnosticFilter.RecheckBudget(TimeSpan.FromMinutes 5.0)

    let drops = ref 0

    let decide project file (now: DateTime) =
        budget.Decide(project, file, now, 0L, (fun () -> 0L), (fun () -> incr drops))

    let first = decide "A.fsproj" "Admin.fs" t0
    let insideCooldown = decide "A.fsproj" "Jobs.fs" (t0.AddMinutes 4.0)
    let afterCooldown = decide "A.fsproj" "Jobs.fs" (t0.AddMinutes 5.0)
    // A spend on one project must not spend another's — each is invalidated on
    // its own, and a shared budget would leave the second one stale.
    let otherProject = decide "B.fsproj" "Other.fs" t0

    test <@ first = RecheckOutcome.StateDropped @>
    // A refusal names who spent the budget and when, for the log line that says so.
    test <@ insideCooldown = RecheckOutcome.BudgetSpent("Admin.fs", t0) @>
    test <@ afterCooldown = RecheckOutcome.StateDropped @>
    test <@ otherProject = RecheckOutcome.StateDropped @>
    test <@ drops.Value = 3 @>

[<Fact(Timeout = 15000)>]
let ``the internal fault states the observation and does not claim a cause`` () =
    // This guard exists because a cause WAS claimed once and retracted: a
    // `LoadTime`/`Stamp` defect reaching `AreSameForChecking`, measured to be on
    // a path TransparentCompiler never takes. The predicate never depended on
    // it — the argument is about what the compiler SAID — and the text a reader
    // sees must not re-acquire a mechanism nobody has established.
    let fault: FcsDiagnosticFilter.SelfIncompatibleDiagnostic =
        { RenderedType = "Foo.Bar"
          Line = 1
          Column = 1 }

    let shown =
        (selfIncompatibleLedgerEntries "W.fsproj" [ fault ] |> List.map _.Message)
        @ selfIncompatibleLogLines "W.fsproj" "T.fs" [ fault ]

    let claimsACause (text: string) =
        [ "FSharpEntity"
          "two entities"
          "assembly identity"
          "stale"
          "LoadTime"
          "Stamp" ]
        |> List.exists (fun claim -> text.Contains(claim, StringComparison.OrdinalIgnoreCase))

    test <@ List.isEmpty (shown |> List.filter claimsACause) @>
    // What it MUST say: not your fault, and that we do not know why.
    let says (phrase: string) (text: string) =
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase)

    test <@ shown |> List.forall (says "not an error in your code") @>
    test <@ shown |> List.exists (says "not yet known") @>

[<Fact>]
let ``the two rendered types are found when FCS separates them with GROUP SEPARATOR`` () =
    // Released as alpha.58 and MISSED 64 real occurrences on a consuming repo.
    //
    // The predicate collapsed runs of `\s` before looking for the connective
    // phrase. .NET's `\s` is `[\f\n\r\t\v\x85\p{Z}]` — whitespace and Unicode
    // SEPARATORS. FCS joins the two rendered types with GROUP SEPARATOR
    // (U+001D), a CONTROL character in category Cc, which `\s` does not match.
    // The separator survived, the padded ` but here has type ` lookup missed,
    // and every one of those diagnostics was reported as an ordinary code error
    // on a tree whose 32,557 tests passed.
    //
    // Pinned with the real separator rather than a newline, because a newline
    // is exactly what made the original tests pass while the shipped guard did
    // not work.
    let gs = string (char 0x1D)

    let separatedBy (sep: string) (opening: string) (connective: string) (left: string) (right: string) =
        opening
        + sep
        + "    '"
        + left
        + "'    "
        + sep
        + connective
        + sep
        + "    '"
        + right
        + "'    "

    let selfIncompatible =
        separatedBy
            gs
            "This expression was expected to have type"
            "but here has type"
            "Intelligence.Lib.BriefPipelineDsl.Stage.StageContext"
            "Intelligence.Lib.BriefPipelineDsl.Stage.StageContext"

    test
        <@
            tryRenderedTypePair selfIncompatible = Some(
                "Intelligence.Lib.BriefPipelineDsl.Stage.StageContext",
                "Intelligence.Lib.BriefPipelineDsl.Stage.StageContext"
            )
        @>

    // NEGATIVE CONTROL: a genuine mismatch carried by the same separator must
    // still yield two DIFFERENT names, so the guard cannot swallow a real error.
    let genuine =
        separatedBy gs "This expression was expected to have type" "but here has type" "int" "string"

    test <@ tryRenderedTypePair genuine = Some("int", "string") @>

    // The other two families carry the same separator and must parse too.
    let constraintMismatch =
        separatedBy gs "Type constraint mismatch. The type" "is not compatible with type" "A.T" "A.T"

    test <@ tryRenderedTypePair constraintMismatch = Some("A.T", "A.T") @>

    let expectingA =
        separatedBy gs "Type mismatch. Expecting a" "but given a" "A.T" "A.T"

    test <@ tryRenderedTypePair expectingA = Some("A.T", "A.T") @>

[<Fact(Timeout = 5000)>]
let ``projectChangeLine names the write, and says when it was a restore`` () =
    let line =
        Daemon.projectChangeLine "/repo" [ "/repo/src/App/obj/project.assets.json"; "/repo/src/Lib/Lib.fsproj" ]

    test
        <@
            line = Some
                "project input content changed: 2 [src/App/obj/project.assets.json (restore rewrote the package graph), src/Lib/Lib.fsproj]"
        @>

[<Fact(Timeout = 5000)>]
let ``projectChangeLine says nothing when no project input changed`` () =
    test <@ Daemon.projectChangeLine "/repo" [] = None @>

/// A scan request admitted while another scan has not yet read the tree is answered by
/// that scan: every file it checks is read after the request arrived. `fshw check`
/// against a daemon still in its cold scan sends exactly that request, and running it
/// as a second full scan re-checked every registered file for nothing.
[<Fact(Timeout = 30000)>]
let ``a scan requested before the running scan read the tree is answered by it`` () =
    withTempDir "scan-coalesce" (fun tmpDir ->
        let sourceDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(sourceDir) |> ignore
        let projectPath = Path.Combine(sourceDir, "Probe.fsproj")
        File.WriteAllText(projectPath, "<Project />")
        let loaded = minimalLoadedProject projectPath
        let loader = SequencedWorkspaceLoader([ [ loaded ] ])

        use daemon =
            Daemon.createWithWorkspaceLoader
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                loader
                (fun projects ->
                    projects
                    |> List.map (fun project -> makeProjectOptions project.ProjectFileName [] []))

        let first = Async.StartAsTask(daemon.ScanAll())
        test <@ loader.Entered(0).Wait(TimeSpan.FromSeconds 10.0) @>

        // Admitted while the first scan is still discovering — before it read any file.
        let second =
            daemon.AdmitScan().WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

        loader.Resume(0)
        first.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()
        second.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

        test <@ daemon.GetScanGeneration() = 1L @>

        // Control: a request admitted AFTER a scan read the tree is a real scan.
        daemon.ScanAll() |> Async.RunSynchronously
        test <@ daemon.GetScanGeneration() = 2L @>)
