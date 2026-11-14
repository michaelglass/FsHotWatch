/// A plugin's declared inputs (AUTOMATION-1577): a file command's pattern declares exactly the
/// files whose change runs it, and its key moves with them and with nothing else.
module FsHotWatch.Tests.PluginInputsTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// A tree with `.fsx` and `CHANGELOG.md` files inside the scope and outside it.
let private fixture (root: string) =
    for rel in
        [ "build.fsx"
          "scripts/tool.fsx"
          "src/A/gen.fsx"
          "src/A/bin/Debug/copied.fsx"
          "src/A/obj/stale.fsx"
          "node_modules/pkg/x.fsx"
          ".workspaces/sibling/build.fsx"
          ".jj/repo/x.fsx"
          "CHANGELOG.md"
          "src/A/CHANGELOG.md"
          "src/A/a.fs" ] do
        write root rel rel

let private fsx = Watcher.FilePattern.parse "*.fsx"
let private changelog = Watcher.FilePattern.parse "CHANGELOG.md"

let private matched (root: string) pattern =
    PluginInputs.matches root pattern |> fst |> List.map fst

[<Fact>]
let ``a pattern declares the files its watcher reacts to, and no others`` () =
    withTempDir "plugin-inputs-scope" (fun root ->
        fixture root

        // The watcher: a pattern match outside build output, in a directory its stream reaches.
        let watcherScope (pattern: Watcher.FilePattern) =
            let excluded = Watcher.kernelExclusions root

            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.filter (fun abs ->
                Watcher.acceptsUnderRoot root [ pattern ] abs
                && Watcher.FilePattern.matches pattern abs
                && not (
                    excluded
                    |> List.exists (fun dir -> abs.StartsWith(dir + string Path.DirectorySeparatorChar))
                ))
            |> Array.map (fun abs -> Path.GetRelativePath(root, abs).Replace('\\', '/'))
            |> Array.sort
            |> List.ofArray

        test <@ matched root fsx = [ "build.fsx"; "scripts/tool.fsx"; "src/A/gen.fsx" ] @>
        test <@ matched root fsx = watcherScope fsx @>
        test <@ matched root changelog = [ "CHANGELOG.md"; "src/A/CHANGELOG.md" ] @>
        test <@ matched root changelog = watcherScope changelog @>)

let private spec entry =
    PluginInputs.InputSpec.Matching(fsx, entry)

[<Fact>]
let ``the key moves with a match, the entry and the producer, and with nothing else`` () =
    withTempDir "plugin-inputs-key" (fun root ->
        fixture root

        let key entry producer =
            (PluginInputs.key root producer "file-cmd-*.fsx" (spec entry, [])).Value

        let before = key "e1" "fshw 1"

        // Unrelated files, and a match where the watcher never looks, leave it alone.
        write root "src/A/a.fs" "edited"
        write root "src/A/bin/Debug/copied.fsx" "rebuilt"
        write root "node_modules/pkg/x.fsx" "upgraded"
        test <@ key "e1" "fshw 1" = before @>

        test <@ key "e2" "fshw 1" <> before @>
        test <@ key "e1" "fshw 2" <> before @>

        write root "scripts/tool.fsx" "edited"
        test <@ key "e1" "fshw 1" <> before @>)

[<Fact>]
let ``a match that appears or disappears moves the key`` () =
    withTempDir "plugin-inputs-set" (fun root ->
        fixture root
        let digest () = PluginInputs.digest root (spec "e")
        let before = digest ()
        write root "src/B/new.fsx" "new"
        let added = digest ()
        test <@ added <> before @>
        File.Delete(Path.Combine(root, "src/B/new.fsx"))
        test <@ digest () = before @>)

[<Fact>]
let ``a directory the walk cannot see is part of every pattern's key`` () =
    withTempDir "plugin-inputs-hole" (fun root ->
        fixture root
        let digest () = PluginInputs.digest root (spec "e")
        let before = digest ()
        let hidden = Path.Combine(root, "src", "Hidden")
        Directory.CreateDirectory hidden |> ignore
        File.WriteAllText(Path.Combine(hidden, "secret.fsx"), "x")

        try
            File.SetUnixFileMode(hidden, UnixFileMode.None)
            // The walk cannot list it, so whatever it holds is unknown: the key must not match the
            // key of a tree without it, nor the key of the same tree with it readable.
            test <@ snd (PluginInputs.matches root fsx) = [ "src/Hidden" ] @>
            test <@ digest () <> before @>
        finally
            File.SetUnixFileMode(hidden, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute))
