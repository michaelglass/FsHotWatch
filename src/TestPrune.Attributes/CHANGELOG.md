# Changelog — TestPrune.Attributes

## [Unreleased]
- feat: initial release. Three consumer-side marker attributes the TestPrune
  indexer understands without special-case code:
  - `[<DependsOn(typeof<T>)>]` — reflection / DI-by-type / plug-in edges.
  - `[<DependsOnFile(path)>]` — depend on a specific non-F# file.
  - `[<DependsOnGlob(pattern)>]` — glob-matched variant (`**` crosses
    segments, `*` within one, `?` single non-`/`).
  - `[<CompositionRoot>]` — marks an application composition root (a routing
    table, a DI registration block): a symbol that names the whole application
    in order to wire it up. Relevance does not propagate THROUGH it, but a
    change TO it propagates in full. See TestPrune.Core's changelog for the
    measured effect and, importantly, for when NOT to apply it.
    Requires TestPrune.Core with composition-root support.
- Targets `netstandard2.0` and `net10.0`. Zero runtime dependencies; the
  attributes have no behavior, they're metadata for indexing.
