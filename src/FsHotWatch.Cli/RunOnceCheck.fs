/// `check` and `confirm` WITHOUT a daemon — the `--run-once` path, which is how CI
/// runs fshw.
///
/// ONE VERDICT, TWO TRANSPORTS. Everything that DECIDES anything here is shared with
/// the daemon path, not re-implemented beside it: the scope commands and their parser
/// (`IpcParsing`), the completeness signal (`Daemon.LiveCoverage`, the same computation
/// the IPC `GetUncheckedCount` closure serves), the verdict (`CheckVerdict.verdict` /
/// `converge`), and the verdict FILE (`IpcOutput.publishVerdict`).
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

/// Send a command to the in-process plugin host.
///
/// `None` — the host has no such command, which is the in-process spelling of the IPC
/// unknown-command sentinel. It means the test-prune plugin is not registered (no test
/// projects configured), NOT that everything is fine.
let private runHostCommand (host: PluginHost.PluginHost) (name: string) (args: string array) : string option =
    host.RunCommand(name, args) |> Async.RunSynchronously

/// Ask the host what the last completed run ACTUALLY covered.
///
/// No way of not getting a straight answer can round UP to `FullSuite`, so `confirm` can
/// only go green on a scope it positively established. But the ways are different FACTS
/// and are reported as such:
///
///   * NO SUCH COMMAND — the test-prune plugin is not registered, i.e. no test projects
///     are configured. A provable "there is no scope to report, and there never will
///     be": `ScopeUnknown`, which the inner loop tolerates and `confirm` refuses.
///   * A THROW — we asked and could not find out. `ScopeUnreadable`, which BOTH modes
///     refuse: the state it hides may be `NoTestsRun`.
///
/// Never SILENT about either.
let internal readTestRun (host: PluginHost.PluginHost) : TestRunReport =
    try
        match runHostCommand host TestScopeCommand [||] with
        | None ->
            Logging.warn
                "cli-confirm"
                $"the plugin host has no `%s{TestScopeCommand}` command — no test projects are configured, so a merge \
                   verdict cannot be earned here. `fshw confirm --run-once` will report NO VERDICT."

            TestRunReport.ofScopeOnly ScopeUnknown
        | Some reply -> parseTestRunReport reply
    with ex ->
        Logging.warn
            "cli-confirm"
            $"could not read the test scope: %s{ex.Message}. This is NOT \"no tests were needed\" — the check will \
               report NO VERDICT rather than pass on a reading it does not have."

        TestRunReport.ofScopeOnly (
            ScopeUnreadable $"the plugin host's `%s{TestScopeCommand}` command threw: %s{ex.Message}"
        )

/// AUTOMATION-259. Ask the in-process host what `check`'s impact selection WOULD have
/// reached in the run this `confirm` did not have to escalate — the `--run-once` twin of
/// `Program.readCheckReach`.
///
/// Reads state that already exists; nothing runs. Every way of not getting an answer is a
/// VALUE — no such command (no test projects, or a plugin build that predates the
/// projection), a throw, a reply this build cannot read — and each reaches the verdict as
/// "no sample". None of them may reach it as "they agreed".
let internal readCheckReach (host: PluginHost.PluginHost) : CheckReachReading =
    try
        match runHostCommand host CheckReachCommand [||] with
        | None ->
            ReachUnavailable
                $"the plugin host has no `%s{CheckReachCommand}` command — no test projects are configured, so there \
                   is no impact selection to project through"
        | Some reply -> parseCheckReach reply
    with ex ->
        ReachUnavailable $"the plugin host's `%s{CheckReachCommand}` command threw: %s{ex.Message}"

