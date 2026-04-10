# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### FsHotWatch

#### Fixed
- `RunWithIpc` no longer hangs the test process when `cts` is cancelled during the initial scan. Previously, cancellation was only registered after `ScanAll` completed, so cancelling during a slow scan (e.g. under thread-pool contention in large test suites) left the daemon task running indefinitely. The scan now races against the cancellation signal so shutdown is immediate regardless of scan progress.
- `Daemon` now implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose. Previously, agents started in `Daemon.createWith` ran indefinitely since they had no cancellation token, keeping the process alive after tests completed.
- `Daemon` changed from an F# record to a class with an `internal` constructor, hiding internal fields (`Lifetime`, `Watcher`, `ScanAgent`, etc.) from consumers. `createWith` now disposes the `CancellationTokenSource` if construction fails.

### FsHotWatch.Cli

#### Fixed
- `startFreshDaemon` startup poll deadline is now configurable via the `startupTimeoutSeconds` parameter (default: 30s). Previously the 30-second deadline was hardcoded, making tests that exercise startup failure unnecessarily slow.
- Process launch in `startFreshDaemon` is now injectable via `IpcOps.LaunchDaemon`, so tests can skip the real `nohup` subprocess spawn.

### FsHotWatch.TestPrune

#### Fixed
- Comment-only source changes no longer add the file to `ChangedFiles`. Previously, `newChangedFiles` was computed before `changedNames` (the set of changed symbols), so any `FileChecked` event — including those where only comments changed — would add the file to `ChangedFiles` and trigger extension-based tests (e.g. Falco route matching). The fix gates `newChangedFiles` on `changedNames` being non-empty, so only genuine AST changes propagate.
