# Cold-workspace benchmark: analyzer identity from the compiler's receipt

Measured 2026-09-16 on the maintainer's macOS box (arm64, dotnet 10.0.302), one
run at a time. The box was NOT quiet: another gate ran alongside for part of the
session, and the 1-minute load average at the start of each run is recorded below —
read the wall-clock numbers as an order of magnitude and the miss counts as exact.

## What is measured

Each run creates a fresh jj workspace, builds (`mise run build`, which includes
`build-analyzers`), runs one `check` through the daemon with debug logging, reads
`.fshw/verdict.json` and `logs/daemon.log`, stops the daemon, and forgets the
workspace. From the verdict: `observedElapsedMs`, the `plugin.analyzers` spans
(count, total ms, how many carry `(cached)`), and the `plugin.test-prune` spans.
From the daemon log: every `[task-cache] plugin=analyzers hit=…` line, with the
miss reason (`no-entry`, or `inputs-changed:<labels>` naming the key slots that
differ from the entry already stored for that file).

Whole-check time is dominated by scan and by test-prune running the full test
suite in a workspace that has no baseline (320–350 s of a ~360 s check). The
claim this benchmark supports is about the **analyzer** entries: their miss count
and the reason named, and the analyzer span time. Nothing here is a wall-clock
claim.

## Conditions

- **before** — `c6c0323c` (FsHotWatch.Cli 0.14.0-alpha.47): byte-keyed
  `analyzer-assemblies` slot (`analyzers-merkle-v4`), and the store namespaced per
  checkout name.
- **after** — the analyzer-identity stack: receipt-keyed `analyzer-inputs`
  (`analyzers-merkle-v5`), the store namespaced per repository, and the
  `analyzer-paths` slot repo-relative with a trailing separator.

Designs:

- **B (same name, different path)** — three workspaces created in turn at
  `.workspaces/r<n>/bench-<cond>`: same directory NAME and depth, so under
  *before* they share one (per-name) store directory and the analyzer DLL is
  salted with a different absolute path each time. This isolates the identity
  change from the namespace change; under *before* it is the ticket's original
  observation.
- **A (distinct names)** — three workspaces `.workspaces/bench-after-A<n>`: the
  acceptance test. Under *before* a distinct name is a private, empty store
  (`no-entry` for every file — one data point taken on unmodified main:
  184 analyzer misses, all `no-entry`).
- **warm** — a second `check` in the third workspace of each design.

## Commands

```
cd <your FsHotWatch checkout>
jj workspace add --name <name> <dir> -r <rev>
cd <dir>
mise exec -- dotnet tool restore
time mise run build
time mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- --log-level debug check
python3 … .fshw/verdict.json logs/daemon.log      # extraction, see below
mise exec -- dotnet run --project src/FsHotWatch.Cli --no-build -- stop
cd - && jj workspace forget <name> && rm -rf <dir>
```

Extraction: `observedElapsedMs` and `timingSpans[scope="plugin.analyzers"]`
(`elapsedMs`, `detail` contains `(cached)`) from the verdict; `plugin=analyzers
hit=true` / `hit=false miss=<reason>` counted from the daemon log. Runs were driven
by a script that did exactly the above, sequentially.

## Results

Analyzer entries per cold `check` (176 files with entries; the 8 `no-entry` misses
recur in every run and are files no run ever stores):

| condition | cold workspace 2 and 3 | reason named |
|---|---|---|
| before (v4, per-name store), same name, different path | 173 misses / 173 | `inputs-changed:analyzer-assemblies,analyzer-paths` |
| before, distinct name (unmodified main, 1 run) | 184 misses / 184 | `no-entry` (a private, empty store) |
| after, same name, different path | 0 misses (352 hits) | — |
| after, DISTINCT names (the acceptance test), 3 workspaces | 0 misses (352 hits) each | — |

