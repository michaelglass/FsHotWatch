# AGENTS.md — using fshw

`fshw` is a daemon that keeps F# checks (build, lint, analyzers, format, tests)
warm so they return in milliseconds. If this repo has it installed, use it
instead of shelling out to `dotnet build` / `dotnet test` / `dotnet fantomas` /
`dotnet fsharplint` — those will lag the daemon and waste tokens.

Don't restart the daemon to "refresh" — it re-checks on save. After an edit,
wait ~500ms before querying.

## Global flags

- `-a` / `--agent` — parseable, no ANSI, `name: state [summary="..."]` per plugin, with a `next:` hint. Use this.
- `-q` / `--compact` — terse human output
- `--verbose` — full output

Placement-independent (`fshw -a check` ≡ `fshw check -a`).

The verbs that matter:

- **`check`** — the fast inner loop. Triggers a full run and blocks until done;
  exit 0 = green. The tests are **impact-filtered**: a green says nothing you
  changed broke anything the selector chose to look at, not that the whole suite
  is green.
- **`confirm`** — run the FULL suite and confirm `check` told the truth. Same
  checks, but the tests run unfiltered, and a green is refused (exit 3) unless they
  actually did. This is what CI runs.

**Which verb gates a merge is YOUR project's policy, not fshw's.** The two verbs
report different strengths of evidence and say honestly which one they produced;
neither of them decides your workflow. If your repo has a `CLAUDE.md`, that is
where the rule lives, and fshw will not contradict it.
- **`status`** — the observer. Reports current state without triggering anything.
- **`verdict`** — read the last verdict from `.fshw/verdict.json`. No socket, no
  run: reading cannot perturb.

The old per-plugin verbs (`build`, `test`, `lint`, `analyze`, `format-check`,
`errors`) were folded into `check`.

**When `check` and `confirm` disagree, that is a BUG — never noise:**

- *failed under `confirm`, never selected by `check`* → the selector **MISSED** a
  test. An impact-analysis bug, not a test bug.
- *passed under `confirm`, but `check` says red* → a stale red, a flake, or a
  **test-isolation defect** (a test that only passes with company, because another
  test sets up state it depends on). There, `check` is the honest one.

"Full suite" means every test project `.fshw.json` knows about — not necessarily
every test project in the solution.

## Workflows

**Did my edit break anything? / Confirm clean before claiming done.**
`fshw -a check`. It runs every plugin and blocks until done. Exit 0 = green; 1 = failures; 2 = completeness unconfirmed;
3 = no verdict — nothing failed, but the daemon holds no full-suite baseline for the tests
this run skipped (a cold repository, or `tests.projects` grew). It widens its next run to
earn one: run `check` again, or `confirm`.

**A plugin looks unhappy — inspect without re-running.**
`fshw -a status` lists `name: state` per plugin (`ok` / `fail` / `warn` / `running`). Drill in with `fshw status <plugin>` (`build`, `test-prune`, `analyzers`, `lint`, `format`).

**Test failed — was it flaky?**
`fshw flaky-tests` returns the top tests by `transitions / (n-1)` over the last 20 runs. Score > 0 → suspect; rerun before debugging.

**Cached output looks stale (e.g. coverage didn't re-run after a config edit).**
`fshw rerun <plugin>` clears the cache key and re-fires. fshw also auto-warns `cached output may be stale → run fshw rerun <plugin>` when arg-file mtimes outpace the last run — heed it.

**A repository clean removed `bin/` / `obj/`.**
`fshw invalidate` clears every cached task result for this workspace without stopping
the daemon. Use it from repo-side clean commands; the next check rebuilds while FCS
stays warm.

**Renamed / moved / added files and they aren't being checked.**
`fshw scan` re-discovers the tree. Edits to `.fshw.json` trigger this automatically.

**Plugin stuck in `Running`.**
Long test/build. `fshw status <plugin>` shows progress. Wait — don't kill the daemon.

**`Daemon not running` message.**
Fine. Any command auto-starts it. `—` (em-dash) state = idle, not broken.

## Don't

- Run `dotnet build` / `test` / `fantomas` / `fsharplint` directly — results will lag fshw's cache.
- Kill or restart the daemon to "force a refresh" — `fshw check` does that without losing warm state.
- Edit files in `.fshw/` — it's daemon state (cache, pid, lock, test history, logs).
