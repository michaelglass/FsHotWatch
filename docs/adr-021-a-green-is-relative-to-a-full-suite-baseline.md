# ADR-021: A green is relative to a full-suite baseline, and the reds are durable

Status: Accepted (2026-09-06)

Relates to: [ADR-013](adr-013-the-verdict-is-a-file-content-addressed-to-its-tree.md)
(the verdict is a file, content-addressed to its tree). ADR-013 made a green a claim
about a specific tree. This one is about what the claim covers: the tests the run did
not execute.

## Context

`fshw check` runs the tests impact analysis selects. A green from it was read as "the
suite is green", and for weeks it was not: AUTOMATION-108 found 17 tests red on `main`
that no `check` had selected, because nothing they covered had changed. The design
review named the hole exactly — impact selection is sound only if the unselected set was
green, and nothing recorded that. The pending-verification queue accounts for changed
symbols; it does not account for skipped tests.

Three mechanisms existed by the time this was addressed, and none closed it:

- **Red-test quarantine** (AUTOMATION-67): a test red in the last run that executed it
  is re-selected on every run until it passes. Session-scoped, on the argument that a
  restarted daemon runs the full suite. That argument holds only when the durable
  pending queue is empty at restart. After a red run it is not empty — a failing project
  blocks the commit of every symbol it covers — so the restarted daemon's first run is
  impact-filtered, `hasCachedResults` is then true, and a red the filter does not reach
  is never selected again. The AUTOMATION-108 shape, reproduced by a restart.
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
   the last run that executed every configured project unfiltered and left each one
   ACCOUNTED FOR, meaning passed, or red with the red recorded. Not only a green run: a
   red full suite proves what every other test did, and its reds are carried. Without a
   valid baseline every run is widened to the full suite, exactly as an unreadable ledger
   widens it (AUTOMATION-150), until one is earned; a configured project the baseline
   never executed makes it stale the same way.

3. **The reds are durable** — `.fshw/test-prune/outstanding-failures.json`, beside the
   queue, loaded at startup and quarantined into the first run as in-session reds are.
   An unreadable file is debt of unknown membership and takes the AUTOMATION-150 road.

4. **Owed-but-unrunnable coverage is reported, never written off silently.** A changed
   symbol whose only covering tests live in a project `tests.projects` does not list is
   still dropped from the queue (AUTOMATION-99: nothing here can discharge it), but the
   write-off names the project — in the log, on the `test-scope` reply, and in the
   verdict's `changes-uncovered` reason.

### Why a skipped test is now accounted for

The watermark and the two durable ledgers compose into one claim. A test the run
skipped was last executed in the baseline run. Since then, either nothing it covers
changed (or the change was passed by a covering run and left the queue), and it was not
red (or its red is in the outstanding list and it will be re-selected until it passes) —
or it is owed, and the verdict is not green. A skip is discharged by the baseline's
validity, never by silence. That is why the ticket's third acceptance criterion ("the
ledger owes work for skipped tests") is met without a per-test ledger: adding one would
be a second accounting of the same debt.

### Why the baseline is written for a red full suite too

The ticket asked for the last *known-green* full suite. That is sufficient but costly:
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
- The candidate cause (c) from AUTOMATION-108 — the `runnableProjects` drop — was not the
  cause in the intelligence repository (every test project is listed), and is no longer
  silent anywhere.
