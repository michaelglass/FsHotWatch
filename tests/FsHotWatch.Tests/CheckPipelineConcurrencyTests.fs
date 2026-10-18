module FsHotWatch.Tests.CheckPipelineConcurrencyTests

open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// An activity sink that blocks every check it sees start for `hold`, as a check
/// blocked on I/O or a lock would, and records how many were inside at once.
type private BlockingChecks(hold: int) =
    let mutable inside = 0
    let mutable peak = 0

    member _.Peak = Volatile.Read &peak

    interface PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member _.Log message =
            if message.StartsWith "check start" then
                let now = Interlocked.Increment &inside

                let rec raise () =
                    let seen = Volatile.Read &peak

                    if now > seen && Interlocked.CompareExchange(&peak, now, seen) <> seen then
                        raise ()

                raise ()
                // Blocks the thread, not just the async: thread-pool starvation is how an
                // unbounded fan-out grew to hundreds of threads.
                Thread.Sleep hold
                Interlocked.Decrement &inside |> ignore

        member _.SetSummary _ = ()

/// A script project of `count` one-line files in `dir`, registered with `pipeline`.
let private manyFiles (checker: FSharpChecker) (pipeline: CheckPipeline) (dir: string) (count: int) =
    let files =
        [ for i in 1..count do
              let path = Path.GetFullPath(Path.Combine(dir, $"M%d{i}.fs"))
              File.WriteAllLines(path, [| $"module M%d{i}"; $"let x = %d{i}" |])
              path ]

    let options, _ =
        checker.GetProjectOptionsFromScript(
            List.head files,
            SourceText.ofString (File.ReadAllText(List.head files)),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    let options =
        { options with
            SourceFiles = Array.ofList files }

    pipeline.RegisterProject(Path.Combine(dir, "Many.fsproj"), options)
    files |> List.map AbsFilePath.create

[<Fact(Timeout = 120000)>]
let ``no more checks run at once than the pipeline's bound, however many are asked for`` () =
    withTempDir "check-bound" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let sink = BlockingChecks(hold = 150)

        let pipeline =
            CheckPipeline(checker, activity = sink, repoRoot = dir, maxConcurrentChecks = 2)

        let files = manyFiles checker pipeline dir 12

        let results =
            files |> List.map pipeline.CheckFile |> Async.Parallel |> Async.RunSynchronously

        test <@ results |> Array.forall Option.isSome @>
        test <@ sink.Peak >= 1 && sink.Peak <= 2 @>
        test <@ pipeline.CheckConcurrencyPeak <= 2 @>)

[<Fact(Timeout = 120000)>]
let ``a waiting check whose caller gives up leaves without taking a slot`` () =
    withTempDir "check-bound-cancel" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let sink = BlockingChecks(hold = 300)

        let pipeline =
            CheckPipeline(checker, activity = sink, repoRoot = dir, maxConcurrentChecks = 1)

        let files = manyFiles checker pipeline dir 3
        use cts = new CancellationTokenSource()

        let first = pipeline.CheckFile(List.head files) |> Async.StartAsTask
        // Queued behind the first, then abandoned by its caller.
        let queued = pipeline.CheckFile(List.item 1 files, cts.Token) |> Async.StartAsTask
        cts.Cancel()

        test <@ queued.Result = None @>
        test <@ first.Result |> Option.isSome @>
        // The slot the abandoned call never took is still there for the next one.
        test <@ pipeline.CheckFile(List.item 2 files) |> Async.RunSynchronously |> Option.isSome @>)

[<Fact(Timeout = 120000)>]
let ``checking every file of a project builds its snapshot once`` () =
    withTempDir "check-snapshot-once" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let pipeline = CheckPipeline(checker, repoRoot = dir)
        let files = manyFiles checker pipeline dir 8

        files
        |> List.map pipeline.CheckFile
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

        test <@ pipeline.SnapshotBuilds = 1L @>)

[<Fact(Timeout = 120000)>]
let ``two sessions checking through one scheduler share its bound`` () =
    withTempDir "check-bound-shared" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let sink = BlockingChecks(hold = 150)
        let scheduler = WorkScheduler.Scheduler 2

        let session name =
            let root = Path.Combine(dir, name)
            Directory.CreateDirectory root |> ignore

            let pipeline =
                CheckPipeline(checker, activity = sink, repoRoot = root, admission = scheduler.Lane name)

            pipeline, manyFiles checker pipeline root 6

        let a, aFiles = session "a"
        let b, bFiles = session "b"

        let results =
            (aFiles |> List.map a.CheckFile) @ (bFiles |> List.map b.CheckFile)
            |> Async.Parallel
            |> Async.RunSynchronously

        test <@ results |> Array.forall Option.isSome @>
        // Each pipeline alone would allow 2, so 4 together without the shared bound.
        test <@ sink.Peak >= 1 && sink.Peak <= 2 @>
        test <@ scheduler.Running = 0 && scheduler.Queued = 0 @>)

