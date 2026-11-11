# ADR-067: A tree every project holds a record for skips without a session baseline

`NoSessionBaseline` has been doing two jobs under one name: 'is there a baseline' and 'has
this daemon diffed the tree it is about to skip'. ADR-021 made the first durable
(`full-suite-baseline.json`). Nothing persisted answers the second, so a restarted daemon
refuses the zero-affected skip on its first build even over the very tree its full suite
passed on. This ADR answers the second job for one case: the tree is one the box-wide
records already vouch for, project by project. AUTOMATION-1587.

Status: Accepted (2026-11-12)

## Context

The zero-affected skip needs `hasCachedResults` (`LastResults.IsSome`), which only a run
completed in this process sets. After a restart, or before a new daemon's first run, the
skip is refused with "no run has completed in this session yet", even when nothing is
owed, the full-suite baseline stands, nothing is forced, and the tree has not moved. That
refusal protects against one false green: a receipt bound to an old tree replayed as a
green for a new one (TestPrune alpha.52, and the restart pending-queue seeding note in
`PluginStartup`).

The box-wide stores already hold what the second job needs for an unchanged tree. Every
full, first-attempt, all-pass project run publishes a green under its K_P
(`TestEvidenceKey`), and every completed project run files a pass or fail record at that
K_P in the outcome store (ADR-059). Under `reads: verdict-tree`, K_P names every file of the
verdict tree. A record at a project's current K_P therefore says that project passed in full
on these exact inputs.

Measured before this change, on the evidence-run harness, a restart over the tree its full
suite passed on logged the refusal, launched a full-suite run, and reused every project
there: no test ran, but the cycle was labelled a refused skip and paid for the run's setup
and bookkeeping. With no store wired, the restart ran every project.

## Decision

**The T' = T rule.** A zero-affected cycle whose ONLY refusal cause is `NoSessionBaseline`
skips when every runnable project holds a full-scope PASS record at its K_P, and the skip
cites those records. "Only cause" means `zeroAffectedWidening` returns exactly
`[NoSessionBaseline]`: nothing queued, no unknown debt, a valid full-suite baseline, no
runtime-coverage debt or missed ingest, no outstanding failures, no evidence gap, and
nothing forced or narrowed. With any other cause the cycle behaves as before.

How the rule is decided (`RecordedSkip`, used by `ImpactRun`):

1. **Asked once before `beforeRun`.** Every runnable project is keyed and looked up over the
   tree as the daemon finds it. A miss refuses the skip at once and logs
   `no skip on records: <project>: <why>`. A miss costs no extra `beforeRun`.
2. **Decided on the settled tree.** The run then goes through the ordinary launch path. Its
   evidence plan is made after `beforeRun` has settled the tree (AUTOMATION-1510), and every
   reuse is re-checked at completion. The skip holds only when every runnable project's
   green was filed from its record and still held at completion, so nothing launched. A skip
   has no completion-time re-check of its own, so the decision waits for the run's re-check.
   If `beforeRun` moved an input, a project whose record no longer holds launches. The skip is
   then refused with `no skip on records: at the settled tree … ran: <projects>`.
3. **Local reds win.** An outstanding failure in this checkout's ledger is a forced cause
   (the cycle never reaches the rule). The skip asks no reuse question of its own: it reads
   the evidence plan's reuse, from which `TestEvidenceRun.reuseRefusal` (ADR-059 §34) has
   already removed every project with a newer full or impact fail at its K_P, or a record
   there that cannot be read, naming why. Such a project is no skip, and it runs.
4. **Moved trees run.** A covered edit or any non-source path changed while the daemon was
   down moves K_P (under `reads: verdict-tree`, every project's K_P), so the lookup misses and
   the cycle runs as before.

**Citing the records.** The daemon log names every record the skip rests on: per project,
the earning run id, its worktree and the K_P prefix
(`Zero-affected skip with no session baseline: … TX <- run <id> in <worktree> under key <kp>`).
The verdict names them too: each project's report in the run directory is filed from its
record and stamped `fshwReusedFrom` (run id, worktree, key), which `verdict.json` carries as
`reusedFrom` on each suite. The launch is labelled `ZeroSelection.AlreadyVerified`. Its
selection stays every project in full, because the selection is the scope the receipt and
the full-suite baseline are entitled to, and every project's green is held.

## Consequences

- A restarted daemon over a tree whose every project passed in full launches nothing and
  says so, citing the records, instead of logging a refused skip. A new long-lived checkout
  at an already-passed tree does the same once it holds a full-suite baseline.
- The cycle still runs `beforeRun` and the completion re-check. This is deliberate: a skip
  decided on pre-`beforeRun` keys could hide AUTOMATION-1510's disagreement.
- Under `reads: verdict-tree` the rule covers only an unchanged tree. The first edit moves
  every K_P. Restart after an edit, and a fresh workspace one edit from a passed tree, still
  run as before.
- The skip refuses exactly what an ordinary reuse refuses, because both read one check
  (ADR-059 §34). Tightening that check tightens the skip; nothing here can drift from it.

## What follows

Per the AUTOMATION-1587 design, in order, each landing green on its own:

- **Step 2:** write `test-baseline` records (tree-keyed, box-wide, with a per-file manifest of
  content hash and analysis identity) in shadow, unread, and measure their size.
- **Step 4:** hold the first launch for the cold scan's diff when a baseline record is in hand,
  not only with `LastResults` (`DiffWait`).
- **Step 5:** the same-checkout restart after an edit, which is the ticket's measured case.
  It uses the baseline record, the local `RowsFromPriorRun` rows and step 4.
- **Step 6:** a fresh workspace diffs against the nearest baseline tree's analysis records
  (`RowsFromBaselineTree`), behind a flag and in shadow first. Its positive test is in
  `TestPruneRestartSkipTests`, skipped until then.

## Rejected

- **Skip on pre-`beforeRun` keys, without the run.** This is cheaper, but a key over the
  unsettled tree can disagree with the tree the tests would run against (AUTOMATION-1510).
  A bare skip also has no completion re-check that would catch it.
- **An empty-selection skip after a restart.** No receipt is held after a restart, so an
  empty, quiet `AlreadyVerified` completion is revoked, and `check` cannot mint a green from
  it (ADR-033). Filing each project's green from its record gives the verdict evidence it
  can grade and name.
