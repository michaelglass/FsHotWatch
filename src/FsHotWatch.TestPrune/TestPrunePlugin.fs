module FsHotWatch.TestPrune.TestPrunePlugin

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







/// The test projects `run-tests --only-failed` re-runs: every non-green project of the
/// last run, plus every project still owing an outstanding failure.
///
/// "Failed" is the OUTSTANDING set, not merely the last run's results: after an
/// impact-filtered run the failing project is not in `LastResults` at all (it wasn't
/// selected), and `--only-failed` would have re-run nothing — "no matching test
/// projects" — while a red sat there. The outstanding ledger is what still owes a
/// re-run, so it is what this verb must re-run.
let internal failedTestConfigs
    (allConfigs: TestConfig list)
    (lastResults: TestResults option)
    (outstandingFailures: OutstandingFailure list)
    : Result<TestConfig list, string> =
    let outstandingProjects =
        outstandingFailures |> List.map (fun f -> f.Project) |> Set.ofList

    let lastRunFailed =
        match lastResults with
        | Some prev ->
            prev.Results
            |> Map.toList
            |> List.choose (fun (name, r) ->
                match r with
                | TestsFailed _
                | TestsTimedOut _
                // A deferred project never ran, and an errored one aborted without a
                // verdict — both are non-green, so `--only-failed` must pick them up.
                | TestsDeferred _
                | TestsErrored _ -> Some name
                | _ -> None)
            |> Set.ofList
        | None -> Set.empty

    let failedNames = Set.union lastRunFailed outstandingProjects

    if lastResults.IsNone && Set.isEmpty failedNames then
        Error "no previous results — cannot determine failed projects"
    else
        Ok(allConfigs |> List.filter (fun c -> failedNames.Contains(c.Project)))

/// Build the degenerate Started→Aborted lifecycle a faulted run posts back so the
/// synchronous `TestsFinished` handler drives the plugin to a NON-green terminal status.
/// A `beforeRun` throw / `executeTests` fault means the suite it guards NEVER RAN — that
/// must surface as a failure, never a stale prior green. Shared by the
/// impact path and the manual `run-tests` command so the two stay in lockstep.
/// `Results = Map.empty` ⇒ the handler commits nothing from the pending queue; `reason`
/// carries the hook's failure output so `fshw check` / `fshw errors` shows WHY.
let private abortedRunLifecycle
    (emittedStart: TestRunStarted option)
    (reason: string)
    : TestRunStarted * TestRunCompleted =
    let started =
        emittedStart
        |> Option.defaultWith (fun () ->
            { RunId = Guid.NewGuid()
              StartedAt = DateTime.UtcNow })

    let completed: TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Aborted reason
          Results = Map.empty
          // No project was invoked, so that is what it says. Under the old `bool`
          // this had to pick between claiming a suite that never ran and claiming a
          // filtering that never happened — the comment here read "a lie either
          // way". There is now a case for what actually occurred.
          Verification = NoProjectsSelected }

    started, completed








/// Tokenize the `ProcessStartInfo.Arguments` string far enough to discover a project
/// path — the one word-splitting rule in `ProcessHelper.splitArgs`. An unfinished quote
/// is not a partial command line, so discovery fails closed.
let private argTokens (args: string) : string[] option = ProcessHelper.splitArgs args

/// The value following `--project`/`-p` in the quote-aware tokenized args.
/// Shared by `tryApphostPresent` and `detectCtrfRunnerFamily` (both derive a project
/// from the runner command line the same way).
let private projectFlagValue (tokens: string[]) : string option =
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
let private xunitMtpBridgeIdPattern = "xunit\\.v3\\.core\\.mtp-v[0-9]+"

let private packageIdRegex (pattern: string) =
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

let private assemblyNameElement =
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

/// Split a runner's fully-qualified test name into (class, method) against `classes`,
/// the test classes the runner's report declares and the index stores.
///
/// The runner prints compiled names, and a backticked method or class keeps every `.`
/// written between its backticks (`Ns.Tests+Parsing.parses v1.5 input`), so where the
/// class ends is not visible in the name alone. The class is therefore the LONGEST
/// prefix ending before a `.` that is one of `classes`; the method is the rest, theory
/// arguments included. A name no known class prefixes falls back to the last dotted
/// segment as the method and the COMPLETE prefix as the class. A name with no dot is its
/// own class. Keeping the full prefix is load-bearing: launch selections use fully
/// qualified class names, so truncating `A.B.Tests.method` to `Tests` makes a test run
/// and pass while its retained red remains permanently "not covered".
///
/// ONE derivation, used by BOTH sides of the ledger: the class a red is FILED under
/// (`parseFailedTestsAgainst`, off the runner's `failed <name>` console lines, and the
/// CTRF rows that name a red the console did not) and the class a run's report
/// VINDICATES (`passedClassesOfReport`, off the CTRF `name` field) — and the class a
/// recall sample asks the retained selection about (`failedTestsOfReport`). All of them
/// join the index's classes, so all of them split here.
let internal splitTestName (classes: Set<string>) (name: string) : string * string =
    let rec declared (before: int) =
        match name.LastIndexOf('.', before) with
        | separator when separator > 0 ->
            if Set.contains (name.Substring(0, separator)) classes then
                Some separator
            else
                declared (separator - 1)
        | _ -> None

    let separator =
        if Set.isEmpty classes || name.Length < 2 then
            None
        else
            declared (name.Length - 2)
        |> Option.defaultWith (fun () -> name.LastIndexOf('.'))

    if separator > 0 && separator < name.Length - 1 then
        name.Substring(0, separator), name.Substring(separator + 1)
    else
        name, name

/// Parse "failed Namespace.Class.Method (Xms)" lines from test output, colour removed —
/// `failed (canceled) <name> (Xms)` and a multi-unit duration `(10s 112ms)` included.
/// Returns (className, methodName, fullLine) tuples; `fullLine` is the line as words.
/// Each name is split against `classes` (`splitTestName`).
/// Only a line in the runner's result shape is read (`isFailedTestLine`): prose that opens
/// with `failed` names no test.
let internal parseFailedTestsAgainst (classes: Set<string>) (output: string) : (string * string * string) list =
    ConsoleText.lines output
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if isFailedTestLine trimmed then
            // Strip the "failed " prefix, a parenthesised qualifier such as
            // "(canceled)" that MTP puts before the name, and the trailing duration.
            let rest =
                let afterVerb = trimmed.Substring(7).Trim()

                if afterVerb.StartsWith("(") then
                    match afterVerb.IndexOf(')') with
                    | -1 -> afterVerb
                    | close -> afterVerb.Substring(close + 1).Trim()
                else
                    afterVerb

            let name =
                match rest.LastIndexOf(" (") with
                | -1 -> rest
                | i -> rest.Substring(0, i)

            let className, methodName = splitTestName classes name
            Some(className, methodName, trimmed)
        else
            None)
    |> Array.toList

/// `parseFailedTestsAgainst` with no known classes: each name splits at its last `.`.
let parseFailedTests (output: string) : (string * string * string) list =
    parseFailedTestsAgainst Set.empty output

/// The classes ONE project's CTRF report proves RAN AND PASSED in this run
/// — the receipt `RunCoverage.ofRun` reads for a raw `--filter`
/// passthrough, whose launch REQUEST claims nothing.
///
/// A class is claimed only when the report holds at least one PASSED test for it and
/// NO failed/other test for it. Everything else fails CLOSED — an empty set, which
/// leaves every red exactly as it was:
///
///   * no parseable SUMMARY block → the report was truncated or never flushed;
///   * per-test array shorter (or longer) than the summary's total → the array is
///     INCOMPLETE. A real report omits per-test entries for tests that threw a raw
///     (non-assertion) exception while still counting them in the summary, so a class
///     could look all-green here while one of its tests exploded. Counting is the only
///     way to see the omission, so an array that does not account for every test in the
///     summary is not evidence about ANY class in it;
///   * a class with a failed/other entry, or with no passed entry at all (all skipped)
///     → not claimed.
///
/// Skips are neutral rather than disqualifying, because the unfiltered arm they must
/// agree with is: a full run whose report contains skips still returns
/// `CoveredWholeProject` and clears everything. A rule that let a skip block a class
/// here would be stricter than the whole-project path it is a refinement of.
let internal passedTestsOfReport (json: string) : Set<string * string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.groupBy (fun r -> splitTestName classes r.Name)
            |> List.choose (fun (testIdentity, forTest) ->
                let anyPassed = forTest |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forTest
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then
                    Some testIdentity
                else
                    None)
            |> Set.ofList

let internal passedClassesOfReport (json: string) : Set<string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.groupBy (fun r -> fst (splitTestName classes r.Name))
            |> List.choose (fun (cls, forClass) ->
                let anyPassed = forClass |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forClass
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then Some cls else None)
            |> Set.ofList

/// Every class ONE project's CTRF report has a row for, in any outcome — skipped
/// included — when the report is complete: a parseable summary and a per-test array that
/// accounts for every test in it. `None` otherwise, because an incomplete report cannot
/// say a class is ABSENT, only that some rows are missing.
let internal classesRunOfReport (json: string) : Set<string> option =
    match Ctrf.trySummary json with
    | None -> None
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            None
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json

            records
            |> List.map (fun r -> fst (splitTestName classes r.Name))
            |> Set.ofList
            |> Some

/// Per project that PASSED in this run, the classes its complete CTRF report ran
/// (`classesRunOfReport`). A project that failed, timed out, matched nothing, or wrote
/// no complete report is absent: nothing is known about which classes it has.
let internal classesRunOfPassedProjects
    (repoRoot: string)
    (runId: Guid)
    (results: Map<string, TestResult>)
    : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        match Map.tryFind report.Project results with
        | Some(TestsPassed _) ->
            try
                classesRunOfReport (File.ReadAllText report.Path)
                |> Option.map (fun classes -> report.Project, classes)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None
        | _ -> None)
    |> Map.ofList

/// The per-project passed-class evidence for a completed run, read back from the CTRF
/// reports THAT RUN wrote (`.fshw/test-runs/<runId>/<Project>.ctrf.json`).
///
/// Scoped by RUN DIRECTORY, so a previous run's report can never be mistaken for this
/// one's: the directory IS the run. A project with no readable report contributes no
/// entry, and a project absent from the map claims nothing — the absence of evidence is
/// never evidence of a pass.
let internal passedClassesOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedClassesOfReport with
        | Some classes when not (Set.isEmpty classes) -> Some(report.Project, classes)
        | _ -> None)
    |> Map.ofList

let internal passedTestsOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string * string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedTestsOfReport with
        | Some tests when not (Set.isEmpty tests) -> Some(report.Project, tests)
        | _ -> None)
    |> Map.ofList

/// Exact failed rows from a COMPLETE CTRF report. The summary's failed count is the
/// denominator authority; if the per-test array omits even one failure, this refuses
/// the sample rather than shrinking recall's denominator to the rows that happened to
/// parse.
let internal failedTestsOfReport (json: string) : (string * string) list option =
    match Ctrf.trySummary json with
    | None -> None
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        let failed =
            records |> List.filter (fun record -> record.Outcome = Flakiness.Failed)

        if records.Length <> summary.Total || failed.Length <> summary.Failed then
            None
        else
            let classes = Flakiness.parseCtrfDeclaredClasses json
            failed |> List.map (fun record -> splitTestName classes record.Name) |> Some

/// Every failed row of a CTRF report, by fully-qualified name, whether or not the rows
/// reconcile to the summary. Naming is not counting: a report that omitted a raw-throw
/// row still names the assertions that failed, and each name is a red the console did
/// not spell out.
let internal failedRowsOfReport (json: string) : string list =
    Flakiness.parseCtrfTests json
    |> List.filter (fun record -> record.Outcome = Flakiness.Failed)
    |> List.map (fun record -> record.Name)

/// What one project's CTRF report contributes to filing its reds: the failed rows it
/// names, and the classes it declares. A red is split against those classes whether the
/// console or the report named it, so it is filed under the class the report's passing
/// rows later vindicate.
type internal ReportedFailures =
    { Names: string list
      Classes: Set<string> }

module internal ReportedFailures =
    /// No report: the console is all a red can be named from.
    let none = { Names = []; Classes = Set.empty }

    let ofReport (json: string) =
        { Names = failedRowsOfReport json
          Classes = Flakiness.parseCtrfDeclaredClasses json }

let internal sharedInfrastructureFailureOfReport (json: string) : string option =
    try
        use document = JsonDocument.Parse json
        let mutable results = Unchecked.defaultof<JsonElement>
        let mutable tests = Unchecked.defaultof<JsonElement>

        if
            document.RootElement.TryGetProperty("results", &results)
            && results.TryGetProperty("tests", &tests)
            && tests.ValueKind = JsonValueKind.Array
        then
            let failedRows =
                tests.EnumerateArray()
                |> Seq.filter (fun row ->
                    let mutable status = Unchecked.defaultof<JsonElement>
                    row.TryGetProperty("status", &status) && status.GetString() = "failed")
                |> Seq.toList

            let rec stringDescendants (element: JsonElement) =
                seq {
                    match element.ValueKind with
                    | JsonValueKind.String -> yield element.GetString()
                    | JsonValueKind.Object ->
                        for property in element.EnumerateObject() do
                            yield! stringDescendants property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            yield! stringDescendants item
                    | _ -> ()
                }

            let exceptionTexts (row: JsonElement) =
                seq {
                    for property in row.EnumerateObject() do
                        if
                            property.Name.Equals("trace", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("stack", StringComparison.OrdinalIgnoreCase)
                            || property.Name.Equals("exception", StringComparison.OrdinalIgnoreCase)
                        then
                            yield! stringDescendants property.Value
                }

            let containsTypeIdentity (identity: string) (text: string) =
                let isIdentifierCharacter c =
                    Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '`'

                let rec search start =
                    let index = text.IndexOf(identity, start, StringComparison.Ordinal)

                    if index < 0 then
                        false
                    else
                        let beforeIsBoundary = index = 0 || not (isIdentifierCharacter text[index - 1])
                        let after = index + identity.Length
                        let afterIsBoundary = after = text.Length || not (isIdentifierCharacter text[after])

                        if beforeIsBoundary && afterIsBoundary then
                            true
                        else
                            search (index + 1)

                search 0

            let causesOf (row: JsonElement) =
                exceptionTexts row
                |> Seq.collect (fun text ->
                    [ if containsTypeIdentity "Npgsql.NpgsqlException" text then
                          "NpgsqlException"
                      if containsTypeIdentity "System.Net.Sockets.SocketException" text then
                          "SocketException" ])
                |> Seq.toList

            match failedRows with
            | [] -> None
            | rows ->
                match rows |> List.collect causesOf |> List.distinct with
                | [] -> None
                | causes -> Some(String.concat "/" causes)
        else
            None
    with
    | :? JsonException
    | :? InvalidOperationException -> None

let internal failedTestsOfRun
    (repoRoot: string)
    (runId: Guid)
    (results: TestResults)
    : Result<OutstandingFailure list, string> =
    let reports =
        Ctrf.reportsForRun repoRoot runId
        |> List.map (fun report -> report.Project, report)
        |> Map.ofList

    results.Results
    |> Map.toList
    |> List.fold
        (fun state (project, result) ->
            state
            |> Result.bind (fun accumulated ->
                if TestResult.isTimedOut result then
                    Error $"%s{project} timed out, so no complete failing-test denominator exists"
                else
                    match result, Map.tryFind project reports with
                    | TestsErrored reason, _ ->
                        Error $"%s{project} did not produce an executable test result: %s{reason}"
                    | TestsDeferred reason, _ -> Error $"%s{project} tests were deferred: %s{reason}"
                    | TestsNoMatch _, _ -> Error $"%s{project} executed no tests"
                    | TestsPassed _, _ -> Ok accumulated
                    | (TestsFailed _ | TestsTimedOut _), None ->
                        Error $"%s{project} failed but wrote no current-run CTRF report"
                    | _, Some report ->
                        try
                            let json = File.ReadAllText report.Path

                            match sharedInfrastructureFailureOfReport json, failedTestsOfReport json with
                            | Some cause, _ -> Error $"%s{project} failed through shared infrastructure (%s{cause})"
                            | None, None -> Error $"%s{project}'s CTRF failed rows do not reconcile to its summary"
                            | None, Some identities ->
                                identities
                                |> List.map (fun (className, methodName) ->
                                    { Project = project
                                      Class = Some className
                                      Method = Some methodName
                                      File = report.Path
                                      Entry = ErrorLedger.ErrorEntry.error "observed full-run failure" })
                                |> List.append accumulated
                                |> Ok
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> Error $"%s{project}'s CTRF report could not be read"))
        (Ok [])

/// The reds a completed run FOUND, as outstanding failures. Pure:
/// the same run always yields the same set, so the ledger projection is a function
/// of the evidence and nothing else.
///
/// A red is attributed to a CLASS only when the runner named one AND the result is a
/// genuine test failure. A TIMEOUT is deliberately project-level (`Class = None`)
/// even when its output names tests: a project killed for being stuck is a fact
/// about the PROJECT, and a later class-filtered green must not be allowed to
/// vindicate it. Deferred/errored projects are project-level for the same reason —
/// nothing about them was verified.
///
/// `runLogOf` names the run log a project's output was streamed to: a
/// project-level red carries the head of its output AND the path, so the verdict says
/// what the tool knows and where the rest is.
let internal failuresOf
    (runLogOf: string -> RunLog.Ref)
    (reportOf: string -> ReportedFailures)
    (classFiles: Map<string, string>)
    (results: TestResults)
    : OutstandingFailure list =
    let synthetic (project: string) = $"<tests/%s{project}>"

    results.Results
    |> Map.toList
    |> List.collect (fun (project, result) ->
        let projectLevel (entry: ErrorLedger.ErrorEntry) =
            { Project = project
              Class = None
              Method = None
              File = synthetic project
              Entry = entry }

        match result with
        | TestsFailed(output, _, _)
        | TestsTimedOut(output, _, _, _) ->
            // A timeout is never attributable to one class (see above).
            let isTimeout = TestResult.isTimedOut result

            // The console names the reds; when it names none, the CTRF report the
            // runner wrote beside the output log names them instead. The report row is
            // the same fully-qualified name the console prints, so a red filed from it
            // is retired by the same passing row. Either way the name splits against the
            // classes the report declares and the index stores.
            let report = reportOf project

            let classes = classFiles |> Map.keys |> Set.ofSeq |> Set.union report.Classes

            let parsed =
                match parseFailedTestsAgainst classes output with
                | [] ->
                    report.Names
                    |> List.map (fun name ->
                        let className, methodName = splitTestName classes name

                        className,
                        methodName,
                        $"failed %s{name} — named by the run's CTRF report; the console printed no line the parser recognised")
                | fromConsole -> fromConsole

            if parsed.IsEmpty then
                // ONE entry for the project, so the whole captured output is carried
                // ONCE. This is the only arm where that text is the entry's own
                // subject: no test was named, so the run itself is what the reader has
                // to go on.
                //
                // The MESSAGE carries the head of that output and the
                // log path — the verdict records only `Message`, and "Tests failed in X"
                // with the cause hidden in a `Detail` no surface prints sent a reader to
                // reap daemons over a shard-pool refusal stated on line 1.
                [ projectLevel (
                      ErrorLedger.ErrorEntry.errorWithDetail
                          (FailureCause.render (
                              FailureCause.ofOutput (RedRunEnding.ofResult result) project (runLogOf project) output
                          ))
                          output
                  ) ]
            else
                parsed
                |> List.map (fun (className, methodName, line) ->
                    let file =
                        classFiles |> Map.tryFind className |> Option.defaultValue (synthetic project)

                    { Project = project
                      Class = (if isTimeout then None else Some className)
                      Method = (if isTimeout then None else Some methodName)
                      File = file
                      // The failing LINE, and no detail.
                      //
                      // This used to attach `output` — the whole captured project run —
                      // to every parsed failure, which made the ledger's size the
                      // PRODUCT of two unbounded numbers rather than their sum. 753
                      // failing tests against a 48 MB capture is 36 GB once a mirror of
                      // the ledger writes each entry's copy out, and the merge gate died
                      // building exactly that reply after the daemon had already
                      // finished the run.
                      //
                      // Nothing is lost by dropping it, because it was never this
                      // entry's fact: it was the same project-wide string repeated per
                      // test, no renderer has ever printed a ledger `Detail`, and the
                      // untruncated capture is on disk at
                      // `.fshw/test-runs/<runId>/<project>.output.log`. The transport
                      // bound in `ErrorLedger.Transport` still stands behind this — a
                      // plugin must not be able to do it again — but the bound is a
                      // backstop, and this is the defect.
                      Entry = ErrorLedger.ErrorEntry.error line })
        | TestsDeferred reason ->
            // NOT a test failure — surface an honest "waiting on build / did not
            // run" diagnostic at `Deferred` severity so the verdict is NON-green
            // (nothing was verified) yet NOT a red: the CLI routes any
            // `Deferred`-severity entry to `Incomplete`/exit 2, distinct from the
            // exit 1 a real failure earns. This entry still joins the Outstanding
            // failure list, so cache participation stays refused (a deferred run is
            // never replayed as a green) — the severity governs the VERDICT, the
            // outstanding LIST governs the CACHE, and the two are decoupled.
            // The DETAIL used to assert one cause for every defer: "its build artifact
            // (apphost) was not produced … a build-ordering issue". That is false for a
            // stale-artifact refusal, where the artifact WAS produced and holds bytes
            // that do not match its sources — and it is false in the expensive
            // direction, because a build-ordering race settles on its own while stale
            // output does not. A reader told to wait it out re-runs and gets the
            // identical refusal, which is the defect the stale-artifact preflight exists to delete.
            // So the detail is derived from the reason, not asserted over it.
            let detail =
                if StaleArtifactPreflight.isStaleOutputDeferral reason then
                    $"The %s{project} test project did not run because its build OUTPUT is stale: the artifact \
                      exists, but its bytes do not match the sources it was built from, so running the suite would \
                      test code you did not write. This is NOT a build-ordering race and re-running will not settle \
                      it — the reason above names the file and the command that repairs it."
                else
                    $"The %s{project} test project did not run because its build artifact (apphost) was not \
                      produced. Tests were NOT executed, so this cycle cannot be reported as passing. This is a \
                      build-ordering issue, not a test failure."

            [ projectLevel (
                  ErrorLedger.ErrorEntry.deferredWithDetail $"%s{project}: waiting on build — %s{reason}" detail
              ) ]
        | TestsErrored reason ->
            // NOT a test failure (no test was shown to fail) and NOT a pass
            // (nothing was verified) — an honest "aborted" diagnostic so the
            // verdict is non-green without the misleading "Tests failed in X".
            //
            // It is reported at `HostAborted` SEVERITY, which is what makes that honesty
            // reach the verdict. At `Error` it was counted by `failingDiagnostics`,
            // and every surface downstream — the exit code, `verdict.json`'s outcome,
            // the terminal — then said "failures found" about a run in which nothing
            // failed. The severity routes it to `CheckOutcome.RunnerAborted`/exit 2
            // instead, exactly as `Deferred` routes a defer to `WaitingOnBuild`.
            //
            // It still joins the OUTSTANDING list, so cache participation stays
            // refused and an abort is never replayed as a green: the severity governs
            // the VERDICT, the outstanding list governs the CACHE.
            [ projectLevel (
                  ErrorLedger.ErrorEntry.abortedWithDetail
                      $"%s{project}: aborted — %s{reason}"
                      $"The %s{project} test host did not finish, so NO pass/fail verdict could be derived — nothing was verified. This is NOT a reported test failure and NOT a pass. Any per-test lines in the captured output are a TRANSCRIPT of a killed run, not findings: a test the host never reached is written out the same way as one that ran, which is why an abort must never be counted as failures. Re-run on a machine with headroom (e.g. `dotnet fshw test-rerun`). A run that only goes green on retry is itself a real failure, so this stays non-green."
              ) ]
        // No ledger entry, same as a pass. A filter matching nothing in THIS project is
        // not this project's error — the run-level verdict is where a workspace-wide
        // zero match is refused, and duplicating it here would put a
        // red on every project an ordinary impact selection happened not to name.
        | TestsNoMatch _
        | TestsPassed _ -> [])

/// Rewrite this plugin's whole slice of the error ledger to be exactly what is still
/// OUTSTANDING.
///
/// `ClearAllErrors` (== `ClearPlugin "test-prune"`) wipes the slate, and MUST always be
/// paired with a re-report of everything the plugin still owes: every outstanding red,
/// including ones carried from earlier runs this one did not cover, and every
/// unanalysable-file warning. Re-reporting only THIS run's findings
/// lets a narrower run erase reds it never executed, and drops the warning that is
/// supposed to deny the check its green verdict.
///
/// Clearing-and-re-reporting is the ONLY path to the ledger, so an entry can disappear
/// only by leaving the outstanding set: a red needs a run that COVERED it, a warning
/// needs the file to analyse cleanly.
let private reportOutstanding
    (ctx: PluginCtx<TestPruneMsg>)
    (unanalyzable: Map<string, UnanalyzableFile>)
    (failedExtensions: Map<string, string>)
    (outstanding: OutstandingFailure list)
    =
    ctx.ClearAllErrors()

    let failureEntries = outstanding |> List.map (fun f -> f.File, f.Entry)

    let unanalyzableEntries =
        unanalyzable
        |> Map.toList
        |> List.map (fun (_, u) -> u.File, unanalyzableFileDiagnostic u.RelPath u.Reason)

    let extensionEntries =
        failedExtensions
        |> Map.toList
        |> List.map (fun (name, reason) -> extensionLedgerKey name, extensionFailedDiagnostic name reason)

    failureEntries @ unanalyzableEntries @ extensionEntries
    |> List.groupBy fst
    |> List.iter (fun (file, entries) -> ctx.ReportErrors file (entries |> List.map snd))

let private flakinessHistoryPath (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-history.json")

/// What a run's `executeTests` needs to record traces: the run's trace settings and
/// mode, the decision function, the plugin's activity log and subtasks, and a way to wait
/// for the plugin to fold its symbol index.
type internal TraceRunHost =
    {
        TraceRuntime: TraceRuntime
        TraceWiring: TraceWiring
        TraceLog: string -> unit
        HoldSubtask: string -> string -> IDisposable
        /// The plugin's own folds of the symbol index, which the traces are joined against.
        IndexFold: IndexFold
    }

/// Execute test configs with optional affected classes for filtering. Handles beforeRun,
/// coveragePaths, process execution, result storage. `rawFilter` is a passthrough filter
/// string (from the run-tests command) that bypasses the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
///
/// `ctx = None` (a one-off command) fires no lifecycle events — the caller just gets the
/// final TestResults — and also disables the skip-on-stale shortcut, so a manual run is
/// never deadlocked by a stuck dirty bit. See the staleness branch below.
let private executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (emitStarted: TestRunStarted -> unit)
    (repoRoot: string)
    (launchDeadline: TimeSpan)
    // `tests.maxParallelProjects`: at most this many groups in flight, longest-expected
    // first; `None` launches every group at once.
    (maxParallelProjects: int option)
    // This session's lane of its repository host's process scheduler: each group holds
    // one of its slots while it runs (AUTOMATION-682). `None` for a per-worktree daemon.
    (processes: FsHotWatch.WorkScheduler.Lane option)
    // `tests.traces`, resolved for this run's mode, with the plugin's activity log (which
    // a force run's `ctx = None` would otherwise lose); `None` when not configured, which
    // leaves every launch exactly as it is without traces.
    (traces: TraceRunHost option)
    // The input tree the run was launched against, for the traces it records and the
    // receipt's re-binding once `beforeRun` is done.
    (launchTree: ReceiptTree option)
    // Which repo-relative paths a build consumed, observed once `beforeRun` finishes:
    // decides whether the receipt may re-bind to the tree the tests actually run against.
    (feedsCompile: unit -> string -> bool)
    // Receives the run id so the hook's own timings can be filed
    // under this run, beside the CTRF reports the verdict already reads.
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    // Where each step `beforeRun` runs is held while it runs: the plugin's subtasks, on
    // every path, so a wait or wedge inside the setup names the step it is on.
    (trackHookStep: HookStep.Tracker)
    (coveragePaths: (string -> CoveragePaths option) option)
    (coverageIngestFailed: CoverageIngestFailure -> unit)
    (afterRun: (TestResults -> unit) option)
    (configuredCoverageProjects: TestConfig list)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    // `checkSkipClasses` a project run whole leaves out, through its exclude template
    // (`buildExcludeArgs`). Empty for every run that skips nothing.
    (excludedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    // The test-evidence plan made at launch (`TestEvidenceRun.plan`): the projects this
    // run reuses instead of launching, and every key it publishes against. `None` runs
    // every selected project exactly as it would without a store.
    (evidence: TestEvidenceRun.Plan option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        // The run's own directory. Created NOW — before anything runs — so that a run
        // which executes but reports nothing is distinguishable from a run that never
        // happened: the first leaves an empty directory, the second leaves none.
        let runDir = Ctrf.runDir repoRoot runId

        try
            Directory.CreateDirectory(runDir) |> ignore
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn "test-prune" $"could not create the run directory %s{runDir}: %s{ex.Message}"

        let skipsClasses =
            excludedClassesByProject
            |> Map.exists (fun _ classes -> not (List.isEmpty classes))

        let isFilteredRun =
            not affectedClassesByProject.IsEmpty || Option.isSome rawFilter || skipsClasses

        // Every configured project launched, none of them filtered: a full run whatever
        // mode launched it.
        let runsEveryProjectInFull =
            Option.isNone rawFilter
            && not skipsClasses
            && configuredCoverageProjects
               |> List.forall (fun configured ->
                   configs |> List.exists (fun c -> c.Project = configured.Project)
                   && (affectedClassesByProject.IsEmpty
                       || affectedClassesByProject
                          |> Map.tryFind configured.Project
                          |> Option.exists List.isEmpty))

        let traces =
            traces
            |> Option.map (fun host ->
                { host with
                    TraceRuntime =
                        { host.TraceRuntime with
                            RunsEveryProjectInFull = runsEveryProjectInFull } })

        // Said once per run, loudly: every project below then stores and logs the same
        // reason as its refusal.
        traces
        |> Option.bind (fun host -> TraceRun.unrecordedReason host.TraceRuntime)
        |> Option.iter (fun reason -> Logging.warn "test-prune" $"traces: this run records none — %s{reason}")

        let primaryLabel =
            if isFilteredRun then
                $"running %d{configs.Length} selected test projects"
            else
                $"running full suite (%d{configs.Length} projects)"

        let startedAt = DateTime.UtcNow

        ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
        // `TestRunStarted` is emitted by the CALLER (which receives `started` in the
        // returned tuple), so the synchronous `TestsFinished` handler can fire it inside
        // the cache-write capture window.
        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }
        emitStarted started

        // The tree this run's receipt binds to: re-read once `beforeRun` has done its
        // generating, so its own output does not count as an edit made mid-run.
        let boundTree =
            match beforeRun with
            | Some setup ->
                Logging.info "test-prune" "Running beforeRun setup..."
                setup runId trackHookStep
                Logging.info "test-prune" "beforeRun complete"

                let binding, why =
                    ReceiptInputTree.afterBeforeRun (feedsCompile ()) launchTree (ReceiptInputTree.readTree repoRoot)

                why |> Option.iter (Logging.info "test-prune")
                binding
            | None -> launchTree

        let testHostStartedAt = DateTime.UtcNow

        let groups = configs |> List.groupBy (fun c -> c.Group)

        let coveragePathsByProject =
            configuredCoverageProjects
            |> List.choose (fun config ->
                coveragePaths
                |> Option.bind (fun pathsForProject -> pathsForProject config.Project)
                |> Option.map (fun paths -> config.Project, paths))
            |> Map.ofList

        let enabledRatchetProjects =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.choose (fun (project, paths) -> if paths.IncludeInRatchet then Some project else None)
            |> Set.ofSeq

        let configuredCoverageOutput =
            coveragePathsByProject
            |> Map.toSeq
            |> Seq.tryHead
            |> Option.map (fun (_, paths) -> paths.Cobertura)

        // Impact analysis may have decided a project has no affected classes. Such a
        // project never launches, so it is neither preflighted (no artifacts worth
        // walking) nor deferred (nothing was going to run).
        let skipProjectOf (config: TestConfig) =
            not affectedClassesByProject.IsEmpty
            && not (affectedClassesByProject |> Map.containsKey config.Project)

        /// What an impact-skipped project reports. Skipped-due-to-impact-analysis is the
        /// strongest form of filtering — hence `wasFiltered = true`, so no caller mistakes
        /// it for a full run — and its coverage contribution is "nothing new". Elapsed is
        /// Zero because the test runner never started.
        ///
        /// Beside `skipProjectOf` rather than at either call site: the refusal path and the
        /// run loop both answer for skipped projects, and they have to answer the same way.
        let skippedResultOf (config: TestConfig) =
            config.Project, TestsPassed("", true, TimeSpan.Zero)

        // Cumulative results built up as groups complete. Mutable under a lock
        // so concurrent group completions see a consistent prefix-chain. Per-
        // group deltas are emitted via TestProgress; the final cumulative is
        // carried by TestRunCompleted (and returned to non-daemon callers).
        let mutable cumulative: Map<string, TestResult> = Map.empty
        let accumulatorLock = obj ()

        // Raw-cobertura ingest inputs collected across the parallel per-project
        // runs. Each entry retains the producing project and ratchet intent.
        // Ingest+emit runs SERIALLY after
        // Async.Parallel completes so concurrent group completions never race on
        // the DB write or the single shared output file.
        let mutable coverageInputs: CoverageInput list = []
        let mutable coverageReceiptFailures: CoverageIngestFailure list = []
        let coverageRawPathsLock = obj ()

        // Per-test flakiness records, COLLECTED here and written ONCE after the
        // parallel section — exactly like `coverageRawPaths` above, and for both of
        // the same reasons. `Flakiness.appendRecords` is a full parse + full rewrite
        // of the whole history file, so a per-config call from inside the parallel
        // group body would mean one parse+rewrite cycle per project, AND a
        // read-modify-write racing itself across parallel groups (two projects
        // finishing together each load the same history, and the second writer drops
        // the first's records).
        let mutable flakinessRecords: Flakiness.TestRunRecord list = []
        let flakinessLock = obj ()

        // Each launched project's trace decision, ingested SERIALLY after the parallel
        // section, beside coverage and for the same reason: one writer to one store.
        let mutable traceRuns: TracedProjectRun list = []
        let traceRunsLock = obj ()

        /// Write a line to the plugin's activity log when there is a host to write to.
        /// One binding for the whole run: the preflight, the refusal path and the per-
        /// config runner all report through it, and it is allocated once rather than
        /// per config.
        let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

        let foldAndEmit (groupOutput: (string * TestResult) list) =
            lock accumulatorLock (fun () ->
                for (k, v) in groupOutput do
                    cumulative <- Map.add k v cumulative

                ctx
                |> Option.iter (fun c ->
                    c.EmitTestProgress
                        { RunId = runId
                          NewResults = Map.ofList groupOutput }))

        // STALE-ARTIFACT PREFLIGHT. The freshness question is pure
        // file I/O and is answerable in seconds, so it is asked about EVERY config
        // HERE — before a single suite launches — rather than inside the parallel
        // per-config body, where a group-A project wrote its CTRF before group B was
        // examined and the refusal surfaced three minutes into a partial run that
        // read like progress.
        //
        // The preflight also REPAIRS what is provably repairable (a build-output copy
        // whose origin exists on disk), re-verifies the bytes afterwards, and records
        // every repair to a durable ledger that trips a circuit breaker on repetition.
        // See `StaleArtifactPreflight` for why exactly one stale case is healed.
        // TEST EVIDENCE. A project the plan reuses is not launched: its stored CTRF report
        // is filed in this run's directory, its stored coverage where the run would have
        // written it, and it reports a green that names where it was earned. A project
        // whose coverage the gate grades is reused only when the entry carries it.
        let reusable: Map<string, TestEvidenceStore.Entry> =
            match evidence with
            | None -> Map.empty
            | Some plan ->
                plan.Reused
                |> Map.filter (fun project entry ->
                    configs |> List.exists (fun c -> c.Project = project && not (skipProjectOf c))
                    && (not (coveragePathsByProject.ContainsKey project) || Option.isSome entry.Coverage))

        let mutable materialized: Map<string, TestEvidenceStore.Entry> = Map.empty
        let materializedLock = obj ()

        /// File `entry` as this run's evidence for `config`; `false` when it could not be
        /// filed, and then the project is launched like any other. A filed project reports
        /// NOTHING until the completion re-check has confirmed it (`TestEvidenceRun.reconcile`):
        /// no progress subscriber may see a green the re-check could still withdraw.
        let tryReuse (config: TestConfig) : bool =
            Map.tryFind config.Project reusable
            |> Option.exists (fun entry ->
                try
                    Directory.CreateDirectory runDir |> ignore

                    File.WriteAllText(
                        Path.Combine(runDir, config.Project + Ctrf.ReportSuffix),
                        TestEvidenceRun.reusedReport repoRoot entry
                    )

                    match Map.tryFind config.Project coveragePathsByProject, entry.Coverage with
                    | Some paths, Some blob ->
                        let content = TestEvidenceStore.rebind repoRoot blob.Content
                        // Where a launch would have written it; a fresh checkout has no
                        // such directory until something writes there.
                        Directory.CreateDirectory(Path.GetDirectoryName paths.Baseline) |> ignore
                        File.WriteAllText(paths.Baseline, content)
                        File.WriteAllText(runCoveragePath runDir config.Project, content)

                        lock coverageRawPathsLock (fun () ->
                            coverageInputs <-
                                coverageInput config.Project paths.IncludeInRatchet paths.Baseline
                                :: coverageInputs)
                    | _ -> ()

                    lock materializedLock (fun () -> materialized <- Map.add config.Project entry materialized)

                    let line =
                        $"%s{config.Project}: %s{TestEvidenceRun.Provenance.describe (TestEvidenceRun.Provenance.ofEntry entry)}"

                    Logging.info "test-prune" line
                    logToCtx line
                    true
                with
                | :? IOException
                | :? UnauthorizedAccessException
                | :? System.Text.Json.JsonException as ex ->
                    Logging.warn
                        "test-prune"
                        $"%s{config.Project}: could not file reused evidence (%s{ex.Message}) — launching it instead"

                    TestEvidenceRun.deleteQuietly (Path.Combine(runDir, config.Project + Ctrf.ReportSuffix))
                    false)

        // Every project that may launch is preflighted, a reusable one included: its filing
        // can still fail, and then it launches like any other.
        let runnableConfigs = configs |> List.filter (fun c -> not (skipProjectOf c))

        let preflightTargets =
            runnableConfigs
            |> List.choose (fun c -> deriveProjectBin c.Args repoRoot |> Option.map (fun t -> c.Project, t))

        // THE FLOOR. `List.choose` above DROPS every config whose build-output target
        // could not be derived, and a dropped project is indistinguishable downstream
        // from a project that was checked and found fresh — so a derivation that
        // regressed would silently reduce this gate to checking nothing while every run
        // stayed green. Say what was covered; see `coverageReport` for why it reports
        // instead of refusing.
        match
            StaleArtifactPreflight.coverageReport
                (runnableConfigs |> List.map (fun c -> c.Project))
                (preflightTargets |> List.map fst)
        with
        | Some gap ->
            Logging.warn "test-prune" gap
            logToCtx gap
        | None -> ()

        let preflight = StaleArtifactPreflight.run repoRoot DateTime.UtcNow preflightTargets

        for repaired in preflight.Healed do
            logToCtx $"repaired stale build output before running: {repaired}"

        // ALL-OR-NOTHING on a refusal, as two named alternatives. A run whose tree is
        // provably not built cannot reach a green verdict, so launching the projects that
        // happen to be fresh buys minutes of partial execution for signal the verdict
        // cannot use — which is the "reads like progress" half of the defect.
        //
        // ADR-016 kept this and changed the layer below it: the refusal
        // is still run-wide, but the preflight now repairs every copy its breaker did not
        // name, so a refused run leaves a better tree than it found and the refusal set
        // shrinks run over run. `preflight.Healed` above can therefore be non-empty on
        // this path — repairs happened, the launch did not.
        //
        // Both arms are bound as functions rather than inlined into the `if`, so the
        // 390-line group loop keeps the indentation it has always had. A run-level
        // guard should cost one line here, not re-flow every line it guards.

        /// Nothing spawns. Every configured project comes back deferred, naming its own
        /// reason where the preflight found one and the run-wide cause where it did not.
        let refuseWholeRun () =
            async {
                let refused =
                    preflight.Refusals |> List.map (fun r -> r.Project, r.Reason) |> Map.ofList

                // Every affected project, named in full — the headline may be
                // shortened by a fixed-width surface, but this list never is.
                let names =
                    preflight.Refusals |> List.map (fun r -> r.Project) |> String.concat ", "

                for refusal in preflight.Refusals do
                    Logging.warn "test-prune" $"%s{refusal.Project}: %s{refusal.Reason}"
                    logToCtx $"{refusal.Project}: waiting on build ({refusal.Reason})"

                let results =
                    configs
                    |> List.map (fun config ->
                        if skipProjectOf config then
                            skippedResultOf config
                        else
                            match refused.TryFind config.Project with
                            | Some reason -> config.Project, TestsDeferred reason
                            | None ->
                                config.Project,
                                TestsDeferred
                                    // The remedy points at the projects
                                    // that carry one rather than restating a generic
                                    // build command. Each named project's own deferral
                                    // holds the remedy for ITS cause, and those causes
                                    // need different actions — a raw `dotnet build`
                                    // fixes neither a tripped repair breaker nor a
                                    // byte-differing copy, and some repositories refuse
                                    // that command outright.
                                    $"not run — the whole run was refused before any suite launched because \
                                      %d{preflight.Refusals.Length} project(s) have stale build output: \
                                      %s{names}. Remedy: read those projects' own deferrals — each names its \
                                      cause and what to do about it — then re-run.")

                foldAndEmit results
                return [| results |]
            }

        // `tests.maxParallelProjects`: with a cap, groups launch longest expected wall first
        // (from the retained CTRF reports), so the long ones are not left for last. Without
        // one every group launches at once and the order is moot, so no history is read.
        let scheduledGroups =
            match maxParallelProjects with
            | None -> groups
            | Some cap ->
                let ordered =
                    GroupSchedule.longestFirst
                        (fun (_, groupConfigs: TestConfig list) -> groupConfigs |> List.map (fun c -> c.Project))
                        (GroupSchedule.expectedWalls repoRoot)
                        groups

                let order = ordered |> List.map fst |> String.concat ", "
                Logging.info "test-prune" $"launching at most %d{cap} test group(s) at once, in order: %s{order}"
                ordered

        /// Launch one project as configured and classify what it did: the per-project body
        /// of the run, shared by the first launch and a starvation re-run. Returns the
        /// project's result and its CTRF report text (when one was requested and read).
        let launchProjectOrRaise
            (runToken: Threading.CancellationToken)
            (config: TestConfig)
            : Async<TestResult * string option> =
            async {
                // Collect extra args (filter + coverage) to append
                let extraArgs = ResizeArray<string>()

                // A project the selection runs whole may still leave out its
                // `checkSkipClasses`; a class filter, when there is one, already names
                // every class that runs.
                let filterArgs =
                    buildFilterArgs config affectedClassesByProject
                    |> Option.orElseWith (fun () ->
                        excludedClassesByProject
                        |> Map.tryFind config.Project
                        |> Option.bind (buildExcludeArgs config))

                match filterArgs with
                | Some f -> extraArgs.Add(f)
                | None -> ()

                // Raw passthrough filter (from run-tests command)
                match rawFilter with
                | Some f -> extraArgs.Add(f)
                | None -> ()

                let wasFiltered = Option.isSome filterArgs || Option.isSome rawFilter

                // Resolve per-project coverage paths (if coverage is configured for
                // this project). wasFiltered determines which file coverlet writes
                // to; the post-test step reads those files back to produce cobertura.
                let projectCoveragePaths = Map.tryFind config.Project coveragePathsByProject

                let projectCoverageLaunch =
                    projectCoveragePaths
                    |> Option.map (fun paths ->
                        let rawPath = if wasFiltered then paths.Partial else paths.Baseline

                        let launch =
                            prepareCoverageArtifact config.Project paths.IncludeInRatchet wasFiltered rawPath

                        extraArgs.Add(buildCoverageArgs paths wasFiltered)
                        launch)

                // xUnit's runner supports a major-specific CTRF switch, which fshw
                // reads back as the AUTHORITATIVE pass/fail verdict (and for
                // flakiness history). An UNSUPPORTED `--report-*` flag is
                // FATAL (the runner exits "invalid command line" and runs
                // zero tests), so injection is scoped: `Disabled` never
                // injects, `Ctrf` always does, `AutoDetect` injects iff the
                // runner's restored package graph resolves to a supported xUnit
                // major. Forced `Ctrf` keeps the xUnit 3 switches as its fallback
                // for explicitly-configured custom runners, but uses the resolved
                // xUnit 4 switches whenever the project graph identifies v4.
                let ctrfRunnerFamily =
                    match config.ReportVerificationFormat with
                    | Disabled -> None
                    | Ctrf -> detectCtrfRunnerFamily config.Args repoRoot |> Option.orElse (Some Xunit3)
                    | AutoDetect -> detectCtrfRunnerFamily config.Args repoRoot

                // ONE DIRECTORY PER RUN. A run's reports live in
                // `.fshw/test-runs/<runId>/` and nothing else does, so membership is
                // a fact about where a file IS, never an inference from its mtime.
                //
                // The run-dir is created whether or not any project reports, so an
                // executed run that produced nothing leaves an EMPTY DIRECTORY —
                // distinguishable from a run that never happened.
                let ctrfPath =
                    match ctrfRunnerFamily with
                    | Some family ->
                        Directory.CreateDirectory(runDir) |> ignore
                        // The dir already names the run, so the file need only name
                        // the project. No guid to guess at, nothing to parse.
                        let ctrfName = $"{config.Project}{Ctrf.ReportSuffix}"

                        extraArgs.Add(ctrfArguments family ctrfName runDir)

                        // A result line per test on the console, so a host killed before it
                        // writes this report still leaves its classes in the profile.
                        detailedOutputArgument config.Args |> Option.iter extraArgs.Add

                        // xUnit's report of a test still running past a threshold, so a host
                        // killed at its timeout still names the test it was running. Its config
                        // is the project's own with diagnostic messages on; a project whose
                        // config cannot be found or read gets nothing injected. Only for a project
                        // that asks (`nameHungTests`): xUnit's monitor holds a pool thread for the
                        // whole run, which a diagnostic must not impose (AUTOMATION-1353).
                        let ownConfig =
                            if config.NameHungTests then
                                projectTestConfigFor config.Args repoRoot
                            else
                                None

                        match ownConfig with
                        | Some own ->
                            match diagnosticsTestConfig own with
                            | Ok text ->
                                let configPath = Path.Combine(runDir, $"{config.Project}.testconfig.json")

                                match
                                    longRunningArguments config.Args configPath (longRunningSeconds config.TimeoutSec)
                                with
                                | Some args ->
                                    File.WriteAllText(configPath, text)
                                    extraArgs.Add args
                                | None -> ()
                            | Error why ->
                                Logging.info
                                    "test-prune"
                                    $"%s{config.Project}: not asking xUnit for long-running reports — its testconfig.json %s{why}"
                        | None -> ()

                        Some(Path.Combine(runDir, ctrfName))
                    | None -> None

                let finalArgs =
                    if extraArgs.Count > 0 then
                        let extra = String.concat " " extraArgs
                        $"%s{config.Args} %s{extra}"
                    else
                        config.Args

                // TRACES. Decided here, after the preflight certified the build
                // output fresh and before the spawn: a traced project launches its
                // woven copy; a refused one launches exactly as configured, and the
                // refusal is stored with its reason after the run.
                let decided =
                    match traces with
                    | None -> Untraced None
                    | Some host ->
                        // Weaving and JIT verification can take minutes: held as a
                        // subtask, so a wait line names it rather than the run.
                        use _ =
                            host.HoldSubtask $"{config.Project}:traces" $"preparing traces for {config.Project}"

                        host.TraceWiring.Decide
                            runToken
                            host.TraceRuntime
                            { Project = config.Project
                              Command = config.Command
                              Args = config.Args
                              Environment = config.Environment
                              Target = deriveProjectBin config.Args repoRoot
                              CtrfPath = ctrfPath
                              TraceWeave = host.TraceWiring.WeaveOverrides.TryFind config.Project }
                            runDir
                            (List.ofSeq extraArgs)

                match decided with
                | Untraced(Some reason) ->
                    Logging.warn
                        "test-prune"
                        $"%s{config.Project}: not tracing this run, launching as configured — %s{reason}"
                | Traced(spec, _) ->
                    Logging.info "test-prune" $"%s{config.Project}: tracing, launching %s{spec.Command}"
                | Untraced None -> ()

                // A ref: the untraced retry below replaces it from inside `runOnce`.
                let traceDecision = ref decided

                Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

                let timeoutSpan =
                    match config.TimeoutSec with
                    | Some s -> TimeSpan.FromSeconds(float s)
                    | None -> System.Threading.Timeout.InfiniteTimeSpan

                let projectSw = Stopwatch.StartNew()

                // THE RUN LOG. Opened for EVERY project on every
                // run, before the spawn — which project will need explaining is not
                // knowable in advance and the artifact costs a file handle.
                //
                // STREAMED, not buffered (see `RunLog`): the failure that needs it
                // most is the suite SIGKILLed at its timeout, which reaches no
                // writer at all and whose in-memory capture the kill truncates.
                let runLog = RunLog.openFor runDir config.Project

                match runLog.Ref with
                | RunLog.Ref.Written path ->
                    Logging.info "test-prune" $"%s{config.Project}: streaming run output to %s{path}"
                | RunLog.Ref.Unavailable reason ->
                    Logging.warn
                        "test-prune"
                        $"%s{config.Project}: NOT saving a run log — %s{reason}. The run proceeds; only \
                      the console tail will be available if it fails."

                // THE PROFILE (AUTOMATION-1300). Every project, every run, streamed beside
                // the run log: the load and the CPU time of the host's tree while it runs, its wall
                // time, and each class's span and the load over it — from the report, or, for a host
                // killed at its timeout (which writes none), from the result lines it printed.
                // This record is fshw's, so it survives the kill. See `Profile`.
                // The host's tree, read at each sample: its CPU time, and at the end its child
                // processes by command.
                let processTree = Profile.processTree ()

                let profile =
                    Profile.start
                        runDir
                        config.Project
                        config.TimeoutSec
                        Environment.ProcessorCount
                        OperationWatchdog.loadAverage
                        processTree.Cpu
                        Profile.DefaultInterval

                Profile.trackChildren profile processTree.Children

                // Every path out of this launch closes the profile and stops its sampler:
                // the classified outcome below finishes it first (`finish` writes once), so
                // this lands only on a path that never classified one — a cancelled run, or
                // an exception between the spawn and the verdict.
                use _profileClosed =
                    { new IDisposable with
                        member _.Dispose() =
                            Profile.finish
                                profile
                                "abandoned"
                                (Error "no report: the run ended before this project's outcome was classified") }

                // The output goes to the run log and, chunk by chunk, to the profile, which
                // keeps the per-test result lines a killed host's classes are read from. The
                // profile first: it never throws, and a throwing sink is disabled whole.
                let outputSink =
                    match runLog.Ref with
                    | RunLog.Ref.Written _ ->
                        Some(fun chunk ->
                            Profile.observe profile chunk
                            runLog.Write chunk)
                    | RunLog.Ref.Unavailable _ -> Some(Profile.observe profile)

                // A test runner STREAMS (discovery banner, progress, per-test
                // lines), so its first byte is a sound liveness proof and the
                // launch deadline can bound the spawn even when the config sets
                // no TimeoutSec at all.
                //
                // A host that overruns is asked to stop before it is killed
                // (`TestHostStop`); the teardown is kept for the timeout marker.
                let spawnedAt = ref DateTime.UtcNow
                let lastTeardown: TreeTeardown option ref = ref None

                let launch (decision: TraceDecision) =
                    async {
                        let command, args, environment =
                            TraceRun.launchOf decision (config.Command, finalArgs, config.Environment)

                        spawnedAt.Value <- DateTime.UtcNow

                        let outcome, teardown =
                            runProcessToStopping
                                TestHostStop
                                outputSink
                                (Profile.spawned profile)
                                command
                                args
                                repoRoot
                                environment
                                (ProcessBounds.streaming timeoutSpan launchDeadline)

                        lastTeardown.Value <- teardown
                        return outcome
                    }

                let runOnce = async { return! launch traceDecision.Value }

                // A traced launch that verified nothing is repeated untraced,
                // unless it timed out (see `TraceRun.untracedRetry`): tracing
                // cannot turn a crash red, and a timeout is not run twice.
                let runOnceTracedOrNot =
                    async {
                        let announce followUp =
                            let reason, seam =
                                match followUp with
                                | RelaunchUntraced reason ->
                                    reason,
                                    "the traced launch failed without writing a test report; relaunching \
                                     untraced. Everything above is the TRACED attempt, everything below the \
                                     untraced one."
                                | KeepTimeout reason ->
                                    reason,
                                    "the traced launch was killed at the project timeout; not relaunching \
                                     untraced, so the timeout is this project's outcome. `\"traces\": false` \
                                     on the project runs it untraced."

                            Logging.warn "test-prune" $"%s{config.Project}: %s{reason}"
                            RunLog.note runLog seam

                        let! outcome, decision =
                            TraceRun.launchTracedOrNot
                                traceDecision.Value
                                (fun () -> ctrfPath |> Option.exists File.Exists)
                                announce
                                launch

                        traceDecision.Value <- decision
                        return outcome
                    }

                // See `tryApphostPresent`; `looksLikeApphostMissing` is the
                // fallback for a command with no derivable project.
                let detectApphostMissing (outcome: ProcessOutcome) : bool =
                    // A clean exit means the apphost ran — never a
                    // launch-ordering problem, regardless of artifacts.
                    if isSucceeded outcome then
                        false
                    else
                        match tryApphostPresent config.Args repoRoot with
                        | Some present -> not present
                        | None ->
                            // Not derivable — fall back to the text sniff.
                            match outcome with
                            | ProcessOutcome.Failed(_, out) -> looksLikeApphostMissing (ProcessOutput.text out)
                            | _ -> false

                // Cold-start apphost-missing retry. The BuildCompleted→TestPrune
                // ordering already gates the launch on a successful build, but a
                // narrow race can still fire `--no-build` before the apphost
                // lands. Retry ONCE after a short wait; a still-missing apphost
                // is DEFERRED ("waiting on build"), never FAILED.
                let runTestWithRetry =
                    async {
                        let! first = runOnceTracedOrNot

                        if detectApphostMissing first then
                            Logging.warn
                                "test-prune"
                                $"%s{config.Project}: apphost missing at launch (build not settled yet); retrying once after a short wait"

                            // Both attempts stream into the SAME log, so mark the
                            // seam — otherwise two runs read as one confusing run.
                            RunLog.note
                                runLog
                                "apphost missing at launch; relaunching once. Everything above is the \
                             FIRST attempt, everything below the second."

                            do! Async.Sleep 750
                            let! second = runOnce
                            return second
                        else
                            return first
                    }

                // `finally`, not a close after the bind: a launch stall RE-RAISES
                // out of this block, and a run log whose handle leaked on the one
                // path where the child never came back is a log of nothing.
                let! processResult =
                    async {
                        try
                            try
                                let! outcome =
                                    match ctx with
                                    | Some c ->
                                        PluginCtxHelpers.withSubtask
                                            c
                                            config.Project
                                            $"testing {config.Project}"
                                            runTestWithRetry
                                    | None -> runTestWithRetry

                                // A host torn down at its timeout wrote no report and its
                                // log ends wherever the kill caught it: say why, in the
                                // log and beside it (`TimeoutMarker`).
                                match outcome with
                                | ProcessOutcome.TimedOut(after, _, kill) ->
                                    let facts: TimeoutMarker.Facts =
                                        { Project = config.Project
                                          After = after
                                          SpawnedAt = spawnedAt.Value
                                          Kill = kill
                                          Teardown = lastTeardown.Value }

                                    RunLog.note runLog (TimeoutMarker.message facts)
                                    TimeoutMarker.write runDir facts
                                | _ -> ()

                                return outcome
                            with LaunchStalledException reason ->
                                // The watchdog killed a child that never showed a
                                // sign of life within the launch deadline. Re-raise
                                // NAMING the config and elapsed, for `launchProject`
                                // to record as this project's `TestsErrored` result: a
                                // launch stall means this project NEVER RAN, so it is
                                // never a pass and `check` exits non-green rather than
                                // wedging at Running, while the rest of the run keeps
                                // what it verified. A child
                                // that EXITS is not a stall — the poll observes the
                                // exit and classifies it normally.
                                Profile.finish profile "launch-stalled" (Error $"no report: %s{reason}")

                                return
                                    raise (
                                        LaunchStalledException
                                            $"%s{config.Project}: %s{reason} (after %.0f{projectSw.Elapsed.TotalSeconds}s)"
                                    )
                        finally
                            // The pumps are done by the time `runProcessTo`
                            // returns (it drains them), so nothing is still
                            // writing when this closes.
                            runLog.Close()
                    }

                projectSw.Stop()
                let projectElapsed = projectSw.Elapsed

                let apphostMissing = detectApphostMissing processResult

                // A filtered run that matched zero tests in this project is
                // not a failure (see `isZeroTestsUnderFilter`) — treat it
                // like an impact-skip.
                let zeroTestsUnderFilter = isZeroTestsUnderFilter wasFiltered processResult

                let output = outputOf processResult

                // Read the structured report ONCE (when one was requested
                // from a capable runner). It is BOTH the authoritative
                // pass/fail signal (summary counts) AND the flakiness
                // source (per-test records). Read BEFORE the verdict so
                // the REPORT — not the exit code — decides green/red.
                let reportJson =
                    match ctrfPath with
                    | Some p ->
                        try
                            Some(File.ReadAllText p)
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> None
                    | None -> None

                let reportEvidence =
                    match ctrfPath, reportJson with
                    | None, _ -> NoReportRequested
                    | Some _, Some json -> ReportRequested(Ctrf.tryVerdictReport json)
                    | Some p, None -> ReportRequested(Error $"no readable report at %s{p}")

                let result =
                    if apphostMissing then
                        // Tests NEVER RAN — the apphost wasn't produced.
                        // A dedicated `TestsDeferred`, NOT a pass
                        // (`verdict` = `NothingVerified` → never a silent
                        // false-green) and NOT a real failure: surfaced as an honest
                        // "waiting on build" diagnostic. Carries no
                        // elapsed/wasFiltered, so it never lowers a
                        // coverage baseline.
                        TestsDeferred "apphost not produced; tests did not run"
                    elif zeroTestsUnderFilter then
                        // Not a failure — per project, a filter selecting
                        // nothing is not that project's fault, so it is never
                        // reported as one. Its own case (rather than a passing
                        // result wearing a marker prefix) so that every fold
                        // must decide it; see `TestResult.verdict` and
                        // `verificationOf`.
                        TestsNoMatch(output, projectElapsed)
                    else
                        classifyTestOutcome reportEvidence wasFiltered projectElapsed processResult

                Profile.finish
                    profile
                    (Profile.outcomeOf result)
                    (match ctrfPath, reportJson with
                     | Some _, Some json -> Ok(Profile.classTimings json)
                     | None, _ -> Error "no report: this runner was not asked for CTRF"
                     | Some _, None ->
                         match result with
                         | TestsTimedOut(_, after, _, _) ->
                             Error $"no report: the host was killed at its %d{int after.TotalSeconds}s timeout"
                         | _ -> Error "no report: the host wrote none")

                // Log driven off the AUTHORITATIVE verdict (not the raw
                // exit code) so the console line can never disagree with
                // the recorded result.
                match result with
                | TestsDeferred _ ->
                    logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

                    Logging.warn
                        "test-prune"
                        $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (a build-ordering issue, never a test failure)"
                | TestsNoMatch _ ->
                    logToCtx $"{config.Project}: no tests matched the filter — skipped"

                    Logging.info
                        "test-prune"
                        $"%s{config.Project}: no tests matched the active filter — skipped, not FAILED (a filtered run that selects nothing here is not a test failure)"
                | TestsPassed _ ->
                    logToCtx $"{config.Project}: passed"
                    Logging.info "test-prune" $"%s{config.Project}: PASSED"
                | TestsErrored reason ->
                    logToCtx $"{config.Project}: ABORTED (nothing verified) — {reason}"

                    Logging.error
                        "test-prune"
                        $"%s{config.Project}: ABORTED — %s{reason} Nothing was verified; this is NOT a test failure and NOT a pass — re-run (e.g. `dotnet fshw test-rerun`)."
                | TestsFailed _ ->
                    logToCtx $"{config.Project}: failed"
                    Logging.error "test-prune" $"%s{config.Project}: FAILED"
                | TestsTimedOut(_, after, _, _) ->
                    logToCtx $"{config.Project}: timed out (infrastructure)"

                    Logging.error
                        "test-prune"
                        $"%s{config.Project}: TIMED OUT (infrastructure) after %d{int after.TotalSeconds}s"

                // Report the outcome in full. The two reports are DIFFERENT
                // documents on purpose: a failure report
                // counts the runner's `failed ...` lines and calls them
                // failures, which is true of a run that finished and false —
                // expensively, alarmingly false — of one that was killed
                // holding a half-written transcript.
                match result with
                | TestsFailed _
                | TestsTimedOut _ ->
                    let ctrfFailedRows =
                        reportJson |> Option.map failedRowsOfReport |> Option.defaultValue []

                    for line in
                        formatFailureReport
                            (RedRunEnding.ofResult result)
                            ctrfFailedRows
                            config.Project
                            runLog.Ref
                            output do
                        Logging.error "test-prune" line
                | TestsErrored reason ->
                    for line in formatAbortReport config.Project runLog.Ref reason output do
                        Logging.error "test-prune" line
                | TestsPassed _
                | TestsDeferred _
                | TestsNoMatch _ -> ()

                projectCoverageLaunch |> Option.iter (keepRunCoverage runDir)

                // Collect this project's raw runner cobertura for SERIAL
                // ingest after Async.Parallel (a parallel DB write +
                // shared-file write would race). A run that never executed
                // (apphost missing) contributes NO input, so a partial file
                // cannot lower coverage.
                match
                    projectCoverageLaunch
                    |> Option.map (coverageReceiptFromReceipt (TestResult.verifiedGreen result))
                with
                | Some(CoverageReceiptAccepted input) ->
                    lock coverageRawPathsLock (fun () -> coverageInputs <- input :: coverageInputs)
                | Some(CoverageReceiptFailed failure) ->
                    lock coverageRawPathsLock (fun () -> coverageReceiptFailures <- failure :: coverageReceiptFailures)
                | Some CoverageReceiptAbsent
                | None -> ()

                // Per-test flakiness tracking: reuse the report content
                // already read for the verdict; COLLECT this project's
                // per-test records for the single post-parallel write (see
                // `flakinessRecords`). Best-effort — exceptions never fail
                // the run.
                //
                // The report is RETAINED — the verdict file POINTS at these
                // reports rather than deleting them once their records are
                // folded into the flakiness history. `Ctrf.tidyRunsDir`
                // (post-run) keeps the newest few per project, so retention
                // stays bounded.
                match ctrfPath, reportJson with
                | Some _, Some json ->
                    try
                        let records = Flakiness.parseCtrfTests json

                        if not records.IsEmpty then
                            lock flakinessLock (fun () -> flakinessRecords <- flakinessRecords @ records)
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException
                    | :? JsonException as ex ->
                        Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"
                | Some p, None ->
                    // Report requested but unreadable (missing — the host
                    // aborted before flushing — or locked). Nothing to retain;
                    // its ABSENCE already drove the Errored verdict.
                    try
                        File.Delete p
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> ()
                | None, _ -> ()

                if Option.isSome traces then
                    let traced: TracedProjectRun =
                        { Project = config.Project
                          Decision = traceDecision.Value
                          Filtered = wasFiltered
                          CtrfPath = ctrfPath }

                    lock traceRunsLock (fun () -> traceRuns <- traced :: traceRuns)

                return result, reportJson
            }

        // STARVATION. A project that declared `starvation` is sampled while it runs; a
        // timeout on a starved box is held back from the group's results and re-run alone
        // once every group has finished (`rerunStarved`). See `Starvation`.
        let mutable starvedProjects: (TestConfig * Starvation.TimedOut * Starvation.Reading) list =
            []

        let starvedLock = obj ()

        /// `launchProjectOrRaise`, with a launch stall answered as THIS project's result.
        ///
        /// A stall ("launch produced no live process within Ns") means this project never
        /// ran. It used to raise out of the whole run, which then completed `Aborted` with
        /// no results at all: the projects that had run, or were still running, lost what
        /// they verified, and the next run ran every one of them in full again. Now the
        /// project is `TestsErrored` — nothing verified, never a pass, so the run is still
        /// not green — and every other project keeps its own result.
        let launchProject
            (runToken: Threading.CancellationToken)
            (config: TestConfig)
            : Async<TestResult * string option> =
            async {
                try
                    return! launchProjectOrRaise runToken config
                with LaunchStalledException reason ->
                    logToCtx $"{config.Project}: ABORTED (nothing verified) — {reason}"

                    Logging.error
                        "test-prune"
                        $"%s{config.Project}: ABORTED — %s{reason}. Nothing was verified for this project; the rest of the run keeps its results. Re-run when the box is quiet."

                    return TestsErrored reason, None
            }

        /// The classes the run filtered `config`'s project to; `None` when it runs in full
        /// or under a raw filter, whose classes are not known.
        let selectionOf (config: TestConfig) : string list option =
            match rawFilter, Map.tryFind config.Project affectedClassesByProject with
            | None, Some(_ :: _ as classes) -> Some classes
            | _ -> None

        /// `launchProject`, with the load sampled while it runs.
        let launchSampled (runToken: Threading.CancellationToken) (config: TestConfig) =
            async {
                let sampler =
                    Starvation.Sampler.start Starvation.Sampler.DefaultInterval OperationWatchdog.loadAverage

                let samples = ref []

                let! result, reportJson =
                    async {
                        try
                            return! launchProject runToken config
                        finally
                            samples.Value <- Starvation.Sampler.stop sampler
                    }

                let reading: Starvation.Reading =
                    { Cores = Environment.ProcessorCount
                      Samples = samples.Value }

                return result, reportJson, reading
            }

        /// Launch `config`'s project; `None` when it timed out under starvation and is
        /// held back for its re-run alone.
        let launchWatched (runToken: Threading.CancellationToken) (config: TestConfig) : Async<TestResult option> =
            async {
                match config.Starvation with
                | None ->
                    let! result, _ = launchProject runToken config
                    return Some result
                | Some policy ->
                    let! result, reportJson, reading = launchSampled runToken config

                    match Starvation.decide (Some policy) (selectionOf config) result reportJson reading with
                    | Starvation.RerunAlone(timedOut, firstReading) ->
                        let line =
                            $"%s{config.Project}: %s{Starvation.TimedOut.describe timedOut} under CPU starvation \
                              (%s{Starvation.Reading.describe policy firstReading}); re-running it ALONE once the \
                              rest of the run has finished — its re-run is the result reported"

                        Logging.warn "test-prune" line
                        logToCtx line

                        lock starvedLock (fun () ->
                            starvedProjects <- (config, timedOut, firstReading) :: starvedProjects)

                        return None
                    | Starvation.Report why ->
                        why
                        |> Option.iter (fun reason ->
                            Logging.info "test-prune" $"%s{config.Project}: starvation declared; %s{reason}")

                        return Some result
            }

        /// Re-run every held-back project, one at a time, after every group has finished:
        /// alone, as far as this run is concerned. The first attempt's report, log and
        /// timeout marker are set aside first, so the run directory's report for the
        /// project is the re-run's; `<Project>.starved.json` records both attempts.
        let rerunStarved () : Async<(string * TestResult) list> =
            async {
                let pending = lock starvedLock (fun () -> List.rev starvedProjects)
                let! runToken = Async.CancellationToken
                let mutable rerun = []

                for (config, timedOut, firstReading) in pending do
                    let policy =
                        config.Starvation |> Option.defaultValue (Starvation.Policy.rerunAlone [])

                    // `runCoveragePath`'s suffix: the copy of the raw coverage a run keeps.
                    Starvation.setAside
                        runDir
                        config.Project
                        [ Ctrf.ReportSuffix
                          RunLog.Suffix
                          TimeoutMarker.Suffix
                          Profile.Suffix
                          ".coverage.cobertura.xml" ]
                    |> ignore

                    // The first attempt's trace, if any, describes a run that is no longer
                    // the project's result.
                    lock traceRunsLock (fun () ->
                        traceRuns <- traceRuns |> List.filter (fun t -> t.Project <> config.Project))

                    // PREFLIGHTED LIKE ANY LAUNCH. The run-wide preflight certified this
                    // tree before the first attempt, not after it. A first attempt torn down
                    // with its whole tree takes the coverage controller with it before it puts
                    // back the assemblies it instrumented in this project's output, and a
                    // coverage run over an instrumented copy passes and writes an empty report
                    // (land entry 224: 56,724 -> 0 covered entries). The copies the build made
                    // are repaired from their origins first, and a tree this cannot certify
                    // is not launched: its re-run is deferred, never a pass.
                    let refusal =
                        deriveProjectBin config.Args repoRoot
                        |> Option.bind (fun target ->
                            let checkedTree =
                                StaleArtifactPreflight.run repoRoot DateTime.UtcNow [ config.Project, target ]

                            for repaired in checkedTree.Healed do
                                let line =
                                    $"%s{config.Project}: repaired %s{repaired} before its re-run alone (the first \
                                      attempt's teardown left it differing from the build's copy)"

                                Logging.warn "test-prune" line
                                logToCtx line

                            checkedTree.Refusals |> List.tryHead |> Option.map _.Reason)

                    let! result, reading =
                        match refusal with
                        | Some reason ->
                            async {
                                Logging.warn "test-prune" $"%s{config.Project}: not re-run alone — %s{reason}"
                                logToCtx $"{config.Project}: not re-run alone — {reason}"

                                let notLaunched: Starvation.Reading =
                                    { Cores = Environment.ProcessorCount
                                      Samples = [] }

                                return TestsDeferred reason, notLaunched
                            }
                        | None ->
                            async {
                                let! result, _, reading = launchSampled runToken config
                                return result, reading
                            }

                    let outcome =
                        match result with
                        | TestsPassed _ -> "passed"
                        | TestsFailed _ -> "failed"
                        | TestsTimedOut _ -> "timed out again"
                        | TestsErrored _ -> "aborted"
                        | TestsDeferred _ -> "was deferred"
                        | TestsNoMatch _ -> "matched no test"

                    let record: Starvation.Record =
                        { Project = config.Project
                          TimedOut = timedOut
                          FirstAttempt = firstReading
                          Rerun = reading
                          RerunOutcome = outcome
                          PassedAlone = TestResult.verifiedGreen result
                          AttemptDir = Path.Combine(runDir, Starvation.AttemptDirName) }

                    Starvation.write policy runDir record
                    let line = Starvation.summary record

                    if record.PassedAlone then
                        Logging.warn "test-prune" line
                    else
                        Logging.error "test-prune" line

                    logToCtx line
                    rerun <- (config.Project, result) :: rerun

                return List.rev rerun
            }

        /// The normal path: every target certified fresh, so the groups launch in
        /// parallel (at most `maxParallelProjects` at a time) and fold their results into
        /// the shared accumulator as they finish, in whatever order that is.
        let runAllGroups () =
            scheduledGroups
            |> List.map (fun (_, groupConfigs) ->
                // A group is charged the largest weight among its projects (ADR-053).
                FsHotWatch.WorkScheduler.JobSpec.weighted
                    (groupConfigs |> List.map _.Weight |> List.fold max 1.0)
                    FsHotWatch.WorkScheduler.JobSpec.interactive,
                async {
                    // The run's token: trace preparation (weave + JIT verification) stops
                    // when the run is cancelled.
                    let! runToken = Async.CancellationToken
                    let mutable results = []

                    for config in groupConfigs do
                        // FRESHNESS IS ALREADY SETTLED. The preflight above
                        // asked `ArtifactFreshness.stale` about every config in this run before
                        // the first spawn and refused the whole run if any answer was stale, so
                        // reaching this line means the bits match the sources. There is
                        // deliberately NO second freshness check here: a per-config gate inside
                        // the parallel body is precisely what let one group write its CTRF
                        // before another group's staleness had even been looked at.
                        //
                        // Template-based class filter (from impact analysis). When the map is
                        // non-empty but has no classes for this project, skip the project
                        // entirely (impact analysis found no relevant tests).
                        let filed = not (skipProjectOf config) && tryReuse config

                        match skipProjectOf config with
                        | true ->
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"
                            results <- skippedResultOf config :: results
                        | false when filed -> ()
                        | false ->
                            let! settled = launchWatched runToken config

                            match settled with
                            | Some result -> results <- (config.Project, result) :: results
                            | None -> ()

                    // Atomically fold this group's results into the shared
                    // accumulator and emit a cumulative snapshot. Groups that
                    // complete later will extend (never contradict) this one.
                    foldAndEmit results
                    return results
                })
            |> GroupSchedule.runAdmittedAs processes maxParallelProjects

        let! groupResults =
            if List.isEmpty preflight.Refusals then
                runAllGroups ()
            else
                refuseWholeRun ()

        // groupResults is the per-group return values; we ignore it because
        // `cumulative` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore

        // STARVATION: the held-back projects, re-run alone now that every group is done.
        let! rerunResults = rerunStarved ()

        if not rerunResults.IsEmpty then
            foldAndEmit rerunResults

        let rerunProjects = rerunResults |> List.map fst |> Set.ofList

        // TEST EVIDENCE, at completion. One fresh walk keys every candidate again. A reuse
        // whose key moved while the run was in flight no longer describes this tree: its
        // filed report and coverage are withdrawn and it reports a deferral, so this run
        // cannot go green on it and the next one launches it. A project that really ran
        // publishes its green only when it qualifies (`TestEvidenceRun.qualifies`).
        let reusedNow = lock materializedLock (fun () -> materialized)

        let reconciled =
            evidence
            |> Option.map (fun plan -> plan, TestEvidenceRun.reconcile plan repoRoot reusedNow)

        // A withdrawn reuse takes back everything it filed and reports a deferral; a held one
        // reports its green now, and only now.
        reconciled
        |> Option.iter (fun (_, r) ->
            for (project, reason) in r.Withdrawn do
                Logging.warn "test-prune" $"%s{project}: %s{reason}"
                logToCtx $"{project}: {reason}"
                TestEvidenceRun.deleteQuietly (Path.Combine(runDir, project + Ctrf.ReportSuffix))

                lock coverageRawPathsLock (fun () ->
                    coverageInputs <- coverageInputs |> List.filter (fun i -> i.Project <> project))

            let reported =
                (r.Withdrawn |> List.map (fun (project, reason) -> project, TestsDeferred reason))
                @ (r.Held
                   |> Map.toList
                   |> List.map (fun (project, entry) ->
                       project,
                       TestsPassed(
                           TestEvidenceRun.Provenance.describe (TestEvidenceRun.Provenance.ofEntry entry),
                           false,
                           TimeSpan.Zero
                       )))

            lock accumulatorLock (fun () ->
                for (project, result) in reported do
                    cumulative <- Map.add project result cumulative))

        let evidenceHeld: Map<string, TestEvidenceRun.Provenance> =
            reconciled
            |> Option.map (fun (_, r) -> r.Held |> Map.map (fun _ entry -> TestEvidenceRun.Provenance.ofEntry entry))
            |> Option.defaultValue Map.empty

        // Coverage: now that ALL projects have finished, serially ingest each
        // project's raw runner cobertura into the TestPrune DB (max-merged,
        // symbol-relative) and emit the FULL DB ONCE to the single shared
        // cobertura file. Done here, outside Async.Parallel, so there is no
        // DB-write contention and no file-write race on the shared output.
        let collectedInputs, receiptFailures =
            lock coverageRawPathsLock (fun () -> List.rev coverageInputs, List.rev coverageReceiptFailures)

        let receiptFailures =
            receiptFailures
            |> List.map (fun failure ->
                try
                    invalidateCoverageReceiptFailure db failure
                    failure
                with ex ->
                    { failure with
                        Reason =
                            $"%s{failure.Reason}; prior coverage invalidation failed (%s{ex.GetType().Name}: %s{ex.Message})" })

        let coverageOutcome =
            ingestAndEmitCoverageForProjects
                db
                repoRoot
                (runId.ToString("N"))
                enabledRatchetProjects
                configuredCoverageOutput
                collectedInputs

        // Coverage missing (AUTOMATION-1371): the prior rows were kept; the run says so.
        for missing in coverageOutcome.CoverageMissing do
            logToCtx $"%s{missing.Project}: %s{missing.Reason}; its prior runtime coverage is kept"

        let coverageFailures = receiptFailures @ coverageIngestFailures coverageOutcome

        if not coverageFailures.IsEmpty then
            for failure in coverageFailures do
                coverageIngestFailed failure

            lock accumulatorLock (fun () -> cumulative <- applyCoverageIngestFailures coverageFailures cumulative)

        // TEST EVIDENCE: publish. Only now are this run's own results final — a project
        // whose coverage did not ingest is no longer green — so only now may a green leave
        // this checkout.
        reconciled
        |> Option.iter (fun (plan, r) ->
            let finalNow = lock accumulatorLock (fun () -> cumulative)

            // A project re-run under starvation is not published: its green was earned on a
            // second attempt, which the stored evidence could not say.
            let runOf (candidate: TestEvidenceRun.Candidate) : TestEvidenceRun.ProducedRun option =
                Map.tryFind candidate.Project finalNow
                |> Option.filter (fun _ -> not (rerunProjects.Contains candidate.Project))
                |> Option.map (fun result ->
                    let coveragePath = runCoveragePath runDir candidate.Project

                    { Project = candidate.Project
                      Result = result
                      Report =
                        TestEvidenceRun.readIfPresent (Path.Combine(runDir, candidate.Project + Ctrf.ReportSuffix))
                      CoverageRequired = coveragePathsByProject.ContainsKey candidate.Project
                      Coverage =
                        TestEvidenceRun.readIfPresent coveragePath
                        |> Option.map (fun text -> coveragePath, text) })

            let producer: TestEvidenceStore.Producer =
                { Worktree = Path.GetFullPath repoRoot
                  RunId = runId.ToString("N")
                  TreeHash = launchTree |> Option.map _.Identity |> Option.defaultValue "unbound"
                  ProducerHash = plan.Wiring.ProducerHash
                  EarnedAt = DateTimeOffset.UtcNow }

            let written = TestEvidenceRun.publish plan repoRoot r reusedNow runOf producer
            let tally = TestEvidenceRun.tally plan r written
            Logging.info "test-prune" tally
            logToCtx tally)

        // Flakiness: ONE parse + ONE rewrite of the history file for the whole run,
        // with every project's records — not one per project, and not racing itself
        // from inside Async.Parallel. Best-effort: the history is a diagnostic, so a
        // failure to record it must never fail the run that produced it.
        let collectedFlakiness = lock flakinessLock (fun () -> flakinessRecords)

        if not collectedFlakiness.IsEmpty then
            try
                Flakiness.appendRecords (flakinessHistoryPath repoRoot) 20 collectedFlakiness
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

        // Traces: after every project finished, before `tidyRunsDir` can rotate the run
        // directory holding the dumps. `ingestAll` first waits, bounded, for this plugin's
        // mailbox to fold the analysis events admitted before now, so the traces join
        // against the index those events write. The folds never wait on this run, whose
        // result is posted only after it returns. It never throws, and nothing it does
        // reaches `cumulative`: a trace failure is a log line and a stored row, never a
        // changed result.
        match traces with
        | Some host ->
            let runs = lock traceRunsLock (fun () -> List.rev traceRuns)

            do!
                TraceRun.ingestAll
                    host.TraceRuntime
                    (TestPrune.Ports.toSymbolStore db)
                    host.IndexFold
                    (runId.ToString("N"))
                    (boundTree |> Option.map _.Identity)
                    (fun () -> ReceiptInputTree.read repoRoot)
                    runs
                    host.TraceLog
        | None -> ()

        // Bound what `.fshw/test-runs/` retains, and purge the DEAD `.log` format.
        // Runs AFTER this run's reports were written, so the
        // evidence the verdict is about to point at is always among the survivors.
        Ctrf.tidyRunsDir repoRoot Ctrf.RetainedRuns

        sw.Stop()

        let finalResults = lock accumulatorLock (fun () -> cumulative)

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // `TestRunCompleted` is emitted by the CALLER (the synchronous Custom handler) so
        // it lands in EmittedEvents for cache replay; returned in the tuple instead. The
        // matching start was emitted before any host could mutate its input artifacts.
        // `Outcome = Normal` means the run completed naturally — per-project pass/fail
        // lives in Results, and Aborted is reserved for cancellation/timeout/crash.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              Verification =
                verificationWithin
                    (configuredCoverageProjects |> List.map (fun c -> c.Project) |> Set.ofList)
                    finalResults }

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed, boundTree, testHostStartedAt, evidenceHeld
    }

/// Count of Error-severity diagnostics not suppressed (`FcsSuppression.blockingErrors`) —
/// used only for the skip-log message so operators have a number to correlate against the
/// FCS-error stream. `hasFcsErrors` is defined by it, so the count matches the gate.
let internal fcsErrorCount (suppression: Suppression) (state: FileCheckState) : int =
    match state with
    | FullCheck cr -> (blockingErrors suppression cr.Diagnostics).Length
    // The diagnostics the filed check reported (ADR-059 §17).
    | Replayed outputs -> (blockingErrors suppression (FileCheckResult.diagnosticsOfOutputs outputs)).Length
    | ParseOnly -> 0

/// FCS cache-poisoning gate. A `FileChecked` whose FCS result reports any
/// Error-severity diagnostic is untrustworthy: cold-start FCS sometimes returns
/// "expected type X but here has type X" for files that compile cleanly once warm, and
/// flushing those poisoned symbols overwrites the prior good DB snapshot. Gated by
/// SEVERITY, not message text, so the cold-start race and the user-broke-their-code case
/// are handled identically — both hold the prior DB row. `ParseOnly` (check aborted)
/// counts as "no observable errors".
///
/// `suppression` (`FileCheckResult.suppression`) is applied by
/// `FcsSuppression.blockingErrors`, so the gate applies the same filter as the
/// user-visible error stream in `Daemon.reportFcsDiagnostics` and the analysis evidence. Without that symmetry the
/// gate trips on codes the user has already silenced (e.g. FS1182 promoted to Error by
/// `<TreatWarningsAsErrors>` but suppressed via `#nowarn "1182"`), killing cache-replay
/// across daemon restarts on every cold scan.
let internal hasFcsErrors (suppression: Suppression) (state: FileCheckState) : bool =
    fcsErrorCount suppression state > 0

/// The label the test-impact DB write declares over itself: how much it writes and where,
/// so a wait or wedge line names the database rather than the plugin alone.
let internal dbWriteLabel (dbPath: string) (fileCount: int) =
    $"test-impact DB write: %d{fileCount} file(s) to %s{dbPath}"

/// The busy timeout the test-impact DB's connections carry: how long a write waits on a lock
/// another connection holds before SQLite refuses it. Read from a connection the database
/// opened, so the bound a blocked write reports is the one it ran out of, not a restatement.
let internal dbBusyTimeout (db: Database) : TimeSpan =
    use conn = db.OpenConnection()
    TimeSpan.FromSeconds(float conn.DefaultTimeout)

/// A test-impact DB write SQLite refused (`SQLITE_BUSY` 5 / `SQLITE_LOCKED` 6) because another
/// connection held the lock for the whole busy timeout (`dbBusyTimeout`) is re-raised naming
/// the database, the write, how long it waited and the bound that expired. SQLite's own
/// "database is locked" names none of them, and an expired busy timeout is a held lock, not a
/// wedge. Every other failure passes through untouched, so schema-drift repair still reads
/// the exception it raised.
let internal namedDbWriteFailure
    (dbPath: string)
    (busyTimeout: TimeSpan)
    (fileCount: int)
    (waited: TimeSpan)
    (ex: exn)
    : exn =
    match ex with
    | :? Microsoft.Data.Sqlite.SqliteException as sqlite when sqlite.SqliteErrorCode = 5 || sqlite.SqliteErrorCode = 6 ->
        TimeoutException(
            $"test-impact DB write blocked for %s{PluginWedge.formatElapsed waited}: SQLite busy timeout (%s{PluginWedge.formatElapsed busyTimeout}) expired — %s{dbPath} is locked by another connection, so %d{fileCount} file(s) were not written ({sqlite.Message}). "
            + "Another fshw daemon or CLI on the same .fshw/ directory, or any process holding a write transaction on that file, holds it.",
            ex
        )
        :> exn
    | other -> other

/// Flush accumulated per-file analysis results to the DB in a single RebuildProjects
/// call. Pure function: takes state, returns updated state.
///
/// The results go to `RebuildProjects` ONE PER FILE, never merged per project.
/// TestPrune.Core attributes each edge, test method and attribute to the file whose
/// `AnalysisResult` carried it, so re-indexing a file replaces exactly that file's facts.
/// A merged result carrying a signature (`.fsi`) and its implementation would credit the
/// signature's facts to whichever file declared the shared name last, and a later flush
/// of that file alone would delete them.
let private flushPendingAnalysis
    (db: Database)
    (dbPath: string)
    (busyTimeout: TimeSpan)
    (declare: string -> IDisposable)
    (state: TestPruneState)
    =
    let allResults = ResizeArray<AnalysisResult>()

    let mutable newPending = state.PendingAnalysis

    for projectName in state.PendingAnalysis |> Map.toList |> List.map fst do
        match Map.tryFind projectName newPending with
        | Some items ->
            newPending <- Map.remove projectName newPending
            Logging.info "test-prune" $"Flushing %d{items.Length} files for %s{projectName} to DB"
            allResults.AddRange(items)
        | None -> ()

    if allResults.Count > 0 then
        // Each file's analysis identity goes in the same transaction as its symbols, so
        // the index never claims an analysis it rolled back (`IndexedAnalysis`).
        //
        // Declared over itself, inside the impact selection's declaration: a write blocked on
        // a lock or crawling through contention then reads as THIS write, with its size, its
        // database and how far into its bound it is, rather than as a fold that never
        // returned. Timed and logged when it returns, so daemon.log shows where the time went.
        let label = dbWriteLabel dbPath allResults.Count
        let clock = Diagnostics.Stopwatch.StartNew()

        do
            use _declaration = declare label

            try
                db.RebuildProjects(Seq.toList allResults, fileKeys = Map.toList state.PendingIdentities)
            with ex ->
                match namedDbWriteFailure dbPath busyTimeout allResults.Count clock.Elapsed ex with
                | named when obj.ReferenceEquals(named, ex) -> reraise ()
                | named -> raise named

        Logging.info
            "test-prune"
            $"Wrote %d{allResults.Count} file(s) to %s{dbPath} in %s{PluginWedge.formatElapsed clock.Elapsed}"

    // Update in-memory snapshot so subsequent FileChecked reads see the
    // new symbols instead of hitting the DB mid-rebuild.
    let mutable newSnapshot = state.SymbolSnapshot

    for result in allResults do
        for (file, symbols) in result.Symbols |> List.groupBy (fun s -> s.SourceFile) do
            newSnapshot <- Map.add file symbols newSnapshot

    { state with
        PendingAnalysis = newPending
        SymbolSnapshot = newSnapshot }

/// Detect schema-drift errors (stale cache DB lacking a column the current
/// `TestPrune.Core` requires). These surface as SQLite "no such column" /
/// "no column named" messages. Deliberately pure / internal so the caller
/// can unit-test both branches without needing a corrupt DB on disk.
let internal looksLikeSchemaDrift (ex: exn) =
    let msg = ex.Message.ToLowerInvariant()
    msg.Contains("no such column") || msg.Contains("no column named")

/// If `ex` looks like schema drift, delete the cache DB at `dbPath` so the next run
/// rebuilds from scratch. The cache is derivative and safe to regenerate, and a user
/// should never have to know which file to delete.
let internal tryRepairSchemaDrift (dbPath: string) (ex: exn) =
    if looksLikeSchemaDrift ex && File.Exists dbPath then
        try
            // Delegate to TestPrune.Core — it owns the SQLite-sidecar
            // invariant. Deleting only the main file leaves stale `-wal` /
            // `-shm` sidecars that SQLite may try to "recover" against a
            // freshly created empty DB, producing a 0-byte main DB with no
            // tables — every subsequent INSERT then hits "no such column:
            // <name>".
            TestPrune.Database.deleteCacheFiles dbPath

            Logging.warn
                "test-prune"
                $"Deleted stale cache DB %s{dbPath} after schema-drift error: %s{ex.Message}. Next run will rebuild from scratch."
        with deleteEx ->
            Logging.error
                "test-prune"
                $"Could not delete stale cache DB %s{dbPath}: %s{deleteEx.Message}. Delete it manually and restart the daemon."

/// Where a daemon sets aside its own index when a NEWER TestPrune.Core wrote it:
/// `test-impact.db.schema-v<N>`, beside the original. The suffix names the schema that
/// wrote it, so a consumer at that schema or newer can recognise the file and reuse it
/// (renaming it back); this consumer never opens it.
let internal quarantinedIndexPath (dbPath: string) (foundVersion: int) : string = $"%s{dbPath}.schema-v%d{foundVersion}"

/// Open the daemon's own impact index at `dbPath`. When a NEWER TestPrune.Core wrote it
/// (`SchemaNewerThanConsumerException` naming this very file), the index is a cache this
/// consumer can rebuild, so it is moved aside to `quarantinedIndexPath` with its SQLite
/// sidecars, kept intact, and a fresh index is opened in its place. Before this, the
/// refusal escaped plugin registration: a land worktree left forward-migrated by a failed
/// pin bump stopped every later, older-pinned daemon from starting at all.
///
/// TestPrune.Core's refusal itself stands: it must never downgrade a newer file in place.
let internal openOwnIndex (dbPath: string) (walkPolicy: WalkPolicy) : Database =
    try
        Database.create (dbPath, walkPolicy)
    with SchemaNewerThanConsumerException(_, found, supported) ->
        let aside = quarantinedIndexPath dbPath found

        for ext in [ ""; "-wal"; "-shm" ] do
            if File.Exists(dbPath + ext) then
                File.Move(dbPath + ext, aside + ext, true)
            elif File.Exists(aside + ext) then
                // A sidecar left by an earlier quarantine must not pair with this file.
                File.Delete(aside + ext)

        Logging.warn
            "test-prune"
            $"%s{dbPath} was written by TestPrune.Core schema v%d{found}; this daemon supports schema v%d{supported}. Moved it aside to %s{aside} (kept for a consumer at schema v%d{found} or newer) and starting on a fresh test-impact index."

        let fresh = Database.create (dbPath, walkPolicy)

        Logging.info
            "test-prune"
            $"Started a fresh test-impact index at %s{dbPath} (schema v%d{supported}); it rebuilds as files are checked."

        fresh

/// The line an extension refresh writes per extension that answered: its edge count and
/// how long the whole refresh (every extension, one call) took.
let internal extensionStoredLine (name: string) (edgeCount: int) (refreshMs: int64) : string =
    $"Extension '%s{name}' stored %d{edgeCount} edge(s) (extension refresh took %d{refreshMs}ms)"

/// Delete the FCS check cache (`.fshw/cache/*.json`) for `repoRoot`, returning the number
/// of entries removed. Called when the TestPrune symbol DB was recreated (a schema bump):
/// the persisted FCS cache would otherwise let unchanged files hit the cache and SKIP
/// re-checking, so their symbols never re-flush into the freshly-emptied DB — leaving the
/// symbol graph (and therefore coverage + impact analysis) permanently partial. Clearing it
/// forces the next scan to re-check, and thus re-index, every file. Pure path logic so it
/// is unit-testable without a daemon.
let internal clearFcsCheckCache (repoRoot: string) : int =
    let cacheDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "cache")

    if Directory.Exists cacheDir then
        let files = Directory.GetFiles(cacheDir, "*.json")

        for f in files do
            File.Delete f

        files.Length
    else
        0

/// Build the TestPrune task-cache key for one event, from its three state inputs.
///
/// Every input is a THUNK, and that is load-bearing. `FileChecked` is the per-FILE,
/// highest-frequency probe — one event per file on every scan — and it uses NONE of the
/// three. By value, the `dependsOn` hash (a full-repo `SafeWalk` plus a SHA256 of every
/// matched file) is computed once per checked file for a value that arm discards.
/// "cacheKey runs once per event, not per file" is true of
/// BuildCompleted and false of FileChecked.
///
/// Lifted out of the `create` closure so the property is STRUCTURAL: an arm cannot pay
/// for an input it does not name, and a test can prove it by counting calls.
///
/// `pendingQueueHash`/`dependsOnHash` return `None` for "nothing to contribute",
/// which keeps the corresponding merkle entry OMITTED — the empty-queue,
/// no-dependsOn key stays byte-identical to the pre-feature key, so existing
/// on-disk caches keep hitting.
let internal cacheKeyFor
    (changedSymbolsHash: unit -> string)
    (pendingQueueHash: unit -> string option)
    (dependsOnHash: unit -> string option)
    // The content merkle of the repo's PROJECT FILES — the files
    // that declare what is compiled (`projectStructureHash`). Not optional and not
    // omittable: every repo has a structure, and an omitted entry is what let a tree
    // that had just gained a `<Compile Include=…>` compute the key of the tree without
    // it and replay a green that never ran the new tests.
    //
    // Adding this entry ORPHANS every `outcomeKey` entry written before it, which is
    // exactly right — those entries assert a verdict over a structure they never
    // recorded — so no `plugin-version` bump is needed on top.
    (projectStructureHash: unit -> string)
    // `Some "full"` while the caller has asked for the whole suite;
    // `None` for the impact-filtered inner loop. Without it, `confirm` on an unchanged
    // tree HITS the entry written by an earlier impact-filtered run and replays a
    // filtered green as a merge verdict, with no test process ever starting.
    //
    // `None` rather than "impact" for the inner loop keeps the ordinary key
    // byte-identical to the pre-feature one, so existing on-disk caches keep hitting.
    (fullSuiteScopeHash: unit -> string option)
    // The content identity of the tree this event is about (`ReceiptInputTree.read`): the
    // term that PINS THE TREE (ADR-059 §4). The changed-symbols term cannot, since on a cold
    // scan this event is dispatched before the FCS pass; so two clean-building trees used to
    // compute one key. `None` (a hole, an unreadable file) makes the event uncacheable rather
    // than keying it on a placeholder two different trees would share.
    (inputTree: unit -> string option)
    // True while a failure no covering run has passed is outstanding.
    // While it is, this plugin does not participate in the task cache AT ALL:
    //   * no REPLAY — a cached green served on a BuildCompleted would skip the handler,
    //     skip the run, and hand back exactly the laundered verdict this flag
    //     prevents (the same reasoning that makes a non-empty pending queue refuse);
    //   * no WRITE — the terminal status of such a run carries a red the run itself
    //     did not produce, and pinning that to a content merkle would let it replay on
    //     a tree that has since been fixed (in reverse).
    // Read at DISPATCH time, so on a `TestsFinished` it is the PRIOR outstanding set —
    // which is the sound one to gate on: `allPassed` with an empty prior set implies an
    // empty post-run set, so a genuinely green run is still cacheable, and a run that
    // CLEARS a red merely forgoes one cache write.
    (hasOutstandingFailures: unit -> bool)
    // Has a run in THIS PROCESS produced test evidence — i.e. does this
    // session's `RunCoverage` cover any project at all?
    //
    // Serving a cached BuildCompleted skips the handler, so no run happens, no
    // `TestsFinished` lands, and `LastCoverage` stays empty. Every consumer reading the
    // plugin's STATE (`test-scope`, and through it the verdict file) then hears "NO TESTS
    // RAN" while the status line reports "1 passed (cached)" — one run, two surfaces,
    // opposite answers, and both `check` and `confirm` exit 3.
    //
    // The key cannot rescue this because it does not pin the TREE: on a cold scan
    // `BuildCompleted` is dispatched BEFORE the FCS pass, so `changed-symbols` is empty
    // whatever the tree contains, and two different clean-building trees compute the SAME
    // key. What makes the cache sound in a warm daemon is the symbol-diff pipeline that
    // runs after it and supersedes the entry; across a process boundary there is no such
    // run.
    //
    // So fail closed — no replay AND no write — as a non-empty pending queue and an
    // outstanding failure already do. This also restores `hasCachedResults`: a cold start
    // with no session baseline must run the full suite, and a cache hit skipped the
    // handler that rule lives in. The warm inner loop is untouched — once this session's
    // first run lands there IS coverage, and later BuildCompleteds replay as before.
    (sessionHasTestEvidence: unit -> bool)
    (event: PluginEvent<TestPruneMsg>)
    : ContentHash option =
    let optionalEntry (name: string) (value: string option) =
        match value with
        | Some v -> [ name, v ]
        | None -> []

    // Reuses the same merkle for BuildCompleted and Custom TestsFinished so the
    // cache writes on TestsFinished (synchronous handler — captures EmittedEvents)
    // and the next BuildCompleted hits via the matching key. TestsFinished only
    // fires after BuildSucceeded (BuildFailed short-circuits earlier), so
    // outcome="succeeded" is correct for the Custom path.
    //
    // The `-v2` salt orphans entries written, which cached FAILED
    // verdicts and could replay them on a now-green tree. Bump it again for any change
    // that makes an old entry unsound, rather than asking users to wipe the cache.
    let outcomeKey (buildOutcome: string) : ContentHash option =
        inputTree ()
        |> Option.map (fun tree ->
            FsHotWatch.TaskCache.merkleCacheKey (
                [ "plugin-version", "test-prune-merkle-v2"
                  "event", "BuildCompleted"
                  "input-tree", tree
                  "changed-symbols", changedSymbolsHash ()
                  // The one term that pins the SHAPE of the tree. The
                  // changed-symbols term cannot: on a scan this event is dispatched before
                  // the FCS pass, so it is empty whatever the tree holds.
                  "project-structure", projectStructureHash ()
                  "build-outcome", buildOutcome ]
                @ optionalEntry "pending-queue" (pendingQueueHash ())
                @ optionalEntry "depends-on" (dependsOnHash ())
                @ optionalEntry "full-suite-scope" (fullSuiteScopeHash ())
            ))

    match event with
    | BuildCompleted BuildSucceeded ->
        // A cache HIT replays the cached terminal status and SKIPS the handler
        // (`PluginFramework.tryReplayCache`) — but this handler is the drain trigger for
        // the pending-verification queue. The key folds in a queue
        // hash, but that is read at DISPATCH time and on a scan the queue is mutated
        // afterwards by the FCS pass, so the key cannot be trusted to notice outstanding
        // work. `None` refuses the cache entirely — no replay AND no write — so the
        // handler always runs and always gets its chance to drain.
        //
        // The three refusals below are the same rule from three directions; see the
        // parameter docs. The empty-queue green fast-path is untouched.
        match pendingQueueHash (), hasOutstandingFailures (), sessionHasTestEvidence () with
        | Some _, _, _
        | _, true, _
        | _, _, false -> None
        | None, false, true -> outcomeKey "succeeded"
    | Custom(TestsFinished(_, completed, _)) ->
        // A FAILED test outcome must never be served from cache as a current verdict.
        // Unlike BuildPlugin — whose result is a pure function of its content-merkle
        // inputs — a test outcome is NOT pinned by the changed-symbols merkle: the same
        // key recurs after the tree is fixed (or for a flaky test), so a cached `Failed`
        // replays as a stale red on a green tree. Observed: an 08:35 failure replayed at
        // 10:19 and 10:49 and through four deploy-preflights on a `failed: 0` tree.
        // `None` makes a non-passing run UNCACHEABLE, so `runAndCache` skips the write
        // and the next matching BuildCompleted re-runs.
        //
        // A green must ALSO leave the queue empty to be cacheable — a green with symbols
        // still queued is not a "safe to skip" verdict. And the outcome must be
        // non-Aborted: an aborted run has empty Results, which the all-passed fold treats
        // as trivially passing.
        // Written out PER CASE, at the site, because the JUSTIFICATION
        // differs per case:
        //   * `Verified`        — ran and green. The only positive evidence there is.
        //   * `Refuted`         — a real red. Never cacheable, for the reason above.
        //   * `NothingVerified` — a zero MATCH is admitted here ONLY because the
        //     `allZeroMatchRun` conjunct below refuses the run where EVERY project
        //     matched nothing, so what survives is a mixed run in which some project
        //     did verify something. A Deferred/Errored project is refused outright:
        //     a build-ordering race or a host crash must never be replayable as a
        //     green.
        //
        // The per-result VALUES, unlike the reasons, are not independent: this match is
        // the exact negation of the `nonGreen` match in the `TestsFinished` handler's
        // status ladder (a result admitted here is one that match excludes, and the
        // reverse). Nothing enforces that — the two are kept in agreement BY HAND, so a
        // case changed at one site must be changed at the other. A shared helper would
        // not make them provably agree either, because what surrounds them differs: here
        // the fold is ANDed with `Ran FullSuite`, `notAborted`, `not allZeroMatchRun`, an
        // empty pending queue and no outstanding failures, and it decides a cache key;
        // there it runs only once the aborted, zero-projects-with-queued-symbols and
        // all-zero-match arms have been ruled out, and it decides a status line.
        let noProjectRefutedOrUnrun =
            completed.Results
            |> Map.forall (fun _ r ->
                match TestResult.verdict r with
                | Verified -> true
                | Refuted -> false
                | NothingVerified -> TestResult.isNoMatch r)

        let notAborted =
            match completed.Outcome with
            | Aborted _ -> false
            | Normal -> true

        // A run that matched NO tests must not mint a cacheable green.
        // The fold above admits a zero-match project (see its comment), so on its own it
        // would write a replayable entry: a later BuildCompleted on the same tree would
        // hit a green produced by executing zero tests.
        //
        // Deliberately NOT extended to an empty result set — that is the "nothing to
        // verify" skip, decided separately. This covers only a run where projects ran and
        // matched nothing.
        let allZeroMatchRun = allZeroMatchOf completed.Results

        // Third condition: a run that passed everything IT ran while an
        // earlier, uncovered failure is outstanding is NOT green — its terminal status is
        // a Failed carrying the carried-over red.
        //
        // Deliberately NOT gated on `sessionHasTestEvidence`. This arm is the WRITE, read
        // at DISPATCH time, when the run this message carries has not yet been folded
        // into state and there is no evidence to see. Its key is never used for a LOOKUP:
        // the framework does not replay over a `Custom` message at all, since a Custom's
        // payload is not in its key.
        // this write feeds the key an ordinary whole-tree check reads.
        // The terminal receipt therefore has to establish the whole configured suite;
        // a green project-scoped/manual or impact-scoped run cannot mint broader proof.
        let provenFullSuite =
            completed.Verification = Ran FullSuite
            && noProjectRefutedOrUnrun
            && notAborted
            && not allZeroMatchRun

        if
            provenFullSuite
            && (pendingQueueHash ()).IsNone
            && not (hasOutstandingFailures ())
        then
            outcomeKey "succeeded"
        else
            None
    | BuildCompleted(BuildFailed errs) ->
        // Shares `outcomeKey` with the BuildSucceeded arm so the salt and the
        // pending-queue/dependsOn entries can never split across the two.
        outcomeKey ("failed:" + String.concat "|" (List.sort errs))
    | FileChecked r ->
        // `fcs-signature` captures cross-file FCS state so upstream symbol changes
        // invalidate this file's cached symbol-diff.
        //
        // Note what this arm does NOT read: not the changed symbols, not the pending
        // queue, not the dependsOn globs. It is a pure function of THIS file — which is
        // why all three are thunks.
        let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

        // A per-file cache hit derives a terminal summary from the plugin's whole live
        // ledger and labels it `(cached)`. While a fresh test failure is outstanding,
        // that would falsely relabel the test run as replayed merely because a helper
        // file's earlier symbol analysis hit its cache. Refuse the per-file replay until
        // a covering test run clears the red; healthy trees keep the hot path.
        if hasOutstandingFailures () then
            None
        // The entry is written for every analysis and READ only where the index holds it
        // (`IndexedAnalysis`, through the state's `IFileCacheReadGuard`).
        else
            Some(
                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", "test-prune-merkle-v2"
                      "event", "FileChecked"
                      "file", AbsFilePath.value r.File
                      "source", r.Source
                      "fcs-signature", fcsSignature ]
            )
    | _ -> None

/// The impact queries the plugin runs against its index, in one place, so a test can
/// count them per handler instance.
type internal ImpactQueries =
    {
        /// The tests `QueryAffectedTests` selects for a set of changed symbols.
        AffectedTests: string list -> TestMethodInfo list
        /// For each symbol, the test projects its single-seed query would select from.
        CoveringProjectsBySeed: string list -> Map<string, Set<string>>
        /// Every test project the index holds a test method for. Not a covering query:
        /// one scan, whatever is queued. `None` when the index cannot be read this way
        /// (an in-memory index is private to the connection that made it).
        IndexedTestProjects: unit -> Set<string> option
    }

module internal ImpactQueries =
    let private indexedTestProjects (db: Database) () : Set<string> option =
        try
            use conn = db.OpenConnection()
            use query = conn.CreateCommand()
            query.CommandText <- "SELECT DISTINCT test_project FROM test_methods;"
            use reader = query.ExecuteReader()

            [ while reader.Read() do
                  reader.GetString 0 ]
            |> Set.ofList
            |> Some
        with :? Microsoft.Data.Sqlite.SqliteException ->
            None

    let ofDatabase (db: Database) : ImpactQueries =
        { AffectedTests = db.QueryAffectedTests
          CoveringProjectsBySeed = db.QueryCoveringProjectsBySeed
          IndexedTestProjects = indexedTestProjects db }

/// Create a TestPrune plugin handler using the declarative plugin framework.
/// `buildExtensions` receives the plugin's own `Database` so extensions that
/// need a `RouteStore`/`SymbolStore` derive it from the same DB the plugin
/// queries against — structurally prevents the caller from wiring an extension
/// to a different DB than the plugin's. `queriesOf` builds the impact queries over that
/// same DB.
let internal createWithEvidenceAndQueries
    // The box-wide test-evidence store this plugin reuses from and publishes to;
    // `None` (the kill switch, or no store) runs exactly as without one.
    (evidence: TestEvidenceRun.Wiring option)
    (queriesOf: Database -> ImpactQueries)
    (launchDeadline: TimeSpan)
    // `tests.maxParallelProjects`: at most this many test groups in flight; `None` for all.
    (maxParallelProjects: int option)
    // The declared exclusions: indexed test-project name -> written reason. Called once
    // per debt classification (flush, launch, completion), so a caller can re-resolve
    // them against the current project inventory. A throw is a refusal to classify, and
    // leaves the debt owed.
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    // `dependsOn`: repo-root-relative globs naming EXTERNAL inputs (DB
    // migrations, generated files, schemas) that the symbol-diff cache key can't
    // see. Their content hash salts the BuildCompleted cache key so editing one
    // forces a genuine re-run instead of replaying a stale verdict. `[]` → no
    // salt (key byte-identical to the pre-feature key).
    (dependsOn: string list)
    // `tests.traces`: `None` records nothing and launches every project as configured.
    (traces: TraceWiring option)
    // The mode the session starts in: `TestMode.initial` for a daemon,
    // `TestMode.startingWith false` for a host that runs only what a command requests.
    (initialMode: TestMode)
    =
    /// Which repo-relative paths a build consumed, under the model the host publishes when
    /// asked — so `executeTests` observes it after `beforeRun`, not at launch.
    let compileInputsOf (ctx: PluginCtx<TestPruneMsg>) () : string -> bool =
        ReceiptInputTree.feedsCompile repoRoot (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd)

    /// The traces of a run launched under `mode`, when `tests.traces` is configured, with
    /// the plugin's activity log and subtasks: a force run's `executeTests` has no `ctx`.
    let tracesFor (ctx: PluginCtx<TestPruneMsg>) (mode: TestMode) : TraceRunHost option =
        traces
        |> Option.map (fun wiring ->
            { TraceRuntime =
                { Settings = wiring.Policy
                  RepoRoot = repoRoot
                  ExcludedProjects = wiring.OptedOut
                  Mode = mode
                  // Known once the run's selection is: set by `executeTests`.
                  RunsEveryProjectInFull = false }
              TraceWiring = wiring
              TraceLog = ctx.Log
              HoldSubtask =
                fun key label ->
                    ctx.StartSubtask key label

                    { new IDisposable with
                        member _.Dispose() = ctx.EndSubtask key }
              IndexFold =
                { Folded =
                    fun () ->
                        let folded =
                            Tasks.TaskCompletionSource(Tasks.TaskCreationOptions.RunContinuationsAsynchronously)

                        ctx.Post(IndexFoldProbe folded)
                        folded.Task
                  Bound = TraceRun.indexFoldBound } })

    let db = openOwnIndex dbPath walkPolicy
    let busyTimeout = dbBusyTimeout db
    let queries = queriesOf db
    let configuredTestProjects = testConfigs |> Option.defaultValue []

    /// Claim the "tests" key and the shared artifact lease for `work`. `owed` is the
    /// dependency fanout this launch consumes; an unavailable launch hands it back.
    ///
    /// Result-first: the completion fold sheds only the symbols the run launched with, so
    /// a file check or build dispatched during the run stays owed whether it folds before
    /// the result or after it. Folding after can only cost a run: a BootScan cohort that
    /// would have joined a full run in flight (`joinsFullRun`) finds none and queues its
    /// own. It never lets less evidence discharge anything. The run emits test lifecycle
    /// events this plugin does not subscribe to. Without this, a result stuck behind a
    /// storm of checks holds the "tests" key, and its green, for as long as those folds
    /// take.
    let runTestHostExclusive
        (ctx: PluginCtx<TestPruneMsg>)
        (owed: Set<string>)
        (reply: Tasks.TaskCompletionSource<string> option)
        (workOf: PluginCtx<TestPruneMsg> -> Async<TestPruneMsg>)
        =
        // The run this launch started and has not closed. Its result's fold closes it; a
        // launch whose result never folds — its work returned, but its test host's
        // termination could not be established — closes it from its failure instead.
        // Subscribers track every started run until its completion (Build defers every
        // build while one is open), so a run left open wedges them for good.
        let opened = ref None

        let tracked =
            { ctx with
                EmitTestRunStarted =
                    fun started ->
                        opened.Value <- Some started
                        ctx.EmitTestRunStarted started
                EmitTestRunCompleted =
                    fun completed ->
                        match opened.Value with
                        | Some started when started.RunId = completed.RunId -> opened.Value <- None
                        | _ -> ()

                        ctx.EmitTestRunCompleted completed }

        let workFor =
            function
            | Ready ->
                // Cancelled once every client that wanted it has gone (when `workOf` is
                // cooperative-safe), it folds as withdrawn: it closes the run it opened and
                // hands back what it consumed, so nothing of it is left in flight.
                workOf tracked
                |> PluginWork.onAbandon (fun () -> TestRunWithdrawn(owed, reply, opened.Value))
                |> PluginWork.resultFirst
            | Invalid reason -> PluginWork.resultFirst (async { return ArtifactsUnavailable(reason, owed, reply) })

        // What this launch releases into the shared "build-artifacts" lease, which the
        // next claimant is handed. Only a verdict ABOUT the artifacts may change it.
        // A refusal that found them unusable carries that invalidity forward, so a
        // failed build stays sticky until one succeeds. A test host that would not
        // start says nothing about the build output: it releases the lease as valid,
        // so the next launch on an unchanged tree runs instead of being refused — with
        // the build named as the culprit — until something forces a real rebuild.
        let classify =
            function
            | ArtifactsUnavailable(reason, _, _) -> Invalid reason
            | TestHostUnavailable _ -> Ready
            | _ -> Ready

        let failureMessage (ex: exn) =
            TestHostUnavailable(ex.Message, owed, reply, opened.Value)

        match ctx.RunExclusiveShared "tests" "build-artifacts" workFor classify failureMessage with
        | SharedClaimed
        | SharedQueued -> Claimed
        | LocalSlotBusy -> SlotBusy

    // A recreated DB (schema bump) leaves the FCS check cache stale — see
    // `clearFcsCheckCache`.
    if db.WasRecreated then
        try
            let cleared = clearFcsCheckCache repoRoot

            Logging.warn
                "test-prune"
                $"TestPrune DB was recreated (schema change) — cleared %d{cleared} FCS check-cache entries so every file re-indexes on this scan."
        with ex ->
            Logging.error "test-prune" $"failed to clear the FCS check cache after a DB recreate: %s{ex.Message}"

    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    /// Whether a `FileChecked` cache hit may stand in for analysing `result`
    /// (`IndexedAnalysis`): the index holds exactly this analysis, and no other is pending
    /// for the file. Analysis-only daemons keep the plain key.
    /// The analysis identity the index holds for a file (`IndexedAnalysis`). An index that
    /// cannot be read holds nothing it can vouch for.
    let indexedKey (relPath: string) : string option =
        try
            db.GetFileKey relPath
        with ex ->
            Logging.debug "test-prune" $"index identity unreadable for %s{relPath}: %s{ex.Message}"
            None

    let mayReplay (pending: Map<string, string>) (result: FileCheckResult) : bool =
        testConfigs |> Option.forall List.isEmpty
        || (let relPath =
                Path.GetRelativePath(repoRoot, AbsFilePath.value result.File).Replace('\\', '/')

            IndexedAnalysis.identityOf repoRoot result
            |> Option.exists (IndexedAnalysis.mayReplay (indexedKey relPath) (Map.tryFind relPath pending)))

    // Durable "needs-testing" queue (plugin-owned sidecar). The set of changed
    // symbols not yet proven test-equivalent to the last green run. Loaded once
    // at construction so a restart with a non-empty queue re-flags those
    // symbols. The live queue is `state.Debt.PendingQueue`; this file is its durable
    // copy, written by `PrepareCommit`. A symbol leaves ONLY when a covering test
    // run passed (or it has no covering test). See PendingVerification.fs.
    let loadedQueue = PendingVerification.load repoRoot

    /// `Some reason` when the sidecar EXISTS but could not be read
    /// (a torn write, corrupt JSON, a non-string entry). What was owed is then
    /// UNKNOWN — which is NOT the same fact as "nothing is owed", and must never
    /// again be spelled with the same value. A MISSING file is not this: it is a
    /// provable `Loaded empty` (fresh clone), and stays a fast no-op.
    let ledgerUnreadableReason =
        match loadedQueue with
        | PendingVerification.LoadedQueue.Loaded _ -> None
        | PendingVerification.LoadedQueue.Unreadable reason -> Some reason

    /// Present while a durable debt publication is between its first write and its
    /// publication. A restart that finds it cannot tell which sidecars describe the
    /// published state, so what is owed is unknown. Only `Finalize` removes it, and only
    /// for a state that is no longer unknown: a session that ends with the debt still
    /// unknown has written nothing, so the marker it found still describes the sidecars.
    let debtPublicationPath =
        Path.Combine(FsHwPaths.root repoRoot, "test-prune", "owner-publication-pending")

    let interruptedPublication = File.Exists debtPublicationPath

    if interruptedPublication then
        Logging.warn
            "test-prune"
            $"a verification-debt publication did not finish (%s{debtPublicationPath}), and no full suite has passed every configured project since. The sidecars may describe a state that was never published, so what is owed is UNKNOWN. The marker stays, and every test run, in this daemon and the next, is widened to every configured project in full until a full suite passes every configured project."

    /// The reds carried in from the previous session — quarantined into
    /// the first run exactly as in-session reds are. An UNREADABLE file is debt of unknown
    /// membership and takes the unreadable-ledger road: widen to the full suite, which
    /// re-executes every test and rebuilds the list from evidence.
    let loadedFailures, failuresUnreadableReason =
        match OutstandingFailure.load repoRoot with
        | OutstandingFailure.LoadedFailures.Loaded failures -> failures, None
        | OutstandingFailure.LoadedFailures.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the outstanding-failures ledger (%s{OutstandingFailure.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every test still red from earlier runs, so which tests are owed is now UNKNOWN. Every test run is widened to every configured project in full until a full suite has re-executed them all."

            [], Some reason

    /// The full-suite watermark this ledger's impact-filtered greens are
    /// relative to. `None` until a full-suite run has accounted for every configured
    /// project; an unreadable file is the same recovery, said out loud.
    let loadedBaseline =
        match FullSuiteBaseline.load repoRoot with
        | FullSuiteBaseline.LoadedBaseline.Loaded baseline -> baseline
        | FullSuiteBaseline.LoadedBaseline.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the full-suite baseline (%s{FullSuiteBaseline.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. The next test run is widened to the full suite to earn a new one."

            None

    /// Whole-project coverage the previous daemon recorded. Unreadable is no record: the
    /// projects it might have named run whole again, which costs time and nothing else.
    let restoredEvidence =
        match WholeProjectEvidence.load repoRoot with
        | WholeProjectEvidence.LoadedRecord.Loaded record ->
            if not (Map.isEmpty record) then
                let names = record |> Map.keys |> String.concat ", "

                Logging.info
                    "test-prune"
                    $"Whole-project evidence recorded by an earlier daemon for %d{Map.count record} project(s) (%s{names}): each stands while its compile inputs are the ones recorded"

            EarnedEvidence.restored record
        | WholeProjectEvidence.LoadedRecord.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the whole-project evidence record (%s{WholeProjectEvidence.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. Every test project runs in full until this daemon has seen it run whole."

            None

    let loadedRuntimeObligations, runtimeObligationsUnreadableReason =
        match loadRuntimeCoverageObligations repoRoot with
        | Ok obligations -> obligations, None
        | Error reason ->
            Logging.warn
                "test-prune"
                $"the runtime coverage obligation ledger could not be read: %s{reason}. Widening to the full suite until a verified full run recovers the unknown debt."

            Map.empty, Some reason

    /// Projects an earlier daemon owed a whole-project run because their runtime coverage did
    /// not ingest. A record that cannot be read leaves which projects are owed unknown.
    let loadedNotIngested, notIngestedUnreadableReason =
        match CoverageNotIngested.load repoRoot with
        | CoverageNotIngested.LoadedRecord.Loaded record ->
            if not (Map.isEmpty record) then
                let owed =
                    record
                    |> Map.toList
                    |> List.map (fun (project, reason) -> $"%s{project} (%s{reason})")
                    |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"runtime coverage did not ingest for %d{Map.count record} project(s): %s{owed}. Each runs in full until a run of it ingests."

            record, None
        | CoverageNotIngested.LoadedRecord.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the record of runtime coverage that did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. Which projects are owed is UNKNOWN; widening to the full suite until a full suite passes."

            Map.empty, Some reason

    /// Records, durably and before the run returns, that a project's runtime coverage did
    /// not ingest, then tells the owner through a message. A record that cannot be written
    /// is logged; the message still makes this session owe the project.
    let coverageIngestFailed (ctx: PluginCtx<TestPruneMsg>) (failure: CoverageIngestFailure) =
        try
            armCoverageNotIngested
                (fun () -> ctx.Post(RuntimeCoverageFailed(failure.Project, failure.Reason)))
                (CoverageNotIngested.sidecarPath repoRoot)
                failure
        with ex ->
            ctx.Post(RuntimeCoverageFailed(failure.Project, failure.Reason))

            Logging.error
                "test-prune"
                $"failed to durably record that the runtime coverage of %s{failure.Project} did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}): %s{ex.Message}"

    /// How many CONSECUTIVE flush cycles each currently-queued symbol
    /// has seeded, so a symbol that is pinned AND selecting wide can be named out loud
    /// (see `isPoisonSuspect`).
    ///
    /// In-memory and per-session on purpose. Persisting it needs either a second sidecar
    /// or a shape change to `pending-verification.json` — and that file's reader treats
    /// ANY unparseable content as unknown debt that widens every run to the full suite,
    /// so a format change hands every existing checkout one gratuitous
    /// full-suite recovery. Forgetting on restart costs three cycles of re-arming.
    ///
    /// A diagnostic only: no selection, cache or debt decision reads it.
    let mutable pendingAgeRef: Map<string, int> = Map.empty

    // Say it out loud — a silent recovery here reads as a green.
    match ledgerUnreadableReason with
    | Some reason ->
        Logging.warn
            "test-prune"
            $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every symbol still awaiting a green test run, so what is owed is now UNKNOWN — which is NOT the same as nothing owed. Until a FULL-SUITE run passes, every test run is widened to every configured project in full and no cached verdict may be replayed."
    | None -> ()

    /// The test projects this daemon can actually RUN — i.e. the ones in
    /// `testConfigs`. Empty when the plugin is analysis-only.
    ///
    /// The symbol DB indexes test methods from EVERY test project it analyzed, which is
    /// not the same set. A symbol covered by a project outside this set is NOT covered by
    /// a test we can run, and it is not thereby verified either: see `debtScope` for the
    /// only way such a project stops owing.
    let runnableProjects: Set<string> =
        match testConfigs with
        | Some configs -> configs |> List.map (fun c -> c.Project) |> Set.ofList
        | None -> Set.empty


    /// Why the full-suite baseline cannot vouch for what a filtered run
    /// over the configured projects would skip — `None` when it can. Analysis-only
    /// daemons make no test claim and so owe no baseline.
    ///
    /// Unknown debt is folded in: the baseline composes with the queue (the queue names
    /// what changed since the baseline), so a baseline beside a ledger that cannot be
    /// read vouches for nothing until a full suite re-earns both.
    let baselineInvalidReason (debt: VerificationDebt) : string option =
        if Set.isEmpty runnableProjects then
            None
        elif debt.RecoveryOutstanding then
            Some "what is owed is unknown, so the full-suite baseline cannot say what changed since it was earned"
        else
            match debt.Baseline with
            | None -> Some FullSuiteBaseline.absentReason
            | Some baseline -> FullSuiteBaseline.staleness runnableProjects baseline

    /// The one question every skip in this plugin is really asking: is the
    /// needs-testing queue PROVABLY empty? Unknown debt is never `true` here — an empty
    /// queue we could not read is not an empty queue.
    ///
    /// An absent or stale full-suite baseline is owed work too — the
    /// tests a filtered run skips have nothing to be equivalent TO until one exists —
    /// so it is folded in here rather than checked beside this at each skip site.
    let nothingOwed (debt: VerificationDebt) =
        Set.isEmpty debt.PendingQueue
        && Map.isEmpty debt.RuntimeObligations
        && Map.isEmpty debt.CoverageNotIngested
        && not debt.RecoveryOutstanding
        && Option.isNone (baselineInvalidReason debt)

    /// What a drain is FOR, in words. Unknown debt cannot be counted, so it is named.
    let owedDescription (debt: VerificationDebt) =
        let queued = Set.count debt.PendingQueue

        if debt.RecoveryOutstanding then
            $"UNKNOWN verification debt (%s{describeUnknownDebt debt.UnknownDebt}), which only a full suite can prove, + %d{queued} newly-queued symbol(s)"
        else
            // A drain owed only to the baseline says so, or a reader sees
            // "0 symbol(s) awaiting verification — draining now" and goes looking for the
            // bug in the queue.
            let notIngested =
                if Map.isEmpty debt.CoverageNotIngested then
                    ""
                else
                    let projects = debt.CoverageNotIngested |> Map.keys |> String.concat ", "
                    $" + a whole-project run of %s{projects}, whose runtime coverage did not ingest"

            match baselineInvalidReason debt with
            | Some reason ->
                $"%d{queued} symbol(s) awaiting verification + a full suite to earn the baseline (%s{reason})%s{notIngested}"
            | None -> $"%d{queued} symbol(s) awaiting verification%s{notIngested}"

    /// Write the queue before the analysis flush advances the durable symbol snapshot:
    /// once the snapshot advances, a symbol missing from the durable queue can no longer
    /// be re-detected after a crash. Only ever ADDS to what is on disk — the state it
    /// writes is the published queue plus this event's enqueues — so writing it before
    /// publication can over-test, never under-test.
    ///
    /// Unknown debt is not written: the unreadable file is the honest record until the
    /// recovering full suite publishes a new one.
    let persistQueueAdditions (debt: VerificationDebt) =
        if not debt.RecoveryOutstanding then
            try
                PendingVerification.save repoRoot debt.PendingQueue
            with ex ->
                Logging.warn
                    "test-prune"
                    $"failed to persist pending-verification queue before the analysis flush: %s{ex.Message}; the queue is published with this event's state"

    /// The debt revision of `symbol`. A symbol loaded from the durable queue and never
    /// re-enqueued is at revision 0.
    let revisionOf (debt: VerificationDebt) (symbol: string) : int64 =
        debt.SymbolRevisions |> Map.tryFind symbol |> Option.defaultValue 0L

    /// Add `symbols` to the queue at one new revision, including a re-edit of a symbol
    /// already queued: a run captured before it cannot have built it.
    let enqueuePending (symbols: string list) (debt: VerificationDebt) =
        if symbols.IsEmpty then
            debt
        else
            let revision = debt.Revision + 1L

            { debt with
                PendingQueue = (debt.PendingQueue, symbols) ||> List.fold (fun q s -> Set.add s q)
                Revision = revision
                SymbolRevisions =
                    (debt.SymbolRevisions, symbols)
                    ||> List.fold (fun revisions s -> Map.add s revision revisions) }

    /// Remove `symbols` from the queue: a covering run passed them, or they have no
    /// covering test. Durable once the state carrying it is prepared.
    let commitPending (symbols: Set<string>) (debt: VerificationDebt) =
        if symbols.IsEmpty then
            debt
        else
            { debt with
                PendingQueue = Set.difference debt.PendingQueue symbols
                SymbolRevisions = debt.SymbolRevisions |> Map.filter (fun s _ -> not (Set.contains s symbols)) }

    let expectedRuntimeCoverageProjects =
        match testConfigs, coveragePaths with
        | Some configs, Some pathsForProject ->
            configs
            |> List.choose (fun config ->
                if pathsForProject config.Project |> Option.isSome then
                    Some config.Project
                else
                    None)
        | _ -> []

    let allowedRuntimeProjects = Set.ofList expectedRuntimeCoverageProjects

    let loadedDebt =
        { PendingQueue =
            match loadedQueue with
            | PendingVerification.LoadedQueue.Loaded queue -> queue
            // The owed symbols cannot be NAMED, so none are seeded; `UnknownDebt` carries
            // the debt instead and widens every run until a full suite proves the tree.
            // Seeding `empty` is safe ONLY because that cause is recorded.
            | PendingVerification.LoadedQueue.Unreadable _ -> PendingVerification.empty
          SymbolRevisions = Map.empty
          Revision = 0L
          UnknownDebt =
            [ match ledgerUnreadableReason with
              | Some reason ->
                  $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              match failuresUnreadableReason with
              | Some reason ->
                  $"the outstanding-failures ledger (%s{OutstandingFailure.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              match runtimeObligationsUnreadableReason with
              | Some reason ->
                  $"the runtime coverage obligation ledger (%s{runtimeCoverageObligationsPath repoRoot}) could not be trusted: %s{reason}"
              | None -> ()
              match notIngestedUnreadableReason with
              | Some reason ->
                  $"the record of runtime coverage that did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              if interruptedPublication then
                  $"a verification-debt publication did not finish (%s{debtPublicationPath}), and no full suite has passed since to recover it" ]
          Baseline = loadedBaseline
          RuntimeObligations = pruneRuntimeCoverageObligations allowedRuntimeProjects loadedRuntimeObligations
          CoverageNotIngested = loadedNotIngested }

    // A project that no longer produces runtime coverage cannot retire an obligation, so
    // its obligations are pruned at load. Written here, before any event, because no
    // publication will otherwise carry an unchanged debt to disk.
    if
        not loadedDebt.RecoveryOutstanding
        && loadedDebt.RuntimeObligations <> loadedRuntimeObligations
    then
        match
            persistRuntimeCoverageObligationsWith
                (fun () ->
                    FsHwPaths.atomicWriteAllText
                        (runtimeCoverageRecoveryPath repoRoot)
                        "runtime obligation write in progress")
                (fun () -> saveRuntimeCoverageObligations repoRoot loadedDebt.RuntimeObligations)
                (fun () -> File.Delete(runtimeCoverageRecoveryPath repoRoot))
        with
        | Ok() -> ()
        | Error ex ->
            Logging.warn
                "test-prune"
                $"failed to persist pruned runtime coverage obligations: %s{ex.Message}; the next publication writes them"

    let runtimeCoverageSelection changedFiles =
        selectByRuntimeCoverage
            db
            expectedRuntimeCoverageProjects
            changedFiles
            (DateTimeOffset.UtcNow - RuntimeCoverageMaxAge)

    /// The covering test projects a symbol's debt waits on, under ONE resolution of the
    /// declared exclusions. Empty ⇒ nothing is owed for it and it may leave the queue.
    ///
    /// A covering project counts unless it is unconfigured AND declared excluded with a
    /// written reason. That is the whole policy, applied at every place debt is
    /// classified — the flush that drops uncovered symbols, the launch that captures each
    /// symbol's coverers, the completion that retires them (launch and BootScan cohorts
    /// alike), and the residual diagnostics that name what is still owed:
    ///   * a CONFIGURED project always counts, whatever an exclusion says;
    ///   * an unconfigured project with no declaration counts. Nothing can run it here,
    ///     so the symbol stays owed and the verdict stays red, naming the project. The
    ///     earlier rule dropped such a symbol, which let a configured-suite green
    ///     discharge tests that never ran;
    ///   * a blank reason declares nothing.
    /// Excluding a project is never evidence that its tests passed: the declaration only
    /// removes that project from THIS gate's claim.
    ///
    /// `excluded` is lazy so a classification that never meets an unconfigured coverer
    /// never resolves the declarations. Analysis-only daemons make no test claim, so every
    /// covering project counts and the declarations are never consulted. `covering` is a
    /// pass's `coveringOf`.
    let debtScope (excluded: Lazy<Map<string, string>>) (covering: string -> Set<string>) : string -> Set<string> =
        let declaredExcluded (project: string) =
            match Map.tryFind project excluded.Value with
            | Some reason -> not (String.IsNullOrWhiteSpace reason)
            | None -> false

        fun symbol ->
            let projects = covering symbol

            if Set.isEmpty runnableProjects then
                projects
            else
                projects
                |> Set.filter (fun project -> Set.contains project runnableProjects || not (declaredExcluded project))

    /// Whether the index names no test project beyond those a run here executes or
    /// declares excluded, so that a run of every runnable project leaves nothing owed
    /// elsewhere. An index that cannot be scanned may name one.
    let indexNamesOnlyRunnable (excluded: Lazy<Map<string, string>>) =
        match queries.IndexedTestProjects() with
        | Some indexed -> Set.isSubset (debtScope excluded (fun _ -> indexed) "") runnableProjects
        | None -> false

    /// `debtScope` resolving the declarations only if a classification needs them.
    let lazyDebtScope (covering: string -> Set<string>) =
        debtScope (lazy (resolveExcludedProjects ())) covering

    /// The test projects covering each symbol a classification pass is about: what its
    /// single-seed `QueryAffectedTests` would select from. One grouped query answers all
    /// of `symbols`, run the first time any symbol is asked about and never when none is.
    /// A symbol outside `symbols` is answered by a grouped query of its own.
    let coveringOf (symbols: string seq) : string -> Set<string> =
        let known = symbols |> Seq.distinct |> List.ofSeq
        let grouped = lazy (queries.CoveringProjectsBySeed known)

        fun symbol ->
            match Map.tryFind symbol grouped.Value with
            | Some projects -> projects
            | None ->
                queries.CoveringProjectsBySeed [ symbol ]
                |> Map.tryFind symbol
                |> Option.defaultValue Set.empty

    /// The unconfigured projects `owedTo` still waits on for `symbols`, for a message
    /// that has to name them.
    let owedElsewhere (owedTo: string -> Set<string>) (symbols: string seq) : Set<string> =
        symbols
        |> Seq.map (fun s -> Set.difference (owedTo s) runnableProjects)
        |> Set.unionMany

    // True until this process's first extension refresh: the stored edges were written by
    // an earlier process, over a tree that may differ. After that, a refresh is owed when a
    // flush indexes something or an extension failed (`FailedExtensions`).
    let extensionsUnrefreshed = ref true

    // The refresh a restart owes before anything this process indexed (AUTOMATION-1441),
    // running beside the fold: its answer, and how long it took. A restart's first flush is
    // the `BuildCompleted` its scan dispatches before the checks; on a 2,674-file
    // repository the refresh took 6 s, and every check result queued behind the build
    // waited for it. A `FileChecked` reads neither the extensions' edges nor their failures,
    // so it folds while the refresh runs. Every other event, a selection that queries the
    // index, and a run launched in the meantime take its answer first (`joinRefresh`). It
    // reads what the fold's own refresh would have: no flush writes the index until the
    // answer is taken.
    let refreshInFlight: Tasks.Task<ExtensionRefresh list * int64> option ref = ref None

    /// The failures `outcomes` leave, from those before them: a refreshed extension
    /// leaves the map, a failed one enters it.
    let failuresAfter (outcomes: ExtensionRefresh list) (failed: Map<string, string>) =
        outcomes
        |> List.fold
            (fun failed outcome ->
                match outcome with
                | ExtensionRefresh.Refreshed(name, _) -> Map.remove name failed
                | ExtensionRefresh.Failed(name, ex) -> Map.add name ex.Message failed)
            failed

    /// Fold a refresh's outcomes into `state`: the log, the ledger, `FailedExtensions`.
    let applyRefresh
        (ctx: PluginCtx<TestPruneMsg>)
        (outcomes: ExtensionRefresh list, refreshMs: int64)
        (state: TestPruneState)
        =
        for outcome in outcomes do
            match outcome with
            | ExtensionRefresh.Refreshed(name, edgeCount) ->
                ctx.ClearErrors(extensionLedgerKey name)
                Logging.info "test-prune" (extensionStoredLine name edgeCount refreshMs)
            | ExtensionRefresh.Failed(name, ex) ->
                Logging.error
                    "test-prune"
                    $"Extension '%s{name}' failed: %s{ex.ToString()}; its previously stored edges are kept and every test project runs in full until it answers"

                ctx.ReportErrors (extensionLedgerKey name) [ extensionFailedDiagnostic name ex.Message ]

        { state with
            FailedExtensions = failuresAfter outcomes state.FailedExtensions }

    /// Run every extension and time the whole refresh: it runs them in one call.
    let refreshTimed (exts: ITestPruneExtension list) () =
        let refreshClock = Diagnostics.Stopwatch.StartNew()
        let outcomes = refreshExtensionEdges db repoRoot exts
        outcomes, refreshClock.ElapsedMilliseconds

    /// Take the answer of the refresh running beside the fold, if one is, into `state`.
    let joinRefresh (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        match refreshInFlight.Value with
        | None -> state
        | Some refresh ->
            refreshInFlight.Value <- None

            let answer =
                use _ = ctx.DeclareBoundedWork "extension refresh" ImpactSelectionDeadline
                refresh.GetAwaiter().GetResult()

            applyRefresh ctx answer state

    // Flush pending analysis to DB and merge the runtime obligations it names.
    // Extensions (if any) then replace their COMPLETE edge sets via
    // `refreshExtensionEdges` — after the AST results are written, so they resolve
    // against current symbols, and before QueryAffectedTests, so their edges
    // participate in impact traversal. They re-run only when this flush indexed
    // something or a refresh is owed: their answer is a function of the tree.
    let flushPending (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        // Capture OLD literal coupling before `RebuildProjects`
        // replaces a changed producer's outgoing graph. The unchanged test still
        // points at that old literal, but after the rebuild the producer does not;
        // querying only the changed producer would therefore select nothing.
        //
        // Queue the literal NODE, not a stale producer edge. It then follows the
        // ordinary pending-verification lifecycle and disappears after its covering
        // tests pass, while the rebuilt graph remains an exact description of current
        // source. This is bounded evidence carried across one destructive boundary,
        // not permanent false-positive coupling.
        let preRebuildSymbols =
            Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
            |> Set.toList

        let priorLiteralSeeds = db.GetPriorSharedLiteralSeeds preRebuildSymbols

        // Only a seed not already owed is new debt. Re-enqueuing an owed one would issue
        // it a newer revision and deny a covering run in flight its discharge.
        let newlyOwedLiterals =
            priorLiteralSeeds
            |> List.filter (fun symbol -> not (Set.contains symbol state.Debt.PendingQueue))

        let state =
            { state with
                Debt = enqueuePending newlyOwedLiterals state.Debt
                ChangedSymbols = (priorLiteralSeeds @ state.ChangedSymbols) |> List.distinct }

        if not priorLiteralSeeds.IsEmpty then
            Logging.info
                "test-prune"
                $"Preserved %d{priorLiteralSeeds.Length} pre-rebuild literal coupling seed(s) for impact selection"

        // Persist the pending queue BEFORE flushPendingAnalysis advances the
        // durable analysis snapshot. One write per flush (vs per FileChecked) — same
        // crash-safety, batch-size fewer disk writes. See `persistQueueAdditions`.
        persistQueueAdditions state.Debt

        let indexedSomething = not state.PendingAnalysis.IsEmpty

        // A refresh still running beside the fold reads the index this flush would write.
        let state = joinRefresh ctx state

        let flushedState =
            flushPendingAnalysis
                db
                dbPath
                busyTimeout
                (fun label -> ctx.DeclareBoundedWork label ImpactSelectionDeadline)
                state

        // The flush wrote these analyses and their identities in one transaction.
        let flushedState =
            let written = state.PendingIdentities |> Map.keys |> Set.ofSeq

            { flushedState with
                PendingIdentities = Map.empty
                Unindexed = Set.difference flushedState.Unindexed written }

        let flushedState =
            match extensions with
            | Some exts when
                not exts.IsEmpty
                && (indexedSomething
                    || extensionsUnrefreshed.Value
                    || not flushedState.FailedExtensions.IsEmpty)
                ->
                extensionsUnrefreshed.Value <- false

                if not indexedSomething && flushedState.FailedExtensions.IsEmpty then
                    // Owed only because this process has not refreshed: nothing it indexed
                    // is in the answer, so it runs beside the fold (`refreshInFlight`).
                    refreshInFlight.Value <- Some(Tasks.Task.Run(refreshTimed exts))
                    flushedState
                else
                    // At info, because a refresh that holds the fold for minutes is
                    // otherwise invisible.
                    applyRefresh ctx (refreshTimed exts ()) flushedState
            | _ -> flushedState

        let runtimeSelection = runtimeCoverageSelection flushedState.ChangedFiles

        let runtimeObligations =
            if Map.isEmpty runtimeSelection.ProjectsByFile then
                flushedState.Debt.RuntimeObligations
            else
                mergeRuntimeCoverageObligations flushedState.Debt.RuntimeObligations runtimeSelection.ProjectsByFile

        if not (Map.isEmpty runtimeSelection.ProjectsByFile) then
            for KeyValue(file, projects) in runtimeSelection.ProjectsByFile do
                if not (Set.isEmpty projects) then
                    let projectNames = projects |> Set.toList |> String.concat ", "

                    Logging.info
                        "test-prune"
                        $"runtime coverage selected %s{file} -> %s{projectNames} (project-in-full)"

        reportRuntimeCoverageWidenings (Logging.warn "test-prune") runtimeSelection

        flushedState, runtimeObligations

    /// The impact selection over a flushed state, and the classification of every queued
    /// symbol by the projects covering it.
    let selectAffected (flushedState: TestPruneState) (runtimeObligations: RuntimeCoverageObligations) =
        // Affected tests must be computed from the WHOLE needs-testing queue —
        // the in-memory hot view UNION the durable sidecar — not just the latest
        // diff. The persisted queue holds symbols a green run hasn't yet cleared
        // (e.g. carried across a restart, or left behind by an Aborted/failed
        // run); they must keep selecting tests until a covering run passes.
        let symbols =
            Set.union flushedState.Debt.PendingQueue (Set.ofList flushedState.ChangedSymbols)
            |> Set.toList

        let affectedTests, seedFanout =
            if symbols.IsEmpty then
                [], SeedFanoutReading.NoSelection
            else
                // Scoped to projects this daemon actually runs. Selecting tests in an
                // unconfigured project would put classes in the run map that never
                // execute — and make `allChangesUncovered` (and so the zero-affected
                // skip) disagree with the commit rule. See `debtScope`.
                let queryRunnable (seeds: string list) =
                    queries.AffectedTests seeds
                    |> fun ts ->
                        if Set.isEmpty runnableProjects then
                            ts
                        else
                            ts |> List.filter (fun t -> Set.contains t.TestProject runnableProjects)

                let affected = queryRunnable symbols
                let sortedSeeds = List.sort symbols

                // Count the INPUT explicitly, not just the output. `symbols` is the
                // union of the durable pending-verification queue and the in-memory
                // hot view, so it grows monotonically across aborted runs until a
                // green run clears it — "the queue is wedged and growing" and "a
                // small, precise selection" look identical without this number, and
                // `%A` truncated it away at 100.
                //
                // The seed list is logged in FULL and sorted (`describeAll`, not
                // `%A`): when a handful of junk seeds drags in thousands of tests,
                // the offending seed is the whole diagnosis, and a sample that
                // happens to omit it costs hours.
                Logging.info
                    "test-prune"
                    $"QueryAffectedTests: %d{symbols.Length} seed(s) → %d{affected.Length} affected tests"

                Logging.info "test-prune" $"  seeds: %s{describeAll sortedSeeds}"

                // How many tests ONE seed selects on its own — a full recursive
                // reverse-walk each time, so it is memoised. Two diagnostics below ask
                // this same question (the poisoned-seed guard and the per-seed
                // attribution), and the seeds they ask about OVERLAP: `agedSeeds` is a
                // subset of `sortedSeeds`, so in the case that matters most — a wide
                // selection AND pinned seeds, which is exactly the shape both exist to
                // catch — every aged seed was paying for the same query twice in one
                // flush. Only `.Length` is ever used, so nothing needs the rows.
                let aloneCounts = System.Collections.Generic.Dictionary<string, int>()

                let aloneCount (seed: string) : int =
                    match aloneCounts.TryGetValue seed with
                    | true, n -> n
                    | false, _ ->
                        let n = (queryRunnable [ seed ]).Length
                        aloneCounts[seed] <- n
                        n

                // The poisoned-seed guard.
                //
                // The per-seed attribution below asks "is one seed dominating THIS run?",
                // a question about a moment. The failure that happened was about TIME:
                // `name` and `kind` dominated every run for days, and each run looked
                // like a legitimately expensive edit. Only width that PERSISTS separates
                // them.
                //
                // This only READS the ages — they advance once per test RUN, at the
                // launch point (see `pendingAgeRef`). Do not bump them here: this
                // function runs 2-3 times per edit-save cycle, so the counter would
                // measure flushes, and two edits to one function on a green repo would
                // trip a guard designed to fire late.
                //
                // Independent of `WideSelectionTests`: a seed pinning 200 tests of a
                // 400-test suite is the same disease as one pinning 3,000, and gating on
                // absolute width would miss every smaller repo.
                let ages = Volatile.Read(&pendingAgeRef)

                let ageOf s =
                    ages |> Map.tryFind s |> Option.defaultValue 0

                // Each check below is a full recursive reverse-walk, so both the count and
                // the decision to run at all are gated:
                //
                //  * an EMPTY selection can never yield a suspect (`isPoisonSuspect`
                //    requires `affectedCount > 0`), so the loop is skipped — the common
                //    case on a no-op cycle;
                //  * the same `MaxSeedsToAttribute` budget the attribution loop uses.
                //    `agedSeeds` grows precisely when the queue is wedged — the situation
                //    this guard exists to report — so an unbudgeted loop would pay N graph
                //    walks per build exactly when the daemon is already struggling. A
                //    suspect must account for >=25% of the selection, so at most four
                //    seeds can qualify and a large aged list is waste by construction.
                //    The cap is never silent.
                let agedSeeds = sortedSeeds |> List.filter (fun s -> ageOf s >= PoisonSeedRuns)

                if not affected.IsEmpty && agedSeeds.Length > MaxSeedsToAttribute then
                    Logging.warn
                        "test-prune"
                        $"%d{agedSeeds.Length} seed(s) have been queued across %d{PoisonSeedRuns}+ consecutive \
                          runs, which exceeds the %d{MaxSeedsToAttribute}-seed budget — the poisoned-seed check \
                          is SKIPPED this cycle. A pending queue that size is itself the finding; the full seed \
                          list is logged above."

                for seed in
                    (if affected.IsEmpty || agedSeeds.Length > MaxSeedsToAttribute then
                         []
                     else
                         agedSeeds) do
                    let runs = ageOf seed
                    let alone = aloneCount seed

                    if isPoisonSuspect runs affected.Length alone then
                        let pct = alone * 100 / (max 1 affected.Length)

                        // Deliberately a WARNING and not a quarantine: dropping the
                        // symbol would be under-testing on a guess. The failure this
                        // addresses is that nobody could SEE the pattern.
                        Logging.warn
                            "test-prune"
                            $"POSSIBLE POISONED SEED: '%s{seed}' has been queued for verification across \
                              %d{runs} consecutive runs and alone selects %d{alone} of %d{affected.Length} \
                              tests (%d{pct}%%). A symbol only leaves the queue once every runnable project \
                              covering it passes, so one persistently-failing project pins it and it re-seeds \
                              this selection every run. Check whether it is a real dependency-graph hub, a \
                              mis-qualified symbol, or a test project that has been red for a \
                              while."

                // Per-seed attribution, but ONLY when the selection is already wide.
                // A single seed accounting for most of a run is either a genuine
                // graph hub or a mis-qualified symbol, and is the only thing worth
                // looking at; below the threshold this costs nothing because the
                // re-query loop never runs.
                if affected.Length > WideSelectionTests then
                    if sortedSeeds.Length > MaxSeedsToAttribute then
                        // Never cap silently: say the attribution was skipped and why,
                        // so an absent breakdown is not read as "no seed dominated".
                        Logging.warn
                            "test-prune"
                            $"%d{affected.Length} tests selected, but %d{sortedSeeds.Length} seeds exceeds the \
                              %d{MaxSeedsToAttribute}-seed attribution budget — per-seed breakdown SKIPPED"
                    else
                        // Derived from the same constant `isPoisonSuspect` uses, never a
                        // second spelling of it: a `/ 4` here beside a `25` there drifts,
                        // and the two diagnostics then disagree about "dominant".
                        let dominantShare = affected.Length * PoisonSeedSharePercent / 100

                        for seed in sortedSeeds do
                            let alone = aloneCount seed

                            if alone > affected.Length then
                                // Not a hub: the selection is not monotone in its seeds. The
                                // composition-root fail-safe restores a test project's
                                // unbarriered tests only when the whole seed set leaves it
                                // empty, so a sibling seed that reaches the project directly
                                // can suppress the restore this seed gets on its own.
                                Logging.warn
                                    "test-prune"
                                    $"seed '%s{seed}' alone selects %d{alone} tests, MORE than the %d{affected.Length} \
                                      the whole queue selects — the composition-root fail-safe is not monotone in \
                                      its seeds, so this seed's own tests are narrower when it is queued with others"
                            elif alone > dominantShare then
                                let pct = alone * 100 / affected.Length

                                Logging.warn
                                    "test-prune"
                                    $"seed '%s{seed}' alone selects %d{alone} of %d{affected.Length} tests \
                                      (%d{pct}%%) — a dependency-graph hub, or a mis-qualified symbol"

                // The top seeds by fanout, for the verdict: the same memoised walks the
                // breakdown above paid for, under the same width and seed budget.
                let fanout =
                    SeedFanoutReading.ofSelection
                        WideSelectionTests
                        MaxSeedsToAttribute
                        affected.Length
                        sortedSeeds
                        aloneCount

                affected, fanout

        // Classify every considered symbol ONCE, under one resolution of the declared
        // exclusions (see `debtScope`):
        //
        //  * NOTHING OWED — no covering test at all, or only coverers that `tests.excluded`
        //    declares out of scope with a reason. Dropped from the durable queue now:
        //    retaining it would re-select zero tests forever and never empty. Only ever
        //    REMOVES from the queue. A declaration-covered drop is a WRITE-OFF, not a
        //    discharge, so it is REPORTED, naming the project, in the log and on the verdict
        //    (`UncoveredChanges`).
        //  * OWED ELSEWHERE — a covering project this daemon does not run and nothing
        //    declares excluded. Nothing here can discharge it, and dropping it would let a
        //    configured-suite green retire tests that never ran. It STAYS owed; the verdict
        //    stays red and names the project until the config lists or excludes it.
        let covering = coveringOf symbols
        let owedTo = lazyDebtScope covering
        let owing = symbols |> List.map (fun s -> s, owedTo s) |> Map.ofList

        // An index missing a file's analysis (`Unindexed`) cannot prove that nothing covers
        // a change: the missing file may hold the very test. Nothing is dropped as
        // uncovered then; it stays owed until a covering run passes.
        let indexIncomplete = not (Set.isEmpty flushedState.Unindexed)

        let uncovered =
            if indexIncomplete then
                Set.empty
            else
                owing
                |> Map.filter (fun _ projects -> Set.isEmpty projects)
                |> Map.keys
                |> Set.ofSeq

        let unrunnable: UnrunnableCoverage =
            if Set.isEmpty uncovered || Set.isEmpty runnableProjects then
                Map.empty
            else
                uncovered
                |> Set.toList
                |> List.choose (fun s ->
                    match covering s with
                    | projects when Set.isEmpty projects -> None
                    | projects -> Some(s, projects))
                |> Map.ofList

        let owedToUnrunnable =
            owing
            |> Map.map (fun _ projects -> Set.difference projects runnableProjects)
            |> Map.filter (fun _ projects -> not (Set.isEmpty projects))

        if not (Set.isEmpty uncovered) then
            Logging.info
                "test-prune"
                $"Dropping %d{Set.count uncovered} queued symbol(s) with no covering test in a governed project from pending-verification queue"

            if not (Map.isEmpty unrunnable) then
                let projects =
                    UnrunnableCoverage.projects unrunnable |> Set.toList |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{Map.count unrunnable} of them ARE covered — only by tests in %s{projects}, which \
                      `tests.excluded` declares out of this gate's scope. The obligation is written off by that \
                      declaration, not discharged by a test. Symbols: \
                      %s{describeAll (unrunnable |> Map.keys |> List.ofSeq)}"

        if not (Map.isEmpty owedToUnrunnable) then
            let projects =
                UnrunnableCoverage.projects owedToUnrunnable |> Set.toList |> String.concat ", "

            Logging.warn
                "test-prune"
                $"%d{Map.count owedToUnrunnable} queued symbol(s) stay OWED to tests in %s{projects}, which this \
                  daemon does not run and `tests.excluded` does not declare. No run here can discharge them, so \
                  the verdict stays red: list the project in `tests.projects`, or declare it in `tests.excluded` \
                  with a reason. Symbols: %s{describeAll (owedToUnrunnable |> Map.keys |> List.ofSeq)}"

        // Keep the in-memory hot view aligned with the durable queue so the
        // ChangedSymbols carried in state (and the cache-key snapshot) don't
        // re-select the uncovered symbols on the next event.
        let remainingSymbols =
            flushedState.ChangedSymbols
            |> List.filter (fun s -> not (Set.contains s uncovered))

        // There WERE symbols to consider this cycle, yet the affected set is empty — so
        // every one of them was just dropped as uncovered (a union query returning zero
        // tests means every per-symbol query did too). That is a definitive "nothing to
        // verify" green, which the run-trigger reads to complete immediately instead of
        // running the full suite. An EMPTY `symbols` (genuine cold start, nothing
        // pending) leaves this false so the baseline still runs.
        //
        // The flag buys a green that executes NOTHING, so it must rest
        // on proof — and an empty `QueryAffectedTests` proves "no test covers this" only
        // for a symbol the index KNOWS. For a name it has never heard of, the identical
        // empty result means "I cannot answer". So ask the index what it KNOWS
        // (`unknownToIndex`), never how it came to be in that state.
        //
        // The two files drift apart in practice: a `SchemaVersion` bump deletes and
        // recreates `test-impact.db`, but the pending-verification sidecar beside it
        // carries no version and SURVIVES. Every name in the queue then resolves to
        // nothing, is dropped just above as "no runnable covering test", and the cycle
        // completes GREEN with ZERO tests run — the debt discharged by the schema bump
        // rather than by a test. A stale queue entry naming a since-renamed symbol takes
        // the same route with no recreate involved.
        //
        // The symbols are still dropped from the queue above, so a permanently-absent
        // name cannot wedge it; the run happens once and discharges the debt.
        //
        // `GetAllSymbolNames` is a full read of the `symbols` table, so it sits behind
        // `noCoveringTest` — already the rare branch (queued symbols AND a zero-length
        // selection). On the ordinary path the index is never consulted.
        let noCoveringTest = not symbols.IsEmpty && List.isEmpty affectedTests

        let unknownToIndex =
            if not noCoveringTest then
                []
            else
                let known = db.GetAllSymbolNames()
                symbols |> List.filter (fun s -> not (known.Contains s)) |> List.sort

        let indexCannotVouch = noCoveringTest && not (List.isEmpty unknownToIndex)

        if indexCannotVouch then
            Logging.warn
                "test-prune"
                $"%d{symbols.Length} queued symbol(s) resolved to no covering test, but %d{unknownToIndex.Length} \
                  of them are NOT KNOWN to the symbol index — so for those that is 'the index cannot answer', not \
                  proof they are untested. Refusing the zero-test green; this run verifies them for real. \
                  Unknown: %s{describeAll unknownToIndex}"

        if noCoveringTest && indexIncomplete then
            Logging.warn
                "test-prune"
                $"%d{symbols.Length} queued symbol(s) resolved to no covering test, but the index lacks the analysis \
                  of %d{Set.count flushedState.Unindexed} file(s) a project-model change retired \
                  (%s{describeAll (Set.toList flushedState.Unindexed)}) — refusing the zero-test green"

        // Symbols still owed elsewhere are not uncovered: the zero-test green must not
        // retire the red they hold.
        let allChangesUncovered =
            if
                noCoveringTest
                && List.isEmpty unknownToIndex
                && Map.isEmpty owedToUnrunnable
                && not indexIncomplete
            then
                UncoveredChanges.AllUncovered(List.sort symbols, unrunnable)
            else
                UncoveredChanges.No

        // Remember the seeds ONLY when this selection actually has tests to run.
        // The question the report has to answer is "what was the last change that
        // DID trigger tests?" — a selection that chose nothing did not, and
        // recording it would answer that question with the one change guaranteed
        // to be irrelevant. Carrying the previous value forward is the point: it
        // is what survives to be reported by a later check that selects nothing.
        let seedsThatSelectedTests, fanoutOfThoseSeeds =
            if List.isEmpty affectedTests then
                flushedState.LastSeeds, flushedState.LastSeedFanout
            else
                List.sort symbols, seedFanout

        { flushedState with
            Debt =
                { commitPending uncovered flushedState.Debt with
                    RuntimeObligations = runtimeObligations }
            ChangedSymbols = remainingSymbols
            AffectedTests = Analyzed affectedTests
            ChangedSymbolsAllUncovered = allChangesUncovered
            LastSeeds = seedsThatSelectedTests
            LastSeedFanout = fanoutOfThoseSeeds }

    /// Flush, then select, unless the mode skips `FlushSelection`: pass-through records
    /// the debt without selecting or classifying it, and only a full run's green
    /// discharges it.
    let flushAndQueryAffected (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        let flushedState, runtimeObligations = flushPending ctx state

        // A selection that queries the index follows the extensions' edges.
        let flushedState =
            if
                Set.isEmpty flushedState.Debt.PendingQueue
                && List.isEmpty flushedState.ChangedSymbols
            then
                flushedState
            else
                joinRefresh ctx flushedState

        if TestMode.skips FlushSelection flushedState.Mode then
            { flushedState with
                Debt =
                    { flushedState.Debt with
                        RuntimeObligations = runtimeObligations }
                AffectedTests = Analyzed []
                ChangedSymbolsAllUncovered = UncoveredChanges.No }
        else
            selectAffected flushedState runtimeObligations

    /// `flushAndQueryAffected` under the bound it declares over itself
    /// (`ImpactSelectionDeadline`). Every caller goes through this: the fold is the same
    /// work whichever event drove it, and a caller that forgot the declaration would be
    /// the one the stall detector failed.
    let flushAndQueryAffectedBounded (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        use _declaration = ctx.DeclareBoundedWork "impact selection" ImpactSelectionDeadline
        flushAndQueryAffected ctx state

    // Per-file FCS freshness sidecar, loaded once at plugin construction from
    // `.fshw/test-prune/file-freshness.json` and updated incrementally on each
    // FileChecked. Survives daemon restarts so a cross-restart replay can decide which
    // files' stored symbols are trustworthy enough to run detectChanges against.
    //
    // Closure-local mutable cell + Volatile: only the Update handler reads and writes it.
    let mutable freshnessRef: FileFreshness.Store = FileFreshness.load repoRoot

    /// The clock `storedRowsExist: bool` did not have. Rows written
    /// seconds ago by this session's own scan are not a baseline; diffing the current
    /// extraction against them compares the file to itself, always reports "nothing
    /// changed", and selects zero test projects — on a diff that ADDS tests. See
    /// `FileFreshness.PriorRowLedger`, which owns the rule.
    let priorRows = FileFreshness.PriorRowLedger()

    let updateFreshness (newStore: FileFreshness.Store) =
        Volatile.Write(&freshnessRef, newStore)

        try
            FileFreshness.save repoRoot newStore
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist file-freshness sidecar: %s{ex.Message}; in-memory state still updated"

    // The project model generation the sidecar was last reconciled against. A file
    // deleted together with its compile entry is unregistered by the re-evaluation that
    // publishes the next model, so it gets no FileChecked and no `stamp` forgets it; the
    // model moving is the event that says such a file may be gone.
    let mutable freshnessReconciledAt: int64 option = None

    let reconcileFreshness (modelGeneration: int64 option) =
        match modelGeneration with
        | Some generation when modelGeneration <> Volatile.Read(&freshnessReconciledAt) ->
            Volatile.Write(&freshnessReconciledAt, modelGeneration)
            let kept, dropped = FileFreshness.dropGone repoRoot (Volatile.Read(&freshnessRef))

            if not dropped.IsEmpty then
                for relPath in dropped do
                    Logging.info
                        "test-prune"
                        $"%s{relPath} no longer exists; dropping its freshness record (project model generation %d{generation})"

                updateFreshness kept
        | _ -> ()

    // Seed the in-memory hot view from the durable queue so a restart with a non-empty
    // queue re-flags those symbols. Without this, a restart diffs current symbols against
    // the already-advanced analysis snapshot → "nothing changed" → zero tests run → false
    // green.
    let initialState =
        { Debt = loadedDebt
          Mode = initialMode
          InFlight = None
          LastRed = None
          SealedTree = None
          AwaitingDiff = None
          OwedRunHeld = false
          CompletedRuns = []
          CheckReaches = []
          Replies = []
          PendingAnalysis = Map.empty
          AnalysisModelGeneration = None
          AnalysisFiles = Map.empty
          CohortSeal = None
          AnalysisReceipt = None
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = loadedDebt.PendingQueue |> Set.toList
          ChangedFiles = []
          LastResults = None
          LastSeeds = []
          LastSeedFanout = SeedFanoutReading.NoSelection
          DebtDuringFullRun = Map.empty
          CoveredFullSuite = None
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false
          PriorProjectFingerprints = Map.empty
          PendingForceRunProjects = Set.empty
          ChangedSymbolsAllUncovered = UncoveredChanges.No
          UnanalyzableFiles = Map.empty
          NotSelected = Map.empty
          PendingIdentities = Map.empty
          Unindexed = Set.empty
          IndexGuard = IndexedAnalysis.Guard mayReplay
          FailedExtensions = Map.empty
          // The previous session's reds, quarantined into the first run.
          OutstandingFailures = loadedFailures
          LastCoverage = RunCoverage.none
          LastZeroSelection = ZeroSelection.NotAZero
          Receipt = HeldReceipt.NoneYet
          Earned = None
          RestoredEvidence = restoredEvidence }

    /// The generation of the model the host currently publishes, when it is available.
    let observeModelGeneration (ctx: PluginCtx<TestPruneMsg>) =
        ObservedModel.generation ctx.ProjectGraph

    /// Each configured test project's `.fsproj` and compile-input identity now.
    let observeTestInputs (ctx: PluginCtx<TestPruneMsg>) =
        ObservedModel.testInputs ctx.ProjectGraph (defaultArg testConfigs [] |> List.map _.Project)

    /// The evidence candidates of `configs`: each project the model knows a `.fsproj` for, with
    /// its reference closure and keyed configuration.
    let evidenceCandidates (graph: ProjectGraphAccessor) (configs: TestConfig list) : TestEvidenceRun.Candidate list =
        let files = ObservedModel.projectFiles graph

        configs
        |> List.choose (fun config ->
            Map.tryFind config.Project files
            |> Option.map (fun fsproj ->
                { TestEvidenceRun.Candidate.Project = config.Project
                  ProjectFile = fsproj
                  Closure = TestEvidenceRun.closureOf graph.GetProjectReferences fsproj
                  TestConfig = canonicalTestConfig config }))

    /// The outcome-store key of each configured project's full run over the tree as it is now
    /// (ADR-059 step 7b), for the `test-scope` reply. Empty without a store or without the
    /// model's compile-input identities: nothing can be keyed then.
    let currentOutcomeKeys (graph: ProjectGraphAccessor) : Map<string, string> =
        match evidence, ObservedModel.inputs graph with
        | Some wiring, Some identities ->
            TestEvidenceRun.currentOutcomeKeys
                wiring
                repoRoot
                (fun fsproj -> Map.tryFind fsproj identities)
                (evidenceCandidates graph (defaultArg testConfigs []))
        | _ -> Map.empty

    /// The test-evidence plan of a run about to launch `configs`: every project the model
    /// can key, and a lookup for each one `launching` admits that has no outstanding
    /// failure, when `lookups` allows any. `None` without a store, or without the
    /// model's compile-input identities (nothing can be keyed without them).
    let evidencePlanFor
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (launching: string -> bool)
        (lookups: bool)
        (outstanding: OutstandingFailure list)
        : TestEvidenceRun.Plan option =
        evidence
        |> Option.bind (fun wiring ->
            match ObservedModel.inputs ctx.ProjectGraph with
            | None ->
                Logging.info "test-prune" "test evidence: the project model publishes no identities — nothing keyed"
                None
            | Some identities ->
                let failing = outstanding |> List.map _.Project |> Set.ofList
                let candidates = evidenceCandidates ctx.ProjectGraph configs

                let plan =
                    TestEvidenceRun.plan
                        wiring
                        repoRoot
                        (fun fsproj -> Map.tryFind fsproj identities)
                        candidates
                        (fun project -> lookups && launching project && not (failing.Contains project))

                for KeyValue(project, why) in plan.Misses do
                    Logging.info "test-prune" $"%s{project}: launching — %s{why}"

                Some plan)

    /// Hold a launch while the host discovers its project model (`ObservedModel.awaitDiscovery`),
    /// so the run is launched under the model it completes under. Called inside the launch's
    /// exclusive work, before anything about the model is read.
    let awaitDiscoveryBeforeLaunch (ctx: PluginCtx<TestPruneMsg>) =
        async {
            match ctx.ProjectGraph.ObserveModel() with
            | FsHotWatch.ProjectModel.Observation.Rediscovering generation ->
                Logging.info
                    "test-prune"
                    $"project discovery (generation %d{generation}) is in flight; waiting for it before launching"
            | _ -> ()

            let! wait = ObservedModel.awaitDiscovery ctx.ProjectGraph
            ObservedModel.reportDiscoveryWait (Logging.warn "test-prune") (Logging.info "test-prune") wait
        }

    /// A launch, named by the model it was selected under and what it selected, so the
    /// completion line (`describeCompletionEvidence`) can be read against it.
    let logLaunch (kind: string) (mode: TestMode) (launch: TestRunLaunch) =
        let scope =
            if TestMode.requestsFullSuite mode then
                "confirm mode"
            else
                "check mode"

        Logging.info
            "test-prune"
            $"Launching %s{kind} run under project model generation %s{ObservedModel.describe launch.ModelGeneration} (%s{scope}, %d{Map.count launch.Selection} project(s) selected)"

    /// Which cause selected each project of a run that is about to execute. Logged once
    /// the run is past the zero-affected skip, which executes nothing and selects nothing.
    let logSelectedBy (launch: TestRunLaunch) =
        Logging.info "test-prune" $"Selected by: %s{ProjectAttribution.describe launch.SelectedBy}"

        // Priced before it runs, from the class durations the newest runs recorded
        // (`SelectionPrice`). A diagnostic: a price that cannot be read never stops a run.
        try
            let book =
                SelectionPrice.Book.ofRunsDir SelectionPrice.DefaultWindow (FsHotWatch.Ctrf.reportsDir repoRoot)

            Logging.info
                "test-prune"
                $"Priced: %s{SelectionPrice.describe (SelectionPrice.price book (askedOf launch.Selection))}"
        with ex ->
            Logging.warn "test-prune" $"Priced: not priced — %s{ex.Message}"

    /// Returns the `TestsFinished` message the framework's RunExclusive posts back to the
    /// agent; the synchronous `Custom(TestsFinished)` handler emits `TestRunCompleted`
    /// inside the cache-write capture window. `TestRunStarted` fires before the host starts.
    /// Catches its own exceptions to produce an `Aborted` lifecycle — letting RunExclusive
    /// eat the message would free the slot with no completion posted, stranding
    /// `LastResults` and the debt the run launched against.
    let runTestsWithImpact
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        // The four fields a run reads — NOT the state record. The async this
        // returns outlives the state generation it was launched from; see
        // `TestRunInputs`.
        (inputs: TestRunInputs)
        // `hasCachedResults` (`state.LastResults.IsSome`, computed by the caller)
        // means a run already completed THIS session — i.e. a green baseline exists
        // to be "test-equivalent" to. The zero-affected skip needs this AS WELL AS an
        // empty queue: a cold daemon with an empty queue but no prior run has no
        // baseline yet and must run the full suite once to establish one. See
        // the skip gate below.
        (hasCachedResults: bool)
        // Dependency-fanout (DependencyFanout): test projects whose dependency
        // fingerprint changed since the last build. Their tests run in FULL
        // (project-coarse), UNIONED with the symbol-precise selection — a binary
        // change the symbol diff can't see still re-runs the dependent tests.
        // Empty in the common case (no dependency change), so ordinary
        // source-symbol edits keep their precise, minimal selection.
        (fanoutProjects: Set<string>)
        : Async<TestPruneMsg> =
        async {
            do! awaitDiscoveryBeforeLaunch ctx

            let mutable emittedStart: TestRunStarted option = None

            let emitStarted started =
                emittedStart <- Some started
                ctx.EmitTestRunStarted started

            // The single chokepoint every launch path funnels through (BatchChecked
            // drain, BuildCompleted, deferred rerun). Both widenings live here rather
            // than at each call site, so no future launch path can forget one and
            // quietly reintroduce a filtered run where an unfiltered one was owed.
            //
            //  * full-suite scope: run EVERY project, unfiltered.
            //  * unanalysable files: run every project, because a
            //    selection made without them cannot be trusted.
            //  * an UNREADABLE pending-verification ledger: run every
            //    project, because the ledger names what is still owed, and a selection
            //    made without it cannot be trusted either. Same shape as 113: the
            //    missing input is a SAFETY input, so its absence widens rather than
            //    narrows.
            //  * NO VALID FULL-SUITE BASELINE: run every project,
            //    because the tests a filtered run skips have nothing to be equivalent
            //    to. A cold repository earns its baseline here; a repository whose
            //    `tests.projects` grew re-earns it. Same shape as 150.
            let scopeIsFullSuite = TestMode.requestsFullSuite inputs.Mode
            let ledgerUnreadable = inputs.Debt.RecoveryOutstanding
            let baselineInvalid = baselineInvalidReason inputs.Debt

            // The coarse fallback only needs to know WHICH files are unanalysable; the
            // map's values exist so the ledger projection can re-report their
            // diagnostics.
            let unanalyzablePaths = inputs.UnanalyzableFiles |> Map.keys |> Set.ofSeq

            // A failed extension is a hole in the graph exactly as an unanalysable file
            // is: its stored edges describe an older tree. Same coarse fallback.
            let coarseGaps =
                inputs.FailedExtensions
                |> Map.keys
                |> Seq.map extensionLedgerKey
                |> Set.ofSeq
                |> Set.union unanalyzablePaths
                |> Set.union inputs.Unindexed

            let launchedRuntimeObligations = inputs.Debt.RuntimeObligations

            // A project whose runtime coverage did not ingest runs in full too: what its tests
            // reach is not known, so no runtime selection of it can be trusted.
            let runtimeForceProjects =
                launchedRuntimeObligations
                |> Map.values
                |> Seq.collect Map.keys
                |> Set.ofSeq
                |> Set.union (
                    inputs.Debt.CoverageNotIngested
                    |> Map.keys
                    |> Set.ofSeq
                    |> Set.intersect (fullSuiteProjects configs)
                )

            // A project with no whole-project run under the current model can only be
            // vouched for by running it in full: filtered or skipped, it is a refusal in
            // the evidence this run earns, and a later run that selects nothing keeps that
            // refusal. After a model change or a restart, this is every project the
            // current model has not yet seen run whole.
            let evidenceGap =
                EarnedEvidence.wholeProjectGap
                    (observeModelGeneration ctx)
                    (ObservedModel.inputs ctx.ProjectGraph)
                    (fullSuiteProjects configs)
                    inputs.Earned

            let fullSuiteCause =
                fullSuiteCauseOf
                    scopeIsFullSuite
                    ledgerUnreadable
                    (Option.isSome baselineInvalid)
                    (not (Set.isEmpty coarseGaps))
                    (Set.isSubset (fullSuiteProjects configs) evidenceGap)

            let forceRunProjects =
                let widened = coarseFallbackProjects configs coarseGaps fanoutProjects
                let widened = Set.union widened runtimeForceProjects
                let widened = Set.union widened evidenceGap

                if scopeIsFullSuite || ledgerUnreadable || Option.isSome baselineInvalid then
                    Set.union widened (fullSuiteProjects configs)
                else
                    widened

            if scopeIsFullSuite then
                Logging.info
                    "test-prune"
                    "Scope: FULL SUITE — impact filtering is disabled for this run; every configured test project runs in full"

            if not (Set.isEmpty evidenceGap) then
                let names = evidenceGap |> Set.toList |> String.concat ", "

                Logging.info
                    "test-prune"
                    $"Evidence gap: %d{Set.count evidenceGap} project(s) have no whole-project run under the current project model (%s{names}) — running them in full, so this run's evidence can support a green"

            match baselineInvalid with
            | Some reason when not ledgerUnreadable ->
                Logging.info
                    "test-prune"
                    $"Scope: FULL SUITE (no valid full-suite baseline) — %s{reason}. Every configured test project runs in full; impact filtering resumes once this run has accounted for every project."
            | _ -> ()

            if ledgerUnreadable then
                Logging.warn
                    "test-prune"
                    $"Scope: FULL SUITE (verification debt unknown: %s{describeUnknownDebt inputs.Debt.UnknownDebt}) — what still needs testing is not known, so this run cannot know what it owes. It runs EVERY configured test project in full rather than trust an impact selection made without that record. Impact filtering resumes once a full suite passes."

            if not (Map.isEmpty inputs.FailedExtensions) then
                let names = inputs.FailedExtensions |> Map.keys |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{inputs.FailedExtensions.Count} test-impact extension(s) failed their last refresh (%s{names}) — their edges describe an older tree, so this run falls back to EVERY test project in full rather than trusting a selection made over them"

            if not (Set.isEmpty inputs.Unindexed) then
                Logging.warn
                    "test-prune"
                    $"%d{Set.count inputs.Unindexed} file(s) whose analysis a project-model change retired are not in the impact index yet (%s{describeAll (Set.toList inputs.Unindexed)}) — this run falls back to EVERY test project in full until they are analysed again"

            if not (Set.isEmpty unanalyzablePaths) then
                let names = unanalyzablePaths |> Set.toList |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{Set.count unanalyzablePaths} file(s) could not be analysed (%s{names}) — their symbols are missing from the impact graph, so this run falls back to EVERY test project in full rather than trusting a selection made without them"

            // The queue snapshot this run is LAUNCHED against — the durable
            // queue UNION the in-memory hot view. Captured here (not read from
            // state at completion time) because mid-run BatchChecked flushes
            // mutate both; the synchronous TestsFinished handler commits ONLY
            // these symbols and leaves mid-run arrivals queued for the rerun.
            let launchedSymbols =
                Set.union inputs.Debt.PendingQueue (Set.ofList inputs.ChangedSymbols)

            let launchedRevisions =
                launchedSymbols
                |> Set.toList
                |> List.map (fun symbol -> symbol, revisionOf inputs.Debt symbol)
                |> Map.ofList

            // Advance the poisoned-seed counters HERE, at the launch of
            // a test RUN, so the count means what `PoisonSeedRuns` and the warning text
            // claim. `flushAndQueryAffected` runs several times per edit-save cycle.
            if not (TestMode.skips SeedAgeing inputs.Mode) then
                Volatile.Write(
                    &pendingAgeRef,
                    bumpSeedAges (Volatile.Read(&pendingAgeRef)) (Set.toList launchedSymbols)
                )

            try
                // For each launched symbol, the set of test PROJECTS whose tests
                // cover it. An empty set ⇒ no covering test. Queried per-symbol so
                // a symbol commits ONLY when every project covering IT passed (a
                // run-wide union would over-couple unrelated symbols). Empty queue ⇒
                // no queries. Kept INSIDE the try: these are DB reads that can throw
                // transiently on a cold/contended box (SQLITE_BUSY, schema drift). A
                // throw here must produce the Aborted lifecycle below (an honest,
                // re-runnable "tests did not run" verdict) rather than escaping to
                // the framework's `runOne`, which would only log-and-strand the run.
                // The projects gating a symbol's commit are `debtScope`'s — the same
                // rule `flushAndQueryAffected` uses to drop symbols, so the two cannot
                // disagree. An unconfigured, undeclared coverer blocks the commit on
                // purpose: its tests never ran, so they verified nothing.
                //
                // A pass-through run asks nothing: it runs every project, and its
                // completion discharges by the full run's green (see `TestsFinished`).
                let coveringProjectsBySymbol =
                    if TestMode.skips LaunchCoveringCapture inputs.Mode then
                        Map.empty
                    else
                        let owedTo = lazyDebtScope (coveringOf launchedSymbols)

                        launchedSymbols |> Set.toList |> List.map (fun s -> s, owedTo s) |> Map.ofList

                // Extension-contributed edges were already written to the DB by
                // flushAndQueryAffected, so `inputs.AffectedTests` already includes tests
                // reachable through extension edges (sql, sql-hydra, falco, etc.).
                //
                // A pass-through flush selects nothing, so its launch asks the one grouped
                // question the check-reach sample needs: what `check` would have selected.
                let affectedTestsList =
                    if TestMode.skips FlushSelection inputs.Mode then
                        if Set.isEmpty launchedSymbols then
                            []
                        else
                            queries.AffectedTests(Set.toList launchedSymbols)
                            |> List.filter (fun t ->
                                Set.isEmpty runnableProjects || Set.contains t.TestProject runnableProjects)
                    else
                        match inputs.AffectedTests with
                        | Analyzed tests -> tests
                        | NotYetAnalyzed -> []

                let symbolAffectedByProject =
                    affectedTestsList
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
                    |> Map.ofList

                // `tests.traces.select: "shadow"`: what the recorded traces would select,
                // beside the static symbol selection above, from the same seeds and index at
                // the same instant. Logged now, stored at completion; it selects nothing.
                let traceShadow =
                    match traces with
                    | Some wiring when wiring.Policy.Select = SelectShadow ->
                        let symbols = TestPrune.Ports.toSymbolStore db

                        TraceShadow.atLaunch
                            wiring.Policy
                            repoRoot
                            (configs |> List.map (fun c -> c.Project) |> List.distinct)
                            (fun project -> symbols.GetTestMethodsInProjects [ project ])
                            (lazy (TestPrune.Trace.TraceIngest.versionHashes symbols))
                            affectedTestsList
                            (Logging.info "test-prune")
                    | _ -> []

                // A prior red is mandatory verification debt. The
                // graph for today's edit may not reach yesterday's failing class, but
                // the next ordinary run must still execute it. Unknown class scope is
                // conservatively the whole project.
                let quarantinedAffectedByProject =
                    OutstandingFailure.quarantine
                        (fullSuiteProjects configs)
                        inputs.OutstandingFailures
                        symbolAffectedByProject

                // UNION the dependency-fanout: each force-run project enters the
                // map with an EMPTY class list, which `buildFilterArgs` treats as
                // "no filter → run ALL tests in this project" (a project ABSENT
                // from a non-empty map is skipped; present-with-[] runs in full).
                // We don't overwrite a project that already has symbol-affected
                // classes — its precise selection plus a forced full run are the
                // same "run this project"; [] (full) is the safe superset, so a
                // fanout hit promotes a partially-selected project to full.
                let affectedByProject =
                    forceRunProjects
                    |> Set.fold (fun acc proj -> Map.add proj [] acc) quarantinedAffectedByProject

                // The skip gate counts symbol-affected classes only. A pure
                // dependency-fanout (force-run projects, zero symbol classes) must
                // NOT be counted as "0 affected" and skipped — so the gate below
                // also checks `forceRunProjects` is empty.
                let totalClasses =
                    quarantinedAffectedByProject |> Map.values |> Seq.sumBy List.length

                let inputTree = ReceiptInputTree.readTree repoRoot

                // Nothing is owed and nothing was selected, but the tree moved since the
                // evidence was earned, by an edit the symbol diff saw as changing no
                // symbol. Running every project in full is what a non-source edit costs;
                // an edit confined to compiled F# source files costs the classes covering
                // their symbols (`SymbolFreeEdit`). `None` when this is not that launch.
                let symbolFreeEdit =
                    let unverified =
                        not (ReceiptInputTree.matches inputs.EvidenceTree (inputTree |> Option.map _.Identity))

                    let nothingSelected =
                        totalClasses = 0
                        && Set.isEmpty forceRunProjects
                        && List.isEmpty inputs.OutstandingFailures
                        && not (UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered)

                    if
                        unverified
                        && hasCachedResults
                        && not scopeIsFullSuite
                        && nothingOwed inputs.Debt
                        && nothingSelected
                    then
                        let compiled =
                            ctx.ProjectGraph.ObserveCheckableFiles()
                            |> Option.map (fun (_, files) ->
                                fun (rel: string) -> files.Contains(AbsFilePath.create (Path.Combine(repoRoot, rel))))

                        let index: SymbolFreeEdit.Index =
                            { SymbolsIn = fun rel -> db.GetSymbolsInFile rel |> List.map _.FullName |> List.distinct
                              TestsIn = db.GetTestMethodsInFile
                              Affected = queries.AffectedTests }

                        match
                            SymbolFreeEdit.select
                                inputs.EvidenceManifest
                                inputTree
                                inputs.SealedTree
                                compiled
                                (fun project -> Set.isEmpty runnableProjects || Set.contains project runnableProjects)
                                index
                        with
                        // No test covers the edited files, and no always-class stands in
                        // for them: only running can earn evidence for the edited tree.
                        | SymbolFreeEdit.Covering(files, _, classes) when
                            Map.isEmpty classes && Map.isEmpty (AlwaysClasses.only configs)
                            ->
                            SymbolFreeEdit.Refused
                                $"no runnable test covers a symbol of %s{describeAll files}, and no project declares alwaysClasses"
                        | narrowing -> narrowing
                        |> Some
                    else
                        None

                let affectedByProject, narrowedSeeds =
                    match symbolFreeEdit with
                    | Some(SymbolFreeEdit.Covering(files, seeds, classes)) ->
                        let classCount = classes |> Map.values |> Seq.sumBy List.length

                        let running =
                            if classCount = 0 then
                                $"no runnable test covers their %d{List.length seeds} symbol(s), so running only the declared alwaysClasses"
                            else
                                $"running the %d{classCount} class(es) covering their %d{List.length seeds} symbol(s)"

                        Logging.info
                            "test-prune"
                            $"No affected classes, and the tree moved since the evidence was earned only by an edit to %d{List.length files} compiled F# source file(s) the symbol diff saw as changing no symbol (%s{describeAll files}); %s{running} instead of every project in full"

                        classes, Some seeds
                    | Some(SymbolFreeEdit.Refused _)
                    | None -> affectedByProject, None

                // ALWAYS-CLASSES. Every filtered launch carries each project's declared
                // `alwaysClasses`: tests that read the source tree as data, which no
                // symbol edge leads to. An edit whose every changed symbol has no
                // covering test would otherwise take the nothing-to-verify skip below,
                // yet the tree changed under classes that read all of it, so that edit
                // runs exactly those classes instead. So does an edit no symbol diff sees
                // to compiled files no test covers (`SymbolFreeEdit.Covering` with no
                // class). An empty selection is otherwise left alone: it already runs
                // every project in full.
                let uncoveredSymbolFreeEdit =
                    match symbolFreeEdit with
                    | Some(SymbolFreeEdit.Covering(_, _, classes)) -> Map.isEmpty classes
                    | _ -> false

                let alwaysOnly =
                    totalClasses = 0
                    && Set.isEmpty forceRunProjects
                    && List.isEmpty inputs.OutstandingFailures
                    && Map.isEmpty affectedByProject
                    && (UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered
                        || uncoveredSymbolFreeEdit)
                    && not (Map.isEmpty (AlwaysClasses.only configs))

                let affectedByProject =
                    if alwaysOnly then
                        if not uncoveredSymbolFreeEdit then
                            Logging.info
                                "test-prune"
                                "Every changed symbol has no covering test, but the tree changed under the declared alwaysClasses; running only those"

                        AlwaysClasses.only configs
                    else
                        AlwaysClasses.widen configs affectedByProject

                // TEST EVIDENCE, at launch: AFTER selection, so it can only take away
                // launches the selector chose, never add a green it did not. Keyed now,
                // before `beforeRun` touches anything. Every run keys (so its greens can be
                // published); only a run that owes nothing a reuse cannot pay looks
                // anything up. `confirm` owes a run of everything. A run owed a
                // full-suite baseline owes one only running can earn: a green is graded
                // relative to it, so a checkout that reused instead would answer "no
                // verdict" for as long as the evidence lasted. An unreadable ledger is
                // owed a full suite that passed.
                let evidenceLookups =
                    not scopeIsFullSuite && not ledgerUnreadable && Option.isNone baselineInvalid

                let evidencePlan =
                    evidencePlanFor
                        ctx
                        configs
                        (fun project -> Map.isEmpty affectedByProject || Map.containsKey project affectedByProject)
                        evidenceLookups
                        inputs.OutstandingFailures

                /// The run's SCOPE, in the same shape `executeTests`
                /// will actually honour, captured on the launch so the completion
                /// handler knows what this run is entitled to clear.
                ///
                /// Mirrors `executeTests`' own reading of `affectedClassesByProject`
                /// EXACTLY, which is why it is derived from that map and not
                /// re-decided: an EMPTY map means "no selection" → every project runs
                /// in full; a NON-empty map means a project present with `[]` runs in
                /// full, a project present with classes runs filtered, and a project
                /// ABSENT is skipped entirely (and recorded as a filtered pass that
                /// proves nothing — the laundering vector).
                let wholeSelection: Map<string, ProjectSelection> =
                    selectionOf configs affectedByProject

                // CHECK-SKIP CLASSES. A project this launch takes whole only for widenings
                // its `checkSkipOn` lists runs without its `checkSkipClasses`, under its
                // exclude filter; any other reason runs them. `confirm` is such a reason, so
                // it never skips. The classes the run selected by name always run.
                let checkSkipWidenings: CheckSkipLaunch.Widenings =
                    { FullSuiteRequested = scopeIsFullSuite
                      LedgerUnreadable = ledgerUnreadable
                      NoFullSuiteBaseline = Option.isSome baselineInvalid
                      CoarseGaps = not (Set.isEmpty coarseGaps)
                      EvidenceGap = evidenceGap
                      DependencyFanout = fanoutProjects
                      RuntimeCoverage = runtimeForceProjects
                      ProjectLevelReds =
                        quarantinedAffectedByProject
                        |> Map.filter (fun _ classes -> List.isEmpty classes)
                        |> Map.keys
                        |> Set.ofSeq
                      EmptySelection =
                        if Map.isEmpty affectedByProject then
                            Some(
                                zeroAffectedWidening
                                    hasCachedResults
                                    inputs.Debt.UnknownDebt
                                    (Set.count inputs.Debt.PendingQueue)
                                    inputs.Debt.RuntimeObligations
                                    (inputs.Debt.CoverageNotIngested |> Map.keys |> Set.ofSeq)
                                    (List.length inputs.OutstandingFailures)
                                    baselineInvalid
                                    evidenceGap
                                    (not (
                                        ReceiptInputTree.matches
                                            inputs.EvidenceTree
                                            (inputTree |> Option.map _.Identity)
                                    ))
                            )
                        else
                            None }

                let namedClasses: Map<string, Set<string>> =
                    let symbolFreeClasses =
                        match symbolFreeEdit with
                        | Some(SymbolFreeEdit.Covering(_, _, classes)) -> classes
                        | _ -> Map.empty

                    [ quarantinedAffectedByProject; AlwaysClasses.only configs; symbolFreeClasses ]
                    |> List.collect Map.toList
                    |> List.groupBy fst
                    |> List.map (fun (project, entries) -> project, entries |> List.collect snd |> Set.ofList)
                    |> Map.ofList

                let checkSkipDecisions =
                    CheckSkipLaunch.decide configs checkSkipWidenings namedClasses wholeSelection

                let excludedByProject = CheckSkipLaunch.excludedOf checkSkipDecisions

                for line in CheckSkipLaunch.describe checkSkipDecisions do
                    Logging.info "test-prune" line

                let selection: Map<string, ProjectSelection> =
                    CheckSkipLaunch.narrow excludedByProject wholeSelection


                // Which cause put each project in this run. Every source is kept apart
                // up to here; unioned, a wide selection cannot be told from an honest one.
                let selectedBy =
                    let everyProject = fullSuiteProjects configs

                    let sources: SelectionSources =
                        { FullSuite =
                            if scopeIsFullSuite || ledgerUnreadable || Option.isSome baselineInvalid then
                                everyProject
                            else
                                Set.empty
                          CoarseFallback = if Set.isEmpty coarseGaps then Set.empty else everyProject
                          EvidenceGap = evidenceGap
                          DependencyFanout = fanoutProjects
                          RuntimeCoverage = runtimeForceProjects
                          OutstandingFailure =
                            quarantinedAffectedByProject
                            |> Map.filter (fun project classes ->
                                Map.tryFind project symbolAffectedByProject <> Some classes)
                            |> Map.keys
                            |> Set.ofSeq
                          ChangedSymbols = symbolAffectedByProject |> Map.keys |> Set.ofSeq
                          SymbolFreeEdit =
                            match symbolFreeEdit with
                            | Some(SymbolFreeEdit.Covering(_, _, classes)) -> classes |> Map.keys |> Set.ofSeq
                            | _ -> Set.empty
                          AlwaysClasses = AlwaysClasses.only configs |> Map.keys |> Set.ofSeq }

                    SelectionAttribution.attribute sources affectedByProject selection
                    |> CheckSkipLaunch.attribute excludedByProject

                // The SAME derivation with the full-suite widening taken
                // back out — what `check` would have launched over this tree, this
                // instant. Every OTHER widening stays: the coarse fallback
                // for unanalysable files and the unreadable-ledger fallback
                // apply to the inner loop too, so removing them would project a selection
                // narrower than the one `check` actually uses and manufacture misses the
                // selector never made.
                //
                // Computed here, at the chokepoint, and NOT re-derived later: by
                // completion the symbol queue has moved on, which is exactly why the
                // reading was previously unrecoverable.
                let wouldHaveRun: Map<string, ProjectSelection> option =
                    if not scopeIsFullSuite then
                        // Nothing was widened past — the run IS the impact selection, and
                        // there is no second reading to project.
                        None
                    else
                        wouldHaveRunSelection
                            configs
                            quarantinedAffectedByProject
                            (Set.union (coarseFallbackProjects configs coarseGaps fanoutProjects) evidenceGap)
                            runtimeForceProjects
                            ledgerUnreadable
                        // What `check` would have left out of the projects it took whole:
                        // the same decision, without the full-suite widening.
                        |> fun projected ->
                            let checkWidenings =
                                { checkSkipWidenings with
                                    FullSuiteRequested = false
                                    NoFullSuiteBaseline = false
                                    EmptySelection = None }

                            CheckSkipLaunch.narrow
                                (CheckSkipLaunch.excludedOf (
                                    CheckSkipLaunch.decide configs checkWidenings namedClasses projected
                                ))
                                projected
                        |> Some

                let launch =
                    { InputTree = inputTree
                      ModelGeneration = observeModelGeneration ctx
                      ProjectInputs = observeTestInputs ctx
                      Symbols = launchedSymbols
                      SymbolRevisions = launchedRevisions
                      ChangedFiles = inputs.ChangedFiles
                      TestHostStartedAt = None
                      CoveringProjectsBySymbol = coveringProjectsBySymbol
                      RuntimeProjectsByFile = launchedRuntimeObligations
                      Selection = selection
                      WouldHaveRun = wouldHaveRun
                      Reused = Map.empty
                      Seeds = narrowedSeeds |> Option.defaultValue inputs.Seeds
                      ZeroSelection = ZeroSelection.NotAZero
                      FullSuiteCause = fullSuiteCause
                      SelectedBy = selectedBy
                      SeedFanout = inputs.SeedFanout
                      TraceShadow = traceShadow }

                logLaunch "impact" inputs.Mode launch

                let hasQuarantinedFailures = not (List.isEmpty inputs.OutstandingFailures)

                // Two independent routes to the degenerate zero-affected skip. Both
                // terminate as a clean green via the same lifecycle, differing from
                // "tests exist and all passed" only in that zero ran:
                //
                //  (1) Baseline-equivalent. Queue PROVABLY empty AND a session baseline
                //      exists. An empty queue means "test-equivalent to the last green
                //      run", so "0 affected tests" is a sound green. Both halves are
                //      load-bearing: a NON-empty queue with 0 affected classes (covered
                //      symbols whose tests aren't indexed yet) must run the suite rather
                //      than silent-green, and the first run of a session has no baseline
                //      to be equivalent TO, so it must run the full suite to establish
                //      one. Reads `nothingOwed`, not `Set.isEmpty` — an unreadable ledger
                //      owes an unknown debt and can never be baseline-equivalent
                //
                //  (2) Nothing-to-verify. This cycle HAD changed/queued symbols and every
                //      one proved to have no covering test (`ChangedSymbolsAllUncovered`,
                //      set by `flushAndQueryAffected` as it dropped them), so even a
                //      cold-start full suite would verify nothing about them. Sound
                //      WITHOUT a session baseline, unlike route 1, because it is gated on
                //      symbols having existed and provably lacking any test. Without it
                //      an all-uncovered cold run falls through to the full suite and
                //      hangs, never resolving WaitForComplete. A genuine cold start with
                //      NO pending symbols leaves the flag false, so the baseline runs.
                //
                //  Route 1 also needs evidence bound to THIS tree. Test-equivalence is a
                //  claim about symbols; a receipt is a claim about bytes, and an edit the
                //  symbol diff does not see (a comment) moves the bytes. Skipping there
                //  earns nothing, so `check` could only answer "no tests ran".
                let treeUnverified =
                    not (ReceiptInputTree.matches inputs.EvidenceTree launch.InputTreeHash)

                let baselineEquivalent =
                    nothingOwed inputs.Debt && hasCachedResults && not treeUnverified

                let nothingToVerify = UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered

                let nothingForced =
                    totalClasses = 0 && Set.isEmpty forceRunProjects && not hasQuarantinedFailures

                if nothingForced && not alwaysOnly && (baselineEquivalent || nothingToVerify) then
                    if nothingToVerify then
                        Logging.info
                            "test-prune"
                            "Every changed symbol has no covering test — nothing to verify, skipping tests (green, 0 ran)"
                    else
                        Logging.info
                            "test-prune"
                            "No affected classes, no dependency fanout, empty pending queue, baseline exists — skipping tests"

                    // Build a degenerate lifecycle (Started → Completed with empty
                    // Results). Start fires immediately; the synchronous Custom handler
                    // emits completion inside the cache-write capture window.
                    let runId = Guid.NewGuid()

                    let started: TestRunStarted =
                        { RunId = runId
                          StartedAt = DateTime.UtcNow }

                    emitStarted started

                    let completed: TestRunCompleted =
                        { RunId = runId
                          TotalElapsed = TimeSpan.Zero
                          Outcome = Normal
                          Results = Map.empty
                          // Impact analysis selected no project, so none was invoked.
                          // Stated rather than inferred, and the
                          // outcome is `Normal`, so it reaches consumers that filter
                          // on `Outcome`.
                          Verification = NoProjectsSelected }

                    // The skip EXECUTES NOTHING, so it covers nothing and may clear
                    // nothing. Empty results already yield an empty
                    // `RunCoverage`; the empty selection says so at the source too, so
                    // the "0 affected, green, 0 ran" path can never be mistaken for
                    // evidence about a project.
                    return
                        TestsFinished(
                            started,
                            completed,
                            { launch with
                                Selection = Map.empty
                                SelectedBy = []
                                SeedFanout = SeedFanoutReading.NoSelection
                                // Executed nothing, so it neither covers nor projects
                                // anything.
                                WouldHaveRun = None
                                Reused = Map.empty
                                ZeroSelection =
                                    (match inputs.ChangedSymbolsAllUncovered with
                                     | UncoveredChanges.AllUncovered(symbols, unrunnable) ->
                                         ZeroSelection.ChangesUncovered(symbols, unrunnable)
                                     | UncoveredChanges.No -> ZeroSelection.AlreadyVerified) }
                        )
                else
                    logSelectedBy launch

                    if totalClasses = 0 && narrowedSeeds.IsNone && not alwaysOnly then
                        // NAME the debt that refused the zero-affected
                        // skip, and say what will actually run. Both halves were wrong
                        // before: the cause was hard-coded to two of five possibilities,
                        // and "running all tests" was printed over runs that were a
                        // couple of force-run projects.
                        let widenings =
                            zeroAffectedWidening
                                hasCachedResults
                                inputs.Debt.UnknownDebt
                                (Set.count inputs.Debt.PendingQueue)
                                inputs.Debt.RuntimeObligations
                                (inputs.Debt.CoverageNotIngested |> Map.keys |> Set.ofSeq)
                                (List.length inputs.OutstandingFailures)
                                baselineInvalid
                                evidenceGap
                                treeUnverified

                        // An EMPTY selection is not "a few projects": `selectionOf` reads
                        // it as no selection at all and every configured project runs in
                        // full. That is the expensive outcome, so it is the one named.
                        let willRun =
                            if Map.isEmpty affectedByProject then
                                "EVERY configured project, in full"
                            else
                                $"%d{affectedByProject.Count} force-run project(s)"

                        match widenings with
                        | [] ->
                            // Nothing is owed and a baseline exists, so the zero-affected
                            // skip should have discharged this cycle for free — and did
                            // not. That is a defect in whichever arm consumed the signal,
                            // and it costs `willRun`. The phantom
                            // runtime-coverage obligation was exactly this shape and left
                            // no attributable line at all; this one is the alarm that a
                            // DIFFERENT arm has re-opened the same hole.
                            Logging.warn
                                "test-prune"
                                $"No affected classes, and NOTHING is owed — the zero-affected skip should have discharged \
                                  this cycle without running anything, yet it is running %s{willRun}. Some verification \
                                  debt is being reported as outstanding while naming nothing that can select or discharge \
                                  it."
                        | causes ->
                            let notNarrowed =
                                match symbolFreeEdit with
                                | Some(SymbolFreeEdit.Refused reason) ->
                                    $" (not narrowed to the edited files' covering classes: %s{reason})"
                                | _ -> ""

                            Logging.info
                                "test-prune"
                                $"No affected classes, but the zero-affected skip is refused — \
                                  %s{ZeroAffectedWidening.describeMany causes}; running %s{willRun}%s{notNarrowed}"
                    else
                        for (proj, classes) in affectedByProject |> Map.toList do
                            // Never `%A` here: it caps the list at 100, so a
                            // 1,500-class blowout reads like a 100-class one, and it
                            // renders an EMPTY list as `[]` — which here means the
                            // project runs UNFILTERED, the exact opposite of "nothing
                            // selected". `describeMany` leads with the exact count.
                            let rendered =
                                match classes, Map.tryFind proj excludedByProject with
                                | [], Some skipped ->
                                    $"ALL but %d{List.length skipped} checkSkipClasses (whole-project, exclude filter)"
                                | [], None -> "ALL (unfiltered — force-run)"
                                | classes, _ -> describeMany classes

                            Logging.info "test-prune" $"Affected classes for %s{proj}: %s{rendered}"

                    // Run-level selectivity in ONE line, since the per-project lines
                    // above can be interleaved or truncated. Separates the two ways a
                    // run can be wide: many classes named, versus projects unfiltered.
                    let unfilteredProjects =
                        affectedByProject |> Map.filter (fun _ cs -> List.isEmpty cs) |> Map.count

                    Logging.info
                        "test-prune"
                        $"Selectivity: %d{affectedByProject.Count} project(s) selected, \
                          %d{unfilteredProjects} of them UNFILTERED (whole-project), \
                          %d{affectedByProject |> Map.values |> Seq.sumBy List.length} class(es) named in total"

                    let! results, started, completed, boundTree, testHostStartedAt, evidenceHeld =
                        executeTests
                            db
                            (Some ctx)
                            emitStarted
                            repoRoot
                            launchDeadline
                            maxParallelProjects
                            ctx.Processes
                            (tracesFor ctx inputs.Mode)
                            launch.InputTree
                            (compileInputsOf ctx)
                            beforeRun
                            (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                            coveragePaths
                            (coverageIngestFailed ctx)
                            afterRun
                            configuredTestProjects
                            configs
                            affectedByProject
                            excludedByProject
                            None
                            evidencePlan

                    // `executeTests` emits Started before launching the host and still
                    // emits per-group TestProgress live; the synchronous handler captures
                    // Completed for cache replay.
                    ignore results

                    return
                        TestsFinished(
                            started,
                            completed,
                            { launch with
                                InputTree = boundTree
                                TestHostStartedAt = Some testHostStartedAt
                                Reused = evidenceHeld }
                        )
            with ex ->
                Logging.error "test-prune" $"runTests failed: %s{ex.Message}"

                // Build an Aborted lifecycle so subscribers see a coherent end
                // to this run rather than hanging at TestRunStarted.
                let started, completed = abortedRunLifecycle emittedStart ex.Message

                if emittedStart.IsNone then
                    emitStarted started

                // launch carries the queue snapshot this aborted run was
                // launched against; the TestsFinished handler commits NOTHING
                // for an Aborted outcome, so those symbols stay queued. Rebuilt
                // from `launchedSymbols` here (rather than the in-try `launch`,
                // which may not exist if the per-symbol coverage query itself
                // threw) with an empty covering map — an Aborted run commits
                // nothing, so the covering map is never consulted on this path.
                // The empty SELECTION says the same thing about the ledger: an
                // aborted run executed nothing, so it clears nothing.
                let launch =
                    { InputTree = None
                      ModelGeneration = observeModelGeneration ctx
                      ProjectInputs = observeTestInputs ctx
                      Symbols = launchedSymbols
                      SymbolRevisions = launchedRevisions
                      ChangedFiles = inputs.ChangedFiles
                      TestHostStartedAt = None
                      CoveringProjectsBySymbol = Map.empty
                      RuntimeProjectsByFile = launchedRuntimeObligations
                      Selection = Map.empty
                      WouldHaveRun = None
                      Reused = Map.empty
                      Seeds = inputs.Seeds
                      ZeroSelection = ZeroSelection.NotAZero
                      FullSuiteCause = fullSuiteCause
                      SelectedBy = []
                      SeedFanout = SeedFanoutReading.NoSelection
                      TraceShadow = [] }

                return TestsFinished(started, completed, launch)
        }

    /// The `run-tests` force-run work async, launched under the "tests" key by the
    /// `RunTestsRequested` fold. Every exit that returns yields a `CommandTestsFinished`,
    /// whose fold delivers the earned terminal status and resolves `reply` once that is
    /// published; cancellation resolves `reply` itself (the IPC command is awaiting it,
    /// bounded).
    ///
    /// Empty launch set: `run-tests` is a manual FORCE run (optionally
    /// filtered to a subset / only-failed). It is NOT the impact-analysis
    /// queue-draining path, and a filtered force-run may not cover every
    /// queued symbol — so it commits NOTHING from the pending-verification
    /// queue (over-testing is the safe direction). The queue drains through
    /// the normal BuildCompleted impact flow.
    let commandForceRun
        (ctx: PluginCtx<TestPruneMsg>)
        // The mode the force run launches under: it decides whether it records traces.
        (mode: TestMode)
        (configs: TestConfig list)
        (filter: string option)
        (reply: Tasks.TaskCompletionSource<string>)
        : Async<TestPruneMsg> =
        // A force-run launches exactly `configs`, each with NO class selection (it
        // passes `Map.empty` to `executeTests`), so each runs IN FULL — a plain
        // `dotnet fshw test-rerun` is therefore the unfiltered run that can clear ANY
        // outstanding red (the escape hatch that keeps the clear-only-what-it-covered rule
        // from wedging into a permanent stuck-red).
        //
        // A `--filter` passthrough is a different matter: `RunCoverage.ofRun` sees
        // `wasFiltered = true` on the results and declines to claim coverage for a
        // filter string whose reach it cannot compute. Projects NOT in `configs`
        // (`--only-failed`, `--projects`) are absent from the selection and so are
        // covered by nothing — exactly right, they did not run.
        let commandLaunch: TestRunLaunch =
            { InputTree = None
              ModelGeneration = observeModelGeneration ctx
              ProjectInputs = observeTestInputs ctx
              Symbols = Set.empty
              SymbolRevisions = Map.empty
              // A force-run is not launched from the changed files, so it consumes none.
              ChangedFiles = []
              TestHostStartedAt = None
              CoveringProjectsBySymbol = Map.empty
              RuntimeProjectsByFile = Map.empty
              Selection = configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
              // A FORCE run has no impact selection behind it — nothing was widened past,
              // so there is no `check` reading to project. This is the
              // ESCALATING `confirm`'s path, and it already carries an EXECUTED
              // impact-scoped reading taken before the escalation.
              WouldHaveRun = None
              Reused = Map.empty
              Seeds = []
              ZeroSelection = ZeroSelection.NotAZero
              FullSuiteCause = FullSuiteCause.ForceRun
              SeedFanout = SeedFanoutReading.NoSelection
              TraceShadow = []
              SelectedBy =
                configs
                |> List.map (fun c ->
                    { Project = c.Project
                      Causes = [ SelectionCause.ForceRun ]
                      SkippedClasses = [] }) }

        async {
            do! awaitDiscoveryBeforeLaunch ctx

            // The model is read after the wait, not when the run was requested.
            let commandLaunch =
                { commandLaunch with
                    InputTree = ReceiptInputTree.readTree repoRoot
                    ModelGeneration = observeModelGeneration ctx
                    ProjectInputs = observeTestInputs ctx }

            logLaunch "forced" mode commandLaunch
            logSelectedBy commandLaunch

            let mutable emittedStart: TestRunStarted option = None
            let mutable returned = false

            let emitStarted started =
                emittedStart <- Some started
                ctx.EmitTestRunStarted started

            try
                try
                    let! results, started, completed, boundTree, testHostStartedAt, _ =
                        executeTests
                            db
                            None
                            emitStarted
                            repoRoot
                            launchDeadline
                            maxParallelProjects
                            ctx.Processes
                            (tracesFor ctx mode)
                            commandLaunch.InputTree
                            (compileInputsOf ctx)
                            beforeRun
                            (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                            coveragePaths
                            (coverageIngestFailed ctx)
                            afterRun
                            configuredTestProjects
                            configs
                            Map.empty
                            Map.empty
                            filter
                            // A `run-tests` force run is an explicit request to run: it
                            // neither reuses nor publishes evidence.
                            None

                    // The counts come from the CTRF reports THIS RUN wrote, located by
                    // the run id the daemon just handed back — declared membership, never
                    // an mtime scan of a shared pile. An empty list from an existing
                    // run-dir means the run executed no tests, and that is exactly the
                    // fact the CLI has to be able to state. Only verdict evidence is
                    // read, so the counts printed beside a status are the ones that
                    // decided it.
                    let runReports =
                        FsHotWatch.Ctrf.verdictReportsForRun repoRoot started.RunId
                        |> List.map (fun r -> r.Project, r.Summary)
                        |> Map.ofList

                    // Returned (not Posted) so the framework's completion path
                    // delivers it: the synchronous handler does the error reporting and
                    // status updates a bare emit would skip, and the reply is resolved
                    // only once that completion is published.
                    returned <- true

                    return
                        CommandTestsFinished(
                            started,
                            completed,
                            { commandLaunch with
                                InputTree = boundTree
                                TestHostStartedAt = Some testHostStartedAt },
                            reply,
                            formatTestResultsJson filter runReports results
                        )
                with ex ->
                    // A `beforeRun` throw / `executeTests` fault
                    // means the suite it guards NEVER RAN — that must surface
                    // as a failure, never a stale prior green. The Aborted
                    // lifecycle drives the TestsFinished handler to a Failed
                    // status.
                    Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                    let started, completed = abortedRunLifecycle emittedStart ex.Message

                    if emittedStart.IsNone then
                        emitStarted started

                    returned <- true

                    return
                        CommandTestsFinished(
                            started,
                            completed,
                            commandLaunch,
                            reply,
                            JsonSerializer.Serialize({| error = ex.Message |})
                        )
            finally
                // Cancellation — daemon teardown, or every client that asked for this run
                // has gone — skips `with` but runs `finally`, and no completion will fold.
                if not returned then
                    let reason = "the run was cancelled before it completed"

                    // Close the run this work opened. Subscribers track every started run
                    // until its completion (Build defers every build while one is live),
                    // so a started run must end. `Aborted` with no results is evidence of
                    // nothing: it is never a pass.
                    match emittedStart with
                    | Some started ->
                        let _, completed = abortedRunLifecycle (Some started) reason
                        ctx.EmitTestRunCompleted completed
                    | None -> ()

                    // Never leave a client that is still waiting on a reply that cannot come.
                    reply.TrySetResult(JsonSerializer.Serialize({| error = reason |})) |> ignore
        }

    let commands =
        [ "affected-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  // Compute on demand from state.ChangedSymbols against current DB
                  // state. ChangedSymbols accumulates across FileChecked events and
                  // is reset by flushAndQueryAffected on BuildCompleted.
                  let symbols = state.ChangedSymbols |> List.distinct

                  let tests =
                      if symbols.IsEmpty then
                          []
                      else
                          queries.AffectedTests symbols

                  let testsData =
                      tests
                      |> List.map (fun t ->
                          {| project = t.TestProject
                             ``class`` = t.TestClass
                             ``method`` = t.TestMethod |})

                  return JsonSerializer.Serialize(testsData)
              })

          "changed-files",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async { return JsonSerializer.Serialize(state.ChangedFiles) })

          "test-results",
          // The last COMMITTED result set, and only once it is the current one. While a
          // run's worker is live this answers `running`. Between the worker finishing and
          // its `TestsFinished`/`CommandTestsFinished` fold committing, `state` is still
          // the pre-fold state — the PREVIOUS run's results — so this answers `settling`
          // rather than hand those back as though they were the run that just finished.
          PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
              async {
                  if ctx.IsRunning "tests" then
                      return JsonSerializer.Serialize({| status = "running" |})
                  elif ctx.IsSettling "tests" then
                      return JsonSerializer.Serialize({| status = "settling" |})
                  else
                      match state.LastResults with
                      // No filter: `test-results` reports the LAST stored result set,
                      // which does not remember what launched it. Saying nothing is
                      // correct here; inventing "(none)" would claim it was unfiltered.
                      | Some results -> return formatTestResultsJson None Map.empty results
                      | None -> return JsonSerializer.Serialize({| status = "not run" |})
              })

          "flaky-tests",
          PluginCommand.Observe(fun (_ctx: CommandReadCtx) (_state: TestPruneState) (_args: string array) ->
              async {
                  let history = Flakiness.loadHistory (flakinessHistoryPath repoRoot)
                  let top = Flakiness.topFlaky 10 history

                  let payload =
                      top
                      |> List.map (fun (name, score) ->
                          let runs =
                              history |> Map.tryFind name |> Option.map List.length |> Option.defaultValue 0

                          {| name = name
                             flakiness = score
                             runs = runs |})

                  return JsonSerializer.Serialize({| tests = payload |})
              }) ]

    // run-tests / scope commands (only if testConfigs are provided). The scope verbs
    // live behind the same condition on purpose: a repo with no test projects has no suite
    // to run in full, so `fshw confirm` finds no `set-scope` command, cannot establish
    // the full-suite scope, and refuses to produce a merge verdict — rather than
    // silently issuing a green one it never earned.
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "set-scope",
                PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
                    async {
                        // `fshw confirm` calls this BEFORE triggering its
                        // scan, so the test run the scan provokes is already unfiltered.
                        // It replies once the owner has committed the scope, so the scan
                        // it triggers next is folded against it.
                        let requested =
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                            try
                                use doc = JsonDocument.Parse(argStr)

                                match doc.RootElement.TryGetProperty("scope") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> "impact"
                            with _ ->
                                "impact"

                        match requested with
                        | "owed" ->
                            do! ctx.EnqueueExclusiveIntent "scope" None OwedRunRequested |> Async.AwaitTask
                            return JsonSerializer.Serialize({| scope = requested |})
                        | "full"
                        | "impact" ->
                            // Its own intent key: a scope change queued behind a running
                            // suite would hold `confirm` for that suite's whole length.
                            do!
                                ctx.EnqueueExclusiveIntent "scope" None (ScopeRequested(TestMode.ofScope requested))
                                |> Async.AwaitTask

                            return JsonSerializer.Serialize({| scope = requested |})
                        | other ->
                            return
                                JsonSerializer.Serialize(
                                    {| error = $"unknown scope '%s{other}' (expected 'full', 'impact' or 'owed')" |}
                                )
                    })

                "test-scope",
                PluginCommand.Observe(fun (ctx: CommandReadCtx) (state: TestPruneState) (_args: string array) ->
                    async {
                        // What the last completed run ACTUALLY covered — the evidence a
                        // merge verdict is computed from. Never a restatement of what was
                        // requested: `set-scope full` is a request, this is the receipt.
                        // A run still in flight reports `running` while its worker is live
                        // and `settling` until its result fold commits. `confirm` treats
                        // both as "no verdict yet" rather than as a scope.
                        //
                        // The reply carries the RUN ID as well, so the CLI can DECLARE
                        // which CTRF reports belong to this run
                        // (`.fshw/test-runs/<runId>/`) instead of inferring membership
                        // from mtimes.
                        // Check again at the read boundary: inputs may have changed
                        // after completion, even before the next watcher event arrives.
                        //
                        // And against the model: a receipt earned under a replaced model
                        // grades nothing, so a `confirm` reading it escalates to a run under
                        // the model it is graded against instead of grading a run that no
                        // evidence for that model names.
                        //
                        // In flight covers the fold window too: once the worker is gone,
                        // `IsRunning "tests"` is false, but until the fold holding the key
                        // commits, the state's receipt, scope and completed runs are the
                        // PREVIOUS run's. Graded as settled, they would hand `confirm` that
                        // run's scope as the current one; `settling` names no run instead.
                        let currentTree = lazy (ReceiptInputTree.read repoRoot)

                        let inFlight =
                            if ctx.IsRunning "tests" then Some "running"
                            elif ctx.IsSettling "tests" then Some "settling"
                            else None

                        let receipt =
                            match inFlight, state.EvidenceReceipt with
                            | None, Some evidence ->
                                let currentModel = ObservedModel.generation ctx.ProjectGraph

                                if not (ReceiptInputTree.matches evidence.InputTreeHash currentTree.Value) then
                                    None
                                elif
                                    not (
                                        receiptSpeaksFor currentModel (ObservedModel.inputs ctx.ProjectGraph) evidence
                                    )
                                then
                                    let withheld = evidence.RunId.ToString("N")

                                    ctx.Log
                                        $"test-scope: run %s{withheld} earned its receipt under project model generation %s{ObservedModel.describe evidence.ModelGeneration}; the model is now %s{ObservedModel.describe currentModel}, so it grades nothing until a run under this model earns one"

                                    None
                                else
                                    Some evidence
                            | _ -> None

                        let runId =
                            match receipt with
                            | Some receipt -> box (receipt.RunId.ToString("N"))
                            | None -> null

                        // The evidence this state holds for the run it grades, read from the
                        // SAME state as `runId`. The verdict grades that run by its evidence;
                        // read here, the two come from one snapshot instead of from two
                        // replies (this one and the diagnostics ledger) that a completion can
                        // land between. `null` when no evidence for that run is held under
                        // the current model, which refuses the green as surely as an absent
                        // ledger entry does.
                        let evidence =
                            receipt
                            |> Option.bind (fun receipt ->
                                EarnedEvidence.retainedForRun
                                    (ObservedModel.generation ctx.ProjectGraph)
                                    (ObservedModel.inputs ctx.ProjectGraph)
                                    receipt.RunId
                                    state.Earned)
                            |> Option.map (AnalysisEvidence.refuseThrough state.CohortSeal)
                            |> Option.map (fun held ->
                                box
                                    {| runId = held.RunId.ToString("N")
                                       modelGeneration = held.Generation
                                       refusals = List.toArray held.FailureReasons |})
                            |> Option.toObj

                        // The scope is a PROJECTION of `LastCoverage` — the very value the
                        // ledger uses to decide what a run is entitled to CLEAR.
                        // See `scopeOf`.
                        let projects = allConfigs |> List.map (fun c -> c.Project)

                        // The change that selected the last run's tests. Sent so a
                        // check that selects NOTHING can still say what the last
                        // change that DID trigger tests was — otherwise that fact
                        // exists only in a daemon log, which is exactly where a
                        // reader will not look before concluding the selector is
                        // broken.
                        //
                        // Truncated on the WIRE, with the full count beside it: a
                        // pathological flush can carry thousands of seeds, and a
                        // reply that grows without bound to serve a diagnostic line
                        // is a new failure mode in the path that earns verdicts.
                        //
                        // A zero-test completion no longer writes a
                        // receipt (`ReceiptTransition`), so the seeds and the zero
                        // reason it used to carry are read from the state the same
                        // completion wrote: `LastSeeds` is the change that last
                        // selected tests, `LastZeroSelection` why this one selected
                        // none. A retained receipt still speaks for itself.
                        let evidenceSeeds =
                            receipt
                            |> Option.map (fun receipt -> receipt.Seeds)
                            |> Option.defaultValue state.LastSeeds

                        let seeds = evidenceSeeds |> List.truncate 8 |> List.toArray
                        let seedCount = List.length evidenceSeeds

                        // Every run this session has completed, newest
                        // first. Sent on EVERY branch — including `running`, which is
                        // what a check reads when it takes its baseline against a busy
                        // daemon, and a baseline that came back silent would hand the
                        // check its predecessor's runs.
                        let runIds =
                            state.CompletedRuns |> List.map (fun id -> id.ToString("N")) |> List.toArray

                        // The full-suite baseline this ledger's greens are
                        // relative to, on EVERY branch — a `running` reply still names the
                        // baseline a later reading will be judged against. `baseline` is
                        // the reference; `baselineAbsent` says why there is none. Both
                        // null only for an analysis-only daemon, which makes no test claim.
                        let baseline, baselineAbsent =
                            match baselineInvalidReason state.Debt, state.Debt.Baseline with
                            | None, Some b when not (Set.isEmpty runnableProjects) ->
                                box
                                    {| runId = b.RunId.ToString("N")
                                       earnedAt = b.EarnedAt.ToString("o")
                                       projects = b.Projects |> Set.toArray |},
                                null
                            | None, _ -> null, null
                            | Some reason, _ -> null, box reason

                        // The changed files whose changes selected no tests, on EVERY branch:
                        // they are a fact about the current selection, whichever run the
                        // scope is read from. Always an array, so an empty one says "none".
                        let notSelected =
                            state.NotSelected
                            |> Map.toArray
                            |> Array.map (fun (file, reason) -> NotSelectedFile.wire { File = file; Reason = reason })

                        match inFlight with
                        | Some kind ->
                            return
                                JsonSerializer.Serialize(
                                    {| kind = kind
                                       notSelected = notSelected
                                       runId = runId
                                       runIds = runIds
                                       baseline = baseline
                                       baselineAbsent = baselineAbsent |}
                                )
                        | None ->
                            // ADR-059 step 7b: each project's full-run outcome key over this tree,
                            // for the shadow lookup verdict. Settled replies only: a run in flight
                            // has no answer yet, and keying the tree is a walk.
                            let outcomeKeys =
                                System.Collections.Generic.Dictionary<string, string>(
                                    currentOutcomeKeys ctx.ProjectGraph
                                )

                            let evidenceCoverage =
                                receipt
                                |> Option.map (fun receipt -> receipt.Coverage)
                                |> Option.defaultValue RunCoverage.none

                            // Which cause selected each project of the graded run. Null only
                            // where no receipt says, which a `full` or `filtered` scope never is.
                            let selectedBy =
                                receipt
                                |> Option.map (fun receipt ->
                                    receipt.SelectedBy |> List.map ProjectAttribution.wire |> Array.ofList)
                                |> Option.toObj

                            // The seeds that alone selected the most tests, beside `selectedBy`.
                            let seedFanout =
                                receipt
                                |> Option.map (fun receipt -> SeedFanoutReading.wire receipt.SeedFanout)
                                |> Option.toObj

                            match scopeOf projects evidenceCoverage with
                            | ScopeFull n ->
                                // Why this run took every project. Always present here: a
                                // `full` scope is read from a receipt, and the receipt
                                // carries its launch's cause.
                                let cause =
                                    receipt
                                    |> Option.map (fun receipt -> FullSuiteCause.token receipt.FullSuiteCause)
                                    |> Option.toObj

                                return
                                    JsonSerializer.Serialize(
                                        {| kind = "full"
                                           cause = cause
                                           selectedBy = selectedBy
                                           seedFanout = seedFanout
                                           notSelected = notSelected
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = n
                                           totalProjects = n
                                           runId = runId
                                           evidence = evidence
                                           seeds = seeds
                                           seedCount = seedCount
                                           outcomeKeys = outcomeKeys |}
                                    )
                            | ScopeFiltered(ran, total) ->
                                return
                                    JsonSerializer.Serialize(
                                        {| kind = "filtered"
                                           selectedBy = selectedBy
                                           seedFanout = seedFanout
                                           notSelected = notSelected
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = ran
                                           totalProjects = total
                                           runId = runId
                                           evidence = evidence
                                           seeds = seeds
                                           seedCount = seedCount
                                           outcomeKeys = outcomeKeys |}
                                    )
                            | ScopeNone total ->
                                let zero =
                                    receipt
                                    |> Option.map (fun receipt -> receipt.ZeroSelection)
                                    |> Option.defaultValue state.LastZeroSelection

                                // A completion that selected tests names no zero reason, so
                                // the reason is why no evidence speaks for this tree.
                                let reason, detail =
                                    match ZeroSelection.token zero with
                                    | Some token -> token, None
                                    | None ->
                                        UnverifiedTree.classify
                                            state.EvidenceReceipt
                                            currentTree.Value
                                            state.ReceiptRevoked
                                            state.CompletedRuns
                                        |> UnverifiedTree.wire

                                return
                                    JsonSerializer.Serialize(
                                        {| kind = "none"
                                           notSelected = notSelected
                                           runIds = runIds
                                           baseline = baseline
                                           baselineAbsent = baselineAbsent
                                           ranProjects = 0
                                           totalProjects = total
                                           runId = runId
                                           evidence = evidence
                                           seeds = seeds
                                           seedCount = seedCount
                                           noTestsReason = reason
                                           noTestsDetail = Option.toObj detail
                                           uncoveredSymbols =
                                            (ZeroSelection.symbols zero |> List.truncate 8 |> List.toArray)
                                           uncoveredSymbolCount = List.length (ZeroSelection.symbols zero)
                                           // Of the uncovered symbols, how many
                                           // HAVE covering tests this daemon cannot run, and where.
                                           unrunnableSymbolCount = Map.count (ZeroSelection.unrunnable zero)
                                           unrunnableProjects =
                                            (ZeroSelection.unrunnable zero
                                             |> UnrunnableCoverage.projects
                                             |> Set.toArray)
                                           outcomeKeys = outcomeKeys |}
                                    )
                    })

                // What `check` WOULD have concluded about the run
                // `confirm` just widened to full — the sample `confirm` used to destroy.
                //
                // A SEPARATE command from `test-scope`, not another field on it. The two
                // answer opposite questions (`test-scope`: what DID run; this: what would
                // NOT have) and `test-scope` is on the path that earns every verdict,
                // where a new field is a new way for the reply to fail to parse. A daemon
                // that has never heard of this command returns the unknown-command
                // sentinel, which the CLI reads as "no sample" — never as agreement.
                "check-reach",
                //
                // The optional argument is the run to answer for — the run the verdict
                // GRADES, which need not be the newest completion. Without one, the newest.
                PluginCommand.Observe(fun (_ctx: CommandReadCtx) (state: TestPruneState) (args: string array) ->
                    async {
                        let notRecorded (reason: string) =
                            JsonSerializer.Serialize({| recorded = false; reason = reason |})

                        let requested =
                            match args |> Array.tryHead |> Option.map _.Trim() with
                            | None
                            | Some "" -> Ok None
                            | Some text ->
                                match Guid.TryParse text with
                                | true, runId -> Ok(Some runId)
                                | _ -> Error $"'%s{text}' is not a run id"

                        let entry =
                            match requested with
                            | Error why -> Error why
                            | Ok None ->
                                match List.tryHead state.CheckReaches with
                                | Some found -> Ok found
                                | None ->
                                    Error "no test run has completed in this session, so there is no result to project"
                            | Ok(Some wanted) ->
                                match state.CheckReaches |> List.tryFind (fun (id, _, _, _) -> id = wanted) with
                                | Some found -> Ok found
                                | None ->
                                    let wantedId = wanted.ToString("N")

                                    Error
                                        $"run %s{wantedId} has no retained projection in this session — it never \
                                           completed here, or it is older than the last %d{SessionRunLedger} completions"

                        match entry with
                        | Error why -> return notRecorded why
                        | Ok(runId, wouldHaveRun, reach, recall) ->
                            let projects = allConfigs |> List.map (fun c -> c.Project)

                            let kind, ranProjects, totalProjects =
                                match wouldHaveRun |> Option.map (scopeOfSelection projects) with
                                | Some(ScopeFull n) -> box "full", n, n
                                | Some(ScopeFiltered(ran, total)) -> box "filtered", ran, total
                                | Some(ScopeNone total) -> box "none", 0, total
                                // No retained selection ⇒ no scope to describe. `null`,
                                // never a count: a zero here would read as "check would
                                // have run nothing", which is a claim, and this is an
                                // absence.
                                | None -> null, 0, List.length projects

                            let failingSuites =
                                match reach with
                                | ReachedAFailure ps -> List.toArray ps
                                | ReachedNoFailure _
                                | NoFailuresToReach
                                | ReachUnknown _ -> [||]

                            let missed =
                                match reach with
                                | ReachedNoFailure failures ->
                                    failures
                                    |> List.map (fun failure ->
                                        {| project = failure.Project
                                           ``class`` = failure.Class
                                           cause =
                                            (match failure.Cause with
                                             | MissCause.ProjectNotSelected -> "project-not-selected"
                                             | MissCause.ClassNotInFilter -> "class-not-in-filter") |})
                                    |> List.toArray
                                | _ -> [||]

                            let reason =
                                match reach with
                                | ReachUnknown r -> box r
                                | ReachedAFailure _
                                | ReachedNoFailure _
                                | NoFailuresToReach -> null

                            let (recallMeasured,
                                 recallReached,
                                 recallTotal,
                                 recallThreshold,
                                 recallAcceptable,
                                 recallReason) =
                                match recall with
                                | RecallMeasured(reached, total, threshold, acceptable) ->
                                    true, reached, total, threshold, box acceptable, null
                                | RecallNotMeasurable why -> false, 0, 0, 1.0, null, box why

                            return
                                JsonSerializer.Serialize(
                                    {| recorded = true
                                       runId = runId.ToString("N")
                                       kind = kind
                                       ranProjects = ranProjects
                                       totalProjects = totalProjects
                                       reach = CheckReach.token reach
                                       failingSuites = failingSuites
                                       missed = missed
                                       reason = reason
                                       conditionalFailureRecall =
                                        {| measured = recallMeasured
                                           reached = recallReached
                                           total = recallTotal
                                           threshold = recallThreshold
                                           acceptable = recallAcceptable
                                           reason = recallReason |} |}
                                )
                    })

                "run-tests",
                PluginCommand.Request(fun (ctx: CommandCtx<TestPruneMsg>) (args: string array) ->
                    async {
                        // FORCE semantics: `test-rerun` is the explicit "prove it
                        // ran" verb. The run NEVER executes here — it is an intent on
                        // the "tests" key, queued behind the run in flight (see
                        // `RunTestsRequested`). A force-run is owed work, never
                        // refused. The only thing bounded here is the WAIT:
                        // `waitSec` caps queue time plus run time, and on expiry
                        // this reports a DISTINCT `busy` status so the CLI exits
                        // non-zero rather than reporting a verdict no run produced.
                        try
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"
                            let waitForResultMs = parseRunTestsWaitMs argStr DefaultRunTestsWaitMs

                            let parseResult =
                                try
                                    Ok(JsonDocument.Parse(argStr))
                                with ex ->
                                    Error ex.Message

                            match parseResult with
                            | Error msg -> return JsonSerializer.Serialize({| error = $"invalid JSON: %s{msg}" |})
                            | Ok doc ->

                                use doc = doc
                                let root = doc.RootElement

                                let filter =
                                    match root.TryGetProperty("filter") with
                                    | true, v -> Some(v.GetString())
                                    | false, _ -> None

                                let onlyFailed =
                                    match root.TryGetProperty("only-failed") with
                                    | true, v -> v.GetBoolean()
                                    | false, _ -> false

                                let projectFilter =
                                    match root.TryGetProperty("projects") with
                                    | true, v ->
                                        v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                                    | false, _ -> None

                                // `--only-failed` depends on what failed, which is plugin state. A
                                // request never reads state, so the choice is made by `Update`
                                // against the state it folds the request into.
                                let selection =
                                    if onlyFailed then
                                        None
                                    else
                                        match projectFilter with
                                        | Some names ->
                                            Some(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                                        | None -> Some allConfigs

                                match selection with
                                | Some configs when configs.IsEmpty ->
                                    // Name what was asked for and what exists. A bare
                                    // "no matching test projects" is unactionable for the
                                    // one case that actually produces it — a mistyped or
                                    // renamed `--project` — and the configured names are
                                    // right here.
                                    let msg =
                                        match projectFilter with
                                        | Some names ->
                                            let asked = names |> Set.toList |> List.sort |> String.concat ", "

                                            let known =
                                                allConfigs
                                                |> List.map (fun c -> c.Project)
                                                |> List.sort
                                                |> String.concat ", "

                                            $"no test project matches --project %s{asked}. Configured test projects: %s{known}"
                                        | None -> "no matching test projects"

                                    return JsonSerializer.Serialize({| error = msg |})
                                | _ ->
                                    let reply =
                                        Tasks.TaskCompletionSource<string>(
                                            Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                                        )

                                    // An intent on the "tests" key: it waits behind the run
                                    // in flight, owned, and launches from the fold it
                                    // becomes. A receipt that fails means no fold will
                                    // launch it, so the wait below ends with that failure.
                                    let request =
                                        match selection with
                                        | Some configs -> RunTestsRequested(configs, filter, reply)
                                        | None -> RunFailedTestsRequested(filter, reply)

                                    let receipt = ctx.EnqueueExclusiveIntent "tests" None request

                                    receipt.ContinueWith(
                                        (fun (admitted: Tasks.Task) ->
                                            if admitted.IsFaulted then
                                                reply.TrySetResult(
                                                    JsonSerializer.Serialize(
                                                        {| error = admitted.Exception.GetBaseException().Message |}
                                                    )
                                                )
                                                |> ignore),
                                        Tasks.TaskContinuationOptions.ExecuteSynchronously
                                    )
                                    |> ignore

                                    // Bounded await: the reply resolves
                                    // when the run finishes — behind the test-prune
                                    // mailbox and possibly behind a run already in
                                    // flight — so an unbounded wait here could pin the
                                    // IPC caller for as long as the daemon is wedged.
                                    let! winner =
                                        Tasks.Task.WhenAny(reply.Task, Tasks.Task.Delay(waitForResultMs))
                                        |> Async.AwaitTask

                                    if winner = (reply.Task :> Tasks.Task) then
                                        return reply.Task.Result
                                    else
                                        return
                                            JsonSerializer.Serialize(
                                                {| status = "busy"
                                                   message =
                                                    $"the test run did not produce a result within %d{waitForResultMs / 1000}s (still queued or running); retry, or raise --wait-sec" |}
                                            )
                        with ex ->
                            // Command-local faults only (the run itself executes in
                            // the mailbox-launched work, which owns run faults per
                            // and always resolves the reply + posts
                            // the Aborted lifecycle). Nothing to post here: no run
                            // was launched.
                            Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                            return JsonSerializer.Serialize({| error = ex.Message |})
                    }) ]
        | _ -> commands

    /// Owe one impact run after whatever holds the "tests" key. Coalesced: however many
    /// triggers arrive while the key is held, one run is queued, and it decides against
    /// the state it is delivered into (`impactRun`).
    let enqueueImpactRun (ctx: PluginCtx<TestPruneMsg>) =
        ctx.EnqueueExclusiveIntent "tests" (Some "impact") ImpactRunRequested |> ignore

    /// What a launch from `inputs` will execute: the widenings `runTestsWithImpact`
    /// applies to every project.
    let launchScopeOf (inputs: TestRunInputs) =
        if
            TestMode.requestsFullSuite inputs.Mode
            || inputs.Debt.RecoveryOutstanding
            || Option.isSome (baselineInvalidReason inputs.Debt)
        then
            LaunchedFullSuite
        else
            LaunchedSelection

    /// Claim the "tests" key for an impact launch from `launchState`. `Some` carries the
    /// state recording what the claimed run will execute; `None` means the key is held.
    ///
    /// A session that launches only requested runs (`TestMode.launchesOwedRuns`) claims
    /// nothing: the debt stays owed, the fanout is kept, and the status says no run was
    /// launched, so a settle does not wait on a run that will never start. A requested run
    /// holding the key keeps its own status.
    let launchImpactRun
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (launchState: TestPruneState)
        (hasCachedResults: bool)
        (fanout: Set<string>)
        =
        let inputs = TestRunInputs.ofState launchState
        // A refresh still running beside the fold: the run takes its failures before it
        // reads anything, off the fold (AUTOMATION-1441).
        let refreshing = refreshInFlight.Value

        let withRefreshed (inputs: TestRunInputs) =
            match refreshing with
            | None -> async.Return inputs
            | Some refresh ->
                async {
                    let! outcomes, _ = Async.AwaitTask refresh

                    return
                        { inputs with
                            FailedExtensions = failuresAfter outcomes inputs.FailedExtensions }
                }

        if not (TestMode.launchesOwedRuns inputs.Mode) then
            let detail =
                "no owed test run launched: this session runs only the tests a command requests"

            Logging.info "test-prune" detail

            if ctx.SlotHolder "tests" = SlotHolder.Free then
                ctx.ReportStatus(PluginStatus.verifiedNothingNow detail TimeSpan.Zero)

            Some
                { launchState with
                    AwaitingDiff = None
                    OwedRunHeld = true
                    PendingForceRunProjects = Set.union launchState.PendingForceRunProjects fanout }
        else
            match
                // Cooperative-safe: a run held only by clients is cancelled once they have
                // all gone. Its test hosts run in its own process scope (reaped), its
                // result publishes only through the fold it returns, and the run it opened
                // is closed by its withdrawal fold. A run the daemon owes is never cancelled.
                runTestHostExclusive ctx fanout None (fun runCtx ->
                    PluginWork.cooperativeSafe (
                        async {
                            let! inputs = withRefreshed inputs
                            return! runTestsWithImpact runCtx configs inputs hasCachedResults fanout
                        }
                    ))
            with
            | Claimed ->
                Some
                    { launchState with
                        AwaitingDiff = None
                        InFlight =
                            Some
                                { Scope = launchScopeOf inputs
                                  Mode = inputs.Mode
                                  WatcherReported = Set.empty } }
            | SlotBusy -> None

    /// Whether debt found while the key is held joins the run holding it instead of
    /// queueing another. Only a full-suite run can take it. Under pass-through it takes
    /// every arrival, since no run may follow it. Under impact selection it takes a
    /// BootScan cohort, which scanned the tree that run is testing; an in-session cohort
    /// is an edit the run never built, and queues its own run.
    let joinsFullRun (state: TestPruneState) (bootScan: bool) =
        (state.InFlight |> Option.exists (fun run -> run.Scope = LaunchedFullSuite))
        && (TestMode.skips RerunIntents state.Mode || bootScan)

    /// Whether an owed launch now would only retry the red run that concluded last
    /// (AUTOMATION-1373). That run launched against every symbol owed now, each at the
    /// revision it is at now, over the tree there is now, and no dependency fanout has
    /// arrived since: the same selection over the same bytes. Its red is the answer for
    /// those inputs. Under CPU starvation a retry costs as long as the run did, and every
    /// cohort that folded while it ran had queued one, so a gate never converged.
    ///
    /// Not launching can never make a green: the debt stays owed, the red stays standing.
    /// An edit, a newly owed symbol, a fanout, or a full suite requested moves one of these
    /// and launches as before. A run that executed nothing is no conclusion and is retried.
    let onlyRetriesRed (state: TestPruneState) =
        match state.LastRed with
        | Some red ->
            not (TestMode.requestsFullSuite state.Mode)
            && Set.isEmpty state.PendingForceRunProjects
            && Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
               |> Set.forall (fun symbol -> Map.tryFind symbol red.SymbolRevisions = Some(revisionOf state.Debt symbol))
            && ReceiptInputTree.matches (Some red.Tree) (ReceiptInputTree.read repoRoot)
        | None -> false

    /// Say why an owed launch was not made (`onlyRetriesRed`).
    let logRedStands (trigger: string) (state: TestPruneState) =
        Logging.info
            "test-prune"
            $"%s{trigger}: %s{owedDescription state.Debt} — all of it was in the run that concluded red over this same tree, so a launch would only retry it; that result stands until the tree or what is owed moves (AUTOMATION-1373)"

    /// Discharge what a BootScan cohort owes when the last completed run already covered
    /// it: that run passed every runnable project in full over the tree and model the
    /// cohort sealed on, every file the cohort checked is part of that tree, and the
    /// index names no coverer it did not run. A cold daemon's build launches that run
    /// before the scan has checked the tree, so the cohort usually seals after the run's
    /// result has folded, with no run in flight to attach to (`joinsFullRun`). Launching
    /// for it would run the tree's impact a second time.
    /// An in-session cohort is an edit, and debt the covering run cannot speak for (an
    /// unreadable ledger, a missing baseline, a dependency force-run) keeps its launch.
    let dischargeCoveredBootScan (ctx: PluginCtx<TestPruneMsg>) (batch: BatchChecked) (state: TestPruneState) =
        // A file outside the hashed tree can change without moving its identity.
        let inTree (tree: ReceiptTree) =
            let hashed = tree.Entries |> List.map fst |> Set.ofList

            batch.Files
            |> List.forall (fun file ->
                hashed.Contains(Path.GetRelativePath(repoRoot, AbsFilePath.value file).Replace('\\', '/')))

        // An idle boot scan that owes debt the covering run can speak for.
        let bootScanOwesCoverableDebt () =
            batch.Trigger = BootScan
            && state.InFlight.IsNone
            && not (Set.isEmpty state.Debt.PendingQueue && Map.isEmpty state.Debt.RuntimeObligations)
            && not state.Debt.RecoveryOutstanding
            && Option.isNone (baselineInvalidReason state.Debt)
            && Set.isEmpty state.PendingForceRunProjects

        // The covering run passed this very tree, under the current model.
        let coveredBy (tree: ReceiptTree) generation =
            ReceiptInputTree.matches (Some tree.Identity) state.SealedTree
            && inTree tree
            && observeModelGeneration ctx = generation
            && indexNamesOnlyRunnable (lazy (resolveExcludedProjects ()))

        match state.CoveredFullSuite with
        | Some(tree, generation) when bootScanOwesCoverableDebt () && coveredBy tree generation ->
            Logging.info
                "test-prune"
                $"BatchChecked: %s{owedDescription state.Debt} discovered by BootScan over the tree the last full-suite run passed — covered by that run, no re-run"

            // Shed what a covering completion sheds: the queue, and the in-memory view
            // the next selection reads from, so a later edit selects only itself.
            { state with
                Debt =
                    { commitPending state.Debt.PendingQueue state.Debt with
                        RuntimeObligations = Map.empty }
                ChangedFiles = []
                ChangedSymbols = []
                AffectedTests = Analyzed [] }
        | _ -> state

    /// Attach what is owed to the full run in flight. The FIRST captured revision stands:
    /// a later attach must not advance a symbol past an edit the held run never built.
    let attachToFullRun (state: TestPruneState) =
        { state with
            DebtDuringFullRun =
                (state.DebtDuringFullRun, state.Debt.PendingQueue)
                ||> Set.fold (fun captured symbol ->
                    if Map.containsKey symbol captured then
                        captured
                    else
                        Map.add symbol (revisionOf state.Debt symbol) captured) }

    /// The run in flight has concluded: forget it, and end the pass-through it ran for
    /// when it executed at least one project to a verdict (`TestMode.afterRun`).
    let endRun (executedAny: bool) (state: TestPruneState) =
        let mode =
            match state.InFlight with
            | Some run -> TestMode.afterRunIn initialMode run.Mode executedAny state.Mode
            | None -> state.Mode

        // Said out loud: a later launch in the same session is impact-selected again, and
        // its status line (`selected: yes`) reads as if the confirm had lost its scope.
        if TestMode.requestsFullSuite state.Mode && not (TestMode.requestsFullSuite mode) then
            Logging.info
                "test-prune"
                "Scope back to IMPACT-FILTERED: the full-suite run the scope was set for has concluded; later launches in this session are impact-selected"
        elif TestMode.launchesOwedRuns state.Mode && not (TestMode.launchesOwedRuns mode) then
            Logging.info
                "test-prune"
                "Holding test runs again: the run a command asked for has concluded; later builds launch none until a command (check, confirm, test-rerun) asks"
        elif
            not executedAny
            && state.InFlight |> Option.exists (fun run -> TestMode.requestsFullSuite run.Mode)
        then
            Logging.info
                "test-prune"
                "Scope stays FULL SUITE: the run launched for it executed no project to a verdict, so the next launch runs every project in full"

        { state with
            InFlight = None
            Mode = mode }

    /// A launch that found its artifacts or test host unavailable ran nothing: it revokes
    /// the receipt, hands back the fanout it consumed, and fails.
    let unavailableRun
        (ctx: PluginCtx<TestPruneMsg>)
        (state: TestPruneState)
        (message: string)
        (owed: Set<string>)
        (reply: Tasks.TaskCompletionSource<string> option)
        =
        ctx.ReportStatus(PluginStatus.failedNow message message TimeSpan.Zero)

        { endRun false state with
            Receipt = HeldReceipt.Revoked $"the test run could not start: %s{message}"
            PendingForceRunProjects = Set.union state.PendingForceRunProjects owed
            Replies =
                reply
                |> Option.map (fun target -> target, JsonSerializer.Serialize {| error = message |})
                |> Option.toList }

    // Launched from the mailbox so it is serialised with every other launch site and
    // holds the "tests" key for its whole duration — see the `RunTestsRequested` case
    // for why that matters.
    //
    // Cooperative-safe: a force-run exists only for the client that asked for it, so the
    // framework may cancel it once that client is gone. Its test hosts run in its own
    // process scope (reaped), its result publishes only through the fold it returns
    // (never folded when cancelled), and its `finally` closes the run it opened.
    let requestTestRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) configs filter reply =
        let workOf runCtx =
            PluginWork.cooperativeSafe (commandForceRun runCtx state.Mode configs filter reply)

        match runTestHostExclusive ctx Set.empty (Some reply) workOf with
        | Claimed ->
            { state with
                Receipt = HeldReceipt.Revoked "a manual test run replaced it"
                InFlight =
                    Some
                        { Scope = LaunchedSelection
                          Mode = state.Mode
                          WatcherReported = Set.empty } }
        | SlotBusy ->
            // A busy key QUEUES the run, never refuses it: a refusal that reads as
            // success is a vacuous green. The intent waits behind the holder, owned, and
            // the IPC command bounds its own wait on `reply`.
            ctx.Log "  ↳ queued run-tests force-run (tests already running)"

            ctx.EnqueueExclusiveIntent "tests" None (RunTestsRequested(configs, filter, reply))
            |> ignore

            state

    /// The owed run an `ImpactRunRequested` intent stands for, decided against the state it
    /// is delivered into. Flush first, so changes that landed while the previous run held
    /// the key are selected; then launch only if something is still owed. An intent is
    /// queued while a run is in flight, and that run may have discharged exactly the debt
    /// that queued it: launching blindly would produce a zero-project run whose result
    /// erases the passing evidence.
    ///
    /// Stale needs the evidence to cover the tree as well. A run that executed nothing
    /// (every client abandoned it, its artifacts or test host were unavailable) discharged
    /// nothing, and the debt alone cannot show that: a tree moved only by an edit the
    /// symbol diff sees as changing no symbol owes no symbol, yet has no evidence. The
    /// rerun queued behind such a run is what verifies it.
    let impactRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        match testConfigs with
        | Some configs when not configs.IsEmpty ->
            match
                (try
                    Ok(flushAndQueryAffectedBounded ctx state)
                 with ex ->
                     Error ex)
            with
            | Error ex ->
                Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
                tryRepairSchemaDrift ex
                ctx.ReportStatus(PluginStatus.failedNow ex.Message $"rerun flush failed: %s{ex.Message}" TimeSpan.Zero)
                state
            | Ok rerunState when
                nothingOwed rerunState.Debt
                && Set.isEmpty rerunState.PendingForceRunProjects
                && ReceiptInputTree.matches
                    (rerunState.EvidenceReceipt |> Option.bind (fun receipt -> receipt.InputTreeHash))
                    (ReceiptInputTree.read repoRoot)
                ->
                Logging.info
                    "test-prune"
                    "Queued impact rerun is stale — nothing is owed, no dependency fanout remains, and the evidence covers the current tree"

                rerunState
            | Ok rerunState when onlyRetriesRed rerunState ->
                logRedStands "Queued impact rerun" rerunState
                rerunState
            | Ok rerunState ->
                Logging.info "test-prune" "Re-running tests (queued during previous run)"
                let fanout = rerunState.PendingForceRunProjects

                let launchState =
                    { rerunState with
                        PendingForceRunProjects = Set.empty }

                match launchImpactRun ctx configs launchState rerunState.LastResults.IsSome fanout with
                | Some launched -> launched
                | None when joinsFullRun rerunState false -> attachToFullRun rerunState
                | None ->
                    enqueueImpactRun ctx
                    rerunState
        | _ -> state

    /// Launch the run a session that launches only requested runs held back, now that a
    /// command has asked for it. `state` is already armed (`TestMode.armOwedRun`). Flushed
    /// first, like a build's launch, so changes folded since the hold are selected; the
    /// launch then applies every widening a build's would — the missing baseline, the
    /// evidence gap, quarantined reds.
    let launchRequested (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        match testConfigs with
        | Some configs when not configs.IsEmpty ->
            match
                (try
                    Ok(flushAndQueryAffectedBounded ctx state)
                 with ex ->
                     Error ex)
            with
            | Error ex ->
                Logging.error "test-prune" $"flushAndQueryAffected (requested run) failed: %s{ex.Message}"
                tryRepairSchemaDrift ex

                ctx.ReportStatus(
                    PluginStatus.failedNow ex.Message $"requested run flush failed: %s{ex.Message}" TimeSpan.Zero
                )

                state
            | Ok flushed ->
                Logging.info "test-prune" "A command asked for the owed test run: launching the run this session held"
                let fanout = flushed.PendingForceRunProjects

                let launchState =
                    { flushed with
                        PendingForceRunProjects = Set.empty
                        OwedRunHeld = false }

                match launchImpactRun ctx configs launchState flushed.LastResults.IsSome fanout with
                | Some launched -> launched
                | None when joinsFullRun flushed false -> attachToFullRun { flushed with OwedRunHeld = false }
                | None ->
                    enqueueImpactRun ctx
                    { flushed with OwedRunHeld = false }
        | _ -> state

    /// Hold a build's launch for its tree's check results (`DiffWait`), and post its
    /// expiry. The mailbox is not blocked: the `BatchChecked` it waits for is delivered
    /// through it.
    let holdForDiff (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        let held =
            { Id = Guid.NewGuid()
              Since = DateTime.UtcNow }

        Logging.info
            "test-prune"
            $"BuildSucceeded: the check results for the tree this build compiled are not folded yet, so its symbol diff is unknown — holding the launch for the next BatchChecked (at most %.0f{DiffWait.bound.TotalSeconds}s)"

        Tasks.Task
            .Delay(DiffWait.bound)
            .ContinueWith(fun (_: Tasks.Task) ->
                try
                    ctx.Post(DiffWaitExpired held.Id)
                with ex ->
                    Logging.debug "test-prune" $"held launch expiry not delivered: %s{ex.Message}")
        |> ignore

        { state with AwaitingDiff = Some held }

    /// Launch the run a build held back (`holdForDiff`), selected from the state it is
    /// delivered into, where the diff has now folded, or the wait expired without it.
    let launchHeld (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (why: string) =
        let waited =
            state.AwaitingDiff
            |> Option.map (fun held -> (DateTime.UtcNow - held.Since).TotalSeconds)
            |> Option.defaultValue 0.0

        let state = { state with AwaitingDiff = None }

        match testConfigs with
        | Some configs when not configs.IsEmpty ->
            Logging.info "test-prune" $"Launching the build's held run after %.1f{waited}s: %s{why}"
            let fanout = state.PendingForceRunProjects

            let launchState =
                { state with
                    PendingForceRunProjects = Set.empty }

            match launchImpactRun ctx configs launchState state.LastResults.IsSome fanout with
            | Some launched -> launched
            | None when joinsFullRun state false -> attachToFullRun state
            | None ->
                // A run that claimed the key after the build describes the built tree.
                Logging.info "test-prune" "  ↳ tests slot already held — queueing re-run"
                enqueueImpactRun ctx
                state
        | _ -> state

    /// Make a candidate's durable debt the record a restart reads, then publish it.
    ///
    /// The marker goes first and comes off only in `Finalize`, after the candidate is
    /// published: a restart that finds it cannot know which sidecars describe a published
    /// state, and treats what is owed as unknown. Only what changed is written, unless an
    /// earlier publication left the marker, in which case every sidecar is rewritten from
    /// this candidate. While the debt is unknown nothing is written, the marker included: the unreadable
    /// record is the honest one until a full suite recovers it.
    ///
    /// Replies owed by the candidate are resolved last, so a command never acknowledges an
    /// outcome that is not published.
    let prepareCommit (prior: TestPruneState) (candidate: TestPruneState) =
        async {
            let changed (read: TestPruneState -> 'T) =
                not (obj.ReferenceEquals(read prior, read candidate))
                && read prior <> read candidate

            let queueChanged = changed (fun state -> state.Debt.PendingQueue)
            let failuresChanged = changed (fun state -> state.OutstandingFailures)
            let obligationsChanged = changed (fun state -> state.Debt.RuntimeObligations)
            let baselineChanged = changed (fun state -> state.Debt.Baseline)
            let recoveryChanged = changed (fun state -> state.Debt.RecoveryOutstanding)
            let notIngestedChanged = changed (fun state -> state.Debt.CoverageNotIngested)
            let interrupted = File.Exists debtPublicationPath

            let publishes =
                interrupted
                || queueChanged
                || failuresChanged
                || obligationsChanged
                || baselineChanged
                || recoveryChanged
                || notIngestedChanged

            // While the debt is unknown nothing is written: the unreadable record is the
            // honest one until a full suite recovers it. No publication begins then, so
            // no marker is written either. One written then outlived the session, since
            // only a recovered state removes it, and the next daemon read an unfinished
            // publication that never happened. A marker an earlier publication left
            // stays, because the sidecars on disk are still that publication's.
            if publishes && not candidate.Debt.RecoveryOutstanding then
                FsHwPaths.atomicWriteAllText debtPublicationPath "verification debt publication in progress"
                let rewrite = interrupted || recoveryChanged

                if rewrite || queueChanged then
                    PendingVerification.save repoRoot candidate.Debt.PendingQueue

                if rewrite || failuresChanged then
                    OutstandingFailure.save repoRoot candidate.OutstandingFailures

                if rewrite || obligationsChanged then
                    saveRuntimeCoverageObligations repoRoot candidate.Debt.RuntimeObligations

                if rewrite || notIngestedChanged then
                    CoverageNotIngested.save repoRoot candidate.Debt.CoverageNotIngested

                match candidate.Debt.Baseline with
                | Some baseline when rewrite || baselineChanged -> FullSuiteBaseline.save repoRoot baseline
                | _ -> ()

            // Evidence, not debt: it records which projects ran whole over which compile
            // inputs, which stays true whatever is owed. A state that earned none leaves
            // the last record standing, since every entry is checked against the compile
            // inputs of the daemon that reads it.
            if changed (fun state -> state.Earned) then
                match candidate.Earned |> Option.map EarnedEvidence.wholeProjectInputs with
                | Some record when not (Map.isEmpty record) -> WholeProjectEvidence.save repoRoot record
                | _ -> ()

            return
                { Finalize =
                    async {
                        if not candidate.Debt.RecoveryOutstanding then
                            // The runtime marker is written by a run before its failure
                            // reaches this owner, so only the state that recovered from
                            // unknown debt may remove it.
                            let markers =
                                if recoveryChanged then
                                    [ runtimeCoverageRecoveryPath repoRoot; debtPublicationPath ]
                                else
                                    [ debtPublicationPath ]

                            // `File.Delete` of an absent file is a no-op, but not in an
                            // absent directory: a repository that never wrote debt has none.
                            for marker in markers do
                                if File.Exists marker then
                                    File.Delete marker

                        for reply, response in candidate.Replies do
                            reply.TrySetResult response |> ignore
                    } }
        }

    { Name = PluginName.create FsHotWatch.PluginActivity.TestPrunePluginName
      Init = initialState
      Update =
        fun ctx state event ->
            async {
                // Replies belong to the event that owes them; the previous event's were
                // resolved when its state was published.
                let state = { state with Replies = [] }

                // Only a check result folds while a refresh runs beside the fold: every
                // other event may read the extensions' edges or failures (AUTOMATION-1441).
                let state =
                    match event with
                    | PluginEvent.FileChecked _ -> state
                    | _ -> joinRefresh ctx state

                let modelGeneration = observeModelGeneration ctx
                reconcileFreshness modelGeneration

                let state =
                    if
                        state.AnalysisModelGeneration <> modelGeneration
                        && not state.PendingAnalysis.IsEmpty
                    then
                        // Pending compiler results belong to the model they were accepted
                        // under. Retire them before a flush can write them into another
                        // model's symbol index. Their changed symbols stay owed, and the
                        // index can no longer vouch for what covers them, so every runnable
                        // project owes a run.
                        // Outcomes already recorded for the current model stay: a replayed
                        // `FileChecked` records one without passing through this fold, and
                        // dropping it here left the seal refusing a file no later check
                        // re-analysed (AUTOMATION-1415).
                        let currentOutcomes =
                            AnalysisFileEvidence.forModel modelGeneration state.AnalysisFiles

                        Logging.info
                            "test-prune"
                            $"project model changed (%A{state.AnalysisModelGeneration} -> %A{modelGeneration}); retiring %d{state.PendingAnalysis.Count} project(s) of pending analysis, keeping %d{currentOutcomes.Count} file outcome(s) already recorded for the current model"

                        // The retired analyses never reached the index. Where it does not
                        // already hold the same analysis, it cannot vouch for the file
                        // until the file is analysed and flushed again, and nothing this
                        // process cached for it may replay.
                        let retired =
                            state.PendingIdentities
                            |> Map.filter (fun relPath identity -> indexedKey relPath <> Some identity)
                            |> Map.keys
                            |> Set.ofSeq

                        { state with
                            PendingAnalysis = Map.empty
                            PendingIdentities = Map.empty
                            Unindexed = Set.union state.Unindexed retired
                            AnalysisModelGeneration = modelGeneration
                            AnalysisFiles = currentOutcomes
                            CohortSeal = None
                            AnalysisReceipt = None
                            PendingForceRunProjects = Set.union state.PendingForceRunProjects runnableProjects }
                    else
                        state

                // Analysis folds only a result or seal published against the model this host
                // publishes now. One from a replaced model was admitted before the
                // replacement, and its cohort is retried against the current model. One
                // with no model was captured against none, so it cannot describe this one.
                let notCurrent (published: int64 option) =
                    published.IsNone || published <> modelGeneration

                // Recorded before any arm can return early: whether a moved path is an
                // edit or the run's own write depends only on the watcher having seen it.
                let state =
                    match event, state.InFlight with
                    | PluginEvent.FileChecked result, Some run ->
                        let relPath =
                            Path.GetRelativePath(repoRoot, AbsFilePath.value result.File).Replace('\\', '/')

                        Logging.debug "test-prune" $"watcher reported %s{relPath} during the in-flight run"

                        { state with
                            InFlight =
                                Some
                                    { run with
                                        WatcherReported = Set.add relPath run.WatcherReported } }
                    | _ -> state

                match event with
                | PluginEvent.FileChecked result when notCurrent result.ModelGeneration ->
                    Logging.info
                        "test-prune"
                        $"ignoring FileChecked for %s{AbsFilePath.value result.File} from model %A{result.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | PluginEvent.BatchChecked batch when notCurrent batch.ModelGeneration ->
                    Logging.info
                        "test-prune"
                        $"ignoring BatchChecked from model %A{batch.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | PluginEvent.FileChecked result ->
                    let analysisStarted = DateTime.UtcNow
                    let fileStr = AbsFilePath.value result.File
                    let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

                    // The ONE treatment for a file whose symbol analysis
                    // failed (an `analyzeSourceFromResults` Error and a handler fault are the same
                    // condition). The file is REMEMBERED as unanalysable, with three
                    // consequences, none silent:
                    //   1. a WARNING lands in the error ledger, keyed to the file, so
                    //      `fshw check` prints it and — under the default warn-fail
                    //      policy — refuses a green verdict;
                    //   2. `runTestsWithImpact` falls back to EVERY test project in full
                    //      while the set is non-empty;
                    //   3. the non-empty force-run set disables the zero-affected skip
                    //      gate, so this can never end as "0 affected — green, 0 ran".
                    // The file leaves the set the moment it analyses cleanly.
                    //
                    // The Failed stamp needs no idle guard: the framework's ReportStatus
                    // funnel drops any terminal stamped while an exclusive run is in
                    // flight (the run owns the status), so a mid-run analysis failure
                    // cannot manufacture a terminal. The ledger entry and the
                    // force-full-suite consequence persist either way.
                    let markUnanalysable (reason: string) (detail: string) (logDetail: string) : TestPruneState =
                        match FileFreshness.resolvePresence repoRoot relPath with
                        | FileFreshness.Gone ->
                            // A path that no longer exists is GONE, not a
                            // file that failed analysis: there is no source to be invisible
                            // and no parse error to fix. Warning here named files a merge had
                            // deleted, and the warning denied the gate its green. Forget the
                            // file everywhere this plugin remembers it instead.
                            Logging.info
                                "test-prune"
                                $"%s{relPath} no longer exists; dropping it rather than reporting '%s{reason}' (%s{detail})"

                            updateFreshness (
                                FileFreshness.stamp
                                    FileFreshness.Gone
                                    false
                                    DateTime.UtcNow
                                    relPath
                                    (Volatile.Read(&freshnessRef))
                            )

                            { state with
                                AnalysisFiles = Map.remove result.File state.AnalysisFiles
                                UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles
                                Unindexed = Set.remove relPath state.Unindexed }
                        | FileFreshness.Present ->
                            Logging.error
                                "test-prune"
                                $"%s{reason} for %s{relPath}: %s{logDetail} — this file is INVISIBLE to the impact graph (no symbols), so every test project will be run in full until it analyses cleanly"

                            ctx.ReportErrors fileStr [ unanalyzableFileDiagnostic relPath detail ]

                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"%s{reason}: %s{detail}",
                                    DateTime.UtcNow,
                                    RunVerdict.create $"%s{reason}: %s{detail}" (DateTime.UtcNow - analysisStarted)
                                )
                            )

                            { state with
                                AnalysisFiles =
                                    Map.add
                                        result.File
                                        (AnalysisFileEvidence.fromResult ctx.FcsSuppressedCodes result (Error detail))
                                        state.AnalysisFiles
                                UnanalyzableFiles =
                                    Map.add
                                        relPath
                                        { RelPath = relPath
                                          File = fileStr
                                          Reason = detail }
                                        state.UnanalyzableFiles }

                    try
                        // Canonical project identity. For real .fsproj files, FCS
                        // gives "MyProject.fsproj" → "MyProject". For .fsx scripts
                        // FCS synthesizes "Lib.fsx.fsproj" → "Lib.fsx" after one
                        // strip; drop the trailing ".fsx" so config that specifies
                        // `"Lib"` matches both cases.
                        let projectName =
                            match result.CheckResults with
                            | ParseOnly ->
                                // Analysis below fails explicitly before this identity is
                                // consumed. Do not inspect ProjectOptions: an aborted FCS
                                // check may not carry usable options either.
                                ""
                            | Replayed _ -> raise (ReplayedResultException("test-prune symbol analysis", fileStr))
                            | FullCheck _ ->
                                let raw = result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                                if raw.EndsWith(".fsx") then
                                    raw.Substring(0, raw.Length - 4)
                                else
                                    raw

                        // The per-file freshness sidecar gates the `detectChanges` call
                        // site, not the symbol-DB write: dirty FCS results are still
                        // persisted (cold-scan rows must go in), and the sidecar records
                        // `fcsClean = false` so a cross-restart replay treats those rows
                        // as untrusted-for-diff. The next clean recheck overwrites the
                        // rows and flips the sidecar back.
                        let currentClean =
                            not (
                                hasFcsErrors
                                    (FileCheckResult.suppression ctx.FcsSuppressedCodes result)
                                    result.CheckResults
                            )

                        let storedFreshness =
                            let store = Volatile.Read(&freshnessRef)
                            FileFreshness.classify relPath store

                        // FileChecked is the completion payload from the daemon's warm FCS
                        // pass. Re-entering FSharpChecker here duplicated that expensive
                        // work and could observe a different compiler state than the event
                        // being handled. TestPrune 8.1 accepts the existing parse/check
                        // results directly. ParseOnly is not analysable: spelling that as
                        // Error keeps the established fail-closed full-suite fallback.
                        // Checked under a virtual root, the results name the file and
                        // every symbol by virtual path: analysis asks for the file by that
                        // name, and repository-relative paths come from the virtual root,
                        // the same in every worktree.
                        let analysisRoot =
                            result.Frame
                            |> Option.fold (fun _ (frame: PathFrame.PathFrame) -> frame.Virtual) repoRoot

                        let analysisFile = PathFrame.nameIn result.Frame fileStr

                        let analysisResult =
                            match result.CheckResults with
                            | FullCheck checkResults ->
                                analyzeSourceFromResults
                                    analysisFile
                                    result.Source
                                    result.ParseResults
                                    checkResults
                                    projectName
                            | Replayed _ -> raise (ReplayedResultException("test-prune symbol analysis", fileStr))
                            | ParseOnly ->
                                Error
                                    "FCS produced parse-only results; full type-check results are required for impact analysis"

                        match analysisResult with
                        | Ok analysisResult ->
                            // Said only once the analysis has produced symbols to persist:
                            // a syntax error refuses the analysis below, and that file is
                            // reported as unanalysable instead (AUTOMATION-1391).
                            if not currentClean then
                                let errCount =
                                    fcsErrorCount
                                        (FileCheckResult.suppression ctx.FcsSuppressedCodes result)
                                        result.CheckResults

                                Logging.warn
                                    "test-prune"
                                    $"FCS reported %d{errCount} error(s) for %s{relPath}; persisting symbols but marking file dirty in freshness sidecar (Phase B detectChanges will fall back for this file)"

                            let normalizedSymbols = normalizeSymbolPaths analysisRoot analysisResult.Symbols

                            let fileAnalysis =
                                { Symbols = normalizedSymbols
                                  Dependencies = analysisResult.Dependencies
                                  TestMethods =
                                    analysisResult.TestMethods
                                    |> List.map (fun t -> { t with TestProject = projectName })
                                  Attributes = analysisResult.Attributes
                                  ParentLinks = analysisResult.ParentLinks
                                  Diagnostics = analysisResult.Diagnostics }

                            // Read stored symbols from the in-memory snapshot (populated after
                            // each flush). Falls back to DB for warm starts where the snapshot
                            // hasn't been populated yet.
                            let storedSymbols =
                                match Map.tryFind relPath state.SymbolSnapshot with
                                | Some symbols -> symbols
                                | None -> db.GetSymbolsInFile(relPath)

                            // WHOSE rows are these? `storedSymbols` is
                            // non-empty both when a prior run indexed this file and when
                            // THIS run did, seconds ago — and those mean opposite things.
                            // The ledger remembers the first look, which is necessarily
                            // taken before this session wrote anything for the file, so
                            // it survives the snapshot short-circuit above.
                            let storedRows = priorRows.Classify(relPath, not storedSymbols.IsEmpty)

                            // Accumulate per-project; flush on BuildCompleted.
                            // Replace any prior analysis for this file to avoid double-counting
                            // when a file is checked more than once before the flush (e.g. initial
                            // scan followed by a file-change recheck).
                            let existingForProject =
                                state.PendingAnalysis |> Map.tryFind projectName |> Option.defaultValue []

                            let filteredExisting =
                                existingForProject
                                |> List.filter (fun a ->
                                    not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                            let newPending =
                                state.PendingAnalysis
                                |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                            // Can the stored rows be diffed against? The CURRENT
                            // extraction must be FCS-clean (a dirty current result means
                            // the just-extracted symbols are themselves suspect); given
                            // that, `FileFreshness.trustStoredRows` decides, from the
                            // sidecar's verdict plus what the index HOLDS for this file
                            // and SINCE WHEN. Every arm of that pair is load-bearing and
                            // all are documented there —
                            // `EverySymbolIsNew`, which is what a `Clean` stamp means once
                            // a schema recreate has emptied the index underneath it, and
                            // `RowsFromThisRun`, which is what rows mean
                            // when this run is the one that wrote them.
                            let storedTrust = FileFreshness.trustStoredRows storedFreshness storedRows

                            // `planLook` is the whole decision. Its only way of
                            // contributing nothing is the one that HIDES a file's changes,
                            // and it goes out at warn. It replaced a `(names, bool)` pair
                            // whose bool this call site computed and then `ignore`d.
                            let changedNames, notSelected =
                                match FileFreshness.planLook currentClean storedTrust with
                                | FileFreshness.Diffable baseline ->
                                    // WHAT to diff against is the plan's decision, not
                                    // `storedSymbols`'s. This call site used to pass the
                                    // stored rows for BOTH diffable arms, which made
                                    // `EverySymbolIsNew` behave exactly like
                                    // `DiffAgainstStored` — and whenever the rows were
                                    // this run's own, that is the self-comparison
                                    // the `RowsFromThisRun` clock was introduced to replace.
                                    // A self-comparison reports zero changes every time.
                                    let priorSymbols = FileFreshness.baselineRows baseline storedSymbols

                                    let (changes, _events) = detectChanges normalizedSymbols priorSymbols

                                    // A trustworthy extraction has now
                                    // been taken AND consumed, so the rows it leaves
                                    // behind are a real "before" for the next look —
                                    // which is what keeps a missing baseline costing ONE
                                    // widening per file rather than one on every save.
                                    // Marked only here: the bypass arms below discard
                                    // their extraction, and a baseline they never
                                    // established must not be claimed.
                                    priorRows.MarkBaselineEstablished relPath

                                    Logging.info
                                        "test-prune"
                                        $"detectChanges for %s{relPath} (stored=%A{storedFreshness}, rows=%A{storedRows}, trust=%A{storedTrust}, baseline=%A{baseline}): %d{changes.Length} changes, %d{priorSymbols.Length} diffed against, %d{normalizedSymbols.Length} current"

                                    changedSymbolNames changes, Map.remove relPath state.NotSelected
                                | FileFreshness.FileUnverified ->
                                    // The one arm that DROPS a file's changes. At warn,
                                    // and stating the consequence rather than the
                                    // mechanism, because "detectChanges bypassed" at info
                                    // is what this defect looked like for three runs.
                                    Logging.warn
                                        "test-prune"
                                        $"NOT SELECTED: %s{relPath} changed but FCS reported errors for it on this check, so its symbols may be partial and no tests were selected from it (stored=%A{storedFreshness}, rows=%A{storedRows}, storedRowCount=%d{storedSymbols.Length}). The next FCS-clean check of this file widens it back in."

                                    [], Map.add relPath NotSelectedReason.FcsErrors state.NotSelected

                            let newChangedSymbols, newDebt =
                                if not changedNames.IsEmpty then
                                    // These feed straight into `enqueuePending`, so this
                                    // line is the primary evidence when diagnosing over-
                                    // or under-selection. `describeMany`, not `%A` —
                                    // the count must be exact and uncapped.
                                    Logging.info "test-prune" $"Changed symbols: %s{describeMany changedNames}"

                                    // Queued at the SAME point the in-memory hot view
                                    // accumulates, and written durably before the BatchChecked
                                    // analysis flush advances the symbol snapshot. They leave
                                    // the queue only when a covering test run passes
                                    // (TestsFinished) or they prove to have no covering test
                                    // (flushAndQueryAffected).
                                    (state.ChangedSymbols @ changedNames) |> List.distinct,
                                    enqueuePending changedNames state.Debt
                                else
                                    state.ChangedSymbols, state.Debt

                            // Only track file as changed if its AST actually changed.
                            // Comment-only changes produce the same symbol hashes, so they
                            // should not trigger extension-based tests (e.g. Falco routes).
                            let newChangedFiles =
                                let runtimeRelevantChange = not changedNames.IsEmpty || fileAnalysis.Symbols.IsEmpty

                                if runtimeRelevantChange && not (state.ChangedFiles |> List.contains relPath) then
                                    relPath :: state.ChangedFiles
                                else
                                    state.ChangedFiles

                            // Update class→file mapping for test methods found in this file
                            let newClassFiles =
                                fileAnalysis.TestMethods
                                |> List.fold (fun acc t -> Map.add t.TestClass fileStr acc) state.TestClassFiles

                            // `AffectedTests` is set exclusively by `flushAndQueryAffected`
                            // on BuildCompleted and consumed by `runTestsWithImpact`; the
                            // `affected-tests` IPC command computes its own answer on
                            // demand from `ChangedSymbols` against the current DB.
                            let newState =
                                { state with
                                    Debt = newDebt
                                    NotSelected = notSelected
                                    ChangedFiles = newChangedFiles
                                    PendingAnalysis = newPending
                                    PendingIdentities =
                                        Map.add
                                            relPath
                                            (IndexedAnalysis.identityOf repoRoot result
                                             |> Option.defaultWith IndexedAnalysis.undescribed)
                                            state.PendingIdentities
                                    AnalysisModelGeneration = modelGeneration
                                    AnalysisFiles =
                                        Map.add
                                            result.File
                                            (AnalysisFileEvidence.fromResult ctx.FcsSuppressedCodes result (Ok()))
                                            state.AnalysisFiles
                                    ChangedSymbols = newChangedSymbols
                                    TestClassFiles = newClassFiles
                                    // The file analysed cleanly, so it is back in the impact
                                    // graph and no longer owes the coarse fallback. The
                                    // framework already cleared this plugin's ledger entries
                                    // for the file when the FileChecked arrived, and dropping
                                    // it here means the next test run's ledger rewrite stops
                                    // re-reporting the warning: the entry
                                    // leaves the ledger because the CONDITION cleared, which
                                    // is the only reason any entry may leave it.
                                    UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles }

                            // Stamp the freshness sidecar with the result of THIS check.
                            // After analysis, not at the top, so a failed `analyzeSourceFromResults`
                            // cannot lock in a clean stamp for a file we have no symbols
                            // for. `markClean` only with a BuildCompleted this session
                            // (see `BuildCompletedInThisSession`) AND a clean FCS result;
                            // otherwise `markUnverified`, which will not downgrade a
                            // previously-clean entry to dirty.
                            let now = DateTime.UtcNow

                            // A check of a path that has since vanished
                            // forgets the record rather than re-stamping it — see
                            // `FileFreshness.stamp`.
                            let updatedFreshness =
                                FileFreshness.stamp
                                    (FileFreshness.resolvePresence repoRoot relPath)
                                    (currentClean && state.BuildCompletedInThisSession)
                                    now
                                    relPath
                                    (Volatile.Read(&freshnessRef))

                            updateFreshness updatedFreshness

                            // A helper analysis is not new evidence about a test red. Keep
                            // the fresh run's terminal provenance until a covering run
                            // clears the outstanding failure.
                            let analysisFinished = DateTime.UtcNow

                            if List.isEmpty state.OutstandingFailures then
                                ctx.ReportStatus(
                                    Completed(
                                        analysisFinished,
                                        RunVerdict.create
                                            $"symbol analysis: %s{relPath}, %d{List.length changedNames} changed symbol(s); no run due"
                                            (analysisFinished - analysisStarted)
                                    )
                                )

                            return newState
                        | Error msg ->
                            // On analysis failure the file must NOT be dropped: a
                            // dropped file contributes no symbols, a change to it diffs
                            // against nothing and selects NO tests, and the check reports
                            // green having run nothing relevant. Silent under-selection:
                            // the one failure mode a test-impact tool must not have. See
                            // `markUnanalysable` for the treatment.
                            return markUnanalysable "Analysis failed" msg msg

                    with ex ->
                        // A fault ANYWHERE in this handler — not just an `analyzeSourceFromResults`
                        // Error — leaves the file unanalysed, the same condition and so
                        // the same treatment.
                        return markUnanalysable "FileChecked handler failed" ex.Message (ex.ToString())

                | PluginEvent.BatchChecked batch ->
                    // Cohort-complete flush. The mailbox is FIFO and the daemon emits
                    // BatchChecked strictly after the last FileChecked, so every
                    // FileChecked from this cohort is already folded into
                    // `ChangedSymbols`/`PendingAnalysis` by now.
                    //
                    // This is the canonical DB persistence point, NOT BuildCompleted. On a
                    // cold scan `Daemon.performScan` awaits BuildPlugin terminal BEFORE
                    // the FCS tier checks, so BuildCompleted reaches this mailbox before
                    // any FileChecked — a BuildCompleted-only flush would always fire
                    // against an empty `PendingAnalysis` and leave the symbol DB
                    // permanently empty. BuildCompleted's flush stays as an idempotent
                    // re-run plus the test-trigger.
                    let flushed =
                        try
                            Ok(flushAndQueryAffectedBounded ctx state)
                        with ex ->
                            Error ex

                    match flushed with
                    | Error ex ->
                        Logging.error "test-prune" $"BatchChecked flushAndQueryAffected failed: %s{ex.Message}"
                        tryRepairSchemaDrift ex
                        return state
                    | Ok flushedState ->
                        // The seal is the moment the scan has an answer: every checkable
                        // file of this model either analysed or did not. An analysis-only
                        // daemon publishes that answer as its receipt. A daemon that runs
                        // tests grades its test evidence through it, so a file the scan
                        // never finished refuses the run selected without its symbols.
                        let flushedState =
                            // A seal that re-discovered the model on the scoped path names
                            // the files it did not re-check because their inputs did not
                            // change. Their outcomes stand for this model.
                            let analysisFiles =
                                match batch.Retained, modelGeneration with
                                | Some carried, Some generation ->
                                    AnalysisFileEvidence.carryInto generation carried flushedState.AnalysisFiles
                                | _ -> flushedState.AnalysisFiles

                            let membership =
                                match ctx.ProjectGraph.ObserveCheckableFiles() with
                                | Some(generation, files) when Some generation = modelGeneration -> Some files
                                | Some _
                                | None -> None

                            let seal =
                                membership
                                |> Option.bind (fun files ->
                                    AnalysisEvidence.fromCompleted modelGeneration files analysisFiles)

                            { flushedState with
                                AnalysisFiles = analysisFiles
                                CohortSeal = seal
                                AnalysisReceipt = if runnableProjects.IsEmpty then seal else None }

                        // ── DRAIN THE PENDING QUEUE ────────────────
                        // The cohort seal is the first moment this scan's symbols are
                        // known. `BuildCompleted` cannot be the only test trigger: on a
                        // scan it fires BEFORE the FCS pass, so scan-discovered symbols
                        // would never be verified by the run it launched and would
                        // accumulate silently while `check` reported a stale terminal
                        // status. If symbols remain unverified here, RUN the tests that
                        // verify them — a verdict is only ever earned by a run.
                        //
                        // The skip asks `nothingOwed`, not `Set.isEmpty`: an UNREADABLE
                        // ledger leaves the in-memory queue empty because we cannot name
                        // what it held, and reading that as "nothing to drain" lets a
                        // corrupt sidecar run ZERO tests and still go green.
                        // The tree these check results describe, for `DiffWait`: read after
                        // the fold, so bytes written since read as a moved tree.
                        let flushedState =
                            if flushedState.LastResults.IsSome then
                                { flushedState with
                                    SealedTree = ReceiptInputTree.read repoRoot }
                            else
                                flushedState

                        let flushedState = dischargeCoveredBootScan ctx batch flushedState

                        if nothingOwed flushedState.Debt then
                            if flushedState.AwaitingDiff.IsSome then
                                return
                                    launchHeld
                                        ctx
                                        flushedState
                                        "the check results it waited for are folded and owe nothing"
                            else
                                return flushedState
                        else
                            match testConfigs with
                            | Some configs when not configs.IsEmpty && onlyRetriesRed flushedState ->
                                logRedStands "BatchChecked" flushedState

                                return
                                    { flushedState with
                                        AwaitingDiff = None }
                            | Some configs when not configs.IsEmpty ->
                                // Drain UNCONDITIONALLY when the slot is free. Never defer
                                // to "the BuildCompleted that is surely coming": several
                                // `FileChanged` shapes never produce one at all
                                // (BuildPlugin ignores `SolutionChanged`, and DROPS a
                                // FileChanged arriving while a build is running), so the
                                // deferral can wait forever. CI caught exactly that — 3
                                // symbols deferred to a BuildCompleted that never came,
                                // zero tests run, exit 0.
                                //
                                // Draining on a half-built tree is safe: the
                                // apphost-freshness gate in `executeTests` refuses
                                // `--no-build` against an artifact older than its sources
                                // and defers that project WITHOUT spawning a test process,
                                // so the stale case costs a status flip, not a run.
                                //
                                // The claim is ATTEMPTED, not pre-checked: `RunExclusive`
                                // returns the outcome, so there is no TOCTOU between an
                                // `IsRunning` read and the launch.
                                let hasCachedResults = flushedState.LastResults.IsSome
                                let forceRunProjects = flushedState.PendingForceRunProjects

                                let drainedState =
                                    { flushedState with
                                        PendingForceRunProjects = Set.empty
                                        AwaitingDiff = None }

                                match launchImpactRun ctx configs drainedState hasCachedResults forceRunProjects with
                                | Some launched ->
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} — draining now"

                                    return launched
                                | None when joinsFullRun flushedState (batch.Trigger = BootScan) ->
                                    // The full-suite run in flight covers the built tree
                                    // this cohort describes, so it takes the debt instead of
                                    // a duplicate run. TestsFinished may discharge it only
                                    // from actual green full-suite evidence over an
                                    // unchanged input tree; failure, partial scope, or a
                                    // moved tree keeps the durable queue outstanding.
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} discovered during a full-suite run — attaching debt to that run"

                                    return
                                        attachToFullRun
                                            { flushedState with
                                                AwaitingDiff = None }
                                | None ->
                                    // A run is in flight but was launched against an older
                                    // queue snapshot, so it cannot clear these symbols.
                                    // Queue the rerun behind it as an intent. The pending
                                    // fanout is retained (the work was NOT consumed).
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription flushedState.Debt} still outstanding while a run is in flight — queueing re-run"

                                    enqueueImpactRun ctx

                                    return
                                        { flushedState with
                                            AwaitingDiff = None }
                            | _ ->
                                // Analysis-only (no test configs): nothing can verify
                                // these symbols, so there is nothing to drain.
                                return flushedState

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        // Record that BuildCompleted has fired this session, so subsequent
                        // FileChecked events may promote the freshness sidecar to clean.
                        // Set unconditionally across both the queued-rerun and run-now
                        // branches: the gate asks whether the build has realized the
                        // reference graph, not whether tests are queued.
                        let state =
                            { state with
                                BuildCompletedInThisSession = true }

                        // ── Dependency-fanout fingerprint (computed for EVERY
                        // build, before the running/idle split, so the prior
                        // fingerprint always advances and a mid-run dependency
                        // change is never lost). Fingerprint each test project from
                        // its referenced projects' non-symbol inputs + own package
                        // versions (DependencyFanout). A project whose fingerprint moved
                        // since the last build had a dependency/binary change the
                        // symbol diff can't see → force-run it. An empty graph
                        // (tests / no graph wired) yields no fanout, so the
                        // symbol-precise path is unchanged.
                        let fsprojByName =
                            ctx.ProjectGraph.GetAllProjects()
                            |> List.map (fun p -> Path.GetFileNameWithoutExtension p, p)
                            |> Map.ofList

                        let currentFingerprints =
                            match testConfigs with
                            | Some configs ->
                                let fingerprint = DependencyFanout.fingerprinter ctx.ProjectGraph

                                configs
                                |> List.choose (fun c ->
                                    Map.tryFind c.Project fsprojByName
                                    |> Option.map (fun fsproj -> c.Project, fsproj))
                                |> List.map (fun (name, fsproj) -> name, fingerprint fsproj)
                                |> Map.ofList
                            | None -> Map.empty

                        let fanoutNow =
                            DependencyFanout.changedProjects state.PriorProjectFingerprints currentFingerprints

                        if not (Set.isEmpty fanoutNow) then
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: %d{Set.count fanoutNow} test project(s) had a \
                                  dependency-fingerprint change — force-running: \
                                  %s{describeAll (Set.toList fanoutNow)}"
                        else
                            // Say that nothing fanned out, and say what was examined to
                            // reach that conclusion. "No dependency changed" and
                            // "fingerprinting never ran" otherwise produce identical
                            // logs, so an inert `computeProjectFingerprint` — the safety
                            // net against binary-only changes silently OFF — reads as a
                            // healthy quiet run. Zero fingerprints computed, or zero
                            // graph projects, means inert rather than clean.
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: none — \
                                  %d{currentFingerprints.Count} project fingerprint(s) computed, \
                                  %d{fsprojByName.Count} project(s) in the graph, \
                                  %d{state.PriorProjectFingerprints.Count} prior fingerprint(s) to compare against"

                        // Advance the baseline on every build; carry any not-yet-run
                        // fanout in the pending set (consumed by the queued rerun).
                        let state =
                            { state with
                                PriorProjectFingerprints =
                                    DependencyFanout.advance state.PriorProjectFingerprints currentFingerprints }

                        // Asked BEFORE selection: a held key refuses the claim, and
                        // selection is the expensive step (a flush and an impact query
                        // over the whole index), so selecting only to be refused is
                        // wasted work. A fold of a finished run holds the key as surely
                        // as a live run does. The claim below still handles `SlotBusy`
                        // for a key taken after this read.
                        let heldBy =
                            match ctx.SlotHolder "tests" with
                            | SlotHolder.Free -> None
                            | SlotHolder.LiveRun -> Some "tests already running"
                            | SlotHolder.Fold -> Some "tests key held by an uncommitted fold"

                        match heldBy with
                        | Some reason when joinsFullRun state false ->
                            // A pass-through full run takes what is owed: no run follows
                            // it. The fanout is kept for whatever launches next.
                            ctx.Log $"  ↳ attached to the full run (%s{reason})"

                            Logging.info
                                "test-prune"
                                $"BuildSucceeded received during a full-suite run (%s{reason}) — attaching debt to that run"

                            return
                                { attachToFullRun state with
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
                        | Some reason ->
                            // The leading two spaces nest this under the in-flight test
                            // run in the activity-fold `recent:` view (the renderer
                            // already indents every tail entry by 8), so it does not read
                            // as a sibling of the test-result lines.
                            ctx.Log $"  ↳ queued re-run (%s{reason})"

                            Logging.info
                                "test-prune"
                                $"BuildSucceeded received but %s{reason} — will re-run after, without selecting now"

                            // Stash the fanout so the rerun runs it (don't lose a
                            // mid-run dependency change).
                            enqueueImpactRun ctx

                            return
                                { state with
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
                        | None ->
                            Logging.info "test-prune" "BuildSucceeded: starting test run"

                            // Flush/query before announcing Running so the reported status never
                            // lies: announcing Running before the flush would flash Running even
                            // on a schema-drifted DB.
                            // The framework catches uncaught throws and forces Failed as a
                            // defense-in-depth net; we still trap locally here so we can run
                            // the schema-drift self-heal and preserve the idle transition.
                            match
                                (try
                                    Ok(flushAndQueryAffectedBounded ctx state)
                                 with ex ->
                                     Error ex)
                            with
                            | Error ex ->
                                Logging.error "test-prune" $"flushAndQueryAffected failed: %s{ex.Message}"
                                tryRepairSchemaDrift ex

                                ctx.ReportStatus(
                                    PluginStatus.failedNow ex.Message $"flush failed: %s{ex.Message}" TimeSpan.Zero
                                )

                                return state
                            | Ok stateWithAffected ->
                                match testConfigs with
                                | Some configs when not configs.IsEmpty ->
                                    let hasCachedResults = state.LastResults.IsSome

                                    // Only a launch with a session baseline can wait for the
                                    // diff, and whether it does reads the extensions'
                                    // failures. Without one, the run takes them itself.
                                    let stateWithAffected =
                                        if hasCachedResults then
                                            joinRefresh ctx stateWithAffected
                                        else
                                            stateWithAffected

                                    // Union this build's fanout with any pending
                                    // fanout deferred from a prior mid-run build,
                                    // then clear the pending set (it's being run).
                                    let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                                    let launchState =
                                        { stateWithAffected with
                                            PendingForceRunProjects = Set.empty }

                                    let launchInputs = TestRunInputs.ofState launchState

                                    let awaitsDiff =
                                        DiffWait.awaits
                                            { HasSessionBaseline = hasCachedResults
                                              HasModel = (observeModelGeneration ctx).IsSome
                                              OwesNothing = nothingOwed launchInputs.Debt
                                              RunsInFullAnyway =
                                                Set.unionMany
                                                    [ forceRunProjects
                                                      EarnedEvidence.wholeProjectGap
                                                          (observeModelGeneration ctx)
                                                          (ObservedModel.inputs ctx.ProjectGraph)
                                                          (fullSuiteProjects configs)
                                                          launchInputs.Earned ]
                                              RunsSomethingAnyway =
                                                not (
                                                    List.isEmpty launchInputs.OutstandingFailures
                                                    && Map.isEmpty launchInputs.UnanalyzableFiles
                                                    && Set.isEmpty launchInputs.Unindexed
                                                    && Map.isEmpty launchInputs.FailedExtensions
                                                )
                                              FoldedChanges =
                                                UncoveredChanges.isAll launchInputs.ChangedSymbolsAllUncovered
                                              Scope = launchScopeOf launchInputs
                                              EvidenceTree = launchInputs.EvidenceTree
                                              CurrentTree = ReceiptInputTree.read repoRoot
                                              SealedTree = launchState.SealedTree }

                                    if awaitsDiff then
                                        return holdForDiff ctx launchState
                                    else
                                        match
                                            launchImpactRun ctx configs launchState hasCachedResults forceRunProjects
                                        with
                                        | Some launched -> return launched
                                        | None when joinsFullRun stateWithAffected false ->
                                            // The key is held by a pass-through full run whose
                                            // result is not folded yet: it takes the debt.
                                            Logging.info
                                                "test-prune"
                                                "BuildSucceeded: tests slot held by a full-suite run — attaching debt to that run"

                                            return
                                                { attachToFullRun stateWithAffected with
                                                    PendingForceRunProjects = forceRunProjects }
                                        | None ->
                                            // The key is held without a live run: a result fold
                                            // or an intent. Same treatment: queue the rerun behind
                                            // it, retain the un-consumed fanout.
                                            Logging.info
                                                "test-prune"
                                                "BuildSucceeded: tests slot already held — queueing re-run"

                                            enqueueImpactRun ctx

                                            return
                                                { stateWithAffected with
                                                    PendingForceRunProjects = forceRunProjects }
                                | _ ->
                                    // No test configs — flush only; nothing to run.
                                    return stateWithAffected
                    | BuildFailed _ -> return state

                | Custom(TestsFinished(started, launchedCompletion, launchedAs) as message)
                | Custom(CommandTestsFinished(started, launchedCompletion, launchedAs, _, _) as message) ->
                    // A completion that lands while the model is being re-discovered has
                    // nothing to be compared against yet: wait for the re-discovery to
                    // settle, so it is carried or revoked per project instead of whole.
                    let! waited = ObservedModel.awaitSettled ctx.ProjectGraph launchedAs.ModelGeneration

                    if waited > TimeSpan.Zero then
                        let waitedRun = launchedCompletion.RunId.ToString("N")

                        Logging.info
                            "test-prune"
                            $"Run %s{waitedRun} completed while the project model was being re-discovered; waited %.1f{waited.TotalSeconds}s for it to settle before folding"

                    // A model replaced while the run was in flight takes back only the
                    // results of the projects whose compile inputs it changed.
                    let modelCarry, completed, launch =
                        acrossModelChange
                            (observeModelGeneration ctx)
                            (ObservedModel.inputs ctx.ProjectGraph)
                            launchedCompletion
                            launchedAs

                    let runText = launchedCompletion.RunId.ToString("N")
                    let launchedText = ObservedModel.describe launchedAs.ModelGeneration
                    let nowText = ObservedModel.describe (observeModelGeneration ctx)

                    match modelCarry with
                    | ModelCarry.SameModel -> ()
                    | ModelCarry.Carried ->
                        Logging.info
                            "test-prune"
                            $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, but no test project's compile inputs changed — its results stand"
                    | ModelCarry.Revoked changed ->
                        Logging.info
                            "test-prune"
                            $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran and changed the compile inputs of %s{describeAll (Set.toList changed)} — their results are revoked; the others stand"
                    | ModelCarry.Unprovable ->
                        Logging.info
                            "test-prune"
                            $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, and no per-project compile-input identities can show what changed — the whole run is revoked"

                    // The declarations this completion retires debt under, resolved ONCE and
                    // BEFORE any side effect. A resolution that throws (an ambiguous or
                    // unobserved excluded project) fails this handler before it has emitted,
                    // recorded or committed anything, so the framework settles the work as a
                    // failure and every symbol stays owed.
                    // Every symbol this completion can ask about: what the run launched,
                    // what was attached to it, and what is queued. One grouped query,
                    // and only if something asks.
                    let covering =
                        coveringOf (
                            Seq.concat
                                [ Set.toSeq launch.Symbols
                                  Map.keys state.DebtDuringFullRun
                                  Set.toSeq state.Debt.PendingQueue ]
                        )

                    let excluded =
                        if Set.isEmpty runnableProjects then
                            lazy (resolveExcludedProjects ())
                        else
                            Lazy<_>.CreateFromValue(resolveExcludedProjects ())

                    let owedTo = debtScope excluded covering

                    // Whether the index names a test project no run here can discharge:
                    // neither configured nor declared excluded. If not, no symbol can be
                    // owed elsewhere, so nothing needs asking per symbol. One scan of the
                    // index, not a covering query, and only if something asks. An index
                    // that cannot be scanned may name one.
                    let noUnrunnableCoverers = lazy (indexNamesOnlyRunnable excluded)

                    // Emit the lifecycle events synchronously here, inside the framework's
                    // per-event capture window, so they land in the cached EmittedEvents
                    // and re-fire on cache replay — subscribers that key off
                    // TestRunCompleted (FileCommandPlugin) must see it on a hit.
                    ctx.EmitTestRunCompleted completed

                    // This run joins the session ledger HERE, so every state this fold
                    // returns carries it. A run that completed is a run whose directory a
                    // reader may need, whatever the handler goes on to decide about its
                    // results.
                    //
                    // Except a skip: a launch that selected nothing and ran nothing wrote
                    // no directory, and counting it tells a reader the tests ran twice.
                    let skipped =
                        launch.ZeroSelection <> ZeroSelection.NotAZero && Map.isEmpty completed.Results

                    let completedRuns =
                        if skipped then
                            state.CompletedRuns
                        else
                            completed.RunId
                            :: (state.CompletedRuns
                                |> List.filter (fun id -> id <> completed.RunId)
                                |> List.truncate (SessionRunLedger - 1))

                    // Apply error reporting synchronously here too — live emission from
                    // the async wouldn't be captured for cache replay.
                    let testResults: TestResults =
                        { Results = completed.Results
                          Elapsed = completed.TotalElapsed }

                    // A run may clear ONLY what it COVERED.
                    //
                    // Two properties must hold together: the ledger is REWRITTEN each
                    // cycle (so a superseded red cannot linger), and it is
                    // rewritten from the OUTSTANDING set — what this run found PLUS every
                    // earlier red it did not cover. Rewriting from this run's failures
                    // alone lets a narrower run's green erase reds it never executed.
                    // Coverage comes from the run's own launch selection, so a red dies
                    // only to evidence that executed it, and dies the moment that evidence
                    // exists (no stuck-red).
                    //
                    // The launch selection cannot express the reach of a raw `--filter`
                    // passthrough — it records every project as `ProjectInFull` — so hand
                    // `ofRun` the classes the run's OWN report shows passing.
                    // Read from THIS run's directory only, and empty
                    // whenever the report is missing or incomplete.
                    let coverage =
                        RunCoverage.ofRun
                            launch.Selection
                            completed.Results
                            (passedClassesOfRun repoRoot completed.RunId)

                    let passedTests = passedTestsOfRun repoRoot completed.RunId

                    // The run log a project-level cause points at. Named
                    // only when it is on disk: `Written` is otherwise reserved for the
                    // code holding the open handle, and a message must never point at
                    // a log nobody wrote.
                    let runLogOf (project: string) : RunLog.Ref =
                        let path = RunLog.pathIn (Ctrf.runDir repoRoot completed.RunId) project

                        if File.Exists path then
                            RunLog.Ref.Written path
                        else
                            RunLog.Ref.Unavailable "no output log is on disk for this run"

                    // The failed rows and declared classes of the CTRF report each
                    // project wrote in this run's directory, for naming a red the console
                    // line did not and for splitting every red's name.
                    let reportOf =
                        let reports =
                            Ctrf.reportsForRun repoRoot completed.RunId
                            |> List.map (fun report -> report.Project, report.Path)
                            |> Map.ofList

                        fun (project: string) ->
                            match Map.tryFind project reports with
                            | None -> ReportedFailures.none
                            | Some path ->
                                try
                                    ReportedFailures.ofReport (File.ReadAllText path)
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException -> ReportedFailures.none

                    let foundFailures = failuresOf runLogOf reportOf state.TestClassFiles testResults

                    let failedTests = failedTestsOfRun repoRoot completed.RunId testResults

                    let checkReach, conditionalFailureRecall =
                        failedTests |> CheckReach.classifyEvidence launch.WouldHaveRun

                    // Shadow trace selection: store the launch's comparison with this
                    // run's failed tests. A run with no complete report judges no miss.
                    match traces with
                    | Some wiring when not launch.TraceShadow.IsEmpty ->
                        let failedKeys =
                            match failedTests with
                            | Ok failures ->
                                failures
                                |> List.choose (fun f ->
                                    match f.Class, f.Method with
                                    | Some cls, Some meth -> Some(TraceShadow.failureKey f.Project cls meth)
                                    | _ -> None)
                            | Error _ -> []

                        TraceShadow.atCompletion
                            wiring.Policy
                            repoRoot
                            launch.TraceShadow
                            (completed.RunId.ToString "N")
                            failedKeys
                            (Logging.info "test-prune")
                    | _ -> ()

                    // Classified HERE, against THIS run's failures and the
                    // selection retained at its launch. Nothing extra runs and nothing is
                    // re-read: both inputs are already in hand.

                    // A prior red naming a class this run selected, in a project that then
                    // passed with a complete report holding no row of that class, names no
                    // test the project has. No pass can ever retire it, so it goes now.
                    let absentFailures, priorFailures =
                        OutstandingFailure.partitionAbsent
                            coverage
                            (classesRunOfPassedProjects repoRoot completed.RunId completed.Results)
                            state.OutstandingFailures

                    if not absentFailures.IsEmpty then
                        Logging.info
                            "test-prune"
                            $"%d{absentFailures.Length} failure(s) from an earlier run name a test class this run selected and its project does not have (%s{OutstandingFailure.summarize absentFailures}) — retired, since no run can ever execute them"

                    let carriedFailures =
                        OutstandingFailure.carriedOver runnableProjects coverage passedTests priorFailures

                    let outstandingFailures =
                        OutstandingFailure.carry runnableProjects coverage passedTests foundFailures priorFailures

                    if not carriedFailures.IsEmpty then
                        Logging.info
                            "test-prune"
                            $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

                    // Discharged HERE, immediately before the ledger
                    // rewrite, because this is the one place the outstanding set is
                    // recomputed — pruning anywhere else would leave the ledger and the
                    // state disagreeing about what is still owed.
                    let unanalyzable = pruneDeletedUnanalyzable File.Exists state.UnanalyzableFiles

                    // The ONLY path to the ledger: clear the slate, re-report the whole
                    // outstanding set. There is no wholesale clear a filtered run can
                    // reach for.
                    reportOutstanding ctx unanalyzable state.FailedExtensions outstandingFailures

                    // `LastCoverage` is the moment the process acquires test evidence — a
                    // run completed and we know what it covered. Until a state carrying it
                    // is supplied, the cache key refuses to let a cached BuildCompleted
                    // assert a result this process never ran.
                    let debtDuringFullRun = state.DebtDuringFullRun

                    let currentTree = ReceiptInputTree.readTree repoRoot
                    let currentInputTree = currentTree |> Option.map _.Identity
                    let currentModelGeneration = observeModelGeneration ctx
                    let currentInputs = ObservedModel.inputs ctx.ProjectGraph

                    // A typed transition, not a candidate-then-guard: see
                    // `ReceiptTransition`. `Noop` and `Narrower` keep whatever was earned,
                    // `Revoked` clears it, and only `Earned` can write.
                    let writtenByTestRun =
                        ReceiptInputTree.writtenByTestRun
                            repoRoot
                            (ReceiptInputTree.watcherObserves
                                repoRoot
                                (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd))
                            (state.InFlight |> Option.map (fun run -> run.WatcherReported))
                            launch.TestHostStartedAt
                            launch.InputTree
                            currentTree

                    // The evidence the standing receipt's own run earned, under the
                    // current model. A completion that keeps the receipt (`Noop`,
                    // `Narrower`) publishes exactly this, so the run `test-scope` grades
                    // and the run the published evidence names are one run.
                    let standingEvidence =
                        state.EvidenceReceipt
                        |> Option.bind (fun receipt ->
                            EarnedEvidence.retainedForRun
                                currentModelGeneration
                                currentInputs
                                receipt.RunId
                                state.Earned)

                    let receiptTransition =
                        ReceiptTransition.classify
                            (Set.toList runnableProjects)
                            state.EvidenceReceipt
                            standingEvidence
                            currentInputTree
                            currentModelGeneration
                            currentInputs
                            writtenByTestRun
                            launch
                            completed
                            coverage

                    match receiptTransition with
                    | ReceiptTransition.Revoked reason ->
                        ctx.Log $"  ↳ test evidence receipt revoked: %s{reason}"

                        // WHICH paths, so a revocation with no watcher event behind it
                        // (a gitignored build output under a discovery root, say) is
                        // answered by this line rather than by an afternoon of ctimes.
                        match ReceiptInputTree.movedPaths launch.InputTree currentTree with
                        | Some paths -> ctx.Log $"    moved paths: %s{paths}"
                        | None -> ()
                    | ReceiptTransition.Earned _
                    | ReceiptTransition.Noop
                    | ReceiptTransition.Narrower -> ()

                    let evidenceReceipt =
                        ReceiptTransition.apply state.EvidenceReceipt receiptTransition

                    let state =
                        { state with
                            CompletedRuns = completedRuns
                            CheckReaches =
                                (completed.RunId, launch.WouldHaveRun, checkReach, conditionalFailureRecall)
                                :: (state.CheckReaches
                                    |> List.filter (fun (id, _, _, _) -> id <> completed.RunId)
                                    |> List.truncate (SessionRunLedger - 1))
                            OutstandingFailures = outstandingFailures
                            LastCoverage = coverage
                            LastZeroSelection = launch.ZeroSelection
                            Receipt = ReceiptTransition.hold state.Receipt receiptTransition currentTree
                            // Debt is scoped to exactly the run that was active when the
                            // BootScan cohort sealed. Failure keeps it durable, but must not
                            // let a later unrelated run claim it implicitly.
                            DebtDuringFullRun = Map.empty
                            InFlight = None
                            // A conclusion only when the run executed to a red over the
                            // tree and model it launched under (`onlyRetriesRed`).
                            LastRed =
                                match completed.Outcome, launch.InputTreeHash, currentInputTree with
                                | Normal, Some launched, Some current when
                                    launched = current
                                    && launch.ModelGeneration = currentModelGeneration
                                    && completed.Results
                                       |> Map.exists (fun _ result -> TestResult.verdict result = Refuted)
                                    ->
                                    Some
                                        { SymbolRevisions = launch.SymbolRevisions
                                          Tree = current }
                                | _ -> None
                            Mode =
                                (endRun
                                    (completed.Results
                                     |> Map.exists (fun _ result -> TestResult.executedTests result))
                                    state)
                                    .Mode
                            // Carried with them: the pruned map is what the ledger was
                            // just written from, so the next run's coarse-fallback
                            // widening reads the same set the user was shown.
                            UnanalyzableFiles = unanalyzable }


                    // Outcome-conditional, per-project green-commit. A launched
                    // symbol leaves the needs-testing queue ONLY when the run
                    // genuinely covered it green:
                    //   - the run did NOT abort (a beforeRun throw / crash gives
                    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
                    //   - EVERY project covering the symbol produced a PASSED result.
                    // A project counts as passed only if it appears in completed.Results
                    // with `TestResult.verifiedGreen` — a covering project ABSENT from the
                    // results (didn't run this cycle) blocks the commit (we can't claim
                    // it green). A symbol with NO covering project was already dropped at
                    // flush time, but if one slipped through it commits here (nothing to
                    // wait on). Genuine in-session mid-run arrivals are NOT in
                    // launch.Symbols, and a launched symbol edited again during the run is
                    // at a newer revision than the launch captured: both stay queued and
                    // the queued impact run re-runs them. BootScan debt may join the candidate set only when
                    // this completion proves the run was actually full-suite.
                    //
                    // This fold read `TestResult.isPassed`, which was TRUE
                    // for `TestsNoMatch` — so a symbol whose covering project ran under an
                    // impact-derived class filter that matched ZERO tests had its test debt
                    // DISCHARGED by a project that executed nothing, and left
                    // pending-verification.json unverified. `verifiedGreen` is `Verified`
                    // only, so a project that ran nothing can no longer retire anything.
                    // A run selected under a model this host has since replaced verified
                    // that model, not the current one: it discharges nothing, like an abort.
                    let aborted =
                        launch.ModelGeneration <> currentModelGeneration
                        || match completed.Outcome with
                           | Aborted _ -> true
                           | Normal -> false

                    let projectPassed (proj: string) =
                        match Map.tryFind proj completed.Results with
                        | Some r -> TestResult.verifiedGreen r
                        | None -> false

                    // A run that executed every runnable project in full, over the input tree
                    // it launched against, and passed all of them, covered every symbol that
                    // tree holds: there is nothing left to ask per symbol, unless the index
                    // names a coverer this daemon neither runs nor declares excluded.
                    let coveredByConstruction =
                        not aborted
                        && not (Set.isEmpty runnableProjects)
                        && completed.Verification = Ran FullSuite
                        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
                        && runnableProjects |> Set.forall projectPassed
                        && noUnrunnableCoverers.Value

                    let committedSymbols =
                        if aborted then
                            Set.empty
                        else
                            // An attached symbol may borrow this run only when the run was
                            // actually full, over the input tree it launched against, and the
                            // symbol is still at the revision captured when it attached.
                            let attachedCandidates =
                                match completed.Verification with
                                | Ran FullSuite when ReceiptInputTree.matches launch.InputTreeHash currentInputTree ->
                                    debtDuringFullRun
                                    |> Map.filter (fun symbol captured -> revisionOf state.Debt symbol = captured)
                                    |> Map.keys
                                    |> Set.ofSeq
                                | _ -> Set.empty

                            let launchedCurrent =
                                launch.Symbols
                                |> Set.filter (fun symbol ->
                                    revisionOf state.Debt symbol = (Map.tryFind symbol launch.SymbolRevisions
                                                                    |> Option.defaultValue 0L))

                            let candidates = Set.union launchedCurrent attachedCandidates

                            if coveredByConstruction then
                                candidates
                            else
                                candidates
                                |> Set.filter (fun s ->
                                    match Map.tryFind s launch.CoveringProjectsBySymbol with
                                    | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                                    | Some _ -> true
                                    | None -> owedTo s |> Set.forall projectPassed)

                    if not (Set.isEmpty committedSymbols) then
                        Logging.info
                            "test-prune"
                            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

                    let debt = commitPending committedSymbols state.Debt

                    if not aborted && not (Map.isEmpty launch.RuntimeProjectsByFile) then
                        for KeyValue(file, launchedProjects) in launch.RuntimeProjectsByFile do
                            let passed = launchedProjects |> Map.keys |> Seq.filter projectPassed |> Set.ofSeq

                            if not (Set.isEmpty passed) then
                                let projectNames = passed |> Set.toList |> String.concat ", "

                                Logging.info "test-prune" $"runtime coverage verified %s{file} by %s{projectNames}"

                    // A run covered by construction discharges every obligation owed now,
                    // not only those it launched with: what attached to it arrived over the
                    // tree it ran.
                    let retiredRuntime =
                        if coveredByConstruction then
                            debt.RuntimeObligations
                        else
                            launch.RuntimeProjectsByFile

                    let debt =
                        if aborted || Map.isEmpty retiredRuntime then
                            debt
                        else
                            { debt with
                                RuntimeObligations =
                                    retireRuntimeCoverageObligations
                                        debt.RuntimeObligations
                                        retiredRuntime
                                        projectPassed }

                    // A project this run executed UNFILTERED and passed, over the input
                    // tree it launched against, ran every test a trigger raised for it
                    // during the run could ask for: a scan re-deriving its runtime
                    // obligation (a newer generation over the same bytes) or a dependency
                    // fanout from a build of that tree. Those triggers are absorbed here,
                    // per project, as `coveredByConstruction` absorbs them for the whole
                    // suite; otherwise the queued re-run executes the project in full
                    // again. A filtered or reused project, a red, an abort, or a tree that
                    // moved since launch absorbs nothing.
                    let absorbedProjects =
                        if aborted || not (ReceiptInputTree.matches launch.InputTreeHash currentInputTree) then
                            Set.empty
                        else
                            launch.Selection
                            |> Map.filter (fun project selection ->
                                selection = ProjectInFull
                                && not (Map.containsKey project launch.Reused)
                                && projectPassed project)
                            |> Map.keys
                            |> Set.ofSeq

                    let debt =
                        if Set.isEmpty absorbedProjects then
                            debt
                        else
                            { debt with
                                RuntimeObligations =
                                    debt.RuntimeObligations
                                    |> Map.map (fun _ projects ->
                                        projects
                                        |> Map.filter (fun project _ -> not (Set.contains project absorbedProjects)))
                                    |> Map.filter (fun _ projects -> not (Map.isEmpty projects)) }

                    // A project whose runtime coverage did not ingest is paid by a run that executed
                    // it in full and passed: a pass means its coverage ingested this time (a run
                    // that could not ingest it reports the project errored, never passed). A
                    // filtered or reused result, a red or an abort pays nothing.
                    let ingested =
                        if aborted then
                            Set.empty
                        else
                            debt.CoverageNotIngested
                            |> Map.keys
                            |> Seq.filter (fun project ->
                                Map.tryFind project launch.Selection = Some ProjectInFull
                                && not (Map.containsKey project launch.Reused)
                                && projectPassed project)
                            |> Set.ofSeq

                    if not (Set.isEmpty ingested) then
                        let names = ingested |> Set.toList |> String.concat ", "

                        Logging.info
                            "test-prune"
                            $"Runtime coverage ingested again for %s{names} after a whole-project run passed; no longer owed"

                    let debt =
                        if Set.isEmpty ingested then
                            debt
                        else
                            { debt with
                                CoverageNotIngested =
                                    debt.CoverageNotIngested
                                    |> Map.filter (fun project _ -> not (Set.contains project ingested)) }

                    // Discharge an UNREADABLE ledger's debt.
                    //
                    // The debt is owed in FULL, because its membership is unknown: the only
                    // run that can retire it is one that executed EVERY runnable project,
                    // unfiltered, and passed. At that point every symbol the lost ledger
                    // could possibly have held has been verified by an actual test run, so
                    // there is nothing left for it to owe.
                    //
                    // Each conjunct is load-bearing:
                    //  * `not aborted` — an aborted run has empty Results and verified nothing.
                    //  * every RUNNABLE project passed — `projectPassed` demands the project
                    //    be PRESENT in the results AND green, so a project that never ran
                    //    cannot be counted.
                    //  * `Ran FullSuite` — the run EXECUTED and none of it was
                    //    impact-FILTERED. A case, not a bool: scope is unreachable unless
                    //    something ran, so emptiness needs no separate check.
                    //  * a non-empty `runnableProjects` — an analysis-only daemon runs no
                    //    tests, so it can never prove anything and must not discharge. It
                    //    asks about the SELECTION, not the results.
                    //
                    // Only a state that has discharged it rewrites the ledger: `PrepareCommit`
                    // leaves the corrupt file untouched while the debt is unknown, so that a
                    // crash mid-recovery leaves the next session the same honest "unknown"
                    // rather than a clean, empty, WRONG ledger.
                    let executedFullSuite =
                        not aborted
                        && not (Set.isEmpty runnableProjects)
                        && completed.Verification = Ran FullSuite

                    let recovers =
                        debt.RecoveryOutstanding
                        && executedFullSuite
                        && runnableProjects |> Set.forall projectPassed

                    // Obligations this run launched were retired above; any left are newer
                    // than the run and stay owed.
                    if recovers then
                        let causes = describeUnknownDebt debt.UnknownDebt

                        Logging.info
                            "test-prune"
                            $"A full suite passed every configured project — the unknown verification debt is discharged (%s{causes}), and the ledgers are rewritten from this run. Impact filtering resumes."

                    let debt = if recovers then { debt with UnknownDebt = [] } else debt

                    // The full-suite WATERMARK. Written when a full-suite
                    // run has ACCOUNTED for every configured project: passed, or red with
                    // the red recorded in the durable outstanding list. Not only for a
                    // green run — a red full suite still proves what every other test did,
                    // and its reds are quarantined until they pass — so the inner loop can
                    // resume impact filtering (plus quarantine) after one full run rather
                    // than running the whole suite until the last red is fixed. A project
                    // that produced no accountable outcome (deferred, errored) is neither,
                    // and `Ran FullSuite` is already false for it.
                    //
                    // The same `executedFullSuite` the unreadable-ledger discharge reads: two
                    // readings of what a full-suite run is would let one recover a ledger
                    // the other refused to baseline.
                    let accountedFor (proj: string) =
                        projectPassed proj
                        || outstandingFailures |> List.exists (fun f -> f.Project = proj)

                    // A run that reused evidence ran less than the suite here; its greens
                    // stand, but a full-suite baseline is only ever earned by running.
                    let earnsBaseline =
                        executedFullSuite
                        && Map.isEmpty launch.Reused
                        && runnableProjects |> Set.forall accountedFor

                    let debt =
                        if earnsBaseline then
                            { debt with
                                Baseline =
                                    Some
                                        { RunId = completed.RunId
                                          EarnedAt = DateTime.UtcNow
                                          Projects = runnableProjects } }
                        else
                            debt

                    // What this completion EARNED. What it left OWED is a refusal it
                    // carries: a proof that records a red is still a proof of what ran.
                    //
                    // Owed is what THIS run did not cover, not everything the queue holds
                    // at this instant. A cold tree queues obligations while the run is
                    // already executing — the build fires the full suite first and the
                    // scan's file events land during it — so they are absent from the
                    // launch snapshot and survive the launch-scoped retirement above. A run
                    // that executed every runnable project IN FULL and passed, over the
                    // tree it launched against, covered those files whenever they were
                    // queued; refusing them turned a green check into exit 2 with nothing
                    // failing anywhere. A run that did not (a project absent, failed, or
                    // filtered) covers nothing extra, and every one of them still refuses.
                    let coveredEverythingRunnable =
                        executedFullSuite
                        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
                        && runnableProjects |> Set.forall projectPassed

                    let owedSymbols =
                        if coveredEverythingRunnable then
                            Set.empty
                        else
                            debt.PendingQueue

                    let owedRuntimeObligations =
                        if coveredEverythingRunnable then
                            0
                        else
                            debt.RuntimeObligations |> Map.values |> Seq.sumBy Map.count

                    let pendingObligations =
                        Set.count owedSymbols
                        + owedRuntimeObligations
                        + unanalyzable.Count
                        + outstandingFailures.Length
                        + Map.count debt.CoverageNotIngested
                        + (if debt.RecoveryOutstanding then 1 else 0)

                    // A completion that leaves the receipt standing — a narrower run, or a
                    // quiet one that found the tree already verified — leaves the evidence
                    // THAT receipt's run earned standing with it: the verdict grades the
                    // receipt's run and looks its evidence up by run id. Under a model,
                    // `classify` keeps a receipt only when this evidence is held.
                    //
                    // Including a quiet completion that folds while an obligation is owed
                    // (symbols a scan delivered while it was in flight). It used to publish
                    // an evidence of its own, refusing for the obligation, beside the
                    // receipt it kept: the two named different runs, and the narrower run
                    // that then discharged the obligation found no evidence for the
                    // receipt's run to keep and published its own, so `check` graded the
                    // full suite and found nothing vouching for it.
                    //
                    // A receipt that covers the whole suite on the tree it is bound to has
                    // run every test that tree has, so an obligation owed on that same tree
                    // is one it already verified, as it is after a narrower run. A
                    // narrower receipt cannot say so: its evidence refuses for what is owed.
                    let keptWithReceipt =
                        match receiptTransition, evidenceReceipt with
                        | ReceiptTransition.Noop, Some receipt when
                            pendingObligations > 0
                            && not (RunCoverage.coversWholeSuite (Set.toList runnableProjects) receipt.Coverage)
                            ->
                            standingEvidence
                            |> Option.map (
                                EarnedEvidence.withRefusals
                                    [ $"%d{pendingObligations} verification obligation(s) remain pending" ]
                            )
                        | ReceiptTransition.Noop, _
                        | ReceiptTransition.Narrower, _ -> standingEvidence
                        | ReceiptTransition.Earned _, _
                        | ReceiptTransition.Revoked _, _ -> None

                    let earned =
                        match keptWithReceipt, completed.Verification with
                        | Some kept, _ -> Some kept
                        | None, NoProjectsSelected when
                            pendingObligations = 0
                            && not aborted
                            && launch.ZeroSelection <> ZeroSelection.NotAZero
                            && launch.ModelGeneration = currentModelGeneration
                            ->
                            // Nothing needed running, so this completion proves nothing new.
                            // The evidence it was already verified by stands, while it belongs
                            // to the current model.
                            EarnedEvidence.retainedForZeroSelection currentModelGeneration currentInputs state.Earned
                        | None, NoProjectsSelected
                        | None, AllZeroMatch _
                        | None, NothingExecuted
                        | None, Ran _ ->
                            EarnedEvidence.fromCompletion
                                started.RunId
                                launch.ModelGeneration
                                currentModelGeneration
                                currentInputs
                                (ObservedModel.projectFiles ctx.ProjectGraph)
                                runnableProjects
                                pendingObligations
                                (TestPruneState.coverageEvidence state)
                                completed

                    Logging.info
                        "test-prune"
                        (describeCompletionEvidence
                            completed.RunId
                            launch.ModelGeneration
                            currentModelGeneration
                            receiptTransition
                            evidenceReceipt
                            earned)

                    match receiptEvidenceMismatch currentModelGeneration evidenceReceipt earned with
                    | Some mismatch -> Logging.warn "test-prune" mismatch
                    | None -> ()

                    let state =
                        { state with
                            Debt = debt
                            Earned = earned
                            PendingForceRunProjects = Set.difference state.PendingForceRunProjects absorbedProjects
                            CoveredFullSuite =
                                match launch.InputTree with
                                | Some tree when coveredByConstruction -> Some(tree, currentModelGeneration)
                                | _ -> None }

                    // `PrepareCommit` writes no sidecar while what is owed is unknown, so a
                    // baseline earned then lives only in this daemon's memory. Saying it was
                    // recorded would claim a write that never happens.
                    if earnsBaseline then
                        let runId = completed.RunId.ToString("N")

                        if debt.RecoveryOutstanding then
                            Logging.info
                                "test-prune"
                                $"Full-suite baseline earned by run %s{runId} but not written: what is owed is still UNKNOWN (%s{describeUnknownDebt debt.UnknownDebt}), and nothing is written until a full suite passes every configured project. Until then every run, in this daemon and the next, is the full suite."
                        else
                            Logging.info
                                "test-prune"
                                $"Full-suite baseline recorded: run %s{runId} accounted for every configured project (%d{Set.count runnableProjects}); impact-filtered greens are now relative to it"

                    // The in-memory hot view must shed ONLY the committed symbols,
                    // never the whole list — symbols left in the queue (mid-run
                    // arrivals, projects that failed/aborted) must keep selecting
                    // tests until a covering run passes. `queueAfterCommit` is the
                    // post-commit queue; it drives the cleared ChangedSymbols.
                    let queueAfterCommit = debt.PendingQueue
                    let remainingChangedSymbols = queueAfterCommit |> Set.toList

                    // Why the queue is still non-empty, in words. Symbols owed to projects
                    // this daemon cannot run are not "waiting on build": no build will
                    // discharge them, so the message names the projects and the way out.
                    // A function: it queries each queued symbol, and only non-green
                    // branches ask.
                    let pendingDescription () =
                        let elsewhere =
                            if noUnrunnableCoverers.Value then
                                Set.empty
                            else
                                owedElsewhere owedTo queueAfterCommit

                        match Set.toList elsewhere with
                        | [] -> $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)"
                        | projects ->
                            let names = projects |> String.concat ", "

                            $"%d{Set.count queueAfterCommit} symbol(s) still owed to tests in %s{names}, which this \
                              daemon does not run: list them in `tests.projects`, or declare them in \
                              `tests.excluded` with a reason"

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both rerun and final-idle branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        // Reds this run did not cover are still RED —
                        // they must deny it a green verdict exactly as its own failures
                        // would, or `check` exits 0 with a failing test outstanding
                        // (the whole defect). Named in every non-green message below so
                        // the reason a passing run is not green is never a mystery.
                        let carriedCount = carriedFailures.Length

                        let carriedNote =
                            if carriedCount = 0 then
                                ""
                            else
                                $" (+%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures})"

                        // Consult `completed.Outcome` FIRST. An Aborted run (beforeRun
                        // threw, runner crashed, run cancelled) must be non-green
                        // regardless of result counts — empty results trivially satisfy
                        // "failed = 0 && deferred = 0" and would otherwise false-green.
                        // Likewise a run that executed ZERO projects while the pending
                        // queue still holds symbols verified nothing, so it takes the
                        // honest "waiting on build (tests did not run)" path.
                        let abortMessage =
                            match completed.Outcome with
                            | Aborted reason -> Some reason
                            | Normal -> None

                        match abortMessage with
                        | Some reason ->
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"test run aborted (tests did not run): %s{reason}%s{carriedNote}"
                                    $"test run aborted: %s{reason}%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
                            // Zero projects executed but symbols still await
                            // verification — honest non-green, same wording/path as a
                            // deferred (never-ran) project.
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%s{pendingDescription ()}%s{carriedNote}"
                                    $"0 projects ran; symbols still awaiting verification%s{carriedNote}"
                                    results.Elapsed
                            )
                        // Projects RAN and every one matched zero tests, so
                        // nothing executed and nothing was verified — not a green. The
                        // ladder below counts zero matches OUT of both `passed` and
                        // `failed`, so without this check `failed = 0 && deferred = 0`
                        // holds, the green branch fires, and it reports "0 passed, 0
                        // failed in N projects" about N projects that ran no test at all.
                        //
                        // Scoped to ALL projects deliberately: a zero match in ONE project
                        // is a correct pass for it (an impact selection naming no class in
                        // the Integration project must not fail that project). Only the
                        // run-level verdict changes, and only when nothing matched
                        // anywhere — a mis-aimed filter, never a verified pass. An
                        // empty-results run stays on its existing "nothing to verify" path
                        // because `Map.forall` is vacuously true for an empty map.
                        | None when allZeroMatchOf results.Results ->
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%d{total} project(s) ran and matched ZERO tests — nothing was verified (not a pass)%s{carriedNote}"
                                    $"%d{total} project(s) discovered their tests; the active filter matched none of them, so no test executed%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None ->

                            // Non-green = anything this run did not verify green, MINUS
                            // the zero matches (counted separately below). Split further
                            // into genuine failures vs deferred (never-ran) so the verdict
                            // can be honest: deferred is non-green but is "could not run /
                            // waiting on build", NOT "failed".
                            //
                            // Written out per case, at the site. `not verifiedGreen`
                            // would sweep the zero matches in here and report them as
                            // failures; matching only `Refuted` would drop the
                            // Deferred/Errored projects, which ARE non-green — they owed
                            // a result and produced none.
                            //
                            // Per result, this match is the exact negation of
                            // `noProjectRefutedOrUnrun` in `cacheKeyFor`, kept in agreement
                            // BY HAND — see that site for why a shared helper would not
                            // enforce it (the conditions around the two differ).
                            let nonGreen =
                                results.Results
                                |> Map.toList
                                |> List.filter (fun (_, r) ->
                                    match TestResult.verdict r with
                                    | Verified -> false
                                    | Refuted -> true
                                    | NothingVerified -> not (TestResult.isNoMatch r))

                            let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

                            // An ABORTED project — its test host died
                            // mid-run — is counted OUT of `failed`, which used to sweep
                            // it in (`not isDeferred`). Nothing failed there: the runner
                            // never finished asking. Reporting it as "N failed: X" is a
                            // non-result rendered as a definite negative, and it is the
                            // exact shape a reader mistakes for a mass regression.
                            let abortedList = nonGreen |> List.filter (fun (_, r) -> TestResult.isErrored r)

                            let failedList =
                                nonGreen
                                |> List.filter (fun (_, r) ->
                                    not (TestResult.isDeferred r) && not (TestResult.isErrored r))

                            let failed = failedList.Length
                            let deferred = deferredList.Length
                            let aborted = abortedList.Length

                            // Zero-match projects are counted OUT of `passed`. `passed` is
                            // derived by exclusion, and the `nonGreen` fold above
                            // deliberately excludes them (a zero match is not a failure to
                            // report), so without this subtraction a project that executed
                            // no test is reported as one that passed — and the status line
                            // then disagrees with the CLI, which counts them separately.
                            let noMatch =
                                results.Results |> Map.filter (fun _ r -> TestResult.isNoMatch r) |> Map.count

                            let passed = total - failed - deferred - aborted - noMatch

                            let noMatchSuffix = if noMatch = 0 then "" else $", %d{noMatch} matched nothing"

                            let anyFiltered =
                                results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                            let selectedSuffix = if anyFiltered then "yes" else "no"

                            let timedOutProjects =
                                failedList
                                |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                            // When 2+ projects ran and at least one has recorded elapsed,
                            // surface the slowest in the summary so a bottlenecked project
                            // is visible without having to query test-results JSON.
                            let slowestSuffix =
                                if total < 2 then
                                    ""
                                else
                                    let withElapsed =
                                        results.Results
                                        |> Map.toList
                                        |> List.choose (fun (name, r) ->
                                            let e = TestResult.elapsed r
                                            if e > TimeSpan.Zero then Some(name, e) else None)

                                    match withElapsed with
                                    | [] -> ""
                                    | _ ->
                                        let (n, e) = withElapsed |> List.maxBy snd
                                        $", slowest: %s{n} %.1f{e.TotalSeconds}s"

                            let deferredSuffix =
                                if deferred > 0 then
                                    $", %d{deferred} waiting on build"
                                else
                                    ""

                            // Never folded into the `failed` number it sits beside: the
                            // summary is the one line most readers see, so it is the one
                            // line that must not call a dead host a failure.
                            let abortedSuffix =
                                if aborted > 0 then
                                    $", %d{aborted} ABORTED (host died, nothing verified)"
                                else
                                    ""

                            let abortedNames = abortedList |> List.map fst |> String.concat ", "

                            if not timedOutProjects.IsEmpty then
                                let names = timedOutProjects |> String.concat ", "
                                // Flip the recorded outcome to TimedOut; the verdict on
                                // the Failed below carries the summary (one channel).
                                ctx.CompleteWithTimeout $"test project(s): {names}"

                                ctx.ReportStatus(
                                    PluginStatus.failedNow
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        results.Elapsed
                                )
                            else
                                let runSummary =
                                    $"%d{passed} passed, %d{failed} failed%s{abortedSuffix}%s{deferredSuffix}%s{noMatchSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

                                // EVERY terminal below CARRIES the run's evidence —
                                // `runSummary` + measured duration — on the status
                                // itself. There is no separate summary channel left to
                                // forget or contradict.
                                if
                                    failed = 0
                                    && aborted = 0
                                    && deferred = 0
                                    && Set.isEmpty queueAfterCommit
                                    && carriedCount = 0
                                then
                                    // Nothing failed, nothing is owed — and on a
                                    // run that EXECUTED NOTHING that is not a pass, it is an
                                    // absence of evidence. `runSummary` would report it as
                                    // "0 passed, 0 failed in 0 projects", which is how a plugin
                                    // line goes `✓` for a check that verified nothing while the
                                    // verdict layer is (correctly) refusing it exit 0.
                                    //
                                    // The STATUS stays `Completed`: nothing failed, and a
                                    // `Failed` here would claim one — turning `check`'s honest
                                    // exit 3 "NO VERDICT" into an exit 1 "failures found". It is
                                    // the VERDICT that carries the fact — the run record becomes
                                    // `RunOutcome.VerifiedNothing` — and every renderer keys its
                                    // glyph off that case (`ParsedPluginStatus.verifiedNothing`).
                                    //
                                    // Asked of `RunVerification`, THE derivation, so this is a
                                    // question about the RUN ("did anything execute?") and not
                                    // about one selection bug: any future path that lands an
                                    // executed-nothing run here is covered without a new arm.
                                    let verdict =
                                        match receiptTransition, evidenceReceipt with
                                        | ReceiptTransition.Noop, Some retained when
                                            RunVerification.verifiedNothing (verificationOf results.Results)
                                            ->
                                            // Nothing needed running because an earlier run
                                            // already verified this tree and model, and its
                                            // receipt stands: that run is the evidence, so
                                            // the line names it rather than the empty skip.
                                            let retainedRun = retained.RunId.ToString("N")

                                            RunVerdict.create
                                                $"no test needed: nothing changed since run %s{retainedRun}, which verified this tree"
                                                results.Elapsed
                                        | _ when RunVerification.verifiedNothing (verificationOf results.Results) ->
                                            RunVerdict.verifiedNothing
                                                $"%d{total} test project(s) ran, no test executed"
                                                results.Elapsed
                                        | _ -> RunVerdict.create runSummary results.Elapsed

                                    ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))
                                elif failed = 0 && aborted = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
                                    // Everything this run RAN passed, the
                                    // queue is drained — and yet an earlier failure it did
                                    // not execute is still outstanding. A narrower run
                                    // cannot vindicate a test it never ran, so this is
                                    // NON-green: the red survives until a run that COVERS
                                    // it passes (`dotnet fshw test-rerun` runs every
                                    // project unfiltered and will clear it if it is fixed).
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 && aborted = 0 && deferred = 0 then
                                    // Everything that RAN passed, but the pending queue
                                    // still holds symbols this (e.g. filtered) run did not
                                    // cover green — NOT test-equivalent to a green run yet.
                                    // Non-green with the honest "waiting on build" wording;
                                    // the next BuildCompleted re-selects and runs them.
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%s{pendingDescription ()}%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 && aborted = 0 then
                                    // Only deferred projects — nothing FAILED, but
                                    // nothing was verified either. Non-green, honest
                                    // "waiting on build" (never "failed").
                                    let names = deferredList |> List.map fst |> String.concat ", "

                                    if carriedCount = 0 then
                                        // PURE defer: no red this run or carried. A
                                        // NON-failing terminal, so the verdict reads the
                                        // `Deferred`-severity ledger entry and routes it to
                                        // `Incomplete`/exit 2, not the exit 1 a red earns —
                                        // a build-ordering race left one project unrun.
                                        // A defer's `verdict` is `NothingVerified`, so its
                                        // symbols never commit (the next build re-runs them)
                                        // and the result is uncacheable.
                                        ctx.ReportStatus(
                                            PluginStatus.completedNow
                                                $"%s{runSummary} — %d{deferred} waiting on build (tests did not run): %s{names}"
                                                results.Elapsed
                                        )
                                    else
                                        // A carried RED from an earlier run is still
                                        // outstanding — that dominates a defer. Stay
                                        // Failed/red (exit 1); the ledger's carried Error
                                        // entry independently keeps the verdict red.
                                        ctx.ReportStatus(
                                            PluginStatus.failedNow
                                                $"%d{deferred} waiting on build (tests did not run): %s{names}%s{carriedNote}"
                                                $"%s{runSummary}%s{carriedNote}"
                                                results.Elapsed
                                        )
                                elif failed = 0 then
                                    // Nothing failed — a test HOST DIED.
                                    // Killed by a signal under load, or gone before it
                                    // could write a report. No test was shown to fail and
                                    // no test was shown to pass, so this is the third
                                    // answer and it must LOOK like the third answer: a
                                    // dead host reported as "N failed: X" sends the reader
                                    // hunting a regression that is not there, which is the
                                    // hours this arm exists to stop burning.
                                    //
                                    // A NON-failing terminal, exactly like the pure-defer
                                    // arm above: the `HostAborted`-severity ledger entries
                                    // route the verdict to `RunnerAborted`/exit 2 rather
                                    // than the exit 1 a red earns. The verdict is a
                                    // verified-nothing one (`RunOutcome.VerifiedNothing` on
                                    // the run record), so no renderer can give this run a
                                    // bare `✓` either.
                                    let deferredNote =
                                        if deferred > 0 then
                                            let dn = deferredList |> List.map fst |> String.concat ", "
                                            $" (+%d{deferred} waiting on build: %s{dn})"
                                        else
                                            ""

                                    let abortLine =
                                        $"%d{aborted} test host(s) ABORTED — killed mid-run, nothing verified (NOT a test failure): %s{abortedNames}%s{deferredNote}"

                                    if carriedCount = 0 then
                                        ctx.ReportStatus(PluginStatus.verifiedNothingNow abortLine results.Elapsed)
                                    else
                                        // A carried RED from an earlier run outranks an
                                        // abort — same rule the defer arm applies, and for
                                        // the same reason: an aborted run cannot vindicate
                                        // a test it never ran. Stay red (exit 1); the
                                        // carried Error entry keeps the ledger red anyway.
                                        ctx.ReportStatus(
                                            PluginStatus.failedNow
                                                $"%s{abortLine}%s{carriedNote}"
                                                $"%s{runSummary}%s{carriedNote}"
                                                results.Elapsed
                                        )
                                else
                                    let names = failedList |> List.map fst |> String.concat ", "

                                    let deferredNote =
                                        if deferred > 0 then
                                            let dn = deferredList |> List.map fst |> String.concat ", "
                                            $" (%d{deferred} waiting on build: %s{dn})"
                                        else
                                            ""

                                    // A real red DOMINATES an abort beside it — but the
                                    // abort is still NAMED, and named as an abort: those
                                    // projects proved nothing, and a reader must not add
                                    // them to the failure count.
                                    let abortNote =
                                        if aborted > 0 then
                                            $" (+%d{aborted} ABORTED, nothing verified: %s{abortedNames})"
                                        else
                                            ""

                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{failed} failed: %s{names}%s{abortNote}%s{deferredNote}%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )

                    // Whatever this run left owed is queued behind it as an intent by
                    // the trigger that owed it (`enqueueImpactRun`, a queued `run-tests`),
                    // and is delivered only after this fold is committed. Nothing is
                    // launched from here.
                    recordRunOutcome testResults

                    let replies =
                        match message with
                        | CommandTestsFinished(_, _, _, reply, response) -> [ reply, response ]
                        | _ -> []

                    return
                        { state with
                            LastResults = Some testResults
                            // Only the files this run launched against are consumed; a
                            // file that changed during the run selects the next one.
                            ChangedFiles =
                                if coveredByConstruction then
                                    []
                                else
                                    state.ChangedFiles
                                    |> List.filter (fun file -> not (List.contains file launch.ChangedFiles))
                            ChangedSymbols = remainingChangedSymbols
                            AffectedTests = Analyzed []
                            Replies = replies }

                // An unavailable launch ran nothing. Its receipt is revoked and the fanout
                // it consumed is owed again. No run is queued: the next build or cohort
                // seal is what can make the artifacts or the host available, and it
                // launches with this debt.
                | Custom(ArtifactsUnavailable(reason, owed, reply)) ->
                    let message =
                        $"Tests did not run because the preceding build left invalid artifacts: %s{reason}"

                    return unavailableRun ctx state message owed reply

                | Custom(TestHostUnavailable(reason, owed, reply, None)) ->
                    let message = $"Tests did not run because the test host could not start: %s{reason}"
                    return unavailableRun ctx state message owed reply

                // The run started and its result is gone, so it proves nothing: it ends
                // Aborted, and what it would have discharged stays owed.
                | Custom(TestHostUnavailable(reason, owed, reply, Some started)) ->
                    let _, completed = abortedRunLifecycle (Some started) reason
                    ctx.EmitTestRunCompleted completed
                    let message = $"The test run's result was discarded: %s{reason}"
                    return unavailableRun ctx state message owed reply

                // Nobody is left to read this run, so it reports nothing: the status its
                // `Running` displaced is already back. It closes what it opened, ends the
                // run in flight, and hands back the fanout it consumed. The rest of its debt
                // was never discharged, so whatever next wants a run launches with it.
                | Custom(TestRunWithdrawn(owed, reply, opened)) ->
                    let reason = "every client that wanted the run has gone"

                    opened
                    |> Option.iter (fun started ->
                        let _, completed = abortedRunLifecycle (Some started) reason
                        ctx.EmitTestRunCompleted completed)

                    reply
                    |> Option.iter (fun target ->
                        target.TrySetResult(JsonSerializer.Serialize {| error = reason |}) |> ignore)

                    return
                        { endRun false state with
                            PendingForceRunProjects = Set.union state.PendingForceRunProjects owed
                            DebtDuringFullRun = Map.empty }

                | Custom OwedRunRequested ->
                    if TestMode.launchesOwedRuns state.Mode then
                        return state
                    else
                        let armed =
                            { state with
                                Mode = TestMode.armOwedRun state.Mode }

                        if state.OwedRunHeld then
                            return launchRequested ctx armed
                        else
                            Logging.info
                                "test-prune"
                                "A command asked for the owed test run: none is held yet, so the next build's run launches"

                            return armed

                | Custom(ScopeRequested mode) ->
                    if TestMode.requestsFullSuite mode then
                        Logging.info
                            "test-prune"
                            "Scope set to FULL SUITE — impact filtering disabled for subsequent runs in this daemon session"
                    else
                        Logging.info "test-prune" "Scope set to IMPACT-FILTERED (inner-loop default)"

                    return { state with Mode = mode }

                | Custom(RuntimeCoverageFailed(project, reason)) ->
                    // What is unknown is what THIS project's tests reach on the tree they ran
                    // on, so it is this project's debt, not the suite's.
                    Logging.warn
                        "test-prune"
                        $"runtime coverage for %s{project} could not be ingested: %s{reason}; %s{project} runs in full until a run of it ingests"

                    return
                        { state with
                            Debt =
                                { state.Debt with
                                    CoverageNotIngested = Map.add project reason state.Debt.CoverageNotIngested } }

                | Custom ImpactRunRequested -> return impactRun ctx state
                | Custom(DiffWaitExpired id) ->
                    match state.AwaitingDiff with
                    | Some held when held.Id = id ->
                        Logging.warn
                            "test-prune"
                            $"No check results were folded within %.0f{DiffWait.bound.TotalSeconds}s of the build that held its launch for them"

                        return launchHeld ctx state "its wait expired, so it selects without the diff"
                    | _ -> return state

                | Custom(RunTestsRequested(configs, filter, reply)) ->
                    return requestTestRun ctx state configs filter reply

                | Custom(IndexFoldProbe folded) ->
                    folded.TrySetResult() |> ignore
                    return state

                | Custom(RunFailedTestsRequested(filter, reply)) ->
                    match failedTestConfigs (defaultArg testConfigs []) state.LastResults state.OutstandingFailures with
                    | Error msg ->
                        reply.TrySetResult(JsonSerializer.Serialize({| error = msg |})) |> ignore
                        return state
                    | Ok [] ->
                        reply.TrySetResult(JsonSerializer.Serialize({| error = "no matching test projects" |}))
                        |> ignore

                        return state
                    | Ok configs -> return requestTestRun ctx state configs filter reply

                | _ -> return state
            }
      PrepareCommit = Some prepareCommit
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            // BatchChecked is the cohort-complete flush signal: it fires after the last
            // FileChecked of a batch and before any subsequent BuildCompleted racing the
            // same change, so by the time the agent processes it every FileChecked update
            // has been folded in to `state.ChangedSymbols`.
            //
            // BuildCompleted is subscribed UNCONDITIONALLY so the freshness-stamp gate
            // works even when the plugin is analysis-only: with no testConfigs the handler
            // still runs `flushAndQueryAffected` (idempotent on empty PendingAnalysis) and
            // skips the test-run path.
            [ SubscribeFileChecked; SubscribeBatchChecked; SubscribeBuildCompleted ]
        )
      CacheKey =
        // Pure-content cache key, built by the lifted `cacheKeyFor` so the per-arm input
        // dependencies are structural rather than a convention. The thunks are this
        // closure's live state; `cacheKeyFor` decides which arm forces which — and
        // `FileChecked`, the per-file probe, forces none.
        let cacheKey (state: TestPruneState) (event: PluginEvent<TestPruneMsg>) : ContentHash option =
            let changedSymbolsHash () =
                state.ChangedSymbols
                |> List.distinct
                |> List.sort
                |> String.concat "|"
                |> FsHotWatch.CheckCache.sha256Hex

            // The persisted needs-testing queue. `None` = empty, which both omits the
            // merkle entry (keeping the empty-queue green fast-path key byte-stable) and,
            // on BuildCompleted, is what makes the event cacheable at all: a green that
            // left symbols queued must re-run, never replay.
            let pendingQueueHash () =
                if state.Debt.RecoveryOutstanding then
                    // An unreadable ledger is outstanding debt whose
                    // membership is unknown. `None` would assert "provably nothing owed"
                    // and make BuildCompleted cacheable, so a cached green written over
                    // this same content merkle would replay with no test process running.
                    // `Some` refuses cache participation outright, as a non-empty queue
                    // does. A constant rather than a hash — there is nothing to hash.
                    Some "unreadable-ledger"
                elif not (Map.isEmpty state.Debt.CoverageNotIngested) then
                    // A project owes a whole-project run that ingests: a cached green
                    // cannot pay it. Refused as a non-empty queue is.
                    let owed = state.Debt.CoverageNotIngested |> Map.keys |> String.concat ","

                    Some $"coverage-not-ingested:%s{owed}|%s{PendingVerification.hash state.Debt.PendingQueue}"
                elif Set.isEmpty state.Debt.PendingQueue then
                    None
                else
                    Some(PendingVerification.hash state.Debt.PendingQueue)

            // External-dependency salt: a content hash of the files matched by the
            // configured `dependsOn` globs. Editing a matched file (a DB migration
            // that changes the TEST database schema but no test SOURCE) changes this
            // hash → cache miss → genuine re-run. `None` when unconfigured or when
            // the globs match nothing, so the entry is omitted and existing on-disk
            // caches keep hitting.
            let dependsOnHash () =
                match externalDependencyHash repoRoot dependsOn with
                | "" -> None
                | h -> Some h

            // A full-suite run must never REPLAY an impact-filtered run's
            // cached verdict. Salting the key with the requested scope makes that
            // impossible rather than merely unlikely.
            let fullSuiteScopeHash () =
                // A run widened by a missing baseline is a full-suite
                // run too, and must not replay a filtered run's cached verdict.
                if
                    TestMode.requestsFullSuite state.Mode
                    || Option.isSome (baselineInvalidReason state.Debt)
                then
                    Some "full"
                else
                    None

            // No cache participation while a red no covering run has
            // passed is outstanding.
            let hasOutstandingFailures () =
                not (List.isEmpty state.OutstandingFailures)

            // No cache participation on BuildCompleted until a run in
            // THIS process has covered something.
            //
            // ANALYSIS-ONLY IS EXEMPT. With no runnable test projects this plugin makes no
            // test claim at all — its terminal status is a symbol-analysis summary — so
            // there is no verdict to launder, and gating it would throw away a working
            // cache to guard an assertion it never makes.
            let sessionHasTestEvidence () =
                Set.isEmpty runnableProjects
                || not (Set.isEmpty (RunCoverage.coveredProjects state.LastCoverage))

            // A full-repo walk of the project files, so it is a thunk
            // like the rest: `FileChecked` fires once per file on every scan and must
            // not pay for an input it never splices.
            let structureHash () = projectStructureHash repoRoot

            match event with
            // Dependency fanout still owed: the launch that runs it is this handler's, so
            // no replay AND no write, as for a non-empty pending queue. A replayed green
            // would skip the only event that launches it, and the fanout is not a key
            // input, so a later lookup could not tell the entry from one that ran it.
            | BuildCompleted _
            | Custom(TestsFinished _) when not (Set.isEmpty state.PendingForceRunProjects) -> None
            // A full-suite request earns its evidence from a real run, never a replay. The
            // run's own entry is still written, from its `TestsFinished` window.
            | BuildCompleted _ when TestMode.requestsFullSuite state.Mode -> None
            // A session that launches no owed run has no build outcome to replay or record.
            | BuildCompleted _ when not (TestMode.launchesOwedRuns state.Mode) -> None
            // No receipt binds the tree as it is now. The key names the changed symbols
            // and the project files, not the bytes, so an edit that changes neither (a
            // comment) computes the key of the green it follows. A replay would skip the
            // handler, and with it the only run that can earn evidence for this tree.
            | BuildCompleted BuildSucceeded when
                not (Set.isEmpty runnableProjects)
                && not (
                    state.EvidenceReceipt
                    |> Option.exists (fun evidence ->
                        ReceiptInputTree.matches evidence.InputTreeHash (ReceiptInputTree.read repoRoot))
                )
                ->
                None
            | _ ->
                cacheKeyFor
                    changedSymbolsHash
                    pendingQueueHash
                    dependsOnHash
                    structureHash
                    fullSuiteScopeHash
                    (fun () -> ReceiptInputTree.read repoRoot)
                    hasOutstandingFailures
                    sessionHasTestEvidence
                    event

        Some cacheKey
      Teardown = None }

/// `createWithQueries` over the plugin's own index.
/// The test-evidence wiring a daemon gets for `repoRoot`: the repository's box-wide store,
/// its projects' `evidence` blocks, this process's toolchain and the daemon's `exclude`.
/// `None` when `FSHW_TEST_EVIDENCE=0`, or when the config cannot be read (loudly).
let internal evidenceWiringWhen
    (enabled: bool)
    (excludePatterns: string list)
    (repoRoot: string)
    : TestEvidenceRun.Wiring option =
    if not enabled then
        Logging.info "test-prune" $"test evidence: off (%s{TestEvidenceStore.KillSwitchEnvVar}=0)"
        None
    else
        match TestEvidenceKey.configsOf repoRoot with
        | Error reason ->
            Logging.warn "test-prune" $"test evidence: off — %s{reason}"
            None
        | Ok configs ->
            // Eager: the environment and env files as THIS daemon loaded them, not as a
            // later keying finds them on disk.
            let loaded = TestEvidenceKey.LoadedEnvironment.ofProcess repoRoot configs.Values
            Logging.info "test-prune" $"test evidence: keyed %s{TestEvidenceKey.LoadedEnvironment.describe loaded}"

            try
                Some
                    { Store = TestEvidenceStore.openFor repoRoot (Logging.info "test-prune")
                      Configs = configs
                      Toolchain = lazy (TestEvidenceKey.toolchainOf repoRoot)
                      Loaded = loaded
                      ProducerHash = ContentHash.ofText CheckCache.toolchainIdentity
                      ExcludePatterns = excludePatterns
                      // ADR-059 step 7: every completed project run is recorded, box-wide,
                      // under the K_P it ran over. The shadow verdict reads these next.
                      Outcomes = Some(OutcomeStore.openFor repoRoot (Logging.info "test-prune")) }
            with ex ->
                Logging.warn "test-prune" $"test evidence: off — the store could not be opened: %s{ex.Message}"
                None

/// The test-evidence wiring a daemon gets for `repoRoot`, unless `FSHW_TEST_EVIDENCE=0`.
let internal defaultEvidenceWiring (excludePatterns: string list) (repoRoot: string) =
    evidenceWiringWhen (TestEvidenceStore.enabled ()) excludePatterns repoRoot

/// `createWithEvidenceAndQueries` with the daemon's default test-evidence wiring.
let internal createWithQueries
    (queriesOf: Database -> ImpactQueries)
    (launchDeadline: TimeSpan)
    (maxParallelProjects: int option)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    =
    createWithEvidenceAndQueries
        (defaultEvidenceWiring [] repoRoot)
        queriesOf
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot

let internal createWithLaunchDeadline
    (launchDeadline: TimeSpan)
    (maxParallelProjects: int option)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    (traces: TraceWiring option)
    (initialMode: TestMode)
    =
    createWithQueries
        ImpactQueries.ofDatabase
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        traces
        initialMode

/// Create a TestPrune handler that honors declared test-scope exclusions and records
/// per-test traces as `tests.traces` asks.
///
/// `traces` is the parsed `tests.traces` block; `None` records nothing and launches every
/// project exactly as `createWithScope` does. `untracedProjects` names the projects that
/// opted out with `"traces": false`; `weaveOverrides` maps a project to its own
/// `traces.weaveTests`, which it is woven with instead of `traces.WeaveTests`. A project tracing refuses (no build output, a refused
/// weave, a failed JIT verification, an unknown `dotnet run` option) runs untraced, and
/// the refusal is stored in the trace database and logged with its reason. No trace
/// failure changes a test result or the verdict.
///
/// `initialMode` is the mode the session starts in (`TestMode.initial` for a daemon). See
/// `createWithScope` for `resolveExcludedProjects` and the launch policy.
///
/// `excludePatterns` is the daemon's `exclude`, the patterns its verdict tree hash is
/// computed with. The test-evidence key leaves out exactly those files, so two checkouts
/// whose tree hashes agree also agree on every key.
let createWithTracesIn
    (excludePatterns: string list)
    (initialMode: TestMode)
    // `tests.maxParallelProjects`: at most this many test groups run at once, longest
    // expected wall first; `None` launches every group at once.
    (maxParallelProjects: int option)
    (traces: TraceSettings option)
    (untracedProjects: Set<string>)
    (weaveOverrides: Map<string, TraceWeaveTests>)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    let launchDeadline =
        Environment.GetEnvironmentVariable "FSHW_LAUNCH_DEADLINE_SEC"
        |> Option.ofObj
        |> resolveLaunchDeadline

    createWithEvidenceAndQueries
        (defaultEvidenceWiring excludePatterns repoRoot)
        ImpactQueries.ofDatabase
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        (traces
         |> Option.map (fun settings ->
             { Policy = settings
               OptedOut = untracedProjects
               WeaveOverrides = weaveOverrides
               Decide = TraceRun.decide }))
        initialMode

/// `createWithTracesIn` for a daemon session, which starts in `TestMode.initial`.
let createWithTraces
    (traces: TraceSettings option)
    (untracedProjects: Set<string>)
    (weaveOverrides: Map<string, TraceWeaveTests>)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithTracesIn
        []
        TestMode.initial
        None
        traces
        untracedProjects
        weaveOverrides
        resolveExcludedProjects
        dbPath
        TestPrune.Domain.WalkPolicy.FollowAll
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn

/// Create a TestPrune handler that honors declared test-scope exclusions.
///
/// `resolveExcludedProjects` returns indexed test-project name (the project file's name
/// without extension) -> written reason. A covering project that is not configured and
/// is declared here with a non-blank reason no longer holds a symbol's verification
/// debt; every other covering project does, configured or not. It is called at each
/// debt classification; a throw refuses that classification and leaves the debt owed.
///
/// The launch policy is captured at construction. Environment configuration is
/// process-global, so reading it lazily at run time can retroactively change
/// already-created handlers (and made a short-deadline regression kill unrelated
/// in-flight test children under parallel CI). A handler's policy is immutable for its
/// lifetime; tests inject it through `createWithLaunchDeadline` without mutating
/// process-global state.
let createWithScope
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithTraces
        None
        Set.empty
        Map.empty
        resolveExcludedProjects
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn

/// Create a TestPrune handler with no declared exclusions: every covering test project
/// holds its symbols' verification debt. See `createWithScope`.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithScope
        (fun () -> Map.empty)
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
