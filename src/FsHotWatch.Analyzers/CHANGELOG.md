# Changelog — FsHotWatch.Analyzers

## Unreleased

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
