module TestPrune.Tests.AnonymousRecordAnalyzerTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Analyzers.SDK
open TestPrune.Analyzers.AnonymousRecordAnalyzer

/// Shared across every test module in this file; each carries
/// [<Collection("FCS-AnonymousRecord")>] so they serialize with each other
/// instead of hitting this single FSharpChecker instance in parallel.
/// New test modules added to this file must join the same collection.
let private checker = FSharpChecker.Create()

/// Parse `source` under `fileName` and return the collected anonymous-record ranges.
/// `fileName` selects the parse mode: a `.fsx`/`.fs` implementation file by default, or a
/// `.fsi` signature file when called with such an extension.
let private collectIn (fileName: string) (source: string) =
    let sourceText = FSharp.Compiler.Text.SourceText.ofString source

    let projOptions, _ =
        checker.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously

    let parseResults =
        checker.ParseFile(fileName, sourceText, projOptions |> checker.GetParsingOptionsFromProjectOptions |> fst)
        |> Async.RunSynchronously

    collectAnonRecordRanges parseResults.ParseTree

/// Parse `source` as an implementation file (`.fsx`) and return the collected ranges.
let private collect (source: string) =
    collectIn "/tmp/AnonRecordTest.fsx" source

let private messagesFor (source: string) = collect source |> buildMessages

[<Collection("FCS-AnonymousRecord")>]
module ``Anonymous-record expressions`` =

    [<Fact>]
    let ``flags a single anon-record expression and reports it on its line`` () =
        let ranges =
            collect
                """
module M
let x = {| Year = 2026 |}
"""

        test <@ List.length ranges = 1 @>
        // Anon record is on line 3 of the source.
        let startLine = (List.exactlyOne ranges).StartLine
        test <@ startLine = 3 @>

    [<Fact>]
    let ``flags an anon record built with copy-and-update`` () =
        let ranges =
            collect
                """
module M
let base' = {| Year = 2026 |}
let derived = {| base' with Month = 6 |}
"""

        // base' definition + the copy-update expression = 2 anon records.
        test <@ List.length ranges = 2 @>

[<Collection("FCS-AnonymousRecord")>]
module ``Anonymous-record type annotations`` =

    [<Fact>]
    let ``flags an anon-record type annotation on a binding`` () =
        let ranges =
            collect
                """
module M
let f (x: {| Year: int |}) = x.Year
"""

        test <@ List.length ranges >= 1 @>

    [<Fact>]
    let ``flags an anon-record return-type annotation`` () =
        let ranges =
            collect
                """
module M
let make () : {| Year: int |} = {| Year = 2026 |}
"""

        // One for the return-type annotation, one for the expression.
        test <@ List.length ranges = 2 @>

[<Collection("FCS-AnonymousRecord")>]
module ``Nested and multiple occurrences`` =

    [<Fact>]
    let ``flags each anon record in a nested anon record`` () =
        let ranges =
            collect
                """
module M
let x = {| Outer = {| Inner = 1 |} |}
"""

        test <@ List.length ranges = 2 @>

    [<Fact>]
    let ``flags multiple independent anon records`` () =
        let ranges =
            collect
                """
module M
let a = {| X = 1 |}
let b = {| Y = 2 |}
let c = {| Z = 3 |}
"""

        test <@ List.length ranges = 3 @>

    [<Fact>]
    let ``flags anon records inside a list`` () =
        let ranges =
            collect
                """
module M
let xs = [ {| N = 1 |}; {| N = 2 |} ]
"""

        test <@ List.length ranges = 2 @>

