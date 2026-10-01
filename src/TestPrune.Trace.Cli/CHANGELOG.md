# Changelog — TestPrune.Trace.Cli

## Unreleased

## 0.6.1 - 2026-10-01

- trace: Scopes.NoteInput, an escape hatch for reads the weaver cannot see
- trace: record the remaining listing and open overloads
- deps: build tooling


## 0.6.0 - 2026-09-30

- feat: `audit`, `overhead` and `file-census` take `--keep <dir>`: the verb runs in `<dir>`
  and leaves its dumps and CTRF reports there for inspection instead of deleting its scratch
  run directory.

## 0.5.0 - 2026-09-29

- feat: `audit`, `overhead` and `file-census` take `--weave-tests sites|full`: how the test
  assembly is woven, as `TraceSession.PrepareRequest.WeaveTests`. The default, `sites`, is
  the weave these verbs always used.

## 0.4.1 - 2026-09-28

- changelog: the TestPrune.Inline attribute index is a feature, not a breaking change
- coverage: pin the SDK band and remove the branches SDK 10.0.4xx left uncovered
- Index inline members as the TestPrune.Inline attribute


## 0.4.0 - 2026-09-27

- fix: `file-census` counts a test that reads the repository from outside it (an absolute
  path such as `__SOURCE_DIRECTORY__`) as depending on the repository, and a test whose type
  initializer read a repository file as reading it. Its table adds `reach-outside`.

## 0.3.1 - 2026-09-27

- Trace: key the weave cache on the weaver's build, not its version
- Trace: keep branch points under a match's later hidden sequence points

## 0.3.0 - 2026-09-27

- Merge main@origin into the branch-point weaver fix
- Keep branch points in coverage of woven assemblies
- Merge main@origin into the trace output fix and phase-1 parity results
- Keep a failing launch's output in the trace measurement verbs


## 0.2.1 - 2026-09-27

- chore: rebuild to bundle updated dependencies


## 0.2.0 - 2026-09-26

- fix: `census` reports each project's newest run whatever its status: a refused run prints
  `REFUSED` with its reason and does not fail the census, a failed one prints `FAILED` with its
  reason and exits 1. With no run at all it says "no trace run" (was "no recorded run").
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
