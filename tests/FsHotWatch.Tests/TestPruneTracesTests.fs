/// Recording per-test traces inside a test run: which projects are traced and how they
/// launch (`TraceRun.decide`), and how a finished run's traces or refusals are stored
/// (`TraceRun.ingestAll`). A refusal runs the project untraced and is stored with its
/// reason; nothing here can throw into the run or change its verdict.
module FsHotWatch.Tests.TestPruneTracesTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.TestPrune
open TestPrune.Trace
open TestPrune.Trace.Model

let private settings record =
    { Record = record
      DbPath = ".fshw/test-traces.db"
      WeaveTests = WeaveTestSites
      FingerprintInputs = []
      FingerprintEnv = []
      VerifyTimeoutSec = 120 }

let private runtime root record excluded mode =
    { Settings = settings record
      RepoRoot = root
      ExcludedProjects = excluded
      Mode = mode
      RunsEveryProjectInFull = false }

let private target (root: string) project : FsHotWatch.TestPrune.ArtifactFreshness.RunnerTarget =
    { ProjectFile = None
      ProjectDir = Path.Combine(root, "tests", project)
      AssemblyName = project
      BinDir = Path.Combine(root, "tests", project, "bin", "Debug") }

let private project (root: string) name : TraceProject =
    { Project = name
      Command = "dotnet"
      Args = $"run --project tests/%s{name} --no-build"
      Environment = [ "APP_ENV", "test" ]
      Target = Some(target root name)
      CtrfPath = Some(Path.Combine(root, "run", $"%s{name}.ctrf.json")) }

let private tempRoot () =
    Directory.CreateTempSubdirectory("fshw-traces-").FullName

let private emptyManifest: Manifest =
    { Rows = [||]
      Documents = Map.empty
      IdCount = 0 }

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

let private shadowOf (dir: string) manifest : ShadowBin.Shadow =
    { Dir = dir
      Apphost = Path.Combine(dir, "T")
      ManifestDir = dir
      Manifest = manifest
      WeaveKey = "k"
      Reused = false
      OriginalDepsJsonSha256 = "0"
      Verify =
        { Prepared = 0
          Invalid = []
          SkippedGeneric = 0
          Other = 0 } }

/// A prepared launch whose dump directory exists and is empty.
let private sessionOf (root: string) name manifest : TraceSession.TraceLaunch =
    let dumpDir = Path.Combine(root, "run", "traces", name)
    Directory.CreateDirectory dumpDir |> ignore

    { TestProject = name
      Apphost = Path.Combine(root, "tests", name, "bin", "Traced", "net10.0", name)
      Env =
        [ "TESTPRUNE_TRACE_OUT", dumpDir
          "TESTPRUNE_TRACE_IDS", "1"
          "TESTPRUNE_TRACE_REPO_ROOT", root
          "DOTNET_ROOT", "/session-root" ]
      DumpDir = dumpDir
      InputRoot = root
      Shadow = shadowOf (Path.Combine(root, "shadow")) manifest }

/// One finished process dump: a single passing test `Ns.C.t` that executed nothing.
let private writeDump (dumpDir: string) =
    let lines =
        [ """{"format":"testprune-trace/1","pid":1,"parentScope":null,"runtime":".NET 10.0.0","os":"OSX","arch":"Arm64","ids":1,"cpuMs":5,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}"""
          """{"key":"T:1","test":{"class":"Ns.C","method":"t","display":"Ns.C.t"},"parents":[],"links":[],"ids":[],"inputs":[],"children":[]}"""
          """{"end":true}""" ]

    File.WriteAllLines(Path.Combine(dumpDir, "trace-1.ndjson"), lines)

