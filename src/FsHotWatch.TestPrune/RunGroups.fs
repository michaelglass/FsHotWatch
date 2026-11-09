/// A run's groups: reuse, the stale-artifact refusal, watched and sampled launches, the starvation re-run and the group fold.
module FsHotWatch.TestPrune.RunGroups

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.TestPrune.TestReports
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.PluginCommands
open FsHotWatch.TestPrune.IndexMaintenance
open FsHotWatch.TestPrune.RunSupport
open FsHotWatch.TestPrune.ProjectLaunch

/// One project launched under a starvation sampler: its result, CTRF report text and the
/// load reading taken while it ran.
type internal SampledLauncher =
    Threading.CancellationToken -> TestConfig -> Async<TestResult * string option * Starvation.Reading>

/// What the starvation re-run reads from its run (`rerunStarvedWith`).
type internal RerunEnv =
    { RepoRoot: string
      RunDir: string
      Collected: RunCollector
      LogToCtx: string -> unit
      LaunchSampled: SampledLauncher }

/// Re-run every held-back project, one at a time, after every group has finished:
/// alone, as far as this run is concerned. The first attempt's report, log and
/// timeout marker are set aside first, so the run directory's report for the
/// project is the re-run's; `<Project>.starved.json` records both attempts.
let internal rerunStarvedWith (env: RerunEnv) () : Async<(string * TestResult) list> =
    let repoRoot = env.RepoRoot
    let runDir = env.RunDir
    let collected = env.Collected
    let logToCtx = env.LogToCtx
    let launchSampled = env.LaunchSampled

    async {
        let pending = collected.Read(fun c -> List.rev c.Starved)
        let! runToken = Async.CancellationToken
        let mutable rerun = []

        for (config, timedOut, firstReading) in pending do
            let policy =
                config.Starvation |> Option.defaultValue (Starvation.Policy.rerunAlone [])

            // `runCoveragePath`'s suffix: the copy of the raw coverage a run keeps.
            Starvation.setAside
                runDir
                config.Project
                [ Ctrf.ReportSuffix
                  RunLog.Suffix
                  TimeoutMarker.Suffix
                  Profile.Suffix
                  ".coverage.cobertura.xml" ]
            |> ignore

            // The first attempt's trace, if any, describes a run that is no longer
            // the project's result.
            collected.Update(fun c ->
                { c with
                    TraceRuns = c.TraceRuns |> List.filter (fun t -> t.Project <> config.Project) })

            // PREFLIGHTED LIKE ANY LAUNCH. The run-wide preflight certified this
            // tree before the first attempt, not after it. A first attempt torn down
            // with its whole tree takes the coverage controller with it before it puts
            // back the assemblies it instrumented in this project's output, and a
            // coverage run over an instrumented copy passes and writes an empty report
            // (land entry 224: 56,724 -> 0 covered entries). The copies the build made
            // are repaired from their origins first, and a tree this cannot certify
            // is not launched: its re-run is deferred, never a pass.
            let refusal =
                deriveProjectBin config.Args repoRoot
                |> Option.bind (fun target ->
                    let checkedTree =
                        StaleArtifactPreflight.run repoRoot DateTime.UtcNow [ config.Project, target ]

                    for repaired in checkedTree.Healed do
                        let line =
                            $"%s{config.Project}: repaired %s{repaired} before its re-run alone (the first \
                              attempt's teardown left it differing from the build's copy)"

                        Logging.warn "test-prune" line
                        logToCtx line

                    checkedTree.Refusals |> List.tryHead |> Option.map _.Reason)

            let! result, reading =
                match refusal with
                | Some reason ->
                    async {
                        Logging.warn "test-prune" $"%s{config.Project}: not re-run alone — %s{reason}"
                        logToCtx $"{config.Project}: not re-run alone — {reason}"

                        let notLaunched: Starvation.Reading =
                            { Cores = Environment.ProcessorCount
                              Samples = [] }

                        return TestsDeferred reason, notLaunched
                    }
                | None ->
                    async {
                        let! result, _, reading = launchSampled runToken config
                        return result, reading
                    }

            let outcome =
                match result with
                | TestsPassed _ -> "passed"
                | TestsFailed _ -> "failed"
                | TestsTimedOut _ -> "timed out again"
                | TestsErrored _ -> "aborted"
                | TestsDeferred _ -> "was deferred"
                | TestsNoMatch _ -> "matched no test"

            let record: Starvation.Record =
                { Project = config.Project
                  TimedOut = timedOut
                  FirstAttempt = firstReading
                  Rerun = reading
                  RerunOutcome = outcome
                  PassedAlone = TestResult.verifiedGreen result
                  AttemptDir = Path.Combine(runDir, Starvation.AttemptDirName) }

            Starvation.write policy runDir record
            let line = Starvation.summary record

            if record.PassedAlone then
                Logging.warn "test-prune" line
            else
                Logging.error "test-prune" line

            logToCtx line
            rerun <- (config.Project, result) :: rerun

        return List.rev rerun
    }

