# ADR-040: A declared timeout under measured starvation is re-run alone, once

Status: Accepted (2026-10-22)

## Context

A bounded test unit that runs out of wall clock on a saturated box reads exactly like a real
red. On 2026-10-20/21 every land on one host at load 30–70 (12 cores) went red on one suite
overrunning its 600 s `timeoutSec`, with 60 s per-test cancellations as the knock-on; every
class passed when re-run alone. FsHotWatch's own CI saw 20 of 5873 tests fail as xUnit
timeouts, pure functions at 5 s among them, and all passed alone. Nothing classified these:
an agent noticing the load, or a "retry when the box is quiet" rule in a brief, did.

## Decision

- A test project DECLARES its behaviour under contention: `tests.projects[].starvation =
  { "onTimeout": "rerun-alone" }`, optionally for named `classes`, with `loadPerCore`
  (default 2.0) and `share` (default 0.5) thresholds. Without it nothing changes.
- fshw samples the 1-minute load average (`getloadavg`) every 5 s while a declared project
  runs. `ps` is not used: on macOS it cannot read CPU time without an entitlement and reports
  0 %CPU for busy processes.
- A result qualifies only when every failure is a timeout — the project's own `timeoutSec`,
  or a CTRF report whose every failed row is `Xunit.Sdk.TestTimeoutException` in a declared
  class — AND at least `share` of the samples had load ≥ `loadPerCore` × cores. An assertion
  failure is never re-run, so a red with one assertion failure among timeouts stays red.
- A qualifying project is held back from its group's results and re-run ONCE, after every
  group of the run has finished. Its re-run's result is the one reported. The first attempt's
  report, log and timeout marker move to `starved-attempt/` in the run directory, and
  `<Project>.starved.json` records both attempts and their load samples. The verdict's suite
  entry carries `starvedRerun`, so "starved, then passed alone" stays told apart from a clean
  pass, and a re-run that is still red says so.
- A green earned by a re-run is not published as cross-checkout test evidence: the stored
  evidence has no field that would carry the annotation.

## Rejected

- **Retrying any red, or any timeout.** That hides real regressions and real hangs. The re-run
  requires both a declaration and a measurement.
- **Re-running only the timed-out classes.** It would need a second filter shape and a partial
  coverage path for one attempt. The project re-runs its own selection; alone, it is fast.
- **Waiting for a quiet window before running.** That is admission control and
  `tests.maxParallelProjects`, not result classification.
- **Load from `ps` / per-process CPU time.** Unreadable on macOS (entitlement).
