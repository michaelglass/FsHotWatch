module TestPrune.Trace.Tests.EndToEndTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.TraceSession
open TestPrune.Trace.Tests

let private request projectDir runDir =
    { RepoRoot = Fixtures.repoRoot
      ProjectDir = projectDir
      AssemblyName = "FxTests"
      TestProject = "FxTests"
      WeaveTests = SitesOnly
      RunDir = runDir
      VerifyTimeout = TimeSpan.FromMinutes 2.0 }

/// What the end-to-end run hands its assertions.
type private E2e =
    { Store: TraceStore.Store
      Summary: TraceIngest.IngestSummary
      Launch: TraceLaunch
      Outcomes: TestOutcome list
      StaleDumpSurvived: bool }

/// Prepare a scratch copy of the fixture's xUnit v3 build output, run its woven apphost
/// with the CTRF switches a host passes, ingest, and hand back the store and the summary.
/// A stale dump left in the run's dump directory must not survive preparation.
let private run =
    lazy
        (let runDir = Directory.CreateTempSubdirectory("tp-e2e-").FullName
         let projectDir = ShadowBinTests.scratchProject ()

         try
             let stale = Path.Combine(runDir, "traces", "FxTests", "trace-1.ndjson")
             Directory.CreateDirectory(Path.GetDirectoryName stale) |> ignore
             File.WriteAllText(stale, "stale")

             let launch =
                 prepareProject (request projectDir runDir) |> Result.defaultWith failwith

             let staleSurvived = File.Exists stale

             let code, output =
                 Launch.run
                     launch.Apphost
                     [ "--report-xunit-ctrf"
                       "--report-xunit-ctrf-filename"
                       "FxTests.ctrf.json"
                       "--results-directory"
                       runDir ]
                     launch.Env
                     Fixtures.repoRoot
                     (TimeSpan.FromMinutes 3.0)

             if code <> 0 then
                 failwith $"woven fixture suite failed (exit %d{code}):\n%s{output}"

             let outcomes =
                 Ctrf.parse (File.ReadAllText(Path.Combine(runDir, "FxTests.ctrf.json")))

             let store = TraceStore.Store.Open(Path.Combine(runDir, "traces.db"))
             let symbols = TestPrune.Ports.toSymbolStore FixtureIndex.withTests.Value

             // The fixture index is rooted at a scratch copy with the fixtures' relative
             // layout; the woven PDB documents live under the real fixture root. Ingest
             // against that root. File inputs stay relative to the repository root the
             // recorder filtered them against (TraceLaunch.InputRoot).
             let summary =
                 ingestProject
                     store
                     Fixtures.fixtureRoot
                     launch
                     { RunId = "e2e"
                       Kind = TraceStore.FullRun
                       LaunchTreeHash = "t"
                       CurrentTreeHash = "t"
                       Outcomes = outcomes
                       Symbols = symbols
                       FingerprintFiles = []
                       FingerprintEnv = [] }
                 |> Result.defaultWith failwith

             { Store = store
               Summary = summary
               Launch = launch
               Outcomes = outcomes
               StaleDumpSurvived = staleSurvived }
         finally
             ShadowBinTests.deleteProject projectDir)

let private traceOf (meth: string) =
    let e = run.Value

    let cls =
        if meth.StartsWith "a " then "ClassA"
        elif meth.StartsWith "b " then "ClassB"
        elif meth.StartsWith "c " then "ClassC"
        elif meth.StartsWith "d " then "ClassD"
        else "ClassE"

    e.Store.TryRead(
        TraceIngest.testKey "FxTests" $"FxTests.AttributionTests+%s{cls}" meth,
        e.Summary.EnvFingerprint.Value
    )
    |> Option.get

let private names (t: TraceStore.StoredTrace) = t.Symbols |> Set.map fst

[<Fact>]
let ``the launch carries exactly the recorder's environment and a fresh dump directory`` () =
    let e = run.Value

    test
        <@
            e.Launch.Env = [ Contract.OutEnv, e.Launch.DumpDir
                             Contract.IdsEnv, string e.Launch.Shadow.Manifest.IdCount
                             Contract.RepoRootEnv, Fixtures.repoRoot
                             "DOTNET_ROOT", Launch.dotnetRoot () ]
        @>

    test <@ e.Launch.InputRoot = Fixtures.repoRoot && e.Launch.TestProject = "FxTests" @>
    test <@ e.Launch.Apphost = e.Launch.Shadow.Apphost @>
    test <@ not e.StaleDumpSurvived @>

