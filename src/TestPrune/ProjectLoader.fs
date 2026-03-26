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
