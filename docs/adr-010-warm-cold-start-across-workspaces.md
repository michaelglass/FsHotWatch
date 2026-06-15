# ADR-010: Warm cold-start across workspaces — seed test-impact, then content-address the cache

Status: Accepted (2026-06-15)

## Context

`.fshw/` is gitignored (`.gitignore:81`); the cache and `test-impact.db` live under
`FsHwPaths.root` = `<repoRoot>/.fshw` (`FsHwPaths.fs:7`). So a freshly-created checkout
or workspace (`jj workspace add`, a new clone) starts with an **empty `.fshw/`**.

The cold-start tax on the *first* file change in such a workspace:

- **full MSBuild evaluation** of all projects (no fsproj fingerprint yet);
- **full `dotnet build`** — the build merkle task cache is empty;
- **full FCS scan** of every file — `FileCheckCache` is empty;
- **full, unpruned test run** — `test-impact.db` is empty, so test-prune has no impact
  map and falls back to running *all* tests.

For workflows that spin up many short-lived parallel workspaces (e.g. one per agent or
task), this cold-start is paid **repeatedly**. Idle-exit makes it recurring: `/.workspaces/`
daemons default to a 30-minute idle-exit (`IdleExit.fs:104`), so a daemon dies idle and
re-pays cold-start on the next touch.

Empirically, the dominant cold cost is **full test execution** (integration/database
suites take minutes; build/FCS are seconds). So the highest-value target is the empty
`test-impact.db`, not the build or FCS caches.

## The portability finding

- **`test-impact.db` is seedable across workspaces.** Symbol identity is the
  fully-qualified name and `source_file` is stored **repo-relative** — the
  symbol/dependency/test/coverage graph is a pure function of repo content, portable
  verbatim. Only the `file_keys`/`project_keys` invalidation rows embed absolute path +
  mtime (workspace-local); a seeded db with stale invalidation rows simply triggers a
  **re-index of those files** — correctness-safe (over-indexing, never stale).
- **The merkle / FCS caches are content-hashed but absolute-path-salted.** Per-file keys
  include `AbsFilePath` (`TestPrunePlugin.fs:2592`), the FCS options hash includes absolute
  `ProjectFileName`/`SourceFiles` (`CheckCache.fs:70-77`), and task filenames embed the
  absolute path (`FileTaskCache.fs`). So those caches are **not** copy-portable across
  workspaces without re-keying.

## Decision

**Warm the cold start; don't merely coordinate the redundant work.** The alternative
framings — a shared cross-daemon task queue, or a single shared daemon for N workspaces —
*schedule* the redundant cold work more politely but do not *remove* it; they are deferred
behind this and reconsidered only if concurrency still oversubscribes after the redundant
work is gone (see the "one shared daemon for N workspaces" note in ADR-006).

Two increments:

1. **(landing now) Seed `test-impact.db` on daemon start.** When a daemon starts in a
   workspace whose `test-impact.db` is absent or empty (0 indexed test methods) and a donor
   db is available with a matching schema `user_version`, copy the donor's graph *before*
   the first test selection — so prune runs pruned instead of all-tests from the first
   change. Donor = the repo's default (main) workspace `.fshw/test-impact.db`, auto-detected
   (with a config override). The portable graph (symbols/deps/tests/coverage) carries over;
   the workspace-local invalidation rows force a safe re-index. No donor (first-ever
   workspace) → no-op, behaviour unchanged.

2. **(planned follow-on) Content-address the rest of the cache** by making per-file
   merkle/FCS keys **repo-relative** instead of absolute, then expose a shared/seedable
   cache store so build/FCS/lint/analyzer verdicts also replay warm on a fresh workspace.
   This turns `jj workspace add` fully warm. Bigger — touches every plugin's `cacheKey`
   function plus the FCS options hash, and needs a merkle-salt bump to orphan old entries.

**Rejected: a single shared live `test-impact.db` across workspaces.** sqlite multi-writer
contention across concurrent daemons; copy-on-create is safer and the graph is small enough
to copy once per fresh workspace.

## Consequences

- A fresh workspace starts **pruned** — the single biggest cold-start cost (the full test
  suite) is removed without waiting to rebuild the impact map.
- Seeding is correctness-safe by construction: a stale seed over-indexes (re-checks files)
  but never serves a stale verdict, because invalidation rows stay workspace-local and
  content-checked.
- Until increment 2 lands, build/FCS caches still start cold (cheaper than the test run, so
  acceptable as an interim).
- If this is reconsidered in favour of a shared daemon, the bar is evidence that
  *simultaneously-active* (not merely numerous) workspaces oversubscribe even after seeding
  + content-addressed caching remove the redundant recompute.
