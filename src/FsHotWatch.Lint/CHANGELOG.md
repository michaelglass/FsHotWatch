# Changelog — FsHotWatch.Lint

## Unreleased

## 0.7.0-alpha.36 - 2026-10-25

- Finish: AUTOMATION-1313 fshw findings: tree-bound analyzer findings with codes


## 0.7.0-alpha.35 - 2026-10-24

- fix: a cached lint result is no longer replayed after a type it depends on changes
  shape in an earlier file or a referenced project. The key's only cross-file input was the
  file's own compiler diagnostics, which stay the same when, say, an option field becomes a
  record and the file still compiles; a typed FSharpLint rule (NoPartialFunctions on
  `x.Value`) then answers differently, and the cache served the old answer. The key now
  names the public shape of every symbol the file's check resolved outside the file
  (`used-signatures`), as the analyzers key does. An upstream edit that changes only
  bodies, comments or line positions still hits. A file whose used signatures cannot be
  read has no key: it is linted and nothing is cached. Salt bumped to `lint-merkle-v3`, so
  every existing entry is re-run once.

## 0.7.0-alpha.34 - 2026-10-18

- fix: files matched by `ignoreFiles` in fsharplint.json are no longer linted.
  FSharpLint applies `ignoreFiles` only when it lints a whole project itself; the
  plugin lints one parsed file at a time, which never checks it, so every ignored file
  (a generated one, typically) was reported anyway. The plugin now asks FSharpLint's
  own matcher (`IgnoreFiles.shouldFileBeIgnored`) before linting a file.
- fix: an edit to fsharplint.json is now seen by a running daemon. Its hash (part of
  every lint cache key) and its shape check were computed once at start-up, so after
  an edit the cache kept serving results reached under the old settings. The plugin
  now re-reads the file and recomputes both, and its `ignoreFiles`, when its text changes.

## 0.7.0-alpha.33 - 2026-10-18

- style(lint)!: enable literalNames


## 0.7.0-alpha.32 - 2026-10-18

- fix!: an fsharplint.json key FSharpLint's loader binds to no rule (a group wrapped in
  `enabled`/`config`, a setting outside `config`, an unknown key) fails the lint and
  names the key, instead of silently dropping every rule under it.

## 0.7.0-alpha.31 - 2026-10-18

- fix!: a lint run that threw, or examined nothing, now reports Failed and names the
  exception. Before, FSharpLint reported its internal failures only as
  `ProjectProgress.Failed` and returned no warnings, and the plugin reported 0 issues.
- fix: FSharpLint.Core now comes from `FSharpLintAnalyzerShim` 0.5.0-alpha.1, a build
  compiled against FCS 43.12.202+. The published 0.27.0 and 0.27.1 nightlies throw
  `MissingMethodException` under the FCS in use.
- fix: the cache key carries FSharpLint.Core's module version id, not its assembly version.

## 0.7.0-alpha.30 - 2026-10-16

- fix!: a #nowarn file no longer turns the evidence receipt red after an edit


## 0.7.0-alpha.29 - 2026-10-13

- fix: a lint result replayed from the task cache fills `WarningsByFile` like a fresh
  lint, so `warnings` counts a file a session served from a sibling checkout's cache.

## 0.7.0-alpha.28 - 2026-10-06

- feat: logging. Ignoring a `FileChecked` captured against a replaced project model is
  logged at Info, up from Debug.

## 0.7.0-alpha.27 - 2026-09-29

- Share JSON accessors, deadline parsing and run helpers; one scope-label key


## 0.7.0-alpha.26 - 2026-09-23

- Fixed: a phantom lint finding for a file the project model dropped. A
  rediscovery that removes a file clears that file's findings in every plugin
  ledger, and it clears them before the replacement model is published — so a
  `FileChecked` captured under the OLD model and still queued in the lint
  mailbox was folded afterwards and re-reported a finding about a file that is
  no longer part of the build. Nothing checks a dropped path again, so the
  finding stood until the daemon was restarted. The fold now applies
  TestPrune's rule: a result is linted only when it carries the generation of
  the model the host currently publishes as available. A file the new model
  still has is re-checked against it and republishes, so refusing costs it
  nothing.

## 0.7.0-alpha.25 - 2026-09-17

- docs: reword comments and docs left ungrammatical by removing private references
- Plugin handlers state what they read and what they commit (breaking plugin API)


## 0.7.0-alpha.24 - 2026-09-15

- Finish: update SourceLink to fix CVE-2026-62900 restore failure


## 0.7.0-alpha.23 - 2026-09-04

- the lint cache key names its file REPO-RELATIVELY, so a lint verdict
  is shared between the workspaces of one repository. The tool version, the
  configuration hash, the source and the FCS check signature are unchanged inputs.
  Salt bumped to `lint-merkle-v2`, orphaning the old absolute-path entries.

## 0.7.0-alpha.22 - 2026-09-01

- chore: rebuild against the updated FsHotWatch core dependency.

## 0.7.0-alpha.21 - 2026-09-01

- chore(deps): rebuild for the synchronized FsHotWatch.Cli dependency release.
## 0.7.0-alpha.20 - 2026-08-13

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments


## 0.7.0-alpha.18 - 2026-08-11

- chore(deps): TestPrune.Core 6.1.2, FSharpLint 0.27.0, and two others


## 0.7.0-alpha.17 - 2026-08-03

- chore(deps): update dev-tools + external dependencies


## 0.7.0-alpha.16 - 2026-07-15

- fix!: adapt to verdict-carrying terminals: `Failed` statuses carry a
  `RunVerdict` (summary + measured elapsed), and the deleted `CompleteWithSummary`
  side-channel is replaced by the verdict the terminal itself carries.

- fix!: adapt to core `RunVerdict`: completions carry the lint
  summary plus the measured per-file lint duration.

## 0.7.0-alpha.15 - 2026-07-02

- fix: skip compile items that resolve outside the repo root (NuGet-injected `_content`, e.g. xunit.v3's `DefaultRunnerReporters.fs`) — compiled in, but not ours to lint, and a latent FSharpLint-crash surface. `create` now takes a leading `repoRoot: string option` (Some → skip out-of-repo; None → include them, via the CLI's `includeOutsideRepo` override).

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
