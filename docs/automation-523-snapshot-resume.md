# AUTOMATION-523: discovery identity and structured refusal

This is the continuation of the approved A104/A106 model contract and the parked
A523 change `975ddb63`. The recovered scan captures registered membership,
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
