module FsHotWatch.Tests.FileCommandPluginTests

open System
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

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            "/tmp"
            None

    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "run-scripts" @>

[<Fact(Timeout = 15000)>]
let ``command runs when matching files change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            "/tmp"
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

[<Fact(Timeout = 20000)>]
let ``command does not run for non-matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            "/tmp"
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

[<Fact(Timeout = 15000)>]
let ``command captures stdout output`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "captured-output"
            "/tmp"
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

[<Fact(Timeout = 15000)>]
let ``command with environment variables`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "env-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "env-test-output"
            "/tmp"
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

[<Fact(Timeout = 15000)>]
let ``command runs on ProjectChanged with matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "proj-watcher")
            (fileTrigger (fun f -> f.EndsWith(".fsproj")))
            "echo"
            "project changed"
            "/tmp"
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

[<Fact(Timeout = 20000)>]
let ``command ignores SolutionChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "sln-watcher")
            (fileTrigger (fun _ -> true))
            "echo"
            "hello"
            "/tmp"
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged)

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

[<Fact(Timeout = 15000)>]
let ``command reports Failed status on command failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-cmd")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            "/tmp"
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

// ``FileCommandPlugin honors timeoutSec and records TimedOut`` moved to
// FsHotWatch.IntegrationTests/PluginTimeoutTests.fs (coverage-deterministic; rationale there).

[<Fact(Timeout = 15000)>]
let ``command reports Failed status on exception`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "bad-cmd")
            (fileTrigger (fun _ -> true))
            "this-command-does-not-exist-xyz"
            ""
            "/tmp"
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

[<Fact(Timeout = 20000)>]
let ``status command returns not run when no files matched`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "no-match")
            (fileTrigger (fun _ -> false))
            "echo"
            "hello"
            "/tmp"
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

[<Fact(Timeout = 15000)>]
let ``status command returns false when command failed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-status")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            "/tmp"
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

[<Fact(Timeout = 15000)>]
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
            "/tmp"
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

[<Fact(Timeout = 15000)>]
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
            "/tmp"
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

[<Fact(Timeout = 15000)>]
let ``afterTests TestProjects fires when ALL listed projects have results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-listed") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero)
          "B", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero)
          "Other", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

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

