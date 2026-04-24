# Changelog — FsHotWatch.FileCommand

## Unreleased

### Added

- Set `FSHW_RAN_FULL_SUITE=true|false` on every `afterTests` command. `"true"`
  iff every project in the test run executed without an impact filter; use it
  to gate baseline refreshes or threshold tightening.

### Changed

- Emit distinct success/failure/crashed summary strings
  (`<name>: succeeded` / `<name>: failed` / `<name>: crashed`) instead of
  the ambiguous `ran <name>` for every outcome.

## 0.7.0-alpha.7 - 2026-04-23

### Added
- `afterTests` trigger — fileCommand entries can now run after a test run completes, optionally filtered by a list of test project names.
- Required `name` field for entries whose primary trigger is `afterTests`.

### Changed
- **BREAKING:** `FileCommandPlugin.create` now takes a `CommandTrigger` record (`FilePattern` + `AfterTests`) instead of `fileFilter` + `runOnStart`. Callers registering directly against the plugin API must migrate.
- `afterTests` list-form semantics: the plugin fires iff **every** listed project is present (previously "any"). Combined with TestPrune's progressive per-group emission, this means the command fires exactly once per run — on the first `TestProgress` whose cumulative accumulator covers every listed project, OR on `TestRunCompleted` (whichever arrives first, relevant for cache replay) — so a hanging non-listed project (e.g. integration tests) never blocks it.
- **BREAKING:** Subscriptions updated for the new event lifecycle — plugins with `AfterTests.IsSome` now subscribe to `SubscribeTestProgress` and `SubscribeTestRunCompleted` (not the removed `SubscribeTestCompleted`). Dedup is now keyed on `RunId` via a `LastFiredRunId` sentinel; the previous `LastAfterTestsKey` superset heuristic is gone — it had a correctness bug whereby identical-project-set runs after the first silently skipped.

### Fixed
- Idempotency across back-to-back runs with identical project sets. Previously the "is this a fresh batch?" check compared incoming project sets against the last-fired key via `Set.isSubset`; when two consecutive runs produced the same final set (the dominant case for stable configs), the second was treated as a continuation of the first and never fired. Now keyed on `RunId` — distinct runs always re-fire.

### Removed
- `runOnStart` config/API field. Use an explicit trigger (e.g. `afterTests: true`) or schedule initialization another way.

- chore: bump upstream tool versions

## 0.5.0-alpha.1 (2026-04-12)

*No changes since 0.3.0-alpha.1.*

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; enable `TreatWarningsAsErrors`; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `FileCommandPlugin.create` gains required `runOnStart: bool` parameter (pass `false` for previous behavior)
- FileCommandPlugin now emits `CommandCompleted` events after each run

### Migration from 0.1.0-alpha.1

```fsharp
// FileCommandPlugin.create: add runOnStart parameter
FileCommandPlugin.create(pattern, command, args, runOnStart = false)
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Run arbitrary shell commands when specific file patterns change