[<Collection("FCS-AnonymousRecord")>]
module ``Nested inside control-flow and binding constructs`` =

    /// Each fixture embeds anonymous records inside a different syntactic construct,
    /// asserting the walk descends through that construct's children. `expected` is the
    /// number of distinct anon-record occurrences the construct should surface.
    [<Theory>]
    [<InlineData("let f = fun x -> {| V = x |}", 1)>] // lambda body
    [<InlineData("let f x = if x then {| V = 1 |} else {| V = 2 |}", 2)>] // both branches
    [<InlineData("let f x = match x with | 0 -> {| V = 0 |} | _ -> {| V = 1 |}", 2)>] // match clauses
    [<InlineData("let f x = match x with | n when n > 0 -> {| V = n |} | _ -> {| V = 0 |}", 2)>] // when + clauses
    [<InlineData("let f () = let y = {| V = 1 |} in y", 1)>] // let-in body
    [<InlineData("let f () = (printfn \"a\"); {| V = 1 |}", 1)>] // sequential
    [<InlineData("let f () = try {| V = 1 |} with _ -> {| V = 0 |}", 2)>] // try/with
    [<InlineData("let f () = for i in 1..2 do ignore {| V = i |}", 1)>] // for body
    [<InlineData("let f xs = for x in xs do ignore {| V = x |}", 1)>] // foreach body
    [<InlineData("let f () = while true do ignore {| V = 1 |}", 1)>] // while body
    [<InlineData("let f () = [ for i in 1..2 -> {| V = i |} ]", 1)>] // list comprehension
    [<InlineData("let f () = ({| V = 1 |} : obj) |> ignore", 1)>] // typed expr
    [<InlineData("let f () = (fun () -> {| V = 1 |}) () |> ignore", 1)>] // app + paren + lambda
    [<InlineData("let f () = printfn \"%A\" {| V = 1 |}", 1)>] // app arg
    let ``flags the anon record nested in the construct`` (body: string, expected: int) =
        let ranges = collect (sprintf "module M\n%s\n" body)
        test <@ List.length ranges = expected @>

    [<Fact>]
    let ``flags an anon record returned from a member`` () =
        let ranges =
            collect
                """
module M
type T() =
    member _.Make() = {| V = 1 |}
"""

        test <@ List.length ranges = 1 @>

    [<Fact>]
    let ``flags an anon-record field type inside a named record`` () =
        let ranges =
            collect
                """
module M
type Wrapper = { Inner: {| Year: int |} }
"""

        test <@ List.length ranges = 1 @>

    [<Fact>]
    let ``flags an anon-record type abbreviation`` () =
        let ranges =
            collect
                """
module M
type Alias = {| Year: int |}
"""

        test <@ List.length ranges = 1 @>

    [<Fact>]
    let ``flags an anon record inside a nested module`` () =
        let ranges =
            collect
                """
module M
module Inner =
    let x = {| V = 1 |}
"""

        test <@ List.length ranges = 1 @>

    [<Fact>]
    let ``flags an anon record interpolated into a string`` () =
        let ranges =
            collect
                """
module M
let s = $"value: {({| V = 1 |})}"
"""

        test <@ List.length ranges = 1 @>

    [<Fact>]
    let ``flags an anon record in a tuple-element type annotation`` () =
        let ranges =
            collect
                """
module M
let f (p: int * {| Year: int |}) = p
"""

        test <@ List.length ranges = 1 @>

[<Collection("FCS-AnonymousRecord")>]
module ``No false positives`` =

    [<Fact>]
    let ``a named record produces no diagnostics`` () =
        let ranges =
            collect
                """
module M
type Point = { X: int; Y: int }
let p = { X = 1; Y = 2 }
"""

        test <@ List.isEmpty ranges @>

    [<Fact>]
    let ``tuples and primitives produce no diagnostics`` () =
        let ranges =
            collect
                """
module M
let t = (1, "two", 3.0)
let n = 42
let s = "hello"
"""

        test <@ List.isEmpty ranges @>

    [<Fact>]
    let ``a record type annotation produces no diagnostics`` () =
        let ranges =
            collect
                """
module M
type Point = { X: int; Y: int }
let f (p: Point) = p.X
"""

        test <@ List.isEmpty ranges @>

    /// Exercises the negative arms of walkType / walkPat (generics, arrays, functions,
    /// tuples, hash constraints, typed/tuple/list patterns) without any anon record.
    [<Theory>]
    [<InlineData("let f (xs: int list) = xs")>] // generic app
    [<InlineData("let f (xs: int[]) = xs")>] // array type
    [<InlineData("let f (g: int -> string) = g 1")>] // function type
    [<InlineData("let f (p: int * string) = p")>] // tuple type
    [<InlineData("let f (x: #seq<int>) = x")>] // hash constraint
    [<InlineData("let f (Some x) = x")>] // long-ident arg pattern
    [<InlineData("let f ((a, b): int * int) = a + b")>] // typed tuple pattern
    [<InlineData("let f ([ a ]: int list) = a")>] // list pattern
    [<InlineData("let f (x: int as y) = x + y")>] // as pattern
    [<InlineData("let f ({ contents = c }: int ref) = c")>] // record pattern
    let ``rich types and patterns without anon records produce no diagnostics`` (body: string) =
        let ranges = collect (sprintf "module M\n%s\n" body)
        test <@ List.isEmpty ranges @>

    [<Fact>]
    let ``a signature file produces no diagnostics`` () =
        let ranges = collectIn "/tmp/AnonRecordTest.fsi" "module M\nval x: int\n"
        test <@ List.isEmpty ranges @>

