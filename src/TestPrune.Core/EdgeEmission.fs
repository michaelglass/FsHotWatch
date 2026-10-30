/// Shared edge-emission for extensions that inject out-of-band dependencies.
///
/// An extension exists to teach TestPrune about coupling the AST cannot see: an HTTP
/// route served by a handler, a database table two symbols share, a fixture wired up by
/// convention. Every such extension has to answer the same question — *which* symbols do
/// these dependents actually depend on across that boundary? — and both bugs TestPrune has
/// shipped came from a plugin answering it by hand:
///
/// * OVER-selection (TestPrune.Falco 2.0.3): edges were the cross-product of every test
///   matched by the changed FILE's routes and every symbol in that file, so touching one
///   handler in a multi-route file re-ran every route's browser tests.
/// * UNDER-selection (TestPrune.SqlHydra, core 5.0.0): the scoping step kept only the
///   FIRST candidate it found (`List.tryHead`) and silently dropped the rest, so genuinely
///   affected tests were never selected — the one failure mode a test-impact tool must not
///   have.
///
/// `edgesTo` is that step, done once: emit an edge from each dependent to the specific
/// symbol it depends on; scope precisely when the fact names a symbol we can resolve; fall
/// back to the whole candidate set when it does not. Never a cross-product, never empty.
module TestPrune.EdgeEmission

open TestPrune.AstAnalyzer

/// The far side of an out-of-band boundary, as the seeded fact describes it.
type EdgeTarget =
    /// The fact names the symbol its dependents reach across the boundary — a route's
    /// handler function, say. Resolved against the candidates by exact full name or by
    /// dotted suffix, because a seed typically carries the short `Module.function` while
    /// the symbol store holds the fully-qualified name.
    | NamedSymbol of symbolName: string
    /// The fact cannot name a symbol (legacy or unresolved seed data). Every candidate is
    /// a target: coarse, but a superset is safe where a gap is not.
    | UnnamedSymbol

/// The symbols an `EdgeTarget` picks out of `candidates` (typically the symbols of the
/// changed file the fact points at).
///
/// A `NamedSymbol` that matches nothing falls back to the full candidate set rather than
/// to the empty set. A seed can name a function that has since been renamed or moved, and
/// the honest reading of "this file's routes have tests, but I can no longer tell you which
/// function serves them" is the coarse answer — not "no tests are affected".
let resolveTargets (candidates: SymbolInfo list) (target: EdgeTarget) : SymbolInfo list =
    match target with
    | UnnamedSymbol -> candidates
    | NamedSymbol name ->
        let scoped =
            candidates
            |> List.filter (fun s -> s.FullName = name || s.FullName.EndsWith($".%s{name}"))

        if scoped.IsEmpty then candidates else scoped

/// Emit an edge from each dependent (typically a test method) to each symbol it depends on
/// across an out-of-band boundary, scoped by `target`.
///
/// Scoping to the DIRECT symbol is sufficient — a plugin never has to widen an edge to the
/// callees of its target, or to the target's file, to be safe. `SymbolStore.QueryAffectedTests`
/// is a recursive TRANSITIVE reverse-walk of the dependency graph, so a single edge
/// `test → handler` already selects that test when anything the handler calls (transitively,
/// through the ordinary AST `Calls`/`UsesType` edges) changes. Widening the edge set by hand
/// only buys duplicates of what the walk already reaches — and, as Falco 2.0.3 showed, tests
/// it should never have reached at all.
///
/// Self-edges are dropped: a symbol does not depend on itself across a boundary.
let edgesTo
    (source: string)
    (kind: DependencyKind)
    (candidates: SymbolInfo list)
    (target: EdgeTarget)
    (dependents: SymbolInfo list)
    : Dependency list =
    resolveTargets candidates target
    |> List.collect (fun t ->
        dependents
        |> List.choose (fun d ->
            if d.FullName = t.FullName then
                None
            else
                Some
                    { FromSymbol = d.FullName
                      ToSymbol = t.FullName
                      Kind = kind
                      Source = source }))
    |> List.distinct
