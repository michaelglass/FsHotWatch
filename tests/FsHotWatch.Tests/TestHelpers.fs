module FsHotWatch.Tests.TestHelpers

open System
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

/// Shared FSharpChecker for tests that only need basic compilation.
/// Lazy so it's only created if actually used.
let sharedChecker =
    lazy FSharpChecker.Create(projectCacheSize = 200, keepAssemblyContents = true, keepAllBackgroundResolutions = true)

/// A non-null FSharpParseFileResults built without invoking the constructor.
/// Plugins under test that consume FileCheckResult never inspect parse fields.
let dummyParseResults () : FSharpParseFileResults =
    RuntimeHelpers.GetUninitializedObject(typeof<FSharpParseFileResults>) :?> FSharpParseFileResults

/// Build a FileCheckResult with safe-uninitialized FCS parts. Lets plugin tests
/// fire FileChecked events without spinning up real FCS.
let fakeFileCheckResult (file: string) : FileCheckResult =
    { File = AbsFilePath.create file
      Source = "module Fake"
      ParseResults = dummyParseResults ()
      CheckResults = ParseOnly
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

/// Build a `BatchChecked` payload covering `files`, with deterministic timestamps and
/// Generation = 1.
let fakeBatchChecked (files: string list) : BatchChecked =
    let now = DateTime.UtcNow

    { Trigger = BootScan
      Files = files |> List.map AbsFilePath.create
      Generation = 1L
      StartedAt = now
      CompletedAt = now }

/// Spawn a redirected `sleep N` child for tests that need a long-lived but
/// inert process (registry kill paths, daemon teardown checks).
let startSleep (seconds: int) : Process =
    let psi = ProcessStartInfo("sleep", string seconds)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    Process.Start(psi)

/// Run `body` with a freshly-spawned `sleep` child, force-killing it in `finally` so an
/// assertion failure inside `body` cannot leak the child (registry KillAll swallows errors,
/// so `use proc = startSleep ...` is not enough on its own).
let withTrackedSleep (seconds: int) (body: Process -> 'a) : 'a =
    let proc = startSleep seconds

    try
        body proc
    finally
        try
            if not proc.HasExited then
                proc.Kill(entireProcessTree = true)
        with _ ->
            ()

        proc.Dispose()

/// Poll until condition is true or timeout (50ms poll interval). Returns whether it
/// actually became true.
///
/// Prefer this over `waitUntil` when the wait itself is what the test asserts: `waitUntil`
/// cannot tell "the condition held" from "we gave up", so a test that only checks state
/// afterwards can pass vacuously by timing out.
let waitUntilTrue (condition: unit -> bool) (timeoutMs: int) : bool =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable ok = condition ()

    while not ok && DateTime.UtcNow < deadline do
        Thread.Sleep(50)
        ok <- condition ()

    ok

/// Poll until condition is true or timeout (default 50ms poll interval).
let waitUntil (condition: unit -> bool) (timeoutMs: int) =
    waitUntilTrue condition timeoutMs |> ignore

/// Run `write` every 2s until `hasEvent` returns true or timeout expires.
/// Repeated writes mean a write that races watcher setup cannot lose the event,
/// so the test never depends on one fixed delay being long enough. The timeout
/// bounds only the failure case — the loop returns the moment the event lands.
///
/// A caller's budget is not a statement about how fast the filesystem is. The
/// large ones absorb stalls seen only under full-suite load; the same tests
/// finish in about a second on their own.
let probeLoop (write: int -> unit) (hasEvent: unit -> bool) (timeoutMs: int) =
    let overall = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable probe = 0

    while not (hasEvent ()) && DateTime.UtcNow < overall do
        write probe
        probe <- probe + 1
        let batchEnd = min overall (DateTime.UtcNow.AddMilliseconds(2000.0))

        while not (hasEvent ()) && DateTime.UtcNow < batchEnd do
            Thread.Sleep(50)

/// Repeatedly write probe files to a directory every 2s until hasEvent returns true.
let probeUntilEvent (dir: string) (hasEvent: unit -> bool) (timeoutMs: int) =
    probeLoop (fun n -> File.WriteAllText(Path.Combine(dir, $"_fshw-probe-{n}.fs"), $"// probe {n}")) hasEvent timeoutMs

/// Poll until the plugin reaches a terminal status (Completed or Failed).
let waitForTerminalStatus (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus(pluginName) with
            | Some(FsHotWatch.Events.Completed _)
            | Some(FsHotWatch.Events.Failed _) -> true
            | _ -> false)
        timeoutMs

/// Subscribe to `OnStatusChanged` BEFORE querying current status, returning a
/// `Task<PluginStatus>` that completes the first time `pred` matches a status for
/// `pluginName`. Callers subscribe, then trigger the work, then await — this observes a
/// terminal state deterministically (no polling, no xUnit `Fact(Timeout)` race).
let beginAwaitStatusWith
    (host: FsHotWatch.PluginHost.PluginHost)
    (pluginName: string)
    (matchCurrent: bool)
    (pred: FsHotWatch.Events.PluginStatus -> bool)
    : System.Threading.Tasks.Task<FsHotWatch.Events.PluginStatus> =
    let tcs =
        System.Threading.Tasks.TaskCompletionSource<FsHotWatch.Events.PluginStatus>()

    let handler =
        Handler<string * FsHotWatch.Events.PluginStatus>(fun _ (n, s) ->
            if n = pluginName && pred s then
                tcs.TrySetResult(s) |> ignore)

    host.OnStatusChanged.AddHandler(handler)
    // Subscribe-then-check ordering is required — check-then-subscribe races. Callers that
    // need the *next* transition (e.g. after an emit that cycles Running→Completed) pass
    // matchCurrent=false.
    if matchCurrent then
        match host.GetStatus(pluginName) with
        | Some s when pred s -> tcs.TrySetResult(s) |> ignore
        | _ -> ()
    // Unsubscribe once completed so handlers don't accumulate across tests.
    tcs.Task.ContinueWith(
        System.Action<System.Threading.Tasks.Task<FsHotWatch.Events.PluginStatus>>(fun _ ->
            host.OnStatusChanged.RemoveHandler(handler))
    )
    |> ignore

    tcs.Task

let beginAwaitStatus host pluginName pred =
    beginAwaitStatusWith host pluginName true pred

/// Convenience: await a terminal status (Completed or Failed).
let private isTerminalStatus =
    function
    | FsHotWatch.Events.Completed _
    | FsHotWatch.Events.Failed _ -> true
    | _ -> false

let beginAwaitTerminal (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) =
    beginAwaitStatus host pluginName isTerminalStatus

/// Await the *next* terminal transition, ignoring current status. Use after
/// emitting an event that'll cycle the plugin through Running→Completed when
/// it's already at Completed from an earlier event.
let beginAwaitNextTerminal (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) =
    beginAwaitStatusWith host pluginName false isTerminalStatus

/// Poll until the plugin status is no longer Running, with a timeout.
let waitForSettled (host: FsHotWatch.PluginHost.PluginHost) (pluginName: string) (timeoutMs: int) =
    waitUntil
        (fun () ->
            match host.GetStatus(pluginName) with
            | Some(FsHotWatch.Events.Running _) -> false
            | _ -> true)
        timeoutMs

/// Poll until every registered plugin has drained its mailbox, with a timeout. This is the
/// correct synchronization after emitting events like `BatchChecked`/`FileChecked` that
/// persist as a side-effect WITHOUT a status transition: `beginAwaitNextTerminal` hangs the
/// full timeout on those, whereas quiescence returns the instant the handler finishes.
let waitForQuiescent (host: FsHotWatch.PluginHost.PluginHost) (timeoutMs: int) =
    waitUntil (fun () -> not (host.AnyPluginBusy())) timeoutMs

/// Create a plugin that records BuildCompleted events.
/// Returns (getBuildResult, handler) where getBuildResult() returns the captured result.
let buildRecorder () =
    let mutable receivedBuild: FsHotWatch.Events.BuildResult option = None

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> receivedBuild), handler)

