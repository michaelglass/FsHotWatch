# ADR-075: An obligation names only test projects the tree has

A covering test project counts as absent only when three independent signals agree. An absent
project covers nothing, so nothing can be owed to it. AUTOMATION-1717.

Status: Accepted (2026-11-14)

## Context

A checkout once ran a branch that had a test project P. It then checked out a tree without P.
The impact index still held P's tests. Every run went red with "N symbol(s) still owed to tests
in P, which this daemon does not run", even though every test passed. Measured in a remote-ci
slot with FsHotWatch.Simulation.Tests.

Both "owed" messages read their project list from one map. That map comes only from
`PluginSteps.coveringOfIn`, which walks the index's dependency edges up to
`test_methods.test_project`. Three stores looked like suspects and are not involved:

- `test-history.json` and `deps-baseline.json`, which do not feed what is owed;
- the box-wide outcome store;
- the shadow records, which are diagnostic only.

## Decision

1. **Absence takes three signals.** A covering project P is absent only when all of these hold
   (`PluginSteps.absentFromTreeIn`):
   - P is not configured (`tests.projects`).
   - An available model's discovered inventory does not name P, by the `.fsproj` stem that the
     index keys tests by.
   - None of the source files the index holds P's test methods from exists in the tree
     (`ImpactQueries.TestSourceFiles`).

   The source-file signal is the one that protects real coverers. Script tests (`.fsx`) and
   projects outside the discovery roots are named by no `.fsproj` in the inventory, so the
   inventory alone would silently drop them. Leftover `bin/` or `obj/` counts as neither
   presence nor absence.

2. **The filter sits where the obligation is computed.** It runs in `coveringOfIn`, not in a
   reconciliation at daemon start. Everything that decides or names an obligation reads that
   one function:
   - the flush classification;
   - the launch's coverer capture;
   - the completion's retirement;
   - `owedElsewhere`;
   - the `test-scope` report.

   A start-time pass would miss a branch switch made while the daemon is running, and a
   project removed by a later re-discovery. It would also need to delete index rows to take
   effect. The filter deletes nothing: if P comes back, P owes again at once.

3. **The default fails closed.** If there is no available model, or the index cannot say where
   P's tests live, P is not absent, and the obligation stands. Missing evidence never makes an
   obligation disappear.

4. **Absence is always visible.** Each absent project is logged once per daemon. Every run's
   summary also names each one, for example "(1 symbol(s) not owed: project Sim is no longer
   in this tree)". A wrong judgement therefore shows even on a green run.

## Consequences

- Once this ships, remote-ci's block that resets a slot's `test-history.json` and
  `deps-baseline.json` when the tree's project set changes can be deleted. It treats a
  symptom; the obligation came from the index.
- Shadow records naming an absent project need no cleanup.
- Each pass decides absence at most once per project. The extra query runs only for coverers
  that are neither configured nor in the inventory.

## Tests

`TestPruneDebtScopeTests`:

- **Repro:** an absent coverer owes nothing, and the run is green.
- **Controls, each still owing:**
  - the project is in the inventory but unconfigured;
  - there is no model;
  - the project is not in the inventory, but its test sources are in the tree.
- **Summary note:** a run with an absent coverer carries the note; a run without one does not.
- **Mutation:** with "nothing is ever absent", only the repro fails. With "everything is
  absent", every control fails.
