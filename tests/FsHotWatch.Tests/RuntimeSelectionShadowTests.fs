/// AUTOMATION-1645: `tests.projects[].runtimeSelection: "shadow"`.
///
/// Runtime coverage keeps being collected for impact, and what it would add to a run (a
/// whole project per changed file it attributes) is computed and logged, one
/// machine-readable line per launch. It is never merged into the selection that runs, so
/// it is never an obligation for a completion to retire, and a run that took only the
/// static selection is never mistaken for a full suite.
module FsHotWatch.Tests.RuntimeSelectionShadowTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CompletionStages
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// ---------------------------------------------------------------------------
// Splitting what runtime coverage selected into what decides and what is shadowed.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``a shadow project's runtime selection is split out of what decides the run`` () =
    let selected =
        Map.ofList
            [ "src/A.fs", set [ "Shadowed"; "Decided" ]
              "src/B.fs", set [ "Shadowed" ]
              "src/C.fs", Set.empty ]

    let split = splitRuntimeSelection (fun project -> project = "Shadowed") selected

    test <@ split.Decided = Map.ofList [ "src/A.fs", set [ "Decided" ] ] @>
    test <@ split.Shadowed = Map.ofList [ "src/A.fs", set [ "Shadowed" ]; "src/B.fs", set [ "Shadowed" ] ] @>

[<Fact(Timeout = 10000)>]
let ``with no shadow project every project runtime coverage selected decides`` () =
    let selected = Map.ofList [ "src/A.fs", set [ "P"; "Q" ]; "src/B.fs", Set.empty ]

    let split = splitRuntimeSelection (fun _ -> false) selected

    // A file naming no project is no obligation on either side.
    test <@ split.Decided = Map.ofList [ "src/A.fs", set [ "P"; "Q" ] ] @>
    test <@ Map.isEmpty split.Shadowed @>

[<Fact(Timeout = 10000)>]
let ``a shadow project's missing baseline is reported as a widening it would make, not one it makes`` () =
    let selection =
        { ProjectsByFile = Map.ofList [ "src/A.fs", set [ "Shadowed"; "Decided" ] ]
          Widenings = [ "Shadowed", MissingBaseline; "Decided", MissingBaseline ] }

    let messages = ResizeArray<string>()
    reportRuntimeCoverageWidenings messages.Add (fun project -> project = "Shadowed") selection

    test <@ messages.Count = 2 @>

    test
        <@
            messages.[0].Contains "'Shadowed'"
            && messages.[0].Contains "shadow: would widen"
        @>

    test
        <@
            messages.[1].Contains "'Decided'"
            && messages.[1].Contains "; widening to every test"
        @>

// ---------------------------------------------------------------------------
// The log line: golden.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``the shadow line names the static selection, what runtime coverage would add, and the counts`` () =
    let staticSelection =
        Map.ofList
            [ "FsHotWatch.Tests", ProjectClasses(set [ "Ns.SdkChoiceTests"; "Ns.CliTests" ])
              "TestPrune.Tests", ProjectInFull
              "Bench.Tests", ProjectExcept(set [ "Ns.Slow" ]) ]

    let wouldAdd: RuntimeCoverageObligations =
        Map.ofList
            [ "src/FsHotWatch/SdkChoice.fs", Map.ofList [ "FsHotWatch.Tests", 3L; "TestPrune.Tests", 1L ]
              "src/FsHotWatch/Other.fs", Map.ofList [ "FsHotWatch.Tests", 1L ]
              // A file naming no project would add nothing, and is not counted.
              "src/FsHotWatch/Untraced.fs", Map.empty ]

    let line = RuntimeShadowLine.render (Some "tree-abc") staticSelection wouldAdd

    let expected =
        "runtime-selection shadow: "
        + """{"v":1,"tree":"tree-abc","""
        + """"static":{"projects":3,"wholeProjects":1,"classes":2,"byProject":{"""
        + """"Bench.Tests":{"scope":"all-but","classes":["Ns.Slow"]},"""
        + """"FsHotWatch.Tests":{"scope":"classes","classes":["Ns.CliTests","Ns.SdkChoiceTests"]},"""
        + """"TestPrune.Tests":{"scope":"whole","classes":[]}}},"""
        + """"wouldAdd":{"granularity":"project","projects":2,"files":2,"beyondStatic":["FsHotWatch.Tests"],"byProject":{"""
        + """"FsHotWatch.Tests":["src/FsHotWatch/Other.fs","src/FsHotWatch/SdkChoice.fs"],"""
        + """"TestPrune.Tests":["src/FsHotWatch/SdkChoice.fs"]}}}"""

    test <@ line = expected @>

