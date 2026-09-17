# ADR-024: A test verdict needs a coherent CTRF report

Status: Accepted (2026-09-17).

## Context

The verdict read a CTRF report's summary through `Ctrf.trySummary`, which defaults an
absent counter to zero and never looks at the rows. So a clean summary claiming seven
tests beside a single row counted as seven passes. `Verdict.suiteVerdicts` then copied
those seven passes into `.fshw/verdict.json`, where they outlive the file. When a report
was requested and missing, a clean process exit still made the project green. The CLI's
`test-rerun` render printed "Tests passed" with exit 0 when one project passed and a
sibling errored. The same happened when the sibling deferred or reported a status the CLI
did not recognise.

## Decision

`Ctrf.VerdictReport` is opaque, and `Ctrf.tryVerdictReport` is its only constructor. It
requires:

- all six summary counters, each a nonnegative integer;
- for a clean summary (no `failed`, no `other`), a `tests` array with one row per counted
  test, every row `passed`, `pending` or `skipped`, and each of those counts matching the
  summary.

A red summary stays authoritative without reconciliation. MTP counts a test that threw a
raw exception in `failed` and `other` but writes no row for it.

The verdict consumers read only this evidence:

- TestPrune's `classifyTestOutcome`. When a report was requested and none is usable, the
  project is errored on any exit. A coherent zero-test report after a clean exit is also
  errored.
- `Verdict.suiteVerdicts` and the `run-tests` reply counts, through
  `Ctrf.verdictReportsForRun`.
- The diagnostic readers (`trySummary`, `tryReadReport`, `reportsForRun`,
  `latestRunReports`) stay permissive for status and flakiness views.

In the CLI render, a `ran` coverage with any selected project that errored, deferred or
reported an unrecognised status exits 3, the same as a run where nothing executed. A
zero-match sibling does not block the pass.

## Rejected and deferred

- Exit 1 for an errored sibling. A host abort is not a test failure anywhere else: the
  ledger's `HostAborted` never counts as failing, and `check` maps it to exit 2. Exit 1
  here would call it a failure in one verb only.
- Reconciling red reports, or requiring the six counters to sum. Real raw-exception
  reports would stop being red.
- An unfiltered zero-test report with a non-zero exit stays `TestsFailed` (red). Treating
  it as "verifies nothing" is undecided and not part of this change.
- Coherence is not scope or run ownership. A coherent report proves only that its counts
  and rows agree.

## Verification

Before the change, 24 of the 47 selected acceptance and control cases failed on `main`.
All controls passed: the raw-exception red, the coherent nested and flattened reports, and
the zero-match sibling. After the change, all 1059 tests in the classes that drive the
CTRF readers and the CLI render pass. Every stored real xUnit v3 report found locally satisfies
the rule: 53 reports from seven checkouts of three repositories.
