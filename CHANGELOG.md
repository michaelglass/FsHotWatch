# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

### FsHotWatch.TestPrune

#### Fixed
- Comment-only source changes no longer add the file to `ChangedFiles`. Previously, `newChangedFiles` was computed before `changedNames` (the set of changed symbols), so any `FileChecked` event — including those where only comments changed — would add the file to `ChangedFiles` and trigger extension-based tests (e.g. Falco route matching). The fix gates `newChangedFiles` on `changedNames` being non-empty, so only genuine AST changes propagate.
