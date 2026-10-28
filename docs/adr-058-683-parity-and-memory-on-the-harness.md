# ADR-058: AUTOMATION-683 measured: same work, about half the memory with a consumer, no wall-clock gain from dropping tier barriers

Status: Accepted (2026-10-28). AUTOMATION-683 slice 5, the closing measurement.

## Question

683's last open acceptance item is "coverage/diagnostic parity matches the baseline
harness; no memory win comes from doing less work." The coordinator also asked whether
slice 3 (ADR-056, dependency-ordered checks) shows a wall-clock gain, and what the RSS is.

## Harness change

`bench/two-session/run.sh` gains `CHAINS` and `SMALL_FILES`:

- It adds a chain of `CHAINS` projects, each of `SMALL_FILES` chained files, `S1 <- S2 <- …`,
  beside the big `Synth` project.
- An `All` project references `Synth` and the last project of the chain. The build
  plugin builds `All`.
- `Synth` and `S1` share the first tier, so under a tier barrier `S2` waits for `Synth`,
  and under ADR-056 it does not.

The harness also writes `scan.tsv`: each session's cold scan, read from its own
`.fshw/scan-metrics.jsonl`. The columns are duration, files checked, FCS check starts,
retry rounds, and the RSS at the end of the scan. For a hosted session that RSS is the
host's, covering both sessions.

## Builds compared

- **base:** main 1e5f7132, before any 683 slice.
- **s2:** main e403c4e8. Slices 1 and 2: live-result bound, streamed cascade. Tiers are
  still barriers.
- **s4:** main cd84e2c4. Slices 1 to 4: adds ADR-056 dependency ordering.

All runs: the default 240-file `Synth`, `DURATION=0` (cold scans only), check cache off,
on a 12-processor machine (live bound 24). Runs alternate builds, 3 repetitions each,
each behind `idlegate.sh 60`.

## Results

### Lint off, `CHAINS=6 SMALL_FILES=5` (271 files per session)

| build | cold scan r (ms) | cold scan b (ms) | checked / starts | RSS (MB) |
|---|---|---|---|---|
| base | 12417, 10649, 10378 | 10831, 12090, 11803 | 271 / 271 | 1718, 1837, 1955 |
| s2 | 11732, 11864, 10765 | 10406, 10560, 12096 | 271 / 271 | 1654, 1746, 1746 |
| s4 | 11749, 11892, 11929 | 10448, 10555, 10535 | 271 / 271 | 1852, 1775, 1652 |

### Lint off, `CHAINS=2 SMALL_FILES=120` (481 files per session)

| build | cold scan r (ms) | cold scan b (ms) | checked / starts | RSS (MB) |
|---|---|---|---|---|
| base | 10060, 9571, 10104 | 9430, 8898, 9456 | 481 / 481 | 1449, 1705, 1553 |
| s2 | 9053, 9462, 9318 | 9713, 10098, 9974 | 481 / 481 | 1753, 1553, 1724 |
| s4 | 9419, 9347, 10124 | 8781, 8693, 9493 | 481 / 481 | 1766, 1361, 1725 |

### Lint on, `CHAINS=2 SMALL_FILES=120` (481 files per session)

| build | cold scan r (ms) | cold scan b (ms) | checked / starts | RSS (MB) | live peak / bound |
|---|---|---|---|---|---|
| base | 9704, 10207, 10181 | 10339, 10856, 10810 | 481 / 481 | 1328, 1280, 1176 | — |
| s4 | 11161, 10262, 11094 | 11808, 10913, 10471 | 481 / 481 | 713, 672, 677 | 24 / 24 |

## Conclusions

- **Parity holds.**
  - Every build checked every file: 271 of 271 and 481 of 481 in every run.
  - It started exactly one FCS check per file, with 0 retry rounds and 0 unchecked.
  - So the memory figures below come from the same work, not from less.
- **Memory.** With a `FileChecked` consumer (lint), the host's RSS at the end of the cold
  scans fell from 1176–1328 MB to 672–713 MB, about 45 % less. That is ADR-054 and
  ADR-055 at work: the live peak sat at the bound, 24.
  - The price is scan wall time. The cold scans took 0–15 % longer (10.3–11.8 s
    against 9.7–10.9 s), because a scan now goes at lint's pace.
  - Without a consumer there is nothing to bound, and RSS is noise across all three
    builds: 1.36–1.96 GB.
- **No wall-clock gain from ADR-056 on this machine and harness.**
  - s2 and s4 are within run-to-run noise on both layouts.
  - Why: two sessions scan at once through one 12-slot host scheduler, so a slot that one
    session's tier barrier leaves idle is taken by the other session's checks.
  - A barrier costs wall time only when the box has idle slots. That means one session,
    a graph with a long tier-crossing path, and spare processors.
  - ADR-056 stays for its correctness and boundedness: one bounded queue, retries per
    project, no hold on unrelated projects. This harness does not show a speed-up from
    it.
  - A single-session variant of the harness would be needed to measure the idle-slot
    case. It is not built: the default hosted path always runs two sessions.

## Raw data

The runs are in `~/.local/state/fshw-two-session/683p-*`, `683q-*` and `683l-*`, on
wutang-lan.

## Follow-ups filed

- **AUTOMATION-1359.** Publish a change batch's changed-file results under the
  live-result bound. This needs a split `FileChanged` announcement, so it is not small.
- **AUTOMATION-1360.** One live-result bound per repository host, with a lane per session.
  Today's per-session bound does not cap the host.

## Closing gate

- **`mise run ci` exit 0:**
  - test-direct: 6499 passed, 0 failed, 2 skipped;
  - coverage floors passed;
  - lint-project: 123/123;
  - analyzers: 0 findings; format: rewrote 0 of 431; test-prune: 2/2;
  - integration: 115/115.
- **`fshw -a check` exit 0:** `outcome: green`, scope full (2 of 2 projects).
- **`fshw verdict` exit 0:** "green — for THIS tree", sha256:ff160c7a…, relative to
  full-suite run e0b9dfef.
