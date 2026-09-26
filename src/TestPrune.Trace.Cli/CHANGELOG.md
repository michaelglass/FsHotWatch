# Changelog — TestPrune.Trace.Cli

## Unreleased

- feat: `test-prune-traces` tool scaffold. It prints its usage and exits 2; the
  measurement verbs arrive with the trace store.
- feat: `test-prune-traces census [--db <path>] [--run <runId>] [--json]`: prints each project's
  census (or one JSON report per project) and exits 0 when every project meets the phase-1 bars,
  1 when one misses a bar or no recorded run exists, and 2 on a usage error, a missing database or
  one written by a newer trace schema. Without `--db` it reads `.fshw/test-traces.db`, else
  `.test-prune-traces.db`.
