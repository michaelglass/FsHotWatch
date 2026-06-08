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

/// Total exit-code mapping. Exhaustive over every CheckOutcome case — adding a
/// new case is a compile error here, so a new state can never silently fall
/// through to a default exit code.
let exitCode (outcome: CheckOutcome) : int =
    match outcome with
    | CheckOutcome.Clean -> 0
    | CheckOutcome.FailuresFound -> 1
    | CheckOutcome.Incomplete _ -> 2

/// Pure verdict from (hasFailures, coverage). Coverage is required. Failures
/// short-circuit to FailuresFound. `Unknown` maps to an Incomplete outcome
/// (`-1` sentinel) — it MUST NOT map to Clean.
let verdict (hasFailures: bool) (coverage: Coverage) : CheckOutcome =
    if hasFailures then
        CheckOutcome.FailuresFound
    else
        match coverage with
        | Complete -> CheckOutcome.Clean
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
/// `initial` is the (hasFailures, coverage) already read by the caller before
/// converging. If that initial read is terminal (failures, or Complete), it is
/// returned without re-scanning. Otherwise, up to `maxAttempts` times: trigger a
/// re-scan, re-read (hasFailures, coverage), and:
///   - failures      -> FailuresFound (exit 1)
///   - Complete      -> Clean         (exit 0)
///   - else, if the unchecked magnitude did not shrink vs the previous attempt,
///     break (no progress) and report Incomplete (exit 2).
/// After the budget is exhausted, report Incomplete (exit 2).
let converge
    (maxAttempts: int)
    (triggerScan: unit -> unit)
    (reread: unit -> bool * Coverage)
    (initial: bool * Coverage)
    : CheckOutcome =
    let (initFailures, initCoverage) = initial
    let initOutcome = verdict initFailures initCoverage

    match initOutcome with
    | CheckOutcome.FailuresFound
    | CheckOutcome.Clean -> initOutcome
    | CheckOutcome.Incomplete _ ->
        // Enter convergence. `prevMagnitude` is the unchecked magnitude we're
        // trying to improve on; it starts at the initial read.
        let rec loop (attempt: int) (prevMagnitude: int) =
            if attempt > maxAttempts then
                // Budget exhausted without reaching Complete.
                CheckOutcome.Incomplete prevMagnitude
            else
                triggerScan ()
                let (failures, coverage) = reread ()

                if failures then
                    CheckOutcome.FailuresFound
                else
                    match coverage with
                    | Complete -> CheckOutcome.Clean
                    | _ ->
                        let magnitude = uncheckedMagnitude coverage

                        if magnitude >= prevMagnitude then
                            // No progress: the re-scan did not reduce the
                            // unchecked count. Stop — genuinely un-completable.
                            verdict false coverage
                        else
                            loop (attempt + 1) magnitude

        loop 1 (uncheckedMagnitude initCoverage)
