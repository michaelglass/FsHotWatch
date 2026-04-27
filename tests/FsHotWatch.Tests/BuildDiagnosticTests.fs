module FsHotWatch.Tests.BuildDiagnosticTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Build.BuildDiagnostics

[<Fact(Timeout = 5000)>]
let ``parseMSBuildDiagnostics extracts error lines`` () =
    let output =
        "/src/Foo.fs(12,5): error FS0001: This expression was expected to have type int"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Severity = DiagnosticSeverity.Error @>
    test <@ diagnostics.[0].Line = 12 @>
    test <@ diagnostics.[0].Column = 5 @>
    test <@ diagnostics.[0].Message = "FS0001: This expression was expected to have type int" @>

[<Fact(Timeout = 5000)>]
let ``parseMSBuildDiagnostics extracts warning lines`` () =
    let output =
        "/src/Bar.fs(3,1): warning FS0040: This construct causes code to be less generic"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Severity = DiagnosticSeverity.Warning @>

[<Fact(Timeout = 5000)>]
let ``parseMSBuildDiagnostics handles mixed output`` () =
    let output =
        "Build started...\n/src/Foo.fs(12,5): error FS0001: Bad type\n  Restoring packages...\n/src/Bar.fs(3,1): warning FS0040: Less generic\nBuild succeeded with warnings."

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.Length = 2 @>

[<Fact(Timeout = 5000)>]
let ``parseMSBuildDiagnostics returns empty for clean output`` () =
    let output = "Build succeeded.\n\n    0 Warning(s)\n    0 Error(s)"

    let diagnostics = parseMSBuildDiagnostics output
    test <@ diagnostics.IsEmpty @>

[<Fact(Timeout = 5000)>]
let ``parseDllPaths extracts project-to-dll mapping from dotnet build output`` () =
    let output =
        """
Build succeeded.
  FsHotWatch -> /repo/src/FsHotWatch/bin/Debug/net10.0/FsHotWatch.dll
  FsHotWatch.Cli -> /repo/src/FsHotWatch.Cli/bin/Debug/net10.0/FsHotWatch.Cli.dll
    0 Warning(s)
    0 Error(s)
"""

    let result = parseDllPaths output
    test <@ result |> Map.containsKey "FsHotWatch" @>
    test <@ result |> Map.containsKey "FsHotWatch.Cli" @>
    test <@ result.["FsHotWatch"] = "/repo/src/FsHotWatch/bin/Debug/net10.0/FsHotWatch.dll" @>

[<Fact(Timeout = 5000)>]
let ``parseDllPaths ignores lines that contain 'error'`` () =
    let output = "  MyProject -> /path/error/MyProject.dll"
    let result = parseDllPaths output
    test <@ Map.isEmpty result @>

[<Fact(Timeout = 5000)>]
let ``parseDllPaths returns empty map for output with no arrow lines`` () =
    let result = parseDllPaths "Build succeeded.\n  0 Warning(s)\n  0 Error(s)"
    test <@ Map.isEmpty result @>
