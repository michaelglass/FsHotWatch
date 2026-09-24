/// One scenario, run twice: once as a per-worktree daemon and once as the single session
/// of a repository host. Hosting subtracts a named set of process-wide behaviours and
/// must not change anything a consumer observes. So the two runs must agree on the
/// diagnostics and receipts, every plugin's status and last run, the sequence of status
/// transitions, the build, the test runs and their history, the full-suite baseline, the
/// coverage, and every file written under `.fshw`, except the fields `modeOwned` names.
///
/// Both runs use the same worktree path, one after the other, each with its own empty
/// shared cache, so a difference can only come from the mode. What differs between ANY
/// two runs (run ids, clocks, durations) is removed before comparing, and says so.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.RepositoryHostParityTests

// TransparentCompiler.CacheSizes is marked experimental; it is the checker's configuration.
#nowarn "57"

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.Ipc
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionRegistry
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

/// What the modes may differ in, and nothing else:
///
/// - `host-session.json`: a hosted worktree records the host that serves it.
/// - `heartbeat`: a wall-clock beat, rewritten while work runs.
/// - `scan-metrics.jsonl`'s process figures and `scope`: a host reports its own totals.
///   Every other field of each record must agree.
let private modeOwned =
    {| Files = set [ "host-session.json"; "heartbeat" ]
       ScanMetricsFields =
        set
            [ "scope"
              "rssBytes"
              "managedBytes"
              "gen2Collections"
              "forcedGc"
              "sampledAt"
              "durationMs" ] |}

let private inertWatcher: Daemon.Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

let private libProject =
    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""

let private testProject =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup><Compile Include="Tests.fs" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../../src/Lib/Lib.fsproj" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.0" />
    <PackageReference Include="xunit.v3.mtp-v2" Version="4.0.0" />
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.9.0" />
  </ItemGroup>
</Project>"""

let private tests =
    """module LibTests

open Xunit

[<Fact>]
let ``add adds`` () = Assert.Equal(3, Lib.add 1 2)
"""

let private config =
    """{
  "build": { "command": "dotnet", "args": "build" },
  "format": false,
  "lint": true,
  "tests": {
    "projects": [
      {
        "project": "Lib.Tests",
        "command": "dotnet",
        "args": "run --project tests/Lib.Tests --no-build --",
        "filterTemplate": "--filter-class {classes}",
        "classJoin": " "
      }
    ]
  }
}"""

/// A worktree with a library, an xUnit test project covering it, and every plugin
/// that runs, tests, and records coverage.
let private writeWorktree (root: string) =
    let write (relative: string) (text: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, text)

    write "src/Lib/Lib.fsproj" libProject
    write "src/Lib/Lib.fs" "module Lib\nlet add a b = a + b\n"
    write "tests/Lib.Tests/Lib.Tests.fsproj" testProject
    write "tests/Lib.Tests/Tests.fs" tests
    write ".fshw.json" config

    write
        "Lib.slnx"
        """<Solution>
  <Project Path="src/Lib/Lib.fsproj" />
  <Project Path="tests/Lib.Tests/Lib.Tests.fsproj" />
