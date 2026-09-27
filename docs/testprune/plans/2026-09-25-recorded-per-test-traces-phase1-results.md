# Recorded per-test traces, phase 1: results on TestPrune's own suite

Measured 2026-09-26 with FsHotWatch CLI `0.14.0-alpha.69`, `tests.traces` =
`{"record": "full-runs", "fingerprintInputs": ["global.json", "Directory.Packages.props"]}`, macOS arm64.
The host was also running another repository's release gate at the time, so CPU numbers are noisy.

## Runs

| Run | Tree | Result | Recorded |
|---|---|---|---|
| `confirm` 1 | both projects traced | exit 1: 10 TestPrune.Trace.Tests failures (see below) | `b1a6f30e…` both projects |
| `confirm` 2 | TestPrune.Trace.Tests `"traces": false` | exit 0 | `2bdf1176…` TestPrune.Tests |
| `confirm` 3 | unchanged | exit 0: run-2 verdict reused, no tests ran | nothing new |

Runs 1 and 2 recorded identical TestPrune.Tests traces (same counters, 848 complete).
`trace_runs.status` is `recorded` with no reason for every run: preparation refused nothing.

## TestPrune.Trace.Tests is not traced

Traced, the suite's outcome changes: 10 tests that pass untraced fail. The shadow bin replaces the app's
own `TestPrune.Trace.Recorder` build with the weaver's bundled one, so the suite that tests the recorder
runs against a different, live recorder:

| Failure | Cause |
|---|---|
| `ScaffoldTests` stubs inert, `DumpRoundTripTests` counters | the process recorder is live |
| `ShadowBinTests` only repository assemblies are woven | the shadow bin ships the recorder without its PDB |
| `InputCaptureTests` / `LaunchTests` child-trace tests | the bundled recorder predates the child-scope fix |
| 5 `EndToEndTests` (`recorder-no-output`) | the bundled shim gives the woven fixture's process the parent's scope, so the fixture run has no main dump |

The run also overflowed (10,370 overflow hits, 259 of 280 tests `recorder-overflow`), because the suite's
own tests swap the process recorder for small test recorders. The project is marked `"traces": false`.
Phase 1 never changes a verdict, so the opt-out is required rather than a choice.

## Bars (TestPrune.Tests, run `2bdf1176…`)

| Bar | Pass when | Measured | Verdict |
|---|---|---|---|
| census | traced ≥ 0.99; ambient < 0.1 % or listed | traced 1024/1024 (1.0000); ambient 0/14,771,003 | pass |
| audit | `ExtraTotal = 0`; `MissingUser` explained | sampled 11, extra 15 (one test, see below) | fail: tool defect |
| overhead | CPU ratio ≤ 1.15 | 1.101 (untraced median 83.0 s, traced 91.4 s); traced max RSS 1,910.8 MiB | pass |
| file-census | ratio ≤ 0.05 | 0.048 (reads-repo 21, fail-outside 20, fail-in-repo 0) | pass |
| coverage parity | identical cobertura with traces removed | not measured: `confirm` here collects no coverage | open |
| PDB / JIT | `prepare` refused nothing | no refused run | pass |

### Incomplete traces (99 of 1024 tests; 848 of 947 stored traces complete)

| Reason | Tests | Cause |
|---|---|---|
| `unmapped-code` `<StartupCode$TestPrune-Falco>` closures (`no-document`) | 72 | 7 closure methods in `FalcoRouteAnalysis` / `RouteStore` resolve to no PDB document; joiner defect |
| `unmapped-code` F# `exception` types (`type-not-indexed`) | 13 | `SymbolTraversalBudgetExceeded`, `SymbolTraversalDepthExceeded`, `ProjectAnalysisFailed`, `ProjectOptionsEmpty` are not in the symbol index |
| `child-process-untraced` `dotnet`, `sleep`, `sh`, `chmod` | 14 | tests that start non-woven processes; expected by design |

### Audit

The one test with extras is the theory row
`rich types and patterns without anon records produce no diagnostics(body: "let f (p: int * string) = p")`.
Its 15 "extra" ids are everything it executes, its own method included, and the audit reports it as
"not recorded when run alone". The isolated `--filter-display-name` run did not select the row, so the
comparison was against nothing. It is an audit defect, not cross-test leakage. The `missing` entries are
static constructors, closures and union cases that an earlier test already ran in the parallel run.
The 8 `missing user` entries in `SymbolOccurrenceStoreTests` are AstAnalyzer record constructors and
union-case getters. They run once per process, so in the parallel run an earlier test ran them first.

