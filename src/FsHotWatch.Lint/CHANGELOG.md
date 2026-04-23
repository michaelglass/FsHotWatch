# Changelog — FsHotWatch.Lint

## Unreleased

## 0.7.0-alpha.7 - 2026-04-23

- chore: bump upstream tool versions

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Lint runner injectable via `lintRunner` parameter on `create` for testability — allows tests to run without FSharpLint config

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.3 (2026-04-02)

- Guard against concurrent lint operations

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- FSharpLint integration using warm FCS parse results via `lintParsedSource`
