module TestPrune.Tests.LiteralCouplingTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
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

    let private referenceSharedLiterals (tree: ParsedInput) =
        let found = ResizeArray<string * range>()

        let isSharedLiteral (text: string) =
            text.Length >= 24
            && text.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries).Length
               >= 4

        let rec walk (value: obj) =
            if not (isNull value) then
                match value with
                | :? string -> ()
                | :? SynExpr as expression ->
                    match expression with
                    | SynExpr.Const(SynConst.String(text = text), expressionRange) when isSharedLiteral text ->
                        found.Add(text, expressionRange)
                    | SynExpr.InterpolatedString _ -> ()
                    | _ -> walkUnionFields expression
                | :? System.Collections.IEnumerable as values ->
                    for item in values do
                        walk item
                | _ ->
                    let valueType = value.GetType()

                    if Microsoft.FSharp.Reflection.FSharpType.IsUnion valueType then
                        walkUnionFields value
                    elif Microsoft.FSharp.Reflection.FSharpType.IsRecord valueType then
                        for field in Microsoft.FSharp.Reflection.FSharpValue.GetRecordFields value do
                            walk field
                    elif Microsoft.FSharp.Reflection.FSharpType.IsTuple valueType then
                        for field in Microsoft.FSharp.Reflection.FSharpValue.GetTupleFields value do
                            walk field

        and walkUnionFields (value: obj) =
            let _, fields =
                Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, value.GetType())

            for field in fields do
                walk field

        walk tree
        found |> Seq.distinct |> Seq.toList

    let private literalKey (text, literalRange: range) =
        text,
        literalRange.FileName,
        literalRange.StartLine,
        literalRange.StartColumn,
        literalRange.EndLine,
        literalRange.EndColumn

    let private assertTypedLiteralParity caseName source =
        let fileName = $"/tmp/LiteralParity-{caseName}.fsx"
        let options = getScriptOptions checker fileName source |> Async.RunSynchronously

        let parseResults, checkAnswer =
            checker.ParseAndCheckFileInProject(fileName, 0, FSharp.Compiler.Text.SourceText.ofString source, options)
            |> Async.RunSynchronously

        match checkAnswer with
        | FSharpCheckFileAnswer.Aborted -> failwith $"Type checking aborted for {caseName}"
        | FSharpCheckFileAnswer.Succeeded _ -> ()

        let expected =
            referenceSharedLiterals parseResults.ParseTree
            |> List.map literalKey
            |> Set.ofList

        let actual =
            collectSharedLiterals parseResults.ParseTree
            |> List.map literalKey
            |> Set.ofList

        test <@ actual = expected @>

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
    let ``a prose literal in a binding attribute is attributed to that binding`` () =
        let result =
            analyze
                "/tmp/LiteralAttribute.fsx"
                "Producer"
                """module Producer

open System

type MarkerAttribute(message: string) =
    inherit Attribute()

[<Marker("the audit log write failed and dropped the entry")>]
let emit () = ()
"""

        let edge = literalEdges result |> List.exactlyOne
        test <@ edge.FromSymbol.StartsWith("TestPrune.__Literal__.", StringComparison.Ordinal) @>
        test <@ edge.ToSymbol = "Producer.emit" @>

    [<Fact>]
    let ``ordinary prose inside an interpolated fill is excluded with the whole interpolation`` () =
        let result =
            analyze
                "/tmp/LiteralInterpolationFill.fsx"
                "Producer"
                ("module Producer\n\n"
                 + "let emit condition =\n"
                 + "    $\"\"\"result: {if condition then \"the audit log write failed and dropped the entry\" else \"the audit log write succeeded and retained the entry\"}\"\"\"\n")

        test <@ literalEdges result |> List.isEmpty @>

    [<Fact>]
    let ``typed literal traversal matches the 8_1_4 reflection baseline across pruned syntax branches`` () =
        let prelude =
            """module Corpus

open System

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type MarkerAttribute(message: string) =
    inherit Attribute()
    member val Detail = "" with get, set
"""

        let bindingSource =
            prelude
            + "\nlet emit condition =\n"
            + "    let ordinary = \"the ordinary binding message has enough words\"\n"
            + "    let verbatim = @\"the verbatim binding message has enough words\"\n"
            + "    let triple = \"\"\"the triple binding message has enough words\"\"\"\n"
            + "    let skipped = $\"the interpolated binding message has {condition} words\"\n"
            + "    ordinary, verbatim, triple, skipped\n"

        let attributeSource =
            prelude
            + "\n[<Marker(\"the binding attribute has enough prose words\", Detail = \"the named argument has enough prose words\")>]\n"
            + "let attributed () = ()\n\n"
            + "[<Marker(\"the record field attribute has enough prose words\")>]\n"
            + "type Record =\n"
            + "    { [<Marker(\"the nested record field has enough prose words\")>]\n"
            + "      Value: string }\n"

        let nestedModuleSource =
            prelude
            + "\n[<Marker(\"the nested module attribute has enough prose words\")>]\n"
            + "module Nested =\n    let value = 1\n"

        let exceptionSource =
            prelude
            + "\n[<Marker(\"the exception attribute has enough prose words\")>]\n"
            + "exception CorpusFailure of string with\n"
            + "    member _.Explanation = \"the exception member has enough prose words\"\n"
            + "    member _.AttributedReturn() : [<Marker(\"the exception return attribute has enough prose words\")>] string = \"the attributed return body has enough prose words\"\n"
            + "    member _.Deferred = lazy \"the deferred exception value has enough prose words\"\n"
            + "    member _.Lambda = fun () -> \"the exception lambda has enough prose words\"\n"
            + "    member _.Matcher = function | _ -> \"the exception match lambda has enough prose words\"\n"
            + "    member _.Interpolated(value) = $\"the excluded exception interpolation has {value} prose words\"\n"
            + "    member _.Sequence = seq { \"the implicit sequence statement has enough prose words\"; yield \"the explicit sequence yield has enough prose words\" }\n"
            + "    member _.SetIndexed() = let values = [| \"the initial indexed value has enough prose words\" |] in values.[0] <- \"the updated indexed value has enough prose words\"\n"
            + "    member _.Property\n"
            + "        with get () = \"the exception getter has enough prose words\"\n"
            + "        and set value = ignore value\n"

        let exceptionCastsSource =
            prelude
            + "\nexception CastFailure of string with\n"
            + "    member _.Upcast = (\"the upcast expression has enough prose words\" :> obj)\n"
            + "    member _.Downcast = (box \"the downcast expression has enough prose words\" :?> string)\n"
            + "    member _.TypeTest = (box \"the type test expression has enough prose words\" :? string)\n"

        let exceptionInterfaceSource =
            prelude
            + "\nexception InterfaceFailure of string with\n"
            + "    interface IDisposable with\n"
            + "        [<Marker(\"the nested interface member attribute has enough prose words\")>]\n"
            + "        member _.Dispose() = ()\n"

        let exceptionNestedTypeSource =
            prelude
            + "\ntype Base(message: string) =\n    member _.Message = message\n\n"
            + "exception NestedTypeFailure of string with\n"
            + "    type Nested =\n"
            + "        inherit Base(\"the implicit inheritance argument has enough prose words\")\n"
            + "        let stored = \"the nested let binding has enough prose words\"\n"
            + "        [<DefaultValue; Marker(\"the nested value field attribute has enough prose words\")>]\n"
            + "        val mutable Field: string\n"
            + "        [<Marker(\"the nested auto property attribute has enough prose words\")>]\n"
            + "        member val Auto = \"the nested auto property has enough prose words\" with get, set\n"
            + "        [<Marker(\"the nested type member attribute has enough prose words\")>]\n"
            + "        member _.Value = stored\n"
            + "        member _.AttributedReturn() : [<Marker(\"the nested return attribute has enough prose words\")>] string = stored\n"
            + "        member _.Property\n"
            + "            with get () = \"the nested getter has enough prose words\"\n"
            + "            and set value = ignore value\n"

        let exceptionControlFlowSource =
            prelude
            + "\ntype MessageRecord = { Value: string }\n\n"
            + "exception ControlFlowFailure of string with\n"
            + "    member _.Run(condition, items: int list) =\n"
            + "        let quoted = <@ \"the quoted expression has enough prose words\" @>\n"
            + "        let chosen = if condition then \"the true branch has enough prose words\" else \"the false branch has enough prose words\"\n"
            + "        let matched = match items with | [] -> \"the empty match has enough prose words\" | _ -> \"the full match has enough prose words\"\n"
            + "        let collected = [ for item in items do if item > 0 then yield \"the yielded item has enough prose words\" ]\n"
            + "        let array = [| \"the array item has enough prose words\"; \"the second array item has enough words\" |]\n"
            + "        let slice = array.[0..1]\n"
            + "        let record = { Value = \"the record field has enough prose words\" }\n"
            + "        let anonymous = {| Value = \"the anonymous field has enough prose words\" |}\n"
            + "        let disposable = { new IDisposable with member _.Dispose() = ignore \"the object expression has enough prose words\" }\n"
            + "        let extraInterface = { new obj() with interface IDisposable with member _.Dispose() = ignore \"the extra interface body has enough prose words\" }\n"
            + "        let mutable assigned = \"the initial assignment has enough prose words\"\n"
            + "        assigned <- \"the updated assignment has enough prose words\"\n"
            + "        for _ in items do ignore \"the foreach body has enough prose words\"\n"
            + "        for _ = 0 to 0 do ignore \"the numeric for body has enough prose words\"\n"
            + "        while false do ignore \"the while body has enough prose words\"\n"
            + "        try ignore \"the protected body has enough prose words\" with _ -> ignore \"the handler body has enough prose words\"\n"
            + "        try ignore \"the finalizable body has enough prose words\" finally ignore \"the finally body has enough prose words\"\n"
            + "        quoted, chosen, matched, collected, slice, record, anonymous, disposable, extraInterface, assigned\n"

        [ "binding-and-interpolation", bindingSource
          "attributes", attributeSource
          "nested-module", nestedModuleSource
          "exception", exceptionSource
          "exception-casts", exceptionCastsSource
          "exception-interface", exceptionInterfaceSource
          "exception-nested-type", exceptionNestedTypeSource
          "exception-control-flow", exceptionControlFlowSource ]
        |> List.iter (fun (caseName, source) -> assertTypedLiteralParity caseName source)

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
