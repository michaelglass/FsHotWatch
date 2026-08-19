module FsHotWatch.Tests.SafeWalkTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

// SafeWalk is the one repo-scale walker (see its doc comment for the 2026-07-13
// symlink-cycle wedge RCA). These pin its guarantees: no symlinked-dir descent,
// depth cap, missing-root tolerance, best-effort over unreadable subtrees — and
// (AUTOMATION-164) that what it could NOT see is reported rather than deleted.

let private names (files: seq<FileInfo>) =
    files |> Seq.map (fun f -> f.Name) |> Seq.sort |> Seq.toList

[<Fact(Timeout = 15000)>]
let ``enumerateFiles yields files recursively and skips excluded dir names`` () =
    withTempDir "sw-basic" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "root.fs"), "")
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "sub", "nested.fs"), "")
        Directory.CreateDirectory(Path.Combine(tmpDir, "bin")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "bin", "generated.fs"), "")

        let got = SafeWalk.bestEffortFiles (set [ "bin" ]) tmpDir |> names
        test <@ got = [ "nested.fs"; "root.fs" ] @>)

[<Fact(Timeout = 15000)>]
let ``enumerateFiles is empty for a missing root`` () =
    let got =
        SafeWalk.bestEffortFiles Set.empty "/definitely/not/a/real/path" |> Seq.toList

    test <@ List.isEmpty got @>

// REGRESSION (2026-07-13 wedge): a self-loop symlink (`loop -> .`) must not be
// entered. Two of them in one dir is the exact /nix/store ncurses shape that
// made the pre-SafeWalk recursion ~2^90 paths. On a walker that follows
// symlinked dirs this test trips its Timeout.
[<Fact(Timeout = 15000)>]
let ``enumerateFiles never descends into symlinked directories`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "sw-symlink" (fun tmpDir ->
            File.WriteAllText(Path.Combine(tmpDir, "real.fs"), "")

            // Self-loops (the cycle shape) ...
            Directory.CreateSymbolicLink(Path.Combine(tmpDir, "loop"), ".") |> ignore
            Directory.CreateSymbolicLink(Path.Combine(tmpDir, "loop2"), ".") |> ignore

            // ... and a portal to a sibling tree holding another file.
            let outside = Path.Combine(tmpDir, "..", Path.GetFileName(tmpDir) + "-outside")
            Directory.CreateDirectory outside |> ignore

            try
                File.WriteAllText(Path.Combine(outside, "outside.fs"), "")

                Directory.CreateSymbolicLink(Path.Combine(tmpDir, "portal"), outside) |> ignore

                let got = SafeWalk.bestEffortFiles Set.empty tmpDir |> names
                test <@ got = [ "real.fs" ] @>
            finally
                Directory.Delete(outside, true))

// Best-effort enumeration: an unreadable subtree must not fault the whole walk
// (a freshness scan that throws on one permission hole would fail the gate for
// an unrelated reason). Both IO-error arms — GetFiles and GetDirectories on the
// locked dir — are exercised here.
[<Fact(Timeout = 15000)>]
let ``enumerateFiles survives an unreadable subtree and keeps walking`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "sw-perm" (fun tmpDir ->
            File.WriteAllText(Path.Combine(tmpDir, "readable.fs"), "")

            let locked = Path.Combine(tmpDir, "locked")
            Directory.CreateDirectory locked |> ignore
            File.WriteAllText(Path.Combine(locked, "unreachable.fs"), "")
            File.SetUnixFileMode(locked, UnixFileMode.None)

            try
                let got = SafeWalk.bestEffortFiles Set.empty tmpDir |> names
                test <@ got = [ "readable.fs" ] @>
            finally
                // Restore so withTempDir's recursive delete can clean up.
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// The depth cap is the belt for cycles that evade the symlink guard (e.g.
// bind mounts): a subtree nested beyond MaxDepth is skipped — with a warning,
// not silently — while everything above the cap is still enumerated.
[<Fact(Timeout = 15000)>]
let ``enumerateFiles stops at the depth cap`` () =
    withTempDir "sw-depth" (fun tmpDir ->
        // Build a chain 5 levels DEEPER than the cap. Depth here counts dirs
        // below the root: root=0, d1=1, ... A file at depth (MaxDepth) is
        // reachable; a file at depth (MaxDepth + 5) is beyond the cap.
        let mutable dir = tmpDir

        for i in 1 .. SafeWalk.MaxDepth + 5 do
            dir <- Path.Combine(dir, $"d%d{i}")
            Directory.CreateDirectory dir |> ignore

            if i = SafeWalk.MaxDepth then
                File.WriteAllText(Path.Combine(dir, "at-cap.fs"), "")

        File.WriteAllText(Path.Combine(dir, "beyond-cap.fs"), "")

        let got = SafeWalk.bestEffortFiles Set.empty tmpDir |> names
        test <@ got = [ "at-cap.fs" ] @>)

// ---------------------------------------------------------------------------
// The skip channel (AUTOMATION-164). `bestEffortFiles` DISCARDS what the walk
// could not see, which is why it must not be the only way to ask: an unreadable
// directory contributes nothing, and a consumer reading "nothing" concludes
// "clean". `walk` returns both halves so each caller states its own conclusion.
// ---------------------------------------------------------------------------

// POSITIVE CONTROL for every test below. Without it, a walker that reported
// EVERYTHING as skipped — or returned no files at all — would pass the
// unreadable-directory tests while being useless, and "fail closed" would have
// been implemented as "fail always".
[<Fact(Timeout = 15000)>]
let ``walk over a fully readable tree reports its files and NOTHING skipped`` () =
    withTempDir "sw-walk-clean" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "root.fs"), "")
        Directory.CreateDirectory(Path.Combine(tmpDir, "sub")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "sub", "nested.fs"), "")

        let result = SafeWalk.walk Set.empty "*" tmpDir
        test <@ names result.Files = [ "nested.fs"; "root.fs" ] @>
        test <@ List.isEmpty result.Skipped @>)

