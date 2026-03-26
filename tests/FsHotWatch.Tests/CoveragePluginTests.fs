module FsHotWatch.Tests.CoveragePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Coverage.CoveragePlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = CoveragePlugin("/tmp/nonexistent") :> IFsHotWatchPlugin
    test <@ plugin.Name = "coverage" @>

[<Fact>]
let ``coverage command returns empty initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = CoveragePlugin("/tmp/nonexistent")
    host.Register(plugin)

    let result = host.RunCommand("coverage", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "{}" @>

[<Fact>]
let ``coverage plugin reads Cobertura XML`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-test-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

    try
        let mutable checkDone = false

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let plugin = CoveragePlugin(tmpDir, afterCheck = (fun () -> checkDone <- true))
        host.Register(plugin)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        test <@ checkDone @>

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>

        let result = host.RunCommand("coverage", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("85.0") @>
        test <@ result.Value.Contains("70.0") @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``coverage plugin fails when below threshold`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-thresh-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "MyProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

    let thresholdsPath = Path.Combine(tmpDir, "thresholds.json")

    File.WriteAllText(thresholdsPath, """{"MyProject": {"line": 90.0, "branch": 50.0}}""")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let plugin = CoveragePlugin(tmpDir, thresholdsFile = thresholdsPath)
        host.Register(plugin)

        let testResults =
            { Results = Map.ofList [ "MyProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Failed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()
