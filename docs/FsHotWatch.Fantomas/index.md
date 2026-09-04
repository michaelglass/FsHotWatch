# FsHotWatch.Fantomas

Plugin and preprocessor for [Fantomas](https://github.com/fsprojects/fantomas)
formatting. This package provides two components:

1. **FormatPreprocessor** -- automatically formats files on save (before
   other plugins see the change)
2. **FormatCheckPlugin** -- reports which files are not properly formatted
   (read-only, for CI)

Both run **the Fantomas your repository pins** — `dotnet tool run fantomas`,
resolved from `.config/dotnet-tools.json` — and say so on every status line.
The package links no Fantomas of its own.

> **Status: early alpha, and a lot of it is AI-written.** APIs shift between
> versions and rough edges are expected — your mileage may vary. Issues and PRs
> are very welcome.

## Why

A format verdict is only evidence if it comes from the same formatter CI runs.
Earlier versions of this package linked their own `Fantomas.Core` and formatted
in-process with that library's defaults, while hosted CI ran the repository's
pinned `dotnet fantomas` — its version, its `.editorconfig`. The two agreed by
coincidence, and a local `formatted 0 files` could not say which one had been
consulted, or whether one had been consulted at all.

So the plugin now runs the pinned tool: the same version, the same
configuration discovery, the same ignore files. A pin bump is picked up on the
next event, and the version it ran is on the status line —
`format OK (12 checked) — dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)`.

The preprocessor still runs *before* other plugins receive the `FileChanged`
event, so format-on-save doesn't re-trigger the entire pipeline. A tool start
costs a few hundred milliseconds per batch, amortised over every file in it.

## How it works

**FormatPreprocessor (format-on-save):**
1. You save a file
2. FormatPreprocessor receives the changed files *before* other plugins
3. It reads the `fantomas` pin from `.config/dotnet-tools.json` and runs
   `dotnet tool run fantomas <files>` from the repository root
4. Files whose bytes changed are reported as modified; the daemon suppresses
   re-trigger events for them
5. Its status names what ran: `format: rewrote 1 of 3 file(s) — dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)`

**FormatCheckPlugin (format check):**
1. A file change event reaches the plugin
2. It runs `dotnet tool run fantomas --check <files>` — nothing on disk changes
3. Unformatted files are tracked and reported to the error ledger, each entry
   naming the formatter whose opinion it is; a file the tool cannot parse is an
   error entry

**When there is no pin:** a repository whose manifest does not pin `fantomas`
gets a refusal, not a green. The preprocessor's status is `Failed` with the
manifest path and the remedy (`dotnet tool install fantomas`); the check
plugin's status is `format check refused: …` and its cache key is `None`, so
the refusal is re-earned on every event and never replayed as a verdict.
`fshw format` replies `format refused — format: …` with the same reason.

**What invalidates a cached check:** the file bytes, the pinned version, and
the `.editorconfig` files between the repository root and the file. Any of the
three changing is a cache miss.

## Configuration

In `.fshw.json`:

```json
{
  "format": true
}
```

- `true` registers the format-on-save preprocessor.
- `"check"` registers the read-only check plugin instead (no rewrites; the
  verdict gates on unformatted files).
- `false` disables both.

The pin lives where CI already looks for it:

```bash
dotnet new tool-manifest      # once, if .config/dotnet-tools.json does not exist
dotnet tool install fantomas  # pins the version the plugin runs
```

The daemon logs the resolved pin at startup
(`format: dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)`), or
an error naming the missing pin.

## CLI

```bash
# Format every registered file with the pinned tool. The reply names the set
# and the formatter: `formatted 0 of 312 files — dotnet fantomas 7.0.5 (pinned
# in .config/dotnet-tools.json)`, or `format refused — …` with the reason.
fshw format

# Re-run the check plugin from a cleared cache over every registered file
# ("format": "check" mode). No daemon restart needed.
fshw rerun format-check

# Query which files are unformatted
fshw unformatted
```

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
// Format-on-save preprocessor (runs before other plugins)
daemon.RegisterPreprocessor(FormatPreprocessor())

// Read-only format check plugin (reports unformatted files). Runs the Fantomas
// the repository pins in `.config/dotnet-tools.json`, from `repoRoot`.
daemon.RegisterHandler(
    FormatCheckPlugin.createFormatCheck
        repoRoot
        None   // timeoutSec (None → 60s default)
)
```

Both take an optional per-batch timeout in seconds. The tool is a child
process bounded by it; on expiry the batch is left as it was and the check run
is recorded as timed out.

## Install

```bash
dotnet add package FsHotWatch.Fantomas
```
