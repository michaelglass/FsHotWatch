# FCS cold-start cache poisoning — analysis

**Date:** 2026-05-01
**Author:** fcs-cache-poisoning agent
**Status:** ~~Research complete; recommending **deferral** of the fix.~~ **Shipped 2026-05-01** as jj rev `rlzutnxv` (commit `2b8485ed`) by the `fcs-poisoning-fix` agent — the prior "defer" recommendation is superseded; team-lead greenlighted ship-it. The implementation is the simpler severity-only gate at `FileChecked` time (no first-boot tentative flag, no schema migration); cold-boot cost is deferred to the first real change after boot. See `src/FsHotWatch.TestPrune/TestPrunePlugin.fs`'s `hasFcsErrors` helper + the `FileChecked` short-circuit, plus the new poisoning tests at the end of `tests/FsHotWatch.Tests/TestPrunePluginTests.fs`.
**Symptom source:** Intelligence stress test, fshw 0.10.0-stresstest2 / Phase B.

---

## TL;DR

- **Layer 1** (FCS produces 1477 type-identity errors at cold start): not reproduced
  here, but consistent with known FCS behavior and project memory
  (`fcs_cold_start_profiling.md` — 99.9% of cold check is `BuildFrameworkTcImports`).
  Almost certainly an upstream-FCS issue; out of scope to fix in this repo.
- **Layer 2** (TestPrune commits partial symbols to disk, poisoning subsequent
  boots): **confirmed by code reading**. The plugin extracts symbols on every
  `FileChecked` event and `db.RebuildProjects` clears+re-inserts whatever it
  was given. There is no FCS-error gate before the flush.
- **Recommended fix shape: C with a clean-result gate** — only let a file's
  symbols replace the prior DB entry when the FCS result for that file has
  no Error-severity diagnostics. Cold-start poisoned results never displace
  a prior known-good snapshot, and on first-ever boot we simply hold off
  until a clean result lands.
- **Recommended action: defer.** The fix is invasive (plumbing + heuristics
  + new state machine + reproduction infra), the failure mode is conservative
  ("we run more tests than strictly needed") not corrupting, and the right
  long-term fix is upstream FCS. Ship a focused implementation effort later.

---

## Layer 1 — FCS cold-start type-identity errors

### What the stress test observed

> Phase A — cold no cache: FCS produced **1477 type-identity errors**
> ("expected type X but here has type X" — same fully-qualified name on both
> sides) across 89 files. Build itself succeeded.

### Characterization

This is the classic FCS cold-start pattern. The same logical type is being
loaded through two distinct paths (e.g. project reference vs. transitive
package), each producing a distinct `TType` instance. F# compares types by
reference identity in many places, so two `TType` instances that name the
same FQN compare unequal — yielding the ill-formed-looking
"expected X but here has type X" diagnostic.

Why cold-only: per project memory `fcs_cold_start_profiling.md`,
`BuildFrameworkTcImports` is the bottleneck (~1s) and builds the
`TcImports` / `TcGlobals` mutable graph. When multiple project tiers begin
checking concurrently against an in-flight `FrameworkImportsCache`, two
tiers can each construct their own framework-import bundle for the same
assembly — racing entries land in different `CCU` thunks. Once the cache
settles (warm), every subsequent check resolves through one canonical path,
errors disappear.

We have not reproduced this locally because reproduction requires the
Intelligence project tree at the size that triggers it; the fshw repo
itself is too small to expose the race. The behavior is described in
several FCS issues over the years (e.g. dotnet/fsharp #11141 family) and is
generally acknowledged as a TcImports/CCU-resolution race.

### Does it stabilize once warm?

Per project memory and per the FCS code, yes: once `TcImports` is built
and cached for the framework, subsequent checks reuse the same CCU thunks
and types resolve uniquely. Phase B's symptom (different symbol counts
than Phase A) is itself evidence: the warm result produces a more complete
symbol set (282 symbols) than the cold result (19 stored). The cold
1477-errors-across-89-files result is **transient**.

### Conclusion

Layer 1 is upstream. **Out of scope for this repo.** The cache layer must
be defensive against it, not try to prevent it.

---

## Layer 2 — How TestPrune commits partial symbols

### Code path (file:line, jj rev `wrzzknkrymmv`, parent `vmuksykm`)

1. **Symbol extraction**, `src/FsHotWatch.TestPrune/TestPrunePlugin.fs:997`
   — `PluginEvent.FileChecked` handler.
