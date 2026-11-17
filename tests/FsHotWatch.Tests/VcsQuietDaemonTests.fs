/// A warm daemon that sees a VCS operation move its tree goes quiet until a client asks
/// (AUTOMATION-1705, option b): it neither checks nor tests what the operation rewrote on
/// its own, says so in `status`, checks a later save at once, and answers the first check
/// after the operation for the tree the operation left.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.VcsQuietDaemonTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
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

/// A probe that reads `position`, which the test moves.
let private tracking (position: Result<Position, string> ref) = Probe.Tracked(fun () -> position.Value)

/// What a daemon's plugin was told: each source file with the bytes it read when told,
/// and how many runs it started (one per `FileChanged`, as a test runner starts one).
type private Witness =
    { Told: ConcurrentQueue<string * string>
      Runs: int ref }

/// A daemon over `root` with `probe` as its VCS position, a watcher the test drives by
/// hand, and a witness plugin whose every run takes `runCost`, standing in for a test
/// run. Not yet scanned.
[<WallClock("simulated work: a run's cost stands in for a test suite's, so a check that waits for the run measurably waits; every outcome is awaited by an event, not by the sleep")>]
let private gatedDaemonWith (runCost: TimeSpan) (probe: Probe) (root: string) =
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
                VcsProbe = fun _ -> probe }
            (NoProjects())
            (fun _ -> [])
            watcher

    let witness =
        { Told = ConcurrentQueue()
          Runs = ref 0 }

    daemon.RegisterHandler
        { Name = PluginName.create "vcs-quiet-witness"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged files) ->
                        Interlocked.Increment(&witness.Runs.contents) |> ignore
                        ctx.ReportStatus(Running DateTime.UtcNow)

                        for file in files do
                            witness.Told.Enqueue(file, File.ReadAllText file)

                        do! Async.Sleep runCost
                        ctx.ReportStatus(Completed(DateTime.UtcNow, RunVerdict.create "witnessed" runCost))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    daemon, Option.get callback.Value, witness

let private gatedDaemon (root: string) (position: Result<Position, string> ref) =
    gatedDaemonWith TimeSpan.Zero (tracking position) root

/// `gatedDaemon` after its cold scan: warm, so every later batch is a change batch.
let private warmDaemon (root: string) (position: Result<Position, string> ref) =
    let daemon, deliver, witness = gatedDaemon root position
    daemon.ScanAll() |> Async.RunSynchronously
    daemon, deliver, witness

/// Run `body` against `daemon` served on a pipe of its own, its startup scan settled.
let private served (daemon: Daemon) (body: string -> 'a) : 'a =
    let cts = new CancellationTokenSource()
    let pipeName = $"fshw-{Guid.NewGuid():N}"
    let run = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

    try
        test <@ daemon.StartupScanSettled.Wait(HangWait) @>
        body pipeName
    finally
        cts.Cancel()

        try
            run.Wait(HangWait) |> ignore
        with :? AggregateException ->
            ()

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

/// The bytes of `path` the plugin was told about, oldest first.
let private seen (witness: Witness) (path: string) =
    witness.Told
    |> Seq.filter (fun (f, _) -> real f = real path)
    |> Seq.map snd
    |> List.ofSeq

let private told (witness: Witness) (path: string) = not (List.isEmpty (seen witness path))

let private sourceIn (root: string) (name: string) (text: string) =
    let path = Path.Combine(root, "src", $"{name}.fs")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)
    path

/// The rebase: the position moves from `a` to `b`, and `source` is rewritten.
let private rebase (position: Result<Position, string> ref) (source: string) (text: string) deliver =
    position.Value <- Ok(at 'b')
    File.WriteAllText(source, text)
    deliver (SourceChanged [ source ])

