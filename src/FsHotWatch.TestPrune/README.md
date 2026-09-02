# FsHotWatch.TestPrune

Plugin for test impact analysis. When you change a source file,
TestPrune figures out which tests are affected and runs only those --
instead of your entire test suite.

> **Status: early alpha, and a lot of it is AI-written.** APIs and behavior
> shift between versions and rough edges are expected — your mileage may vary.
> Issues and PRs are very welcome.

## Why

Running all tests after every save is slow. If you change a single
function, you probably only need to run 3 tests out of 500. TestPrune
uses the warm FSharpChecker's symbol analysis to track which tests
depend on which symbols, so it can tell you exactly what to re-run.

## How it works

1. You save a file
2. TestPrune receives `FileChecked` with the warm compiler's results
3. It analyzes which symbols changed
4. It looks up which test classes reference those symbols
5. If `testConfigs` are provided, it runs only the affected tests
6. It emits `TestCompleted` for downstream plugins (like Coverage)

### The needs-testing queue (why "nothing to test" is trustworthy)

Every changed symbol enters a durable queue
(`.fshw/test-prune/pending-verification.json`) and leaves it **only when a
test run that covered it completes green**. A run that fails, aborts (e.g. a
failing `beforeRun` hook), or never executes commits nothing — those symbols
stay queued and keep selecting tests until a covering run passes, across
daemon restarts. The "no affected tests — skip" fast path and cached green
replays are both gated on this queue being empty, so a green verdict always
means "test-equivalent to the last green run", never "tests didn't happen to
run". The queue can only err toward over-testing.

### Stale build output is caught before anything runs

Before a single suite launches, every configured project's build output is compared
against its sources. If any of it is stale, **nothing spawns** — the whole run is
refused and every project comes back deferred, naming its remedy. Running the projects
that happen to be fresh would buy minutes of partial execution for signal the verdict
cannot use.

Exactly one stale case is **repaired** rather than refused: a dependency or fixture in a
test project's output directory holding bytes no current build output holds, which is
MSBuild's incremental copy comparing equal timestamps and skipping the copy. The origin
bytes are written across, re-read, and the run proceeds. A stale *compile* is refused
instead — no file on disk holds the bytes it would need, and inventing them is how
silent degradation starts.

Repairs are recorded to `.fshw/test-prune/stale-heals.json`, which drives a circuit
breaker: **ten repairs of one file inside two days** stop being a repair and become a
finding, so the run refuses and names the file and the count rather than absorbing the
inversion forever. The breaker gates the *repair*, not the run, so a tripped breaker on
a clean tree changes nothing. Delete the ledger to reset it; the window also ages out on
its own.

### When the symbol index is emptied under a live sidecar

`.fshw/test-prune/file-freshness.json` records whether a file ended its last check
FCS-clean. It carries no schema version and sits beside a `test-impact.db` that deletes
and recreates itself on a `SchemaVersion` bump — so the sidecar can outlive the rows it
describes and still say `Clean` about them.

`FileFreshness.trustStoredRows` decides what may be done with that, from the sidecar's
verdict plus one structural fact: **does the index still hold rows for this file.** It
answers a named `StoredRowTrust` rather than a bare boolean, and a `Clean` stamp over an
emptied index resolves to `EverySymbolIsNew` — every symbol reads as added, which
**widens** the run. That is the safe direction and it was already the behaviour; naming
it is what stops a future refactor from "tidying" it into under-selection with no test
failing.

Whether the database was recreated is deliberately **not** an input: that is also true
of a first-ever creation, so it cannot tell a schema bump from a fresh clone, and it
says nothing about an individual file. Ask the index what it *holds*, never how it came
to be that way.

## Configuration

In `.fshw.json`:

