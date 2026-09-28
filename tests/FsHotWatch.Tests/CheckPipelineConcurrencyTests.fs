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
            files
            |> List.map (fun f -> pipeline.CheckFile f)
            |> Async.Parallel
            |> Async.RunSynchronously

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
        |> List.map (fun f -> pipeline.CheckFile f)
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore

        test <@ pipeline.SnapshotBuilds = 1L @>)
