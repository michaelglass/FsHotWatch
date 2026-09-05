[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.PluginHostTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact(Timeout = 20000)>]
let ``plugin receives file change events`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable fileChanges: FileChangeKind list = []

    let handler =
        { Name = PluginName.create "recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged c -> fileChanges <- c :: fileChanges
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> fileChanges.Length >= 1) 12000
    test <@ fileChanges.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``plugin registers command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "cmd-test"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = [ "greet", fun _ctx _state _args -> async { return "hello" } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    let result = host.RunCommand("greet", [||]) |> Async.RunSynchronously
    test <@ result = Some "hello" @>

[<Fact(Timeout = 15000)>]
let ``RunCommand returns None for unknown command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let result = host.RunCommand("bogus", [||]) |> Async.RunSynchronously
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "status-test"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> (host.GetStatus("status-test")) <> Some Idle) 12000

    let status = host.GetStatus("status-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Running _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``GetAllStatuses returns all plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let makeHandler name =
        { Name = PluginName.create name
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "a")
    host.RegisterHandler(makeHandler "b")
    let all = host.GetAllStatuses()
    test <@ all.Count = 2 @>
    test <@ all |> Map.containsKey "a" @>
    test <@ all |> Map.containsKey "b" @>

[<Fact(Timeout = 20000)>]
let ``EmitBuildCompleted reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let handler =
        { Name = PluginName.create "build-listener"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitBuildCompleted(BuildSucceeded)
    waitUntil (fun () -> receivedBuild.IsSome) 12000
    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact(Timeout = 20000)>]
let ``EmitBuildCompleted with failure reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let handler =
        { Name = PluginName.create "build-fail-listener"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    let errors = [ "error CS0001: Something broke" ]
    host.EmitBuildCompleted(BuildFailed errors)
    waitUntil (fun () -> receivedBuild.IsSome) 12000

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``preprocessor runs before events are dispatched`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable preprocessorCalled = false

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "tracker"

            member _.Process (changedFiles: string list) (_repoRoot: string) =
                preprocessorCalled <- true

                Ok
                    { Modified = []
                      Considered = changedFiles.Length
                      Evidence = "tracker" }

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let _ = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ preprocessorCalled @>

[<Fact(Timeout = 15000)>]
let ``preprocessor modified files are returned`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "modifier"

            member _.Process (_changedFiles: string list) (_repoRoot: string) =
                Ok
                    { Modified = [ "src/Formatted.fs"; "src/Other.fs" ]
                      Considered = 1
                      Evidence = "modifier v1" }

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let run = host.RunPreprocessors([ "src/Lib.fs" ])
    test <@ run.Modified = [ "src/Formatted.fs"; "src/Other.fs" ] @>
    // The evidence line is the reply `fshw format` prints: what ran, over how many.
    test <@ run.Lines = [ "modifier: rewrote 2 of 1 file(s) — modifier v1" ] @>
    test <@ run.Evidence = [ "modifier v1" ] @>
    test <@ List.isEmpty run.Refused @>

[<Fact(Timeout = 15000)>]
let ``a preprocessor that cannot run is a Failed status and a refusal, never "none rewritten"`` () =
    // AUTOMATION-447: the formatter's pin was missing and the pass returned an empty
    // list, which the host summarised as "12 file(s) checked, none rewritten" — the
    // same words as a clean tree. A pass that did not run must say so on every surface.
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "unpinned"

            member _.Process (_changedFiles: string list) (_repoRoot: string) =
                Result.Error "no fantomas pin in the manifest"

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let run = host.RunPreprocessors([ "src/Lib.fs" ])

    test <@ List.isEmpty run.Modified @>
    test <@ List.isEmpty run.Lines @>
    test <@ List.isEmpty run.Evidence @>
    test <@ run.Refused = [ "unpinned", "no fantomas pin in the manifest" ] @>

    test
        <@
            match host.GetStatus("unpinned") with
            | Some(Failed(reason, _, verdict)) ->
                reason = "no fantomas pin in the manifest"
                && verdict.Summary = "unpinned refused: no fantomas pin in the manifest"
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``preprocessor status is tracked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "status-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) =
                Ok
                    { Modified = [ "a.fs" ]
                      Considered = 1
                      Evidence = "status-pp" }

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)

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

[<Fact(Timeout = 20000)>]
let ``multiple plugins receive the same event`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable received1 = false
    let mutable received2 = false
    let mutable received3 = false

    let makeHandler name (setter: unit -> unit) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> setter ()
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "p1" (fun () -> received1 <- true))
    host.RegisterHandler(makeHandler "p2" (fun () -> received2 <- true))
    host.RegisterHandler(makeHandler "p3" (fun () -> received3 <- true))

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil (fun () -> received1 && received2 && received3) 12000
    test <@ received1 @>
    test <@ received2 @>
    test <@ received3 @>

