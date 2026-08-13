# Changelog — FsHotWatch.TestPrune

## Unreleased

## 0.13.0-alpha.10 - 2026-08-13

- fix: unblock the release — coverage floor with real headroom, versions rolled back
- Comment audit: cut AI thinking-out-loud from comments


## 0.13.0-alpha.10 - 2026-08-13

- Comment audit: cut AI thinking-out-loud from comments


## 0.13.0-alpha.9 - 2026-08-12

- chore(deps): **the `SQLitePCLRaw.lib.e_sqlite3` pin is removed — `TestPrune.Core`
  carries that floor itself now.** The pin existed because `TestPrune.Core`'s
  `SqliteSymbolStore` pulled `lib.e_sqlite3` 2.1.11 transitively, which carries
  GHSA-2m69-gcr7-jv3q (High, `NU1903`). `TestPrune.Core` 6.1.2 declares
  `SQLitePCLRaw.lib.e_sqlite3 3.50.3` as its own dependency, so a forced no-cache
  restore resolves 3.50.3 with the pin gone. Restore-time `NU1903` stays the actual
  guard if a vulnerable version ever resolves again.

## 0.13.0-alpha.8 - 2026-08-11

- fix: **a `ProjectReference` whose `Include` is an MSBuild property no longer defers
  every test run.** The artifact-freshness gate resolved an `Include` by joining it onto
  the project directory with no property expansion, so a computed reference —
  `<ProjectReference Include="$(SomeProject)" />` — became a search for a file literally
  named `$(SomeProject)`. That never exists, so the gate answered "cannot determine what
  this test run's inputs are" and deferred the run as *waiting on build*, permanently, on
  a tree that was perfectly fresh. Found against TestPrune, whose test project computes an
  optional sibling reference exactly this way: `dotnet fshw check` exited **2** with scope
  `none` and no test ever ran.

  The gate now expands properties declared in the project's own `PropertyGroup`s, plus
  `MSBuildThisFileDirectory` and `MSBuildProjectDirectory`. Deliberately *not* an MSBuild
  evaluation — anything it cannot expand stays unexpanded, the path still does not exist,
  and the existing fail-closed arm still refuses. The worst case is exactly the old
  behaviour, never a false "fresh".

  Also: **a reference the project guards with a `Condition` is treated as optional** when
  it resolves to a missing file, rather than as an undeterminable input. That is how an
  optional sibling checkout is expressed, and without it a machine that has not cloned the
  sibling defers every run over a reference the build correctly ignores. The trade, stated
  plainly: a *required* reference wrongly carrying an always-true `Condition` is now
  skipped rather than refused — narrower than it sounds, since the build runs before this
  gate and fails loudly on a genuinely missing reference, which is the deferral this gate
  says it wants. An **unconditional** missing reference is still an error.
- feat!: **the public `ZeroMatchMarker` literal is removed.** It was the magic output
  prefix a zero-match result used to be tagged with, before "the filter matched no test"
  became a `TestResult` case. Nothing constructed a value carrying it any more, and its
  one remaining reader — reconstructing pre-existing cache entries — names
  `TestResult.LegacyZeroMatchMarker` in core instead. Anyone referencing
  `TestPrunePlugin.ZeroMatchMarker` should switch to `TestResult.isNoMatch` to ask the
  question, or to the core literal if they genuinely need the legacy string.
- fix: **the run summary no longer counts a project that ran no tests as one that
  passed.** `passed` was derived by exclusion (`total - failed - deferred`), and
  `TestResult.isPassed` is deliberately true for a zero-match project, so a mixed run
  reported "2 passed, 0 failed in 2 projects" while the CLI — which counts them
  separately — said "2 project(s): 1 passed, 1 matched nothing". Two surfaces describing
  one run differently, and the daemon's was the one claiming a pass nothing earned.
  Zero-match now has its own term in the summary.
- feat: **every test project's output is saved, streamed, for every run**
  (AUTOMATION-279). `.fshw/test-runs/<runId>/<Project>.output.log`, written as the
  runner speaks, on success as well as failure and with no project special-cased —
  which suite will need explaining is not knowable in advance, and a passing-but-slow
  one is worth reading too. The failure that most needs it is the suite SIGKILLed at
  its `timeoutSec`: it reaches no end-of-run writer at all, so anything that buffered
  and flushed at the end would leave nothing exactly where everything is wanted.
- fix: **the failure report no longer points at a log nobody wrote.** It said the
  failure was visible "*without the saved log*" — and there was no saved log; the
  plugin's only `File.WriteAllText` emitted Cobertura coverage. It now names the run
  log's real path, or, when the log could not be opened, says so and why. The 40-line
  console tail stays: this ADDS an artifact, it does not remove the summary. It is
  also structurally the wrong end of the output for a killed run — an integration
  suite hit its 900s cap on four consecutive `check`/`confirm` runs and all forty
  lines of tail were the same repeated startup logging from seven app instances,
  while the cause ("test shard pool … is already in use by PID 18024") had been
  printed in the first seconds, at the head. Five wrong hypotheses were chased before
  someone ran the suite by hand.
- fix: **a schema recreate could discharge real test debt as a green that ran nothing
  (AUTOMATION-275).** `ChangedSymbolsAllUncovered` buys a "nothing to verify" green that
  executes zero tests, and it was inferred from `QueryAffectedTests` returning empty.
  That result only proves "no test covers this symbol" for a symbol the index KNOWS; for
  a name it has never heard of, the identical empty result means "I cannot answer". A
  `SchemaVersion` bump makes the difference matter: TestPrune deletes and recreates
  `test-impact.db` on schema drift, while the `pending-verification.json` sidecar beside
  it carries no version and **survives**. Every name in the surviving queue then resolved
  to nothing, was dropped as "no runnable covering test", and the cycle completed green
  having run no tests at all — the outstanding debt retired by a schema bump rather than
  by a test. Reproduced end-to-end: a symbol indexed *with* a covering test, queued
  unverified, then orphaned by a recreate, logged
  `Every changed symbol has no covering test — nothing to verify, skipping tests (green, 0 ran)`.
  The shortcut now additionally requires that the index was not rebuilt this session
  (`Database.WasRecreated`, the same signal that already invalidates the FCS check cache);
  the symbols are still dropped from the queue, so a permanently-absent symbol cannot
  wedge it, but the run now actually happens and it is the run that discharges the debt.
  Scoped tightly — on a genuine cold start the queue is empty, so the flag was already
  false and nothing changes.
- feat: **poisoned pending-verification seeds are now named in the log
  (AUTOMATION-275).** A symbol leaves the needs-testing queue only when every runnable
  project covering it passes, so a single persistently-red project pins it indefinitely,
  and while pinned it re-seeds its whole selection on every run. Combined with a
  mis-qualified symbol (AUTOMATION-270's `name`/`kind`, which alone selected ~2,837
  tests) that produced a permanent, invisible, near-full suite in which each individual
  run looked like an ordinary expensive edit — width alone could not distinguish them,
  only width that persists. Any seed queued across three consecutive runs while alone
  accounting for ≥25% of the selection is now reported by name, with its age and share.
  A warning rather than a quarantine on purpose: dropping a queued symbol on a heuristic
  would be under-testing, and the failure was that nobody could see the pattern — not
  that nothing could be done once seen.

  Counted per test RUN, at the point a run is launched against the queue. Counting per
  *flush* — the first shape — ticked 2-3 times per edit-save cycle, because the flush
  runs on `BatchChecked`, again on `BuildSucceeded` and again on the rerun path; editing
  one function twice on a green repo was enough to trip the threshold. A guard designed
  to fire late specifically so it would not cry wolf during an ordinary red-to-green
  cycle would have done exactly that, and paid a graph query per seed for the privilege.
  The per-seed check is also budgeted (`MaxSeedsToAttribute`, the same cap the
  neighbouring attribution breakdown uses) and skipped entirely on an empty selection:
  each check is a recursive reverse-walk, and the aged-seed list grows precisely when the
  queue is wedged — the very state the guard exists to report — so an unbudgeted loop
  would cost most when the daemon could least afford it. As next door, the cap is never
  silent: a skipped check says so, and why.

