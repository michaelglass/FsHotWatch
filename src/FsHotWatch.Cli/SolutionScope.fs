module FsHotWatch.Cli.SolutionScope

// ---------------------------------------------------------------------------
// AUTOMATION-158 — "full suite" must not quietly mean "the suites I was told
// about".
//
// THE DEFECT. `confirm` renders a verdict whose scope reads
// `{"kind":"full","ranProjects":N,"totalProjects":N}`. Both numbers count
// `.fshw.json`'s `tests.projects`, so "full" means "every suite the config
// named" — never "every suite in the solution". A test project that is in the
// solution and in no gated list is not reported as UNRUN; it is simply absent,
// and absent is indistinguishable from passing. This repo found it on itself:
// `FsHotWatch.IntegrationTests` was in `FsHotWatch.slnx`, was a runnable test
// project, and was in no list `confirm` read.
//
// THE RULE. Every test project in the solution is either GATED (named in
// `tests.projects`) or EXCLUDED with a written reason (`tests.excluded`).
// Anything else is a `ConfigError` — the same treatment `analyzerPathFailures`
// already gives an analyzer path that loads zero analyzers, one level up. An
// unloaded analyzer and a clean one must never look alike; an unrun test
// project and a passing one must not either.
//
// THE SOLUTION IS THE AUTHORITY, not a directory scan: the solution is what
// `dotnet build` compiles and what a solution-wide `dotnet test` runs, so it is
// the set against which "full" has to mean something. A project on disk but in
// no solution is built by nothing and is correctly out of scope.
//
// WHERE THIS BINDS. `DaemonConfig.loadConfig` runs it, and every fshw verb
// loads the config in the CLI process on every invocation. So the reconciliation
// is re-done against the CURRENT solution each time `check` / `confirm` /
// `start` runs, not only when a daemon boots: adding a test project to the
// solution without touching `.fshw.json` fails the very next command, even
// against a long-lived daemon that never reloaded anything.
//
// WHAT IT CANNOT SEE, stated rather than implied:
//   * A repo with NO solution file has no declared universe to be complete
//     against, so there is nothing to reconcile and this returns no findings.
//     fshw's full-suite claim is complete RELATIVE TO THE SOLUTION; with no
//     solution there is no such claim to make.
//   * A test project whose runner this module does not recognise is invisible
//     to the classifier. That is why the marker list leans INCLUSIVE and why a
//     GATED project counts as a test project whether or not the classifier
//     agrees: the config's own claim is stronger evidence than the heuristic.
//
// Everything except `reconcile` is a pure function over TEXT (the solution, each
// project file, the parsed config), so the whole matrix is unit-tested without
// staging a checkout.
// ---------------------------------------------------------------------------

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// A declared exclusion: a solution test project the gate deliberately does not
/// run, and the reason it does not.
///
/// `Reason` is NOT optional. An exclusion without a written reason is a silence
/// wearing a declaration's clothes, so the empty string is representable only so
/// that `decide` can REPORT it — never so that it can pass.
type Exclusion = { Project: string; Reason: string }

/// A test project the config gates, as the config spells it.
///
/// Both spellings are carried because both are in use and either may be the one
/// that resolves: `Project` is the label (`"FsHotWatch.Tests"`), `Args` is the
/// command line the runner actually launches (`"run --project
/// tests/FsHotWatch.Tests --no-build --"`). A config that names the project only
/// in its args must still reconcile, and so must one that names it only in its
/// label.
type GatedProject = { Project: string; Args: string }

/// One way the gated scope and the solution disagree. Each renders to a single
/// actionable line naming the project at fault.
type Finding =
    /// In the solution, declares a test runner, governed by nothing. THE defect.
    | UndeclaredTestProject of project: string
    /// An exclusion with no reason — see `Exclusion.Reason`.
    | ExclusionMissingReason of project: string
    /// An exclusion naming something the solution does not contain at all:
    /// either a typo or a leftover from a project that moved. Left unchecked it
    /// silently stops excluding anything, and the project it was meant to cover
    /// becomes undeclared again without anyone editing the exclusion.
    | StaleExclusion of project: string
    /// Gated AND excluded — the config contradicts itself and there is no safe
    /// way to guess which was meant.
    | GatedAndExcluded of project: string
    /// The config gates a suite the solution does not contain, under any of its
    /// spellings.
    | GatedProjectNotInSolution of project: string
    /// FLOOR. Tests are configured, yet nothing the config gates resolves into
    /// the solution and the classifier found no test project either. An empty
    /// offender list from a scan that saw nothing proves nothing — it is
    /// byte-identical to the report from a fully governed repo, which is the
    /// false confidence this check exists to remove.
    | NoTestProjectsFound
    /// More than one solution file sits at the repo root and no `tests.solution`
    /// says which one is the authority. Picking one would make the completeness
    /// claim depend on directory-enumeration order.
    | AmbiguousSolution of candidates: string list
    /// `tests.solution` names a file that is not there. DISTINCT from having no
    /// solution at all: the config named an authority for its scope, so this is a
    /// contradictory declaration rather than an absent one, and it must not fall
    /// through to "nothing to reconcile".
    | SolutionNotFound of path: string