/// What reusing a project's stored test evidence reads from its run (`tryReuseWith`).
type internal ReuseEnv =
    { RepoRoot: string
      RunDir: string
      Collected: RunCollector
      LogToCtx: string -> unit
      Reusable: Map<string, TestEvidenceStore.Entry>
      CoveragePathsByProject: Map<string, CoveragePaths> }

/// File `entry` as this run's evidence for `config`; `false` when it could not be
/// filed, and then the project is launched like any other. A filed project reports
/// NOTHING until the completion re-check has confirmed it (`TestEvidenceRun.reconcile`):
/// no progress subscriber may see a green the re-check could still withdraw.
let internal tryReuseWith (env: ReuseEnv) (config: TestConfig) : bool =
    let repoRoot = env.RepoRoot
    let runDir = env.RunDir
    let collected = env.Collected
    let logToCtx = env.LogToCtx
    let reusable = env.Reusable
    let coveragePathsByProject = env.CoveragePathsByProject

    Map.tryFind config.Project reusable
    |> Option.exists (fun entry ->
        try
            Directory.CreateDirectory runDir |> ignore

            File.WriteAllText(
                Path.Combine(runDir, config.Project + Ctrf.ReportSuffix),
                TestEvidenceRun.reusedReport repoRoot entry
            )

            match Map.tryFind config.Project coveragePathsByProject, entry.Coverage with
            | Some paths, Some blob ->
                let content = TestEvidenceStore.rebind repoRoot blob.Content
                // Where a launch would have written it; a fresh checkout has no
                // such directory until something writes there.
                Directory.CreateDirectory(Path.GetDirectoryName paths.Baseline) |> ignore
                File.WriteAllText(paths.Baseline, content)
                File.WriteAllText(runCoveragePath runDir config.Project, content)

                collected.Update(fun c ->
                    { c with
                        CoverageInputs =
                            coverageInput config.Project paths.IncludeInRatchet paths.Baseline
                            :: c.CoverageInputs })
            | _ -> ()

            collected.Update(fun c ->
                { c with
                    Materialized = Map.add config.Project entry c.Materialized })

            let line =
                $"%s{config.Project}: %s{TestEvidenceRun.Provenance.describe (TestEvidenceRun.Provenance.ofEntry entry)}"

            Logging.info "test-prune" line
            logToCtx line
            true
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? System.Text.Json.JsonException as ex ->
            Logging.warn
                "test-prune"
                $"%s{config.Project}: could not file reused evidence (%s{ex.Message}) — launching it instead"

            TestEvidenceRun.deleteQuietly (Path.Combine(runDir, config.Project + Ctrf.ReportSuffix))
            false)

