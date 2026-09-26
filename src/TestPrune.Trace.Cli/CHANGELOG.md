# Changelog — TestPrune.Trace.Cli

## Unreleased

- fix: `overhead` exits 2 with "cannot measure CPU overhead: getrusage is macOS/Linux only" on
  Windows, before preparing or launching anything, instead of crashing on the missing `getrusage`;
  any other failure reading `getrusage` is that exit-2 error too.

## 0.1.0 - 2026-09-26

- docs: ships the TestPrune.Trace README, which documents every verb, as its package README.
- feat: `test-prune-traces` tool scaffold. It prints its usage and exits 2; the
  measurement verbs arrive with the trace store.
- feat: `test-prune-traces census [--db <path>] [--run <runId>] [--json]`: prints each project's
  census (or one JSON report per project) and exits 0 when every project meets the phase-1 bars,
  1 when one misses a bar or no recorded run exists, and 2 on a usage error, a missing database or
  one written by a newer trace schema. Without `--db` it reads `.fshw/test-traces.db`, else
  `.test-prune-traces.db`.
- feat: `test-prune-traces audit|overhead|file-census --project-dir <dir> --assembly <name>
  [--repo <root>] [--timeout-min 30] [--json] [-- <app args>]`, with `audit [--sample 0.01]
  [--seed 1]` and `overhead [--reps 3]`. Each prints its report (or JSON with `--json`) and exits 0
  when the project meets its bar, 1 when it does not, and 2 on a usage error or a project that
  cannot be prepared or measured.
