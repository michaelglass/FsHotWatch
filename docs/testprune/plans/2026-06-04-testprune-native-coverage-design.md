# TestPrune-native, edit-aware coverage — design

## Problem

The fshw coverage ratchet drifts and goes stale. Root cause, established empirically
against thellma/intelligence:

- A fresh MS `--coverage` run is **deterministic and complete** — its line set equals
  the PDB's span-expanded sequence points (e.g. `Embeddings.fs`: 412 lines, MS == PDB,
  identical sets). MS is *not* the problem.
- The staleness is **line-number drift under edits**. `coverageratchet` / `CoverageMerge`
  do a blind per-`(file,line)` max-merge across runs. When a file is edited, lines shift,
  and old line *numbers* (now landing on comments / `type` decls) accumulate in the
  baseline and never age out. `Embeddings.fs` baseline grew to 639 = 412 real + 227 stale
  (verified: the 227 are comments, blanks, `type EmbeddingVector =`, etc.). Floors got set
  against the inflated 639 → unreachable.

Two distinct correctness bugs:
- **Bug A — line shift:** an edit moves lines; baseline coordinates go stale.
- **Bug B — shrinkage:** a re-run test now covers fewer lines; max-merge can only add,
  never drop, so the regression is invisible.

## Key insight

Coverage state belongs **in TestPrune**, keyed by **`(symbol, offset)`**, not absolute
line. TestPrune already maintains, per symbol: `source_file`, `line_start/line_end`, and
`content_hash` (for impact analysis). That is exactly the machinery needed:

- **Moved symbol** (`content_hash` same, `line_start` shifted) → its coverage's absolute
  lines recompute from the new `line_start`. **Bug A solved by construction** — no diff,
  no remap pass.
- **Changed symbol** (`content_hash` differs) → purge its coverage; impact analysis already
  re-runs *every* test that reaches it → they re-ingest fresh. **Bug B solved** — the
  deleted rows only come back if a re-run test still covers them.

So **symbol-level invalidation + complete impact re-run substitutes for per-test
attribution.** We get AltCover-grade correctness without AltCover's 2–3× instrumentation
slowdown (ADR: thellma/intelligence `docs/adr/0001-coverage-tooling.md`).

Load-bearing assumptions (named, because correctness rests on them):
1. Impact analysis is complete (every test reaching a changed symbol re-runs). Failure is
   *conservative* (a changed line reads uncovered until its test re-runs), never inflated.
2. Symbol granularity covers coverable lines (F# top-level `let`s/members are symbols;
   rare inter-symbol lines fall back to a file-delta remap).

## What we learned from OpenCover / AltCover (the wheel)

- Proven point model: `Method → SequencePoint(sl, offset, vc, uspid) + BranchPoint(offset,
  path, vc)`, files by `uid`. The point is keyed by **IL `offset`**; source line is a
  *derived* attribute — validating the `(symbol, offset)` choice.
- Per-test = `TrackedMethods` + `TrackedMethodRef(uid, vc)` on each point.
- **OpenCover #715:** merging `coverbytest` runs collides per-run `TrackedMethod` uids →
  ambiguous. **TestPrune's stable symbol identity (`test_methods.symbol_id`) fixes this for
  free** — the one thing a generic coverage tool can't have.
- Branch coverage explodes in F# (CE branches, ~2×). AltCover is conservative. → do
  sequence-point (line) coverage first; branches conservative/deferred.
- Denominator = PDB sequence points, universally (MS/OpenCover/AltCover/coverlet agree).

## Design

We do **not** instrument. We ingest MS cobertura (already complete) and store it
symbol-relative. AltCover-style per-test stays an optional future enrichment.

### Schema (additions to `TestPrune.Database`, bump `SchemaVersion`)

```sql
CREATE TABLE IF NOT EXISTS coverage_points (
    symbol_id   INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    line_offset INTEGER NOT NULL,   -- absolute line - symbol.line_start (stable under moves)
    kind        TEXT NOT NULL DEFAULT 'line',  -- 'line' | 'branch' (later)
    hits        INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (symbol_id, line_offset, kind)
);
-- Reserved for per-test (AltCover) enrichment; unused initially:
-- CREATE TABLE test_point_visits (test_symbol_id, covered_symbol_id, line_offset, hits)
```

The DB stores **all** coverable points (covered and hits=0) so the denominator is the
count of rows and covered is `hits > 0`. Absolute line on read = `symbol.line_start +
line_offset`. Lines outside any symbol fall to a per-file fallback table (TBD; rare).

### Lifecycle

1. **Ingest** (after a test run): parse cobertura → for each `(file, line, hits)`, find the
   symbol whose `[line_start, line_end]` contains `line`, store `(symbol_id, line-line_start,
   hits)` as a **max-merge by `(symbol_id, line_offset)`** (partial-run protection, but now
   shift-proof because it's symbol-relative).
2. **Re-index** (file changed): symbols updated. Changed symbols (`content_hash` differs)
   → `DELETE FROM coverage_points WHERE symbol_id IN (changed)`. Moved symbols → nothing to
   do (offsets stable, lines derived). Impact re-run re-ingests changed symbols.
3. **Check**: per file, covered = rows with `hits>0`, total = rows; compare to floor.

### Wiring

`FsHotWatch.TestPrune` ingests into the DB after each run and queries it for the check,
retiring the blind `CoverageMerge.mergePerLineMax` / `coverageratchet --merge-baselines`
line-keyed merges.

## Phases

0. This doc.
1. Schema + `(symbol,offset)` store/query + symbol-relative remap — unit tests (move → lines
   follow; hash change → purge).
2. Cobertura → coverage_points ingest — round-trip test.
3. DB → per-file % — matches cobertura test.
4. Edit-aware lifecycle wired to re-index — edit-shift-and-change test.
5. Wire into `FsHotWatch.TestPrune`; retire the blind merges.
6. Verify against intelligence (30 files honest; edit → no drift).
