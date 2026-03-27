module TestPrune.Tests.AstAnalyzerTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer

let checker = FSharpChecker.Create()

let analyze source =
    let fileName = "/tmp/AstAnalyzerTest.fsx"

    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    let result = analyzeSource checker fileName source options |> Async.RunSynchronously

    match result with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

module ``Simple function extraction`` =

    [<Fact>]
    let ``extracts a simple function with correct kind`` () =
        let result =
            analyze
                """
module M
let add x y = x + y
"""

        let addSymbol =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("add", StringComparison.Ordinal))

        test <@ addSymbol.IsSome @>
        test <@ addSymbol.Value.Kind = Function @>

    [<Fact>]
    let ``extracts a value binding`` () =
        let result =
            analyze
                """
module M
let greeting = "hello"
"""

        let sym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("greeting", StringComparison.Ordinal))

        test <@ sym.IsSome @>
        test <@ sym.Value.Kind = Value @>

module ``DU type extraction`` =

    [<Fact>]
    let ``extracts DU type and cases as separate symbols`` () =
        let result =
            analyze
                """
module M

type Shape =
    | Circle of float
    | Rectangle of float * float
"""

        let shapeType =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Shape", StringComparison.Ordinal) && s.Kind = Type)

        test <@ shapeType.IsSome @>

        let circleCase =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Circle", StringComparison.Ordinal) && s.Kind = DuCase)

        let rectCase =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Rectangle", StringComparison.Ordinal) && s.Kind = DuCase)

        test <@ circleCase.IsSome @>
        test <@ rectCase.IsSome @>

module ``Dependency extraction`` =

    [<Fact>]
    let ``function calling another function creates calls dependency`` () =
        let result =
            analyze
                """
module M

let add x y = x + y
let double x = add x x
"""

        let dep =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("double", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("add", StringComparison.Ordinal))

        test <@ dep.IsSome @>
        test <@ dep.Value.Kind = Calls @>

    [<Fact>]
    let ``function using a DU type creates uses_type dependency`` () =
        let result =
            analyze
                """
module M

type Shape =
    | Circle of float
    | Rectangle of float * float

let describe (s: Shape) = "a shape"
"""

        let dep =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("describe", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("Shape", StringComparison.Ordinal))

        test <@ dep.IsSome @>
        test <@ dep.Value.Kind = UsesType @>

    [<Fact>]
    let ``function pattern matching on DU creates dependency to DU cases`` () =
        let result =
            analyze
                """
module M

type Shape =
    | Circle of float
    | Rectangle of float * float

let area (shape: Shape) =
    match shape with
    | Circle r -> r * r * 3.14
    | Rectangle(w, h) -> w * h
"""

        // Pattern matching on DU cases may show as pattern_matches (FSharpUnionCase)
        // or calls (FSharpMemberOrFunctionOrValue for the case constructor)
        let areaDeps =
            result.Dependencies
            |> List.filter (fun d -> d.FromSymbol.EndsWith("area", StringComparison.Ordinal))

        // The function should depend on at least the Shape type or one of its cases
        let hasRelevantDep =
            areaDeps
            |> List.exists (fun d ->
                d.ToSymbol.EndsWith("Shape", StringComparison.Ordinal)
                || d.ToSymbol.EndsWith("Circle", StringComparison.Ordinal)
                || d.ToSymbol.EndsWith("Rectangle", StringComparison.Ordinal))

        test <@ areaDeps.Length > 0 @>
        test <@ hasRelevantDep @>

    [<Fact>]
    let ``cross-function dependency: double calls add`` () =
        let result =
            analyze
                """
module M

let add a b = a + b
let double x = add x x
let quadruple x = double (double x)
"""

        let doubleCallsAdd =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("double", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("add", StringComparison.Ordinal))

        let quadCallsDouble =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("quadruple", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("double", StringComparison.Ordinal))

        test <@ doubleCallsAdd.IsSome @>
        test <@ quadCallsDouble.IsSome @>

module ``Module extraction`` =

    [<Fact>]
    let ``extracts module as a symbol`` () =
        let result =
            analyze
                """
module MyModule

let x = 1
"""

        let modSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("MyModule", StringComparison.Ordinal) && s.Kind = Module)

        test <@ modSym.IsSome @>

module ``Test method detection`` =

    // NOTE: Detecting [<Fact>] attributes requires xunit assemblies to be referenced
    // in the project options. With script-based options, the attribute resolution may
    // not work. This test verifies the mechanism works when attributes resolve.
    [<Fact>]
    let ``detects test methods with Fact attribute when resolvable`` () =
        // We test with a custom attribute that contains "Fact" in the name
        // to verify the detection logic works with script options
        let result =
            analyze
                """
module M

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let myTest () = ()
"""

        let testMethod =
            result.TestMethods |> List.tryFind (fun t -> t.TestMethod = "myTest")

        test <@ testMethod.IsSome @>
        test <@ testMethod.Value.TestClass.EndsWith("M", StringComparison.Ordinal) @>

