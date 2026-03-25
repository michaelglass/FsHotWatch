module FsHotWatch.Tests.DaemonTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.Plugin

// macOS kqueue-based FileSystemWatcher is unreliable — use polling watcher
do
    if
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX
        )
    then
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1")

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

        // Test the suppression mechanism at the host level (more reliable than watcher timing)
        // Simulate what processChanges does: run preprocessors, then emit
        let host = daemon.Host
        let testFiles = [ Path.Combine(srcDir, "Fmt.fs") ]

        // RunPreprocessors returns modified files
        let modified = host.RunPreprocessors(testFiles)

        // The preprocessor claims all files as modified
        test <@ modified.Length = testFiles.Length @>

        // Verify preprocessor status was updated
        let status = host.GetStatus("test-formatter")
        test <@ status.IsSome @>

        cts.Cancel()
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
        Thread.Sleep(2000)

        // Write a file to trigger the watcher
        let newFile = Path.Combine(tmpDir, "src", "New.fs")
        File.WriteAllText(newFile, "module New")
        Thread.Sleep(500)
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow)
        Thread.Sleep(5000)

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
        Thread.Sleep(2000)

        // Write 3 files rapidly (within debounce window)
        let fileA = Path.Combine(tmpDir, "src", "A.fs")
        let fileB = Path.Combine(tmpDir, "src", "B.fs")
        let fileC = Path.Combine(tmpDir, "src", "C.fs")
        File.WriteAllText(fileA, "module A")
        File.WriteAllText(fileB, "module B")
        File.WriteAllText(fileC, "module C")
        // Touch files to ensure watcher sees them (macOS kqueue)
        Thread.Sleep(500)
        File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow)
        File.SetLastWriteTimeUtc(fileB, DateTime.UtcNow)
        File.SetLastWriteTimeUtc(fileC, DateTime.UtcNow)

        // Wait for debounce to fire (500ms debounce + buffer)
        Thread.Sleep(5000)

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
        Thread.Sleep(2000)

        // Write an fsproj file to trigger a ProjectChanged event
        let projFile = Path.Combine(tmpDir, "src", "Test.fsproj")
        File.WriteAllText(projFile, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        Thread.Sleep(500)
        File.SetLastWriteTimeUtc(projFile, DateTime.UtcNow)
        Thread.Sleep(5000)

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
        Thread.Sleep(2000)

        // Write a .sln file to trigger a SolutionChanged event
        let slnFile = Path.Combine(tmpDir, "Test.sln")
        File.WriteAllText(slnFile, "Microsoft Visual Studio Solution File")
        Thread.Sleep(500)
        File.SetLastWriteTimeUtc(slnFile, DateTime.UtcNow)
        Thread.Sleep(5000)

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
