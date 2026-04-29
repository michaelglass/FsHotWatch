# AGENTS.md — using fs-hot-watch

Terse playbook for AI agents working in a repo where `fs-hot-watch` is installed.
It keeps the F# compiler warm so checks take milliseconds, not minutes.

## Ground rules

- Don't run `dotnet build`, `dotnet test`, `dotnet fantomas`, or `dotnet fsharplint`
  directly unless fshw is unavailable. The daemon already ran them; its
  cached results are authoritative and cheaper.
- Don't restart the daemon to "get fresh results" — it re-checks on file save.
- After edits, let the daemon debounce (~500ms) before querying status.

## Core commands

| Command | Use |
|---|---|
| `fshw status` | One-line state per plugin — look for non-idle entries first |
| `fshw status <plugin>` | Deeper detail for `build`, `test-prune`, `analyzers`, `lint`, `format` |
| `fshw errors` | Accumulated diagnostics across all plugins; exit code `0` = clean, non-zero = errors present |
| `fshw check` | Force a full re-run of all checks; use before claiming work is done |
| `fshw scan` | Re-scan the project tree (new files, renames) |

All commands start the daemon automatically if it isn't running.

## Find errors

```
fshw errors
```

Output groups diagnostics by file and plugin. Each entry has severity
(`error` / `warning`), location (`file:line:col`), message, and source plugin.
Exit code is non-zero iff at least one `error` is present.

If `errors` is clean but something still feels wrong, `fshw status` may
show a plugin in `Failed` state (infrastructure problem, not a code diagnostic).

## Fix workflow

1. `fshw errors` → pick the highest-severity diagnostic at a real source
   location (skip generated `obj/` / `bin/` paths — they're filtered anyway).
2. Edit the file.
3. Wait ~1s, then `fshw errors` again. The diagnostic should be gone or
   reduced; new ones may appear as dependents re-check.
4. When `errors` is clean, run `fshw check` once to confirm a full pass.

If a specific plugin regressed (lint/format/coverage), query it directly:
`fshw status lint` gives lint-specific detail.

## Common gotchas

- **Stale diagnostics after a rename/move**: `fshw scan` to rediscover.
- **Config change (`.fshw.json`) not picked up**: the daemon auto-restarts
  on config edits — wait 1-2s and re-query.
- **Plugin stuck in `Running`**: likely a long test or build; `fshw status <plugin>`
  shows progress. Don't kill the daemon — tests finish on their own.
- **"Daemon not running" but commands work**: fine. Commands auto-start; status
  of `—` (em-dash) means idle, not broken.

## What fshw does NOT do

- It doesn't run arbitrary commands — use `file-command` plugin for that.
- It doesn't commit or push — use git/jj directly.
- It doesn't modify your code — only the `format` preprocessor does, and only on save.
