/// The cache-key inputs that live outside the judged file: the config a
/// tool discovers by walking up, and the sources a type check can depend on.
module FsHotWatch.Tests.CacheInputsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.CacheInputs
open FsHotWatch.Tests.TestHelpers

// =============================================================================
// Config discovered by walking up from the file
// =============================================================================

[<Fact>]
let ``editorConfigInputs collects every editorconfig from the repo root down to the file`` () =
    withTempDir "fantomas-editorconfig" (fun dir ->
        let sub = Path.Combine(dir, "src", "deep")
        Directory.CreateDirectory sub |> ignore
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(dir, "src", ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let file = Path.Combine(sub, "A.fs")
        File.WriteAllText(file, "module A\n")

        let inputs = editorConfigInputs dir [ file ]

        // the LABEL is repo-relative, so the same `.editorconfig` is
        // the same cache-key input in every checkout of the repository.
        test
            <@
                inputs = [ "editorconfig:repo:.editorconfig", "root = true\n"
                           "editorconfig:repo:src/.editorconfig", "[*.fs]\nmax_line_length = 80\n" ]
            @>

        // A file that does not exist yet still resolves its directories' configs.
        test <@ editorConfigInputs dir [ Path.Combine(dir, "src", "B.fs") ] |> List.length = 2 @>

        // No config files, no inputs.
        withTempDir "fantomas-noeditorconfig" (fun bare ->
            test <@ List.isEmpty (editorConfigInputs bare [ Path.Combine(bare, "A.fs") ]) @>)

        // A file OUTSIDE the root walks to the filesystem root and stops there, and
        // never reports this repository's configs.
        withTempDir "fantomas-outside" (fun elsewhere ->
            let outside = editorConfigInputs dir [ Path.Combine(elsewhere, "B.fs") ]
            test <@ outside |> List.forall (fun (label, _) -> not (label.Contains dir)) @>))

[<Fact>]
let ``configChainInputs finds any walked-up config file under its own label`` () =
    withTempDir "chain" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        File.WriteAllText(Path.Combine(dir, "fsharplint.json"), "{}")
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")

        let inputs =
            configChainInputs dir "fsharplint.json" "fsharplint" [ Path.Combine(dir, "src", "A.fs") ]

        test <@ inputs = [ "fsharplint:repo:fsharplint.json", "{}" ] @>)

// =============================================================================
// The sources a type check of a file can depend on
// =============================================================================

/// A one-project repository under `root`: `files` (name, content) in compile order.
let private writeProject (root: string) (project: string) (files: (string * string) list) =
    let dir = Path.Combine(root, project)
    Directory.CreateDirectory dir |> ignore

    let paths =
        files
        |> List.map (fun (name, content) ->
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, content)
            path)

    fakeProjectOptions (Path.Combine(dir, project + ".fsproj")) paths

let private closureOf root (options: FSharpProjectOptions) (name: string) =
    let file = options.SourceFiles |> Array.find (fun f -> Path.GetFileName f = name)

    dependencyClosureHash (Some root) options file

let private threeFiles =
    [ "A.fs", "module A\n"; "B.fs", "module B\n"; "C.fs", "module C\n" ]

[<Fact>]
let ``an earlier file's edit moves the closure of every later file`` () =
    withTempDir "closure-earlier" (fun root ->
        let options = writeProject root "App" threeFiles
        let before = closureOf root options "C.fs"
        File.WriteAllText(options.SourceFiles[0], "module A\nlet x = 1\n")
        test <@ before.IsSome @>
        test <@ closureOf root options "C.fs" <> before @>)

[<Fact>]
let ``a later file or the file itself does not move a closure`` () =
    // F# resolves names only backwards, and the checked source is the caller's own
    // key input — so neither may reach the closure, or every edit would miss twice.
    withTempDir "closure-later" (fun root ->
        let options = writeProject root "App" threeFiles
        let before = closureOf root options "B.fs"
        File.WriteAllText(options.SourceFiles[1], "module B\nlet y = 2\n")
        File.WriteAllText(options.SourceFiles[2], "module C\nlet z = 3\n")
        test <@ before.IsSome @>
        test <@ closureOf root options "B.fs" = before @>)

[<Fact>]
let ``a referenced project's source moves the closure, transitively`` () =
    withTempDir "closure-ref" (fun root ->
        let core =
            writeProject root "Core" [ "Types.fs", "module Types\ntype T = { X: int }\n" ]

        let lib =
            { writeProject root "Lib" [ "Lib.fs", "module Lib\n" ] with
                ReferencedProjects = [| FSharpReferencedProject.FSharpReference("Core.dll", core) |] }

        let app =
            { writeProject root "App" [ "Main.fs", "module Main\n" ] with
                ReferencedProjects = [| FSharpReferencedProject.FSharpReference("Lib.dll", lib) |] }

        let before = closureOf root app "Main.fs"
        File.WriteAllText(core.SourceFiles[0], "module Types\ntype T = | X of int\n")
        test <@ before.IsSome @>
        test <@ closureOf root app "Main.fs" <> before @>)

[<Fact>]
let ``identical sources in two checkouts produce the same closure`` () =
    // The shared store's whole point: the closure names paths repo-relatively and
    // hashes content, so a second checkout of the same tree hits.
    withTempDir "closure-twin-a" (fun a ->
        withTempDir "closure-twin-b" (fun b ->
            let closureA = closureOf a (writeProject a "App" threeFiles) "C.fs"
            let closureB = closureOf b (writeProject b "App" threeFiles) "C.fs"
            test <@ closureA.IsSome @>
            test <@ closureA = closureB @>))

[<Fact>]
let ``an unreadable dependency leaves no closure`` () =
    withTempDir "closure-missing" (fun root ->
        let options = writeProject root "App" threeFiles
        File.Delete options.SourceFiles[0]
        test <@ closureOf root options "C.fs" = None @>
        // A dependency the file does not have cannot take its key away.
        test <@ (closureOf root options "A.fs").IsSome @>)

[<Fact>]
let ``generated sources under obj are not part of the closure`` () =
    withTempDir "closure-generated" (fun root ->
        let options = writeProject root "App" threeFiles
        let generated = Path.Combine(root, "App", "obj", "AssemblyInfo.fs")
        Directory.CreateDirectory(Path.GetDirectoryName generated) |> ignore
        File.WriteAllText(generated, "// commit abc\n")

        let withGenerated =
            { options with
                SourceFiles = Array.append [| generated |] options.SourceFiles }

        let before = closureOf root withGenerated "C.fs"
        File.WriteAllText(generated, "// commit def\n")
        test <@ before.IsSome @>
        test <@ closureOf root withGenerated "C.fs" = before @>)
