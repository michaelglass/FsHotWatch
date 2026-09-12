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
    // Resolve parent directory aliases as the SDK process working directory does.
    // GetFullPath alone keeps macOS /var while the producer records /private/var.
    let rec physicalDirectory (directory: DirectoryInfo) =
        if isNull directory.Parent then directory.FullName
        else
            let candidate = DirectoryInfo(Path.Combine(physicalDirectory directory.Parent, directory.Name))
            match candidate.ResolveLinkTarget(true) with
            | null -> candidate.FullName
            | target -> target.FullName

    let ownProcesses () =
        let registry = FsHotWatch.ProcessRegistry.Registry()
        let scope = FsHotWatch.ProcessRegistry.install registry
        { new IDisposable with
            member _.Dispose() =
                try
                    Assert.Empty(registry.Snapshot())
                    Assert.Empty(registry.Leaks)
                finally
                    scope.Dispose() }

    let withProducerDir prefix body =
        let directory = Path.Combine(Path.GetTempPath(), $"fshw-{prefix}-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore
        let root = physicalDirectory (DirectoryInfo directory)
        // Synthetic fixtures remain in place on failure, preserving path-bound receipts.
        let result =
            use ownership = ownProcesses ()
            body root
        Directory.Delete(root, true)
        result

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

    let prepareProjectChain root transitive =
        let dependency = Path.Combine(root, "Library")
        let producer = Path.Combine(root, "Producer")
        prepare dependency "Library" "module Library\nlet value = 1" None false None

        let reference, source =
            if transitive then
                let middle = Path.Combine(root, "Middle")
                prepare middle "Middle" "module Middle\nlet value = Library.value" None false (Some "../Library/Library.fsproj")
                "../Middle/Middle.fsproj", "module MiniRules\nlet answer = Middle.value"
            else
                "../Library/Library.fsproj", "module MiniRules\nlet answer = Library.value"

        prepare producer "Mini" source None false (Some reference)
        build producer "Mini" "" |> succeeds
        dependency, producer

    let singleOutputSnapshot directory stem =
        FsHotWatch.Analyzers.AnalyzerProvenance.trySnapshot
            ((<>) stem)
            [ output directory stem |> Path.GetDirectoryName ]

    let requireSnapshot =
        function
        | Result.Ok snapshot -> snapshot
        | Result.Error reason -> failwith $"Expected accountable analyzer output: {reason}"

[<Collection("Analyzer provenance builds")>]
type AnalyzerProvenanceBuildTests() =
    [<Theory(Timeout = 300000)>]
    [<InlineData("same-output")>]
    [<InlineData("different-copy")>]
    [<InlineData("retargeted-alias")>]
    member _.``output aliases preserve physical receipt binding``(scenario: string) =
        AnalyzerProvenanceBuildFixture.withProducerDir "output-alias" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let copied = Path.Combine(root, "Copied")
            let alias = Path.Combine(root, "Alias")
            AnalyzerProvenanceBuildFixture.prepare producer "Mini" "module MiniRules\nlet answer = 1" None false None
            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let original = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(original.IsSome, "The physical producer must establish a reusable baseline")
            let output = AnalyzerProvenanceBuildFixture.output producer "Mini"
            let copiedOutput = AnalyzerProvenanceBuildFixture.output copied "Mini"
            Directory.CreateDirectory(Path.GetDirectoryName copiedOutput) |> ignore
            File.Copy(output, copiedOutput)
            File.Copy(output + ".fshw-analyzer.xml", copiedOutput + ".fshw-analyzer.xml")
            Assert.True(File.ReadAllBytes(output) = File.ReadAllBytes(copiedOutput))

            if scenario = "different-copy" then
                Assert.True(
                    (AnalyzerProvenanceBuildFixture.key copied "Mini").IsNone,
                    "Identical bytes in a different physical output do not inherit the original receipt binding"
                )
            else
                Directory.CreateSymbolicLink(alias, producer) |> ignore
                try
                    let aliased = AnalyzerProvenanceBuildFixture.key alias "Mini"
                    Assert.True(aliased.IsSome, "A directory alias must admit the same physical analyzer output")
                    Assert.Equal(original, aliased)

                    let semantic directory =
                        let outputDirectory = AnalyzerProvenanceBuildFixture.output directory "Mini" |> Path.GetDirectoryName
                        match FsHotWatch.Analyzers.AnalyzerProvenance.trySnapshot ((<>) "Mini") [ outputDirectory ] with
                        | Result.Ok snapshot -> FsHotWatch.Analyzers.AnalyzerProvenance.semantic snapshot
                        | Result.Error reason -> failwith reason

                    Assert.Equal(semantic producer, semantic alias)

                    if scenario = "retargeted-alias" then
                        Directory.Delete alias
                        Directory.CreateSymbolicLink(alias, copied) |> ignore
                        Assert.True(
                            (AnalyzerProvenanceBuildFixture.key alias "Mini").IsNone,
                            "Retargeting an admitted alias to a copy must recheck physical receipt binding"
                        )
                finally
                    Directory.Delete alias)

    [<Fact(Timeout = 300000)>]
    member _.``real producer source link metadata does not partition equivalent workspace keys``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "real-parity" (fun root ->
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

                // F# compiler ilwritepdb.fs emits this SourceLink kind on the
                // portable PDB module; prove the actual payload for both roots.
                let sourceLinkKind = Guid("cc110556-a091-4d38-9fec-25ab9a351a6a")
                let pdbPath = Path.ChangeExtension(AnalyzerProvenanceBuildFixture.output directory "Mini", ".pdb")
                use pdbStream = File.OpenRead pdbPath
                use provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(pdbStream)
                let metadata = provider.GetMetadataReader()
                let sourceLinks =
                    metadata.CustomDebugInformation
                    |> Seq.map metadata.GetCustomDebugInformation
                    |> Seq.filter (fun entry -> metadata.GetGuid(entry.Kind) = sourceLinkKind)
                    |> Seq.toList
                let sourceLink = Assert.Single sourceLinks
                Assert.Equal(System.Reflection.Metadata.HandleKind.ModuleDefinition, sourceLink.Parent.Kind)
                Assert.Equal<byte>(
                    File.ReadAllBytes(Path.Combine(directory, "obj", "links.json")),
                    metadata.GetBlobBytes(sourceLink.Value))

            let firstKey = AnalyzerProvenanceBuildFixture.key first "Mini"
            let secondKey = AnalyzerProvenanceBuildFixture.key second "Mini"
            Assert.True(firstKey.IsSome && secondKey.IsSome)
            Assert.Equal(firstKey, secondKey))

    [<Fact(Timeout = 300000)>]
    member _.``source link mutation without rebuilding refuses the old producer binding``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "sourcelink-binding" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None true None
            AnalyzerProvenanceBuildFixture.build root "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds
            Assert.True((AnalyzerProvenanceBuildFixture.key root "Mini").IsSome)

            let output = AnalyzerProvenanceBuildFixture.output root "Mini"
            let originalAssembly = File.ReadAllBytes output
            let links = Path.Combine(root, "obj", "links.json")
            let original = File.ReadAllText links
            let changed = original.Replace("example.invalid/source", "example.invalid/changed-source")
            Assert.NotEqual<string>(original, changed)
            File.WriteAllText(links, changed)

            Assert.Equal<byte>(originalAssembly, File.ReadAllBytes output)
            Assert.True((AnalyzerProvenanceBuildFixture.key root "Mini").IsNone,
                        "changed debug input must invalidate its local compiler binding until rebuilt"))

    [<Fact(Timeout = 300000)>]
    member _.``source link JSON also embedded as a resource remains a semantic analyzer input``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "sourcelink-resource" (fun root ->
            let directories = [ Path.Combine(root, "a"); Path.Combine(root, "a-longer-workspace") ]
            let keys =
                directories
                |> List.map (fun directory ->
                    AnalyzerProvenanceBuildFixture.prepare directory "Mini" "module MiniRules\nlet answer = 1" None true None
                    let projectPath = Path.Combine(directory, "Mini.fsproj")
                    let project = XElement.Load projectPath
                    let n = AnalyzerProvenanceBuildFixture.node
                    let a = AnalyzerProvenanceBuildFixture.attr
                    project.Add(
                        n "ItemGroup" [|
                            n "EmbeddedResource" [|
                                a "Include" "obj/links.json"
                                n "LogicalName" [| "rule-map.json" |] |] |])
                    project.Save projectPath
                    AnalyzerProvenanceBuildFixture.build directory "Mini" ""
                    |> AnalyzerProvenanceBuildFixture.succeeds

                    // Prove that these same bytes are an assembly resource a rule
                    // can read, not merely an authored project declaration.
                    let assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(AnalyzerProvenanceBuildFixture.output directory "Mini"))
                    use resource = assembly.GetManifestResourceStream("rule-map.json")
                    Assert.NotNull resource
                    use reader = new StreamReader(resource)
                    Assert.Equal(File.ReadAllText(Path.Combine(directory, "obj", "links.json")), reader.ReadToEnd())
                    let key = AnalyzerProvenanceBuildFixture.key directory "Mini"
                    Assert.True(key.IsSome, "the rebuilt dual-role producer must have valid provenance")
                    key)
            Assert.NotEqual(keys.[0], keys.[1]))

    [<Fact(Timeout = 300000)>]
    member _.``real producer external glob detects new linked sources without refusing unchanged membership``() =
        // Keep this synthetic producer in place on failure: absolute-path receipts
        // cannot be diagnosed faithfully after copying/deleting their inputs.
        let root = Path.Combine(Path.GetTempPath(), "fshw-external-glob-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let root = AnalyzerProvenanceBuildFixture.physicalDirectory (DirectoryInfo root)
        use ownership = AnalyzerProvenanceBuildFixture.ownProcesses ()
        (fun root ->
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
            if original.IsNone then
                let outputDirectory = AnalyzerProvenanceBuildFixture.output producer "Mini" |> Path.GetDirectoryName
                let diagnostic =
                    FsHotWatch.Analyzers.AnalyzerProvenance.trySnapshot
                        (FsHotWatch.Analyzers.AnalyzersPlugin.isKnownNonAnalyzerPrefix
                            FsHotWatch.Analyzers.AnalyzersPlugin.knownNonAnalyzerPrefixes)
                        [ outputDirectory ]
                match diagnostic with
                | Result.Error reason -> Assert.Fail($"Unchanged producer refused: {reason}; synthetic fixture retained at {root}")
                | Result.Ok _ -> Assert.Fail($"Public cache key refused despite valid provenance; synthetic fixture retained at {root}")
            Assert.True(original.IsSome)
            Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")
            File.WriteAllText(Path.Combine(shared, "NewRule.fs"), "module NewRule\nlet value = 2")
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone)

            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let rebuilt = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(rebuilt.IsSome)
            Assert.NotEqual(original, rebuilt)) root

    [<Fact(Timeout = 300000)>]
    member _.``incremental build cannot bless a replaced compiler output``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "replaced-output" (fun root ->
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
        AnalyzerProvenanceBuildFixture.withProducerDir "mid-build" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None

            AnalyzerProvenanceBuildFixture.build root "Mini" "-p:MutateAfterCompile=true"
            |> AnalyzerProvenanceBuildFixture.fails "Analyzer inputs changed while compiler was running"

            Assert.False(File.Exists(AnalyzerProvenanceBuildFixture.output root "Mini" + ".fshw-analyzer.xml")))

    [<Theory(Timeout = 300000)>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``copied direct and transitive project outputs retain semantic identity and local binding``(transitive: bool) =
        AnalyzerProvenanceBuildFixture.withProducerDir "project-copy-identity" (fun root ->
            let dependency, producer = AnalyzerProvenanceBuildFixture.prepareProjectChain root transitive
            let originalOutput = AnalyzerProvenanceBuildFixture.output dependency "Library"
            let copiedOutput = AnalyzerProvenanceBuildFixture.output producer "Library"
            Assert.True(File.Exists copiedOutput, "The actual build must copy the dependency implementation")
            Assert.True(File.ReadAllBytes originalOutput = File.ReadAllBytes copiedOutput)

            let original =
                AnalyzerProvenanceBuildFixture.singleOutputSnapshot dependency "Library"
                |> AnalyzerProvenanceBuildFixture.requireSnapshot

            let copied =
                AnalyzerProvenanceBuildFixture.singleOutputSnapshot producer "Library"
                |> AnalyzerProvenanceBuildFixture.requireSnapshot

            Assert.Equal(
                FsHotWatch.Analyzers.AnalyzerProvenance.semantic original,
                FsHotWatch.Analyzers.AnalyzerProvenance.semantic copied
            )
            Assert.NotEqual(
                FsHotWatch.Analyzers.AnalyzerProvenance.materialization original,
                FsHotWatch.Analyzers.AnalyzerProvenance.materialization copied
            )
            let baseline = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(baseline.IsSome, "All copied first-party outputs must be accountable through the public cache key")
            AnalyzerProvenanceBuildFixture.build producer "Mini" "" |> AnalyzerProvenanceBuildFixture.succeeds
            Assert.Equal(baseline, AnalyzerProvenanceBuildFixture.key producer "Mini"))

    [<Theory(Timeout = 300000)>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``dependency only rebuild cannot bless a stale analyzer copy``(transitive: bool) =
        AnalyzerProvenanceBuildFixture.withProducerDir "project-copy-rebuild" (fun root ->
            let dependency, producer = AnalyzerProvenanceBuildFixture.prepareProjectChain root transitive
            let baseline = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(baseline.IsSome)
            let copiedOutput = AnalyzerProvenanceBuildFixture.output producer "Library"
            let copiedBytes = File.ReadAllBytes copiedOutput
            File.WriteAllText(Path.Combine(dependency, "Rules.fs"), "module Library\nlet value = 2")
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone)

            AnalyzerProvenanceBuildFixture.build dependency "Library" "" |> AnalyzerProvenanceBuildFixture.succeeds
            Assert.True(copiedBytes = File.ReadAllBytes copiedOutput, "Building only Library must leave the consumer copy untouched")
            Assert.False(copiedBytes = File.ReadAllBytes(AnalyzerProvenanceBuildFixture.output dependency "Library"))
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone,
                        "A newly valid original dependency cannot attest the stale loaded copy")

            AnalyzerProvenanceBuildFixture.build producer "Mini" "" |> AnalyzerProvenanceBuildFixture.succeeds
            let rebuilt = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(rebuilt.IsSome)
            Assert.NotEqual(baseline, rebuilt)
            Assert.True(File.ReadAllBytes copiedOutput = File.ReadAllBytes(AnalyzerProvenanceBuildFixture.output dependency "Library")))

    [<Theory(Timeout = 300000)>]
    [<InlineData("output-bytes")>]
    [<InlineData("original-receipt")>]
    member _.``copied dependency refuses replaced bytes or an original path receipt``(replacement: string) =
        AnalyzerProvenanceBuildFixture.withProducerDir "project-copy-replacement" (fun root ->
            let dependency, producer = AnalyzerProvenanceBuildFixture.prepareProjectChain root false
            let baseline = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(baseline.IsSome)
            let copiedOutput = AnalyzerProvenanceBuildFixture.output producer "Library"
            let receipt = copiedOutput + ".fshw-analyzer.xml"
            let originalBytes = File.ReadAllBytes copiedOutput
            let originalReceipt = File.ReadAllBytes receipt

            match replacement with
            | "output-bytes" -> File.AppendAllText(copiedOutput, "changed local materialization")
            | _ -> File.Copy(AnalyzerProvenanceBuildFixture.output dependency "Library" + ".fshw-analyzer.xml", receipt, true)

            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone)
            File.WriteAllBytes(copiedOutput, originalBytes)
            File.WriteAllBytes(receipt, originalReceipt)
            Assert.Equal(baseline, AnalyzerProvenanceBuildFixture.key producer "Mini"))

    [<Fact(Timeout = 300000)>]
    member _.``producer refuses a dependency receipt naming another successful output``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "project-copy-foreign-receipt" (fun root ->
            let dependency, producer = AnalyzerProvenanceBuildFixture.prepareProjectChain root false
            let foreign = Path.Combine(root, "Foreign")
            AnalyzerProvenanceBuildFixture.prepare foreign "Library" "module Library\nlet value = 1" None false None
            AnalyzerProvenanceBuildFixture.build foreign "Library" "" |> AnalyzerProvenanceBuildFixture.succeeds
            // Establish the same global-property context used by the publication probe.
            AnalyzerProvenanceBuildFixture.build producer "Mini" "-t:Rebuild -p:BuildProjectReferences=false"
            |> AnalyzerProvenanceBuildFixture.succeeds
            let publication = "-t:_FshwPrepareAnalyzerProvenance,_FshwPublishAnalyzerProvenance -p:BuildProjectReferences=false"
            AnalyzerProvenanceBuildFixture.build producer "Mini" publication |> AnalyzerProvenanceBuildFixture.succeeds
            let foreignReceipt = AnalyzerProvenanceBuildFixture.output foreign "Library" + ".fshw-analyzer.xml"
            let dependencyReceipt = AnalyzerProvenanceBuildFixture.output dependency "Library" + ".fshw-analyzer.xml"
            File.Copy(foreignReceipt, dependencyReceipt, true)

            // Run the real producer publication boundary without rebuilding Library,
            // which would otherwise replace the deliberately substituted receipt.
            match AnalyzerProvenanceBuildFixture.build producer "Mini" publication with
            | Failed _ -> ()
            | other -> Assert.Fail($"Foreign dependency receipt was not refused: %A{other}"))

    [<Fact(Timeout = 300000)>]
    member _.``first party project dependencies have validated source provenance``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "project-dependency" (fun root ->
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

            let baseline = AnalyzerProvenanceBuildFixture.key producer "Mini"
            if baseline.IsNone then
                let diagnostic =
                    FsHotWatch.Analyzers.AnalyzerProvenance.trySnapshot
                        (FsHotWatch.Analyzers.AnalyzersPlugin.isKnownNonAnalyzerPrefix
                            FsHotWatch.Analyzers.AnalyzersPlugin.knownNonAnalyzerPrefixes)
                        [ AnalyzerProvenanceBuildFixture.output producer "Mini" |> Path.GetDirectoryName ]
                match diagnostic with
                | Result.Error reason -> Assert.Fail($"ProjectReference producer refused: {reason}; synthetic fixture retained at {root}")
                | Result.Ok _ -> Assert.Fail("Public ProjectReference key refused despite valid provenance")
            Assert.True(baseline.IsSome)
            File.AppendAllText(Path.Combine(dependency, "Rules.fs"), "\nlet added = 2")
            Assert.True((AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone))


    [<Theory(Timeout = 300000)>]
    [<InlineData("imported")>]
    [<InlineData("indirect")>]
    [<InlineData("initially-absent")>]
    [<InlineData("excluded-and-removed")>]
    [<InlineData("custom-global")>]
    member _.``evaluated membership preserves the producer source selection``(shape: string) =
        AnalyzerProvenanceBuildFixture.withProducerDir "evaluated-membership" (fun root ->
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

    [<Fact(Timeout = 300000)>]
    member _.``optional external import invalidates unchanged source membership``() =
        AnalyzerProvenanceBuildFixture.withProducerDir "optional-import" (fun root ->
            let producer = Path.Combine(root, "Producer")
            let imported = Path.Combine(root, "Rules.props")

            AnalyzerProvenanceBuildFixture.prepare
                producer
                "Mini"
                "module MiniRules\n#if OPTIONAL_RULES\nlet answer = 2\n#else\nlet answer = 1\n#endif\n"
                None
                false
                None

            let projectPath = Path.Combine(producer, "Mini.fsproj")
            let project = XElement.Load projectPath
            let n = AnalyzerProvenanceBuildFixture.node
            let a = AnalyzerProvenanceBuildFixture.attr

            project.Add(
                n
                    "Import"
                    [| a "Project" "../Rules.props"
                       a "Condition" "Exists('../Rules.props')" |]
            )

            project.Save projectPath
            Assert.False(File.Exists imported)
            AnalyzerProvenanceBuildFixture.build producer "Mini" ""
            |> AnalyzerProvenanceBuildFixture.succeeds

            let original = AnalyzerProvenanceBuildFixture.key producer "Mini"
            Assert.True(original.IsSome, "The actual producer must establish reusable baseline provenance")
            Assert.Equal(original, AnalyzerProvenanceBuildFixture.key producer "Mini")

            // Outside producer discovery: only the evaluated import set changes.
            // The existing Compile items and five effective context fields remain unchanged.
            let rules =
                n
                    "Project"
                    [| n
                           "PropertyGroup"
                           [| n "DefineConstants" [| "$(DefineConstants);OPTIONAL_RULES" |] |] |]

            rules.Save imported

            Assert.True(
                (AnalyzerProvenanceBuildFixture.key producer "Mini").IsNone,
                "A newly resolved external import changing compiler options must refuse the old analyzer receipt"
            ))

    [<Theory(Timeout = 300000)>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``current environment membership is observed without replaying environment values``(propertyFunction: bool) =
        AnalyzerProvenanceBuildFixture.withProducerDir "current-environment" (fun root ->
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
                    if propertyFunction then
                        project.Add(n "PropertyGroup" [|
                            n "CurrentRuleSelection" [| $"$([System.Environment]::GetEnvironmentVariable('{variable}'))" |]
                        |])
                        "$(CurrentRuleSelection)"
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
        AnalyzerProvenanceBuildFixture.withProducerDir "private-invocation" (fun root ->
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
        AnalyzerProvenanceBuildFixture.withProducerDir "global-escaping" (fun root ->
            AnalyzerProvenanceBuildFixture.prepare root "Mini" "module MiniRules\nlet answer = 1" None false None
            let selectedSource = Path.Combine(root, "Selected.fs")
            let selectsSource = true

            if selectsSource then
                File.WriteAllText(selectedSource, "module SelectedRules\nlet value = 2")
                let projectPath = Path.Combine(root, "Mini.fsproj")
                let project = XElement.Load projectPath
                let n = AnalyzerProvenanceBuildFixture.node
                let a = AnalyzerProvenanceBuildFixture.attr

                project.Add(
                    n
                        "ItemGroup"
                        [| n
                               "Compile"
                               [| a "Include" "Selected.fs"
                                  a "Condition" ("'$(RuleFlavor)' == '" + encoded + "'") |] |]
                )

                project.Save projectPath

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
            // IBuildEngine6 exposes escaped global values, as consumed by Project's constructor.
            Assert.True((actual = encoded), "Private context must preserve the SDK's escaped global representation")

            if selectsSource then
                let compiledSources =
                    receipt.Element(XName.Get "Inputs").Elements(XName.Get "File")
                    |> Seq.filter (fun file -> file.Attribute(XName.Get "key").Value.StartsWith("source:", StringComparison.Ordinal))
                    |> Seq.map (fun file -> file.Attribute(XName.Get "path").Value |> Path.GetFullPath)
                    |> Seq.toList

                Assert.Contains(Path.GetFullPath selectedSource, compiledSources)

            Assert.True((AnalyzerProvenanceBuildFixture.key root "Mini").IsSome))

    [<Theory(Timeout = 300000)>]
    [<InlineData("missing")>]
    [<InlineData("digest")>]
    [<InlineData("another-producer")>]
    member _.``missing or mismatched private invocation context refuses reuse``(damage: string) =
        AnalyzerProvenanceBuildFixture.withProducerDir "context-refusal" (fun root ->
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
        AnalyzerProvenanceBuildFixture.withProducerDir "publish-context-binding" (fun root ->
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
