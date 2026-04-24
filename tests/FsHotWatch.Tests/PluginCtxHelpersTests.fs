module FsHotWatch.Tests.PluginCtxHelpersTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework

/// Build a PluginCtx that records every call for assertion.
let private makeRecordingCtx () =
    let calls = System.Collections.Generic.List<string>()

    let ctx: PluginCtx<int> =
        { ReportStatus = fun s -> calls.Add(sprintf "ReportStatus %A" s)
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
          CompleteWithSummary = fun s -> calls.Add(sprintf "Summary %s" s) }

    ctx, calls

[<Fact(Timeout = 5000)>]
let ``withSubtask brackets work with Start then End`` () =
    let ctx, calls = makeRecordingCtx ()

    PluginCtxHelpers.withSubtask ctx "k" "label" (async { return 42 })
    |> Async.RunSynchronously
    |> ignore

    test <@ calls |> Seq.toList = [ "Start k:label"; "End k" ] @>

[<Fact(Timeout = 5000)>]
let ``withSubtask returns the result of inner work`` () =
    let ctx, _ = makeRecordingCtx ()

    let result =
        PluginCtxHelpers.withSubtask ctx "k" "l" (async { return "hello" })
        |> Async.RunSynchronously

    test <@ result = "hello" @>

[<Fact(Timeout = 5000)>]
let ``withSubtask calls EndSubtask even when work throws`` () =
    let ctx, calls = makeRecordingCtx ()

    let throwing: Async<int> = async { return raise (Exception "boom") }

    Assert.Throws<Exception>(fun () ->
        PluginCtxHelpers.withSubtask ctx "k" "l" throwing
        |> Async.RunSynchronously
        |> ignore)
    |> ignore

    test <@ calls |> Seq.toList = [ "Start k:l"; "End k" ] @>

[<Fact(Timeout = 5000)>]
let ``completeWith emits Summary then Completed status`` () =
    let ctx, calls = makeRecordingCtx ()

    PluginCtxHelpers.completeWith ctx "done"

    test <@ calls.Count = 2 @>
    test <@ calls.[0] = "Summary done" @>
    test <@ (calls.[1]: string).StartsWith("ReportStatus Completed ") @>
