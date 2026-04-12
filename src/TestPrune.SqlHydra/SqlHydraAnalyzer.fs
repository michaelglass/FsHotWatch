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
