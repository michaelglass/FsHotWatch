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

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
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

            test <@ Task.WaitAll([| firstRun; secondRun |], HangWait) @>
            test <@ deleteWhileWritten (Path.Combine(root, "first")) 10000 @>
            test <@ processWorkingDirectoryExists () @>))

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
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

                    test <@ entered.Wait(HangWait) @>

                    // The evaluation finishes only once the test is on its way back out: its
                    // move back waits for the evaluation in flight, so release it once this
                    // thread is seen blocked there, not after a guessed delay.
                    let testThread = Thread.CurrentThread

                    Task.Run(fun () ->
                        try
                            waitUntilOr
                                "the test blocking on its way back out"
                                (fun () -> testThread.ThreadState.HasFlag ThreadState.WaitSleepJoin)
                                HangWaitMs
                        finally
                            release.Set())
                    |> ignore

                    run)

            test <@ evaluation.Wait(HangWait) @>
            afterEvaluation <- Directory.GetCurrentDirectory())

        test <@ processWorkingDirectoryExists () @>
        test <@ afterEvaluation = original @>)

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
let ``a read at a settled working directory waits out an evaluation in flight`` () =
    // AUTOMATION-1411: a test reading the working directory while a daemon evaluates
    // read that daemon's scratch project, not its own. The evaluation is held standing in
    // its project; the read must see where the process stood before it, not the project.
    withProcessWorkingDirectory None (fun () ->
        let original = Directory.GetCurrentDirectory()

        withTempDir "eval-read" (fun root ->
            let projectDir = repository root "reader"
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)

            let loader =
                DirectoryMovingLoader(
                    projectDir,
                    ignore,
                    fun () ->
                        entered.Set()
                        release.Wait(HangWait) |> ignore
                )

            use daemon =
                Daemon.createWithWorkspaceLoader
                    Unchecked.defaultof<FSharpChecker>
                    (Path.Combine(root, "reader"))
                    oneShotDaemonOptions
                    loader
                    (fun _ -> [])

            let evaluation =
                Task.Run(fun () -> daemon.DiscoverAndRegisterProjects() |> Async.RunSynchronously |> ignore)

            test <@ entered.Wait(HangWait) @>

            let seen = ref ""

            let reader =
                Thread(ThreadStart(fun () -> seen.Value <- atSettledWorkingDirectory Directory.GetCurrentDirectory))

            reader.Start()

            // Release the evaluation once the reader has read, or is seen waiting for it:
            // never after a guessed delay.
            try
                waitUntilOr
                    "the reader to read, or to wait for the evaluation"
                    (fun () -> not reader.IsAlive || reader.ThreadState.HasFlag ThreadState.WaitSleepJoin)
                    HangWaitMs
            finally
                release.Set()

            test <@ reader.Join(HangWait) @>
            test <@ evaluation.Wait(HangWait) @>
            test <@ seen.Value = original @>))

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
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

[<Fact(Timeout = InProcessEvaluationTestCapMs)>]
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

// --- A deletion waits only for an evaluation in its own tree (AUTOMATION-1499) ---
// `withTempDir` deletes its tree under the evaluation lock, so every test's teardown
// queued behind every other test's in-process MSBuild evaluation, which has no product
// bound: a 15 s cap over a test that never evaluates anything could still be reached.
// The deletion needs to exclude only an evaluation standing in the tree it deletes.

/// `work` on its own thread, with a gate that holds it until released.
let private held (work: (unit -> unit) -> unit) =
    let entered = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)

    let task =
        Task.Run(fun () ->
            work (fun () ->
                entered.Set()
                release.Wait()))

    entered, release, task

[<Fact(Timeout = 4 * HangWaitMs)>]
let ``deleting a tree no evaluation stands in does not wait for an evaluation elsewhere`` () =
    withTempDir "tree-elsewhere" (fun root ->
        let evaluated = Path.Combine(root, "evaluated")
        let deleted = Path.Combine(root, "deleted")

        let entered, release, evaluation =
            held (fun hold -> evaluateInProcessIn [ evaluated ] hold)

        try
            test <@ entered.Wait HangWait @>
            let deletion = Task.Run(fun () -> whileNoEvaluationRunsUnder [ deleted ] ignore)
            test <@ deletion.Wait HangWait @>
        finally
            release.Set()
            evaluation.Wait HangWait |> ignore)

[<Fact(Timeout = 4 * HangWaitMs)>]
let ``deleting a tree an evaluation stands in waits for that evaluation`` () =
    withTempDir "tree-under-evaluation" (fun root ->
        let project = Path.Combine(root, "repo", "src")

        let entered, release, evaluation =
            held (fun hold -> evaluateInProcessIn [ project ] hold)

        let mutable ran = false

        try
            test <@ entered.Wait HangWait @>

            let deletion =
                Task.Run(fun () -> whileNoEvaluationRunsUnder [ Path.Combine(root, "repo") ] (fun () -> ran <- true))

            waitUntilOr "the deletion waiting for the evaluation" (fun () -> treeWaiters () = 1) HangWaitMs
            test <@ not ran @>
            release.Set()
            test <@ deletion.Wait HangWait @>
            test <@ ran @>
        finally
            release.Set()
            evaluation.Wait HangWait |> ignore)

[<Fact(Timeout = 4 * HangWaitMs)>]
let ``an evaluation of a project in a tree being deleted waits for the deletion`` () =
    withTempDir "tree-being-deleted" (fun root ->
        let tree = Path.Combine(root, "repo")

        let entered, release, deletion =
            held (fun hold -> whileNoEvaluationRunsUnder [ tree ] hold)

        let mutable evaluated = false

        try
            test <@ entered.Wait HangWait @>

            let evaluation =
                Task.Run(fun () -> evaluateInProcessIn [ Path.Combine(tree, "src") ] (fun () -> evaluated <- true))

            waitUntilOr "the evaluation waiting for the deletion" (fun () -> treeWaiters () = 1) HangWaitMs
            test <@ not evaluated @>
            release.Set()
            test <@ evaluation.Wait HangWait @>
            test <@ evaluated @>
        finally
            release.Set()
            deletion.Wait HangWait |> ignore)