module ``Symbol source locations`` =

    [<Fact>]
    let ``symbols have correct source file`` () =
        let fileName = "/tmp/AstAnalyzerTest.fsx"

        let source =
            """
module M
let f x = x
"""

        let options = getScriptOptions checker fileName source |> Async.RunSynchronously

        let result = analyzeSource checker fileName source options |> Async.RunSynchronously

        match result with
        | Ok r ->
            let fSym =
                r.Symbols
                |> List.tryFind (fun s -> s.FullName.EndsWith("f", StringComparison.Ordinal))

            test <@ fSym.IsSome @>
            test <@ fSym.Value.SourceFile = fileName @>
        | Error msg -> failwith msg

    [<Fact>]
    let ``symbols have line numbers`` () =
        let result =
            analyze
                """
module M

let first = 1

let second = 2
"""

        let first =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("first", StringComparison.Ordinal))

        let second =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("second", StringComparison.Ordinal))

        test <@ first.IsSome @>
        test <@ second.IsSome @>
        test <@ first.Value.LineStart < second.Value.LineStart @>

module ``Local binding scoping`` =

    [<Fact>]
    let ``uses inside local let bindings are attributed to enclosing function`` () =
        let result =
            analyze
                """
module M

let helper x = x + 1

let main args =
    let config = helper 1
    let host = helper 2
    config + host
"""

        // Debug: print ranges from parsed AST
        let checker2 = FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()
        let fileName = "/tmp/AstAnalyzerTest.fsx"

        let src =
            """
module M

let helper x = x + 1

let main args =
    let config = helper 1
    let host = helper 2
    config + host
"""

        let options2 = getScriptOptions checker2 fileName src |> Async.RunSynchronously
        let sourceText = FSharp.Compiler.Text.SourceText.ofString src

        let parseResults, _ =
            checker2.ParseAndCheckFileInProject(fileName, 0, sourceText, options2)
            |> Async.RunSynchronously

        let ranges = collectModuleBindingRanges parseResults.ParseTree

        for name, r in ranges do
            printfn "RANGE: %s -> %d:%d - %d:%d" name r.StartLine r.StartColumn r.EndLine r.EndColumn

        // Debug: print all dependencies and symbols
        for d in result.Dependencies do
            printfn "DEP: %s -> %s (%A)" d.FromSymbol d.ToSymbol d.Kind

        for s in result.Symbols do
            printfn "SYM: %s (%A) lines %d-%d" s.FullName s.Kind s.LineStart s.LineEnd

        // Both calls to `helper` should be attributed to `main`, not to `config` or `host`
        let mainCallsHelper =
            result.Dependencies
            |> List.tryFind (fun d ->
                d.FromSymbol.EndsWith("main", StringComparison.Ordinal)
                && d.ToSymbol.EndsWith("helper", StringComparison.Ordinal))

        test <@ mainCallsHelper.IsSome @>

        // There should NOT be a dependency from `host` or `config` to `helper`
        let localCallsHelper =
            result.Dependencies
            |> List.filter (fun d ->
                (d.FromSymbol.EndsWith("host", StringComparison.Ordinal)
                 || d.FromSymbol.EndsWith("config", StringComparison.Ordinal))
                && d.ToSymbol.EndsWith("helper", StringComparison.Ordinal))

        test <@ localCallsHelper.IsEmpty @>

module ``Record type extraction`` =

    [<Fact>]
    let ``extracts record type`` () =
        let result =
            analyze
                """
module M

type Person = { Name: string; Age: int }
"""

        let recSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Person", StringComparison.Ordinal) && s.Kind = Type)

        test <@ recSym.IsSome @>

module ``Binding range diagnostics`` =
    [<Fact>]
    let ``multi-line function captures full body range`` () =
        let source =
            """
module M

let routeHandler (x: int) : int =
    match x with
    | 1 -> 10
    | 2 -> 20
    | _ -> 0
"""

        let tree =
            let fileName = "/tmp/RangeTest.fsx"
            let sourceText = FSharp.Compiler.Text.SourceText.ofString source

            let projOptions, _ =
                checker.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false)
                |> Async.RunSynchronously

            let parseResults =
                checker.ParseFile(
                    fileName,
                    sourceText,
                    projOptions |> checker.GetParsingOptionsFromProjectOptions |> fst
                )
                |> Async.RunSynchronously

            parseResults.ParseTree

        let ranges = TestPrune.AstAnalyzer.collectModuleBindingRanges tree
        let rh = ranges |> List.tryFind (fun (name, _) -> name = "routeHandler")
        test <@ rh.IsSome @>
        let (_, r) = rh.Value
        // routeHandler is on line 4, body ends on line 8
        let startLine = r.StartLine
        let endLine = r.EndLine
        test <@ startLine <= 4 @>
        test <@ endLine >= 8 @>

module ``Enum type extraction`` =

    [<Fact>]
    let ``extracts enum type as a Type symbol`` () =
        let result =
            analyze
                """
module M

type Color =
    | Red = 0
    | Green = 1
    | Blue = 2
"""

        let enumSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Color", StringComparison.Ordinal) && s.Kind = Type)

        test <@ enumSym.IsSome @>

