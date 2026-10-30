/// An audit of the names the analyzer is allowed to put in `symbols.full_name`.
///
/// `full_name` is UNIQUE and inserts resolve conflicts with `ON CONFLICT DO UPDATE`, so a
/// name that is not the globally unique name of one thing does not fail — it MERGES. Every
/// same-named thing in the repo lands on that one row, which then carries every dependent
/// any of them had. The schema's `symbols_full_name_is_qualified` CHECK is the last line
/// against that, and it exempts exactly one kind: `Module`, because a top-level
/// single-segment module (`module Alpha`) has no qualifier to have.
///
/// That exemption is the whole attack surface. A pass that decides "no dot, therefore a
/// module" relabels every unqualified name into the exempt kind, and the constraint
/// becomes unfailable — the hub reforms wearing `Module`. So these tests assert two things
/// that a green suite must actually have looked at:
///
///   1. the analyzer does not MINT unqualified names (the custom-operation case below), and
///   2. `Module` is only ever chosen on evidence, so an unqualified non-module row still
///      reaches the constraint and is rejected by name.
///
/// Every "nothing unqualified remains" assertion here is paired with a positive control
/// proving the predicate can see such a row, so a zero means "I looked and found nothing".
module TestPrune.Tests.UnqualifiedNameAuditTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Tests.TestHelpers

/// Own checker instance: this file is its own xunit collection, so it runs in parallel
/// with other files but its cases are serialized against each other.
let private checker = FSharpChecker.Create()

let private analyze source =
    let fileName = "/tmp/UnqualifiedNameAuditTests.fsx"

    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    match
        analyzeSource checker fileName source options "TestProject"
        |> Async.RunSynchronously
    with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

/// A row `symbols_full_name_is_qualified` would REJECT.
let private rejectedByTheConstraint (s: SymbolInfo) =
    not (s.FullName.Contains '.') && s.Kind <> Module

/// A row that survives only because it wears the exempt kind. Legitimate for a real
/// top-level single-segment module; the laundering channel for everything else.
let private unqualifiedUnderTheExemption (s: SymbolInfo) =
    not (s.FullName.Contains '.') && s.Kind = Module

/// A computation-expression builder plus a consumer of its custom operations.
///
/// At the USE site FCS reports the operation KEYWORD as the symbol's `FullName` (`where`),
/// not the member it resolves to (`M.QBuilder.Where`) — `LogicalName` and `DeclaringEntity`
/// carry the real identity. Indexing the keyword is what built the hubs: on a v9 index of a
/// ~9,300-test repo, `where` had 451 direct dependents reaching 1,828 test methods.
let private customOperationSource =
    """
module M

type Row = { id: int; name: string }

type QBuilder() =
    member _.Yield(_) = Unchecked.defaultof<Row>

    [<CustomOperation("where", MaintainsVariableSpace = true)>]
    member _.Where(r: Row, [<ProjectionParameter>] f: Row -> bool) = r

    [<CustomOperation("pick")>]
    member _.Pick(r: Row, [<ProjectionParameter>] f: Row -> string) = f r

let q = QBuilder()

let consume () =
    q {
        where (id > 0)
        pick name
    }
"""

[<Fact>]
let ``a custom operation is indexed under its builder member, not its keyword`` () =
    let r = analyze customOperationSource

    let targets =
        r.Dependencies
        |> List.filter (fun d -> d.FromSymbol = "M.consume")
        |> List.map (fun d -> d.ToSymbol)
        |> List.sort

    // The keyword names must not appear at all: they are not symbols, they are syntax.
    test <@ not (targets |> List.contains "where") @>
    test <@ not (targets |> List.contains "pick") @>

    // And the edges must land on the members the keywords resolve to, which is the name
    // the DEFINITION side of the same analysis records — so a change to the builder
    // selects its consumers instead of both vanishing into a node named after syntax.
    test <@ targets |> List.contains "M.QBuilder.Where" @>
    test <@ targets |> List.contains "M.QBuilder.Pick" @>

    let defined = r.Symbols |> List.map (fun s -> s.FullName)
    test <@ defined |> List.contains "M.QBuilder.Where" @>

