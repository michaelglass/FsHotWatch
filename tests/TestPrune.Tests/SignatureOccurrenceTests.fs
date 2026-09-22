module TestPrune.Tests.SignatureOccurrenceTests

// A signature (`.fsi`) declaration describes the implementation's symbol; it is not
// code of its own. The analysis must record it as a second source OCCURRENCE of the
// canonical symbol, and the stores must keep each file's occurrence, edges and
// hashes separately so re-indexing either file cannot erase the other's.

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.InMemoryStore
open TestPrune.Ports

let private signatureSource =
    "module Library\ntype Token =\n    { Value: int }\nval calculate:\n    Token -> int\n"

let private implementationSource =
    "module Library\ntype Token = { Value: int }\nlet calculate (token: Token) = token.Value + 1\n"

let private plainSource = "module Plain\nlet helper x = x * 2\n"

let private consumerSource =
    "module Consumer\nlet useIt () = Library.calculate { Value = 1 } + Plain.helper 1\n"

/// Analyze the four-file fixture through the real compiler and return each file's
/// result keyed by file name, with symbol paths relative to the fixture directory.
let private analyzeFixture () =
    let directory =
        Path.Combine(Path.GetTempPath(), $"signature-occurrence-{Guid.NewGuid():N}")

    Directory.CreateDirectory directory |> ignore

    let sources =
        [ "Library.fsi", signatureSource
          "Library.fs", implementationSource
          "Plain.fs", plainSource
          "Consumer.fs", consumerSource ]
        |> List.map (fun (name, source) -> Path.Combine(directory, name), source)

    try
        for path, source in sources do
            File.WriteAllText(path, source)

        let checker = FSharpChecker.Create()
        let consumerPath, consumer = List.last sources

        let scriptOptions =
            getScriptOptions checker consumerPath consumer |> Async.RunSynchronously

        let options =
            { scriptOptions with
                SourceFiles = sources |> List.map fst |> List.toArray }

        sources
        |> List.map (fun (path, source) ->
            match analyzeSource checker path source options "Fixture" |> Async.RunSynchronously with
            | Ok result ->
                Path.GetFileName path,
                { result with
                    Symbols = normalizeSymbolPaths directory result.Symbols }
            | Error message -> failwith message)
        |> Map.ofList
    finally
        Directory.Delete(directory, true)

let private realSymbols (result: AnalysisResult) =
    result.Symbols |> List.filter (fun symbol -> not symbol.IsExtern)

let private graphNames (result: AnalysisResult) =
    (result.Symbols |> List.map _.FullName)
    @ (result.Dependencies
       |> List.collect (fun edge -> [ edge.FromSymbol; edge.ToSymbol ]))
    @ (result.ParentLinks |> List.collect (fun link -> [ link.Child; link.Parent ]))

