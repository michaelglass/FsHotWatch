# AGENTS.md — using fshw

The daemon keeps F# checks warm. Don't shell out to `dotnet build` / `dotnet test` /
`dotnet fantomas` / `dotnet fsharplint` — they're already running and the cache is
authoritative. Don't restart the daemon to "refresh" — it re-checks on save.

After an edit, wait ~500ms before querying.

## Global flags

- `-a` / `--agent` — parseable, no ANSI, `name: state [summary="..."]` per plugin, with a `next:` hint
- `-q` / `--compact` — terse human output
- `--verbose` — full output

All three are global; placement-independent (`fshw -a errors` ≡ `fshw errors -a`).

## Workflows

**Did my edit break anything?**
`fshw -a errors` — exit 0 = clean. Skip diagnostics under `obj/` / `bin/` (already filtered).

**Confirm clean before claiming done.**
`fshw check` — forces a full re-run. Exit 0 = green.

**One plugin looks unhappy.**
`fshw -a status` to see who's `fail` / `running`, then `fshw status <plugin>` for detail (`build`, `test-prune`, `analyzers`, `lint`, `format`).

**Test failed — was it flaky?**
`fshw flaky-tests` — top 10 by `transitions / (n-1)` over the last 20 runs. Score > 0 → suspect.

**Cached output looks stale (e.g. coverage didn't re-run after a config edit).**
`fshw rerun <plugin>` clears the plugin's cache key and re-fires it. The CLI also auto-warns `cached output may be stale → run fshw rerun <plugin>` when arg-file mtimes outpace the last run.

**Renamed / moved / added files and the daemon hasn't picked them up.**
`fshw scan` — re-discovers the project tree. (Config edits on `.fshw.json` trigger this automatically.)

**Coverage gate is red on CI.**
`mise run check` auto-corrects local thresholds in both directions. CI gates via `coverage-check` (no auto-correct), so a Linux-only drift won't show locally — loosen the threshold in `coverage-ratchet-FsHotWatch.json` under `"platform": "linux"`.

**Plugin stuck in `Running`.**
Long test/build. `fshw status <plugin>` shows progress. Don't kill the daemon — it finishes.

**`Daemon not running` but commands work.**
Fine. Commands auto-start. `—` (em-dash) state means idle, not broken.

## Don't

- Run dotnet tools directly (results will lag the daemon's).
- Commit / push (use `jj` directly; this repo uses Jujutsu, not git).
- Edit files via the `format` preprocessor — it's the only thing fshw mutates, and only on save.