/// The stale-artifact preflight over every project a run will launch: a coverage gap is
/// said, and a repaired output is logged.
let internal preflightLaunch
    (repoRoot: string)
    (configs: TestConfig list)
    (skipProjectOf: TestConfig -> bool)
    (logToCtx: string -> unit)
    : StaleArtifactPreflight.Outcome =
    // Every project that may launch is preflighted, a reusable one included: its filing
    // can still fail, and then it launches like any other.
    let runnableConfigs = configs |> List.filter (fun c -> not (skipProjectOf c))

    let preflightTargets =
        runnableConfigs
        |> List.choose (fun c -> deriveProjectBin c.Args repoRoot |> Option.map (fun t -> c.Project, t))

    // THE FLOOR. `List.choose` above DROPS every config whose build-output target
    // could not be derived, and a dropped project is indistinguishable downstream
    // from a project that was checked and found fresh — so a derivation that
    // regressed would silently reduce this gate to checking nothing while every run
    // stayed green. Say what was covered; see `coverageReport` for why it reports
    // instead of refusing.
    match
        StaleArtifactPreflight.coverageReport
            (runnableConfigs |> List.map (fun c -> c.Project))
            (preflightTargets |> List.map fst)
    with
    | Some gap ->
        Logging.warn "test-prune" gap
        logToCtx gap
    | None -> ()

    let preflight = StaleArtifactPreflight.run repoRoot DateTime.UtcNow preflightTargets

    for repaired in preflight.Healed do
        logToCtx $"repaired stale build output before running: {repaired}"

    preflight

/// Fold one group's results into the run and emit them as progress.
type internal GroupFold = (string * TestResult) list -> unit

/// What refusing a whole run on stale artifacts reads from it (`refuseWholeRunWith`).
type internal RefusalEnv =
    { Configs: TestConfig list
      Preflight: StaleArtifactPreflight.Outcome
      LogToCtx: string -> unit
      SkipProjectOf: TestConfig -> bool
      SkippedResultOf: TestConfig -> string * TestResult
      FoldAndEmit: GroupFold }

/// Nothing spawns. Every configured project comes back deferred, naming its own
/// reason where the preflight found one and the run-wide cause where it did not.
let internal refuseWholeRunWith (env: RefusalEnv) () =
    let configs = env.Configs
    let preflight = env.Preflight
    let logToCtx = env.LogToCtx
    let skipProjectOf = env.SkipProjectOf
    let skippedResultOf = env.SkippedResultOf
    let foldAndEmit = env.FoldAndEmit

    async {
        let refused =
            preflight.Refusals |> List.map (fun r -> r.Project, r.Reason) |> Map.ofList

        // Every affected project, named in full — the headline may be
        // shortened by a fixed-width surface, but this list never is.
        let names =
            preflight.Refusals |> List.map (fun r -> r.Project) |> String.concat ", "

        for refusal in preflight.Refusals do
            Logging.warn "test-prune" $"%s{refusal.Project}: %s{refusal.Reason}"
            logToCtx $"{refusal.Project}: waiting on build ({refusal.Reason})"

        let results =
            configs
            |> List.map (fun config ->
                if skipProjectOf config then
                    skippedResultOf config
                else
                    match refused.TryFind config.Project with
                    | Some reason -> config.Project, TestsDeferred reason
                    | None ->
                        config.Project,
                        TestsDeferred
                            // The remedy points at the projects
                            // that carry one rather than restating a generic
                            // build command. Each named project's own deferral
                            // holds the remedy for ITS cause, and those causes
                            // need different actions — a raw `dotnet build`
                            // fixes neither a tripped repair breaker nor a
                            // byte-differing copy, and some repositories refuse
                            // that command outright.
                            $"not run — the whole run was refused before any suite launched because \
                              %d{preflight.Refusals.Length} project(s) have stale build output: \
                              %s{names}. Remedy: read those projects' own deferrals — each names its \
                              cause and what to do about it — then re-run.")

        foldAndEmit results
        return [| results |]
    }

/// One project's launch: its result and CTRF report text.
type internal ProjectLauncher = Threading.CancellationToken -> TestConfig -> Async<TestResult * string option>

