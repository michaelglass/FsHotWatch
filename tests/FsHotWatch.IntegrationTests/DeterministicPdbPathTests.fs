/// A shipped assembly must not name a pdb path that exists on the machine that packed it.
///
/// Every assembly's CodeView debug entry records the path its pdb was written to, and a
/// coverage collector that finds no pdb beside an assembly falls back to that path. A
/// non-deterministic Release build records `<checkout>/src/<Project>/obj/Release/<tfm>/<Project>.pdb`.
/// When a consumer builds on the same machine that packed the package, as a local release
/// canary does, the collector finds that pdb, instruments the package, and writes the
/// packing checkout's absolute source paths into the consumer's coverage report.
/// TestPrune.Core 16.1.0 did exactly that: intelligence's coverage gate refused the report
/// because 475 of its files lay outside the repository. A deterministic build records `/_/…`,
/// which resolves nowhere.
module FsHotWatch.Tests.DeterministicPdbPathTests

open System.Diagnostics
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: FsHotWatch.slnx is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "FsHotWatch.slnx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(System.AppContext.BaseDirectory))

/// The pdb paths an assembly's CodeView debug entries name.
let private codeViewPaths (assembly: string) =
    use stream = File.OpenRead assembly
    use reader = new PEReader(stream)

    reader.ReadDebugDirectory()
    |> Seq.filter (fun entry -> entry.Type = DebugDirectoryEntryType.CodeView)
    |> Seq.map (fun entry -> reader.ReadCodeViewDebugDirectoryData(entry).Path)
    |> Seq.toList

/// The source document paths a portable pdb records.
let private documentPaths (pdb: string) =
    use stream = File.OpenRead pdb
    use provider = MetadataReaderProvider.FromPortablePdbStream stream
    let reader = provider.GetMetadataReader()

    reader.Documents
    |> Seq.map (fun handle -> reader.GetString(reader.GetDocument(handle).Name))
    |> Seq.toList

let private releaseBuild (root: string) (project: string) =
    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true

    [ "build"; project; "-c"; "Release"; "--disable-build-servers"; "-nologo" ]
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

[<Fact>]
let ``a Release build of a shipped project names its pdb by a deterministic path`` () =
    let root = repoRoot ()
    let projectDir = Path.Combine(root, "src", "TestPrune.Attributes")

    // A path-map change does not invalidate an incremental compile, so an earlier Release
    // build's assemblies would answer for this one. Release output is used by nothing else.
    for output in [ "bin"; "obj" ] do
        let directory = Path.Combine(projectDir, output, "Release")

        if Directory.Exists directory then
            Directory.Delete(directory, true)

    let exitCode, output =
        releaseBuild root (Path.Combine(projectDir, "TestPrune.Attributes.fsproj"))

    test <@ (exitCode, if exitCode = 0 then "" else output) = (0, "") @>

    // Named rather than discovered, so a target framework that stops building fails here
    // instead of leaving the assertions below with less to check.
    let assemblies =
        [ "net10.0"; "netstandard2.0" ]
        |> List.map (fun tfm -> Path.Combine(projectDir, "bin", "Release", tfm, "TestPrune.Attributes.dll"))

    // The exact form, so a change in how the root is mapped cannot drift past this test.
    // The SDK maps `<checkout>/` to `/_/`; the F# compiler keeps the separator after the
    // stripped prefix, hence `/_//`. GitHub CI builds with `.git` present, so SourceLink
    // supplies the root there and this test pins that both paths produce the same form.
    let expected =
        assemblies
        |> List.map (fun assembly ->
            let tfm = Path.GetFileName(Path.GetDirectoryName assembly)
            assembly, [ $"/_//src/TestPrune.Attributes/obj/Release/%s{tfm}/TestPrune.Attributes.pdb" ])

    let actual =
        assemblies |> List.map (fun assembly -> assembly, codeViewPaths assembly)

    test <@ actual = expected @>

    // The pdb's source documents carry the same mapping. These are the paths a collector
    // writes into a coverage report, and none may name the checkout.
    let documents =
        assemblies
        |> List.collect (fun assembly -> documentPaths (Path.ChangeExtension(assembly, ".pdb")))
        |> List.distinct

    test <@ List.contains "/_//src/TestPrune.Attributes/Attributes.fs" documents @>

    test
        <@
            documents
            |> List.forall (fun path -> path.StartsWith "/_//src/TestPrune.Attributes/")
        @>
