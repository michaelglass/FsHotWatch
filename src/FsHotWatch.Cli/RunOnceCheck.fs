/// `check` and `confirm` WITHOUT a daemon — the `--run-once` path, which is how CI
/// runs fshw.
///
/// ONE VERDICT, TWO TRANSPORTS. Everything that DECIDES anything here is shared with
/// the daemon path, not re-implemented beside it: the scope commands and their parser
/// (`IpcParsing`), the completeness signal (`Daemon.LiveCoverage`, the same computation
/// the IPC `GetUncheckedCount` closure serves), the verdict (`CheckVerdict.verdict`),
/// and the verdict FILE (`IpcOutput.publishVerdict`).
///
/// Only the transport differs: `PluginHost.RunCommand` in-process instead of a socket.
/// A second verdict computation would be a second thing that can go green while the
/// first goes red.
module FsHotWatch.Cli.RunOnceCheck

open CommandTree
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.RunOnceOutput

/// The scope commands over this transport — the in-process host. The bodies, and every
/// sentence they print, live ONCE in `ScopeCommands`, shared with the daemon path; see
/// there for what each way of not getting an answer means.
let internal readTestRun (host: PluginHost.PluginHost) : TestRunReport =
    ScopeCommands.readTestRun (ScopeCommands.inProcess host)

/// `ScopeCommands.readCheckReach` over the in-process host.
let internal readCheckReach (host: PluginHost.PluginHost) : CheckReachReading =
    ScopeCommands.readCheckReach (ScopeCommands.inProcess host)

/// `ScopeCommands.requestFullSuiteScope` over the in-process host.
let internal requestFullSuiteScope (host: PluginHost.PluginHost) : unit =
    ScopeCommands.requestFullSuiteScope (ScopeCommands.inProcess host)

/// `ScopeCommands.requestFullRun` over the in-process host. `forceFullRun` below is what
/// waits for it.
let internal requestFullRun (host: PluginHost.PluginHost) : unit =
    ScopeCommands.requestFullRun (ScopeCommands.inProcess host)

/// Request the full run AND wait for it.
///
/// The wait is the authoritative bound, not the command's own budget: a `run-tests` whose
/// `waitSec` expires leaves the run going, and reading the scope at that moment would
/// report a run still in flight (`ScopeUnknown`) — a refusal caused by not having waited,
/// not by the evidence. Settle rather than re-scan (a second scan would rebuild the world
/// just to wait for a run already in progress), so the scope reflects what the run DID.
let internal forceFullRun (daemon: Daemon.Daemon) : unit =
    requestFullRun daemon.Host

    try
        daemon.Settle() |> Async.RunSynchronously
    with ex ->
        Logging.warn "cli-confirm" $"could not settle after the forced full-suite run: %s{ex.Message}"

/// The failing ledger entries, each as `(file, (source, entry))`.
///
/// ONE traversal, feeding both `failingCount` (which decides the exit code) and
/// `redCauses` (which the verdict file records), so the number and the reasons cannot
/// disagree — "exit 1 with nothing named" is what disagreement looks like from outside
let private failingEntries (daemon: Daemon.Daemon) (noWarnFail: bool) : (string * (string * ErrorEntry)) list =
    daemon.Host.GetErrors()
    |> Map.toList
    |> List.collect (fun (file, entries) ->
        entries
        |> List.filter (fun (_, e) -> ErrorEntry.isFailing (not noWarnFail) e)
        |> List.map (fun sourced -> file, sourced))

/// The plugins' failing-diagnostic count — HALF of "did this run find real
/// problems". The other half is `CheckVerdict.CheckInputs.anyPluginFailed`: a plugin
/// can reach `Failed` without writing a single `ErrorEntry` (the framework's
/// crash-nets force exactly that), so both terms are needed. Never used alone; see
/// `reread` below.
let private failingCount (daemon: Daemon.Daemon) (noWarnFail: bool) : int =
    failingEntries daemon noWarnFail |> List.length

