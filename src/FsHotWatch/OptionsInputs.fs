/// What an MSBuild evaluation of some projects READ (ADR-059 §18, AUTOMATION-1372), so the
/// options it produced can be keyed on exactly that.
///
/// Each project is evaluated again here, the way Ionide evaluates it (its global properties,
/// with and without the target framework), through a file system that records every call
/// MSBuild makes: each file it opens, each path it asks about (an `Exists` condition, an
/// import it skipped), and each directory it lists (a wildcard import, an item glob such as
/// the SDK's default `**/*.resx`). To those it adds what the evaluation reads without asking
/// the file system: every resolved import's bytes (the project XML cache opens them
/// directly), each project's package assets (read by the design-time build, not the
/// evaluation), and the `Directory.Build.*`, `Directory.Packages.props` and `global.json`
/// that MSBuild finds by walking up from the project. Compile items the design-time build
/// generates (under `obj/`) are read too: a replay does not regenerate one a clean removed.
/// The variables the evaluation can read as properties are every `$(Name)` its files mention.
///
/// This module is the only one that names MSBuild's own types. MSBuild is the SDK's,
/// located by Ionide (`Init.init`) before anything here runs, and fshw compiles against the
/// SDK's assembly without shipping it, as Ionide does.
module FsHotWatch.OptionsInputs

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Microsoft.Build.Evaluation
open Microsoft.Build.Evaluation.Context
open Microsoft.Build.FileSystem
open Ionide.ProjInfo
open FsHotWatch.OptionsRecords

/// The files MSBuild looks for above a project, by name.
let wellKnownAbove =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      "global.json" ]

let private propertyReference =
    Regex(@"\$\(\s*([A-Za-z_][A-Za-z0-9_\-]*)", RegexOptions.Compiled)

let private environmentRead =
    Regex(
        @"GetEnvironmentVariable\(\s*['""]?([A-Za-z_][A-Za-z0-9_\-]*)",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase
    )

/// The names a file's text can read from the environment: every property it mentions, and
/// every variable it asks for by name.
let environmentNamesIn (text: string) : string list =
    [ for m in propertyReference.Matches text -> m.Groups[1].Value
      for m in environmentRead.Matches text -> m.Groups[1].Value ]

let private fullPath (path: string) =
    if Path.IsPathRooted path then
        Path.GetFullPath path
    else
        path

/// Passes every call to the real file system, recording what was asked.
type internal RecordingFileSystem() =
    inherit MSBuildFileSystemBase()

    let reads = ConcurrentDictionary<string, unit>()
    let probes = ConcurrentDictionary<string, unit>()
    let listings = ConcurrentDictionary<Listing, unit>()

    let read (path: string) =
        reads.TryAdd(fullPath path, ()) |> ignore

    let probe (path: string) =
        probes.TryAdd(fullPath path, ()) |> ignore

    let listed (kind: string) (directory: string) (pattern: string) (option: SearchOption) =
        listings.TryAdd(
            { Kind = kind
              Directory = fullPath directory
              Pattern = (if isNull pattern then "*" else pattern)
              // FSHW-WALK-001 ok: this compares the option MSBuild asked for and walks
              // nothing; a recursive listing is never re-read (`OptionsRecords.listingState`).
              Recursive = (option = SearchOption.AllDirectories) },
            ()
        )
        |> ignore

    member _.Reads = List.ofSeq reads.Keys
    member _.Probes = List.ofSeq probes.Keys
    member _.Listings = List.ofSeq listings.Keys

    /// A new recorder. Tests construct one here: F# refuses them its constructor (FS0491)
    /// through the MSBuild base class, though the type itself is visible to them.
    static member internal Create() = RecordingFileSystem()

    override _.ReadFile(path) =
        read path
        base.ReadFile path

    override _.GetFileStream(path, mode, access, share) =
        read path
        base.GetFileStream(path, mode, access, share)

    override _.ReadFileAllText(path) =
        read path
        base.ReadFileAllText path

    override _.ReadFileAllBytes(path) =
        read path
        base.ReadFileAllBytes path

    override _.EnumerateFiles(path, searchPattern, searchOption) =
        listed "files" path searchPattern searchOption
        base.EnumerateFiles(path, searchPattern, searchOption)

    override _.EnumerateDirectories(path, searchPattern, searchOption) =
        listed "directories" path searchPattern searchOption
        base.EnumerateDirectories(path, searchPattern, searchOption)

    override _.EnumerateFileSystemEntries(path, searchPattern, searchOption) =
        listed "entries" path searchPattern searchOption
        base.EnumerateFileSystemEntries(path, searchPattern, searchOption)

    override _.GetAttributes(path) =
        probe path
        base.GetAttributes path

    override _.GetLastWriteTimeUtc(path) =
        probe path
        base.GetLastWriteTimeUtc path

    override _.DirectoryExists(path) =
        probe path
        base.DirectoryExists path

    override _.FileExists(path) =
        probe path
        base.FileExists path

    override _.FileOrDirectoryExists(path) =
        probe path
        base.FileOrDirectoryExists path

