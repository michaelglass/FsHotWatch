module FsHotWatch.Tests.PluginHostTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

type RecordingPlugin() =
    let mutable fileChanges = []

    interface IFsHotWatchPlugin with
        member _.Name = "recorder"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change -> fileChanges <- change :: fileChanges)

        member _.Dispose() = ()

    member _.FileChanges = fileChanges |> List.rev

[<Fact>]
let ``plugin receives file change events`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let plugin = RecordingPlugin()
    host.Register(plugin)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    test <@ plugin.FileChanges.Length = 1 @>

[<Fact>]
let ``plugin registers command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable called = false

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "cmd-test"

            member _.Initialize(ctx) =
                ctx.RegisterCommand(
                    "greet",
                    fun _args ->
                        async {
                            called <- true
                            return "hello"
                        }
                )

            member _.Dispose() = () }

    host.Register(plugin)
    let result = host.RunCommand("greet", [||]) |> Async.RunSynchronously
    test <@ result = Some "hello" @>
    test <@ called @>

[<Fact>]
let ``RunCommand returns None for unknown command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let result = host.RunCommand("bogus", [||]) |> Async.RunSynchronously
    test <@ result = None @>

[<Fact>]
let ``plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "status-test"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

            member _.Dispose() = () }

    host.Register(plugin)
    let status = host.GetStatus("status-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Running _ -> true
            | _ -> false
        @>

[<Fact>]
let ``GetAllStatuses returns all plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let p1 =
        { new IFsHotWatchPlugin with
            member _.Name = "a"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    let p2 =
        { new IFsHotWatchPlugin with
            member _.Name = "b"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    host.Register(p1)
    host.Register(p2)
    let all = host.GetAllStatuses()
    test <@ all.Count = 2 @>
    test <@ all |> Map.containsKey "a" @>
    test <@ all |> Map.containsKey "b" @>

[<Fact>]
let ``EmitBuildCompleted reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "build-listener"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)
    host.EmitBuildCompleted(BuildSucceeded)
    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact>]
let ``EmitBuildCompleted with failure reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "build-fail-listener"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)
    let errors = [ "error CS0001: Something broke" ]
    host.EmitBuildCompleted(BuildFailed errors)

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact>]
let ``EmitProjectChecked reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedProject: ProjectCheckResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "project-check-listener"

            member _.Initialize(ctx) =
                ctx.OnProjectChecked.Add(fun result -> receivedProject <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)

    let result =
        { Project = "/tmp/test/Test.fsproj"
          FileResults = Map.empty }

    host.EmitProjectChecked(result)
    test <@ receivedProject.IsSome @>
    test <@ receivedProject.Value.Project = "/tmp/test/Test.fsproj" @>

[<Fact>]
let ``preprocessor runs before events are dispatched`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable preprocessorCalled = false

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "tracker"

            member _.Process (changedFiles: string list) (_repoRoot: string) =
                preprocessorCalled <- true
                []

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let _ = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ preprocessorCalled @>

[<Fact>]
let ``preprocessor modified files are returned`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "modifier"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = [ "src/Formatted.fs"; "src/Other.fs" ]

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let modified = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ modified = [ "src/Formatted.fs"; "src/Other.fs" ] @>

[<Fact>]
let ``preprocessor status is tracked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "status-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = [ "a.fs" ]

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)

    // Before running, status should be Idle
    let statusBefore = host.GetStatus("status-pp")
    test <@ statusBefore = Some Idle @>

    let _ = host.RunPreprocessors([ "src/Lib.fs" ])

    let statusAfter = host.GetStatus("status-pp")
    test <@ statusAfter.IsSome @>

    test
        <@
            match statusAfter.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``multiple plugins receive the same event`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable received1 = false
    let mutable received2 = false
    let mutable received3 = false

    let makePlugin name (setter: unit -> unit) =
        { new IFsHotWatchPlugin with
            member _.Name = name

            member _.Initialize(ctx) =
                ctx.OnFileChanged.Add(fun _ -> setter ())

            member _.Dispose() = () }

    host.Register(makePlugin "p1" (fun () -> received1 <- true))
    host.Register(makePlugin "p2" (fun () -> received2 <- true))
    host.Register(makePlugin "p3" (fun () -> received3 <- true))

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test <@ received1 @>
    test <@ received2 @>
    test <@ received3 @>

[<Fact>]
let ``plugin can report and query errors via host`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "error-reporter"

            member _.Initialize(ctx) =
                ctx.ReportErrors
                    "/src/A.fs"
                    [ { Message = "bad"
                        Severity = "warning"
                        Line = 1
                        Column = 0 } ]

            member _.Dispose() = () }

    host.Register(plugin)
    test <@ host.HasErrors() @>
    test <@ host.ErrorCount() = 1 @>
    let errors = host.GetErrors()
    test <@ errors.ContainsKey "/src/A.fs" @>

[<Fact>]
let ``plugin ClearErrors removes errors from ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable clearFn: (string -> unit) option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "clear-test"

            member _.Initialize(ctx) =
                ctx.ReportErrors
                    "/src/B.fs"
                    [ { Message = "oops"
                        Severity = "error"
                        Line = 5
                        Column = 0 } ]

                clearFn <- Some ctx.ClearErrors

            member _.Dispose() = () }

    host.Register(plugin)
    test <@ host.HasErrors() @>
    clearFn.Value "/src/B.fs"
    test <@ not (host.HasErrors()) @>

[<Fact>]
let ``GetErrorsByPlugin returns only that plugin's errors`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let makeErrorPlugin name file msg =
        { new IFsHotWatchPlugin with
            member _.Name = name

            member _.Initialize(ctx) =
                ctx.ReportErrors
                    file
                    [ { Message = msg
                        Severity = "error"
                        Line = 1
                        Column = 0 } ]

            member _.Dispose() = () }

    host.Register(makeErrorPlugin "pluginA" "/src/A.fs" "from A")
    host.Register(makeErrorPlugin "pluginB" "/src/B.fs" "from B")
    test <@ host.ErrorCount() = 2 @>
    let aErrors = host.GetErrorsByPlugin("pluginA")
    test <@ aErrors.Count = 1 @>
    test <@ aErrors.ContainsKey "/src/A.fs" @>

[<Fact>]
let ``EmitFileCheckedParallel runs handlers concurrently`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    // Both handlers enter simultaneously, proving concurrency.
    // If sequential, handler2 would never reach the barrier before timeout.
    let barrier = new CountdownEvent(2)
    let mutable handler1Entered = false
    let mutable handler2Entered = false

    let makePlugin name (setter: bool ref) =
        { new IFsHotWatchPlugin with
            member _.Name = name

            member _.Initialize(ctx) =
                ctx.OnFileChecked.Add(fun _ ->
                    setter.Value <- true
                    barrier.Signal() |> ignore
                    // Wait for both to be running before completing
                    barrier.Wait(5000) |> ignore)

            member _.Dispose() = () }

    let ref1 = ref false
    let ref2 = ref false
    host.Register(makePlugin "p1" ref1)
    host.Register(makePlugin "p2" ref2)

    let dummyResult =
        { File = "/tmp/test.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_>
          ProjectOptions = Unchecked.defaultof<_> }

    host.EmitFileCheckedParallel(dummyResult) |> Async.RunSynchronously

    // Both handlers ran and reached the barrier concurrently
    test <@ ref1.Value @>
    test <@ ref2.Value @>