- fix: **`run-tests` says which project you asked for and which exist when nothing
  matches (AUTOMATION-272).** The reply was a bare `no matching test projects`, which is
  unactionable for the one case that actually produces it — a mistyped or renamed
  `--project` — while the configured project names were right there to list.
- fix: **a run in which every project matched zero tests is no longer a green
  (AUTOMATION-272).** Two aggregators were blind to it in the same way. `recordRunOutcome`
  — the sole producer of this plugin's terminal status — decides on `TestResult.isPassed`
  counts, and `executeTests` records a zero-match project as `TestsPassed` carrying the
  `ZeroMatchMarker` prefix. `isPassed` is therefore true, `failed = 0 && deferred = 0`
  holds, and the green branch fired: the status read **"2 passed, 0 failed in 2 projects"**
  for two projects that executed no test at all. The cache key's `allPassed` fold had the
  identical blind spot with a longer half-life — such a run was **cacheable and replayable
  as a green**, so a later `BuildCompleted` on the same tree could hit a cached pass minted
  by running nothing. Both now check for an all-zero-match run explicitly.
  `isPassed` cannot exclude this the way it excludes `TestsDeferred`/`TestsErrored`,
  because zero-match is not a case — it is a string prefix on a pass — so every fold over
  results has to remember to re-derive it. Two did; these two did not.
  **Per-project semantics are unchanged:** a zero match in ONE project is still a pass for
  that project, because an impact selection naming no class in some project must not fail
  it. Only the run-level verdict changes, and only when nothing matched anywhere — a
  mis-aimed filter or operator error, never a verified pass. A run with an empty result set
  (the deliberate "nothing to verify" skip) keeps its existing path in both places.

- feat: **a run states what it VERIFIED**, instead of leaving the consumer to infer it.
  `allZeroMatch` returned `false` for an empty result set — defensible in isolation
  ("no project ran" is not the claim "every project matched nothing") and wrong in
  effect: both no-op shapes became indistinguishable from a real run downstream, so a
  run that executed nothing reached the CLI looking like one that had, and was reported
  as `Tests passed`, exit 0. Replaced with `RunVerification` —
  `NoProjectsSelected | AllZeroMatch of projectCount | Ran`. Three cases rather than one
  case with a flag, because they carry different **evidence**: `AllZeroMatch` knows a
  filter ran against discovered tests and can say so, while `NoProjectsSelected` has no
  discovered names at all, so a "did you mean…" there would be guessing dressed as
  diagnosis. The `run-tests` payload keeps `noTestsMatched` for older CLIs and adds
  `coverage` (`"no-projects-selected" | "all-zero-match" | "ran"`) plus
  `verifiedNothing`. A consumer that does not know the new fields falls back to the
  counts — an **absent** field must never be read as `"ran"`.

- fix: **impact-selection breadth is measurable again.** "Affected classes for …",
  "Changed symbols: …" and `QueryAffectedTests(…)` rendered their collections with
  `%A`, which truncates at 100 elements — so a 1,500-class selection was
  indistinguishable from a 101-class one, and "Changed symbols" carried no count at
  all. All three now lead with the exact count via `StringHelpers.describeMany`.
  Empty stays distinguishable (`0 []`), which matters: a project present with no
  affected classes means "run it in full".

