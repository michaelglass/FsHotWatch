/// `tests.traces` through the real plugin: a `run-tests` run launches a traced project's
/// woven apphost (here a script standing in for one, so nothing is built or woven), stores
/// its traces after the run, and stores and logs every refusal. The verdict is the one the
/// run would have had without traces.
module FsHotWatch.Tests.TestPruneTracesPluginTests

open System
open System.IO
open System.Threading
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open TestPrune.AstAnalyzer
open TestPrune.Trace
open TestPrune.Trace.Model

let private settings record : TraceSettings =
    { Record = record
      DbPath = ".fshw/test-traces.db"
      WeaveTests = WeaveTestSites
      FingerprintInputs = []
      FingerprintEnv = []
      VerifyTimeoutSec = 60 }

let private oneRowManifest: Manifest =
    { Rows =
        [| { Id = 0
             Kind = UserMethod
             Assembly = "L"
             TypeName = "L.M"
             Member = "f"
             Document = None
             FirstLine = 1
             LastLine = 1 } |]
      Documents = Map.empty
      IdCount = 1 }

let private ctrfReport =
    """{"results":{"summary":{"tests":1,"passed":1,"failed":0,"skipped":0,"pending":0,"other":0},"tests":[{"name":"Ns.C.t","status":"passed","duration":1}]}}"""

/// A recorder dump: `Ns.C.t` passed, having executed `ids`.
let private dumpExecuting (ids: string) =
    [ """{"format":"testprune-trace/1","pid":1,"parentScope":null,"runtime":".NET 10.0.0","os":"OSX","arch":"Arm64","ids":1,"cpuMs":5,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}"""
      $"""{{"key":"T:1","test":{{"class":"Ns.C","method":"t","display":"Ns.C.t"}},"parents":[],"links":[],"ids":[%s{ids}],"inputs":[],"children":[]}}"""
      """{"end":true}""" ]

/// A shell script that touches `marker`, runs `prelude`, then writes the CTRF report the
/// run asked for (unless `fail`) and `dumpLines` into `$TESTPRUNE_TRACE_OUT` when that is
/// set.
let private writeRunnerWith (path: string) (marker: string) (fail: bool) (prelude: string) (dumpLines: string list) =
    let dump = dumpLines |> String.concat "\n"

    let body =
        "#!/bin/sh\n"
        + $"touch '%s{marker}'\n"
        + prelude
        + "name=''; dir=''\n"
        + "while [ $# -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    --report-ctrf-filename) name=\"$2\"; shift ;;\n"
        + "    --results-directory) dir=\"$2\"; shift ;;\n"
        + "  esac\n"
        + "  shift\n"
        + "done\n"
        + (if fail then
               "exit 1\n"
           else
               $"if [ -n \"$dir\" ] && [ -n \"$name\" ]; then printf '%%s' '%s{ctrfReport}' > \"$dir/$name\"; fi\n"
               + $"if [ -n \"$TESTPRUNE_TRACE_OUT\" ]; then printf '%%s\\n' '%s{dump}' > \"$TESTPRUNE_TRACE_OUT/trace-1.ndjson\"; fi\n")

    File.WriteAllText(path, body)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

/// `writeRunnerWith` with no prelude and a dump that executed nothing.
let private writeRunner (path: string) (marker: string) (fail: bool) =
    writeRunnerWith path marker fail "" (dumpExecuting "")

/// A preparation that "weaves" by handing back `apphost` as the woven app.
let private preparedAs
    (root: string)
    (apphost: string)
    (req: TraceSession.PrepareRequest)
    : Result<TraceSession.TraceLaunch, string> =
    let dumpDir = Path.Combine(req.RunDir, "traces", req.TestProject)
    Directory.CreateDirectory dumpDir |> ignore

    Ok
        { TestProject = req.TestProject
          Apphost = apphost
          Env =
            [ "TESTPRUNE_TRACE_OUT", dumpDir
              "TESTPRUNE_TRACE_IDS", "1"
              "TESTPRUNE_TRACE_REPO_ROOT", root
              "DOTNET_ROOT", "/unused" ]
          DumpDir = dumpDir
          InputRoot = root
          Shadow =
            { ShadowBin.Shadow.Dir = Path.GetDirectoryName apphost
              Apphost = apphost
              ManifestDir = root
              Manifest = oneRowManifest
              WeaveKey = "k"
              Reused = false
              OriginalDepsJsonSha256 = "0"
              Verify =
                { Prepared = 0
                  Invalid = []
                  SkippedGeneric = 0
                  Other = 0 } } }

