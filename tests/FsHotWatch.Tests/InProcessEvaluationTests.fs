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
