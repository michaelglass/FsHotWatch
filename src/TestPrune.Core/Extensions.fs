module TestPrune.Extensions

open TestPrune.AstAnalyzer
open TestPrune.Ports

/// Extension interface for custom dependency sources beyond AST analysis.
/// Implement this to add framework-specific edge injection (e.g., SQL table coupling,
/// route-based dependencies, or manual hints).
///
/// An extension returns EDGES, not test selections: core walks them transitively along
/// with the AST's own, so an extension only has to state the single hop the AST cannot
/// see. `EdgeEmission.edgesTo` is the shared, tested way to build those edges — reach for
/// it before hand-rolling a product of dependents and symbols.
///
/// An extension whose facts come from outside the AST and must survive between processes
/// (a route table seeded at build time, say) can own storage of its own inside TestPrune's
/// cache database via `Ports.PluginStore`.
type ITestPruneExtension =
    /// Unique name for this extension (used in logging and edge source attribution).
    abstract Name: string

    /// Given a symbol store and a list of changed source files (repo-relative paths),
    /// return additional dependency edges to inject into the graph.
    abstract AnalyzeEdges: symbolStore: SymbolStore -> changedFiles: string list -> repoRoot: string -> Dependency list
