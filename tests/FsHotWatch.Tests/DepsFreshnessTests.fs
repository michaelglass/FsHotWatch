/// Tests for FsHotWatch.DepsFreshness — the deps-freshness gate. Covers the
/// pure freshness comparator, the dep-file enumeration (ancestor walk), and the
/// detect→recover→revalidate orchestration with an injected restore runner so
/// the success / fail-fast / no-loop branches are exercised without shelling out
/// or invoking FCS.
module FsHotWatch.Tests.DepsFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.DepsFreshness
open FsHotWatch.ErrorLedger
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginHost
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

// ---- pure comparator ----

[<Fact(Timeout = 5000)>]
let ``compareFreshness: assets newer than all dep files is Fresh`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)

    let deps =
        [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
          DateTime(2026, 6, 2, 11, 0, 0, DateTimeKind.Utc) ]

    test <@ compareFreshness (Some assets) deps = Fresh @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: assets older than one dep file is Stale`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)

    let deps =
        [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
          DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc) ]

    test <@ compareFreshness (Some assets) deps = Stale @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: missing assets is Stale`` () =
    let deps = [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) ]
    test <@ compareFreshness None deps = Stale @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: no dep files is Fresh`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)
    test <@ compareFreshness (Some assets) [] = Fresh @>

// ---- dependency-file enumeration ----

let private touch (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "x")

[<Fact(Timeout = 5000)>]
let ``dependencyFiles: includes own fsproj and ancestor props`` () =
    withTempDir "deps-enum" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, "Directory.Packages.props"))

        let found = dependencyFiles fsproj root |> List.map Path.GetFileName |> Set.ofList

        test <@ found.Contains "Proj.fsproj" @>
        test <@ found.Contains "Directory.Packages.props" @>)

[<Fact(Timeout = 5000)>]
let ``dependencyFiles: excludes .config/dotnet-tools.json (not a package-graph input)`` () =
    withTempDir "deps-no-tools" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, ".config", "dotnet-tools.json"))

        let found = dependencyFiles fsproj root |> List.map Path.GetFileName |> Set.ofList

        test <@ not (found.Contains "dotnet-tools.json") @>)

/// Regression for the production false-stale: bumping `.config/dotnet-tools.json`
/// (a dotnet-tool version bump) must NOT make a project look stale. The assets
/// are newer than every real dependency input but older than a freshly-touched
/// tools manifest; the project must still evaluate Fresh.
[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: assets older than dotnet-tools.json but newer than real deps is Fresh`` () =
    withTempDir "deps-tools-bump" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        let assets = Path.Combine(projDir, "obj", "project.assets.json")
        let toolsJson = Path.Combine(root, ".config", "dotnet-tools.json")

        // Real dep inputs first, then assets (so assets are newer than all of them).
        touch fsproj
        touch (Path.Combine(root, "Directory.Packages.props"))
        touch assets

        // Now bump the tools manifest so it is the newest file of all.
        System.Threading.Thread.Sleep 10
        touch toolsJson
        test <@ File.GetLastWriteTimeUtc toolsJson > File.GetLastWriteTimeUtc assets @>

        test <@ detectProjectFreshness root fsproj = Fresh @>)

[<Fact(Timeout = 5000)>]
let ``dependencyFiles: nearest Directory.Build.props wins, no double-count`` () =
    withTempDir "deps-nearest" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        // Two levels both have the file; only the nearest (projDir) should count.
        touch (Path.Combine(root, "Directory.Build.props"))
        touch (Path.Combine(projDir, "Directory.Build.props"))

        let buildProps =
            dependencyFiles fsproj root
            |> List.filter (fun f -> Path.GetFileName f = "Directory.Build.props")

        test <@ buildProps.Length = 1 @>

        test
            <@
                Path.GetFullPath(List.head buildProps) = Path.GetFullPath(
                    Path.Combine(projDir, "Directory.Build.props")
                )
            @>)

// ---- tools manifest (restore-runner probe, NOT a freshness dep) ----

[<Fact(Timeout = 5000)>]
let ``toolsManifest: finds nearest .config/dotnet-tools.json in the ancestor chain`` () =
    withTempDir "deps-tools-find" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        let toolsJson = Path.Combine(root, ".config", "dotnet-tools.json")
        touch toolsJson

        match toolsManifest fsproj root with
        | Some found -> test <@ Path.GetFullPath found = Path.GetFullPath toolsJson @>
        | None -> failwith "expected the tools manifest to be found")

[<Fact(Timeout = 5000)>]
let ``toolsManifest: nearest wins when present at multiple levels`` () =
    withTempDir "deps-tools-nearest" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, ".config", "dotnet-tools.json"))
        let nearest = Path.Combine(projDir, ".config", "dotnet-tools.json")
        touch nearest

        match toolsManifest fsproj root with
        | Some found -> test <@ Path.GetFullPath found = Path.GetFullPath nearest @>
        | None -> failwith "expected the tools manifest to be found")

[<Fact(Timeout = 5000)>]
let ``toolsManifest: None when no manifest exists`` () =
    withTempDir "deps-tools-none" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        test <@ (toolsManifest fsproj root).IsNone @>)

/// Defensive branch: when the project is NOT under `repoRoot`, the ancestor walk
/// climbs to the filesystem root instead of stopping at repoRoot. It must still
/// terminate (not loop) and find an ancestor manifest, exercising the
/// IsNullOrEmpty/parent==dir sentinel that the under-root happy path skips.
[<Fact(Timeout = 5000)>]
let ``toolsManifest: project not under repoRoot still walks to filesystem root and terminates`` () =
    withTempDir "deps-tools-unrooted" (fun projParent ->
        let projDir = Path.Combine(projParent, "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        // A manifest right next to the project so the walk finds something.
        let toolsJson = Path.Combine(projDir, ".config", "dotnet-tools.json")
        touch toolsJson

        // repoRoot is a sibling directory the project is NOT under, forcing the
        // walk past it up to the filesystem root.
        let unrelatedRoot = Path.Combine(projParent, "elsewhere")
        Directory.CreateDirectory unrelatedRoot |> ignore

        match toolsManifest fsproj unrelatedRoot with
        | Some found -> test <@ Path.GetFullPath found = Path.GetFullPath toolsJson @>
        | None -> failwith "expected the tools manifest to be found via the FS-root walk")

// ---- disk-backed probe ----

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: missing assets is Stale`` () =
    withTempDir "deps-probe-missing" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        test <@ detectProjectFreshness root fsproj = Stale @>)

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: assets newer than fsproj is Fresh`` () =
    withTempDir "deps-probe-fresh" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        let assets = assetsPath fsproj
        touch assets
        // Make assets strictly newer than the fsproj.
        File.SetLastWriteTimeUtc(fsproj, DateTime.UtcNow.AddMinutes(-5.0))
        File.SetLastWriteTimeUtc(assets, DateTime.UtcNow)
        test <@ detectProjectFreshness root fsproj = Fresh @>)

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: assets older than fsproj is Stale`` () =
    withTempDir "deps-probe-stale" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        let assets = assetsPath fsproj
        touch assets
        File.SetLastWriteTimeUtc(assets, DateTime.UtcNow.AddMinutes(-5.0))
        File.SetLastWriteTimeUtc(fsproj, DateTime.UtcNow)
        test <@ detectProjectFreshness root fsproj = Stale @>)

// ---- content-aware dep-relevant signature (mtime is not a content oracle) ----

/// A minimal SDK-style fsproj body parameterized on the sole PackageReference
/// version and the list of Compile items, so tests can vary the dep-relevant and
/// the source-item axes independently.
let private fsprojXml (pkgVersion: string) (compiles: string list) =
    let items =
        compiles
        |> List.map (fun c -> $"""    <Compile Include="%s{c}" />""")
        |> String.concat "\n"

    $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
%s{items}
    <PackageReference Include="FSharp.Core" Version="%s{pkgVersion}" />
  </ItemGroup>
</Project>
"""

/// The crux of the fix: a compile-item-only edit (adding/moving `<Compile>`
/// entries — which does NOT touch the package graph) leaves the dep-relevant
/// signature UNCHANGED. That invariance is what suppresses the phantom stale.
[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: compile-item-only change yields the SAME signature`` () =
    withTempDir "deps-sig-compile" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore

        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "A.fs" ])
        let before = depRelevantSignature root fsproj

        // Add + reorder Compile items only; the package graph is untouched.
        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "B.fs"; "A.fs"; "C.fs" ])
        let after = depRelevantSignature root fsproj

        test <@ before = after @>)

/// A real PackageReference change DOES move the signature (re-arms recovery).
[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: PackageReference version change yields a DIFFERENT signature`` () =
    withTempDir "deps-sig-pkg" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore

        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "A.fs" ])
        let before = depRelevantSignature root fsproj

        File.WriteAllText(fsproj, fsprojXml "9.0.0" [ "A.fs" ])
        let after = depRelevantSignature root fsproj

        test <@ before <> after @>)

/// mtime is NOT a content oracle (rsync -a / cp -p / git checkout restore an OLD
/// mtime while changing CONTENT): the signature must reflect ancestor dep-file
/// CONTENT so a preserved-mtime rewrite of `paket.lock` still re-arms recovery.
[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: differs when a dep file's content changes but mtime is preserved (rsync -a)`` () =
    withTempDir "deps-sig-content" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore
        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "A.fs" ])
        let lockFile = Path.Combine(root, "paket.lock")
        File.WriteAllText(lockFile, "NUGET\n  remote: x\n    PackageA (1.0)\n")

        // Pin both files' mtimes so a content rewrite can preserve them exactly.
        let pinned = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)
        File.SetLastWriteTimeUtc(fsproj, pinned)
        File.SetLastWriteTimeUtc(lockFile, pinned)

        let before = depRelevantSignature root fsproj

        // Rewrite content, then restore the EXACT old mtime (what rsync -a does).
        File.WriteAllText(lockFile, "NUGET\n  remote: x\n    PackageA (2.0)\n")
        File.SetLastWriteTimeUtc(lockFile, pinned)
        test <@ File.GetLastWriteTimeUtc lockFile = pinned @>

        let after = depRelevantSignature root fsproj
        test <@ before <> after @>)

[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: stable across repeated computes when nothing changes`` () =
    withTempDir "deps-sig-stable" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore
        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "A.fs" ])
        File.WriteAllText(Path.Combine(root, "paket.lock"), "lock-content")

        let first = depRelevantSignature root fsproj
        let second = depRelevantSignature root fsproj
        test <@ first = second @>)

/// Old-style (MSBuild-namespaced, no `Sdk` attr) fsproj with an `<Import>` and a
/// `<PackageReference>` carrying child metadata: canonicalization must be
/// namespace-agnostic (LocalName) AND descend into child elements, so a change to
/// the child metadata (`<PrivateAssets>`) still moves the signature. Exercises the
/// no-Sdk-attr branch, the `<Import>` element, and the recursive child/text walk.
[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: old-style project captures Import + PackageReference child metadata`` () =
    withTempDir "deps-sig-oldstyle" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore

        let xml privateAssets =
            $"""<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="..\Shared.props" />
  <ItemGroup>
    <Compile Include="A.fs" />
    <PackageReference Include="X" Version="1.0">
      <PrivateAssets>%s{privateAssets}</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
"""

        File.WriteAllText(fsproj, xml "all")
        let before = depRelevantSignature root fsproj

        // Change ONLY the child metadata → the signature must move.
        File.WriteAllText(fsproj, xml "none")
        let afterMetadata = depRelevantSignature root fsproj

        test <@ before <> afterMetadata @>

        // Restore the metadata but reorder the (excluded) Compile item → SAME sig.
        File.WriteAllText(
            fsproj,
            """<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="..\Shared.props" />
  <ItemGroup>
    <PackageReference Include="X" Version="1.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <Compile Include="Z.fs" />
    <Compile Include="A.fs" />
  </ItemGroup>
</Project>
"""
        )

        test <@ depRelevantSignature root fsproj = before @>)

/// A malformed fsproj must never throw — the digest folds a content-derived
/// sentinel, and fixing the XML (a content change) still moves the signature.
[<Fact(Timeout = 5000)>]
let ``depRelevantSignature: unparseable fsproj folds a sentinel and re-arms on fix`` () =
    withTempDir "deps-sig-bad" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        Directory.CreateDirectory projDir |> ignore

        File.WriteAllText(fsproj, "<Project><not-closed>")
        let broken = depRelevantSignature root fsproj // must not throw

        File.WriteAllText(fsproj, fsprojXml "8.0.0" [ "A.fs" ])
        let fixed_ = depRelevantSignature root fsproj

        test <@ broken <> fixed_ @>)

// ---- orchestration: evaluateProject ----

let private sigZero (_: string) = "sig-0"
let private assetsYes (_: string) = true
let private assetsNo (_: string) = false
// These fakes stand in for a REAL restore whose output we fully drained — hence
// `ProcessOutput.Drained`. A fake may never hand back a capture it claims to have
// measured but did not; that is the conflation AUTOMATION-126 removed.
let private succeedingRunner: RestoreRunner =
    fun _ -> Succeeded(ProcessOutput.Drained "restored")

let private failingRunner: RestoreRunner =
    fun _ -> Failed(1, ProcessOutput.Drained "NU1101: package not found")

[<Fact(Timeout = 5000)>]
let ``evaluateProject: fresh project proceeds without restore`` () =
    let mutable ran = false

    let runner: RestoreRunner =
        fun _ ->
            ran <- true
            Succeeded(ProcessOutput.Drained "")

    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Fresh) sigZero assetsYes runner tracker "P.fsproj"

    test <@ result = Proceed @>
    test <@ not ran @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: stale then successful restore (assets present) -> RecoveredOk`` () =
    // A successful restore is trusted even if the mtime probe would still read
    // stale (no-op restore doesn't bump assets mtime); only assets *presence*
    // is required post-success.
    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsYes succeedingRunner tracker "P.fsproj"

    test <@ result = RecoveredOk @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: stale and restore fails -> FailFast with one message`` () =
    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsYes failingRunner tracker "P.fsproj"

    match result with
    | FailFast(msg, detail) ->
        test <@ msg.Contains "stale" @>
        test <@ msg.Contains "P.fsproj" @>
        test <@ detail.Contains "NU1101" @>
    | other -> failwithf "expected FailFast, got %A" other

[<Fact(Timeout = 5000)>]
let ``evaluateProject: restore succeeds but assets still missing -> FailFast`` () =
    let tracker = RecoveryTracker()
    // Restore "succeeds" but assets are still absent on disk → genuine failure.
    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsNo succeedingRunner tracker "P.fsproj"

    match result with
    | FailFast(msg, _) -> test <@ msg.Contains "still missing" @>
    | other -> failwithf "expected FailFast, got %A" other

[<Fact(Timeout = 5000)>]
let ``evaluateProject: still-stale second cycle does not re-run restore (no loop)`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Failed(1, ProcessOutput.Drained "still broken")

    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    let first = evaluateProject (fun _ -> Stale) sigZero assetsYes runner tracker proj
    let second = evaluateProject (fun _ -> Stale) sigZero assetsYes runner tracker proj

    test <@ runs = 1 @>

    match first with
    | FailFast _ -> ()
    | other -> failwithf "expected FailFast first, got %A" other

    test <@ second = SkipAlreadyAttempted @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: a new dep bump re-arms recovery`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Failed(1, ProcessOutput.Drained "broken")

    // Signature changes between the two attempts (a fresh bump).
    let sigs = System.Collections.Generic.Queue<string>([ "sig-1"; "sig-2" ])
    let signatureOf (_: string) = sigs.Dequeue()

    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    evaluateProject (fun _ -> Stale) signatureOf assetsYes runner tracker proj
    |> ignore

    evaluateProject (fun _ -> Stale) signatureOf assetsYes runner tracker proj
    |> ignore

    test <@ runs = 2 @>

/// Content-drift escape hatch: the mtime probe can be fooled into reporting
/// Fresh by a preserved-mtime dep rewrite (rsync -a). When the dep-content
/// signature has drifted from the last value recovery proceeded on, the project
/// must be treated as Stale and re-restored, NOT silently proceeded.
[<Fact(Timeout = 5000)>]
let ``evaluateProject: mtime-Fresh but content signature drifted -> restores (not silently Proceed)`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Succeeded(ProcessOutput.Drained "restored")

    // Two cycles: cycle 1 establishes the fresh baseline; cycle 2 has the SAME
    // mtime-Fresh probe verdict but a DIFFERENT content signature (preserved-mtime
    // rewrite) and must re-restore rather than Proceed blindly.
    let sigs = System.Collections.Generic.Queue<string>([ "fresh-A"; "fresh-B" ])
    let signatureOf (_: string) = sigs.Dequeue()
    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    let first =
        evaluateProject (fun _ -> Fresh) signatureOf assetsYes runner tracker proj

    let second =
        evaluateProject (fun _ -> Fresh) signatureOf assetsYes runner tracker proj

    // First cycle: genuinely fresh, no restore.
    test <@ first = Proceed @>
    // Second cycle: same mtime verdict, drifted content -> recovery runs.
    test <@ runs = 1 @>
    test <@ second = RecoveredOk @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: mtime-Fresh with unchanged content signature stays Proceed (no restore loop)`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Succeeded(ProcessOutput.Drained "restored")

    let signatureOf (_: string) = "stable-sig"
    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    let first =
        evaluateProject (fun _ -> Fresh) signatureOf assetsYes runner tracker proj

    let second =
        evaluateProject (fun _ -> Fresh) signatureOf assetsYes runner tracker proj

    test <@ first = Proceed @>
    test <@ second = Proceed @>
    test <@ runs = 0 @>

/// The compile-item-only false-positive this fix targets: once a project has been
/// observed Fresh (baseline recorded), a compile-item-only fsproj edit bumps the
/// fsproj mtime so the probe reads Stale — but the dep-relevant signature is
/// UNCHANGED. evaluateProject must Proceed WITHOUT invoking the restore runner
/// (no phantom stale→restore→timeout→pinned-red).
[<Fact(Timeout = 5000)>]
let ``evaluateProject: mtime-Stale but dep signature matches baseline -> Proceed with NO restore`` () =
    let mutable ran = false

    let runner: RestoreRunner =
        fun _ ->
            ran <- true
            Succeeded(ProcessOutput.Drained "restored")

    let sigStable (_: string) = "dep-sig-stable"
    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    // Cycle 1: genuinely Fresh establishes the known-good baseline.
    let first = evaluateProject (fun _ -> Fresh) sigStable assetsYes runner tracker proj

    // Cycle 2: a compile-item edit bumped the mtime (probe now Stale) but the
    // dep-relevant signature is unchanged → suppressed, no restore.
    let second =
        evaluateProject (fun _ -> Stale) sigStable assetsYes runner tracker proj

    test <@ first = Proceed @>
    test <@ second = Proceed @>
    test <@ not ran @>

/// The inverse: a real dependency change (the dep signature moved) on a Stale
/// probe is NOT suppressed — recovery runs.
[<Fact(Timeout = 5000)>]
let ``evaluateProject: mtime-Stale with CHANGED dep signature -> restores (not suppressed)`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Succeeded(ProcessOutput.Drained "restored")

    // Cycle 1 fresh baseline at dep-A; cycle 2 has a real dep change to dep-B.
    let sigs = System.Collections.Generic.Queue<string>([ "dep-A"; "dep-B" ])
    let signatureOf (_: string) = sigs.Dequeue()
    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    let first =
        evaluateProject (fun _ -> Fresh) signatureOf assetsYes runner tracker proj

    let second =
        evaluateProject (fun _ -> Stale) signatureOf assetsYes runner tracker proj

    test <@ first = Proceed @>
    test <@ runs = 1 @>
    test <@ second = RecoveredOk @>

