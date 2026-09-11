namespace FsHotWatch.Tests

open System
open System.IO
open System.Xml.Linq
open Xunit
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

[<CollectionDefinition("Analyzer provenance builds", DisableParallelization = true)>]
type AnalyzerProvenanceBuildCollection() = class end

module private AnalyzerProvenanceBuildFixture =
    let name value = XName.Get value
    let attr key (value: string) = XAttribute(name key, value)
    let node key (children: obj array) = XElement(name key, children)

    let rec repoRoot (directory: DirectoryInfo) =
        if isNull directory then
            failwith "Cannot find FsHotWatch checkout"
        elif File.Exists(Path.Combine(directory.FullName, ".fshw.json")) then
            directory.FullName
        else
            repoRoot directory.Parent

    let target =
        Path.Combine(
            repoRoot (DirectoryInfo(AppContext.BaseDirectory)),
            "src/FsHotWatch.Analyzers/build/FsHotWatch.AnalyzerProvenance.targets"
        )

    let prepare directory stem (source: string) includePattern sourceLink reference =
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, "Rules.fs"), source)

        if sourceLink then
            Directory.CreateDirectory(Path.Combine(directory, "obj")) |> ignore

            let document =
                System.Text.Json.JsonSerializer.Serialize(
                    {| documents = Map.ofList [ directory + "/**", "https://example.invalid/source/*" ] |}
                )

            File.WriteAllText(Path.Combine(directory, "obj", "links.json"), document)

        let project =
            node
                "Project"
                [| attr "Sdk" "Microsoft.NET.Sdk"
                   node
                       "PropertyGroup"
                       [| node "TargetFramework" [| "net10.0" |]
                          node "IsPackable" [| "false" |]
                          node "GenerateAssemblyInfo" [| "false" |]
                          node "AssemblyName" [| stem |]
                          node "FsHotWatchAnalyzerProvenance" [| "true" |]
                          if sourceLink then
                              node "SourceLink" [| "$(MSBuildProjectDirectory)/obj/links.json" |] |]
                   node
                       "ItemGroup"
                       [| node "Compile" [| attr "Include" "Rules.fs" |]
                          match includePattern with
                          | Some pattern -> node "Compile" [| attr "Include" pattern |]
                          | None -> ()
                          match reference with
                          | Some path -> node "ProjectReference" [| attr "Include" path |]
                          | None -> () |]
                   node "Import" [| attr "Project" target |]
                   node
                       "Target"
                       [| attr "Name" "ChangeInputWhileCompiling"
                          attr "BeforeTargets" "_FshwRecordAnalyzerCompilation"
                          attr "Condition" "'$(MutateAfterCompile)' == 'true'"
                          node
                              "WriteLinesToFile"
                              [| attr "File" "Rules.fs"
                                 attr "Lines" "module MiniRules%0Alet answer = 99"
                                 attr "Overwrite" "true" |] |] |]

        project.Save(Path.Combine(directory, stem + ".fsproj"))

    let build directory stem extra =
        runProcess
            "dotnet"
            ($"build {stem}.fsproj --nologo -v quiet {extra}")
            directory
            []
            (ProcessBounds.silent (TimeSpan.FromMinutes 2.0))

    let succeeds =
        function
        | Succeeded _ -> ()
        | other -> Assert.Fail($"Expected producer build success: %A{other}")

    let fails reason =
        function
        | Failed(_, output) -> Assert.Contains(reason, ProcessOutput.text output)
        | other -> Assert.Fail($"Expected producer refusal: %A{other}")

    let output directory stem =
        Path.Combine(directory, "bin", "Debug", "net10.0", stem + ".dll")

    let key directory stem =
        let path = output directory stem |> Path.GetDirectoryName

        let handler =
            FsHotWatch.Analyzers.AnalyzersPlugin.create (Some directory) [ path ] None DiagnosticSeverity.Error

        handler.CacheKey.Value(FileChecked(fakeFileCheckResult (Path.Combine(directory, "Subject.fs"))))

