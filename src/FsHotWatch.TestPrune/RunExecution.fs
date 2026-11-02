/// Executing one test run: launching each configured project, collecting what it reports, and ingesting coverage, flakiness and traces once every project has finished.
module FsHotWatch.TestPrune.RunExecution

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

/// A finished run's coverage, ingested: invalidate what failed its receipt, ingest and emit
/// the cobertura, and report every failure as that project's debt.
let internal ingestRunCoverage
    (collected: RunCollector)
    (logToCtx: string -> unit)
    (runId: Guid)
    db
    repoRoot
    runDir
    enabledRatchetProjects
    configuredCoverageOutput
    coverageIngestFailed
    (coveragePathsByProject: Map<string, CoveragePaths>)
    (launchTree: ReceiptTree option)
    (reconciled: (TestEvidenceRun.Plan * TestEvidenceRun.Reconciled) option)
    (rerunProjects: Set<string>)
    reusedNow
    =
    // Coverage: now that ALL projects have finished, serially ingest each
    // project's raw runner cobertura into the TestPrune DB (max-merged,
    // symbol-relative) and emit the FULL DB ONCE to the single shared
    // cobertura file. Done here, outside Async.Parallel, so there is no
    // DB-write contention and no file-write race on the shared output.
    let collectedInputs, receiptFailures =
        collected.Read(fun c -> List.rev c.CoverageInputs, List.rev c.CoverageFailures)

    let receiptFailures =
        receiptFailures
        |> List.map (fun failure ->
            try
                invalidateCoverageReceiptFailure db failure
                failure
            with ex ->
                { failure with
                    Reason =
                        $"%s{failure.Reason}; prior coverage invalidation failed (%s{ex.GetType().Name}: %s{ex.Message})" })

    let coverageOutcome =
        ingestAndEmitCoverageForProjects
            db
            repoRoot
            (runId.ToString("N"))
            enabledRatchetProjects
            configuredCoverageOutput
            collectedInputs

    // Coverage missing (AUTOMATION-1371): the prior rows were kept; the run says so.
    for missing in coverageOutcome.CoverageMissing do
        logToCtx $"%s{missing.Project}: %s{missing.Reason}; its prior runtime coverage is kept"

    let coverageFailures = receiptFailures @ coverageIngestFailures coverageOutcome

    if not coverageFailures.IsEmpty then
        for failure in coverageFailures do
            coverageIngestFailed failure

        collected.Update(fun c ->
            { c with
                Results = applyCoverageIngestFailures coverageFailures c.Results })

    // TEST EVIDENCE: publish. Only now are this run's own results final — a project
    // whose coverage did not ingest is no longer green — so only now may a green leave
    // this checkout.
    reconciled
    |> Option.iter (fun (plan, r) ->
        let finalNow = collected.Read _.Results

        // A project re-run under starvation is not published: its green was earned on a
        // second attempt, which the stored evidence could not say.
        let runOf (candidate: TestEvidenceRun.Candidate) : TestEvidenceRun.ProducedRun option =
            Map.tryFind candidate.Project finalNow
            |> Option.filter (fun _ -> not (rerunProjects.Contains candidate.Project))
            |> Option.map (fun result ->
                let coveragePath = runCoveragePath runDir candidate.Project

                { Project = candidate.Project
                  Result = result
                  Report = TestEvidenceRun.readIfPresent (Path.Combine(runDir, candidate.Project + Ctrf.ReportSuffix))
                  CoverageRequired = coveragePathsByProject.ContainsKey candidate.Project
                  Coverage =
                    TestEvidenceRun.readIfPresent coveragePath
                    |> Option.map (fun text -> coveragePath, text) })

        let producer: TestEvidenceStore.Producer =
            { Worktree = Path.GetFullPath repoRoot
              RunId = runId.ToString("N")
              TreeHash = launchTree |> Option.map _.Identity |> Option.defaultValue "unbound"
              ProducerHash = plan.Wiring.ProducerHash
              EarnedAt = DateTimeOffset.UtcNow }

        let written = TestEvidenceRun.publish plan repoRoot r reusedNow runOf producer
        let tally = TestEvidenceRun.tally plan r written
        Logging.info "test-prune" tally
        logToCtx tally)

    ()

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