/// Create a plugin that records CommandCompleted events.
/// Returns (getCommandResult, handler) where getCommandResult() returns the captured result.
let commandRecorder () =
    let mutable receivedCommand: FsHotWatch.Events.CommandCompletedResult option = None

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "command-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.CommandCompleted result -> receivedCommand <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeCommandCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> receivedCommand), handler)

/// Create a plugin that counts CommandCompleted events for a given plugin name.
/// Returns (getCount, handler) — getCount() is the current total.
let commandCounter (pluginName: string) =
    let count = ref 0

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create $"counter-for-{pluginName}"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.CommandCompleted result when result.Name = pluginName ->
                        System.Threading.Interlocked.Increment(count) |> ignore
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeCommandCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> count.Value), handler)

/// Returns (getEvents, handler) — getEvents snapshots all received TestProgress events
/// (delta per group) in FIFO order.
let testProgressRecorder () =
    let received =
        System.Collections.Concurrent.ConcurrentQueue<FsHotWatch.Events.TestProgress>()

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "test-progress-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.TestProgress progress -> received.Enqueue(progress)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeTestProgress ]
          CacheKey = None
          Teardown = None }

    ((fun () -> received |> Seq.toList), handler)

/// Returns (getEvents, handler) — getEvents snapshots all received TestRunCompleted events
/// (the end-of-run summary) in FIFO order.
let testRunCompletedRecorder () =
    let received =
        System.Collections.Concurrent.ConcurrentQueue<FsHotWatch.Events.TestRunCompleted>()

    let handler: FsHotWatch.PluginFramework.PluginHandler<unit, obj> =
        { Name = FsHotWatch.PluginFramework.PluginName.create "test-run-completed-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FsHotWatch.Events.TestRunCompleted completed -> received.Enqueue(completed)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ FsHotWatch.PluginFramework.SubscribeTestRunCompleted ]
          CacheKey = None
          Teardown = None }

    ((fun () -> received |> Seq.toList), handler)

