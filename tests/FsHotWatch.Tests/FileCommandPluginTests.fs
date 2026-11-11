module FsHotWatch.Tests.FileCommandPluginTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.FileCommand.FileCommandPlugin
open FsHotWatch.Tests.TestHelpers

/// An empty repository root. A FileChanged key hashes every file the trigger pattern
/// matches under its root (AUTOMATION-1557), so a fixture rooted at `/tmp` with a
/// match-everything pattern would hash the whole of `/tmp` on every event.
let private emptyRoot =
    lazy
        (let dir =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fc-empty-root-" + Guid.NewGuid().ToString("N"))

         System.IO.Directory.CreateDirectory(dir) |> ignore
         dir)

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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``plugin has correct name`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            emptyRoot.Value
            None

    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "run-scripts" @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command runs when matching files change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command does not run for non-matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // The event was handled and fired nothing — witnessed by its commit, not by a wait
    // expiring.
    waitForHandled host 1L 10000

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command captures stdout output`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "captured-output"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "anything.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("echo-test") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    waitForQuiescent host 30000

    let result = host.RunCommand("echo-test-status", [||]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("true") @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command with environment variables`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "env-test")
            (fileTrigger (fun _ -> true))
            "echo"
            "env-test-output"
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command runs on ProjectChanged with matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "proj-watcher")
            (fileTrigger (fun f -> f.EndsWith(".fsproj")))
            "echo"
            "project changed"
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command ignores SolutionChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "sln-watcher")
            (fileTrigger (fun _ -> true))
            "echo"
            "hello"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged)

    // The event was handled and fired nothing — witnessed by its commit, not by a wait
    // expiring.
    waitForHandled host 1L 10000

    let status = host.GetStatus("sln-watcher")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command reports Failed status on command failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-cmd")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``command reports Failed status on exception`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "bad-cmd")
            (fileTrigger (fun _ -> true))
            "this-command-does-not-exist-xyz"
            ""
            emptyRoot.Value
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


[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the status command declares its reply schema`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "declared")
            (fileTrigger (fun _ -> false))
            "echo"
            "hello"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    let reply = host.RunCommandReply("declared-status", [||]) |> Async.RunSynchronously
    let declared = reply |> Option.map fst

    test <@ declared = Some(ReplySchema.Typed FsHotWatch.FileCommand.FileCommandReplies.StatusSchema) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``status command returns not run when no files matched`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "no-match")
            (fileTrigger (fun _ -> false))
            "echo"
            "hello"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    // The event was handled and fired nothing — witnessed by its commit, not by a wait
    // expiring.
    waitForHandled host 1L 10000

    waitForQuiescent host 30000

    let result = host.RunCommand("no-match-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``status command returns false when command failed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-status")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fail-status") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    waitForQuiescent host 30000

    let result = host.RunCommand("fail-status-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("false") @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``emits CommandCompleted on success`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-cmd")
            (fileTrigger (fun _ -> true))
            "echo"
            "hello"
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``emits CommandCompleted on failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "fail-cmd-emit")
            (fileTrigger (fun _ -> true))
            "false"
            ""
            emptyRoot.Value
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests TestProjects fires when ALL listed projects have results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "afterTests-listed")
            trigger
            "echo"
            "ran"
            emptyRoot.Value
            None

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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests TestProjects does not fire when only some listed projects have completed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "afterTests-partial")
            trigger
            "echo"
            "ran"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    // Only A has completed — B is still outstanding. Model as mid-run progress.
    emitProgress host (System.Guid.NewGuid()) [ "A", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    // The event was handled and fired nothing — witnessed by its commit, not by a wait
    // expiring.
    waitForHandled host 1L 10000

    let status = host.GetStatus("afterTests-partial")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests TestProjects does not fire when no listed project matches`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "Intelligence.Tests.Unit" ])) }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "afterTests-miss")
            trigger
            "echo"
            "ran"
            emptyRoot.Value
            None

    host.RegisterHandler(handler)

    emitRunCompleted host [ "Intelligence.Tests.Integration", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]

    // The event was handled and fired nothing — witnessed by its commit, not by a wait
    // expiring.
    waitForHandled host 1L 10000

    let status = host.GetStatus("afterTests-miss")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<WallClock("negative window: asserts that something does NOT happen while it waits; load only delays events, so it can hide a defect on a slow box but cannot fail a correct one (docs/wall-clock-bounds.md rule 3)")>]
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests TestProjects fires exactly once across progressive deltas`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
    let (getCount, counter) = commandCounter "afterTests-once"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "afterTests-once")
            trigger
            "echo"
            "ran"
            emptyRoot.Value
            None

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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests TestProjects fires again on a fresh batch`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
    let (getCount, counter) = commandCounter "afterTests-rebatch"
    host.RegisterHandler(counter)

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "A"; "B" ])) }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "afterTests-rebatch")
            trigger
            "echo"
            "ran"
            emptyRoot.Value
            None

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
[<WallClock("negative window: asserts that something does NOT happen while it waits; load only delays events, so it can hide a defect on a slow box but cannot fail a correct one (docs/wall-clock-bounds.md rule 3)")>]
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``parseConfig + registration + TestRunCompleted fires coverage-ratchet-style plugin`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

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
        create
            (FsHotWatch.PluginFramework.PluginName.create fc.PluginName)
            trigger
            fc.Command
            fc.Args
            emptyRoot.Value
            None

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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests AnyTest fires on TestRunCompleted regardless of projects`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-any") trigger "echo" "ran" emptyRoot.Value None

    host.RegisterHandler(handler)
    // Subscribe-before-emit, used by every test in this file that awaits a terminal
    // status: a `waitUntil` poll races Idle→Running→Completed, because a slow `echo`
    // fork+exec under parallel CPU load can outlast the poll budget while the Fact
    // watchdog still has seconds to spare. Awaiting OnStatusChanged has no such window.
    let completion = beginAwaitTerminal host "afterTests-any"
    emitRunCompleted host [ "AnyProject", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]
    completion.Wait HangWait |> ignore

    test
        <@
            match host.GetStatus("afterTests-any") with
            | Some(Completed _) -> true
            | _ -> false
        @>

// --- Combined trigger: pattern + afterTests ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``plugin with both pattern and afterTests fires on file change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-a") trigger "echo" "hi" emptyRoot.Value None

    host.RegisterHandler(handler)
    let completion = beginAwaitTerminal host "combined-a"
    host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
    completion.Wait HangWait |> ignore

    test
        <@
            match host.GetStatus("combined-a") with
            | Some(Completed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``plugin with both pattern and afterTests fires on test completion`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "combined-b") trigger "echo" "hi" emptyRoot.Value None

    host.RegisterHandler(handler)
    let completion = beginAwaitTerminal host "combined-b"
    emitRunCompleted host [ "proj-a", TestsPassed("ok", false, TimeSpan.Zero) ]
    completion.Wait HangWait |> ignore

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

