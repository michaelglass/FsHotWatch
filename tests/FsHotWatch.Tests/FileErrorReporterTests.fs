module FsHotWatch.Tests.FileErrorReporterTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.FileErrorReporter
open FsHotWatch.Tests.TestHelpers

let private entry msg sev =
    { Message = msg
      Severity = sev
      Line = 0
      Column = 0
      Detail = None }

[<Fact>]
let ``Report writes JSON file to error directory`` () =
    withTempDir "fer-report" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)

        (reporter :> IErrorReporter).Report "build" "<build>" [ entry "Build FAILED" DiagnosticSeverity.Error ]

        let expectedFile = Path.Combine(tmpDir, "build--_build_.json")
        test <@ File.Exists(expectedFile) @>
        let content = File.ReadAllText(expectedFile)
        test <@ content.Contains("Build FAILED") @>)

[<Fact>]
let ``Report with empty entries deletes file`` () =
    withTempDir "fer-empty" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "build" "<build>" [ entry "err" DiagnosticSeverity.Error ]
        r.Report "build" "<build>" []
        let expectedFile = Path.Combine(tmpDir, "build--_build_.json")
        test <@ not (File.Exists(expectedFile)) @>)

[<Fact>]
let ``Clear deletes file`` () =
    withTempDir "fer-clear" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ entry "warn" DiagnosticSeverity.Warning ]
        r.Clear "lint" "/src/A.fs"
        let expectedFile = Path.Combine(tmpDir, "lint---src-A.fs.json")
        test <@ not (File.Exists(expectedFile)) @>)

[<Fact>]
let ``ClearPlugin deletes all files for plugin`` () =
    withTempDir "fer-clearplugin" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ entry "a" DiagnosticSeverity.Warning ]
        r.Report "lint" "/src/B.fs" [ entry "b" DiagnosticSeverity.Warning ]
        r.Report "fcs" "/src/A.fs" [ entry "c" DiagnosticSeverity.Error ]
        r.ClearPlugin "lint"
        let remaining = Directory.GetFiles(tmpDir, "*.json")
        test <@ remaining.Length = 1 @>
        test <@ Path.GetFileName(remaining.[0]).StartsWith("fcs--") @>)

[<Fact>]
let ``ClearAll deletes all files`` () =
    withTempDir "fer-clearall" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)
        let r = reporter :> IErrorReporter
        r.Report "lint" "/src/A.fs" [ entry "a" DiagnosticSeverity.Warning ]
        r.Report "fcs" "/src/B.fs" [ entry "b" DiagnosticSeverity.Error ]
        r.ClearAll()
        let remaining = Directory.GetFiles(tmpDir, "*.json")
        test <@ remaining.Length = 0 @>)

[<Fact>]
let ``sanitizeFileName replaces slashes and angle brackets`` () =
    withTempDir "fer-sanitize" (fun tmpDir ->
        let reporter = FileErrorReporter(tmpDir)

        (reporter :> IErrorReporter).Report "fcs" "/src/FsHotWatch/Daemon.fs" [ entry "e" DiagnosticSeverity.Error ]

        let expectedFile = Path.Combine(tmpDir, "fcs---src-FsHotWatch-Daemon.fs.json")

        test <@ File.Exists(expectedFile) @>)
