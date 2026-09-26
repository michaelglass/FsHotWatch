module TestPrune.Trace.Tests.JoinerTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Trace.Model
open TestPrune.Trace.Joiner

let private sym name kind file line =
    { FullName = name
      Kind = kind
      SourceFile = file
      LineStart = line
      LineEnd = line
      ContentHash = "h"
      IsExtern = false }

let private index (syms: SymbolInfo list) =
    { InFile = fun f -> syms |> List.filter (fun s -> s.SourceFile = f)
      Exists = fun n -> syms |> List.exists (fun s -> s.FullName = n)
      IsIndexedFile = fun f -> syms |> List.exists (fun s -> s.SourceFile = f)
      RepoRoot = "/r" }

let private row kind typ mem doc first =
    { Id = 0
      Kind = kind
      Assembly = "A"
      TypeName = typ
      Member = mem
      Document = doc
      FirstLine = first
      LastLine = first }

let private m = Some "/r/src/M.fs"
let private t = Some "/r/src/T.fs"

let private ix =
    index
        [ sym "N.M" Module "src/M.fs" 1
          sym "N.M.area" Function "src/M.fs" 5
          sym "N.M.fieldName" Value "src/M.fs" 10
          sym "N.M.helpText" Value "src/M.fs" 12
          sym "N.M.( +. )" Function "src/M.fs" 20
          sym "N.M.(|Big|Small|)" Function "src/M.fs" 22
          sym "N.M.``a b``" Function "src/M.fs" 24
          sym "N.M.shout" Function "src/M.fs" 26
          sym "N.M.outside" ExternRef "src/M.fs" 28
          sym "N.M.later" Function "src/M.fs" 40
          sym "N.Shape" Type "src/T.fs" 3
          sym "N.Shape.Circle" DuCase "src/T.fs" 4
          sym "N.Shape.Square" DuCase "src/T.fs" 5
          sym "N.Shape.Describe" Function "src/T.fs" 6
          sym "N.Dog" Type "src/T.fs" 9
          sym "N.Dog.Speak" Function "src/T.fs" 10
          sym "N.Dog.Name" Property "src/T.fs" 11
          sym "N.Cat" Type "src/T.fs" 13
          sym "N.Cat.Speak" Function "src/T.fs" 14
          sym "N.Greeter" Type "src/T.fs" 16
          sym "N.Greeter.Greet" Function "src/T.fs" 17
          sym "N.Box`1" Type "src/T.fs" 19
          sym "N.Shape" Module "src/T.fs" 21 ]

let private unions = set [ "N.Shape" ]
let private join = joinRow ix unions

