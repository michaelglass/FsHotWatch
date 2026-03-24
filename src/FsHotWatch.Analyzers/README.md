# FsHotWatch.Analyzers

FsHotWatch plugin that hosts F# analyzers in-process using the warm FSharpChecker's
check results. Loads analyzer DLLs from configurable paths.

## Usage

```fsharp
let plugin = AnalyzersPlugin(analyzerPaths = ["/path/to/analyzers"])
daemon.Register(plugin)

// Query diagnostics
// fs-hot-watch diagnostics
```

Compatible with [G-Research F# Analyzers](https://github.com/G-Research/fsharp-analyzers)
and custom `[<CliAnalyzer>]` implementations.
