# Changelog — FsHotWatch.Coverage

## Unreleased

- feat: initial release — `CoveragePlugin.create` checks per-file line and branch coverage thresholds from `coverage.cobertura.xml` files after each `TestRunCompleted` event. Reads thresholds from a `coverage-ratchet.json` config. Exposes `coverage-ratchet` and `coverage-status` IPC commands.