/// A fully-populated `DaemonConfiguration` with deterministic, env-independent values, and
/// the single source of truth for the record's shape: tests build on it via record-update
/// (`{ defaultTestConfig () with Tests = ... }`), so a new config field is a one-line edit
/// here rather than ~20 across the suite.
///
/// Values mirror `DaemonConfig.defaultConfigFor` except where determinism matters:
/// `Cache = NoCache` is explicit so config-shape tests stay stable regardless of
/// environment.
let defaultTestConfig () : FsHotWatch.Cli.DaemonConfig.DaemonConfiguration =
    { Build =
        Some
            [ {| Command = "dotnet"
                 Args = "build"
                 BuildTemplate = None
                 DependsOn = []
                 TimeoutSec = None |} ]
      Format = FsHotWatch.Cli.DaemonConfig.Auto
      Lint = true
      Cache = FsHotWatch.Cli.DaemonConfig.NoCache
      Analyzers = None
      Tests = None
      FileCommands = []
      Coverage = None
      Exclude = []
      IncludeOutsideRepo = false
      LogDir = "logs"
      TimeoutSec = None
      IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Absent
      PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Absent
      FsEventsLatencyMs = 250
      BeforeRun = None
      AfterRun = None
      RunHookTimeoutSec = None
      RunHookCommands = FsHotWatch.Cli.DaemonConfig.DefaultRunHookCommands }

let errorEntry msg (sev: FsHotWatch.ErrorLedger.DiagnosticSeverity) : FsHotWatch.ErrorLedger.ErrorEntry =
    { Message = msg
      Severity = sev
      Line = 0
      Column = 0
      Detail = None }

