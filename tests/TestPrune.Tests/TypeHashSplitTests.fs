module TestPrune.Tests.TypeHashSplitTests

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.ImpactAnalysis
open TestPrune.InMemoryStore

/// Shared across every test module in this file; each carries
/// [<Collection("FCS-TypeHashSplit")>] so they serialize with each other instead of
/// hitting this single FSharpChecker instance in parallel.
let checker = FSharpChecker.Create()

let private fileName = "/tmp/TypeHashSplitTest.fsx"

let analyze source =
    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    match
        analyzeSource checker fileName source options "TestProject"
        |> Async.RunSynchronously
    with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

/// Which hashes differ between the two versions, as `(kind, name)` pairs, over every
/// symbol present in both.
let private changedBetween (before: AnalysisResult) (after: AnalysisResult) =
    let index (r: AnalysisResult) =
        r.Symbols
        |> List.map (fun s -> (s.Kind, s.FullName), s.ContentHash)
        |> Map.ofList

    let b = index before
    let a = index after

    a
    |> Map.toList
    |> List.choose (fun (key, h) ->
        match b |> Map.tryFind key with
        | Some old when old <> h -> Some key
        | _ -> None)
    |> Set.ofList

let private selected (before: AnalysisResult) (after: AnalysisResult) =
    let store = fromAnalysisResults [ before ]

    match selectTests store [ fileName ] (Map.ofList [ fileName, after.Symbols ]) |> fst with
    | RunAll reason -> failwith $"expected a subset, got RunAll %A{reason}"
    | RunSubset tests -> tests |> List.map _.TestMethod |> Set.ofList

[<Collection("FCS-TypeHashSplit")>]
module ``Union case edits`` =

    let private shape circlePayload =
        $"""
module M

type Shape =
    | Circle of radius: %s{circlePayload}
    | Square of side: float
"""

    [<Fact>]
    let ``editing one case payload changes that case and not the type header`` () =
        let before = analyze (shape "float")
        let after = analyze (shape "double")

        let changed = changedBetween before after
        test <@ changed = set [ DuCase, "M.Shape.Circle" ] @>

    [<Fact>]
    let ``a multi-line case payload is covered by the case hash`` () =
        let mk t =
            $"""
module M

type Shape =
    | Circle of
        radius: float *
        label: %s{t}
    | Square of side: float
"""

        let changed = changedBetween (analyze (mk "string")) (analyze (mk "int"))
        test <@ changed = set [ DuCase, "M.Shape.Circle" ] @>

    [<Fact>]
    let ``cases on one line hash independently`` () =
        let mk t =
            $"""
module M
type Shape = Circle of float | Square of %s{t}
"""

        let changed = changedBetween (analyze (mk "float")) (analyze (mk "int"))
        test <@ changed = set [ DuCase, "M.Shape.Square" ] @>

    [<Fact>]
    let ``adding a case changes the type header`` () =
        let before = analyze (shape "float")

        let after =
            analyze
                """
module M

type Shape =
    | Circle of radius: float
    | Square of side: float
    | Triangle of float
"""

        let changed = changedBetween before after
        test <@ changed |> Set.contains (Type, "M.Shape") @>

    [<Fact>]
    let ``reordering cases changes the type header`` () =
        let before = analyze (shape "float")

        let after =
            analyze
                """
module M

type Shape =
    | Square of side: float
    | Circle of radius: float
"""

        let changed = changedBetween before after
        test <@ changed |> Set.contains (Type, "M.Shape") @>

    [<Fact>]
    let ``an attribute on the type changes the header`` () =
        let before = analyze (shape "float")

        let after =
            analyze
                """
module M

[<RequireQualifiedAccess>]
type Shape =
    | Circle of radius: float
    | Square of side: float
"""

        let changed = changedBetween before after
        test <@ changed |> Set.contains (Type, "M.Shape") @>

    [<Fact>]
    let ``a RequireQualifiedAccess union still splits case edits from the header`` () =
        let mk t =
            $"""
module M

[<RequireQualifiedAccess>]
type Shape =
    | Circle of radius: %s{t}
    | Square of side: float
"""

        let changed = changedBetween (analyze (mk "float")) (analyze (mk "int"))
        test <@ changed = set [ DuCase, "M.Shape.Circle" ] @>

    [<Fact>]
    let ``a single-case union's payload edit changes only the case`` () =
        let mk t =
            $"""
module M
type OrderId = OrderId of %s{t}
"""

        let changed = changedBetween (analyze (mk "int")) (analyze (mk "string"))
        test <@ changed = set [ DuCase, "M.OrderId.OrderId" ] @>

    [<Fact>]
    let ``a struct union's payload edit changes the header, since its cases share one layout`` () =
        let mk t =
            $"""
module M

[<Struct>]
type Shape =
    | Circle of radius: %s{t}
    | Square of side: float
"""

        let changed = changedBetween (analyze (mk "float")) (analyze (mk "int"))
        test <@ changed = set [ Type, "M.Shape"; DuCase, "M.Shape.Circle" ] @>

    [<Fact>]
    let ``an attribute on one case changes that case, not the header`` () =
        let mk attr =
            $"""
module M

type Shape =
    | %s{attr}Circle of radius: float
    | Square of side: float
"""

        let changed =
            changedBetween (analyze (mk "")) (analyze (mk "[<CompiledName(\"Round\")>] "))

        test <@ changed = set [ DuCase, "M.Shape.Circle" ] @>

    [<Fact>]
    let ``an active pattern next to the type is hashed apart from it`` () =
        let mk body =
            $"""
module M

type Shape =
    | Circle of radius: float
    | Square of side: float

let (|Round|Angular|) s =
    match s with
    | Circle _ -> %s{body}
    | Square _ -> Angular
"""

        let changed =
            changedBetween (analyze (mk "Round")) (analyze (mk "(ignore 1; Round)"))

        test <@ changed = set [ Function, "M.(|Round|Angular|)" ] @>

