module FsHotWatch.Tests.LoggingTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Logging

[<Fact(Timeout = 30000)>]
let ``default log level is Info`` () =
    let original = logLevel

    try
        setLogLevel LogLevel.Info
        test <@ logLevel = LogLevel.Info @>
    finally
        setLogLevel original

[<Fact(Timeout = 30000)>]
let ``setting verbose sets level to Debug`` () =
    let original = logLevel

    try
        setLogLevel LogLevel.Debug
        test <@ logLevel = LogLevel.Debug @>
        test <@ verbose @>
    finally
        setLogLevel original

[<Fact(Timeout = 30000)>]
let ``isEnabled returns true for levels at or above current`` () =
    let original = logLevel

    try
        setLogLevel LogLevel.Warning
        test <@ isEnabled LogLevel.Error @>
        test <@ isEnabled LogLevel.Warning @>
        test <@ not (isEnabled LogLevel.Info) @>
        test <@ not (isEnabled LogLevel.Debug) @>
    finally
        setLogLevel original

[<Fact(Timeout = 30000)>]
let ``log function respects level`` () =
    let original = logLevel
    let sb = System.Text.StringBuilder()
    let writer = new System.IO.StringWriter(sb)
    let prevErr = System.Console.Error

    try
        System.Console.SetError(writer)
        setLogLevel LogLevel.Warning
        log LogLevel.Error "test" "should appear"
        log LogLevel.Debug "test" "should not appear"
        writer.Flush()
        let output = sb.ToString()
        test <@ output.Contains("should appear") @>
        test <@ not (output.Contains("should not appear")) @>
    finally
        System.Console.SetError(prevErr)
        setLogLevel original