[<Fact>]
let ``a custom operation use mints no unqualified symbol of any kind`` () =
    let r = analyze customOperationSource

    // `module M` itself is single-segment and legitimately unqualified, so the audit is
    // scoped to the extern placeholders and members — the rows the keyword produced.
    let offenders =
        r.Symbols
        |> List.filter (fun s -> s.FullName <> "M")
        |> List.filter (fun s -> rejectedByTheConstraint s || unqualifiedUnderTheExemption s)
        |> List.map (fun s -> $"%s{s.FullName} (%A{s.Kind}, extern=%b{s.IsExtern})")

    test <@ offenders |> List.isEmpty @>

[<Fact>]
let ``POSITIVE CONTROL - the audit predicates do see the rows they are meant to catch`` () =
    let row name kind isExtern =
        { FullName = name
          Kind = kind
          SourceFile = ExternSourceFile
          LineStart = 0
          LineEnd = 0
          ContentHash = ""
          IsExtern = isExtern }

    // Exactly the shape v9 shipped: the keyword, wearing the exempt kind.
    test <@ unqualifiedUnderTheExemption (row "where" Module true) @>
    // And the shape that must reach the constraint instead.
    test <@ rejectedByTheConstraint (row "where" ExternRef true) @>
    test <@ rejectedByTheConstraint (row "name" Function false) @>
    // Qualified names are never flagged by either.
    test <@ not (rejectedByTheConstraint (row "M.QBuilder.Where" ExternRef true)) @>
    test <@ not (unqualifiedUnderTheExemption (row "M.QBuilder.Where" Module true)) @>

[<Fact>]
let ``an unqualified target with no module evidence stays ExternRef and the DB rejects it`` () =
    // The extern placeholder pass may only choose `Module` on evidence that FCS classified
    // that name as a module. Without evidence the row stays `ExternRef`, so the constraint
    // sees it — this is the path v9 closed off by relabelling.
    let result =
        { Symbols =
            [ { FullName = "M.consume"
                Kind = Function
                SourceFile = "src/M.fs"
                LineStart = 1
                LineEnd = 2
                ContentHash = "h"
                IsExtern = false }
              { FullName = "where"
                Kind = ExternRef
                SourceFile = ExternSourceFile
                LineStart = 0
                LineEnd = 0
                ContentHash = ""
                IsExtern = true } ]
          Dependencies =
            [ { FromSymbol = "M.consume"
                ToSymbol = "where"
                Kind = Calls
                Source = "core" } ]
          TestMethods = []
          Attributes = []
          ParentLinks = []
          Diagnostics = AnalysisDiagnostics.Zero }

    withDb (fun db ->
        let ex = Assert.ThrowsAny<exn>(fun () -> db.RebuildProjects([ result ]))
        Assert.Contains("where", ex.Message, StringComparison.Ordinal)
        Assert.Contains("symbols_full_name_is_qualified", ex.Message, StringComparison.Ordinal))

[<Fact>]
let ``no test is reachable from a query keyword, and the builder member still reaches it`` () =
    let analyzed = analyze customOperationSource

    // `M.consume` stands in for a test that exercises the query. Selection is MEASURED
    // here — seed a name, count the tests that come back — not asserted structurally.
    let result =
        { analyzed with
            TestMethods =
                [ { SymbolFullName = "M.consume"
                    TestProject = "MyTests"
                    TestClass = "Tests"
                    TestMethod = "consume" } ] }

    withDb (fun db ->
        db.RebuildProjects([ result ])

        // The blast radius of the keyword node. Under v9 this was 1: `where` was a real
        // row and the consumer's edge pointed at it, so every consumer of that keyword
        // ANYWHERE in the repo hung off one node — `full_name` is UNIQUE, so they all
        // merge onto it. Measured on a v9 index of a ~9,300-test repo, the same node
        // reached 1,828 test methods. It must now reach none, because it is not a symbol.
        test <@ db.QueryAffectedTests [ "where" ] |> List.isEmpty @>
        test <@ db.QueryAffectedTests [ "pick" ] |> List.isEmpty @>

        // ...and the attribution the keyword was standing in for still works: the member
        // the operation actually resolves to reaches the test. This direction passes
        // under v9 too (aggregate-type invalidation reaches it through `M.QBuilder`), so
        // it is a guard against the fix over-shooting, not evidence for it.
        let selected = db.QueryAffectedTests [ "M.QBuilder.Where" ]
        test <@ selected |> List.map (fun t -> t.TestMethod) = [ "consume" ] @>)
