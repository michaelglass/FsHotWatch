# ADR-009: Reducing fseventsd dispatch load — watch-root strategy and FSEvents exclusion

Status: Proposed — deferred pending measurement (2026-06-15)

## Context

On macOS, fshw drives file watching through two mechanisms (`src/FsHotWatch/Watcher.fs:170-203`):

- **one native FSEvents stream** (`MacFsEvents.createWithCoalesced`, `:202`) over the
  *discovery roots* — `src/` and `tests/` (`src/FsHotWatch/Discovery.fs:12`), watched
  recursively;
- a **non-recursive** `.NET FileSystemWatcher` at the repo root for `*.sln`/`*.slnx`
  (`Watcher.fs:170`);
- **N recursive** `.NET FileSystemWatcher`s at the repo root, one per `fileCommands`
  pattern (`Watcher.fs:178-183`).

On macOS, `System.IO.FileSystemWatcher` is itself implemented on FSEvents — so every one
of those .NET watchers is its **own independent fseventsd client**. A daemon therefore
registers **2 + N** fseventsd streams, not one.

Under heavy build activity — and especially with many concurrent per-workspace daemons
and/or large trees — fseventsd CPU can climb. A natural request is "let me tell fshw not
to watch certain subdirectories." This ADR records what that can and cannot buy, and why
the obvious form of it is a no-op on the current architecture.

A user-facing exclude **already exists**: the `exclude` config key
(`src/FsHotWatch.Cli/DaemonConfig.fs:137`, parsed `:538`) feeds `PathFilter.isExcludedPath`
(`src/FsHotWatch/PathFilter.fs:21`), alongside the hardcoded `obj/`+`bin/` filter
(`isGeneratedPath`, `:10`) and `.gitignore`/`.fantomasignore` rules. But all of it runs
**post-delivery**, in the managed callback (`MacFsEvents.fs` `classifyEvent` → handler):
fseventsd has already journaled and dispatched the event before fshw drops it. So today's
exclude saves fshw's *own* downstream work (FCS / build / test) and does nothing for
fseventsd.

## The cost model that bounds everything here

fseventsd does two things per filesystem write:

1. **Journal** — append to the per-volume `.fseventsd` log. Global, unconditional,
   independent of who is watching. No watcher-side configuration can avoid it.
2. **Dispatch** — fan the event out to every stream whose watched root covers the path.

Every option in this ADR attacks only (2). If profiling shows (1) — raw event *volume* —
dominates, the only lever is **fewer concurrent builds/daemons**, which is operational, not
a watcher feature. Measure which half dominates before building anything here.

## FSEvents constraints

- **Recursive-only.** FSEvents watches each root recursively; there is no non-recursive
  mode. (That is exactly why the `*.sln` watcher is a *.NET* `FileSystemWatcher` with
  `IncludeSubdirectories=false` — FSEvents cannot express "repo-root top level only.")
- **`FSEventStreamSetExclusionPaths`** is the only kernel-level exclusion: at most **8**
  directory-prefix subtrees, **no globs**, set once before the stream starts. The P/Invoke
  is **not currently bound** in `MacFsEvents.fs`.

Two consequences fall straight out:

- **Kernel exclusion is a no-op on the current narrow roots.** The native stream watches
  only `src/`+`tests/`. The dirs one would want to exclude — VCS metadata, dependency
  caches, nested workspace checkouts — are *siblings* of `src/`, not under it. You cannot
  exclude what you are not watching. Kernel exclusion only becomes meaningful if the
  watched root is **broadened to contain** those dirs.
- **Generated dirs cannot be kernel-excluded.** `obj/`/`bin/` are scattered under every
  project at arbitrary depth — far more than 8 prefixes, and they need glob semantics
  FSEvents lacks. They stay on the managed filter permanently.

So "add an exclusion feature" is not an independent knob. **Kernel exclusion and "broaden
the watch root (and consolidate streams)" are a single decision** — you do both or neither.

## Options

Ordered cheapest → most ambitious.

**A. Coalescing-latency only, then measure.**
The FSEvents coalescing latency is a separate, shipped knob (`fsEventsLatencyMs`, default
250 ms — replacing a hardcoded 50 ms). Higher latency lets the kernel batch more events per
callback, cutting dispatch *frequency*. May be sufficient on its own. **Default position
until measured.**

**B. Scope the FileCommand watchers.**
Give `fileCommands` an optional watch subdirectory and/or honor `exclude` so the recursive
repo-root `.NET` watchers stop descending into noise trees. Cheap and localized
(`Watcher.fs`); removes the recursive repo-root watcher's descent into
VCS/dependency/nested-workspace dirs without touching the native stream or the per-daemon
stream count.

**C. Configurable native watch roots (includes, not excludes).**
Make the discovery roots configurable (default `[src; tests]`). The inverse of exclusion:
recursion is bounded by what you list, with no 8-path cap and no broad-tree journaling
concern. Cheap; gives non-standard layouts a way in. Does not consolidate streams.

**D. Consolidate to one repo-root native stream + kernel exclusions.**
Replace (native src/tests stream + sln FSW + N FileCommand FSWs) with a **single** native
FSEvents stream over the repo root, feeding `FSEventStreamSetExclusionPaths` the
directory-prefix subset of `exclude` (coarse trees: VCS metadata, dependency caches, nested
workspaces — ≤8), and moving all pattern matching (sln, FileCommand) into the managed
callback (which already filters).

- **Buys:** per-daemon fseventsd client count drops **2+N → 1**; removes double-dispatch of
  events under roots covered by more than one stream; one coherent watch path.
- **Costs / risks:** reworks the FileCommand trigger path and its tests; the coalesced
  `MustScanSubDirs` rescan (`onCoalesced`, `Watcher.fs:191-200`) now spans the repo root and
  must be bounded by the same exclusions, or a single coalesced event could enumerate the
  whole tree; edges around newly-created top-level dirs. The 8-path cap is comfortable here
  (a handful of coarse excludes).
- A natural sub-form: **promote** directory-prefix entries from the existing `exclude` to
  kernel exclusions automatically (coarse dir → FSEvents, globs/files → managed). One config
  surface, two enforcement layers.

## Decision

**Deferred, pending measurement.** Do not build kernel exclusion or stream consolidation
yet. Ship and observe the latency knob (A) first; confirm fseventsd is actually the hot
process and that dispatch — not journaling/volume — is the cost. The analysis is recorded
here so it is not re-derived from scratch.

Evidence-gated sequence once measured:

1. If the latency knob calms fseventsd → done.
2. If still hot and the cause is the recursive repo-root watchers descending into noise
   trees → **B** (cheap, targeted).
3. **D** only as a separate, evidence-gated project — it is the cleaner end-state (one
   stream per daemon) but the only option with real behavioral risk, and it is pointless if
   dispatch is not the bottleneck.

## Consequences

- The existing `exclude` key stays the single user surface. If D is adopted, it gains a
  second enforcement layer (kernel) for its coarse directory entries, transparently.
- Adopting B or D requires binding `FSEventStreamSetExclusionPaths` in `MacFsEvents.fs`
  (absent today) and, for D, reworking the sln/FileCommand watch paths to run through the
  native stream's managed callback.
- None of these reduce fseventsd journaling or raw event volume. If measurement points
  there, the lever is operational (fewer concurrent daemons/builds) and out of scope for
  this ADR — see the "one shared daemon for N workspaces" note in ADR-006.
- If this is reopened, the bar is a measurement that names fseventsd as the hot process and
  shows dispatch fan-out (not journaling) as the dominant term — otherwise the latency knob
  plus operational limits are the answer.
