# Handoff: the FsHotWatch.Tests coverage-load CI flake

Status: **Open investigation** (2026-06-16). Mechanism narrowed; exact failing
test not yet named. This is a handoff — anyone can pick it up. **Read the "Red
herrings" section first** — this suite is full of intentional-failure fixtures
whose output mimics real failures, and they have repeatedly misled investigation
(including mine).

## TL;DR / current best understanding

`FsHotWatch.Tests` intermittently fails **only** in CI's `dotnet test --coverage`
step (~1 in 6 runs). It does **not** reproduce when the test DLL is run directly
(1442/1442, repeatedly, even under CPU saturation) — coverage instrumentation
slows execution and reshuffles timing/interleaving, which is what tips it over.

Two **non-fixture** error signatures survive filtering and are the live suspects:

1. **`Failed to start daemon` / `Daemon did not respond in time. It may be busy or
   hung`** — a CLI/program test (`fshw-prog-*` / `fshw-cli-*` temp dirs) starts a
   daemon and the connect/startup wait times out under load. Most likely culprit.
2. **`[test-prune] Plugin handler failed: System.NullReferenceException`** (recurs
   several times in one failing run) — the known non-deterministic FCS/"Object
   reference not set" diagnostic under churn (see the FsHotWatch memory on FCS
   internal-error diags; also `adr`-adjacent notes). May be a second, independent
   flake, or downstream of #1.

The fix will be a **timeout / startup-robustness** change (bump the daemon connect
timeout, or make it adaptive to load), and/or hardening the test-prune handler
against the FCS NRE — **not** a stale-artifact or `[<Fact(Timeout=N)>]` change.

## Red herrings — do NOT chase these (each cost a prior investigator hours)

- **`build failed: 1 stale artifacts` / `MyLib: DLL <t> older than newest source <t+10min>`.**
  This is the *expected output* of `runVerifyHarness "build-verify-stale-demotion"`
  (`tests/FsHotWatch.Tests/BuildPluginTests.fs:939`, called at `:983` with
  `dllOffset = -10min`), which deliberately back-dates a fake `MyLib.dll` by exactly
  10 minutes to assert that `verifyArtifactsFresh` *correctly detects* staleness. The
  round 10-minute delta is the tell. `verifyArtifactsFresh`
  (`src/FsHotWatch.Build/BuildPlugin.fs:213`) is working **correctly** — it is NOT the
  flake. (An earlier hypothesis that this defensive check was the root cause is
  **wrong**; do not "clean it up" to silence the flake.)
- **`[<Fact(Timeout=2000)>]` tests "timing out".** Also wrong. The 28 tests at 2s are
  trivial/fast (string-matching, hashing); the heavy tests are already at 15s. Bumping
  them was tried and reverted — it changes nothing.
- **Intentional-failure fixtures** whose stderr/stack traces flood `--log-failed`:
  `simulated processBatch bug`, `reporter boom`, `runTests failed: beforeRun boom`,
  `RunExclusive 'k' work failed: boom`, `throwInvalidOp/throwArg/throwIo`
  (`CheckCacheTests.fs:455-480`), `createWithSlowHook` + `AnalyzerProjectOptions ctor
  failed: NRE` (`AnalyzersPluginTests` slow-hook timeout fixture),
  `CancellationTokenSource has been disposed` (teardown-cancels-CTS fixture),
  `Daemon already running … (pid 99999)` (mock pid), `fshw-prog-daemon-down-*` /
  `fshw-cli-fail-*` (deliberate failure-scenario tests). All of these print
  failure-shaped output **on success**. Filter them out before drawing conclusions.

## The actual blocker, and the concrete next step

CI's aggregate log does **not** cleanly name the failed *test method* — it's buried
under the fixtures above, and the `dotnet test` step doesn't surface it. **End the
guessing by capturing the MTP test report:**

1. In the reusable workflow `michaelglass/MichaelsWackyFsPackageTools`
   `.github/workflows/michaels-wacky-build.yml`, the "Test with coverage" step
   (`dotnet test … --coverage …`), add a TRX report:
   `--report-trx --report-trx-filename $project_name.trx` (MTP v2 syntax), and let the
   existing `failure-artifacts` upload (added 2026-06-16, default globs `.fshw/...`)
   also glob `**/*.trx` — or just add `**/*.trx` to FsHotWatch's `ci.yml`
   `failure-artifacts` input.
