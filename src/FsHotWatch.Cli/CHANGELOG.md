# Changelog — FsHotWatch.Cli

Note: CLI versions release together with the core package under the `core-v` tag (no separate `cli-v` tag prefix).

## Unreleased

### Added
- `fs-hot-watch config check` — validates `.fs-hot-watch.json` without starting the daemon. Exits `0` on valid config, `2` on parse/validation error. Intended for editor integration and CI.

### Changed
- **BREAKING (behavioral):** `.fs-hot-watch.json` parse and validation errors now abort startup with exit code `2` and a message naming the offending field. Previously, any parse failure was logged and the daemon silently ran with defaults. `fileCommands` validation failures (missing `pattern`/`afterTests`, `afterTests` without `name`) surface through the same exit-code-2 path.
- While the daemon is running, any write to `.fs-hot-watch.json` now stops it cleanly and logs the reason (`config changed, stopping (restart to apply)` for valid edits, `config invalid, stopping: <error>` for parse failures). Restart the daemon to pick up the new config. No hot-reload.

## 0.8.0-alpha.9 - 2026-04-23

### Added
- `--agent` / `-a` global flag: parseable, token-minimal output for AI coding agents. Emits a one-line banner, `name: state [summary="..."]` per non-idle plugin, and a state-aware `next:` hint (e.g. `next: fs-hot-watch --agent build` when the build fails). States: `ok | fail | warn | running`. No ANSI, idle plugins omitted. Diagnostic output (`errors --agent`) uses the format `<plugin>:<file>:<line>:<col>: <severity> <message>`.

### Removed
- **BREAKING:** `coverage` config block no longer accepted. Coverage enforcement now flows through `fileCommands` with `afterTests`, invoking an external CLI (e.g. `coverageratchet`).
- `FsHotWatch.Coverage` project dependency (retired).
- `runOnStart` field on `fileCommands` entries (see FsHotWatch.FileCommand CHANGELOG).

### Changed
- **BREAKING:** `--compact` / `-q` is now a global flag, not a per-command flag. Invocation changes from `fs-hot-watch check -q` to `fs-hot-watch -q check`. Matches the placement of other global flags (`--verbose`, `--agent`). Accepted on every subcommand, including `status` and `errors`, which previously didn't support it.
- `fileCommands` entries accept `name` (string) and `afterTests` (`true` or string list) fields. An entry must set at least one of `pattern` / `afterTests`; entries with `afterTests` must have an explicit `name`.
- Coverage output directory is now configured via `tests.coverageDir` (default `"coverage"`). Previously lived on the removed top-level `coverage.directory`. Per-project opt-out via `tests.projects[].coverage = false` unchanged.

- chore: bump upstream tool versions

## 0.8.0-alpha.8 (2026-04-22)

### Added

- `errors --wait [--timeout <seconds>]` — block until every tracked plugin reaches a terminal state before printing diagnostics. Timeout is enforced server-side by the daemon's `waitForAllTerminal` loop, so timeout messages include the list of plugins still running. Exit codes: `0` clean, `1` failures, `2` timeout or invalid flag combination (e.g. `--timeout` without `--wait`). Default timeout 600s.

### Fixed

- `start` is now a singleton per repo, enforced by an OS-level exclusive file lock on `.fs-hot-watch/daemon.lock` held for the daemon's lifetime. Two concurrent `start` invocations cannot both acquire the lock, so duplication is impossible rather than just unlikely. The second invocation exits `0` with `Daemon already running at pipe <name> (pid <n>)`. Previously, repeated `start` invocations could race past the probe-based guard and accumulate concurrent daemons serving stale results.
- `stop` drains running daemons until `IsRunning` returns `false` on two consecutive probes (bounded by a 30 s overall timeout), reporting the count stopped (or `No daemon running` if none). The fixed 10-attempt cap used previously could leave orphans when more duplicates had accumulated, and the fixed single-probe termination could misreport "No daemon running" while the OS was still tearing down the last pipe endpoint.

## 0.8.0-alpha.3 (2026-04-18)

### Added

- `exclude` config field in `.fs-hot-watch.json` — array of gitignore-style glob patterns to exclude entire project trees from discovery (e.g. `["vendor/"]`)
- Pass `config.Exclude` to `Daemon.create` for project-level exclusion

---

## 0.5.0-alpha.1 (2026-04-12)

### Added

- Filter Info/Hint diagnostics from CLI output — only Error and Warning shown in both daemon and run-once modes

### Changed

