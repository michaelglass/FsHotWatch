module TestPrune.Tests.ToolchainPinTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: no .config/dotnet-tools.json in any ancestor"
        elif File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

[<Theory>]
// Change this one parameter only after the behaviour this repo's gate depends on
// has a real CLI release. alpha.45 was the first pin whose bundled TestPrune plugin
// selects the CTRF report flag by runner family, which is what lets the gate drive
// an xunit.v3 4.0.0 runner; the xunit-3-only flag made a 4.0.0 host exit 5 and
// report zero tests rather than fail.
[<InlineData("0.14.0-alpha.123")>]
let ``FsHotWatch gate uses the reviewed released pin`` (expectedReleasedVersion: string) =
    let root = repoRoot ()

    use manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")))

    let tool = manifest.RootElement.GetProperty("tools").GetProperty("fshotwatch.cli")
    let pinnedVersion = tool.GetProperty("version").GetString()
    let rollsForward = tool.GetProperty("rollForward").GetBoolean()
    test <@ pinnedVersion = expectedReleasedVersion @>
    test <@ rollsForward = false @>

    let commands =
        tool.GetProperty("commands").EnumerateArray()
        |> Seq.map (fun command -> command.GetString())
        |> Seq.toList

    test <@ commands = [ "fshw" ] @>

    let mise = File.ReadAllText(Path.Combine(root, "mise.toml"))

    let workflow =
        File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))

    test <@ mise.Contains("dotnet fshw check --run-once") @>
    test <@ mise.Contains("dotnet fshw confirm --run-once") @>
    test <@ workflow.Contains("dotnet fshw confirm --run-once") @>

/// The NuGet global-packages folder `dotnet tool restore` installs the pinned CLI into.
let private nugetPackagesRoot () =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
    | configured -> configured

/// The FSharp.Compiler.Service version the pinned fshw host loads, read from the restored
/// CLI's own deps.json. The host supplies FCS to every analyzer it loads, so this is the
/// only FCS version TestPrune.Analyzers can bind to inside the gate.
let private hostFcsVersion (root: string) =
    use manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")))

    let cliVersion =
        manifest.RootElement.GetProperty("tools").GetProperty("fshotwatch.cli").GetProperty("version").GetString()

    let toolsDir =
        Path.Combine(nugetPackagesRoot (), "fshotwatch.cli", cliVersion, "tools")

    if not (Directory.Exists toolsDir) then
        failwith $"fshotwatch.cli {cliVersion} is not restored at {toolsDir}; run `dotnet tool restore`"

    let depsFiles =
        Directory.GetFiles(toolsDir, "FsHotWatch.Cli.deps.json", SearchOption.AllDirectories)

    test <@ depsFiles.Length = 1 @>
    use deps = JsonDocument.Parse(File.ReadAllText depsFiles[0])

    let fcsVersions =
        deps.RootElement.GetProperty("targets").EnumerateObject()
        |> Seq.collect (fun target -> target.Value.EnumerateObject())
        |> Seq.map (fun library -> library.Name)
        |> Seq.filter (fun name -> name.StartsWith("FSharp.Compiler.Service/", StringComparison.Ordinal))
        |> Seq.map (fun name -> name.Substring("FSharp.Compiler.Service/".Length))
        |> Seq.distinct
        |> Seq.toList

    test <@ fcsVersions.Length = 1 @>
    fcsVersions.Head

/// A floating `43.*` restored FCS 43.12.402 while the pinned fshw host
/// carried 43.12.401, so the gate's analyzer stage failed to load TestPrune.Analyzers on
/// every file. FCS's assembly version carries the patch, so the analyzer must be compiled
/// against EXACTLY the host's FCS. A fshw pin bump therefore moves this pin with it.
[<Fact>]
let ``TestPrune.Analyzers is compiled against the pinned fshw host's exact FCS`` () =
    let root = repoRoot ()
    let hostFcs = hostFcsVersion root

    let props = Xml.Linq.XDocument.Load(Path.Combine(root, "Directory.Packages.props"))

    let declaredFcs =
        props.Descendants(Xml.Linq.XName.Get "PackageVersion")
        |> Seq.filter (fun element -> element.Attribute(Xml.Linq.XName.Get "Include").Value = "FSharp.Compiler.Service")
        |> Seq.map (fun element -> element.Attribute(Xml.Linq.XName.Get "Version").Value)
        |> Seq.toList

    // An exact version, not a float or a range: a float re-resolves on every fresh restore.
    test <@ declaredFcs = [ hostFcs ] @>

    // What the host will actually be asked to load: the analyzer assembly's FCS reference.
    let referencedFcs =
        Reflection.Assembly.Load("TestPrune.Analyzers").GetReferencedAssemblies()
        |> Array.filter (fun name -> name.Name = "FSharp.Compiler.Service")
        |> Array.map (fun name -> name.Version.ToString(3))
        |> Array.toList

    test <@ referencedFcs = [ hostFcs ] @>
