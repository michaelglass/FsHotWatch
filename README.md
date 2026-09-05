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
4. **You query the results** — `fshw check` runs every plugin and reports what each one found.

Saves are debounced: if 10 files change at once (a formatter sweeping the repo,
say), FsHotWatch waits for things to settle and processes them in one batch.

## Quick start

```bash
# Install the CLI
dotnet tool install -g FsHotWatch.Cli

# The inner loop. Auto-starts the daemon the first time —
# no separate "start" step needed. Verbose by default.
fshw check

# Prefer one line per plugin?
fshw check --compact   # or -q

# Need the full suite, unfiltered? Confirm `check` told the truth.
fshw confirm
```

`fshw init` writes a starter `.fshw.json`; see [Configuration](#configuration).

## `check`, `confirm`, `verdict`

Three verbs, and the difference between them is the difference between a fast
answer and a trustworthy one.

**`fshw check` is the inner loop, and its green is a narrower claim.** It runs
every plugin (build, lint, analyze, test, format-check), but the *tests* are
**impact-filtered** — it runs the tests a heuristic selector *thinks* your change
can affect. A green means *"nothing you changed broke anything the selector chose
to look at"*, not *"the suite is green"*. That is a latency optimization, and it is
the right trade for the loop you run on every save.

**`fshw confirm` is the unfiltered verb.** It runs the same checks with the tests
**unfiltered**, and it *refuses to go green* unless they actually ran that way —
"nothing failed" is not a verdict if the run never produced the evidence
(exit `3`). Running it beside `check` is a **comparison**, and every disagreement
is a bug in one of them:

| What you see | What it means |
|---|---|
| Failed under `confirm`, never selected by `check` | **The selector MISSED a test.** An impact-analysis bug, not a test bug. |
| Passed under `confirm`, but `check` says red | **A stale ledger, a flake, or a test-isolation defect** — a test that only passes *with company*. Here `check` is the honest one. |

**`fshw verdict` reads the answer back**, from a file, without contacting the
daemon — see [Machine-readable state](#machine-readable-state-for-agents-and-ci).

**Which verb gates a merge is your project's policy, not fshw's.** These two verbs
produce different strengths of evidence and each says honestly which one it produced;
neither decides your workflow, and neither will tell you to switch verbs mid-gate.
Write the rule down where your team and your agents read it — a `CLAUDE.md`, a
`CONTRIBUTING.md`, a CI job — and fshw will not contradict it.

### What these verbs do *not* claim

This matters more than the feature list, so it is stated up front:

- **"The full suite" means every test project your `.fshw.json` knows about** — not
  every test project in your solution. A project that is in the solution but absent
  from `.fshw.json` is not run by `confirm`, and `confirm` does not claim otherwise.
- **Impact *selection* is still known-unsound.** `confirm` makes the merge verdict
  **unforgeable** — a green must be backed by an unfiltered run. It does **not** make
  the selector sound: `check` can still miss tests it should have picked. Those are
  two different claims and only the first one is fixed.
- **A warm cache makes `confirm` exit 3, not 0.** Re-run it on an *unchanged* tree and
  the test plugin replays its cached result rather than running; a replay is not
  evidence, so `confirm` refuses (exit 3) instead of greening. Clear the cache
  (`mise run cache-clear`) or change a file. It fails safe, but it is a rough edge.
- **The cache is in two places, and `rm -rf .fshw/cache` only empties one of them.**
  Verdicts that are a pure function of content — `format-check`, `lint`, `analyzers` —
  live in a box-wide store OUTSIDE the checkout, so a freshly created workspace starts
  warm instead of re-scanning a tree it already has an answer for. Everything that
  asserts something about *this* checkout (`build`, `test-prune`) stays under `.fshw/`.
  `mise run cache-clear` empties both. The shared store is at `$FSHW_CACHE_HOME`,
  else `$XDG_CACHE_HOME/fshw`, else `~/.cache/fshw`, namespaced per repository — the
  daemon logs the exact directory and its entry count on every start.

## Commands

| Command | What it does |
|---------|--------------|
| `fshw check` | **The inner loop.** Run every plugin and report findings; tests are impact-filtered. Auto-starts the daemon. Exits 0 (clean), 1 (failures), 2 (completeness unconfirmed). `--run-once` runs without a daemon; `-q`/`--compact` for one line per plugin. |
| `fshw confirm` | **The unfiltered verb.** Same checks, but the tests run unfiltered — and a green is refused unless they did. Exits 0/1/2 as `check`, plus **3** (unearned scope). `--run-once` for CI. |
| `fshw verdict` | Read `.fshw/verdict.json` and report whether it still applies to the tree on disk. Contacts no daemon, triggers no run. |
| `fshw status [plugin]` | Show the daemon's current status (optionally for one plugin). Triggers nothing. |
| `fshw start` | Run the daemon in the foreground (Ctrl+C to stop). Optional — `check`/`status` start it for you. |
| `fshw stop` | Stop the running daemon. **Not a cache reset** — the task cache is on disk and survives it; see below. |
| `fshw format` | Format the code (Fantomas). |
| `fshw test-rerun` | Rerun tests for an xUnit v3 `--filter-class` / `--filter-trait` slice. Add `--project <name>` (repeatable) to aim it at particular test projects — without it the filter is fanned out across every configured project, so a class living in one of them reports zero matches in all the others. |
| `fshw rerun <plugin>` | Force one plugin to re-run, clearing its cached state. |
| `fshw invalidate` | Clear every cached task result for this workspace without stopping its warm daemon. Intended for repository-side clean commands that remove `bin/` or `obj/`. |
| `fshw init` | Generate a starter `.fshw.json`. |
| `fshw config check` | Validate `.fshw.json` without starting the daemon. |

Add `-v` for debug logging or `-a` for agent-friendly, parseable output. Run
`fshw --help` for the full list, and see the
[CLI README](src/FsHotWatch.Cli/) for every verb and flag.

> **If a check looks stuck or a result looks stale, do not run `fshw stop`.** The task
> cache is on disk under `.fshw/` and survives a stop, a crash and a reboot, so a
> restart throws away the warm compiler and then serves you the same cached answer. The
> cache-reset primitive is **`fshw invalidate`**, which clears this workspace's task
> results and preserves the compiler process. The verb that cannot be served from cache is **`fshw confirm`**, which forces a real build and refuses to replay a cached
> verdict. If the cause is stale build *output*, `dotnet build` is the fix — and a copy
> MSBuild skipped on equal timestamps needs `dotnet build --no-incremental`.

## Machine-readable state (for agents and CI)

**Don't parse the CLI's output.** It is a progress display written for a human and
it will change. Every `check` and `confirm` publishes its result as a file:

```bash
fshw verdict          # stdout: a JSON envelope; exit code: the answer
# 0 green · 1 red · 2 incomplete · 3 unearned scope · 4 STALE · 5 no verdict · 6 IN FLIGHT
#   (`check` adds 7 — the run finished and its result never reached the CLI)
```

`.fshw/verdict.json` is written atomically at the end of every run — **including**
the ones that fail, time out, or lose the daemon mid-run, which are exactly the
moments the human output is least sufficient. It is content-addressed to **the tree
it verified** *and* **the binary that verified it**, so a green from a different tree
(or from an older, buggier `fshw`) can never be mistaken for a current one. Reading
it opens no socket and starts nothing, so *asking cannot perturb the answer*.

It is also content-addressed to **the run that is happening**. The file is stamped
only at completion, so mid-run it still holds the PREVIOUS run's result — over an
unchanged tree that parses, matches, and reads green. A read taken while a run is in
flight over the tree it describes reports **exit 6** and `"inFlight": true`, never that
green. 6 rather than 4 because the fix differs: 4 means "the code moved, re-run", 6
means "the answer is being computed, wait".

This — not the progress display — is the surface agents and CI should read. Full
schema, exit codes, and the tree-hash recipe: [CLI
README](src/FsHotWatch.Cli/README.md#machine-readable-state-for-agents-and-ci),
[ADR-013](docs/adr-013-the-verdict-is-a-file-content-addressed-to-its-tree.md) and
[ADR-019](docs/adr-019-a-verdict-is-current-only-when-no-other-run-is-in-flight.md).

### `.fshw/heartbeat` — is this daemon still *working*?

`.fshw/daemon.pid` answers "is a daemon resident?", which is the wrong question: a
daemon can be alive and wedged. `.fshw/heartbeat` answers the right one.

| | |
|---|---|
| **Path** | `<repoRoot>/.fshw/heartbeat` |
| **Format** | Unix epoch **seconds**, decimal ASCII, **no trailing newline** (same shape as `daemon.pid`) |
| **Cadence** | rewritten every **15 s** while a run is in progress |
| **Only while running** | an **idle daemon never beats** |
| **Never deleted** | between runs the file holds the *previous* run's timestamp |
| **Absence / unparseable** | **UNKNOWN — never "stale"** |

The beat is driven by whether work is *in flight*, not by log output, so a test
phase that runs for ten minutes in silence keeps beating throughout. Writes are
atomic, so a concurrent reader sees either the previous beat or this one — never a
torn or empty file.

Two rules for consumers:

- **Treat 15 s as the _floor_ of a staleness threshold, never the threshold
  itself.** A beat can be late on a saturated box or a slow disk.
- **A missing or unreadable heartbeat means UNKNOWN — fall back to your own
  timeout.** Do not read it as "dead". Erring toward "still alive" costs a slow
  reclaim; erring toward "dead" lets two heavy runs run concurrently, which is
  worse. The file is never deleted precisely because a stale timestamp is a
  stronger, more actionable signal than absence.

Like `daemon.pid`, this is a fact fshw publishes about itself with no opinion about
who reads it or why.

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
  "cache": "memory",
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
        "group": "unit",
        "coverage": {
          "enabled": false,
          "collectForImpact": true
        }
      }
    ],
    "excluded": [
      {
        "project": "tests/MyProject.IntegrationTests",
        "reason": "end-to-end; run by `make integration` and by CI's solution-wide dotnet test"
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
| `exclude` | `string[]` | `[]` | Gitignore-style globs (repo-root-relative) for paths to skip entirely — watching, building, checking. (`obj/` + `bin/` are always skipped, independent of this.) |
| `beforeRun` | `string \| false` | — | Shell command run **once** at the very start of a `check`/`confirm` run, before the daemon is contacted. Fail-closed preflight. See [Run-level hooks](#run-level-hooks). |
| `afterRun` | `string \| false` | — | Shell command run **once** at the end of the run, as a `finally`. Best-effort — never changes the verdict. See [Run-level hooks](#run-level-hooks). |
| `runHookTimeoutSec` | `number \| false` | — | Timeout bounding **each** run-level hook. See [Run-level hooks](#run-level-hooks). |
| `runHookCommands` | `string[]` | `["check","confirm"]` | Which verbs the run-level hooks bracket. See [Run-level hooks](#run-level-hooks). |
| `verdictInputs` | `object` | — | The files that decide what a check concludes but that no walk of `src/`+`tests/` would find — your coverage floors, your analyzer rules, your baselines. Folded into the verdict's tree hash, so editing one stops a prior green from applying. See [What decides the verdict is what is hashed](#what-decides-the-verdict-is-what-is-hashed). |
| `includeOutsideRepo` | `bool` | `false` | Report on compile items that resolve **outside** the repo root — e.g. NuGet-injected `_content` source (xunit's `DefaultRunnerReporters.fs`), or files above/beside the repo. Default `false`: the report-producing plugins (analyzers, lint) skip such third-party source — it's compiled into your project, but not yours to lint, and a latent analyzer-crash surface (AUTOMATION-49). Set `true` to lint them anyway. |

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
| `extensions` | `array` | `[]` | Explicit TestPrune attribution extensions. `sql` discovers `ReadsFrom`/`WritesTo` attributes; `sql-hydra` maps generated SqlHydra table types and query operations. |
| `projects` | `array` | `[]` | List of test project configurations. |
| `excluded` | `array` | `[]` | Solution test projects the gate deliberately does **not** run, each with the reason it does not. The only sanctioned way for a test project in the solution to be outside the scope — see [The scope must cover the solution](#the-scope-must-cover-the-solution). |
| `solution` | `string` | — | The solution the test scope is reconciled against. Only needed when the repo root holds more than one `*.slnx`/`*.sln`, or when the authority is not at the root. |

**Test attribution extensions:**

```json
"extensions": [
  { "type": "sql" },
  {
    "type": "sql-hydra",
    "generatedModulePrefix": "Intelligence.Database.Generated"
  }
]
```

`sql` constructs TestPrune's `AutoSqlExtension()` and needs no other fields.
`sql-hydra` constructs `SqlHydraExtension(prefix)`; its
`generatedModulePrefix` is the required, non-blank, fully qualified prefix
before the generated schema and table type. `sqlhydra` remains accepted as a
compatibility alias. Unknown kinds and incomplete extension objects are
configuration errors rather than silently disabling attribution.

**`tests.projects[].coverage` schema:**

| Shape/field | Type | Default | Description |
|-------------|------|---------|-------------|
| `coverage` | `bool \| object` | `true` | A boolean controls both collection and consumer-ratchet participation. Use the object form to separate them. |
| `coverage.enabled` | `bool` | `true` | Include this project's raw report in the shared consumer coverage snapshot. |
| `coverage.collectForImpact` | `bool` | value of `enabled` | Retain source-file → test-project runtime evidence for impact selection. `{ "enabled": false, "collectForImpact": true }` is the integration-test adoption shape. |
| `coverage.argsTemplate` | `string` | MTP Cobertura template | Runner arguments containing the required `{output}` placeholder. |

Only a full project run replaces its runtime file map. A filtered run adds
positive evidence. Missing complete evidence, or a complete baseline older than
seven days, widens to the entire configured project with an explicit diagnostic.

**`tests.excluded[]` fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `project` | `string` | — | The project, by repo-relative directory (`tests/App.Tests`), by `.fsproj` path, or by bare name (`App.Tests`). |
| `reason` | `string` | — | Why it is not run. **Required and non-blank** — an exclusion without a written reason is refused. |

### What decides the verdict is what is hashed

A verdict is content-addressed to the tree it verified: read `verdict.json`, and if
its `treeHash` is not the hash of the tree on disk, **the verdict does not apply**.
That guarantee is only as wide as the set of files being hashed.

The rule the set is derived from, stated so the next addition is derived rather than
remembered:

> **A file belongs in the tree hash iff changing it can change what a check
> concludes.**

fshw applies that rule itself for the files it can know about — everything under
`src/` and `tests/`, its own `.fshw.json`, and the root-level toolchain and
dependency files (`Directory.Build.props`, `Directory.Build.targets`,
`Directory.Packages.props`, `global.json`, `nuget.config`, `paket.lock`,
`paket.dependencies`). Flip `TreatWarningsAsErrors` off and every prior green stops
applying, with no configuration needed.

It cannot know the rest. fshw has no way to tell that `coverage-counts.json` holds
your floors, or that `probe-collapse-baseline.json` is the census a finding is
measured against. **You name those**, each with a `why` a reviewer can check:

```json
{
  "verdictInputs": {
    "hashed": [
      { "path": "coverage-counts.json",
        "why": "lower a floor and a verdict earned under the higher one must stop applying" },
      { "path": "analyzers/**/*.fs",
        "why": "these ARE the house rules the analyze plugin enforces" },
      { "path": "analyzers/Rules/bin/Debug/net10.0/Rules.dll",
        "why": "the assembly the analyze plugin actually loads" }
    ],
    "notInputs": [
      { "path": "CHANGELOG.md",
        "reason": "prose about work that was already gated; re-gating on it buys nothing" }
    ]
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `hashed[].path` | `string` | Repo-relative file, directory (taken whole), or gitignore-style glob. **Required.** |
| `hashed[].why` | `string` | How changing it can change an answer. **Required and non-blank.** |
| `notInputs[].path` | `string` | Repo-relative path deliberately left out of the hash. **Required.** |
| `notInputs[].reason` | `string` | Why changing it cannot change an answer. **Required and non-blank.** |

**A declaration is never silently skipped** — that silence was the defect this
feature exists to end:

- A declaration fshw cannot honour as written — no `path`, no stated reason, the
  same path declared twice, a path declared as both an input and a not-an-input, a
  path outside the repo — is a **hard config error**. The daemon refuses to start.
- A declaration that matches **no file** is hashed as *absent* under its own entry,
  never as nothing. A typo cannot quietly restore the old behaviour, the hash moves
  when the file appears, and `fshw` warns at load. `verdict.json` records
  `treeAbsentDeclarationCount` so you can see it in the artifact.
- `verdict.json` records `treeDeclaredCount`: how many files your declarations
  actually contributed. A repo that declares twenty-nine inputs and reads back `0`
  has been told.

A declared path is **not** filtered by `exclude`, nor by the implicit `bin/`+`obj/`
skip. Those decide where the walk goes *looking*; a declaration is an explicit
statement that this file decides an answer, and the specific statement wins over the
general one. It is what makes the analyzer assembly a check actually loads
declarable at all.

`notInputs` never **removes** anything from the hash. It records a decision so that
"not hashed" is reviewable rather than an omission nobody noticed. A config key that
could delete files from the tree hash would be a supported, one-line way to weaken
the gate — with a `reason` field to make it look considered.

### The scope must cover the solution

`confirm` reports its scope as `{"kind":"full","ranProjects":N,"totalProjects":N}`.
Both numbers count `tests.projects`, so on its own "full" would mean *every suite the
config named* — never *every suite in the solution*. A test project sitting in the
solution and in no gated list would not be reported as UNRUN; it would simply be
absent, and absent is indistinguishable from passing.

So every config load reconciles `tests.projects` with the solution at the repo root,
and **refuses to load** (exit `2`, `config error:`) when they disagree:

- a solution test project that is neither gated nor excluded;
- an exclusion with no `reason`, or naming a project the solution does not contain;
- a project listed in **both** `tests.projects` and `tests.excluded`;
- a gated project the solution does not contain;
- more than one solution file at the repo root with no `tests.solution` to pick one.

A test project is one that declares a **test runner** — `UseMicrosoftTestingPlatformRunner`,
`IsTestProject`, `TestingPlatformDotnetTestSupport`, or a reference to
`Microsoft.NET.Test.Sdk` / `Microsoft.Testing.Platform` / `xunit.v3.mtp-v2` /
`xunit.runner.visualstudio` / `NUnit3TestAdapter` / `MSTest.TestAdapter`. A library that
merely references a test framework is not one. A project you gate is one whether or not
that list recognises it.

A declared, reasoned exclusion is fine — the silence is the bug. Every exclusion is
logged on the green path and recorded in `.fshw/verdict.json` under `scope.excluded`,
so a consumer reading `"kind": "full"` can see the gap rather than infer completeness
from a count.

A repo with **no** solution file has no declared universe to be complete against, and
nothing is reconciled: the full-suite claim is complete *relative to the solution*, and
with no solution there is no such claim. A repo that configures no test projects is left
alone entirely — it makes no full-suite claim, and `confirm` already refuses to build a
merge verdict without one.

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
| `reportVerificationFormat` | `string` | `"auto"` | How the pass/fail verdict's structured test report is obtained. The report (not the process exit code) decides green/red. `auto` reads `obj/project.assets.json` and injects the matching CTRF switches for a resolved xUnit 3 or 4 runner; missing, malformed, conflicting, and unknown versions receive no report switches. `ctrf` forces report switches, using the detected family when possible and the xUnit 3 names for an unknown custom runner. `off` never injects them and the exit code stays authoritative. |

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

### Run-level hooks

`beforeRun` / `afterRun` bracket a **whole `check` or `confirm` run** — once, at the
top level, outside the daemon. They are distinct from `tests.beforeRun`, which the
daemon runs per test run inside the tests slot.

```json
{
  "beforeRun": "my-gate acquire",
  "afterRun": "my-gate release",
  "runHookTimeoutSec": 900,
  "runHookCommands": ["confirm"]
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `beforeRun` | `string \| false` | — | Run once at the very start, **before the daemon is contacted**. **Fail-closed:** a non-zero exit aborts the run with exit 2 and no plugin work happens. |
| `afterRun` | `string \| false` | — | Run once at the end, as a `finally` — on success, on a red verdict, **and** on abort (including SIGINT/SIGTERM). **Best-effort:** a non-zero exit is logged loudly but never changes the verdict, so a release hiccup can't flip green↔red. |
| `runHookTimeoutSec` | `number \| false` | — | Timeout (seconds) bounding **each** hook. Resolution: this → global `timeoutSec` → built-in default. A run-level hook is **always** bounded, even when the global timeout is disabled — a hook that hangs forever is worse than whatever it guards. |
| `runHookCommands` | `string[]` | `["check","confirm"]` | Which verbs the hooks bracket. Entries: `"check"`, `"confirm"`. |

**`runHookCommands`** exists so you can bracket the expensive verb only. A gate that
serialises heavy runs across workspaces usually wants to guard the *merge* verdict —
`confirm`, unfiltered, expensive — while leaving the inner loop (`check`,
impact-scoped, run on every save) free. Bracketing both makes every save-triggered
check queue behind someone else's full suite:

```json
{ "runHookCommands": ["confirm"] }
```

A verb **not** in the set runs completely unwrapped — no latch, no signal handlers,
no shell-out — the same straight path taken when no hook is configured. The daemon
and `--run-once` behave identically, so CI cannot silently lose the gate.
`confirm`'s "verdict still applies" fast path is never bracketed.

Failure modes lean **safe**, because silently un-gating is the dangerous direction:

- **Absent → both verbs**, exactly the behaviour from before the key existed. Adding
  the key to fshw changed no existing config's meaning.
- **Unrecognised or wrongly-typed → both verbs**, with a warning. A typo
  (`["comfirm"]`) can never un-gate a run.
- **Explicitly `[]` → bracket nothing.** Legal — the config said so plainly — but
  warned about at load.

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
