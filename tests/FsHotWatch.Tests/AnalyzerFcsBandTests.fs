/// An analyzer compiled against another FSharp.Compiler.Service band than the host runs
/// must be refused at load (AUTOMATION-1706): loaded anyway, it crashed on every file it
/// analysed (MissingMethodException get_recordFields, FCS 43.13 vs 43.12), the crashes
/// were logged per file, and the analyzer silently produced nothing.
///
/// The fixture is the real ConfigCacheAnalyzer with its one FCS AssemblyRef row
/// rewritten (`retargetFcsReference`): that row is all a compiler
/// records of the FCS it built against, so no second fixture project with a second FCS
/// pin has to be restored and built.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AnalyzerFcsBandTests

open System
open System.Buffers.Binary
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Analyzers
open FsHotWatch.Tests.TestHelpers

/// The config-caching fixture analyzer, built through FsHotWatch.Tests' project
/// reference: a real analyzer the real load path registers.
let private fixtureDll =
    let dll =
        Path.Combine(
            RepoTasks.repoRoot (),
            "tests",
            "Fixtures",
            "ConfigCacheAnalyzer",
            "bin",
            "Debug",
            "net10.0",
            "ConfigCacheAnalyzer.dll"
        )

    if not (File.Exists dll) then
        failwith $"fixture analyzer not built at %s{dll}: build tests/FsHotWatch.Tests"

    dll

[<Literal>]
let private FcsAssemblyName = "FSharp.Compiler.Service"

/// The FSharp.Compiler.Service version `dll`'s metadata says it was compiled against.
let private fcsReferenceIn (dll: string) : Version =
    use stream = File.OpenRead dll
    use pe = new PEReader(stream)
    let reader = pe.GetMetadataReader()

    reader.AssemblyReferences
    |> Seq.map reader.GetAssemblyReference
    |> Seq.find (fun reference -> reader.GetString reference.Name = FcsAssemblyName)
    |> _.Version

/// Make `dll` an analyzer COMPILED AGAINST FSharp.Compiler.Service `version`: the four
/// version columns of its one FCS AssemblyRef row, rewritten in place. That row is all a
/// compiler records of the FCS it built against. Anything but exactly one FCS reference
/// is a fixture that no longer describes an analyzer, and fails loudly; so does a patch
/// that does not read back.
let private retargetFcsReference (dll: string) (version: Version) =
    let bytes = File.ReadAllBytes dll

    let rowOffset =
        use pe = new PEReader(ImmutableArray.Create<byte>(bytes))
        let reader = pe.GetMetadataReader()

        let fcsRows =
            reader.AssemblyReferences
            |> Seq.filter (fun handle -> reader.GetString (reader.GetAssemblyReference handle).Name = FcsAssemblyName)
            |> Seq.toList

        match fcsRows with
        | [ handle ] ->
            let entity: EntityHandle = AssemblyReferenceHandle.op_Implicit handle
            let row = MetadataTokens.GetRowNumber entity

            pe.PEHeaders.MetadataStartOffset
            + MetadataReaderExtensions.GetTableMetadataOffset(reader, TableIndex.AssemblyRef)
            + (row - 1)
              * MetadataReaderExtensions.GetTableRowSize(reader, TableIndex.AssemblyRef)
        | rows -> failwith $"%s{dll}: expected one %s{FcsAssemblyName} reference, found %d{List.length rows}"

    // AssemblyRef columns 1-4: MajorVersion, MinorVersion, BuildNumber, RevisionNumber (u16 LE).
    [ version.Major; version.Minor; version.Build; version.Revision ]
    |> List.iteri (fun column value ->
        BinaryPrimitives.WriteUInt16LittleEndian(Span<byte>(bytes, rowOffset + 2 * column, 2), uint16 value))

    File.WriteAllBytes(dll, bytes)

    if fcsReferenceIn dll <> version then
        failwith $"%s{dll}: the %s{FcsAssemblyName} reference did not take version %O{version}"

/// The FCS this test host runs, as its AssemblyVersion carries it (e.g. 43.12.402.0).
let private hostFcs = typeof<FSharp.Compiler.Text.Range>.Assembly.GetName().Version

/// The incident's shape: analyzers built one band BELOW the host (43.12 under a 43.13 host).
let private olderBand = Version(hostFcs.Major, hostFcs.Minor - 1, 400, 0)

/// The fixture's file name: it matches `*Analyzer*.dll` and holds no "test".
[<Literal>]
let private FixtureName = "FcsBandAnalyzer.dll"

/// A fresh analyzer directory under `root` holding a copy of the fixture analyzer.
let private fixtureCopy (root: string) : string =
    let dir = Path.Combine(root, "bins", Path.GetFileNameWithoutExtension FixtureName)
    Directory.CreateDirectory dir |> ignore
    File.Copy(fixtureDll, Path.Combine(dir, FixtureName))
    dir

