# Changelog — FsHotWatch.Cli

## Unreleased

## 0.14.0-alpha.36 - 2026-09-01

- fix(AUTOMATION-608): recognize daemon-side corrupted-frame faults wrapped by
  StreamJsonRpc so the CLI restarts the daemon and retries once.
- fix: retain an executed test run when a same-tree convergence scan reports it
  already verified, instead of discarding the earned scope and verdict.

## 0.14.0-alpha.35 - 2026-08-31

- feat(AUTOMATION-358): add the bounded `fshw invalidate` command. It clears all
  task-result cache entries for the current workspace and preserves the warm daemon.

## 0.14.0-alpha.34 - 2026-08-31

- Finish: AUTOMATION-357 refuse unexplained red verdicts
- Finish: AUTOMATION-357 keep subset test receipts out of the whole-tree cache


## 0.14.0-alpha.33 - 2026-08-30

- chore: rebuild to bundle updated dependencies


## 0.14.0-alpha.32 - 2026-08-30

- feat(AUTOMATION-315): `tests.projects[].coverage` now accepts independent
  `enabled` and `collectForImpact` booleans. This lets integration projects feed
  project-attributed runtime impact selection without joining the consumer
  coverage ratchet. Missing or seven-day-stale complete baselines widen to the
  whole configured project and name the reason.

## 0.14.0-alpha.31 - 2026-08-29

- Fix: satisfy analyzer in A67 quarantine regression
- Finish: AUTOMATION-67 preserve literal selections and quarantine prior reds


## 0.14.0-alpha.30 - 2026-08-29

- chore(deps): bundle TestPrune.Core 8.1.5 and reuse the daemon's completed FCS
  payload for impact analysis instead of running a second compiler pass. One
  shared per-file traversal budget bounds all live FCS symbol walks; typed-AST
  literal extraction avoids reflection-driven allocation, and pathological
  cyclic, deep, or high-fanout graphs fail closed.

- fix: defer rebuilds for the complete lifetime of active test hosts, including
  cache-replayed runs whose start boundary is synthesized, so a build cannot
  rewrite instrumented binaries before their matching test completion event.

- AUTOMATION-558: `tests.extensions` now supports explicit SQL attribution:
  `{"type":"sql"}` creates TestPrune's automatic `ReadsFrom`/`WritesTo`
  extension, while `{"type":"sql-hydra","generatedModulePrefix":"…"}`
  creates its generated-query extension. Configuration is typed per kind,
  preserves declared factory order, and fails closed for unknown kinds or
  missing fields. Both integrations use the public TestPrune.Sql and
  TestPrune.SqlHydra 0.1.0 packages.

## 0.14.0-alpha.29 - 2026-08-28

- fix: bundle TestPrune.Falco 3.1.3 so `>]` inside an attribute string no longer
  truncates the attribute while selecting tests, with its required
  Microsoft.Data.Sqlite 10.0.11 floor.

- AUTOMATION-581: IPC recovery now distinguishes a proven corrupt/oversized
  StreamJsonRpc frame from a genuine CLI `OutOfMemoryException`. Only the former
  restarts and retries the daemon; a client OOM preserves the healthy daemon and
  reports the client memory failure in the headline (never as a daemon-connection
  failure). A lifecycle regression test proves the same workspace-owned daemon
  remains reachable by the next command without a shutdown or replacement. An
  `OverflowException` likewise authorizes a restart only when its stack proves it
  arose in StreamJsonRpc's header-delimited frame reader; a bare overflow does not.

- AUTOMATION-290: daemon-backed `check`/`confirm` and `--run-once` now fail fast
  when MSBuild loads zero of the discovered projects. Both paths overwrite any
  prior green with an exit-2 `incomplete` verdict carrying the exact loader
  diagnosis; run-once awaits discovery again at every scan, settle, and
  convergence boundary, while `confirm` does not force tests after discovery has
  already made a run impossible. The app-local `Microsoft.NET.StringTools`
  floor is enforced by local CI, package/release tasks, remote CI, and the
  tag-triggered CLI NuGet release workflow itself. Its guard shares the exact
  restore and Release build consumed by the no-build pack, so a manually pushed
  CLI tag cannot bypass the built-manifest guard or publish a differently resolved
  artifact.

## 0.14.0-alpha.28 - 2026-08-26

- **AUTOMATION-165: a verdict from a different tree-hashing SCHEME no longer validates.**
  `verdict.json` has always recorded `treeHashAlgorithm` and `applicability` never read it;
  the producer check masked that, which is exactly why it was easy to leave wrong. A
  mismatch is now `Applicability.StaleAlgorithm` — `applies: false`, exit 4, reported as a
  different scheme rather than a different tree, and checked BEFORE the two hashes are
  compared as strings.
- `verdict.json` gains `treeDeclaredCount` and `treeAbsentDeclarationCount`: how many files
  the repo's `verdictInputs.hashed` declarations contributed, and how many declarations
  matched nothing. Recorded because an ignored declaration and an honoured one look
  identical from outside — which is how `verdictInputs` sat inert in a consuming repo while
  reading as protection. Absent in older verdicts, which read as 0.
- `loadConfig` REFUSES a `verdictInputs` declaration it cannot honour as written (no `path`,
  no stated reason, a duplicate, a path declared as both an input and a not-an-input, a path
  outside the repo). A declared path that is not on disk yet is a warning rather than a
  refusal — an analyzer assembly does not exist until the first build — and the guarantee is
  carried structurally instead, by the absent-declaration entry in the tree hash.

## 0.14.0-alpha.27 - 2026-08-25

- chore: rebuild to bundle updated dependencies


## 0.14.0-alpha.26 - 2026-08-24

- **`confirm` now records a real check-vs-confirm sample on the runs that produce one —
  which is all of them (AUTOMATION-259).** The comparison shipped in 0.14.0-alpha.16 and
  then measured nothing: `confirm` sends `set-scope full` BEFORE the scan that provokes
  the test run, so the run is unfiltered by construction, the capture condition
  ("did confirm escalate?") is false every time, and every verdict recorded
  `divergence: "no-impact-scoped-run"` — a true statement that produced, across
  seventeen confirms in ten days, zero comparisons.

  The suite still runs exactly ONCE. The impact selection is now RETAINED at the moment
  `confirm` widens past it, and the run's own result is projected back through it to
  derive what `check` would have concluded. `checkComparison.impactScopedRun` gains a
  `basis` field — `"executed"` when `confirm` escalated and the impact-scoped run really
  ran, `"projected-from-full-run"` when it did not have to. A verdict written before the
  field reads as `"executed"`, which is what those samples were.

  A projection answers about REACH only: whether `check`'s selection would have executed
  a test the run saw fail. It can never claim a `check-only-failures` — that would assert
  an order- or isolation-sensitivity one execution cannot observe — and every way of not
  being able to decide (no projection on offer, a projection belonging to another run, a
  project-level red under a class filter, a residue of reds fshw cannot attribute to this
  tree) lands on `incomparable`, never on `agreed`.

## 0.14.0-alpha.25 - 2026-08-23

- fix(AUTOMATION-245): the replay gate was correct and switched OFF in every live daemon
- docs: remove three stray diff3 base markers from the changelogs


## 0.14.0-alpha.24 - 2026-08-23

