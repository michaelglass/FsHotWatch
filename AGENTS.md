# AGENTS.md — using fshw

`fshw` is a daemon that keeps F# checks (build, lint, analyzers, format, tests)
warm so they return in milliseconds. If this repo has it installed, use it
instead of shelling out to `dotnet build` / `dotnet test` / `dotnet fantomas` /
`dotnet fsharplint` — those will lag the daemon and waste tokens.

Don't restart the daemon to "refresh" — it re-checks on save. After an edit,
wait ~500ms before querying.

## Global flags

- `-a` / `--agent` — ONE compact JSON line on stdout (`{"fshw":"fshw-agent-v1","command","exit","outcome","scope","reasons",...}`), nothing on stderr; the human output goes to `.fshw/agent-output.log`, which the line names. Use this.
  A repo whose `.fshw.json` sets `"agent": true` gets it with no flag; `--no-agent` opts back out.
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
  actually did. This is what CI runs. On an unchanged tree it answers from the
  evidence already earned; `confirm --fresh` launches a new full-suite run instead
  (for timing or tracing a run — not needed for a verdict).

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
every test project in the solution. A project in `tests.excluded` is not run by either
verb; its `reason` says what runs it instead (a slow end-to-end suite typically runs
after `confirm`, in the full verification, and never in `check`).

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

**`check` says NO VERDICT: stale daemon state (a type incompatible with itself).**
The daemon already re-checked those files once for this tree before grading, keeping the
test evidence it holds, and they survived. Run `fshw stop`, then `check` again; a new
daemon starts without that evidence, so its first check runs the suite.

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

## Merging to this repository's `main`

The sections above are about USING fshw. This one is the rule for changing FsHotWatch
itself (`CLAUDE.md` is gitignored here, so the rule lives in this file).

Before every push to `main` — a merge, a re-land, a revert — run BOTH:

1. `mise run ci` — the full-suite `confirm` plus the integration suite, on this machine.
2. `mise run ci-linux` — GitHub Actions' CI jobs (`build`, `integration`, `lint-project`)
   in a local Linux container, with the workflow's SDK band, coverage floors, `CI` /
   `GITHUB_ACTIONS` environment and a non-root user. Needs a Docker daemon (Docker
   Desktop, OrbStack or colima). Exit 0 = every job green; it prints each step's exit and
   duration and leaves logs, coverage and `.fshw/test-runs` in `.ci-linux/`.

Do not push `main` unless both exit 0. `mise run ci` alone is not enough: every
FsHotWatch CI red inspected 2026-10-05..28 (14 runs) passed on macOS and failed only on
Linux — coverage floors that measure differently there, real-process tests, wall-clock and
cancellation tests — and all four reverts of that period were Linux-only failures.

- `CI_LINUX_JOBS=build mise run ci-linux` runs one job; `mise run ci-linux -- <checkout>`
  runs another checkout (any revision, including one older than the script).
- ci-linux derives its steps from `.github/workflows/ci.yml` and transcribes the shared
  reusable workflows; it REFUSES to run (exit 2) when either upstream file changed since
  the transcription. Update `scripts/ci-linux/steps.sh` to match, then the pinned blob ids
  in `scripts/ci-linux.sh`. Do not set `CI_LINUX_ACCEPT_WORKFLOW_DRIFT=1` to get past it.
- It runs on the host's architecture (arm64 on Apple silicon); GitHub's runner is x64. The
  ANSI long-running-report red that GitHub hit on bc0918d7 (fixed by 5ed72a5f) reproduces on
  arm64, but an x64-only difference is possible. `CI_LINUX_PLATFORM=linux/amd64` emulates
  x64 when it might.
- After pushing, watch the commit's GitHub CI run to the end anyway. A local green is
  strong evidence, not the verdict; if GitHub goes red, revert first and diagnose after.