let private writeCtrf (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

    File.WriteAllText(
        path,
        """{"results":{"summary":{"tests":1,"passed":1,"failed":0},"tests":[{"name":"Ns.C.t","status":"passed"}]}}"""
    )

let private symbolsOf (root: string) =
    TestPrune.Ports.toSymbolStore (TestPrune.Database.Database.create (Path.Combine(root, "i.db")))

let private storePath (root: string) =
    Path.Combine(root, ".fshw", "test-traces.db")

/// The line every join logs with how long the symbol index took to fold.
let private foldLinePrefix =
    "traces: the symbol index folded the events admitted before ingestion in "

/// The per-project lines, without the fold lines.
let private projectLines (lines: ResizeArray<string>) =
    lines
    |> List.ofSeq
    |> List.filter (fun l -> not (l.StartsWith foldLinePrefix || l = TraceRun.indexWaitLine))

let private runsOf (root: string) name =
    use store = TraceStore.Store.Open(storePath root)
    store.Runs name

/// `decideWith` with a preparation that must never be reached.
let private unreachablePrepare (_: TraceSession.PrepareRequest) : Result<TraceSession.TraceLaunch, string> =
    failwith "prepare must not run"

let private someRoot () = Some "/fshw-root"

// --- decide: when a project is traced at all ---

[<Fact>]
let ``check under full-runs does not trace`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty ImpactSelection

    test <@ TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Fact>]
let ``record off does not trace even under confirm`` () =
    let rt = runtime "/nowhere" RecordOff Set.empty PassThrough

    test <@ TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Fact>]
let ``an excluded project does not trace`` () =
    let rt = runtime "/nowhere" RecordEveryRun (set [ "T" ]) PassThrough

    test <@ TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced None @>

// --- decide: refusals, each named, each before any weaving it would waste ---

[<Fact>]
let ``a project that writes no CTRF report is refused, because its traces could never be complete`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty PassThrough

    let p =
        { project "/nowhere" "T" with
            CtrfPath = None }

    match TraceRun.decideWith unreachablePrepare someRoot rt p "/tmp/run" [] with
    | Untraced(Some reason) -> test <@ reason.StartsWith "no-ctrf-report" @>
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a project whose build output cannot be derived is refused`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty PassThrough

    let p =
        { project "/nowhere" "T" with
            Target = None }

    test
        <@ TraceRun.decideWith unreachablePrepare someRoot rt p "/tmp/run" [] = Untraced(Some "no-derivable-project") @>

[<Fact>]
let ``an unknown dotnet run option is refused before anything is woven`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty PassThrough

    let p =
        { project "/nowhere" "T" with
            Args = "run --project tests/T --launch-profile p" }

    test
        <@
            TraceRun.decideWith unreachablePrepare someRoot rt p "/tmp/run" [] = Untraced(
                Some "unrecognized-run-option:--launch-profile"
            )
        @>

[<Fact>]
let ``no dotnet root for the apphost is refused before anything is woven`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty PassThrough

    match TraceRun.decideWith unreachablePrepare (fun () -> None) rt (project "/nowhere" "T") "/tmp/run" [] with
    | Untraced(Some reason) -> test <@ reason.StartsWith "no-dotnet-root" @>
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a project with no build output is refused with the weaver's reason, and so runs untraced`` () =
    let root = tempRoot ()
    let rt = runtime root RecordEveryRun Set.empty PassThrough

    match TraceRun.decide CancellationToken.None rt (project root "T") (Path.Combine(root, "run")) [] with
    | Untraced(Some reason) -> test <@ reason.Contains "no build output" @>
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a cancelled run stops preparation instead of refusing the project`` () =
    let root = tempRoot ()
    let rt = runtime root RecordEveryRun Set.empty PassThrough

    raises<OperationCanceledException>
        <@ TraceRun.decide (CancellationToken(true)) rt (project root "T") (Path.Combine(root, "run")) [] @>

[<Fact>]
let ``a preparation refusal is passed through verbatim`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty PassThrough

    let refuse (_: TraceSession.PrepareRequest) : Result<TraceSession.TraceLaunch, string> =
        Error "no-woven-assembly: the weave set is empty"

    test
        <@
            TraceRun.decideWith refuse someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced(
                Some "no-woven-assembly: the weave set is empty"
            )
        @>

// --- decide: a traced launch ---

[<Fact>]
let ``a prepared project launches its woven apphost with the trace environment`` () =
    let root = tempRoot ()
    let rt = runtime root RecordFullRuns Set.empty PassThrough
    let session = sessionOf root "T" oneRowManifest
    let mutable request = None

    let prepare (req: TraceSession.PrepareRequest) =
        request <- Some req
        Ok session

    let runDir = Path.Combine(root, "run")

    match TraceRun.decideWith prepare someRoot rt (project root "T") runDir [ "-- --filter-class A" ] with
    | Traced(spec, launched) ->
        test <@ launched = session @>
        test <@ spec.Command = session.Apphost @>
        test <@ spec.Args = [ "--filter-class"; "A" ] @>
        // The project's own environment, fshw's DOTNET_ROOT (not the facade's), and the
        // recorder's keys.
        test
            <@
                spec.Environment = [ "APP_ENV", "test"
                                     "DOTNET_ROOT", "/fshw-root"
                                     "TESTPRUNE_TRACE_OUT", session.DumpDir
                                     "TESTPRUNE_TRACE_IDS", "1"
                                     "TESTPRUNE_TRACE_REPO_ROOT", root ]
            @>
    | other -> failwith $"%A{other}"

    let req = request.Value

    test
        <@
            (req.RepoRoot, req.ProjectDir, req.AssemblyName, req.TestProject) = (root,
                                                                                 Path.Combine(root, "tests", "T"),
                                                                                 "T",
                                                                                 "T")
        @>

    test <@ (req.WeaveTests, req.RunDir, req.VerifyTimeout) = (SitesOnly, runDir, TimeSpan.FromSeconds 120.0) @>

[<Fact>]
let ``weaveTests full weaves the test assembly in full, and a project's own DOTNET_ROOT is kept`` () =
    let root = tempRoot ()

    let rt =
        { runtime root RecordFullRuns Set.empty PassThrough with
            Settings =
                { settings RecordFullRuns with
                    WeaveTests = WeaveTestFull } }

    let session = sessionOf root "T" oneRowManifest
    let mutable mode = None

    let prepare (req: TraceSession.PrepareRequest) =
        mode <- Some req.WeaveTests
        Ok session

    let p =
        { project root "T" with
            Environment = [ "DOTNET_ROOT", "/mine" ] }

    match TraceRun.decideWith prepare someRoot rt p (Path.Combine(root, "run")) [] with
    | Traced(spec, _) ->
        test <@ spec.Environment |> List.filter (fst >> (=) "DOTNET_ROOT") = [ "DOTNET_ROOT", "/mine" ] @>
    | other -> failwith $"%A{other}"

    test <@ mode = Some Full @>

[<Fact>]
let ``a full run under impact selection that full-runs skips is a named refusal`` () =
    let rt =
        { runtime "/nowhere" RecordFullRuns Set.empty ImpactSelection with
            RunsEveryProjectInFull = true }

    match TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] with
    | Untraced(Some reason) -> test <@ reason.StartsWith "not-recorded:" && reason.Contains "full-runs" @>
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an impact-selected subset that full-runs skips is not named`` () =
    let rt = runtime "/nowhere" RecordFullRuns Set.empty ImpactSelection
    test <@ TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Fact>]
