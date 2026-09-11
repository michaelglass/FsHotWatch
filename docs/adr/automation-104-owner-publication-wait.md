# Owner transitions wait for actual publication

## Observed failure

Under a delayed store writer, synchronous owner transitions could report failure even though their queued mutations would subsequently commit.

The store queued a mutation, then abandoned its synchronous waiter after five seconds. That timeout did not cancel the queued mutation. A late SettleEvent could retire the work identity while losing receipt completion and successor delivery; the framework then attempted failure settlement of the already retired identity. A late CompleteRun could similarly lose its returned event identity. The original daemon logs contain both the timeout and subsequent foreign-or-completed identity failure. The cause of the initial scheduling delay is not established.

## Decision

Internal owner transitions now wait for actual store publication, preserving the existing FIFO asynchronous writer and error propagation. External operation deadlines remain separate. Increasing the timeout would retain the same late-publication race. Replacing the writer with a lock would change the asynchronous contract without evidence requiring that change.

## Evidence

The immutable test-first revision 62a1db91 holds an actual queued store writer and exercises PublishEventState, SettleEvent and CompleteRun through the real owner API. Each dedicated caller must remain pending beyond the old timeout, then publish and settle exactly once after release. The tests verify the tracked receipt, returned completion identity, committed state and final idle ownership; genuine foreign-identity rejection remains unchanged.

Against the unchanged producer, the focused suite passed 40 cases and failed all three new controls. With the publication wait correction, all 43 passed with zero skips and a clean compile.

These focused results do not establish full candidate acceptance. The corrected source requires fresh CI, integration and package consumer verification.
