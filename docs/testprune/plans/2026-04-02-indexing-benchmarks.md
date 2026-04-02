# Indexing Benchmarks Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a benchmark project that runs cold + warm indexing on SampleSolution, emits JSON metrics, and integrates with `dotnet-trace`/speedscope for flame graph profiling.

**Architecture:** Standalone console app in `benchmarks/TestPrune.Benchmarks/` that calls `Orchestration.runIndexWith` directly, reusing the same dependency injection pattern as `Program.fs`. Profiling is external via `dotnet-trace collect --format speedscope`.

**Tech Stack:** F# console app, `dotnet-trace`, speedscope, mise tasks

---

### Task 1: Create the benchmark project and add to solution

**Files:**
- Create: `benchmarks/TestPrune.Benchmarks/TestPrune.Benchmarks.fsproj`
- Create: `benchmarks/TestPrune.Benchmarks/Program.fs`
- Modify: `TestPrune.slnx`

**Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <OutputType>Exe</OutputType>
        <NoWarn>$(NoWarn);NU1605;MSB3277;NETSDK1188</NoWarn>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="Program.fs" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../../src/TestPrune/TestPrune.fsproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Update="FSharp.Core" Version="10.1.*" />
    </ItemGroup>

</Project>
```

References `TestPrune` (the CLI project) which transitively includes `TestPrune.Core`. This gives access to `Orchestration.runIndexWith`, `createChecker`, `dotnetBuildRunner`, `getProjectOptions`, and `createNoopSink`.

**Step 2: Create a minimal Program.fs that compiles**

```fsharp
module TestPrune.Benchmarks.Program

[<EntryPoint>]
let main _argv =
    printfn "TODO: benchmark"
    0
```

**Step 3: Add to the solution**

In `TestPrune.slnx`, add a benchmarks folder:

```xml
<Folder Name="/benchmarks/">
  <Project Path="benchmarks/TestPrune.Benchmarks/TestPrune.Benchmarks.fsproj" />
</Folder>
```

**Step 4: Verify it builds**

Run: `dotnet build benchmarks/TestPrune.Benchmarks`
Expected: Build succeeded

**Step 5: Commit**

```
Add benchmark project skeleton
```

---

### Task 2: Implement cold + warm index benchmark

**Files:**
- Modify: `benchmarks/TestPrune.Benchmarks/Program.fs`

**Step 1: Write the benchmark runner**

Replace `Program.fs` with:

```fsharp
module TestPrune.Benchmarks.Program

open System
open System.IO
open System.Text.Json
open TestPrune.Orchestration
open TestPrune.ProjectLoader
open TestPrune.AuditSink

/// Metrics collected from an index run.
type BenchmarkMetrics =
    { Mode: string
      Symbols: int
      Dependencies: int
      TestMethods: int
      ProjectsAnalyzed: int
      ProjectsSkipped: int
      FilesAnalyzed: int
      FilesSkipped: int }

/// Capture stderr output from runIndexWith to parse metrics.
/// runIndexWith prints stats to stderr — we intercept them.
let private captureIndex repoRoot checker parallelism =
    let oldErr = Console.Error
    let sw = new StringWriter()
    Console.SetError(sw)

    try
        let exitCode =
            runIndexWith dotnetBuildRunner getProjectOptions repoRoot checker parallelism (createNoopSink ())

        Console.SetError(oldErr)
        let output = sw.ToString()
        exitCode, output
    with ex ->
        Console.SetError(oldErr)
        reraise ()

let private parseMetrics (mode: string) (output: string) : BenchmarkMetrics =
    let lines = output.Split('\n')

    let parseIntAfter (prefix: string) (line: string) =
        let idx = line.IndexOf(prefix)

        if idx >= 0 then
            let rest = line.Substring(idx + prefix.Length).Trim()
            let numStr = rest |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
            Int32.TryParse(numStr) |> function true, v -> Some v | _ -> None
        else
            None

    let findInt prefix =
        lines |> Array.tryPick (parseIntAfter prefix) |> Option.defaultValue 0

    { Mode = mode
      Symbols = findInt "Indexed "
      Dependencies =
          lines
          |> Array.tryPick (fun l ->
              if l.Contains("symbols,") then
                  let parts = l.Split(',')

                  parts
                  |> Array.tryPick (fun p ->
                      if p.Contains("dependencies") then
                          p.Trim().Split(' ').[0] |> Int32.TryParse |> function true, v -> Some v | _ -> None
                      else
                          None)
              else
                  None)
          |> Option.defaultValue 0
      TestMethods =
          lines
          |> Array.tryPick (fun l ->
              if l.Contains("test methods") then
                  let parts = l.Split(',')

                  parts
                  |> Array.tryPick (fun p ->
                      if p.Contains("test methods") then
                          p.Trim().Split(' ').[0] |> Int32.TryParse |> function true, v -> Some v | _ -> None
                      else
                          None)
              else
                  None)
          |> Option.defaultValue 0
      ProjectsAnalyzed = findInt "Found "
      ProjectsSkipped = findInt "Skipped "
      FilesAnalyzed = 0 // TODO: parse from output if needed
      FilesSkipped = findInt "Skipped " }

