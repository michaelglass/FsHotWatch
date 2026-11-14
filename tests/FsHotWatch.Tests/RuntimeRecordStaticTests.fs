/// AUTOMATION-1686: `tests.projects[].runtimeSelection: "runtime/record-static"`, through the
/// plugin.
///
/// The binding condition (ADR-070): recording must not change what runs or the verdict. The
/// same tree and the same edit, run with the mode on and off, launch the same runner
/// invocations and end in the same verdict; only the store differs.
module FsHotWatch.Tests.RuntimeRecordStaticTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// What one daemon observably did from the edit on.
type private Observed =
    {
        /// Every runner invocation, with the per-run paths taken out: what was asked to run.
        Invocations: string list
        /// Each completed run's per-project verdict and what it verified.
        Completions: (Map<string, ProjectVerdict> * RunVerification) list
        /// test-prune's final status: completed or failed.
        Status: string
        /// The `Selected by:` lines the launches logged.
        SelectedBy: string list
        /// The lines the edit added to the record store, in order.
        Records: string list
        Warnings: string list
        RecordLines: string list
    }

/// Writes the coverage it is asked for, logs what it was asked to run, and writes a CTRF
/// report where fshw asked. With `broken` beside it, one test fails.
let private runnerScript (coverage: string) =
    $"""#!/bin/sh
set -eu
here=$(dirname "$0")
name=''; dir=''; asked=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --coverage-output) cp "%s{coverage}" "$2"; shift 2 ;;
    --report-ctrf-filename) name="$2"; shift 2 ;;
    --results-directory) dir="$2"; shift 2 ;;
    *) asked="$asked $1"; shift ;;
  esac
done
printf 'run:%%s\n' "$asked" >> "$here/invocations.log"
if [ -f "$here/broken" ]; then
  printf '{{"results":{{"summary":{{"tests":2,"passed":1,"failed":1,"pending":0,"skipped":0,"other":0}},"tests":[{{"name":"Ns.Runtime.t1","status":"failed","suite":["asm","Ns.Runtime","t1"]}},{{"name":"Ns.Runtime.t2","status":"passed","suite":["asm","Ns.Runtime","t2"]}}]}}}}' > "$dir/$name"
  exit 1
fi
printf '{{"results":{{"summary":{{"tests":2,"passed":2,"failed":0,"pending":0,"skipped":0,"other":0}},"tests":[{{"name":"Ns.Runtime.t1","status":"passed","suite":["asm","Ns.Runtime","t1"]}},{{"name":"Ns.Runtime.t2","status":"passed","suite":["asm","Ns.Runtime","t2"]}}]}}}}' > "$dir/$name"
"""

/// How the record store is set up before the daemon starts.
type private Store =
    | Writable
    /// A file stands where the store's directory must be, so nothing can be written there.
    | Blocked

let private stripTimestamp (line: string) =
    // `[test-prune] 2026-11-14T06:01:13.913Z message`
    match line.IndexOf("Z ", StringComparison.Ordinal) with
    | i when i > 0 -> line.Substring(i + 2)
    | _ -> line

