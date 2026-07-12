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

            // EVERY DSL access this symbol performs, de-duplicated — not just the first.
            //
            // A `Dependency` carries no source range and `GetDependenciesFromFile` has no
            // ORDER BY, so we cannot pair a given DSL call with the table it operates on.
            // Keeping only the FIRST access (the old `List.tryHead`) therefore silently
            // discarded every other access, and which one survived was decided by SQLite's
            // row order. An upsert-style symbol that selects and then inserts was recorded
            // as a pure READER: `articles` had no writer at all, so readers of `articles`
            // got no edge to it and their tests were never selected when it changed —
            // under-selection, the one failure mode a test-impact tool must not have.
            //
            // Keeping them all is exact for the common single-access symbol and degrades to
            // a conservative (access x table) product only for a symbol that genuinely mixes
            // reads and writes — where no finer answer is derivable from the data we have.
            // It can only ever ADD edges, so it cannot drop a genuinely-affected test.
            let dslAccesses =
                deps
                |> List.choose (fun d ->
                    if d.Kind = Calls then
                        let i = d.ToSymbol.LastIndexOf('.')
                        let funcName = if i >= 0 then d.ToSymbol.[i + 1 ..] else d.ToSymbol
                        SqlHydraAnalyzer.classifyDslContext funcName
                    else
                        None)
                |> List.distinct

            let tableRefs =
                deps
                |> List.choose (fun d ->
                    if d.Kind = UsesType && d.ToSymbol.Contains(prefix) then
                        SqlHydraAnalyzer.parseTableReference d.ToSymbol
                    else
                        None)

            [ for tref in tableRefs do
                  for access in dslAccesses do
                      { Symbol = sym.FullName
                        Table = tref.Table
                        Column = "*"
                        Access = access } ])

    interface ITestPruneExtension with
        member _.Name = "SqlHydra"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            |> SqlCoupling.buildEdges
            |> List.map (fun d -> { d with Source = "sql-hydra" })
