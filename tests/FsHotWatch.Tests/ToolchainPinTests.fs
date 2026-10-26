/// FSharp.Compiler.Service and FSharp.Core are pinned EXACTLY, and the CLI bundles
/// exactly the pinned versions.
///
/// They used to float (`43.*`, `10.1.*`), so a restore picked up whatever had been
/// published most recently. That is not harmless for a host that loads other people's
/// analyzers: every consumer analyzer (FSharp.Analyzers.SDK pins its FCS exactly) is
/// compiled against one FCS, and a CLI that silently moved to a newer one fails to load
/// them. The float turned 43.12.402 into 43.12.403 between two releases with no source
/// change at all. Move these pins deliberately, together, and only when the analyzer
/// ecosystem the CLI hosts has moved too.
module FsHotWatch.Tests.ToolchainPinTests

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private fcsVersion = "43.12.402"
let private fsharpCoreVersion = "10.1.402"

let private pinnedPackages =
    Map [ "FSharp.Compiler.Service", fcsVersion; "FSharp.Core", fsharpCoreVersion ]

/// Every `PackageReference` to a pinned package, as (project, package, Version attribute).
let private pinnedReferences (root: string) (dir: string) =
    FsHotWatch.Tests.TestHelpers.filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.fsproj" (Path.Combine(root, dir))
    |> List.toArray
    |> Array.collect (fun path ->
        XDocument.Load(path).Descendants(XName.Get "PackageReference")
        |> Seq.choose (fun reference ->
            let attr (name: string) =
                reference.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value

            match attr "Include" |> Option.orElse (attr "Update") with
            | Some package when pinnedPackages.ContainsKey package ->
                Some(Path.GetRelativePath(root, path), package, attr "Version" |> Option.defaultValue "")
            | _ -> None)
        |> Seq.toArray)

[<Fact>]
let ``every shipped project pins FCS and FSharp.Core exactly`` () =
    let root = repoRoot ()
    let references = pinnedReferences root "src"
    // FsHotWatch itself and the analyzer host plugin both name FCS directly.
    let fcsProjects =
        references
        |> Array.filter (fun (_, package, _) -> package = "FSharp.Compiler.Service")
        |> Array.length

    test <@ fcsProjects >= 2 @>

    let wrong =
        references
        |> Array.filter (fun (_, package, version) -> version <> $"[%s{pinnedPackages[package]}]")

    test <@ Array.isEmpty wrong @>

[<Fact>]
let ``no project in the repository floats FCS or FSharp.Core`` () =
    let root = repoRoot ()

    let floating =
        [| "src"; "tests"; "bench"; "examples"; "analyzers" |]
        |> Array.filter (fun dir -> Directory.Exists(Path.Combine(root, dir)))
        |> Array.collect (pinnedReferences root)
        |> Array.filter (fun (_, _, version) -> version.Contains "*")

    test <@ Array.isEmpty floating @>

[<Fact>]
let ``the CLI bundles exactly the pinned FCS and FSharp.Core`` () =
    // The CLI is a project reference of this test project, so its deps.json is copied
    // beside the test binary: the same resolution `dotnet pack` puts in the tool package.
    let depsJson = Path.Combine(AppContext.BaseDirectory, "FsHotWatch.Cli.deps.json")
    test <@ File.Exists depsJson @>

    use deps = JsonDocument.Parse(File.ReadAllText depsJson)

    let bundled =
        deps.RootElement.GetProperty("libraries").EnumerateObject()
        |> Seq.map _.Name
        |> Seq.choose (fun name ->
            match name.Split('/') with
            | [| package; version |] when pinnedPackages.ContainsKey package -> Some(package, version)
            | _ -> None)
        |> Seq.sort
        |> Seq.toList

    let expected = pinnedPackages |> Map.toList |> List.sort
    test <@ bundled = expected @>
