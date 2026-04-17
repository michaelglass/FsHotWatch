module FsHotWatch.Tests.CoveragePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Coverage.CoveragePlugin
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 30000)>]
let ``coverage command returns empty initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create "/tmp/nonexistent" None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("coverage", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "{}" @>

[<Fact(Timeout = 30000)>]
let ``coverage plugin reads Cobertura XML`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-test-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

    try
        let mutable checkDone = false

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler =
            create
                tmpDir
                None
                (Some(fun () ->
                    checkDone <- true
                    true, ""))
                None

        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

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

[<Fact(Timeout = 30000)>]
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

        let handler = create tmpDir (Some thresholdsPath) None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "MyProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Failed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Failed _ -> true
                | _ -> false
            @>

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin skips check when no test results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create "/tmp/nonexistent" None None None
    host.RegisterHandler(handler)

    let testResults =
        { Results = Map.empty
          Elapsed = TimeSpan.FromSeconds(0.0) }

    host.EmitTestCompleted(testResults)

    // Empty results — handler returns early. Poll briefly; will time out at Idle (expected)
    waitUntil
        (fun () ->
            match host.GetStatus("coverage") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000

    // Status should remain Idle since the handler returns early
    let status = host.GetStatus("coverage")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 30000)>]
let ``coverage plugin reports Failed when coverage dir does not exist`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create "/tmp/nonexistent-cov-dir-xyz" None None None
    host.RegisterHandler(handler)

    let testResults =
        { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
          Elapsed = TimeSpan.FromSeconds(1.0) }

    host.EmitTestCompleted(testResults)

    waitUntil
        (fun () ->
            match host.GetStatus("coverage") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("coverage")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed(msg, _) -> msg.Contains("No coverage files found")
            | _ -> false
        @>

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles threshold file with missing branch property`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-nobranch-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "Proj")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.50" branch-rate="0.50" />""")

    let thresholdsPath = Path.Combine(tmpDir, "thresholds.json")
    // Only line threshold, no branch key
    File.WriteAllText(thresholdsPath, """{"Proj": {"line": 40.0}}""")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir (Some thresholdsPath) None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "Proj", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles threshold file with missing line property`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-noline-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "Proj")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.50" branch-rate="0.50" />""")

    let thresholdsPath = Path.Combine(tmpDir, "thresholds.json")
    // Only branch threshold, no line key
    File.WriteAllText(thresholdsPath, """{"Proj": {"branch": 40.0}}""")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir (Some thresholdsPath) None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "Proj", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles invalid thresholds JSON`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-badjson-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

    let thresholdsPath = Path.Combine(tmpDir, "thresholds.json")
    File.WriteAllText(thresholdsPath, "this is not valid json {{{")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir (Some thresholdsPath) None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // Invalid JSON falls back to Map.empty, so no thresholds => all pass
        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles non-existent thresholds file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-nothresh-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")

    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir (Some "/tmp/nonexistent-thresholds-xyz.json") None None

        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles XML with missing attributes`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-nullattr-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")
    // XML with no line-rate or branch-rate attributes
    File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage />""")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir None None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

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
        test <@ result.Value.Contains("0.0") @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``coverage plugin reports error when afterCheck fails`` () =
    withTempDir "cov-aftercheck-fail" (fun tmpDir ->
        let subDir = Path.Combine(tmpDir, "TestProject")
        Directory.CreateDirectory(subDir) |> ignore
        let xmlPath = Path.Combine(subDir, "cobertura.xml")
        File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler =
            create tmpDir None (Some(fun () -> false, "ratchet failed: coverage regressed")) None

        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Failed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")

        test
            <@
                match status.Value with
                | Failed(msg, _) -> msg.Contains("afterCheck failed")
                | _ -> false
            @>

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>)

[<Fact(Timeout = 30000)>]
let ``coverage plugin clears afterCheck error on success`` () =
    withTempDir "cov-aftercheck-pass" (fun tmpDir ->
        let subDir = Path.Combine(tmpDir, "TestProject")
        Directory.CreateDirectory(subDir) |> ignore
        let xmlPath = Path.Combine(subDir, "cobertura.xml")
        File.WriteAllText(xmlPath, """<?xml version="1.0" ?><coverage line-rate="0.85" branch-rate="0.70" />""")

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
        let handler = create tmpDir None (Some(fun () -> true, "all good")) None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("coverage")

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>

        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>)

[<Fact(Timeout = 30000)>]
let ``coverage plugin handles invalid XML file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cov-badxml-{Guid.NewGuid():N}")
    let subDir = Path.Combine(tmpDir, "TestProject")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "cobertura.xml")
    File.WriteAllText(xmlPath, "this is not valid xml <<<>>>")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = create tmpDir None None None
        host.RegisterHandler(handler)

        let testResults =
            { Results = Map.ofList [ "TestProject", TestsPassed "ok" ]
              Elapsed = TimeSpan.FromSeconds(1.0) }

        host.EmitTestCompleted(testResults)

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // Invalid XML is skipped by parseCoberturaXml (returns None)
        // but there are coverage files found, so it goes to allPass check with empty results
        let status = host.GetStatus("coverage")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()