/// Construct an FSharpProjectOptions with sensible defaults for tests that only care about
/// ProjectFileName / SourceFiles / OtherOptions.
let makeProjectOptions (projectFile: string) (sourceFiles: string list) (otherOptions: string list) =
    { ProjectFileName = projectFile
      ProjectId = None
      SourceFiles = Array.ofList sourceFiles
      OtherOptions = Array.ofList otherOptions
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// Set an environment variable for the duration of `body`, restoring the
/// prior value (or unset state) afterward. `Some ""` sets to empty;
/// `None` unsets.
let withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable(name)

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

/// Recursively delete a throwaway temp dir, tolerating the daemon-teardown race.
///
/// Daemon shutdown is cooperative: after a bounded `task.Wait`, in-flight checks can still
/// have `FileErrorReporter` recreate `.fshw/errors/` and write JSON. That background writer
/// races `withTempDir`'s `Directory.Delete(recursive=true)`, which then throws IOException
/// "Directory not empty" from a `finally` — surfacing as an unattributed test-host failure
/// with no per-test name. Cleanup is therefore best-effort: retry, then swallow.
///
/// This does NOT mask real failures — a failing assertion throws from `body` before
/// `finally`, so it propagates unchanged.
let private deleteTempDirResilient (tmpDir: string) =
    let mutable remaining = 10
    let mutable deleted = false

    while not deleted && remaining > 0 do
        remaining <- remaining - 1

        try
            if Directory.Exists(tmpDir) then
                Directory.Delete(tmpDir, true)

            deleted <- true
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // A not-yet-drained daemon is still writing into the tree. On the final
            // attempt, give up: leaking a scratch temp dir beats failing the test.
            if remaining = 0 then
                deleted <- true
            else
                System.Threading.Thread.Sleep(25)

let withTempDir (prefix: string) (body: string -> 'a) =
    // Canonicalize so /var/folders/... and /private/var/folders/... don't diverge
    // across test+plugin views of the same path (macOS temp dir is a symlink).
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-{prefix}-{Guid.NewGuid():N}")
        |> Path.GetFullPath

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        body tmpDir
    finally
        deleteTempDirResilient tmpDir

/// Write a minimal `.fsproj` with a `<TargetFramework>` and the given `<Compile Include="…">`
/// items, for tests that need `RegisterFromFsproj` to record both TFM and source files
/// (e.g. anything exercising canonical-DLL path lookup).
let writeMinimalFsproj (projPath: string) (tfm: string) (compiles: string list) =
    let compileItems =
        compiles
        |> List.map (fun c -> $"    <Compile Include=\"{c}\" />")
        |> String.concat "\n"

    let xml =
        "<Project>\n"
        + $"  <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>\n"
        + "  <ItemGroup>\n"
        + compileItems
        + "\n  </ItemGroup>\n"
        + "</Project>"

    File.WriteAllText(projPath, xml)

// ----------------------------------------------------------------------------
// Seeded test-prune environment scaffolding: `withSeededTestEnv` factors the ~25-line
// prelude several TestPrunePlugin regression guards share, so they don't accumulate
// near-duplicate boilerplate.
// ----------------------------------------------------------------------------

/// State handed to a `withSeededTestEnv` body. `FilePath` is absolute (temp dir + RelPath);
/// `RelPath` is the `Lib.fs` / `Lib.fsx` form the plugin sees.
type SeededTestEnv =
    { TmpDir: string
      Db: TestPrune.Database.Database
      Pipeline: FsHotWatch.CheckPipeline.CheckPipeline
      Host: FsHotWatch.PluginHost.PluginHost
      Checker: FSharpChecker
      ProjOptions: FSharpProjectOptions
      RelPath: string
      FilePath: string
      SeededSymbols: TestPrune.AstAnalyzer.SymbolInfo list }

/// Seed a one-file F# project into a temp dir, persist its symbols to a fresh SQLite DB,
/// mark the freshness sidecar clean, and hand the body a fully wired pipeline + plugin
/// host. Cleanup happens in `withTempDir`'s `finally`.
let withSeededTestEnv (prefix: string) (relPath: string) (source: string) (body: SeededTestEnv -> unit) : unit =
    withTempDir prefix (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let filePath = Path.Combine(tmpDir, relPath)

        // Fresh checker per call (NOT sharedChecker): these envs drive script-closure
        // resolution + analyzeSource + CheckPipeline while ~5 other test classes hit the
        // shared instance concurrently, so its caches (keyed by options/paths) could bleed
        // state across tests. Costs ~3s per env for un-amortized framework resolution.
        let checker =
            FSharpChecker.Create(
                projectCacheSize = 200,
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true
            )

        File.WriteAllText(filePath, source)
        let sourceText = FSharp.Compiler.Text.SourceText.ofString source

        let projOptions =
            checker.GetProjectOptionsFromScript(filePath, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously
            |> fst

        let seedResult =
            TestPrune.AstAnalyzer.analyzeSource checker filePath source projOptions "TestProject"
            |> Async.RunSynchronously

        let seededSymbols =
            match seedResult with
            | Error msg ->
                Xunit.Assert.Fail($"Initial analysis failed: {msg}")
                []
            | Ok result ->
                let normalized =
                    { result with
                        Symbols = TestPrune.AstAnalyzer.normalizeSymbolPaths tmpDir result.Symbols }

                let db = TestPrune.Database.Database.create dbPath
                db.RebuildProjects([ normalized ])
                normalized.Symbols

        // Sidecar must be clean for detectChanges to actually run (Path D gate).
        FsHotWatch.TestPrune.FileFreshness.save
            tmpDir
            (FsHotWatch.TestPrune.FileFreshness.markClean DateTime.UtcNow relPath Map.empty)

        let pipeline = FsHotWatch.CheckPipeline.CheckPipeline(checker)
        pipeline.RegisterProject(projOptions.ProjectFileName, projOptions)

        let host = FsHotWatch.PluginHost.PluginHost.create checker tmpDir

        let handler =
            FsHotWatch.TestPrune.TestPrunePlugin.create dbPath tmpDir None None None None None []

        host.RegisterHandler(handler)

        let env =
            { TmpDir = tmpDir
              Db = TestPrune.Database.Database.create dbPath
              Pipeline = pipeline
              Host = host
              Checker = checker
              ProjOptions = projOptions
              RelPath = relPath
              FilePath = filePath
              SeededSymbols = seededSymbols }

        body env)

// ----------------------------------------------------------------------------
// Log-global capture + serialization.
//
// `Logging.logLevel`/`verbose` are module-level mutables and `Console.Error` is
// process-wide. Under xUnit parallel execution these race across test classes: A's
// `setLogLevel`/`Console.SetError` lands between B's set and assert, so B reads the wrong
// level or captures the wrong writer. Every module touching them therefore carries
// `[<Collection(LogGlobalCollectionName)>]`, which serializes ONLY those modules; the ~40
// others keep running in parallel. Each site still does its own save / restore-in-`finally`
// dance, now safe because no other mutator runs concurrently.
// ----------------------------------------------------------------------------

/// Name of the serialized collection grouping every test class touching PROCESS-GLOBAL
/// state: the logging globals, `Console.Error`, and the process ENVIRONMENT (`withEnv`).
///
/// The environment is the easiest of the three to miss: a class that mutates it and spawns
/// a child which snapshots it races every other class doing the same, and the symptom is
/// silent — an empty string where a value was expected, no exception, no timeout.
[<Literal>]
let LogGlobalCollectionName = "LogGlobal"

/// Serializes every test class touching the logging globals or `Console.Error`.
[<Xunit.CollectionDefinition(LogGlobalCollectionName, DisableParallelization = true)>]
type LogGlobalCollection() = class end

// ----------------------------------------------------------------------------
// Real-filesystem-watch serialization.
//
// Tests waiting on a live `FileSystemWatcher` OS event are timing-dependent: under heavy
// parallel CPU load the OS can take seconds to deliver, blowing the test's `signal.Wait`
// budget. Serializing them (as `MacFsEvents` already does) stops them competing with the
// rest of the suite for file-watch delivery while the machine is saturated.
// ----------------------------------------------------------------------------

/// Name of the serialized collection grouping tests that wait on a live
/// `FileSystemWatcher` OS event.
[<Literal>]
let FileWatchCollectionName = "FileWatch"

[<Xunit.CollectionDefinition(FileWatchCollectionName, DisableParallelization = true)>]
type FileWatchCollection() = class end

/// A minimal RunVerdict for tests that only exercise the status TRANSITION — the content is
/// irrelevant to them, but the type will not let a terminal status exist without one.
let testVerdict: FsHotWatch.Events.RunVerdict =
    FsHotWatch.Events.RunVerdict.create "test verdict" System.TimeSpan.Zero

/// Completed status at `at`, carrying the canonical test verdict.
let completedAt (at: System.DateTime) : FsHotWatch.Events.PluginStatus =
    FsHotWatch.Events.Completed(at, testVerdict)

/// Failed status at `at`, carrying the canonical test verdict.
let failedAt (error: string) (at: System.DateTime) : FsHotWatch.Events.PluginStatus =
    FsHotWatch.Events.Failed(error, at, testVerdict)

/// Cached per-file terminal for cache-entry fixtures. Per-file entries carry no summary or
/// timestamp by construction (AUTOMATION-186), only the measured duration — the replay
/// derives the summary from the live ledger.
let cachedFileDone: FsHotWatch.TaskCache.CachedStatus =
    FsHotWatch.TaskCache.CachedFileCompleted System.TimeSpan.Zero

/// Cached whole-run terminal carrying the canonical test verdict (run entries are keyed on
/// their full input, so the verdict replays verbatim).
let cachedRunDone: FsHotWatch.TaskCache.CachedStatus =
    FsHotWatch.TaskCache.CachedRunCompleted testVerdict

/// `IpcParsing.parsePluginStatuses` for tests asserting on the CONTENTS of a payload they
/// already know parses. Fails the test on an unreadable payload rather than degrading to an
/// empty map, so a test can't quietly start asserting over nothing.
let parseStatuses (json: string) : Map<string, FsHotWatch.Cli.RunOnceOutput.ParsedPluginStatus> =
    match FsHotWatch.Cli.IpcParsing.parsePluginStatuses json with
    | Ok statuses -> statuses
    | Error reason -> failwithf "the plugin-status payload could not be read: %s\n%s" reason json
