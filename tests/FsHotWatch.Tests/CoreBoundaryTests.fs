/// FsHotWatch.Core is the re-architecture's correctness core (L0-L2): no FCS, no MSBuild, no xUnit,
/// so it can be simulated, kept small, and published Native AOT for experiment E5 (AUTOMATION-1695;
/// plan §3). This pins that by what the compiled assembly REFERENCES, so a project reference or a
/// package that drags FCS in fails here, not in a later AOT publish.
module FsHotWatch.Tests.CoreBoundaryTests

open System
open System.Reflection
open Xunit
open Swensen.Unquote

/// What FsHotWatch.Core may reference: the runtime and FSharp.Core. Anything else needs a decision
/// recorded here and in Core's fsproj comment.
let private allowed (name: AssemblyName) =
    name.Name = "FSharp.Core"
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
let ``FsHotWatch.Core references nothing but the runtime and FSharp.Core`` () =
    let core = typeof<FsHotWatch.Core.NonEmpty<int>>.Assembly
    test <@ core.GetName().Name = "FsHotWatch.Core" @>
    test <@ forbidden core = [] @>

/// The control: the same check flags FCS where it is referenced, so a green above means
/// something.
[<Fact>]
let ``the boundary check flags FCS in an assembly that references it`` () =
    let daemon = typeof<FsHotWatch.OutcomeStore.Store>.Assembly
    test <@ forbidden daemon |> List.contains "FSharp.Compiler.Service" @>
