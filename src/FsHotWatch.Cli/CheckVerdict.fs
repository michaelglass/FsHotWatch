module FsHotWatch.Cli.CheckVerdict

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

/// WHY the check is running — and therefore what it is allowed to claim
/// (AUTOMATION-112).
///
/// These are two different questions, and the whole bug was answering the second with
/// the first:
///
///   `InnerLoop` — "did my change break anything it plausibly touches?" An
///   impact-filtered run answers this well, and that is what impact filtering is
///   genuinely FOR: it is a LATENCY OPTIMIZATION.
///
///   `MergeGate` — "is the suite green?" That is a CORRECTNESS CLAIM, and a heuristic
///   selector may not be its sole basis unless the selector is PROVEN sound. Ours
///   demonstrably isn't: 35 tests sat red on `main` for an unknown period, never
///   selected, gate green throughout.
///
/// Enforced in the TYPE, not by convention. "Remember to also run an unfiltered
/// test-rerun before merging" is precisely the discipline that has already failed —
/// a gate that depends on someone remembering is not a gate. So `MergeGate` demands a
/// `FullSuite` scope as EVIDENCE and has no branch that can reach `Clean` without one.
type CheckMode =
    /// The inner dev loop. Keeps impact filtering, which is what it is good at.
    | InnerLoop
    /// The merge gate. Only a full-suite run can produce a clean verdict.
    | MergeGate

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
    /// A MERGE-GATE run whose tests did not cover the whole suite. Nothing failed —
    /// but the run did not produce the evidence a merge verdict is made of, so there
    /// is no verdict to give. Distinct from `FailuresFound` (nothing is known to be
    /// broken) and from `Clean` (nothing is known to be sound, either): the gate is
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

/// Pure verdict from (mode, hasFailures, coverage, testScope). Every input is
/// REQUIRED — a verdict cannot be computed without knowing what the tests covered,
/// which is what makes "a merge verdict produced from a filtered run" unrepresentable
/// rather than merely discouraged.
///
/// Failures short-circuit (a real problem is reported immediately, whatever the
/// scope). Then completeness. Then — in `MergeGate` only — scope: `FullSuite` is the
/// ONLY scope that can reach `Clean`. `ImpactFiltered`, `NoTestsRun` and `ScopeUnknown`
/// all land on `UnearnedScope`, including the cross-version case where the daemon
/// simply didn't answer: an unknown scope is not a full-suite scope.
///
/// `InnerLoop` ignores the scope entirely — an impact-filtered green is exactly the
/// answer it wants, and making the fast loop demand the whole suite would defeat the
/// point of having one.
let verdict (mode: CheckMode) (hasFailures: bool) (coverage: Coverage) (testScope: TestScope) : CheckOutcome =
    if hasFailures then
        CheckOutcome.FailuresFound
    else
        match coverage with
        | Complete ->
            match mode with
            | InnerLoop -> CheckOutcome.Clean
            | MergeGate ->
                match testScope with
                | FullSuite _ -> CheckOutcome.Clean
                | ImpactFiltered _
                | NoTestsRun
                | ScopeUnknown -> CheckOutcome.UnearnedScope testScope
        | Incomplete n -> CheckOutcome.Incomplete n
        | Unknown -> CheckOutcome.Incomplete -1

/// Comparable "unchecked" magnitude used for progress tracking across
/// convergence attempts. Complete is 0; Incomplete carries its count; Unknown
/// is treated as the largest possible value so that an Unknown→Incomplete
/// transition counts as progress, while Unknown→Unknown does not.
let private uncheckedMagnitude (coverage: Coverage) : int =
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
///                      MergeGate if the run was not full-suite
///   - else, if the unchecked magnitude did not shrink vs the previous attempt,
///     break (no progress) and report Incomplete (exit 2).
/// After the budget is exhausted, report Incomplete (exit 2).
///
/// `UnearnedScope` deliberately does NOT drive convergence: re-scanning cannot widen
/// the scope of a run that already happened. The gate's job there is to report that it
/// has no verdict, loudly — not to keep scanning in the hope of a different answer.
let converge
    (mode: CheckMode)
    (maxAttempts: int)
    (triggerScan: unit -> unit)
    (reread: unit -> bool * Coverage * TestScope)
    (initial: bool * Coverage * TestScope)
    : CheckOutcome =
    let (initFailures, initCoverage, initScope) = initial
    let initOutcome = verdict mode initFailures initCoverage initScope

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
                let (failures, coverage, scope) = reread ()

                if failures then
                    CheckOutcome.FailuresFound
                else
                    match coverage with
                    | Complete -> verdict mode false coverage scope
                    | _ ->
                        let magnitude = uncheckedMagnitude coverage

                        if magnitude >= prevMagnitude then
                            // No progress: the re-scan did not reduce the
                            // unchecked count. Stop — genuinely un-completable.
                            verdict mode false coverage scope
                        else
                            loop (attempt + 1) magnitude

        loop 1 (uncheckedMagnitude initCoverage)