/// Turn impact filtering OFF for this process, BEFORE anything runs.
///
/// The ordering matters, and is the same rule the daemon path follows: the scan below
/// provokes the test run, and that run must ALREADY be unfiltered. Asking afterwards
/// would only learn that it wasn't.
///
/// A failure here is not fatal on its own — `confirm` does not trust this call's return
/// value. It trusts `readTestRun`, which reports what actually ran. The request is not
/// the evidence.
let internal requestFullSuiteScope (host: PluginHost.PluginHost) : unit =
    try
        match runHostCommand host SetScopeCommand [| FullSuiteScopeArgs |] with
        | None ->
            eprintfn
                $"fshw confirm: the plugin host has no `%s{SetScopeCommand}` command (no test projects configured). \
                   The verdict will be refused."
        | Some reply -> Logging.debug "cli-confirm" $"set-scope reply: %s{reply}"
    with ex ->
        eprintfn
            $"fshw confirm: could not disable impact filtering (%s{ex.Message}). \
               The tests will run impact-filtered, and the verdict will be refused."

/// Ask the host to run EVERY configured test project, now — `run-tests` with no filter
/// and no project selection. `confirm`'s teeth: see `CheckVerdict.confirmNeedsFullRun`.
///
/// Sends no `waitSec`, so the plugin's own default budget applies (ONE default, not a
/// second one here). If that budget expires the run is NOT cancelled — it was already
/// launched, and only the WAIT gave up. So this REQUESTS the run; `forceFullRun` below
/// is what waits for it.
let internal requestFullRun (host: PluginHost.PluginHost) : unit =
    try
        match runHostCommand host RunTestsCommand [| "{}" |] with
        | None ->
            // No test-prune plugin: there is nothing to force, and `readTestRun` will
            // report `ScopeUnknown` — which `confirm` refuses. Nothing to do but say so.
            Logging.warn "cli-confirm" $"the plugin host has no `%s{RunTestsCommand}` command — no tests can be forced"
        | Some reply -> Logging.debug "cli-confirm" $"run-tests reply: %s{reply}"
    with ex ->
        Logging.warn "cli-confirm" $"the forced full-suite run failed: %s{ex.Message}"

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
/// (AUTOMATION-303).
let private failingEntries
    (daemon: Daemon.Daemon)
    (noWarnFail: bool)
    (pluginName: string option)
    : (string * (string * ErrorEntry)) list =
    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    allErrors
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
let private failingCount (daemon: Daemon.Daemon) (noWarnFail: bool) (pluginName: string option) : int =
    failingEntries daemon noWarnFail pluginName |> List.length

/// AUTOMATION-303. The in-process twin of `IpcOutput.redCausesOf`: the failing ledger
/// entries the exit code was computed from, as the verdict records them. Derived from
/// the SAME traversal as the count, so the two transports — and the file and the exit
/// code — cannot disagree about what reddened the run.
let private redCauses (daemon: Daemon.Daemon) (noWarnFail: bool) (pluginName: string option) =
    failingEntries daemon noWarnFail pluginName
    |> List.map (fun (file, (source, e)) ->
        { Verdict.Source = source
          Verdict.File = file
          Verdict.Severity = DiagnosticSeverity.toString e.Severity
          Verdict.Message = e.Message
          Verdict.Kind = Verdict.RedCause.classify source file e.Message })

/// AUTOMATION-303. How many failing entries are NOT claims about the tree on disk — the
/// in-process twin of `IpcOutput.unattributableCountOf`, off the same traversal as
/// `redCauses` AND through the same `RedCause.unattributable` selection, so the two
/// transports classify identically.
let private unattributableCount (daemon: Daemon.Daemon) (noWarnFail: bool) (pluginName: string option) : int =
    redCauses daemon noWarnFail pluginName
    |> Verdict.RedCause.unattributable
    |> List.length

