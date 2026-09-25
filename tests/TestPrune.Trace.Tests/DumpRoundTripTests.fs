/// In the counters collection: one test compares a dump's counters with the live ones.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.DumpRoundTripTests

open System
open System.IO
open System.Runtime.InteropServices
open Xunit
open Swensen.Unquote
open TestPrune.Trace.Recorder
open TestPrune.Trace
open TestPrune.Trace.Model

let private header =
    """{"format":"testprune-trace/1","pid":2,"parentScope":null,"runtime":"r","os":"o","arch":"a","ids":1,"cpuMs":0,"counters":{"test":0,"class":0,"collection":0,"assembly":0,"override":0,"staticInit":0,"ambient":0,"overflow":0}}"""

let private endLine = """{"end":true}"""

let private writeLines (lines: string[]) =
    let dir = Directory.CreateTempSubdirectory().FullName
    let path = Path.Combine(dir, "trace-9.ndjson")
    File.WriteAllLines(path, lines)
    path

[<Fact>]
let ``a dump reads back exactly what the recorder held`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let state = RecorderState(128, None, null)
    state.EnterScope "T:x"
    state.Hit 3
    state.Hit 70
    state.CurrentScope().Links.TryAdd("P:pool", 0uy) |> ignore
    state.ExitScope()
    let path = Path.Combine(dir, "trace-1.ndjson")
    DumpWriter.write path state
    let dump = DumpReader.readFile path |> Result.defaultWith failwith
    let x = dump.Scopes |> List.find (fun s -> s.Key = "T:x")
    test <@ x.Ids = [| 3; 70 |] @>
    test <@ x.Links = [ "P:pool" ] @>
    test <@ dump.IdCount = 128 @>
    test <@ not (File.Exists(path + ".tmp")) @>

[<Fact>]
let ``every scope field and header field survives the round trip`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let state = RecorderState(8, None, "T:parent")
    state.EnterScope "T:t"
    let s = state.CurrentScope()
    s.TestClass <- "Ns.C"
    s.TestMethod <- "m"
    s.TestDisplay <- "Ns.C.m"
    s.Parents <- [| "C:Ns.C"; "A:assembly" |]
    s.Links.TryAdd("P:b", 0uy) |> ignore
    s.Links.TryAdd("P:a", 0uy) |> ignore
    s.Inputs.TryAdd(struct ("read", "/r/x.json"), 0uy) |> ignore
    s.Inputs.TryAdd(struct ("exists", "/r/y"), 0uy) |> ignore
    s.Inputs.TryAdd(struct ("list", "/r/d"), 0uy) |> ignore
    s.Children.Enqueue(struct (456, "dotnet", true))
    s.Children.Enqueue(struct (457, "git", false))
    state.Hit 1
    state.ExitScope()
    state.EnterScope "T:empty" // a test that executed nothing is still written
    state.ExitScope()
    state.EnterScope "P:unused" // any other empty scope is not
    state.ExitScope()
    state.Hit 2 // lands on the parent scope
    let path = DumpWriter.pathIn dir
    test <@ path = Path.Combine(dir, $"trace-%d{Environment.ProcessId}.ndjson") @>
    DumpWriter.write path state
    let dump = DumpReader.readFile path |> Result.defaultWith failwith

    test <@ dump.Pid = Environment.ProcessId @>
    test <@ dump.ParentScope = Some "T:parent" @>
    test <@ dump.Runtime = RuntimeInformation.FrameworkDescription @>
    test <@ dump.Os = DumpWriter.osNameOf RuntimeInformation.IsOSPlatform @>
    test <@ dump.Arch = string RuntimeInformation.ProcessArchitecture @>
    test <@ dump.CpuMs > 0L @>
    test <@ dump.Counters.Override >= 2L @>
    test <@ dump.Scopes |> List.map (fun s -> s.Key) |> set = set [ "T:t"; "T:empty"; "T:parent" ] @>

    let t = dump.Scopes |> List.find (fun s -> s.Key = "T:t")

    test
        <@
            t = { Key = "T:t"
                  Test =
                    Some
                        { Class = "Ns.C"
                          Method = "m"
                          Display = "Ns.C.m" }
                  Parents = [ "C:Ns.C"; "A:assembly" ]
                  Links = [ "P:a"; "P:b" ]
                  Ids = [| 1 |]
                  Inputs =
                    [ { Kind = ExistenceProbe; Path = "/r/y" }
                      { Kind = DirectoryListing
                        Path = "/r/d" }
                      { Kind = FileRead; Path = "/r/x.json" } ]
                  Children =
                    [ { Pid = 456
                        FileName = "dotnet"
                        EnvInjected = true }
                      { Pid = 457
                        FileName = "git"
                        EnvInjected = false } ] }
        @>

    let empty = dump.Scopes |> List.find (fun s -> s.Key = "T:empty")
    test <@ empty.Test = None && Array.isEmpty empty.Ids && List.isEmpty empty.Parents @>

