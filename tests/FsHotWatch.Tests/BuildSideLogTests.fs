/// fshw's side binary log of a dotnet build: when it is attached, where it goes, and how
/// an overrun report reads the targets that were running out of it.
///
/// The replay is injected everywhere but in the `replayBinlogWith` tests, which drive a
/// stand-in `dotnet`; a real build that stalls in a known target is in
/// FsHotWatch.IntegrationTests/PluginTimeoutTests.fs.
module FsHotWatch.Tests.BuildSideLogTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.Build.BuildPlugin

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-sidelog-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

// ---------------------------------------------------------------------------
// sideLogFor — attached to a command line that invokes dotnet, unless the user's own
// MSBUILD_LOGGING_ARGS is set

[<Fact>]
let ``the side log is attached to a shell wrapper that invokes dotnet`` () =
    test
        <@
            sideLogFor "sh" "-c \"dotnet build -v q\"" [] None "/r/.fshw/build-binlog" = SideLog.Attached
                "/r/.fshw/build-binlog"
        @>

[<Fact>]
let ``the side log is not attached to a command that never names dotnet`` () =
    test
        <@
            match sideLogFor "sh" "-c \"make all\"" [] None "/d" with
            | SideLog.NotAttached reason -> reason.Contains "never names dotnet"
            | SideLog.Attached _ -> false
        @>

[<Fact>]
let ``a configured MSBUILD_LOGGING_ARGS wins over the side log`` () =
    test
        <@
            match sideLogFor "dotnet" "build" [ SideLogVariable, "-bl:mine.binlog" ] None "/d" with
            | SideLog.NotAttached reason -> reason.Contains "-bl:mine.binlog"
            | SideLog.Attached _ -> false
        @>

[<Fact>]
let ``an inherited MSBUILD_LOGGING_ARGS wins over the side log, a blank one does not`` () =
    test
        <@
            match sideLogFor "dotnet" "build" [] (Some "-bl") "/d" with
            | SideLog.NotAttached reason -> reason.Contains "already set"
            | SideLog.Attached _ -> false
        @>

    test <@ sideLogFor "dotnet" "build" [] (Some "  ") "/d" = SideLog.Attached "/d" @>

[<Fact>]
let ``the side log lives under the fshw state directory`` () =
    test <@ sideLogDir "/r" = Path.Combine("/r", ".fshw", "build-binlog") @>

[<Fact>]
let ``the side log env asks MSBuild for a uniquely named binlog without imports`` () =
    test
        <@
            sideLogEnv (SideLog.Attached "/d") = [ SideLogVariable,
                                                   "-bl:" + Path.Combine("/d", "{}.binlog") + ";ProjectImports=None" ]
        @>

    test <@ List.isEmpty (sideLogEnv (SideLog.NotAttached "no")) @>

// ---------------------------------------------------------------------------
// prepareSideLog — each build reads only its own logs

[<Fact>]
let ``preparing the side log empties a directory an earlier build left logs in`` () =
    let root = tempDir ()
    let dir = Path.Combine(root, "build-binlog")
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "old.binlog"), "x")

    try
        test <@ prepareSideLog (SideLog.Attached dir) = SideLog.Attached dir @>
        test <@ Directory.Exists dir @>
        test <@ Array.isEmpty (Directory.GetFiles dir) @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``a side-log directory that cannot be created detaches the log instead of failing the build`` () =
    let root = tempDir ()
    let blocker = Path.Combine(root, "file")
    File.WriteAllText(blocker, "not a directory")

    try
        test
            <@
                match prepareSideLog (SideLog.Attached(Path.Combine(blocker, "build-binlog"))) with
                | SideLog.NotAttached reason -> reason.Contains "could not be prepared"
                | SideLog.Attached _ -> false
            @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``preparing a detached side log leaves it detached`` () =
    test <@ prepareSideLog (SideLog.NotAttached "no") = SideLog.NotAttached "no" @>

// ---------------------------------------------------------------------------
// inFlightTargets — what a replayed binlog says was running at the interrupt