let private config project command args : TestConfig =
    { Project = project
      Command = command
      Args = args
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = Some 60
      ReportVerificationFormat = Ctrf }

/// Run `run-tests` once over `configs` with `traces`, returning the command's JSON and
/// the plugin's activity log.
let private runTests (root: string) (configs: TestConfig list) (traces: TraceWiring option) =
    let host = createModelHost (Unchecked.defaultof<_>) root

    host.RegisterHandler(
        createWithLaunchDeadline
            (TimeSpan.FromMinutes 2.0)
            (fun () -> Map.empty)
            (Path.Combine(root, "tp.db"))
            root
            (Some configs)
            None
            None
            None
            None
            []
            traces
    )

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    // The run is over, so its activity is the last run's.
    let activity =
        host.GetActivitySnapshot("test-prune").LastRun
        |> Option.map (fun r -> r.ActivityTail)
        |> Option.defaultValue []

    result.Value, activity

let private statusOf (json: string) (project: string) =
    use doc = JsonDocument.Parse json

    doc.RootElement.GetProperty("projects").EnumerateArray()
    |> Seq.find (fun p -> p.GetProperty("project").GetString() = project)
    |> fun p -> p.GetProperty("status").GetString()

let private runsOf (root: string) project =
    use store = TraceStore.Store.Open(Path.Combine(root, ".fshw", "test-traces.db"))
    store.Runs project

[<Fact(Timeout = 60000)>]
let ``a traced project runs its woven copy, and its traces are stored and logged`` () =
    withTempDir "tp-traced" (fun root ->
        let woven = Path.Combine(root, "woven.sh")
        let marker = Path.Combine(root, "traced-ran")
        writeRunner woven marker false

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide = fun _ -> TraceRun.decideWith (preparedAs root woven) (fun () -> Some "/dotnet-root") }

        let json, activity =
            runTests root [ config "T" "dotnet" "run --project tests/T --no-build" ] (Some wiring)

        test <@ File.Exists marker @>
        test <@ statusOf json "T" = "passed" @>
        test <@ activity |> List.contains "traces: T 1/1 traced, 1 complete" @>
        let run = runsOf root "T" |> List.exactlyOne
        test <@ (run.Status, run.Kind) = (TraceStore.Recorded, TraceStore.FullRun) @>)

[<Fact(Timeout = 60000)>]
let ``trace preparation is given the run's cancellation token`` () =
    withTempDir "tp-token" (fun root ->
        let runner = Path.Combine(root, "runner.sh")
        writeRunner runner (Path.Combine(root, "ran")) false
        let tokens = ResizeArray<CancellationToken>()

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide =
                fun ct _ _ _ _ ->
                    tokens.Add ct
                    Untraced None }

        runTests root [ config "T" runner "" ] (Some wiring) |> ignore

        test <@ tokens.Count = 1 && tokens.[0].CanBeCanceled @>)

[<Fact(Timeout = 60000)>]
let ``each project is decided with its own weaveTests, and none where it set none`` () =
    withTempDir "tp-weave" (fun root ->
        let runner = Path.Combine(root, "runner.sh")
        writeRunner runner (Path.Combine(root, "ran")) false
        let weaves = ResizeArray<string * TraceWeaveTests option>()

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.ofList [ "A", WeaveTestFull ]
              Decide =
                fun _ _ project _ _ ->
                    lock weaves (fun () -> weaves.Add((project.Project, project.TraceWeave)))
                    Untraced None }

        runTests root [ config "A" runner ""; config "B" runner "" ] (Some wiring)
        |> ignore

        test <@ weaves |> Seq.sort |> List.ofSeq = [ "A", Some WeaveTestFull; "B", None ] @>)