- fix: **an unfiltered project no longer logs its class list as `[]`.** A project
  selected with no class filter — one that is about to run its tests **in full** —
  rendered as `[]`, which reads as the exact opposite of what it means ("nothing was
  selected"). It now says `ALL (unfiltered — force-run)`.

- feat: **the impact query explains its own breadth.** `QueryAffectedTests` now logs
  the seed count and the **full, sorted seed list** — never sampled, because when a
  handful of junk seeds drags in thousands of tests the offending seed *is* the
  diagnosis and a sample that happens to omit it is worse than useless. When a
  selection exceeds 500 tests it adds a **per-seed breakdown** naming how many tests
  each seed pulls in on its own. Attribution re-queries each seed alone, so past a
  200-seed budget the breakdown is skipped — and **says** it was skipped, so its
  absence is never misread as "no single seed dominated".

- feat: **dependency fanout says why it did nothing.** A run with no fanout now
  reports fingerprints computed, projects in the graph, and priors available to
  compare against. Zero fingerprints or zero graph projects means the fanout is
  *inert*; previously that was indistinguishable from a healthy run where nothing had
  genuinely changed — an under-selection risk that looked exactly like a clean one.

- feat: **a run-level selectivity summary** is logged in one line before tests
  execute, answering "did impact analysis actually narrow anything this run?" without
  reassembling the per-project lines above it — which can be scrolled past,
  interleaved, or truncated by whatever is reading the log. It separates the two ways
  a run can be wide: many classes named, versus projects running unfiltered.

## 0.13.0-alpha.7 - 2026-08-05

- fix!: **a filtered re-run that PASSES retires the red it re-ran** (AUTOMATION-225).
  `test-rerun --filter-class X` re-ran X, X passed, and X's red survived — forever,
  because the run's launch selection records every project as `ProjectInFull` and an
  opaque filter string's reach cannot be read out of it. Coverage for such a run is now
  derived from the run's **own CTRF report** — the classes it shows ran and PASSED —
  rather than from the launch request.

  **BREAKING**: `RunCoverage.ofRun` takes a third argument, the per-project passed-class
  evidence (`Map<string, Set<string>>`). Pass `Map.empty` for the previous behaviour;
  `passedClassesOfRun repoRoot runId` reads it from the run's report directory.

  Fails closed everywhere: only classes with a pass and no failure are claimed; a
  project absent from the selection is claimed by nothing; project-level reds still need
  a full-project run; and a report that is missing, unparseable or shorter than its own
  summary total (raw-throw tests are omitted from the per-test array) claims nothing.

## 0.13.0-alpha.6 - 2026-08-03

- chore(deps): update dev-tools + external dependencies
- chore: trim stale/historical comments to minimal current-state context


## 0.13.0-alpha.5 - 2026-07-20

- chore(deps): TestPrune.Core 6.1.1 — bounded post-exit output drain in
  `runProcessWith` (grandchild-held pipe can't wedge the drain) (AUTOMATION-98).

## 0.13.0-alpha.4 - 2026-07-18

- chore(deps): TestPrune.Core 6.1.0 — bounded test-run wait (`WaitForExit` with a
  30-minute default hang detector, `TESTPRUNE_TEST_RUN_TIMEOUT_MS` override, exit
  124 + process-tree kill on expiry) (AUTOMATION-98).

## 0.13.0-alpha.3 - 2026-07-15

- fix!: **a copy and its origin are compared by CONTENT, so a target-framework mismatch
  cannot be expressed.** (AUTOMATION-169) The freshness gate's copy check asked
  `copyMtime < originMtime` — and **resolved the origin to the wrong target framework**.
  A multi-targeted dependency (`netstandard2.0; net8.0; net9.0; net10.0` — a vendored
  SqlHydra fork) consumed by a test project on **net10.0**: MSBuild copies the net10.0
  output, but the gate resolved the origin to whichever per-TFM output was **newest**,
  which was **net8.0**, built nine minutes later. So it compared a net10.0 copy against a
  net8.0 origin and condemned a **byte-perfect copy**. The message indicted itself — the
  origin it printed ended `/net8.0/`, the destination `/net10.0/`. **4 of 6 test projects
  refused to run**; verdict red.
  - **No `dotnet build` could answer it.** A correct rebuild re-copies net10.0, so the copy
    keeps the net10.0 stamp and the gate compares it against net8.0 again. That is the
    unanswerable accusation AUTOMATION-122 was written to kill, back through a different
    door: not the wrong project — the wrong **framework**.
  - Different TFMs of one project **build at different times**, so an mtime comparison
    *across* TFMs is not a bad heuristic, it is a **category error**. Correcting the
    resolution would leave the error expressible, so the mtimes are **gone from the copy
    verdict entirely**: `CopyOlderThanOrigin(origin, copy, originMtime, copyMtime)` is now
    **`CopyDiffersFromOrigin(origin, copy)`**. A verdict that holds no mtimes cannot compare
    two of them across a TFM boundary.
  - The rule is now one rule with two applications: **a copy is current iff its bytes are
    the bytes of one of the outputs the build could have copied it from** — for a content
    item, the file at that relative path; for a dependency assembly, any of that project's
    per-TFM outputs. The question never mentions a target framework, so it cannot get one
    wrong — and the gate never has to guess which TFM MSBuild picked (a question needing
    nearest-compatible-framework rules that a graph-free on-disk parse cannot answer).
  - **Strictly stronger, not weaker.** Content also catches what mtimes never could: a stale
    copy whose mtime *equals* its origin's — a `jj`/`git` working-copy restamp, a coarse
    filesystem timestamp, a rebuild inside one timestamp tick. Verified against the real
    consumer: the APPLIC-24 fake green (a changed fixture the build did not re-copy) is
    still caught **even with the mtime left untouched**, which the old rule called *fresh*.
  - Uses core's one hasher, `ContentHash`, and inherits its fail-closed sentinel: a file the
    gate **cannot read** is `InputsUndeterminable`, never "fresh" — in BOTH directions, an
    unreadable copy and an unreadable origin. `AssemblyOlderThanSource` (a `.fs` source vs
    the `.dll` compiled from it) **keeps its mtime** — those two share no bytes, so the clock
    is the only signal there, and that half of the module was right.
  - **Shadowing, closed in the same change.** A relative path can be claimed by several
    projects in one closure (`xunit.runner.json` sits in five of them in that consumer);
    MSBuild copies them all to one destination and the last writer wins. Comparing the
    survivor against a single claimant condemns the SHADOWED project for a build doing
    exactly what it means to do — and under a content rule that misfire would have been
    **permanent**, where the old mtime rule only tripped when the shadowed file happened to
    be newer. So a copy is now checked against **every claimant in the closure** and is
    current if it matches any. Fixing the TFM bug without this would have opened a new door
    for the same wolf.
  - This settles the standing disagreement between two sibling modules: `TreeHash` already
    held that *"the hash is over CONTENT, never mtimes — mtime is precisely what lied in
    APPLIC-24."* `ArtifactFreshness` now agrees.

- fix!: **a PROCESS may not assert a test result it has no record of running.**
  (AUTOMATION-161) On a warm task cache the first `BuildCompleted` of a new process was a
  cache **hit**, which skips the handler — so no test run happened, no `TestsFinished`
  landed, and `LastCoverage` stayed empty. The plugin then told everyone who reads its
  *state* (`test-scope`, and through it `.fshw/verdict.json`) that **NO TESTS RAN**, while
  its *status line* simultaneously reported *"1 passed (cached)"*. One tree, two surfaces,
  opposite answers — and **both `fshw check` and `fshw confirm` exited 3 on a green tree,
  on the second run, every time**.
  - The key cannot rescue this, because **the key does not pin the tree**. On a cold scan
    `BuildCompleted` is dispatched *before* the FCS pass, so `changed-symbols` is empty
    whatever the tree contains; two different trees that both build clean with an empty
    queue compute the *same* key. What makes a replay sound in a warm daemon is not the key
    but the symbol-diff pipeline that runs *after* it — and a new process has no such run
    to supersede the replay with.
  - So test-prune no longer participates in the task cache on `BuildCompleted` (no replay,
    **and** no write) until a run in *this* process has covered something — the same
    fail-closed shape as the existing pending-queue and outstanding-failure guards. This
    also **restores `hasCachedResults`** ("a cold start with no session baseline must run
    the full suite to establish one"), an invariant the cache was quietly defeating by
    skipping the handler it lives in.
  - **Analysis-only mode is exempt** (no runnable test projects ⇒ no test claim ⇒ nothing
    to fail closed about), and the **warm in-session inner loop is untouched**: once this
    session's first run lands, every later `BuildCompleted` replays as before.
  - Deliberately **not** done: minting a CTRF receipt for the replayed run id. That would
    manufacture merge-grade evidence from a key that cannot see the tree — the vacuous
    green, rebuilt inside the fix for it. The fast path a repeat `confirm` wants runs
    through `.fshw/verdict.json`, which *is* content-addressed to the tree.

- fix: **`DependencyFanout` no longer silently DROPS a dependency it cannot resolve.**
  The fingerprint was built with `List.choose graph.GetCanonicalDllPath`, so a
  referenced project whose DLL path would not resolve was dropped. A dropped project
  contributes nothing to the fingerprint, so the fingerprint never moves when that
  project changes — so a dependency bump inside it **never fans out and its tests are
  never selected**. That is under-selection: the exact failure `DependencyFanout` exists
  to prevent, reborn inside it. `ContentHash` names it as unsafe answer #1: *"SKIP the
  file — the hash matches, and the claim silently covers a file nobody looked at."*
  An unresolvable DLL now hashes to `ContentHash.UnhashableContent`, which can never
  collide with a real digest.
- fix: `DependencyFanout` now hashes through the shared `ContentHash` hasher. It had its
  own two sentinels (`"missing"`, `"unreadable"`), which made `DaemonIdentity`'s
  *"one value for I-could-not-read-it, repo-wide"* claim false as written. It also
  base64-encoded each referenced DLL and hashed the resulting TEXT (~3.7× the DLL size
  held transiently, per referenced DLL, on every `BuildCompleted`); `ContentHash.ofFile`
  streams. Note the fingerprint VALUES change, so the first build after upgrading sees
  every test project's fingerprint move and fans out once — over-selection, which is the
  safe direction, and it settles on the next build.

- feat!: **`fshw gate` is now `fshw confirm`.** (AUTOMATION-160) **Migration: `fshw gate`
  → `fshw confirm`.** The old verb is removed, not aliased. `gate` named what the verb
  *blocks*, so it got built as a bouncer; its real job is to run the FULL suite and
  confirm that `check` told the truth — and any disagreement between the two is a bug in
  one of them (a test `confirm` fails but `check` never selected means the **selector**
  missed it; a test `confirm` passes but `check` calls red means a stale red, a flake, or
  a test that only passes with company).

  Inside this plugin the scope concept is now named for **what it is** rather than for the
  verb that requests it — TestPrune has no business knowing which CLI verb asked:
  `gateScopeHash` → `fullSuiteScopeHash`, and the §2a cache-key entry `gate-scope` →
  `full-suite-scope`.
  - **Cache note:** the renamed key entry changes the §2a cache key for full-suite runs, so
    the first `confirm` after upgrading re-runs instead of replaying a cached entry. A
    one-time miss; impact-filtered (`check`) keys are unchanged.

- fix!: **an unreadable needs-testing ledger is not an empty one.** (AUTOMATION-150)
  `PendingVerification.load` answered every failure with `with _ -> empty`, so a corrupt,
  truncated or unreadable sidecar silently absorbed the ENTIRE outstanding test debt: the
  value it returned was indistinguishable from the one a genuinely-clean queue returns,
  the drain gate read it as "nothing owed", ZERO tests ran, and the plugin went green. The
  module was breaking the invariant its own header states — *"the queue may only err
  toward OVER-testing, never under-testing"* — since an unreadable ledger causing no tests
  to run is precisely under-testing.

  `load` now returns `LoadedQueue = Loaded of Queue | Unreadable of reason`, so the two
  facts are different VALUES and the compiler makes every caller decide which it holds
  (the same move as `ProcessOutput.DrainTimedOut`). An `Unreadable` ledger WIDENS the run
  — every configured test project, in full, because a selection made without the record of
  what is owed cannot be trusted — refuses task-cache participation entirely (or a cached
  green would replay straight over the unknown debt), and says so loudly in the log rather
  than recovering in silence. The debt is discharged only by a run that executed every
  runnable project unfiltered and passed; the corrupt file is deliberately left on disk
  until then, so a crash mid-recovery leaves the next session the same honest "unknown"
  instead of a clean, empty, wrong ledger.

  A **missing** file remains `Loaded empty`: "the file does not exist" (first run, fresh
  clone, nothing ever queued) and "the file exists and I could not read it" are different
  facts, and only the second is an unknown — so fresh clones keep their fast no-op instead
  of wedging into a permanent full suite. A non-string or `null` entry now makes the whole
  ledger unreadable too: the old `Seq.choose` dropped such entries, absorbing that symbol's
  debt one element at a time.

- fix!: **a run may clear only what it COVERED.** (AUTOMATION-125) A full run failed
  project X; a queued impact-filtered re-run then executed a *narrower* selection,
  passed, and — via `ClearAllErrors` + last-cycle-wins — superseded X's red. X never
  re-ran and never passed, yet `check` went green. Same disease as AUTOMATION-95/99/112:
  "no failures reported by THIS run" read as "no failures". `confirm` was protected
  (a filtered green is `UnearnedScope`); the inner loop was not, so a developer or agent
  saw red → made an unrelated edit → saw green and concluded they had fixed it.

  A run now carries the SELECTION it was launched against (`TestRunLaunch.Selection`),
  a completed run's `RunCoverage` is derived from that selection intersected with what
  actually executed, and the ledger is rewritten from the OUTSTANDING set — this run's
  failures plus every earlier red it did not cover. "Clear everything" is not something
  a filtered run can express. A red therefore survives every run that did not execute
  it, and dies the moment one that did executes it green (`dotnet fshw test-rerun` runs
  every project unfiltered and clears anything that is genuinely fixed — no stuck-red).
  Precise beyond project granularity: a class-filtered green clears only the classes it
  ran, and a timed-out / errored / deferred project needs a WHOLE-project pass. While a
  red is outstanding the plugin does not participate in the task cache at all — no
  replay of a cached green over a failing test, no write of a carried red to disk.
  `run-tests --only-failed` now re-runs the outstanding set, not merely the last run's
  results (after a filtered run the failing project isn't in them at all).

  `RunCoverage` is PUBLIC (`ofRun`, `covers`, `coveredProjects`, `coversWholeSuite`) and
  the last run's coverage is carried in state (`LastCoverage`) beside `LastResults`:
  results say what a run FOUND, coverage says what it COVERED, and a verdict writer must
  read the second. One notion of scope in the system — the same one the ledger clears
  by — rather than a parallel one that can drift from it.

- fix: an unanalysable-file warning (AUTOMATION-113) no longer disappears at the first
  test run after the analysis failure. The `TestsFinished` ledger rewrite cleared this
  plugin's whole slice, so the file went on forcing full-suite runs while nothing told
  anyone — and the warning that is supposed to deny `check` its green verdict quietly
  stopped doing so. The warning is now re-reported from state on every rewrite and
  leaves the ledger only when the file analyses cleanly.

- fix!: **the freshness gate judged a test project against the whole repo — so it
  condemned projects no build could absolve, and missed the fixtures it should have
  caught.** (AUTOMATION-122) `apphostStale` compared the test project's DLL against
  the newest source mtime found ANYWHERE IN THE REPO. Two failures fell out of that
  one comparison:

  - **It cried wolf.** An edit to any project condemned every project outside that
    edit's dependency closure — and the accusation could not be answered: an
    incremental `dotnet build` is correctly a no-op for an unaffected project, so its
    DLL never caught up with the repo-wide watermark. The only escape was
    `dotnet build -t:Rebuild` — a relink forced purely to move a timestamp. (Observed:
    a change touching only `Intelligence.Build.Dev` wedged `Intelligence.Tests.Integration`.)
  - **It let a red main through.** It looked at `.fs`/`.cs` only, so a changed test
    FIXTURE copied in from a shared project was invisible: the run read the OLD copy
    still sitting in `bin/` and PASSED (intelligence, `dsa-scope-4.json` — a fake green
    that merged and left main red for hours).

  Freshness is now decided over the test project's **own transitive `ProjectReference`
  closure**, in terms of the only two things a build does to an output tree: it
  COMPILES each project's sources into that project's own assembly
  (`AssemblyOlderThanSource`), and it COPIES files — dependency assemblies and
  content/fixture items, transitively — into the test project's output dir
  (`CopyOlderThanOrigin`). Both are exactly what a plain `dotnet build` fixes, and
  nothing else is asserted: a file outside the closure cannot make the gate fire, and
  a file the build never copies has no destination to be compared against, so the
  content check cannot become a new wolf-cry. Every verdict now names the offending
  file pair instead of "older than newest source". The real hole stays closed — a
  source in the project's own closure newer than the assembly built from it still
  blocks the `--no-build` run.

  `newestSourceMtime`/`apphostStale` are replaced by the `ArtifactFreshness` module.

- feat!: **CTRF reports are RETAINED, and the dead `.log` format is deleted.** (AUTOMATION-129)
  Every report used to be `File.Delete`d the instant its per-test records had been folded
  into the flakiness history — so the reports an operator found in `.fshw/test-runs/` were
  the ones whose deletion had FAILED: orphans, months old, indistinguishable from a
  current run's evidence. `.fshw/verdict.json` now POINTS at these reports, and a pointer
  into a directory of accidental survivors is worse than no pointer at all. The newest 5
  per project are kept (`Ctrf.tidyRunsDir`, swept after each run).
  - The `.fshw/test-runs/<Project>-<ts>.log` raw-output dump is GONE. It was written only
    when something broke, so the newest one dated from the last red run — and anyone
    listing the directory read that date as "when tests last ran". It said 2026-06-30, and
    produced the confident, false conclusion that no test had run in weeks. A stale
    artifact that looks authoritative is worse than none. Nothing is lost: the failing
    tests, with messages and traces, are in the retained CTRF report, and the failure
    report is still logged in full.

- refactor!: `Flakiness.TestReport` is now an abbreviation of `FsHotWatch.Ctrf.Summary`,
  and `tryParseReport` delegates to `Ctrf.trySummary`. One CTRF summary reader for the
  whole solution — the pass/fail verdict, the flakiness history and the verdict file's
  `suites` cannot disagree about what a report says.

- fix!: **a force-run refused the slot is QUEUED, never declined.** (AUTOMATION-99)
  Routing `run-tests` through the mailbox left one hole: if a run was already in flight
  the handler replied `busy` and ran nothing — and the CLI mapped that to exit 0. A
  force-run is owed work, so it now joins `QueuedCommandRuns` and is drained (FIFO) when
  the in-flight run finishes. The command's wait on the reply is BOUNDED by the existing
  `--wait-sec` budget (AUTOMATION-98: bound every seam), and a run that outlives it
  reports `busy` — which the CLI now exits non-zero on.

- fix: the `FileChecked` handler's duplicated unanalysable-file treatment (an
  `analyzeSource` error and a handler fault are the same condition) is one
  `markUnanalysable` helper; the three hand-written `if not (ctx.IsRunning "tests")`
  status guards are gone — the framework enforces that universally now.

- fix!: **a force-run the daemon cannot see is a gate that cannot gate.**
  (AUTOMATION-99) The `run-tests` IPC command executed the suite directly on the
  IPC thread — outside the `RunExclusive "tests"` slot, with no `Running` status
  and no busy accounting. For the whole run the daemon's model read "at rest":
  a concurrent `FileChecked` stamped a terminal status over the live run (the
  observed `✓ test-prune  started: … (no elapsed:)`), and a concurrent
  `fshw check` resolved its verdict wait and exited 0 while the test process was
  literally alive. The command now posts `RunTestsRequested` to the plugin
  mailbox; the handler claims the slot and reports `Running` like every other
  launch site, and the reply carries the results JSON back to the command.
- fix: `FileChecked` idle checks moved to the point of use — the `isIdle`
  snapshot taken at handler entry could go stale across the analysis await while
  a run claimed the slot, stamping `Completed`/`Failed` over the live run.
- fix: any fault inside the `FileChecked` handler (not just an `analyzeSource`
  error) now routes through the unanalysable-file machinery: ledger diagnostic +
  forced full-suite runs until the file analyses cleanly — F10's "never silent"
  guarantee in durable form, without manufacturing a terminal status mid-run.
- fix!: the green verdict is carried BY the `Completed` status (summary +
  measured run duration) per the core `RunVerdict` change; per-file analysis
  stamps state what they analysed.

- fix!: **a file whose symbol analysis failed was silently dropped from the impact
  graph — so a change to it selected NO tests and the gate went green having run
  nothing relevant.** The `FileChecked` error branch logged and `return state`d: the
  file contributed no symbols, the impact graph never saw it, the symbol diff found
  nothing changed in it, and selection was empty. The one trace it left — a `Failed`
  plugin status — was overwritten by the very next file's `Completed`, so in practice
  nothing in the system would ever have told anyone. Now an unanalysable file is
  REMEMBERED (`TestPruneState.UnanalyzableFiles`), and while any file is in that set
  the run falls back to the **coarse selection**: every configured test project, in
  full. Safe over-selection beats silent under-selection — exactly the rule
  `EdgeEmission` follows when a seed names a symbol it can no longer resolve. A
  WARNING naming the file and the reason lands in the error ledger, so `fshw check`
  prints it and (under the default warn-fail policy) refuses a green verdict; and
  because the force-run set is non-empty, the run can no longer terminate through the
  zero-affected skip gate as "0 affected — green, 0 ran". A file leaves the set the
  moment it analyses cleanly, so a healthy tree pays nothing. Paired with the
  TestPrune.Core 6.0.1 root fix, which stops files being refused for a merely
  *informational* parse diagnostic in the first place. (AUTOMATION-113)

- feat!: **`fshw confirm` runs the full suite, and a merge verdict can no longer be
  produced from an impact-filtered run.** Impact filtering is a latency optimization
  for the inner dev loop; a merge decision is a correctness claim, and we had been using
  the first as the second. An impact-filtered green means "your change didn't break
  anything I chose to look at" — not "the suite is green". Two new plugin commands
  make the distinction real rather than remembered: `set-scope` puts the daemon in
  full-suite scope (every project force-run, unfiltered — reusing the force-run
  machinery the dependency-fanout already relies on), and `test-scope` reports what
  the last completed run **actually covered**, which is the evidence the CLI's
  merge-gate verdict is computed from. The requested scope is also folded into the
  §2a cache key, so a gate cannot HIT the entry an earlier filtered run wrote and
  replay its green without a test process ever starting. `classifyRunScope` refuses
  to launder the degenerate case: `TestRunCompleted.RanFullSuite` is vacuously true
  for an empty results map (nothing was filtered because nothing ran), which is
  precisely the shape in which 35 tests sat red on `main`, never selected, gate green
  throughout. (AUTOMATION-112)

- fix: the **flakiness history file only ever grew, and racing writes lost
  records.** `keepN` bounded each test's record list but nothing bounded the set
  of test NAMES, so a renamed/deleted test kept its entry forever (5.5 MB
  observed). Worse, `appendRecords` — a full parse plus a full rewrite of the
  whole file — was called once per test CONFIG from inside `executeTests`, so six
  projects meant six sequential parse+rewrite cycles per run; and because those
  configs run under `Async.Parallel`, that read-modify-write raced itself and
  could silently drop a project's records. Records are now collected across all
  projects and written ONCE per run (the shape coverage collection already used),
  and a test whose newest run is older than `Flakiness.DefaultHistoryRetention`
  (30 days) is expired.
- perf: the `dependsOn` cache-key hash is no longer computed EAGERLY on every
  event. It sat above the `match`, so `FileChecked` — which never splices it, and
  which is ONE EVENT PER FILE, not per batch — paid for a full-repo `SafeWalk`
  plus a SHA256 of every matched file, and threw the result away. With one glob
  configured, a cold scan of N files did N full-repo walks. The cache-key builder
  (`cacheKeyFor`) now takes its state as thunks, so an arm cannot pay for an input
  it does not name.
- perf: test-runner spawns now go through the single `ProcessHelper.runProcess`
  with `ProcessBounds.streaming` (behaviour unchanged — a test runner's first byte
  still proves liveness, and the launch deadline still bounds a spawn that goes
  nowhere).

- fix!: **the apphost-freshness gate no longer hangs the daemon forever.** Its
  repo-wide newest-source scan (`newestSourceMtime`) walked with a hand-rolled
  recursion that FOLLOWED SYMLINKED DIRECTORIES. On a devenv/nix repo it
  descended `.devenv/profile` into `/nix/store` and hit two self-loop symlinks
  in one directory (`ncurses-6.6-dev/include/{ncurses,ncursesw} -> .`), which
  branches into ~2^32 paths — non-terminating in practice. The plugin went
  in-flight and NEVER completed (observed 8h36m), spawning zero test processes,
  so the launch-liveness watchdog (which guards a spawned child) never engaged:
  the wedge was UPSTREAM of process launch. The scan now runs through
  `SafeWalk` (no symlinked-dir descent, depth-capped) and `.devenv`/`.direnv`
  are excluded by name.
- fix: the `dependsOn` glob resolver had the same defect — a repo-root-rooted
  `SearchOption.AllDirectories` over EVERY file — and is now on `SafeWalk` too.
- perf: the freshness scan runs at most ONCE per test run, shared lazily across
  configs. It previously re-walked the entire repo once PER test project (6x on
  a 6-project run), and evaluated even when no built DLL existed to compare
  against. `apphostStale` now short-circuits on "no DLLs" before forcing the
  scan.
- feat: the freshness scan logs its duration (`freshness scan of <root>: newest
  source mtime in Nms`), so a pathological walk is visible in the daemon log
  instead of being an unattributed silence.

## 0.13.0-alpha.2 - 2026-07-11

- chore(deps): TestPrune.Core 5.0.0 — function-scoped route attribution
  (`RouteHandlerEntry.HandlerFunction`, `route_handlers.handler_function`).

## 0.13.0-alpha.1 - 2026-07-06

- fix: a cycle whose changed/queued symbols **all have no covering test** now
  resolves as a clean "nothing to verify" green (0 ran) IMMEDIATELY, instead of
  falling through to the cold-start full-suite run. The zero-affected skip
  previously required a session baseline (`hasCachedResults`); on a cold daemon
  with no baseline, an all-uncovered cycle (every symbol dropped as
  no-covering-test → empty affected set) instead ran the full suite — and on a
  loaded box that run could wedge in `executeTests`, streaming "Waiting for
  plugins: test-prune" for hours with `WaitForComplete` never resolving (observed
  5 h+). The launch-liveness watchdog (0.12.0-alpha.1) can't catch this: no
  testhost is *expected* (nothing to run), so "no child" is the correct steady
  state, not a stall. `flushAndQueryAffected` now records
  `ChangedSymbolsAllUncovered` when it drops every symbol as uncovered, and the
  run-trigger treats that as a definitive green — sound WITHOUT a baseline
  because no test covers any changed symbol, so a run would verify nothing about
  them. A genuine cold start with NO pending symbols leaves the flag false, so
  the full-suite baseline still runs. Same terminal green as "tests exist and all
  passed", differing only in that zero ran. (AUTOMATION-65 QA finding: the
  empty-affected-set completion path)

## 0.12.0-alpha.1 - 2026-07-05

- fix: a test child that **never becomes a live process** no longer wedges the
  plugin at `Running` forever. `executeTests` launched each config through
  `runProcessWithTimeout` with an INFINITE timeout (configs rarely set
  `TimeoutSec`), so after `executeTests starting…` / `beforeRun complete` the
  actual child launch could go nowhere — an overloaded box where the child never
  appears, or a machine sleep that kills it mid-launch — and the wait blocked
  indefinitely. No fault was raised, so neither AUTOMATION-65's faulted-run
  Failed path nor AUTOMATION-68's beforeRun fix applied, and `check`'s
  `WaitForComplete` streamed "Waiting for plugins" for hours (observed 33 min –
  16 h). Launches now go through `runProcessWithLaunchWatchdog` with a bounded
  launch-liveness deadline (5 min default, `FSHW_LAUNCH_DEADLINE_SEC` override).
  A slow-but-progressing suite that streams output is untouched — the deadline
  governs launch, not total duration. A **stall** (no life within the deadline)
  kills the tree and raises `LaunchStalledException`, which `executeTests`
  enriches with the config name and elapsed and the impact / `run-tests` catches
  turn into the SAME `Aborted` lifecycle a `beforeRun` throw does (AUTOMATION-68
  seam) → `PluginStatus.Failed`, so `check` reads non-green with a legible
  "re-run when quiet" diagnostic. A child that EXITS — including a sleep-killed
  one — is no longer a wedge either: the poll observes its exit and it is
  classified normally (a nonzero exit with no output → `TestsErrored`, honest and
  re-runnable), never forced to abort (which would misclassify every genuine
  no-output test failure). (AUTOMATION-65 QA finding: the launch gap)

## 0.11.0-alpha.1 - 2026-07-03

- feat: the manual `run-tests` command's slot-wait budget (how long it waits for
  a prior in-flight run to release the `tests` `RunExclusive` slot before
  reporting `busy`) is now configurable via the CLI's `--wait-sec` (payload
  `waitSec`, seconds) and defaults to **600 s**, up from a hardcoded 120 s. A
  background run with a long `tests.beforeRun` chain (90 s+) no longer makes an
  explicit `test-rerun` give up before the slot frees. Absent/malformed
  `waitSec` falls back to the default. (AUTOMATION-66; `parseRunTestsWaitMs` /
  `DefaultRunTestsWaitMs`)

