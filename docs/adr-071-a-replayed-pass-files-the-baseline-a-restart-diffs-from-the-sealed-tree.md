# ADR-071: A replayed pass files the baseline, and a restart diffs from the tree the index is sealed at

Only the first workspace on a tree could ever give a restarted daemon something to diff from.
Every later workspace ran the full suite after each restart (461–651 s against 341 s filtered,
measured under AUTOMATION-1623). Two gates caused it, and both are changed here. A run that
replays a project's full pass now files the `test-baseline` record. A restart now diffs from the
tree the impact index is sealed at, when this checkout's own full suite passed that tree.
AUTOMATION-1682.

Status: Accepted (2026-11-14)

## Context

Measured on one shared server (intelligence a9018f3aa55c, fshw 0.14.0-alpha.146) in three
workspaces on one tree. Every check there makes a one-file body edit to a covered symbol.

**Gate 1, filing (`TestBaseline.fullPasses`).** A full-suite run in a second workspace
replays the projects the first workspace already passed at the same K_P. It earns the ledger's
full-suite baseline, but the box-wide record was refused: `reason=lacks-full-pass — … its
full-scope pass is from run … in …/m-1623-a, not this run`. Only the first workspace on a tree
filed one.

**Gate 2, reading (`restartBaselineIn`).** A restart diffs from a full pass only when the
index is still sealed at the ledger's tree. That makes a cycle. A fresh daemon with nothing to
diff from runs the full suite. That run earns the ledger at the EDITED tree. The revert then
re-seals the index at the base tree. The next fresh daemon refuses (`the index is sealed at
033bc173…, not at the baseline's tree 991e8667…`), runs the full suite at the next edited tree,
and the cycle repeats. m-1623-a escaped it only because its ledger tree was the base tree,
which every revert re-seals.

**The soundness bound (AUTOMATION-1587).** In those workspaces a fresh daemon mapped the
changed symbol to ZERO classes. Only the zero-affected refusal turned that into a full suite.
A resident daemon selected 14,730 tests. Any fix that lets a restart select must make it
select what a resident daemon would, never less.

## Decision

**1. A pass at the project's current K_P is a full pass, whichever run filed it.**
`fullPasses` takes the newest full-scope record at each runnable project's CURRENT K_P. A pass
counts whether this run filed it (`FullPass.EarnedHere`) or another run did and this one replayed
it (`FullPass.Replayed`, naming that run and checkout). Why this is sound:

- K_P names the project's inputs as they are now: closure, options, packages, toolchain,
  loaded environment and scope. A pass filed under it says what a pass of this run would say.
- The run's own green already rested on this record. Replay is how it passed the project. The
  baseline claims nothing the verdict did not.
- Every other gate stays. The completion must earn the full-suite baseline (a full run that
  passed every runnable project), and the index must be sealed at the receipt tree.
