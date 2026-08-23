# Changelog — FsHotWatch.Build

## Unreleased

## 0.7.0-alpha.27 - 2026-08-23

- AUTOMATION-245: **a cache replay is now actually refused over a MISSING build output
  — in the daemon, where it never was.** The replay gate shipped correct and inert: the
  daemon constructs this plugin with `artifactGateReddens = false` (AUTOMATION-368),
  the arbiter returned `[]` unconditionally in that mode, and every lookup fell through
  to the merkle. One consuming repo's log holds **820** `artifact-gate (report-only)`
  findings, all discarded, including runs where the canonical DLL was simply absent —
  so `built N projects (cached)` kept being asserted about outputs that did not exist.
  Refusing a replay cannot turn anything red (it returns `None` from the cache key —
  the same bypass `force-rebuild` uses, costing one real build whose own result decides
  the colour), so the flag that holds back the *mtime* reading has no jurisdiction over
  a file that is not there. `DllOlderThanSources` stays behind `artifactGateReddens`;
  `DllMissing` now blocks a replay in both modes. Highest-frequency instance closed:
  the first `check` in a brand-new `jj workspace add`, whose sources hash identically
  to the workspace whose entry it hits and whose `bin/` has never existed.
- AUTOMATION-245: a build that reports success and produces no output for a project
  stops that project justifying a bypass, so the new refusal is worth at most **one**
  extra build per unproduced output rather than a rebuild-every-time regression. Said
  out loud the first time each one appears — a project in the graph that the build
  command's solution does not contain is a finding nothing else was reporting.

## 0.7.0-alpha.26 - 2026-08-23

- AUTOMATION-368: no behaviour change here, but the artifact gate this plugin owns can
  now be believed about a quiescent tree — core's `GetMaxSourceMtime` no longer reads
  MSBuild's regenerated `obj/` compile items as source edits. `artifactGateReddens` is
  still `false` in the daemon; promotion waits on one observation window against the
  corrected reading. See [ADR-015](../../docs/adr-015-a-compile-item-is-not-an-edit.md).

## 0.7.0-alpha.25 - 2026-08-19

- AUTOMATION-368: give the artifact gate a real path, and keep it report-only


## 0.7.0-alpha.24 - 2026-08-18

