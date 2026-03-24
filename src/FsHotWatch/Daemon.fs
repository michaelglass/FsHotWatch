module FsHotWatch.Daemon

open System.Threading
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Watcher

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
[<NoComparison; NoEquality>]
type Daemon =
    { Host: PluginHost
      Watcher: FileWatcher
      Checker: FSharpChecker
      Pipeline: CheckPipeline
      RepoRoot: string }

    /// Register a plugin with the daemon's plugin host.
    member this.Register(plugin: IFsHotWatchPlugin) = this.Host.Register(plugin)

    /// Register a project's options so its files can be checked incrementally.
    member this.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        this.Pipeline.RegisterProject(projectPath, options)

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

    /// Run the daemon with IPC server on the given pipe name.
    member this.RunWithIpc(pipeName: string, cancellationToken: CancellationToken) =
        async {
            use _ = this.Watcher :> System.IDisposable

            let ipcTask =
                Async.StartAsTask(IpcServer.start pipeName this.Host cancellationToken)

            let tcs =
                System.Threading.Tasks.TaskCompletionSource<unit>()

            use _reg =
                cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

            do! tcs.Task |> Async.AwaitTask

            try
                ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
            with
            | _ -> ()
        }

/// Functions for creating and managing daemons.
module Daemon =
    let private defaultDebounceMs = 500

    /// Create a daemon with the given checker (internal, for testing).
    let internal createWith (checker: FSharpChecker) (repoRoot: string) =
        let host = PluginHost.create checker repoRoot
        let pipeline = CheckPipeline(checker)

        // Debounce: accumulate changes, dispatch after quiet period
        let pendingChanges = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let mutable debounceTimer: System.Threading.Timer option = None
        let debounceLock = obj ()

        let processChanges (_state: obj) =
            let changes = System.Collections.Generic.List<FileChangeKind>()
            let mutable item = Unchecked.defaultof<_>

            while pendingChanges.TryTake(&item) do
                changes.Add(item)

            if changes.Count > 0 then
                // Merge all SourceChanged files into one batch
                let allSourceFiles =
                    changes
                    |> Seq.collect (fun c ->
                        match c with
                        | SourceChanged files -> files
                        | _ -> [])
                    |> Seq.distinct
                    |> Seq.toList

                let hasProjectChange =
                    changes |> Seq.exists (fun c -> match c with ProjectChanged _ -> true | _ -> false)

                let hasSolutionChange =
                    changes |> Seq.exists (fun c -> match c with SolutionChanged -> true | _ -> false)

                // Emit batched events
                if hasSolutionChange then
                    host.EmitFileChanged(SolutionChanged)

                if hasProjectChange then
                    let projFiles =
                        changes
                        |> Seq.collect (fun c ->
                            match c with ProjectChanged f -> f | _ -> [])
                        |> Seq.distinct
                        |> Seq.toList
                    host.EmitFileChanged(ProjectChanged projFiles)

                if not allSourceFiles.IsEmpty then
                    host.EmitFileChanged(SourceChanged allSourceFiles)

                    for file in allSourceFiles do
                        let result = pipeline.CheckFile(file) |> Async.RunSynchronously

                        match result with
                        | Some checkResult -> host.EmitFileChecked(checkResult)
                        | None -> ()

        let onChange change =
            pendingChanges.Add(change)

            lock debounceLock (fun () ->
                match debounceTimer with
                | Some timer -> timer.Change(defaultDebounceMs, System.Threading.Timeout.Infinite) |> ignore
                | None ->
                    debounceTimer <-
                        Some(
                            new System.Threading.Timer(
                                System.Threading.TimerCallback(processChanges),
                                null,
                                defaultDebounceMs,
                                System.Threading.Timeout.Infinite
                            )
                        ))

        let watcher = FileWatcher.create repoRoot onChange

        { Host = host
          Watcher = watcher
          Checker = checker
          Pipeline = pipeline
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
