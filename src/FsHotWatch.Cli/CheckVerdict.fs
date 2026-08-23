module FsHotWatch.Cli.CheckVerdict

open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing

// ----------------------------------------------------------------------------
// Converge-then-verdict completeness guarantee for `fshw check`.
//
// The exit code reflects not just "were failures found" but "did the daemon
// actually check every file it's responsible for". A scan that left files
// unchecked (cancellation race, retries exhausted) must NEVER read as a clean
// exit 0 for a programmatic consumer (CI/agent). Three structural guarantees:
//  1. `exitCode` is TOTAL over an explicit CheckOutcome — no wildcard branch that
//     could swallow a new case.
//  2. Coverage is a REQUIRED input to `verdict`, and `Unknown` (a missing or
//     unparseable field) is never mapped to Clean.
//  3. `failures found` short-circuits BEFORE any convergence.
// ----------------------------------------------------------------------------

/// WHY the check is running — and therefore what it is allowed to claim.
///
///   `InnerLoop` — "did my change break anything it plausibly touches?" An
///   impact-filtered run answers this well; impact filtering is a LATENCY
///   OPTIMIZATION.
///
///   `Confirmation` — "is the suite green?" That is a CORRECTNESS CLAIM, and a
///   heuristic selector may not be its sole basis unless proven sound. Ours isn't.
///
/// Enforced in the TYPE, not by convention: a check that depends on someone
/// remembering to also run an unfiltered test-rerun before merging confirms
/// nothing. So `Confirmation` demands a `FullSuite` scope as EVIDENCE and has no
/// branch that can reach `Clean` without one.
type CheckMode =
    /// The inner dev loop. Keeps impact filtering, which is what it is good at.
    | InnerLoop
    /// Confirming that the inner loop told the truth. Only a full-suite run can
    /// produce a clean verdict.
    | Confirmation

/// WHY a test project's tests did not run — the question "waiting on build" answered
/// with one word for two causes that need OPPOSITE remedies.
///
///   * `ArtifactNotProduced` — the build-ordering race. The artifact is not there yet;
///     the next build produces it. "Re-run once the build settles" is correct advice.
///   * `StaleOutput` — the stale-artifact preflight refused. The artifact IS there and
///     its bytes do not match its sources, so re-running returns the identical refusal
///     and `fshw confirm` spends another full gate cycle to reach it. `dotnet build` is
///     the remedy, and for a timestamp-inverted copy only `--no-incremental` re-emits it.
///
/// Giving both one value is how the gate came to print the wrong remedy for the second:
/// the words "its build artifact was not produced" are not merely vague there, they are
/// false. So the cause is carried, not re-guessed at the terminal (AUTOMATION-201).
///
/// `StaleOutput` carries the deferral MESSAGES verbatim — each already names its
/// project, the file that is stale and the command that repairs it — so the top-level
/// message names every affected project without a second list to keep in step.
[<RequireQualifiedAccess>]
type BuildWait =
    /// No test project was deferred.
    | NotWaiting
    /// At least one project deferred, and none of them for stale output.
    | ArtifactNotProduced
    /// At least one deferral is the stale-artifact preflight's refusal. Dominates
    /// `ArtifactNotProduced` when a run has both: it is the more specific cause, and it
    /// is the one whose wrong remedy costs a gate cycle.
    | StaleOutput of deferrals: string list

module BuildWait =
    /// Is anything waiting on a build at all? THE predicate the verdict branches on, so
    /// a new case cannot be added without deciding this question for it.
    let isWaiting (w: BuildWait) : bool =
        match w with
        | BuildWait.NotWaiting -> false
        | BuildWait.ArtifactNotProduced
        | BuildWait.StaleOutput _ -> true

    /// The stale-output deferrals, empty when that is not the cause.
    let staleDeferrals (w: BuildWait) : string list =
        match w with
        | BuildWait.StaleOutput ds -> ds
        | BuildWait.NotWaiting
        | BuildWait.ArtifactNotProduced -> []

    /// Classify the deferral messages a run produced. ONE implementation, asked by both
    /// transports, so a socket-served check and an in-process one cannot disagree about
    /// what a defer means.
    ///
    /// Recognition is `StaleArtifactPreflight`'s own `isStaleOutputDeferral` — the
    /// module that WRITES the message also decides what one looks like, so there is no
    /// second copy of the marker to drift.
    let classify (deferralMessages: string list) : BuildWait =
        match
            deferralMessages
            |> List.filter FsHotWatch.TestPrune.StaleArtifactPreflight.isStaleOutputDeferral
        with
        | [] ->
            if List.isEmpty deferralMessages then
                BuildWait.NotWaiting
            else
                BuildWait.ArtifactNotProduced
        | stale -> BuildWait.StaleOutput stale

