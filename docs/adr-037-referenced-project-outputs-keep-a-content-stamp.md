# ADR-037: referenced-project outputs keep a content stamp in the snapshot

Status: Rejected alternative (2026-09-24)

## Context

A leaf test-file edit in this repository settled in ~16 s with the build plugin on,
against ~1.3 s with it off. The check was not waiting for the build. FCS was
re-typechecking the whole test project, because every build rewrote an upstream DLL
with new bytes: CommandTree's build targets stamped jj's `@` commit id into it, and `@`
moves on every edit. `ProjectSnapshots` stamps each in-repo `-r:` reference by content,
so the project's version moved with each edit. CommandTree now stamps `@`'s parent
instead.

## Rejected: a stable stamp for an `FSharpReference`'s output

Giving the output of a referenced F# project a fixed stamp would have stopped any
build-injected metadata from forcing a re-check. It is unsound. In FCS 43.12.401
(dotnet/dotnet `e34a38d2`):

- `TransparentCompiler.fs:1884-1925` (`ComputeAssemblyData`) types against the
  reference's on-disk DLL whenever the DLL's mtime is at least the referenced
  snapshot's `GetLastModifiedTimeOnDisk()`: the newest real-filesystem mtime of its
  project file and sources (`FSharpProjectSnapshot.fs:326-333`). Only an older DLL
  sends it to the in-memory sources.
- The parent project's key sees that DLL only through the `referencesOnDisk` paths
  and stamps (`FSharpProjectSnapshot.fs:405-408`).

With a fixed stamp, a result typed against one DLL survives a rebuild that changes the
DLL but not its sources. Sources restored with preserved mtimes (see ADR-008) reach
exactly that state, and the stale result is served until some source changes.

## Decision

Keep the content stamp. Fix build-injected churn where it is injected (as CommandTree
did), not by blinding the snapshot to the bytes FCS types against.

`CheckCache.upstreamFingerprints` makes the opposite assumption, that FCS types
against references' in-memory sources. It leaves those outputs out of the check-cache
key. That is tracked separately.
