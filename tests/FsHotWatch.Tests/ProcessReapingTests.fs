module FsHotWatch.Tests.ProcessReapingTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

// the daemon must REAP the children its plugins spawned; the
// wedge self-heal restarts on the same graceful path as `fshw stop`, so a leak
// there re-creates the mess the operator was clearing with `kill -9`.
//
// It leaked on an ordering bug: the process registry is an `AsyncLocal`, visible
// only to ExecutionContexts captured AFTER it is set, and the daemon installed it
// in its CONSTRUCTOR — after the PluginHost and the scan/change mailboxes already
// existed. `ProcessRegistry.track` then silently dropped the child and `KillAll`
// reaped nothing.
//
// So the child is spawned from a PREPROCESSOR: preprocessors run INSIDE the scan
// mailbox (`performScan` -> `RunPreprocessors`), the context that was blind to the
// registry. Spawning from the test thread proves nothing — that thread is the one
// that installed it.

/// Spawns a long-lived child the way `ProcessHelper.runProcess` does
/// (`Process.Start` then `ProcessRegistry.track`), from whatever context the
/// daemon runs preprocessors in, and then HOLDS the preprocessor open until the
/// test releases it: the child must be observed while its work is still active.
type private ChildSpawningPreprocessor
    (entered: ManualResetEventSlim, release: ManualResetEventSlim, returned: ManualResetEventSlim) =
    let spawned = ResizeArray<Process>()

    member _.Spawned = lock spawned (fun () -> List.ofSeq spawned)

    interface FsHotWatch.Plugin.IFsHotWatchPreprocessor with
        member _.Name = "child-spawner"

        member _.Process (changedFiles: string list) (_repoRoot: string) =
            if lock spawned (fun () -> spawned.Count = 0) then
                let psi = ProcessStartInfo("sleep", "120")
                psi.UseShellExecute <- false
                let p = Process.Start(psi)
                lock spawned (fun () -> spawned.Add p)
                FsHotWatch.ProcessRegistry.track p
                entered.Set()

                try
                    Assert.True(release.Wait(TimeSpan.FromSeconds 30.0), "the fixture preprocessor must be released")
                finally
                    returned.Set()

            Ok
                { FsHotWatch.Plugin.PreprocessResult.Modified = changedFiles
                  Considered = changedFiles.Length
                  Evidence = "child-spawner" }

        member _.Dispose() = ()

/// A daemon over a one-file project, with `preprocessor` registered: a non-empty file
/// set makes the scan actually run preprocessors — from the scan mailbox's context.
let private withSpawningDaemon (preprocessor: ChildSpawningPreprocessor) (body: Daemon -> 'a) =
    withTempDir "reaping" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let checker = sharedChecker.Value
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        let sourceFile = Path.Combine(tmpDir, "src", "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")
        let absSource = Path.GetFullPath sourceFile

        let options, _ =
            checker.GetProjectOptionsFromScript(
                absSource,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText absSource)
            )
            |> Async.RunSynchronously

        let options =
            { options with
                SourceFiles = Array.append options.SourceFiles [| absSource |] |> Array.distinct }

        daemon.RegisterProject(Path.Combine(tmpDir, "Test.fsproj"), options)
        daemon.RegisterPreprocessor(preprocessor)
        body daemon)

/// Reap every child the fixture preprocessor started, through the handles it kept.
let private reapSpawned (preprocessor: ChildSpawningPreprocessor) =
    for child in preprocessor.Spawned do
        try
            if not child.HasExited then
                child.Kill(entireProcessTree = true)

            Assert.True(child.WaitForExit(5000), "the fixture-owned child must be reaped")
        finally
            child.Dispose()

[<Fact(Timeout = 60000)>]
let ``a child spawned by a daemon-dispatched plugin is tracked and reaped on shutdown`` () =
    // Released up front: the preprocessor returns at once and the scan runs to the end.
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(true)
    use returned = new ManualResetEventSlim(false)
    let preprocessor = ChildSpawningPreprocessor(entered, release, returned)

    try
        withSpawningDaemon preprocessor (fun daemon ->
            // Dispatch through the daemon's OWN scan machinery — not the test thread.
            Async.RunSynchronously(daemon.ScanAll(), timeout = 40000)

            let child = Assert.Single(preprocessor.Spawned)
            Assert.False(child.HasExited)

            // The assertion that fails on the old ordering: without a registry in the
            // spawning context the daemon could not reap the child even in principle.
            Assert.Contains(child, daemon.ProcessRegistry.Snapshot())

            // Shutdown (the same path `fshw stop` and the wedge self-heal take) reaps it.
            (daemon :> IDisposable).Dispose()
            Assert.True(child.WaitForExit(10000), "daemon shutdown must reap the child"))
    finally
        reapSpawned preprocessor

[<Fact(Timeout = 60000)>]
let ``a child spawned by active daemon work is tracked and reaped on shutdown`` () =
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    use returned = new ManualResetEventSlim(false)
    let preprocessor = ChildSpawningPreprocessor(entered, release, returned)

    try
        withSpawningDaemon preprocessor (fun daemon ->
            // Dispatch through the daemon's OWN scan machinery — not the test thread.
            //
            // The scan's own reply is NOT awaited: a daemon disposed mid-scan never answers
            // it. What must settle is the preprocessor call the test is holding open.
            Async.StartAsTask(daemon.ScanAll()) |> ignore

            try
                Assert.True(entered.Wait(TimeSpan.FromSeconds 40.0), "the daemon's preprocessor must start its child")
                let child = Assert.Single(preprocessor.Spawned)
                Assert.False(child.HasExited)

                // The assertion that fails on the old ordering: without a registry in the
                // spawning context the daemon could not reap the child even in principle.
                Assert.Contains(child, daemon.ProcessRegistry.Snapshot())

                // Shutdown (the same path `fshw stop` and the wedge self-heal take) reaps it
                // while the work that spawned it is still running.
                (daemon :> IDisposable).Dispose()
                Assert.True(child.WaitForExit(5000), "daemon shutdown must reap the active work's child")
            finally
                release.Set()
                (daemon :> IDisposable).Dispose()

                if entered.IsSet then
                    Assert.True(
                        returned.Wait(TimeSpan.FromSeconds 20.0),
                        "the held preprocessor must return once released"
                    ))
    finally
        reapSpawned preprocessor
