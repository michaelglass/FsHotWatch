/// Which files the house rules treat as test sources. Shared, as a linked file, by
/// FsHotWatch.Rules and FsHotWatch.WallClock: the two assemblies are configured with
/// different `analyzers.scope`s, and must still agree on what a test is.
module FsHotWatch.Rules.TestSource

/// Deliberately name-based rather than project-based: an analyzer sees one file at a
/// time and its `FileName` is the only thing that survives into the AST, and everything
/// the test rules name as the fix (`probeLoop`, `waitUntilTrue`, `withWatchedDir`)
/// lives in the test assemblies.
let isTestSource (fileName: string) : bool =
    let normalized = fileName.Replace('\\', '/')
    let name = normalized.Split('/') |> Array.tryLast |> Option.defaultValue ""

    name.EndsWith("Tests.fs", System.StringComparison.Ordinal)
    || name.StartsWith("TestHelpers", System.StringComparison.Ordinal)
    || normalized.Contains("/tests/")