/// DID A TEST HOST DIE? The `BuildWait` of AUTOMATION-294, and deliberately its twin:
/// "the runner never finished" is the same CLASS of answer as "the runner never
/// started" — non-green because nothing was verified, and NOT a red because nothing
/// failed.
///
/// Its own type rather than a reuse of `BuildWait`, because the remedies are opposite
/// in the way that costs time. A defer settles on its own (the next build produces the
/// artifact); a host killed on a saturated box does NOT settle, and telling its reader
/// to "re-run once the build settles" sends them to wait for an event that is not
/// coming. The abort's remedy is a machine with headroom.
///
/// Carries the abort MESSAGES verbatim — each already names its project and what killed
/// it — so the terminal and `verdict.json` name every affected project from one list.
[<RequireQualifiedAccess>]
type RunnerAbort =
    /// Every runner that started also finished.
    | NoAbort
    /// At least one test host did not finish. `aborts` are the ledger messages.
    | HostDied of aborts: string list

module RunnerAbort =
    /// Did any test host die? THE predicate the verdict branches on, so a new case
    /// cannot be added without deciding this question for it.
    let isAborted (a: RunnerAbort) : bool =
        match a with
        | RunnerAbort.NoAbort -> false
        | RunnerAbort.HostDied _ -> true

    /// The abort messages, empty when nothing aborted.
    let aborts (a: RunnerAbort) : string list =
        match a with
        | RunnerAbort.NoAbort -> []
        | RunnerAbort.HostDied msgs -> msgs

    /// Classify the abort messages a run produced. ONE implementation, asked by both
    /// transports, so a socket-served check and an in-process one cannot disagree about
    /// what an abort means.
    let classify (abortMessages: string list) : RunnerAbort =
        match abortMessages with
        | [] -> RunnerAbort.NoAbort
        | msgs -> RunnerAbort.HostDied msgs

/// The decided outcome of a `check`, in one-to-one correspondence with an exit
/// code. `Incomplete` carries the residual unchecked count for reporting
/// (`-1` when coverage was `Unknown` — count not reported by the daemon).
[<RequireQualifiedAccess>]
type CheckOutcome =
    /// Complete coverage and no failures.
    | Clean
    /// Failures found (regardless of coverage — failures short-circuit).
    | FailuresFound
    /// No failures, but completeness could not be achieved.
    | Incomplete of unchecked: int
    /// No failures — but a test project was WAITING ON BUILD, so its tests did not
    /// run. Nothing was verified, so this is NON-green; nothing FAILED, so it is
    /// not a red. A distinct exit-2 outcome (like `Incomplete`) with its own
    /// verdict-file reason, so a deploy preflight reads "could not complete —
    /// retry", never "tests failed". A real failure alongside a defer still
    /// short-circuits to `FailuresFound` (failures are checked first).
    ///
    /// Carries the STALE-OUTPUT deferrals (empty for the build-ordering race), because
    /// the two causes have opposite remedies and every surface that explains this
    /// outcome — both terminals and the verdict file — must be able to tell them apart.
    | WaitingOnBuild of staleOutput: string list
    /// No failures — but a test HOST DIED mid-run, so its tests did not finish.
    /// AUTOMATION-294.
    ///
    /// The case this whole ticket is about. A killed host used to arrive here as
    /// `FailuresFound`: its `TestsErrored` result was counted among the failures, the
    /// exit code said 1, `verdict.json` said `red`, and the console listed the runner's
    /// half-written transcript under "N test(s) failed". Every one of those surfaces
    /// asserted a definite negative about code that had not been tested at all — which
    /// is the fail-open degrade inverted, and more expensive, because a red is
    /// investigated where a green is merely trusted.
    ///
    /// So it is its OWN exit-2 outcome beside `WaitingOnBuild`: nothing was verified
    /// (never green), nothing failed (never red), and the reason names what killed the
    /// host. A real failure alongside an abort still short-circuits to `FailuresFound`
    /// — failures are checked first, so this can never launder a red.
    | RunnerAborted of aborts: string list
    /// A CONFIRMATION run whose tests did not cover the whole suite. Nothing failed —
    /// but the run did not produce the evidence a merge verdict is made of, so there
    /// is no verdict to give. Distinct from `FailuresFound` (nothing is known to be
    /// broken) and from `Clean` (nothing is known to be sound, either): the run is
    /// owed work it did not discharge. Unreachable in `InnerLoop`, by construction.
    | UnearnedScope of TestScope
    /// EVERY failing diagnostic was one the daemon cannot attribute to the tree on
    /// disk, and no plugin failed. AUTOMATION-303, second direction.
    ///
    /// A red is a claim: "something in THIS tree is wrong." A ledger full of FCS
    /// internal errors (the checker crashed) or of diagnostics against files that are
    /// no longer on disk supports no such claim — and reporting it as exit 1 sends the
    /// reader to look for a defect that is not there. It cost one agent ~40 minutes,
    /// and the incident that taught the opposite lesson (a cached build hiding a REAL
    /// error) looked identical from the outside, so no guidance can separate them.
    ///
    /// So the tool stops picking a side: NO VERDICT (exit 3), same as an unearned
    /// scope. Nothing is claimed broken and nothing is claimed sound, the gate still
    /// refuses, and — the part that closes the loop — the output names `fshw stop`,
    /// which is the only thing that clears this state.
    ///
    /// Never reached while any plugin failed or any diagnostic IS attributable: a
    /// single real red outranks any amount of stale noise beside it.
    | StaleDaemonState of unattributable: int

