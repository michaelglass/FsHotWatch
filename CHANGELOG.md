# Changelog

All notable changes to FsHotWatch packages are documented here.

This is a monorepo — all packages are released together from the same commit.
Tag prefixes: `core-v`, `build-v`, `testprune-v`, `analyzers-v`, `lint-v`, `fantomas-v`, `coverage-v`, `filecommand-v`, `cli-v`.

## 0.2.0-alpha.1 (2026-04-07)

Packaging and infrastructure release. No public API changes beyond dependency bumps.

### All packages

- Add MIT license
- Add SourceLink for source debugging (`Microsoft.SourceLink.GitHub`)
- Enable `TreatWarningsAsErrors` on Build, Coverage, FileCommand, Cli projects
- Replace bespoke F# scripts with shared NuGet tools (`coverageratchet`, `syncdocs`, `fssemantictagger`) and reusable CI workflows

### FsHotWatch (core)

- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1
- Bump `Ionide.ProjInfo` / `Ionide.ProjInfo.FCS` 0.68.0 → 0.74.2
- Bump `FSharp.Data.Adaptive` 1.2.16 → 1.2.26

### FsHotWatch.TestPrune

- Bump `TestPrune.Core` 0.1.0-beta.1 → 1.0.1

### FsHotWatch.Cli (first release: 0.1.0-alpha.1)

- Add `--version` flag
- Bump `CommandTree` 0.3.2 → 0.3.3
- Bump `TestPrune.Falco` 0.1.0-beta.1 → 1.0.1

### Migration from 0.1.0-alpha.3

- Update `TestPrune.Core` dependency to 1.0.1 (check for API changes in that library)
- Update `Ionide.ProjInfo` to 0.74.2 (may affect project loading behavior)
- No FsHotWatch API signature changes

---

## 0.1.0-alpha.3 (2026-04-02)

Severity-aware diagnostics, FCS warning reporting, MSBuild diagnostic parsing, CLI rewrite.

### FsHotWatch (core)

- **Breaking:** `ErrorLedger.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `ErrorLedger.Count()` removed → use `FailingReasons(warningsAreFailures: bool)` which returns `Map<string, (string * ErrorEntry) list>`
- **Breaking:** `PluginHost.HasErrors()` removed → use `HasFailingReasons(warningsAreFailures: bool)`
- **Breaking:** `PluginHost.ErrorCount()` removed → use `FailingReasons(warningsAreFailures: bool)`
- **Breaking:** IPC method `GetErrors` renamed to `GetDiagnostics` (both server and client)
- **Breaking:** `Daemon.create` gains `fcsSuppressedCodes: int list option` parameter (pass `None` for default — suppresses FS1182)
- **Behavioral change:** FCS now reports all diagnostic severities (Error, Warning, Info, Hidden), not just errors. Warnings will appear in the error ledger. Use `--no-warn-fail` or filter by severity if this is unwanted.
- Add `ErrorEntry.isFailing` helper for severity-aware failure checks

### FsHotWatch.Build

- Build plugin now reports parsed MSBuild diagnostics via new `BuildDiagnostics.parseMSBuildDiagnostics` — structured `ErrorEntry` items with severity, file, line, column instead of raw text

### FsHotWatch.Lint

- Guard against concurrent lint operations

### FsHotWatch.Cli

- **Breaking:** CLI completely rewritten to use `CommandTree` library — hand-written `parseCommand` removed
- **Breaking:** CLI output changed from JSON on stdout to colored text on stderr — update any scripts parsing CLI output
- **Breaking:** `AnalyzeCheck` command renamed to `Analyze`
- **Breaking:** `ScanStatus` removed as standalone command — integrated into `Scan`
- **Breaking:** `PluginCommand` removed from Command DU — unknown commands handled via error path
- Add `--no-warn-fail` global flag — warnings don't cause non-zero exit codes
- Add `IpcOutput` module for parsing and colored rendering of IPC responses
- Add `pollAndRender` for live progress display in daemon mode
- Add fish shell completions via `fs-hot-watch completions`

### Migration from 0.1.0-alpha.2

```fsharp
// ErrorLedger: before
ledger.HasErrors()
ledger.Count()

// ErrorLedger: after
ledger.HasFailingReasons(false)       // errors only
ledger.HasFailingReasons(true)        // errors + warnings
ledger.FailingReasons(false)          // get failing entries

// PluginHost: same pattern
host.HasFailingReasons(false)
host.FailingReasons(false)

// IPC client: before
IpcClient.getErrors proxy

// IPC client: after
IpcClient.getDiagnostics proxy

