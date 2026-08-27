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

    /// Classify only calls owned by SqlHydra's query DSL. Matching a terminal
    /// method name alone would turn an unrelated `Other.updateTask` call into a
    /// database write whenever the same function also mentions a generated table.
    let internal classifyDslSymbol (fullName: string) : AccessKind option =
        match fullName with
        | "SqlHydra.Query.selectTask"
        | "SqlHydra.Query.selectAsync"
        | "SqlHydra.Query.select"
        | "SqlHydra.Query.SelectBuilders.selectTask"
        | "SqlHydra.Query.SelectBuilders.selectAsync"
        | "SqlHydra.Query.SelectBuilders.select" -> Some Read
        | "SqlHydra.Query.insertTask"
        | "SqlHydra.Query.insertAsync"
        | "SqlHydra.Query.insert"
        | "SqlHydra.Query.InsertBuilders.insertTask"
        | "SqlHydra.Query.InsertBuilders.insertAsync"
        | "SqlHydra.Query.InsertBuilders.insert"
        | "SqlHydra.Query.updateTask"
        | "SqlHydra.Query.updateAsync"
        | "SqlHydra.Query.update"
        | "SqlHydra.Query.UpdateBuilders.updateTask"
        | "SqlHydra.Query.UpdateBuilders.updateAsync"
        | "SqlHydra.Query.UpdateBuilders.update"
        | "SqlHydra.Query.deleteTask"
        | "SqlHydra.Query.deleteAsync"
        | "SqlHydra.Query.delete"
        | "SqlHydra.Query.DeleteBuilders.deleteTask"
        | "SqlHydra.Query.DeleteBuilders.deleteAsync"
        | "SqlHydra.Query.DeleteBuilders.delete" -> Some Write
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

    /// Parse a generated table value relative to the configured generated-module prefix.
    /// A table has exactly two segments below that prefix: schema and table. Requiring
    /// the dot boundary prevents similarly-named modules from being attributed.
    let internal parseGeneratedTableReference (prefix: string) (fullName: string) : TableReference option =
        let boundary = $"%s{prefix}."

        if
            System.String.IsNullOrWhiteSpace prefix
            || not (fullName.StartsWith(boundary, System.StringComparison.Ordinal))
        then
            None
        else
            let relativeName = fullName.Substring(boundary.Length)
            let parts = relativeName.Split('.')

            if
                parts.Length = 2
                && parts |> Array.forall (System.String.IsNullOrWhiteSpace >> not)
            then
                Some { Schema = parts[0]; Table = parts[1] }
            else
                None

/// Extension that detects SqlHydra query patterns in the dependency graph
/// and produces SharedState edges via SqlCoupling.
type SqlHydraExtension(generatedModulePrefix: string) =

    do
        if System.String.IsNullOrWhiteSpace generatedModulePrefix then
            invalidArg
                (nameof generatedModulePrefix)
                "Generated module prefix must be a dot-separated qualified name without empty or surrounding-whitespace segments"

        let segments = generatedModulePrefix.Split('.')

        if
            generatedModulePrefix <> generatedModulePrefix.Trim()
            || segments |> Array.exists System.String.IsNullOrWhiteSpace
        then
            invalidArg
                (nameof generatedModulePrefix)
                "Generated module prefix must be a dot-separated qualified name without empty or surrounding-whitespace segments"

    static member extractFacts (prefix: string) (store: SymbolStore) : SqlFact list =
        let allSymbols = store.GetAllSymbols() |> List.filter (fun s -> not s.IsExtern)

        let symbolsByName =
            allSymbols |> List.map (fun symbol -> symbol.FullName, symbol) |> Map.ofList

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
            // Keeping only the FIRST access would discard the rest, with SQLite's row order
            // deciding which survived: an upsert-style symbol that selects and then inserts
            // is recorded as a pure READER, its table ends up with no writer at all, and
            // readers of that table get no edge — under-selection, the one failure mode a
            // test-impact tool must not have.
            //
            // Keeping them all is exact for the common single-access symbol and degrades to
            // a conservative (access x table) product only for a symbol that genuinely mixes
            // reads and writes — where no finer answer is derivable from the data we have.
            // It can only ever ADD edges, so it cannot drop a genuinely-affected test.
            let dslAccesses =
                deps
                |> List.choose (fun d ->
                    if d.Kind = Calls then
                        SqlHydraAnalyzer.classifyDslSymbol d.ToSymbol
                    else
                        None)
                |> List.distinct

            let tableRefs =
                deps
                |> List.choose (fun d ->
                    if d.Kind = Calls then
                        symbolsByName
                        |> Map.tryFind d.ToSymbol
                        |> Option.filter (fun target -> target.Kind = Value && not target.IsExtern)
                        |> Option.bind (fun _ -> SqlHydraAnalyzer.parseGeneratedTableReference prefix d.ToSymbol)
                    else
                        None)

            [ for tref in tableRefs do
                  for access in dslAccesses do
                      { Symbol = sym.FullName
                        Table = $"%s{tref.Schema}.%s{tref.Table}"
                        Column = "*"
                        Access = access } ])

    interface ITestPruneExtension with
        member _.Name = "SqlHydra"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            |> SqlCoupling.buildEdges
            |> List.map (fun d -> { d with Source = "sql-hydra" })
