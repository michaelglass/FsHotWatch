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
      UnattributableDiagnostics = 0
      WaitingOnBuild = BuildWait.NotWaiting
      RunnerAborted = RunnerAbort.NoAbort
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
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
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
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
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
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
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
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.ArtifactNotProduced
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop waiting = CheckOutcome.WaitingOnBuild [] @>
    test <@ verdict Confirmation waiting = CheckOutcome.WaitingOnBuild [] @>
    test <@ exitCode (verdict InnerLoop waiting) = 2 @>
    test <@ exitCode (verdict Confirmation waiting) = 2 @>

[<Fact(Timeout = 15000)>]
let ``verdict: a REAL failure alongside waiting on build still short-circuits to FailuresFound / exit 1`` () =
    // A defer never LAUNDERS a red. Failures are checked first, so a genuine failing
    // diagnostic (or a crashed plugin) dominates a co-occurring defer.
    let failureAndWaiting =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 1
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.ArtifactNotProduced
          RunnerAborted = RunnerAbort.NoAbort
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
    let outcome = verdict Confirmation (inputs false Complete (NoTestsRun NoTestsReason.Unstated))

    test <@ outcome = CheckOutcome.UnearnedScope (NoTestsRun NoTestsReason.Unstated) @>
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
    // AUTOMATION-129. `(NoTestsRun NoTestsReason.Unstated)` does NOT mean "impact analysis selected nothing this
    // time". It means the daemon holds NO TEST EVIDENCE AT ALL — no run has completed
    // in this session, or the one that did executed zero tests ("0 passed, 0 failed in
    // 0 projects"). This assertion once read `= CheckOutcome.Clean`, which is the
    // vacuous green in its purest form.
    //
    // So it is refused in BOTH modes: impact filtering is a question of HOW MUCH we
    // tested, this is a question of WHETHER WE TESTED AT ALL. The inner loop may test
    // less; it may not test nothing and call it green.
    test <@ verdict InnerLoop (inputs false Complete (NoTestsRun NoTestsReason.Unstated)) = CheckOutcome.UnearnedScope (NoTestsRun NoTestsReason.Unstated) @>
    test <@ verdict Confirmation (inputs false Complete (NoTestsRun NoTestsReason.Unstated)) = CheckOutcome.UnearnedScope (NoTestsRun NoTestsReason.Unstated) @>

    // ...but a REAL failure still outranks it: a red is a red, and reporting "no
    // verdict" would bury it.
    test <@ verdict InnerLoop (inputs true Complete (NoTestsRun NoTestsReason.Unstated)) = CheckOutcome.FailuresFound @>

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
let ``parseTestRunReport: a none reply parses as (NoTestsRun NoTestsReason.Unstated)`` () =
    let json = """{"scope":"none","ranProjects":0,"totalProjects":3}"""
    test <@ (parseTestRunReport json).Scope = (NoTestsRun NoTestsReason.Unstated) @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: seeds and their true count come through`` () =
    let json =
        """{"scope":"filtered","ranProjects":1,"totalProjects":3,"seeds":["Lib.A.one","Lib.B.two"],"seedCount":7}"""

    let r = parseTestRunReport json
    test <@ r.Seeds = [ "Lib.A.one"; "Lib.B.two" ] @>
    // 7, not 2: the wire list is truncated, and a report that shows 2 while
    // claiming that is all of them is the same lie as "no tests ran".
    test <@ r.SeedCount = 7 @>

