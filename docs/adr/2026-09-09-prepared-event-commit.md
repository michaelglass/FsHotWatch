# Keep an event owned through publication and commit effects

The tracked receipt for a throwing Update previously succeeded: safeUpdate returned
the prior state as a successful result. Cache writes also preceded owner-state
publication. The FailedUpdateReceipt control observed this failure directly.

Update now returns an internal success/failure result. Successful updates may use
PrepareCommit to perform external durable preparation and return a Finalize action.
The owner publishes the candidate while retaining the exact event as Committing;
finalization and cache writes run outside the pure Store writer, and only then does
the event settle. Handlers without a preparation hook use the same publication /
cache / settlement order. Failed updates never prepare or populate the cache.

Preparation failure retains prior state; finalization failure retains the published
candidate with a commit failure. The event receipt fails and the failure remains
visible. A genuine prepared recovery may supersede commit failure; unrelated
ordinary success cannot. Existing workers retain their actual completion path.

One typed failure projection distinguishes recoverable event failure, commit failure,
supervised operation failure, and dead executor. Host readers derive their views
from the same immutable rows. Treating every failed event as a dead executor was
rejected after full-run controls demonstrated erroneous run-once exceptions.
Supervised operation failures retain their failure boundary without being labeled
as dead plugin executors.

Failure reporting remains part of the original event: its diagnostic attempt occurs
before exactly-once failed settlement. Retiring first was rejected after the actual
wait control observed an idle owner before its failure report had been enqueued.
A secondary reporting failure must not cause another retirement of the same identity.

This is an intermediate A104/A106 change. TestPrune's PrepareCommit remains None;
its debt migration has not been applied. The broader common-supervisor/deadline
migration must cover preparation/finalization as well as Update and exclusive work
before final qualification; the Async hook alone does not establish that guarantee.
Private earned-verdict construction, downstream heuristic removal, full CI, updated
integration tests, and consumer qualification remain required.