/// A one-project daemon, a baseline run, then an edit to a file no test reaches statically
/// but whose runtime coverage reaches the project, run under `mode`. With `breaks`, the edit
/// breaks a test of that project.
let private runtimeOnlyEdit (mode: SelectionMode) (breaks: bool) (store: Store) : Observed =
    withTempDir "tp-record-static" (fun tmpDir ->
        let lines = System.Collections.Concurrent.ConcurrentQueue<string>()

        let sink: FsHotWatch.Logging.LogSink =
            { Write = lines.Enqueue
              Level = FsHotWatch.Logging.LogLevel.Info
              File = None }

        use _ = FsHotWatch.Logging.installSink sink

        let storePath = StaticRecordStore.defaultStorePath tmpDir

        match store with
        | Writable -> ()
        | Blocked ->
            let parent = Path.GetDirectoryName storePath
            Directory.CreateDirectory(Path.GetDirectoryName parent) |> ignore
            File.WriteAllText(parent, "")

        let sourceFile = Path.Combine(tmpDir, "src", "RuntimeOnly.fs")
        let projectFile = Path.Combine(tmpDir, "RuntimeProject.fsproj")
        Directory.CreateDirectory(Path.GetDirectoryName sourceFile) |> ignore
        File.WriteAllText(sourceFile, "namespace RuntimeOnly\n")

        let coverage = Path.Combine(tmpDir, "runtime-project.cobertura.xml")

        File.WriteAllText(
            coverage,
            $"""<?xml version="1.0"?><coverage><packages><package name="RuntimeProject"><classes><class filename="%s{sourceFile}" name="RuntimeOnly"><lines><line number="1" hits="1" /></lines></class></classes></package></packages></coverage>"""
        )

        let runner = Path.Combine(tmpDir, "runtime-project-runner.sh")
        File.WriteAllText(runner, runnerScript coverage)

        let config =
            { Project = "RuntimeProject"
              Command = "sh"
              Args = runner
              Group = "default"
              Environment = []
              FilterTemplate = Some "--filter-class {classes}"
              ClassJoin = " "
              TimeoutSec = Some 10
              ReportVerificationFormat = Ctrf
              Starvation = None
              CheckSkip = None
              NameHungTests = false
              Weight = 1.0
              AlwaysClasses = [] }

        let coveragePaths _ =
            Some
                { Baseline = Path.Combine(tmpDir, BaselineName)
                  Partial = Path.Combine(tmpDir, PartialName)
                  Cobertura = Path.Combine(tmpDir, CoberturaName)
                  IncludeInRatchet = false
                  ArgsTemplate = "--coverage-output {output}"
                  RuntimeSelection = mode }

        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath
        db.ReplaceRuntimeCoverage("RuntimeProject", "baseline", [ "src/RuntimeOnly.fs" ])

        let host = createModelHost sharedChecker.Value tmpDir
        let completions, recorder = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let handler =
            create dbPath tmpDir (Some [ config ]) None None None (Some coveragePaths) []

        host.RegisterHandler(handler)

        emitBuildAndWaitTerminal host
        lines.Clear()

        let readStore () =
            if File.Exists storePath then
                File.ReadAllLines storePath |> List.ofArray
            else
                []

        let recordsAtEdit = readStore () |> List.length
        let completedBefore = completions () |> List.length
        File.Delete(Path.Combine(tmpDir, "invocations.log"))

        let source = "namespace RuntimeOnly\n\n// edited\n"
        File.WriteAllText(sourceFile, source)

        if breaks then
            File.WriteAllText(Path.Combine(tmpDir, "broken"), "")

        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let projectOptions =
            getScriptOptions checker sourceFile source
            |> Async.RunSynchronously
            |> fun options ->
                { options with
                    ProjectFileName = projectFile
                    SourceFiles = [| sourceFile |] }

        pipeline.RegisterProject(sourceFile, projectOptions)

        let checkResult =
            pipeline.CheckFile(AbsFilePath.create sourceFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        emitFileAndQuiesce host checkResult
        emitBatchAndQuiesce host [ sourceFile ]
        emitBuildAndWaitTerminal host

        let logged = lines |> Seq.map stripTimestamp |> List.ofSeq

        let invocations =
            let path = Path.Combine(tmpDir, "invocations.log")

            if File.Exists path then
                File.ReadAllLines path |> List.ofArray
            else
                []

        { Invocations = invocations
          Completions =
            completions ()
            |> List.skip completedBefore
            |> List.map (fun c -> c.Results |> Map.map (fun _ r -> TestResult.verdict r), c.Verification)
          Status =
            match host.GetStatus "test-prune" with
            | Some(Completed _) -> "completed"
            | Some(Failed _) -> "failed"
            | other -> $"%A{other}"
          SelectedBy = logged |> List.filter (fun line -> line.Contains "Selected by:")
          Records = readStore () |> List.skip recordsAtEdit
          Warnings = logged |> List.filter (fun line -> line.Contains "could not file the record")
          RecordLines =
            logged
            |> List.filter (fun line -> line.StartsWith(StaticRecordStore.LogPrefix + "{")) })

/// What two runs must agree on: everything but the store and its log lines.
let private run (o: Observed) =
    o.Invocations, o.Completions, o.Status, o.SelectedBy

let private judgementOf (line: string) =
    Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("judgement").GetProperty("kind").GetString()

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``recording static changes neither what runs nor the verdict - the same edit with the mode on and off``
    (breaks: bool)
    =
    let off = runtimeOnlyEdit SelectionMode.RuntimeDecides breaks Writable
    let on = runtimeOnlyEdit SelectionMode.RuntimeDecidesStaticRecorded breaks Writable

    // Positive control: the edit launched a run, selected by runtime coverage.
    test <@ not (List.isEmpty off.Invocations) @>
    test <@ off.SelectedBy |> List.exists (fun line -> line.Contains "runtime-coverage") @>
    test <@ off.Status = (if breaks then "failed" else "completed") @>

    test <@ run on = run off @>

    // Only the store differs: the mode on filed a record of the run, the mode off none.
    test <@ List.isEmpty off.Records && List.isEmpty off.RecordLines @>
    test <@ not (List.isEmpty on.Records) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a red only runtime coverage selected is filed as missed by static`` () =
    let on = runtimeOnlyEdit SelectionMode.RuntimeDecidesStaticRecorded true Writable

    // The edit's run, then the run that retries its red. The retry is caught by static
    // selection: an outstanding failure is a static cause, so it names the failed class.
    test <@ on.Records |> List.map judgementOf = [ "missed-by-static"; "caught-by-static" ] @>

    // In the edit's run static selection alone would have launched nothing.
    let root = Text.Json.JsonDocument.Parse(List.head on.Records).RootElement
    let staticProjects = root.GetProperty("static").EnumerateObject() |> Seq.length

    let ranScope =
        root.GetProperty("ran").GetProperty("RuntimeProject").GetProperty("scope").GetString()

    let outside =
        root.GetProperty("judgement").GetProperty("outsideFailed").EnumerateArray()
        |> Seq.map (fun f -> f.GetProperty("test").GetProperty("name").GetString())
        |> List.ofSeq

    test <@ staticProjects = 0 @>
    test <@ ranScope = "whole" @>
    test <@ outside = [ "Ns.Runtime.t1" ] @>

    test
        <@
            on.RecordLines
            |> List.exists (fun line -> line.Contains "judgement: missed-by-static")
        @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an unwritable store leaves the run and its verdict as they were, with a warning and no record`` () =
    let off = runtimeOnlyEdit SelectionMode.RuntimeDecides true Writable

    let blocked =
        runtimeOnlyEdit SelectionMode.RuntimeDecidesStaticRecorded true Blocked

    test <@ run blocked = run off @>
    test <@ List.isEmpty blocked.Records @>
    // One warning for each record it could not file, and the log line still written.
    test <@ not (List.isEmpty blocked.Warnings) @>
    test <@ blocked.Warnings.Length = blocked.RecordLines.Length @>