/// How an env probe's command is spelled: the `(command, args)` the plugin runs for the
/// probe script at the given path.
type private ProbeCommand = string -> string * string

/// The command the env probes run: `sh` reading the script. The script is written just
/// before it runs, in a suite that forks children in parallel, so a child forked while it
/// was open for writing may still hold that handle when the probe fires. `sh` only reads
/// the script; executing it directly is refused on Linux while any handle can write it.
let private probeCommand: ProbeCommand = fun script -> "sh", script

/// The env-probe harness, given the command the plugin runs and whether the probe script
/// is still open for writing when that command fires. Temp-dir lifecycle, status budget
/// and swallowed cleanup live here only. Returns the value the child observed in
/// `FSHW_RAN_FULL_SUITE`.
let private runEnvProbeAs
    (command: ProbeCommand)
    (scriptHeldOpen: bool)
    (pluginName: string)
    (trigger: CommandTrigger)
    (drive: PluginHost -> unit)
    : string =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let outFile = System.IO.Path.Combine(tmpDir, "out")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let script = writeEnvProbeScript tmpDir outFile
        let commandName, args = command script

        let handler =
            create (FsHotWatch.PluginFramework.PluginName.create pluginName) trigger commandName args tmpDir None

        host.RegisterHandler(handler)

        // A process that forks while the script is open for writing gives its child a
        // copy of that write handle, which the child holds until it execs. Holding the
        // handle across the fire makes that ordering certain.
        let held =
            if scriptHeldOpen then
                Some(
                    new System.IO.FileStream(
                        script,
                        System.IO.FileMode.Append,
                        System.IO.FileAccess.Write,
                        System.IO.FileShare.ReadWrite
                    )
                )
            else
                None

        // Any terminal, not only Completed: a fire that ended Failed is the answer, and
        // waiting for a Completed that cannot come would only report the budget.
        let terminal = beginAwaitTerminal host pluginName

        try
            drive host

            if not (terminal.Wait HangWait) then
                raise (
                    TimeoutException
                        $"env probe '%s{pluginName}' reached no terminal status within %d{HangWaitMs} ms (status: %A{host.GetStatus pluginName})"
                )
        finally
            held |> Option.iter (fun stream -> stream.Dispose())

        match terminal.Result with
        | Failed(error, _, _) ->
            let reported =
                host.GetErrorsByPlugin pluginName
                |> Map.toList
                |> List.collect (fun (_, entries) -> entries |> List.map (fun entry -> entry.Message))

            failwith
                $"env probe '%s{pluginName}' ended Failed: %s{error}; reported: %s{String.Join(Environment.NewLine, reported)}"
        | _ -> ()

        test <@ System.IO.File.Exists(outFile) @>
        System.IO.File.ReadAllText(outFile)
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

