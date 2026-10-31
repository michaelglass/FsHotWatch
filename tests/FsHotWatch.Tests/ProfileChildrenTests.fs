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

// AUTOMATION-1421: under load the first `ps` read can fail. That is no reading — never a tree
// of the root alone, recorded as one `?` process — and it does not wait out the refresh
// cadence: the next sample reads the table again.
[<Fact>]
let ``a failed table read is no reading and is retried on the next sample`` () =
    let tables =
        Collections.Generic.Queue<Result<FsHotWatch.ProcessHelper.ProcessRow list, string>>(
            [ Error "`ps` did not answer"
              Ok [ row 10 1 "sh -c sleep"; row 11 10 "/bin/sleep 7" ] ]
        )

    let tree =
        Profile.processTreeWith 6 (fun () -> tables.Dequeue()) (fun pid -> Some(float pid))

    test <@ tree.Cpu 10 = Some 10.0 @>
    test <@ List.isEmpty (tree.Children()) @>

    test <@ tree.Cpu 10 = Some 21.0 @>

    test
        <@
            tree.Children() = [ { Command = "sleep"
                                  Processes = 1
                                  CpuMs = 11.0 }
                                { Command = "sh"
                                  Processes = 1
                                  CpuMs = 10.0 } ]
        @>

    test <@ tables.Count = 0 @>

[<Fact>]
let ``a failed table read after a reading is retried too, and the members already known are kept`` () =
    let reads = ref 0

    let tree =
        Profile.processTreeWith
            2
            (fun () ->
                reads.Value <- reads.Value + 1

                if reads.Value = 1 then
                    Ok [ row 10 1 "sh -c sleep"; row 11 10 "/bin/sleep 7" ]
                else
                    Error "`ps` did not answer")
            (fun pid -> Some(float pid))

    for _ in 1..5 do
        tree.Cpu 10 |> ignore

    // Read at sample 1 (a reading: the next is due at 3), then at 3, 4 and 5, each failing.
    test <@ reads.Value = 4 @>
    test <@ tree.Children() |> List.map _.Command = [ "sleep"; "sh" ] @>

// AUTOMATION-1421: each sample says whether the host's tree has been read, so a reader (and a
// test) can tell a reading from its absence instead of inferring it from the sampling cadence.
[<Fact>]
let ``a sample records the processes the tree's reading holds, and none before a reading`` () =
    let dir = tempRunDir ()

    let tables =
        Collections.Generic.Queue<Result<FsHotWatch.ProcessHelper.ProcessRow list, string>>(
            [ Error "`ps` did not answer"
              Ok [ row 10 1 "sh -c sleep"; row 11 10 "/bin/sleep 7" ] ]
        )

    let tree =
        Profile.processTreeWith 6 (fun () -> tables.Dequeue()) (fun pid -> Some(float pid))

    let r =
        Profile.start dir "P.Tests" None 4 (fun () -> None) tree.Cpu Threading.Timeout.InfiniteTimeSpan

    Profile.trackChildren r tree.Children
    Profile.spawned r 10
    Profile.sampleNow r
    Profile.sampleNow r
    Profile.finish r "passed" (Ok [])

    let processes =
        Profile.read (Profile.pathIn dir "P.Tests")
        |> List.choose (function
            | Profile.Sampled s -> Some s.Processes
            | _ -> None)

    test <@ processes = [ None; Some 2 ] @>

[<Fact>]
let ``a sample's processes round-trip, and a sample line written before them reads as no reading`` () =
    let at = DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)

    let line =
        Profile.Sampled
            { At = at
              Load1 = Some 2.0
              CpuMs = Some 5.0
              Processes = Some 3 }

    test <@ Profile.tryParse (Profile.render line) = Some line @>

    test
        <@
            Profile.tryParse """{"event":"sample","at":"2026-10-31T12:00:00.0000000Z","load1":2,"cpuMs":5}""" = Some(
                Profile.Sampled
                    { At = at
                      Load1 = Some 2.0
                      CpuMs = Some 5.0
                      Processes = None }
            )
        @>
