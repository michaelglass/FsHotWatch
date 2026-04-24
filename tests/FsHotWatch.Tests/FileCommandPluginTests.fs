module FsHotWatch.Tests.FileCommandPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.FileCommand.FileCommandPlugin
open FsHotWatch.Tests.TestHelpers

let private fileTrigger (filter: string -> bool) : CommandTrigger =
    { FilePattern = Some filter
      AfterTests = None }

/// Emit a TestRunCompleted event with the given final Results. Used by tests
/// that want to drive the plugin as if a test run had just finished.
let private emitRunCompleted (host: PluginHost) (results: (string * TestResult) list) =
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          RanFullSuite = true }

/// Emit a TestProgress event (delta for one group) with the given RunId. Used
/// by tests that want to simulate the in-progress phase of a run.
let private emitProgress (host: PluginHost) (runId: System.Guid) (delta: (string * TestResult) list) =
    host.EmitTestProgress
        { RunId = runId
          NewResults = Map.ofList delta }

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            None
            None

    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "run-scripts" @>

[<Fact(Timeout = 5000)>]
let ``command runs when matching files change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])

    waitUntil
        (fun () ->
            match host.GetStatus("run-scripts") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``command does not run for non-matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // No matching files — wait briefly for agent to process the no-op
    waitUntil
        (fun () ->
            match host.GetStatus("run-scripts") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000
    |> ignore

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 5000)>]
let ``command captures stdout output`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "captured-output"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "anything.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("echo-test") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let result = host.RunCommand("echo-test-status", [||]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("true") @>

[<Fact(Timeout = 5000)>]
let ``command with environment variables`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "env-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "env-test-output"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("env-test") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("env-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``command runs on ProjectChanged with matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "proj-watcher")
            (fileTrigger (fun f -> f.EndsWith(".fsproj")))
            "echo"
            "project changed"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitUntil
        (fun () ->
            match host.GetStatus("proj-watcher") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("proj-watcher")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``command ignores SolutionChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "sln-watcher")
            (fileTrigger (fun _ -> true))
            "echo"
            "hello"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged "test.sln")

    // SolutionChanged is ignored — poll briefly; will time out at Idle (expected)
    waitUntil
        (fun () ->
            match host.GetStatus("sln-watcher") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000

    let status = host.GetStatus("sln-watcher")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 5000)>]
