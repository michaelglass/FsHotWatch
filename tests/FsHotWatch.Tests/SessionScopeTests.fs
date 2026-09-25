/// What one worktree session carries in its ExecutionContext when several share a
/// process: its log sink, the environment its children inherit, and the registry that
/// reaps them. Each must reach exactly its own session's work — a sibling's children
/// never see this session's PATH, a sibling's shutdown never kills this session's
/// children, a sibling's log lines never land in this session's log.
module FsHotWatch.Tests.SessionScopeTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Isolated construction
// ---------------------------------------------------------------------------

let private probe = Threading.AsyncLocal<string>()

[<Fact(Timeout = 15000)>]
let ``isolated work sees none of the caller's AsyncLocals, and its own do not leak back`` () =
    probe.Value <- "caller"

    let seen =
        isolated (fun () ->
            let before = probe.Value
            probe.Value <- "session"
            before)

    test <@ isNull seen @>
    test <@ probe.Value = "caller" @>

[<Fact(Timeout = 15000)>]
let ``isolated works the same when the caller already suppressed flow`` () =
    probe.Value <- "caller"

    let seen =
        using (Threading.ExecutionContext.SuppressFlow()) (fun _ -> isolated (fun () -> probe.Value))

    test <@ isNull seen @>

[<Fact(Timeout = 15000)>]
let ``isolated returns the work's value and rethrows its exception`` () =
    test <@ isolated (fun () -> 42) = 42 @>

    let ex =
        Assert.Throws<InvalidOperationException>(fun () -> isolated (fun () -> invalidOp "boom") |> ignore)

    test <@ ex.Message = "boom" @>

// ---------------------------------------------------------------------------
// Log sinks
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``each session's log lines land in its own sink only`` () =
    let a = ConcurrentQueue<string>()
    let b = ConcurrentQueue<string>()

    let logAs (sink: ConcurrentQueue<string>) (message: string) =
        isolated (fun () ->
            use _ =
                installSink
                    { Write = sink.Enqueue
                      Level = LogLevel.Info }

            info "session" message)

    logAs a "from A"
    logAs b "from B"

    test
        <@
            a.Count = 1
            && a |> Seq.forall (fun l -> l.Contains "[session]" && l.EndsWith "from A")
        @>

    test <@ b.Count = 1 && b |> Seq.forall (fun l -> l.EndsWith "from B") @>

[<Fact(Timeout = 15000)>]
let ``a sink carries its own level`` () =
    let lines = ConcurrentQueue<string>()

    isolated (fun () ->
        use _ =
            installSink
                { Write = lines.Enqueue
                  Level = LogLevel.Warning }

        test <@ isEnabled LogLevel.Warning && not (isEnabled LogLevel.Info) @>
        info "t" "dropped"
        warn "t" "kept"
        error "t" "kept too"
        debug "t" "dropped too")

    test
        <@
            List.ofSeq lines
            |> List.map (fun l -> l.EndsWith "kept" || l.EndsWith "kept too") = [ true; true ]
        @>

[<Fact(Timeout = 15000)>]
let ``disposing a sink restores the one it replaced`` () =
    let outer = ConcurrentQueue<string>()
    let inner = ConcurrentQueue<string>()

    isolated (fun () ->
        use _ =
            installSink
                { Write = outer.Enqueue
                  Level = LogLevel.Info }

        (using
            (installSink
                { Write = inner.Enqueue
                  Level = LogLevel.Info })
            (fun _ -> info "t" "inner"))

        info "t" "outer")

    test <@ inner.Count = 1 && outer.Count = 1 @>

[<Fact(Timeout = 15000)>]
let ``a file sink appends dated lines to its file`` () =
    withTempDir "sink" (fun dir ->
        let path = Path.Combine(dir, "logs", "daemon.log")
        let sink = fileSink path LogLevel.Info

        isolated (fun () ->
            use _ = installSink sink
            info "config" "one"
            info "config" "two")

        let lines = File.ReadAllLines path
        test <@ lines.Length = 2 @>
        test <@ lines[0].StartsWith "  [config] " && lines[0].EndsWith " one" @>)

// ---------------------------------------------------------------------------
// Session environment
// ---------------------------------------------------------------------------

let private envOf (root: string) (vars: (string * string) list) =
    SessionEnvironment.create root (Map.ofList vars)

[<Fact(Timeout = 15000)>]
let ``the MSBuild-relevant variables are the listed prefixes and names, case-insensitively`` () =
    for name in
        [ "DOTNET_ROOT"
          "dotnet_cli_home"
          "MSBuildSDKsPath"
          "MSBUILDDISABLENODEREUSE"
          "NUGET_PACKAGES"
          "Configuration"
          "configuration"
          "Platform"
          "TargetFramework"
          "RuntimeIdentifier"
          "ContinuousIntegrationBuild"
          "CI"
          "TF_BUILD"
          "GITHUB_ACTIONS" ] do
        test <@ isMsbuildRelevant name @>

    for name in [ "PATH"; "HOME"; "TERM"; "DOTNETX"; "MY_CONFIGURATION"; "CIX" ] do
        test <@ not (isMsbuildRelevant name) @>

