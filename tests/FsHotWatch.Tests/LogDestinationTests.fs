module FsHotWatch.Tests.LogDestinationTests

// Where a process's log lines go, and the sentences that send a reader there.
//
// A diagnostic that says "inspect <file>" is only worth printing if the file exists and
// holds the lines it is talking about. An in-process host (`--run-once`, a `test-rerun`
// with no baseline and no daemon) used to install no file sink at all, while the WEDGED,
// DEAD and OVERRAN messages hard-coded `logs/daemon.log` — so the message named a file
// that was never written, under a directory the configuration may not even use.

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Cli
open FsHotWatch.Cli.Program
open FsHotWatch.Tests.TestHelpers

/// Run `body` in its own execution context, so a sink it installs ends with it.
let private isolated (body: unit -> 'a) : 'a =
    Task.Run(fun () -> body ()).GetAwaiter().GetResult()

let private stuckHandler (name: string) (release: ManualResetEventSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                release.Wait()
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

let private overrunningHandler (name: string) (entered: ManualResetEventSlim) (release: ManualResetEventSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state _event ->
            async {
                use _bound =
                    ctx.DeclareBoundedWork "cold impact attribution" (TimeSpan.FromSeconds 1.0)

                entered.Set()
                release.Wait()
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

/// The wedge failure a host with `handler` registered raises, read in the caller's context.
let private wedgeFailure (register: PluginHost -> unit) (afterEmit: unit -> unit) : TimeoutException =
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    register host
    host.EmitBuildCompleted(BuildSucceeded)
    afterEmit ()

    Assert.Throws<TimeoutException>(fun () ->
        Daemon.waitForAllTerminalCore
            host
            (TimeSpan.FromSeconds 30.0)
            (TimeSpan.FromMilliseconds 300.0)
            CancellationToken.None
        |> fun t -> t.GetAwaiter().GetResult())

let private stuckWedge () =
    use release = new ManualResetEventSlim(false)

    try
        wedgeFailure (fun host -> host.RegisterHandler(stuckHandler "wedged-plugin" release)) (fun () ->
            Thread.Sleep 200)
    finally
        release.Set()

let private overranWedge () =
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    try
        wedgeFailure
            (fun host -> host.RegisterHandler(overrunningHandler "overrunning-plugin" entered release))
            (fun () -> Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "the declared work must start"))
    finally
        release.Set()

// --- Logging: the sink knows where its lines land ---

[<Fact>]
let ``with no sink installed the log pointer is stderr`` () =
    isolated (fun () -> test <@ Logging.logPointer () = "stderr above" @>)

[<Fact>]
let ``a file sink's pointer is its file, and its lines are in it`` () =
    withTempDir "log-pointer-file" (fun dir ->
        let path = Path.Combine(dir, "logs", "daemon.log")

        isolated (fun () ->
            use _ = Logging.installSink (Logging.fileSink path Logging.LogLevel.Info)
            Logging.info "probe" "a line for the file"
            test <@ Logging.logPointer () = path @>)

        test <@ File.ReadAllText(path).Contains "a line for the file" @>)

[<Fact>]
let ``a tee sink writes the file and names it`` () =
    withTempDir "log-pointer-tee" (fun dir ->
        let path = Path.Combine(dir, "custom-logs", "daemon.log")

        isolated (fun () ->
            use _ = Logging.installSink (Logging.teeSink path Logging.LogLevel.Info)
            Logging.info "probe" "teed line"
            test <@ Logging.logPointer () = path @>)

        test <@ File.ReadAllText(path).Contains "teed line" @>)

[<Fact>]
let ``a redirected-stderr sink names the file its launcher redirected into`` () =
    isolated (fun () ->
        use _ =
            Logging.installSink (Logging.stderrInto "/repo/logs/daemon.log" Logging.LogLevel.Info)

        test <@ Logging.logPointer () = "/repo/logs/daemon.log" @>)

[<Fact>]
let ``a redirected-stderr sink writes only stderr, never the file it names`` () =
    withTempDir "stderr-into" (fun dir ->
        let path = Path.Combine(dir, "logs", "daemon.log")
        (Logging.stderrInto path Logging.LogLevel.Info).Write "redirected line"
        test <@ not (File.Exists path) @>)

[<Fact>]
let ``a sink that holds no file points at stderr`` () =
    isolated (fun () ->
        use _ =
            Logging.installSink
                { Write = ignore
                  Level = Logging.LogLevel.Info
                  File = None }

        test <@ Logging.logPointer () = "stderr above" @>)

// --- Daemon: the wedge messages name the log they were actually given ---

[<Fact(Timeout = 60_000)>]
let ``a WEDGED failure names the installed log file, which holds the message`` () =
    withTempDir "wedge-names-log" (fun dir ->
        let path = Path.Combine(dir, "elsewhere", "daemon.log")

        let failure =
            isolated (fun () ->
                use _ = Logging.installSink (Logging.fileSink path Logging.LogLevel.Info)
                stuckWedge ())

        test <@ failure.Message.Contains "WEDGED" @>
        test <@ failure.Message.Contains path @>
        test <@ not (failure.Message.Contains "logs/daemon.log") @>
        test <@ File.Exists path @>
        test <@ File.ReadAllText(path).Contains "WEDGED" @>)

[<Fact(Timeout = 60_000)>]
let ``a WEDGED failure with no file sink names stderr`` () =
    let failure = isolated stuckWedge

    test <@ failure.Message.Contains "stderr above" @>
    test <@ not (failure.Message.Contains "daemon.log") @>

[<Fact(Timeout = 60_000)>]
let ``an OVERRAN failure names the installed log file`` () =
    withTempDir "overran-names-log" (fun dir ->
        let path = Path.Combine(dir, "logs", "daemon.log")

        let failure =
            isolated (fun () ->
                use _ = Logging.installSink (Logging.fileSink path Logging.LogLevel.Info)
                overranWedge ())

        test <@ failure.Message.Contains "OVERRAN" @>
        test <@ failure.Message.Contains path @>
        test <@ File.Exists path @>)

[<Fact(Timeout = 60_000)>]
let ``an OVERRAN failure with no file sink names stderr`` () =
    let failure = isolated overranWedge

    test <@ failure.Message.Contains "OVERRAN" @>
    test <@ failure.Message.Contains "stderr above" @>
    test <@ not (failure.Message.Contains "daemon.log") @>

[<Fact>]
let ``a DEAD failure names the log it is given`` () =
    let message = Daemon.deadPluginMessage "/w/logs/daemon.log" "build" (exn "boom")

    test <@ message.Contains "DEAD" @>
    test <@ message.Contains "boom" @>
    test <@ message.Contains "See /w/logs/daemon.log for the full stack" @>

    let onStderr = Daemon.deadPluginMessage "stderr above" "build" (exn "boom")
    test <@ onStderr.Contains "See stderr above for the full stack" @>

// --- CLI: which process writes the log file, and where ---

let private configWithLogDir (root: string) (logDir: string) =
    { defaultTestConfig () with
        LogDir = logDir }

[<Fact>]
let ``the daemon log file resolves a relative logDir against the repo root`` () =
    test <@ DaemonConfig.DaemonLog.file "/r" "logs" = Path.Combine("/r", "logs", "daemon.log") @>
    test <@ DaemonConfig.DaemonLog.file "/r" "/var/fshw" = Path.Combine("/var/fshw", "daemon.log") @>

[<Fact>]
let ``an in-process test-rerun writes the configured daemon log`` () =
    withTempDir "inproc-rerun-log" (fun root ->
        let config = configWithLogDir root "custom-logs"
        let expected = Path.Combine(root, "custom-logs", "daemon.log")

        isolated (fun () ->
            match processLogSink (TestRerun [ FilterClass "*Foo*" ]) root config None with
            | None -> failwith "an in-process host must install a file log"
            | Some sink ->
                use _ = Logging.installSink sink
                Logging.info "probe" "in-process rerun line"
                test <@ Logging.logPointer () = expected @>)

        test <@ File.Exists expected @>
        test <@ File.ReadAllText(expected).Contains "in-process rerun line" @>)

[<Fact>]
let ``check --run-once and confirm --run-once write the configured daemon log`` () =
    withTempDir "inproc-runonce-log" (fun root ->
        let config = configWithLogDir root "logs"

        for command in [ Check [ RunOnce ]; Confirm [ ConfirmFlag.RunOnce ] ] do
            test
                <@
                    (processLogSink command root config None |> Option.bind (fun s -> s.File)) = Some(
                        DaemonConfig.DaemonLog.file root "logs"
                    )
                @>)

[<Fact>]
let ``a launched daemon names the file its launcher redirected stderr into, without writing it twice`` () =
    let config = configWithLogDir "/r" "logs"

    match processLogSink Start "/r" config (Some "/r/logs/daemon.log") with
    | Some sink -> test <@ sink.File = Some "/r/logs/daemon.log" @>
    | None -> failwith "a launched daemon must name its log"

    // Started by hand, nobody redirected anything: its lines are on its stderr.
    test <@ processLogSink Start "/r" config None |> Option.isNone @>

[<Fact>]
let ``a client command talking to a daemon installs no log of its own`` () =
    test
        <@
            processLogSink (Check []) "/r" (configWithLogDir "/r" "logs") None
            |> Option.isNone
        @>

[<Fact>]
let ``the daemon launch line tells the daemon which file its stderr is redirected into`` () =
    let line = DetachedLaunch.daemonShellCommand "/bin/fshw" "" "" "/logs/a b.log"

    test <@ line.StartsWith $"%s{DaemonConfig.DaemonLog.LaunchedEnvVar}='/logs/a b.log' nohup " @>

// --- Every other sentence that sends a reader to the log ---

[<Fact>]
let ``the core log pointers name the installed log file`` () =
    withTempDir "core-pointers" (fun dir ->
        let path = Path.Combine(dir, "elsewhere", "daemon.log")

        let firing: OperationWatchdog.HeapValveFiring =
            { Count = 1
              At = DateTime.UtcNow
              HeapBefore = 0L
              HeapAfter = 0L
              QuietFor = TimeSpan.Zero }

        let loadingFailed =
            ProjectModel.ofCompleted
                2L
                { Discovered = 4
                  Loaded = 0
                  OptionsMapped = 0
                  Registered = 0 }

        let messages =
            isolated (fun () ->
                use _ = Logging.installSink (Logging.fileSink path Logging.LogLevel.Info)

                [ Daemon.totalDiscoveryFailure 3 0 |> Option.defaultValue ""
                  OperationWatchdog.recoveryAction ()
                  OperationWatchdog.heapValveReport firing
                  (ProjectModel.UnavailableException loadingFailed).Message ])

        for message in messages do
            test <@ message.Contains path @>)

[<Fact>]
let ``the CLI pointers name the daemon log they are given`` () =
    let given = "/configured/daemon.log"

    let messages =
        [ DaemonStartupFailure.describeLaunchFailure given None
          ipcErrorHint given (TimeoutException "slow")
          Verdict.CheckProse.noNewRun given "the run was refused"
          (IpcParsing.ProjectModelReading.describeUnavailable
              given
              (IpcParsing.ProjectModelReading.Observed ProjectModel.Observation.Unobserved)
           |> Option.defaultValue "") ]

    for message in messages do
        test <@ message.Contains given @>

[<Fact>]
let ``a repo's daemon log resolves through the configured logDir`` () =
    withTempDir "for-repo-log" (fun root ->
        File.WriteAllText(Path.Combine(root, ".fshw.json"), """{ "logDir": "var/fshw" }""")
        test <@ DaemonConfig.DaemonLog.forRepo root = DaemonConfig.DaemonLog.file root "var/fshw" @>)

/// Every `.fs` file under `src/`, as (path relative to the repo, text).
let private sourceFiles () =
    let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
    let src = Path.Combine(repo, "src")

    filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.fs" src
    |> Seq.map (fun path -> Path.GetRelativePath(repo, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/bin/" || relative.Contains "/obj/"))
    |> Seq.map (fun (relative, path) -> relative, File.ReadAllText path)
    |> Seq.toList

[<Fact>]
let ``no source outside the resolver spells out a daemon log path`` () =
    // A sentence that hard-codes where the log is guesses at someone else's `logDir`,
    // and names a file that does not exist wherever no daemon wrote one. The path has
    // one owner: `DaemonConfig.DaemonLog` in the CLI, `Logging.logPointer` in core.
    let resolver = "src/FsHotWatch.Cli/DaemonConfig.fs"
    let files = sourceFiles ()

    // The scan saw the tree, so an empty result means something.
    test <@ files |> List.exists (fun (relative, _) -> relative = resolver) @>

    let offenders =
        files
        |> List.filter (fun (relative, text) -> relative <> resolver && text.Contains "logs/daemon.log")
        |> List.map fst

    test <@ List.isEmpty offenders @>
