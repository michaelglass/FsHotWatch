# ADR-021: A green is relative to a full-suite baseline, and the reds are durable

Status: Accepted (2026-09-06); decision 2 amended 2026-11-11 (AUTOMATION-1581, see "Amendment")

Relates to: [ADR-013](adr-013-the-verdict-is-a-file-content-addressed-to-its-tree.md)
(the verdict is a file, content-addressed to its tree). ADR-013 made a green a claim
about a specific tree. This one is about what the claim covers: the tests the run did
not execute.

## Context

`fshw check` runs the tests impact analysis selects. A green from it was read as "the
suite is green", and for weeks it was not: 17 tests turned out to be red on `main`
that no `check` had selected, because nothing they covered had changed. The design
review named the hole exactly — impact selection is sound only if the unselected set was
green, and nothing recorded that. The pending-verification queue accounts for changed
symbols; it does not account for skipped tests.

Three mechanisms existed by the time this was addressed, and none closed it:

- **Red-test quarantine**: a test red in the last run that executed it
  is re-selected on every run until it passes. Session-scoped, on the argument that a
  restarted daemon runs the full suite. That argument holds only when the durable
  pending queue is empty at restart. After a red run it is not empty — a failing project
  blocks the commit of every symbol it covers — so the restarted daemon's first run is
  impact-filtered, `hasCachedResults` is then true, and a red the filter does not reach
  is never selected again. The same never-selected red, reproduced by a restart.
- **The session baseline** (`hasCachedResults`): the zero-affected skip requires a run
  to have completed in this session. Any run — a filtered one counts.
- **`confirm`'s watermark** (`Verdict.priorConfirmation`): a full-suite green over this
  exact tree by this exact binary. Correct, and only for `confirm`; `check` never asks.

## Decision

1. **A green names the full-suite run it is relative to, and cannot be built without
   one.** `Verdict.Outcome.Green of Baseline`, where a `Baseline` is `FullSuiteRun` (run
   id, when, how many projects) or `NoTestSuite` (the daemon runs no tests, so nothing is
   skipped). `CheckVerdict.CheckOutcome.Clean` carries the same and is minted only from a
   `BaselineReading.Valid` / `NoTestSuite` reported by the daemon. `Absent` and
   `NotReported` land on `CheckOutcome.NoBaseline`: exit 3, no verdict, the daemon's
   reason in the file. A green verdict file from before this ADR has no baseline and is
   unreadable; `confirm` earns the evidence instead of trusting it.

2. **The daemon records the baseline durably** — `.fshw/test-prune/full-suite-baseline.json`:
   the last run that executed every configured project unfiltered and PASSED each one.
   *(Amended 2026-11-11: this used to read "left each one ACCOUNTED FOR, meaning passed,
   or red with the red recorded"; a red project is no longer counted as covered. See
   "Amendment".)* A red full suite leaves the earlier baseline standing, and its reds are
   carried. Without a
   valid baseline every run is widened to the full suite, exactly as an unreadable ledger
   widens it, until one is earned; a configured project the baseline
   never executed makes it stale the same way.

3. **The reds are durable** — `.fshw/test-prune/outstanding-failures.json`, beside the
   queue, loaded at startup and quarantined into the first run as in-session reds are.
   An unreadable file is debt of unknown membership and takes the unreadable-ledger road:
   the run is widened to the full suite.

4. **Owed-but-unrunnable coverage is reported, never written off silently.** A changed
   symbol whose only covering tests live in a project `tests.projects` does not list is
   still dropped from the queue (nothing here can discharge it), but the
   write-off names the project — in the log, on the `test-scope` reply, and in the
   verdict's `changes-uncovered` reason.

### Why a skipped test is now accounted for