/// A cold first sighting (no baseline yet) that is Stale must still restore — the
/// suppression only fires against a previously-recorded known-good baseline.
[<Fact(Timeout = 5000)>]
let ``evaluateProject: Stale on first sighting (no baseline) still restores`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Succeeded(ProcessOutput.Drained "restored")

    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Stale) (fun _ -> "dep-sig") assetsYes runner tracker "P.fsproj"

    test <@ runs = 1 @>
    test <@ result = RecoveredOk @>

// ---- daemon-level gate wiring: applyDepsGate ----

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: None gate always proceeds and reports nothing`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proceed = applyDepsGate None host "/r/P.fsproj"

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: Proceed clears any prior deps diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"
    // Seed a stale diagnostic, then a fresh gate result should clear it.
    host.ReportErrors(pluginName, proj, [ ErrorEntry.error "old" ])

    let proceed = applyDepsGate (Some(fun _ -> Proceed)) host proj

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: FailFast reports one error, skips FCS, verdict non-zero`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"

    let proceed =
        applyDepsGate (Some(fun _ -> FailFast("deps stale for P.fsproj", "NU1101"))) host proj

    test <@ not proceed @>

    let byPlugin = host.GetErrorsByPlugin pluginName
    let entries = byPlugin |> Map.tryFind proj |> Option.defaultValue []
    test <@ entries.Length = 1 @>
    test <@ host.HasFailingReasons false @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: SkipAlreadyAttempted skips FCS without adding a new diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proceed = applyDepsGate (Some(fun _ -> SkipAlreadyAttempted)) host "/r/P.fsproj"

    test <@ not proceed @>
    // No new diagnostic is added on this path (a prior one, if any, is retained).
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: RecoveredOk proceeds and clears prior diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"
    host.ReportErrors(pluginName, proj, [ ErrorEntry.error "old" ])

    let proceed = applyDepsGate (Some(fun _ -> RecoveredOk)) host proj

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

// ---- paketGroupsFromLock (drives the per-group, walk-free paket restore) ----

[<Fact(Timeout = 5000)>]
let ``paketGroupsFromLock: lock with no GROUP headers yields just Main`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-groups-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let lockPath = Path.Combine(dir, "paket.lock")

    try
        File.WriteAllText(lockPath, "NUGET\n  remote: https://api.nuget.org/v3/index.json\n    FSharp.Core (8.0.0)\n")
        test <@ paketGroupsFromLock lockPath = [ "Main" ] @>
    finally
        Directory.Delete(dir, true)

