module FsHotWatch.Tests.ProcessReapingTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

// AUTOMATION-147 — the daemon must REAP the children its plugins spawned.
//
// The wedge self-heal restarts the daemon on the same graceful path as
// `fshw stop`. If that path leaks the wedged plugin's process, the "fix" just
// launders the mess the operator was already cleaning up with `kill -9`.
//
// It DID leak. The process registry is scoped by an `AsyncLocal`, and an
// AsyncLocal is only visible to ExecutionContexts captured AFTER it is set.
// The daemon installed its registry in the `Daemon` CONSTRUCTOR — by which time
// the PluginHost and the scan/change mailboxes already existed. So when the scan
// mailbox dispatched to a plugin, the plugin's `runProcess` resolved NO registry,
// `ProcessRegistry.track` silently dropped the child, `KillAll` reaped nothing,
// and the child outlived the daemon reparented to init. Proven on a live daemon:
//   [PROBE] track pid=69166 -> NO REGISTRY (dropped)
//   [PROBE] KillAll: 0 tracked
//
// This test pins the ordering. It spawns its child from a PREPROCESSOR, because
// preprocessors run INSIDE the scan mailbox (`performScan` -> `RunPreprocessors`)
// — the exact context that used to be blind to the registry. Spawning from the
// test thread would prove nothing: that thread is the one that installed it.

/// A preprocessor that spawns a long-lived child the way `ProcessHelper.runProcess`
/// does — `Process.Start` followed by `ProcessRegistry.track` — from whatever
/// context the daemon runs preprocessors in.
type private ChildSpawningPreprocessor() =
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

            changedFiles

        member _.Dispose() = ()

[<Fact(Timeout = 60000)>]
let ``a child spawned by a daemon-dispatched plugin is tracked and reaped on shutdown`` () =
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

        let preprocessor = ChildSpawningPreprocessor()
        daemon.RegisterPreprocessor(preprocessor)

        // Dispatch through the daemon's OWN scan machinery — not the test thread.
        Async.RunSynchronously(daemon.ScanAll(), timeout = 40000)

        let child =
            match preprocessor.Spawned with
            | [ p ] -> p
            | other -> failwith $"expected exactly one spawned child, got %d{other.Length}"

        test <@ not child.HasExited @>

        // The child must be VISIBLE to the daemon's registry. This is the assertion
        // that fails on the old ordering: the child was spawned into a context with
        // no registry, so the daemon could not have reaped it even in principle.
        let tracked = daemon.ProcessRegistry.Snapshot() |> List.map (fun p -> p.Id)
        test <@ List.contains child.Id tracked @>

        // Shutdown (the same path `fshw stop` and the wedge self-heal take) reaps it.
        (daemon :> IDisposable).Dispose()

        let deadline = DateTime.UtcNow.AddSeconds 10.0

        while not child.HasExited && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep 100

        test <@ child.HasExited @>

        if not child.HasExited then
            child.Kill())
