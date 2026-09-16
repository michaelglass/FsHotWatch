/// a workspace whose analyzers are not built gets a message saying so —
/// per path, classified from the filesystem — and exit 2, never an unhandled exception.
///
/// In the LogGlobal collection because the start-path tests capture the process-global
/// stderr.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AnalyzerPathDiagnosisTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.AnalyzerPathDiagnosis
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.Program
open FsHotWatch.Daemon
open FsHotWatch.ErrorLedger
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

/// Text only an escaped .NET exception would put on the surface.
let private assertNoStackTrace (text: string) =
    test <@ not (text.Contains("Unhandled exception")) @>
    test <@ not (text.Contains("   at ")) @>
    test <@ not (text.Contains("ConfigError")) @>
    test <@ not (text.Contains("Exception")) @>

let private analyzersConfig (paths: string list) (hints: (string * string) list) : DaemonConfiguration =
    { stripConfig (defaultTestConfig ()) with
        Analyzers =
            Some
                {| Paths = paths
                   FailOnSeverity = DiagnosticSeverity.Hint
                   BootstrapHints = Map.ofList hints |} }

/// Bytes that are a `.dll` by name and nothing by content — "not an analyzer".
let private writeJunkDll (dir: string) (name: string) =
    Directory.CreateDirectory dir |> ignore
    File.WriteAllBytes(Path.Combine(dir, name), [| 0x4Duy; 0x5Auy; 0x00uy; 0x13uy; 0x37uy |])

let private noIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "" }
      Scan = fun _ -> async { return "" }
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
      FormatAll = fun _ -> async { return "" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      Invalidate = fun _ -> async { return "" }
      IsRunning = fun _ -> false
      LaunchDaemon = fun _ _ _ -> () }

/// The user-facing text a `ConfigError` carries. `exn.Message` on an F# exception is its
/// structured `%A` rendering (`ConfigError "…"`), which is not what any surface prints.
let private configErrorText (register: unit -> unit) : string =
    match Assert.Throws<ConfigError>(register) :> exn with
    | ConfigError message -> message
    | other -> failwith $"expected ConfigError, got %s{other.GetType().Name}"

/// A repo root the zero-projects pre-check accepts.
let private withProjectRepo (prefix: string) (body: string -> unit) =
    withTempDir prefix (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src", "App")) |> ignore
        writeMinimalFsproj (Path.Combine(root, "src", "App", "App.fsproj")) "net10.0" []
        body root)

// --- the classifier ---

[<Fact(Timeout = 5000)>]
let ``an absent directory classifies as Missing`` () =
    withTempDir "missing" (fun root ->
        test
            <@ classify (Path.Combine(root, "analyzers", "bin", "Release", "net10.0")) = AnalyzerPathProblem.Missing @>)

[<Fact(Timeout = 5000)>]
let ``an absent Release directory whose Debug twin exists classifies as built in the other configuration`` () =
    withTempDir "twin" (fun root ->
        let debug = Path.Combine(root, "rules", "bin", "Debug", "net10.0")
        Directory.CreateDirectory debug |> ignore
        let release = Path.Combine(root, "rules", "bin", "Release", "net10.0")

        test <@ classify release = AnalyzerPathProblem.BuiltInOtherConfiguration debug @>)

[<Fact(Timeout = 5000)>]
let ``a present directory with no dlls classifies as Empty`` () =
    withTempDir "empty" (fun root ->
        let dir = Path.Combine(root, "bin")
        Directory.CreateDirectory dir |> ignore
        // A non-DLL file does not make the directory a candidate.
        File.WriteAllText(Path.Combine(dir, "README.txt"), "not an assembly")

        test <@ classify dir = AnalyzerPathProblem.Empty @>)

[<Fact(Timeout = 5000)>]
let ``a directory whose dlls are not analyzers classifies as NoAnalyzersInDlls with the count`` () =
    withTempDir "junk" (fun root ->
        let dir = Path.Combine(root, "bin")
        writeJunkDll dir "Junk.Analyzer.dll"
        writeJunkDll dir "Other.dll"

        test <@ classify dir = AnalyzerPathProblem.NoAnalyzersInDlls 2 @>)

// --- the rendered message ---

