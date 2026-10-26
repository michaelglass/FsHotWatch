# ADR-042: `checkSkipClasses` skips only on widenings a filtered run can discharge

Status: Accepted (2026-10-26)

## Context

A consumer's test project held one class that drives real repositories: 112 s of its 122 s
median whole-project wall. Impact selection already ran it only when a change reached it. The
cost came from widenings that run the whole project regardless of the change: 267 dependency
fanouts and 55 first-run-of-session refusals in one box's logs. `alwaysClasses` can only add
classes, and `args` is shared by `check` and `confirm`, so the repository could not say "skip
this class in a widened `check`, run it in `confirm`". A test that skips itself on an
environment variable would be a silent skip.

## Decision

- `tests.projects[].checkSkipClasses` lists the classes. `excludeFilterTemplate` says how to
  leave them out (`--filter-not-class {classes}` for xUnit v3). `checkSkipOn` lists the
  widenings that may skip them: `no-session-baseline` and `runtime-coverage-debt` by default,
  `dependency-fanout` on request.
- A project is run without them only when every reason it runs whole is a listed widening.
  `confirm`, an evidence gap, a project-level red, a coarse fallback, an unreadable ledger and
  a missing full-suite baseline always run them. A class the run selected by name (a changed
  symbol, a quarantined red, an always-class, a symbol-free edit) always runs.
- The launch records `ProjectExcept`. Its coverage is what its report shows ran, so it is
  never a whole-project run, a full-suite scope, a baseline or published evidence. The scope's
  `selectedBy` entry carries `skippedClasses`, and `check` prints the skip.

## Rejected

- **Skipping on a missing full-suite baseline.** Only a run that executes every project in
  full earns the baseline. A skip there would leave every later `check` owing it, at no
  verdict, until a `confirm`. The ticket's "no-baseline" lines were the session's first run,
  which is a different widening.
- **Skipping on an evidence gap.** A filtered result for a project with no whole-project run
  under the current model is a refusal in the evidence it earns, so that `check` could never
  go green, and the gap would widen every later run.
- **Skipping on a dependency fanout by default.** A binary change reaches every seam,
  including the listed classes'. It is opt-in.

## Consequences

- A runtime-coverage obligation is retired by the project's green even though the listed
  classes did not run. That is the trade the key declares, and the scope says so.
  `confirm` runs them.
- The skip is class-granular: one slow test in a large class skips the whole class.