[<Collection("FCS-AstAnalyzer")>]
module ``Analysis seam`` =

    [<Fact>]
    let ``a signature declaration is an occurrence of the implementation's symbol`` () =
        let results = analyzeFixture ()
        let signature = results["Library.fsi"]
        let implementation = results["Library.fs"]

        let occurrence (result: AnalysisResult) name =
            realSymbols result |> List.find (fun symbol -> symbol.FullName = name)

        // Both files declare the SAME canonical names; each keeps its own location and hash.
        for name in [ "Library"; "Library.Token"; "Library.calculate" ] do
            test <@ (occurrence signature name).SourceFile = "Library.fsi" @>
            test <@ (occurrence implementation name).SourceFile = "Library.fs" @>

        let signatureCalculate = occurrence signature "Library.calculate"
        let implementationCalculate = occurrence implementation "Library.calculate"
        test <@ signatureCalculate.LineStart = 4 @>
        test <@ signatureCalculate.ContentHash <> implementationCalculate.ContentHash @>

        // No synthetic namespace and no bridge edge between two names for one symbol.
        for result in [ signature; implementation ] do
            test
                <@
                    graphNames result
                    |> List.forall (fun name -> not (name.Contains "__Signature__"))
                @>

            test
                <@
                    result.Dependencies
                    |> List.forall (fun edge -> edge.FromSymbol <> edge.ToSymbol)
                @>

        // The signature's own dependency: the type named in the `val` declaration.
        test
            <@
                signature.Dependencies
                |> List.exists (fun edge -> edge.FromSymbol = "Library.calculate" && edge.ToSymbol = "Library.Token")
            @>

    [<Fact>]
    let ``files without a signature keep their ordinary graph`` () =
        let results = analyzeFixture ()
        let plain = results["Plain.fs"]
        let consumer = results["Consumer.fs"]

        let shape (result: AnalysisResult) =
            realSymbols result
            |> List.map (fun symbol -> symbol.FullName, symbol.Kind, symbol.SourceFile, symbol.LineStart)
            |> List.sort

        test <@ shape plain = [ "Plain", Module, "Plain.fs", 1; "Plain.helper", Function, "Plain.fs", 2 ] @>

        test
            <@
                plain.Dependencies |> List.map (fun edge -> edge.FromSymbol, edge.ToSymbol) = [ "Plain.helper",
                                                                                                "Microsoft.FSharp.Core.Operators.(*)" ]
            @>

        let consumerTargets =
            consumer.Dependencies
            |> List.filter (fun edge -> edge.FromSymbol = "Consumer.useIt")
            |> List.map _.ToSymbol
            |> Set.ofList

        test <@ Set.isSubset (set [ "Library.calculate"; "Library.Token"; "Plain.helper" ]) consumerTargets @>

[<Collection("FCS-AstAnalyzer")>]
module ``Stored occurrences`` =

    let private fixtureStores () =
        let results = analyzeFixture ()

        let ordered =
            [ "Library.fsi"; "Library.fs"; "Plain.fs"; "Consumer.fs" ]
            |> List.map (fun f -> results[f])

        results, ordered

    [<Fact>]
    let ``each file keeps its own occurrence and edges when the other file is re-indexed`` () =
        let results, ordered = fixtureStores ()

        TestPrune.Tests.TestHelpers.withDb (fun db ->
            for reindex in
                [ ordered
                  [ results["Library.fs"] ]
                  [ results["Library.fsi"] ]
                  List.rev ordered ] do
                db.RebuildProjects reindex

                let persisted = toSymbolStore db
                let memory = fromAnalysisResults ordered

                for store in [ persisted; memory ] do
                    let occurrences file =
                        store.GetSymbolsInFile file
                        |> List.filter (fun symbol -> symbol.FullName = "Library.calculate")

                    test <@ (occurrences "Library.fsi" |> List.map _.LineStart) = [ 4 ] @>
                    test <@ (occurrences "Library.fs" |> List.map _.LineStart) = [ 3 ] @>

                    // The `val` signature's edge to `Token` belongs to the signature file.
                    test
                        <@
                            store.GetDependenciesFromFile "Library.fsi"
                            |> List.exists (fun edge ->
                                edge.FromSymbol = "Library.calculate" && edge.ToSymbol = "Library.Token")
                        @>

                    test
                        <@
                            store.GetDependenciesFromFile "Library.fs"
                            |> List.exists (fun edge ->
                                edge.FromSymbol = "Library.calculate"
                                && edge.ToSymbol = "Microsoft.FSharp.Core.Operators.(+)")
                        @>

                    // One node per name, whatever the number of files declaring it.
                    let names = store.GetAllSymbolNames()
                    test <@ names.Contains "Library.calculate" @>
                    test <@ names |> Set.forall (fun name -> not (name.Contains "__Signature__")) @>

                // Both stores agree on every file's contributed edges.
                for file in [ "Library.fsi"; "Library.fs"; "Plain.fs"; "Consumer.fs" ] do
                    let fromDb = persisted.GetDependenciesFromFile file |> List.distinct |> List.sort
                    let fromMemory = memory.GetDependenciesFromFile file |> List.distinct |> List.sort
                    test <@ fromMemory = fromDb @>)
