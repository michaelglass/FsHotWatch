# Changelog — FsHotWatch.Build

## Unreleased

## 0.7.0-alpha.9 - 2026-04-26

### Added

- **`formatSilentFailureDiagnostic`** — surfaces exit code, output size, and any `Time Elapsed` tail when `dotnet build` exits non-zero with no parseable diagnostics (typically MSBuild bailing during evaluation/restore).

### Changed

- The MSBUILDDISABLENODEREUSE env injection moved to `ProcessHelper.runProcessWithTimeout` (core), so the build plugin no longer maintains its own copy. Behavior is identical from the caller's perspective.

## 0.7.0-alpha.8 - 2026-04-25

### Fixed

- **Skip-for-test-files-only path no longer races FCS.** When `SourceChanged`
  carries only test files, the build plugin used to emit `BuildSucceeded`
  immediately. Downstream test-prune dispatched off stale `AffectedTests`
  before FCS finished checking the changed file, so partial test runs were
  silently skipped. Plugin now subscribes to `FileChecked`, transitions into
  a new `WaitingForFcsPhase` carrying the awaiting set (path-normalized via
  `Path.GetFullPath`), and emits `BuildSucceeded` only when every changed
  file has produced a `FileChecked`.

### Changed

- **BREAKING:** `BuildPhase` DU gains a `WaitingForFcsPhase` variant.
  Consumers that pattern-match `BuildPhase` must add a case for it.
- Subscriptions: build plugin now subscribes to `SubscribeFileChecked` in
  addition to `SubscribeFileChanged`.
- Timeout-handling: build failures induced by exceeding the configured
  `timeoutSec` are reported via the `ProcessOutcome.TimedOut` case
  (replacing the prior `output.StartsWith TimedOutPrefix` heuristic).
- Emit a `build failed: N errors` summary on the failure path (previously the
  failure case relied on the now-removed last-log-line summary fallback).

## 0.7.0-alpha.7 - 2026-04-23

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

## 0.1.0-alpha.3 (2026-04-02)

- Build plugin now reports parsed MSBuild diagnostics via new `BuildDiagnostics.parseMSBuildDiagnostics` — structured `ErrorEntry` items with severity, file, line, column instead of raw text

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `BuildPlugin.create` gains required `dependsOn: string list` parameter (pass `[]` for no dependencies)
- **Breaking:** `BuildState` gains `SatisfiedDeps` and `PendingFiles` fields
- Build dependency ordering — build waits for named `CommandCompleted` events before starting

### Migration from 0.1.0-alpha.1

```fsharp
// BuildPlugin.create: add dependsOn parameter
BuildPlugin.create(buildTemplate, dependsOn = [])
```

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Build plugin wrapping `dotnet build` with concurrent-build guard
