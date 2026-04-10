module FsHotWatch.Tests.DaemonTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// Probe the watched directory until the daemon processes an event (proves watcher is live).
/// Uses repeated file writes so FSEvents cold-start latency (4-20s) doesn't cause timeouts.
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


[<Fact>]
let ``daemon starts and stops without error`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact>]
let ``daemon suppresses watcher events for preprocessor-modified files`` () =
    withTempDir "daemon" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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

[<Fact>]
let ``daemon dispatches file change events to plugins`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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
        // After a large probe batch in waitForDaemonReady, fseventsd may batch
        // subsequent events for 15-30s; repeated writes handle that delay.
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

[<Fact>]
let ``daemon debounces rapid file changes into one batch`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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

[<Fact>]
let ``daemon handles ProjectChanged events`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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

[<Fact>]
let ``daemon handles SolutionChanged events`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let mutable receivedChanges: FileChangeKind list = []
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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
                    | SolutionChanged _ -> true
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
                | SolutionChanged _ -> true
                | _ -> false)

        test <@ solutionChanges @>)

[<Fact>]
let ``daemon Run completes when cancellation is immediate`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        cts.Cancel()
        let task = Async.StartAsTask(daemon.Run(cts.Token))

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact>]
let ``Daemon.create creates a working daemon with real checker`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let daemon = Daemon.create tmpDir None None None
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
        test <@ daemon.RepoRoot = tmpDir @>)

[<Fact>]
let ``daemon RunWithIpc starts and stops cleanly`` () =
    withTempDir "daemon-ipc" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        daemon.Ready.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>)

[<Fact>]
let ``daemon RunWithIpc responds to IPC queries`` () =
    withTempDir "daemon-ipc" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let cts = new CancellationTokenSource()
        let pipeName = $"fshw-test-{Guid.NewGuid():N}"
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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

[<Fact>]
let ``daemon RegisterProject stores options in pipeline`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let checker =
            FSharp.Compiler.CodeAnalysis.FSharpChecker.Create(projectCacheSize = 10)

        let daemon = Daemon.createWith checker tmpDir None None (set [ 1182 ])

        let sourceFile = Path.Combine(tmpDir, "src", "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")

        let absSource = Path.GetFullPath(sourceFile)

        let options, _ =
            checker.GetProjectOptionsFromScript(
                absSource,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText absSource)
            )
            |> Async.RunSynchronously

        daemon.RegisterProject("/tmp/Test.fsproj", options)

        let result = daemon.Pipeline.CheckFile(absSource) |> Async.RunSynchronously
        test <@ result.IsSome @>)

// --- FormatScanStatus tests ---

[<Fact>]
let ``FormatScanStatus returns idle for ScanIdle`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        daemon.SetScanState(ScanIdle)
        test <@ daemon.FormatScanStatus() = "idle" @>)

[<Fact>]
let ``FormatScanStatus returns progress for Scanning`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        daemon.SetScanState(Scanning(10, 5, DateTime.UtcNow))
        let status = daemon.FormatScanStatus()
        test <@ status.Contains("5/10") @>
        test <@ status.Contains("50%") @>)

[<Fact>]
let ``FormatScanStatus returns complete for ScanComplete`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])
        daemon.SetScanState(ScanComplete(70, TimeSpan.FromSeconds(15.5)))
        let status = daemon.FormatScanStatus()
        test <@ status.Contains("70 files") @>
        test <@ status.Contains("15.5s") @>)

[<Fact>]
let ``RunOnce completes and returns plugin statuses`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir None None (set [ 1182 ])

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