/// THE compatibility guarantee for this field, and the reason it is safe to add to
/// a reply that earns merge verdicts: a daemon older than `seeds` sends none, and
/// that MUST degrade to silence — never to `ScopeUnreadable`, which both check and
/// confirm refuse. A diagnostic nicety may not be able to turn a working check into
/// a refusal.
[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: a reply with no seeds still parses its scope`` () =
    let json = """{"scope":"full","ranProjects":3,"totalProjects":3}"""
    let r = parseTestRunReport json

    test <@ r.Scope = FullSuite 3 @>
    test <@ List.isEmpty r.Seeds @>
    test <@ r.SeedCount = 0 @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: seedCount falls back to the seeds actually sent`` () =
    // An older/odd daemon may send seeds without the count. Reporting 0 while
    // holding two seeds would make the renderer compute a negative "and N more".
    let json =
        """{"scope":"filtered","ranProjects":1,"totalProjects":3,"seeds":["Lib.A.one","Lib.B.two"]}"""

    test <@ (parseTestRunReport json).SeedCount = 2 @>

[<Fact(Timeout = 15000)>]
let ``parseTestRunReport: non-string seed entries are dropped, not fatal`` () =
    let json =
        """{"scope":"filtered","ranProjects":1,"totalProjects":3,"seeds":["Lib.A.one",42,null,"Lib.B.two"]}"""

    let r = parseTestRunReport json
    test <@ r.Seeds = [ "Lib.A.one"; "Lib.B.two" ] @>
    test <@ r.Scope = ImpactFiltered(1, 3) @>

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
// `(NoTestsRun NoTestsReason.Unstated)` is refused in both modes, but that refusal is only REACHED when the
// scope read succeeded — and every way of FAILING to read the scope used to produce
// the same value as "this repo has no test projects", which the inner loop tolerates
// and must keep tolerating. So a fault on the read path (a throwing command, a dropped
// transport, a reply from another version) turned exit 3 into exit 0 on an unchanged
// daemon state. These pin the split in BOTH directions: the fault is refused, the
// legitimately-absent scope still tolerated.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``InnerLoop: a scope read that FAULTED is never Clean — it cannot rule out (NoTestsRun NoTestsReason.Unstated)`` () =
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

// ----------------------------------------------------------------------------
// AUTOMATION-303 (QA rework) — a red must be EARNED, exactly as a green must.
//
// The ticket's premise applied to the other sign. A ledger whose every failing entry is
// an FCS `internal error:` (the checker crashed) or a diagnostic against a file that is
// no longer on disk supports no claim that THIS tree is broken — and reporting exit 1
// sends the reader hunting a defect that is not there. It cost one agent ~40 minutes,
// and the incident that taught the opposite lesson (a cached build hiding a REAL error)
// was output-identical, so no guidance can separate them.
//
// So the tool stops picking a side: exit 3, NO VERDICT. The gate still refuses; nothing
// is claimed broken and nothing is claimed sound.
// ----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: an all-unattributable ledger is NO VERDICT (exit 3), not a red`` () =
    let stale =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 51
          UnattributableDiagnostics = 51
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 6 }

    test <@ verdict InnerLoop stale = CheckOutcome.StaleDaemonState 51 @>
    test <@ verdict Confirmation stale = CheckOutcome.StaleDaemonState 51 @>
    test <@ exitCode (verdict InnerLoop stale) = 3 @>
    test <@ exitCode (verdict Confirmation stale) = 3 @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: ONE attributable diagnostic among them keeps the red`` () =
    // THE POSITIVE CONTROL, and the one the whole change lives or dies on. Case 2 was a
    // REAL compile error arriving beside stale noise; a rule that demoted the pair would
    // ship a non-compiling tree. A single claim about this tree outranks any amount of
    // state that is not.
    let mixed =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 52
          UnattributableDiagnostics = 51
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 6 }

    test <@ verdict InnerLoop mixed = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation mixed = CheckOutcome.FailuresFound @>
    test <@ exitCode (verdict Confirmation mixed) = 1 @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: a FAILING PLUGIN beside a stale ledger keeps the red`` () =
    // The other half of the failure disjunction. A plugin reaches `Failed` without
    // writing a diagnostic every time it throws, and that IS a claim about this run — so
    // no amount of unattributable ledger noise beside it may launder it into "cannot
    // tell".
    let crashedBesideStale =
        { PluginStatuses = statusOf (StatusView.Failed("plugin exploded", DateTime.UtcNow))
          FailingDiagnostics = 51
          UnattributableDiagnostics = 51
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 6 }

    test <@ verdict InnerLoop crashedBesideStale = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation crashedBesideStale = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: a CLEAN ledger is still Clean, never stale-daemon-state`` () =
    // The green direction. `UnattributableDiagnostics = 0` and `FailingDiagnostics = 0`
    // satisfies "no attributable failures" vacuously, and a rule written as a bare
    // comparison would route every passing run to exit 3. Nothing failed is not
    // everything that failed was noise.
    let clean =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 6 }

    test <@ verdict InnerLoop clean = CheckOutcome.Clean @>
    test <@ verdict Confirmation clean = CheckOutcome.Clean @>
    test <@ exitCode (verdict Confirmation clean) = 0 @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: converge does not re-scan stale daemon state`` () =
    // `fshw scan` is the DOCUMENTED remedy for this class and has never cleared it once.
    // Convergence would spend three more full passes to arrive at the same answer, and
    // (worse) each pass looks to the operator like the tool making progress.
    let stale =
        { PluginStatuses = statusOf (StatusView.Completed DateTime.UtcNow)
          FailingDiagnostics = 7
          UnattributableDiagnostics = 7
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 6 }

    let mutable scans = 0

    let outcome =
        converge Confirmation 3 (fun () -> scans <- scans + 1) (fun () -> stale) stale

    test <@ outcome = CheckOutcome.StaleDaemonState 7 @>
    test <@ scans = 0 @>

    // THE POSITIVE CONTROL for `scans = 0`: the same loop, the same budget, an input that
    // DOES drive convergence — so the zero above is a property of the outcome and not of
    // a `triggerScan` that could never be called.
    let incomplete =
        { stale with
            FailingDiagnostics = 0
            UnattributableDiagnostics = 0
            Coverage = Incomplete 5 }

    converge Confirmation 3 (fun () -> scans <- scans + 1) (fun () -> incomplete) incomplete
    |> ignore

    test <@ scans > 0 @>