/// What evaluating `projects` reads, as `OptionsRecords.optionsKey` keys it.
let list (projects: Types.ProjectOptions list) : EvaluationInputs =
    let fileSystem = RecordingFileSystem()
    // Shared: the only policy that takes a file system. Its caches only skip a call
    // already recorded, so each distinct question is recorded once.
    let context =
        EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, fileSystem)


    use collection =
        new ProjectCollection(
            Dictionary<string, string>(),
            null,
            null,
            ToolsetDefinitionLocations.Local,
            1,
            false,
            true
        )

    let reads = HashSet<string>()
    let probes = HashSet<string>()
    let envNames = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let readFile (path: string) =
        if reads.Add path && File.Exists path then
            for name in environmentNamesIn (File.ReadAllText path) do
                envNames.Add name |> ignore

    for project in projects do
        let projectFile = Path.GetFullPath project.ProjectFileName
        readFile projectFile

        if not (String.IsNullOrEmpty project.ProjectSdkInfo.ProjectAssetsFile) then
            reads.Add(Path.GetFullPath project.ProjectSdkInfo.ProjectAssetsFile) |> ignore

        // A compile item the design-time build generates (under obj/ or bin/) is written by
        // an evaluation and not by a replay: one a clean has removed must evaluate again.
        for source in project.SourceFiles do
            if PathFilter.isGeneratedPath source then
                reads.Add(Path.GetFullPath source) |> ignore

        let mutable dir = Path.GetDirectoryName projectFile

        while not (String.IsNullOrEmpty dir) do
            for name in wellKnownAbove do
                probes.Add(Path.Combine(dir, name)) |> ignore

            dir <- Path.GetDirectoryName dir

        let frameworks =
            [ None
              if not (String.IsNullOrEmpty project.TargetFramework) then
                  Some project.TargetFramework ]

        for framework in frameworks do
            let options =
                Microsoft.Build.Definition.ProjectOptions(
                    GlobalProperties = ProjectLoader.getGlobalProps framework [] Set.empty,
                    ProjectCollection = collection,
                    LoadSettings =
                        (ProjectLoadSettings.IgnoreMissingImports
                         ||| ProjectLoadSettings.IgnoreInvalidImports),
                    EvaluationContext = context
                )

            let evaluated = Project.FromFile(projectFile, options)

            for import in evaluated.Imports do
                readFile import.ImportedProject.FullPath

            collection.UnloadProject evaluated

    { Reads = Seq.append reads fileSystem.Reads |> Seq.distinct |> List.ofSeq
      Probes = Seq.append probes fileSystem.Probes |> Seq.distinct |> List.ofSeq
      Listings = fileSystem.Listings
      EnvNames = List.ofSeq envNames }

/// The builds that decide what an evaluation produces besides its inputs: fshw's, Ionide's
/// (its global properties and design-time targets), and the MSBuild that runs it.
let toolchain () : string =
    [ typeof<EvaluationInputs>.Assembly
      typeof<WorkspaceLoader>.Assembly
      typeof<Project>.Assembly ]
    |> List.map (fun assembly ->
        let name = assembly.GetName()
        $"%s{name.Name}@%O{name.Version}/%O{assembly.ManifestModule.ModuleVersionId}")
    |> String.concat ";"
