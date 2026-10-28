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

// AUTOMATION-681 slice 4 (ADR-046): each incarnation of a worktree's session claims
// through its own lease. A recreated session's predecessor can still be finishing work
// (a frame choice made by a check that started before it ended); its late claim arrives
// after its release and must change nothing.
[<Fact>]
let ``an ended incarnation's late claim is refused, and its late release leaves its successor's hold`` () =
    let registry = Registry()
    let project = "src/Lib/Lib.fsproj"
    let old = registry.Open "a"
    test <@ registry.Claim(project, "h1", old) @>
    registry.Release old

    let successor = registry.Open "a"
    test <@ registry.Claim(project, "h2", successor) @>

    // The old incarnation's check completes now, with the content it read then.
    test <@ not (registry.Claim(project, "h1", old)) @>
    test <@ registry.CanonicalHash project = Some "h2" @>
    test <@ registry.HoldersOf project = [ "a" ] @>
    test <@ registry.RefusedRetired = 1L @>

    // Releasing it again, late, does not end the successor's hold.
    registry.Release old
    test <@ registry.HoldersOf project = [ "a" ] @>
    test <@ not (registry.Claim(project, "h3", registry.Open "b")) @>

[<Fact>]
let ``a ghost claim never blocks a sibling once the live holder ends`` () =
    let registry = Registry()
    let project = "src/Lib/Lib.fsproj"
    let old = registry.Open "a"
    registry.Release old
    // The predecessor's delayed claim: refused, so it holds nothing.
    test <@ not (registry.Claim(project, "h1", old)) @>
    test <@ List.isEmpty (registry.HoldersOf project) @>
    let b = registry.Open "b"
    test <@ registry.Claim(project, "h2", b) @>
    test <@ registry.CanonicalHash project = Some "h2" @>

[<Fact>]
let ``releasing one session's lease leaves every other session's hold`` () =
    let registry = Registry()
    let project = "src/Lib/Lib.fsproj"
    let a = registry.Open "a"
    let b = registry.Open "b"
    test <@ registry.Claim(project, "h1", a) @>
    test <@ registry.Claim(project, "h1", b) @>
    registry.Release a
    test <@ registry.HoldersOf project = [ "b" ] @>
