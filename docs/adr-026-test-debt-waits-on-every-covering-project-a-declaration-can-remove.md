# ADR-026: Test debt waits on every covering project a declaration does not remove

Status: Accepted (2026-09-17).

## Context

TestPrune owes a changed symbol a green run of every test project whose tests cover it.
Debt could leave the queue without such a run in three ways:

1. **A coverer the daemon does not run was dropped.** The symbol index records tests from
   every project it analyzes, and `tests.projects` names fewer. A symbol whose only
   coverers were unconfigured was dropped from the queue, logged as a write-off, and the
   check went green. Nothing had run those tests. A symbol with both configured and
   unconfigured coverers retired as soon as the configured ones passed.
2. **The per-project ratchet mapped and wrote in separate transactions.** It looked up the
   symbol id for each covered line on one connection and inserted the ids on another. A
   graph rebuild that deleted a mapped symbol in between turned the insert into SQLite
   error 19 (FOREIGN KEY constraint failed). A test that injects the deletion between the
   two steps reproduced this on main.
3. **A BootScan cohort borrowed a held full run for edits made after it sealed.** A cold
   cohort that seals during a requested full run attaches its symbols to that run instead
   of queueing a second one. Main attached symbol names only. An edit made after the seal,
   even one that restored the sealed bytes, was retired when the held run passed, and that
   run had never built the edit.

## Decision

**One exclusion policy, applied at every debt classification.** A covering project holds a
symbol's debt unless it is unconfigured and `tests.excluded` declares it with a non-blank
reason. The flush that drops symbols, the launch that records each symbol's coverers, the
completion that retires them (launch and BootScan candidates alike) and the residual status
message all use this one rule. A configured project stays required whatever an exclusion
says. An unconfigured, undeclared coverer keeps the symbol owed. The verdict stays red and
names the project until the config lists it or excludes it. Excluding a project does not
show that its tests passed. It only removes that project from this gate's claim. Symbols
dropped because of a declaration are still reported as `changes-uncovered` with the
declared project named.

**Exclusions resolve to the identity the index uses.** The index keys tests by project file
name without its extension. `SolutionScope.createExclusionResolver` reads the solution once
and resolves each declaration against the current discovered project inventory every time
it is called. It refuses a declaration in each of these cases:

- It names no solution project, or its alias matches more than one.
- It has a blank reason.
- The inventory does not contain the declared project itself.
- The inventory contains another project with the same file name, in any letter case.

A directory name can locate a solution entry, but the resolver always returns the
discovered file name. It extends the existing `tests.excluded` declaration; no second
declaration exists. A refusal throws. The completion handler resolves before any side
effect, so a refusal there settles the handler as a failure and every symbol stays owed.
Flush and launch resolve lazily, only when they meet an unconfigured coverer.

Solution project paths are now read relative to the solution's own directory. Main stripped
every leading `.` and `/` character, which made rooted and parent-relative paths wrong and
made a solution kept below the root name projects that do not exist.

**Mapping and write share one IMMEDIATE transaction.** The lookup runs inside the transaction
that writes the rows. A concurrent symbol deletion waits for the commit, and the FK cascade
then removes the obsolete points.

**BootScan retirement requires the captured revision.** Every enqueue stamps the symbol with
a new in-memory revision, including a re-edit of a symbol that is already queued. An attached
cohort keeps the first revision it captured. A late candidate retires only when all three
hold: the run was actually full, the run's launch and completion input trees match, and the
symbol is still at its captured revision. Ordinary launch-set retirement is unchanged.

## Consequences

- A repository whose indexed test projects are not all configured or declared will see a
  red `check` that names those projects. This is the intended visible cost. The fix is one
  line of config. This repository declares its xUnit v4 runner fixture, which discovery
  indexes, and lists the fixture in `FsHotWatch.slnx` so the declaration can resolve.
- While such debt is outstanding, the zero-test skip is refused and every build runs the
  configured suite. No run can discharge the debt, and the status says so instead of
  "waiting on build".
- Exclusion resolution needs a discovered inventory. A completion that lands during project
  rediscovery refuses to classify and stays red until the next completion.
- `createWithScope` is the new public constructor. `create` keeps its signature and declares
  no exclusions. `TestPruneState.BootScanDebtDuringFullRun` changes from `Set<string>` to
  `Map<string, int64>`.
- Revisions live in memory only. A restart has no held run for an edit to borrow.

## Rejected

- **Keeping the drop and relying on the warning.** A log line next to a green verdict is the
  silent write-off this decision removes.
- **Resolving exclusions once at registration.** A project discovered later with a colliding
  file name would then be exempted without any check.
- **Matching exclusions by directory name.** The index cannot see directories, so a
  same-named directory elsewhere could exempt a different project.