/// The one env-probe harness: `runEnvProbeAs` with the probes' own command.
let private runEnvProbeWith (pluginName: string) (trigger: CommandTrigger) (drive: PluginHost -> unit) : string =
    runEnvProbeAs probeCommand false pluginName trigger drive

/// A complete run of one project, `afterTests: true`. The project is filtered iff
/// the run is not a full suite, so the results agree with the claim.
let private runEnvProbe (pluginName: string) (ranFullSuite: bool) : string =
    runEnvProbeWith pluginName anyTestTrigger (fun host ->
        emitRunCompletedFor host [ "P", FsHotWatch.Events.TestsPassed("", not ranFullSuite, TimeSpan.Zero) ])

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=true on a full run`` () =
    let contents = runEnvProbe "env-full" true
    test <@ contents = "true" @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests command receives FSHW_RAN_FULL_SUITE=false on a partial run`` () =
    let contents = runEnvProbe "env-partial" false
    test <@ contents = "false" @>

// The mid-run fire used to derive its full-suite claim from the ACCUMULATOR — a strict
// PREFIX of the run. Projects split across `group`s emit one TestProgress each, so with
// `afterTests: true` the hook fired after the FIRST group with a claim computed from that
// group alone; if a later group was impact-filtered the hook had been told `"true"`, and
// RunId dedupe meant the truthful TestRunCompleted could never correct it. A prefix can
// prove "partial" but never "full", so mid-run the only honest answer is `"unknown"`.
[<Fact(Timeout = SyncHangBackstopMs)>]
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

// AUTOMATION-1494. In a loaded CI run the mid-run probe never wrote its file, its status
// never became Completed, and the test was cancelled at its 20 s cap: nothing said why.
// A probe whose command cannot start ends Failed, and a harness that waits only for
// Completed sits out its whole budget instead of naming that failure.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an env probe whose command cannot start reports that failure, not a missing result`` () =
    let unstartable: ProbeCommand =
        fun script -> System.IO.Path.Combine(System.IO.Path.GetDirectoryName script, "no-such-probe"), ""

    let failure =
        Assert.ThrowsAny<exn>(fun () ->
            runEnvProbeAs unstartable false "env-unstartable" anyTestTrigger (fun host ->
                emitRunCompletedFor host [ "P", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ])
            |> ignore)

    test <@ not (failure :? TimeoutException) @>
    test <@ failure.Message.Contains "env-unstartable" @>
    test <@ failure.Message.Contains "Failed" @>

// The ordering that leaves a command unable to start in a parallel suite. The probe
// script is written just before it is run, and a process that forks while the script is
// open for writing gives its child a copy of that write handle until the child execs.
// Linux refuses to exec a file open for writing (ETXTBSY, "Text file busy"). The test
// holds the handle across the fire, so the ordering is certain rather than a race.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an env probe still reports what its command saw while the script is open for writing`` () =
    let contents =
        runEnvProbeAs probeCommand true "env-held-open" anyTestTrigger (fun host ->
            emitRunCompletedFor host [ "P", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ])

    test <@ contents = "true" @>

// --- A command refused as busy (ETXTBSY) is started again (AUTOMATION-1499) ---
// A user's afterTests script may be regenerated just before it runs. In a daemon that
// forks children concurrently, a child forked while the script was open for writing
// holds that write handle until it execs, and Linux refuses to exec a file open for
// writing. The refusal lasts only until that child execs, so the plugin starts the
// command again after a short pause instead of failing the run.

/// `ETXTBSY`, which Linux and macOS both number 26.
let private textFileBusy () =
    System.ComponentModel.Win32Exception(26, "Text file busy") :> exn

/// A plugin over `spawn` that records each pause, fired by one completed test run.
/// Returns the terminal status and the pauses taken.
let private fireWith (name: string) (spawn: Spawn) (pause: int -> unit) (command: string) (repoRoot: string) =
    let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot
    let pauses = System.Collections.Concurrent.ConcurrentQueue<int>()

    let handler =
        createWith
            spawn
            (fun ms ->
                pauses.Enqueue ms
                pause ms)
            (FsHotWatch.PluginFramework.PluginName.create name)
            anyTestTrigger
            command
            ""
            repoRoot
            None

    host.RegisterHandler(handler)
    let terminal = beginAwaitTerminal host name
    emitRunCompletedFor host [ "P", FsHotWatch.Events.TestsPassed("", false, TimeSpan.Zero) ]
    test <@ terminal.Wait HangWait @>
    terminal.Result, List.ofSeq pauses

