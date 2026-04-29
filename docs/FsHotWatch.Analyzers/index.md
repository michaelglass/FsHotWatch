# FsHotWatch.Analyzers

Plugin that runs F# analyzers in-process using the warm FSharpChecker's
check results. Compatible with [G-Research F# Analyzers SDK](https://github.com/G-Research/fsharp-analyzers)
and custom `[<CliAnalyzer>]` implementations.

## Why

F# analyzers normally need to start their own compiler to get type
information. With FsHotWatch, the compiler is already warm -- analyzers
get parse results and check results instantly, so they run in
milliseconds instead of minutes.

## How it works

1. You save a file
2. The daemon type-checks it with the warm FSharpChecker
3. AnalyzersPlugin receives `FileChecked` with the results
4. It constructs a `CliContext` from the warm results (via reflection to
   handle FCS version mismatches)
5. It runs all loaded analyzers against that context
6. Diagnostics are reported to the error ledger

## Configuration

In `.fs-hot-watch.json`:

```json
{
  "analyzers": {
    "paths": ["analyzers/"]
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `paths` | `string[]` | -- | Directories containing analyzer DLLs. Relative paths are resolved from the repo root. |

## Writing a custom analyzer

See the [ExampleAnalyzer](../../examples/ExampleAnalyzer/) for a
complete working example. Here's the key pattern:

```fsharp
open FSharp.Analyzers.SDK

[<CliAnalyzer("MyAnalyzer", "Description of what it checks")>]
let myAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            // context.ParseFileResults has the AST
            // context.CheckFileResults has type info
            // Walk the AST, find issues, return diagnostics
            return
                [ { Type = "My Rule"
                    Message = "Something is wrong here"
                    Code = "MY-001"
                    Severity = Severity.Warning
                    Range = someRange
                    Fixes = [] } ]
        }
```

Build the analyzer as a class library and point `analyzers.paths` at
the output directory.

## CLI

```bash
# Query analyzer diagnostics
fs-hot-watch diagnostics
```

## Programmatic usage

```fsharp
daemon.RegisterHandler(
    AnalyzersPlugin.create
        [ "/path/to/analyzers" ]    // directories with analyzer DLLs
        None                        // timeoutSec (None → no timeout)
        DiagnosticSeverity.Hint     // failOnSeverity threshold
)
```

## Install

```bash
dotnet add package FsHotWatch.Analyzers
```
