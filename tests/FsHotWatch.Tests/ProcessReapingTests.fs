module FsHotWatch.Tests.ProcessReapingTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

// AUTOMATION-147 — the daemon must REAP the children its plugins spawned; the
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
/// daemon runs preprocessors in.
type private ChildSpawningPreprocessor
    (entered: System.Threading.ManualResetEventSlim, release: System.Threading.ManualResetEventSlim) =
    let spawned = ResizeArray<Process>()

    member _.Spawned = spawned |> List.ofSeq

    interface FsHotWatch.Plugin.IFsHotWatchPreprocessor with
        member _.Name = "child-spawner"

        member _.Process (changedFiles: string list) (_repoRoot: string) =
            if spawned.Count = 0 then
                let psi = ProcessStartInfo("sleep", "120")
                psi.UseShellExecute <- false
                let p = Process.Start(psi)
                FsHotWatch.ProcessRegistry.track p
                spawned.Add p
                entered.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 20.0), "fixture preprocessor must be released")

            Ok
                { FsHotWatch.Plugin.PreprocessResult.Modified = changedFiles
                  Considered = changedFiles.Length
                  Evidence = "child-spawner" }

        member _.Dispose() = ()

[<Fact(Timeout = 60000)>]
let ``a child spawned by active daemon work is tracked and reaped on shutdown`` () =
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

        // A registered project gives the scan a non-empty file set, so it actually
        // runs preprocessors — from the scan mailbox's context.
        daemon.RegisterProject(Path.Combine(tmpDir, "Test.fsproj"), options)

        use entered = new System.Threading.ManualResetEventSlim(false)
        use release = new System.Threading.ManualResetEventSlim(false)
        let preprocessor = ChildSpawningPreprocessor(entered, release)
        daemon.RegisterPreprocessor(preprocessor)

        // Hold the actual preprocessor open: operation completion now reaps
        // children itself, so shutdown ownership must be observed while active.
        let scan = Async.StartAsTask(daemon.ScanAll())

        try
            Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "daemon preprocessor must start its child")
            let child = Assert.Single(preprocessor.Spawned)
            Assert.False(child.HasExited)
            Assert.Contains(child, daemon.ProcessRegistry.Snapshot())

            (daemon :> IDisposable).Dispose()
            Assert.True(child.WaitForExit(5000), "daemon shutdown must reap the active operation child")
        finally
            release.Set()

            try
                (daemon :> IDisposable).Dispose()
            finally
                try
                    let settled =
                        System.Threading.Tasks.Task
                            .WhenAny(
                                [| scan :> System.Threading.Tasks.Task
                                   System.Threading.Tasks.Task.Delay(10000) |]
                            )
                            .GetAwaiter()
                            .GetResult()

                    Assert.True(
                        obj.ReferenceEquals(scan, settled),
                        "the original scan task must settle after fixture release"
                    )

                    try
                        scan.GetAwaiter().GetResult()
                    with
                    | :? OperationCanceledException -> ()
                    | :? AggregateException as failure when
                        failure.Flatten().InnerExceptions
                        |> Seq.forall (fun inner -> inner :? OperationCanceledException)
                        ->
                        ()
                finally
                    for child in preprocessor.Spawned do
                        try
                            if not child.HasExited then
                                child.Kill(entireProcessTree = true)

                            Assert.True(child.WaitForExit(5000), "fixture-owned child must be reaped")
                        finally
                            child.Dispose())
