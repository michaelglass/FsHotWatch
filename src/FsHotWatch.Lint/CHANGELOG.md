# Changelog — FsHotWatch.Lint

## Unreleased

## 0.7.0-alpha.17 - 2026-08-03

- chore(deps): update dev-tools + external dependencies


## 0.7.0-alpha.16 - 2026-07-15

- fix!: adapt to verdict-carrying terminals (AUTOMATION-99): `Failed` statuses carry a
  `RunVerdict` (summary + measured elapsed), and the deleted `CompleteWithSummary`
  side-channel is replaced by the verdict the terminal itself carries.

- fix!: adapt to core `RunVerdict` (AUTOMATION-99): completions carry the lint
  summary plus the measured per-file lint duration.

## 0.7.0-alpha.15 - 2026-07-02

- fix: skip compile items that resolve outside the repo root (NuGet-injected `_content`, e.g. xunit.v3's `DefaultRunnerReporters.fs`) — compiled in, but not ours to lint, and a latent FSharpLint-crash surface (AUTOMATION-49). `create` now takes a leading `repoRoot: string option` (Some → skip out-of-repo; None → include them, via the CLI's `includeOutsideRepo` override).

## 0.7.0-alpha.14 - 2026-06-24

- chore(deps): bump the bundled `FSharpLintAnalyzerShim` to 0.3.0-alpha.5 (FCS 43.12 alignment — resolves the FL0000 host-incompatibility caveat).

## 0.7.0-alpha.13 - 2026-06-17

- docs: README accuracy & early-alpha status-note pass.

## 0.7.0-alpha.12 - 2026-06-08

- chore: bump FSharpLintAnalyzerShim 0.3.0-alpha.1 → 0.3.0-alpha.3 (security pin for `System.Security.Cryptography.Xml` 10.0.8; FCS 43.12.204) and refresh transitive dependencies. (Reclaims the `0.7.0-alpha.11` version number — the prior alpha.11 attempt was tagged locally but never published to NuGet.)

## 0.7.0-alpha.10 - 2026-04-29

### Changed

- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed. New positional order: `lintConfigPath → lintRunner → timeoutSec`.

## 0.7.0-alpha.9 - 2026-04-26

### Changed

- Per-file error reporting now goes through `PluginCtxHelpers.reportOrClearFile` (core). No behavior change.

## 0.7.0-alpha.8 - 2026-04-25

### Added

- Per-event timeout. `create` accepts a new `timeoutSec: int option`;
  when lint work for a `FileChecked` event exceeds the timeout the run is
  recorded as `TimedOut` and the plugin continues with the next event.
  Timeouts are advisory — the orphan `lintParsedSource` call is not
  cancelled, only the result is discarded.

### Changed

- Emit a `lint failed on <file>` summary on the failure path (previously
  the failure case relied on the now-removed last-log-line summary fallback).

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
