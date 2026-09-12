# Current completed failures can settle a verdict wait

A completed build failure can leave the shared build artifacts invalid. Tests then
refuse admission without starting a process. Every owner is idle, but neither a
test receipt nor an analysis receipt exists. Waiting only for earned evidence
makes this actual failed check wait indefinitely.

The build worker captures the available model generation, readable configured
input-tree identity, and hashes of the evaluated graph's source and project files
inside its acquired shared operation. Its actual completion carries a separate
`CompletedFailureEvidence`. The immutable BuildState and owner ledger publish
that receipt together. Reported Failed status, cached diagnostics, and synthetic
BuildDone messages cannot create it. It is not test or analysis evidence.

A verdict wait can accept that receipt only when all work is idle, the model
still has the captured generation, all captured inputs remain readable and equal,
and the host snapshot remained unchanged during validation. Production discovery
advances the generation before replacing graph membership. A real successor
clears the old receipt; ignored events do not discard valid failure authority.
A successful build alone still does not satisfy an evidence wait.

Diagnostics carry an explicit local input manifest of paths and content hashes,
never source contents. Paths are relative to the repository where possible;
external Compile items, including absolute paths across Windows volumes, remain
valid. The consumer canonicalizes paths, rejects missing, malformed, duplicate or
unreadable witnesses, and checks current bytes. This matters because valid project
sources can live outside the ordinary src/tests tree. A response captured before
such a source edit must not stop Confirm from demanding current authority.

Check and Confirm publish the actual failed diagnostics with the actual no-test
scope. Confirm avoids another test launch only with a current completed failure;
ordinary filtered success still requires full-suite escalation. Both IPC and
in-process transports use that rule.

Failed build cache entries are misses before replay side effects, including
legacy build-failure status entries. Successful build caches and cached failures
from other plugins retain their existing behavior. The failure entry may still be
stored to replace an earlier green entry, but another check executes the build
again to establish current authority.

Rejected shortcuts were promoting idle/Failed UI state to evidence, minting a
fake test receipt, reconstructing launch provenance from cache replay, and changing
the global verdict TreeHash recipe. The failure-only witness augments that recipe
with actual graph inputs instead.