[<Fact(Timeout = 20000)>]
let ``plugin can report and query errors via host`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "error-reporter"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportErrors
                            "/src/A.fs"
                            [ { Message = "bad"
                                Severity = DiagnosticSeverity.Warning
                                Line = 1
                                Column = 0
                                Detail = None } ]
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 12000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

    let errors = host.GetErrors()
    test <@ errors.ContainsKey "/src/A.fs" @>

[<Fact(Timeout = 20000)>]
let ``plugin ClearErrors removes errors from ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "clear-test"
          Init = false
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ when not state ->
                        ctx.ReportErrors
                            "/src/B.fs"
                            [ { Message = "oops"
                                Severity = DiagnosticSeverity.Error
                                Line = 5
                                Column = 0
                                Detail = None } ]

                        return true
                    | FileChanged _ when state ->
                        ctx.ClearErrors "/src/B.fs"
                        return state
                    | _ -> return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 12000
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitUntil (fun () -> not (host.HasFailingReasons(warningsAreFailures = true))) 12000
    test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 20000)>]
let ``GetErrorsByPlugin returns only that plugin's errors`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "pluginA",
        "/src/A.fs",
        [ { Message = "from A"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ]
    )

    host.ReportErrors(
        "pluginB",
        "/src/B.fs",
        [ { Message = "from B"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ]
    )

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 2
        @>

    let aErrors = host.GetErrorsByPlugin("pluginA")
    test <@ aErrors.Count = 1 @>
    test <@ aErrors.ContainsKey "/src/A.fs" @>

[<Fact(Timeout = 20000)>]
let ``EmitFileChecked dispatches to framework plugin handlers`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let ref1 = ref false
    let ref2 = ref false

    let makeHandler name (r: bool ref) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChecked _ -> r.Value <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeHandler "p1" ref1)
    host.RegisterHandler(makeHandler "p2" ref2)

    let dummyResult =
        { File = AbsFilePath.create "/tmp/test.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    host.EmitFileChecked(dummyResult)

    waitUntil (fun () -> ref1.Value && ref2.Value) 12000
    test <@ ref1.Value @>
    test <@ ref2.Value @>

// ----------------------------------------------------------------------------
// Live "checked files" coverage set. `EmitFileChecked` is the choke point both the cold scan
// and the incremental path flow through: a FULL check adds the file, ParseOnly does not, and
// `EmitFileChanged` removes it (an edited-but-not-yet-rechecked file correctly counts as
// unchecked). Daemon.fs's `GetUncheckedCount` is `registered minus checked`.
// ----------------------------------------------------------------------------

let private fullCheckResult (file: string) : FileCheckResult =
    { File = AbsFilePath.create file
      Source = ""
      ParseResults = Unchecked.defaultof<_>
      CheckResults = FullCheck(Unchecked.defaultof<_>)
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

let private parseOnlyResult (file: string) : FileCheckResult =
    { File = AbsFilePath.create file
      Source = ""
      ParseResults = Unchecked.defaultof<_>
      CheckResults = ParseOnly
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

[<Fact(Timeout = 15000)>]
let ``EmitFileChecked with FullCheck marks the file checked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let f = AbsFilePath.create "/tmp/cov/A.fs"
    test <@ not (host.IsFileChecked f) @>
    host.EmitFileChecked(fullCheckResult "/tmp/cov/A.fs")
    test <@ host.IsFileChecked f @>

[<Fact(Timeout = 15000)>]
let ``EmitFileChecked with ParseOnly does NOT mark the file checked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let f = AbsFilePath.create "/tmp/cov/B.fs"
    host.EmitFileChecked(parseOnlyResult "/tmp/cov/B.fs")
    test <@ not (host.IsFileChecked f) @>

[<Fact(Timeout = 15000)>]
let ``EmitFileChanged removes a previously-checked file from the set`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let f = AbsFilePath.create "/tmp/cov/C.fs"
    host.EmitFileChecked(fullCheckResult "/tmp/cov/C.fs")
    test <@ host.IsFileChecked f @>
    host.EmitFileChanged(SourceChanged [ "/tmp/cov/C.fs" ])
    test <@ not (host.IsFileChecked f) @>

[<Fact(Timeout = 15000)>]
let ``ProjectChanged removes the changed file from the checked set`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let f = AbsFilePath.create "/tmp/cov/D.fs"
    host.EmitFileChecked(fullCheckResult "/tmp/cov/D.fs")
    test <@ host.IsFileChecked f @>
    host.EmitFileChanged(ProjectChanged [ "/tmp/cov/D.fs" ])
    test <@ not (host.IsFileChecked f) @>

[<Fact(Timeout = 15000)>]
let ``SolutionChanged clears the entire checked set`` () =
    // A solution-level change can retarget every file's options, so nothing is known-checked
    // afterward and removed files must not linger in the set.
    let host = PluginHost.create nullChecker "/tmp/test"
    let a = AbsFilePath.create "/tmp/cov/S1.fs"
    let b = AbsFilePath.create "/tmp/cov/S2.fs"
    host.EmitFileChecked(fullCheckResult "/tmp/cov/S1.fs")
    host.EmitFileChecked(fullCheckResult "/tmp/cov/S2.fs")
    test <@ host.IsFileChecked a && host.IsFileChecked b @>
    host.EmitFileChanged(SolutionChanged)
    test <@ not (host.IsFileChecked a) && not (host.IsFileChecked b) @>
    test <@ host.CheckedFileCount() = 0 @>

[<Fact(Timeout = 15000)>]
let ``unchecked count = registered minus checked`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let registered =
        [ AbsFilePath.create "/tmp/cov/E1.fs"
          AbsFilePath.create "/tmp/cov/E2.fs"
          AbsFilePath.create "/tmp/cov/E3.fs" ]

    let unchecked () =
        registered |> List.filter (host.IsFileChecked >> not) |> List.length

    test <@ unchecked () = 3 @>
    host.EmitFileChecked(fullCheckResult "/tmp/cov/E1.fs")
    host.EmitFileChecked(fullCheckResult "/tmp/cov/E2.fs")
    test <@ unchecked () = 1 @>
    host.EmitFileChecked(fullCheckResult "/tmp/cov/E3.fs")
    test <@ unchecked () = 0 @>

[<Fact(Timeout = 15000)>]
let ``preprocessor exception sets Failed status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "boom-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) = failwith "preprocessor kaboom"

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    let run = host.RunPreprocessors([ "src/Lib.fs" ])

    test <@ run.Modified |> List.isEmpty @>
    test <@ run.Refused = [ "boom-pp", "preprocessor kaboom" ] @>

    let status = host.GetStatus("boom-pp")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed(msg, _, _) -> msg.Contains("preprocessor kaboom")
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``preprocessor exception sets Failed with ex.ToString() (type+stack), not just ex.Message`` () =
    // Same invariant as PluginFramework.safeUpdate: the preprocessor failure path records
    // the full exception string, so diagnosing does not mean grepping daemon.log.
    let host = PluginHost.create nullChecker "/tmp/test"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "boom-detail-pp"

            member _.Process (_changedFiles: string list) (_repoRoot: string) =
                raise (System.InvalidOperationException("pp-kaboom-distinctive"))

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)
    host.RunPreprocessors([ "src/Lib.fs" ]) |> ignore

    let status = host.GetStatus("boom-detail-pp")
    test <@ status.IsSome @>

    let msg =
        match status.Value with
        | Failed(m, _, _) -> m
        | _ -> ""

    test <@ msg.Contains("pp-kaboom-distinctive") @>
    test <@ msg.Contains("InvalidOperationException") @>

