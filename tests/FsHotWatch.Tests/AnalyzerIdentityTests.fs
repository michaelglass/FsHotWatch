/// the analyzer cache key identifies a first-party analyzer by the
/// compiler's receipt (the portable PDB's document checksums), not by DLL bytes that
/// fsc salts with the PDB's absolute path. The subject throughout is the repository's
/// own house-rules assembly, built from `analyzers/FsHotWatch.Rules` by the same
/// `dotnet build` that produced this test host.
module FsHotWatch.Tests.AnalyzerIdentityTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Analyzers.AnalyzerIdentity
open FsHotWatch.Tests.TestHelpers

open FsHotWatch.Tests.AnalyzerFixtures


let private throwawayDll (dir: string) (name: string) (bytes: byte array) =
    let dll = Path.Combine(dir, $"%s{name}.dll")
    File.WriteAllBytes(dll, bytes)
    dll

// ---------------------------------------------------------------------------
// The enabling fact
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``fsc records a checksum for every source document in the portable PDB`` () =
    // Both the house-rules PDB and this test host's own: the property is the
    // compiler's, not one project's.
    let testHostPdb = Path.Combine(AppContext.BaseDirectory, "FsHotWatch.Tests.pdb")

    for pdb in [ Path.ChangeExtension(rulesDll, ".pdb"); testHostPdb ] do
        let sources =
            pdbDocuments pdb
            |> List.filter (fun (path, _) -> path.EndsWith(".fs", StringComparison.Ordinal))

        test <@ not (List.isEmpty sources) @>
        test <@ sources |> List.forall (fun (_, hash) -> hash.Length > 0) @>

// ---------------------------------------------------------------------------
// The cross-workspace property
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``the same rules built at two roots have one FirstParty identity, equal to the in-place one`` () =
    withTempDir "az-id-a" (fun rootA ->
        withTempDir "az-id-b" (fun rootB ->
            let dllA = relocate rootA
            let dllB = relocate rootB

            // Relocation is real: the recorded document paths point at the ORIGINAL
            // checkout, not at either copy, so equality below is the resolution rule
            // at work rather than a string match.
            let recorded = inRepoDocuments (Path.ChangeExtension(rulesDll, ".pdb"))

            test
                <@
                    recorded
                    |> List.forall (fun p -> not (p.StartsWith(rootA, StringComparison.Ordinal)))
                @>

            let inPlace = identityOf (Some repoRoot) rulesDll
            let atA = identityOf (Some rootA) dllA
            let atB = identityOf (Some rootB) dllB

            test <@ atA = atB @>
            test <@ atA = inPlace @>

            match inPlace with
            | Ok(AnalyzerAssemblyIdentity.FirstParty _) -> ()
            | other -> failwith $"expected FirstParty, got %A{other}"))

[<Fact(Timeout = 30000)>]
let ``a source byte changed after the build is DocumentDrift naming the file`` () =
    withTempDir "az-id-drift" (fun root ->
        let dll = relocate root

        let source =
            Path.Combine(root, "analyzers", "FsHotWatch.Rules", "ConventionAnalyzers.fs")

        let bytes = File.ReadAllBytes source
        bytes[0] <- bytes[0] ^^^ 1uy
        File.WriteAllBytes(source, bytes)

        match identityOf (Some root) dll with
        | Error(Refusal.DocumentDrift(file, recorded, actual)) ->
            test <@ file = source @>
            test <@ recorded <> actual @>
            test <@ actual = sha256Lower bytes @>
        | other -> failwith $"expected DocumentDrift, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``a project file newer than its output is OutputOlderThanProject`` () =
    withTempDir "az-id-stale" (fun root ->
        let dll = relocate root

        let project =
            Path.Combine(root, "analyzers", "FsHotWatch.Rules", "FsHotWatch.Rules.fsproj")

        File.SetLastWriteTimeUtc(project, File.GetLastWriteTimeUtc(dll).AddMinutes 1.0)

        test <@ identityOf (Some root) dll = Error(Refusal.OutputOlderThanProject(dll, project)) @>)

[<Fact(Timeout = 30000)>]
let ``a receipt naming this repository's sources with no project above the DLL is ProducerNotFound`` () =
    withTempDir "az-id-noproj" (fun dir ->
        // In place: the recorded documents ARE under the repository root, so the
        // receipt is this repository's — but nothing above the DLL produced it.
        let dll = Path.Combine(dir, Path.GetFileName rulesDll)
        File.Copy(rulesDll, dll)
        File.Copy(Path.ChangeExtension(rulesDll, ".pdb"), Path.ChangeExtension(dll, ".pdb"))

        test <@ identityOf (Some repoRoot) dll = Error(Refusal.ProducerNotFound dll) @>)

[<Fact(Timeout = 30000)>]
let ``a relocated receipt with no project to anchor it is a package, not a refusal`` () =
    withTempDir "az-id-noanchor" (fun root ->
        // Relocated: the recorded paths point at another checkout, and the producer
        // project is the only thing that could relate them to this root. Without it
        // the DLL looks exactly like a package that shipped its PDB.
        let dll = relocate root
        File.Delete(Path.Combine(root, "analyzers", "FsHotWatch.Rules", "FsHotWatch.Rules.fsproj"))

        test
            <@
                identityOf (Some root) dll = Ok(
                    AnalyzerAssemblyIdentity.PackageBytes(sha256Lower (File.ReadAllBytes dll))
                )
            @>)

[<Fact(Timeout = 30000)>]
let ``a first-party DLL whose PDB is gone is MissingPdb, not a byte digest`` () =
    withTempDir "az-id-nopdb" (fun dir ->
        // The DLL's CodeView entry places its PDB under THIS repository's root, so
        // the receipt is expected wherever the DLL now sits.
        let dll = Path.Combine(dir, Path.GetFileName rulesDll)
        File.Copy(rulesDll, dll)

        test <@ identityOf (Some repoRoot) dll = Error(Refusal.MissingPdb dll) @>)

[<Fact(Timeout = 30000)>]
let ``a sidecar PDB that is not the one the build wrote is PdbMismatch`` () =
    withTempDir "az-id-wrongpdb" (fun root ->
        let dll = relocate root
        // This test host's own PDB, wearing the analyzer's name: a stale sidecar.
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "FsHotWatch.Tests.pdb"),
            Path.ChangeExtension(dll, ".pdb"),
            true
        )

        test <@ identityOf (Some root) dll = Error(Refusal.PdbMismatch(dll, Path.ChangeExtension(dll, ".pdb"))) @>)

