# Changelog — TestPrune.Attributes

## [Unreleased]
- feat: initial release. Four consumer-side marker attributes, three of which
  widen impact analysis and one of which narrows it:
  - `[<DependsOn(typeof<T>)>]` — reflection / DI-by-type / plug-in edges.
  - `[<DependsOnFile(path)>]` — depend on a specific non-F# file.
  - `[<DependsOnGlob(pattern)>]` — glob-matched variant (`**` crosses
    segments, `*` within one, `?` single non-`/`).
  - `[<CompositionRoot>]` — marks an application composition root (a routing
    table, a DI registration block): a symbol that names the whole application
    in order to wire it up. Relevance does not propagate THROUGH it, but a
    change TO it propagates in full. This is the only marker that makes
    TestPrune run FEWER tests, so it is the only one that can hide a real
    failure. See TestPrune.Core's changelog for the measured effect and,
    importantly, for when NOT to apply it. Requires TestPrune.Core with
    composition-root support.
- Targets `netstandard2.0` and `net10.0`. Zero runtime dependencies; the
  attributes have no behavior, they're metadata for indexing.
- **This package is not published to NuGet, and does not need to be.** TestPrune
  matches all four attributes by type NAME off the syntax tree — the namespace is
  ignored and the assembly is never loaded — so a consumer declares the ones it
  uses directly (`type CompositionRootAttribute() = inherit System.Attribute()`).
  The project exists so TestPrune's own tests and examples share one definition.
  See the "You declare the attributes yourself" section of the root README.