// ---------------------------------------------------------------------------
// Paths and identities
// ---------------------------------------------------------------------------

/// Project file extensions a solution can carry that fshw might gate.
let private projectExtensions = [ ".fsproj"; ".csproj"; ".vbproj" ]

/// Repo-relative, '/'-separated, no trailing slash, no leading './', with any
/// project-file name trimmed off.
///
/// `.sln` files use '\' on every platform, `.slnx` and config args use '/', and
/// either may name the project directory or the project file itself. All of
/// those spellings have to compare equal or the reconciliation invents
/// differences.
let normalisePath (path: string) : string =
    let slashed = (if isNull path then "" else path).Replace('\\', '/').Trim()

    let withoutProjectFile =
        if
            projectExtensions
            |> List.exists (fun ext -> slashed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        then
            match slashed.LastIndexOf '/' with
            | -1 ->
                // A project file at the repo root: there is no directory to fall
                // back to, so the extension comes off and the bare name IS the
                // identity. Returning `Foo.fsproj` here would make it match
                // nothing a config can plausibly spell.
                match slashed.LastIndexOf '.' with
                | -1 -> slashed
                | dot -> slashed.Substring(0, dot)
            | i -> slashed.Substring(0, i)
        else
            slashed

    let trimmed = withoutProjectFile.TrimEnd('/')

    if trimmed.StartsWith("./", StringComparison.Ordinal) then
        trimmed.Substring 2
    else
        trimmed

/// The last '/'-separated segment of a normalised path — the project directory's
/// name, which by near-universal .NET convention is also the project's name.
///
/// This is what lets `"project": "FsHotWatch.Tests"` (a bare label) resolve to
/// `tests/FsHotWatch.Tests/FsHotWatch.Tests.fsproj`.
let private leafName (normalised: string) : string =
    match normalised.LastIndexOf '/' with
    | -1 -> normalised
    | i -> normalised.Substring(i + 1)

/// Every spelling by which a config may legitimately refer to a solution
/// project: its repo-relative directory, and its bare name.
let identitiesOf (solutionProjectPath: string) : string list =
    let dir = normalisePath solutionProjectPath
    let name = leafName dir

    if String.Equals(dir, name, StringComparison.Ordinal) then
        [ dir ]
    else
        [ dir; name ]

/// The `--project <path>` a test entry's args actually launch, when they name
/// one. `None` for a runner invoked some other way — which is not an error here;
/// the `project` label is then the only identity, and it usually resolves.
let projectFromArgs (args: string) : string option =
    if String.IsNullOrWhiteSpace args then
        None
    else
        let tokens = args.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

        tokens
        |> Array.tryFindIndex (fun t -> String.Equals(t, "--project", StringComparison.Ordinal))
        |> Option.bind (fun i ->
            if i + 1 < tokens.Length then
                Some(normalisePath tokens[i + 1])
            else
                None)

/// Every spelling by which a gated entry refers to its project.
let identitiesOfGated (gated: GatedProject) : string list =
    [ normalisePath gated.Project
      yield! (projectFromArgs gated.Args |> Option.toList) ]
    |> List.filter (String.IsNullOrWhiteSpace >> not)
    |> List.distinct

// ---------------------------------------------------------------------------
// Reading a solution
// ---------------------------------------------------------------------------

/// Every project a solution references, as repo-relative project FILE paths —
/// as the solution wrote them, with only the separators normalised.
///
/// The path is NOT reduced to its directory here: `tryReadProject` has to open
/// the file, and a project that lives at the repo root (`App.fsproj`) has no
/// directory to be reduced to. `decide` normalises for identity; this stays
/// literal so the read stays possible.
///
/// Read from the solution's TEXT rather than through MSBuild, and with ONE regex
/// for both formats: the classic `.sln`'s
/// `Project("{GUID}") = "Name", "path\to\Name.fsproj", "{GUID}"` and the modern
/// `.slnx`'s `<Project Path="path/to/Name.fsproj" />` both put the project path
/// inside double quotes ending in a project extension. That is the stable part
/// of both formats, and reading it needs neither an SDK nor a restore — this
/// check has to be able to say what the solution contains before anything is
/// built.
let solutionProjects (solutionText: string) : string list =
    if isNull solutionText then
        []
    else
        let pattern =
            projectExtensions
            |> List.map (fun ext -> Regex.Escape ext)
            |> String.concat "|"
            |> sprintf "\"([^\"]+(?:%s))\""

        Regex.Matches(solutionText, pattern, RegexOptions.IgnoreCase)
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Groups[1].Value.Replace('\\', '/').Trim().TrimStart('.', '/'))
        |> Seq.distinct
        |> List.ofSeq

