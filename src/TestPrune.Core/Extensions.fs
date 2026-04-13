module TestPrune.Extensions

open TestPrune.AstAnalyzer
open TestPrune.Ports

/// Result from an extension's test selection.
type AffectedTest =
    { TestProject: string
      TestClass: string }

/// Extension interface for custom dependency sources beyond AST analysis.
/// Implement this to add framework-specific edge injection (e.g., SQL table coupling,
/// route-based dependencies, or manual hints).
type ITestPruneExtension =
    /// Unique name for this extension (used in logging and edge source attribution).
    abstract Name: string

    /// Given a symbol store and a list of changed source files (repo-relative paths),
    /// return additional dependency edges to inject into the graph.
    abstract AnalyzeEdges: symbolStore: SymbolStore -> changedFiles: string list -> repoRoot: string -> Dependency list
