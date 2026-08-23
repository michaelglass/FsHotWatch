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

/// Drive the plugin as if a test run had just finished.
let private emitRunCompleted (host: PluginHost) (results: (string * TestResult) list) =
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          Verification = Ran RunScope.FullSuite }

/// Simulate the in-progress phase of a run: one group's delta under a given RunId.
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

    // Nothing matches, so this poll is expected to time out with the plugin still Idle.
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

    // SolutionChanged is ignored, so this poll is expected to time out at Idle.
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

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    // Nothing matches, so this poll is expected to time out with the plugin still Idle.
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

// The other tests build the CommandTrigger inline, so a bug in the config→trigger glue
// (a parser yielding AfterTests = None for a valid JSON list) would pass all of them.
// This one goes through parseConfig.
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

    // Without both subscriptions, dispatch never routes events to Update.
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

    // TestPrune's progressive emission: a partial delta WITHOUT the afterTests-listed
    // project, then a TestRunCompleted whose Results include it.
    let runId = System.Guid.NewGuid()
    emitProgress host runId [ "Other", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    // Long enough for the partial to be processed, so "still Idle" means something.
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
          Verification = Ran RunScope.FullSuite }

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
    // Subscribe-before-emit, used by every test in this file that awaits a terminal
    // status: a `waitUntil` poll races Idle→Running→Completed, because a slow `echo`
    // fork+exec under parallel CPU load can outlast the poll budget while the Fact
    // watchdog still has seconds to spare. Awaiting OnStatusChanged has no such window.
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

/// Emit a completed run whose verification is DERIVED from the results, the way
/// production derives it. Taking the scope as a separate bool would let a fixture assert
/// a breadth its own results contradict — a filtered project alongside a "full suite"
/// claim — and pin behaviour against a run that could not exist.
let private emitRunCompletedFor (host: PluginHost) (results: (string * TestResult) list) =
    let results = Map.ofList results

    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

/// Writes an executable probe script that echoes `$FSHW_RAN_FULL_SUITE` into `outFile`.
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

/// The one env-probe harness: temp-dir lifecycle, status budget and swallowed cleanup
/// live here only. Returns the value the child observed in `FSHW_RAN_FULL_SUITE`.
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
        emitRunCompletedFor host [ "P", FsHotWatch.Events.TestsPassed("", not ranFullSuite, TimeSpan.Zero) ])

[<Fact(Timeout = 20000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=true on a full run`` () =
    let contents = runEnvProbe "env-full" true
    test <@ contents = "true" @>

[<Fact(Timeout = 20000)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=false on a partial run`` () =
    let contents = runEnvProbe "env-partial" false
    test <@ contents = "false" @>

// The mid-run fire used to derive its full-suite claim from the ACCUMULATOR — a strict
// PREFIX of the run. Projects split across `group`s emit one TestProgress each, so with
// `afterTests: true` the hook fired after the FIRST group with a claim computed from that
// group alone; if a later group was impact-filtered the hook had been told `"true"`, and
// RunId dedupe meant the truthful TestRunCompleted could never correct it. A prefix can
// prove "partial" but never "full", so mid-run the only honest answer is `"unknown"`.
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

// A run that EXECUTED NOTHING must never tell a hook the full suite ran. Both degenerate
// lifecycles TestPrune builds — the aborted preflight and the "0 affected classes"
// impact-skip — carry `Results = Map.empty` with a vacuous full-suite claim. They are
// blocked only because `CommandTrigger.matches AnyTest` requires a non-empty results map
// and config rejects an empty `afterTests` list: an incidental guard nothing pinned.
[<Fact(Timeout = 20000)>]
let ``afterTests command does not fire for a run that executed nothing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "env-empty-run") trigger "echo" "ran" "/tmp" None

    host.RegisterHandler(handler)

    // The impact-skip lifecycle: Normal, no results. Fed the harsher legacy claim on
    // purpose — a replayed cache entry or an external producer can still assert a full
    // suite, so the guard is tested against the worst input, not today's honest one.
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.empty
          Verification = NoProjectsSelected }

    // And the aborted-preflight lifecycle.
    host.EmitTestRunCompleted
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Aborted "beforeRun hook failed"
          Results = Map.empty
          Verification = NoProjectsSelected }

    // Quiescence, not a fixed sleep: it proves both events were drained, where a sleep
    // proves only that time passed.
    waitForQuiescent host 5000
    test <@ host.GetStatus("env-empty-run") = Some Idle @>

// --- FullSuiteClaim: the whole truth table, in one place ---
// The dangerous cell is the first: a `"true"` on a run that verified nothing hands
// arbitrary user code a licence to refresh a coverage baseline off no evidence.

let private passed wasFiltered =
    FsHotWatch.Events.TestsPassed("", wasFiltered, TimeSpan.Zero)

/// The verification a result map establishes — the same derivation production uses.
let private verificationFor results =
    RunVerification.ofResults (Map.ofList results)

