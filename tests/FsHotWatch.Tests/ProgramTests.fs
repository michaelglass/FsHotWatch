[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ProgramTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CommandTree
open FsHotWatch.Cli.Program
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Tests.TestHelpers

let private captureStderr (f: unit -> 'a) : string * 'a =
    let original = Console.Error
    use sw = new StringWriter()
    Console.SetError(sw)

    try
        let result = f ()
        sw.Flush()
        sw.ToString(), result
    finally
        Console.SetError(original)

/// Both streams. `UI.warn`, `dimInfo`, `success` and `info` all write to STDOUT in
/// CommandTree 0.11.0 (measured, not assumed — only `UI.fail` uses stderr), so the
/// message assertions below would hold on a stdout-only capture. Both are taken
/// anyway because the `executeCommand` paths also emit diagnostics on stderr, and a
/// one-stream capture there reads a message that named nothing.
let private captureBothStreams (f: unit -> 'a) : string * 'a =
    let originalOut = Console.Out
    let originalErr = Console.Error
    use sw = new StringWriter()
    Console.SetOut(sw)
    Console.SetError(sw)

    try
        let result = f ()
        sw.Flush()
        sw.ToString(), result
    finally
        Console.SetOut(originalOut)
        Console.SetError(originalErr)

// --- Helper: shared fake config and IPC ---

let private fakeConfig: DaemonConfiguration =
    { defaultTestConfig () with
        Build = None
        Format = Off
        Lint = false
        Cache = FsHotWatch.Cli.DaemonConfig.NoCache }

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name }
      GetDiagnostics =
        fun _ _ ->
            async {
                return
                    """{"count": 0, "files": {}, "projectModel": {"schema": "fshw-project-model-v1", "status": "available", "generation": 7, "counts": {"discovered": 3, "loaded": 3, "optionsMapped": 3, "registered": 3}, "reasonCode": null}}"""
            }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      Invalidate = fun _ -> async { return "invalidated" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

// --- computeConfigHashWith tests ---
// The hash covers `.fshw.json` content ONLY. Do not put binary staleness back in:
// exe mtime tracks the WRONG file for dotnet-hosted invocations (the muxer, not
// the fshw dll). That job belongs to the DaemonIdentity handshake.

[<Fact(Timeout = 15000)>]
let ``computeConfigHashWith returns 16-char hex string`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let result = computeConfigHashWith fileOps "/tmp/repo"
    test <@ result.Length = 16 @>
    test <@ result |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c || Char.IsDigit c) @>

[<Fact(Timeout = 15000)>]
let ``computeConfigHashWith is deterministic`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let h1 = computeConfigHashWith fileOps "/tmp/repo"
    let h2 = computeConfigHashWith fileOps "/tmp/repo"
    test <@ h1 = h2 @>

[<Fact(Timeout = 15000)>]
let ``computeConfigHashWith changes when config content changes`` () =
    let mutable configContent = "v1"

    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith(".fshw.json")
            ReadAllText = fun _ -> configContent }

    let h1 = computeConfigHashWith fileOps "/tmp/repo"
    configContent <- "v2"
    let h2 = computeConfigHashWith fileOps "/tmp/repo"
    test <@ h1 <> h2 @>

[<Fact(Timeout = 15000)>]
let ``computeConfigHashWith with no config file uses empty content`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    let result = computeConfigHashWith fileOps "/tmp/repo"
    test <@ result.Length = 16 @>

// --- killStaleDaemonWith tests ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``killStaleDaemonWith handles invalid pid file gracefully`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun path -> path.EndsWith("daemon.pid")
            ReadAllText = fun _ -> "not-a-number" }

    let processOps =
        { GetProcessById = fun _ -> failwith "should not be called"
          KillProcess = fun _ -> ()
          WaitForExit = fun _ _ -> true }

    killStaleDaemonWith fileOps processOps "/tmp/repo"

// --- startFreshDaemonWith tests ---

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith returns true when daemon starts immediately`` () =
    let mutable written = false
    let mutable launchCalled = false

    let fileOps =
        { defaultFileOps with
            CreateDirectory = fun _ -> ()
            WriteAllText = fun _path _content -> written <- true }

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> true
            LaunchDaemon = fun _ _ _ -> launchCalled <- true }

    let result = startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "" "logs" 5.0

    test <@ result @>
    test <@ launchCalled @>
    // The launcher attests nothing: the daemon publishes its own loaded identity.
    test <@ not written @>

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith returns false when daemon never starts`` () =
    let fileOps =
        { defaultFileOps with
            CreateDirectory = fun _ -> ()
            WriteAllText = fun _ _ -> () }

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> false
            LaunchDaemon = fun _ _ _ -> () }

    let result = startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "" "logs" 0.0

    test <@ not result @>

[<Fact(Timeout = 15000)>]
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

    startFreshDaemonWith fileOps ipc "/tmp/repo" "pipe" "--verbose " "logs" 5.0
    |> ignore

    test <@ receivedArgs = "--verbose " @>

// --- Restart flow tests (via decideRunningDaemonAction) ---

[<Fact(Timeout = 15000)>]
let ``restart flow is triggered when stored config hash differs`` () =
    let action =
        decideRunningDaemonAction FsHotWatch.DaemonIdentity.IdentityVerdict.Match "old-hash" "new-hash"

    test <@ action = RestartConfigChanged @>

[<Fact(Timeout = 15000)>]
let ``restart flow handles shutdown failure gracefully`` () =
    // The shutdown exception itself is caught by ensureDaemon, not here.
    let action =
        decideRunningDaemonAction FsHotWatch.DaemonIdentity.IdentityVerdict.Match "old-hash" "new-hash"

    test <@ action = RestartConfigChanged @>
    // startFreshDaemonWith still works after a failed shutdown
    withTempDir "prog-restart-fail" (fun tmpDir ->
        let mutable launchCalled = false

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> launchCalled <- true }

        let result = startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "" "logs" 5.0

        test <@ result @>
        test <@ launchCalled @>)

// --- killStaleDaemon ---

[<Fact(Timeout = 15000)>]
let ``killStaleDaemonWith cleans up stale PID file`` () =
    withTempDir "prog-kill-stale" (fun tmpDir ->
        let stateDir = Path.Combine(tmpDir, ".fshw")
        Directory.CreateDirectory(stateDir) |> ignore
        File.WriteAllText(Path.Combine(stateDir, "daemon.pid"), "999999999")

        killStaleDaemonWith defaultFileOps defaultProcessOps tmpDir

        test <@ not (File.Exists(Path.Combine(stateDir, "daemon.pid"))) @>)