## Defects found

| Owner | Defect |
|---|---|
| TestPrune.Trace (released recorder) | the `Process.Start` shim gives a child with its own dump directory the parent's scope, and `Launch.run` passes inherited `TESTPRUNE_TRACE_*` to the child. Fixed in this change; FsHotWatch must bundle the next recorder |
| TestPrune.Trace | the shadow bin silently substitutes its bundled recorder for an app's own build of the same version |
| TestPrune.Trace | joiner: closures in `<StartupCode$…>` classes have no document |
| TestPrune.Core / joiner | F# `exception` declarations are not indexed, so their probes are unmapped |
| TestPrune.Trace | audit: a sampled test the isolated run does not select is counted as extras |
| TestPrune.Trace | census reports a project's last recorded run after the project opted out |

## Second run (2026-09-27)

Measured with FsHotWatch CLI `0.14.0-alpha.71`, which bundles TestPrune.Core 13.2.2 (index schema 18: F#
exception types are indexed) and TestPrune.Trace 0.2.0. The `"traces": false` opt-out on
TestPrune.Trace.Tests is removed. Same `tests.traces`, macOS arm64. The host was loaded again (load average
17–39), so CPU numbers are noisy.

### Runs

| Run | Result | Recorded |
|---|---|---|
| `confirm` (fresh workspace, test-impact DB recreated at schema 18) | exit 0, green full-suite run: TestPrune.Tests 1033 passed + 1 skipped, TestPrune.Trace.Tests 303 passed | `8af7b8f8…` |

`trace_runs` for `8af7b8f8…`:

| Project | Status | Reason |
|---|---|---|
| TestPrune.Tests | `recorded` | none |
| TestPrune.Trace.Tests | `refused` | the app ships its own TestPrune.Trace.Recorder build; tracing would replace it (its deps.json lists the recorder as a project) |

The shadow bin now refuses TestPrune.Trace.Tests by itself. The suite runs untraced and stays green, with
no config opt-out. The measurement verbs refuse it with the same reason, so the bars below cover
TestPrune.Tests only.

### Bars (TestPrune.Tests)

| Bar | Pass when | First run (alpha.69, `2bdf1176…`) | Second run (alpha.71, `8af7b8f8…`) | Verdict |
|---|---|---|---|---|
| census: traced | ≥ 0.99 | 1024/1024 (1.0000) | 1030/1030 (1.0000) | pass |
| census: ambient | < 0.001, or listed | 0/14,771,003 | 0/14,851,732 | pass |
| audit | `ExtraTotal = 0`; `MissingUser` explained | sampled 11, extra 15 (display-filter defect) | sampled 11, extra 0; missing only `cctor`/`gen` | pass |
| overhead | CPU ratio ≤ 1.15 | 1.101 (83.0 s → 91.4 s); max RSS 1,910.8 MiB | 1.068 (67.4 s → 72.0 s); max RSS 1,781.3 MiB; see below | pass |
| file-census | ratio ≤ 0.05 | 0.048 (reads-repo 21, fail-outside 20, fail-in-repo 0) | 0.048 (reads-repo 21, fail-outside 20, fail-in-repo 0) | pass |
| coverage parity | identical cobertura with traces removed | not measured | lines identical, branches not; see "Coverage parity" below | fail |
| PDB / JIT | `prepare` refused nothing on a traced project | no refused run | 0 invalid of 5,774 verified methods; the one refusal is the intended own-recorder refusal | pass |

The theory row that produced the first run's 15 false extras
(`rich types and patterns without anon records produce no diagnostics(body: "let f (p: int * string) = p")`)
was sampled again and is now selected alone: extra 0, missing none. No `missing user` entries remain.

### Incomplete traces

| Reason | First run (tests) | Second run (tests) |
|---|---|---|
| `unmapped-code` `<StartupCode$…>` closures (`no-document`) | 72 | 0 |
| `unmapped-code` F# `exception` types (`type-not-indexed`) | 13 | 0 |
| `child-process-untraced` (`dotnet`, `sleep`, `sh`, `chmod`) | 14 | 14 (dotnet 6, sleep 5, sh 2, chmod 1) |
| **total incomplete / stored traces** | 99 / 947 (848 complete) | 14 / 953 (939 complete) |

`unmappedIds` is 0. The only incomplete traces left are tests that start non-woven processes, which is
expected by design.

