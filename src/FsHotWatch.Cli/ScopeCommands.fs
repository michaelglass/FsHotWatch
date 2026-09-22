/// The test-prune commands `check` and `confirm` send to earn a verdict, written ONCE.
///
/// There are two transports — the daemon's IPC socket (`Program`) and `--run-once`'s
/// in-process plugin host (`RunOnceCheck`) — and they used to carry a copy of each of
/// these bodies apiece. The copies drifted: the same refusal was worded differently
/// depending on which way the CLI happened to reach the plugin, and one copy's doc named
/// a function that no longer existed. The ONLY real difference between them was how
/// "the host has no such command" is spelled — the IPC unknown-command sentinel on one
/// side, `None` from `PluginHost.RunCommand` on the other — and `Send` erases it: both
/// adapters answer `None`. Everything that decides or says anything lives here.
module FsHotWatch.Cli.ScopeCommands

open FsHotWatch
open FsHotWatch.Cli.IpcParsing

/// Send one command to the plugin host, whichever transport reaches it: command name,
/// then its JSON args (`""` for none). `None` — the host has no such command, which
/// means the test-prune plugin is not registered (no test projects configured), NOT
/// that everything is fine. A transport or handler fault THROWS; the bodies below
/// decide what a throw means.
type Send = string -> string -> string option

/// The daemon's transport: `runCommand` is the IPC `RunCommand` bound to a pipe.
/// Its unknown-command sentinel is the wire spelling of `None`.
let overIpc (runCommand: string -> string -> string) : Send =
    fun name args ->
        let reply = runCommand name args

        if Ipc.isUnknownCommandReply reply then None else Some reply

/// The in-process transport. Maps `""` to NO args exactly as the daemon's IPC handler
/// does (`Ipc.fs` `RunCommand`), so a command sees the same `args` array whichever way
/// it was reached.
let inProcess (host: PluginHost.PluginHost) : Send =
    fun name args ->
        let argv =
            if System.String.IsNullOrEmpty args then
                [||]
            else
                [| args |]

        host.RunCommand(name, argv) |> Async.RunSynchronously

/// Ask what the last completed run ACTUALLY covered.
///
/// No way of not getting a straight answer can round UP to `FullSuite`, so `confirm` can
/// only go green on a scope it positively established. But the ways are different FACTS
/// and are reported as such:
///
///   * NO SUCH COMMAND — no test projects are configured. A provable "there is no scope
///     to report, and there never will be": `ScopeUnknown`, which the inner loop
///     tolerates and `confirm` refuses.
///   * A THROW — we asked and could not find out. `ScopeUnreadable`, which BOTH modes
///     refuse: the state it hides may be `NoTestsRun`.
///
/// Never SILENT about either.
let readTestRun (send: Send) : TestRunReport =
    try
        match send TestScopeCommand "" with
        | None ->
            Logging.warn
                "cli-confirm"
                $"there is no `%s{TestScopeCommand}` command — no test projects are configured, so a full-suite \
                   verdict cannot be earned here. `fshw confirm` will report NO VERDICT."

            TestRunReport.noTestSuite
        | Some reply -> parseTestRunReport reply
    with ex ->
        Logging.warn
            "cli-confirm"
            $"could not read the test scope: %s{ex.Message}. This is NOT \"no tests were needed\" — the check will \
               report NO VERDICT rather than pass on a reading it does not have."

        TestRunReport.ofScopeOnly (ScopeUnreadable $"the `%s{TestScopeCommand}` command faulted: %s{ex.Message}")

/// Ask what `check`'s impact selection WOULD have reached in the run this `confirm` did
/// not have to escalate.
///
/// Costs nothing and RUNS nothing: the answer was computed at the launch chokepoint and
/// has been sitting in the plugin since. Every way of not getting one is a VALUE — no
/// such command (no test projects, or a plugin build that predates the projection), a
/// fault, a reply this build cannot read — and each reaches the verdict as "no sample",
/// never as "they agreed".
let readCheckReach (send: Send) : CheckReachReading =
    try
        match send CheckReachCommand "" with
        | None ->
            ReachUnavailable
                $"there is no `%s{CheckReachCommand}` command — no test projects are configured, or the plugin \
                   predates the projection"
        | Some reply -> parseCheckReach reply
    with ex ->
        ReachUnavailable $"the `%s{CheckReachCommand}` command faulted: %s{ex.Message}"

/// Turn impact filtering OFF for the rest of the session, BEFORE anything runs: the scan
/// `confirm` triggers provokes the test run, and that run must ALREADY be unfiltered.
/// Asking afterwards would only learn that it wasn't.
///
/// A failure here is NOT fatal on its own — `confirm` does not trust this call's return
/// value. It trusts `readTestRun`, which reports what actually ran; if the scope could
/// not be set, the run comes back impact-filtered and the verdict is `UnearnedScope`.
/// The request is not the evidence.
let requestFullSuiteScope (send: Send) : unit =
    try
        match send SetScopeCommand FullSuiteScopeArgs with
        | None ->
            eprintfn
                $"fshw confirm: there is no `%s{SetScopeCommand}` command (no test projects configured). \
                   The verdict will be refused."
        | Some reply -> Logging.debug "cli-confirm" $"set-scope reply: %s{reply}"
    with ex ->
        eprintfn
            $"fshw confirm: could not disable impact filtering (%s{ex.Message}). \
               The tests will run impact-filtered, and the verdict will be refused."

/// Ask the host to run EVERY configured test project, now — `run-tests` with no filter
/// and no project selection. `confirm`'s teeth: `set-scope full` only makes the NEXT
/// run unfiltered, and on a warm host whose impact DB says nothing changed there is no
/// next run. `CheckVerdict.confirmNeedsFullRun` decides when this fires.
///
/// Sends no `waitSec`, so the plugin's own default budget applies (ONE default, not a
/// second one here). An expired budget does NOT cancel the run — it was already
/// launched; only the wait gave up — so this REQUESTS the run and the caller's settle is
/// the authoritative bound.
let requestFullRun (send: Send) : unit =
    try
        match send RunTestsCommand "{}" with
        | None ->
            // No test-prune plugin: nothing to force, and `readTestRun` will report
            // `ScopeUnknown` — which `confirm` refuses. Nothing to do but say so.
            Logging.warn "cli-confirm" $"there is no `%s{RunTestsCommand}` command — no tests can be forced"
        | Some reply -> Logging.debug "cli-confirm" $"run-tests reply: %s{reply}"
    with ex ->
        Logging.warn "cli-confirm" $"the forced full-suite run failed: %s{ex.Message}"
