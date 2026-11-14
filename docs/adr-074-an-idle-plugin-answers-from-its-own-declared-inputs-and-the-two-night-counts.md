# ADR-074: An idle plugin answers from its own declared inputs, and the two night counts

A configured plugin that sat idle on a tree is answered by its last outcome when, and only when,
it declares its own inputs and they are unchanged. This is ADR-059 §3's "each plugin's own
declared inputs replace the tree digest", for the one plugin kind whose inputs its configuration
states: a file command with a `pattern`. Every other plugin keeps the whole-tree key, and when it
is missing the lookup names it as such. This ADR also fixes what "night N" means for the two
counts the ADR-059 switch waits on, so nobody counts one of them as the other. AUTOMATION-1577,
under AUTOMATION-1362.

Status: Accepted (2026-11-14). Prioritised by the supervisor ahead of the rest of Phase 1;
designed and built test-first.

## Context

A file command runs only when a file its `pattern` matches changes. On every other tree it files
nothing, and `verdict.json`'s `plugins[]` leaves it out. Since AUTOMATION-1568 (ADR-059 §33)
R(c, T) requires every configured plugin. Keyed by the whole tree, an idle file command was
therefore missing on every tree it did not run on, and the lookup never answered green on a
repository that configures one.

Measured on thellma-and-louisa on 2026-11-14: every intelligence checkout with an alpha.144–148
daemon that is green today reads lookup-missing on `plugin:file-cmd-*.fsx` (30 checked; 5 also
on `plugin:changelog-gate`). alpha.148's own shadow line for one such tree logs the same. The
lookup-agreement evidence the switch waits on could not accrue.

## Decision

