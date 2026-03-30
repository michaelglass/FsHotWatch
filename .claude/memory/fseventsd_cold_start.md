---
name: FSEvents cold-start latency and probe-based testing
description: FSEvents test reliability fix — probeLoop pattern for cold-dir startup latency and post-batch batch-mode delays
type: project
---

FSEvents (macOS) has two reliability issues when testing with brand-new temp directories:

1. **Cold-start latency**: newly created directories can take 4-20s (or 20-40s under system load after many FSEvent streams were created/destroyed) before fseventsd begins delivering events.

2. **Post-batch delay**: after delivering a large initial event batch (e.g., 18 probe files), fseventsd can enter a batch mode that delays subsequent events by 15-30s, even with `kFSEventStreamCreateFlagNoDefer`.

**Fix**: `probeLoop` in TestHelpers.fs — writes files every 2s until `hasEvent()` returns true.

- `probeUntilEvent (dir) (hasEvent) (timeoutMs)`: writes `_fshw-probe-N.fs` files to `dir` until an event fires (handles cold-start)
- `probeLoop (write) (hasEvent) (timeoutMs)`: custom write action per iteration (handles both cold-start AND post-batch delays)

Applied to:
- `MacFsEventsTests.fs`: all 4 event-detection tests use probeLoop for BOTH stream warm-up and test event writing
- `DaemonTests.fs` (`waitForDaemonReady`): uses `probeUntilEvent` for daemon ready detection; individual tests that write specific files (New.fs, Test.fsproj, Test.sln, A/B/C.fs) use `probeLoop`
- `WatcherTests.fs`: uses `probeUntilEvent` instead of single-write

**Why:** The single-sentinel write approach (write once, wait 30s) is fragile because both the cold-start and post-batch delays are unpredictable. Probe loops handle any delay by retrying.

**How to apply:** Any new test that creates an FSEventStream or depends on macOS file watching should use `probeLoop`/`probeUntilEvent` from TestHelpers.fs instead of single writes with fixed timeouts.
