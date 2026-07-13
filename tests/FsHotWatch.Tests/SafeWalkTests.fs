module FsHotWatch.Tests.SafeWalkTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

// SafeWalk is the one repo-scale walker (see its doc comment for the
// 2026-07-13 symlink-cycle wedge RCA). These tests pin its three guarantees:
// no symlinked-dir descent, depth cap, missing-root tolerance — plus the
// basic enumeration/exclusion semantics every caller relies on.

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

        let got = SafeWalk.enumerateFiles (set [ "bin" ]) tmpDir |> names
        test <@ got = [ "nested.fs"; "root.fs" ] @>)

[<Fact(Timeout = 15000)>]
let ``enumerateFiles is empty for a missing root`` () =
    let got =
        SafeWalk.enumerateFiles Set.empty "/definitely/not/a/real/path" |> Seq.toList

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

                let got = SafeWalk.enumerateFiles Set.empty tmpDir |> names
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
                let got = SafeWalk.enumerateFiles Set.empty tmpDir |> names
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

        let got = SafeWalk.enumerateFiles Set.empty tmpDir |> names
        test <@ got = [ "at-cap.fs" ] @>)
