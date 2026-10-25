/// Moves the process working directory, as MSBuild evaluation does, so it runs alone.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.InProcessEvaluationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo

/// A workspace loader that evaluates the way MSBuild does in process: it stands the
/// process in the project's directory and, when done, puts back the directory it found.
/// `meanwhile` runs while the process stands there.
type private DirectoryMovingLoader(projectDir: string, before: unit -> unit, meanwhile: unit -> unit) =
    let notifications = Event<Types.WorkspaceProjectState>()

    member private _.Load() : seq<Types.ProjectOptions> =
        before ()
        evaluateInProjectDirectory projectDir meanwhile
        Seq.empty

    interface IWorkspaceLoader with
        member this.LoadProjects(_projectPaths) = this.Load()
        member this.LoadProjects(_projectPaths, _customProperties, _binaryLog) = this.Load()
        member this.LoadSln(_solutionPath) = this.Load()
        member this.LoadSln(_solutionPath, _customProperties, _binaryLog) = this.Load()

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private repository (root: string) (name: string) : string =
    let projectDir = Path.Combine(root, name, "src")
    Directory.CreateDirectory projectDir |> ignore
    File.WriteAllText(Path.Combine(projectDir, $"%s{name}.fsproj"), "<Project />")
    projectDir

[<Fact(Timeout = 60000)>]
let ``overlapping evaluations leave the process standing where it stood`` () =
    // Two daemons in one process evaluate at once: the first stands in its project, the
    // second stands in its own, the first finishes and puts back where it started, and
    // the second puts back where IT started, the first project. Its repository is then
    // deleted, and every relative path and `Process.Start` in the process fails.
    // Evaluations that overlap only through this ordering wait out the bound instead.
    withTempDir "eval-overlap" (fun root ->
        let firstDir = repository root "first"
        let secondDir = repository root "second"
        use firstMoved = new ManualResetEventSlim(false)
        use secondMoved = new ManualResetEventSlim(false)
        use firstDone = new ManualResetEventSlim(false)
        let bound = TimeSpan.FromSeconds 2.0

        let first =
            DirectoryMovingLoader(
                firstDir,
                ignore,
                fun () ->
                    firstMoved.Set()
                    secondMoved.Wait bound |> ignore
            )

        let second =
            DirectoryMovingLoader(
                secondDir,
                (fun () -> firstMoved.Wait bound |> ignore),
                fun () ->
                    secondMoved.Set()
                    firstDone.Wait bound |> ignore
            )

        let daemonFor (repoRoot: string) loader =
            Daemon.createWithWorkspaceLoader
                Unchecked.defaultof<FSharpChecker>
                repoRoot
                oneShotDaemonOptions
                loader
                (fun _ -> [])

        withProcessWorkingDirectory (Some root) (fun () ->
            use firstDaemon = daemonFor (Path.Combine(root, "first")) first
            use secondDaemon = daemonFor (Path.Combine(root, "second")) second

            let firstRun =
                Task.Run(fun () ->
                    firstDaemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously |> ignore
                    firstDone.Set())

            let secondRun =
                Task.Run(fun () -> secondDaemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously |> ignore)

            test <@ Task.WaitAll([| firstRun; secondRun |], TimeSpan.FromSeconds 30.0) @>
            test <@ deleteWhileWritten (Path.Combine(root, "first")) 10000 @>
            test <@ processWorkingDirectoryExists () @>))

[<Fact(Timeout = 60000)>]
let ``an evaluation in flight when a test moves back cannot leave the process in the test's scratch tree`` () =
    // The cascade behind AUTOMATION-1295, in one test. The host verb stands the process in
    // its control directory, inside the test's scratch tree; a session's evaluation starts
    // there and notes it; the test moves the process back while that evaluation runs; the
    // evaluation then puts back the directory IT noted; and the scratch tree is deleted
    // with the process standing in it. Every relative path and `Process.Start` in the test
    // host fails after that, in whichever test runs next.
    withProcessWorkingDirectory None (fun () ->
        let original = Directory.GetCurrentDirectory()
        let mutable afterEvaluation = ""

        withTempDir "eval-inflight" (fun root ->
            let projectDir = repository root "inflight"
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)

            let loader =
                DirectoryMovingLoader(
                    projectDir,
                    ignore,
                    fun () ->
                        entered.Set()
                        release.Wait(TimeSpan.FromSeconds 10.0) |> ignore
                )

            use daemon =
                Daemon.createWithWorkspaceLoader
                    Unchecked.defaultof<FSharpChecker>
                    (Path.Combine(root, "inflight"))
                    oneShotDaemonOptions
                    loader
                    (fun _ -> [])

            let evaluation =
                withProcessWorkingDirectory (Some root) (fun () ->
                    let run =
                        Task.Run(fun () -> daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously |> ignore)

                    test <@ entered.Wait(TimeSpan.FromSeconds 30.0) @>
                    // The evaluation finishes only once the test is on its way back out.
                    Task.Delay(500).ContinueWith(fun (_: Task) -> release.Set()) |> ignore
                    run)

            test <@ evaluation.Wait(TimeSpan.FromSeconds 30.0) @>
            afterEvaluation <- Directory.GetCurrentDirectory())

        test <@ processWorkingDirectoryExists () @>
        test <@ afterEvaluation = original @>)

[<Fact(Timeout = 60000)>]
let ``deleting a scratch tree the process stands in fails the test that did it and moves the process out`` () =
    // A test that leaves the process standing in its own scratch tree (as the host verb
    // does) must fail ITSELF, with the process moved somewhere that exists, rather than
    // delete the directory and break every test after it.
    withProcessWorkingDirectory None (fun () ->
        let mutable scratch = ""

        let raised =
            try
                withTempDir "stood-in" (fun dir ->
                    scratch <- dir
                    let control = Path.Combine(dir, "state", "control")
                    Directory.CreateDirectory control |> ignore
                    standLikeTheHostVerb control)

                None
            with :? InvalidOperationException as ex ->
                Some ex.Message

        test <@ processWorkingDirectoryExists () @>
        test <@ raised |> Option.exists (fun message -> message.Contains scratch) @>
        test <@ not (Directory.Exists scratch) @>)

[<Fact(Timeout = 15000)>]
let ``after a test that leaves the process in a deleted directory, the guard fails it and moves the process out`` () =
    withProcessWorkingDirectory None (fun () ->
        let gone = Path.Combine(Path.GetTempPath(), $"fshw-gone-{Guid.NewGuid():N}")
        Directory.CreateDirectory gone |> ignore
        standLikeTheHostVerb gone
        Directory.Delete gone

        let failure = ProcessWorkingDirectoryGuard.check "the test that left"

        test <@ processWorkingDirectoryExists () @>

        test
            <@
                failure
                |> Option.exists (fun message -> message.Contains "'the test that left'")
            @>
        // The tests after it find a working directory, and pass the guard.
        test <@ ProcessWorkingDirectoryGuard.check "the test after it" = None @>)

[<Fact(Timeout = 15000)>]
let ``the test assembly guards the process working directory after every test`` () =
    let guards =
        Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(true)
        |> Array.filter (fun a -> a :? ProcessWorkingDirectoryGuard.ProcessWorkingDirectoryGuardAttribute)

    test <@ guards.Length = 1 @>