/// What watching one project's launch reads from its run (`launchWatchedWith`).
type internal WatchEnv =
    {
        Collected: RunCollector
        /// A project's result is final: no starvation re-run is pending for it.
        Settled: string -> unit
        LogToCtx: string -> unit
        RunDir: string
        LaunchProject: ProjectLauncher
        LaunchSampled: SampledLauncher
        SelectionOf: TestConfig -> string list option
    }

/// Launch `config`'s project; `None` when it timed out under starvation and is
/// held back for its re-run alone.
let internal launchWatchedWith
    (env: WatchEnv)
    (runToken: Threading.CancellationToken)
    (config: TestConfig)
    : Async<TestResult option> =
    let collected = env.Collected
    let logToCtx = env.LogToCtx
    let launchProject = env.LaunchProject
    let launchSampled = env.LaunchSampled
    let selectionOf = env.SelectionOf

    async {
        match config.Starvation with
        | None ->
            let! result, _ = launchProject runToken config
            env.Settled config.Project
            return Some result
        | Some policy ->
            let! result, reportJson, reading = launchSampled runToken config

            match Starvation.decide (Some policy) (selectionOf config) result reportJson reading with
            | Starvation.RerunAlone(timedOut, firstReading) ->
                let line =
                    $"%s{config.Project}: %s{Starvation.TimedOut.describe timedOut} under CPU starvation \
                      (%s{Starvation.Reading.describe policy firstReading}); re-running it ALONE once the \
                      rest of the run has finished — its re-run is the result reported"

                Logging.warn "test-prune" line
                logToCtx line

                collected.Update(fun c ->
                    { c with
                        Starved = (config, timedOut, firstReading) :: c.Starved })

                return None
            | Starvation.Report why ->
                why
                |> Option.iter (fun reason ->
                    let line = $"%s{config.Project}: starvation declared; %s{reason}"
                    Logging.info "test-prune" line
                    logToCtx line
                    Starvation.writeNotRerun policy env.RunDir config.Project reason reading)

                env.Settled config.Project

                return Some result
    }

/// One project's watched launch: its settled result, or `None` when the starvation re-run
/// takes it.
type internal WatchedLauncher = Threading.CancellationToken -> TestConfig -> Async<TestResult option>

/// What running every scheduled group reads from its run (`runAllGroupsWith`).
type internal GroupsEnv =
    { ScheduledGroups: (string * TestConfig list) list
      Processes: FsHotWatch.WorkScheduler.Lane option
      MaxParallelProjects: int option
      SkipProjectOf: TestConfig -> bool
      SkippedResultOf: TestConfig -> string * TestResult
      TryReuse: TestConfig -> bool
      LaunchWatched: WatchedLauncher
      FoldAndEmit: GroupFold }

/// The normal path: every target certified fresh, so the groups launch in
/// parallel (at most `maxParallelProjects` at a time) and fold their results into
/// the shared accumulator as they finish, in whatever order that is.
let internal runAllGroupsWith (env: GroupsEnv) () =
    let scheduledGroups = env.ScheduledGroups
    let processes = env.Processes
    let maxParallelProjects = env.MaxParallelProjects
    let skipProjectOf = env.SkipProjectOf
    let skippedResultOf = env.SkippedResultOf
    let tryReuse = env.TryReuse
    let launchWatched = env.LaunchWatched
    let foldAndEmit = env.FoldAndEmit

    scheduledGroups
    |> List.map (fun (_, groupConfigs) ->
        // A group is charged the largest weight among its projects (ADR-053).
        FsHotWatch.WorkScheduler.JobSpec.weighted
            (groupConfigs |> List.map _.Weight |> List.fold max 1.0)
            FsHotWatch.WorkScheduler.JobSpec.interactive,
        async {
            // The run's token: trace preparation (weave + JIT verification) stops
            // when the run is cancelled.
            let! runToken = Async.CancellationToken
            let mutable results = []

            for config in groupConfigs do
                // FRESHNESS IS ALREADY SETTLED. The preflight above
                // asked `ArtifactFreshness.stale` about every config in this run before
                // the first spawn and refused the whole run if any answer was stale, so
                // reaching this line means the bits match the sources. There is
                // deliberately NO second freshness check here: a per-config gate inside
                // the parallel body is precisely what let one group write its CTRF
                // before another group's staleness had even been looked at.
                //
                // Template-based class filter (from impact analysis). When the map is
                // non-empty but has no classes for this project, skip the project
                // entirely (impact analysis found no relevant tests).
                let filed = not (skipProjectOf config) && tryReuse config

                match skipProjectOf config with
                | true ->
                    Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"
                    results <- skippedResultOf config :: results
                | false when filed -> ()
                | false ->
                    let! settled = launchWatched runToken config

                    match settled with
                    | Some result -> results <- (config.Project, result) :: results
                    | None -> ()

            // Atomically fold this group's results into the shared
            // accumulator and emit a cumulative snapshot. Groups that
            // complete later will extend (never contradict) this one.
            foldAndEmit results
            return results
        })
    |> GroupSchedule.runAdmittedAs processes maxParallelProjects

