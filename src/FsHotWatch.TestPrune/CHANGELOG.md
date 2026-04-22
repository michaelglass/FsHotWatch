# Changelog — FsHotWatch.TestPrune

## Unreleased

- chore: bump upstream tool versions

## 0.7.0-alpha.8 (2026-04-22)

### Changed

- **BREAKING**: Bump `TestPrune.Core` 2.0.0 → 3.0.2. Adopts the revised
  `ITestPruneExtension` interface: extensions now implement `AnalyzeEdges`
  (returning `Dependency list` to inject into the graph) rather than
  `FindAffectedTests`. Extension-contributed edges are written to the DB
  via `RebuildProjects` before `QueryAffectedTests` so impact traversal
  unifies AST-based and extension-based dependencies in a single pass.
  3.0.2 also closes the pre-versioning stale-DB hole (`openCheckedConnection`
  now recreates any DB where `user_version` reads 0 *and* user tables
  already exist), so combined with the plugin-side stuck-state fix below
  the schema-drift hang is prevented at both layers.
- `AnalysisResult` construction now passes `Attributes` through from the
  analyzer (new field in `TestPrune.Core` schema v3).

### Fixed

- **Stuck-state bug**: the synchronous `flushAndQueryAffected` call sites in
  `BuildCompleted` and `TestsFinished (RerunQueued)` ran outside the async
  try/with and had no net, so a DB hiccup would leave the plugin permanently
  pinned in `Running` with no work dispatched. Both branches now wrap the
  flush in a try/with that reports `PluginStatus.Failed`, transitions back
  to `TestsIdle`, and leaves the plugin responsive to the next event.
- **Schema-drift self-heal**: when a flush fails with SQLite "no such column"
  / "no column named" (stale cache DB from a previous `TestPrune.Core` schema
  version), the plugin deletes the offending DB file and logs a warning. The
  next run rebuilds from scratch — the cache is derivative and safe to
  regenerate. The caller no longer has to know which file to `rm`.
- `affected-tests` command now updates on every `FileChecked` event
  rather than waiting for the next `BuildCompleted`. Each file check
  re-queries `QueryAffectedTests` against the currently-persisted DB
  state so consumers can observe impact changes incrementally. Fix
  depends on `TestPrune.Core 3.0.0`'s UPSERT row-id preservation and
  post-commit WAL checkpoint.

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Bump `TestPrune.Core` 1.0.1 → 2.0.0 — adds cross-project extern symbol support via `projectName` parameter in `analyzeSource`
- `buildFilterArgs` changed from private to internal for testability
- Add `InternalsVisibleTo` for FsHotWatch.Tests

### Fixed

- Comment-only source changes no longer add the file to `ChangedFiles` — only genuine AST changes (non-empty `changedNames`) propagate to extension-based tests (e.g. Falco route matching)

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)

---

## 0.1.0-alpha.2 (2026-03-28)

- Fix: move `ReportStatus` to `TestsFinished` handler to eliminate race condition

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Test impact analysis via symbol dependency graph
- Test execution with configurable test project configs
