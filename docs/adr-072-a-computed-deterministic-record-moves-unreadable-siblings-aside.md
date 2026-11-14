# ADR-072: A computed record of a deterministic action moves unreadable files under its key aside

An unreadable file under a key made that key a miss for as long as the file stayed unreadable.
That held even after a fresh run on the same inputs filed a new record beside it. Reads keep the
AUTOMATION-1568 rule unchanged. A writer that has just COMPUTED a record of a DETERMINISTIC action
now moves the unreadable or foreign files under its key into quarantine, by rename. Test runs keep
1568 exactly. Every quarantined file stays visible in `fshw audit`. AUTOMATION-1701, found by the
Phase 1 simulation harness (AUTOMATION-1695).

Status: Accepted (2026-11-14). Decided by the team lead and accepted by the supervisor, with three
conditions (below).

## Context

AUTOMATION-1568 made an unreadable file under a key a miss for the WHOLE key, and moves nothing.
The file may be the newest fail. Answering from the readable rest would give the older pass, and
quarantining the file would make that answer permanent.

The simulation harness found the cost of that rule. Once a record file is unreadable (a mode, an
owner, a persistent I/O error), the key never answers again. A fresh run files the same key under
a DIFFERENT name, because the payload carries `recordedAt`. The new record is filed, but the
unreadable sibling still makes the whole key a miss. Every gate on that tree reports "no verdict"
for that requirement and runs it again. This is never a false green, but the lookup never reaches
the clean verdict, which is the completeness half of the plan's theorem. The failing seeds were
73 (disk) and 52 (in-memory).

The first fix, approved as AUTOMATION-1701 (a), was "a writer replaces an unreadable file of the
SAME name". It was built (FsHotWatch eca22667) and did not clear the seeds, because the sibling
does not share the new record's name. It is folded into this decision. Its tests are cases of
the rule below.

## Decision

1. **Reads still move nothing unreadable** (AUTOMATION-1568).
2. **Only a COMPUTED record acts.** `IOutcomeStore.FileComputed` takes `OutcomeRecords.Computed`,
   a distinct type made only by `Computed.justRan` (`OutcomeStore.justRan`). Call sites use it
   only where the action ran in this process. A record that is replayed, copied, or projected from
   a verdict is filed with `File`, which never moves anything. A replay therefore cannot launder a
   key whose only real evidence is the unreadable file (supervisor condition 1).
3. **The action decides, from the type.** `SiblingPolicy.ofRan` maps `Ran.NonTest` (a check, an
   analysis, an options evaluation, a format pass, a TestPrune analysis, a suspect-recheck receipt)
   to `QuarantineUnreadable`, and `Ran.TestRun` / `Ran.InRun` to `KeepUnreadable`. No flag.
4. **What a computing deterministic writer does.** It files the record write-once. Only when that
   filed (now or already), it moves every file under the key that cannot be read, or is not a
   record of the key, into `quarantine/` by rename, never deleting. If its own name was one of
   them, it files again. When a move or a write fails, the result is `NotFiled` or the key stays a
   miss; nothing throws into the run.
5. **Test keys keep 1568 exactly.** A flaky suite can fail in a concurrent run that files AFTER
   ours. If that newer fail is the unreadable file, moving it aside lets our older pass answer, a
   false green. Nothing orders the two without filesystem time, which ADR-008 rules out. Soundness
   outranks completeness, so the key stays a miss. The simulation harness narrows its completeness
   property for test keys holding an unreadable sibling, and pins that such a key STAYS a miss.

## Why "cannot be read" is never another writer mid-write (supervisor condition 2)

Every writer of record files was audited (2026-11-14, FsHotWatch at this change). There is one:
`OutcomeStore.Store.writeOnce`. It writes a temporary file in `<store>/tmp/`, on the same file
system as `records/`, then renames it into place. The rename is atomic, so a reader opens a whole
record or no file at all. The other operations on record files only delete or rename whole files:
`Supersede` deleting the key's other records, GC, and quarantine. A read can fail on a file it may
not open, on an I/O error, or on a file that vanished under a supersede or a GC; moving a vanished
file fails harmlessly. No other code writes under `records/` (grepped: `OutcomeStore.fs` only).

## Visibility (supervisor condition 3)

Every move into quarantine, by a read or by a computing writer, leaves a note in
`<store>/quarantine-why/`: the key, the file, who moved it (`read`, or `writer` with the record's
kind), why, and when. `OutcomeStore.readQuarantine` reads the census without changing anything:
the noted files, plus how many quarantined files carry no note (moved before notes existed).
`fshw audit` and the nightly audit print every quarantined file by key, mover, kind and time, and
the JSON (`fshw-audit-v1`) carries them as `quarantined` and `quarantinedUnnoted`. GC never touches
`quarantine/` or `quarantine-why/`: nothing is collected silently.

## Consequences

- A deterministic key with an unreadable sibling answers again after the next computed run on
  its inputs. Seeds 73 and 52 are named regressions in the harness.
- A test key with an unreadable sibling stays a miss until the file reads again or GC evicts the
  key, exactly as before.
- Shadow publications (`ShadowVerdict.observeWith`) project a verdict's outcomes. The verdict does
  not record whether a plugin ran in this check or was answered from its cache, so they file with
  `File` and never quarantine. A shadow `plugin:*` or `diagnostics` key with an unreadable sibling
  stays a miss until GC; the simulation harness excuses exactly those keys and proves quarantine on
  computed direct-action records instead. Recording per-plugin provenance in the verdict is
  AUTOMATION-1702.
- `quarantine/` grows by what writers move. That is the visibility the supervisor asked for, not
  a leak: the audit counts it.
