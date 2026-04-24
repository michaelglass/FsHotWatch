# Changelog — TestPrune.Attributes

## [Unreleased]
- feat: initial release. Ships three consumer-side marker attributes that
  TestPrune.Core recognizes without any special-case analyzer code:
  - `[<TestPrune.DependsOn(typeof<T>)>]` — declare a dependency on a type
    the static graph can't see (reflection, DI registration by type,
    runtime plug-ins). The `typeof<T>` symbol use is captured by the
    existing FCS-based edge pipeline.
  - `[<TestPrune.DependsOnFile("relative/path.ext")>]` — mark the
    annotated symbol as depending on a specific non-F# file (snapshots,
    golden files, SQL migrations, config, test data). Editing the file
    invalidates the annotated symbol's downstream tests.
  - `[<TestPrune.DependsOnGlob("pattern/**/*.ext")>]` — same idea,
    matched by a small glob dialect: `**` any segments, `*` within one
    segment, `?` single char, everything else literal. Paths are
    repo-relative forward-slash strings.
- Targets `netstandard2.0` and `net10.0`. Zero runtime dependencies; the
  attributes have no behavior, they're metadata for the indexer.
