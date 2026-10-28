# ADR-053: Test projects declare a weight, charged as slot-time

Status: Accepted (2026-10-28). AUTOMATION-682 slice 7, AUTOMATION-1299. Builds on ADR-050,
ADR-051 and ADR-052.

## Source of the decision

ADR-050 left AUTOMATION-1299 parked on two questions: where a test's concurrency tolerance is
declared, and what to do about class-level declarations. Both went to the maintainer as a
journal question (2026-10-28, `e4a02f62-3--q-20261028T125353Z-87d2`). It passed its deadline
with no answer, so its recommended default applies:

- **Project-level weights in `.fshw.json`**, a per-project key read by each session's lane.
  ADR-051 already named the place a weight is charged: `Lane.Service`.
- **Class-level exclusivity stays with xUnit collections inside the host**, with no
  per-class host split. Revisit with measurements if a class-level conflict shows up.

## Criteria (fixed before code)

- **(a) A weight scales the charge.** `JobSpec` carries a `Weight` (default 1). A slot held
  for a job of weight `w` adds `w` × its slot-time to its lane's service. Raw slot-time
  (`Held`) is unchanged, and the weighted charge is reported beside it.
- **(b) A heavier project gets proportionally less admission under contention.** With an
  injected clock and one slot, two sessions run continuous jobs of equal length, weighted
  1 and 3. Over 2000 s, the lighter session gets three times the grants of the heavier,
  within 20%, and their weighted service ends within 20% of each other.
- **(c) Declared in `.fshw.json`.** `tests.projects[].weight` is a number greater than
  zero, and 1 when absent. Anything else is a config error naming the key. It is
  documented in the README.
- **(d) TestPrune charges it.** A test group acquires its process slot with the largest
  weight among its projects. A plugin-level test shows a group of a weight-3 project
  charged three times its slot-time.
- **(e) Undeclared is unchanged.** With every weight 1, the slice 5 fairness, priority and
  aging tests pass unchanged. A weight is no part of a project's test-evidence key, so no key changes.
- **(f) The usual bar.** RED first, then a full `mise run ci`; merged with GitHub CI green,
  reverted if it goes red; no coverage floor lowered.

## Decision

- **`JobSpec.Weight`, charged at `Lane.Service`.** A lane accrues raw slot-time (`Held`)
  and weighted slot-time (`Charged`) side by side. Service, the quantity ADR-051 serves
  sessions by, is now the weighted charge plus catch-up. Nothing else in the scheduler
  changed: ranks, aging, the quantum and catch-up are as they were.
- **A weight changes the charge, not the slots.** A weight-3 job still takes one slot. It
  waits longer for its next one because its session has been charged more. Taking `N`
  slots for a `needsCores: N` project was the alternative; see Rejected.
- **A slot is given back at its own weight.** `Lane.Release(job)` names the weight being
  given back, and `Lane.Run` uses it. The unweighted `Release()` gives back a weight-1
  slot if the lane holds one, else its oldest. Callers that acquire unweighted jobs are
  therefore unchanged.
- **Declared per project in `.fshw.json`: `tests.projects[].weight`.** A number greater than
  0, and 1 when absent. Anything else is a `ConfigErrorException` naming the key. In terms
  of 1299's vocabulary, `needsCores: N` is weight `N`, and "exclusive" is a weight as large
  as the host's processor count. "serial" (never parallel with itself) is not a weight, and
  is not provided. Each session already runs a project at most once at a time, and nothing
  measured shows two sessions running the same project as a problem.
- **A group is charged its heaviest project.** A group holds one slot for all its projects
  in turn (ADR-050). It is charged the largest weight among them, rather than switching
  weight between projects in mid-hold.
- **Not part of the test-evidence key.** A weight decides when a host is admitted, never
  what it runs or reports, like `alwaysClasses`.
- **Class-level declarations stay inside the host**, through xUnit collections
  (`DisableParallelization`), as the default decided.

## Rejected

- **`needsCores: N` takes N slots.** That needs multi-slot grants, which bring a
  head-of-line question: does a 4-slot job hold back 1-slot jobs that could run now, or
  wait for 4 to come free at once and risk starving? The default chose weights, which
  need neither, so this stays out until a measurement asks for it.
- **A per-class host split.** The default rejected it: each split class costs a host
  launch, and its tests' isolation changes.

## Measured

Unit tests, against an injected clock except (d):

- (a) A weight-3 slot held 2 s beside a weight-1 slot held 3 s reports `Held` 5 s and
  `Charged` 9 s, and `chargedMs` in the payload.
- (b) One slot, two sessions with continuous 5 s jobs weighted 1 and 3, over 2000 s:
  - the lighter session got three times the heavier's grants, within 20%;
  - their charges ended within 20% of each other.
- (c) `weight` absent is 1; 3 and 0.5 are read; 0, -2, a string, a boolean and null are each
  a config error naming `tests.projects[T].weight`.
- (d) A TestPrune group of a weight-3 project and a weight-1 project, run through a real
  plugin host, is granted one slot and charged exactly 3 × its held time.
- (e) The WorkScheduler, fairness, priority, status and cancellation tests all pass
  unchanged, and the evidence key is unchanged with weight 3.
- RED: 5 failed against a stub that kept the charge unweighted. These were every test of
  the charge (a), (b) and (d). The config, evidence-key and validation tests were written
  with the stub's types, so they passed against it.
