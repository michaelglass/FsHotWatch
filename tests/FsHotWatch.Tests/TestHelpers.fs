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

/// Build a `BatchChecked` payload covering `files`, with deterministic
/// timestamps + Generation = 1 — sufficient for unit tests that only care
/// about the `Files` set the cohort signal carries.
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

/// Run `body` with a freshly-spawned `sleep` child, force-killing the process
/// in `finally` so an assertion failure inside `body` cannot leak the child
/// (registry KillAll swallows errors, so `use proc = startSleep ...` alone is
/// not enough on its own).
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

/// Poll until condition is true or timeout (default 50ms poll interval).
let waitUntil (condition: unit -> bool) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep(50)

/// Run `write` every 2s until `hasEvent` returns true or timeout expires.
/// Use this for FSEvents tests: brand-new temp directories can have 4-20s cold-start
/// latency, and after a large initial event batch, fseventsd may batch subsequent events
/// for 15-30s regardless of kFSEventStreamCreateFlagNoDefer. Repeated writes ensure the
/// event fires as soon as fseventsd is ready, without relying on fixed timeouts.
let probeLoop (write: int -> unit) (hasEvent: unit -> bool) (timeoutMs: int) =
    let overall = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable probe = 0

    while not (hasEvent ()) && DateTime.UtcNow < overall do
        write probe
        probe <- probe + 1
        // Poll for up to 2s per write before retrying
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
/// `Task<PluginStatus>` that completes the first time `pred` matches a status
/// for `pluginName`. Use this when a test needs to observe a plugin's terminal
/// state deterministically (no polling, no xUnit `Fact(Timeout)` race).
///
/// Usage pattern — subscribe, then trigger the work, then await:
///   let completion = TestHelpers.beginAwaitStatus host "plugin" (function Completed _ -> true | _ -> false)
///   host.EmitBuildCompleted(BuildSucceeded)
///   let status = completion.Wait(TimeSpan.FromSeconds 15.0)
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
    // Fast path: the plugin may already be at the desired status when we subscribe.
    // Subscribe-then-check ordering is required — check-then-subscribe races.
    // Callers that need to observe the *next* transition (e.g. after an emit
    // that'll cycle Running→Completed) pass matchCurrent=false.
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

/// Poll until every registered plugin has drained its mailbox (no plugin
/// busy), with a timeout. This is the correct synchronization after emitting
/// events like `BatchChecked`/`FileChecked` that persist as a side-effect
/// WITHOUT a status transition: `beginAwaitNextTerminal` hangs the full
/// timeout on those (it waits for a Completed/Failed transition that never
/// fires), whereas quiescence returns the instant the handler finishes.
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

/// Create a plugin that records TestCompleted events in order.
/// Returns (getEvents, handler) — getEvents returns a snapshot of all received
/// TestProgress events (delta per group) in FIFO order.
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

/// Returns (getEvents, handler) — getEvents returns a snapshot of all received
/// TestRunCompleted events in FIFO order. (Renamed from testCompletedRecorder
/// to reflect the new event lifecycle: subscribers observe TestRunCompleted
/// for the end-of-run summary.)
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

/// A fully-populated `DaemonConfiguration` with deterministic, env-independent
/// values for tests. The single source of truth for the record's shape: every
/// test that needs a config builds on this via record-update
/// (`{ defaultTestConfig () with Tests = ... }`) and overrides only the fields
/// it cares about. Adding a new config field is then a one-line edit here rather
/// than ~20 mechanical edits across the test suite.
///
/// Values mirror the product defaults (`DaemonConfig.defaultConfigFor`) except
/// where determinism matters: `Cache = NoCache` is set explicitly (matching the
/// product default, which AUTOMATION-98 made honest — it was the never-hitting
/// file backend, which behaved as NoCache anyway), so config-shape tests stay
/// stable regardless of environment.
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
      RunHookTimeoutSec = None }

/// Create an ErrorEntry for tests.
let errorEntry msg (sev: FsHotWatch.ErrorLedger.DiagnosticSeverity) : FsHotWatch.ErrorLedger.ErrorEntry =
    { Message = msg
      Severity = sev
      Line = 0
      Column = 0
      Detail = None }