The watermark and the two durable ledgers compose into one claim. A test the run
skipped was last executed in the baseline run. Since then, either nothing it covers
changed (or the change was passed by a covering run and left the queue), and it was not
red (or its red is in the outstanding list and it will be re-selected until it passes) —
or it is owed, and the verdict is not green. A skip is discharged by the baseline's
validity, never by silence. That is why the third requirement ("the
ledger owes work for skipped tests") is met without a per-test ledger: adding one would
be a second accounting of the same debt.

### Why the baseline was written for a red full suite too (superseded 2026-11-11)

*Kept as the reasoning that was accepted; the "Amendment" below shows where it fails.*

The original proposal asked for the last *known-green* full suite. That is sufficient but costly:
a repository with one persistent red would run the whole suite on every check until
that red was fixed, because no green full suite would ever be earned. A red full suite
carries the same information about every test that passed, and quarantine carries the
rest. Requiring green would buy nothing the reds do not already buy, and would cost the
inner loop.

## Consequences

- `fshw check` in a fresh clone, or after `tests.projects` grows, runs the full suite
  once and then resumes impact filtering. Before this ADR a fresh clone ran the full
  suite once per session anyway; the difference is that the reason is recorded and the
  next session inherits it.
- A red in one session is red in the next, on the same evidence, until a run that
  executes it passes. `fshw stop` does not clear a red. It never did in the session; now
  it does not across sessions either.
- `verdict.json` greens carry `outcome.baseline`; consumers that switched on
  `outcome.kind` alone are unaffected, and the exit codes are unchanged except that a
  check whose daemon has no baseline exits 3 where it used to exit 0.
- The cache key for the test-prune plugin is salted by the widening, so a filtered run's
  cached verdict cannot replay for a run that would be widened.
- The candidate cause (c) — the `runnableProjects` drop — was not the
  cause in the large private downstream repository where the 17 reds sat (every test
  project is listed there), and is no longer
  silent anywhere.

## Amendment (2026-11-11, AUTOMATION-1581): a red unfiltered project is never covered

### The counterexample

Decision 2 and "Why the baseline is written for a red full suite too" both assume a red full
run executed every test, so that "a red full suite carries the same information about every
test that passed". A host that ends early breaks that. xUnit v3 under `--stop-on-fail on`
stops at the first failure, exits 2 (an ordinary failure, not a signal or a timeout), and
writes a coherent CTRF report listing only the tests it reached. Take project P holding
`A.a1`, `Z.z1` and `Z.z2`, with `A.a1` and `Z.z1` red. On a cold ledger:

1. The widened full suite runs P whole, stops at `A.a1`, and reports `{tests:1, failed:1}`.
   P's only red is recorded, so P was "accounted for" and the run earned the baseline.
2. `A.a1` is fixed. The next `check` selects `A` only (the still-owed symbol and the
   quarantined red both point there), it passes, and the outstanding list empties.
3. `check` is green relative to a baseline in which `Z.z1` never ran, and it stays green.

"A test the run skipped was last executed in the baseline run" is false for `Z.z1`: it
was never executed, and it is not in the red list either. The same assumption sat in
`EarnedEvidence.fromCompletion`, which counted the red project as whole-project coverage,
so the evidence-gap widening did not fire either. `confirm` was not fooled: it runs every
project whole, so once `A.a1` passes it reaches `Z.z1`.

### The rule

**A red unfiltered project is never counted as covered.** A red run proves nothing about
tests it did not report as passed.

- The baseline is written only by a full-suite run that PASSED every configured project
  unfiltered (`CompletionStages.settleDebt`, `earnsBaseline`).
- An unfiltered red project contributes no whole-project coverage to earned evidence
  (`EarnedEvidence.fromCompletion`). Coverage that an earlier green whole-project run
  earned under the same model is still carried.
- A red full suite leaves the earlier baseline standing. Every test it skipped last ran in
  that baseline's run, which is the claim this ADR needs. Its reds are carried and
  quarantined as before.
- This applies to every red, whatever ended it. A timeout, which used to earn the baseline
  through its project-level red, earns none either.

### Cost

The cost is what the superseded section feared, but only where no baseline exists yet: a
cold ledger, or `tests.projects` grown. There, every run stays the full suite until one
passes every project. With a baseline already earned, a red full run changes nothing for
selection. A red project with no green whole-project run under the current model stays in
the evidence gap, and the next launch runs it whole until it passes. The cost measured on
the downstream repository before this change shipped is recorded with the AUTOMATION-1581 fix.

### Rejected

- **Witness by test-method count**: count the project as covered when its report's rows
  cover the index's test methods for it. Rejected. The count is not available where the
  baseline is decided (`settleDebt` has neither the report nor the index). It is also
  fragile in both directions:
  - a theory's data rows can match the method count of a run that stopped early;
  - unindexed tests (C#, generated or inherited code, tests added since the last index) can
    make a short report look complete;
  - explicit tests, static trait filters in a project's arguments, and `checkSkipClasses`
    can make a complete report look short.
- **Detect `--stop-on-fail`** and refuse only those runs. Rejected: the setting can come
  from `xunit.runner.json`, a project's own arguments or a `testconfig.json`, none of which
  fshw parses, and other ways of ending early (MTP `--maximum-failed-tests`, a host that
  writes a partial report and exits non-zero) would remain.
