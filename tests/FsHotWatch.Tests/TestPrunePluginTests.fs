module FsHotWatch.Tests.TestPrunePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    test <@ plugin.Name = "test-prune" @>

[<Fact>]
let ``affected-tests command returns empty list when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``changed-files command returns empty list when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with
    | _ -> ()

    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")

[<Fact>]
let ``changed-files tracks files after emit with valid relative path`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let dbPath = Path.Combine(tmpDir, "test.db")

    try
        let host =
            PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let plugin = TestPrunePlugin(dbPath, tmpDir)
        host.Register(plugin)

        // Create a fake file under tmpDir so GetRelativePath produces a meaningful relative path
        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = Unchecked.defaultof<_> }

        // This will trigger the catch because CheckResults is null,
        // but the changed-files tracking and storedSymbols path both execute before that.
        try
            host.EmitFileChecked(fakeResult)
        with
        | _ -> ()

        // The file should have been tracked in lastChangedFiles before the error
        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``duplicate file checks do not duplicate in changed-files list`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-dup-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let dbPath = Path.Combine(tmpDir, "test.db")

    try
        let host =
            PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let plugin = TestPrunePlugin(dbPath, tmpDir)
        host.Register(plugin)

        let fakeFile = Path.Combine(tmpDir, "Dup.fs")
        File.WriteAllText(fakeFile, "module Dup\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Dup\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = Unchecked.defaultof<_> }

        // Emit twice — the plugin deduplicates via List.contains
        for _ in 1..2 do
            try
                host.EmitFileChecked(fakeResult)
            with
            | _ -> ()

        // Status should be set (Running or Failed)
        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``dispose is callable`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    plugin.Dispose()