2. Re-run CI until it flakes (loop below). The TRX names the failed test method +
   its assertion. **That** pins the test; then read its body and fix the
   timeout/race.

Until that lands, you're triaging aggregate stdout, which this suite makes unreliable.

## How to reproduce / catch it

Does not reproduce locally on demand (mac arm64, x64 Linux Docker, direct DLL run all
pass — verified extensively across 8+ attempts). Catch it on CI (rate ~1/6):

```bash
rid=$(gh run list -R michaelglass/FsHotWatch --branch main --limit 1 --json databaseId -q '.[0].databaseId')
for i in $(seq 1 8); do
  gh run watch $rid -R michaelglass/FsHotWatch --exit-status && { gh run rerun $rid -R michaelglass/FsHotWatch; sleep 12; continue; }
  echo "caught on attempt $i"; break
done
# then, once the TRX upload (next-step #1) is in place:
gh run download $rid -R michaelglass/FsHotWatch -n failure-diagnostics
```

To triage the raw log meanwhile, strip the fixtures:

```bash
gh run view <run> -R michaelglass/FsHotWatch --log-failed \
  | sed -E 's/\x1b\[[0-9;]*m//g' \
  | grep -iE "Failed to start daemon|did not respond in time|Plugin handler failed: System.NullReference" \
  | grep -viE "boom|simulated|daemon-down|cli-fail|pid 99999"
```

## Diagnostic instrumentation already shipped (don't re-derive)

- `formatFailureReport` (`TestPrunePlugin.fs`, commit `31a412ec`) — names failing tests
  in the **test-prune daemon** path + dumps the output tail when a run fails with no
  per-test line. This is what first revealed "it's not a test timeout."
- `staleDiagnostic` now logged to `ctx.Log` (`applyBuildOutcome`, commit `ea1b3d5f`) —
  surfaces the per-project stale **detail**. Useful in general; for *this* flake it only
  confirmed the `MyLib` line is the fixture (the 10-min delta).
- CI `failure-artifacts` upload (reusable workflow, commit `3c141bd4`) — uploads
  `.fshw/test-runs/**` + `.fshw/**/*.log` on `failure()`. **Gap:** the failing
  diagnostics for a build/daemon failure *inside a test* live in the `dotnet test`
  stdout, not `.fshw/`, so it uploads nothing for this mode. The TRX step above closes
  that gap.

## On the `verifyArtifactsFresh` defensive code (the original question)

It is **not** flaking — it correctly catches the `MyLib` fixture. The "should we clean
it up?" question is therefore *not* driven by this flake. If you still want to evaluate
it on its own merits (it guards against MSBuild incremental-cache lies — see
`adr-008-mtime-is-not-a-content-oracle.md`, `docs/msbuild-node-reuse-bug.md`), the bar
is: does the content-hash merkle (`BuildInputsHasher`) now subsume the temporal check,
and have the upstream MSBuild bugs it was added for been fixed past the SDK floor? If
both, delete it and update ADR-008. But that's orthogonal to the CI flake.

## Anchors

- Flake signatures: `Failed to start daemon` / `Daemon did not respond in time`
  (daemon connect/startup wait — grep `src/FsHotWatch.Cli/` for the connect timeout);
  `[test-prune] Plugin handler failed: System.NullReferenceException`
  (`src/FsHotWatch.TestPrune/TestPrunePlugin.fs` handler; FCS-churn NRE).
- Red-herring fixture: `tests/FsHotWatch.Tests/BuildPluginTests.fs:939` (`runVerifyHarness`).
- Defensive check (NOT the flake): `src/FsHotWatch.Build/BuildPlugin.fs:213`
  (`verifyArtifactsFresh`); ADR `docs/adr-008-mtime-is-not-a-content-oracle.md`.
- First clean capture: FsHotWatch CI run `27619217113` (2026-06-16); the original
  `27614624285` rerun 2 is where the cascade was first seen.