/// Total exit-code mapping. Exhaustive over every CheckOutcome case — adding a
/// new case is a compile error here, so a new state can never silently fall
/// through to a default exit code.
let exitCode (outcome: CheckOutcome) : int =
    match outcome with
    | CheckOutcome.Clean -> 0
    | CheckOutcome.FailuresFound -> 1
    | CheckOutcome.Incomplete _ -> 2
    // "Waiting on build" is the same class of answer as `Incomplete`: the run
    // could not be completed, not that it failed. Same exit 2.
    | CheckOutcome.WaitingOnBuild _ -> 2
    // A dead test host is the same class of answer again — "could not complete", not
    // "failed". Exit 2, NEVER the 1 it used to return (AUTOMATION-294).
    | CheckOutcome.RunnerAborted _ -> 2
    | CheckOutcome.UnearnedScope _ -> 3
    // Same exit code as an unearned scope, and for the same reason: the run produced
    // no verdict. "I cannot tell" is not a pass and it is not a failure.
    | CheckOutcome.StaleDaemonState _ -> 3

/// EVERYTHING a verdict is computed from. ONE record, both transports.
///
/// The two transports differ ONLY in how they observe the daemon — a socket or an
/// in-process host — and may not differ in what they DECIDE. So they hand over
/// observations, the disjunction (crashed plugin OR failing ledger) is computed once
/// here, and a transport that forgets a term fails to compile rather than going green.
type CheckInputs =
    {
        /// Per-plugin status, as the transport observed it. A plugin in here can be
        /// failing WITHOUT having written a single diagnostic — which is precisely
        /// why this is a field and not something the caller was trusted to fold in.
        PluginStatuses: Map<string, ParsedPluginStatus>
        /// Failing entries in the diagnostic ledger. Whether warnings count is
        /// resolved by the transport (`--no-warn-fail`), because only it knows.
        FailingDiagnostics: int
        /// How many of `FailingDiagnostics` are NOT claims about the tree on disk —
        /// `Verdict.RedCauseKind` says which two cases qualify and why each is decidable.
        /// A SUBSET, always: `UnattributableDiagnostics <= FailingDiagnostics`.
        ///
        /// A separate term rather than a filtered count, because the two questions are
        /// different: `FailingDiagnostics > 0` still means "the ledger is not clean"
        /// (which denies a green), while this one decides whether the run may claim the
        /// tree is BROKEN. Both transports compute it from the same traversal that
        /// produces `redCauses`, so the number that decides the exit code and the reasons
        /// the verdict file records cannot disagree.
        UnattributableDiagnostics: int
        /// Is a test project WAITING ON BUILD — deferred so its tests did not run —
        /// and if so, WHY? Non-green (nothing verified) but NOT a failure. A distinct
        /// term, not folded into `FailingDiagnostics`, so the verdict can route it to
        /// `WaitingOnBuild`/exit 2 instead of a red.
        ///
        /// A `BuildWait` rather than a bool: the two causes need opposite remedies and
        /// a bool cannot carry which one this run has, so the terminal was left to
        /// assert a cause it did not know. Both transports classify it the same way
        /// (`BuildWait.classify` over the `Deferred`-severity ledger messages); a
        /// transport that forgets it fails to compile.
        WaitingOnBuild: BuildWait
        /// Did a test host DIE mid-run — killed, so its tests did not finish — and if
        /// so, with what diagnosis? Non-green (nothing verified) but NOT a failure.
        ///
        /// A distinct term for the same reason `WaitingOnBuild` is one, and enforced the
        /// same way: it is NOT folded into `FailingDiagnostics`, so the verdict can route
        /// it to `RunnerAborted`/exit 2 instead of a red, and a transport that forgets to
        /// supply it fails to compile rather than quietly reporting the old exit 1.
        RunnerAborted: RunnerAbort
        /// Did the run actually check every file it is responsible for? `Unknown` is
        /// never `Complete`.
        Coverage: Coverage
        /// What the tests that ran actually COVERED. `ScopeUnknown` is never full-suite.
        Scope: TestScope
    }