[<Fact(Timeout = 15000)>]
let ``walk over a missing root reports nothing seen and nothing skipped`` () =
    let result = SafeWalk.walk Set.empty "*" "/definitely/not/a/real/path"
    test <@ List.isEmpty result.Files @>
    // A root that is not there is an ABSENCE, not a blind spot: there is no
    // directory whose contents we failed to look at.
    test <@ List.isEmpty result.Skipped @>

// An EXCLUDED directory is not a hole either — it was never in scope, so
// reporting it would drown the real holes in noise.
[<Fact(Timeout = 15000)>]
let ``walk does not report an excluded directory as skipped`` () =
    withTempDir "sw-walk-excluded" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "bin")) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "bin", "generated.fs"), "")

        let result = SafeWalk.walk (set [ "bin" ]) "*" tmpDir
        test <@ List.isEmpty result.Files @>
        test <@ List.isEmpty result.Skipped @>)

// THE BUG. Before this, an unreadable directory contributed zero entries and was
// invisible: the freshness gate found no source newer than the assembly and said
// FRESH, and TreeHash hashed a tree it had never fully seen.
[<Fact(Timeout = 15000)>]
let ``walk REPORTS an unreadable directory instead of dropping it`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "sw-walk-perm" (fun tmpDir ->
            File.WriteAllText(Path.Combine(tmpDir, "readable.fs"), "")

            let locked = Path.Combine(tmpDir, "locked")
            Directory.CreateDirectory locked |> ignore
            File.WriteAllText(Path.Combine(locked, "unreachable.fs"), "")
            File.SetUnixFileMode(locked, UnixFileMode.None)

            try
                let result = SafeWalk.walk Set.empty "*" tmpDir

                // What it COULD see is still returned — the hole does not fault the walk.
                test <@ names result.Files = [ "readable.fs" ] @>

                match result.Skipped with
                | [ skipped ] ->
                    test <@ skipped.Path = locked @>

                    match skipped.Reason with
                    | SafeWalk.Unreadable _ -> ()
                    | other -> failwithf "expected Unreadable, got %A" other

                    // One skip for the directory, not one per failed read.
                    test <@ (SafeWalk.describeSkip skipped).Contains locked @>
                | other -> failwithf "expected exactly one skipped dir, got %A" other
            finally
                // Restore so withTempDir's recursive delete can clean up.
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// "A MaxDepth truncation is reported, not merely logged": a warning in a log is
// not something a caller can branch on, and the caller is the one deciding
// whether its claim still holds.
[<Fact(Timeout = 15000)>]
let ``walk REPORTS a subtree cut off by the depth cap`` () =
    withTempDir "sw-walk-depth" (fun tmpDir ->
        let mutable dir = tmpDir

        for i in 1 .. SafeWalk.MaxDepth + 1 do
            dir <- Path.Combine(dir, $"d%d{i}")
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(Path.Combine(tmpDir, "at-root.fs"), "")
        File.WriteAllText(Path.Combine(dir, "beyond-cap.fs"), "")

        let result = SafeWalk.walk Set.empty "*" tmpDir
        test <@ names result.Files = [ "at-root.fs" ] @>

        match result.Skipped with
        | [ skipped ] ->
            test <@ skipped.Path = dir @>
            test <@ skipped.Reason = SafeWalk.DepthCapped @>
            test <@ (SafeWalk.describeSkip skipped).Contains "depth cap" @>
        | other -> failwithf "expected exactly one skipped dir, got %A" other)

// The laziness `RunOnceOutput` depends on (`Seq.exists` stopping at the first
// .fsproj) is why the walk is a stream of entries rather than a record whose
// skip list is only correct once fully enumerated.
[<Fact(Timeout = 15000)>]
let ``enumerateEntries is lazy — an early-stopping caller never walks the rest`` () =
    withTempDir "sw-lazy" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "a.fsproj"), "")

        // A directory that would fault a walker reading it eagerly is never reached,
        // because the first entry satisfies the caller.
        let deep = Path.Combine(tmpDir, "z-deep")
        Directory.CreateDirectory deep |> ignore
        File.WriteAllText(Path.Combine(deep, "b.fsproj"), "")

        let seen = ResizeArray<string>()

        let found =
            SafeWalk.enumerateEntries Set.empty "*.fsproj" tmpDir
            |> Seq.map (fun entry ->
                match entry with
                | SafeWalk.Found f -> seen.Add f.Name
                | SafeWalk.Skipped s -> seen.Add s.Path

                entry)
            |> Seq.exists (function
                | SafeWalk.Found f -> f.Name = "a.fsproj"
                | SafeWalk.Skipped _ -> false)

        test <@ found @>
        test <@ List.ofSeq seen = [ "a.fsproj" ] @>)
