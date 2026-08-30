# Changelog

All notable changes to FsHotWatch packages are documented here.

## Unreleased

> ### ⚠️ Read this first if you run `fshw` in CI or from a script
>
> **`fshw stop` is not a remedy, and never was.** Months of advice — ours included —
> told people to restart the daemon when a check looked wedged or a cached result
> looked stale. It cannot work: the task cache is `FileTaskCache`, it lives on disk
> under `.fshw/`, and it survives `stop`, a reboot, and a fresh daemon. A restart
> throws away the warm compiler and reinstates the exact same cached answer.
>
> The escape is **`fshw confirm`** — it runs the full suite unfiltered and refuses to
> replay a cached verdict. Every `waiting on build` message now says so in place of the
> old "re-run once the build settles", which was the one instruction that could not
> work. If a stale *build output* is the cause, `dotnet build` is the fix, and a
> timestamp-inverted copy needs `dotnet build --no-incremental`.
>
> **Exit codes: six runs that used to be green can now be red.**
>
> 1. **`fshw gate` is gone — the verb is `fshw confirm`.** Removed, not aliased.
>    (`gate` was introduced unreleased and never appeared in a published package, so
>    there is **no published consumer to migrate**. It still bites anyone tracking
>    `main` or running a local pack.)
> 2. **`fshw check --run-once` can now exit `2` where it previously exited `0`.**
>    Not a re-labelling — `--run-once` never computed a `CheckOutcome` at all before,
>    so it could not report an incomplete scan. It can now, and it does. A CI job that
>    treats "not 0" as failure will start seeing red on trees it used to pass.
> 3. **`fshw test-rerun` can now exit `3` where it previously exited `0`.** A run that
>    executed no tests — the filter matched nothing, or no project was selected at all —
>    printed `✓ Tests passed`. Same shape as the item above: the runs that change colour
>    are exactly the ones that never verified anything.
> 4. **`fshw check` can now exit `3` where it previously exited `0`** — but only when it
>    could not READ what the tests covered (the `test-scope` command threw, the IPC call
>    faulted, the reply was unparseable). A repo with no test projects configured is
>    unaffected and still exits `0`. See below.
> 5. **`fshw test-rerun` can now exit `1` where it previously exited `0`** for a test
>    project KILLED at its timeout. The CLI's failure check matched only the `failed`
>    per-project status, so a `timed-out` project printed `✓ Tests passed` and exited
>    `0` — while the daemon's own terminal status for the very same run was a failure
>    plus a timeout. Only the CLI was wrong.
> 6. **`fshw test-rerun` can now exit `3` where it previously exited `0`** when talking
>    to an OLDER daemon that sends no `coverage` field and the run was a MIXED no-op —
>    e.g. one project matched nothing and another deferred. Not all-zero-match, so the
>    existing guard missed it, and nothing executed.
>
> **One machine-readable surface changed shape without changing an exit code:** a run
> that selected zero test projects now records `warn` rather than `ok` in
> `.fshw/verdict.json`'s `plugins[]`, and agent mode tokens it `warn` so `next:` points
> at `status` instead of `done`. If you branch on `plugins[].state == "ok"`, that run
> stops matching. The exit code was already `3`; only the per-plugin state was lying.
>
> **A whole test run can now be refused before any suite launches.** If a configured
> test project's build output does not match its sources, nothing spawns and every
> project comes back deferred — where previously the fresh projects ran first and the
> refusal surfaced minutes in, as a partial red. The remedy is named in the message.
>
> That refusal now gets its **own** words, and this is the sentence to read: **`waiting
> on build` was two causes wearing one label.** A build artifact that has not been
> produced yet settles on the next build, so "re-run once the build settles" is right for
> it. A build output that is STALE — the artifact exists and its bytes do not match its
> sources — does not settle, and re-running, `fshw confirm` and restarting the daemon all
> spend a full cycle to arrive back at the identical refusal. The stale case now names
> every affected project, quotes the file, and says `dotnet build` (or `dotnet build
> --no-incremental` for a timestamp-inverted copy). **If you match on the `reason` string
> in `.fshw/verdict.json`, that string changes shape for this cause** — the exit code is
> unchanged at 2.
>
> The F# API breaks — `RunVerdict`, `RunClaim`, `CommandCtx`, `ProcessOutput`,
> `KillOutcome`, `CheckInputs`, `CheckOutcome`, `LoadedQueue` — are listed per package
> in [`src/*/CHANGELOG.md`](src/), each marked **BREAKING**. They share one shape: a
> state that used to be a lie is now **unrepresentable**, so the migration is the
> compiler telling you where you were guessing.

- **AUTOMATION-315:** coverage-derived source-file → test-project evidence is
  unioned with TestPrune's AST selection. Project identity survives collection,
  integration suites may opt into impact collection without the consumer
  ratchet, and missing/stale complete evidence widens instead of narrowing.

`check-reach` and the persisted verdict's `checkComparison.conditionalFailureRecall` now
include measured failure recall for full-suite `confirm` evidence:
`reached / total` observed failing tests, with a correctness threshold of 100%. Zero
failures is recorded as not measurable rather than vacuous 100%, and the field is
explicitly limited to observed failures—not general relevance recall. (AUTOMATION-67)
The measurement is accepted only when the `check-reach` receipt names the exact run
graded by the verdict; missing or mismatched run identity is recorded as not measurable.

- fix: package releases now follow project-reference dependency levels and wait
  for each exact version to become restorable from NuGet before tagging a
  dependent lane. The CLI remains last, so it cannot publish references to core
  or plugin versions that NuGet has not made available yet.

### test-prune: a changed message retains its pre-rebuild literal selection edge

An incremental scan used to discover a changed producer symbol, replace that file's
graph, and only then ask which tests depended on the change. For ordinary symbol edges
that order is correct because incoming test edges survive the replacement. A shared
literal's production edge points the other way (`literal -> producer`), so replacing a
message removed the only path from the changed producer to a test still asserting the
old prose. The selector logged one changed symbol, selected zero tests, and dropped the
symbol as uncovered.

The plugin now captures TestPrune.Core's pre-rebuild literal-node seeds before the
destructive graph update and carries those nodes through the existing durable
pending-verification queue. It does not retain stale edges: the rebuilt graph describes
current source, while the old node remains owed only until its covering tests pass.

Prior red tests are also quarantined into the next ordinary impact run. A named failing
class is unioned with the graph-selected classes; a timeout, dead host, or otherwise
unknown class scope promotes the whole project to an unfiltered run. The zero-affected
shortcut cannot skip this debt, and a daemon restart still establishes its existing
full-suite baseline before impact filtering resumes. Failure, launch, and CTRF receipts
now retain the fully qualified class identity; a filtered red retires only when its exact
failing method appears as passed in that run's complete CTRF receipt, never from a
sibling method's aggregate class green.
(AUTOMATION-67)

### core/cli: the tree hash now covers what DECIDES the check — BREAKING (verdict applicability)

**A verdict is content-addressed to the tree it verified, and that tree was never the
whole tree.** Up to `fshw-tree-sha256-v2` the hashed set was a walk of `src/`+`tests/`
plus `.fshw.json` — a list inherited from the *watcher*, whose job is a different one.
Everything that decides an answer from outside those roots was omitted. Lower a coverage
floor, edit an analyzer rule, flip `TreatWarningsAsErrors` off, and the green earned
under the **stronger** check still reported `Applies`. The failure was silent and in the
worst possible direction: weaken a check, and the verdict that was earned before you
weakened it certifies the tree afterwards (AUTOMATION-165).

`fshw-tree-sha256-v3` derives the set from one rule, written down in `VerdictInputs`:

> **A file belongs in the tree hash iff changing it can change what a check concludes.**

