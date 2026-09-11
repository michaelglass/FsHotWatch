# AUTOMATION-106: initial macOS process boundary coverage

The new process containment files now have explicit macOS coverage requirements,
following actual descendant settlement, admission refusal, bounded cleanup,
argument transport, cancellation, output drain and cleanup-error precedence
controls. The full unit measurement on `d16246d4` ran 3425 tests: 3421 passed,
with four unrelated IPC fixtures failing because their pipe names exceeded the
macOS path limit. Those four controls subsequently passed within IpcTests57/57.
This report establishes measurement, not full CI or release acceptance.

| New file | Measured lines / branches | macOS requirement |
|---|---|---|
| DetachedLaunch.fs | 97.4% / 75.0% | 97% / 75% |
| ProcessContainment.fs | 85.7% / 75.0% | 85% / 75% |
| OwnedChild.fs | 95.7% / 71.4% | 95% / 71% |

The residual code includes unexecuted Windows branches **and** native defensive
failure paths: missing runtime/helper identity, refused native process/group
queries, a helper that cannot be reaped, and a start API returning false.
These are not all platform-only branches. Successful native ownership behavior
and controllable refusal/cleanup paths were exercised; inducing unsafe OS-wide
failures or exposing private corruption hooks merely to reach 100% is rejected.
No coverage exclusion attributes or existing-file threshold reductions are used.
Linux requirements are unchanged, and this macOS report makes no Linux or Windows
runtime verification claim. The standalone Windows job adapter retains its
separately documented host-specific declaration.

Exact raw report, complete unit log, diagnostic and SHA-256 manifest are preserved
in the Intelligence handoff `resume-2026-09-11/fshw-boundaries-d16246d4`.
Full current-tree CI, integration, package validation and private qualification
remain required before release.
