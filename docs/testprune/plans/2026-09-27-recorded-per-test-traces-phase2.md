# Recorded per-test traces, phase 2 (selection in shadow mode) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select tests from recorded per-test traces (rules R1–R7 below), next to today's static selection. The
trace selection is computed on every run, compared with the static one, and the comparison and every observed
miss are stored and reported. **Which tests run does not change in this phase** until a project passes the
promotion bar (D5) and its host is switched to `select: "live"`.

**Architecture:** A new `TraceSelect` module in TestPrune.Trace answers "which tests of this project must run
now?" from the trace store alone, plus the current symbol index: a test runs when it has no usable trace, or when
anything its trace names (a symbol version, a file input) no longer matches the tree. Code the probes cannot see
falls back to today's static walk for the changed symbols that need it. A `Shadow` module compares that answer
with the static selection and stores one row per (run, project) plus one row per miss in the trace database.
FsHotWatch computes both selections at the launch it already instruments for `CheckReach`, runs the static one,
and at completion records the comparison, the misses and the trace selection's failure recall. A mutation
harness (`test-prune-traces mutate`) and a report verb (`test-prune-traces shadow-report`) turn the promotion
bar into commands with thresholds.

**Tech Stack:** F# (net10.0 tooling, net8.0 recorder); Microsoft.Data.Sqlite (already used by the trace store);
xUnit v3 on Microsoft Testing Platform v2; Unquote; FsHotWatch plugin framework.

## Global Constraints