- `DiagnosticEntry.Severity` typed as `DiagnosticSeverity` DU instead of string in `IpcOutput`
- `startFreshDaemon` startup poll deadline now configurable via `startupTimeoutSeconds` parameter (default: 30s)
- Process launch in `startFreshDaemon` injectable via `IpcOps.LaunchDaemon` for testing
- Bump `CommandTree` 0.3.5 → 0.4.0, `TestPrune.Falco` 1.0.1 → 1.0.2
- Deduplicate `DisplayStatus` type — reuse `PluginStatus` from core `Events` module
- Deduplicate `formatStatusLine`/error formatting — reuse `RunOnceOutput.formatStepResult` and `RunOnceOutput.formatErrors`
- File/process operations injectable via `FileOps`/`ProcessOps` records for testability

### Fixed

- `renderIpcResult` crash (`InvalidOperationException`) on JSON containing array values (e.g. test results with `projects` array)
- Guard `statusMap` fallback against non-string JSON values

---

## 0.3.0-alpha.1 (2026-04-08)

### Bug fixes

- **Breaking:** `run-once` positional subcommand replaced by `--run-once` flag (e.g. `check --run-once`, `build --run-once`) — uses CommandTree flag list support
- Fix `build --run-once` not running the build plugin — `stripConfig` was discarding the build config; it is now restored
- Fix `format-check` (run-once and daemon mode) always reporting "No errors" — was querying plugin errors under name `"format"` instead of `"format-check"`
- Fix `check` (daemon mode) hanging forever when a `file-cmd-*` plugin stayed Idle — Idle is now treated as terminal in `pollAndRender`
- Fix daemon auto-start when running as a `dotnet` local tool — `computeLaunchCommand` now detects the dotnet binary and constructs `dotnet tool run fs-hot-watch`

### Improvements

- Extract `isRunOnce` helper and `withDaemon` guard in `executeCommand` to remove repetition
- Fix `format` (daemon mode) to pass result through `renderIpcResult` consistently with other commands
- Avoid redundant `IsRunning` probe at end of `startFreshDaemon` polling loop

### Infrastructure / CI

- CLI moved under core's shared tag in `semantic-tagger.json` — no longer versioned separately
- Bump internal tooling: `coverageratchet` 0.10.0-alpha.1, `syncdocs` 0.10.0-alpha.1, `fssemantictagger` 0.10.0-alpha.1, `fsprojlint` 0.7.0-alpha.1
- Bump `CommandTree` 0.3.3 → 0.3.5 (flag list support)

---

## 0.2.0-alpha.1 (2026-04-07)

First release of CLI under the shared release cycle (previously first released as 0.1.0-alpha.1 in the 0.2.0-alpha.1 monorepo release).

- Add MIT license; add SourceLink; enable `TreatWarningsAsErrors`; replace bespoke scripts with shared NuGet tools and reusable CI workflows
- Add `--version` flag
- Bump `CommandTree` 0.3.2 → 0.3.3
- Bump `TestPrune.Falco` 0.1.0-beta.1 → 1.0.1

---

## 0.1.0-alpha.3 (2026-04-02)

CLI completely rewritten.

- **Breaking:** CLI completely rewritten to use `CommandTree` library — hand-written `parseCommand` removed
- **Breaking:** CLI output changed from JSON on stdout to colored text on stderr — update any scripts parsing CLI output
- **Breaking:** `AnalyzeCheck` command renamed to `Analyze`
- **Breaking:** `ScanStatus` removed as standalone command — integrated into `Scan`
- **Breaking:** `PluginCommand` removed from Command DU — unknown commands handled via error path
- Add `--no-warn-fail` global flag — warnings don't cause non-zero exit codes
- Add `IpcOutput` module for parsing and colored rendering of IPC responses
- Add `pollAndRender` for live progress display in daemon mode
- Add fish shell completions via `fs-hot-watch completions`

---

## 0.1.0-alpha.2 (2026-03-28)

- **Breaking:** `DaemonConfiguration.Build` changes from single build config to `list option` — supports multiple build steps
- **Breaking:** `DaemonConfiguration.Format` changes from `bool` to `FormatMode` DU (`Off | Auto | Check`)
- Add `--run-once` flag on subcommands: `start`, `build`, `test`, `format`, `lint`, `analyze`
- Add `fs-hot-watch init` — generates `.fs-hot-watch.json` from discovered projects
- Add format `"check"` mode — read-only format checking without modifying files
- Add test extensions config (e.g., Falco route mapping)
- Add `coverage.afterCheck` config option

### Migration from 0.1.0-alpha.1

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]

// "format" accepts string mode instead of bool:
"format": "auto"   // or "check" or "off" (booleans still work)
```
