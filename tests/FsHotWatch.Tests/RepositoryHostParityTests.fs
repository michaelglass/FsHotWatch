/// One scenario, run twice: once as a per-worktree daemon and once as the single session
/// of a repository host. Hosting subtracts a named set of process-wide behaviours and
/// must not change anything a consumer observes. So the two runs must agree on the
/// diagnostics, every plugin's status and last run, the sequence of status transitions,
/// and every file written under `.fshw`, except the fields `modeOwned` names.
module FsHotWatch.Tests.RepositoryHostParityTests

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

let private project =
    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""

/// A worktree with one project, with lint on.
let private writeWorktree (root: string) =
    let lib = Path.Combine(root, "src", "Lib")
    Directory.CreateDirectory lib |> ignore
    File.WriteAllText(Path.Combine(lib, "Lib.fsproj"), project)
    File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\nlet ids = List.map (fun x -> x) [ 1 ]\n")
    File.WriteAllText(Path.Combine(root, ".fshw.json"), """{ "build": false, "format": false, "lint": true }""")
    let fsproj = Path.Combine(lib, "Lib.fsproj")

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
let private build (hosting: DaemonHosting.Hosting) (transitions: ConcurrentQueue<string * string>) (root: string) =
    let config = Cli.DaemonConfig.loadConfig root

    let daemon =
        Daemon.Daemon.createWithWatcherFactory
            (Daemon.Daemon.createChecker ())
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

/// The scenario: settle the first scan, make an edit that breaks the build, settle again.
let private drive (root: string) (config: DaemonRpcConfig) (generation: unit -> int64) =
    let settle () =
        (config.WaitForAllTerminal(TimeSpan.FromMinutes 3.0)).Wait()

    config.WaitForScanGeneration(0L).Wait()
    settle ()
    File.WriteAllText(Path.Combine(root, "src", "Lib", "Lib.fs"), "module Lib\nlet ids: int = \"not an int\"\n")
    let before = generation ()
    config.RequestScan()
    config.WaitForScanGeneration(before).Wait()
    settle ()

/// Everything a consumer observes, with this worktree's root spelled `<root>`.
let private observe (root: string) (config: DaemonRpcConfig) (transitions: ConcurrentQueue<string * string>) =
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

    // Consecutive repeats collapse: "running, running" is one transition.
    let collapsed =
        transitions
        |> Seq.fold
            (fun (acc: (string * string) list) t ->
                match acc with
                | last :: _ when last = t -> acc
                | _ -> t :: acc)
            []
        |> List.rev
        |> List.groupBy fst
        |> List.map (fun (name, ts) -> name, ts |> List.map snd)
        |> List.sort

    let state = FsHwPaths.root root

    let files =
        Directory.GetFiles(state, "*", SearchOption.AllDirectories)
        |> Array.map (fun f -> normalize (Path.GetRelativePath(state, f)), f)
        |> Array.filter (fun (rel, _) -> not (modeOwned.Files.Contains(Path.GetFileName rel)))
        |> Array.filter (fun (rel, _) -> rel <> "scan-metrics.jsonl")
        |> Array.sortBy fst
        |> Array.map (fun (rel, f) ->
            let content = normalize (File.ReadAllText f)
            rel, Convert.ToHexString(SHA256.HashData(Text.Encoding.UTF8.GetBytes content)))
        |> List.ofArray

    let scanMetrics =
        File.ReadAllLines(Path.Combine(state, "scan-metrics.jsonl"))
        |> Array.map (fun line ->
            let node = JsonNode.Parse(line).AsObject()

            node
            |> Seq.filter (fun kv -> not (modeOwned.ScanMetricsFields.Contains kv.Key))
            |> Seq.map (fun kv -> kv.Key, text kv.Value)
            |> List.ofSeq)
        |> List.ofArray

    {| Count = text diagnostics["count"]
       Files = text diagnostics["files"]
       Unchecked = text diagnostics["unchecked"]
       ProjectModel = text diagnostics["projectModel"]
       Statuses = statuses
       Transitions = collapsed
       StateFiles = files
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
    let daemon = build DaemonHosting.standalone transitions root
    use cts = new CancellationTokenSource()
    let served = TaskCompletionSource<DaemonRpcConfig>()

    let run =
        Async.StartAsTask(daemon.RunWith(serveInto served, TimeSpan.FromSeconds 5.0, DateTime.UtcNow, cts))

    let config = served.Task.Result
    drive root config daemon.GetScanGeneration
    let observed = observe root config transitions
    cts.Cancel()
    run.Wait(TimeSpan.FromSeconds 60.0) |> ignore
    observed

let private runHosted (root: string) =
    let transitions = ConcurrentQueue()

    let worktree =
        match resolveWorktree root with
        | Ok w -> w
        | Error e -> failwith (IdentityError.describe e)

    use registry =
        new SessionRegistry(fun spec ->
            build (DaemonHosting.hostedBy inertWatcher) transitions spec.Worktree.Root.Value)

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
          Owned = [] }

    let session =
        match registry.Start(id, spec) with
        | Ok s -> s
        | Error e -> failwith e

    let config = session.Serving.Result
    drive root config session.Daemon.GetScanGeneration
    let observed = observe root config transitions
    test <@ registry.Detach id @>
    observed

[<Fact(Timeout = 600000)>]
let ``a legacy daemon and a single hosted session observe the same run`` () =
    withTempDir "parity" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        let legacyRoot = Path.Combine(canonical, "legacy")
        let hostedRoot = Path.Combine(canonical, "hosted")

        for root in [ legacyRoot; hostedRoot ] do
            Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
            writeWorktree root

        let legacy = runLegacy legacyRoot
        let hosted = runHosted hostedRoot

        // A vacuous agreement is not parity: the scenario must have produced something.
        test <@ legacy.Count <> "0" @>
        test <@ not (List.isEmpty legacy.Statuses) @>
        test <@ not (List.isEmpty legacy.ScanMetrics) @>

        test <@ hosted.Count = legacy.Count @>
        test <@ hosted.Files = legacy.Files @>
        test <@ hosted.Unchecked = legacy.Unchecked @>
        test <@ hosted.ProjectModel = legacy.ProjectModel @>
        test <@ hosted.Statuses = legacy.Statuses @>
        test <@ hosted.Transitions = legacy.Transitions @>
        test <@ hosted.StateFiles = legacy.StateFiles @>
        test <@ hosted.ScanMetrics = legacy.ScanMetrics @>)
