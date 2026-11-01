/// Characterisation of impact selection: which test classes the plugin runs, through the
/// real BuildCompleted → check → run flow with real FCS checks, for a fixed sequence of
/// edits. It pins what is selected today so a refactor of the plugin cannot change it
/// unnoticed: the expected list below is the selection as measured before the refactor,
/// and any difference fails here, naming the step.
module FsHotWatch.Tests.SelectionCharacterisationTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer

/// The library and its tests in one script. `f`, `g` and `h` are each reached by a known
/// subset of the test modules; each step edits one of them.
let private source (f: string) (g: string) (h: string) (extra: string) =
    $"module Sel\n\
      type FactAttribute() =\n    inherit System.Attribute()\n\
      let f x = %s{f}\n\
      let g x = %s{g}\n\
      let h x = %s{h}\n\
      %s{extra}\
      module ATests =\n    [<Fact>]\n    let usesF () = f 1 |> ignore\n\
      module BTests =\n    [<Fact>]\n    let usesG () = g 1 |> ignore\n\
      module CTests =\n    [<Fact>]\n    let usesFAndH () = f (h 1) |> ignore\n\
      module DTests =\n    [<Fact>]\n    let usesNothing () = ()\n"

/// The steps: a name, whether BTests fails in the run it causes, and the source after it.
let private steps =
    [ "baseline", false, source "x + 1" "x * 2" "x - 1" ""
      "edit f", false, source "x + 2" "x * 2" "x - 1" ""
      "edit g", false, source "x + 2" "x * 3" "x - 1" ""
      "edit h", false, source "x + 2" "x * 3" "x - 2" ""
      "comment only", false, source "x + 2" "x * 3" "x - 2" "// a comment\n"
      "add unused", false, source "x + 2" "x * 3" "x - 2" "// a comment\nlet unused x = x\n"
      "no change", false, source "x + 2" "x * 3" "x - 2" "// a comment\nlet unused x = x\n"
      "edit f and g", false, source "x + 4" "x * 5" "x - 2" "// a comment\nlet unused x = x\n"
      "edit g, BTests red", true, source "x + 4" "x * 6" "x - 2" "// a comment\nlet unused x = x\n"
      "edit h while BTests red", true, source "x + 4" "x * 6" "x - 3" "// a comment\nlet unused x = x\n"
      "edit f, BTests green again", false, source "x + 5" "x * 6" "x - 3" "// a comment\nlet unused x = x\n"
      "edit h after the red cleared", false, source "x + 5" "x * 6" "x - 4" "// a comment\nlet unused x = x\n" ]

/// What each step ran: the runner's argument lines, sorted, or `<none>`.
let private observe () : (string * string) list =
    let mutable observed = []

    withTempDir "tp-select-char" (fun tmpDir ->
        let project = "Sel"
        seedBaseline tmpDir [ project ]

        let capture = Path.Combine(tmpDir, "ran")
        let runner = Path.Combine(tmpDir, "runner.sh")
        let failFlag = Path.Combine(tmpDir, "fail-b")

        File.WriteAllText(
            runner,
            $"#!/bin/sh\necho \"$@\" >> '{capture}'\nif [ -f '{failFlag}' ]; then echo 'failed Sel+BTests.usesG (1ms)'; exit 1; fi\nexit 0\n"
        )

        let configs =
            [ { Project = project
                Command = "sh"
                Args = runner
                Group = "default"
                Environment = []
                FilterTemplate = Some "--filter-class {classes}"
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect
                Starvation = None
                CheckSkip = None
                NameHungTests = false
                Weight = 1.0
                AlwaysClasses = [] } ]

        let host = createModelHost sharedChecker.Value tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some configs) None None None None [])

        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let filePath = Path.Combine(tmpDir, "Sel.fsx")

        let ran () =
            if File.Exists capture then
                let lines = File.ReadAllLines capture |> Array.sort |> String.concat " | "
                File.Delete capture
                lines
            else
                "<none>"

        // The warm session: one build earns the session baseline before any edit.
        emitBuildAndWaitTerminal host
        ran () |> ignore

        for name, failB, text in steps do
            if failB then
                File.WriteAllText(failFlag, "")
            elif File.Exists failFlag then
                File.Delete failFlag

            File.WriteAllText(filePath, text)
            let options = getScriptOptions checker filePath text |> Async.RunSynchronously
            pipeline.RegisterProject(filePath, options)

            let result =
                pipeline.CheckFile(AbsFilePath.create filePath)
                |> Async.RunSynchronously
                |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

            emitFileAndQuiesce host result
            emitBatchAndQuiesce host [ AbsFilePath.value result.File ]
            emitBuildAndWaitTerminal host
            observed <- (name, ran ()) :: observed)

    List.rev observed

[<Fact(Timeout = 180000)>]
let ``impact selection runs the same classes for each step of a fixed edit sequence`` () =
    let expected =
        [ "baseline", "--filter-class Sel+ATests Sel+BTests Sel+CTests Sel+DTests"
          "edit f", "--filter-class Sel+ATests Sel+CTests"
          "edit g", "--filter-class Sel+BTests"
          "edit h", "--filter-class Sel+CTests"
          "comment only", "<none>"
          "add unused", "<none>"
          "no change", "<none>"
          "edit f and g", "--filter-class Sel+ATests Sel+BTests Sel+CTests"
          // A red run is retried once; the red class is then quarantined into every run.
          "edit g, BTests red", "--filter-class Sel+BTests | --filter-class Sel+BTests"
          "edit h while BTests red", "--filter-class Sel+BTests Sel+CTests | --filter-class Sel+BTests Sel+CTests"
          "edit f, BTests green again", "--filter-class Sel+ATests Sel+BTests Sel+CTests | --filter-class Sel+BTests"
          "edit h after the red cleared", "--filter-class Sel+BTests | --filter-class Sel+CTests Sel+BTests" ]

    test <@ observe () = expected @>
