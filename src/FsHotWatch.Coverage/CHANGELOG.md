# Changelog — FsHotWatch.Coverage

## Unreleased

## 0.7.0-alpha.7 (2026-04-22)

### Fixed

- Cache key now includes a tristate salt derived from the thresholds file
  (`absent` / `err:<exception-kind>` / content SHA-256). Previously the key was
  commit-id-only, so editing `coverage-ratchet.json` (e.g. via
  `coverageratchet loosen`) under the same commit silently replayed the stale
  cached plugin status. Splitting unreadable-file errors from absent-file cases
  also means a transient IO hiccup no longer produces the same cache key as "no
  thresholds file at all".

---

## 0.7.0-alpha.3 (2026-04-18)

### Changed

- `afterCheck` hook now returns `bool * string` instead of `unit` — non-zero exit codes report failures to the error ledger under `<coverage>`

---

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

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Coverage threshold checking from Cobertura XML reports
