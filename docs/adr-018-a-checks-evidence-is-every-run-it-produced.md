# ADR-018: A check's evidence is every run it produced, not the last one

Status: Accepted (2026-09-05)

## Context

ADR-013 made the verdict a file and gave it a `runId`, so the reports a check rested
on could be **declared** — they are the files in `.fshw/test-runs/<runId>/` — rather
than inferred from timestamps. That fixed the question it was asked. It answered a
different question by accident.

A single `check` does not run the tests once. It runs them in batches:

* the impact-selected run the scan provokes;
* the rerun a file change arriving mid-run queues behind it;
* `confirm`'s forced full suite, when the settled scope was not already full;
* the FIFO drain of a queued `run-tests` force-run.

Each batch is a separate run with its own id and its own directory. The daemon's
`test-scope` reply named ONE of them — the run its evidence receipt pointed at — and
the verdict copied that id into `runId` and built `suites[]` from that directory
alone.

So the verdict was a truthful answer to "which run was this graded from" being read,
by everybody, as "what did this check execute". The two are not the same list, and
the difference is not small. From one session on 2026-08-25:

* A profile rename check wrote FOUR run directories: six reports at 15:07, five at
  15:25, five at 15:32, two at 15:33. The verdict named 15:33, whose two reports were
  both build-tooling suites. The change's own acceptance tests were present and
  passing in the three earlier ones.
* A signup-persona change produced two directories thirty seconds apart from ONE
  invocation: 14:38:23 held five reports, 10,979 tests, and all 23 of the change's
  own, passing; 14:38:53 held one report and 566 tests, none of them the change's.
  The verdict named the second.

The batch the verdict named could be the SMALLEST and least representative of the
set, and nothing in the file suggested looking further. Three readers in that session
concluded their tests had never run. In every case that could be checked afterwards,
the tests had run and passed. Two of those conclusions nearly caused real damage: one
change was about to be redone, and one agent reimplemented two design-guard scanners
from scratch to work out what the real guards would have said. The guards had already
run.

This is the failure mode ADR-013 exists to prevent, one level up. An empty run
directory is a stated fact; a batch that is **absent from the record** is
indistinguishable from a batch that never happened.

## Decision

**A check's evidence is every run it produced. The verdict enumerates all of them.**

Three pieces, one per layer:

1. **The daemon keeps a run ledger.** Every completed run joins a session-scoped list,
   written once at the top of the completion handler — before the five branches that
   handler returns through, so no branch can drop a batch. `test-scope` declares the
   whole ledger as `runIds` (newest first, bounded), alongside the unchanged `runId`.

2. **The check attributes runs by DIFFERENCE, not by clock.** The driver reads the
   ledger once before its scan; everything the daemon completes after that baseline
   belongs to this check. Both ends are declared, so a run belonging to an earlier
   check cannot be adopted by this one, and a batch nobody watched go by is still
   counted.

3. **The verdict records `runs[]`** — every batch, graded run first, each with the
   reports it wrote — and `suites[]` becomes the flattening of it. `suites` is a
   derived member of the type, not a second field, so the flat view and the per-batch
   view cannot disagree.

`runId` keeps its meaning: the run this verdict was GRADED from. It is not deleted,
because that question is still asked — by the comparison record, by the reader who
wants to know which counts the outcome was computed against, and because the graded
run is not always one of the check's own (a check that reuses an applicable
full-suite green from an earlier run on the same tree is graded from a run that
predates it, and that run's reports are still its evidence, which is why it leads
`runs[]`).

## Consequences

* One project can appear TWICE in `suites[]`, from two batches, with different
  counts. That is what happened. Summing the totals therefore counts test
  *executions*, not distinct tests — so the surfaces that print a total say "across N
  batches" when there was more than one, rather than quietly reporting a number
  larger than any report on disk.
* Consumers that read `suites[]` as (project, report) pairs and open the reports —
  which is what the consuming repo's selection-coverage and recall-miss audits do —
  get more complete answers with no change on their side. They were under-counting
  for exactly this reason.
* Each check makes one extra `test-scope` round trip, for the baseline. It is
  read-only, it answers while a run is in flight, and it is the only moment at which
  "not this check's" is knowable.
* A daemon older than the ledger sends no `runIds`. That degrades to naming the one
  run it was told about — the behaviour before this ADR — never to a refusal. A
  diagnostic addition may not turn a working check into a failure.
* The ledger is bounded (64). A check with more batches than that would be
  under-reported by its oldest ones. No observed check has exceeded four.

## Alternatives rejected

**Enumerate the run directories on disk.** The capability already existed
(`Ctrf.runDirs`). It would have meant deciding membership by directory mtime, which
is precisely what ADR-008 and the run-directory layout exist to make unnecessary: in
a shared pile with no manifest you cannot tell which files are yours. It also cannot
tell this check's runs from the previous one's.

**Attribute runs by completion timestamp against the invocation's start.** No extra
round trip, and no mtimes — but it decides membership by comparing clocks instead of
by identity, and it has nothing to say about a run that was already in flight when
the check began.

**Record only the count of batches.** "Your test ran in one of the other three
directories" is not an answer, and a reader who has to go and find them is back to
the forensics this record exists to end.
