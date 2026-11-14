/// What a restarted daemon's first check would select (AUTOMATION-1623, ADR-069): published by
/// the test plugin as a file beside the daemon's state, read by idle-exit on every tick.
module FsHotWatch.Tests.RestartTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact>]
let ``a published SelectsCorrectly reads back with its tree`` () =
    withTempDir "restart" (fun root ->
        Restart.publish ignore root (Restart.Selection.SelectsCorrectly "sha256:t")
        test <@ Restart.read root = Restart.Selection.SelectsCorrectly "sha256:t" @>)

[<Fact>]
let ``a published WouldRunFullSuite reads back with its reason`` () =
    withTempDir "restart" (fun root ->

        Restart.publish
            ignore
            root
            (Restart.Selection.WouldRunFullSuite "the index is sealed at sha256:a, not at the baseline's tree sha256:b")

        test
            <@
                Restart.read root = Restart.Selection.WouldRunFullSuite
                    "the index is sealed at sha256:a, not at the baseline's tree sha256:b"
            @>)

[<Fact>]
let ``a later publication replaces an earlier one, so a stale SelectsCorrectly does not survive`` () =
    withTempDir "restart" (fun root ->
        Restart.publish ignore root (Restart.Selection.SelectsCorrectly "sha256:t")
        Restart.publish ignore root (Restart.Selection.WouldRunFullSuite "re-sealed elsewhere")
        test <@ Restart.read root = Restart.Selection.WouldRunFullSuite "re-sealed elsewhere" @>)

[<Fact>]
let ``nothing published reads as WouldRunFullSuite, never as SelectsCorrectly`` () =
    withTempDir "restart" (fun root ->

        match Restart.read root with
        | Restart.Selection.WouldRunFullSuite _ -> ()
        | Restart.Selection.SelectsCorrectly tree -> failwith $"an absent file claimed a restart selects at %s{tree}")

[<Fact>]
let ``an unreadable publication reads as WouldRunFullSuite`` () =
    withTempDir "restart" (fun root ->
        let path = Restart.pathOf root
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        for junk in [ ""; "selects-correctly"; "selects-correctly "; "maybe sha256:t"; "\n\n" ] do
            File.WriteAllText(path, junk)

            match Restart.read root with
            | Restart.Selection.WouldRunFullSuite _ -> ()
            | Restart.Selection.SelectsCorrectly tree -> failwith $"%A{junk} was read as SelectsCorrectly %s{tree}")

[<Fact>]
let ``the publication lives under the daemon's state directory`` () =
    withTempDir "restart" (fun root -> test <@ Path.GetDirectoryName(Restart.pathOf root) = FsHwPaths.root root @>)

[<Fact>]
let ``a publication that cannot be written is logged, does not throw, and leaves no SelectsCorrectly behind`` () =
    withTempDir "restart" (fun root ->
        Restart.publish ignore root (Restart.Selection.SelectsCorrectly "sha256:t")
        // A file where the state directory must be: the write cannot happen.
        let state = FsHwPaths.root root
        Directory.Delete(state, true)
        File.WriteAllText(state, "not a directory")
        let logged = Collections.Generic.List<string>()
        Restart.publish logged.Add root (Restart.Selection.SelectsCorrectly "sha256:u")
        test <@ logged |> Seq.exists (fun l -> l.Contains "could not be written") @>

        match Restart.read root with
        | Restart.Selection.WouldRunFullSuite _ -> ()
        | Restart.Selection.SelectsCorrectly tree ->
            failwith $"an unwritten publication read as SelectsCorrectly %s{tree}")

[<Fact>]
let ``a publication that cannot be read reads as WouldRunFullSuite`` () =
    withTempDir "restart" (fun root ->
        Restart.publish ignore root (Restart.Selection.SelectsCorrectly "sha256:t")
        let path = Restart.pathOf root
        File.SetUnixFileMode(path, UnixFileMode.None)
        // The control must be real: a reader that can still read the file proves nothing.
        let readable =
            try
                File.ReadAllText path |> ignore
                true
            with :? UnauthorizedAccessException ->
                false

        test <@ not readable @>

        match Restart.read root with
        | Restart.Selection.WouldRunFullSuite _ -> ()
        | Restart.Selection.SelectsCorrectly tree ->
            failwith $"an unreadable publication read as SelectsCorrectly %s{tree}")
