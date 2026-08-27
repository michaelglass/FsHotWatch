module FsHotWatch.Tests.StructureFilesTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-290: the packed CLI uses the exact restore and build checked by the dependency guard`` () =
    let workflow =
        File.ReadAllLines(
            Path.GetFullPath(
                Path.Combine(System.AppContext.BaseDirectory, "../../../../../.github/workflows/release.yml")
            )
        )
        |> Array.filter (fun line -> not (line.TrimStart().StartsWith("#")))

    let jobStarts =
        workflow
        |> Array.indexed
        |> Array.choose (fun (index, line) ->
            if
                line.StartsWith("  ")
                && not (line.StartsWith("    "))
                && line.TrimEnd().EndsWith(":")
            then
                Some(index, line.Trim().TrimEnd(':'))
            else
                None)

    let jobBlock name =
        let start = jobStarts |> Array.find (snd >> (=) name) |> fst

        let finish =
            jobStarts
            |> Array.tryFind (fun (index, _) -> index > start)
            |> Option.map fst
            |> Option.defaultValue workflow.Length

        workflow[start .. finish - 1] |> String.concat "\n"

    let releaseCli = jobBlock "release-cli"
    let publishCli = jobBlock "publish-cli"
    let build = "dotnet build src/FsHotWatch.Cli/FsHotWatch.Cli.fsproj"
    let check = "dotnet fsi scripts/check-msbuild-deps.fsx"

    // The reusable workflow restores once, runs this hook, then performs its Release
    // build with --no-restore and packs with --no-build. The hook's focused build must
    // therefore use that same assets file; otherwise a floating package can resolve
    // differently in the job whose artifact is actually published.
    test <@ releaseCli.Contains("pre-build-cmd:") @>
    test <@ releaseCli.Contains(build) @>
    test <@ releaseCli.Contains("-c Release") @>
    test <@ releaseCli.Contains("--no-restore") @>
    test <@ releaseCli.Contains("-p:ReleaseBuild=true") @>
    test <@ releaseCli.Contains(check) @>
    test <@ releaseCli.Contains("no-build-pack: true") @>
    test <@ releaseCli.IndexOf(build) < releaseCli.IndexOf(check) @>
    test <@ publishCli.Contains("needs: release-cli") @>

// ---------------------------------------------------------------------------
// AUTOMATION-303. THE list of files that decide what is compiled — the input every
// "did the tree's SHAPE change?" cache key reads.
//
// It existed twice and the copies disagreed, which is the failure mode the ticket is
// about wearing a different hat: two caches with two ideas of "structural" means one of
// them misses while the other replays, and the replaying one wins because it is the
// optimistic one.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: the structure list covers every MSBuild implicit import, not just Directory.Build.props`` () =
    // `Directory.Build.props` was the only one either copy knew about. The other two are
    // imported by MSBuild on exactly the same terms and can carry exactly the same
    // `<Compile Include=…>`, so a list with one of the three is a list that answers "no
    // structural change" to two thirds of the ways a repo makes one.
    test <@ StructureFiles.implicitImportNames |> List.contains "Directory.Build.props" @>
    test <@ StructureFiles.implicitImportNames |> List.contains "Directory.Build.targets" @>
    test <@ StructureFiles.implicitImportNames |> List.contains "Directory.Packages.props" @>

    // And the whole-repo walk sees BOTH halves — project files and implicit imports —
    // or a consumer that globs `allPatterns` silently inspects less than it claims.
    test <@ StructureFiles.allPatterns |> List.contains "*.fsproj" @>

    for name in StructureFiles.implicitImportNames do
        test <@ StructureFiles.allPatterns |> List.contains name @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: implicitImportsFor finds the NEAREST ancestor, MSBuild's own rule`` () =
    withTempDir "structure-nearest" (fun root ->
        let projDir = Path.Combine(root, "src", "Thing")
        Directory.CreateDirectory projDir |> ignore
        let proj = Path.Combine(projDir, "Thing.fsproj")
        File.WriteAllText(proj, "<Project />")

        let atRoot = Path.Combine(root, "Directory.Build.props")
        File.WriteAllText(atRoot, "<Project />")

        // Only the root copy exists, so it is the one MSBuild imports and the one we hash.
        let found = StructureFiles.implicitImportsFor proj
        test <@ found |> List.contains atRoot @>

        // Now a NEARER one appears. MSBuild's `GetPathOfFileAbove` stops at the first hit,
        // so this shadows the root copy — and hashing the shadowed one would make an edit
        // to a file the build never reads invalidate every project under it.
        let nearer = Path.Combine(projDir, "Directory.Build.props")
        File.WriteAllText(nearer, "<Project />")

        let shadowed = StructureFiles.implicitImportsFor proj
        test <@ shadowed |> List.contains nearer @>
        test <@ not (shadowed |> List.contains atRoot) @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303: implicitImportsFor ignores a props file in a SIBLING directory`` () =
    // THE POSITIVE CONTROL for the assertion above. "Does not contain" passes trivially
    // against a function that finds nothing at all, so the same call must be shown
    // FINDING something in the same tree: the sibling's copy is invisible while the
    // project's own is not.
    withTempDir "structure-sibling" (fun root ->
        let projDir = Path.Combine(root, "src", "Thing")
        let siblingDir = Path.Combine(root, "src", "Other")
        Directory.CreateDirectory projDir |> ignore
        Directory.CreateDirectory siblingDir |> ignore

        let proj = Path.Combine(projDir, "Thing.fsproj")
        File.WriteAllText(proj, "<Project />")

        let sibling = Path.Combine(siblingDir, "Directory.Build.props")
        File.WriteAllText(sibling, "<Project />")

        test <@ not (StructureFiles.implicitImportsFor proj |> List.contains sibling) @>

        // The control: the very same call DOES see the project's own copy.
        let own = Path.Combine(projDir, "Directory.Build.targets")
        File.WriteAllText(own, "<Project />")
        test <@ StructureFiles.implicitImportsFor proj |> List.contains own @>)