- Open-source repositories: no private tracker ids, no consumer project names, no session links in code,
  comments, commits, changelogs or docs. Say "a consumer". Commit trailer:
  `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- VCS is jj. Describe a change before editing it (`jj describe -m "…"`), then `jj new` after it is done. For a
  message with backticks, `$` or quotes, use `jj describe --stdin <<'MSG'` … `MSG`.
- F# only, formatted with Fantomas, FSharpLint clean, `TreatWarningsAsErrors=true`,
  `GenerateDocumentationFile=true` on packable projects. Use `List.isEmpty`, never `x = []` (the analyzer stage of
  the gate rejects it even though `lint-project` passes).
- `TestPrune.Trace.Recorder` stays **net8.0** with **FSharp.Core pinned to 8.0.403** and no other dependency.
- **Shadow never changes a run.** With `select: "shadow"`, the tests launched, their filters, the verdict, the
  evidence, the outstanding-failure ledger and every cache are byte-for-byte what `select: "off"` produces. A
  shadow failure (an exception, an unreadable store, a timeout) is one warning line and a missing shadow row,
  never a changed result. F3 has a test that pins this.
- **Live selection is a superset rule.** Where it is live, a project runs
  `trace ∪ R6-static ∪ R7-static ∪ R9-obligations`, and only if the project is **eligible** (D3). An ineligible
  project runs today's static selection.
- A trace is only ever read under the fingerprint it was recorded with (ADR 0006). Selection never reuses a
  trace across fingerprints.
- The trace database keeps its forward-only migrations. This phase appends migration 2 and never edits
  migration 1.
- With no `tests.traces.select` key, or `"select": "off"`, FsHotWatch behaves byte-for-byte as it does today.
- Verification:
  - Inner loop: `dotnet build`, then
    `dotnet run --project tests/<Project> --no-build -- --filter-class <Class>`. Run the new test class **and**
    every existing class whose file the task touched.
  - Gate: each repository's `mise run ci` (TestPrune) or `fshw check` (FsHotWatch). Read the fshw verdict file
    as well as the exit code.
  - Never pipe a gating command into `tail`, `head` or `grep`. Redirect the output to a file, then read the file
    and the exit status separately.

---

## Background: the rules, restated

The phase-0 design spike defined the selection rules R1–R9. They are restated here so this plan is
self-contained. `E` is the environment fingerprint (ADR 0006). A test's trace is the union of the scopes it
links: its own test scope plus inherited fixture, collection and pool scopes. It holds symbol versions
`(full name, version hash)` and file inputs `(kind, key, hash)`.

**Test t is selected iff any of:**

| Rule | Condition | Phase-1 data it reads |
|---|---|---|
| **R1 no trace** | t has no trace under the current E (a new test, a first run) | `trace_tests` |
| **R2 verifying trace** | some symbol version `(s, h)` in t's trace has `current(s) ≠ h`, or s no longer exists | `trace_entries`, `symbol_versions`, `TraceIngest.versionHashes` |
| **R3 file input** | some input `(kind, key, h)` in t's trace hashes differently now (a read file changed, a probed path appeared or vanished, a listed directory gained or lost an entry, or a swept assembly gained or lost a type (Task 11)) | `trace_inputs` |
| **R4 incomplete** | t's trace is marked incomplete (failed, skipped, no outcome, source drift, unmapped code, an untraced child process, overflow, tree moved, rejected dump) | `trace_tests.complete` |
| **R5 fingerprint** | t has a trace, but only under another E | `trace_tests.env_fingerprint` |
| **R6 static fallback** | t is in the static walk of a changed symbol that the probes cannot represent (see D2) | the index; the trace store's symbol facts |
| **R7 untraced input** | a changed non-source file that no trace input of t's project claims; the static file rules decide | `trace_inputs`; the index's `DependsOnFile` attributes; runtime coverage |
| **R8 DB tables** | a changed migration touching a table t read or wrote | **phase 4**; see D2 |
| **R9 obligations** | FsHotWatch's outstanding failures, pending verification and fanout, unchanged | FsHotWatch state |

**Why it is sound, modulo known cases.** A deterministic test whose outcome depends only on the code it
executed and the inputs it read keeps its outcome while none of those changed. R2 and R3 cover executed code and
read inputs, R1, R4 and R5 cover missing or partial evidence, R6 covers code the probes cannot see, and R7 covers
inputs the recorder cannot see. New code is reached only through a changed caller, which is in the trace (R2).
A trace recorded at any earlier tree stays valid while every entry still verifies, so traces refreshed only by
the tests that happened to run are still sound.

**Known unsound cases** and where this plan measures them:

| Case | Mitigation in this phase | Measured by |
|---|---|---|
| Once-per-process code (static init, module values) | each initializer's `S:<type>` scope is inherited by the tests that touch the type (see the 2026-09-27 note under D2); what no test inherits stays a fallback class (R6) | mutation harness |
| Memoised values (`Lazy`, a cached checker): only the first test to force them records the factory | order-unstable detector at ingestion (Task 4); those symbols are a fallback class (R6) | mutation harness; verified-failure misses |
| Non-determinism | failures of tests FsHotWatch's flakiness history flags are recorded as `flaky`, not `unexplained` | verified-failure misses |
| A change the content hash does not see | none by design; it surfaces as a test that failed while its trace verified | verified-failure misses |
| Reads the recorder cannot see | R7 plus the file census's per-test gaps (Task 9) | file census |
| Reflection over types: whole-assembly sweeps, union-case enumeration | type-set inputs (R3) and Type touches from `ldtoken`, generic instantiations and union-reflection shims (R2); Task 11 | Task 11 reproducers; union-type mutation samples |

---

## Decisions (with reasons)

### D1. The shadow-mode contract

- **Both selections are computed from the same inputs at the same instant.** FsHotWatch already retains, at the
  launch chokepoint, what `check` would have run (`WouldHaveRun`) so that a `confirm` can measure its failure
  recall (`CheckReach`). The trace selection is computed at that point, from the same seeds, changed files and
  index. Computing it later would compare it against a tree the static selection never saw.
- **Compared before obligations.** The comparison is between the static *symbol* selection (the tests
  `QueryAffectedTests` returns for the run's seeds, plus the static file rules) and the trace selection
  (R1–R7). R9 obligations, the dependency fanout, the unanalysable-file fallback and the quarantine are applied
  after either selection, identically, so they are left out of the comparison: including them would dilute both
  sides with the same tests.
- **Compared at two granularities.** FsHotWatch launches by test class (`--filter-class`). The row stores both
  test-level sets and class-level sets, plus `trace_class_tests` and `static_class_tests`: the tests each side
  would actually launch after class expansion. The payoff bar is judged on launched tests, because method-level
  savings that class expansion gives back are not savings.
- **Stored in the trace database, not in FsHotWatch state.** One `shadow_selections` row per (run, project) and
  one `shadow_misses` row per miss. The trace database already survives core schema bumps (ADR 0006), the report
  verb reads it without a daemon, and a consumer can inspect it with SQLite.
- **Written once, at completion.** The launch computes the selection and keeps it in memory on the launch record
  (as `WouldHaveRun` is kept). Completion writes the row with the outcome half filled in. A run that never
  completes (cancelled, superseded) leaves no row: the report counts completed runs only, and a half row would be
  a sample with no outcome.
- **Logged as one line per run:**
  `trace-shadow: <project> static 2032/41c, trace 85/9c (launch 312), trace-only 3, misses 0, R1 4 R2 61 R6 20, 184 ms`.

### D2. What counts as a miss, and how each is measured

| Term | Definition | Stored | Role |
|---|---|---|---|
| **static extra** | selected by static, not by trace | `static_only_classes`, counts | the payoff; not a miss |
| **trace addition** | selected by trace, not by static | `trace_only_tests` | a static-graph miss (interface dispatch was the measured cause); a quality signal for the graph |
| **verified-failure miss** | a test that **failed** in any run while the trace selection computed at that run's launch did **not** select it (its trace verified) | `shadow_misses` kind `verified-failure` | the direct safety measure; cause `flaky` when the flakiness history flags the test, else `unexplained` |
| **trace recall on a full run** | `CheckReach.measure` applied to the trace selection (class level, obligations applied) on a `confirm` | `shadow_selections.trace_recall`, next to `static_recall` | the same measure `check` already has, on the same failures |
| **mutation miss** | a test that fails when a sampled changed symbol is made to throw, and that the trace selection for "that symbol changed" does not select | `shadow_misses` kind `mutation`; `mutation_samples` | the only measure that does not wait for a real failure |
| **unverifiable share** | tests selected only by R1, R4 or R5 | `rule_counts` | the safety net's cost; high values mean traces are stale or incomplete, not unsafe |
| **fingerprint prediction miss** | the E predicted before the run differs from the E the run recorded | `fingerprint_outcome` | `unsafe-miss` when the predicted E had stored traces (selection trusted traces of another environment); `safe-miss` otherwise |

- **Why "failed while its trace verified" is the miss.** It is Shake's verifying-trace property tested
  directly: the trace claimed the test's inputs were unchanged, and its outcome changed anyway. It needs no
  baseline and works on every run that executed the test, not only on full runs. Every such row is either
  non-determinism (`flaky`), a missing probe, a hash that did not see a change, or an unrecorded input: all of
  them are defects to explain before promotion.
- **Tests without traces (the safety net).** R1, R4 and R5 select them outright, rather than falling back to
  static, because a test with no evidence has no business being skipped. The cost is the unverifiable share.
  A project whose complete-trace share is below 0.99 is **ineligible** (D3): live mode gives it the static
  selection, so a project that cannot be traced (the shadow bin refuses it, as it refuses TestPrune.Trace.Tests)
  is never selected by trace at all.
- **Stale traces after content-hash changes.** Three cases, each handled without a special rule:
  - The code changed: R2 selects every test whose trace names the old version. That is the rule working, not
    staleness.
  - What a content hash *means* changed (a core `SchemaVersion` bump): the hash scheme is part of E, so every
    trace becomes an R5 mismatch and is re-recorded. The unverifiable share spikes for one run; the report shows
    it.
  - The hash did not see a behavioural change: it surfaces as a verified-failure miss (`unexplained`).
  Traces recorded by `full-runs` only go stale between full runs (every edited symbol keeps selecting its tests
  until the next `confirm`), so shadow numbers on `check` runs are only meaningful under `record: "every-run"`.
  The report states the recording policy of every row's run.
- **R8 is not implemented in this phase.** SQL text lives in symbols, so a changed query is R2. Migrations
  applied by fixture code that reads them from disk are file inputs of the fixture scope, so a changed migration
  is R3 for every test linked to that fixture. Migrations the recorder cannot see (embedded resources) are
  unclaimed changed files, so R7. The mutation harness samples real changed symbols, which include SQL-emitting
  ones. Phase 4 adds table-level R8.

### D3. The trace selection (TestPrune.Trace `TraceSelect`)

- **Keys.** A test's key is `<project>|<class>|<method>` with `+` replaced by `.` on both sides
  (`TraceSelect.keyOf`, `TraceSelect.normalizeKey`). The index stores CLR class names built by FCS; the recorder
  stores xUnit's `TestClassName`. Both are CLR names; normalising nested-type separators removes the one
  difference either could introduce. The end-to-end test (Task 6) asserts every indexed fixture test maps to a
  stored trace.
- **Universe.** The project's tests as the index knows them (`SymbolStore.GetTestMethodsInProjects`). A stored
  trace of a test the index no longer has is ignored; an indexed test with no trace is R1.
- **Set-based verification.** R2 reads the distinct symbol versions the project's traces reference, compares each
  with `TraceIngest.versionHashes` (the function ingestion used, so the two cannot disagree), and asks the store
  for the tests linked to the stale ones in one query (`json_each`, ADR 0003). R3 does the same for distinct
  inputs, rehashing each with `TraceIngest.inputHash`, the function ingestion used. Neither reads a trace per
  test.
- **Fallback classes (R6).** A changed seed falls back to the static walk when:
  - `NotProbed`: no probe of the project's latest weave maps to it (test-assembly code, which is woven
    sites-only; abstract and interface members; record fields; symbols added since the last weave);
  - `Inline`: the index marks it `inline` (Task 1): its probe never fires where it was inlined;
  - `Literal`: it carries `[<Literal>]` (already indexed as `LiteralAttribute`): it has no code at all;
  - `StaticInit`: it executed inside a static constructor in some recorded run of the project, where hits go to
    `S:static-init` and no test;
  - `OrderUnstable`: the order-unstable detector (Task 4) saw it appear in or vanish from a test's trace while
    nothing that test executed changed.

  The static walk is today's `QueryAffectedTests`, with composition-root barriers and the per-seed fail-safe
  while they exist. Phase 3 retires them for eligible projects; this phase records each run's fallback seeds
  (`fallback_seeds`) so phase 3 has the data.

  > **Note, 2026-09-27 (static-init inheritance landed after this plan).** Static-init hits no longer go to one
  > `S:static-init` scope that no test links. The recorder keeps one `S:<type>` scope per type initializer, and
  > ingestion (`RunScopes.staticInheritance`) links it to every test that ran code of that type or code in the
  > source file its initializer is in, transitively through the initializers it touched. An initializer that
  > cannot be placed (no manifest row names its type: a `SitesOnly` test assembly, or an unnamed one) is linked
  > to every test. So a static-init symbol and the files its initializer read are in the traces of the tests
  > that depend on them, and R2/R3 verify them like any other entry. What this changes for R6:
  > - `StaticInit` is no longer needed for soundness where the scope was linked: R2 already selects those tests.
  >   It is still needed for a symbol whose static-init scope no test of the run inherited (its type was
  >   initialized but no test touched it: nothing links, so no trace would select on a change to it).
  > - The fact can therefore narrow to "executed only in static-init scopes that no test linked", which is
  >   computable at ingestion from `trace_test_scopes`. Keeping the broader "executed in any static
  >   constructor" fact is still sound, only wider.
  > - Everything in an unplaceable initializer reaches every test's trace, so a change there selects the
  >   whole project through R2, not R6.
- **Why the probed set is stored and not recomputed.** Selection runs before a run prepares its shadow bin, so the
  manifest of the build about to run is not available. The latest ingested weave's joined symbols are. A symbol
  added since then is `NotProbed` and falls back, which is conservative. It is stored per (project, E) as
  `trace_symbol_facts` rows, replaced by every ingestion.
- **R7 claims.** A changed file is claimed when a trace input of the project has it as its key, or has its
  directory as a `list` key. An indexed source file is handled by symbols. Anything else is unclaimed: the static
  file rules decide (`ImpactAnalysis.selectTests` over the unclaimed files, which applies `DependsOnFile`,
  runtime coverage and the `.fsproj` rule), and the tests the file census found reading repository files without
  a recorded input (Task 9) are added.
- **Eligibility.** A project is eligible when at least 0.99 of its universe has a complete trace under the
  current E. The threshold matches phase 1's census bar.

### D4. Predicting the environment fingerprint before a run

Selection needs the current E before the test process exists, and E includes the runtime the process will load
(`RuntimeInformation.FrameworkDescription`).

- **Chosen:** `Fingerprint.predict` computes every component from disk and resolves the runtime with the host's
  roll-forward rules (`RuntimeResolution`, Task 3) over `<App>.runtimeconfig.json` and the runtimes installed
  under `DOTNET_ROOT`. The recorder's `RuntimeIdentity` type is the one place the runtime, OS and architecture
  strings are produced, and both the dump writer and `predict` use it, so the strings cannot drift.
- **Validated on every traced run.** Completion compares the predicted E with the E ingestion recorded and
  stores `hit`, `safe-miss` or `unsafe-miss` (D2). The bar is 0 unsafe misses.
- **Rejected: reuse the E of the project's latest recorded run.** A runtime update between two runs would then
  trust traces of the old runtime for one run, with nothing to notice it. Prediction closes that window except
  where the resolver is wrong, and the validation measures exactly that.
- **Rejected: launch the app once to ask.** It costs a process start per project per run, which is the latency
  shadow is meant to avoid measuring.

### D5. The promotion bar: every item is a command with a threshold

Measured per test project by
`dotnet tool run test-prune-traces shadow-report --project <P>` (exit 0 when every bar passes). With no
`--since`, the window starts at the project's first shadow row, so it grows until the counts are met:

| Bar | Measured from | Threshold |
|---|---|---|
| Window | `shadow_selections` rows | count-driven: ≥ 100 impact-run rows and ≥ 10 full-run rows, over **at least** 14 days between the first and last row (the window runs until the counts are met, however long that takes); every impact-run row from a run that recorded under `every-run` |
| Verified-failure misses | `shadow_misses` kind `verified-failure` on eligible rows | 0 `unexplained`; each `flaky` one listed in the output |
| Trace recall on full runs | `trace_recall` of full-run rows | every measured value is `r/r` (recall 1.0); rows where recall is not measurable are counted and listed, not failed |
| Mutation | `mutation_samples`, `shadow_misses` kind `mutation` | ≥ 50 `measured` symbol samples, and ≥ 5 `measured` union-type samples (all of them when the project has fewer than 5 union types whose mutant fails a test); 0 missed tests across both kinds |
| Eligibility | `eligible` | ≥ 0.95 of rows eligible |
| Fingerprint prediction | `fingerprint_outcome` | 0 `unsafe-miss`; `safe-miss` ≤ 0.05 of traced rows |
| Latency | `select_ms` | p95 ≤ 1000 ms |
| Payoff | `trace_class_tests / static_class_tests`, impact rows with `static_class_tests > 0` | median ≤ 0.5 |

The payoff bar is not a safety bar. It is there because live mode has a cost (a new code path in every `check`),
and a project where the trace selection launches more than half of what static launches is not worth it yet.

**Before promotion**, the project must also meet phase 1's bar on that consumer (census, audit, overhead, file
census, coverage parity) and run `record: "every-run"` (phase 1's Task F5).

**Promotion** is a one-line `.fshw.json` change per repository: `"select": "live"`. It applies to every eligible
project of the repository; an ineligible one keeps static selection. Live mode keeps writing shadow rows (with
the static selection as the comparison side), so the misses and recall keep being measured after the switch.

### D6. File inputs and child processes

- **File reads captured at call sites** (phase 1's redirects) are trace inputs of the scope that read them: a
  fixture's reads are shared by every test linked to that fixture. R3 rehashes each distinct input once per
  selection.
- **What call sites miss** is measured by the file census (phase 1 bar: ≤ 5 % symmetric difference). Phase 2
  gives the census a use: `test-prune-traces file-census --store` writes the tests that fail when run outside the
  repository but recorded no repository input (`trace_file_gaps`). R7 adds them whenever an unclaimed
  non-source file changed. The census is re-run (and the gaps replaced) as part of the nightly job the mutation
  harness runs in.
- **The process-level `open()` interposer stays deferred** (ADR 0008). It would turn the census's gap list into
  per-process read sets. Reconsider it if the gap list grows past 1 % of a project's tests.
- **Traced child processes** (a woven .NET child) already record into the scope that started them, so their code
  is in the parent test's trace.
- **Untraced child processes** (`dotnet`, `sh`, `sleep`, `chmod` on TestPrune's own suite: 14 tests) mark the
  starting test incomplete, so R4 selects it on every run. That is sound. Whether a trusted-executable allowlist
  is worth its risk is decided by a measured threshold (Decisions from review, Q2).

### D7. FsHotWatch integration and rollout order

**Config** (`.fshw.json`):

```json
{
  "tests": {
    "traces": {
      "record": "every-run",
      "select": "shadow"
    }
  }
}
```

- `select`: `"off"` (the default when the key is absent), `"shadow"` or `"live"`. Unknown values are a
  `ConfigError`. `select` other than `off` with `record: "off"` is a `ConfigError` (nothing would ever be
  selected from). `live` with `record: "full-runs"` is a `ConfigError` (traces would go stale between full
  runs, D2).
- **Mode branching stays in `TestMode.fs`.** `TestMode.selectsByTrace policy mode` is true only for `live` under
  `ImpactSelection`. `confirm` runs everything by definition. The shadow computation runs in both modes: under
  `PassThrough` it is what feeds the trace recall.
- **Where:** a new `src/FsHotWatch.TestPrune/TraceShadow.fs` holds everything but the two call sites in
  `TestPrunePlugin.fs`: after `affectedTestsList` at the launch chokepoint, and next to
  `CheckReach.classifyEvidence` at completion.
- **Symbol store:** the flushed index (`TestPrune.Ports.toSymbolStore db`), the same one ingestion joins
  against.

**Release order** (topological; each step waits for the previous to be restorable from NuGet):

1. TestPrune.Core (`core-v`, Task 1: `inline` indexed; index `SchemaVersion` 18 → 19).
2. TestPrune.Trace + Recorder + Cli (`trace-v`, Tasks 2–10).
3. FsHotWatch (Tasks F1–F4), bundling both.
4. TestPrune's own FsHotWatch pin (Task D1): the dogfood shadow window starts.
5. A consumer's FsHotWatch pin, per its own plan.

The core bump changes the hash scheme folded into E, so every stored trace becomes an R5 mismatch once and is
re-recorded by the next full run. That is intended (ADR 0006).

**Consumer rollout order** (the consumer plan owns the steps; this is the order and the gates):

1. Phase 1's bar met on the consumer's full runs, then `record: "every-run"`.
2. `select: "shadow"` for the whole repository. Shadow is inert to results, so there is no reason to stage it.
3. Nightly: `test-prune-traces file-census --store` and `test-prune-traces mutate --samples 10` per project.
4. Promote per repository once every project that matters passes `shadow-report`. Order the consumer's work so
   the projects without pooled servers reach the bar first (unit before database before browser/integration):
   integration projects depend on the pool-scope propagation, which is the consumer-side risk.
5. Keep shadow rows flowing after promotion (D5) and re-run `shadow-report` weekly.

### D8. What phase 3 inherits

Phase 3 retires the composition-root barriers and the emptied-project fail-safe for eligible projects. It needs:

- the fallback seeds and their reasons per run (`fallback_seeds`), to show that no R6 seed reached a marker for 30
  days;
- live mode itself (F4), where the barrier walk then only serves R6.

`QueryAffectedTests` and `SeedCoverage` are not changed in this phase.

### Decisions from review

The first draft's open questions, as settled in review:

- **Q1: the Core change is in (Task 1).** `inline` members are indexed as the synthetic attribute
  `TestPrune.Inline`, and the index schema goes from 18 to 19. **Migration cost, paid once:** every host rebuilds
  its index on the next start, as it does for any schema bump. The hash scheme folded into E is the schema
  version, so every stored trace becomes an R5 mismatch and is re-recorded by the next full run. Until that
  run, every test of every project is selected by R5, so every project is ineligible and live mode falls back to
  static. Nothing is deleted: the old traces stay in the file until garbage collection drops their fingerprint.
  The alternative (treat every never-executed changed seed as a fallback) needs no Core change, but it brings
  back the over-selection of code that runs only in production, which is what the design set out to remove.
- **Q2: the trusted-executable allowlist is deferred, with a measured decision point.** Each shadow row counts
  `child_untraced_tests`: the tests selected only by R4 with a `child-process-untraced` reason.
  `shadow-report` prints the median of `child_untraced_tests / trace_class_tests` over impact rows. **If that
  median exceeds 0.05** (tests held by untraced children are more than 5 % of what the trace selection
  launches), the allowlist becomes a task. Below it, R4 stays as it is.
- **Q3: reflection is answered, and closed by Task 11.** A consumer census found no DI or route scanning (every
  registration is explicit), but two reflection shapes that traces as specified would miss:
  1. **Whole-assembly type sweeps.** Meta-tests enumerate every type or method of an assembly (wire-contract
     checks, record-field lint, test-class lint, checks over generated database types), and one production audit
     loads an assembly by name and enumerates its types, reached only from a test. A new type in an **existing**
     file changes their result while no traced symbol changes: the new type has no probe yet, so it is
     `not-probed`, and the static walk from it finds no caller, because the call is by reflection.
  2. **Union-case enumeration in production code.** `FSharpType.GetUnionCases` over a union, `all` lists built
     from it, a generic `unionCases<'T>` helper, and `GetCustomAttributes` over the cases. A new case changes
     behaviour. Adding a case does change the union type's content hash (the header hash keeps the name and order
     of every case; Task 11 pins it), but the Type symbol is in the enumerating test's trace only if something
     probed it. `typeof<U>` compiles to `ldtoken`, which phase 1 does not probe; a generic helper's `typeof<'T>`
     names no type at all; and code that reflects over a value (`x.GetType()`) has hit only the value's **case**
     probes, which join to the DuCase symbols, not to the Type.

  Reflection over the type of a value the test itself constructed is already covered where it reads that value's
  members (its probes are in the trace). Task 11 adds a **type-set input** (R3) for the first shape and makes
  `ldtoken`, repository generic instantiations and the union-reflection APIs **touch the Type** (R2) for the
  second. The mutation harness gains a union-type sample kind, and the promotion bar requires it.
- **Q4: the window is count-driven.** The D5 window bar requires the counts, over a minimum of 14 days; the
  window runs until the counts are met. The counts are not lowered for a repository with few `check` runs.
- **Q5: method-level filters are the named next task** if class expansion alone fails the payoff bar: the
  method-level launch ratio (`trace_tests / static_tests`) passes 0.5 while the class-level one
  (`trace_class_tests / static_class_tests`) does not. The report prints both ratios, so the case is visible.

---

## File structure

**TestPrune (this repository)**

| File | Responsibility | Task |
|---|---|---|
| `src/TestPrune.Core/AstAnalyzer.fs` | index `inline` as the synthetic attribute `TestPrune.Inline` | 1 |
| `src/TestPrune.Core/Database.fs` | `SchemaVersion` 18 → 19 | 1 |
| `src/TestPrune.Trace/TraceStore.fs` | migration 2; verification facts; symbol facts; shadow rows, misses, mutation samples, file gaps | 2 |
| `src/TestPrune.Trace.Recorder/RuntimeIdentity.fs` (new) | the runtime/OS/arch strings, shared by dumps and prediction | 3 |
| `src/TestPrune.Trace/RuntimeResolution.fs` (new) | runtimeconfig + installed runtimes → runtime description | 3 |
| `src/TestPrune.Trace/Fingerprint.fs` | `predict`, `toJson`, `diff` | 3 |
| `src/TestPrune.Trace/TraceIngest.fs` | `inputHash` public; probed and static-init facts; order-unstable detector; fingerprint inputs stored | 4 |
| `src/TestPrune.Trace/TraceSelect.fs` (new) | R1–R7, eligibility | 5 |
| `src/TestPrune.Trace/Shadow.fs` (new) | comparison, misses, row assembly | 6 |
| `src/TestPrune.Trace.Recorder/Probes.fs`, `Contract.fs` | mutation mode (`TESTPRUNE_TRACE_MUTATE`) | 7 |
| `src/TestPrune.Trace/Mutation.fs` (new) | sample, run, judge | 7 |
| `src/TestPrune.Trace/ShadowReport.fs` (new) | the D5 bars | 8 |
| `src/TestPrune.Trace/FileCensus.fs` | `--store` gaps | 9 |
| `src/TestPrune.Trace/Model.fs` | `InputKind` gains `TypeSet`, `TypeReflected` | 11 |
| `src/TestPrune.Trace.Recorder/Reflect.fs` (new) | reflection shims: type sets, reflected types, mutated types | 11 |
| `src/TestPrune.Trace/Redirects.fs`, `SiteProbes.fs` | reflection redirects; `ldtoken` and generic-instantiation Type probes | 11 |
| `tests/TraceFixtures/src/FxLib/Shapes.fs`, `tests/FxTests/ReflectionTests.fs` | the reflection fixture | 11 |
| `src/TestPrune.Trace.Cli/Program.fs` | `mutate`, `shadow-report`, `file-census --store` | 7, 8, 9 |
| `tests/TestPrune.Trace.Tests/*.fs` | tests for all of the above | 2–9, 11 |
| `docs/adr/0009-shadow-selection-contract.md`, `0010-fingerprint-prediction.md`, `0011-reflection-inputs.md` | decisions | 10 |

**FsHotWatch**

| File | Responsibility | Task |
|---|---|---|
| `src/FsHotWatch.TestPrune/Traces.fs` | `TraceSelectPolicy`, `TraceSettings.Select`, `parseSelect` | F1 |
| `src/FsHotWatch.Cli/DaemonConfig.fs` | parse and validate `tests.traces.select` | F1 |
| `src/FsHotWatch.TestPrune/TestMode.fs` | `selectsByTrace` | F2 |
| `src/FsHotWatch.TestPrune/TraceShadow.fs` (new) | launch-time selection, completion-time recording, live classes | F3, F4 |
| `src/FsHotWatch.TestPrune/TestPrunePlugin.fs` | two call sites; `TestRunLaunch.TraceShadow` | F3, F4 |

---

## Cross-task contracts

Fixed here so tasks that run in parallel agree on names. Every type below is public.

**Core (Task 1):**

```fsharp
// TestPrune.AstAnalyzer
[<Literal>]
let InlineAttributeName = "TestPrune.Inline"
// TestPrune.Database.SchemaVersion = 19
```

**Recorder (Tasks 3, 7):**

```fsharp
namespace TestPrune.Trace.Recorder

/// The strings a dump records for its environment. The one producer: the dump writer and
/// `Fingerprint.predict` both call it.
[<AbstractClass; Sealed>]
type RuntimeIdentity =
    static member Runtime: unit -> string   // RuntimeInformation.FrameworkDescription, e.g. ".NET 10.0.0"
    static member Os: unit -> string        // what the dump's "os" field holds today
    static member Arch: unit -> string      // string RuntimeInformation.ProcessArchitecture

/// Thrown by a probe whose id is listed in TESTPRUNE_TRACE_MUTATE.
type MutantHitException =
    inherit System.Exception
    new: id: int -> MutantHitException
    member Id: int

// Contract.MutateEnv = "TESTPRUNE_TRACE_MUTATE"   (comma-separated probe ids)
```

**Trace store (Task 2)**, in `TestPrune.Trace.TraceStore`:

```fsharp
type SymbolFact =
    | Probed        // "probed": some probe of the latest weave joins to the symbol; replaced per ingestion
    | StaticInit    // "static-init": executed inside a static constructor; accumulated
    | Unstable      // "unstable": appeared in or vanished from a trace with nothing changed; accumulated

type VerificationFacts =
    {
        /// Stored test key (unnormalised) -> (complete, incomplete-reason codes).
        Tests: Map<string, bool * string list>
        /// Distinct (symbol_versions.id, full name, version hash) the project's traces reference.
        Versions: (int64 * string * string) list
        /// Distinct (kind, key, hash) inputs of the project's traces.
        Inputs: (string * string * string) list
        /// Stored test keys that have a trace only under another fingerprint.
        OtherFingerprintKeys: Set<string>
        Probed: Set<string>
        StaticInit: Set<string>
        Unstable: Set<string>
    }

type ShadowRunMode =
    | ImpactRun   // "impact": a `check`
    | FullSuiteRun // "full": a `confirm` or nightly (not `RunKind.FullRun`, which names a recorded run)

type SelectPolicy =
    | ShadowPolicy   // "shadow"
    | LivePolicy     // "live"

type FingerprintOutcome =
    | PredictionHit      // "hit"
    | SafeMiss           // "safe-miss"
    | UnsafeMiss         // "unsafe-miss"
    | NotTraced          // "untraced": the run recorded no traces for the project

type ShadowSelectionRow =
    { RunId: string
      TestProject: string
      Mode: ShadowRunMode
      Policy: SelectPolicy
      RecordPolicy: string          // "full-runs" | "every-run"
      TreeHash: string
      RecordedAt: System.DateTimeOffset
      PredictedFingerprint: string
      ActualFingerprint: string option
      FingerprintOutcome: FingerprintOutcome
      Eligible: bool
      CompleteShare: float
      Universe: int
      StaticTests: int
      TraceTests: int
      StaticClasses: int
      TraceClasses: int
      StaticClassTests: int
      TraceClassTests: int
      RuleCounts: Map<string, int>  // "R1".."R7" -> tests carrying that rule
      StaticOnlyClasses: string list   // at most 200, sorted
      TraceOnlyTests: string list      // at most 200, sorted
      ChildUntracedTests: int          // selected only by R4 with a child-process-untraced reason (Q2 decision point)
      Seeds: string list
      FallbackSeeds: Map<string, string>  // seed -> FallReason code
      SelectMs: int64
      TraceRecall: string   // "r/t" | "n/a: <reason>"
      StaticRecall: string }

type MissKind =
    | VerifiedFailure   // "verified-failure"
    | MutationMiss      // "mutation"

type MissCause =
    | Flaky        // "flaky"
    | Unexplained  // "unexplained"

type ShadowMiss =
    { RunId: string          // a run id, or "mutation:<sample id>"
      TestProject: string
      TestKey: string        // normalised
      Kind: MissKind
      Cause: MissCause
      Detail: string }

type MutationOutcome =
    | Measured      // "measured": the mutant made at least one test fail
    | NoFailure     // "no-failure": no test failed
    | NotMutable    // "not-mutable": no probe id maps to the symbol
    | RunFailed     // "run-failed": no CTRF report

type MutationKind =
    | SymbolMutant     // "symbol": the probes of a changed seed throw
    | UnionTypeMutant  // "union-type": every probe and reflective use of a union type throws (Task 11)

type MutationSample =
    { TestProject: string
      Kind: MutationKind
      Symbol: string
      TreeHash: string
      RecordedAt: System.DateTimeOffset
      Outcome: MutationOutcome
      Failing: int
      Selected: int
      Missed: string list }

// Store members added:
//   VerificationFacts(testProject, envFingerprint) : VerificationFacts
//   TestsWithVersions(testProject, envFingerprint, versionIds: int64 list) : (string * string) list      // (test key, symbol)
//   TestsWithInputs(testProject, envFingerprint, inputs: (string * string) list) : (string * string * string) list  // (test key, kind, key)
//   RecordSymbolFacts(testProject, envFingerprint, fact: SymbolFact, symbols: (string * string) list) : unit     // (symbol, evidence)
//   RunFingerprint(runId, testProject) : string option
//   LatestFingerprint(testProject) : string option
//   HasTraces(testProject, envFingerprint) : bool
//   RecordShadow(row: ShadowSelectionRow, misses: ShadowMiss list) : unit
//   RecordMutation(sample: MutationSample, misses: ShadowMiss list) : unit
//   ShadowRows(testProject, since: DateTimeOffset) : ShadowSelectionRow list
//   Misses(testProject, since: DateTimeOffset) : (ShadowMiss * DateTimeOffset) list
//   MutationSamples(testProject, since: DateTimeOffset) : MutationSample list
//   RecordFileGaps(testProject, testKeys: string list) : unit     // replaces the project's gaps
//   FileGaps(testProject) : Set<string>
```

**Selection (Task 5)**, `TestPrune.Trace.TraceSelect`:

```fsharp
type FallReason = NotProbed | Inline | Literal | StaticInitOnly | OrderUnstable

type Rule =
    | NoTrace                                 // R1
    | SymbolChanged of symbol: string         // R2
    | InputChanged of kind: string * key: string   // R3
    | Incomplete of reasons: string list      // R4
    | FingerprintMismatch                     // R5
    | StaticFallback of reason: FallReason    // R6
    | UntracedInput of file: string           // R7

type Request =
    { TestProject: string
      EnvFingerprint: string
      Tests: TestPrune.AstAnalyzer.TestMethodInfo list
      CurrentVersions: Map<string, string>
      CurrentInput: string -> string -> string        // kind -> key -> hash now
      Seeds: string list
      ChangedFiles: string list                       // repo-relative, '/'-separated
      IsIndexed: string -> bool
      AttributesOf: string -> string list
      StaticWalk: string list -> TestPrune.AstAnalyzer.TestMethodInfo list
      FileFallback: string list -> TestPrune.AstAnalyzer.TestMethodInfo list option   // None = every test
      FileGaps: Set<string> }

type Selection =
    { TestProject: string
      Eligible: bool
      CompleteShare: float
      Universe: Set<string>             // normalised keys
      Selected: Map<string, Rule list>  // normalised key -> rules, in rule order
      FallbackSeeds: Map<string, FallReason> }

val EligibilityThreshold: float      // 0.99
val normalizeKey: string -> string
val keyOf: TestPrune.AstAnalyzer.TestMethodInfo -> string
val ruleCode: Rule -> string         // "R1".."R7"
val fallCode: FallReason -> string   // "not-probed" | "inline" | "literal" | "static-init" | "order-unstable"
val fallReason: TraceStore.VerificationFacts -> (string -> string list) -> string -> FallReason option
val staticFileFallback: TestPrune.Ports.SymbolStore -> string list -> TestPrune.AstAnalyzer.TestMethodInfo list option
val select: TraceStore.Store -> Request -> Selection
```

**Shadow (Task 6)**, `TestPrune.Trace.Shadow`:

```fsharp
type Comparison =
    { StaticTests: Set<string>; TraceTests: Set<string>
      StaticOnly: Set<string>; TraceOnly: Set<string>
      StaticClasses: Set<string>; TraceClasses: Set<string>
      StaticClassTests: int; TraceClassTests: int }

val compare: universe: TestMethodInfo list -> staticSelection: TestMethodInfo list -> trace: TraceSelect.Selection -> Comparison
val verifiedFailureMisses: runId: string -> trace: TraceSelect.Selection -> failedKeys: string list -> isFlaky: (string -> bool) -> TraceStore.ShadowMiss list
val fingerprintOutcome: predicted: string -> actual: string option -> predictedHadTraces: bool -> TraceStore.FingerprintOutcome
val row: ShadowRowInputs -> TraceStore.ShadowSelectionRow
```

---

## Task graph and parallel groups

```text
G0:  T1 (Core)  ∥  T2 (store)  ∥  T3 (prediction)  ∥  F1  ∥  F2
G1:  T4 (ingest; T2, T3)  ∥  T7a (recorder mutation mode; none)
G1b: T5 (select; T1 interface, T2, T4)
G2:  T6 (shadow; T5)  ∥  T9 (census gaps; T2)  ∥  T11 (reflection inputs; T4, T5 for its reproducers)
G3:  T7b (mutation harness + verb; T5, T7a, T11)  ∥  T8 (report; T2, T6)
G4:  T10 docs + release: core-v (T1), then trace-v (T2–T9, T11)
G5:  F3 shadow wiring (T10 released, F1, F2)  →  F4 live wiring  →  FsHotWatch release
G6:  D1 TestPrune dogfood shadow window (F3/F4 released and pinned)
G7:  D2 promotion of TestPrune to live (D1 bar met)   ∥   consumer rollout (its own plan)
```

Every task ships on its own:

- New modules are inert until a host calls them.
- Migration 2 adds tables and a column; a phase-1 reader ignores them.
- F3 and F4 are gated on `tests.traces.select ≠ off`.

---

## Task 1: Index `inline` as a synthetic attribute (TestPrune.Core)

**Files:**

- Modify: `src/TestPrune.Core/AstAnalyzer.fs` (the member-attribute loop near line 2524; add the literal next to
  the other `[<Literal>]` names near the top)
- Modify: `src/TestPrune.Core/Database.fs:365` (`SchemaVersion = 19`)
- Modify: `src/TestPrune.Core/CHANGELOG.md`
- Test: `tests/TestPrune.Tests/AstAnalyzerTests.fs` (new nested module at the end, in the `FCS-AstAnalyzer`
  collection)

**Interfaces:**

- Produces: `TestPrune.AstAnalyzer.InlineAttributeName` (`"TestPrune.Inline"`); every `inline` member gets one
  `SymbolAttribute { AttributeName = InlineAttributeName; ArgsJson = "[]" }`. `[<Literal>]` values are already
  indexed with `AttributeName = "LiteralAttribute"`; this task pins that with a test.

**Migration cost** (accepted in review, see "Decisions from review", Q1): every host rebuilds its index once, and
every stored trace becomes an R5 mismatch until the next full run re-records it. State this in the changelog
entry so consumers expect one slow run and one full-suite `check` after upgrading.

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "Index inline members as the TestPrune.Inline attribute"
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/TestPrune.Tests/AstAnalyzerTests.fs`:

```fsharp
[<Collection("FCS-AstAnalyzer")>]
module ``Traceability attributes`` =

    let private attributesOf (result: AnalysisResult) (name: string) =
        result.Attributes
        |> List.filter (fun a -> a.SymbolFullName = name)
        |> List.map (fun a -> a.AttributeName)

    [<Fact>]
    let ``an inline function carries the TestPrune.Inline attribute`` () =
        let result =
            analyze
                """
module M
let inline twice x = x + x
let plain x = x + 1
"""

        test <@ attributesOf result "M.twice" = [ InlineAttributeName ] @>
        test <@ attributesOf result "M.plain" |> List.isEmpty @>

    [<Fact>]
    let ``an inline member carries the TestPrune.Inline attribute`` () =
        let result =
            analyze
                """
module M
type T() =
    member inline _.Twice(x: int) = x + x
"""

        test <@ result.Attributes |> List.exists (fun a -> a.SymbolFullName.EndsWith "Twice" && a.AttributeName = InlineAttributeName) @>

    [<Fact>]
    let ``a literal is already indexed as LiteralAttribute`` () =
        let result =
            analyze
                """
module M
[<Literal>]
let Answer = 42
"""

        test <@ attributesOf result "M.Answer" = [ "LiteralAttribute" ] @>
```

- [ ] **Step 3: Run the tests and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Tests --no-build -- --filter-class "TestPrune.Tests.AstAnalyzerTests+Traceability attributes"`
Expected: build error `The value or constructor 'InlineAttributeName' is not defined`.

- [ ] **Step 4: Implement**

Near the other literals at the top of `AstAnalyzer.fs`:

```fsharp
/// The synthetic attribute the analyser records on every `inline` member. `inline` is a
/// keyword, not an attribute, so FCS reports no attribute for it; recording it here lets
/// consumers of the attribute index (trace selection) see which members are inlined at
/// their call sites and so leave no trace of their own.
[<Literal>]
let InlineAttributeName = "TestPrune.Inline"
```

In the member loop, directly after the `for attr in mfv.Attributes do … ` block (inside the same `try`):

```fsharp
                            if mfv.InlineAnnotation = FSharpInlineAnnotation.AlwaysInline then
                                attributes <-
                                    { SymbolFullName = memberName mfv
                                      AttributeName = InlineAttributeName
                                      ArgsJson = "[]" }
                                    :: attributes
```

In `Database.fs`: `let SchemaVersion = 19`. The comment above it lists what each bump changed; add
`19: inline members carry the TestPrune.Inline attribute.` in the same style.

- [ ] **Step 5: Run the tests and the touched classes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Tests --no-build -- --filter-class "TestPrune.Tests.AstAnalyzerTests+Traceability attributes"`
Expected: 3 passed.
Run the schema-version pins too: `dotnet run --project tests/TestPrune.Tests --no-build -- --filter-class TestPrune.Tests.DatabaseTests`
Expected: PASS. If a test pins `SchemaVersion = 18`, update it to 19 in the same commit.

- [ ] **Step 6: Changelog and commit**

`src/TestPrune.Core/CHANGELOG.md`, under Unreleased:
`- feat!: inline members are indexed with the synthetic attribute TestPrune.Inline. Index schema 19: existing indexes are rebuilt on first start, and recorded traces (whose fingerprint includes the schema) are re-recorded by the next full run.`

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t1-ci.log" 2>&1; echo "exit=$?"
jj new
```

Expected: `exit=0`.

---

## Task 2: Trace store migration 2 and the selection queries

**Files:**

- Modify: `src/TestPrune.Trace/TraceStore.fs`
- Test: `tests/TestPrune.Trace.Tests/TraceStoreTests.fs` (append a module `Phase2`)

**Interfaces:**

- Consumes: the phase-1 `Store`, `RecordRun`, `CollectGarbage`.
- Produces: every type and member in "Cross-task contracts → Trace store".

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "Trace store: migration 2 for selection facts and shadow rows"
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/TestPrune.Trace.Tests/TraceStoreTests.fs` (the file already has a helper that opens a store in
a temp directory; reuse it as `withStore`; if it is named differently, use that name):

```fsharp
module Phase2 =
    open System
    open TestPrune.Trace.Model
    open TestPrune.Trace.TraceStore

    let private run id fp : TraceRun =
        { RunId = id
          TestProject = "P"
          TreeHash = "t"
          EnvFingerprint = fp
          RecordedAt = DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)
          Kind = RunKind.FullRun
          Status = Recorded
          Reason = ""
          StatsJson = "{}" }

    let private scope key symbols inputs : ScopeContent =
        { Key = key; Symbols = symbols; Inputs = inputs }

    let private testTrace key reasons scopes : TestTrace =
        { TestKey = key; Status = Some Passed; Reasons = reasons; ScopeKeys = scopes }

    /// Two tests: A executes f and reads a.json through its class fixture; B executes g and
    /// is incomplete. A third test has a trace only under another fingerprint.
    let private seeded (store: Store) =
        store.RecordRun(
            run "r1" "E1",
            [ scope "T:a" [ "N.f", "hf" ] []
              scope "C:K" [] [ "read", "a.json", "ha" ]
              scope "T:b" [ "N.g", "hg" ] [] ],
            [ testTrace "P|K|a" [] [ "T:a"; "C:K" ]
              testTrace "P|K|b" [ NoOutcome ] [ "T:b" ] ]
        )

        store.RecordRun(run "r0" "E0", [ scope "T:c" [ "N.f", "hf" ] [] ], [ testTrace "P|K|c" [] [ "T:c" ] ])

    [<Fact>]
    let ``migration 2 applies to a phase-1 file and keeps its traces`` () =
        let path = IO.Path.Combine(IO.Directory.CreateTempSubdirectory().FullName, "t.db")

        using (Store.OpenWith(List.truncate 1 migrations, path)) (fun v1 ->
            v1.RecordRun(run "r1" "E1", [ scope "T:a" [ "N.f", "hf" ] [] ], [ testTrace "P|K|a" [] [ "T:a" ] ]))

        using (Store.Open path) (fun v2 ->
            test <@ TraceSchemaVersion = 2 @>
            test <@ v2.RunFingerprint("r1", "P") = Some "E1" @>
            test <@ (v2.VerificationFacts("P", "E1")).Tests.ContainsKey "P|K|a" @>)

    [<Fact>]
    let ``verification facts read the project's traces under one fingerprint`` () =
        withStore (fun _ store ->
            seeded store
            let f = store.VerificationFacts("P", "E1")
            test <@ f.Tests = Map.ofList [ "P|K|a", (true, []); "P|K|b", (false, [ "no-outcome" ]) ] @>
            test <@ f.Versions |> List.map (fun (_, n, h) -> n, h) |> List.sort = [ "N.f", "hf"; "N.g", "hg" ] @>
            test <@ f.Inputs = [ "read", "a.json", "ha" ] @>
            test <@ f.OtherFingerprintKeys = set [ "P|K|c" ] @>)

    [<Fact>]
    let ``tests linked to stale versions and inputs, through inherited scopes`` () =
        withStore (fun _ store ->
            seeded store
            let f = store.VerificationFacts("P", "E1")
            let fId = f.Versions |> List.pick (fun (id, n, _) -> if n = "N.f" then Some id else None)
            test <@ store.TestsWithVersions("P", "E1", [ fId ]) = [ "P|K|a", "N.f" ] @>
            test <@ store.TestsWithInputs("P", "E1", [ "read", "a.json" ]) = [ "P|K|a", "read", "a.json" ] @>
            test <@ store.TestsWithVersions("P", "E1", []) |> List.isEmpty @>)

    [<Fact>]
    let ``probed facts are replaced, static-init and unstable facts accumulate`` () =
        withStore (fun _ store ->
            store.RecordSymbolFacts("P", "E1", Probed, [ "N.f", ""; "N.g", "" ])
            store.RecordSymbolFacts("P", "E1", Probed, [ "N.f", "" ])
            store.RecordSymbolFacts("P", "E1", StaticInit, [ "N.s", "" ])
            store.RecordSymbolFacts("P", "E1", StaticInit, [ "N.t", "" ])
            store.RecordSymbolFacts("P", "E1", Unstable, [ "N.u", "P|K|a" ])
            let f = store.VerificationFacts("P", "E1")
            test <@ f.Probed = set [ "N.f" ] @>
            test <@ f.StaticInit = set [ "N.s"; "N.t" ] @>
            test <@ f.Unstable = set [ "N.u" ] @>)

    let private shadowRow runId at : ShadowSelectionRow =
        { RunId = runId
          TestProject = "P"
          Mode = ImpactRun
          Policy = ShadowPolicy
          RecordPolicy = "every-run"
          TreeHash = "t"
          RecordedAt = at
          PredictedFingerprint = "E1"
          ActualFingerprint = Some "E1"
          FingerprintOutcome = PredictionHit
          Eligible = true
          CompleteShare = 1.0
          Universe = 2
          StaticTests = 2
          TraceTests = 1
          StaticClasses = 1
          TraceClasses = 1
          StaticClassTests = 2
          TraceClassTests = 2
          RuleCounts = Map.ofList [ "R2", 1 ]
          StaticOnlyClasses = []
          TraceOnlyTests = []
          ChildUntracedTests = 0
          Seeds = [ "N.f" ]
          FallbackSeeds = Map.empty
          SelectMs = 12L
          TraceRecall = "n/a: impact run"
          StaticRecall = "n/a: impact run" }

    [<Fact>]
    let ``a shadow row and its misses round-trip`` () =
        withStore (fun _ store ->
            let at = DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero)

            let miss =
                { RunId = "r1"; TestProject = "P"; TestKey = "P|K|b"; Kind = VerifiedFailure
                  Cause = Unexplained; Detail = "failed while its trace verified" }

            store.RecordShadow(shadowRow "r1" at, [ miss ])
            test <@ store.ShadowRows("P", at.AddDays -1.0) = [ shadowRow "r1" at ] @>
            test <@ store.Misses("P", at.AddDays -1.0) = [ miss, at ] @>
            test <@ store.ShadowRows("P", at.AddDays 1.0) |> List.isEmpty @>)

    [<Fact>]
    let ``file gaps are replaced per project`` () =
        withStore (fun _ store ->
            store.RecordFileGaps("P", [ "P|K|a"; "P|K|b" ])
            store.RecordFileGaps("P", [ "P|K|b" ])
            test <@ store.FileGaps "P" = set [ "P|K|b" ] @>)
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class "TestPrune.Trace.Tests.TraceStoreTests+Phase2"`
Expected: build errors naming `VerificationFacts`, `RecordSymbolFacts`, `ShadowSelectionRow`.

- [ ] **Step 4: Implement migration 2**

Append a second entry to `migrations` (never edit entry 1):

```fsharp
      2,
      """
      ALTER TABLE trace_runs ADD COLUMN fingerprint_inputs TEXT NOT NULL DEFAULT '{}';
      CREATE TABLE trace_symbol_facts (
          test_project TEXT NOT NULL,
          env_fingerprint TEXT NOT NULL,
          fact TEXT NOT NULL CHECK (fact IN ('probed', 'static-init', 'unstable')),
          symbol_full_name TEXT NOT NULL,
          evidence TEXT NOT NULL DEFAULT '',
          PRIMARY KEY (test_project, env_fingerprint, fact, symbol_full_name)) WITHOUT ROWID;
      CREATE TABLE trace_file_gaps (
          test_project TEXT NOT NULL,
          test_key TEXT NOT NULL,
          PRIMARY KEY (test_project, test_key)) WITHOUT ROWID;
      CREATE TABLE shadow_selections (
          id INTEGER PRIMARY KEY,
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          row_json TEXT NOT NULL,
          UNIQUE (run_id, test_project));
      CREATE INDEX shadow_selections_by_project ON shadow_selections (test_project, recorded_at);
      CREATE TABLE shadow_misses (
          run_id TEXT NOT NULL,
          test_project TEXT NOT NULL,
          test_key TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('verified-failure', 'mutation')),
          cause TEXT NOT NULL CHECK (cause IN ('flaky', 'unexplained')),
          detail TEXT NOT NULL DEFAULT '',
          recorded_at TEXT NOT NULL,
          PRIMARY KEY (run_id, test_project, test_key, kind)) WITHOUT ROWID;
      CREATE TABLE mutation_samples (
          id INTEGER PRIMARY KEY,
          test_project TEXT NOT NULL,
          recorded_at TEXT NOT NULL,
          sample_json TEXT NOT NULL);
      CREATE INDEX mutation_samples_by_project ON mutation_samples (test_project, recorded_at);
      """
```

A shadow row is stored as one JSON document (`row_json`) plus the columns the report filters on. The row's
shape will grow while shadow runs; a JSON column lets it grow without a migration per field, and the report
reads whole rows anyway.

- [ ] **Step 5: Implement the types and members**

Add the types from "Cross-task contracts → Trace store" above `Store`. Code tables for the text codes:

```fsharp
let private factCode =
    function
    | Probed -> "probed"
    | StaticInit -> "static-init"
    | Unstable -> "unstable"

let private missKindCode =
    function
    | VerifiedFailure -> "verified-failure"
    | MutationMiss -> "mutation"

let private missKindOf =
    function
    | "mutation" -> MutationMiss
    | _ -> VerifiedFailure

let private causeCode =
    function
    | Flaky -> "flaky"
    | Unexplained -> "unexplained"

let private causeOf =
    function
    | "flaky" -> Flaky
    | _ -> Unexplained
```

`ShadowSelectionRow` and `MutationSample` hold F# unions, which `System.Text.Json` does not serialise without a
converter, and the repository takes no converter package. Serialise them through DTO records whose union fields
are the text codes (`mode`: `impact`/`full`, `policy`: `shadow`/`live`, `fingerprintOutcome`, `outcome`), with
four small functions `rowToJson`, `rowOfJson`, `sampleToJson`, `sampleOfJson`, each a field-by-field copy plus
the code tables. Keep the DTOs public: System.Text.Json serialises a non-public F# record as `{}`, as `RunStats`
already notes.

Members on `Store` (each a single statement or one transaction; `json_each` for every key list, ADR 0003):

```fsharp
    member _.VerificationFacts(testProject: string, envFingerprint: string) : VerificationFacts =
        let ps = [ "@p", box testProject; "@e", box envFingerprint ]

        let tests =
            readRows conn "SELECT test_key, complete, incomplete_reasons FROM trace_tests WHERE test_project = @p AND env_fingerprint = @e" ps (fun r ->
                r.GetString 0, (r.GetInt64 1 = 1L, JsonSerializer.Deserialize<string[]>(r.GetString 2) |> List.ofArray))
            |> Map.ofList

        let versions =
            readRows conn """SELECT DISTINCT v.id, v.symbol_full_name, v.content_hash FROM trace_tests t
                   JOIN trace_test_scopes ts ON ts.test_id = t.id
                   JOIN trace_entries e ON e.scope_id = ts.scope_id
                   JOIN symbol_versions v ON v.id = e.symbol_version_id
                   WHERE t.test_project = @p AND t.env_fingerprint = @e""" ps (fun r -> r.GetInt64 0, r.GetString 1, r.GetString 2)

        let inputs =
            readRows conn """SELECT DISTINCT i.kind, i.key, i.hash FROM trace_tests t
                   JOIN trace_test_scopes ts ON ts.test_id = t.id
                   JOIN trace_inputs i ON i.scope_id = ts.scope_id
                   WHERE t.test_project = @p AND t.env_fingerprint = @e ORDER BY i.kind, i.key""" ps (fun r -> r.GetString 0, r.GetString 1, r.GetString 2)

        let others =
            readRows conn "SELECT DISTINCT test_key FROM trace_tests WHERE test_project = @p AND env_fingerprint <> @e" ps (fun r -> r.GetString 0)
            |> Set.ofList
            |> fun s -> Set.difference s (tests |> Map.keys |> Set.ofSeq)

        let facts =
            readRows conn "SELECT fact, symbol_full_name FROM trace_symbol_facts WHERE test_project = @p AND env_fingerprint = @e" ps (fun r -> r.GetString 0, r.GetString 1)

        let factSet code =
            facts |> List.choose (fun (f, n) -> if f = code then Some n else None) |> Set.ofList

        { Tests = tests
          Versions = versions
          Inputs = inputs
          OtherFingerprintKeys = others
          Probed = factSet "probed"
          StaticInit = factSet "static-init"
          Unstable = factSet "unstable" }

    member _.TestsWithVersions(testProject: string, envFingerprint: string, versionIds: int64 list) : (string * string) list =
        if List.isEmpty versionIds then
            []
        else
            readRows conn """SELECT DISTINCT t.test_key, v.symbol_full_name FROM trace_tests t
                   JOIN trace_test_scopes ts ON ts.test_id = t.id
                   JOIN trace_entries e ON e.scope_id = ts.scope_id
                   JOIN symbol_versions v ON v.id = e.symbol_version_id
                   WHERE t.test_project = @p AND t.env_fingerprint = @e
                     AND e.symbol_version_id IN (SELECT value FROM json_each(@ids))
                   ORDER BY t.test_key, v.symbol_full_name"""
                [ "@p", box testProject; "@e", box envFingerprint; "@ids", box (JsonSerializer.Serialize(Array.ofList versionIds)) ]
                (fun r -> r.GetString 0, r.GetString 1)

    member _.TestsWithInputs(testProject: string, envFingerprint: string, inputs: (string * string) list) : (string * string * string) list =
        if List.isEmpty inputs then
            []
        else
            let pairs = inputs |> List.map (fun (k, key) -> [| k; key |]) |> Array.ofList

            readRows conn """SELECT DISTINCT t.test_key, i.kind, i.key FROM trace_tests t
                   JOIN trace_test_scopes ts ON ts.test_id = t.id
                   JOIN trace_inputs i ON i.scope_id = ts.scope_id
                   WHERE t.test_project = @p AND t.env_fingerprint = @e
                     AND EXISTS (SELECT 1 FROM json_each(@inputs) j
                                 WHERE json_extract(j.value, '$[0]') = i.kind AND json_extract(j.value, '$[1]') = i.key)
                   ORDER BY t.test_key, i.kind, i.key"""
                [ "@p", box testProject; "@e", box envFingerprint; "@inputs", box (JsonSerializer.Serialize pairs) ]
                (fun r -> r.GetString 0, r.GetString 1, r.GetString 2)

    member _.RecordSymbolFacts(testProject: string, envFingerprint: string, fact: SymbolFact, symbols: (string * string) list) : unit =
        inTransaction conn (fun tx ->
            let ps = [ "@p", box testProject; "@e", box envFingerprint; "@f", box (factCode fact) ]

            if fact = Probed then
                exec conn tx "DELETE FROM trace_symbol_facts WHERE test_project = @p AND env_fingerprint = @e AND fact = @f" ps

            for name, evidence in List.distinctBy fst symbols do
                exec conn tx """INSERT OR IGNORE INTO trace_symbol_facts (test_project, env_fingerprint, fact, symbol_full_name, evidence)
                       VALUES (@p, @e, @f, @n, @ev)""" (ps @ [ "@n", box name; "@ev", box evidence ]))
```

`RunFingerprint`, `LatestFingerprint` (the newest `recorded` run's fingerprint), `HasTraces` (any `trace_tests`
row), `RecordShadow` (upsert the row by `(run_id, test_project)`, insert-or-replace each miss with the row's
`recorded_at`, one transaction), `RecordMutation` (insert the sample; its misses use run id
`mutation:<new id>`), `ShadowRows`, `Misses`, `MutationSamples` (filter `recorded_at >= @since` on the ISO-8601
text, which sorts chronologically for one offset: always write `ToUniversalTime().ToString "O"`),
`RecordFileGaps` (delete the project's rows, insert the new set, one transaction) and `FileGaps` follow the same
pattern.

Extend `CollectGarbage` with one statement at the end: facts of a fingerprint the project no longer has traces
under are dropped.

```fsharp
            exec conn tx """DELETE FROM trace_symbol_facts WHERE test_project = @p
                   AND env_fingerprint NOT IN (SELECT DISTINCT env_fingerprint FROM trace_tests WHERE test_project = @p)"""
                [ "@p", box testProject ]
```

Shadow rows, misses and samples are never garbage-collected by ingestion: they are the measurement record. The
report verb reads a window; `shadow-report --prune-before <date>` (Task 8) is the only delete.

- [ ] **Step 6: Run the new and the touched classes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceStoreTests`
Expected: PASS, including every phase-1 test in the file (the migration test that pins version 1 now reads
`TraceSchemaVersion = 2`; update it and keep its "a newer file is refused" case).

- [ ] **Step 7: Gate and commit**

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t2-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 3: Predicting the fingerprint (`RuntimeIdentity`, `RuntimeResolution`, `Fingerprint.predict`)

**Files:**

- Create: `src/TestPrune.Trace.Recorder/RuntimeIdentity.fs` (compile before `DumpWriter.fs`)
- Modify: `src/TestPrune.Trace.Recorder/DumpWriter.fs:39-41` (use `RuntimeIdentity`)
- Create: `src/TestPrune.Trace/RuntimeResolution.fs` (compile after `Fingerprint.fs`)
- Modify: `src/TestPrune.Trace/Fingerprint.fs` (`toJson`, `diff`, `predict`)
- Test: `tests/TestPrune.Trace.Tests/RuntimeResolutionTests.fs` (new), `FingerprintTests.fs` (append)

**Interfaces:**

- Produces:

```fsharp
module TestPrune.Trace.RuntimeResolution
/// (framework name, requested version, rollForward) of Microsoft.NETCore.App in a runtimeconfig.json.
val requestedFramework: runtimeConfigJson: string -> (string * string) option     // version, rollForward ("Minor" when absent)
val installed: dotnetRoot: string -> string list          // directory names under shared/Microsoft.NETCore.App
val resolve: requested: string -> rollForward: string -> installed: string list -> string option
val describe: version: string -> string                   // ".NET " + version
val predictRuntime: dotnetRoot: string -> runtimeConfigPath: string -> rollForwardEnv: string option -> string option

module TestPrune.Trace.Fingerprint
val toJson: Inputs -> string                 // the canonical JSON `compute` hashes
val diff: Inputs -> Inputs -> string list    // names of differing components, sorted
val predict: repoRoot: string -> files: string list -> env: string list -> depsJsonSha256: string -> runtime: string -> Inputs
```

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "Predict a traced run's environment fingerprint before it starts"
```

- [ ] **Step 2: Write the failing tests**

`tests/TestPrune.Trace.Tests/RuntimeResolutionTests.fs`:

```fsharp
module TestPrune.Trace.Tests.RuntimeResolutionTests

open Xunit
open Swensen.Unquote
open TestPrune.Trace.RuntimeResolution

let installedSet = [ "8.0.8"; "8.0.11"; "9.0.1"; "10.0.0"; "10.0.2"; "11.0.0-preview.1.25" ]

[<Theory>]
[<InlineData("10.0.0", "Minor", "10.0.2")>]         // same major.minor: its latest patch
[<InlineData("8.0.0", "Minor", "8.0.11")>]
[<InlineData("8.1.0", "Minor", null)>]              // no 8.x >= 8.1 installed
[<InlineData("9.0.0", "LatestPatch", "9.0.1")>]
[<InlineData("9.2.0", "LatestPatch", null)>]
[<InlineData("8.0.0", "Major", "8.0.11")>]
[<InlineData("7.0.0", "Major", "8.0.11")>]          // lowest higher major, its latest patch
[<InlineData("8.0.0", "LatestMinor", "8.0.11")>]
[<InlineData("8.0.0", "LatestMajor", "10.0.2")>]    // releases only while one satisfies
[<InlineData("10.0.0", "Disable", "10.0.0")>]
[<InlineData("10.0.1", "Disable", null)>]
let ``roll-forward picks the runtime the host would`` (requested: string, rollForward: string, expected: string) =
    test <@ resolve requested rollForward installedSet = Option.ofObj expected @>

[<Fact>]
let ``a prerelease is chosen only when the request is a prerelease`` () =
    test <@ resolve "11.0.0-preview.1.25" "Minor" installedSet = Some "11.0.0-preview.1.25" @>
    test <@ resolve "11.0.0" "Minor" installedSet = None @>

[<Fact>]
let ``runtimeconfig framework and rollForward are read`` () =
    let json =
        """{"runtimeOptions":{"tfm":"net10.0","rollForward":"Major","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"""

    test <@ requestedFramework json = Some("10.0.0", "Major") @>

[<Fact>]
let ``an app with several frameworks is keyed on Microsoft.NETCore.App`` () =
    let json =
        """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.AspNetCore.App","version":"10.0.0"},{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}"""

    test <@ requestedFramework json = Some("10.0.0", "Minor") @>

[<Fact>]
let ``the description matches the recorder's for this process`` () =
    let running = System.Environment.Version.ToString 3
    test <@ describe running = TestPrune.Trace.Recorder.RuntimeIdentity.Runtime() @>
```

Append to `FingerprintTests.fs`:

```fsharp
[<Fact>]
let ``predict equals gather for the same environment`` () =
    let root = System.IO.Directory.CreateTempSubdirectory().FullName
    System.IO.File.WriteAllText(System.IO.Path.Combine(root, "global.json"), "{}")

    let dump =
        { Pid = 1; ParentScope = None
          Runtime = TestPrune.Trace.Recorder.RuntimeIdentity.Runtime()
          Os = TestPrune.Trace.Recorder.RuntimeIdentity.Os()
          Arch = TestPrune.Trace.Recorder.RuntimeIdentity.Arch()
          IdCount = 0; CpuMs = 0L
          Counters = { Test = 0L; Class = 0L; Collection = 0L; Assembly = 0L; Override = 0L; StaticInit = 0L; Ambient = 0L; Overflow = 0L }
          Scopes = [] }

    let gathered = Fingerprint.gather root [ "global.json" ] [ "HOME" ] dump "deps"
    let predicted = Fingerprint.predict root [ "global.json" ] [ "HOME" ] "deps" dump.Runtime
    test <@ predicted = gathered @>
    test <@ Fingerprint.diff predicted gathered |> List.isEmpty @>

[<Fact>]
let ``diff names the components that differ`` () =
    let a = Fingerprint.predict "/nowhere" [] [] "d1" ".NET 10.0.0"
    let b = { a with Runtime = ".NET 10.0.2"; DepsJsonSha256 = "d2" }
    test <@ Fingerprint.diff a b = [ "deps"; "runtime" ] @>
```

- [ ] **Step 3: Run and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.RuntimeResolutionTests`
Expected: build error: `RuntimeResolution` is not defined.

- [ ] **Step 4: Implement `RuntimeIdentity`** (recorder, net8.0, no new dependency). Move `osNameOf` out of
`DumpWriter.fs` into this file unchanged, and make `DumpWriter` write `RuntimeIdentity.Runtime()`,
`RuntimeIdentity.Os()` and `RuntimeIdentity.Arch()`. The dump bytes must not change; the dump round-trip tests
pin them.

```fsharp
namespace TestPrune.Trace.Recorder

open System.Runtime.InteropServices

/// The environment strings a dump records. The dump writer and the host's fingerprint
/// prediction both call these, so a prediction compares like with like.
[<AbstractClass; Sealed>]
type RuntimeIdentity =
    // `osNameOf`, moved here verbatim from DumpWriter.fs, as a private static member.

    /// `RuntimeInformation.FrameworkDescription`, e.g. ".NET 10.0.0".
    static member Runtime() : string = RuntimeInformation.FrameworkDescription

    /// The OS name, as the dump's "os" field has always recorded it.
    static member Os() : string = RuntimeIdentity.osNameOf RuntimeInformation.IsOSPlatform

    /// The process architecture, e.g. "Arm64".
    static member Arch() : string = string RuntimeInformation.ProcessArchitecture
```

- [ ] **Step 5: Implement `RuntimeResolution`**

```fsharp
/// Which Microsoft.NETCore.App an apphost will load, computed the way the .NET host
/// resolves it (runtimeconfig `rollForward`, or `DOTNET_ROLL_FORWARD`, over the runtimes
/// installed under DOTNET_ROOT). Used only to predict a fingerprint; every prediction is
/// checked against the runtime the run then reports.
module TestPrune.Trace.RuntimeResolution

open System
open System.IO
open System.Text.Json

type private V =
    { Major: int; Minor: int; Patch: int; Pre: string; Text: string }

let private parse (text: string) : V option =
    let core, pre =
        match text.IndexOf '-' with
        | -1 -> text, ""
        | i -> text.Substring(0, i), text.Substring(i + 1)

    match core.Split '.' |> Array.map Int32.TryParse with
    | [| (true, a); (true, b); (true, c) |] -> Some { Major = a; Minor = b; Patch = c; Pre = pre; Text = text }
    | _ -> None

let private key (v: V) = v.Major, v.Minor, v.Patch

let requestedFramework (runtimeConfigJson: string) : (string * string) option =
    use doc = JsonDocument.Parse runtimeConfigJson
    let opts = doc.RootElement.GetProperty "runtimeOptions"

    let rollForward =
        match opts.TryGetProperty "rollForward" with
        | true, v -> v.GetString()
        | _ -> "Minor"

    let frameworks =
        [ match opts.TryGetProperty "framework" with
          | true, f -> f
          | _ -> ()
          match opts.TryGetProperty "frameworks" with
          | true, fs -> yield! fs.EnumerateArray()
          | _ -> () ]

    frameworks
    |> List.tryFind (fun f -> f.GetProperty("name").GetString() = "Microsoft.NETCore.App")
    |> Option.map (fun f -> f.GetProperty("version").GetString(), rollForward)

let installed (dotnetRoot: string) : string list =
    let dir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App")

    if Directory.Exists dir then
        Directory.GetDirectories dir |> Array.map Path.GetFileName |> Array.sort |> List.ofArray
    else
        []

let resolve (requested: string) (rollForward: string) (installedVersions: string list) : string option =
    match parse requested with
    | None -> None
    | Some req ->
        let candidates =
            installedVersions
            |> List.choose parse
            // A prerelease runtime satisfies only a prerelease request, as the host does.
            |> List.filter (fun v -> v.Pre = "" || req.Pre <> "")
            |> List.filter (fun v -> key v >= key req)

        let latestPatchOf (vs: V list) (major, minor) =
            vs
            |> List.filter (fun v -> v.Major = major && v.Minor = minor)
            |> List.sortByDescending key
            |> List.tryHead

        let lowestMinorThenLatestPatch (vs: V list) =
            vs
            |> List.sortBy key
            |> List.tryHead
            |> Option.bind (fun v -> latestPatchOf vs (v.Major, v.Minor))

        let sameMajor = candidates |> List.filter (fun v -> v.Major = req.Major)

        let chosen =
            match rollForward.ToLowerInvariant() with
            | "disable" -> candidates |> List.tryFind (fun v -> v.Text = req.Text)
            | "latestpatch" -> latestPatchOf candidates (req.Major, req.Minor)
            | "minor" -> lowestMinorThenLatestPatch sameMajor
            | "major" ->
                lowestMinorThenLatestPatch sameMajor
                |> Option.orElseWith (fun () -> lowestMinorThenLatestPatch candidates)
            | "latestminor" -> sameMajor |> List.sortByDescending key |> List.tryHead
            | "latestmajor" -> candidates |> List.sortByDescending key |> List.tryHead
            | _ -> None

        chosen |> Option.map (fun v -> v.Text)

let describe (version: string) : string = ".NET " + version

let predictRuntime (dotnetRoot: string) (runtimeConfigPath: string) (rollForwardEnv: string option) : string option =
    if not (File.Exists runtimeConfigPath) then
        None
    else
        requestedFramework (File.ReadAllText runtimeConfigPath)
        |> Option.bind (fun (version, configured) ->
            let policy = rollForwardEnv |> Option.defaultValue configured
            resolve version policy (installed dotnetRoot))
        |> Option.map describe
```

The "Minor" branch follows the host: the requested major.minor if installed, else the lowest higher minor, each
at its latest patch. Check the table in the test against the .NET docs page "Framework-dependent apps roll
forward" before relying on it, and keep the test as the pin.

- [ ] **Step 6: Implement `toJson`, `diff`, `predict`** in `Fingerprint.fs`:

```fsharp
/// The canonical JSON `compute` hashes; stored with every recorded run so a prediction
/// miss can say which component differed.
let toJson (i: Inputs) : string =
    let pairs (xs: (string * string) list) =
        xs |> List.sort |> List.map (fun (k, v) -> k + "=" + v) |> List.toArray

    JsonSerializer.Serialize(
        { arch = i.Arch
          deps = i.DepsJsonSha256
          env = pairs i.ConfigEnv
          files = pairs i.ConfigFiles
          hashScheme = i.HashScheme
          os = i.Os
          recorder = i.RecorderVersion
          runtime = i.Runtime
          weaver = i.WeaverVersion }
        : CanonicalFingerprint
    )

let compute (i: Inputs) : string =
    toJson i |> Encoding.UTF8.GetBytes |> sha256Hex

/// The canonical field names whose values differ, sorted.
let diff (a: Inputs) (b: Inputs) : string list =
    let fields (i: Inputs) =
        use doc = JsonDocument.Parse(toJson i)
        doc.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value.GetRawText()) |> Map.ofSeq

    let fa, fb = fields a, fields b
    fa |> Map.toList |> List.filter (fun (k, v) -> fb.[k] <> v) |> List.map fst |> List.sort

/// The fingerprint a traced launch will record, computed before it starts: `gather` with
/// the runtime predicted by `RuntimeResolution` and this host's OS and architecture.
let predict (repoRoot: string) (files: string list) (env: string list) (depsJsonSha256: string) (runtime: string) : Inputs =
    let dump: ProcessDump =
        { Pid = 0
          ParentScope = None
          Runtime = runtime
          Os = TestPrune.Trace.Recorder.RuntimeIdentity.Os()
          Arch = TestPrune.Trace.Recorder.RuntimeIdentity.Arch()
          IdCount = 0
          CpuMs = 0L
          Counters =
            { Test = 0L; Class = 0L; Collection = 0L; Assembly = 0L
              Override = 0L; StaticInit = 0L; Ambient = 0L; Overflow = 0L }
          Scopes = [] }

    gather repoRoot files env dump depsJsonSha256
```

`compute` keeps its output byte-for-byte (the existing fingerprint tests pin it).

- [ ] **Step 7: Run the new and touched classes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.RuntimeResolutionTests --filter-class TestPrune.Trace.Tests.FingerprintTests --filter-class TestPrune.Trace.Tests.DumpRoundTripTests`
Expected: PASS.

- [ ] **Step 8: Gate and commit**

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t3-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 4: Ingestion records selection facts and detects order-unstable symbols

**Files:**

- Modify: `src/TestPrune.Trace/TraceIngest.fs`
- Modify: `src/TestPrune.Trace/TraceStore.fs` (`TraceRun.FingerprintInputs: string`, written into
  `trace_runs.fingerprint_inputs`)
- Test: `tests/TestPrune.Trace.Tests/TraceIngestTests.fs` (append)

**Interfaces:**

- Consumes: Task 2's `RecordSymbolFacts`, `TryRead`.
- Produces:

```fsharp
// TraceIngest
val inputHash: root: string -> kind: string -> key: string -> string   // "read" | "exists" | "list" | "file-level"
val detectUnstable:
    currentVersions: Map<string, string> ->
    old: TraceStore.StoredTrace ->
    newSymbols: Set<string * string> ->
    newInputs: Set<string * string * string> ->
    newComplete: bool ->
        Set<string>
// IngestSummary gains: UnstableSymbols: int
```

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "Ingestion: record probed and static-init symbols, detect order-unstable ones"
```

- [ ] **Step 2: Write the failing tests** (append to `TraceIngestTests.fs`; the file's `World` type builds a
  repository, an index with `N.M.f` and `N.M.g`, a three-row manifest and dumps from a real `RecorderState`;
  reuse it, and add what is missing to it rather than a second world):

```fsharp
[<Fact>]
let ``ingestion records the probed symbols of the weave`` () =
    use w = new World()
    let summary = w.IngestPassing [ "T:a", [ 0 ] ]
    let facts = w.Store.VerificationFacts("P", summary.EnvFingerprint.Value)
    test <@ facts.Probed = set [ "N.M.f"; "N.M.g" ] @>

[<Fact>]
let ``symbols executed in a static constructor are recorded as static-init facts`` () =
    use w = new World()
    let summary = w.IngestPassingWithStaticInit [ "T:a", [ 0 ] ] [ 1 ]
    let facts = w.Store.VerificationFacts("P", summary.EnvFingerprint.Value)
    test <@ facts.StaticInit = set [ "N.M.g" ] @>

[<Fact>]
let ``a trace that changes with nothing changed flags the differing symbols`` () =
    use w = new World()
    w.IngestPassing [ "T:a", [ 0 ] ] |> ignore
    let second = w.IngestPassing [ "T:a", [ 0; 1 ] ]
    let facts = w.Store.VerificationFacts("P", second.EnvFingerprint.Value)
    test <@ facts.Unstable = set [ "N.M.g" ] @>
    test <@ second.UnstableSymbols = 1 @>

[<Fact>]
let ``a trace that changes because its code changed flags nothing`` () =
    use w = new World()
    w.IngestPassing [ "T:a", [ 0 ] ] |> ignore
    w.EditSymbol "N.M.f" "hf2"
    let second = w.IngestPassing [ "T:a", [ 0; 1 ] ]
    test <@ second.UnstableSymbols = 0 @>

[<Fact>]
let ``detectUnstable is pure over the two traces`` () =
    let old: TraceStore.StoredTrace =
        { TestKey = "P|K|a"; EnvFingerprint = "E"; RunId = "r"; Complete = true; Reasons = []
          Symbols = set [ "a", "1"; "b", "1" ]; Inputs = Set.empty }

    let current = Map.ofList [ "a", "1"; "b", "1"; "c", "1" ]
    test <@ detectUnstable current old (set [ "a", "1"; "c", "1" ]) Set.empty true = set [ "b"; "c" ] @>
    // an incomplete side proves nothing
    test <@ detectUnstable current old (set [ "a", "1" ]) Set.empty false |> Set.isEmpty @>
    // a changed input explains the difference
    test <@ detectUnstable current old (set [ "a", "1" ]) (set [ "read", "x", "h" ]) true |> Set.isEmpty @>
    // a changed version explains the difference
    test <@ detectUnstable (current.Add("b", "2")) old (set [ "a", "1" ]) Set.empty true |> Set.isEmpty @>

[<Fact>]
let ``inputHash is the hash ingestion stores`` () =
    use w = new World()
    w.WriteFile "data/x.json" "{}"
    test <@ inputHash w.Root "read" "data/x.json" = Fingerprint.hashFile w.Root "data/x.json" @>
    test <@ inputHash w.Root "exists" "data/missing" = "absent" @>
    test <@ inputHash w.Root "list" "data" <> "absent" @>
    test <@ inputHash w.Root "file-level" "src/M.fs" = Fingerprint.hashFile w.Root "src/M.fs" @>
```

`World.IngestPassing scopes` writes one dump whose test scopes hit the given ids and a CTRF outcome `Passed` for
each, then calls `ingest`. `IngestPassingWithStaticInit` also sets the static-init bitset. `EditSymbol`
rewrites the index row's content hash. Add these members to `World` in this step.

- [ ] **Step 3: Run and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceIngestTests`
Expected: build errors naming `inputHash`, `detectUnstable`, `UnstableSymbols`.

- [ ] **Step 4: Implement**

1. Turn the private `inputOf` into `inputHash` plus a thin `inputOf`:

```fsharp
/// The hash an input of `kind` at repo-relative `key` has now, as ingestion stores it and
/// as selection re-checks it.
let inputHash (root: string) (kind: string) (key: string) : string =
    let p = Path.Combine(root, key)

    match kind with
    | "exists" -> if Path.Exists p then "present" else "absent"
    | "list" ->
        if Directory.Exists p then
            Directory.GetFileSystemEntries p |> Seq.map Path.GetFileName |> Seq.sort |> String.concat "\n" |> sha256Text
        else
            "absent"
    | _ -> Fingerprint.hashFile root key   // "read" and "file-level"

let private kindOf =
    function
    | FileRead -> "read"
    | ExistenceProbe -> "exists"
    | DirectoryListing -> "list"

let private inputOf (root: string) (i: RecordedInput) =
    repoRelative root i.Path
    |> Option.map (fun rel -> kindOf i.Kind, rel, inputHash root (kindOf i.Kind) rel)
```

2. `detectUnstable`:

```fsharp
/// Symbols that appeared in or vanished from a test's trace although nothing the test
/// executed or read changed: the old trace's every symbol still has the version it was
/// recorded at, the inputs are identical, and both traces are complete. A deterministic
/// test executes the same code then, so a difference is order or memoisation (a factory
/// only the first test to force it records).
let detectUnstable
    (currentVersions: Map<string, string>)
    (old: StoredTrace)
    (newSymbols: Set<string * string>)
    (newInputs: Set<string * string * string>)
    (newComplete: bool)
    : Set<string> =
    let unchanged =
        old.Symbols |> Set.forall (fun (n, h) -> currentVersions.TryFind n = Some h)

    if not (old.Complete && newComplete && unchanged && old.Inputs = newInputs) then
        Set.empty
    else
        let names = Set.map fst
        let o, n = names old.Symbols, names newSymbols
        Set.union (Set.difference o n) (Set.difference n o)
```

3. In `ingest`, after `tests` is built and before `store.RecordRun`, compute each test's new symbol and input sets
   from `contents` and its `ScopeKeys`, read the old trace with `store.TryRead(key, fp)`, and collect
   `(symbol, testKey)` pairs from `detectUnstable (versionHashes req.Symbols) …`. After `CollectGarbage`:

```fsharp
        store.RecordSymbolFacts(req.TestProject, fp, Probed, probedSymbols |> List.map (fun n -> n, ""))
        store.RecordSymbolFacts(req.TestProject, fp, StaticInit, staticInitSymbols |> List.map (fun n -> n, ""))
        store.RecordSymbolFacts(req.TestProject, fp, Unstable, unstable)
```

   where `probedSymbols` is every `ToSymbol n` target of `joinManifest` (compute it where `effectsOf` already
   joins, and return it alongside) and `staticInitSymbols` is the symbol list of the merged
   `StaticInitScope` content, when present. `versionHashes` is computed once per ingestion and passed down; it is
   the expensive call.

4. `TraceRun` gains `FingerprintInputs: string`; `ingest` passes `Fingerprint.toJson inputs` and `UpsertRun`
   writes it. `RecordRunWithoutTraces` callers pass `"{}"`. Add `FingerprintInputs = "{}"` to every `TraceRun`
   literal in the existing tests (Task 2's `Phase2` module included) in this same commit.

- [ ] **Step 5: Run the new and touched classes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceIngestTests --filter-class TestPrune.Trace.Tests.TraceStoreTests --filter-class TestPrune.Trace.Tests.EndToEndTests`
Expected: PASS.

- [ ] **Step 6: Gate and commit**

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t4-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 5: `TraceSelect`: the rules R1–R7 and eligibility

**Files:**

- Create: `src/TestPrune.Trace/TraceSelect.fs` (compile after `TraceSession.fs`)
- Test: `tests/TestPrune.Trace.Tests/TraceSelectTests.fs` (new)

**Interfaces:**

- Consumes: Task 2's store members; Task 4's `inputHash`; `TraceIngest.versionHashes`, `TraceIngest.testKey`;
  Task 1's `InlineAttributeName`; `ImpactAnalysis.selectTests` (Core).
- Produces: "Cross-task contracts → Selection".

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "TraceSelect: select tests from recorded traces (R1-R7)"
```

- [ ] **Step 2: Write the failing tests**

`tests/TestPrune.Trace.Tests/TraceSelectTests.fs`:

```fsharp
module TestPrune.Trace.Tests.TraceSelectTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.TraceStore
open TestPrune.Trace.TraceSelect

let private tm cls meth : TestMethodInfo =
    { SymbolFullName = $"T.%s{cls}.%s{meth}"; TestProject = "P"; TestClass = cls; TestMethod = meth }

let private a, b, c, n = tm "K" "a", tm "K" "b", tm "K+Inner" "c", tm "K" "n"

/// A store where: a executes N.f and reads a.json through fixture C:K; b executes N.g;
/// c (a nested class) executes N.h and is incomplete; n (new) has no trace; the project's
/// latest weave probed N.f, N.g and N.h; N.s ran in a static constructor.
let private world () =
    let path = IO.Path.Combine(IO.Directory.CreateTempSubdirectory().FullName, "t.db")
    let store = Store.Open path

    let run: TraceRun =
        { RunId = "r1"; TestProject = "P"; TreeHash = "t"; EnvFingerprint = "E"
          RecordedAt = DateTimeOffset.UnixEpoch; Kind = RunKind.FullRun; Status = Recorded
          Reason = ""; StatsJson = "{}"; FingerprintInputs = "{}" }

    store.RecordRun(
        run,
        [ { Key = "T:a"; Symbols = [ "N.f", "hf" ]; Inputs = [] }
          { Key = "C:K"; Symbols = []; Inputs = [ "read", "a.json", "ha" ] }
          { Key = "T:b"; Symbols = [ "N.g", "hg" ]; Inputs = [] }
          { Key = "T:c"; Symbols = [ "N.h", "hh" ]; Inputs = [] } ],
        [ { TestKey = "P|K|a"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:a"; "C:K" ] }
          { TestKey = "P|K|b"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:b" ] }
          { TestKey = "P|K+Inner|c"; Status = Some Passed; Reasons = [ SourceDrift "src/H.fs" ]; ScopeKeys = [ "T:c" ] } ]
    )

    store.RecordSymbolFacts("P", "E", Probed, [ "N.f", ""; "N.g", ""; "N.h", "" ])
    store.RecordSymbolFacts("P", "E", StaticInit, [ "N.s", "" ])
    store

let private request (overrides: Request -> Request) : Request =
    overrides
        { TestProject = "P"
          EnvFingerprint = "E"
          Tests = [ a; b; c; n ]
          CurrentVersions = Map.ofList [ "N.f", "hf"; "N.g", "hg"; "N.h", "hh"; "N.s", "hs" ]
          CurrentInput = fun kind key -> if (kind, key) = ("read", "a.json") then "ha" else "absent"
          Seeds = []
          ChangedFiles = []
          IsIndexed = fun f -> f.EndsWith ".fs"
          AttributesOf = fun _ -> []
          StaticWalk = fun _ -> []
          FileFallback = fun _ -> Some []
          FileGaps = Set.empty }

let private keysOf (s: Selection) = s.Selected |> Map.keys |> Set.ofSeq

[<Fact>]
let ``nothing changed selects only the untraced and the incomplete`` () =
    use store = world ()
    let s = select store (request id)
    test <@ s.Selected.["P|K|n"] = [ NoTrace ] @>
    test <@ s.Selected.["P|K.Inner|c"] = [ Incomplete [ "source-drift:src/H.fs" ] ] @>
    test <@ keysOf s = set [ "P|K|n"; "P|K.Inner|c" ] @>

[<Fact>]
let ``a changed version selects exactly the tests that executed it (R2)`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with CurrentVersions = r.CurrentVersions.Add("N.g", "hg2") }))
    test <@ s.Selected.["P|K|b"] = [ SymbolChanged "N.g" ] @>
    test <@ not (s.Selected.ContainsKey "P|K|a") @>

[<Fact>]
let ``a removed symbol selects the tests that executed it (R2)`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with CurrentVersions = r.CurrentVersions.Remove "N.f" }))
    test <@ s.Selected.["P|K|a"] = [ SymbolChanged "N.f" ] @>

[<Fact>]
let ``a changed input read by a fixture selects every test linked to it (R3)`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with CurrentInput = fun _ _ -> "changed" }))
    test <@ s.Selected.["P|K|a"] = [ InputChanged("read", "a.json") ] @>
    test <@ not (s.Selected.ContainsKey "P|K|b") @>

[<Fact>]
let ``a trace under another fingerprint is R5, not R1`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with EnvFingerprint = "E2" }))
    test <@ s.Selected.["P|K|a"] = [ FingerprintMismatch ] @>
    test <@ s.Selected.["P|K|n"] = [ NoTrace ] @>
    test <@ not s.Eligible @>

[<Theory>]
[<InlineData("N.s", "static-init")>]
[<InlineData("N.unprobed", "not-probed")>]
let ``a seed the probes cannot represent falls back to the static walk (R6)`` (seed: string, reason: string) =
    use store = world ()
    let walked = ref []

    let s =
        select store (request (fun r ->
            { r with
                Seeds = [ seed; "N.f" ]
                StaticWalk = fun seeds -> walked.Value <- seeds; [ b ] }))

    test <@ walked.Value = [ seed ] @>
    test <@ s.Selected.["P|K|b"] |> List.map ruleCode = [ "R6" ] @>
    test <@ s.FallbackSeeds |> Map.map (fun _ r -> fallCode r) = Map.ofList [ seed, reason ] @>

[<Fact>]
let ``inline and literal seeds fall back even when probed`` () =
    use store = world ()

    let attrs =
        function
        | "N.f" -> [ TestPrune.AstAnalyzer.InlineAttributeName ]
        | "N.g" -> [ "LiteralAttribute" ]
        | _ -> []

    let s = select store (request (fun r -> { r with Seeds = [ "N.f"; "N.g"; "N.h" ]; AttributesOf = attrs }))
    test <@ s.FallbackSeeds = Map.ofList [ "N.f", Inline; "N.g", Literal ] @>

[<Fact>]
let ``a static walk never selects another project's tests`` () =
    use store = world ()
    let other = { tm "K" "x" with TestProject = "Q" }
    let s = select store (request (fun r -> { r with Seeds = [ "N.s" ]; StaticWalk = fun _ -> [ other ] }))
    test <@ not (s.Selected.ContainsKey "Q|K|x") @>

[<Fact>]
let ``an unclaimed changed file defers to the static file rules and the census gaps (R7)`` () =
    use store = world ()

    let s =
        select store (request (fun r ->
            { r with
                ChangedFiles = [ "config/app.json" ]
                FileFallback = fun files -> if files = [ "config/app.json" ] then Some [ b ] else Some []
                FileGaps = set [ "P|K|a" ] }))

    test <@ s.Selected.["P|K|b"] = [ UntracedInput "config/app.json" ] @>
    test <@ s.Selected.["P|K|a"] = [ UntracedInput "config/app.json" ] @>

[<Fact>]
let ``a claimed or indexed changed file is not R7`` () =
    use store = world ()
    let fallbackCalls = ref 0

    let _ =
        select store (request (fun r ->
            { r with
                ChangedFiles = [ "a.json"; "src/M.fs" ]
                FileFallback = fun _ -> fallbackCalls.Value <- fallbackCalls.Value + 1; Some [] }))

    test <@ fallbackCalls.Value = 0 @>

[<Fact>]
let ``a file added under a listed directory is claimed by the listing`` () =
    use store = world ()

    store.RecordRun(
        { RunId = "r2"; TestProject = "P"; TreeHash = "t"; EnvFingerprint = "E"; RecordedAt = DateTimeOffset.UnixEpoch
          Kind = RunKind.PartialRun; Status = Recorded; Reason = ""; StatsJson = "{}"; FingerprintInputs = "{}" },
        [ { Key = "T:b"; Symbols = [ "N.g", "hg" ]; Inputs = [ "list", "fixtures", "hl" ] } ],
        [ { TestKey = "P|K|b"; Status = Some Passed; Reasons = []; ScopeKeys = [ "T:b" ] } ]
    )

    let fallbackCalls = ref 0

    let _ =
        select store (request (fun r ->
            { r with
                ChangedFiles = [ "fixtures/new.json" ]
                FileFallback = fun _ -> fallbackCalls.Value <- fallbackCalls.Value + 1; Some [] }))

    test <@ fallbackCalls.Value = 0 @>

[<Fact>]
let ``a fallback of None (a project file changed) selects every test`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with ChangedFiles = [ "src/P.fsproj" ]; FileFallback = fun _ -> None }))
    test <@ keysOf s = set [ "P|K|a"; "P|K|b"; "P|K.Inner|c"; "P|K|n" ] @>

[<Fact>]
let ``eligibility is the complete-trace share of the universe`` () =
    use store = world ()
    let s = select store (request (fun r -> { r with Tests = [ a; b ] }))
    test <@ s.CompleteShare = 1.0 && s.Eligible @>
    let s2 = select store (request id)
    test <@ s2.CompleteShare = 0.5 && not s2.Eligible @>

[<Fact>]
let ``staticFileFallback applies DependsOnFile and the project-file rule`` () =
    let db = TestPrune.Database.Database.create (IO.Path.Combine(IO.Directory.CreateTempSubdirectory().FullName, "i.db"))
    let store = TestPrune.Ports.toSymbolStore db
    test <@ staticFileFallback store [ "src/P.fsproj" ] = None @>
    test <@ staticFileFallback store [ "config/app.json" ] = Some [] @>
```

- [ ] **Step 3: Run and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceSelectTests`
Expected: build error: `TraceSelect` is not defined.

- [ ] **Step 4: Implement**

`src/TestPrune.Trace/TraceSelect.fs`:

```fsharp
/// Test selection from recorded traces. A test must run when it has no usable trace under
/// the current environment (R1, R4, R5), when a symbol version or file input its trace
/// names no longer matches the tree (R2, R3), when a changed symbol is one the probes
/// cannot represent and the static walk reaches the test (R6), or when a changed file no
/// trace claims is one the static file rules select it for (R7). Everything is answered
/// from the trace store and the current index; no trace is read per test.
module TestPrune.Trace.TraceSelect

open System.Collections.Generic
open TestPrune.AstAnalyzer
open TestPrune.Trace.TraceStore

type FallReason =
    | NotProbed
    | Inline
    | Literal
    | StaticInitOnly
    | OrderUnstable

type Rule =
    | NoTrace
    | SymbolChanged of symbol: string
    | InputChanged of kind: string * key: string
    | Incomplete of reasons: string list
    | FingerprintMismatch
    | StaticFallback of reason: FallReason
    | UntracedInput of file: string

type Request =
    { TestProject: string
      EnvFingerprint: string
      Tests: TestMethodInfo list
      CurrentVersions: Map<string, string>
      CurrentInput: string -> string -> string
      Seeds: string list
      ChangedFiles: string list
      IsIndexed: string -> bool
      AttributesOf: string -> string list
      StaticWalk: string list -> TestMethodInfo list
      FileFallback: string list -> TestMethodInfo list option
      FileGaps: Set<string> }

type Selection =
    { TestProject: string
      Eligible: bool
      CompleteShare: float
      Universe: Set<string>
      Selected: Map<string, Rule list>
      FallbackSeeds: Map<string, FallReason> }

/// The complete-trace share a project needs before its trace selection may be used.
[<Literal>]
let EligibilityThreshold = 0.99

/// The index and the recorder may spell a nested class `Outer+Inner` or `Outer.Inner`.
let normalizeKey (key: string) = key.Replace('+', '.')

let keyOf (t: TestMethodInfo) =
    normalizeKey (TraceIngest.testKey t.TestProject t.TestClass t.TestMethod)

let ruleCode =
    function
    | NoTrace -> "R1"
    | SymbolChanged _ -> "R2"
    | InputChanged _ -> "R3"
    | Incomplete _ -> "R4"
    | FingerprintMismatch -> "R5"
    | StaticFallback _ -> "R6"
    | UntracedInput _ -> "R7"

let fallCode =
    function
    | NotProbed -> "not-probed"
    | Inline -> "inline"
    | Literal -> "literal"
    | StaticInitOnly -> "static-init"
    | OrderUnstable -> "order-unstable"

/// Why a changed symbol cannot be verified by traces, if it cannot. The order is the
/// order of the evidence: a measured fact about the symbol first, then what the index
/// says, then the absence of a probe.
let fallReason (facts: VerificationFacts) (attributesOf: string -> string list) (symbol: string) : FallReason option =
    let attrs = attributesOf symbol

    if facts.Unstable.Contains symbol then Some OrderUnstable
    elif facts.StaticInit.Contains symbol then Some StaticInitOnly
    elif attrs |> List.exists (fun a -> a = "LiteralAttribute" || a = "Literal") then Some Literal
    elif attrs |> List.contains InlineAttributeName then Some Inline
    elif not (facts.Probed.Contains symbol) then Some NotProbed
    else None

/// The static file rules for changed files no trace claims: `DependsOnFile`/`DependsOnGlob`
/// declarations and runtime coverage. `None` when they select everything (a project file
/// changed).
let staticFileFallback (store: TestPrune.Ports.SymbolStore) (files: string list) : TestMethodInfo list option =
    match TestPrune.ImpactAnalysis.selectTests store files Map.empty |> fst with
    | TestPrune.ImpactAnalysis.RunAll _ -> None
    | TestPrune.ImpactAnalysis.RunSubset tests -> Some tests

let private parentDir (path: string) =
    match path.LastIndexOf '/' with
    | -1 -> ""
    | i -> path.Substring(0, i)

let select (store: Store) (req: Request) : Selection =
    let facts = store.VerificationFacts(req.TestProject, req.EnvFingerprint)
    let universe = req.Tests |> List.map keyOf |> Set.ofList

    let stored =
        facts.Tests |> Map.toSeq |> Seq.map (fun (k, v) -> normalizeKey k, v) |> Map.ofSeq

    let elsewhere = facts.OtherFingerprintKeys |> Set.map normalizeKey
    let selected = Dictionary<string, Rule list>()

    let add (key: string) (rule: Rule) =
        let key = normalizeKey key

        if universe.Contains key then
            selected[key] <-
                match selected.TryGetValue key with
                | true, rules when List.contains rule rules -> rules
                | true, rules -> rule :: rules
                | _ -> [ rule ]

    // R1, R4, R5
    for key in universe do
        match stored.TryFind key with
        | None -> add key (if elsewhere.Contains key then FingerprintMismatch else NoTrace)
        | Some(false, reasons) -> add key (Incomplete reasons)
        | Some(true, _) -> ()

    // R2
    let staleVersions =
        facts.Versions
        |> List.choose (fun (id, name, hash) ->
            if req.CurrentVersions.TryFind name = Some hash then None else Some id)

    for key, symbol in store.TestsWithVersions(req.TestProject, req.EnvFingerprint, staleVersions) do
        add key (SymbolChanged symbol)

    // R3
    let staleInputs =
        facts.Inputs
        |> List.choose (fun (kind, key, hash) -> if req.CurrentInput kind key = hash then None else Some(kind, key))
        |> List.distinct

    for key, kind, input in store.TestsWithInputs(req.TestProject, req.EnvFingerprint, staleInputs) do
        add key (InputChanged(kind, input))

    // R6
    let fallbacks =
        req.Seeds
        |> List.distinct
        |> List.choose (fun s -> fallReason facts req.AttributesOf s |> Option.map (fun r -> s, r))

    for reason, group in fallbacks |> List.groupBy snd do
        for t in req.StaticWalk(List.map fst group) do
            if t.TestProject = req.TestProject then
                add (keyOf t) (StaticFallback reason)

    // R7
    let claimed = facts.Inputs |> List.map (fun (_, key, _) -> key) |> Set.ofList

    let listed =
        facts.Inputs
        |> List.choose (fun (kind, key, _) -> if kind = "list" then Some key else None)
        |> Set.ofList

    let unclaimed =
        req.ChangedFiles
        |> List.distinct
        |> List.filter (fun f -> not (req.IsIndexed f || claimed.Contains f || listed.Contains(parentDir f)))

    match unclaimed with
    | [] -> ()
    | first :: _ ->
        match req.FileFallback unclaimed with
        | None ->
            for key in universe do
                add key (UntracedInput first)
        | Some tests ->
            for t in tests do
                if t.TestProject = req.TestProject then
                    add (keyOf t) (UntracedInput first)

        for key in req.FileGaps do
            add key (UntracedInput first)

    let complete =
        universe
        |> Seq.filter (fun k ->
            match stored.TryFind k with
            | Some(true, _) -> true
            | _ -> false)
        |> Seq.length

    let share =
        if universe.IsEmpty then 0.0 else float complete / float universe.Count

    { TestProject = req.TestProject
      Eligible = share >= EligibilityThreshold
      CompleteShare = share
      Universe = universe
      Selected = selected |> Seq.map (fun kv -> kv.Key, List.rev kv.Value) |> Map.ofSeq
      FallbackSeeds = Map.ofList fallbacks }
```

`TraceRun` records in the test file need `FingerprintInputs` (Task 4). If Task 4 has not landed in this
workspace yet, merge it first (`jj new <T4> <this>`), never copy its code.

- [ ] **Step 5: Run the new class**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.TraceSelectTests`
Expected: PASS (15 tests; the two theory rows count separately).

- [ ] **Step 6: Gate and commit**

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t5-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 6: `Shadow`: comparison, misses, the row, and the end-to-end pin

**Files:**

- Create: `src/TestPrune.Trace/Shadow.fs` (compile after `TraceSelect.fs`)
- Test: `tests/TestPrune.Trace.Tests/ShadowTests.fs` (new); `EndToEndTests.fs` (append one test)

**Interfaces:**

- Consumes: Task 5's `Selection`, `keyOf`, `ruleCode`, `fallCode`; Task 2's row types.
- Produces:

```fsharp
module TestPrune.Trace.Shadow

type Comparison = (see Cross-task contracts)

type ShadowRowInputs =
    { RunId: string
      Mode: TraceStore.ShadowRunMode
      Policy: TraceStore.SelectPolicy
      RecordPolicy: string
      TreeHash: string
      RecordedAt: System.DateTimeOffset
      Seeds: string list
      Selection: TraceSelect.Selection
      Comparison: Comparison
      PredictedFingerprint: string
      ActualFingerprint: string option
      PredictedHadTraces: bool
      SelectMs: int64
      TraceRecall: string
      StaticRecall: string }

val ListCap: int   // 200
val compare: TestMethodInfo list -> TestMethodInfo list -> TraceSelect.Selection -> Comparison
val verifiedFailureMisses: string -> TraceSelect.Selection -> string list -> (string -> bool) -> TraceStore.ShadowMiss list
val fingerprintOutcome: string -> string option -> bool -> TraceStore.FingerprintOutcome
val row: ShadowRowInputs -> TraceStore.ShadowSelectionRow
val logLine: TraceStore.ShadowSelectionRow -> missCount: int -> string
```

- [ ] **Step 1: Describe the change**

```bash
jj describe -m "Shadow: compare trace and static selections and record misses"
```

- [ ] **Step 2: Write the failing tests**

`tests/TestPrune.Trace.Tests/ShadowTests.fs`:

```fsharp
module TestPrune.Trace.Tests.ShadowTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.TraceStore
open TestPrune.Trace.TraceSelect

let private tm cls meth : TestMethodInfo =
    { SymbolFullName = $"T.%s{cls}.%s{meth}"; TestProject = "P"; TestClass = cls; TestMethod = meth }

let private universe = [ tm "K" "a"; tm "K" "b"; tm "L" "c"; tm "L" "d"; tm "M" "e" ]

let private selection (keys: (string * Rule) list) : Selection =
    { TestProject = "P"
      Eligible = true
      CompleteShare = 1.0
      Universe = universe |> List.map keyOf |> Set.ofList
      Selected = keys |> List.map (fun (k, r) -> k, [ r ]) |> Map.ofList
      FallbackSeeds = Map.empty }

[<Fact>]
let ``compare splits both sides and expands classes`` () =
    let staticSel = [ tm "K" "a"; tm "L" "c"; tm "L" "d" ]
    let trace = selection [ "P|K|a", SymbolChanged "N.f"; "P|M|e", SymbolChanged "N.g" ]
    let c = Shadow.compare universe staticSel trace
    test <@ c.StaticOnly = set [ "P|L|c"; "P|L|d" ] @>
    test <@ c.TraceOnly = set [ "P|M|e" ] @>
    test <@ c.StaticClasses = set [ "K"; "L" ] && c.TraceClasses = set [ "K"; "M" ] @>
    test <@ c.StaticClassTests = 4 && c.TraceClassTests = 3 @>

[<Fact>]
let ``another project's static tests are not compared`` () =
    let other = { tm "K" "z" with TestProject = "Q" }
    let c = Shadow.compare universe [ other ] (selection [])
    test <@ c.StaticTests |> Set.isEmpty @>

[<Fact>]
let ``a failure the trace selection did not select is a verified-failure miss`` () =
    let trace = selection [ "P|K|a", SymbolChanged "N.f" ]
    let misses = Shadow.verifiedFailureMisses "r1" trace [ "P|K|a"; "P|K|b"; "P|L|c" ] (fun k -> k = "P|L|c")
    test <@ misses |> List.map (fun m -> m.TestKey, m.Cause) = [ "P|K|b", Unexplained; "P|L|c", Flaky ] @>
    test <@ misses |> List.forall (fun m -> m.Kind = VerifiedFailure && m.RunId = "r1") @>

[<Fact>]
let ``a failing test outside the universe is not a miss`` () =
    test <@ Shadow.verifiedFailureMisses "r1" (selection []) [ "P|Gone|x" ] (fun _ -> false) |> List.isEmpty @>

[<Fact>]
let ``nested-class failures match normalised keys`` () =
    let trace = { selection [] with Universe = set [ "P|K.Inner|a" ] }
    test <@ Shadow.verifiedFailureMisses "r1" trace [ "P|K+Inner|a" ] (fun _ -> false) |> List.map _.TestKey = [ "P|K.Inner|a" ] @>

[<Theory>]
[<InlineData("E", "E", true, "hit")>]
[<InlineData("E", "F", true, "unsafe-miss")>]
[<InlineData("E", "F", false, "safe-miss")>]
[<InlineData("E", null, true, "untraced")>]
let ``fingerprint outcomes`` (predicted: string, actual: string, hadTraces: bool, expected: string) =
    let code =
        function
        | PredictionHit -> "hit"
        | SafeMiss -> "safe-miss"
        | UnsafeMiss -> "unsafe-miss"
        | NotTraced -> "untraced"

    test <@ code (Shadow.fingerprintOutcome predicted (Option.ofObj actual) hadTraces) = expected @>

[<Fact>]
let ``the row counts rules and caps its lists`` () =
    let many = [ for i in 1..300 -> tm "Big" $"t%d{i}" ]

    let trace =
        { selection [ "P|K|a", SymbolChanged "N.f"; "P|K|b", NoTrace ] with
            Universe = (universe @ many) |> List.map keyOf |> Set.ofList
            FallbackSeeds = Map.ofList [ "N.s", StaticInitOnly ] }

    let c = Shadow.compare (universe @ many) many trace

    let r =
        Shadow.row
            { RunId = "r1"; Mode = ImpactRun; Policy = ShadowPolicy; RecordPolicy = "every-run"; TreeHash = "t"
              RecordedAt = System.DateTimeOffset.UnixEpoch; Seeds = [ "N.f"; "N.s" ]; Selection = trace
              Comparison = c; PredictedFingerprint = "E"; ActualFingerprint = Some "E"; PredictedHadTraces = true
              SelectMs = 7L; TraceRecall = "n/a: impact run"; StaticRecall = "n/a: impact run" }

    test <@ r.RuleCounts = Map.ofList [ "R1", 1; "R2", 1 ] @>
    test <@ r.StaticOnlyClasses = [ "Big" ] @>
    test <@ r.StaticTests = 300 && r.TraceTests = 2 @>
    test <@ r.FallbackSeeds = Map.ofList [ "N.s", "static-init" ] @>
    test <@ r.FingerprintOutcome = PredictionHit @>
```

Append to `EndToEndTests.fs` (it already weaves FxTests, runs it traced, and ingests into a store over an index
of the fixture; reuse that setup):

```fsharp
[<Fact>]
let ``every indexed fixture test maps to a stored trace, and an untouched tree selects only incomplete ones`` () =
    let e = recordedFixture ()   // the existing helper that returns store, symbol store, fingerprint, tests
    let s =
        TraceSelect.select
            e.Store
            { TestProject = e.TestProject
              EnvFingerprint = e.Fingerprint
              Tests = e.Symbols.GetTestMethodsInProjects [ e.TestProject ]
              CurrentVersions = TraceIngest.versionHashes e.Symbols
              CurrentInput = TraceIngest.inputHash e.RepoRoot
              Seeds = []
              ChangedFiles = []
              IsIndexed = fun f -> (e.Symbols.GetFileKey f).IsSome
              AttributesOf = fun n -> e.Symbols.GetAttributesForSymbol n |> List.map fst
              StaticWalk = e.Symbols.QueryAffectedTests
              FileFallback = TraceSelect.staticFileFallback e.Symbols
              FileGaps = Set.empty }

    test <@ s.Selected |> Map.forall (fun _ rules -> rules |> List.forall (function Incomplete _ -> true | _ -> false)) @>
    test <@ s.Selected |> Map.forall (fun _ rules -> not (List.contains NoTrace rules)) @>
```

If the existing end-to-end setup does not expose those fields, extend its helper record rather than duplicate the
setup.

- [ ] **Step 3: Run and see them fail**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ShadowTests`
Expected: build error: `Shadow` is not defined.

- [ ] **Step 4: Implement** `src/TestPrune.Trace/Shadow.fs`:

```fsharp
/// Shadow mode: the trace selection computed next to the static one, compared, and the
/// comparison and every miss stored. Nothing here decides which tests run.
module TestPrune.Trace.Shadow

open TestPrune.AstAnalyzer
open TestPrune.Trace.TraceStore
open TestPrune.Trace.TraceSelect

type Comparison =
    { StaticTests: Set<string>
      TraceTests: Set<string>
      StaticOnly: Set<string>
      TraceOnly: Set<string>
      StaticClasses: Set<string>
      TraceClasses: Set<string>
      StaticClassTests: int
      TraceClassTests: int }

type ShadowRowInputs =
    { RunId: string
      Mode: ShadowRunMode
      Policy: SelectPolicy
      RecordPolicy: string
      TreeHash: string
      RecordedAt: System.DateTimeOffset
      Seeds: string list
      Selection: Selection
      Comparison: Comparison
      PredictedFingerprint: string
      ActualFingerprint: string option
      PredictedHadTraces: bool
      SelectMs: int64
      TraceRecall: string
      StaticRecall: string }

/// The most entries a row keeps of each test or class list.
[<Literal>]
let ListCap = 200

let private classOf (key: string) = key.Split('|').[1]

let compare (universe: TestMethodInfo list) (staticSelection: TestMethodInfo list) (trace: Selection) : Comparison =
    let project = trace.TestProject
    let ofProject = List.filter (fun (t: TestMethodInfo) -> t.TestProject = project)
    let staticKeys = staticSelection |> ofProject |> List.map keyOf |> Set.ofList
    let traceKeys = trace.Selected |> Map.keys |> Set.ofSeq
    let classes = Set.map classOf
    let universeKeys = universe |> ofProject |> List.map keyOf

    let launched (cs: Set<string>) =
        universeKeys |> List.filter (fun k -> cs.Contains(classOf k)) |> List.length

    { StaticTests = staticKeys
      TraceTests = traceKeys
      StaticOnly = Set.difference staticKeys traceKeys
      TraceOnly = Set.difference traceKeys staticKeys
      StaticClasses = classes staticKeys
      TraceClasses = classes traceKeys
      StaticClassTests = launched (classes staticKeys)
      TraceClassTests = launched (classes traceKeys) }

/// Tests that failed in this run although the trace selection computed at its launch did
/// not select them: their traces verified, and their outcome changed anyway.
let verifiedFailureMisses (runId: string) (trace: Selection) (failedKeys: string list) (isFlaky: string -> bool) : ShadowMiss list =
    failedKeys
    |> List.map normalizeKey
    |> List.distinct
    |> List.filter (fun k -> trace.Universe.Contains k && not (trace.Selected.ContainsKey k))
    |> List.map (fun k ->
        { RunId = runId
          TestProject = trace.TestProject
          TestKey = k
          Kind = VerifiedFailure
          Cause = (if isFlaky k then Flaky else Unexplained)
          Detail = "failed while its trace verified" })

let fingerprintOutcome (predicted: string) (actual: string option) (predictedHadTraces: bool) : FingerprintOutcome =
    match actual with
    | None -> NotTraced
    | Some a when a = predicted -> PredictionHit
    | Some _ when predictedHadTraces -> UnsafeMiss
    | Some _ -> SafeMiss

let private capped (xs: seq<string>) = xs |> Seq.sort |> Seq.truncate ListCap |> List.ofSeq

let row (i: ShadowRowInputs) : ShadowSelectionRow =
    let c = i.Comparison

    { RunId = i.RunId
      TestProject = i.Selection.TestProject
      Mode = i.Mode
      Policy = i.Policy
      RecordPolicy = i.RecordPolicy
      TreeHash = i.TreeHash
      RecordedAt = i.RecordedAt
      PredictedFingerprint = i.PredictedFingerprint
      ActualFingerprint = i.ActualFingerprint
      FingerprintOutcome = fingerprintOutcome i.PredictedFingerprint i.ActualFingerprint i.PredictedHadTraces
      Eligible = i.Selection.Eligible
      CompleteShare = i.Selection.CompleteShare
      Universe = i.Selection.Universe.Count
      StaticTests = c.StaticTests.Count
      TraceTests = c.TraceTests.Count
      StaticClasses = c.StaticClasses.Count
      TraceClasses = c.TraceClasses.Count
      StaticClassTests = c.StaticClassTests
      TraceClassTests = c.TraceClassTests
      RuleCounts =
        i.Selection.Selected
        |> Map.toList
        |> List.collect (fun (_, rules) -> rules |> List.map ruleCode |> List.distinct)
        |> List.countBy id
        |> Map.ofList
      StaticOnlyClasses = c.StaticOnly |> Set.map classOf |> capped
      TraceOnlyTests = capped c.TraceOnly
      ChildUntracedTests =
        i.Selection.Selected
        |> Map.filter (fun _ rules ->
            rules
            |> List.forall (function
                | Incomplete reasons -> reasons |> List.exists (fun r -> r.StartsWith "child-process-untraced:")
                | _ -> false))
        |> Map.count
      Seeds = i.Seeds
      FallbackSeeds = i.Selection.FallbackSeeds |> Map.map (fun _ r -> fallCode r)
      SelectMs = i.SelectMs
      TraceRecall = i.TraceRecall
      StaticRecall = i.StaticRecall }

let logLine (r: ShadowSelectionRow) (missCount: int) : string =
    let rules =
        r.RuleCounts |> Map.toList |> List.map (fun (k, v) -> $"%s{k} %d{v}") |> String.concat " "

    $"trace-shadow: %s{r.TestProject} static %d{r.StaticTests}/%d{r.StaticClasses}c, trace %d{r.TraceTests}/%d{r.TraceClasses}c "
    + $"(launch %d{r.TraceClassTests} vs %d{r.StaticClassTests}), trace-only %d{r.TraceOnlyTests.Length}, misses %d{missCount}, "
    + $"%s{rules}, %d{r.SelectMs} ms%s{if r.Eligible then "" else " (ineligible)"}"
```

- [ ] **Step 5: Run the new and touched classes**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ShadowTests --filter-class TestPrune.Trace.Tests.EndToEndTests`
Expected: PASS. If the end-to-end test finds indexed tests with `NoTrace`, the class keys disagree: print the
unmatched keys from both sides and fix `normalizeKey`, not the test.

- [ ] **Step 6: Gate and commit**

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t6-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 7: The mutation harness (`TESTPRUNE_TRACE_MUTATE`, `Mutation`, `mutate` verb)

Split into two commits: 7a (recorder, no dependency) and 7b (harness and verb, after Task 5).

**Files:**

- Modify: `src/TestPrune.Trace.Recorder/Contract.fs` (`MutateEnv`), `Probes.fs` (mutation check),
  `CHANGELOG.md`
- Create: `src/TestPrune.Trace/Mutation.fs` (compile after `Shadow.fs`)
- Modify: `src/TestPrune.Trace.Cli/Program.fs` (`mutate` verb)
- Test: `tests/TestPrune.Trace.Tests/RecorderStateTests.fs` (append), `MutationTests.fs` (new),
  `MeasurementCliTests.fs` (append)

**Interfaces:**

- Produces (recorder): `Contract.MutateEnv = "TESTPRUNE_TRACE_MUTATE"`, `MutantHitException` (Cross-task
  contracts). When the variable lists ids, a probe hit with one of them throws `MutantHitException id` after
  recording the hit. When it is unset, the only added cost is one static-field null check per hit.
- Produces (harness):

```fsharp
module TestPrune.Trace.Mutation

type Sample = { Symbol: string; Ids: int list }

type MutateRequest =
    { RepoRoot: string
      ProjectDir: string
      AssemblyName: string
      TestProject: string
      Store: TraceStore.Store
      Symbols: TestPrune.Ports.SymbolStore
      Samples: int
      Seed: int
      Since: System.DateTimeOffset
      Timeout: System.TimeSpan
      RunDir: string }

/// Seeds recorded by shadow rows since `since`, deduplicated, that the manifest can mutate.
val candidates: TraceStore.Store -> testProject: string -> since: System.DateTimeOffset -> Model.Manifest -> Joiner.JoinTarget[] -> Sample list * string list   // mutable, not-mutable
val choose: seed: int -> n: int -> Sample list -> Sample list
/// Failing keys of the mutant run minus those of the baseline; missed = failing not selected.
val judge: baseline: Set<string> -> mutant: Set<string> -> selected: Set<string> -> TraceStore.MutationOutcome * Set<string> * Set<string>
/// CTRF names to normalised test keys, through the universe's class.method stems.
val keysOfNames: universe: TestPrune.AstAnalyzer.TestMethodInfo list -> names: string list -> string list
val run: MutateRequest -> Result<TraceStore.MutationSample list, string>
```

- [ ] **Step 1 (7a): Describe**

```bash
jj describe -m "Recorder: TESTPRUNE_TRACE_MUTATE makes listed probes throw"
```

- [ ] **Step 2 (7a): Failing test** (append to `RecorderStateTests.fs`; the file installs a fresh state through
  `Runtime.state`; do the same for `Runtime.mutants`):

```fsharp
[<Fact>]
let ``a listed probe throws after it is recorded, an unlisted one does not`` () =
    let saved = Runtime.mutants

    try
        Runtime.mutants <- Runtime.parseMutants "3, 5"
        Probes.Hit 4
        let ex = Assert.Throws<MutantHitException>(fun () -> Probes.Hit 5)
        test <@ ex.Id = 5 @>
    finally
        Runtime.mutants <- saved

[<Fact>]
let ``no variable means no mutants`` () =
    test <@ isNull (Runtime.parseMutants null) @>
    test <@ isNull (Runtime.parseMutants "") @>
```

- [ ] **Step 3 (7a): Run, see it fail, implement**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.RecorderStateTests`
Expected first: build error naming `Runtime.mutants`.

Implementation (`Contract.fs`):

```fsharp
/// Comma-separated probe ids that throw `MutantHitException` when hit (mutation testing only).
[<Literal>]
let MutateEnv = "TESTPRUNE_TRACE_MUTATE"
```

`Probes.fs`, in the `Runtime` module and on `Probes.Hit`:

```fsharp
/// Thrown by a probe listed in TESTPRUNE_TRACE_MUTATE: the code it stands for "changed".
type MutantHitException(id: int) =
    inherit Exception($"mutant probe %d{id} hit")
    member _.Id = id

    // in module Runtime:
    /// The ids listed in TESTPRUNE_TRACE_MUTATE, or null. Null in every run but a mutation run.
    let parseMutants (value: string) : bool[] =
        if String.IsNullOrWhiteSpace value then
            null
        else
            let ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.map int
            let flags = Array.zeroCreate (Array.max ids + 1)
            for id in ids do
                flags.[id] <- true
            flags

    let mutable mutants: bool[] = parseMutants (Environment.GetEnvironmentVariable Contract.MutateEnv)

    // Probes.Hit:
    static member Hit(id: int) : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.Hit id

        let m = Runtime.mutants

        if not (isNull m) && uint32 id < uint32 m.Length && m.[id] then
            raise (MutantHitException id)
```

Rerun; expected PASS. Record in `src/TestPrune.Trace.Recorder/CHANGELOG.md`:
`- feat: TESTPRUNE_TRACE_MUTATE makes the listed probe ids throw MutantHitException (mutation testing).`
Gate (`mise run ci`, redirect as above) and `jj new`.

- [ ] **Step 4 (7b): Describe**

```bash
jj describe -m "test-prune-traces mutate: measure trace selection against thrown mutants"
```

- [ ] **Step 5 (7b): Failing tests**

`tests/TestPrune.Trace.Tests/MutationTests.fs`:

```fsharp
module TestPrune.Trace.Tests.MutationTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.TraceStore

[<Fact>]
let ``judge subtracts the baseline and names what the selection missed`` () =
    let outcome, failing, missed = Mutation.judge (set [ "P|K|flaky" ]) (set [ "P|K|a"; "P|K|b"; "P|K|flaky" ]) (set [ "P|K|a" ])
    test <@ outcome = Measured @>
    test <@ failing = set [ "P|K|a"; "P|K|b" ] @>
    test <@ missed = set [ "P|K|b" ] @>

[<Fact>]
let ``a mutant that fails nothing is not a sample`` () =
    let outcome, _, _ = Mutation.judge Set.empty Set.empty Set.empty
    test <@ outcome = NoFailure @>

[<Fact>]
let ``choose is deterministic for a seed`` () =
    let xs = [ for i in 1..20 -> ({ Symbol = $"S%d{i}"; Ids = [ i ] }: Mutation.Sample) ]
    test <@ Mutation.choose 7 5 xs = Mutation.choose 7 5 xs @>
    test <@ (Mutation.choose 7 5 xs).Length = 5 @>
    test <@ Mutation.choose 7 50 xs |> List.length = 20 @>

[<Fact>]
let ``CTRF names map to keys through the universe, theory rows included`` () =
    let u =
        [ { SymbolFullName = "T.K.a"; TestProject = "P"; TestClass = "Ns.K"; TestMethod = "a" }
          { SymbolFullName = "T.K.b"; TestProject = "P"; TestClass = "Ns.K+Inner"; TestMethod = "b" } ]

    test <@ Mutation.keysOfNames u [ "Ns.K.a"; "Ns.K+Inner.b(x: 1)"; "Ns.Gone.c" ] = [ "P|Ns.K|a"; "P|Ns.K.Inner|b" ] @>
```

Append to `MeasurementCliTests.fs` a usage test in the style of the existing verbs:

```fsharp
[<Fact>]
let ``mutate without --project-dir is a usage error`` () =
    let o = Program.dispatch (System.IO.Directory.GetCurrentDirectory()) [ "mutate" ]
    test <@ o.Exit = 2 && o.Stderr.Contains "usage: test-prune-traces mutate" @>
```

(Use the dispatch function name the existing CLI tests use.)

Add one end-to-end test to `EndToEndTests.fs` on the FxTests fixture: mutate `FxLib`'s function that one fixture
test class executes, and require that the failing tests equal that class's tests and `missed` is empty. Pick
the symbol from the fixture's `Logic.fs` that exactly one test class calls; name it in the test.

- [ ] **Step 6 (7b): Implement `Mutation.fs`**

- `candidates`: read `store.ShadowRows(testProject, since)`, collect `Seeds`, distinct; map each to the manifest
  row ids whose `joinManifest` target is `ToSymbol seed`. Seeds with no id are returned as "not mutable" (each
  becomes a `NotMutable` sample in `run`, so the report can count them).
- `choose`: `System.Random(seed)`; Fisher–Yates over a copy; take `n`.
- `keysOfNames`: build a map from `normalizeKey (cls + "." + method)` to `keyOf t` over the universe, then look
  each name up by its stem (the name up to `(`, `+` → `.`). Unknown names are dropped.
- `run`:
  1. `TraceSession.prepareProject` (a refused project returns `Error reason`);
  2. a baseline run with `Ctrf.run` and the launch's environment **without** `MutateEnv` and with the recorder's
     dump directory pointed at a scratch directory under `RunDir` (dumps are discarded);
  3. per chosen sample, the same run with `MutateEnv = String.Join(",", sample.Ids)`;
  4. the trace selection for "the sample's symbol changed": `TraceSelect.select` with
     `CurrentVersions = (versionHashes Symbols).Add(symbol, "mutant")`, `Seeds = [ symbol ]`, no changed files,
     the project's latest fingerprint (`store.LatestFingerprint`), and the same static walk and attributes a host
     passes;
  5. `judge`; `store.RecordMutation(sample, misses)` with one `MutationMiss` per missed key (cause `Unexplained`;
     a mutant's failure is deterministic by construction).
  A run with no CTRF report is `RunFailed`; it is stored and does not count toward the 50.
- **Union-type samples** (after Task 11; `MutationKind.UnionTypeMutant`). Candidates are the index's union types
  (Type symbols with `DuCase` children) that some manifest `TypeUse` row joins to. A sample sets
  `TESTPRUNE_TRACE_MUTATE` to every probe id that joins to the Type **and** `TESTPRUNE_TRACE_MUTATE_TYPES` to its
  CLR name, so every `typeof<U>`, repository generic instantiation over `U` and union-reflection call on `U`
  throws. The selection it is judged against is "U changed": `CurrentVersions` with U's version replaced and
  `Seeds = [ U ]`, which is what adding a nullary case produces (Task 11 Step 2 pins that a new case changes U's
  hash and nothing else). This is the promotion evidence for "add a nullary case to a union whose cases are
  enumerated → the enumerating callers are reselected": a missed test is one that reached U by reflection
  without U in its trace. `--kinds symbol,union-type` (default both) and `--union-samples 5` choose the mix.

- [ ] **Step 7 (7b): The verb**

`test-prune-traces mutate --project-dir <dir> --assembly <name> [--project <name>] [--db <path>] [--index <path>] [--samples 10] [--seed 1] [--since 14d] [--timeout-min 30]`

- `--index` defaults to `.fshw/test-impact.db` under the working directory, else `.test-prune.db`.
- **Before** opening the index as a `Database`, read its `PRAGMA user_version` through a read-only
  `SqliteConnection` (`Mode=ReadOnly`). If it differs from `Database.SchemaVersion`, exit 2 with
  `index schema v<found>; this test-prune-traces reads v<supported>`. `Database.create` deletes an index of
  another version, and this verb must never delete a host's index.
- Prints one line per sample (`<symbol>: failing 12, selected 40, missed 0`) and a summary; exit 0 when no
  measured sample missed a test, 1 otherwise, 2 on a usage error or an unreadable store or index.

- [ ] **Step 8 (7b): Run the new and touched classes, gate, commit**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.MutationTests --filter-class TestPrune.Trace.Tests.MeasurementCliTests --filter-class TestPrune.Trace.Tests.EndToEndTests`
Expected: PASS.

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t7-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 8: `shadow-report`: the promotion bar as a command

**Files:**

- Create: `src/TestPrune.Trace/ShadowReport.fs` (compile after `Mutation.fs`)
- Modify: `src/TestPrune.Trace.Cli/Program.fs` (`shadow-report` verb)
- Test: `tests/TestPrune.Trace.Tests/ShadowReportTests.fs` (new)

**Interfaces:**

```fsharp
module TestPrune.Trace.ShadowReport

type Bars =
    { MinDays: float                 // 14
      MinImpactRuns: int             // 100
      MinFullRuns: int               // 10
      MinMutationSamples: int        // 50
      MinUnionTypeSamples: int       // 5
      /// How many union types the project has whose mutant fails a test, when known (the `mutate` verb
      /// prints it); the union-type count is then min(MinUnionTypeSamples, this).
      UnionTypeCandidates: int option
      MinEligibleShare: float        // 0.95
      MaxSafeMissShare: float        // 0.05
      MaxSelectMsP95: int64          // 1000
      MaxLaunchRatio: float }        // 0.5

val defaults: Bars

type BarResult = { Name: string; Measured: string; Threshold: string; Pass: bool }

val evaluate:
    Bars ->
    rows: TraceStore.ShadowSelectionRow list ->
    misses: (TraceStore.ShadowMiss * System.DateTimeOffset) list ->
    samples: TraceStore.MutationSample list ->
        BarResult list
val render: BarResult list -> string
val passes: BarResult list -> bool
```

- [ ] **Step 1: Describe**

```bash
jj describe -m "test-prune-traces shadow-report: the phase-2 promotion bar"
```

- [ ] **Step 2: Failing tests** `tests/TestPrune.Trace.Tests/ShadowReportTests.fs`:

```fsharp
module TestPrune.Trace.Tests.ShadowReportTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.TraceStore

let private t0 = DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)

let private baseRow : ShadowSelectionRow =
    { RunId = "r"; TestProject = "P"; Mode = ImpactRun; Policy = ShadowPolicy; RecordPolicy = "every-run"
      TreeHash = "t"; RecordedAt = t0; PredictedFingerprint = "E"; ActualFingerprint = Some "E"
      FingerprintOutcome = PredictionHit; Eligible = true; CompleteShare = 1.0; Universe = 100
      StaticTests = 50; TraceTests = 5; StaticClasses = 5; TraceClasses = 1; StaticClassTests = 50; TraceClassTests = 10
      RuleCounts = Map.empty; StaticOnlyClasses = []; TraceOnlyTests = []; ChildUntracedTests = 0; Seeds = [ "N.f" ]; FallbackSeeds = Map.empty
      SelectMs = 100L; TraceRecall = "n/a: impact run"; StaticRecall = "n/a: impact run" }

/// 15 days, 120 impact rows, 12 full rows with measured recall 2/2, all green.
let private greenRows =
    [ for i in 0..119 -> { baseRow with RunId = $"i%d{i}"; RecordedAt = t0.AddHours(float i * 3.0) } ]
    @ [ for i in 0..11 ->
            { baseRow with RunId = $"f%d{i}"; Mode = FullSuiteRun; RecordedAt = t0.AddDays(float i + 0.5); TraceRecall = "2/2"; StaticRecall = "2/2" } ]

let private greenSamples =
    [ for i in 1..55 ->
          { TestProject = "P"; Kind = (if i > 50 then UnionTypeMutant else SymbolMutant); Symbol = $"S%d{i}"
            TreeHash = "t"; RecordedAt = t0; Outcome = Measured; Failing = 3; Selected = 9; Missed = [] } ]

let private bar name results = results |> List.find (fun (r: ShadowReport.BarResult) -> r.Name = name)

[<Fact>]
let ``a green window passes every bar`` () =
    let results = ShadowReport.evaluate ShadowReport.defaults greenRows [] greenSamples
    test <@ ShadowReport.passes results @>

[<Fact>]
let ``one unexplained verified-failure miss fails; a flaky one is listed and passes`` () =
    let miss cause = { RunId = "i3"; TestProject = "P"; TestKey = "P|K|a"; Kind = VerifiedFailure; Cause = cause; Detail = "" }, t0
    test <@ not (bar "verified-failure misses" (ShadowReport.evaluate ShadowReport.defaults greenRows [ miss Unexplained ] greenSamples)).Pass @>
    let flaky = bar "verified-failure misses" (ShadowReport.evaluate ShadowReport.defaults greenRows [ miss Flaky ] greenSamples)
    test <@ flaky.Pass && flaky.Measured.Contains "P|K|a" @>

[<Fact>]
let ``a full run whose trace recall is below 1 fails`` () =
    let rows = greenRows |> List.map (fun r -> if r.RunId = "f3" then { r with TraceRecall = "1/2" } else r)
    test <@ not (bar "trace recall on full runs" (ShadowReport.evaluate ShadowReport.defaults rows [] greenSamples)).Pass @>

[<Fact>]
let ``too few mutation samples or any mutation miss fails`` () =
    test <@ not (bar "mutation" (ShadowReport.evaluate ShadowReport.defaults greenRows [] (List.truncate 49 greenSamples))).Pass @>
    let missed = { greenSamples.Head with Missed = [ "P|K|a" ] } :: greenSamples.Tail
    test <@ not (bar "mutation" (ShadowReport.evaluate ShadowReport.defaults greenRows [] missed)).Pass @>

[<Fact>]
let ``too few union-type samples fails, unless the project has no more union types to sample`` () =
    let symbolOnly = greenSamples |> List.filter (fun s -> s.Kind = SymbolMutant)
    test <@ not (bar "mutation" (ShadowReport.evaluate ShadowReport.defaults greenRows [] symbolOnly)).Pass @>
    let noUnions = { ShadowReport.defaults with UnionTypeCandidates = Some 0 }
    test <@ (bar "mutation" (ShadowReport.evaluate noUnions greenRows [] symbolOnly)).Pass @>

[<Fact>]
let ``an unsafe fingerprint miss fails`` () =
    let rows = { greenRows.Head with FingerprintOutcome = UnsafeMiss } :: greenRows.Tail
    test <@ not (bar "fingerprint prediction" (ShadowReport.evaluate ShadowReport.defaults rows [] greenSamples)).Pass @>

[<Fact>]
let ``a window shorter than 14 days, or recorded under full-runs, fails`` () =
    let short = greenRows |> List.filter (fun r -> r.RecordedAt < t0.AddDays 10.0)
    test <@ not (bar "window" (ShadowReport.evaluate ShadowReport.defaults short [] greenSamples)).Pass @>
    let fullRuns = greenRows |> List.map (fun r -> { r with RecordPolicy = "full-runs" })
    test <@ not (bar "window" (ShadowReport.evaluate ShadowReport.defaults fullRuns [] greenSamples)).Pass @>

[<Fact>]
let ``the payoff bar is the median launch ratio of impact rows`` () =
    let heavy = greenRows |> List.map (fun r -> { r with TraceClassTests = 40 })
    test <@ not (bar "payoff" (ShadowReport.evaluate ShadowReport.defaults heavy [] greenSamples)).Pass @>
```

- [ ] **Step 3: Run, see them fail, implement**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ShadowReportTests`
Expected first: build error naming `ShadowReport`.

Implement each bar of D5 as one function returning a `BarResult`, named exactly as the tests use them: `window`,
`verified-failure misses`, `trace recall on full runs`, `mutation`, `eligibility`, `fingerprint prediction`,
`latency`, `payoff`. The mutation bar counts `SymbolMutant` and `UnionTypeMutant` samples separately and
requires `min(MinUnionTypeSamples, UnionTypeCandidates)` measured union-type samples when the candidate count
is known, else `MinUnionTypeSamples`. The payoff line prints) both the class-level ratio (the bar) and the method-level ratio
`trace_tests / static_tests` (Q5). After the table, `render` prints one informational section that never
affects `passes`:

- **Untraced children (Q2):** the median of `child_untraced_tests / trace_class_tests` over impact rows, and
  `allowlist task due` when it exceeds 0.05.
Misses are counted only when their row (joined by `RunId`) is eligible, and mutation misses
come from `samples`, not `misses`. `TraceRecall` parses as `r/t`; any other value is "not measurable" and is
counted in `Measured` without failing. p95 is nearest-rank over `SelectMs`. `render` prints a Markdown table of
`Name | Measured | Threshold | Verdict`, then the listed flaky misses and the not-measurable recall rows.

- [ ] **Step 4: The verb**

`test-prune-traces shadow-report --project <name> [--db <path>] [--since <n>d|<ISO date>] [--json] [--prune-before <ISO date>]`

With `--index <path>` (read-only, the same schema check as `mutate`), the verb counts the union types a
`mutate` run could sample and passes it as `UnionTypeCandidates`. With no `--since`, the window starts at the project's first shadow row (the D5 window is count-driven with a
14-day minimum, so a fixed look-back would cut off rows the counts need). After the bar table, the report prints
the child-process decision point (Decisions from review, Q2), which never fails it.

Exit 0 when `passes`, 1 when a bar fails, 2 on a usage error or an unreadable store. `--prune-before` deletes
shadow rows, misses and samples older than the date (after printing how many) and exits 0; it is the only code
path that deletes measurement rows. Add a CLI test per exit code in `MeasurementCliTests.fs`.

- [ ] **Step 5: Run the new and touched classes, gate, commit**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ShadowReportTests --filter-class TestPrune.Trace.Tests.MeasurementCliTests`
Expected: PASS.

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t8-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 9: File-census gaps feed R7

**Files:**

- Modify: `src/TestPrune.Trace/FileCensus.fs` (expose the per-test gap list)
- Modify: `src/TestPrune.Trace.Cli/Program.fs` (`file-census --store`)
- Test: `tests/TestPrune.Trace.Tests/MeasurementLogicTests.fs` (append), `MeasurementCliTests.fs` (append)

**Interfaces:**

- Consumes: Task 2's `RecordFileGaps`, `FileGaps`.
- Produces: `FileCensus.gaps: <the census result type> -> string list` (normalised test keys of tests that fail
  outside the repository and recorded no repository input); `--store` writes them with
  `store.RecordFileGaps(project, gaps)`.

- [ ] **Step 1: Describe**

```bash
jj describe -m "file-census --store: record per-test read gaps for selection"
```

- [ ] **Step 2: Failing test** (append to `MeasurementLogicTests.fs`, building the census result the way the
  existing file-census logic tests do, with three tests: one failing outside with an input, one failing outside
  with none, one passing outside):

```fsharp
[<Fact>]
let ``gaps are the tests that fail outside the repository with no recorded input`` () =
    let census = fileCensusOf [ "P|K|withInput", true, true; "P|K|gap", true, false; "P|K|fine", false, false ]
    test <@ FileCensus.gaps census = [ "P|K|gap" ] @>
```

(`fileCensusOf` is a local helper in the test file building the census record from
`(key, failsOutside, hasRepoInput)` triples; write it against the census record's real fields.)

- [ ] **Step 3: Run, see it fail, implement, rerun**

Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.MeasurementLogicTests`
Expected first: build error naming `FileCensus.gaps`; then PASS.

- [ ] **Step 4: The flag**, with a CLI test that `--store` writes the gaps to a temp database and a second run
  replaces them. Gate and commit:

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t9-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 11: Reflection inputs: type-set inputs and Type touches

Decisions from review, Q3. Two shapes of reflection change a test's outcome with no traced symbol changing:
whole-assembly type sweeps (a new type in an existing file) and union-case enumeration (a new case). This task
records both.

**Files:**

- Modify: `src/TestPrune.Trace/Model.fs` (`InputKind` gains `TypeSet` and `TypeReflected`)
- Create: `src/TestPrune.Trace.Recorder/Reflect.fs` (shims; compile after `Io.fs`)
- Modify: `src/TestPrune.Trace.Recorder/DumpWriter.fs`, `src/TestPrune.Trace/DumpReader.fs` (input kinds `types`
  and `type`)
- Modify: `src/TestPrune.Trace/Redirects.fs` (reflection redirects)
- Modify: `src/TestPrune.Trace/SiteProbes.fs` (`ldtoken` and repository generic-instantiation Type probes)
- Modify: `src/TestPrune.Trace/TraceIngest.fs` (`inputHash "types"`; `TypeReflected` joined to the Type symbol)
- Modify: `tests/TraceFixtures/src/FxLib/` (new `Shapes.fs`), `tests/TraceFixtures/tests/FxTests/` (new
  `ReflectionTests.fs`)
- Test: `tests/TestPrune.Trace.Tests/ReflectionInputTests.fs` (new; unit), `ReflectionReproTests.fs` (new; the
  two reproducers), `tests/TestPrune.Tests/AstAnalyzerTests.fs` (the header-hash pin)

**Interfaces:**

- Produces:

```fsharp
// Model
type InputKind =
    | FileRead
    | ExistenceProbe
    | DirectoryListing
    /// An assembly's types were enumerated. Path: the assembly's file.
    | TypeSet
    /// A type was inspected through reflection. Path: "<assembly file>|<CLR full name of the type definition>".
    | TypeReflected

// TraceIngest
// inputHash root "types" key = SHA-256 of the assembly's sorted type-definition full names (nested with '+'),
//   read with System.Reflection.Metadata; "absent" when the file is missing.
val typeSetHash: path: string -> string

// Recorder
// Contract.MutateTypesEnv = "TESTPRUNE_TRACE_MUTATE_TYPES"   (';'-separated CLR full names)
// Reflect: static shims, each records its input in the current scope and then calls through:
//   GetTypes(Assembly), GetExportedTypes(Assembly), get_DefinedTypes(Assembly), get_ExportedTypes(Assembly),
//   ModuleGetTypes(Module), Load(String), Load(AssemblyName), LoadFrom(String),
//   and one shim per FSharp.Core union-reflection API that takes a Type or a UnionCaseInfo.
```

- Dump format: two new input kinds on the existing `inputs` array, `{"kind":"types","path":…}` and
  `{"kind":"type","path":"<file>|<name>"}`. The format name stays `testprune-trace/1`: the weaver, recorder and
  reader release together under `trace-v` (ADR 0007), and a reader meeting an unknown kind rejects the dump as
  it does today.

- [ ] **Step 1: Describe**

```bash
jj describe -m "Reflection inputs: type sets and Type touches for union enumeration"
```

- [ ] **Step 2: Pin the hash fact** (append to the `Traceability attributes` module of Task 1 in
  `tests/TestPrune.Tests/AstAnalyzerTests.fs`):

```fsharp
    [<Fact>]
    let ``adding a nullary case changes the union's header hash and no existing case's hash`` () =
        let hashes source =
            (analyze source).Symbols |> List.map (fun s -> s.FullName, s.ContentHash) |> Map.ofList

        let before = hashes "module M\ntype Colour =\n    | Red\n    | Green\n"
        let after = hashes "module M\ntype Colour =\n    | Red\n    | Green\n    | Blue\n"
        test <@ before.["M.Colour"] <> after.["M.Colour"] @>
        test <@ before.["M.Colour.Red"] = after.["M.Colour.Red"] @>
        test <@ before.["M.Colour.Green"] = after.["M.Colour.Green"] @>
```

  Run: `dotnet build && dotnet run --project tests/TestPrune.Tests --no-build -- --filter-class "TestPrune.Tests.AstAnalyzerTests+Traceability attributes"`.
  Expected: PASS without a Core change (the header hash keeps the name and order of every case). If it fails,
  stop: R2 cannot see a new case, and the fix belongs in Core's `hashTypeHeader` before anything below.

- [ ] **Step 3: The fixture.** `tests/TraceFixtures/src/FxLib/Shapes.fs` (add it to `FxLib.fsproj` after the
  existing files):

```fsharp
module FxLib.Shapes

open Microsoft.FSharp.Reflection

type Colour =
    | Red
    | Green

/// A generic helper: `typeof<'T>` names no type in its own IL.
let unionCases<'T> () =
    FSharpType.GetUnionCases typeof<'T> |> Array.map (fun c -> c.Name)

module Colour =
    /// Built once, in the module's static initializer.
    let all = FSharpType.GetUnionCases typeof<Colour> |> Array.map (fun c -> c.Name)
    let names () = unionCases<Colour> ()

/// Reflection over a value's own type.
let caseNamesOf (value: obj) =
    FSharpType.GetUnionCases(value.GetType()) |> Array.map (fun c -> c.Name)

/// A production audit that loads an assembly by name and sweeps its types.
let audit () =
    System.Reflection.Assembly.Load("FxLib").GetTypes()
    |> Array.filter (fun t -> t.Name.StartsWith "Forbidden")
    |> Array.length
```

  `tests/TraceFixtures/tests/FxTests/ReflectionTests.fs`:

```fsharp
module FxTests.ReflectionTests

open Xunit
open FxLib.Shapes

[<Fact>]
let ``all colours are listed`` () = Assert.Equal<string[]>([| "Red"; "Green" |], Colour.all)

[<Fact>]
let ``the helper names every colour`` () = Assert.Equal<string[]>([| "Red"; "Green" |], Colour.names ())

[<Fact>]
let ``a value's cases are named`` () = Assert.Equal<string[]>([| "Red"; "Green" |], caseNamesOf (box Red))

[<Fact>]
let ``no type in FxLib is forbidden (sweep)`` () =
    Assert.DoesNotContain(typeof<Colour>.Assembly.GetTypes(), fun t -> t.Name.StartsWith "Forbidden")

[<Fact>]
let ``the audit finds nothing forbidden`` () = Assert.Equal(0, audit ())

[<Fact>]
let ``a test that never reflects`` () = Assert.Equal(2, 1 + 1)
```

- [ ] **Step 4: Write the failing unit tests** (`ReflectionInputTests.fs`):

```fsharp
module TestPrune.Trace.Tests.ReflectionInputTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace

[<Fact>]
let ``the type-set hash is the sorted type names of the assembly`` () =
    let fxLib = Fixtures.builtAssembly "FxLib"   // the path the existing fixture helpers resolve
    let h = TraceIngest.typeSetHash fxLib
    test <@ h = TraceIngest.typeSetHash fxLib @>
    test <@ h <> "absent" @>
    test <@ TraceIngest.typeSetHash (Path.Combine(Path.GetTempPath(), "missing.dll")) = "absent" @>

[<Fact>]
let ``inputHash reads a types key as the type-set hash`` () =
    let root = Fixtures.repoRoot ()
    let rel = Fixtures.repoRelative (Fixtures.builtAssembly "FxLib")
    test <@ TraceIngest.inputHash root "types" rel = TraceIngest.typeSetHash (Path.Combine(root, rel)) @>

[<Fact>]
let ``every FSharp.Core union-reflection API that takes a Type or a UnionCaseInfo is redirected`` () =
    let apis =
        [ typeof<Microsoft.FSharp.Reflection.FSharpType>; typeof<Microsoft.FSharp.Reflection.FSharpValue> ]
        |> List.collect (fun t -> t.GetMethods() |> List.ofArray)
        |> List.filter (fun m -> m.IsStatic && m.Name.Contains "Union")
        |> List.filter (fun m ->
            m.GetParameters()
            |> Array.exists (fun p ->
                p.ParameterType = typeof<System.Type>
                || p.ParameterType = typeof<Microsoft.FSharp.Reflection.UnionCaseInfo>))
        |> List.map (fun m -> m.DeclaringType.FullName + "::" + m.Name)
        |> List.distinct

    let redirected =
        Redirects.table |> List.map (fun r -> r.DeclaringType + "::" + r.Name) |> Set.ofList

    test <@ apis |> List.filter (redirected.Contains >> not) |> List.isEmpty @>
```

  and the weave-shape tests, in `SiteProbeShapeTests.fs` style (weave FxLib, inspect the IL with Cecil):
  - `typeof<Colour>` in `Colour.all`'s initializer is preceded by a Type probe whose manifest row has kind `type`
    and type name `FxLib.Shapes/Colour` (Cecil spelling; the manifest converts to `+`);
  - the call `unionCases<Colour>()` in `Colour.names` is preceded by a Type probe for `Colour`; a call to
    `Array.map<UnionCaseInfo, string>` (FSharp.Core, not the repository) gets none;
  - `Assembly.GetTypes`, `Assembly.Load(string)` and `FSharpType.GetUnionCases` call sites in `Shapes.fs` are
    redirected to `Reflect`.

  Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ReflectionInputTests --filter-class TestPrune.Trace.Tests.SiteProbeShapeTests`
  Expected: build errors naming `typeSetHash` and `Reflect`.

- [ ] **Step 5: Implement.**
  1. **`typeSetHash`** (TraceIngest; `inputHash` routes `"types"` to it):

```fsharp
/// SHA-256 of an assembly's type-definition full names, sorted: what a whole-assembly
/// type sweep sees. Read from metadata, so the assembly is never loaded.
let typeSetHash (path: string) : string =
    if not (File.Exists path) then
        "absent"
    else
        use pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead path)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe

        let rec fullName (h: System.Reflection.Metadata.TypeDefinitionHandle) =
            let t = md.GetTypeDefinition h
            let name = md.GetString t.Name
            let outer = t.GetDeclaringType()

            if outer.IsNil then
                match md.GetString t.Namespace with
                | "" -> name
                | ns -> ns + "." + name
            else
                fullName outer + "+" + name

        md.TypeDefinitions |> Seq.map fullName |> Seq.sort |> String.concat "\n" |> sha256Text
```

  2. **Recorder shims** (`Reflect.fs`). Each notes its input through the same `Io.NoteWith(Runtime.state, kind,
     path)` the file shims use, then calls through:

```fsharp
namespace TestPrune.Trace.Recorder

open System
open System.Reflection
open Microsoft.FSharp.Reflection

[<AbstractClass; Sealed>]
type Reflect =
    static member private NoteAssembly(a: Assembly) =
        if not (isNull a) && not a.IsDynamic && a.Location <> "" then
            Io.NoteWith(Runtime.state, "types", a.Location)

    static member private NoteType(t: Type) =
        if not (isNull t) then
            let def = if t.IsGenericType then t.GetGenericTypeDefinition() else t

            if not def.Assembly.IsDynamic && def.Assembly.Location <> "" then
                Io.NoteWith(Runtime.state, "type", def.Assembly.Location + "|" + def.FullName)

            Runtime.throwIfMutantType def.FullName

    static member GetTypes(a: Assembly) : Type[] =
        Reflect.NoteAssembly a
        a.GetTypes()

    static member GetExportedTypes(a: Assembly) : Type[] =
        Reflect.NoteAssembly a
        a.GetExportedTypes()

    static member get_DefinedTypes(a: Assembly) =
        Reflect.NoteAssembly a
        a.DefinedTypes

    static member get_ExportedTypes(a: Assembly) =
        Reflect.NoteAssembly a
        a.ExportedTypes

    static member ModuleGetTypes(m: Module) : Type[] =
        Reflect.NoteAssembly m.Assembly
        m.GetTypes()

    static member Load(name: string) : Assembly =
        let a = Assembly.Load name
        Reflect.NoteAssembly a
        a

    static member Load(name: AssemblyName) : Assembly =
        let a = Assembly.Load name
        Reflect.NoteAssembly a
        a

    static member LoadFrom(path: string) : Assembly =
        let a = Assembly.LoadFrom path
        Reflect.NoteAssembly a
        a

    static member GetUnionCases(t: Type, bindingFlags: BindingFlags option) : UnionCaseInfo[] =
        Reflect.NoteType t
        FSharpType.GetUnionCases(t, ?bindingFlags = bindingFlags)

    // One shim per API the redirect-completeness test lists (GetUnionFields, PreComputeUnionTagReader,
    // PreComputeUnionTagMemberInfo, PreComputeUnionReader, PreComputeUnionConstructor,
    // PreComputeUnionConstructorInfo, MakeUnion, IsUnion, …), each noting `t` or `info.DeclaringType`.
    // Copy each shim's parameter list from the FSharp.Core method it replaces, as Cecil spells it, so
    // optional parameters (`FSharpOption<BindingFlags>`) and the `allowAccessToPrivateRepresentation`
    // overloads bind.
```

     `Runtime.throwIfMutantType` reads `TESTPRUNE_TRACE_MUTATE_TYPES` once (null when unset, as `mutants` is)
     and throws `MutantHitException -1` when the name is listed: a mutated union type makes every reflective use
     of it throw, as its probes do.
  3. **Redirects**: add each shim to `Redirects.table` with `ioInstance` for the instance members
     (`GetTypes`, `GetExportedTypes`, `get_DefinedTypes`, `get_ExportedTypes`, `Module.GetTypes`) and `io` for
     the statics (`Assembly.Load`, `Assembly.LoadFrom`, the FSharp.Core APIs). The pass already redirects in
     product **and** test assemblies, which is where the meta-tests sweep.
  4. **Type probes** (SiteProbes): before an `ldtoken` whose operand resolves to a type defined in the weave set,
     insert a `TypeUse` hit for that type (stack-neutral: `ldc.i4 id; call Probes.Hit` before the `ldtoken`). For
     a generic instance (`typeof<Result<Colour, string>>`), add one hit per generic argument defined in the weave
     set. Before a `call`, `callvirt` or `newobj` whose target is a **weave-set** method with generic arguments
     (a `GenericInstanceMethod`, or a method of a `GenericInstanceType`), insert a `TypeUse` hit for each generic
     argument defined in the weave set. Calls into FSharp.Core or the BCL get none: their generic arguments are
     not reflection by our code, and probing every `List.map<Colour, _>` would cost CPU for nothing.
  5. **Ingestion**: `TypeSet` inputs are file-like: key = repo-relative assembly path (`bin/Traced/` →
     `bin/Debug/`, as for every input), hash = `typeSetHash`. `TypeReflected` inputs are joined, not hashed:
     split the path at `|`, keep it only when the assembly is under the repository, resolve the CLR name with
     `Joiner.typeCandidates` against the index exactly as `TypeUse` rows are resolved, and add the Type symbol at
     its current version to the scope's symbols (so R2 covers it). A name that resolves to no symbol is
     `UnmappedCode "reflected-type:<name>"`, which marks the trace incomplete (R4): sound.

- [ ] **Step 6: The reproducers** (`ReflectionReproTests.fs`). Each copies the fixture mini-repository to a temp
  directory, builds it, indexes it and records one traced run of FxTests (the steps `EndToEndTests` already runs;
  factor them into a `recordFixtureAt root` helper instead of copying them), edits one source file, rebuilds,
  re-indexes and calls `TraceSelect.select` with the rebuilt index:

```fsharp
[<Fact>]
let ``a new type in an existing file re-selects the sweeping tests and nothing else`` () =
    let fx = recordFixtureAt (copyFixture ())
    fx.Append "src/FxLib/Values.fs" "\ntype ForbiddenAdded() = class end\n"
    let s = fx.RebuildAndSelect()

    let fxLibDll = "src/FxLib/bin/Debug/net10.0/FxLib.dll"
    test <@ s.Selected.[fx.Key "FxTests.ReflectionTests" "no type in FxLib is forbidden (sweep)"] = [ TraceSelect.InputChanged("types", fxLibDll) ] @>
    test <@ s.Selected.[fx.Key "FxTests.ReflectionTests" "the audit finds nothing forbidden"] = [ TraceSelect.InputChanged("types", fxLibDll) ] @>
    test <@ not (s.Selected.ContainsKey(fx.Key "FxTests.ReflectionTests" "a test that never reflects")) @>
    test <@ not (s.Selected.ContainsKey(fx.Key "FxTests.ReflectionTests" "the helper names every colour")) @>

[<Fact>]
let ``a new nullary case re-selects every test that enumerates the union`` () =
    let fx = recordFixtureAt (copyFixture ())
    fx.Replace "src/FxLib/Shapes.fs" "    | Green\n" "    | Green\n    | Blue\n"
    let s = fx.RebuildAndSelect()
    let colour = "FxLib.Shapes.Colour"
    let rulesOf name = s.Selected.[fx.Key "FxTests.ReflectionTests" name]
    test <@ rulesOf "the helper names every colour" |> List.contains (TraceSelect.SymbolChanged colour) @>   // generic-instantiation probe
    test <@ rulesOf "a value's cases are named" |> List.contains (TraceSelect.SymbolChanged colour) @>      // reflection shim
    // `Colour.all` runs in a static initializer: its test is reached by R2 through `ldtoken` in the
    // initializer only if static-init hits link to it; otherwise by R6 (Colour is a static-init fact).
    test <@ (rulesOf "all colours are listed") |> List.exists (function
                                                                | TraceSelect.SymbolChanged c -> c = colour
                                                                | TraceSelect.StaticFallback _ -> true
                                                                | _ -> false) @>
    test <@ not (s.Selected.ContainsKey(fx.Key "FxTests.ReflectionTests" "a test that never reflects")) @>
```

  The second reproducer passes `Seeds = [ "FxLib.Shapes.Colour" ]` (what the host passes for the edited symbol)
  so R6 can act. `fx.Key` builds the normalised key from the fixture's project, class and method. Both tests
  also run the edited suite and assert that the selected tests are the ones that now fail (the sweep and audit
  find `ForbiddenAdded`; the three enumerations see `Blue`), which shows the selection is not just non-empty but
  right.

  Run: `dotnet build && dotnet run --project tests/TestPrune.Trace.Tests --no-build -- --filter-class TestPrune.Trace.Tests.ReflectionReproTests`
  Expected before Step 5: both fail (the sweep tests are not selected; the helper and value tests are not
  selected). After Step 5: PASS.

- [ ] **Step 7: Overhead.** The new probes add work on every `typeof` and every repository generic call. Re-run
  phase 1's overhead bar on TestPrune.Tests with this tree's weaver:

```bash
dotnet run --project src/TestPrune.Trace.Cli --no-build -- overhead --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests --reps 3 > "$CLAUDE_JOB_DIR/tmp/t11-overhead.log" 2>&1; echo "exit=$?"
```

  Expected: exit 0, ratio ≤ 1.15. Record the ratio in the commit message.

- [ ] **Step 8: Gate and commit.** Also run `ReflectionInputTests`, `SiteProbeShapeTests`, `SiteProbeScenarioTests`,
  `WeaverTests`, `InputCaptureTests`, `DumpRoundTripTests`, `TraceIngestTests` and `EndToEndTests`: this task
  touches all of their subjects.

```bash
mise run ci > "$CLAUDE_JOB_DIR/tmp/t11-ci.log" 2>&1; echo "exit=$?"
jj new
```

---

## Task 10: Decision records, documentation and the releases

**Files:**

- Create: `docs/adr/0009-shadow-selection-contract.md` (D1, D2, D3's fallback classes)
- Create: `docs/adr/0010-fingerprint-prediction.md` (D4)
- Create: `docs/adr/0011-reflection-inputs.md` (Task 11: why type sweeps are a type-set input and union
  reflection a Type touch, and why generic arguments are probed only on calls into repository code)
- Modify: `docs/adr/0008-trace-deferred-options.md` (the "Selection from traces" line: now phase 2, shadow first,
  see ADR 0009; add the trusted-child allowlist and table-level R8 as deferred with their reasons)
- Modify: `README.md` and `docs/TestPrune.Trace/` (selection, shadow, the two new verbs; run `mise run sync-docs`)
- Modify: `CLAUDE.md` project-structure line for TestPrune.Trace ("record, and select in shadow")
- Modify: `src/TestPrune.Trace/CHANGELOG.md`, `src/TestPrune.Trace.Cli/CHANGELOG.md`

- [ ] **Step 1:** Write the two ADRs in the format of 0005–0008 (Status, Context, Decision, Consequences), one
  page each. ADR 0009 states the shadow contract as an invariant ("shadow never changes a run"), the miss
  taxonomy table, and why misses are "failed while its trace verified". ADR 0010 states the prediction, its
  validation, and the two rejected alternatives.
- [ ] **Step 2:** `mise run sync-docs && mise run sync-docs-check`; expected exit 0.
- [ ] **Step 3:** Gate: `mise run ci > "$CLAUDE_JOB_DIR/tmp/t10-ci.log" 2>&1; echo "exit=$?"`, expected 0. Commit
  `"Document shadow selection and fingerprint prediction"`, with the trailer.
- [ ] **Step 4: Release** with `mise run release` (never an unscoped `fssemantictagger release`). It tags
  `core-v` first (Task 1 is a schema bump: a minor version), waits for NuGet, then `trace-v`. Verify both
  packages restore from nuget.org before any FsHotWatch task pins them.

---

# FsHotWatch tasks

These run in `~/Developer/opensource/FsHotWatch` on their own jj workspace. F1 and F2 need no TestPrune release;
F3 and F4 need Task 10's releases pinned in `Directory.Packages.props` (`TestPrune.Core`, `TestPrune.Trace`)
first, as its own commit.

## Task F1: Parse `tests.traces.select`

**Files:**

- Modify: `src/FsHotWatch.TestPrune/Traces.fs` (`TraceSelectPolicy`, `TraceSettings.Select`, `parseSelect`)
- Modify: `src/FsHotWatch.Cli/DaemonConfig.fs` (the `tests.traces` block, near line 915)
- Test: the existing `tests.traces` config tests (search `tests/` for `traces.record has unknown value`); add
  cases in the same file

**Interfaces:**

```fsharp
type TraceSelectPolicy =
    | SelectOff
    | SelectShadow
    | SelectLive

// TraceSettings gains: Select: TraceSelectPolicy   (SelectOff when the key is absent)
// TraceSettings.parseSelect: string -> TraceSelectPolicy option   (case-insensitive "off" | "shadow" | "live")
```

- [ ] **Step 1: Describe** `jj describe -m "Config: tests.traces.select (off, shadow, live)"`
- [ ] **Step 2: Failing tests**, next to the existing `record` tests:

```fsharp
[<Theory>]
[<InlineData("shadow", "every-run")>]
[<InlineData("shadow", "full-runs")>]
[<InlineData("live", "every-run")>]
let ``a valid select value is read`` (select: string, record: string) =
    let cfg = parse $"""{{"tests":{{"projects":[{{"project":"A","command":"dotnet","args":"test"}}],"traces":{{"record":"%s{record}","select":"%s{select}"}}}}}}"""
    test <@ (traceSettingsOf cfg).Select = (TraceSettings.parseSelect select).Value @>

[<Fact>]
let ``select is off when absent`` () =
    let cfg = parse """{"tests":{"projects":[{"project":"A","command":"dotnet","args":"test"}],"traces":{"record":"every-run"}}}"""
    test <@ (traceSettingsOf cfg).Select = SelectOff @>

[<Theory>]
[<InlineData("sometimes", "every-run", "tests.traces.select has unknown value 'sometimes'")>]
[<InlineData("shadow", "off", "tests.traces.select needs tests.traces.record")>]
[<InlineData("live", "full-runs", "tests.traces.select \"live\" needs tests.traces.record \"every-run\"")>]
let ``an invalid select is a ConfigError`` (select: string, record: string, message: string) =
    let ex = Assert.ThrowsAny<exn>(fun () -> parse $"""{{"tests":{{"projects":[{{"project":"A","command":"dotnet","args":"test"}}],"traces":{{"record":"%s{record}","select":"%s{select}"}}}}}}""" |> ignore)
    test <@ ex.Message.Contains message @>
```

(`parse` and `traceSettingsOf` stand for the helpers the existing config tests use; use their real names.)

- [ ] **Step 3: Implement** in `DaemonConfig.fs`, after `record` is parsed:

```fsharp
                    let select =
                        match str "select" with
                        | None -> FsHotWatch.TestPrune.SelectOff
                        | Some raw ->
                            match FsHotWatch.TestPrune.TraceSettings.parseSelect raw with
                            | Some s -> s
                            | None ->
                                raise (ConfigError $"tests.traces.select has unknown value '%s{raw}' (expected off, shadow or live)")

                    match select, record with
                    | FsHotWatch.TestPrune.SelectOff, _ -> ()
                    | _, FsHotWatch.TestPrune.RecordOff ->
                        raise (ConfigError "tests.traces.select needs tests.traces.record: nothing would ever be selected from")
                    | FsHotWatch.TestPrune.SelectLive, FsHotWatch.TestPrune.RecordFullRuns ->
                        raise (ConfigError "tests.traces.select \"live\" needs tests.traces.record \"every-run\": traces recorded only by full runs go stale between them")
                    | _ -> ()
```

and `Select = select` in the settings record.

- [ ] **Step 4:** run the config test class and every class that constructs `TraceSettings`, then the gate
  (`fshw check`, output to a file, read the verdict), then `jj new`.

## Task F2: `TestMode.selectsByTrace`

**Files:**

- Modify: `src/FsHotWatch.TestPrune/TestMode.fs`
- Test: `tests/FsHotWatch.TestPrune.Tests/TestModeTests.fs` (or wherever `recordsTraces` is tested)

- [ ] **Step 1: Describe** `jj describe -m "TestMode.selectsByTrace: live trace selection only under check"`
- [ ] **Step 2: Failing test:**

```fsharp
[<Theory>]
[<InlineData("off", "check", false)>]
[<InlineData("shadow", "check", false)>]
[<InlineData("live", "check", true)>]
[<InlineData("live", "confirm", false)>]
let ``trace selection replaces static only when live under check`` (policy: string, mode: string, expected: bool) =
    let p = (TraceSettings.parseSelect policy).Value
    let m = if mode = "confirm" then TestMode.ofScope "full" else TestMode.initial
    test <@ TestMode.selectsByTrace p m = expected @>
```

- [ ] **Step 3: Implement** in `TestMode.fs`, next to `recordsTraces`:

```fsharp
    /// Whether a run launched under `mode` runs the trace selection instead of the static
    /// one. Only `live`, and only under `check`: `confirm` runs every test by definition.
    /// `shadow` computes the trace selection in both modes but never runs it.
    let selectsByTrace (policy: TraceSelectPolicy) (mode: TestMode) =
        match policy with
        | SelectLive -> not (requestsFullSuite mode)
        | SelectOff
        | SelectShadow -> false
```

- [ ] **Step 4:** run `TestModeSeamTests` too (it scans `src/` for mode branches outside `TestMode.fs`), gate,
  `jj new`.

## Task F3: Shadow at launch and completion

**Files:**

- Create: `src/FsHotWatch.TestPrune/TraceShadow.fs` (compile after `TraceRun.fs`)
- Modify: `src/FsHotWatch.TestPrune/TestPrunePlugin.fs`: `TestRunLaunch` gains `TraceShadow: TraceShadowLaunch option`
  (every construction site that sets `WouldHaveRun = None` sets `TraceShadow = None`); the launch chokepoint
  (after `affectedTestsList`, near line 7156); the completion fold (next to `CheckReach.classifyEvidence`, near
  line 9323)
- Test: `tests/FsHotWatch.TestPrune.Tests/TraceShadowTests.fs` (new, pure parts);
  `tests/FsHotWatch.IntegrationTests` (new class, the contract pin)

**Interfaces:**

```fsharp
namespace FsHotWatch.TestPrune

type TraceShadowProject =
    { Selection: TestPrune.Trace.TraceSelect.Selection
      Comparison: TestPrune.Trace.Shadow.Comparison
      Universe: TestPrune.AstAnalyzer.TestMethodInfo list
      PredictedFingerprint: string
      PredictedHadTraces: bool
      SelectMs: int64 }

type TraceShadowLaunch =
    { Projects: Map<string, TraceShadowProject>
      Seeds: string list
      Mode: TestPrune.Trace.TraceStore.ShadowRunMode
      Policy: TestPrune.Trace.TraceStore.SelectPolicy
      RecordPolicy: string }

type TraceShadowProjectInput =
    { Project: string
      /// `bin/Debug/<tfm>/` of the project's build, for its deps.json and runtimeconfig.json.
      OutputDir: string option
      AssemblyName: string }

[<RequireQualifiedAccess>]
module TraceShadow =
    /// The trace selection of every traced project, next to the static selection. `None`
    /// when select is off. A project whose selection throws is logged and left out; the
    /// whole step never throws.
    val atLaunch:
        settings: TraceSettings ->
        repoRoot: string ->
        excluded: Set<string> ->
        mode: TestMode ->
        symbols: TestPrune.Ports.SymbolStore ->
        projects: TraceShadowProjectInput list ->
        seeds: string list ->
        changedFiles: string list ->
        staticSelection: TestPrune.AstAnalyzer.TestMethodInfo list ->
        dotnetRoot: string option ->
        log: (string -> unit) ->
            TraceShadowLaunch option

    /// The classes live mode launches for an eligible project: the index's own class names
    /// (unnormalised), so they match `--filter-class`.
    val liveClasses: TraceShadowProject -> string list

    /// Write each project's row and misses. Never throws.
    val atCompletion:
        settings: TraceSettings ->
        repoRoot: string ->
        runId: string ->
        treeHash: string ->
        launch: TraceShadowLaunch ->
        failedKeys: Map<string, string list> ->      // project -> "<project>|<class>|<method>" of each exact failure
        isFlaky: (string -> bool) ->
        traceRecall: string ->
        staticRecall: string ->
        log: (string -> unit) ->
            unit
```

- [ ] **Step 1: Describe** `jj describe -m "Shadow trace selection next to the static one"`
- [ ] **Step 2: Failing unit tests** (`TraceShadowTests.fs`), with a temp trace store seeded through
  `TraceStore.RecordRun` as in TestPrune's `TraceSelectTests`, and an in-memory index
  (`TestPrune.InMemoryStore`) holding two test classes:
  - `atLaunch` with `SelectOff` returns `None` and does not open the store (point `DbPath` at a directory that
    does not exist and assert it was not created);
  - `atLaunch` with `SelectShadow` returns one project whose `Selection.Selected` holds exactly the test whose
    traced symbol version differs from the index;
  - an excluded project (`"traces": false`) is absent;
  - `liveClasses` returns `K+Inner`, not `K.Inner`, for a nested class;
  - `atCompletion` writes one `shadow_selections` row per project and one `verified-failure` miss for a failed
    key the selection did not hold; a second call with the same run id replaces the row;
  - `atCompletion` against an unreadable store logs one line and returns.
- [ ] **Step 3: Implement `TraceShadow.fs`:**
  - Open the store with `TraceStore.Store.Open (Path.Combine(repoRoot, settings.DbPath))` inside `try`; on
    `TraceSchemaNewerThanConsumer` log it once and return `None`.
  - Per project: predicted runtime from
    `RuntimeResolution.predictRuntime root (<OutputDir>/<AssemblyName>.runtimeconfig.json) (env DOTNET_ROLL_FORWARD)`,
    deps hash of `<OutputDir>/<AssemblyName>.deps.json`, then
    `Fingerprint.compute (Fingerprint.predict repoRoot settings.FingerprintInputs settings.FingerprintEnv deps runtime)`.
    No output directory or no runtime: skip the project with a log line (it runs untraced anyway).
  - `TraceSelect.select` with `CurrentVersions = TraceIngest.versionHashes symbols` computed **once** per launch
    for all projects, `CurrentInput = TraceIngest.inputHash repoRoot` memoised per `(kind, key)`,
    `IsIndexed = fun f -> (symbols.GetFileKey f).IsSome`,
    `AttributesOf = fun n -> symbols.GetAttributesForSymbol n |> List.map fst`,
    `StaticWalk = symbols.QueryAffectedTests`, `FileFallback = TraceSelect.staticFileFallback symbols`,
    `FileGaps = store.FileGaps project`. Time it with `Stopwatch`.
  - `Shadow.compare universe staticSelection selection`.
  - `atCompletion`: `store.RunFingerprint(runId, project)` for the actual E;
    `Shadow.verifiedFailureMisses`; `Shadow.row`; `store.RecordShadow`; `log (Shadow.logLine row misses.Length)`.
- [ ] **Step 4: Wire the two call sites** in `TestPrunePlugin.fs`:
  - **Launch**, right after `affectedTestsList`: call `TraceShadow.atLaunch` with the configured projects'
    `Target` (its `BinDir` plus the TFM directory the traced launch already resolves), `Set.toList
    launchedSymbols`, `inputs.ChangedFiles`, `affectedTestsList`, and `TracedLaunch.dotnetRootOfThisProcess ()`.
    Keep the result on the launch record: `TraceShadow = traceShadow`. Do not touch `symbolAffectedByProject`
    here (F4 does, under `selectsByTrace`).
  - **Completion**, next to `CheckReach.classifyEvidence launch.WouldHaveRun`: when `launch.TraceShadow` is
    `Some ts`,
    - build `failedKeys` from `failedTestsOfRun`'s `Ok` failures that name a class and a method
      (`TraceIngest.testKey f.Project cls meth`);
    - the trace recall: `CheckReach.measure (Some wouldHaveRunByTrace) failures`, where `wouldHaveRunByTrace` is
      `wouldHaveRunSelection` applied to the trace classes of every eligible project (static classes for the
      rest) with the same widenings `wouldHaveRun` used; format `RecallMeasured(r, t, _, _)` as `"r/t"` and
      `RecallNotMeasurable why` as `"n/a: " + why`; the static recall is the existing
      `conditionalFailureRecall` in the same format; impact runs pass `"n/a: impact run"` for both;
    - `isFlaky`: `Flakiness.computeFlakiness` over the history entry whose CTRF name stems to the key's
      `class.method` is greater than 0 (load the history once per completion);
    - call `TraceShadow.atCompletion`.
- [ ] **Step 5: The contract pin** (integration test, same scratch-repository fixture as phase 1's F4: two
  test classes, traces recorded by a `confirm`, `record: "every-run"`):
  1. Edit a function only class A executes. Run `fshw check` with `select: "off"`, capture the launched
     filter arguments and the verdict file.
  2. Revert, set `select: "shadow"`, repeat the same edit and `fshw check`.
  3. Assert: the launched filter arguments are identical; the verdict files are identical except for their run
     ids and timestamps; the trace store has one `shadow_selections` row per project for the second run, whose
     trace side is class A's tests.
  This is the test that pins "shadow never changes a run". Name it
  `` shadow mode launches exactly what off launches ``.
- [ ] **Step 6:** run the new and touched test classes (`TraceShadowTests`, the integration class,
  `TestModeSeamTests`, the CheckReach tests), then the gate, then `jj new`.

## Task F4: Live selection (behind `select: "live"`)

**Files:**

- Modify: `src/FsHotWatch.TestPrune/TestPrunePlugin.fs` (the launch chokepoint)
- Test: `tests/FsHotWatch.IntegrationTests` (new class)

- [ ] **Step 1: Describe** `jj describe -m "Live trace selection for eligible projects"`
- [ ] **Step 2: Failing integration tests** (same fixture as F3):
  - `select: "live"`, the edit from F3: the run launches class A only, and the shadow row's policy is `live`;
  - make the project ineligible (delete its traces from the store): the run launches the static selection;
  - `confirm` under `live` runs every class (pass-through is untouched);
  - an outstanding failure in class B (from a previous red) is still launched: R9 obligations apply after the
    trace selection.
- [ ] **Step 3: Implement** at the chokepoint, where `symbolAffectedByProject` is built:

```fsharp
                let symbolAffectedByProject =
                    match traceShadow with
                    | Some ts when TestMode.selectsByTrace traceSettings.Select inputs.Mode ->
                        ts.Projects
                        |> Map.fold
                            (fun acc project p ->
                                if p.Selection.Eligible then
                                    match TraceShadow.liveClasses p with
                                    | [] -> Map.remove project acc
                                    | classes -> Map.add project classes acc
                                else
                                    acc)
                            staticSymbolAffectedByProject
                    | _ -> staticSymbolAffectedByProject
```

  Everything after it (`quarantine`, fanout, `selectionOf`, `wouldHaveRun`) is unchanged, which is what makes R9
  and the other widenings apply to both sides. The shadow row under `live` compares the same two sets; nothing
  else changes in `TraceShadow`.
- [ ] **Step 4:** run the new class and F3's classes, gate, `jj new`. Then release FsHotWatch (its release
  runbook), bundling TestPrune.Core and TestPrune.Trace from Task 10.

---

# Done-bar

## Task D1: TestPrune's own shadow window (dogfood)

Runs in TestPrune once the FsHotWatch release with F3/F4 is out. The FsHotWatch pin runbook
(`docs/runbooks/fshotwatch-pin-upgrade.md`) fixes the order: release, then pin, then the full gate.

**Files:**

- Modify: `.config/dotnet-tools.json`, `tests/TestPrune.Tests/ToolchainPinTests.fs` (the pin bump, its own commit)
- Modify: `.fshw.json`: `tests.traces.record` → `"every-run"` (only once phase 1's F5 bar holds here),
  `tests.traces.select` → `"shadow"`
- Create: `docs/plans/<date>-recorded-per-test-traces-phase2-results.md`

- [ ] **Step 1:** Bump the pin exactly per the runbook; commit alone.
- [ ] **Step 2:** Enable shadow; run `dotnet fshw confirm` once to re-record under the new fingerprint (the core
  bump invalidated every trace), then work normally.
- [ ] **Step 3: Nightly until the report's counts are met, and for at least 14 days** (a scheduled job, or by
  hand). TestPrune's own `check` volume may take longer than 14 days to reach 100 impact runs; the window
  extends, the counts do not drop:

```bash
dotnet tool run test-prune-traces file-census --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests --store > "$CLAUDE_JOB_DIR/tmp/d1-files.log" 2>&1; echo "exit=$?"
dotnet tool run test-prune-traces mutate --project-dir tests/TestPrune.Tests --assembly TestPrune.Tests --samples 10 --seed "$(date +%j)" > "$CLAUDE_JOB_DIR/tmp/d1-mutate.log" 2>&1; echo "exit=$?"
```

- [ ] **Step 4: Report:**

```bash
dotnet tool run test-prune-traces shadow-report --project TestPrune.Tests > "$CLAUDE_JOB_DIR/tmp/d1-report.log" 2>&1; echo "exit=$?"
```

  Paste the table into the results file, with every listed miss explained. TestPrune.Trace.Tests is refused by
  the shadow bin (it ships its own recorder), so it is ineligible by construction and stays on static selection.
- [ ] **Step 5:** File each unexplained item as a defect in the owning repository's tracker **before** declaring
  the bar met. Commit the results file.

## Task D2: Promote TestPrune to live

Only after D1's report exits 0.

- [ ] **Step 1:** `.fshw.json`: `"select": "live"`. Run `fshw check` on an edit that the static graph
  over-selects (the phase-0 spike's example: a change to `NamedDispatch.registrationNames`, which static selects
  0 tests for and traces 15) and record the launched tests in the results file.
- [ ] **Step 2:** Re-run `shadow-report` weekly for 4 weeks; any failing bar reverts `select` to `"shadow"`
  first and is investigated second.

The consumer's shadow window and promotion are in the consumer plan, in the order D7 gives.

---

## Out of scope for phase 2

- Retiring composition-root barriers and the emptied-project fail-safe (phase 3; this phase records the fallback
  seeds it needs).
- Table-level R8 through the Npgsql `ActivitySource` (phase 4).
- Method-level filters in FsHotWatch. Live mode launches classes and the payoff bar is measured after class
  expansion; method-level filters are the named next task if class expansion alone fails the payoff bar (Q5).
- A trusted-executable allowlist for untraced child processes: a task once the report's untraced-children median
  exceeds 0.05 (Q2).
- The process-level `open()` interposer (ADR 0008; the census gaps cover what it would).
- A one-hop rule for `inline` and literal symbols (select only the tests whose traces hold a direct caller). The
  full static walk is sound; the shadow rows' `fallback_seeds` show whether the refinement is worth it.

## Open questions

None. Q1 to Q5 of the first draft are settled in "Decisions from review"; Q3 became Task 11.

## Self-review against the brief

| Brief item | Where |
|---|---|
| Shadow contract: compute alongside, never change which tests run, log and store the diff | D1; Task 6; F3 (the contract pin); Global Constraints |
| What counts as a miss and how it is measured: static extras vs trace misses | D2 table; Tasks 6, 7, 8 |
| A safety net for tests without traces | D2 ("Tests without traces"); D3 eligibility; R1/R4/R5 in Task 5 |
| Stale traces after content-hash changes | D2 ("Stale traces"); R5 through the hash scheme in E; verified-failure misses |
| The promotion bar, as commands with thresholds | D5; Task 8; D1 |
| File inputs from the file census, child processes | D6; R3 and R7 in Task 5; Task 9 |
| FsHotWatch integration | D7; Tasks F1–F4 |
| Consumer rollout order | D7; D2 (TestPrune first) |
| What phase 3 intends to retire (`QueryAffectedTests` barriers, the per-seed fail-safe) | D3 (R6 uses today's walk); D8; out of scope |
| Reflection: whole-assembly sweeps, union-case enumeration (review Q3) | Decisions from review Q3; Task 11 (type-set input, Type touches, two reproducers); Task 7 union-type samples; D5 mutation bar |
| R1–R9 from the design | "Background: the rules, restated"; Task 5 (R1–R7); D2 (R8); F4 (R9 applied after) |