[<Fact(Timeout = 15000)>]
let ``killStaleDaemonWith handles missing PID file gracefully`` () =
    withTempDir "prog-no-pid" (fun tmpDir ->
        // Should not throw when PID file doesn't exist
        killStaleDaemonWith defaultFileOps defaultProcessOps tmpDir)

// --- startFreshDaemonWith ---

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith reports a failed launch without waiting for a daemon`` () =
    withTempDir "prog-launch-fails" (fun tmpDir ->
        let mutable probes = 0

        let ipc =
            { fakeIpc () with
                IsRunning =
                    fun _ ->
                        probes <- probes + 1
                        false
                LaunchDaemon = fun _ _ _ -> invalidOp "helper exited 7 launching: start" }

        let clock = Diagnostics.Stopwatch.StartNew()

        let stderr, result =
            captureStderr (fun () -> startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "" "logs" 10.0)

        test <@ not result @>
        test <@ clock.Elapsed < TimeSpan.FromSeconds 5.0 @>
        test <@ probes = 1 @>
        test <@ stderr.Contains "Could not launch the daemon: helper exited 7 launching: start" @>)

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith leaves loaded config identity to the daemon`` () =
    withTempDir "prog-hash-write" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), "{}")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        let result = startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "" "logs" 5.0

        test <@ result @>
        let hashPath = Path.Combine(tmpDir, ".fshw", "config.hash")
        test <@ not (File.Exists hashPath) @>)

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith creates log directory from logDirName param`` () =
    withTempDir "prog-log-dir" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "" "custom-logs" 5.0
        |> ignore

        test <@ Directory.Exists(Path.Combine(tmpDir, "custom-logs")) @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, "log"))) @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, "logs"))) @>)

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith accepts absolute logDirName`` () =
    withTempDir "prog-log-abs" (fun tmpDir ->
        let absLogDir = Path.Combine(tmpDir, "nested", "absolute-logs")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "" absLogDir 5.0 |> ignore

        test <@ Directory.Exists absLogDir @>)

[<Fact(Timeout = 15000)>]
let ``startFreshDaemonWith passes extra args to launch`` () =
    withTempDir "prog-extra-args" (fun tmpDir ->
        let mutable receivedArgs = ""

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ args _ -> receivedArgs <- args }

        startFreshDaemonWith defaultFileOps ipc tmpDir "pipe" "--verbose --no-cache " "logs" 5.0
        |> ignore

        test <@ receivedArgs = "--verbose --no-cache " @>)

// --- Completions command ---
//
// This test used to run the real writer against the real `~/.config/fish/completions/`,
// so every run of the unit suite overwrote the DEVELOPER's live fish completions — a unit
// test reaching out and editing the environment it runs in. It now points the write at a
// temp config dir, and asserts the real path is untouched.
//
// `XDG_CONFIG_HOME` is the seam because it is the one fish itself uses, and because the
// obvious alternative does not work: `SpecialFolder.UserProfile` does NOT follow `HOME` on
// this runtime — overriding `HOME` makes it return `""` (measured, .NET 10 / macOS), which
// would have turned the absolute clobber into a relative write into the working directory.

[<Fact(Timeout = 15000)>]
let ``executeCommand Completions returns 0 and writes only under XDG_CONFIG_HOME`` () =
    withTempDir "prog-completions" (fun configHome ->
        // The negative control. The real file is the developer's live config and may
        // legitimately exist, so "absent afterwards" is not the assertion — "byte-identical
        // to whatever it was before" is, and it holds whether or not it exists.
        let realPath =
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                ".config",
                "fish",
                "completions",
                $"{cliName}.fish"
            )

        let realBefore =
            if File.Exists realPath then
                Some(File.ReadAllBytes realPath, File.GetLastWriteTimeUtc realPath)
            else
                None

        withEnv "XDG_CONFIG_HOME" (Some configHome) (fun () ->
            let result =
                executeCommand
                    ""
                    (fun _ -> Unchecked.defaultof<_>)
                    (fakeIpc ())
                    "/tmp"
                    "pipe"
                    Completions
                    defaultGlobalOptions
                    fakeConfig
                    30.0

            test <@ result = 0 @>

            // It wrote — under the temp dir, with real content.
            let written = Path.Combine(configHome, "fish", "completions", $"{cliName}.fish")

            test <@ File.Exists written @>
            test <@ (File.ReadAllText written).Contains $"complete -c {cliName}" @>)

        let realAfter =
            if File.Exists realPath then
                Some(File.ReadAllBytes realPath, File.GetLastWriteTimeUtc realPath)
            else
                None

        test <@ realAfter = realBefore @>)

[<Fact(Timeout = 15000)>]
let ``fishCompletionsDir prefers XDG_CONFIG_HOME over the home-directory fallback`` () =
    withEnv "XDG_CONFIG_HOME" (Some "/somewhere/else") (fun () ->
        // The bug this pins: fish reads $XDG_CONFIG_HOME/fish when it is set, so writing to
        // ~/.config/fish there produces a file fish never loads — and a success message.
        test <@ fishCompletionsDir () = Ok(Path.Combine("/somewhere/else", "fish", "completions")) @>)

[<Fact(Timeout = 15000)>]
let ``fishCompletionsDir falls back to the home directory when XDG_CONFIG_HOME is unset`` () =
    withEnv "XDG_CONFIG_HOME" None (fun () ->
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        test <@ fishCompletionsDir () = Ok(Path.Combine(home, ".config", "fish", "completions")) @>)

// --- Start command singleton guarantee ---

/// Simulate a running daemon by pre-holding an exclusive lock on
/// `.fshw/daemon.lock`, the same handle `Start` uses to enforce singleton.
/// The returned disposable releases the lock.
let private holdDaemonLock (tmpDir: string) (pid: int) : IDisposable =
    let stateDir = Path.Combine(tmpDir, ".fshw")
    Directory.CreateDirectory(stateDir) |> ignore
    File.WriteAllText(Path.Combine(stateDir, "daemon.pid"), string pid)
    let lockPath = Path.Combine(stateDir, "daemon.lock")

    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None) :> IDisposable

