/// One test project's launch: its arguments, trace decision, process run and result, over a ProjectLaunchEnv.
module FsHotWatch.TestPrune.ProjectLaunch

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

/// A project's launch arguments (`launchArgsOf`): its filter, coverage and CTRF switches.
type internal ProjectLaunchArgs =
    { ExtraArgs: ResizeArray<string>
      WasFiltered: bool
      ProjectCoverageLaunch: CoverageArtifactLaunch option
      CtrfPath: string option
      FinalArgs: string }

/// The arguments one project launches with: the class filter the selection names, the
/// coverage collector, the CTRF report switch and xUnit's long-running report, appended to
/// its configured args.
let internal launchArgsOf
    (repoRoot: string)
    (runDir: string)
    (affectedClassesByProject: Map<string, string list>)
    (excludedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    (coveragePathsByProject: Map<string, CoveragePaths>)
    (config: TestConfig)
    : ProjectLaunchArgs =
    let extraArgs = ResizeArray<string>()

    // A project the selection runs whole may still leave out its
    // `checkSkipClasses`; a class filter, when there is one, already names
    // every class that runs.
    let filterArgs =
        buildFilterArgs config affectedClassesByProject
        |> Option.orElseWith (fun () ->
            excludedClassesByProject
            |> Map.tryFind config.Project
            |> Option.bind (buildExcludeArgs config))

    match filterArgs with
    | Some f -> extraArgs.Add(f)
    | None -> ()

    // Raw passthrough filter (from run-tests command)
    match rawFilter with
    | Some f -> extraArgs.Add(f)
    | None -> ()

    let wasFiltered = Option.isSome filterArgs || Option.isSome rawFilter

    // Resolve per-project coverage paths (if coverage is configured for
    // this project). wasFiltered determines which file coverlet writes
    // to; the post-test step reads those files back to produce cobertura.
    let projectCoveragePaths = Map.tryFind config.Project coveragePathsByProject

    let projectCoverageLaunch =
        projectCoveragePaths
        |> Option.map (fun paths ->
            let rawPath = if wasFiltered then paths.Partial else paths.Baseline

            let launch =
                prepareCoverageArtifact config.Project paths.IncludeInRatchet wasFiltered rawPath

            extraArgs.Add(buildCoverageArgs paths wasFiltered)
            launch)

    // xUnit's runner supports a major-specific CTRF switch, which fshw
    // reads back as the AUTHORITATIVE pass/fail verdict (and for
    // flakiness history). An UNSUPPORTED `--report-*` flag is
    // FATAL (the runner exits "invalid command line" and runs
    // zero tests), so injection is scoped: `Disabled` never
    // injects, `Ctrf` always does, `AutoDetect` injects iff the
    // runner's restored package graph resolves to a supported xUnit
    // major. Forced `Ctrf` keeps the xUnit 3 switches as its fallback
    // for explicitly-configured custom runners, but uses the resolved
    // xUnit 4 switches whenever the project graph identifies v4.
    let ctrfRunnerFamily =
        match config.ReportVerificationFormat with
        | Disabled -> None
        | Ctrf -> detectCtrfRunnerFamily config.Args repoRoot |> Option.orElse (Some Xunit3)
        | AutoDetect -> detectCtrfRunnerFamily config.Args repoRoot

    // ONE DIRECTORY PER RUN. A run's reports live in
    // `.fshw/test-runs/<runId>/` and nothing else does, so membership is
    // a fact about where a file IS, never an inference from its mtime.
    //
    // The run-dir is created whether or not any project reports, so an
    // executed run that produced nothing leaves an EMPTY DIRECTORY —
    // distinguishable from a run that never happened.
    let ctrfPath =
        match ctrfRunnerFamily with
        | Some family ->
            Directory.CreateDirectory(runDir) |> ignore
            // The dir already names the run, so the file need only name
            // the project. No guid to guess at, nothing to parse.
            let ctrfName = $"{config.Project}{Ctrf.ReportSuffix}"

            extraArgs.Add(ctrfArguments family ctrfName runDir)

            // A result line per test on the console, so a host killed before it
            // writes this report still leaves its classes in the profile.
            detailedOutputArgument config.Args |> Option.iter extraArgs.Add

            // xUnit's report of a test still running past a threshold, so a host
            // killed at its timeout still names the test it was running. Its config
            // is the project's own with diagnostic messages on; a project whose
            // config cannot be found or read gets nothing injected. Only for a project
            // that asks (`nameHungTests`): xUnit's monitor holds a pool thread for the
            // whole run, which a diagnostic must not impose (AUTOMATION-1353).
            let ownConfig =
                if config.NameHungTests then
                    projectTestConfigFor config.Args repoRoot
                else
                    None

            match ownConfig with
            | Some own ->
                match diagnosticsTestConfig own with
                | Ok text ->
                    let configPath = Path.Combine(runDir, $"{config.Project}.testconfig.json")

                    match longRunningArguments config.Args configPath (longRunningSeconds config.TimeoutSec) with
                    | Some args ->
                        File.WriteAllText(configPath, text)
                        extraArgs.Add args
                    | None -> ()
                | Error why ->
                    Logging.info
                        "test-prune"
                        $"%s{config.Project}: not asking xUnit for long-running reports — its testconfig.json %s{why}"
            | None -> ()

            Some(Path.Combine(runDir, ctrfName))
        | None -> None

    let finalArgs =
        if extraArgs.Count > 0 then
            let extra = String.concat " " extraArgs
            $"%s{config.Args} %s{extra}"
        else
            config.Args

    { ExtraArgs = extraArgs
      WasFiltered = wasFiltered
      ProjectCoverageLaunch = projectCoverageLaunch
      CtrfPath = ctrfPath
      FinalArgs = finalArgs }

/// One project's finished launch, as `projectResultOf` reads it.
type internal FinishedLaunch =
    { Config: TestConfig
      RunDir: string
      Traces: TraceRunHost option
      Collected: RunCollector
      LogToCtx: string -> unit
      ProcessResult: ProcessOutcome
      DetectApphostMissing: ProcessOutcome -> bool
      ProjectSw: Stopwatch
      Profile: Profile.Recorder
      RunLog: RunLog.Handle
      TraceDecision: TraceDecision ref
      WasFiltered: bool
      CtrfPath: string option
      ProjectCoverageLaunch: CoverageArtifactLaunch option }

/// Interpret one project's finished launch and report it: its result from the process
/// outcome and CTRF report, the console and log lines, the collected report and trace.
let internal projectResultOf (launched: FinishedLaunch) : TestResult * string option =
    let config = launched.Config
    let runDir = launched.RunDir
    let traces = launched.Traces
    let collected = launched.Collected
    let logToCtx = launched.LogToCtx
    let processResult = launched.ProcessResult
    let detectApphostMissing = launched.DetectApphostMissing
    let projectSw = launched.ProjectSw
    let profile = launched.Profile
    let runLog = launched.RunLog
    let traceDecision = launched.TraceDecision
    let wasFiltered = launched.WasFiltered
    let ctrfPath = launched.CtrfPath
    let projectCoverageLaunch = launched.ProjectCoverageLaunch

    let projectElapsed = projectSw.Elapsed

    let apphostMissing = detectApphostMissing processResult

    // A filtered run that matched zero tests in this project is
    // not a failure (see `isZeroTestsUnderFilter`) — treat it
    // like an impact-skip.
    let zeroTestsUnderFilter = isZeroTestsUnderFilter wasFiltered processResult

    let output = outputOf processResult

    // Read the structured report ONCE (when one was requested
    // from a capable runner). It is BOTH the authoritative
    // pass/fail signal (summary counts) AND the flakiness
    // source (per-test records). Read BEFORE the verdict so
    // the REPORT — not the exit code — decides green/red.
    let reportJson =
        match ctrfPath with
        | Some p ->
            try
                Some(File.ReadAllText p)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None
        | None -> None

    let reportEvidence =
        match ctrfPath, reportJson with
        | None, _ -> NoReportRequested
        | Some _, Some json -> ReportRequested(Ctrf.tryVerdictReport json)
        | Some p, None -> ReportRequested(Error $"no readable report at %s{p}")

    let result =
        if apphostMissing then
            // Tests NEVER RAN — the apphost wasn't produced.
            // A dedicated `TestsDeferred`, NOT a pass
            // (`verdict` = `NothingVerified` → never a silent
            // false-green) and NOT a real failure: surfaced as an honest
            // "waiting on build" diagnostic. Carries no
            // elapsed/wasFiltered, so it never lowers a
            // coverage baseline.
            TestsDeferred "apphost not produced; tests did not run"
        elif zeroTestsUnderFilter then
            // Not a failure — per project, a filter selecting
            // nothing is not that project's fault, so it is never
            // reported as one. Its own case (rather than a passing
            // result wearing a marker prefix) so that every fold
            // must decide it; see `TestResult.verdict` and
            // `verificationOf`.
            TestsNoMatch(output, projectElapsed)
        else
            classifyTestOutcome reportEvidence wasFiltered projectElapsed processResult

    Profile.finish
        profile
        (Profile.outcomeOf result)
        (match ctrfPath, reportJson with
         | Some _, Some json -> Ok(Profile.classTimings json)
         | None, _ -> Error "no report: this runner was not asked for CTRF"
         | Some _, None ->
             match result with
             | TestsTimedOut(_, after, _, _) ->
                 Error $"no report: the host was killed at its %d{int after.TotalSeconds}s timeout"
             | _ -> Error "no report: the host wrote none")

    // Log driven off the AUTHORITATIVE verdict (not the raw
    // exit code) so the console line can never disagree with
    // the recorded result.
    match result with
    | TestsDeferred _ ->
        logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

        Logging.warn
            "test-prune"
            $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (a build-ordering issue, never a test failure)"
    | TestsNoMatch _ ->
        logToCtx $"{config.Project}: no tests matched the filter — skipped"

        Logging.info
            "test-prune"
            $"%s{config.Project}: no tests matched the active filter — skipped, not FAILED (a filtered run that selects nothing here is not a test failure)"
    | TestsPassed _ ->
        logToCtx $"{config.Project}: passed"
        Logging.info "test-prune" $"%s{config.Project}: PASSED"
    | TestsErrored reason ->
        logToCtx $"{config.Project}: ABORTED (nothing verified) — {reason}"

        Logging.error
            "test-prune"
            $"%s{config.Project}: ABORTED — %s{reason} Nothing was verified; this is NOT a test failure and NOT a pass — re-run (e.g. `dotnet fshw test-rerun`)."
    | TestsFailed _ ->
        logToCtx $"{config.Project}: failed"
        Logging.error "test-prune" $"%s{config.Project}: FAILED"
    | TestsTimedOut(_, after, _, _) ->
        logToCtx $"{config.Project}: timed out (infrastructure)"

        Logging.error "test-prune" $"%s{config.Project}: TIMED OUT (infrastructure) after %d{int after.TotalSeconds}s"

    // Report the outcome in full. The two reports are DIFFERENT
    // documents on purpose: a failure report
    // counts the runner's `failed ...` lines and calls them
    // failures, which is true of a run that finished and false —
    // expensively, alarmingly false — of one that was killed
    // holding a half-written transcript.
    match result with
    | TestsFailed _
    | TestsTimedOut _ ->
        let ctrfFailedRows =
            reportJson |> Option.map failedRowsOfReport |> Option.defaultValue []

        for line in formatFailureReport (RedRunEnding.ofResult result) ctrfFailedRows config.Project runLog.Ref output do
            Logging.error "test-prune" line
    | TestsErrored reason ->
        for line in formatAbortReport config.Project runLog.Ref reason output do
            Logging.error "test-prune" line
    | TestsPassed _
    | TestsDeferred _
    | TestsNoMatch _ -> ()

    projectCoverageLaunch |> Option.iter (keepRunCoverage runDir)

    // Collect this project's raw runner cobertura for SERIAL
    // ingest after Async.Parallel (a parallel DB write +
    // shared-file write would race). A run that never executed
    // (apphost missing) contributes NO input, so a partial file
    // cannot lower coverage.
    match
        projectCoverageLaunch
        |> Option.map (coverageReceiptFromReceipt (TestResult.verifiedGreen result))
    with
    | Some(CoverageReceiptAccepted input) ->
        collected.Update(fun c ->
            { c with
                CoverageInputs = input :: c.CoverageInputs })
    | Some(CoverageReceiptFailed failure) ->
        collected.Update(fun c ->
            { c with
                CoverageFailures = failure :: c.CoverageFailures })
    | Some CoverageReceiptAbsent
    | None -> ()

    // Per-test flakiness tracking: reuse the report content
    // already read for the verdict; COLLECT this project's
    // per-test records for the single post-parallel write (see
    // `flakinessRecords`). Best-effort — exceptions never fail
    // the run.
    //
    // The report is RETAINED — the verdict file POINTS at these
    // reports rather than deleting them once their records are
    // folded into the flakiness history. `Ctrf.tidyRunsDir`
    // (post-run) keeps the newest few per project, so retention
    // stays bounded.
    match ctrfPath, reportJson with
    | Some _, Some json ->
        try
            let records = Flakiness.parseCtrfTests json

            if not records.IsEmpty then
                collected.Update(fun c ->
                    { c with
                        Flakiness = c.Flakiness @ records })
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"
    | Some p, None ->
        // Report requested but unreadable (missing — the host
        // aborted before flushing — or locked). Nothing to retain;
        // its ABSENCE already drove the Errored verdict.
        try
            File.Delete p
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()
    | None, _ -> ()

    if Option.isSome traces then
        let traced: TracedProjectRun =
            { Project = config.Project
              Decision = traceDecision.Value
              Filtered = wasFiltered
              CtrfPath = ctrfPath }

        collected.Update(fun c ->
            { c with
                TraceRuns = traced :: c.TraceRuns })

    result, reportJson

/// Whether one project launches traced: decided after the preflight certified the build
/// output fresh and before the spawn, and logged. A refused project launches as configured.
let internal decideTracing
    (traces: TraceRunHost option)
    (repoRoot: string)
    (runDir: string)
    (runToken: Threading.CancellationToken)
    (config: TestConfig)
    (ctrfPath: string option)
    (extraArgs: ResizeArray<string>)
    : TraceDecision =
    // TRACES. Decided here, after the preflight certified the build
    // output fresh and before the spawn: a traced project launches its
    // woven copy; a refused one launches exactly as configured, and the
    // refusal is stored with its reason after the run.
    let decided =
        match traces with
        | None -> Untraced None
        | Some host ->
            // Weaving and JIT verification can take minutes: held as a
            // subtask, so a wait line names it rather than the run.
            use _ =
                host.HoldSubtask $"{config.Project}:traces" $"preparing traces for {config.Project}"

            host.TraceWiring.Decide
                runToken
                host.TraceRuntime
                { Project = config.Project
                  Command = config.Command
                  Args = config.Args
                  Environment = config.Environment
                  Target = deriveProjectBin config.Args repoRoot
                  CtrfPath = ctrfPath
                  TraceWeave = host.TraceWiring.WeaveOverrides.TryFind config.Project }
                runDir
                (List.ofSeq extraArgs)

    match decided with
    | Untraced(Some reason) ->
        Logging.warn "test-prune" $"%s{config.Project}: not tracing this run, launching as configured — %s{reason}"
    | Traced(spec, _) -> Logging.info "test-prune" $"%s{config.Project}: tracing, launching %s{spec.Command}"
    | Untraced None -> ()

    decided

/// Whether a failed launch found its apphost missing: from the project's own bin when it
/// can be derived (`tryApphostPresent`), else from the output (`looksLikeApphostMissing`).
let internal apphostMissingIn (repoRoot: string) (config: TestConfig) (outcome: ProcessOutcome) : bool =
    // A clean exit means the apphost ran — never a
    // launch-ordering problem, regardless of artifacts.
    if isSucceeded outcome then
        false
    else
        match tryApphostPresent config.Args repoRoot with
        | Some present -> not present
        | None ->
            // Not derivable — fall back to the text sniff.
            match outcome with
            | ProcessOutcome.Failed(_, out) -> looksLikeApphostMissing (ProcessOutput.text out)
            | _ -> false

/// What one project's launch reads from its run (`launchProjectWith`).
type internal ProjectLaunchEnv<'msg> =
    { Ctx: PluginCtx<'msg> option
      RepoRoot: string
      RunDir: string
      LaunchDeadline: TimeSpan
      Traces: TraceRunHost option
      Collected: RunCollector
      LogToCtx: string -> unit
      AffectedClassesByProject: Map<string, string list>
      ExcludedClassesByProject: Map<string, string list>
      RawFilter: string option
      CoveragePathsByProject: Map<string, CoveragePaths> }

/// Launch one project as configured and classify what it did: the per-project body
/// of the run, shared by the first launch and a starvation re-run. Returns the
/// project's result and its CTRF report text (when one was requested and read).
let internal launchProjectWith
    (env: ProjectLaunchEnv<'msg>)
    (runToken: Threading.CancellationToken)
    (config: TestConfig)
    : Async<TestResult * string option> =
    let ctx = env.Ctx
    let repoRoot = env.RepoRoot
    let runDir = env.RunDir
    let launchDeadline = env.LaunchDeadline
    let traces = env.Traces
    let collected = env.Collected
    let logToCtx = env.LogToCtx
    let affectedClassesByProject = env.AffectedClassesByProject
    let excludedClassesByProject = env.ExcludedClassesByProject
    let rawFilter = env.RawFilter
    let coveragePathsByProject = env.CoveragePathsByProject

    async {
        // Collect extra args (filter + coverage) to append
        let launchArgs =
            launchArgsOf
                repoRoot
                runDir
                affectedClassesByProject
                excludedClassesByProject
                rawFilter
                coveragePathsByProject
                config

        let extraArgs = launchArgs.ExtraArgs
        let wasFiltered = launchArgs.WasFiltered
        let projectCoverageLaunch = launchArgs.ProjectCoverageLaunch
        let ctrfPath = launchArgs.CtrfPath
        let finalArgs = launchArgs.FinalArgs

        let decided =
            decideTracing traces repoRoot runDir runToken config ctrfPath extraArgs

        // A ref: the untraced retry below replaces it from inside `runOnce`.
        let traceDecision = ref decided

        Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

        let timeoutSpan =
            match config.TimeoutSec with
            | Some s -> TimeSpan.FromSeconds(float s)
            | None -> System.Threading.Timeout.InfiniteTimeSpan

        let projectSw = Stopwatch.StartNew()

        // THE RUN LOG. Opened for EVERY project on every
        // run, before the spawn — which project will need explaining is not
        // knowable in advance and the artifact costs a file handle.
        //
        // STREAMED, not buffered (see `RunLog`): the failure that needs it
        // most is the suite SIGKILLed at its timeout, which reaches no
        // writer at all and whose in-memory capture the kill truncates.
        let runLog = RunLog.openFor runDir config.Project

        match runLog.Ref with
        | RunLog.Ref.Written path -> Logging.info "test-prune" $"%s{config.Project}: streaming run output to %s{path}"
        | RunLog.Ref.Unavailable reason ->
            Logging.warn
                "test-prune"
                $"%s{config.Project}: NOT saving a run log — %s{reason}. The run proceeds; only \
              the console tail will be available if it fails."

        // THE PROFILE (AUTOMATION-1300). Every project, every run, streamed beside
        // the run log: the load and the CPU time of the host's tree while it runs, its wall
        // time, and each class's span and the load over it — from the report, or, for a host
        // killed at its timeout (which writes none), from the result lines it printed.
        // This record is fshw's, so it survives the kill. See `Profile`.
        // The host's tree, read at each sample: its CPU time, and at the end its child
        // processes by command.
        let processTree = Profile.processTree ()

        let profile =
            Profile.start
                runDir
                config.Project
                config.TimeoutSec
                Environment.ProcessorCount
                OperationWatchdog.loadAverage
                processTree.Cpu
                Profile.DefaultInterval

        Profile.trackChildren profile processTree.Children

        // Every path out of this launch closes the profile and stops its sampler:
        // the classified outcome below finishes it first (`finish` writes once), so
        // this lands only on a path that never classified one — a cancelled run, or
        // an exception between the spawn and the verdict.
        use _profileClosed =
            { new IDisposable with
                member _.Dispose() =
                    Profile.finish
                        profile
                        "abandoned"
                        (Error "no report: the run ended before this project's outcome was classified") }

        // The output goes to the run log and, chunk by chunk, to the profile, which
        // keeps the per-test result lines a killed host's classes are read from. The
        // profile first: it never throws, and a throwing sink is disabled whole.
        let outputSink =
            match runLog.Ref with
            | RunLog.Ref.Written _ ->
                Some(fun chunk ->
                    Profile.observe profile chunk
                    runLog.Write chunk)
            | RunLog.Ref.Unavailable _ -> Some(Profile.observe profile)

        // A test runner STREAMS (discovery banner, progress, per-test
        // lines), so its first byte is a sound liveness proof and the
        // launch deadline can bound the spawn even when the config sets
        // no TimeoutSec at all.
        //
        // A host that overruns is asked to stop before it is killed
        // (`TestHostStop`); the teardown is kept for the timeout marker.
        let spawnedAt = ref DateTime.UtcNow
        let lastTeardown: TreeTeardown option ref = ref None

        let launch (decision: TraceDecision) =
            async {
                let command, args, environment =
                    TraceRun.launchOf decision (config.Command, finalArgs, config.Environment)

                spawnedAt.Value <- DateTime.UtcNow

                let outcome, teardown =
                    runProcessToStopping
                        TestHostStop
                        outputSink
                        (Profile.spawned profile)
                        command
                        args
                        repoRoot
                        environment
                        (ProcessBounds.streaming timeoutSpan launchDeadline)

                lastTeardown.Value <- teardown
                return outcome
            }

        let runOnce = async { return! launch traceDecision.Value }

        // A traced launch that verified nothing is repeated untraced,
        // unless it timed out (see `TraceRun.untracedRetry`): tracing
        // cannot turn a crash red, and a timeout is not run twice.
        let runOnceTracedOrNot =
            async {
                let announce followUp =
                    let reason, seam =
                        match followUp with
                        | RelaunchUntraced reason ->
                            reason,
                            "the traced launch failed without writing a test report; relaunching \
                             untraced. Everything above is the TRACED attempt, everything below the \
                             untraced one."
                        | KeepTimeout reason ->
                            reason,
                            "the traced launch was killed at the project timeout; not relaunching \
                             untraced, so the timeout is this project's outcome. `\"traces\": false` \
                             on the project runs it untraced."

                    Logging.warn "test-prune" $"%s{config.Project}: %s{reason}"
                    RunLog.note runLog seam

                let! outcome, decision =
                    TraceRun.launchTracedOrNot
                        traceDecision.Value
                        (fun () -> ctrfPath |> Option.exists File.Exists)
                        announce
                        launch

                traceDecision.Value <- decision
                return outcome
            }

        let detectApphostMissing = apphostMissingIn repoRoot config

        // Cold-start apphost-missing retry. The BuildCompleted→TestPrune
        // ordering already gates the launch on a successful build, but a
        // narrow race can still fire `--no-build` before the apphost
        // lands. Retry ONCE after a short wait; a still-missing apphost
        // is DEFERRED ("waiting on build"), never FAILED.
        let runTestWithRetry =
            async {
                let! first = runOnceTracedOrNot

                if detectApphostMissing first then
                    Logging.warn
                        "test-prune"
                        $"%s{config.Project}: apphost missing at launch (build not settled yet); retrying once after a short wait"

                    // Both attempts stream into the SAME log, so mark the
                    // seam — otherwise two runs read as one confusing run.
                    RunLog.note
                        runLog
                        "apphost missing at launch; relaunching once. Everything above is the \
                     FIRST attempt, everything below the second."

                    do! Async.Sleep 750
                    let! second = runOnce
                    return second
                else
                    return first
            }

        // `finally`, not a close after the bind: a launch stall RE-RAISES
        // out of this block, and a run log whose handle leaked on the one
        // path where the child never came back is a log of nothing.
        let! processResult =
            async {
                try
                    try
                        let! outcome =
                            match ctx with
                            | Some c ->
                                PluginCtxHelpers.withSubtask
                                    c
                                    config.Project
                                    $"testing {config.Project}"
                                    runTestWithRetry
                            | None -> runTestWithRetry

                        // A host torn down at its timeout wrote no report and its
                        // log ends wherever the kill caught it: say why, in the
                        // log and beside it (`TimeoutMarker`).
                        match outcome with
                        | ProcessOutcome.TimedOut(after, _, kill) ->
                            let facts: TimeoutMarker.Facts =
                                { Project = config.Project
                                  After = after
                                  SpawnedAt = spawnedAt.Value
                                  Kill = kill
                                  Teardown = lastTeardown.Value }

                            RunLog.note runLog (TimeoutMarker.message facts)
                            TimeoutMarker.write runDir facts
                        | _ -> ()

                        return outcome
                    with LaunchStalledException reason ->
                        // The watchdog killed a child that never showed a
                        // sign of life within the launch deadline. Re-raise
                        // NAMING the config and elapsed, for `launchProject`
                        // to record as this project's `TestsErrored` result: a
                        // launch stall means this project NEVER RAN, so it is
                        // never a pass and `check` exits non-green rather than
                        // wedging at Running, while the rest of the run keeps
                        // what it verified. A child
                        // that EXITS is not a stall — the poll observes the
                        // exit and classifies it normally.
                        Profile.finish profile "launch-stalled" (Error $"no report: %s{reason}")

                        return
                            raise (
                                LaunchStalledException
                                    $"%s{config.Project}: %s{reason} (after %.0f{projectSw.Elapsed.TotalSeconds}s)"
                            )
                finally
                    // The pumps are done by the time `runProcessTo`
                    // returns (it drains them), so nothing is still
                    // writing when this closes.
                    runLog.Close()
            }

        projectSw.Stop()

        return
            projectResultOf
                { Config = config
                  RunDir = runDir
                  Traces = traces
                  Collected = collected
                  LogToCtx = logToCtx
                  ProcessResult = processResult
                  DetectApphostMissing = detectApphostMissing
                  ProjectSw = projectSw
                  Profile = profile
                  RunLog = runLog
                  TraceDecision = traceDecision
                  WasFiltered = wasFiltered
                  CtrfPath = ctrfPath
                  ProjectCoverageLaunch = projectCoverageLaunch }
    }
