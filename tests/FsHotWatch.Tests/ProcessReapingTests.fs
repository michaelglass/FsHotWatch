module FsHotWatch.Tests.ProcessReapingTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
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

            Ok
                { FsHotWatch.Plugin.PreprocessResult.Modified = changedFiles
                  Considered = changedFiles.Length
                  Evidence = "child-spawner" }

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

        // The assertion that fails on the old ordering: without a registry in the
        // spawning context the daemon could not reap the child even in principle.
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