1. **Eligibility is a type derived from the configuration.**
   - `PluginInputs.LookupEligibility = DeclaredInputs of first: InputSpec * rest: InputSpec list |
     NoDeclaredInputs of reason`. The non-empty pair makes "declares inputs, but none" impossible.
     (FsHotWatch.Core is not referenced from this assembly, so it is not Core's `NonEmpty`.)
   - `DaemonConfig.requiredPlugins` derives each required plugin's eligibility. Only a file command
     with a `pattern` and no `afterTests` declares inputs: `InputSpec.Matching(pattern, entry)`.
   - Every other plugin reads the whole tree, or (an `afterTests` command) test outcomes, and gets
     `NoDeclaredInputs` with that reason.
   - There is no default: a plugin cannot be lookup-eligible without saying what it reads.
2. **A declared input set is what runs the plugin.** A pattern declares the files under the
   repository root it matches, in its watcher's own scope (`PluginInputs.matches`): outside the
   directories the watcher's stream drops and outside build output. `PluginInputsTests` pins that
   scope against `Watcher.acceptsUnderRoot`. So intelligence's `changelog-gate` declares
   `CHANGELOG.md`, although the file is a `verdictInputs.notInputs` of the tree hash: the plugin
   reads it, and keying the plugin on it strands no verdict, only that plugin's requirement.
3. **The key is `plugin-inputs`.**
   - It holds the plugin, the producer, and per declared set a digest over: each match's content;
     each directory the walk could not see, as unreadable (fail closed, as ADR-072 is one level up);
     the plugin's own configuration entry (`entryDigest`: name, pattern, command, args, timeout);
     and the tool-known root files (`.fshw.json` aside, which the entry stands for).
   - It is a new kind, so no record keyed before collides with it, and no other key moves.
4. **The shadow files and reads under the key the eligibility names** (`ShadowVerdict.pluginKeyFor`).
   - A publication files each plugin's outcome under it, from its own checkout as it is.
   - An idle plugin files nothing and is answered by the record an earlier tree with the same
     inputs filed: that carry-forward is the remedy.
   - A changed match finds nothing until the plugin runs again. An absent record is never green.
5. **A missing plugin with no declared inputs is named.** `ShadowVerdict.noDeclaredInputs` gives
   each such requirement with its reason, and the shadow line carries it as
   `lookup.noDeclaredInputs`. A reader tells "only a run on this tree answers, and none did" from
   "nothing on record".
6. **A miss means run (ADR-059 §2).** The shadow store is per checkout, and an idle file command
   never runs by itself. So at a daemon's start, each declared-input plugin with no record for its
   inputs as they are now runs once (`ShadowVerdict.runUnrecordedDeclared`, through
   `PluginHost.RerunPlugin`).
   - Once one has run on its inputs, later starts run nothing.
   - **Behaviour change:** such a command runs at start although nothing it matches changed, so one
     that would fail turns that first check red where it stayed green before.
   - It is off with the shadow (`FSHW_OUTCOME_SHADOW=0`).

## Why this is sound (rulings of 2026-11-14)

- **Two states, no third.** `LookupEligibility` has exactly two cases, and each names a key:
  `DeclaredInputs` is the `Matching` scope (the plugin's own inputs), and `NoDeclaredInputs` is the
  `WholeTree` scope (the tree digest, with the reason). No state means "no key", so no requirement can
  be met by absence: a key with no record is missing, never green.
- **A pattern's key covers every file the pattern matches,** in its watcher's scope, whether or not
  the file is a tree input. `verdictInputs.notInputs` means "does not invalidate the TREE", not "does
  not invalidate the plugin that reads it". Otherwise a checkout whose `CHANGELOG.md` is broken would be
  answered by an older pass carried forward: a false green that no run of the gate ever produced.
- **The enumeration goes beyond the verdict walk, for the plugin key only.** `PluginInputs.matches`
  walks the repository root with the watcher's own exclusions, so root files such as intelligence's
  `xake-build.fsx` are in the `*.fsx` key. The tree-hash algorithm is unchanged, so there is no cold
  cache. The enumeration costs a walk of the root per pattern on every lookup, and experiment E5 measures
  it against the 150 ms budget.
- **Every whole-tree plugin has a record on every concluded check.**

  | Plugin | Runs on |
  |---|---|
  | `build` | any file change (`FileChanged`), and a check's first scan |
  | `format` (preprocessor) and other preprocessors (`dbtypes-sync`) | every batch of files a check delivers |
  | `format-check` | any file change |
  | `lint`, `analyzers` | every file FCS checks (`FileChecked`); a first scan checks every file |
  | `coverage` | every test run (`TestRunCompleted`) |
  | `test-prune` | every check (`FileChecked`, `BatchChecked`, `BuildCompleted`) |
  | an `afterTests` file command | every test run of the projects it names |

  Within a daemon's life a plugin's last status carries into every publication, and a restarted
  daemon's first scan delivers every file. So a concluded check reports every whole-tree plugin.
  - Measured on thellma-and-louisa: across 253 daemon-green publications of alpha.140–149, the only
    plugins ever missing were the two pattern commands (`file-cmd-*.fsx` 170 times, `changelog-gate`
    20 times), never a whole-tree plugin.
  - The carried status is a separate, pre-existing gap: the verdict does not say whether a plugin ran on
    this tree (AUTOMATION-1702). It applies to the tree key exactly as before.

## The two night counts

The switch (ADR-059 §8, §35 Change 2) waits on two independent properties. Each counts its own
nights, and a report never quotes one as the other.

**(a) RECORD CORRECTNESS: every audited stored record equals its clean recompute.**
- A night counts when the nightly record audit (FsHotWatch's `nightly-replay` workflow,
  `fshw-bench audit --kind all --sample 0`, AUTOMATION-1633) reports `recordsVerdict: agree`.
  That means no mismatch and no vacuous kind, over every shared record, at least 200 per kind.
- It counts the records' own verdict, `Report.RecordsVerdict`, never the overall exit code, which
  (b)'s alarm also moves.
- A mismatch resets it to zero.
- It counts from the night of 2026-11-14. It is unaffected by this ADR: no record kind's key changes
  except the new `plugin-inputs`, which the audit compares like any kind.

**(b) LOOKUP AGREEMENT: on every tree the daemon grades green the lookup answers green too, and
never green where the daemon grades red.**
- A night counts for a repository when, over that night's publications in its shadow logs, every
  daemon green is a lookup green (100%) and there is no lookup green on a daemon red.
- It is read by `fshw audit --shadow-log <log>` (or `fshw-bench audit --shadow-log`), which reports
  the rate and the count.
- A night with no daemon green does not count, and does not reset.
- A lookup green on a red resets the count, and is a defect ticket of its own: it is a false green.
- **For intelligence, (b) starts counting on the first night intelligence pins an fshw that
  contains this ADR.** Before that, the lookup could not answer green there, so (b) is at zero
  whatever (a) says.
- FsHotWatch's own nightly reports (b) for FsHotWatch's tree from tonight.

So a status reads "(a) night N", "(b) night M (since the pin of <version> on <date>)", never
"night N of 14" alone.

**The alarm.** The nightly audit fails the night as VACUOUS (exit 3, the class of a kind that
compared nothing) at 0% agreement, or when no publication was graded green. It fails it as
MISMATCH (exit 1) on any lookup green on a daemon red. The 0% alarm is a smoke alarm, not the
criterion: (b) needs 100%.

## Consequences

- An idle file command answers from its last outcome while its matches are unchanged. The
  `stricter` lines it caused stop, and the lookup can answer green on intelligence.
- A file command now runs once per checkout per input digest at daemon start, when nothing is on
  record: one `fsi --typecheck-only build.fsx` and one `changelog-gate` for intelligence's
  configuration.
- Files a pattern watches but the tree hash does not are now in their plugin's key; the tree hash is
  unchanged. For intelligence that is 91 `*.fsx` matches outside `src/`, `tests/` and `verdictInputs` (80
  of them gitignored scratch under `.tmp/`); AUTOMATION-1710 asks intelligence to declare or state them,
  and proposes the load-time warning and honouring `.gitignore` in a pattern's scope.
- `afterTests` file commands and every whole-tree plugin keep today's key. Keying an `afterTests`
  command on its projects' K_P set (an upstream input, ADR-059 §38) is future work.
- On FsHotWatch's `main`, this lands before the Phase 1 stack, which moves `LookupVerdict`,
  `Requirements` and the store into FsHotWatch.Core and rebases onto it.
