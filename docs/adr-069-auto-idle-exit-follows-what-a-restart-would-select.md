# ADR-069: AUTO idle exit follows what a restart would select

An idle daemon in a `/.workspaces/` checkout is kept for 30 minutes (AUTO). That window was set
when a restart cost a cold scan of 10-20 minutes. With check records replaying a scan in about
70 seconds (ADR-059) the window prices something else. A restart now costs its first check's
test selection: a correct selection when the checkout has a full pass to diff from, and the
whole suite when it does not. AUTO now exits after 5 minutes when a restart would select
correctly, and keeps 30 when it would run the full suite. AUTOMATION-1623.

Status: Accepted (2026-11-14)

## Context

Measured on intelligence (a9018f3a, fshw 0.14.0-alpha.146) on one shared server, 2026-11-13/14.
Every timed run made a distinct no-op body edit, so none was answered from a check record an
earlier run had filed. Time runs from the edit to the verdict. Each sample ran under the box's
heavy-job lock, with no remote CI running and a 1-minute load below 20. "Fresh" means the
daemon was stopped before the edit. "Resident" means it idled 5 minutes holding the lock
(15 minutes for A1). Fresh and resident ran in the same checkout, so they shared one impact
index.

| run | daemon | load before | time | live checks | tests | footprint idle → peak |
|---|---|---|---|---|---|---|
| E1 | fresh | 3.5 | 372 s | 24 | 14,730 | 0 → 6.2 GB |
| A1 | resident | 1.7 | 368 s | 1 | 14,730 | 9.6 → 12.2 GB |
| A3 | resident | 1.6 | 369 s | 1 | 14,730 | 11.5 → 11.9 GB |
| E2 | fresh, analyzer cache overwritten | 7.1 | 638 s | 2,786 | 14,730 | 0 → 11.0 GB |
| A2 | resident, analyzer cache overwritten | 5.1 | 676 s | 2,786 | 14,730 | 9.7 → 13.2 GB |
| CE1 | fresh, comment-only edit | 3.1 | 235 s | 1 | 1,987 | 0 → 4.6 GB |
| CA1 | resident, comment-only edit | 1.4 | 177 s | 1 | 1,987 | 2.3 → 3.4 GB |
| LE1 | fresh, no usable checkout baseline | 3.7 | 461 s | 1 | 20,856 (full) | 0 → 4.6 GB |
| LE2 | fresh, no usable checkout baseline | 20.0 | 651 s | 2,808 | 20,856 (full) | 0 → 13.6 GB |
| LA1 | resident, same checkout as LE | 1.7 | 341 s | 1 | 14,730 | 2.1 → 3.3 GB |

What the table shows:

- **Where a restart selects correctly,** fresh and resident are indistinguishable on a full
  edit. The test sets are identical by name. The resident daemon held 9-11.5 GB the whole time
  it idled.
- **The known price of exiting:** on a test-less edit the fresh daemon pays its start-up (cold
  scan about 67 s against a forced rescan of about 48 s, plus process start), 235 s against
  177 s, **+58 s**.