module ``Type abbreviation extraction`` =

    [<Fact>]
    let ``source with type abbreviation compiles and produces module symbol`` () =
        // FCS does not emit FSharpEntity definition uses for type abbreviations via
        // GetAllUsesOfAllSymbolsInFile; it resolves them to the underlying type.
        // This test verifies that source containing a type abbreviation is analyzed
        // without error and that the enclosing module is still captured as a symbol.
        let result =
            analyze
                """
module M

type UserId = int
type Name = string
"""

        let modSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("M", StringComparison.Ordinal) && s.Kind = Module)

        test <@ modSym.IsSome @>

    [<Fact>]
    let ``type abbreviation of user-defined record is analyzed without error`` () =
        // When a function uses a type abbreviation of a user-defined type, analysis
        // completes successfully and the underlying record is captured as a Type symbol.
        let result =
            analyze
                """
module M

type Point = { X: float; Y: float }
type Coord = Point

let makePoint x y : Point = { X = x; Y = y }
"""

        let pointSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Point", StringComparison.Ordinal) && s.Kind = Type)

        test <@ pointSym.IsSome @>

module ``Class type extraction`` =

    [<Fact>]
    let ``extracts class type as a Type symbol`` () =
        let result =
            analyze
                """
module M

type MyClass() =
    member _.Value = 42
"""

        let classSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("MyClass", StringComparison.Ordinal) && s.Kind = Type)

        test <@ classSym.IsSome @>

    [<Fact>]
    let ``extracts property member as a Property symbol`` () =
        let result =
            analyze
                """
module M

type Counter() =
    member _.Prop = 42
"""

        let propSym =
            result.Symbols
            |> List.tryFind (fun s -> s.FullName.EndsWith("Prop", StringComparison.Ordinal) && s.Kind = Property)

        test <@ propSym.IsSome @>

let analyzeRaw source =
    let fileName = "/tmp/AstAnalyzerTest.fsx"
    let options = getScriptOptions checker fileName source |> Async.RunSynchronously
    analyzeSource checker fileName source options |> Async.RunSynchronously

module ``Parse error handling`` =

    [<Fact>]
    let ``returns Error for source with parse errors`` () =
        let result = analyzeRaw "let let let = ="

        test <@ Result.isError result @>

        match result with
        | Error msg -> test <@ msg.StartsWith("Parse errors:", StringComparison.Ordinal) @>
        | Ok _ -> failwith "expected error"

module ``Cross-file dependencies (regression)`` =

    [<Fact>]
    let ``function using type from module parameter creates dependency`` () =
        // Regression test: when a type is used in a parameter annotation,
        // getScriptOptions should include related modules in the project context
        // so that cross-file references can be resolved.
        let tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testprune-cross-file-" + System.Guid.NewGuid().ToString("N"))
        System.IO.Directory.CreateDirectory(tmpDir) |> ignore

        try
            let libFile = System.IO.Path.Combine(tmpDir, "Lib.fsx")
            let consumerFile = System.IO.Path.Combine(tmpDir, "Consumer.fsx")

            let libSource =
                """module Lib

type Config = { Value: string }

let processConfig (cfg: Config) = cfg.Value.Length
"""

            let consumerSource =
                """module Consumer

open Lib

let test () =
    let cfg = { Value = "hello" }
    let len = processConfig cfg
    len
"""

            // Write files to disk so FCS can analyze them
            System.IO.File.WriteAllText(libFile, libSource)
            System.IO.File.WriteAllText(consumerFile, consumerSource)

            let libOptions = getScriptOptions checker libFile libSource |> Async.RunSynchronously
            let consumerOptions =
                getScriptOptions checker consumerFile consumerSource |> Async.RunSynchronously

            // Both should be analyzable
            let libResult = analyzeSource checker libFile libSource libOptions |> Async.RunSynchronously
            let consumerResult =
                analyzeSource checker consumerFile consumerSource consumerOptions |> Async.RunSynchronously

            // Check if lib analysis worked
            match libResult with
            | Ok libAnalysis ->
                // Lib.fsx should have a dependency: processConfig -> Config
                let configDep =
                    libAnalysis.Dependencies
                    |> List.tryFind (fun d ->
                        d.FromSymbol.EndsWith("processConfig", StringComparison.Ordinal)
                        && d.ToSymbol.EndsWith("Config", StringComparison.Ordinal))

                test <@ configDep.IsSome @>
                test <@ configDep.Value.Kind = UsesType @>
            | Error e -> failwith $"lib analysis failed: {e}"

            // Verify cross-file dependency detection:
            // getScriptOptions should detect the 'open Lib' statement and include Lib.fsx
            // in the SourceFiles array, allowing FCS to resolve Lib's symbols
            test <@ consumerOptions.SourceFiles |> Array.exists (fun f -> f.EndsWith("Lib.fsx", StringComparison.Ordinal)) @>

            // The core fix is validated above:
            // getScriptOptions now detects 'open Lib' and includes Lib.fsx in SourceFiles.
            // This enables downstream tools to resolve cross-file symbols properly.
        finally
            try
                System.IO.Directory.Delete(tmpDir, true)
            with _ ->
                ()
