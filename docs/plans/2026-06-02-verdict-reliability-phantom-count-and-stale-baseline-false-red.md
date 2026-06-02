# Verdict reliability II: phantom plugin counts (false green) + stale-baseline coverage non-determinism (false red)

> **Agent task.** Two distinct verdict-reliability defects were reproduced in a
> downstream consumer (thellma/intelligence, fshotwatch.cli 0.8.0-alpha.14, .NET 10,
> xUnit v3) on **2026-06-02**, both in the same session. They are the two halves of
> "you cannot trust the aggregate verdict": one prints a non-zero **error count that
> doesn't gate and corresponds to no real finding** (false green in the display), the
> other prints **errors that aren't real and DO gate** (false red). Both teach users
> to ignore the verdict — the exact rot the CI gate exists to prevent. This extends
> [`2026-06-01-testprune-verdict-reliability.md`](2026-06-01-testprune-verdict-reliability.md)
> (Issue 2 stale snapshot, Issue 3 coverage-baseline corruption) and
> [`2026-06-01-reporter-failure-false-clean.md`](2026-06-01-reporter-failure-false-clean.md)
> with concrete, fresh reproductions and a sharper framing.

The framing that exposes both (from the downstream user): **a finding shown as an
"error" must either fail the gate, or not appear in the output at all. Anything shown
as an error that does not gate is a bug; any error that gates but isn't real is also a
bug.** There is no legitimate "displayed-but-not-gating" or "gating-but-not-real" state.

---

## Issue A — phantom plugin summary count: `✓ analyzers — N findings (N errors, 0 warnings)` with N real = 0

### Symptom
A warm/cold `dotnet fshw check` (or `./build.fsx check`) printed:

```
✓ analyzers                (3.7s) — analyzed 731 files, 9 findings (9 errors, 0 warnings)
...
No errors
```

with overall exit code **0**. But:
- `dotnet fshw errors --wait` listed **0 analyzer entries** (only coverage entries).
- Grepping the full check/analyze output for any analyzer finding **detail** line
  (rule id, `file.fs(line,col)`, message) returned **nothing** — there were no 9
  findings anywhere, only the summary count claiming "9 (9 errors, 0 warnings)".
- The plugin verdict was ✓ and the run was genuinely clean of analyzer violations.

So the analyzer plugin's **summary line printed a finding/error count that (a) did not
correspond to any actual finding and (b) did not gate** (✓, exit 0). It is a stale or
miscomputed count — most likely carried over from a prior analyze cycle's diagnostics
(same stale-snapshot family as Issue 2 in the v1 doc), surfacing in the human-readable
summary even though the gated diagnostic set was empty.

### Why it matters
"`✓ … 9 errors`" is a contradiction on its face. A reader who trusts it either (a)
concludes the gate ignores errors (and stops trusting green), or (b) goes hunting for 9
non-existent findings. Worse, if the summary count can drift **above** the gated set, it
can presumably also drift **below** it (showing fewer than really exist) — a latent
false-green. The count shown to humans MUST be the same set the verdict gates on.

### Fix direction
- The analyzer plugin's summary count must be computed from **the same diagnostic
  collection that drives the pass/fail verdict for the current cycle** — never a cached
  or previous-cycle count. If the gated set is empty, the summary must read `0 findings`.
- Audit every plugin's summary renderer (build/lint/analyze/test/coverage) for the same
  count-vs-verdict divergence; the bug is likely a shared "last known counts" field read
  by the summary but not reset/replaced when a cycle supersedes it.
- Add a regression test: drive a plugin cycle that produces 0 gated findings after a
  prior cycle produced N>0, and assert the rendered summary count is 0 (not N).

---

## Issue B — coverage verdict is non-deterministic on an UNCHANGED commit (false red from impact-filter + stale baseline)

### Symptom
In one workspace, on **one unchanged commit**, three consecutive coverage evaluations
(`./build.fsx check`, then `dotnet fshw analyze`, then `dotnet fshw errors`) reported:

```
run 1:  ✓ coverage                              (exit 0)
run 2:  ✗ coverage — 9 file(s) below threshold  (exit 1)
run 3:  ✗ coverage — 85 file(s) below threshold (exit 1)
```

