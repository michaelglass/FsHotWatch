# FsHotWatch.Lint

Plugin that runs [FSharpLint](https://fsprojects.github.io/FSharpLint/)
using the warm FSharpChecker's parse and check results -- no re-parsing needed.

## Why

FSharpLint normally parses your entire project from scratch every time
you run it. With FsHotWatch, the compiler is already warm -- the lint
plugin calls `lintParsedSource` directly with the AST and type-check
results that are already in memory, so linting takes milliseconds.

## How it works

1. You save a file
2. The daemon type-checks it with the warm FSharpChecker
3. LintPlugin receives `FileChecked` with parse results and check results
4. It calls `FSharpLint.lintParsedSource` with the warm AST
5. Warnings are reported to the error ledger

## Configuration

In `.fs-hot-watch.json`:

```json
{
  "lint": true
}
```

Set `"lint": false` to disable. The plugin automatically loads
`fsharplint.json` from your repo root if it exists.

## CLI

```bash
# Run linter on all files and show warnings
fs-hot-watch lint

# Query lint warning count
fs-hot-watch warnings

# Show all errors (including lint warnings)
fs-hot-watch errors
```

## Programmatic usage

```fsharp
daemon.RegisterHandler(
    LintPlugin.create
        (Some "fsharplint.json")   // config path (or None for defaults)
        None                        // getCommitId for caching
)
```

## Install

```bash
dotnet add package FsHotWatch.Lint
```