- **Tool-known, no configuration needed.** The root-level toolchain and dependency files
  — `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  `global.json`, `nuget.config`, `paket.lock`, `paket.dependencies` — are hashed. So a
  repo that declares nothing still stops reusing a green across a `TreatWarningsAsErrors`
  flip.
- **Declared, for everything fshw cannot know.** A new `.fshw.json` key,
  `verdictInputs.hashed`, names your coverage floors, your analyzer rules, your
  baselines — each with a `why` a reviewer can check. `verdictInputs.notInputs` states
  the opposite decision with a `reason`, so "not hashed" is reviewable rather than an
  omission nobody noticed. See
  [Configuration](README.md#what-decides-the-verdict-is-what-is-hashed).
- **A declaration is never silently skipped.** One that cannot be honoured as written —
  no `path`, no stated reason, a duplicate, a path in both lists, a path outside the repo
  — is a hard config error and the daemon refuses to start. One that matches no file is
  hashed as *absent* under its own entry, so a typo cannot quietly restore the old
  behaviour; the hash moves when the file appears, and `verdict.json` reports
  `treeAbsentDeclarationCount`.
- **`treeHashAlgorithm` is now read, not merely recorded.** A verdict from a different
  hashing scheme is `applies: false` / exit **4**, reported as a different *scheme*
  rather than a different tree — a `v2` hash and a `v3` hash address different file sets,
  so comparing them as strings answers a question nobody asked.

**What this means for you:** every verdict written by an earlier build is now
inapplicable — that is the point, since each of them was earned over a narrower tree.
The first `check`/`confirm` after upgrading runs for real. `verdict.json` gains
`treeDeclaredCount` and `treeAbsentDeclarationCount`, so a repo can see in the artifact
whether its declaration is being honoured rather than merely written down.

### core: a kill that never ANSWERS is its own outcome, and a tree we lost is booked — BREAKING (API)

**Breaking (API):** `KillOutcome` gains a fourth case, `KillTimedOut of budget: TimeSpan`. A
consumer matching it exhaustively will not compile until the case is handled.

An earlier entry below, under "A process tree we failed to kill is no longer reported as killed",
documents `KillOutcome` as `Killed | AlreadyExited | KillFailed of exn`. That was true when it was
written and is no longer the whole type — this entry is the correction, and the reason to read it
before matching on the type.

The new case is not a flavour of `KillFailed`, and collapsing them would lose the distinction that
matters during an incident: `KillFailed` is the operating system telling us **no**, with a reason
it can name. `KillTimedOut` is the operating system telling us **nothing** — the kill call never
returned inside the teardown budget, so there is no success, no refusal, and no answer at all.
Both leave the tree unaccounted for and both fail closed; only one of them has a reason to give.

The budget exists so a teardown cannot hang a run forever. An unbounded wait there is a phase that
cannot finish, and a phase that cannot finish cannot report — so the run would never reach a
verdict at all, which is a worse failure than reporting an unaccounted tree.

**New public surface on `ProcessRegistry`,** so a tree we could not account for is named rather
than silently dropped: the `LeakedTree` record (`Pid`, `Description`, and why termination could not
be established), the `reportLeak` function, and the registry's own `ReportLeak` / `Leaks` members.
The leak list is append-only by design — a tree we could not account for is never un-leaked,
because nothing later observed would prove it died rather than that its pid was reused.

### cli: "full suite" now has to mean every suite in the solution — BREAKING for repos with an undeclared test project

`confirm` reports its scope as `{"kind":"full","ranProjects":N,"totalProjects":N}`, and a
deploy preflight authorises on exactly that field. Both numbers counted `.fshw.json`'s
`tests.projects`, so "full" meant *every suite the config named* — never *every suite in
the solution*. A test project sitting in the solution and in no gated list was not
reported as UNRUN. It was simply absent, and absent is indistinguishable from passing.

This repo was the example. `FsHotWatch.IntegrationTests` — 64 end-to-end tests — is in
`FsHotWatch.slnx`, is a runnable test project, and was in no list `confirm` read. Every
local green this repo has ever produced was `FsHotWatch.Tests` alone, and nothing in the
verdict said so.

**Every config load now reconciles `tests.projects` with the solution at the repo root
and raises a `config error` (exit `2`) when they disagree.** That is a load, not a daemon
boot: the reconciliation is re-done in the CLI process on every `check` / `confirm` /
`start`, so adding a test project to the solution without touching `.fshw.json` fails the
very next command, even against a warm daemon that never reloaded anything. The refusals:

- a solution test project that is neither gated nor excluded;
- an exclusion with no `reason`, or naming a project the solution does not contain;
- a project listed in **both** `tests.projects` and `tests.excluded`;
- a gated project the solution does not contain;
- more than one solution file at the repo root with no `tests.solution` to pick one;
- a scan that recognised nothing at all — the floor, because an empty problem list from a
  blind scan is byte-identical to the one from a fully governed repo.

**`tests.excluded` is the escape hatch, and it is first-class**: an array of
`{"project": …, "reason": …}` where the reason is required and non-blank. A declared,
reasoned exclusion is not the bug — the silence is. Every exclusion is logged on the
green path and recorded in `.fshw/verdict.json` under **`scope.excluded`**, so a consumer
reading `"kind": "full"` can see the gap instead of inferring completeness from a count.
`scope.excluded` is `null` — not `[]` — on a verdict written before the field existed:
"this verdict does not say" and "nothing was excluded" are different facts and are
different bytes.

`tests.solution` names the authority when the repo root holds more than one solution file.
A repo with **no** solution file is not reconciled at all: the full-suite claim is complete
*relative to the solution*, and with no solution there is no such claim to make. A repo
that configures no test projects is untouched — it makes no full-suite claim, and `confirm`
already refuses to build a merge verdict without one.

`FsHotWatch.IntegrationTests` is now a declared exclusion carrying its reason (it stands up
real daemons and asserts on wall-clock behaviour, so running it from inside the daemon that
IS the gate makes its timing assertions a function of machine load). It is not ungated: CI's
solution-wide `dotnet test` and `mise run test-integration` both run it.

*F# API, BREAKING:* `Verdict.create` takes the declared exclusions
(`SolutionScope.Exclusion list option`) between the tree and the outcome, and the `tests`
config record gains `Excluded` and `Solution`. See
[`src/FsHotWatch.Cli/CHANGELOG.md`](src/FsHotWatch.Cli/CHANGELOG.md).


### core/build: MSBuild's own generated `obj/` sources stop reading as edits (AUTOMATION-368)

The artifact-freshness gate asks one question — *did a source change after the build
wrote the DLL?* — and `ProjectGraph.GetMaxSourceMtime` was answering it over MSBuild's
whole compile-item list. That list is not a list of edits. Every SDK project compiles
`obj/<cfg>/<tfm>/<Project>.AssemblyInfo.fs`, every design-time evaluation regenerates
it, and **project discovery is a design-time evaluation** — so each discovery pass
stamped every project's newest "source" strictly after the DLL the last build had just
produced, and a tree nobody had touched read as universally stale.

The gate shipped report-only for exactly this reason, and the window paid for itself.
Read back over ~40 workspaces of a consuming repo (2026-08-18..23) the logs held **2090
stale findings, 91% of them within 90 s of an `MSBuild evaluation` pass in the same
daemon log**. Promoting it to reddening on that reading would have failed essentially
every build in every workspace, on the first discovery after each one.

`GetMaxSourceMtime` now excludes build output. TestPrune's independent
`ArtifactFreshness` never had the bug — it walks under `SafeWalk.SourceExcludedDirs`,
whose own doc comment names this precise trap — so this **is** the
BuildPlugin/TestPrune disagreement the promotion criteria asked about, and the two now
answer from one shared fact (`SafeWalk.BuildOutputDirs`) rather than having been
separately taught it.

`SafeWalk.isBuildOutput` asks that question **relative to the project directory**, never
over the absolute path. Matching segments of the absolute path would classify every file
of a repo checked out under a directory named `bin` or `obj` as build output — nothing
would ever be newer than the DLL, and the gate would answer FRESH forever. Same silence,
arrived at by being more thorough. (`.workspaces/` is the live instance: jj workspaces
are full checkouts nested under an excluded name.)

**The gate is still report-only.** The corrected reading has not run against a real
repository either, and promoting a build-reddening predicate in the change that fixes it
is the mistake the flag exists to prevent. One observation window on the corrected
detector comes first.

Also here, and the reason this was invisible for two releases: every test of this gate
built its fixture through `RegisterFromFsproj`, a path no live daemon takes. The
fixtures now register the way `Daemon.fs` does — `RegisterProject` +
`RegisterProjectOutput`, from what MSBuild reported — and carry the generated `obj/`
compile item a real project has, so the tests fail for the reason production would.


### cli/test-prune: a killed test host is an ABORT, and stops reading as a mass regression

Under CPU load the gate returned large numbers of failures at 0ms. A 0ms failure is a
test that **never ran** — the host was killed and everything it had not reached was
written out in exactly the shape of a test that ran and failed. The gate then reported
`N failed`, exit `1`, `red` in `.fshw/verdict.json`, and a console list of test names.
Every surface asserted a definite negative about code that had not been tested at all,
and each occurrence cost hours of investigation into code that was fine.

The discriminator is the exit code, and only the part of it no runner chooses: .NET on
Unix reports a child terminated by signal N as `128 + N`, so `137` (SIGKILL, the OOM
killer or a reaper) and `134` (SIGABRT, the runtime aborting on an unhandled exception)
mean *the host did not finish*. Microsoft.Testing.Platform's own exit codes are single
digits, so a genuine mass failure can never be mistaken for one — that direction is
asserted as explicitly as the first.

What changed, given that fact:

- A signalled host is classified as an abort **regardless of any CTRF report it flushed
  on the way down**. Reading that partial report as the verdict is what minted the
  phantom regression.
- The abort has its own console report. It never prints the "N test(s) failed" headline,
  it says NOTHING WAS VERIFIED first, and it labels the transcript as a transcript.
- **`fshw check` / `confirm` exit `2`, not `1`**, and `.fshw/verdict.json` records
  `outcome: "incomplete"` with a reason naming the signal and the projects. If you branch
  on `red` to mean "a test broke", a killed host stops matching. A real failure beside an
  abort is still `red` / exit `1`.
- **No retry was added.** An automatic retry cannot tell a host killed by a busy box from
  one that aborts every time because something is genuinely broken, so a loop that
  retried until it got a verdict would turn a real crash into a slow green. The abort is
  reported honestly, once; the reader is the one who can see whether the machine was busy.

### docs: the replacement for `isPassed` is now something you can copy

`TestResult.isPassed` was deleted this cycle and `TestResult.verdict` /
`ProjectVerdict` put in its place. That distinction — `NothingVerified` is neither a
pass nor a failure — was documented on the type and in the changelog, and nowhere a
reader could run. `docs/writing-plugins.md` now has a **Reading a test run's results**
section: the three cases in a table, why there is deliberately no "did not fail" bool
(`not verifiedGreen` is true for a zero-match project too, which is right for a gate and
wrong for a report), and why the run-level question is `Verification` rather than a
`Map.forall` over the per-project bool — that fold is vacuously true for an empty map.

The code in it is sourced from `examples/PluginExample/PluginExample.fs`, which is
compiled by the solution with `TreatWarningsAsErrors`. So the example now exercises
`TestResult.verdict`, all three `ProjectVerdict` cases and `TestResult.verifiedGreen`:
re-shaping any of them breaks the example build before it can rot the docs.

### refactor: three places where two tickets had solved the same problem twice

No behaviour change, no message text change — these are the seams left by four tickets
landing in the same files over one day:

- **build** — `verifyArtifactsFresh` and `artifactCoverageGap` walked the project graph
  separately, each with its own copy of the same three-way freshness rule; the code
  admitted it in a comment. One walk now answers both.
- **cli** — the daemon and `--run-once` paths each carried the same six-arm match over
  `CheckOutcome`, so both AUTOMATION-201 and AUTOMATION-303 had to add their arms twice.
  One `CheckProse.explainOutcome` now serves both.
- **cli** — the "which causes are not about this tree?" filter existed three times, one
  per surface, which is three chances for the count deciding exit 1 vs exit 3 to disagree
  with the causes printed beside it.

### core/test-prune: "ran zero tests" stops being a string on a passing result

Zero-test-ness was not a state in the type system. A filtered run whose filter matched
nothing was stored as a PASS carrying a magic output prefix, and the fact was recoverable
only by re-deriving it with a string comparison. `TestResult.isPassed` answered TRUE for
it — deliberately, because per PROJECT a filter that names no class in the Integration
project is not that project's failure. But that made "we verified nothing" a sub-case of
"we passed" at every fold that reached for the predicate, and each aggregator had to
remember, on its own, to ask a second question. Three did. Two did not, and one of those
could mint a **cacheable** green that replayed on a later build.

Naming the two remaining holes fixed those two. It did not fix the shape, and the shape
produced another: the per-symbol green-commit discharged a changed symbol's test debt
whenever every project covering it "passed" — including a project whose impact-derived
class filter had matched zero tests. The symbol left the pending-verification queue
having been verified by a run that executed nothing. The repo's own analyzer rule could
not see it: the predicate sits behind a `match` inside a lookup lambda, and the rule's
allow-list had that exact shape written down as sanctioned.

`TestResult.isPassed` is now **deleted** (see [`src/FsHotWatch/CHANGELOG.md`](src/FsHotWatch/CHANGELOG.md),
**BREAKING**). In its place `TestResult.verdict` returns a three-case `ProjectVerdict` —
`Verified`, `Refuted`, `NothingVerified` — so a fold cannot sweep "nothing ran" into
either side without writing it down, and the one bool that ships (`verifiedGreen`) is
TRUE for `Verified` alone. Every fold in the tree became a compile error and was decided
in place, with the reason at the call site.

The analyzer rule survives, retargeted and with its own premise struck: it used to open
by asserting that no signature could distinguish the two questions while one predicate
answered both. That was false, and believing it is why the fix was a detector for three
releases.

Also in this area, both found by attacking the fix rather than reading it:

- A test project **killed at its timeout** exited `0` from `fshw test-rerun` (see the
  exit-code callout above).
- The older-daemon fallback greened a **mixed no-op** run — one project matched nothing,
  another never ran — because it only recognised the all-zero-match shape.

`test-rerun` also now prints **total / succeeded / failed for every project it ran**, on
every outcome including the green. Those counts existed only in `daemon.log`, so the one
line that separates a real pass from a vacuous one required going to look for it. A
project that produced no readable report says so rather than printing zeros.

And a `test-rerun` refusal now **names the filter it used and every project it searched**,
rather than a project count and a pointer at `fshw status test-prune`. The three causes of
a zero match — a typo, a renamed class, and a filter aimed at projects that do not contain
it — are indistinguishable without those two facts, and telling them apart from the
message is the whole point of the message.

### build: a cache hit may not assert freshness it has never confirmed

`check` could wedge and stay wedged. The build cache key is a content merkle over
SOURCES, so it is structurally blind to what happened to the OUTPUTS: a file rewritten
to byte-identical content with a new mtime (a formatter's no-op pass), a `bin/` written
by another workspace, or a fresh `jj workspace add` with no `bin/` at all all leave the
key unmoved. The stored `BuildPassed` replayed as `built N projects (cached)` without
running, TestPrune's freshness gate compared mtimes, correctly found the output older
than its source, and deferred every affected project as `waiting on build`. Both sides
were right, neither moved, and because nothing in the loop depended on the previous
attempt, re-running `check` reproduced it verbatim — for as long as you were willing to
keep re-running it.

`BuildPlugin` now runs the same artifact verification the real-build path runs
(`verifyAndDemote`) before it will serve a cached result. Stale or missing outputs
suppress the LOOKUP only — the store still happens, so a recovered build is cached
again immediately and the inner loop keeps its cache. Cache-hit and real-build are now
indistinguishable to downstream plugins, which is what AUTOMATION-224 asked for and
only gave `confirm`.

Cost is a stat per project, ordered BEFORE the merkle, so the bypass path skips the
SHA-256 of every source file — the wedge path is now cheaper than the warm path, not
dearer. No repo is newly condemned to rebuild-every-time: one whose artifacts fail this
predicate after a build was already being demoted to `BuildArtifactsStale`.

Also: `waiting on build` no longer says only "re-run once the build settles" — the one
instruction that could not work. It now names `fshw confirm` as the escape and says
that restarting the daemon is not one (the task cache is on disk and survives `stop`).

**Two follow-ups, and one of them is a warning rather than a fix.**

The gate matched `FileChanged` alone, and so did `force-rebuild` before it. A build
plugin configured with `dependsOn` buffers file changes until its dependencies report
and starts its build from the `CommandCompleted` that satisfies the last one — so for
exactly those repos the lookup that decides whether a build runs was ungated, and
neither `confirm`'s forced rebuild nor the artifact re-verification reached it. Every
event that READS the cache is now gated; only the store arm is exempt.

And the freshness check now says when it examined nothing. `verifyArtifactsFresh`
returns the projects it found stale, so "nothing is stale" and "nothing could be
looked at" are the same value; a graph that stops yielding build outputs switches the
guard off while every run stays green. It now names each project it could not examine
and why, once per plugin instance, on the `build` log channel. **Read that line if it
appears: today a live daemon prints it about every project it has.** A TargetFramework
reaches the graph only via `ProjectGraph.RegisterFromFsproj`, which nothing outside the
test suite calls — the daemon registers projects through `RegisterProject`, which
records no framework — so `GetCanonicalDllPath` answers `None` in production and this
plugin's artifact freshness, replay gate and post-build demotion alike, has never
examined a real artifact. Teaching the graph MSBuild's actual target path is the fix,
and it deserves its own change: it switches on a build-reddening path that has never
run outside tests.

Measured, because the acceptance asked: the gate costs 0.40 ms on this repo's own
graph (12 projects / 135 files, minimum of 50 interleaved lookups) against a merkle
that hashes the same tree in 9.7–11.4 ms — about 4% on a warm lookup, and smaller than
the merkle's own run-to-run spread. A warm no-op check still serves its cache.

### test-prune: stale build output is caught before the suite runs, and repaired

A stale test-output dependency used to surface roughly three minutes into a run.
The freshness gate itself was right — it refuses to run `--no-build` against bytes
that do not match the sources — but its only call site sat inside the per-config
body of the PARALLEL run loop. A project in group A had already run its suite and
written its report before group B's staleness was even looked at, so the result was
a partial-execution red that reads like progress. It was hit three times in one
day, and the third trigger was a plain `jj` merge of `main` into a workspace: no
edit at all, just a working copy rewritten under a live daemon.

The comparison is pure file I/O and takes milliseconds, so it now runs as a
PREFLIGHT over every configured project before anything launches. If anything is
stale, nothing spawns.

It also repairs what is provably repairable. When a dependency assembly or fixture
in a test project's output dir holds bytes that no current build output holds —
MSBuild's incremental copy comparing equal timestamps and skipping the copy — the
preflight writes the origin's bytes across, re-reads them, and lets the run
proceed. Exactly that one case is repaired: a stale COMPILE and an unreadable
project file are refused instead, because there is no file on disk holding the
bytes they need and inventing them is how silent degradation starts.

Every repair is logged by name AND recorded to `.fshw/test-prune/stale-heals.json`,
which drives a circuit breaker: ten repairs of one file inside two days stop being
a repair and become a finding, so the run refuses and names the file and the count
rather than absorbing the inversion forever. The breaker gates the REPAIR, not the
run, so a tripped breaker on a clean tree changes nothing — and its message names
its own reset (delete the ledger; the window also ages out on its own).

Refusals now state a remedy. Every one names `dotnet build`, and the copy case adds
that a timestamp-inverted copy needs `dotnet build --no-incremental`. None of them
says "stop the daemon": the task cache is file-backed and survives a restart, so
that folk remedy never cleared anything by itself.

### cli: a shortened agent summary now says what it dropped

The agent status line is a fixed-width surface and still truncates to 80
characters, but a bare `...` is indistinguishable from prose — a reader seeing
`4 waiting on build (tests did not run): Intelligence.Build.Dev.Tests, Intelli...`
has no way to tell a shortened list from a complete one. Truncation now states the
omitted count instead, and the untruncated detail is in the ledger entry and log
where the remedy also lives.

### cli/test-prune: a run that tested nothing no longer renders as a ✓

`fshw check` on a run that selected ZERO test projects printed
`✓ test-prune — 0 passed, 0 failed in 0 projects` and then, correctly, refused to
certify it (exit 3 — NO VERDICT, "the tests that ran were no tests ran"). The
verdict was sound; the plugin line was not. A reader scanning glyphs saw success,
and only the exit code disagreed.

Such a run now records `NOTHING VERIFIED: 0 test project(s) ran, no test executed`
as its verdict summary, and every surface refuses it a green: compact and verbose
render `⚠` instead of `✓`, agent mode tokens it `warn` (so `next:` points at
`status`, never `done`), and `plugins[]` in `.fshw/verdict.json` records `warn`
instead of `ok`.

The plugin STATUS stays `Completed` on purpose — nothing failed, and reporting a
failure would turn the honest exit 3 (no verdict) into an exit 1 (failures found).
The refusal is asked of `RunVerification`, so it keys on "did anything execute?"
rather than on any one selection bug.

**Where this rule does NOT yet hold, stated rather than implied.** `nothingVerified`
is a marker a plugin *sets*; it is not something the framework can infer. A plugin
that verifies nothing and does not say so still renders a `✓` — and two paths in this
very release are exactly that: format-check's `no files to check` (below) is prose
where this is structure, and test-prune's pure-`waiting on build` terminal reports
`Completed` with a clean ledger. Both are one call to the constructor above, but both
flip a glyph green→warn, so they are being landed with their own regression tests
rather than slipped into a release-prep pass. Making the marker impossible to forget
means moving the fact into `RunVerdict.create`, which is a wire *and* cache-format
change and is deliberately not bundled here.

### format-check: a cached verdict can no longer claim files its key never covered

AUTOMATION-191, the `File = None` half of AUTOMATION-186. Format-check subscribes to
`FileChanged`, which the framework keys as a **whole-run** entry, so its stored verdict
replays **verbatim** — the AUTOMATION-186 derive-from-ledger path only ever reached
per-file entries. Verbatim replay is honest only when the summary is a function of the
key, and the key is a content merkle of *that event's files*. The summary counted
`state.Unformatted`, the whole-session accumulated set.

So an entry minted for one clean file, in a session where a *different* file was
unformatted, stored `1 files need formatting` — and replayed it, unchanged, into a later
session whose ledger was empty and whose verdict was green. Reproduced against the real
plugin: replayed `"1 files need formatting (cached)"` where a cold run over the same
bytes says `"format OK"`.

The summary now states what the run it is keyed on actually checked — `3 of 12 files need
formatting`, `format OK (12 checked)` — so a cache hit says exactly what running it says,
the invariant AUTOMATION-245 stated for the build cache. AUTOMATION-186's scope rule (*a
cache entry may only assert facts derivable from its key's scope*) is enforced rather than
weakened, and no framework, plugin-API or cache-format change was needed. The two
mechanisms the ticket proposed — a general per-plugin "summary is ledger-derived"
capability, and a narrow framework-side special-case — were measured and rejected in
`docs/adr-014-a-plugin-summary-is-scoped-to-its-cache-key.md`.

The whole-session view is unchanged where it stays live: every unformatted file is still an
error-ledger entry, and the `unformatted` command still answers with the accumulated set.
A run that compared nothing now says `no files to check` rather than `format OK`.

### test-prune: the freshness sidecar's verdict over an emptied index is a decision, not a by-product

`.fshw/test-prune/file-freshness.json` carries no schema version and sits beside a
`test-impact.db` that deletes and recreates itself on a `SchemaVersion` bump — the same
shape that let `pending-verification.json` discharge real test debt as a zero-test green
(AUTOMATION-275). Measured against a real recreate rather than a simulated one: the
sidecar survives, still saying `Clean` about rows the index no longer holds.

**No behaviour changed**, because that case was already resolving the right way —
diffing against an empty row set makes every symbol read as added, which *widens* the
run. What was missing was anything saying so. The widening fell out of `detectChanges`'
handling of an empty list, and a refactor that gated `Clean` on stored rows existing (the
way `Unknown` already is) would have flipped it to under-testing with no test failing.

The sidecar's verdict plus one structural fact — does the index still hold rows for this
file — now resolves through `FileFreshness.trustStoredRows` to a named `StoredRowTrust`,
and `EverySymbolIsNew` is the arm a recreate lands on. `Database.WasRecreated` is
deliberately not an input: it is also true for a first-ever creation, so it cannot tell a
schema bump from a fresh clone, and it says nothing about an individual file.

### tests: a watched-dir fixture, and FSHW-WAIT-001 to keep the flaky shape out

Two tests flaked on the same shape: `Thread.Sleep(100)` to "give the watcher a
moment", ONE write, then `Assert.True(signal.Wait(5000))`. On macOS a brand-new
temp directory carries 4-20s of FSEvents cold-start latency, so the write that
mattered could land before the watcher was live and never be reported at all.
The immediate fix was to write repeatedly via `probeLoop`; this makes writing the
bad version impossible instead.

`tests/FsHotWatch.Tests/WatchedDir.fs` splits the lifetime in two, so a path
exists only where an unguarded write is honest:

- `UnwatchedDir` — before anything watches it. `Root` / `PathTo` / `Seed`.
- `WatchedDir` — while it IS watched. **No path is exposed.** The only mutation
  is `WriteUntil(relative, contents, observed)`, which writes repeatedly and
  returns whether the event was actually seen.

"Write once and hope" and "wait, then assert something else" are both
unwriteable now, rather than merely discouraged.

`FSHW-WAIT-001` (a fourth house analyzer, beside CLAIM/CLOCK/VERDICT) flags, in
test sources only, a `Thread.Sleep` followed by a fixed-budget event wait inside
an assertion. A deliberate sleep — asserting a NEGATIVE within a window — opts
out with a greppable `// FSHW-WAIT-001 ok: <reason>`; two exist today.

