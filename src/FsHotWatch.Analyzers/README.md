# FsHotWatch.Analyzers

Plugin that runs F# analyzers in-process using the warm FSharpChecker's
check results. Compatible with [G-Research F# Analyzers SDK](https://github.com/G-Research/fsharp-analyzers)
and custom `[<CliAnalyzer>]` implementations.

> **Status: early alpha, and a lot of it is AI-written.** APIs shift between
> versions and rough edges are expected — your mileage may vary. Issues and PRs
> are very welcome.

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

In `.fshw.json`:

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

> ### The DLL's filename must contain `Analyzer`
>
> **This is the one thing that will silently cost you an afternoon.** The
> FSharp.Analyzers.SDK loader (`Client.LoadAnalyzers`) only opens assemblies whose
> **filename** matches `*Analyzer*.dll`. A DLL without `Analyzer` in its name is
> never scanned — it loads **zero** analyzers, and the old failure mode was for the
> check to sail on green having run none of your rules. An unloaded analyzer and a
> clean one look identical from the outside.
>
> If your project is named something else, set `<AssemblyName>` so the *output* file
> matches, even though the project file does not:
>
> ```xml
> <!-- MyCompany.Rules.fsproj -->
> <AssemblyName>MyCompany.ConventionAnalyzers</AssemblyName>
> ```
>
> FsHotWatch fails loud rather than quiet here: an `analyzers.paths` entry that
> resolves to **0 analyzers** aborts startup with
> `config error: Analyzer path(s) loaded 0 analyzers`. Silence is not treated as
> success.

## House rules: repo-local analyzers

Analyzers do not have to be a published package. Point `analyzers.paths` at a
project inside your own repo and you have **house rules** — conventions the type
system cannot reach, enforced on every `check` and `confirm`.

FsHotWatch does exactly this to itself. [`analyzers/FsHotWatch.Rules`](../../analyzers/FsHotWatch.Rules/)
(assembly `FsHotWatch.ConventionAnalyzers`, per the filename rule above) is
`IsPackable=false` — it is never shipped, because these are FsHotWatch's own
conventions, not something to impose on consumers:

| Rule | What it enforces |
|------|------------------|
| `FSHW-CLAIM-001` | A `RunClaim` (the result of `PluginCtx.RunExclusive`) must be handled, never discarded. `TreatWarningsAsErrors` + FS0020 already force the value to be *acknowledged*, but `\|> ignore` remains a legal escape — and silently dropping a refused claim is dropped **work**. |
| `FSHW-CLOCK-001` | No local clocks. Every timestamp in the daemon is UTC; one stray `DateTime.Now` skewed the elapsed a human reads. |

The pattern generalises: when a rule is "the type system *should* make this
unrepresentable, but that refactor is out of scope today", an analyzer is where the
class of bug goes to be caught in the meantime.

## CLI

```bash
# Query analyzer diagnostics
fshw diagnostics
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
