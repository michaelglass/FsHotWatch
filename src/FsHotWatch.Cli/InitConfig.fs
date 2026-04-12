module FsHotWatch.Cli.InitConfig

open System.IO
open System.Text
open System.Text.Json
open FsHotWatch.Cli.DaemonConfig

/// Classification of a discovered .fsproj file.
type ProjectKind =
    | SourceProject of name: string
    | TestProject of name: string

/// Classify a project by its relative path.
/// Projects under tests/ or test/, or with a .Tests suffix, are test projects.
let classifyProject (relativePath: string) : ProjectKind =
    let normalized = relativePath.Replace('\\', '/')
    let name = Path.GetFileNameWithoutExtension(normalized)

    let isTestDir = normalized.StartsWith("tests/") || normalized.StartsWith("test/")

    let isTestName = name.EndsWith(".Tests") || name.EndsWith(".Test")

    if isTestDir || isTestName then
        TestProject name
    else
        SourceProject name

/// Discover all .fsproj files under a directory, returning paths relative to repoRoot.
/// Excludes bin/, obj/, and other artifact directories.
/// An optional enumerateFiles function can be injected for testing (permission errors, etc.).
let discoverProjects
    (repoRoot: string)
    (enumerateFiles: (string -> string -> SearchOption -> seq<string>) option)
    : string list =
    let enumerate =
        defaultArg enumerateFiles (fun dir pattern opt -> Directory.EnumerateFiles(dir, pattern, opt))

    try
        enumerate repoRoot "*.fsproj" SearchOption.AllDirectories
        |> Seq.map (fun fullPath -> Path.GetRelativePath(repoRoot, fullPath))
        |> Seq.filter (fun p ->
            let n = p.Replace('\\', '/')
            not (n.Contains("/obj/")) && not (n.Contains("/bin/")))
        |> Seq.sort
        |> Seq.toList
    with
    | :? DirectoryNotFoundException -> []
    | :? System.UnauthorizedAccessException -> []

/// Generate a DaemonConfiguration from discovered project paths.
let generateConfig (projectPaths: string list) (hasJj: bool) : DaemonConfiguration =
    let classified = projectPaths |> List.map (fun p -> (p, classifyProject p))

    let testProjects =
        classified
        |> List.choose (fun (path, kind) ->
            match kind with
            | TestProject name ->
                let projectDir = Path.GetDirectoryName(path).Replace('\\', '/')

                Some
                    { Project = name
                      Command = "dotnet"
                      Args = $"run --project %s{projectDir} --no-build --"
                      Group = "default"
                      Environment = []
                      FilterTemplate = Some "--filter-class {classes}"
                      ClassJoin = " "
                      Coverage = true }
            | SourceProject _ -> None)

    { Build =
        Some
            [ {| Command = "dotnet"
                 Args = "build"
                 BuildTemplate = None
                 DependsOn = [] |} ]
      Format = Auto
      Lint = true
      Cache = if hasJj then JjFileBackend else FileBackend
      Analyzers = None
      Tests =
        if testProjects.IsEmpty then
            None
        else
            Some
                {| BeforeRun = None
                   Extensions = []
                   Projects = testProjects |}
      Coverage = None
      FileCommands = []
      Exclude = [] }

/// Serialize a DaemonConfiguration to a pretty-printed JSON string.
let serializeConfig (config: DaemonConfiguration) : string =
    use stream = new MemoryStream()
    let options = JsonWriterOptions(Indented = true)
    use writer = new Utf8JsonWriter(stream, options)

    writer.WriteStartObject()

    // Build
    match config.Build with
    | Some [ b ] ->
        writer.WritePropertyName("build")
        writer.WriteStartObject()
        writer.WriteString("command", b.Command)
        writer.WriteString("args", b.Args)
        writer.WriteEndObject()
    | Some builds when builds.Length > 1 ->
        writer.WritePropertyName("build")
        writer.WriteStartArray()

        for b in builds do
            writer.WriteStartObject()
            writer.WriteString("command", b.Command)
            writer.WriteString("args", b.Args)
            writer.WriteEndObject()

        writer.WriteEndArray()
    | _ -> ()

    // Format & Lint
    match config.Format with
    | Auto -> writer.WriteBoolean("format", true)
    | Off -> writer.WriteBoolean("format", false)
    | Check -> writer.WriteString("format", "check")

    writer.WriteBoolean("lint", config.Lint)

    // Cache
    match config.Cache with
    | JjFileBackend -> writer.WriteString("cache", "jj")
    | FileBackend -> writer.WriteString("cache", "file")
    | InMemoryOnly _ -> writer.WriteString("cache", "memory")
    | NoCache -> writer.WriteBoolean("cache", false)

    // Tests
    match config.Tests with
    | Some t when not t.Projects.IsEmpty ->
        writer.WritePropertyName("tests")
        writer.WriteStartObject()
        writer.WritePropertyName("projects")
        writer.WriteStartArray()

        for p in t.Projects do
            writer.WriteStartObject()
            writer.WriteString("project", p.Project)
            writer.WriteString("command", p.Command)
            writer.WriteString("args", p.Args)

            match p.FilterTemplate with
            | Some ft -> writer.WriteString("filterTemplate", ft)
            | None -> ()

            writer.WriteString("classJoin", p.ClassJoin)
            writer.WriteString("group", p.Group)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
    | _ -> ()

    // Coverage
    match config.Coverage with
    | Some cov ->
        writer.WritePropertyName("coverage")
        writer.WriteStartObject()
        writer.WriteString("directory", cov.Directory)

        match cov.ThresholdsFile with
        | Some tf -> writer.WriteString("thresholdsFile", tf)
        | None -> ()

        match cov.AfterCheck with
        | Some ac -> writer.WriteString("afterCheck", ac)
        | None -> ()

        writer.WriteEndObject()
    | None -> ()

    writer.WriteEndObject()
    writer.Flush()
    Encoding.UTF8.GetString(stream.ToArray())
