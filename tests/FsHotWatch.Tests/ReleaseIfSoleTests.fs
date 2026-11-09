/// A one-shot verb stops the daemon it started only when no other client is using it.
/// Those clients are not always run claims: a `test-rerun` and the read-only verbs hold
/// a connection and no claim, and only the daemon can see those.
module FsHotWatch.Tests.ReleaseIfSoleTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.Program
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

let private heldProject (started: string) (release: string) : TestConfig =
    { Project = "ProjA"
      Command = "sh"
      Args = $"-c \"touch '%s{started}'; while [ ! -f '%s{release}' ]; do sleep 0.05; done; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

[<Fact(Timeout = 60000)>]
let ``a test-rerun holding no claim keeps its daemon through another client's release`` () =
    withTempDir "release-if-sole" (fun repoRoot ->
        let started = Path.Combine(repoRoot, "started")
        let release = Path.Combine(repoRoot, "release")
        use daemon = Daemon.createWith nullChecker repoRoot oneShotDaemonOptions

        daemon.RegisterHandler(
            create ":memory:" repoRoot (Some [ heldProject started release ]) None None None None []
        )

        use cts = new CancellationTokenSource()
        let pipeName = $"release-{Guid.NewGuid():N}"
        let server = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

        try
            awaitServing pipeName (fun () ->
                if server.IsCompleted then
                    Some "the daemon stopped"
                else
                    None)
            // What a `test-rerun` on the daemon sends: one held call, and no run claim.
            let other = Async.StartAsTask(IpcClient.runCommand pipeName "run-tests" "{}")
            waitUntil (fun () -> File.Exists started) 15000

            let link =
                ownDaemonLink defaultIpcOps repoRoot pipeName defaultGlobalOptions AgentEnvelopeTests.fakeConfig 5.0

            releaseUnlessShared repoRoot link.Release

            test <@ IpcClient.isRunning pipeName @>

            File.WriteAllText(release, "")
            other.Wait(TimeSpan.FromSeconds 30.0) |> ignore
            test <@ other.IsCompletedSuccessfully @>

            // The other client gone, the release does stop the daemon.
            releaseUnlessShared repoRoot link.Release
            test <@ not (IpcClient.isRunning pipeName) @>
        finally
            File.WriteAllText(release, "")
            cts.Cancel()

            try
                server.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            with _ ->
                ())

[<Fact(Timeout = 60000)>]
let ``a daemon releases only once a run claim it did not see at the start is gone`` () =
    withTempDir "release-if-sole-claim" (fun repoRoot ->
        use daemon = Daemon.createWith nullChecker repoRoot oneShotDaemonOptions
        use cts = new CancellationTokenSource()
        let pipeName = $"release-{Guid.NewGuid():N}"
        let server = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

        try
            awaitServing pipeName (fun () ->
                if server.IsCompleted then
                    Some "the daemon stopped"
                else
                    None)
            // The claim the CLI would have read before asking, seen by the daemon instead.
            let claim = RunClaim.acquire repoRoot "check" "other-invocation"

            let reply = IpcClient.releaseIfSole pipeName |> Async.RunSynchronously
            test <@ ReleaseReply.keptFor reply <> None @>
            test <@ IpcClient.isRunning pipeName @>

            RunClaim.release repoRoot claim

            let afterClaim = IpcClient.releaseIfSole pipeName |> Async.RunSynchronously
            test <@ afterClaim = ReleaseReply.released @>
        finally
            cts.Cancel()

            try
                server.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            with _ ->
                ())