[<Collection("FCS-TypeHashSplit")>]
module ``Member edits`` =

    [<Fact>]
    let ``editing one member body changes only that member`` () =
        let mk body =
            $"""
module M

type Counter(start: int) =
    member _.Start = start
    member _.Next() =
        %s{body}
    member _.Reset() = 0
"""

        let changed = changedBetween (analyze (mk "start + 1")) (analyze (mk "start + 2"))
        test <@ changed = set [ Function, "M.Counter.Next" ] @>

    [<Fact>]
    let ``editing a multi-line property body changes that property, not the header`` () =
        let mk body =
            $"""
module M

type Counter(start: int) =
    member _.Doubled =
        let twice = start * 2
        %s{body}
    member _.Reset() = 0
"""

        let changed = changedBetween (analyze (mk "twice")) (analyze (mk "twice + 1"))
        test <@ changed = set [ Property, "M.Counter.Doubled" ] @>

    [<Fact>]
    let ``editing a member on a union changes that member, not the header`` () =
        let mk body =
            $"""
module M

type Shape =
    | Circle of radius: float
    | Square of side: float

    member this.Area =
        match this with
        | Circle r -> %s{body}
        | Square s -> s * s
"""

        let changed = changedBetween (analyze (mk "r * r")) (analyze (mk "3.14 * r * r"))
        test <@ changed = set [ Property, "M.Shape.Area" ] @>

    [<Fact>]
    let ``adding a member changes the header`` () =
        let before =
            analyze
                """
module M

type Counter(start: int) =
    member _.Start = start
"""

        let after =
            analyze
                """
module M

type Counter(start: int) =
    member _.Start = start
    member _.Next() = start + 1
"""

        let changed = changedBetween before after
        test <@ changed |> Set.contains (Type, "M.Counter") @>

    [<Fact>]
    let ``a class let binding is not a symbol, so its edit changes the header`` () =
        let mk v =
            $"""
module M

type Counter(start: int) =
    let offset = %s{v}
    member _.Next() = start + offset
"""

        let changed = changedBetween (analyze (mk "1")) (analyze (mk "2"))
        test <@ changed |> Set.contains (Type, "M.Counter") @>

    [<Fact>]
    let ``an interface implementation body edit changes that member, not the header`` () =
        let mk body =
            $"""
module M

type IGreeter =
    abstract Greet: string -> string

type Greeter() =
    interface IGreeter with
        member _.Greet name = %s{body}
"""

        let changed =
            changedBetween (analyze (mk "\"hi \" + name")) (analyze (mk "\"hello \" + name"))

        test <@ not (changed |> Set.contains (Type, "M.Greeter")) @>
        test <@ not (changed |> Set.contains (Type, "M.IGreeter")) @>
        test <@ not changed.IsEmpty @>

