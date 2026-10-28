# ADR-050: Test hosts are admitted by the host's process scheduler

Status: Accepted (2026-10-28). AUTOMATION-682 slice 3. Builds on ADR-038 and ADR-049.
Related: AUTOMATION-1299.

## Context

A repository host bounds its sessions' checks with one scheduler, but test hosts had no
host-wide bound. Each session launched its test groups through TestPrune's
`GroupSchedule.run`: all at once, or at most `tests.maxParallelProjects` at a time. Both
limits are per session. With two sessions' gates running, the box ran every test host
of both gates at once. That is the load that stretched the subprocess-heavy classes in
AUTOMATION-1298 and AUTOMATION-1299.

## Decision

- **One process scheduler per host.** A repository host makes one
  `WorkScheduler.Scheduler` for processes, with the processor count as its capacity (the
  same bound as checks). `DaemonHosting.withProcesses` hands it to every session. Each
  session's daemon takes its own lane of it, so sessions take turns, and the lane counts
  are per session.
- **A group holds one slot for as long as it runs.** A group's projects run one after
  another, so a group is one test host at a time.
  `GroupSchedule.runAdmitted lane maxParallel` acquires the slot before the group starts
  and releases it when the group ends: on success, on failure and on cancellation. A
  group still waiting when the run is cancelled never starts.
- **`tests.maxParallelProjects` still applies, inside the session.** The session caps its
  own groups first, and the host's scheduler then bounds what all sessions run together.
- **The lane reaches plugins as `PluginCtx.Processes`.** It goes
  `PluginHost ?processes` → `PluginHostServices.Processes` → `contextFor`. TestPrune's
  `executeTests` launches through it on both the event path and the force-run path.
- **A per-worktree daemon has none.** `DaemonHosting.withProcesses` leaves a standalone
  hosting unchanged. With no host to share a bound with, its launches are exactly what
  they were.
- **Surfaced.** `status --repository` prints `test hosts: R of N slot(s) held, Q queued`
  (`processes` in `-a`), and `-a` carries each session's `processAdmission`.

## Not decided here: AUTOMATION-1299's declarations

1299 asks for `exclusive`, `needsCores: N` and `serial` per task, project or test class.
In this design they are JobSpec fields on the process capacity: a slot count, a take-all
flag, and a one-at-a-time key. Two open questions keep them out of this slice:

1. **Where are they declared?** The candidates are a per-project key in `.fshw.json`
   `tests.projects[]`, an xUnit attribute on the class, or both. A per-project key fits
   group launches, but `.fshw.json` is per worktree while the bound is per host. That is
   fine for a project's own weight, but a host-level capacity setting would need a home
   that no worktree owns.
2. **Classes are not the unit here.** The scheduler admits test hosts, not the classes
   inside them. A class declaration (LandJj's 20 spawns, PlaneHost's cold fsi compile)
   can be honoured only by splitting that class into its own host launch through the
   class filter, or by xUnit's own parallelism settings inside the host. Either one is a
   TestPrune launch-shape change, not a scheduler change.

## Rejected

- **A second scheduler inside TestPrune.** Two bounds on one box are the problem this
  ticket exists to remove.
- **Holding a slot per project instead of per group.** A group already runs its projects
  one at a time, so a slot per project would add hand-overs between projects, letting
  another session take the slot mid-group, and buy no extra bound.
