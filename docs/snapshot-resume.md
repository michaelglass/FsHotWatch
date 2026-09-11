# discovery identity and structured refusal

This is the continuation of the approved model contract and the parked
change `975ddb63`. The recovered scan captures registered membership,
dependency tiers and compiler options under discovery exclusion. It releases that
lease before builds and FCS execution. The parked author's evidence records the
forced race failing before this correction and 101 model/daemon tests passing
after it; those results are historical, not a current integrated verdict.

The new model observation is published in the shared host Store before discovery
can clear registries. The final completed generation and four stage counts are
published before the discovery receipt completes. A queued discovery hides the
previous completed model. Failure after clearing removes the previous availability
claim. Plugins capture the completed model generation at owner-resolved test
selection; the completed fold may earn proof only against that same generation.

The prepared publication regression observes the actual shared Store from inside
an admitted loader callback, then checks completed and failed receipts and a
retained immutable snapshot. The callback seam remains intentionally unwired at
the prepared-red checkpoint. Two recovered RunOnceOutput controls require a
machine-readable unavailable model for mapping and registration failures. These
new controls await execution in the combined owner/debt/model tree.

## Wire contract

`projectModel` is an object with schema `fshw-project-model-v1`, a typed status
(`unobserved`, `rediscovering`, `available`, `unavailable`), discovery generation,
four stage counts and a stable reason code where applicable. A genuinely empty
impact selection on an available model remains distinct from model failure.

The daemon raises a typed model-unavailable exception. The RPC boundary converts
it to application error code 523 with the versioned object in `ErrorData`; the
client does not parse human prose or issue a second diagnostics read after that
failure. StreamJsonRpc explicitly supports this through
[LocalRpcException](https://microsoft.github.io/vs-streamjsonrpc/docs/exceptions.html).
Both daemon and run-once paths publish an incomplete verdict containing the
observation so a prior green cannot survive the failed invocation.

Verdict v2 carries this model contract. Existing v1 consumers must be updated and
qualified together with the release: Intelligence's VerdictProvenance,
SelectionScoreboard and InfraValidation currently reject a different schema.
No package release, pin bump, main merge or QA transition is justified by source
preparation or a subset of the integrated unit tests.

## Integrated assertion-red checkpoint (2026-09-11)

The combined owner/debt/model full unit invocation executed 3207 tests: 3190
passed, 17 failed, zero skipped. `/tmp/integrated-unit-red.log` records both
mapping/registration RunOnce controls failing because no ConfigError was thrown,
and the publication control failing inside the loader because Store still held
Unobserved instead of Rediscovering1. These are actual assertion failures after
a successful combined compile; they justify the following source correction.

The corrected source publishes the coordinator observation into Store and carries
it through typed RPC error code523 and verdict-v2 `projectModel`. GetDiagnostics
projects the current model and authorized earned run IDs from one immutable host
snapshot; publication joins the graded run ID to that same generation and refuses
missing/failed receipts. Prior same-tree green preservation requires that same
current authorization. Same-input narrower completions may authorize an earlier
receipt only through the private earned-evidence binder, not arbitrary UI status.

A committed plugin owner failure now uses WorkFailedException, distinct from an
actual timeout. RunOnce may render its recorded plugin failure after ownership is
empty, preserving the failure verdict instead of escaping before publication.
These corrections remain source-only until the subsequent combined checks pass.

## Captured cohort invariant

FileCheckResult.ModelGeneration and BatchChecked.ModelGeneration name the discovery
model which supplied the compiler options. They are independent of source versions
and the scan sequence counter. The daemon must stamp captured identity, and the
TestPrune owner must retain debt on an absent or mismatched lineage. A captured scan
which is invalidated before publication must fail explicitly; finishing FCS against
old options is not proof about a newly registered model. The tightened preprocessor
barrier race control establishes this ordering without native watcher timing.
The data fields are prepared; their producer/consumer migration and the new stale
assertion-red/green pair are still pending at this checkpoint.

## Captured scan publication invariant

The combined unit run in `/tmp/model-queue-unit.log` reproduced the stale
capture control: the paused old scan returned success instead of the required
InvalidOperationException after a second discovery began. `WithCurrent` now
checks the captured generation and dispatches each file/cohort while holding the
same short state lock used to admit discovery. Results carry that captured
ModelGeneration; no accessor read can relabel them. The discovery writer lease
is released before preprocessors/build/FCS work. Invalidated scans retain an
explicit owner operation failure and cannot publish a stale cohort.

This source still needs combined green verification. Incremental batch capture,
the asynchronous scan-owner completion signal, and analysis-only wire receipts
remain coordinated follow-ups. Verdict-v2 Intelligence adapters and consumer
qualification remain required release dependencies; no supported consumer pin
has been claimed.
