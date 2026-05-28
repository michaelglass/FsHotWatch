# Changelog — FsHotWatch.Coverage

## Unreleased

- chore: bump CoverageRatchet.Core 0.1.0-alpha.1 → 0.1.0-alpha.2.

## 0.7.0-alpha.8 - 2026-05-04

- feat: initial release — `CoveragePlugin.create` checks per-file line and branch coverage thresholds from `coverage.cobertura.xml` files after each `TestRunCompleted` event. Reads thresholds from a `coverage-ratchet.json` config. Exposes `coverage-ratchet` and `coverage-status` IPC commands.
