module TestPrune.Extensions

open TestPrune.AstAnalyzer
open TestPrune.Database
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

    /// Return EVERY dependency edge this extension contributes for the tree as it stands:
    /// the indexed symbols in `symbolStore` plus whatever the extension reads under
    /// `repoRoot`. A host stores the result in place of the extension's previous edges
    /// (`refreshExtensionEdges`), so the answer must be a function of the tree alone —
    /// an edge left out is an edge deleted, whatever changed since the last call.
    ///
    /// Called once per index build. An extension instance that outlives one build must
    /// not let anything it read from the repo on an earlier call stand in for the repo as
    /// it is now; a cache keyed by the content it was derived from is safe.
    abstract AnalyzeEdges: symbolStore: SymbolStore -> repoRoot: string -> Dependency list

/// How one extension's refresh ended.
type ExtensionRefresh =
    /// The extension's stored edges were replaced with this many edges.
    | Refreshed of extensionName: string * edgeCount: int
    /// `AnalyzeEdges` threw. The extension's previously stored edges are kept: an
    /// extension that cannot answer this build has not said its edges are gone, and
    /// deleting them would silently drop the tests they select.
    | Failed of extensionName: string * error: exn

/// Run every extension over the current tree and replace each one's stored edges with
/// its answer (`Database.ReplaceExtensionEdges`). Call after the build's own AST results
/// are written, so the symbols the extensions resolve against are current.
let refreshExtensionEdges
    (db: Database)
    (repoRoot: string)
    (extensions: ITestPruneExtension list)
    : ExtensionRefresh list =
    let store = toSymbolStore db

    extensions
    |> List.map (fun extension ->
        let answer =
            try
                Ok(extension.AnalyzeEdges store repoRoot)
            with ex ->
                Error ex

        match answer with
        | Ok edges ->
            db.ReplaceExtensionEdges(extension.Name, edges)
            Refreshed(extension.Name, edges.Length)
        | Error ex -> Failed(extension.Name, ex))