Both narrowings were measured rather than assumed: without the
"inside an assertion" clause the rule fired 13 times repo-wide, nearly all
sanctioned (teardown waits, and bounded-response assertions whose budget IS the
claim). A proposed extension to also flag the unit-returning `waitUntil` was
measured too — 3 fires, every one a deliberate teardown drain after the real
assertions, zero true positives — and declined, with the measurement and a
reopen condition recorded on the rule rather than shipping something that cries
wolf.

### A scope `check` could not read stops reporting a pass

`check` and `confirm` both refuse `NoTestsRun` — "the daemon holds no test evidence at
all" may never be a green. But that refusal was only ever *reached* when the read that
produces it succeeded. Every way of failing to read the scope — a `test-scope` command
that threw, a faulted IPC call, a reply that was not JSON, a daemon contradicting its own
project counts — collapsed into the same value as "this repo has no test projects
configured", and the inner loop deliberately tolerates *that*. So a fault on the read
path turned an exit `3` into an exit `0` on an unchanged daemon state.

The two are now different values. "There is no scope to report" (no `test-scope` command;
a run still in flight) stays tolerated by `check`. "I asked and could not find out" is its
own case, carries the reason, and is refused in both modes — a read that faulted cannot
rule out the `NoTestsRun` it may be concealing. This is `PendingVerification`'s rule
(AUTOMATION-150) one layer up: **a ledger you could not read is not an empty ledger.**

### A run that executed nothing stops reporting a pass

Three changes with one shape: **the absence of a complaint was being read as evidence.**

`test-rerun` printed `✓ Tests passed` and exited `0` for runs that ran no tests at all.
The cause was a boolean that could not tell "no project was selected" from "every project
matched nothing" — it answered `false` to both, so an empty run became indistinguishable
from a real one and fell through to the success branch. It is now a three-state
(`NoProjectsSelected | AllZeroMatch | Ran`) carried on the wire, and both no-op outcomes
exit `3`, matching `confirm`'s existing contract: refuse to green without evidence.

The remediation differs by cause, because the available evidence differs. A run that
selected no project has discovered no test names, so it says that rather than offering a
"did you mean…" it cannot support. A filter that matched nothing ran against real
discovered tests, so it reports how many projects were searched and points at
`fshw status test-prune`.

Separately, a **red** verdict now names the failing plugin. The agent hint block listed
suites and never plugins, so a run red on `analyzers` or `format` displayed a wall of
passing test counts — seen twice in one day, misattributed both times. A non-zero exit
with no failing plugin *and* no failing suite now says `UNEXPLAINED` instead of rendering
as a tidy block.

And a finding promoted past the failure threshold now reads `[promoted from warning]`
rather than `[warning]`, which on a record stamped `severity: error` looked like a
contradiction and got a build-blocking finding triaged as non-urgent.

### A green re-run retires the red it just disproved (AUTOMATION-225)

`fshw test-rerun --filter-class '*BrowserIntegrationTests'` re-ran those tests, they
**all passed**, and the verdict stayed **RED** — forever. The run went down as
`ProjectInFull` with the filter as an opaque passthrough string, so the launch
*request* could not describe what the run had reached, and a run that claims no reach
retires nothing. An environmentally-caused failure (`ERR_NETWORK_IO_SUSPENDED` after a
machine suspend) therefore became **permanently sticky**, with no command short of a
full-suite run able to clear it. It blocked a production deploy three times.

The launch request was the wrong witness. A run's **own CTRF report** knows exactly
which classes it executed, so that is what the coverage is now derived from: the
classes the report shows **ran and passed** in this run. The re-run-and-passed reds
retire; every other red stands.

It fails **closed** at each step, because the alternative to a stuck red must never be
a false green:

- only classes with a PASS **and no failure** in this run's report are claimed —
  a class that ran and failed stays red on its own evidence;
- a project **absent from the launch selection** (impact-skipped) is claimed by
  nothing, whatever reports exist on disk;
- a **project-level** red (timeout, errored, deferred, unparseable failure) still
  requires a full-project run, exactly as before;
- a report that is **missing, unparseable, or shorter than its own summary total**
  (the runner omits per-test entries for raw-throw failures) claims **nothing** — the
  pre-fix behaviour, reached by default.

`confirm` is untouched: a filtered run is still a filtered scope, and a merge verdict
built on one is still **exit 3**.

### `fshw --version` names the source ref it was built from (AUTOMATION-123)

`fshw --version` now prints a second line naming the SOURCE this binary was
built from, in words — the human-readable complement to the binary-identity
handshake (AUTOMATION-147) and the verdict's producer hash (AUTOMATION-129):

- a RefStamp local pack: `source ref: <change-id>.g<commit-id>[.dirty] (local ref-stamped pack…)`
- a release/CI build: `source ref: <sha> (commit metadata, release/CI build)`
- anything else: `source ref: unknown …` — stated, never guessed.

"Is my gate running my fix?" is now one command, not a `strings`-probe of a
cached DLL. The ref arrives via the version RefStamp embeds at pack time
(`X.Y.Z-ref.<change-id>.g<commit-id>[.dirty]`) or the `+<sha>` metadata
CommandTree's build stamp records for plain dev builds.

### The verdict is a FILE — `.fshw/verdict.json` and `fshw verdict`

