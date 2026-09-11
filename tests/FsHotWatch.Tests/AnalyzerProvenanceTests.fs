module FsHotWatch.Tests.AnalyzerProvenanceTests

open System
open System.IO
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

let private digest path =
    File.ReadAllBytes path
    |> Security.Cryptography.SHA256.HashData
    |> Convert.ToHexString

let private attribute name (value: string) = XAttribute(XName.Get name, value)
let private element name (children: obj array) = XElement(XName.Get name, children)

let private materialize root (bytes: byte array) =
    let outputDir = Path.Combine(root, "bin")
    Directory.CreateDirectory outputDir |> ignore
    let source = Path.Combine(root, "Rules.fs")
    let project = Path.Combine(root, "Rules.fsproj")
    File.WriteAllText(source, "module Rules\nlet rule = 1")
    File.WriteAllText(project, "<Project />")
    let output = Path.Combine(outputDir, "Rules.dll")
    File.WriteAllBytes(output, bytes)

    let inputs =
        [| for key, path in [ "source:0:Rules.fs", source; "project:Rules.fsproj", project ] do
               yield
                   element "File" [| attribute "key" key; attribute "path" path; attribute "hash" (digest path) |]
                   :> obj |]

    let receipt =
        element
            "AnalyzerProvenance"
            [| attribute "version" "1"
               attribute "output" output
               attribute "outputHash" (digest output)
               element "Inputs" inputs
               element
                   "Discovery"
                   [| element
                          "Directory"
                          [| attribute "path" root
                             attribute "hash" (FsHotWatch.CheckCache.sha256Hex "8:Rules.fs12:Rules.fsproj") |] |]
               element
                   "Options"
                   [| element "Option" [| attribute "name" "TargetFramework"; attribute "value" "net10.0" |] |]
               element
                   "Packages"
                   [| element "Package" [| attribute "id" "FSharp.Analyzers.SDK"; attribute "version" "0.37.2" |] |]
               element "Dependencies" [||] |]

    receipt.Save(output + ".fshw-analyzer.xml")
    outputDir, source, project, output

let private key root outputDir =
    let handler = create (Some root) [ outputDir ] None DiagnosticSeverity.Error
    let event = FileChecked(fakeFileCheckResult (Path.Combine(root, "Subject.fs")))
    handler.CacheKey.Value event

[<Fact(Timeout = 15000)>]
let ``identical first party inputs share a key despite emitted debug byte differences`` () =
    withTempDir "provenance" (fun root ->
        let first = Path.Combine(root, "a")
        let second = Path.Combine(root, "a-much-longer-workspace")
        let firstBin, _, _, _ = materialize first [| 1uy; 2uy |]
        let secondBin, _, _, _ = materialize second [| 9uy; 8uy; 7uy |]
        let firstKey = key first firstBin
        let secondKey = key second secondBin
        test <@ firstKey.IsSome @>
        test <@ firstKey = secondKey @>)

[<Fact(Timeout = 15000)>]
let ``an unaccountable analyzer output cannot supply a cache key`` () =
    withTempDir "no-receipt" (fun root ->
        let outputDir, _, _, output = materialize root [| 1uy |]
        File.Delete(output + ".fshw-analyzer.xml")
        test <@ (key root outputDir).IsNone @>)

[<Theory(Timeout = 15000)>]
[<InlineData("source")>]
[<InlineData("project")>]
[<InlineData("output")>]
let ``changed inputs or replaced output cannot reuse a prior build receipt`` changed =
    withTempDir "stale-receipt" (fun root ->
        let outputDir, source, project, output = materialize root [| 1uy |]

        let path =
            match changed with
            | "source" -> source
            | "project" -> project
            | _ -> output

        File.AppendAllText(path, "changed")
        test <@ (key root outputDir).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``one warm handler changes key after a newly materialized source change`` () =
    withTempDir "warm-key" (fun root ->
        let outputDir, source, _, output = materialize root [| 1uy |]
        let handler = create (Some root) [ outputDir ] None DiagnosticSeverity.Error
        let event = FileChecked(fakeFileCheckResult (Path.Combine(root, "Subject.fs")))
        let first = handler.CacheKey.Value event
        File.AppendAllText(source, "\nlet addedRule = 2")
        test <@ (handler.CacheKey.Value event).IsNone @>
        File.WriteAllBytes(output, [| 2uy |])
        let receipt = XDocument.Load(output + ".fshw-analyzer.xml")

        for input in receipt.Descendants(XName.Get "File") do
            let path = input.Attribute(XName.Get "path").Value
            input.SetAttributeValue(XName.Get "hash", digest path)

        receipt.Root.SetAttributeValue(XName.Get "outputHash", digest output)
        receipt.Save(output + ".fshw-analyzer.xml")
        let second = handler.CacheKey.Value event
        test <@ first.IsSome && second.IsSome @>
        test <@ first <> second @>)

[<Fact(Timeout = 15000)>]
let ``a new source file refuses a receipt over the old evaluated source list`` () =
    withTempDir "new-source" (fun root ->
        let outputDir, _, _, _ = materialize root [| 1uy |]
        File.WriteAllText(Path.Combine(root, "NewRule.fs"), "module NewRule")
        test <@ (key root outputDir).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``copied package identity uses package version rather than materialized bytes`` () =
    withTempDir "package" (fun root ->
        let outputDir = Path.Combine(root, "bin")
        Directory.CreateDirectory outputDir |> ignore
        let output = Path.Combine(outputDir, "Rules.dll")

        let publish version (bytes: byte array) =
            File.WriteAllBytes(output, bytes)

            let receipt =
                element
                    "PackageProvenance"
                    [| attribute "schema" "1"
                       attribute "id" "Rules.Package"
                       attribute "version" version
                       attribute "output" output
                       attribute "outputHash" (digest output) |]

            receipt.Save(output + ".fshw-package.xml")

        publish "1.0.0" [| 1uy |]
        let first = key root outputDir
        publish "1.0.0" [| 9uy; 8uy |]
        let samePackage = key root outputDir
        publish "2.0.0" [| 9uy; 8uy |]
        let changedPackage = key root outputDir
        test <@ first.IsSome @>
        test <@ first = samePackage @>
        test <@ first <> changedPackage @>)

[<Fact(Timeout = 15000)>]
let ``configured real analyzer producers earn usable source and package provenance`` () =
    let rec findRoot (directory: DirectoryInfo) =
        if isNull directory then
            failwith "Cannot find built FsHotWatch repository"
        elif File.Exists(Path.Combine(directory.FullName, ".fshw.json")) then
            directory.FullName
        else
            findRoot directory.Parent

    let root = findRoot (DirectoryInfo(AppContext.BaseDirectory))

    for relative in
        [ "analyzers/FsHotWatch.Rules/bin/Debug/net10.0"
          "tools/fsharplint-shim/bin/Debug/net10.0" ] do
        let outputDir = Path.Combine(root, relative)

        let result =
            FsHotWatch.Analyzers.AnalyzerProvenance.trySnapshot
                (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes)
                [ outputDir ]

        match result with
        | Result.Error error -> Assert.Fail(error)
        | Ok snapshot ->
            test <@ not (String.IsNullOrEmpty(FsHotWatch.Analyzers.AnalyzerProvenance.semantic snapshot)) @>