// Daemon.create: add fcsSuppressedCodes parameter
Daemon.create(config, plugins, fcsSuppressedCodes = None)
```

**Behavioral change:** FCS warnings now appear in the error ledger. If your workflow only expected errors, either:
- Pass `warningsAreFailures = false` to `HasFailingReasons`/`FailingReasons`
- Use `--no-warn-fail` CLI flag
- Filter `ErrorEntry` items by severity

---

## 0.1.0-alpha.2 (2026-03-28)

Subcommands, `--run-once` mode, `CommandCompleted` events, build dependencies, config enhancements.

### FsHotWatch (core)

- **Breaking:** `PluginEvent<'Msg>` gains `CommandCompleted of CommandCompletedResult` case — exhaustive matches must handle it
- **Breaking:** `PluginSubscriptions` gains `CommandCompleted: bool` field — direct construction must include it (or use `PluginSubscriptions.none`)
- **Breaking:** `PluginCtx` gains `EmitCommandCompleted: CommandCompletedResult -> unit` field
- **Breaking:** `CachedEvent` gains `CachedCommandCompleted` case
- Add `CommandCompletedResult` type and full event pipeline
- Add `Daemon.RunOnce()` for single-pass in-process mode

### FsHotWatch.Build

- **Breaking:** `BuildPlugin.create` gains required `dependsOn: string list` parameter (pass `[]` for no dependencies)
- **Breaking:** `BuildState` gains `SatisfiedDeps` and `PendingFiles` fields
- Build dependency ordering — build waits for named `CommandCompleted` events before starting

### FsHotWatch.FileCommand

- **Breaking:** `FileCommandPlugin.create` gains required `runOnStart: bool` parameter (pass `false` for previous behavior)
- FileCommandPlugin now emits `CommandCompleted` events after each run

### FsHotWatch.Cli (DaemonConfig)

- **Breaking:** `DaemonConfiguration.Build` changes from single build config to `list option` — supports multiple build steps
- **Breaking:** `DaemonConfiguration.Format` changes from `bool` to `FormatMode` DU (`Off | Auto | Check`)
- Add `--run-once` flag on subcommands: `start`, `build`, `test`, `format`, `lint`, `analyze`
- Add `fs-hot-watch init` — generates `.fs-hot-watch.json` from discovered projects
- Add format `"check"` mode — read-only format checking without modifying files
- Add test extensions config (e.g., Falco route mapping)
- Add `coverage.afterCheck` config option

### FsHotWatch.TestPrune

- Fix: move `ReportStatus` to `TestsFinished` handler to eliminate race condition

### Migration from 0.1.0-alpha.1

```fsharp
// PluginSubscriptions: add CommandCompleted field
{ PluginSubscriptions.none with FileChanged = true; CommandCompleted = false }

// BuildPlugin.create: add dependsOn parameter
BuildPlugin.create(buildTemplate, dependsOn = [])

// FileCommandPlugin.create: add runOnStart parameter
FileCommandPlugin.create(pattern, command, args, runOnStart = false)
```

Handle new union cases in exhaustive matches:
```fsharp
match event with
| FileChanged _ -> ...
| BuildCompleted _ -> ...
| CommandCompleted _ -> ()  // new
// etc.
```

Config file changes:
```jsonc
// "build" can now be an array of build steps:
"build": [{ "command": "dotnet", "args": "build", "dependsOn": [] }]

// "format" accepts string mode instead of bool:
"format": "auto"   // or "check" or "off" (booleans still work)
```

---

## 0.1.0-alpha.1 (2026-03-21)

Initial alpha release of all packages (except CLI).

### FsHotWatch (core)

- Daemon with FSharpChecker warm cache
- File watcher with source/project/solution change detection
- Plugin host with event dispatch and command registration
- IPC server/client over named pipes (StreamJsonRpc)
- Preprocessor pipeline (format-on-save runs before events dispatch)
- Debounced file changes (500ms source, 200ms project)
- ProjectGraph for cross-project dependency tracking
- TaskCache for event deduplication

### FsHotWatch.Build

- Build plugin wrapping `dotnet build` with concurrent-build guard

### FsHotWatch.TestPrune

- Test impact analysis via symbol dependency graph
- Test execution with configurable test project configs

### FsHotWatch.Coverage

- Coverage threshold checking from Cobertura XML reports

### FsHotWatch.Analyzers

- F# analyzer host using reflection-based `CliContext` (FCS version mismatch workaround)

### FsHotWatch.Lint

- FSharpLint integration using warm FCS parse results via `lintParsedSource`

### FsHotWatch.Fantomas

- Format preprocessor (rewrites files before events dispatch)
- Format check plugin (read-only validation)

### FsHotWatch.FileCommand

- Run arbitrary shell commands when specific file patterns change