/// A replay of a real cancelled build: `Build` depends on `Before` (finished) and
/// `StallHere` (running an `Exec` when SIGINT arrived at 04:55:33.180).
let private cancelledBuild =
    """04:55:25.261     0>BinLogFilePath=/r/stall.binlog
                   CurrentUICulture=en-US
04:55:25.298     1>Project "/r/stall.proj" on node 1 (Restore target(s)).
04:55:25.299     1>Target "Restore" skipped. The target does not exist in the project and SkipNonexistentTargets is set to true.
04:55:25.300     1>Done Building Project "/r/stall.proj" (Restore target(s)).
04:55:25.303   1:2>Project "/r/stall.proj" on node 1 (default targets).
04:55:25.304   1:2>Target "Before" in project "/r/stall.proj" (target "Build" depends on it):
                   Task "Message"
                   Done executing task "Message".
04:55:25.313   1:2>Done building target "Before" in project "stall.proj".
04:55:25.313   1:2>Target "StallHere" in project "/r/stall.proj" (target "Build" depends on it):
                   Task "Exec"
04:55:33.189   1:2>/r/stall.proj(4,28): warning MSB5021: Terminating the task executable "bash" and its child processes because the build was canceled.
04:55:33.214   1:2>Done building target "StallHere" in project "stall.proj" -- FAILED.
04:55:33.214   1:2>Done Building Project "/r/stall.proj" (default targets) -- FAILED.
"""

let private at (text: string) = TimeSpan.Parse text

[<Fact>]
let ``the target cancelled by the interrupt is in flight and the one that finished before is not`` () =
    let found = inFlightTargets (at "04:55:33.180") cancelledBuild
    test <@ found |> List.map (fun t -> t.Target, t.Project) = [ "StallHere", "/r/stall.proj" ] @>
    test <@ found |> List.map _.Running = [ at "04:55:33.180" - at "04:55:25.313" ] @>

[<Fact>]
let ``a target in an imported file is named with its project, and one never finished is in flight`` () =
    let log =
        """10:00:00.000   2:3>Target "CoreCompile" in file "/sdk/Microsoft.FSharp.Targets" from project "/r/src/Api/Api.fsproj" (target "Compile" depends on it):
"""

    test
        <@
            inFlightTargets (at "10:05:00.000") log = [ { Target = "CoreCompile"
                                                          Project = "/r/src/Api/Api.fsproj"
                                                          Running = TimeSpan.FromMinutes 5.0 } ]
        @>

[<Fact>]
let ``a target started after the interrupt is not in flight`` () =
    let log = "10:00:01.000   1:2>Target \"Late\" in project \"/r/a.proj\":\n"
    test <@ List.isEmpty (inFlightTargets (at "10:00:00.000") log) @>

[<Fact>]
let ``times are compared the short way round midnight`` () =
    let log =
        """23:59:50.000   1:2>Target "Long" in project "/r/a.proj":
00:00:05.000   1:2>Done building target "Long" in project "a.proj" -- FAILED.
23:59:40.000   1:3>Target "Early" in project "/r/b.proj":
23:59:45.000   1:3>Done building target "Early" in project "b.proj".
"""

    let found = inFlightTargets (at "00:00:01.000") log
    test <@ found |> List.map (fun t -> t.Target, t.Running) = [ "Long", TimeSpan.FromSeconds 11.0 ] @>

[<Fact>]
let ``a done line with no matching start is ignored, and the same target in two contexts is paired per context`` () =
    let log =
        """10:00:00.000   1:2>Done building target "Orphan" in project "a.proj".
10:00:01.000   1:2>Target "Build" in project "/r/a.proj":
10:00:02.000   1:3>Target "Build" in project "/r/b.proj":
10:00:03.000   1:3>Done building target "Build" in project "b.proj".
"""

    let found = inFlightTargets (at "10:00:04.000") log
    test <@ found |> List.map _.Project = [ "/r/a.proj" ] @>

// ---------------------------------------------------------------------------
// readSideLog — from the directory and the interrupt to what the report says