### Overhead: an unexplained traced failure

The first `overhead --reps 3` exited 1. The ratio was 1.049, but traced samples 2 and 3 exited 2 (untraced
runs all exited 0). The verb discards the launched app's output, so the failing tests are unknown. The
failure did not come back in 8 more traced launches: 2 back-to-back runs sharing one dump directory, 3 runs
interleaved with untraced ones as the verb does, and a second `overhead` run (all 6 samples exit 0; its
numbers are in the table). The fshw daemon was idle during the failing run, so no rebuild overlapped it.
Host load average was 25–39 at the time.

| Owner | Defect |
|---|---|
| TestPrune.Trace | `overhead` (via `Launch.run`) discards a launch's output, so a sample with a nonzero exit cannot be diagnosed. It should keep the output of a failing launch |

### What still blocks the phase-1 bar

| Item | State |
|---|---|
| coverage parity | measured below: line coverage is identical, branch coverage is not |
| 3 consecutive traced full runs (D6 census bar) | this run recorded one `confirm` |
| intermittent traced exit 2 under load | 2 of 12 traced launches, not reproduced; open until a failing launch's output is kept |

### Follow-up (2026-09-27): overhead reruns and coverage parity

`overhead` and `audit` now print a nonzero-exit launch's output (TestPrune.Trace Unreleased), which fixes
the defect above.

#### Overhead reruns with output capture

| Run | Load average | Ratio | Untraced median | Traced median | Peak RSS | Exits |
|---|---|---|---|---|---|---|
| 1 | 24–28 | 1.022 | 66.2 s | 67.7 s | 1,831.7 MiB | all 0 |
| 2 | 24–31 | 1.027 | 69.1 s | 71.0 s | 1,802.5 MiB | all 0 |
| 3 | 22–27 | 1.043 | 65.6 s | 68.4 s | 1,840.9 MiB | all 0 |
| 4 | 19–25 | 1.030 | 67.0 s | 69.0 s | 1,777.0 MiB | all 0 |

None of the 12 traced launches exited nonzero, so the earlier exit 2 did not recur. Over the day, 2 of 23
traced measurement launches exited nonzero, both in the same `overhead` run. It stays open: the next failure will print its tests.

#### Coverage parity

The second run's "`confirm` collects no coverage" was wrong. fshw collects coverage by default
(`tests.projects[].coverage` defaults to `true`). Each `confirm` writes
`coverage/<project>/coverage.baseline.cobertura.xml` per project and a merged, line-only
`coverage/coverage.cobertura.xml`. Two `confirm` runs on the same tree, traced (run `ef14ad88…`, TestPrune.Tests
recorded, TestPrune.Trace.Tests refused) and with `tests.traces` removed, were compared per file on lines
covered/valid and branches covered/valid:

| Report | Files | Line counts | Branch counts | Recorder module |
|---|---|---|---|---|
| TestPrune.Tests (traced) | 31 = 31 | identical in every file (5,504 / 5,903) | **differ in 8 files**: traced 1,196 / 1,548, untraced 1,283 / 1,646 | none |
| TestPrune.Trace.Tests (refused, so untraced both times) | 32 = 32 | identical | identical (696 / 712) | none |
| merged `coverage/coverage.cobertura.xml` | 63 = 63 | identical (8,594 / 8,994) | none reported | none |

The traced report adds no branch and changes no line hit. It drops every branch point on 31 lines, 98 branch
points in all: `AstAnalyzer.fs` (38), `Orchestration.fs` (28), `SymbolDiff.fs` (16), `Database.fs` (8),
`AuditSink.fs`, `EdgeEmission.fs`, `ImpactAnalysis.fs`, `Program.fs` (2 each). Every such line is a union
`match` (`match change with`, `match r.Outcome with`) or a union-case test (`if sym.IsExtern then`). Those
are the sites where the site-probe pass instruments the union's tag read before the switch. The loss is
deterministic: two traced runs wrote byte-identical reports.

| Owner | Defect |
|---|---|
| TestPrune.Trace (weaver, site probes) | coverage of a woven assembly loses the branch points of union matches and union-case tests: 98 of 1,646 in TestPrune.Tests. Line coverage is unaffected. A consumer whose coverage ratchet reads per-project branch rates from a traced run would see them drop. Cause not yet confirmed; the tag-read probe before the `switch` is the likely one |


## Coverage parity with the weaver fix (2026-09-27)