The headline of this release. Every `check` and `confirm` now publishes its result to
`.fshw/verdict.json`, written atomically (temp + rename, so a partial read is
impossible) — **including** the runs that fail, time out, or lose the daemon mid-run,
which are precisely the moments the human-readable output is least sufficient and the
temptation to scrape it is highest.

**This is the surface agents and CI should read.** Not the progress display: that is
written for a human and it will change. An orchestrator spent two days grepping
`total:` and `elapsed:` out of it, and then wrote a 40-line unverified bash harness
that made merge decisions.

The file is content-addressed to **the tree it verified** (`treeHash`) *and* **the
binary that verified it** (`producer.hash`). Both, because a stale daemon writes a
verdict for an unchanged tree, the `treeHash` matches, and the verdict reads as
current — the provenance chain had a hole in the middle. `fshw verdict` does the
comparison for you, **contacts no daemon and triggers no run**: reading cannot perturb
the thing being read.

```bash
fshw verdict          # stdout: a JSON envelope that always states `applies`
# 0 green · 1 red · 2 incomplete · 3 unearned scope · 4 STALE · 5 no verdict
```

The exit code and the file's `outcome` are two renderings of **one** `CheckOutcome`.
There is deliberately no "agent mode" that changes what a check *means*: presentation
may adapt to the caller; semantics may not. Schema, exit codes and the tree-hash recipe
are in the [CLI README](src/FsHotWatch.Cli/); the reasoning is
[ADR-013](docs/adr-013-the-verdict-is-a-file-content-addressed-to-its-tree.md).


### `confirm` on a warm cache: a refusal despite evidence (AUTOMATION-161)

Run `fshw confirm` twice on a **byte-identical tree** and the second exited **3 — "NO
TESTS RAN — nothing was verified"**, while the very same run's status line reported *"1
passed, 0 failed in 1 projects, full suite (cached)"*. Both described the same run. So did
`fshw check`. **Both headline verbs, on every repeat run.**

This release is about greens nobody earned. This was its **inverse**: not a green without
evidence, but a **refusal despite evidence** — because the replay dropped the receipt.

And it was worse than a dropped receipt. The second `confirm` **did** force the full suite
and **did** run it — 102 seconds, 1965 tests passed, a complete CTRF report written to
disk — and then the task cache **replayed a cached terminal over the `TestsFinished` that
carried the result**. The one handler that folds a finished run into plugin state was
skipped, so `LastCoverage` stayed empty, so `test-scope` answered *"no tests ran"*, so the
verdict refused. **`confirm` refused a verdict on evidence it had just spent 102 seconds
producing.**

One rule, in three places:

- **core** — a `Custom` message is a cache **writer**, never a cache **reader**. Its payload
  is not in the key, so a hit is a *collision*, not a proof; and it delivers work already
  done, so a replay can only *destroy* evidence.
- **test-prune** — a **process may not assert a test result it has no record of running**.
  The `BuildCompleted` key does not pin the tree (on a cold scan it is dispatched *before*
  the FCS pass, so `changed-symbols` is empty whatever the tree holds), so it may not carry
  a test claim across a process boundary. Fail closed: run.
- **cli** — `confirm` **honours a verdict it already earned**. `.fshw/verdict.json` is
  content-addressed to the tree *and* to the producing binary, and it is the **only** thing
  entitled to carry a green across a process boundary. `1m 45s → 1.4s`.

Deliberately **not** done: making the cached replay mint a CTRF receipt for the new run id.
That would manufacture merge-grade evidence from a key that cannot see the tree — the
vacuous green, rebuilt inside the fix for it.

### `fshw gate` is now `fshw confirm` — run the full suite and confirm `check` told the truth

**Migration: `fshw gate` → `fshw confirm`.** The old verb is **removed**, not aliased —
one name for one thing. (`gate` never appeared in a published package; this only affects
anyone tracking `main` or a local pack.)

`gate` named what the verb *blocks*, so it got built as a bouncer — pass/fail — and the
most valuable thing it produces was discarded as a side-effect. Its real job is to **run
the full suite and confirm that `check` told the truth.**

Running an unfiltered suite next to an impact-filtered `check` is a **comparison**, and
every disagreement between the two is a **bug in one of them**:

- *failed under `confirm`, never selected by `check`* → the selector **MISSED** a test.
  An impact-analysis bug, not a test bug.
- *passed under `confirm`, but `check` says red* → a stale red, a flake, or a
  **test-isolation defect**: a test that only passes *with company*, because another test
  sets up the state it depends on. There, `check` is the honest one and the full suite is
  the liar.

Nobody built that comparison because the name did not suggest there was one to make.
Reporting it is the next change; this one makes it obvious it is owed.

**"Full suite"** means every test project **`.fshw.json` knows about** — today,
`FsHotWatch.Tests` alone. `FsHotWatch.IntegrationTests` is in the solution but not in
`.fshw.json`, so `confirm` does not run it (AUTOMATION-158). It does **not** claim to run
every test in the solution.

### `confirm` is reachable without a daemon — and CI now runs it

The verb existed **only** on the daemon IPC path. `--run-once` bypasses the daemon
entirely, and `--run-once` is what CI uses — so our own CI could not invoke the very check
it is supposed to be judged by, and ran `check --run-once` instead.

That was fine only by accident. In CI the impact DB starts **cold**, and a cold DB
selects everything, so the full suite ran anyway. Warm that cache and the same green
would silently start coming from a subset.

`fshw confirm --run-once` closes it, and CI (`lint-cmd`) plus `mise run ci` now use it.
`confirm` also **runs the suite it demands** rather than merely asking for it: setting
full-suite scope makes the next run unfiltered, but does not make a run *happen*, so a
`confirm` on a tree whose suite had not run would refuse forever with no way to satisfy it.

**Known limitation — a warm task cache still defeats that.** Forcing a run makes a run
happen; it does not make it *execute tests*. On an unchanged tree whose result is still
in `.fshw/cache/`, `test-prune` replays the cached result, the replay writes no reports
for the new run, and `confirm` therefore exits **3** ("no tests ran") rather than 0. It
refuses instead of inventing a green — the safe direction — but a second `confirm` on an
unchanged tree cannot go green until the cache is cleared (`mise run cache-clear`). CI
starts cold and does not hit it. So the headline above is true of a cold cache only.

**Breaking (API):** `Command.Gate` → `Command.Confirm` (`Confirm of RunFlag list`);
`CheckVerdict.CheckMode.MergeGate` → `Confirmation`; `CheckVerdict.gateNeedsFullRun` →
`confirmNeedsFullRun`; `Verdict.Command.Gate` → `Verdict.Confirm`;
`IpcOutput.pollAndRender` gains a `forceFullRun` seam. **Breaking (wire):**
`.fshw/verdict.json`'s `command` field reads `"confirm"` where it read `"gate"`.
**Breaking (behaviour):** `--run-once` now writes `.fshw/verdict.json` and computes a real
`CheckOutcome`, so `check --run-once` can exit 2 (incomplete) where it previously exited 0.

### A process tree we failed to kill is no longer reported as killed

**Breaking (API):** `ProcessOutcome.TimedOut` now carries a third field —
`TimedOut of TimeSpan * ProcessOutput * KillOutcome`, where `KillOutcome` is
`Killed` | `AlreadyExited` | `KillFailed of exn`.

When a spawned child overran its timeout, `runProcess` killed its process tree
through a helper whose entire error handling was `with _ -> ()`. Every failure —
a Win32 permission refusal, anything at all — was swallowed, and the caller was
handed a `TimedOut` whose own documentation promised the tree had been killed. "I
could not kill it" was spelled exactly like "I killed it": a failure to *do the
work*, made indistinguishable from the work succeeding. The same disease as a
drain that could not measure returning `""` (below), and the runaway tree it hides
is still holding a lock, a port, a pipe, or a core.

The kill's outcome is now a value, and it is on the `TimedOut` case where a caller
cannot read the timeout without it. Only the already-exited race — the child
exiting in the gap between the timeout firing and the kill landing — is treated as
benign, which is exactly what `isExpectedKillException` had specified since the
2026-05-02 audit while the call site that was meant to consult it swallowed
everything instead. A genuine `KillFailed` is now logged at ERROR with the command,
the pid and the reason, and is named in every human-facing rendering: `outputOf`
spells it out in full, and one-line plugin statuses and verdicts carry a short
`(KILL FAILED — process tree STILL RUNNING)` marker so a status line can no longer
imply the runaway is over.

### A weakened test assertion, restored

`runProcess reports TimedOut on kill` had been loosened to assert only the
`TimedOut` tag, on the recorded grounds that "capturing pre-kill stdout races
subprocess startup under load". It did not race subprocess startup — it raced the
**thread pool**, which is the bug fixed below, and the loosened assertion had been
passing for months precisely because it had stopped testing the thing that was
broken. It now asserts again that a child's pre-kill stdout actually reaches the
`tail`. Verified by breaking the drain and watching the restored assertion go red
where the weakened one stayed green, and by 30 consecutive runs on a box saturated
to a load of 56 across 12 cores.

### A process whose output we failed to read no longer reports an empty output

**Breaking (API):** `ProcessOutcome`'s three cases now carry a `ProcessOutput`
rather than a `string` — `Succeeded of ProcessOutput`, `Failed of int *
ProcessOutput`, `TimedOut of TimeSpan * ProcessOutput * KillOutcome` (that third
field is from *A process tree we failed to kill*, above). Use `outputOf` /
`renderOutput` (rendered for humans, and it names an incomplete read) or
`ProcessOutput.text` (raw bytes, for text-searching) to get a `string` back.

Once a spawned child exits, its redirected stdout/stderr are drained for a
bounded 2 s window — bounded because a *grandchild* that inherited the pipe (an
MSBuild node, a Playwright driver) can hold it open long after the child is gone,
and an unbounded wait on that pipe is the 16 h wedge. When that window expired,
`runProcess` handed back whatever it had captured as a plain `string`. An empty
capture was therefore indistinguishable from a child that printed nothing.

They are not the same fact, and the difference bit. The stream pumps were
`ReadAsync` continuations scheduled on the **thread pool**, so on a saturated box
— a full-suite `check`, exactly when a spawn's output matters most — the reader
was never scheduled at all, the window expired having read zero bytes, and the
child's output came back `""`. The 2 s clock was measuring the *thread pool*, not
the process. Reproduced 5/5 under a saturated pool: a child that printed `hello`
reported an empty stdout.

Two fixes, because one alone would only have made the silence rarer:

- The pumps now own **dedicated threads** and read synchronously, so a saturated
  pool can no longer starve them (the same `LongRunning` remedy
  `runWithCancellableTimeout` already used). The drain window now measures the
  pipe, which is the thing it names.
