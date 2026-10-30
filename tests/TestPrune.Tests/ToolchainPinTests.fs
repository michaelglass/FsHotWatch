module TestPrune.Tests.ToolchainPinTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote

// TestPrune's gate used to be a RELEASED fshotwatch.cli pinned in .config/dotnet-tools.json, and
// TestPrune.Analyzers had to be compiled against exactly the FCS that pinned host carried. Since
// the import into the FsHotWatch repository (AUTOMATION-1367) the gate is fshw built from this
// source (`dotnet run --project src/FsHotWatch.Cli -- confirm`), so the host's FCS is the one
// the FsHotWatch engine project references. The invariant is unchanged: FCS's assembly version
// carries the patch, so an analyzer compiled against any other patch fails to load in the gate.

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: no Directory.Packages.props in any ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private pinnedFcs (root: string) =
    let props = Xml.Linq.XDocument.Load(Path.Combine(root, "Directory.Packages.props"))

    props.Descendants(Xml.Linq.XName.Get "PackageVersion")
    |> Seq.filter (fun element -> element.Attribute(Xml.Linq.XName.Get "Include").Value = "FSharp.Compiler.Service")
    |> Seq.map (fun element -> element.Attribute(Xml.Linq.XName.Get "Version").Value)
    |> Seq.toList

/// The FSharp.Compiler.Service the gate's host loads: the engine project's reference. An
/// inline version (exact, `[x.y.z]`) when the project states one, otherwise the central pin.
let private hostFcsVersion (root: string) =
    let engine =
        File.ReadAllText(Path.Combine(root, "src", "FsHotWatch", "FsHotWatch.fsproj"))

    let inline' =
        Regex.Match(engine, "<PackageReference Include=\"FSharp.Compiler.Service\" Version=\"\\[?([^\\]\"]+)\\]?\"")

    if inline'.Success then
        inline'.Groups[1].Value
    else
        match pinnedFcs root with
        | [ version ] -> version
        | other -> failwith $"expected exactly one FSharp.Compiler.Service pin, found %A{other}"

[<Fact>]
let ``the gate runs fshw built from this source, not a released pin`` () =
    let root = repoRoot ()
    let mise = File.ReadAllText(Path.Combine(root, "mise.toml"))

    let workflow =
        File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))

    let fromSource =
        "dotnet run --project src/FsHotWatch.Cli --no-build -- confirm --run-once"

    test <@ mise.Contains fromSource @>
    test <@ workflow.Contains fromSource @>

    let tools = File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json"))

    test <@ not (tools.Contains "fshotwatch.cli") @>

[<Fact>]
let ``TestPrune.Analyzers is compiled against the gate host's exact FCS`` () =
    let root = repoRoot ()
    let hostFcs = hostFcsVersion root

    // An exact version, not a float or a range: a float re-resolves on every fresh restore.
    test <@ pinnedFcs root = [ hostFcs ] @>

    // What the host will actually be asked to load: the analyzer assembly's FCS reference.
    let referencedFcs =
        Reflection.Assembly.Load("TestPrune.Analyzers").GetReferencedAssemblies()
        |> Array.filter (fun name -> name.Name = "FSharp.Compiler.Service")
        |> Array.map (fun name -> name.Version.ToString(3))
        |> Array.toList

    test <@ referencedFcs = [ hostFcs ] @>
