/// A run every client abandoned executed nothing, so it discharges nothing. The tree it
/// was launched to verify is still unverified, and a rerun a build queued behind it
/// launches: it is not "stale" because the run before it "cleared all verification debt".
/// That run cleared nothing. A build that lands between the launch's selection and its
/// test hosts queues exactly such a rerun ("tests already running").
module FsHotWatch.Tests.TestPruneWithdrawnRunTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
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

let private linesOf (path: string) =
    if File.Exists path then
        File.ReadAllLines(path).Length
    else
        0

/// A P1 whose test host records that it started, waits for `gate`, then records a run.
/// A host killed while it waits records no run.
type private Session =
    { Host: PluginHost
      TestFile: string
      Gate: string
      Started: string
      Runs: string }

let private session (tmpDir: string) =
    let testFile = Path.Combine(tmpDir, "tests", "P1", "Tests.fs")
    Directory.CreateDirectory(Path.GetDirectoryName testFile) |> ignore
    File.WriteAllText(testFile, testSource "original comment")
    seedBaseline tmpDir [ "P1" ]

    let gate = Path.Combine(tmpDir, "gate")
    let started = Path.Combine(tmpDir, "p1-started")
    let runs = Path.Combine(tmpDir, "p1-runs")

    let config =
        { PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) with
            Args = $"-c \"echo s >> {started}; while [ ! -f {gate} ]; do sleep 0.02; done; echo run >> {runs}; exit 0\"" }

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some [ config ]) None None None None [])

    { Host = host
      TestFile = testFile
      Gate = gate
      Started = started
      Runs = runs }

/// Earn evidence on the original tree, then move the tree by a comment only: the symbol
/// diff owes nothing, but the evidence is for bytes that are gone.
let private greenThenCommentEdit (s: Session) =
    File.WriteAllText(s.Gate, "")
    let terminal = beginAwaitNextTerminal s.Host "test-prune"
    s.Host.EmitBuildCompleted BuildSucceeded
    test <@ terminal.Wait(TimeSpan.FromSeconds 15.0) @>
    waitForQuiescent s.Host 20000
    test <@ linesOf s.Runs = 1 @>

    match scopeOf s.Host with
    | FullSuite(1, _) -> ()
    | other -> failwith $"positive control: the first build runs P1 in full, got %A{other}"

    File.Delete s.Gate
    File.WriteAllText(s.TestFile, testSource "edited comment")

    match scopeOf s.Host with
    | NoTestsRun NoTestsReason.TreeMoved -> ()
    | other -> failwith $"an edited tree with no run on it must read tree-moved, got %A{other}"

/// A build only `client` wants: its run is launched, and its test host is waiting.
let private launchForClient (s: Session) (client: CancellationTokenSource) =
    PluginWorkOwner.ConsumerLeases.onBehalfOf (PluginWorkOwner.ConsumerLeases.ofClient client.Token) (fun () ->
        s.Host.EmitBuildCompleted BuildSucceeded)

    test <@ waitUntilTrue (fun () -> linesOf s.Started = 2) 15000 @>

[<Fact(Timeout = 60000)>]
let ``a rerun queued behind a run every client abandoned still runs, and earns the verdict`` () =
    withTempDir "tp-withdrawn-rerun" (fun tmpDir ->
        let s = session tmpDir
        use client = new CancellationTokenSource()

        try
            greenThenCommentEdit s
            launchForClient s client

            // The daemon's own build lands while that run holds the key.
            let before = committedBy s.Host "test-prune"
            s.Host.EmitBuildCompleted BuildSucceeded
            test <@ waitForCommitted s.Host "test-prune" before 1L 15000 @>

            client.Cancel()

            // The rerun launches its own host over the edited tree.
            test <@ waitUntilTrue (fun () -> linesOf s.Started = 3) 15000 @>
            File.WriteAllText(s.Gate, "")
            waitForQuiescent s.Host 20000

            // The abandoned host was killed before it ran; the rerun ran once.
            test <@ linesOf s.Runs = 2 @>

            match scopeOf s.Host with
            | FullSuite(1, _) -> ()
            | other -> failwith $"the rerun must verify the edited tree, got %A{other}"
        finally
            File.WriteAllText(s.Gate, "")
            s.Host.Teardown())

[<Fact(Timeout = 60000)>]
let ``a run every client abandoned is cancelled, and discharges nothing`` () =
    withTempDir "tp-withdrawn-nothing" (fun tmpDir ->
        let s = session tmpDir
        use client = new CancellationTokenSource()

        try
            greenThenCommentEdit s
            launchForClient s client

            client.Cancel()
            waitForQuiescent s.Host 20000

            test <@ linesOf s.Runs = 1 @>
            test <@ linesOf s.Started = 2 @>

            match scopeOf s.Host with
            | NoTestsRun NoTestsReason.TreeMoved -> ()
            | other -> failwith $"a cancelled run earns no evidence, got %A{other}"
        finally
            File.WriteAllText(s.Gate, "")
            s.Host.Teardown())