let ``an opted-out project in a full run is not named`` () =
    let rt =
        { runtime "/nowhere" RecordFullRuns (Set.ofList [ "T" ]) ImpactSelection with
            RunsEveryProjectInFull = true }

    test <@ TraceRun.decideWith unreachablePrepare someRoot rt (project "/nowhere" "T") "/tmp/run" [] = Untraced None @>

[<Theory>]
[<InlineData("off")>]
[<InlineData("every-run")>]
let ``only full-runs names a full run it does not record`` (policy: string) =
    let rt =
        { runtime "/nowhere" (TraceSettings.parseRecord policy).Value Set.empty ImpactSelection with
            RunsEveryProjectInFull = true }

    test <@ TraceRun.unrecordedReason rt = None @>

[<Fact>]
let ``the launch is the traced one only when the decision is Traced`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest

    let spec: TracedLaunchSpec =
        { Command = "/w/T"
          Args = [ "--a"; "b c" ]
          Environment = [ "K", "V" ] }

    let plain = "dotnet", "run --project tests/T", [ "E", "1" ]
    test <@ TraceRun.launchOf (Traced(spec, session)) plain = ("/w/T", "--a \"b c\"", [ "K", "V" ]) @>
    test <@ TraceRun.launchOf (Untraced(Some "x")) plain = plain @>

// --- the store path ---

[<Fact>]
let ``a relative db path is under the repository root; an absolute one is kept`` () =
    let rt = runtime "/repo" RecordFullRuns Set.empty PassThrough
    test <@ TraceRun.dbPath rt = Path.Combine("/repo", ".fshw/test-traces.db") @>

    let abs =
        { rt with
            Settings =
                { rt.Settings with
                    DbPath = "/elsewhere/t.db" } }

    test <@ TraceRun.dbPath abs = "/elsewhere/t.db" @>

// --- ingestAll: refusals and results are stored and logged, never thrown ---

let private ingest root runs (lines: ResizeArray<string>) =
    let rt = runtime root RecordFullRuns Set.empty PassThrough

    TraceRun.ingestAll
        rt
        (symbolsOf root)
        TraceRun.foldedIndex
        "run1"
        (Some "tree")
        (fun () -> Some "tree")
        runs
        lines.Add
    |> Async.RunSynchronously

[<Fact>]
let ``a refusal is stored as a refused run and logged with its reason`` () =
    let root = tempRoot ()
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision = Untraced(Some "no build output under x")
            Filtered = false
            CtrfPath = None } ]
        lines

    test <@ projectLines lines = [ "traces: T not recorded — no build output under x" ] @>
    let run = runsOf root "T" |> List.exactlyOne

    test
        <@
            (run.Status, run.Reason, run.Kind, run.TreeHash) = (TraceStore.Refused,
                                                                "no build output under x",
                                                                TraceStore.FullRun,
                                                                "tree")
        @>

[<Fact>]
let ``a project tracing does not apply to stores nothing and says nothing`` () =
    let root = tempRoot ()
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision = Untraced None
            Filtered = false
            CtrfPath = None } ]
        lines

    test <@ lines.Count = 0 @>
    test <@ List.isEmpty (runsOf root "T") @>

[<Fact>]
let ``a traced run is ingested with its CTRF outcomes and logged with its counts`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    writeDump session.DumpDir
    let ctrf = Path.Combine(root, "run", "T.ctrf.json")
    writeCtrf ctrf
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = Some ctrf } ]
        lines

    test <@ projectLines lines = [ "traces: T 1/1 traced, 1 complete" ] @>
    let run = runsOf root "T" |> List.exactlyOne
    test <@ (run.Status, run.Kind, run.RunId) = (TraceStore.Recorded, TraceStore.FullRun, "run1") @>