module CheckInputs =
    /// Did any plugin reach a FAILING status?
    ///
    /// THE definition, and both transports ask it rather than each deciding for
    /// themselves. `StatusView.Unreadable` counts: a status this build cannot read is
    /// not a passing status (see `IpcParsing.parseTaggedStatus`), and rounding it down
    /// to idle is how a plugin disappears from the verdict entirely.
    let anyPluginFailed (statuses: Map<string, ParsedPluginStatus>) : bool =
        statuses
        |> Map.exists (fun _ parsed ->
            match parsed.Status with
            | StatusView.Failed _
            | StatusView.Unreadable _ -> true
            | StatusView.Idle
            | StatusView.Running _
            | StatusView.Completed _ -> false)

    /// THE definition of "this run found real problems" — BOTH terms, in ONE place.
    /// A crashed plugin is a failure even with a spotless ledger; a failing ledger is a
    /// failure even with every plugin green.
    let foundProblems (statuses: Map<string, ParsedPluginStatus>) (failingDiagnostics: int) : bool =
        anyPluginFailed statuses || failingDiagnostics > 0

    /// Does this check have failures? The `hasFailures` half of the verdict.
    let hasFailures (inputs: CheckInputs) : bool =
        foundProblems inputs.PluginStatuses inputs.FailingDiagnostics

    /// Are the run's failures ENTIRELY things it cannot attribute to this tree?
    ///
    /// Deliberately conjunctive and deliberately strict. A failing plugin is always a
    /// claim about this tree, so one of those defeats it outright; so does a single
    /// attributable diagnostic. It answers `false` on a clean ledger — "nothing failed"
    /// is not "everything that failed was noise", and a green must never route here.
    let onlyUnattributableFailures (inputs: CheckInputs) : bool =
        not (anyPluginFailed inputs.PluginStatuses)
        && inputs.FailingDiagnostics > 0
        && inputs.UnattributableDiagnostics >= inputs.FailingDiagnostics

