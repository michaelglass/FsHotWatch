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

    let private columnsMatch (a: string) (b: string) = a = "*" || b = "*" || a = b

    /// For each (table, column) pair, connect every writer to every reader.
    /// Wildcard columns ("*") match any specific column.
    let buildEdges (facts: SqlFact list) : Dependency list =
        let byTable = facts |> List.groupBy (fun f -> f.Table)

        byTable
        |> List.collect (fun (_, tableFacts) ->
            let writers = tableFacts |> List.filter (fun f -> f.Access = Write)
            let readers = tableFacts |> List.filter (fun f -> f.Access = Read)

            [ for w in writers do
                  for r in readers do
                      if w.Symbol <> r.Symbol && columnsMatch w.Column r.Column then
                          // Reader depends on writer: when writer changes, reader is affected
                          { FromSymbol = r.Symbol
                            ToSymbol = w.Symbol
                            Kind = SharedState
                            Source = "sql" } ])