/// Create a temp directory with the given prefix, run the body, then clean up.
/// Returns the result of the body function.
/// Construct an FSharpProjectOptions with sensible defaults for tests that
/// only care about ProjectFileName / SourceFiles / OtherOptions.
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
/// Root cause (2026-06-17, CI flake on `check --run-once`, Linux): a daemon test
/// cancels its daemon and waits a bounded time (`task.Wait(5s)`) for shutdown,
/// but the daemon's watcher / FCS / FileErrorReporter pipeline is cooperative —
/// in-flight checks can still emit `FileChecked` and have `FileErrorReporter`
/// (re)create `.fshw/errors/` and write JSON *after* `Wait` returns. When
/// `withTempDir`'s `finally` then runs `Directory.Delete(tmpDir, recursive=true)`
/// on the test thread, that background writer races the recursive delete: the OS
/// removes a subdir's entries, the writer recreates one, and `Directory.Delete`
/// throws `IOException "Directory not empty"` / `DirectoryNotFoundException`.
/// That exception escapes the test body via `finally` and is recorded by the
/// test host as an unattributed failure ("failed: 1" with no per-test name and
/// some tests unreported) — exactly the observed CI signature. (The same race is
/// already *tolerated* inside `FileErrorReporter.tryDelete`; the only unguarded
/// spot was this cleanup.) A direct micro-repro throws on ~80% of attempts.
///
/// Fix: the temp dir is genuinely transient, so cleanup is best-effort. Retry the
/// recursive delete a few times (the daemon's background work drains in well under
/// a second once cancelled), and if it still races, swallow — a test verdict must
/// never depend on whether the OS finished tearing down a scratch dir. This does
/// NOT mask real failures: a failing assertion throws from `body` *before*
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
            // A not-yet-drained daemon is still writing into the tree. Give it a
            // moment to finish, then retry. On the final attempt, give up: leaking
            // a scratch temp dir is harmless and far better than failing the test.
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

/// Write a minimal `.fsproj` with a `<TargetFramework>` and the given
/// `<Compile Include="…">` items. Returns nothing — the caller already knows
/// the path. Useful for tests that need `RegisterFromFsproj` to record both
/// TFM and source files (e.g. anything exercising canonical-DLL path lookup).
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
// Seeded test-prune environment scaffolding.
//
// Three TestPrunePlugin tests share ~25 lines of identical setup: write a
// single-file F# script into a temp dir, derive ProjectOptions, run a baseline
// `analyzeSource`, write the symbols into a fresh DB via `RebuildProjects`, mark
// the freshness sidecar clean for that file, then stand up a CheckPipeline +
// PluginHost wired to a TestPrunePlugin handler. The body block then performs
// the test-specific work (re-check, AST edit, etc.) and asserts.
//
// `withSeededTestEnv` factors that prelude into one place so the regression
// guards don't accumulate near-duplicate boilerplate.
// ----------------------------------------------------------------------------

/// State handed to a `withSeededTestEnv` body. All paths are temp-dir-relative
/// where appropriate; `FilePath` is absolute (the temp dir + RelPath) and
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