[<Fact(Timeout = 15000)>]
let ``executeCommand Start refuses to spawn a duplicate when lock is held`` () =
    withTempDir "prog-start-dup" (fun tmpDir ->
        // Stage a discoverable .fsproj so the failIfNoProjects pre-check
        // passes and execution actually reaches the lock-acquisition code.
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "Stub.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        use _held = holdDaemonLock tmpDir 99999
        let mutable createDaemonCalled = false

        let createDaemon _ =
            createDaemonCalled <- true
            Unchecked.defaultof<_>

        let result =
            executeCommand
                ""
                createDaemon
                (fakeIpc ())
                tmpDir
                "pipe-singleton"
                Start
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ not createDaemonCalled @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Start — second concurrent invocation cannot claim the lock`` () =
    // Regression: two back-to-back Start calls must not both proceed to
    // createDaemon. The file lock is OS-enforced, so concurrent holders are
    // impossible regardless of probe-timing races.
    withTempDir "prog-start-concurrent" (fun tmpDir ->
        let srcDir = System.IO.Path.Combine(tmpDir, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(srcDir, "Stub.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />"
        )

        use _held = holdDaemonLock tmpDir 12345
        let mutable createDaemonCalls = 0

        let createDaemon _ =
            createDaemonCalls <- createDaemonCalls + 1
            Unchecked.defaultof<_>

        let result =
            executeCommand
                ""
                createDaemon
                (fakeIpc ())
                tmpDir
                "pipe-concurrent"
                Start
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ createDaemonCalls = 0 @>)

[<Fact(Timeout = 15000)>]
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
                ""
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "pipe-multi"
                (Stop [])
                defaultGlobalOptions
                fakeConfig
                5.0

        // Exit 2, not 0: this fake never writes `.fshw/daemon.pid`, so there is no
        // process for `stop` to watch leave the table and it reports — correctly —
        // that it could not verify the exit. The subject of this test is the loop
        // below, which is unchanged.
        test <@ result = 2 @>
        test <@ shutdownCalls = 3 @>
        test <@ remainingDaemons = 0 @>)

[<Fact(Timeout = 15000)>]
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

        let out, result =
            captureBothStreams (fun () ->
                executeCommand
                    ""
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    tmpDir
                    "pipe-none"
                    (Stop [])
                    defaultGlobalOptions
                    fakeConfig
                    5.0)

        test <@ result = 0 @>
        test <@ shutdownCalls = 0 @>
        // Quiet: it says nothing ran, and warns about nothing.
        test <@ out.Contains "No daemon running" @>
        test <@ not (out.Contains "⚠") @>)

[<Fact(Timeout = 15000)>]
let ``parse completions returns Completions`` () =
    match globalSpec.Parse [| "completions" |] with
    | Ok(_, cmd) -> test <@ cmd = Completions @>
    | Error e -> Assert.Fail($"Expected Ok Completions, got Error: %A{e}")

// --- Init command ---

[<Fact(Timeout = 15000)>]
let ``executeCommand Init creates config in empty dir`` () =
    withTempDir "prog-init" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore

        let result =
            executeCommand
                ""
                (fun _ -> Unchecked.defaultof<_>)
                (fakeIpc ())
                tmpDir
                "pipe"
                Init
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ result = 0 @>
        test <@ File.Exists(Path.Combine(tmpDir, ".fshw.json")) @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Init returns 1 when config already exists`` () =
    withTempDir "prog-init-dup" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), "{}")

        let result =
            executeCommand
                ""
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

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags with unknown log level still builds extra args`` () =
    test <@ (applyGlobalFlags [ LogLevel "trace" ]).DaemonExtraArgs = "--log-level trace " @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags preserves order of multiple flags`` () =
    let opts =
        applyGlobalFlags [ Verbose; LogLevel "debug"; GlobalFlag.NoCache; NoWarnFail ]

    test <@ opts.NoCache @>
    test <@ opts.NoWarnFail @>
    test <@ opts.DaemonExtraArgs = "--verbose --log-level debug --no-cache " @>

// --- decideRunningDaemonAction additional edge cases ---

[<Fact(Timeout = 15000)>]
let ``decideRunningDaemonAction restarts when stored hash is empty but running`` () =
    let action =
        decideRunningDaemonAction FsHotWatch.DaemonIdentity.IdentityVerdict.Match "" "new-hash"

    test <@ action = RestartConfigChanged @>

// --- config hash determinism ---

[<Fact(Timeout = 15000)>]
let ``config hash is deterministic across multiple calls`` () =
    withTempDir "prog-hash-det" (fun tmpDir ->
        let fileOps =
            { defaultFileOps with
                FileExists = fun _ -> false }

        let hash1 = computeConfigHashWith fileOps tmpDir
        let hash2 = computeConfigHashWith fileOps tmpDir
        test <@ hash1 = hash2 @>)

[<Fact(Timeout = 15000)>]
let ``config hash changes when config file is added`` () =
    withTempDir "prog-hash-change" (fun tmpDir ->
        let hash1 = computeConfigHashWith defaultFileOps tmpDir

        File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), """{"build": {}}""")

        let hash2 = computeConfigHashWith defaultFileOps tmpDir
        test <@ hash1 <> hash2 @>)

// --- Reuse path ---

[<Fact(Timeout = 15000)>]
let ``reuse path does not launch daemon when hash matches`` () =
    withTempDir "prog-reuse" (fun tmpDir ->
        // Reuse also requires the identity handshake to match: record THIS
        // process's identity as the running daemon's.
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> () }

        executeCommand "" (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" Scan defaultGlobalOptions fakeConfig 5.0
        |> ignore

        let mutable launchCalled = false

        let ipc2 =
            { fakeIpc () with
                IsRunning = fun _ -> true
                LaunchDaemon = fun _ _ _ -> launchCalled <- true }

        let result =
            executeCommand
                ""
                (fun _ -> Unchecked.defaultof<_>)
                ipc2
                tmpDir
                "pipe"
                Scan
                defaultGlobalOptions
                fakeConfig
                5.0

        test <@ result = 0 @>
        test <@ not launchCalled @>)

// --- Daemon startup failure for various commands ---

