/// A brand-new source file must never be able to run zero tests.
///
/// `FileFreshness.trustStoredRows` used to answer `Unknown, NoRows` with `NoDiff` — "this
/// file contributes no changed symbols" — on the premise that the pair only arises in the
/// ordinary cold scan, whose full-suite run covers it anyway. A file created while the
/// daemon is warm arrives at the SAME pair (no sidecar record, no rows) with no cold
/// scan behind it: the full-suite watermark is valid and the session already has a
/// baseline, so nothing else widened the run and it took the zero-affected skip. This
/// drives that exact situation through the real plugin and a real FCS check.
module FsHotWatch.Tests.NewFileSelectionTests

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

/// A real, FCS-clean check of `fileName` in `tmpDir`, with the file left on disk.
let private checkForReal (tmpDir: string) (fileName: string) (source: string) : FileCheckResult =
    let checker = sharedChecker.Value
    let pipeline = CheckPipeline(checker)
    let filePath = Path.Combine(tmpDir, fileName)
    File.WriteAllText(filePath, source)
    let projOptions = getScriptOptions checker filePath source |> Async.RunSynchronously
    pipeline.RegisterProject(filePath, projOptions)

    pipeline.CheckFile(AbsFilePath.create filePath)
    |> Async.RunSynchronously
    |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

/// A new test file: one `[<Fact>]` the analyser recognises by attribute display name.
/// The attribute is declared locally so the script needs no package reference.
let private newTestSource =
    "module NewTests\n\
     type FactAttribute() =\n    inherit System.Attribute()\n\
     [<Fact>]\n\
     let brandNewTest () = ()\n"

[<Fact(Timeout = 90000)>]
let ``a file created mid-session with a valid full-suite watermark still runs its tests`` () =
    withTempDir "tp-new-file" (fun tmpDir ->
        // The project identity FCS gives a script: `NewTests.fsx.fsproj` → `NewTests`.
        let project = "NewTests"

        // A valid watermark over the only configured project: no cold-scan full suite
        // is owed, which is what takes the "the full-suite run covers it" premise away.
        seedBaseline tmpDir [ project ]

        // The runner appends the arguments it was launched with, so the capture names
        // WHAT ran — a run widened by something else would not name the new class.
        let capture = Path.Combine(tmpDir, "ran")
        let runner = Path.Combine(tmpDir, "runner.sh")
        File.WriteAllText(runner, $"#!/bin/sh\necho \"$@\" >> '{capture}'\nexit 0\n")

        let configs =
            [ { Project = project
                Command = "sh"
                Args = runner
                Group = "default"
                Environment = []
                FilterTemplate = Some "--filter-class {classes}"
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = createModelHost sharedChecker.Value tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some configs) None None None None [])

        // The warm session: one build completes, earning the session baseline.
        emitBuildAndWaitTerminal host

        // Guard: the harness really runs tests, so an absent capture below is a skip.
        test <@ File.Exists capture @>
        File.Delete capture

        // Now the brand-new file appears: no sidecar record, no rows in the index.
        let result = checkForReal tmpDir "NewTests.fsx" newTestSource

        // Guard: the premise is exactly the `Unknown, NoRows` pair.
        test <@ FileFreshness.classify "NewTests.fsx" (FileFreshness.load tmpDir) = FileFreshness.Unknown @>

        emitFileAndQuiesce host result
        emitBatchAndQuiesce host [ AbsFilePath.value result.File ]
        emitBuildAndWaitTerminal host

        // THE ASSERTION: a run executed the new file's test class, by name. An absent
        // capture is the zero-affected skip — a green that executed nothing from it.
        test <@ File.Exists capture @>
        test <@ (File.ReadAllText capture).Contains "--filter-class NewTests" @>)
