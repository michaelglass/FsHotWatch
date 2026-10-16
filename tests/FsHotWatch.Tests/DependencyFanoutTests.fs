module FsHotWatch.Tests.DependencyFanoutTests

open System.IO
open System.Text
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune.DependencyFanout

// The pure dependency-fanout primitives. End-to-end daemon behaviour (skip-gate
// closing) lives in TestPrunePluginTests; these pin the fingerprint/diff building
// blocks directly.

let private writeTempProject (body: string) =
    let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    let p = Path.Combine(dir, "Proj.fsproj")
    File.WriteAllText(p, body)
    dir, p

module ``directPackageVersions`` =

    [<Fact>]
    let ``extracts inline PackageReference versions, sorted`` () =
        let dir, fsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" Version="0.7.0" />
    <PackageReference Include="Aaa" Version="1.0.0" />
  </ItemGroup>
</Project>"""

        try
            test <@ directPackageVersions fsproj = [ ("Aaa", "1.0.0"); ("CommandTree", "0.7.0") ] @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a CPM reference (no inline Version) yields an empty version`` () =
        let dir, fsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" /></ItemGroup>
</Project>"""

        try
            test <@ directPackageVersions fsproj = [ ("CommandTree", "") ] @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a reference with no Include (an Update of a transitive package) is skipped`` () =
        let dir, fsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" Version="0.7.0" />
    <PackageReference Update="FSharp.Core" Version="10.0.100" />
  </ItemGroup>
