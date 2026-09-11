# macOS ownership and evidence coverage baseline

Canonical CI on `190c1b21` passed compilation without warnings, project lint,
documentation synchronization, and all 3436 unit tests without skips. It stopped
at coverage before its final confirmation command. These initial macOS-only
requirements describe the new ownership/evidence surfaces honestly:

| File | Measured lines / branches | Requirement |
|---|---|---|
| DebouncedWork.fs | 98.7% / 94.1% | 98% / 94% |
| SupervisedWork.fs | 90.1% / 68.8% | 90% / 68% |
| PluginWorkOwner.fs | 98.9% / 90.0% | 98% / 90% |
| Events.fs | 100% / 92.2% | 100% / 92% |
| ProjectModelWire.fs | 100% / 91.2% | 100% / 91% |

These residuals belong to different categories, not one blanket assertion that
uncovered code is unreachable:

* Events has five generated enumerator-disposal branch alternatives over immutable
  expected-project sets, result maps, compiler diagnostics, expected-file sets and
  refusal lists. Every line is reached, including a real completed compiler warning
  distinguished from an error. ModelWire has three generated branches on the shared
  available/unavailable decision tree; valid and malformed status/generation/count
  inputs, including missing and null identities, are exercised.
* DebouncedWork retains a private FIFO identity-corruption guard and its fault
  observer. Owner retains analogous private queued-slot integrity guards. Test-only
  mutation hooks into these private representations would weaken the boundary just
  to influence a percentage, and are rejected.
* SupervisedWork retains defensive writer-publication failures, deadline/close versus
  disposal races, and startup failure handling. Some are possible failure paths that
  were not exercised. This baseline makes no stronger claim. Reachable controlled
  cancellation-registration failure, notification failure, cleanup failure, queued
  admission while finishing, late deadline callbacks, noncooperative work and
  suppressed execution context all have actual passing controls.

Public foreign/wrong-kind/duplicate capabilities, update and prepared-commit failure,
executor failure with a still-live worker, receipt publication, retained failure and
recovery have dedicated tests. Two final wrong-kind FailEvent/FailRun controls passed
within the 40-test supervisor/owner group after the full measurement, without changing
production semantics or replacing the full-suite evidence.

An impossible outer Task cancellation branch was removed separately: both launch
paths construct `Task.Run(Action)` without a cancellation token, while owned request
cancellation flows through the execution result. Its failure continuation now uses
OnlyOnFaulted and preserves the actual base exception and executor-failure transition.

All existing explicit thresholds remain unchanged. In particular Ipc's 79% branch
requirement must still be earned. No exclusion attributes or Linux baselines are
introduced here. Full current-tree CI and integration remain mandatory.

Raw full CI log and coverage, the final focused receipt and SHA-256 manifest are in
Intelligence handoff `resume-2026-09-11/fshw-ci-190c1b21`. The source-level residual audit
is retained at `/private/tmp/residual-owner-coverage.md`; that earlier audit is a
map for review, not a substitute for the final raw measurement.
