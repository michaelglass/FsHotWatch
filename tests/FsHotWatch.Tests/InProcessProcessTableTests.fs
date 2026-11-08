/// The kernel's process table, decoded without starting `ps` (AUTOMATION-1153).
module FsHotWatch.Tests.InProcessProcessTableTests

open System
open System.Text
open Xunit
open FsHotWatch

let private rowOf pid command : InProcessProcessTable.Row =
    { Pid = pid
      ParentPid = 0
      Zombie = false
      Command = command }

let private bsdInfo pid ppid (status: uint32) (comm: string) =
    let info = Array.zeroCreate<byte> 136
    BitConverter.GetBytes(status).CopyTo(info, 4)
    BitConverter.GetBytes(uint32 pid).CopyTo(info, 12)
    BitConverter.GetBytes(uint32 ppid).CopyTo(info, 16)
    Encoding.ASCII.GetBytes(comm).CopyTo(info, 48)
    info

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a running process decodes from proc_bsdinfo`` () =
    let row =
        InProcessProcessTable.decodeBsdInfo (bsdInfo 42 7 2u "dotnet") |> Option.get

    Assert.Equal(42, row.Pid)
    Assert.Equal(7, row.ParentPid)
    Assert.False(row.Zombie)
    Assert.Equal("dotnet", row.Command)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``SZOMB decodes as a zombie`` () =
    let row =
        InProcessProcessTable.decodeBsdInfo (bsdInfo 42 7 5u "defunct") |> Option.get

    Assert.True(row.Zombie)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a proc_bsdinfo of the wrong size decodes to nothing`` () =
    Assert.True((InProcessProcessTable.decodeBsdInfo (Array.zeroCreate 8)).IsNone)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a proc stat line decodes state and parent, from the last parenthesis`` () =
    let row =
        InProcessProcessTable.parseProcStat "1234 (weird) name) R 77 1234 1234 0 -1"
        |> Option.get

    Assert.Equal(1234, row.Pid)
    Assert.Equal(77, row.ParentPid)
    Assert.False(row.Zombie)
    Assert.Equal("weird) name", row.Command)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a zombie proc stat line decodes as a zombie`` () =
    let row = InProcessProcessTable.parseProcStat "9 (x) Z 1 9" |> Option.get
    Assert.True(row.Zombie)

[<Theory(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
[<InlineData("")>]
[<InlineData("no parens here")>]
[<InlineData("abc (x) R 1")>]
[<InlineData("9 (x) R")>]
[<InlineData("9 (x) R notanumber")>]
let ``a proc stat line that does not have the fields decodes to nothing`` (text: string) =
    Assert.True((InProcessProcessTable.parseProcStat text).IsNone)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``listing pids grows the buffer when the answer may not have fit`` () =
    let calls = ref 0

    let list (buffer: int[]) =
        calls.Value <- calls.Value + 1

        if buffer.Length < 8 then
            buffer.Length
        else
            buffer[0] <- 5
            buffer[1] <- 6
            2

    let pids = InProcessProcessTable.listPidsWith list (fun () -> 0) 4
    Assert.Equal<int[]>([| 5; 6 |], Result.defaultValue [||] pids)
    Assert.Equal(2, calls.Value)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a failed pid listing names the errno`` () =
    match InProcessProcessTable.listPidsWith (fun _ -> -1) (fun () -> 13) 4 with
    | Error reason -> Assert.Contains("13", reason)
    | Ok _ -> Assert.Fail "expected an error"

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the read dispatches on the platform and refuses one with no table`` () =
    let mac () = Ok [ rowOf 1 "m" ]
    let linux () = Ok [ rowOf 2 "l" ]
    Assert.Equal(1, (InProcessProcessTable.readOn true false mac linux |> Result.defaultValue []).Head.Pid)
    Assert.Equal(2, (InProcessProcessTable.readOn false true mac linux |> Result.defaultValue []).Head.Pid)
    Assert.True((InProcessProcessTable.readOn false false mac linux) |> Result.isError)

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``this host's table holds this process`` () =
    match InProcessProcessTable.read () with
    | Ok rows -> Assert.Contains(rows, fun r -> r.Pid = Environment.ProcessId)
    | Error reason -> Assert.Fail reason