</Project>"""

        try
            test <@ directPackageVersions fsproj = [ ("CommandTree", "0.7.0") ] @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``an unreadable / missing project yields an empty list`` () =
        test <@ List.isEmpty (directPackageVersions "/no/such/path/Nope.fsproj") @>

module ``computeProjectFingerprint`` =

    /// A graph where `testFsproj` references one library whose DLL is `dllPath`.
    let private graphWith (testFsproj: string) (libFsproj: string) (dllPath: string) =
        { ProjectGraphAccessor.none with
            GetProjectReferences = fun p -> if p = testFsproj then [ libFsproj ] else []
            GetCanonicalDllPath = fun p -> if p = libFsproj then Some dllPath else None }

    [<Fact>]
    let ``referenced-DLL content change flips the fingerprint`` () =
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let libFsproj = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(libFsproj, "<Project></Project>")
        let dll = Path.Combine(dir, "Lib.dll")

        try
            File.WriteAllBytes(dll, Encoding.UTF8.GetBytes "lib-v1")
            let graph = graphWith testFsproj libFsproj dll
            let before = computeProjectFingerprint graph testFsproj

            File.WriteAllBytes(dll, Encoding.UTF8.GetBytes "lib-v2-different")
            let after = computeProjectFingerprint graph testFsproj

            test <@ before <> after @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a missing referenced DLL hashes to a stable sentinel (deterministic)`` () =
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let libFsproj = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(libFsproj, "<Project></Project>")
        let missingDll = Path.Combine(dir, "does-not-exist.dll")

        try
            let graph = graphWith testFsproj libFsproj missingDll
            let a = computeProjectFingerprint graph testFsproj
            let b = computeProjectFingerprint graph testFsproj
            test <@ a = b @> // stable across reads (the "missing" sentinel)
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a referenced project whose DLL will not RESOLVE is folded in, never dropped`` () =
        // `List.choose graph.GetCanonicalDllPath` DROPPED any project the graph could not
        // give a DLL path for. A dropped project contributes nothing to the fingerprint, so
        // the fingerprint never moves when it changes and a dependency bump inside it never
        // fans out — the under-selection DependencyFanout exists to prevent.
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let known = Path.Combine(dir, "Known.fsproj")
        let knownDll = Path.Combine(dir, "Known.dll")
        let opaque = Path.Combine(dir, "Opaque.fsproj")

        for p in [ known; opaque ] do
            File.WriteAllText(p, "<Project></Project>")

        File.WriteAllBytes(knownDll, Encoding.UTF8.GetBytes "known-binary")

        try
            // Same resolvable reference in both. The ONLY difference is a second
            // referenced project whose DLL path the graph cannot resolve.
            let withoutOpaque =
                { ProjectGraphAccessor.none with
                    GetProjectReferences = fun p -> if p = testFsproj then [ known ] else []
                    GetCanonicalDllPath = fun p -> if p = known then Some knownDll else None }

            let withOpaque =
                { ProjectGraphAccessor.none with
                    GetProjectReferences = fun p -> if p = testFsproj then [ known; opaque ] else []
                    GetCanonicalDllPath = fun p -> if p = known then Some knownDll else None }

            test
                <@
                    computeProjectFingerprint withoutOpaque testFsproj
                    <> computeProjectFingerprint withOpaque testFsproj
                @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``an unresolvable DLL and a missing DLL are ONE value — the repo-wide unhashable sentinel`` () =
        // `DaemonIdentity` promises one value for I-could-not-read-it, repo-wide. Both
        // ways of not having a DLL hash to `ContentHash.UnhashableContent`, so the two
        // fingerprints agree — a second local sentinel here would break that promise.
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let lib = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(lib, "<Project></Project>")

        try
            let missingDll =
                { ProjectGraphAccessor.none with
                    GetProjectReferences = fun p -> if p = testFsproj then [ lib ] else []
                    GetCanonicalDllPath =
                        fun p ->
                            if p = lib then
                                Some(Path.Combine(dir, "never-built.dll"))
                            else
                                None }

            let unresolvableDll =
                { ProjectGraphAccessor.none with
                    GetProjectReferences = fun p -> if p = testFsproj then [ lib ] else []
                    GetCanonicalDllPath = fun _ -> None }

            test
                <@
                    computeProjectFingerprint missingDll testFsproj = computeProjectFingerprint
                        unresolvableDll
                        testFsproj
                @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a DLL that appears where none was flips the fingerprint exactly once`` () =
        // The sentinel must NOT collide with a real digest: missing -> present has to be a
        // change, or the "rebuilt" signal is lost and the fanout never fires.
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let lib = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(lib, "<Project></Project>")
        let dll = Path.Combine(dir, "Lib.dll")

        let graph =
            { ProjectGraphAccessor.none with
                GetProjectReferences = fun p -> if p = testFsproj then [ lib ] else []
                GetCanonicalDllPath = fun p -> if p = lib then Some dll else None }

        try
            let beforeBuild = computeProjectFingerprint graph testFsproj
            File.WriteAllBytes(dll, Encoding.UTF8.GetBytes "freshly-built")
            let afterBuild = computeProjectFingerprint graph testFsproj
            test <@ beforeBuild <> afterBuild @>
            test <@ afterBuild = computeProjectFingerprint graph testFsproj @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``the test project's own direct PackageReference version is folded in`` () =
        let dir, testFsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" Version="0.6.3" /></ItemGroup>
</Project>"""

        try
            let graph = ProjectGraphAccessor.none // no project refs
            let before = computeProjectFingerprint graph testFsproj

            File.WriteAllText(
                testFsproj,
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" Version="0.7.0" /></ItemGroup>
</Project>"""
            )

            let after = computeProjectFingerprint graph testFsproj
            test <@ before <> after @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``transitive references are walked (a deduped diamond doesn't loop)`` () =
        // test -> a, test -> b, a -> c, b -> c (c reached twice; must not loop).
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let a = Path.Combine(dir, "A.fsproj")
        let b = Path.Combine(dir, "B.fsproj")
        let c = Path.Combine(dir, "C.fsproj")
        let cDll = Path.Combine(dir, "C.dll")

        for p in [ a; b; c ] do
            File.WriteAllText(p, "<Project></Project>")

        File.WriteAllBytes(cDll, Encoding.UTF8.GetBytes "c-binary")

        let graph =
            { ProjectGraphAccessor.none with
                GetProjectReferences =
                    fun p ->
                        if p = testFsproj then [ a; b ]
                        elif p = a || p = b then [ c ]
                        else []
                GetCanonicalDllPath = fun p -> if p = c then Some cDll else None }

        try
            let fp = computeProjectFingerprint graph testFsproj
            test <@ fp.Length > 0 @>
        finally
            Directory.Delete(dir, true)

/// What the fingerprint folds in for a referenced project, against a realistic layout:
/// a test project referencing `Lib`, whose compiled source is `Code.fs` (registered, so
/// the symbol diff sees its edits), with a restored `obj/project.assets.json`, a built
/// `bin/` DLL, and an embedded resource the symbol diff cannot see.
module ``fingerprint inputs`` =

    type private Layout =
        { Root: string
          TestFsproj: string
          LibFsproj: string
          Source: string
          Assets: string
          Dll: string
          Resource: string }

    let private layout () =
        let root = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
        let lib = Path.Combine(root, "Lib")
        let tests = Path.Combine(root, "Tests")

        for d in
            [ Path.Combine(lib, "obj")
              Path.Combine(lib, "bin", "Debug", "net10.0")
              Path.Combine(lib, "Resources")
              tests ] do
            Directory.CreateDirectory d |> ignore

        let l =
            { Root = root
              TestFsproj = Path.Combine(tests, "Tests.fsproj")
              LibFsproj = Path.Combine(lib, "Lib.fsproj")
              Source = Path.Combine(lib, "Code.fs")
              Assets = Path.Combine(lib, "obj", "project.assets.json")
              Dll = Path.Combine(lib, "bin", "Debug", "net10.0", "Lib.dll")
              Resource = Path.Combine(lib, "Resources", "prompt.txt") }

        File.WriteAllText(l.TestFsproj, "<Project></Project>")

        File.WriteAllText(
            l.LibFsproj,
            """<Project><ItemGroup><EmbeddedResource Include="Resources/prompt.txt" /></ItemGroup></Project>"""
        )

        File.WriteAllText(l.Source, "module Lib.Code\nlet answer = 42\n")
        File.WriteAllText(l.Assets, """{ "libraries": { "CommandTree/0.6.3": { "sha512": "aaa" } } }""")
        File.WriteAllBytes(l.Dll, Encoding.UTF8.GetBytes "lib-build-1")
        File.WriteAllText(l.Resource, "You are a helpful prompt.")
        l

    /// `checkable` is what the model says the symbol diff analyses; `None` is no model.
    let private graphOf (l: Layout) (checkable: string list option) =
        { ProjectGraphAccessor.none with
            ObserveCheckableFiles =
                fun () ->
                    checkable
                    |> Option.map (fun files ->
                        1L, files |> List.map FsHotWatch.Events.AbsFilePath.create |> Set.ofList)
            GetProjectReferences = fun p -> if p = l.TestFsproj then [ l.LibFsproj ] else []
            GetCanonicalDllPath = fun p -> if p = l.LibFsproj then Some l.Dll else None }

    /// The fingerprint before and after `change`, over the same graph.
    let private across (l: Layout) (checkable: string list option) (change: unit -> unit) =
        let graph = graphOf l checkable
        let before = computeProjectFingerprint graph l.TestFsproj
        change ()
        before, computeProjectFingerprint graph l.TestFsproj

    let private withLayout (body: Layout -> unit) =
        let l = layout ()

        try
            body l
        finally
            Directory.Delete(l.Root, true)

    [<Fact>]
    let ``a comment-only edit to a compiled source, and the rebuild it causes, do not move the fingerprint`` () =
        // The symbol diff owns compiled sources. Hashing the rebuilt DLL's bytes made every
        // edit under a referenced project — a comment included — force-run its test
        // projects in full, the opposite of what fanout exists for.
        withLayout (fun l ->
            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.AppendAllText(l.Source, "// a comment\n")
                    File.WriteAllBytes(l.Dll, Encoding.UTF8.GetBytes "lib-build-2-new-pdb-id"))

            test <@ before = after @>)

    [<Fact>]
    let ``a package bump in a referenced project's restored graph moves the fingerprint`` () =
        withLayout (fun l ->
            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.WriteAllText(l.Assets, """{ "libraries": { "CommandTree/0.7.0": { "sha512": "bbb" } } }"""))

            test <@ before <> after @>)

    [<Fact>]
    let ``an embedded-resource edit, which no symbol diff sees, moves the fingerprint`` () =
        withLayout (fun l ->
            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(l.Resource, "You are a terse prompt."))

            test <@ before <> after @>)

    [<Fact>]
    let ``a referenced project's own fsproj edit moves the fingerprint`` () =
        withLayout (fun l ->
            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.WriteAllText(
                        l.LibFsproj,
                        """<Project><PropertyGroup><DefineConstants>X</DefineConstants></PropertyGroup></Project>"""
                    ))

            test <@ before <> after @>)

    [<Fact>]
    let ``a Directory.Build.props above a referenced project moves the fingerprint`` () =
        withLayout (fun l ->
            let props = Path.Combine(l.Root, "Directory.Build.props")
            File.WriteAllText(props, "<Project></Project>")

            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.WriteAllText(
                        props,
                        "<Project><PropertyGroup><Optimize>true</Optimize></PropertyGroup></Project>"
                    ))

            test <@ before <> after @>)

    [<Fact>]
    let ``a non-F# compile input (a C# file) moves the fingerprint`` () =
        withLayout (fun l ->
            let cs = Path.Combine(Path.GetDirectoryName l.LibFsproj, "Interop.cs")
            File.WriteAllText(cs, "class A {}")

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(cs, "class A { int x; }"))

            test <@ before <> after @>)

    [<Fact>]
    let ``an F# file the model does not compile is an input like any other`` () =
        // Only files the symbol diff analyses are left to it. A `.fs` that is content, a
        // fixture or simply not in the compile list is invisible to it.
        withLayout (fun l ->
            let fixture = Path.Combine(Path.GetDirectoryName l.LibFsproj, "Fixture.fs")
            File.WriteAllText(fixture, "let x = 1")

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(fixture, "let x = 2"))

            test <@ before <> after @>)

    [<Fact>]
    let ``with no project model every source is an input (over-selection, never under)`` () =
        withLayout (fun l ->
            let before, after =
                across l None (fun () -> File.AppendAllText(l.Source, "// a comment\n"))

            test <@ before <> after @>)

    [<Fact>]
    let ``a project with no restored graph falls back to its built DLL's bytes`` () =
        // Without `obj/project.assets.json` (an unrestored or non-SDK project) nothing names
        // its packages, so the compiled binary is the only witness to a dependency change.
        withLayout (fun l ->
            File.Delete l.Assets

            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.WriteAllBytes(l.Dll, Encoding.UTF8.GetBytes "lib-against-0.7.0"))

            test <@ before <> after @>)

    [<Fact>]
    let ``build output under bin and obj other than the restored graph is not an input`` () =
        withLayout (fun l ->
            let before, after =
                across l (Some [ l.Source ]) (fun () ->
                    File.WriteAllText(
                        Path.Combine(Path.GetDirectoryName l.Assets, "Lib.AssemblyInfo.fs"),
                        "// generated"
                    )

                    File.WriteAllBytes(l.Dll, Encoding.UTF8.GetBytes "lib-build-2"))

            test <@ before = after @>)

    [<Fact>]
    let ``a nested project's directory belongs to that project, not to the one above it`` () =
        // A project file at the top of a tree must not fold in every project beneath it:
        // a referenced nested project is in the closure on its own, and an unreferenced one
        // is not an input at all.
        withLayout (fun l ->
            let nested = Path.Combine(Path.GetDirectoryName l.LibFsproj, "Tools", "Gen")
            Directory.CreateDirectory nested |> ignore
            File.WriteAllText(Path.Combine(nested, "Gen.fsproj"), "<Project></Project>")
            let data = Path.Combine(nested, "data.json")
            File.WriteAllText(data, "{}")

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(data, """{ "x": 1 }"""))

            test <@ before = after @>)

    [<Fact>]
    let ``hidden files are not inputs`` () =
        // `.DS_Store` and editor droppings come and go on their own; counting them would
        // fan out on nothing.
        withLayout (fun l ->
            let hidden = Path.Combine(Path.GetDirectoryName l.LibFsproj, ".DS_Store")
            File.WriteAllText(hidden, "a")

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(hidden, "b"))

            test <@ before = after @>)

    [<Fact>]
    let ``hidden directories are not walked`` () =
        // `.git`, `.vs`, `.idea`: tool state, not build input.
        withLayout (fun l ->
            let hiddenDir = Path.Combine(Path.GetDirectoryName l.LibFsproj, ".idea")
            Directory.CreateDirectory hiddenDir |> ignore
            let state = Path.Combine(hiddenDir, "workspace.xml")
            File.WriteAllText(state, "a")

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(state, "b"))

            test <@ before = after @>)

    [<Fact>]
    let ``a symlinked directory is not walked`` () =
        // `.devenv`-style links point into stores of hundreds of thousands of files.
        withLayout (fun l ->
            let target = Path.Combine(l.Root, "elsewhere")
            Directory.CreateDirectory target |> ignore
            let data = Path.Combine(target, "data.txt")
            File.WriteAllText(data, "1")

            Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName l.LibFsproj, "linked"), target)
            |> ignore

            let before, after =
                across l (Some [ l.Source ]) (fun () -> File.WriteAllText(data, "2"))

            test <@ before = after @>)

    [<Fact>]
    let ``a directory that cannot be listed counts as unhashable, never as absent`` () =
        // Dropping it would make an unreadable input indistinguishable from no input, so a
        // change behind it could never be seen to move anything.
        withLayout (fun l ->
            let locked = Path.Combine(Path.GetDirectoryName l.LibFsproj, "Locked")
            Directory.CreateDirectory locked |> ignore
            let graph = graphOf l (Some [ l.Source ])

            try
                File.SetUnixFileMode(locked, UnixFileMode.None)
                let unreadable = computeProjectFingerprint graph l.TestFsproj

                // Only the locked directory is unhashable: an edit beside it still moves
                // the fingerprint. Catching at the parent turned the whole project into
                // one sentinel that no later edit could move.
                File.WriteAllText(l.Resource, "You are a terse prompt.")
                test <@ computeProjectFingerprint graph l.TestFsproj <> unreadable @>
                File.WriteAllText(l.Resource, "You are a helpful prompt.")

                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                Directory.Delete locked
                let absent = computeProjectFingerprint graph l.TestFsproj
                test <@ unreadable <> absent @>
            finally
                if Directory.Exists locked then
                    File.SetUnixFileMode(
                        locked,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    ))

    [<Fact>]
    let ``a referenced project whose directory is gone counts as unhashable, and stably so`` () =
        // A project removed while the model still names it. Its directory cannot be
        // listed, which must read as one stable "could not hash" value, not as an empty,
        // input-free project that a re-creation could never be seen to change.
        withLayout (fun l ->
            let graph = graphOf l (Some [ l.Source ])
            let present = computeProjectFingerprint graph l.TestFsproj
            Directory.Delete(Path.GetDirectoryName l.LibFsproj, true)
            let gone = computeProjectFingerprint graph l.TestFsproj

            test <@ gone <> present @>
            test <@ gone = computeProjectFingerprint graph l.TestFsproj @>)

    [<Fact>]
    let ``a non-IO failure while reading a project's inputs propagates instead of reading as unreadable`` () =
        // Only an IO failure may become the "could not hash" sentinel. Swallowing any other
        // exception would turn a bug in the walk into the same sentinel for every project:
        // a fingerprint that never moves, and fanout silently off.
        withLayout (fun l ->
            let failing (_: string) : string seq =
                raise (System.InvalidOperationException "a bug, not an unreadable directory")

            raises<System.InvalidOperationException>
                <@ projectDirectoryInputs failing (Path.GetDirectoryName l.LibFsproj) (fun _ -> false) @>)

    [<Fact>]
    let ``one fingerprinter shares a referenced project across test projects without changing either answer`` () =
        withLayout (fun l ->
            let other = Path.Combine(l.Root, "Other", "Other.fsproj")
            Directory.CreateDirectory(Path.GetDirectoryName other) |> ignore
            File.WriteAllText(other, "<Project></Project>")

            let graph =
                { graphOf l (Some [ l.Source ]) with
                    GetProjectReferences =
                        fun p ->
                            if p = l.TestFsproj || p = other then
                                [ l.LibFsproj ]
                            else
                                [] }

            let shared = fingerprinter graph
            let first = shared l.TestFsproj
            let second = shared other

            test <@ first = computeProjectFingerprint graph l.TestFsproj @>
            test <@ second = computeProjectFingerprint graph other @>)

module ``ProjectGraphAccessor none`` =

    [<Fact>]
    let ``the no-op accessor returns empty/None for every query`` () =
        let g = ProjectGraphAccessor.none
        test <@ List.isEmpty (g.GetAllProjects()) @>
        test <@ List.isEmpty (g.GetTransitiveDependentProjects "X.fsproj") @>
        test <@ List.isEmpty (g.GetProjectReferences "X.fsproj") @>
        test <@ g.GetCanonicalDllPath "X.fsproj" = None @>

module ``changedProjects`` =

    [<Fact>]
    let ``reports only projects whose fingerprint moved against a known prior`` () =
        let prior = Map.ofList [ "A", "fp1"; "B", "fp1" ]
        let current = Map.ofList [ "A", "fp2"; "B", "fp1" ] // A moved, B same
        test <@ changedProjects prior current = Set.ofList [ "A" ] @>

    [<Fact>]
    let ``a project with no prior fingerprint is not reported`` () =
        let current = Map.ofList [ "A", "fp1" ]
        test <@ Set.isEmpty (changedProjects Map.empty current) @>

module ``advance`` =

    [<Fact>]
    let ``a build's fingerprints replace the prior ones they observed`` () =
        let prior = Map.ofList [ "A", "fp1"; "B", "fp1" ]
        let current = Map.ofList [ "A", "fp2"; "B", "fp1" ]
        test <@ advance prior current = current @>

    [<Fact>]
    let ``a project a build could not fingerprint keeps its prior`` () =
        let prior = Map.ofList [ "A", "fp1"; "B", "fp1" ]
        let partial = Map.ofList [ "A", "fp2" ]
        test <@ advance prior partial = Map.ofList [ "A", "fp2"; "B", "fp1" ] @>
        test <@ advance prior Map.empty = prior @>
        // The next build that sees B again compares it against the kept prior.
        test <@ changedProjects (advance prior Map.empty) (Map.ofList [ "B", "fp2" ]) = Set.ofList [ "B" ] @>
