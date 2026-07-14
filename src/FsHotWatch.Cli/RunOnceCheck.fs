/// `check` and `confirm` WITHOUT a daemon — the `--run-once` path (AUTOMATION-117).
///
/// WHY THIS FILE EXISTS. `confirm` was reachable only over the daemon's IPC socket, and
/// `--run-once` bypasses the daemon entirely — which is precisely how CI runs fshw. So
/// our own CI could not invoke the very check it is supposed to be judged by. It happened to
/// run the full suite anyway, because a CI checkout starts with a COLD impact DB and a
/// cold DB selects everything; that is an accident of the environment, not a property
/// of the tool. Warm the cache, restore the DB, or optimise CI at all, and the same
/// green would silently start coming from a subset — the exact bug this release exists
/// to eliminate, sitting in the release's own CI config.
///
/// ONE VERDICT, TWO TRANSPORTS. Everything that DECIDES anything here is shared with
/// the daemon path, not re-implemented beside it:
///
///   * the scope commands and their parser — `IpcParsing` (`SetScopeCommand`,
///     `TestScopeCommand`, `parseTestRunReport`);
///   * the completeness signal — `Daemon.LiveCoverage`, the same computation the IPC
///     `GetUncheckedCount` closure serves;
///   * the verdict — `CheckVerdict.verdict` / `converge`;
///   * the verdict FILE — `IpcOutput.publishVerdict`.
///
/// The only thing that differs is the transport: `PluginHost.RunCommand` in-process
/// instead of a socket. A second verdict computation is a second thing that can go
/// green while the first goes red, and the entire point of `.fshw/verdict.json` is
/// that its answer and the exit code cannot disagree.
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
/// Every way of not getting a straight answer — no test-prune plugin, an unparseable
/// reply, a throw — becomes `ScopeUnknown`, which `confirm` refuses. The failure
/// direction is safe BY CONSTRUCTION: `confirm` can only go green on a scope it positively
/// established. But it is never SILENT about it — safe-and-mute is how `fshw confirm`
/// stayed broken on every repo for its whole life (AUTOMATION-129).
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
        Logging.warn "cli-confirm" $"could not read the test scope: %s{ex.Message}"
        { Scope = ScopeUnknown; RunId = None }

/// Turn impact filtering OFF for this process, BEFORE anything runs.
///
/// The ordering is load-bearing and is the same rule the daemon path follows: the scan
/// below provokes the test run, and that run must ALREADY be unfiltered. Asking
/// afterwards would only learn that it wasn't.
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
/// report a run still in flight (`ScopeUnknown`) — a refusal caused not by the evidence but by not
/// having waited long enough for the answer it asked for. Settling (never re-scanning: a second
/// scan would rebuild the world just to wait for a run already in progress) means the
/// scope is read from what the run actually DID.
let internal forceFullRun (daemon: Daemon.Daemon) : unit =
    requestFullRun daemon.Host

    try
        daemon.Settle() |> Async.RunSynchronously
    with ex ->
        Logging.warn "cli-confirm" $"could not settle after the forced full-suite run: %s{ex.Message}"

/// The plugins' failing-diagnostic count — the `hasFailures` half of the verdict.
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
        let reread () : bool * Coverage * TestScope =
            finalStatuses.Value <- snapshotHost daemon.Host (daemon.Host.GetAllStatuses())
            finalRun.Value <- readTestRun daemon.Host
            (failingCount daemon noWarnFail pluginName > 0, liveCoverage daemon, finalRun.Value.Scope)

        /// The convergence re-scan: scan again and settle. In-process, a re-`RunOnce`
        /// IS the re-scan.
        let rescan () : unit = runOnceWithProgress daemon |> ignore

        // CONFIRM EARNS ITS EVIDENCE. A cold run-once scan reaches the test-prune
        // launch chokepoint (build → BuildCompleted), where full-suite scope has already
        // forced every project in full — so in the common case the scope is ALREADY
        // `FullSuite` here and nothing more runs. This is the backstop for every case
        // where it is not (a replayed cache entry, a skipped launch): `confirm` goes and
        // produces the evidence rather than refusing for want of evidence it declined to
        // get. It is a backstop, not the mechanism — hence it costs nothing when the
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

        // Defense-in-depth: warn if any FileCommand plugin's args reference a file
        // modified after the plugin's last run started. Catches cache-key gaps in
        // plugins whose salt doesn't fully cover their inputs.
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
        // it, from the same `CheckOutcome` the exit code below comes from. Before this,
        // `check --run-once` (i.e. CI) wrote no verdict at all, so `fshw verdict` after a
        // CI run reported "no verdict" — the machine-readable answer was missing from the
        // one place a machine was reading.
        IpcOutput.publishVerdict repoRoot config.Exclude checkMode noWarnFail finalRun.Value finalStatuses.Value outcome

        match outcome with
        | CheckVerdict.CheckOutcome.Incomplete n ->
            let detail =
                if n > 0 then
                    $"%d{n} file(s) could not be checked"
                else
                    "coverage could not be confirmed"

            UI.fail $"Check incomplete: %s{detail}"
        | CheckVerdict.CheckOutcome.UnearnedScope scope ->
            // Nothing failed — and that is the point. `confirm` was asked for a claim
            // about the whole suite; the tests that ran do not support one; so it has no
            // verdict to give. Say so. Never launder it into a green.
            UI.fail
                $"Confirm: NO VERDICT — the tests that ran were %s{TestScope.describe scope}, \
                   not the full suite.\nAn impact-filtered green means \"your change didn't break anything I chose to \
                   look at\", which is not the claim a merge needs. Nothing is reported broken, but nothing is \
                   reported sound either."
        | CheckVerdict.CheckOutcome.Clean
        | CheckVerdict.CheckOutcome.FailuresFound -> ()

        CheckVerdict.exitCode outcome
