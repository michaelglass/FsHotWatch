# Changelog — FsHotWatch.Fantomas

## Unreleased

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments


## 0.7.0-alpha.16 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context


## 0.7.0-alpha.15 - 2026-07-15

- fix: `FormatPreprocessor` gains the same `slowHook` test seam its twin
  (`createFormatCheckWithSlowHook`) already had. Its timeout test could previously only
  RACE the timer against Fantomas (`timeoutSec = 0`) — on a warm, idle box the format won,
  the file was rewritten, and the "leaves the file alone on timeout" test failed while
  asserting nothing about the timeout path. The seam makes the `WorkTimedOut` branch
  deterministic.

- fix!: adapt to verdict-carrying terminals (AUTOMATION-99): `Failed` statuses carry a
  `RunVerdict` (summary + measured elapsed), and the deleted `CompleteWithSummary`
  side-channel is replaced by the verdict the terminal itself carries.

- fix!: adapt to core `RunVerdict` (AUTOMATION-99): format-check completions carry
  the format summary plus the measured check duration.

- fix!: **`FormatPreprocessor` ran Fantomas with NO timeout** while its twin
  `createFormatCheck` wrapped the IDENTICAL call in `runWithCancellableTimeout` —
  one bounded, one not, and the unbounded one on the worse path. A preprocessor
  runs inside the daemon's `processBatch`, so a Fantomas hang there wedges the
  **change agent**: the daemon silently stops processing file changes, forever.
  It also runs inside `performScan`, which `WaitForScan` (`check`'s first step)
  blocks on. It is now bounded by the same `timeoutSec` the read-only twin
  already honoured, driven under the timeout's cancellation token; a timed-out
  file is left unformatted and logged, never half-written.
  - `FormatPreprocessor` takes an optional `timeoutSec` (default 60s, matching
    `FormatTimeoutDefaultSec`). `FormatPreprocessor()` still compiles.

## 0.7.0-alpha.14 - 2026-06-24

- chore(deps): dependency refresh (version-coupled release; no functional change).

## 0.7.0-alpha.13 - 2026-06-17

- docs: README accuracy & early-alpha status-note pass.

## 0.7.0-alpha.12 - 2026-06-09

- chore: refresh transitive dependencies (CommandTree 0.5.1, CoverageRatchet.Core 0.1.0-alpha.2, TestPrune.Core 4.0.2, FSharpLintAnalyzerShim 0.3.0-alpha.3 via the lint shim).

## 0.7.0-alpha.10 - 2026-04-29

### Changed

- **BREAKING:** `createFormatCheck` no longer takes `getCommitId`. New signature: `createFormatCheck (timeoutSec: int option)`. The cache key migrated from jj commit_id to a content-merkle of `(file path, file source)` for each file in the FileChanged event — Fantomas formatting is content-deterministic, so two daemons agree on cache values regardless of working-copy state.

## 0.7.0-alpha.9 - 2026-04-26

### Changed

- Per-file error reporting now goes through `PluginCtxHelpers.reportOrClearFile` (core). No behavior change.

## 0.7.0-alpha.8 - 2026-04-25

### Added

- Per-event timeout on `createFormatCheck`. A new `timeoutSec: int option`
  parameter bounds the wall-clock time for a single `FileChanged` batch's
  format check. When `CodeFormatter.FormatDocumentAsync` exceeds the
  timeout the run is recorded as `TimedOut` and the plugin continues with
  the next event. Timeouts are advisory — the orphan Fantomas task is not
  cancelled, only its result is discarded.

### Changed

- FormatCheckPlugin emits a `"primary"` subtask (`checking format of N files`)
  and a distinct terminal summary (`format OK` / `N files need formatting`).

## 0.7.0-alpha.7 - 2026-04-23

- chore: bump upstream tool versions

## 0.7.0-alpha.3 (2026-04-18)

### Added

- `FormatPreprocessor` and `createFormatCheck` respect `.gitignore` and `.fantomasignore` — files matching either are skipped during format and format-check
- Ignore rules cached per repo root via `IgnoreFilterCache`, auto-reloaded when ignore files change on disk

---

## 0.5.0-alpha.1 (2026-04-12)

*No changes since 0.3.0-alpha.1.*

---

## 0.3.0-alpha.1 (2026-04-08)

### Bug fixes

- Fix `format-check` plugin not reporting errors to the ErrorLedger — unformatted files now appear in `fs-hot-watch errors` output and are cleared when the file is fixed

### Infrastructure / CI

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Format preprocessor (rewrites files before events dispatch)
- Format check plugin (read-only validation)
