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
                  Level = Logging.LogLevel.Debug
                  File = None })
            (fun _ -> action ())

    result, List.ofSeq lines

/// The cancellation-related lines: the `[check]` lines naming a cancellation.
let private cancellationLines (lines: string list) =
    lines
    |> List.filter (fun line ->
        line.Contains "[check]"
        && (line.Contains "Cancelled"
            || line.Contains "canceled"
            || line.Contains "cancelled"
            || line.Contains "superseded"))
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
// "Failed to check …: The operation was canceled." read as a defect. The line names who
// cancelled the check and what that does to the verdict.
let ``a check canceled while FCS runs returns None without waiting for it, names its canceller and the verdict effect, caches nothing and throws nothing``
    ()
    =
    withTempDir "cancel-after-fcs" (fun tmpDir ->
        use cts = new CancellationTokenSource()
        let pipeline, cache, file = scriptPipeline tmpDir (Some(CheckStartHook(cts.Cancel)))

        let (result, lines), throws =
            countingCanceledThrows (fun () ->
                capturingLogs (fun () ->
                    pipeline.CheckFile(file, cts.Token)
                    |> CheckCaller.within "scan"
                    |> Async.RunSynchronously))

        test <@ cts.IsCancellationRequested @>
        test <@ result = None @>

        test
            <@
                cancellationLines lines = [ $"Check of %s{AbsFilePath.value file} cancelled by scan before it finished; no answer. Verdict: a scan re-runs the file and counts it unchecked if it never finishes; a change batch leaves it to the file's next check." ]
            @>

        test
            <@
                lines
                |> List.exists (fun l -> l.Contains "[check]" && l.Contains "Failed to check")
                |> not
            @>

        test <@ cache.Count = 0 @>
        test <@ throws = 0 @>)

/// An activity sink that, on the FIRST check start, starts `second` and holds the first
/// check until the second has joined it (or a bound passes), so the second call is
/// guaranteed to arrive while the first is in flight.
type private JoinProbe() =
    let starts = ref 0
    let joined = new ManualResetEventSlim(false)
    member val Second: Threading.Tasks.Task<FileCheckResult option> option = None with get, set
    member val StartSecond: unit -> Threading.Tasks.Task<FileCheckResult option> = (fun () -> null) with get, set
    member _.Starts = starts.Value
    member _.Joined = joined.IsSet

    interface PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member this.Log message =
            if message.StartsWith "check start" then
                if Interlocked.Increment starts = 1 then
                    this.Second <- Some(this.StartSecond())
                    joined.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            elif message.StartsWith "joining the in-flight check" then
                joined.Set()

        member _.SetSummary _ = ()

[<Fact(Timeout = 60000)>]
let ``a second check of the same unchanged file joins the first instead of cancelling it`` () =
    withTempDir "join-identical" (fun tmpDir ->
        let probe = JoinProbe()

        let checker =
            FSharpChecker.Create(projectCacheSize = 1, keepAssemblyContents = true)

        let pipeline = CheckPipeline(checker, activity = probe)
        let sourceFile = Path.GetFullPath(Path.Combine(tmpDir, "Mod.fs"))
        File.WriteAllLines(sourceFile, [| "module Mod"; "let x = 1" |])

        let options, _ =
            checker.GetProjectOptionsFromScript(sourceFile, SourceText.ofString (File.ReadAllText sourceFile))
            |> Async.RunSynchronously

        pipeline.RegisterProject(Path.Combine(tmpDir, "Mod.fsproj"), options)
        let file = AbsFilePath.create sourceFile
        probe.StartSecond <- fun () -> Async.StartAsTask(pipeline.CheckFile file)

        let first = pipeline.CheckFile file |> Async.RunSynchronously

        let second =
            probe.Second.Value.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

        test <@ probe.Joined @>
        test <@ probe.Starts = 1 @>
        test <@ first.IsSome && second.IsSome @>
        test <@ first.Value.Version = second.Value.Version @>)

