module TestPrune.Tests.FcsTests

open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

module ``FSharp Compiler Service on NET 10`` =

    let sampleSource =
        """
module SampleModule

type Shape =
    | Circle of radius: float
    | Rectangle of width: float * height: float

let area (shape: Shape) =
    match shape with
    | Circle r -> System.Math.PI * r * r
    | Rectangle(w, h) -> w * h

let describeShape (shape: Shape) =
    match shape with
    | Circle _ -> "circle"
    | Rectangle _ -> "rectangle"
"""

    let checker = FSharpChecker.Create()

    let getProjectOptions () =
        let sourceFileName = "/tmp/TestPruneSpike.fs"
        let sourceText = SourceText.ofString sampleSource

        // Get default script options, then we'll use them
        let projOptions, _diagnostics =
            checker.GetProjectOptionsFromScript(sourceFileName, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously

        sourceFileName, sourceText, projOptions

    [<Fact>]
    let ``can parse F# source`` () =
        let sourceFileName, sourceText, projOptions = getProjectOptions ()

        let parseResults =
            checker.ParseFile(
                sourceFileName,
                sourceText,
                projOptions |> checker.GetParsingOptionsFromProjectOptions |> fst
            )
            |> Async.RunSynchronously

        test <@ not parseResults.ParseHadErrors @>

    [<Fact>]
    let ``can type-check and extract symbol declarations`` () =
        let sourceFileName, sourceText, projOptions = getProjectOptions ()

        let parseResults, checkAnswer =
            checker.ParseAndCheckFileInProject(sourceFileName, 0, sourceText, projOptions)
            |> Async.RunSynchronously

        test <@ not parseResults.ParseHadErrors @>

        let checkResults =
            match checkAnswer with
            | FSharpCheckFileAnswer.Succeeded results -> results
            | FSharpCheckFileAnswer.Aborted -> failwith "Type checking was aborted"

        // Extract all symbol declarations from the typed AST
        let symbolNames =
            checkResults.GetAllUsesOfAllSymbolsInFile()
            |> Seq.filter (fun symbolUse -> symbolUse.IsFromDefinition)
            |> Seq.map (fun symbolUse -> symbolUse.Symbol.DisplayName)
            |> Seq.toList

        // Verify we found the key declarations
        test <@ symbolNames |> List.contains "SampleModule" @>
        test <@ symbolNames |> List.contains "Shape" @>
        test <@ symbolNames |> List.contains "Circle" @>
        test <@ symbolNames |> List.contains "Rectangle" @>
        test <@ symbolNames |> List.contains "area" @>
        test <@ symbolNames |> List.contains "describeShape" @>

    [<Fact>]
    let ``can extract symbol kinds`` () =
        let sourceFileName, sourceText, projOptions = getProjectOptions ()

        let _parseResults, checkAnswer =
            checker.ParseAndCheckFileInProject(sourceFileName, 0, sourceText, projOptions)
            |> Async.RunSynchronously

        let checkResults =
            match checkAnswer with
            | FSharpCheckFileAnswer.Succeeded results -> results
            | FSharpCheckFileAnswer.Aborted -> failwith "Type checking was aborted"

        let symbolDefs =
            checkResults.GetAllUsesOfAllSymbolsInFile()
            |> Seq.filter (fun symbolUse -> symbolUse.IsFromDefinition)
            |> Seq.map (fun symbolUse -> symbolUse.Symbol.DisplayName, symbolUse.Symbol.GetType().Name)
            |> Seq.toList

        // Shape should be an FSharpEntity (union type)
        let shapeSymbol = symbolDefs |> List.tryFind (fun (name, _) -> name = "Shape")

        test <@ shapeSymbol.IsSome @>
        test <@ snd shapeSymbol.Value = "FSharpEntity" @>

        // area should be an FSharpMemberOrFunctionOrValue
        let areaSymbol = symbolDefs |> List.tryFind (fun (name, _) -> name = "area")

        test <@ areaSymbol.IsSome @>
        test <@ snd areaSymbol.Value = "FSharpMemberOrFunctionOrValue" @>
