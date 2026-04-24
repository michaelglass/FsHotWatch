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

// ============================================================================
// parseNowarnCodes tests
// Workaround for https://github.com/dotnet/fsharp/issues/9796 —
// FCS TransparentCompiler ignores #nowarn directives for warnaserror codes.
// When that issue is resolved, parseNowarnCodes and these tests can be removed.
// ============================================================================

[<Fact(Timeout = 5000)>]
let ``parseNowarnCodes extracts single nowarn code`` () =
    let source =
        """#nowarn "3536"
module Foo
let x = 1"""

    test <@ parseNowarnCodes source = Set.ofList [ 3536 ] @>

[<Fact(Timeout = 5000)>]
let ``parseNowarnCodes extracts multiple nowarn directives`` () =
    let source =
        """#nowarn "1182"
#nowarn "3536"
module Foo"""

    test <@ parseNowarnCodes source = Set.ofList [ 1182; 3536 ] @>

[<Fact(Timeout = 5000)>]
let ``parseNowarnCodes returns empty set when no directives`` () =
    let source =
        """module Foo
let x = 1"""

    test <@ parseNowarnCodes source = Set.empty @>

[<Fact(Timeout = 5000)>]
let ``parseNowarnCodes ignores non-numeric nowarn`` () =
    let source =
        """#nowarn "notanumber"
module Foo"""

    test <@ parseNowarnCodes source = Set.empty @>

[<Fact(Timeout = 5000)>]
let ``parseNowarnCodes handles multiple codes on one line`` () =
    let source =
        """#nowarn "1182" "3536"
module Foo"""

    test <@ parseNowarnCodes source = Set.ofList [ 1182; 3536 ] @>

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


[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 10000)>]
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

[<Fact(Timeout = 5000)>]
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
        test <@ daemon.Pipeline.GetAllRegisteredFiles() |> List.contains absSource @>)

// --- FormatScanStatus tests ---

[<Fact(Timeout = 5000)>]
let ``FormatScanStatus returns idle for ScanIdle`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        daemon.SetScanState(ScanIdle)
        test <@ daemon.FormatScanStatus() = "idle" @>)

[<Fact(Timeout = 5000)>]
let ``FormatScanStatus returns progress for Scanning`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        daemon.SetScanState(Scanning(10, 5, DateTime.UtcNow))
        let status = daemon.FormatScanStatus()
        test <@ status.Contains("5/10") @>
        test <@ status.Contains("50%") @>)

[<Fact(Timeout = 5000)>]
let ``FormatScanStatus returns complete for ScanComplete`` () =
    withTempDir "daemon" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        daemon.SetScanState(ScanComplete(70, TimeSpan.FromSeconds(15.5)))
        let status = daemon.FormatScanStatus()
        test <@ status.Contains("70 files") @>
        test <@ status.Contains("15.5s") @>)

[<Fact(Timeout = 10000)>]
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

// ============================================================================
// isTruthyEnv tests
// ============================================================================

let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable(name)

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv returns false when unset`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    test <@ isTruthyEnv var = false @>

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv returns false for empty string`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv returns false for 0`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "0") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv returns false for false case-insensitive`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "FaLsE") (fun () -> test <@ isTruthyEnv var = false @>)

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv returns true for 1`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "1") (fun () -> test <@ isTruthyEnv var = true @>)

[<Fact(Timeout = 1000)>]
let ``isTruthyEnv trims whitespace`` () =
    let var = "FSHW_TEST_TRUTHY_" + string (Guid.NewGuid())
    withEnv var (Some "  true  ") (fun () -> test <@ isTruthyEnv var = true @>)

// ============================================================================
// countReferences tests
// ============================================================================

[<Fact(Timeout = 1000)>]
let ``countReferences counts -r: prefixes`` () =
    let opts = [| "-r:/a.dll"; "--nowarn:42"; "-r:/b.dll"; "-r:/c.dll" |]
    test <@ countReferences opts = 3 @>

[<Fact(Timeout = 1000)>]
let ``countReferences returns 0 when no references`` () =
    test <@ countReferences [| "--nowarn:42" |] = 0 @>
    test <@ countReferences [||] = 0 @>

// ============================================================================
// dumpProjectOptions tests
// ============================================================================

[<Fact(Timeout = 1000)>]
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

[<Fact(Timeout = 1000)>]
let ``dumpProjectOptions handles empty options`` () =
    withTempDir "dump-opts" (fun tmp ->
        let opts = makeProjectOptions "/tmp/Empty.fsproj" [] []
        dumpProjectOptions tmp opts
        test <@ File.Exists(Path.Combine(tmp, "Empty.opts.txt")) @>)

[<Fact(Timeout = 1000)>]
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
