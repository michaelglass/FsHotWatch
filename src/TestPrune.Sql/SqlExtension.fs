namespace TestPrune.Sql

open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// Extension that injects SharedState edges based on explicitly provided SQL access facts.
type SqlExtension(facts: SqlFact list) =

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_repoRoot: string) =
            SqlCoupling.buildEdges (symbolStore.GetTestMethodSymbolNames()) facts

/// Extension that auto-discovers ReadsFrom/WritesTo attributes from the symbol store
/// and produces SharedState edges based on shared table access.
type AutoSqlExtension() =

    /// The arguments as text (see `AttributeArgs`). A payload that cannot be read is
    /// reported and read as no arguments, which couples it to every other such fact on the
    /// table-less group, as an unreadable payload always did.
    static let parseArgsJson (symbolName: string) (json: string) : string list =
        match TestPrune.AttributeArgs.parse json with
        // An empty argument reads as absent: `ReadsFrom("t", "")` is table-level, as before.
        | Ok args -> TestPrune.AttributeArgs.texts args |> List.filter (fun s -> s <> "")
        | Error reason ->
            eprintfn $"Warning: SQL attribute on %s{symbolName} is unreadable: %s{reason}"
            []

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
                    let args = parseArgsJson symbolName argsJson
                    let table = args |> List.tryHead |> Option.defaultValue ""

                    let column =
                        args
                        |> List.tryItem 1
                        |> Option.map SqlColumn.parse
                        |> Option.defaultValue AllColumns

                    { Symbol = symbolName
                      Table = table
                      Column = column
                      Access = access })))

    interface ITestPruneExtension with
        member _.Name = "SQL Coupling (Auto)"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_repoRoot: string) =
            let facts = AutoSqlExtension.ExtractFacts(symbolStore)
            SqlCoupling.buildEdges (symbolStore.GetTestMethodSymbolNames()) facts