[<Fact(Timeout = 20000)>]
let ``afterTests TestProjects does not fire when only some listed projects have completed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-partial") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    // Only A has completed — B is still outstanding. Model as mid-run progress.
    emitProgress host (System.Guid.NewGuid()) [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

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

[<Fact(Timeout = 20000)>]
let ``afterTests TestProjects does not fire when no listed project matches`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "Intelligence.Tests.Unit" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-miss") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    emitRunCompleted host [ "Intelligence.Tests.Integration", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

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

[<Fact(Timeout = 20000)>]
let ``afterTests TestProjects fires exactly once across progressive deltas`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCount, counter) = commandCounter "afterTests-once"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-once") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    let runId = System.Guid.NewGuid()

    // Delta 1: {A} arrives — accumulator = {A}, filter not satisfied.
    emitProgress host runId [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    // Delta 2: {B} arrives — accumulator = {A,B}, filter satisfied; fire.
    emitProgress host runId [ "B", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    waitUntil (fun () -> getCount () >= 1) 12000

    // Delta 3: {C} arrives — accumulator = {A,B,C}, filter still satisfies
    //           but this is the same RunId → dedupe, no re-fire.
    emitProgress host runId [ "C", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    System.Threading.Thread.Sleep(500)

    test <@ getCount () = 1 @>

[<Fact(Timeout = 20000)>]
let ``afterTests TestProjects fires again on a fresh batch`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCount, counter) = commandCounter "afterTests-rebatch"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-rebatch") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    // Batch 1 — plugin fires once when both projects complete.
    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero)
          "B", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    waitUntil (fun () -> getCount () >= 1) 12000

    // Batch 2 — NEW RunId. Plugin's idempotency sentinel is tied to the
    // previous RunId, so this fresh event must fire again.
    emitRunCompleted
        host
        [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero)
          "B", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    waitUntil (fun () -> getCount () >= 2) 12000
    test <@ getCount () = 2 @>

// Regression: end-to-end from parseConfig(.fshw.json) → daemon registration
// path → lifecycle dispatch. The earlier unit tests built the CommandTrigger
// inline and hit the plugin the same way the daemon's RegisterHandler path does,
// but a bug in the config→trigger glue (e.g. parser yielding AfterTests = None
// for a valid JSON list) would not be caught without going through parseConfig.
[<Fact(Timeout = 15000)>]
let ``parseConfig + registration + TestRunCompleted fires coverage-ratchet-style plugin`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let json =
        """{"fileCommands": [{"name": "cov-r", "afterTests": ["ProjA"], "command": "echo", "args": "ok"}]}"""

    let defaults: FsHotWatch.Cli.DaemonConfig.DaemonConfiguration =
        { defaultTestConfig () with
            Build = None
            Format = FsHotWatch.Cli.DaemonConfig.Off
            Lint = false
            Cache = FsHotWatch.Cli.DaemonConfig.NoCache }

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
        create (FsHotWatch.PluginFramework.PluginName.create fc.PluginName) trigger fc.Command fc.Args "/tmp" None

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
    emitProgress host runId [ "Other", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    // Brief pause to make sure the partial was processed.
    System.Threading.Thread.Sleep(200)
    test <@ host.GetStatus("cov-r") = Some Idle @>

    host.EmitTestRunCompleted
        { RunId = runId
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results =
            Map.ofList
                [ "Other", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero)
                  "ProjA", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]
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

[<Fact(Timeout = 15000)>]
let ``afterTests AnyTest fires on TestRunCompleted regardless of projects`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-any") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)
    // Subscribe-before-emit (see `combined-a`/`combined-b` below): the previous
    // `waitUntil ... 5000` poll raced the Idle→Running→Completed transition — a
    // slow `echo` fork+exec under heavy parallel CPU load could outlast the 5s
    // polling budget while the `Fact(Timeout=15000)` watchdog still had 10s to
    // spare. Awaiting the terminal status on the OnStatusChanged event removes
    // the poll-granularity race and uses the full Fact budget deterministically.
    let completion = beginAwaitTerminal host "afterTests-any"
    emitRunCompleted host [ "AnyProject", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]
    completion.Wait(TimeSpan.FromSeconds 12.0) |> ignore

    test
        <@
            match host.GetStatus("afterTests-any") with
            | Some(Completed _) -> true
            | _ -> false
        @>

// --- Combined trigger: pattern + afterTests ---

[<Fact(Timeout = 20000)>]
let ``plugin with both pattern and afterTests fires on file change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-a") trigger "echo" "hi" "/tmp" None

    host.RegisterHandler(handler)
    // Subscribe-before-emit: avoids the polling race in waitForTerminalStatus where
    // a slow `echo` fork+exec under CI load can exceed the 5s polling budget while
    // the Fact(Timeout=20000) watchdog still has 15s to spare.
    let completion = beginAwaitTerminal host "combined-a"
    host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
    completion.Wait(TimeSpan.FromSeconds 15.0) |> ignore

    test
        <@
            match host.GetStatus("combined-a") with
            | Some(Completed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 20000)>]
let ``plugin with both pattern and afterTests fires on test completion`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-b") trigger "echo" "hi" "/tmp" None

    host.RegisterHandler(handler)
    // Subscribe-before-emit: see sibling test above. Observed 5081ms timeout under
    // CI load when previous waitForTerminalStatus polling pattern raced the Run→
    // Completed transition.
    let completion = beginAwaitTerminal host "combined-b"
    emitRunCompleted host [ "proj-a", TestsPassed("ok", false, TimeSpan.Zero) ]
    completion.Wait(TimeSpan.FromSeconds 15.0) |> ignore

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

/// The `afterTests: true` trigger — no file pattern, fire after any test run.
let private anyTestTrigger: CommandTrigger =
    { FilePattern = None
      AfterTests = Some AnyTest }

/// Run the env probe against an arbitrary driver that feeds the host events.
/// Returns the value the child process observed in `FSHW_RAN_FULL_SUITE`.
///
/// The ONE env-probe harness: the temp-dir lifecycle, the 8000ms status budget and
/// the swallowed cleanup live here only. `runEnvProbe` below is this function with
/// the two things it fixes — trigger and driver — supplied.
let private runEnvProbeWith (pluginName: string) (trigger: CommandTrigger) (drive: PluginHost -> unit) : string =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let outFile = System.IO.Path.Combine(tmpDir, "out")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let script = writeEnvProbeScript tmpDir outFile

        let handler =
            create (FsHotWatch.PluginFramework.PluginName.create pluginName) trigger script "" tmpDir None

        host.RegisterHandler(handler)
        drive host

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

/// A complete run of one project, `afterTests: true`. The project is filtered iff
/// the run is not a full suite, so the results agree with the claim.
let private runEnvProbe (pluginName: string) (ranFullSuite: bool) : string =
    runEnvProbeWith pluginName anyTestTrigger (fun host ->
        emitRunCompletedWithRanFullSuite
            host
            [ "P", FsHotWatch.Events.TestsPassed("", not ranFullSuite, TimeSpan.Zero) ]
            ranFullSuite)

[<Fact(Timeout = 20000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=true on a full run`` () =
    let contents = runEnvProbe "env-full" true
    test <@ contents = "true" @>

[<Fact(Timeout = 20000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=false on a partial run`` () =
    let contents = runEnvProbe "env-partial" false
    test <@ contents = "false" @>

// REGRESSION (hook-scope): the mid-run fire derived its full-suite claim from
// the ACCUMULATOR — a strict PREFIX of the run. A run whose projects are split
// across `group`s emits one TestProgress per group, so with `afterTests: true`
// (the README's own coverage-ratchet example) the hook fires after the FIRST
// group with a claim computed from that group alone. If a later group is
// impact-filtered the run is partial, yet the hook was told `"true"` — and
// RunId dedupe means the truthful `TestRunCompleted` (RanFullSuite=false) can
// never correct it. A prefix can PROVE "partial" (a filtered project stays
// filtered) but can never prove "full", so mid-run the only honest answer is
// `"unknown"`.
[<Fact(Timeout = 20000)>]
let ``afterTests command is not told the full suite ran while the run is still in flight`` () =
    let runId = System.Guid.NewGuid()

    let contents =
        runEnvProbeWith
            "env-midrun"
            { FilePattern = None
              AfterTests = Some AnyTest }
            (fun host ->
                // Group 1 finishes, unfiltered. Groups 2..n have not reported.
                emitProgress host runId [ "GroupOne", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ])

    test <@ contents = "unknown" @>

// REGRESSION (hook-scope): a run that EXECUTED NOTHING must never tell a hook
// the full suite ran. Both degenerate lifecycles TestPrune builds carry
// `Results = Map.empty` with `RanFullSuite = true` (vacuously — nothing was
// filtered): `abortedRunLifecycle` (TestPrunePlugin.fs, `Outcome = Aborted`)
// and the "0 affected classes" impact-skip (`Outcome = Normal`).
//
// They are blocked today only because `CommandTrigger.matches AnyTest` requires
// a non-empty results map, and config rejects an empty `afterTests` list — an
// INCIDENTAL guard that nothing pinned. This pins it: an empty run fires no
// afterTests command at all.
[<Fact(Timeout = 20000)>]
let ``afterTests command does not fire for a run that executed nothing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "env-empty-run") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    // The impact-skip lifecycle: Normal, no results.
    //
    // `RanFullSuite = true` is the HARSHER input, deliberately — it is what these
    // lifecycles emitted before AUTOMATION-281 made them say `false`. Keeping the
    // old value here means the guard is tested against the worst thing that can
    // arrive (a replayed cache entry or an external producer can still say `true`),
    // rather than only against today's honest producer.
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.empty
          RanFullSuite = true }

    // And the aborted-preflight lifecycle.
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Aborted "beforeRun hook failed"
          Results = Map.empty
          RanFullSuite = true }

    // Quiescence rather than a fixed 600ms: returns as soon as both events are
    // drained, and actually proves they were — a sleep proves only that time passed.
    waitForQuiescent host 5000
    test <@ host.GetStatus("env-empty-run") = Some Idle @>

// --- FullSuiteClaim: the whole truth table, in one place ---
// The env var's value is decided HERE, so this is where the contract is pinned.
// The dangerous cell is the first one: a `"true"` on a run that verified nothing
// is a licence, handed to arbitrary user code, to refresh a coverage baseline
// off no evidence.

let private passed wasFiltered =
    FsHotWatch.Events.TestsPassed("", wasFiltered, TimeSpan.Zero)

[<Fact>]
let ``FullSuiteClaim is unknown for a completed run that executed nothing`` () =
    // The impact-skip / aborted-preflight shape: empty Results, RanFullSuite
    // vacuously true.
    test <@ FullSuiteClaim.derive true Map.empty true = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is unknown when every project in a completed run failed to execute`` () =
    // Non-empty Results, still nothing verified.
    let results =
        Map.ofList
            [ "A", FsHotWatch.Events.TestsDeferred "apphost not produced"
              "B", FsHotWatch.Events.TestsNoMatch("", TimeSpan.Zero)
              "C", FsHotWatch.Events.TestsErrored "no parseable report" ]

    test <@ FullSuiteClaim.derive true results (TestResult.ranFullSuite results) = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is unknown mid-run even when nothing so far was filtered`` () =
    let prefix = Map.ofList [ "GroupOne", passed false ]
    test <@ FullSuiteClaim.derive false prefix true = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is partial mid-run once any project is known filtered`` () =
    // A filtered project stays filtered, so PARTIAL is provable from a prefix.
    let prefix = Map.ofList [ "GroupOne", passed true ]
    test <@ FullSuiteClaim.derive false prefix false = PartialSuite @>

[<Fact>]
let ``FullSuiteClaim is partial for a completed filtered run`` () =
    let results = Map.ofList [ "A", passed false; "B", passed true ]
    test <@ FullSuiteClaim.derive true results false = PartialSuite @>

[<Fact>]
let ``FullSuiteClaim is full only for a completed unfiltered run that executed`` () =
    let results = Map.ofList [ "A", passed false; "B", passed false ]
    test <@ FullSuiteClaim.derive true results true = FullSuite @>

[<Fact>]
let ``FullSuiteClaim tokens are the documented wire values`` () =
    test <@ FullSuiteClaim.token FullSuite = "true" @>
    test <@ FullSuiteClaim.token PartialSuite = "false" @>
    test <@ FullSuiteClaim.token BreadthUnknown = "unknown" @>

// --- Cache-key salt regression tests ---
// Bug: editing a config file referenced in args (e.g. coverage-ratchet.json
// thresholds) didn't invalidate the FileCommandPlugin cache because the key
// was just the jj commit_id. The salt must include command, args, and the
// content of any path-like arg that exists on disk.

/// Build a handler and pull its CacheKey function. Tests then evaluate it
/// against synthetic events and assert how the key responds to input changes.
let private cacheKeyFnFor (command: string) (args: string) =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "ck-test")
            (fileTrigger (fun _ -> true))
            command
            args
            "/tmp"
            None

    // Drive one run so the cold-start guard flips; CacheKey returns None until then.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "trigger.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("ck-test") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        5000
    |> ignore

    handler.CacheKey.Value

[<Fact(Timeout = 20000)>]
let ``cache key is independent of commit_id`` () =
    // jj reliance dropped: the create signature no longer accepts getCommitId.
    // This test now degenerates to "two handlers with identical (command, args,
    // file content) hash the same" — preserved as a structural invariant.
    let buildKeyFn () =
        let handler =
            create
                (FsHotWatch.PluginFramework.PluginName.create "ck-commit-test")
                (fileTrigger (fun _ -> true))
                "echo"
                "args"
                "/tmp"
                None

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
        host.RegisterHandler(handler)
        host.EmitFileChanged(SourceChanged [ "trigger.txt" ])

        waitUntil
            (fun () ->
                match host.GetStatus("ck-commit-test") with
                | Some(Completed _)
                | Some(Failed _) -> true
                | _ -> false)
            5000
        |> ignore

        handler.CacheKey.Value

    let keyFnA = buildKeyFn ()
    let keyFnB = buildKeyFn ()
    let event = FileChanged(SourceChanged [ "trigger.txt" ])
    let kA = keyFnA event
    let kB = keyFnB event
    test <@ kA.IsSome @>
    test <@ kA = kB @>

[<Fact(Timeout = 20000)>]
let ``cache key changes when content of a path-arg file changes`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let configPath = System.IO.Path.Combine(tmpDir, "config.json")

    try
        System.IO.File.WriteAllText(configPath, """{"threshold": 80}""")
        let keyFn1 = cacheKeyFnFor "echo" configPath
        let event = FileChanged(SourceChanged [ "trigger.txt" ])
        let k1 = keyFn1 event

        System.IO.File.WriteAllText(configPath, """{"threshold": 70}""")
        let keyFn2 = cacheKeyFnFor "echo" configPath
        let k2 = keyFn2 event

        test <@ k1.IsSome @>
        test <@ k2.IsSome @>
        test <@ k1 <> k2 @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 20000)>]
let ``single handler: cache key reflects current file content per event`` () =
    // Regression: salt must be re-evaluated per event so that mid-session
    // edits to a config file invalidate the cache. A "compute once at create"
    // optimization would freeze the salt and reintroduce the original bug.
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let configPath = System.IO.Path.Combine(tmpDir, "config.json")

    try
        System.IO.File.WriteAllText(configPath, """{"threshold": 80}""")
        let keyFn = cacheKeyFnFor "echo" configPath
        let event = FileChanged(SourceChanged [ "trigger.txt" ])
        let k1 = keyFn event

        System.IO.File.WriteAllText(configPath, """{"threshold": 70}""")
        let k2 = keyFn event

        test <@ k1.IsSome @>
        test <@ k2.IsSome @>
        test <@ k1 <> k2 @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 20000)>]
let ``cache key changes when args change`` () =
    let keyFn1 = cacheKeyFnFor "echo" "alpha"
    let keyFn2 = cacheKeyFnFor "echo" "beta"
    let event = FileChanged(SourceChanged [ "trigger.txt" ])
    let k1 = keyFn1 event
    let k2 = keyFn2 event
    test <@ k1.IsSome @>
    test <@ k1 <> k2 @>

[<Fact(Timeout = 20000)>]
let ``cache key changes when command changes`` () =
    let keyFn1 = cacheKeyFnFor "echo" "x"
    let keyFn2 = cacheKeyFnFor "true" "x"
    let event = FileChanged(SourceChanged [ "trigger.txt" ])
    let k1 = keyFn1 event
    let k2 = keyFn2 event
    test <@ k1.IsSome @>
    test <@ k1 <> k2 @>

// --- collectArgFiles helper (used by observer staleness warning) ---
// Returns absolute paths of args tokens that resolve to existing files.
// Used by run-once reporters to flag inputs that were modified after a
// plugin's last run, hinting that cached output may be stale.

[<Fact(Timeout = 15000)>]
let ``collectArgFiles returns absolute path of an existing relative arg`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfgPath = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfgPath, "{}")
        let result = collectArgFiles tmpDir "--check cfg.json"
        test <@ List.contains cfgPath result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``collectArgFiles ignores non-file tokens`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        // None of these args reference an existing file.
        let result = collectArgFiles tmpDir "--flag value --another"
        test <@ List.isEmpty result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``collectArgFiles accepts absolute paths`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfgPath = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfgPath, "{}")
        // Pass an unrelated repoRoot — absolute path should still resolve.
        let result = collectArgFiles "/elsewhere" $"check {cfgPath}"
        test <@ List.contains cfgPath result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

// --- argsStalerThan: compare arg-file mtimes to a reference time ---
// Returns the subset of arg-file paths whose mtime exceeds `referenceTime`.
// If any are returned, a cached run from before `referenceTime` may not
// reflect current input.

[<Fact(Timeout = 15000)>]
let ``argsStalerThan flags files modified after the reference time`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfgPath = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfgPath, "{}")
        let oldMtime = System.DateTime.UtcNow.AddMinutes(-1.0)
        System.IO.File.SetLastWriteTimeUtc(cfgPath, System.DateTime.UtcNow)
        let result = argsStalerThan tmpDir "--check cfg.json" oldMtime
        test <@ List.contains cfgPath result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

// --- DI-injected error paths ---
// hashFileWith and Update's defensive arms are exercised through dependency
// injection / direct invocation rather than relying on real OS errors. A
// separate integration suite (no coverage) confirms the injected behaviors
// match real-world failure modes (e.g. unreadable files).

[<Fact(Timeout = 15000)>]
let ``computeArgsSaltWith differs when an arg-file's hash returns None vs Some`` () =
    // The Option.map None branch of computeArgsSalt is reached when a path
    // passes File.Exists in collectArgFiles but tryHashFile returns None.
    // Inject the hash function so we can exercise both branches deterministically.
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let p = System.IO.Path.Combine(tmpDir, "config.json")

    try
        System.IO.File.WriteAllText(p, "x")

        let saltWithSome =
            computeArgsSaltWith (fun _ -> Some "abc") tmpDir "echo" "config.json"

        let saltWithNone = computeArgsSaltWith (fun _ -> None) tmpDir "echo" "config.json"

        // When hash returns None, the file contributes no salt entry — distinct
        // from when hash returns Some.
        test <@ saltWithSome <> saltWithNone @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``hashFileWith returns None when reader throws IOException`` () =
    let throwing _ =
        raise (System.IO.IOException("simulated read failure"))

    let result = hashFileWith throwing "/any/path"
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``hashFileWith returns None when reader throws UnauthorizedAccessException`` () =
    // UnauthorizedAccessException is an IOException-class failure (chmod-000 path)
    // — narrow catch must accept it as a transient/expected file-IO error.
    let throwing _ =
        raise (System.UnauthorizedAccessException("denied"))

    let result = hashFileWith throwing "/any/path"
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``hashFileWith propagates non-IO exceptions (F5)`` () =
    // F5: bare `with _` previously swallowed real bugs. After narrowing to
    // IOException + UnauthorizedAccessException, a programming bug
    // (NullReferenceException) must surface, not produce silent None.
    let throwing _ : byte[] =
        raise (System.NullReferenceException("real bug"))

    let mutable thrown = false

    try
        hashFileWith throwing "/any/path" |> ignore
    with :? System.NullReferenceException ->
        thrown <- true

    test <@ thrown @>

[<Fact(Timeout = 15000)>]
let ``hashFileWith returns Some hex for successful read`` () =
    let constReader (_: string) =
        System.Text.Encoding.UTF8.GetBytes("hello")

    let result = hashFileWith constReader "/any/path"
    // sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
    test <@ result = Some "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824" @>

[<Fact(Timeout = 15000)>]
let ``Update is a no-op for FileChanged when trigger has no FilePattern`` () =
    // Defensive arm: the framework filters by Subscriptions before dispatching,
    // so an afterTests-only handler shouldn't receive FileChanged. Direct-invoke
    // Update to assert the safety net behaves correctly anyway.
    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "no-pattern") trigger "echo" "hi" "/tmp" None

    let ctx: FsHotWatch.PluginFramework.PluginCtx<unit> =
        { ReportStatus = fun _ -> ()
          ReportErrors = fun _ _ -> ()
          ClearErrors = fun _ -> ()
          ClearAllErrors = fun () -> ()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = "/tmp"
          Post = fun _ -> ()
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let initialState = handler.Init
    let event = FileChanged(SourceChanged [ "anything.fs" ])

    let nextState = handler.Update ctx initialState event |> Async.RunSynchronously

    test <@ nextState = initialState @>

[<Fact(Timeout = 15000)>]
let ``argsStalerThan returns empty when files are older than reference`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfgPath = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfgPath, "{}")
        let pastMtime = System.DateTime.UtcNow.AddMinutes(-5.0)
        System.IO.File.SetLastWriteTimeUtc(cfgPath, pastMtime)
        let referenceTime = System.DateTime.UtcNow
        let result = argsStalerThan tmpDir "--check cfg.json" referenceTime
        test <@ List.isEmpty result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

// ---------------------------------------------------------------------------
// The timed-out arm: a command that outlives its budget is a NON-GREEN terminal
// carrying an honest verdict — never a ✓, and never a zero-length run.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a command that exceeds its timeout reports Failed with a timed-out verdict`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "slow-cmd")
            (fileTrigger (fun _ -> true))
            "sh"
            "-c \"sleep 30\""
            "/tmp"
            (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "anything.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("slow-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        25000

    match host.GetStatus("slow-cmd") with
    | Some(Failed(err, _, v)) ->
        test <@ err.Contains "timed out" @>
        // The verdict says WHAT happened and HOW LONG it took — the run record
        // therefore records a real elapsed, not a fabricated zero.
        test <@ v.Summary.Contains "timed out" @>
        test <@ v.Elapsed > TimeSpan.Zero @>
    | other -> failwithf "expected Failed carrying a timed-out verdict, got %A" other

    // The run history records the TimedOut outcome (set via CompleteWithTimeout),
    // distinct from an ordinary failure.
    let record = List.head (host.GetHistory("slow-cmd"))

    match record.Outcome with
    | TimedOut _ -> ()
    | other -> failwithf "expected a TimedOut run outcome, got %A" other

    test <@ record.Elapsed > TimeSpan.Zero @>
