/// The listing and open overloads beyond the original shim set are recorded: a woven run
/// of each family notes its input on the calling test's scope.
module TestPrune.Trace.Tests.InputOverloadTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// Every scope of one woven FxDriver run, with what it recorded.
let private wovenScopes =
    lazy
        (let dir, r = WeaverTests.weaveFx [ Redirects.pass () ]

         try
             let out = Path.Combine(dir, "traces")

             let code, output =
                 Launch.run
                     (Path.Combine(dir, "FxDriver"))
                     []
                     [ Contract.OutEnv, out
                       Contract.IdsEnv, string r.Manifest.IdCount
                       Contract.RepoRootEnv, Fixtures.repoRoot ]
                     dir
                     (TimeSpan.FromMinutes 1.0)

             if code <> 0 then
                 failwith $"FxDriver exited %d{code}: %s{output}"

             let dumps, _ = DumpReader.readDirectory out
             dumps |> List.collect (fun d -> d.Scopes)
         finally
             WeaverTests.deleteScratch dir)

let private recorded (scopeKey: string) =
    wovenScopes.Value
    |> List.filter (fun s -> s.Key = scopeKey)
    |> List.collect (fun s -> s.Inputs)
    |> List.map (fun i -> i.Kind, i.Path)

[<Fact(Timeout = 180000)>]
let ``a DirectoryInfo listing of directories is recorded as a listing`` () =
    test
        <@
            recorded "T:dirInfoListing"
            |> List.contains (DirectoryListing, Fixtures.repoRoot)
        @>

[<Fact(Timeout = 180000)>]
let ``a listing that takes EnumerationOptions is recorded as a listing`` () =
    test
        <@
            recorded "T:optionsListing"
            |> List.contains (DirectoryListing, Fixtures.repoRoot)
        @>

[<Fact(Timeout = 180000)>]
let ``a reader opened by path through another overload is recorded as a read`` () =
    test
        <@
            recorded "T:openOverload"
            |> List.contains (FileRead, Path.Combine(Fixtures.repoRoot, "global.json"))
        @>

[<Theory(Timeout = 180000)>]
[<InlineData("T:metaFile", "global.json")>]
[<InlineData("T:metaLength", "global.json")>]
[<InlineData("T:metaInfo", "global.json")>]
[<InlineData("T:metaDirectory", "")>]
let ``a read of a path's metadata is recorded as a metadata read`` (scopeKey: string, rel: string) =
    let path =
        if rel = "" then
            Fixtures.repoRoot
        else
            Path.Combine(Fixtures.repoRoot, rel)

    test <@ recorded scopeKey |> List.contains (MetadataRead, path) @>

[<Fact(Timeout = 180000)>]
let ``an input noted through Scopes.NoteInput reaches the dump under its test only`` () =
    let noted = FileRead, Path.Combine(Fixtures.repoRoot, "TestPrune.slnx")
    test <@ recorded "T:noted" |> List.contains noted @>

    let elsewhere =
        wovenScopes.Value
        |> List.filter (fun s -> s.Key <> "T:noted")
        |> List.exists (fun s -> s.Inputs |> List.exists (fun i -> (i.Kind, i.Path) = noted))

    test <@ not elsewhere @>