/// The failing ledger entries with the KIND each classifies as — the in-process twin of
/// `IpcOutput.failingEntriesWithKind`. One traversal and one classifier feeding both the
/// causes the verdict records and the count of the ones that are not about this tree.
let private failingEntriesWithKind
    (daemon: Daemon.Daemon)
    (noWarnFail: bool)
    : (string * string * ErrorEntry * Verdict.RedCauseKind) list =
    let suspect =
        daemon.Host.GetErrors()
        |> Map.toSeq
        |> Seq.collect (fun (file, entries) -> entries |> Seq.map (fun (source, _) -> file, source))
        |> Verdict.RedCause.suspectFiles

    failingEntries daemon noWarnFail
    |> List.map (fun (file, (source, e)) -> file, source, e, Verdict.RedCause.classify suspect source file e.Message)

/// The in-process twin of `IpcOutput.redCausesOf`: the failing ledger
/// entries the exit code was computed from, as the verdict records them. Derived from
/// the SAME traversal as the count, so the two transports — and the file and the exit
/// code — cannot disagree about what reddened the run.
let private redCauses (daemonLog: string) (daemon: Daemon.Daemon) (noWarnFail: bool) =
    failingEntriesWithKind daemon noWarnFail
    |> List.map (fun (file, source, e, kind) ->
        { Verdict.Source = source
          Verdict.File = file
          Verdict.Severity = DiagnosticSeverity.toString e.Severity
          Verdict.Message = Verdict.RedCauseMessage.ofLedger daemonLog source file e.Message
          Verdict.Kind = kind })

/// How many failing entries are NOT claims about the tree on disk — the
/// in-process twin of `IpcOutput.unattributableCountOf`, off the same traversal as
/// `redCauses` AND through the same `RedCause.classify` / `RedCauseKind.isAboutThisTree`
/// selection, so the two transports classify identically.
let private unattributableCount (daemon: Daemon.Daemon) (noWarnFail: bool) : int =
    // Off the same traversal and the same classifier as `redCauses`. It asks only about
    // the KIND, so it needs no log pointer: `RedCause.classify` reads the LEDGER's
    // message, not the rendered one.
    failingEntriesWithKind daemon noWarnFail
    |> List.filter (fun (_, _, _, kind) -> not (Verdict.RedCauseKind.isAboutThisTree kind))
    |> List.length

/// Is any test project WAITING ON BUILD — a `Deferred`-severity ledger entry (its tests
/// did not run) — and WHY? The in-process twin of `IpcOutput.waitingOnBuild`: both read
/// the SAME condition (a deferred diagnostic) and hand it to the SAME classifier, so the
/// two transports cannot disagree either about what a defer means or about which of its
/// two causes this run has.
let private waitingOnBuild (daemon: Daemon.Daemon) : CheckVerdict.BuildWait =
    daemon.Host.GetErrors()
    |> Map.toList
    |> List.collect snd
    |> List.filter (fun (_, e) -> ErrorEntry.isWaitingOnBuild e)
    |> List.map (fun (_, e) -> e.Message)
    |> CheckVerdict.BuildWait.classify

/// Did a test HOST DIE mid-run — a `HostAborted`-severity ledger entry (its tests did not
/// finish) — and with what diagnosis? The in-process twin of `IpcOutput.runnerAborted`:
/// both read the SAME condition and hand it to the SAME classifier, so the two
/// transports cannot disagree about what an abort means.
let private runnerAborted (daemon: Daemon.Daemon) : CheckVerdict.RunnerAbort =
    daemon.Host.GetErrors()
    |> Map.toList
    |> List.collect snd
    |> List.filter (fun (_, e) -> ErrorEntry.isRunnerAbort e)
    |> List.map (fun (_, e) -> e.Message)
    |> CheckVerdict.RunnerAbort.classify

