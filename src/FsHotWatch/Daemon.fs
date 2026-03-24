module FsHotWatch.Daemon

open System.Threading
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Watcher

/// The daemon ties together a warm FSharpChecker, file watcher, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
[<NoComparison; NoEquality>]
type Daemon =
    { Host: PluginHost
      Watcher: FileWatcher
      Checker: FSharpChecker
      RepoRoot: string }

    /// Register a plugin with the daemon's plugin host.
    member this.Register(plugin: IFsHotWatchPlugin) = this.Host.Register(plugin)

    /// Run the daemon until cancellation is requested.
    member this.Run(cancellationToken: CancellationToken) =
        async {
            use _ = this.Watcher :> System.IDisposable

            let tcs =
                System.Threading.Tasks.TaskCompletionSource<unit>()

            use _reg =
                cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

            do! tcs.Task |> Async.AwaitTask
        }

/// Functions for creating and managing daemons.
module Daemon =
    /// Create a daemon with the given checker (internal, for testing).
    let internal createWith (checker: FSharpChecker) (repoRoot: string) =
        let host = PluginHost.create checker repoRoot
        let watcher = FileWatcher.create repoRoot host.EmitFileChanged

        { Host = host
          Watcher = watcher
          Checker = checker
          RepoRoot = repoRoot }

    /// Create a new daemon for the given repository root with a warm FSharpChecker.
    let create (repoRoot: string) =
        let checker =
            FSharpChecker.Create(
                projectCacheSize = 200,
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true,
                parallelReferenceResolution = true
            )

        createWith checker repoRoot
