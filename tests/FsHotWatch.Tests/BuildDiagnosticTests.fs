module FsHotWatch.Tests.BuildDiagnosticTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Build.BuildDiagnostics

[<Fact(Timeout = 30000)>]
let ``parseMSBuildDiagnostics extracts error lines`` () =
    let output =
        "/src/Foo.fs(12,5): error FS0001: This expression was expected to have type int"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Severity = DiagnosticSeverity.Error @>
    test <@ diagnostics.[0].Line = 12 @>
    test <@ diagnostics.[0].Column = 5 @>
    test <@ diagnostics.[0].Message = "FS0001: This expression was expected to have type int" @>

[<Fact(Timeout = 30000)>]
let ``parseMSBuildDiagnostics extracts warning lines`` () =
    let output =
        "/src/Bar.fs(3,1): warning FS0040: This construct causes code to be less generic"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Severity = DiagnosticSeverity.Warning @>

[<Fact(Timeout = 30000)>]
let ``parseMSBuildDiagnostics handles mixed output`` () =
    let output =
        "Build started...\n/src/Foo.fs(12,5): error FS0001: Bad type\n  Restoring packages...\n/src/Bar.fs(3,1): warning FS0040: Less generic\nBuild succeeded with warnings."

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 2 @>

[<Fact(Timeout = 30000)>]
let ``parseMSBuildDiagnostics returns empty for clean output`` () =
    let output = "Build succeeded.\n\n    0 Warning(s)\n    0 Error(s)"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.IsEmpty @>