`confirm` collects per-project cobertura by default. A traced and an untraced `confirm` on alpha.71 matched on
lines but not on branches: the traced TestPrune.Tests report lost 98 of 1,646 branch points (1,196 / 1,548
against 1,283 / 1,646), all on lines with a union `match`, a field comparison or a type test.

### Cause

MS CodeCoverage counts a conditional branch under a hidden sequence point only while no call lies between it
and its line's visible point. F# puts a match's or comparison's test under a hidden point, and a site probe
is a call inserted right there, so the line keeps its hits and loses its branch points. Observed on the IL:

| Unwoven shape | Branch counted |
|---|---|
| `nop [L]; ldarg [hidden]; ldfld; …; ble` | yes |
| `… [hidden]; callvirt get_IsCircle; brfalse` (an `if x.IsCase`) | no, even untraced |
| the same `ldfld` shape with a probe call after the `ldfld` | no |
| a probe, then a hidden copy of the enclosing point where the probe resumes | no |
| a probe, then a visible copy of the line's point where the probe resumes | yes |

### Fix (TestPrune.Trace, Unreleased)

Where a probe precedes a conditional branch in a hidden chain that falls through from its line (before any
call, transfer or jump target), the site-probe pass gives the instruction the probe resumes at a copy of the
line's visible sequence point. Probes and ids are unchanged; only the woven PDB gains points.
`CoverageParityTests` weaves the FxTests fixture and requires every FxLib line's hits and condition-coverage
to match the untraced run.

### Measurement

The pinned fshw (alpha.71) bundles the released weaver, so `confirm` cannot measure an unreleased weaver
fix. TestPrune.Tests was therefore woven with this tree's weaver and run twice with fshw's default coverage
arguments (`--coverage --coverage-output-format cobertura`): the shadow apphost traced, and `bin/Debug`
untraced. Both runs: 1,037 tests, 0 failed.

| Report (repository files) | Lines covered / valid | Branches covered / valid | Files differing |
|---|---|---|---|
| untraced | 5,552 / 5,949 | 1,283 / 1,644 | — |
| traced, released weaver (`confirm`, alpha.71) | identical | 1,196 / 1,548 (98 points lost) | 8 |
| traced, fixed weaver | identical, hits identical | 1,279 / 1,640 (4 points lost) | 1 |

The 4 remaining points are `Orchestration.fs` lines 679 and 741 (`match selection with`): a branch under a
later hidden point, after calls, that counts unwoven because the chain's first hidden range holds no call.
The copy that restores the line's first branch makes the later range the first one. Every attempt to close
that range with a hidden point broke other lines, so the gap is recorded rather than patched. The traced
report has no `TestPrune.Trace.Recorder` package when the recorder's PDB is absent (the released recorder).
A recorder built in this tree has its PDB at its build path, and MS CodeCoverage then reports it.

| Bar | Pass when | Measured | Verdict |
|---|---|---|---|
| coverage parity | identical per-file lines and branches; no recorder module | lines identical; branches 1,640 / 1,644 (2 lines differ); no recorder module with the released recorder | fail (4 points), until a `confirm` on a release bundling the fix |

## Third run (alpha.74, 2026-09-27)

FsHotWatch CLI `0.14.0-alpha.74`, which bundles TestPrune.Trace 0.3.0: the site-probe branch-point fix and the
output capture of a failing measurement launch. Same `tests.traces`, macOS arm64, fresh workspace off `main`.
Several other agents shared the host, so each launch is listed with its load average.

### Runs

| Run | Load (start → end) | Result | TestPrune.Tests | TestPrune.Trace.Tests |
|---|---|---|---|---|
| `confirm` 1 (`1d0448d5…`) | 31.2 → 40.6 | exit 0 | 1036 passed + 1 skipped; recorded | 307 passed; refused (own recorder) |
| `confirm` 2 (`be4a9b83…`) | 23.6 → 23.2 | exit 0 | 1036 passed + 1 skipped; recorded | 307 passed; refused (own recorder) |
| `confirm` 3 (`165841b2…`) | 21.6 → 37.3 | exit 0 | 1036 passed + 1 skipped; recorded | 307 passed; refused (own recorder) |
| `confirm`, `tests.traces` removed (`21e976a7…`) | 43.7 → 29.7 | exit 0 | 1036 passed + 1 skipped | 307 passed |

`.fshw.json` was restored byte-identical after the untraced run.

