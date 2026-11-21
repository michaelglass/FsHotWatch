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

            // Every file in the fixture (SafeWalk, excluding nothing), then the watcher's own rules.
            (SafeWalk.walk Set.empty "*" root).Files
            |> List.map _.FullName
            |> List.filter (fun abs ->
                Watcher.acceptsUnderRoot root [ pattern ] abs
                && Watcher.FilePattern.matches pattern abs
                && not (
                    excluded
                    |> List.exists (fun dir -> abs.StartsWith(dir + string Path.DirectorySeparatorChar))
                ))
            |> List.map (fun abs -> Path.GetRelativePath(root, abs).Replace('\\', '/'))
            |> List.sort

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

[<Fact>]
let ``a command's own entry digest moves with its timeout`` () =
    let entry timeout =
        PluginInputs.entryDigest "file-cmd-*.fsx" "*.fsx" "sh" "check.sh" timeout

    test <@ entry None <> entry (Some 30) @>
    test <@ entry (Some 30) <> entry (Some 60) @>
    test <@ entry (Some 30) = entry (Some 30) @>

[<Fact>]
let ``the toolchain the command runs under is part of the key`` () =
    withTempDir "plugin-inputs-toolchain" (fun root ->
        fixture root
        let digest () = PluginInputs.digest root (spec "e")
        let before = digest ()
        write root "global.json" """{ "sdk": { "version": "10.0.100" } }"""
        let pinned = digest ()
        test <@ pinned <> before @>
        write root "global.json" """{ "sdk": { "version": "10.0.200" } }"""
        test <@ digest () <> pinned @>)

[<Fact>]
let ``a pattern matches as the daemon does, whatever the case of the name`` () =
    withTempDir "plugin-inputs-case" (fun root ->
        fixture root
        write root "scripts/LOUD.FSX" "loud"
        test <@ matched root fsx |> List.contains "scripts/LOUD.FSX" @>
        test <@ Watcher.FilePattern.matches fsx (Path.Combine(root, "scripts/LOUD.FSX")) @>)

