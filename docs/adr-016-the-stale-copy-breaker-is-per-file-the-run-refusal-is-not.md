# ADR-016: The stale-copy breaker is per-file; the run refusal is not

Status: Accepted (2026-09-04)

Relates to: [ADR-008](adr-008-mtime-is-not-a-content-oracle.md) (mtime is never a
content oracle — why `CopyDiffersFromOrigin` is a byte comparison and therefore
provably repairable). Ticket: AUTOMATION-495.

## Context

`StaleArtifactPreflight` repairs exactly one class of stale build output — a copy whose
origin exists on disk with different bytes — and records every repair to
`.fshw/test-prune/stale-heals.json`. Past `Threshold` repairs of ONE file inside
`Window`, a circuit breaker stops repairing that file and refuses instead, because a
repair that fires forever destroys the only signal that something upstream keeps
rebuilding origins without their consumers.

The breaker's *intent* was per-file — `healsInWindow` counts by destination path, and
the module header said "the breaker gates the HEAL, not the run". The *implementation*
was not. `List.partition` split the round into `tripped` and `toRepair`, and the guard
that followed refused when **either** list was non-empty, mapping `toRepair` into
refusals alongside the tripped file. The same guard also fired on a non-empty
`unrepairable` list, so a project merely needing a compile suppressed every repairable
copy in the round too.

The stated reason was "a repair whose run never launches is a ledger entry bought for
nothing". That is true of one run in isolation and false in aggregate: it is precisely
what made the state non-convergent. Nothing was repaired, so the next run met a
byte-identical tree, took the identical decision, and refused again. The only exit was
a human deleting the ledger — and in thellma/intelligence the ledger regrew to 95
entries in roughly six hours of ordinary work, so the exit had to be taken repeatedly.

Observed twice (2026-08-23 and 2026-08-24, on two CLI versions): every project passed,
a queued re-run refused, executed nothing, and recorded `outcome: red`,
`scope: {"kind":"none"}`, `reddenedByCount: 0` with `test-prune: fail` and every other
plugin ok. Nothing had failed. Nothing had been covered.

## Decision

**The breaker gates the repair of the file it names, and nothing else.** A round
computes its refusals (unrepairable cases, plus every file over threshold) and then
repairs every under-threshold copy regardless — including in a round that is going to
refuse. Each repair is written, logged by name, and recorded to the ledger.

**The RUN still refuses while any file is uncertifiable.** `Outcome.Refusals` non-empty
still means nothing launches; `TestPrunePlugin` is unchanged. The ticket says this half
is correct and it is: an uncovered earlier failure must stay red until a run that
executes it passes.

Convergence comes from the repairs happening, not from launching a subset. A run that
repairs four of five projects and names the fifth leaves a strictly better tree behind
it, so the refusal set shrinks each run until only the file the operator is being asked
to root-cause remains — which is a finding with a name, not a wedge.

## Rejected

**Per-file EXECUTION: let the projects whose copies are fresh launch, and report the
tripped file's project as unverified.** This is the reading of "per-file" that would
also satisfy the ticket's second acceptance line, and it is the worse one.

A run that executes a subset writes CTRF reports and coverage for a tree the verdict
cannot call green anyway — which is exactly the "three-minute partial-execution red
that reads like progress" that AUTOMATION-201 created this whole preflight to delete.
Turning `outcome: red, scope: none` into `outcome: red, scope: partial` does not give
the operator a usable signal; it gives them a longer wait before the same refusal, plus
report artifacts that a later reader can mistake for evidence. The wedge was never that
too few suites ran. It was that no run could improve the tree.

It is also not free to build: "refuse only the work that depends on the tripped file"
requires a dependency answer the preflight does not have. It knows the copy's
destination directory, which names one project's output — not which other projects
transitively consume the origin whose copy went stale. A subset chosen from the first
fact and presented as if it were the second would be a partial run claiming a
completeness it never established.

**Deleting the breaker.** The repeated-repair count is the only surviving evidence of
the build-scope gap that produces these inversions (AUTOMATION-516). Absorbing it
silently forever destroys the finding, which is what the breaker was added to stop.

**Making the ledger self-clearing on a refusal.** It converts the breaker into a
formality: every trip would erase its own history and the count could never reach the
threshold twice. The two resets the refusal message already names — deleting the ledger,
and the count ageing out of `Window` — stay the only two.

## Consequences

A refused run now writes files and ledger entries. That is a deliberate reversal of the
old rule and it is bounded: only `CopyDiffersFromOrigin` is ever repaired, the write is
the copy the build itself meant to make, and it is idempotent. A repaired copy is
byte-identical to its origin, so the round after it cannot see the same finding again.

`Outcome.Healed` can now be non-empty alongside a non-empty `Refusals`. Callers that
read `Healed` as "this run launched" were already wrong — `TestPrunePlugin` logs it
independently of the launch decision — but the field's doc comment now says so.
