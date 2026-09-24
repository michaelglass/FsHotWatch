# Mutating pre-build preprocessors

Status: implemented. Measured against `main@origin` `045451e5`.

## Problem

The only preprocessor a `.fshw.json` can ask for is the built-in Fantomas one, wired
through `FormatMode.Auto`. A repository whose build input is *generated* — a database
types file regenerated from a migrations directory, say — has to regenerate it before
the build, in place, without leaving the tree dirty mid-check and without the
regenerator's own write re-triggering the daemon. That is exactly what
`IFsHotWatchPreprocessor` already provides for the formatter; nothing exposes it to a
command.

## Design

### Configuration

A top-level `preprocessors` array. Each entry:

```json
{
  "name": "dbtypes-sync",
  "command": "dotnet",
  "args": "fsi build.fsx dbtypes-sync",
  "cwd": ".",
  "triggers": ["*.sql"],
  "writes": ["src/Intelligence/Database/DbTypes.fs"],
  "timeoutSec": 120
}
```

- `name` (required, unique): the status line and the plugin name a verdict names.
- `command` + `args` (required): argv, run through `ProcessHelper.runProcess` so the
  child is tracked in the ambient `ProcessRegistry` scope and reaped with the run.
- `cwd` (optional, default the repository root): repo-relative or absolute.
- `triggers` (optional): `FilePattern`s (`*suffix` or a literal basename, the same shape
  `fileCommands.pattern` takes). The preprocessor runs when a changed file in the batch
  matches one. Absent → runs before EVERY run. Trigger patterns are also added to the
  daemon's extra watch patterns, so a `.sql` edit reaches a batch at all.
- `writes` (optional): repo-relative paths the command may rewrite. Attribution is by
  content: each is hashed before and after the command, and only those whose bytes
  changed are reported as `Modified` — so the watcher echo of the command's own write
  is suppressed, the rewritten file joins the batch the checks see, and a no-op run
  suppresses nothing (a later real edit to that file is never swallowed).
- `timeoutSec` (optional): per-entry → global `timeoutSec` → `DefaultGlobalTimeoutSec`,
  the `fileCommands` chain. A silent child (`ProcessBounds.silent`).

Typed: `Trigger = Always | Matching of FilePattern list`; the spec is a record, not a
bag of options that the runner re-validates.

### The built-in formatter stays `FormatMode`

`FormatMode` is unchanged. Its `Check` arm is a *plugin* (read-only, reports), not a
preprocessor, so a tri-state cannot be one array entry; and the formatter's evidence
(which pin, from which manifest) is something a generic argv cannot say. What changes
is ORDER: configured preprocessors run first, in config order, and the built-in
formatter runs LAST, over the batch plus everything the earlier passes rewrote — so
generated output is formatted by the pinned formatter, and each pass sees what the
previous ones produced.

### Host changes (`src/FsHotWatch`)

- `PluginHost.RunPreprocessors`: registration order is run order (a list, not a
  `ConcurrentBag`), and each pass receives `files ∪ modified-so-far`.
- `Daemon.processBatch`: files preprocessors modified join `allSourceFiles`, so a
  regenerated source is dispatched to build/check even when it was not in the batch.
- `Modified` is reported in the watcher's path form. Found while implementing: a native
  watcher reports real paths (`/private/var/…` on macOS), the configuration writes the
  root as given, and the daemon suppresses an echo by exact path — so a generated file
  under a symlinked root was re-triggering its own generator. The batch is the
  evidence for which form to report (`realPathOf`, `inWatcherForm`).
- New `CommandPreprocessor.fs`: `Spec`, `Trigger`, and `create : Spec -> IFsHotWatchPreprocessor`.
  A non-zero exit, a timeout, or a spawn failure is `Error reason` (with the output
  tail) — the host records a Failed status, which reddens `check`/`confirm`. A pass that
  is not triggered is `Ok { Modified = []; Considered = 0; Evidence = "not triggered" }`.

### Mode parity

Every entry point — daemon start, hosted session, `check --run-once`, `confirm`,
`runOnceAndReport` — registers plugins through ONE function,
`DaemonConfig.registerPlugins`. The preprocessors are registered there and nowhere
else, and a source-scan test (the `HostingSeamTests` shape) refuses any other
`RegisterPreprocessor` call site in `src/FsHotWatch.Cli`. Trigger patterns join the
extra watch patterns in `daemonWith`, the one daemon constructor the watching paths
share; one-shot runs have no watcher and scan the whole tree instead.

## Tests (failing first)

1. `CommandPreprocessorTests`: a command that rewrites a declared file → `Modified`
   holds it and its new bytes are on disk; a no-op command → `Modified = []`; an
   undeclared write is not attributed; `triggers` that match nothing → not run;
   `Always` runs on any non-empty batch; non-zero exit → `Error` naming the exit code
   and output; timeout → `Error`.
2. `PluginHostTests`: preprocessors run in registration order; the second sees the
   first's `Modified`; a refused one is a `Failed` status.
3. `DaemonTests`: a preprocessor's `Modified` file, absent from the batch, is dispatched.
4. `DaemonConfigTests`: parsing — defaults, each field, required-name refusal,
   duplicate-name refusal, invalid trigger refusal, timeout chain.
5. `registerPlugins` parity: registers configured preprocessors, in order, after none
   when the array is empty; source scan for stray `RegisterPreprocessor` sites.

## Docs

README configuration reference (`preprocessors` key + fields table), root and package
CHANGELOGs (minor: new config key; core: ordered preprocessors, modified files join
the batch).
