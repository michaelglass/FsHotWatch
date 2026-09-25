module FxDriver.Program

open FxLib
open TestPrune.Trace.Recorder

let scope name (f: unit -> unit) =
    Scopes.Enter("T:" + name)

    try
        f ()
    finally
        Scopes.Exit()

[<EntryPoint>]
let main _ =
    let circle = ref Unchecked.defaultof<Shape>
    let circle2 = ref Unchecked.defaultof<Shape>
    let square = ref Unchecked.defaultof<Shape>
    let blue = ref Unchecked.defaultof<Color5>
    let east = ref Unchecked.defaultof<Dir>
    let err = ref Unchecked.defaultof<SResult>
    let dog = ref Unchecked.defaultof<Dog>
    let cat = ref Unchecked.defaultof<Cat>
    let pt = ref Unchecked.defaultof<Point>
    let tagCase = ref Unchecked.defaultof<Tricky>

    scope "warm" (fun () ->
        circle.Value <- Circle 2.0
        circle2.Value <- Circle 2.0
        square.Value <- Square 1.0
        blue.Value <- Blue 3
        east.Value <- East
        err.Value <- SErr "e"
        dog.Value <- Dog()
        cat.Value <- Cat()
        pt.Value <- { X = 1; Y = 2 }
        tagCase.Value <- Tag
        Values.threshold |> ignore)

    let sink = System.Collections.Generic.List<obj>()
    let run name (f: unit -> obj) = scope name (fun () -> sink.Add(f ()))
    run "caseA_area" (fun () -> box (Logic.area circle.Value))
    run "caseB_isCircleOnly" (fun () -> box (Logic.isCircleOnly square.Value))
    run "static_case_match" (fun () -> box (Logic.defaultArea ()))
    run "tag5" (fun () -> box (Logic.colorCode blue.Value))
    run "tag5_static" (fun () -> box (Logic.colorCode Values.prebuiltBlue))
    run "nullary" (fun () -> box (Logic.turn east.Value))
    run "struct" (fun () -> box (Logic.sval err.Value))
    run "tricky_tag" (fun () -> box (Logic.tricky tagCase.Value))
    run "modval" (fun () -> box (Logic.aboveThreshold 50))
    run "sameFileValue" (fun () -> box (Values.thresholdPlus 1))
    run "typetest_dog" (fun () -> box (Logic.describe (box dog.Value)))
    run "typetest_cat" (fun () -> box (Logic.describe (box cat.Value)))
    run "unboxgeneric" (fun () -> box (Logic.castDog (box dog.Value)))
    run "record" (fun () -> box (Logic.sumPoint pt.Value))

    run "testcode_match" (fun () ->
        box (
            match circle.Value with
            | Circle r -> r
            | Square s -> -s
        ))

    run "testcode_typetest" (fun () ->
        box (
            match box cat.Value with
            | :? Dog -> 1
            | _ -> 2
        ))

    run "constValue" (fun () -> box (Logic.addConst 1))
    run "literal" (fun () -> box (Logic.addLiteral 1))
    run "isCaseProp" (fun () -> box (Logic.isCircleProp square.Value))
    run "activePattern" (fun () -> box (Logic.classify pt.Value))
    run "recordEquality" (fun () -> box (Logic.samePoint pt.Value pt.Value))
    run "unionEquality" (fun () -> box (circle.Value = circle2.Value))

    run "fileRead" (fun () ->
        let root = System.Environment.GetEnvironmentVariable "TESTPRUNE_TRACE_REPO_ROOT"
        box (Logic.readRepoFile (System.IO.Path.Combine(root, "global.json"))))

    0
