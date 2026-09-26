/// Recording per-test traces inside a test run: which projects are traced and how they
/// launch (`TraceRun.decide`), and how a finished run's traces or refusals are stored
/// (`TraceRun.ingestAll`). A refusal runs the project untraced and is stored with its
/// reason; nothing here can throw into the run or change its verdict.
module FsHotWatch.Tests.TestPruneTracesTests

open System
open System.IO
open Xunit
open Swensen.Unquote
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
      Mode = mode }

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

    match TraceRun.decide rt (project root "T") (Path.Combine(root, "run")) [] with
    | Untraced(Some reason) -> test <@ reason.Contains "no build output" @>
    | other -> failwith $"%A{other}"

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
    TraceRun.ingestAll rt (symbolsOf root) "run1" (Some "tree") (fun () -> Some "tree") runs lines.Add

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

    test <@ List.ofSeq lines = [ "traces: T not recorded — no build output under x" ] @>
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

    test <@ List.ofSeq lines = [ "traces: T 1/1 traced, 1 complete" ] @>
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

    test <@ List.ofSeq lines = [ "traces: T 0/0 traced, 0 complete (no CTRF outcomes)" ] @>

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
            lines
            |> Seq.exactlyOne
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

    test <@ List.ofSeq lines = [ "traces: T not recorded — recorder-no-output" ] @>
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

    test <@ List.ofSeq lines = [ "traces: T 1/1 traced, 0 complete (the input tree moved during the run)" ] @>
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

    test <@ List.ofSeq lines = [ "traces: T not recorded — trace ingestion failed: boom" ] @>
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
            lines
            |> Seq.exactlyOne
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

    test
        <@
            List.ofSeq lines = [ "traces: U not recorded — trace storage failed: disk full"
                                 "traces: T not recorded — refused" ]
        @>

    test <@ (runsOf root "T" |> List.exactlyOne).Status = TraceStore.Refused @>

// --- untracedRetry: a traced launch that verified nothing is repeated untraced ---

[<Fact>]
let ``a traced launch that failed without a report is repeated untraced`` () =
    let root = tempRoot ()

    let traced =
        Traced(
            { Command = ""
              Args = []
              Environment = [] },
            sessionOf root "T" oneRowManifest
        )

    test
        <@
            TraceRun.untracedRetry traced false false = Some
                "the traced launch failed without writing a test report; the project re-ran untraced"
        @>

[<Fact>]
let ``a traced launch that reported, or succeeded, is never repeated`` () =
    let root = tempRoot ()

    let traced =
        Traced(
            { Command = ""
              Args = []
              Environment = [] },
            sessionOf root "T" oneRowManifest
        )
    // A reported failure is the run's verdict; re-running it could re-roll a flake green.
    test <@ TraceRun.untracedRetry traced false true = None @>
    test <@ TraceRun.untracedRetry traced true false = None @>

[<Fact>]
let ``an untraced launch is never repeated`` () =
    test <@ TraceRun.untracedRetry (Untraced(Some "x")) false false = None @>
