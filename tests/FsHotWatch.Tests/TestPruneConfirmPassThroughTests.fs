/// `confirm` runs every test by definition, so TestPrune runs it in PASS-THROUGH mode: one
/// unfiltered run, no impact selection, no per-symbol covering queries, and no re-run
/// intents. Debt that arrives while that run is in flight attaches to it, and the run
/// covers it only when the input tree it launched against is still the tree at
/// completion. `check` keeps impact selection.
module FsHotWatch.Tests.TestPruneConfirmPassThroughTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer
open TestPrune.Database

/// Impact queries over the real index, counting each kind.
type private Recorded() =
    let gate = obj ()
    let mutable affected = 0
    let mutable covering = 0

    member _.Queries(db: Database) : ImpactQueries =
        let real = ImpactQueries.ofDatabase db

        { real with
            AffectedTests =
                fun seeds ->
                    lock gate (fun () -> affected <- affected + 1)
                    real.AffectedTests seeds
            CoveringProjectsBySeed =
                fun seeds ->
                    lock gate (fun () -> covering <- covering + 1)
                    real.CoveringProjectsBySeed seeds }

    member _.AffectedQueries = lock gate (fun () -> affected)
    member _.CoveringQueries = lock gate (fun () -> covering)

/// A task cache that records every lookup outcome and keeps every write.
type private RecordingTaskCache() =
    let inner =
        FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

    let gate = obj ()
    let lookups = Collections.Generic.List<FsHotWatch.TaskCache.CompositeKey * bool>()

    let writes =
        Collections.Generic.Dictionary<
            FsHotWatch.TaskCache.CompositeKey,
            ContentHash * FsHotWatch.TaskCache.TaskCacheResult
         >()

    /// Lookups so far, as (key, hit).
    member _.Lookups = lock gate (fun () -> List.ofSeq lookups)

    /// The last write under each composite key.
    member _.Entries =
        lock gate (fun () -> writes |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq)

    interface FsHotWatch.TaskCache.ITaskCache with
        member _.TryGet key cacheKey =
            let result = inner.TryGet key cacheKey
            lock gate (fun () -> lookups.Add((key, Option.isSome result)))
            result

        member _.Lookup key cacheKey =
            let result = inner.Lookup key cacheKey

            let hit =
                match result with
                | FsHotWatch.TaskCache.CacheHit _ -> true
                | FsHotWatch.TaskCache.CacheMiss _ -> false

            lock gate (fun () -> lookups.Add((key, hit)))
            result

        member _.Set key cacheKey result =
            lock gate (fun () -> writes[key] <- (cacheKey, result))
            inner.Set key cacheKey result

        member _.Clear() = inner.Clear()
        member _.ClearPlugin plugin = inner.ClearPlugin plugin
        member _.ClearFile file = inner.ClearFile file
        member _.ClearPluginFile plugin file = inner.ClearPluginFile plugin file

/// Records the lifecycle events test-prune publishes, in order.
let private eventRecorder () =
    let received = Collections.Concurrent.ConcurrentQueue<string>()

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "parity-event-recorder"
          Init = ()
          Update =
            fun _ state event ->
                async {
                    match event with
                    | TestRunStarted started -> received.Enqueue $"TestRunStarted %A{started}"
                    | TestProgress progress -> received.Enqueue $"TestProgress %A{progress}"
                    | TestRunCompleted completed -> received.Enqueue $"TestRunCompleted %A{completed}"
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeTestRunStarted; SubscribeTestProgress; SubscribeTestRunCompleted ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }

    (fun () -> List.ofSeq received), handler

type private Scope =
    /// `set-scope full` before the run: what `fshw confirm` sends.
    | Confirm
    /// No scope request: what `fshw check` sends. With no baseline on disk, the first run
    /// still widens to the full suite to earn one.
    | Check

type private Setup =
    {
        Scope: Scope
        /// Cohorts that seal while the run is held.
        Cohorts: BatchCheckedTrigger list
        /// Edit the source after the cohorts and before the run is released.
        EditMidRun: bool
        /// After the run, a `check` over the same unchanged tree in the same daemon.
        WarmCheck: bool
    }

let private setup scope cohorts =
    { Scope = scope
      Cohorts = cohorts
      EditMidRun = false
      WarmCheck = false }

/// What a `check` after the scenario cost, in the same daemon over the unchanged tree.
type private Warm =
    { FileLookupMisses: (string * string option) list
      CoveringQueries: int
      Runs: int
      Status: PluginStatus option }