[<Fact(Timeout = 60000)>]
let ``a newer check of a changed file still supersedes the running one`` () =
    withTempDir "supersede-changed" (fun tmpDir ->
        let starts = ref 0
        let mutable second: Threading.Tasks.Task<FileCheckResult option> option = None

        let mutable startSecond: unit -> Threading.Tasks.Task<FileCheckResult option> =
            fun () -> null

        let secondStarted = new ManualResetEventSlim(false)
        let sourceFile = ref ""

        let sink =
            { new PluginActivity.IActivitySink with
                member _.StartSubtask(_, _) = ()
                member _.UpdateSubtask(_, _) = ()
                member _.EndSubtask _ = ()

                member _.Log message =
                    if message.StartsWith "check start" then
                        match Interlocked.Increment starts with
                        | 1 ->
                            // Change the file, then check it again while this check runs.
                            File.WriteAllLines(sourceFile.Value, [| "module Mod"; "let x = 2" |])
                            second <- Some(startSecond ())
                            secondStarted.Wait(TimeSpan.FromSeconds 10.0) |> ignore
                        | _ -> secondStarted.Set()

                member _.SetSummary _ = () }

        let checker =
            FSharpChecker.Create(projectCacheSize = 1, keepAssemblyContents = true)

        let pipeline = CheckPipeline(checker, activity = sink)
        sourceFile.Value <- Path.GetFullPath(Path.Combine(tmpDir, "Mod.fs"))
        File.WriteAllLines(sourceFile.Value, [| "module Mod"; "let x = 1" |])

        let options, _ =
            checker.GetProjectOptionsFromScript(
                sourceFile.Value,
                SourceText.ofString (File.ReadAllText sourceFile.Value)
            )
            |> Async.RunSynchronously

        pipeline.RegisterProject(Path.Combine(tmpDir, "Mod.fsproj"), options)
        let file = AbsFilePath.create sourceFile.Value
        startSecond <- fun () -> Async.StartAsTask(pipeline.CheckFile file)

        let first, lines =
            capturingLogs (fun () -> pipeline.CheckFile file |> Async.RunSynchronously)

        let latest =
            second.Value.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

        test <@ starts.Value = 2 @>
        test <@ first = None @>
        test <@ latest.IsSome @>
        test <@ latest.Value.Source.Contains "let x = 2" @>

        test
            <@
                cancellationLines lines
                |> List.contains
                    $"Check of %s{AbsFilePath.value file} was superseded by a newer check of the same file; that check's answer replaces this one. Verdict: unaffected."
            @>)

// A project's options list MSBuild's generated compile items (obj/…AssemblyInfo.fs). In a
// fresh workspace they do not exist yet, and every change batch that reached a dependent
// project checked them on their own and logged "Failed to check …: Unable to find file …"
// for each. They are compiled with the project, never checked on their own.
[<Fact(Timeout = 60000)>]
let ``a generated obj file is never checked on its own, so a missing one logs no failure`` () =
    withTempDir "generated-obj" (fun tmpDir ->
        let pipeline, cache, _ = scriptPipeline tmpDir None
        let options = (pipeline.GetProjectOptions(Path.Combine(tmpDir, "Mod.fsproj"))).Value

        let generated =
            Path.GetFullPath(Path.Combine(tmpDir, "obj", "Debug", "net10.0", "Mod.AssemblyInfo.fs"))

        let options =
            { options with
                SourceFiles = Array.append [| generated |] options.SourceFiles }

        let result, lines =
            capturingLogs (fun () ->
                pipeline.CheckFileWithOptions(AbsFilePath.create generated, options)
                |> Async.RunSynchronously)

        test <@ result = None @>
        test <@ lines |> List.exists (fun l -> l.Contains "Failed to check") |> not @>

        test
            <@
                lines
                |> List.exists (fun l -> l.Contains "generated" && l.Contains "Mod.AssemblyInfo.fs")
            @>

        test <@ cache.Count = 0 @>)

/// An activity sink that holds the FIRST check inside FCS's place in line: on its
/// `check start` it runs `OnStart`, then blocks until `Release` (or a bound passes). The
/// check therefore cannot finish until the test lets it, so "did the caller return
/// before the check finished?" has one answer however loaded the box is.
type private HeldCheck() =
    let starts = ref 0
    let release = new ManualResetEventSlim(false)
    let joined = new ManualResetEventSlim(false)
    let unwanted = new ManualResetEventSlim(false)
    member val OnStart: unit -> unit = ignore with get, set
    member _.Starts = starts.Value
    member _.Release() = release.Set()
    member _.Joined = joined
    member _.FinishedUnwanted = unwanted

    interface PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member this.Log message =
            if message.StartsWith "check start" then
                if Interlocked.Increment starts = 1 then
                    this.OnStart()
                    release.Wait(TimeSpan.FromSeconds 45.0) |> ignore
            elif message.StartsWith "joining the in-flight check" then
                joined.Set()
            elif message.Contains "no caller waiting" then
                unwanted.Set()

        member _.SetSummary _ = ()

