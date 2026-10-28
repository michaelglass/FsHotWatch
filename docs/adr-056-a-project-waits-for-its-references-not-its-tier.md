# ADR-056: A project's checks wait for its references, not for its tier

Status: Accepted (2026-10-28). AUTOMATION-683 slice 3. Builds on ADR-038, ADR-054 and
ADR-055.

## Context

The scan and the change batch both check projects in dependency tiers (`GetParallelTiers`),
and each tier is a barrier. No file of tier `n + 1` starts until every file of tier `n`
has finished, retries included.

A project in tier `n + 1` needs only the projects it references. It does not need its
tier-`n` siblings. So one slow project holds back every project in the next tier,
including those that do not reference it. The checks sit idle on a shape of the graph,
not on a dependency. AUTOMATION-683 asks for the tiers to become explicit job
dependencies, within the existing bounds.

## Criteria (fixed before code)

- **(a) A project starts when its references finish.** Projects `A` and `B` have no
  references, and `C` references only `A`. While `B`'s check is held, `C`'s checks start
  as soon as `A` has finished. Under the tier barrier, they would wait for `B`.
- **(b) A project never starts before its references finish.** No check of `C` starts
  while any check of `A` is unfinished, retry rounds included.
- **(c) The bound holds across projects.** With projects running side by side, at most
  `maxInFlight` checks are started and unfinished at once.
- **(d) Retries stay rounds, per project.** A project's files whose check returned
  `None` are retried after that project's round has finished, up to the budget. Its
  dependents start only after its last round. A budget exhausted for a project is
  reported with its files. Extra rounds are summed across projects.
- **(e) A failure or cancellation stops the rest.** A check that throws, or a caller
  that cancels, fails the whole run. No further check starts after it.
- **(f) The scan and the batch both run this way.**
  - The scan resolves each file to the first project, in tier order, that claims it
    (`freshForTier`, unchanged). It decides the deps gate for every project before any
    check starts.
  - Dependencies are each project's references in strictly earlier tiers. A reference
    cycle, which `GetParallelTiers` puts in one final tier, therefore has no edges
    inside the cycle and cannot deadlock.
  - The existing scan, change-batch and delivery tests pass unchanged.
- **(g) The usual bar.**
  - RED first.
  - `mise run ci` exits 0, and no coverage floor is lowered.
  - It is merged with GitHub CI green, and reverted if that goes red.

## Decision

- **`Daemon.runDependentChecks` replaces the per-tier loop.** It takes:
  - `maxRetries` and `maxInFlight`;
  - `check` (a project and a file to a result, or `None`), and an asynchronous `emit`;
  - the projects, in tier order, each with the projects it waits for and its files.

  It keeps one queue of runnable checks, in tier order, and starts them while fewer than
  `maxInFlight` are unfinished.
- **When a project finishes.** A project finishes its round when its last check returns.
  Then:
  - its `None` files start the next round, if the budget allows;
  - otherwise the project is done, and each dependent whose last reference just finished
    is released.
- **Failure.** The first failure cancels the run's linked token, so the started checks
  are cancelled, and it is raised.
- **The scan decides everything up front.** It decides the deps gate and its claims for
  every project before it starts, rather than tier by tier. A gate's restore still
  supersedes the model, as before (`ModelSupersededException`), now before any check
  starts.
- **The batch keeps its two cohorts.**
  - The changed cohort collects its results, which are published once the cascade is
    decided (ADR-055).
  - The cascade publishes each result as its check returns.
  - Neither retries, as before.

## Measured

- **RED.** 4 of 9 `DependentChecksTests` failed against a stub that kept the tier barrier
  and did not retry: (a), (b), (d), and the summed rounds. The other 5 (bound, failure,
  cancellation, outside references, empty) pin behaviour the barrier shared.
- **GREEN.** 9 of 9, in each of 5 runs. With a 10th test, a result emitted as its own
  check returns, the scan, batch, delivery, pipeline, cold-scan, live-result,
  scan-then-check and locked-emit classes passed 259 of 259.
- **Retired.** `runChecksWithRetry` was left with no caller, so it is removed, together
  with its tests. Its invariants now live in `DependentChecksTests`: convergence,
  exhausted budget, the bound, emit-as-it-returns, and the empty cohort.
- **Not measured on the harness.** The synthetic repository is one project, so it has
  no edge for this change to use.
- **(g) `mise run ci` exit 0:**
  - test-direct: 6497 passed, 0 failed, 2 skipped;
  - coverage floors passed;
  - lint-project: 123/123;
  - analyzers: 0 findings; format: clean; test-prune: 2/2;
  - integration: 115/115.
