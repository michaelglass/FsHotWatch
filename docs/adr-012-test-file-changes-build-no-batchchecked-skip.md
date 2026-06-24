# ADR-012: Test-file changes run a real build — the `BatchChecked` skip was a stale-binary false-green

Status: Accepted (2026-06-24)

Amends ADR-002 ("`BatchChecked` event added; `RequireWarmStart` workaround
removed"), which introduced `BuildPlugin.WaitingForBatchPhase`. This ADR removes
that phase.

## Context

ADR-002 added a build-plugin optimization: when a file-change batch touched
**only test files**, the build plugin skipped MSBuild and instead parked in
`WaitingForBatchPhase`, waiting for FCS's in-memory `BatchChecked` cohort signal
to confirm the changed `.fs` files type-check, then emitted `BuildSucceeded`. The
stated intent was "don't rebuild for a test-only change; wait for FCS to confirm
the N changed test files type-check."

The unstated (and false) assumption: **a test-file-only change needs no on-disk
artifact to change.** That holds only if nothing downstream executes the test
project's compiled output. But `TestPrunePlugin` runs each test project with:

```
dotnet run --project <testProj> --no-build -- ...
```

`--no-build` executes the **on-disk** assembly. For an xUnit v3 standalone-exe
test project, only **MSBuild** emits that runnable DLL — FCS type-checking the
edited `.fs` does **not**. So the chain was:

1. Edit a test file only.
2. Build plugin: `Skipping build — only test files changed; waiting for BatchChecked`.
3. FCS type-checks the edit → `BatchChecked` fires → build plugin emits
   `BuildSucceeded` **without re-emitting the DLL**.
4. TestPrune: `BuildSucceeded: starting test run` → `dotnet run … --no-build` runs
   the **stale** DLL → **false green**.

This violated the build plugin's own documented contract (BuildPlugin.fs:
"BuildSucceeded means every project's DLL is up-to-date with its sources").

The post-build freshness guard `verifyArtifactsFresh` (DLL-mtime vs newest-source
mtime, ADR-008's temporal complement) — the exact check that would have caught a
stale DLL — ran **only** on the real-build path (`startBuild` / `startTemplateBuild`
→ `verifyAndDemote`). The test-only skip bypassed it entirely.

### Observed downstream (thellma/intelligence)

Editing only `RawSqlTests.fs` then `dotnet fshw test-rerun --filter-class
'*DbSpanLocationTests'` reported pass / exit 0 against a stale test DLL. A forced
`dotnet build` then made the **same** test fail deterministically; the test DLL's
mtime only advanced after the manual build. Daemon log, in order:
`Skipping build — only test files changed` → `BuildSucceeded` → `dotnet run …
--no-build`.

## Decision

**Remove the test-only build skip. Every source change — test files included —
runs the real build.** `BuildPlugin.WaitingForBatchPhase` is deleted; `BuildPhase`
collapses to a single `IdlePhase` case. The build plugin no longer subscribes to
`BatchChecked`.

`handleSourceChanged` now unconditionally dispatches to `startBuild` /
`startTemplateBuild` for any `SourceChanged`, so:

- MSBuild re-emits the test project's DLL **before** `BuildSucceeded` fires.
- `verifyArtifactsFresh` runs on that build and demotes to `BuildArtifactsStale`
  if the DLL is older than its sources — so `BuildSucceeded` again means "every
  DLL is fresh", and TestPrune's `--no-build` run can only ever execute an
  up-to-date binary.

### Why this stays fast

A test-file edit was never free under the cache anyway: the build-input merkle
(`BuildInputsHasher`) content-hashes **every** source file, test files included,
so a test-file edit already moved the cache key → cache miss → a real build. The
skip was an *extra* optimization layered on top of the cache to avoid even the
incremental MSBuild no-op. MSBuild's own incremental engine relinks only the one
touched project; for the repo sizes fshw targets that cost is dominated by the
test run it gates. Correctness (never executing a stale test DLL) outranks the
sub-second the skip bought — the same tradeoff ADR-002 → ADR-008 already made for
the merkle.

### Why the FCS race ADR-002 worried about does not return

ADR-002 justified the skip partly as "otherwise downstream test-prune dispatch
would race FCS and read stale `AffectedTests`." That race is handled
**independently** by `TestPrunePlugin`'s own `BatchChecked` subscription (the
cohort-complete *seal* that re-publishes `changedSymbolsRef`), which this change
does **not** touch. Going through the real build is, if anything, safer: MSBuild
takes longer than FCS, so `BuildSucceeded` now arrives strictly **after** the
`BatchChecked` seal has flushed `AffectedTests` — the mailbox is FIFO and the
daemon emits `BatchChecked` after the last `FileChecked` of the cohort.

## Consequences

- `BuildPhase` is a single-case union (`IdlePhase`); the build state machine has
  one path for all source changes. Impossible to emit `BuildSucceeded` for a
  test-file change without re-emitting + verifying the DLL.
- The build plugin drops `SubscribeBatchChecked` and the `BatchChecked → None`
  cache-key special case. `TestPrunePlugin` keeps its `BatchChecked` subscription.
- Deleted: `WaitingForBatchPhase`, its `BatchChecked`/`SourceChanged`/`ProjectChanged`
  match arms, and the merge-into-`expecting` logic.
- One incremental MSBuild build per test-file edit (previously skipped). The
  common dev-loop case — repeatedly editing a test and rerunning it — now always
  runs the freshly-compiled assembly.

## Related

- Amends: `docs/adr-002-batchchecked-event-and-requirewarmstart-removal.md`.
- Builds on: `docs/adr-008-mtime-is-not-a-content-oracle.md` (the
  `verifyArtifactsFresh` temporal guard that now gates every build).
- Source: `src/FsHotWatch.Build/BuildPlugin.fs` (`handleSourceChanged`, `BuildPhase`).
- Regression test: `tests/FsHotWatch.Tests/BuildPluginTests.fs`
  ("test-file-only change runs a real build …").
