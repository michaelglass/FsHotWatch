# ADR-022: Retained filtered receipts require matching inputs

Status: Accepted (2026-09-09); implementation verification in progress.

## Context

A real two-project command regression reproduced a successful
filtered execution followed by an empty `already-verified` drain. The drain
replaced the receipt, and `check` returned exit 3 despite holding the passing
project report. Full-suite receipts were retained within an episode, but partial
receipts were not. A subsequent build launch also cleared the receipt.

The session run ledger answers which runs executed. It does not bind their
reports to current inputs, so adopting any earlier nonempty report would permit
stale evidence. This defect needs a receipt applicability check, not a weaker
rule for accepting zero tests.

## Decision

Capture a content identity from the core input walk when a test run launches.
Qualify its receipt against the same identity at completion, and check current
bytes again before publishing its scope. Missing roots, unreadable files, and
walk holes cannot authorize reuse. Declared inputs and absent declarations
participate in the identity.

A same-input `already-verified` drain may retain an applicable earlier receipt
when it executes nothing and no failure remains outstanding. A real narrower
execution may preserve earlier same-input full coverage, while its newer failure
still makes the outcome red. Aborted or invalid execution cannot borrow the
earlier passing receipt. Manual run boundaries retain their explicit reset.

The identity is internal receipt provenance, not a new persisted verdict schema
or a user-selected revision baseline. Config-excluded sources are conservatively
included because the plugin does not receive the complete exclusion contract.

## Cost and verification

This avoids repeating tests merely because a no-work drain erased their scope.
Identity validation reads and hashes the included bytes at launch, completion,
and idle scope reads. Its cost is proportional to input size; an absent receipt
or running tests skip the scope-read hash. Large-repository overhead remains to
be measured before claiming a delivery-time improvement. No watcher-only or
mtime-only shortcut is introduced.

The command regression covers cold committed and working-copy edits, a real
zero-test repository, filtered execution, repeated identical inputs, and a later
failing change. Focused controls additionally cover changes during and after
execution, unreadability, and aborts. Tests and the canonical gate remain required
before landing; this decision record is not a verification result.

## Alternatives rejected

- Keeping the last nonempty receipt without input identity could reuse stale
  evidence after a source change.
- Recovering scope from all recorded run IDs would confuse execution history
  with applicability to the current tree.
- Forcing full execution on every empty drain would remove the useful filtered
  behavior instead of repairing its evidence lifecycle.
- Resolving a user-selected revision baseline is a separate selection contract;
  it neither supplies skipped-test evidence nor repairs this receipt loss.

## Amendment: the receipt layer is deliberately ledger-independent

The Decision above says a same-input `already-verified` drain may retain an applicable
earlier receipt "when it executes nothing and no failure remains outstanding". The code does
not read that second clause: `ReceiptTransition.classify` decides `Noop` from the launch's
zero-selection, the coverage and the input-tree binding, and never consults the failure
ledger. That is the intended design, not drift, and this paragraph records why.

A receipt answers ONE question — what ran, and over which tree. The red comes from the
error ledger, which is a different surface with a different lifetime: a failure outstanding
from an earlier run is not evidence about what this drain executed, and making the receipt
forget what it legitimately holds would lose the filtered scope the drain was supposed to
preserve. That is the very loss this record exists to repair.

What must never happen is the PAIR: a retained receipt beside an outstanding failure
producing a green. Two independent things stop it. The outstanding failure is itself a
refusal carried by the evidence a completion mints (ADR-033 — `pendingObligations` counts
outstanding reds, so `EarnedEvidence.FailureReasons` is non-empty and the CLI refuses the
green), and the ledger's own diagnostics redden the exit code regardless of any receipt.
Pinned by `a retained receipt beside an outstanding failure earns a refusal, never a green`
(TestPruneRunScopeTests), which drives a quiet already-verified drain over a state that
still owes a failing project and asserts both halves: the failure is still owed afterwards,
and the evidence the drain earned refuses. Retention of the receipt itself stays covered by
`same-input already-verified drain retains a filtered receipt`.
