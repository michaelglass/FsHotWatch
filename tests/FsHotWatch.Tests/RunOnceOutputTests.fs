module FsHotWatch.Tests.RunOnceOutputTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput

[<Fact>]
let ``formatStepResult shows checkmark for completed`` () =
    let result = formatStepResult "build" (Completed DateTime.UtcNow)
    test <@ result.Contains("\u2713") @>
    test <@ result.Contains("build") @>

[<Fact>]
let ``formatStepResult shows X for failed`` () =
    let result = formatStepResult "build" (Failed("compile error", DateTime.UtcNow))
    test <@ result.Contains("\u2717") @>
    test <@ result.Contains("build") @>
    test <@ result.Contains("compile error") @>

[<Fact>]
let ``formatStepResult shows ellipsis for running`` () =
    let result = formatStepResult "lint" (Running(DateTime.UtcNow.AddSeconds(-5.0)))
    test <@ result.Contains("\u2026") @>
    test <@ result.Contains("lint") @>

[<Fact>]
let ``formatStepResult shows dash for idle`` () =
    let result = formatStepResult "format" Idle
    test <@ result.Contains("\u2014") @>
    test <@ result.Contains("format") @>

[<Fact>]
let ``formatSummary shows all plugins`` () =
    let statuses =
        Map.ofList [ "lint", Completed(DateTime.UtcNow); "build", Completed(DateTime.UtcNow) ]

    let result = formatSummary statuses
    test <@ result.Contains("\u2713") @>
    test <@ result.Contains("lint") @>
    test <@ result.Contains("build") @>

[<Fact>]
let ``formatSummary shows X for failed plugins`` () =
    let statuses = Map.ofList [ "build", Failed("compile error", DateTime.UtcNow) ]

    let result = formatSummary statuses
    test <@ result.Contains("\u2717") @>
    test <@ result.Contains("build") @>

[<Fact>]
let ``formatSummary shows failure message`` () =
    let statuses = Map.ofList [ "build", Failed("compile error", DateTime.UtcNow) ]

    let result = formatSummary statuses
    test <@ result.Contains("compile error") @>

[<Fact>]
let ``formatSummary empty map returns empty string`` () =
    let result = formatSummary Map.empty
    test <@ result = "" @>

[<Fact>]
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

[<Fact>]
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

[<Fact>]
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
    test <@ result.Contains("2 error(s) in 2 file(s)") @>

[<Fact>]
let ``formatErrors with no errors shows clean message`` () =
    let result = formatErrors Map.empty
    test <@ result.Contains("No errors") @>
