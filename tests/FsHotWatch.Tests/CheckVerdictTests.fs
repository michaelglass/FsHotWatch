module FsHotWatch.Tests.CheckVerdictTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.CheckVerdict

/// The scope these coverage-convergence tests are indifferent to. Convergence is
/// about COMPLETENESS (did every file get checked), not about what the tests
/// covered — so these cases pin the InnerLoop mode, which ignores the scope
/// entirely. Scope itself is pinned by the Confirmation tests below.
let private anyScope = FullSuite 1

/// A plugin whose status is `status` and which has NOTHING else to say — no run
/// record, no diagnostics. The shape of a plugin the framework's crash-net forced to
/// `Failed`: it threw before it could report anything.
let private statusOf (status: StatusView) : Map<string, ParsedPluginStatus> =
    Map.ofList
        [ "boom",
          { Status = status
            Subtasks = []
            ActivityTail = []
            LastRun = None
            Diagnostics = DiagnosticCounts.empty } ]

/// The verdict's inputs. `hasFailures` here means a failing DIAGNOSTIC only; the OTHER
/// term — a plugin that failed without writing a diagnostic — has its own tests below,
/// because it is the one that greened CI.
let private inputs (hasFailures: bool) (coverage: Coverage) (scope: TestScope) : CheckInputs =
    { PluginStatuses = Map.empty
      FailingDiagnostics = (if hasFailures then 1 else 0)
      WaitingOnBuild = false
      Coverage = coverage
      Scope = scope }