let private interruptAt (localTime: string) : TreeInterrupt =
    { At = DateTime.Today.Add(at localTime).ToUniversalTime()
      Signalled = [ 1 ]
      Refused = []
      Waited = TimeSpan.FromSeconds 0.2
      Exited = true }

[<Fact>]
let ``a detached side log reads as unavailable with its reason`` () =
    test
        <@
            readSideLog
                (fun _ -> failwith "not listed")
                (fun _ -> failwith "not replayed")
                (SideLog.NotAttached "why")
                None = SideLogReading.Unavailable "not attached: why"
        @>

[<Fact>]
let ``a build that was never interrupted has no side log to read`` () =
    test
        <@
            match
                readSideLog (fun _ -> failwith "not listed") (fun _ -> failwith "no") (SideLog.Attached "/d") None
            with
            | SideLogReading.Unavailable reason -> reason.Contains "not interrupted"
            | SideLogReading.Read _ -> false
        @>

[<Fact>]
let ``an interrupted build that wrote no binlog says why there is none`` () =
    test
        <@
            match
                readSideLog
                    (fun _ -> [])
                    (fun _ -> failwith "no")
                    (SideLog.Attached "/d")
                    (Some(interruptAt "04:55:33.180"))
            with
            | SideLogReading.Unavailable reason ->
                reason.Contains "no binary log in /d" && reason.Contains SideLogVariable
            | SideLogReading.Read _ -> false
        @>

[<Fact>]
let ``each binlog is replayed and read at the interrupt's local time`` () =
    let replay binlog =
        if binlog = "/d/a.binlog" then
            Ok cancelledBuild
        else
            Error "replay failed"

    let reading =
        readSideLog
            (fun _ -> [ "/d/a.binlog"; "/d/b.binlog" ])
            replay
            (SideLog.Attached "/d")
            (Some(interruptAt "04:55:33.180"))

    let expected =
        SideLogReading.Read
            [ "/d/a.binlog",
              Ok
                  [ { Target = "StallHere"
                      Project = "/r/stall.proj"
                      Running = at "04:55:33.180" - at "04:55:25.313" } ]
              "/d/b.binlog", Error "replay failed" ]

    test <@ reading = expected @>

[<Fact>]
let ``listBinlogs lists a directory's binlogs in name order and a missing directory as none`` () =
    let dir = tempDir ()

    try
        File.WriteAllText(Path.Combine(dir, "b.binlog"), "")
        File.WriteAllText(Path.Combine(dir, "a.binlog"), "")
        File.WriteAllText(Path.Combine(dir, "a.log"), "")

        test <@ listBinlogs dir |> List.map Path.GetFileName = [ "a.binlog"; "b.binlog" ] @>
        test <@ List.isEmpty (listBinlogs (Path.Combine(dir, "missing"))) @>
    finally
        Directory.Delete(dir, true)

// ---------------------------------------------------------------------------
// replayBinlogWith — `dotnet msbuild <binlog>` into a file logger, here a stand-in dotnet

