# Watch: intermittent `build failed: 1 stale artifacts` CI flake

Status: **Open investigation** (2026-06-16). Root-cause *mechanism* identified;
exact mtime race not yet pinned. Diagnostic instrumentation shipped so the next
occurrence is fully self-describing. This doc is a handoff — anyone can pick it up.

Related: [`adr-008-mtime-is-not-a-content-oracle.md`](adr-008-mtime-is-not-a-content-oracle.md)
(the ADR that *justifies* the defensive check this flake lives in).

## TL;DR

`dotnet fshw check` / the test suite intermittently fails on CI with
`build failed: 1 stale artifacts`. It is **NOT** a test-timeout flake (an earlier
hypothesis — disproven). It is a **false positive** in the BuildPlugin's post-build
freshness check (`verifyArtifactsFresh`), firing on **exactly one project** at a
time, only under **coverage instrumentation** (the slower timing). It is benign
(the build is actually fine) but it reds the suite. Do not "fix" it by bumping test
timeouts — that was tried and reverted; it's the wrong layer.

## The defensive code (what's firing)

`src/FsHotWatch.Build/BuildPlugin.fs`:

- **`verifyArtifactsFresh ()`** (~`:213`) — after a build the subprocess reports as
  *succeeded*, this stats every project's canonical DLL and compares
  `File.GetLastWriteTimeUtc dll` against `graph.GetMaxSourceMtime proj` (~`:227`).
  If `dllTime < srcTime` it yields `DllOlderThanSources(dllTime, srcTime)`. Any
  non-empty result demotes `BuildPassed` → `BuildArtifactsStale` (`verifyAndDemote`,
  ~`:293`), which `applyBuildOutcome` (~`:261`, the `BuildArtifactsStale` arm `:268`)
  turns into the `build failed: N stale artifacts` error.
- **Purpose** (per ADR-008): catch MSBuild's incremental cache *lying* — a DLL that
  wasn't rebuilt when its sources changed. mtime is the *temporal* complement to the
  content-hash merkle (`BuildInputsHasher`), which is the *content* guard. The check
  is deliberate, not accidental — see the long comment at `BuildPlugin.fs:205-212`.

## What's confirmed

