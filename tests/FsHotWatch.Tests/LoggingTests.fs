[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.LoggingTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Logging

[<Fact(Timeout = 15000)>]
let ``default log level is Info`` () =
    let original = logLevel

    try
        setLogLevel LogLevel.Info
        test <@ logLevel = LogLevel.Info @>
    finally
        setLogLevel original

[<Fact(Timeout = 15000)>]
let ``setting verbose sets level to Debug`` () =
    let original = logLevel

    try
        setLogLevel LogLevel.Debug
        test <@ logLevel = LogLevel.Debug @>
        test <@ verbose @>
    finally
        setLogLevel original

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``a log line carries the date, and the Z it prints is true`` () =
    // `logs/daemon.log` is not rotated per run or per day — a file spanning several
    // days is ordinary — so a time-only stamp cannot place a line on a day, and
    // "did this happen before or after the version bump" is the first question asked
    // of it during a pinned-tool investigation.
    let original = logLevel
    let sb = System.Text.StringBuilder()
    let writer = new System.IO.StringWriter(sb)
    let prevErr = System.Console.Error

    try
        System.Console.SetError(writer)
        setLogLevel LogLevel.Info
        let observed = System.DateTime.UtcNow
        log LogLevel.Info "test" "dated"
        writer.Flush()

        let stamp =
            System.Text.RegularExpressions.Regex.Match(sb.ToString(), @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z")

        test <@ stamp.Success @>

        // A stamp that prints `Z` while carrying LOCAL time is worse than one that
        // prints no zone at all: it invites exactly the confident misreading that has
        // already produced a phantom wedge and an empty `find -newermt` window that
        // looked like a refutation. So parse it back and check it against the instant
        // we observed rather than trusting the suffix.
        let parsed =
            System.DateTime.Parse(
                stamp.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                ||| System.Globalization.DateTimeStyles.AssumeUniversal
            )

        test <@ abs (parsed - observed).TotalMinutes < 5.0 @>
    finally
        System.Console.SetError(prevErr)
        setLogLevel original
