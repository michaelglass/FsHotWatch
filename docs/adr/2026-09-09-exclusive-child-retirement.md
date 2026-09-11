# Exclusive work owns child cleanup before completion transfer

A real-process control launched and tracked a sleeping child from RunExclusive,
then allowed the work to return and positively observed its completion fold and
host rest. The child was still alive. Capturing the registered plugin's execution
context kept daemon-wide shutdown ownership but supplied no operation boundary
that could establish cleanup before this run retired.

Exclusive work now enters the same child-scope implementation used by supervised
scan/batch work. An asynchronous wrapper preserves the scope across continuations;
its synchronous counterpart continues to serve SupervisedWork.Queue. The worker
settles children in a finally before its result can become a completion message.
Cleanup failure follows the existing failed-work path and cannot publish a
successful completion. Shared-run classification/release and the atomic transfer
to the completion event still occur after that cleanup.

The regression has success and faulted-work cases. It observes an actual live OS
child, actual parent tracking, active host ownership, and final owner rest before
checking child exit. Fixture cleanup reaps only the child it launched.

This closes one demonstrated lifetime gap, not the whole A106 supervision design.
Exclusive work still needs the common finite deadline/restart/onKill policy;
CancellationToken.None here makes no claim that a hung worker can be canceled.
Update/commit and preprocessor supervision, private earned verdicts, debt ownership,
heuristic removal, full CI and consumer qualification remain unfinished.