- "I did not finish draining" is now a **distinguishable value**, not an empty
  string: `ProcessOutput.Drained text` (both streams reached EOF — the child's
  complete output, the only capture you may assert against) versus
  `ProcessOutput.DrainTimedOut (captured, window)` (we stopped listening while a
  stream was still open — `""` here means "we read nothing", never "the child
  printed nothing"). A caller that renders text says so; a caller that decides
  anything must handle the timed-out case explicitly. Human-facing renderings
  (`fshw errors`, plugin status, a build's silent-failure diagnostic) now NAME an
  incomplete read instead of presenting it as the child's output.

This also fixes an intermittent `ProcessHelperTests` failure on loaded boxes,
where a starved drain was asserted against as though an empty stdout had been
measured — including, in the env-strip tests, *passing* for the wrong reason.

### A compile-item-only project-file edit can no longer wedge the deps-freshness gate red

The deps-freshness gate uses an mtime fast-path to notice when a project's
restored packages (`obj/project.assets.json`) have fallen behind its declared
dependencies. But mtime is not a content oracle: adding or reordering a
`<Compile>` item bumps the `.fsproj`'s modification time without touching the
package graph at all. The gate read that as Stale, attempted a one-shot restore,
and — on a memory-pressured box where the restore timed out — the debounce
tracker kept the project pinned Stale (deps RED) on every subsequent cycle until
the daemon was restarted. The gate now backs the mtime probe with a CONTENT
signature over only the dependency-declaring inputs: the fsproj's
`PackageReference` / `ProjectReference` / `Import` / `Sdk` / target-framework
subset (source items like `<Compile>` are deliberately excluded), plus the full
bytes of every governing `Directory.Packages.props` / `Directory.Build.props` /
`paket.lock` / `paket.dependencies`. A compile-item-only edit leaves that
signature unchanged, so the phantom Stale is recognised and suppressed; a real
package-graph change still moves the signature and re-arms recovery. See
`docs/adr-008-mtime-is-not-a-content-oracle.md`.

### A check with nothing testable to run can no longer hang forever

When the only things a `fshw check` had queued to verify were code symbols with
**no covering test** (e.g. an infra-only or docs-only change that left behind a
few untestable helper symbols), a freshly (re)started daemon would drop those
symbols as uncovered — correctly finding an empty set of affected tests — and
then, having no test baseline yet for this session, fall back to running the
ENTIRE suite anyway. On a memory-pressured box that full run could stall before
any test process even appeared, and the check streamed "Waiting for plugins:
test-prune" for hours without ever finishing (observed 5 h+). The launch
watchdog couldn't help: nothing was *expected* to run, so "no test process" was
the correct state, not a stall. An all-untestable cycle is now recognised for
what it is — genuinely nothing to verify — and the check reports a clean pass
(zero tests ran) immediately. A genuine cold check that has never established a
baseline still runs the full suite as before.

### A daemon-startup race can no longer poison a check verdict

`fshw check` issued right after a daemon (re)start — e.g. an explicit
`stop`/`start` to reload analyzers — could fire its first RPC while the daemon
was still cold-scanning (analyzer reflection load starving the pipe acceptor) or
briefly between pipe endpoints during the restart. The `ConnectAsync` timeout
("The operation has timed out") or a dropped connection surfaced as **exit 1** —
which a programmatic consumer reads as "the daemon ran and found failures". An
autonomous loop watching the exit code would then treat a transient startup race
as real diagnostics. `check` now gates on the daemon actually *answering* an RPC,
not merely the pipe being listenable: transient connect faults during startup are
RETRIED (with a visible progress line) against a startup deadline that is
distinct from the per-RPC connect timeout. A genuine connect failure — the daemon
is absent, crashed during startup (detected via its pidfile, so it fails fast
rather than spinning out the deadline), or never becomes responsive — now exits
**2** ("un-completable") with a pointer to `logs/daemon.log`, never exit 1.

### `test-rerun` can outlast a long `beforeRun` chain

`fshw test-rerun` waited a fixed 120 s for an in-flight background test run to
release the test slot before giving up with `busy`. A repo whose
`tests.beforeRun` chain takes 90 s+ (so the prior run easily runs past 120 s)
could never get its class-level rerun output in the terminal. The wait is now
configurable via `--wait-sec <seconds>` and defaults to **600 s**, so a long
setup chain no longer defeats an explicit rerun.

### A stale test binary can no longer pass the gate

`test-prune` runs each test project with `dotnet run --no-build`, executing the
**on-disk** assembly. The cold-start apphost check only deferred when that
assembly was *missing* — a present-but-**stale** binary (one that exists but
predates the newest source) was still run, executing code that no longer matches
the sources and reporting a pass/fail that isn't real. The old check also fired
only on a FAILED launch, so a stale binary that exited 0 sailed straight through
as a confident false green. The gate now defers a test project whose compiled
assembly predates the newest source as "waiting on build" — without launching it
— exactly the honest signal a missing apphost already produced, so a stale
artifact can never yield a passing verdict. Mirrors
`BuildPlugin.verifyArtifactsFresh` (ADR-008).

### The task cache no longer re-reads its whole directory on every write

Cache entries are named `{plugin--file}@{contentHash}.json`, and the write that
supersedes one collects its predecessors. It found them by asking the filesystem —
`Directory.EnumerateFiles(cacheDir, prefix + "*.json")` — which is **not** a
prefix-optimised syscall: it `readdir`s the whole directory and pattern-matches in
managed code.

A cold scan writes about **three entries per source file** (Lint, Analyzers and
FormatCheck each carry a per-`FileChecked` cache key) into a directory that grows to
about three entries per source file. Every write scanned the lot, so the cold scan was
**quadratic** in the size of the repo. Measured at ~2.2 ms per scan against a
4,500-entry directory: roughly **ten seconds of pure directory scanning** added to a
cold scan of a 1,500-file repo — on precisely the paths that were already timing out
(cold scan, `--run-once`, `confirm`).

The writer already knows the path it just wrote, so the cache now remembers the path
each key was last written to and deletes exactly that one. **No scan on the write
path, at all** — a test asserts the count is zero across 200 writes. A one-time sweep
at construction seeds that memo from whatever a previous process left on disk, so
inherited siblings are still collected by the first write to the key that owns them.
The guarantee is unchanged: only the newest content-hash entry for a plugin+file
survives. Each delete is now independently guarded, too — one undeletable sibling no
longer shields every sibling behind it.

### A test run no longer pins the symbol table for its entire duration

The test-run `Async` closed over the whole `TestPruneState` record, and it lives as
long as the suite does — minutes. Whatever it closes over, it **pins** for that whole
time: including `SymbolSnapshot` (the repo-wide symbol table) and `PendingAnalysis`,
neither of which a run touches. Meanwhile the agent loop keeps folding incoming
`FileChecked` events into new state generations, and the pinned generation holds down
every node the newer ones replace. The peak lands exactly when the suite is running
and FCS is at its own peak — and FsHotWatch is ~85% native FCS memory. Recent work had
been widening the pinned record, not narrowing it.

The run now takes a `TestRunInputs` record carrying **only the four fields it reads**
(`AffectedTests`, `ChangedSymbols`, `ChangedSymbolsAllUncovered`, `UnanalyzableFiles`).
The rest of each generation dies on schedule. The type is the enforcement, not a
convention: a run cannot reach a field it was not given.

### The freshness gate's memo now memoises

`ArtifactFreshness.Cache` is documented "each project is walked at most **once** per
run" and "thread-safe: test groups run in parallel". Both were true — but they were not
the same claim, and only the second one was implemented.
`ConcurrentDictionary.GetOrAdd(key, valueFactory)` is thread-safe (exactly one result
is published) without being once-only: it may invoke the factory **concurrently on
several threads** for the same key and throw the losers' work away. Test groups do run
in parallel, and their `ProjectReference` closures overlap heavily — so the duplicated
directory walks and `XDocument.Load` parses the memo exists to eliminate were still
each happening N times. The memo's stated guarantee was not the one it had.

It is now a `Lazy` under `ExecutionAndPublication`: exactly one execution per key, with
every other caller blocking on it and taking its result. A test pins that from 16
threads released together.

Also in the same path: the map of "every file the build put in this output dir",
built from a full walk of the output tree and read only by key, was an immutable F#
`Map` — O(n log n) to build, with a heap node per file, for an ordering nothing asks
for. It is a `Dictionary` now.

### docs: the full-pipeline example is now COMPILED, and it had rotted

`examples/FullPipelineExample` was a README with an F# code block and nothing else —
no project, nothing that compiled it. It had drifted from the API it claims to
demonstrate, in three places at once: `Daemon.create` takes a `DaemonOptions` it did
not pass, and `LintPlugin.create` / `AnalyzersPlugin.create` both take a leading
`repoRoot` it did not pass. A reader following it would not have got a working daemon.

It is now a real project (`examples/FullPipelineExample/FullPipelineExample.fsproj`,
a member of `FsHotWatch.slnx`, built with `TreatWarningsAsErrors`), and the README's
code block is **sourced from it** via a SyncDocs `src=` region — the same mechanism
`docs/writing-plugins.md` already used for `PluginExample`. The snippet a reader
copies is now, by construction, a snippet the compiler accepted.

`mise run ci` now runs `sync-docs-check`, which it previously did not. The drift guard
existed and worked; nothing ran it — so a guard that could not do its work was
reporting nothing, which is the same failure this release is otherwise about.
`examples/ExampleAnalyzer` and `examples/PluginExample` also gain
`TreatWarningsAsErrors`, so a new DU case breaks the examples loudly instead of
quietly leaving them teaching a stale API.

### deps: `StreamJsonRpc` 2.25.29 retires both MessagePack pins

Two pins existed to hold StreamJsonRpc's transitive serializers above advisories: a
direct `MessagePack` reference in `src/FsHotWatch/FsHotWatch.fsproj`
(GHSA-hv8m-jj95-wg3x, LZ4 decompression AccessViolation, patched 2.5.301) and a
repo-wide `Nerdbank.MessagePack` pin in `Directory.Build.props`
(GHSA-2cwq-pwfr-wcw3, attacker-controlled `stackalloc` in `DateTime` decoding,
patched 1.1.62). Both were compensating for floors StreamJsonRpc 2.24.92 declared
*below* the fixes — `MessagePack [2.5.198, )`.

`StreamJsonRpc` 2.25.29 declares `MessagePack [2.5.302, )` and
`Nerdbank.MessagePack [1.2.4, )`. The floors now come from the dependency itself, so
**both pins are removed**: restore resolves 2.5.302 and 1.2.4 transitively, with no
advisories solution-wide. Dropping the direct reference *without* the StreamJsonRpc
bump was measured, not assumed — it resolves the range floor `MessagePack 2.5.198`
and fails restore under `NU1903`/`NU1902` with seven advisories.

This supersedes the `MessagePack 3.1.8` bump earlier in this cycle. FsHotWatch never
executes MessagePack at all — `Ipc.fs` builds `JsonRpc` over a bare
`HeaderDelimitedMessageHandler`, StreamJsonRpc's default **JSON** formatter — so that
change's claim to have verified 3.x at runtime through the IPC tests was wrong; those
tests exercise the JSON path. And since a direct `PackageReference` becomes a public
dependency of the published package, it pushed MessagePack's breaking 3.x rewrite onto
every consumer, including any that *do* select the MessagePack formatter that
StreamJsonRpc 2.x was not built against. Tracking the line StreamJsonRpc targets is
both safer downstream and two fewer pins to carry.

### deps: a sweep of the rest, and two more pins retired

Surveyed every direct dependency with `dotnet list package --outdated`, once on
stable and once `--include-prerelease`. Almost everything is already current; what
came back as "behind" was mostly next-major pre-release lines this repo has no
business tracking — `FSharp.Core` 11.0.101-preview, `Microsoft.Data.Sqlite` /
`Microsoft.SourceLink.GitHub` / `System.Security.Cryptography.Xml` 11.0.0-preview
(all .NET 11 previews against a `net10.0` target), `xunit.v3` 4.0.0-pre,
`Fantomas.Core` 8.0.0-alpha — plus `dotnet-fsharplint` 0.27.1--date20260810, a
nightly rather than a release. `FSharp.Core` and `FSharp.Compiler.Service` float
(`10.1.*`, `43.*`) and are already at the newest in-range build. All seven pinned
`dotnet-tools.json` tools are at their latest published versions, so the manifest is
unchanged.

Two real changes fell out:

- **`FSharpLintAnalyzerShim` 0.3.0-alpha.6 → 0.3.0-alpha.7** in
  `tools/fsharplint-shim`. The `.fshw.json` analyzer path is version-independent
  (`bin/Debug/net10.0/`), so nothing else moves with it.
- **Both `SQLitePCLRaw.lib.e_sqlite3` pins removed** — from `FsHotWatch.Cli` and
  `FsHotWatch.TestPrune`. Same story as MessagePack, one layer over: the pin existed
  to clear GHSA-2m69-gcr7-jv3q (High) out of the transitive 2.1.11, and
  `TestPrune.Core` 6.1.2 now declares `SQLitePCLRaw.lib.e_sqlite3 3.50.3` itself. A
  forced `--no-cache` restore confirms 3.50.3 resolves with both pins gone. The
  first restore after removing them *appeared* to confirm it too, from a stale
  assets file — the answer only counts because the forced one agrees.

`SQLitePCLRaw.lib.e_sqlite3` 3.53.3 exists and is newer than the 3.50.3 we land on.
Taking it here would mean re-adding exactly the pin just removed, so it belongs in
`TestPrune.Core` — the package that owns the constraint — and is left for that repo.

## Released — the `alpha.9` line onward (2026-04-22 → 2026-06-24)

_These narratives are all shipped. This root file is a human-readable summary that fell
behind around `core-v0.8.0-alpha.8` — the entries below were released across the alpha.9+
series but only some got closed out of `Unreleased`. For the precise per-version, per-package
history (the source of truth that drives the release tags) see each `src/<package>/CHANGELOG.md`.
Latest released: `core-v0.8.0-alpha.33` · `cli-v0.8.0-alpha.39` · `testprune-v0.7.0-alpha.29` ·
`analyzers-v0.7.0-alpha.20` · `build-v0.7.0-alpha.15` · `coverage-v0.7.0-alpha.14`._

### A test-file-only edit can no longer go green against a stale test binary

The build plugin used to **skip** MSBuild when a change touched only test files,
waiting instead for FCS's in-memory `BatchChecked` type-check signal and then
emitting `BuildSucceeded`. But FCS type-checking does not emit the runnable
assembly for an xUnit v3 standalone-exe test project — only MSBuild does — and
`test-prune` runs each test project with `dotnet run --no-build`, executing the
**on-disk** DLL. So a freshly-edited test compiled fine in FCS, the build was
skipped, and the test runner ran the **stale** binary → a confident false green
(`test-rerun` / the `check` test phase both took this path). Now every source
change, test files included, runs the real build: MSBuild re-emits the test DLL
and the `verifyArtifactsFresh` post-build guard runs before `BuildSucceeded`, so
`--no-build` can only ever execute an up-to-date assembly. `WaitingForBatchPhase`
is removed. See `docs/adr-012-test-file-changes-build-no-batchchecked-skip.md`.

### The test gate trusts the test report, not the exit code

`fshw check` could go falsely RED when a test host exited non-zero during a dirty
shutdown (e.g. the Microsoft.Testing.Platform exit-7 flake) after writing a clean
report — surfacing "Tests failed in <project>" with zero named tests while a re-run
came back green. The pass/fail verdict is now derived from the CTRF report's summary
counts and is authoritative over the process exit code (only a tie-break when no
report exists): a non-zero exit with a clean, complete report is GREEN. A run that
aborts before writing any parseable report (non-zero exit, no results) gets a new
`TestsErrored` verdict — not a failure, not a pass, never cached, surfaced as an
honest "errored — re-run". CTRF report injection is scoped per project via the new
`.fshw.json` `reportVerificationFormat` (`auto` | `ctrf` | `off`), so a non-xUnit
runner that would choke on `--report-ctrf` keeps the exit code authoritative. Also
fixed: per-test flakiness tracking, which silently recorded nothing because the
parser read a top-level `tests` array instead of the real nested `results.tests`.

### The test gate can no longer go green without running the tests

`fshw check` could report "No errors" while executing zero tests: TestPrune's
impact baseline advanced on symbol ANALYSIS, not on tests passing, so a run
that aborted (e.g. a failing test `beforeRun` hook) or failed still absorbed
the symbols it never verified — a later check then found "0 affected tests"
and exited 0. TestPrune now keeps a durable needs-testing queue
(`.fshw/test-prune/pending-verification.json`); a symbol leaves it only when a
covering test run completes green. Aborted runs report Failed instead of
green, the "nothing to test" fast path requires the persisted queue to be
empty, a cached green can only replay for a queue-empty state, and a daemon
restart re-flags anything still unverified. The sidecar is written once per
analysis batch, so the per-file hot path gains no I/O.

### Deterministic unit-suite coverage under machine load

The coverage ratchet no longer flakes when the machine is busy: the two
real-subprocess plugin timeout tests moved to the coverage-excluded integration
suite, the post-kill drain tail was extracted into an internal
`ProcessHelper.drainedOrEmpty` helper with direct deterministic tests, and the
absent-key `EndSubtask` arm gained a direct test. Per-file line coverage is now
identical run-to-run (quiet or loaded) and ratchet floors are settled to the
stable actuals.

### Daemon: idle-exit — quit after a configurable idle period

An idle daemon still holds a large warm working set (mostly FCS-rooted native
memory, ~2.8-3.1 GB). With one daemon per jj workspace, idle workspace daemons
waste gigabytes between bursts of work. The daemon can now shut itself down
after a configurable idle period to reclaim that memory. This is transparent:
the next `fshw` command auto-starts a fresh daemon and the file-backed check
cache survives restarts, so the next `check` pays one auto-start plus a
mostly-cache-hit scan. Shutdown is the daemon's normal graceful path (the same
`cts.Cancel()` the IPC `stop` request uses — clean pid/lock release and plugin
disposal), guarded by an atomic fire-once latch so it can never fire twice.

#### Added
- **`idleExitMin` config key** in `.fshw.json` (`number | false`):
  - **absent → AUTO mode**: enabled with a 30-minute threshold **iff** the repo
    root path contains a `/.workspaces/` segment (non-default jj workspaces);
    disabled otherwise — the default/main workspace daemon never auto-quits.
  - **`0` or `false`**: disabled everywhere (explicit opt-out, overrides AUTO).
  - **positive integer `N`**: enabled with an `N`-minute threshold regardless of
    path (explicit opt-in, even for the default workspace).

  The daemon only exits when idle (no file events, no running plugin work) for
  the full window; work in flight at the threshold defers the exit to a later
  check.

### Daemon: ship `System.GC.ConserveMemory=9` default

The daemon keeps `FSharpChecker` and its FCS caches warm, which generates a
large amount of collectable managed churn above the live working set. Left to
the default GC policy, that churn accumulates into multiple gigabytes of
retained heap. The CLI now bakes `System.GC.ConserveMemory=9` into its
`runtimeconfig.json`, which in benchmarks cut the daemon's steady footprint by
~25-40% (settled ~3.0GB vs 3.9-4.4GB; peak 5.0 vs 5.9-7.8GB against a
32-project solution) with no measurable cost to scan speed or diagnostic
parity.

#### Changed
- **`FsHotWatch.Cli` runtime config.** Added the `System.GC.ConserveMemory=9`
  `RuntimeHostConfigurationOption` so the daemon runs with conservative GC by
  default. Override per-process with the `DOTNET_GCConserveMemory` environment
  variable (`0`-`9`), which takes precedence over the baked-in default.

#### Removed
- **Dead `projectCacheSize` argument.** Dropped the `projectCacheSize = 200`
  argument from the daemon's `FSharpChecker.Create` call. It is ignored by the
  `TransparentCompiler` path (`useTransparentCompiler = true`), which never
  reads it, so the value had no effect.

### Daemon: fix silent truncation of cold scans (cancelled-check race)

During a cold scan the BuildPlugin's `dotnet build` touches `obj/**/ref/*.dll`;
the watcher fires, `processBatch` re-checks the affected files, and
`CancelPreviousCheck` cancels the scan-side in-flight check of the *same* file. A
cancelled check surfaces as `None`, and the scan emit loop silently dropped it
(`| None -> ()`). The dropped files were never reported to NOR cleared from the
ErrorLedger, so a scan could report **green** while diagnostics for hundreds of
never-checked files were missing — observed as `Checked 103 files … skipped 46`
on a 742-file registration with exit 0 and a known diagnostic absent from
`check` output.

#### Fixed
- **Scan now retries cancelled/aborted/failed checks** within a bounded budget
  (3 retry rounds per tier). The retry re-invokes the same per-file check, which
  re-reads current disk content via `CancelPreviousCheck` — so a newer user edit
  that legitimately superseded an in-flight check is observed on retry (not
  duplicated), preserving the cancellation ordering guarantee. The common
  single-race case converges to all files checked.
- **Incomplete scans no longer present as clean.** If files remain unchecked
  after the retry budget, the scan-complete state carries the unchecked count;
  daemon status renders `incomplete: N files checked, M unchecked …` (a non-ok
  condition) instead of `complete: …`, and the scan log line gains an
  `, unchecked M` suffix. The existing `Checked N files (T tiers), skipped M`
  prefix is preserved for external tooling that greps it.

#### Changed
- `ScanState.ScanComplete` now carries `unchecked: int`
  (`ScanComplete of total * unchecked * elapsed`).

### Daemon: auto-recovering deps-freshness gate before FCS analysis

When a project's restored dependency state (`obj/project.assets.json`) goes
stale relative to its declared deps — a `PackageReference` added without a
`dotnet restore`, or a restore that half-completed — FCS otherwise emits a
phantom error-storm (`namespace`/`type not found` across the whole project)
that looks like broken code but is really a stale restore. The daemon now
catches that state before type-checking, and recovers from it automatically
where it can.

#### Added
- **Deps-freshness gate (`FsHotWatch.DepsFreshness`).** Before FCS analysis the
  daemon compares each project's restored-assets mtime against its declared
  dependency files (`.fsproj`, `Directory.Packages.props` /
  `Directory.Build.props`, `paket.lock` / `paket.dependencies`,
  `.config/dotnet-tools.json`). On a staleness signal it first attempts a
  **one-shot restore to recover automatically**; only if recovery fails does it
  **fail fast with a single actionable diagnostic**, instead of letting the
  type-checker produce a misleading "namespace not found" storm. Detection and
  orchestration are pure (injected restore runner + freshness probe), unit-tested
  without shelling out or touching FCS. See
  `docs/plans/2026-06-02-deps-freshness-gate.md`.

### Dependencies: refresh external packages

Routine maintenance bump of external (non-FsHotWatch) NuGet dependencies to
current releases. All gated checks (daemon `check`, 1230 unit tests with
coverage) stay green; no public API change.

#### Changed
- `FSharp.Compiler.Service` 43.12.203 → 43.12.204 (core daemon + Analyzers
  plugin + ExampleAnalyzer).
- `StreamJsonRpc` 2.24.84 → 2.24.92 (IPC).
- `Microsoft.SourceLink.GitHub` 10.0.203 → 10.0.300 (all packable projects).
- `Microsoft.Testing.Extensions.CodeCoverage` 18.6.2 → 18.7.0 (test).
- `CommandTree` 0.6.1 → 0.6.2 (CLI) — picks up the revision-stamping target fix,
  so building `fshw` outside a VCS repo (e.g. a `.git`-less jj sub-workspace) no
  longer emits `MSB3073` warnings.
- Pinned transitives advanced to current patched releases:
  `System.Security.Cryptography.Xml` 10.0.7 → 10.0.8 and
  `Nerdbank.MessagePack` 1.1.62 → 1.2.4 (both still cover their respective
  CVE pins in `Directory.Build.props`).

`FSharp.Core` stays on the pinned `10.1.*` float (already current). No
YamlDotNet dependency exists in this repo.

### Daemon: auto-refresh FCS on `.fsproj` and `obj/project.assets.json` changes

Reported by `thellma/intelligence` during the `bedrock-spike` landing
(docs/fr-auto-refresh-fsproj-changes.md, 2026-05-25). Adding an
`AWSSDK.Bedrock` `PackageReference` and running `dotnet restore` left
the daemon reporting `FS0039: namespace 'Bedrock' is not defined`
until the user ran `dotnet fshw stop && dotnet fshw start` — discarding
the entire FCS cache for ~20 unrelated projects unnecessarily.

Closing the loop took three contracts: detect the change, re-evaluate
only what's affected, and keep everything else hot.

#### Fixed
- **The daemon now re-runs FCS on the affected project's source files
  after a project-tier change.** Previously `processBatch` invalidated
  the FCS cache and re-discovered options on a `.fsproj` change, but
  the per-file re-check only ran when the same batch also contained
  source-file edits. A pure project change had no source files, so the
  error ledger retained the previous cohort's stale diagnostics until
  the user saved a `.fs` file or restarted the daemon. The boot-scan
  re-check on restart is what made the "stop && start" workaround
  appear to "fix" the bug.
- **A project change no longer cold-starts every other project.** The
  re-check above, as first written, re-checked *all* registered files
  and cleared the whole check cache — on a 20-project solution that
  reintroduced the ~30s cold start the FR set out to eliminate. The
  daemon now scopes invalidation to the changed project **and its
  transitive dependents** (`Daemon.resolveAffectedProjects`): it calls
  `FSharpChecker.InvalidateConfiguration` for just that set, re-discovers
  without clearing unrelated projects' cached results, and re-checks only
  that set. Dependents are explicitly cache-invalidated so a dependent
  that breaks when the changed project's public surface changes still
  recomputes (correctness over warmth). Repo-wide changes (`.props`,
  solution edits, a brand-new project) and the case where watcher and
  project-graph paths diverge (a repo under a symlink) fall back to the
  full invalidate-and-recheck path.

#### Added
- `Watcher.isProjectAssetsJson` / extended `Watcher.classifyChange` —
  `obj/project.assets.json` (the post-`dotnet restore` materialization
  of the package graph) is now treated as a project-tier change. The
  `FileSystemWatcher` / FSEvents enumeration picks it up despite living
  under `obj/` (every other `obj/` entry stays excluded). This gives
  the daemon a second, canonical "package graph is coherent on disk"
  signal that doesn't race with a `.fsproj` edit's "package graph is
  intended to change."

#### Changed
- `Pinned transitive Nerdbank.MessagePack 1.1.62` (GHSA-2cwq-pwfr-wcw3).
  `StreamJsonRpc 2.24.84` pulls in vulnerable `1.0.2` by default,
  failing fresh `dotnet restore` under `NU1903`. Same Directory.Build.props
  pattern as the existing `System.Security.Cryptography.Xml` pin.

### TestPrune: test-skip now works correctly after daemon restart

TestPrune prunes the test suite in two phases: **Phase A** runs during the initial cold scan (FCS checks every file from scratch and records symbol fingerprints), and **Phase B** runs on subsequent daemon restarts when FCS is warm (the daemon reloads the persisted fingerprints and skips tests whose symbol fingerprints haven't changed). Before this fix, Phase B would always re-run tests even with no source edits, because the fingerprint comparison included extern symbols (cross-file type references) on the current side but not the stored side, producing phantom diffs on every restart.

#### Fixed
- fix: daemon restart with no source edits no longer spuriously re-runs tests — `detectChanges` now filters extern symbols from both sides internally (requires TestPrune.Core ≥ 4.0.1), eliminating the phantom symbol diffs that caused already-passing tests to re-run on every warm restart.

#### Changed
- refactor: remove redundant `currentForFile` pre-filter from `TestPrunePlugin` (`detectChanges` now handles extern filtering internally in TestPrune.Core).

### Drop hardcoded FS1182 default suppression

#### Changed
- **BREAKING (behavior):** `Daemon.DaemonOptions.FcsSuppressedCodes = None` now resolves to an empty `Set<int>` instead of `Set.ofList [ 1182 ]`. The daemon no longer ships a built-in suppression for FS1182 ("unused binding"), which embedded a project-level policy (originally a workaround for SqlHydra-generated code) at the wrong layer. Projects that need FS1182 silenced should declare it explicitly via `<NoWarn>FS1182</NoWarn>` in the fsproj (e.g. `Directory.Build.props`, as Intelligence already does) or `#nowarn "1182"` in source — both paths report at the correct scope.

#### Added
- `Daemon.resolveFcsSuppressedCodes : int list option -> Set<int>` — public helper exposing the option→Set resolution so it's directly testable.

### BuildPlugin owns artifact-freshness; remove ProjectDirtyTracker

#### Added
- `FsHotWatch.Build.BuildOutcome.BuildArtifactsStale of stale: StaleArtifact list * output: string` — new variant emitted when MSBuild's incremental cache reports success but per-project canonical DLLs are missing or older than their newest source file. Post-build verification runs in the async worker after `decideBuildOutcome` returns `BuildPassed`. Downstream plugins (TestPrune, etc.) can therefore trust `BuildSucceeded` as a guarantee of artifact freshness.
- `FsHotWatch.Build.StaleArtifact` / `StaleReason` types carry the structured diagnostic so cache replay reproduces the same per-project messages deterministically.
- Core `IProjectGraphReader` gained `GetTargetFramework`, `GetCanonicalDllPath`, and `GetMaxSourceMtime` accessors so `BuildPlugin.verifyArtifactsFresh` (and other consumers) can probe canonical paths without re-opening .fsproj files.

#### Removed
- **BREAKING:** `FsHotWatch.ProjectDirtyTracker` module — the dirty-bit handoff between BuildPlugin and TestPrunePlugin is gone. With staleness enforced inline by post-build verification, the heuristic dirty tracker has no consumers (`markDirty` / `clearFreshProjects` / `isStaleProject` removed).
- **BREAKING:** `BuildPlugin.create` no longer takes `dirtyTracker` (drops the 9th positional argument). `TestPrunePlugin.create` no longer takes `dirtyTracker` or `stalenessCheck` (drops the 8th and 9th arguments).
- TestPrune skip-on-stale code path, stale-binary warning re-emit, and the manual-run-tests deadlock workaround. With the freshness contract upstream, TestPrune dispatches every project on `BuildSucceeded`.
- `adaptiveTimeout` helper and `lastSuccessfulElapsed` map (only meaningful for stale-manual recovery, which no longer exists).
- `FsHotWatch.Cli.DaemonConfig.canonicalDllPath` — moved to `IProjectGraphReader.GetCanonicalDllPath` in the core lib.

### Naming normalized to `fshw`

#### Changed
- **BREAKING:** CLI command renamed from `fs-hot-watch` to `fshw` (`ToolCommandName` + IPC pipe-name prefix).
- **BREAKING:** Config file renamed from `.fs-hot-watch.json` to `.fshw.json`. Existing repos must rename.
- **BREAKING:** State directory consolidated from `.fs-hot-watch/` to `.fshw/` — pid, lock, and config-hash now live alongside the existing `cache/`, `errors/`, `logs/`, `test-runs/`, and `test-impact.db`. One directory for everything fshw writes. Existing daemons must be stopped and the legacy `.fs-hot-watch/` directory deleted.

### Drop jj reliance from plugin cache keys; content-hash FCS cache keys

#### Added
- `FsHotWatch.CheckCache.DiagnosticSignature` record (`StartLine/StartColumn/ErrorNumber/Severity/Message`) and `hashDiagnosticSignatures` — extracted from `fcsCheckSignature` so the hashing/sorting logic is unit-testable without a live `FSharpCheckFileResults`.

#### Changed
- `TimestampCacheKeyProvider.GetFileHash` now hashes file **content** (SHA-256) instead of metadata (path + size + mtime). Closes a correctness gap where two files with the same size + mtime but different bytes would collide. Class name preserved for backward compatibility; behavior matches the original "ls-tree merkle hash" design intent.
- `FileCommandPlugin` cache key migrated from `optionalSaltedCacheKey getCommitId` to a pure `merkleCacheKey` over `(command, args, arg-file SHA-256s)`. Editing a config file referenced in `args` (e.g. `coverage-ratchet.json`) now invalidates cached output even when the working-copy commit_id is unchanged.
- `FsHotWatch.Fantomas` `FormatCheckPlugin` cache key migrated from `optionalCacheKey getCommitId` to a content-merkle of `(file path, file source)` per `FileChanged` event.

#### Removed
- **BREAKING:** `getCommitId` parameter dropped from all six plugin `create` signatures (`BuildPlugin`, `TestPrunePlugin`, `AnalyzersPlugin`, `LintPlugin`, `FormatCheckPlugin.createFormatCheck`, `FileCommandPlugin`). New positional orders are documented in each package README.
- **BREAKING:** `FsHotWatch.JjHelper` module (`JjScanGuard`, `JjScanDecision`, `getWorkingCopyCommitId`, `getChangedFiles`) — the scan-skip-when-commit-unchanged optimization saved <5ms on a no-op trigger and was the only runtime jj reliance.
- **BREAKING:** `FsHotWatch.CheckCache.JjCacheKeyProvider` — was a stub that delegated to `TimestampCacheKeyProvider`; only role was as a marker for `Daemon.fs` runtime type-test.
- **BREAKING:** `Daemon.DaemonOptions.EnableJjScanGuard` field.
- **BREAKING:** `DaemonConfig.JjFileBackend` variant. The string `"jj"` is still accepted as a legacy alias and falls back to `FileBackend`.
- **BREAKING:** `force` parameter removed from the Scan API: `Daemon.ScanAll(?force)` → `ScanAll()`, `DaemonRpcConfig.RequestScan: bool -> unit` → `unit -> unit`, `IpcClient.scan pipeName force` → `IpcClient.scan pipeName`. The CLI `scan --force` flag is gone (had been a no-op since the scan-guard was deleted).

### TestPrune: per-test flakiness + per-project elapsed

#### Added
- `FsHotWatch.TestPrune.Flakiness` module: parses CTRF (Common Test Report Format) JSON from Microsoft Testing Platform runners (xUnit v3, MSTest v3+), persists per-test rolling history to `.fshw/test-history.json` (capped at 20 runs per test), and computes a `transitions / (n - 1)` flakiness score with skipped runs filtered out.
- `flaky-tests` IPC command — returns the top-K flakiest tests with name, score, and run count. CTRF generation is opt-in via a `dotnet`-vs-non-dotnet command discriminator so non-MTP runners (echo/sleep stubs in unit tests) are unaffected.
- **BREAKING:** Core `TestResult` DU widened with `elapsed: TimeSpan` on all three constructors (`TestsPassed`, `TestsFailed`, `TestsTimedOut`). Round-tripped via a new `elapsedSeconds` field in `FileTaskCache`; older cached entries deserialize as `TimeSpan.Zero`. New `TestResult.elapsed` accessor; `elapsedMs` field on per-project `test-results` JSON output.
- TestPrune run summary now names the slowest project when 2+ projects ran (e.g. `"3 passed, 0 failed in 3 projects (selected: no, slowest: ProjA 1.2s)"`) so a bottlenecked project surfaces from the plugin status line without querying JSON.

### CLI: warn when FileCommand plugin inputs go stale

#### Added
- Run-once output now scans each `FileCommand` plugin's args for files modified after the plugin's last successful run and emits `cached output may be stale → run fshw rerun <plugin>`. Defense-in-depth alongside the FileCommand cache-key salt fix. New helpers: `FsHotWatch.Cli.RunOnceOutput.PluginRunInfo`, `detectStalePluginInputs`, `formatStalenessWarning`; `FsHotWatch.FileCommand.collectArgFiles`, `argsStalerThan`.
- Cold-start cache bypass for `BuildPlugin`, `TestPrunePlugin`, and `FileCommandPlugin` — `CacheKey` returns `None` until each plugin's first work completes in the daemon session, so a stale on-disk entry from a prior session can't pre-empt the cold-start replay.

### Analyzers: failOnSeverity threshold

#### Added
- `failOnSeverity` parameter on `AnalyzersPlugin.create` — promotes analyzer diagnostics at or above the given severity to error. Default `Hint` (everything is fail-worthy). Configurable via `analyzers.failOnSeverity` in `.fshw.json`; unknown strings are warned and ignored.
- `FsHotWatch.ErrorLedger.DiagnosticSeverity.order` — total order on `Error/Warning/Info/Hint` for severity-threshold comparisons. `fromString` now returns `DiagnosticSeverity option` instead of throwing on unknown strings.

### MSBuild orphan workers fixed at the ProcessHelper layer

#### Added
- `FsHotWatch.ProcessHelper.isDotnetCommand` and `mergeDotnetEnv` (public).
- `runProcessWithTimeout` now injects `MSBUILDDISABLENODEREUSE=1` automatically whenever the command is `dotnet` (or `dotnet.exe`) and the caller hasn't set the key. Eliminates orphan `MSBuild.dll /nodemode:1` workers across daemon-spawned builds without requiring per-plugin opt-in. See `docs/msbuild-node-reuse-bug.md` for the reproduction (verified: 5 builds → 22 orphan workers without env, single-generation with).
- `FsHotWatch.PluginFramework.PluginCtxHelpers.reportOrClearFile` — collapses the per-file "if entries.IsEmpty then ClearErrors else ReportErrors" idiom shared by Lint, Analyzers, and FormatCheck.

### TestPrune: rerun history + IPC error formatting + silent-build diagnostic

#### Added
- TestPrune's `RerunQueued` branch now records the just-finished run's terminal Completed/Failed status before kicking off the rerun. Without this, the previous run's outcome was silently dropped from history.
- `FsHotWatch.Build.BuildPlugin.formatSilentFailureDiagnostic` — surfaces exit code, output size, and "Time Elapsed" tail when `dotnet build` exits non-zero with no parseable diagnostics (typically MSBuild bailing during evaluation/restore).
- CLI: `unwrapIpcException` peels `AggregateException` wrappers so `dotnet fs-hot-watch` surfaces the underlying OOM / Timeout instead of "One or more errors occurred".

### Per-task timeouts (cross-package)

#### Added
- `timeoutSec` configuration at three levels:
  - Top-level (`"timeoutSec": 120`) — default for plugins/projects that don't set their own.
  - Per-build-entry (`build.timeoutSec`) and per-file-command entry (`fileCommands[].timeoutSec`).
  - Per-test-project (`tests.projects[].timeoutSec`).
- `FsHotWatch.Events.RunOutcome.TimedOut of reason: string` — new variant recorded when a plugin's configured timeout fires.
- `FsHotWatch.ProcessHelper.ProcessOutcome` DU (`Succeeded` / `Failed of exitCode * output` / `TimedOut of after * tail`) replaces the historical `bool * string` return on `runProcessWithTimeout` / `runProcess`. Callers pattern-match instead of parsing a magic prefix from the output.
- `FsHotWatch.ProcessHelper.WorkOutcome<'a>` DU (`WorkCompleted` / `WorkTimedOut of after`) replaces `Result<'a, string>` on `runWithTimeout`.
- `FsHotWatch.Events.TestResult.TestsTimedOut of output * after * wasFiltered` — distinguishes timeout-killed test runs from regular failures. `TestResult.isTimedOut` helper added.
- `PluginCtx.CompleteWithTimeout reason` — lets a plugin flip its terminal outcome to `TimedOut` without introducing a new `PluginStatus` case. Backed by `PluginHostServices.SetNextTerminalOutcome` + `PluginActivity.SetNextTerminalOutcome`.
- Renderer: distinct `⏱` glyph in compact/verbose modes; `timed-out` token with `summary="timed out: …"` in agent mode.

#### Removed
- **BREAKING:** `FsHotWatch.ProcessHelper.TimedOutPrefix` literal. Pattern-match `ProcessOutcome` / `TestResult.TestsTimedOut` instead.

#### Behavior
- On timeout the daemon kills the process tree, records `TimedOut`, and keeps running. The next change retriggers normally.
- Plugins wired: `TestPrune` (per-project), `Build` (per build entry), `FileCommand` (per entry). Lint / Analyzers / Fantomas are in-process and use `Timeout.InfiniteTimeSpan` by default; timeout wrapping for those runs on a future change.

### Daemon shutdown reaps in-flight child processes

#### Added
- `FsHotWatch.ProcessRegistry` — per-daemon `AsyncLocal`-scoped registry of live `Process` handles. `Daemon.Dispose` calls `processRegistry.KillAll()` so `dotnet fs-hot-watch stop` no longer leaves orphan dotnet test runners (and their playwright drivers) competing with the next start.
- `Daemon.ProcessRegistry` (internal) — used by tests to track child processes against a daemon's registry without going through `runProcessWithTimeout`.

#### Fixed
- `runProcessWithTimeout` now registers the spawned process and unregisters in a `finally` block so daemon shutdown can tear it down even mid-call.

### Build plugin: skip-for-test-files-only no longer races FCS

#### Fixed
- `FsHotWatch.Build.BuildPlugin` test-only-skip path used to emit `BuildSucceeded` instantly, beating FCS to the file. Test-prune then dispatched off stale `AffectedTests` and skipped runs that should have happened.
- New `BuildPhase.WaitingForFcsPhase` variant: when `SourceChanged` carries only test files, the plugin transitions into a wait phase carrying the awaiting set (path-normalized via `Path.GetFullPath`) and emits `BuildSucceeded` only once every file has produced a `FileChecked`. Subscribes to `SubscribeFileChecked`.
- **BREAKING:** `BuildPhase` is a public DU; consumers that pattern-match on it must add a `WaitingForFcsPhase` case.

### FsHotWatch.Coverage (new package)

#### Added
- New `FsHotWatch.Coverage` NuGet package — checks per-file line and branch coverage
  thresholds after every `TestRunCompleted` event. Reads Cobertura XML produced by the
  test runner and compares against per-file thresholds in a `coverage-ratchet.json` config.
  Violations surface via `fshw errors`; thresholds are updated via `fshw coverage-ratchet`.
- `CoveragePlugin.create (configPath: string) (searchDir: string)` — factory function.
- IPC commands: `coverage-ratchet [configPath]` (update thresholds), `coverage-status`.
- `.fshw.json` `"coverage"` section: `{ "configPath": "...", "searchDir": "..." }`. Both
  fields are optional (defaults: `"coverage-ratchet.json"` and `"."`).
- Always merges partial runs into a coverage baseline (`mergeIntoBaselines`) before
  checking; replaces baseline wholesale (`refreshBaselines`) only after a passing
  full-suite run, so partial/impact-filtered runs accumulate coverage rather than resetting it.

### FsHotWatch (core)

#### Changed
- **BREAKING:** Test-lifecycle events split into three: `TestRunStarted` (once per run, with `RunId` + `StartedAt`), `TestProgress` (per-group delta with `RunId` + `NewResults`), and `TestRunCompleted` (once per run, with `TotalElapsed` + `Outcome` + final cumulative `Results`). All three share one `RunId` per run. Replaces the single `TestCompleted` event. `PluginEvent`, `SubscribedEvent`, `PluginDispatchEvent`, `PluginCtx<_>`, and `PluginHostServices` all updated.
- `TestResults` retained as a plain internal value type (for TestPrune internals + afterRun hooks); no longer dispatched as an event.

#### Added
- `TestRunOutcome` DU (`Normal` / `Aborted of reason`). Per-project pass/fail derived from `TestResult` values in `Results`.

### FsHotWatch.FileCommand

#### Added
- `afterTests` trigger: fires after a test run completes, optionally filtered by test project names.

#### Changed
- **BREAKING:** `FileCommandPlugin.create` takes a `CommandTrigger` record instead of positional `fileFilter` + `runOnStart` args.
- `afterTests` list-form fires iff **every** listed project is present. Combined with TestPrune's per-group progress emission, the command fires exactly once per run — on the first `TestProgress` whose cumulative accumulator covers every listed project, or on `TestRunCompleted` (cache replay) — and is unblocked by slow non-listed groups (e.g. integration tests).
- **BREAKING:** Subscribes to `SubscribeTestProgress` + `SubscribeTestRunCompleted` (not the removed `SubscribeTestCompleted`). Dedup is keyed on `RunId` via `FileCommandState.LastFiredRunId`.

#### Fixed
- Idempotency across back-to-back runs with identical project sets. The previous `Set.isSubset`-based batch-boundary heuristic silently skipped every run after the first when project sets were stable (the dominant case).

#### Removed
- `runOnStart` config/API field.

### FsHotWatch.TestPrune

#### Changed
- **Behavior:** `run-tests` IPC command (invoked by `fshw test`) now routes through the
  event machinery, emitting `TestRunStarted` → `TestProgress` × N → `TestRunCompleted`.
  Plugins subscribed to `SubscribeTestRunCompleted` (including `CoveragePlugin`) now
  observe manually-triggered runs the same way as daemon-triggered ones. No API change.
- **BREAKING:** `executeTests` emits the three-event lifecycle (`TestRunStarted` → `TestProgress` × N → `TestRunCompleted`) instead of the single `TestCompleted`. The abort path emits `TestRunStarted` + `TestRunCompleted(Aborted reason)` so subscribers see a coherent end to every run.
- The per-group accumulator is now a mutable `Map<string, TestResult>` under the emission lock (per-project `Map.add`) instead of rebuilding a `Map` from a `ResizeArray` on every emission.

### FsHotWatch.Cli

#### Added
- `--agent` / `-a` global flag for AI-agent-friendly parseable output: banner, `name: state [summary="..."]` per non-idle plugin, state-aware `next:` hint. States: `ok | fail | warn | running`. No ANSI.

#### Removed
- **BREAKING:** `coverage` config block.

#### Changed
- **BREAKING:** `--compact` / `-q` promoted to a global flag. `fs-hot-watch check -q` → `fs-hot-watch -q check`. Now accepted on every subcommand (including `status` and `errors`), matching the placement of `--verbose` and `--agent`.
- `fileCommands` entries accept `name` and `afterTests`; validation requires at least one of `pattern` / `afterTests` and an explicit `name` when `afterTests` is set. The config record now carries `PluginName: string` (derived at parse time) instead of `Name: string option`, eliminating a `failwith "unreachable"` fallback in the registration loop.
- Coverage output directory moves from the removed `coverage.directory` to `tests.coverageDir` (default `"coverage"`). Files are emitted at `<repoRoot>/<tests.coverageDir>/<project>/coverage.cobertura.xml`.

### FsHotWatch.Fantomas

#### Added
- Format preprocessor and format-check plugin respect `.gitignore` and `.fantomasignore`

---

## 2026-04-22 (`core-v0.8.0-alpha.8` · `testprune-v0.7.0-alpha.8` · `analyzers-v0.7.0-alpha.7` · `coverage-v0.7.0-alpha.7`)

### FsHotWatch 0.8.0-alpha.8

#### Added
- `PathFilter` module — shared path filtering with gitignore-style glob matching (via `Ignore` 0.2.1 package)
- `excludePatterns` parameter on `Daemon.create` / `Daemon.createWith` for excluding project trees from discovery
- `CheckPipeline.RegisterProject` filters out generated files in obj/ and bin/
- `IgnoreFilterCache` — caches .gitignore/.fantomasignore rules, auto-reloads on file changes
- `TaskCache.saltedCacheKey` / `optionalSaltedCacheKey` — cache-key builders that fold a per-event salt into the commit-based key, for plugins whose cache validity depends on state beyond the commit

#### Changed
- `performScan` takes `BatchContext` instead of 12 individual parameters
- Path filtering consolidated through `PathFilter` module (Watcher, CheckPipeline, Daemon)
- **BREAKING (IPC)**: `WaitForComplete` RPC now accepts a `timeoutMs: int` argument; `<= 0` means no client-imposed timeout. `DaemonRpcConfig.WaitForAllTerminal` signature changed from `unit -> Task<unit>` to `TimeSpan -> Task<unit>`.

#### Fixed
- `PluginFramework.registerHandler` now auto-reports `Failed(ex.Message, now)` when a handler's `Update` throws. Previously an uncaught throw after `ReportStatus(Running)` left the plugin stuck displaying `Running` indefinitely. Structural: no plugin author can forget it; impossible for a throw to leave the observed status non-terminal.

### FsHotWatch.Cli 0.8.0-alpha.8

#### Added
- `exclude` config field in `.fs-hot-watch.json` — gitignore-style glob patterns to exclude project trees
- `errors --wait [--timeout <seconds>]` — block until every tracked plugin reaches a terminal state before printing diagnostics

#### Fixed
- `start` is a singleton per repo, enforced by an OS exclusive lock on `.fs-hot-watch/daemon.lock` held for the daemon's lifetime; concurrent invocations cannot both proceed. Second invocation exits 0 with "Daemon already running at pipe <name> (pid <n>)".
- `stop` drains until the pipe is observed quiet for two consecutive probes (30 s overall timeout), cleanly taking down any number of historically-accumulated duplicate daemons and no longer misreporting "No daemon running" during pipe tear-down.

### FsHotWatch.TestPrune 0.7.0-alpha.8

#### Changed
- **BREAKING**: Bump `TestPrune.Core` 2.0.0 → 3.0.2. Adopts the revised `ITestPruneExtension` interface: extensions now implement `AnalyzeEdges` (returning `Dependency list` to inject into the graph) rather than `FindAffectedTests`. 3.0.2 also closes the pre-versioning stale-DB hole — `openCheckedConnection` recreates any DB where `user_version = 0` with existing user tables — so the schema-drift hang is prevented at both the Core and plugin layers.
- `AnalysisResult` construction now passes `Attributes` through from the analyzer (new field in `TestPrune.Core` schema v3).

#### Fixed
- **Stuck-state bug**: `flushAndQueryAffected` call sites in `BuildCompleted` and `TestsFinished (RerunQueued)` were unguarded; a DB hiccup pinned the plugin in `Running` forever. Both now report `Failed` and transition back to `TestsIdle` on exception.
- **Schema-drift self-heal**: SQLite "no such column" errors on a stale cache DB now trigger automatic deletion of the DB file with a warning, so the caller no longer has to know which file to remove.
- `affected-tests` command now updates on every `FileChecked` event rather than waiting for the next `BuildCompleted`.

### FsHotWatch.Analyzers 0.7.0-alpha.7

#### Changed
- Extracted `isKnownNonAnalyzerPrefix` and `buildAnalyzerProjectOptions` from `createCliContext` (internal) to enable deterministic unit tests for branches that live-SDK integration tests used to hit nondeterministically.

### FsHotWatch.Coverage 0.7.0-alpha.7

#### Fixed
- Cache key now carries a tristate salt derived from the thresholds file (absent / unreadable / content SHA-256), so editing `coverage-ratchet.json` under the same commit invalidates the cached plugin status, and a transient IO error on the thresholds file no longer presents as "file absent" to the cache.

### Tests / CI (this cycle)

- Split end-to-end FCS / analyzer / lint / format / build tests into a new `tests/FsHotWatch.IntegrationTests` project, excluded from the coverage aggregate to stabilize the ratchet.

---

## 0.5.0-alpha.1 (2026-04-12)

### FsHotWatch

#### Added
- Enable TransparentCompiler for hash-based deterministic FCS caching (`useTransparentCompiler = true`)
- Parse `#nowarn` directives to suppress FCS TransparentCompiler warnings (workaround for dotnet/fsharp#9796)
- Plugin teardown support in `PluginHandler`

#### Changed (Breaking)
- Type safety overhaul: `AbsFilePath`/`AbsProjectPath` single-case DUs replace raw strings; `PluginName` DU with uniqueness check; `ContentHash` wrapper; `CommandOutcome` DU replaces `Succeeded: bool` + `Output: string`; `FileCheckState` DU replaces `CheckResults option`; `AffectedTestsState` DU; `RerunIntent` DU; `Set<SubscribedEvent>` replaces `PluginSubscriptions` bool record; `TaskCacheKey` struct; `TestExtensionKind` DU; `CacheClearFilter` DU
- Plugin registration uses `PluginHostServices` record instead of multi-param function
- `Daemon` changed from F# record to class with `internal` constructor
- `IProjectGraphReader` interface decouples `BuildPlugin` from mutable `ProjectGraph`

#### Fixed
- Propagate cancellation token into `CheckFileCore` — `CancelPreviousCheck` now actually stops in-flight FCS checks
- Handle shared source files (linked items): `fileToProjects` now stores all projects per file; `GetProjectsForFile` returns all; Daemon checks shared files in each project context via `CheckFileWithOptions`
- `Daemon` implements `IDisposable` and stops all internal `MailboxProcessor` agents on dispose
- `RunWithIpc` races initial scan against cancellation to prevent test-process hangs
- Standalone files not in any project now checked via uncovered-files fallback

### FsHotWatch.Cli

#### Added
- Filter Info/Hint diagnostics from CLI output — only Error and Warning shown

#### Changed
- `DiagnosticEntry.Severity` typed as `DiagnosticSeverity` DU instead of string
- `startFreshDaemon` startup poll deadline configurable via `startupTimeoutSeconds` parameter (default: 30s)
- Process launch in `startFreshDaemon` injectable via `IpcOps.LaunchDaemon`
- Bump `CommandTree` 0.3.5 → 0.4.0, `TestPrune.Falco` 1.0.1 → 1.0.2

#### Fixed
- `renderIpcResult` crash on JSON containing array values (e.g. test results)
- Deduplicate `DisplayStatus`/`formatStatusLine`/error formatting — reuse `PluginStatus` from core and shared formatting from `RunOnceOutput`

### FsHotWatch.Analyzers

#### Changed
- Run parse-only analyzers (passing `null` for check results) instead of skipping files without full type-check results

### FsHotWatch.Lint

#### Changed
- Lint runner injectable via `lintRunner` parameter for testability

### FsHotWatch.TestPrune

#### Changed
- Bump `TestPrune.Core` 1.0.1 → 2.0.0 — cross-project extern symbol support

#### Fixed
- Comment-only source changes no longer add the file to `ChangedFiles` — only genuine AST changes propagate to extension-based tests