type private Outcome =
    {
        RunCount: int
        Queue: Set<string>
        Status: PluginStatus option
        Baseline: FullSuiteBaseline.Baseline option
        AffectedQueries: int
        CoveringQueries: int
        /// Everything observable, by name, normalized for run ids, times and durations.
        Observed: Map<string, string>
        Warm: Warm option
    }

let private libSource1 = "module Lib\nlet foo (x: int) = x + 1\n"
let private libSource2 = "module Lib\nlet foo (x: int) = x + 2\n"

let private testsSource =
    """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let fooTest () = assert (foo 1 = 2)
"""

let private ctrfReport =
    """{"results":{"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Tests.fooTest","status":"passed","duration":1}]}}"""

let private cobertura (root: string) =
    $"""<?xml version="1.0"?><coverage line-rate="1" branch-rate="1" version="1" timestamp="0"><sources><source>%s{root}</source></sources><packages><package name="Lib" line-rate="1" branch-rate="1"><classes><class name="Lib" filename="src/Lib.fsx" line-rate="1" branch-rate="1"><methods/><lines><line number="2" hits="1"/></lines></class></classes></package></packages></coverage>"""

/// Run ids, times and durations differ between any two runs; nothing else may.
let private normalize (root: string) (text: string) =
    let replace (pattern: string) (by: string) (input: string) = Regex.Replace(input, pattern, by)

    text.Replace(root, "<root>")
    |> replace @"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}" "<id>"
    |> replace @"\d{4}-\d\d-\d\d[T ][\d:.]+(Z|[+-]\d\d:?\d\d)?" "<time>"
    |> replace @"-?\d+[:.]\d\d:\d\d(\.\d+)?" "<span>"
    |> replace @"\d+(\.\d+)?\s?(ms|s)\b" "<duration>"
    |> replace @"(?i)(""?\w*(elapsed|duration|ticks)\w*""?\s*[:=]\s*)-?[\d.]+" "$1<n>"

/// Every file the plugin writes: `.fshw` (sidecars, run artifacts, flakiness history) and
/// the coverage directory (ratchet input), by repo-relative path, normalized.
let private writtenFiles (root: string) =
    [ ".fshw"; "coverage" ]
    |> List.map (fun dir -> Path.Combine(root, dir))
    |> List.filter Directory.Exists
    |> Seq.collect (fun dir -> Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    |> Seq.map (fun path ->
        let relative = Path.GetRelativePath(root, path).Replace('\\', '/')
        $"file:%s{normalize root relative}", normalize root (File.ReadAllText path))
    |> Map.ofSeq

/// Every row of every table in the TestPrune index, sorted.
let private indexRows (root: string) (dbPath: string) =
    clearSqlitePool dbPath
    use conn = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString dbPath)
    conn.Open()

    let tables =
        use query = conn.CreateCommand()
        query.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"
        use reader = query.ExecuteReader()

        [ while reader.Read() do
              reader.GetString 0 ]

    tables
    |> List.map (fun table ->
        use query = conn.CreateCommand()
        query.CommandText <- $"SELECT * FROM \"%s{table}\""
        use reader = query.ExecuteReader()

        let rows =
            [ while reader.Read() do
                  [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]
                  |> String.concat "|" ]
            |> List.map (normalize root)
            |> List.sort

        $"index:%s{table}", String.concat "\n" rows)
    |> Map.ofList

/// The cache as the scenario left it: the last write under each composite key.
let private cacheEntries (root: string) (cache: RecordingTaskCache) =
    cache.Entries
    |> List.map
        (fun
            (key: FsHotWatch.TaskCache.CompositeKey,
             (cacheKey: ContentHash, result: FsHotWatch.TaskCache.TaskCacheResult)) ->
            let name = $"cache:%s{key.Plugin}:%s{key.File |> Option.defaultValue String.Empty}"

            name,
            normalize
                root
                $"%s{ContentHash.value cacheKey}\n%A{result.Status}\n%A{result.EmittedEvents}\n%A{result.Errors}")
    |> Map.ofList

/// `test-scope`, one entry per top-level property.
let private testScope (root: string) (host: PluginHost) =
    let reply =
        host.RunCommand("test-scope", [||]) |> Async.RunSynchronously |> Option.get

    use json = JsonDocument.Parse reply

    json.RootElement.EnumerateObject()
    |> Seq.map (fun p -> $"test-scope:%s{p.Name}", normalize root (p.Value.GetRawText()))
    |> Map.ofSeq

