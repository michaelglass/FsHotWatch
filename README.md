<!-- sync:intro:start -->
# FsHotWatch

Speed up your F# development feedback loop.

FsHotWatch is a background daemon that watches your source files and
keeps the F# compiler warm. When you save a file, it instantly re-checks
it and tells your tools (linters, analyzers, test runners) what changed
— without restarting the compiler from scratch each time.
<!-- sync:intro:end -->

## The problem

F# tools are slow because they each start their own compiler from zero.
A 15-project solution takes ~2 minutes to analyze. Every time you save a
file, your linter restarts, your analyzer restarts, your test runner
restarts — all parsing and type-checking the same 500 files again.

## How FsHotWatch helps

FsHotWatch runs one compiler in the background and shares it with all
your tools:

1. **You save a file** — FsHotWatch notices the change
2. **It re-checks just that file** using the compiler that's already warm
   (milliseconds, not minutes)
3. **Plugins get the results instantly** — your linter, analyzer, and test
   runner all see the updated check results without re-parsing anything
4. **You query the results** — `fshw status` shows what each tool found

Changes are debounced — if you save 10 files in quick succession (like
a formatter running), FsHotWatch waits for things to settle, then
processes them all in one batch.

## Quick start

```bash
# Install the CLI tool
dotnet tool install -g FsHotWatch.Cli

# Start the daemon in your repo (runs in foreground, Ctrl+C to stop)
fshw start

# From another terminal, check what's happening
fshw status

# Run all checks; verbose by default (per-subtask progress + last-run recap)
fshw check

# Prefer one line per plugin?
fshw check --compact   # or -q
```

## Packages

FsHotWatch is split into small packages so you only install what you need:

| Package | What it does |
|---------|-------------|
| [`FsHotWatch`](src/FsHotWatch/) | Core library — the daemon, file watcher, plugin system, IPC |
| [`FsHotWatch.Cli`](src/FsHotWatch.Cli/) | CLI tool — `fshw start/stop/status` |
| [`FsHotWatch.TestPrune`](src/FsHotWatch.TestPrune/) | Plugin: figures out which tests to run when code changes |
| [`FsHotWatch.Analyzers`](src/FsHotWatch.Analyzers/) | Plugin: runs F# analyzers (like [G-Research](https://github.com/G-Research/fsharp-analyzers) or your own) |
| [`FsHotWatch.Lint`](src/FsHotWatch.Lint/) | Plugin: runs FSharpLint using the warm compiler's results |
| [`FsHotWatch.Fantomas`](src/FsHotWatch.Fantomas/) | Plugin: checks if your files are formatted with Fantomas |
| [`FsHotWatch.Build`](src/FsHotWatch.Build/) | Plugin: runs `dotnet build` and emits BuildCompleted events |
| [`FsHotWatch.FileCommand`](src/FsHotWatch.FileCommand/) | Plugin: runs custom commands when specific files change |

## Writing your own plugin

Plugins use a declarative framework: you define an update function that
receives events and returns new state. The framework manages the agent,
status tracking, and IPC command registration.

```fsharp
open FsHotWatch.Events
open FsHotWatch.PluginFramework

type MyState = { FilesChecked: int }

let myPlugin: PluginHandler<MyState, unit> =
    { Name = "my-plugin"
      Init = { FilesChecked = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    // result.ParseResults and result.CheckResults come from
                    // the warm FSharpChecker — no re-parsing needed.
                    printfn "Checked: %s" result.File
                    ctx.ReportStatus(Completed(System.DateTime.UtcNow))
                    return { FilesChecked = state.FilesChecked + 1 }
                | _ -> return state
            }
      Commands =
        [ "my-status",
          fun state _args ->
              async { return $"checked %d{state.FilesChecked} files" } ]
      Subscriptions =
        { PluginSubscriptions.none with
            FileChecked = true }
      CacheKey = None }

// Register with the daemon:
daemon.RegisterHandler(myPlugin)
```

**Available events (subscribe via `Subscriptions`):**
- `FileChanged` — a source file was saved (you get the file paths)
- `BuildCompleted` — `dotnet build` finished (success or failure)
- `FileChecked` — a file was type-checked (you get parse + check results)
- `TestCompleted` — tests finished running (you get per-project results)

**What you can do in Update via `ctx`:**
- `ctx.Checker` — the warm FSharpChecker (reuse it for your own analysis)
- `ctx.RepoRoot` — path to the repository root
- `ctx.ReportStatus(status)` — tell the daemon your current status
- `ctx.ReportErrors(file, entries)` — report diagnostics to the error ledger
- `ctx.EmitBuildCompleted(result)` — emit events to other plugins
- `ctx.Post(msg)` — send a custom message back to your own agent
- `ctx.StartSubtask(key, label)` / `ctx.EndSubtask(key)` — surface named concurrent work
  with live per-subtask elapsed in `fshw check` output