- **Where a restart cannot select** (`LE1`, `LE2`), the fresh daemon maps the changed symbol to
  no classes, and AUTOMATION-1587's refusal runs every project in full: 461-651 s against
  341 s. The checkout had no usable `test-baseline` (TestPrune's `checkoutBaselineIn` refused
  with "no test-baseline record for that tree" and "the index is sealed at X, not at the
  baseline's tree Y"). AUTOMATION-1682 is the root cause.

## Decision

1. **The test plugin publishes what a restart would select.** It publishes a
   `Restart.Selection`: `SelectsCorrectly tree` when `restartBaselineIn` finds the full pass a
   restarted daemon would diff from (the ledger's tree T, the index still sealed at T, T's
   record), and `WouldRunFullSuite reason` otherwise. It writes the file
   `.fshw/restart-selection` at start, after every completion, and after every write or clear
   of the index seal. Those are the only events that change one of the three facts, so a stale
   `SelectsCorrectly` cannot survive a re-seal at another tree. An absent or unreadable file
   reads as `WouldRunFullSuite`.
2. **Idle-exit reads it on every tick, and only for AUTO.** `resolveThreshold` now returns an
   `IdleWindow`:
   - `Fixed n` for `idleExitMin: N` or `FSHW_IDLE_EXIT_MIN=N`;
   - `AutoByRestartSelection` for AUTO in a `/.workspaces/` checkout, whose window is
     `autoWindowFor`: 5 minutes for `SelectsCorrectly`, 30 for `WouldRunFullSuite`.
3. **Precedence is unchanged.** The default checkout stays exempt (`None`). An explicit value
   or the environment override is `Fixed` and ignores the selection. The memory-pressure floor
   still shortens whichever window the tick chose.
4. **The log names the choice.** The enable line names both AUTO windows. The exit line names
   the selection that chose the window.

## Consequences

- An idle workspace daemon whose restart would select correctly frees its 2-11.5 GB after
  5 minutes instead of 30. A test-less edit after such an exit costs about 58 s more.
- A workspace without a usable checkout baseline keeps today's 30 minutes. It would pay a full
  suite on restart until AUTOMATION-1682 is fixed, and then this ADR shortens its window as
  well, with no further change.
- Core does not reference TestPrune: the publication is a file, in the shape a restarted daemon
  would read anyway.
- Breaking: `IdleExit.resolveThreshold` and `ResolvedIdleExit.ThresholdMin` return
  `IdleWindow option`. `IdleExitDeps.BaseThresholdMin` is replaced by `Window` and
  `RestartSelection`. `DaemonOptions.IdleExitMin` is `IdleWindow option`.

## Rejected

- **Landing AUTOMATION-1682 first.** It needs two soundness changes in false-green-sensitive
  code: accepting another run's same-K_P pass, and dropping "index sealed at the ledger's
  tree". This ADR is safe without them: its worst case is today's 30 minutes.
- **A 5-minute AUTO everywhere.** It would turn every restart in a checkout without a usable
  baseline into a full suite: +120-310 s per check, measured.
- **A `PluginCtx` field for the hint.** It means 45 construction sites, against one file the
  plugin already sits beside.

## Addendum (2026-11-14): the resident cost on 0.14.0-alpha.147, and recycling after a cold scan

Measured on intelligence c2387bda (fshw 0.14.0-alpha.147), on the same server and under the
same heavy-job lock as above, in a fresh workspace. "Footprint" is `footprint -p`; every daemon
had `FSHW_IDLE_EXIT_MIN=0`.

| step | pass 1: the daemon that ran the cold gate | pass 2: a fresh daemon whose session began with a replayed gate |
|---|---|---|
| cold gate | 491 s, 919 live checks, 8.3 GB after (peak 8.7) | — |
| replayed gate | 29 s, 0 live checks, 7.36 GB after | 72 s (its own replayed scan), 0 live checks, 2.32 GB after (peak 3.3) |
| idle +5 min | 7.36 GB | 2.32 GB |
| real edit (one live check, 14,774 tests run) | 327 s, 8.36 GB after (peak 9.0) | 318 s, 4.30 GB after (peak 5.2) |
| idle +5 min after the edit | 8.35 GB | 4.30 GB |

- **A daemon's idle cost depends on how its session began.** A daemon that ran a cold scan
  keeps its compiler caches (about 5 GB more than a replay-started one). The steady-state
  resident cost on alpha.147 is pass 2's: 2.3 GB idle after a replayed gate, 4.3 GB after one
  real edit.
- **Recycling after a cold scan pays for itself.** Stopping the cold daemon took about 3 s, and
  its successor's first gate replayed in 72 s against the resident daemon's 29 s. That is +46 s
  once, for 5.0 GB less held while idle (4.0 GB less after the next edit), with the edit itself
  no slower (318 s against 327 s) and the same 14,774 tests. Pass 2 ran after pass 1's daemon
  had stopped, on the same tree and records, which is the state a recycle leaves.
- **Proposed, not built here:** a daemon whose session included a cold scan recycles itself once
  that check completes (stop; the next command starts a successor that replays from records).
  It matters most for the land workspace's 60-minute window, which is exactly where a cold scan
  is followed by a long resident idle.
