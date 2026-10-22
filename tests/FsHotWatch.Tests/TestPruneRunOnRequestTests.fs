/// A daemon configured with `tests.runOn: "request"` launches no test run on its own.
///
/// Its builds still fold what they change into the debt, so nothing is forgotten: the
/// run a build leaves owed is HELD, not dropped. It launches when a command asks for it
/// (`set-scope owed`, which `check` sends; `set-scope full`, which `confirm` sends; or
/// `run-tests`), and once that run has executed, the session holds again. Without this a
/// daemon with no full-suite baseline widens the first run an edit provokes to every
/// configured project, unasked.
///
/// These drive the real BuildCompleted → run → TestsFinished flow with `sh` runners that
/// leave a receipt per launch, so what ran is read from disk.
module FsHotWatch.Tests.TestPruneRunOnRequestTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A runner that appends one line to `runs` per launch and fails while `failFlag` exists.
let private runner (tmpDir: string) (project: string) (failedTest: string) : TestConfig =
    let runs = Path.Combine(tmpDir, $"%s{project}-runs")
    let failFlag = Path.Combine(tmpDir, $"%s{project}-fail")

    { Project = project
      Command = "sh"
      Args =
        $"-c \"echo run >> %s{runs}; if [ -f %s{failFlag} ]; then echo 'failed %s{failedTest} (1ms)'; exit 1; fi; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      AlwaysClasses = [] }

let private runsOf (tmpDir: string) (project: string) : int =
    let path = Path.Combine(tmpDir, $"%s{project}-runs")

    if File.Exists path then
        File.ReadAllLines path |> Array.length
    else
        0

let private setFailing (tmpDir: string) (project: string) (failing: bool) =
    let flag = Path.Combine(tmpDir, $"%s{project}-fail")

    if failing then
        File.WriteAllText(flag, "")
    elif File.Exists flag then
        File.Delete flag

let private twoProjectDb (tmpDir: string) : string =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
    PendingQueueHelpers.seedCoveredSymbol db "Lib.bar" "Bar.fs" "P2" "P2Tests" "barTest"
    dbPath

/// A daemon session that runs tests only on request: the mode `tests.runOn: "request"`
/// starts a daemon in.
let private onRequestSession (tmpDir: string) (dbPath: string) (configs: TestConfig list) =
    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

    let handler =
        createWithTracesIn
            []
            (TestMode.startingWith false)
            None
            None
            Set.empty
            Map.empty
            (fun () -> Map.empty)
            dbPath
            TestPrune.Domain.WalkPolicy.FollowAll
            tmpDir
            (Some configs)
            None
            None
            None
            None
            []

    host.RegisterHandler(handler)
    host

let private settleAfter (host: PluginHost) (provoke: unit -> unit) =
    let await = beginAwaitNextTerminal host "test-prune"
    provoke ()
    test <@ await.Wait(TimeSpan.FromSeconds 20.0) @>
    waitForQuiescent host 10000

let private build (host: PluginHost) =
    settleAfter host (fun () -> host.EmitBuildCompleted(BuildSucceeded))

let private requestOwed (host: PluginHost) =
    settleAfter host (fun () ->
        host.RunCommand("set-scope", [| """{"scope":"owed"}""" |])
        |> Async.RunSynchronously
        |> ignore)

let private baselineOf (tmpDir: string) : FullSuiteBaseline.Baseline option =
    match FullSuiteBaseline.load tmpDir with
    | FullSuiteBaseline.LoadedBaseline.Loaded b -> b
    | FullSuiteBaseline.LoadedBaseline.Unreadable reason -> failwith $"baseline unreadable: {reason}"

[<Fact(Timeout = 60000)>]
let ``with no baseline, a build launches nothing until a command asks, then the asked run earns the baseline`` () =
    withTempDir "on-request-cold" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        let host = onRequestSession tmpDir dbPath configs

        // The build a daemon's first scan (or an agent's first edit) produces. With no
        // baseline, a session that launches owed runs would widen this to every project.
        build host
        test <@ runsOf tmpDir "P1" = 0 && runsOf tmpDir "P2" = 0 @>
        test <@ (baselineOf tmpDir).IsNone @>

        // `check` asks for the owed run: it launches at once, over the tree already built.
        requestOwed host
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        match baselineOf tmpDir with
        | Some b -> test <@ b.Projects = Set.ofList [ "P1"; "P2" ] @>
        | None -> failwith "the requested run executed every project and must earn the baseline")

