/// A warm daemon that sees a VCS operation move its tree goes quiet until a client asks
/// (AUTOMATION-1705, option b): it neither checks nor tests the batch on its own.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.VcsQuietDaemonTests

open System
open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.VcsPosition
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// A workspace with no projects: discovery settles at once on an empty model.
type private NoProjects() =
    let notifications = Event<Types.WorkspaceProjectState>()

    interface IWorkspaceLoader with
        member _.LoadProjects(_projectPaths) = Seq.empty
        member _.LoadProjects(_projectPaths, _customProperties, _binaryLog) = Seq.empty
        member _.LoadSln(_solutionPath) = Seq.empty
        member _.LoadSln(_solutionPath, _customProperties, _binaryLog) = Seq.empty

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private at (commit: char) =
    Position.GitBranch("refs/heads/main", GitRef.Points(String(commit, 40)))

/// Every source batch a warm daemon's plugins were told about.
type private Witness = { Told: ConcurrentQueue<string list> }

/// A daemon over `root` whose VCS position is `position`, with a watcher the test drives
/// by hand, not yet scanned.
let private gatedDaemon (root: string) (position: Result<Position, string> ref) =
    let callback = ref None

    let watcher: Daemon.WatcherFactory =
        fun _ onChange _ _ _ ->
            callback.Value <- Some onChange

            { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
              Disposables = [] }

    let daemon =
        Daemon.createWithWorkspaceLoaderAndWatcher
            nullChecker
            root
            { watchingDaemonOptions with
                VcsProbe = fun _ -> Probe.Tracked(fun () -> position.Value) }
            (NoProjects())
            (fun _ -> [])
            watcher

    let witness = { Told = ConcurrentQueue() }

    daemon.RegisterHandler
        { Name = PluginName.create "vcs-quiet-witness"
          Init = ()
          Update =
            fun _ state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged files) -> witness.Told.Enqueue files
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    daemon, Option.get callback.Value, witness

/// `gatedDaemon` after its cold scan: warm, so every later batch is a change batch.
let private warmDaemon (root: string) (position: Result<Position, string> ref) =
    let daemon, deliver, witness = gatedDaemon root position
    daemon.ScanAll() |> Async.RunSynchronously
    daemon, deliver, witness

/// Wait until the daemon owns no change work: the batch has been admitted and decided.
let private changesSettled (daemon: Daemon) =
    waitUntilOr
        "the change batch to be decided"
        (fun () -> not (List.contains "changes" (daemon.Host.BusyPluginNames())))
        HangWaitMs

/// Whether the daemon ran a change batch: its check phase records every one, and the
/// cold scan records a scan phase, not this.
let private ranChangeBatch (daemon: Daemon) =
    daemon.Host.Phases.Snapshot(DateTime.UtcNow)
    |> List.exists (fun phase -> phase.Scope = "daemon.check")

let private real = FsHotWatch.CommandPreprocessor.realPathOf

let private told (witness: Witness) (path: string) =
    witness.Told
    |> Seq.exists (fun files -> files |> List.exists (fun f -> real f = real path))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a warm daemon given a VCS-operation batch checks nothing until a client asks, then checks it`` () =
    withTempDir "vcs-quiet-daemon" (fun root ->
        let source = Path.Combine(root, "src", "Rebased.fs")
        Directory.CreateDirectory(Path.GetDirectoryName source) |> ignore
        File.WriteAllText(source, "module Rebased\nlet value = 1\n")
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = warmDaemon root position
        use _ = daemon

        // The rebase: the branch moves and rewrites the file.
        position.Value <- Ok(at 'b')
        File.WriteAllText(source, "module Rebased\nlet value = 2\n")
        deliver (SourceChanged [ source ])
        changesSettled daemon

        test <@ not (told witness source) @>
        test <@ not (ranChangeBatch daemon) @>
        waitUntilOr "the daemon to own no work" (fun () -> not (daemon.Host.AnyPluginBusy())) HangWaitMs
        test <@ not (told witness source) @>

        // A further edit while quiet is held with it.
        let later = Path.Combine(root, "src", "Later.fs")
        File.WriteAllText(later, "module Later\n")
        deliver (SourceChanged [ later ])
        changesSettled daemon

        test <@ not (ranChangeBatch daemon) @>

        // A client asks: everything held is checked.
        test <@ (daemon.ClientAsked "check").Wait(HangWait) @>
        waitUntilOr "the held changes to be checked" (fun () -> told witness source && told witness later) HangWaitMs

        test <@ ranChangeBatch daemon @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a warm daemon checks an ordinary edit at once`` () =
    withTempDir "vcs-edit-daemon" (fun root ->
        let source = Path.Combine(root, "src", "Edited.fs")
        Directory.CreateDirectory(Path.GetDirectoryName source) |> ignore
        File.WriteAllText(source, "module Edited\nlet value = 1\n")
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = warmDaemon root position
        use _ = daemon

        File.WriteAllText(source, "module Edited\nlet value = 2\n")
        deliver (SourceChanged [ source ])

        waitUntilOr "the edit to be checked" (fun () -> told witness source) HangWaitMs
        test <@ ranChangeBatch daemon @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the scan request fshw check sends wakes a quiet daemon`` () =
    withTempDir "vcs-quiet-ipc" (fun root ->
        let source = Path.Combine(root, "src", "Switched.fs")
        Directory.CreateDirectory(Path.GetDirectoryName source) |> ignore
        File.WriteAllText(source, "module Switched\nlet value = 1\n")
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = gatedDaemon root position
        use _ = daemon
        let cts = new System.Threading.CancellationTokenSource()
        let pipeName = $"fshw-{Guid.NewGuid():N}"
        let run = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

        try
            test <@ daemon.StartupScanSettled.Wait(HangWait) @>

            position.Value <- Ok(at 'b')
            File.WriteAllText(source, "module Switched\nlet value = 2\n")
            deliver (SourceChanged [ source ])
            changesSettled daemon
            test <@ not (ranChangeBatch daemon) @>

            let ipc = FsHotWatch.Cli.Program.defaultIpcOps
            FsHotWatch.Cli.Program.forceScanAndWait ipc pipeName None |> ignore

            waitUntilOr "the held change to be checked" (fun () -> ranChangeBatch daemon) HangWaitMs
        finally
            cts.Cancel()

            try
                run.Wait(HangWait) |> ignore
            with :? AggregateException ->
                ())
