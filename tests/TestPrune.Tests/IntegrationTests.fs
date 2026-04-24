module TestPrune.Tests.IntegrationTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode
open TestPrune.Ports
open TestPrune.SymbolDiff
open TestPrune.Domain
open TestPrune.ImpactAnalysis
open TestPrune.Tests.TestHelpers

let private runDeadCodeResult (db: Database) (patterns: string list) (includeTests: bool) =
    runDeadCode db patterns includeTests |> fst

let checker = FSharpChecker.Create()

let analyze source =
    let fileName = "/tmp/IntegrationTest.fsx"
    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    let result =
        analyzeSource checker fileName source options "TestProject"
        |> Async.RunSynchronously

    match result with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

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
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            let testAnalysis =
                { Symbols = remapSymbols "tests/LibTests.fs" testResult.Symbols
                  Dependencies = testResult.Dependencies
                  TestMethods =
                    testResult.TestMethods
                    |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            // Store both in DB
            db.RebuildProjects([ libAnalysis; testAnalysis ])

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
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
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
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

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
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
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
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

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
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
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
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // A new file not in the DB
            let currentSymbols =
                Map.ofList
                    [ "src/NewModule.fs",
                      [ { FullName = "NewModule.newFunc"
                          Kind = Function
                          SourceFile = "src/NewModule.fs"
                          LineStart = 1
                          LineEnd = 5
                          ContentHash = ""
                          IsExtern = false } ] ]

            let result, _events =
                selectTests (toSymbolStore db) [ "src/NewModule.fs" ] currentSymbols

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
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let result, _events = selectTests (toSymbolStore db) [] Map.empty

            match result with
            | RunSubset tests -> test <@ tests |> List.isEmpty @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}")