/// Pure verdict from (mode, inputs). Every input is REQUIRED, which is what makes "a
/// merge verdict produced from a filtered run" (or from a crashed plugin)
/// unrepresentable rather than merely discouraged.
///
/// Precedence: failures short-circuit, whatever the scope. Then completeness. Then — in
/// `Confirmation` only — scope, where `FullSuite` is the ONLY scope that can reach
/// `Clean`; everything else, including a daemon that simply didn't answer, lands on
/// `UnearnedScope`.
///
/// `InnerLoop` ignores the DEGREE of scope — an impact-filtered green is the answer it
/// wants, and a fast loop that demanded the whole suite would defeat the point. It does
/// NOT ignore the two evidence questions: `NoTestsRun` ("we tested nothing") and
/// `ScopeUnreadable` ("we could not find out whether we tested anything") are refused in
/// both modes.
let verdict (mode: CheckMode) (inputs: CheckInputs) : CheckOutcome =
    let coverage = inputs.Coverage
    let testScope = inputs.Scope

    // AUTOMATION-303. Checked BEFORE `FailuresFound` and nowhere else, because it is a
    // REFINEMENT of it: this branch is only reachable when the run does have failures,
    // and it asks the narrower question the red never asked — are any of them about this
    // tree? A `false` here falls straight through to the red, so the ONLY way to leave
    // `FailuresFound` is to prove every failure unattributable.
    if CheckInputs.onlyUnattributableFailures inputs then
        CheckOutcome.StaleDaemonState inputs.UnattributableDiagnostics
    elif CheckInputs.hasFailures inputs then
        CheckOutcome.FailuresFound
    elif RunnerAbort.isAborted inputs.RunnerAborted then
        // AUTOMATION-294. No real failure, but a test host was KILLED mid-run, so its
        // tests did not finish. Non-green, but "could not complete", never a red.
        //
        // Checked AFTER failures — a genuine failure alongside an abort is still a red,
        // and that ordering is what stops this from laundering one. Checked BEFORE
        // `WaitingOnBuild` because when a run has both, the abort is the fact that
        // explains the other: a box that killed a test host is a box that also lost a
        // build race, and "re-run once the build settles" is advice that never arrives
        // for a machine that is simply out of CPU.
        CheckOutcome.RunnerAborted(RunnerAbort.aborts inputs.RunnerAborted)
    elif BuildWait.isWaiting inputs.WaitingOnBuild then
        // No real failure, but a project's tests DID NOT RUN because its build
        // artifact wasn't ready. Non-green, but "could not complete", never a red.
        // Checked AFTER failures (a genuine failure alongside a defer is still a
        // red) and BEFORE coverage/scope (this answer is more specific than "some
        // files unchecked" or "scope not full"): the run's incompleteness has a
        // known, nameable cause.
        CheckOutcome.WaitingOnBuild(BuildWait.staleDeferrals inputs.WaitingOnBuild)
    else
        match coverage with
        | Complete ->
            // Matched as a PAIR (not a nested `match testScope` per mode), so every
            // (mode, scope) combination is enumerated by the compiler in ONE place and a
            // new scope case is a single edit rather than two lists to keep in step.
            match mode, testScope with
            // NO TESTS RAN — in EITHER mode.
            //
            // `NoTestsRun` does not mean "impact analysis selected nothing this
            // time"; it means the daemon holds NO TEST EVIDENCE AT ALL — no run has
            // completed in this session, or the one that did executed zero tests
            // ("0 passed, 0 failed in 0 projects"). A `check` that goes green on that
            // is the vacuous green in its purest form: nothing was verified, and the
            // exit code said everything was fine.
            //
            // It is not a scope question ("did we test enough?") but an evidence
            // question ("did we test AT ALL?"), so unlike `ImpactFiltered` it is
            // refused in the inner loop too. The inner loop is allowed to test LESS;
            // it is not allowed to test NOTHING and call it green.
            | _, NoTestsRun -> CheckOutcome.UnearnedScope NoTestsRun
            // THE SCOPE COULD NOT BE READ — in EITHER mode.
            //
            // Not "the daemon reported no scope" (that is `ScopeUnknown`, below, and it
            // is a legitimate everyday answer from a repo with no test projects). This
            // is "we asked what ran and the answer faulted": the command threw, the
            // transport dropped, the reply was a shape this build cannot read.
            //
            // A read that faulted tells us NOTHING about what ran, and in particular does
            // not rule out `NoTestsRun` — the state the arm above exists to refuse. Give
            // the two one value and any fault on the read path converts an exit 3 into an
            // exit 0. Same principle as AUTOMATION-150's unreadable ledger: a MISSING
            // reading may not be treated as a GOOD one. So it is refused wherever
            // `NoTestsRun` is, which is everywhere.
            | _, ScopeUnreadable _ -> CheckOutcome.UnearnedScope testScope
            // The inner loop keeps impact filtering, which is what it is good at.
            // `ScopeUnknown` is tolerated here — a repo with no test-prune plugin
            // configured has no tests to run, and punishing it would be nonsense.
            | InnerLoop, (FullSuite _ | ImpactFiltered _ | ScopeUnknown) -> CheckOutcome.Clean
            // `confirm` demands the full suite and accepts nothing narrower.
            | Confirmation, FullSuite _ -> CheckOutcome.Clean
            | Confirmation, (ImpactFiltered _ | ScopeUnknown) -> CheckOutcome.UnearnedScope testScope
        | Incomplete n -> CheckOutcome.Incomplete n
        | Unknown -> CheckOutcome.Incomplete -1

/// Must `confirm` go and PRODUCE the evidence it is about to demand?
///
/// Setting full-suite scope makes the next run unfiltered; it does not make a run
/// HAPPEN. So a `confirm` asked "may I merge this?" on a tree whose suite has not run —
/// a fresh CI checkout, or a warm daemon whose impact DB says nothing changed — would
/// refuse for want of evidence while offering no way to produce any. So `confirm` RUNS
/// the suite it demands, and only then judges it.
///
/// Deliberately expressed as the exact negation of what `verdict` will accept: this
/// says "go and earn a `FullSuite`", `verdict` says "only a `FullSuite` may pass". Two
/// readings of ONE rule (`TestScope.isFullSuite`). If they could drift, `confirm` could
/// force a run it then refused — endless work — or, far worse, skip the run and accept
/// what it never asked for.
///
/// `InnerLoop` never forces: an impact-filtered green IS the answer it wants, and a
/// fast loop that secretly runs the whole suite is not a fast loop.
let confirmNeedsFullRun (mode: CheckMode) (scope: TestScope) : bool =
    match mode with
    | InnerLoop -> false
    | Confirmation -> not (TestScope.isFullSuite scope)