/// Execute test configs with optional affected classes for filtering. Handles beforeRun,
/// coveragePaths, process execution, result storage. `rawFilter` is a passthrough filter
/// string (from the run-tests command) that bypasses the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
///
/// `ctx = None` (a one-off command) fires no lifecycle events — the caller just gets the
/// final TestResults — and also disables the skip-on-stale shortcut, so a manual run is
/// never deadlocked by a stuck dirty bit. See the staleness branch below.
let internal executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (emitStarted: TestRunStarted -> unit)
    (repoRoot: string)
    (launchDeadline: TimeSpan)
    // `tests.maxParallelProjects`: at most this many groups in flight, longest-expected
    // first; `None` launches every group at once.
    (maxParallelProjects: int option)
    // This session's lane of its repository host's process scheduler: each group holds
    // one of its slots while it runs (AUTOMATION-682). `None` for a per-worktree daemon.
    (processes: FsHotWatch.WorkScheduler.Lane option)
    // `tests.traces`, resolved for this run's mode, with the plugin's activity log (which
    // a force run's `ctx = None` would otherwise lose); `None` when not configured, which
    // leaves every launch exactly as it is without traces.
    (traces: TraceRunHost option)
    // The input tree the run was launched against, for the traces it records and the
    // receipt's re-binding once `beforeRun` is done.
    (launchTree: ReceiptTree option)
    // Which repo-relative paths a build consumed, observed once `beforeRun` finishes:
    // decides whether the receipt may re-bind to the tree the tests actually run against.
    (feedsCompile: unit -> string -> bool)
    // Receives the run id so the hook's own timings can be filed
    // under this run, beside the CTRF reports the verdict already reads.
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    // Where each step `beforeRun` runs is held while it runs: the plugin's subtasks, on
    // every path, so a wait or wedge inside the setup names the step it is on.
    (trackHookStep: HookStep.Tracker)
    (coveragePaths: (string -> CoveragePaths option) option)
    (coverageIngestFailed: CoverageIngestFailure -> unit)
    (afterRun: (TestResults -> unit) option)
    (configuredCoverageProjects: TestConfig list)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    // `checkSkipClasses` a project run whole leaves out, through its exclude template
    // (`buildExcludeArgs`). Empty for every run that skips nothing.
    (excludedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    // The test-evidence plan made at launch (`TestEvidenceRun.plan`): the projects this
    // run reuses instead of launching, and every key it publishes against. `None` runs
    // every selected project exactly as it would without a store.
    (evidence: TestEvidenceRun.Plan option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        // The run's own directory. Created NOW — before anything runs — so that a run
        // which executes but reports nothing is distinguishable from a run that never
        // happened: the first leaves an empty directory, the second leaves none.
        let runDir = Ctrf.runDir repoRoot runId

        try
            Directory.CreateDirectory(runDir) |> ignore
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn "test-prune" $"could not create the run directory %s{runDir}: %s{ex.Message}"

        let skipsClasses =
            excludedClassesByProject
            |> Map.exists (fun _ classes -> not (List.isEmpty classes))

        let isFilteredRun =
            not affectedClassesByProject.IsEmpty || Option.isSome rawFilter || skipsClasses

        // Every configured project launched, none of them filtered: a full run whatever
        // mode launched it.
        let runsEveryProjectInFull =
            Option.isNone rawFilter
            && not skipsClasses
            && configuredCoverageProjects
               |> List.forall (fun configured ->
                   configs |> List.exists (fun c -> c.Project = configured.Project)
                   && (affectedClassesByProject.IsEmpty
                       || affectedClassesByProject
                          |> Map.tryFind configured.Project
                          |> Option.exists List.isEmpty))

        let traces =
            traces
            |> Option.map (fun host ->
                { host with
                    TraceRuntime =
                        { host.TraceRuntime with
                            RunsEveryProjectInFull = runsEveryProjectInFull } })

        // Said once per run, loudly: every project below then stores and logs the same
        // reason as its refusal.
        traces
        |> Option.bind (fun host -> TraceRun.unrecordedReason host.TraceRuntime)
        |> Option.iter (fun reason -> Logging.warn "test-prune" $"traces: this run records none — %s{reason}")

        let primaryLabel =
            if isFilteredRun then
                $"running %d{configs.Length} selected test projects"
            else
                $"running full suite (%d{configs.Length} projects)"

        let startedAt = DateTime.UtcNow

        ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
        // `TestRunStarted` is emitted by the CALLER (which receives `started` in the
        // returned tuple), so the synchronous `TestsFinished` handler can fire it inside
        // the cache-write capture window.
        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }
        emitStarted started

        // The tree this run's receipt binds to: re-read once `beforeRun` has done its
        // generating, so its own output does not count as an edit made mid-run.
        let boundTree =
            match beforeRun with
            | Some setup ->
                Logging.info "test-prune" "Running beforeRun setup..."
                setup runId trackHookStep
                Logging.info "test-prune" "beforeRun complete"

                let binding, why =
                    ReceiptInputTree.afterBeforeRun (feedsCompile ()) launchTree (ReceiptInputTree.readTree repoRoot)

                why |> Option.iter (Logging.info "test-prune")
                binding
            | None -> launchTree

        let testHostStartedAt = DateTime.UtcNow

        let groups = configs |> List.groupBy (fun c -> c.Group)

        let coveragePathsByProject =
            configuredCoverageProjects
            |> List.choose (fun config ->
                coveragePaths
                |> Option.bind (fun pathsForProject -> pathsForProject config.Project)
                |> Option.map (fun paths -> config.Project, paths))
            |> Map.ofList

        let enabledRatchetProjects =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.choose (fun (project, paths) -> if paths.IncludeInRatchet then Some project else None)
            |> Set.ofSeq

        let configuredCoverageOutput =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.tryHead
            |> Option.map (fun (_, paths) -> paths.Cobertura)

        // Impact analysis may have decided a project has no affected classes. Such a
        // project never launches, so it is neither preflighted (no artifacts worth
        // walking) nor deferred (nothing was going to run).
        let skipProjectOf (config: TestConfig) =
            not affectedClassesByProject.IsEmpty
            && not (affectedClassesByProject |> Map.containsKey config.Project)

        /// What an impact-skipped project reports. Skipped-due-to-impact-analysis is the
        /// strongest form of filtering — hence `wasFiltered = true`, so no caller mistakes
        /// it for a full run — and its coverage contribution is "nothing new". Elapsed is
        /// Zero because the test runner never started.
        ///
        /// Beside `skipProjectOf` rather than at either call site: the refusal path and the
        /// run loop both answer for skipped projects, and they have to answer the same way.
        let skippedResultOf (config: TestConfig) =
            config.Project, TestsPassed("", true, TimeSpan.Zero)

        // What the parallel project launches collect, under one lock (`RunCollector`): the
        // run-wide results, and the coverage, flakiness, trace, evidence and starvation
        // records that are used SERIALLY once the parallel section ends.
        let collected = RunCollector()

        /// Write a line to the plugin's activity log when there is a host to write to.
        /// One binding for the whole run: the preflight, the refusal path and the per-
        /// config runner all report through it, and it is allocated once rather than
        /// per config.
        let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

        let foldAndEmit (groupOutput: (string * TestResult) list) =
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

        // STALE-ARTIFACT PREFLIGHT. The freshness question is pure
        // file I/O and is answerable in seconds, so it is asked about EVERY config
        // HERE — before a single suite launches — rather than inside the parallel
        // per-config body, where a group-A project wrote its CTRF before group B was
        // examined and the refusal surfaced three minutes into a partial run that
        // read like progress.
        //
        // The preflight also REPAIRS what is provably repairable (a build-output copy
        // whose origin exists on disk), re-verifies the bytes afterwards, and records
        // every repair to a durable ledger that trips a circuit breaker on repetition.
        // See `StaleArtifactPreflight` for why exactly one stale case is healed.
        // TEST EVIDENCE. A project the plan reuses is not launched: its stored CTRF report
        // is filed in this run's directory, its stored coverage where the run would have
        // written it, and it reports a green that names where it was earned. A project
        // whose coverage the gate grades is reused only when the entry carries it.
        let reusable: Map<string, TestEvidenceStore.Entry> =
            match evidence with
            | None -> Map.empty
            | Some plan ->
                plan.Reused
                |> Map.filter (fun project entry ->
                    configs |> List.exists (fun c -> c.Project = project && not (skipProjectOf c))
                    && (not (coveragePathsByProject.ContainsKey project) || Option.isSome entry.Coverage))


        /// File `entry` as this run's evidence for `config`; `false` when it could not be
        /// filed, and then the project is launched like any other. A filed project reports
        /// NOTHING until the completion re-check has confirmed it (`TestEvidenceRun.reconcile`):
        /// no progress subscriber may see a green the re-check could still withdraw.
        let tryReuse (config: TestConfig) : bool =
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

        // ALL-OR-NOTHING on a refusal, as two named alternatives. A run whose tree is
        // provably not built cannot reach a green verdict, so launching the projects that
        // happen to be fresh buys minutes of partial execution for signal the verdict
        // cannot use — which is the "reads like progress" half of the defect.
        //
        // ADR-016 kept this and changed the layer below it: the refusal
        // is still run-wide, but the preflight now repairs every copy its breaker did not
        // name, so a refused run leaves a better tree than it found and the refusal set
        // shrinks run over run. `preflight.Healed` above can therefore be non-empty on
        // this path — repairs happened, the launch did not.
        //
        // Both arms are bound as functions rather than inlined into the `if`, so the
        // 390-line group loop keeps the indentation it has always had. A run-level
        // guard should cost one line here, not re-flow every line it guards.

        /// Nothing spawns. Every configured project comes back deferred, naming its own
        /// reason where the preflight found one and the run-wide cause where it did not.
        let refuseWholeRun () =
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

        // `tests.maxParallelProjects`: with a cap, groups launch longest expected wall first
        // (from the retained CTRF reports), so the long ones are not left for last. Without
        // one every group launches at once and the order is moot, so no history is read.
        let scheduledGroups =
            match maxParallelProjects with
            | None -> groups
            | Some cap ->
                let ordered =
                    GroupSchedule.longestFirst
                        (fun (_, groupConfigs: TestConfig list) -> groupConfigs |> List.map (fun c -> c.Project))
                        (GroupSchedule.expectedWalls repoRoot)
                        groups

                let order = ordered |> List.map fst |> String.concat ", "
                Logging.info "test-prune" $"launching at most %d{cap} test group(s) at once, in order: %s{order}"
                ordered

        let projectLaunchEnv: ProjectLaunchEnv<'msg> =
            { Ctx = ctx
              RepoRoot = repoRoot
              RunDir = runDir
              LaunchDeadline = launchDeadline
              Traces = traces
              Collected = collected
              LogToCtx = logToCtx
              AffectedClassesByProject = affectedClassesByProject
              ExcludedClassesByProject = excludedClassesByProject
              RawFilter = rawFilter
              CoveragePathsByProject = coveragePathsByProject }

        let launchProjectOrRaise runToken config =
            launchProjectWith projectLaunchEnv runToken config

        // STARVATION. A project that declared `starvation` is sampled while it runs; a
        // timeout on a starved box is held back from the group's results and re-run alone
        // once every group has finished (`rerunStarved`). See `Starvation`.

        /// `launchProjectOrRaise`, with a launch stall answered as THIS project's result.
        ///
        /// A stall ("launch produced no live process within Ns") means this project never
        /// ran. It used to raise out of the whole run, which then completed `Aborted` with
        /// no results at all: the projects that had run, or were still running, lost what
        /// they verified, and the next run ran every one of them in full again. Now the
        /// project is `TestsErrored` — nothing verified, never a pass, so the run is still
        /// not green — and every other project keeps its own result.
        let launchProject
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

        /// The classes the run filtered `config`'s project to; `None` when it runs in full
        /// or under a raw filter, whose classes are not known.
        let selectionOf (config: TestConfig) : string list option =
            match rawFilter, Map.tryFind config.Project affectedClassesByProject with
            | None, Some(_ :: _ as classes) -> Some classes
            | _ -> None

        /// `launchProject`, with the load sampled while it runs.
        let launchSampled (runToken: Threading.CancellationToken) (config: TestConfig) =
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

        /// Launch `config`'s project; `None` when it timed out under starvation and is
        /// held back for its re-run alone.
        let launchWatched (runToken: Threading.CancellationToken) (config: TestConfig) : Async<TestResult option> =
            async {
                match config.Starvation with
                | None ->
                    let! result, _ = launchProject runToken config
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
                            Logging.info "test-prune" $"%s{config.Project}: starvation declared; %s{reason}")

                        return Some result
            }

        let rerunEnv: RerunEnv =
            { RepoRoot = repoRoot
              RunDir = runDir
              Collected = collected
              LogToCtx = logToCtx
              LaunchSampled = launchSampled }

        let rerunStarved = rerunStarvedWith rerunEnv

        /// The normal path: every target certified fresh, so the groups launch in
        /// parallel (at most `maxParallelProjects` at a time) and fold their results into
        /// the shared accumulator as they finish, in whatever order that is.
        let runAllGroups () =
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

        let! groupResults =
            if List.isEmpty preflight.Refusals then
                runAllGroups ()
            else
                refuseWholeRun ()

        // groupResults is the per-group return values; we ignore it because
        // `collected.Results` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore

        // STARVATION: the held-back projects, re-run alone now that every group is done.
        let! rerunResults = rerunStarved ()

        if not rerunResults.IsEmpty then
            foldAndEmit rerunResults

        let rerunProjects = rerunResults |> List.map fst |> Set.ofList

        // TEST EVIDENCE, at completion. One fresh walk keys every candidate again. A reuse
        // whose key moved while the run was in flight no longer describes this tree: its
        // filed report and coverage are withdrawn and it reports a deferral, so this run
        // cannot go green on it and the next one launches it. A project that really ran
        // publishes its green only when it qualifies (`TestEvidenceRun.qualifies`).
        let reusedNow = collected.Read _.Materialized

        let reconciled =
            evidence
            |> Option.map (fun plan -> plan, TestEvidenceRun.reconcile plan repoRoot reusedNow)

        // A withdrawn reuse takes back everything it filed and reports a deferral; a held one
        // reports its green now, and only now.
        reconciled
        |> Option.iter (fun (_, r) ->
            for (project, reason) in r.Withdrawn do
                Logging.warn "test-prune" $"%s{project}: %s{reason}"
                logToCtx $"{project}: {reason}"
                TestEvidenceRun.deleteQuietly (Path.Combine(runDir, project + Ctrf.ReportSuffix))

                collected.Update(fun c ->
                    { c with
                        CoverageInputs = c.CoverageInputs |> List.filter (fun i -> i.Project <> project) })

            let reported =
                (r.Withdrawn |> List.map (fun (project, reason) -> project, TestsDeferred reason))
                @ (r.Held
                   |> Map.toList
                   |> List.map (fun (project, entry) ->
                       project,
                       TestsPassed(
                           TestEvidenceRun.Provenance.describe (TestEvidenceRun.Provenance.ofEntry entry),
                           false,
                           TimeSpan.Zero
                       )))

            collected.Update(fun c ->
                { c with
                    Results =
                        reported
                        |> List.fold (fun acc (project, result) -> Map.add project result acc) c.Results }))

        let evidenceHeld: Map<string, TestEvidenceRun.Provenance> =
            reconciled
            |> Option.map (fun (_, r) -> r.Held |> Map.map (fun _ entry -> TestEvidenceRun.Provenance.ofEntry entry))
            |> Option.defaultValue Map.empty

        ingestRunCoverage
            collected
            logToCtx
            runId
            db
            repoRoot
            runDir
            enabledRatchetProjects
            configuredCoverageOutput
            coverageIngestFailed
            coveragePathsByProject
            launchTree
            reconciled
            rerunProjects
            reusedNow

        // Flakiness: ONE parse + ONE rewrite of the history file for the whole run,
        // with every project's records — not one per project, and not racing itself
        // from inside Async.Parallel. Best-effort: the history is a diagnostic, so a
        // failure to record it must never fail the run that produced it.
        let collectedFlakiness = collected.Read _.Flakiness

        if not collectedFlakiness.IsEmpty then
            try
                Flakiness.appendRecords (Flakiness.historyPath repoRoot) 20 collectedFlakiness
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

        // Traces: after every project finished, before `tidyRunsDir` can rotate the run
        // directory holding the dumps. `ingestAll` first waits, bounded, for this plugin's
        // mailbox to fold the analysis events admitted before now, so the traces join
        // against the index those events write. The folds never wait on this run, whose
        // result is posted only after it returns. It never throws, and nothing it does
        // reaches `cumulative`: a trace failure is a log line and a stored row, never a
        // changed result.
        match traces with
        | Some host ->
            let runs = collected.Read(fun c -> List.rev c.TraceRuns)

            do!
                TraceRun.ingestAll
                    host.TraceRuntime
                    (TestPrune.Ports.toSymbolStore db)
                    host.IndexFold
                    (runId.ToString("N"))
                    (boundTree |> Option.map _.Identity)
                    (fun () -> ReceiptInputTree.read repoRoot)
                    runs
                    host.TraceLog
        | None -> ()

        // Bound what `.fshw/test-runs/` retains, and purge the DEAD `.log` format.
        // Runs AFTER this run's reports were written, so the
        // evidence the verdict is about to point at is always among the survivors.
        Ctrf.tidyRunsDir repoRoot Ctrf.RetainedRuns

        sw.Stop()

        let finalResults = collected.Read _.Results

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // `TestRunCompleted` is emitted by the CALLER (the synchronous Custom handler) so
        // it lands in EmittedEvents for cache replay; returned in the tuple instead. The
        // matching start was emitted before any host could mutate its input artifacts.
        // `Outcome = Normal` means the run completed naturally — per-project pass/fail
        // lives in Results, and Aborted is reserved for cancellation/timeout/crash.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              Verification =
                verificationWithin
                    (configuredCoverageProjects |> List.map (fun c -> c.Project) |> Set.ofList)
                    finalResults }

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed, boundTree, testHostStartedAt, evidenceHeld
    }
