module TestPrune.Tests.LiteralCouplingTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.InMemoryStore

[<Collection("FCS-LiteralCoupling")>]
module ``AST literal bridge`` =

    let private checker = FSharpChecker.Create()

    let private analyze (fileName: string) (projectName: string) (source: string) =
        let options = getScriptOptions checker fileName source |> Async.RunSynchronously

        match
            analyzeSource checker fileName source options projectName
            |> Async.RunSynchronously
        with
        | Ok result -> result
        | Error error -> failwith $"Analysis failed for {fileName}: {error}"

    let private literalEdges result =
        result.Dependencies
        |> List.filter (fun dependency ->
            dependency.FromSymbol.StartsWith("TestPrune.__Literal__.", StringComparison.Ordinal)
            || dependency.ToSymbol.StartsWith("TestPrune.__Literal__.", StringComparison.Ordinal))

    let private producerSource literal =
        $"""module Producer

let emit () = "{literal}"
"""

    let private testSource literal =
        $"""module Tests

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let assertsMessage () =
    let actual = "{literal}"
    if actual <> "{literal}" then failwith "mismatch"
"""

    [<Fact>]
    let ``an index from before literal edges is rebuilt instead of reused`` () =
        let dbPath =
            Path.Combine(Path.GetTempPath(), $"testprune-literal-schema-{Guid.NewGuid():N}.db")

        try
            let initial = Database.create dbPath

            do
                use connection = initial.OpenConnection()
                use command = connection.CreateCommand()
                command.CommandText <- "CREATE TABLE pre_literal_marker (id INTEGER); PRAGMA user_version = 10;"
                command.ExecuteNonQuery() |> ignore

            let reopened = Database.create dbPath
            test <@ reopened.WasRecreated @>

            use connection = reopened.OpenConnection()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE name = 'pre_literal_marker'"
            test <@ Convert.ToInt32(command.ExecuteScalar()) = 0 @>
        finally
            for suffix in [ ""; "-shm"; "-wal" ] do
                let path = dbPath + suffix

                if File.Exists path then
                    File.Delete path

    [<Fact>]
    let ``normalization preserves the extern sentinel used by literal nodes`` () =
        let symbol =
            { FullName = "TestPrune.__Literal__.0123456789abcdef"
              Kind = ExternRef
              SourceFile = ExternSourceFile
              LineStart = 0
              LineEnd = 0
              ContentHash = ""
              IsExtern = true }

        let normalized = normalizeSymbolPaths "/repo/root" [ symbol ] |> List.exactlyOne
        test <@ normalized.SourceFile = ExternSourceFile @>

    [<Fact>]
    let ``a prose literal bridges its producer to the test that asserts it`` () =
        let literal = "the audit log write failed and dropped the entry"

        let producer =
            analyze "/tmp/LiteralProducer.fsx" "Producer" (producerSource literal)

        let tests = analyze "/tmp/LiteralTests.fsx" "Tests" (testSource literal)
        let producerEdges = literalEdges producer
        let testEdges = literalEdges tests

        test <@ producerEdges.Length = 1 @>
        test <@ testEdges.Length = 1 @>

        let producerEdge = List.exactlyOne producerEdges
        let testEdge = List.exactlyOne testEdges

        test <@ producerEdge.FromSymbol = testEdge.ToSymbol @>
        test <@ producerEdge.ToSymbol = "Producer.emit" @>
        test <@ testEdge.FromSymbol = "Tests.assertsMessage" @>

    [<Fact>]
    let ``FCS-decoded escape spellings join the same literal node`` () =
        let producer =
            analyze
                "/tmp/LiteralEscapedProducer.fsx"
                "Producer"
                (producerSource "the audit log write fail\\u0065d and dropped the entry")

        let tests =
            analyze
                "/tmp/LiteralDecodedTests.fsx"
                "Tests"
                (testSource "the audit log write failed and dropped the entry")

        let producerNode = literalEdges producer |> List.exactlyOne |> _.FromSymbol
        let testNode = literalEdges tests |> List.exactlyOne |> _.ToSymbol

        test <@ producerNode = testNode @>

    [<Fact>]
    let ``literal node names retain the full SHA-256 identity`` () =
        let first =
            analyze
                "/tmp/LiteralDigestOne.fsx"
                "ProducerOne"
                ("module ProducerOne\nlet emit () = \"the audit log write failed and dropped the first entry\"\n")

        let second =
            analyze
                "/tmp/LiteralDigestTwo.fsx"
                "ProducerTwo"
                ("module ProducerTwo\nlet emit () = \"the audit log write failed and dropped the second entry\"\n")

        let firstNode = literalEdges first |> List.exactlyOne |> _.FromSymbol
        let secondNode = literalEdges second |> List.exactlyOne |> _.FromSymbol
        let prefix = "TestPrune.__Literal__."

        test <@ firstNode.StartsWith(prefix, StringComparison.Ordinal) @>
        test <@ firstNode.Length = prefix.Length + 64 @>
        test <@ firstNode <> secondNode @>

    [<Fact>]
    let ``verbatim and triple-quoted spellings join their decoded value`` () =
        let producer =
            analyze
                "/tmp/LiteralVerbatim.fsx"
                "Producer"
                "module Producer\nlet emit () = @\"the audit log \"\"write\"\" failed and dropped the entry\"\n"

        let tests =
            analyze
                "/tmp/LiteralTriple.fsx"
                "Tests"
                ("module Tests\n"
                 + "type FactAttribute() = inherit System.Attribute()\n"
                 + "[<Fact>]\n"
                 + "let assertsMessage () = \"\"\"the audit log \"write\" failed and dropped the entry\"\"\" |> ignore\n")

        let producerNode = literalEdges producer |> List.exactlyOne |> _.FromSymbol
        let testNode = literalEdges tests |> List.exactlyOne |> _.ToSymbol
        test <@ producerNode = testNode @>

    [<Fact>]
    let ``interpolated text does not create a literal bridge`` () =
        let result =
            analyze
                "/tmp/LiteralInterpolation.fsx"
                "Producer"
                """module Producer

let emit count = $"the audit log write failed and dropped {count} entries"
"""

        test <@ literalEdges result |> List.isEmpty @>

    [<Fact>]
    let ``identifier-shaped strings do not create a literal bridge`` () =
        let result =
            analyze "/tmp/LiteralIdentifier.fsx" "Producer" (producerSource "__RequestVerificationToken")

        test <@ literalEdges result |> List.isEmpty @>

    [<Theory>]
    [<InlineData("aaaaaa bbbbbb cccccc ddd", true)>]
    [<InlineData("aaaaaa bbbbbb cccccc dd", false)>]
    [<InlineData("aaaaaaa bbbbbbb cccccccc", false)>]
    let ``only literals meeting both prose boundaries are bridged`` (literal: string) (expected: bool) =
        let result = analyze "/tmp/LiteralThreshold.fsx" "Producer" (producerSource literal)

        test <@ (literalEdges result |> List.isEmpty) = not expected @>

    [<Fact>]
    let ``one producer change fans out to every test sharing the message`` () =
        let literal = "the audit log write failed and dropped the entry"

        let producerOne =
            analyze "/tmp/LiteralFanoutProducerOne.fsx" "Producer" (producerSource literal)

        let producerTwo =
            analyze
                "/tmp/LiteralFanoutProducerTwo.fsx"
                "OtherProducer"
                ("module OtherProducer\nlet emit () = \"" + literal + "\"\n")

        let testOne = analyze "/tmp/LiteralFanoutTestOne.fsx" "TestOne" (testSource literal)

        let testTwo =
            analyze
                "/tmp/LiteralFanoutTestTwo.fsx"
                "TestTwo"
                ((testSource literal).Replace("module Tests", "module OtherTests"))

        let store = fromAnalysisResults [ producerOne; producerTwo; testOne; testTwo ]

        for producerName in [ "Producer.emit"; "OtherProducer.emit" ] do
            let selected = store.QueryAffectedTests [ producerName ]

            test <@ selected |> List.map _.TestProject |> Set.ofList = set [ "TestOne"; "TestTwo" ] @>

    [<Fact>]
    let ``reindexing a producer retires its old literal bridge`` () =
        let oldLiteral = "the audit log write failed and dropped the entry"
        let newLiteral = "the audit outbox write failed and retained the entry"

        let producer =
            analyze "/tmp/LiteralDbProducer.fsx" "Producer" (producerSource oldLiteral)

        let tests = analyze "/tmp/LiteralDbTests.fsx" "Tests" (testSource oldLiteral)

        let dbPath =
            Path.Combine(Path.GetTempPath(), $"testprune-literal-{Guid.NewGuid():N}.db")

        try
            let db = Database.create dbPath
            db.RebuildProjects [ producer; tests ]

            let persistedTestEdge =
                db.GetDependenciesFromFile "/tmp/LiteralDbTests.fsx"
                |> List.filter (fun edge -> edge.Kind = SharedLiteral)
                |> List.exactlyOne

            test <@ persistedTestEdge.FromSymbol = "Tests.assertsMessage" @>

            let oldNode = persistedTestEdge.ToSymbol

            let selectedBefore = db.QueryAffectedTests [ "Producer.emit" ]

            test
                <@
                    selectedBefore
                    |> List.exists (fun item -> item.SymbolFullName = "Tests.assertsMessage")
                @>

            // The caller must capture the OLD literal node before replacing this
            // file's graph. Once the production edge is rebuilt, the changed
            // producer no longer points at the prose the unchanged test still
            // asserts. Losing that old node here is the stale-green this feature
            // exists to prevent.
            let priorLiteralSeeds = db.GetPriorSharedLiteralSeeds [ "Producer.emit" ]

            test <@ priorLiteralSeeds = [ oldNode ] @>

            let changedProducer =
                analyze "/tmp/LiteralDbProducer.fsx" "Producer" (producerSource newLiteral)

            db.RebuildProjects [ changedProducer ]

            let selectedAfter = db.QueryAffectedTests("Producer.emit" :: priorLiteralSeeds)

            test
                <@
                    selectedAfter
                    |> List.exists (fun item -> item.SymbolFullName = "Tests.assertsMessage")
                @>

            // The old test still asserts the old message, so its half of the bridge and
            // the shared node must survive even though the producer half has gone.
            let survivingTestEdge =
                db.GetDependenciesFromFile "/tmp/LiteralDbTests.fsx"
                |> List.filter (fun edge -> edge.Kind = SharedLiteral)
                |> List.exactlyOne

            test <@ survivingTestEdge.ToSymbol = oldNode @>

            do
                use connection = db.OpenConnection()
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT COUNT(*) FROM symbols WHERE full_name = @node"
                command.Parameters.AddWithValue("@node", oldNode) |> ignore
                test <@ Convert.ToInt32(command.ExecuteScalar()) = 1 @>

            let changedTests = analyze "/tmp/LiteralDbTests.fsx" "Tests" (testSource newLiteral)

            db.RebuildProjects [ changedTests ]

            use connection = db.OpenConnection()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT COUNT(*) FROM symbols WHERE full_name = @node"
            command.Parameters.AddWithValue("@node", oldNode) |> ignore
            test <@ Convert.ToInt32(command.ExecuteScalar()) = 0 @>
        finally
            for suffix in [ ""; "-shm"; "-wal" ] do
                let path = dbPath + suffix

                if File.Exists path then
                    File.Delete path
