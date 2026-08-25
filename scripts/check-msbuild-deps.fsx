#!/usr/bin/env dotnet fsi

/// AUTOMATION-290: the app-local MSBuild support assemblies cannot be silently
/// downgraded.
///
/// WHAT HAPPENED. `Microsoft.NET.StringTools` moved backwards in a published CLI
/// (18.4.0 -> an older build) as a transitive consequence of an unrelated bump.
/// Nothing in this repository names that package, so nothing constrained it and
/// nothing noticed. The result was total: MSBuild project loading threw
/// `Method not found: 'Boolean
/// Microsoft.NET.StringTools.SpanBasedStringBuilder.Equals(System.String,
/// System.StringComparison)'` for EVERY project, discovery registered zero
/// projects, no plugin ran, and no test executed — in the consuming repository,
/// after publish.
///
/// WHY THE OTHER HALF OF 290 IS NOT ENOUGH. `failIfNoProjects` now makes that
/// state loud in seconds instead of an hour, and it is well covered. But it is a
/// SYMPTOM check: it fires in the consumer, after the bad package shipped. This
/// is the half that stops it shipping.
///
/// WHY READ `deps.json` AND NOT THE PROJECT FILES. The package is TRANSITIVE —
/// it arrives through Ionide.ProjInfo's MSBuild dependencies. Reading
/// `.fsproj`s would prove only that we did not name it, which is already true
/// and was never the question. `FsHotWatch.Cli.deps.json` is what the built
/// application actually resolved and is the same file the original diagnosis
/// used to identify the regression.
///
/// FLOOR, NOT EQUALITY. Asserting an exact version would fail on every ordinary
/// upgrade, and a guard that cries wolf gets deleted. The rule is the one that
/// matches the failure: never BELOW the version known to work.
///
/// Usage: dotnet fsi scripts/check-msbuild-deps.fsx   (exit 1 on any failure)

open System
open System.IO
open System.Text.Json

/// The version the working CLI shipped, per AUTOMATION-290's package diff. The
/// broken build was strictly older; anything at or above this is fine.
let floors: (string * Version) list =
    [ ("Microsoft.NET.StringTools", Version(18, 4, 0)) ]

let repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull (box dir) then
            failwith "Could not locate the repository root (no FsHotWatch.slnx above this script)."
        elif File.Exists(Path.Combine(dir.FullName, "FsHotWatch.slnx")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo __SOURCE_DIRECTORY__)

/// Every built `FsHotWatch.Cli.deps.json`, newest first. A `Debug` build is the
/// ordinary local case and a `Release` build is what `pack` publishes; either is
/// evidence about what the resolver produced, so the check does not insist on a
/// particular configuration having been built.
let depsFiles () =
    let cliDir = Path.Combine(repoRoot, "src", "FsHotWatch.Cli")

    if Directory.Exists cliDir then
        Directory.EnumerateFiles(cliDir, "FsHotWatch.Cli.deps.json", SearchOption.AllDirectories)
        |> Seq.sortByDescending File.GetLastWriteTimeUtc
        |> List.ofSeq
    else
        []

/// The resolved version of `package` in one deps file, if it appears at all.
///
/// Absence is NOT success and is reported by the caller as its own case: a
/// package that stopped appearing is either no longer app-local (which changes
/// what this guard means) or a rename, and both deserve a human rather than a
/// silent pass.
let resolvedVersion (depsPath: string) (package: string) : Version option =
    use doc = JsonDocument.Parse(File.ReadAllText depsPath)

    let libraries =
        match doc.RootElement.TryGetProperty "libraries" with
        | true, libs -> libs.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
        | _ -> []

    libraries
    |> List.tryPick (fun name ->
        // `libraries` keys are `Package/Version`.
        match name.Split('/') with
        | [| id; version |] when String.Equals(id, package, StringComparison.OrdinalIgnoreCase) ->
            match Version.TryParse version with
            | true, v -> Some v
            | _ -> None
        | _ -> None)

let mutable failures = 0

let fail (message: string) =
    eprintfn "  FAIL %s" message
    failures <- failures + 1

let pass (message: string) = printfn "  PASS %s" message

printfn "Checking app-local MSBuild support assemblies (AUTOMATION-290)..."

match depsFiles () with
| [] ->
    // Not a pass. The guard had nothing to read, which is exactly the state in
    // which "no failures found" is meaningless.
    eprintfn "  FAIL no built FsHotWatch.Cli.deps.json found — build the CLI first, or this check proves nothing"
    exit 1
| deps ->
    let depsPath = List.head deps
    printfn "  reading %s" (Path.GetRelativePath(repoRoot, depsPath))

    for package, floor in floors do
        match resolvedVersion depsPath package with
        | Some resolved when resolved >= floor -> pass $"%s{package} %A{resolved} >= %A{floor}"
        | Some resolved ->
            fail
                $"%s{package} resolved to %A{resolved}, BELOW the known-good %A{floor}. \
                  This is the AUTOMATION-290 regression: MSBuild project loading throws \
                  'Method not found' for every project, discovery registers zero projects, \
                  and no test runs in the consuming repo. Do not publish this."
        | None ->
            fail
                $"%s{package} does not appear in the built deps.json at all. It is expected \
                  app-local (transitively, via Ionide.ProjInfo). Either it stopped shipping \
                  with the app — which changes what this guard covers — or it was renamed. \
                  Neither is safe to assume away."

    if failures = 0 then
        printfn ""
        printfn "App-local MSBuild support assemblies are at or above their known-good versions"
        exit 0
    else
        eprintfn ""
        eprintfn "%d check(s) failed" failures
        exit 1
