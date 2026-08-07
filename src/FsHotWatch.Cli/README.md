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
> rm -rf .fshw/cache     # then re-run `fshw confirm`
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
>
> A CI checkout starts cold, so CI does not hit this. Change any source file and the
> cache misses, the suite runs, and `confirm` decides normally.

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
  "treeHashAlgorithm": "fshw-tree-sha256-v1",
  "treeFileCount": 144,
  "scope":   { "kind": "full", "ranProjects": 6, "totalProjects": 6 },
  "outcome": { "kind": "green" },
  "exitCode": 0,
  "plugins": [
    { "name": "test-prune", "outcome": "ok", "elapsedMs": 91449,
      "summary": "6 passed, 0 failed in 6 projects" }
  ],
  "suites": [
    { "project": "Intelligence.Tests.Unit",
      "ctrf": ".fshw/test-runs/24bf6606…/Intelligence.Tests.Unit.ctrf.json",
      "total": 5136, "passed": 5136, "failed": 0, "skipped": 0 }
  ]
}
```

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
| `outcome.kind` | `green` · `red` · `incomplete` (with `reason`) |
| `scope.kind` | `full` · `filtered` · `none` · `unknown` (with `ranProjects` / `totalProjects`) |
| `plugins[].outcome` | `ok` · `warn` · `fail` · `timed-out` · `running` |
| `command` | `check` (impact-scoped) · `confirm` (unfiltered, evidence-required) |

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
# 0 green · 1 red · 2 incomplete · 3 unearned scope · 4 STALE · 5 no verdict
```

Its stdout is *only* the envelope, which always states `applies` — a stale green
can never be mistaken for a current one:

```json
{ "schema": "fshw-verdict-report-v1", "applies": false,
  "reason": "stale: the verdict describes a different tree",
  "currentTreeHash": "sha256:692e536c…",
  "verdict": { "…the file, verbatim…" } }
```

If you'd rather compute the hash yourself, the recipe (`fshw-tree-sha256-v1`) is:

- take every file under `src/` and `tests/`, excluding `bin/`, `obj/`, tooling
  dirs, and your `.fshw.json` `exclude` patterns — **sources _and_ content/fixture
  files** — plus `.fshw.json` itself;
- for each, in ordinal order of its repo-relative path, emit
  `relPath + NUL + sha256hex(bytes) + LF`;
- `treeHash = "sha256:" + sha256hex(utf8(that))`.

Fixtures are in the hash on purpose. A changed JSON fixture that MSBuild declined
to re-copy once let a suite run green against the *old* fixture and put a red
commit on `main`. Content, never mtimes — see
[ADR-008](../../docs/adr-008-mtime-is-not-a-content-oracle.md).

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