[<Fact(Timeout = 15000)>]
let ``environments that differ only in their own worktree roots do not mismatch`` () =
    let host =
        envOf
            "/r"
            [ "NUGET_PACKAGES", "/r/.nuget"
              "PATH", "/r/scripts:/usr/bin"
              "DOTNET_ROOT", "/nix/dotnet" ]

    let client =
        envOf
            "/r/.workspaces/b"
            [ "NUGET_PACKAGES", "/r/.workspaces/b/.nuget"
              "PATH", "/r/.workspaces/b/scripts:/usr/bin"
              "DOTNET_ROOT", "/nix/dotnet" ]

    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches host client) @>

[<Fact(Timeout = 15000)>]
let ``an MSBuild-relevant difference is a mismatch naming the variable and both values`` () =
    let host = envOf "/r" [ "Configuration", "Debug"; "DOTNET_ROOT", "/a" ]
    let client = envOf "/b" [ "Configuration", "Release"; "NUGET_PACKAGES", "/cache" ]

    let expected: EnvironmentMismatch list =
        [ { Name = "Configuration"
            Host = Some "Debug"
            Client = Some "Release" }
          { Name = "DOTNET_ROOT"
            Host = Some "/a"
            Client = None }
          { Name = "NUGET_PACKAGES"
            Host = None
            Client = Some "/cache" } ]

    test <@ SessionEnvironment.msbuildMismatches host client = expected @>

[<Fact(Timeout = 15000)>]
let ``variables a .NET process writes for itself never count as a mismatch`` () =
    // In-process MSBuild discovery writes the MSBuild paths into the host's own
    // environment once its first session is built, and the .NET host sets its own paths:
    // they describe the process, not the client's shell. The SDK they name is compared
    // separately, by the toolchain check.
    let host =
        envOf
            "/r"
            [ "MSBUILD_EXE_PATH", "/sdk/MSBuild.dll"
              "MSBuildExtensionsPath", "/sdk/"
              "MSBuildSDKsPath", "/sdk/Sdks"
              "DOTNET_HOST_PATH", "/dotnet/dotnet"
              "DOTNET_ROOT_ARM64", "/dotnet" ]

    let client = envOf "/b" []
    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches host client) @>
    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches client host) @>

[<Fact(Timeout = 15000)>]
let ``the thread-suspend setting a launched host runs with is not a mismatch`` () =
    // On macOS the CLI launches a repository host with thread-suspend injection off. A
    // GC setting of the host process: it changes no evaluation, and a shell that does
    // not set it must still attach.
    let host = envOf "/r" [ "DOTNET_INTERNAL_ThreadSuspendInjection", "0" ]
    let client = envOf "/b" []
    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches host client) @>
    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches client host) @>

[<Fact(Timeout = 15000)>]
let ``an irrelevant difference is not a mismatch`` () =
    let host = envOf "/r" [ "PATH", "/usr/bin"; "TERM", "xterm" ]
    let client = envOf "/b" [ "PATH", "/opt/bin"; "EDITOR", "vi" ]
    test <@ List.isEmpty (SessionEnvironment.msbuildMismatches host client) @>

[<Fact(Timeout = 15000)>]
let ``a mismatch is described with what differed`` () =
    let text =
        SessionEnvironment.describeMismatch
            { Name = "Configuration"
              Host = Some "Debug"
              Client = None }

    test <@ text.Contains "Configuration" && text.Contains "Debug" && text.Contains "unset" @>

[<Fact(Timeout = 15000)>]
let ``the process environment is captured with its root`` () =
    let env = SessionEnvironment.ofProcess "/r"
    test <@ SessionEnvironment.root env = "/r" @>
    test <@ (SessionEnvironment.variables env).ContainsKey "PATH" @>

[<Fact(Timeout = 15000)>]
let ``no session environment is in scope by default`` () =
    test <@ isolated (fun () -> SessionEnvironment.current ()) |> Option.isNone @>

// ---------------------------------------------------------------------------
// Children inherit THEIR session's environment
// ---------------------------------------------------------------------------

let private printPath () =
    ProcessHelper.runProcess
        "/bin/sh"
        "-c \"printf %s \\\"$PATH\\\"\""
        "."
        []
        (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0))

let private stdoutOf outcome =
    match outcome with
    | ProcessHelper.Succeeded(ProcessHelper.ProcessOutput.Drained text) -> text
    | other -> failwith $"the child did not complete cleanly: %A{other}"