The flagged files were **out-of-diff** source files (not touched by the working
commit), many at an impossible `line=0.0% < min 100.0%`. A file at literally 0%
against a 100% floor has not "lost coverage" — its coverage data is simply **absent
from this cycle**.

### Root cause (confirmed by daemon log)
test-prune is **impact-filtered**: it runs only the test classes/projects affected by
the diff and logs `Skipping <Project> — no affected classes` for the rest. The coverage
ratchet, however, evaluates **every** source file against its floor. For files whose
tests were filtered out this cycle, the only coverage signal is the persisted
`coverage/<proj>/coverage.baseline.xml`. When that baseline is **stale or missing**
(idle workspace whose baselines predate newer source files; or baselines deleted/garbled
by the cold-start race in v1 Issue 3), the merge has nothing to supply → the file reads
0% → false red. The **count varies run-to-run** because which baseline fragments are
present/merged varies as the daemon churns. A *healthy* workspace (recently ran a full
suite, complete baselines) evaluated the **same commit** as `✓ coverage` — proving the
red is workspace-state, not code.

### Why it matters
This is the most corrosive verdict failure: the gate fails on an **unchanged commit**,
nondeterministically, on files the change never touched. Users learn "coverage is just
flaky" and start ignoring it — at which point a real coverage regression ships
unnoticed. It also cost a downstream session significant time disproving a NEWS merge
"regressed coverage" when it had not.

### Fix direction (pick and justify; coordinate with v1 Issue 3)
- **Do not gate a file whose tests did not run this cycle unless a trustworthy baseline
  supplies its coverage.** Options: (a) skip ratchet evaluation for files with no
  current-cycle coverage AND no valid baseline, emitting a loud `coverage: N files not
  evaluated (no current run, no baseline) — run a full suite to establish baselines`
  rather than a false `0.0%` red; (b) require a full-suite baseline (the
  `FSHW_RAN_FULL_SUITE` signal, see `2026-04-24-testprune-ranfullsuite-signal.md`)
  before the ratchet gates at all, and treat impact-filtered runs as "raise-only,
  never red on un-run files."
- A `0.0%` reading for a file that **had no test execution this cycle** must be
  distinguishable from a genuine `0.0%` (tests ran, covered nothing). Don't conflate
  "absent" with "zero".
- Tie into v1 Issue 3's "a partial/aborted run must never lower a baseline" — same
  principle on the read side: a partial run must never *fail* a file it didn't exercise.

### Repro sketch
1. Healthy workspace, full suite once → complete baselines → `check` is `✓ coverage`.
2. Add a new source file + its tests on a branch; merge elsewhere so the baseline goes
   stale relative to the new file; OR simply delete `coverage/*/coverage.baseline.xml`
   to simulate an idle/cold workspace.
3. Run an **impact-filtered** `fshw check` (small diff so test-prune skips most
   projects). Observe coverage flags out-of-diff files at `0.0% < floor`, exit 1, with a
   count that varies across repeated runs — while the commit is unchanged and a
   full-suite run in a clean workspace is green.

---

## Acceptance criteria
- A plugin's human-readable summary count always equals the gated diagnostic set for the
  current cycle (Issue A): `✓` ⇒ summary shows `0 findings`; `N` shown ⇒ verdict is `✗`.
- The coverage ratchet never emits a false `0.0% < floor` red for a file whose tests were
  impact-filtered out this cycle and for which no valid baseline exists; instead it either
  raises-only or reports "not evaluated" loudly (Issue B). Same unchanged commit ⇒ same
  coverage verdict across repeated runs.
- Regression tests for both (phantom-count-after-prior-cycle; impact-filtered-run-does-
  not-false-red-on-unrun-files).
- `mise run ci` green, no warnings, new code covered.

## Verification
`mise run ci`. Plus explicit reproductions: (A) prior-cycle-with-findings → current-clean
→ assert summary `0`; (B) stale/empty baseline + impact-filtered run → assert no false red
on un-run files, and that repeated runs on an unchanged commit are stable.
