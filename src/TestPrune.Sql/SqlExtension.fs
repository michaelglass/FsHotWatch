namespace TestPrune.Sql

open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// Extension that injects SharedState edges based on SQL access facts.
/// Facts can be provided directly or discovered by subextensions (e.g., SqlHydra).
type SqlExtension(facts: SqlFact list) =

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling"

        member _.AnalyzeEdges (_symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            SqlCoupling.buildEdges facts
