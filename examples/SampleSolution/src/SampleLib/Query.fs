/// A miniature query computation expression — the code shape TestPrune has to name
/// correctly, kept in the example solution so `mise run example` indexes it on every run.
///
/// At a USE site the compiler service reports a custom operation's full name as the
/// operation KEYWORD (`where`, `select`), not as the member it resolves to
/// (`QueryBuilder.Where`). Indexing the keyword collapses every builder in the repo — and
/// every builder in every referenced library — onto one node called `where`. TestPrune
/// qualifies the operation through its declaring builder instead, so the edge lands on the
/// member the definition side already records.
///
/// This file is the end-to-end guard for that: a regression names these operations
/// `where`/`select`, which have no qualifier, and the schema's
/// `symbols_full_name_is_qualified` CHECK fails the index instead of silently merging.
module SampleLib.Query

open SampleLib.Math

/// A query builder over `int list`, carrying three custom operations.
type QueryBuilder() =

    member _.Yield(_: unit) : int list = []

    /// Custom operation `source` — the list the query runs over.
    [<CustomOperation("source")>]
    member _.Source(_: int list, items: int list) : int list = items

    /// Custom operation `where` — keep the items satisfying `predicate`.
    [<CustomOperation("where")>]
    member _.Where(items: int list, predicate: int -> bool) : int list = items |> List.filter predicate

    /// Custom operation `select` — map every item through `projection`.
    [<CustomOperation("select")>]
    member _.Select(items: int list, projection: int -> int) : int list = items |> List.map projection

/// The builder instance the query expressions below are written against.
let query = QueryBuilder()

/// Double every even number in `items`, via the custom operations above.
///
/// The `select` step calls `SampleLib.Math.multiply`, so this query genuinely depends on
/// `Math` — that is the edge which disappears when an operation is indexed under its
/// keyword rather than under its builder member.
let doubleEvens (items: int list) : int list =
    query {
        source items
        where (fun n -> n % 2 = 0)
        select (multiply 2)
    }
