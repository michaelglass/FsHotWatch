# ADR-059: One outcome store; the verdict is a lookup; the daemon only accelerates

Status: Accepted (2026-10-29). AUTOMATION-1362 (epic). Supersedes, at the switch (§9), the
verdict-currency parts of ADR-018, 019, 021, 022, 023, 033 and 034, and makes the
correctness half of ADR-045 to 048 unnecessary. The maintainer's direction was to write this
and implement it without waiting for review ("we can change after the fact"), so the
judgement calls are recorded here (§12) rather than asked.

## Context

The convergence analysis (intelligence `results/handoffs/fshw-thrash-analysis-2026-10-29.md`,
§5 and §6) counted:

- 24 persisted stores that can affect correctness, plus 5 in-process memos;
- four different "are these the same inputs?" functions, each with its own walk and its own
  exclude handling:
  - `TreeHash` (the verdict);
  - `TestEvidenceKey` `K_P` (shared test evidence);
  - `ProjectModel.ProjectInputs` (whole-project evidence);
  - `TaskCache.merkleCacheKey` (plugin task results);
- a verdict that still *grades live daemon state*:
  - plugin statuses;
  - "settled";
  - receipts and Earned evidence held in memory;
  - generation-keyed fast paths.

Two defect classes dominate the six-week history, and both follow from that shape:

- **Chain C, "a key omits an input"** (16 tickets): 948, 1099, 1138, 1150, 1151, 1200,
  1217, 1247, 1003, 1255, 1256, 1273, 793, 1116, 1246, 824. Each new key re-derived its
  input set, so each could miss something.
- **Chain D, "no verdict, stale verdict, or false green from bookkeeping"** (25 tickets):
  800, 833, 838, 859, 873, 881, 887, 895, 897, 903, 941, 958, 962, 1040, 1104, 1107, 1110,
  1112, 1129, 1228, 1250, 1268, 1337, 1352, 1355.

AUTOMATION-681, 682 and 683 (46 slice commits, 19 ADRs, 3 reverts) exist mostly to make
the live state that a verdict reads race-free.

FCS checker state cannot be serialised, so the warm daemon stays. The question is only
whether **correctness** reads its memory. This ADR says it must not.

## Decision, in one paragraph

Every unit of verifiable work is an **action** with an **ActionKey**: a content digest of
its declared inputs, computed by one function over one walk. Every completed action
writes an immutable **outcome record** under its key to one **outcome store**. A verdict
for tree `T` is a **lookup**: compute the required actions for `T`, then look up a record
for each. It is green if all of them passed, red if any failed, and "no verdict" if any is
missing. `check` means "make the missing records exist, then look up". `confirm` means the
same with every test action required in full. `verdict` means look up without running
anything. The daemon fills the store faster, and that is all it does. Killing it, racing
it or restarting it can delay or omit a record. It can never make a stale record look
current, because a record is found only by the key of the inputs it was produced from.

## 1. One ActionKey

```
ActionKey(kind, inputs) =
  "ak1:" + lowerhex(sha256(utf8(
      "fshw-action-v1" LF kind LF
      for each (label, digest) in inputs, sorted by label (ordinal):
          len(label) ":" label "|" len(digest) ":" digest "|"
  )))
```

- **Inputs are labelled digests.** A miss names the label that moved, which today's
  `TestEvidenceKey.moved` and `KeyFingerprints` do in two different ways. A digest is a
  file's `ContentHash`, a nested ActionKey, or a tree digest. Nesting is how a key over many
  files stays a merkle key.
- **The encoding is length-prefixed,** as `merkleCacheKey` already is, so no label/value
  split can collide. Labels are unique: a duplicate label is a programming error and
  throws.
- **One walk, one exclude model.** Every file input of every action is a *projection* of a
  single walk: `TreeHash.files`, the verdict walk with `exclude` plus `verdictInputs`.
  `TestEvidenceKey.snapshot` already partitions that walk instead of walking again. Every
  other key does the same, so no second walker and no second exclude, gitignore or
  `SourceExcludedDirs` list can exist. Chain C's exclude members (1003, 1255, 1256, 1273,
  1274) are made impossible, not fixed one at a time.
- **One toolchain identity.** Today there are three: `TestEvidenceKey.toolchainOf`,
  `CheckCache.toolchainIdentity` and `TaskCache.scopeToToolchain`. The ActionKey takes
  one `toolchain` label from one function. That makes 1217 and 1247 moot.
- **No time in any key.** No mtime and no "racy window". Content only (ADR-008).

**Migration.** During shadow mode the four legacy functions keep their byte-exact recipes.
Changing them would invalidate every verdict and every evidence entry on every machine,
and it would confound the shadow comparison. At the switch, they are re-expressed as
ActionKey kinds (`tree`, `test`, `project-inputs`, `task`) in one release, with each
`Algorithm`/`Version` bumped once. That costs one cold cache per machine.

## 2. The outcome store

- **Location:** `~/.cache/fshw/outcomes/<repository namespace>/`. It is box-wide and
  shared by every checkout of one repository, exactly as the test-evidence store is
  (`TestEvidenceStore.directoryFor`). Keys are content addresses with repo-relative paths,
  so two checkouts holding the same bytes compute the same key, and a record earned in one
  speaks for the other. A built output embeds its checkout's path, so a check record names a
  referenced output by what a check reads of it, not by its bytes (§27).
  Every API takes its root explicitly. Tests use a temporary root
  and never the user's cache (the 1100 lesson).
- **Format:** one immutable JSON file per record, at
  `records/<key[4..5]>/<key>/<recordId>.json`. `recordId` is the sha256 of the payload, so
  re-recording an identical outcome is a no-op. The payload holds:
  - `schema: "fshw-outcome-v1"`, `key`, `kind` and `subject` (for example the project);
  - `inputs` (the labelled digests);
  - `outcome`: `pass` or `fail`, with counts where they apply;
  - `measurements` (duration);
  - `producer` (fshw binary identity, worktree, run id);
  - `recordedAt`.

  The file is an envelope `{checksum, payload}`, as in `TestEvidenceStore`.
- **Atomic write protocol:**
  1. Serialise.
  2. Write to `tmp/<guid>` in the same filesystem.
  3. Rename to the final path. Never overwrite: if the final path exists, it holds the
     same bytes by construction, so drop the temp file.

  A torn file, a bad checksum or an unknown schema is a **miss**. The file is moved to
  `quarantine/`. Nothing in the store can make a result green that a real run did not
  produce.
- **Reading:** `lookup key` returns the newest record for the key, ordered by
  `recordedAt` with ties going to `fail`. `history key` returns all of them; that is the
  flakiness input (§9 of the analysis). Several records per key is the point: a flake is
  two outcomes for one input set, and it is recorded rather than overwritten.
- **GC:** `gc now ttl maxBytes` deletes whole key directories whose newest record is
  older than `ttl` (default 14 days). It then evicts the oldest keys until the store is
  under `maxBytes` (default 256 MB). GC is opportunistic, run after a publication, and it
  never deletes a key the publication just looked up. It is correctness-neutral: GC can
  turn a hit into a miss, and a miss means "run".

## 3. The lookup verdict

`LookupVerdict.decide required lookup` returns `Green`, `Red failing` or
`Missing missing`. These map onto the **existing** outcomes and exit codes: green is 0,
red is 1, and missing is the existing "no verdict" (3). **No verdict state, exit code or
ledger is added.**

The required set for command `c` on tree `T`, `R(c, T)`:

- **`plugin:<name>`**, one per configured non-test plugin (build, lint, analyzers, format,
  coverage, file commands). Its key covers the tree digest, the plugin name and the
  producer. Later, each plugin's own declared inputs replace the tree digest (§11).
- **`diagnostics`**: the FCS ledger, the `fcs` red-cause source that no plugin owns
  (ADR-024 case 3). Its key covers the tree digest and the producer.
- **`test:<project>`**, one per test project in the configured scope (`tests.projects`
  minus `tests.excluded`):
  - Under `confirm`, a passing record of scope **`full`** is required.
  - Under `check`, either a passing `full` record, **or** a passing `impact` record for
    this tree's selection **and** a passing `full` record for the project under some
    earlier key. The second is the baseline of ADR-021, read from the store instead of
    from `full-suite-baseline.json`.

**Restart.** The store is on disk, and records are found only by content keys. A new daemon
(or none at all: `fshw verdict`) answers exactly as the old one would have for the same
bytes. The daemon's memory can add records. It is never consulted.

**Selection** (which tests an `impact` record covers) is out of scope here. The shadow
takes today's selection from the run report. AUTOMATION-975/976 replace it with traces.

## 4. Ledgers #8–#14 and the test-prune task key

Each ledger is replaced by a *query* over the store. None of them survives as a file.

| # | Ledger | Becomes | Makes moot |
|---|---|---|---|
| 8 | `full-suite-baseline.json` | "is there a passing `full` record for project P" | 833, 1032, 895 |
| 9 | `pending-verification.json` | the required actions with no passing record at the current key | 800, 1040 |
| 10 | `outstanding-failures.json` | the newest record for the key is `fail`, so the result is red until a passing record exists at the current key | 801 (a flake rate is `history`) |
| 11 | `whole-project-evidence.json` | a passing `full` test record at `K_P`, the project's ActionKey | 1320's sidecar |
| 12 | `runtime-coverage-not-ingested.json` | coverage is the action `coverage:<project>`; no record means missing | 1352 |
| 13 | `runtime-coverage-obligations.json` | the same as 12 | 1352 |
| 14 | `owner-publication-pending` | nothing: a crash leaves records missing, and missing already means "run" | 1347 |

**The test-prune task key** (`TestPrunePlugin.cacheKeyFor`) does not pin the tree. It keys
on the in-memory changed-symbol set, the pending-queue hash and `sessionHasTestEvidence`,
and that is why it needs the fail-closed `sessionHasTestEvidence` guard.

- **Step one (shadow phase):** add the tree digest as a key input. Adding an input can only
  turn hits into misses, never the reverse, so it is safe on its own.
- **At the switch:** the whole-run test-prune cache is deleted. A `BuildCompleted` whose
  required test actions all have records is a no-op by lookup, which is what the cache
  approximated.

## 5. TestPrune schema bumps stop deleting state

The ledgers were pushed out of the TestPrune DB into sidecar JSON because a schema bump
**deletes the DB file** (`Database.openCheckedConnection`; `PendingVerification.fs`
says so). Two changes end that:

1. **Outcomes never live in the TestPrune DB.** The outcome store is fshw's own,
   independent file tree. No TestPrune schema can reach it.
