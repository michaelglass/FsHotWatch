# Indexing Benchmarks Design

## Goal

Establish benchmarks measuring cold and warm index time, symbol count, and
cache hit rate on the SampleSolution. Serves two purposes:

1. **CI regression detection** — catch metric regressions (symbol counts, cache
   behavior) on every PR
2. **Experiment comparison** — baseline for TransparentCompiler vs
   BackgroundCompiler and other Tier 3 FCS experiments

## Benchmark Project

`benchmarks/TestPrune.Benchmarks/` — a console app referencing `TestPrune.Core`.

- Runs `Orchestration.runIndexWith` against `examples/SampleSolution/`
- Two runs per invocation:
  1. **Cold**: deletes `.test-prune.db`, full analysis
  2. **Warm**: DB and FCS caches populated, max cache hits
- Emits JSON to stdout with count metrics:
  - Symbols, dependencies, test methods
  - Files analyzed vs skipped, projects analyzed vs skipped
- No manual Stopwatch instrumentation — timing comes from `dotnet-trace`

### Experiment Flags

CLI flags to toggle FCS experiment settings:

- `--transparent-compiler` → `useTransparentCompiler = true`
- `--keep-all-background-symbol-uses` → `keepAllBackgroundSymbolUses = true`
- `--capture-identifiers-when-parsing` → `captureIdentifiersWhenParsing = true`

Each flag passes through to FCS options, enabling side-by-side flame graph
comparison between configurations.

## Profiling

Uses `dotnet-trace` + speedscope for auto-instrumented flame graphs:

- `dotnet-trace collect --format speedscope` wraps the benchmark process
- Produces `.speedscope.json` — open in speedscope for interactive flame charts
- No manual instrumentation needed; hotspots visible automatically
- Compare flame graphs between experiment runs (e.g., BackgroundCompiler vs
  TransparentCompiler)

## Mise Tasks

**`mise run bench`** — run under `dotnet-trace`:

```bash
dotnet-trace collect --format speedscope \
  --output benchmarks/results/trace \
  -- dotnet run --project benchmarks/TestPrune.Benchmarks
```

Produces `benchmarks/results/trace.speedscope.json` and JSON metrics on stdout.

**`mise run bench-raw`** — run without tracing (CI / quick checks):

```bash
dotnet run --project benchmarks/TestPrune.Benchmarks
```

`benchmarks/results/` is gitignored.

## CI Integration

- Runs `mise run bench-raw`
- Captures JSON metrics as CI artifact
- Validates correctness invariants (e.g., warm index has full cache hits)
- No timing gates — CI runners are too noisy for reliable timing comparison
- Timing regression detection is manual: compare flame graphs locally
