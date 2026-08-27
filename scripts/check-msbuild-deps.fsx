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
/// WHY THE OTHER HALF OF 290 IS NOT ENOUGH. `Daemon.totalDiscoveryFailure` now
/// makes that state loud in seconds instead of an hour, and it is well covered.
/// But it is a SYMPTOM check: it fires in the consumer, after the bad package
/// shipped. This is the half that stops it shipping.
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
/// PRERELEASES ARE ORDERED, NOT MISREPORTED. `Version.TryParse` rejects
/// `18.5.0-preview.1`, and an earlier revision of this script turned that
/// rejection into "does not appear in the manifest at all" — a misdirecting
/// message, inside a guard whose whole subject is a misdirecting message. The
/// numeric core and the SemVer prerelease suffix are separated and both are used:
/// a prerelease of a LATER version clears the floor; a prerelease OF the floor
/// version does not, because SemVer orders it below and a preview of the build
/// that carries the fix is not a build that carries the fix.
///
/// THE GUARD PROVES IT CAN FAIL, ON EVERY RUN. A version check that cannot fail
/// is decoration, and one that silently stops matching is worse than nothing. So
/// `selfControls` drives the SAME decision function the real manifest goes
/// through, over synthetic manifests covering both directions, and a control that
/// does not land where it is declared to land aborts the run before the real
/// check is even attempted. A green below therefore means the guard worked, not
/// merely that it found nothing.
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

/// What a manifest says about one package. FOUR outcomes, kept apart on purpose:
/// each wants a different sentence, and collapsing any two of them is how a guard
/// starts lying about what it found.
[<RequireQualifiedAccess>]
type Lookup =
    /// Present and orderable. `Core` is the numeric part; `Prerelease` is the
    /// SemVer suffix after `-`, which orders BELOW the same core release.
    | Found of raw: string * core: Version * prerelease: string option
    /// Present, but the version string is not one this script can order.
    | Unparseable of raw: string
    /// Not in the manifest at all. NOT success — see `verdictFor`.
    | Absent

/// Split `18.5.0-preview.1+sha` into `18.5.0` and `preview.1`. Build metadata
/// (`+…`) carries no ordering in SemVer and is dropped.
let parseSemVer (raw: string) : (Version * string option) option =
    let withoutBuild =
        match raw.IndexOf('+') with
        | -1 -> raw
        | i -> raw.Substring(0, i)

    let core, prerelease =
        match withoutBuild.IndexOf('-') with
        | -1 -> withoutBuild, None
        | i -> withoutBuild.Substring(0, i), Some(withoutBuild.Substring(i + 1))

    match Version.TryParse core with
    | true, v -> Some(v, prerelease)
    | _ -> None

/// The resolved version of `package` in one deps file.
let resolvedVersion (depsPath: string) (package: string) : Lookup =
    use doc = JsonDocument.Parse(File.ReadAllText depsPath)

    let libraries =
        match doc.RootElement.TryGetProperty "libraries" with
        | true, libs -> libs.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
        | _ -> []

    let raw =
        libraries
        |> List.tryPick (fun name ->
            // `libraries` keys are `Package/Version`.
            match name.Split('/') with
            | [| id; version |] when String.Equals(id, package, StringComparison.OrdinalIgnoreCase) -> Some version
            | _ -> None)

    match raw with
    | None -> Lookup.Absent
    | Some raw ->
        match parseSemVer raw with
        | Some(core, prerelease) -> Lookup.Found(raw, core, prerelease)
        | None -> Lookup.Unparseable raw

let private regressionConsequence =
    "MSBuild project loading throws 'Method not found' for every project, discovery registers \
     zero projects, and no test runs in the consuming repo. Do not publish this."

/// THE DECISION. `Ok` = the shipped assembly clears the floor; `Error` = it does
/// not, or the manifest did not let us find out. Taken as a pure function of the
/// lookup so the self-controls below exercise the real rule rather than a
/// re-implementation of it that could agree with the code and disagree with
/// reality.
let verdictFor (package: string) (floor: Version) (lookup: Lookup) : Result<string, string> =
    match lookup with
    // A release at or above the floor, or a PRERELEASE of a strictly later
    // version — `18.5.0-preview.1` carries everything `18.4.0` did.
    | Lookup.Found(raw, core, None) when core >= floor -> Ok $"%s{package} %s{raw} >= %A{floor}"
    | Lookup.Found(raw, core, Some pre) when core > floor ->
        Ok $"%s{package} %s{raw} >= %A{floor} (prerelease `%s{pre}` of a later version)"
    // A prerelease OF the floor version is not the floor version.
    | Lookup.Found(raw, core, Some pre) when core = floor ->
        Error
            $"%s{package} resolved to %s{raw} — prerelease `%s{pre}` of the known-good %A{floor}, \
              which SemVer orders BELOW it. A preview of the version that carries the fix is not \
              proof of the fix. %s{regressionConsequence}"
    | Lookup.Found(raw, _, _) ->
        Error
            $"%s{package} resolved to %s{raw}, BELOW the known-good %A{floor}. This is the \
              AUTOMATION-290 regression: %s{regressionConsequence}"
    // Its own sentence. Reporting an unreadable version as an absent package
    // sends the reader looking for a rename that did not happen.
    | Lookup.Unparseable raw ->
        Error
            $"%s{package} is in the manifest as `%s{raw}`, which this check cannot order against \
              %A{floor}. That is a bug in the check, or a version scheme it has never seen — \
              either way it has proved nothing, and a guard that proved nothing must not pass."
    | Lookup.Absent ->
        Error
            $"%s{package} does not appear in the built deps.json at all. It is expected app-local \
              (transitively, via Ionide.ProjInfo). Either it stopped shipping with the app — which \
              changes what this guard covers — or it was renamed. Neither is safe to assume away."