2. **In TestPrune, a schema rebuild drops and recreates core tables only, in one
   transaction.** Plugin-owned tables, and anything core did not create, are kept. The
   file is no longer deleted. The symbol index is derived data and is still rebuilt
   (`WasRecreated` stays). What a bump can no longer do is destroy state that is not
   derivable from the sources.

This makes 951's class (a non-additive schema meeting an older consumer) a refusal rather
than a deletion. That half already exists as `SchemaNewerThanConsumerException`.

## 6. Dropping the mtime-keyed `typeProviderCache` (1200)

`FrameExclusions.typeProviderCache` keys on `(path, mtime)`, the last mtime key, against
ADR-008. It is re-keyed on `(path, ContentHash.ofFile path)`. The verdict is then a pure
function of the bytes. Hashing costs a read, but a provider DLL is read only when an
option names it, which is once per project-options evaluation.

## 7. Shadow mode

- **Where:** at every verdict publication (`check` and `confirm`, `IpcOutput`), and at
  every `fshw verdict` read.
- **What:**
  1. Project the run's results into outcome records, and write them.
  2. Compute `R(c, T)` from the configuration, not from the run.
  3. Look the required set up in the store.
  4. Compare the lookup's outcome with today's.
- **The log:** one line is appended to `.fshw/shadow/lookup-verdict.jsonl`:

  ```json
  {"schema":"fshw-shadow-v1","at":"…","command":"check|confirm|verdict",
   "tree":"sha256:…","producer":"…",
   "today":{"outcome":"green|red|incomplete|model-unavailable","class":"green|red|missing","exit":0},
   "lookup":{"outcome":"green|red|missing","missing":["test:…"],"failing":["plugin:lint"]},
   "agree":true,"direction":"agree|stricter|looser|red-vs-missing","recorded":7}
  ```

  This is a **diagnostic log, not a ledger.** Nothing reads it to make a decision. It
  exists only to measure the switch criterion, and deleting it changes no answer.
- **Agreement** compares outcome classes: green with green, red with red, and anything
  else with missing. A disagreement is never noise. Each one is either a false green in
  today's verdict, a lost record or a mis-derived `R(c, T)`. It gets a ticket before the
  count continues.
- **Kill switch:** `FSHW_OUTCOME_SHADOW=0`. Shadow work is wrapped so that a failure in it
  is logged and **never** changes today's exit code or `verdict.json`.

The first shadow keys test actions by **tree digest**, which gives parity with
`verdict.json`'s addressing and no reuse across trees. Re-keying them on `K_P`, written by
the daemon's test runner, is the step that turns the store into an accelerator (§11). The
switch criterion is measured on the K_P keys.

## 8. The switch criterion

The switch needs **0 disagreements over 50 consecutive intelligence lands** (server side)
**or**, laptop side, all of the following:

- 0 disagreements over **50 consecutive shadow lines** with `command ≠ verdict`, across
  FsHotWatch's own `mise run ci` and intelligence `./build.fsx check` runs;
- at least **10 `confirm`** lines, at least **10 lines with `recorded: 0`** (a publication
  that ran nothing, such as the first after a daemon restart, and answered only from records
  already on file), and at least **5 `verdict` reads** on a tree
  whose last publication was a different command;
- every one of those `verdict` reads agreeing as well.

It is measured with `jq` over the logs:

```sh
jq -s '[.[] | select(.command != "verdict")] | .[-50:] | map(select(.agree | not)) | length' \
  .fshw/shadow/lookup-verdict.jsonl
```

A disagreement resets the count. There are no "expected" disagreement classes. If one
appears by design, then `R(c, T)` is wrong: fix it, and restart the count.

**Amendment (step 3, 2026-10-29): the direction of a disagreement.** Every line also
carries a `direction`:

- `agree`;
- `stricter`: today is green and the lookup is not;
- `looser`: the lookup is green and today is not;
- `red-vs-missing`.

`stricter` and `red-vs-missing` count against the criterion with no exception.

`looser` is the case the lookup exists for. A restarted daemon, or a check that ran
nothing, refuses (exit 3) a tree the store already holds a full green for. A `looser` line
counts as agreement only when a `confirm` line on the **same tree** is green with
`direction: agree`. Otherwise it counts as a disagreement:

```sh
jq -s '[.[] | select(.direction == "looser")] as $l
       | [.[] | select(.command == "confirm" and .direction == "agree"
                       and .lookup.outcome == "green") | .tree] as $ok
       | $l | map(select(.tree as $t | $ok | index($t) | not)) | length' \
  .fshw/shadow/lookup-verdict.jsonl
```

**Amendment (step 3): where the shadow store lives.** During shadow the store is
`.fshw/shadow/outcomes`, per checkout. Tree-keyed records gain nothing from sharing across
checkouts, and a box-wide store written by every test that publishes a verdict in a
temporary repository is how 1100 leaked 98k directories. The store moves box-wide (§2)
together with the K_P re-key (§11 step 7). The location is only the root the store is
opened with.

## 9. Deletion plan after the switch

These are deleted in this order. Each is its own commit, with `mise run ci` and GitHub CI
green:

1. The verdict reads the lookup, and the shadow log goes.
2. Ledgers #8–#14, with their load/save, recovery and tests.
3. Receipts and their grading: `EvidenceReceipt`, `receiptSpeaksFor`, Earned evidence,
   `CohortSeal`, settled-read grading (ADR-033, 034), and the receipt-refusal path in
   `IpcOutput.publishVerdictWithReason`.
4. The whole-run test-prune task cache (§4).
5. The four legacy key functions, re-expressed as ActionKey kinds (§1).
6. `TestEvidenceStore`, folded into the outcome store. It is already a content-addressed
   store of `test` records under `K_P`; it becomes those records.
7. Re-scope the live-state guards (ADR-045 to 048 epochs, incarnations and generation CAS)
   to throughput and memory. Delete those whose only purpose was verdict correctness.

## 10. Which tickets each part makes moot

- **§1 One ActionKey:** 1003, 1217, 1247, 1255, 1256, 1273, 1274, and the reason for 1138,
  1150 and 1151.
- **§2–3 Store and lookup verdict:** 838, 859, 873, 887, 897, 903, 1104, 1107, 1129, 1228,
  1268, 1337, 1355, and the correctness stake in 938 and 939's remaining phases and in
  1359 and 1360.
- **§4 Ledgers:** 800, 801, 833, 895, 1032, 1040, 1320 (sidecar), 1347, 1352.
- **§5 Schema:** 885, 905, 951, 1041, and 1151's cold re-index cost.
- **§6:** 1200.

## 11. Implementation order (each step lands green on its own)

1. This ADR.
2. `ActionKey`: the pure function, the labelled inputs and `moved`, with tests.
3. `OutcomeStore`: atomic immutable records, lookup, history, quarantine and GC, with
   tests on a temporary root.
4. `LookupVerdict`: `decide` and `R(c, T)`, as pure functions with tests.
5. Shadow: record at publication, compare, and append the log; also at `fshw verdict`.
   **This is shadow mode.**
6. The test-prune task key pins the tree (§4, step one), and `typeProviderCache` is
   re-keyed (§6).
7. Re-key `test` records on `K_P` from the daemon's test runner. Accumulate shadow data.
8. TestPrune: a rebuild that keeps non-core tables (§5).
9. The switch (§8), then the deletions (§9).

## 12. Judgement calls made without review

- **Box-wide store, not per-checkout:** the same choice as the evidence store, for
  cross-checkout reuse. A per-checkout store would be simpler, but it would make a fresh
  workspace cold. That is the 1320 and 1304 cost this work exists to remove.
- **Several records per key, and the newest wins:** this keeps flake history as data
  instead of overwriting it. Ordering by `recordedAt` is a tie-break between *outcomes*,
  not a freshness rule. No record expires by time except through GC.
- **The first shadow keys tests by tree digest:** this gives exact parity with
  `verdict.json` first, so early disagreements point at bookkeeping, not at key design.
- **The shadow log is not a ledger:** it is a diagnostic and write-only. Nothing reads it
  for a decision. "No new ledgers" is therefore kept.
- **No `tree` ActionKey replaces `TreeHash` during shadow:** that is deferred to the switch
  to avoid a fleet-wide cold cache in the middle of the comparison.

## 13. Triage of 2026-10-29: folded, obviated and blocked tickets

**Folded into this epic, each a parity case** (`tests/FsHotWatch.Tests/LookupParityCasesTests.fs`).
The ActionKey and the lookup must get each one right. Today's machinery got each one wrong,
so its disagreement on these is expected:

| Ticket | The case | How the lookup gets it right |
|---|---|---|
| 641 | A test-prune claim was replayed after a hashed input the tests read had changed | The test action's key folds every file it reads, so a moved file means no record answers |
| 698 | A `notInputs` file read by a file-command gate can never invalidate its claim | A plugin's key folds what *it* reads, independent of the verdict tree's exclusions |
| 699 | A green verdict whose own suites report a failure | A failed suite is a `fail` record, and any fail is red |
| 1027 | An analyzer pass whose examined plus replayed files do not cover the model | One `analyze` requirement per model file; one without a record is missing |
| 1242 | A configured test project outside the discovery roots is never keyed | Requirements come from the configuration, not the model, so it is required and missing |
| 109 | "A pure `decide`, with effects as data" | `LookupVerdict.decide` is a pure function of recorded outcomes, and any store instance over the same directory gives the same answer |

641 and 699 have a second half that the shadow cannot yet see. In both, a run graded at
another tree had its reports filed for this one. Run directories carry no tree identity,
so shadow v1, which records from the published verdict, inherits whatever today's
grading accepted. That half is closed by step 7: the test runner writes records at launch
under the K_P of the inputs it actually ran over. Its tests land with that step.

**Obviated and cancelled.** These are covered by the deletion plan (§9):

- **1104** (filter-class receipts that never republish) and **1129**: §9.3, receipts and
  their grading.