[<Fact>]
let ``case and type rows join by name`` () =
    test <@ join (row UnionCase "N.Shape" "Circle" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ join (row TypeUse "N.Dog" "" None 0) = ToSymbol "N.Dog" @>
    test <@ join (row TypeUse "N.Box`1" "" None 0) = ToSymbol "N.Box`1" @>
    test <@ join (row UnionCase "N.Shape" "Triangle" None 0) = Unmapped "case-not-indexed" @>
    test <@ join (row TypeUse "N.Wolf" "" None 0) = Unmapped "type-not-indexed" @>

[<Fact>]
let ``union-generated members map to cases or are dropped`` () =
    test <@ join (row GeneratedMethod "N.Shape" "get_Tag" None 0) = Dropped @>
    test <@ join (row GeneratedMethod "N.Shape" "CompareTo" None 0) = Dropped @>
    test <@ join (row StaticCtor "N.Shape" ".cctor" None 0) = Dropped @>
    test <@ join (row GeneratedMethod "N.Shape" "NewCircle" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ join (row GeneratedMethod "N.Shape" "get_IsSquare" None 0) = ToSymbol "N.Shape.Square" @>
    test <@ join (row GeneratedMethod "N.Shape" "get_Square" None 0) = ToSymbol "N.Shape.Square" @>
    test <@ join (row GeneratedMethod "N.Shape+Circle" "get_radius" None 0) = ToSymbol "N.Shape.Circle" @>
    test <@ join (row GeneratedMethod "N.Shape+_Square" ".ctor" None 0) = ToSymbol "N.Shape.Square" @>

    test
        <@ join (row GeneratedMethod "N.Shape+Circle@DebugTypeProxy" "get_radius" None 0) = ToSymbol "N.Shape.Circle" @>

[<Fact>]
let ``a union member the user wrote is not plumbing`` () =
    // A user `override ToString` on a union is user code; only generated members drop.
    test <@ join (row UserMethod "N.Shape" "ToString" t 6) = ToSymbol "N.Shape.Describe" @>
    test <@ join (row UserMethod "N.Shape" "Describe" t 6) = ToSymbol "N.Shape.Describe" @>
    // A generated member whose name looks like a case accessor but names no case.
    test <@ join (row GeneratedMethod "N.Shape" "get_Area" None 0) = ToSymbol "N.Shape" @>
    test <@ join (row GeneratedMethod "N.Shape" "New" None 0) = ToSymbol "N.Shape" @>
    // A class `N.Dog` is not a union, so its `get_Tag` is not dropped.
    test <@ join (row GeneratedMethod "N.Dog" "get_Tag" None 0) = ToSymbol "N.Dog" @>

[<Fact>]
let ``a closure nested in a union type is not a case class`` () =
    test <@ join (row GeneratedMethod "N.Shape+Describe@7" "Invoke" t 7) = ToSymbol "N.Shape.Describe" @>
    test <@ join (row GeneratedMethod "N.Shape+Hexagon" "get_x" None 0) = ToSymbol "N.Shape" @>

[<Fact>]
let ``a closure joins to the binding it was written in`` () =
    test <@ join (row GeneratedMethod "N.M+area@6" "Invoke" m 6) = ToSymbol "N.M.area" @>
    test <@ join (row GeneratedMethod "N.M+area@6-3" "Invoke" m 30) = ToSymbol "N.M.area" @>
    test <@ join (row GeneratedMethod "N.M+area@6" ".ctor" m 0) = Dropped @>
    test <@ join (row StaticCtor "N.M+area@6" ".cctor" None 0) = Dropped @>
    test <@ join (row GeneratedMethod "N.M+area@6" "Invoke" None 0) = ToSymbol "N.M.area" @>
    // A closure with no binding name falls back to the line rule.
    test <@ join (row GeneratedMethod "N.M+@_instance" "Invoke" m 11) = ToSymbol "N.M.fieldName" @>

[<Fact>]
let ``same-file name match wins over the line rule when the index and binary drift`` () =
    // The getter's first line (13) is past `helpText` (12): the line rule alone
    // would pick the neighbour. The name picks the right one.
    test <@ join (row UserMethod "N.M" "get_fieldName" m 13) = ToSymbol "N.M.fieldName" @>
    // The binary's `later` starts at line 30 while the index has it at 40 (lines were
    // added since): no same-named symbol precedes, so the nearest same-named one wins,
    // not `N.M.shout` the line rule would pick.
    test <@ join (row UserMethod "N.M" "later" m 30) = ToSymbol "N.M.later" @>

[<Fact>]
let ``same-named members of two types resolve to the method's own type`` () =
    // `Cat.Speak`'s binary line sits in `Dog`'s span: nearness alone would pick `Dog.Speak`.
    test <@ join (row UserMethod "N.Cat" "Speak" t 10) = ToSymbol "N.Cat.Speak" @>
    test <@ join (row UserMethod "N.Dog" "Speak" t 15) = ToSymbol "N.Dog.Speak" @>

[<Fact>]
let ``F# compiled names match their source names`` () =
    test <@ join (row UserMethod "N.M" "op_PlusDot" m 20) = ToSymbol "N.M.( +. )" @>
    test <@ join (row UserMethod "N.M" "|Big|Small|" m 22) = ToSymbol "N.M.(|Big|Small|)" @>
    test <@ join (row UserMethod "N.M" "a b" m 24) = ToSymbol "N.M.``a b``" @>
    test <@ join (row UserMethod "N.Dog" "get_Name" t 11) = ToSymbol "N.Dog.Name" @>
    test <@ join (row UserMethod "N.Dog" "set_Name" t 11) = ToSymbol "N.Dog.Name" @>
    test <@ join (row UserMethod "N.Dog" ".ctor" t 9) = ToSymbol "N.Dog" @>
    test <@ join (row UserMethod "N.Greeter" "N.IGreeter.Greet" t 17) = ToSymbol "N.Greeter.Greet" @>

[<Fact>]
let ``the line rule, then the file, are the fallbacks`` () =
    test <@ join (row UserMethod "N.M" "helper" m 7) = ToSymbol "N.M.area" @>
    test <@ join (row UserMethod "N.Other" "f" (Some "/r/src/Unindexed.fs") 3) = ToFile "src/Unindexed.fs" @>
    // Nothing declared at or before line 0 (a method with no line), and no name match.
    test <@ join (row GeneratedMethod "N.M" "Invoke" m 0) = ToFile "src/M.fs" @>
    // An extern reference is not a declaration, so it never catches a line.
    test <@ join (row UserMethod "N.M" "helper" m 28) = ToSymbol "N.M.shout" @>

[<Fact>]
let ``a document outside the repository is unmapped`` () =
    test <@ join (row UserMethod "N.M" "f" (Some "/elsewhere/x.fs") 3) = Unmapped "outside-repo" @>

[<Fact>]
let ``without a document: member, then type, then enclosing module`` () =
    test <@ join (row GeneratedMethod "N.Dog" "Equals" None 0) = ToSymbol "N.Dog" @>
    test <@ join (row GeneratedMethod "N.Dog" "get_Name" None 0) = ToSymbol "N.Dog.Name" @>
    test <@ join (row GeneratedMethod "N.M+Inner" "get_x" None 0) = ToSymbol "N.M" @>
    test <@ join (row GeneratedMethod "N.M+Inner" "Invoke" None 0) = ToSymbol "N.M" @>
    test <@ join (row GeneratedMethod "Q.Nowhere" "f" None 0) = Unmapped "no-document" @>

[<Fact>]
let ``type candidates cover arity and module suffix, most specific first`` () =
    test <@ typeCandidates "N.M+AnswerState`1" = [ "N.M.AnswerState`1"; "N.M.AnswerState" ] @>
    test <@ typeCandidates "N.ShapeModule" = [ "N.ShapeModule"; "N.Shape" ] @>
    test <@ typeCandidates "N.Module" = [ "N.Module" ] @>
    test <@ join (row GeneratedMethod "N.ShapeModule" "helper" None 0) = ToSymbol "N.Shape" @>

[<Fact>]
let ``joinManifest indexes targets by probe id and finds union types itself`` () =
    let rows =
        [| { row GeneratedMethod "N.Shape" "get_Tag" None 0 with
               Id = 0 }
           { row GeneratedMethod "N.Shape" "NewCircle" None 0 with
               Id = 1 }
           { row UserMethod "N.M" "area" m 5 with
               Id = 3 }
           { row UnionCase "N.Other" "X" None 0 with
               Id = 4 }
           { row GeneratedMethod "N.Dog" "NewPuppy" None 0 with
               Id = 5 } |]

    let targets =
        joinManifest
            ix
            { Rows = rows
              Documents = Map.empty
              IdCount = 6 }

    // `N.Shape` is a union because its `NewCircle` names an indexed case, so `get_Tag`
    // drops; `N.Dog`'s `NewPuppy` names no case, so `N.Dog` stays a class.
    test
        <@
            targets = [| Dropped
                         ToSymbol "N.Shape.Circle"
                         Unmapped "no-row"
                         ToSymbol "N.M.area"
                         Unmapped "case-not-indexed"
                         ToSymbol "N.Dog" |]
        @>

[<Fact>]
let ``the woven fixture joins to the real index`` () =
    let dir, r = WeaverTests.weaveFx []

    try
        let ix =
            ofStore (TestPrune.Ports.toSymbolStore FixtureIndex.build.Value) Fixtures.fixtureRoot

        let targets = joinManifest ix r.Manifest

        let find typ mem =
            targets.[(r.Manifest.Rows |> Array.find (fun x -> x.TypeName = typ && x.Member = mem)).Id]

        test <@ find "FxLib.Logic" "area" = ToSymbol "FxLib.Logic.area" @>
        test <@ find "FxLib.Logic" "|Big|Small|" = ToSymbol "FxLib.Logic.(|Big|Small|)" @>
        test <@ find "FxLib.Values" "get_threshold" = ToSymbol "FxLib.Values.threshold" @>
        test <@ find "FxLib.Dog" "Speak" = ToSymbol "FxLib.Dog.Speak" @>
        test <@ find "FxLib.Cat" "Speak" = ToSymbol "FxLib.Cat.Speak" @>
        test <@ find "FxLib.Shape" "NewCircle" = ToSymbol "FxLib.Shape.Circle" @>
        test <@ find "FxLib.Dir" "get_IsNorth" = ToSymbol "FxLib.Dir.North" @>
        test <@ find "FxLib.Shape" "get_Tag" = Dropped @>

        let fxLib = r.Manifest.Rows |> Array.filter (fun x -> x.Assembly = "FxLib")

        let unmapped =
            fxLib
            |> Array.filter (fun x ->
                match targets.[x.Id] with
                | Unmapped _ -> true
                | _ -> false)

        // The spike's hit-level rate was 98.1 % on a large suite; the fixture must do no worse.
        test <@ float unmapped.Length / float fxLib.Length <= 0.02 @>
    finally
        WeaverTests.deleteScratch dir

[<Fact>]
let ``against a drifted index the name still finds each Logic function, where the line would not`` () =
    let dir, r = WeaverTests.weaveFx []

    try
        let store = TestPrune.Ports.toSymbolStore FixtureIndex.drifted.Value
        let ix = ofStore store Fixtures.fixtureRoot
        let targets = joinManifest ix r.Manifest

        let logic =
            r.Manifest.Rows
            |> Array.filter (fun x -> x.TypeName = "FxLib.Logic" && x.Document.IsSome)

        let logicSymbols =
            store.GetSymbolsInFile "src/FxLib/Logic.fs"
            |> List.filter (fun s -> s.Kind = Function)

        // What the line rule alone picks: the nearest symbol declared at or before the
        // binary's first line.
        let byLineOnly (x: ManifestRow) =
            logicSymbols
            |> List.filter (fun s -> s.LineStart <= x.FirstLine)
            |> List.sortByDescending (fun s -> s.LineStart)
            |> List.tryHead
            |> Option.map (fun s -> ToSymbol s.FullName)

        let expected (x: ManifestRow) =
            ToSymbol(
                "FxLib.Logic."
                + (if x.Member.StartsWith "|" then
                       $"(%s{x.Member})"
                   else
                       x.Member)
            )

        let wrongByLine = logic |> Array.filter (fun x -> byLineOnly x <> Some(expected x))

        test <@ logic.Length >= 15 @>
        test <@ logic |> Array.forall (fun x -> targets.[x.Id] = expected x) @>
        // The drift is real: the line rule alone mis-attributes most of these.
        test <@ wrongByLine.Length * 2 > logic.Length @>
    finally
        WeaverTests.deleteScratch dir
