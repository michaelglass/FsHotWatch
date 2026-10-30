namespace TestPrune.Sql

open System

/// Declares that the annotated function reads from a database table.
/// Use with column name for column-level tracking, or without for table-level.
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = true)>]
type ReadsFromAttribute(table: string, column: string) =
    inherit Attribute()
    new(table: string) = ReadsFromAttribute(table, "*")
    member _.Table = table
    member _.Column = column

/// Declares that the annotated function writes to a database table.
/// Use with column name for column-level tracking, or without for table-level.
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = true)>]
type WritesToAttribute(table: string, column: string) =
    inherit Attribute()
    new(table: string) = WritesToAttribute(table, "*")
    member _.Table = table
    member _.Column = column
