# ADR-070: Runtime decides, static is recorded with outcomes

`tests.projects[].runtimeSelection` used to take `"decide"` or `"shadow"`. It now takes one
value naming who DECIDES what runs and what is RECORDED beside the run. The new value
`"runtime/record-static"` keeps runtime coverage in charge and files, per completed run, the
set static selection alone would have run together with every run test's outcome. A miss by
static selection is then read straight off real outcomes. AUTOMATION-1686.

Status: Accepted (2026-11-14)

## Context

Intelligence wants to switch its test selection from runtime coverage to static. A one-line
edit runs about 14.8k tests under runtime and about 1.5k under static (AUTOMATION-1652). The
switch needs evidence that static selection does not miss regressions.

`"shadow"` (AUTOMATION-1645) points the wrong way for that evidence. Static decides and runs,
and runtime coverage's additions are only logged. Tests outside the static set never run, so
no shadow line can show a failure static selection missed; judging one needs a later full run
over the same tree, which mostly does not exist. No other mode recorded the static set: in
`"decide"` only the per-file `runtime coverage selected X -> P` lines were logged, and
`tests.traces.select: "shadow"` is again static-decides.

## Decision

1. **One enum, not a second flag.** `SelectionMode`:

   | value | decides what runs | recorded per run |
   |---|---|---|
   | `"runtime"` (`RuntimeDecides`) | static ∪ runtime coverage | nothing |
   | `"runtime/record-static"` (`RuntimeDecidesStaticRecorded`) | static ∪ runtime coverage, unchanged | a `StaticRecord` |
   | `"static/log-runtime"` (`StaticDecidesRuntimeLogged`) | static only | the `runtime-selection shadow:` line |

   `"decide"` and `"shadow"` parse as aliases of `"runtime"` and `"static/log-runtime"` for one
   release. Any other string is refused, naming the three spellings. Both modes that read
   runtime coverage without deciding by it alone are refused on a project that collects no
   coverage for impact.

2. **Recording is not allowed to touch the run.** `"runtime/record-static"` is told apart
   from `"runtime"` in exactly one place: the plugin builds a `StaticRecord.Recording` from it.
   Every obligation, selection, widening, skip, verdict and cache key reads the mode as
   `RuntimeDecides`. The record is computed from what the launch already decided, captured
   before any test runs, and filed after the run completes. A record that cannot be built or
   written is one warning and no record, never a different verdict.
   `RuntimeRecordStaticTests` runs the same tree and the same edit with the mode on and off,
   green and red, and asserts the same runner invocations, the same per-project verdicts and
   run verification, the same `Selected by:` lines and the same final status.

3. **The static set is derived from what ran.** Runtime coverage enters a launch only by
   forcing projects whole. `widenLaunch` computes the forced set twice: with runtime
   obligations (`ForceRunProjects`, what runs) and without them (`StaticForceRunProjects`; a
   coverage that did not ingest still forces, as under `"static/log-runtime"`). Their
   difference, among the recording projects, is the projects only runtime coverage took
   whole. `StaticRecord.staticOf` drops those from what ran, keeping in each what static causes
   named there: its outstanding failures (a project-level red is the whole project) and its
   always-classes. A move beside symbols takes every project whole for static selection too,
   so it leaves no runtime-only project.

   Known approximation: a launch that runtime coverage forced does not re-decide the paths
   that need nothing forced (the zero-affected skip, the symbol-free narrowing, always-only).
   Where static selection alone would have taken one of them, the static set holds what
   remained once the runtime-only projects were removed. That is what such a skip would have
   run, or close to it; the record carries both selections, so a reader can tell.