[<Fact(Timeout = 10000)>]
let ``the shadow line is written for a launch that runtime coverage would add nothing to`` () =
    // A full run joined to a line that would have added nothing is still one counted run.
    let line = RuntimeShadowLine.render None Map.empty Map.empty

    let expected =
        "runtime-selection shadow: "
        + """{"v":1,"tree":null,"""
        + """"static":{"projects":0,"wholeProjects":0,"classes":0,"byProject":{}},"""
        + """"wouldAdd":{"granularity":"project","projects":0,"files":0,"beyondStatic":[],"byProject":{}}}"""

    test <@ line = expected @>

// ---------------------------------------------------------------------------
// Completion: no unretired obligation, no false full-suite baseline.
// ---------------------------------------------------------------------------

let private stubDebt: CompletionDebt =
    { CommitPending = commitPending
      CoveringOf = fun _ _ -> Set.empty
      DebtScope = fun _ owed symbol -> owed symbol
      IndexNamesOnlyRunnable = fun _ -> true
      OwedElsewhere = fun _ _ -> Set.empty
      RevisionOf = fun _ _ -> 0L }

let private wouldAddP: RuntimeCoverageObligations =
    Map.ofList [ "src/A.fs", Map.ofList [ "P", 1L ] ]

/// A static, class-filtered launch of P, carrying what runtime coverage would have added
/// (all of P) had P decided rather than shadowed.
let private shadowLaunch () =
    { filteredLaunch [ "P", [ "Ns.A" ] ] with
        ModelGeneration = Some 1L
        RuntimeShadow = wouldAddP }

let private settleShadowRun (state: TestPruneState) (result: TestResult) =
    let results = Map.ofList [ "P", result ]

    let completed: TestRunCompleted =
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    settleDebt
        { State = state
          Completed = completed
          Launch = shadowLaunch ()
          RunnableProjects = Set.ofList [ "P" ]
          CurrentInputTree = None
          CurrentModelGeneration = Some 1L
          DebtDuringFullRun = Map.empty
          OwedTo = fun _ -> Set.empty
          NoUnrunnableCoverers = lazy true
          Debt = stubDebt }

let private shadowState () =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ testConfigNamed "P" ]) None None None None []

    { handler.Init with
        Debt =
            { handler.Init.Debt with
                ShadowRuntime = wouldAddP } }

[<Fact(Timeout = 10000)>]
let ``a green shadow run cannot earn a full-suite baseline`` () =
    let settled =
        settleShadowRun (shadowState ()) (TestsPassed("passed", true, TimeSpan.FromSeconds 1.0))

    // Runtime selection would have run all of P; the static run ran one class of it.
    test <@ not settled.ExecutedFullSuite @>
    test <@ not settled.EarnsBaseline @>
    test <@ settled.Debt.Baseline = None @>
    // Nothing was ever owed for runtime coverage, so nothing is left unretired.
    test <@ Map.isEmpty settled.Debt.RuntimeObligations @>
    // The run it would have joined completed: the would-add is spent.
    test <@ Map.isEmpty settled.Debt.ShadowRuntime @>

[<Fact(Timeout = 10000)>]
let ``a red shadow run keeps what runtime coverage would add for the run that retries it`` () =
    let settled =
        settleShadowRun (shadowState ()) (TestsFailed("failed Ns.A.a1 (1ms)", true, TimeSpan.FromSeconds 1.0))

    test <@ settled.Debt.ShadowRuntime = wouldAddP @>
    test <@ Map.isEmpty settled.Debt.RuntimeObligations @>
    test <@ settled.Debt.Baseline = None @>

// ---------------------------------------------------------------------------
// The plugin: a shadow project is never force-run by its runtime coverage.
// ---------------------------------------------------------------------------

