module FsHotWatch.Tests.CheckPipelineCancellationTests

open System
open System.Collections.Concurrent
open System.IO
open System.Runtime.ExceptionServices
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.CheckCache
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

type private CountingCache() =
    let store = ConcurrentDictionary<string, CachedCheck>()

    member _.Count = store.Count

    interface ICheckCacheBackend with
        member _.TryGet(key) =
            match store.TryGetValue(hashCacheKey key) with
            | true, v -> Some v
            | _ -> None

        member _.Set key result = store[hashCacheKey key] <- result

        member _.Invalidate(key) =
            store.TryRemove(hashCacheKey key) |> ignore

        member _.Clear() = store.Clear()

/// An activity sink that runs `onCheckStart` when the pipeline announces a check,
/// which it does after its own cancellation checks and before FCS runs.
type private CheckStartHook(onCheckStart: unit -> unit) =
    interface PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member _.Log message =
            if message.StartsWith "check start" then
                onCheckStart ()

        member _.SetSummary _ = ()

/// Marks the execution context of one test, so the first-chance counter counts only
/// exceptions raised by work that test started, on whatever thread they are raised.
let private inScope = AsyncLocal<bool>()

/// Run `action`, counting the OperationCanceledExceptions raised by work it starts.
let private countingCanceledThrows (action: unit -> 'a) : 'a * int =
    let count = ref 0

    let handler =
        EventHandler<FirstChanceExceptionEventArgs>(fun _ args ->
            if inScope.Value && (args.Exception :? OperationCanceledException) then
                Interlocked.Increment count |> ignore)

    AppDomain.CurrentDomain.FirstChanceException.AddHandler handler
    inScope.Value <- true

    try
        let result = action ()
        result, count.Value
    finally
        inScope.Value <- false
        AppDomain.CurrentDomain.FirstChanceException.RemoveHandler handler

/// Run `action` with this context's log lines captured at debug level.
let private capturingLogs (action: unit -> 'a) : 'a * string list =
    let lines = ConcurrentQueue<string>()

    let result =
        using
            (Logging.installSink
                { Write = lines.Enqueue
                  Level = Logging.LogLevel.Debug })
            (fun _ -> action ())

    result, List.ofSeq lines

/// The cancellation-related lines: the `[check]` lines naming a cancellation.
let private cancellationLines (lines: string list) =
    lines
    |> List.filter (fun line ->
        line.Contains "[check]"
        && (line.Contains "Cancelled" || line.Contains "canceled"))
    |> List.map (fun line ->
        // Drop the timestamp: `  [check] <ts> <message>`.
        let afterTag = line.Substring(line.IndexOf "] " + 2)
        afterTag.Substring(afterTag.IndexOf ' ' + 1))

/// A pipeline over a one-file script project in `tmpDir`, with a counting cache.
let private scriptPipeline (tmpDir: string) (activity: PluginActivity.IActivitySink option) =
    let checker =
        FSharpChecker.Create(projectCacheSize = 1, keepAssemblyContents = true)

    let cache = CountingCache()

    let pipeline =
        match activity with
        | Some sink -> CheckPipeline(checker, cacheBackend = cache, activity = sink)
        | None -> CheckPipeline(checker, cacheBackend = cache)

    let sourceFile = Path.GetFullPath(Path.Combine(tmpDir, "Mod.fs"))
    File.WriteAllLines(sourceFile, [| "module Mod"; "let x = 1" |])

    let options, _ =
        checker.GetProjectOptionsFromScript(sourceFile, SourceText.ofString (File.ReadAllText sourceFile))
        |> Async.RunSynchronously

    pipeline.RegisterProject(Path.Combine(tmpDir, "Mod.fsproj"), options)
    pipeline, cache, AbsFilePath.create sourceFile

[<Fact(Timeout = 15000)>]
let ``the first-chance counter sees a cancellation thrown on another thread`` () =
    let _, count =
        countingCanceledThrows (fun () ->
            async {
                do! Async.SwitchToThreadPool()

                try
                    CancellationToken(true).ThrowIfCancellationRequested()
                with :? OperationCanceledException ->
                    ()
            }
            |> Async.RunSynchronously)

    test <@ count = 1 @>

[<Fact(Timeout = 60000)>]
let ``an uncanceled check is cached, so the cache below can observe a canceled one`` () =
    withTempDir "cancel-control" (fun tmpDir ->
        let pipeline, cache, file = scriptPipeline tmpDir None
        let result = pipeline.CheckFile(file) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ cache.Count = 1 @>)

[<Fact(Timeout = 60000)>]
let ``a check canceled before FCS returns None, logs Cancelled, caches nothing and throws nothing`` () =
    withTempDir "cancel-before-fcs" (fun tmpDir ->
        let pipeline, cache, file = scriptPipeline tmpDir None
        use cts = new CancellationTokenSource()
        cts.Cancel()

        let (result, lines), throws =
            countingCanceledThrows (fun () ->
                capturingLogs (fun () -> pipeline.CheckFile(file, cts.Token) |> Async.RunSynchronously))

        test <@ result = None @>
        test <@ cancellationLines lines = [ "Cancelled: Mod.fs" ] @>
        test <@ cache.Count = 0 @>
        test <@ throws = 0 @>)

[<Fact(Timeout = 60000)>]
let ``a check with explicit options canceled before FCS returns None, logs Cancelled and throws nothing`` () =
    withTempDir "cancel-before-fcs-options" (fun tmpDir ->
        let pipeline, cache, file = scriptPipeline tmpDir None
        let options = (pipeline.GetProjectOptions(Path.Combine(tmpDir, "Mod.fsproj"))).Value
        use cts = new CancellationTokenSource()
        cts.Cancel()

        let (result, lines), throws =
            countingCanceledThrows (fun () ->
                capturingLogs (fun () ->
                    pipeline.CheckFileWithOptions(file, options, cts.Token)
                    |> Async.RunSynchronously))

        test <@ result = None @>
        test <@ cancellationLines lines = [ "Cancelled: Mod.fs" ] @>
        test <@ cache.Count = 0 @>
        test <@ throws = 0 @>)

[<Fact(Timeout = 60000)>]
let ``a check canceled while FCS runs returns None after it, is logged as before, caches nothing and throws nothing``
    ()
    =
    withTempDir "cancel-after-fcs" (fun tmpDir ->
        use cts = new CancellationTokenSource()
        let pipeline, cache, file = scriptPipeline tmpDir (Some(CheckStartHook(cts.Cancel)))

        let (result, lines), throws =
            countingCanceledThrows (fun () ->
                capturingLogs (fun () -> pipeline.CheckFile(file, cts.Token) |> Async.RunSynchronously))

        test <@ cts.IsCancellationRequested @>
        test <@ result = None @>

        test
            <@ cancellationLines lines = [ $"Failed to check %s{AbsFilePath.value file}: The operation was canceled." ] @>

        test <@ cache.Count = 0 @>
        test <@ throws = 0 @>)