[<Fact>]
let ``a filtered project's traces are a partial run, which drops no other test's trace`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    writeDump session.DumpDir
    let ctrf = Path.Combine(root, "run", "T.ctrf.json")
    writeCtrf ctrf

    ingest
        root
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = true
            CtrfPath = Some ctrf } ]
        (ResizeArray())

    test <@ (runsOf root "T" |> List.exactlyOne).Kind = TraceStore.PartialRun @>

[<Fact>]
let ``an unreadable CTRF report leaves every trace without an outcome, and says so`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    writeDump session.DumpDir
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = Some(Path.Combine(root, "run", "missing.ctrf.json")) } ]
        lines

    test <@ projectLines lines = [ "traces: T 0/0 traced, 0 complete (no CTRF outcomes)" ] @>

[<Fact>]
let ``an empty weave stored by ingestion is logged with the weaver's reason`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" emptyManifest
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = None } ]
        lines

    test
        <@
            projectLines lines
            |> List.exactlyOne
            |> (fun l -> l.StartsWith "traces: T not recorded — no-woven-assembly")
        @>

    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.Refused @>

[<Fact>]
let ``a traced run whose processes wrote no dump is stored failed and logged`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = None } ]
        lines

    test <@ projectLines lines = [ "traces: T not recorded — recorder-no-output" ] @>
    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.FailedToRecord @>

[<Fact>]
let ``a tree that was unbound at launch claims no complete trace`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    writeDump session.DumpDir
    let ctrf = Path.Combine(root, "run", "T.ctrf.json")
    writeCtrf ctrf
    let lines = ResizeArray()
    let rt = runtime root RecordFullRuns Set.empty PassThrough

    TraceRun.ingestAll
        rt
        (symbolsOf root)
        TraceRun.foldedIndex
        "run1"
        None
        (fun () -> None)
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = Some ctrf } ]
        lines.Add
    |> Async.RunSynchronously

    test <@ projectLines lines = [ "traces: T 1/1 traced, 0 complete (the input tree moved during the run)" ] @>
    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.TreeMovedDuringRun @>