[<Fact(Timeout = 60000)>]
let ``a refused project runs as configured with the same verdict, and the refusal is stored and logged`` () =
    withTempDir "tp-refused" (fun root ->
        let runner = Path.Combine(root, "runner.sh")
        let marker = Path.Combine(root, "plain-ran")
        writeRunner runner marker false

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide = TraceRun.decide }

        let json, activity = runTests root [ config "T" runner "" ] (Some wiring)

        test <@ File.Exists marker @>
        test <@ statusOf json "T" = "passed" @>
        test <@ activity |> List.contains "traces: T not recorded — not-a-dotnet-run-command" @>
        let run = runsOf root "T" |> List.exactlyOne
        test <@ (run.Status, run.Reason) = (TraceStore.Refused, "not-a-dotnet-run-command") @>)

[<Fact(Timeout = 60000)>]
let ``a policy that does not record this mode leaves no trace store at all`` () =
    withTempDir "tp-untraced" (fun root ->
        let runner = Path.Combine(root, "runner.sh")
        writeRunner runner (Path.Combine(root, "ran")) false

        let wiring =
            { Policy = settings RecordFullRuns
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide = TraceRun.decide }

        // run-tests launches under `check`'s mode here, which full-runs does not record.
        let json, activity = runTests root [ config "T" runner "" ] (Some wiring)

        test <@ statusOf json "T" = "passed" @>
        test <@ activity |> List.exists (fun l -> l.StartsWith "traces:") |> not @>
        test <@ not (File.Exists(Path.Combine(root, ".fshw", "test-traces.db"))) @>)

[<Fact(Timeout = 60000)>]
let ``a traced launch that verified nothing re-runs untraced, and the refusal says so`` () =
    withTempDir "tp-fallback" (fun root ->
        let woven = Path.Combine(root, "woven.sh")
        let tracedMarker = Path.Combine(root, "traced-ran")
        writeRunner woven tracedMarker true
        let runner = Path.Combine(root, "runner.sh")
        let plainMarker = Path.Combine(root, "plain-ran")
        writeRunner runner plainMarker false

        // The configured command is the untraced one; the stand-in decision traces it
        // with the failing "woven" script.
        let decide _ rt (project: TraceProject) runDir extraArgs =
            match TraceRun.decideWith (preparedAs root woven) (fun () -> Some "/r") rt project runDir extraArgs with
            | Untraced(Some "not-a-dotnet-run-command") ->
                match
                    preparedAs
                        root
                        woven
                        { RepoRoot = root
                          ProjectDir = root
                          AssemblyName = "T"
                          TestProject = project.Project
                          WeaveTests = SitesOnly
                          RunDir = runDir
                          VerifyTimeout = TimeSpan.FromSeconds 1.0 }
                with
                | Ok session ->
                    Traced(
                        { Command = woven
                          Args = TracedLaunch.appArgs "dotnet" "run" extraArgs |> Result.defaultValue []
                          Environment = session.Env },
                        session
                    )
                | Error e -> Untraced(Some e)
            | other -> other

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide = decide }

        let json, activity = runTests root [ config "T" runner "" ] (Some wiring)

        test <@ File.Exists tracedMarker && File.Exists plainMarker @>
        test <@ statusOf json "T" = "passed" @>

        let reason =
            "the traced launch failed without writing a test report; the project re-ran untraced"

        test <@ activity |> List.contains $"traces: T not recorded — %s{reason}" @>
        let run = runsOf root "T" |> List.exactlyOne
        test <@ (run.Status, run.Reason) = (TraceStore.Refused, reason) @>)

/// A real FCS check of `source`, saved as `fileName` under `root`.
let private checkForReal (root: string) (fileName: string) (source: string) =
    async {
        let checker = sharedChecker.Value
        let pipeline = FsHotWatch.CheckPipeline.CheckPipeline(checker)
        let filePath = Path.Combine(root, fileName)
        File.WriteAllText(filePath, source)
        let! projOptions = getScriptOptions checker filePath source
        pipeline.RegisterProject(filePath, projOptions)
        return! pipeline.CheckFile(FsHotWatch.Events.AbsFilePath.create filePath)
    }
    |> Async.RunSynchronously
    |> Option.defaultWith (fun () -> failwith $"no check result for %s{fileName}")
    |> stampFixture