- **800** (`test-rerun` spending a verdict; pending verification): §9.2, ledger #9.
- **696** (test-prune's summary cannot tell "passed" from "ran nothing"): a `test` record
  carries counts, so a zero-test record is visible as one. The summary line is no longer
  an input to any verdict (§9.1).

**On the path:**

- **1355** (`confirm` returned a prior run's verdict for an identical tree). Under the
  lookup, the contract is explicit. `confirm` requires a `full` record for every test
  project at this tree's keys. A record earned by an earlier invocation over the same bytes
  satisfies it, by design, and the record names its producing `runId`. "Forces execution"
  is not a property any verdict can verify, so `confirm --fresh` remains the verb for a new
  run. Until the switch, 1355 is documented behaviour, pinned by a test.
- **1200** (the mtime-keyed `typeProviderCache`): §6, in step 6.

**Blocked by this epic.** There are 28 tickets, including 938, 939, 682's remaining
scheduler work, 914 and 1331. Their live-state machinery is **not extended**. Where they
guard verdict correctness they are moot at the switch (§9.7). Where they guard throughput
or memory, they are re-scoped against the lookup afterwards.

## 14. The check-result store is part of the one store (AUTOMATION-1361)

The 1361 spike (intelligence `results/handoffs/AUTOMATION-1361-spike-2026-10-29.md`) found:

- **Persisting FCS checker state is a NO-GO** without an FCS fork.
- **A persisted, content-addressed check-result store with early cutoff and lazy re-check is
  a GO.** Its prototype served every result after a clean restart in 0.23 s, against a
  301 s median cold scan. One test-file edit after a restart took 13.6 s. It had 0
  mismatches against a full re-check.

That store is **not a fourth store.** It is the `check(file)` action of this ADR, filed in the
same outcome store:

- **Its key is an ActionKey of kind `check`** over:
  - the file's content;
  - the **API signatures** of the earlier files in its project (`ApiSignature` /
    `makeApiCacheKey`, made repository-relative);
  - the project's options other than `-o:`;
  - the upstream projects' output stamps;
  - the toolchain.

  The API-signature cutoff is mandatory. A content-only key made one comment edit miss
  1,584 files, 121 s, which is barely better than cold.
- **Its record's outputs are what plugins need without FCS objects:**
  - the diagnostics;
  - `fcsCheckSignature`;
  - `usedSignaturesHash`;
  - the file's own API signature, which feeds the next file's key.

  The record format gains an optional `outputs` object for this. A `test` record has none.
- **Plugin task results converge on the same schema:** `lint`, `analyze`, `format` and
  `build` actions keyed by ActionKey. `TaskCache`/`FileTaskCache` become a read-through view
  over outcome records, then go (§9.5). TestPrune's `test-impact.db` stays a **derived index**:
  symbol to test, rebuildable, and never the sole copy of an outcome (§5). Its rows are the
  input to selection, not to the verdict.

**Phasing.** This is §11 step 7b, after the K_P re-key, because both need the box-wide
store:

1. Persist `check` records, serve the hits at boot, and lazily re-check the misses in
   dependency order.
2. Add a `Replayed` arm of `FileCheckResult`, built from a record with no FCS objects, for
   plugins to consume. A plugin-cache miss demands a real check of that file.
3. **Abandon criterion** (the spike's): a clean restart on intelligence over 5 s, or a
   replayed result that disagrees with a forced re-check in the nightly full suite. Either
   stops the work at phase 1.

The project-options cache (4–10 s per restart) is filed separately as a quick win. It is an
accelerator, and no verdict reads it. §18 records it.

**Amendment (2026-11-01): check records are on for intelligence at a 27 s restart.**

- **Decision.** The maintainer enabled check records (`"checkRecords": true`) for
  intelligence at a clean restart of about 27 s, against the 5 s abandon bar above.
- **Why the bar is read this way.** The abandon criterion tests whether check records
  themselves work, not whether the whole restart is fast. Measured on fshotwatch.cli
  0.14.0-alpha.133 over five restarts of an unchanged intelligence tree:
  - every restart with records on started **0 FCS checks**, replayed all 2674 records and
    demanded none;
  - the same tree with records off (`FSHW_CHECK_RECORDS=0`) scanned in 192.5 s;
  - the replayed diagnostics were identical to a forced re-check with records off:
    **0 disagreements**.
- **Where the remaining time goes.** It belongs to other plugins, not to check records.
  Those tickets now carry the 5 s target:
  - the fantomas preprocessor checks every file on each scan, about 10 s (AUTOMATION-1436);
  - the replay waits on per-file plugin caches and on test-prune's extension refresh,
    about 14.5 s (AUTOMATION-1437);
  - options records miss their key on the first plain start after a gate, about 10 s once
    (AUTOMATION-1438);
  - the dbtypes-sync preprocessor, about 2 s.
- **What still abandons check records.** Any disagreement between a replayed result and a
  forced re-check in the nightly full suite. The restart-time half of the criterion no
  longer applies to check records alone.

**Amendment (AUTOMATION-1442): the abandon criterion's detector.** Nothing ran the
replayed-vs-forced comparison above. The detector is `fshw-bench replay` followed by
`fshw-bench replay-gate`, which exits:

- `0` when concluded steps were compared and agree, and some replay answered checks from
  records;
- `1` on any diagnostic difference, or a clean tree published green by one run and red by the
  other;
- `3` when nothing was compared, or nothing was replayed.

The "nothing replayed" refusal comes from measurement. On 2026-11-03 three setups agreed
vacuously:

- a binary built from each tree replayed 0 of 704 checks from the step before;
- a pinned binary across two commits replayed 0 on a GitHub runner (179 on a laptop; explained
  in §27);
- a second checkout of the same tree replayed 153, because the `project-outputs` label differed
  for the rest. A referenced project's built output embeds its checkout's path, so two
  checkouts of the same bytes key most check records differently. That limits the box-wide
  reuse §2 intends. Recorded here; not changed by this amendment. §27 (AUTOMATION-1478) changes
  it: the same setup now replays 351.

Measured, one records-on check plus one forced check:

- **FsHotWatch:** about 9 min each on a laptop and 15 min on a GitHub runner, tests included.
- **intelligence:** a median of 18.4 min per commit and a maximum of 69 min (the AUTOMATION-1440
  replay, 51 forced steps), on two cold daemons of a box the land queue also uses.

**Decision.**

- **FsHotWatch, scheduled here.** `.github/workflows/nightly-replay.yml` runs every night at
  06:17 UTC, plus `workflow_dispatch`, and takes about 34 min.
  - It tests a restart, which is the case check records exist for. One fshw is built from
    main's tip. It checks the tip with records on, then again on a fresh daemon in the same
    checkout (answering from those records), then once more with records off. It gates on
    the last two and uploads the replay and its outcome records.
  - A pair of commits was rejected. A release bumps every project's version, so the
    nightly after each release would replay nothing.
  - A failed scheduled run notifies the repository owner.
  - It exercises check records on a real F# tree with analyzers, lint and in-repo rules,
    under the binary about to be released: a pre-release detector.
- **intelligence: proposed, not yet scheduled.** The criterion guards intelligence's
  `checkRecords: true`, so its detector must run on intelligence's tree. That belongs on
  thellma-and-louisa beside `nightly-unfiltered`, and it is routed to the maintainer rather
  than changed here.
  - Proposed: a LaunchAgent `com.thellma.intelligence.nightly-records-replay` at 03:30 local.
  - In a dedicated workspace it runs `fshw-bench replay --count 1 --from main@origin
    --no-forced --check-cmd "./build.fsx check"` (filing records), then the same with
    forced re-checks into a second out directory, then `replay-gate` on that directory.
  - A non-zero gate reports through `nightly-unfiltered`'s loud-failure path (PostHog Error
    Tracking, then Zulip).

  Until it runs, the abandon criterion on intelligence has only this FsHotWatch-side detector
  and the hand replays.

## 15. What a gate waits on, and the forced scan (AUTOMATION-1370)

1370's handoff (intelligence `results/handoffs/AUTOMATION-1370-2026-10-29.md`) raised three
points.

- **What the gate is waiting on is the lookup's own `Missing` list.** It is not a separate
  field, and it is not a rendering of plugin statuses. `decide` names every requirement with
  no deciding record, which is precisely what the gate waits on. The heartbeat prints that
  list. Which daemon work will *produce* a missing record (`formatTimeoutDetail`'s
  knowledge) is accelerator detail. It may annotate the line, but it is never what decides
  that the gate is done.
- **The forced scan is not waited for** (`forceScanAndWait`'s `WaitForScan(-1)` returns
  at once on a warm daemon). 1370 suggested keying the lookup on the forced scan's
  generation. **Judgement call: no.** A generation is live daemon state, and keying a verdict
  on it is the defect class this ADR removes.
  - Under the lookup, `check` computes `R(c, T)` from the bytes on disk and then waits
    until every requirement has a record. It does not wait for a scan to finish.
  - A scan that did not happen leaves records missing, and missing is never green. The
    wait is therefore correct whether or not the scan was waited on.
  - Until the switch, today's bug stays today's. A shadow `stricter` line is the detector
    for it.
- **A gate under load cannot converge,** because outstanding symbols keep queueing new test
  runs while one is in flight. Under the lookup, convergence is per tree. Once every key of
  `R(c, T)` has a record, the answer is final for `T`. A run queued for keys that already
  have records is redundant work, which the scheduler may drop. It cannot hold the verdict
  open.

## 16. Step 7a, and the analyzer cache's cross-workspace replay

**Step 7a (merged as the first half of step 7).** The test runner records every project it
launched in the box-wide outcome store (`OutcomeStore.openFor`, beside the evidence
store):

- The record's key is `TestEvidenceRun.testActionKey`: K_P's labels as `kp:<label>`, K_P
  itself, and `run-scope` (`full` or `impact`).
- K_P is taken at **launch** and must still hold at completion. A run whose inputs moved
  under it files nothing. That closes the half of 641 and 699 that shadow v1 cannot see,
  a run graded at another tree.
- Passes and fails are both recorded, a timeout as a fail. A run that verified nothing
  (deferred, errored, no match) files nothing.

Step 7b gives the shadow these keys: the test-scope reply carries each project's
completion-time K_P, and `test:<project>` gains the K_P record as an alternative.

**The analyzer cache's replay across workspaces is the box-wide reuse working as
intended** (checked 2026-10-29, after `pin130` replayed 2,611 files on its first gate and
`./build.fsx lint` agreed with 0 findings). The key, `AnalyzersPlugin` `analyzers-merkle-v7`
under `TaskCache.scopeToToolchain`, is content-addressed and workspace-free:

- the file, **repo-relative** (`CachePathIdentity.keyOf`);
- the source bytes;
- the analyzer config chain's content;
- the fail-on-severity threshold;
- `used-signatures`, the shape of what the file resolved upstream;
- `fcs-signature`;
- the .NET toolchain.

The analyzer set enters twice:

- by **repo-relative path** (`analyzer-paths`);
- by **identity** (`analyzer-inputs`, `AnalyzerIdentity`): for an in-repository analyzer,
  the PDB's document checksums, re-verified against the tree on disk, plus its references
  and project file; otherwise the DLL's byte digest.

A disagreement between that receipt and the tree is a refusal, never a fallback. Two
workspaces whose analyzer DLLs differ in any rule therefore compute different keys, and a
replay cannot hide a finding a different analyzer set would report.

The remaining trust boundary is the receipt itself: an analyzer built from sources the tree
still holds. That is the property ADR-011 states.

## 17. Amendment (AUTOMATION-1361 phase 1+2a): the `check` key is canonical

The phase 1 of §14 had no consumer on its own. A persisted record holds no FCS objects, so it
cannot be handed to anything that reads `FileCheckResult`, and every plugin and the cohort seal
read it. Phases 1 and 2 therefore ship as one deliverable, "1+2a", behind a flag that defaults
OFF. It has three steps, each merged on its own while the flag is off:

1. The `check` record, keyed canonically (this section; `CheckRecords`).
2. A `Replayed` arm of the check result, consumed by the FCS ledger, the seal, lint and
   analyzers. A plugin that finds no answer in its own cache demands a real check. A plugin
   that would read FCS objects from a `Replayed` result fails loudly; it never degrades.
3. TestPrune's per-file analysis, replayed from its task cache.

**The in-memory API key cannot be persisted as it is.** `UpstreamFingerprints.ForApi` names
each earlier file by whatever signatures were recorded *before* its table was built.
- A cold scan therefore keys every file by its content.
- A later generation keys the same bytes by signature (`SettleApiIndex` then re-keys).

Persisted under that key, a record would be filed under one key and looked up under another.

**The canonical key.** It is an ActionKey of kind `check` over these labelled inputs:
- `toolchain`;
- `options`, the relative options hash;
- `file`, repository-relative;
- `content`;
- `project-outputs`, the bytes of every referenced project's output. That is stricter than
  the in-memory per-entry guard (ADR-037).
- `upstream`, the chain of §14, with one difference: each earlier file is named by the API
  signature **in its own record, under its own canonical key**, or by its content where the
  store holds no record or the record declares no signature.

Records are filed only in compile order. `CheckPipeline.PersistCheckRecords` walks each
project after the projects it references, and each file after the files before it. So the
writer computes exactly the key a later reader will. A check is filed only while its inputs
still hold:
- its bytes;
- its content upstream, which is exact and history-free;
- its options;
- every project output it read.

Soundness is the in-memory API key's induction, made history-free.

**A miss is never wrong, only slow.** An earlier file with no record is named by its content,
so the files after it miss until it is re-checked and filed. That is the spike's "lazily, in
dependency order".

**What a record carries.** The outputs object (`fshw-check-outputs-v1`) holds:
- the diagnostics, with in-repository paths relative;
- `fcsCheckSignature`;
- `usedSignaturesHash`;
- the file's API signature.

The `options` kind is reserved beside `check` for AUTOMATION-1372 (§18).

**The merge bar for every step.** Tests show four things. A restart replays every filed
check, and the replay equals a forced re-check. An edited file misses. An API edit upstream
misses every later file, in its own project and downstream. A comment edit upstream is cut
off once the edited file is re-checked. A changed option, toolchain or referenced output
misses exactly the files it reaches. A check whose inputs moved before filing is not filed.

The abandon criterion of §14 stands: if a clean restart on intelligence takes over 5 s, or a
replay disagrees with a forced check, the flag stays off.

**Step 2 found that an API signature must not depend on enumeration order.** The
intelligence-scale measurement compared each filed record with a forced live check. 560 of
2,670 API signatures differed, with no source change. FCS enumerates an entity's members and
nested entities in an order that can differ between processes, and `ApiSignature` named them in
that order. A record filed by one daemon would therefore miss in the next one. `ApiSignature`
now sorts an entity's members and nested entities ordinally before naming them
(`ApiSignatureTests` "an entity's members are named in ordinal order"). After the fix, 0 of
2,670 replays disagreed with a forced check, and a clean restart took 2.2–2.7 s with 0 FCS
checks. The in-memory API key changes with it, which is harmless: it was never persisted.

**Step 3: the daemon end to end.** `CheckRecordsRestartTests` runs the real CLI daemon, with
build, lint, a real analyzer and TestPrune, over a fixture repository with check records on.
The first daemon checks, files its records and stops. A second daemon over the unchanged tree
reaches the same green with 4 checks replayed, 0 FCS checks started and 0 demanded. An API edit
made while no daemon ran re-checks the edited file and the files compiled after it, 3 of 4, and
replays the one before it. Each daemon logs this tally as it stops
(`check-records: this daemon: …`). The test found two demand sources, both now fixed:
- **TestPrune never cached a first analysis.** Its `FileChecked` key was refused until the index
  held the analysis, which refused the write as well as the read. A file analysed once had no
  entry, so a restart demanded it. The key no longer asks the index. The state refuses only the
  read (`IFileCacheReadGuard`, `IndexedAnalysis.Guard`). Writing an entry for an analysis that is
  never flushed is sound: its read still needs the index to hold exactly that analysis.
- **Lint and analyzers skip NuGet `_content` sources** without a terminal status, so they cache
  nothing for them. A replay of such a file was demanded only to be skipped. Their states are
  `ISkipsOutOfRepoFiles`, and the framework answers that replay as `Update` would.

A comment edit is not cut off within the scan that re-checks it. The files after it are keyed
by the edited file's content until its new record is filed, so they are re-checked as well.

**A file compiled into several projects is checked under one, and it must be the same one in
every daemon (AUTOMATION-1432).** On intelligence a clean restart still started 15 to 28 FCS
checks, all in files linked into more than one project with `<Compile Include="../…" Link=…>`
and in the files compiled after them. The record key was not at fault: it names the project
through `options` and `upstream`, and pending checks are held by (file, project). The scan
checks a linked file once, under the first project of its tier to reach it
(`Daemon.freshForTier`), and that order was the project graph's hash order, which .NET seeds
per process. Each restart could check the file under a different project, whose record did not
exist, and that miss renamed the file in the project's chain, so the files after it missed too.
Two restarts on the same tree checked `Jj.fs` under GateHook and then under Vcs. A tier is now
listed in ordinal path order (`ProjectGraph.GetParallelTiers`), and `CheckRecordsRestartTests`
covers a file linked into three projects of one tier. A record is still filed only under the
project that checked the file. The other projects name it by content, which is stable, and
checking it under each of them would only buy duplicate checks.

**The startup work before the check tier is not covered by §14's 5 s bar yet.** A restart's
scan runs the preprocessors, then waits for the build plugin to leave Running before any check
or replay, because the checks read the project outputs the build rewrites (ADR-037). A record's
key also hashes those outputs, so serving records before the build would key them on outputs
the build may still replace. It would need every replayed key re-validated after the build.
That changes when a scan's answers are taken, so it is a design change and is not made here. On
intelligence that window was 15 s (18:00:05 to 18:00:20). The logs do not show how it splits
between the `dbtypes-sync` preprocessor and the build, because a `spawn` line names only the
executable.

## 18. Amendment (AUTOMATION-1372): project options are records in the same store

§14 left the project-options cache as a quick win: MSBuild evaluation, through Ionide's
design-time build, costs every daemon restart 4–10 s, and the 1361 spike read the options back
from a JSON file in 0.03 s. §17 reserved the `options` kind beside `check` for it. It is an
accelerator. No verdict reads it, and a miss only costs the evaluation that runs today.

**A stale replay is far worse than a miss.** A wrong option set makes every check of every file
wrong in a way nothing downstream can see. So the key covers what the evaluation actually read,
and the filer refuses whenever it cannot be sure.

**Two kinds, one store** (`OptionsRecords`, `OptionsInputs`):
- **`options-inputs`**, keyed by the request: the checkout root, the project files asked for,
  the toolchain (fshw's, Ionide's and the running MSBuild's assembly identities), the SDK
  MSBuild runs from (its resolved path, which names its version and what `global.json`
  resolved to), and the global properties. Its record names what the last evaluation of that
  request read, and the `options` key it filed. It is only an index: a missing or wrong one is
  a miss.
- **`options`**, keyed by the request and the state, read from disk at lookup, of every input
  that evaluation read:
  - `read:<path>`, a file's bytes: the project files, every resolved import (SDK imports
    included), each project's `project.assets.json`, every compile item the design-time build
    generated under `obj/` (a replay does not regenerate one a clean removed), and every file
    MSBuild opened;
  - `probe:<path>`, a path's bytes, `directory` or `absent`: each path MSBuild asked about (an
    `Exists` condition, a missing import, `GetDirectoryNameOfFileAbove`), and the
    `Directory.Build.*`, `Directory.Packages.props` and `global.json` of every directory above
    each project;
  - `listing:<kind>:<depth>:<dir>/<pattern>`, the names in a directory listing MSBuild made: a
    wildcard import such as `obj/*.props`, and every directory an item glob walked, including
    the SDK's default `**/*.resx` (on for F# projects);
  - `env:<name>`, the value of every variable set whose name the evaluation's files mention as
    `$(Name)` or `GetEnvironmentVariable`, case-insensitively, and of every `DOTNET_*`,
    `MSBUILD*` and `NUGET_*` variable set.

  A record is replayed only when every one of those is byte-identical. A miss names the labels
  that moved.

**How the inputs are found.** After Ionide evaluates, each project is evaluated again,
evaluation only, with Ionide's own global properties (`ProjectLoader.getGlobalProps`), both
without and with its target framework, as Ionide does. The evaluation runs through an
`EvaluationContext` over a recording `MSBuildFileSystemBase`. That records every file-system
question the evaluation asks, so the key is not a guess at what MSBuild reads. MSBuild loads
import XML without asking the file system, so `Project.Imports` adds the imports. The design-time
build reads the assets file after evaluation, so that is added explicitly.

**When nothing is filed.**
- A project failed to load, or none loaded.
- An input's status-change time (`FileStamp`, which no copy can set back) is later than the
  evaluation's start, less the racy window. An absent path is judged by the nearest directory
  above it that exists, so a file read and then deleted during the evaluation is caught.
  Measured on the box: none of the checkout's ancestors (home included) changed in two minutes
  of normal work. A busy ancestor only costs misses.
- MSBuild listed a directory TREE (a recursive enumeration). None did on intelligence or
  FsHotWatch. A walk that follows what MSBuild's follows (hidden directories, directory
  symlinks) need not terminate, and one that does not (`SafeWalk`) would key a different
  listing, so such an evaluation is simply not filed.