/// `launchProjectOrRaise`, with a launch stall answered as THIS project's result.
///
/// A stall ("launch produced no live process within Ns") means this project never
/// ran. It used to raise out of the whole run, which then completed `Aborted` with
/// no results at all: the projects that had run, or were still running, lost what
/// they verified, and the next run ran every one of them in full again. Now the
/// project is `TestsErrored` — nothing verified, never a pass, so the run is still
/// not green — and every other project keeps its own result.
/// One project's launch, with a stalled launch reported as that project's abort rather than the run's.
let internal launchProjectGuarded
    (launchProjectOrRaise: ProjectLauncher)
    (logToCtx: string -> unit)
    (runToken: Threading.CancellationToken)
    (config: TestConfig)
    : Async<TestResult * string option> =
    async {
        try
            return! launchProjectOrRaise runToken config
        with LaunchStalledException reason ->
            logToCtx $"{config.Project}: ABORTED (nothing verified) — {reason}"

            Logging.error
                "test-prune"
                $"%s{config.Project}: ABORTED — %s{reason}. Nothing was verified for this project; the rest of the run keeps its results. Re-run when the box is quiet."

            return TestsErrored reason, None
    }

/// `launchProject`, with the load sampled while it runs.
let internal launchSampledWith
    (launchProject: ProjectLauncher)
    (runToken: Threading.CancellationToken)
    (config: TestConfig)
    =
    async {
        let sampler =
            Starvation.Sampler.start Starvation.Sampler.DefaultInterval OperationWatchdog.loadAverage

        let samples = ref []

        let! result, reportJson =
            async {
                try
                    return! launchProject runToken config
                finally
                    samples.Value <- Starvation.Sampler.stop sampler
            }

        let reading: Starvation.Reading =
            { Cores = Environment.ProcessorCount
              Samples = samples.Value }

        return result, reportJson, reading
    }

/// The classes the run filtered `config`'s project to; `None` when it runs in full
/// or under a raw filter, whose classes are not known.
let internal selectionIn
    (rawFilter: string option)
    (affectedClassesByProject: Map<string, string list>)
    (config: TestConfig)
    : string list option =
    match rawFilter, Map.tryFind config.Project affectedClassesByProject with
    | None, Some(_ :: _ as classes) -> Some classes
    | _ -> None

/// Fold one group's results into the run's collector and, after the fold, emit them as progress.
let internal foldAndEmitInto
    (ctx: PluginCtx<'msg> option)
    (collected: RunCollector)
    (runId: Guid)
    (groupOutput: (string * TestResult) list)
    =
    collected.UpdateThen(
        (fun c ->
            { c with
                Results = groupOutput |> List.fold (fun acc (k, v) -> Map.add k v acc) c.Results }),
        fun () ->
            ctx
            |> Option.iter (fun c ->
                c.EmitTestProgress
                    { RunId = runId
                      NewResults = Map.ofList groupOutput })
    )
