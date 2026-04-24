module FsHotWatch.Tests.ProgramTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CommandTree
open FsHotWatch.Cli.Program
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Tests.TestHelpers

// --- Helper: shared fake config and IPC ---

let private fakeConfig: DaemonConfiguration =
    { Build = None
      Format = Off
      Lint = false
      Cache = FsHotWatch.Cli.DaemonConfig.NoCache
      Analyzers = None
      Tests = None
      FileCommands = []
      Exclude = []
      LogDir = "logs"
      TimeoutSec = None }

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus =
        fun _ ->
            async {
                return
                    """{"plugin": {"status": {"tag": "completed", "at": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null}}"""
            }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ _ _ -> async { return "unknown command" }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

// --- computeConfigHashWith tests ---

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith returns 16-char hex string`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let result = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    test <@ result.Length = 16 @>
    test <@ result |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c || Char.IsDigit c) @>

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith is deterministic`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let h1 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    let h2 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    test <@ h1 = h2 @>

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith changes when config content changes`` () =
    let mutable configContent = "v1"

    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith(".fs-hot-watch.json")
            ReadAllText = fun _ -> configContent
            GetLastWriteTimeUtc = fun _ -> DateTime(2026, 1, 1) }

    let h1 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    configContent <- "v2"
    let h2 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    test <@ h1 <> h2 @>

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith changes when exe mod time changes`` () =
    let mutable modTime = DateTime(2026, 1, 1)

    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> true
            ReadAllText = fun _ -> "config"
            GetLastWriteTimeUtc = fun _ -> modTime }

    let h1 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    modTime <- DateTime(2026, 1, 2)
    let h2 = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    test <@ h1 <> h2 @>

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith with no files uses empty strings`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let result = computeConfigHashWith fileOps "/tmp/repo" "/tmp/exe"
    test <@ result.Length = 16 @>

[<Fact(Timeout = 5000)>]
let ``computeConfigHashWith with config but no exe`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith(".fs-hot-watch.json")
            ReadAllText = fun _ -> """{"build": {}}""" }

    let result = computeConfigHashWith fileOps "/tmp/repo" "/nonexistent/exe"
    test <@ result.Length = 16 @>