[<Fact>]
let ``every executed test is traced and nothing is unattributed`` () =
    let s = run.Value.Summary
    test <@ s.Status = TraceStore.Recorded && List.isEmpty s.RejectedDumps @>
    test <@ s.Executed = 14 && s.Traced = 14 @>
    test <@ List.isEmpty s.UntracedExecuted @>
    test <@ s.Counters.Ambient = 0L && s.Counters.Overflow = 0L @>
    // Fourteen CTRF rows are twelve tests (each theory's two rows share one trace). Only the test
    // that starts an unwoven child (/bin/echo leaves no dump) is incomplete.
    let e = run.Value
    test <@ (e.Store.TestKeysOf("FxTests", s.EnvFingerprint.Value)).Count = 12 @>
    test <@ s.Complete = 11 && s.ReasonCounts = Map [ "child-process-untraced", 1 ] @>
    test <@ s.UnmappedIds = 0 @>

[<Fact>]
let ``sync, task, Task.Run, async and Async.Parallel tests each record exactly their own code`` () =
    test
        <@
            names (traceOf "a sync area") |> Set.isSuperset
            <| set [ "FxLib.Logic.area"; "FxLib.Shape.Circle" ]
        @>

    test <@ not (names (traceOf "a sync area") |> Set.contains "FxLib.Logic.turn") @>

    test
        <@
            names (traceOf "a async task turn") |> Set.isSuperset
            <| set [ "FxLib.Logic.turn"; "FxLib.Dir.East"; "FxLib.Dir.South" ]
        @>

    test
        <@
            names (traceOf "a Task.Run describe") |> Set.isSuperset
            <| set [ "FxLib.Logic.describe"; "FxLib.Dog" ]
        @>

    test <@ names (traceOf "b async block colorCode") |> Set.contains "FxLib.Color5.Blue" @>

    test
        <@
            names (traceOf "b Async.Parallel sval") |> Set.isSuperset
            <| set [ "FxLib.SResult.SOk"; "FxLib.SResult.SErr" ]
        @>

    test
        <@
            names (traceOf "b theory aboveThreshold") |> Set.isSuperset
            <| set [ "FxLib.Logic.aboveThreshold"; "FxLib.Values.threshold" ]
        @>

[<Fact>]
let ``a class fixture's code is inherited by that class's tests only`` () =
    test <@ names (traceOf "a sync area") |> Set.contains "FxLib.Logic.sumPoint" @>
    test <@ not (names (traceOf "b async block colorCode") |> Set.contains "FxLib.Logic.sumPoint") @>

[<Fact>]
let ``each test's own entry point is in its trace`` () =
    test
        <@
            names (traceOf "a sync area")
            |> Set.exists (fun n -> n.StartsWith "FxTests.AttributionTests.ClassA.")
        @>

[<Fact>]
let ``a repository file read is an input; an untraced child makes the trace incomplete`` () =
    let read = traceOf "c reads a repo file"

    test
        <@
            read.Complete
            && read.Inputs |> Set.exists (fun (k, key, _) -> k = "read" && key = "global.json")
        @>

    let child = traceOf "c starts a child process"
    test <@ child.Reasons = [ "child-process-untraced:echo" ] @>

[<Fact>]
let ``a file a module value reads while its type initializes is an input of the tests that use the module`` () =
    let readsToolConfig (t: TraceStore.StoredTrace) =
        t.Inputs |> Set.exists (fun (k, key, _) -> k = "read" && key = "mise.toml")

    let user = traceOf "d reads a module value"
    test <@ user.Complete && readsToolConfig user @>
    test <@ names user |> Set.contains "FxLib.Settings.toolConfig" @>
    test <@ not (readsToolConfig (traceOf "a sync area")) @>

[<Fact>]
let ``a file a test-project module value reads while it initializes is an input of each test that uses it`` () =
    // Whichever test triggers the initializer first, the other still depends on the read.
    let readsLicence (t: TraceStore.StoredTrace) =
        t.Inputs |> Set.exists (fun (k, key, _) -> k = "read" && key = "LICENSE")

    test <@ readsLicence (traceOf "e first licence reader") @>
    test <@ readsLicence (traceOf "e second licence reader") @>

// ---------------------------------------------------------------- refusals

[<Fact>]
let ``a project with no build output is refused with the shadow bin's reason`` () =
    let dir = Directory.CreateTempSubdirectory("tp-e2e-none-").FullName

    match prepareProject (request dir dir) with
    | Error why -> test <@ why = $"""no build output under %s{Path.Combine(dir, "bin", "Debug")}""" @>
    | Ok _ -> failwith "prepared a project with no build output"

[<Fact>]
let ``a project with nothing woven is refused before it runs`` () =
    let projectDir = ShadowBinTests.scratchProject ()
    let elsewhere = Directory.CreateTempSubdirectory("tp-e2e-root-").FullName

    try
        let result =
            prepareProject
                { request projectDir elsewhere with
                    RepoRoot = elsewhere }

        test <@ result = Error(TraceIngest.nothingWovenReason elsewhere) @>
        test <@ not (Directory.Exists(Path.Combine(elsewhere, "traces"))) @>
    finally
        ShadowBinTests.deleteProject projectDir

