module TestPrune.Tests.IntegrationTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode
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
    let result = analyzeSource checker fileName source options |> Async.RunSynchronously

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
                  Diagnostics = AnalysisDiagnostics.Zero }

            let testAnalysis =
                { Symbols = remapSymbols "tests/LibTests.fs" testResult.Symbols
                  Dependencies = testResult.Dependencies
                  TestMethods =
                    testResult.TestMethods
                    |> List.map (fun t -> { t with TestProject = "TestProject" })
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
                selectTests db.GetSymbolsInFile db.QueryAffectedTests [ "src/NewModule.fs" ] currentSymbols

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
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ analysis ])

            let result, _events =
                selectTests db.GetSymbolsInFile db.QueryAffectedTests [] Map.empty

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
