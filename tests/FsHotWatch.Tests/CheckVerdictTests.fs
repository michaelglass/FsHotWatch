module FsHotWatch.Tests.CheckVerdictTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.CheckVerdict

// ----------------------------------------------------------------------------
// Pure verdict mapping. The exit code is a TOTAL function over an explicit
// CheckOutcome — no wildcard branch can swallow a new case. Coverage is a
// REQUIRED input. Failures short-circuit BEFORE convergence.
// ----------------------------------------------------------------------------

// hasFailures = true short-circuits to FailuresFound regardless of coverage.

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Complete -> FailuresFound`` () =
    test <@ verdict true Complete = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Incomplete -> FailuresFound (failures short-circuit)`` () =
    test <@ verdict true (Incomplete 5) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Unknown -> FailuresFound`` () =
    test <@ verdict true Unknown = CheckOutcome.FailuresFound @>

// no failures: coverage decides.

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Complete -> Clean`` () =
    test <@ verdict false Complete = CheckOutcome.Clean @>

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Incomplete -> Incomplete`` () =
    test <@ verdict false (Incomplete 3) = CheckOutcome.Incomplete 3 @>

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Unknown -> Incomplete (Unknown never reads as Clean)`` () =
    // Unknown must enter convergence, never map to Clean. We model that by
    // mapping it to an Incomplete outcome (unchecked count unknown -> -1).
    match verdict false Unknown with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

// exitCode is a total mapping 0/1/2.

[<Fact(Timeout = 15000)>]
let ``exitCode: Clean -> 0`` () =
    test <@ exitCode CheckOutcome.Clean = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCode: FailuresFound -> 1`` () =
    test <@ exitCode CheckOutcome.FailuresFound = 1 @>

[<Fact(Timeout = 15000)>]
let ``exitCode: Incomplete -> 2`` () =
    test <@ exitCode (CheckOutcome.Incomplete 4) = 2 @>

// Unknown must NEVER produce exit 0.

[<Fact(Timeout = 15000)>]
let ``Unknown coverage with no failures never yields exit 0`` () =
    let code = exitCode (verdict false Unknown)
    test <@ code <> 0 @>

// ----------------------------------------------------------------------------
// Convergence loop. Injected with a scripted sequence of (coverage, hasFailures)
// responses + a triggerScan stub. When the outcome is Incomplete/Unknown AND no
// failures, it re-scans and re-reads, bounded to 3 attempts, breaking on
// no-progress (unchecked count didn't decrease).
// ----------------------------------------------------------------------------

/// Build a re-read stub that returns scripted (hasFailures, coverage) values in
/// order, repeating the last one once exhausted. Also counts triggerScan calls.
let private scripted (responses: (bool * Coverage) list) =
    let queue = System.Collections.Generic.Queue<bool * Coverage>(responses)
    let mutable last = List.last responses
    let scans = ref 0

    let triggerScan () = incr scans

    let reread () =
        if queue.Count > 0 then
            last <- queue.Dequeue()

        last

    (triggerScan, reread, scans)

[<Fact(Timeout = 15000)>]
let ``converge: already complete after first re-read -> Clean`` () =
    let (triggerScan, reread, scans) = scripted [ (false, Complete) ]
    let outcome = converge 3 triggerScan reread (false, Incomplete 2)
    test <@ outcome = CheckOutcome.Clean @>
    test <@ scans.Value >= 1 @>

[<Fact(Timeout = 15000)>]
let ``converge: failures appear mid-convergence -> FailuresFound`` () =
    // attempt 1: still incomplete (shrinking); attempt 2: a failure surfaces.
    let (triggerScan, reread, _) =
        scripted [ (false, Incomplete 3); (true, Incomplete 1) ]

    let outcome = converge 3 triggerScan reread (false, Incomplete 5)
    test <@ outcome = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``converge: no progress (5,5,5) -> Incomplete`` () =
    let (triggerScan, reread, _) =
        scripted [ (false, Incomplete 5); (false, Incomplete 5); (false, Incomplete 5) ]

    let outcome = converge 3 triggerScan reread (false, Incomplete 5)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

[<Fact(Timeout = 15000)>]
let ``converge: progress then complete (5 -> 2 -> 0) -> Clean`` () =
    let (triggerScan, reread, scans) =
        scripted [ (false, Incomplete 2); (false, Complete) ]

    let outcome = converge 3 triggerScan reread (false, Incomplete 5)
    test <@ outcome = CheckOutcome.Clean @>
    test <@ scans.Value = 2 @>

[<Fact(Timeout = 15000)>]
let ``converge: bounded to 3 attempts when shrinking but never complete`` () =
    // Always reports a smaller-but-nonzero count, so progress never stalls; the
    // attempt budget must cap it.
    let (triggerScan, reread, scans) =
        scripted
            [ (false, Incomplete 4)
              (false, Incomplete 3)
              (false, Incomplete 2)
              (false, Incomplete 1) ]

    let outcome = converge 3 triggerScan reread (false, Incomplete 5)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

    test <@ scans.Value = 3 @>

[<Fact(Timeout = 15000)>]
let ``converge: Unknown that stays Unknown -> Incomplete (never Clean)`` () =
    let (triggerScan, reread, _) =
        scripted [ (false, Unknown); (false, Unknown); (false, Unknown) ]

    let outcome = converge 3 triggerScan reread (false, Unknown)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

[<Fact(Timeout = 15000)>]
let ``converge: Unknown then complete -> Clean`` () =
    let (triggerScan, reread, _) = scripted [ (false, Complete) ]
    let outcome = converge 3 triggerScan reread (false, Unknown)
    test <@ outcome = CheckOutcome.Clean @>
