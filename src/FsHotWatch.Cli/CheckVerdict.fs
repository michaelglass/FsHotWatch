module FsHotWatch.Cli.CheckVerdict

open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing

// ----------------------------------------------------------------------------
// Converge-then-verdict completeness guarantee for `fshw check`.
//
// The exit code reflects not just "were failures found" but "did the daemon
// actually check every file it's responsible for". A scan that left files
// unchecked (cancellation race, retries exhausted) must NEVER read as a clean
// exit 0 for a programmatic consumer (CI/agent).
//
// Structural guarantees:
//  1. The exit code is a TOTAL function over an explicit CheckOutcome — no
//     wildcard/default branch that could swallow a new case.
//  2. Coverage is a REQUIRED input to `verdict` (see DiagnosticsResponse.Coverage,
//     which defaults a missing/unparseable field to `Unknown`). `Unknown` is
//     never mapped to Clean.
//  3. `failures found` short-circuits to FailuresFound (exit 1) BEFORE any
//     convergence — real problems are reported immediately.
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
    /// A CONFIRMATION run whose tests did not cover the whole suite. Nothing failed —
    /// but the run did not produce the evidence a merge verdict is made of, so there
    /// is no verdict to give. Distinct from `FailuresFound` (nothing is known to be
    /// broken) and from `Clean` (nothing is known to be sound, either): the run is
    /// owed work it did not discharge. Unreachable in `InnerLoop`, by construction.
    | UnearnedScope of TestScope

/// Total exit-code mapping. Exhaustive over every CheckOutcome case — adding a
/// new case is a compile error here, so a new state can never silently fall
/// through to a default exit code.
let exitCode (outcome: CheckOutcome) : int =
    match outcome with
    | CheckOutcome.Clean -> 0
    | CheckOutcome.FailuresFound -> 1
    | CheckOutcome.Incomplete _ -> 2
    | CheckOutcome.UnearnedScope _ -> 3

/// EVERYTHING a verdict is computed from. ONE record, both transports.
///
/// The two transports differ ONLY in how they observe the daemon — a socket or an
/// in-process host. They may not differ in what they DECIDE. So the observations are
/// what they hand over, the disjunction (crashed plugin OR failing ledger) is
/// computed once, here, and a transport that forgets a term no longer produces a
/// green: it fails to compile.
type CheckInputs =
    {
        /// Per-plugin status, as the transport observed it. A plugin in here can be
        /// failing WITHOUT having written a single diagnostic — which is precisely
        /// why this is a field and not something the caller was trusted to fold in.
        PluginStatuses: Map<string, ParsedPluginStatus>
        /// Failing entries in the diagnostic ledger. Whether warnings count is
        /// resolved by the transport (`--no-warn-fail`), because only it knows.
        FailingDiagnostics: int
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

/// Pure verdict from (mode, inputs). Every input is REQUIRED — a verdict cannot be
/// computed without knowing what the plugins did, what the ledger says, what was
/// checked, and what the tests covered. That is what makes "a merge verdict produced
/// from a filtered run" (or from a crashed plugin) unrepresentable rather than merely
/// discouraged.
///
/// Failures short-circuit (a real problem is reported immediately, whatever the
/// scope). Then completeness. Then — in `Confirmation` only — scope: `FullSuite` is the
/// ONLY scope that can reach `Clean`. `ImpactFiltered`, `NoTestsRun` and `ScopeUnknown`
/// all land on `UnearnedScope`, including the cross-version case where the daemon
/// simply didn't answer: an unknown scope is not a full-suite scope.
///
/// `InnerLoop` ignores the scope entirely — an impact-filtered green is exactly the
/// answer it wants, and making the fast loop demand the whole suite would defeat the
/// point of having one.
let verdict (mode: CheckMode) (inputs: CheckInputs) : CheckOutcome =
    let coverage = inputs.Coverage
    let testScope = inputs.Scope

    if CheckInputs.hasFailures inputs then
        CheckOutcome.FailuresFound
    else
        match coverage with
        | Complete ->
            match testScope with
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
            | NoTestsRun -> CheckOutcome.UnearnedScope NoTestsRun
            | FullSuite _
            | ImpactFiltered _
            | ScopeUnknown ->
                match mode with
                // The inner loop keeps impact filtering, which is what it is good at.
                // `ScopeUnknown` is tolerated here — a repo with no test-prune plugin
                // configured has no tests to run, and punishing it would be nonsense.
                | InnerLoop -> CheckOutcome.Clean
                | Confirmation ->
                    match testScope with
                    | FullSuite _ -> CheckOutcome.Clean
                    | ImpactFiltered _
                    | NoTestsRun
                    | ScopeUnknown -> CheckOutcome.UnearnedScope testScope
        | Incomplete n -> CheckOutcome.Incomplete n
        | Unknown -> CheckOutcome.Incomplete -1

/// Must `confirm` go and PRODUCE the evidence it is about to demand?
///
/// A demand nobody can satisfy is not a check, it is an obstacle. Setting
/// full-suite scope makes the next run unfiltered; it does not make a run HAPPEN.
/// So a `confirm` asked "may I merge this?" on a tree whose suite has not run — a
/// fresh CI checkout, or a warm daemon whose impact DB says nothing changed — would
/// refuse for want of evidence while offering no way to produce any. So `confirm`
/// RUNS the suite it demands, and only then judges it.
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

/// Comparable "unchecked" magnitude used for progress tracking across
/// convergence attempts. Complete is 0; Incomplete carries its count; Unknown
/// is treated as the largest possible value so that an Unknown→Incomplete
/// transition counts as progress, while Unknown→Unknown does not.
///
/// `internal` (not `private`) so the total mapping is unit-testable directly:
/// `converge` structurally never routes `Complete` here (a Complete read
/// resolves to a verdict before any magnitude comparison), so the `Complete`
/// arm is defensive totality that only a direct test can pin.
let internal uncheckedMagnitude (coverage: Coverage) : int =
    match coverage with
    | Complete -> 0
    | Incomplete n -> n
    | Unknown -> System.Int32.MaxValue

/// Bounded converge-then-verdict loop.
///
/// `initial` is the (hasFailures, coverage, testScope) already read by the caller
/// before converging. If that initial read is terminal (failures, or a Complete read
/// that satisfies the mode), it is returned without re-scanning. Otherwise, up to
/// `maxAttempts` times: trigger a re-scan, re-read, and:
///   - failures      -> FailuresFound (exit 1)
///   - Complete      -> Clean         (exit 0), or UnearnedScope (exit 3) under
///                      Confirmation if the run was not full-suite
///   - else, if the unchecked magnitude did not shrink vs the previous attempt,
///     break (no progress) and report Incomplete (exit 2).
/// After the budget is exhausted, report Incomplete (exit 2).
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
    | CheckOutcome.UnearnedScope _ -> initOutcome
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