let ``command reports Failed status on command failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-cmd")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fail-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("fail-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``FileCommandPlugin honors timeoutSec and records TimedOut`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "slow-cmd")
            (fileTrigger (fun _ -> true))
            "sleep"
            "10"
            None
            (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("slow-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        8000

    let history = host.GetHistory("slow-cmd")
    test <@ not history.IsEmpty @>
    let last = List.last history

    test
        <@
            match last.Outcome with
            | TimedOut _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 5000)>]
let ``command reports Failed status on exception`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "bad-cmd")
            (fileTrigger (fun _ -> true))
            "this-command-does-not-exist-xyz"
            ""
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("bad-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("bad-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 10000)>]
let ``status command returns not run when no files matched`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "no-match")
            (fileTrigger (fun _ -> false))
            "echo"
            "hello"
            None
            None

    host.RegisterHandler(handler)

    // No files match, so command never runs
    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    // No matching files — poll briefly; will time out at Idle (expected)
    waitUntil
        (fun () ->
            match host.GetStatus("no-match") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000

    let result = host.RunCommand("no-match-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 5000)>]
let ``status command returns false when command failed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-status")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fail-status") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let result = host.RunCommand("fail-status-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("false") @>

[<Fact(Timeout = 5000)>]
let ``emits CommandCompleted on success`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-cmd")
            (fileTrigger (fun _ -> true))
            "echo"
            "hello"
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match getCommand () with
            | Some _ -> true
            | None -> false)
        5000

    let cmd = getCommand ()
    test <@ cmd.IsSome @>
    test <@ cmd.Value.Name = "echo-cmd" @>

    test
        <@
            match cmd.Value.Outcome with
            | FsHotWatch.Events.CommandSucceeded output -> output.Contains("hello")
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``emits CommandCompleted on failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-cmd-emit")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match getCommand () with
            | Some _ -> true
            | None -> false)
        5000

    let cmd = getCommand ()
    test <@ cmd.IsSome @>
    test <@ cmd.Value.Name = "fail-cmd-emit" @>

    test
        <@
            match cmd.Value.Outcome with
            | FsHotWatch.Events.CommandFailed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``afterTests TestProjects fires when ALL listed projects have results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-listed") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false)
          "B", FsHotWatch.Events.TestsPassed("", false)
          "Other", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil
        (fun () ->
            match host.GetStatus("afterTests-listed") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("afterTests-listed")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``afterTests TestProjects does not fire when only some listed projects have completed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-partial") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    // Only A has completed — B is still outstanding. Model as mid-run progress.
    emitProgress host (System.Guid.NewGuid()) [ "A", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil
        (fun () ->
            match host.GetStatus("afterTests-partial") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000
    |> ignore

    let status = host.GetStatus("afterTests-partial")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 10000)>]
let ``afterTests TestProjects does not fire when no listed project matches`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "Intelligence.Tests.Unit" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-miss") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    emitRunCompleted host [ "Intelligence.Tests.Integration", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil
        (fun () ->
            match host.GetStatus("afterTests-miss") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000
    |> ignore

    let status = host.GetStatus("afterTests-miss")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 10000)>]
let ``afterTests TestProjects fires exactly once across progressive deltas`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCount, counter) = commandCounter "afterTests-once"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-once") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    let runId = System.Guid.NewGuid()

    // Delta 1: {A} arrives — accumulator = {A}, filter not satisfied.
    emitProgress host runId [ "A", FsHotWatch.Events.TestsPassed("", false) ]

    // Delta 2: {B} arrives — accumulator = {A,B}, filter satisfied; fire.
    emitProgress host runId [ "B", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil (fun () -> getCount () >= 1) 5000

    // Delta 3: {C} arrives — accumulator = {A,B,C}, filter still satisfies
    //           but this is the same RunId → dedupe, no re-fire.
    emitProgress host runId [ "C", FsHotWatch.Events.TestsPassed("", false) ]

    System.Threading.Thread.Sleep(500)

    test <@ getCount () = 1 @>

[<Fact(Timeout = 10000)>]
let ``afterTests TestProjects fires again on a fresh batch`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCount, counter) = commandCounter "afterTests-rebatch"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-rebatch") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    // Batch 1 — plugin fires once when both projects complete.
    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false)
          "B", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil (fun () -> getCount () >= 1) 5000

    // Batch 2 — NEW RunId. Plugin's idempotency sentinel is tied to the
    // previous RunId, so this fresh event must fire again.
    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false)
          "B", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil (fun () -> getCount () >= 2) 5000
    test <@ getCount () = 2 @>

// Regression: end-to-end from parseConfig(.fs-hot-watch.json) → daemon registration
// path → lifecycle dispatch. The earlier unit tests built the CommandTrigger
// inline and hit the plugin the same way the daemon's RegisterHandler path does,
// but a bug in the config→trigger glue (e.g. parser yielding AfterTests = None
// for a valid JSON list) would not be caught without going through parseConfig.
[<Fact(Timeout = 5000)>]
let ``parseConfig + registration + TestRunCompleted fires coverage-ratchet-style plugin`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let json =
        """{"fileCommands": [{"name": "cov-r", "afterTests": ["ProjA"], "command": "echo", "args": "ok"}]}"""

    let defaults: FsHotWatch.Cli.DaemonConfig.DaemonConfiguration =
        { Build = None
          Format = FsHotWatch.Cli.DaemonConfig.Off
          Lint = false
          Cache = FsHotWatch.Cli.DaemonConfig.NoCache
          Analyzers = None
          Tests = None
          FileCommands = []
          Exclude = []
          LogDir = "logs"
          TimeoutSec = None }

    let config = FsHotWatch.Cli.DaemonConfig.parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    let fc = config.FileCommands.[0]
    test <@ fc.PluginName = "cov-r" @>
    test <@ fc.AfterTests.IsSome @>

    // Mirror exactly what DaemonConfig.registerPlugins does for each fileCommand.
    let trigger: CommandTrigger =
        { FilePattern =
            fc.Pattern
            |> Option.map (fun p ->
                let suffix = p.TrimStart('*')
                fun (path: string) -> path.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
          AfterTests = fc.AfterTests }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create fc.PluginName) trigger fc.Command fc.Args None None

    // The plugin must subscribe to TestProgress + TestRunCompleted — if this
    // assertion fails, dispatch will never route events to Update.
    test
        <@
            handler.Subscriptions
            |> Set.contains FsHotWatch.PluginFramework.SubscribeTestProgress
        @>

    test
        <@
            handler.Subscriptions
            |> Set.contains FsHotWatch.PluginFramework.SubscribeTestRunCompleted
        @>

    host.RegisterHandler(handler)

    // Simulate TestPrune's progressive emission: a partial delta that does NOT
    // include the afterTests-listed project, followed by a TestRunCompleted
    // whose Results do. The plugin must stay Idle after the partial and only
    // fire on the completed event.
    let runId = System.Guid.NewGuid()
    emitProgress host runId [ "Other", FsHotWatch.Events.TestsPassed("", false) ]

    // Brief pause to make sure the partial was processed.
    System.Threading.Thread.Sleep(200)
    test <@ host.GetStatus("cov-r") = Some Idle @>

    host.EmitTestRunCompleted
        { RunId = runId
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results =
            Map.ofList
                [ "Other", FsHotWatch.Events.TestsPassed("", false)
                  "ProjA", FsHotWatch.Events.TestsPassed("", false) ]
          RanFullSuite = true }

    waitUntil
        (fun () ->
            match host.GetStatus("cov-r") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("cov-r")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``afterTests AnyTest fires on TestRunCompleted regardless of projects`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-any") trigger "echo" "ran" None None

    host.RegisterHandler(handler)

    emitRunCompleted host [ "AnyProject", FsHotWatch.Events.TestsPassed("", false) ]

    waitUntil
        (fun () ->
            match host.GetStatus("afterTests-any") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("afterTests-any")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

// --- Combined trigger: pattern + afterTests ---

[<Fact(Timeout = 10000)>]
let ``plugin with both pattern and afterTests fires on file change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-a") trigger "echo" "hi" None None

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
    waitForTerminalStatus host "combined-a" 5000

    test
        <@
            match host.GetStatus("combined-a") with
            | Some(Completed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``plugin with both pattern and afterTests fires on test completion`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-b") trigger "echo" "hi" None None

    host.RegisterHandler(handler)
    emitRunCompleted host [ "proj-a", TestsPassed("ok", false) ]
    waitForTerminalStatus host "combined-b" 5000

    test
        <@
            match host.GetStatus("combined-b") with
            | Some(Completed _) -> true
            | _ -> false
        @>

// --- FSHW_RAN_FULL_SUITE environment variable ---

let private emitRunCompletedWithRanFullSuite
    (host: PluginHost)
    (results: (string * TestResult) list)
    (ranFullSuite: bool)
    =
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          RanFullSuite = ranFullSuite }

/// Writes a probe script to `dir` that echoes `$FSHW_RAN_FULL_SUITE` into
/// `outFile`. Returns the script path. The script is marked executable.
let private writeEnvProbeScript (dir: string) (outFile: string) =
    let scriptPath = System.IO.Path.Combine(dir, "probe.sh")
    let script = $"#!/bin/sh\nprintf %%s \"$FSHW_RAN_FULL_SUITE\" > {outFile}\n"
    System.IO.File.WriteAllText(scriptPath, script)

    System.IO.File.SetUnixFileMode(
        scriptPath,
        System.IO.UnixFileMode.UserRead
        ||| System.IO.UnixFileMode.UserWrite
        ||| System.IO.UnixFileMode.UserExecute
    )

    scriptPath

let private runEnvProbe (pluginName: string) (ranFullSuite: bool) : string =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let outFile = System.IO.Path.Combine(tmpDir, "out")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let trigger =
            { FilePattern = None
              AfterTests = Some AnyTest }

        let script = writeEnvProbeScript tmpDir outFile

        let handler =
            create (FsHotWatch.PluginFramework.PluginName.create pluginName) trigger script "" None None

        host.RegisterHandler(handler)

        emitRunCompletedWithRanFullSuite host [ "P", FsHotWatch.Events.TestsPassed("", not ranFullSuite) ] ranFullSuite

        waitUntil
            (fun () ->
                match host.GetStatus(pluginName) with
                | Some(Completed _) -> true
                | _ -> false)
            8000

        test <@ System.IO.File.Exists(outFile) @>
        System.IO.File.ReadAllText(outFile)
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 10000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=true on a full run`` () =
    let contents = runEnvProbe "env-full" true
    test <@ contents = "true" @>

[<Fact(Timeout = 10000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=false on a partial run`` () =
    let contents = runEnvProbe "env-partial" false
    test <@ contents = "false" @>
