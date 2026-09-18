# ADR-035: A superseded scan re-captures; it does not abandon the check

## Context

ADR-032 gave every published check result the generation of the project model it was
computed against, and the discovery coordinator refuses a publication whose captured
generation is no longer the live one (`ModelSupersededException`). The refusal is
correct: a scan must never tell a plugin something about a model that has been replaced.

The change-batch path was given the refusal AND a recovery. `processBatch` catches the
refusal, re-runs the cohort against the model that replaced it, up to
`changeBatchAttemptLimit` attempts, and only then fails by name
(`ModelKeptChangingException`) with its changes still owed to the next batch.

The scan path was given the refusal WITHOUT the recovery. One rediscovery landing
mid-scan therefore killed the scan, and with it the whole check. Observed on a consumer
repository on a COLD workspace:

```
Starting daemon...
  Scanning...
Could not connect to daemon: The captured project model generation 3 was invalidated
  before scan publication.
  The check could not complete — see logs/daemon.log.
```

Two things are wrong there, and they are independent.

**A mid-scan rediscovery is normal, not exceptional.** A cold `check` runs a restore and
a build in its own `beforeRun`, before the first scan finishes. That rewrites project
state, the watcher reports it, the change batch re-discovers, and the generation the scan
captured is gone. The check caused the event that killed it. The first scan of a cold
workspace is precisely the longest window in which this can happen.

**The message names the wrong process.** The daemon had connected, answered, and said
exactly what was wrong. `Could not connect to daemon` sent the reader to the pipe and the
daemon's health; the fault was in the tree.

There is also a false positive underneath. `ContentDedup.Tracker` answers "changed" for a
path it has never seen — the only honest answer for a source file, because there is no
prior. For a project file the daemon has just LOADED its model from, a prior does exist.
On a cold daemon the tracker is empty, so the first `ProjectChanged` for every `.fsproj`
re-discovered the whole workspace whatever the file said. That is what advanced the
generation in the incident above.

## Decision

- **A superseded scan re-captures and scans again, bounded.** `performScan`'s body becomes
  one attempt; the scan runs it under the same shape `processBatch` uses, up to
  `scanAttemptLimit` (5) attempts. Every attempt captures its own epoch and publishes only
  under that epoch, so the refusal's guarantee is untouched: no results about a superseded
  model ever escape. Recovery happens INSIDE the activity lease, so the gap between
  attempts is never mistaken for idleness by idle-exit or the heartbeat.
- **Exhaustion fails by name.** `ModelKeptChangingDuringScanException` says the model was
  replaced during each of N scan attempts, that nothing was published, and where to read
  which replacements happened. `isModelKeptChangingDuringScanMessage` recognizes it across
  the JSON-RPC boundary, which does not preserve the concrete type — the same
  stable-prefix technique as `isTotalDiscoveryFailureMessage`.
- **The daemon's own words reach the operator.** `IpcOutput`'s check terminal handles the
  named failure alongside the total-discovery-failure terminal: an `incomplete` verdict
  is published and the reason is printed. `Program.ipcErrorHeadline` will not wrap it in
  `Could not connect to daemon` on the other entry points either.
- **Discovery observes the project files it is about to load.** `observeProjectContent`
  records each discovered `.fsproj`'s content in the daemon's `ContentDedup.Tracker`
  BEFORE the loader reads them, so a watcher echo carrying the same bytes the model was
  built from is answered "unchanged" instead of provoking a rediscovery.
- **The fingerprint memo survives a re-capture.** An attempt that already re-discovered
  and memoized the project fingerprint does not pay a second MSBuild evaluation on the
  next attempt. A supersession caused by a real `.fsproj` edit moves the timestamps too,
  so the next attempt still re-discovers when — and only when — it must.

## Consequences

- A legitimate mid-scan rediscovery costs a re-scan (warm FCS, so mostly cache replay)
  instead of costing the check.
- A repository something is rewriting continuously still terminates, at five attempts,
  with a reason an operator can act on. It is not a green: the verdict published is
  `incomplete` and the exit code is 2.
- The ordering of `observeProjectContent` is the whole of its safety argument, and it is
  load-bearing. A write landing between this read and the loader's read leaves the tracker
  holding the OLDER bytes, so the echo still reports a change and the model is
  re-discovered — the race costs a redundant rediscovery, never a missed one. Observing
  AFTER the load would invert that: the tracker would hold bytes the model was not built
  from, and the single event that would have repaired it would be swallowed.
- `rediscoverAndClearRemoved` takes the tracker as a parameter. The one discovery path
  that does not go through it — the `Daemon.DiscoverAndRegisterProjects` member — has no
  production caller; it exists for tests, which drive the tracker explicitly when they
  care.

## Alternatives rejected

- **Leave the refusal fatal and make the trigger impossible.** Removing the cold-start
  false positive (which this change also does) shrinks the window but does not close it: a
  developer saving a `.fsproj`, or a generator writing one, during a twelve-minute cold
  scan is a legitimate event that must not abort a check. Deduplication reduces frequency;
  only the recovery makes the outcome correct.
- **Retry only the publication, not the scan.** Re-publishing results computed against the
  old model under the new generation is precisely what the refusal exists to prevent. The
  new model can hold different files and different compiler options; the results have to
  be recomputed, not restamped.
- **Let the scan hold the coordinator's admission lease for its whole duration.** This
  would make supersession impossible by making rediscovery wait for the scan. It also
  makes a twelve-minute cold scan block every change batch behind it, and turns a
  single-project edit into a stall — trading a rare recoverable abort for a routine one.
- **An unbounded re-capture loop.** "Capture the current model" is not a convergent loop on
  a repository being rewritten continuously. A check that never returns is worse than one
  that says why it could not.