let private assertFailsWhenDaemonDown (cmd: Command) =
    withTempDir "prog-daemon-down" (fun tmpDir ->
        // Stage a fake .fsproj so executeCommand's no-projects pre-check (exit 2)
        // doesn't preempt the daemon-launch failure path under test.
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Stub.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let result =
            executeCommand
                ""
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "pipe"
                cmd
                defaultGlobalOptions
                fakeConfig
                0.0

        test <@ result = 1 @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Format returns 1 when daemon startup fails`` () = assertFailsWhenDaemonDown (Format [])

[<Fact(Timeout = 15000)>]
let ``executeCommand Rerun returns 1 when daemon startup fails`` () =
    assertFailsWhenDaemonDown (Rerun "coverage-ratchet")

/// The banner is a hardcoded string: without this test a typo, rename or removed
/// subcommand would advertise a command that no longer exists.
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

[<Fact(Timeout = 15000)>]
let ``unwrapIpcException returns the exception unchanged when not aggregate`` () =
    let ex = OutOfMemoryException("buffer too large")
    let unwrapped = unwrapIpcException ex
    test <@ obj.ReferenceEquals(unwrapped, ex) @>

[<Fact(Timeout = 15000)>]
let ``unwrapIpcException unwraps single-inner AggregateException`` () =
    // StreamJsonRpc surfaces pipe-corruption OOM wrapped in an Aggregate, which the
    // CLI would otherwise print as "One or more errors occurred." — the operator
    // needs the inner OOM.
    let inner =
        OutOfMemoryException("Insufficient memory to continue the execution of the program.")

    let agg = AggregateException(inner)
    let unwrapped = unwrapIpcException agg
    test <@ obj.ReferenceEquals(unwrapped, inner) @>

[<Fact(Timeout = 15000)>]
let ``unwrapIpcException recurses through nested AggregateException`` () =
    let leaf = TimeoutException("daemon unresponsive")
    let nested = AggregateException(AggregateException(leaf))
    let unwrapped = unwrapIpcException nested
    test <@ obj.ReferenceEquals(unwrapped, leaf) @>

[<Fact(Timeout = 15000)>]
let ``unwrapIpcException stops at multi-inner AggregateException`` () =
    // Several distinct errors: unwrapping past the aggregate would lose the rest,
    // so it stops at the first inner and keeps the behaviour predictable.
    let a = InvalidOperationException("a")
    let b = InvalidOperationException("b")
    let agg = AggregateException(a, b)
    let unwrapped = unwrapIpcException agg
    test <@ obj.ReferenceEquals(unwrapped, a) @>

// --- IPC failures: typed classification and user-actionable hints ---

[<Fact(Timeout = 15000)>]
let ``OOM in StreamJsonRpc frame reader is classified as a corrupted frame`` () =
    let ex = OutOfMemoryException("Insufficient memory") :> exn

    match
        classifyIpcFaultAt FaultOrigin.Client (Some "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()") ex
    with
    | IpcFault.CorruptedFrame actual -> test <@ obj.ReferenceEquals(actual, ex) @>
    | actual -> failwithf "expected CorruptedFrame, got %A" actual

[<Fact(Timeout = 15000)>]
let ``OOM without frame-reader evidence is a genuine client OOM`` () =
    let oom = OutOfMemoryException("Insufficient memory")

    match classifyIpcFaultAt FaultOrigin.Client None oom with
    | IpcFault.ClientOutOfMemory actual -> test <@ obj.ReferenceEquals(actual, oom) @>
    | actual -> failwithf "expected ClientOutOfMemory, got %A" actual

    let hint = ipcErrorHint oom
    test <@ hint.Contains("CLI ran out of memory") @>
    test <@ hint.Contains("was not restarted") @>

[<Fact(Timeout = 15000)>]
let ``overflow in StreamJsonRpc frame reader is classified as a corrupted frame`` () =
    let ex = OverflowException("Arithmetic operation resulted in an overflow.") :> exn

    match
        classifyIpcFaultAt FaultOrigin.Client (Some "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()") ex
    with
    | IpcFault.CorruptedFrame actual -> test <@ obj.ReferenceEquals(actual, ex) @>
    | actual -> failwithf "expected CorruptedFrame, got %A" actual

[<Fact(Timeout = 15000)>]
let ``overflow without frame-reader evidence is not classified as corruption`` () =
    let ex = OverflowException("Arithmetic operation resulted in an overflow.") :> exn

    match classifyIpcFaultAt FaultOrigin.Client None ex with
    | IpcFault.Other actual -> test <@ obj.ReferenceEquals(actual, ex) @>
    | actual -> failwithf "expected Other, got %A" actual

    // Unclassified is not silent: the reader still gets the generic recovery steps.
    test <@ (ipcErrorHint ex).Contains "logs/daemon.log" @>

[<Fact(Timeout = 15000)>]
let ``ipcErrorHint maps TimeoutException to busy-or-hung-or-absent hint`` () =
    let ex = TimeoutException("daemon unresponsive") :> exn
    let hint = ipcErrorHint ex
    test <@ hint.Contains("hung") || hint.Contains("busy") @>
    // A daemon that is not there at all times out identically (ConnectAsync retries every
    // connect error), so the hint must not send the reader looking only at a live daemon.
    test <@ hint.Contains("no daemon") @>

[<Fact(Timeout = 15000)>]
let ``ipcErrorHint answers even for an exception it cannot classify`` () =
    // The old contract returned None here, and `reportDaemonError` printed a bare
    // exception with no guidance — the whole point of making the hint total.
    let ex = InvalidOperationException("something else") :> exn
    let hint = ipcErrorHint ex
    test <@ hint <> "" @>
    test <@ hint.Contains "logs/daemon.log" @>
    test <@ hint.Contains "fshw stop" @>

// --- classifyIpcFault: a fault that happened DAEMON-side arrives wrapped in
// RemoteInvocationException, never as the daemon's own exception type. ---

let private remoteFault (typeName: string) (message: string) (stackTrace: string) : exn =
    let data =
        StreamJsonRpc.Protocol.CommonErrorData(TypeName = typeName, Message = message, StackTrace = stackTrace)

    StreamJsonRpc.RemoteInvocationException(message, 0, null, data) :> exn

[<Fact(Timeout = 15000)>]
let ``classifyIpcFault reconstructs a daemon-side OOM from RemoteInvocationException, using the REMOTE stack trace``
    ()
    =
    // The reconstructed exception is freshly constructed, never thrown — its OWN
    // .StackTrace is empty. Classification must use the remote CommonErrorData's
    // stack trace, not the (useless) local one, or a genuine corrupted-frame fault
    // that happened on the daemon would always misclassify as ClientOutOfMemory.
    let remote =
        remoteFault
            "System.OutOfMemoryException"
            "Insufficient memory to continue the execution of the program."
            "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"

    match classifyIpcFault remote with
    | IpcFault.CorruptedFrame actual ->
        test <@ actual :? OutOfMemoryException @>
        test <@ actual.Message = "Insufficient memory to continue the execution of the program." @>
    | actual -> failwithf "expected CorruptedFrame, got %A" actual

[<Fact(Timeout = 15000)>]
let ``classifyIpcFault reconstructs a daemon-side overflow from RemoteInvocationException`` () =
    let remote =
        remoteFault
            "System.OverflowException"
            "Arithmetic operation resulted in an overflow."
            "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"

    match classifyIpcFault remote with
    | IpcFault.CorruptedFrame actual -> test <@ actual :? OverflowException @>
    | actual -> failwithf "expected CorruptedFrame, got %A" actual

[<Fact(Timeout = 15000)>]
let ``classifyIpcFault reconstructs a daemon-side timeout from RemoteInvocationException`` () =
    let remote = remoteFault "System.TimeoutException" "daemon unresponsive" ""

    match classifyIpcFault remote with
    | IpcFault.TimedOut actual -> test <@ actual.Message = "daemon unresponsive" @>
    | actual -> failwithf "expected TimedOut, got %A" actual

[<Fact(Timeout = 15000)>]
let ``classifyIpcFault leaves an unrecognized RemoteInvocationException as Other, never misreported as recoverable``
    ()
    =
    // A real daemon-side bug unrelated to the corrupted-pipe family (e.g. a genuine
    // InvalidOperationException in a plugin) must not be treated as a recoverable
    // pipe corruption — only the three known types get reconstructed.
    let remote =
        remoteFault "System.InvalidOperationException" "some real daemon-side bug" ""

    match classifyIpcFault remote with
    | IpcFault.DaemonThrew actual -> test <@ obj.ReferenceEquals(actual, remote) @>
    | actual -> failwithf "expected DaemonThrew, got %A" actual

[<Fact(Timeout = 15000)>]
let ``classifyIpcFault treats a RemoteInvocationException with no deserialized data as a daemon-side throw`` () =
    let remote =
        StreamJsonRpc.RemoteInvocationException("opaque failure", 0, (null: obj)) :> exn

    match classifyIpcFault remote with
    | IpcFault.DaemonThrew actual -> test <@ obj.ReferenceEquals(actual, remote) @>
    | actual -> failwithf "expected DaemonThrew, got %A" actual

[<Fact(Timeout = 15000)>]
let ``a daemon-side corrupted-frame OOM reaches the SAME self-heal hint as a local one`` () =
    // End-to-end: the whole point of reconstructing type + remote stack trace is so
    // the existing recovery messaging treats a daemon-side fault identically to a
    // client-side one, rather than falling through to "Other" with no hint at all.
    let remote =
        remoteFault
            "System.OutOfMemoryException"
            "Insufficient memory to continue the execution of the program."
            "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"

    let hint = ipcErrorHint remote
    test <@ hint.Contains("corrupted") @>

[<Fact(Timeout = 15000)>]
let ``tryDeleteForCleanup returns Some on successful delete`` () =
    let path =
        Path.Combine(Path.GetTempPath(), $"fshw-cleanup-{System.Guid.NewGuid()}.tmp")

    File.WriteAllText(path, "x")
    let r = tryDeleteForCleanup path
    test <@ r = Some path @>
    test <@ not (File.Exists path) @>

[<Fact(Timeout = 15000)>]
let ``tryDeleteForCleanup logs at debug and returns None on failure (F9)`` () =
    // F9: a bare `with _` hid the exception class. The helper now logs it at Debug
    // so cleanup failures are diagnosable on --verbose.
    let original = FsHotWatch.Logging.logLevel
    let sb = System.Text.StringBuilder()
    let writer = new StringWriter(sb)
    let prevErr = System.Console.Error

    try
        System.Console.SetError(writer)
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
        // An empty path forces File.Delete to throw ArgumentException.
        let r = tryDeleteForCleanup ""
        writer.Flush()
        let output = sb.ToString()
        test <@ r = None @>
        test <@ output.Contains("cli-cleanup") @>
        test <@ output.Contains("ArgumentException") @>
    finally
        System.Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original

// ---------------------------------------------------------------------------
// an out-of-memory fault must name the process that HAD it.
//
// The incident: `dotnet fshw check` printed "The fshw CLI ran out of memory while
// handling daemon IPC" seven times. It was investigated seven times as a client
// problem — `DOTNET_GCConserveMemory=9` set on the CLI, the box's free memory measured
// at 6-8 GB — and none of that could have mattered, because a fault raised on the
// DAEMON crosses the wire reconstructed as an ordinary local `OutOfMemoryException`,
// and the classifier had no way to say where it came from. Everything outside the frame
// reader was called a client OOM by construction.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a daemon-side OOM outside the frame reader is the DAEMON's, not this CLI's`` () =
    // Where an over-large reply actually fails: building the JSON, not reading the frame.
    let remote =
        remoteFault
            "System.OutOfMemoryException"
            "Insufficient memory to continue the execution of the program."
            "at System.Text.Json.JsonSerializer.Serialize[TValue](TValue value)"

    match classifyIpcFault remote with
    | IpcFault.DaemonOutOfMemory actual -> test <@ actual :? OutOfMemoryException @>
    | actual -> failwithf "expected DaemonOutOfMemory, got %A" actual

    // And every surface a reader actually sees says so.
    let headline = ipcErrorHeadline remote
    test <@ headline.Contains "DAEMON" @>
    test <@ not (headline.Contains "CLI ran out of memory") @>

    let hint = ipcErrorHint remote
    test <@ hint.Contains "on that side" @>
    // The lever that IS available — the run's evidence survives on disk.
    test <@ hint.Contains ".fshw/test-runs/" @>

[<Fact(Timeout = 15000)>]
let ``a daemon-side transcoder overflow is the same fact as its OOM, not a client bug`` () =
    // System.Text.Json raises OverflowException, not OOM, when one string token is too
    // large to encode. Both mean "this reply cannot exist"; only one used to be named.
    let remote =
        remoteFault
            "System.OverflowException"
            "Arithmetic operation resulted in an overflow."
            "at System.Text.Json.Utf8JsonWriter.WriteStringValue(String value)"

    match classifyIpcFault remote with
    | IpcFault.DaemonOutOfMemory _ -> ()
    | actual -> failwithf "expected DaemonOutOfMemory, got %A" actual

[<Fact(Timeout = 15000)>]
let ``a local OOM is still this CLI's — the fix does not move the blame the other way`` () =
    let oom = OutOfMemoryException("Insufficient memory") :> exn

    match classifyIpcFault oom with
    | IpcFault.ClientOutOfMemory _ -> ()
    | actual -> failwithf "expected ClientOutOfMemory, got %A" actual

    test <@ (ipcErrorHeadline oom).Contains "fshw CLI ran out of memory" @>

[<Fact(Timeout = 15000)>]
let ``a daemon that named its own scan terminal is not headlined as a failure to connect`` () =
    // The regression this pins: a scan whose project model kept being replaced reached
    // the operator as `Could not connect to daemon: ...`, and the daemon had connected,
    // answered, and said exactly what was wrong. The reader went to the pipe; the fault
    // was a build rewriting project files underneath the scan.
    let terminal =
        remoteFault
            "System.InvalidOperationException"
            (FsHotWatch.Daemon.ModelKeptChangingDuringScanException(FsHotWatch.Daemon.scanAttemptLimit).Message)
            "at FsHotWatch.Daemon.performScan"

    let headline = ipcErrorHeadline terminal
    test <@ not (headline.Contains "Could not connect to daemon") @>
    test <@ FsHotWatch.Daemon.isModelKeptChangingDuringScanMessage headline @>
    test <@ headline.Contains "scan attempts" @>

[<Fact(Timeout = 15000)>]
let ``a daemon OOM never restarts the daemon — that would discard the run it just finished`` () =
    let remote =
        remoteFault
            "System.OutOfMemoryException"
            "Insufficient memory to continue the execution of the program."
            "at System.Text.Json.JsonSerializer.Serialize[TValue](TValue value)"

    let mutable restarts = 0

    let exitCode =
        runIpcWithSelfHeal
            (fun () ->
                restarts <- restarts + 1
                true)
            (fun _ -> 7)
            (fun () -> raise remote)

    test <@ restarts = 0 @>
    test <@ exitCode = 7 @>
// --- `stop` tells the truth about the daemon PROCESS --------------------------
//
// The defect these pin: `stop` counted delivered IPC shutdown requests and called
// any count above zero "Daemon stopped". The pipe going quiet proves the LISTENER
// is gone; the daemon can acknowledge the request, close its endpoint and go on
// checking files. An operator told it stopped re-runs, and now two daemons share
// one workspace — and the wedge message steers them down exactly this path.

/// A clock whose ONLY advance is `SleepMs`, so a bounded wait completes instantly
/// and what is under test is the bound itself, not real time. Returns the ops plus
/// a reader for how much simulated time the call consumed.
let private fakeStopOps (alive: int -> bool) : StopOps * (unit -> TimeSpan) =
    let start = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let now = ref start

    let ops =
        { IsProcessAlive = alive
          Now = fun () -> now.Value
          SleepMs = fun ms -> now.Value <- now.Value.AddMilliseconds(float ms) }

    ops, (fun () -> now.Value - start)

/// A pipe that answers until `answers` shutdown requests have been delivered and
/// then goes quiet — the shape of a daemon closing its endpoint. Says nothing
/// about whether the PROCESS behind it exits, which is the whole point.
let private quietingIpc (answers: int) : IpcOps =
    let delivered = ref 0

    { fakeIpc () with
        IsRunning = fun _ -> delivered.Value < answers
        Shutdown =
            fun _ ->
                async {
                    delivered.Value <- delivered.Value + 1
                    return "shutting down"
                } }

let private pidPathOf (root: string) =
    Path.Combine(root, ".fshw", "daemon.pid")

let private writePid (root: string) (pid: int) =
    Directory.CreateDirectory(Path.Combine(root, ".fshw")) |> ignore
    File.WriteAllText(pidPathOf root, string pid)

// --- the liveness probe: only ESRCH proves death ---

[<Fact(Timeout = 15000)>]
let ``kill probe returning 0 means the process is alive`` () =
    test <@ processAliveByProbe (fun _ -> 0) (fun () -> 0) 4242 @>

[<Fact(Timeout = 15000)>]
let ``kill probe failing with ESRCH is the one answer that proves death`` () =
    test <@ not (processAliveByProbe (fun _ -> -1) (fun () -> 3) 4242) @>

[<Fact(Timeout = 15000)>]
let ``a kill probe refused with EPERM means alive, not gone`` () =
    // The process exists and belongs to somebody else. Reading that as death is how a
    // live daemon's pidfile gets deleted out from under it.
    test <@ processAliveByProbe (fun _ -> -1) (fun () -> 1) 4242 @>

[<Fact(Timeout = 15000)>]
let ``a nonsense pid is never probed`` () =
    test <@ not (processAliveByProbe (fun _ -> failwith "should not be probed") (fun () -> 0) 0) @>

// --- stopDaemonWith ---

[<Fact(Timeout = 15000)>]
let ``a daemon that exits cleanly is reported stopped`` () =
    withTempDir "stop-clean-exit" (fun root ->
        writePid root 4242
        // A clean exit deletes its own pidfile on the way out.
        let delivered = ref 0

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> delivered.Value < 1
                Shutdown =
                    fun _ ->
                        async {
                            delivered.Value <- delivered.Value + 1
                            File.Delete(pidPathOf root)
                            return "shutting down"
                        } }

        // The pid probe still says ALIVE — the number has been reused by something
        // unrelated. The vanished pidfile settles it anyway, which is how pid reuse
        // is kept from turning a real stop into a false "still running".
        let ops, elapsed = fakeStopOps (fun _ -> true)
        let outcome = stopDaemonWith ipc defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.Stopped 1 @>
        test <@ elapsed () < TimeSpan.FromSeconds 1.0 @>)