[<Fact(Timeout = 120000)>]
let ``a session flooding the checker cannot keep another session's check behind its queue`` () =
    withTempDir "check-fair-shared" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let sink = BlockingChecks(hold = 100)
        let scheduler = WorkScheduler.Scheduler 1

        let session name count =
            let root = Path.Combine(dir, name)
            Directory.CreateDirectory root |> ignore

            let pipeline =
                CheckPipeline(checker, activity = sink, repoRoot = root, admission = scheduler.Lane name)

            pipeline, manyFiles checker pipeline root count

        let a, aFiles = session "a" 12
        let b, bFiles = session "b" 1

        let flood = [ for f in aFiles -> a.CheckFile f |> Async.StartAsTask ]

        let deadline = System.DateTime.UtcNow.AddSeconds 30.0

        while scheduler.Queued < 8 && System.DateTime.UtcNow < deadline do
            Thread.Sleep 5

        test <@ scheduler.Queued >= 8 @>

        let one = b.CheckFile(List.head bFiles) |> Async.StartAsTask
        test <@ one.Result |> Option.isSome @>
        let aDoneWhenBFinished = flood |> List.filter _.IsCompleted |> List.length

        // Served in turn, B waits behind the running check and at most one more of
        // A's; in arrival order it would wait behind all eight or more queued.
        test <@ aDoneWhenBFinished <= 4 @>
        test <@ flood |> List.forall (fun t -> t.Result |> Option.isSome) @>)

/// An activity sink that counts the checks it sees start.
type private StartedChecks() =
    let mutable started = 0

    member _.Started = Volatile.Read &started

    interface PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member _.Log message =
            if message.StartsWith "check start" then
                Interlocked.Increment &started |> ignore

        member _.SetSummary _ = ()

/// A one-file project at `root/src/Lib` whose check takes FCS long enough to cancel
/// mid-way: a chain of inferred bindings, then a planted type error.
let private slowProject (checker: FSharpChecker) (root: string) =
    let dir = Path.Combine(root, "src", "Lib")
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, "Lib.fs")

    File.WriteAllLines(
        file,
        [| yield "module Lib"
           yield "let f0 (x: int) = [ x ]"
           for i in 1..800 do
               yield $"let f%d{i} x = f%d{i - 1} x |> List.map (fun v -> v + %d{i}) |> List.rev"
           yield "let answer: int = \"no\"" |]
    )

    let fsproj = Path.Combine(dir, "Lib.fsproj")

    File.WriteAllText(
        fsproj,
        """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""
    )

    let options, _ =
        checker.GetProjectOptionsFromScript(
            file,
            SourceText.ofString (File.ReadAllText file),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    { options with
        ProjectFileName = fsproj
        SourceFiles = [| file |]
        UseScriptResolutionRules = false },
    AbsFilePath.create file

[<Fact(Timeout = 300000)>]
let ``work two sessions share survives one of them cancelling`` () =
    withTempDir "check-shared-cancel" (fun dir ->
        // A repository host's two sessions of identical worktrees: one checker, one
        // scheduler, one registry of canonical projects, each framed under the virtual root.
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let scheduler = WorkScheduler.Scheduler 4
        let canonical = CanonicalProjects.Registry()
        let virtualRoot = Path.Combine(dir, "virtual")

        let session name =
            let root = Path.Combine(dir, name)
            let sink = StartedChecks()

            let pipeline =
                CheckPipeline(
                    checker,
                    activity = sink,
                    repoRoot = root,
                    frames = SessionFrames.choice canonical root virtualRoot ignore,
                    admission = scheduler.Lane name
                )

            let options, file = slowProject checker root
            pipeline.RegisterProject(options.ProjectFileName, options)
            pipeline, sink, options, file

        let a, aStarted, aOptions, aFile = session "a"
        let b, bStarted, _, bFile = session "b"

        let waitFor what (condition: unit -> bool) =
            let deadline = System.DateTime.UtcNow.AddSeconds 60.0

            while not (condition ()) && System.DateTime.UtcNow < deadline do
                Thread.Sleep 2

            if not (condition ()) then
                failwith $"timed out waiting for %s{what}"

        let aCheck = a.CheckFile aFile |> Async.StartAsTask
        waitFor "A's check to start" (fun () -> aStarted.Started = 1)
        let bCheck = b.CheckFile bFile |> Async.StartAsTask
        waitFor "B's check to start" (fun () -> bStarted.Started = 1)

        // Session A gives up everything it is checking, as its rediscovery does.
        a.PrepareForRediscovery()
        // Otherwise the cancel came too late to prove anything.
        let bFinishedBeforeCancel = bCheck.IsCompleted
        test <@ not bFinishedBeforeCancel @>

        test <@ aCheck.Result = None @>

        let fullCheck (result: FileCheckResult option) =
            match result with
            | None -> failwith "session B's check was lost when session A cancelled"
            | Some result ->
                test <@ result.Frame.IsSome @>

                match result.CheckResults with
                | FullCheck results -> results
                | ParseOnly -> failwith "session B's check was aborted when session A cancelled"

        let bResults = fullCheck bCheck.Result
        test <@ bResults.Diagnostics |> Array.exists (fun d -> d.Message.Contains "string") @>

        // The work was one computation, shared: A, asking again, is handed the very
        // answer B's check finished.
        a.RegisterProject(aOptions.ProjectFileName, aOptions)
        let aAgain = fullCheck (a.CheckFile aFile |> Async.RunSynchronously)
        test <@ obj.ReferenceEquals(aAgain, bResults) @>

        test <@ scheduler.Running = 0 && scheduler.Queued = 0 @>)