/// A spawn that raises `refusal` on its first `refusals` calls, then runs the command.
let private refusingFirst (refusals: int) (refusal: unit -> exn) (calls: int ref) : Spawn =
    fun command args dir env bounds ->
        calls.Value <- calls.Value + 1

        if calls.Value <= refusals then
            raise (refusal ())
        else
            FsHotWatch.ProcessHelper.runProcess command args dir env bounds

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a command refused as busy is started again once the refusal passes`` () =
    let calls = ref 0

    let status, pauses =
        fireWith "busy-twice" (refusingFirst 2 textFileBusy calls) ignore "true" emptyRoot.Value

    test <@ calls.Value = 3 @>
    test <@ pauses.Length = 2 @>

    test
        <@
            match status with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a command that stays busy fails after a bounded number of tries`` () =
    let calls = ref 0

    let status, pauses =
        fireWith "busy-always" (refusingFirst Int32.MaxValue textFileBusy calls) ignore "true" emptyRoot.Value

    test <@ pauses.Length >= 1 @>
    test <@ calls.Value = pauses.Length + 1 @>

    test
        <@
            match status with
            | Failed(error, _, _) -> error.Contains "Text file busy"
            | _ -> false
        @>

/// A spawn failure that is not a busy refusal: another OS refusal, or a failure that is
/// not the OS's at all.
let private otherRefusal (name: string) : unit -> exn =
    match name with
    | "not-found" -> fun () -> System.ComponentModel.Win32Exception(2, "No such file or directory") :> exn
    | _ -> fun () -> InvalidOperationException "the spawn broke" :> exn

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("not-found")>]
[<InlineData("not-an-os-refusal")>]
let ``a command refused for any other reason is not started again`` (refusal: string) =
    let calls = ref 0

    let status, pauses =
        fireWith refusal (refusingFirst Int32.MaxValue (otherRefusal refusal) calls) ignore "true" emptyRoot.Value

    test <@ calls.Value = 1 @>
    test <@ List.isEmpty pauses @>

    test
        <@
            match status with
            | Failed _ -> true
            | _ -> false
        @>

// The real ordering: the script is still open for writing when the command fires, and the
// handle goes away during the plugin's first pause, as a forked child's copy does when the
// child execs. Linux refuses the first exec (ETXTBSY); elsewhere the first exec runs.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a script still open for writing when its command fires runs once the handle is gone`` () =
    withTempDir "busy-script" (fun dir ->
        let outFile = System.IO.Path.Combine(dir, "out")
        let script = writeEnvProbeScript dir outFile

        let held =
            new System.IO.FileStream(
                script,
                System.IO.FileMode.Append,
                System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite
            )

        try
            let status, _ =
                fireWith "busy-script" FsHotWatch.ProcessHelper.runProcess (fun _ -> held.Dispose()) script dir

            test
                <@
                    match status with
                    | Completed _ -> true
                    | _ -> false
                @>

            test <@ System.IO.File.ReadAllText outFile = "true" @>
        finally
            held.Dispose())

// A run that EXECUTED NOTHING must never tell a hook the full suite ran. Both degenerate
// lifecycles TestPrune builds — the aborted preflight and the "0 affected classes"
// impact-skip — carry `Results = Map.empty` with a vacuous full-suite claim. They are
// blocked only because `CommandTrigger.matches AnyTest` requires a non-empty results map
// and config rejects an empty `afterTests` list: an incidental guard nothing pinned.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``afterTests command does not fire for a run that executed nothing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "env-empty-run") trigger "echo" "ran" emptyRoot.Value None

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

[<Fact>]
let ``afterTests events are never cacheable across runs, whether filtered or full`` () =
    let trigger =
        { FilePattern = Some(fun _ -> true)
          AfterTests = Some AnyTest }

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "coverage-gate")
            trigger
            "coverage-gate"
            "counts.json"
            emptyRoot.Value
            None

    let key = handler.CacheKey.Value handler.Init

    let completed (verification: RunVerification) (results: (string * TestResult) list) : PluginEvent<unit> =
        TestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.ofList results
              Verification = verification }

    // A previous full run may have cached a RED from complete Cobertura. A later
    // filtered run publishes newer coverage.partial evidence that the command must
    // observe itself; replaying the old RED would skip that observation entirely.
    test <@ key (completed (Ran RunScope.FullSuite) [ "P", passed false ]) = None @>
    test <@ key (completed (Ran RunScope.Partial) [ "P", passed true ]) = None @>

    let progress (filtered: bool) : PluginEvent<unit> =
        TestProgress
            { RunId = Guid.NewGuid()
              NewResults = Map.ofList [ "P", passed filtered ] }

    // The plugin may fire from progressive deltas before TestRunCompleted. Both
    // breadths must bypass replay at that earlier seam too, or the stale RED can
    // survive without the completion event ever reaching the handler.
    test <@ key (progress false) = None @>
    test <@ key (progress true) = None @>

    // Pattern triggers keep their content cache. This positive control prevents
    // the fix from silently disabling the useful, separately-correct file cache.
    test <@ (key (FileChanged(SourceChanged [ "counts.json" ]))).IsSome @>

