namespace TestPrune.Sql

open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// Extension that injects SharedState edges based on explicitly provided SQL access facts.
type SqlExtension(facts: SqlFact list) =

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling"

        member _.AnalyzeEdges (_symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            SqlCoupling.buildEdges facts

/// Extension that auto-discovers ReadsFrom/WritesTo attributes from the symbol store
/// and produces SharedState edges based on shared table access.
type AutoSqlExtension() =

    static let parseArgsJson (json: string) : string list =
        json.Trim('[', ']').Split(',')
        |> Array.map (fun s -> s.Trim().Trim('"'))
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

    static member ExtractFacts(symbolStore: SymbolStore) : SqlFact list =
        symbolStore.GetAllSymbols()
        |> List.collect (fun sym ->
            let attrs = symbolStore.GetAttributesForSymbol sym.FullName

            attrs
            |> List.choose (fun (attrName, argsJson) ->
                let args = parseArgsJson argsJson

                match attrName with
                | "ReadsFromAttribute"
                | "ReadsFrom" ->
                    let table = args |> List.tryHead |> Option.defaultValue ""
                    let column = args |> List.tryItem 1 |> Option.defaultValue "*"
                    Some { Symbol = sym.FullName; Table = table; Column = column; Access = Read }
                | "WritesToAttribute"
                | "WritesTo" ->
                    let table = args |> List.tryHead |> Option.defaultValue ""
                    let column = args |> List.tryItem 1 |> Option.defaultValue "*"
                    Some { Symbol = sym.FullName; Table = table; Column = column; Access = Write }
                | _ -> None))

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling (Auto)"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            let facts = AutoSqlExtension.ExtractFacts(symbolStore)
            SqlCoupling.buildEdges facts
