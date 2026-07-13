module TestPrune.Tests.SafeWalkTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune

/// The 2026-07-13 wedge, reproduced in miniature: a directory containing TWO
/// self-loop symlinks (`a -> .`, `b -> .`) — the exact shape found in the Nix
/// store (ncurses-6.6-dev/include/{ncurses,ncursesw} -> `.`). Each level doubles
/// the reachable path count, so a walker that follows directory symlinks never
/// terminates. `Directory.GetFiles(root, "*", SearchOption.AllDirectories)` on
/// this tree hangs; SafeWalk must return promptly.
let private withSelfLoopTree (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"safewalk-{System.Guid.NewGuid():N}")
    Directory.CreateDirectory(root) |> ignore

    try
        let nested = Path.Combine(root, "nested")
        Directory.CreateDirectory(nested) |> ignore
        File.WriteAllText(Path.Combine(root, "top.fs"), "// top")
        File.WriteAllText(Path.Combine(nested, "deep.fs"), "// deep")

        // The killers: two symlinks in one directory, each pointing at that
        // same directory. Branching factor 2 per level.
        Directory.CreateSymbolicLink(Path.Combine(nested, "loopA"), nested) |> ignore
        Directory.CreateSymbolicLink(Path.Combine(nested, "loopB"), nested) |> ignore

        f root
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

[<Fact>]
let ``enumerateFiles terminates on a directory with self-loop symlinks`` () =
    withSelfLoopTree (fun root ->
        // The whole point: this returns AT ALL. Pre-fix, the AllDirectories walk
        // this replaced enumerated ~2^depth paths and never came back.
        let files = SafeWalk.enumerateFiles "*.fs" root

        let names = files |> List.map Path.GetFileName |> List.sort
        test <@ names = [ "deep.fs"; "top.fs" ] @>)

[<Fact>]
let ``enumerateFiles never yields a file reached through a symlinked directory`` () =
    withSelfLoopTree (fun root ->
        let files = SafeWalk.enumerateFiles "*.fs" root

        // deep.fs is reachable as nested/deep.fs, and ALSO as
        // nested/loopA/deep.fs, nested/loopA/loopB/deep.fs, ... — every one of
        // those is a symlink traversal. Exactly one real path must survive.
        let deepPaths = files |> List.filter (fun p -> Path.GetFileName p = "deep.fs")
        test <@ List.length deepPaths = 1 @>
        test <@ not (deepPaths.Head.Contains "loopA") @>
        test <@ not (deepPaths.Head.Contains "loopB") @>)

[<Fact>]
let ``enumerateFiles prunes bin and obj rather than filtering them afterwards`` () =
    let root = Path.Combine(Path.GetTempPath(), $"safewalk-{System.Guid.NewGuid():N}")
    Directory.CreateDirectory(root) |> ignore

    try
        let bin = Path.Combine(root, "bin")
        let obj = Path.Combine(root, "obj")
        Directory.CreateDirectory(bin) |> ignore
        Directory.CreateDirectory(obj) |> ignore
        File.WriteAllText(Path.Combine(root, "real.fs"), "// real")
        File.WriteAllText(Path.Combine(bin, "generated.fs"), "// generated")
        File.WriteAllText(Path.Combine(obj, "generated.fs"), "// generated")

        let files = SafeWalk.enumerateFiles "*.fs" root

        test <@ files |> List.map Path.GetFileName = [ "real.fs" ] @>
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

[<Fact>]
let ``enumerateFiles returns empty for a missing root`` () =
    let missing =
        Path.Combine(Path.GetTempPath(), $"safewalk-absent-{System.Guid.NewGuid():N}")

    test <@ SafeWalk.enumerateFiles "*.fs" missing |> List.isEmpty @>

[<Fact>]
let ``enumerateFiles stops at MaxDepth instead of recursing without bound`` () =
    // The belt-and-braces guard: even if a cycle somehow evaded the symlink
    // check (a bind mount, say), recursion must stop. Build a real tree one
    // level PAST the cap and assert the too-deep file is not returned while the
    // shallow one is.
    let root =
        Path.Combine(Path.GetTempPath(), $"safewalk-deep-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(root) |> ignore

    try
        File.WriteAllText(Path.Combine(root, "shallow.fs"), "// shallow")

        let deepest =
            [ 1 .. SafeWalk.MaxDepth + 1 ]
            |> List.fold
                (fun (dir: string) i ->
                    let next = Path.Combine(dir, $"d%d{i}")
                    Directory.CreateDirectory(next) |> ignore
                    next)
                root

        File.WriteAllText(Path.Combine(deepest, "toodeep.fs"), "// too deep")

        let names = SafeWalk.enumerateFiles "*.fs" root |> List.map Path.GetFileName

        test <@ List.contains "shallow.fs" names @>
        test <@ not (List.contains "toodeep.fs" names) @>
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

[<Fact>]
let ``enumerateFiles tolerates an unreadable subdirectory instead of faulting`` () =
    // Enumeration is best-effort: a permission hole must not fault the caller
    // (TestPrune walks repos it does not own every file of).
    let root =
        Path.Combine(Path.GetTempPath(), $"safewalk-perm-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(root) |> ignore

    try
        File.WriteAllText(Path.Combine(root, "readable.fs"), "// readable")
        let locked = Path.Combine(root, "locked")
        Directory.CreateDirectory(locked) |> ignore
        File.WriteAllText(Path.Combine(locked, "hidden.fs"), "// hidden")

        // Strip all permissions so GetFiles/GetDirectories throws inside the walk.
        File.SetUnixFileMode(locked, UnixFileMode.None)

        let names = SafeWalk.enumerateFiles "*.fs" root |> List.map Path.GetFileName

        // It returned rather than throwing, and still found what it could see.
        test <@ List.contains "readable.fs" names @>
    finally
        try
            File.SetUnixFileMode(
                Path.Combine(root, "locked"),
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )
        with _ ->
            ()

        try
            Directory.Delete(root, true)
        with _ ->
            ()