## 0.10.0-alpha.1 - 2026-07-03

- fix: a failing `beforeRun` preflight in the manual `run-tests` command now
  surfaces as a **non-green** verdict. The command ran `executeTests` inside a
  try/with that, on a `beforeRun` throw, returned a command-level JSON error and
  posted **nothing** back — leaving the plugin at its prior (possibly green)
  status, so a concurrent `fshw check` read the daemon aggregate
  (`anyPluginFailed`) as clean and exited **0** even though the preflight-guarded
  suite NEVER RAN. It now posts the SAME `Aborted` lifecycle the impact path
  (`runTestsWithImpact`) builds, driving the plugin to `Failed` with the hook's
  output surfaced, so `check` reads non-green. The two Aborted-lifecycle
  constructions are unified in one helper so they can't drift. (AUTOMATION-68)

## 0.9.0-alpha.1 - 2026-07-03

- fix: a **seeded** `test-impact.db` (copied into a fresh workspace per ADR-010)
  no longer silently under-selects. The fshw-owned freshness sidecar
  (`file-freshness.json`) doesn't travel with the copied DB, so every seeded
  file had no sidecar record and the `detectChanges` call site treated
  "no record" the same as "poisoned" — it BYPASSED the diff and a real edit
  against a seeded row set detected zero changed symbols → zero affected tests →
  a vacuous green gate. `FileFreshness` now classifies stored rows three ways
  (`Clean` / `Dirty` / `Unknown`): an ABSENT record (`Unknown`) over a non-empty
  stored row set is a seeded DB and IS diffed — restoring ADR-010's "a seeded DB
  over-indexes but never serves a stale verdict" guarantee — while an explicit
  `fcsClean=false` record (`Dirty`, possibly-partial cold-scan rows) stays
  bypassed to avoid the phantom "all symbols changed" delta. A genuinely empty
  DB (real cold scan) still falls through to no-diff so it doesn't select the
  whole suite. (AUTOMATION-67)

