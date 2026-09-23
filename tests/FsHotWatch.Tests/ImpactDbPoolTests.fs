module FsHotWatch.Tests.ImpactDbPoolTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

let private libFooSymbol: TestPrune.AstAnalyzer.SymbolInfo =
    { FullName = "Lib.foo"
      Kind = TestPrune.AstAnalyzer.SymbolKind.Value
      SourceFile = "src/Lib.fs"
      LineStart = 1
      LineEnd = 1
      ContentHash = "hash-v1"
      IsExtern = false }

/// A database at `dbPath` holding one symbol, whose file is then deleted. A pooled
/// connection still reads the deleted file; a new one sees an empty database.
let private seedThenDelete (dbPath: string) =
    let db = TestPrune.Database.Database.create dbPath
    db.RebuildProjects [ TestPrune.AstAnalyzer.AnalysisResult.Create([ libFooSymbol ], [], []) ]
    File.Delete dbPath

let private symbolsAt (dbPath: string) =
    (TestPrune.Database.Database.create dbPath).GetSymbolsInFile "src/Lib.fs"

[<Fact(Timeout = 20000)>]
let ``the key is the connection string the library opens with`` () =
    // A key that differs from the library's clears a different, empty pool: every test
    // still passes and the stale connection is still handed out.
    withTempDir "impact-pool-key" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test-impact.db")
        let db = TestPrune.Database.Database.create dbPath
        use conn = db.OpenConnection()
        test <@ conn.ConnectionString = ImpactDbPool.connectionString dbPath @>)

[<Fact(Timeout = 20000)>]
let ``clearing one database leaves another database's pooled connections alone`` () =
    withTempDir "impact-pool-narrow" (fun tmpDir ->
        let cleared = Path.Combine(tmpDir, "a", "test-impact.db")
        let kept = Path.Combine(tmpDir, "b", "test-impact.db")
        Directory.CreateDirectory(Path.GetDirectoryName cleared) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName kept) |> ignore
        seedThenDelete cleared
        seedThenDelete kept

        ImpactDbPool.clear cleared

        test <@ (symbolsAt cleared).IsEmpty @>
        // PositiveControl: `kept`'s pooled connection survived the clear, so it still
        // reads the deleted file's row.
        test <@ (symbolsAt kept).Length = 1 @>
        ImpactDbPool.clear kept
        test <@ (symbolsAt kept).IsEmpty @>)

[<Fact(Timeout = 20000)>]
let ``no production source clears the process-wide SQLite pool`` () =
    // In a repository host every session's databases share one process: a process-wide
    // clear when one session ends drops its siblings' connections mid-use.
    // Spelled in halves so this guard is not itself a match for what it forbids.
    let processWideClear = "ClearAll" + "Pools"

    let src =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../../../src"))

    let offenders =
        Directory.GetDirectories src
        |> Array.collect (fun project -> Directory.GetFiles(project, "*.fs"))
        |> Array.filter (fun path ->
            File.ReadAllLines path
            |> Array.exists (fun line -> not ((line.TrimStart()).StartsWith "//") && line.Contains processWideClear))
        |> List.ofArray

    test <@ Directory.Exists src @>
    Assert.True(List.isEmpty offenders, $"these sources clear every pool in the process: %A{offenders}")