4. **The record and its judgement.**

   ```fsharp
   type StaticRecord =
       { Tree: RecordedTree                       // Tree identity | Unidentified
         RunId: Guid
         Static: Map<string, ProjectSelection>    // whole | classes | all-but
         Ran: Map<string, ProjectSelection>
         Outcomes: Map<string, ProjectOutcome>
         KnownFlaky: Set<TestId> }

   type ProjectOutcome =
       | Ran of rows: Map<TestId, TestOutcome>    // Passed | Failed | Skipped, after retries
       | RedUnnamed                               // failed, its report named no failed test
       | TimedOut                                 // killed at its timeout
       | ProvedNothing of reason: string          // errored or deferred

   type StaticJudgement =
       | StaticRanEverything                      // Static = Ran: no evidence either way
       | OutcomesUnknown of NonEmptySet<project>  // a run project verified nothing
       | Green
       | CaughtByStatic of insideFailed: NonEmptySet<Failure> * outsideFailed: Set<Failure>
       | MissedByStatic of outsideFailed: NonEmptySet<Failure>
   ```

   A test is inside the static set when its project is whole there, or its class is named,
   or (an exclude filter) its class is not excluded. A timeout or an unnamed red is a failure
   of its project, inside when static selection launched the project at all. `MissedByStatic`
   means a failure outside the static set on a run where nothing inside it failed. A project
   that verified nothing makes the record `OutcomesUnknown` rather than a guess. Outcomes are
   per project because a timeout and an errored host are facts about a project's run, not
   about any one test; the ticket's per-test `TimedOut` became `ProjectOutcome.TimedOut`.

   Only `CompletedRun.ofCompletion` makes the value the writer takes, and it refuses an
   `Aborted` completion: a cancelled or crashed run has no record. `KnownFlaky` is the run's
   tests whose history in `.fshw/test-history.json` flipped between pass and fail before the
   run launched. It is data beside the judgement and never removes a failure from it.

5. **Where records go.** One JSON line per completed run (`"v": 1`) appended to
   `<shared cache>/cache/selection-record/<repository namespace>/records.jsonl`, box-wide
   because the workspaces that file them are reclaimed nightly. Each record also logs one info
   line, `selection-record: {tree, staticTests, ranTests, judgement}`, so a missing store
   still leaves a trace.

## The bar for switching to static selection

Written before the first record is collected, as the supervisor's approval required.

- **Informative red gate:** a record judged `CaughtByStatic` or `MissedByStatic` on an
  identified tree. `Green`, `StaticRanEverything` and `OutcomesUnknown` records are not
  informative. Each tree counts once, by the first informative record filed for it, so the
  run that retries a red on the same tree adds nothing to N.
- **N = 150** informative red gates on distinct trees with **zero non-flaky
  `MissedByStatic`** pass the bar.
- **Checkpoint at 60:** the count, every `CaughtByStatic` record's outside failures and every
  flaky-only miss are reported, and the maintainer may stop the experiment there.
- **Any non-flaky `MissedByStatic` stops the count.** It is filed as a selection defect,
  root-caused and fixed in static selection; the count restarts from zero after the fix lands.
- **Flaky-only misses** (`isFlakyOnly`: every outside failure is a test in `KnownFlaky`, and
  no project-level failure) neither stop the count nor count toward N. Each one is listed at
  the checkpoint and at the end. A test that appears in a second flaky-only miss is treated as
  non-flaky from then on: a known flake that keeps failing only where static selection does
  not look is a selection-relevant failure until shown otherwise, so that second miss stops
  the count. If flaky-only misses exceed 10 % of the informative gates counted, the count
  pauses until the flaky tests are quarantined or fixed.

## Consequences

- Intelligence can set `"runtime/record-static"` on its coverage-collecting projects at no
  test-time cost and collect the evidence the bar asks for.
- `"decide"` and `"shadow"` keep working for one release. The release after drops them, or
  refuses them naming the new spelling.
- A record reader (the collector ticket) needs only the store: each line carries both
  selections, every outcome and its judgement, so judgements can be recomputed if the rule
  changes.

## Rejected

- **A second flag beside `runtimeSelection`** (`recordStatic: true`). Two booleans make four
  states, one of them meaningless (static decides and static is recorded). One enum names the
  three that exist.
- **Inverting the existing shadow** to run the runtime additions after a static run. It costs
  test time on every gate, and what it measures is the same outcome this mode reads for free.
