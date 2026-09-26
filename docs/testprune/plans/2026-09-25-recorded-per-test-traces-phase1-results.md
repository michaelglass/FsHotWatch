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