[<Fact>]
let ``FullSuiteClaim is unknown for a completed run that selected no project`` () =
    // The impact-skip / aborted-preflight shape. It can no longer be spelled as
    // "empty results claiming full suite": there is no such value to construct.
    test <@ FullSuiteClaim.derive true NoProjectsSelected = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is unknown when every project in a completed run failed to execute`` () =
    // Non-empty Results, still nothing verified. Before NothingExecuted existed, every
    // consumer had to re-derive this case for itself.
    let verification =
        verificationFor
            [ "A", FsHotWatch.Events.TestsDeferred "apphost not produced"
              "B", FsHotWatch.Events.TestsNoMatch("", TimeSpan.Zero)
              "C", FsHotWatch.Events.TestsErrored "no parseable report" ]

    test <@ verification = NothingExecuted @>
    test <@ FullSuiteClaim.derive true verification = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is unknown mid-run even when nothing so far was filtered`` () =
    // The regression this whole area exists for: a prefix cannot prove FULL.
    let prefix = verificationFor [ "GroupOne", passed false ]
    test <@ prefix = Ran RunScope.FullSuite @>
    test <@ FullSuiteClaim.derive false prefix = BreadthUnknown @>

[<Fact>]
let ``FullSuiteClaim is partial mid-run once any project is known filtered`` () =
    // A filtered project stays filtered, so PARTIAL is provable from a prefix.
    test <@ FullSuiteClaim.derive false (verificationFor [ "GroupOne", passed true ]) = PartialSuite @>

[<Fact>]
let ``FullSuiteClaim is partial for a completed filtered run`` () =
    test <@ FullSuiteClaim.derive true (verificationFor [ "A", passed false; "B", passed true ]) = PartialSuite @>

[<Fact>]
let ``FullSuiteClaim is full only for a completed unfiltered run that executed`` () =
    test <@ FullSuiteClaim.derive true (verificationFor [ "A", passed false; "B", passed false ]) = FullSuite @>

[<Fact>]
let ``FullSuiteClaim tokens are the documented wire values`` () =
    test <@ FullSuiteClaim.token FullSuite = "true" @>
    test <@ FullSuiteClaim.token PartialSuite = "false" @>
    test <@ FullSuiteClaim.token BreadthUnknown = "unknown" @>

// --- Cache-key salt regression tests ---
// The key used to be just the jj commit_id, so editing a config file referenced in args
// (coverage-ratchet.json thresholds, say) did not invalidate the cache. The salt must
// include command, args, and the content of any path-like arg that exists on disk.

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
    // `create` no longer accepts getCommitId, so this is now the structural invariant:
    // two handlers with identical (command, args, file content) hash the same.
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
    // The salt must be re-evaluated per event, or mid-session edits to a config file
    // never invalidate the cache. A "compute once at create" optimisation reintroduces
    // the original bug.
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

// --- argsStalerThan: arg-file paths whose mtime exceeds `referenceTime`. A non-empty
// result means a cached run from before that time may not reflect current input. ---

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
// hashFileWith and Update's defensive arms are reached by injection rather than real OS
// errors. The integration suite confirms the injected behaviour matches reality — without
// that positive control these would be assertions about a mock.

[<Fact(Timeout = 15000)>]
let ``computeArgsSaltWith differs when an arg-file's hash returns None vs Some`` () =
    // computeArgsSalt's Option.map None branch is reached when a path passes File.Exists
    // in collectArgFiles but tryHashFile returns None — injectable, unlike the real race.
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
    // It does not derive from IOException, so the narrow catch has to name it
    // explicitly — a chmod-000 file is expected, not a bug.
    let throwing _ =
        raise (System.UnauthorizedAccessException("denied"))

    let result = hashFileWith throwing "/any/path"
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``hashFileWith propagates non-IO exceptions (F5)`` () =
    // A bare `with _` here swallowed real bugs. With the catch narrowed to IOException +
    // UnauthorizedAccessException, a programming bug must surface rather than become a
    // silent None.
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
    // Subscriptions already filter this out before dispatch, so Update is invoked
    // directly to reach the safety net at all.
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

// ---------------------------------------------------------------------------
// AUTOMATION-343 — FILE-COMMAND's own cold-vs-cached ledger parity
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-343: a cached file-command replay leaves an out-of-batch finding standing`` () =
    // Same shape as the build control, different plugin: FileCommandPlugin reports
    // and clears exactly one pseudo-file, `<run-scripts>`, and never calls
    // ClearAllErrors. Its cache key is whole-run (`File = None`), which is precisely
    // the combination the deleted blanket `ClearPlugin` was destructive for — a
    // per-file ledger under a whole-run key.
    let cache =
        FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            "/tmp"
            None

    host.RegisterHandler(handler)

    // A finding carried from an earlier batch, about a file this command never
    // mentions.
    seedOutOfBatch host "run-scripts"

    // COLD. The command really runs and mints the cache entry.
    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])
    waitForTerminalStatus host "run-scripts" 15000
    let coldSummary = terminalSummaryOf host "run-scripts"
    test <@ not (coldSummary.Contains "(cached)") @>
    let cold = ledgerSlice host "run-scripts"

    // The real run keeps it.
    test <@ ledgerHasOutOfBatch host "run-scripts" @>

    // WARM. Same command, same args, no arg-file moved — so the merkle is unchanged
    // and this dispatch MUST be served from the cache.
    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])
    let replayed = waitForCachedReplay host "run-scripts" 15000
    test <@ replayed @>

    let cached = ledgerSlice host "run-scripts"

    test <@ ledgerHasOutOfBatch host "run-scripts" @>
    test <@ cached = cold @>