// =============================================================================
// Self-controls — run BEFORE the real check, on every invocation
// =============================================================================

/// A minimal `deps.json` carrying exactly the `libraries` entries given. Written
/// to a temp file so the controls exercise the real JSON reader and the real
/// `libraries` key shape, not a hand-built `Lookup`.
let private syntheticManifest (libraries: (string * string) list) : string =
    let entries =
        libraries
        |> List.map (fun (id, version) -> $"    \"%s{id}/%s{version}\": {{ \"type\": \"package\" }}")
        |> String.concat ",\n"

    let unique = Guid.NewGuid().ToString("N")
    let path = Path.Combine(Path.GetTempPath(), $"fshw-a290-control-%s{unique}.json")
    File.WriteAllText(path, "{\n  \"libraries\": {\n" + entries + "\n  }\n}\n")
    path

/// `(description, libraries, expected-to-pass)`. The package under control is
/// always the first floor's, so adding a floor never silently leaves the controls
/// describing a package nobody checks.
let private controls =
    let pkg = fst (List.head floors)

    [ "the known-good release", [ (pkg, "18.4.0") ], true
      "a later release", [ (pkg, "19.0.1") ], true
      // THE DEFECT THIS REVISION FIXES: a legitimate prerelease upgrade used to
      // fail the version parse and be reported as absent from the manifest.
      "a prerelease of a later version", [ (pkg, "18.5.0-preview.1.25") ], true
      "build metadata on a later release", [ (pkg, "18.6.0+abc123") ], true
      // THE REGRESSION ITSELF, read out of the alpha that took the gate down.
      "the version that broke the gate", [ (pkg, "17.14.28") ], false
      "a prerelease OF the floor version", [ (pkg, "18.4.0-preview.1") ], false
      "an unorderable version string", [ (pkg, "not.a.version") ], false
      "the package missing entirely", [ ("Some.Other.Package", "1.0.0") ], false ]

/// Every control that did not land where it was declared to land.
let private failedControls () =
    let package, floor = List.head floors

    controls
    |> List.choose (fun (description, libraries, shouldPass) ->
        let path = syntheticManifest libraries

        try
            let actual = verdictFor package floor (resolvedVersion path package)

            match actual, shouldPass with
            | Ok _, true
            | Error _, false -> None
            | Ok message, false -> Some $"  %s{description}: expected a FAILURE, got a pass — %s{message}"
            | Error message, true -> Some $"  %s{description}: expected a PASS, got a failure — %s{message}"
        finally
            try
                File.Delete path
            with _ ->
                ())

// =============================================================================
// The check
// =============================================================================

let mutable failures = 0

let fail (message: string) =
    eprintfn "  FAIL %s" message
    failures <- failures + 1

let pass (message: string) = printfn "  PASS %s" message

printfn "Checking app-local MSBuild support assemblies (AUTOMATION-290)..."

match failedControls () with
| _ :: _ as broken ->
    // Abort BEFORE reading the real manifest. A guard whose own controls have
    // stopped agreeing with it cannot be trusted to report on anything else, and
    // running it anyway would produce a green that means nothing.
    eprintfn "  FAIL the self-controls no longer describe this check's behaviour:"
    broken |> List.iter (eprintfn "%s")
    eprintfn ""
    eprintfn "Fix the rule or fix the control — do NOT relax the control to match a new rule"
    eprintfn "without saying, in the control's own description, what the new rule is."
    exit 1
| [] -> printfn "  PASS %d self-control(s): this check fails on a downgrade and passes on an upgrade" (List.length controls)

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
        match verdictFor package floor (resolvedVersion depsPath package) with
        | Ok message -> pass message
        | Error message -> fail message

    if failures = 0 then
        printfn ""
        printfn "App-local MSBuild support assemblies are at or above their known-good versions"
        exit 0
    else
        eprintfn ""
        eprintfn "%d check(s) failed" failures
        exit 1
