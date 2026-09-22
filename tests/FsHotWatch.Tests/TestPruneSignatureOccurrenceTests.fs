/// A signature file (`.fsi`) declaration is an occurrence of the symbol its `.fs`
/// implements (TestPrune.Core 10, ADR 0004). End to end through the plugin: editing
/// only the signature must select the consumer's test, and a flush that re-indexes
/// the implementation alone must keep the facts the signature contributed.
module FsHotWatch.Tests.TestPruneSignatureOccurrenceTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

[<Literal>]
let private SignatureSource =
    """module Library
type Token =
    { Value: int }
val compute:
    Token -> int
"""

[<Literal>]
let private ImplementationSource =
    """module Library
type Token = { Value: int }
let compute (token: Token) = token.Value + 1
"""

[<Literal>]
let private TestsSource =
    """module Tests
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let computeTest () =
    assert (Library.compute { Value = 1 } = 2)

[<Fact>]
let unrelatedTest () =
    assert (1 + 1 = 2)
"""

/// A fixture project (`Library.fsi`, `Library.fs`, `Tests.fs`) indexed through the plugin.
type private IndexedFixture =
    { Host: PluginHost
      Pipeline: CheckPipeline
      DbPath: string
      SignatureFile: string
      ImplementationFile: string
      TestsFile: string }

    /// Type-check `path` and hand its result to the plugin.
    member this.Check(path: string) =
        match this.Pipeline.CheckFile(AbsFilePath.create path) |> Async.RunSynchronously with
        | Some result -> emitFileAndQuiesce this.Host result
        | None -> failwith $"CheckFile failed for {path}"

let private indexFixture (tmpDir: string) =
    let signatureFile = Path.Combine(tmpDir, "Library.fsi")
    let implementationFile = Path.Combine(tmpDir, "Library.fs")
    let testsFile = Path.Combine(tmpDir, "Tests.fs")
    File.WriteAllText(signatureFile, SignatureSource)
    File.WriteAllText(implementationFile, ImplementationSource)
    File.WriteAllText(testsFile, TestsSource)

    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let host = createModelHost checker tmpDir
    let dbPath = Path.Combine(tmpDir, "tp.db")

    // `Fixture.fsx` only names the project; the compiled files are the three above.
    let scriptOptions =
        getScriptOptions checker (Path.Combine(tmpDir, "Fixture.fsx")) TestsSource
        |> Async.RunSynchronously

    let options =
        { scriptOptions with
            SourceFiles = [| signatureFile; implementationFile; testsFile |] }

    let testConfigs =
        [ { Project = "Fixture"
            Command = "echo"
            Args = "ok"
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = None
            ReportVerificationFormat = AutoDetect } ]

    host.RegisterHandler(create dbPath tmpDir (Some testConfigs) None None None None [])
    pipeline.RegisterProject(Path.Combine(tmpDir, "Fixture.fsx"), options)
    emitBuildAndWaitTerminal host

    let fixture =
        { Host = host
          Pipeline = pipeline
          DbPath = dbPath
          SignatureFile = signatureFile
          ImplementationFile = implementationFile
          TestsFile = testsFile }

    // All three files are pending together, so ONE flush indexes the signature and the
    // implementation side by side.
    for file in [ signatureFile; implementationFile; testsFile ] do
        fixture.Check file

    emitBatchAndQuiesce host [ signatureFile; implementationFile; testsFile ]
    fixture

let private affectedTests (host: PluginHost) =
    match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
    | Some tests -> tests
    | None -> ""

[<Fact(Timeout = 60000)>]
let ``editing only the signature selects the consumer's test`` () =
    withTempDir "tp-fsi-select" (fun tmpDir ->
        let fixture = indexFixture tmpDir

        // A signature-only edit: name the parameter. The implementation is untouched.
        File.WriteAllText(
            fixture.SignatureFile,
            SignatureSource.Replace("    Token -> int", "    token: Token -> int")
        )

        fixture.Check fixture.SignatureFile

        let mutable selected = ""

        waitUntil
            (fun () ->
                selected <- affectedTests fixture.Host
                selected.Contains "computeTest")
            5000

        test <@ selected.Contains "computeTest" @>
        test <@ not (selected.Contains "unrelatedTest") @>)

[<Fact(Timeout = 60000)>]
let ``re-indexing the implementation alone keeps the signature's own edges`` () =
    withTempDir "tp-fsi-owner" (fun tmpDir ->
        let fixture = indexFixture tmpDir

        let signatureOwnsTokenEdge () =
            clearSqlitePool fixture.DbPath

            (Database.create fixture.DbPath).GetDependenciesFromFile "Library.fsi"
            |> List.exists (fun edge -> edge.FromSymbol = "Library.compute" && edge.ToSymbol = "Library.Token")

        // After the joint flush, the `val compute: Token -> int` edge is the signature's.
        waitUntil signatureOwnsTokenEdge 5000
        test <@ signatureOwnsTokenEdge () @>

        // Change the implementation only, and flush it ALONE.
        File.WriteAllText(fixture.ImplementationFile, ImplementationSource.Replace("+ 1", "+ 2"))
        fixture.Check fixture.ImplementationFile
        emitBatchAndQuiesce fixture.Host [ fixture.ImplementationFile ]

        test <@ signatureOwnsTokenEdge () @>

        let signatureOccurrence =
            (Database.create fixture.DbPath).GetSymbolsInFile "Library.fsi"
            |> List.filter (fun symbol -> symbol.FullName = "Library.compute")

        test <@ signatureOccurrence.Length = 1 @>)

[<Fact>]
let ``a TestPrune v13 index is recreated at the current schema and the FCS check cache cleared`` () =
    withTempDir "tp-v13-recreate" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let cacheDir = Path.Combine(tmpDir, ".fshw", "cache")
        Directory.CreateDirectory cacheDir |> ignore
        File.WriteAllText(Path.Combine(cacheDir, "checked.json"), "{}")

        // A TestPrune.Core 9 index: one `symbols` row per name, carrying the file.
        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath}")
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """CREATE TABLE symbols (id INTEGER PRIMARY KEY, full_name TEXT NOT NULL UNIQUE, source_file TEXT NOT NULL);
                   INSERT INTO symbols (full_name, source_file) VALUES ('TestPrune.__Signature__.Library.compute', 'Library.fsi');
                   PRAGMA user_version = 13;"""

            cmd.ExecuteNonQuery() |> ignore

        clearSqlitePool dbPath

        // Opening the plugin opens the index: the schema-version path recreates it and the
        // plugin logs "TestPrune DB was recreated (schema change)" and clears the cache.
        create dbPath tmpDir None None None None None [] |> ignore

        test <@ not (File.Exists(Path.Combine(cacheDir, "checked.json"))) @>

        clearSqlitePool dbPath
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath}")
        conn.Open()
        use version = conn.CreateCommand()
        version.CommandText <- "PRAGMA user_version;"
        let userVersion = version.ExecuteScalar() :?> int64 |> int
        use tables = conn.CreateCommand()

        tables.CommandText <-
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'symbol_occurrences';"

        let occurrenceTables = tables.ExecuteScalar() :?> int64
        conn.Close()
        clearSqlitePool dbPath

        test <@ userVersion = SchemaVersion && SchemaVersion = 14 @>
        test <@ occurrenceTables = 1L @>)
