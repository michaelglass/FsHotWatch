/// A test run launched while the host is still discovering its project model, and the
/// coverage each run keeps for itself.
///
/// A launch reads the model's generation. While a discovery is in flight that reading is
/// `None`, and a run launched under `None` that completes under a real generation cannot
/// show which projects the new model changed, so the whole run is revoked. On a cold
/// daemon that is the first full-suite run `confirm` asks for.
module FsHotWatch.Tests.TestPruneDiscoveryWaitTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

/// A project whose test command signals it started, then waits to be released, so the
/// harness can move the model while the run is in flight.
let private gatedProject (project: string) (started: string) (release: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch '%s{started}'; while [ ! -f '%s{release}' ]; do sleep 0.05; done; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled }

/// A repo root with one source file, so the run binds a readable input tree.
let private withRepo body =
    withTempDir "discovery-wait" (fun repoRoot ->
        let src = Path.Combine(repoRoot, "src")
        Directory.CreateDirectory src |> ignore
        File.WriteAllText(Path.Combine(src, "Value.fs"), "module Value\nlet answer = 1\n")
        body repoRoot)

/// Run `trigger` against a host that is discovering its model (`Rediscovering`), publish
/// the model half a second into the run, then release the tests. Returns the log.
let private runWhileDiscovering (repoRoot: string) (trigger: PluginHost -> unit) =
    let handshake = Path.Combine(repoRoot, "handshake")
    let started = handshake + ".started"
    let release = handshake + ".release"
    let lines = System.Collections.Concurrent.ConcurrentQueue<string>()

    use _sink =
        FsHotWatch.Logging.installSink
            { Write = lines.Enqueue
              Level = FsHotWatch.Logging.LogLevel.Debug }

    let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot
    host.WorkStore.PublishProjectModel(FsHotWatch.ProjectModel.Observation.Rediscovering fixtureModelGeneration)

    host.RegisterHandler(
        create ":memory:" repoRoot (Some [ gatedProject "ProjA" started release ]) None None None None []
    )

    let run = Threading.Tasks.Task.Run(fun () -> trigger host)

    // Long enough for a launch that does not wait to have started its tests.
    Thread.Sleep 500
    host.WorkStore.PublishProjectModel fixtureModel
    File.WriteAllText(release, "")

    run.Wait(TimeSpan.FromSeconds 20.0) |> ignore
    waitUntil (fun () -> lines |> Seq.exists (fun l -> l.Contains " completed: launched under ")) 10000
    List.ofSeq lines

let private completionLine (logged: string list) =
    logged |> List.tryFind (fun l -> l.Contains " completed: launched under ")

[<Fact(Timeout = 40000)>]
let ``a forced run launched while the model is being discovered waits for it and is not revoked`` () =
    withRepo (fun repoRoot ->
        let logged =
            runWhileDiscovering repoRoot (fun host ->
                host.RunCommand("run-tests", [| "{}" |])
                |> Async.Ignore
                |> fun run -> Async.RunSynchronously(run, 20000))

        let completion = completionLine logged

        test
            <@
                completion
                |> Option.exists (fun l ->
                    l.Contains $"launched under project model generation %d{fixtureModelGeneration}, completed under")
            @>

        test
            <@
                completion
                |> Option.exists (fun l -> not (l.Contains "the project model was replaced"))
            @>

        test <@ logged |> List.exists (fun l -> l.Contains "waited") @>)

[<Fact(Timeout = 40000)>]
let ``a full-suite run launched by a build while the model is being discovered waits for it`` () =
    withRepo (fun repoRoot ->
        let logged =
            runWhileDiscovering repoRoot (fun host ->
                host.RunCommand("set-scope", [| """{"scope":"full"}""" |])
                |> Async.Ignore
                |> fun set -> Async.RunSynchronously(set, 10000)

                host.EmitBuildCompleted(BuildSucceeded))

        let completion = completionLine logged

        test
            <@
                completion
                |> Option.exists (fun l ->
                    l.Contains $"launched under project model generation %d{fixtureModelGeneration}, completed under")
            @>

        test
            <@
                completion
                |> Option.exists (fun l -> not (l.Contains "the project model was replaced"))
            @>)

/// The guard on the wait: `Unobserved` is also a host that never discovers anything
/// (analysis-only, or a fixture), so a launch there must not wait for a model that will
/// never come.
[<Fact(Timeout = 20000)>]
let ``a host that has observed no discovery launches at once`` () =
    withRepo (fun repoRoot ->
        let release = Path.Combine(repoRoot, "released")
        File.WriteAllText(release, "")

        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

        host.RegisterHandler(
            create
                ":memory:"
                repoRoot
                (Some [ gatedProject "ProjA" (Path.Combine(repoRoot, "started")) release ])
                None
                None
                None
                None
                []
        )

        let clock = Diagnostics.Stopwatch.StartNew()

        host.RunCommand("run-tests", [| "{}" |])
        |> Async.Ignore
        |> fun run -> Async.RunSynchronously(run, 15000)

        test <@ File.Exists(Path.Combine(repoRoot, "started")) @>
        test <@ clock.Elapsed < TimeSpan.FromSeconds 10.0 @>)

/// A project whose test command writes a numbered cobertura to the coverage output path
/// it is handed (`$0` of the `sh -c` script), so each run's report is distinguishable.
let private numberedCoverageProject (project: string) (counter: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args =
        $"-c \"n=$(cat '%s{counter}' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '%s{counter}'; echo \\\"<coverage run='$n'/>\\\" > \\\"$0\\\"; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled }

[<Fact(Timeout = 40000)>]
let ``each run keeps its own copy of the coverage it wrote`` () =
    withRepo (fun repoRoot ->
        let coverageDir = Path.Combine(repoRoot, "coverage", "ProjA")
        Directory.CreateDirectory coverageDir |> ignore

        let paths (_: string) =
            Some
                { Baseline = Path.Combine(coverageDir, BaselineName)
                  Partial = Path.Combine(coverageDir, PartialName)
                  Cobertura = Path.Combine(repoRoot, "coverage", CoberturaName)
                  IncludeInRatchet = false
                  ArgsTemplate = "{output}" }

        let host = createModelHost (Unchecked.defaultof<_>) repoRoot

        // A file database: coverage ingest opens its own connections, and each
        // `:memory:` connection is a separate, empty database.
        host.RegisterHandler(
            create
                (Path.Combine(repoRoot, "tp.db"))
                repoRoot
                (Some [ numberedCoverageProject "ProjA" (Path.Combine(repoRoot, "counter")) ])
                None
                None
                None
                (Some paths)
                []
        )

        for _ in 1..2 do
            host.RunCommand("run-tests", [| "{}" |])
            |> Async.Ignore
            |> fun run -> Async.RunSynchronously(run, 15000)

        // The baseline is the latest run's; the first run's report survives only in its
        // own run directory.
        test <@ (File.ReadAllText(Path.Combine(coverageDir, BaselineName))).Contains "run='2'" @>

        let kept =
            Directory.GetDirectories(FsHotWatch.Ctrf.reportsDir repoRoot)
            |> Array.map (fun dir -> Path.Combine(dir, "ProjA.coverage.cobertura.xml"))
            |> Array.filter File.Exists
            |> Array.map (fun path -> (File.ReadAllText path).Trim())
            |> Array.sort
            |> List.ofArray

        test <@ kept = [ "<coverage run='1'/>"; "<coverage run='2'/>" ] @>)