</Solution>"""

    let fsproj = Path.Combine(root, "tests", "Lib.Tests", "Lib.Tests.fsproj")

    match
        ProcessHelper.runProcess
            "dotnet"
            $"restore \"%s{fsproj}\""
            root
            []
            (ProcessHelper.ProcessBounds.silent (TimeSpan.FromMinutes 3.0))
    with
    | ProcessHelper.Succeeded _ -> ()
    | other -> failwith $"restore failed: %A{other}"

/// Build the worktree's daemon as the CLI does: its configuration, its plugins.
let private build
    (hosting: DaemonHosting.Hosting)
    (checker: FSharp.Compiler.CodeAnalysis.FSharpChecker)
    (transitions: ConcurrentQueue<string * string>)
    (root: string)
    =
    let config = Cli.DaemonConfig.loadConfig root

    let daemon =
        Daemon.Daemon.createWithWatcherFactory
            checker
            root
            { Daemon.Daemon.DaemonOptions.defaults with
                ExcludePatterns = config.Exclude
                Hosting = hosting }
            inertWatcher

    daemon.Host.OnStatusChanged.Add(fun (name, status) ->
        let tag =
            match status with
            | Events.Idle -> "idle"
            | Events.Running _ -> "running"
            | Events.Completed _ -> "completed"
            | Events.Failed _ -> "failed"

        transitions.Enqueue(name, tag))

    Cli.DaemonConfig.registerPlugins daemon root config
    daemon

/// The scenario: settle the first scan (build, tests, coverage), make an edit the tests
/// cover, settle again. Returns, for each settle point, every plugin's status there and
/// whether it ran in the phase that led to it.
let private drive
    (root: string)
    (config: DaemonRpcConfig)
    (generation: unit -> int64)
    (transitions: ConcurrentQueue<string * string>)
    =
    let settle () =
        (config.WaitForAllTerminal(TimeSpan.FromMinutes 3.0)).Wait()

    let mutable seen = 0

    let snapshot () =
        let all = transitions.ToArray()
        let phase = all[seen..]
        seen <- all.Length

        let ran =
            phase
            |> Array.filter (fun (_, tag) -> tag = "running")
            |> Array.map fst
            |> Set.ofArray

        config.Host.GetAllStatuses()
        |> Map.toList
        |> List.map (fun (name, status) ->
            let tag =
                match status with
                | Events.Idle -> "idle"
                | Events.Running _ -> "running"
                | Events.Completed _ -> "completed"
                | Events.Failed _ -> "failed"

            name, tag, ran.Contains name)

    config.WaitForScanGeneration(0L).Wait()
    settle ()
    let first = snapshot ()
    File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet add a b = b + a\n")
    let before = generation ()
    config.RequestScan()
    config.WaitForScanGeneration(before).Wait()
    settle ()
    [ first; snapshot () ]

/// Everything a consumer observes, with this worktree's root spelled `<root>`.
let private observe (root: string) (config: DaemonRpcConfig) phases =
    // Error files are named after the path they report, with `/` spelled `-`.
    let normalize (text: string) =
        text.Replace(root, "<root>").Replace(root.Replace('/', '-'), "<root>")

    let diagnostics = JsonNode.Parse(DaemonRpcTarget(config).GetDiagnostics "")

    let text (node: JsonNode) =
        match node with
        | null -> "null"
        | n -> normalize (n.ToJsonString())

    let statuses =
        diagnostics["statuses"].AsObject()
        |> Seq.map (fun kv ->
            let s = kv.Value

            let lastRun =
                match s["lastRun"] with
                | null -> "none"
                | run ->
                    let outcome = run["outcome"]
                    let summary = run["summary"]
                    text outcome + " " + text summary

            let status = s["status"]
            let tag = status["tag"]
            let error = status["error"]
            let counts = s["diagnostics"]
            kv.Key, text tag, text error, lastRun, text counts)
        |> List.ofSeq
        |> List.sort

    // Different between any two runs, whatever the mode: when, how long, which run, and
    // how many times a run was repeated. TestPrune re-runs a project after a transient
    // state, and whether it does is timing: a control of legacy against legacy differs in
    // it. So runs, history entries and cache entries are compared as distinct results.
    let runOwned =
        set
            [ "durationMs"
              "runStartedAt"
              "runId"
              "earnedAt"
              "lastCleanCheckAt"
              "reportId"
              "timestamp"
              "start"
              "stop"
              "duration"
              "generatedBy"
              "environment"
              "id"
              "extra"
              "tool" ]

    let rec strip (node: JsonNode) : string =
        match node with
        | :? JsonObject as o ->
            o
            |> Seq.filter (fun kv -> not (runOwned.Contains kv.Key))
            |> Seq.sortBy (fun kv -> kv.Key)
            |> Seq.map (fun kv -> $"%s{kv.Key}=%s{strip kv.Value}")
            |> String.concat ","
            |> sprintf "{%s}"
        | :? JsonArray as a ->
            a
            |> Seq.map strip
            |> Seq.distinct
            |> Seq.sort
            |> String.concat ","
            |> sprintf "[%s]"
        | null -> "null"
        | v -> text v

    let jsonOf (path: string) =
        strip (JsonNode.Parse(File.ReadAllText path))

    let state = FsHwPaths.root root

    let relative (f: string) =
        normalize (Path.GetRelativePath(state, f).Replace('\\', '/'))

    let all =
        Directory.GetFiles(state, "*", SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (modeOwned.Files.Contains(Path.GetFileName f)))

    // Every file under .fshw, by kind: its meaning where a run leaves ids and clocks in
    // it, its bytes otherwise, its name only where the bytes are an engine's own
    // (SQLite's journal) or a raw tool transcript.
    let files =
        all
        |> Array.map (fun f ->
            let rel = relative f

            let content =
                if rel.StartsWith "test-runs/" then
                    // The run directory is a fresh id per run; which runs happened is the
                    // CTRF report's content.
                    "(run file)"
                elif rel.StartsWith "cache/tasks/" then
                    "(cache entry)"
                elif rel.EndsWith ".json" && rel <> "scan-metrics.jsonl" then
                    jsonOf f
                elif rel.StartsWith "test-impact.db" then
                    "(sqlite)"
                elif rel = "scan-metrics.jsonl" then
                    "(compared by field)"
                else
                    normalize (File.ReadAllText f)

            let name =
                if rel.StartsWith "test-runs/" then
                    "test-runs/<run>/" + Path.GetFileName rel
                else
                    rel

            name, content)
        |> Array.distinct
        |> Array.sort
        |> List.ofArray

    let testRuns =
        all
        |> Array.filter (fun f -> f.EndsWith ".ctrf.json")
        |> Array.map (fun f ->
            let report = JsonNode.Parse(File.ReadAllText f)
            let results = report["results"]
            strip results["summary"] + " " + strip results["tests"])
        |> Array.distinct
        |> Array.sort
        |> List.ofArray

    let coverage =
        let dir = Path.Combine(root, "coverage")

        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories)
            |> Array.map (fun f ->
                let xml = Xml.Linq.XDocument.Load f

                let lines =
                    xml.Descendants(Xml.Linq.XName.Get "class")
                    |> Seq.collect (fun c ->
                        let file = normalize (c.Attribute(Xml.Linq.XName.Get "filename").Value)

                        c.Descendants(Xml.Linq.XName.Get "line")
                        |> Seq.map (fun l ->
                            let number = l.Attribute(Xml.Linq.XName.Get "number").Value
                            let hits = l.Attribute(Xml.Linq.XName.Get "hits").Value
                            $"%s{file}:%s{number}=%s{hits}"))
                    |> Seq.sort
                    |> String.concat ";"

                relative f, lines)
            |> Array.sort
            |> List.ofArray
        else
            []

    let scanMetrics =
        File.ReadAllLines(Path.Combine(state, "scan-metrics.jsonl"))
        |> Array.map (fun line ->
            let node = JsonNode.Parse(line).AsObject()

            node
            |> Seq.filter (fun kv -> not (modeOwned.ScanMetricsFields.Contains kv.Key))
            |> Seq.map (fun kv -> kv.Key, text kv.Value)
            |> List.ofSeq)
        |> List.ofArray

    let receipts =
        match diagnostics["modelReceipts"] with
        | null -> "null"
        | node -> strip node

    {| Count = text diagnostics["count"]
       Files = text diagnostics["files"]
       Unchecked = text diagnostics["unchecked"]
       ProjectModel = text diagnostics["projectModel"]
       Receipts = receipts
       Statuses = statuses
       // Not the full transition sequence: a control of legacy against legacy differs
       // there in 2 of 3 runs (test-prune's transient states interleave with the build
       // differently), so it cannot tell the modes apart. Where each plugin stands at
       // every settle point, and whether it ran to get there, is stable and is compared.
       Phases = phases
       StateFiles = files
       TestRuns = testRuns
       Coverage = coverage
       ScanMetrics = scanMetrics |}

let private serveInto (served: TaskCompletionSource<DaemonRpcConfig>) =
    fun (config: DaemonRpcConfig) (cts: CancellationTokenSource) ->
        async {
            served.TrySetResult config |> ignore
            let stopped = TaskCompletionSource()
            use _ = cts.Token.Register(fun () -> stopped.TrySetResult() |> ignore)
            do! stopped.Task |> Async.AwaitTask
        }

let private runLegacy (root: string) =
    let transitions = ConcurrentQueue()

    let daemon =
        build (DaemonHosting.standalone ()) (Daemon.Daemon.createChecker ()) transitions root

    use cts = new CancellationTokenSource()
    let served = TaskCompletionSource<DaemonRpcConfig>()

    let run =
        Async.StartAsTask(daemon.RunWith(serveInto served, TimeSpan.FromSeconds 5.0, DateTime.UtcNow, cts))

    let config = served.Task.Result
    let phases = drive root config daemon.GetScanGeneration transitions
    let observed = observe root config phases
    cts.Cancel()
    run.Wait(TimeSpan.FromSeconds 60.0) |> ignore
    // The per-worktree daemon's process would exit here, and its pooled database
    // connections with it. This one shares the test process, so release them as the
    // exit would (a host does the same when a session ends: `SessionResources`).
    clearSqlitePool (Cli.DaemonConfig.testImpactDbPath root)
    observed

/// Run `body` with `name` set to `value` in the process environment, restoring it after.
/// Process-global, hence the serialized collection this module runs in.
let private withEnvValue (name: string) (value: string) (body: unit -> 'T) : 'T =
    let prior = Environment.GetEnvironmentVariable name
    Environment.SetEnvironmentVariable(name, value)

    try
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let private resolvedOrFail (root: string) =
    match resolveWorktree root with
    | Ok w -> w
    | Error e -> failwith (IdentityError.describe e)

/// Start the session for `root` in `registry`.
let private startSession (registry: SessionRegistry) (root: string) =
    let worktree = resolvedOrFail root

    let id =
        { Repository = worktree.Repository
          Worktree = worktree.Worktree
          Incarnation = SessionIncarnation.mint () }

    let spec =
        { Worktree = worktree
          Config = ConfigDigest.ofText "parity"
          Environment = SessionEnvironment.ofProcess root
          Sink =
            { Write = ignore
              Level = Logging.LogLevel.Info }
          // What the host gives every session (`SessionResources`): its pooled test-impact
          // connections go when it ends. Each mode reuses this worktree's path, and a
          // pooled connection would otherwise carry the last mode's database into the next.
          Owned =
            [ { new IDisposable with
                  member _.Dispose() =
                      FsHotWatch.TestPrune.ImpactDbPool.clear (Cli.DaemonConfig.testImpactDbPath root) } ] }

    match registry.Start(id, spec) with
    | Ok s -> id, s
    | Error e -> failwith e

/// The session's checker is its own: no sibling shares it.
let private unshared: DaemonHosting.CheckerFactory =
    fun _ -> failwith "this session was given its checker"

let private runHosted (root: string) =
    let transitions = ConcurrentQueue()

    use registry =
        new SessionRegistry(fun spec ->
            build
                (DaemonHosting.hostedBy inertWatcher unshared)
                (Daemon.Daemon.createChecker ())
                transitions
                spec.Worktree.Root.Value)

    let id, session = startSession registry root
    let config = session.Serving.Result
    let phases = drive root config session.Daemon.GetScanGeneration transitions
    let observed = observe root config phases
    test <@ registry.Detach id @>
    observed

/// A hosted session whose checker, and whose projects' results, are shared with a
/// sibling under one virtual root: a second worktree of the
/// same repository, the same content at another path, that has checked everything once
/// and then sits idle while the observed session runs the scenario. Sharing is then
/// exercised, not merely configured: the sibling's projects are in the checker the
/// observed session checks through.
let private runHostedBesideASibling (root: string) =
    let sibling = Path.Combine(Path.GetDirectoryName root, "sibling")
    let siblingJj = Path.Combine(sibling, ".jj")
    Directory.CreateDirectory siblingJj |> ignore

    File.WriteAllText(
        Path.Combine(siblingJj, "repo"),
        Path.GetRelativePath(siblingJj, Path.Combine(root, ".jj", "repo"))
    )

    writeWorktree sibling

    let partitions =
        CheckerPartitions.Partitions Daemon.Daemon.createCheckerWithCacheSizes

    let shared =
        partitions.For(
            FSharp.Compiler.CodeAnalysis.TransparentCompiler.CacheSizes.Create
                Daemon.Daemon.DefaultCheckerCacheSizeFactor
        )

    // Both worktrees under one virtual root: the observed session's projects are the
    // sibling's, so its checks are served from the sibling's.
    let canonicalProjects = CanonicalProjects.Registry()
    let virtualRoot = Path.Combine(Path.GetDirectoryName root, "state", "virtual")
    let transitions = ConcurrentQueue()
    // The sibling's own statuses: never part of what the observed session is compared on.
    let siblingTransitions = ConcurrentQueue()

    use registry =
        new SessionRegistry(fun spec ->
            let worktreeRoot = spec.Worktree.Root.Value

            let queue =
                if worktreeRoot = root then
                    transitions
                else
                    siblingTransitions

            let frames = SessionFrames.choice canonicalProjects worktreeRoot virtualRoot ignore

            build (DaemonHosting.hostedUnderFrames inertWatcher partitions.For frames) shared queue worktreeRoot)

    // The sibling shares the observed session's task cache as well as its checker, so
    // lint's content-keyed results reach the observed session from the sibling's run.
    let siblingId, siblingSession = startSession registry sibling
    let siblingConfig = siblingSession.Serving.Result

    test <@ waitUntilTrue (fun () -> siblingSession.Daemon.GetScanGeneration() > 0L) 300000 @>
    (siblingConfig.WaitForAllTerminal(TimeSpan.FromMinutes 3.0)).Wait()

    // Sharing is exercised, not merely configured: before the observed session starts,
    // the store it will read already holds the sibling's lint results. The parity
    // assertions then show a run served from them reports what a cold run reports.
    let sharedStore =
        Path.Combine(FsHwPaths.sharedCacheHome (), "cache", "tasks", RepoIdentity.namespaceOf root)

    test <@ FsHotWatch.FileTaskCache.FileTaskCache(sharedStore).Stats.EntryCount > 0 @>

    let id, session = startSession registry root
    test <@ obj.ReferenceEquals(session.Daemon.Checker, siblingSession.Daemon.Checker) @>
    // The observed session's project is the canonical content the sibling claimed.
    test <@ (canonicalProjects.CanonicalHash "src/Lib/Lib.fsproj").IsSome @>
    let config = session.Serving.Result
    let phases = drive root config session.Daemon.GetScanGeneration transitions
    let observed = observe root config phases
    test <@ registry.Detach id @>
    test <@ registry.Detach siblingId @>
    observed

[<Fact(Timeout = 900000)>]
let ``a legacy daemon, a hosted session, and one sharing its checker observe the same run`` () =
    withTempDir "parity" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        let root = Path.Combine(canonical, "w")

        /// A fresh worktree at the same path, and a fresh shared cache, for each mode.
        let inFreshWorktree (mode: string) (run: string -> 'T) : 'T =
            Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
            writeWorktree root
            // Each mode builds and runs on a context of its own: a per-worktree daemon
            // installs its process registry into the context that constructs it, which
            // in the CLI is a process of its own.
            let observed = isolated (fun () -> run root)
            Directory.Move(root, Path.Combine(canonical, $"done-%s{mode}"))
            observed

        let legacy =
            withEnvValue "FSHW_CACHE_HOME" (Path.Combine(canonical, "cache-legacy")) (fun () ->
                inFreshWorktree "legacy" runLegacy)

        let hosted =
            withEnvValue "FSHW_CACHE_HOME" (Path.Combine(canonical, "cache-hosted")) (fun () ->
                inFreshWorktree "hosted" runHosted)

        let besideASibling =
            withEnvValue "FSHW_CACHE_HOME" (Path.Combine(canonical, "cache-shared")) (fun () ->
                inFreshWorktree "shared" runHostedBesideASibling)

        // A vacuous agreement is not parity: the scenario must have built, tested and
        // measured coverage.
        let names = legacy.Statuses |> List.map (fun (name, _, _, _, _) -> name)
        test <@ names |> List.contains "build" && names |> List.contains "test-prune" @>
        test <@ not (List.isEmpty legacy.TestRuns) @>

        test
            <@
                legacy.Phases
                |> List.forall (List.exists (fun (name, _, ran) -> name = "test-prune" && ran))
            @>

        test <@ legacy.TestRuns |> List.forall (fun r -> r.Contains "passed=1") @>
        test <@ not (List.isEmpty legacy.Coverage) @>
        test <@ not (List.isEmpty legacy.ScanMetrics) @>

        test <@ hosted.Count = legacy.Count @>
        test <@ hosted.Files = legacy.Files @>
        test <@ hosted.Unchecked = legacy.Unchecked @>
        test <@ hosted.ProjectModel = legacy.ProjectModel @>
        test <@ hosted.Receipts = legacy.Receipts @>
        test <@ hosted.Statuses = legacy.Statuses @>
        test <@ hosted.Phases = legacy.Phases @>
        test <@ hosted.TestRuns = legacy.TestRuns @>
        test <@ hosted.Coverage = legacy.Coverage @>
        test <@ hosted.StateFiles = legacy.StateFiles @>
        test <@ hosted.ScanMetrics = legacy.ScanMetrics @>

        // The same run through a checker a sibling session shares.
        test <@ besideASibling.Count = legacy.Count @>
        test <@ besideASibling.Files = legacy.Files @>
        test <@ besideASibling.Unchecked = legacy.Unchecked @>
        test <@ besideASibling.ProjectModel = legacy.ProjectModel @>
        test <@ besideASibling.Receipts = legacy.Receipts @>
        test <@ besideASibling.Statuses = legacy.Statuses @>
        // The one observable difference is the sharing itself: the sibling already linted
        // this content, so the observed session's first scan replays lint instead of
        // running it. Every later phase, and every final status, is the legacy run's.
        let lintRanFirst (phases: (string * string * bool) list list) =
            phases |> List.head |> List.exists (fun (name, _, ran) -> name = "lint" && ran)

        let lintReplayedFirst (phases: (string * string * bool) list list) =
            phases
            |> List.mapi (fun index phase ->
                if index = 0 then
                    phase |> List.map (fun (name, tag, ran) -> name, tag, ran && name <> "lint")
                else
                    phase)

        test <@ lintRanFirst legacy.Phases @>
        test <@ not (lintRanFirst besideASibling.Phases) @>
        test <@ besideASibling.Phases = lintReplayedFirst legacy.Phases @>
        test <@ besideASibling.TestRuns = legacy.TestRuns @>
        test <@ besideASibling.Coverage = legacy.Coverage @>
        test <@ besideASibling.StateFiles = legacy.StateFiles @>
        test <@ besideASibling.ScanMetrics = legacy.ScanMetrics @>)