- `ctx.Log(msg)` — preferred logging path; appends to the activity tail shown under
  your plugin in `check`, and also routes to `Logging.info`
- `ctx.CompleteWithSummary(summary)` — override the auto-derived summary captured
  in run history on the next terminal transition (e.g. "built 4 projects")

Status transitions are fully observed: when a plugin moves to `Completed` or
`Failed`, the host snapshots current subtasks + activity into a bounded run
history (per plugin), visible under the `check` verbose output as
`started / elapsed / summary` on the next run.

## Configuration

Create `.fshw.json` in your repo root. All fields are optional — sensible defaults are used when omitted.

```json
{
  "build": {
    "command": "dotnet",
    "args": "build"
  },
  "format": true,
  "lint": true,
  "cache": "file",
  "tests": {
    "beforeRun": "dotnet build",
    "projects": [
      {
        "project": "MyProject.Tests",
        "command": "dotnet",
        "args": "run --project tests/MyProject.Tests --no-build --",
        "filterTemplate": "--filter-class {classes}",
        "classJoin": " ",
        "group": "unit"
      }
    ]
  },
  "analyzers": {
    "paths": ["analyzers/"]
  },
  "fileCommands": [
    {
      "pattern": "*.fsx",
      "command": "dotnet",
      "args": "fsi --typecheck-only"
    }
  ]
}
```

### Configuration reference

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `build` | `object \| bool` | `{"command": "dotnet", "args": "build"}` | Build command. `false` disables. |
| `format` | `bool` | `true` | Enable Fantomas format-on-save preprocessor. |
| `lint` | `bool` | `true` | Enable FSharpLint plugin. Uses `fsharplint.json` if found. |
| `cache` | `string \| bool` | `"file"` | Cache strategy: `"none"`, `"memory"`, or `"file"`. (`"jj"` is accepted as a legacy alias for `"file"`.) |
| `tests` | `object` | — | Test runner config. See below. |
| `coverage` | `object` | — | Coverage threshold checking. |
| `analyzers` | `object` | — | F# Analyzers SDK integration. |
| `fileCommands` | `array` | `[]` | Custom commands triggered by file patterns. |
| `timeoutSec` | `int` | — | Global default per-task timeout in seconds. Used when a plugin/project has no per-entry override. |

**Per-task timeouts.** Any of `build[]`, `tests.projects[]`, and `fileCommands[]` entries may set their own `timeoutSec` to override the global default. When a task exceeds its timeout, the daemon kills the child process tree, records the run with outcome `timed out` (distinct `⏱` glyph in the UI, `timed-out` token in agent mode), and stays running — the next change retriggers normally.

**`build` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `command` | `string` | `"dotnet"` | Build command. |
| `args` | `string` | `"build"` | Arguments to the build command. |
| `buildTemplate` | `string` | — | Template for incremental builds. `{projects}` is replaced with changed project paths. |

**`tests` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `beforeRun` | `string` | — | Command to run before each test run (e.g. `"dotnet build"`). |
| `projects` | `array` | `[]` | List of test project configurations. |

**`tests.projects[]` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `project` | `string` | `"unknown"` | Project name (used for filtering and display). |
| `command` | `string` | `"dotnet"` | Test runner command. |
| `args` | `string` | `"test --project <project>"` | Arguments to the test runner. |
| `group` | `string` | `"default"` | Group name (for running subsets). |
| `environment` | `object` | `{}` | Extra environment variables as `"KEY": "VALUE"` pairs. |
| `filterTemplate` | `string` | — | Template for class-based filtering. `{classes}` is replaced with affected test class names. |
| `classJoin` | `string` | `" "` | Separator for joining class names in the filter. |

**`analyzers` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `paths` | `string[]` | — | Directories containing analyzer DLLs. Relative paths resolved from repo root. |

**`fileCommands[]` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `pattern` | `string` | `"*.fsx"` | File extension pattern to match (e.g. `"*.fsx"`, `"*.sql"`). |
| `command` | `string` | `"echo"` | Command to run when a matching file changes. |
| `args` | `string` | `""` | Arguments to the command. |

### Cache directory

FsHotWatch stores check result caches and the TestPrune database in `.fshw/` at the repository root. Add this to your `.gitignore`:

```
.fshw/
```

The cache directory contains:
- `cache/` — Cached FCS check results for faster cold starts
- `test-impact.db` — TestPrune dependency analysis database
