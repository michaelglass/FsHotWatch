/// AUTOMATION-1900 shadow, blind-spot fields: two things the per-file symbol-use comparison
/// cannot see, logged beside every additive-model judgement so the shadow week can weigh them.
///   * DROPPED EDGES: a use the analyser could not attach to an enclosing symbol (a top-level
///     `do`, a bare module-level expression) is counted (`AnalysisDiagnostics.DroppedEdges`)
///     and recorded nowhere, so a rebinding there leaves the use set unchanged.
///   * OVERLOADS: the analyser names every overload of one entity by its member name, so a
///     rebinding between two overloads of that entity leaves the use set unchanged.
/// Both are LOG FIELDS only: they never refuse and never change a selection.
module FsHotWatch.Tests.AdditiveBlindSpotTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.TestPrune.AdditiveModelChange
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

/// Each file of a one-project fixture under `root`, checked by the real compiler in this
/// order and analysed as the plugin does, by file name.
let private analyse (root: string) (sources: (string * string) list) : Map<string, AnalysisResult> =
    let dir = Path.Combine(root, "src", "P")
    Directory.CreateDirectory dir |> ignore

    let paths =
        sources
        |> List.map (fun (name, text) ->
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, text)
            name, path)

    let checker = sharedChecker.Value
    let first = snd (List.head paths)

    let options, _ =
        scriptProjectOptions (checker, first, FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText first))
        |> Async.RunSynchronously

    let options =
        { options with
            ProjectFileName = Path.Combine(dir, "P.fsproj")
            SourceFiles = paths |> List.map snd |> List.toArray }

    paths
    |> List.map (fun (name, path) ->
        let text = File.ReadAllText path

        let parse, answer =
            checker.ParseAndCheckFileInProject(path, 0, FSharp.Compiler.Text.SourceText.ofString text, options)
            |> Async.RunSynchronously

        match answer with
        | FSharpCheckFileAnswer.Succeeded check ->
            test
                <@
                    check.Diagnostics
                    |> Array.forall (fun d -> d.Severity <> FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                @>

            match analyzeSourceFromResults path text parse check "P" with
            | Ok analysis -> name, analysis
            | Error why -> failwith $"analysis of %s{name} failed: %s{why}"
        | FSharpCheckFileAnswer.Aborted -> failwith $"the check of %s{name} aborted")
    |> Map.ofList

let private lib =
    "Lib.fs", "namespace P\ntype Counter() =\n    member _.Value = 1\nmodule Helpers =\n    let value () = 1\n"

let private overloadedLib =
    "Lib.fs",
    "namespace P\ntype Printer =\n    static member Show(x: obj) = \"obj\"\n    static member Show(x: int) = \"int\"\n"

let private plainConsumer =
    "Consumer.fs", "module P.Consumer\nlet run (c: P.Counter) = P.Helpers.value () + c.Value\n"

/// A top-level `do` whose uses have no enclosing symbol to attach to.
let private consumerWithDo =
    "Consumer.fs",
    "module P.Consumer\nlet run (c: P.Counter) = c.Value\ndo printfn \"%d\" (P.Helpers.value ())\ndo ignore (P.Helpers.value ())\n"

let private overloadConsumer =
    "Consumer.fs", "module P.Consumer\nlet run () = P.Printer.Show 3\n"

let private reliedOnBoth = [ "Lib.fs"; "Consumer.fs" ]

[<Fact(Timeout = 180000)>]
let ``REAL COMPILER: a top-level do's uses are dropped edges, counted per relied-on file and named by the worst file``
    ()
    =
    withTempDir "blind-dropped" (fun dir ->
        let analyses = analyse dir [ lib; consumerWithDo ]
        let dropped = analyses["Consumer.fs"].Diagnostics.DroppedEdges

        test <@ dropped > 0 @>

        test
            <@
                BlindSpots.ofAnalyses reliedOnBoth analyses = { Dropped = DroppedEdges(dropped, "Consumer.fs", dropped)
                                                                Overloads = NoOverloads }
            @>)

[<Fact(Timeout = 180000)>]
let ``REAL COMPILER: a relied-on use of an overloaded member is multi-overload, naming it`` () =
    withTempDir "blind-overload" (fun dir ->
        let analyses = analyse dir [ overloadedLib; overloadConsumer ]

        // How it is detected: both overloads are definitions of one full name in their file's
        // analysis, which the index would merge into one symbol.
        test
            <@
                analyses["Lib.fs"].Symbols
                |> List.filter (fun s -> s.FullName = "P.Printer.Show")
                |> List.length = 2
            @>

        test <@ (BlindSpots.ofAnalyses reliedOnBoth analyses).Overloads = Overloaded "P.Printer.Show" @>)

[<Fact(Timeout = 180000)>]
let ``REAL COMPILER: a plain project has no dropped edges and no overloads`` () =
    withTempDir "blind-plain" (fun dir ->
        let analyses = analyse dir [ lib; plainConsumer ]

        test
            <@
                BlindSpots.ofAnalyses reliedOnBoth analyses = { Dropped = NoDroppedEdges
                                                                Overloads = NoOverloads }
            @>)

[<Fact(Timeout = 180000)>]
let ``REAL COMPILER: an overloaded member no relied-on file uses is not reported`` () =
    withTempDir "blind-unused-overload" (fun dir ->
        let analyses =
            analyse dir [ overloadedLib; "Consumer.fs", "module P.Consumer\nlet run () = 1\n" ]

        test <@ (BlindSpots.ofAnalyses reliedOnBoth analyses).Overloads = NoOverloads @>)

// --- Pure parts ------------------------------------------------------------------------------

let private withDropped (count: int) =
    { AnalysisResult.Create([], [], []) with
        Diagnostics =
            { AnalysisDiagnostics.Zero with
                DroppedEdges = count } }

[<Fact>]
let ``only relied-on files count, a file with no analysis counts nothing, and the total sums them`` () =
    let analyses =
        Map.ofList [ "a.fs", withDropped 2; "b.fs", withDropped 5; "outside.fs", withDropped 100 ]

    test <@ (BlindSpots.ofAnalyses [ "a.fs"; "b.fs"; "missing.fs" ] analyses).Dropped = DroppedEdges(7, "b.fs", 5) @>

[<Fact>]
let ``the fields are appended as one fixed-order suffix`` () =
    test
        <@
            BlindSpots.describe
                { Dropped = DroppedEdges(12, "src/A/B.fs", 5)
                  Overloads = Overloaded "P.Printer.Show" } = "dropped-edges=12 dropped-max=5 file=src/A/B.fs multi-overload=yes entity=P.Printer.Show"
        @>

    test
        <@
            BlindSpots.describe
                { Dropped = NoDroppedEdges
                  Overloads = NoOverloads } = "dropped-edges=0 dropped-max=0 multi-overload=no"
        @>

let private shape (items: string list) : ProjectShape =
    { CompileItems = items
      Defines = []
      References = "r"
      Options = "o" }

[<Fact>]
let ``the relied-on files are the pre-existing files outside the diff of the changed projects and their dependents``
    ()
    =
    let change: Change =
        { Before =
            Map.ofList
                [ "A", shape [ "a1.fs"; "a2.fs" ]
                  "T", shape [ "t.fs" ]
                  "Other", shape [ "o.fs" ] ]
          After =
            Map.ofList
                [ "A", shape [ "a1.fs"; "new.fs"; "a2.fs" ]
                  "T", shape [ "t.fs" ]
                  "Other", shape [ "o.fs" ] ]
          Dependents = Map.ofList [ "A", set [ "T" ]; "T", Set.empty; "Other", Set.empty ]
          UsesAtT = Map.empty
          UsesNow = Map.empty
          Diff = set [ "new.fs"; "a2.fs" ] }

    test <@ BlindSpots.reliedOn change = [ "a1.fs"; "t.fs" ] @>
    test <@ List.isEmpty (BlindSpots.reliedOn { change with After = change.Before }) @>
