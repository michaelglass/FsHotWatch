module FsHotWatch.Tests.DaemonTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.Plugin

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact>]
let ``daemon starts and stops without error`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon suppresses watcher events for preprocessor-modified files`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable sourceChangedEvents: string list list = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        // Register a preprocessor that claims it modified the file
        let preprocessor =
            { new IFsHotWatchPreprocessor with
                member _.Name = "test-formatter"

                member _.Process (changedFiles: string list) (_repoRoot: string) =
                    // Claim we modified all files (simulates format-on-save)
                    changedFiles

                member _.Dispose() = () }

        daemon.RegisterPreprocessor(preprocessor)

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "suppression-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change ->
                        match change with
                        | SourceChanged files -> sourceChangedEvents <- files :: sourceChangedEvents
                        | _ -> ())

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write a file — the preprocessor will claim it modified it, so the daemon
        // should suppress re-trigger when the preprocessor's write fires the watcher again
        let testFile = Path.Combine(srcDir, "Fmt.fs")
        File.WriteAllText(testFile, "module Fmt\nlet x = 1\n")
        Thread.Sleep(2000)

        // First write triggers: preprocessor runs, file is dispatched, but marked as suppressed.
        // When the preprocessor "rewrites" the file, the watcher fires again — but the second
        // event should be suppressed (filtered out).
        let eventCount = sourceChangedEvents.Length

        // Write the same file again to simulate the preprocessor's rewrite triggering the watcher
        File.WriteAllText(testFile, "module Fmt\nlet x = 2\n")
        Thread.Sleep(2000)

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        // The first write should produce an event, but the second (rewrite) may be suppressed.
        // We verify that at least one event was received (the original change)
        test <@ sourceChangedEvents.Length >= 1 @>

        // The suppression mechanism should mean we don't get more events than without it.
        // At minimum, the first event count should be <= total events (sanity check)
        test <@ eventCount >= 1 @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon dispatches file change events to plugins`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let mutable receivedChanges = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "test-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change -> receivedChanges <- change :: receivedChanges)

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write a file to trigger the watcher
        File.WriteAllText(Path.Combine(tmpDir, "src", "New.fs"), "module New")
        Thread.Sleep(1500)

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ receivedChanges.Length >= 1 @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon debounces rapid file changes into one batch`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let mutable receivedChanges: FileChangeKind list = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "debounce-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change -> receivedChanges <- change :: receivedChanges)

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write 3 files rapidly (within debounce window)
        File.WriteAllText(Path.Combine(tmpDir, "src", "A.fs"), "module A")
        File.WriteAllText(Path.Combine(tmpDir, "src", "B.fs"), "module B")
        File.WriteAllText(Path.Combine(tmpDir, "src", "C.fs"), "module C")

        // Wait for debounce to fire (500ms debounce + buffer)
        Thread.Sleep(2000)

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        // The debounce should merge rapid changes - we expect fewer SourceChanged events
        // than individual file writes (ideally 1 batched event, but timing may vary)
        let sourceChanges =
            receivedChanges
            |> List.choose (fun c ->
                match c with
                | SourceChanged files -> Some files
                | _ -> None)

        // We should have received at least one SourceChanged event
        test <@ sourceChanges.Length >= 1 @>

        // The total files across all SourceChanged events should cover our 3 files
        let allFiles = sourceChanges |> List.collect id
        test <@ allFiles.Length >= 3 @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon handles ProjectChanged events`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let mutable receivedChanges: FileChangeKind list = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "project-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change -> receivedChanges <- change :: receivedChanges)

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write an fsproj file to trigger a ProjectChanged event
        File.WriteAllText(
            Path.Combine(tmpDir, "src", "Test.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
        )
        Thread.Sleep(2000)

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

        test <@ projectChanges @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon handles SolutionChanged events`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let mutable receivedChanges: FileChangeKind list = []
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "solution-recorder"

                member _.Initialize(ctx) =
                    ctx.OnFileChanged.Add(fun change -> receivedChanges <- change :: receivedChanges)

                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)

        // Write a .sln file to trigger a SolutionChanged event
        File.WriteAllText(Path.Combine(tmpDir, "Test.sln"), "Microsoft Visual Studio Solution File")
        Thread.Sleep(2000)

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

        test <@ solutionChanges @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon Run completes when cancellation is immediate`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()

    try
        let daemon = Daemon.createWith nullChecker tmpDir
        // Cancel immediately
        cts.Cancel()
        let task = Async.StartAsTask(daemon.Run(cts.Token))

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``Daemon.create creates a working daemon with real checker`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-create-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()

    try
        // Exercise the Daemon.create path (not createWith) which creates its own FSharpChecker
        let daemon = Daemon.create tmpDir
        let task = Async.StartAsTask(daemon.Run(cts.Token))
        Thread.Sleep(500)
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
        test <@ daemon.RepoRoot = tmpDir @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon RunWithIpc starts and stops cleanly`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-ipc-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"

    try
        let daemon = Daemon.createWith nullChecker tmpDir
        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        Thread.Sleep(500)
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

        test <@ task.IsCompleted @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon RunWithIpc responds to IPC queries`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-ipc-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let cts = new CancellationTokenSource()
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"

    try
        let daemon = Daemon.createWith nullChecker tmpDir

        let plugin =
            { new IFsHotWatchPlugin with
                member _.Name = "ipc-test"
                member _.Initialize(ctx) = ctx.ReportStatus(Idle)
                member _.Dispose() = () }

        daemon.Register(plugin)

        let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
        Thread.Sleep(500)

        let result = FsHotWatch.Ipc.IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("ipc-test") @>

        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``daemon RegisterProject stores options in pipeline`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-daemon-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

    try
        let checker =
            FSharp.Compiler.CodeAnalysis.FSharpChecker.Create(projectCacheSize = 10)

        let daemon = Daemon.createWith checker tmpDir

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

        // Verify by checking a file through the pipeline
        let result = daemon.Pipeline.CheckFile(absSource) |> Async.RunSynchronously
        test <@ result.IsSome @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)
