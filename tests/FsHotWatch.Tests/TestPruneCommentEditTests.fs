/// An edit that changes a file's bytes but none of its symbols — a comment in a test
/// file — after a green run. The symbol diff owes nothing, but the tree the evidence was
/// earned on is gone, so what `test-scope` reports next must still be a verdict or a
/// named reason, never a silent "no tests ran".
module FsHotWatch.Tests.TestPruneCommentEditTests

open System
open System.IO
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Cli.IpcParsing

let private testSource comment =
    $"module P1Tests\n\n// %s{comment}\nlet ``adds`` () = 1 + 1\n"

let private scopeOf (host: PluginHost) =
    match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
    | Some reply -> (parseTestRunReport reply).Scope
    | None -> failwith "no test-scope command"

/// Land one build and wait for the plugin to settle on it, whether it ran or replayed.
let private landBuild (host: PluginHost) =
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted BuildSucceeded
    Assert.True(terminal.Wait(TimeSpan.FromSeconds 15.0), "test-prune never reported a terminal status")
    waitForQuiescent host 20000

[<Fact(Timeout = 60000)>]
let ``a comment-only edit to a test file after a green run is re-verified`` () =
    withTempDir "tp-comment-edit" (fun tmpDir ->
        let testFile = Path.Combine(tmpDir, "tests", "P1", "Tests.fs")
        Directory.CreateDirectory(Path.GetDirectoryName testFile) |> ignore
        File.WriteAllText(testFile, testSource "original comment")
        let runs = Path.Combine(tmpDir, "p1-runs")
        // A repository that has earned its full-suite baseline before, as a checkout
        // used day to day has: the first run of this session is then an ordinary one,
        // and its green is what a later build of the same symbols is keyed against.
        seedBaseline tmpDir [ "P1" ]

        let config =
            { PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) with
                Args = $"-c \"echo run >> {runs}; exit 0\"" }

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some [ config ]) None None None None [])

        try
            // The session's first run executes P1 in full and earns evidence on this tree.
            landBuild host

            match scopeOf host with
            | FullSuite 1 -> ()
            | other -> Assert.Fail($"positive control: the first build runs P1 in full, got %A{other}")

            let runCount () = File.ReadAllLines(runs).Length
            let runsBefore = runCount ()

            // Only a comment changes: the symbols, the project files and the build
            // outcome are what they were.
            File.WriteAllText(testFile, testSource "edited comment")

            // Before any build: the evidence is for the old tree, and the reading says so.
            match scopeOf host with
            | NoTestsRun NoTestsReason.TreeMoved -> ()
            | other -> Assert.Fail($"an edited tree with no run on it must read tree-moved, got %A{other}")

            landBuild host

            match scopeOf host with
            | FullSuite 1 -> Assert.Equal(runsBefore + 1, runCount ())
            | other -> Assert.Fail($"a comment-only edit must be re-verified on the edited tree, got %A{other}")

            // The tree is verified now: another build over it runs nothing more.
            landBuild host
            Assert.Equal(runsBefore + 1, runCount ())

            match scopeOf host with
            | FullSuite 1 -> ()
            | other -> Assert.Fail($"the verified tree keeps its evidence, got %A{other}")
        finally
            host.Teardown())

[<Fact(Timeout = 30000)>]
let ``before any run the scope says no run has completed`` () =
    withTempDir "tp-no-run-yet" (fun tmpDir ->
        let config =
            PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never"))

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some [ config ]) None None None None [])

        try
            match scopeOf host with
            | NoTestsRun NoTestsReason.NoRunYet -> ()
            | other -> Assert.Fail($"expected no-run-yet, got %A{other}")
        finally
            host.Teardown())

let private receipt tree =
    { InputTreeHash = tree
      RunId = Guid.NewGuid()
      Coverage = RunCoverage.none
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

[<Fact(Timeout = 10000)>]
let ``every reading with no evidence for the tree names why`` () =
    let run = [ Guid.NewGuid() ]
    let classify = UnverifiedTree.classify

    Assert.Equal(UnverifiedTree.CoversNothing, classify (Some(receipt (Some "a"))) (Some "a") None run)
    Assert.Equal(UnverifiedTree.TreeMoved, classify (Some(receipt (Some "a"))) (Some "b") None run)
    Assert.Equal(UnverifiedTree.TreeUnreadable, classify (Some(receipt (Some "a"))) None None run)
    Assert.Equal(UnverifiedTree.Revoked "aborted", classify None (Some "a") (Some "aborted") run)
    Assert.Equal(UnverifiedTree.NoRunYet, classify None (Some "a") None [])

    match classify None (Some "a") None run with
    | UnverifiedTree.Revoked reason -> Assert.Contains("no test evidence", reason)
    | other -> Assert.Fail($"a completed run with no receipt and no revocation is still named, got %A{other}")

    Assert.Equal(("no-run-yet", None), UnverifiedTree.wire UnverifiedTree.NoRunYet)
    Assert.Equal(("tree-moved", None), UnverifiedTree.wire UnverifiedTree.TreeMoved)
    Assert.Equal(("tree-unreadable", None), UnverifiedTree.wire UnverifiedTree.TreeUnreadable)
    Assert.Equal(("evidence-revoked", Some "why"), UnverifiedTree.wire (UnverifiedTree.Revoked "why"))
    Assert.Equal(("covers-nothing", None), UnverifiedTree.wire UnverifiedTree.CoversNothing)