[<Fact(Timeout = 60000)>]
let ``once the requested run has executed, the session holds again`` () =
    withTempDir "on-request-holds" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        // A red in the requested run is quarantined: a session that launches owed runs
        // re-selects it on the very next build, so that build is owed a run.
        setFailing tmpDir "P2" true
        let host = onRequestSession tmpDir dbPath configs
        build host
        requestOwed host
        test <@ runsOf tmpDir "P2" = 1 @>

        build host
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        // And the next request runs the quarantined red.
        requestOwed host
        test <@ runsOf tmpDir "P2" = 2 @>)

[<Fact(Timeout = 60000)>]
let ``a request with nothing held launches nothing, and the next build's run launches`` () =
    withTempDir "on-request-armed" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        let host = onRequestSession tmpDir dbPath configs

        // No build yet: there is no tree to run against, so the request only arms.
        host.RunCommand("set-scope", [| """{"scope":"owed"}""" |])
        |> Async.RunSynchronously
        |> ignore

        waitForQuiescent host 10000
        test <@ runsOf tmpDir "P1" = 0 && runsOf tmpDir "P2" = 0 @>

        // The build `check`'s scan provokes is the one the request was for.
        build host
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>)

[<Fact(Timeout = 60000)>]
let ``a requested run whose impact selection fails reports the failure and launches nothing`` () =
    withTempDir "on-request-flush-fails" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir
        // Something owed, so the request's flush has a queue to select from.
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        // The index answers the build's flush, then fails the request's.
        let failing = ref false

        let queries (db: Database) : ImpactQueries =
            let real = ImpactQueries.ofDatabase db

            let guarded (query: string list -> 'a) =
                fun seeds ->
                    if failing.Value then
                        failwith "simulated impact index failure"
                    else
                        query seeds

            { real with
                AffectedTests = guarded real.AffectedTests
                CoveringProjectsBySeed = guarded real.CoveringProjectsBySeed }

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        host.RegisterHandler(
            createWithQueries
                queries
                (TimeSpan.FromMinutes 5.0)
                None
                (fun () -> Map.empty)
                dbPath
                TestPrune.Domain.WalkPolicy.FollowAll
                tmpDir
                (Some configs)
                None
                None
                None
                None
                []
                None
                (TestMode.startingWith false)
        )

        build host
        test <@ runsOf tmpDir "P1" = 0 && runsOf tmpDir "P2" = 0 @>

        failing.Value <- true
        requestOwed host
        test <@ runsOf tmpDir "P1" = 0 && runsOf tmpDir "P2" = 0 @>

        match host.GetStatus("test-prune") with
        | Some(Failed(error, _, _)) -> test <@ error.Contains "simulated impact index failure" @>
        | other -> failwithf "a failed selection must fail the requested run, got %A" other)

[<Fact(Timeout = 60000)>]
let ``a session that already launches owed runs is left as it is by a request`` () =
    // `check` asks every daemon; one under `tests.runOn: "change"` has nothing to arm.
    withTempDir "on-request-eager" (fun tmpDir ->
        let dbPath = twoProjectDb tmpDir

        let configs =
            [ runner tmpDir "P1" "P1Tests.fooTest"; runner tmpDir "P2" "P2Tests.barTest" ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create dbPath tmpDir (Some configs) None None None None [])

        build host
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>

        let reply =
            host.RunCommand("set-scope", [| """{"scope":"owed"}""" |])
            |> Async.RunSynchronously

        waitForQuiescent host 10000
        test <@ reply = Some """{"scope":"owed"}""" @>
        test <@ runsOf tmpDir "P1" = 1 && runsOf tmpDir "P2" = 1 @>)
