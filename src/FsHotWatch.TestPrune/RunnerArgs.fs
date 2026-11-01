/// A configured test runner's command line: its project and build output, the CTRF runner family and arguments, diagnostics and long-running settings.
module FsHotWatch.TestPrune.RunnerArgs

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports

/// Tokenize the `ProcessStartInfo.Arguments` string far enough to discover a project
/// path — the one word-splitting rule in `ProcessHelper.splitArgs`. An unfinished quote
/// is not a partial command line, so discovery fails closed.
let internal argTokens (args: string) : string[] option = ProcessHelper.splitArgs args

/// The value following `--project`/`-p` in the quote-aware tokenized args.
/// Shared by `tryApphostPresent` and `detectCtrfRunnerFamily` (both derive a project
/// from the runner command line the same way).
let internal projectFlagValue (tokens: string[]) : string option =
    tokens
    |> Array.tryFindIndex (fun t -> t = "--project" || t = "-p")
    |> Option.bind (fun i -> if i + 1 < tokens.Length then Some tokens.[i + 1] else None)

/// Derive the runner's build-output target (project file, dir, assembly name,
/// `bin/Debug`) from its `--project` arg, or `None` when no `--project`/`-p`
/// token is present (a custom, non-`dotnet run` command). The `--project` value
/// may point at a `.fsproj`/`.csproj` file OR a directory; the assembly name
/// defaults to the project/dir leaf — matching `ProjectGraph.GetCanonicalDllPath`,
/// which uses the project file's base name. Shared by `tryApphostPresent`
/// (presence) and `ArtifactFreshness.stale` (freshness) so this
/// fsproj-or-directory derivation has ONE definition.
let internal deriveProjectBin (args: string) (repoRoot: string) : ArtifactFreshness.RunnerTarget option =
    argTokens args
    |> Option.bind projectFlagValue
    |> Option.map (fun proj ->
        // Resolve to an absolute path (relative paths are repoRoot-relative).
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        let projectFile, projDir, assemblyName =
            if
                File.Exists abs
                && (abs.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || abs.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            then
                Some abs, Path.GetDirectoryName(abs), Path.GetFileNameWithoutExtension(abs)
            else
                // Treat as a directory. The assembly name conventionally matches
                // the directory leaf; if a single project file lives there, prefer
                // that file's base name.
                let dir = abs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let projFile =
                    if Directory.Exists dir then
                        Directory.GetFiles(dir, "*.fsproj")
                        |> Array.append (Directory.GetFiles(dir, "*.csproj"))
                        |> Array.tryHead
                    else
                        None

                projFile,
                dir,
                (projFile
                 |> Option.map Path.GetFileNameWithoutExtension
                 |> Option.defaultValue (Path.GetFileName dir))

        { ArtifactFreshness.ProjectFile = projectFile
          ArtifactFreshness.ProjectDir = projDir
          ArtifactFreshness.AssemblyName = assemblyName
          ArtifactFreshness.BinDir = Path.Combine(projDir, "bin", "Debug") })

/// STRUCTURAL apphost-missing detection. On a cold daemon a `dotnet run --project
/// <proj> --no-build` can be launched before the build plugin produced that project's
/// apphost binary; `dotnet run` then fails to spawn it and exits non-zero. That is an
/// ORDERING bug, never a test failure.
///
/// Derived from the runner's `--project` arg and a `File.Exists`, rather than sniffing
/// localized OS error text out of the runner output — that is fragile to locale and SDK
/// phrasing (`looksLikeApphostMissing` keeps it only as a fallback). The apphost is the
/// extension-less sibling of `<projDir>/bin/Debug/<tfm>/<assemblyName>.dll` (`.exe` on
/// Windows); the TFM is unknown without the project graph, so every `bin/Debug/*/` dir
/// is globbed. Presence only — `ArtifactFreshness.stale` is the freshness complement.
///
/// Returns:
///   Some true  — project derivable AND apphost present
///   Some false — project derivable AND apphost absent (the deferred signal)
///   None       — could not derive a project from args (e.g. a non-`dotnet run`
///                custom command), OR its `bin/Debug` could not be listed; caller
///                falls back to the output sniff.
///
/// An unlistable `bin/Debug` is `None`, not `Some false`: `Some false` DEFERS the run as
/// "waiting on build", which would dress a failure we cannot diagnose from the
/// filesystem as a build that has not landed yet. `None` is this function's existing
/// "the filesystem cannot answer" — and the sniff reads the runner's own output instead.
let internal tryApphostPresent (args: string) (repoRoot: string) : bool option =
    deriveProjectBin args repoRoot
    |> Option.bind (fun target ->
        // The apphost lives at bin/Debug/<tfm>/<assemblyName>(.exe). We don't
        // know the TFM, so scan every TFM output dir for the extension-less
        // binary (Unix) or the `.exe` (Windows); no build output yet ⇒ no TFM
        // dirs ⇒ apphost definitionally absent.
        match ArtifactFreshness.tfmOutputDirs target.BinDir with
        | Error _ -> None
        | Ok tfmDirs ->
            tfmDirs
            |> Array.exists (fun tfmDir ->
                File.Exists(Path.Combine(tfmDir, target.AssemblyName))
                || File.Exists(Path.Combine(tfmDir, target.AssemblyName + ".exe")))
            |> Some)


/// The switch that makes a Microsoft.Testing.Platform host print a result line as each test
/// finishes (`passed <name> (4ms)`), which a profile reads a killed host's classes from
/// (`Profile.observe`, AUTOMATION-1300). Asked of the hosts fshw already asks for CTRF —
/// xUnit v3/v4 on MTP, whose every supported platform version accepts it — and never
/// when the project chose its own `--output`, which MTP refuses to take twice.
///
/// One line per test, written by the host's own console reporter: no host-side code, and
/// fshw already streams that output into the run log.
let internal detailedOutputArgument (configArgs: string) : string option =
    let chose =
        ProcessHelper.splitArgs configArgs
        |> Option.defaultValue [||]
        |> Array.exists (fun t -> t = "--output" || t.StartsWith "--output=")

    if chose then None else Some "--output Detailed"

/// The threshold, in seconds, after which xUnit reports a test as long-running: a third of
/// the project's timeout, so a test hung until the kill is reported at least twice before
/// it, and at most 10 s. A report names the test and how long it has run, which is how a
/// killed host's profile names the test it was running (`Profile.observe`).
let internal longRunningSeconds (timeoutSec: int option) : int =
    match timeoutSec with
    | Some t -> max 1 (min 10 (t / 3))
    | None -> 10

/// The `testconfig.json` a host is given so xUnit prints its long-running reports, which
/// are diagnostic messages: the project's own config (`own`, from its build output) with
/// `xUnit.diagnosticMessages` on. `--config-file` REPLACES a project's config rather than
/// adding to it, so a config fshw cannot read and merge is an `Error`, and nothing is
/// injected over it.
let internal diagnosticsTestConfig (own: string option) : Result<string, string> =
    try
        let root =
            match own with
            | Some text -> Nodes.JsonNode.Parse text
            | None -> Nodes.JsonObject() :> Nodes.JsonNode

        match root with
        | :? Nodes.JsonObject as o ->
            let xunit =
                match o["xUnit"] with
                | null ->
                    let section = Nodes.JsonObject()
                    o["xUnit"] <- section
                    Ok section
                | :? Nodes.JsonObject as section -> Ok section
                | _ -> Error "its `xUnit` section is not an object"

            xunit
            |> Result.map (fun section ->
                section["diagnosticMessages"] <- Nodes.JsonValue.Create true
                o.ToJsonString())
        | _ -> Error "it is not a JSON object"
    with :? JsonException as ex ->
        Error $"it is not JSON: %s{ex.Message}"

/// The switches that make xUnit report a test running past `seconds` (`--long-running`) and
/// print the report (`--config-file`, the diagnostics config at `configPath`); `None` when
/// the project chose either itself.
let internal longRunningArguments (configArgs: string) (configPath: string) (seconds: int) : string option =
    let chose =
        ProcessHelper.splitArgs configArgs
        |> Option.defaultValue [||]
        |> Array.exists (fun t ->
            t = "--long-running"
            || t.StartsWith "--long-running="
            || t = "--config-file"
            || t.StartsWith "--config-file=")

    if chose then
        None
    else
        Some $"--long-running %d{seconds} --config-file \"%s{configPath}\""

/// The xUnit runner major determines the names of its CTRF report switches.
/// xUnit 4 names them `--report-xunit-ctrf*`; xUnit 3 used
/// `--report-ctrf*`. Passing the other family's switches is fatal and runs zero
/// tests, so unknown majors are deliberately not treated as a best guess.
type internal CtrfRunnerFamily =
    | Xunit3
    | Xunit4

/// Render only the switches owned by the resolved runner family. The results
/// directory is an MTP switch and is shared by both supported families.
let internal ctrfArguments (family: CtrfRunnerFamily) (reportName: string) (resultsDirectory: string) : string =
    match family with
    | Xunit3 -> $"--report-ctrf --report-ctrf-filename %s{reportName} --results-directory \"%s{resultsDirectory}\""
    | Xunit4 ->
        $"--report-xunit-ctrf --report-xunit-ctrf-filename %s{reportName} --results-directory \"%s{resultsDirectory}\""

/// The xUnit v3 MTP bridge, `xunit.v3.core.mtp-v<N>`: its `xunit.v3.mtp-v<N>`
/// assembly registers xUnit with Microsoft.Testing.Platform, CTRF report switches
/// included. Every package id that ships the MTP runner is this bridge or depends on it.
let internal xunitMtpBridgeIdPattern = "xunit\\.v3\\.core\\.mtp-v[0-9]+"

let internal packageIdRegex (pattern: string) =
    System.Text.RegularExpressions.Regex(
        $"^(?:%s{pattern})$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
        ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )

/// The package that registers xUnit's CTRF report switches: the MTP bridge alone.
/// Restored `libraries` lists the whole transitive closure, so the bridge is present
/// whichever runner-bearing id the project references; its version still selects
/// the switch family.
let internal ctrfReporterPackageId = packageIdRegex xunitMtpBridgeIdPattern

/// Every xUnit v3 package id a project can REFERENCE to get the MTP runner: the
/// bridge itself, the `xunit.v3.mtp-v<N>` packages that depend on it, and the
/// `xunit.v3` package that depends on one of those. For matching direct references
/// (e.g. `PackageReference` ids in project XML), where the closure is not visible.
let internal xunitMtpRunnerPackageId =
    packageIdRegex $"xunit\\.v3|xunit\\.v3\\.mtp-v[0-9]+|%s{xunitMtpBridgeIdPattern}"

/// The project file a runner's arguments name: the value after `--project`/`-p`, else any
/// token that is itself a project file (`dotnet test path/to/Proj.fsproj`), resolved against
/// `repoRoot`; a directory resolves to the project file in it.
let internal runnerProjectFile (args: string) (repoRoot: string) : string option =
    let tokens = argTokens args

    let looksLikeProjectFile (t: string) =
        t.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

    // The project hint: the value after --project/-p, else any token that is
    // itself a project file path (e.g. `dotnet test path/to/Proj.fsproj`).
    let projArg =
        tokens
        |> Option.bind (fun parsed ->
            projectFlagValue parsed
            |> Option.orElse (parsed |> Array.tryFind looksLikeProjectFile))

    let resolveProjectFile (proj: string) : string option =
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        if File.Exists abs && looksLikeProjectFile abs then
            Some abs
        elif Directory.Exists abs then
            Directory.GetFiles(abs, "*.fsproj")
            |> Array.append (Directory.GetFiles(abs, "*.csproj"))
            |> Array.tryHead
        else
            None

    projArg |> Option.bind resolveProjectFile

/// Resolve the actual xUnit runner family from NuGet's restored graph. The
/// project file can contain central versions, properties, ranges, or stale
/// text, while `obj/project.assets.json` records the exact package version that
/// will run. Missing/malformed assets and unsupported majors return `None` so
/// auto-detection fails closed rather than giving a runner fatal arguments.
let internal detectCtrfRunnerFamily (args: string) (repoRoot: string) : CtrfRunnerFamily option =
    runnerProjectFile args repoRoot
    |> Option.bind (fun projFile ->
        try
            let assetsPath =
                Path.Combine(Path.GetDirectoryName(projFile), "obj", "project.assets.json")

            use assets = JsonDocument.Parse(File.ReadAllText assetsPath)
            let mutable libraries = Unchecked.defaultof<JsonElement>

            if assets.RootElement.TryGetProperty("libraries", &libraries) then
                let xunitLibraries =
                    libraries.EnumerateObject()
                    |> Seq.filter (fun package ->
                        let slash = package.Name.IndexOf('/')
                        slash > 0 && ctrfReporterPackageId.IsMatch(package.Name.Substring(0, slash)))
                    |> Seq.toList

                let completeNuGetVersion =
                    System.Text.RegularExpressions.Regex(
                        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:\\.(0|[1-9][0-9]*))?(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    )

                let tryFamily (package: JsonProperty) =
                    let mutable libraryType = Unchecked.defaultof<JsonElement>

                    if
                        not (package.Value.TryGetProperty("type", &libraryType))
                        || libraryType.ValueKind <> JsonValueKind.String
                        || not (String.Equals(libraryType.GetString(), "package", StringComparison.Ordinal))
                    then
                        None
                    else
                        let slash = package.Name.IndexOf('/')

                        // NuGet library keys use `<id>/<normalized-version>`. We only
                        // need the SemVer major; System.Version deliberately rejects
                        // valid NuGet prerelease suffixes such as `4.0.0-pre.12`.
                        let version = package.Name.Substring(slash + 1)
                        let matched = completeNuGetVersion.Match version

                        let numericComponents =
                            if matched.Success then
                                [ 1..4 ]
                                |> List.choose (fun group ->
                                    if matched.Groups[group].Success then
                                        Some matched.Groups[group].Value
                                    else
                                        None)
                                |> List.map Int32.TryParse
                            else
                                []

                        let prereleaseIdentifiersAreValid =
                            if matched.Success && matched.Groups[5].Success then
                                matched.Groups[5].Value.Split('.')
                                |> Array.forall (fun identifier ->
                                    let numeric = identifier |> Seq.forall Char.IsAsciiDigit
                                    not (numeric && identifier.Length > 1 && identifier[0] = '0'))
                            else
                                matched.Success

                        match numericComponents, prereleaseIdentifiersAreValid with
                        | (true, 3) :: remaining, true when remaining |> List.forall fst -> Some Xunit3
                        | (true, 4) :: remaining, true when remaining |> List.forall fst -> Some Xunit4
                        | _ -> None

                let parsedFamilies = xunitLibraries |> List.map tryFamily

                if List.isEmpty parsedFamilies || List.exists Option.isNone parsedFamilies then
                    None
                else
                    let families = parsedFamilies |> List.choose id |> Set.ofList

                    // Multiple patch/minor versions from one major are harmless, but two
                    // runner majors make the accepted command line ambiguous. Do not let
                    // JSON property order decide which suite gets fatal flags.
                    if families.Count = 1 then
                        families |> Seq.exactlyOne |> Some
                    else
                        None
            else
                None
        with _ ->
            None)

let internal assemblyNameElement =
    Text.RegularExpressions.Regex(@"<AssemblyName\b[^>]*>\s*([^<]*?)\s*</AssemblyName>")

/// The assembly a project builds, when its text makes that certain: the one literal
/// `<AssemblyName>` it declares, or, when neither it nor any `Directory.Build.props` /
/// `Directory.Build.targets` between it and `repoRoot` sets one, its file name (MSBuild's
/// default). `None` when the name is computed (`$(...)`), declared more than once, set in
/// a shared props file, or unreadable — fshw cannot tell which assembly is the project's.
let internal declaredAssemblyName (projectFile: string) (repoRoot: string) : string option =
    try
        let declared =
            assemblyNameElement.Matches(File.ReadAllText projectFile)
            |> Seq.map (fun m -> m.Groups[1].Value)
            |> Seq.toList

        let root = Path.GetFullPath repoRoot

        let rec setAbove (dir: string) =
            if isNull dir || not (Path.GetFullPath(dir).StartsWith root) then
                false
            else
                [ "Directory.Build.props"; "Directory.Build.targets" ]
                |> List.exists (fun name ->
                    let path = Path.Combine(dir, name)
                    File.Exists path && (File.ReadAllText path).Contains "<AssemblyName")
                || setAbove (Path.GetDirectoryName dir)

        match declared with
        | [ name ] when name <> "" && not (name.Contains '$') -> Some name
        | [] when not (setAbove (Path.GetDirectoryName projectFile)) ->
            Some(Path.GetFileNameWithoutExtension projectFile)
        | _ -> None
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

/// The project's own testconfig: `<Assembly>.testconfig.json` beside `<Assembly>.dll` in its
/// `bin/`, newest first, for the assembly it builds (`declaredAssemblyName`). `Some None`
/// when the output holds none. `None` — inject nothing — when the assembly name is not
/// certain, when the output holds a config (beside its assembly) of ANOTHER name, or when
/// it cannot be read: `--config-file` replaces a config, so a config that may be the
/// project's must never be guessed past.
let internal projectTestConfigOf (projectFile: string) (repoRoot: string) : string option option =
    declaredAssemblyName projectFile repoRoot
    |> Option.bind (fun assembly ->
        let bin = Path.Combine(Path.GetDirectoryName projectFile, "bin")
        let suffix = ".testconfig.json"

        try
            if Directory.Exists bin then
                // A directory the walk could not read might hold the config: unknown, not absent.
                match SafeWalk.walk Set.empty ("*" + suffix) bin with
                | { Skipped = [] } as found ->
                    let configs =
                        found.Files
                        |> List.choose (fun f ->
                            let name = f.Name.Substring(0, f.Name.Length - suffix.Length)

                            if File.Exists(Path.Combine(f.DirectoryName, name + ".dll")) then
                                Some(name, f)
                            else
                                None)

                    if configs |> List.exists (fun (name, _) -> name <> assembly) then
                        None
                    else
                        configs
                        |> List.map snd
                        |> List.sortByDescending _.LastWriteTimeUtc
                        |> List.tryHead
                        |> Option.map (fun f -> File.ReadAllText f.FullName)
                        |> Some
                | _ -> None
            else
                Some None
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None)

let internal projectTestConfigFor (configArgs: string) (repoRoot: string) : string option option =
    runnerProjectFile configArgs repoRoot
    |> Option.bind (fun projectFile -> projectTestConfigOf projectFile repoRoot)

/// Fallback apphost-missing classifier, used ONLY when `tryApphostPresent` cannot derive
/// a project from the runner args (custom, non-`dotnet run` commands).
///
/// Distinguishes an apphost launch failure from a genuine non-zero test exit: a real
/// xUnit/MTP failure carries `failed <name>` lines and a `failed:`/`Test run summary`
/// block, while the launch failure carries the host's "An error occurred trying to start
/// process …" / "No such file or directory" signature and NO test-summary block.
/// Deliberately conservative — in doubt, treat the output as a real failure and never
/// silence a red.
let internal looksLikeApphostMissing (output: string) : bool =
    if String.IsNullOrWhiteSpace output then
        false
    else
        let lower = output.ToLowerInvariant()

        // Signatures the .NET host emits when it cannot launch the apphost the
        // build was supposed to produce.
        let hasStartProcessFailure =
            lower.Contains("an error occurred trying to start process")
            || (lower.Contains("no such file or directory")
                && (lower.Contains("apphost")
                    || lower.Contains("/bin/")
                    || lower.Contains("\\bin\\")))
            || lower.Contains("apphost_version not found")

        // A genuine test run always emits a summary / per-test `failed ` lines.
        // Their PRESENCE means the runner actually executed tests, so this is a
        // real failure, not a launch race — don't misclassify it.
        let looksLikeRealTestFailure =
            lower.Contains("test run summary")
            || lower.Contains("failed:")
            || (ConsoleText.lines output |> Array.exists isFailedTestLine)

        hasStartProcessFailure && not looksLikeRealTestFailure
