# FsHotWatch.Fantomas

Plugin and preprocessor for [Fantomas](https://github.com/fsprojects/fantomas)
formatting. This package provides two components:

1. **FormatPreprocessor** -- automatically formats files on save (before
   other plugins see the change)
2. **FormatCheckPlugin** -- reports which files are not properly formatted
   (read-only, for CI)

## Why

Running `dotnet fantomas` on every save is slow because it starts a new
process each time. The FormatPreprocessor runs in-process and formats
only the changed files, so formatting happens in milliseconds.

The preprocessor runs *before* other plugins receive the `FileChanged`
event, so format-on-save doesn't re-trigger the entire pipeline.

## How it works

**FormatPreprocessor (format-on-save):**
1. You save a file
2. FormatPreprocessor receives the changed files *before* other plugins
3. It formats each file with Fantomas in-process
4. If the file changed, it writes the formatted version
5. The daemon suppresses re-trigger events for files the preprocessor wrote

**FormatCheckPlugin (format check):**
1. A file change event reaches the plugin
2. It formats the file in memory and compares with the original
3. Unformatted files are tracked and reported to the error ledger

## Configuration

In `.fs-hot-watch.json`:

```json
{
  "format": true
}
```

Set `"format": false` to disable the format-on-save preprocessor.
The format check plugin always runs alongside the preprocessor.

## CLI

```bash
# Format all files
fs-hot-watch format

# Query which files are unformatted
fs-hot-watch unformatted
```

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
// Format-on-save preprocessor (runs before other plugins)
daemon.RegisterPreprocessor(FormatPreprocessor())

// Read-only format check plugin (reports unformatted files)
daemon.RegisterHandler(
    FormatCheckPlugin.createFormatCheck
        None   // getCommitId for caching
)
```

## Install

```bash
dotnet add package FsHotWatch.Fantomas
```
