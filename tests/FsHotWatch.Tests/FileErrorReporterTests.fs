module FsHotWatch.Tests.FileErrorReporterTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.FileErrorReporter
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 30000)>]
let ``Report writes JSON file to error directory`` () =
    withTempDir "fer-report" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)

        (reporter :> IErrorReporter).Report "build" "<build>" [ errorEntry "Build FAILED" DiagnosticSeverity.Error ]

        let expectedFile = Path.Combine(tmpDir, "build--_build_.json")
        test <@ File.Exists(expectedFile) @>
        let content = File.ReadAllText(expectedFile)
        test <@ content.Contains("Build FAILED") @>)

[<Fact(Timeout = 30000)>]
let ``Report with empty entries deletes file`` () =
    withTempDir "fer-empty" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "build" "<build>" [ errorEntry "err" DiagnosticSeverity.Error ]
        r.Report "build" "<build>" []
        let expectedFile = Path.Combine(tmpDir, "build--_build_.json")
        test <@ not (File.Exists(expectedFile)) @>)

[<Fact(Timeout = 30000)>]
let ``Clear deletes file`` () =
    withTempDir "fer-clear" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ errorEntry "warn" DiagnosticSeverity.Warning ]
        r.Clear "lint" "/src/A.fs"
        let expectedFile = Path.Combine(tmpDir, "lint---src-A.fs.json")
        test <@ not (File.Exists(expectedFile)) @>)

[<Fact(Timeout = 30000)>]
let ``ClearPlugin deletes all files for plugin`` () =
    withTempDir "fer-clearplugin" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ errorEntry "a" DiagnosticSeverity.Warning ]
        r.Report "lint" "/src/B.fs" [ errorEntry "b" DiagnosticSeverity.Warning ]
        r.Report "fcs" "/src/A.fs" [ errorEntry "c" DiagnosticSeverity.Error ]
        r.ClearPlugin "lint"
        let remaining = Directory.GetFiles(tmpDir, "*.json")
        test <@ remaining.Length = 1 @>
        test <@ Path.GetFileName(remaining.[0]).StartsWith("fcs--") @>)

[<Fact(Timeout = 30000)>]
let ``ClearAll deletes all files`` () =
    withTempDir "fer-clearall" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ errorEntry "a" DiagnosticSeverity.Warning ]
        r.Report "fcs" "/src/B.fs" [ errorEntry "b" DiagnosticSeverity.Error ]
        r.ClearAll()
        let remaining = Directory.GetFiles(tmpDir, "*.json")
        test <@ remaining.Length = 0 @>)

[<Fact(Timeout = 30000)>]
let ``sanitizeFileName replaces slashes and angle brackets`` () =
    withTempDir "fer-sanitize" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)

        (reporter :> IErrorReporter).Report
            "fcs"
            "/src/FsHotWatch/Daemon.fs"
            [ errorEntry "e" DiagnosticSeverity.Error ]

        let expectedFile = Path.Combine(tmpDir, "fcs---src-FsHotWatch-Daemon.fs.json")

        test <@ File.Exists(expectedFile) @>)
