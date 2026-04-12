namespace TestPrune.SqlHydra

open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports
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

/// Extension that detects SqlHydra query patterns in the dependency graph
/// and produces SharedState edges via SqlCoupling.
type SqlHydraExtension(generatedModulePrefix: string) =

    static member extractFacts (prefix: string) (store: SymbolStore) : SqlFact list =
        let allSymbols = store.GetAllSymbols() |> List.filter (fun s -> not s.IsExtern)

        let depsByFile =
            allSymbols
            |> List.map (fun s -> s.SourceFile)
            |> List.distinct
            |> List.map (fun f -> f, store.GetDependenciesFromFile f)
            |> Map.ofList

        allSymbols
        |> List.collect (fun sym ->
            let deps =
                depsByFile
                |> Map.tryFind sym.SourceFile
                |> Option.defaultValue []
                |> List.filter (fun d -> d.FromSymbol = sym.FullName)

            let dslAccess =
                deps
                |> List.choose (fun d ->
                    if d.Kind = Calls then
                        let i = d.ToSymbol.LastIndexOf('.')
                        let funcName = if i >= 0 then d.ToSymbol.[i + 1 ..] else d.ToSymbol
                        SqlHydraAnalyzer.classifyDslContext funcName
                    else
                        None)
                |> List.tryHead

            let tableRefs =
                deps
                |> List.choose (fun d ->
                    if d.Kind = UsesType && d.ToSymbol.Contains(prefix) then
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
            SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            |> SqlCoupling.buildEdges
            |> List.map (fun d -> { d with Source = "sql-hydra" })
