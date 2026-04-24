# Changelog — FsHotWatch.Fantomas

## Unreleased

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