// ----------------------------------------------------------------------------
// AUTOMATION-201 — "waiting on build" is TWO causes, and they need opposite remedies.
// ----------------------------------------------------------------------------

/// The classification, over messages the stale-artifact preflight really produces.
/// Hard-coded prose here would prove only that the test agrees with itself, so the
/// stale side is fed from `StaleArtifactPreflight.refusalMessages` — the same list the
/// producer's own round-trip test pins.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a stale-output deferral classifies apart from a build-ordering one`` () =
    let apphost = "P2: waiting on build — apphost not produced; tests did not run"

    // Nothing deferred at all.
    test <@ BuildWait.classify [] = BuildWait.NotWaiting @>

    // Only the build-ordering race — the cause whose existing "re-run once the build
    // settles" advice is CORRECT and must not be replaced.
    test <@ BuildWait.classify [ apphost ] = BuildWait.ArtifactNotProduced @>

    for stale in FsHotWatch.TestPrune.StaleArtifactPreflight.refusalMessages "/repo" do
        test <@ BuildWait.classify [ stale ] = BuildWait.StaleOutput [ stale ] @>

        // MIXED: a run can hold both. The stale cause dominates, because it is the one
        // whose wrong remedy costs a full gate cycle to arrive back at the same refusal.
        test <@ BuildWait.classify [ apphost; stale ] = BuildWait.StaleOutput [ stale ] @>

/// The verdict CARRIES the deferrals rather than reducing them to a flag, so every
/// surface that explains this outcome can name the projects and the remedy. Exit code
/// and non-redness are unchanged — this is about what the run SAYS, not what it decides.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: the verdict carries the stale deferrals, still exit 2, still not a red`` () =
    let stale =
        FsHotWatch.TestPrune.StaleArtifactPreflight.refusalMessages "/repo"
        |> List.take 2

    let waiting =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.StaleOutput stale
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = anyScope }

    test <@ verdict InnerLoop waiting = CheckOutcome.WaitingOnBuild stale @>
    test <@ verdict Confirmation waiting = CheckOutcome.WaitingOnBuild stale @>
    test <@ exitCode (CheckOutcome.WaitingOnBuild stale) = 2 @>

    // POSITIVE CONTROL for "carries": the build-ordering cause carries an EMPTY list, so
    // a `WaitingOnBuild` that always carried everything could not pass both.
    let ordering =
        { waiting with
            WaitingOnBuild = BuildWait.ArtifactNotProduced }

    test <@ verdict InnerLoop ordering = CheckOutcome.WaitingOnBuild [] @>

/// A REAL failure alongside a stale-output defer is still a red. The defer must not
/// launder one — that precedence existed before and this change may not move it.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a real failure beside a stale-output defer is still FailuresFound`` () =
    let both =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 1
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.StaleOutput [ "stale build output — x" ]
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = anyScope }

    test <@ verdict InnerLoop both = CheckOutcome.FailuresFound @>

// ---------------------------------------------------------------------------
// AUTOMATION-294 — a dead test host is exit 2, and a red is still exit 1.
// ---------------------------------------------------------------------------