1. The CI failure surfaces as `build failed: 1 stale artifacts` (captured 2026-06-16,
   FsHotWatch CI run `27614624285` rerun #2). The `[test-prune] … failed: 1` lines are
   *downstream* noise — the test-prune run is marked failed because the build it
   depended on failed; no actual test failed (the "no per-test 'failed' line parsed"
   backstop confirms this).
2. It fires on a **temp project built inside a FsHotWatch.Tests test** (fixtures named
   `ProjA`/`ProjB`/`FlipProj`/`P2`/`TestProject`), not the real solution. The real
   `check --run-once` (CI "Lint" step) passed in the same run.
3. It is **coverage-specific**: the suite passes when the test DLL is run directly
   (1442/1442, repeatedly, even under CPU saturation). It only flakes under
   `dotnet test --coverage` — the instrumentation slows execution and shifts the race.
4. Rate is roughly **~1 in 6 CI runs** (caught on rerun 2 of a 6-run loop).

## What is NOT yet known (the open question)

**Why is one temp project's DLL mtime older than its newest source mtime right after
a successful build?** Candidate mechanisms, in rough likelihood order:

- **(A) A source touched *after* the DLL.** Something bumps a source's mtime post-build
  — the format preprocessor reformatting a fixture source, or an MSBuild target writing
  a generated `obj/**/*.fs` that `GetSourceFiles` counts. If true, the DLL isn't stale;
  the source change is just pending for the *next* build. → Fix: snapshot source mtimes
  at build **start** and compare against those, or exclude generated/obj sources from
  `GetMaxSourceMtime`.
- **(B) Sub-resolution timestamp jitter.** DLL and source land within filesystem
  mtime granularity and the strict `<` trips on microsecond ordering. → Fix: require a
  meaningful margin (e.g. DLL older by > ~1s) before flagging — defensible without
  violating ADR-008 (still mtime-based, just tolerating sub-resolution noise).
- **(C) A genuine concurrent-build race** in the temp-project test harness (two builds
  racing the same output dir). → Fix in the test setup, not the plugin.

The **mtime delta** distinguishes these: sub-second ⇒ (B); seconds-to-minutes ⇒ (A) or
(C). That delta is exactly what the instrumentation below now surfaces.

## What to watch for (the data that resolves this)

The next CI failure now prints, in the failed job log (and in the test output that
captures the in-process daemon's log):

```
<ProjectStem>: DLL <dllTimeUtc> older than newest source <srcTimeUtc>
```

Grab it with:

```bash
gh run view <run-id> -R michaelglass/FsHotWatch --log-failed \
  | grep -iE "older than newest source|DLL missing at"
```

Read the delta `srcTime - dllTime`:
- **< ~1s** → mechanism (B), apply the margin fix.
- **≥ ~1s** → mechanism (A): find *which* source has the late mtime (is it under `obj/`?
  is it a `.fs` the format preprocessor would rewrite?), then snapshot-at-build-start or
  exclude that source class.

Also identify **which test** built that fixture (grep the fixture stem, e.g. `FlipProj`,
in `tests/FsHotWatch.Tests/`) — it pins the harness and whether the format preprocessor
is wired in that test.

## Diagnostic instrumentation already shipped (so you don't re-derive it)

- `fix(test-prune): surface failing test names in CI output` (commit `uwnmuyqs`/`31a412ec`,
  `TestPrunePlugin.fs` `formatFailureReport`) — names failing tests + backstop-dumps the
  output tail when a run fails with no per-test line. This is what revealed the build (not
  test) failure.
- `fix(build): surface per-project stale-artifact detail in the live log` (commit
  `ea1b3d5f`, `applyBuildOutcome`) — logs the full `staleDiagnostic` (project + mtime
  delta) to `ctx.Log`, not just the count. **This is the line you'll read above.**
- CI artifact upload (`MichaelsWackyFsPackageTools` reusable `michaels-wacky-build.yml`,
  commit `3c141bd4`): a `failure-artifacts` input (default `.fshw/test-runs/**` +
  `.fshw/**/*.log`) uploads daemon diagnostics on `failure()`. **Known gap:** for a
  build-failure-*inside-a-test* the detail is in the test's stdout (the job log), not
  `.fshw/` — so the upload is empty for *this* failure mode. It still helps for real
  `check`-step (daemon) failures. Consider also globbing the temp-project build dirs if
  the test writes its daemon log to disk.

## How to reproduce / catch it

It does not reproduce locally on demand (mac arm64, x64 Linux Docker, direct DLL run all
pass — verified extensively). Catch it on CI instead:

```bash
rid=$(gh run list -R michaelglass/FsHotWatch --branch main --limit 1 --json databaseId -q '.[0].databaseId')
for i in $(seq 1 8); do
  gh run watch $rid -R michaelglass/FsHotWatch --exit-status && { gh run rerun $rid -R michaelglass/FsHotWatch; sleep 12; continue; }
  echo "caught on attempt $i"; gh run view $rid -R michaelglass/FsHotWatch --log-failed | grep -i "older than newest source"; break
done
```

## When to clean up / remove this defensive code

`verifyArtifactsFresh` is a guard against a *specific* historical failure (MSBuild
incremental cache returning a stale DLL). Revisit removing or relaxing it if **any** of:

- **It out-flakes the bug it catches.** If, over a meaningful window, this check produces
  only false positives (this flake) and zero true catches of an actually-stale DLL, its
  cost exceeds its value — relax it (margin) or gate it behind a debug/CI-only opt-in.
- **The content-hash merkle subsumes it.** ADR-008's `BuildInputsHasher` already forces a
  rebuild on content change with preserved mtime. If analysis shows the merkle now covers
  every case `verifyArtifactsFresh` was added for (i.e. the temporal check can no longer
  catch anything the content check misses), delete it and update ADR-008.
- **MSBuild stops lying.** The check was added for a concrete MSBuild incremental bug
  (see `docs/msbuild-node-reuse-bug.md`, `docs/leaked-msbuild-env-bug.md`). If those are
  fixed upstream and the SDK floor is raised past them, the guard may be obsolete.

Do **not** remove it just to silence this flake — first fix the false positive (above),
*then* evaluate removal on its own merits. Record the decision in ADR-008.

## Anchors

- `src/FsHotWatch.Build/BuildPlugin.fs`: `verifyArtifactsFresh` `:213`, `GetMaxSourceMtime`
  call `:227`, `applyBuildOutcome` `BuildArtifactsStale` arm `:268`, `verifyAndDemote` `:293`.
- `src/FsHotWatch/ProjectGraph.fs`: `GetMaxSourceMtime` `:223` (what counts as a "source").
- ADR: `docs/adr-008-mtime-is-not-a-content-oracle.md`.
- First captured occurrence: FsHotWatch CI run `27614624285` (rerun 2), 2026-06-16.