[<Fact>]
let ``counters are written under their bucket names`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    let state = RecorderState(4, None, null)
    let before = state.Counters()
    state.Hit 0
    state.Hit 9
    let path = Path.Combine(dir, "trace-5.ndjson")
    DumpWriter.write path state
    let dump = DumpReader.readFile path |> Result.defaultWith failwith
    let c = state.Counters()
    let after = dump.Counters

    test
        <@
            [ after.Test
              after.Class
              after.Collection
              after.Assembly
              after.Override
              after.StaticInit
              after.Ambient
              after.Overflow ] = List.ofArray c
        @>

    test <@ c.[6] > before.[6] && c.[7] > before.[7] @>
    test <@ dump.ParentScope = None @>

[<Fact>]
let ``the os name covers every platform`` () =
    let only (p: OSPlatform) = fun (q: OSPlatform) -> q = p
    test <@ DumpWriter.osNameOf (only OSPlatform.OSX) = "OSX" @>
    test <@ DumpWriter.osNameOf (only OSPlatform.Linux) = "Linux" @>
    test <@ DumpWriter.osNameOf (only OSPlatform.Windows) = "Windows" @>
    test <@ DumpWriter.osNameOf (only OSPlatform.FreeBSD) = "Other" @>

[<Fact>]
let ``a dump without its end marker is rejected`` () =
    let path = writeLines [| header |]
    test <@ DumpReader.readFile path = Error "truncated" @>

[<Fact>]
let ``an empty dump, a foreign format and unparseable lines are rejected`` () =
    test <@ DumpReader.readFile (writeLines [| ""; "  " |]) = Error "empty" @>

    let foreign = header.Replace("testprune-trace/1", "testprune-trace/2")
    test <@ DumpReader.readFile (writeLines [| foreign; endLine |]) = Error "unknown format" @>

    test <@ DumpReader.readFile (writeLines [| "not json" |]) |> Result.isError @>
    test <@ DumpReader.readFile (writeLines [| endLine |]) = Error "no header" @>

    let badKind =
        """{"key":"T:x","test":null,"parents":[],"links":[],"ids":[],"inputs":[{"kind":"write","path":"/x"}],"children":[]}"""

    test <@ DumpReader.readFile (writeLines [| header; badKind; endLine |]) = Error "unknown input kind write" @>

[<Fact>]
let ``a header and end marker alone is a dump with no scopes`` () =
    let dump =
        DumpReader.readFile (writeLines [| header; endLine |])
        |> Result.defaultWith failwith

    test <@ List.isEmpty dump.Scopes && dump.Pid = 2 && dump.ParentScope = None @>

[<Fact>]
let ``readDirectory ignores tmp files and reports rejected dumps by name`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    File.WriteAllText(Path.Combine(dir, "trace-3.ndjson.tmp"), "partial")
    File.WriteAllText(Path.Combine(dir, "trace-4.ndjson"), "not json")
    let good, bad = DumpReader.readDirectory dir
    test <@ List.isEmpty good @>
    test <@ bad |> List.map fst = [ Path.Combine(dir, "trace-4.ndjson") ] @>

[<Fact>]
let ``readDirectory returns good dumps and treats a missing directory as empty`` () =
    let dir = Directory.CreateTempSubdirectory().FullName
    File.WriteAllLines(Path.Combine(dir, "trace-7.ndjson"), [| header; endLine |])
    let good, bad = DumpReader.readDirectory dir
    test <@ good |> List.map (fun d -> d.Pid) = [ 2 ] && List.isEmpty bad @>
    test <@ DumpReader.readDirectory (Path.Combine(dir, "missing")) = ([], []) @>