[<Fact(Timeout = 15000)>]
let ``a hard-killed daemon is reported stopped and its leftover pidfile removed`` () =
    // A SIGTERM or `kill -9` never reaches the daemon's own cleanup, so the pidfile
    // outlives the process and the next liveness read consults a dead pid.
    withTempDir "stop-hard-killed" (fun root ->
        writePid root 4242
        let ops, _ = fakeStopOps (fun _ -> false)
        let outcome = stopDaemonWith (quietingIpc 1) defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.Stopped 1 @>
        test <@ not (File.Exists(pidPathOf root)) @>)

[<Fact(Timeout = 15000)>]
let ``a daemon that acknowledges shutdown and keeps running is NOT reported stopped`` () =
    // The observed defect, in one test: the pipe goes quiet, the process does not.
    withTempDir "stop-still-alive" (fun root ->
        writePid root 4242
        let ops, elapsed = fakeStopOps (fun _ -> true)
        let outcome = stopDaemonWith (quietingIpc 1) defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.StillAlive(4242, 1) @>
        // Bounded: it gives up instead of waiting on a daemon that never leaves.
        test <@ elapsed () < TimeSpan.FromSeconds(StopPipeQuietSeconds + StopProcessExitSeconds + 1.0) @>
        test <@ elapsed () >= TimeSpan.FromSeconds StopProcessExitSeconds @>)

