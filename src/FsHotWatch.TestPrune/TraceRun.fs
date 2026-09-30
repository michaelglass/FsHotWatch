/// Recording per-test traces inside a test run (`tests.traces`).
///
/// Two steps, one on each side of the run's parallel section:
///
/// 1. `decide`, per project about to launch: trace it or not. A traced project launches
///    its woven apphost from `bin/Traced/<tfm>/` with the recorder's environment instead
///    of its configured `dotnet run` line. Anything that stands in the way (no CTRF
///    report, an underivable project, an unknown `dotnet run` option, no dotnet root, a
///    weave or JIT-verification refusal) is a named REFUSAL: the project launches exactly
///    as it would without traces.
/// 2. `ingestAll`, once every project finished: store each traced project's traces, or
///    its refusal, in the separate trace store, and log one line per project naming the
///    counts or the reason.
///
/// Neither step throws, except that `decide` lets a cancelled run's
/// `OperationCanceledException` through. Tracing records evidence about a run; it never
/// decides one, so no trace failure can change a test result, the verdict, or the run's
/// lifecycle.
///
/// Preparation's JIT-verification child is launched through `ProcessHelper`, so it joins
/// the run's process scope: a daemon or run shutting down mid-verify kills it with the
/// run's other children, and it is visible to leak reporting.
namespace FsHotWatch.TestPrune

open System
open System.IO
open System.Threading
open FsHotWatch
open FsHotWatch.ProcessHelper
open TestPrune.Trace

/// What tracing needs to know about the run a project launches in.
type TraceRuntime =
    {
        /// The `tests.traces` block.
        Settings: TraceSettings
        RepoRoot: string
        /// Projects that opted out with `"traces": false`.
        ExcludedProjects: Set<string>
        /// The mode the run was launched under.
        Mode: TestMode
    }

/// What `decide` needs from a configured test project.
type TraceProject =
    {
        Project: string
        /// The configured command and arguments (`dotnet run --project … --no-build`).
        Command: string
        Args: string
        Environment: (string * string) list
        /// The project's build output, when its `--project` can be derived.
        Target: ArtifactFreshness.RunnerTarget option
        /// Where the run's CTRF report for this project goes; `None` when none is requested.
        CtrfPath: string option
        /// The project's own `traces.weaveTests`; `None` takes `TraceSettings.WeaveTests`.
        TraceWeave: TraceWeaveTests option
    }

/// Whether, and how, one project is traced.
type TraceDecision =
    /// Launched as configured. `None`: tracing does not apply to this run or project.
    /// `Some reason`: tracing was refused, and the reason is logged and stored.
    | Untraced of reason: string option
    /// Launched from the woven copy.
    | Traced of launch: TracedLaunchSpec * session: TraceSession.TraceLaunch

/// One launched project, as ingestion sees it once the run finished.
type TracedProjectRun =
    {
        Project: string
        Decision: TraceDecision
        /// Launched with a class or raw filter: its traces are a partial run, which never
        /// drops the stored trace of a test it did not run.
        Filtered: bool
        CtrfPath: string option
    }

/// What follows a traced launch that ended without writing its test report.
type TracedFailure =
    /// It crashed or exited without its report: the trace (the woven copy, the recorder)
    /// is the likeliest cause, so the project runs again untraced, with the full project
    /// timeout it would have had without traces.
    | RelaunchUntraced of reason: string
    /// It was killed at the project timeout, and is not relaunched. An untraced run given
    /// what is left of the timeout could not finish; one given a fresh timeout doubles the
    /// project's wall-clock bound. The timeout stands as the project's outcome.
    | KeepTimeout of reason: string

/// Storing one traced project's finished run (`TraceSession.ingestProject`'s shape).
type TraceIngestion =
    TraceStore.Store
        -> string
        -> TraceSession.TraceLaunch
        -> TraceSession.Completion
        -> Result<TraceIngest.IngestSummary, string>

