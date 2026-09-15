# ADR-023: Verification is a state; the shared daemon is not what resolves the always-on tension

Status: Accepted (2026-09-15)

## Context

AUTOMATION-569 states the organizing model behind the gate-latency work: tests are
not a gate but a continuously maintained signal that is observed and acted on.
Verification stops being an event and becomes a state — the daemon is running, tests
are running, test state is observable, and nothing blocks on a suite. Gating survives
in exactly two places: the merge queue's push gate, and the deploy gate.

The ticket also names the one tension that must be *resolved rather than assumed*:
always running the daemon conflicts with idle-exit, which exists for a measured
reason. And it proposes a resolution — **one daemon shared across workspaces instead
of one per workspace**.

This ADR adopts the model and **rejects that resolution**, because the evidence for
it does not hold and newer evidence points elsewhere.

## The proposed resolution rests on a mechanism already measured as neutral

The shared daemon is not a new idea here; it is recorded twice, both times deferred
rather than adopted.

- **ADR-006** records it as "one shared daemon for N workspaces", rejected as not
  worth the architectural cost, with an explicit revisit condition: *if a future
  workload has many simultaneously-active large-solution daemons*. It also names
  where the win would come from — **shared file-backed metadata pages** — and points
  at `exp/mmap-metadata-snapshots` as the natural companion.
- **ADR-004** measured that companion. `mmap` metadata snapshots are in the
  **rejected as memory-neutral** list, alongside ReadyToRun, DATAS and
  `keepAllBackgroundSymbolUses=false`.
- **ADR-010** defers the shared daemon and the shared task queue together, on the
  grounds that they *schedule* redundant cold work more politely but do not *remove*
  it, "reconsidered only if concurrency still oversubscribes after the redundant work
  is gone".

So the mechanism through which a shared daemon was expected to pay — one copy of the
BCL/package metadata instead of N — is the same mechanism that was benchmarked and
found to save nothing. Adopting the shared daemon now would be adopting a
architectural cost whose named benefit has already been measured at zero.

## New evidence: the binding constraint is one daemon, not N

Measured 2026-09-15 against thellma/intelligence (~1,890 F# files — roughly 2.4x the
~775-file solution ADR-003 and ADR-004 benchmarked), using `footprint <pid>`:

| observation | figure |
|---|---|
| fresh daemon at start | 849 MB |
| after ~1 minute of scanning | 2.9 GB |
| peak during the scan | **16 GB** |
| settled, still checking files | ~6.9 GB |
| a 14.5-hour-old daemon in the default workspace | 17 GB, rising to 20 GB |
| system swap during a single scan | 15.8 GB of 17.4 GB used |

**Caveat, stated plainly:** these are `footprint` readings, which count compressed and
swapped pages, and they were taken on a working machine. ADR-003 and ADR-004 used
forced `dotnet-gcdump collect` post-GC live sets on a quiet box. The figures are
therefore **not directly comparable** to the 2.8-3.1 GB steady state those ADRs
record, and the peak in particular is partly high-water churn rather than live set.
What they do establish is the shape: one daemon on a solution of this size transits
the double-digit GB range and settles well above the small-solution steady state.

The consequence for this ticket is the point. **A single daemon on a large solution
already exceeds the budget an always-on model needs.** Sharing N daemons into 1
cannot fix a constraint that 1 daemon already violates; it removes a multiplier that
is not the binding term. On this box the gate's own memory pre-flight refuses to
start a second verification run below 7 GB available, and during these scans it did
exactly that, parking a run in its 30-minute WAITING loop.

## Decision

1. **Adopt the model.** Verification is a continuously maintained state, observed and
   acted on, not an event that callers block behind. Gating survives only at the
   merge queue's push gate and the deploy gate.

2. **Reject "one shared daemon for N workspaces" as the resolution of the always-on
   tension**, on the evidence above. ADR-006's revisit condition is *not* met in the
   way it anticipated: the pain is not N simultaneously-active daemons multiplying a
   fixed cost, it is one daemon's own cost on a large solution. ADR-010's condition —
   reconsider only if concurrency still oversubscribes once redundant work is gone —
   is likewise not the live constraint.

3. **Always-on is a per-solution admission decision, measured, not a global property.**
   A daemon is admissible as always-on when its settled footprint plus the gate's
   memory bar fits the box it runs on. For small solutions this already holds. For a
   solution the size of intelligence it does not, and the honest answer is that
   always-on does not apply there yet rather than that the daemon should be shared.

4. **Idle-exit stays exactly as ADR-004 shipped it**, including the default
   workspace's exemption from auto-quit. Note the consequence that exemption now
   carries: the default checkout is precisely where an unbounded daemon accumulates,
   because nothing sheds it. The 14.5-hour 17-20 GB daemon above was in the default
   workspace. That is a known cost of the exemption, not an argument against it —
   the default checkout is the one whose warm cache is most valuable.

5. **Observability without a running daemon is already satisfied — do not build it
   again.** The ticket asks that test state be observable for a workspace whose
   daemon is not running, or that the requirement be explicitly rejected. It is met:
   ADR-013 made the verdict a file content-addressed to the tree it verified, and the
   `verdict` verb reads `.fshw/verdict.json` and *never contacts the daemon*
   (`src/FsHotWatch.Cli/Program.fs:164`). A dead daemon costs nothing here: the
   answer is a file read, and staleness is detectable because the file carries the
   tree hash it describes.

6. **Retirable-or-not is a mechanical two-term answer, and needs no daemon.** A
   workspace is retirable when (a) a verdict exists that is green *for the tree
   currently on disk* — `verdict` exit 0, not exit 3 (no verdict) or exit 4 (stale) —
   and (b) its work has landed, i.e. its head is an ancestor of the tracked remote
   bookmark. Both terms are file or VCS reads. Graph ancestry alone is not
   sufficient for (b): a descendant can delete the very files the work added, so
   containment must be checked against content, not only reachability.

## Consequences

- No shared-daemon work is scheduled. If it is revisited, the bar is new evidence
  that the *metadata-sharing* mechanism pays — which means re-measuring
  `exp/mmap-metadata-snapshots`, not re-asserting the architecture.
- The always-on model ships where it fits and is explicitly out of scope where a
  single daemon does not fit the box. That boundary is a measurement, and it moves
  when either the daemon's footprint or the machine changes.
- Reducing a large solution's scan footprint becomes the lever that would make
  always-on apply more widely. That is a different ticket from this one, and it is
  the one worth opening.
- Anything reading test state should read the verdict file, not start a daemon to ask.