[<Fact(Timeout = 15000)>]
let ``a live daemon's pidfile is never deleted by stop`` () =
    // Deleting it would strand the process beyond the reach of the next `stop`.
    withTempDir "stop-keeps-live-pidfile" (fun root ->
        writePid root 4242
        let ops, _ = fakeStopOps (fun _ -> true)
        stopDaemonWith (quietingIpc 1) defaultFileOps ops root "pipe" |> ignore

        test <@ File.Exists(pidPathOf root) @>
        test <@ File.ReadAllText(pidPathOf root).Trim() = "4242" @>)

[<Fact(Timeout = 15000)>]
let ``stop with no daemon running is a quiet no-op`` () =
    withTempDir "stop-nothing" (fun root ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let ops, elapsed = fakeStopOps (fun _ -> failwith "no pid to probe")
        let outcome = stopDaemonWith ipc defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.NothingRunning @>
        test <@ elapsed () < TimeSpan.FromSeconds 1.0 @>)

[<Fact(Timeout = 15000)>]
let ``a stale pidfile with no daemon running is cleaned up, still quietly`` () =
    withTempDir "stop-stale-pid" (fun root ->
        writePid root 4242

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let ops, _ = fakeStopOps (fun _ -> false)
        let outcome = stopDaemonWith ipc defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.NothingRunning @>
        test <@ not (File.Exists(pidPathOf root)) @>)

