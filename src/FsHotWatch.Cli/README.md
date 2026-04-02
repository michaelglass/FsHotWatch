# FsHotWatch.Cli

Command-line tool for the FsHotWatch daemon. It auto-starts the daemon
in the background when you run any command, so you don't need to manually
manage daemon lifecycle.

## Install

```bash
dotnet tool install -g FsHotWatch.Cli
```

## Quick start

```bash
# Run all checks (scan, build, lint, errors)
fs-hot-watch check

# Start daemon in foreground (useful for debugging)
fs-hot-watch start

# Check plugin statuses
fs-hot-watch status
```

## Commands

| Command | Description |
|---------|-------------|
| `start` | Start daemon in foreground (auto-scans on boot, Ctrl+C to stop). |
| `stop` | Gracefully stop the running daemon. |
| `scan [--force]` | Re-scan all files. `--force` bypasses the jj fingerprint guard. |
| `scan-status` | Check scan progress without blocking. |
| `status [plugin]` | Show plugin statuses. Optionally filter to one plugin. |
| `build` | Trigger a build and wait for completion. |
| `test [opts]` | Run tests. Options: `-p project`, `-f filter`, `--only-failed`. |
| `format` | Run Fantomas formatter on all files. |
| `lint` | Run FSharpLint on all files and report warnings. |
| `errors` | Show current errors from all plugins. |
| `check` | Full check: scan all files, wait for plugins, then report errors. |
| `invalidate-cache <file>` | Clear cache for a file and re-check it. |
| `<command> [args]` | Run any plugin-registered command (e.g. `diagnostics`, `coverage`). |

## Options

| Flag | Description |
|------|-------------|
| `-v`, `--verbose` | Show per-file status transitions (same as `--log-level=debug`). |
| `--log-level=<level>` | Set log level: `error`, `warning`, `info`, `debug` (default: `info`). |
| `--no-cache` | Disable the check result cache. |

## Examples

```bash
# Run tests for a specific project
fs-hot-watch test -p MyApp.Tests

# Run only previously-failed tests
fs-hot-watch test --only-failed

# Show just the lint plugin's status
fs-hot-watch status lint

# Query a plugin command directly
fs-hot-watch diagnostics
fs-hot-watch coverage
fs-hot-watch warnings
```

## How it works

The CLI computes a deterministic pipe name from your repo root, then
communicates with the daemon over named pipes (StreamJsonRpc). If the
daemon isn't running or its config has changed, the CLI automatically
starts/restarts it in the background.
