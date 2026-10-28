/// A scan is an observation, not a consumption.
///
/// `check` forces a from-disk scan of its own before it reads `test-scope`
/// (`Program.forceScanAndWait`), so `fshw scan` followed by `check` delivers TWO
/// boot-scan cohorts where `check` alone delivers one. If the first cohort used up the
/// edit's change signal and left nothing the second could select from, the check would
/// read a `none` scope over a tree no run spoke for and widen to the full suite to earn
/// one: the fastest path silently converting itself into the slowest. This drives both
/// sequences through the real plugin over a real FCS check and asserts that, for the
/// same edit, the check reads the same scope, the same seeds and the same executed
/// selection whether or not a scan preceded it.
module FsHotWatch.Tests.ScanThenCheckSelectionTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer

let private libSource =
    "module Lib\n\
     let validate (value: string) = value.Length > 0\n\
     let describe (value: string) = \"value: \" + value\n"

/// Only `validate`'s body moves, so only the class that calls it is affected.
let private editedLibSource =
    "module Lib\n\
     let validate (value: string) = value.Length > 1\n\
     let describe (value: string) = \"value: \" + value\n"

let private validateTestsSource =
    "module ValidateTests\n\
     type FactAttribute() =\n    inherit System.Attribute()\n\
     [<Fact>]\n\
     let validates () = assert (Lib.validate \"x\")\n"

let private describeTestsSource =
    "module DescribeTests\n\
     type FactAttribute() =\n    inherit System.Attribute()\n\
     [<Fact>]\n\
     let describes () = assert (Lib.describe \"x\" = \"value: x\")\n"

/// What the check reads once the edit has settled: its `test-scope` reply, and the
/// argument line of every test run launched after the edit.
type private CheckReading =
    { Scope: JsonElement
      RunsSinceEdit: string list }

/// The scope fields a check grades. Run ids are excluded: two daemons mint different ones
/// for the same selection.
let private gradedFields (scope: JsonElement) =
    let field name =
        match scope.TryGetProperty(name: string) with
        | true, value -> value.GetRawText()
        | false, _ -> "<absent>"

    [ "kind"
      "cause"
      "ranProjects"
      "totalProjects"
      "seeds"
      "seedCount"
      "notSelected" ]
    |> List.map (fun name -> name, field name)

/// One warm session: the tree indexed and verified, then `Lib` edited, then
/// `scansBeforeCheck` `fshw scan` cohorts, then the check's own forced scan.
let private readCheckAfterEdit (scansBeforeCheck: int) : CheckReading =
    let root = isolatedRoot ()
    // The runner, its capture and the index live outside the hashed tree, so writing them
    // cannot move the tree a receipt was earned over.
    let outside = isolatedRoot ()
    let capture = Path.Combine(outside, "ran")
    let runner = Path.Combine(outside, "runner.sh")
    File.WriteAllText(runner, $"#!/bin/sh\necho \"$@\" >> '{capture}'\nexit 0\n")

    // Under a discovery root: the receipt's input tree hashes only `src/` and `tests/`
    // (plus tool-known root files), so an edit to a root-level script would leave the
    // tree unmoved and the full baseline run would keep grading the check.
    let sources = Directory.CreateDirectory(Path.Combine(root, "src")).FullName
    let libFile = Path.Combine(sources, "Lib.fsx")
    let validateTestsFile = Path.Combine(sources, "ValidateTests.fsx")
    let describeTestsFile = Path.Combine(sources, "DescribeTests.fsx")
    File.WriteAllText(libFile, libSource)
    File.WriteAllText(validateTestsFile, validateTestsSource)
    File.WriteAllText(describeTestsFile, describeTestsSource)
    let files = [ libFile; validateTestsFile; describeTestsFile ]

    // The project identity FCS gives the registered script: `Lib.fsx` → `Lib`.
    let project = "Lib"

    // A valid full-suite baseline, so no rule outside the one under test widens the run.
    seedBaseline root [ project ]

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

    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let host = createModelHost checker root
    host.RegisterHandler(create (Path.Combine(outside, "tp.db")) root (Some configs) None None None None [])

    let libOptions =
        getScriptOptions checker libFile libSource |> Async.RunSynchronously

    pipeline.RegisterProject(
        libFile,
        { libOptions with
            SourceFiles = List.toArray files }
    )

    let mutable generation = 0L

    // What a forced scan delivers: a FileChecked for every registered file read from
    // disk, then the BootScan seal over all of them, each cohort one generation on.
    let scanCohort () =
        for file in files do
            match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
            | Some result -> emitFileAndQuiesce host result
            | None -> failwith $"CheckFile returned None for %s{file}"

        generation <- generation + 1L

        host.EmitBatchChecked
            { fakeBatchChecked files with
                Generation = generation }

        waitForQuiescent host 20000

    // The session's build, then the cohort that indexes and verifies the unedited tree.
    emitBuildAndWaitTerminal host
    scanCohort ()

    if File.Exists capture then
        File.Delete capture

    File.WriteAllText(libFile, editedLibSource)

    for _ in 1..scansBeforeCheck do
        scanCohort ()

    // The check: its own forced scan, then the scope it grades.
    scanCohort ()

    let scope =
        match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
        | Some reply -> (JsonDocument.Parse reply).RootElement.Clone()
        | None -> failwith "test-scope returned nothing"

    let runs =
        if File.Exists capture then
            File.ReadAllLines capture |> List.ofArray
        else
            []

    { Scope = scope; RunsSinceEdit = runs }

[<Fact(Timeout = 120000)>]
let ``a check alone selects only the edited symbol's tests`` () =
    let alone = readCheckAfterEdit 0

    // Guard for the comparison below: the edit really selects a strict subset, so a
    // widened run would differ from it.
    test <@ alone.Scope.GetProperty("kind").GetString() = "filtered" @>
    test <@ alone.RunsSinceEdit = [ "--filter-class ValidateTests" ] @>

[<Fact(Timeout = 120000)>]
let ``a scan before a check leaves the check the selection it would have made alone`` () =
    let alone = readCheckAfterEdit 0
    let afterScan = readCheckAfterEdit 1

    test <@ gradedFields afterScan.Scope = gradedFields alone.Scope @>
    test <@ afterScan.Scope.GetProperty("kind").GetString() = "filtered" @>
    test <@ afterScan.RunsSinceEdit = alone.RunsSinceEdit @>

[<Fact(Timeout = 120000)>]
let ``repeated scans before a check neither re-run nor widen the selection`` () =
    let alone = readCheckAfterEdit 0
    let afterScans = readCheckAfterEdit 3

    test <@ gradedFields afterScans.Scope = gradedFields alone.Scope @>
    test <@ afterScans.RunsSinceEdit = alone.RunsSinceEdit @>