let private projectOptions: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions =
    { ProjectFileName = "/my/repo/Fixture.fsproj"
      ProjectId = None
      SourceFiles = [||]
      OtherOptions = [||]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// A handler over `dirs` in a repository at `root`, registered on a host.
let private registered (root: string) (dirs: string list) =
    let host = createModelHost (Unchecked.defaultof<_>) root
    // Error keeps the fixture's Warning unpromoted, so its text is untouched.
    let handler = AnalyzersPlugin.create (Some root) dirs None DiagnosticSeverity.Error
    host.RegisterHandler handler
    handler, host

/// Check `name` (under `root/src`) once, as version `version`, and wait for the plugins to drain.
let private check (host: FsHotWatch.PluginHost.PluginHost) (root: string) (name: string) (version: int64) =
    let file = Path.Combine(root, "src", name)
    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
    File.WriteAllText(file, "module Subject\n")

    host.EmitFileChecked
        { fakeFileCheckResult file with
            Source = "module Subject\n"
            ProjectOptions = projectOptions
            Version = version }

    waitForQuiescent host 15000

/// Every entry the analyzers plugin holds in the ledger, across every key.
let private analyzerEntries (host: FsHotWatch.PluginHost.PluginHost) : ErrorEntry list =
    host.GetErrorsByPlugin "analyzers" |> Map.toList |> List.collect snd

let private isFailed (host: FsHotWatch.PluginHost.PluginHost) =
    match host.GetStatus "analyzers" with
    | Some(FsHotWatch.Events.Failed _) -> true
    | _ -> false

[<Fact(Timeout = 60000)>]
let ``an analyzer compiled against another FCS band is refused once, and the run is red naming both versions`` () =
    withTempDir "az-fcs-refused" (fun root ->
        let dir = fixtureCopy root
        let dll = Path.Combine(dir, FixtureName)
        retargetFcsReference dll olderBand
        let handler, host = registered root [ dir ]

        // Refused at load: nothing from the set runs.
        test <@ handler.Init.LoadedCount = 0 @>

        // Two files: the refusal is one fact about the set, not one crash per file.
        check host root "A.fs" 1L
        check host root "B.fs" 2L

        let entries = analyzerEntries host
        test <@ List.length entries = 1 @>

        let refusal = List.exactlyOne entries
        test <@ refusal.Severity = DiagnosticSeverity.Error @>
        test <@ refusal.Message.Contains dll @>
        test <@ refusal.Message.Contains(string olderBand) @>
        test <@ refusal.Message.Contains(string hostFcs) @>
        test <@ isFailed host @>)

[<Fact(Timeout = 60000)>]
let ``an analyzer compiled against the host's FCS band, at another patch, still loads and runs`` () =
    // The band, not the exact version: FSharp.Analyzers.SDK 0.39.2 references FCS
    // 43.12.400 and runs under fshw's 43.12.402. An exact rule would refuse that.
    withTempDir "az-fcs-band" (fun root ->
        let dir = fixtureCopy root
        retargetFcsReference (Path.Combine(dir, FixtureName)) (Version(hostFcs.Major, hostFcs.Minor, 0, 0))
        let handler, host = registered root [ dir ]

        test <@ handler.Init.LoadedCount >= 1 @>

        check host root "A.fs" 1L

        let entries = analyzerEntries host

        test
            <@
                entries
                |> List.exists (fun entry -> entry.Message.StartsWith("context=", StringComparison.Ordinal))
            @>

        test
            <@
                not (
                    entries
                    |> List.exists (fun entry -> entry.Message.Contains "FSharp.Compiler.Service")
                )
            @>

        test <@ not (isFailed host) @>)

[<Fact(Timeout = 60000)>]
let ``a refused set stays one refusal across a config edit, and a rebuild against the host's band lifts it`` () =
    withTempDir "az-fcs-lifted" (fun root ->
        let dir = fixtureCopy root
        let dll = Path.Combine(dir, FixtureName)
        retargetFcsReference dll olderBand
        File.WriteAllText(Path.Combine(root, ".editorconfig"), "one")
        let _, host = registered root [ dir ]

        check host root "A.fs" 1L
        // A config the analyzers read changes: a refused set has nothing loaded to reload.
        File.WriteAllText(Path.Combine(root, ".editorconfig"), "two")
        check host root "B.fs" 2L

        test <@ List.length (analyzerEntries host) = 1 @>
        test <@ isFailed host @>

        // The analyzer is rebuilt against the host's FCS: the set loads, the refusal goes.
        File.Copy(fixtureDll, dll, true)
        check host root "C.fs" 3L

        let entries = analyzerEntries host

        test
            <@
                not (
                    entries
                    |> List.exists (fun entry -> entry.Message.Contains "FSharp.Compiler.Service")
                )
            @>

        test
            <@
                entries
                |> List.exists (fun entry -> entry.Message.StartsWith("context=", StringComparison.Ordinal))
            @>

        test <@ not (isFailed host) @>)

[<Fact(Timeout = 60000)>]
let ``a refused set has no cache key: nothing it did not analyse is filed or replayed`` () =
    withTempDir "az-fcs-key" (fun root ->
        let dir = fixtureCopy root
        retargetFcsReference (Path.Combine(dir, FixtureName)) olderBand
        let handler, _ = registered root [ dir ]
        let file = Path.Combine(root, "src", "A.fs")

        let key =
            (handler.CacheKey.Value handler.Init) (
                FsHotWatch.Events.FileChecked
                    { fakeFileCheckResult file with
                        ProjectOptions = projectOptions }
            )

        test <@ key = None @>)

[<Fact(Timeout = 60000)>]
let ``a path reloaded after its analyzer was rebuilt against another band refuses the whole set`` () =
    withTempDir "az-fcs-reload" (fun root ->
        let dir = fixtureCopy root
        let dll = Path.Combine(dir, FixtureName)
        let set = AnalyzerLoad.loadSet hostFcs (fun _ -> false) [ dir ]
        test <@ (AnalyzerLoad.loadedPaths set |> List.sumBy _.Count) >= 1 @>

        retargetFcsReference dll olderBand
        let reload = AnalyzerLoad.reloadWhere hostFcs (fun _ -> false) (fun _ -> true) set
        reload.Retired |> List.iter AnalyzerLoad.unload

        test <@ List.length reload.Retired = 1 @>
        test <@ reload.Reloaded = 0 @>

        match reload.Set with
        | AnalyzerLoad.AnalyzerSet.RefusedFcsMismatch(only, []) ->
            test
                <@
                    only = { AnalyzerLoad.Assembly = dll
                             Built = olderBand
                             Host = hostFcs }
                @>
        | other -> failwith $"expected one refusal, got %A{other}")

[<Fact(Timeout = 15000)>]
let ``the band is major.minor: a patch apart passes, a minor or a major apart is refused, the SDK included`` () =
    let host = Version(43, 13, 101, 0)

    let mismatches =
        AnalyzerLoad.fcsMismatches
            host
            [ "FSharp.Analyzers.SDK", Version(43, 12, 400, 0)
              "/bins/Patch.dll", Version(43, 13, 404, 0)
              "/bins/Major.dll", Version(44, 13, 101, 0)
              "/bins/Exact.dll", host ]

    test
        <@
            mismatches = [ { AnalyzerLoad.Assembly = "FSharp.Analyzers.SDK"
                             Built = Version(43, 12, 400, 0)
                             Host = host }
                           { AnalyzerLoad.Assembly = "/bins/Major.dll"
                             Built = Version(44, 13, 101, 0)
                             Host = host } ]
        @>

[<Fact(Timeout = 15000)>]
let ``the refusal names every refused assembly with its FCS and the host's, and the band to rebuild against`` () =
    let host = Version(43, 13, 101, 0)

    let message =
        AnalyzerLoad.describeFcsRefusal
            { AnalyzerLoad.Assembly = "/bins/A.dll"
              Built = Version(43, 12, 400, 0)
              Host = host }
            [ { AnalyzerLoad.Assembly = "FSharp.Analyzers.SDK"
                Built = Version(43, 12, 402, 0)
                Host = host } ]

    for named in
        [ "/bins/A.dll"
          "43.12.400.0"
          "FSharp.Analyzers.SDK"
          "43.12.402.0"
          "43.13.101.0"
          "43.13" ] do
        test <@ message.Contains named @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the FCS reference is read from metadata, and a file with none references nothing`` () =
    withTempDir "az-fcs-read" (fun root ->
        let dll = Path.Combine(fixtureCopy root, FixtureName)
        test <@ AnalyzerLoad.fcsReferenceOf dll = Some(fcsReferenceIn fixtureDll) @>

        // A managed assembly that references no FCS (xUnit's) has nothing to be out of band.
        test <@ AnalyzerLoad.fcsReferenceOf typeof<FactAttribute>.Assembly.Location = None @>

        // Not a PE image at all.
        let junk = Path.Combine(root, "JunkAnalyzer.dll")
        File.WriteAllBytes(junk, [| 0uy; 1uy; 2uy |])
        test <@ AnalyzerLoad.fcsReferenceOf junk = None @>

        // A PE image with no managed metadata (a native DLL): its CLI header directory zeroed.
        let native = Path.Combine(root, "NativeAnalyzer.dll")
        let bytes = File.ReadAllBytes fixtureDll

        let cliDirectory =
            use pe = new PEReader(ImmutableArray.Create<byte>(bytes))
            let headers = pe.PEHeaders
            // Data directories follow the optional header's fixed fields; the CLI header is entry 14.
            let fixedFields =
                if headers.PEHeader.Magic = PEMagic.PE32Plus then
                    112
                else
                    96

            headers.PEHeaderStartOffset + fixedFields + 14 * 8

        Array.fill bytes cliDirectory 8 0uy
        File.WriteAllBytes(native, bytes)
        test <@ AnalyzerLoad.fcsReferenceOf native = None @>)