[<Fact(Timeout = 15000)>]
let ``a quiet pipe with no pidfile to watch is reported unverified, not stopped`` () =
    withTempDir "stop-no-pidfile" (fun root ->
        let ops, _ = fakeStopOps (fun _ -> failwith "no pid to probe")
        let outcome = stopDaemonWith (quietingIpc 1) defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.Unverified 1 @>)

[<Fact(Timeout = 15000)>]
let ``a daemon whose pipe never answered is still reported alive when its pid is`` () =
    // The wedge shape: the listener is unreachable, the process is right there.
    withTempDir "stop-wedged-pipe" (fun root ->
        writePid root 4242

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let ops, _ = fakeStopOps (fun _ -> true)
        let outcome = stopDaemonWith ipc defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.StillAlive(4242, 0) @>
        test <@ File.Exists(pidPathOf root) @>)

[<Fact(Timeout = 15000)>]
let ``an unparseable pidfile is left alone rather than deleted or believed`` () =
    withTempDir "stop-garbage-pid" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, ".fshw")) |> ignore
        File.WriteAllText(pidPathOf root, "not-a-number")
        let ops, _ = fakeStopOps (fun _ -> failwith "no pid to probe")
        let outcome = stopDaemonWith (quietingIpc 1) defaultFileOps ops root "pipe"

        test <@ outcome = StopOutcome.Unverified 1 @>
        test <@ File.Exists(pidPathOf root) @>)

// --- what the operator actually reads ---

[<Fact(Timeout = 15000)>]
let ``a still-running daemon never gets a success line`` () =
    let out, code =
        captureBothStreams (fun () -> reportStopOutcome (StopOutcome.StillAlive(4242, 1)))

    test <@ code = 1 @>
    test <@ not (out.Contains "✓") @>
    test <@ out.Contains "STILL RUNNING" @>
    test <@ out.Contains "4242" @>
    // The remedy is named, not left to be guessed.
    test <@ out.Contains "kill -0 4242" @>

[<Fact(Timeout = 15000)>]
let ``a daemon proven gone gets the success line and exit 0`` () =
    let out, code =
        captureBothStreams (fun () -> reportStopOutcome (StopOutcome.Stopped 1))

    test <@ code = 0 @>
    test <@ out.Contains "✓" @>
    test <@ out.Contains "Daemon stopped" @>

[<Fact(Timeout = 15000)>]
let ``no daemon running says so without warning about anything`` () =
    let out, code =
        captureBothStreams (fun () -> reportStopOutcome StopOutcome.NothingRunning)

    test <@ code = 0 @>
    test <@ out.Contains "No daemon running" @>
    test <@ not (out.Contains "⚠") @>
    test <@ not (out.Contains "✓") @>

[<Fact(Timeout = 15000)>]
let ``an unverified stop claims neither outcome, in the message AND in the exit code`` () =
    // Exit 2 — "could not establish", the code this CLI already gives a check that
    // reached no verdict. Exit 0 here would be the defect in a quieter voice: a
    // wrapper reads the code, not the prose, and would take it for a clean stop.
    let out, code =
        captureBothStreams (fun () -> reportStopOutcome (StopOutcome.Unverified 1))

    test <@ code = 2 @>
    test <@ not (out.Contains "✓") @>
    test <@ out.Contains "daemon.pid" @>

[<Fact(Timeout = 15000)>]
let ``the three stop exit codes are distinct claims`` () =
    // 0 established gone, 1 established still there, 2 established neither. A caller
    // that only branches on zero/non-zero still gets the safe answer in all three.
    let code outcome =
        snd (captureBothStreams (fun () -> reportStopOutcome outcome))

    test <@ code StopOutcome.NothingRunning = 0 @>
    test <@ code (StopOutcome.Stopped 1) = 0 @>
    test <@ code (StopOutcome.StillAlive(4242, 1)) = 1 @>
    test <@ code (StopOutcome.Unverified 1) = 2 @>


// ---------------------------------------------------------------------------
// The faults that actually reach `classifyIpcFault` and used to land in `Other`.
//
// `Other` printed the raw exception and — because `ipcErrorHint` returned `None` for
// it — nothing else. The two faults below are the ones with the MOST specific remedies
// and they both went there, for a reason invisible from the exception message:
// `RemoteMethodNotFoundException` and `ConnectionLostException` do not derive from
// `RemoteInvocationException`. All three are siblings under `RemoteRpcException`, so
// the `:? RemoteInvocationException` pattern that reconstructs daemon-side faults never
// matched either of them, and the fallthrough classified them client-side as `Other`.
//
// These are driven through a REAL StreamJsonRpc connection over a real named pipe
// rather than a hand-built exception: the claim being pinned is "this type reaches this
// path", which a constructed exception cannot establish.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``RemoteMethodNotFound and ConnectionLost are NOT RemoteInvocationException`` () =
    // The whole mechanism of the bug, in one assertion.
    let isInvocation (t: Type) =
        typeof<StreamJsonRpc.RemoteInvocationException>.IsAssignableFrom t

    test <@ not (isInvocation typeof<StreamJsonRpc.RemoteMethodNotFoundException>) @>
    test <@ not (isInvocation typeof<StreamJsonRpc.ConnectionLostException>) @>
    // …and they are all the same family, which is why they arrive on the same path.
    test
        <@ typeof<StreamJsonRpc.RemoteRpcException>.IsAssignableFrom typeof<StreamJsonRpc.RemoteMethodNotFoundException> @>

    test <@ typeof<StreamJsonRpc.RemoteRpcException>.IsAssignableFrom typeof<StreamJsonRpc.ConnectionLostException> @>