[<Fact>]
let ``a tree unreadable at completion claims no complete trace either`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    writeDump session.DumpDir
    let ctrf = Path.Combine(root, "run", "T.ctrf.json")
    writeCtrf ctrf
    let rt = runtime root RecordFullRuns Set.empty PassThrough

    TraceRun.ingestAll
        rt
        (symbolsOf root)
        TraceRun.foldedIndex
        "run1"
        (Some "tree")
        (fun () -> None)
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = Some ctrf } ]
        ignore
    |> Async.RunSynchronously

    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.TreeMovedDuringRun @>

[<Fact>]
let ``an ingestion error is stored as a failed run and logged`` () =
    let root = tempRoot ()
    let session = sessionOf root "T" oneRowManifest
    let lines = ResizeArray()
    let rt = runtime root RecordFullRuns Set.empty PassThrough
    let failing _ _ _ _ = Error "trace ingestion failed: boom"

    TraceRun.ingestAllWith
        failing
        rt
        (symbolsOf root)
        TraceRun.foldedIndex
        "run1"
        (Some "tree")
        (fun () -> Some "tree")
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = true
            CtrfPath = None } ]
        lines.Add
    |> Async.RunSynchronously

    test <@ projectLines lines = [ "traces: T not recorded — trace ingestion failed: boom" ] @>
    let run = runsOf root "T" |> List.exactlyOne

    test
        <@
            (run.Status, run.Reason, run.Kind) = (TraceStore.FailedToRecord,
                                                  "trace ingestion failed: boom",
                                                  TraceStore.PartialRun)
        @>

[<Fact>]
let ``a store that cannot be opened is logged, never thrown`` () =
    let root = tempRoot ()
    // A directory where the database file should be.
    Directory.CreateDirectory(storePath root) |> ignore
    let lines = ResizeArray()

    ingest
        root
        [ { Project = "T"
            Decision = Untraced(Some "x")
            Filtered = false
            CtrfPath = None } ]
        lines

    test
        <@
            projectLines lines
            |> List.exactlyOne
            |> (fun l -> l.StartsWith "traces: not recorded — could not use the trace store")
        @>

[<Fact>]
let ``one project's storage failure does not stop the next project's`` () =
    let root = tempRoot ()
    let session = sessionOf root "U" oneRowManifest
    let lines = ResizeArray()
    let rt = runtime root RecordFullRuns Set.empty PassThrough
    let throwing _ _ _ _ = raise (IOException "disk full")

    TraceRun.ingestAllWith
        throwing
        rt
        (symbolsOf root)
        TraceRun.foldedIndex
        "run1"
        (Some "tree")
        (fun () -> Some "tree")
        [ { Project = "U"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = None }
          { Project = "T"
            Decision = Untraced(Some "refused")
            Filtered = false
            CtrfPath = None } ]
        lines.Add
    |> Async.RunSynchronously

    test
        <@
            projectLines lines = [ "traces: U not recorded — trace storage failed: disk full"
                                   "traces: T not recorded — refused" ]
        @>

    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.Refused @>

// --- ingestAll: the traces join against an index that has folded what the run admitted ---

/// One probe id: the type `L.M`, which a test that runs executes.
let private typeUseManifest: Manifest =
    { Rows =
        [| { Id = 0
             Kind = TypeUse
             Assembly = "L"
             TypeName = "L.M"
             Member = ""
             Document = None
             FirstLine = 0
             LastLine = 0 } |]
      Documents = Map.empty
      IdCount = 1 }

/// One finished process dump: a single passing test `Ns.C.t` that executed probe id 0.
let private writeDumpHittingId0 (dumpDir: string) =
    let lines =
        [ """{"format":"testprune-trace/1","pid":1,"parentScope":null,"runtime":".NET 10.0.0","os":"OSX","arch":"Arm64","ids":1,"cpuMs":5,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}"""
          """{"key":"T:1","test":{"class":"Ns.C","method":"t","display":"Ns.C.t"},"parents":[],"links":[],"ids":[0],"inputs":[],"children":[]}"""
          """{"end":true}""" ]

    File.WriteAllLines(Path.Combine(dumpDir, "trace-1.ndjson"), lines)

/// What the plugin's fold writes into the index for `L.M`'s file.
let private indexTypeM (root: string) =
    let symbol: TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = "L.M"
          Kind = TestPrune.AstAnalyzer.SymbolKind.Type
          SourceFile = "src/L.fs"
          LineStart = 1
          LineEnd = 3
          ContentHash = "m-v1"
          IsExtern = false }

    (TestPrune.Database.Database.create (Path.Combine(root, "i.db"))).RebuildProjects
        [ TestPrune.AstAnalyzer.AnalysisResult.Create([ symbol ], [], []) ]

/// Ingest one traced project `T` hitting `L.M`, against `index`, keeping the summary.
let private ingestTypeUseLogging (root: string) (index: IndexFold) (log: string -> unit) =
    let session = sessionOf root "T" typeUseManifest
    writeDumpHittingId0 session.DumpDir
    let ctrf = Path.Combine(root, "run", "T.ctrf.json")
    writeCtrf ctrf
    let rt = runtime root RecordFullRuns Set.empty PassThrough
    let summaries = ResizeArray<TraceIngest.IngestSummary>()

    let keeping store repoRoot launch completion =
        let result = TraceSession.ingestProject store repoRoot launch completion
        result |> Result.iter summaries.Add
        result

    TraceRun.ingestAllWith
        keeping
        rt
        (symbolsOf root)
        index
        "run1"
        (Some "tree")
        (fun () -> Some "tree")
        [ { Project = "T"
            Decision =
              Traced(
                  { Command = ""
                    Args = []
                    Environment = [] },
                  session
              )
            Filtered = false
            CtrfPath = Some ctrf } ]
        log
    |> Async.RunSynchronously

    List.ofSeq summaries

let private ingestTypeUse (root: string) (index: IndexFold) (lines: ResizeArray<string>) =
    ingestTypeUseLogging root index lines.Add

/// A fold that is still writing the index when ingestion starts: it indexes `write` only
/// after `delay`, then reports folded. `asked` counts the waits.
let private slowFold (delay: TimeSpan) (write: unit -> unit) (asked: int ref) : IndexFold =
    { Folded =
        fun () ->
            asked.Value <- asked.Value + 1

            task {
                do! Tasks.Task.Delay delay
                write ()
            }
      Bound = TimeSpan.FromSeconds 30.0 }

[<Fact(Timeout = 30000)>]
let ``a trace is joined only once the index has folded what the run admitted`` () =
    let root = tempRoot ()
    let lines = ResizeArray()
    let asked = ref 0

    let summary =
        ingestTypeUse root (slowFold (TimeSpan.FromMilliseconds 300.0) (fun () -> indexTypeM root) asked) lines
        |> List.exactlyOne

    // Joined before the fold wrote `L.M`, the id would be unmapped (type-not-indexed) and
    // the trace incomplete for a reason that is only timing.
    test <@ asked.Value = 1 @>
    test <@ (summary.UnmappedIds, summary.Complete, summary.ReasonCounts) = (0, 1, Map.empty) @>
    test <@ projectLines lines = [ "traces: T 1/1 traced, 1 complete" ] @>

[<Fact(Timeout = 30000)>]
let ``an id the folded index does not hold is still reported unmapped`` () =
    let root = tempRoot ()
    let lines = ResizeArray()
    let asked = ref 0

    let summary =
        ingestTypeUse root (slowFold (TimeSpan.FromMilliseconds 50.0) ignore asked) lines
        |> List.exactlyOne

    test <@ (summary.UnmappedIds, summary.Complete) = (1, 0) @>
    test <@ projectLines lines = [ "traces: T 1/1 traced, 0 complete" ] @>

[<Fact(Timeout = 30000)>]
let ``an index still folding at the bound stores the project as not recorded, joining nothing`` () =
    let root = tempRoot ()
    let lines = ResizeArray()
    let never = Tasks.TaskCompletionSource()

    let index: IndexFold =
        { Folded = fun () -> never.Task
          Bound = TimeSpan.FromMilliseconds 100.0 }

    let summaries = ingestTypeUse root index lines

    test <@ List.isEmpty summaries @>
    let run = runsOf root "T" |> List.exactlyOne
    test <@ (run.Status, run.Reason) = (TraceStore.FailedToRecord, TraceRun.indexUnsettledReason index.Bound) @>

    test
        <@
            List.ofSeq lines = [ TraceRun.indexWaitLine
                                 $"traces: T not recorded — %s{TraceRun.indexUnsettledReason index.Bound}" ]
        @>

[<Fact(Timeout = 30000)>]
let ``a run of refusals stores without waiting on the index`` () =
    let root = tempRoot ()
    let lines = ResizeArray()
    let rt = runtime root RecordFullRuns Set.empty PassThrough
    let never = Tasks.TaskCompletionSource()

    let index: IndexFold =
        { Folded = fun () -> never.Task
          Bound = TimeSpan.FromMinutes 5.0 }

    TraceRun.ingestAll
        rt
        (symbolsOf root)
        index
        "run1"
        (Some "tree")
        (fun () -> Some "tree")
        [ { Project = "T"
            Decision = Untraced(Some "refused")
            Filtered = false
            CtrfPath = None } ]
        lines.Add
    |> Async.RunSynchronously

    test <@ List.ofSeq lines = [ "traces: T not recorded — refused" ] @>

[<Fact(Timeout = 30000)>]
let ``every join logs how long the index took to fold, before the project's line`` () =
    let root = tempRoot ()
    let lines = ResizeArray()
    let waitLogged = Tasks.TaskCompletionSource()

    let slow =
        slowFold (TimeSpan.FromMilliseconds 300.0) (fun () -> indexTypeM root) (ref 0)

    // The fold cannot finish before ingestion has seen it behind: a starved thread between
    // asking and checking would otherwise find it folded, and rightly log no wait.
    let behind =
        { slow with
            Folded =
                fun () ->
                    task {
                        do! waitLogged.Task
                        do! slow.Folded()
                    } }

    let log (line: string) =
        lines.Add line

        if line = TraceRun.indexWaitLine then
            waitLogged.TrySetResult() |> ignore

    ingestTypeUseLogging root behind log |> ignore

    test <@ lines.Count = 3 @>
    test <@ lines[0] = TraceRun.indexWaitLine @>
    let waitedMs = lines[1].Substring(foldLinePrefix.Length).TrimEnd('m', 's') |> int
    test <@ lines[1].StartsWith foldLinePrefix && waitedMs >= 300 @>
    test <@ lines[2] = "traces: T 1/1 traced, 1 complete" @>

// --- untracedRetry: a traced launch that verified nothing is repeated untraced ---

let private tracedIn root =
    Traced(
        { Command = ""
          Args = []
          Environment = [] },
        sessionOf root "T" oneRowManifest
    )

let private crashed = FsHotWatch.ProcessHelper.Failed(134, ProcessOutput.Drained "")

let private passed = FsHotWatch.ProcessHelper.Succeeded(ProcessOutput.Drained "")

let private timedOut =
    FsHotWatch.ProcessHelper.TimedOut(TimeSpan.FromSeconds 300.0, ProcessOutput.Drained "", KillOutcome.Killed)

[<Fact>]
let ``a traced launch that failed without a report is repeated untraced`` () =
    test
        <@
            TraceRun.untracedRetry (tracedIn (tempRoot ())) crashed false = Some(
                RelaunchUntraced TraceRun.RelaunchReason
            )
        @>

[<Fact>]
let ``a traced launch that timed out without a report is kept, not repeated`` () =
    test <@ TraceRun.untracedRetry (tracedIn (tempRoot ())) timedOut false = Some(KeepTimeout TraceRun.TimeoutReason) @>

[<Fact>]
let ``a traced launch that reported, or succeeded, is never repeated`` () =
    let traced = tracedIn (tempRoot ())
    // A reported failure is the run's verdict; re-running it could re-roll a flake green.
    test <@ TraceRun.untracedRetry traced crashed true = None @>
    test <@ TraceRun.untracedRetry traced timedOut true = None @>
    test <@ TraceRun.untracedRetry traced passed false = None @>

[<Fact>]
let ``an untraced launch is never repeated`` () =
    test <@ TraceRun.untracedRetry (Untraced(Some "x")) crashed false = None @>
    test <@ TraceRun.untracedRetry (Untraced None) timedOut false = None @>

/// `launchTracedOrNot` over a stand-in launch that answers each attempt from `outcomes`
/// in order, recording the decision each attempt launched under.
let private launchWith (decision: TraceDecision) (reportExists: bool) (outcomes: ProcessOutcome list) =
    let launched = ResizeArray<TraceDecision>()
    let announced = ResizeArray<TracedFailure>()
    let remaining = Collections.Generic.Queue<ProcessOutcome>(outcomes)

    let outcome, stored =
        TraceRun.launchTracedOrNot decision (fun () -> reportExists) announced.Add (fun d ->
            async {
                launched.Add d
                return remaining.Dequeue()
            })
        |> Async.RunSynchronously

    outcome, stored, List.ofSeq launched, List.ofSeq announced

[<Fact>]
let ``a traced launch that timed out is launched once, its timeout stands, and a refusal is stored`` () =
    let traced = tracedIn (tempRoot ())
    let outcome, stored, launched, announced = launchWith traced false [ timedOut ]

    test <@ outcome = timedOut @>
    test <@ launched = [ traced ] @>
    test <@ stored = Untraced(Some TraceRun.TimeoutReason) @>
    test <@ announced = [ KeepTimeout TraceRun.TimeoutReason ] @>

[<Fact>]
let ``a traced launch that crashed without a report is relaunched untraced, and the relaunch stands`` () =
    let traced = tracedIn (tempRoot ())

    let outcome, stored, launched, announced =
        launchWith traced false [ crashed; passed ]

    test <@ outcome = passed @>
    test <@ launched = [ traced; Untraced(Some TraceRun.RelaunchReason) ] @>
    test <@ stored = Untraced(Some TraceRun.RelaunchReason) @>
    test <@ announced = [ RelaunchUntraced TraceRun.RelaunchReason ] @>

[<Fact>]
let ``a traced launch that reported is launched once and stays traced`` () =
    let traced = tracedIn (tempRoot ())
    let outcome, stored, launched, announced = launchWith traced true [ timedOut ]

    test <@ outcome = timedOut @>
    test <@ launched = [ traced ] @>
    test <@ stored = traced @>
    test <@ List.isEmpty announced @>