/// A one-project daemon, a baseline run, then an edit to a file no test reaches
/// statically but the project's runtime coverage attributes to it, with the project's
/// runtime selection set to `mode`. The host writes the coverage it is asked for, so every
/// run ingests. Returns the runtime obligation ledger the daemon persisted once the edit
/// was flushed, and the log lines written from the edit on.
let private runtimeOnlyEdit (mode: SelectionMode) =
    withTempDir "tp-runtime-shadow" (fun tmpDir ->
        let lines = System.Collections.Concurrent.ConcurrentQueue<string>()

        let sink: FsHotWatch.Logging.LogSink =
            { Write = lines.Enqueue
              Level = FsHotWatch.Logging.LogLevel.Info
              File = None }

        use _ = FsHotWatch.Logging.installSink sink

        let sourceFile = Path.Combine(tmpDir, "src", "RuntimeOnly.fs")
        let projectFile = Path.Combine(tmpDir, "RuntimeProject.fsproj")
        Directory.CreateDirectory(Path.GetDirectoryName sourceFile) |> ignore
        File.WriteAllText(sourceFile, "namespace RuntimeOnly\n")

        // The coverage every run of the project writes: it executed RuntimeOnly.fs.
        let coverage = Path.Combine(tmpDir, "runtime-project.cobertura.xml")

        File.WriteAllText(
            coverage,
            $"""<?xml version="1.0"?><coverage><packages><package name="RuntimeProject"><classes><class filename="%s{sourceFile}" name="RuntimeOnly"><lines><line number="1" hits="1" /></lines></class></classes></package></packages></coverage>"""
        )

        let runner = Path.Combine(tmpDir, "runtime-project-runner.sh")

        File.WriteAllText(
            runner,
            $"""#!/bin/sh
set -eu
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--coverage-output" ]; then cp "%s{coverage}" "$2"; shift 2; else shift; fi
done
"""
        )

        let config =
            { Project = "RuntimeProject"
              Command = "sh"
              Args = runner
              Group = "default"
              Environment = []
              FilterTemplate = Some "--ignored-filter {classes}"
              ClassJoin = " "
              TimeoutSec = Some 10
              ReportVerificationFormat = Disabled
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

        let handler =
            create dbPath tmpDir (Some [ config ]) None None None (Some coveragePaths) []

        host.RegisterHandler(handler)

        emitBuildAndWaitTerminal host
        lines.Clear()

        let source = "namespace RuntimeOnly\n\n// edited\n"
        File.WriteAllText(sourceFile, source)

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

        let ledger =
            match loadRuntimeCoverageObligations tmpDir with
            | Ok obligations -> obligations
            | Error reason -> failwith reason

        emitBuildAndWaitTerminal host

        ledger, List.ofSeq lines)

let private selectedBy (lines: string list) =
    lines |> List.filter (fun line -> line.Contains "Selected by: RuntimeProject")

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``control - a deciding project is owed, and run, for the file its runtime coverage reached`` () =
    let _, lines = runtimeOnlyEdit SelectionMode.RuntimeDecides

    test
        <@
            lines
            |> List.exists (fun line -> line.Contains "runtime-coverage debt over 1 file(s)")
        @>

    test <@ selectedBy lines |> List.exists (fun line -> line.Contains "runtime-coverage") @>
    test <@ not (lines |> List.exists (fun line -> line.Contains RuntimeShadowLine.Prefix)) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a shadow project owes nothing for the file its runtime coverage reached, and the launch logs what it would add``
    ()
    =
    let ledger, lines = runtimeOnlyEdit SelectionMode.StaticDecidesRuntimeLogged

    test <@ Map.isEmpty ledger @>
    test <@ not (lines |> List.exists (fun line -> line.Contains "runtime-coverage debt")) @>

    test
        <@
            lines
            |> List.exists (fun line ->
                line.Contains "runtime coverage (shadow) would select src/RuntimeOnly.fs -> RuntimeProject")
        @>

    // Positive control: the edit launched a run.
    test <@ not (List.isEmpty (selectedBy lines)) @>

    test
        <@
            selectedBy lines
            |> List.forall (fun line -> not (line.Contains "runtime-coverage"))
        @>

    test
        <@
            lines
            |> List.exists (fun line ->
                line.Contains RuntimeShadowLine.Prefix
                && line.Contains "\"RuntimeProject\":[\"src/RuntimeOnly.fs\"]")
        @>