[<Fact>]
let ``a cancelled preparation raises instead of refusing`` () =
    let projectDir = ShadowBinTests.scratchProject ()
    let runDir = Directory.CreateTempSubdirectory("tp-e2e-cancel-").FullName

    try
        use cts = new CancellationTokenSource()

        let cancelling: Launch.Launcher =
            fun _ _ ->
                cts.Cancel()
                137, "killed"

        raises<OperationCanceledException> <@ prepareProjectWith cancelling cts.Token (request projectDir runDir) @>
        test <@ not (Directory.Exists(Path.Combine(runDir, "traces"))) @>
    finally
        ShadowBinTests.deleteProject projectDir

[<Fact>]
let ``a launcher's cancellation that is not the run's is a refusal`` () =
    let projectDir = ShadowBinTests.scratchProject ()

    try
        let timedOut: Launch.Launcher =
            fun _ _ -> raise (OperationCanceledException "the launcher gave up")

        test
            <@
                prepareProjectWith timedOut CancellationToken.None (request projectDir projectDir) = Error
                    "trace preparation failed: the launcher gave up"
            @>
    finally
        ShadowBinTests.deleteProject projectDir

[<Fact>]
let ``a host-launched preparation refuses an app that ships its own recorder before launching`` () =
    let projectDir = ShadowBinTests.scratchProject ()
    let runDir = Directory.CreateTempSubdirectory("tp-e2e-own-recorder-").FullName

    try
        let debug =
            Directory.GetDirectories(Path.Combine(projectDir, "bin", "Debug"))
            |> Array.exactlyOne

        let depsPath = Path.Combine(debug, "FxTests.deps.json")
        let version = typeof<Probes>.Assembly.GetName().Version.ToString 3

        let asProject, _ =
            DepsJson.injectRecorder (File.ReadAllText depsPath) "FxTests" version
            |> Result.defaultWith failwith

        File.WriteAllText(depsPath, asProject)
        let launched = ref false

        let launcher: Launch.Launcher =
            fun req ct ->
                launched.Value <- true
                Launch.direct req ct

        let expected =
            ShadowBin.describeRefusal (ShadowBin.AppShipsOwnRecorder "its deps.json lists the recorder as a project")

        test <@ prepareProjectWith launcher CancellationToken.None (request projectDir runDir) = Error expected @>
        test <@ not launched.Value @>
        test <@ not (Directory.Exists(Path.Combine(projectDir, "obj", "traced"))) @>
        test <@ not (Directory.Exists(Path.Combine(runDir, "traces"))) @>
    finally
        ShadowBinTests.deleteProject projectDir

[<Fact>]
let ``preparation never throws`` () =
    match prepareProject (request null (Path.GetTempPath())) with
    | Error why -> test <@ why.StartsWith "trace preparation failed: " @>
    | Ok _ -> failwith "prepared a null project"

[<Fact>]
let ``ingestion never throws`` () =
    let dir = Directory.CreateTempSubdirectory("tp-e2e-ingest-").FullName
    let store = TraceStore.Store.Open(Path.Combine(dir, "traces.db"))
    (store :> IDisposable).Dispose()

    let launch =
        { TestProject = "P"
          Apphost = ""
          Env = []
          DumpDir = dir
          InputRoot = dir
          Shadow =
            { Dir = dir
              Apphost = ""
              ManifestDir = dir
              Manifest =
                { Rows = [||]
                  Documents = Map.empty
                  IdCount = 0 }
              WeaveKey = ""
              Reused = false
              OriginalDepsJsonSha256 = ""
              Verify =
                { Prepared = 0
                  Invalid = []
                  SkippedGeneric = 0
                  Other = 0 } } }

    let result =
        ingestProject
            store
            dir
            launch
            { RunId = "r"
              Kind = TraceStore.PartialRun
              LaunchTreeHash = "t"
              CurrentTreeHash = "t"
              Outcomes = []
              Symbols = TestPrune.Ports.toSymbolStore FixtureIndex.build.Value
              FingerprintFiles = []
              FingerprintEnv = [] }

    match result with
    | Error why -> test <@ why.StartsWith "trace ingestion failed: " @>
    | Ok _ -> failwith "ingested into a disposed store"

[<Fact>]
let ``a refusal is stored as a refused run with its reason`` () =
    let dir = Directory.CreateTempSubdirectory("tp-e2e-refusal-").FullName
    use store = TraceStore.Store.Open(Path.Combine(dir, "traces.db"))
    recordRefusal store "r1" "P" TraceStore.PartialRun "tree" "no apphost"
    let r = store.Runs "P" |> List.exactlyOne

    test
        <@
            (r.RunId, r.Status, r.Reason, r.Kind, r.TreeHash, r.EnvFingerprint) = ("r1",
                                                                                   TraceStore.Refused,
                                                                                   "no apphost",
                                                                                   TraceStore.PartialRun,
                                                                                   "tree",
                                                                                   "")
        @>
