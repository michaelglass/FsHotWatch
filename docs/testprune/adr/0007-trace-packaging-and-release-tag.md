# ADR 0007 — Trace packaging: an F# net8.0 recorder, and one trace-v tag for three packages

Status: Accepted (2026-09-26)

## Context

Tracing has three parts with different constraints. The recorder is loaded into a
consumer's test process, so it must not raise any floor there. The weaver and trace store
need Mono.Cecil and a second SQLite lifecycle, which TestPrune.Core's consumers (the CLI,
FsHotWatch, every extension) should not carry. The measurement verbs read the trace store
and depend on the weaver. And the weaver emits calls to recorder methods by name, resolved
from the recorder assembly it finds at `typeof<Probes>.Assembly.Location`.

## Decision

- `TestPrune.Trace.Recorder` is F#, targets net8.0, and pins FSharp.Core to 8.0.403 with
  `DisableImplicitFSharpCoreReference`. It has no other dependency. F# keeps one language
  in the repository and its release tooling; the recorder needs nothing F# lacks.
- `TestPrune.Trace` (net10.0) holds the weaver, shadow bin, ingestion, trace store and
  measurements, and references TestPrune.Core and the recorder.
- `TestPrune.Trace.Cli` is the `test-prune-traces` tool. It is not a verb of the `test-prune`
  CLI: that CLI releases under `core-v` together with Core, and Core must release before
  Trace, so a CLI dependency on Trace would make the release order circular.
- All three release under one `trace-v` tag, after Core. The tagger's primary project is
  `TestPrune.Trace`; the recorder and the CLI are `fsProjsSharingSameTag`. One tag means a
  weaver can never be published against a recorder of another version.

## Consequences

`mise run release` tags `trace-v` in the same level as Falco and Sql, then waits until all
three packages restore from nuget.org. The semantic tagger diffs only the primary project's
public API, so a breaking change to the recorder's `Scopes` contract, which consumers bind
by reflection, must be declared in `src/TestPrune.Trace.Recorder/CHANGELOG.md` with a
`feat!:` or `BREAKING CHANGE:` entry. The implementation plan named the recorder as the
primary project; `TestPrune.Trace` was chosen instead because its host API is the larger
surface and the one that changes most, so the API diff covers more of what can break.

The tagger also decides whether a package changed from the primary project's directory and
its `ProjectReference` closure, not from the shared projects. With `TestPrune.Trace` as the
primary, a recorder change is seen (Trace references the recorder), but a change confined to
`TestPrune.Trace.Cli` does not by itself trigger a `trace-v` release. The same holds today for
the `test-prune` CLI under `core-v`. Until the tagger counts shared projects, a CLI-only fix
ships with the next change to the primary or its references.
