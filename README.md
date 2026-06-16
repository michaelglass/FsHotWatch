<!-- sync:intro:start -->
# FsHotWatch

Trying to speed up the F# development feedback loop.

FsHotWatch is a background daemon that watches your source files and aims to
keep the F# compiler warm, so saving a file re-checks just what changed and
hands the results to your tools (linters, analyzers, test runners) — instead of
each tool restarting the compiler from scratch every time.
<!-- sync:intro:end -->

> **Status: early alpha, and a lot of it is AI-written.** It runs the author's
> own daily F# work, but behavior and APIs shift between versions, rough edges
> are expected, and your mileage may vary. The goal is a faster F# loop — it's
> still finding its shape, so issues and PRs are very welcome.

## The problem

F# tools are slow because each one starts its own compiler from zero. A
15-project solution can take ~2 minutes to analyze. Every save restarts your
linter, your analyzer, and your test runner — all re-parsing and type-checking
the same hundreds of files again.

## How it works

FsHotWatch runs one compiler in the background and shares it with all your tools:

1. **You save a file** — FsHotWatch notices.
2. **It re-checks just that file** using the already-warm compiler — ideally milliseconds rather than minutes.
3. **Plugins get the results instantly** — your linter, analyzer, and test runner see the new check results without re-parsing.
4. **You query the results** — `fshw check` runs everything and reports what each tool found.

Saves are debounced: if 10 files change at once (a formatter sweeping the repo,
say), FsHotWatch waits for things to settle and processes them in one batch.

## Quick start

```bash
# Install the CLI
dotnet tool install -g FsHotWatch.Cli

# Run all checks. This auto-starts the daemon the first time —
# no separate "start" step needed. Verbose by default.
fshw check

# Prefer one line per plugin?
fshw check --compact   # or -q
```

`fshw init` writes a starter `.fshw.json`; see [Configuration](#configuration).

## Commands

| Command | What it does |
|---------|--------------|
| `fshw check` | Run all configured checks and report findings. Auto-starts the daemon. `--run-once` runs without a daemon; `-q`/`--compact` for one line per plugin. |
| `fshw status [plugin]` | Show the daemon's current status (optionally for one plugin). |
| `fshw start` | Run the daemon in the foreground (Ctrl+C to stop). Optional — `check`/`status` start it for you. |
| `fshw stop` | Stop the running daemon. |
| `fshw format` | Format the code (Fantomas). |
| `fshw test-rerun` | Rerun tests for an xUnit v3 `--filter-class` / `--filter-trait` slice. |
| `fshw rerun <plugin>` | Force one plugin to re-run, clearing its cached state. |
| `fshw init` | Generate a starter `.fshw.json`. |
| `fshw config check` | Validate `.fshw.json` without starting the daemon. |

Add `-v` for debug logging or `-a` for agent-friendly, parseable output. Run
`fshw --help` for the full list.

## Packages

FsHotWatch is split into small packages so you install only what you need:

| Package | What it does |
|---------|-------------|
| [`FsHotWatch`](src/FsHotWatch/) | Core library — the daemon, file watcher, plugin system, IPC |
| [`FsHotWatch.Cli`](src/FsHotWatch.Cli/) | CLI tool — `fshw check`, `start`, `stop`, `status`, … |
| [`FsHotWatch.TestPrune`](src/FsHotWatch.TestPrune/) | Plugin: figures out which tests to run when code changes |
| [`FsHotWatch.Analyzers`](src/FsHotWatch.Analyzers/) | Plugin: runs F# analyzers (like [G-Research](https://github.com/G-Research/fsharp-analyzers) or your own) |
| [`FsHotWatch.Lint`](src/FsHotWatch.Lint/) | Plugin: runs FSharpLint using the warm compiler's results |
| [`FsHotWatch.Fantomas`](src/FsHotWatch.Fantomas/) | Plugin: checks if your files are formatted with Fantomas |
| [`FsHotWatch.Build`](src/FsHotWatch.Build/) | Plugin: runs `dotnet build` and emits BuildCompleted events |
| [`FsHotWatch.FileCommand`](src/FsHotWatch.FileCommand/) | Plugin: runs custom commands when specific files change |
| [`FsHotWatch.Coverage`](src/FsHotWatch.Coverage/) | Plugin: checks per-file line/branch coverage thresholds after each test run |

## Configuration

Run `fshw init` to scaffold a `.fshw.json` in your repo root, or write one by
hand. Every field is optional — sensible defaults apply when omitted.

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
    "dependsOn": ["src/MyApp/Database/Migrations/**"],
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
  ],
  "coverage": {
    "configPath": "coverage-ratchet.json",
    "searchDir": "coverage"
  }
}
```

### Reference

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `build` | `object \| bool` | `{"command": "dotnet", "args": "build"}` | Build command. `false` disables. |
| `format` | `bool` | `true` | Enable Fantomas format-on-save preprocessor. |
| `lint` | `bool` | `true` | Enable FSharpLint plugin. Uses `fsharplint.json` if found. |
| `cache` | `string \| bool` | `"file"` | Cache strategy: `"none"`, `"memory"`, or `"file"`. (`"jj"` is a legacy alias for `"file"`.) |
| `tests` | `object` | — | Test runner config. See below. |
| `coverage` | `object` | — | Coverage threshold checking. |
| `analyzers` | `object` | — | F# Analyzers SDK integration. |
| `fileCommands` | `array` | `[]` | Custom commands triggered by file patterns. |

For memory/idle-exit, FSEvents latency, and per-task timeout keys, see
[Memory & tuning](docs/memory-and-tuning.md).

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
| `dependsOn` | `string[]` | `[]` | Repo-root-relative globs (`*`, `?`, `**`) naming **external** test inputs the symbol-diff can't see — DB migrations, generated files, schemas. Their content hash salts the test cache key, so editing a matched file forces a real test re-run even when no test source changed. |
| `coverageDir` | `string` | `"coverage"` | Directory (repo-root-relative) for per-project Cobertura artifacts. |
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

**`coverage` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configPath` | `string` | `"coverage-ratchet.json"` | Path to the coverage-ratchet thresholds file (relative to repo root or absolute). |
| `searchDir` | `string` | `"."` | Directory tree to search for `coverage.cobertura.xml` files after each test run. |

### Cache directory

FsHotWatch keeps its check-result cache and the TestPrune database in `.fshw/`
at the repository root. Add it to your `.gitignore`:

```
.fshw/
```

**What it does and doesn't cache.** The on-disk cache stores FCS *check
results*, keyed by file content — so a fresh daemon can replay unchanged files
instead of re-checking them. It does **not** persist the compiler's in-memory
warmth: `FSharpChecker` and its FCS caches are rebuilt from cold on every daemon
start, so the first scan after a (re)start still pays that warm-up before the
cached results start landing.

## Writing plugins

Plugins are declarative update functions over a shared warm compiler: you define
how your state reacts to events (file checked, build completed, tests finished),
and the framework manages the agent, status, caching, and IPC. See
[Writing a plugin](docs/writing-plugins.md).

## Memory & tuning

The daemon keeps the F# compiler warm, which costs memory. FsHotWatch ships with
conservative defaults (aggressive GC, optional idle-exit) so this stays in check
— see [Memory & tuning](docs/memory-and-tuning.md) if you want to adjust them.