[<Fact>]
let ``every unreadable directory is in the key, in a stable order`` () =
    withTempDir "plugin-inputs-holes" (fun root ->
        fixture root

        let hide (rel: string) =
            let dir = Path.Combine(root, rel)
            Directory.CreateDirectory dir |> ignore
            File.SetUnixFileMode(dir, UnixFileMode.None)
            dir

        let dirs = [ hide "src/Zeta"; hide "src/Alpha" ]

        try
            test <@ snd (PluginInputs.matches root fsx) = [ "src/Alpha"; "src/Zeta" ] @>
        finally
            for dir in dirs do
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// AUTOMATION-1840: a script command reads every file its script `#load`s, transitively. Those
// files are inputs of the command although no pattern names them, so they are computed from
// the script, never listed by hand.

/// A script tree: `build.fsx` loads two files on one directive and a script in a subdirectory,
/// which loads a file relative to ITSELF, one through `__SOURCE_DIRECTORY__`, a missing file,
/// and `build.fsx` again (a cycle). Commented-out directives name files that must not count.
let private scriptTree (root: string) =
    write
        root
        "build.fsx"
        "#!/usr/bin/env -S dotnet fsi\n// #load \"src/commented.fs\"\n#load \"src/A.fs\" @\"src/B.fs\" // trailing \"src/also-commented.fs\"\n  #load \"tools/helper.fsx\"\nprintfn \"build\"\n"

    write
        root
        "tools/helper.fsx"
        "#load \"\"\"../lib/C.fs\"\"\"\n#load \"__SOURCE_DIRECTORY__/D.fs\"\n#load \"missing.fs\"\n#load \"../build.fsx\"\n"

    for rel in
        [ "src/A.fs"
          "src/B.fs"
          "lib/C.fs"
          "tools/D.fs"
          "src/commented.fs"
          "src/also-commented.fs" ] do
        write root rel rel

let private closureOf (root: string) =
    PluginInputs.loadClosure root [ "build.fsx" ]
    |> List.map (function
        | PluginInputs.Loaded.Present rel -> "present:" + rel
        | PluginInputs.Loaded.Missing rel -> "missing:" + rel
        | PluginInputs.Loaded.Included rel -> "include:" + rel)

[<Fact>]
let ``a script's load closure is every file it loads, transitively, each relative to its loader`` () =
    withTempDir "plugin-inputs-load-closure" (fun root ->
        scriptTree root

        test
            <@
                closureOf root = [ "present:build.fsx"
                                   "present:src/A.fs"
                                   "present:src/B.fs"
                                   "present:tools/helper.fsx"
                                   "present:lib/C.fs"
                                   "present:tools/D.fs"
                                   "missing:tools/missing.fs" ]
            @>)

[<Fact>]
let ``a script that is itself missing is a missing term, not an empty closure`` () =
    withTempDir "plugin-inputs-load-missing-root" (fun root ->
        test <@ PluginInputs.loadClosure root [ "build.fsx" ] = [ PluginInputs.Loaded.Missing "build.fsx" ] @>)

[<Fact>]
let ``the script names in a command's arguments are its fsx tokens`` () =
    test <@ PluginInputs.scriptsIn "fsi --typecheck-only build.fsx" = [ "build.fsx" ] @>
    test <@ PluginInputs.scriptsIn "fsi  scripts/A.FSX  other.fs" = [ "scripts/A.FSX" ] @>
    test <@ List.isEmpty (PluginInputs.scriptsIn "lint migrations/") @>
    test <@ List.isEmpty (PluginInputs.scriptsIn "fsi *.fsx") @>

[<Fact>]
let ``a load-closure key moves with a loaded file, and with a missing one appearing, and nothing else`` () =
    withTempDir "plugin-inputs-load-key" (fun root ->
        scriptTree root
        let spec = PluginInputs.InputSpec.LoadClosure [ "build.fsx" ]
        let digest () = PluginInputs.digest root spec
        let before = digest ()

        // Unrelated files, and a file named only in a comment, leave it alone.
        write root "src/unrelated.fs" "edited"
        write root "src/commented.fs" "edited"
        test <@ digest () = before @>

        write root "lib/C.fs" "edited"
        let edited = digest ()
        test <@ edited <> before @>

        write root "tools/missing.fs" "now here"
        test <@ digest () <> edited @>)

[<Fact>]
let ``a directive's paths are read as F# reads its string literals`` () =
    let targets (text: string) =
        PluginInputs.directivesIn "/repo/scripts/s.fsx" text

    let load = PluginInputs.Directive.Load
    test <@ targets "#load @\"q\"\"uote.fs\"" = [ load "q\"uote.fs" ] @>
    test <@ targets "#load \"es\\\"c.fs\" \"back\\\\slash.fs\"" = [ load "es\"c.fs"; load "back\\slash.fs" ] @>
    test <@ targets "#load \"/elsewhere/x.fs\"" = [ load "/elsewhere/x.fs" ] @>

    test <@ targets "#load \"__SOURCE_DIRECTORY__/a.fs\"" = [ load (Path.GetFullPath "/repo/scripts" + "/a.fs") ] @>

    test
        <@
            targets "#r \"x.dll\"\n#reference @\"y.dll\"" = [ PluginInputs.Directive.Reference "x.dll"
                                                              PluginInputs.Directive.Reference "y.dll" ]
        @>

    test
        <@
            targets "#I \"lib\"\n#include \"more\"" = [ PluginInputs.Directive.Include "lib"
                                                        PluginInputs.Directive.Include "more" ]
        @>
    // A directive whose literal never closes names nothing, nor does another directive.
    test <@ List.isEmpty (targets "#load \"open.fs") @>
    test <@ List.isEmpty (targets "#load @\"open.fs") @>
    test <@ List.isEmpty (targets "#load \"\"\"open.fs") @>
    test <@ List.isEmpty (targets "#loader \"x.fs\"\n#if DEBUG\n#Ix \"y\"") @>
    test <@ List.isEmpty (targets "#load") @>

[<Fact>]
let ``a loaded file that cannot be read is present, and its own loads are not followed`` () =
    withTempDir "plugin-inputs-load-unreadable" (fun root ->
        write root "build.fsx" "#load \"sealed.fsx\"\n"
        write root "sealed.fsx" "#load \"hidden.fs\"\n"
        write root "hidden.fs" "module Hidden"
        let sealedScript = Path.Combine(root, "sealed.fsx")

        try
            File.SetUnixFileMode(sealedScript, UnixFileMode.None)

            test
                <@
                    PluginInputs.loadClosure root [ "build.fsx" ] = [ PluginInputs.Loaded.Present "build.fsx"
                                                                      PluginInputs.Loaded.Present "sealed.fsx" ]
                @>
        finally
            File.SetUnixFileMode(sealedScript, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a loaded file another process holds exclusively is present, and its own loads are not followed`` () =
    withTempDir "plugin-inputs-load-locked" (fun root ->
        write root "build.fsx" "#load \"held.fsx\"\n"
        write root "held.fsx" "#load \"hidden.fs\"\n"
        write root "hidden.fs" "module Hidden"

        // .NET takes an exclusive advisory lock for FileShare.None on Unix too, so the read
        // fails with an IOException while the stream is open.
        use _held =
            new FileStream(Path.Combine(root, "held.fsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        test
            <@
                PluginInputs.loadClosure root [ "build.fsx" ] = [ PluginInputs.Loaded.Present "build.fsx"
                                                                  PluginInputs.Loaded.Present "held.fsx" ]
            @>)

// AUTOMATION-1840 (addition): `#r` to a local path and `#I` are inputs of the script too. A
// referenced assembly's bytes decide what the script compiles against, and an `#I` directory
// decides where a bare name resolves. `#r "nuget: ..."` is text, already keyed in the script.

[<Fact>]
let ``a local reference's bytes are keyed`` () =
    withTempDir "plugin-inputs-ref-bytes" (fun root ->
        write root "build.fsx" "#r \"lib/Tool.dll\"\n"
        write root "lib/Tool.dll" "v1"
        let spec = PluginInputs.InputSpec.LoadClosure [ "build.fsx" ]
        let before = PluginInputs.digest root spec
        test <@ closureOf root = [ "present:build.fsx"; "present:lib/Tool.dll" ] @>
        write root "lib/Tool.dll" "v2"
        test <@ PluginInputs.digest root spec <> before @>)

[<Fact>]
let ``a missing local reference keys as missing, and is not parsed for directives`` () =
    withTempDir "plugin-inputs-ref-missing" (fun root ->
        write root "build.fsx" "#r @\"lib/Absent.dll\"\n#reference \"lib/Script.fsx\"\n"
        // A referenced file is an assembly: directives in it are not the script's.
        write root "lib/Script.fsx" "#load \"never.fs\"\n"
        test <@ closureOf root = [ "present:build.fsx"; "missing:lib/Absent.dll"; "present:lib/Script.fsx" ] @>)

[<Fact>]
let ``an include directory is keyed and resolves a name its declaring file does not hold`` () =
    withTempDir "plugin-inputs-include" (fun root ->
        write root "build.fsx" "#I \"vendor\"\n#load \"Helper.fs\"\n#r \"Tool.dll\"\n#load \"Nowhere.fs\"\n"
        write root "vendor/Helper.fs" "module Helper"
        write root "vendor/Tool.dll" "bytes"

        test
            <@
                closureOf root = [ "present:build.fsx"
                                   "include:vendor"
                                   "present:vendor/Helper.fs"
                                   "present:vendor/Tool.dll"
                                   "missing:Nowhere.fs" ]
            @>

        let spec = PluginInputs.InputSpec.LoadClosure [ "build.fsx" ]
        let before = PluginInputs.digest root spec
        // The declaring file's own directory comes first, as FSI resolves it.
        write root "Helper.fs" "module Helper"

        test
            <@
                closureOf root = [ "present:build.fsx"
                                   "include:vendor"
                                   "present:Helper.fs"
                                   "present:vendor/Tool.dll"
                                   "missing:Nowhere.fs" ]
            @>

        test <@ PluginInputs.digest root spec <> before @>
        // A file appearing in the include directory under a missing name moves the key too.
        let withHelper = PluginInputs.digest root spec
        write root "vendor/Nowhere.fs" "module Nowhere"
        test <@ PluginInputs.digest root spec <> withHelper @>)

[<Fact>]
let ``a package reference adds no file`` () =
    withTempDir "plugin-inputs-ref-nuget" (fun root ->
        write root "build.fsx" "#r \"nuget: Fake.Core, 6.0.0\"\n#r \"NuGet:Other\"\n"
        test <@ closureOf root = [ "present:build.fsx" ] @>)
