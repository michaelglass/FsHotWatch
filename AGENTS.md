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

Placement-independent (`fshw -a errors` ≡ `fshw errors -a`).

## Workflows

**Did my edit break anything?**
`fshw -a errors`. Exit 0 = clean.

**Confirm clean before claiming done.**
`fshw check` forces a full re-run. Exit 0 = green.

**A plugin looks unhappy in `errors` / `status`.**
`fshw -a status` lists `name: state` per plugin (`ok` / `fail` / `warn` / `running`). Drill in with `fshw status <plugin>` (`build`, `test-prune`, `analyzers`, `lint`, `format`).

**Test failed — was it flaky?**
`fshw flaky-tests` returns the top tests by `transitions / (n-1)` over the last 20 runs. Score > 0 → suspect; rerun before debugging.

**Cached output looks stale (e.g. coverage didn't re-run after a config edit).**
`fshw rerun <plugin>` clears the cache key and re-fires. fshw also auto-warns `cached output may be stale → run fshw rerun <plugin>` when arg-file mtimes outpace the last run — heed it.

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