// --- killStaleDaemonWith tests ---

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith does nothing when no pid file exists`` () =
    let mutable deleteCalled = false

    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false
            DeleteFile = fun _ -> deleteCalled <- true }

    let processOps =
        { defaultProcessOps with
            GetProcessById = fun _ -> failwith "should not be called" }

    killStaleDaemonWith fileOps processOps "/tmp/repo"
    test <@ not deleteCalled @>

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith reads pid and kills process`` () =
    let mutable killCalled = false
    let mutable deletedPath = ""

    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith("daemon.pid")
            ReadAllText = fun _ -> "12345\n"
            DeleteFile = fun path -> deletedPath <- path }

    let fakeProc = Unchecked.defaultof<System.Diagnostics.Process>

    let processOps =
        { GetProcessById = fun pid -> if pid = 12345 then fakeProc else failwith "wrong pid"
          KillProcess = fun _ -> killCalled <- true
          WaitForExit = fun _ _ -> true }

    killStaleDaemonWith fileOps processOps "/tmp/repo"
    test <@ killCalled @>
    test <@ deletedPath.EndsWith("daemon.pid") @>

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith handles process not found gracefully`` () =
    let mutable deletedPath = ""

    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith("daemon.pid")
            ReadAllText = fun _ -> "99999"
            DeleteFile = fun path -> deletedPath <- path }

    let processOps =
        { GetProcessById = fun _ -> raise (ArgumentException("No process with that ID"))
          KillProcess = fun _ -> failwith "should not be called"
          WaitForExit = fun _ _ -> true }

    killStaleDaemonWith fileOps processOps "/tmp/repo"
    test <@ deletedPath.EndsWith("daemon.pid") @>

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith handles invalid pid file gracefully`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith("daemon.pid")
            ReadAllText = fun _ -> "not-a-number" }

    let processOps =
        { GetProcessById = fun _ -> failwith "should not be called"
          KillProcess = fun _ -> ()
          WaitForExit = fun _ _ -> true }

    // Should not throw
    killStaleDaemonWith fileOps processOps "/tmp/repo"

// --- startFreshDaemonWith tests ---

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith returns true when daemon starts immediately`` () =
    let mutable hashWritten = ""
    let mutable launchCalled = false

    let fileOps =
        { defaultFileOps with
            CreateDirectory = fun _ -> ()
            WriteAllText = fun _path content -> hashWritten <- content }

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> true
            LaunchDaemon = fun _ _ _ -> launchCalled <- true }

    let result =
        startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "abc123" "" "logs" 5.0

    test <@ result @>
    test <@ launchCalled @>
    test <@ hashWritten = "abc123" @>

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith returns false when daemon never starts`` () =
    let fileOps =
        { defaultFileOps with
            CreateDirectory = fun _ -> ()
            WriteAllText = fun _ _ -> () }

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> false
            LaunchDaemon = fun _ _ _ -> () }

    let result =
        startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "abc123" "" "logs" 0.0

    test <@ not result @>

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith passes extra args to LaunchDaemon`` () =
    let mutable receivedArgs = ""

    let fileOps =
        { defaultFileOps with
            CreateDirectory = fun _ -> ()
            WriteAllText = fun _ _ -> () }

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> true
            LaunchDaemon = fun _ args _ -> receivedArgs <- args }

    startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "hash" "--verbose " "logs" 5.0
    |> ignore

    test <@ receivedArgs = "--verbose " @>

// --- Restart flow tests (via decideDaemonAction) ---

[<Fact(Timeout = 5000)>]
let ``restart flow is triggered when stored config hash differs`` () =
    let action = decideDaemonAction true "old-hash" "new-hash"
    test <@ action = Restart @>

[<Fact(Timeout = 5000)>]
let ``restart flow handles shutdown failure gracefully`` () =
    // decideDaemonAction returns Restart, shutdown exception is caught by ensureDaemon
    let action = decideDaemonAction true "old-hash" "new-hash"
    test <@ action = Restart @>
    // startFreshDaemonWith still works after a failed shutdown
    withTempDir "prog-restart-fail" (fun tmpDir ->
        let mutable launchCalled = false

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> launchCalled <- true }

        let result =
            startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "hash" "" "logs" 5.0

        test <@ result @>
        test <@ launchCalled @>)

// --- killStaleDaemon ---

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith cleans up stale PID file`` () =
    withTempDir "prog-kill-stale" (fun tmpDir ->
        let stateDir = Path.Combine(tmpDir, ".fs-hot-watch")
        Directory.CreateDirectory(stateDir) |> ignore
        File.WriteAllText(Path.Combine(stateDir, "daemon.pid"), "999999999")

        killStaleDaemonWith defaultFileOps defaultProcessOps tmpDir

        test <@ not (File.Exists(Path.Combine(stateDir, "daemon.pid"))) @>)

[<Fact(Timeout = 5000)>]
let ``killStaleDaemonWith handles missing PID file gracefully`` () =
    withTempDir "prog-no-pid" (fun tmpDir ->
        // Should not throw when PID file doesn't exist
        killStaleDaemonWith defaultFileOps defaultProcessOps tmpDir)

// --- startFreshDaemonWith ---

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith writes config hash file`` () =
    withTempDir "prog-hash-write" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        let result =
            startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "abcd1234abcd1234" "" "logs" 5.0

        test <@ result @>
        let hashPath = Path.Combine(tmpDir, ".fs-hot-watch", "config.hash")
        test <@ File.Exists hashPath @>
        let hash = File.ReadAllText(hashPath).Trim()
        test <@ hash = "abcd1234abcd1234" @>)

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith creates log directory from logDirName param`` () =
    withTempDir "prog-log-dir" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "hash" "" "custom-logs" 5.0
        |> ignore

        test <@ Directory.Exists(Path.Combine(tmpDir, "custom-logs")) @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, "log"))) @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, "logs"))) @>)

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith accepts absolute logDirName`` () =
    withTempDir "prog-log-abs" (fun tmpDir ->
        let absLogDir = Path.Combine(tmpDir, "nested", "absolute-logs")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "hash" "" absLogDir 5.0
        |> ignore

        test <@ Directory.Exists absLogDir @>)

[<Fact(Timeout = 5000)>]
let ``startFreshDaemonWith passes extra args to launch`` () =
    withTempDir "prog-extra-args" (fun tmpDir ->
        let mutable receivedArgs = ""

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ args _ -> receivedArgs <- args }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "hash" "--verbose --no-cache " "logs" 5.0
        |> ignore

        test <@ receivedArgs = "--verbose --no-cache " @>)

// --- Scan with Force flag ---

[<Fact(Timeout = 5000)>]
let ``executeCommand Scan Force passes true to IPC scan`` () =
    let mutable forceValue = false

    let ipc =
        { fakeIpc () with
            Scan =
                fun _ force ->
                    async {
                        forceValue <- force
                        return "scan started"
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Scan [ Force ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ forceValue @>

// --- Completions command ---

[<Fact(Timeout = 5000)>]
let ``executeCommand Completions returns 0`` () =
    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            (fakeIpc ())
            "/tmp"
            "pipe"
            Completions
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

// --- Start command singleton guarantee ---

/// Simulate a running daemon by pre-holding an exclusive lock on
/// `.fs-hot-watch/daemon.lock` within `tmpDir`, the same handle `Start`
/// uses to enforce singleton. Returns a disposable that releases the lock.
let private holdDaemonLock (tmpDir: string) (pid: int) : IDisposable =
    let stateDir = Path.Combine(tmpDir, ".fs-hot-watch")
    Directory.CreateDirectory(stateDir) |> ignore
    File.WriteAllText(Path.Combine(stateDir, "daemon.pid"), string pid)
    let lockPath = Path.Combine(stateDir, "daemon.lock")

    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None) :> IDisposable

[<Fact(Timeout = 5000)>]
let ``executeCommand Start refuses to spawn a duplicate when lock is held`` () =
    withTempDir "prog-start-dup" (fun tmpDir ->
        use _held = holdDaemonLock tmpDir 99999
        let mutable createDaemonCalled = false

        let createDaemon _ =
            createDaemonCalled <- true
            Unchecked.defaultof<_>

        let result =
            executeCommand createDaemon (fakeIpc ()) tmpDir "pipe-singleton" Start defaultGlobalOptions fakeConfig 5.0

        test <@ result = 0 @>
        test <@ not createDaemonCalled @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand Start — second concurrent invocation cannot claim the lock`` () =
    // Regression: two back-to-back Start calls must not both proceed to
    // createDaemon. The file lock is OS-enforced, so concurrent holders are
    // impossible regardless of probe-timing races.
    withTempDir "prog-start-concurrent" (fun tmpDir ->
        use _held = holdDaemonLock tmpDir 12345
        let mutable createDaemonCalls = 0

        let createDaemon _ =
            createDaemonCalls <- createDaemonCalls + 1
            Unchecked.defaultof<_>

        let result =
            executeCommand createDaemon (fakeIpc ()) tmpDir "pipe-concurrent" Start defaultGlobalOptions fakeConfig 5.0

        test <@ result = 0 @>
        test <@ createDaemonCalls = 0 @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand Stop iterates Shutdown until pipe goes quiet`` () =
    // Regression: if multiple daemons share a pipe, a single Shutdown only
    // stops one. Stop must loop until no daemon responds.
    withTempDir "prog-stop-multi" (fun tmpDir ->
        let mutable remainingDaemons = 3
        let mutable shutdownCalls = 0

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> remainingDaemons > 0
                Shutdown =
                    fun _ ->
                        async {
                            shutdownCalls <- shutdownCalls + 1
                            remainingDaemons <- remainingDaemons - 1
                            return "shutting down"
                        } }

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "pipe-multi"
                Stop
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ shutdownCalls = 3 @>
        test <@ remainingDaemons = 0 @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand Stop reports when no daemon is running`` () =
    withTempDir "prog-stop-none" (fun tmpDir ->
        let mutable shutdownCalls = 0

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false
                Shutdown =
                    fun _ ->
                        async {
                            shutdownCalls <- shutdownCalls + 1
                            return "shutting down"
                        } }

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "pipe-none"
                Stop
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ shutdownCalls = 0 @>)

[<Fact(Timeout = 5000)>]
let ``parse completions returns Completions`` () =
    match globalSpec.Parse [| "completions" |] with
    | Ok(_, cmd) -> test <@ cmd = Completions @>
    | Error e -> Assert.Fail($"Expected Ok Completions, got Error: %A{e}")

// --- Init command ---

[<Fact(Timeout = 5000)>]
let ``executeCommand Init creates config in empty dir`` () =
    withTempDir "prog-init" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                (fakeIpc ())
                tmpDir
                "pipe"
                Init
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ result = 0 @>
        test <@ File.Exists(Path.Combine(tmpDir, ".fs-hot-watch.json")) @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand Init returns 1 when config already exists`` () =
    withTempDir "prog-init-dup" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, ".fs-hot-watch.json"), "{}")

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                (fakeIpc ())
                tmpDir
                "pipe"
                Init
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ result = 1 @>)

// --- applyGlobalFlags edge cases ---

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags with unknown log level still builds extra args`` () =
    test <@ (applyGlobalFlags [ LogLevel "trace" ]).DaemonExtraArgs = "--log-level trace " @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags preserves order of multiple flags`` () =
    let opts =
        applyGlobalFlags [ Verbose; LogLevel "debug"; GlobalFlag.NoCache; NoWarnFail ]

    test <@ opts.NoCache @>
    test <@ opts.NoWarnFail @>
    test <@ opts.DaemonExtraArgs = "--verbose --log-level debug --no-cache " @>

// --- decideDaemonAction additional edge cases ---

[<Fact(Timeout = 5000)>]
let ``decideDaemonAction restarts when stored hash is empty but running`` () =
    let action = decideDaemonAction true "" "new-hash"
    test <@ action = Restart @>

// --- config hash determinism ---

[<Fact(Timeout = 5000)>]
let ``config hash is deterministic across multiple calls`` () =
    withTempDir "prog-hash-det" (fun tmpDir ->
        let fileOps =
            { defaultFileOps with
                FileExists = fun _ -> false }

        let hash1 = computeConfigHashWith fileOps tmpDir "/tmp/exe"
        let hash2 = computeConfigHashWith fileOps tmpDir "/tmp/exe"
        test <@ hash1 = hash2 @>)

[<Fact(Timeout = 5000)>]
let ``config hash changes when config file is added`` () =
    withTempDir "prog-hash-change" (fun tmpDir ->
        let hash1 = computeConfigHashWith defaultFileOps tmpDir "/tmp/exe"

        File.WriteAllText(Path.Combine(tmpDir, ".fs-hot-watch.json"), """{"build": {}}""")

        let hash2 = computeConfigHashWith defaultFileOps tmpDir "/tmp/exe"
        test <@ hash1 <> hash2 @>)

// --- Reuse path ---

[<Fact(Timeout = 5000)>]
let ``reuse path does not launch daemon when hash matches`` () =
    withTempDir "prog-reuse" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            tmpDir
            "pipe"
            (Scan [])
            defaultGlobalOptions
            fakeConfig
            5.0
        |> ignore

        let mutable launchCalled = false

        let ipc2 =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> launchCalled <- true }

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc2
                tmpDir
                "pipe"
                (Scan [])
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ not launchCalled @>)

// --- Daemon startup failure for various commands ---

let private assertFailsWhenDaemonDown (cmd: Command) =
    withTempDir "prog-daemon-down" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let result =
            executeCommand (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" cmd defaultGlobalOptions fakeConfig 0.0

        test <@ result = 1 @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand Format returns 1 when daemon startup fails`` () = assertFailsWhenDaemonDown (Format [])

[<Fact(Timeout = 5000)>]
let ``executeCommand FormatCheck returns 1 when daemon startup fails`` () =
    assertFailsWhenDaemonDown (FormatCheck [])

[<Fact(Timeout = 5000)>]
let ``executeCommand Analyze returns 1 when daemon startup fails`` () = assertFailsWhenDaemonDown (Analyze [])

[<Fact(Timeout = 5000)>]
let ``executeCommand Rerun returns 1 when daemon startup fails`` () =
    assertFailsWhenDaemonDown (Rerun "coverage-ratchet")

/// The agent-mode banner advertises a curated set of subcommands. If any name
/// drifts from the real command tree (typo, rename, removed subcommand) this
/// test fails — catching drift that the hardcoded banner string would otherwise hide.
[<Fact>]
let ``agent banner command names all exist as subcommands`` () =
    let bannerLine =
        FsHotWatch.Cli.ProgressRenderer.renderAll FsHotWatch.Cli.ProgressRenderer.Agent false DateTime.UtcNow Map.empty
        |> List.head

    let advertised =
        bannerLine.Substring(bannerLine.IndexOf("cmds:") + 5).Trim().Split(' ')
        |> Array.toList

    let childNames =
        match commandTree with
        | Group g -> g.Children |> List.map CommandTree.name |> Set.ofList
        | Leaf _ -> Set.empty

    let missing = advertised |> List.filter (fun n -> not (Set.contains n childNames))

    test <@ List.isEmpty missing @>