let private cacheKeyFnFor (command: string) (args: string) =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "ck-test")
            (fileTrigger (fun _ -> true))
            command
            args
            emptyRoot.Value
            None

    // Drive one run so the cold-start guard flips; CacheKey returns None until then.
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
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

    handler.CacheKey.Value handler.Init

[<Fact(Timeout = SyncHangBackstopMs)>]
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
                emptyRoot.Value
                None

        let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value
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

        handler.CacheKey.Value handler.Init

    let keyFnA = buildKeyFn ()
    let keyFnB = buildKeyFn ()
    let event = FileChanged(SourceChanged [ "trigger.txt" ])
    let kA = keyFnA event
    let kB = keyFnB event
    test <@ kA.IsSome @>
    test <@ kA = kB @>

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``cache key changes when the content of the file that matched the pattern changes`` () =
    // The defect: a FileChanged run was keyed by command, args and arg-file contents
    // only. The file that matched `pattern` is the input the command was fired for, and
    // was not in the key, so a breaking second edit replayed "succeeded (cached)".
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let watched = System.IO.Path.Combine(tmpDir, "schema.sql")

    try
        System.IO.File.WriteAllText(watched, "create table ok (id int);")
        let keyFn = cacheKeyFnFor "validate" "--all"
        let event = FileChanged(SourceChanged [ watched ])
        let k1 = keyFn event

        System.IO.File.WriteAllText(watched, "create table broken (")
        let k2 = keyFn event

        test <@ k1.IsSome @>
        test <@ k2.IsSome @>
        test <@ k1 <> k2 @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``a matched file that was deleted keys differently, and one that cannot be read gives no key`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let watched = System.IO.Path.Combine(tmpDir, "schema.sql")

    try
        System.IO.File.WriteAllText(watched, "create table ok (id int);")

        let present =
            computeFileChangedSaltWith (fun _ -> Some "h") tmpDir "validate" "" [ watched ]
        // Exists, but the read fails: nothing hashed the content, so nothing may replay.
        let unreadable =
            computeFileChangedSaltWith (fun _ -> None) tmpDir "validate" "" [ watched ]

        System.IO.File.Delete watched

        let absent =
            computeFileChangedSaltWith (fun _ -> Some "h") tmpDir "validate" "" [ watched ]

        test <@ present.IsSome @>
        test <@ unreadable = None @>
        test <@ absent.IsSome @>
        test <@ absent <> present @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``cache key changes when args change`` () =
    let keyFn1 = cacheKeyFnFor "echo" "alpha"
    let keyFn2 = cacheKeyFnFor "echo" "beta"
    let event = FileChanged(SourceChanged [ "trigger.txt" ])
    let k1 = keyFn1 event
    let k2 = keyFn2 event
    test <@ k1.IsSome @>
    test <@ k1 <> k2 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``argsStalerThan skips a token whose modification time cannot be read`` () =
    // A NUL in a path makes the stat throw; the token is skipped, not the whole list.
    test <@ List.isEmpty (argsStalerThan emptyRoot.Value "cfg\u0000.json" DateTime.UtcNow) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``events a file command does not act on leave its state alone`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "cmd")
            (fileTrigger (fun _ -> true))
            "echo"
            ""
            emptyRoot.Value
            None

    let ignored: PluginEvent<_> list =
        [ FileChecked Unchecked.defaultof<_>
          BatchChecked Unchecked.defaultof<_>
          BuildCompleted BuildSucceeded
          TestRunStarted Unchecked.defaultof<_>
          CommandCompleted Unchecked.defaultof<_>
          Custom Unchecked.defaultof<_> ]

    for event in ignored do
        let after =
            handler.Update Unchecked.defaultof<_> handler.Init event
            |> Async.RunSynchronously

        test <@ obj.ReferenceEquals(after, handler.Init) @>

