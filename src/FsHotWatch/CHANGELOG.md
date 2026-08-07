# Changelog — FsHotWatch (core)

## Unreleased

- feat: **activity heartbeat.** The daemon rewrites `<repoRoot>/.fshw/heartbeat` —
  Unix epoch seconds, decimal ASCII — every 15s for exactly as long as a run is in
  progress, and **never while idle**. Same contract shape as `daemon.pid`: fshw
  publishes a fact about itself and holds no opinion about who reads it or why.
  Process liveness answers the wrong question (a daemon can be alive and wedged,
  holding something while doing nothing); "is it beating?" answers the right one.
  Absence or unparseable contents means **UNKNOWN, never "stale"** — the file is
  never deleted (a stale timestamp is a stronger signal than absence), writes are
  atomic so no torn read can look like a fresh beat, and a failed beat is logged and
  swallowed rather than killing the daemon. The beat is driven by the inflight-work
  signal (`AnyPluginBusy`, held for the whole lifetime of an exclusive run) plus
  active verdict waits — never by log output — so a test phase that runs for minutes
  in silence keeps beating. The timer wakes every 1s but writes every 15s, so a run's
  first beat lands within a second rather than up to a cadence late.
- fix: heartbeat beats are serialised. A `Timer` fires on its period whether or not the
  previous callback finished, so at a one-second tick under load the callbacks overlapped
  and two beats raced on the single temp file inside the atomic write — the loser's rename
  found its source already consumed. It failed safe (logged and swallowed, next beat
  correct) but cost a beat; a non-blocking gate now keeps beats strictly serial.
- fix: **`%A` no longer truncates diagnostic logs.** `%A` caps sequences at 100
  elements (four for a bare `seq`) and hard-wraps at 80 columns, so a log line built
  with it could not distinguish 101 items from 1,500 and smeared one record across
  many lines. New `StringHelpers.describeMany` always leads with the exact count,
  then a bounded single-line sample.

## 0.10.0-alpha.6 - 2026-08-05

- check/confirm: classify "waiting on build" as Incomplete (exit 2), not a failure (exit 1)


## 0.10.0-alpha.5 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context


## 0.10.0-alpha.4 - 2026-07-22

- chore(deps): bump the transitive `System.Security.Cryptography.Xml` pin 10.0.9 → 10.0.10
  (10.0.9 became affected by GHSA-8q5v-6pqq-x66h / GHSA-23rf-6693-g89p and siblings, high
  severity, patched in 10.0.10).

- fix: **a cached per-file plugin result can no longer replay a stale whole-session
  summary that contradicts the verdict** (AUTOMATION-186). The analyzers plugin computed
  its `summary:` line from the whole-session diagnostics map + run counter, but the
  framework cached that terminal status under a *per-file* key whose content hash covers
  only that one file. Fixing a finding in file A rewrote only A's cache entry; every other
  file's entry kept the old "5 findings" summary string, and on the next cache hit
  `tryReplayCache` re-reported it verbatim as the plugin's current status — so `confirm`
  and `verdict.json` rendered "5 findings (cached)" while the diagnostics ledger the
  verdict actually gates on was empty and green. Same defect reached lint (also per-file).
  - The stale state is now **unrepresentable**: a per-file cache entry (`CachedFile*`)
    carries elapsed only — no summary, no timestamp. On replay the summary is *derived*
    from the live diagnostics ledger at report time, inside the same status-lock guard that
    reports it (atomic derive-and-report), so a summary that claims findings the verdict
    can't see cannot occur. Run-level (`File=None`) entries — e.g. Build, whose summary
    carries pass counts that are not in the error ledger — keep replaying their stored
    summary verbatim. The scope rule: *a cache entry may only assert facts derivable from
    its key's scope.*
  - `FileTaskCache` serialization is versioned (format 2); pre-existing on-disk entries in
    the old shape deterministically miss (telemetry counter increments) rather than
    half-parsing. First check per workspace after upgrade is a cold cache — self-healing.
  - Format-check has the same defect but sits on the shared `File=None` path and needs a
    per-plugin "summary is ledger-derived" signal to distinguish it from Build; deliberately
    scoped to a follow-up (AUTOMATION-191) rather than bundled here.

## 0.10.0-alpha.3 - 2026-07-15

- fix!: **the task cache can no longer DESTROY the result of a run that actually
  happened.** (AUTOMATION-161) `PluginFramework` consulted the cache for *every*
  dispatched event, including a plugin's own `Custom` messages — and a `Custom` message is
  not an observation of the world, it is the **delivery of work already done**. Its payload
  is not in the cache key: TestPrune's `cacheKeyFor` reads the `TestRunCompleted` it
  carries only far enough to decide whether the result is *cacheable*, never far enough to
  *identify* it. Two different runs — different run ids, different results, both green —
  therefore collide on one key, and serving the hit **skips the handler**, which is the
  only thing that folds a finished run into plugin state.
  - Observed on an unchanged tree: `fshw confirm` forced the full suite, ran it for 102
    seconds, passed 1965 tests and wrote a complete CTRF report — and the framework
    replayed a cached terminal over the `TestsFinished` carrying it. The plugin never
    learned the run had happened, `test-scope` went on answering *"no tests ran"*, and
    `confirm` refused to give a verdict on evidence it had just spent 102 seconds
    producing.
  - A `Custom` message is now a cache **writer**, never a cache **reader**. The write is
    kept: a Custom window is how the entry the next `BuildCompleted` hits gets minted at
    all. A cache that can destroy evidence is worse than no cache.

