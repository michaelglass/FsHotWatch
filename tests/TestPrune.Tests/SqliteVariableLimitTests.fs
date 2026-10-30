/// Every query that tests membership against a caller-supplied list of names has a
/// ceiling on how long that list may be, and the ceiling is not a TestPrune constant —
/// it is SQLite's `SQLITE_MAX_VARIABLE_NUMBER`, the cap on host parameters in one
/// prepared statement. `IN (@p0, @p1, ..., @pN)` spends one parameter per name, so the
/// ceiling is reached by repository size, not by anything a caller can see.
///
/// That is why it went unnoticed: the impact-filtered path passes a handful of changed
/// symbols, and only a full unfiltered pass passes the whole graph. On a 64,913-symbol
/// index the flush raised `SQLite Error 1: 'too many SQL variables'.` and failed the
/// run wholesale.
///
/// So these tests cross the ceiling on purpose. Each one pads a real, asserted result
/// set out past the limit and requires the real answer back: a fix that merely stopped
/// throwing — by chunking away the seeds, say — would return the wrong set and fail
/// here. `SQLITE_MAX_VARIABLE_NUMBER is a real ceiling` is the positive control: it
/// proves the limit these tests claim to cross actually exists in the SQLite build
/// loaded at runtime, so a green here cannot mean "the padding was never large enough".
module TestPrune.Tests.SqliteVariableLimitTests

open System
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Tests.TestHelpers

/// The host-parameter cap of the SQLite build actually loaded, read from that build
/// rather than assumed. The documented default moved from 999 to 32766 in SQLite 3.32,
/// and the value is a compile-time option, so hardcoding either number would make these
/// tests describe a different library than the one under test.
let private maxVariableNumber =
    use conn = new SqliteConnection("Data Source=:memory:")
    conn.Open()

    let compiledValue =
        seq { 0..500 }
        |> Seq.map (fun n ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- $"SELECT sqlite_compileoption_get(%d{n})"

            match cmd.ExecuteScalar() with
            | :? string as option -> Some option
            | _ -> None)
        |> Seq.takeWhile Option.isSome
        |> Seq.choose id
        |> Seq.tryPick (fun option ->
            if option.StartsWith("MAX_VARIABLE_NUMBER=", StringComparison.Ordinal) then
                Some(int (option.Substring("MAX_VARIABLE_NUMBER=".Length)))
            else
                None)

    // Not compiled with an explicit value: fall back to the documented default for
    // SQLite 3.32 and later.
    compiledValue |> Option.defaultValue 32766

/// One more name than the loaded SQLite build can bind in a single statement. Every
/// padded list below is this long, so each test is crossing a measured ceiling rather
/// than a remembered one.
let private overTheLimit = maxVariableNumber + 1

/// `names` padded out to `overTheLimit` entries with filler that matches nothing in the
/// graph. The real names stay first so an assertion failure reads clearly.
let private padded (names: string list) =
    let filler =
        seq { 1 .. overTheLimit - names.Length }
        |> Seq.map (fun i -> $"Padding.Absent.Symbol%d{i}")
        |> List.ofSeq

    names @ filler

let private symbol fullName kind sourceFile =
    { FullName = fullName
      Kind = kind
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 2
      ContentHash = ""
      IsExtern = false }

let private externSymbol fullName =
    { symbol fullName Function "_extern" with
        IsExtern = true }

let private dependency from' to' kind =
    { FromSymbol = from'
      ToSymbol = to'
      Kind = kind
      Source = "test" }

module ``The ceiling these tests cross`` =

    /// Positive control. Without this, a green above could mean the padding never
    /// reached a limit that exists — the tests would be asserting nothing.
    [<Fact>]
    let ``SQLITE_MAX_VARIABLE_NUMBER is a real ceiling`` () =
        use conn = new SqliteConnection("Data Source=:memory:")
        conn.Open()

        let bindN n =
            use cmd = conn.CreateCommand()
            let placeholders = seq { 0 .. n - 1 } |> Seq.map (fun i -> $"@p%d{i}")
            cmd.CommandText <- "SELECT 1 WHERE 1 IN (" + String.Join(", ", placeholders) + ")"

            for i in 0 .. n - 1 do
                cmd.Parameters.AddWithValue($"@p%d{i}", i) |> ignore

            cmd.ExecuteScalar() |> ignore

        // At the limit: fine.
        bindN maxVariableNumber

        // One past it: the exact failure a full-suite flush hit.
        let ex = Assert.Throws<SqliteException>(fun () -> bindN overTheLimit)
        test <@ ex.Message.Contains "too many SQL variables" @>

