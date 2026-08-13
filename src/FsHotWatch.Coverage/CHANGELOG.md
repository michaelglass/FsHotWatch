# Changelog — FsHotWatch.Coverage

## Unreleased

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments


## 0.7.0-alpha.17 - 2026-08-11

- **fix: a run that executed no test no longer reaches a coverage verdict.**
  The plugin now declines the check outright — the same thing it already did for
  an aborted run — instead of gating on a claim that run could not support.

  Previously the decision ran on `TestRunCompleted.RanFullSuite`, which was
  vacuously `true` for an empty result map. So a run that executed nothing (the
  "0 affected classes" impact-skip, or one whose every project deferred or
  errored) chose between gating a shortfall and downgrading it, and **both
  answers were wrong**: `true` gated on coverage that run never produced, `false`
  quietly turned a real shortfall into a non-gating notice. Nothing was mis-gated
  in the safe direction by accident — the exposure was real, it was simply
  fail-closed at the one consumer that had an `Outcome` guard in front of it.
  (AUTOMATION-280)

- **BREAKING (internal API): `gateVerdict` takes a `RunScope`, not a `bool`.**
  New signature `gateVerdict : RunScope -> CheckResult -> CoverageVerdict`.
  `FullSuite` gates a shortfall; `Partial` downgrades it to a non-gating notice.
  A run that verified nothing has no `RunScope` to pass, so the case can no
  longer be smuggled in as `false` — which is what the guard clause above used to
  intercept by hand. Follows core's `TestRunCompleted.Verification` change.
  (AUTOMATION-282)

  `coverage-status`' three states (`no check run yet` / `OK` / `FAILED`) are now
  covered by tests; "no check has run yet" must never be confusable with "OK".

## 0.7.0-alpha.16 - 2026-08-03

- chore(deps): bump ecosystem tools to latest (fssemantictagger 0.13.0-alpha.20 incl. isCommitPushed fix, coverageratchet 0.15.0-alpha.11, syncdocs 0.13.0-alpha.4, fsprojlint 0.10.0-alpha.14, RefStamp 0.1.0-alpha.2)
- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context
- deps: bump CoverageRatchet.Core 0.1.0-alpha.3 -> 0.1.0-alpha.4


## 0.7.0-alpha.15 - 2026-07-15

- fix!: **`coverage-ratchet` no longer races the check that reads the file it rewrites.**
  (AUTOMATION-99) The command rewrote the thresholds config on the IPC thread while a
  `RunExclusive "coverage-check"` run might be parsing it. It now posts to the mailbox and
  claims the SAME exclusive slot as the check, so the two are serialised; if a check is in
  flight the ratchet says so instead of writing underneath it.

- fix: the coverage check reports `Running` while it runs (the framework now owns that).
  Previously it claimed its slot with no `Running`, so it rendered `✓` while still
  running AND never advanced the host's work-cycle generation — which meant
  `WaitForComplete` could never take its fast terminal path while coverage was registered.

- fix: `DateTime.Now` → `DateTime.UtcNow` on the below-threshold failure. The package's own
  changelog already asserted "timestamps now use UTC"; this one site had not been, and a
  local reading mixed into UTC arithmetic skews (or negates) the elapsed a human reads
  when coverage gates them. The new `FSHW-CLOCK-001` analyzer now bans the class repo-wide.

- fix!: adapt to core `RunVerdict` (AUTOMATION-99): `Completed` statuses carry a
  verdict ("coverage floors passed" / the not-gated notice) plus the measured
  check duration, threaded through `CheckDone`. Timestamps now use UTC.

## 0.7.0-alpha.14 - 2026-06-17

- docs: correct the README "how it works" (the plugin finds/parses/gates; TestPrune performs the coverage merge) and add an early-alpha status note.

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
