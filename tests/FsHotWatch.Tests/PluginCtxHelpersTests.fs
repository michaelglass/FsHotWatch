module FsHotWatch.Tests.PluginCtxHelpersTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework

/// Build a PluginCtx that records every call for assertion. Statuses are recorded
/// as VALUES, not `%A` renderings: asserting on a formatted string couples the
/// test to F#'s layout algorithm, which reflows whenever a field name or width
/// changes.
let private makeRecordingCtx () =
    let calls = System.Collections.Generic.List<string>()
    let statuses = System.Collections.Generic.List<PluginStatus>()

    let ctx: PluginCtx<int> =
        { ReportStatus =
            fun s ->
                statuses.Add s
                calls.Add "ReportStatus"
          ReportErrors = fun _ _ -> ()
          ClearErrors = fun _ -> ()
          ClearAllErrors = fun () -> ()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = ""
          Post = fun _ -> ()
          StartSubtask = fun k l -> calls.Add(sprintf "Start %s:%s" k l)
          UpdateSubtask = fun k l -> calls.Add(sprintf "Update %s:%s" k l)
          EndSubtask = fun k -> calls.Add(sprintf "End %s" k)
          Log = fun _ -> ()
          CompleteWithTimeout = fun r -> calls.Add(sprintf "Timeout %s" r)
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, calls, statuses

[<Fact(Timeout = 15000)>]
let ``withSubtask brackets work with Start then End`` () =
    let ctx, calls, _statuses = makeRecordingCtx ()

    PluginCtxHelpers.withSubtask ctx "k" "label" (async { return 42 })
    |> Async.RunSynchronously
    |> ignore

    test <@ calls |> Seq.toList = [ "Start k:label"; "End k" ] @>

[<Fact(Timeout = 15000)>]
let ``withSubtask returns the result of inner work`` () =
    let ctx, _, _ = makeRecordingCtx ()

    let result =
        PluginCtxHelpers.withSubtask ctx "k" "l" (async { return "hello" })
        |> Async.RunSynchronously

    test <@ result = "hello" @>

[<Fact(Timeout = 15000)>]
let ``withSubtask calls EndSubtask even when work throws`` () =
    let ctx, calls, _statuses = makeRecordingCtx ()

    let throwing: Async<int> = async { return raise (Exception "boom") }

    Assert.Throws<Exception>(fun () ->
        PluginCtxHelpers.withSubtask ctx "k" "l" throwing
        |> Async.RunSynchronously
        |> ignore)
    |> ignore

    test <@ calls |> Seq.toList = [ "Start k:l"; "End k" ] @>

[<Fact(Timeout = 15000)>]
let ``completeWith reports a Completed status carrying the verdict`` () =
    let ctx, calls, statuses = makeRecordingCtx ()

    PluginCtxHelpers.completeWith ctx "done" (TimeSpan.FromSeconds 1.5)

    // ONE call: the Completed status carries the summary AND the measured duration
    // (RunVerdict), so there is no second channel for the two to disagree through.
    test <@ calls.Count = 1 @>
    test <@ statuses.Count = 1 @>

    match statuses.[0] with
    | Completed(_, v) ->
        test <@ v.Summary = "done" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 1.5 @>
    | other -> failwithf "expected Completed carrying a verdict, got %A" other

[<Fact(Timeout = 15000)>]
let ``failedWith reports a Failed status carrying BOTH the diagnosis and the verdict`` () =
    let ctx, calls, statuses = makeRecordingCtx ()

    PluginCtxHelpers.failedWith ctx "stack trace" "2 failed: A, B" (TimeSpan.FromSeconds 4.0)

    test <@ calls.Count = 1 @>

    match statuses.[0] with
    | Failed(err, _, v) ->
        // The error is the DIAGNOSIS; the verdict is the human summary plus the
        // measured duration. Carrying both stops the run record guessing a start
        // time (the "started: with no elapsed:" signature).
        test <@ err = "stack trace" @>
        test <@ v.Summary = "2 failed: A, B" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 4.0 @>
    | other -> failwithf "expected Failed carrying a verdict, got %A" other
