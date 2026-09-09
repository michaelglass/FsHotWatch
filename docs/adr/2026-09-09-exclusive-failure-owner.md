# Failed exclusive retirement retains its evidence

The actual launch-failure and shared-cleanup regressions reported Failed but then
retired with no exception in the immutable owner snapshot. Their original checks
covered only UI reporting and empty work. Adding owner-failure assertions reproduced
both losses in full run bf40bf12c4fc4b3f9e0d2c820b42266b.

FailRun now requires the original exception. Retirement and RunFailure publication
share one owner transition. runOne retains a typed success/failure outcome until
cleanup and transfer, replacing its optional-success-only completion value.
Synchronous launch failures and terminal shared startup errors pass their actual
exception through the same retirement API.

RunFailure is separate from dead-executor and supervised-queue failure: an exclusive
failure does not mean its plugin mailbox died. Ordinary successful events retain it;
a successful prepared recovery can supersede it. Existing executor or commit failure
is not downgraded by failed run retirement. A later ordinary event failure cannot
erase it either. Tests observe exception identity and an actual later committed event.

This remains part of the unfinished A104/A106 change. Both original failure controls pass in full unit reports d6a3512f39de4e7fbca78eef9678c341
and f49f93c5b7214ea1a8353071280733b9; five other unit failures remain. Full integration session59532 passed all72 tests, including original worker exception
identity and surviving-executor controls;
private earned-verdict construction and qualified recovery/debt publication still
need implementation. No public status is treated as proof of recovery.
