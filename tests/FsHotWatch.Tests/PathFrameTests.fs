module FsHotWatch.Tests.PathFrameTests

open Xunit
open Swensen.Unquote
open FsHotWatch

let private frame =
    PathFrame.create "/r/.workspaces/b" "/state/repositories/abc/virtual"

[<Fact>]
let ``a worktree path has one virtual twin, and maps back`` () =
    let real = "/r/.workspaces/b/src/Lib/Lib.fs"
    let virtual' = PathFrame.toVirtual frame real
    test <@ virtual' = "/state/repositories/abc/virtual/src/Lib/Lib.fs" @>
    test <@ PathFrame.toReal frame virtual' = real @>

[<Fact>]
let ``the roots map to each other`` () =
    test <@ PathFrame.toVirtual frame "/r/.workspaces/b" = "/state/repositories/abc/virtual" @>
    test <@ PathFrame.toReal frame "/state/repositories/abc/virtual" = "/r/.workspaces/b" @>

[<Fact>]
let ``paths outside the worktree pass through`` () =
    let sdk =
        "/usr/local/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Runtime.dll"

    test <@ PathFrame.toVirtual frame sdk = sdk @>
    test <@ PathFrame.toReal frame sdk = sdk @>

[<Fact>]
let ``a sibling that only shares the prefix is not under the root`` () =
    test <@ PathFrame.toVirtual frame "/r/.workspaces/b2/src/X.fs" = "/r/.workspaces/b2/src/X.fs" @>
    test <@ PathFrame.toReal frame "/state/repositories/abc/virtual2/X.fs" = "/state/repositories/abc/virtual2/X.fs" @>

[<Fact>]
let ``a trailing separator on a root changes nothing`` () =
    let slashed =
        PathFrame.create "/r/.workspaces/b/" "/state/repositories/abc/virtual/"

    test
        <@
            PathFrame.toVirtual slashed "/r/.workspaces/b/src/Lib/Lib.fs" = "/state/repositories/abc/virtual/src/Lib/Lib.fs"
        @>

[<Fact>]
let ``a message's virtual paths are made real, and nothing else`` () =
    let message =
        "The type 'X' in '/state/repositories/abc/virtual/src/Lib/Lib.fs' is obsolete; see /state/repositories/abc/virtual2/notes and /state/repositories/abc/virtual."

    test
        <@
            PathFrame.textToReal frame message = "The type 'X' in '/r/.workspaces/b/src/Lib/Lib.fs' is obsolete; see /state/repositories/abc/virtual2/notes and /r/.workspaces/b."
        @>

[<Fact>]
let ``with no frame, names and text are the worktree's own`` () =
    test <@ PathFrame.nameIn None "/r/.workspaces/b/src/X.fs" = "/r/.workspaces/b/src/X.fs" @>
    test <@ PathFrame.textFrom None "/state/repositories/abc/virtual/X.fs" = "/state/repositories/abc/virtual/X.fs" @>

[<Fact>]
let ``with a frame, names are virtual and text is made real`` () =
    test <@ PathFrame.nameIn (Some frame) "/r/.workspaces/b/src/X.fs" = "/state/repositories/abc/virtual/src/X.fs" @>

    test
        <@
            PathFrame.textFrom (Some frame) "see /state/repositories/abc/virtual/src/X.fs" = "see /r/.workspaces/b/src/X.fs"
        @>
