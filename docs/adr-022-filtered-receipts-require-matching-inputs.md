# ADR-022: Retained filtered receipts require matching inputs

Status: Accepted (2026-09-09); implementation verification in progress.

## Context

the real two-project command regression reproduced a successful
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