[<Collection("Analyzer provenance builds")>]
type AnalyzerProvenanceBuildTests() =
    [<Fact(Timeout = 300000)>]
    member _.``real producer source link metadata does not partition equivalent workspace keys``() =
        withTempDir "real-parity" (fun root ->
            let first = Path.Combine(root, "a")
            let second = Path.Combine(root, "a-substantially-longer-workspace")

            for directory in [ first; second ] do
                AnalyzerProvenanceBuildFixture.prepare
                    directory
                    "Mini"
                    "module MiniRules\nlet answer = 1"
                    None
                    true
                    None

                AnalyzerProvenanceBuildFixture.build directory "Mini" ""
                |> AnalyzerProvenanceBuildFixture.succeeds

            let firstKey = AnalyzerProvenanceBuildFixture.key first "Mini"
            let secondKey = AnalyzerProvenanceBuildFixture.key second "Mini"
            Assert.True(firstKey.IsSome && secondKey.IsSome)
            Assert.Equal(firstKey, secondKey))

    [<Fact(Timeout = 300000)>]
    member _.``real producer external glob detects new linked sources without refusing unchanged membership``() =
        withTempDir "external-glob" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let shared = Path.Combine(root, "Shared")
            Directory.CreateDirectory shared |> ignore
            File.WriteAllText(Path.Combine(shared, "Shared.fs"), "module Shared\nlet value = 1")

            AnalyzerProvenanceBuildFixture.prepare
                producer
                "Mini"
                "module MiniRules\nlet answer = 1"
                (Some "../Shared/**/*.fs")
                false
                None

            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let original = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(original.IsSome)
            Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")
            File.WriteAllText(Path.Combine(shared, "NewRule.fs"), "module NewRule\nlet value = 2")
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone)

            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let rebuilt = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(rebuilt.IsSome)
            Assert.NotEqual(original, rebuilt))

    [<Fact(Timeout = 300000)>]
    member _.``incremental build cannot bless a replaced compiler output``() =
        withTempDir "replaced-output" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None

            AnalyzerProvenanceBuildFixture.build root "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let receipt =
                AnalyzerProvenanceBuildFixture.output root "Mini" + ".fshw-analyzer.xml"

            let originalReceipt = File.ReadAllText receipt
            let intermediate = Path.Combine(root, "obj", "Debug", "net10.0", "Mini.dll")
            let timestamp = File.GetLastWriteTimeUtc intermediate
            let bytes = File.ReadAllBytes intermediate
            bytes.[bytes.Length - 1] <- bytes.[bytes.Length - 1] ^^^ 1uy
            File.WriteAllBytes(intermediate, bytes)
            File.SetLastWriteTimeUtc(intermediate, timestamp)

            AnalyzerProvenanceBuildFixture.build root "Mini" ""
            |> AnalyzerProvenanceBuildFixture.fails "Inputs/output changed during analyzer build"

            Assert.Equal(originalReceipt, File.ReadAllText receipt))

    [<Fact(Timeout = 300000)>]
    member _.``a source changed after compiler execution cannot receive a successful receipt``() =
        withTempDir "mid-build" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None

            AnalyzerProvenanceBuildFixture.build root "Mini" "-p:MutateAfterCompile=true"
            |> AnalyzerProvenanceBuildFixture.fails "Analyzer inputs changed while compiler was running"

            Assert.False(File.Exists(AnalyzerProvenanceBuildFixture.output root "Mini" + ".fshw-analyzer.xml")))

    [<Fact(Timeout = 300000)>]
    member _.``first party project dependencies have validated source provenance``() =
        withTempDir "project-dependency" (fun root ->
            let dependency = Path.Combine(root, "Library")
            let producer = Path.Combine(root, "Producer")
            AnalyzerProvenanceBuildFixture.prepare dependency "Library" "module Library\nlet value = 1" None false None

            AnalyzerProvenanceBuildFixture.prepare
                producer
                "Mini"
                "module MiniRules\nlet answer = Library.value"
                None
                false
                (Some "../Library/Library.fsproj")

            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsSome)
            File.AppendAllText(Path.Combine(dependency, "Rules.fs"), "\nlet added = 2")
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone))


    [<Theory(Timeout = 300000)>]
    [<InlineData("imported")>]
    [<InlineData("indirect")>]
    [<InlineData("initially-absent")>]
    [<InlineData("excluded-and-removed")>]
    [<InlineData("custom-global")>]
    member _.``evaluated membership preserves the producer source selection``(shape: string) =
        withTempDir "evaluated-membership" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let shared = Path.Combine(root, "Shared")
            let source filename = Path.Combine(shared, filename)
            let add filename moduleName =
                Directory.CreateDirectory shared |> ignore
                File.WriteAllText(source filename, $"module {moduleName}\nlet value = 1")

            if shape <> "initially-absent" then
                add "Existing.fs" "Existing"

            AnalyzerProvenanceBuildFixture.prepare
                producer "Mini" "module MiniRules\nlet answer = 1" None false None

            let projectPath = Path.Combine(producer, "Mini.fsproj")
            let project = XElement.Load projectPath
            let n = AnalyzerProvenanceBuildFixture.node
            let a = AnalyzerProvenanceBuildFixture.attr
            let selection = "../Shared/**/*.fs"
            let items =
                match shape with
                | "indirect" ->
                    n "ItemGroup" [|
                        n "LinkedRule" [| a "Include" selection |]
                        n "Compile" [| a "Include" "@(LinkedRule)" |]
                    |]
                | "excluded-and-removed" ->
                    n "ItemGroup" [|
                        n "Compile" [| a "Include" selection; a "Exclude" "../Shared/Excluded*.fs" |]
                        n "Compile" [| a "Remove" "../Shared/Removed*.fs" |]
                        n "Compile" [| a "Include" "../Shared/RemovedButRestored*.fs" |]
                    |]
                | "custom-global" ->
                    n "ItemGroup" [|
                        a "Condition" "'$(RuleFlavor)' == 'Enabled'"
                        n "Compile" [| a "Include" selection |]
                    |]
                | _ -> n "ItemGroup" [| n "Compile" [| a "Include" selection |] |]

            if shape = "imported" then
                let definitions = Path.Combine(producer, "Definitions")
                Directory.CreateDirectory definitions |> ignore
                let imported = Path.Combine(definitions, "Linked.props")
                (n "Project" [| items |]).Save imported
                project.Add(n "Import" [| a "Project" "Definitions/Linked.props" |])
            else
                project.Add items

            project.Save projectPath
            let arguments = if shape = "custom-global" then "-p:RuleFlavor=Enabled" else ""
            AnalyzerProvenanceBuildFixture.build producer "Mini" arguments
            |> AnalyzerProvenanceBuildFixture.succeeds

            let original = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(original.IsSome, $"Unchanged {shape} membership must remain reusable")
            Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")

            if shape = "excluded-and-removed" then
                add "ExcludedNew.fs" "ExcludedNew"
                add "RemovedNew.fs" "RemovedNew"
                Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")
                add "RemovedButRestoredNew.fs" "Restored"
            else
                add "Added.fs" "Added"

            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone,
                        $"New selected source in {shape} must invalidate the old receipt")
            AnalyzerProvenanceBuildFixture.build producer "Mini" arguments
            |> AnalyzerProvenanceBuildFixture.succeeds
            let rebuilt = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(rebuilt.IsSome)
            Assert.NotEqual(original, rebuilt)

            let added = if shape = "excluded-and-removed" then "RemovedButRestoredNew.fs" else "Added.fs"
            File.Delete(source added)
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone))

    [<Theory(Timeout = 300000)>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``current environment membership is observed without replaying environment values``(propertyFunction: bool) =
        withTempDir "current-environment" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let shared = Path.Combine(root, "Shared")
            Directory.CreateDirectory shared |> ignore
            File.WriteAllText(Path.Combine(shared, "Selected.fs"), "module Selected\nlet value = 1")
            let variable = "FSHW_MEMBERSHIP_" + Guid.NewGuid().ToString("N")
            let previous = Environment.GetEnvironmentVariable variable
            try
                Environment.SetEnvironmentVariable(variable, null)
                AnalyzerProvenanceBuildFixture.prepare
                    producer "Mini" "module MiniRules\nlet answer = 1" None false None
                let projectPath = Path.Combine(producer, "Mini.fsproj")
                let project = XElement.Load projectPath
                let n = AnalyzerProvenanceBuildFixture.node
                let a = AnalyzerProvenanceBuildFixture.attr
                let value =
                    if propertyFunction then $"$([System.Environment]::GetEnvironmentVariable('{variable}'))"
                    else "$(" + variable + ")"
                project.Add(n "ItemGroup" [|
                    a "Condition" ("'" + value + "' == 'enabled'")
                    n "Compile" [| a "Include" "../Shared/*.fs" |]
                |])
                project.Save projectPath
                AnalyzerProvenanceBuildFixture.build producer "Mini" ""
                |> AnalyzerProvenanceBuildFixture.succeeds
                let original = AnalyzerProvenanceBuildFixture.key producer "Mini"
                Assert.True(original.IsSome)
                Environment.SetEnvironmentVariable(variable, "still-disabled")
                Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")
                Environment.SetEnvironmentVariable(variable, "enabled")
                Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone)
            finally
                Environment.SetEnvironmentVariable(variable, previous))

    [<Fact(Timeout = 300000)>]
    member _.``producer invocation values stay in private local context``() =
        withTempDir "private-invocation" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None
            let probe = "synthetic-private-global-" + Guid.NewGuid().ToString("N")
            AnalyzerProvenanceBuildFixture.build root "Mini" ("-p:RuleFlavor=" + probe)
            |> AnalyzerProvenanceBuildFixture.succeeds
            let receiptPath = AnalyzerProvenanceBuildFixture.output root "Mini" + ".fshw-analyzer.xml"
            let publicText = File.ReadAllText receiptPath
            Assert.False(publicText.Contains(probe, StringComparison.Ordinal))
            let receipt = XElement.Parse publicText
            let reference = receipt.Element(XName.Get "Evaluation")
            Assert.NotNull reference
            let id = reference.Attribute(XName.Get "id").Value
            let directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "fshw", "analyzer-contexts")
            let contextPath = Path.Combine(directory, id + ".xml")
            let context = XElement.Load contextPath
            Assert.True(context.ToString().Contains(probe, StringComparison.Ordinal))
            Assert.Null(context.Element(XName.Get "Environment"))
            if not (OperatingSystem.IsWindows()) then
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode contextPath)
                Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute,
                             File.GetUnixFileMode directory)
            Assert.True((AnalyzerProvenanceBuildFixture.key root "Mini").IsSome))

    [<Theory(Timeout = 300000)>]
    [<InlineData("percent%value", "percent%25value")>]
    [<InlineData("semi;colon", "semi%3Bcolon")>]
    [<InlineData("com,ma", "com%2Cma")>]
    [<InlineData("a\"quote", "a%22quote")>]
    [<InlineData("line\nfeed", "line%0Afeed")>]
    [<InlineData("two words", "two words")>]
    [<InlineData("trailing\\", "trailing\\")>]
    member _.``invocation globals survive private response file replay``(expected: string, encoded: string) =
        withTempDir "global-escaping" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None
            let argument =
                if encoded.EndsWith("\\", StringComparison.Ordinal) then "-p:RuleFlavor=" + encoded
                else "\"-p:RuleFlavor=" + encoded + "\""
            AnalyzerProvenanceBuildFixture.build root "Mini" argument
            |> AnalyzerProvenanceBuildFixture.succeeds
            let receipt = XElement.Load(AnalyzerProvenanceBuildFixture.output root "Mini" + ".fshw-analyzer.xml")
            let id = receipt.Element(XName.Get "Evaluation").Attribute(XName.Get "id").Value
            let context = XElement.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                    "fshw", "analyzer-contexts", id + ".xml"))
            let actual =
                context.Element(XName.Get "Globals").Elements(XName.Get "Property")
                |> Seq.find (fun property -> property.Attribute(XName.Get "name").Value = "RuleFlavor")
                |> fun property -> property.Attribute(XName.Get "value").Value
            Assert.True((actual = expected), "Producer test invocation must preserve the intended synthetic value")
            Assert.True((AnalyzerProvenanceBuildFixture.key root "Mini").IsSome))

    [<Theory(Timeout = 300000)>]
    [<InlineData("missing")>]
    [<InlineData("digest")>]
    [<InlineData("another-producer")>]
    member _.``missing or mismatched private invocation context refuses reuse``(damage: string) =
        withTempDir "context-refusal" (fun root ->
            let producer = Path.Combine(root, "Producer")
            AnalyzerProvenanceBuildFixture.prepare producer "Mini" "module MiniRules\nlet answer = 1" None false None
            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsSome)
            let path = AnalyzerProvenanceBuildFixture.output producer "Mini" + ".fshw-analyzer.xml"
            let receipt = XElement.Load path
            let reference = receipt.Element(XName.Get "Evaluation")
            if damage = "missing" then
                reference.SetAttributeValue(XName.Get "id", Guid.NewGuid().ToString("N"))
            elif damage = "digest" then
                reference.SetAttributeValue(XName.Get "hash", String.replicate 64 "0")
            else
                let other = Path.Combine(root, "OtherProducer")
                AnalyzerProvenanceBuildFixture.prepare other "Mini" "module MiniRules\nlet answer = 1" None false None
                AnalyzerProvenanceBuildFixture.build other "Mini" ""
                |> AnalyzerProvenanceBuildFixture.succeeds
                let otherReceipt = XElement.Load(AnalyzerProvenanceBuildFixture.output other "Mini" + ".fshw-analyzer.xml")
                reference.ReplaceWith(XElement(otherReceipt.Element(XName.Get "Evaluation")))
            receipt.Save path
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone))


    [<Fact(Timeout = 300000)>]
    member _.``producer rejects another valid private context before publication comparison``() =
        withTempDir "publish-context-binding" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let other = Path.Combine(root, "Other")
            for directory in [ producer; other ] do
                AnalyzerProvenanceBuildFixture.prepare directory "Mini" "module MiniRules\nlet answer = 1" None false None
                AnalyzerProvenanceBuildFixture.build directory "Mini" ""
                |> AnalyzerProvenanceBuildFixture.succeeds
            let receiptPath = AnalyzerProvenanceBuildFixture.output producer "Mini" + ".fshw-analyzer.xml"
            let original = File.ReadAllText receiptPath
            let otherReceipt = XElement.Load(AnalyzerProvenanceBuildFixture.output other "Mini" + ".fshw-analyzer.xml")
            let compiledPath = Path.Combine(producer, "obj", "Debug", "net10.0", "Mini.fshw-inputs.xml.compiled")
            let compiled = XElement.Load compiledPath
            compiled.Element(XName.Get "Evaluation").ReplaceWith(XElement(otherReceipt.Element(XName.Get "Evaluation")))
            compiled.Save compiledPath
            AnalyzerProvenanceBuildFixture.build producer "Mini" "-t:_FshwPublishAnalyzerProvenance"
            |> AnalyzerProvenanceBuildFixture.fails "Analyzer producer evaluated source membership changed or could not be verified"
            Assert.Equal(original, File.ReadAllText receiptPath))