[<Fact(Timeout = 5000)>]
let ``the message names every unresolved path with its classification and carries no stack trace`` () =
    let message =
        render
            [ { Path = "/repo/a/bin/Release/net10.0"
                Problem = AnalyzerPathProblem.Missing
                BootstrapHint = None }
              { Path = "/repo/b/bin/Release/net10.0"
                Problem = AnalyzerPathProblem.BuiltInOtherConfiguration "/repo/b/bin/Debug/net10.0"
                BootstrapHint = None }
              { Path = "/repo/c/bin"
                Problem = AnalyzerPathProblem.Empty
                BootstrapHint = None }
              { Path = "/repo/d/bin"
                Problem = AnalyzerPathProblem.NoAnalyzersInDlls 3
                BootstrapHint = None } ]

    let lines = message.Split('\n')

    let classificationOf (path: string) =
        let index =
            lines |> Array.findIndex (fun l -> l.EndsWith(path, StringComparison.Ordinal))

        lines[index + 1]

    test <@ (classificationOf "/repo/a/bin/Release/net10.0").Contains("MISSING") @>
    test <@ (classificationOf "/repo/b/bin/Release/net10.0").Contains("WRONG CONFIGURATION") @>
    test <@ (classificationOf "/repo/b/bin/Release/net10.0").Contains("/repo/b/bin/Debug/net10.0") @>
    test <@ (classificationOf "/repo/c/bin").Contains("EMPTY") @>
    test <@ (classificationOf "/repo/d/bin").Contains("NO ANALYZERS") @>
    test <@ (classificationOf "/repo/d/bin").Contains("3 .dll") @>
    test <@ message.Contains("loaded 0 analyzers") @>
    test <@ message.Contains(".fshw.json analyzers.paths") @>
    test <@ not (message.Contains("to build it")) @>
    assertNoStackTrace message

[<Fact(Timeout = 15000)>]
let ``registerPlugins classifies each zero-loading path from disk and keeps the healthy guard`` () =
    withTempDir "register" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        let emptyDir = Path.Combine(root, "empty-bin")
        Directory.CreateDirectory emptyDir |> ignore
        let junkDir = Path.Combine(root, "junk-bin")
        writeJunkDll junkDir "Junk.Analyzer.dll"

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) root Daemon.DaemonOptions.defaults

        let config = analyzersConfig [ "never-built/bin"; emptyDir; junkDir ] []

        let message = configErrorText (fun () -> registerPlugins daemon root config)

        test <@ message.Contains(Path.Combine(root, "never-built", "bin")) @>
        test <@ message.Contains("MISSING") @>
        test <@ message.Contains("EMPTY") @>
        test <@ message.Contains("NO ANALYZERS") @>
        assertNoStackTrace message
        test <@ not (daemon.Host.GetAllStatuses().ContainsKey("analyzers")) @>)

// --- bootstrapHints ---

[<Fact(Timeout = 15000)>]
let ``a configured bootstrap hint is echoed verbatim for its path and absent adds nothing`` () =
    withTempDir "hint" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) root Daemon.DaemonOptions.defaults

        let hint = "mise run build-analyzers -- --configuration Release"

        let config = analyzersConfig [ "hinted/bin"; "unhinted/bin" ] [ "hinted/bin", hint ]

        let lines =
            (configErrorText (fun () -> registerPlugins daemon root config)).Split('\n')

        let blockOf (path: string) =
            lines
            |> Array.skipWhile (fun l -> not (l.EndsWith(path, StringComparison.Ordinal)))
            |> Array.skip 1
            |> Array.takeWhile (fun l -> l.StartsWith("    ", StringComparison.Ordinal))

        test <@ blockOf "hinted/bin" |> Array.contains $"    to build it: %s{hint}" @>

        test
            <@
                blockOf "unhinted/bin"
                |> Array.forall (fun l -> not (l.Contains("to build it")))
            @>)

[<Fact(Timeout = 5000)>]
let ``parseConfig reads bootstrapHints and defaults to none`` () =
    let defaults = defaultTestConfig ()

    let withHints =
        parseConfig """{"analyzers": {"paths": ["a/bin"], "bootstrapHints": {"a/bin": "dotnet build a"}}}""" defaults

    let withoutHints = parseConfig """{"analyzers": {"paths": ["a/bin"]}}""" defaults

    test <@ withHints.Analyzers.Value.BootstrapHints = Map.ofList [ "a/bin", "dotnet build a" ] @>
    test <@ withoutHints.Analyzers.Value.BootstrapHints = Map.empty @>

[<Fact(Timeout = 5000)>]
let ``parseConfig refuses a bootstrap hint for an unconfigured path or a non-string hint`` () =
    let defaults = defaultTestConfig ()

    let unconfigured =
        Assert.Throws<ConfigError>(fun () ->
            parseConfig """{"analyzers": {"paths": ["a/bin"], "bootstrapHints": {"b/bin": "x"}}}""" defaults
            |> ignore)

    test <@ unconfigured.Message.Contains("b/bin") @>

    Assert.Throws<ConfigError>(fun () ->
        parseConfig """{"analyzers": {"paths": ["a/bin"], "bootstrapHints": {"a/bin": 3}}}""" defaults
        |> ignore)
    |> ignore

// --- the startup path ---

[<Fact(Timeout = 30000)>]
let ``daemon start with unbuilt analyzers exits 2 with the message and no unhandled exception`` () =
    withProjectRepo "start" (fun root ->
        let config = analyzersConfig [ "analyzers/bin/Release/net10.0" ] []

        let createDaemon (r: string) =
            Daemon.createWith (Unchecked.defaultof<_>) r Daemon.DaemonOptions.defaults

        let stderr, exitCode =
            captureStderr (fun () ->
                executeCommand createDaemon (noIpc ()) root "fshw-start" Start defaultGlobalOptions config 5.0)

        // Positive control that fail-loud survives: the refusal is still exit 2.
        test <@ exitCode = 2 @>
        test <@ stderr.Contains("MISSING") @>
        test <@ stderr.Contains(Path.Combine(root, "analyzers", "bin", "Release", "net10.0")) @>
        assertNoStackTrace stderr
        // Recorded for the CLI that launched this detached daemon.
        test <@ (DaemonStartupFailure.tryRead root |> Option.defaultValue "").Contains("MISSING") @>
        test <@ not (File.Exists(Path.Combine(root, ".fshw", "daemon.pid"))) @>)