[<Fact(Timeout = 60000)>]
let ``session B's child sees B's PATH, never A's or the host's`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX shell"

    let base' = SessionEnvironment.variables (SessionEnvironment.ofProcess "/")

    let sessionEnv (root: string) =
        SessionEnvironment.create root (base'.Add("PATH", $"%s{root}/scripts/toolchain-bin:/usr/bin:/bin"))

    let runIn (root: string) =
        isolated (fun () ->
            use _ = SessionEnvironment.install (sessionEnv root)
            stdoutOf (printPath ()))

    let a = runIn "/repo"
    let b = runIn "/repo/.workspaces/b"
    test <@ b = "/repo/.workspaces/b/scripts/toolchain-bin:/usr/bin:/bin" @>
    test <@ a = "/repo/scripts/toolchain-bin:/usr/bin:/bin" @>
    // Outside any session the host's own environment still applies.
    test <@ stdoutOf (printPath ()) = Environment.GetEnvironmentVariable "PATH" @>

[<Fact(Timeout = 60000)>]
let ``a caller's explicit overlay still wins over the session environment`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX shell"

    let env =
        SessionEnvironment.create "/s" (Map.ofList [ "PATH", "/usr/bin:/bin"; "FSHW_PROBE", "session" ])

    let seen =
        isolated (fun () ->
            use _ = SessionEnvironment.install env

            ProcessHelper.runProcess
                "/bin/sh"
                "-c \"printf %s \\\"$FSHW_PROBE\\\"\""
                "."
                [ "FSHW_PROBE", "overlay" ]
                (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0))
            |> stdoutOf)

    test <@ seen = "overlay" @>

// ---------------------------------------------------------------------------
// One session's teardown never reaps a sibling's children
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``killing session B's registry leaves session A's child running`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX sleep"

    let registryA = ProcessRegistry.Registry()
    let registryB = ProcessRegistry.Registry()

    let spawnIn (registry: ProcessRegistry.Registry) =
        isolated (fun () ->
            use _ = ProcessRegistry.install registry
            let p = Process.Start("sleep", "30")
            ProcessRegistry.track p
            p)

    use childA = spawnIn registryA
    use childB = spawnIn registryB

    try
        registryB.KillAll()
        test <@ childB.WaitForExit 10000 @>
        test <@ not childA.HasExited @>
    finally
        registryA.KillAll()

    test <@ childA.WaitForExit 10000 @>

[<Fact(Timeout = 60000)>]
let ``a bare command is found on the session's PATH, not the host's`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX shell"

    withTempDir "session-path" (fun dir ->
        // The session's own `dotnet`, the way a workspace's toolchain-bin wraps it.
        let bin = Path.Combine(dir, "toolchain-bin")
        Directory.CreateDirectory bin |> ignore
        let wrapper = Path.Combine(bin, "dotnet")
        File.WriteAllText(wrapper, "#!/bin/sh\nprintf session-dotnet\n")
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        let env =
            SessionEnvironment.create dir (Map.ofList [ "PATH", $"%s{bin}:/usr/bin:/bin" ])

        let seen =
            isolated (fun () ->
                use _ = SessionEnvironment.install env

                ProcessHelper.runProcess
                    "dotnet"
                    "--version"
                    "."
                    []
                    (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0))
                |> stdoutOf)

        test <@ seen = "session-dotnet" @>)

[<Fact(Timeout = 15000)>]
let ``a session without a PATH finds no bare command, rather than falling back to the host's`` () =
    let env = SessionEnvironment.create "/s" (Map.ofList [ "HOME", "/s" ])

    let refused =
        isolated (fun () ->
            use _ = SessionEnvironment.install env

            try
                ProcessHelper.runProcess
                    "sh"
                    "-c true"
                    "."
                    []
                    (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 10.0))
                |> ignore

                None
            with :? System.ComponentModel.Win32Exception as ex ->
                Some ex.Message)

    test
        <@
            refused
            |> Option.exists (fun message -> message.Contains "not found on this worktree's PATH")
        @>

[<Fact(Timeout = 15000)>]
let ``a bare command resolves against a PATH, first match wins, rooted commands are left alone`` () =
    withTempDir "resolve-path" (fun dir ->
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")
        Directory.CreateDirectory a |> ignore
        Directory.CreateDirectory b |> ignore

        let exe (folder: string) (name: string) =
            let path = Path.Combine(folder, name)
            File.WriteAllText(path, "#!/bin/sh\n")

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

            path

        let inB = exe b "tool"
        let inA = exe a "tool"
        File.WriteAllText(Path.Combine(a, "plain"), "not executable")
        let path = $"%s{a}:%s{b}"
        test <@ ProcessHelper.resolveOnPath path "tool" = inA @>
        test <@ ProcessHelper.resolveOnPath $"%s{b}:%s{a}" "tool" = inB @>
        test <@ ProcessHelper.resolveOnPath path "missing" = "missing" @>
        test <@ ProcessHelper.resolveOnPath path "/bin/sh" = "/bin/sh" @>
        test <@ ProcessHelper.resolveOnPath path "./tool" = "./tool" @>
        test <@ ProcessHelper.resolveOnPath "" "tool" = "tool" @>

        if not (OperatingSystem.IsWindows()) then
            test <@ ProcessHelper.resolveOnPath path "plain" = "plain" @>)