```json
{
  "tests": {
    "beforeRun": "dotnet build",
    "projects": [
      {
        "project": "MyApp.Tests",
        "command": "dotnet",
        "args": "run --project tests/MyApp.Tests --no-build --",
        "filterTemplate": "--filter-class {classes}",
        "classJoin": " ",
        "group": "unit"
      }
    ]
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `beforeRun` | `string` or `string[]` | -- | One command, or ordered fail-fast commands, run before each test run. Each array element is one atomic shell command and is timed separately in the verdict's `hooks[]`. |
| `projects[].project` | `string` | `"unknown"` | Project name (for filtering and display). |
| `projects[].command` | `string` | `"dotnet"` | Test runner command. |
| `projects[].args` | `string` | `"test --project <name>"` | Arguments to the test runner. |
| `projects[].group` | `string` | `"default"` | Group name (for organizing/filtering test projects). |
| `projects[].environment` | `object` | `{}` | Extra environment variables as `"KEY": "VALUE"` pairs. |
| `projects[].filterTemplate` | `string` | -- | Template for class-based filtering. `{classes}` is replaced with affected test class names. |
| `projects[].classJoin` | `string` | `" "` | Separator for joining class names in the filter. |
| `projects[].coverage` | `bool` or `object` | `true` | Collect and gate coverage for this project. `false` disables both. An object accepts `enabled` (consumer-ratchet participation), `collectForImpact` (TestPrune collection), and `argsTemplate`; `{ "enabled": false, "collectForImpact": true }` collects runtime evidence without opting the project into the consumer ratchet. |

Runtime evidence is fail-safe: a configured collection project with no complete
baseline, or one older than seven days, is selected in full with an explicit
missing/stale diagnostic. Filtered runs only add evidence and cannot erase the
last complete project baseline.
| `projects[].timeoutSec` | `int` | -- | Per-project test timeout in seconds. Falls back to the top-level `tests.timeoutSec`. |
| `coverageDir` | `string` | `"coverage"` | Directory (under the repo root) where per-project coverage artifacts are written. |

## CLI

```bash
# Run every check — TestPrune runs the affected tests as part of it
fshw check

# Rerun a specific slice for investigation (bypasses impact analysis)
fshw test-rerun --filter-class "*MyApp.Tests*"

# Query which tests are affected by recent changes (plugin command)
fshw affected-tests

# Reset coverage baseline — next full run rebuilds coverage.baseline.json
fshw coverage refresh-baseline
```

## Coverage

When `projects[].coverage` is `true` (the default), TestPrune asks coverlet to
emit its native JSON format per test project under
`<repoRoot>/<tests.coverageDir>/<project>/`:

- **`coverage.baseline.json`** — written by every *full* run. Authoritative
  snapshot of the whole suite's coverage.
- **`coverage.partial.json`** — written by *impact-filtered* runs. Only the
  subset of lines the filtered tests touched.
- **`coverage.cobertura.xml`** — always derived; downstream gating
  (`coverageratchet`, editor badges, etc.) reads this file.

After each test run, TestPrune either converts the baseline directly (full
run) or merges the partial into the baseline per-line (max of hit counts)
before rewriting the cobertura file. Partial runs **never lower** the reported
coverage.

**Bootstrap.** If no `coverage.baseline.json` exists and the run was filtered,
TestPrune skips cobertura emission entirely. Run `fshw check` (or any
full-suite invocation) once to produce a baseline; subsequent filtered runs
will merge against it.

**Caveat.** Coverlet's merge keys by file path + line number, not by content
hash. Edits between a baseline and a partial can misattribute hits at the line
level. The aggregate coverage ratio stays correct.

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
daemon.RegisterHandler(
    TestPrunePlugin.create
        ".fshw/test-impact.db"   // database path
        repoRoot                  // repo root
        (Some [                   // test configs
            { Project = "MyApp.Tests"
              Command = "dotnet"
              Args = "run --project tests/MyApp.Tests --no-build --"
              Group = "unit"
              Environment = []
              FilterTemplate = Some "--filter-class {classes}"
              ClassJoin = " "
              TimeoutSec = None }
        ])
        None                      // buildExtensions: Database -> ITestPruneExtension list
        None                      // beforeRun callback
        None                      // afterRun callback
        None                      // coveragePaths: project -> CoveragePaths option
        []                        // dependsOn: repo-root-relative globs naming external
                                  //   test inputs (migrations, generated files) — their
                                  //   content hash salts the test cache key
)
```

## Install

```bash
dotnet add package FsHotWatch.TestPrune
```