/// A daemon serving `pipeName` whose RPC surface is `target` (or, when `target` is
/// None, one that accepts the connection and then dies without answering). Returns the
/// server task so the test can await its teardown.
let private serveRpc (pipeName: string) (target: obj option) =
    System.Threading.Tasks.Task.Run(fun () ->
        use server =
            new System.IO.Pipes.NamedPipeServerStream(
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous
            )

        server.WaitForConnection()

        match target with
        | None ->
            // The daemon exits mid-call: the client has connected and is waiting on a
            // reply that will never come because the process serving it is gone. The
            // blocking read IS the synchronisation — it returns once the client's
            // request is on the wire, so there is no sleep here betting on that.
            server.Read(Array.zeroCreate<byte> 1, 0, 1) |> ignore
            server.Dispose()
        | Some t ->
            let handler = new StreamJsonRpc.HeaderDelimitedMessageHandler(server :> Stream)

            use rpc = new StreamJsonRpc.JsonRpc(handler, t)
            rpc.StartListening()
            rpc.Completion.Wait())

/// Invoke `methodName` the way `IpcClient.invoke` does, and return whatever it threw.
let private faultFromCall (pipeName: string) (methodName: string) : exn =
    use pipeClient =
        new System.IO.Pipes.NamedPipeClientStream(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous
        )

    pipeClient.Connect(5000)

    let handler = new StreamJsonRpc.HeaderDelimitedMessageHandler(pipeClient :> Stream)

    use rpc = new StreamJsonRpc.JsonRpc(handler)
    rpc.StartListening()

    try
        rpc.InvokeAsync<string>(methodName, [||]).GetAwaiter().GetResult() |> ignore
        failwith "expected the call to fail"
    with ex ->
        unwrapIpcException ex

/// An fshw daemon from a DIFFERENT build: it serves RPC, but not the method this CLI calls.
type private OtherBuildDaemon() =
    member _.SomeOtherMethod() : System.Threading.Tasks.Task<string> =
        System.Threading.Tasks.Task.FromResult "ok"

[<Fact(Timeout = 30000)>]
let ``a version-mismatched daemon is named as one, with the fshw stop remedy`` () =
    let pipeName = $"fp-{Guid.NewGuid():N}"
    let server = serveRpc pipeName (Some(OtherBuildDaemon() :> obj))

    try
        let fault = faultFromCall pipeName "GetStatus"
        test <@ fault :? StreamJsonRpc.RemoteMethodNotFoundException @>

        match classifyIpcFault fault with
        | IpcFault.DaemonMethodMissing actual -> test <@ obj.ReferenceEquals(actual, fault) @>
        | actual -> failwithf "expected DaemonMethodMissing, got %A" actual

        // The daemon ANSWERED, so "could not connect" would send the reader to the pipe.
        let headline = ipcErrorHeadline fault
        test <@ not (headline.Contains "Could not connect to daemon") @>
        test <@ headline.Contains "different fshw build" @>

        let hint = ipcErrorHint fault
        test <@ hint.Contains "fshw stop" @>
        test <@ hint.Contains "DIFFERENT fshw build" @>
    finally
        server.Wait(TimeSpan.FromSeconds 5.0) |> ignore

[<Fact(Timeout = 30000)>]
let ``a daemon that dies mid-call is named as one, not left unclassified`` () =
    let pipeName = $"fp-{Guid.NewGuid():N}"
    let server = serveRpc pipeName None

    try
        let fault = faultFromCall pipeName "GetStatus"
        test <@ fault :? StreamJsonRpc.ConnectionLostException @>

        match classifyIpcFault fault with
        | IpcFault.ConnectionLost actual -> test <@ obj.ReferenceEquals(actual, fault) @>
        | actual -> failwithf "expected ConnectionLost, got %A" actual

        let hint = ipcErrorHint fault
        test <@ hint.Contains "EXITED" @>
        test <@ hint.Contains "logs/daemon.log" @>
    finally
        server.Wait(TimeSpan.FromSeconds 5.0) |> ignore

[<Fact(Timeout = 30000)>]
let ``neither new fault restarts the daemon`` () =
    // Self-heal exists for corrupted frames. A build mismatch survives a restart-and-
    // retry unchanged, and a daemon that has already exited has nothing to restart.
    let pipeName = $"fp-{Guid.NewGuid():N}"
    let server = serveRpc pipeName (Some(OtherBuildDaemon() :> obj))

    let faults =
        try
            let missing = faultFromCall pipeName "GetStatus"
            server.Wait(TimeSpan.FromSeconds 5.0) |> ignore
            let deadPipe = $"fp-{Guid.NewGuid():N}"
            let dead = serveRpc deadPipe None
            let lost = faultFromCall deadPipe "GetStatus"
            dead.Wait(TimeSpan.FromSeconds 5.0) |> ignore
            [ missing; lost ]
        finally
            server.Wait(TimeSpan.FromSeconds 5.0) |> ignore

    for fault in faults do
        let mutable restarts = 0

        let exitCode =
            runIpcWithSelfHeal
                (fun () ->
                    restarts <- restarts + 1
                    true)
                (fun _ -> 7)
                (fun () -> raise fault)

        test <@ restarts = 0 @>
        test <@ exitCode = 7 @>

[<Fact(Timeout = 15000)>]
let ``every IpcFault case yields a non-empty hint`` () =
    // The totality guarantee, stated as a test as well as a type: a reader who hits any
    // classified fault is told what to do about it.
    let samples: exn list =
        [ OutOfMemoryException "client oom"
          remoteFault
              "System.OutOfMemoryException"
              "daemon oom"
              "at System.Text.Json.JsonSerializer.Serialize[TValue](TValue value)"
          remoteFault
              "System.OutOfMemoryException"
              "frame"
              "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"
          TimeoutException "slow"
          remoteFault "System.InvalidOperationException" "plugin blew up" ""
          InvalidOperationException "nothing known about this" ]

    for sample in samples do
        let hint = ipcErrorHint sample
        test <@ hint.Length > 0 @>
        test <@ not (hint.Contains "None") @>