/// Seed a one-file F# project into a temp dir, persist its symbols to a fresh
/// SQLite DB, mark the freshness sidecar clean, and hand the body block a fully
/// wired pipeline + plugin host. The body performs whatever test-specific edits
/// + assertions it needs; cleanup happens in `withTempDir`'s `finally`.
let withSeededTestEnv (prefix: string) (relPath: string) (source: string) (body: SeededTestEnv -> unit) : unit =
    withTempDir prefix (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let filePath = Path.Combine(tmpDir, relPath)

        // Fresh checker per call (NOT sharedChecker): these envs drive a full
        // script-closure resolution + analyzeSource + CheckPipeline through the
        // checker while ~5 other test classes hit the shared instance
        // concurrently, so shared FCS caches (keyed by options/paths) could
        // bleed state across tests nondeterministically. Measured cost
        // (2026-06-12): ~3s per env for the un-amortized framework/script
        // resolution — the calling tests went 7s → 17s as a sequential class,
        // ~+10s (~1.1x) on the full suite, far under the 2x bar set for this
        // isolation change.
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
// `FsHotWatch.Logging.logLevel`/`verbose` are module-level mutables and
// `System.Console.Error` is process-wide. Several test modules need to set a
// log level and capture stderr to assert on log output. Under xUnit parallel
// execution these globals race across test classes: module A's `setLogLevel`
// or `Console.SetError` lands between module B's set and assert, so B reads the
// wrong level / captures the wrong writer and fails nondeterministically.
//
// Guard: every module that touches these globals carries
// `[<Collection(LogGlobalCollectionName)>]`, a DisableParallelization
// collection (see below). That serializes ONLY those modules with respect to
// each other; the ~40 other test modules keep running in parallel. Each site
// keeps its own save-level / SetError / restore-in-`finally` dance (now safe
// because no other mutator runs concurrently).
//
// De-globalizing `Logging` (threading an injected level through every
// `log`/`info`/`warn` call in the daemon, plugins, and ledger) would be far
// more invasive and would churn the public API surface that `check-api`
// guards, so the surgical serialized-collection approach is used instead —
// mirroring the existing `MacFsEvents` DisableParallelization collection.
// ----------------------------------------------------------------------------

/// Name of the serialized collection that groups every test class touching
/// PROCESS-GLOBAL state: the logging globals, `Console.Error`, and the process
/// ENVIRONMENT (`withEnv`). A literal so it can be used in the
/// `[<Collection(...)>]` / `[<CollectionDefinition(...)>]` attributes.
///
/// The environment is the third global and the last to be noticed: a class that
/// mutates it and then spawns a child which snapshots it is racing every other
/// class doing the same, and the symptom is a child that simply does not see the
/// variable that was just set — no exception, no timeout, just an empty string
/// where a value was expected.
[<Literal>]
let LogGlobalCollectionName = "LogGlobal"

/// xUnit collection that serializes every test class touching the logging
/// globals (`Logging.logLevel`/`verbose`) or `Console.Error`. Parallelization
/// is disabled so these classes never run concurrently with one another.
[<Xunit.CollectionDefinition(LogGlobalCollectionName, DisableParallelization = true)>]
type LogGlobalCollection() = class end

// ----------------------------------------------------------------------------
// Real-filesystem-watch serialization.
//
// Tests that exercise a live `FileSystemWatcher` (waiting on an OS file-change
// event) are timing-dependent: under heavy parallel CPU load the OS can take
// several seconds to deliver the event, blowing the test's `signal.Wait`
// budget and flaking nondeterministically (e.g. the DaemonConfig
// `watchConfigFile`/`watchRepoConfigFile` tests). This mirrors the existing
// `MacFsEvents` DisableParallelization collection: serialize the real-watcher
// tests so they don't compete with the rest of the suite (or each other) for
// file-watch delivery while the machine is saturated.
// ----------------------------------------------------------------------------

/// Name of the serialized collection grouping tests that wait on a live
/// `FileSystemWatcher` OS event.
[<Literal>]
let FileWatchCollectionName = "FileWatch"

[<Xunit.CollectionDefinition(FileWatchCollectionName, DisableParallelization = true)>]
type FileWatchCollection() = class end

/// A minimal RunVerdict for tests that only exercise the status TRANSITION —
/// the verdict content is irrelevant to them, but the type (correctly) will
/// not let a terminal status exist without one.
let testVerdict: FsHotWatch.Events.RunVerdict =
    FsHotWatch.Events.RunVerdict.create "test verdict" System.TimeSpan.Zero

/// Completed status at `at`, carrying the canonical test verdict.
let completedAt (at: System.DateTime) : FsHotWatch.Events.PluginStatus =
    FsHotWatch.Events.Completed(at, testVerdict)

/// Failed status at `at`, carrying the canonical test verdict.
let failedAt (error: string) (at: System.DateTime) : FsHotWatch.Events.PluginStatus =
    FsHotWatch.Events.Failed(error, at, testVerdict)

/// Cached per-file terminal for cache-entry fixtures. Per-file entries carry
/// no summary or timestamp BY CONSTRUCTION (AUTOMATION-186) — only the
/// measured duration; the replay derives the summary from the live ledger.
let cachedFileDone: FsHotWatch.TaskCache.CachedStatus =
    FsHotWatch.TaskCache.CachedFileCompleted System.TimeSpan.Zero

/// Cached whole-run terminal carrying the canonical test verdict (run entries
/// are keyed on their full input, so the verdict replays verbatim).
let cachedRunDone: FsHotWatch.TaskCache.CachedStatus =
    FsHotWatch.TaskCache.CachedRunCompleted testVerdict

/// `IpcParsing.parsePluginStatuses`, for tests that are asserting on the CONTENTS of a
/// payload they already know parses. It FAILS the test on an unreadable payload rather
/// than degrading to an empty map — which is the same rule the production callers now
/// follow, and the reason a test can't quietly start asserting over nothing.
let parseStatuses (json: string) : Map<string, FsHotWatch.Cli.RunOnceOutput.ParsedPluginStatus> =
    match FsHotWatch.Cli.IpcParsing.parsePluginStatuses json with
    | Ok statuses -> statuses
    | Error reason -> failwithf "the plugin-status payload could not be read: %s\n%s" reason json