| Recorded stats, TestPrune.Tests | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| executed / traced / complete | 1033 / 1033 / 942 | 1033 / 1033 / 942 | 1033 / 1033 / 942 |
| test-scope hits | 19,389,838 | 19,389,838 | 19,389,838 |
| static-init / ambient / overflow hits | 410 / 0 / 0 | 410 / 0 / 0 | 410 / 0 / 0 |
| unmapped ids, rejected dumps | 0, none | 0, none | 0, none |
| recorder CPU | 67.7 s | 61.9 s | 58.1 s |

### Bars (TestPrune.Tests; TestPrune.Trace.Tests is refused by every verb with the own-recorder reason, exit 2)

| Bar | Pass when | Measured | Load | Verdict |
|---|---|---|---|---|
| census: traced | ≥ 0.99 on 3 consecutive full runs | 1033/1033 (1.0000) on runs 1, 2 and 3 | see Runs | pass |
| census: ambient | < 0.001, or listed | 0 / 19,390,250 on each run | see Runs | pass |
| audit | `ExtraTotal = 0`; `MissingUser` explained | sampled 11, extra 0; missing only `cctor` (and 1 `gen`); no `missing user` | 29.1 → 29.0 | pass |
| overhead | CPU ratio ≤ 1.15 | 1.066 (untraced median 75.8 s, traced 80.8 s); peak RSS 1,872.9 MiB; all 6 samples exit 0 | 19.7 → 21.2 | pass |
| file-census | ratio ≤ 0.05 | 0.048 (reads-repo 21, fail-outside 20, fail-in-repo 0) | 29.0 → 19.7 | pass |
| coverage parity | identical per-file lines and branches; no recorder module | lines and every line's hit count identical in all 63 files; branches differ in 1 file (4 points, below); no recorder module in the traced report | 43.7 → 29.7 | fail (4 points, known) |
| PDB / JIT | `prepare` refused nothing on a traced project | `trace_runs.status` `recorded`, no rejected dump, on all 3 runs; the only refusal is the intended own-recorder one | — | pass |

### Coverage parity (run 3 against the untraced run)

| Report | Files | Lines covered / valid | Branches covered / valid | Differing files |
|---|---|---|---|---|
| TestPrune.Tests traced | 31 = 31 | 5,552 / 5,949 = | 1,279 / 1,640 against 1,283 / 1,644 | 1 |
| TestPrune.Trace.Tests (refused, untraced both times) | 32 = 32 | 3,160 / 3,161 = | 718 / 734 = | 0 |
| merged `coverage/coverage.cobertura.xml` | 63 = 63 | 8,712 / 9,110 = | none reported | 0 |

| File | Line | Traced | Untraced | Explanation |
|---|---|---|---|---|
| `src/TestPrune/Orchestration.fs` | 679 | hits 1, branches 2/2 | hits 1, branches 4/4 | the recorded gap after the weaver fix (`match selection with`, a branch under a later hidden point); being worked separately |
| `src/TestPrune/Orchestration.fs` | 741 | hits 1, branches 2/2 | hits 1, branches 4/4 | same |

The alpha.71 traced report lost 98 branch points in 8 files. alpha.74 loses the 4 above and nothing else. The three
traced runs wrote identical per-file reports. TestPrune.Trace.Tests reports a `TestPrune.Trace.Recorder` package
traced and untraced alike: that suite tests the recorder, and it is refused, so the package is its own code under
test, not a woven-in module.

### Other observations

| Observation | Detail |
|---|---|
| no traced launch exited nonzero | 3 traced `confirm`s, 3 traced `overhead` samples, the audit's parallel and 11 isolated launches: all exit 0. The intermittent exit 2 did not recur at load 20–40 |
| `confirm` on an unchanged tree cannot be forced to re-run | `confirm`, `invalidate` + `confirm` and `--no-cache confirm --run-once` all replay the previous verdict (`.fshw/verdict.json` and the daemon's receipt). Runs 2 and 3 were forced by stopping the daemon and moving `verdict.json` aside |
| census of an older run loses its incomplete reasons | `census --run 1d0448d5…` after run 2 prints no `incomplete` line; the same tests' traces were re-recorded by run 2, which replaced run 1's reasons. The newest run prints `child-process-untraced 14` |
| audit's parallel run has 17 `child-process-untraced` tests, the census 14 | audit: dotnet 9, sleep 5, sh 2, chmod 1; `confirm` run: 14 (second run's breakdown: dotnet 6, sleep 5, sh 2, chmod 1). The 3 extra `dotnet` children are in the audit's own launch only; not investigated |