module ``Dead code — full pipeline`` =

    [<Fact>]
    let ``unreachable function detected from real FCS analysis`` () =
        withDb (fun db ->
            let source =
                """
module M

let usedFunc x = x + 1
let main () = usedFunc 5 |> ignore
let deadFunc x = x * 2
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.main" ] false

            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("deadFunc", StringComparison.Ordinal))
                @>

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("usedFunc", StringComparison.Ordinal))
                    |> not
                @>

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("main", StringComparison.Ordinal))
                    |> not
                @>)

    [<Fact>]
    let ``transitive reachability keeps deep dependencies alive`` () =
        withDb (fun db ->
            let source =
                """
module M

let baseHelper x = x + 1
let midHelper x = baseHelper x
let topFunc () = midHelper 5 |> ignore
let orphan x = x - 1
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.topFunc" ] false

            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            // baseHelper and midHelper are transitively reachable from topFunc
            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("baseHelper", StringComparison.Ordinal))
                    |> not
                @>

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("midHelper", StringComparison.Ordinal))
                    |> not
                @>
            // orphan is not reachable
            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("orphan", StringComparison.Ordinal))
                @>)

    [<Fact>]
    let ``DU cases are excluded from dead code report`` () =
        withDb (fun db ->
            let source =
                """
module M

type Shape =
    | Circle of float
    | Square of float

let area (s: Shape) =
    match s with
    | Circle r -> System.Math.PI * r * r
    | Square s -> s * s

let main () = area (Circle 1.0) |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.main" ] false

            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            // DU cases should not appear in dead code
            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("Circle", StringComparison.Ordinal))
                    |> not
                @>

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("Square", StringComparison.Ordinal))
                    |> not
                @>)

module ``SymbolDiff — real source change detection`` =

    [<Fact>]
    let ``function body change detected`` () =
        let v1 =
            analyze
                """
module M
let compute x = x + 1
"""

        let v2 =
            analyze
                """
module M
let compute x = x * 2 + 1
"""

        let v1Symbols = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let v2Symbols = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges v2Symbols v1Symbols
        let names = changedSymbolNames changes
        test <@ names |> List.exists (fun n -> n.EndsWith("compute", StringComparison.Ordinal)) @>

    [<Fact>]
    let ``record field added detected`` () =
        let v1 =
            analyze
                """
module M
type Config = { Host: string }
"""

        let v2 =
            analyze
                """
module M
type Config = { Host: string; Port: int }
"""

        let v1Symbols = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let v2Symbols = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges v2Symbols v1Symbols
        let names = changedSymbolNames changes
        test <@ names |> List.exists (fun n -> n.EndsWith("Config", StringComparison.Ordinal)) @>

    [<Fact>]
    let ``DU case added detected`` () =
        let v1 =
            analyze
                """
module M
type Shape =
    | Circle of float
    | Square of float
"""

        let v2 =
            analyze
                """
module M
type Shape =
    | Circle of float
    | Square of float
    | Triangle of float * float
"""

        let v1Symbols = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let v2Symbols = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges v2Symbols v1Symbols
        let names = changedSymbolNames changes
        // Shape type itself should be Modified (line range changed)
        test <@ names |> List.exists (fun n -> n.EndsWith("Shape", StringComparison.Ordinal)) @>
        // Triangle should be Added
        test
            <@
                changes
                |> List.exists (fun c ->
                    match c with
                    | SymbolChange.Added n -> n.EndsWith("Triangle", StringComparison.Ordinal)
                    | _ -> false)
            @>

    [<Fact>]
    let ``DU case removed detected`` () =
        let v1 =
            analyze
                """
module M
type Msg =
    | Increment
    | Decrement
    | Reset
"""

        let v2 =
            analyze
                """
module M
type Msg =
    | Increment
    | Decrement
"""

        let v1Symbols = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let v2Symbols = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges v2Symbols v1Symbols
        // Reset should be Removed
        test
            <@
                changes
                |> List.exists (fun c ->
                    match c with
                    | SymbolChange.Removed n -> n.EndsWith("Reset", StringComparison.Ordinal)
                    | _ -> false)
            @>

    [<Fact>]
    let ``attribute added to function detected`` () =
        let v1 =
            analyze
                """
module M
open System
let oldFunc x = x + 1
"""

        let v2 =
            analyze
                """
module M
open System
[<Obsolete>]
let oldFunc x = x + 1
"""

        let v1Symbols = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let v2Symbols = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges v2Symbols v1Symbols
        let names = changedSymbolNames changes
        test <@ names |> List.exists (fun n -> n.EndsWith("oldFunc", StringComparison.Ordinal)) @>

    [<Fact>]
    let ``identical source produces no changes`` () =
        let source =
            """
module M
let f x = x + 1
let g y = y * 2
"""

        let r1 = analyze source
        let r2 = analyze source
        let s1 = r1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = r2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``adding line comment to function body produces no changes`` () =
        let v1 =
            analyze
                """
module M
let compute x =
    x + 1
"""

        let v2 =
            analyze
                """
module M
let compute x =
    // increment by one
    x + 1
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``adding block comment to function body produces no changes`` () =
        let v1 =
            analyze
                """
module M
let compute x =
    x + 1
"""

        let v2 =
            analyze
                """
module M
let compute x =
    (* this is a block comment *)
    x + 1
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``adding line comment to type definition produces no changes`` () =
        let v1 =
            analyze
                """
module M
type Shape =
    | Circle of float
    | Square of float
"""

        let v2 =
            analyze
                """
module M
type Shape =
    // circle variant
    | Circle of float
    // square variant
    | Square of float
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``double-slash inside string literal is not stripped`` () =
        // Changing a non-comment part of the body should still detect as a change
        let v1 =
            analyze
                """
module M
let getUrl () = "http://example.com/v1"
"""

        let v2 =
            analyze
                """
module M
let getUrl () = "http://example.com/v2"
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        let names = changedSymbolNames changes
        test <@ names |> List.exists (fun n -> n.EndsWith("getUrl", StringComparison.Ordinal)) @>

    [<Fact>]
    let ``escape sequence in string literal does not affect stripping`` () =
        let v1 =
            analyze
                """
module M
let msg () = "hello\nworld"
"""

        let v2 =
            analyze
                """
module M
let msg () = "hello\nworld"
// a comment
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``nested block comments produce no changes`` () =
        let v1 =
            analyze
                """
module M
let compute x = x + 1
"""

        let v2 =
            analyze
                """
module M
let compute x =
    (* outer (* nested *) comment *)
    x + 1
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``verbatim string content is preserved and affects hash`` () =
        let v1 =
            analyze
                """
module M
let path () = @"C:\old\path"
"""

        let v2 =
            analyze
                """
module M
let path () = @"C:\new\path"
"""

        let s1 = v1.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let s2 = v2.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
        let changes, _ = detectChanges s2 s1
        let names = changedSymbolNames changes
        test <@ names |> List.exists (fun n -> n.EndsWith("path", StringComparison.Ordinal)) @>

module ``Impact analysis — change affects zero tests`` =

    [<Fact>]
    let ``changing isolated function selects no tests`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let isolated x = x + 1
let used x = x * 2

[<Fact>]
let myTest () = used 5 |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change `isolated` — no test depends on it
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("isolated", StringComparison.Ordinal) then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected |> List.isEmpty @>)

module ``Impact analysis — change affects two test classes`` =

    [<Fact>]
    let ``changing shared helper selects both tests`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let sharedHelper x = x + 1

let wrapperA x = sharedHelper x
let wrapperB x = sharedHelper (x * 2)

[<Fact>]
let testAlpha () = wrapperA 1 |> ignore

[<Fact>]
let testBeta () = wrapperB 2 |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change sharedHelper — both tests depend on it transitively
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("sharedHelper", StringComparison.Ordinal) then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            let testNames = affected |> List.map (fun t -> t.TestMethod) |> List.sort
            test <@ testNames = [ "testAlpha"; "testBeta" ] @>)

module ``Impact analysis — generic type parameter change affects test`` =

    [<Fact>]
    let ``changing type used as generic arg selects dependent test`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type Config = { Host: string; Port: int }

let loadConfigs () : Config list = []

[<Fact>]
let testLoad () = loadConfigs () |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change Config type — test uses loadConfigs which returns Config list
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("Config", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod = "testLoad") @>)

module ``Impact analysis — record field construction affects test`` =

    [<Fact>]
    let ``changing record type selects test that constructs it`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type Person = { Name: string; Age: int }

let makePerson () = { Name = "Alice"; Age = 30 }

[<Fact>]
let testMake () = makePerson () |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("Person", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod = "testMake") @>)

module ``Impact analysis — DU parent type change affects test via case usage`` =

    [<Fact>]
    let ``changing DU type selects test that pattern matches its cases`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type Color =
    | Red
    | Green
    | Blue

let describe c =
    match c with
    | Red -> "red"
    | Green -> "green"
    | Blue -> "blue"

[<Fact>]
let testDescribe () = describe Red |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change Color type — test depends on describe which pattern matches Color cases
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("Color", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod = "testDescribe") @>)

module ``Impact analysis — four hop transitive chain`` =

    [<Fact>]
    let ``change at depth 4 selects test through full chain`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let depth4 x = x + 1
let depth3 x = depth4 x
let depth2 x = depth3 x
let depth1 x = depth2 x

[<Fact>]
let testDeep () = depth1 5 |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change depth4 — 4 hops away from testDeep
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("depth4", StringComparison.Ordinal) then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testDeep" @>)

module ``Impact analysis — mixed edge types`` =

    [<Fact>]
    let ``test selected through function call AND type usage AND pattern match`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type Result =
    | Ok of int
    | Err of string

type Config = { Timeout: int }

let helper (c: Config) = c.Timeout
let process (c: Config) =
    match Ok (helper c) with
    | Ok v -> v
    | Err _ -> 0

[<Fact>]
let testProcess () =
    let c = { Timeout = 5 }
    process c |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Change Config — test depends through: testProcess -> process -> helper -> Config
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("Config", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod = "testProcess") @>)

module ``Dead code — generic type parameter keeps type alive`` =

    [<Fact>]
    let ``type used only as generic argument is not dead`` () =
        withDb (fun db ->
            let source =
                """
module M

type MyData = { Value: int }

let loadAll () : MyData list = []
let main () = loadAll () |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.main" ] false
            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            // MyData should NOT be dead — it's used as generic arg in list<MyData>
            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("MyData", StringComparison.Ordinal))
                    |> not
                @>)