- A load with custom properties or a binary log always evaluates.

**What the key does not cover (judgement calls).**
- **Files the design-time targets read after evaluation, other than the assets file.**
  `ResolveAssemblyReferences` reads the package and reference-pack DLLs the assets file and the
  SDK name. Both are immutable per version, and both versions are keyed. A target that reads a
  file the evaluation never names would be missed.
- **Property functions that call `System.IO` directly**, not through MSBuild's file system.
  The `Directory.Build.props` look-up is seen by the recorder in the tests (a test that drops
  the walk-up probes still misses on a new one), and the walk-up probes cover it a second time.
- **Directory listings are names, not contents.** That is what an item glob depends on. Matched
  files whose bytes matter (imports) are `read:` inputs too.
- **The checkout root is in the request key**, so options are never shared between checkouts.
  Every path in them belongs to one checkout, and sharing would mean betting that nothing else
  in an evaluation depends on where the checkout is. One evaluation per workspace is cheap next
  to that bet.
- **`AllProperties` and `AllItems` are not filed.** They are the evaluation's full dumps, which
  fshw never reads, and they would make a record many times larger. A replay carries them empty.

**Verified on real trees** (the spike's method: idle-gated, a fresh process per run, three runs).
- **intelligence**, 25 projects, SDK 10.0.401:
  - MSBuild evaluation took 6.90–7.31 s, plus 1.12–1.20 s mapping;
  - a replay took 87.8–88.9 ms, plus 23 ms mapping, with 0 evaluations;
  - **about 8.1 s saved per restart**;
  - a replay equalled a fresh evaluation for 25 of 25 projects.
- **FsHotWatch**, 14 projects:
  - evaluation took 2.93–3.05 s, plus 0.53–0.60 s;
  - a replay took 45–46 ms, plus 11 ms;
  - **about 3.5 s saved**, 0 mismatches.

A miss costs the listing as well: about 2.0 s on intelligence (9.1 s against 7.1 s) and 1.0 s on
FsHotWatch. A restart over unchanged bytes is the common case, so the trade is taken.
Re-discoveries after a project edit pay the listing. Moving the listing off the discovery path,
after the options are registered, is the follow-up if that cost shows up.

**Flag: `optionsRecords`** (`FSHW_OPTIONS_RECORDS`), OFF by default. It is a sibling of
`checkRecords`, not the same flag:
- the two accelerators fail differently. A stale option set is wrong everywhere, and a stale
  check is wrong in one file;
- §14's abandon criterion stops check records, and must not take the options cache down with it,
  or the reverse;
- each can be measured on intelligence with the other off.

Both file into the same store and share its GC.

## 19. Amendment (AUTOMATION-1428): three shadow bookkeeping defects, none a false green

Intelligence's land workspaces logged 15 `stricter` lines on the server and 3 on the laptop
between 2026-10-30 and 2026-10-31, on fshw 0.14.0-alpha.130 to 132. Each was traced to the
store's records and the daemon log. Today's verdict was right every time. The shadow was
wrong, in three ways.

**A later run's pass did not supersede an earlier fail in the same check (6 lines).** A check
ran a project twice: run 1 failed a test, the outstanding-failure ledger re-selected it, and
run 2 passed it. Today graded run 2 green. The shadow filed every suite of every run at the
publication's one instant. `lookup` breaks a `recordedAt` tie toward `fail`, so the lookup was
red. In one laptop case, the fail and the pass were under different keys (an `impact` fail,
then a `full` pass), so no tie-break could have helped. The fix: a failed suite is not filed
when every failed test is **named** in its report and each one passed in a run the check
attributed later (`supersededFailures`). The order comes from `TestRunReport.CheckRuns`, which
the check keeps in completion order (`runOrder`). This mirrors §4's ledger #10: a failure
stands until a passing record exists. It does not loosen the lookup. A failure nobody can name
(a raw throw, a crashed host), a run of unknown position, or a later run that did not run the
test leaves the fail filed.

**The baseline was pinned to the producer (2 lines and 2 reads).** `test-baseline` keyed on
the fshw binary. The land workspace runs the binary each landing branch pins, which moved
between alpha.124 and alpha.132 in these two days. Each new binary started with no baseline,
while today's watermark (`full-suite-baseline.json`) does not depend on the binary. The tests
a baseline vouches for are built from the tree, and the binary does not change them. So the
key is now the project alone. The `impact` record beside it stays pinned to the tree and the
producer.

**A full-suite run whose publication did not conclude filed no baseline (6 lines).** On
2026-10-30 at 22:49, run 248f25ac earned today's watermark. Its publication was `incomplete`,
and the shadow files nothing for an unconcluded publication, so every check the next day had
nothing to be relative to. A baseline names no tree, so such a publication now files the
baselines of the full-suite run it was graded from, and still nothing about its tree.

**Not fixed, and why.** The four alpha.130 lines on 2026-10-30 rest on a watermark earned at
12:12 that day by an alpha.126 daemon, which predates shadow mode. That evidence was never in
the store, and no keying can find it. The class ends once every binary on a box files
records, and the fix above lets a baseline filed by one binary serve the next. One more class
is predicted, though not yet seen. Today's watermark accepts a red full suite ("accounted
for") and carries its reds as outstanding failures. The store requires a passing full record.
A project that was red in the watermark run, and later passed only in impact runs, is
therefore missing in the lookup while today is green. If it appears, it needs its own ticket.
The `fshw verdict` read also keeps no K_P keys (`OutcomeKeys` comes from the daemon, and a
read has none), so a check that a K_P record alone made green can read as missing.

## 20. Amendment (AUTOMATION-1436): the format preprocessor's outcomes are records

§17 noted that a restart's scan runs the preprocessors before any check or replay. On
intelligence the format preprocessor alone took 9.4 to 10.3 s of a clean restart (2,674 files
checked, 0 rewritten): its memo of "the pinned Fantomas left these bytes unchanged" lived only
in the process, so every new daemon handed the tool every file again.

**The memo is now a `format` record in this store.** The key is an ActionKey of kind `format`
over:
- `recipe`: `fshw-format-settled-v1`;
- `fantomas`: the version the tool manifest pins;
- `editorconfig`: a digest of every `.editorconfig` from the repository root down to the
  file's directory, labelled repository-relative (`CacheInputs.editorConfigInputs`);
- `file`: the file, repository-relative;
- `content`: the file's bytes.

A restart looks each file's key up and hands the tool only the misses. Every checkout of the
repository reads the same records, as for `check` records.

**What is filed.** A `pass` record, only when the tool ran over the file without rewriting it
or failing it, and the key computed after the run equals the key computed before it. A rewrite,
a parse error, a timeout, or a save or `.editorconfig` edit that landed while the tool ran files
nothing. A false hit is the one failure that matters here: the preprocessor is the format stage,
so a record that named unformatted bytes would let a gate pass a file that needs formatting.
`FormatRecordsTests` covers each of these. A mutant that drops both guards turns two of its
tests red; one that drops only the second (the key after the run) turns the
`.editorconfig`-saved-mid-run test red.

**Judgement calls.**
- **The path is in the key.** Identical bytes at another path are not shared. An
  `.editorconfig` section can match a file by name, and a `.fsi` parses differently from a
  `.fs`, so the bytes alone do not decide the tool's output.
- **`.fantomasignore` and `.gitignore` are not in the key.** They decide which files are handed
  over, not what the tool says about one. They are read on every pass, as before.
- **No config key, on by default.** Check and options records are off by default because a
  stale one is wrong in ways nothing downstream sees. A format record is narrower: it can only
  skip a run of a deterministic tool over bytes it has already left as they are.
  `FSHW_FORMAT_RECORDS=0` keeps the memo per process, for measuring.
- **Lookups run in parallel.** A restart reads one record per file.

**Measured** (a fresh process per run, the pinned Fantomas, load average 25 to 35):
- FsHotWatch, 650 files: 4.9 to 7.2 s before, 139 to 140 ms on a restart after.
- A copy of intelligence's sources, 2,529 formattable files (Fantomas 7.0.6): 9.6 to 14.8 s
  before, 325 to 327 ms on a restart after. The first run after the change costs what a run
  cost before (9.9 s), plus filing.

## 21. Amendment (AUTOMATION-1437): a replay goes at the store's pace, not the plugins'

With 0 FCS checks, a clean intelligence restart still spent about 14.5 s replaying 2,674
records. Two waits caused it, and neither was the store.

- **The live-results bound held replays.** Each replay took a place under ADR-054's bound, and
  a place is given back only once every subscribing plugin has committed the result. When
  test-prune spent 6 s refreshing its extensions after the build, the places filled and the
  replay crawled at about 13 files/s. The bound exists for results that carry FCS objects. A
  replayed result carries none, so it now takes no place (`LiveResults.Bound.HoldFor`). Every
  result is still dispatched to every plugin before `BatchChecked`, and the gate waits on what
  it waited on.
- **Each plugin's cache replay re-read the disk per file.** About 1.6 of 2.2 ms of an analyzers
  replay was hashing every analyzer DLL, once per file, in series on the plugin's loop. A key may
  now share such work across a **dispatch batch** (`PluginFramework.DispatchBatch`): the event
  the loop takes plus every message already waiting, all admitted before the batch began. A key
  that reused the work says so. A hit under it replays a result true of the inputs it names. A
  miss is keyed again outside the batch before the handler runs, so a result is only stored
  under a key that read the disk for its own event. §18's rule holds: neither change decides
  anything.

**Measured on this repository** (645 files, warm restarts, debug logs): with the analyzers
alone, the scan's replay fell from 1.33 s to 0.59 s and the analyzers drained in 0.6 s. With
test-prune also running one test project, the scan replayed in 1.5–1.7 s and every plugin had
folded every replay within 0.1 s of the last one.

**Not done here.** Test-prune folds its `BuildCompleted` before the replays queued behind it, so
on intelligence its own replays still begin after its 6 s extension refresh, though the scan no
longer waits for them. Moving that refresh off the fold, and test-prune's per-file replay cost
(about 1.8 ms here), are follow-ups.

## 22. Amendment (AUTOMATION-1438): one evaluation, both start modes

After a cold `./build.fsx check` on intelligence, the next plain `fshw start` logged "no
evaluation of this request is filed" and evaluated for 10.1 s. Two causes were measured. The
request key was never one of them: both start modes compute the same root, projects,
toolchain, SDK and global properties.

- **The cold evaluation was never filed.** In a checkout that was never built, Ionide's
  design-time build writes each project's generated compile items under `obj/` and creates
  `bin/` beside the project while the projects are being evaluated. Those are §18 inputs (the
  compile items, and the project directory an absent probe there is judged by), so the filer
  refused: "...Intelligence.Build.AssemblyInfo.fs changed while the projects were evaluated".
  Every AssemblyInfo in that workspace carried a change time inside the 9 s evaluation, in load
  order. `file` now names every input that moved. When all of them lie in the requested
  projects' own directories, the loader waits for the racy window to pass the moment the
  first load returned, after every write its design-time build made, and evaluates once more.
  The wait rounds up and reads the clock again: truncating a fractional millisecond started the
  second evaluation up to 1 ms inside the window on Linux CI, where it was refused for the first
  build's own write. Judging from the latest change among the first evaluation's inputs instead
  would miss a write only the second evaluation's inputs name. That design-time build finds its outputs written and writes
  nothing, as MSBuild's incremental targets do, so it is filed. A second move, or a move outside
  the projects, still files nothing. §18's rule is unchanged: what is filed came from an
  evaluation during which no input moved.
- **The two modes ran with different environments.** The detached launcher sets
  `DOTNET_INTERNAL_ThreadSuspendInjection=0` for the daemon it starts; a plain `fshw start` runs
  with the runtime default. Every `DOTNET_*` variable was keyed, so a gate-started daemon missed
  what a plain one filed ("inputs moved: env:DOTNET_INTERNAL_ThreadSuspendInjection"), and the
  reverse. `DOTNET_INTERNAL_*` knobs configure the CLR hosting MSBuild, never what it reads, so
  they are no longer keyed by prefix. One an evaluation's files name is keyed like any other.

**Measured on this repository** (30 projects, restored but never built, debug CLI):
- before: the cold start was refused, and the next start evaluated again (8.5 s);
- after: the cold start evaluated twice and filed (14.2 s, against 9.9 s). The next start,
  under the launcher's environment, replayed in 0.1 s, and a plain start after it replayed in
  0.1 s too.

The cost is one extra evaluation, about 4–6 s, paid once by the first daemon in a checkout that
was never built. Every later start in that checkout saves a full evaluation.

## 23. Amendment (AUTOMATION-1441): test-prune's share of a restart's replay

§21 left two costs in test-prune.

- **Its first extension refresh held the fold.** A new process owes every extension a refresh on
  its first flush, which is the `BuildCompleted` a scan dispatches before its checks. On
  intelligence that refresh took 6 s, and every replay queued behind the build waited for it. When
  the refresh is owed only because the process has not refreshed yet (nothing indexed, no failed
  extension), it now runs on its own task, over the index the fold's refresh would have read: no
  flush writes the index until its answer is taken. A `FileChecked` folds while it runs. Every other
  event, a flush, a selection that queries the index, and a `BuildCompleted` that may wait for the
  diff take its answer first. A run launched in the meantime takes the extensions' failures before
  it reads anything, off the fold. What test-prune selects is unchanged.
- **Its per-file replay.** Profiled on this repository (646 records, info logging, quiet restarts),
  test-prune's fold of a replay costs 0.35–0.54 ms, not the 1.8 ms first estimated (that figure
  summed every plugin). Recording the replay's outcome took 80–120 µs of it, nearly all building
  the file's suppression (its `#nowarn` lines and every compiler flag), which only decides whether
  an error blocks. It is now read only when a diagnostic is an error: 5–6 µs per file. What is left
  per file: about 150 µs of task-cache lookup and replay reporting (the framework's, shared by
  every plugin), about 75 µs of key (the source is hashed for the key and again for the key's miss
  fingerprint), about 25–70 µs reading the file's analysis identity from SQLite on a fresh
  connection, and about 25 µs hashing that identity.

