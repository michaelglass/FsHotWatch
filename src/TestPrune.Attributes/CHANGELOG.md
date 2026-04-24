# Changelog — TestPrune.Attributes

## [Unreleased]
- feat: initial release. Three consumer-side marker attributes the TestPrune
  indexer understands without special-case code:
  - `[<DependsOn(typeof<T>)>]` — reflection / DI-by-type / plug-in edges.
  - `[<DependsOnFile(path)>]` — depend on a specific non-F# file.
  - `[<DependsOnGlob(pattern)>]` — glob-matched variant (`**` crosses
    segments, `*` within one, `?` single non-`/`).
- Targets `netstandard2.0` and `net10.0`. Zero runtime dependencies; the
  attributes have no behavior, they're metadata for indexing.
