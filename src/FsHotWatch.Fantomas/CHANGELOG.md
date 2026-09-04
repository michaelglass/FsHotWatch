# Changelog — FsHotWatch.Fantomas

## Unreleased

- fix!: **the plugin runs the Fantomas the repository pins, and says so** (AUTOMATION-447).
  Both `FormatPreprocessor` and `createFormatCheck` linked their own `Fantomas.Core` and
  formatted in-process with that library's defaults, while hosted CI ran the repository's
  pinned `dotnet fantomas` — its version, its `.editorconfig`. On 2026-08-21 the local
  `fshw format` reported `formatted 0 files` and the next CI run rejected four files;
  nothing in the local output said which formatter had been asked, or whether one had.
  - The package no longer references `Fantomas.Core`. Both components read the
    `fantomas` pin from `.config/dotnet-tools.json` and run `dotnet tool run fantomas`
    (`--check` for the plugin, in place for the preprocessor) from the repository root —
    the same resolution CI's `dotnet fantomas` uses, so the version, the configuration
    discovery and the ignore files are the same by construction.
  - Every status line carries the evidence:
    `format OK (12 checked) — dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)`,
    `1 of 3 files need formatting — …`, `1 of 3 files could not be formatted — …` (a parse
    error is an error-ledger entry naming the formatter, not a crashed plugin).
  - **A repository that pins no `fantomas` is refused, not greened.** `FantomasTool.PinError`
    (`ManifestMissing` | `ManifestUnreadable` | `PinMissing`) names the file and the remedy;
    the check plugin's status is `format check refused: …`, the preprocessor returns
    `Error`, and the cache key is `None` so the refusal is re-earned on every event. An
    unrestored pin (`Run "dotnet tool restore"`) is `ToolFailure.NotRestored`, rendered with
    the version and the command.
  - The check cache key now covers the pinned version and every `.editorconfig` between
    the repository root and the file, beside the source bytes: a pin bump or a config edit
    is a miss, never a replayed `format OK`. Plugin-version salt `format-check-pinned-tool-v2`.
  - **BREAKING:** `createFormatCheck` takes the repository root:
    `createFormatCheck (repoRoot: string) (timeoutSec: int option)` — the cache key needs
    the pin before any plugin context exists. `FormatPreprocessor(?timeoutSec, ?runner)`
    replaces the `slowHook` test seam with a `FantomasTool.Runner`, the function that
    stands in for the process.
  - New public module `FsHotWatch.Fantomas.FantomasTool`: `readPin`, `parseManifest`,
    `describe`, `check`, `format`, `parseOutput`, `editorConfigInputs`, `dotnetToolRunner`.

## 0.7.0-alpha.21 - 2026-09-01

- chore: rebuild against the updated FsHotWatch core dependency.

## 0.7.0-alpha.20 - 2026-09-01

- chore(deps): rebuild for the synchronized FsHotWatch.Cli dependency release.
## 0.7.0-alpha.19 - 2026-08-17

- fix!: **a cached format-check verdict can no longer claim files its cache key never
  covered** (AUTOMATION-191 — the `File = None` half of AUTOMATION-186). Format-check
  subscribes to `FileChanged`, which the framework keys as a whole-run entry, so its
  stored verdict replays verbatim. The summary counted `state.Unformatted` — the
  whole-session accumulated set — while the key is a content merkle of *that event's*
  files. An entry minted for one clean file while another file was unformatted therefore
  stored "1 files need formatting", and replayed it, unchanged, into a later session whose
  ledger was empty and green: a `summary:` line contradicting the verdict beside it.
  - The summary now states what the run it is keyed on actually checked —
    `3 of 12 files need formatting`, `format OK (12 checked)` — making it a function of
    the same bytes the merkle covers, so a cache hit says exactly what a cold run over
    those bytes says. AUTOMATION-186's scope rule (*a cache entry may only assert facts
    derivable from its key's scope*) is enforced rather than weakened; no framework,
    plugin-API or cache-format change was needed. Road not taken in
    `docs/adr-014-a-plugin-summary-is-scoped-to-its-cache-key.md`.
  - The whole-session view is unchanged where it stays live: every unformatted file is
    still an error-ledger entry (`fshw status` lists them, the verdict gates on them) and
    the `unformatted` command still answers with the accumulated set.
  - A run that compared nothing now reports `no files to check` instead of `format OK` —
    a green earned by checking nothing is the shape AUTOMATION-272 exists to refuse.

## 0.7.0-alpha.18 - 2026-08-13

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