/// Did the in-process run actually check every file it is responsible for?
///
/// The SAME question, from the SAME computation, as the daemon's `GetUncheckedCount`.
/// Note there is no `Unknown` case here and there should not be: in-process we are not
/// asking a possibly-old daemon over a wire that might not answer — we are reading our
/// own host. Absence of an answer is not a possible state, so it is not a representable
/// one.
let private liveCoverage (daemon: Daemon.Daemon) : Coverage =
    match daemon.LiveCoverage() with
    | _, 0 -> Complete
    | _, unchecked -> Incomplete unchecked

/// Run every check once, in-process, and produce the verdict.
///
/// `checkMode` is the ONLY difference between `check --run-once` and `confirm --run-once`:
///   * `InnerLoop` — impact filtering stays on; the scope is read, reported, and (per
///     `CheckVerdict.verdict`) ignored. An impact-filtered green is the answer it wants.
///   * `Confirmation` — impact filtering is turned OFF before the scan; the full suite is
///     FORCED if the scan did not already produce it; and only a `FullSuite` scope can
///     reach a green. Anything less is exit 3, `UnearnedScope` — no verdict, never a
///     laundered pass.
/// Dependency-injected form used to pin command-level scan cardinality without spawning
/// an external CLI process. Production passes `runOnceWithProgress` unchanged.
let private runOnceAndVerdictIn
    // The invocation every verdict this run publishes belongs to.
    (invocation: Verdict.Invocation)
    (runScan: Daemon.Daemon -> Map<string, PluginStatus>)
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    : int =
    match failIfNoProjects repoRoot config.Exclude with
    | Some _ ->
        // A zero-project run is a terminal `incomplete` like any other
        // infrastructure refusal: it publishes, so a prior green cannot survive it as the
        // answer, and the exit code is the one the file records.
        IpcOutput.publishTerminalIncompleteForInvocation
            invocation
            repoRoot
            config.Exclude
            checkMode
            "no projects were discovered — check `.fshw.json` exclude patterns or the working directory"
            IpcOutput.NeverSettled
    | None ->

        let daemon = createDaemon repoRoot
        registerPlugins daemon repoRoot config

        // BEFORE the scan — the run it provokes must already be unfiltered.
        if checkMode = CheckVerdict.Confirmation then
            requestFullSuiteScope daemon.Host

        // The `--run-once` twin of the daemon path's
        // post-`WaitForComplete` capture: the tree as it was when the in-process run
        // finished. Re-captured at EVERY settle — the first scan and the forced full
        // suite — and never after the reads, the summary render and
        // the staleness scan below, all of which run against a live working tree.
        let settledTree = ref IpcOutput.NeverSettled

        let awaitDiscovery () =
            match daemon.WaitForDiscoveryFailure().GetAwaiter().GetResult() with
            | Some message ->
                // Publish BEFORE Program.fs converts ConfigError to exit 2. Otherwise a
                // prior green remains readable after this failed run and lies about the
                // current tree — the most dangerous form of the original incident.
                settledTree.Value <- IpcOutput.SettledTree.capture repoRoot config.Exclude

                IpcOutput.publishTerminalIncompleteForInvocation
                    invocation
                    repoRoot
                    config.Exclude
                    checkMode
                    message
                    settledTree.Value
                |> ignore

                raise (ConfigError message)
            | None -> ()

        let scanAndSettle () : Map<string, PluginStatus> =
            let statuses = runScan daemon
            awaitDiscovery ()
            daemon.Host.PruneVanishedErrors(System.IO.File.Exists) |> ignore
            settledTree.Value <- IpcOutput.SettledTree.capture repoRoot config.Exclude
            statuses

        let statuses = scanAndSettle ()

        // What the run produced. Re-read after every step that can change it (the scan,
        // and a forced full run) — never carried over from an earlier snapshot,
        // which is how a verdict ends up describing a run that isn't the one it graded.
        let finalStatuses = ref (snapshotHost daemon.Host statuses)
        let finalRun = ref (TestRunReport.ofScopeOnly ScopeUnknown)
        let retainedTestRun: IpcOutput.QualifyingEvidence option ref = ref None

        // Every run this check has provoked, oldest first. The baseline
        // is KNOWN-EMPTY and needs no read: `createDaemon` above made this host for this
        // invocation, so every run in its session ledger is by definition this check's.
        let checkRuns: System.Guid list ref = ref []

        let observeTestRun (run: TestRunReport) : TestRunReport =
            checkRuns.Value <- IpcOutput.TestRunEvidence.attribute (Some Set.empty) checkRuns.Value run

            let effective, retained =
                IpcOutput.TestRunEvidence.reconcile settledTree.Value run retainedTestRun.Value

            retainedTestRun.Value <- retained

            let effective =
                { effective with
                    CheckRuns = checkRuns.Value }

            finalRun.Value <- effective
            effective

        // The model the latest reading was taken against — recorded in
        // the verdict beside the outcome that reading produced.
        let finalModel =
            ref (IpcParsing.ProjectModelReading.Observed(daemon.ProjectModel()))

        /// Read the current state of the host. NO scan — the caller decides when work
        /// happens, so a read can never be mistaken for one.
        ///
        /// The in-process half of "one verdict, two transports" (the daemon's half is
        /// `IpcOutput.checkInputs`). It OBSERVES; it decides nothing.
        let reread () : CheckVerdict.CheckInputs =
            // A watcher may begin project rediscovery after the preceding scan/settle.
            // Never grade the transient cleared graph/pipeline as complete: await the
            // atomic completed discovery outcome at every reading.
            awaitDiscovery ()
            finalStatuses.Value <- snapshotHost daemon.Host (daemon.Host.GetAllStatuses())
            finalModel.Value <- IpcParsing.ProjectModelReading.Observed(daemon.ProjectModel())
            let run = readTestRun daemon.Host |> observeTestRun

            { PluginStatuses = finalStatuses.Value
              FailingDiagnostics = failingCount daemon noWarnFail
              UnattributableDiagnostics = unattributableCount daemon noWarnFail
              WaitingOnBuild = waitingOnBuild daemon
              RunnerAborted = runnerAborted daemon
              Coverage = liveCoverage daemon
              Scope = run.Scope
              Baseline = run.Baseline
              // In-process there is no wire to fail: the host's own coordinator answers,
              // so the reading is always an observation, never `NotReported`.
              ProjectModel = finalModel.Value }

        // CONFIRM EARNS ITS EVIDENCE. A cold run-once scan reaches the test-prune launch
        // chokepoint (build → BuildCompleted), where full-suite scope has already forced
        // every project in full, so in the common case the scope is ALREADY `FullSuite`
        // here and nothing more runs. This is the backstop for the cases where it is not
        // (a replayed cache entry, a skipped launch) — it costs nothing when the
        // mechanism worked.

        // The reading `confirm` is about to throw away. `reread` OBSERVES — no scan, no run
        // — so taking it here costs nothing and gives both branches the same starting fact.
        let preEscalation = reread ()

        // Captured BEFORE the forced run, from state that already exists:
        // same tree, same host, same instant as the verdict below. `None` when no
        // escalation was needed, which the verdict RECORDS rather than omitting.
        let impactScoped =
            if CheckVerdict.confirmNeedsFullRun checkMode preEscalation.Scope then
                Some(Verdict.impactScopedRun repoRoot finalRun.Value preEscalation)
            else
                None

        let initialRead =
            match impactScoped with
            | Some _ ->
                eprintfn "%s" (Verdict.CheckProse.forcingFullSuite preEscalation.Scope)

                forceFullRun daemon
                awaitDiscovery ()
                settledTree.Value <- IpcOutput.SettledTree.capture repoRoot config.Exclude
                // Re-read: the forced run's failures ARE the answer.
                reread ()
            | None -> preEscalation

        // ONE read decides, the same as the daemon path: this read was taken after the
        // scan settled, so re-scanning could only produce a different tree's answer.
        let outcome = CheckVerdict.verdict checkMode initialRead

        let summary = renderSummary finalStatuses.Value

        if summary <> "" then
            eprintfn "%s" summary

        eprintfn "%s" (formatErrors (daemon.Host.GetErrors()))

        // Defense-in-depth against cache-key gaps — see `detectStalePluginInputs`.
        let staleInputs =
            runInfoOfFileCommands repoRoot config finalStatuses.Value
            |> detectStalePluginInputs

        let stalenessWarning = formatStalenessWarning staleInputs

        if stalenessWarning <> "" then
            eprintfn "%s" stalenessWarning

        // The verdict file — on EVERY terminal path, exactly as the daemon path writes
        // it, from the same `CheckOutcome` the exit code below comes from, so
        // `fshw verdict` after a CI run has a machine-readable answer that matches the
        // exit code.
        // Same rule as the daemon path: an escalation produced an EXECUTED
        // reading; a `confirm` that did not have to escalate offers the PROJECTION through
        // the selection retained at the widening; a `check` offers nothing.
        let checkScoped =
            match impactScoped, checkMode with
            | Some reading, _ -> Verdict.ExecutedReading(reading, readCheckReach daemon.Host)
            | None, CheckVerdict.Confirmation -> Verdict.ProjectedThrough(readCheckReach daemon.Host)
            | None, CheckVerdict.InnerLoop -> Verdict.NoReading

        // Take the code `publishVerdict` WROTE. The comment above
        // claimed this path's exit code came "from the same CheckOutcome" as the file,
        // and it did not: `publishVerdict` downgrades to `incomplete` when the tree
        // moves during the check, and the caller recomputed 0 from the original.
        let publishedExitCode =
            IpcOutput.publishVerdictForInvocation
                invocation
                repoRoot
                config.Exclude
                checkMode
                noWarnFail
                finalRun.Value
                checkScoped
                finalStatuses.Value
                (IpcParsing.DaemonEvidence.ofHost daemon.Host)
                (redCauses (DaemonConfig.DaemonLog.under config.LogDir) daemon noWarnFail)
                finalModel.Value
                settledTree.Value
                outcome

        // `Verdict.CheckProse.explainOutcome`, the very call the daemon path makes:
        // `--run-once` differs in HOW the check ran, never in what it means. Neither path
        // re-scans, so neither has attempts to report.
        match Verdict.CheckProse.explainOutcome outcome with
        | Some explanation -> UI.fail explanation
        | None -> ()

        publishedExitCode

/// Dependency-injected form for tests that pin command-level scan cardinality without
/// a CLI bracket: the run gets a fresh invocation of its own.
let runOnceAndVerdictWith
    (runScan: Daemon.Daemon -> Map<string, PluginStatus>)
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    : int =
    runOnceAndVerdictIn
        (Verdict.Invocation.start ())
        runScan
        renderSummary
        checkMode
        noWarnFail
        createDaemon
        repoRoot
        config

/// The production `--run-once` driver: the verdict it publishes is owned by the CLI
/// invocation that bracketed it, so the wrapper's hook timing can be attached to it.
let runOnceAndVerdictForInvocation
    (invocation: Verdict.Invocation)
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    : int =
    runOnceAndVerdictIn invocation runOnceWithProgress renderSummary checkMode noWarnFail createDaemon repoRoot config

/// `runOnceAndVerdictForInvocation` for a run that no CLI bracket wraps.
let runOnceAndVerdict
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    : int =
    runOnceAndVerdictForInvocation
        (Verdict.Invocation.start ())
        renderSummary
        checkMode
        noWarnFail
        createDaemon
        repoRoot
        config