/// A cold daemon over a warm index, rooted at `root`: `Lib.foo` changed before the build,
/// the build launches the one run, and the cohorts seal while that run is held. A BootScan
/// cohort checks every file, as a boot scan does; an in-session cohort checks the edited
/// one. The runner writes a CTRF report and a cobertura file, so flakiness history and
/// coverage-ratchet input are real. Sources live under `src/` so the run's input-tree
/// identity sees them.
let private runScenario (root: string) (setup: Setup) =
    let dbPath = Path.Combine(root, "tp.db")
    let srcDir = Path.Combine(root, "src")
    Directory.CreateDirectory srcDir |> ignore
    let libFile = Path.Combine(srcDir, "Lib.fsx")
    let testsFile = Path.Combine(srcDir, "Tests.fsx")
    let runMarker = Path.Combine(root, "runs")
    let started = Path.Combine(root, "started")
    let runner = Path.Combine(root, "runner.sh")

    withReleaseGate root "pass-through" (fun release ->
        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        File.WriteAllText(
            runner,
            "#!/bin/sh\n"
            + $"printf 'run\\n' >> '%s{runMarker}'\n"
            + $"touch '%s{started}'\n"
            + gatedWait root release 3000
            + "\n"
            + "cov=''; name=''; dir=''\n"
            + "while [ $# -gt 0 ]; do\n"
            + "  case \"$1\" in\n"
            + "    --coverage-output) cov=\"$2\"; shift ;;\n"
            + "    --report-ctrf-filename) name=\"$2\"; shift ;;\n"
            + "    --results-directory) dir=\"$2\"; shift ;;\n"
            + "  esac\n"
            + "  shift\n"
            + "done\n"
            + $"if [ -n \"$dir\" ] && [ -n \"$name\" ]; then printf '%%s' '%s{ctrfReport}' > \"$dir/$name\"; fi\n"
            + $"if [ -n \"$cov\" ]; then printf '%%s' '%s{cobertura root}' > \"$cov\"; fi\n"
        )

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        pipeline.RegisterProject(
            libFile,
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }
        )

        let check (host: PluginHost) file =
            match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
            | Some result -> host.EmitFileChecked(stampFixture result)
            | None -> failwith $"check failed for {file}"

        // Prime the index in an analysis-only host. No run, so no baseline on disk.
        let primingHost = createModelHost checker root
        primingHost.RegisterHandler(create dbPath root None None None None None [])
        primingHost.EmitBuildCompleted(BuildSucceeded)
        waitForPluginIdle primingHost "test-prune" 5.0

        for file in [ libFile; testsFile ] do
            check primingHost file

        emitBatchAndQuiesce primingHost [ libFile; testsFile ]

        let configs =
            [ { Project = "Lib"
                Command = "sh"
                Args = runner
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 15
                ReportVerificationFormat = Ctrf } ]

        let coveragePaths _ =
            let dir = Path.Combine(root, "coverage")

            Some
                { Baseline = Path.Combine(dir, BaselineName)
                  Partial = Path.Combine(dir, PartialName)
                  Cobertura = Path.Combine(dir, CoberturaName)
                  IncludeInRatchet = true
                  ArgsTemplate = "--coverage-output {output}" }

        let recorded = Recorded()
        let cache = RecordingTaskCache()

        let host =
            PluginHost(checker, root, taskCache = (cache :> FsHotWatch.TaskCache.ITaskCache))

        host.WorkStore.PublishProjectModel fixtureModel
        let events, recorder = eventRecorder ()
        host.RegisterHandler recorder
        let statuses = Collections.Concurrent.ConcurrentQueue<string>()

        host.OnStatusChanged.Add(fun (name, status) ->
            if name = "test-prune" then
                statuses.Enqueue(normalize root $"%A{status}"))

        host.RegisterHandler(
            createWithQueries
                recorded.Queries
                (TimeSpan.FromMinutes 5.0)
                (fun () -> Map.empty)
                dbPath
                root
                (Some configs)
                None
                None
                None
                (Some coveragePaths)
                []
                None
        )

        match setup.Scope with
        | Confirm ->
            host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
            |> Async.RunSynchronously
            |> ignore
        | Check -> ()

        File.WriteAllText(libFile, libSource2)
        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists started) 10000

        let seal (trigger: BatchCheckedTrigger) (files: string list) =
            let committedBefore = committedBy host "test-prune"

            for file in files do
                check host file

            host.EmitBatchChecked(
                { fakeBatchChecked files with
                    Trigger = trigger }
            )

            test <@ waitForCommitted host "test-prune" committedBefore (int64 files.Length + 1L) 10000 @>

        for trigger in setup.Cohorts do
            match trigger with
            | BootScan -> seal trigger [ libFile; testsFile ]
            | InSessionBatch _ -> seal trigger [ libFile ]

        if setup.EditMidRun then
            File.WriteAllText(libFile, "module Lib\nlet foo (x: int) = x + 3\n")
            seal (InSessionBatch [ SourceChanged [ libFile ] ]) [ libFile ]

        File.WriteAllText(release, "")
        waitForQuiescent host 20000

        let runCount () = File.ReadAllLines(runMarker).Length

        let observed =
            [ yield! writtenFiles root |> Map.toList
              yield! indexRows root dbPath |> Map.toList
              yield! cacheEntries root cache |> Map.toList
              yield! testScope root host |> Map.toList
              "events", events () |> List.map (normalize root) |> String.concat "\n"
              "statuses", statuses |> String.concat "\n" ]
            |> Map.ofList

        let outcome =
            { RunCount = runCount ()
              Queue = PendingQueueHelpers.loadQueue root
              Status = host.GetStatus("test-prune")
              Baseline =
                match FullSuiteBaseline.load root with
                | FullSuiteBaseline.LoadedBaseline.Loaded baseline -> baseline
                | FullSuiteBaseline.LoadedBaseline.Unreadable reason -> failwith $"unreadable baseline: {reason}"
              AffectedQueries = recorded.AffectedQueries
              CoveringQueries = recorded.CoveringQueries
              Observed = observed
              Warm = None }

        if not setup.WarmCheck then
            outcome
        else
            // `check` over the unchanged tree: its scan re-checks every file and seals a
            // cohort, and its build completes.
            let lookupsBefore = cache.Lookups.Length
            let coveringBefore = recorded.CoveringQueries
            let runsBefore = runCount ()
            seal BootScan [ libFile; testsFile ]
            host.EmitBuildCompleted(BuildSucceeded)
            waitForQuiescent host 20000

            let misses =
                cache.Lookups
                |> List.skip lookupsBefore
                |> List.filter (fun (key: FsHotWatch.TaskCache.CompositeKey, hit) ->
                    key.Plugin = "test-prune" && Option.isSome key.File && not hit)
                |> List.map (fun (key: FsHotWatch.TaskCache.CompositeKey, _) -> key.Plugin, key.File)

            { outcome with
                Warm =
                    Some
                        { FileLookupMisses = misses
                          CoveringQueries = recorded.CoveringQueries - coveringBefore
                          Runs = runCount () - runsBefore
                          Status = host.GetStatus("test-prune") } })

