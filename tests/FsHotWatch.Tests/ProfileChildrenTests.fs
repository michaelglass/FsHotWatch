/// The child processes of a test host's tree, by command: how many were seen and the CPU
/// time they used (AUTOMATION-1300, slice 5). Read from the same process-table samples the
/// tree-CPU reading already takes, so a process that lived and died between two table
/// reads is not seen: the counts are a lower bound.
module FsHotWatch.Tests.ProfileChildrenTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

let private row pid parent command : FsHotWatch.ProcessHelper.ProcessRow =
    { Pid = pid
      ParentPid = parent
      Zombie = false
      Command = command }

[<Theory>]
[<InlineData("/usr/bin/git status --porcelain", "git")>]
[<InlineData("git", "git")>]
[<InlineData("dotnet /r/bin/Debug/net10.0/testhost.dll --port 1", "testhost")>]
[<InlineData("/nix/store/x-dotnet/bin/dotnet run --project T", "dotnet")>]
[<InlineData("  sh -c \"sleep 7\"", "sh")>]
[<InlineData("", "?")>]
let ``commandName is the program a command line runs`` (commandLine: string, expected: string) =
    test <@ Profile.commandName commandLine = expected @>

[<Fact>]
let ``children are counted and timed by command, and an exited child keeps what it was seen to use`` () =
    let tables =
        Collections.Generic.Queue<Result<FsHotWatch.ProcessHelper.ProcessRow list, string>>(
            [ Ok
                  [ row 10 1 "dotnet run --project T"
                    row 11 10 "dotnet /b/testhost.dll"
                    row 12 11 "/usr/bin/git status"
                    row 99 1 "unrelated" ]
              // The first git exited; another started.
              Ok
                  [ row 10 1 "dotnet run --project T"
                    row 11 10 "dotnet /b/testhost.dll"
                    row 13 11 "/usr/bin/git log" ] ]
        )

    let cpu = Map [ 10, 100.0; 11, 5000.0; 12, 30.0; 13, 20.0; 99, 1e9 ]

    let tree =
        Profile.processTreeWith 1 (fun () -> tables.Dequeue()) (fun pid -> Map.tryFind pid cpu)

    test <@ tree.Cpu 10 = Some 5130.0 @>
    test <@ tree.Cpu 10 = Some 5150.0 @>

    test
        <@
            tree.Children() = [ { Command = "testhost"
                                  Processes = 1
                                  CpuMs = 5000.0 }
                                { Command = "dotnet"
                                  Processes = 1
                                  CpuMs = 100.0 }
                                { Command = "git"
                                  Processes = 2
                                  CpuMs = 50.0 } ]
        @>

let private tempRunDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-profile-children-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

[<Fact>]
let ``finish writes one children line per command, and they round-trip`` () =
    let dir = tempRunDir ()

    let r =
        Profile.start dir "P.Tests" None 4 (fun () -> None) (fun _ -> None) Threading.Timeout.InfiniteTimeSpan

    Profile.trackChildren r (fun () ->
        [ { Command = "git"
            Processes = 3
            CpuMs = 12.5 } ])

    Profile.finish r "passed" (Ok [])

    let children =
        Profile.read (Profile.pathIn dir "P.Tests")
        |> List.choose (function
            | Profile.Children c -> Some c
            | _ -> None)

    test
        <@
            children = [ { Command = "git"
                           Processes = 3
                           CpuMs = 12.5 } ]
        @>

    let line =
        Profile.Children
            { Command = "sh"
              Processes = 1
              CpuMs = 0.0 }

    test <@ Profile.tryParse (Profile.render line) = Some line @>
    test <@ Profile.tryParse """{"event":"children","command":"sh"}""" = None @>

[<Fact>]
let ``a recorder whose children reader throws still finishes`` () =
    let dir = tempRunDir ()

    let r =
        Profile.start dir "P.Tests" None 4 (fun () -> None) (fun _ -> None) Threading.Timeout.InfiniteTimeSpan

    Profile.trackChildren r (fun () -> raise (InvalidOperationException "ps went away"))
    Profile.finish r "passed" (Ok [])

    test
        <@
            Profile.read (Profile.pathIn dir "P.Tests")
            |> List.exists (function
                | Profile.End _ -> true
                | _ -> false)
        @>
