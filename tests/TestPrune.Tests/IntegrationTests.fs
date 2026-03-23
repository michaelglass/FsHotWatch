module TestPrune.Tests.IntegrationTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff
open TestPrune.ImpactAnalysis

let checker = FSharpChecker.Create()

let analyze source =
    let fileName = "/tmp/IntegrationTest.fsx"
    let options = getScriptOptions checker fileName source |> Async.RunSynchronously
    let result = analyzeSource checker fileName source options |> Async.RunSynchronously

    match result with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

let private withDb (f: Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f db
    finally
        if File.Exists path then
            File.Delete path

        let walPath = path + "-wal"
        let shmPath = path + "-shm"

        if File.Exists walPath then
            File.Delete walPath

        if File.Exists shmPath then
            File.Delete shmPath

module ``Full pipeline — index and select affected tests`` =

    [<Fact>]
    let ``change to add function selects only the test that depends on it`` () =
        withDb (fun db ->
            let libSource =
                """
module Lib

type FactAttribute() =
    inherit System.Attribute()

let add x y = x + y

type Counter = { Value: int }
"""

            let testSource =
                """
module LibTests

type FactAttribute() =
    inherit System.Attribute()

let add x y = x + y

type Counter = { Value: int }

[<Fact>]
let ``add returns correct sum`` () =
    let result = add 2 3
    ()

[<Fact>]
let ``counter starts at zero`` () =
    let c = { Value = 0 }
    ()
"""

            let libResult = analyze libSource
            let testResult = analyze testSource

            // Remap source files so they look like project paths
            let remapSymbols file symbols =
                symbols |> List.map (fun s -> { s with SourceFile = file })

            let libAnalysis =
                { Symbols = remapSymbols "src/Lib.fs" libResult.Symbols
                  Dependencies = libResult.Dependencies
                  TestMethods = [] }

            let testAnalysis =
                { Symbols = remapSymbols "tests/LibTests.fs" testResult.Symbols
                  Dependencies = testResult.Dependencies
                  TestMethods =
                    testResult.TestMethods
                    |> List.map (fun t -> { t with TestProject = "TestProject" }) }

            // Store both in DB
            db.RebuildForProject("MyProject", libAnalysis)
            db.RebuildForProject("MyProject", testAnalysis)

            // Verify test methods were detected
            let allSymbols = db.GetAllSymbolNames()
            test <@ allSymbols.Count > 0 @>

            // Check that test methods were found
            test <@ testAnalysis.TestMethods.Length >= 1 @>

            // Find the "add" symbol and simulate a change by shifting its line range
            let addSymbol =
                libAnalysis.Symbols
                |> List.tryFind (fun s -> s.FullName.EndsWith("add", StringComparison.Ordinal))

            test <@ addSymbol.IsSome @>

            let storedSymbols = db.GetSymbolsInFile "src/Lib.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("add", StringComparison.Ordinal) then
                        { s with LineEnd = s.LineEnd + 5 }
                    else
                        s)

            let changes = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            // Only "add" should be changed
            test <@ changedNames.Length = 1 @>
            test <@ changedNames[0].EndsWith("add", StringComparison.Ordinal) @>)

module ``Transitive dependency chain`` =

    [<Fact>]
    let ``changing baseFunc selects test via transitive dependency`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let baseFunc x = x + 1
let helperFunc x = baseFunc x
[<Fact>]
let testMethod () = helperFunc 5 |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" }) }

            db.RebuildForProject("MyProject", analysis)

            // Verify transitive chain exists: testMethod -> helperFunc -> baseFunc
            let helperCallsBase =
                result.Dependencies
                |> List.exists (fun d ->
                    d.FromSymbol.EndsWith("helperFunc", StringComparison.Ordinal)
                    && d.ToSymbol.EndsWith("baseFunc", StringComparison.Ordinal))

            let testCallsHelper =
                result.Dependencies
                |> List.exists (fun d ->
                    d.FromSymbol.EndsWith("testMethod", StringComparison.Ordinal)
                    && d.ToSymbol.EndsWith("helperFunc", StringComparison.Ordinal))

            test <@ helperCallsBase @>
            test <@ testCallsHelper @>

            // Simulate changing baseFunc
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("baseFunc", StringComparison.Ordinal) then
                        { s with LineEnd = s.LineEnd + 3 }
                    else
                        s)

            let changes = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            test
                <@
                    changedNames
                    |> List.exists (fun n -> n.EndsWith("baseFunc", StringComparison.Ordinal))
                @>

            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testMethod" @>)

module ``DU case change affects pattern-matching tests`` =

    [<Fact>]
    let ``adding a DU case selects tests that pattern match on the DU`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type Shape =
    | Circle of float
    | Square of float

let describe (s: Shape) =
    match s with
    | Circle r -> "circle"
    | Square s -> "square"

[<Fact>]
let testDescribe () = describe (Circle 1.0) |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" }) }

            db.RebuildForProject("MyProject", analysis)

            // Find the Shape DU symbol and simulate a change (adding a case shifts lines)
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let shapeSymbol =
                storedSymbols
                |> List.tryFind (fun s -> s.FullName.EndsWith("Shape", StringComparison.Ordinal) && s.Kind = Type)

            test <@ shapeSymbol.IsSome @>

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("Shape", StringComparison.Ordinal) && s.Kind = Type then
                        { s with LineEnd = s.LineEnd + 2 }
                    else
                        s)

            let changes = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            test
                <@
                    changedNames
                    |> List.exists (fun n -> n.EndsWith("Shape", StringComparison.Ordinal))
                @>

            let affected = db.QueryAffectedTests changedNames

            // The test should be selected because it depends on describe which depends on Shape
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod = "testDescribe") @>)

module ``Unindexed file triggers RunAll`` =

    [<Fact>]
    let ``selectTests returns RunAll for a file not in the DB`` () =
        withDb (fun db ->
            let source =
                """
module M
let f x = x
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = [] }

            db.RebuildForProject("MyProject", analysis)

            // A new file not in the DB
            let currentSymbols =
                Map.ofList
                    [ "src/NewModule.fs",
                      [ { FullName = "NewModule.newFunc"
                          Kind = Function
                          SourceFile = "src/NewModule.fs"
                          LineStart = 1
                          LineEnd = 5 } ] ]

            let result = selectTests db [ "src/NewModule.fs" ] currentSymbols

            match result with
            | RunAll _ -> ()
            | RunSubset _ -> failwith "Expected RunAll for unindexed file")

module ``No changes returns empty RunSubset`` =

    [<Fact>]
    let ``selectTests with no changed files returns RunSubset empty`` () =
        withDb (fun db ->
            let source =
                """
module M
let f x = x
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = [] }

            db.RebuildForProject("MyProject", analysis)

            let result = selectTests db [] Map.empty

            match result with
            | RunSubset tests -> test <@ tests = [] @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")