module ``Dead code — record type kept alive by field construction`` =

    [<Fact>]
    let ``record type used only via field construction is not dead`` () =
        withDb (fun db ->
            let source =
                """
module M

type Settings = { Verbose: bool; MaxRetries: int }

let defaultSettings () = { Verbose = false; MaxRetries = 3 }
let main () = defaultSettings () |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.main" ] false
            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("Settings", StringComparison.Ordinal))
                    |> not
                @>)

module ``Dead code — DU type kept alive by case pattern match`` =

    [<Fact>]
    let ``DU type used only via case matching is not dead`` () =
        withDb (fun db ->
            let source =
                """
module M

type Direction =
    | North
    | South
    | East
    | West

let isVertical d =
    match d with
    | North | South -> true
    | East | West -> false

let main () = isVertical North |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = []
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let deadResult = runDeadCodeResult db [ "*.main" ] false
            let deadNames = deadResult.UnreachableSymbols |> List.map (fun s -> s.FullName)

            // Direction type should NOT be dead — it's the parent of matched cases
            test
                <@
                    deadNames
                    |> List.exists (fun n -> n.EndsWith("Direction", StringComparison.Ordinal))
                    |> not
                @>)

module ``Impact analysis — class-based (type member) tests`` =

    [<Fact>]
    let ``type member test methods produce dependency edges`` () =
        let source =
            """
module M

type FactAttribute() =
    inherit System.Attribute()

let helper x = x + 1

type MyTests() =
    [<Fact>]
    member _.``helper returns incremented value`` () =
        let result = helper 5
        ()
"""

        let result = analyze source

        // The test method should be detected
        test <@ result.TestMethods.Length >= 1 @>

        // The test method should have an edge to helper
        let testToHelper =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.Contains("helper returns incremented value")
                && d.ToSymbol.EndsWith("helper", StringComparison.Ordinal))

        test <@ testToHelper @>

    [<Fact>]
    let ``type member test selects via QueryAffectedTests`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let helper x = x + 1

type MyTests() =
    [<Fact>]
    member _.``helper works`` () =
        let result = helper 5
        ()
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = result.Attributes
                  ParentLinks = result.ParentLinks
                  Diagnostics = result.Diagnostics }

            db.RebuildProjects([ analysis ])

            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("helper", StringComparison.Ordinal) && s.Kind = Function then
                        { s with ContentHash = "modified" }
                    else
                        s)

            let changes, _ = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected.Length >= 1 @>
            test <@ affected |> List.exists (fun t -> t.TestMethod.Contains("helper")) @>)

    [<Fact>]
    let ``this self-identifier produces same edges as underscore`` () =
        let source =
            """
module M

type FactAttribute() =
    inherit System.Attribute()

let helper x = x + 1

type MyTests() =
    [<Fact>]
    member this.``helper via this`` () =
        let result = helper 5
        ()
"""

        let result = analyze source

        test <@ result.TestMethods.Length >= 1 @>

        let testToHelper =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.Contains("helper via this")
                && d.ToSymbol.EndsWith("helper", StringComparison.Ordinal))

        test <@ testToHelper @>

    [<Fact>]
    let ``cross-project type member test selects via transitive chain`` () =
        withDb (fun db ->
            // Project A: library function
            let libSource =
                """
module Lib

let compute x = x * 2
"""

            // Project B: class-based test calling the library function
            let testSource =
                """
module LibTests

type FactAttribute() =
    inherit System.Attribute()

let compute x = x * 2

type ComputeTests() =
    [<Fact>]
    member _.``compute doubles value`` () =
        let result = compute 5
        ()
"""

            let libResult = analyze libSource
            let testResult = analyze testSource

            let libAnalysis =
                { Symbols = libResult.Symbols |> List.map (fun s -> { s with SourceFile = "src/Lib.fs" })
                  Dependencies = libResult.Dependencies
                  TestMethods = []
                  Attributes = libResult.Attributes
                  ParentLinks = libResult.ParentLinks
                  Diagnostics = libResult.Diagnostics }

            let testAnalysis =
                { Symbols =
                    testResult.Symbols
                    |> List.map (fun s ->
                        { s with
                            SourceFile = "tests/LibTests.fs" })
                  Dependencies = testResult.Dependencies
                  TestMethods =
                    testResult.TestMethods
                    |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = testResult.Attributes
                  ParentLinks = testResult.ParentLinks
                  Diagnostics = testResult.Diagnostics }

            db.RebuildProjects([ libAnalysis; testAnalysis ])

            // Verify the type member test was stored
            let testMethods = db.QueryAffectedTests [ "LibTests.compute" ]

            // The class-based test should be found when its dependency changes
            test <@ testMethods |> List.exists (fun t -> t.TestMethod.Contains("compute")) @>)

module ``Cross-assembly dependency extraction`` =

    [<Fact>]
    let ``using a type from another assembly produces extern symbol and dependency edge`` () =
        let source =
            """
module M

open System.Text

let buildString () =
    let sb = StringBuilder()
    sb.Append("hello") |> ignore
    sb.ToString()
"""

        let result = analyze source

        // Should have a dependency edge from buildString to System.Text.StringBuilder
        let sbEdge =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("buildString", System.StringComparison.Ordinal)
                && d.ToSymbol.Contains("StringBuilder"))

        test <@ sbEdge.IsSome @>

        // Should have an extern symbol for StringBuilder in the symbols list
        let externSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.Contains("StringBuilder") && s.IsExtern)

        test <@ externSym.IsSome @>
        test <@ externSym.Value.Kind = ExternRef @>

    [<Fact>]
    let ``cross-assembly dependency enables test selection through extern symbols`` () =
        withDb (fun db ->
            let testSource =
                """
module Tests

type FactAttribute() =
    inherit System.Attribute()

open System.Text

[<Fact>]
let ``test uses string builder`` () =
    let sb = StringBuilder()
    sb.Append("hello") |> ignore
    ()
"""

            let result = analyze testSource

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "tests/Tests.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Changing StringBuilder should affect the test
            let affected = db.QueryAffectedTests [ "System.Text.StringBuilder" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod.Contains("string builder") @>)

module ``Fixture member invalidation`` =

    [<Fact>]
    let ``editing a fixture member the test doesn't directly call still selects the test`` () =
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type TestServerFixture() =
    member _.HttpClient = "client"
    member _.shutdown () = ()

type BrowserTests(fixture: TestServerFixture) =
    [<Fact>]
    member _.``uses client`` () =
        fixture.HttpClient |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = result.ParentLinks
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Sanity: parent links captured shutdown and HttpClient as members of TestServerFixture
            test
                <@
                    result.ParentLinks
                    |> List.exists (fun l ->
                        l.Child.EndsWith("TestServerFixture.shutdown", StringComparison.Ordinal)
                        && l.Parent.EndsWith("TestServerFixture", StringComparison.Ordinal))
                @>

            test <@ result.TestMethods |> List.exists (fun t -> t.TestMethod.Contains "uses client") @>

            let browserUsesFixture =
                result.Dependencies
                |> List.exists (fun d ->
                    d.FromSymbol.Contains "BrowserTests"
                    && d.ToSymbol.EndsWith("TestServerFixture", StringComparison.Ordinal))

            test <@ browserUsesFixture @>

            let testUsesHttpClient =
                result.Dependencies
                |> List.exists (fun d -> d.FromSymbol.Contains "uses client" && d.ToSymbol.Contains "HttpClient")

            test <@ testUsesHttpClient @>

            // Simulate editing the fixture: the `shutdown` member body changed, which also
            // changes the TestServerFixture type symbol's hash (its range covers all members).
            // The test body only accesses `HttpClient`, so without aggregate-type
            // invalidation the recursive walk finds no path from shutdown or from the
            // type itself back to the test method.
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("TestServerFixture.shutdown", StringComparison.Ordinal) then
                        { s with
                            ContentHash = "modified_member" }
                    elif
                        s.FullName.EndsWith("TestServerFixture", StringComparison.Ordinal)
                        && s.Kind = Type
                    then
                        { s with ContentHash = "modified_type" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            test
                <@
                    changedNames
                    |> List.exists (fun n -> n.EndsWith("TestServerFixture.shutdown", StringComparison.Ordinal))
                @>

            let affected = db.QueryAffectedTests changedNames

            test <@ affected |> List.exists (fun t -> t.TestMethod.Contains "uses client") @>)

    [<Fact>]
    let ``test that receives fixture but never accesses it is still selected`` () =
        // Layer 1 gap: with aggregate-type invalidation, editing a fixture member
        // selects tests that accessed ANY member of the fixture. But a test method
        // whose body never references the fixture at all has no direct edge into
        // the fixture's member set — only its enclosing class does (via ctor-param).
        // Layer 2a.1 closes this by emitting a direct testMethod → fixtureType edge
        // for every ctor-param type of the test's declaring class.
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

type TestFix() =
    member _.start () = ()

type Tests(_fixture: TestFix) =
    [<Fact>]
    member _.``standalone test`` () =
        let x = 1 + 1
        ()
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = result.ParentLinks
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // Simulate editing the fixture's `start` member. The test body touches
            // nothing on TestFix, so without a direct edge the aggregate expansion
            // lifts to TestFix → {start, .ctor} but the reverse walk can't reach
            // `standalone test`.
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("TestFix.start", StringComparison.Ordinal) then
                        { s with
                            ContentHash = "modified_start" }
                    elif s.FullName.EndsWith("TestFix", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified_type" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            let affected = db.QueryAffectedTests changedNames
            test <@ affected |> List.exists (fun t -> t.TestMethod.Contains "standalone test") @>)

    [<Fact>]
    let ``test class declaring IClassFixture<T> interface gets a direct fixture edge`` () =
        // xUnit's IClassFixture<T> pattern: declaring the interface is how you opt in
        // to per-class fixture lifecycle. Layer 2a.1 detects this even when there's
        // no primary-ctor param (xUnit receives the fixture via interface).
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

// Local stand-in for xUnit's IClassFixture<T> — TestPrune matches by DisplayName.
type IClassFixture<'T> = interface end

type TestFix() =
    member _.start () = ()

type Tests() =
    interface IClassFixture<TestFix>

    [<Fact>]
    member _.``standalone test`` () =
        let x = 1 + 1
        ()
"""

            let result = analyze source

            // Sanity: the direct testMethod → TestFix edge exists via interface detection
            let testToFixture =
                result.Dependencies
                |> List.exists (fun d ->
                    d.FromSymbol.Contains "standalone test"
                    && d.ToSymbol.EndsWith("TestFix", StringComparison.Ordinal))

            test <@ testToFixture @>

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = result.ParentLinks
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("TestFix.start", StringComparison.Ordinal) then
                        { s with ContentHash = "modified" }
                    elif s.FullName.EndsWith("TestFix", StringComparison.Ordinal) && s.Kind = Type then
                        { s with ContentHash = "modified_type" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected |> List.exists (fun t -> t.TestMethod.Contains "standalone test") @>)

    [<Fact>]
    let ``ClassData typeof reference is captured as a dependency edge`` () =
        // xUnit's [<ClassData(typeof<T>)>] points theory tests at a data source class.
        // FCS emits a symbol use for T at the typeof<T> site (inside the attribute list),
        // which findEnclosing attributes to the enclosing test method. So this edge is
        // captured by the main dependency pass — no framework-specific handling needed.
        let source =
            """
module M

type FactAttribute() = inherit System.Attribute()
type TheoryAttribute() = inherit System.Attribute()

type ClassDataAttribute(t: System.Type) =
    inherit System.Attribute()

type DataProvider() =
    interface System.Collections.IEnumerable with
        member _.GetEnumerator() = (Seq.empty :> System.Collections.IEnumerable).GetEnumerator()

[<Theory>]
[<ClassData(typeof<DataProvider>)>]
let ``theory test`` () = ()
"""

        let result = analyze source

        let theoryToProvider =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.Contains "theory test"
                && d.ToSymbol.EndsWith("DataProvider", StringComparison.Ordinal))

        test <@ theoryToProvider @>

    [<Fact>]
    let ``MemberData nameof reference is captured as a dependency edge`` () =
        // xUnit's [<MemberData(nameof source)>] is the F# idiom. `nameof` is resolved
        // by the compiler to a string, but FCS still reports the underlying symbol use,
        // so the edge from the theory test to the data source gets captured.
        let source =
            """
module M

type FactAttribute() = inherit System.Attribute()
type TheoryAttribute() = inherit System.Attribute()

type MemberDataAttribute(name: string) =
    inherit System.Attribute()

let myData : int seq = Seq.empty

[<Theory>]
[<MemberData(nameof myData)>]
let ``theory via nameof`` (_: int) = ()
"""

        let result = analyze source

        let theoryToData =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.Contains "theory via nameof"
                && d.ToSymbol.EndsWith("myData", StringComparison.Ordinal))

        test <@ theoryToData @>

    [<Fact>]
    let ``TestPrune.DependsOn(typeof<T>) creates a dependency edge like any typeof arg`` () =
        // The TestPrune.Attributes package ships [<TestPrune.DependsOn(typeof<T>)>] as
        // a plain marker with no runtime behavior. The edge is captured for free by the
        // same FCS typeof-in-attribute-arg mechanism that handles ClassData/MemberData,
        // so the published attribute works without any special-case code in the analyzer.
        // This test uses a local stand-in attribute with the same shape as proof-by-analogy;
        // an end-to-end test requiring the Attributes assembly to be loaded by FCS is out
        // of scope for the script-based `analyze` harness.
        let source =
            """
namespace TestPrune

open System

[<AttributeUsage(AttributeTargets.Method, AllowMultiple = true)>]
type DependsOnAttribute(target: Type) =
    inherit Attribute()
    member _.Target = target

namespace Consumer

open System
open TestPrune

type FactAttribute() = inherit Attribute()

type ReflectionTarget() =
    member _.lookup () = 42

module Tests =
    [<Fact>]
    [<DependsOn(typeof<ReflectionTarget>)>]
    let ``reflection-backed test`` () = ()
"""

        let result = analyze source

        let testToTarget =
            result.Dependencies
            |> List.exists (fun d ->
                d.FromSymbol.Contains "reflection-backed test"
                && d.ToSymbol.EndsWith("ReflectionTarget", StringComparison.Ordinal))

        test <@ testToTarget @>

    [<Fact>]
    let ``Collection/CollectionDefinition resolves tests to fixture through synthetic symbol`` () =
        // xUnit collection fixtures: the test class names a collection by string
        // ([<Collection("name")>]) and the collection is declared elsewhere with
        // [<CollectionDefinition("name")>] + ICollectionFixture<T>. Layer 2a.2 bridges
        // the two with a synthetic "TestPrune.__Collection__.<name>" symbol.
        withDb (fun db ->
            let source =
                """
module M

open System

type FactAttribute() = inherit Attribute()
type CollectionAttribute(name: string) = inherit Attribute()
type CollectionDefinitionAttribute(name: string) = inherit Attribute()
type ICollectionFixture<'T> = interface end

type TestServerFixture() =
    member _.HttpClient = "c"

[<CollectionDefinition("Browser")>]
type BrowserCollection() =
    interface ICollectionFixture<TestServerFixture>

[<Collection("Browser")>]
type BrowserTests() =
    [<Fact>]
    member _.``browser test`` () = 1 + 1 |> ignore
"""

            let result = analyze source

            // Sanity: test method reaches the fixture via the synthetic collection symbol.
            let syntheticName = "TestPrune.__Collection__.Browser"

            let testToSynthetic =
                result.Dependencies
                |> List.exists (fun d -> d.FromSymbol.Contains "browser test" && d.ToSymbol = syntheticName)

            test <@ testToSynthetic @>

            let syntheticToFixture =
                result.Dependencies
                |> List.exists (fun d ->
                    d.FromSymbol = syntheticName
                    && d.ToSymbol.EndsWith("TestServerFixture", StringComparison.Ordinal))

            test <@ syntheticToFixture @>

            // End-to-end: editing the fixture flows through the synthetic to the test.
            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = result.ParentLinks
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if
                        s.FullName.EndsWith("TestServerFixture", StringComparison.Ordinal)
                        && s.Kind = Type
                    then
                        { s with
                            ContentHash = "modified_fixture" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes
            let affected = db.QueryAffectedTests changedNames
            test <@ affected |> List.exists (fun t -> t.TestMethod.Contains "browser test") @>)

    [<Fact>]
    let ``module sibling edits do not fan out to unrelated tests`` () =
        // Aggregate-type invalidation must NOT apply to module members: editing one
        // helper in a module must not invalidate tests whose only connection is a
        // different helper in the same module.
        withDb (fun db ->
            let source =
                """
module M

type FactAttribute() =
    inherit System.Attribute()

let alpha x = x + 1
let beta x = x * 2

[<Fact>]
let ``uses alpha only`` () = alpha 3 |> ignore
"""

            let result = analyze source

            let analysis =
                { Symbols = result.Symbols |> List.map (fun s -> { s with SourceFile = "src/M.fs" })
                  Dependencies = result.Dependencies
                  TestMethods = result.TestMethods |> List.map (fun t -> { t with TestProject = "TestProject" })
                  Attributes = []
                  ParentLinks = result.ParentLinks
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            // `beta` and `alpha` live in the same module but are independent bindings.
            // Editing `beta` should NOT select the test that only touches `alpha`.
            let storedSymbols = db.GetSymbolsInFile "src/M.fs"

            let modifiedSymbols =
                storedSymbols
                |> List.map (fun s ->
                    if s.FullName.EndsWith("beta", StringComparison.Ordinal) then
                        { s with ContentHash = "modified_beta" }
                    else
                        s)

            let changes, _events = detectChanges modifiedSymbols storedSymbols
            let changedNames = changedSymbolNames changes

            let affected = db.QueryAffectedTests changedNames

            test <@ affected |> List.forall (fun t -> not (t.TestMethod.Contains "uses alpha only")) @>)