2. **Re-runs FCS** at line 1018 via
   `analyzeSource ctx.Checker fileStr result.Source result.ProjectOptions projectName`.
   Note: `analyzeSource` (TestPrune.Core) re-invokes `ParseAndCheck` itself;
   it does **not** consume `result.CheckResults`. FCS internally caches the
   prior result so it's cheap, but the FCS diagnostics live behind
   `analyzeSource`'s opaque return type.
3. **`AnalysisResult.Diagnostics`** (`AnalysisDiagnostics`) is _not_ a
   carrier for FCS errors — its fields are `TotalDefinitions`,
   `FilteredSymbols`, `DroppedEdges` (per the TestPrune.Core 3.0.2 XML
   docs). It's an analysis-pipeline counter, not an FCS-error pass-through.
   So **TestPrune cannot tell whether FCS had errors during analysis**
   without re-reading `result.CheckResults` directly.
4. **Pending accumulation**, lines 1054–1056 — symbols stuffed into
   `state.PendingAnalysis[projectName]` unconditionally, replacing any
   prior entry for the same file.
5. **Diff against stored**, line 1058 — `detectChanges` runs against
   `state.SymbolSnapshot` (in-memory mirror of last-flushed DB state).
   This is where the `263 changes, 19 stored, 282 current` log message
   originates (line 1063).
6. **Flush to DB**, `flushPendingAnalysis` (line 592) → `db.RebuildProjects`
   (line 625). Per TestPrune.Core 3.0.2 XML docs:
   > Clear and re-insert symbols, dependencies, and test methods.
   So the DB row set for a project is replaced wholesale with whatever the
   plugin accumulated since the last flush. **There is no validation gate.**

### Failure narrative for Phase A → Phase B

