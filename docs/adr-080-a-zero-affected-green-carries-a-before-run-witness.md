# ADR-080: A zero-affected green carries a beforeRun witness

A green test-prune verdict that ran no test must stand on a passing `tests.beforeRun` over
the tree it answers for, or on there being no `beforeRun` configured. The type makes any
other green impossible to build. AUTOMATION-1835 part 2.

Status: Accepted (2026-11-21)

## Context

`tests.beforeRun` runs inside `executeTests`, at the start of a run. A consumer repository
puts gates there that need the prepared tree: route-registry checks, a generated-types
check. The zero-affected skip (`zeroAffectedSkip`) returns a green with zero tests run, and
it returns before `executeTests`, so `beforeRun` does not run.

The skip has two routes.

- **Route 1 (baseline-equivalent)** skips only when the input tree equals the tree a
  receipt is bound to. A receipt is earned only by a run that executed, and every such run
  went through `beforeRun` first, so this route has always stood on a passing `beforeRun`.
  The type did not say so. Control flow did.
- **Route 2 (nothing to verify)**, where every changed symbol has no covering test, has no
  tree requirement at all. A change that adds a route handler no test covers could reach a
  green without the route gates in `beforeRun` having seen it. The consumer's measurement
  found no land that was hit by this (route 1 fired 39 times in 563 launches, route 2 never
  fired), but nothing prevented it.

## Decision

1. **`BeforeRunWitness` is the evidence, and only `settle` can build it.** It is a public
   type with private cases: `NoHookConfigured`, or `PassedOn tree`. The only place that
   builds `PassedOn` is `BeforeRunWitness.settle`. It runs the configured hook, which
   throws on a failing step, then reads the tree. It returns a witness only when that tree
   is the one the tests run against: no compile input moved (`afterBeforeRun`'s binding
   equals the tree read after the hook). With no hook configured it runs nothing, and the
   witness says so.
2. **The hook is parsed once.** `RunConfig.BeforeRun` is a `BeforeRunHook`
   (`NotConfigured | Configured run`), not an option that each reader has to interpret.
3. **The witness travels with the tree it covers.** `executeTests` returns the run's
   `Settlement` (`Settled of WitnessedTree | Unsettled of launchBinding`), replacing the
   `boundTree`/`settled` pair. `TestRunLaunch.BeforeRun` carries the witness, and
   `ReceiptTransition.classify` earns a receipt only when that witness covers the tree the
   receipt binds. `TestEvidenceReceipt.BeforeRun` keeps it.
4. **The skip takes a `WitnessedTree`, not a flag.** `zeroAffectedSkip` binds the tree the
   witness vouches for and records the witness on its launch. A `WitnessedTree` comes from
   `settle` or from `BeforeRunWitness.held`, which returns:
   - any tree, when no hook is configured;
   - with a hook configured, the standing receipt's witness, when that receipt's run passed
     `beforeRun` on this very tree. This is route 1, unchanged.
5. **Route 2 without a held witness runs `beforeRun` first, then decides.** It runs the hook
   under the skip's own run id, so the hook's timings and an aborted lifecycle name the same
   run. A failing hook throws into the impact run's aborted lifecycle, and the plugin
   reports Failed. A passing hook that moved nothing a build compiles yields the witness, and
   the skip binds the tree the hook left. A hook that moved a compile input leaves a tree the
   symbol diff never saw, so the launch runs, as any refused skip does.

## Consequences

- No-hook repositories behave exactly as before: every skip holds `NoHookConfigured`.
- Route 1 behaves exactly as before: it runs no hook of its own.
- Route 2 with a hook configured costs one `beforeRun` per skip. When that hook also moves a
  compile input, the run that follows runs `beforeRun` again. That combination is rare, and
  running twice is correct.
- A run whose tree cannot be read after `beforeRun` no longer earns a receipt. Before, it
  could earn one if the tree read at completion matched its launch binding.
- `TestEvidenceReceipt`, `TestRunLaunch` and `TestRunInputs` gain a field each. These are
  breaking public changes.

## Alternatives rejected

- **Require a verified tree for route 2** (`not treeUnverified`, as route 1 does). Route 2
  exists for a tree that has moved, so this would refuse nearly every route-2 skip and run
  the full suite instead. It would also still be a check placed beside the skip, not a value
  the skip cannot be built without.
- **Run `beforeRun` on every skip, route 1 included.** Route 1 already holds a witness. The
  measured route-1 skips are re-scans with no byte change, and each would pay for a full
  `beforeRun` chain and prove nothing new.
- **Keep the witness as an option on the receipt.** A receipt with no witness would be a
  reachable state that means nothing. An earned receipt always has a witness. The option
  sits on the launch, where "nothing vouches for this tree" is a real case: an unsettled
  run, the aborted lifecycle.
- **Treat a stored `NoHookConfigured` as vouching under a configured hook.** A receipt earned
  while no hook was configured says nothing about a hook configured now. `held` derives "no
  hook" from the current configuration, never from a receipt.