/// Comparable "unchecked" magnitude used for progress tracking across convergence
/// attempts. Complete is 0; Incomplete carries its count; Unknown is the largest
/// possible value, so Unknown→Incomplete counts as progress while Unknown→Unknown does
/// not.
///
/// `internal` (not `private`) so the total mapping is unit-testable directly: `converge`
/// structurally never routes `Complete` here, so that arm is defensive totality only a
/// direct test can pin.
let internal uncheckedMagnitude (coverage: Coverage) : int =
    match coverage with
    | Complete -> 0
    | Incomplete n -> n
    | Unknown -> System.Int32.MaxValue

/// Bounded converge-then-verdict loop.
///
/// `initial` is the read the caller already made. If it is terminal it is returned
/// without re-scanning. Otherwise, up to `maxAttempts` times: trigger a re-scan and
/// re-read, stopping early on any terminal verdict, or when the unchecked magnitude
/// stops shrinking (no progress → `Incomplete`). An exhausted budget is `Incomplete`.
///
/// `UnearnedScope` deliberately does NOT drive convergence: re-scanning cannot widen
/// the scope of a run that already happened. `confirm`'s job there is to report that it
/// has no verdict, loudly — not to keep scanning in the hope of a different answer.
let converge
    (mode: CheckMode)
    (maxAttempts: int)
    (triggerScan: unit -> unit)
    (reread: unit -> CheckInputs)
    (initial: CheckInputs)
    : CheckOutcome =
    let initOutcome = verdict mode initial

    match initOutcome with
    | CheckOutcome.FailuresFound
    | CheckOutcome.Clean
    // `WaitingOnBuild` is terminal here for the same reason as `UnearnedScope`:
    // re-scanning does not retroactively run a test the settled run already
    // deferred. exit 2 says "could not complete — retry", which is the answer.
    | CheckOutcome.WaitingOnBuild _
    // Terminal, and DELIBERATELY not retried (AUTOMATION-294). A re-scan cannot un-kill
    // a host, and re-running the check inside the same convergence loop would re-run it
    // under the same load that killed it — buying, at best, a slower identical answer.
    //
    // It is also the retry that must NOT be built here. An automatic retry cannot tell a
    // host killed by a busy box from a host that aborts every time because something is
    // genuinely broken, so a loop that retried until it got a verdict would convert a
    // real crash into a slow green. Reporting the abort honestly, once, keeps that
    // distinction in the hands of the reader — who can see whether the machine was busy.
    | CheckOutcome.RunnerAborted _
    | CheckOutcome.UnearnedScope _
    // Terminal for the same reason: a re-scan does not clear stale daemon state. That
    // is the whole finding — `fshw scan` was the DOCUMENTED remedy for the FCS-fault
    // class and never cleared it once; only `fshw stop` did. Re-scanning here would
    // spend three more full passes to arrive at the same answer.
    | CheckOutcome.StaleDaemonState _ -> initOutcome
    | CheckOutcome.Incomplete _ ->
        // Enter convergence. `prevMagnitude` is the unchecked magnitude we're
        // trying to improve on; it starts at the initial read.
        let rec loop (attempt: int) (prevMagnitude: int) =
            if attempt > maxAttempts then
                // Budget exhausted without reaching Complete.
                CheckOutcome.Incomplete prevMagnitude
            else
                triggerScan ()
                let inputs = reread ()

                // Every re-read goes through the SAME `verdict` as the first one — the
                // convergence loop holds no second opinion about what a read MEANS.
                match verdict mode inputs with
                | CheckOutcome.Incomplete _ as incomplete ->
                    let magnitude = uncheckedMagnitude inputs.Coverage

                    if magnitude >= prevMagnitude then
                        // No progress: the re-scan did not reduce the unchecked
                        // count. Stop — genuinely un-completable.
                        incomplete
                    else
                        loop (attempt + 1) magnitude
                | terminal -> terminal

        loop 1 (uncheckedMagnitude initial.Coverage)
