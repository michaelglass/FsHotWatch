module FsHotWatch.Tests.RunOnceOutputTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput

[<Fact>]
let ``formatProgressLine shows status for each plugin`` () =
    let now = DateTime.UtcNow

    let statuses =
        Map.ofList
            [ "build", Running(now.AddSeconds(-12.0))
              "format", Completed now
              "lint", Idle ]

    let result = formatProgressLine statuses
    test <@ result.Contains("format") @>
    test <@ result.Contains("\u2713") @>
    test <@ result.Contains("lint") @>
    test <@ result.Contains("...") @>
    test <@ result.Contains("build") @>

[<Fact>]
let ``formatSummary shows checkmark for completed plugins`` () =
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
let ``formatSummary pads plugin names for alignment`` () =
    let statuses =
        Map.ofList [ "lint", Completed(DateTime.UtcNow); "build", Completed(DateTime.UtcNow) ]

    let result = formatSummary statuses
    // "build" is 5 chars, "lint" is 4 chars — lint should be padded
    test <@ result.Contains("lint ") @>

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
    test <@ result.Contains("error: type error") @>
    test <@ result.Contains("warning: bad name") @>

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
