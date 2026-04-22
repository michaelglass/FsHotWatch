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

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fileTrigger (fun f -> f.EndsWith(".fsx")))
            "echo"
            "hello"
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
        create (FsHotWatch.PluginFramework.PluginName.create "fail-cmd") (fileTrigger (fun _ -> true)) "false" "" None

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
let ``afterTests TestProjects fires when a listed project has results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "Intelligence.Tests.Unit" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-listed") trigger "echo" "ran" None

    host.RegisterHandler(handler)

    let results: FsHotWatch.Events.TestResults =
        { Results =
            Map.ofList
                [ "Intelligence.Tests.Unit", FsHotWatch.Events.TestsPassed ""
                  "Other", FsHotWatch.Events.TestsPassed "" ]
          Elapsed = System.TimeSpan.Zero }

    host.EmitTestCompleted(results)

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
let ``afterTests TestProjects does not fire when no listed project matches`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some(TestProjects(Set.ofList [ "Intelligence.Tests.Unit" ])) }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-miss") trigger "echo" "ran" None

    host.RegisterHandler(handler)

    let results: FsHotWatch.Events.TestResults =
        { Results = Map.ofList [ "Intelligence.Tests.Integration", FsHotWatch.Events.TestsPassed "" ]
          Elapsed = System.TimeSpan.Zero }

    host.EmitTestCompleted(results)

    // Command must NOT run — poll briefly for Completed/Failed; will time out at Idle (expected)
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

// Regression: end-to-end from parseConfig(.fs-hot-watch.json) → daemon registration
// path → TestCompleted dispatch. The earlier unit tests built the CommandTrigger
// inline and hit the plugin the same way the daemon's RegisterHandler path does,
// but a bug in the config→trigger glue (e.g. parser yielding AfterTests = None
// for a valid JSON list) would not be caught without going through parseConfig.
[<Fact(Timeout = 5000)>]
let ``parseConfig + registration + TestCompleted fires coverage-ratchet-style plugin`` () =
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
          LogDir = "logs" }

    let config = FsHotWatch.Cli.DaemonConfig.parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    let fc = config.FileCommands.[0]
    test <@ fc.Name = Some "cov-r" @>
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
        create (FsHotWatch.PluginFramework.PluginName.create "cov-r") trigger fc.Command fc.Args None

    // The plugin must subscribe to TestCompleted — if this assertion fails,
    // dispatch will never route events to Update.
    test
        <@ handler.Subscriptions |> Set.contains FsHotWatch.PluginFramework.SubscribeTestCompleted @>

    host.RegisterHandler(handler)

    let results: FsHotWatch.Events.TestResults =
        { Results = Map.ofList [ "ProjA", FsHotWatch.Events.TestsPassed "" ]
          Elapsed = System.TimeSpan.Zero }

    host.EmitTestCompleted(results)

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
let ``afterTests AnyTest fires on TestCompleted regardless of projects`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let trigger =
        { FilePattern = None
          AfterTests = Some AnyTest }

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "afterTests-any") trigger "echo" "ran" None

    host.RegisterHandler(handler)

    let results: FsHotWatch.Events.TestResults =
        { Results = Map.ofList [ "AnyProject", FsHotWatch.Events.TestsPassed "" ]
          Elapsed = System.TimeSpan.Zero }

    host.EmitTestCompleted(results)

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