[<Fact(Timeout = 20000)>]
let ``ReportErrors with version passes through to ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "v2 error"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 2L
    )

    // A report at an older version is ignored, so the v2 entry survives.
    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "v1 stale"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 1L
    )

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

    let errors = host.GetErrors()
    let fileErrors = errors.["/src/A.fs"]
    test <@ (snd fileErrors.[0]).Message = "v2 error" @>

[<Fact(Timeout = 15000)>]
let ``ClearErrors with version passes through to ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "fcs",
        "/src/A.fs",
        [ { Message = "error"
            Severity = DiagnosticSeverity.Error
            Line = 1
            Column = 0
            Detail = None } ],
        version = 2L
    )

    // A clear at an older version than the report is ignored; one at a newer version lands.
    host.ClearErrors("fcs", "/src/A.fs", version = 1L)
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    host.ClearErrors("fcs", "/src/A.fs", version = 3L)
    test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

[<Fact(Timeout = 20000)>]
let ``OnStatusChanged event fires when plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable statusEvents: (string * PluginStatus) list = []
    host.OnStatusChanged.Add(fun (name, status) -> statusEvents <- (name, status) :: statusEvents)

    let handler =
        { Name = PluginName.create "status-eventer"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // At least 3: the initial Idle from RegisterHandler, plus Running and Completed.
    waitUntil (fun () -> statusEvents.Length >= 3) 12000
    test <@ statusEvents.Length >= 3 @>

    test
        <@
            statusEvents
            |> List.exists (fun (name, s) ->
                name = "status-eventer"
                && match s with
                   | Running _ -> true
                   | _ -> false)
        @>

    test
        <@
            statusEvents
            |> List.exists (fun (name, s) ->
                name = "status-eventer"
                && match s with
                   | Completed _ -> true
                   | _ -> false)
        @>

[<Fact(Timeout = 20000)>]
let ``work-cycle generation bumps once across consecutive Running reports`` () =
    // `bumpGenerationIfStarting` bumps only on a non-Running ▸ Running EDGE, so a plugin
    // that reports Running again with no terminal status in between must NOT bump twice.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "running-twice"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    // ONLY Running, so the next FileChanged finds prev = Some(Running _).
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    // First edge: Idle ▸ Running → generation 1.
    host.EmitFileChanged(SourceChanged [ "src/A.fs" ])
    waitUntil (fun () -> host.WorkCycleGenerations().TryFind "running-twice" = Some 1L) 12000
    test <@ host.WorkCycleGenerations().TryFind "running-twice" = Some 1L @>

    // Second report while already Running → NO second bump (stays at 1).
    host.EmitFileChanged(SourceChanged [ "src/B.fs" ])
    waitForQuiescent host 12000
    // Give the status agent a beat to apply any (non-)mutation before asserting.
    Thread.Sleep(150)
    test <@ host.WorkCycleGenerations().TryFind "running-twice" = Some 1L @>

// --- REGRESSION (daemon side): vacuous resolution on an all-Idle host ---
//
// On a cold daemon every registered plugin is Idle and nothing has run, yet the quiescence
// leg of `allPluginsAtRest` ("no plugin Running + quiet window") resolved the wait
// immediately — so the `WaitForComplete` behind a foreground `check`/`errors --wait`
// rendered an empty ledger as a vacuous exit-0 "No errors". The verdict guard now requires
// at least one plugin to have reached Completed/Failed, so with nothing ever running the
// verdict wait must TIME OUT rather than resolve. The Idle-tolerant scan-settling path
// (`waitForAllTerminal`, requireVerdict=false) keeps the original behaviour and is covered
// by `waitForAllTerminal returns within quiescence window ...`.
[<Fact(Timeout = 20000)>]
let ``waitForVerdict does not resolve on an all-Idle host (cold start, nothing verified)`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    // Registered, never run, nothing verified — the exact cold-start shape.
    let handler =
        { Name = PluginName.create "never-runs"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    test <@ host.GetAllStatuses() |> Map.forall (fun _ s -> s = Idle) @>

    // A short timeout: with the bug this resolves almost immediately via the quiescence leg;
    // with no terminal plugin the only exit is the timeout, so the task must fault.
    let waitTask =
        waitForVerdict host (TimeSpan.FromSeconds(1.0)) System.Threading.CancellationToken.None

    let faultedWithTimeout =
        try
            waitTask.Wait(TimeSpan.FromSeconds(6.0)) |> ignore
            // Resolved cleanly == the vacuous-green bug is still present.
            false
        with :? AggregateException as ex ->
            ex.InnerExceptions |> Seq.exists (fun e -> e :? TimeoutException)

    test <@ faultedWithTimeout @>

[<Fact(Timeout = 20000)>]
let ``OnStatusChanged subscriber re-entrantly calling GetAllStatuses does not deadlock`` () =
    // `statusChanged.Trigger` must fire OUTSIDE the status agent's serialization boundary:
    // fired inside the agent's loop, a subscriber doing PostAndReply (GetAllStatuses) blocks
    // waiting on the agent that is itself blocked inside the trigger.
    let host = PluginHost.create nullChecker "/tmp/test"

    let observedFromHandler = ref None
    let handlerEntered = new ManualResetEventSlim(false)
    let handlerDone = new ManualResetEventSlim(false)

    host.OnStatusChanged.Add(fun (name, _status) ->
        if name = "reentrant" then
            handlerEntered.Set()
            // Re-entrant read must not deadlock.
            let snapshot = host.GetAllStatuses()
            observedFromHandler.Value <- Some snapshot
            handlerDone.Set())

    let handler =
        { Name = PluginName.create "reentrant"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let entered = handlerEntered.Wait(TimeSpan.FromSeconds(5.0))
    test <@ entered @>
    let finished = handlerDone.Wait(TimeSpan.FromSeconds(5.0))
    test <@ finished @>
    test <@ observedFromHandler.Value.IsSome @>
    test <@ observedFromHandler.Value.Value |> Map.containsKey "reentrant" @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal does not deadlock when OnStatusChanged subscriber calls GetAllStatuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "deadlock-test"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        Thread.Sleep(50)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    // `waitForAllTerminal` subscribes to OnStatusChanged and calls GetAllStatuses() inside
    // the handler. If OnStatusChanged fired synchronously inside the MailboxProcessor, that
    // PostAndReply would deadlock and the wait below times out.
    let waitTask =
        waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) System.Threading.CancellationToken.None

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let completed = waitTask.Wait(TimeSpan.FromSeconds(8.0))
    test <@ completed @>

// `setStatus` must not block the calling plugin-agent thread on a TCS round-trip to the
// status agent, while still keeping the two deadlock-freedom tests above green.
[<Fact(Timeout = 20000)>]
let ``OnStatusChanged subscriber observes the newly-applied status via GetAllStatuses`` () =
    // The trigger fires only AFTER the agent has applied the mutation, so a subscriber
    // reading GetAllStatuses inside the handler sees the new value and never a stale one.
    // The non-blocking setStatus does this from the agent's post-mutation continuation —
    // off the agent loop, so the re-entrant GetAllStatuses cannot deadlock.
    let host = PluginHost.create nullChecker "/tmp/test"

    let observed = ref None
    let seen = new ManualResetEventSlim(false)

    host.OnStatusChanged.Add(fun (name, status) ->
        match status with
        | Running _ when name = "observer" ->
            let snapshot = host.GetAllStatuses()
            observed.Value <- Map.tryFind "observer" snapshot
            seen.Set()
        | _ -> ())

    let handler =
        { Name = PluginName.create "observer"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test <@ seen.Wait(TimeSpan.FromSeconds(5.0)) @>

    test
        <@
            match observed.Value with
            | Some(Running _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 20000)>]
let ``a throwing OnStatusChanged subscriber is logged and does not kill status notifications`` () =
    // If the fault escaped, the MailboxProcessor loop would die and every future
    // OnStatusChanged notification would be silently dropped.
    let host = PluginHost.create nullChecker "/tmp/test"

    let sawCompleted = new ManualResetEventSlim(false)
    let mutable threwOnRunning = false

    host.OnStatusChanged.Add(fun (name, status) ->
        if name = "fault-subscriber" then
            match status with
            | Running _ ->
                threwOnRunning <- true
                failwith "subscriber boom"
            | Completed _ -> sawCompleted.Set()
            | _ -> ())

    let original = FsHotWatch.Logging.logLevel
    let sb = System.Text.StringBuilder()
    let writer = new System.IO.StringWriter(sb)
    let prevErr = Console.Error

    try
        Console.SetError(writer)
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

        let handler =
            { Name = PluginName.create "fault-subscriber"
              Init = ()
              Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            ctx.ReportStatus(Running(since = DateTime.UtcNow))
                            ctx.ReportStatus(completedAt DateTime.UtcNow)
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        host.RegisterHandler(handler)
        host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

        // Completed is posted after the Running whose subscriber threw, so its arrival is
        // what proves the trigger loop survived.
        test <@ sawCompleted.Wait(TimeSpan.FromSeconds(5.0)) @>
        test <@ threwOnRunning @>

        // The trigger agent is FIFO, so by the time Completed was delivered the Running
        // fault had already been written to stderr.
        writer.Flush()
        test <@ sb.ToString().Contains("OnStatusChanged subscriber failed") @>
    finally
        Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal with TimeSpan.MaxValue does not overflow deadline arithmetic`` () =
    // Regression: DateTime.UtcNow + TimeSpan.MaxValue throws; MaxValue must be treated
    // as "no deadline" so the RPC path that passes it through (WaitForComplete with
    // timeoutMs <= 0) doesn't crash on a live daemon.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "instant-terminal"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let waitTask =
        waitForAllTerminal host TimeSpan.MaxValue System.Threading.CancellationToken.None

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let completed = waitTask.Wait(TimeSpan.FromSeconds(5.0))
    test <@ completed @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal waits for downstream plugin that hasn't yet picked up its event`` () =
    // B delays its Idle->Running transition past the legacy 1s stability window, opening a
    // wide window where A is Completed, B is still Idle with BuildCompleted unprocessed in
    // its mailbox, and `allTerminal` is therefore true while B is about to start work. The
    // wait must stay pending until B is terminal.
    let host = PluginHost.create nullChecker "/tmp/test"
    let bDelay = TimeSpan.FromMilliseconds(1500.0)
    let mutable bReachedRunning = false
    let mutable bReachedCompleted = false

    let aHandler =
        { Name = PluginName.create "plugin-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "plugin-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        // Slow handler entry: long enough that any sub-1s stability window
                        // in the waiter expires while B is still Idle.
                        do! Async.Sleep(int bDelay.TotalMilliseconds)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        bReachedRunning <- true
                        do! Async.Sleep(50)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                        bReachedCompleted <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // No internal deadline (MaxValue): the regression guarded here is the wait returning
    // EARLY, caught by bReachedRunning/bReachedCompleted, and a genuine hang is bounded by
    // the outer `.Wait(20s)` + `Fact(Timeout=30000)`. A finite internal timeout only adds a
    // wall-clock number that fires spuriously under load while the cascade is still draining.
    let waitTask =
        waitForAllTerminal host TimeSpan.MaxValue System.Threading.CancellationToken.None

    let completed = waitTask.Wait(TimeSpan.FromSeconds(20.0))
    test <@ completed @>
    // Both plugins must have completed a real work cycle; the bug returns before B started.
    test <@ bReachedRunning @>
    test <@ bReachedCompleted @>

    let final = host.GetAllStatuses()

    let isCompleted s =
        match s with
        | Completed _ -> true
        | _ -> false

    test <@ final |> Map.forall (fun _ s -> isCompleted s) @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal does not return while a downstream plugin still has events queued in its mailbox after it has already advanced a generation``
    ()
    =
    // The BuildCompleted -> Pending -> Running edge race. B subscribes to both FileChanged
    // and BuildCompleted, and A emits BuildCompleted late enough that B has already finished
    // its FileChanged cycle. In the window before B's handler picks the event up, both
    // plugins are Completed with their generations advanced past the snapshot, yet B still
    // has a queued event — so the legacy `allPluginsAdvancedToTerminal` returned true and
    // WaitForComplete resolved before B's second cycle even started.
    let host = PluginHost.create nullChecker "/tmp/test"
    let bRunningCount = ref 0

    let aHandler =
        { Name = PluginName.create "plugin-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        // Long enough for B to settle into Completed before BuildCompleted.
                        do! Async.Sleep(300)
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "plugin-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(50)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | BuildCompleted _ ->
                        // Widens the race window: B sits in Completed from the prior
                        // FileChanged cycle with BuildCompleted still in its mailbox.
                        do! Async.Sleep(500)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        Interlocked.Increment(&bRunningCount.contents) |> ignore

                        do! Async.Sleep(20)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged; SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // No internal deadline (MaxValue): the regression guarded here is the wait returning
    // EARLY, caught by `observed = 1`, and a genuine hang is bounded by the outer
    // `.Wait(20s)` + `Fact(Timeout=30000)`. A finite internal timeout only adds a wall-clock
    // number that fires spuriously under load while B's queued event is still draining.
    let waitTask =
        waitForAllTerminal host TimeSpan.MaxValue System.Threading.CancellationToken.None

    let completed = waitTask.Wait(TimeSpan.FromSeconds(20.0))
    test <@ completed @>
    // 1 means B drained its queued BuildCompleted and ran the second cycle to terminal. With
    // the bug the wait returns while the event is still queued (or B is in its pre-Running
    // sleep), giving 0.
    let observed = Volatile.Read(&bRunningCount.contents)
    test <@ observed = 1 @>

[<Fact(Timeout = 30000)>]
let ``waitForAllTerminal waits for full cascade A -> B -> C`` () =
    // A (FileChanged -> BuildCompleted), B (BuildCompleted -> TestRunCompleted), C
    // (TestRunCompleted -> terminal). The wait starts before any of it and must cover the
    // whole chain even though each downstream plugin begins Idle.
    let host = PluginHost.create nullChecker "/tmp/test"

    let aHandler =
        { Name = PluginName.create "cascade-a"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    let bHandler =
        { Name = PluginName.create "cascade-b"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        do! Async.Sleep(300)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)

                        ctx.EmitTestRunCompleted
                            { RunId = Guid.NewGuid()
                              TotalElapsed = TimeSpan.Zero
                              Outcome = Normal
                              Results = Map.empty
                              Verification = NoProjectsSelected }

                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    let mutable cCompleted = false

    let cHandler =
        { Name = PluginName.create "cascade-c"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | TestRunCompleted _ ->
                        do! Async.Sleep(300)
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                        cCompleted <- true
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeTestRunCompleted ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(aHandler)
    host.RegisterHandler(bHandler)
    host.RegisterHandler(cHandler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let waitTask =
        waitForAllTerminal host (TimeSpan.FromSeconds(10.0)) System.Threading.CancellationToken.None

    let completed = waitTask.Wait(TimeSpan.FromSeconds(15.0))
    test <@ completed @>
    test <@ cCompleted @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal completes when plugin fails mid-cycle`` () =
    // Failed is a terminal status, so a crashed plugin still satisfies the wait.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "crasher"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep(20)
                        ctx.ReportStatus(PluginStatus.failedNow "boom" "boom" TimeSpan.Zero)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let waitTask =
        waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) System.Threading.CancellationToken.None

    let completed = waitTask.Wait(TimeSpan.FromSeconds(8.0))
    test <@ completed @>

    let final = host.GetAllStatuses()

    let isFailed s =
        match s with
        | Failed _ -> true
        | _ -> false

    test <@ final |> Map.forall (fun _ s -> isFailed s) @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal returns within quiescence window when no work is pending`` () =
    // A plugin that stays Idle (never receives an event) must not hang WaitForComplete: with
    // nothing happening the quiescence window fires and the wait returns.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "never-fires"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let started = DateTime.UtcNow

    let waitTask =
        waitForAllTerminal host (TimeSpan.FromSeconds(5.0)) System.Threading.CancellationToken.None

    let completed = waitTask.Wait(TimeSpan.FromSeconds(10.0))
    let elapsed = DateTime.UtcNow - started
    test <@ completed @>
    // Well under the 5s wait timeout — the 4.9s bound leaves room for slow CI machines while
    // still failing if the wait ran to its timeout instead of settling on quiescence.
    test <@ elapsed < TimeSpan.FromSeconds(4.9) @>

[<Fact(Timeout = 20000)>]
let ``waitForAllTerminal faults with OperationCanceledException when shutdown token fires mid-wait`` () =
    // A foreground client blocked in WaitForComplete while the daemon shuts down: the wait
    // must fault so the client exits non-zero, or the RPC resolves cleanly during teardown
    // and the foreground process reports success.
    let host = PluginHost.create nullChecker "/tmp/test"

    // Goes Running and never reaches terminal, so the quiescence window cannot fire and the
    // wait stays blocked until cancellation.
    let handler =
        { Name = PluginName.create "blocked"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(DateTime.UtcNow))
                        do! Async.Sleep 60_000
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    use cts = new System.Threading.CancellationTokenSource()

    let waitTask = waitForAllTerminal host TimeSpan.MaxValue cts.Token

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Give the wait a moment to enter its loop, then trip the shutdown token.
    Threading.Thread.Sleep(200)
    cts.Cancel()

    // Async.StartAsTask wraps OperationCanceledException as AggregateException
    // when the inner async observes a cancelled token via Async.Sleep.
    let captured: exn option =
        try
            waitTask.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            None
        with ex ->
            Some ex

    test <@ captured.IsSome @>

    let inner: exn =
        match captured.Value with
        | :? AggregateException as agg -> agg.InnerException
        | ex -> ex

    test <@ inner :? OperationCanceledException @>

[<Fact(Timeout = 20000)>]
let ``HasFailingReasons distinguishes warnings from errors`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    host.ReportErrors(
        "linter",
        "/src/A.fs",
        [ { Message = "style warning"
            Severity = DiagnosticSeverity.Warning
            Line = 1
            Column = 0
            Detail = None } ]
    )

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    test <@ not (host.HasFailingReasons(warningsAreFailures = false)) @>

    let withWarnings = host.FailingReasons(warningsAreFailures = true)
    test <@ withWarnings.Count = 1 @>

    let withoutWarnings = host.FailingReasons(warningsAreFailures = false)
    test <@ withoutWarnings.Count = 0 @>

    host.ReportErrors(
        "fcs",
        "/src/B.fs",
        [ { Message = "real error"
            Severity = DiagnosticSeverity.Error
            Line = 5
            Column = 0
            Detail = None } ]
    )

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    test <@ host.HasFailingReasons(warningsAreFailures = false) @>

    // 2 files: the warning and the error.
    let allFailing = host.FailingReasons(warningsAreFailures = true)
    test <@ allFailing.Count = 2 @>

    // 1 file: only the error.
    let errorsOnly = host.FailingReasons(warningsAreFailures = false)
    test <@ errorsOnly.Count = 1 @>
    test <@ errorsOnly.ContainsKey "/src/B.fs" @>

// --- FileCommand pattern registry ---

let private parsePattern = FsHotWatch.Watcher.FilePattern.parse

[<Fact(Timeout = 15000)>]
let ``RegisterFileCommandPattern stores pattern retrievable by name`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("coverage-ratchet", parsePattern "*.ratchet.json")
    test <@ host.GetFileCommandPattern("coverage-ratchet") = Some(parsePattern "*.ratchet.json") @>

[<Fact(Timeout = 15000)>]
let ``GetFileCommandPattern returns None for unregistered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    test <@ host.GetFileCommandPattern("nonexistent") = None @>

[<Fact(Timeout = 15000)>]
let ``RegisterFileCommandPattern overwrites on re-register`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("plugin-a", parsePattern "*.ratchet.json")
    host.RegisterFileCommandPattern("plugin-a", parsePattern "coverage-ratchet.json")
    test <@ host.GetFileCommandPattern("plugin-a") = Some(parsePattern "coverage-ratchet.json") @>

[<Fact(Timeout = 15000)>]
let ``RerunPlugin returns Error for unregistered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    let result = host.RerunPlugin("nonexistent", (fun () -> []))

    match result with
    | Result.Error msg -> test <@ msg.Contains("nonexistent") @>
    | Result.Ok() -> failwith "expected Error"

[<Fact(Timeout = 15000)>]
let ``RerunPlugin returns Ok for registered plugin`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterFileCommandPattern("coverage-ratchet", parsePattern "*.ratchet.json")
    test <@ host.RerunPlugin("coverage-ratchet", (fun () -> [])) = Result.Ok() @>

// ---------------------------------------------------------------------------
// AUTOMATION-447 — `rerun` reaches every FileChanged subscriber, not only FileCommands
// ---------------------------------------------------------------------------

/// A FileChanged subscriber that records each batch it was handed.
let private fileChangedRecorder (name: string) =
    let batches = ResizeArray<string list>()

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged files) -> lock batches (fun () -> batches.Add files)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    handler, (fun () -> lock batches (fun () -> List.ofSeq batches))

[<Fact(Timeout = 15000)>]
let ``RerunPlugin re-fires a FileChanged subscriber over the registered source set`` () =
    // `fshw rerun format-check` used to be refused ("no registered file pattern") — the
    // only supported way to refresh a suspect cached verdict was to stop the daemon.
    let host = PluginHost.create nullChecker "/tmp"
    let handler, batches = fileChangedRecorder "fmt-like"
    host.RegisterHandler(handler)

    let sources = [ "/tmp/src/A.fs"; "/tmp/src/B.fs" ]
    test <@ host.RerunPlugin("fmt-like", (fun () -> sources)) = Result.Ok() @>

    waitUntil (fun () -> not (host.AnyPluginBusy()) && not (List.isEmpty (batches ()))) 5000
    test <@ batches () = [ sources ] @>

[<Fact(Timeout = 15000)>]
let ``RerunPlugin refuses a FileChanged subscriber while no source files are registered`` () =
    // An empty re-fire would land a `no files to check` and read as a refresh that found
    // nothing — the same shape as the "formatted 0 files" this ticket exists to end.
    let host = PluginHost.create nullChecker "/tmp"
    let handler, batches = fileChangedRecorder "fmt-like"
    host.RegisterHandler(handler)

    match host.RerunPlugin("fmt-like", (fun () -> [])) with
    | Ok() -> failwith "expected a refusal with no registered sources"
    | Result.Error msg ->
        test <@ msg.Contains "fmt-like" @>
        test <@ msg.Contains "no source files are registered" @>

    test <@ List.isEmpty (batches ()) @>

[<Fact(Timeout = 15000)>]
let ``RerunPlugin names a preprocessor as such and points at fshw format`` () =
    // In `"format": true` mode the formatter is the preprocessor, not a plugin: it holds
    // no cached state, and `fshw format` is the primitive that re-runs it with evidence.
    let host = PluginHost.create nullChecker "/tmp"

    let preprocessor =
        { new IFsHotWatchPreprocessor with
            member _.Name = "format"

            member _.Process (changedFiles: string list) (_repoRoot: string) =
                Ok
                    { Modified = []
                      Considered = changedFiles.Length
                      Evidence = "fake" }

            member _.Dispose() = () }

    host.RegisterPreprocessor(preprocessor)

    match host.RerunPlugin("format", (fun () -> [ "/tmp/src/A.fs" ])) with
    | Ok() -> failwith "expected a refusal naming the preprocessor"
    | Result.Error msg ->
        test <@ msg.Contains "preprocessor" @>
        test <@ msg.Contains "fshw format" @>

[<Fact(Timeout = 15000)>]
let ``RerunPlugin refuses a registered plugin that does not consume file changes`` () =
    let host = PluginHost.create nullChecker "/tmp"
    host.RegisterHandler(buildRecorder () |> snd)

    match host.RerunPlugin("build-recorder", (fun () -> [ "/tmp/src/A.fs" ])) with
    | Ok() -> failwith "expected a refusal for a plugin with nothing to re-fire"
    | Result.Error msg -> test <@ msg.Contains "does not consume file changes" @>

[<Fact(Timeout = 15000)>]
let ``Teardown logs failing plugin Teardown with exception class (F14)`` () =
    // F14: plugin Teardown is a third-party-extension boundary, so the catch keeps cleanup
    // going across other plugins. Logging only `ex.Message` stripped the stack trace exactly
    // when a misbehaving plugin needed diagnosing, hence the exception-type assertion.
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "boom"
          Init = ()
          Update = fun _ _ _ -> async { return () }
          Commands = []
          Subscriptions = Set.empty
          CacheKey = None
          Teardown = Some(fun () -> raise (System.InvalidOperationException("teardown boom"))) }

    host.RegisterHandler(handler)

    let original = FsHotWatch.Logging.logLevel
    let sb = System.Text.StringBuilder()
    let writer = new System.IO.StringWriter(sb)
    let prevErr = System.Console.Error

    try
        System.Console.SetError(writer)
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
        host.Teardown()
        writer.Flush()
        let output = sb.ToString()
        test <@ output.Contains("Teardown failed") @>
        test <@ output.Contains("InvalidOperationException") @>
    finally
        System.Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original

// ---------------------------------------------------------------------------
// Task-cache clearing: BOTH arms of every `match taskCache with` — a host built WITH a cache
// forwards the clear, and a host built WITHOUT one is a safe no-op rather than a crash.
// ---------------------------------------------------------------------------

let private cacheEntry (summary: string) : FsHotWatch.TaskCache.TaskCacheResult =
    { CacheKey = ContentHash.create "k"
      Errors = []
      // These entries are only ever CLEARED, never replayed, so the CachedStatus content is
      // immaterial beyond keeping `summary` distinguishing.
      Status = FsHotWatch.TaskCache.CachedRunCompleted(RunVerdict.create summary TimeSpan.Zero)
      EmittedEvents = [] }

[<Fact(Timeout = 15000)>]
let ``ClearTaskCache variants forward to the cache when the host has one`` () =
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let c = cache :> FsHotWatch.TaskCache.ITaskCache
    let key = ContentHash.create "k"

    let repoRoot = "/tmp/test"

    // AUTOMATION-564: entries are keyed by the REPO-RELATIVE identity, so a caller
    // naming a file by its absolute path must be translated before it can clear
    // anything. Storing under the same spelling the framework writes is what makes
    // these assertions test the translation instead of assuming it away.
    let ckOf plugin file : FsHotWatch.TaskCache.CompositeKey =
        { Plugin = plugin
          File = file |> Option.map (FsHotWatch.CachePathIdentity.keyOf (Some repoRoot)) }

    let host = PluginHost(Unchecked.defaultof<_>, repoRoot, taskCache = c)

    c.Set (ckOf "p" (Some "/tmp/test/a.fs")) key (cacheEntry "a")
    test <@ (c.TryGet (ckOf "p" (Some "/tmp/test/a.fs")) key).IsSome @>
    host.ClearTaskCachePluginFile("p", "/tmp/test/a.fs")
    test <@ (c.TryGet (ckOf "p" (Some "/tmp/test/a.fs")) key).IsNone @>

    c.Set (ckOf "p" (Some "/tmp/test/b.fs")) key (cacheEntry "b")
    host.ClearTaskCacheFile("/tmp/test/b.fs")
    test <@ (c.TryGet (ckOf "p" (Some "/tmp/test/b.fs")) key).IsNone @>

    c.Set (ckOf "p" (Some "/tmp/test/c.fs")) key (cacheEntry "c")
    host.ClearTaskCachePlugin("p")
    test <@ (c.TryGet (ckOf "p" (Some "/tmp/test/c.fs")) key).IsNone @>

    c.Set (ckOf "p" (Some "/tmp/test/d.fs")) key (cacheEntry "d")
    c.Set (ckOf "q" None) key (cacheEntry "q")
    host.ClearTaskCache()
    test <@ (c.TryGet (ckOf "p" (Some "/tmp/test/d.fs")) key).IsNone @>
    test <@ (c.TryGet (ckOf "q" None) key).IsNone @>

[<Fact(Timeout = 15000)>]
let ``ClearTaskCache variants are safe no-ops on a host with no cache`` () =
    // A cacheless host (the default) must absorb every clear without throwing — these are
    // called from IPC verbs that don't know whether a cache was configured.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp/test"

    host.ClearTaskCache()
    host.ClearTaskCachePlugin("p")
    host.ClearTaskCacheFile("/a.fs")
    host.ClearTaskCachePluginFile("p", "/a.fs")

    test <@ host.GetAllStatuses() = Map.empty @>

[<Fact(Timeout = 15000)>]
let ``RerunPlugin fails with a named reason when the plugin has no pattern`` () =
    // A name that is neither a FileCommand with a pattern nor a registered plugin has
    // nothing to re-fire. That must be a named Error, never a silent Ok reporting
    // success for a rerun that never happened.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp/test"

    match host.RerunPlugin("no-such-plugin", (fun () -> [])) with
    | Ok() -> failwith "expected an Error for a plugin with no registered pattern"
    | Result.Error msg ->
        test <@ msg.Contains "no-such-plugin" @>
        test <@ msg.Contains "no registered file pattern" @>

    test <@ (host.GetFileCommandPattern "no-such-plugin").IsNone @>

// ---------------------------------------------------------------------------
// AUTOMATION-300 — a vanished path's findings must clear from EVERY plugin
// ---------------------------------------------------------------------------
//
// Renaming a file left the daemon analyzing the OLD path from memory. The
// finding it produced — TestPrune's "symbol analysis failed — Parse errors" —
// was keyed to a path that no longer exists, so nothing ever replaced it and the
// gate stayed red until `fshw stop`, the one command the docs tell you not to
// run. Renames are routine here (the compiler IS the migration checklist), so
// this taxed exactly the refactoring the codebase asks for.
//
// The removed-file path used to clear `fcs` ONLY, which is why the TestPrune
// finding survived it.

let private ghostEntry message =
    [ { Message = message
        Severity = DiagnosticSeverity.Error
        Line = 1
        Column = 0
        Detail = None } ]

[<Fact(Timeout = 20000)>]
let ``clearing a vanished file clears findings from every plugin, not just fcs`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let ghost = "/tmp/test/Renamed.fs"

    // `fcs` is the one the old code cleared; the other two stand for TestPrune
    // and the analyzers, whose findings are what actually kept the gate red.
    for plugin in [ "fcs"; "test-prune"; "analyzers" ] do
        host.ReportErrors(plugin, ghost, ghostEntry "symbol analysis failed — Parse errors")

    // Precondition: without this the test could pass by never having reported.
    test <@ host.GetErrors() |> Map.containsKey ghost @>
    test <@ host.GetErrors() |> Map.find ghost |> List.length = 3 @>

    host.ClearFileEverywhere ghost

    test <@ host.GetErrors() |> Map.tryFind ghost = None @>

[<Fact(Timeout = 20000)>]
let ``clearing a vanished file leaves findings for files that still exist`` () =
    // The control. A `ClearFileEverywhere` that cleared the whole ledger would
    // satisfy the test above and silently discard every real finding in the
    // repo — a worse failure than the one being fixed, and invisible, because an
    // empty ledger reads as a green gate.
    let host = PluginHost.create nullChecker "/tmp/test"
    let ghost = "/tmp/test/Renamed.fs"
    let alive = "/tmp/test/StillHere.fs"

    host.ReportErrors("test-prune", ghost, ghostEntry "about a file that is gone")
    host.ReportErrors("test-prune", alive, ghostEntry "a real problem")

    host.ClearFileEverywhere ghost

    let remaining = host.GetErrors()
    test <@ remaining |> Map.tryFind ghost = None @>
    test <@ remaining |> Map.find alive |> List.length = 1 @>

[<Fact(Timeout = 20000)>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``vanished-diagnostic pruning cannot erase a newer report for a recreated path`` () =
    withTempDir "rename-recreate" (fun root ->
        let file = System.IO.Path.Combine(root, "Renamed.fs")
        let host = PluginHost.create nullChecker root
        host.ReportErrors("test-prune", file, ghostEntry "old generation")

        host.PruneVanishedErrors(fun candidate ->
            test <@ candidate = file @>
            host.ReportErrors("test-prune", file, ghostEntry "new generation")
            false)
        |> ignore

        let remaining = host.GetErrors() |> Map.find file
        test <@ remaining |> List.exists (fun (_, entry) -> entry.Message = "new generation") @>)

[<Fact(Timeout = 20000)>]
[<Trait("Issue", "AUTOMATION-300")>]
let ``vanished-diagnostic pruning only treats repository paths as files`` () =
    withTempDir "diagnostic-key-semantics" (fun root ->
        let host = PluginHost.create nullChecker root
        let insideAbsolute = System.IO.Path.Combine(root, "Gone.fs")
        let insideRelative = "AlsoGone.fs"

        let outside =
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root), "outside.fs")

        let pseudo = "<build>"

        for key in [ insideAbsolute; insideRelative; outside; pseudo ] do
            host.ReportErrors("test-prune", key, ghostEntry key)

        host.PruneVanishedErrors(System.IO.File.Exists) |> ignore

        let remaining = host.GetErrors()
        test <@ not (remaining |> Map.containsKey insideAbsolute) @>
        test <@ not (remaining |> Map.containsKey insideRelative) @>
        test <@ remaining |> Map.containsKey outside @>
        test <@ remaining |> Map.containsKey pseudo @>)