/// How long a caller that gave up may take to return while the check it left is held.
/// Generous: it is bounded by scheduling, never by FCS, which the hold keeps waiting.
let private leaveBound = TimeSpan.FromSeconds 20.0

[<Fact(Timeout = 60000)>]
// A caller that gives up stops waiting at once. It used to wait for FCS to finish a check
// it no longer wanted, so a cancelled scan or a stopping session was held for as long as
// the slowest check it had started.
let ``a caller that gives up on a running check returns at once, without waiting for FCS`` () =
    withTempDir "leave-at-once" (fun tmpDir ->
        use cts = new CancellationTokenSource()
        let held = HeldCheck()
        held.OnStart <- cts.Cancel
        let pipeline, cache, file = scriptPipeline tmpDir (Some held)

        let caller =
            pipeline.CheckFile(file, cts.Token)
            |> CheckCaller.within "scan"
            |> Async.StartAsTask

        let returnedWhileHeld = caller.Wait leaveBound
        held.Release()

        test <@ returnedWhileHeld @>
        test <@ caller.Result = None @>
        test <@ held.Starts = 1 @>
        test <@ cache.Count = 0 @>)

[<Fact(Timeout = 60000)>]
// The check is a producer two callers lease. The caller that started it leaving takes
// neither the check nor the other caller's answer: one FCS check, one answer.
let ``a caller that joined a check keeps it, and its answer, when the caller that started it gives up`` () =
    withTempDir "lease-survives" (fun tmpDir ->
        use starterCts = new CancellationTokenSource()
        let held = HeldCheck()
        let pipeline, _, file = scriptPipeline tmpDir (Some held)
        let mutable joiner: Threading.Tasks.Task<FileCheckResult option> = null

        held.OnStart <-
            fun () ->
                joiner <-
                    pipeline.CheckFile file
                    |> CheckCaller.within "change batch"
                    |> Async.StartAsTask

                held.Joined.Wait(TimeSpan.FromSeconds 20.0) |> ignore
                starterCts.Cancel()

        let starter =
            pipeline.CheckFile(file, starterCts.Token)
            |> CheckCaller.within "scan"
            |> Async.StartAsTask

        let starterLeftWhileHeld = starter.Wait leaveBound
        let joinerStillWaiting = not (isNull joiner) && not joiner.IsCompleted
        held.Release()
        let joined = joiner.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

        test <@ held.Joined.IsSet @>
        test <@ starterLeftWhileHeld @>
        test <@ joinerStillWaiting @>
        test <@ starter.Result = None @>
        test <@ joined.IsSome @>
        test <@ held.Starts = 1 @>)

[<Fact(Timeout = 60000)>]
// FCS is not given the check's token: cancelling a check mid-way is not cooperative-safe
// (ADR-003 records an FCS internal-error storm under cancellation churn). So a check every
// caller left runs to its end, then says so and leaves the registry, keeping no typed
// tree alive for an answer nobody will read.
let ``a check every caller left finishes, says nobody was waiting, and leaves the registry`` () =
    withTempDir "unwanted-retired" (fun tmpDir ->
        use cts = new CancellationTokenSource()
        let held = HeldCheck()
        held.OnStart <- cts.Cancel
        let pipeline, _, file = scriptPipeline tmpDir (Some held)

        let caller =
            pipeline.CheckFile(file, cts.Token)
            |> CheckCaller.within "scan"
            |> Async.StartAsTask

        let returnedWhileHeld = caller.Wait leaveBound
        let runningWhileHeld = pipeline.RunningChecks
        held.Release()
        let finished = held.FinishedUnwanted.Wait(TimeSpan.FromSeconds 30.0)

        test <@ returnedWhileHeld @>
        test <@ runningWhileHeld = 1 @>
        test <@ finished @>
        test <@ pipeline.RunningChecks = 0 @>)