/// Is any test project WAITING ON BUILD — a `Deferred`-severity ledger entry (its tests
/// did not run) — and WHY? The in-process twin of `IpcOutput.waitingOnBuild`: both read
/// the SAME condition (a deferred diagnostic) and hand it to the SAME classifier, so the
/// two transports cannot disagree either about what a defer means or about which of its
/// two causes this run has.
let private waitingOnBuild (daemon: Daemon.Daemon) (pluginName: string option) : CheckVerdict.BuildWait =
    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    allErrors
    |> Map.toList
    |> List.collect snd
    |> List.filter (fun (_, e) -> ErrorEntry.isWaitingOnBuild e)
    |> List.map (fun (_, e) -> e.Message)
    |> CheckVerdict.BuildWait.classify

/// Did a test HOST DIE mid-run — a `HostAborted`-severity ledger entry (its tests did not
/// finish) — and with what diagnosis? The in-process twin of `IpcOutput.runnerAborted`:
/// both read the SAME condition and hand it to the SAME classifier, so the two
/// transports cannot disagree about what an abort means.
let private runnerAborted (daemon: Daemon.Daemon) (pluginName: string option) : CheckVerdict.RunnerAbort =
    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    allErrors
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
    // AUTOMATION-555. The invocation every verdict this run publishes belongs to.
    (invocation: Verdict.Invocation)
    (runScan: Daemon.Daemon -> Map<string, PluginStatus>)
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (pluginName: string option)
    : int =
    match failIfNoProjects repoRoot config.Exclude with
    | Some _ ->
        // AUTOMATION-555. A zero-project run is a terminal `incomplete` like any other
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

        // AUTOMATION-167. The `--run-once` twin of the daemon path's
        // post-`WaitForComplete` capture: the tree as it was when the in-process run
        // finished. Re-captured at EVERY settle — the first scan, the forced full suite,
        // each convergence re-scan — and never after the reads, the summary render and
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

        // What the run produced. Re-read after every step that can change it (a forced
        // run, a convergence re-scan) — never carried over from an earlier snapshot,
        // which is how a verdict ends up describing a run that isn't the one it graded.
        let finalStatuses = ref (snapshotHost daemon.Host statuses)
        let finalRun = ref (TestRunReport.ofScopeOnly ScopeUnknown)
        let retainedTestRun: IpcOutput.RetainedTestRun option ref = ref None

        // AUTOMATION-533. Every run this check has provoked, oldest first. The baseline
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

        /// Read the current state of the host. NO scan — the caller decides when work
        /// happens, so a read can never be mistaken for one.
        ///
        /// The in-process half of "one verdict, two transports" (the daemon's half is
        /// `IpcOutput.checkInputs`). It OBSERVES; it decides nothing.
        let reread () : CheckVerdict.CheckInputs =
            // A watcher may begin project rediscovery after the preceding scan/settle.
            // Never grade the transient cleared graph/pipeline as complete: await the
            // atomic completed discovery outcome at every convergence reading.
            awaitDiscovery ()
            finalStatuses.Value <- snapshotHost daemon.Host (daemon.Host.GetAllStatuses())
            let run = readTestRun daemon.Host |> observeTestRun

            { PluginStatuses = finalStatuses.Value
              FailingDiagnostics = failingCount daemon noWarnFail pluginName
              UnattributableDiagnostics = unattributableCount daemon noWarnFail pluginName
              WaitingOnBuild = waitingOnBuild daemon pluginName
              RunnerAborted = runnerAborted daemon pluginName
              Coverage = liveCoverage daemon
              Scope = run.Scope }

        /// The convergence re-scan: scan again and settle. In-process, a re-`RunOnce`
        /// IS the re-scan.
        let rescan () : unit = scanAndSettle () |> ignore

        // CONFIRM EARNS ITS EVIDENCE. A cold run-once scan reaches the test-prune launch
        // chokepoint (build → BuildCompleted), where full-suite scope has already forced
        // every project in full, so in the common case the scope is ALREADY `FullSuite`
        // here and nothing more runs. This is the backstop for the cases where it is not
        // (a replayed cache entry, a skipped launch) — it costs nothing when the
        // mechanism worked.

        // The reading `confirm` is about to throw away. `reread` OBSERVES — no scan, no run
        // — so taking it here costs nothing and gives both branches the same starting fact.
        let preEscalation = reread ()

        // AUTOMATION-259. Captured BEFORE the forced run, from state that already exists:
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
                eprintfn
                    "  Confirm: the tests that ran were %s — running the FULL suite to earn a verdict..."
                    (TestScope.describe preEscalation.Scope)

                forceFullRun daemon
                awaitDiscovery ()
                settledTree.Value <- IpcOutput.SettledTree.capture repoRoot config.Exclude
                // Re-read: the forced run's failures ARE the answer.
                reread ()
            | None -> preEscalation

        // The SAME convergence the daemon path runs: an incomplete-but-clean read is
        // re-scanned (up to a budget) before it is called un-completable.
        let outcome =
            CheckVerdict.converge checkMode IpcOutput.MaxConvergeAttempts rescan reread initialRead

        let summary = renderSummary finalStatuses.Value

        if summary <> "" then
            eprintfn "%s" summary

        let allErrors =
            match pluginName with
            | Some name ->
                daemon.Host.GetErrorsByPlugin(name)
                |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
            | None -> daemon.Host.GetErrors()

        eprintfn "%s" (formatErrors allErrors)

        // Defense-in-depth against cache-key gaps — see `detectStalePluginInputs`.
        let staleInputs =
            config.FileCommands
            |> List.choose (fun fc ->
                match
                    Map.tryFind fc.PluginName finalStatuses.Value
                    |> Option.bind (fun p -> p.LastRun)
                with
                | Some lastRun ->
                    Some
                        { Name = fc.PluginName
                          LastRunStarted = lastRun.StartedAt
                          RepoRoot = repoRoot
                          Args = fc.Args }
                | None -> None)
            |> detectStalePluginInputs

        let stalenessWarning = formatStalenessWarning staleInputs

        if stalenessWarning <> "" then
            eprintfn "%s" stalenessWarning

        // The verdict file — on EVERY terminal path, exactly as the daemon path writes
        // it, from the same `CheckOutcome` the exit code below comes from, so
        // `fshw verdict` after a CI run has a machine-readable answer that matches the
        // exit code.
        // AUTOMATION-259. Same rule as the daemon path: an escalation produced an EXECUTED
        // reading; a `confirm` that did not have to escalate offers the PROJECTION through
        // the selection retained at the widening; a `check` offers nothing.
        let checkScoped =
            match impactScoped, checkMode with
            | Some reading, _ -> Verdict.ExecutedReading(reading, readCheckReach daemon.Host)
            | None, CheckVerdict.Confirmation -> Verdict.ProjectedThrough(readCheckReach daemon.Host)
            | None, CheckVerdict.InnerLoop -> Verdict.NoReading

        // AUTOMATION-167. Take the code `publishVerdict` WROTE. The comment above
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
                (redCauses daemon noWarnFail pluginName)
                settledTree.Value
                outcome

        // `Verdict.CheckProse.explainOutcome`, the very call the daemon path makes:
        // `--run-once` differs in HOW the check ran, never in what it means. `None` for
        // the re-scan count is not a missing number — this path does not converge, so it
        // has no attempts to report.
        match Verdict.CheckProse.explainOutcome None outcome with
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
    (pluginName: string option)
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
        pluginName

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
    (pluginName: string option)
    : int =
    runOnceAndVerdictIn
        invocation
        runOnceWithProgress
        renderSummary
        checkMode
        noWarnFail
        createDaemon
        repoRoot
        config
        pluginName

/// `runOnceAndVerdictForInvocation` for a run that no CLI bracket wraps.
let runOnceAndVerdict
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (pluginName: string option)
    : int =
    runOnceAndVerdictForInvocation
        (Verdict.Invocation.start ())
        renderSummary
        checkMode
        noWarnFail
        createDaemon
        repoRoot
        config
        pluginName