- fix: **`Ctrf.tidyRunsDir` can no longer FAULT the run it is cleaning up after.** It
  enumerated the run directory TWICE and applied the SECOND enumeration's count to the
  FIRST enumeration's list (`runDirs |> List.skip (min keepRuns (List.length (runDirs
  …)))`). A run directory appearing between them — a second fshw process, a concurrent
  workspace, a parallel suite finishing its own run — pushed the skip count past the
  list's length, and `List.skip` raised `ArgumentException`, which the enclosing
  `IOException | UnauthorizedAccessException` handler did not catch. It escaped
  best-effort housekeeping that is explicitly documented as *"must never fail the run
  that produced the evidence"*. It now enumerates ONCE, and the catch is widened —
  "must never fail the run" is a promise about all exceptions or it is not a promise.

- fix: **the terminal-status ownership guard is now ATOMIC against a run-slot claim**
  (AUTOMATION-118). A design review alleged the shipped AUTOMATION-95/99 guard was
  "a narrowing, not a cure", and it was right about the mechanism: `liveRunOwnsStatus`
  read the run slots under `runSlotsLock` and the caller reported the status *after*
  releasing it. A `RunExclusive` claim landing in that window publishes `Running`, and
  the stale terminal — a cache replay, a `FileChecked` completion, a `safeUpdate`
  crash-net stamp — then lands **on top of the live run**: the "content-free ✓ while
  tests are still running" signature (`started:` with no `elapsed:`) that AUTOMATION-99
  exists to make impossible. This reproduces against the real framework.
  - It was **not reachable from any shipped plugin**: every `ctx.RunExclusive` and
    `ctx.ReportStatus` call in all eight plugins is lexically inside `Update`, and the
    agent loop awaits each `Update` before dequeuing the next event — so the check, the
    claim and the report were totally ordered on one logical thread. But `PluginCtx` is
    a record of closures; nothing in the type system confines it to the mailbox. That
    was soundness by **convention**, and any plugin claiming a slot from a `work` async
    or a spawned task — a legal use of the API — reopened the window.
  - The ownership *decision* and the *report* are now one critical section under a new
    per-handler `statusLock`, which `runExclusive` also holds across [claim slot +
    report `Running`]. A terminal therefore either wins the race (published while no run
    is genuinely live) or loses it (the claim is already visible and it is suppressed) —
    never both. Lock order is always `statusLock` → `runSlotsLock` and never the reverse,
    and nothing reachable from `services.ReportStatus` re-enters the framework
    (`PluginHost.setStatus` is a dictionary write plus a non-blocking `MailboxProcessor.Post`),
    so no cycle exists.

- feat!: **the daemon records its own binary identity, so a new CLI can never
  silently talk to an old daemon.** (AUTOMATION-147) On startup the daemon writes
  `.fshw/daemon.identity` — its assembly version **and a content hash of its binary** —
  before the IPC pipe starts listening. A hash, not just a semver: a locally-repacked
  build can share a version string and differ in content, which is AUTOMATION-123
  reincarnated as a *process* rather than a package. `DaemonIdentity.compareIdentity`
  is the whole contract, and it fails CLOSED: a daemon whose recorded identity differs
  is stale, and a daemon with **no recorded identity at all** (any build predating this
  handshake) is stale too. The check is therefore **unilateral** — an old daemon needs
  to cooperate in nothing to be found stale, which is what protects the first repin
  after this ships.
  - **BREAKING:** the internal `Daemon` constructor takes its `ProcessRegistry.Registry`
    as a parameter (see below).

- feat: **a wedged plugin is detected, named, and recovered from** (AUTOMATION-147).
  A plugin that reported `Running` and has posted no completion past a bound is *by
  definition* wedged. `PluginWedge` now says so on a cadence — `[wedge] analyzers still
  running after 5m (no completion posted; treated as wedged at 1h 5m)` — and past the
  bound it names the wedge, leaves a breadcrumb the next `fshw` command prints, and
  gracefully restarts the daemon down the same `cts.Cancel()` path as `fshw stop`.
  A silent log during a wedge is indistinguishable from a healthy idle daemon; the
  AUTOMATION-92 hang was silent for 8h36m.
  - The bound sits **above** the verdict deadline plus grace, so a client blocked on
    `WaitForComplete` still gets its own, more specific `TimeoutException` first, and a
    long-but-live run is never restarted out from under its warm FCS cache.
  - The detector is honest about its own limits: work in flight with *no* plugin
    reporting `Running` past the bound reports that it **cannot tell which plugin** and
    fails closed. It never concludes "healthy" by default.
  - `FSHW_PLUGIN_WEDGE_SEC` overrides the bound. There is deliberately no way to
    configure it off.

- fix: **the daemon reaps the children its plugins spawned — it never did.**
  (AUTOMATION-147) `ProcessRegistry` is scoped by an `AsyncLocal`, and an `AsyncLocal`
  is only visible to `ExecutionContext`s captured *after* it is set. The daemon
  installed its registry in the `Daemon` **constructor** — by which point the
  `PluginHost` and the scan/change mailboxes already existed. So when the scan mailbox
  dispatched to a plugin, that plugin's `runProcess` resolved **no registry**,
  `ProcessRegistry.track` dropped the child on a silent `| None -> ()`, and `KillAll`
  truthfully reported nothing to kill. Every plugin child — test runners, file-command
  processes — outlived the daemon, reparented to init. Observed on a live daemon:
  `track pid=69166 -> NO REGISTRY (dropped)` / `KillAll: 0 tracked`. The registry is now
  constructed and installed by `createWith` **before anything captures a context**, and
  is passed into the daemon, which makes the correct ordering the only constructible one.
  This is what makes `fshw stop` *and* the wedge self-heal actually reap in-flight
  children instead of orphaning them.

- fix: **`ProcessRegistry.track` warns instead of silently dropping.** A child spawned
  with no registry in scope can never be reaped, so the miss is now logged loudly. A leak
  you cannot see is a leak you cannot fix — the silence above is precisely how this one
  survived.

- feat: **`FsHotWatch.ContentHash` — ONE hasher, ONE fail-closed policy.** (AUTOMATION-129)
  Three hashers had grown independently, each with its own answer to the only question
  that matters: *what do you hash when you CANNOT READ the file?* There is one safe
  answer — a sentinel that will NOT match the hash of the same file readable — and it
  must be the same everywhere, or a claim can silently cover a file nobody looked at.
  `TreeHash` and the verdict's producer identity both route through it; the daemon's
  binary identity (AUTOMATION-147) should adopt it on merge.

- feat!: **`FsHotWatch.Ctrf` — ONE DIRECTORY PER RUN.** (AUTOMATION-129) Reports live in
  `.fshw/test-runs/<runId>/<Project>.ctrf.json`, and nothing else does — so **the
  directory IS the run**. A flat shared pile was unreadable in BOTH directions and both
  directions bit us on the same day: PRESENCE was ambiguous (nine files, and nothing
  saying which belonged to the run you just did — the only way to answer was forensics
  on mtimes), and ABSENCE was ambiguous (an empty listing could mean "no tests ran" —
  the single most important fact an agent can learn — or "cleaned up", or "wrong glob").
  Two capable readers guessed, an hour apart, and both guessed wrong.
  - The run-dir is created BEFORE anything executes, so a run that executed and reported
    nothing leaves an EMPTY DIRECTORY — a stated fact — while a run that never happened
    leaves none at all. **Absence stops being something the reader has to decode.**
  - `reportsForRun` replaces any mtime-window heuristic: membership is DECLARED.
  - `tidyRunsDir` rotates whole RUN DIRECTORIES (newest 10) and purges the pre-129 flat
    layout. History is evidence — old runs are rotated, never wiped on start.

- feat: **`FsHotWatch.TreeHash` — the content address of the tree fshw verifies.** (AUTOMATION-129)
  `TreeHash.compute repoRoot excludePatterns` hashes every file under the discovery roots
  that is not build output, tooling state, or config-excluded — **sources AND
  content/fixture files** — plus `.fshw.json` itself, by CONTENT (never mtime, per
  ADR-008). This is what a verdict is addressed by, so that "a green from a different
  tree" becomes detectable rather than silently reusable. The recipe
  (`fshw-tree-sha256-v1`) is a documented contract: `relPath + NUL + sha256hex(bytes) +
  LF` per file in ordinal path order, SHA-256 over the whole. Fixtures are in the hash on
  purpose — a changed JSON fixture that MSBuild declined to re-copy once let a suite run
  green against the OLD fixture and put a red commit on `main` for hours (APPLIC-24).

- feat: **`FsHotWatch.Ctrf` — one CTRF reader, and reports that are RETAINED.** (AUTOMATION-129)
  The summary parser the verdict layer, the flakiness recorder and `.fshw/verdict.json`
  all read a report through, so they cannot disagree about what it says. Plus report
  discovery (`reportsSince` — the reports a given run produced, never "the newest file in
  the directory") and `tidyRunsDir`, which bounds retention to the newest few per project
  and purges the DEAD `.log` format.

- feat: `FsHwPaths.configFile` / `ConfigFileName` — `.fshw.json`'s path, named in one
  place. It is an input to the tree hash, and a second spelling is a way for that to stop
  being true.

- fix!: **a refused `RunExclusive` claim is a value you cannot drop.** (AUTOMATION-99)
  `PluginCtx.RunExclusive` returned `unit` and silently discarded the work when the
  slot was held. The caller could not tell — so a force-run whose claim was refused
  reported success having executed nothing, and a reply resolved *inside* the dropped
  work never resolved at all. It now returns `RunClaim = Claimed | SlotBusy`, which
  (with `TreatWarningsAsErrors`) the caller must handle: skip with a stated reason, or
  queue. The repo's own `FSHW-CLAIM-001` analyzer rejects `|> ignore`-ing it.
  - **BREAKING:** every `ctx.RunExclusive` call site must handle the returned `RunClaim`.

- fix!: **the framework reports `Running` at the claim** (AUTOMATION-99). A launched run
  that nobody can see as `Running` is now unrepresentable: `runExclusive` reports it
  itself. (`CoveragePlugin` shipped exactly that gap — it rendered `✓` while running,
  and because the host's work-cycle generation only advances on a `Running` transition,
  its generation never moved, so `WaitForComplete` could never take its fast path while
  coverage was registered.) The hand-written `ReportStatus(Running)`-before-`RunExclusive`
  pairs are gone.

- fix!: **every terminal carries its verdict; `RunVerdict` refuses to be empty.**
  (AUTOMATION-99) `Failed` is now `Failed of error * at * verdict: RunVerdict` — the
  error is the *diagnosis*, the verdict is the one-line summary + the measured duration.
  The old shape fell back to `startedAt = at` when no `Running` preceded a `Failed`,
  recording an elapsed of ZERO — i.e. the "`started:` with no `elapsed:`" render that is
  this bug's own diagnostic tell survived on the `Failed` path. `RunVerdict` is now a
  private-field type whose only constructor (`RunVerdict.create`) rejects an
  empty/whitespace summary, so no site — daemon, cache deserializer, CLI, test helper or
  example — can build a content-free `✓`.
  - **BREAKING:** `ReportStatus(Failed …)` takes a verdict; `CompleteWithSummary` and
    `PluginHostServices.SetSummary` are DELETED (the status is the summary channel).
    `PluginCtxHelpers.failedWith` is the counterpart to `completeWith`.

- fix: **terminal-stamp ownership is enforced at the framework's one funnel**, not
  re-implemented per plugin (AUTOMATION-99). While an exclusive run is in flight it OWNS
  the plugin's status; any *other* terminal — from a per-file handler, a cache replay, or
  the handler-crash net — is dropped. Previously three hand-written
  `if not (ctx.IsRunning "tests")` guards in TestPrune enforced this for one plugin only,
  keyed by a slot name the framework didn't know: the same duplication class that caused
  the bug. A suppressed terminal is now also barred from the task cache, so it cannot be
  replayed later as a verdict no run produced.

- fix!: **IPC commands get a narrow `CommandCtx` — they can observe and `Post`, never
  launch work.** (AUTOMATION-99) `PluginHandler.Commands` received the full `PluginCtx`
  and ran on the IPC thread, outside the mailbox and outside the busy accounting — the
  exact capability that caused this bug, and one that had a second live user
  (`coverage-ratchet` rewriting its config file while a check might be reading it).
  `CommandCtx` exposes `RepoRoot` / `Log` / `Post` / `IsRunning` / `ProjectGraph` and
  nothing else, so `Post` is the only expressible way for a command to cause work.
  - **BREAKING:** command handlers take `CommandCtx<'Msg>` instead of `PluginCtx<'Msg>`.

- feat: repo-local convention analyzers (`analyzers/FsHotWatch.Rules`, loaded by
  `check`/`confirm` via `.fshw.json`): `FSHW-CLAIM-001` (a `RunClaim` must never be discarded) and
  `FSHW-CLOCK-001` (no `DateTime.Now` — every daemon timestamp is UTC). Both fire in CI
  and are pinned by positive *and* negative controls.

- fix!: **`Completed` carries its verdict — a guard that cannot say what it measured
  has not measured anything.** (AUTOMATION-99) `PluginStatus.Completed` is now
  `Completed of at * verdict: RunVerdict` where `RunVerdict = { Summary; Elapsed }`,
  so a plugin physically cannot report "done" without stating what it did and how
  long it took. This kills the manufactured "✓ with `started:` but no `elapsed:`"
  terminal — the signature of a status stamped over a live run — at the type level,
  and collapses the summary side-channel: the host routes `verdict.Summary` into the
  run record, so the status and the history can never disagree.
  - **BREAKING:** every `ReportStatus(Completed …)` site must supply a `RunVerdict`;
    `PluginCtxHelpers.completeWith` now takes the elapsed duration.
  - The run record's elapsed is the verdict's sworn duration (startedAt derived as
    `at - Elapsed`); the old fallback that recorded a ZERO elapsed for a Completed
    with no preceding Running is gone.
  - Cache replay keeps the ORIGINAL verdict and marks it `(cached)`; pre-verdict
    on-disk cache entries are rejected as misses (no evidence to replay).
  - Preprocessor completions now carry a verdict (files checked / rewritten + duration).
- fix!: **a plugin with work in flight is BUSY, full stop — one counter, no hand-off
  gap.** (AUTOMATION-99) An exclusive `RunExclusive` run now holds a token in the
  SAME `inflightCount` that counts mailbox events, from claim until AFTER its
  completion message is posted. The previous `inflight > 0 || anyRunSlotBusy()`
  composite read two atomics at different instants, and the slot-release →
  completion-post hand-off had a window in which a verdict-waiting `check` could
  observe "at rest" while the run's verdict was still in flight.
- fix: a handler that throws while an exclusive run is in flight no longer stomps a
  forced `Failed` over the live run's `Running` (the run's completion path is
  guaranteed to deliver the earned terminal status); the crash is still logged.
  With no run in flight the forced `Failed` stands, as before. (AUTOMATION-99)

- feat: `ErrorLedger.ErrorEntry.warningWithDetail` — a Warning-severity entry with a
  detail body, the sibling `errorWithDetail` never had. For conditions that deny a
  clean verdict under the default warn-fail policy without themselves being a failed
  check: the first is a source file the symbol analyser could not read, which leaves a
  hole in the impact graph the gate must not silently paper over. (AUTOMATION-113)

- fix!: **ONE spawn primitive, bounded at both ends.** There were TWO —
  `runProcessWithTimeout` and `runProcessWithLaunchWatchdog` — and every caller
  except TestPrune used the unsafe one, so the two wedges the watchdog was built
  to close stayed wide open in Build / FileCommand / DepsFreshness / hooks: a
  single blocking `WaitForExit(-1)` that a machine sleep turns into a permanent
  wait, and an UNBOUNDED `Task.WaitAll` drain **on the success path** that never
  returns when a child exits while a GRANDCHILD (an MSBuild node, a Playwright
  driver) still holds the inherited stdout pipe — EOF never comes, and that is
  the 16 h wedge. Both are now collapsed into `ProcessHelper.runProcess`, which
  ALWAYS polls `HasExited` and ALWAYS bounds the post-exit drain.
  - **BREAKING:** `runProcessWithTimeout` and `runProcessWithLaunchWatchdog` are
    gone; `runProcess` takes a `ProcessBounds` instead of a bare `TimeSpan`.
  - `ProcessBounds` is constructed only via `ProcessBounds.streaming` (a child
    whose first byte proves liveness — a test runner) or `ProcessBounds.silent`
    (a child that may print nothing for its whole run — `dotnet build -v q`, a
    buffering `sh -c` wrapper — for which a launch deadline would false-kill a
    healthy slow build, so a finite timeout is the bound). Its fields are
    private, so a call site cannot assemble "no bound at all" out of two
    `InfiniteTimeSpan`s.
- fix!: **`WaitForScan` could reproduce the 8h36m wedge exactly.** It had no
  deadline: `WaitForScanGeneration` raced only daemon *shutdown*, never a clock;
  the CLI passes `-1L` (including on every convergence re-scan); and it is
  `check`'s FIRST step. Any hang inside `performScan` — a Fantomas preprocessor,
  an Ionide design-time evaluation, an FCS check — meant "Scanning…" forever: no
  timeout, no error, no verdict. The deadline is now enforced at the
  `trackedTask` SEAM, so EVERY bracketed RPC is bounded by construction rather
  than one method at a time (bounding them one at a time is how you get the
  second `WaitForScan`). An RPC that bounds itself more precisely —
  `WaitForComplete`, which names the still-running plugins — still wins the race.
- fix!: **the operation watchdog went blind under concurrency — the one thing it
  existed for.** It tracked a SINGLE in-flight op while the IPC server runs three
  acceptors by design: a second `Begin` overwrote the first's record and the
  first `End` erased the second's. Two parallel `fshw check` clients were enough,
  and a genuinely wedged op then heartbeat as `idle` with `WedgeReport() = None`.
  Ops are now keyed by an `OpToken` minted at `Begin` and retired at `End`, and
  the report names the OLDEST overrunning op.
  - **BREAKING:** `Watchdog.Begin` returns an `OpToken`; `Watchdog.End` takes one.
    `WatchdogState.InFlight` is an `InFlightOp list`, not an `InFlightOp option`.
- fix!: **the on-disk FCS check cache was a structural no-op — removed.**
  `FileCheckCache.TryGet` always reconstructed `CheckResults = ParseOnly` (FCS
  types aren't serializable), and `ParseOnly` is exactly what
  `CheckPipeline.tryGetCachedFullCheck` treats as a MISS. It could not hit —
  ever, by construction, on any input — while writing and enumerating a dead JSON
  file per checked file per daemon restart (1,051 measured in one repo). It was
  also the DEFAULT.
  - **BREAKING:** the `FsHotWatch.FileCheckCache` module and the
    `CacheBackendConfig.FileBackend` case are gone, as is
    `detectDefaultCacheBackend`. The default is now `NoCache` — which is what the
    file backend already did, minus the dead I/O. `"cache": "file"` / `"jj"` in
    `.fshw.json` is REJECTED with a loud config warning naming the removal, not
    silently accepted as a setting that does nothing. `"cache": "memory"` is
    unaffected and really does cache.
- fix: the on-disk **task cache grew without bound**. Entries are named
  `{plugin--file}@{contentHash}.json` so "multiple versions coexist", but only
  the entry matching the CURRENT content is reachable (`tryGet` reconstructs the
  exact path), so every edit permanently added a dead sibling: 3,126 files /
  13 MB in a ~1.5-day-old workspace, while `Stats`/`clearFile`/`clearPlugin`
  full-scan the directory. A write now collects its superseded siblings. No LRU —
  an LRU would retain entries that are not merely cold but unreachable.

- fix!: **the daemon can no longer wait forever.** A client-unbounded
  `WaitForComplete` (what `fshw check` issues) used to resolve to
  `TimeSpan.MaxValue` — a literally infinite wait. When a plugin wedged, the
  daemon heartbeat-logged `in-flight WaitForComplete running Ns` indefinitely
  and the gate never returned (observed: 8h36m, silent, no error, no timeout).
  The daemon now always applies a hard deadline (`Ipc.resolveVerdictDeadline`,
  default 60 min, override `FSHW_VERDICT_DEADLINE_SEC`; there is deliberately no
  "infinite" setting). On breach it raises a `TimeoutException` NAMING the
  still-running plugin and its elapsed time, which the CLI renders as a
  diagnostic exit 2 plus the recovery path, instead of hanging.
- fix: `SafeWalk` — a new symlink-safe, depth-bounded directory walker, now THE
  walker for every repo-scale enumeration (`Discovery`, `Watcher`, plus the CLI
  and TestPrune plugin walks). It never descends a symlinked directory, so
  termination is structural. Both `Directory.GetDirectories`-plus-recursion and
  `SearchOption.AllDirectories` follow directory symlinks into cycles: on a
  devenv/nix repo, `.devenv/profile` links into `/nix/store`, whose reachable
  tree has TWO self-loop symlinks in one directory
  (`ncurses-6.6-dev/include/{ncurses,ncursesw} -> .`). That branches — every
  level doubles the path count — so within the kernel's ~32-symlink ELOOP
  envelope there are ~2^32 paths to enumerate (measured with
  `AllDirectories`: 800k+ files in 52s, still climbing). This is what wedged
  the gate.

## 0.10.0-alpha.2 - 2026-07-08

- fix: a compile-item-only `.fsproj` edit no longer wedges the deps-freshness
  gate red. The gate's mtime fast-path read an added/reordered `<Compile>` item
  as a stale restore; on a memory-pressured box where the phantom restore timed
  out, the debounce tracker kept the project pinned Stale (deps RED) on every
  subsequent cycle until the daemon restarted. The mtime probe is now backed by
  a CONTENT signature over ONLY the dependency-declaring inputs — the fsproj's
  `PackageReference` / `ProjectReference` / `Import` / `Sdk` / target-framework
  subset (source items like `<Compile>` are excluded), plus the bytes of every
  governing `Directory.Packages.props` / `Directory.Build.props` / `paket.lock` /
  `paket.dependencies`. A compile-item-only edit leaves that signature unchanged
  so the phantom Stale is recognised and suppressed; a real package-graph change
  still moves it and re-arms recovery. See
  `docs/adr-008-mtime-is-not-a-content-oracle.md`.

## 0.10.0-alpha.1 - 2026-07-05

- feat: `ProcessHelper.runProcessWithLaunchWatchdog` — run a child under a
  **launch-liveness watchdog** that can never block forever. `runProcessWithTimeout`
  bounds only the *total* run via one `WaitForExit`, which is INFINITE for a
  caller that passes no timeout; if the spawned child never becomes a live,
  progressing process — an overloaded box where it never appears, or a machine
  sleep that kills it mid-launch — that wait hangs forever. The watchdog polls
  liveness instead and enforces a bounded *launch* deadline: the window in which
  the child must show its FIRST sign of life (any output, or an exit). Once it
  does, the wait is unbounded again, so a slow-but-progressing suite streaming
  output is never launch-killed — the deadline governs launch, not total
  duration. Only a **stall** (no life within the deadline) kills the tree and
  raises `LaunchStalledException`; a child that EXITS is classified normally by
  its exit code (a nonzero exit with no output is a genuine failing / zero-match
  test, indistinguishable from a spawn-death, so it is never force-aborted). The
  machine-sleep case is closed instead by (a) polling `HasExited` so the exit is
  observed at all, and (b) a **bounded** post-exit drain — an unbounded
  `WaitForExit` blocks forever if a grandchild (an MSBuild/vstest node) inherits
  the stdout pipe and outlives the child, which is the actual 16 h wedge. The
  pure decision (`decideLaunchStep`) and injectable loop
  (`launchWatchdogLoopWith`) are deterministically testable, mirroring
  `waitForDaemonReadyWith`. (AUTOMATION-65 QA finding: the launch gap)

## 0.9.0-alpha.1 - 2026-07-03

- fix: a faulted exclusive run (`RunExclusive` build/coverage/tests slot) can no longer strand its plugin in `Running` — the fault branch now forces a terminal `Failed` status, so a client `WaitForComplete` gets a prompt non-zero verdict instead of waiting forever (AUTOMATION-65; the fresh-workspace "test run never launches" wedge).
- fix: the idle-exit can no longer fire while a client verdict-wait is in flight — active `WaitForAllTerminal` waits now count as busy via `IdleExit.busyForIdleExit` (AUTOMATION-65; previously the daemon shut down mid-`check` after 30 min, dropping the client with a connection error).

## 0.8.0-alpha.34 - 2026-07-02

- feat: `PathFilter.isOutsideRepo` — true when a path resolves outside the repo root (a rooted or `..`-prefixed relative path), e.g. a NuGet-injected `_content` compile item under `~/.nuget`. `isExcludedPath`'s out-of-repo test now shares this check (AUTOMATION-49). `PathFilter.isOutsideRepoScoped` lifts it over an optional repo root (`None` = include everything) — the shared predicate the analyzers + lint plugins use for the `includeOutsideRepo` skip.

## 0.8.0-alpha.33 - 2026-06-24

- chore(deps): bump `System.Security.Cryptography.Xml` 10.0.9; suite-wide dependency refresh (version-coupled with the cli/build release).

## 0.8.0-alpha.32 - 2026-06-19

- feat: a new `TestResult.TestsErrored` case (plus `TestResult.isErrored`) for a
  test run that aborted before producing a usable result — not a pass and not a
  failure. The TestPrune gate uses it to surface an honest "errored" diagnostic
  instead of a misleading test failure. Note: exhaustive matches on `TestResult`
  in downstream code now need an arm for it.

## 0.8.0-alpha.31 - 2026-06-17

- docs: README accuracy & early-alpha status-note pass (no functional changes).

## 0.8.0-alpha.30 - 2026-06-16

- feat: `PluginCtx.ProjectGraph` exposes a read-only project-graph accessor
  (`GetAllProjects` / `GetTransitiveDependentProjects` / `GetProjectReferences`
  / `GetCanonicalDllPath`) to plugins. The daemon installs the live graph before
  registering plugins (`PluginHost.SetProjectGraph`); tests and the null-checker
  daemon get a no-op accessor. Enables the TestPrune plugin's dependency-
  fingerprint fanout.

## 0.8.0-alpha.29 - 2026-06-15

- feat: FSEvents watch latency is now configurable (was a hardcoded 50ms).
  `MacFsEvents.create` / `createWithCoalesced` and `FileWatcher.create` take an explicit
  `latencySeconds`, threaded from the new `DaemonOptions.FsEventsLatencySeconds` (default 0.25s).

## 0.8.0-alpha.28 - 2026-06-12

- fix: `ContentDedup` is now scoped per daemon instance, so two daemons sharing a
  machine can no longer poison each other's content-hash cache (a change seen by one
  daemon was wrongly deduplicated as "already seen" by another).
- fix: `PluginHost.setStatus` is non-blocking, closing a latent deadlock where a plugin
  setting its status from an agent thread could stall the host.
- fix: the IPC accept loop now logs faults instead of silently swallowing them, so a
  failing client connection surfaces in the daemon log rather than vanishing.
- fix: `FilePattern.parse` rejects globs it cannot match consistently rather than
  accepting a pattern that would silently never fire.
- refactor: discovery roots now have a single source of truth and the fingerprint honors
  configured excludes, so excluded paths no longer perturb change detection.
- chore: float `FSharp.Compiler.Service` via a `43.*` wildcard so restore resolves the
  latest published 43.x.
- fix(deps): lift transitive `MessagePack` to 2.5.301 (GHSA-hv8m-jj95-wg3x).

## 0.8.0-alpha.27 - 2026-06-11

### Changed

- `DepsFreshness.productionRestoreRunner` now composes a pure, internal `restoreSteps` plan
  with a thin `runRestoreSteps` executor (behavior, ordering, and per-step timeouts unchanged).
  `restoreSteps repoRoot fsproj` returns the ordered list of `RestoreStep` records (`Purpose`,
  `Args`, `WorkingDir`) — `dotnet restore` first, then one `dotnet paket restore --group <g>`
  per group enumerated from the in-scope `paket.lock` (via `paketGroupsFromLock`, falling back
  to `Main` when no lock is found), then `dotnet tool restore` when a `.config/dotnet-tools.json`
  is in scope. Extracting the branchy "which steps, in what order, with what args" decision from
  the process shell-out makes it unit-testable without invoking `dotnet`/`paket`; the
  DepsFreshness.fs coverage floor rose accordingly (macOS line 72→91, branch 46→83).

## 0.8.0-alpha.26 - 2026-06-10

### Changed

- Extracted the post-kill output-drain tail into an internal `ProcessHelper.drainedOrEmpty`
  helper (behavior unchanged). Part of making unit-suite coverage deterministic under machine
  load: the two real-subprocess plugin timeout tests moved to the coverage-excluded integration
  suite and ratchet floors are settled to stable actuals.

## 0.8.0-alpha.25 - 2026-06-10

### Fixed

- Deps-freshness recovery no longer runs a bare `dotnet paket restore`. Bare `paket restore` is paket's full-repo `AllProjects` mode: it walks every project directory (`FindAllProjects`) to inject references, following directory symlinks with no cycle detection. A self-referential symlink loop — e.g. the macOS-SDK `ncurses` links inside a Nix `.devenv` profile — made it recurse forever, wedging the restore until the per-step timeout and failing the deps gate. Recovery now restores **per group** (`paket restore --group <g>`, enumerated from `paket.lock` via the new `paketGroupsFromLock`): passing an explicit group makes paket skip the project-discovery walk while still restoring that group's sources/git-dependencies. Per-project reference injection (for repos using `paket.references`) is already handled by the preceding `dotnet restore <fsproj>` step via `Paket.Restore.targets`' `paket restore --project`.

## 0.8.0-alpha.24 - 2026-06-08

### Performance

- The plugin dispatch loop computes each event's task-cache key once and threads the single value to both the cache lookup (`tryReplayCache`) and the store (`runAndCache`). Previously `cacheKeyFn event` was called twice per dispatch; for BuildPlugin that key is a full content-hash of the project graph, so a cache miss paid two SHA-256 passes per trigger. Threading one value also guarantees the lookup key equals the store key by construction.

### Fixed

- `WaitForComplete` no longer reports a vacuous clean on a cold / never-ran daemon. The daemon-side wait (`waitForAllTerminalCore`) gained a `requireVerdict` guard: on the `WaitForComplete` RPC path (`waitForVerdict`), the host is only considered at rest once at least one plugin has reached a real terminal state (Completed/Failed) — an all-Idle host (registered plugins, nothing run/verified) now keeps blocking until a real verdict instead of resolving immediately via the quiescence leg. All other callers (`RunOnce`/scan-settling via `waitForAllTerminal`) keep the Idle-tolerant behavior so they can never hang on a legitimately never-run plugin.
- Deps-freshness signature is now content-hashed instead of keyed on max dep-file mtime ticks. A preserved-mtime dep-file rewrite (`rsync -a` / `cp -p` / branch-switch over `paket.lock` / `Directory.Packages.props`) previously left the signature byte-identical, so debounced restore recovery never re-armed. `evaluateProject` also gains a content-drift cross-check: an mtime-`Fresh` verdict whose content signature drifted from the last fresh baseline is re-restored rather than silently proceeded. `staleSignature` return type changed `int64 → string`; `RecoveryTracker` gains `HasContentDrifted`/`RecordFreshSignature`. See `docs/adr-008-mtime-is-not-a-content-oracle.md`.
- The daemon's blocking waits (`waitForAllTerminal` and the IPC `WaitForScanGeneration`) now observe the daemon's shutdown `CancellationToken`. Previously, if the daemon was torn down (`fshw stop`, idle-exit, or any `cts.Cancel()`) while an RPC was blocked mid-wait, the wait could resolve cleanly during teardown and the in-flight `check` / `WaitForComplete` / `WaitForScan` RPC would falsely report **success** (or, for in-process callers, hang). The waits now fault with `OperationCanceledException("daemon shutting down")` the moment the shutdown token fires, so the in-flight RPC propagates a failure to the client instead of a false green.

### Changed

- `status`, `WaitForScan`, and the daemon logs now report **live** completeness — registered files minus the ones currently lacking a valid full type-check — instead of a frozen scan-end snapshot. They are computed from the same coverage signal `fshw check` uses, so the `complete:`/`incomplete:` line always agrees with `check` and no longer rots when an incremental edit + re-check fixes a file after the scan finished. Mechanically, `ScanState.ScanComplete` dropped its `total`/`unchecked` snapshot counts (it now only carries `elapsed`); both consumers read one shared `liveCoverage` (registered minus checked) at request time. The rendered string formats are unchanged.

### Added

- feat: `fshw check` now converges on incompleteness instead of reporting it. After the daemon settles, `check` reads diagnostics **and a live coverage signal** (how many registered files currently lack a valid full type-check result). If failures are found it short-circuits to **exit 1** immediately (real problems are reported now, not re-scanned away). If coverage is complete and clean → **exit 0**. If the check is incomplete but otherwise clean, `check` tries to **fix** it: it forces a bounded series of re-scans (up to 3), re-reading coverage each time, and stops early on completion, on a newly-surfaced failure, or when the unchecked count stops shrinking. Only a genuinely un-completable check returns the new **exit 2**. Structurally, the verdict is a total function over an explicit outcome type and coverage is a *required* input: a daemon that doesn't report coverage (old build / parse gap) is treated as `Unknown`, which enters convergence and can never read as a false green 0. The daemon-side signal is a live "checked files" set in `PluginHost` (a full check via `EmitFileChecked` adds a file; a file change via `EmitFileChanged` removes it), exposed over IPC as an `unchecked` field on the check response.

- feat: memory pressure shortens idle-exit (`pressureIdleFloorMin` in `.fshw.json`). When a daemon is already idle-exit-eligible (a `/.workspaces/` checkout in AUTO mode, or an explicit `idleExitMin N`) AND the machine is under memory pressure, the effective idle window is shortened to `min(idleExitMin, pressureIdleFloorMin)` so a tight machine sheds idle daemons fast — a 30-min workspace daemon quits after 2 min idle under pressure. Pressure is the GC's own high-load mark (`GC.GetGCMemoryInfo().MemoryLoadBytes >= HighMemoryLoadThresholdBytes`), re-evaluated each 30s tick (if pressure subsides the full window is restored). The default/main workspace stays **exempt**: pressure only shortens an already-applicable window, it never creates one. Absent → floor at `2` min; `0`/`false` → pressure-shortening disabled; positive `N` → floor at `N` min. This replaces the earlier (unreleased, same-session) `pressureTrimPct` in-place cache-trim, which was reversed before release: trimming kept ~400 MB plus the process resident yet still forced a cold FCS rebuild on the next edit (the file-backed CheckCache survives both trim and quit), so quitting dominates. See ADR-005.

- `ErrorLedger` now accepts an optional `logError: string -> string -> unit` sink
  (defaults to the process-global `Logging.error`/stderr) used when a reporter throws.
  The failure log is emitted on the ledger's MailboxProcessor agent thread, so a
  `Console.Error` capture in tests raced any concurrent `Console.SetError`; the
  injectable sink lets callers (notably tests) observe reporter failures without
  touching process-global console state. No behavior change at the default.
## 0.8.0-alpha.23 - 2026-06-07

- chore: republish so the bundled `fshw` carries the CLI's dead-code schema-probe fix
  (now sourced from TestPrune.Core's public `Database.SchemaVersion`, 4.2.1). Also folds
  in an internal `ProcessHelper` refactor unifying the child-env sanitization lists into
  one `sanitizedChildEnvKeys`. No core API change.

## 0.8.0-alpha.22 - 2026-06-07

- feat: idle-exit — a daemon quits gracefully after a configurable idle period, freeing
  100% of its footprint; the next `fshw` command auto-restarts it (the file-backed check
  cache survives, so the rescan is mostly cache hits). `idleExitMin` in `.fshw.json`:
  absent → AUTO (30 min, but only for `/.workspaces/` checkouts — the default/main
  workspace never auto-quits); `0`/`false` → disabled; positive `N` → `N` min in any
  workspace. See docs/adr-004. (Entry restored 2026-06-07: shipped in alpha.22 but the
  CHANGELOG line was lost in a merge.)
- fix: spawned `dotnet build` (and any other child process) no longer inherits the
  `MSBUILD_EXE_PATH` / `MSBuildExtensionsPath` / `MSBuildSDKsPath` variables that
  Ionide.ProjInfo writes into the daemon's own environment during in-process project
  evaluation. On a multi-SDK machine those leaked vars pinned the child's MSBuild to a
  different (or incomplete) SDK band than the muxer resolved, so the implicit restore
  failed with exit 1 and zero diagnostics — surfacing as the long-standing
  `fshw check --run-once` "Build FAILED / 0 Error(s)" while a plain-shell `dotnet build`
  of the same tree was clean. `ProcessHelper.runProcessWithTimeout` now strips these keys
  before every spawn (same treatment as the arch-specific `DOTNET_ROOT_*` keys);
  caller-supplied overrides still win. See docs/leaked-msbuild-env-bug.md.
- fix: the deps-freshness gate no longer counts .config/dotnet-tools.json as a dependency input — a dotnet-tool bump no longer false-stales every project into restore-recovery/skipped scans

## 0.8.0-alpha.21 - 2026-06-06

- feat: `fshw dead-code` — runs TestPrune's unreachable-symbol analysis against the daemon's .fshw/test-impact.db (same semantics as the standalone test-prune CLI: --entry/--include-tests/--verbose), no DB copying needed.
- feat: the CLI now ships `System.GC.ConserveMemory=9` in its runtimeconfig, cutting the
  daemon's steady memory footprint ~25–40% (benchmarked: settled ~3.0 GB vs 3.9–4.4 GB,
  peak 5.0 vs 5.9–7.8 GB against a 32-project solution) at no scan-speed or diagnostics
  cost. Override per-process with the `DOTNET_GCConserveMemory` env var. Also dropped the
  dead `projectCacheSize` arg (ignored by the TransparentCompiler). See docs/adr-003.
- fix: cold scans no longer silently truncate. A build touching `obj/**/ref/*.dll` could
  cancel in-flight scan checks (`CancelPreviousCheck`); cancelled checks surfaced as
  `None` and were dropped, so a scan could report green with a shrunken diagnostic set.
  The scan now retries cancelled/aborted checks (bounded) and surfaces any still-unchecked
  count as a non-ok `incomplete:` condition in status + the scan log line.
  (Both entries restored 2026-06-07: shipped in alpha.21 but the CHANGELOG lines were lost in a merge.)

## 0.8.0-alpha.20 - 2026-06-05

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.19 (clears the FCS check-cache after a
  schema-bump DB recreate, so the symbol graph re-indexes fully instead of staying
  partial). No core API change; republished so the bundled tool ships the fix.

## 0.8.0-alpha.19 - 2026-06-04

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.18 (cold-start coverage no longer
  clobbers prior coverage while the symbol graph is still indexing). No CLI API change;
  republished so `dotnet fshw` carries the fix.

## 0.8.0-alpha.18 - 2026-06-04

- chore: bundle the DB-backed coverage plugins (FsHotWatch.TestPrune 0.7.0-alpha.17,
  FsHotWatch.Coverage 0.7.0-alpha.11). No CLI API change; republished so the `fshw`
  tool's bundled plugins carry TestPrune-native single-source coverage.

## 0.8.0-alpha.17 - 2026-06-03

- feat: auto-recovering deps-freshness gate before FCS analysis. When a project's `obj/project.assets.json` is stale relative to its declared deps (a `PackageReference` added without `dotnet restore`, or a half-completed restore), the daemon detects it *before* type-checking, attempts a one-shot `dotnet`/`paket`/`tool restore` to recover automatically, and only if recovery fails reports a single actionable `deps` diagnostic — instead of letting FCS emit a phantom "namespace/type not found" error-storm. Detection and orchestration are pure and unit-tested (`FsHotWatch.DepsFreshness`: `compareFreshness`, `dependencyFiles`, `evaluateProject`, `RecoveryTracker`); each restore step is bounded by a 5-minute timeout so a hung restore fails fast rather than wedging the scan.

## 0.8.0-alpha.16 - 2026-06-02

- feat: TestsDeferred result case — a test project that never ran (apphost not yet produced) is reported as deferred ("waiting on build"), non-passing, instead of a false-green pass; the aggregate verdict/exit code can no longer be green when tests didn't actually run.
- fix: a throwing IErrorReporter no longer yields a false-clean verdict — the ledger self-reports the reporter failure so the verdict/exit code reflects that diagnostics couldn't be recorded.

## 0.8.0-alpha.15 - 2026-05-28

- fix: `FileErrorReporter` caps oversized `message`/`detail` fields before serialization to avoid `System.Text.Json` transcode `OverflowException` (FR `fr-fileerrorreporter-overflow.md`).

## 0.8.0-alpha.14 - 2026-05-26

- feat: daemon auto-refreshes FCS state on `.fsproj` / `obj/project.assets.json` changes — adding a `PackageReference` + `dotnet restore` now resolves without a daemon restart or a `.fs` save (FR `docs/fr-auto-refresh-fsproj-changes.md`). `Watcher.classifyChange` routes `obj/project.assets.json` through `ProjectChanged` (recovers the `.fsproj`-edit-races-`restore` window), and `Daemon.processBatch` re-checks the affected project's source files after re-discovery.
- feat: scoped FCS invalidation — a single project change invalidates only that project plus its transitive dependents (`Daemon.resolveAffectedProjects`), keeping unrelated projects' warm FCS state and cached check results instead of cold-starting the whole solution. Repo-wide changes (`.props`, solution edits, a brand-new project) fall back to full re-discovery.
- feat: `CheckPipeline.PrepareForRediscovery` gained an optional `?clearCheckCache` parameter (default `true`); the scoped project-change path passes `false` to retain unrelated projects' cached check results across re-discovery.

## 0.8.0-alpha.13 - 2026-05-04

- fix: `Daemon.DaemonOptions.FcsSuppressedCodes = None` now resolves to an empty `Set<int>` instead of `Set.ofList [ 1182 ]`; projects that need FS1182 silenced should declare `<NoWarn>FS1182</NoWarn>` in their fsproj
- feat: `Daemon.resolveFcsSuppressedCodes : int list option -> Set<int>` — public helper exposing the option→Set resolution so it is directly testable

## 0.8.0-alpha.12 - 2026-04-29

### Added

- **`FsHotWatch.ErrorLedger.DiagnosticSeverity.order`** — total order on `Error/Warning/Info/Hint` for severity-threshold comparisons.
- **`FsHotWatch.CheckCache.DiagnosticSignature`** record (`StartLine/StartColumn/ErrorNumber/Severity/Message`) and **`hashDiagnosticSignatures`** — extracted from `fcsCheckSignature` so the hashing/sorting logic is unit-testable without a live `FSharpCheckFileResults`.
- **`FsHotWatch.FileTaskCache`** — atomic on-disk writes (write-temp-then-rename) and startup size telemetry logging total entry count and on-disk bytes.
- **`IProjectGraphReader.GetTargetFramework`** + **`ProjectGraph.GetTargetFramework`** — exposes the first `<TargetFramework>` (or first entry of `<TargetFrameworks>`) parsed from each .fsproj at registration time. Avoids re-opening + re-parsing the .fsproj from downstream consumers. **`extractTargetFramework`** is the underlying pure XDocument-taking helper.
- **`IProjectGraphReader.GetCanonicalDllPath`** — returns `<projDir>/bin/Debug/<TFM>/<projectName>.dll` (or `None` when TFM is missing). Centralises the canonical-DLL convention so consumers like `BuildPlugin.verifyArtifactsFresh` don't reinvent the path.
- **`IProjectGraphReader.GetMaxSourceMtime`** — newest `LastWriteTimeUtc` across a project's on-disk source files. Drives mtime-based artifact-freshness checks.

### Removed

- **`FsHotWatch.ProjectDirtyTracker` module.** The dirty-bit handoff between BuildPlugin and TestPrunePlugin is gone — staleness is enforced inline by BuildPlugin's post-build verification, so the heuristic dirty tracker has no consumers. Files using it: `markDirty`, `clearFreshProjects`, `isStaleProject`, the manual-run-tests deadlock workaround. See FsHotWatch.Build / FsHotWatch.TestPrune CHANGELOGs for downstream impact.

### Changed

- **BREAKING — `TestResult` DU widened with `elapsed: TimeSpan`.** All three constructors (`TestsPassed`, `TestsFailed`, `TestsTimedOut`) now carry a per-project wall-clock duration. `FileTaskCache` round-trips it via a new `elapsedSeconds` JSON field; older cached entries that omit the field deserialize as `TimeSpan.Zero` (no recorded duration). Pattern-match callers must add the new bind position. New `TestResult.elapsed` accessor is the recommended way to read it.

### Changed

- **`FsHotWatch.ErrorLedger.fromString`** now returns `DiagnosticSeverity option` instead of throwing on unknown severity strings. Callers that previously caught the exception should match on `None`.
- **`FsHotWatch.CheckCache.fcsCheckSignature`** guards `Unchecked.defaultof<FSharpCheckFileResults>` and other null cases — returns `"full-check-null"` / `"full-check-error"` instead of throwing.
- **`TimestampCacheKeyProvider.GetFileHash`** now hashes file content (SHA-256) instead of metadata (path + size + mtime). Closes a correctness gap where two files with the same size but different bytes (or same bytes with different mtime) would produce the wrong key. The name is preserved for backward compatibility; behavior matches the original "ls-tree merkle hash" design intent that was deferred at module creation.

### Removed

- **`JjCacheKeyProvider`** — was a stub that delegated to `TimestampCacheKeyProvider`. Its only role was as a marker for `Daemon.fs` to wire up `JjScanGuard` via runtime type-test.
- **`FsHotWatch.JjHelper` module** — `JjScanGuard`, `JjScanDecision`, `getWorkingCopyCommitId`, and `getChangedFiles`. The scan-skip-when-commit-unchanged optimization is gone. Plugin caches are content-addressed (post-§2a) and the FCS check-result cache now hashes file content directly; together they make the scan-skip path's marginal benefit negligible while removing the only jj runtime reliance.
- **`Daemon.DaemonOptions.EnableJjScanGuard`** — no longer needed; the option is dropped from the public surface.
- **`DaemonConfig.JjFileBackend`** variant — `"jj"` cache config string is still accepted as a legacy alias and falls back to `FileBackend`.
- **BREAKING — `force` parameter removed from scan API:** `Daemon.ScanAll(?force)` → `ScanAll()`, `DaemonRpcConfig.RequestScan: bool -> unit` → `unit -> unit`, `DaemonRpcTarget.Scan(force)` → `Scan()`, `IpcClient.scan pipeName force` → `IpcClient.scan pipeName`. The flag had been a no-op since `JjScanGuard` was deleted.

### Changed

- **`DaemonConfig.createCacheComponents`** return type went back from `(backend, provider, enableJjScanGuard)` triple to `(backend, provider)` pair (the third element was always paired with the now-removed `JjFileBackend`).
- **`DaemonConfig.detectDefaultCacheBackend`** now always returns `FileBackend` (kept for API compatibility; previously returned `JjFileBackend` when a `.jj/` directory existed).
- **`InitConfig.generateConfig`** signature dropped its `hasJj: bool` parameter.

## 0.8.0-alpha.11 - 2026-04-26

### Added

- **`FsHotWatch.ProcessHelper.isDotnetCommand`** and **`mergeDotnetEnv`** — public helpers that detect a `dotnet`/`dotnet.exe` command basename and merge `MSBUILDDISABLENODEREUSE=1` into its env (unless already set).
- `runProcessWithTimeout` now injects the env automatically for `dotnet` commands. Eliminates orphan `MSBuild.dll /nodemode:1` workers across daemon-spawned builds without per-plugin opt-in. See `docs/msbuild-node-reuse-bug.md`.
- **`FsHotWatch.PluginFramework.PluginCtxHelpers.reportOrClearFile`** — collapses the per-file `if entries.IsEmpty then ClearErrors else ReportErrors` idiom shared across analyzer-style plugins.

## 0.8.0-alpha.10 - 2026-04-25

### Added

- **`FsHotWatch.ProcessHelper.ProcessOutcome` DU** (`Succeeded of output` /
  `Failed of exitCode * output` / `TimedOut of after * tail`) replaces the
  historical `bool * string` return on `runProcessWithTimeout` / `runProcess`.
  Callers pattern-match instead of parsing a magic prefix from the output. Helpers:
  `isSucceeded`, `isTimedOut`, `outputOf`.
- **`FsHotWatch.ProcessHelper.WorkOutcome<'a>` DU** (`WorkCompleted of 'a` /
  `WorkTimedOut of after`) replaces `Result<'a, string>` on `runWithTimeout`.
- **`FsHotWatch.Events.TestResult.TestsTimedOut of output * after * wasFiltered`** —
  new variant distinguishing timeout-killed test runs from regular failures.
  `TestResult.isTimedOut` helper added; existing helpers updated to handle the
  new case. `FileTaskCache` round-trips it under the `"timed-out"` JSON tag.
- **`FsHotWatch.ProcessRegistry`** module — per-daemon, `AsyncLocal`-scoped
  registry of live `Process` handles. `Daemon.Dispose` calls `KillAll` so
  `dotnet fs-hot-watch stop` reaps in-flight test runners (and their playwright
  drivers etc.) instead of leaving orphans that contend with the next start.
  `runProcessWithTimeout` registers spawned children and unregisters in
  `finally`.

### Changed

- **BREAKING:** `runProcessWithTimeout` / `runProcess` return `ProcessOutcome`
  (was `bool * string`).
- **BREAKING:** `runWithTimeout` returns `WorkOutcome<'a>` (was `Result<'a, string>`).
- **BREAKING:** `FsHotWatch.ProcessHelper.TimedOutPrefix` literal removed.
  Pattern-match the new DUs.
- **Plugin status visibility sweep.** Plugins are now responsible for calling
  `ctx.CompleteWithSummary` explicitly at the end of each run; the framework
  no longer derives a summary from the last log line or the longest-running
  subtask. `IActivitySink` / `PluginCtx` gain `UpdateSubtask(key, label)` for
  in-place label updates on a long-lived primary subtask without churning
  state. The compact renderer now shows the `"primary"` subtask's descriptive
  label when present, instead of falling back to the activity tail.

## 0.8.0-alpha.9 - 2026-04-23

### Changed (breaking)

- **Test lifecycle events split into three**: `TestCompleted` is replaced by
  `TestRunStarted` (once per run, with `RunId` + `StartedAt`), `TestProgress`
  (per-group delta with `RunId` + `NewResults`), and `TestRunCompleted` (once
  per run, with final cumulative `Results` + `Outcome`). All three share one
  `RunId` per run. Subscribers that only care about end-of-run state read
  `TestRunCompleted.Results`; subscribers that want per-group progress consume
  `TestProgress` deltas and accumulate locally keyed by `RunId`.
- `PluginEvent` adds `TestRunStarted` / `TestProgress` / `TestRunCompleted`;
  drops `TestCompleted`.
- `SubscribedEvent` / `PluginDispatchEvent` gain matching variants; drop
  `SubscribeTestCompleted` / `DispatchTestCompleted`.
- `PluginCtx<_>` and `PluginHostServices` replace `EmitTestCompleted` with
  `EmitTestRunStarted` / `EmitTestProgress` / `EmitTestRunCompleted`.
- `TestResults` kept as a plain value type for internal TestPrune use; no
  longer dispatched as an event.

### Added

- `TestRunOutcome` DU (`Normal` / `Aborted of reason`). Per-project pass/fail
  lives in `TestRunCompleted.Results` (derivable from `TestResult` values).

### Fixed

- `FileCommandPlugin`'s `afterTests` trigger previously used a superset
  heuristic to detect batch boundaries and would silently skip every run
  after the first when project sets were identical (the dominant case for
  stable configs). Now keyed on `RunId`: fires exactly once per distinct run.

- chore: bump upstream tool versions

## 0.8.0-alpha.8 (2026-04-22)

### Added

- `TaskCache.saltedCacheKey` / `TaskCache.optionalSaltedCacheKey` — cache-key
  builders that fold a per-event salt into the commit-based key. Plugins whose
  cache validity depends on state beyond the commit (e.g. a config file whose
  edits don't change the commit) can salt with a hash of that state. Empty salt
  produces the pre-existing key format, so on-disk cache compatibility is
  preserved.

### Changed

- **BREAKING (IPC)**: `WaitForComplete` RPC now accepts a `timeoutMs: int` argument; `<= 0` means no client-imposed timeout. `DaemonRpcConfig.WaitForAllTerminal` signature changed from `unit -> Task<unit>` to `TimeSpan -> Task<unit>` so clients can pass their own deadline. The daemon's previous hard-coded 30-minute cap no longer applies when the client supplies a timeout.

### Fixed

- `PluginFramework.registerHandler`: when a handler's `Update` throws, the framework now auto-reports `PluginStatus.Failed(ex.Message, now)` in addition to logging. Previously an uncaught handler throw left the plugin in whatever transient status it had reported beforehand (classic case: TestPrune reports `Running`, hits a schema-drifted DB, never transitions further → UI shows "running" forever). This is a structural fix: no plugin author can forget to do it, and no plugin can leave an observable status non-terminal due to an exception inside its handler.

## 0.8.0-alpha.3 (2026-04-18)

### Added

- Project-discovery diagnostics: `Ionide.ProjInfo.IWorkspaceLoader.Notifications` is now
  subscribed during `discoverAndRegisterProjects` so per-project design-time failures
  (e.g. `ProjectNotRestored`, `ReferencesNotLoaded`) are logged instead of silently dropped
- Per-project FCS options dumped to `.fshw/logs/projinfo/<Project>.opts.txt` after every
  discovery pass. Contains source files, `OtherOptions` (incl. `-r:` references), and
  referenced project outputs. Registration log line now includes the `-r:` reference count
- `FSHW_PROJINFO_BINLOG=1` env var enables MSBuild binary-log capture at
  `.fshw/logs/projinfo/binlogs/<project>.binlog` for diffing design-time eval vs `dotnet build`
- `PathFilter` module with shared path filtering utilities:
  - `isGeneratedPath` — checks if a path is inside obj/ or bin/ directories
  - `isExcludedPath` — gitignore-style glob matching via the `Ignore` package (replaces string-contains matching)
  - `loadIgnoreFile` / `collectIgnoreRules` — load .gitignore and .fantomasignore files and combine into a single predicate
  - `IgnoreFilterCache` — caches ignore rules per repo root, auto-reloads when files change on disk
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` — exclude entire project trees from discovery using gitignore-style globs
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/ directories

### Changed

- `Daemon.performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering across Watcher, CheckPipeline, and Daemon consolidated through `PathFilter` module

### Dependencies

- Added `Ignore` 0.2.1 (gitignore-style pattern matching, same package used by Fantomas)

---

## 0.5.0-alpha.1 (2026-04-12)

### Added

- Enable TransparentCompiler for hash-based deterministic FCS caching (`useTransparentCompiler = true`)
- Parse `#nowarn` directives to suppress FCS TransparentCompiler warnings (workaround for dotnet/fsharp#9796)
- Plugin teardown support in `PluginHandler` (disposes semaphores, CTS, DB handles)

### Changed (Breaking)

- Type safety overhaul: `AbsFilePath`/`AbsProjectPath` single-case DUs replace raw strings; `PluginName` single-case DU with uniqueness check; `ContentHash` wrapper; `CommandOutcome` DU replaces `Succeeded: bool` + `Output: string`; `FileCheckState` DU replaces `CheckResults option`; `AffectedTestsState` DU; `RerunIntent` DU; `Set<SubscribedEvent>` replaces `PluginSubscriptions` bool record; `TaskCacheKey` struct replaces string key; `TestExtensionKind` DU; `CacheClearFilter` DU
- Plugin registration uses `PluginHostServices` record instead of multi-param function
- `Daemon` changed from F# record to class with `internal` constructor
- `IProjectGraphReader` interface decouples `BuildPlugin` from mutable `ProjectGraph`
- `BuildPhase` folds `PendingFiles` into `IdlePhase` (only meaningful when idle)

### Improved

- Extract pure filtering functions from `MacFsEvents` for testability
- `Watcher` accepts injectable `isMacOS` flag for cross-platform testability

### Changed (Breaking)

- `IProjectGraphReader` adds `GetProjectsForFile` method returning `AbsProjectPath list`
- `ProjectGraph.fileToProjects` now stores all projects per file (was `fileToProject` storing one)
- `CheckPipeline.projectOptionsByFile` stores all project options per file (list instead of single)
- New `CheckPipeline.CheckFileWithOptions` method for checking a file with explicit project options
- New `CheckPipeline.GetProjectOptions` method

### Fixed

- Propagate cancellation token into `CheckFileCore` — `CancelPreviousCheck` now actually stops in-flight FCS checks (previously only checked at entry, not around the expensive FCS call)
- Handle shared source files (linked items): a file appearing in multiple projects now triggers re-checks in all projects, not just the last-registered one
- `Daemon` implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose — agents previously ran indefinitely, keeping processes alive after tests
- `RunWithIpc` races initial scan against cancellation to prevent test-process hangs when `cts` is cancelled during slow `ScanAll`
- Standalone files not in any project now checked via uncovered-files fallback

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure and tooling release. No public API changes.

- CLI moved under core's shared tag in `semantic-tagger.json` — CLI now versions and releases together with the core package
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No public API changes beyond dependency bumps.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1
- Bump `Ionide.ProjInfo` / `Ionide.ProjInfo.FCS` 0.68.0 → 0.74.2
- Bump `FSharp.Data.Adaptive` 1.2.16 → 1.2.26

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)
- Update `Ionide.ProjInfo` to 0.74.2 (may affect project loading behavior)
- No FsHotWatch API signature changes

---

## 0.1.0-alpha.3 (2026-03-28 → 2026-04-02)

Severity-aware diagnostics, FCS warning reporting, MSBuild diagnostic parsing.

- **Breaking:** `ErrorLedger.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `ErrorLedger.Count()` removed → use `FailingReasons(warningsAreFailures: bool)` which returns `Map<string, (string * ErrorEntry) list>`
- **Breaking:** `PluginHost.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `PluginHost.ErrorCount()` removed → use `FailingReasons(warningsAreFailures: bool)`
- **Breaking:** IPC method `GetErrors` renamed to `GetDiagnostics` (both server and client)
- **Breaking:** `Daemon.create` gains `fcsSuppressedCodes: int list option` parameter (pass `None` for default — suppresses FS1182)
- **Behavioral change:** FCS now reports all diagnostic severities (Error, Warning, Info, Hidden), not just errors. Warnings will appear in the error ledger. Use `--no-warn-fail` or filter by severity if this is unwanted.
- Add `ErrorEntry.isFailing` helper for severity-aware failure checks

### Migration from 0.1.0-alpha.2

```fsharp
// ErrorLedger: before
ledger.HasErrors()
ledger.Count()

// ErrorLedger: after
ledger.HasFailingReasons(false)       // errors only
ledger.HasFailingReasons(true)        // errors + warnings
ledger.FailingReasons(false)          // get failing entries

// PluginHost: same pattern
host.HasFailingReasons(false)
host.FailingReasons(false)

// IPC client: before
IpcClient.getErrors proxy

// IPC client: after
IpcClient.getDiagnostics proxy

// Daemon.create: add fcsSuppressedCodes parameter
Daemon.create(config, plugins, fcsSuppressedCodes = None)
```

**Behavioral change:** FCS warnings now appear in the error ledger. If your workflow only expected errors, either:
- Pass `warningsAreFailures = false` to `HasFailingReasons`/`FailingReasons`
- Use `--no-warn-fail` CLI flag
- Filter `ErrorEntry` items by severity

---

## 0.1.0-alpha.2 (2026-03-28)

Subcommands, `--run-once` mode, `CommandCompleted` events, build dependencies, config enhancements.

- **Breaking:** `PluginEvent<'Msg>` gains `CommandCompleted of CommandCompletedResult` case — exhaustive matches must handle it
- **Breaking:** `PluginSubscriptions` gains `CommandCompleted: bool` field — direct construction must include it (or use `PluginSubscriptions.none`)
- **Breaking:** `PluginCtx` gains `EmitCommandCompleted: CommandCompletedResult -> unit` field
- **Breaking:** `CachedEvent` gains `CachedCommandCompleted` case
- Add `CommandCompletedResult` type and full event pipeline
- Add `Daemon.RunOnce()` for single-pass in-process mode

### Migration from 0.1.0-alpha.1

```fsharp
// PluginSubscriptions: add CommandCompleted field
{ PluginSubscriptions.none with FileChanged = true; CommandCompleted = false }
```

Handle new union cases in exhaustive matches:
```fsharp
match event with
| FileChanged _ -> ...
| BuildCompleted _ -> ...
| CommandCompleted _ -> ()  // new
// etc.
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Daemon with FSharpChecker warm cache
- File watcher with source/project/solution change detection
- Plugin host with event dispatch and command registration
- IPC server/client over named pipes (StreamJsonRpc)
- Preprocessor pipeline (format-on-save runs before events dispatch)
- Debounced file changes (500ms source, 200ms project)
- ProjectGraph for cross-project dependency tracking
- TaskCache for event deduplication
