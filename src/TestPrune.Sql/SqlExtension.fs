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

    static let readsFromNames =
        let full = nameof ReadsFromAttribute
        Set.ofList [ full; full.Replace("Attribute", "") ]

    static let writesToNames =
        let full = nameof WritesToAttribute
        Set.ofList [ full; full.Replace("Attribute", "") ]

    static let classifyAttribute (attrName: string) : AccessKind option =
        if readsFromNames.Contains(attrName) then Some Read
        elif writesToNames.Contains(attrName) then Some Write
        else None

    static member ExtractFacts(symbolStore: SymbolStore) : SqlFact list =
        symbolStore.GetAllAttributes()
        |> Map.toList
        |> List.collect (fun (symbolName, attrs) ->
            attrs
            |> List.choose (fun (attrName, argsJson) ->
                classifyAttribute attrName
                |> Option.map (fun access ->
                    let args = parseArgsJson argsJson
                    let table = args |> List.tryHead |> Option.defaultValue ""
                    let column = args |> List.tryItem 1 |> Option.defaultValue "*"

                    { Symbol = symbolName
                      Table = table
                      Column = column
                      Access = access })))

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling (Auto)"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            let facts = AutoSqlExtension.ExtractFacts(symbolStore)
            SqlCoupling.buildEdges facts
