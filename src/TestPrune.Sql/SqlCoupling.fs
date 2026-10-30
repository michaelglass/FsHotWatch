namespace TestPrune.Sql

open TestPrune.AstAnalyzer

/// Whether a fact represents a read or write access.
type AccessKind =
    | Read
    | Write

/// A fact about a symbol's database access.
type SqlFact =
    { Symbol: string
      Table: string
      Column: string
      Access: AccessKind }

/// Build SharedState dependency edges from SQL access facts.
module SqlCoupling =

    /// `Dependency.Source` of the edges `buildEdges` returns.
    [<Literal>]
    let Source = "sql"

    /// `Dependency.Source` TestPrune.SqlHydra stamps on the edges it builds here.
    [<Literal>]
    let SqlHydraSource = "sql-hydra"

    /// The walk policy for a suite whose tests each run against their own database state
    /// (truncated and re-seeded per test or per fixture). A changed writer then selects a
    /// test through SQL coupling only when that test reaches the writer through its other
    /// edges, which is what not following the SQL edges at all amounts to (see
    /// `TestPrune.Domain.WalkPolicy`). Pass it to `Database.create(path, policy)`.
    ///
    /// Under-selects when tests share database state: a test that reads rows another
    /// test, a class fixture the graph does not link to it, or a pre-built seed snapshot
    /// wrote. Keep `WalkPolicy.FollowAll` for such a suite.
    let reachabilityGated: TestPrune.Domain.WalkPolicy =
        { TestScopedSources = set [ Source; SqlHydraSource ] }

    let private columnsMatch (a: string) (b: string) = a = "*" || b = "*" || a = b

    /// For each (table, column) pair, connect every writer to every reader.
    /// Wildcard columns ("*") match any specific column.
    ///
    /// A test method in `testMethods` is never a writer. A test that seeds its own rows
    /// writes them only for itself: no reader outside that test observes them. Coupling
    /// the table's readers to it would make them depend on a test, and the impact walk,
    /// reaching that test through anything it calls, would go on from it into every
    /// reader of every table it seeds and all of their tests. A test method is still a
    /// reader: a test that reads a table depends on that table's writers.
    let buildEdges (testMethods: Set<string>) (facts: SqlFact list) : Dependency list =
        let byTable = facts |> List.groupBy (fun f -> f.Table)

        byTable
        |> List.collect (fun (_, tableFacts) ->
            let writers =
                tableFacts
                |> List.filter (fun f -> f.Access = Write && not (testMethods.Contains f.Symbol))

            let readers = tableFacts |> List.filter (fun f -> f.Access = Read)

            [ for w in writers do
                  for r in readers do
                      if w.Symbol <> r.Symbol && columnsMatch w.Column r.Column then
                          // Reader depends on writer: when writer changes, reader is affected
                          { FromSymbol = r.Symbol
                            ToSymbol = w.Symbol
                            Kind = SharedState
                            Source = Source } ])