[<Fact(Timeout = 30000)>]
let ``a document the receipt names but the tree lacks is DocumentMissing`` () =
    withTempDir "az-id-missing" (fun root ->
        let dll = relocate root
        let source = Path.Combine(root, rulesSourceRel)
        File.Delete source

        test <@ identityOf (Some root) dll = Error(Refusal.DocumentMissing source) @>)

[<Fact(Timeout = 30000)>]
let ``two project files in the producer directory anchor nothing: a relocated receipt is a package`` () =
    withTempDir "az-id-twoproj" (fun root ->
        let dll = relocate root
        let project = Path.Combine(root, rulesProjectRel)
        File.Copy(project, Path.Combine(Path.GetDirectoryName project, "Other.fsproj"))

        test
            <@
                identityOf (Some root) dll = Ok(
                    AnalyzerAssemblyIdentity.PackageBytes(sha256Lower (File.ReadAllBytes dll))
                )
            @>)

[<Fact(Timeout = 30000)>]
let ``a checksum algorithm this module cannot recompute is UnverifiableChecksum`` () =
    withTempDir "az-id-alg" (fun root ->
        let dll = relocate root
        let unknown = Guid "0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f"
        recordChecksumAlgorithm root unknown

        match identityOf (Some root) dll with
        | Error(Refusal.UnverifiableChecksum(_, algorithm)) -> test <@ algorithm = unknown @>
        | other -> failwith $"expected UnverifiableChecksum, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``a SHA-1 receipt is recomputed as SHA-1, so a SHA-256 blob under it reads as drift`` () =
    withTempDir "az-id-sha1" (fun root ->
        let dll = relocate root
        recordChecksumAlgorithm root (Guid "ff1816ec-aa5e-4d10-87f7-6f4963833460")

        match identityOf (Some root) dll with
        | Error(Refusal.DocumentDrift(_, recorded, actual)) ->
            test <@ recorded.Length = 64 @>
            test <@ actual.Length = 40 @>
        | other -> failwith $"expected DocumentDrift, got %A{other}")

// ---------------------------------------------------------------------------
// Package bytes
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``bytes with no receipt are PackageBytes equal to their raw SHA-256`` () =
    withTempDir "az-id-pkg" (fun dir ->
        let bytes = [| 1uy; 2uy; 3uy |]
        let dll = throwawayDll dir "MyAnalyzer" bytes

        test <@ identityOf (Some dir) dll = Ok(AnalyzerAssemblyIdentity.PackageBytes(sha256Lower bytes)) @>)

[<Fact(Timeout = 30000)>]
let ``with no repository root every DLL is PackageBytes`` () =
    withTempDir "az-id-noroot" (fun dir ->
        let throwaway = throwawayDll dir "MyAnalyzer" [| 9uy; 8uy |]

        test <@ identityOf None throwaway = Ok(AnalyzerAssemblyIdentity.PackageBytes(sha256Lower [| 9uy; 8uy |])) @>

        test
            <@
                identityOf None rulesDll = Ok(
                    AnalyzerAssemblyIdentity.PackageBytes(sha256Lower (File.ReadAllBytes rulesDll))
                )
            @>)

// ---------------------------------------------------------------------------
// The set
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``ofPaths reports every refusal in the set and never throws`` () =
    withTempDir "az-id-set" (fun root ->
        let drifted = relocate root

        let source =
            Path.Combine(root, "analyzers", "FsHotWatch.Rules", "ConventionAnalyzers.fs")

        File.WriteAllText(source, "// not what was compiled\n")
        let absent = Path.Combine(root, "nowhere.dll")

        match ofPaths (Some root) [ absent; drifted; rulesDll ] with
        | Error refusals ->
            test <@ List.length refusals = 2 @>

            test
                <@
                    refusals
                    |> List.exists (function
                        | Refusal.Unreadable(dll, _) -> dll = absent
                        | _ -> false)
                @>

            test
                <@
                    refusals
                    |> List.exists (function
                        | Refusal.DocumentDrift(file, _, _) -> file = source
                        | _ -> false)
                @>
        | Ok _ -> failwith "expected refusals")

[<Fact(Timeout = 30000)>]
let ``ofPaths semantic is checkout-independent while materialization follows the bytes`` () =
    withTempDir "az-id-set-a" (fun rootA ->
        withTempDir "az-id-set-b" (fun rootB ->
            let dllA = relocate rootA
            let dllB = relocate rootB
            // A package DLL beside the first-party one, identical in both checkouts.
            let pkgA = throwawayDll (Path.GetDirectoryName dllA) "Third.Analyzer" [| 4uy; 5uy |]
            let pkgB = throwawayDll (Path.GetDirectoryName dllB) "Third.Analyzer" [| 4uy; 5uy |]

            let setA = ofPaths (Some rootA) [ dllA; pkgA ]
            let setB = ofPaths (Some rootB) [ dllB; pkgB ]

            match setA, setB with
            | Ok a, Ok b ->
                test <@ a.SemanticKey = b.SemanticKey @>
                test <@ a.MaterializationDigest = b.MaterializationDigest @>

                // A rebuilt (here: any) byte change in the package DLL moves both;
                // the first-party DLL's bytes are salted by fsc, so only a real
                // build at a different path would show the materialization moving
                // while the semantic holds.
                File.WriteAllBytes(pkgB, [| 4uy; 5uy; 6uy |])

                match ofPaths (Some rootB) [ dllB; pkgB ] with
                | Ok b' ->
                    test <@ b'.SemanticKey <> b.SemanticKey @>
                    test <@ b'.MaterializationDigest <> b.MaterializationDigest @>
                | Error e -> failwith $"unexpected refusals %A{e}"
            | other -> failwith $"expected two identities, got %A{other}"))

// ---------------------------------------------------------------------------
// Positive control over the real analyzer set
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``the repository's own analyzer paths identify: house rules first-party, the NuGet shim by bytes`` () =
    // The same DLL set the loader inspects, from the same paths `.fshw.json` names.
    let dlls =
        [ "tools/fsharplint-shim/bin/Debug/net10.0"; rulesBinRel ]
        |> List.collect (fun rel ->
            Directory.GetFiles(Path.Combine(repoRoot, rel), "*.dll")
            |> Array.filter (fun dll ->
                not (
                    FsHotWatch.Analyzers.AnalyzersPlugin.isKnownNonAnalyzerPrefix
                        FsHotWatch.Analyzers.AnalyzersPlugin.knownNonAnalyzerPrefixes
                        (Path.GetFileNameWithoutExtension dll)
                ))
            |> Array.sort
            |> Array.toList)

    let byName =
        dlls
        |> List.map (fun dll -> Path.GetFileName dll, identityOf (Some repoRoot) dll)

    let isFirstParty (id: Result<AnalyzerAssemblyIdentity, Refusal>) =
        match id with
        | Ok(AnalyzerAssemblyIdentity.FirstParty _) -> true
        | _ -> false

    let isPackage (id: Result<AnalyzerAssemblyIdentity, Refusal>) =
        match id with
        | Ok(AnalyzerAssemblyIdentity.PackageBytes _) -> true
        | _ -> false

    test
        <@
            byName
            |> List.exists (fun (name, id) -> name = "FsHotWatch.ConventionAnalyzers.dll" && isFirstParty id)
        @>

    test
        <@
            byName
            |> List.exists (fun (name, id) -> name = "FSharpLintAnalyzerShim.dll" && isPackage id)
        @>

    test <@ byName |> List.forall (fun (_, id) -> Result.isOk id) @>

    match ofPaths (Some repoRoot) dlls with
    | Ok _ -> ()
    | Error refusals -> failwith $"the real analyzer set was refused: %A{refusals}"