/// PURE: does this project declare a TEST RUNNER?
///
/// Decided on runner markers rather than on where the project lives or what it
/// is called. A shared fixture library sits in `tests/` and runs nothing; a
/// retry-harness library references xunit and runs nothing. Neither a directory
/// heuristic nor a bare "mentions xunit" match separates those from a real
/// suite, and a false positive here costs a run that a one-line exclusion never
/// silences again.
///
/// The list leans INCLUSIVE on purpose, and the asymmetry is the whole argument:
/// a false positive is one loud line naming a project, answered by one line of
/// config; a false negative is the silence this check exists to end. So
/// VSTest-era markers are matched beside Microsoft.Testing.Platform ones — a
/// suite added with the older runner must not slip through a detector built for
/// the newer one.
let isTestProject (projectXml: string) : bool =
    if isNull projectXml then
        false
    else
        let propertyIsTrue (name: string) =
            Regex.IsMatch(projectXml, "<" + name + @">\s*[Tt]rue\s*</" + name + ">")

        let referencesPackage (name: string) =
            Regex.IsMatch(projectXml, @"Include\s*=\s*""" + Regex.Escape name + @"""", RegexOptions.IgnoreCase)

        propertyIsTrue "UseMicrosoftTestingPlatformRunner"
        || propertyIsTrue "IsTestProject"
        || propertyIsTrue "TestingPlatformDotnetTestSupport"
        || [ "Microsoft.NET.Test.Sdk"
             "Microsoft.Testing.Platform"
             "xunit.v3.mtp-v2"
             "xunit.runner.visualstudio"
             "NUnit3TestAdapter"
             "MSTest.TestAdapter" ]
           |> List.exists referencesPackage

// ---------------------------------------------------------------------------
// The reconciliation
// ---------------------------------------------------------------------------

/// PURE: every way the gated scope and the solution disagree.
///
/// `solutionProjectPaths` is EVERY project the solution references;
/// `testProjectPaths` is the subset that declares a runner. Both are needed and
/// they answer different questions: the first decides whether a config entry
/// names something that exists at all, the second decides what has to be
/// governed.
///
/// An empty result is the ONLY green, and `NoTestProjectsFound` makes an empty
/// result unreachable from a scan that saw nothing.
let decide
    (solutionProjectPaths: string list)
    (testProjectPaths: string list)
    (gated: GatedProject list)
    (excluded: Exclusion list)
    : Finding list =
    // Identity → canonical directory, over EVERY solution project. Built with an
    // ordinal-ignore-case comparer because the two platforms fshw runs on both
    // have case-insensitive filesystems, and a config that spells a directory
    // with the wrong case names the same project.
    let byIdentity =
        let map =
            Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        for path in solutionProjectPaths do
            let canonical = normalisePath path

            for identity in identitiesOf canonical do
                map[identity] <- canonical

        map

    let resolve (identity: string) : string option =
        match byIdentity.TryGetValue(normalisePath identity) with
        | true, canonical -> Some canonical
        | _ -> None

    let resolveAny (identities: string list) : string option = identities |> List.tryPick resolve

    let testSet = testProjectPaths |> List.map normalisePath |> Set.ofList

    let gatedResolved = gated |> List.map (fun g -> g, resolveAny (identitiesOfGated g))

    let gatedKeys = gatedResolved |> List.choose snd |> Set.ofList

    let excludedResolved = excluded |> List.map (fun e -> e, resolve e.Project)

    let excludedKeys = excludedResolved |> List.choose snd |> Set.ofList

    // A GATED project is a test project by the config's own declaration, whether
    // or not the classifier recognised its runner. Folding those in is what stops
    // a repo whose runner this module does not know from failing the floor below
    // with a message it could not act on.
    let knownTestProjects = Set.union testSet gatedKeys

    if Set.isEmpty knownTestProjects then
        [ NoTestProjectsFound ]
    else
        let undeclared =
            testSet
            |> Set.filter (fun p -> not (gatedKeys.Contains p) && not (excludedKeys.Contains p))
            |> Set.toList
            |> List.map UndeclaredTestProject

        let missingReason =
            excluded
            |> List.filter (fun e -> String.IsNullOrWhiteSpace e.Reason)
            |> List.map (fun e -> ExclusionMissingReason e.Project)

        let stale =
            excludedResolved
            |> List.filter (snd >> Option.isNone)
            |> List.map (fun (e, _) -> StaleExclusion e.Project)

        let contradictory =
            excludedResolved
            |> List.choose (fun (e, resolved) ->
                match resolved with
                | Some key when gatedKeys.Contains key -> Some(GatedAndExcluded e.Project)
                | _ -> None)

        let phantom =
            gatedResolved
            |> List.filter (snd >> Option.isNone)
            |> List.map (fun (g, _) -> GatedProjectNotInSolution g.Project)

        undeclared @ missingReason @ stale @ contradictory @ phantom

/// PURE: one actionable line per finding. `solutionName` is the file the
/// reconciliation was made against, so the message names the authority rather
/// than a generic "the solution".
let renderFinding (solutionName: string) (finding: Finding) : string =
    match finding with
    | UndeclaredTestProject project ->
        $"  %s{project} — in %s{solutionName}, declares a test runner, and no test run covers it. \
           Add it to .fshw.json `tests.projects`, or declare it in `tests.excluded` with a reason."
    | ExclusionMissingReason project ->
        $"  %s{project} — excluded with no `reason`. An exclusion without a written reason is the silence \
           this check exists to end."
    | StaleExclusion project ->
        $"  %s{project} — excluded, but %s{solutionName} contains no such project. Either the name is wrong \
           or the project moved; a stale exclusion silently stops excluding anything."
    | GatedAndExcluded project -> $"  %s{project} — listed in BOTH `tests.projects` and `tests.excluded`. Pick one."
    | GatedProjectNotInSolution project ->
        $"  %s{project} — gated by .fshw.json, but %s{solutionName} contains no such project."
    | NoTestProjectsFound ->
        $"  the scan of %s{solutionName} found NO test projects and resolved none of the gated ones. \
           The scan is blind, so it cannot vouch for anything — an empty problem list here would be \
           meaningless."
    | AmbiguousSolution candidates ->
        let joined = candidates |> String.concat ", "

        $"  %d{List.length candidates} solution files at the repo root (%s{joined}) and no `tests.solution` \
           saying which one the gate's scope is measured against."
    | SolutionNotFound path ->
        $"  `tests.solution` names %s{path}, which does not exist. The scope's authority cannot be a file \
           that is not there."

/// The whole message: every finding, then WHY a config error is the right
/// answer. Public so the wording is asserted once, in a test, rather than
/// re-spelled at each call site.
let describeFindings (solutionName: string) (findings: Finding list) : string =
    let lines = findings |> List.map (renderFinding solutionName) |> String.concat "\n"

    $"tests scope: %d{List.length findings} problem(s) reconciling .fshw.json with %s{solutionName}:\n\
       %s{lines}\n\
       `confirm` reports scope `{{\"kind\":\"full\",…}}` counting only `tests.projects`, so a suite missing \
       from that list is not reported as UNRUN — it is simply absent, and absent is indistinguishable from \
       passing. An unrun test project and a passing one must never look alike (AUTOMATION-158)."

// ---------------------------------------------------------------------------
// Disk
// ---------------------------------------------------------------------------

/// Solution files at the repo root, sorted so the answer does not depend on
/// directory-enumeration order. `.slnx` and `.sln` are both candidates: a repo
/// mid-migration has both, which is exactly when `tests.solution` has to be
/// spelled out.
let solutionCandidates (repoRoot: string) : string list =
    try
        [ yield! Directory.GetFiles(repoRoot, "*.slnx")
          yield! Directory.GetFiles(repoRoot, "*.sln") ]
        |> List.map Path.GetFileName
        |> List.sort
    with :? DirectoryNotFoundException ->
        []

/// Read a project's XML. `None` when the solution references a file that is not
/// there — a missing project is the solution's problem, and reporting it here
/// would bury the one thing this check speaks to. An unreadable project is
/// likewise not a test project: this check refuses a scope, it does not diagnose
/// a broken checkout.
let private tryReadProject (repoRoot: string) (relative: string) : string option =
    let candidate =
        Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar))

    try
        if File.Exists candidate then
            Some(File.ReadAllText candidate)
        else
            None
    with
    | :? IOException -> None
    | :? UnauthorizedAccessException -> None

/// Reconcile `.fshw.json`'s test scope with the solution on disk.
///
/// `[]` means the config's scope covers every test project in the solution, or
/// declares in writing why it does not.
///
/// A repo with NO solution file returns `[]`: there is no declared universe to
/// be complete against. See the module header — that is a stated limit of the
/// claim, not a hole in it.
let reconcile
    (repoRoot: string)
    (solutionOverride: string option)
    (gated: GatedProject list)
    (excluded: Exclusion list)
    : Finding list =
    let chosen =
        match solutionOverride with
        | Some explicitly when not (String.IsNullOrWhiteSpace explicitly) -> Ok(Some explicitly)
        | _ ->
            match solutionCandidates repoRoot with
            | [] -> Ok None
            | [ one ] -> Ok(Some one)
            | many -> Error(AmbiguousSolution many)

    match chosen with
    | Error finding -> [ finding ]
    | Ok None -> []
    | Ok(Some solutionName) ->
        let solutionPath =
            Path.Combine(repoRoot, solutionName.Replace('/', Path.DirectorySeparatorChar))

        if not (File.Exists solutionPath) then
            [ SolutionNotFound solutionName ]
        else
            let text =
                try
                    File.ReadAllText solutionPath
                with :? IOException ->
                    ""

            let allProjects = solutionProjects text

            let testProjects =
                allProjects
                |> List.filter (fun p ->
                    match tryReadProject repoRoot p with
                    | Some xml -> isTestProject xml
                    | None -> false)

            decide allProjects testProjects gated excluded

// ---------------------------------------------------------------------------
// Reading the declarations
// ---------------------------------------------------------------------------

/// The `tests.excluded` entries of a `tests` element, as written.
///
/// An entry with a blank or missing `reason` is RETURNED (with an empty
/// `Reason`), never dropped: dropping it would lose the project's name and turn
/// a reportable config error back into the silence this field exists to end.
/// `decide` reports it.
///
/// The ONE parser. `DaemonConfig` reads the declarations to validate them and the
/// verdict writer reads them to record them; two parsers could disagree about
/// what the config says, and the disagreement would land in a file whose whole
/// job is to be believed.
let exclusionsOf (testsElement: JsonElement) : Exclusion list =
    match testsElement.TryGetProperty "excluded" with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray()
        // `TryGetProperty` is only defined on an object: a bare `42` in the array
        // throws, and a malformed config must produce a FINDING, never a crash.
        |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Object)
        |> Seq.choose (fun e ->
            match e.TryGetProperty "project" with
            | true, p when p.ValueKind = JsonValueKind.String ->
                let reason =
                    match e.TryGetProperty "reason" with
                    | true, r when r.ValueKind = JsonValueKind.String -> r.GetString()
                    | _ -> ""

                Some
                    { Project = normalisePath (p.GetString())
                      Reason = (if isNull reason then "" else reason.Trim()) }
            | _ -> None)
        |> List.ofSeq
    | _ -> []

/// The declarations in a `.fshw.json` document.
///
/// `None` means the document could not be read as one — NOT that it excludes
/// nothing. `Some []` is the positive claim; a parse failure has established
/// nothing and must not be able to spell one.
let parseExclusions (configJson: string) : Exclusion list option =
    try
        use doc = JsonDocument.Parse configJson

        match doc.RootElement.TryGetProperty "tests" with
        | true, tests when tests.ValueKind = JsonValueKind.Object -> Some(exclusionsOf tests)
        // No `tests` section at all: the config gates no suites, so it declares
        // no exclusions either — an answer, not a failure to get one.
        | _ -> Some []
    with :? JsonException ->
        None

/// The declarations on disk at `repoRoot`. `None` when there is no `.fshw.json`,
/// or it cannot be read or parsed — see `parseExclusions`.
let readExclusions (repoRoot: string) : Exclusion list option =
    let path = Path.Combine(repoRoot, ".fshw.json")

    if not (File.Exists path) then
        None
    else
        try
            parseExclusions (File.ReadAllText path)
        with
        | :? IOException -> None
        | :? UnauthorizedAccessException -> None

/// The solution the reconciliation was made against, for a message that has to
/// name it. `"the solution"` when there is none to name.
let solutionNameFor (repoRoot: string) (solutionOverride: string option) : string =
    match solutionOverride with
    | Some explicitly when not (String.IsNullOrWhiteSpace explicitly) -> explicitly
    | _ ->
        match solutionCandidates repoRoot with
        | [ one ] -> one
        | _ -> "the solution"
