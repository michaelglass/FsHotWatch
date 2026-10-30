# Developing TestPrune

F# test impact analysis. Uses FSharp.Compiler.Service to build a symbol dependency graph
from AST analysis, then determines which tests are affected by code changes.

TestPrune lived in its own repository, michaelglass/TestPrune, until 2026-10-30. It now lives
here, with its history (AUTOMATION-1367): the import rewrote its commit ids, and
`docs/testprune/filter-repo-commit-map.txt` maps each original id to its id here. The old
repository is frozen.

## Build & test

TestPrune builds as part of this repository's solution (`FsHotWatch.slnx`);
`TestPrune.slnf` filters it down to the TestPrune projects. Its tests are in `.fshw.json`'s
`tests.projects`, so the repository gate (`mise run ci`: `confirm` built from this source)
runs them. TestPrune is no longer gated by a pinned, released `fshw`.

```bash
mise run compile             # the whole solution, TestPrune included
mise run tp-test             # TestPrune's test projects with coverage (coverage-tp/<project>/)
mise run tp-coverage-check   # TestPrune's per-file coverage floors
mise run tp-coverage-ratchet # tighten them to current values (explicit, never a gate)
mise run tp-example          # run test-prune against the example solution
mise run tp-example-build    # build the example solution
mise run tp-dead-code        # find unreachable production code
mise run tp-dead-code-tests  # find unreachable test code
mise run tp-bench            # benchmarks with dotnet-trace profiling
mise run tp-bench-raw        # benchmarks without profiling (JSON metrics)
```

## Project structure

- `src/TestPrune.Core/` — core library: AST analysis, SQLite graph, symbol diffing, impact selection
- `src/TestPrune/` — CLI tool (index, run, status, dead-code)
- `src/TestPrune.Falco/` — Falco route-based integration test filtering extension
- `src/TestPrune.Sql/`, `src/TestPrune.SqlHydra/` — SQL coupling extensions
- `src/TestPrune.Trace/`, `src/TestPrune.Trace.Recorder/`, `src/TestPrune.Trace.Cli/` —
  recorded per-test traces (preview, record only): weaver and trace store, in-process
  recorder, `test-prune-traces` measurement verbs
- `src/TestPrune.Analyzers/` — TestPrune's own analyzer rules, loaded by the gate
  (`.fshw.json` `analyzers.paths`)
- `src/TestPrune.Attributes/` — marker attributes (unpublished; consumers match them by name)
- `tests/TestPrune.Tests/`, `tests/TestPrune.Trace.Tests/` — tests (xUnit v3 + MTP v2)
- `tests/TraceFixtures/` — fixture solution the weaver tests build and run
- `examples/SampleSolution/` — example F# solution for smoke testing (own `global.json`)
- `benchmarks/TestPrune.Benchmarks/` — indexing benchmarks

## Conventions

- F# formatted with Fantomas, 4-space indentation
- VCS: jj (Jujutsu), not git
- Package versions for every project come from `Directory.Packages.props` (Central Package
  Management, transitive pinning on). FSharp.Core and FCS are EXACT pins, never floats:
  a packed nuspec floor is what restore resolved, so a float leaks the newest release into
  consumers' floors (AUTOMATION-1358). `mise run package-floors` checks real pack output, and
  `mise run ci` and CI's lint step run it.
- Coverage settings are per test project (`testconfig.json`), so CI's reusable workflow and
  `tp-test` measure the same thing

## Package publishing

The TestPrune packages are released by this repository's tagger (`semantic-tagger.json`). Their
tag prefixes were renamed on import so they cannot collide with FsHotWatch's own `core-v*` and
`analyzers-v*` lines:

- `tp-core-v*` — TestPrune.Core + the `test-prune` CLI
- `tp-falco-v*` — TestPrune.Falco
- `tp-analyzers-v*` — TestPrune.Analyzers
- `tp-sql-v*` — TestPrune.Sql
- `tp-sqlhydra-v*` — TestPrune.SqlHydra
- `tp-trace-v*` — TestPrune.Trace + TestPrune.Trace.Recorder + TestPrune.Trace.Cli (one tag:
  the weaver calls recorder methods by name, so the three never ship apart)

`mise run release` releases every package; the tagger orders publication by the
ProjectReference DAG and waits for each wave on NuGet before tagging the next.

### Release train

The TestPrune packages ride this repository's release train (`AGENTS.md`, "Release train"):
at most one release a day, batching that day's merges, cut only by the maintainer or the
agent assigned to this repository, and only from a `main` whose GitHub CI is green on the
exact commit. TestPrune and FsHotWatch now release in the same `mise run release`, so the
old "cut TestPrune before that day's FsHotWatch train" ordering is the tagger's wave order.