let private fakeDotnet (dir: string) (script: string) =
    let exe = Path.Combine(dir, "dotnet")
    File.WriteAllText(exe, "#!/bin/sh\n" + script + "\n")
    File.SetUnixFileMode(exe, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    exe

[<Fact(Timeout = 20000)>]
let ``replay reads back the log the file logger wrote`` () =
    let dir = tempDir ()

    try
        // $5 is `-flp:logfile=<path>;verbosity=...`: write the replay where it says.
        let dotnet =
            fakeDotnet
                dir
                "f=\"${5#-flp:logfile=}\"; f=\"${f%%;*}\"; printf '%s|%s|%s' \"$1\" \"$3\" \"$DOTNET_CLI_UI_LANGUAGE\" > \"$f\""

        let binlog = Path.Combine(dir, "x.binlog")
        test <@ replayBinlogWith dotnet dir binlog = Ok "msbuild|-noconlog|en" @>
    finally
        Directory.Delete(dir, true)

[<Fact(Timeout = 20000)>]
let ``a replay that wrote no log is an error naming the command and what it said`` () =
    let dir = tempDir ()

    try
        let dotnet = fakeDotnet dir "echo 'MSB1025: unreadable binlog'; exit 1"

        test
            <@
                match replayBinlogWith dotnet dir (Path.Combine(dir, "x.binlog")) with
                | Error reason -> reason.Contains "msbuild" && reason.Contains "MSB1025"
                | Ok _ -> false
            @>
    finally
        Directory.Delete(dir, true)

// ---------------------------------------------------------------------------
// interruptGraceFor / leadWithOverruns — how an overrun is torn down and reported

[<Fact>]
let ``only a build with the side log attached is interrupted before the kill`` () =
    test <@ interruptGraceFor (SideLog.Attached "/d") = Some InterruptGrace @>
    test <@ interruptGraceFor (SideLog.NotAttached "no dotnet") = None @>

[<Fact>]
let ``an overrun report leads the entries ahead of the cancellation errors it caused`` () =
    let cancelled =
        FsHotWatch.ErrorLedger.ErrorEntry.error "MSB3073: The command \"sleep 120\" exited with code 130."

    let entries = leadWithOverruns [ "Build overran its 20s budget" ] [ cancelled ]
    test <@ entries |> List.map _.Message = [ "Build overran its 20s budget"; cancelled.Message ] @>

[<Fact>]
let ``an entry that is already the overrun report is not repeated`` () =
    let report = "Build overran its 20s budget"

    let entries =
        leadWithOverruns [ report ] [ FsHotWatch.ErrorLedger.ErrorEntry.error report ]

    test <@ entries |> List.map _.Message = [ report ] @>

// ---------------------------------------------------------------------------
// more of inFlightTargets, and the node-reuse value the report names

[<Fact>]
let ``nested in-flight targets come back longest-running first`` () =
    let log =
        """10:00:00.000   1:2>Target "Build" in project "/r/a.proj":
10:00:03.000   1:2>Target "CoreCompile" in project "/r/a.proj":
10:00:10.000   1:2>Done building target "CoreCompile" in project "a.proj" -- FAILED.
10:00:10.000   1:2>Done building target "Build" in project "a.proj" -- FAILED.
"""

    test <@ inFlightTargets (at "10:00:05.000") log |> List.map _.Target = [ "Build"; "CoreCompile" ] @>

[<Fact>]
let ``a target started and finished after the interrupt is not in flight`` () =
    let log =
        """10:00:01.000   1:2>Target "Late" in project "/r/a.proj":
10:00:02.000   1:2>Done building target "Late" in project "a.proj".
"""

    test <@ List.isEmpty (inFlightTargets (at "10:00:00.000") log) @>

[<Fact>]
let ``a target started just after midnight is after an interrupt just before it`` () =
    let log = "00:00:05.000   1:2>Target \"Tomorrow\" in project \"/r/a.proj\":\n"
    test <@ List.isEmpty (inFlightTargets (at "23:59:59.000") log) @>

[<Fact>]
let ``a log with no targets has nothing in flight`` () =
    test <@ List.isEmpty (inFlightTargets (at "10:00:00.000") "Build started.\n") @>

[<Fact>]
let ``the node-reuse value is the configured one, fshw's for dotnet, or none`` () =
    test <@ nodeReuseFor [] "sh" "-c \"dotnet build\"" = Some "1" @>
    test <@ nodeReuseFor [ "MSBUILDDISABLENODEREUSE", "0" ] "dotnet" "build" = Some "0" @>
    test <@ nodeReuseFor [] "sh" "-c \"make\"" = None @>

[<Fact(Timeout = 20000)>]
let ``a replay failure clips a long output`` () =
    let dir = tempDir ()

    try
        let dotnet = fakeDotnet dir "printf 'x%.0s' $(seq 1 400); exit 1"

        test
            <@
                match replayBinlogWith dotnet dir (Path.Combine(dir, "x.binlog")) with
                | Error reason -> reason.EndsWith "...)"
                | Ok _ -> false
            @>
    finally
        Directory.Delete(dir, true)