let private abortMessages =
    [ "FsHotWatch.Tests: aborted — test host was KILLED by SIGKILL (exit 137)" ]

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-294: a killed test host is RunnerAborted — exit 2, never the exit 1 it used to return`` () =
    // The whole ticket in one assertion. A `TestsErrored` project used to reach here as a
    // failing diagnostic, so the exit code said 1, `verdict.json` said `red`, and the
    // console listed the killed runner's half-written transcript under "N test(s) failed".
    // Nothing failed. Nothing passed either — which is why this may never be green.
    let aborted =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.HostDied abortMessages
          Coverage = Complete
          Scope = anyScope }

    // Both modes: an abort is not a scope question, so `confirm` may not treat it as one.
    test <@ verdict InnerLoop aborted = CheckOutcome.RunnerAborted abortMessages @>
    test <@ verdict Confirmation aborted = CheckOutcome.RunnerAborted abortMessages @>

    test <@ exitCode (CheckOutcome.RunnerAborted abortMessages) = 2 @>
    // And emphatically not the two codes it must never be confused with.
    test
        <@
            exitCode (CheckOutcome.RunnerAborted abortMessages)
            <> exitCode CheckOutcome.FailuresFound
        @>

    test
        <@
            exitCode (CheckOutcome.RunnerAborted abortMessages)
            <> exitCode CheckOutcome.Clean
        @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-294: THE OTHER DIRECTION — a real failure beside an abort is still FailuresFound`` () =
    // The assertion that stops the abort from laundering a red. If a run holds a genuine
    // failing diagnostic AND a killed host, the failure wins: failures short-circuit
    // before the abort is ever consulted.
    let both =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 1
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.HostDied abortMessages
          Coverage = Complete
          Scope = anyScope }

    test <@ verdict InnerLoop both = CheckOutcome.FailuresFound @>
    test <@ verdict Confirmation both = CheckOutcome.FailuresFound @>

    // A crashed PLUGIN with a spotless ledger is the other half of "found problems", and
    // it must win over an abort too.
    let crashedBeside =
        { both with
            FailingDiagnostics = 0
            PluginStatuses = statusOf (StatusView.Failed("plugin exploded", DateTime.UtcNow)) }

    test <@ verdict InnerLoop crashedBeside = CheckOutcome.FailuresFound @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-294: NoAbort changes nothing — a clean run is still Clean`` () =
    // The negative control. Adding the term must not perturb any verdict that has no
    // abort in it, or the fix would be paid for by every ordinary run.
    let clean =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.NoAbort
          Coverage = Complete
          Scope = FullSuite 4 }

    test <@ verdict InnerLoop clean = CheckOutcome.Clean @>
    test <@ verdict Confirmation clean = CheckOutcome.Clean @>

    test <@ RunnerAbort.classify [] = RunnerAbort.NoAbort @>
    test <@ RunnerAbort.classify abortMessages = RunnerAbort.HostDied abortMessages @>
    test <@ not (RunnerAbort.isAborted RunnerAbort.NoAbort) @>
    test <@ RunnerAbort.isAborted (RunnerAbort.HostDied abortMessages) @>
    test <@ RunnerAbort.aborts (RunnerAbort.HostDied abortMessages) = abortMessages @>
    test <@ List.isEmpty (RunnerAbort.aborts RunnerAbort.NoAbort) @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-294: an abort DOMINATES a concurrent build defer`` () =
    // When a box is saturated enough to kill a test host it is also saturated enough to
    // lose a build race, so the two arrive together. The abort must be the one reported:
    // a defer's remedy is "wait for the build to settle", and that event never comes for
    // a machine that is simply out of CPU.
    let both =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.ArtifactNotProduced
          RunnerAborted = RunnerAbort.HostDied abortMessages
          Coverage = Complete
          Scope = anyScope }

    test <@ verdict InnerLoop both = CheckOutcome.RunnerAborted abortMessages @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-294: converge does NOT retry an abort — no automatic retry to mask a real crash`` () =
    // Deliberately terminal. A re-scan cannot un-kill a host, and an automatic retry
    // cannot tell a host killed by a busy box from one that aborts every time because
    // something is genuinely broken — so a loop that retried until it got a verdict would
    // convert a real crash into a slow green. Honest once, rather than survivable wrongly.
    let mutable scans = 0

    let aborted =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = BuildWait.NotWaiting
          RunnerAborted = RunnerAbort.HostDied abortMessages
          Coverage = Complete
          Scope = anyScope }

    let outcome =
        converge InnerLoop 3 (fun () -> scans <- scans + 1) (fun () -> aborted) aborted

    test <@ outcome = CheckOutcome.RunnerAborted abortMessages @>
    test <@ scans = 0 @>
