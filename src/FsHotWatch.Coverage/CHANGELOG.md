# Changelog — FsHotWatch.Coverage

## Unreleased

## 0.7.0-alpha.13 - 2026-06-12

- chore: bump `CoverageRatchet.Core` to 0.1.0-alpha.3.

## 0.7.0-alpha.12 - 2026-06-08

- docs: README now refers to the collapsed CLI verbs — coverage violations are surfaced by `fshw check` (the gate) and inspected with `fshw status`, replacing the retired `fshw errors` verb.

## 0.7.0-alpha.11 - 2026-06-04

- refactor: the coverage check no longer maintains its own per-file max-merged
  baselines — `mergeIntoBaselines`/`refreshBaselines` are retired. The TestPrune DB is
  now the single source of truth (it max-merges symbol-relative and ages out stale
  lines on edit), so the plugin simply checks the one cobertura emitted from the DB.
  The gating policy (full-suite gates; impact-filtered runs notify without gating) is
  unchanged.

## 0.7.0-alpha.10 - 2026-06-02

- fix: impact-filtered (partial) test runs no longer produce a false coverage red — coverage is no longer reported as failing from a stale/partial baseline when only a subset of tests ran.

## 0.7.0-alpha.9 - 2026-05-28

- chore: bump CoverageRatchet.Core 0.1.0-alpha.1 → 0.1.0-alpha.2.

## 0.7.0-alpha.8 - 2026-05-04

- feat: initial release — `CoveragePlugin.create` checks per-file line and branch coverage thresholds from `coverage.cobertura.xml` files after each `TestRunCompleted` event. Reads thresholds from a `coverage-ratchet.json` config. Exposes `coverage-ratchet` and `coverage-status` IPC commands.