[<EntryPoint>]
let main argv =
    let repoRoot =
        match argv |> Array.tryFind (fun a -> not (a.StartsWith("--"))) with
        | Some path -> Path.GetFullPath(path)
        | None ->
            // Default: examples/SampleSolution relative to repo root
            let thisDir = AppContext.BaseDirectory
            // Walk up to find repo root (contains TestPrune.slnx)
            let rec findRoot dir =
                if File.Exists(Path.Combine(dir, "TestPrune.slnx")) then dir
                else findRoot (Path.GetDirectoryName(dir))

            Path.Combine(findRoot thisDir, "examples", "SampleSolution")

    let parallelism = Environment.ProcessorCount
    let checker = createChecker ()

    // Cold index: delete DB to force full analysis
    let dbPath = Path.Combine(repoRoot, ".test-prune.db")

    if File.Exists(dbPath) then
        File.Delete(dbPath)

    eprintfn "=== Cold index ==="
    let coldExit, coldOutput = captureIndex repoRoot checker parallelism

    if coldExit <> 0 then
        eprintfn "Cold index failed with exit code %d" coldExit
        1
    else
        let coldMetrics = parseMetrics "cold" coldOutput

        // Warm index: reuse DB and FCS caches
        eprintfn "=== Warm index ==="
        let warmExit, warmOutput = captureIndex repoRoot checker parallelism

        if warmExit <> 0 then
            eprintfn "Warm index failed with exit code %d" warmExit
            1
        else
            let warmMetrics = parseMetrics "warm" warmOutput

            let results = [| coldMetrics; warmMetrics |]
            let json = JsonSerializer.Serialize(results, JsonSerializerOptions(WriteIndented = true))
            printfn "%s" json
            0
```

**Step 2: Run it to verify**

Run: `dotnet run --project benchmarks/TestPrune.Benchmarks -- ../../examples/SampleSolution`
Expected: JSON output on stdout with cold + warm metrics, diagnostic output on stderr

**Step 3: Commit**

```
Implement cold + warm index benchmark with JSON output
```

---

### Task 3: Gitignore benchmark results

**Files:**
- Modify: `.gitignore`

**Step 1: Add results directory**

Append to `.gitignore`:

```
# Benchmark results
benchmarks/results/
```

**Step 2: Create the results directory with a .gitkeep**

```bash
mkdir -p benchmarks/results
```

**Step 3: Commit**

```
Gitignore benchmark results directory
```

---

### Task 4: Add mise tasks

**Files:**
- Modify: `mise.toml`

**Step 1: Add bench and bench-raw tasks**

Append to `mise.toml`:

```toml
[tasks.bench]
description = "Run benchmarks with dotnet-trace profiling (produces speedscope flame graph)"
run = """
mkdir -p benchmarks/results
dotnet-trace collect --format speedscope \
  --output benchmarks/results/trace \
  -- dotnet run --project benchmarks/TestPrune.Benchmarks -c Release
"""
depends = ["build"]

[tasks.bench-raw]
description = "Run benchmarks without profiling (JSON metrics only)"
run = "dotnet run --project benchmarks/TestPrune.Benchmarks -c Release"
depends = ["build"]
```

**Step 2: Verify**

Run: `mise run bench-raw`
Expected: Builds, runs benchmark, prints JSON metrics

**Step 3: Commit**

```
Add mise bench and bench-raw tasks
```

---

### Task 5: Add experiment flags

**Files:**
- Modify: `benchmarks/TestPrune.Benchmarks/Program.fs`

**Step 1: Parse CLI flags and pass to FCS checker**

Update `createChecker` call to accept experiment flags. Since `createChecker` is in `Orchestration.fs` and we don't want to modify production code, create a local variant in the benchmark:

Add before `[<EntryPoint>]`:

```fsharp
let private createBenchChecker (useTransparent: bool) (keepSymbolUses: bool) (captureIds: bool) =
    FSharpChecker.Create(
        projectCacheSize = 200,
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        keepAllBackgroundSymbolUses = keepSymbolUses,
        parallelReferenceResolution = true,
        captureIdentifiersWhenParsing = captureIds,
        useTransparentCompiler = useTransparent
    )
```

Update `main` to parse flags:

```fsharp
let useTransparent = argv |> Array.contains "--transparent-compiler"
let keepSymbolUses = argv |> Array.contains "--keep-all-background-symbol-uses"
let captureIds = argv |> Array.contains "--capture-identifiers-when-parsing"

let checker = createBenchChecker useTransparent keepSymbolUses captureIds
```

**Step 2: Verify the flag works**

Run: `dotnet run --project benchmarks/TestPrune.Benchmarks -- --transparent-compiler`
Expected: Runs with TransparentCompiler enabled (may produce different timings in trace)

**Step 3: Commit**

```
Add FCS experiment flags to benchmark (--transparent-compiler, etc.)
```

---

### Task 6: End-to-end verification with dotnet-trace

**Step 1: Ensure dotnet-trace is installed**

Run: `dotnet tool list -g | grep dotnet-trace || dotnet tool install -g dotnet-trace`

**Step 2: Run the full bench task**

Run: `mise run bench`
Expected:
- `benchmarks/results/trace.speedscope.json` produced
- JSON metrics on stdout
- Open `trace.speedscope.json` in https://www.speedscope.app/ to verify flame graph renders

**Step 3: Verify experiment comparison workflow**

Run two traces and compare:
```bash
mise run bench
mv benchmarks/results/trace.speedscope.json benchmarks/results/baseline.speedscope.json
mise run bench -- --transparent-compiler
mv benchmarks/results/trace.speedscope.json benchmarks/results/transparent.speedscope.json
```

Open both in speedscope side by side.

**Step 4: Commit (if any fixes needed)**

---

### Task 7: Update fsac-learnings.md

**Files:**
- Modify: `docs/plans/fsac-learnings.md`

**Step 1: Check off the benchmarks item**

Change line 73 from:
```
- [ ] **Indexing benchmarks**
```
to:
```
- [x] **Indexing benchmarks**
```

**Step 2: Commit**

```
Mark indexing benchmarks as done in fsac-learnings
```