- A newer fail at that K_P refuses. A record at any other K_P (another tree's inputs) is no
  record. A pass that names no run is refused too: every test run names itself, so such a
  record is no run's pass, and the replayed earner is always a run id.

The record's outputs gain a `replayed` object (project → run, worktree), and the log line
gains `replayed=N`. The `projects` map, schema and key are unchanged, so existing records still
read.

**2. A restart diffs from the tree the index is sealed at, when this checkout passed it.**
The cold scan diffs the index's rows, and those describe the sealed tree S. So S is the only
tree a restart can diff from correctly. `restartBaselineIn` now uses S, under three conditions:

- S is a tree this checkout's own full suite passed with the index sealed there. The ledger
  keeps those trees: `Tree` and up to 19 `EarlierTrees`, newest first, carried forward by
  `FullSuiteBaseline.succeed` on every earned baseline.
- The `test-baseline` record for exactly S is a pass from this analyser build.
- That record's manifest hashes to S.

A tree that is unchanged from S (or from the ledger's tree) is still a miss, so the skip on
records answers it.

**Why the record alone is not enough.** The record is keyed by the tree's bytes, and any
checkout may file it. Another checkout's pass of S can rest on a different toolchain or a
different loaded environment, and neither is part of the tree. This checkout's own pass of S
is what shows its tests passed there as they run here. It is the same evidence the old "ledger
tree = sealed tree" condition used, kept for more than one tree. A checkout with no full-suite
pass of its own, or whose own passes were all at other trees, still misses and still runs every
project in full. That is the 1587 negative control.

## Alternatives rejected

- **Accept any filed record for the sealed tree (no per-checkout history).** Simplest, and it
  breaks the cycle too. But the restart would select against a pass that may have been earned
  under another environment, and no diff can see that difference. Rejected on soundness.
- **Keep the ledger's tree at a tree every revert re-seals.** This would leave the ledger
  understating the newest full-suite baseline. The ledger is also the watermark that
  impact-filtered greens are relative to (ADR-021), so it must name the newest one. Rejected.
- **Record each checkout's environment in the test-baseline record and compare it on
  restart.** This would make a record from another checkout usable. It needs a schema change
  and a second hash of the tree at restart, for a case (no full pass of one's own at S) that
  Decision 1 already removes in practice: a workspace's first full run now files its own
  record. Deferred.

## Consequences

- A second workspace on a tree that another workspace passed files its own record at its first
  full run. A restart there after an edit holds for the diff and selects, as in the first
  workspace.
- A full pass at an edited tree no longer strands a checkout. After a revert to any of the 20
  trees its ledger keeps, a restart diffs from that tree.
- A restart over an index sealed at a tree this checkout never passed in full still runs the
  full suite. So does one whose record for that tree was evicted (the index keeps the newest 20
  box-wide).
- The ledger file gains `earlierTrees`. An older ledger without it reads as having none.

## Tests

- `TestBaselineTests`: a full pass replayed at the current K_P files the baseline and names
  its earner. Negative control: a pass at another K_P, a newer fail, or an unearned run files
  nothing.
- `TestBaselineRunTests`: a second checkout whose first full run replays a project files its
  own tree's record, and `checkoutBaselineIn` then finds it after an edit.
- `TestPruneBaselineWaitTests`: after a full pass at an edited tree and a revert, a restart
  holds on the reverted tree and runs only the covering classes. Two negative controls: no
  ledger of its own, and a ledger whose passes were all elsewhere. In both, the record for the
  sealed tree exists box-wide, and the restart still runs every project in full.
- `TestPruneSelectionSoundnessTests`: the ledger's tree history is newest first, bounded,
  deduplicated, round-trips, and reads an older ledger.

## Amendment (2026-11-14): a seal that lags the receipt tree by run-time paths

alpha.148 shipped without 1682, so live evidence needs a release that contains both 1682 and this amendment.

Measured on alpha.148, which predates this ADR's code, in every intelligence workspace: the full
pass kept no ledger tree, so `restart-selection` said `would-run-full-suite … records no tree it
passed with the index sealed there`. One example is run 22f396b7 (20,917 tests, pin148-gate). Its
`BatchChecked` sealed the index at 2d864d48 while `beforeRun` (test-preflights) was still writing
the Fable bundle. The receipt rebound to the tree after `beforeRun` (0ec949e8, "moved only paths no
build compiles"). The completion found the seal at 2d864d48 and the receipt at 0ec949e8, so it filed
nothing (`reason=not-sealed`). The re-check that would re-seal at 0ec949e8 is deferred until the test
host completes, so it folds after the completion. Decision 2 alone does not fix this, because
`EarlierTrees` only keeps trees that were already sealed.

**Decision.** A completion that earns the baseline re-seals the index at its receipt tree only
when both conditions below hold. The ledger then records the receipt tree and the record is filed.
Otherwise the seal stays where it is.

1. **No index input moved since the seal.**
   - *What is compared.* The sealed tree's manifest is compared with the receipt tree's manifest,
     entry by entry, by content hash (`ContentHash.ofFile`).
   - *What counts as moved.* An entry whose hash differs, plus any entry that only one of the two
     manifests holds.
   - *Where the manifests come from.* Both are `ReceiptInputTree.readTree`, the same walk both tree
     identities hash. It covers:
     - the discovery roots;
     - the configured test projects and their reference closures;
     - the tool-known root files;
     - the declared verdict inputs.

     Config excludes are not applied.
   - *What counts as an index input.* `ReceiptInputTree.indexInput` is true for the union of:
     - (a) every source the index recorded an analysis of: its `file_keys`, read when the
       predicate is built;
     - (b) `feedsCompile`: declaration sentinels, `.fsproj`/`.props`/`.targets`/`.sln`/`.slnx`,
       `project.assets.json`, the tool-known root files (`Directory.Build.*`, `global.json`,
       `nuget.config`, the lock files), `.fshw.json`, and the current model's compile items;
     - (c) `Watcher.isRelevantFile`: every `.fs`/`.fsi`/`.fsx` and MSBuild project file outside
       `bin/`/`obj/`, whatever the model says;
     - (d) `.cs`, `.vb`, `.csproj`, `.vbproj`, `.proj`, `.projitems`, `.shproj`, `.user`, `.rsp`,
       `.dll`, `.exe`, `.winmd` and `.nupkg`.
2. **No flush has written rows since the seal.** The seal on disk must still name the sealed tree.
   A flush that writes rows clears it there, but not in `TestPruneState.SealedTree`.

**Why the predicate is not narrower than what the index read.**

- *What a row reads.* A row is the analysis of one source. Its identity
  (`IndexedAnalysis.identityOf`) is that source plus the used signatures of its project's
  references.
- *Inside the tree.* Those inputs are:
  - the source itself, in (a) and (c);
  - the project's other sources and its referenced projects' sources, in (c) and (d);
  - the MSBuild evaluation that produced the options: project files, `Directory.Build.*`,
    imports and `<project>.user`, in (b) and (d);
  - checked-in reference assemblies, in (d).
- *Outside the tree.* Referenced projects' build outputs and the package cache sit outside the tree.
  They are derived from the above, or pinned by `project.assets.json`, the lock files,
  `nuget.config` and `global.json`, all in (b).
- *The stronger argument.* Every ordinary seal is already weaker than this. A `BatchChecked` seals
  at the whole tree it reads after folding the check results of the files the watcher reported, and
  the watcher reports only (c) plus file-command patterns, which feed no row. So any seal already
  vouches for paths outside (c) that moved without a re-analysis. Of the four sets, (c) alone
  matches that; (a), (b) and (d) are margin beyond it.
- *What remains.* What stays outside the predicate is outside every seal too: an MSBuild import or a
  type-provider input under any other name.

**Tests.** All are in `TestPruneBaselineWaitTests` unless named otherwise.

- *Positive.* A bundle generated after the seal: the completion re-seals, records the tree, and a
  restart publishes `SelectsCorrectly`. `TestBaselineRunTests` checks that the record is filed for
  the receipt tree.
- *Negative.* Each of these keeps the seal where it was and records no tree:
  - a source edited after the seal;
  - a compile input (`Directory.Build.props`) moved during `beforeRun`. Here the launch binding is
    kept, and the record is refused as `not-sealed`;
  - the `.fsproj` that `TestBaselineRunTests` edits after the seal;
  - the predicate pins: `Directory.Build.props`, `Directory.Packages.props`, `.fsproj`,
    `.fsproj.user`, `.fs`, `.fsi`, `project.assets.json`, `global.json`, a checked-in `.dll`, `.cs`,
    `.csproj`, a `.targets` outside the roots, and a source the index recorded under any name.
- *Mutation check.* With `sealCarriesTo` forced to carry, every one of these negative controls went
  red: 16 failed, 35 passed.