[<Fact(Timeout = 120000)>]
let ``a traced run joins against the analysis admitted while it ran, however far the mailbox is behind`` () =
    withTempDir "tp-fold-order" (fun root ->
        let blocker = checkForReal root "K.fsx" "module K\nlet k = 1\n"

        let indexing =
            checkForReal root "L.fsx" "module L\ntype M() =\n    member _.f() = 1\n"

        let woven = Path.Combine(root, "woven.sh")
        let started = Path.Combine(root, "started")
        let go = Path.Combine(root, "go")
        let finished = Path.Combine(root, "finished")

        // The traced test executes `L.M::f` (probe id 0), and only finishes once `go` exists.
        writeRunnerWith
            woven
            started
            false
            $"while [ ! -f '%s{go}' ]; do sleep 0.05; done\ntrap \"touch '%s{finished}'\" EXIT\n"
            (dumpExecuting "0")

        // Holds this plugin's mailbox: the first refresh after `armed` blocks its fold until
        // `release`, so the events admitted behind it stay unfolded.
        use armed = new ManualResetEventSlim(false)
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)

        let holdingExtension =
            { new TestPrune.Extensions.ITestPruneExtension with
                member _.Name = "holds-the-mailbox"

                member _.AnalyzeEdges _ _ =
                    if armed.IsSet && not entered.IsSet then
                        entered.Set()
                        release.Wait(TimeSpan.FromSeconds 60.0) |> ignore

                    [] }

        let wiring =
            { Policy = settings RecordEveryRun
              OptedOut = Set.empty
              WeaveOverrides = Map.empty
              Decide = fun _ -> TraceRun.decideWith (preparedAs root woven) (fun () -> Some "/dotnet-root") }

        let host = createModelHost (Unchecked.defaultof<_>) root

        host.RegisterHandler(
            createWithLaunchDeadline
                (TimeSpan.FromMinutes 2.0)
                (fun () -> Map.empty)
                (Path.Combine(root, "tp.db"))
                root
                (Some [ config "T" "dotnet" "run --project tests/T --no-build" ])
                (Some(fun _ -> [ holdingExtension ]))
                None
                None
                None
                []
                (Some wiring)
        )

        try
            let run = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
            test <@ waitUntilTrue (fun () -> File.Exists started) 30000 @>

            // While the run is in flight: one analysis whose fold holds the mailbox, then the
            // analysis that indexes `L.M`, admitted behind it.
            armed.Set()
            host.EmitFileChecked blocker
            host.EmitBatchChecked(fakeBatchChecked [ Path.Combine(root, "K.fsx") ])
            test <@ entered.Wait(TimeSpan.FromSeconds 30.0) @>
            host.EmitFileChecked indexing
            host.EmitBatchChecked(fakeBatchChecked [ Path.Combine(root, "L.fsx") ])

            // The test finishes and ingestion posts its probe behind `L`'s analysis, which
            // is still unfolded when the mailbox is let go.
            File.WriteAllText(go, "")
            test <@ waitUntilTrue (fun () -> File.Exists finished) 30000 @>

            // Ingestion logs its wait once the probe is posted: only then is the mailbox
            // let go, so `L` cannot fold before the probe exists.
            test
                <@
                    waitUntilTrue
                        (fun () ->
                            host.GetActivitySnapshot("test-prune").ActivityTail
                            |> List.exists (fun l -> l.Contains TraceRun.indexWaitLine))
                        30000
                @>

            release.Set()
            test <@ run.Wait(TimeSpan.FromSeconds 60.0) @>

            let first = runsOf root "T" |> List.minBy (fun r -> r.RecordedAt)
            use stats = JsonDocument.Parse first.StatsJson

            let stat (name: string) =
                stats.RootElement.GetProperty(name).GetInt32()

            test <@ first.Status = TraceStore.Recorded @>
            test <@ (stat "unmappedIds", stat "complete") = (0, 1) @>
        finally
            release.Set())
