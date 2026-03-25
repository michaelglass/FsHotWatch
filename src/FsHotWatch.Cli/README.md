# FsHotWatch.Cli

CLI tool for the FsHotWatch daemon.

## Commands

- `fs-hot-watch start` — start daemon in foreground
- `fs-hot-watch stop` — gracefully stop the daemon
- `fs-hot-watch scan` — trigger a full re-scan
- `fs-hot-watch scan-status` — check scan progress
- `fs-hot-watch status [plugin]` — show plugin statuses
- `fs-hot-watch <command> [args]` — run a plugin-registered command

## Install

```bash
dotnet tool install -g FsHotWatch.Cli
```