// --- DI-injected error paths ---
// hashFileWith and Update's defensive arms are reached by injection rather than real OS
// errors. The integration suite confirms the injected behaviour matches reality — without
// that positive control these would be assertions about a mock.

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``hashFileWith returns None when reader throws IOException`` () =
    let throwing _ =
        raise (System.IO.IOException("simulated read failure"))

    let result = hashFileWith throwing "/any/path"
    test <@ result = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``hashFileWith returns None when reader throws UnauthorizedAccessException`` () =
    // It does not derive from IOException, so the narrow catch has to name it
    // explicitly — a chmod-000 file is expected, not a bug.
    let throwing _ =
        raise (System.UnauthorizedAccessException("denied"))

    let result = hashFileWith throwing "/any/path"
    test <@ result = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``hashFileWith returns Some hex for successful read`` () =
    let constReader (_: string) =
        System.Text.Encoding.UTF8.GetBytes("hello")

    let result = hashFileWith constReader "/any/path"
    // sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
    test <@ result = Some "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824" @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``Update is a no-op for FileChanged when trigger has no FilePattern`` () =
    // Subscriptions already filter this out before dispatch, so Update is invoked
    // directly to reach the safety net at all.
    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "no-pattern") trigger "echo" "hi" emptyRoot.Value None

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
          RepoRoot = emptyRoot.Value
          Post = fun _ -> ()
          EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          Processes = None
          RunExclusiveShared = fun _ _ _ _ _ -> FsHotWatch.PluginFramework.SharedClaimed
          SlotHolder = fun _ -> FsHotWatch.PluginFramework.SlotHolder.Free
          DeclareBoundedWork = FsHotWatch.PluginFramework.BoundedWork.undeclared
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let initialState = handler.Init
    let event = FileChanged(SourceChanged [ "anything.fs" ])

    let nextState = handler.Update ctx initialState event |> Async.RunSynchronously

    test <@ nextState = initialState @>