// ----------------------------------------------------------------------------
// THE MISSING TERM. `hasFailures` was computed by each transport, and the run-once
// transport computed only half of it.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``verdict: a plugin that FAILED with a spotless ledger is FailuresFound, in BOTH modes`` () =
    // A plugin reaches `Failed` without writing an `ErrorEntry` every time it throws —
    // PluginFramework's crash-nets force exactly that, and cannot invent a file and
    // line for someone else's stack trace. Complete coverage, full suite, empty ledger,
    // and the check DID NOT RUN.
    let crashed =
        { PluginStatuses = statusOf (StatusView.Failed("plugin exploded", DateTime.UtcNow))
          FailingDiagnostics = 0
          WaitingOnBuild = false
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop crashed = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation crashed = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: a plugin in a status this build cannot READ is FailuresFound, never Clean`` () =
    // The cross-version case, now that `PluginOutcome` has gained `Wedged`: a newer
    // daemon reporting a state we have no name for. An unknown state is not a passing
    // state.
    let unreadable =
        { PluginStatuses = statusOf (StatusView.Unreadable "a status tag this build does not recognize")
          FailingDiagnostics = 0
          WaitingOnBuild = false
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop unreadable = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation unreadable = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: a healthy plugin map does not manufacture a failure`` () =
    // The control. Without it, a `hasFailures` that simply returned `true` would pass
    // both tests above and prove nothing.
    let healthy =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 0
          WaitingOnBuild = false
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop healthy = CheckOutcome.Clean @>
    test <@ verdict Confirmation healthy = CheckOutcome.Clean @>

// ----------------------------------------------------------------------------
// 224 — "waiting on build" is INCOMPLETE (exit 2), never a red (exit 1). A test
// project deferred because its build artifact wasn't produced did not run: nothing
// was verified (non-green), nothing failed (not a red). It must route to exit 2 so
// a deploy preflight retries rather than reading a test failure.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``verdict: waiting on build with NO failures is WaitingOnBuild / exit 2, never a red`` () =
    // The plugin reports a NON-failing terminal for a pure defer, and the deferred
    // diagnostic is `Deferred` severity — so FailingDiagnostics is 0 and no plugin is
    // Failed. The distinct `WaitingOnBuild` term carries the non-green.
    let waiting =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 0
          WaitingOnBuild = true
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop waiting = CheckOutcome.WaitingOnBuild @>
    test <@ verdict Confirmation waiting = CheckOutcome.WaitingOnBuild @>
    test <@ exitCode (verdict InnerLoop waiting) = 2 @>
    test <@ exitCode (verdict Confirmation waiting) = 2 @>

[<Fact(Timeout = 15000)>]
let ``verdict: a REAL failure alongside waiting on build still short-circuits to FailuresFound / exit 1`` () =
    // A defer never LAUNDERS a red. Failures are checked first, so a genuine failing
    // diagnostic (or a crashed plugin) dominates a co-occurring defer.
    let failureAndWaiting =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 1
          WaitingOnBuild = true
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop failureAndWaiting = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation failureAndWaiting = CheckOutcome.FailuresFound @>
    test <@ exitCode (verdict InnerLoop failureAndWaiting) = 1 @>

[<Fact(Timeout = 15000)>]
let ``verdict: a clean full run is still Clean / exit 0 — no regression from the waiting-on-build term`` () =
    let clean = inputs false Complete (FullSuite 4)
    test <@ verdict Confirmation clean = CheckOutcome.Clean @>
    test <@ exitCode (verdict Confirmation clean) = 0 @>

// ----------------------------------------------------------------------------
// Pure verdict mapping. The exit code is a TOTAL function over an explicit
// CheckOutcome — no wildcard branch can swallow a new case. Coverage is a
// REQUIRED input. Failures short-circuit BEFORE convergence.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Complete -> FailuresFound`` () =
    test <@ verdict InnerLoop (inputs true Complete anyScope) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Incomplete -> FailuresFound (failures short-circuit)`` () =
    test <@ verdict InnerLoop (inputs true (Incomplete 5) anyScope) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: failures + Unknown -> FailuresFound`` () =
    test <@ verdict InnerLoop (inputs true Unknown anyScope) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Complete -> Clean`` () =
    test <@ verdict InnerLoop (inputs false Complete anyScope) = CheckOutcome.Clean @>

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Incomplete -> Incomplete`` () =
    test <@ verdict InnerLoop (inputs false (Incomplete 3) anyScope) = CheckOutcome.Incomplete 3 @>

[<Fact(Timeout = 15000)>]
let ``verdict: clean + Unknown -> Incomplete (Unknown never reads as Clean)`` () =
    // Unknown must enter convergence, never map to Clean; modelled as an Incomplete
    // outcome whose unchecked count is unknown (-1).
    match verdict InnerLoop (inputs false Unknown anyScope) with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

[<Fact(Timeout = 15000)>]
let ``exitCode: Clean -> 0`` () =
    test <@ exitCode CheckOutcome.Clean = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCode: FailuresFound -> 1`` () =
    test <@ exitCode CheckOutcome.FailuresFound = 1 @>

[<Fact(Timeout = 15000)>]
let ``exitCode: Incomplete -> 2`` () =
    test <@ exitCode (CheckOutcome.Incomplete 4) = 2 @>

[<Fact(Timeout = 15000)>]
let ``Unknown coverage with no failures never yields exit 0`` () =
    let code = exitCode (verdict InnerLoop (inputs false Unknown anyScope))
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

        let (failures, coverage) = last
        inputs failures coverage anyScope

    (triggerScan, reread, scans)

[<Fact(Timeout = 15000)>]
let ``converge: already complete after first re-read -> Clean`` () =
    let (triggerScan, reread, scans) = scripted [ (false, Complete) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false (Incomplete 2) anyScope)

    test <@ outcome = CheckOutcome.Clean @>
    test <@ scans.Value >= 1 @>

[<Fact(Timeout = 15000)>]
let ``converge: failures appear mid-convergence -> FailuresFound`` () =
    // attempt 1: still incomplete (shrinking); attempt 2: a failure surfaces.
    let (triggerScan, reread, _) =
        scripted [ (false, Incomplete 3); (true, Incomplete 1) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false (Incomplete 5) anyScope)

    test <@ outcome = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``converge: no progress (5,5,5) -> Incomplete`` () =
    let (triggerScan, reread, _) =
        scripted [ (false, Incomplete 5); (false, Incomplete 5); (false, Incomplete 5) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false (Incomplete 5) anyScope)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

[<Fact(Timeout = 15000)>]
let ``converge: progress then complete (5 -> 2 -> 0) -> Clean`` () =
    let (triggerScan, reread, scans) =
        scripted [ (false, Incomplete 2); (false, Complete) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false (Incomplete 5) anyScope)

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

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false (Incomplete 5) anyScope)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

    test <@ scans.Value = 3 @>

[<Fact(Timeout = 15000)>]
let ``converge: Unknown that stays Unknown -> Incomplete (never Clean)`` () =
    let (triggerScan, reread, _) =
        scripted [ (false, Unknown); (false, Unknown); (false, Unknown) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false Unknown anyScope)

    match outcome with
    | CheckOutcome.Incomplete _ -> ()
    | other -> failwithf "expected Incomplete, got %A" other

[<Fact(Timeout = 15000)>]
let ``converge: Unknown then complete -> Clean`` () =
    let (triggerScan, reread, _) = scripted [ (false, Complete) ]

    let outcome =
        converge InnerLoop 3 triggerScan reread (inputs false Unknown anyScope)

    test <@ outcome = CheckOutcome.Clean @>

// ----------------------------------------------------------------------------
// AUTOMATION-112 — a merge verdict cannot be produced from an impact-filtered run.
//
// Impact filtering is a latency optimization; a merge decision is a correctness claim.
// An impact-filtered green means "your change didn't break anything I chose to look
// at", not "the suite is green". These tests pin the difference in the TYPE, because
// "remember to also run an unfiltered rerun before merging" is the discipline that
// already failed.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``Confirmation: a full-suite run with no failures is the ONLY route to Clean`` () =
    test <@ verdict Confirmation (inputs false Complete (FullSuite 4)) = CheckOutcome.Clean @>

[<Fact(Timeout = 15000)>]
let ``Confirmation: an impact-filtered run cannot yield Clean`` () =
    // The exact shape of the bug: nothing failed, coverage complete — and the run
    // looked at a selected subset. That is not a merge verdict.
    let outcome = verdict Confirmation (inputs false Complete (ImpactFiltered(1, 4)))

    test <@ outcome = CheckOutcome.UnearnedScope(ImpactFiltered(1, 4)) @>
    test <@ exitCode outcome <> 0 @>

[<Fact(Timeout = 15000)>]
let ``Confirmation: a run that executed no tests at all cannot yield Clean`` () =
    // AUTOMATION-108's shape: the daemon skipped the run (cached/baseline-equivalent)
    // and nothing ran. 35 tests were red on `main` throughout. "No tests ran" is not
    // evidence of a green suite.
    let outcome = verdict Confirmation (inputs false Complete NoTestsRun)

    test <@ outcome = CheckOutcome.UnearnedScope NoTestsRun @>
    test <@ exitCode outcome <> 0 @>

[<Fact(Timeout = 15000)>]
let ``Confirmation: an unknown scope cannot yield Clean`` () =
    // The cross-version backstop. An old daemon, an absent test-prune plugin, a
    // transport fault — every one of them lands here, and none of them is a
    // full-suite run. `confirm` goes green only on a scope it POSITIVELY established.
    let outcome = verdict Confirmation (inputs false Complete ScopeUnknown)

    test <@ outcome = CheckOutcome.UnearnedScope ScopeUnknown @>
    test <@ exitCode outcome <> 0 @>

[<Fact(Timeout = 15000)>]
let ``Confirmation: real failures still short-circuit ahead of scope`` () =
    test <@ verdict Confirmation (inputs true Complete (ImpactFiltered(1, 4))) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``Confirmation: incomplete coverage still outranks scope`` () =
    test <@ verdict Confirmation (inputs false (Incomplete 7) (FullSuite 4)) = CheckOutcome.Incomplete 7 @>

[<Fact(Timeout = 15000)>]
let ``InnerLoop: an impact-filtered run IS Clean — that is what filtering is for`` () =
    // The fast loop keeps its optimization; `confirm` is where the merge claim is made.
    test <@ verdict InnerLoop (inputs false Complete (ImpactFiltered(1, 4))) = CheckOutcome.Clean @>
    // A repo with no test-prune plugin has no tests to run, and punishing it would be
    // nonsense. `ScopeUnknown` is "we cannot say", not "we ran nothing".
    test <@ verdict InnerLoop (inputs false Complete ScopeUnknown) = CheckOutcome.Clean @>

[<Fact(Timeout = 15000)>]
let ``InnerLoop: NO TESTS RAN is never Clean — the inner loop may test LESS, not NOTHING`` () =
    // AUTOMATION-129. `NoTestsRun` does NOT mean "impact analysis selected nothing this
    // time". It means the daemon holds NO TEST EVIDENCE AT ALL — no run has completed
    // in this session, or the one that did executed zero tests ("0 passed, 0 failed in
    // 0 projects"). This assertion once read `= CheckOutcome.Clean`, which is the
    // vacuous green in its purest form.
    //
    // So it is refused in BOTH modes: impact filtering is a question of HOW MUCH we
    // tested, this is a question of WHETHER WE TESTED AT ALL. The inner loop may test
    // less; it may not test nothing and call it green.
    test <@ verdict InnerLoop (inputs false Complete NoTestsRun) = CheckOutcome.UnearnedScope NoTestsRun @>
    test <@ verdict Confirmation (inputs false Complete NoTestsRun) = CheckOutcome.UnearnedScope NoTestsRun @>

    // ...but a REAL failure still outranks it: a red is a red, and reporting "no
    // verdict" would bury it.
    test <@ verdict InnerLoop (inputs true Complete NoTestsRun) = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``exitCode: UnearnedScope is its own code, distinct from failure and incompleteness`` () =
    // A merge that has no verdict is not the same event as a merge that failed, and
    // an autonomous caller must be able to tell them apart.
    let unearned = exitCode (CheckOutcome.UnearnedScope(ImpactFiltered(1, 4)))

    test <@ unearned = 3 @>
    test <@ unearned <> exitCode CheckOutcome.Clean @>
    test <@ unearned <> exitCode CheckOutcome.FailuresFound @>
    test <@ unearned <> exitCode (CheckOutcome.Incomplete 1) @>

[<Fact(Timeout = 15000)>]
let ``converge: a Confirmation never scans its way out of an unearned scope`` () =
    // Re-scanning cannot widen the scope of a run that already happened. `confirm`'s
    // job is to report that it has no verdict — not to keep scanning for a better one.
    let filtered = inputs false Complete (ImpactFiltered(1, 4))
    let scans = ref 0
    let triggerScan () = incr scans
    let reread () = filtered

    let outcome = converge Confirmation 3 triggerScan reread filtered

    test <@ outcome = CheckOutcome.UnearnedScope(ImpactFiltered(1, 4)) @>
    test <@ scans.Value = 0 @>

// --- TestScope parsing: the daemon's answer, and every way it can fail to give one ---

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: a full-suite reply parses as FullSuite`` () =
    let json = """{"scope":"full","ranProjects":3,"totalProjects":3}"""
    test <@ (parseTestRunReport json).Scope = FullSuite 3 @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: a filtered reply parses as ImpactFiltered`` () =
    let json = """{"scope":"filtered","ranProjects":1,"totalProjects":3}"""
    test <@ (parseTestRunReport json).Scope = ImpactFiltered(1, 3) @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: a none reply parses as NoTestsRun`` () =
    let json = """{"scope":"none","ranProjects":0,"totalProjects":3}"""
    test <@ (parseTestRunReport json).Scope = NoTestsRun @>

/// Did the parser fail to READ the reply (as opposed to reading an absent scope)?
/// The shared, exhaustive predicate — a local copy here would need a `| _ ->` wildcard.
let private isUnreadable = TestScope.isUnreadable

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: every unusable reply fails CLOSED, never to FullSuite`` () =
    // The safe direction, by construction: an error reply, a still-running run, an old
    // daemon that echoes nothing useful, and outright garbage all fail closed.
    test <@ not (TestScope.isFullSuite (parseTestRunReport """{"error":"unknown command 'test-scope'"}""").Scope) @>
    test <@ not (TestScope.isFullSuite (parseTestRunReport """{"scope":"running"}""").Scope) @>
    test <@ not (TestScope.isFullSuite (parseTestRunReport """{"scope":"full"}""").Scope) @>
    test <@ not (TestScope.isFullSuite (parseTestRunReport "not json at all").Scope) @>
    test <@ not (TestScope.isFullSuite (parseTestRunReport "").Scope) @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: "no scope reported" and "could not read the reply" are DIFFERENT values`` () =
    // Failing closed is not enough on its own: the inner loop TOLERATES an absent scope
    // (a repo with no test projects has nothing to run) and must NOT tolerate a failed
    // read, so the parser has to hand back two different facts rather than one.

    // "running" is an ANSWER — a run is in flight, so no scope is earned yet. It stays
    // the tolerated value; this is the positive control that it is still producible.
    test <@ (parseTestRunReport """{"scope":"running","runId":null}""").Scope = ScopeUnknown @>

    // Everything else is a FAILURE TO READ: garbage, an empty reply, a plugin error
    // object, a shape from another version.
    test <@ isUnreadable (parseTestRunReport "not json at all").Scope @>
    test <@ isUnreadable (parseTestRunReport "").Scope @>
    test <@ isUnreadable (parseTestRunReport """{"error":"the test-scope command threw"}""").Scope @>
    test <@ isUnreadable (parseTestRunReport """{"scope":"full"}""").Scope @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: a full reply that did not actually cover every project is not FullSuite`` () =
    // A daemon claiming "full" while reporting 2 of 4 projects is not trusted: the
    // counts are the evidence, the label is not — and a daemon contradicting itself is
    // a reply we cannot read, not a reply that says "nothing to report".
    test <@ isUnreadable (parseTestRunReport """{"scope":"full","ranProjects":2,"totalProjects":4}""").Scope @>
    test <@ isUnreadable (parseTestRunReport """{"scope":"full","ranProjects":0,"totalProjects":0}""").Scope @>

// ----------------------------------------------------------------------------
// A MISSING READING IS NOT A GOOD READING (AUTOMATION-150), at the verdict.
//
// `NoTestsRun` is refused in both modes, but that refusal is only REACHED when the
// scope read succeeded — and every way of FAILING to read the scope used to produce
// the same value as "this repo has no test projects", which the inner loop tolerates
// and must keep tolerating. So a fault on the read path (a throwing command, a dropped
// transport, a reply from another version) turned exit 3 into exit 0 on an unchanged
// daemon state. These pin the split in BOTH directions: the fault is refused, the
// legitimately-absent scope still tolerated.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``InnerLoop: a scope read that FAULTED is never Clean — it cannot rule out NoTestsRun`` () =
    let faulted = ScopeUnreadable "the `test-scope` command threw: SQLITE_BUSY"

    test <@ verdict InnerLoop (inputs false Complete faulted) = CheckOutcome.UnearnedScope faulted @>
    test <@ verdict Confirmation (inputs false Complete faulted) = CheckOutcome.UnearnedScope faulted @>
    test <@ exitCode (verdict InnerLoop (inputs false Complete faulted)) = 3 @>

    // A REAL failure still outranks it — a red is a red, and "no verdict" would bury it.
    test <@ verdict InnerLoop (inputs true Complete faulted) = CheckOutcome.FailuresFound @>

    // ...as does incomplete coverage, which is the more specific complaint.
    test <@ verdict InnerLoop (inputs false (Incomplete 3) faulted) = CheckOutcome.Incomplete 3 @>

[<Fact(Timeout = 15000)>]
let ``InnerLoop: an ABSENT scope is still Clean — the split may not punish a tests-less repo`` () =
    // The over-correction guard. `ScopeUnknown` now means only what it can prove: the
    // daemon has no `test-scope` command (no test projects configured), or a run is in
    // flight. Making THAT non-green would turn every ordinary `fshw check` on a repo
    // without tests into an exit 3 — a worse bug than the one above.
    test <@ verdict InnerLoop (inputs false Complete ScopeUnknown) = CheckOutcome.Clean @>
    // ...and `confirm` still refuses it, exactly as before.
    test <@ verdict Confirmation (inputs false Complete ScopeUnknown) = CheckOutcome.UnearnedScope ScopeUnknown @>

// --- uncheckedMagnitude: defensive totality, pinned directly ---------------
// `converge` structurally never routes `Complete` into the magnitude
// comparison (a Complete read resolves to a verdict first), so the Complete
// arm is reachable only through a direct test of the total mapping.

[<Fact(Timeout = 10000)>]
let ``uncheckedMagnitude: Complete is zero, Incomplete carries its count, Unknown is maximal`` () =
    test <@ uncheckedMagnitude Complete = 0 @>
    test <@ uncheckedMagnitude (Incomplete 7) = 7 @>
    test <@ uncheckedMagnitude Unknown = System.Int32.MaxValue @>