Neither change decides anything (§18). Not measured on intelligence: that needs a release.

## 24. Amendment (AUTOMATION-1438, again): a write outside the projects at the first start

§22 did not hold on intelligence. At fshotwatch.cli 0.14.0-alpha.134, a cold `./build.fsx check`
in a fresh workspace still filed nothing ("not filed: ...Intelligence.Build.AssemblyInfo.fs
changed while the projects were evaluated", with no second evaluation), and the next plain
`fshw start` evaluated for 12.2 s. The request key was the same in both modes: the box-wide store
held no `options` or `options-inputs` record for that workspace from the gate's daemon at all.

**Measured cause.** A sampler polled the change time of every directory that judges an absent
input outside the projects (the ancestors' `Directory.Build.*`, `Directory.Packages.props` and
`global.json`, home and the SDK included) while the gate ran in a fresh workspace. In the window
the filing is judged from, exactly one changed: the checkout root, at 13:15:07.104, 0.26 s before
the evaluation started, when `coverage/` appeared in it. test-prune's registration creates
`<root>/<tests.coverageDir>/<project>/` for every test project as the first daemon registers its
plugins, while discovery is already starting. The root is the directory the absent root-level
`Directory.Packages.props` and `Directory.Build.targets` are judged by (§18), and it lies outside
every requested project, so §22's second evaluation never ran. A later daemon finds `coverage/`
there and creates nothing, which is why the next start filed and every start after it replayed.

**Change.** An input that is not there when the filing is judged moved only because the directory
above it did. It is now evaluated once more as a move inside the projects is: after the racy window
has passed the end of the first load, and only once. That second evaluation reads whatever is
there then and is judged by the same rule; a second move files nothing. A file that was read and
then removed during the first evaluation is therefore never filed with that evaluation: the
second one, which read it absent, is. A file outside the projects whose bytes changed is still not
evaluated again: that is an edit, and its own file event re-discovers. "not filed" names an input
outside the projects when one moved, and how many moved, so the next refusal of this kind names
its directory.

Not changed: an absent input is still judged by its directory, so an ancestor that keeps changing
(a busy home directory) still costs a miss, now after two evaluations instead of one.

## 25. Amendment (AUTOMATION-1444): a K_P full fail the check re-ran green

The AUTOMATION-1440 replay of 100 intelligence commits on fshotwatch.cli 0.14.0-alpha.133 logged
19 disagreements. 18 of them were `stricter` checks: today green, the lookup red, with one test
project failing. Step 34 (intelligence c76bd3aebef5) has the shape. Full-suite run 6ef651bd
failed exactly one named Build.Dev test. The graded impact run eb571426 completed later in the
same check, ran that test and passed it. The verdict read 2 s later on the same tree agreed green.

**Cause.** §19's `supersededFailures` governs only what the shadow files under tree keys. The
test runner files its own records under K_P (§16 step 7a): the full run's fail under
`run-scope: full`, and the later impact pass under `run-scope: impact`. Step 7b's alternative
for `test:<project>` is the K_P **full** key, whose newest record stayed the fail. `decide`
reddens a requirement on any failing key of any alternative, so the check was red. The read
saw green because it has no K_P keys (§19).

**Change.** The clearance is a record in the store, so the lookup can see it:

- A failed run whose report names every failure in its summary (no unnamed failed row, no
  `other` row) is filed with the names in its outputs (§14). A timeout names nothing.
- A later **passing** run at the same K_P that passed each named failure of the newest full fail
  files a clearance. The clearance is keyed on that fail's key and run
  (`TestFailureClearance.keyOf`). It must be filed strictly after the fail (a tie goes to the
  fail, as in §2) and by a different run.
- Under `check`, a full fail with its clearance reads as a pass. That is ledger #10 (§4): a
  failure stands until it has passed at the current key. It is the evidence today's check grades
  green on: the full suite ran at these inputs, and every test it failed has since passed at
  these inputs. `confirm` never reads clearances, because today's confirm grades the full-suite
  run itself.

It does not loosen the lookup. Each of these stays red: a failure nobody named, a later run that
failed again or did not pass every named failure, a pass filed before the fail, a newer fail at
the same key (the clearance names the older fail's run), and any `confirm`.

**The tree-keyed records across publications (AUTOMATION-1472).** When check N's graded full run
fails A.x on tree T (today red) and check N+1 on T re-runs A.x green (today green), the shadow's
tree `full` key kept N's fail, so N+1's lookup was red. `supersededFailures` sees only one
publication's runs. `ofVerdict` reads test names only for a project that failed in that verdict.
A re-run after a flake is the normal recovery path, so this would have appeared in real use. The
replay checks each tree once per mode and never exercised it.

The tree-keyed records now get the same clearance:

- A failed suite's test record carries the failures it names (`namedFailures`). The report must
  name every failure it counts and count nothing but passes, fails and skips. A project the
  verdict records as timed out in that run, or one re-run under starvation, names none.
- When a passing suite's record is filed, each of this tree's test keys for its project
  (`full` and `impact`) whose newest record is a named fail is offered to `clearanceOf`.
  The suite's passed names are read from its report (`Suite.Report`) only then.
- The rules are §25's: a different run, filed strictly after the fail, and every named failure
  passed. A pass in the same publication is at the same instant, so it never clears. That case
  stays with `supersededFailures`.
- `check` reads the clearance and `confirm` does not, through the same `lookupIn` as the K_P
  store.

Stricter, deliberately: `namedFailures` also feeds `supersededFailures`. A timed-out suite, or one
with `other` rows, is no longer superseded within one publication either. That matches today's
outstanding-failure ledger, which files a timeout at project level so that a later class-filtered
green cannot clear it (`TestReports.failuresOf`).

## 26. Amendment (AUTOMATION-1443): a read finds the K_P keys the check named

§19 predicted this case and the AUTOMATION-1440 replay showed it, at step 0 (intelligence
6552ffddc769, the workspace's first full suite). The check line agreed green with `recorded`
21. The `fshw verdict` read 3 s later on the same tree and binary was `stricter`. Today said
`applies:green`. The lookup was missing `test:Intelligence.Build.Dev.Tests` and
`test:Intelligence.Tests.Unit`. Only the test runner's K_P records answered for those two, and
a read has no daemon to name K_P keys (`OutcomeKeys` is empty).

**Decision: the read gains the keys. Nothing is exempted.** Each check publication files, per
project, a record naming the K_P key the daemon gave it (`test-outcome-key`, keyed on tree,
producer and project; the key is in its outputs). A read with no keys of its own recovers them
(`withRecordedOutcomeKeys`) and looks them up in the box-wide store, through the same
command-aware `lookupIn` as the check. Two alternatives were rejected:

- **Exempt reads from K_P-only projects.** A read would then say less than the check it reads
  back, and a `verdict` line could agree while the lookup lacked the evidence today's green
  rests on. §8 counts `verdict` reads precisely because a read is the restart case.
- **Recompute K_P in the read.** K_P needs the project model and its options hash. Those belong
  to a daemon, and a read is defined as having none ("no socket, no run").

**Why this is not looser.** The recovered key is addressed exactly as the verdict is: the tree
and the fshw binary. A read on another tree, or under another binary, finds no key and stays
missing. What a key finds is judged as the check judges it: a K_P fail is red, and a cleared fail
(§25) is a pass only for a `check` read, never for `confirm`. The read still files nothing. The
key records are not counted in `recorded`, so a publication that ran nothing still logs
`recorded: 0`.

**The watermark run that was not graded (AUTOMATION-1476).** At step 0 the tree-keyed path also
missed those projects. The check's own full-suite run 9089c471 earned today's watermark, but the
graded run was the impact run after it, and `recordsOf` filed baselines only from a graded
full-suite run. A publication now knows the run its green is relative to (`BaselineRunId`, the
verdict's `FullSuiteRun` baseline). When it holds that run's suites, each passing project files its
`test-baseline` from it, as a graded full run's does.

Only baselines are filed. That run's suites stay `impact` records of the tree, as before, with
their named failures (§25). A `full` record of the tree would satisfy `confirm`, and today's
`confirm` grades only the full suite it ran itself. A project the watermark run failed earns no
baseline. A watermark run the publication does not hold, or a verdict that names none (anything
but green), files nothing. A baseline still names no tree and no binary (§19), so another tree or
binary needs its own impact records, and `confirm` never reads one.

## 27. Amendment (AUTOMATION-1478): a referenced output is named by what a check reads

§14's AUTOMATION-1442 amendment recorded that a second checkout of the same tree replayed only
153 of 704 checks. The `project-outputs` label hashed each referenced project's built output
byte for byte, and an F# compiler output embeds its checkout's absolute path:

- the PDB path, in the debug directory;
- the source file of every range, in the F# signature and optimization data. Those resources
  are deflated, so every byte after the first path moves too;
- a module version id and PE timestamp derived from all of the above.

So two checkouts of the same sources never had the same bytes, and §2's box-wide reuse held
only within one checkout.

**Decision.** A check record names a referenced output by `OutputIdentity`, a hash of what a
dependent's check reads of it:

- the metadata tables, with the columns that are file positions zeroed (a method's or field's
  RVA, a resource's offset);
- the `#Strings`, `#Blob` and `#GUID` heaps, the module version id zeroed;
- every manifest resource by name. An F# resource is inflated where it is compressed, and each
  occurrence of the checkout root is replaced by a placeholder. The pickled length in front of
  it is re-stated as the length after the root, so a path behind a one-byte length in one
  checkout and a two-byte length in another reads alike.

The root is matched under every spelling a build can embed: as the daemon names it, and with
symbolic links resolved (`/var` is `/private/var` on macOS), the longer first. An in-repo `-r:`
assembly in `upstream` is named the same way. A file that is not a managed assembly, has moved
since its plain hash was taken, or has no root to be independent of keeps its plain content
hash.

**What it does not hash, and why that is not looser.** Method bodies, the `#US` heap that only
method bodies read, and the PE and debug headers. FCS types a dependent against metadata and the
F# signature data. It never reads a method body, so a body-only change cannot change a
dependent's diagnostics. The in-memory guard (`CachedCheck.ProjectOutputs`) still hashes the
whole file. A real change still moves the key: an entity, a source file, a method, the assembly's
name, a resource or a type abbreviation (signature data only, no IL). `OutputIdentityTests`,
`CheckRecordsTests` and the real-compiler `OutputIdentityBuildTests` pin each of these. The
format is versioned (`pe-v1`). Records filed under the old key miss once and are collected by GC.

**Measured** with `fshw-bench replay` on FsHotWatch 276ae958: records filed in one workspace,
then a fresh workspace of the same tree replayed against a forced re-check. Both gates said AGREE.

| binary | checks replayed in the second checkout |
|---|---|
| main (276ae958) | 153 of 704 |
| AUTOMATION-1478 | 351 of 704 |
| AUTOMATION-1478, -1480 and -1479 | 704 of 704 |

**AUTOMATION-1479: a reference a project does not compile against is not keyed.** After 1478
the 353 misses were all in `FsHotWatch.Tests` and `TestPrune.Trace.Tests`. Each lists an xUnit v3
test project (`Xunit4RunnerFixture`, `FxTests`) with `ReferenceOutputAssembly="false"`: a
build-order dependency, in `ReferencedProjects` but with no `-r:`. Those builds differ in every
checkout: xUnit names its generated entry-point module randomly once per `obj/`
(`Xunit.AutoGenerated._jWJcAJDyU`), and `[<Fact>]`'s `[<CallerFilePath>]` puts every test file's
absolute path in the `#Blob` heap. Two fixes were weighed:

- **Normalize them in `OutputIdentity`** (mask the module name, rewrite the paths in `#Blob`).
  Rejected. The paths sit behind blob lengths, so a root of another length moves every later blob
  offset, and only a per-column canonical hash of every metadata table would absorb that. The
  module name is visible to a dependent with InternalsVisibleTo. Both would trade soundness
  arguments for a case no check reads.
- **Key only the references a project compiles against.** Chosen. FCS types a project against a
  referenced project only through that project's `-r:`, so a reference without one is read by no
  check: `CheckRecordsTests` pins that B1 does not see A without the `-r:`, as the positive control
  for the exclusion. `walkProject` now keeps a `ReferencedProjects` entry only when its output is
  among the `-r:` options, for both `upstream` and `project-outputs`.

A project that does compile against an xUnit test project would still miss across checkouts, for
the two reasons above. No project here does. A miss is never wrong, only slow.

**AUTOMATION-1480: a commit id is not a key input.** The other anomaly from §14: one fshw across
2721c0648bd9 -> ca4c273eea4c replayed 0 on a GitHub runner and 179 on a laptop. A throwaway run
of that pair on a runner (41630045883) reproduced 0, and every subject filed at both steps
differed in `upstream` alone. On a git-visible checkout the SDK's SourceLink appends the commit id
to the informational version: the runner's generated `AssemblyInfo.fs` read
`AssemblyInformationalVersionAttribute("14.0.0+ca4c273e…")`. F# compiles that file first
(`CompileBefore`), so it led every file's chain, and the same text is in each output's `#Blob`
heap and F# signature data. The laptop's jj repository has no colocated git, so
`Directory.Build.props` turns SourceLink off.

**Decision.** The build metadata (`+…`) of the informational version is not a key input:

- a generated compile item is named in the chain by its content with that argument's `+…`
  removed (`OutputIdentity.generatedSource`);
- an output's identity zeroes the same bytes in its `#Blob` heap and F# resources. A commit id
  is the same length at every commit, so nothing moves.

**Why a check cannot observe it.** The generated file declares no symbol a later file can name,
and a string argument cannot change its own diagnostics. FCS imports from a referenced assembly
only the attributes it acts on (InternalsVisibleTo, AutoOpen, the F# data-version attributes),
and never the informational version. Reflection can read it, but only at run time, and check
records hold diagnostics, not runs. A type provider could read it at design time, and none here
does. The version before the `+`, and every other attribute, still counts: a bump still re-keys.
`OutputIdentityBuildTests` builds one library with two `SourceRevisionId`s and checks that both
the output and the generated file name alike.

Measured with the fixed fshw on a runner, on the same pair (41649087683): 0 -> 179 of 704, the
laptop's number. The 525 left are the projects the release bump reached and the projects that
reference them, whose versions really changed.

**AUTOMATION-1482: TestPrune's analysis is a record too.** In the 704-of-704 replay the second
checkout's daemon still started 704 FCS checks, all demanded by a plugin. A per-plugin tally
(`PluginHost.DemandTally`, now logged when a daemon stops) measured who demanded them:
`test-prune 704`, and no other plugin. Lint, analyzers and format-check answered from their
box-wide task caches (`CacheResidency`). TestPrune's `FileChecked` entry is workspace-local and
may be read only when the workspace's impact index already holds that analysis
(`IndexedAnalysis`). A fresh workspace's index holds none, so every replay was demanded. The
analysis reads the typed tree, which a replay does not carry.

Two answers were weighed:

- **Satisfy the demand from the check records.** Not possible as they stand. A check record
  holds diagnostics and signatures, not the symbols, edges and tests the index is built from.
  Adding them would put one plugin's index rows into every check record.
- **Give the analysis a record.** Chosen. The analysis is a function of the check it reads, so
  TestPrune files it (`AnalysisRecords`, kind `testprune-analysis`) when it analyses a live
  check. The key is the identity the index already trusts to skip re-analysis
  (`IndexedAnalysis.identity`: the source, the compiler signature, and the public shape of
  everything the check resolved outside the file), plus the file, its project and the analyser
  build. Paths are relative to the analysis root.
  - A plugin state can now answer a replayed check itself (`IReplayedCheckReader`). The
    framework asks before demanding a live check.
  - TestPrune answers when the record is filed, and folds the recorded analysis exactly as a
    live one: the index gets the same rows, and the changed-symbol diff and freshness gate run
    on the replayed diagnostics.
  - Any other replay is demanded as before.

The records are filed beside the repository's test evidence, whenever that store is wired. The
false-green guard is `TestPruneIndexReplayTests`. A fresh workspace (empty index, empty task
cache) answers every replayed check from the filed analyses, and its index then selects the test
that calls the edited value. With the records switched off the same test demands all three files
(RED).

Measured with `fshw-bench replay` on 276ae958, second checkout, fresh cache home:

| binary | replayed | FCS checks started | demanded, by plugin |
|---|---|---|---|
| AUTOMATION-1478 + 1480 + 1479 | 704 of 704 | 704 | test-prune 704 |
| + AUTOMATION-1482 | 704 of 704 | 0 | none |

The gate said AGREE.

## 28. Amendment (AUTOMATION-1502): a fresh, land-shaped intelligence workspace, measured

§27's second-checkout figures came from this repository's tree. AUTOMATION-1502 measured
the same sequence on intelligence (25 projects, 2731 checked files), with the fshw it pins
(0.14.0-alpha.137, which contains 1478, 1479, 1480 and 1482). Every run shared one fresh
cache home. Workspace A ran a cold `./build.fsx check`, filing records (2731 FCS checks,
1168 s). Workspace B was then created fresh on the same tree. Workspace C was also fresh,
with `FSHW_CHECK_RECORDS=0`.

| fresh workspace, same tree | records on (B) | records off (C) |
|---|---|---|
| checks replayed / total | 2731 / 2731 | 0 / 2731 |
| FCS checks started | 119 | 2731 |
| demanded, by plugin | test-prune 119 | none |
| FCS scan, build done to all checked | 3 s (+8 s demanded) | 375 s |
| wall time to verdict | 593 s | 767 s |
| test projects reused from evidence | first run 0 of 7 (0 lookups), second run 4 of 6 | first run 0 of 7 (0 lookups), second run 6 of 6 |
| bytes hashed for evidence keys, per test launch | 287 MB | 287 MB |

`replay-gate` on B (records-on run against a forced re-check): AGREE, 1117 diagnostics in
each, both green.

The 119 are the first check tier's files (8 projects). A analysed them but filed no
analysis record (AUTOMATION-1506). Most of the wall time is now the first test run: an
evidence-gap full suite that does not consult the evidence store (AUTOMATION-1508).

**One edit while the daemon is stopped** (a string literal in a function body, compile item
304 of 655 in the largest project):

| | records on (B) | records off (C) |
|---|---|---|
| FCS checks started | 1949 (782 replayed) | 2728 |
| the CheckRecordsRestartTests bound | 1949 | |
| new check records filed | 1 | |

The count holds the bound: the edited file, the 351 files after it and the 1597 files of
its dependents. But 1948 of those checks reproduced records already filed under their
post-edit keys, because no check waited for the edited file's API signature
(AUTOMATION-1507).

## 29. Amendment (AUTOMATION-1508): a fresh checkout's first run consults the evidence store

§28 measured where a fresh workspace's time goes once check records replay: its first test run.
That run is a full suite, because the checkout holds no full-suite baseline (ADR-021), and it made
no evidence-store lookup. Two rules made it so. A run owed a baseline consulted no evidence, and a
run that reused anything never earned a baseline. Each existed for the other: a checkout that
reused but could not earn a baseline would answer "no verdict" for as long as the evidence lasted.

**Decision.** A run owed a baseline looks every project up like any other run, and launches only
the projects that miss. A full-suite run earns the baseline when every project either ran or held
its reuse at completion. A held reuse is a K_P hit: a full-project green earned at byte-identical
inputs (tree, closure, toolchain, options, declared environment), re-keyed when the run completes.
Every one of its tests ran at exactly these inputs, so the watermark's claim (the tests a later
filtered run skips were last executed at the baseline) holds. This is §2's rule for every other
record: a record earned in one checkout speaks for another holding the same bytes.

It does not loosen anything else:

- `confirm`, an explicit full-suite scope and an unreadable verification ledger still make no
  lookups. Those owe a run of everything, or a full suite that passed here.
- A reuse whose key moved during the run is withdrawn and reports a deferral. That project is
  neither passed nor a recorded red, so the run earns no baseline, and the next run looks it up
  again.
- A project with an outstanding failure is never looked up.

`TestEvidenceRunTests` pins this. A fresh checkout of an earned tree launches nothing and earns
its baseline. A non-identical closure, a failing tree and a reuse withdrawn mid-run are the
guards; the last one earns no baseline. A fresh checkout under `confirm`, or with an unreadable
ledger, makes no lookup.

**Measured** with `fshw-bench replay` on FsHotWatch 0a3ab019, one fresh cache home per binary.
Workspace A ran a cold check, filing evidence; a fresh workspace B on the same tree then ran
records on, then forced:

| B's first check | main (0a3ab019) | AUTOMATION-1508 |
|---|---|---|
| evidence lookups | 0 | 3 |
| projects reused | 0 of 4 | 3 of 4 |
| test run | 499.5 s | 4.5 s |
| wall time to verdict | 531 s | 57 s |
| verdict, shadow lookup | green, agree | green, agree |

`replay-gate` said AGREE for both.

## 30. Amendment (AUTOMATION-1507): the early cutoff at a restart

§14 made the API-signature cutoff mandatory, and phase 1 said to "lazily re-check the misses in
dependency order". §28 measured that a restart did not: after one edit inside a function body, the
1949 files after the edited one, in its project and downstream, started their FCS checks at the same
moment as the edited file, and 1948 of them reproduced records already on file.

**Cause.** A scan dispatches every file of a project at once. Each file's record was looked up by a
walk of the store taken before the edited file had been checked. In that walk the edited file has
no record, so it is named by its content, and every file after it, and every project referencing
its project, is keyed differently from its record. Nothing looked again once the edited file's
check had declared its signature, because records are filed only when the scan ends.

**Decision.**

- A replay walk names a file the store has no record of by a check of this pipeline that is pending
  and still holds: exactly what `PersistCheckRecords` would file, under the same canonical key. So a
  file after it is keyed by the signature the edited file just declared. Projects downstream are
  checked after the projects they reference, so their walks see it at once.
- A scan declares the checks it dispatches (`ExpectChecks`). A file whose record misses waits for
  the first file before it in its project that has no record, when that file's check is dispatched,
  has not returned, and the file before it was answered from the store. Then it walks again.
- When the edited file declares the signature its record had, the files after it replay. When it
  declares another, the next miss follows a pending file, so nothing waits for it, and the files
  after an API change are checked at once, as without records. A miss at a project's first file
  waits for nothing, so a cold scan, with no record at all, costs what it did.

Waiting cannot deadlock a scan. The scan queues each project's files in compile order and takes
them first in, first out, so a file a check waits for was taken before it. `EndExpectedChecks`
releases any wait when the dispatch ends, however it ended.

What it does not change is what a replay may answer. A replayed result is still found only under
the key of the inputs it was produced from. A file with errors has no API signature, so it is named
by its content and nothing after it is cut off. `CheckRecordsTests` pins the cutoff in a project and
downstream, against a forced re-check, and the API edit and the file with errors as the positive
controls. `CheckRecordsRestartTests` pins it for a real daemon: a body-only edit while the daemon is
stopped starts 1 FCS check (3 before), while the API edit beside it still starts 3.

**Measured** with `fshw-bench replay` on FsHotWatch: main 0a3ab019, then a commit that edits one
function body in `StringHelpers.fs` (the API is unchanged). Each step ran records on, then forced;
one fresh cache home per binary.

| the edit step | main (0a3ab019) | AUTOMATION-1507 |
|---|---|---|
| checks replayed / FCS checks started | 229 / 504 | 732 / 1 |
| FCS scan, build done to all checked | 48 s | 3 s |
| new check records filed | 1 | 1 |
| verdict, shadow lookup | green, agree | green, agree |

`replay-gate` said AGREE on both steps for both binaries.

## 31. Amendment (AUTOMATION-1506): the byte budget holds what one large tree files

§28 traced the 119 files B demanded to TestPrune analysis records missing from the store. A filed
them, and GC evicted them. §2's GC evicts the keys with the oldest newest record once the store is
over `MaxBytes`, and the default was 256 MB. One intelligence tree files about 300 MB:

- TestPrune analyses are about 120 KB a file (symbols, dependency edges, tests). The largest
  is over 1 MB. There are 2731 of them, about 250 MB on disk;
- check, format and options records add the rest.

The evidence points to GC after A's test run (it runs at evidence filing and at the shadow's
publication). It evicted the oldest keys: options first, then the first check tier's analyses.
Those were filed at 16:45:53–57, and the first record that survived is stamped 16:46:16.
By the end of the measurement the store sat at 256 MB, every check record had been evicted by
the newer analyses, and no log said so, because both callers discard `Gc`'s result.

**Decision.**

- The default budget is 2 GiB, enough for six such trees. Workspaces of one repository at
  different trees share the store, and a land, a fresh agent workspace and the nightly replay
  each file a tree.
- `Gc` logs every eviction: how many keys, how many past the TTL and how many over the
  budget, the kinds, the bytes freed and the bytes left. An eviction is the one way a filed
  record becomes a miss, and it must not be silent.

**Rejected for now: compressing analysis outputs.** The JSON compresses about tenfold, but
compressing it changes the record format and every reader. The budget alone fixes the
measured miss. If a store at 2 GiB still evicts a tree's records in use, compressing the
outputs is the next step. A recency that counts reads, not only filings, is another.

## 32. Amendment (AUTOMATION-1583): GC reclaims from the kind over its share, superseded records first

§31 raised the budget to 2 GiB, "enough for six such trees". On 2026-11-11 intelligence's
store (on the always-on box) was over it again, and one GC evicted 13,504 check keys, 2,794
format keys and 1,383 analysis keys to free 286 MB. The store, read with jq:

| kind | records | keys | on disk |
|---|---|---|---|
| testprune-analysis | 18,087 | 1,335 | 2,125 MB |
| check | 7,065 | 7,065 | 15.5 MB |
| format | 2,781 | 2,781 | 3.1 MB |
| options, options-inputs | 2 | 2 | 3.5 MB |

§31's size estimate held: the newest analysis record of each key averages 122 KB on disk, so
one tree of 2,781 files is about 340 MB. Its capacity estimate did not. 16,752 of the 18,087
analysis records (92%, 1.96 GB) were **superseded**: older records of a key whose newest record
answers every lookup. The newest records of the 1,335 keys hold 162 MB. TestPrune files an
analysis after every live check, and each record carries its checkout and time, so the same
analysis filed by 26 checkouts (6,906 of the records repeat a checkout already filed) is a new
file each time. §2's "re-recording an identical outcome is a no-op" holds only when the bytes
are equal, and an analysis record's never are. The budget filled with copies, not with six
trees.

Then GC evicted by age alone. Check and format records are filed at a scan's start and the
analyses after them, so the oldest keys in the store were the small kinds, and check-record
replay (§17) lost its records to pay for duplicate analyses.

**Decision.** Over budget, GC reclaims only from the kinds over their fair share:

- **The share.** Each kind present may hold an equal split of the budget. A kind that holds
  less keeps all of it, and what it leaves unused is split among the larger kinds. Formally,
  the share is the largest `cap` with `sum over kinds of min(size, cap) <= MaxBytes`. On the
  store above, the small kinds hold 22 MB, so testprune-analysis's share is about 2.1 GB. It
  needs no kind table: a new kind gets a share the day it is filed. The test runner's `test`
  kind and options records are under it by orders of magnitude. A check record is about 2 KB
  and a tree about 6 MB. Even an equal split among six kinds (341 MB) holds about 57 trees.
- **Superseded records first.** In a kind over its share, GC deletes the superseded records
  first, oldest first, then whole keys, those with the oldest newest record first, until the
  kind fits. Deleting a superseded record never changes a lookup. It shortens the key's
  history, the flakiness input, and only in a kind over its share. On the store above this
  alone frees 1.96 GB without evicting any key.
- The TTL still evicts every kind. Kept keys are still never touched. When kept keys hold a
  kind above its share, GC evicts the oldest other keys until the total fits, as before.
- The log line says how many superseded records it deleted, of which kinds, and the share.

**Not chosen now, measured:**

- **Compression.** A gzip of a sample of 300 analysis records is 17 times smaller (33.9 MB
  to 2.0 MB). It would delay the problem, but it does not fix the eviction order: a kind
  that is 17 times smaller still evicts check records first when it fills the budget. It
  also changes the record format and every reader. It is still a valid next step if the
  answers alone outgrow the budget, which happens at about six trees of analyses.
- **Recency by reads.** It cannot be measured on this store: GC reads every record of every
  key on each run, so file access times show GC's reads, not lookups. With duplicates
  trimmed first and shares, no record in use was shown to be evicted for lack of read
  recency.
- **Not re-filing an analysis already filed.** This removes the duplicates at their source
  and is worth doing. It is a TestPrune plugin change. The budget rule above must hold
  whatever any one producer files.
