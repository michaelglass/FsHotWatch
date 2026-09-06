# FsHotWatch.Cli

Command-line tool for the FsHotWatch daemon. It auto-starts the daemon
in the background when you run any command, so you don't need to manually
manage daemon lifecycle.

> **Status: early alpha, and a lot of it is AI-written.** Commands and flags
> shift between versions and rough edges are expected — your mileage may vary.
> Issues and PRs are very welcome.

## Install

```bash
dotnet tool install -g FsHotWatch.Cli
```

## Quick start

```bash
# The inner loop: run every check (build + lint + analyze + test + format-check)
# and report every error. Triggers a full run and blocks until it's done.
fshw check

# Start daemon in foreground (useful for debugging)
fshw start

# Observe plugin statuses / accumulated errors WITHOUT triggering a run
fshw status
```

`fshw check` is the single entry point — it folds the old per-plugin verbs
(`build`, `test`, `lint`, `analyze`, `format-check`, `errors`) into one
command. It runs every plugin, waits for genuine completion, and exits
non-zero on failures (exit 1) or when completeness cannot be confirmed
(exit 2). `fshw status` is the read-only observer: it reports the daemon's
current state without triggering anything.

## SQL test attribution extensions

`tests.extensions` composes explicit TestPrune dependency attribution into the
daemon's impact graph:

```json
{
  "tests": {
    "extensions": [
      {"type": "sql"},
      {"type": "sql-hydra", "generatedModulePrefix": "Intelligence.Database.Generated"}
    ]
  }
}
```

`sql` uses `AutoSqlExtension()` to discover `ReadsFrom` and `WritesTo`
attributes. `sql-hydra` uses `SqlHydraExtension(prefix)` and requires the full,
non-blank prefix before generated schema/table types. `sqlhydra` is accepted as
a compatibility alias. Unknown or incomplete entries fail configuration.

## Commands