[<Collection("FCS-AnonymousRecord")>]
module ``Diagnostic shape`` =

    [<Fact>]
    let ``message carries the stable code, severity, and impact-analysis guidance`` () =
        let messages =
            messagesFor
                """
module M
let x = {| Year = 2026 |}
"""

        let m = List.exactlyOne messages
        test <@ m.Code = "TP001" @>
        test <@ m.Type = "TestPrune.AnonymousRecord" @>
        test <@ m.Severity = Severity.Warning @>
        test <@ m.Message.Contains "impact analysis" @>
        test <@ m.Message.Contains "DependsOnFile" @>

    [<Fact>]
    let ``empty source produces no messages`` () =
        let messages =
            messagesFor
                """
module M
let x = 1
"""

        test <@ List.isEmpty messages @>

/// Gate wiring — the analyzer TestPrune SHIPS must be the analyzer TestPrune RUNS ON ITSELF.
///
/// The tests above prove the RULE fires; they say nothing about whether the gate ever
/// LOADS it. That distinction is the whole point of this suite: an analyzer that reports
/// nothing and an analyzer that was never loaded produce byte-identical output — a silent
/// green. So these tests assert the LOAD, from the very path `.fshw.json` hands the
/// analyzer host, using the same SDK entry point (`Client.LoadAnalyzers`) the host uses.
///
/// Read the path out of `.fshw.json` rather than hard-coding it: if the config drifts from
/// where the build actually emits the DLL (renamed project, changed OutputPath, TFM bump),
/// these go red instead of the gate quietly analyzing nothing.
[<Collection("FCS-AnonymousRecord")>]
module ``Gate wiring`` =

    /// Walk up from the test binary to the repo root — the directory holding `.fshw.json`.
    let private repoRoot () =
        let rec up (dir: DirectoryInfo) =
            if isNull (box dir) then
                failwith "repo root not found: no .fshw.json in any ancestor of the test binary"
            elif File.Exists(Path.Combine(dir.FullName, ".fshw.json")) then
                dir.FullName
            else
                up dir.Parent

        up (DirectoryInfo(AppContext.BaseDirectory))

    /// The `analyzers.paths` the gate actually loads from, resolved against the repo root.
    let private configuredAnalyzerPaths (root: string) =
        use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".fshw.json")))

        doc.RootElement.GetProperty("analyzers").GetProperty("paths").EnumerateArray()
        |> Seq.map (fun e -> Path.Combine(root, e.GetString()))
        |> List.ofSeq

    [<Fact>]
    let ``.fshw.json configures at least one analyzers path`` () =
        // A gate with no analyzers configured is the silent-zero failure by another name.
        test <@ not (configuredAnalyzerPaths (repoRoot ()) |> List.isEmpty) @>

    [<Fact>]
    let ``every analyzers path in .fshw.json loads at least one analyzer`` () =
        let paths = configuredAnalyzerPaths (repoRoot ())

        for path in paths do
            // The directory must exist AND yield a loadable [<CliAnalyzer>]. `LoadAnalyzers`
            // is exactly what FsHotWatch's AnalyzersPlugin calls, so a pass here means the
            // real host will load it too — and a zero here is the cheerful-zero we refuse.
            test <@ Directory.Exists path @>

            let client = Client<CliAnalyzerAttribute, CliContext>()
            let stats = client.LoadAnalyzers path
            test <@ stats.Analyzers > 0 @>

    [<Fact>]
    let ``TestPrune's own analyzer assembly is the one on the configured path`` () =
        // Binds the identity, not just "some analyzer": the DLL the gate loads is the one
        // this repo builds and publishes as the TestPrune.Analyzers package.
        let paths = configuredAnalyzerPaths (repoRoot ())

        let hasOwnAnalyzer =
            paths
            |> List.exists (fun p -> File.Exists(Path.Combine(p, "TestPrune.Analyzers.dll")))

        test <@ hasOwnAnalyzer @>
