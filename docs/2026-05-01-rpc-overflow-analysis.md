# 2026-05-01 — Daemon RPC overflow analysis

## Reported symptom

During Intelligence Phase D first-edit attempt of stress test
`fshw 0.10.0-stresstest4`, after the format preprocessor rewrote 3 files
(`BriefRefinement` / `FixtureLoader` / `BriefPipelineRunner`), the daemon
stopped accepting new RPC connections. The CLI surfaced:

```
Could not connect to daemon: Arithmetic operation resulted in an overflow.
```

The daemon process (PID 99294) was alive and ~7.8 GB RSS. `dotnet fshw stop`
recovered cleanly; the next `start` was healthy.

## Root cause hypotheses (in likelihood order)

### 1. Memory pressure → corrupted/oversized IPC frames (most likely)

7.8 GB RSS is well past the point where allocations and GC pauses become
unreliable. Two concrete failure modes that produce
`OverflowException: Arithmetic operation resulted in an overflow.`:

- **Content-Length parses to a value that overflows downstream Int32
  arithmetic.** `HeaderDelimitedMessageHandler` reads
  `Content-Length: <N>`, parses to `int`, then on some code paths does
  `len * sizeof(T)` or `position + len` in a checked context. If the daemon
  produced a massively-bloated `GetStatus` / `GetDiagnostics` payload (e.g.
  because the activity log or error-detail strings had grown unbounded), `N`
  could be ≥ 2^31 and trip the overflow.
- **Stream-position bookkeeping wraps.** The named-pipe stream tracks bytes
  read/written as `int64` in newer BCLs but had `int` paths in older runtimes.
  A single long-lived connection that has carried > 2 GB of total traffic
  would trip stream-position overflow.

Both paths share a common upstream cause: **runaway daemon memory growth**.
After the format preprocessor rewrote 3 files, the resulting cohort of
`FileChecked` / `BatchChecked` events likely re-checked transitive dependents
across the entire Intelligence repo, producing huge in-memory check-result
objects that FCS retained (the daemon is configured with
`keepAssemblyContents = true`, `keepAllBackgroundResolutions = true`,
`projectCacheSize = 200`).

### 2. Counter overflow in our IPC layer (ruled out)

Searched `src/FsHotWatch/Ipc.fs` and `src/FsHotWatch/Daemon.fs` for `int`
counters that could hit `Int32.MaxValue`. The two scan-related counters
(`Generation`, `InSessionBatchGen`) are `int64`. The activity-log byte counter
(`PluginActivity.totalBytes`) is `int` but capped at 2 MB. `grep -E
'\bChecked\b|Operators\.Checked'` in F# code returns nothing relevant.

The exception message itself ("Arithmetic operation resulted in an overflow.")
is the no-arg `OverflowException` ctor / checked-arithmetic default — distinct
from `int.Parse`'s overflow message ("Value was either too large or too small
for an Int32."), which means it isn't from a `Content-Length: <huge>` parse
either. Origin must be inside StreamJsonRpc / BCL stream code, not our F#.

### 3. Listener wedged after one malformed-frame connection (ruled out by test)

Added `IpcTests."server keeps accepting connections after a malformed-frame
client"`. Hits the IPC server with an oversized Content-Length, random binary
noise, and a truncated header — then verifies a subsequent well-formed
`GetStatus` still succeeds. **Passes deterministically.**

The accept loop in `IpcServer.start` correctly disposes the failed
connection's `pipeServer` and spawns a replacement listener via
`acceptOne`'s `with | _ -> pipeServer.Dispose()` arm + `start`'s
`acceptTasks <-` re-seed.

That falsifies the "single bad message wedges the listener" theory: the
real production failure mode must be one where **every** connection fails
identically because the daemon's heap state is bad — which fits
memory-pressure-on-the-server better than a stuck socket.

### 4. Specific in-fshw leaks reviewed

- **`PluginActivity` activity log / history** — already bounded by
  `maxTotalBytes = 2 * 1024 * 1024` with global LRU eviction. ✅
- **`InMemoryCheckCache`** — explicit LRU with configured `maxSize`. ✅
- **`ContentDedup.fileHashes`** — `ConcurrentDictionary<string, byte[]>`
  storing 32-byte SHA-256 hashes. Bounded by file count; 32 B × 50k files
  = 1.6 MB worst case. Self-cleans on `File.Exists = false`. ✅