let private scenarioWith name (setup: Setup) =
    withTempDir name (fun root -> runScenario root setup)

let private scenario name (scope: Scope) (cohorts: BatchCheckedTrigger list) (editMidRun: bool) =
    scenarioWith
        name
        { setup scope cohorts with
            EditMidRun = editMidRun }

let private inSession = InSessionBatch [ SourceChanged [ "src/Lib.fsx" ] ]

let private assertGreen (outcome: Outcome) =
    match outcome.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the run to complete green, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``a cold confirm with debt sealing mid-run runs once and asks no covering query`` () =
    // Before pass-through, the in-session cohort queued an impact re-run behind the held
    // run, and every flush and the completion classified the queue by covering project.
    let outcome = scenario "tp-pt-once" Confirm [ BootScan; inSession ] false
    Assert.Equal(1, outcome.RunCount)
    test <@ outcome.CoveringQueries = 0 @>
    // The one grouped selection the launch keeps for the check-reach sample, at most.
    test <@ outcome.AffectedQueries <= 1 @>
    assertGreen outcome

[<Fact(Timeout = 30000)>]
let ``a green confirm records the baseline and clears the debt it covered`` () =
    let outcome = scenario "tp-pt-baseline" Confirm [ BootScan ] false
    Assert.Equal(1, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>
    test <@ outcome.Baseline |> Option.exists (fun b -> b.Projects = Set.ofList [ "Lib" ]) @>
    assertGreen outcome

[<Fact(Timeout = 30000)>]
let ``a tree edit during a confirm leaves no verdict and never a second run`` () =
    let outcome = scenario "tp-pt-moved" Confirm [ BootScan ] true
    Assert.Equal(1, outcome.RunCount)
    test <@ outcome.Queue.Contains "Lib.foo" @>

    match outcome.Status with
    | Some(Completed _) -> Assert.Fail("a run over a tree that moved under it must not report green")
    | _ -> ()

[<Fact(Timeout = 30000)>]
let ``check attaches boot-scan debt to its in-flight full run`` () =
    // No baseline, so check's first run is full. The boot cohort scanned the tree that run
    // is testing, so the run covers it.
    let outcome = scenario "tp-check-boot" Check [ BootScan ] false
    Assert.Equal(1, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>
    test <@ outcome.AffectedQueries > 0 @>
    assertGreen outcome

[<Fact(Timeout = 30000)>]
let ``check still queues one rerun for an in-session cohort during its full run`` () =
    // Mutation caught: attaching every cohort, not only BootScan, would let check skip the
    // run an edit owes.
    let outcome = scenario "tp-check-in-session" Check [ inSession ] false
    Assert.Equal(2, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>
    assertGreen outcome

/// Two projects, `Lib.foo` queued and covered by ProjA only, a baseline on disk: a `check`
/// selects ProjA, a `confirm` runs both. `steps` drive one daemon; each is a scope request
/// (or none) and the build it provokes. Returns how often each project ran.
let private scopeSequence name (steps: string option list) =
    withTempDir name (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "ProjA" "ATests" "fooTest"
        seedBaseline tmpDir [ "ProjA"; "ProjB" ]
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        let runs = Path.Combine(tmpDir, "runs")
        File.WriteAllText(runs, "")

        let config project =
            { Project = project
              Command = "sh"
              Args = $"-c \"printf '%s{project}\\n' >> '%s{runs}'\""
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              TimeoutSec = None
              ReportVerificationFormat = AutoDetect }

        let host = createModelHost (Unchecked.defaultof<_>) tmpDir

        host.RegisterHandler(create dbPath tmpDir (Some [ config "ProjA"; config "ProjB" ]) None None None None [])

        for scope in steps do
            match scope with
            | Some scope ->
                host.RunCommand("set-scope", [| $"{{\"scope\":\"%s{scope}\"}}" |])
                |> Async.RunSynchronously
                |> ignore
            | None -> ()

            emitBuildAndWaitTerminal host

        File.ReadAllLines(runs) |> Array.countBy id |> Map.ofArray)

let private confirmStep = Some "full"
let private checkStep = None

[<Fact(Timeout = 30000)>]
let ``full-suite scope ends with the confirm's run`` () =
    // `set-scope full` lasts for the run launched under it. A later build in the same
    // daemon is `check`'s: nothing is owed and the confirm earned the baseline, so it runs
    // nothing.
    let executions = scopeSequence "tp-pt-ends" [ confirmStep; checkStep ]
    test <@ executions = Map.ofList [ "ProjA", 1; "ProjB", 1 ] @>

[<Fact(Timeout = 30000)>]
let ``check, confirm, check in one daemon: the last check runs nothing`` () =
    // The first check selects ProjA for `Lib.foo`, and runs ProjB too: a fresh daemon has
    // no whole-project run of it under its model, so a filtered or skipped ProjB would
    // leave that check's evidence refusing. The confirm runs both; the last check is back
    // under impact selection and owes nothing.
    let executions = scopeSequence "tp-pt-ccc" [ checkStep; confirmStep; checkStep ]
    test <@ executions = Map.ofList [ "ProjA", 2; "ProjB", 2 ] @>

[<Fact(Timeout = 30000)>]
let ``every confirm runs the suite, never a cached one`` () =
    // A confirm bypasses cache reads for its evidence. Each one in a warm daemon over an
    // unchanged tree runs both projects again. The first check runs both as well: a fresh
    // daemon has no whole-project evidence under its model.
    let executions =
        scopeSequence "tp-pt-no-replay" [ checkStep; confirmStep; confirmStep; confirmStep ]

    test <@ executions = Map.ofList [ "ProjA", 4; "ProjB", 4 ] @>

[<Fact(Timeout = 30000)>]
let ``set-scope impact ends pass-through before any run`` () =
    let executions = scopeSequence "tp-pt-impact" [ confirmStep; Some "impact" ]
    // The confirm's run, then a check that owes nothing.
    test <@ executions = Map.ofList [ "ProjA", 1; "ProjB", 1 ] @>

/// The observables a full run owns because it was SELECTED: what `check`'s widening and
/// `confirm`'s request may report differently about the same run. Everything else must
/// match. Each entry is a decision, made here in view.
let private selectionOwned: (string * string) list =
    [ "test-scope:seeds", "the symbols whose impact selection launched the run; pass-through selects nothing"
      "test-scope:seedCount", "the count of those symbols" ]

let private runAt (root: string) (setup: Setup) =
    Directory.CreateDirectory root |> ignore

    try
        runScenario root setup
    finally
        clearSqlitePool (Path.Combine(root, "tp.db"))

        try
            Directory.Delete(root, true)
        with _ ->
            ()

/// One run lifecycle, two modes: pass-through only subtracts `PassThroughSkip`, so its run
/// must be observably the full run a cold `check` makes, except where `selectionOwned`
/// says. Driven over the in-process plugin host, which is the `--run-once` transport. The
/// daemon transport delivers the plugin the same events over IPC, so it is not repeated.
[<Fact(Timeout = 90000)>]
let ``a confirm's run is observably the run check makes to earn its baseline`` () =
    // One root for both, so absolute paths, and every hash over them, agree.
    let root =
        Path.Combine(Path.GetTempPath(), "fshw-parity-" + Guid.NewGuid().ToString("N"))
        |> Path.GetFullPath

    let cold = runAt root (setup Check [ BootScan ])
    let confirm = runAt root (setup Confirm [ BootScan ])

    for outcome in [ cold; confirm ] do
        Assert.Equal(1, outcome.RunCount)
        assertGreen outcome

    // PRESENT, not merely equal: a scenario that recorded nothing would match itself.
    let keys = cold.Observed |> Map.keys |> List.ofSeq

    let missing =
        [ for required in [ "events"; "statuses"; "file:.fshw/test-history.json"; "test-scope:scope" ] do
              if not (List.contains required keys) then
                  required
          // The per-file analysis entries the cohort wrote while the run owned the status.
          for prefix in [ "index:"; "cache:test-prune:"; "file:.fshw/test-runs/"; "file:coverage/" ] do
              if not (keys |> List.exists (fun k -> k.StartsWith prefix)) then
                  prefix + "*" ]

    test <@ List.isEmpty missing @>

    let owned = selectionOwned |> List.map fst |> Set.ofList

    let differing =
        Set.union (cold.Observed |> Map.keys |> Set.ofSeq) (confirm.Observed |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun key -> not (owned.Contains key))
        |> Set.filter (fun key -> Map.tryFind key cold.Observed <> Map.tryFind key confirm.Observed)
        |> Set.toList

    if not (List.isEmpty differing) then
        let describe key =
            let side (o: Outcome) =
                Map.tryFind key o.Observed |> Option.defaultValue "<absent>"

            $"== %s{key}\n-- check:\n%s{side cold}\n-- confirm:\n%s{side confirm}"

        Assert.Fail(
            "confirm's run differs from check's cold full run outside what selection owns:\n"
            + String.Join("\n", differing |> List.map describe)
        )

[<Fact(Timeout = 60000)>]
let ``a check after a confirm in the same daemon is served warm`` () =
    let outcome =
        scenarioWith
            "tp-pt-warm"
            { setup Confirm [ BootScan ] with
                WarmCheck = true }

    Assert.Equal(1, outcome.RunCount)
    let warm = outcome.Warm.Value
    // Every file the check re-checks replays test-prune's cached analysis, including the
    // ones the confirm analysed while its run owned the status.
    test <@ List.isEmpty warm.FileLookupMisses @>
    test <@ warm.CoveringQueries = 0 @>
    test <@ warm.Runs = 0 @>

    match warm.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the warm check to report the confirm's green, got %A{other}")

[<Fact(Timeout = 60000)>]
let ``a covered full run leaves nothing for the next cohort over the same tree`` () =
    // The BootScan cohort attached to the full run and the run covered it. Its files and
    // their runtime-coverage obligations are covered too: before, they stayed in
    // `ChangedFiles`, the next flush turned them back into obligations, and a check over
    // the unchanged tree ran the suite again.
    let outcome =
        scenarioWith
            "tp-covered-files"
            { setup Check [ BootScan ] with
                WarmCheck = true }

    Assert.Equal(1, outcome.RunCount)
    test <@ outcome.Warm.Value.Runs = 0 @>
