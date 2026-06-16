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
# The one gate: run every check (build + lint + analyze + test + format-check)
# and report every error. Triggers a full run and blocks until it's done.
fshw check

# Start daemon in foreground (useful for debugging)
fshw start

# Observe plugin statuses / accumulated errors WITHOUT triggering a run
fshw status
```

`fshw check` is the single gate — it folds the old per-plugin verbs
(`build`, `test`, `lint`, `analyze`, `format-check`, `errors`) into one
command. It runs every plugin, waits for genuine completion, and exits
non-zero on failures (exit 1) or when completeness cannot be confirmed
(exit 2). `fshw status` is the read-only observer: it reports the daemon's
current state without triggering anything.

## Commands

| Command | Description |
|---------|-------------|
| `check [--run-once]` | **The gate.** Run every plugin (build + lint + analyze + test + format-check), wait for genuine completion, and report every error. Exits 0 (clean), 1 (failures), or 2 (completeness unconfirmed). `--run-once` uses an ephemeral daemon (for CI). |
| `status [plugin]` | **The observer.** Show the daemon's current plugin statuses and accumulated errors WITHOUT triggering a run. Optionally filter to one plugin. |
| `start` | Start daemon in foreground (auto-scans on boot, Ctrl+C to stop). |
| `stop` | Gracefully stop the running daemon. |
| `scan` | Re-scan all files. |
| `test-rerun [opts]` | Rerun a slice of tests through the daemon, bypassing impact analysis. Options: `--filter-class <pattern>`, `--filter-trait <name=value>`. Daemon-only. |
| `format [--run-once]` | Run the Fantomas formatter on all files. |
| `rerun <plugin>` | Force a single plugin to re-run, clearing its cached state. |
| `init` | Write a starter `.fshw.json` to the repo root. |
| `config check` | Validate `.fshw.json` without starting the daemon. Exits `0` on valid config, `2` on parse/validation error. |
| `coverage refresh-baseline` | Delete the coverage baseline + partial JSON so the next full run rebuilds it from scratch. |
| `dead-code [opts]` | Report unreachable symbols from entry points (TestPrune dead-code analysis). Options: `--entry <pattern>` (repeatable; replaces the defaults), `--include-tests`. |
| `completions` | Install fish shell completions. |
| `<command> [args]` | Run any plugin-registered command (e.g. `diagnostics`). |

## Options

| Flag | Description |
|------|-------------|
| `-v`, `--verbose` | Enable debug-level logging (same as `--log-level=debug`). |
| `--log-level=<level>` | Set log level: `error`, `warning`, `info`, `debug` (default: `info`). |
| `--no-cache` | Disable the on-disk task result cache. |
| `--no-warn-fail` | Treat warnings as non-fatal (errors still fail the gate). |
| `-q`, `--compact` | One line per plugin instead of per-file detail. |
| `-a`, `--agent` | Agent-friendly parseable output with a next-step hint. |

## Examples

```bash
# Run the full gate (build + lint + analyze + test + format-check) and report errors
fshw check

# Rerun a single test class for investigation (xUnit v3 wildcards supported)
fshw test-rerun --filter-class "*CryptoTests*"

# Rerun only tests with a given trait
fshw test-rerun --filter-trait "Category=Browser"

# Combine filters (passed through to the xUnit v3 standalone runner)
fshw test-rerun --filter-class "*Repository*" --filter-trait "Speed=Fast"

# Show just the lint plugin's status
fshw status lint

# Query a plugin command directly
fshw diagnostics
fshw coverage
fshw warnings
```

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
