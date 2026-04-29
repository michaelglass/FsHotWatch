# Performance Baseline and Benchmarks

## Goal

Establish measurable performance baselines so regressions are caught before release.
Not a benchmark suite for marketing — just enough to know when things get slower.

## Key metrics to track

### Cold start (no cache)
- Time from `fshw start` to `ScanComplete` for N files
- Baseline projects: FsHotWatch itself (~50 files), a larger repo (500+ files)

### Warm re-check (single file change)
- Time from file save to `FileChecked` event
- Should be <1s for most files in a warmed-up daemon

### Build cycle
- Time from `FileChanged` to `BuildCompleted` (wall clock, not dotnet build time)
- Overhead of the daemon vs running `dotnet build` directly

### Test impact analysis
- Time from `BuildCompleted` to test filter resolved (before tests start)
- Number of files in impact DB vs wall-clock query time

### Plugin throughput
- Events/second the PluginHost can dispatch under load
- Relevant for large repos with many concurrent file changes

## Approach

1. **BenchmarkDotNet microbenchmarks** for hot paths:
   - `CheckPipeline.CheckFile` (cached vs uncached)
   - `analyzeSource` (TestPrune AST analysis)
   - `selectTests` (impact graph query)

2. **Integration timing tests** that assert reasonable bounds:
   - "Re-checking a single file after warm-up takes < 2s"
   - "Impact analysis for 10 changed symbols completes < 500ms"
   - These are ceiling tests, not precise benchmarks — they catch 10x regressions

3. **CI tracking** (optional, post-1.0):
   - Record timings in CI artifacts
   - Compare against previous runs
   - Alert on > 20% regression
