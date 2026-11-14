/// FsHotWatch.Core is the re-architecture's correctness core (L0-L2): no FCS, no MSBuild, no xUnit,
/// so it can be simulated, kept small, and published Native AOT for experiment E5 (AUTOMATION-1695;
/// plan §3). This pins that by what the compiled assembly REFERENCES, so a project reference or a
/// package that drags FCS in fails here, not in a later AOT publish.
module FsHotWatch.Tests.CoreBoundaryTests

open System
open System.Reflection
open Xunit
open Swensen.Unquote

/// What FsHotWatch.Core may reference, EXACTLY: the runtime, FSharp.Core, and `Ignore` (gitignore
/// matching for the tree walk; decided 2026-11-14, AUTOMATION-1695). Anything else needs a decision
/// recorded here and in Core's fsproj comment.
let private allowed (name: AssemblyName) =
    name.Name = "FSharp.Core"
    || name.Name = "Ignore"
    || name.Name = "netstandard"
    || name.Name = "mscorlib"
    || name.Name.StartsWith("System.", StringComparison.Ordinal)
    || name.Name = "System"

/// The references of `assembly` the core may not have.
let private forbidden (assembly: Assembly) : string list =
    assembly.GetReferencedAssemblies()
    |> Array.filter (allowed >> not)
    |> Array.map _.Name
    |> Array.sort
    |> List.ofArray

[<Fact>]
let ``FsHotWatch.Core references nothing but the runtime, FSharp.Core and Ignore`` () =
    let core = typeof<FsHotWatch.Core.NonEmpty<int>>.Assembly
    test <@ core.GetName().Name = "FsHotWatch.Core" @>
    test <@ forbidden core = [] @>

/// The control: the same check flags FCS where it is referenced, so a green above means
/// something.
[<Fact>]
let ``the boundary check flags FCS in an assembly that references it`` () =
    let daemon = typeof<FsHotWatch.RecordAudit.Report>.Assembly
    test <@ daemon.GetName().Name = "FsHotWatch" @>
    test <@ forbidden daemon |> List.contains "FSharp.Compiler.Service" @>

/// RepoIdentity names the outcome store's directory with a digest it took from CheckCache, which
/// stays on the FCS side; in Core it takes ContentHash.ofText. If the two ever differed, every
/// existing record on every machine would silently become a miss: a box-wide cold scan.
/// The namespace of `root` as the code before the move computed it: the same label, and the
/// digest from `CheckCache.sha256Hex`.
let private namespaceByCheckCache (root: string) =
    let fresh = FsHotWatch.RepoIdentity.namespaceOf root
    let label = fresh.Substring(0, fresh.LastIndexOf '-')

    let digest =
        (FsHotWatch.CheckCache.sha256Hex (
            FsHotWatch.RepoIdentity.identitySource (FsHotWatch.RepoIdentity.describe root)
        ))
            .Substring(0, 16)

    $"%s{label}-%s{digest}"

/// Real checkouts: this repository's, and any named in `FSHW_TEST_NAMESPACE_CHECKOUTS`
/// (`:`-separated; a consumer's checkout, such as intelligence's, on a machine that has one).
[<Fact>]
let ``a real checkout's store namespace is the same by the old digest and the new`` () =
    let extra =
        match Environment.GetEnvironmentVariable "FSHW_TEST_NAMESPACE_CHECKOUTS" with
        | null
        | "" -> []
        | paths -> paths.Split(':', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    for root in
        FsHotWatch.Tests.RepoTasks.repoRoot () :: extra
        |> List.filter IO.Directory.Exists do
        test <@ FsHotWatch.RepoIdentity.namespaceOf root = namespaceByCheckCache root @>

/// The golden: an identity source's digest, pinned as a literal (a jj common store at a fixed path).
[<Fact>]
let ``the store namespace digest is pinned`` () =
    let source = "jj:/srv/fixture/repo/.jj/repo"
    test <@ (FsHotWatch.ContentHash.ofText source).Substring(0, 16) = "8694552984cbbef6" @>
    test <@ FsHotWatch.ContentHash.ofText source = FsHotWatch.CheckCache.sha256Hex source @>
