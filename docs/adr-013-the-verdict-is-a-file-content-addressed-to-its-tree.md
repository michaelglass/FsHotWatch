# ADR-013: The verdict is a file, content-addressed to the tree it verified

Status: Accepted (2026-07-14)

## Context

An orchestrating agent needs to know one thing: **is this tree green?**

For two days it answered that question by running `fshw check`, scraping `total:`,
`failed:` and `elapsed:` out of a progress display written for a human, and
feeding the numbers into a 40-line bash harness that made merge decisions. It did
this while fshw was already writing structured CTRF JSON for every test run.

The information existed. Nothing ever told the reader where to look.

Three separate defects fed each other:

1. **Nothing pointed at the machine-readable results.** The convention ("if you
   need a primitive fshw doesn't have, add it upstream — don't route around it
   with bash") existed. The CTRF files existed. Both lived somewhere you had to
   already know to look. *A convention that lives somewhere you must already know
   to look is not enforcement — it is hope.*

2. **Measuring perturbed the thing measured.** The `fshw test-rerun` calls the
   orchestrator made *because the gate looked untrustworthy* were themselves what
   corrupted the daemon's busy accounting, which is what made the next `check`
   stamp a content-free green (AUTOMATION-99). The act of measuring created the
   defect being measured.

3. **A verdict could not say which tree it was about.** A changed JSON fixture
   that MSBuild declined to re-copy let a suite run against the *old* fixture and
   pass — 5136 tests, 0 failed — and put a red commit on `main` for hours
   (APPLIC-24). The green was real. It was just a green about a different tree.

## Decision

**The daemon's verdict is a FILE, and its identity is derived from content.**

`.fshw/verdict.json` is written atomically at the end of every `check` and `gate`
— including the ones that fail, time out, or lose the daemon mid-run. It carries
the outcome, the scope, per-plugin results, pointers to this run's CTRF reports,
and a `treeHash`.

The consumer's rule is total and safe by construction:

> Read `verdict.json`. If `treeHash` ≠ hash(current tree), **the verdict does not
> apply.** Never reuse it.

Three properties follow, and each one closes one of the defects above:

**Reading cannot perturb.** A file read opens no socket, claims no plugin slot,
and spawns no process. It is also free, so an agent can poll it. `fshw verdict`
is a convenience over the same file — it too contacts nothing.

**Stale is detectable, not merely avoidable.** The hash covers sources *and
content/fixture files* and `.fshw.json`, by content, never mtime (ADR-008). The
APPLIC-24 fixture is now inside the thing the verdict is addressed by: change it
and the previous green stops applying. Nothing to remember; no MSBuild involved.

**The pointer is in the output you are already reading.** In non-TTY output, every
check and gate prints the actual paths for *this* run:

```
  AGENTS: don't parse this output. Machine-readable results:
    verdict  .fshw/verdict.json   (treeHash-keyed — `dotnet fshw verdict` re-checks it…)
    suites   .fshw/test-runs/Intelligence.Tests.Unit-8134092f….ctrf.json
  this check was impact-scoped (2/6 test projects) — for a MERGE verdict use `fshw gate`
```

Real paths, not a generic pointer: *a hint that makes you go and find the file is
a hint you will ignore.* This ranks far above a doc you must remember to open —
the same ladder we keep returning to: **unrepresentable > structural > detected >
documented.**

## Consequences

**One truth, two surfaces.** The exit code and the file's `outcome` are two
renderings of one `CheckOutcome`. There is deliberately **no "agent mode" that
changes what a check means.** Semantics must never depend on who the tool thinks
is calling: a `check` laxer for humans than for agents makes the two greens mean
different things, and detection (env vars, TTY) is a guess — and guesses fail
open, which is precisely how a `top` that could not sample reported an
overloaded box as "healthy". *Presentation may adapt to the caller; semantics may
not.* The steering hint is presentation: the verdict is byte-identical either way.

**CTRF reports are retained.** They used to be `File.Delete`d the instant their
per-test records had been folded into the flakiness history — so the reports found
in `.fshw/test-runs/` were the ones whose deletion had *failed*: orphans, months
old, indistinguishable from a current run's evidence. A pointer into a directory
of accidental survivors is worse than no pointer at all. The newest few per
project are now kept deliberately.

**The dead `.log` format is gone.** It held raw runner output, written *only* when
something broke — so the newest one dated from the last red run, and anyone
listing the directory read that date as "when tests last ran". It said
2026-06-30, and produced the confident, false conclusion that no test had run in
weeks. *A stale artifact that looks authoritative is worse than none.* The
failing tests, with messages and traces, are in the retained CTRF report.

**A cost.** Every check hashes the tree twice (once after the daemon settles, once
before the write; a difference means the tree moved underneath the verdict and the
outcome becomes `incomplete` rather than a claim about a tree nobody checked).
That is a full content read of `src/` and `tests/` — tens of milliseconds here,
a few hundred on a large repo. Cheap, next to a suite run, and it buys a verdict
that cannot lie about what it covered.

## Alternatives rejected

**A `--json` flag on `check`.** Still a process spawn (~1–3 s of dotnet startup,
and agents call constantly), and still a *request* — it can perturb. The daemon is
long-lived and reactive; "what is true right now" is a **state** question, and
request/response is the wrong shape for it.

**An "agent mode" that tightens the gate.** See above: two greens that mean
different things is the disease, not the cure.

**mtime, or "the newest file in the directory".** Both are what already lied. The
verdict is addressed by content or it is not addressed at all.

## Related

- ADR-008 — mtime is never a content oracle (this is the same rule, applied to the
  verdict rather than to a cache key).
- AUTOMATION-112 — `check` vs `gate`: the verb names its guarantee.
- AUTOMATION-123 — identity derived from content, never asserted by a label. The
  same principle, in a different subsystem, discovered independently.
