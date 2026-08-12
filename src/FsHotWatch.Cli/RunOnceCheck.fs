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

            { Scope = ScopeUnknown; RunId = None }
        | Some reply -> parseTestRunReport reply
    with ex ->
        Logging.warn
            "cli-confirm"
            $"could not read the test scope: %s{ex.Message}. This is NOT \"no tests were needed\" — the check will \
               report NO VERDICT rather than pass on a reading it does not have."

        { Scope = ScopeUnreadable $"the plugin host's `%s{TestScopeCommand}` command threw: %s{ex.Message}"
          RunId = None }

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

/// The plugins' failing-diagnostic count — HALF of "did this run find real
/// problems". The other half is `CheckVerdict.CheckInputs.anyPluginFailed`: a plugin
/// can reach `Failed` without writing a single `ErrorEntry` (the framework's
/// crash-nets force exactly that), so both terms are needed. Never used alone; see
/// `reread` below.
let private failingCount (daemon: Daemon.Daemon) (noWarnFail: bool) (pluginName: string option) : int =
    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    allErrors
    |> Map.toList
    |> List.collect snd
    |> List.filter (fun (_, e) -> ErrorEntry.isFailing (not noWarnFail) e)
    |> List.length

/// Is any test project WAITING ON BUILD — a `Deferred`-severity ledger entry
/// (tests did not run because the build artifact wasn't ready)? The in-process
/// twin of `IpcOutput.waitingOnBuild`: both read the SAME condition (a deferred
/// diagnostic), so the two transports cannot disagree on what a defer means.
let private waitingOnBuild (daemon: Daemon.Daemon) (pluginName: string option) : bool =
    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    allErrors
    |> Map.toList
    |> List.collect snd
    |> List.exists (fun (_, e) -> ErrorEntry.isWaitingOnBuild e)

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
let runOnceAndVerdict
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (createDaemon: string -> Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfiguration)
    (pluginName: string option)
    : int =
    match failIfNoProjects repoRoot config.Exclude with
    | Some exitCode -> exitCode
    | None ->

        let daemon = createDaemon repoRoot
        registerPlugins daemon repoRoot config

        // BEFORE the scan — the run it provokes must already be unfiltered.
        if checkMode = CheckVerdict.Confirmation then
            requestFullSuiteScope daemon.Host

        let statuses = runOnceWithProgress daemon

        // What the run produced. Re-read after every step that can change it (a forced
        // run, a convergence re-scan) — never carried over from an earlier snapshot,
        // which is how a verdict ends up describing a run that isn't the one it graded.
        let finalStatuses = ref (snapshotHost daemon.Host statuses)
        let finalRun = ref (readTestRun daemon.Host)

        /// Read the current state of the host. NO scan — the caller decides when work
        /// happens, so a read can never be mistaken for one.
        ///
        /// The in-process half of "one verdict, two transports" (the daemon's half is
        /// `IpcOutput.checkInputs`). It OBSERVES; it decides nothing.
        let reread () : CheckVerdict.CheckInputs =
            finalStatuses.Value <- snapshotHost daemon.Host (daemon.Host.GetAllStatuses())
            finalRun.Value <- readTestRun daemon.Host

            { PluginStatuses = finalStatuses.Value
              FailingDiagnostics = failingCount daemon noWarnFail pluginName
              WaitingOnBuild = waitingOnBuild daemon pluginName
              Coverage = liveCoverage daemon
              Scope = finalRun.Value.Scope }

        /// The convergence re-scan: scan again and settle. In-process, a re-`RunOnce`
        /// IS the re-scan.
        let rescan () : unit = runOnceWithProgress daemon |> ignore

        // CONFIRM EARNS ITS EVIDENCE. A cold run-once scan reaches the test-prune launch
        // chokepoint (build → BuildCompleted), where full-suite scope has already forced
        // every project in full, so in the common case the scope is ALREADY `FullSuite`
        // here and nothing more runs. This is the backstop for the cases where it is not
        // (a replayed cache entry, a skipped launch) — it costs nothing when the
        // mechanism worked.
        let initialRead =
            if CheckVerdict.confirmNeedsFullRun checkMode finalRun.Value.Scope then
                eprintfn
                    "  Confirm: the tests that ran were %s — running the FULL suite to earn a verdict..."
                    (TestScope.describe finalRun.Value.Scope)

                forceFullRun daemon

            // Reads the host either way — after a forced run (whose failures ARE the
            // answer) or after the scan alone.
            reread ()

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
        IpcOutput.publishVerdict repoRoot config.Exclude checkMode noWarnFail finalRun.Value finalStatuses.Value outcome

        match outcome with
        | CheckVerdict.CheckOutcome.Incomplete n ->
            let detail =
                if n > 0 then
                    $"%d{n} file(s) could not be checked"
                else
                    "coverage could not be confirmed"

            UI.fail $"Check incomplete: %s{detail}"
        | CheckVerdict.CheckOutcome.WaitingOnBuild ->
            // Non-green, but "could not complete", never a red — see `CheckOutcome`.
            UI.fail
                "Check incomplete: waiting on build — a test project's build artifact was not produced, so its \
                 tests did not run. Nothing was verified (not a pass) and nothing failed (not a red); re-run once \
                 the build settles."
        | CheckVerdict.CheckOutcome.UnearnedScope(ScopeUnreadable reason) ->
            // Its own words, not `confirm`'s: not "the run was too narrow" but "we could
            // not see what the run was".
            UI.fail
                $"NO VERDICT — the test scope could not be read (%s{reason}).\nThat is not the same as \"no tests were \
                   needed\": a read that faulted cannot rule out that ZERO tests ran, which is the one thing a green \
                   may never mean. Nothing is reported broken, and nothing is reported sound either."
        | CheckVerdict.CheckOutcome.UnearnedScope scope ->
            // Nothing failed, and that is the point: the tests that ran do not support a
            // whole-suite claim, so there is no verdict to give. Never a green.
            UI.fail
                $"Confirm: NO VERDICT — the tests that ran were %s{TestScope.describe scope}, \
                   not the full suite.\nAn impact-filtered green means \"your change didn't break anything I chose to \
                   look at\", which is not the claim a merge needs. Nothing is reported broken, but nothing is \
                   reported sound either."
        | CheckVerdict.CheckOutcome.Clean
        | CheckVerdict.CheckOutcome.FailuresFound -> ()

        CheckVerdict.exitCode outcome