- **A test project in the solution but not in `.fshw.json` is now a `config error`, not a
  silence (AUTOMATION-158).** `confirm`'s `scope: {"kind":"full"}` counted only
  `tests.projects`, so "full" meant "the suites I was told about"; a suite in the solution
  and in no gated list was not reported as UNRUN, it was simply absent. Every config load
  now reconciles `tests.projects` with the solution at the repo root and refuses (exit `2`)
  on an undeclared test project, an exclusion with no reason or naming nothing in the
  solution, a project both gated and excluded, a gated project the solution lacks, an
  ambiguous set of root solution files, or a scan that recognised nothing at all.
  - **BREAKING for a repo with an undeclared test project**: it stops loading until the
    project is gated or declared.
  - **New config: `tests.excluded`** — `[{"project": …, "reason": …}]`, reason required and
    non-blank. The only sanctioned way for a solution test project to be out of scope.
  - **New config: `tests.solution`** — names the authority when the repo root holds more
    than one `*.slnx`/`*.sln`.
  - **New verdict field: `scope.excluded`** — the declared gaps, project and reason, on
    every scope kind. `null` (not `[]`) on verdicts written before the field existed: "does
    not say" is not "nothing was excluded".
  - A repo with no solution file, or with no test projects configured, is left alone.
  - **BREAKING (F# API)**: `Verdict.create` takes `SolutionScope.Exclusion list option`
    between the tree and the outcome; `DaemonConfiguration.Tests` gains `Excluded` and
    `Solution`. New module `FsHotWatch.Cli.SolutionScope`.


- fix: **a tree that changes while a check is finishing is now actually caught** — both
  `check`/`confirm` and their `--run-once` twin exit `2` and record `incomplete`
  (AUTOMATION-167). The mid-check tree-move detector compared two hashes it took
  ITSELF, at publish time; everything between the daemon settling and the verdict being
  written — reading diagnostics, reading the test scope, rendering the summary — runs
  against a live working tree, so both of its snapshots landed on the far side of the
  move and saw one consistent tree that had "never budged". The transports now capture
  the tree at their settle boundary and hand it to the publisher, which is the only
  place the comparison has a left-hand side. A run that edits a tracked file while a
  check is settling can therefore go from green to `incomplete`/2 — which is the point:
  the verdict is a claim about a particular tree, and that tree is no longer the one
  that was verified.


- **BREAKING** — `CheckVerdict.CheckOutcome` gains `RunnerAborted of aborts: string list`
  and `CheckVerdict.CheckInputs` gains a required `RunnerAborted: RunnerAbort` term
  (AUTOMATION-294). Both transports supply it; a transport that forgets fails to compile
  rather than quietly reporting the old exit 1.
- **`fshw check` / `confirm` now exit `2` where they previously exited `1`** when a test
  HOST DIED mid-run — killed by a signal under load, or gone before it could write a
  report. Nothing failed there and nothing passed, so it is the same class of answer as
  `WaitingOnBuild`: "could not complete", not "failures found". `.fshw/verdict.json`
  records `outcome: "incomplete"` with a reason naming the signal and every affected
  project, where it used to record `red`. **If you branch on `red` to mean "a test
  broke", a killed host stops matching — which is the fix.** A genuine failure beside an
  abort still short-circuits to `FailuresFound`/exit 1, in both modes.
- `converge` treats `RunnerAborted` as TERMINAL — no automatic retry. A re-scan cannot
  un-kill a host, and a retry loop cannot tell a host killed by a busy box from one that
  aborts every time because something is genuinely broken, so retrying until a verdict
  appeared would convert a real crash into a slow green.

## 0.14.0-alpha.23 - 2026-08-21

- chore: rebuild to bundle updated dependencies


## 0.14.0-alpha.22 - 2026-08-21

- fix(ci): format AUTOMATION-163 changes with pinned Fantomas
- AUTOMATION-163: preserve BootScan debt across full-run completion


## 0.14.0-alpha.21 - 2026-08-19

- Verdicts now carry `treeHashAlgorithm: "fshw-tree-sha256-v2"` (AUTOMATION-164): the
  tree hash includes one entry per directory the walk could not see, so a verdict earned
  over a tree with a permission hole in it no longer applies to that tree readable — the
  two used to hash identically. A tree with no holes hashes exactly as it did under v1;
  the version bump is there because the INPUT SET changed, and a consumer reimplementing
  the documented recipe must know which one it is reimplementing.

## 0.14.0-alpha.20 - 2026-08-19

- fix: **`beforeRun failed:` now says which step failed, with its exit code and its output.**
  It used to render as exactly that — colon, then nothing. The message interpolated the
  process output, so a step that wrote to neither stream produced no reason at all: no step
  name, no exit code, no output. Observed on a healthy box across four consecutive `confirm`
  runs, each exiting 2 with no verdict; since `confirm` is the merge verb, the work could not
  be landed, and the only way to find the culprit was to run all nine chained commands by
  hand. It also nearly misattributed the blame to the change under test, which happened to
  touch a file the chain mentions (AUTOMATION-320).
  - The reason is now a **record** (`HookFailure`), not an interpolated string: the label, the
    step's 1-based position, the exact command and the `ProcessOutcome` are all required to
    construct one. The empty message is therefore unconstructible rather than merely
    discouraged, and an absent output prints `(no output on stdout or stderr)` instead of
    trailing off.
  - **`tests.beforeRun` accepts an ARRAY of steps** as well as a string. This is what makes
    per-step attribution possible at all: a single `a && b && c` string is one opaque process
    to the runner, so when it dies there is nothing to name. The chain stops at the first
    failure rather than running on.
  - The **run-level** `beforeRun`/`afterRun` hook had the identical bug and gets the identical
    renderer.
  - **Not breaking.** A string still parses — as a one-step chain — so existing `.fshw.json`
    files are unaffected. Adopting the array form is what buys the per-step message.

## 0.14.0-alpha.19 - 2026-08-18

- refactor: **the two transports now select the check's explanation with one function
  instead of two copies of the same six-arm match.** `Verdict.CheckProse.explainOutcome`
  is the dispatch; `IpcOutput` (daemon) and `RunOnceCheck` (`--run-once`) each call it.
  The *words* were already shared — `CheckProse` exists for that — but the arm-by-arm
  selection between them was not, so AUTOMATION-201 and AUTOMATION-303 each had to add
  their arms twice, and each copy carried a comment promising the other that they said
  the same thing. Whether a daemon served the check may not change what the answer
  means, and now it cannot: the only difference the function admits is the re-scan count,
  which the converging daemon path has and `--run-once` does not. No message text
  changed.
- refactor: `Verdict.RedCause.unattributable` — ONE definition of "the causes that are
  not claims about the tree on disk", replacing three hand-written copies of the same
  filter (`IpcOutput`, `RunOnceCheck`, `ProgressRenderer`). Three copies is three chances
  for the count that decides between exit 1 and exit 3 to disagree with the lines printed
  beside it.
- refactor: the per-project test lines are built with `Seq.zip` rather than `Seq.mapi`
  plus list indexing — same output, without the quadratic index walk.

## 0.14.0-alpha.18 - 2026-08-18

- fix!: **a stale build output no longer borrows the build-ordering defer's words — or
  its remedy** (AUTOMATION-201, QA rework). Both landed on one `waiting on build`
  message, and for a stale output every clause of it was wrong: the artifact WAS
  produced, the build already ran (the field report has `✓ build` in the same run as the
  refusal), and the two escapes it named — "re-run once the build settles" and `fshw
  confirm` — each spend a full gate cycle to arrive back at the identical refusal.
  `CheckOutcome.WaitingOnBuild` now carries the stale-output deferrals, so the terminal
  AND `.fshw/verdict.json`'s `reason` name every affected project, quote the file that
  is stale, and say `dotnet build` — plus `--no-incremental` for a timestamp-inverted
  copy — while ruling out the three remedies that cannot clear it. Same shape as
  AUTOMATION-303's `fshw stop` message, for the same reason. Exit code is unchanged
  (still 2); **the `reason` string in the verdict file changes shape for this cause**,
  so a consumer matching its exact prose stops matching.
- fix: **agent mode no longer truncates a plugin summary at 80 characters**
  (AUTOMATION-201, QA rework). The reported symptom was a status line reading `4 waiting
  on build (tests did not run): Intelligence.Build.Dev.Tests, Intelli…` — a list of
  affected projects severed mid-name. The budget belongs to the caller that REDRAWS:
  compact/verbose are erased by counting the lines printed, so a wrapping line smears
  the block, but that redraw is guarded by `UI.isInteractive` and agent mode is what a
  non-interactive caller gets. Newlines still collapse, so the line-per-plugin contract
  holds. Compact and verbose keep the 80-character budget, marker and all.

- **BREAKING (verdict schema): `reddenedBy[]` entries carry a `kind`, and a red the tool
  cannot attribute to your tree is now exit 3, not exit 1** (AUTOMATION-303). A red is a
  claim — "something in THIS tree is wrong" — and two kinds of failing diagnostic cannot
  support one: an FCS `internal error:` (the checker crashed, so it found nothing) and a
  diagnostic against an absolute path that is no longer on disk (the ledger is describing
  a tree you have already changed). Each cause is now classified `about-this-tree`,
  `checker-fault` or `vanished-file`, and when **every** failing diagnostic is one of the
  latter two and no plugin failed, the outcome is `incomplete` with exit **3** — NO
  VERDICT — instead of a red. The gate still refuses; it stops telling you your code is
  broken when it has no evidence that it is. One cause that IS about the tree keeps the
  whole run a red, so a real compile error arriving beside stale noise is never demoted.
  **If you branch on `exitCode == 1` to mean "failures", that state stops matching** —
  it was never a failure, it was the daemon describing a tree that no longer existed.
- fix: **the gate names `fshw stop` when `fshw stop` is the answer** (AUTOMATION-303).
  For the class above, `fshw scan` was the documented remedy and has never cleared it
  once; only a daemon restart does. That was folklore, and unwritable folklore at that,
  because the opposite incident (a cached build hiding a REAL compile error) produced
  output no human could tell apart — one lesson ships a non-compiling tree, the other
  chases phantoms for an hour. The `REDDENED` lines now mark each unattributable cause
  `[NOT-THIS-TREE: …]` and print the remedy, naming the one that does NOT work.
- fix: **a compile item added through `Directory.Build.props`/`.targets` or
  `Directory.Packages.props` no longer replays a cached build or a cached test result**
  (AUTOMATION-303 case 2). MSBuild's implicit imports can add a `<Compile Include=…>` to
  every project in a repo, and they were in neither list the build merkle hashed — not
  compile items, not projects — so the key stayed byte-identical while the tree gained a
  file. The build replayed `built N projects (cached)`, nothing compiled it, and the FCS
  error beside it was real. The build merkle now folds in each project's nearest-ancestor
  implicit imports (MSBuild's own `GetPathOfFileAbove` rule), and test-prune's structure
  hash reads the same shared list rather than a second copy that knew about one of the
  three. Existing build and test-prune cache entries orphan on upgrade, which is correct:
  they asserted a verdict over inputs they never hashed.
- fix: **a `test-rerun` refusal now NAMES the filter and every project it searched**
  (AUTOMATION-227/272). It named a project count and pointed at `fshw status
  test-prune`. The three causes of a zero match — a typo, a renamed class, and a filter
  fanned out across projects that do not contain it — are indistinguishable without
  those two facts, and the ambiguity has already sent one investigation after a class
  that was never misspelled. The daemon now puts the active filter on the wire
  (`filter`, `null` when there was none); a daemon too old to send it makes the CLI say
  so rather than print "(none)", which would be a claim about the run rather than about
  the daemon. The project list is capped at ten and states how many it left out.
- feat: **`test-rerun` now prints total / succeeded / failed for EVERY project it ran**,
  on every outcome including the green (AUTOMATION-272). Those counts existed only in
  `daemon.log`, so the one line that separates a real pass from a vacuous one required
  going to look. A project that produced no readable report prints "no test report —
  counts unknown (not zero)" rather than zeros: `total: 0, failed: 0` reads as a suite
  that ran cleanly, which is the same vacuous green one level down.
- fix: **a test project KILLED at its timeout is no longer a pass.** The failure check
  matched only the `failed` per-project status, so a `timed-out` project printed
  `✓ Tests passed` and exited `0` — while the daemon's own terminal status for the same
  run was a failure plus a timeout. Now exit `1`, worded so the cause is not mistaken
  for an assertion failure.
- fix: **the older-daemon fallback no longer greens a MIXED no-op run.** With no
  `coverage` field it recognised "no project selected" and "every project matched
  nothing", and sent everything else to a pass — so a run where one project matched
  nothing and another deferred printed its own "1 matched nothing, 1 did not run"
  summary directly above `✓ Tests passed`. It now derives `nothing-executed` from the
  per-project statuses it already had in hand, and an UNRECOGNISED status is not counted
  as having executed (fail closed on a daemon newer than the CLI).

## 0.14.0-alpha.17 - 2026-08-17

- fix: **a run that tested nothing no longer renders as a `✓`** (AUTOMATION-198).
  `fshw check` on a run that selected ZERO test projects printed
  `✓ test-prune — 0 passed, 0 failed in 0 projects` and then, correctly, refused to
  certify it (exit 3 — NO VERDICT). The verdict was sound; the plugin line was not, and
  a reader scanning glyphs saw success while only the exit code disagreed. Every surface
  now refuses it a green: compact and verbose render `⚠`, agent mode tokens `warn` (so
  `next:` points at `status`, never `done`), and `plugins[]` in `.fshw/verdict.json`
  records `warn` instead of `ok`. **If you branch on `plugins[].state == "ok"`, that run
  stops matching** — the exit code was already `3`; only the per-plugin state was lying.

- fix: **`waiting on build` names an escape that can actually work** (AUTOMATION-245).
  The message said only "re-run once the build settles", which is the one instruction
  that cannot clear it — the build was replaying a cached result its outputs no longer
  supported, so re-running reproduced it verbatim. It now names `fshw confirm`, which
  forces a real build, and states that **restarting the daemon is NOT an escape**: the
  task cache is `FileTaskCache` on disk under `.fshw/` and survives `fshw stop` and a
  reboot. That folk remedy has been advised for months and never cleared anything by
  itself.

- fix: **a shortened agent summary says what it dropped** (AUTOMATION-201). The agent
  status line truncates to 80 characters, and a bare `…` is indistinguishable from
  prose — a reader seeing `4 waiting on build (tests did not run): Intelligence.Build…`
  had no way to tell a shortened list from a complete one. Truncation now states the
  omitted count; the untruncated detail is in the ledger entry and log, where the remedy
  also lives.

- refactor: the `CheckOutcome` explanations live once, in `Verdict.CheckProse`, rather
  than as hand-synced copies across the daemon path, the `--run-once` path and the
  verdict file. Whether a daemon served the check is not something the explanation may
  vary on; the `waiting on build` text in particular is what a WEDGED caller reads, so
  it is the one message that must never be the stale copy. No output changed.

- refactor: one decider for a terminal status glyph (`ProgressRenderer.glyphForParsed`),
  called by compact and verbose alike. Every fail-closed rule now holds on both surfaces
  or on neither, instead of being written twice and agreeing by hand.

## 0.14.0-alpha.16 - 2026-08-13

- merge: the no-tests-ran report names the prior verdict and the change that triggered it, into main
- test-prune report: name the CHANGE that triggered the prior run
- test-prune report: name the prior verdict when a run selects no tests
- AUTOMATION-259: confirm records the check-scoped run it already computed


## 0.14.0-alpha.15 - 2026-08-13

- AUTOMATION-258: a confirm verdict cannot record a filtered scope

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- chore: log which process claims the daemon singleton, and who is refused
- Comment audit: cut AI thinking-out-loud from comments

- chore: **two temporary log lines so a duplicate-daemon report can be diagnosed
  from `daemon.log` rather than from `ps` archaeology** (AUTOMATION-289; the
  source carries the condition for removing them).

  Duplicate `FsHotWatch.Cli` processes were seen alive for one repo root, and the
  investigation stalled on a question nothing could answer after the fact: were
  they daemons, or one daemon plus a long-running client? `fshw confirm` is the
  same binary in the same working directory and can run 20+ minutes, and the
  processes were gone before their argv was captured. (A live `confirm` has since
  been observed fitting exactly that shape, so the duplicate-daemon premise is
  itself unconfirmed.)

  The daemon that wins the singleton lock now records its pid and full argv, and
  the one that is REFUSED records the same into `daemon.log`. That refusal
  previously went only to the losing process's stderr — the caller's console — so
  `daemon.log` held 46 "Starting daemon" lines and zero refusals, which reads as
  "the singleton never fires" when it may simply never have been recorded.

  Verified with two real processes rather than assumed: a second `start` is
  refused, and both lines land.

- test: widen the wedge drain test's margins (100 x 20ms against a 500ms stall
  threshold, was 25 x 40ms against 200ms). Two ratios have to hold at once: the gap
  BETWEEN completions must stay well under the threshold, or a healthy drain trips
  the detector; and the whole drain must run well over it, or the detector could
  not have fired even if broken. The old shape left only 5 finished events per
  threshold window; it now leaves 25.

  Deliberately NOT raising the file-watcher budgets. That was tried and reverted:
  a watcher test raised to 45s then failed at 45.6s, having taken ~22 probe writes
  across those seconds without receiving a single event. A watcher that delivers
  nothing over 45s is not slow, it is not working — so a larger budget buys a
  slower red and hides the breakage. Tracked in AUTOMATION-294.


## 0.14.0-alpha.13 - 2026-08-12

- chore(deps): **the `SQLitePCLRaw.lib.e_sqlite3` pin is removed — `TestPrune.Core`
  carries that floor itself now.** The pin existed because `Microsoft.Data.Sqlite` and
  `TestPrune.Core`'s `SqliteSymbolStore` pulled `lib.e_sqlite3` 2.1.11 transitively,
  which carries GHSA-2m69-gcr7-jv3q (High, `NU1903`). `TestPrune.Core` 6.1.2 declares
  `SQLitePCLRaw.lib.e_sqlite3 3.50.3` as its own dependency, so a forced no-cache
  restore resolves 3.50.3 with the pin gone. Removing it also stops this package
  advertising a native SQLite build it never calls directly. Restore-time `NU1903`
  stays the actual guard if a vulnerable version ever resolves again — that check is
  automatic, where the pin was a hand-copy of a constraint whose owner had moved on.

## 0.14.0-alpha.12 - 2026-08-11

- feat: **`test-rerun --project <name>` aims the rerun at specific test projects
  (AUTOMATION-272).** Repeatable; omitting it keeps the existing behaviour of running
  every configured project. Without it a `--filter-class` is fanned out across ALL of
  them, so a class that really exists still reports "matched nothing" in every project
  that does not contain it — and if the project that does contain it was not among those
  invoked, the run is indistinguishable from a typo. That is how an investigation into a
  genuinely failing `JudgeIntegrationTests` case was told the tests passed: the filter
  ran against a project the class does not live in.
  The daemon has read a `projects` array on `run-tests` all along and filtered its
  configs by it — a comment in the plugin even refers to `--projects` as though the flag
  existed. Only the CLI flag and the payload field were missing; this connects a wire
  that was already built from both ends.
- feat: **`test-rerun` prints what the run actually did**, on every outcome including the
  green: `N project(s): X passed, Y failed, Z matched nothing`. These counts previously
  existed only in `daemon.log`, so telling a real pass from a vacuous one meant going and
  reading a log — and the missing line IS the tell, since "1 project: 1 matched nothing"
  and "1 project: 12 passed" are the same `✓` without it. Projects that were deferred,
  timed out or errored are counted as "did not run" so the parts always sum to the total.
- feat: **the zero-match diagnostic offers the right cause first.** It previously said a
  filter matching nothing "is almost always a typo or a renamed class", which sent the
  investigation above after a class that was never misspelled. When exactly one project
  ran it now leads with the possibility that the class lives in a different project, and
  in both cases it shows the `--project` invocation that aims the rerun.

- fix!: **`check` no longer exits `0` when it could not read what the tests covered.**
  `TestScope.NoTestsRun` — "the daemon holds no test evidence at all" — is refused in
  both `check` and `confirm`, and has been since AUTOMATION-129. But that refusal was
  only ever *reached* when the scope read succeeded. Every way of failing to read it (the
  `test-scope` command threw, the IPC call faulted, the reply was not JSON, the daemon
  contradicted its own project counts) produced the same `ScopeUnknown` as "this repo has
  no test projects configured" — which the inner loop deliberately tolerates. So a fault
  on the read path silently converted an exit `3` into an exit `0` on an *unchanged*
  daemon state: nothing verified, everything reported fine.
  `TestScope` now separates the two facts. `ScopeUnknown` means only what it can prove —
  no `test-scope` command exists (no test projects configured), or a run is still in
  flight — and `check` keeps tolerating it. The new `ScopeUnreadable of reason` means "I
  asked and could not find out", and is refused in **both** modes (exit `3`), because a
  read that faulted cannot rule out the `NoTestsRun` it may be hiding. Same principle as
  `PendingVerification.LoadedQueue` (AUTOMATION-150): a ledger you could not read is not
  an empty ledger, and a missing reading may not be treated as a good one.
  **BREAKING (F# API):** `FsHotWatch.Cli.IpcParsing.TestScope` gains a case, so matches
  over it must handle `ScopeUnreadable`. **Behaviour:** a `check` whose scope read faults
  now exits `3` where it exited `0`. A repo with no test projects is unaffected — it
  still exits `0`. `.fshw/verdict.json` gains `scope.kind: "unreadable"` carrying a
  `reason`.

- fix!: **`test-rerun` exits `3` instead of `0` when a run executed nothing.** It printed
  `✓ Tests passed` and exited `0` both when the filter matched no test and when no
  project was selected at all — so a run that verified nothing was indistinguishable
  from one that verified everything, and sailed through any `&&` chain and any CI gate.
  Observed live: a filtered re-run reported a pass having run no tests, and was very
  nearly recorded as verification of two real defects. Both no-op outcomes now exit `3`,
  matching `confirm`'s existing contract — refuse to green without evidence. **A CI job
  that treats non-zero as failure will start seeing red on runs it used to pass; that is
  the point, those runs proved nothing.**

- feat: **a no-op run says why it verified nothing, and what to do about it.** The two
  causes now get different remediation instead of the same generic advice: a run that
  selected no project says so and does not offer test-name suggestions (nothing ran to
  discover any, so a suggestion would be a guess), while a filter that matched nothing
  reports how many projects discovered their tests and points at
  `fshw status test-prune` for the real names. Driven by the new `coverage` token from
  FsHotWatch.TestPrune, with a fallback to the old counts when talking to an older
  daemon — an absent field is never read as "ran".

- fix: **a red verdict names the failing plugin.** The agent hint block printed the
  verdict path, the suite CTRF files and scope advice, and never mentioned plugins — so
  a run that was red because of `analyzers` or `format` showed a wall of passing test
  counts and nothing else, which reads as "the red is not mine". Seen twice in one day
  on the same tree: a `confirm` returned exit 1 with six test projects at `failed=0` and
  three analyzer findings, and was misattributed to an unrelated known test failure
  before anyone opened `plugins[]`. Now emits a `FAILING` line per failing plugin
  carrying its summary, plus an `UNEXPLAINED` line for a non-zero exit with **no**
  failing plugin and no failing suite — which previously rendered as a tidy block that
  looked like a pass.

- feat: **`runHookCommands`** — a top-level `.fshw.json` key selecting WHICH verbs the
  run-level `beforeRun`/`afterRun` hooks bracket. An array of `"check"` / `"confirm"`;
  absent means **both**, exactly the previous behaviour, so the key is a pure addition
  and no existing config changes meaning on upgrade. `["confirm"]` leaves `check`
  completely unwrapped — no latch, no signal handlers, no shell-out, the same straight
  `action ()` taken when no hook is configured — so a box-wide gate can guard the merge
  verdict without taxing the inner loop. Applies identically on the daemon path and
  `--run-once`, because the decision wraps the ACTION and is purely "which verb was
  invoked"; there is no cheapness heuristic through which CI could silently lose the
  gate. `confirm`'s `StillApplies` fast path stays unwrapped regardless. Failure modes
  lean safe: an unrecognised or wrongly-typed value falls back to bracketing BOTH
  rather than silently un-gating; only an explicitly empty array disables bracketing,
  and that is warned about at load.

## 0.14.0-alpha.11 - 2026-08-06

- confirm: force a REAL build — a cache hit must not assert freshness it never verified (AUTOMATION-224)


## 0.14.0-alpha.10 - 2026-08-05

- fix!: **dead config now FAILS the load instead of warning.** `"cache": "file"` / `"jj"`
  in `.fshw.json` selected an on-disk FCS check cache that could never produce a hit. It was
  already rejected, but only with a `Logging.warn` that mapped the value to `NoCache` and
  carried on. That was not enough — a warning scrolls past inside a 10-minute gate, so the
  dead key survived in a real consumer's `.fshw.json` for weeks with every run dutifully
  announcing it and nobody acting. It is now a hard `ConfigError` naming the offending value
  and both fixes (delete the key, or `"cache": "memory"` for a real in-process cache).
  - **Breaking:** a repo still carrying `"cache": "file"` or `"cache": "jj"` fails to start
    until the key is removed or changed. The fix is one line and the error states it.
  - Scope is deliberately narrow: only *known-dead* settings are fatal. Unrecognised values
    (`"cache": "redis"`) still warn and fall back, and unknown top-level keys are still
    ignored — real `.fshw.json` files use `_comment_*` keys as inline documentation, so
    blanket unknown-key strictness would reject working configs.

## 0.14.0-alpha.9 - 2026-08-03

- chore(deps): bump bundled TestPrune.Falco 3.0.1 -> 3.0.2 (AUTOMATION-86)
- chore(deps): bump ecosystem tools to latest (fssemantictagger 0.13.0-alpha.20 incl. isCommitPushed fix, coverageratchet 0.15.0-alpha.11, syncdocs 0.13.0-alpha.4, fsprojlint 0.10.0-alpha.14, RefStamp 0.1.0-alpha.2)
- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context
- deps: bump CoverageRatchet.Core 0.1.0-alpha.3 -> 0.1.0-alpha.4
- deps: bump CommandTree 0.7.0 -> 0.8.0


## 0.14.0-alpha.8 - 2026-07-22

- feat: **generic run-level `beforeRun` / `afterRun` hook pair** (AUTOMATION-188). Two new
  optional top-level `.fshw.json` keys let a consumer bracket a whole `fshw check`/`confirm`
  run with external shell-outs — `beforeRun` before any plugin work, `afterRun` as a true
  `finally` that fires on success, failure, AND abort (build failure, watchdog kill,
  cancellation). fshw stays agnostic about what they do; the first consumer is a box-wide
  gate-lock that serializes concurrent checks with zero manual commands.
  - The bracket is CLI-side (one `check`/`confirm` process = one run), so every daemon-side
    abort — which surfaces to the CLI as an exit code — is caught by the finally; SIGINT/SIGTERM
    get an explicit handler (a plain finally won't run on signal) sharing a fire-once latch with
    the finally. `beforeRun` is fail-closed (non-zero ⇒ exit 2, no plugin work); `afterRun` is
    best-effort and never alters the run's verdict (a lock-release hiccup can't flip green↔red);
    both are timeout-bounded. `confirm`'s prior-confirmation fast-path (no heavy work) is left
    unbracketed. The existing per-test-run `tests.beforeRun` is unchanged and independent.

## 0.14.0-alpha.7 - 2026-07-22

- feat: `fshw --version` prints a second line naming the source ref the binary
  was built from (AUTOMATION-123) — a RefStamp `-ref.<change-id>.g<commit-id>[.dirty]`
  pack stamp, the `+<sha>` commit metadata of a release/CI build, or an honest
  `unknown`. New pure module `FsHotWatch.Cli.SourceRef` (`parse`/`describe`/`line`).

## 0.14.0-alpha.6 - 2026-07-20

- chore(deps): bundle TestPrune.Core 6.1.1 — `runProcessWith` bounds its post-exit
  stdout/stderr drain (30s wedge-detector), so a grandchild that inherited the pipe
  and outlives the direct child can no longer wedge the daemon's test-run drain
  silently. Exit-code verdict unchanged (AUTOMATION-98).

## 0.14.0-alpha.5 - 2026-07-18

- chore(deps): bundle TestPrune.Falco 3.0.1 — route→test selection is now
  per-declaration, not per-file: only test classes/test-bearing modules whose own
  span matches the route are selected (conservative fallback for out-of-span
  matches; non-test helper modules never returned; attributes recognized only
  inside `[<...>]` blocks). Fixes the over-selection where one changed handler
  pulled every class and module of every matching test file (AUTOMATION-86).
- chore(deps): bundle TestPrune.Core 6.1.0 — `runProcessWith` bounds the test-run
  wait (default 30 min, `TESTPRUNE_TEST_RUN_TIMEOUT_MS` override); a wedged runner
  is killed (entire tree) with exit 124 instead of hanging forever (AUTOMATION-98).

## 0.14.0-alpha.4 - 2026-07-15

- feat: **`confirm` HONOURS a verdict it has already earned.** (AUTOMATION-161) `confirm`
  is the pre-merge verb, so it gets run more than once — and on a tree that has not moved,
  the honest answer to *"is the suite green?"* was settled the first time. Asking again is
  not a fresh question; it is the same question about the same bytes. Before starting a
  daemon, setting a scope or running a test, `confirm` now reads `.fshw/verdict.json` and,
  if the recorded verdict is a **full-suite green** whose **`treeHash` and `producer` both
  match**, reports it and exits 0 — naming when it was earned:

  ```
  ✓ confirm — the verdict from 21:25 still applies
              (treeHash + producer match; full suite, 1 project, 1975 passed)
  ```

  Anything else — a moved tree, a *different fshw binary*, an impact-filtered green, a red,
  an unreadable file, no file — is not an answer, and `confirm` goes and earns one. This is
  the **only** thing in fshw allowed to carry a green across a process boundary, and the
  asymmetry is the point: a cached *plugin result* explicitly may not (its key does not pin
  the tree), so the fast path runs through the artifact **built to be trusted** rather than
  one that happens to be lying around. It is what content-addressing is *for*. On this repo:
  **1m 45s → 1.4s.**

- fix!: **`confirm` no longer refuses on evidence it has just produced.** (AUTOMATION-161)
  On a warm cache, running `confirm` twice on a byte-identical tree exited **3 — "NO TESTS
  RAN — nothing was verified"** — while the plugin's own status line said *"1 passed, 0
  failed, full suite (cached)"*. Both described the same run. So did `check`.

  Not a green without evidence, but the inverse: **a refusal despite evidence**. The second
  `confirm` *did* force the full suite and *did* run it — 102 seconds, 1965 passed, a
  complete CTRF report written to disk — and then the task cache replayed a cached terminal
  over the `TestsFinished` carrying the result, so the plugin never learned the run had
  happened. Fixed in core (a `Custom` message is a cache writer, never a reader) and in
  TestPrune (a process may not assert a test result it has no record of running).

  This is also what makes the AUTOMATION-117 claim below — *"`confirm` RUNS the suite it
  demands"* — **true**. It was false on a warm cache: `confirm` ran the suite it demanded,
  and the cache threw the receipt away.

- fix!: **a CRASHED PLUGIN NO LONGER GREENS CI.** `--run-once` — which is what CI runs —
  computed its `hasFailures` as the failing-diagnostic count and nothing else, while the
  daemon path computed `anyPluginFailed || failingDiagnostics`. A plugin can reach
  `Failed` **without writing a single diagnostic**: PluginFramework's two crash-nets force
  exactly that when a work async or an event handler throws (they cannot invent a file and
  line for someone else's stack trace). So `fshw check` exited 1 on a crashed plugin and
  `fshw confirm --run-once` exited **0, `outcome: green`**, with `plugins:
  [{"outcome":"fail"}]` sitting in the same verdict file.
  - Fixed **structurally**, not by copying the missing term across. `CheckVerdict.verdict`
    now consumes a `CheckInputs` record — plugin statuses, failing-diagnostic count,
    coverage, scope — and computes the disjunction ONCE. Both transports hand over the
    same record; neither decides anything. A transport that forgets a term no longer
    produces a green, it fails to compile.
  - **Breaking (API):** `CheckVerdict.verdict` and `CheckVerdict.converge` take
    `CheckInputs` instead of positional `hasFailures`/`coverage`/`testScope`.

- fix!: **`Verdict` is now a private record with a smart constructor,
  `Verdict.create`, that REJECTS a `Green` carrying a failing plugin.** `outcome` and
  `plugins` were assembled side by side from independent sources, so nothing forbade
  `{"outcome":"green","plugins":[{"outcome":"fail"}]}` — and the bug above is exactly how
  you produced one. The same move AUTOMATION-99 made for `RunVerdict`: if a state is a
  lie, do not document that it must not be constructed — make it unconstructible.
  `Verdict.read` enforces the same invariant on the way IN, so a hand-edited or
  future-schema file cannot have a green lifted out of it either (it reads `Unreadable`).
  - **Breaking (API):** the `Verdict` record can no longer be built or copy-updated by
    hand; use `Verdict.create`. Fields are exposed as members and read exactly as before.

- fix!: **the IPC status parser fails CLOSED.** It rounded unknowns DOWN to "fine" in four
  places, while `Verdict.read` one file away stated the opposite policy and enforced it:
  *"an unknown state is not a passing state"*. This is a live cross-version hazard —
  `PluginOutcome` gained `Wedged` in this same batch, so "old CLI, new daemon" is a shape
  that exists.
  - an unparseable `status` object → `StatusView.Idle` → quiescent → the plugin was
    **omitted from the verdict entirely**. Now `StatusView.Unreadable`, which is a
    `PluginOutcome.Fail`, and the plugin stays in `plugins[]`.
  - a `"running"` status whose `since` would not parse → `Idle`. This **silently defeated
    all of AUTOMATION-147's wedge detection**, which fires only on `StatusView.Running
    since`. Now `Unreadable`.
  - an unrecognized `lastRun.outcome` tag → `CompletedRun`, i.e. **an unknown run outcome
    defaulted to a PASS**. Now `FailedRun`.
  - `parsePluginStatuses` returned `Map.empty` on a `JsonException` → every plugin
    vanished and a clean ledger still went green. It now returns
    `Result<_, string>`; an unreadable map is not an empty one.
  - **Breaking (API):** `StatusView` has a new `Unreadable of reason` case;
    `parseTaggedStatus` returns `StatusView` (not `StatusView option`);
    `parsePluginStatuses` returns `Result<Map<_,_>, string>`.

- feat!: **`fshw gate` is now `fshw confirm`.** (AUTOMATION-160)

  **Migration: `fshw gate` → `fshw confirm`.** The old verb is **removed**, not aliased.
  (`gate` was added in AUTOMATION-112 and never appeared in a *published* package, so this
  only affects anyone tracking `main` or a local pack. The entries below describe the verb
  by the name it actually ships under.)

  `gate` named what the verb *blocks*. So it got built as a bouncer — pass/fail — and the
  most valuable thing it produces was thrown away as a side-effect. The verb's real job is
  to **run the full suite and confirm that `check` told the truth**.

  That reframing matters because running an unfiltered suite next to an impact-filtered
  `check` is a **comparison**, and every disagreement between the two is a **bug in one of
  them**:

  - *failed under `confirm`, never selected by `check`* → the selector **MISSED** a test.
    An impact-analysis bug, not a test bug.
  - *passed under `confirm`, but `check` says red* → a stale red, a flake, or a
    **test-isolation defect**: a test that only passes *with company*, because another
    test sets up the state it depends on. There, `check` is the honest one and the full
    suite is the liar.

  Nobody built that comparison because the name did not suggest there was one to make.
  Reporting it is the next change; this one makes it obvious it is owed.
  - **BREAKING:** `Command.Gate` → `Command.Confirm`; `CheckVerdict.CheckMode.MergeGate` →
    `Confirmation`; `CheckVerdict.gateNeedsFullRun` → `confirmNeedsFullRun`;
    `Verdict.Command.Gate` → `Verdict.Confirm`.
  - **BREAKING (wire):** `.fshw/verdict.json`'s `command` field now reads `"confirm"`
    where it read `"gate"`. A verdict written by an older fshw parses as `check` — but a
    verdict from an older binary is already refused by the `producer.hash` rule, so no
    consumer can act on the downgrade.

- feat!: **`fshw confirm --run-once` — the merge verdict, without a daemon.** (AUTOMATION-117)
  The verb existed only on the daemon IPC path, and `--run-once` bypasses the daemon
  entirely — which is what CI uses. So **CI could not invoke the very check it is supposed
  to be judged by**, and ran `check --run-once` instead. That looked fine only by accident: a
  CI checkout starts with a COLD impact DB, and a cold DB selects everything. Cache the
  `.fshw` state between runs, restore the DB, or optimise CI at all, and the same green
  would silently start coming from a **subset**, with nothing in the output to say so.
  `confirm --run-once` makes the full-suite scope a checked precondition of the exit code
  instead of a lucky side effect. This repo's own CI (`lint-cmd`) and `mise run ci` now
  run it.
  - **BREAKING:** `Confirm` carries `RunFlag list` (`Confirm of RunFlag list`), so it can
    take `--run-once` like `check` and `format`.

- feat!: **`confirm` RUNS the suite it demands.** (AUTOMATION-117) `set-scope full` makes
  the next test run unfiltered; it does not make a run **happen**. A verb asked "may I
  merge this?" on a tree whose suite has not run — a fresh CI checkout, or a warm daemon
  whose impact DB says nothing changed — refused for want of evidence while offering no
  way to produce any. That refusal was *correct* and *useless*: **a demand nobody can
  satisfy is one people route around with a shell script**, which is exactly how a
  40-line unverified bash harness ended up making merge decisions on this repo. `confirm`
  now forces a full run (`run-tests`, unfiltered) when the settled scope is not already
  full-suite, then re-reads what that run actually covered and judges *that*. It is a
  **backstop, not the mechanism** — a cold daemon's scan already provokes the unfiltered
  run, so the common case still pays for exactly one suite (`CheckVerdict.confirmNeedsFullRun`).
  - **BREAKING:** `IpcOutput.pollAndRender` takes a new `forceFullRun: unit -> unit` seam
    before `triggerScan`.
  - **KNOWN LIMITATION — the forced run is defeated by a warm task cache.** The force
    makes a run *happen*; it does not make that run *execute tests*. On an unchanged
    tree whose previous result is still in `.fshw/cache/`, `test-prune` REPLAYS the
    cached result (`… (selected: no) (cached)`) instead of running. A replay writes no
    CTRF reports for the new `runId`, so the verdict's scope reads `NoTestsRun` and
    `confirm` exits **3**. It fails in the SAFE direction — it refuses rather than
    inventing a green — but the practical consequence is that a second `confirm` on an
    unchanged tree cannot go green until the cache is cleared (`mise run cache-clear`,
    i.e. `rm -rf .fshw/cache`). Reproduced deterministically: cold cache → exit 0
    (tests ran, full suite); immediately re-run on the byte-identical tree → exit 3,
    every plugin `(cached)`. **CI does not hit this**, because a CI checkout starts
    cold — which is precisely the accident this entry warns about elsewhere, in the
    other direction. So `confirm` does not yet run the suite it demands in every case,
    and this entry's headline is true only of a cold cache.

- fix: **`--run-once` now publishes `.fshw/verdict.json`.** (AUTOMATION-117) It never did
  — so `fshw verdict` after a CI run reported "no verdict on disk": the machine-readable
  answer was missing from the one place a machine was reading. The run-once path also
  never computed a `CheckOutcome` at all; it counted failing diagnostics and returned
  0/1, silently skipping the completeness check (exit 2) and the scope check (exit 3)
  that the daemon path had enforced since AUTOMATION-112. It now shares the daemon path's
  verdict, convergence, verdict file, and exit codes — `RunOnceCheck` differs from the
  daemon only in its transport (`PluginHost.RunCommand` in-process instead of a socket).
  **`check --run-once` can therefore now exit 2** where it previously exited 0, if the
  scan left files unchecked.

- fix: **`fshw verdict` no longer claims "a DIFFERENT tree" when the tree is identical.**
  `Report.Stale` carries a *reason* — it says which provenance link broke, a different tree
  **or** a different fshw binary — but the renderer printed that whole sentence under a
  `current tree` label (a paragraph where a hash belongs) and asserted a tree mismatch
  regardless. A stale *binary* over an unchanged tree now says so.

- **Note on "full suite":** `confirm` asserts that every test project **`.fshw.json` knows
  about** ran unfiltered — today, `FsHotWatch.Tests` alone. `FsHotWatch.IntegrationTests`
  is in the solution but not in `.fshw.json`, so `confirm` does not run it (AUTOMATION-158).
  `confirm` does **not** claim to run every test in the solution.

- feat!: **fshw self-heals a stale or wedged daemon instead of handing you a ritual.**
  (AUTOMATION-147) Before running work, the CLI compares the running daemon's recorded
  binary identity against its own. A different binary — or a daemon that recorded **no**
  identity, i.e. any build predating the handshake — is stopped, replaced, and the
  command continues. It says which it found (`The running daemon was started from a
  different fshw binary (0.9.0) — restarting it with this one...`) and then just works.
  - **A HEALTHY daemon is never restarted.** Restart happens only on a genuine identity
    mismatch, a genuine wedge, or a `.fshw.json` change — the warm FCS cache is the
    entire point of the daemon.
  - **BREAKING:** `decideDaemonAction` is replaced by `decideRunningDaemonAction`, which
    takes an `IdentityVerdict` and returns a `RunningDaemonAction`. The not-running case
    is no longer representable: there is no decision to encode.
  - `computeConfigHashWith` no longer takes an exe path. It hashes `.fshw.json` and
    nothing else. It used to smuggle the binary in as `Environment.ProcessPath`'s mtime,
    which for a `dotnet`-hosted invocation tracked the **dotnet muxer**, not the fshw
    dll — and could not have caught a same-mtime repack anyway. Binary drift is the
    identity handshake's job now; the two signals are honest and separate.

- feat: **a corrupted IPC reply restarts the daemon and retries — automatically.**
  (AUTOMATION-147) The `OutOfMemoryException` whose own code comment conceded it *"is
  misleading because the machine isn't actually out of memory"* used to tell the human to
  run `fshw stop` then `fshw start`. If that is the correct recovery, the tool performs
  it. The retry happens exactly once, and only for the corrupted-pipe family (`OOM` /
  `Overflow`) — a timeout does not restart a daemon that is merely busy.

- feat: **an incomplete plugin is NEVER rendered `✓`** (AUTOMATION-147). A plugin running
  past the wedge bound renders `⚠ analyzers  WEDGED: started 11:38:39, no completion in
  12m` — in compact, verbose, and agent mode (a new `wedged` token, which steers `next:`
  to `status`). A `Completed` status carrying no run record can no longer render as a bare
  `✓` either: it warns, in words. And the `elapsed:` line is now **always** printed —
  its *absence* was the home-made wedge detector the operator had to invent, and a tool
  must never require its user to detect a fault by noticing what isn't printed.

- feat: `fshw status` **names a stale-binary daemon** rather than presenting its output as
  current, and prints what the daemon did if it restarted itself over a wedge:
  `⚠ daemon was wedged on 'analyzers' ... — restarted it`.

- fix: **a `daemon.pid` whose process is dead is cleaned up on the next command.**
  Unknowns lean ALIVE (a missing, unparseable, or unreadable pidfile is never deleted),
  so a live daemon's pidfile is never eaten out from under `fshw stop`.

- fix!: **a missing number is not zero.** (AUTOMATION-129) The verdict READER defaulted a
  missing `elapsedMs` to `0L` and every missing suite count to `0` — which is the
  AUTOMATION-99 signature (`started:` with no `elapsed:`) rebuilt inside the very file
  that exists to prevent it, and worse: `total: 0, failed: 0` conjured from a truncated
  file reads as *"this suite ran cleanly"*. A vacuous green out of thin air.
  - `PluginVerdict.ElapsedMs` is now `int64 option` — `0` is a MEASUREMENT
    ("instantaneous"); absence is the absence of one, and the two must be distinguishable.
    The WRITER had the same bug (no `LastRun` → `0L`); it now writes `null`.
  - A suite entry whose counts cannot be read makes the whole verdict **`Unreadable`**,
    exactly as a missing `treeHash` does. Fail closed, or do not bother having a verdict.
  - These cases cannot arise from our own writer — which is precisely why they needed
    pinning. They arise from the files a verdict exists to SURVIVE.

- feat!: **the verdict is content-addressed to its PRODUCER as well as its subject.**
  (AUTOMATION-129) `treeHash` says WHAT the claim is about; `producer` says WHO made it —
  a SHA-256 of the fshw binary. Without it a stale daemon writes a verdict for an
  UNCHANGED tree, the `treeHash` matches, and the verdict reads as current: the
  provenance chain had a hole in the middle. `fshw verdict` now exits 4 (STALE) for a
  verdict produced by a binary it is not running, however well the tree matches. A
  verdict that cannot say who made it has not established provenance and does not apply.
  (The daemon-handshake half of this argument is AUTOMATION-147; both should share one
  `ContentHash` policy — they now can.)

- fix!: **"no tests ran" can no longer be green — in EITHER mode.** (AUTOMATION-129)
  `NoTestsRun` does not mean "impact analysis selected nothing this time"; it means the
  daemon holds NO TEST EVIDENCE AT ALL ("0 passed, 0 failed in 0 projects"). A `check`
  that went green on that was the vacuous green in its purest form — observed in the wild
  twice on the day this was written. It is not a scope question ("did we test enough?")
  but an evidence question ("did we test AT ALL?"), so unlike `ImpactFiltered` it is now
  refused in the inner loop too. The inner loop may test LESS; it may not test NOTHING
  and call it green.

- feat!: **the verdict DECLARES which reports are its own.** (AUTOMATION-129) It carries
  the `runId`, and the suites are the files in that run's directory
  (`.fshw/test-runs/<runId>/`) — membership stated, never inferred from mtimes. Per-suite
  `total/passed/failed/skipped` are carried **INLINE**, so a number never depends on a
  second file still being readable. And the steering hint never prints a path for a file
  that was not written: when nothing ran it says so, in words.

- feat!: **the verdict is a FILE, content-addressed to the tree it verified.** (AUTOMATION-129,
  ADR-013) Every `check` and `confirm` now publishes `.fshw/verdict.json` — written
  atomically (temp + rename, so a partial read is impossible), carrying the outcome,
  the scope, per-plugin results, pointers to THIS run's CTRF reports, and a `treeHash`.
  Published on every terminal path, including a failing `confirm`, a wedged plugin and a
  daemon that dies mid-run — those are exactly the moments the human-readable output is
  least sufficient and the temptation to scrape it is highest.
  - **The consumer's rule is total:** read the file; if `treeHash` ≠ hash(current tree),
    **the verdict does not apply.** Never reuse it. A green from a different tree is
    still a green — so stale is now DETECTABLE, not merely avoidable.
  - The exit code and the file's `outcome` are two renderings of ONE `CheckOutcome`.
    One truth, two surfaces — never a human surface and an agent surface that can
    disagree. There is deliberately **no "agent mode" that changes what a check means**:
    presentation may adapt to the caller; semantics may not.
  - **BREAKING:** `IpcOutput.pollAndRender` takes `repoRoot` and the config's exclude
    patterns (it now publishes the verdict).

- feat: **`fshw verdict`** — read the last verdict and report whether it still applies to
  the tree on disk. Contacts no daemon, triggers no run: **reading cannot perturb.** (The
  `test-rerun` calls an orchestrator made *because the verdict looked untrustworthy* were
  themselves what corrupted the daemon's busy accounting — the act of measuring created
  the defect being measured, AUTOMATION-99.) stdout is a JSON envelope that always states
  `applies`, so a stale green can never be mistaken for a current one. Exits 0/1/2/3 as
  the verdict itself, plus **4** (STALE) and **5** (no usable verdict).

- feat: **the output points at the machine-readable results.** (AUTOMATION-129) In non-TTY
  output — that is when a machine is reading — `check`, `confirm` and `status` print the
  verdict path and the ACTUAL CTRF paths for THIS run, and an impact-scoped `check` says
  so and names `fshw confirm`. Real paths, not a generic pointer: a hint that makes you go
  and find the file is a hint you will ignore. The CTRF reports already existed; nothing
  ever told the reader where to look, and an orchestrator spent two days grepping
  `total:` and `elapsed:` out of a progress display built for a human.

- fix!: **`fshw confirm` could never go green.** (AUTOMATION-129) `readTestScope` and
  `requestFullSuiteScope` called `RunCommand` with the PLUGIN name (`test-prune`) in the
  command slot and the real command name stuffed into the args. The host looked up a
  command called `test-prune`, found none, and returned the unknown-command sentinel —
  which `parseTestScope` correctly, and silently, read as `ScopeUnknown`, which `confirm`
  correctly, and silently, treats as "not full-suite". So it exited 3 ("unearned
  scope") on every repo, forever, **including one whose entire suite had just run
  unfiltered**. (`set-scope`'s payload was not valid JSON either, so even a routed call
  would have set IMPACT.) It failed in the safe direction, which is why nothing caught
  it: a check that always refuses is never wrong — it is merely useless, and the
  workaround for a useless check is a bash harness making merge decisions. An
  unknown-command reply is now WARNED about rather than folded silently into
  `ScopeUnknown`; safe-and-mute is how a broken check stays broken for its whole life.

- fix!: **`test-rerun` can no longer exit 0 without running.** (AUTOMATION-99) A `busy`
  reply — the force-run produced no result within its budget — now exits NON-ZERO. It
  stays distinct from "Tests failed" (nothing is known to be broken), but `test-rerun` is
  the "prove it ran" verb and an exit 0 with no run is a vacuous green.

- fix: the CLI has its own verdict-free `StatusView`, and the wire no longer duplicates
  the verdict on the status payload (AUTOMATION-99). The summary + elapsed travel only in
  `lastRun` — the one channel every renderer already read — so the CLI never has to
  fabricate a `RunVerdict` from untrusted input, and the two copies cannot disagree.
  The status parse stays TOTAL: a plugin can never drop out of the status map.

- feat!: **new `fshw confirm` verb — runs the FULL test suite and refuses a green verdict
  from anything less.** `fshw check` remains the inner dev loop and keeps impact
  filtering, which is what filtering is genuinely good for.
  `fshw confirm` runs the same checks (build, format, lint, analyzers, coverage) but puts
  the daemon in full-suite scope BEFORE it triggers its scan — so the test run the
  scan provokes is already unfiltered, and `confirm` never pays for two runs.

  The enforcement is in the TYPE, not in a docs note: `CheckVerdict.verdict` now takes
  a `CheckMode` (`InnerLoop` | `Confirmation`) and a `TestScope` (what the run actually
  covered), and `Confirmation` has **no branch that reaches `Clean` without a
  `FullSuite`** scope. An impact-filtered run, a run that executed no tests, and a
  scope the daemon could not report (old daemon, no test-prune plugin, transport
  fault) all land on the new `CheckOutcome.UnearnedScope` — **exit 3**, distinct from
  failure (1) and incompleteness (2), because "there is no verdict" is a different
  event from "a problem was found", and an autonomous caller must be able to tell
  them apart. Failing closed on an unknown scope is the safe direction by
  construction: `confirm` goes green only on a scope it positively established.

  Why enforce it structurally at all: "remember to also run an unfiltered `test-rerun`
  before merging" is exactly the discipline that has already failed. A check that
  depends on someone remembering confirms nothing. (AUTOMATION-112)

- fix!: **the `beforeRun` hook ran with an INFINITE timeout** — the single most
  dangerous spawn in the daemon. It executes INSIDE the `RunExclusive "tests"`
  slot, and a real one is a multi-command shell chain including a network
  `dotnet restore`. A hook that hung — or, worse, one that EXITED while a
  grandchild (an MSBuild node, a Playwright driver) still held the inherited
  stdout pipe, which the old unbounded success-path drain waited on forever —
  held the tests slot for good: the plugin stayed `Running`, every later `check`
  burned its full 60-min deadline, and only a daemon restart recovered. Hooks are
  now bounded by the same `timeoutSec` default (600s) every other spawn uses, and
  a hung hook TIMES OUT into a legible failure.
- fix: the format preprocessor is now registered with the configured `timeoutSec`
  (see FsHotWatch.Fantomas) — an unbounded Fantomas run inside the change agent
  could silently stop the daemon from ever processing another file change.
- **BREAKING:** `"cache": "file"` / `"jj"` in `.fshw.json` is rejected with a loud
  warning and runs with no check cache (which is what it already did — the on-disk
  FCS check cache could never produce a hit; see FsHotWatch core).
  `CacheBackendConfig.FileBackend` and `detectDefaultCacheBackend` are gone.

- fix!: `check` surfaces a wedged plugin instead of hanging forever. When the
  daemon's new hard verdict deadline fires, the CLI recognises it
  (`IpcOutput.isVerdictWaitTimeout`) and fails with a diagnostic exit 2 naming
  the stuck plugin, its elapsed time, and the recovery path (inspect
  `logs/daemon.log`, `fshw stop`, or raise `FSHW_VERDICT_DEADLINE_SEC`).
  Previously an unbounded wait meant `check` never returned at all.
- fix: `fshw init` no longer hangs on a devenv/nix repo. `discoverProjects`
  walked from the REPO ROOT with `SearchOption.AllDirectories`, which follows
  `.devenv/profile` into the `/nix/store` symlink cycle. It now uses `SafeWalk`.
  The injectable enumerator seam drops its vestigial `SearchOption` parameter
  (recursion is now always safe AND always recursive) — a source-breaking change
  for anyone calling `discoverProjects` with an injected enumerator.
- chore: bundles the core + TestPrune fixes above (symlink-safe walks,
  bounded verdict wait).

## 0.14.0-alpha.3 - 2026-07-11

- chore(deps): bundle TestPrune.Falco 2.0.4 + TestPrune.Core 5.0.0 —
  function-scoped route edges. Each route's tests now link to that route's
  handler function, so a one-function change to a multi-route Falco handler no
  longer over-selects every route's browser tests.

## 0.14.0-alpha.2 - 2026-07-08

- chore: rebuild to bundle updated dependencies

## 0.14.0-alpha.1 - 2026-07-06

- chore: rebuild to bundle updated dependencies


## 0.13.0-alpha.1 - 2026-07-05

- chore: rebuild to bundle updated dependencies


## 0.12.0-alpha.1 - 2026-07-03

- fix: `fshw check` no longer misreports a daemon-startup race as failures. A
  check issued right after a daemon (re)start used to fire its first RPC while
  the daemon was still cold-scanning (analyzer load starving the pipe acceptor)
  or briefly between pipe endpoints during a stop→start; the `ConnectAsync`
  timeout / connection-loss surfaced as **exit 1** ("failures found"), poisoning
  an autonomous loop's verdict. A readiness gate now probes the daemon with a
  lightweight RPC and RETRIES transient connect faults (with a visible progress
  line) against a startup deadline distinct from the per-RPC connect timeout. A
  genuine connect failure — daemon absent, crashed during startup (detected via
  the pidfile, so it fails fast instead of spinning), or never responsive — now
  exits **2** (un-completable) with a pointer to `logs/daemon.log`, **never**
  exit 1. (AUTOMATION-66; `isTransientConnectFault` / `waitForDaemonReadyWith` /
  `withCheckIpc`)
- feat: `fshw test-rerun --wait-sec <seconds>` sets how long to wait for an
  in-flight background test run to release the slot before reporting `busy`
  (default **600**, up from a fixed 120 s). A long `tests.beforeRun` chain (90 s+)
  held by a prior run no longer defeats an explicit rerun before it can execute.
  (AUTOMATION-66)

## 0.11.0-alpha.1 - 2026-07-03

- fix: a failing `beforeRun`/hook now includes the command's captured
  stdout/stderr in the raised error (previously only the command string). A
  preflight failure propagates through TestPrune's `Aborted` lifecycle into the
  plugin's `Failed` status, so this is what makes `fshw check` / `fshw errors`
  show **why** the preflight failed, not just that it did. (AUTOMATION-68)

## 0.10.0-alpha.1 - 2026-07-03

- chore: rebuild to bundle updated dependencies


## 0.9.0-alpha.1 - 2026-07-03

- fix: a daemon shutdown/transport teardown during `check`'s settle wait now exits **2** with a clear "daemon shut down mid-wait" diagnostic instead of crashing with an opaque connection-loss error (AUTOMATION-65; `IpcOutput.isDaemonShutdownDuringWait`).

## 0.8.0-alpha.41 - 2026-07-02

- feat: the report-producing plugins (**analyzers** and **lint**) now skip compile items that resolve **outside the repo root** by default — NuGet-injected `_content` source (e.g. xunit.v3's `DefaultRunnerReporters.fs`) or files above/beside the repo. Such third-party source is compiled in but not yours to lint (and was a latent analyzer-crash surface — AUTOMATION-49). Opt back in with `"includeOutsideRepo": true` in `.fshw.json`. (`obj/`+`bin/` are always skipped independently.)

## 0.8.0-alpha.40 - 2026-06-30

- fix(testprune): the bundled test gate now defers on artifact **freshness** as
  well as presence — a present-but-**stale** test binary is deferred as "waiting
  on build" instead of being run with `--no-build`, so it can't report a passing
  verdict against code that no longer matches the sources (see FsHotWatch.TestPrune
  / ADR-008).
- refactor(build): the bundled build plugin's single-case `BuildPhase` union is
  flattened into `BuildState` — no behavioural change (see FsHotWatch.Build).

## 0.8.0-alpha.39 - 2026-06-24

- fix(build): a test-file-only edit no longer goes green against a **stale test binary** — the build plugin now runs a real build (re-emitting the DLL) instead of trusting the FCS `BatchChecked` type-check signal, so `--no-build` test runs can't execute an out-of-date assembly (see FsHotWatch.Build / ADR-012).
- chore(deps): bump `Microsoft.Data.Sqlite` 10.0.9; pin `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 (clears NU1903 / GHSA-2m69-gcr7-jv3q, High).

## 0.8.0-alpha.38 - 2026-06-19

- feat: `.fshw.json` test projects accept a `reportVerificationFormat` field
  (`auto` | `ctrf` | `off`) controlling how the test verdict's structured report
  is obtained (default `auto`). See the README config reference.

## 0.8.0-alpha.37 - 2026-06-17

- chore: rebuild to bundle updated dependencies


## 0.8.0-alpha.36 - 2026-06-17

- chore: rebuild to bundle updated dependencies


## 0.8.0-alpha.35 - 2026-06-17

- fix: configured-but-not-running analyzers now turn the gate RED instead of
  passing silently, PER PATH. Every `.fshw.json` `analyzers.paths` entry must
  contribute ≥1 analyzer; if ANY configured path loads zero — the path is
  missing (e.g. a bin dir built in the wrong configuration, the actual CI bug)
  or present-but-empty — `fshw check --run-once` exits non-zero with `config
  error: Analyzer path(s) loaded 0 analyzers (missing/empty or built in the
  wrong configuration): <path> — check .fshw.json analyzers.paths vs the build
  config`, naming the offending path(s). This catches the partial silent-skip
  the earlier aggregate (total==0) guard missed: a multi-path config where some
  paths load and one quietly loads nothing now goes RED. Linters/analyzers are
  treated like test failures: configured-but-not-running is never a silent pass.

## 0.8.0-alpha.34 - 2026-06-16

- feat: `fshw` re-runs a project's dependent tests when its dependency
  fingerprint changes — a NuGet/`PackageReference` bump, or a referenced project
  rebuilt against a changed dependency — even when no F# symbol moved. Bundles
  the updated FsHotWatch.TestPrune + TestPrune.Core 4.3.0 (`ProjectFanout`).
- fix: a failing test's NAME is now surfaced in `fshw`'s console output, not
  just the saved `.fshw/test-runs` log — so a CI red is diagnosable from the
  console alone instead of collapsing to `failed: 1`. Bundles the
  FsHotWatch.TestPrune observability fix.

## 0.8.0-alpha.33 - 2026-06-16

- fix: a directly-edited test now re-selects itself. Bundles TestPrune.Core
  4.2.3, so `fshw` re-runs a test you just edited instead of skipping it as
  unaffected and leaving a prior failure pinned red (FsHotWatch ISSUE B).

## 0.8.0-alpha.32 - 2026-06-15

- fix: `dotnet fshw check` no longer reports a false green before the test-prune verdict
  lands. The CLI gate now blocks on the daemon's `WaitForComplete` (which waits for every
  triggered plugin run to reach a real terminal verdict) instead of an Idle-tolerant
  scan-settle — closing a race where `check` could exit 0 "No errors" while the
  affected-tests run was still about to launch.

## 0.8.0-alpha.31 - 2026-06-15

- feat: configurable FSEvents watch latency via the new `.fshw.json` `fsEventsLatencyMs`
  key (default 250ms, replacing a hardcoded 50ms). Higher values let the kernel coalesce
  more file-change events per callback, cutting fseventsd dispatch load on busy trees, at
  the cost of slightly higher change-to-rebuild latency.

## 0.8.0-alpha.30 - 2026-06-12

- chore: refresh bundled dependencies — CommandTree 0.6.3, TestPrune.Core 4.2.2,
  TestPrune.Falco 2.0.2 — and rebundle the updated FsHotWatch core plugins.

## 0.8.0-alpha.29 - 2026-06-11

- fix: `test-rerun --filter-class` / `--filter-trait` force-executes the matched
  tests instead of returning an instant non-result. Two changes: (1) when a
  background test run holds the test slot, `run-tests` now WAITS (bounded) for it
  to finish and then runs — rather than bailing instantly with "tests already
  running" (no run, no log); if a run is still in progress after the wait it
  reports a DISTINCT `busy` status (exit 0, retry), never a generic verdict.
  (2) a filtered run that matched NO test anywhere is rendered distinctly as
  `⏭ No tests matched the filter` (exit 0), instead of a misleading `✓ Tests
  passed` that looks like a real green run — so you can tell the filter selected
  nothing. The cache was never consulted on this path, so a stale verdict could
  not have been replayed; the instant non-result was the in-flight-slot guard.

## 0.8.0-alpha.28 - 2026-06-11

- chore: rebuild to bundle updated dependencies

## 0.8.0-alpha.27 - 2026-06-10

- chore: rebuild to bundle updated dependencies


## 0.8.0-alpha.26 - 2026-06-10

- chore: rebuild to bundle updated dependencies


## 0.8.0-alpha.25 - 2026-06-09

- chore: rebuild to bundle updated dependencies


Note: CLI versions release together with the core package under the `core-v` tag (no separate `cli-v` tag prefix).

## 0.8.0-alpha.24 - 2026-06-08

- **breaking:** retired the `build`, `test`, `lint`, `analyze`, `format-check`, and `errors`
  subcommands, collapsing the CLI to two verbs that matter: **`check`** (the gate — runs every
  plugin, waits for genuine completion, and exits non-zero on failures or unconfirmed
  completeness) and **`status`** (the observer — reports the daemon's current state without
  triggering a run). Replacements: `fshw build`/`test`/`lint`/`analyze`/`format-check`/`errors`
  → `fshw check`; inspect one plugin with `fshw status <plugin>`; re-fire one with
  `fshw rerun <plugin>`. `check`'s converge-then-verdict completeness/exit-code semantics are
  unchanged — this is purely a surface collapse.

## 0.8.0-alpha.23 - 2026-06-07

- fix: `fshw dead-code`'s schema-compatibility probe now sources its expected version
  from TestPrune.Core's public `Database.SchemaVersion` (4.2.1) instead of a hardcoded
  copy — the probe moves in lockstep with the bundled Core, so a future schema bump
  can no longer silently invert the wipe-protection.

## 0.8.0-alpha.22 - 2026-06-07

- fix: a CLI invoked as 'dotnet <local-dll>' now spawns that same DLL as the daemon instead of silently launching the pinned 'dotnet tool run fshw' — local daemon builds dogfood correctly

## 0.8.0-alpha.21 - 2026-06-06

- feat: `fshw dead-code` — runs TestPrune's unreachable-symbol analysis against the daemon's .fshw/test-impact.db (same semantics as the standalone test-prune CLI: --entry/--include-tests/--verbose), no DB copying needed.

## 0.8.0-alpha.20 - 2026-06-05

- chore: bundle FsHotWatch.TestPrune 0.7.0-alpha.19 (clears the FCS check-cache after a
  schema-bump DB recreate, so the symbol graph re-indexes fully instead of staying
  partial). No CLI behavior change; republished so the bundled tool ships the fix.

## 0.8.0-alpha.19 - 2026-06-04

- chore: republish to bundle FsHotWatch.TestPrune 0.7.0-alpha.18 (cold-start coverage
  clobber fix) so `dotnet fshw` carries it. No CLI-facing changes.

## 0.8.0-alpha.18 - 2026-06-04

- chore: republish to bundle the DB-backed coverage plugins (FsHotWatch.TestPrune
  0.7.0-alpha.17, FsHotWatch.Coverage 0.7.0-alpha.11) so `dotnet fshw` carries
  TestPrune-native single-source coverage. No CLI-facing changes.

## 0.8.0-alpha.17 - 2026-06-03

- chore: bump CommandTree 0.6.1 → 0.6.2 — picks up the revision-stamping target fix, so building `fshw` outside a VCS repo (e.g. a `.git`-less jj sub-workspace) no longer emits `MSB3073` warnings.

## 0.8.0-alpha.16 - 2026-06-02

- feat: strict CLI parsing (CommandTree 0.6.1) — invalid input (unknown flags/commands) now fails hard with a clear error, the nearest subcommand's help, and a non-zero exit, uniformly and even outside a jj/git repo (previously masked by the repo-root check). Unknown top-level commands are still forwarded to the daemon for plugin resolution and only fail hard when genuinely unrecognized.

## 0.8.0-alpha.15 - 2026-05-28

- chore: bump CommandTree 0.5.0 → 0.5.1.

## 0.8.0-alpha.14 - 2026-05-26

### Added

- `fshw test-rerun [--filter-class <pattern>] [--filter-trait <name=value>]` command. Routes through the existing `run-tests` IPC with the filter passed to the xUnit v3 standalone runner. Lets callers slice a test run for investigation (single class, single trait) without bypassing the daemon — preserving the analyzer/lint/format/coverage hooks. Daemon-only.

  Filter flags deliberately do NOT live on `fshw test`. The forward-progress `test` verb runs everything downstream of a change via test-prune's impact analysis; letting it filter would silently weaken that guarantee. Slicing belongs on the explicit investigation verb.

## 0.8.0-alpha.13 - 2026-05-04

- chore: release together with core 0.8.0-alpha.13

## 0.8.0-alpha.12 - 2026-04-29

### Added

- Run-once output now warns when a `FileCommand` plugin's input files have been modified after the plugin's last successful run. Defense-in-depth against stale cached output. New helpers in `FsHotWatch.Cli.RunOnceOutput`: `PluginRunInfo`, `detectStalePluginInputs`, `formatStalenessWarning`.

### Removed

- **`FsHotWatch.Cli.DaemonConfig.canonicalDllPath`** and **`stalenessCheck`/`dirtyTracker` registrations.** With BuildPlugin owning post-build verification, DaemonConfig no longer constructs the dirty-bit handoff or the mtime-probe closure. The canonical DLL path now lives on `IProjectGraphReader.GetCanonicalDllPath` in the core lib so it can be unit-tested against the graph directly.

### Fixed

- TestPrune staleness check no longer false-positives when an orphaned TFM directory is left in `bin/` after a `<TargetFramework>` bump. The check now parses the .fsproj and probes the canonical `bin/Debug/<TFM>/<projectName>.dll` instead of recursively globbing every `bin/**/<projectName>.dll` and taking the max mtime (which surfaces stale `bin/Debug/net9.0/` entries even when the current `bin/Debug/net10.0/` DLL is fresh).

### Changed

- **BREAKING — naming normalized to `fshw`:**
  - **CLI command** is now `fshw` (was `fs-hot-watch`). The `ToolCommandName` in the package and the pipe-name prefix both use `fshw`.
  - **Config file** is now `.fshw.json` (was `.fs-hot-watch.json`). Existing repos must rename.
  - **State directory** is now `.fshw/` (was `.fs-hot-watch/`). The pid, lock, and config-hash files live alongside the existing `cache/`, `errors/`, `logs/`, `test-runs/`, and `test-impact.db` — one directory for everything fshw writes. Existing daemons must be stopped and the legacy `.fs-hot-watch/` directory deleted.
- `mise check`'s coverage step now auto-corrects thresholds: tries `coverageratchet ratchet`, falls back to `loosen` when coverage drifted below threshold. Other tool exit codes (crash/OOM/killed) propagate so the threshold file is not silently rewritten on tool malfunction.

### Removed

- **BREAKING:** `scan --force` flag removed. The flag had been a no-op since the jj scan-guard was deleted; the IPC `Scan` method, `ScanFlag` DU, and CLI `--force` argument are gone.

## 0.8.0-alpha.11 - 2026-04-26

### Added

- `unwrapIpcException` — peels `AggregateException` wrappers so the CLI surfaces the underlying OOM / Timeout / pipe-corruption exception instead of "One or more errors occurred."

## 0.8.0-alpha.10 - 2026-04-25

### Added
- `fs-hot-watch config check` — validates `.fs-hot-watch.json` without starting the daemon. Exits `0` on valid config, `2` on parse/validation error. Intended for editor integration and CI.

### Changed
- **BREAKING (behavioral):** `.fs-hot-watch.json` parse and validation errors now abort startup with exit code `2` and a message naming the offending field. Previously, any parse failure was logged and the daemon silently ran with defaults. `fileCommands` validation failures (missing `pattern`/`afterTests`, `afterTests` without `name`) surface through the same exit-code-2 path.
- While the daemon is running, any write to `.fs-hot-watch.json` now stops it cleanly and logs the reason (`config changed, stopping (restart to apply)` for valid edits, `config invalid, stopping: <error>` for parse failures). Restart the daemon to pick up the new config. No hot-reload.

## 0.8.0-alpha.9 - 2026-04-23

### Added
- `--agent` / `-a` global flag: parseable, token-minimal output for AI coding agents. Emits a one-line banner, `name: state [summary="..."]` per non-idle plugin, and a state-aware `next:` hint (e.g. `next: fs-hot-watch --agent build` when the build fails). States: `ok | fail | warn | running`. No ANSI, idle plugins omitted. Diagnostic output (`errors --agent`) uses the format `<plugin>:<file>:<line>:<col>: <severity> <message>`.

### Removed
- **BREAKING:** `coverage` config block no longer accepted. Coverage enforcement now flows through `fileCommands` with `afterTests`, invoking an external CLI (e.g. `coverageratchet`).
- `FsHotWatch.Coverage` project dependency (retired).
- `runOnStart` field on `fileCommands` entries (see FsHotWatch.FileCommand CHANGELOG).

### Changed
- **BREAKING:** `--compact` / `-q` is now a global flag, not a per-command flag. Invocation changes from `fs-hot-watch check -q` to `fs-hot-watch -q check`. Matches the placement of other global flags (`--verbose`, `--agent`). Accepted on every subcommand, including `status` and `errors`, which previously didn't support it.
- `fileCommands` entries accept `name` (string) and `afterTests` (`true` or string list) fields. An entry must set at least one of `pattern` / `afterTests`; entries with `afterTests` must have an explicit `name`.
- Coverage output directory is now configured via `tests.coverageDir` (default `"coverage"`). Previously lived on the removed top-level `coverage.directory`. Per-project opt-out via `tests.projects[].coverage = false` unchanged.

- chore: bump upstream tool versions

## 0.8.0-alpha.8 (2026-04-22)

### Added

- `errors --wait [--timeout <seconds>]` — block until every tracked plugin reaches a terminal state before printing diagnostics. Timeout is enforced server-side by the daemon's `waitForAllTerminal` loop, so timeout messages include the list of plugins still running. Exit codes: `0` clean, `1` failures, `2` timeout or invalid flag combination (e.g. `--timeout` without `--wait`). Default timeout 600s.

### Fixed

- `start` is now a singleton per repo, enforced by an OS-level exclusive file lock on `.fs-hot-watch/daemon.lock` held for the daemon's lifetime. Two concurrent `start` invocations cannot both acquire the lock, so duplication is impossible rather than just unlikely. The second invocation exits `0` with `Daemon already running at pipe <name> (pid <n>)`. Previously, repeated `start` invocations could race past the probe-based guard and accumulate concurrent daemons serving stale results.
- `stop` drains running daemons until `IsRunning` returns `false` on two consecutive probes (bounded by a 30 s overall timeout), reporting the count stopped (or `No daemon running` if none). The fixed 10-attempt cap used previously could leave orphans when more duplicates had accumulated, and the fixed single-probe termination could misreport "No daemon running" while the OS was still tearing down the last pipe endpoint.

## 0.8.0-alpha.3 (2026-04-18)

### Added

- `exclude` config field in `.fs-hot-watch.json` — array of gitignore-style glob patterns to exclude entire project trees from discovery (e.g. `["vendor/"]`)
- Pass `config.Exclude` to `Daemon.create` for project-level exclusion

---

## 0.5.0-alpha.1 (2026-04-12)

### Added

- Filter Info/Hint diagnostics from CLI output — only Error and Warning shown in both daemon and run-once modes

### Changed

- `DiagnosticEntry.Severity` typed as `DiagnosticSeverity` DU instead of string in `IpcOutput`
- `startFreshDaemon` startup poll deadline now configurable via `startupTimeoutSeconds` parameter (default: 30s)
- Process launch in `startFreshDaemon` injectable via `IpcOps.LaunchDaemon` for testing
- Bump `CommandTree` 0.3.5 → 0.4.0, `TestPrune.Falco` 1.0.1 → 1.0.2
- Deduplicate `DisplayStatus` type — reuse `PluginStatus` from core `Events` module
- Deduplicate `formatStatusLine`/error formatting — reuse `RunOnceOutput.formatStepResult` and `RunOnceOutput.formatErrors`
- File/process operations injectable via `FileOps`/`ProcessOps` records for testability

### Fixed

- `renderIpcResult` crash (`InvalidOperationException`) on JSON containing array values (e.g. test results with `projects` array)
- Guard `statusMap` fallback against non-string JSON values

---

## 0.3.0-alpha.1 (2026-04-08)

### Bug fixes

- **Breaking:** `run-once` positional subcommand replaced by `--run-once` flag (e.g. `check --run-once`, `build --run-once`) — uses CommandTree flag list support
- Fix `build --run-once` not running the build plugin — `stripConfig` was discarding the build config; it is now restored
- Fix `format-check` (run-once and daemon mode) always reporting "No errors" — was querying plugin errors under name `"format"` instead of `"format-check"`
- Fix `check` (daemon mode) hanging forever when a `file-cmd-*` plugin stayed Idle — Idle is now treated as terminal in `pollAndRender`
- Fix daemon auto-start when running as a `dotnet` local tool — `computeLaunchCommand` now detects the dotnet binary and constructs `dotnet tool run fs-hot-watch`

### Improvements

- Extract `isRunOnce` helper and `withDaemon` guard in `executeCommand` to remove repetition
- Fix `format` (daemon mode) to pass result through `renderIpcResult` consistently with other commands
- Avoid redundant `IsRunning` probe at end of `startFreshDaemon` polling loop

### Infrastructure / CI

- CLI moved under core's shared tag in `semantic-tagger.json` — no longer versioned separately
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1
- Bump `CommandTree` 0.3.3 → 0.3.5 (flag list support)

---

## 0.2.0-alpha.1 (2026-04-07)

First release of CLI under the shared release cycle (previously first released as 0.1.0-alpha.1 in the 0.2.0-alpha.1 monorepo release).

- Add MIT license; add SourceLink; enable `TreatWarningsAsErrors`; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Add `--version` flag
- Bump `CommandTree` 0.3.2 → 0.3.3
- Bump `TestPrune.Falco` 0.1.0-beta.1 → 1.0.1

---

## 0.1.0-alpha.3 (2026-04-02)

CLI completely rewritten.

- **Breaking:** CLI completely rewritten to use `CommandTree` library — hand-written `parseCommand` removed
- **Breaking:** CLI output changed from JSON on stdout to colored text on stderr — update any scripts parsing CLI output
- **Breaking:** `AnalyzeCheck` command renamed to `Analyze`
- **Breaking:** `ScanStatus` removed as standalone command — integrated into `Scan`
- **Breaking:** `PluginCommand` removed from Command DU — unknown commands handled via error path
- Add `--no-warn-fail` global flag — warnings don't cause non-zero exit codes
- Add `IpcOutput` module for parsing and colored rendering of IPC responses
- Add `pollAndRender` for live progress display in daemon mode
- Add fish shell completions via `fs-hot-watch completions`

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `DaemonConfiguration.Build` changes from single build config to `list option` — supports multiple build steps
- **Breaking:** `DaemonConfiguration.Format` changes from `bool` to `FormatMode` DU (`Off | Auto | Check`)
- Add `--run-once` flag on subcommands: `start`, `build`, `test`, `format`, `lint`, `analyze`
- Add `fs-hot-watch init` — generates `.fs-hot-watch.json` from discovered projects
- Add format `"check"` mode — read-only format checking without modifying files
- Add test extensions config (e.g., Falco route mapping)
- Add `coverage.afterCheck` config option

### Migration from 0.1.0-alpha.1

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]

// "format" accepts string mode instead of bool:
"format": "auto"   // or "check" or "off" (booleans still work)
```