// --- AUTOMATION-555 (rework): every plugin run lands on the phase ledger ---

/// The ledger records the plugin's WHOLE `Running` interval, not the elapsed the
/// plugin measured for itself: test-prune is `Running` through minutes of symbol
/// analysis before its own stopwatch starts, and a check waits for all of it.
[<Fact(Timeout = 15000)>]
let ``a plugin's Running to Completed interval is recorded on the host's phase ledger`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let handler =
        { Name = PluginName.create "ledger-test"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))
                        do! Async.Sleep 60
                        // A self-measured elapsed far SHORTER than the Running interval.
                        ctx.ReportStatus(PluginStatus.completedNow "7 passed" (TimeSpan.FromMilliseconds 1.0))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            host.Phases.Snapshot(DateTime.UtcNow)
            |> List.exists (fun r -> r.Scope = "plugin.ledger-test" && r.Detail <> Some "in flight"))
        12000

    match
        host.Phases.Snapshot(DateTime.UtcNow)
        |> List.filter (fun r -> r.Scope = "plugin.ledger-test")
    with
    | [ record ] ->
        test <@ record.Detail = Some "7 passed" @>
        test <@ record.Elapsed >= TimeSpan.FromMilliseconds 50.0 @>
    | other -> failwith $"expected one ledger record for the plugin run, got %A{other}"
