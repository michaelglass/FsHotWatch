# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### FsHotWatch

#### Fixed
- `RunWithIpc` no longer hangs the test process when `cts` is cancelled during the initial scan. Previously, cancellation was only registered after `ScanAll` completed, so cancelling during a slow scan (e.g. under thread-pool contention in large test suites) left the daemon task running indefinitely. The scan now races against the cancellation signal so shutdown is immediate regardless of scan progress.

### FsHotWatch.TestPrune

#### Fixed
- Comment-only source changes no longer add the file to `ChangedFiles`. Previously, `newChangedFiles` was computed before `changedNames` (the set of changed symbols), so any `FileChecked` event — including those where only comments changed — would add the file to `ChangedFiles` and trigger extension-based tests (e.g. Falco route matching). The fix gates `newChangedFiles` on `changedNames` being non-empty, so only genuine AST changes propagate.