- refactor: **artifact freshness is decided by ONE walk over the project graph**, not
  two. `verifyArtifactsFresh` (which drives the post-build demotion and the cache-replay
  gate) and `artifactCoverageGap` (the floor that says how much of the tree the gate could
  actually examine) each carried their own copy of the same three-way rule — including the
  non-obvious part, that a derivable path whose DLL is *missing* was examined, because
  that IS the finding. The code said so out loud ("Mirrors `verifyArtifactsFresh`'s own
  walk exactly"). Two copies of a rule that must agree is exactly how this gate degrades
  quietly: the stale list goes empty because nothing could be looked at, while the floor
  meant to catch that has drifted about what "looked at" means. Both are now
  `List.choose`d out of a single classification. `artifactCoverageGap` keeps its
  signature and its behaviour; no message text changed.

## 0.7.0-alpha.23 - 2026-08-18

- fix: **the cache-replay gate covers every event that reads the cache** (AUTOMATION-245
  QA rework). The gate added in `0.7.0-alpha.22` — and `force-rebuild` before it
  (AUTOMATION-224) — matched `FileChanged` alone. A build plugin configured with
  `dependsOn` BUFFERS file changes until its dependencies report and starts its build from
  the `CommandCompleted` that satisfies the last one, so in exactly those repos the only
  lookup that decides whether a build runs fell through to an ungated key: neither
  `confirm`'s forced rebuild nor the artifact re-verification applied. Only the `Custom`
  store arm is now exempt, so a recovered build is still cached immediately.

- add: **`BuildPlugin.artifactCoverageGap`, a floor under the freshness check.**
  `verifyArtifactsFresh` returns the projects it found stale, and "nothing is stale" is the
  same value as "nothing could be examined" — so a graph that stops yielding build outputs
  turns the whole guard off while every run stays green. The gap is now reported (once per
  plugin instance, on the `build` log channel at warn level) naming each project and why:
  no build output could be located, or no source was on disk to compare one against. It
  REPORTS rather than refuses — bypassing the cache for a tree it cannot examine would
  wedge that repo into rebuilding every time.

  **This is not hypothetical, and reading it matters more than the two fixes above.** A
  TargetFramework reaches the project graph only through `ProjectGraph.RegisterFromFsproj`,
  which has zero callers outside tests; the daemon registers every project through
  `RegisterProject` (from Ionide's MSBuild evaluation), which records sources and
  references and no framework. So `GetCanonicalDllPath` answers `None` for every project in
  a live daemon, and this plugin's artifact freshness — the replay gate AND the post-build
  `BuildPassed` → `BuildArtifactsStale` demotion — has never examined a single artifact
  outside the test suite. Making the graph carry MSBuild's real target path is the fix;
  it turns on a demotion path that has never run against a real repo, so it wants its own
  change and its own canary rather than a quiet ride here.

- fix: **the build inputs merkle hashes MSBuild's implicit imports** (AUTOMATION-303
  case 2). `Directory.Build.props`, `Directory.Build.targets` and
  `Directory.Packages.props` are inputs to every project beneath them and appeared in
  neither list the merkle hashed — they are not compile items and not projects. A
  `<Compile Include=…>` added there gave the repo a new file while the key stayed
  byte-identical, so the task cache replayed `built N projects (cached)`, nothing
  compiled the file, and the `[fcs]` error beside it was real. Resolved per project by
  MSBuild's own nearest-ancestor rule, so a file MSBuild would not import cannot
  invalidate a build it could not have affected. **Existing build cache entries orphan on
  upgrade** — they vouched for inputs they never hashed.

## 0.7.0-alpha.22 - 2026-08-17

- fix: re-verify artifacts at cache-REPLAY time, so a cache hit can never assert
  freshness it has not confirmed (AUTOMATION-245)

## 0.7.0-alpha.21 - 2026-08-13

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments

- Comment audit: cut AI thinking-out-loud from comments


## 0.7.0-alpha.19 - 2026-08-06

- confirm: force a REAL build — a cache hit must not assert freshness it never verified (AUTOMATION-224)


## 0.7.0-alpha.18 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context


## 0.7.0-alpha.17 - 2026-07-15

- fix!: adapt to the core `RunClaim` / verdict-carrying terminals (AUTOMATION-99): the
  build's `RunExclusive` claim is handled explicitly, and `Running` is reported by the
  framework at the claim.
- refactor!: `BuildDone` drops its `summary` field. It was dead on two of its three arms
  (both crash paths built a `$"build crashed: …"` summary nothing ever consumed) and
  shadowed on the third. The handler now derives the summary from the outcome via the same
  pure `buildSummary` helper the worker logs with, so the log line and the status verdict
  cannot disagree.

- fix!: adapt to core `RunVerdict` (AUTOMATION-99): the build's `Completed` status
  carries its verdict — "built N projects" + the measured build duration — via an
  extended `BuildDone` message; build crashes carry an explicit crash summary.

- fix: build spawns go through the single, always-bounded
  `ProcessHelper.runProcess` (`ProcessBounds.silent buildTimeout`). A build is a
  SILENT child — `dotnet build -v q` prints nothing until it finishes, and a
  `sh -c "dotnet build 2> log; cat log"` wrapper buffers everything to the end —
  so output cannot prove liveness and no launch deadline is applied (one would
  false-kill a healthy slow build). What it DOES gain is the polled-exit and the
  bounded post-exit drain: a build whose grandchild MSBuild node holds the
  inherited stdout pipe after the child exits no longer wedges the daemon.

## 0.7.0-alpha.16 - 2026-06-30

- refactor: collapse the now-degenerate single-case `BuildPhase` union into the
  `BuildState` record. After `WaitingForBatchPhase` was removed in alpha.15, the
  union had a single `IdlePhase` case; its payload now lives directly on
  `BuildState` as `PendingFiles` and `LastBuild`. Internal state shape only — no
  behavioural change — but `BuildPhase` is removed from the public surface.

## 0.7.0-alpha.15 - 2026-06-24

- fix: a test-file-only change now runs a real build instead of skipping MSBuild and waiting for FCS's `BatchChecked` signal. The skip emitted `BuildSucceeded` on the in-memory type-check alone, so the on-disk test DLL was never re-emitted — and `test-prune`'s `dotnet run --no-build` then executed the **stale** binary, reporting a false green for a freshly-edited test. Every source change (test files included) now drives MSBuild so the DLL is re-emitted and `verifyArtifactsFresh` runs before `BuildSucceeded`. `WaitingForBatchPhase` is removed (`BuildPhase` collapses to a single `IdlePhase`); the plugin no longer subscribes to `BatchChecked`. See ADR-012 (amends ADR-002).

## 0.7.0-alpha.14 - 2026-06-17

- fix: surface per-project stale-artifact detail in the live build log.
- docs: README early-alpha status-note pass.

## 0.7.0-alpha.13 - 2026-06-08

- fix: content-hash build inputs so mtime-preserving edits invalidate the cache. `BuildInputsHasher.hashFile` previously memoized the content hash under `(path, mtime)`; an `rsync -a` / `cp -p` / branch-switch / in-place rewrite that preserves mtime returned the stale hash, so the build merkle never moved and a stale `BuildDone` (an FS1178 phantom) replayed forever. Every input is now content-hashed on each Compute — mtime is never trusted as a content oracle.

## 0.7.0-alpha.12 - 2026-05-28

- chore: refresh transitive dependencies (CommandTree 0.5.1, CoverageRatchet.Core 0.1.0-alpha.2, TestPrune.Core 4.0.2, FSharpLintAnalyzerShim 0.3.0-alpha.3 via the lint shim).

## 0.7.0-alpha.11 - 2026-05-04

- feat: `BuildOutcome.BuildArtifactsStale` — new variant emitted when MSBuild reports success but canonical DLLs are missing or older than their newest source; downstream plugins can trust `BuildSucceeded` as a guarantee of artifact freshness
- feat: `StaleArtifact` / `StaleReason` types carry structured diagnostics for stale-artifact reporting
- refactor: `ProjectDirtyTracker` removed; staleness now enforced inline by BuildPlugin post-build verification

## 0.7.0-alpha.10 - 2026-04-29

### Added

- **Post-build artifact verification.** A successful `BuildPassed` outcome now means every project's compiled DLL is fresh relative to its sources. The async worker walks `graph.GetAllProjects()` after `decideBuildOutcome` returns `BuildPassed` and demotes to `BuildArtifactsStale` whenever the canonical DLL is missing or older than the newest source — catching MSBuild's incremental cache silently failing to update artifacts. Downstream plugins (TestPrune, etc.) can therefore trust `BuildSucceeded` as a guarantee of artifact freshness and drop their own staleness logic.
- **`StaleArtifact` / `StaleReason` types** carry the structured diagnostic so cache replay reproduces the same per-project messages deterministically.
- **`formatStaleArtifact` / `staleDiagnostic`** format the typed stale list into the human-readable error-ledger entry.

### Changed

- **BREAKING:** `BuildOutcome` gained a third case, `BuildArtifactsStale of stale: StaleArtifact list * output: string`. Pattern-match exhaustiveness will fail for callers that previously handled only `BuildPassed | BuildOutputFailed`.
- **BREAKING:** `create` no longer takes `dirtyTracker`. With staleness enforced inline by post-build verification, the dirty-bit handoff between BuildPlugin and TestPrunePlugin is no longer needed. Drops the 9th positional argument (was: `... dependsOn → timeoutSec → dirtyTracker`; now `... dependsOn → timeoutSec`).
- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed. New positional order drops the 8th argument (was: `... dependsOn → getCommitId → timeoutSec → dirtyTracker`; now `... dependsOn → timeoutSec`).

### Removed

- `markDirty` / `clearFreshProjects` plumbing for the dirty tracker. Replaced by post-build mtime verification.

### Fixed

- **Cold-start cache bypass.** BuildPlugin's cache key returns `None` until the first build completes in the daemon session, so a stale on-disk entry from a prior session can't pre-empt the cold-start build.
- **Skip cache for `FileChecked` events.** BuildPlugin no longer reads/writes the task cache for `FileChecked` events — only for `FileChanged`. `FileChecked` doesn't drive the build path and the cache lookup was producing spurious hit/miss noise.

## 0.7.0-alpha.9 - 2026-04-26

### Added

- **`formatSilentFailureDiagnostic`** — surfaces exit code, output size, and any `Time Elapsed` tail when `dotnet build` exits non-zero with no parseable diagnostics (typically MSBuild bailing during evaluation/restore).

### Changed

- The MSBUILDDISABLENODEREUSE env injection moved to `ProcessHelper.runProcessWithTimeout` (core), so the build plugin no longer maintains its own copy. Behavior is identical from the caller's perspective.

## 0.7.0-alpha.8 - 2026-04-25

### Fixed

- **Skip-for-test-files-only path no longer races FCS.** When `SourceChanged`
  carries only test files, the build plugin used to emit `BuildSucceeded`
  immediately. Downstream test-prune dispatched off stale `AffectedTests`
  before FCS finished checking the changed file, so partial test runs were
  silently skipped. Plugin now subscribes to `FileChecked`, transitions into
  a new `WaitingForFcsPhase` carrying the awaiting set (path-normalized via
  `Path.GetFullPath`), and emits `BuildSucceeded` only when every changed
  file has produced a `FileChecked`.

### Changed

- **BREAKING:** `BuildPhase` DU gains a `WaitingForFcsPhase` variant.
  Consumers that pattern-match `BuildPhase` must add a case for it.
- Subscriptions: build plugin now subscribes to `SubscribeFileChecked` in
  addition to `SubscribeFileChanged`.
- Timeout-handling: build failures induced by exceeding the configured
  `timeoutSec` are reported via the `ProcessOutcome.TimedOut` case
  (replacing the prior `output.StartsWith TimedOutPrefix` heuristic).
- Emit a `build failed: N errors` summary on the failure path (previously the
  failure case relied on the now-removed last-log-line summary fallback).

## 0.7.0-alpha.7 - 2026-04-23

- chore: bump upstream tool versions

## 0.5.0-alpha.1 (2026-04-12)

*No changes since 0.3.0-alpha.1.*

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; enable `TreatWarningsAsErrors`; replace bespoke scripts with shared NuGet tools and reusable CI workflows

---

## 0.1.0-alpha.3 (2026-04-02)

- Build plugin now reports parsed MSBuild diagnostics via new `BuildDiagnostics.parseMSBuildDiagnostics` — structured `ErrorEntry` items with severity, file, line, column instead of raw text

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `BuildPlugin.create` gains required `dependsOn: string list` parameter (pass `[]` for no dependencies)
- **Breaking:** `BuildState` gains `SatisfiedDeps` and `PendingFiles` fields
- Build dependency ordering — build waits for named `CommandCompleted` events before starting

### Migration from 0.1.0-alpha.1

```fsharp
// BuildPlugin.create: add dependsOn parameter
BuildPlugin.create(buildTemplate, dependsOn = [])
```

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Build plugin wrapping `dotnet build` with concurrent-build guard