[<Fact(Timeout = 5000)>]
let ``paketGroupsFromLock: explicit GROUP headers are returned after Main`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-groups-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let lockPath = Path.Combine(dir, "paket.lock")

    try
        File.WriteAllText(
            lockPath,
            "NUGET\n  remote: r\n    A (1.0)\nGROUP Build\nNUGET\n  remote: r\n    B (2.0)\nGROUP Test\nNUGET\n    C (3.0)\n"
        )

        test <@ paketGroupsFromLock lockPath = [ "Main"; "Build"; "Test" ] @>
    finally
        Directory.Delete(dir, true)

[<Fact(Timeout = 5000)>]
let ``paketGroupsFromLock: missing lock falls back to Main`` () =
    test <@ paketGroupsFromLock "/no/such/dir/paket.lock" = [ "Main" ] @>

// ---- restoreSteps (pure restore-step composition extracted from the runner) ----
//
// These pin the branchy "which steps, in what order, with what args" decision
// that productionRestoreRunner used to fold into its shell-out loop — now
// unit-testable without invoking `dotnet`/`paket`. Real paket.lock-shaped content
// drives the per-group enumeration.

/// A `paket.lock` body with the given explicit GROUP headers (Main is implicit).
let private lockWithGroups (groups: string list) =
    let mainBody =
        "NUGET\n  remote: https://api.nuget.org/v3/index.json\n    FSharp.Core (8.0.0)\n"

    let groupBodies =
        groups
        |> List.map (fun g -> $"GROUP %s{g}\nNUGET\n  remote: r\n    Pkg{g} (1.0)\n")
        |> String.concat ""

    mainBody + groupBodies

[<Fact(Timeout = 5000)>]
let ``restoreSteps: no-paket project yields just the dotnet restore step`` () =
    withTempDir "deps-steps-nopaket" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj

        let steps = restoreSteps root fsproj

        test <@ steps |> List.map (fun s -> s.Purpose) = [ "restore" ] @>
        // The lone step restores the fsproj from the project directory.
        let only = List.exactlyOne steps
        test <@ only.Args = $"restore \"%s{fsproj}\"" @>
        test <@ Path.GetFullPath only.WorkingDir = Path.GetFullPath projDir @>)

[<Fact(Timeout = 5000)>]
let ``restoreSteps: paket project with Main+Build groups yields per-group steps in order`` () =
    withTempDir "deps-steps-groups" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, "paket.dependencies"))
        File.WriteAllText(Path.Combine(root, "paket.lock"), lockWithGroups [ "Build" ])

        let steps = restoreSteps root fsproj

        // dotnet restore first, then one paket-restore per group (Main, then Build).
        test <@ steps |> List.map (fun s -> s.Purpose) = [ "restore"; "paket-restore"; "paket-restore" ] @>

        let paketArgs =
            steps
            |> List.filter (fun s -> s.Purpose = "paket-restore")
            |> List.map (fun s -> s.Args)

        test <@ paketArgs = [ "paket restore --group Main"; "paket restore --group Build" ] @>)

[<Fact(Timeout = 5000)>]
let ``restoreSteps: paket.dependencies present but lock missing falls back to Main only`` () =
    withTempDir "deps-steps-nolock" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        // paket.dependencies in scope, but NO paket.lock → just the Main group.
        touch (Path.Combine(root, "paket.dependencies"))

        let steps = restoreSteps root fsproj

        let paketArgs =
            steps
            |> List.filter (fun s -> s.Purpose = "paket-restore")
            |> List.map (fun s -> s.Args)

        test <@ paketArgs = [ "paket restore --group Main" ] @>)

[<Fact(Timeout = 5000)>]
let ``restoreSteps: tools manifest in scope appends a tool-restore step last`` () =
    withTempDir "deps-steps-tool" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, ".config", "dotnet-tools.json"))

        let steps = restoreSteps root fsproj

        // No paket here → restore then tool-restore, with tool-restore LAST.
        test <@ steps |> List.map (fun s -> s.Purpose) = [ "restore"; "tool-restore" ] @>
        let last = List.last steps
        test <@ last.Purpose = "tool-restore" @>
        test <@ last.Args = "tool restore" @>)

[<Fact(Timeout = 5000)>]
let ``restoreSteps: full stack (paket groups + tools) orders restore, per-group paket, then tool-restore`` () =
    withTempDir "deps-steps-full" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, "paket.dependencies"))
        File.WriteAllText(Path.Combine(root, "paket.lock"), lockWithGroups [ "Build"; "Test" ])
        touch (Path.Combine(root, ".config", "dotnet-tools.json"))

        let steps = restoreSteps root fsproj

        test
            <@
                steps |> List.map (fun s -> s.Purpose) = [ "restore"
                                                           "paket-restore" // Main
                                                           "paket-restore" // Build
                                                           "paket-restore" // Test
                                                           "tool-restore" ]
            @>

        // Every step runs `dotnet <args>` from the project directory.
        test
            <@
                steps
                |> List.forall (fun s -> Path.GetFullPath s.WorkingDir = Path.GetFullPath projDir)
            @>)
