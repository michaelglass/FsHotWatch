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
  speaks for the other. Every API takes its root explicitly. Tests use a temporary root
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
accelerator, and no verdict reads it.

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

The `options` kind is reserved beside `check` for AUTOMATION-1372.

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
