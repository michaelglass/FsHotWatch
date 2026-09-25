# Changelog — TestPrune.Trace

## Unreleased

- feat: package scaffold with the shared trace model (`TestPrune.Trace.Model`).
- feat: `DumpReader` parses recorder dumps; a dump without its end marker is rejected as
  truncated, and `readDirectory` reports each rejected file with its reason.