Analyzer span time per cold check: 10.2–12.7 s before, 7.2–7.9 s after on a hit
run (the remaining time is the plugin's own scan of 176 files and the 8 uncached
ones). Whole-check wall time is 340–380 s in every condition and is dominated by
test-prune running the full suite in a workspace with no baseline (316–356 s) and
by the scan; it is not a claim of this change. Warm second check in the same
workspace: 18–29 s. The first `after` run in the shared store re-keyed the 173 v4
entries the default checkout had written (`inputs-changed:…,plugin-version`), as
a version bump must.

Full per-run table. `B-*` = same name, different path; `A-*` = distinct names;
`*-warm` = second check in the same workspace. Load average (1 min) at run start
ranged 7–125; the box was shared for part of the session.

| run | check wall s | test-prune s | analyzer spans (cached) | analyzer ms | analyzer hits | analyzer misses (reason) |
|---|---:|---:|---:|---:|---:|---|
| A-main-unfixed-1 | 358.9 | 333 | 11 (3) | 12693 | 176 | 184 (184× `no-entry`) |
| B-before-1 | 366.3 | 341 | 5 (5) | 10226 | 253 | 101 (101× `no-entry`) |
| B-before-2 | 358.4 | 334 | 9 (4) | 12102 | 173 | 181 (173× `inputs-changed:analyzer-assemblies,analyzer-paths`; 8× `no-entry`) |
| B-before-3 | 351.0 | 322 | 9 (3) | 11385 | 173 | 181 (173× `inputs-changed:analyzer-assemblies,analyzer-paths`; 8× `no-entry`) |
| B-before-3-warm | 29.0 | 12 | 2 (2) | 3028 | 173 | 4 (4× `no-entry`) |
| B-after-1 | 349.4 | 324 | 9 (3) | 17679 | 176 | 184 (169× `inputs-changed:analyzer-assemblies,analyzer-inputs,analyzer-paths,plugin-version`; 11× `no-entry`; 3× `inputs-changed:analyzer-assemblies,analyzer-inputs,analyzer-paths,fcs-signature,plugin-version,source`; 1× `inputs-changed:analyzer-assemblies,analyzer-inputs,analyzer-paths,plugin-version,source`) |
| B-after-2 | 372.1 | 350 | 11 (3) | 11914 | 176 | 184 (176× `inputs-changed:analyzer-paths`; 8× `no-entry`) |
| B-after-1 | 381.8 | 356 | 15 (8) | 21415 | 176 | 184 (173× `inputs-changed:analyzer-paths`; 8× `no-entry`; 3× `inputs-changed:analyzer-paths,source`) |
| B-after-2 | 371.7 | 347 | 7 (7) | 7238 | 352 | 8 (8× `no-entry`) |
| A-after-1 | 344.6 | 320 | 6 (6) | 7866 | 352 | 8 (8× `no-entry`) |
| A-after-2 | 340.1 | 316 | 15 (15) | 7453 | 352 | 8 (8× `no-entry`) |
| A-after-3 | 362.5 | 339 | 9 (9) | 7547 | 352 | 8 (8× `no-entry`) |
| A-after-3-warm | 16.8 | 7 | 2 (2) | 3222 | 176 | 4 (4× `no-entry`) |
| B-after-3 | 325.8 | 301 | 10 (10) | 7805 | 352 | 8 (8× `no-entry`) |
| B-after-3-warm | 18.3 | 8 | 3 (3) | 2973 | 176 | 4 (4× `no-entry`) |

`A-main-unfixed-1` is the single distinct-name run taken on unmodified main
before the namespace and trailing-separator fixes. `B-after-1` re-keyed entries
written by an intermediate run (namespace fix only, absolute `analyzer-paths`) and
so misses on `analyzer-paths`; `B-after-2` is the first run against entries with
every fix and hits. One cold run (namespace fix only) went red on
`ProcessHelperTests.runWithCancellableTimeoutTracked keeps token source alive
through late registration`, a timing test, at load average 40; it is not in the
table.