| Command | Description |
|---------|-------------|
| `check [--run-once]` | **The inner loop.** Run every plugin (build + lint + analyze + test + format-check), wait for genuine completion, and report every error. Tests are impact-filtered — a latency optimization, and the output says so. Exits 0 (clean), 1 (failures), or 2 (completeness unconfirmed). `--run-once` uses an ephemeral daemon (for CI). |
| `confirm [--run-once]` | **Run the full suite and confirm `check` told the truth.** Same checks as `check`, but the tests run UNFILTERED — and a green is refused unless they actually did. Exits 0/1/2 as `check`, plus **3** (`unearned scope`: nothing failed, but the run did not produce the evidence a merge verdict is made of). See [Disagreement is a bug](#disagreement-is-a-bug). |
| `verdict` | **Read the last verdict** from `.fshw/verdict.json` and report whether it still applies to the tree on disk. Contacts no daemon, triggers no run — reading cannot perturb. Exits 0/1/2/3 as the verdict itself, plus **4** (STALE: the verdict describes a different tree) and **5** (no usable verdict). |
| `status [plugin]` | **The observer.** Show the daemon's current plugin statuses and accumulated errors WITHOUT triggering a run. Optionally filter to one plugin. |
| `start` | Start daemon in foreground (auto-scans on boot, Ctrl+C to stop). |
| `stop` | Gracefully stop the running daemon. |
| `scan` | Re-scan all files. |
| `test-rerun [opts]` | Rerun a slice of tests through the daemon, bypassing impact analysis. Options: `--filter-class <pattern>`, `--filter-trait <name=value>`. Daemon-only. Exits 0 (tests ran and passed), 1 (failures), or **3** (the run executed **no tests** — see below). |
| `format [--run-once]` | Run the Fantomas formatter on all files. |
| `rerun <plugin>` | Force a single plugin to re-run, clearing its cached state. |
| `invalidate` | Clear every cached task result for this workspace without stopping the warm daemon. Repository-side clean commands should call this after removing `bin/` or `obj/`. |
| `init` | Write a starter `.fshw.json` to the repo root. |
| `config check` | Validate `.fshw.json` without starting the daemon. Exits `0` on valid config, `2` on parse/validation error. |
| `coverage refresh-baseline` | Delete the coverage baseline + partial JSON so the next full run rebuilds it from scratch. |
| `dead-code [opts]` | Report unreachable symbols from entry points (TestPrune dead-code analysis). Options: `--entry <pattern>` (repeatable; replaces the defaults), `--include-tests`. |
| `completions` | Install fish shell completions. |
| `<command> [args]` | Run any plugin-registered command (e.g. `diagnostics`). |

### Disagreement is a bug

`check` is the fast inner loop and it is impact-filtered — it runs the tests a
heuristic selector *thinks* your change can affect. `confirm` runs the full suite.
Running both is therefore a **comparison**, and when they disagree, one of them is
wrong:

| What you see | What it means |
|---|---|
| Failed under `confirm`, never selected by `check` | **The selector MISSED a test.** A bug in impact analysis, not in the test. (Seen in the wild: a change whose entire content was browser-test edits got a green `check` in 1 ms, having run zero tests.) |
| Passed under `confirm`, but `check` says red | **A stale red, a flake, or a test-isolation defect** — a test that only passes *with company*, because another test sets up state it depends on. Here `check` is the honest one and the full suite is the liar. |

Neither direction is noise, and neither is "just re-run it". Both are defects worth
a ticket.

> **What "the full suite" means.** Every test project `.fshw.json` knows about — not
> necessarily every test project in your solution. A project that is in the solution
> but absent from `.fshw.json` is not run by `confirm`, and `confirm` does not claim
> otherwise.

> **Known limitation: a warm cache makes `confirm` exit 3, not 0.** On an *unchanged*
> tree whose previous result is still in `.fshw/cache/`, the test plugin **replays**
> the cached result instead of running (`… (cached)` in the output). A replay produces
> no test reports for the new run, so `confirm` sees "no tests ran", refuses to call it
> green, and exits **3**. That is the safe direction — it will not invent a verdict it
> did not earn — but it means a second `confirm` on an unchanged tree cannot go green
> until you clear the cache:
>
> ```bash
> fshw invalidate        # preserves the warm daemon; then re-run `fshw confirm`
> ```

### A run that executed nothing is not a pass

`test-rerun` exits **3**, never 0, when the run verified nothing. There are two causes
and they get different advice, because different evidence is available:

| Cause | What you see | Why the advice differs |
|---|---|---|
| The filter matched no test | `No tests matched the filter` + how many projects were searched | Those projects **did** discover their tests, so `fshw status test-prune` can show you the real names — almost always a typo or a renamed class. |
| No project was selected at all | `No test project ran` | Nothing ran, so **nothing was discovered**. There are no names to suggest, and offering some would be a guess. |

This is deliberately the same contract as `confirm`'s exit 3: **refuse to green without
evidence.** Exit 0 would sail through any `&&` chain and any CI gate, and a run that
verified nothing is exactly the run you least want reported as a pass.

The same rule reaches the *display*. A `check` whose test plugin selected zero projects
renders `⚠`, not `✓`, and reports `NOTHING VERIFIED: 0 test project(s) ran, no test
executed`. Agent mode tokens it `warn` (so `next:` points at `status`, never `done`) and
`plugins[]` in `.fshw/verdict.json` records `warn` rather than `ok`. The plugin's
**status** stays `Completed` on purpose — nothing failed, and reporting a failure would
turn an honest exit 3 (no verdict) into an exit 1 (failures found).

### The run finished and its answer was lost — exit 7

`check` and `confirm` exit **7** when the daemon **settled** — it built, ran the suite and
committed its evidence — and the CLI then failed before it could receive that result and
publish a verdict. It is the only non-zero code that says nothing about your code: the
work was done, and the answer was dropped carrying it back.

It is separate from **2** because the remedy is opposite. Exit 2 means *nothing was
verified — spend the time*. Exit 7 means *the time was already spent*: the run's own
output is on disk under `.fshw/test-runs/`, and re-running pays for a twenty-minute suite
to re-derive an answer that already exists. A retry loop that reads both as 2 does
exactly that. The verdict file is still written, so a finished run never leaves the
previous run's verdict standing as if it were current.

The cause this code was introduced for was a reply the daemon could not build: a broadly
red suite where every per-test diagnostic carried the whole project's captured output, so
the ledger's size was `failing tests × output` rather than their sum. That reply is now
bounded (`ErrorLedger.Transport`) and a memory fault is attributed to the process that
actually had it — the message says **DAEMON** or **CLI**, and never guesses.

### `waiting on build` — and why `fshw stop` is not the answer

`check` can report **`waiting on build`** and exit **2**: a test project's tests did not
run. Nothing was verified (not a pass) and nothing failed (not a red), which is why it
gets its own exit code.

**Read the message: it is one of two causes, and only one of them is worth retrying.**

* *the build artifact was not produced* — a build-ordering race. It settles on the next
  build, so an autonomous loop or deploy preflight should **retry**. The rest of this
  section is about that case.
* *`stale build output`* — the artifact exists and does not match what the tree says it
  should be. This one does **not** settle: retrying, `fshw confirm` and restarting
  the daemon each spend a full cycle to arrive back at the identical refusal, because
  the problem is bytes on disk rather than anything cached. The message names every
  affected project and the file that is stale; run `dotnet build`, and if it reports
  success while the refusal persists, the copy is timestamp-inverted and only
  `dotnet build --no-incremental` re-emits it. `fshw` repairs this itself where the
  repair is provable (a build-output copy whose origin is on disk) and says so by name;
  a refusal means it was not.

  One shape of it names `.deps.json` rather than a `.dll`, and it has a different
  remedy trap: the runtime dependency manifest is older than the
  `obj/project.assets.json` it is generated from, so the manifest lists the reference
  closure of a restore that has been superseded. A `dotnet restore` will **not** fix
  it — restore writes the assets file and never touches `bin/**/*.deps.json`, which is
  why this state outlives the automatic recovery that repairs the compile. Only a build
  regenerates it. Do not "fix" it by adding a direct `ProjectReference` to whatever
  failed to load: that puts an entry in the manifest and makes the symptom vanish while
  leaving the superseded restore exactly where it was.

If an otherwise-unchanged re-run says the FIRST one again, the build is serving a **cached result
its outputs no longer support**. The escape is:

```bash
fshw confirm           # forces a real build; will not replay a cached verdict
```

**Restarting the daemon does not clear this, and never did.** The task cache is
`FileTaskCache` — it lives on disk under `.fshw/` and survives `fshw stop`, a crash and
a reboot. Stopping the daemon throws away the warm compiler and then reinstates the
exact same cached answer, which is why the advice appeared to "sometimes work": you were
waiting out something else. If the underlying cause is genuinely stale build **output**,
`dotnet build` is the fix, and a copy MSBuild skipped on equal timestamps needs
`dotnet build --no-incremental`.
>
> A CI checkout starts cold, so CI does not hit this. Change any source file and the
> cache misses, the suite runs, and `confirm` decides normally.

### a red that is not about your tree — and when `fshw stop` IS the answer

The section above is about the build cache, which `fshw stop` cannot touch. This one is
about the daemon's **in-memory diagnostic ledger**, which is the only thing `fshw stop`
does clear — and the distinction matters, because the two look identical from outside.

Two kinds of failing diagnostic are not claims about the tree on disk at all:

* an FCS **`internal error:`** — the checker crashed, so it completed no analysis and
  found nothing. Under heavy churn these arrive in dozens, naming files you never
  touched, beside a build MSBuild compiled cleanly;
* a diagnostic against an absolute path **that is no longer on disk** — the ledger is
  still describing a tree you have already changed.

`fshw` classifies each one (`reddenedBy[].kind` in the verdict) and says so on the
`REDDENED` line. When **all** of them are of these kinds and no plugin failed, there is
no verdict: **exit 3**, not exit 1. The gate still refuses; it just stops claiming your
code is broken when it has no evidence that it is.

```bash
fshw stop              # then re-run. `fshw scan` does NOT clear these.
```

That is the honest answer to "is the `fshw stop` workaround still needed?": **for this
one class, yes** — the FCS faults are upstream and fshw cannot prevent them. What it can
do, and now does, is name the class and the remedy at the moment you hit it, instead of
leaving you to work out which of the two opposite responses this red wants.

## Options

| Flag | Description |
|------|-------------|
| `-v`, `--verbose` | Enable debug-level logging (same as `--log-level=debug`). |
| `--log-level=<level>` | Set log level: `error`, `warning`, `info`, `debug` (default: `info`). |
| `--no-cache` | Disable the on-disk task result cache. |
| `--no-warn-fail` | Treat warnings as non-fatal (errors still fail the check). |
| `-q`, `--compact` | One line per plugin instead of per-file detail. |
| `-a`, `--agent` | Agent-friendly parseable output with a next-step hint. |

## Examples

```bash
# Run every check (build + lint + analyze + test + format-check) and report errors
fshw check

# Rerun a single test class for investigation (xUnit v3 wildcards supported)
fshw test-rerun --filter-class "*CryptoTests*"

# Rerun only tests with a given trait
fshw test-rerun --filter-trait "Category=Browser"

# Combine filters (passed through to the xUnit v3 standalone runner)
fshw test-rerun --filter-class "*Repository*" --filter-trait "Speed=Fast"

# A typo in a filter is not a pass. This matches nothing and exits 3:
fshw test-rerun --filter-class "*RepositryTests"   # exit 3, not 0

# Show just the lint plugin's status
fshw status lint

# Query a plugin command directly
fshw diagnostics
fshw coverage
fshw warnings
```

## Machine-readable state (for agents and CI)

**Don't parse the CLI's output.** It is a progress display written for a human and
it will change. Every `check` and `confirm` publishes its result as a file instead.

### `.fshw/verdict.json` — the verdict

Written atomically (temp + rename, so a partial read is impossible) at the end of
every `check` and `confirm`, including the ones that fail, time out, or lose the
daemon mid-run — those are exactly the moments the human output is least
sufficient.

```json
{
  "schema": "fshw-verdict-v1",
  "producedAt": "2026-07-14T10:52:03.4471180Z",
  "command": "confirm",
  "producer": { "binary": "FsHotWatch.Cli.dll", "hash": "sha256 of the fshw that made this claim" },
  "runId": "24bf66063d004decb0447e3cc3ece719",
  "treeHash": "sha256:24bf6606…",
  "treeHashAlgorithm": "fshw-tree-sha256-v3",
  "treeFileCount": 144,
  "treeDeclaredCount": 29,
  "treeAbsentDeclarationCount": 0,
  "scope":   { "kind": "full", "ranProjects": 6, "totalProjects": 6 },
  "outcome": { "kind": "green",
               "baseline": { "kind": "full-suite-run", "runId": "24bf66063d004decb0447e3cc3ece719",
                             "earnedAt": "2026-07-14T10:50:31.0000000Z", "projects": 6 } },
  "exitCode": 0,
  "plugins": [
    { "name": "test-prune", "outcome": "ok", "elapsedMs": 91449,
      "summary": "6 passed, 0 failed in 6 projects" }
  ],
  "hooks": [
    { "scope": "tests.beforeRun", "stepIndex": 1, "stepCount": 2,
      "command": "dotnet restore", "elapsedMs": 4312, "outcome": "ok" }
  ],
  "timingSpans": [
    { "scope": "tests.beforeRun", "startOffsetMs": 0, "elapsedMs": 4312,
      "detail": "dotnet restore" }
  ],
  "timingIncompleteReasons": [],
  "observedElapsedMs": 102381,
  "invocationId": "8cc0715e5df6420dbe2157066dc0ac5c",
  "runs": [
    { "runId": "24bf66063d004decb0447e3cc3ece719",
      "suites": [
        { "project": "Intelligence.Tests.Unit",
          "ctrf": ".fshw/test-runs/24bf6606…/Intelligence.Tests.Unit.ctrf.json",
          "total": 5136, "passed": 5136, "failed": 0, "skipped": 0 } ] },
    { "runId": "9f21ba0c7f7e4a0e8a6b2d1c4e5f6071",
      "suites": [
        { "project": "Intelligence.Build.Tests",
          "ctrf": ".fshw/test-runs/9f21ba0c…/Intelligence.Build.Tests.ctrf.json",
          "total": 566, "passed": 566, "failed": 0, "skipped": 0 } ] }
  ],
  "suites": [
    { "project": "Intelligence.Tests.Unit",
      "ctrf": ".fshw/test-runs/24bf6606…/Intelligence.Tests.Unit.ctrf.json",
      "total": 5136, "passed": 5136, "failed": 0, "skipped": 0 },
    { "project": "Intelligence.Build.Tests",
      "ctrf": ".fshw/test-runs/9f21ba0c…/Intelligence.Build.Tests.ctrf.json",
      "total": 566, "passed": 566, "failed": 0, "skipped": 0 }
  ],
  "reddenedBy": [],
  "reddenedByCount": 0
}
```

**Where the wall time went.** `hooks[]` accounts for work outside the plugin run
records: each `tests.beforeRun` array element (in configured order, one atomic
command each) and the top-level run hooks as `run.beforeRun` / `run.afterRun`, each
with its position in its chain, exact command, outcome and elapsed milliseconds. Only
steps that RAN appear — the step after a fail-fast failure is absent, not zero.
`observedElapsedMs` is the wall time seen by the wrapping CLI, and `timingSpans[]`
places plugin and hook work on that one timeline, measured from the origin the
invocation captured before any hook ran. The human summary unions overlapping
intervals before reporting attributed time, so nested work is never counted twice.
`timingIncompleteReasons[]` names evidence that was missing, stale (a plugin run or
hook step from an earlier invocation), malformed or out of range — reported
separately from the attribution percentage, so a figure over refused evidence cannot
pass as complete. `invocationId` binds the CLI's own evidence to the verdict its run
produced: a concurrent invocation's verdict is never touched, and a run that ends by
exception, signal or without publishing leaves an invocation-owned `incomplete`
behind instead of a prior green. Older verdicts omit these fields and read back as
having no interval evidence.

**A red says what reddened it.** `reddenedBy` lists the failing ledger diagnostics the
exit code was computed from — `{ "source", "file", "severity", "message", "kind" }` — and
`reddenedByCount` is how many there were before the list was truncated to ten. `source`
is the ledger key, so a diagnostic that belongs to no plugin reports as `fcs`: that is
the whole point. A `confirm` once exited 1 with every plugin `ok` and 9,064 tests
passed, because ~51 FCS diagnostics were reddening it and no field named them. An empty
array on a red means the red came from a failing **plugin**, which `plugins[]` already
names.

**A red must be about this tree.** `kind` says whether each cause can be:

| `kind` | Meaning |
|--------|---------|
| `about-this-tree` | A genuine claim about the tree on disk. The red is earned. The default — nothing reaches the others without proof. |
| `vanished-file` | The diagnostic names an absolute path that is not on disk. The daemon is describing a tree that no longer exists. |
| `checker-fault` | An FCS `internal error:` — the checker crashed, so it made no finding at all. |

When **every** failing diagnostic is one of the latter two and no plugin failed, there
is no verdict to give: the outcome is `incomplete` and the exit code is **3**, not 1.
Nothing is reported broken — do not go looking for a defect — and nothing is reported
sound either. That state is cleared by `fshw stop`, and **`fshw scan` does not clear
it**; the tool says so on the spot rather than leaving it to be rediscovered. A single
cause that IS about this tree keeps the whole run a red, because one real defect
outranks any amount of stale state beside it.

**A missing number is never zero.** `elapsedMs` is `null` when a plugin produced no
measurement (`0` means "instantaneous" — a different fact). A suite entry whose counts
cannot be read makes the whole file **unreadable**, exactly as a missing `treeHash`
does: `"total": 0, "failed": 0` invented from a truncated file would read as "this
suite ran cleanly", and that is a vacuous green conjured out of nothing.

Every variant field is **uniformly tagged** — `kind` is always present, so you
never have to discriminate a JSON string from a JSON object before you can read a
field.

| Field | Values |
|-------|--------|
| `outcome.kind` | `green` (with `baseline`) · `red` · `incomplete` (with `reason`) |
| `outcome.baseline.kind` | `full-suite-run` (with `runId` / `earnedAt` / `projects`) · `no-test-suite` |
| `scope.kind` | `full` · `filtered` · `none` · `unknown` (with `ranProjects` / `totalProjects`) |
| `plugins[].outcome` | `ok` · `warn` · `fail` · `timed-out` · `running` |
| `command` | `check` (impact-scoped) · `confirm` (unfiltered, evidence-required) |

A `green` always names its **baseline**: the last run that executed every configured
test project, which is the run the tests this check *skipped* were last executed in.
An impact-filtered green is a claim about the whole suite only relative to that run —
the daemon's durable ledgers carry every symbol changed since (`pending-verification.json`)
and every test red since (`outstanding-failures.json`, re-selected on every run until it
passes) — so a green with nothing to be relative to is not representable. A check whose
daemon reports no baseline (a cold repository, or `tests.projects` grown since the last
full run) exits **3** with `incomplete` naming the reason, and the daemon widens its next
run to the full suite to earn one. `no-test-suite` is the one green a repository with no
test projects can make.

`incomplete` is the honest third answer: **nothing is known to be broken, and
nothing is known to be sound either.** A `confirm` whose tests ran impact-filtered
lands here. It is never laundered into a green.

### The one rule: a verdict applies only to the tree it verified, from the binary that verified it

**A green from a different tree is still a green** — and so is a green from a different
(older, buggier) fshw. Both are content-addressed:

> Read `verdict.json`. If `treeHash` ≠ hash(current tree), **or** `producer.hash` ≠ the
> fshw you would run now, **the verdict does not apply.** Wait, or trigger a run. Never
> reuse it.

Address the SUBJECT and the PRODUCER, or the provenance chain has a hole in the middle:
a stale daemon writes a verdict for an unchanged tree, the `treeHash` matches, and the
verdict reads as current.

You do not have to implement the hash. `fshw verdict` does the comparison for
you, reads no socket and starts nothing:

```bash
fshw verdict          # stdout: a JSON envelope; exit code: the answer
# 0 green · 1 red · 2 incomplete · 3 unearned scope · 4 STALE · 5 no verdict · 6 IN FLIGHT
#   (`check` adds 7 — the run finished and its result never reached the CLI)
```

Its stdout is *only* the envelope, which always states `applies` and `inFlight` — a
stale green can never be mistaken for a current one:

```json
{ "schema": "fshw-verdict-report-v1", "applies": false, "inFlight": false,
  "reason": "stale: the verdict describes a different tree",
  "currentTreeHash": "sha256:692e536c…",
  "verdict": { "…the file, verbatim…" } }
```

### Exit 6 — a run is in flight over this tree

The verdict is stamped **once, at completion**. Between a run's start and its publish,
the file holds the PREVIOUS run's result — and when the tree has not moved that result
parses cleanly, matches on `treeHash` and on producer, and reads **green**. Under
continuous verification, where something is nearly always running, that is most reads.

A run therefore CLAIMS the repo for its duration, one file per invocation under
`.fshw/in-flight/`, written before it starts and removed after its verdict is on disk.
`fshw verdict` reads those claims — no socket, no daemon, as before — and when one is
held by a run OTHER than the one that published the verdict, it reports:

```json
{ "schema": "fshw-verdict-report-v1", "applies": false, "inFlight": true,
  "reason": "in flight: a run is verifying THIS tree right now — check (pid 41207 on …",
  "verdict": { "…the previous run's file, verbatim…" } }
```

Exit **6**, and never the verdict's own code. Distinct from 4 because the consequence
is different: 4 means *the code moved, go and re-run*; 6 means *the answer is being
computed — wait, and do not start a second check*.

Three rules this obeys:

- **The existing staleness answers are untouched.** The in-flight question is asked only
  where the verdict would otherwise APPLY, so a moved tree, a different binary and a
  different hashing scheme are still 4, even during a run.
- **A run reads back its own verdict.** The claim carries the invocation id the verdict
  records as `attribution.invocationId`; a match is the run that published it, not a
  refusal.
- **A crashed run does not wedge the workspace.** A claim whose process is provably gone
  is abandoned and reaped by the next command. Every unknown — a foreign host, a claim
  file that will not parse — leans "in flight": what is unknown is WHO is running, never
  WHETHER anyone is.

See [ADR-019](../../docs/adr-019-a-verdict-is-current-only-when-no-other-run-is-in-flight.md).

If you'd rather compute the hash yourself, the recipe (`fshw-tree-sha256-v3`) is:

- take every file under `src/` and `tests/`, excluding `bin/`, `obj/`, tooling
  dirs, and your `.fshw.json` `exclude` patterns — **sources _and_ content/fixture
  files**;
- **plus the tool-known inputs**: `.fshw.json` itself, and the root-level toolchain
  and dependency files (`Directory.Build.props`, `Directory.Build.targets`,
  `Directory.Packages.props`, `global.json`, `nuget.config`, `paket.lock`,
  `paket.dependencies`);
- **plus every file your `.fshw.json` `verdictInputs.hashed` declares** — your
  coverage floors, your analyzer rules, your baselines. These are *not* filtered by
  `exclude` or by the `bin/`+`obj/` skip: a declaration outranks both;
- for each, in ordinal order of its repo-relative path, emit
  `relPath + NUL + sha256hex(bytes) + LF`;
- **plus one entry per directory the walk could not see** — unreadable, or nested
  past its depth cap — emitted as `relPath + "/" + NUL + "unhashable" + LF` and
  sorted in with the rest. A trailing `/` is a relative path no file can have, so a
  hole and a file never collide;
- **plus one entry per declaration that matched no file**, emitted as
  `"!verdict-input:" + declaredPath + NUL + "declared-but-absent" + LF`. A
  declaration that resolved to nothing must not contribute nothing, or a typo would
  silently return you to the older, narrower hash;
- `treeHash = "sha256:" + sha256hex(utf8(that))`.

Fixtures are in the hash on purpose. A changed JSON fixture that MSBuild declined
to re-copy once let a suite run green against the *old* fixture and put a red
commit on `main`. Content, never mtimes — see
[ADR-008](../../docs/adr-008-mtime-is-not-a-content-oracle.md).

Holes are in the hash for the same reason. A directory the walker could not read
used to contribute nothing at all, so a tree with a permission hole in it hashed
exactly like the same tree readable — and a green verdict earned over the part we
could see applied to the part we could not. `v2` makes "I could not look" a
different tree, which is what `treeFileCount` alone could not express: it made an
*empty* walk visible, never a *truncated* one.

The **rules** are in the hash from `v3`, for the third instance of the same shape.
Up to `v2` the hashed set was the walk plus one config file, so everything that
decides an answer from outside `src/`+`tests/` was omitted: lower a coverage floor,
edit an analyzer rule, flip `TreatWarningsAsErrors` off, and the green earned under
the **stronger** check still reported `Applies`. `v3` derives the set from one rule
— *a file belongs in the tree hash iff changing it can change what a check
concludes* — applying it to the files fshw can know about and letting the repo
declare the rest via `verdictInputs`. See
[Configuration](../../README.md#what-decides-the-verdict-is-what-is-hashed).

**`treeHashAlgorithm` is now read, not merely recorded.** A verdict whose algorithm
is not the one this build computes is `applies: false` with exit **4**, reported as a
different *scheme* rather than a different tree — a `v2` hash and a `v3` hash address
different sets of files, so comparing them as strings would answer a question nobody
asked. It is checked before the hashes are compared.

### `.fshw/test-runs/<runId>/` — per-suite detail. **The directory IS the run.**

Standard [CTRF](https://ctrf.io) from the test runner: per-test results with messages
and stack traces, plus a `results.summary` block. Every report a run produced lives in
that run's own directory, and nothing else does — so membership is **declared**, never
inferred from timestamps.

This makes the two pathological readings impossible:

| On disk | Means |
|---------|-------|
| `.fshw/test-runs/<runId>/` with reports | those reports, and only those, are that run's evidence |
| `.fshw/test-runs/<runId>/` **empty** | that run executed and ran **no tests** — a stated fact |
| no directory for the run | **no run happened** |

An empty listing used to be indistinguishable from "cleaned up" or "wrong glob". Two
capable readers misread it within an hour of each other, and one of them wrote a bash
harness because of it. Absence must never be something the reader has to decode.

The newest 10 run directories are retained; history is evidence, so old runs are
**rotated, never wiped on start**.

**One check writes SEVERAL of these directories, and the verdict names them all.**
A check runs the tests in batches — the impact-selected run, the rerun a mid-run
change queues behind it, `confirm`'s forced full suite, the drain of a queued
`run-tests` — and each batch gets its own run directory. `runId` names the batch the
verdict was **graded** from, which is only one of them; `runs[]` names **every** batch
the check ran, and `suites[]` is the flattening of `runs[]`, so it covers all of them
too.

So the question "did *my* test run in this check?" is answered by `runs[]` or
`suites[]`, never by opening the single directory `runId` points at. Reading `runId`
as the whole of the check is how three sessions concluded their tests had never run —
one landed on 566 tests out of the 10,979 that had actually executed — and two of them
nearly redid work that was already green. One project can appear **twice** in
`suites[]`, from two batches, with different counts; that is what happened, and the
totals then count test *executions*, not distinct tests.

And check the **full** class name when you search the reports: `Persona` matches
`Impersonation`, which hands you five confident hits from unrelated tests.

### `.fshw/heartbeat` — is the daemon still *working*?

`.fshw/daemon.pid` answers "is a daemon resident?". That is the wrong question: a
daemon can be alive and wedged, holding something while doing nothing. The
heartbeat answers the right one.

| | |
|---|---|
| **Path** | `<repoRoot>/.fshw/heartbeat` |
| **Format** | Unix epoch **seconds**, decimal ASCII, **no trailing newline** (same shape as `daemon.pid`) |
| **Cadence** | rewritten every **15 s** while a run is in progress |
| **Only while running** | an **idle daemon never beats** |
| **Never deleted** | between runs it holds the *previous* run's timestamp |
| **Absence / unparseable** | **UNKNOWN — never "stale"** |

```bash
# seconds since the daemon last announced it was working
echo $(( $(date +%s) - $(cat .fshw/heartbeat) ))
```

The beat is driven by whether work is **in flight** — not by log output — so a test
phase that runs for ten minutes in silence keeps beating throughout. Writes are
atomic, so a concurrent reader sees either the previous beat or this one, never a
torn or empty file. A beat that cannot be written is logged and swallowed: a daemon
must never die because it failed to announce itself.

Two rules for consumers:

- **15 s is the _floor_ of a staleness threshold, never the threshold itself.** A
  beat can be late on a saturated box or a slow disk.
- **Missing or unreadable means UNKNOWN — fall back to your own timeout**, never to
  "dead". Erring toward "still alive" costs a slow reclaim; erring toward "dead" lets
  two heavy runs proceed at once, which is worse. The file is never deleted precisely
  because a stale timestamp is a stronger, more actionable signal than absence.

Like `daemon.pid`, this is a fact fshw publishes about itself, with no opinion about
who reads it or why.

## Config validation

`.fshw.json` is parsed strictly: any parse or validation error
aborts startup with exit code `2` and a message naming the offending
field. Use `fshw config check` to validate without starting
the daemon (handy for editor integration and CI).

While the daemon is running, any write to `.fshw.json` causes
it to stop cleanly, logging the reason:

- Valid edit: `config changed, stopping (restart to apply)`
- Invalid edit: `config invalid, stopping: <parse error>`

Re-invoke the CLI to start a fresh daemon with the new config. There
is no hot-reload — symmetric stop-on-any-change avoids the race risks
of mid-flight plugin re-registration.

## How it works

The CLI computes a deterministic pipe name from your repo root, then
communicates with the daemon over named pipes (StreamJsonRpc). If the
daemon isn't running or its config has changed, the CLI automatically
starts/restarts it in the background.