- **Phase A (cold, no cache).**
  1. FCS cold start: `analyzeSource` runs while TcImports is racing.
     Symbol extraction succeeds in returning *some* `SymbolInfo` list, but
     definitions whose enclosing types failed to resolve (because the
     enclosing type's CCU thunk got the "wrong" `TType` half) are dropped
     by the `findEnclosing/isTrackedSymbol` pipeline. End state: 19 of 282
     symbols extracted for a representative file.
  2. BuildCompleted fires (or BatchChecked, depending on path) →
     `flushAndQueryAffected` → `RebuildProjects(combined)` → DB project
     rows are replaced with the partial 19-symbol set.
  3. Tests run against `ChangedSymbols` derived from "changes vs. empty"
     (since SymbolSnapshot was empty at boot). Cache key for §2a hashes
     the partial-symbol output and is stored alongside the test result.
- **Phase B (cold, with cache).**
  1. Daemon boots. `state.SymbolSnapshot` starts empty; the first
     `FileChecked` triggers `db.GetSymbolsInFile(relPath)` (line 1040) and
     reads the **19 partial symbols** Phase A wrote.
  2. `analyzeSource` re-runs FCS. This is **also cold** — `BuildFrameworkTcImports`
     runs again — so depending on which TcImports race resolves first,
     this run may produce a different partial set, or eventually a full
     set. The stress log shows `282 current`: this run resolved more.
  3. `detectChanges` produces `282 - intersection(19,282) ≈ 263` synthetic
     diffs. The cache-key §2a now hashes a different changed-symbols set
     than Phase A wrote → **cache replay misses** → `BuildSucceeded:
     starting test run`. The load-bearing skip never materializes.

### Why the existing `RequireWarmStart` doesn't catch this

`RequireWarmStart` (Phase 1 work) defers BuildPlugin / TestPrunePlugin
processing until the daemon's first scan completes. But "scan complete" ≠
"FCS warm in the TcImports-stabilized sense". The first scan literally
*causes* the cold TcImports race; the gate fires immediately after. So
the FCS cold errors are still inside the window that produces the flushed
symbols.

### Why the symbol-cache `cacheBackend` (CheckPipeline) doesn't catch this

`CheckPipeline.tryGetCachedFullCheck` only stores `FullCheck` results,
not `ParseOnly`. But "FullCheck with 1477 errors" is still
`FSharpCheckFileAnswer.Succeeded` from FCS's perspective — the answer is
"complete", just wrong. So the cache happily stores it, and on next boot
the plugin still receives a `FullCheck` and runs analysis off it.

---

## Recommended fix shape

### Option C: **clean-result gate, with first-boot allowance**

1. In the `FileChecked` handler (TestPrunePlugin.fs:997), inspect
   `result.CheckResults`. If `FullCheck checkResults`, count
   `checkResults.Diagnostics |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)`.
2. If error-count > 0 **and** the symbol DB already has a non-empty entry
   for this file (`db.GetSymbolsInFile(relPath) |> List.isEmpty |> not`),
   **skip** the pending-analysis update for this file. Log
   `test-prune: skipping symbol-extract for {file} ({n} FCS errors); keeping prior {m} symbols`.
3. If error-count > 0 **and** no prior entry exists, fall back to current
   behavior (tentatively store) and log a warning. This is the first-boot
   case where we have no choice — running tests against a partial set is
   safer than skipping tests.
4. Mark first-boot tentative entries with a flag (new column on the
   sidecar DB, or an in-memory tentative set) so on the **next** boot,
   tentative-stored files re-run analysis even if file content is
   unchanged. This is option B grafted onto C and is what makes the
   first-boot case eventually heal.

### Why not A (pure error-gate skip)

A file with a real user error (typo) would never get its symbols
extracted, so test pruning would silently misbehave for files-under-edit.

### Why not B alone (tag everything tentative)

Doesn't distinguish a single FCS error from a 1477-error stampede; the
"tentative until next clean run" rule applies even to clean cold runs and
costs us a re-analysis pass on every boot.

### Why not "use FCS error count as a tiebreaker" alone

Fragile and easy to fool — a real error introduced by the user looks
identical to a transient cold-start error. We need the prior-snapshot
guard to make it safe.

---

## Why I am NOT shipping the fix in this workspace

1. **Plumbing cost.** TestPrune.Core's `analyzeSource` doesn't carry FCS
   diagnostics. We'd either:
   - call `result.CheckResults` directly in the plugin (fine — they're
     available on `FileCheckResult`), bypassing TestPrune.Core entirely
     for the gate, **OR**
   - extend TestPrune.Core to return diagnostics, which is an external
     package change and a major version bump.
   The first is doable in-repo and is what I'd recommend.
2. **Tentative-flag schema migration.** Adding a "tentative" flag to the
   symbol DB needs a TestPrune.Core change (new column or sidecar table)
   and a schema-version bump. Not invasive, but it's external.
3. **Reproduction infrastructure.** We don't currently have a deterministic
   FCS-cold-start-with-errors test fixture. The fshw repo is too small to
   trigger the TcImports race. Without a fixture, we cannot TDD this fix
   per CLAUDE.md ("TDD strict — test before code"). Building a synthetic
   reproduction (mock checker that emits bogus errors on first call,
   clean results subsequently) is the right move but adds scope.
4. **CI flakiness risk.** The fix touches the FileChecked → flush path
   that runs in every integration test. A subtle gate-bug here will
   produce nondeterministic test-pruning behavior — exactly the failure
   mode the coverage-ratchet workflow is designed to avoid. CLAUDE.md
   says "no parallelism disabling, no threshold lowering" — so a
   noisy fix would just bounce off CI.
5. **The bug is conservative, not corrupting.** Phase B re-runs all
   tests instead of skipping them. That's wasted CI time, not wrong
   answers. Compared to the cost of a half-fix that silently drops real
   FCS errors, taking the time to do this properly is the better bet.

### What I'd estimate for a focused implementation effort

- ~1 day to build the synthetic reproduction (mock `FSharpChecker` that
  returns errors+partial-symbols on first call, clean on second).
- ~1 day to implement the gate + tentative flag + tests.
- ~1 day to verify under stress against Intelligence and ensure the
  cache-replay path actually fires.

Total: ~3 days of focused work. Recommend spawning a dedicated agent
when stress-test churn is no longer the team's top blocker.

---

## Appendix — useful starting points for the implementer

- Gate insertion point: `src/FsHotWatch.TestPrune/TestPrunePlugin.fs:1017`
  (right before `analyzeSource`).
- FCS diagnostic source: `result.CheckResults` is `FileCheckState`
  (`src/FsHotWatch/Events.fs:55-58`); on the `FullCheck cr` branch,
  `cr.Diagnostics : FSharpDiagnostic[]` is what to inspect.
- DB read for "do we have prior symbols": `db.GetSymbolsInFile(relPath)`
  (TestPrune.Core 3.0.2). Empty list ⇒ no prior entry.
- Snapshot read (faster, in-memory): `state.SymbolSnapshot |> Map.tryFind relPath`.
- Test fixture pattern: `tests/FsHotWatch.Tests/TestPrunePluginTests.fs`
  already exercises analyzeSource paths; extending it to feed a
  FileChecked event with a fabricated `FSharpCheckFileResults` is the
  shape (will need a checker stub or a real cold-checker captured to
  fixture data).