let private waitingNotice =
    VcsQuiet.Notice.Waiting(Position.describe (at 'a'), Position.describe (at 'b'), 1)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a warm daemon given a VCS-operation batch checks nothing and says it waits, until a client asks`` () =
    withTempDir "vcs-quiet-daemon" (fun root ->
        let source = sourceIn root "Rebased" "module Rebased\nlet value = 1\n"
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = warmDaemon root position
        use _ = daemon

        rebase position source "module Rebased\nlet value = 2\n" deliver
        changesSettled daemon
        waitUntilOr "the daemon to own no work" (fun () -> not (daemon.Host.AnyPluginBusy())) HangWaitMs

        test <@ not (told witness source) @>
        test <@ not (ranChangeBatch daemon) @>
        test <@ daemon.VcsNotice() = waitingNotice @>

        // A client asks: what the operation rewrote is checked, and the notice clears.
        test <@ (daemon.ClientAsked "check").Wait(HangWait) @>
        waitUntilOr "the held change to be checked" (fun () -> told witness source) HangWaitMs

        test <@ ranChangeBatch daemon @>
        test <@ daemon.VcsNotice() = VcsQuiet.Notice.Checking @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a save after a VCS operation is an edit and checks at once`` () =
    withTempDir "vcs-save-after-op" (fun root ->
        let rebased = sourceIn root "Rebased" "module Rebased\nlet value = 1\n"
        let saved = sourceIn root "Saved" "module Saved\nlet value = 1\n"
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = warmDaemon root position
        use _ = daemon

        rebase position rebased "module Rebased\nlet value = 2\n" deliver
        changesSettled daemon
        test <@ not (ranChangeBatch daemon) @>

        // The editor saves after the operation: same position, so it is an edit.
        File.WriteAllText(saved, "module Saved\nlet value = 2\n")
        deliver (SourceChanged [ saved ])

        waitUntilOr "the save to be checked" (fun () -> told witness saved) HangWaitMs
        test <@ seen witness saved = [ "module Saved\nlet value = 2\n" ] @>

        // What the operation rewrote still waits for a client, and status still says so.
        test <@ not (told witness rebased) @>
        test <@ daemon.VcsNotice() = waitingNotice @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a warm daemon checks an ordinary edit at once`` () =
    withTempDir "vcs-edit-daemon" (fun root ->
        let source = sourceIn root "Edited" "module Edited\nlet value = 1\n"
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = warmDaemon root position
        use _ = daemon

        File.WriteAllText(source, "module Edited\nlet value = 2\n")
        deliver (SourceChanged [ source ])

        waitUntilOr "the edit to be checked" (fun () -> told witness source) HangWaitMs
        test <@ ranChangeBatch daemon @>
        test <@ daemon.VcsNotice() = VcsQuiet.Notice.Checking @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``status names the operation a waiting daemon waits on, and fshw check's scan request wakes it`` () =
    withTempDir "vcs-quiet-ipc" (fun root ->
        let source = sourceIn root "Switched" "module Switched\nlet value = 1\n"
        let position = ref (Ok(at 'a'))
        let daemon, deliver, _ = gatedDaemon root position
        use _ = daemon

        served daemon (fun pipeName ->
            let ipc = FsHotWatch.Cli.Program.defaultIpcOps

            let notice () =
                ipc.GetDiagnostics pipeName ""
                |> Async.RunSynchronously
                |> FsHotWatch.Cli.IpcParsing.vcsOf

            test <@ notice () = Some VcsQuiet.Notice.Checking @>

            rebase position source "module Switched\nlet value = 2\n" deliver
            changesSettled daemon
            test <@ not (ranChangeBatch daemon) @>

            test <@ notice () = Some waitingNotice @>
            let line = notice () |> Option.bind VcsQuiet.Notice.line |> Option.defaultValue ""

            test
                <@
                    line.Contains "waiting for a client"
                    && line.Contains(Position.describe (at 'b'))
                @>

            FsHotWatch.Cli.Program.forceScanAndWait ipc pipeName None |> ignore

            waitUntilOr "the held change to be checked" (fun () -> ranChangeBatch daemon) HangWaitMs
            test <@ notice () = Some VcsQuiet.Notice.Checking @>))

/// `settleFirst`: the daemon decides the operation's batch before the check (it is held),
/// or the check arrives while that batch is still debouncing (one request carries both).
[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``the first check after a VCS operation answers for the tree the operation left`` (settleFirst: bool) =
    withTempDir "vcs-current-tree" (fun root ->
        let before = "module Rebased\nlet value = 1\n"
        let after = "module Rebased\nlet value = 2\n"
        let source = sourceIn root "Rebased" before
        let position = ref (Ok(at 'a'))
        let daemon, deliver, witness = gatedDaemon root position
        use _ = daemon

        served daemon (fun pipeName ->
            let preOperation = (TreeHash.compute root []).Hash
            rebase position source after deliver

            if settleFirst then
                changesSettled daemon
                test <@ not (told witness source) @>

            let postOperation = (TreeHash.compute root []).Hash
            test <@ preOperation <> postOperation @>

            // `fshw check`: the scan request, then the wait it grades after.
            let ipc = FsHotWatch.Cli.Program.defaultIpcOps
            FsHotWatch.Cli.Program.forceScanAndWait ipc pipeName None |> ignore
            ipc.WaitForComplete pipeName -1 |> Async.RunSynchronously |> ignore

            // Read at the settle boundary, where `check` captures the tree it files under:
            // the plugins have already answered for the rewritten bytes, and nothing for
            // the bytes before the operation.
            let settledTree = (TreeHash.compute root []).Hash
            test <@ settledTree = postOperation @>
            test <@ seen witness source = [ after ] @>))

// ---- AUTOMATION-1705 condition 4: the cost removed and the cost added -----------------

/// What one rebase-then-check cycle cost: runs the daemon started on its own after the
/// rebase, runs started in all, and how long the first check after the rebase took.
type private Cycle =
    { UnaskedRuns: int
      TotalRuns: int
      CheckLatency: TimeSpan }

/// A warm daemon, a rebase, the daemon left to react, then `fshw check` (scan request and
/// the wait it grades after), timed from the request to the answer.
[<WallClock("measurement: the latency of the first check after a rebase is the quantity AUTOMATION-1705 asks for, reported in the log and never asserted")>]
let private rebaseThenCheck (runCost: TimeSpan) (settleFirst: bool) (gated: bool) (root: string) : Cycle =
    let source = sourceIn root "Rebased" "module Rebased\nlet value = 1\n"
    let position = ref (Ok(at 'a'))

    let probe =
        if gated then
            tracking position
        else
            // The behaviour before AUTOMATION-1705: every batch checks.
            Probe.Untracked "measuring the ungated daemon"

    let daemon, deliver, witness = gatedDaemonWith runCost probe root
    use _ = daemon

    served daemon (fun pipeName ->
        rebase position source "module Rebased\nlet value = 2\n" deliver

        if settleFirst then
            changesSettled daemon
            waitUntilOr "the daemon to own no work" (fun () -> not (daemon.Host.AnyPluginBusy())) HangWaitMs

        let unasked = witness.Runs.Value

        let ipc = FsHotWatch.Cli.Program.defaultIpcOps
        let clock = Diagnostics.Stopwatch.StartNew()
        FsHotWatch.Cli.Program.forceScanAndWait ipc pipeName None |> ignore
        ipc.WaitForComplete pipeName -1 |> Async.RunSynchronously |> ignore
        clock.Stop()

        { UnaskedRuns = unasked
          TotalRuns = witness.Runs.Value
          CheckLatency = clock.Elapsed })

[<Fact(Timeout = SyncHangBackstopMs)>]
[<WallClock("measurement: logs the latency of the first check after a rebase, gated and ungated; asserts only the run counts, which do not depend on the box")>]
let ``a rebase starts a run on its own only without the gate, and the first check pays for the run instead`` () =
    let runCost = TimeSpan.FromSeconds 2.0
    let ungated = withTempDir "vcs-measure-ungated" (rebaseThenCheck runCost true false)
    let gated = withTempDir "vcs-measure-gated" (rebaseThenCheck runCost true true)

    Logging.info
        "vcs-measure"
        $"run cost %d{int runCost.TotalMilliseconds} ms | ungated: %d{ungated.UnaskedRuns} unasked run(s), %d{ungated.TotalRuns} in all, first check %d{int ungated.CheckLatency.TotalMilliseconds} ms | gated: %d{gated.UnaskedRuns} unasked run(s), %d{gated.TotalRuns} in all, first check %d{int gated.CheckLatency.TotalMilliseconds} ms"

    test <@ ungated.UnaskedRuns = 1 && ungated.TotalRuns = 1 @>
    test <@ gated.UnaskedRuns = 0 && gated.TotalRuns = 1 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
[<WallClock("measurement: logs the latency of a check that arrives while the rebase's batch is still debouncing, gated and ungated; asserts only the run counts")>]
let ``a check right after a rebase runs once, gated or not`` () =
    let runCost = TimeSpan.FromSeconds 2.0

    let ungated =
        withTempDir "vcs-measure-now-ungated" (rebaseThenCheck runCost false false)

    let gated = withTempDir "vcs-measure-now-gated" (rebaseThenCheck runCost false true)

    Logging.info
        "vcs-measure"
        $"check at once, run cost %d{int runCost.TotalMilliseconds} ms | ungated: %d{ungated.TotalRuns} run(s), first check %d{int ungated.CheckLatency.TotalMilliseconds} ms | gated: %d{gated.TotalRuns} run(s), first check %d{int gated.CheckLatency.TotalMilliseconds} ms"

    test <@ ungated.TotalRuns = 1 && gated.TotalRuns = 1 @>
