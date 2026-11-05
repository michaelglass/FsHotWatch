# ADR-064: A declared timeout message counts as a timeout under starvation

Status: Accepted (2026-11-04)

## Context

ADR-040 re-runs a project alone, once, when every failure of its red is a timeout and the box
was measured starved while it ran. "A timeout" meant an xUnit timeout: the project's own
`timeoutSec`, or a CTRF row whose exception is `Xunit.Sdk.TestTimeoutException`.

A browser test that flips under load usually fails on its browser driver's action timeout
instead. Playwright reports `Timeout 45000ms exceeded.` as an exception the test does not
catch, so xUnit records an ordinary failure (`System.AggregateException`,
`System.TimeoutException`). The browser tests that motivated this allow 120 to 240 s per test,
so the action timeout always fires first and the starvation rule never applies to them.

Three options were on the table: let a project declare which failure messages are timeouts;
raise the action timeout above every test timeout; or turn the action timeout into a
cancellation of the test's token, so that xUnit reports a timeout.

## Measurement

The question was the trade-off between rerun yield and masking: how many reds a re-run would
recover, and how often treating an action timeout as load would hide a real defect. It was
answered from the 3,279 CTRF reports retained in one consumer repository on 2026-11-04
(retained `.fshw/test-runs` across its workspaces, a 100-commit replay with 10 seeded faults,
and stored fixtures).

- **What the current rule catches.** 13 failed rows in 6 reports were xUnit timeouts. 86 failed
  rows in 50 reports were `Timeout NNNNms exceeded`, across 47 distinct browser tests.
- **Yield.** In 41 of those 50 reports (68 of the 86 rows), every failure of the report was a
  timeout of one of the two kinds, so the project would qualify. Each of the 15 qualifying runs
  that kept a load profile was starved under the default threshold: 14 had 100% of samples at
  2 or more per core (means of 5.9 to 31.8 per core), and the other had 92%. The other 9 reports
  also had harness or browser-crash failures (`harness failure at admin login`,
  `Page crashed`, `Target page, context or browser has been closed`), so they stay red under
  every option.
- **Masking.** No action timeout in the corpus was a real defect. 37 of the 86 rows came from
  replaying commits that had landed and later passed. 37 rows had a later observation of the
  same test in the same workspace, and every one of those passed. None failed again, and none of
  the 49 rows without a later observation could be tied to a defect. No seeded fault showed up as
  an action timeout: the seeded test and analyzer faults failed as assertions or findings, and
  the action timeouts beside them were the same load noise as on clean trees.
- **What a missed defect costs.** A selector that no longer matches fails on every attempt.
  Re-run alone, it fails again, and ADR-040 reports that red as "still red". So a deterministic
  defect costs one extra project run and is not hidden. Masking needs a defect that is
  intermittent and that coincides with measured starvation. That is the same exposure ADR-040
  already accepted for xUnit timeouts, and the corpus has no instance of it.

## Decision

- `tests.projects[].starvation.timeoutMessages` is a list of regular expressions. A failed CTRF
  row whose `message` matches one of them counts as a timeout, in addition to xUnit's own. A
  project that browses with Playwright declares `["Timeout \\d+ms exceeded"]`.
- Everything else in ADR-040 is unchanged. The declaration, the starvation measurement and the
  every-failure-is-a-timeout rule all still apply, the project is re-run once and alone, and the
  re-run is the result reported. An assertion beside an action timeout keeps the red.
- A pattern that does not compile as a regular expression, a blank one, or a non-string is a
  config error, so a typo cannot quietly match nothing.

## Rejected

- **Raise the action timeout above every browser test's timeout.** The failure would then name
  the test's timeout instead of the selector that never appeared. That throws away the most
  useful line in a real red to fix a classification, and it reverses the consumer's earlier
  decision to bound actions tightly.
- **Cancel the test's token when an action times out.** xUnit has no API for a test to report
  itself as timed out. A wrapper would have to fake `TestTimeoutException` in every browser test,
  and the result would lie about which bound fired.
- **Recognise Playwright's message without a declaration.** fshw does not know which runners or
  libraries a project uses. A message pattern is the project's knowledge, and the project
  declares it the same way it declares the starvation policy.