- **`ErrorLedger`** — keyed by `(plugin, file)`. Each Report replaces the
  entry list. The `Detail` field can hold full test-failure stdout (could
  be 100s of KB per failing test). Bounded by `plugins × files` but if many
  files fail simultaneously with large detail payloads the ledger could
  reach 10s of MB. Not the primary culprit at 7.8 GB scale.
- **`FileTaskCache`** — on-disk; not in-memory.
- **FCS `FSharpChecker`** — configured with `keepAssemblyContents = true`,
  `keepAllBackgroundResolutions = true`, `projectCacheSize = 200`. **This
  is the most likely 7.8 GB contributor.** Each cached project carries
  full assembly contents and resolved symbols; with 200 large projects
  retained (and a stress test that re-checks across project boundaries
  repeatedly), gigabytes are easily plausible.

## What this commit ships

### 1. `ipcErrorHint` — surfaced classification (this commit)

Extracted the inline hint logic in `Program.withIpc` into a pure
`ipcErrorHint : exn -> string option` function and added an
`OverflowException` case that mirrors the existing `OutOfMemoryException`
hint (both are pipe-corruption symptoms; user-facing recovery is the same).

The previous `pkill -f FsHotWatch.Cli.dll` recovery hint was changed to
`dotnet fshw stop` — `pkill -f` is explicitly banned by team conventions and
also fails to clean up child processes registered with the daemon's
`ProcessRegistry`.

Tests:
- `ipcErrorHint maps OverflowException to pipe-corruption hint`
- `ipcErrorHint maps OutOfMemoryException to pipe-corruption hint`
- `ipcErrorHint maps TimeoutException to busy-or-hung hint`
- `ipcErrorHint returns None for unrecognized exceptions`

This does **not** fix the leak. It makes the user-facing failure
self-documenting so the next time it happens the operator has a clear
recovery path instead of a cryptic message.

### 2. Deferred work

The actual leak fix (whichever combination of FCS retention tuning,
`ErrorLedger.Detail` capping, or per-batch GC pressure mitigation
turns out to dominate) requires:

- **A reliable repro.** The Intelligence stress test reproduces it after
  several minutes of full-pipeline cycling. That's not a unit-testable
  signal. We'd need a long-running `dotnet-counters monitor` /
  `dotnet-dump` session against a daemon driven by the stress harness.
- **Heap walk via `dotnet-dump`.** Identify which type dominates Gen2 /
  LOH retention. If FCS dominates, the fix is upstream (or downstream
  in our `FSharpChecker.Create` config). If `ErrorLedger.Detail` strings
  dominate, the fix is a per-entry cap.
- **A dedicated stress-harness that drives the daemon without a real
  Intelligence repo** would let us close the loop without coupling fshw
  CI to that consumer.

None of those tools (dotnet-counters / dotnet-dump) are in the current
devenv `mise.toml` `[tools]` block. Adding them is a separate change
(probably worth doing — see follow-up).

## Recommended follow-ups

1. **Add memory diagnostic logging to the daemon.** Periodic
   (every 60s) `Logging.info "memory" "RSS=NNN MB, Gen2=NNN MB,
   activity-bytes=NNN, ledger-entries=NNN"`. Costs nothing at runtime,
   makes the next reproduction self-diagnosing from `logs/daemon.log`
   alone.
2. **Cap `ErrorLedger.Detail` per entry to (say) 64 KB.** Defensive
   trim on Report; tests for the cap. Bounds the ledger at
   `plugins × files × 64 KB` (~ a few hundred MB worst case rather
   than unbounded).
3. **Audit `FSharpChecker.Create` config.** `projectCacheSize = 200`
   plus `keepAssemblyContents = true` plus `keepAllBackgroundResolutions
   = true` is the most aggressive retention possible. For large
   downstream repos (Intelligence has ≥ 30 projects), a smaller cache
   size or selective `keepAssemblyContents` would dramatically reduce
   ceiling RSS at the cost of warm-cache hit rate.
4. **Add `dotnet-counters` and `dotnet-dump` to `mise.toml`.** So the
   next reproduction can be triaged without environment setup.
