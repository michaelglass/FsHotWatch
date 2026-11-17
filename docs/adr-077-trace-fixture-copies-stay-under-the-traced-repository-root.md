# ADR-077: Trace fixture copies stay under the traced repository root

TestPrune.Trace.Tests copies fixture builds into `tests/TraceFixtures/bin-scratch/<guid>`, inside the
repository. Moving them out was rejected. AUTOMATION-1775.

Status: Accepted (2026-11-17)

## Context

AUTOMATION-1775 makes every test touch disk only under a root it alone owns, enforced by FSHW-ROOT-001.
These copies were what a repo-wide walk raced in AUTOMATION-1671, so the plan was to move them into a
`TestRoot`.

## Decision

The copies stay where they are. One helper makes them (`Fixtures.inRepositoryScratch`), and it carries the
rule's one Trace allow-list marker, which points here.

Tried 2026-11-17: the copies went into a miniature repository under a `TestRoot`, with the marker files the
fixtures look for (`mise.toml`, `LICENSE`, `src/FxLib/FxLib.fsproj`). 12 EndToEnd and Measurement tests went
red, for three reasons:

- The recorder notes a file input only under the traced repository root (`TESTPRUNE_TRACE_REPO_ROOT`). A copy
  outside that root records none of the repository reads these tests exist to measure.
- The PDBs join to the real sources by paths relative to that root.
- The census of tests that break outside the repository needs the fixtures to FAIL there. Teaching
  `FxLib.Settings` the explicit root (as FxDriver reads it) makes them succeed instead, because every traced
  run sets the variable.

Re-rooting the PDB join would make the move possible. It is more than a day of work for one allow-list entry,
so it is not scheduled.

## Consequences

- A walk of `tests/` can still meet a `bin-scratch` directory that vanishes mid-walk. `SafeWalk` tolerates
  that (AUTOMATION-1721), and the source audits walk `src/` only (AUTOMATION-1671).
- The whole-repository double listing that made the race likely is gone: InputCaptureTests' file-shim test
  lists a miniature repository it owns.
