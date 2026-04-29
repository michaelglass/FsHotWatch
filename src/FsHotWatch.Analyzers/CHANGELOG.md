# Changelog — FsHotWatch.Analyzers

## Unreleased

### Added

- **`failOnSeverity` parameter** on `AnalyzersPlugin.create` — promotes analyzer diagnostics at or above the given severity to error. Default: `Hint` (everything is fail-worthy). Configurable via `analyzers.failOnSeverity` in `.fshw.json` (parsed via `FsHotWatch.ErrorLedger.DiagnosticSeverity.fromString`); unknown strings are warned and ignored.
- **`§2a` content-merkle cache key.** AnalyzersPlugin's cache key is now a `merkleCacheKey` of `(plugin-version, analyzer-paths, file, source, fcs-signature)` — fully content-derived, independent of jj commit_id. Cross-file changes invalidate downstream caches via the `fcs-signature` slot (see core's `CheckCache.fcsCheckSignature`).
- **Synchronous analysis on terminal status.** Analysis now awaits inline so the framework's per-event capture window records `Errors` and `EmittedEvents` for §2a cache replay. Previously a fire-and-forget `Async.Start` race produced empty cache entries.

### Changed

- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed. New positional order: `analyzerPaths → timeoutSec → failOnSeverity`.
- `promoteIfFailing` simplified with an early-return; unknown `failOnSeverity` strings now log a warning instead of silently defaulting.

## 0.7.0-alpha.10 - 2026-04-26

### Changed

- Per-file error reporting now goes through `PluginCtxHelpers.reportOrClearFile` (core). No behavior change.

## 0.7.0-alpha.9 - 2026-04-25

### Added

- Per-event timeout. `create` accepts a new `timeoutSec: int option`;
  when analyzer work for a `FileChecked` event exceeds the timeout the run
  is recorded as `TimedOut` and the plugin continues with the next event.
  Timeouts are advisory — the orphan `client.RunAnalyzersSafely` call is
  not cancelled, only the result is discarded.

### Changed

- Emit a `"primary"` subtask with a descriptive label per `FileChecked` event,
  and a richer terminal summary of the form
  `analyzed N files, F findings (E errors, W warnings)`.

## 0.7.0-alpha.8 - 2026-04-23

- chore: bump upstream tool versions

## 0.7.0-alpha.7 (2026-04-22)

### Changed

- Extracted two pure helpers from `createCliContext` to enable deterministic
  unit tests for branches that the live-SDK integration tests used to hit
  nondeterministically:
  - `isKnownNonAnalyzerPrefix` — filter for the analyzer DLL exclusion
    list, lifted out of the `ExcludeFilter` closure.
  - `buildAnalyzerProjectOptions` — SDK-reflection that builds the
    `AnalyzerProjectOptions` instance, testable with `None` / throwing-ctor
    fixtures instead of requiring a real loaded SDK.
- `InternalsVisibleTo FsHotWatch.Tests` added so the unit tests can reach
  these helpers without bloating the package's public API.

---

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Run parse-only analyzers instead of skipping files without full type-check results — passes `null` for `checkResults` in `CliContext`, enabling syntax-only analyzers to run on all files

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Fix per-platform coverage threshold for `AnalyzersPlugin.fs` — macOS reports 83% branch coverage vs Linux 66% due to reflection-based `CliContext` branches
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- F# analyzer host using reflection-based `CliContext` (FCS version mismatch workaround)
