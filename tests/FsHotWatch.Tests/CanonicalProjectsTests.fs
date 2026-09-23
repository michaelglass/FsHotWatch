module FsHotWatch.Tests.CanonicalProjectsTests

open Xunit
open Swensen.Unquote
open FsHotWatch.CanonicalProjects

[<Fact>]
let ``sessions with the project's content share the virtual root`` () =
    let registry = Registry()
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h1", "a") @>
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h1", "b") @>
    test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = Some "h1" @>

[<Fact>]
let ``a session whose content differs from what others check stays at its own root`` () =
    let registry = Registry()
    registry.Claim("src/Lib/Lib.fsproj", "h1", "a") |> ignore
    registry.Claim("src/Lib/Lib.fsproj", "h1", "b") |> ignore
    test <@ not (registry.Claim("src/Lib/Lib.fsproj", "h2", "b")) @>
    // A keeps the canonical content; B's edit did not move it.
    test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = Some "h1" @>
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h1", "a") @>

[<Fact>]
let ``a session editing a project nobody else checks keeps the virtual root`` () =
    let registry = Registry()
    registry.Claim("src/Lib/Lib.fsproj", "h1", "a") |> ignore
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h2", "a") @>
    test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = Some "h2" @>

[<Fact>]
let ``a session that returns to the canonical content rejoins it`` () =
    let registry = Registry()
    registry.Claim("src/Lib/Lib.fsproj", "h1", "a") |> ignore
    registry.Claim("src/Lib/Lib.fsproj", "h1", "b") |> ignore
    test <@ not (registry.Claim("src/Lib/Lib.fsproj", "h2", "b")) @>
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h1", "b") @>

[<Fact>]
let ``once the other holders end, a differing session takes the project over`` () =
    let registry = Registry()
    registry.Claim("src/Lib/Lib.fsproj", "h1", "a") |> ignore
    registry.Claim("src/Lib/Lib.fsproj", "h1", "b") |> ignore
    test <@ not (registry.Claim("src/Lib/Lib.fsproj", "h2", "b")) @>
    registry.Release "a"
    test <@ registry.Claim("src/Lib/Lib.fsproj", "h2", "b") @>
    test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = Some "h2" @>

[<Fact>]
let ``projects are claimed independently`` () =
    let registry = Registry()
    registry.Claim("src/Lib/Lib.fsproj", "h1", "a") |> ignore
    registry.Claim("src/Lib/Lib.fsproj", "h1", "b") |> ignore
    registry.Claim("tests/T/T.fsproj", "t1", "a") |> ignore
    test <@ not (registry.Claim("src/Lib/Lib.fsproj", "h2", "b")) @>
    test <@ registry.Claim("tests/T/T.fsproj", "t1", "b") @>
