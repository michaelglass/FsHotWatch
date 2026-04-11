# Changelog — FsHotWatch.TestPrune

## Unreleased

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
