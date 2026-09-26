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
/// Neither step throws. Tracing records evidence about a run; it never decides one, so no
/// trace failure can change a test result, the verdict, or the run's lifecycle.
namespace FsHotWatch.TestPrune

open System
open System.IO
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

/// Storing one traced project's finished run (`TraceSession.ingestProject`'s shape).
type TraceIngestion =
    TraceStore.Store
        -> string
        -> TraceSession.TraceLaunch
        -> TraceSession.Completion
        -> Result<TraceIngest.IngestSummary, string>

/// How a plugin instance traces: its `tests.traces` settings, the projects that opted
/// out, and the decision function (`TraceRun.decide`, or a test's stand-in).
type internal TraceWiring =
    { Policy: TraceSettings
      OptedOut: Set<string>
      Decide: TraceRuntime -> TraceProject -> string -> string list -> TraceDecision }

/// Deciding which projects a run traces, and storing what they recorded.
[<RequireQualifiedAccess>]
module TraceRun =
    let private weaveMode (weave: TraceWeaveTests) =
        match weave with
        | WeaveTestSites -> Model.SitesOnly
        | WeaveTestFull -> Model.Full

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
                          WeaveTests = weaveMode rt.Settings.WeaveTests
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

    /// For one project about to launch: trace it or not, and how. Never throws.
    let decide rt project runDir extraArgs =
        decideWith TraceSession.prepareProject TracedLaunch.dotnetRootOfThisProcess rt project runDir extraArgs

    /// The command, argument line and environment to launch: the traced ones, or `plain`.
    let launchOf (decision: TraceDecision) (plain: string * string * (string * string) list) =
        match decision with
        | Traced(spec, _) -> spec.Command, TracedLaunch.argsLine spec.Args, spec.Environment
        | Untraced _ -> plain

    /// Why a traced launch must be repeated untraced, or `None`. A traced launch that
    /// failed without writing its CTRF report verified nothing, and the trace (the woven
    /// copy, the recorder) is the likeliest cause: the project re-runs untraced so the
    /// run's verdict is the one it would have had without traces. A traced run that
    /// reported is never repeated, whatever it reported, so a real failure (or a flake)
    /// is never re-rolled into a pass.
    let untracedRetry (decision: TraceDecision) (succeeded: bool) (reportExists: bool) : string option =
        match decision with
        | Traced _ when not succeeded && not reportExists ->
            Some "the traced launch failed without writing a test report; the project re-ran untraced"
        | _ -> None

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

    /// `ingestAll`, with the per-project ingestion injected.
    let ingestAllWith
        (ingest: TraceIngestion)
        (rt: TraceRuntime)
        (symbols: TestPrune.Ports.SymbolStore)
        (runId: string)
        (launchTreeHash: string option)
        (currentTreeHash: unit -> string option)
        (runs: TracedProjectRun list)
        (log: string -> unit)
        : unit =
        let recorded =
            runs
            |> List.choose (fun r ->
                match r.Decision with
                | Untraced None -> None
                | Untraced(Some reason) -> Some(r, Refusal reason)
                | Traced(_, session) -> Some(r, Ingest session))

        if not recorded.IsEmpty then
            try
                use store = TraceStore.Store.Open(dbPath rt)
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
                        match work with
                        | Refusal reason ->
                            TraceSession.recordRefusal store runId run.Project kind launch reason
                            log $"traces: %s{run.Project} not recorded — %s{reason}"
                        | Ingest session ->
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
            with ex ->
                log $"traces: not recorded — could not use the trace store at %s{dbPath rt}: %s{ex.Message}"

    /// After the run's parallel section: store each traced project's traces, or the
    /// refusal of each project tracing refused, and log one line per project. Never
    /// throws; a project tracing does not apply to stores and logs nothing.
    let ingestAll rt symbols runId launchTreeHash currentTreeHash runs log =
        ingestAllWith TraceSession.ingestProject rt symbols runId launchTreeHash currentTreeHash runs log