module ``Name lists longer than the variable limit`` =

    [<Fact>]
    let ``GetPriorSharedLiteralSeeds returns the seed`` () =
        withDb (fun db ->
            let literalNode = "TestPrune.__Literal__.0123456789abcdef"

            let result =
                { Symbols = [ symbol "Producer.emit" Function "src/Producer.fs"; externSymbol literalNode ]
                  Dependencies = [ dependency literalNode "Producer.emit" SharedLiteral ]
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects [ result ]

            // Bound before asserting: an inline `padded` call inside the quotation makes
            // Unquote print all 32,767 names on failure.
            let seeds = db.GetPriorSharedLiteralSeeds(padded [ "Producer.emit" ])

            test <@ seeds = [ literalNode ] @>)

    [<Fact>]
    let ``QueryAffectedTests returns the affected test`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ symbol "Lib.changed" Function "src/Lib.fs"
                      symbol "Tests.coversIt" Function "tests/Tests.fs" ]
                  Dependencies = [ dependency "Tests.coversIt" "Lib.changed" Calls ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.coversIt"
                        TestProject = "Tests"
                        TestClass = "Tests"
                        TestMethod = "coversIt" } ]
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects [ result ]

            let affected = db.QueryAffectedTests(padded [ "Lib.changed" ])

            test
                <@
                    affected
                    |> List.map (fun t -> t.SymbolFullName)
                    |> List.contains "Tests.coversIt"
                @>)

    [<Fact>]
    let ``GetReachableSymbols returns the reachable symbol`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ symbol "Lib.root" Function "src/Lib.fs"
                      symbol "Lib.leaf" Function "src/Lib.fs" ]
                  Dependencies = [ dependency "Lib.root" "Lib.leaf" Calls ]
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects [ result ]

            let reachable = db.GetReachableSymbols(padded [ "Lib.root" ])

            test <@ reachable |> Set.contains "Lib.leaf" @>)

    [<Fact>]
    let ``GetIncomingEdgesBatch returns the incoming edge`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ symbol "Lib.callee" Function "src/Lib.fs"
                      symbol "Lib.caller" Function "src/Lib.fs" ]
                  Dependencies = [ dependency "Lib.caller" "Lib.callee" Calls ]
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects [ result ]

            let incoming = db.GetIncomingEdgesBatch(padded [ "Lib.callee" ])

            test <@ incoming |> Map.tryFind "Lib.callee" = Some [ "Lib.caller" ] @>)

module ``Source-file lists longer than the variable limit`` =

    /// `RebuildProjects` clears each re-indexed file's outgoing metadata with
    /// `source_file IN (...)`, so its ceiling is the number of files in one pass. A
    /// repository that large is not TestPrune's today, but the limit is the same limit,
    /// and it is reached by counting rather than by any caller's choice.
    [<Fact>]
    let ``RebuildProjects indexes more files than the limit`` () =
        withDb (fun db ->
            let symbols =
                [ for i in 1..overTheLimit -> symbol $"Wide.Module%d{i}.value" Function $"src/Wide%d{i}.fs" ]

            let result =
                { Symbols = symbols
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects [ result ]

            let firstFile = db.GetSymbolsInFile "src/Wide1.fs" |> List.map (fun s -> s.FullName)

            test <@ firstFile = [ "Wide.Module1.value" ] @>

            // Re-indexing the same files takes the clear-then-reinsert path over the
            // same oversized `IN (...)` list.
            db.RebuildProjects [ result ]

            let indexed = db.GetAllSymbolNames() |> Set.count

            test <@ indexed = overTheLimit @>)