## 0.8.0-alpha.1 - 2026-07-03

- fix: a transient DB error while resolving covering projects (`QueryAffectedTests` per-symbol lookups) now yields the honest re-runnable `Aborted` lifecycle instead of escaping as a raw framework fault that stranded the run before any test process launched (AUTOMATION-65).

## 0.7.0-alpha.30 - 2026-06-30

- fix: the test gate now defers on artifact **freshness**, not just presence. A
  test project whose compiled assembly EXISTS but predates the newest source is
  no longer run with `dotnet run --no-build` — which would execute stale bits and
  report a pass/fail that doesn't match the sources (the false-green this
  prevents). It is deferred as "waiting on build", exactly the signal a missing
  apphost already produced, so a stale binary can never yield a passing verdict.
  The previous apphost check fired only on a FAILED launch, so a stale artifact
  that exited 0 sailed through as a pass. Runs pre-launch on the canonical
  `<assemblyName>.dll` mtime; mirrors `BuildPlugin.verifyArtifactsFresh` (ADR-008).

## 0.7.0-alpha.29 - 2026-06-24

- chore(deps): pin `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 (clears NU1903 / GHSA-2m69-gcr7-jv3q, High).

## 0.7.0-alpha.28 - 2026-06-19

- fix: the test gate no longer reports a false failure when a test host exits
  non-zero during a dirty shutdown (e.g. the Microsoft.Testing.Platform exit-7
  flake) after writing a clean report. The pass/fail verdict is now derived from
  the CTRF report's summary counts and is authoritative over the process exit
  code (which is only a tie-break when no report exists): a non-zero exit with a
  clean, complete report is GREEN. Previously such a run was reported as "Tests
  failed in <project>" with zero named tests while a re-run came back green.
- feat: a new `TestsErrored` verdict for a run that aborted before producing any
  parseable report (non-zero exit, no results). Distinct from a test failure
  (nothing was shown to fail) and from a pass (nothing was verified) — surfaced
  as an honest "errored — re-run" diagnostic, never green, and never cached.
  `--only-failed` re-runs it.
- feat: a per-project `reportVerificationFormat` setting (`.fshw.json`: `auto` |
  `ctrf` | `off`) scoping CTRF report injection. `auto` (default) injects
  `--report-ctrf` only for a runner detected as xUnit.v3 (an unsupported
  `--report-*` flag is fatal), else falls back to the dotnet heuristic; `off`
  keeps the exit code authoritative for custom runners.
- fix: per-test flakiness tracking now records runs — the CTRF parser read a
  top-level `tests` array, but real Microsoft.Testing.Platform / xUnit.v3 reports
  nest it under `results.tests`, so it had been silently recording nothing.

## 0.7.0-alpha.27 - 2026-06-17

- docs: fix a stale `fshw test` reference (now `fshw check`), document missing config/`create` fields, and add an early-alpha status note in the README.

## 0.7.0-alpha.26 - 2026-06-16

- feat: daemon re-runs dependent tests on a dependency-fingerprint change (new
  `PluginCtx` project-graph accessor), closing the zero-affected skip for
  dependency-only changes. On each `BuildCompleted`, every test project is
  fingerprinted from the content hashes of its referenced projects' compiled
  DLLs plus its own direct `PackageReference` versions (`DependencyFanout`); a
  project whose fingerprint moved since the last build is force-run in full,
  unioned with the symbol-precise selection. A NuGet/PackageReference bump that
  changes binary behaviour without touching an F# symbol (e.g. CommandTree
  0.6.3 → 0.7.0) now re-runs the dependent tests instead of being skipped.
  Bundles TestPrune.Core's `ProjectFanout`.
- fix: a failing test in a daemon run now has its NAME surfaced in the console
  output, not just the on-disk `.fshw/test-runs` log (which CI discards). The
  failure-summary matcher checked for a `failed ` prefix without trimming, so any
  capture path that indented the line (varies by MTP version) was missed —
  producing the contradictory `0 test(s) failed:` alongside `failed: 1` with no
  test name. Now matched on the trimmed prefix (covering `failed (canceled) …`
  timeout-cancellations, the documented under-load flake class), with a backstop
  that dumps the output tail when a run fails but no per-test line parses — so a
  failure is never swallowed into "0 test(s) failed".

## 0.7.0-alpha.25 - 2026-06-16

- fix: a directly-edited test now re-selects itself. Bundles TestPrune.Core
  4.2.3, whose `QueryAffectedTests` now includes the changed symbols themselves
  in the affected set — so editing a test's own body re-runs that test instead
  of leaving a prior failure pinned in the needs-verification queue (FsHotWatch
  ISSUE B). Previously a test method, having no incoming edges, selected zero
  tests when it was itself the change.

## 0.7.0-alpha.24 - 2026-06-12

- fix!: the test gate can no longer go green without the tests having actually
  run ("false green"). A durable needs-testing queue
  (`.fshw/test-prune/pending-verification.json`) records every changed symbol
  until a test run that COVERED it completes green. Concretely: runs that abort
  (e.g. a failing `beforeRun` hook) or fail no longer absorb the symbols they
  never verified; an Aborted run reports Failed instead of green; "no affected
  tests — skip" is gated on the persisted queue being empty; zero projects ran
  with a non-empty queue reports "tests did not run" instead of green; a cached
  green `TestRunCompleted` can only replay for a state whose queue is empty;
  and a daemon restart re-flags anything still unverified. Breaking for
  plugin-message consumers: `TestPruneMsg.TestsFinished` now carries the
  launch-time queue snapshot (`TestRunLaunch`).
- chore: the pending-verification sidecar persists once per analysis batch (at
  the flush chokepoint, before the snapshot advance) rather than on every
  FileChecked — same crash-safety direction (over-testing), far fewer disk
  writes.
- chore: bump TestPrune.Core to 4.2.2 and TestPrune.Falco to 2.0.2.

## 0.7.0-alpha.23 - 2026-06-11

- fix: the `run-tests` command (`fshw test-rerun --filter-*`) now reports a
  filtered run that matched ZERO tests DISTINCTLY instead of as a silent pass. A
  zero-match-under-filter project result is tagged with a stable marker; the
  command surfaces a run-level `noTestsMatched` flag and a per-project
  `no-tests-matched` status when every project matched nothing. It also no longer
  bails instantly when a background run holds the test slot — it waits (bounded)
  for that run to finish so the explicit force-rerun always executes, and reports
  a distinct `busy` status only if a run is still in progress after the wait.

- feat: `tests.dependsOn` (repo-root-relative globs) salts the test cache key
  with a content hash of the matched EXTERNAL inputs — DB migrations, generated
  files, schemas — that the symbol-diff merkle can't see. Editing such a file
  (e.g. a migration that changes the test DATABASE schema but no test SOURCE)
  now changes the BuildCompleted cache key → cache miss → genuine test re-run,
  instead of replaying a stale verdict. Empty / absent `dependsOn` keeps the key
  byte-identical to before (existing on-disk caches keep hitting); missing files
  and zero-match globs contribute no salt. `TestPrunePlugin.create` gains a
  trailing `dependsOn: string list` parameter (pass `[]` for no external deps).

## 0.7.0-alpha.22 - 2026-06-09

- fix: a filtered test run (`run-tests` / `test-rerun --filter-class` / `--filter-trait`)
  no longer reports projects that contain no matching test as FAILED. The raw
  passthrough filter is fanned out to every test project; a project with no test
  matching the filter runs zero tests and the runner exits non-zero (Microsoft
  Testing Platform exit code 8, "Zero tests ran"), which was interpreted as a test
  failure — producing bogus aggregates like "5 failed: Analyzers, Build, Database,
  Unit" when only one project actually had the targeted class. Such a zero-match
  filtered run is now classified as passed/skipped (like a template-filtered project
  with no affected classes), contributing no coverage. Detection is structural (the
  canonical exit code) with a text fallback for runners that exit non-zero without
  emitting code 8. Gated on `wasFiltered`, so an UNFILTERED project that runs zero
  tests (empty suite, misconfigured runner) still surfaces as a failure.

## 0.7.0-alpha.21 - 2026-06-08

- fix: a FAILED test verdict is no longer served from the task cache as a current
  result ("green tree read as red"). The cache key for a completed test run is
  derived from changed symbols + commit, which does NOT pin the test OUTCOME — so a
  failing run and a later passing run on the same tree shared a key. Caching the
  failure let the framework replay a stale red on a now-green tree, surviving daemon
  restarts via the on-disk cache. The plugin now returns a `None` cache key for any
  non-passing `TestsFinished`, making failures uncacheable (always re-run on the next
  matching event); fully-passing runs still cache for the green fast-path. The
  BuildCompleted merkle salt is bumped `v1`→`v2` so entries written by the prior
  failure-caching code are orphaned without a manual cache wipe.


## 0.7.0-alpha.20 - 2026-06-07

- chore: bump TestPrune.Core 4.2.0 -> 4.2.1 (picks up the now-public `Database.SchemaVersion`). No behavior change.

## 0.7.0-alpha.19 - 2026-06-05

- fix: when a schema bump recreates the TestPrune DB, the plugin now clears the FCS
  check-cache so every file re-indexes on the next scan. Previously the recreated DB
  started empty while the on-disk check-cache survived, so cache-hit files were skipped
  and never re-emitted their symbols — leaving the symbol graph (and therefore coverage)
  permanently partial until a manual cache wipe. Keyed on the new `Database.WasRecreated`
  signal from TestPrune.Core 4.2.0.

## 0.7.0-alpha.18 - 2026-06-04

- fix: cold-start coverage no longer clobbers a prior good emission. On the first run
  after a schema bump recreates the TestPrune DB, the daemon is still indexing, so a
  covered file may not have symbols yet and its coverage lines can't be attributed.
  `ingestAndEmitCoverage` now detects an incomplete symbol graph (most lines unmapped)
  and SKIPS the emit rather than overwriting prior coverage with a partial snapshot; the
  DB persists and max-merges, so a later warm run emits in full.

## 0.7.0-alpha.17 - 2026-06-04

- feat: coverage is now stored end-to-end in the TestPrune DB — edit-aware and
  symbol-relative — instead of a blind per-line max-merge. After each test run the
  plugin ingests each project's raw cobertura into the DB (via TestPrune.Core's new
  coverage API) and emits the full DB once to a single shared cobertura, which the
  coverage plugin checks. This eliminates the stale-line accumulation that inflated
  per-file coverage baselines over successive edits. Requires TestPrune.Core 4.1.0.
- refactor: removed the line-keyed `CoverageMerge` parse/merge/emit logic (kept only
  the artifact filename constants); the TestPrune DB is now the single source of truth.

## 0.7.0-alpha.16 - 2026-06-02

- fix: cold-start apphost-missing is no longer a spurious test FAILED — detected structurally (File.Exists on the apphost) and surfaced as "waiting on build" with a one-shot retry.
- fix: `fshw errors` / the aggregate verdict now reflects only the most recent completed test cycle — superseded stale failures are cleared each cycle.
- fix: a partial/aborted test run can no longer lower a coverage baseline.
- chore: bump TestPrune.Core 4.0.2 → 4.0.3 (AST impact analyzer no longer aborts on un-nameable F# symbols such as anonymous-record projections).

## 0.7.0-alpha.15 - 2026-05-28

- chore: bump TestPrune.Core 4.0.1 → 4.0.2 (picks up the backtick-named-test-method shortName fix).

## 0.7.0-alpha.14 - 2026-05-04

- feat: `run-tests` IPC command (invoked by `fshw test`) now routes through the event machinery, emitting `TestRunStarted` → `TestRunCompleted`. Plugins subscribed to `TestRunCompleted` (e.g. `CoveragePlugin`) now observe manually-triggered runs identically to daemon-triggered ones. No API change.

## 0.7.0-alpha.13 - 2026-05-04

- fix: daemon restart with no source edits no longer spuriously re-runs tests — `detectChanges` now filters extern symbols from both sides internally (TestPrune.Core ≥ 4.0.1), eliminating phantom symbol diffs that caused every warm restart to invalidate the full test suite
- refactor: remove redundant `currentForFile` pre-filter from `TestPrunePlugin`; `detectChanges` handles extern filtering internally

## 0.7.0-alpha.12 - 2026-04-29

### Changed

- **BREAKING: TestPrune no longer second-guesses build success.** `BuildSucceeded` is now treated as a contract: artifacts are guaranteed fresh by BuildPlugin's post-build verification. TestPrune dispatches every project on `BuildSucceeded` and drops all skip-on-stale logic. With the dirty-bit handoff gone, `create` no longer takes `dirtyTracker` or `stalenessCheck` — drop the 8th and 9th positional arguments.
- **BREAKING:** `create` no longer takes `getCommitId`. The parameter was unused under §2a's content-merkle keys; removed.

### Removed

- `isStaleProject` / `staleBinaryEntry` and the skip-on-stale code path.
- Stale-binary warning re-emit block in the `TestsFinished` handler.
- `adaptiveTimeout` helper and the `lastSuccessfulElapsed` map (only meaningful for stale-manual recovery, which no longer exists).
- Manual-run-tests deadlock workaround (no skip → no deadlock).

### Added

- **Per-project elapsed time** is now captured on every test run and round-tripped through `FileTaskCache`. Surfaced via the new `TestResult.elapsed` accessor and the `elapsedMs` field on `test-results` JSON output (per-project entry). When 2+ projects run, the run summary now also names the slowest (`"3 passed, 0 failed in 3 projects (selected: no, slowest: ProjA 1.2s)"`) so a bottlenecked project is visible from the plugin status line without querying JSON.
- **Per-test flakiness tracking.** New `FsHotWatch.TestPrune.Flakiness` module captures individual test pass/fail/duration records from CTRF reports emitted by Microsoft Testing Platform runners (xUnit v3, etc.). Per-run history is persisted to `.fshw/test-history.json` (capped at 20 runs per test). The new `flaky-tests` IPC command returns the top-K tests by flakiness score, computed as `transitions / (n - 1)` over the recent history with skipped runs filtered out. CTRF generation is opt-in via the `dotnet`-vs-non-dotnet command discriminator — non-MTP test runners (echo/sleep stubs in unit tests) are unaffected.

### Fixed

- **Cold-start cache bypass.** TestPrunePlugin's `BuildCompleted` cache key now returns `None` until the first `TestsFinished` in the daemon session, so a stale on-disk cache entry from a prior session can't pre-empt the cold-start full-suite run. Mutable plugin-level refs use `Volatile.Read`/`Volatile.Write` for thread safety.

## 0.7.0-alpha.11 - 2026-04-26

### Fixed

- **`RerunQueued` no longer drops the previous run's outcome from history.** The branch that kicks off a queued rerun now records the just-finished run's terminal Completed/Failed status before starting the rerun, so both runs appear in plugin history.

## 0.7.0-alpha.10 - 2026-04-25

### Changed

- **Timeout outcomes are now structural.** Per-project timeouts produce
  `TestResult.TestsTimedOut(output, after, wasFiltered)` instead of a regular
  `TestsFailed` whose output happens to start with `"timed out after Ns"`.
  Plugin's run-completion logic (terminal status, `onlyFailed` re-run filter,
  failed-projects list) now matches the variant directly. The `formatTestResultsJson`
  command surfaces a `"timed-out"` status.
- `runProcessWithTimeout` is consumed via the new `ProcessOutcome` DU; the
  string-prefix heuristic is gone.
- Emit a `"primary"` subtask label that differentiates filtered vs full suite
  runs (`running N selected test projects` vs `running full suite (N projects)`).
  Terminal summary is now `P passed, F failed in N projects (selected: yes|no)`,
  leveraging the existing `TestResult.WasFiltered` flag.

### Added
- `RanFullSuite: bool` field on the `TestRunCompleted` event — `true` iff
  every project in the run executed without an impact filter. Derived from
  per-project `TestResult.WasFiltered`; downstream consumers (e.g.
  FileCommand's `afterTests`) use it to gate baseline-affecting actions.
- **Partial-run coverage merging.** TestPrune now emits coverlet's native JSON
  format (not Cobertura) per test project. Full runs write
  `coverage.baseline.json`; impact-filtered runs write
  `coverage.partial.json` and then merge it with the baseline (per-line max) to
  produce `coverage.cobertura.xml` for downstream gating (e.g. `coverageratchet`).
  Partial runs without a baseline skip cobertura emission entirely (bootstrap);
  run a full test once to establish the baseline.
- `TestResult.WasFiltered`: per-project boolean on `TestsPassed`/`TestsFailed`
  indicating whether impact analysis reduced the run for that project.
  Downstream consumers can distinguish full vs partial results without
  inspecting the command args.
- `fs-hot-watch coverage refresh-baseline` CLI command: deletes
  `coverage.baseline.json` and `coverage.partial.json` for every configured
  test project so the next full run rebuilds coverage from scratch.

### Caveat
- Coverlet's merge keys by file path + line number, not by content hash. File
  edits between baseline and partial may misattribute hits at the line level;
  coverage % stays correct. Revisit with per-test attribution if that noise
  becomes an issue.

### Breaking
- `TestPrunePlugin.create`'s `coverageArgs: (string -> string) option` is
  replaced by `coveragePaths: (string -> CoveragePaths option) option` — the
  caller supplies per-project baseline/partial/cobertura file paths and
  TestPrune composes the coverlet args + merge step internally.
- `TestResult.TestsPassed` and `TestResult.TestsFailed` each gain a
  `wasFiltered: bool` second field. Consumers pattern-matching on
  `TestsPassed output` must update to `TestsPassed(output, _)`.

## 0.7.0-alpha.9 - 2026-04-23

### Changed
- **BREAKING:** The `TestCompleted` event is replaced by a three-event lifecycle (see FsHotWatch CHANGELOG): `TestRunStarted` → `TestProgress` × N → `TestRunCompleted`. TestPrune emits `TestRunStarted` once at the top of `executeTests`, a `TestProgress` per group as it completes (with `NewResults` as a delta keyed by `RunId`), and `TestRunCompleted` once at the end (with the full cumulative `Results` and a `TestRunOutcome`). Cache replay goes through the same path — cached runs replay all three events with a fresh `RunId` so downstream dedup still works.
- Motivation: before this change, a single slow or hanging group (e.g. integration tests) forever-blocked every `TestCompleted`-triggered downstream (coverage ratcheting, `fileCommands afterTests`, etc.) even though the groups the downstream actually depended on had completed long ago. The new lifecycle lets subscribers fire as soon as their required projects have completed without waiting for the rest of the run.
- Abort path now emits `TestRunStarted` + `TestRunCompleted(Aborted reason)` instead of just a dummy `TestCompleted`, so subscribers see a coherent end to the run.

- chore: bump upstream tool versions

## 0.7.0-alpha.8 (2026-04-22)

### Changed

- **BREAKING**: Bump `TestPrune.Core` 2.0.0 → 3.0.2. Adopts the revised
  `ITestPruneExtension` interface: extensions now implement `AnalyzeEdges`
  (returning `Dependency list` to inject into the graph) rather than
  `FindAffectedTests`. Extension-contributed edges are written to the DB
  via `RebuildProjects` before `QueryAffectedTests` so impact traversal
  unifies AST-based and extension-based dependencies in a single pass.
  3.0.2 also closes the pre-versioning stale-DB hole (`openCheckedConnection`
  now recreates any DB where `user_version` reads 0 *and* user tables
  already exist), so combined with the plugin-side stuck-state fix below
  the schema-drift hang is prevented at both layers.
- `AnalysisResult` construction now passes `Attributes` through from the
  analyzer (new field in `TestPrune.Core` schema v3).

### Fixed

- **Stuck-state bug**: the synchronous `flushAndQueryAffected` call sites in
  `BuildCompleted` and `TestsFinished (RerunQueued)` ran outside the async
  try/with and had no net, so a DB hiccup would leave the plugin permanently
  pinned in `Running` with no work dispatched. Both branches now wrap the
  flush in a try/with that reports `PluginStatus.Failed`, transitions back
  to `TestsIdle`, and leaves the plugin responsive to the next event.
- **Schema-drift self-heal**: when a flush fails with SQLite "no such column"
  / "no column named" (stale cache DB from a previous `TestPrune.Core` schema
  version), the plugin deletes the offending DB file and logs a warning. The
  next run rebuilds from scratch — the cache is derivative and safe to
  regenerate. The caller no longer has to know which file to `rm`.
- `affected-tests` command now updates on every `FileChecked` event
  rather than waiting for the next `BuildCompleted`. Each file check
  re-queries `QueryAffectedTests` against the currently-persisted DB
  state so consumers can observe impact changes incrementally. Fix
  depends on `TestPrune.Core 3.0.0`'s UPSERT row-id preservation and
  post-commit WAL checkpoint.

## 0.5.0-alpha.1 (2026-04-12)

### Changed

- Bump `TestPrune.Core` 1.0.1 → 2.0.0 — adds cross-project extern symbol support via `projectName` parameter in `analyzeSource`
- `buildFilterArgs` changed from private to internal for testability
- Add `InternalsVisibleTo` for FsHotWatch.Tests

### Fixed

- Comment-only source changes no longer add the file to `ChangedFiles` — only genuine AST changes (non-empty `changedNames`) propagate to extension-based tests (e.g. Falco route matching)

---

## 0.3.0-alpha.1 (2026-04-08)

Infrastructure release. No public API changes.

- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1

---

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No API changes.

- Add MIT license; add SourceLink; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)

---

## 0.1.0-alpha.2 (2026-03-28)

- Fix: move `ReportStatus` to `TestsFinished` handler to eliminate race condition

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release.

- Test impact analysis via symbol dependency graph
- Test execution with configurable test project configs