[<Fact(Timeout = SyncHangBackstopMs)>]
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

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a command that exceeds its timeout reports Failed with a timed-out verdict`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) emptyRoot.Value

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "slow-cmd")
            (fileTrigger (fun _ -> true))
            "sh"
            "-c \"sleep 30\""
            emptyRoot.Value
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
// FILE-COMMAND's own cold-vs-cached ledger parity
// ---------------------------------------------------------------------------

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a cached file-command replay leaves an out-of-batch finding standing`` () =
    // Same shape as the build control, different plugin: FileCommandPlugin reports
    // and clears exactly one pseudo-file, `<run-scripts>`, and never calls
    // ClearAllErrors. Its cache key is whole-run (`File = None`), which is precisely
    // the combination the deleted blanket `ClearPlugin` was destructive for — a
    // per-file ledger under a whole-run key.
    let cache =
        FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

    let host = PluginHost(Unchecked.defaultof<_>, emptyRoot.Value, taskCache = cache)

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
            emptyRoot.Value
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

/// A FileChanged cache key over `root` for a `*.sql` trigger running `args`, with the
/// cold-start guard flipped by one real run.
let private sqlKeyFn (root: string) (args: string) =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "sql-check")
            (fileTrigger (fun path -> path.EndsWith ".sql"))
            "true"
            args
            root
            None

    let host = PluginHost.create (Unchecked.defaultof<_>) root
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ System.IO.Path.Combine(root, "warmup.sql") ])

    waitUntil
        (fun () ->
            match host.GetStatus("sql-check") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        5000
    |> ignore

    handler.CacheKey.Value handler.Init

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a pattern file edited in an earlier event still moves the key of a later one`` () =
    // The defect (AUTOMATION-1557, directory args): keying only the event's own files
    // let `lint migrations/` replay a pass. b.sql = X passes; a.sql is broken (its run
    // fails); b.sql goes to Y and back to X — the key for b = X replayed the pass while
    // a.sql was still broken.
    withTempDir "fc-dir-arg" (fun root ->
        let migrations = System.IO.Path.Combine(root, "migrations")
        System.IO.Directory.CreateDirectory(migrations) |> ignore
        let a = System.IO.Path.Combine(migrations, "a.sql")
        let b = System.IO.Path.Combine(migrations, "b.sql")
        System.IO.File.WriteAllText(a, "good")
        System.IO.File.WriteAllText(b, "X")

        let keyFn = sqlKeyFn root "migrations/"
        let eventB = FileChanged(SourceChanged [ b ])
        let before = keyFn eventB

        System.IO.File.WriteAllText(a, "broken")
        let after = keyFn eventB

        test <@ before.IsSome && after.IsSome @>
        test <@ before <> after @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file a directory or glob argument names moves the key though no pattern matches it`` () =
    withTempDir "fc-glob-arg" (fun root ->
        let docs = System.IO.Path.Combine(root, "docs")
        System.IO.Directory.CreateDirectory(docs) |> ignore
        let notes = System.IO.Path.Combine(docs, "notes.txt")
        let trigger = System.IO.Path.Combine(root, "t.sql")
        System.IO.File.WriteAllText(notes, "one")
        System.IO.File.WriteAllText(trigger, "t")
        let event = FileChanged(SourceChanged [ trigger ])

        for args in [ "docs"; "docs/*.txt" ] do
            let keyFn = sqlKeyFn root args
            System.IO.File.WriteAllText(notes, "one")
            let before = keyFn event
            System.IO.File.WriteAllText(notes, "two")
            let after = keyFn event
            test <@ before <> after @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the key's input listing is walked once and kept current from the events`` () =
    // Measured on a 3,776-file repository: walking it for pattern files on every
    // FileChanged event cost a median 534 ms (AUTOMATION-1557). The listing is walked once
    // per scope and then follows the paths events name: an added file joins it, a deleted
    // one leaves, and nothing is walked again.
    withTempDir "fc-listing" (fun root ->
        let migrations = System.IO.Path.Combine(root, "migrations")
        System.IO.Directory.CreateDirectory(migrations) |> ignore
        let a = System.IO.Path.Combine(root, "a.sql")
        let m1 = System.IO.Path.Combine(migrations, "001.txt")
        System.IO.File.WriteAllText(a, "a")
        System.IO.File.WriteAllText(m1, "1")

        let walks = ref 0

        let walk (dir: string) =
            walks.Value <- walks.Value + 1

            FsHotWatch.SafeWalk.bestEffortFilePaths FsHotWatch.SafeWalk.SourceExcludedDirs "*" dir
            |> List.ofSeq

        let listing =
            InputListing([ root, (fun p -> p.EndsWith ".sql"); migrations, (fun _ -> true) ], walk)

        let first = listing.Files() |> Set.ofList
        test <@ first = set [ a; m1 ] @>
        test <@ walks.Value = 2 @>

        let b = System.IO.Path.Combine(root, "b.sql")
        let m2 = System.IO.Path.Combine(migrations, "002.txt")
        System.IO.File.WriteAllText(b, "b")
        System.IO.File.WriteAllText(m2, "2")
        System.IO.File.Delete a
        listing.Observe(SourceChanged [ b; m2; a; System.IO.Path.Combine(root, "notes.md") ])

        test <@ (listing.Files() |> Set.ofList) = set [ b; m1; m2 ] @>
        test <@ walks.Value = 2 @>

        // A solution change is a reason to look again.
        listing.Observe SolutionChanged
        listing.Files() |> ignore
        test <@ walks.Value = 4 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file of an unwatched type in a directory argument still moves the key when edited after the walk`` () =
    // The listing caches WHICH files are in a scope, not their content: every event
    // re-hashes every listed file. So `lint migrations/` with `migrations/.sqlfluff`
    // edited after the walk (a type the watcher never reports) keys differently on the
    // next event, which names only an .sql file.
    withTempDir "fc-unwatched-type" (fun root ->
        let migrations = System.IO.Path.Combine(root, "migrations")
        System.IO.Directory.CreateDirectory(migrations) |> ignore
        let b = System.IO.Path.Combine(migrations, "b.sql")
        let rules = System.IO.Path.Combine(migrations, ".sqlfluff")
        let settings = System.IO.Path.Combine(migrations, "lint.yml")
        System.IO.File.WriteAllText(b, "X")
        System.IO.File.WriteAllText(rules, "[sqlfluff]\ndialect = postgres\n")
        System.IO.File.WriteAllText(settings, "rules: strict\n")

        let keyFn = sqlKeyFn root "migrations/"
        let eventB = FileChanged(SourceChanged [ b ])
        let before = keyFn eventB

        System.IO.File.WriteAllText(rules, "[sqlfluff]\ndialect = ansi\n")
        let afterRules = keyFn eventB

        System.IO.File.WriteAllText(settings, "rules: lax\n")
        let afterSettings = keyFn eventB

        test <@ before.IsSome && afterRules.IsSome && afterSettings.IsSome @>
        test <@ before <> afterRules @>
        test <@ afterRules <> afterSettings @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a glob argument with no directory reaches the repository, and a directory outside it is never walked`` () =
    withTempDir "fc-glob-root" (fun root ->
        let notes = System.IO.Path.Combine(root, "notes.txt")
        let trigger = System.IO.Path.Combine(root, "t.sql")
        System.IO.File.WriteAllText(notes, "one")
        System.IO.File.WriteAllText(trigger, "t")
        let event = FileChanged(SourceChanged [ trigger ])

        // `*.txt` has no directory: its prefix is the repository root.
        let globKey = sqlKeyFn root "*.txt"
        let before = globKey event
        System.IO.File.WriteAllText(notes, "two")
        let after = globKey event
        test <@ before <> after @>

        // `/` is a directory, outside the repository: no walk of the machine, and a key.
        let outside = sqlKeyFn root "/" event
        test <@ outside.IsSome @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a glob argument whose directory does not exist yet still keys`` () =
    withTempDir "fc-glob-missing" (fun root ->
        let trigger = System.IO.Path.Combine(root, "t.sql")
        System.IO.File.WriteAllText(trigger, "t")
        let event = FileChanged(SourceChanged [ trigger ])
        let missing = sqlKeyFn root "later/*.txt" event
        test <@ missing.IsSome @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a key reads the files the event names and asks the content hasher for the rest`` () =
    // Every listed file was re-read on every event: a `**/*` pattern over a large
    // repository paid a read of the whole tree per event (AUTOMATION-1557). A file the
    // event names is read fresh; every other one goes to the core FileContentHasher, which
    // re-reads only when the file's stamp (change time included) moves
    // (CheckCacheTests covers that hasher, mtime-restored rewrites included).
    let reads = System.Collections.Generic.List<string>()
    let hashed = System.Collections.Generic.List<string>()

    let digests =
        DigestCache(
            (fun path ->
                reads.Add path
                Some("read:" + path)),
            (fun path ->
                hashed.Add path

                match path with
                | "gone.sql" -> "missing"
                | "locked.sql" -> "unreadable"
                | _ -> "hash:" + path)
        )

    test <@ digests.Digest("a.sql", true) = Some "read:a.sql" @>
    test <@ digests.Digest("b.sql", false) = Some "hash:b.sql" @>
    test <@ List.ofSeq reads = [ "a.sql" ] && List.ofSeq hashed = [ "b.sql" ] @>

    // The hasher's "missing" and "unreadable" go to the reader, which answers for them.
    test <@ digests.Digest("gone.sql", false) = Some "read:gone.sql" @>
    test <@ digests.Digest("locked.sql", false) = Some "read:locked.sql" @>

[<Fact>]
let ``a listing past the large-listing bound is named in a warning`` () =
    test <@ largeListingWarning "lint-everything" LargeListingThreshold = None @>

    match largeListingWarning "lint-everything" (LargeListingThreshold + 1) with
    | Some message ->
        test <@ message.Contains "lint-everything" @>
        test <@ message.Contains(string (LargeListingThreshold + 1)) @>
    | None -> failwith "a listing past the bound must warn"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a relative path in an event is read under the repository, never under the process's working directory`` () =
    // Seen on Linux CI: an event naming `scripts/build.fsx` was resolved against the
    // test host's CURRENT directory, which other tests move, so two identical events
    // keyed differently and a cached replay was missed. The repository is the only root.
    withTempDir "fc-relative-event" (fun root ->
        let scripts = System.IO.Path.Combine(root, "scripts")
        System.IO.Directory.CreateDirectory(scripts) |> ignore
        let script = System.IO.Path.Combine(scripts, "build.sql")
        System.IO.File.WriteAllText(script, "one")

        let keyFn = sqlKeyFn root ""
        let relative = keyFn (FileChanged(SourceChanged [ "scripts/build.sql" ]))
        let absolute = keyFn (FileChanged(SourceChanged [ script ]))
        // One file, one key, however the event spells it.
        test <@ relative = absolute @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a listed file rewritten to other bytes of the same size, its mtime restored, moves the key`` () =
    // ADR-008: mtime is not a content oracle. A digest cached on (size, mtime) kept the
    // old digest when a same-size rewrite restored the last-write time (`cp -p`,
    // `rsync -t`, a checkout, a formatter that restores times), and the key replayed
    // "succeeded (cached)" over changed bytes. The event names ANOTHER file, so the
    // listed one is not forced to a fresh read.
    withTempDir "fc-mtime-restored" (fun root ->
        let a = System.IO.Path.Combine(root, "a.sql")
        let b = System.IO.Path.Combine(root, "b.sql")
        System.IO.File.WriteAllText(a, "good")
        System.IO.File.WriteAllText(b, "b")
        let written = System.IO.File.GetLastWriteTimeUtc a

        let keyFn = sqlKeyFn root ""
        let eventB = FileChanged(SourceChanged [ b ])
        let before = keyFn eventB

        System.IO.File.WriteAllText(a, "evil")
        System.IO.File.SetLastWriteTimeUtc(a, written)
        let after = keyFn eventB

        test <@ before.IsSome && after.IsSome @>
        test <@ before <> after @>)
