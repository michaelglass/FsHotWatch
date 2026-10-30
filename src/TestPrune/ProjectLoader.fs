module TestPrune.ProjectLoader

open System
open System.IO
open System.Xml.Linq
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open Ionide.ProjInfo.Types

/// Lazily initialized MSBuild tooling path (shared across all project loads).
let internal toolsPath =
    lazy (Init.init (DirectoryInfo(Directory.GetCurrentDirectory())) None)

/// Lock for MSBuild project loading — only one loadProject call at a time.
/// Ionide.ProjInfo's WorkspaceLoader retries infinitely with a 50ms sleep when
/// MSBuild returns "a build is already in progress" (process-global lock).
let internal msbuildLock = obj ()

/// Parse a .fsproj file to extract compile items (in order) and project references.
let parseProjectFile (fsprojPath: string) =
    let doc = XDocument.Load(fsprojPath)
    let projDir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath))

    let compileItems =
        doc.Descendants(XName.Get "Compile")
        |> Seq.choose (fun el ->
            let inc = el.Attribute(XName.Get "Include")

            if inc <> null then
                Some(Path.GetFullPath(Path.Combine(projDir, inc.Value)))
            else
                None)
        |> Seq.toList

    let projectRefs =
        doc.Descendants(XName.Get "ProjectReference")
        |> Seq.choose (fun el ->
            let inc = el.Attribute(XName.Get "Include")

            if inc <> null then
                Some(Path.GetFullPath(Path.Combine(projDir, inc.Value)))
            else
                None)
        |> Seq.toList

    compileItems, projectRefs

/// Read every `<PackageVersion Include="X" Version="Y" />` from a
/// `Directory.Packages.props` file (Central Package Management). Returns a
/// name → version map. Missing/unreadable file → empty map.
let private readCentralPackageVersions (propsPath: string) : Map<string, string> =
    if not (File.Exists propsPath) then
        Map.empty
    else
        try
            let doc = XDocument.Load(propsPath)

            doc.Descendants(XName.Get "PackageVersion")
            |> Seq.choose (fun el ->
                let inc = el.Attribute(XName.Get "Include")
                let ver = el.Attribute(XName.Get "Version")

                if inc <> null && ver <> null then
                    Some(inc.Value, ver.Value)
                else
                    None)
            |> Map.ofSeq
        with _ ->
            Map.empty

/// Walk from a project's directory up to the filesystem root collecting the
/// resolved CPM package versions from any `Directory.Packages.props` found.
/// Nearer files win (MSBuild's import order), so we fold from root → leaf and let
/// closer entries overwrite. Returns the merged name → version map.
let private centralPackageVersionsFor (fsprojPath: string) : Map<string, string> =
    let rec ancestors (dir: string) =
        if String.IsNullOrEmpty dir then
            []
        else
            let parent = Path.GetDirectoryName dir

            dir
            :: (if parent = dir || String.IsNullOrEmpty parent then
                    []
                else
                    ancestors parent)

    let projDir = Path.GetDirectoryName(Path.GetFullPath fsprojPath)

    // root → leaf so the closest Directory.Packages.props wins on overwrite.
    ancestors projDir
    |> List.rev
    |> List.fold
        (fun acc dir ->
            let props =
                readCentralPackageVersions (Path.Combine(dir, "Directory.Packages.props"))

            props |> Map.fold (fun m k v -> Map.add k v m) acc)
        Map.empty

/// Extract a project's `<PackageReference>` set as (packageName, resolvedVersion)
/// pairs. Handles BOTH styles:
///   • direct `<PackageReference Include="X" Version="Y" />` (version inline)
///   • CPM `<PackageReference Include="X" />` whose version lives in an ancestor
///     `Directory.Packages.props` as `<PackageVersion Include="X" Version="Y" />`
/// A reference whose version cannot be resolved is emitted with the version
/// `"unresolved"` — still deterministic, and a later resolution flips the
/// fingerprint exactly once. Pairs are sorted by package name for stability.
let parsePackageReferences (fsprojPath: string) : (string * string) list =
    let doc = XDocument.Load(fsprojPath)
    let cpm = centralPackageVersionsFor fsprojPath

    doc.Descendants(XName.Get "PackageReference")
    |> Seq.choose (fun el ->
        let inc = el.Attribute(XName.Get "Include")

        if inc = null then
            None
        else
            let name = inc.Value
            let ver = el.Attribute(XName.Get "Version")

            let version =
                if ver <> null && not (String.IsNullOrWhiteSpace ver.Value) then
                    ver.Value
                else
                    cpm |> Map.tryFind name |> Option.defaultValue "unresolved"

            Some(name, version))
    |> Seq.distinct
    |> Seq.sortBy fst
    |> Seq.toList

/// Load FSharpProjectOptions via Ionide.ProjInfo's MSBuild design-time build,
/// matching the approach used by InProcessLint for full type resolution.
let getProjectOptions (_checker: FSharpChecker) (fsprojPath: string) : FSharpProjectOptions =
    let project = Path.GetFullPath fsprojPath
    let tp = toolsPath.Value

    let allLoaded, opts =
        lock msbuildLock (fun () ->
            let loader = WorkspaceLoader.Create(tp)
            let allLoaded = loader.LoadProjects [ project ] |> Seq.toList

            let projOpts =
                allLoaded
                |> List.tryFind (fun p -> p.ProjectFileName = project)
                |> Option.defaultWith (fun () -> failwith $"Ionide.ProjInfo failed to load project: %s{project}")

            allLoaded, projOpts)

    Ionide.ProjInfo.FCS.mapToFSharpProjectOptions opts allLoaded