[<Collection("FCS-TypeHashSplit")>]
module ``Record field edits`` =

    [<Fact>]
    let ``changing a record field type changes the header`` () =
        let mk t =
            $"""
module M

type Config =
    {{ Host: string
      Port: %s{t} }}
"""

        let changed = changedBetween (analyze (mk "int")) (analyze (mk "int64"))
        test <@ changed = set [ Type, "M.Config" ] @>

    [<Fact>]
    let ``editing a record's member leaves the header alone`` () =
        let mk body =
            $"""
module M

type Config =
    {{ Host: string
      Port: int }}

    member this.Url = %s{body}
"""

        let changed =
            changedBetween (analyze (mk "this.Host")) (analyze (mk "this.Host + \":\" + string this.Port"))

        test <@ changed = set [ Property, "M.Config.Url" ] @>

[<Collection("FCS-TypeHashSplit")>]
module ``Selection through split hashes`` =

    let private source squarePayload =
        $"""
module M

type FactAttribute() =
    inherit System.Attribute()

type Shape =
    | Circle of radius: float
    | Square of side: %s{squarePayload}

let area (s: Shape) =
    match s with
    | Circle r -> r * r
    | Square s -> float s * float s

[<Fact>]
let circleOnly () =
    match Circle 1.0 with
    | Circle r -> ignore r
    | _ -> ()

[<Fact>]
let throughArea () = area (Circle 1.0) |> ignore

[<Fact>]
let squareOnly () =
    match Square 2.0 with
    | Square s -> ignore s
    | _ -> ()
"""

    [<Fact>]
    let ``an edit to one case's payload skips tests that only touch another case`` () =
        let chosen = selected (analyze (source "float")) (analyze (source "double"))

        test <@ not (chosen |> Set.contains "circleOnly") @>

    [<Fact>]
    let ``an edit to one case's payload selects tests reaching it directly or through a function over the type`` () =
        let chosen = selected (analyze (source "float")) (analyze (source "double"))

        test <@ chosen |> Set.contains "squareOnly" @>
        test <@ chosen |> Set.contains "throughArea" @>

    [<Fact>]
    let ``adding a case selects every test that matches on the type`` () =
        let before = analyze (source "float")

        let after =
            analyze (
                (source "float").Replace("    | Square of side: float\n", "    | Square of side: float\n    | Dot\n")
            )

        let chosen = selected before after

        test <@ chosen |> Set.contains "circleOnly" @>
        test <@ chosen |> Set.contains "squareOnly" @>
        test <@ chosen |> Set.contains "throughArea" @>

    [<Fact>]
    let ``a member edit reached only through its type still selects the type's consumers`` () =
        // FCS reports no symbol use at `let!`/`return`, so the test's only route to the
        // edited member is the builder value's type; the walk lifts the changed member
        // to that type.
        let mk bindBody =
            $"""
module M

type FactAttribute() =
    inherit System.Attribute()

type MyBuilder() =
    member _.Bind(x: int option, f: int -> int option) = %s{bindBody}
    member _.Return(x: int) = Some x

let my = MyBuilder()

[<Fact>]
let usesBuilder () =
    my {{
        let! a = Some 1
        return a + 1
    }}
    |> ignore
"""

        let chosen =
            selected (analyze (mk "Option.bind f x")) (analyze (mk "Option.bind f (Option.map id x)"))

        test <@ chosen |> Set.contains "usesBuilder" @>
