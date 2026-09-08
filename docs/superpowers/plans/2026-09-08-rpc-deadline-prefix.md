# RPC synchronous-prefix deadline implementation plan

> **For agentic workers:** Execute inline with superpowers:executing-plans; the machine admits one worker. Steps use checkbox syntax.

**Goal:** Make the existing RPC deadline cover callback execution before its Task is returned.

**Architecture:** Start the existing finite timer before scheduling the complete callback on the task pool. Await the unwrapped callback task through the existing deadline/watchdog bracket. This is one independently testable outer-guard correction, not completion of their shared PluginCore/WorkLedger design.

**Tech Stack:** F#, .NET Task, existing StreamJsonRpc adapter and xUnit tests.

**Spec:** full fetched descriptions retained at /private/tmp/current-description.txt and /private/tmp/current-description.txt. Source mapping: docs/source-preparation.md.

## Global constraints

- Preserve finite deadlines, watchdog bookkeeping, exceptions, and cancellation behavior.
- A timeout releases the RPC caller; it does not prove an in-process callback stopped. Worker supervision and ledger accounting remain required by the full issues.
- Keep In Progress. Do not merge or claim their full acceptance from this test class.
- No changes to the discovery files, no dependency changes, no daemon restart.
- Use mise run compile and the supported daemon class runner; full release still requires mise run ci.

### Task 1: Extend the existing deadline boundary

**Files:** src/FsHotWatch/Ipc.fs; existing tests/FsHotWatch.Tests/IpcTests.fs.

**Interface:** trackedTask retains string -> (unit -> Task<'a>) -> Task<'a>; no external API changes.

- [x] Existing regression failed at its intended assertion: f0b805a6b3de4e5a99031ae703e819fe, 50 passed/1 failed. The callback signals entry, blocks synchronously before returning its Task, and is always released and drained by the test.
- [x] Move callback dispatch after timer creation and schedule its synchronous prefix too:

```fsharp
let d = seamDeadline ()
use timeoutCts = new CancellationTokenSource()
let expiry = Task.Delay(d, timeoutCts.Token)
let work = Task.Run<'a>(Func<Task<'a>>(f))
let! winner = Task.WhenAny(work :> Task, expiry)
```

- [x] Run `mise run compile`; require zero errors and warnings before tests.
- [x] Run `mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- test-rerun --project FsHotWatch.Tests --filter-class FsHotWatch.Tests.IpcTests`; read the named CTRF report and require all 51 tests pass, including existing asynchronous-deadline and watchdog tests.
- [x] Save exact receipts and describe the WIP with an explicit jj message. Preserve the still-red snapshot regression and remaining full design scope.

## Self-review

This phase fixes only the reproduced synchronous-prefix gap in the independent outer deadline. It does not replace busy signals, PendingRerun, free-form status verdicts, mailbox scan reads, or the required single-writer owner. Those remain explicit work in the shared source-preparation document. No constructor or coverage claim is weakened to make this phase pass.
