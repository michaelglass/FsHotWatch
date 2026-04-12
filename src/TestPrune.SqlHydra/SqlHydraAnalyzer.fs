namespace TestPrune.SqlHydra

open TestPrune.Sql

/// Parsed table reference from a SqlHydra generated type name.
type TableReference = { Schema: string; Table: string }

/// Analyzes SqlHydra typed symbol references to automatically produce SQL access facts.
module SqlHydraAnalyzer =

    /// Classify a SqlHydra DSL function name as Read or Write access.
    let classifyDslContext (functionName: string) : AccessKind option =
        match functionName with
        | "selectTask"
        | "selectAsync"
        | "select" -> Some Read
        | "insertTask"
        | "insertAsync"
        | "insert" -> Some Write
        | "updateTask"
        | "updateAsync"
        | "update" -> Some Write
        | "deleteTask"
        | "deleteAsync"
        | "delete" -> Some Write
        | _ -> None

    /// Parse a fully-qualified SqlHydra generated type name to extract schema and table.
    /// SqlHydra generates types like "Generated.public.briefs" or "MyDb.Generated.public.articles".
    /// We look for the last two dotted segments as schema.table.
    let parseTableReference (fullName: string) : TableReference option =
        let parts = fullName.Split('.')

        if parts.Length >= 3 then
            let schema = parts[parts.Length - 2]
            let table = parts[parts.Length - 1]
            Some { Schema = schema; Table = table }
        else
            None

open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// Extension that detects SqlHydra query patterns in the dependency graph
/// and produces SharedState edges via SqlCoupling.
type SqlHydraExtension(generatedModulePrefix: string) =

    /// Extract SqlFacts by analyzing the dependency graph for SqlHydra patterns.
    /// A function is classified as a reader/writer if it:
    /// 1. Has a Calls edge to a SqlHydra DSL function (selectTask, insertTask, etc.)
    /// 2. Has a UsesType edge to a type matching the generated module prefix
    static member extractFacts (generatedModulePrefix: string) (store: SymbolStore) : SqlFact list =
        let allSymbols = store.GetAllSymbols()

        allSymbols
        |> List.collect (fun sym ->
            if sym.IsExtern then
                []
            else
                let deps =
                    store.GetDependenciesFromFile sym.SourceFile
                    |> List.filter (fun d -> d.FromSymbol = sym.FullName)

                // Find DSL function calls (selectTask, insertTask, etc.)
                let dslAccess =
                    deps
                    |> List.choose (fun d ->
                        if d.Kind = Calls then
                            let funcName =
                                let i = d.ToSymbol.LastIndexOf('.')
                                if i >= 0 then d.ToSymbol.[i + 1 ..] else d.ToSymbol

                            SqlHydraAnalyzer.classifyDslContext funcName
                        else
                            None)
                    |> List.tryHead

                // Find SqlHydra table type references
                let tableRefs =
                    deps
                    |> List.choose (fun d ->
                        if d.Kind = UsesType && d.ToSymbol.Contains(generatedModulePrefix) then
                            SqlHydraAnalyzer.parseTableReference d.ToSymbol
                        else
                            None)

                match dslAccess with
                | Some access ->
                    tableRefs
                    |> List.map (fun tref ->
                        { Symbol = sym.FullName
                          Table = tref.Table
                          Column = "*"
                          Access = access })
                | None -> [])

    interface ITestPruneExtension with
        member _.Name = "SqlHydra"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            let facts = SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            SqlCoupling.buildEdges facts
            |> List.map (fun d -> { d with Source = "sql-hydra" })
