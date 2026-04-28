module FsHotWatch.Tests.RunOnceOutputTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput


// --- Staleness warning: detect FileCommand plugin inputs newer than last run ---

[<Fact(Timeout = 5000)>]
let ``detectStalePluginInputs flags plugins whose args are newer than last run`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        let lastRun = DateTime.UtcNow.AddMinutes(-5.0)
        // ensure mtime is after lastRun
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow)

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins

        test
            <@
                result
                |> List.exists (fun (n, files) -> n = "ratchet" && List.contains cfg files)
            @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 5000)>]
let ``detectStalePluginInputs omits plugins with no stale files`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore
    let cfg = System.IO.Path.Combine(tmpDir, "cfg.json")

    try
        System.IO.File.WriteAllText(cfg, "{}")
        System.IO.File.SetLastWriteTimeUtc(cfg, DateTime.UtcNow.AddMinutes(-10.0))
        let lastRun = DateTime.UtcNow

        let plugins =
            [ { Name = "ratchet"
                LastRunStarted = lastRun
                RepoRoot = tmpDir
                Args = "--check cfg.json" } ]

        let result = detectStalePluginInputs plugins
        test <@ List.isEmpty result @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 5000)>]
let ``formatStalenessWarning is empty for no stale plugins`` () =
    test <@ formatStalenessWarning [] = "" @>

[<Fact(Timeout = 5000)>]
let ``formatStalenessWarning names the plugin, file, and rerun hint`` () =
    let warning = formatStalenessWarning [ "ratchet", [ "/tmp/cfg.json" ] ]
    test <@ warning.Contains("ratchet") @>
    test <@ warning.Contains("/tmp/cfg.json") @>
    test <@ warning.Contains("rerun") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors groups by file with plugin prefix`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None })
                ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("src/Foo.fs") @>
    test <@ result.Contains("[lint]") @>
    test <@ result.Contains("[build]") @>
    test <@ result.Contains("L17") @>
    test <@ result.Contains("L42") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors shows severity labels for error and warning`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("build",
                 { Message = "type error"
                   Severity = Error
                   Line = 42
                   Column = 5
                   Detail = None })
                ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 17
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("error") @>
    test <@ result.Contains("type error") @>
    test <@ result.Contains("warning") @>
    test <@ result.Contains("bad name") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors shows count summary`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("lint",
                 { Message = "x"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("build",
                 { Message = "y"
                   Severity = Error
                   Line = 2
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors with no errors shows clean message`` () =
    let result = formatErrors Map.empty
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors hides info-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors hides hint-severity entries from output`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "some hint"
                   Severity = Hint
                   Line = 5
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ not (result.Contains("some hint")) @>
    test <@ result.Contains("No errors") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors shows warnings but hides info in same file`` () =
    let errors =
        Map.ofList
            [ "src/Foo.fs",
              [ ("fcs",
                 { Message = "XML comment is not placed on a valid language element."
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None })
                ("format-check",
                 { Message = "File is not formatted"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("File is not formatted") @>
    test <@ not (result.Contains("XML comment")) @>
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 5000)>]
let ``formatErrors excludes files with only info entries from file count`` () =
    let errors =
        Map.ofList
            [ "src/A.fs",
              [ ("fcs",
                 { Message = "XML comment"
                   Severity = Info
                   Line = 3
                   Column = 0
                   Detail = None }) ]
              "src/B.fs",
              [ ("lint",
                 { Message = "bad name"
                   Severity = Warning
                   Line = 1
                   Column = 0
                   Detail = None }) ] ]

    let result = formatErrors errors
    test <@ result.Contains("1 warning(s) in 1 file(s)") @>