[<Fact(Timeout = 30000)>]
let ``check whose daemon refused to start prints the reason, not only a log pointer, and exits 2 promptly`` () =
    withProjectRepo "check" (fun root ->
        let refusal =
            render
                [ { Path = "/repo/analyzers/bin"
                    Problem = AnalyzerPathProblem.Missing
                    BootstrapHint = Some "mise run build-analyzers" } ]

        // The detached daemon refuses: it records why and never opens the pipe.
        let ipc =
            { noIpc () with
                LaunchDaemon = fun repoRoot _ _ -> DaemonStartupFailure.record repoRoot refusal }

        let stopwatch = Stopwatch.StartNew()

        let stderr, exitCode =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    root
                    "fshw-check"
                    (Check [])
                    defaultGlobalOptions
                    (stripConfig (defaultTestConfig ()))
                    20.0)

        test <@ exitCode = 2 @>
        test <@ stderr.Contains("Failed to start daemon") @>
        test <@ stderr.Contains("/repo/analyzers/bin") @>
        test <@ stderr.Contains("MISSING") @>
        test <@ stderr.Contains("to build it: mise run build-analyzers") @>
        assertNoStackTrace stderr
        // It stopped waiting once the refusal was recorded instead of sitting out the
        // 20 s startup timeout for a pipe that would never open.
        test <@ stopwatch.Elapsed < TimeSpan.FromSeconds 10.0 @>)

[<Fact(Timeout = 15000)>]
let ``a launch clears a refusal recorded by an earlier launch`` () =
    withTempDir "stale" (fun root ->
        DaemonStartupFailure.record root "an old refusal"

        let started =
            startFreshDaemonWith defaultFileOps (noIpc ()) root "fshw-stale" "hash" "" "logs" 0.0

        test <@ not started @>
        test <@ DaemonStartupFailure.tryRead root = None @>

        test
            <@
                DaemonStartupFailure.describeLaunchFailure None
                |> fun s -> s.Contains("logs/daemon.log")
            @>)

// --- the startup-failure record's own I/O arms ---

[<Fact(Timeout = 5000)>]
let ``a record that was never written reads as none, with or without a state directory`` () =
    withTempDir "absent" (fun root ->
        // No `.fshw/` at all: the read fails on the directory.
        test <@ DaemonStartupFailure.tryRead root = None @>
        // `.fshw/` exists, the record does not: the read fails on the file.
        Directory.CreateDirectory(Path.Combine(root, ".fshw")) |> ignore
        test <@ DaemonStartupFailure.tryRead root = None @>
        // Clearing a record that does not exist is a no-op, not an error.
        DaemonStartupFailure.clear root
        test <@ DaemonStartupFailure.tryRead root = None @>)

[<Fact(Timeout = 5000)>]
let ``a blank record reads as none and falls back to the log pointer`` () =
    withTempDir "blank" (fun root ->
        DaemonStartupFailure.record root "  \n\t "

        let recorded = DaemonStartupFailure.tryRead root

        test <@ recorded = None @>
        test <@ (DaemonStartupFailure.describeLaunchFailure recorded).EndsWith("See logs/daemon.log.") @>)

[<Fact(Timeout = 5000)>]
let ``a recorded refusal round-trips trimmed and is printed in full`` () =
    withTempDir "roundtrip" (fun root ->
        DaemonStartupFailure.record root "\nfirst line\nsecond line\n"

        let recorded = DaemonStartupFailure.tryRead root

        test <@ recorded = Some "first line\nsecond line" @>

        test
            <@
                (DaemonStartupFailure.describeLaunchFailure recorded)
                    .EndsWith("refused to start:\nfirst line\nsecond line")
            @>

        DaemonStartupFailure.clear root
        test <@ DaemonStartupFailure.tryRead root = None @>)

[<Fact(Timeout = 5000)>]
let ``a record that cannot be written, cleared or read never throws`` () =
    withTempDir "unwritable" (fun root ->
        // `.fshw` is a FILE, so the record's directory cannot be created.
        File.WriteAllText(Path.Combine(root, ".fshw"), "not a directory")
        DaemonStartupFailure.record root "lost"
        test <@ DaemonStartupFailure.tryRead root = None @>)

    withTempDir "dir-in-place" (fun root ->
        // A DIRECTORY where the record belongs: it can be neither deleted as a file nor read.
        let inPlace = DaemonStartupFailure.path root
        Directory.CreateDirectory inPlace |> ignore
        DaemonStartupFailure.clear root
        test <@ Directory.Exists inPlace @>
        test <@ DaemonStartupFailure.tryRead root = None @>)