/// The symbol index traces are joined against, as ingestion waits for it. The index is
/// written by the plugin's own event folds, which run beside the test run: a traced run on
/// a cold daemon can finish while the analysis events admitted before it are still being
/// folded, and a join against that index reports symbols it has not written yet as not
/// indexed.
type IndexFold =
    {
        /// Completes once every analysis event admitted before the call has been folded
        /// into the index.
        Folded: unit -> Tasks.Task
        /// How long ingestion waits for `Folded` before it gives up on joining.
        Bound: TimeSpan
    }

/// How a plugin instance traces: its `tests.traces` settings, the projects that opted
/// out, the projects' own weave modes, and the decision function (`TraceRun.decide`, or
/// a test's stand-in), given the run's cancellation token.
type internal TraceWiring =
    {
        Policy: TraceSettings
        OptedOut: Set<string>
        /// Project -> its own `traces.weaveTests`, for the projects that set one.
        WeaveOverrides: Map<string, TraceWeaveTests>
        Decide: CancellationToken -> TraceRuntime -> TraceProject -> string -> string list -> TraceDecision
    }

/// Deciding which projects a run traces, and storing what they recorded.
[<RequireQualifiedAccess>]
module TraceRun =
    /// An index with nothing left to fold: ingestion joins at once.
    let foldedIndex: IndexFold =
        { Folded = fun () -> Tasks.Task.CompletedTask
          Bound = TimeSpan.Zero }

    /// How long a run waits for the symbol index before ingesting its traces. A cold
    /// daemon folds its first analysis in minutes; past this, the traces are stored as not
    /// recorded rather than joined against a partial index.
    let indexFoldBound = TimeSpan.FromMinutes 5.0

    /// The reason stored for a traced project whose index did not settle in time.
    let indexUnsettledReason (waited: TimeSpan) =
        $"index-unsettled: the symbol index was still folding analysis events after %.0f{waited.TotalSeconds}s, so no probe id could be joined reliably"

    let private weaveMode (weave: TraceWeaveTests) =
        match weave with
        | WeaveTestSites -> Model.SitesOnly
        | WeaveTestFull -> Model.Full

    /// How `project`'s test assembly is woven: its own `traces.weaveTests`, else the
    /// global `tests.traces.weaveTests`.
    let effectiveWeave (rt: TraceRuntime) (project: TraceProject) =
        project.TraceWeave |> Option.defaultValue rt.Settings.WeaveTests

    /// The trace database: `Settings.DbPath`, resolved against the repository root.
    let dbPath (rt: TraceRuntime) =
        Path.Combine(rt.RepoRoot, rt.Settings.DbPath)

    /// The recorder's own keys. `DOTNET_ROOT` is left to the launch spec, which keeps a
    /// project's own and otherwise resolves it the way fshw resolves its own runtime.
    let private recorderEnv (session: TraceSession.TraceLaunch) =
        session.Env |> List.filter (fun (k, _) -> k <> "DOTNET_ROOT")

    /// `decide`, with preparation and dotnet-root resolution injected.
    let decideWith
        (prepare: TraceSession.PrepareRequest -> Result<TraceSession.TraceLaunch, string>)
        (dotnetRoot: unit -> string option)
        (rt: TraceRuntime)
        (project: TraceProject)
        (runDir: string)
        (extraArgs: string list)
        : TraceDecision =
        if
            not (TestMode.recordsTraces rt.Settings.Record rt.Mode)
            || rt.ExcludedProjects.Contains project.Project
        then
            Untraced None
        else
            // Every cheap refusal before `prepare`, which weaves and JIT-verifies.
            match project.CtrfPath, TracedLaunch.appArgs project.Command project.Args extraArgs with
            | None, _ ->
                Untraced(
                    Some
                        "no-ctrf-report: this project writes no CTRF report, so no trace could record whether its test passed"
                )
            | Some _, Error reason -> Untraced(Some reason)
            | Some _, Ok appArgs ->
                match project.Target, dotnetRoot () with
                | None, _ -> Untraced(Some "no-derivable-project")
                | Some _, None ->
                    Untraced(
                        Some
                            "no-dotnet-root: no DOTNET_ROOT is set and no runtime was found beside the dotnet host, so the woven apphost could not start"
                    )
                | Some target, Some root ->
                    let request: TraceSession.PrepareRequest =
                        { RepoRoot = rt.RepoRoot
                          ProjectDir = target.ProjectDir
                          AssemblyName = target.AssemblyName
                          TestProject = project.Project
                          WeaveTests = weaveMode (effectiveWeave rt project)
                          RunDir = runDir
                          VerifyTimeout = TimeSpan.FromSeconds(float rt.Settings.VerifyTimeoutSec) }

                    match prepare request with
                    | Error reason -> Untraced(Some reason)
                    | Ok session ->
                        Traced(
                            { Command = session.Apphost
                              Args = appArgs
                              Environment = TracedLaunch.environment root project.Environment @ recorderEnv session },
                            session
                        )

    /// The exit code a verify child reports when it overran its timeout and was killed.
    [<Literal>]
    let TimedOutExitCode = -1

    /// `launcher`, telling `onStarted` the child's pid as soon as it is running.
    let internal launcherWith (onStarted: int -> unit) : Launch.Launcher =
        fun req ct ->
            // A scope of its own inside the run's: cancelling `ct` kills this child (and
            // only it), and closing the run's scope or the daemon's still reaches it.
            let outcome =
                ProcessRegistry.withChildScope ct (fun () ->
                    runProcessObserved
                        onStarted
                        req.Exe
                        (req.Args |> List.map quoteArg |> String.concat " ")
                        req.WorkDir
                        req.Env
                        (ProcessBounds.silent req.Timeout))

            ct.ThrowIfCancellationRequested()

            match outcome with
            | Succeeded output -> 0, ProcessOutput.text output
            | Failed(code, output) -> code, ProcessOutput.text output
            | TimedOut(after, tail, kill) ->
                TimedOutExitCode,
                $"timed out after %s{renderBudget after} (%s{renderKillBrief kill}): %s{ProcessOutput.text tail}"

    /// TestPrune.Trace's process launcher on fshw's spawn: the JIT-verification child is
    /// admitted into the current process scope. The verify output is silent until it
    /// exits, so it is bounded by its timeout alone.
    let launcher: Launch.Launcher = launcherWith ignore

    /// For one project about to launch: trace it or not, and how. Preparation runs its
    /// children in the current process scope and stops once `ct` is cancelled, raising
    /// `OperationCanceledException`; nothing else throws.
    let decide (ct: CancellationToken) rt project runDir extraArgs =
        decideWith
            (TraceSession.prepareProjectWith launcher ct)
            TracedLaunch.dotnetRootOfThisProcess
            rt
            project
            runDir
            extraArgs

    /// The command, argument line and environment to launch: the traced ones, or `plain`.
    let launchOf (decision: TraceDecision) (plain: string * string * (string * string) list) =
        match decision with
        | Traced(spec, _) -> spec.Command, TracedLaunch.argsLine spec.Args, spec.Environment
        | Untraced _ -> plain

    [<Literal>]
    let RelaunchReason =
        "the traced launch failed without writing a test report; the project re-ran untraced"

    [<Literal>]
    let TimeoutReason =
        "the traced launch timed out; the project was not re-run untraced"

    /// How a traced launch that verified nothing (it did not succeed and wrote no CTRF
    /// report) is followed up, or `None`. A traced run that reported is never repeated,
    /// whatever it reported, so a real failure (or a flake) is never re-rolled into a pass.
    let untracedRetry (decision: TraceDecision) (outcome: ProcessOutcome) (reportExists: bool) : TracedFailure option =
        match decision, outcome with
        | Untraced _, _
        | _, Succeeded _ -> None
        | Traced _, _ when reportExists -> None
        | Traced _, TimedOut _ -> Some(KeepTimeout TimeoutReason)
        | Traced _, Failed _ -> Some(RelaunchUntraced RelaunchReason)

    /// Launch under `decision`, then follow a traced launch that verified nothing up as
    /// `untracedRetry` says. Returns the outcome that stands and the decision the trace
    /// store records: a traced launch that verified nothing is stored as a refusal naming
    /// what followed it. `reportExists` asks whether the launch wrote its report;
    /// `announce` is told the follow-up before it happens.
    let launchTracedOrNot
        (decision: TraceDecision)
        (reportExists: unit -> bool)
        (announce: TracedFailure -> unit)
        (launch: TraceDecision -> Async<ProcessOutcome>)
        : Async<ProcessOutcome * TraceDecision> =
        async {
            let! first = launch decision

            match untracedRetry decision first (reportExists ()) with
            | Some(RelaunchUntraced reason as followUp) ->
                announce followUp
                let untraced = Untraced(Some reason)
                let! second = launch untraced
                return second, untraced
            | Some(KeepTimeout reason as followUp) ->
                announce followUp
                return first, Untraced(Some reason)
            | None -> return first, decision
        }

    /// The input tree hashes a traced run is ingested with. An unbound tree at either end
    /// is never "unchanged": each side gets a distinct sentinel, so ingestion stores the
    /// run as tree-moved and claims no complete trace it cannot back.
    let private treeHashes (atLaunch: string option) (atCompletion: string option) =
        match atLaunch, atCompletion with
        | Some launch, Some current -> launch, current
        | Some launch, None -> launch, "unreadable-at-completion"
        | None, _ -> "unbound-at-launch", "unbound-at-launch:completion"

    let private readOutcomes (ctrf: string option) =
        ctrf
        |> Option.bind (fun path ->
            try
                Some(File.ReadAllText path)
            with _ ->
                None)
        |> Option.map Ctrf.parse
        |> Option.defaultValue []

    let private summaryLine (project: string) (outcomes: bool) (s: TraceIngest.IngestSummary) =
        let counts =
            $"traces: %s{project} %d{s.Traced}/%d{s.Executed} traced, %d{s.Complete} complete"

        match s.Status with
        | TraceStore.Recorded when outcomes -> counts
        | TraceStore.Recorded -> counts + " (no CTRF outcomes)"
        | TraceStore.TreeMovedDuringRun -> counts + " (the input tree moved during the run)"
        | TraceStore.Refused
        | TraceStore.FailedToRecord -> $"traces: %s{project} not recorded — %s{s.Reason}"

    /// What ingestion does for one project it has something to store for.
    type private Recordable =
        | Refusal of reason: string
        | Ingest of session: TraceSession.TraceLaunch

    /// Logged once ingestion has asked the index to fold and the fold is still behind:
    /// an ingest blocked on the index is visible while it waits.
    let indexWaitLine =
        "traces: waiting for the symbol index to fold the events admitted before ingestion"

    /// Wait for `index` to fold what was admitted before now: `Some waited` once it has,
    /// `None` when `index.Bound` ran out first. Cancelled with the run.
    let private awaitFolded (index: IndexFold) (log: string -> unit) =
        async {
            let! ct = Async.CancellationToken
            let started = Diagnostics.Stopwatch.StartNew()
            // Cancelled once the fold wins, so the bound's timer does not outlive the wait.
            use bound = CancellationTokenSource.CreateLinkedTokenSource ct
            let folded = index.Folded()

            if not folded.IsCompleted then
                log indexWaitLine

            let! first =
                Tasks.Task.WhenAny(folded, Tasks.Task.Delay(index.Bound, bound.Token))
                |> Async.AwaitTask

            bound.Cancel()
            ct.ThrowIfCancellationRequested()

            return
                if obj.ReferenceEquals(first, folded) then
                    Some started.Elapsed
                else
                    None
        }

    /// Store every project of `recorded`. `index` is `Error reason` when the symbol index
    /// had not folded what was admitted before ingestion began: a traced project is then
    /// stored as not recorded, since a join against a partial index would report the
    /// symbols it has not written yet as not indexed.
    let private storeAll
        (ingest: TraceIngestion)
        (rt: TraceRuntime)
        (symbols: TestPrune.Ports.SymbolStore)
        (index: Result<unit, string>)
        (runId: string)
        (launchTreeHash: string option)
        (currentTreeHash: unit -> string option)
        (recorded: (TracedProjectRun * Recordable) list)
        (log: string -> unit)
        =
        try
            // try/finally rather than `use`: `use` guards Dispose with a null check that
            // a store `Open` returned can never take.
            let store = TraceStore.Store.Open(dbPath rt)

            try
                let launch, current = treeHashes launchTreeHash (currentTreeHash ())

                let failed (project: string) kind (reason: string) =
                    store.RecordRunWithoutTraces
                        { RunId = runId
                          TestProject = project
                          TreeHash = launch
                          EnvFingerprint = ""
                          RecordedAt = DateTimeOffset.UtcNow
                          Kind = kind
                          Status = TraceStore.FailedToRecord
                          Reason = reason
                          StatsJson = "{}" }

                recorded
                |> List.iter (fun (run, work) ->
                    let kind =
                        if run.Filtered then
                            TraceStore.PartialRun
                        else
                            TraceStore.FullRun

                    try
                        match work, index with
                        | Refusal reason, _ ->
                            TraceSession.recordRefusal store runId run.Project kind launch reason
                            log $"traces: %s{run.Project} not recorded — %s{reason}"
                        | Ingest _, Error reason ->
                            failed run.Project kind reason
                            log $"traces: %s{run.Project} not recorded — %s{reason}"
                        | Ingest session, Ok() ->
                            let outcomes = readOutcomes run.CtrfPath

                            let completion: TraceSession.Completion =
                                { RunId = runId
                                  Kind = kind
                                  LaunchTreeHash = launch
                                  CurrentTreeHash = current
                                  Outcomes = outcomes
                                  Symbols = symbols
                                  FingerprintFiles = rt.Settings.FingerprintInputs
                                  FingerprintEnv = rt.Settings.FingerprintEnv }

                            match ingest store rt.RepoRoot session completion with
                            | Ok summary -> log (summaryLine run.Project (not outcomes.IsEmpty) summary)
                            | Error reason ->
                                failed run.Project kind reason
                                log $"traces: %s{run.Project} not recorded — %s{reason}"
                    with ex ->
                        log $"traces: %s{run.Project} not recorded — trace storage failed: %s{ex.Message}"
                        // A store that cannot take this row either is the outer handler's.
                        failed run.Project kind $"trace storage failed: %s{ex.Message}")
            finally
                (store :> IDisposable).Dispose()
        with ex ->
            log $"traces: not recorded — could not use the trace store at %s{dbPath rt}: %s{ex.Message}"

    /// `ingestAll`, with the per-project ingestion injected.
    let ingestAllWith
        (ingest: TraceIngestion)
        (rt: TraceRuntime)
        (symbols: TestPrune.Ports.SymbolStore)
        (index: IndexFold)
        (runId: string)
        (launchTreeHash: string option)
        (currentTreeHash: unit -> string option)
        (runs: TracedProjectRun list)
        (log: string -> unit)
        : Async<unit> =
        async {
            let recorded =
                runs
                |> List.choose (fun r ->
                    match r.Decision with
                    | Untraced None -> None
                    | Untraced(Some reason) -> Some(r, Refusal reason)
                    | Traced(_, session) -> Some(r, Ingest session))

            let joins =
                recorded
                |> List.exists (fun (_, work) ->
                    match work with
                    | Ingest _ -> true
                    | Refusal _ -> false)

            // Only a join reads the index; a run of refusals stores at once. Every join logs
            // how long the index took to fold, so the fold's lag behind a run is visible.
            let! indexState =
                if joins then
                    async {
                        match! awaitFolded index log with
                        | Some waited ->
                            log
                                $"traces: the symbol index folded the events admitted before ingestion in %d{int waited.TotalMilliseconds}ms"

                            return Ok()
                        | None -> return Error(indexUnsettledReason index.Bound)
                    }
                else
                    async.Return(Ok())

            if not recorded.IsEmpty then
                storeAll ingest rt symbols indexState runId launchTreeHash currentTreeHash recorded log
        }

    /// After the run's parallel section: wait (bounded by `index.Bound`) for the symbol
    /// index to fold the analysis events admitted before now, then store each traced
    /// project's traces, or the refusal of each project tracing refused, and log one line
    /// per project. Never throws, except that a cancelled run's wait raises
    /// `OperationCanceledException`; a project tracing does not apply to stores and logs
    /// nothing.
    let ingestAll rt symbols index runId launchTreeHash currentTreeHash runs log =
        ingestAllWith TraceSession.ingestProject rt symbols index runId launchTreeHash currentTreeHash runs log
