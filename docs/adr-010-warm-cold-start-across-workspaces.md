# ADR-010: Warm cold-start across workspaces — seed test-impact, then content-address the cache

Status: Accepted 2026-06-15; increment 1 reversed same day — seeding moved to consumer bootstrap.

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

The portability finding above stands: `test-impact.db` *is* the right thing to seed, and the
donor is the repo's default (main) workspace `.fshw/test-impact.db`. The open question is
*who* performs the seed.

1. **Seed `test-impact.db` on fshw daemon start — REJECTED.** The original plan had the fshw
   daemon, on starting in a fresh workspace, auto-detect the donor and copy its graph before
   the first test selection. This is reversed because **fshw cannot reliably locate the
   donor from inside a fresh workspace.** Detection relied on resolving the default
   workspace's root via jj, but `jj workspace root --name default` exits 1
   ("Workspace has no recorded path: default"): only workspaces created with
   `jj workspace add` carry a recorded path; the *bootstrap* default workspace — the donor
   we want — has none. So the auto-detect no-ops in exactly the primary case, leaving the
   feature inert. The donor-path config override doesn't rescue it: requiring every consumer
   to hand-configure an absolute donor path is the consumer's job, not a property the daemon
   can infer. The seeding wiring (the `SeedTestImpact` module, the daemon-start `seedDefault`
   call, and the `tests.seedTestImpactFrom` config key) is therefore removed from fshw. It
   was never released — clean removal.

2. **Seed in the consumer's workspace-bootstrap step — ACCEPTED (where seeding lives).** The
   tool that *creates* a workspace (`jj workspace add …`) already holds the default-workspace
   root path — it is the cwd it runs `jj workspace add` *from*, or a value it computed to get
   there. That bootstrap step is the correct place to copy `<default-root>/.fshw/test-impact.db`
   into the new workspace's `.fshw/` before the first fshw run. fshw stays donor-agnostic: it
   reads whatever `test-impact.db` it finds and re-indexes workspace-local invalidation rows
   exactly as the portability finding describes. The correctness argument is unchanged; only
   the seeding actor moves out of the daemon and into the bootstrap that knows the donor path.

3. **(planned follow-on, still valid fshw-side) Content-address the rest of the cache** by
   making per-file merkle/FCS keys **repo-relative** instead of absolute, then expose a
   shared/seedable cache store so build/FCS/lint/analyzer verdicts also replay warm on a fresh
   workspace. This turns `jj workspace add` fully warm. Bigger — touches every plugin's
   `cacheKey` function plus the FCS options hash, and needs a merkle-salt bump to orphan old
   entries. Independent of who seeds `test-impact.db`.

**Rejected: a single shared live `test-impact.db` across workspaces.** sqlite multi-writer
contention across concurrent daemons; copy-on-create is safer and the graph is small enough
to copy once per fresh workspace.

## Consequences

- fshw ships **no daemon-start seeding**. A fresh workspace whose bootstrap did not seed
  `test-impact.db` starts cold on the test run (full, unpruned) until its first run rebuilds
  the impact map — the pre-ADR baseline. The cold-start tax is paid by *not seeding*, not by
  fshw.
- The portability guarantee that makes seeding correctness-safe (a stale seed over-indexes
  but never serves a stale verdict, because invalidation rows stay workspace-local and
  content-checked) is a property of the db, not the seeding actor — so it holds equally when
  the consumer's bootstrap performs the copy.
- Consumers that want a warm test cold-start must seed `test-impact.db` from the default
  workspace's `.fshw/` as part of their `jj workspace add` bootstrap, using the default-root
  path they already hold. This keeps donor detection out of fshw, where it provably cannot
  work for the bootstrap default workspace.
- The content-addressing follow-on (point 3) remains fshw's to do and is unaffected by this
  reversal.
- If a shared daemon is reconsidered, the bar is still evidence that *simultaneously-active*
  (not merely numerous) workspaces oversubscribe even after seeding + content-addressed
  caching remove the redundant recompute.
